using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Movement
{
    /// <summary>已提交的移动步（只读审计记录；终态清理后仍然保留）。</summary>
    public sealed record CommittedMovementStep(
        ActionPlanId ActionPlanId,
        int StepIndex,
        UnitId UnitId,
        GridPoint From,
        GridPoint To,
        GridDirection Direction,
        int StepWeightUnits,
        long StartTick,
        long EndTick,
        long CommittedAtTick);

    /// <summary>一次空间批次替换的只读结果（诊断用，不参与逻辑与哈希）。</summary>
    public sealed record MovementReplacementResult(
        bool Succeeded,
        string FailureCode,
        IReadOnlyList<MovementSegment> Segments,
        IReadOnlyList<Reservation> Reservations,
        int ReleasedReservationCount,
        int ReleasedSegmentCount)
    {
        public static MovementReplacementResult Rejected(string code, int releasedReservations = 0, int releasedSegments = 0)
            => new MovementReplacementResult(
                false, code, Array.Empty<MovementSegment>(), Array.Empty<Reservation>(),
                releasedReservations, releasedSegments);
    }

    /// <summary>
    /// <strong>排程事务 → 空间事务</strong>的提交端口（任务 06「必须产出」6 第二段）。
    ///
    /// 显式编辑与系统自动延期<strong>共用</strong>同一个实现：它们都先由
    /// <c>ScheduleEvaluator</c> 产出受影响计划的规范顺序与新的绝对 Tick，
    /// 然后把这一批计划交给本端口，由端口在<strong>一个原子批次</strong>内整体替换这些
    /// Editable 计划的未来 <c>MovementSegment</c> 与 Reservation。
    ///
    /// 返回非 null 的稳定错误码 ⇒ 调用方必须把整批计划恢复到提交前的投影
    /// （零局部空间写入；旧段/预留继续权威）。
    /// </summary>
    public interface IEditableMovementSpacePort
    {
        string RebuildMovementSpace(
            IReadOnlyList<ActionPlan> candidatePlans, long scheduleRevision, long tick);
    }

    public interface IEditableMovementSpacePreviewPort
    {
        string ValidateMovementSpacePreview(IReadOnlyList<ActionPlan> candidatePlans, long revision, long tick);
    }

    /// <summary>一次"空间整批替换"的只读结果（诊断用，不参与逻辑与哈希）。</summary>
    public sealed record MovementSpaceBatchResult(
        bool Succeeded,
        string FailureCode,
        IReadOnlyList<ActionPlanId> ReplacedPlanIds,
        IReadOnlyList<MovementSegment> Segments,
        IReadOnlyList<Reservation> Reservations)
    {
        public static MovementSpaceBatchResult Rejected(string code)
            => new MovementSpaceBatchResult(false, code, Array.Empty<ActionPlanId>(),
                Array.Empty<MovementSegment>(), Array.Empty<Reservation>());
    }

    /// <summary>
    /// 单个计划在一次空间整批替换<strong>之前</strong>的形状（段 + 预留 + 待进入格）。
    /// 只用于失败回滚：它不携带计划的排程投影（那由 <c>ScheduleEditor</c> 自己恢复）。
    ///
    /// 段与预留必须<strong>成对</strong>快照/恢复：只恢复预留会让"每段都有同键预留、
    /// 段数 == 预留数"这条自洽性被破坏（那正是"失败零局部写入"要禁止的形状）。
    /// </summary>
    internal readonly struct MovementSpaceSnapshot
    {
        private readonly ActionPlanId _actionPlanId;
        private readonly UnitId _unitId;
        private readonly IReadOnlyList<Reservation> _reservations;
        private readonly IReadOnlyList<MovementSegment> _segments;
        private readonly bool _hadPendingDestination;
        private readonly GridPoint _pendingDestination;

        private MovementSpaceSnapshot(
            ActionPlanId actionPlanId, UnitId unitId, IReadOnlyList<Reservation> reservations,
            IReadOnlyList<MovementSegment> segments,
            bool hadPendingDestination, GridPoint pendingDestination)
        {
            _actionPlanId = actionPlanId;
            _unitId = unitId;
            _reservations = reservations;
            _segments = segments;
            _hadPendingDestination = hadPendingDestination;
            _pendingDestination = pendingDestination;
        }

        public IReadOnlyList<Reservation> Reservations => _reservations;

        public static MovementSpaceSnapshot Capture(
            ActionPlan plan, LogicGrid grid, IReadOnlyList<MovementSegment> segments)
        {
            IReadOnlyList<Reservation> reservations = grid.ReservationsOfPlanOrdered(plan.ActionPlanId);
            bool hasPending = grid.TryGetPendingDestination(plan.OwnerUnitId, out GridPoint pending);
            return new MovementSpaceSnapshot(
                plan.ActionPlanId, plan.OwnerUnitId, reservations,
                segments ?? Array.Empty<MovementSegment>(), hasPending, pending);
        }

        /// <summary>
        /// 把快照原样写回权威表（段 + 预留 + 待进入格）。
        ///
        /// 预留按 <c>Reservation</c> 自身的稳定空间键顺序逐条重取：同格的多条同单位
        /// 互不重叠预留会由 <see cref="LogicGrid.TryReserve"/> 的共存判据原样重建，
        /// 因此"批前形状"（含回程链已经形成的同格共存）可以被逐字恢复。
        /// </summary>
        public void Restore(LogicGrid grid, IDictionary<ReservationKey, MovementSegment> segments)
        {
            for (int i = 0; i < _reservations.Count; i++) grid.TryReserve(_reservations[i]);
            for (int i = 0; i < _segments.Count; i++)
                segments[new ReservationKey(_segments[i].ActionPlanId, _segments[i].StepIndex)] = _segments[i];
            grid.SetPendingDestination(_unitId, _hadPendingDestination ? _pendingDestination : (GridPoint?)null);
        }
    }

    /// <summary>移动段的权威表 + 空间 Reservation 事务（任务包「必须产出」3/4/5/6/8）。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>多步路径每步一个 <see cref="MovementSegment"/>，用 <c>(ActionPlanId, StepIndex)</c> 定位；</item>
    /// <item>一个计划的全部未来段与 Reservation 可以在<strong>一个原子批次</strong>里整体替换：
    /// 任一失败 ⇒ 恢复替换前的段与预留（<strong>零局部写入</strong>）；</item>
    /// <item><c>StartTick &lt;= tick &lt; EndTick</c> 期间单位仍占 <c>From</c>、<c>To</c> 保持 Reservation；
    /// 到 <c>EndTick</c> 的<strong>命令前边界</strong>原子提交到 <c>To</c> 并释放该 Reservation；</item>
    /// <item>任意终态原因都按 <see cref="ActionPlanCleanupOrder.MovementAndReservation"/> 的固定顺序
    /// 清理该计划的<strong>全部</strong>活动与未来段 + <strong>全部</strong> Reservation，
    /// 并保留已完成段与最终逻辑位置的只读审计；</item>
    /// <item>预留冲突<strong>只</strong>服从任务 03 网关的 <c>CommandSequence</c>：
    /// 先成功提交者持有，后者稳定拒绝、不可抢占。</item>
    /// </list>
    /// </summary>
    public sealed class LogicGridMovementAuthority : IActionPlanCleanupParticipant, IEditableMovementSpacePort, IEditableMovementSpacePreviewPort
    {
        private readonly LogicGrid _grid;
        private readonly ActionScheduleAuthority _authority;
        private readonly LogicPathfinder _pathfinder;

        private readonly Dictionary<ReservationKey, MovementSegment> _segments =
            new Dictionary<ReservationKey, MovementSegment>();

        private readonly Dictionary<long, List<CommittedMovementStep>> _audit =
            new Dictionary<long, List<CommittedMovementStep>>();

        public LogicGridMovementAuthority(
            LogicGrid grid,
            ActionScheduleAuthority authority,
            PathCostRules costRules,
            PathSearchRules searchRules)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            CostRules = costRules ?? throw new ArgumentNullException(nameof(costRules));
            SearchRules = searchRules ?? throw new ArgumentNullException(nameof(searchRules));
            _pathfinder = new LogicPathfinder(grid, CostRules, SearchRules);
        }

        public LogicGrid Grid => _grid;

        public PathCostRules CostRules { get; }

        public PathSearchRules SearchRules { get; }

        public LogicPathfinder Pathfinder => _pathfinder;

        /// <summary>终态清理的固定顺序槽位（任务 05 冻结的入口，本任务只插入到该位置）。</summary>
        public int Order => ActionPlanCleanupOrder.MovementAndReservation;

        /// <summary>
        /// Dodge 目的格预留的释放端口（任务 06 内部接线；装配方在构造
        /// <c>DodgeRelocationAuthority</c> 之后回填）。
        ///
        /// 它<strong>不</strong>新增第二个终态清理参与者：Dodge 目的格预留与
        /// <c>MovementSegment</c>/空间 Reservation 属于<strong>同一个</strong>清理槽位
        /// （<see cref="ActionPlanCleanupOrder.MovementAndReservation"/>），因此
        /// "任意终态原因都经同一入口"这一冻结事实不被破坏。
        /// </summary>
        public IDodgeDestinationReservationSink DodgeDestinationReservations { get; set; }

        public string ParticipantId => "actionplan.movement-and-reservation";

        // ————————————————————————————————————————————————————————————
        // 只读查询
        // ————————————————————————————————————————————————————————————

        /// <summary>某计划的全部活动/未来段（按 <c>StepIndex</c> 升序）。</summary>
        public IReadOnlyList<MovementSegment> SegmentsOfPlanOrdered(ActionPlanId actionPlanId)
        {
            var result = new List<MovementSegment>();
            foreach (KeyValuePair<ReservationKey, MovementSegment> pair in _segments)
            {
                if (pair.Key.ActionPlanId.Value == actionPlanId.Value) result.Add(pair.Value);
            }
            result.Sort(MovementSegment.Compare);
            return result;
        }

        /// <summary>全部活动/未来段的规范快照（按 <c>(ActionPlanId, StepIndex)</c> 升序）。</summary>
        public IReadOnlyList<MovementSegment> AllSegmentsOrdered()
        {
            var result = new List<MovementSegment>(_segments.Count);
            foreach (KeyValuePair<ReservationKey, MovementSegment> pair in _segments) result.Add(pair.Value);
            result.Sort(MovementSegment.Compare);
            return result;
        }

        /// <summary>某单位当前"仍在飞行中"的段（<c>StartTick &lt;= tick &lt; EndTick</c>）；无则 null。</summary>
        public MovementSegment InFlightSegmentOf(UnitId unitId, long tick)
        {
            MovementSegment best = null;
            foreach (KeyValuePair<ReservationKey, MovementSegment> pair in _segments)
            {
                MovementSegment segment = pair.Value;
                if (segment.UnitId.Value != unitId.Value) continue;
                if (!segment.IsInFlightAt(tick)) continue;
                if (best == null || MovementSegment.Compare(segment, best) < 0) best = segment;
            }
            return best;
        }

        /// <summary>
        /// 该单位在哪一个 Tick 释放它<strong>当前占据</strong>的格（门禁空间查询的权威边界）。
        /// 没有在飞行的段 ⇒ null（静止占位没有有限释放边界，因此<strong>不</strong>可 Retryable）。
        /// </summary>
        public long? ActiveReleaseTickOf(UnitId unitId, long tick)
        {
            MovementSegment segment = InFlightSegmentOf(unitId, tick);
            return segment?.EndTick;
        }

        /// <summary>某计划已提交步的只读审计（按 <c>StepIndex</c> 升序）。</summary>
        public IReadOnlyList<CommittedMovementStep> AuditOfPlan(ActionPlanId actionPlanId)
            => _audit.TryGetValue(actionPlanId.Value, out List<CommittedMovementStep> list)
                ? (IReadOnlyList<CommittedMovementStep>)new List<CommittedMovementStep>(list)
                : Array.Empty<CommittedMovementStep>();

        /// <summary>单位的最终逻辑位置（就是 <see cref="LogicGrid"/> 的权威锚点）。</summary>
        public bool TryGetFinalLogicalPosition(UnitId unitId, out GridPoint position)
            => _grid.TryGetAnchor(unitId, out position);

        // ————————————————————————————————————————————————————————————
        // 批次整体替换
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 用给定路径为一个 Move 计划<strong>整批替换</strong>它的未来段与 Reservation。
        ///
        /// 预检顺序固定：路径首点必须等于该单位当前权威锚点；相邻点必须是规范单步；
        /// <see cref="MovementSegment.Create"/> 的 checked 时长不得溢出。
        /// 任一步失败 ⇒ 返回失败码且<strong>零写入</strong>（原有段与预留保持不变）。
        /// 成功时：先释放该计划的旧段/预留，再整批写入新段/预留；预留冲突时整体回滚。
        /// </summary>
        public MovementReplacementResult EstablishMovement(
            ActionPlan plan, IReadOnlyList<GridPoint> path, long startTick)
        {
            if (plan == null) return MovementReplacementResult.Rejected(MovementCodes.MOVEMENT_SEGMENT_ORIGIN_MISMATCH);
            return EstablishMovement(
                plan.ActionPlanId, plan.OwnerUnitId, path, startTick, plan.ResolvedBaseStepTicks,
                plan.Destination);
        }

        /// <summary>
        /// 与 <see cref="EstablishMovement(ActionPlan, IReadOnlyList{GridPoint}, long)"/> 同义的**纯数据**入口：
        /// 显式给出 <c>(ActionPlanId, UnitId, 基准每权重 Tick, 目的格)</c>，因此本事务可以在
        /// 没有任何 <c>ActionPlan</c> 实例的情况下被独立验证（也是只读候选预览的入口形态）。
        ///
        /// <paramref name="expectedOrigin"/> 缺席时的额外语义由 <see cref="LogicGrid.TryReserve"/> 承担：
        /// 同一条链内同一单位"稍后重新经过先前的格"允许同格共存（时间窗互不重叠），
        /// 其他一切冲突——包括同一时间窗、其他单位、静止占位——仍然整批拒绝。
        /// </summary>
        public MovementReplacementResult EstablishMovement(
            ActionPlanId actionPlanId,
            UnitId unitId,
            IReadOnlyList<GridPoint> path,
            long startTick,
            int resolvedBaseStepTicks,
            GridPoint? destination = null,
            GridPoint? expectedOrigin = null)
        {
            if (path == null || path.Count < 2)
                return MovementReplacementResult.Rejected(MovementCodes.MOVEMENT_SEGMENT_CHAIN_DISCONTINUOUS);
            if (resolvedBaseStepTicks <= 0)
                return MovementReplacementResult.Rejected(MovementCodes.MOVEMENT_SEGMENT_INTERVAL_INVALID);
            if (!_grid.TryGetAnchor(unitId, out GridPoint anchor))
                return MovementReplacementResult.Rejected(LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN);
            // 排队的 Move 链（任务 07 裁定 B：首版**支持**链）中，第二条及以后的计划其合法起点
            // 是"前一条链内计划的终点"，而不是单位当前锚点。调用方必须把**算路径时用的同一个起点**
            // 显式传进来；不传时保持既有语义（锚点校验）。
            GridPoint validatedOrigin = expectedOrigin ?? anchor;
            if (validatedOrigin.X != path[0].X || validatedOrigin.Y != path[0].Y)
                return MovementReplacementResult.Rejected(MovementCodes.MOVEMENT_SEGMENT_ORIGIN_MISMATCH);

            // —— 1. 构造候选段（溢出/非规范方向在这里整体拒绝，绝不 clamp）——
            var candidates = new List<MovementSegment>(path.Count - 1);
            long cursor = startTick;
            try
            {
                for (int i = 0; i < path.Count - 1; i++)
                {
                    MovementSegment segment = MovementSegment.Create(
                        actionPlanId, i, unitId, path[i], path[i + 1],
                        cursor, CostRules, resolvedBaseStepTicks);
                    candidates.Add(segment);
                    cursor = segment.EndTick;
                }
            }
            catch (LogicDefinitionException ex)
            {
                return MovementReplacementResult.Rejected(ex.ErrorCode);
            }

            // —— 2. 预留候选（同一事务；冲突即整体拒绝）——
            var candidateReservations = new List<Reservation>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                MovementSegment segment = candidates[i];
                candidateReservations.Add(new Reservation(
                    new ReservationKey(segment.ActionPlanId, segment.StepIndex),
                    segment.UnitId, segment.To, segment.StartTick, segment.EndTick));
            }

            // —— 3. 快照旧状态（用于回滚）——
            IReadOnlyList<Reservation> oldReservations = _grid.ReservationsOfPlanOrdered(actionPlanId);
            IReadOnlyList<MovementSegment> oldSegments = SegmentsOfPlanOrdered(actionPlanId);

            // —— 4. 释放旧状态 ——
            _grid.ReleaseReservationsOfPlan(actionPlanId);
            for (int i = 0; i < oldSegments.Count; i++) _segments.Remove(KeyOf(oldSegments[i]));

            // —— 5. 整批写入；任一冲突 ⇒ 回滚到旧状态 ——
            for (int i = 0; i < candidateReservations.Count; i++)
            {
                string error = _grid.TryReserve(candidateReservations[i]);
                if (error == null) continue;
                _grid.ReleaseReservationsOfPlan(actionPlanId);
                Restore(oldSegments, oldReservations);
                return MovementReplacementResult.Rejected(
                    error, oldReservations.Count, oldSegments.Count);
            }

            for (int i = 0; i < candidates.Count; i++) _segments[KeyOf(candidates[i])] = candidates[i];
            _grid.SetPendingDestination(unitId, destination ?? path[path.Count - 1]);
            return new MovementReplacementResult(
                true, null, candidates, candidateReservations, oldReservations.Count, oldSegments.Count);
        }

        private void Restore(IReadOnlyList<MovementSegment> segments, IReadOnlyList<Reservation> reservations)
        {
            for (int i = 0; i < reservations.Count; i++) _grid.TryReserve(reservations[i]);
            for (int i = 0; i < segments.Count; i++) _segments[KeyOf(segments[i])] = segments[i];
        }

        /// <summary>移除某计划的全部活动/未来段与 Reservation（不触碰已提交审计）。</summary>
        public IReadOnlyList<Reservation> ReleasePlanMovement(ActionPlanId actionPlanId)
        {
            IReadOnlyList<MovementSegment> segments = SegmentsOfPlanOrdered(actionPlanId);
            for (int i = 0; i < segments.Count; i++) _segments.Remove(KeyOf(segments[i]));
            return _grid.ReleaseReservationsOfPlan(actionPlanId);
        }

        // ————————————————————————————————————————————————————————————
        // 排程事务 → 空间整批替换（显式编辑与系统自动延期共用）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 把一批受影响计划解析成"可按权威锚点建立段链"的规范工作集。
        ///
        /// 规则（只读，不写任何状态）：
        /// <list type="number">
        /// <item>剔除不可解析项（计划不存在、非 <c>Editable</c> 普通 Move、无目的格）；</item>
        /// <item>单位内按 <c>(EndTick, ActionPlanId)</c> 升序——即"计划链"的规范顺序；</item>
        /// <item>计划链首项：预测起点必须等于该单位的<strong>当前权威锚点</strong>，
        /// 否则它无法在本批次内建立段链（前置计划尚未提交）；</item>
        /// <item>链上后续项：预测起点必须等于<strong>前一个已被纳入</strong>的计划的
        /// <c>To</c>，因此整条链都是"从最后一个不可变位置重算"出来的；
        /// 一旦断链，断点及其之后全部剔除（它们必须等前置计划提交后才能重建）。</item>
        /// </list>
        /// </summary>
        public IReadOnlyList<ActionPlan> ResolveEditableMovementWorkingSet(
            IReadOnlyList<ActionPlan> candidatePlans)
        {
            var result = new List<ActionPlan>();
            if (candidatePlans == null || candidatePlans.Count == 0) return result;

            var candidates = new List<ActionPlan>(candidatePlans.Count);
            var seen = new HashSet<long>();
            for (int i = 0; i < candidatePlans.Count; i++)
            {
                ActionPlan plan = candidatePlans[i];
                if (plan == null) continue;
                if (!plan.IsOrdinaryMove) continue;
                if (!plan.IsEditable) continue;
                if (!plan.Destination.HasValue) continue;
                if (!seen.Add(plan.ActionPlanId.Value)) continue;
                candidates.Add(plan);
            }
            if (candidates.Count == 0) return result;
            candidates.Sort(CompareByChainOrder);

            var cursor = new Dictionary<long, GridPoint>();
            for (int i = 0; i < candidates.Count; i++)
            {
                ActionPlan plan = candidates[i];
                long unitKey = plan.OwnerUnitId.Value;
                GridPoint expectedOrigin;
                if (!cursor.TryGetValue(unitKey, out expectedOrigin))
                {
                    if (!_grid.TryGetAnchor(plan.OwnerUnitId, out GridPoint anchor)) continue;
                    expectedOrigin = anchor;
                }

                PathSearchResult path = TryFindPath(expectedOrigin, plan);
                if (path == null) continue;
                if (!path.Succeeded) continue;
                if (path.Path.Count < 2) continue;

                result.Add(plan);
                cursor[unitKey] = path.Path[path.Path.Count - 1];
            }
            return result;
        }

        /// <summary>
        /// <see cref="IEditableMovementSpacePort"/> 的生产实现：
        /// <strong>整批原子替换</strong>工作集内每个 Editable Move 计划的未来段与 Reservation。
        ///
        /// 阶段固定（任务 07 裁定 B）：
        /// <list type="number">
        /// <item><strong>只读解析</strong>工作集（<see cref="ResolveEditableMovementWorkingSet"/>，
        /// 链内预测起点与链序在这一步定死，不改写任何状态）；</item>
        /// <item><strong>整批快照</strong>工作集全部计划的旧段 + 旧预留 + 待进入格
        /// （在任何写入之前，因此快照就是事务前形状）；</item>
        /// <item><strong>整批释放</strong>工作集的旧段与旧预留——本批次的新预留因此绝不会被
        /// 同批次其他计划的<strong>旧</strong>预留阻塞（排队 Move 链的第一半修复）；</item>
        /// <item><strong>按链序建立</strong>新段与新预留（<see cref="CompareByChainOrder"/> +
        /// <see cref="ProjectedChainOriginOf"/>）；同一单位稍后重新经过自己先前的格由
        /// <see cref="LogicGrid.TryReserve"/> 的时间窗不相交共存判据放行（第二半修复）；</item>
        /// <item>任一步失败 ⇒ <strong>回滚</strong>：先释放本批次已经写入的新段/新预留，
        /// 再把<strong>每一个</strong>快照（含未及建立者）恢复成事务前形状，返回稳定失败码。</item>
        /// </list>
        /// 因此"失败零局部写入"在空间侧成立：调用方据此放弃整批排程提交即可。
        /// </summary>
        public string RebuildMovementSpace(
            IReadOnlyList<ActionPlan> candidatePlans, long scheduleRevision, long tick)
        {
            IReadOnlyList<ActionPlan> working = ResolveEditableMovementWorkingSet(candidatePlans);
            if (working.Count == 0) return null;

            // —— 1. 整批快照（必须早于任何写入）——
            var snapshots = new List<MovementSpaceSnapshot>(working.Count);
            for (int i = 0; i < working.Count; i++)
            {
                ActionPlan plan = working[i];
                snapshots.Add(MovementSpaceSnapshot.Capture(
                    plan, _grid, SegmentsOfPlanOrdered(plan.ActionPlanId)));
            }

            // —— 2. 整批释放旧状态：本批次的新预留不再与同批次计划的旧预留互相阻塞 ——
            for (int i = 0; i < working.Count; i++) ReleasePlanMovement(working[i].ActionPlanId);

            // —— 3. 按链序建立新段与新预留 ——
            var established = new List<ActionPlan>(working.Count);
            for (int i = 0; i < working.Count; i++)
            {
                ActionPlan plan = working[i];
                GridPoint chainOrigin = ProjectedChainOriginOf(working, i);
                PathSearchResult path = TryFindPath(chainOrigin, plan);
                if (path == null || !path.Succeeded)
                {
                    string pathError = path?.FailureCode ?? PathSearchCodes.PATH_INVALID_START;
                    RollbackSpace(snapshots, established);
                    return pathError;
                }

                MovementReplacementResult replacement = EstablishMovement(
                    plan.ActionPlanId, plan.OwnerUnitId, path.Path, plan.StartTick,
                    plan.ResolvedBaseStepTicks, plan.Destination, chainOrigin);
                if (!replacement.Succeeded)
                {
                    RollbackSpace(snapshots, established);
                    return replacement.FailureCode;
                }

                established.Add(plan);
                plan.OnSpaceBatchCommitted(plan.StartTick, scheduleRevision, tick);
            }
            return null;
        }

        /// <summary>批次内计划的预测起点：链首用权威锚点，其余用前一个已纳入计划的目的格。</summary>
        private GridPoint ProjectedChainOriginOf(IReadOnlyList<ActionPlan> working, int index)
        {
            var owner = working[index].OwnerUnitId;
            for (int previous = index - 1; previous >= 0; previous--)
                if (working[previous].OwnerUnitId == owner) return working[previous].Destination ?? default;
            return _grid.TryGetAnchor(owner, out GridPoint anchor) ? anchor : default;
        }

        public string ValidateMovementSpacePreview(IReadOnlyList<ActionPlan> candidatePlans, long revision, long tick)
        {
            var detached = new LogicGridMovementAuthority(_grid.CopyForPreview(), _authority, CostRules, SearchRules);
            foreach (var pair in _segments) detached._segments.Add(pair.Key, pair.Value);
            var copies = new List<ActionPlan>(candidatePlans.Count);
            foreach (var candidate in candidatePlans) copies.Add(candidate.CopyForPreview());
            return detached.RebuildMovementSpace(copies, revision, tick);
        }

        /// <summary>
        /// 失败回滚（<strong>唯一</strong>的空间回滚路径，与 <see cref="MovementSpaceSnapshot"/> 配对）：
        /// 先释放本批次已经写入的新段/新预留，再把快照逐个恢复成事务前形状。
        ///
        /// 未及建立的计划也在快照里（它们的旧状态已在整批释放阶段被释放），因此"失败零局部写入"
        /// 不依赖失败发生在链的哪一环。恢复顺序与建立顺序无关：恢复走的是
        /// <see cref="MovementSpaceSnapshot.Restore"/> 里按稳定空间键逐条重取的路径。
        /// </summary>
        private void RollbackSpace(
            IReadOnlyList<MovementSpaceSnapshot> snapshots, IReadOnlyList<ActionPlan> established)
        {
            for (int i = 0; i < established.Count; i++)
            {
                ReleasePlanMovement(established[i].ActionPlanId);
            }
            for (int i = 0; i < snapshots.Count; i++) snapshots[i].Restore(_grid, _segments);
        }

        /// <summary>
        /// 计划的候选路径：<strong>只有</strong>配置级失败（未知单位、目的格非法、越界）与
        /// 寻路级失败返回 null / 失败结果；其它 <see cref="LogicDefinitionException"/>
        /// 属于真实缺陷，必须继续上抛而不是被"当成没路"吞掉。
        /// </summary>
        private PathSearchResult TryFindPath(GridPoint origin, ActionPlan plan)
        {
            try
            {
                return _pathfinder.FindPath(
                    origin, plan.Destination.Value, plan.OwnerUnitId, plan.ActionPlanId);
            }
            catch (LogicDefinitionException ex) when (
                ex.ErrorCode == LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN ||
                ex.ErrorCode == PathSearchCodes.PATH_INVALID_START ||
                ex.ErrorCode == PathSearchCodes.PATH_INVALID_DESTINATION)
            {
                return null;
            }
        }

        private static int CompareByChainOrder(ActionPlan a, ActionPlan b)
        {
            int byEnd = a.EndTick.CompareTo(b.EndTick);
            if (byEnd != 0) return byEnd;
            return a.ActionPlanId.Value.CompareTo(b.ActionPlanId.Value);
        }

        // ————————————————————————————————————————————————————————————
        // 命令前边界
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <strong>命令前边界</strong>（Step 阶段 0）：把所有 <c>EndTick &lt;= tick</c> 的段按
        /// <c>(EndTick, ActionPlanId, StepIndex)</c> 升序<strong>原子</strong>提交到 <c>To</c>
        /// 并释放对应 Reservation。
        ///
        /// 提交顺序与视觉插值进度完全无关：只由 Step 阶段顺序与段区间决定。
        /// 提交后该段进入只读审计（终态清理<strong>不</strong>删除它）。
        /// </summary>
        public IReadOnlyList<MovementSegment> ApplyPreCommandBoundary(long tick)
        {
            var due = new List<MovementSegment>();
            foreach (KeyValuePair<ReservationKey, MovementSegment> pair in _segments)
            {
                if (pair.Value.EndTick <= tick) due.Add(pair.Value);
            }
            due.Sort(CompareByCommitOrder);

            var committed = new List<MovementSegment>(due.Count);
            for (int i = 0; i < due.Count; i++)
            {
                MovementSegment segment = due[i];
                string error = _grid.CommitAnchor(segment.UnitId, segment.To, FacingOf(segment.UnitId));
                if (error != null)
                    throw new LogicDefinitionException(error, segment.ToString());

                _grid.ReleaseReservation(new ReservationKey(segment.ActionPlanId, segment.StepIndex));
                _segments.Remove(KeyOf(segment));
                AppendAudit(segment, tick);
                committed.Add(segment);
            }
            return committed;
        }

        private GridDirection FacingOf(UnitId unitId)
            => _grid.TryGetFacing(unitId, out GridDirection facing) ? facing : GridDirection.East;

        private void AppendAudit(MovementSegment segment, long tick)
        {
            if (!_audit.TryGetValue(segment.ActionPlanId.Value, out List<CommittedMovementStep> list))
            {
                list = new List<CommittedMovementStep>();
                _audit[segment.ActionPlanId.Value] = list;
            }
            list.RemoveAll(step => step.StepIndex == segment.StepIndex);
            list.Add(new CommittedMovementStep(
                segment.ActionPlanId, segment.StepIndex, segment.UnitId, segment.From, segment.To,
                segment.Direction, segment.StepWeightUnits, segment.StartTick, segment.EndTick, tick));
            list.Sort((a, b) => a.StepIndex.CompareTo(b.StepIndex));
        }

        private static int CompareByCommitOrder(MovementSegment a, MovementSegment b)
        {
            int byEnd = a.EndTick.CompareTo(b.EndTick);
            if (byEnd != 0) return byEnd;
            return MovementSegment.Compare(a, b);
        }

        // ————————————————————————————————————————————————————————————
        // 启动门禁的空间查询
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 面向冻结的 <c>ActionStartGateContext.SpaceBlockUntilTickOf</c> 的适配器
        /// （Free ⇒ null、Retryable ⇒ ReleaseTick、TerminalOrUnknown ⇒ <c>long.MaxValue</c>）。
        /// </summary>
        public Func<UnitId, long?> SpaceBlockUntilTickOf(long tick)
            => _grid.BuildSpaceBlockUntilTickOf(tick, unitId => ActiveReleaseTickOf(unitId, tick));

        // ————————————————————————————————————————————————————————————
        // 终态清理参与者（固定顺序：MovementAndReservation = 500）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 任意终态原因都走这里：
        /// <list type="number">
        /// <item>按 <c>(ActionPlanId, StepIndex)</c> 移除该计划的活动与未来 <c>MovementSegment</c>；</item>
        /// <item>按稳定空间键释放该计划的<strong>全部</strong> Reservation；</item>
        /// <item>保留已完成段与最终逻辑位置的<strong>只读审计</strong>。</item>
        /// </list>
        /// 幂等：重复清理是安全无操作。该参与者<strong>不</strong>改计划终态字段、<strong>不</strong>发事件、
        /// <strong>不</strong>改 <c>ScheduleRevision</c>。
        /// </summary>
        public void Cleanup(ActionPlanTerminalContext context)
        {
            if (context == null || !context.IsFirstRequest) return;
            ActionPlan plan = context.Plan;
            if (plan == null) return;

            ReleasePlanMovement(plan.ActionPlanId);
            // Dodge 目的格预留与段/空间预留同属一个清理槽位：同一次 Cleanup 内释放。
            DodgeDestinationReservations?.ReleaseForPlan(plan);
            if (_grid.TryGetPendingDestination(plan.OwnerUnitId, out GridPoint destination) &&
                plan.Destination.HasValue &&
                destination.X == plan.Destination.Value.X && destination.Y == plan.Destination.Value.Y)
            {
                _grid.SetPendingDestination(plan.OwnerUnitId, null);
            }
        }

        // ————————————————————————————————————————————————————————————
        // 自检
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 只读不变量检查：段的 <c>To</c> 必须有同键 Reservation、段链必须连续、
        /// 计划必须仍注册或已进入终态且无残留段。返回 null = 通过。
        /// </summary>
        public string VerifyInvariants()
        {
            foreach (KeyValuePair<ReservationKey, MovementSegment> pair in _segments)
            {
                if (!_grid.TryGetReservation(pair.Key, out Reservation reservation))
                    return ScheduleCodes.SCHEDULE_TERMINAL_ORPHAN_ACTIVE_REFERENCE + ":missing-reservation=" + pair.Key;
                if (reservation.Cell.X != pair.Value.To.X || reservation.Cell.Y != pair.Value.To.Y)
                    return ScheduleCodes.SCHEDULE_TERMINAL_ORPHAN_ACTIVE_REFERENCE + ":cell-mismatch=" + pair.Key;
            }

            foreach (var pair in _segments)
            {
                var segment = pair.Value;
                var id = segment.ActionPlanId;
                if (segment.StepIndex > 0 && _segments.TryGetValue(new ReservationKey(id, segment.StepIndex - 1), out var previous))
                {
                    if (segment.From != previous.To) return MovementCodes.MOVEMENT_SEGMENT_CHAIN_DISCONTINUOUS + ":" + previous + " -> " + segment;
                    continue;
                }
                // Only the first still-active segment can lack its predecessor. Earlier
                // committed segments are intentionally absent. Validate each chain once
                // without allocating ordered temporary lists for every plan on every Tick.
                int count = 0;
                foreach (var other in _segments.Values)
                {
                    if (other.ActionPlanId != id) continue;
                    if (other.StepIndex < segment.StepIndex) return MovementCodes.MOVEMENT_SEGMENT_CHAIN_DISCONTINUOUS + ":missing predecessor=" + segment;
                    if (other.StepIndex > segment.StepIndex && (!_segments.TryGetValue(new ReservationKey(id, other.StepIndex - 1), out var predecessor)
                        || other.From != predecessor.To))
                        return MovementCodes.MOVEMENT_SEGMENT_CHAIN_DISCONTINUOUS + ":broken predecessor=" + other;
                    count++;
                }
                if (_grid.ReservationCountOfPlan(id) != count)
                    return MovementCodes.MOVEMENT_RESERVATION_BATCH_MISMATCH + ":plan=" + id.Value.ToString(CultureInfo.InvariantCulture);
            }

            // 终态计划不得残留活动段（任务包「必须验收」：任意计划终态后不存在该计划的活动/未来段）。
            foreach (KeyValuePair<ReservationKey, MovementSegment> pair in _segments)
            {
                ActionPlan plan = _authority.Registry.Find(pair.Key.ActionPlanId);
                if (plan != null && plan.IsTerminal)
                    return ScheduleCodes.SCHEDULE_TERMINAL_ORPHAN_ACTIVE_REFERENCE +
                           ":terminal-plan=" + pair.Key;
            }
            return null;
        }

        private static ReservationKey KeyOf(MovementSegment segment)
            => new ReservationKey(segment.ActionPlanId, segment.StepIndex);

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
            => "movement-authority segments=" + _segments.Count.ToString(CultureInfo.InvariantCulture) +
               " reservations=" + _grid.AllReservationsOrdered().Count.ToString(CultureInfo.InvariantCulture);
    }
}
