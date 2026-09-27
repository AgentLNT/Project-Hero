using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Movement
{
    /// <summary>
    /// <strong>Dodge 换位事务的稳定失败码</strong>（任务包「必须产出」11 与
    /// 「Dodge 换位与移动依赖的原子提交」）。只增不改；失败一律
    /// <strong>零局部写入</strong>。
    /// </summary>
    public static class DodgeRelocationCodes
    {
        /// <summary>Dodge 载荷缺失或不是 Dodge（调用方给了错误的计划/规格）。</summary>
        public const string DODGE_RELOCATION_PLAN_NOT_CANONICAL = "DODGE_RELOCATION_PLAN_NOT_CANONICAL";

        /// <summary>计划没有目的格，或没有固定的 <c>TriggerTick</c> 绑定。</summary>
        public const string DODGE_DESTINATION_MISSING = "DODGE_DESTINATION_MISSING";

        /// <summary>目的格越界（Encounter 边界或完整 footprint 越界）。</summary>
        public const string DODGE_DESTINATION_OUT_OF_BOUNDS = "DODGE_DESTINATION_OUT_OF_BOUNDS";

        /// <summary>位移不是单个规范方向的整数步，或步数严格超过 <c>MaxDistanceSteps</c>。</summary>
        public const string DODGE_DESTINATION_TOO_FAR = "DODGE_DESTINATION_TOO_FAR";

        /// <summary>该方向不在 Dodge 的 <c>MovementPatternSpec</c> 覆盖范围内。</summary>
        public const string DODGE_DESTINATION_DIRECTION_NOT_IN_PATTERN =
            "DODGE_DESTINATION_DIRECTION_NOT_IN_PATTERN";

        /// <summary>目的格当前被<strong>其他单位</strong>的权威占位占用（Dodge 无抢占权限）。</summary>
        public const string DODGE_DESTINATION_OCCUPIED = "DODGE_DESTINATION_OCCUPIED";

        /// <summary>目的格在重叠时间窗内被<strong>其他计划</strong>的 Reservation 持有。</summary>
        public const string DODGE_DESTINATION_RESERVED_BY_OTHER = "DODGE_DESTINATION_RESERVED_BY_OTHER";

        /// <summary>同一机会重复预留，或同一目的格已被<strong>本批更早的规范序</strong>预留。</summary>
        public const string DODGE_DESTINATION_ALREADY_RESERVED = "DODGE_DESTINATION_ALREADY_RESERVED";

        /// <summary>预留区间非法（<c>TriggerTick</c> 不严格晚于预留时刻）。</summary>
        public const string DODGE_DESTINATION_INTERVAL_INVALID = "DODGE_DESTINATION_INTERVAL_INVALID";

        /// <summary>换位时单位已不在预留记录的 <c>From</c>（内部矛盾 ⇒ InvariantViolation）。</summary>
        public const string DODGE_RELOCATION_ORIGIN_MISMATCH = "DODGE_RELOCATION_ORIGIN_MISMATCH";

        /// <summary>换位时不存在该机会的目的格预留（内部矛盾 ⇒ InvariantViolation）。</summary>
        public const string DODGE_RELOCATION_NOT_RESERVED = "DODGE_RELOCATION_NOT_RESERVED";

        /// <summary>换位提交阶段的内部矛盾（占位索引/所有权）：零局部写入并令 Step 失败。</summary>
        public const string DODGE_RELOCATION_INVARIANT_VIOLATION = "DODGE_RELOCATION_INVARIANT_VIOLATION";

        /// <summary>目的格体积规范表不可用（单位未绑定规范体积表）。</summary>
        public const string DODGE_DESTINATION_VOLUME_NOT_CANONICAL = "DODGE_DESTINATION_VOLUME_NOT_CANONICAL";
    }

    /// <summary>
    /// Dodge 目的格的<strong>纯数据规则</strong>（<c>DodgePayloadSpec</c> 的运行时投影）。
    ///
    /// 它把"距离/Pattern"从 <c>BattleDefinition</c> 解耦出来，使空间事务可以在
    /// 没有任何 <c>ActionPlan</c>/定义实例的情况下被独立验证（也是只读预检的入口形态）。
    /// 生产装配用 <see cref="FromPayload"/> 从权威定义投影，绝不自行拼一套方向表。
    /// </summary>
    public sealed record DodgeDestinationRules(
        int MaxDistanceSteps,
        IReadOnlyList<DirectionalTriangleSet> PatternDirections)
    {
        /// <summary>从冻结的 Dodge 载荷投影（唯一的定义来源）。</summary>
        public static DodgeDestinationRules FromPayload(DodgePayloadSpec payload)
        {
            if (payload == null || payload.Pattern == null) return null;
            return new DodgeDestinationRules(payload.MaxDistanceSteps, payload.Pattern.Directions);
        }

        /// <summary>
        /// 该方向是否被 Pattern 覆盖。Pattern 是任务 02B 的规范 12 向表：
        /// 缺席（null）或空集合的方向<strong>不</strong>被覆盖。
        /// </summary>
        public bool DirectionAllowed(GridDirection direction)
        {
            if (PatternDirections == null) return false;
            int index = (int)direction;
            if (index < 0 || index >= PatternDirections.Count) return false;
            DirectionalTriangleSet set = PatternDirections[index];
            if (set == null || set.Direction != direction) return false;
            return set.Triangles != null && set.Triangles.Count > 0;
        }
    }

    /// <summary>一次 Dodge 目的格预留请求（纯数据；唯一的预留输入形态）。</summary>
    public sealed record DodgeDestinationRequest(
        ReactionOpportunityId OpportunityId,
        ActionPlanId ReactionPlanId,
        UnitId UnitId,
        GridDirection Facing,
        GridPoint From,
        GridPoint Destination,
        long TriggerTick,
        long CommandSequence);

    /// <summary>
    /// <strong>Dodge 目的格预留</strong>：反应接受事务按
    /// <c>ReactionOpportunityId</c>/<c>ActionPlanId</c> 为 <c>TriggerTick</c> 冻结的合法目的格。
    ///
    /// 时间窗固定为 <c>[ReservedAtTick, TriggerTick]</c>（含端点）：它覆盖"从现在到换位那一刻"，
    /// 冲突判定只在这个窗口与既有 Reservation 的
    /// <c>[StartTick, EndTick)</c> 相交时成立。窗口之外（例如 <c>StartTick &gt;= TriggerTick</c>
    /// 的纯未来预留）不构成冲突，但<strong>权威占位</strong>冲突仍然成立（Dodge 无抢占权限）。
    /// </summary>
    public sealed record DodgeDestinationReservation(
        ReactionOpportunityId OpportunityId,
        ActionPlanId ReactionPlanId,
        UnitId UnitId,
        GridDirection Facing,
        GridPoint From,
        GridPoint Destination,
        int MaxDistanceSteps,
        IReadOnlyList<DirectionalTriangleSet> PatternDirections,
        long TriggerTick,
        long ReservedAtTick,
        long CommandSequence)
    {
        /// <summary>该预留是否与 <c>[startTick, endTick)</c> 相交。</summary>
        public bool Overlaps(long startTick, long endTick)
            => startTick <= TriggerTick && ReservedAtTick < endTick;

        public DodgeDestinationRules Rules => new DodgeDestinationRules(MaxDistanceSteps, PatternDirections);

        public override string ToString()
            => "dodge#" + OpportunityId.Value.ToString(CultureInfo.InvariantCulture) +
               " plan#" + ReactionPlanId.Value.ToString(CultureInfo.InvariantCulture) +
               " unit#" + UnitId.Value.ToString(CultureInfo.InvariantCulture) +
               " " + From + "->" + Destination +
               "@" + TriggerTick.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>一次预留尝试的只读结果（按规范顺序返回）。</summary>
    public sealed record DodgeReservationOutcome(
        ReactionOpportunityId OpportunityId,
        ActionPlanId ReactionPlanId,
        bool Reserved,
        string Code);

    /// <summary>统一只读空间快照中的一个单位条目（锚点 / 朝向 / 格 / 三角）。</summary>
    public sealed record DodgeSpaceUnitEntry(
        UnitId UnitId,
        GridPoint Anchor,
        GridDirection Facing,
        IReadOnlyList<GridPoint> Cells,
        IReadOnlyList<TrianglePoint> Triangles);

    /// <summary>
    /// <strong>任何 Dodge 提交前的统一只读空间快照</strong>（任务包「必须产出」11 末段）。
    ///
    /// 任务 08 用它 + 同一批冻结 Intent 建立"旧接触"证据；提交后再取新位置建"新接触"，
    /// 两者取并集、保留 Undodgeable 旧接触并去重。快照是<strong>冻结的深拷贝</strong>：
    /// 提交之后旧快照仍然只反映提交前的位置，因此旧接触证据不会丢失。
    /// </summary>
    public sealed record DodgeSpaceSnapshot(
        long Tick,
        IReadOnlyList<DodgeSpaceUnitEntry> Units,
        IReadOnlyList<Reservation> MovementReservations,
        IReadOnlyList<DodgeDestinationReservation> DodgeReservations,
        IReadOnlyList<MovementSegment> Segments)
    {
        public static readonly DodgeSpaceSnapshot Empty = new DodgeSpaceSnapshot(
            0L, Array.Empty<DodgeSpaceUnitEntry>(), Array.Empty<Reservation>(),
            Array.Empty<DodgeDestinationReservation>(), Array.Empty<MovementSegment>());

        /// <summary>该单位在快照中的锚点（不存在则 false）。</summary>
        public bool TryGetAnchor(UnitId unitId, out GridPoint anchor)
        {
            for (int i = 0; i < Units.Count; i++)
            {
                if (Units[i].UnitId.Value != unitId.Value) continue;
                anchor = Units[i].Anchor;
                return true;
            }
            anchor = default;
            return false;
        }
    }

    /// <summary>
    /// 一次 Dodge 换位提交的结果。
    ///
    /// <strong><c>Committed = false</c> 的精确含义（任务 06 小修轮 R1 更正，旧文案"零局部写入
    /// ⇒ Reservation 保持原样"与代码不符，已废除）</strong>：
    /// <list type="bullet">
    /// <item><strong>不</strong>保证"零局部写入"这一句话本身；它保证的是
    /// <strong>计划终态 ⇒ 不保留该计划拥有的任何预留</strong>（任务包「逻辑与表现边界」：
    /// "任意计划终态后都不存在该计划的活动/未来 MovementSegment 或 Reservation"）；</item>
    /// <item><see cref="DefenseFailureReason"/> 为
    /// <see cref="ActionTerminationReason.TargetInvalid"/> 的失败分支，
    /// <strong>只</strong>出现在"该 Dodge 计划必随之进入终态"的语义上
    /// （见 <see cref="DodgeRelocationAuthority.TryRelocate"/> 的终态写入）；
    /// 此时本计划的目的格预留被<strong>同批释放</strong>（幂等），因为它已无消费者。
    /// 这不违反上面那条不变量，恰恰是它的要求；</item>
    /// <item>其余失败分支（<c>DefenseFailureReason = None</c>，例如
    /// <c>DODGE_RELOCATION_ORIGIN_MISMATCH</c> 与
    /// <c>DODGE_RELOCATION_INVARIANT_VIOLATION</c>）<strong>不</strong>释放本预留：
    /// 没有任何证据表明计划已终态，预留按"计划仍存活"保留下来供重试/后续释放路径处理；</item>
    /// <item>无论哪条分支，<strong>占位、他人的 Reservation、移动段与预算始终原样</strong>：
    /// 换位本身从未开始（唯一的锚点变异是 <c>CommitAnchor</c>，它在全部失败分支之后）。</item>
    /// </list>
    ///
    /// 判定谓词是 <see cref="DodgeCommitResult.TerminatesPlan"/>：调用方（接缝/任务 08）
    /// 必须用它区分"计划已终态 ⇒ 预留已被释放"与"计划仍存活 ⇒ 预留仍在"两种情形，
    /// 不得再用"失败 ⇒ 预留保持原样"的旧假设编排重试。
    /// </summary>
    public sealed record DodgeCommitResult(
        ActionPlanId ReactionPlanId,
        ReactionOpportunityId OpportunityId,
        UnitId UnitId,
        GridPoint From,
        GridPoint Destination,
        bool Committed,
        string Code,
        ActionTerminationReason DefenseFailureReason,
        long Tick)
    {
        /// <summary>实际发生了位置变化（<c>Committed</c> 且 <c>From != Destination</c>）。</summary>
        public bool Moved => Committed && (From.X != Destination.X || From.Y != Destination.Y);

        /// <summary>
        /// <strong>本结果是否已经/必然把该 Dodge 计划推入终态</strong>
        /// （任务 06 小修轮 R1 新增的唯一判定谓词）。
        ///
        /// <c>true</c> 当且仅当 <c>Committed = false</c> 且
        /// <see cref="DefenseFailureReason"/> 为 <see cref="ActionTerminationReason.TargetInvalid"/>：
        /// 防御者自身原因（目的格在触发时刻已越界/被占/体积非法）导致触发失败。
        /// 在这条语义上，调用方<strong>必须</strong>让该计划进入终态，而按"终态不留预留"的不变量，
        /// 本计划的目的格预留已在同一次提交里被释放 ⇒ 此时
        /// "<c>Committed = false</c> 但预留已消失"是正确的、可预期的形态。
        ///
        /// <c>false</c> 时（成功提交、零位移提交、或 <c>DefenseFailureReason = None</c> 的
        /// 内部矛盾失败）预留的存活状态与提交是否成功一致：成功/零位移 ⇒ 已释放，
        /// 矛盾失败 ⇒ 保持原样。
        /// </summary>
        public bool TerminatesPlan =>
            !Committed && DefenseFailureReason == ActionTerminationReason.TargetInvalid;

        public override string ToString()
            => "dodge-commit plan#" + ReactionPlanId.Value.ToString(CultureInfo.InvariantCulture) +
               " " + From + "->" + Destination +
               " committed=" + (Committed ? "yes" : "no") +
               (Code == null ? string.Empty : " code=" + Code);
    }

    /// <summary>
    /// 固定阶段的 Dodge 提交报告：<strong>提交前</strong>统一只读快照 + 全部提交结果 +
    /// <strong>全部提交后</strong>的新位置快照。任务 08 只用这一个对象就能同时拿到
    /// 旧/新接触来源、<c>From</c>/<c>Destination</c> 与每个提交的结果。
    /// </summary>
    public sealed record DodgeCommitReport(
        long Tick,
        DodgeSpaceSnapshot Before,
        IReadOnlyList<DodgeCommitResult> Results,
        DodgeSpaceSnapshot After)
    {
        public static DodgeCommitReport Empty(long tick)
            => new DodgeCommitReport(tick, DodgeSpaceSnapshot.Empty, Array.Empty<DodgeCommitResult>(),
                DodgeSpaceSnapshot.Empty);

        /// <summary>本批实际发生换位的单位 → 新锚点（按 <c>UnitId</c> 升序）。</summary>
        public IReadOnlyList<KeyValuePair<UnitId, GridPoint>> CommittedPositions()
        {
            var result = new List<KeyValuePair<UnitId, GridPoint>>();
            for (int i = 0; i < Results.Count; i++)
            {
                if (Results[i].Moved) result.Add(new KeyValuePair<UnitId, GridPoint>(Results[i].UnitId, Results[i].Destination));
            }
            result.Sort((a, b) => a.Key.Value.CompareTo(b.Key.Value));
            return result;
        }
    }

    /// <summary>
    /// <strong>只读条件预检</strong>的结果（任务包「提供同源只读条件预检，列出受影响 Move PlanId」）。
    /// 它由与提交<strong>完全相同</strong>的求值函数产出：预检绝不写状态、绝不分配 ID。
    /// </summary>
    public sealed record DodgeDependencyPreview(
        bool WouldCommit,
        string Code,
        IReadOnlyList<ActionPlanId> AffectedPlanIds,
        IReadOnlyList<ActionPlanId> BudgetReleasePlanIds);

    /// <summary>
    /// 终态清理参与者对 Dodge 目的格预留的释放端口（任务 06 内部接线：
    /// <see cref="LogicGridMovementAuthority"/> 在计划终态时调用，不新增第二个清理参与者）。
    /// </summary>
    public interface IDodgeDestinationReservationSink
    {
        void ReleaseForPlan(ActionPlan plan);
    }

    /// <summary>
    /// <strong>Dodge 换位事务的真实实现</strong>（任务包「必须产出」11 与
    /// 「Dodge 换位与移动依赖的原子提交」）。
    ///
    /// 它同时是：
    /// <list type="bullet">
    /// <item>任务 05 冻结的 <see cref="IDodgeRelocationTransaction"/>（TriggerTick 换位端口）；</item>
    /// <item>反应接受事务的目的格预留端口 <see cref="IDodgeDestinationReservationPort"/>；</item>
    /// <item>来源威胁取消的预留释放接收方 <see cref="IReactionReservationReleaseSink"/>
    /// （任务 05 的 <c>SourceThreatCancelled</c> 通知）；</item>
    /// <item>统一终态清理的 Dodge 预留释放端口 <see cref="IDodgeDestinationReservationSink"/>
    /// （挂在既有的 <c>MovementAndReservation = 500</c> 槽位上，不新增参与者）。</item>
    /// </list>
    ///
    /// 冻结语义：
    /// <list type="number">
    /// <item><strong>预留</strong>：接受事务按 <c>ReactionOpportunityId</c>/<c>ActionPlanId</c> 为
    /// <c>TriggerTick</c> 冻结合法目的格；目的格必须满足距离/Pattern、Encounter 边界、
    /// 占位与时间窗冲突规则。冲突胜者<strong>只</strong>由调用方给出的规范命令序
    /// <c>CommandSequence</c> 决定（同批按 <c>(CommandSequence, ActionPlanId)</c> 升序处理，
    /// 与原始枚举顺序无关）；后到者稳定拒绝，<strong>不可抢占</strong>。</item>
    /// <item><strong>换位</strong>：固定阶段先用 <see cref="CaptureSpaceSnapshot"/> 冻结只读快照，
    /// 再用与预检<strong>完全相同</strong>的求值函数复检，然后<strong>原子</strong>提交
    /// "占位 From → Destination + 释放该目的格预留 + 失效依赖移动的全部未来段/预留 +
    /// 预算释放接缝 + 待进入格清理"。任一内部失败 ⇒ <strong>零局部写入</strong>并以
    /// <see cref="DodgeRelocationCodes.DODGE_RELOCATION_INVARIANT_VIOLATION"/> 失败。</item>
    /// <item><strong>失败/取消/未换位</strong>：不因本 Dodge 清理任何移动。来源威胁在触发前取消 ⇒
    /// 只释放预留；防御者自身原因（目的格已越界/被占/体积非法）导致触发失败 ⇒ 以
    /// <see cref="ActionTerminationReason.TargetInvalid"/> 终止该反应且<strong>不移动单位</strong>，
    /// 并因其进入终态而<strong>同批释放该计划自己的目的格预留</strong>
    /// （"计划终态后不存在该计划拥有的任何 Reservation"是不变量；见
    /// <see cref="DodgeCommitResult.TerminatesPlan"/>）。</item>
    /// <item><strong>不</strong>生成持续移动段、<strong>不</strong>逐步插值逻辑位置、
    /// <strong>不</strong>改 <c>ScheduleRevision</c>、<strong>不</strong>泄漏"无敌/Dodging"状态、
    /// <strong>不</strong>把攻击标为已闪避（接触复核仍是任务 08 的职责）。</item>
    /// </list>
    /// </summary>
    public sealed class DodgeRelocationAuthority :
        IDodgeRelocationTransaction,
        IDodgeDestinationReservationPort,
        IDodgeDestinationReservationSink,
        IReactionReservationReleaseSink
    {
        private readonly LogicGrid _grid;
        private readonly LogicGridMovementAuthority _movement;
        private readonly ActionScheduleAuthority _authority;
        private readonly ActionPlanTerminalCoordinator _coordinator;
        private readonly Func<ActionSpecId, DodgeDestinationRules> _rulesResolver;

        private readonly Dictionary<long, DodgeDestinationReservation> _reservations =
            new Dictionary<long, DodgeDestinationReservation>();

        private readonly List<DodgeCommitResult> _commitLog = new List<DodgeCommitResult>();

        public DodgeRelocationAuthority(
            LogicGrid grid,
            LogicGridMovementAuthority movement,
            ActionScheduleAuthority authority,
            ActionPlanTerminalCoordinator coordinator = null,
            Func<ActionSpecId, DodgeDestinationRules> rulesResolver = null)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _movement = movement ?? throw new ArgumentNullException(nameof(movement));
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _coordinator = coordinator;
            _rulesResolver = rulesResolver;
        }

        /// <summary>
        /// 任务 07 的<strong>预算释放接缝</strong>（可留接缝）。
        /// 只在换位<strong>实际发生</strong>时被调用一次，参数为按 <c>ActionPlanId</c> 升序的
        /// 失效计划；失败/取消/未换位时<strong>绝不</strong>被调用。
        ///
        /// <strong>顺序契约（任务 06 小修轮 R4/B-1）</strong>：它在换位提交的
        /// <strong>全部变异之前</strong>被调用（锚点提交已成功、依赖移动清理与待进入格清理尚未开始）。
        /// 因此即便消费者抛异常，世界也处在"完全未提交"的状态，调用方可安全重试，
        /// 不会出现"锚点已换、段/预留未清理"的半提交状态。
        ///
        /// 装配：生产装配点见 <c>BattleSimulationAssembly.BudgetReleaseSink</c>
        /// （<c>BattleSimulation</c> 在构造 <c>DodgeRelocationAuthority</c> 时接通）；
        /// 未注入时为 <c>null</c> ⇒ 不调用任何消费者（本任务不实现预算账本，见交接记录 §13.1）。
        /// </summary>
        public Action<IReadOnlyList<ActionPlanId>> BudgetReleaseSink { get; set; }

        /// <summary>从权威定义投影规则的解析器（装配回填；缺失时只能走纯数据预留入口）。</summary>
        public Func<ActionSpecId, DodgeDestinationRules> RulesResolver => _rulesResolver;

        public LogicGrid Grid => _grid;

        // ————————————————————————————————————————————————————————————
        // 只读查询
        // ————————————————————————————————————————————————————————————

        /// <summary>全部目的格预留的规范快照（按稳定空间键 <c>(TriggerTick, X, Y, OpportunityId)</c> 升序）。</summary>
        public IReadOnlyList<DodgeDestinationReservation> AllReservationsOrdered()
        {
            var result = new List<DodgeDestinationReservation>(_reservations.Count);
            foreach (KeyValuePair<long, DodgeDestinationReservation> pair in _reservations) result.Add(pair.Value);
            result.Sort(CompareCanonical);
            return result;
        }

        /// <summary>本 Tick 到期（<c>TriggerTick &lt;= tick</c>）的预留，按规范顺序。</summary>
        public IReadOnlyList<DodgeDestinationReservation> DueReservationsOrdered(long tick)
        {
            var result = new List<DodgeDestinationReservation>();
            foreach (KeyValuePair<long, DodgeDestinationReservation> pair in _reservations)
            {
                if (pair.Value.TriggerTick <= tick) result.Add(pair.Value);
            }
            result.Sort(CompareCanonical);
            return result;
        }

        public bool TryGetReservation(ReactionOpportunityId opportunityId, out DodgeDestinationReservation reservation)
            => _reservations.TryGetValue(opportunityId.Value, out reservation);

        /// <summary>该格是否被某个 Dodge 目的格预留在 <paramref name="tick"/> 覆盖。</summary>
        public bool IsCellReservedByDodge(GridPoint cell, long tick)
        {
            foreach (KeyValuePair<long, DodgeDestinationReservation> pair in _reservations)
            {
                DodgeDestinationReservation reservation = pair.Value;
                if (reservation.ReservedAtTick > tick || reservation.TriggerTick < tick) continue;
                IReadOnlyList<GridPoint> cells = _grid.ResolveDestinationCells(
                    reservation.UnitId, reservation.Destination, reservation.Facing);
                for (int c = 0; c < cells.Count; c++)
                {
                    if (cells[c].X == cell.X && cells[c].Y == cell.Y) return true;
                }
            }
            return false;
        }

        /// <summary>全部换位提交尝试的只读日志（按发生顺序）。</summary>
        public IReadOnlyList<DodgeCommitResult> CommitLog => _commitLog;

        /// <summary>自 <paramref name="startIndex"/> 起的提交尝试（固定阶段切片用）。</summary>
        public IReadOnlyList<DodgeCommitResult> CommitLogSince(int startIndex)
        {
            if (startIndex < 0) startIndex = 0;
            if (startIndex >= _commitLog.Count) return Array.Empty<DodgeCommitResult>();
            int count = _commitLog.Count - startIndex;
            var result = new List<DodgeCommitResult>(count);
            for (int i = startIndex; i < _commitLog.Count; i++) result.Add(_commitLog[i]);
            return result;
        }

        /// <summary>
        /// <strong>统一只读空间快照</strong>：单位锚点/朝向/footprint + 移动 Reservation +
        /// Dodge 目的格预留 + 活动移动段。全部为冻结深拷贝，提交后旧快照不变。
        /// </summary>
        public DodgeSpaceSnapshot CaptureSpaceSnapshot(long tick)
        {
            IReadOnlyList<UnitId> units = _grid.RegisteredUnitsOrdered();
            var entries = new List<DodgeSpaceUnitEntry>(units.Count);
            for (int i = 0; i < units.Count; i++)
            {
                UnitId unitId = units[i];
                if (!_grid.TryGetAnchor(unitId, out GridPoint anchor)) continue;
                _grid.TryGetFacing(unitId, out GridDirection facing);
                entries.Add(new DodgeSpaceUnitEntry(
                    unitId, anchor, facing, Snapshot(_grid.CellsOf(unitId)), Snapshot(_grid.TrianglesOf(unitId))));
            }

            return new DodgeSpaceSnapshot(
                tick,
                entries,
                Snapshot(_grid.AllReservationsOrdered()),
                AllReservationsOrdered(),
                Snapshot(_movement.AllSegmentsOrdered()));
        }

        /// <summary>构造"提交前 / 提交后"成对的报告（任务 08 的固定阶段入口）。</summary>
        public DodgeCommitReport BuildReport(long tick, DodgeSpaceSnapshot before, IReadOnlyList<DodgeCommitResult> results)
            => new DodgeCommitReport(
                tick, before,
                results ?? Array.Empty<DodgeCommitResult>(),
                CaptureSpaceSnapshot(tick));

        // ————————————————————————————————————————————————————————————
        // 预留（反应接受事务）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <strong>纯数据预留入口</strong>：显式给出规则与全部输入，不依赖任何
        /// <c>ActionPlan</c>/定义实例。成功返回 null；失败返回稳定码且零写入。
        /// </summary>
        public string ReserveDestination(
            DodgeDestinationRequest request, DodgeDestinationRules rules, long tick)
        {
            IReadOnlyList<DodgeReservationOutcome> outcomes =
                ReserveDestinations(new[] { request }, new[] { rules }, tick);
            return outcomes.Count == 0 ? DodgeRelocationCodes.DODGE_DESTINATION_MISSING : outcomes[0].Code;
        }

        /// <summary>
        /// <strong>整批预留</strong>（冲突胜者只由规范命令序决定）。
        ///
        /// 处理顺序固定为 <c>(CommandSequence, ActionPlanId)</c> 升序，因此同一批请求以不同的
        /// 原始枚举顺序输入时，赢家、拒绝结果与最终预留集合完全一致。
        /// 单条失败<strong>不</strong>回滚同批中已经成功的预留（每条请求是独立的接受事务）；
        /// 每条失败项都保证零局部写入。
        /// </summary>
        public IReadOnlyList<DodgeReservationOutcome> ReserveDestinations(
            IReadOnlyList<DodgeDestinationRequest> requests,
            IReadOnlyList<DodgeDestinationRules> rules,
            long tick)
        {
            if (requests == null || requests.Count == 0) return Array.Empty<DodgeReservationOutcome>();

            var order = new List<int>(requests.Count);
            for (int i = 0; i < requests.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                DodgeDestinationRequest left = requests[a];
                DodgeDestinationRequest right = requests[b];
                int bySequence = left.CommandSequence.CompareTo(right.CommandSequence);
                if (bySequence != 0) return bySequence;
                return left.ReactionPlanId.Value.CompareTo(right.ReactionPlanId.Value);
            });

            var outcomes = new DodgeReservationOutcome[requests.Count];
            for (int i = 0; i < order.Count; i++)
            {
                int index = order[i];
                DodgeDestinationRequest request = requests[index];
                DodgeDestinationRules rule = rules != null && index < rules.Count ? rules[index] : null;
                string code = TryReserveOne(request, rule, tick);
                outcomes[index] = new DodgeReservationOutcome(
                    request.OpportunityId, request.ReactionPlanId, code == null, code);
            }
            return outcomes;
        }

        private string TryReserveOne(DodgeDestinationRequest request, DodgeDestinationRules rules, long tick)
        {
            if (request == null) return DodgeRelocationCodes.DODGE_DESTINATION_MISSING;
            if (rules == null) return DodgeRelocationCodes.DODGE_RELOCATION_PLAN_NOT_CANONICAL;
            if (request.TriggerTick <= tick) return DodgeRelocationCodes.DODGE_DESTINATION_INTERVAL_INVALID;
            if (_reservations.ContainsKey(request.OpportunityId.Value))
                return DodgeRelocationCodes.DODGE_DESTINATION_ALREADY_RESERVED;
            if (!_grid.Contains(request.UnitId)) return LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN;

            var provisional = new DodgeDestinationReservation(
                request.OpportunityId, request.ReactionPlanId, request.UnitId, request.Facing,
                request.From, request.Destination, rules.MaxDistanceSteps, rules.PatternDirections,
                request.TriggerTick, tick, request.CommandSequence);

            string error = EvaluateDestination(provisional);
            if (error != null) return error;

            _reservations[request.OpportunityId.Value] = provisional;
            return null;
        }

        /// <summary><see cref="IDodgeDestinationReservationPort"/> 的生产实现。</summary>
        public string ReserveDestination(
            ReactionOpportunityId opportunityId,
            UnitId defenderUnitId,
            ActionSpecId dodgeSpecId,
            GridPoint destination,
            long triggerTick,
            long commandSequence,
            long tick)
        {
            DodgeDestinationRules rules = _rulesResolver?.Invoke(dodgeSpecId);
            if (rules == null) return DodgeRelocationCodes.DODGE_RELOCATION_PLAN_NOT_CANONICAL;
            if (!_grid.TryGetAnchor(defenderUnitId, out GridPoint from))
                return LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN;
            // 朝向取该单位当前的权威朝向；换位提交使用同一个朝向，因此 footprint 不会在
            // "预留 -> 提交"之间漂移。Dodge 不改变朝向（它是位移，不是转向）。
            if (!_grid.TryGetFacing(defenderUnitId, out GridDirection facing))
                return LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN;

            var request = new DodgeDestinationRequest(
                opportunityId, default, defenderUnitId, facing, from, destination, triggerTick, commandSequence);
            return ReserveDestination(request, rules, tick);
        }

        /// <summary>
        /// <see cref="IDodgeDestinationReservationPort.BindPlan"/>：计划创建成功后绑定
        /// <c>ActionPlanId</c>。它<strong>不</strong>改变预留的合法性，也不重新求值。
        /// 预留不存在时是安全无操作（接受事务已回滚的情形）。
        /// </summary>
        public void BindPlan(ReactionOpportunityId opportunityId, ActionPlanId reactionPlanId)
        {
            if (!_reservations.TryGetValue(opportunityId.Value, out DodgeDestinationReservation existing)) return;
            if (existing.ReactionPlanId.Value == reactionPlanId.Value) return;
            _reservations[opportunityId.Value] = existing with { ReactionPlanId = reactionPlanId };
        }

        /// <summary>释放单个机会的预留（幂等）。返回是否真的存在并释放。</summary>
        public bool ReleaseDestination(ReactionOpportunityId opportunityId)
            => _reservations.Remove(opportunityId.Value);

        // 显式实现：接口要求 void，而诊断面需要"是否真的释放过"。
        void IDodgeDestinationReservationPort.ReleaseDestination(ReactionOpportunityId opportunityId)
            => ReleaseDestination(opportunityId);

        /// <summary>来源威胁在触发前取消：只释放预留，<strong>不</strong>清理任何移动。</summary>
        public void ReleaseFor(ActionPlanId reactionPlanId, ReactionOpportunityId opportunityId, long tick)
            => ReleaseDestination(opportunityId);

        /// <summary>计划终态（任意原因）：释放它仍未消费的目的格预留（幂等）。</summary>
        public void ReleaseForPlan(ActionPlan plan)
        {
            if (plan == null) return;
            if (plan.ReactionOpportunityId.HasValue) ReleaseDestination(plan.ReactionOpportunityId.Value);
            // 兜底：机会 id 不可用时按计划 id 反查（预留可能尚未绑定机会）。
            var stale = new List<long>();
            foreach (KeyValuePair<long, DodgeDestinationReservation> pair in _reservations)
            {
                if (pair.Value.ReactionPlanId.Value == plan.ActionPlanId.Value) stale.Add(pair.Key);
            }
            for (int i = 0; i < stale.Count; i++) _reservations.Remove(stale[i]);
        }

        // ————————————————————————————————————————————————————————————
        // 只读预检（与提交共用同一个求值函数）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <strong>唯一的空间求值函数</strong>：预检与提交都只调用它，因此"预检说可以"
        /// 与"提交认为可以"在结构上是同一件事。
        ///
        /// 它<strong>不</strong>写任何状态、<strong>不</strong>分配 ID、<strong>不</strong>推进修订号。
        /// </summary>
        public string EvaluateDestination(DodgeDestinationReservation provisional)
        {
            if (provisional == null) return DodgeRelocationCodes.DODGE_DESTINATION_MISSING;

            // 1. Encounter 边界：结构合法性优先于距离与冲突（越界格不是"太远"，而是非法）。
            if (!_grid.Boundary.Contains(provisional.Destination))
                return DodgeRelocationCodes.DODGE_DESTINATION_OUT_OF_BOUNDS;

            // 2. 距离 / 单一规范方向。零步（目的地就是当前格）是合法的"原地 Dodge"：
            //    它在提交阶段不产生任何位移，因此也绝不失效任何移动依赖。
            bool sameSpot = provisional.From.X == provisional.Destination.X &&
                            provisional.From.Y == provisional.Destination.Y;
            if (!sameSpot)
            {
                int steps = CanonicalSteps(provisional.From, provisional.Destination, out GridDirection direction);
                if (steps < 1 || steps > provisional.MaxDistanceSteps)
                    return DodgeRelocationCodes.DODGE_DESTINATION_TOO_FAR;

                // 3. Pattern 覆盖（缺席/空集合的方向不被覆盖）。
                var rules = new DodgeDestinationRules(provisional.MaxDistanceSteps, provisional.PatternDirections);
                if (!rules.DirectionAllowed(direction))
                    return DodgeRelocationCodes.DODGE_DESTINATION_DIRECTION_NOT_IN_PATTERN;
            }

            // 4. 完整 footprint 的合法性与权威占位（只读）。
            string space = _grid.ValidateDestinationFor(
                provisional.UnitId, provisional.Destination, provisional.Facing);
            if (space != null) return space;

            IReadOnlyList<GridPoint> cells = _grid.ResolveDestinationCells(
                provisional.UnitId, provisional.Destination, provisional.Facing);
            if (cells.Count == 0) return DodgeRelocationCodes.DODGE_DESTINATION_VOLUME_NOT_CANONICAL;

            // 4. 时间窗冲突：其他计划的移动 Reservation。
            for (int i = 0; i < cells.Count; i++)
            {
                Reservation existing = _grid.ReservationAt(cells[i]);
                if (existing == null) continue;
                if (existing.UnitId.Value == provisional.UnitId.Value) continue;
                if (!provisional.Overlaps(existing.StartTick, existing.EndTick)) continue;
                return DodgeRelocationCodes.DODGE_DESTINATION_RESERVED_BY_OTHER + ":cell=" + cells[i];
            }

            // 5. 时间窗冲突：其他 Dodge 目的格预留。
            foreach (KeyValuePair<long, DodgeDestinationReservation> pair in _reservations)
            {
                DodgeDestinationReservation other = pair.Value;
                if (other.OpportunityId.Value == provisional.OpportunityId.Value) continue;
                if (!other.Overlaps(provisional.ReservedAtTick, provisional.TriggerTick + 1L)) continue;
                IReadOnlyList<GridPoint> otherCells = _grid.ResolveDestinationCells(
                    other.UnitId, other.Destination, other.Facing);
                for (int c = 0; c < otherCells.Count; c++)
                {
                    for (int k = 0; k < cells.Count; k++)
                    {
                        if (otherCells[c].X != cells[k].X || otherCells[c].Y != cells[k].Y) continue;
                        return DodgeRelocationCodes.DODGE_DESTINATION_RESERVED_BY_OTHER + ":dodge=" + other;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// <strong>同源只读条件预检</strong>：与提交共用 <see cref="EvaluateDestination"/>，
        /// 列出受影响 Move PlanId 与任务 07 的拟释放预算分组。
        /// 它不写状态、不分配 ID、不推进修订号（可由 UI/AI 反复调用）。
        /// </summary>
        public DodgeDependencyPreview PreviewDependencies(
            DodgeDestinationRequest request,
            DodgeDestinationRules rules,
            IReadOnlyList<ActionPlanId> affectedMoves,
            long tick)
        {
            IReadOnlyList<ActionPlanId> affected = Canonical(affectedMoves);
            if (request == null || rules == null)
                return new DodgeDependencyPreview(
                    false, DodgeRelocationCodes.DODGE_DESTINATION_MISSING, affected, Array.Empty<ActionPlanId>());

            var provisional = new DodgeDestinationReservation(
                request.OpportunityId, request.ReactionPlanId, request.UnitId, request.Facing,
                request.From, request.Destination, rules.MaxDistanceSteps, rules.PatternDirections,
                request.TriggerTick, tick, request.CommandSequence);

            string code = EvaluateDestination(provisional);
            bool wouldCommit = code == null;
            bool moves = wouldCommit &&
                         (request.From.X != request.Destination.X || request.From.Y != request.Destination.Y);
            return new DodgeDependencyPreview(
                wouldCommit, code, affected, moves ? affected : Array.Empty<ActionPlanId>());
        }

        // ————————————————————————————————————————————————————————————
        // TriggerTick 原子提交
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <strong>纯数据换位入口</strong>：按机会 id 找到预留并原子提交。
        /// 用于只读验证与固定阶段的直接驱动（生产路径经
        /// <see cref="IDodgeRelocationTransaction.TryRelocate"/> 走同一个内部实现）。
        ///
        /// 本入口<strong>不</strong>写计划终态（它拿不到 <c>ActionPlan</c>）；因此
        /// <see cref="DodgeCommitResult.TerminatesPlan"/> 为 <c>true</c> 时，调用方必须自行
        /// 经统一终态协调器终止该计划。目的格预留的释放已经在本调用内完成，
        /// 调用方<strong>不得</strong>再假设"失败后预留仍在"。
        /// </summary>
        public DodgeCommitResult CommitRelocation(
            ReactionOpportunityId opportunityId, IReadOnlyList<ActionPlanId> dependentMoves, long tick)
        {
            if (!_reservations.TryGetValue(opportunityId.Value, out DodgeDestinationReservation reservation))
            {
                var missing = new DodgeCommitResult(
                    default, opportunityId, default, default, default, false,
                    DodgeRelocationCodes.DODGE_RELOCATION_NOT_RESERVED,
                    ActionTerminationReason.None, tick);
                _commitLog.Add(missing);
                return missing;
            }
            return CommitReservation(reservation, dependentMoves, tick);
        }

        /// <summary>
        /// 任务 05 冻结的换位端口。返回 null = 换位成功；否则稳定拒绝码。
        ///
        /// <paramref name="invalidatedCandidates"/> 是<strong>预检输入</strong>：只有换位成功后
        /// 才按 <c>ActionPlanId</c> 升序释放它们的全部未来段/预留；失败时它们保持原样。
        ///
        /// <strong>失败语义（R1 更正后）</strong>：
        /// <list type="bullet">
        /// <item>防御者自身原因失败（<see cref="DodgeCommitResult.TerminatesPlan"/>）⇒ 本方法把该反应计划
        /// 经统一终态协调器终止为 <see cref="ActionTerminationReason.TargetInvalid"/>；
        /// 该计划的目的格预留已在提交内同批释放（"终态不留预留"不变量）。
        /// <paramref name="invalidatedCandidates"/> <strong>不</strong>被清理——未换位就不因本 Dodge
        /// 清理任何移动；</item>
        /// <item><c>DefenseFailureReason = None</c> 的失败（内部矛盾）⇒ 本方法<strong>不</strong>写终态，
        /// 预留按"计划仍存活"保留，调用方可重试或走来源威胁取消路径释放；</item>
        /// <item>无论哪条分支，占位、他人的 Reservation、段与预算都保持原样。</item>
        /// </list>
        /// </summary>
        public string TryRelocate(ActionPlan dodgePlan, IReadOnlyList<ActionPlan> invalidatedCandidates, long tick)
        {
            if (dodgePlan == null || dodgePlan.ActionType != ActionType.Dodge || !dodgePlan.IsReaction)
                return DodgeRelocationCodes.DODGE_RELOCATION_PLAN_NOT_CANONICAL;
            if (!dodgePlan.ReactionOpportunityId.HasValue)
                return DodgeRelocationCodes.DODGE_DESTINATION_MISSING;

            ReactionOpportunityId opportunityId = dodgePlan.ReactionOpportunityId.Value;
            if (!_reservations.TryGetValue(opportunityId.Value, out DodgeDestinationReservation reservation))
                return DodgeRelocationCodes.DODGE_RELOCATION_NOT_RESERVED;

            var ids = new List<ActionPlanId>(invalidatedCandidates?.Count ?? 0);
            if (invalidatedCandidates != null)
            {
                for (int i = 0; i < invalidatedCandidates.Count; i++)
                {
                    if (invalidatedCandidates[i] != null) ids.Add(invalidatedCandidates[i].ActionPlanId);
                }
            }

            DodgeCommitResult result = CommitReservation(reservation, ids, tick);
            if (result.Committed) return null;

            // 防御者自身原因导致触发失败：以 TargetInvalid 终止该反应计划；**不**移动单位，
            // **不**清理任何移动。来源威胁取消等外部原因不在这里写终态（由任务 05 的
            // SourceThreatCancelled 路径负责）。
            if (result.DefenseFailureReason == ActionTerminationReason.TargetInvalid &&
                _coordinator != null && !dodgePlan.IsTerminal)
            {
                _coordinator.EnterTerminal(dodgePlan, ActionTerminationReason.TargetInvalid, tick);
            }
            return result.Code;
        }

        private DodgeCommitResult CommitReservation(
            DodgeDestinationReservation reservation, IReadOnlyList<ActionPlanId> dependentMoves, long tick)
        {
            UnitId unitId = reservation.UnitId;
            GridPoint from = reservation.From;
            GridPoint destination = reservation.Destination;

            // —— 阶段 A：在统一只读快照下预检（零写入）——
            if (!_grid.TryGetAnchor(unitId, out GridPoint anchor))
                return Record(Fail(reservation, from, destination, DodgeRelocationCodes.DODGE_RELOCATION_ORIGIN_MISMATCH, tick));
            if (anchor.X != from.X || anchor.Y != from.Y)
                return Record(Fail(reservation, from, destination, DodgeRelocationCodes.DODGE_RELOCATION_ORIGIN_MISMATCH, tick));

            string space = _grid.ValidateDestinationFor(unitId, destination, reservation.Facing);
            if (space != null)
            {
                // 防御者自身原因（目的格已越界/被占/体积非法）⇒ TargetInvalid，单位不移动。
                // 该计划随本结果进入终态 ⇒ 按"终态不留预留"的不变量，本计划的目的格预留
                // 必须在同一次提交里释放（它已无任何消费者）。这正是 R1 更正后的契约。
                // 注意：这里只释放本 Dodge 自己的预留，不触碰任何移动状态。
                return Record(FailTerminal(
                    reservation, from, destination, space,
                    "目标格在触发时刻已不可用；该计划随本结果进入终态，其自有预留同批释放（幂等）。", tick));
            }

            IReadOnlyList<GridPoint> cells =
                _grid.ResolveDestinationCells(unitId, destination, reservation.Facing);
            for (int i = 0; i < cells.Count; i++)
            {
                Reservation existing = _grid.ReservationAt(cells[i]);
                if (existing == null) continue;
                if (existing.UnitId.Value == unitId.Value) continue;
                if (!reservation.Overlaps(existing.StartTick, existing.EndTick)) continue;
                return Record(FailTerminal(
                    reservation, from, destination,
                    DodgeRelocationCodes.DODGE_DESTINATION_RESERVED_BY_OTHER + ":cell=" + cells[i],
                    "目标格已被他人 Reservation 在预留窗口内持有；该计划随本结果进入终态，其自有预留同批释放（幂等）。",
                    tick));
            }

            bool moves = from.X != destination.X || from.Y != destination.Y;
            IReadOnlyList<ActionPlanId> affected = moves ? Canonical(dependentMoves) : Array.Empty<ActionPlanId>();

            // —— 阶段 B：原子提交（阶段 A 已把全部可失败条件排除，因此本阶段不可能失败）——
            //
            // 任务 06 小修轮 R4/B-1 的**顺序契约**：预算释放是外部回调，可能抛异常，
            // 因此它必须在**本方法的第一处变异之前**被调用：
            // <list type="number">
            // <item>此前的全部工作都是只读的（阶段 A 的预检 + 这里的规范排序），
            // 因此"回调抛异常 ⇒ 世界完全未提交"，调用方可安全重试；</item>
            // <item>此后的全部工作都不再可失败（<c>CommitAnchor</c> 的失败已在提交期分支处理），
            // 因此不存在"回调已发、变异做了一半"的半提交窗口；</item>
            // <item>旧实现把回调留在锚点提交与依赖移动清理<strong>之后</strong>：
            // 一旦任务 07 接通消费者，抛异常的消费者会留下"锚点已换、段/预留未清理"的状态。</item>
            // </list>
            // 参数与最终提交的失效集合逐字相同（同为 <c>moves ? Canonical(...) : 空</c>）。
            if (moves) BudgetReleaseSink?.Invoke(affected);

            string commit = _grid.CommitAnchor(unitId, destination, reservation.Facing);
            if (commit != null)
            {
                // 防御性分支（R1 结构证明：见类文档"CommitAnchor 与 ValidateDestinationFor
                // 判定同一格集合"的论证 ⇒ 正常路径不可达）。它是仅有的"失败但不进入终态"的
                // 提交期分支，因此**刻意保留**本计划的目的格预留：调用方（纯数据入口）
                // 没有得到任何"计划已终态"的证据，预留按"计划仍存活"留下。
                // CommitAnchor 自身已经回滚占位索引 ⇒ 占位/段/预算仍然原样。
                return Record(new DodgeCommitResult(
                    reservation.ReactionPlanId, reservation.OpportunityId, unitId, from, destination,
                    false, DodgeRelocationCodes.DODGE_RELOCATION_INVARIANT_VIOLATION + ":" + commit,
                    ActionTerminationReason.None, tick));
            }

            _reservations.Remove(reservation.OpportunityId.Value);

            if (moves)
            {
                for (int i = 0; i < affected.Count; i++) _movement.ReleasePlanMovement(affected[i]);
                _grid.SetPendingDestination(unitId, null);
            }

            return Record(new DodgeCommitResult(
                reservation.ReactionPlanId, reservation.OpportunityId, unitId, from, destination,
                true, null, ActionTerminationReason.None, tick));
        }

        /// <summary>
        /// 防御者自身原因导致的触发失败：结果带 <see cref="ActionTerminationReason.TargetInvalid"/>
        /// （⇒ <see cref="DodgeCommitResult.TerminatesPlan"/> 为 <c>true</c>），
        /// 并<strong>同批释放本计划的目的格预留</strong>——因为该计划必然进入终态，
        /// 而"终态不留该计划拥有的任何 Reservation"是不变量。
        /// 这是 R1 裁定后的<strong>唯一</strong>释放路径：集中在一处，便于契约文本、代码与测试三者对齐，
        /// 也便于下游一眼看出"预留消失"与"计划终态"是同一件事。
        ///
        /// <paramref name="note"/> 只用于把失败语义写进代码（不参与状态、不参与哈希）。
        /// </summary>
        private DodgeCommitResult FailTerminal(
            DodgeDestinationReservation reservation, GridPoint from, GridPoint destination,
            string code, string note, long tick)
        {
            if (note == null) throw new ArgumentNullException(nameof(note));
            _reservations.Remove(reservation.OpportunityId.Value);
            return new DodgeCommitResult(
                reservation.ReactionPlanId, reservation.OpportunityId, reservation.UnitId, from, destination,
                false, code, ActionTerminationReason.TargetInvalid, tick);
        }

        private DodgeCommitResult Fail(
            DodgeDestinationReservation reservation, GridPoint from, GridPoint destination, string code, long tick)
            => new DodgeCommitResult(
                reservation.ReactionPlanId, reservation.OpportunityId, reservation.UnitId, from, destination,
                false, code, ActionTerminationReason.None, tick);

        private DodgeCommitResult Record(DodgeCommitResult result)
        {
            _commitLog.Add(result);
            return result;
        }

        // ————————————————————————————————————————————————————————————
        // 辅助
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 位移是否为"单个规范方向的整数步"，并给出步数与方向。
        /// 步数 &lt; 1（含相同格）或不是任何规范方向的整数倍 ⇒ 返回 0。
        /// </summary>
        public static int CanonicalSteps(GridPoint from, GridPoint to, out GridDirection direction)
        {
            direction = default;
            long dx = (long)to.X - from.X;
            long dy = (long)to.Y - from.Y;
            for (int i = 0; i < GridNeighborTable.DirectionCount; i++)
            {
                var candidate = (GridDirection)i;
                int ox = GridNeighborTable.OffsetX(candidate);
                int oy = GridNeighborTable.OffsetY(candidate);
                if (ox == 0 && oy == 0) continue;
                if (ox != 0 && dx % ox != 0) continue;
                if (oy != 0 && dy % oy != 0) continue;
                if (ox == 0 && dx != 0) continue;
                if (oy == 0 && dy != 0) continue;
                long steps = ox != 0 ? dx / ox : dy / oy;
                if (ox != 0 && (long)ox * steps != dx) continue;
                if (oy != 0 && (long)oy * steps != dy) continue;
                if (steps <= 0) continue;
                if (steps > int.MaxValue) continue;
                if ((long)ox * steps != dx || (long)oy * steps != dy) continue;
                direction = candidate;
                return (int)steps;
            }
            return 0;
        }

        private static IReadOnlyList<ActionPlanId> Canonical(IReadOnlyList<ActionPlanId> ids)
        {
            if (ids == null || ids.Count == 0) return Array.Empty<ActionPlanId>();
            var values = new List<long>(ids.Count);
            for (int i = 0; i < ids.Count; i++) values.Add(ids[i].Value);
            values.Sort();
            var result = new List<ActionPlanId>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0 && values[i] == values[i - 1]) continue;   // 去重但保持升序
                result.Add(new ActionPlanId(values[i]));
            }
            return result;
        }

        private static int CompareCanonical(DodgeDestinationReservation a, DodgeDestinationReservation b)
        {
            int byTrigger = a.TriggerTick.CompareTo(b.TriggerTick);
            if (byTrigger != 0) return byTrigger;
            int byX = a.Destination.X.CompareTo(b.Destination.X);
            if (byX != 0) return byX;
            int byY = a.Destination.Y.CompareTo(b.Destination.Y);
            if (byY != 0) return byY;
            int bySequence = a.CommandSequence.CompareTo(b.CommandSequence);
            if (bySequence != 0) return bySequence;
            return a.OpportunityId.Value.CompareTo(b.OpportunityId.Value);
        }

        private static IReadOnlyList<T> Snapshot<T>(IReadOnlyList<T> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<T>();
            var result = new T[source.Count];
            for (int i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
            => "dodge-relocation reservations=" + _reservations.Count.ToString(CultureInfo.InvariantCulture) +
               " commits=" + _commitLog.Count.ToString(CultureInfo.InvariantCulture);
    }
}
