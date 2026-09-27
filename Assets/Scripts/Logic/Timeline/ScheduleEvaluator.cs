using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// 受影响的 Move 计划的路径投影（任务 06 的 1/2 权重 <c>Pathfinder</c> 接入面）。
    /// </summary>
    public sealed record MovementPathProjection(int EdgeCount, int WeightUnits);

    /// <summary>
    /// 移动链路径重算端口（任务包「必须规范」：受影响 Move 链的
    /// <strong>起止位置、路径、边数、路径权重、绝对 Tick 与预算/Reservation 接缝一起重算</strong>）。
    ///
    /// 任务 05 <strong>不</strong>实现寻路：它只定义接入面，并在其缺失时把
    /// <see cref="NotImplementedMovementPathCalculator"/> 作为默认实现。
    /// 任务 06 提供真实现（<c>LogicGridMovementPathCalculator</c> 消费唯一的纯逻辑
    /// <c>LogicPathfinder</c>），<strong>不改变</strong>本求值器的任何其他语义。
    /// </summary>
    public interface IMovementPathCalculator
    {
        /// <summary>
        /// 为受依赖变化影响的 Move 计划重算路径投影。
        /// 返回 null 表示"该计划<strong>确实</strong>无需重算"（例如它不依赖变动项）。
        ///
        /// 契约：实现<strong>不得</strong>用 null 表示"没有实现"/"算不出来"——
        /// 那必须以稳定码失败（见 <see cref="NotImplementedMovementPathCalculator"/>）。
        /// </summary>
        MovementPathProjection Recompute(ActionPlan plan, long newStartTick, long deltaTicks);
    }

    /// <summary>
    /// 任务 06 接入前的默认路径计算器：它<strong>不猜测</strong>路径，在真的被要求重算时
    /// 以稳定码失败（fail-closed）。
    ///
    /// <strong>修复（任务 05 交接记录 §27.2 / 缺陷 D1）</strong>：本类原先返回 <c>null</c>，
    /// 而 <see cref="ScheduleEvaluator.Evaluate"/> 把 <c>null</c> 解释为"该计划无需重算"
    /// ⇒ 实际行为是<strong>静默恒等</strong>，与本类型自身文档相反，且没有任何测试会失败。
    /// 现在它改为抛出 <see cref="ScheduleCodes.SCHEDULE_PATH_CALCULATOR_NOT_IMPLEMENTED"/>，
    /// 因此"忘记注入真实计算器"是显式可失败的，而不是被伪装成已完成。
    ///
    /// 生产装配不再使用本类型：<c>BattleSimulation</c> 默认绑定由自身 <c>LogicGrid</c> 构造的
    /// 真实现；只有显式注入本实例（负控制测试）才会走到这条失败路径。
    /// </summary>
    public sealed class NotImplementedMovementPathCalculator : IMovementPathCalculator
    {
        public static readonly NotImplementedMovementPathCalculator Instance = new NotImplementedMovementPathCalculator();

        public MovementPathProjection Recompute(ActionPlan plan, long newStartTick, long deltaTicks)
            => throw new LogicDefinitionException(
                ScheduleCodes.SCHEDULE_PATH_CALCULATOR_NOT_IMPLEMENTED,
                "plan=" + (plan == null ? "<null>" : plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture)) +
                " newStartTick=" + newStartTick.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 一次排程求值的输入意图（<strong>已由权威事务校验过</strong>的纯数据）。
    ///
    /// <list type="bullet">
    /// <item><see cref="Plan"/>：受影响的计划。Add 时是事务刚创建、<strong>尚未注册</strong>的候选计划，
    /// 因此预览路径也可以复用同一求值器而不产生任何权威写入。</item>
    /// <item><see cref="DesiredStartTick"/> / <see cref="IsDirectMove"/>：只有<strong>直接 Move</strong>
    /// 的计划可以早于它的旧投影；依赖闭包内的其他计划只使用自己的当前投影。</item>
    /// <item><see cref="AnchoredAfterPlanId"/>：显式插入锚点（<c>default</c> = 无锚点）。</item>
    /// <item><see cref="IsExplicitUserRequest"/>：该位点是<strong>显式命令</strong>请求的，
    /// 因此提交时要把 <c>LastRequestedStartTick</c> 更新到最终位点。系统自动延期
    /// <strong>不</strong>是显式请求——它只改绝对 Tick，绝不覆盖请求起点。</item>
    /// </list>
    /// </summary>
    public sealed record ScheduleOperationIntent(
        ActionPlan Plan,
        long DesiredStartTick,
        bool IsDirectMove,
        ActionPlanId AnchoredAfterPlanId,
        bool IsExplicitUserRequest = true)
    {
        /// <summary>是否存在显式锚点。</summary>
        public bool HasAnchor => AnchoredAfterPlanId.IsValid;
    }

    /// <summary>单个受影响的计划在求值前后的规范投影。</summary>
    public sealed record ScheduleOperationEvaluation(
        ActionPlanId PlanId,
        UnitId OwnerUnitId,
        long PreviousStartTick,
        long NewStartTick,
        long NewEndTick,
        bool IsDirectMove)
    {
        /// <summary>该计划是否被这次求值<strong>移动</strong>（直接放置或右侧避让）。</summary>
        public bool Moved => PreviousStartTick != NewStartTick;

        public long DeltaTicks => NewStartTick - PreviousStartTick;
    }

    /// <summary>
    /// 纯排程求值结果。<strong>它不携带任何新身份</strong>：预览与权威提交返回同一个类型与同一份
    /// 算法输出，因此"预览与提交使用同一求值器"是结构事实而不是约定。
    ///
    /// <see cref="DependencyClosurePlanCount"/> 是本次求值的<strong>依赖闭包规模</strong>
    /// （受影响计划集合的基数），用于消费 <c>ScheduleLimits.MaxDependencyClosurePlans</c>。
    /// </summary>
    public sealed record ScheduleEvaluationResult(
        IReadOnlyList<ScheduleOperationEvaluation> Evaluations,
        string RejectionCode,
        long DependencyClosurePlanCount,
        IReadOnlyDictionary<long, IReadOnlyList<ActionPlan>> LaneOrderings = null,
        IReadOnlyDictionary<long, MovementPathProjection> PathProjections = null)
    {
        public bool Succeeded => RejectionCode == null;

        public static ScheduleEvaluationResult Rejected(string code)
            => new ScheduleEvaluationResult(Array.Empty<ScheduleOperationEvaluation>(), code, 0L);

        /// <summary>求值后的 Lane 规范顺序（提交时整体替换投影的唯一来源）。</summary>
        public IReadOnlyList<ActionPlan> OrderingOf(UnitId unitId)
            => LaneOrderings != null && LaneOrderings.TryGetValue(unitId.Value, out IReadOnlyList<ActionPlan> ordered)
                ? ordered
                : Array.Empty<ActionPlan>();

        /// <summary>
        /// 本次求值是否<strong>覆盖</strong>了该 Lane。
        /// 提交点只允许替换被覆盖的 Lane：未被求值的 Lane 必须原样保留，
        /// 否则"整体替换投影"会把不相干 Lane 的计划清空。
        /// </summary>
        public bool CoversLane(UnitId unitId)
            => LaneOrderings != null && LaneOrderings.ContainsKey(unitId.Value);

        /// <summary>某计划本次求值重算出的路径投影（没有则返回 null）。</summary>
        public MovementPathProjection PathProjectionOf(ActionPlanId actionPlanId)
            => PathProjections != null && PathProjections.TryGetValue(actionPlanId.Value, out MovementPathProjection p)
                ? p
                : null;
    }

    /// <summary>
    /// <strong>权威排程求值器</strong>（任务包「必须产出」6、7 与「核心规则」最后一段）。
    ///
    /// 它是唯一的规范化实现：显式排程事务、系统自动延期、预览与位置依赖闭包查询
    /// <strong>全部</strong>调用它。它只做两件事：
    /// <list type="number">
    /// <item>把直接操作的起点放到请求位置（<strong>只有直接移动的计划可以早于旧投影</strong>）；</item>
    /// <item>从当前 Lane 投影开始<strong>只向右</strong> ripple 重叠。Locked/Running/终态与固定反应区间
    /// 是<strong>不可变障碍</strong>，永不移动、永不被推挤。</item>
    /// </list>
    ///
    /// 它<strong>不</strong>：分配 ID、改 <c>ScheduleRevision</c>、写注册表、写 Lane、
    /// 决定预算或 Reservation——那些是 <c>ScheduleEditor</c> 事务、启动门禁与系统延期事务的职责。
    /// 它<strong>不</strong>做"删除后向左压缩"：删除只移除目标并重算必要的空间依赖。
    ///
    /// 半开区间语义：区间为 <c>[StartTick, EndTick)</c>，相邻不算重叠。
    /// </summary>
    public sealed class ScheduleEvaluator
    {
        private readonly ActionScheduleAuthority _authority;

        public ScheduleEvaluator(ActionScheduleAuthority authority, IMovementPathCalculator pathCalculator = null)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            PathCalculator = pathCalculator ?? NotImplementedMovementPathCalculator.Instance;
        }

        public ActionScheduleAuthority Authority => _authority;

        public ScheduleLimits Limits => _authority.Limits;

        /// <summary>移动链路径重算端口（任务 06 注入；默认 fail-closed 实现见类型文档）。</summary>
        public IMovementPathCalculator PathCalculator { get; }

        /// <summary>
        /// 规范化求值（<strong>只读</strong>：不注册计划、不改修订号、不写 Lane 投影）。
        ///
        /// <paramref name="affectedLaneUnits"/> 是事务已知会受影响的额外 Lane（通常是各操作的
        /// 所有者单位；候选计划的 Lane 会从意图里自动补全）。
        /// </summary>
        public ScheduleEvaluationResult Evaluate(
            IReadOnlyList<ScheduleOperationIntent> intents, IReadOnlyList<UnitId> affectedLaneUnits)
        {
            if (intents == null) intents = Array.Empty<ScheduleOperationIntent>();

            // 空意图只有在"确实有受影响 Lane"时才是合法的：Remove-only 事务没有任何意图，
            // 但仍必须把被删计划从 Lane 投影里去掉，并证明其余计划<strong>一个都不左吸</strong>。
            if (intents.Count == 0 && (affectedLaneUnits == null || affectedLaneUnits.Count == 0))
                return ScheduleEvaluationResult.Rejected(ScheduleCodes.SCHEDULE_OPERATION_INVALID);

            var directPlanIds = new HashSet<long>();
            // "定点"（pinned）= 被直接移动的已有计划 + 本批新增的候选计划。
            // 候选计划没有"旧投影"可以比较，它的请求位点就是它的偏好位点；
            // 而已有计划只有被<strong>直接</strong>移动时才可以早于旧投影。
            var pinnedPlanIds = new HashSet<long>();
            var desired = new Dictionary<long, long>();
            for (int i = 0; i < intents.Count; i++)
            {
                ScheduleOperationIntent intent = intents[i];
                if (intent == null || intent.Plan == null)
                    return ScheduleEvaluationResult.Rejected(ScheduleCodes.SCHEDULE_OPERATION_INVALID);
                if (intent.IsDirectMove) directPlanIds.Add(intent.Plan.ActionPlanId.Value);
                if (intent.IsDirectMove || !_authority.Registry.Contains(intent.Plan.ActionPlanId))
                    pinnedPlanIds.Add(intent.Plan.ActionPlanId.Value);
                desired[intent.Plan.ActionPlanId.Value] = intent.DesiredStartTick < 0L ? 0L : intent.DesiredStartTick;
            }

            var lanes = new List<UnitId>();
            var seenLanes = new HashSet<long>();
            CollectLanes(affectedLaneUnits, lanes, seenLanes);
            for (int i = 0; i < intents.Count; i++) CollectLane(intents[i].Plan.OwnerUnitId, lanes, seenLanes);

            // 本批候选计划（尚未注册）：它们参与求值，但不写任何权威状态。
            var addedByLane = new Dictionary<long, List<ActionPlan>>();
            for (int i = 0; i < intents.Count; i++)
            {
                ActionPlan plan = intents[i].Plan;
                if (_authority.Registry.Contains(plan.ActionPlanId)) continue;
                if (!addedByLane.TryGetValue(plan.OwnerUnitId.Value, out List<ActionPlan> list))
                {
                    list = new List<ActionPlan>();
                    addedByLane[plan.OwnerUnitId.Value] = list;
                }
                list.Add(plan);
            }

            // 求值前的投影（依赖闭包的判定基准）：每条受影响 Lane 的计划副本（含候选）+ 起点/终点。
            var workingByLane = new Dictionary<long, List<ActionPlan>>();
            var previousStart = new Dictionary<long, long>();
            var previousEnd = new Dictionary<long, long>();
            for (int i = 0; i < lanes.Count; i++)
            {
                UnitId unitId = lanes[i];
                var working = new List<ActionPlan>();
                ActorLane lane = _authority.FindLane(unitId);
                if (lane != null) working.AddRange(lane.Plans);
                if (addedByLane.TryGetValue(unitId.Value, out List<ActionPlan> extra)) working.AddRange(extra);
                working.Sort(ActorLane.Compare);
                workingByLane[unitId.Value] = working;
                for (int p = 0; p < working.Count; p++)
                {
                    previousStart[working[p].ActionPlanId.Value] = working[p].StartTick;
                    previousEnd[working[p].ActionPlanId.Value] = working[p].EndTick;
                }
            }

            // —— 1. 依赖闭包：位置依赖的传递闭包 + 显式锚点，跨过非移动计划 ——
            var affected = new HashSet<long>();
            for (int i = 0; i < intents.Count; i++)
            {
                ActionPlan plan = intents[i].Plan;
                if (!plan.IsOrdinary) continue;     // 固定反应区间不参与普通依赖闭包
                affected.Add(plan.ActionPlanId.Value);
            }

            string closureError = ExpandPositionDependencyClosure(
                lanes, workingByLane, previousStart, previousEnd, affected, out long closureCount);
            if (closureError != null) return ScheduleEvaluationResult.Rejected(closureError);

            // —— 2. 逐 Lane 求值（唯一的"只向右 ripple"实现）——
            //
            // "求值前就落在某个<strong>已位移</strong>计划执行期内"的计划必须留在那个计划之后：
            // 这个依赖下界是<strong>传递</strong>的，因此逐轮抬高直到不再变化。
            // 下界只增不减，且每轮至多抬高有限个计划，所以必然收敛。
            var dependencyFloor = new Dictionary<long, long>();
            var finalStart = new Dictionary<long, long>();
            var finalEnd = new Dictionary<long, long>();
            var laneOrderings = new Dictionary<long, IReadOnlyList<ActionPlan>>();

            int maxPasses = affected.Count + 2;
            for (int pass = 0; pass < maxPasses; pass++)
            {
                finalStart.Clear();
                finalEnd.Clear();
                laneOrderings.Clear();

                for (int i = 0; i < lanes.Count; i++)
                {
                    UnitId unitId = lanes[i];
                    string laneError = EvaluateLane(
                        workingByLane[unitId.Value], desired, pinnedPlanIds, dependencyFloor,
                        previousStart, finalStart, finalEnd, out List<ActionPlan> ordering);
                    if (laneError != null) return ScheduleEvaluationResult.Rejected(laneError);
                    laneOrderings[unitId.Value] = ordering;
                }

                if (!RaiseDependencyFloors(lanes, workingByLane, previousStart, previousEnd,
                        dependencyFloor, finalStart, finalEnd))
                {
                    break;
                }
            }

            // —— 3. 提交集 = 依赖闭包 ∪ 本次求值<strong>实际位移</strong>的计划 ——
            //
            // 只按闭包提交是不够的：把计划 A 放到某处可能新压到<strong>原本不依赖 A</strong> 的计划 B，
            // B 被向右避让后必须一起写入，否则 Lane 投影与实际位置分叉。
            for (int i = 0; i < lanes.Count; i++)
            {
                List<ActionPlan> lanePlans = workingByLane[lanes[i].Value];
                for (int p = 0; p < lanePlans.Count; p++)
                {
                    long planIdValue = lanePlans[p].ActionPlanId.Value;
                    if (!finalStart.TryGetValue(planIdValue, out long resolved)) continue;
                    previousStart.TryGetValue(planIdValue, out long before);
                    if (resolved != before) affected.Add(planIdValue);
                }
            }

            if (affected.Count > Limits.MaxDependencyClosurePlans)
                return ScheduleEvaluationResult.Rejected(ScheduleCodes.SCHEDULE_DEPENDENCY_CLOSURE_TOO_LARGE);
            closureCount = affected.Count;

            // —— 4. 移动链路径投影重算（任务 06 的 1/2 权重 Pathfinder 接入面）——
            // 位置变化会改变 Move 的路径、边数与路径权重，而"预算与 EndTick 必须使用路径权重"，
            // 因此重算发生在求值内：结果进入 evaluations 的 NewEndTick，并由提交点整批写入。
            var pathProjections = new Dictionary<long, MovementPathProjection>();
            for (int i = 0; i < lanes.Count; i++)
            {
                List<ActionPlan> lanePlans = workingByLane[lanes[i].Value];
                for (int p = 0; p < lanePlans.Count; p++)
                {
                    ActionPlan plan = lanePlans[p];
                    long planIdValue = plan.ActionPlanId.Value;
                    if (!affected.Contains(planIdValue)) continue;
                    if (!plan.IsOrdinaryMove) continue;
                    if (!finalStart.TryGetValue(planIdValue, out long newStart)) continue;
                    previousStart.TryGetValue(planIdValue, out long oldStart);
                    MovementPathProjection projection = PathCalculator.Recompute(plan, newStart, newStart - oldStart);
                    if (projection == null) continue;
                    pathProjections[planIdValue] = projection;
                    finalEnd[planIdValue] = newStart +
                        (projection.WeightUnits * plan.ResolvedBaseStepTicks) + plan.RecoveryTicks;
                }
            }

            // —— 5. 规范结果（按 ActionPlanId 升序，稳定且与输入顺序无关）——
            var evaluations = new List<ScheduleOperationEvaluation>(affected.Count);
            for (int i = 0; i < lanes.Count; i++)
            {
                List<ActionPlan> lanePlans = workingByLane[lanes[i].Value];
                for (int p = 0; p < lanePlans.Count; p++)
                {
                    ActionPlan plan = lanePlans[p];
                    long planIdValue = plan.ActionPlanId.Value;
                    if (!affected.Contains(planIdValue)) continue;
                    if (!finalStart.TryGetValue(planIdValue, out long newStart)) continue;
                    finalEnd.TryGetValue(planIdValue, out long newEnd);
                    previousStart.TryGetValue(planIdValue, out long oldStart);
                    evaluations.Add(new ScheduleOperationEvaluation(
                        plan.ActionPlanId, plan.OwnerUnitId, oldStart, newStart, newEnd,
                        directPlanIds.Contains(planIdValue)));
                }
            }
            evaluations.Sort((a, b) => a.PlanId.Value.CompareTo(b.PlanId.Value));

            return new ScheduleEvaluationResult(evaluations, null, closureCount, laneOrderings, pathProjections);
        }

        /// <summary>
        /// 只读位置依赖闭包查询（任务包「Dodge 对后续移动的终态接缝」的共用实现）。
        /// 返回<strong>实际 From 变化会失效</strong>的后续计划（按 <c>ActionPlanId</c> 升序）。
        /// 它读取<strong>当前</strong>排程，因此覆盖接受 Dodge 之后新增/编辑的移动。
        /// </summary>
        public IReadOnlyList<ScheduleOperationEvaluation> QueryPositionDependencyClosure(
            IReadOnlyList<ActionPlan> seeds, bool movementOnly)
        {
            var result = new List<ScheduleOperationEvaluation>();
            if (seeds == null || seeds.Count == 0) return result;

            var lanes = new List<UnitId>();
            var seenLanes = new HashSet<long>();
            var projections = new Dictionary<long, List<ActionPlan>>();
            var previousStart = new Dictionary<long, long>();
            var previousEnd = new Dictionary<long, long>();

            for (int i = 0; i < seeds.Count; i++)
            {
                if (seeds[i] == null) continue;
                CollectLane(seeds[i].OwnerUnitId, lanes, seenLanes);
            }

            for (int i = 0; i < lanes.Count; i++)
            {
                UnitId unitId = lanes[i];
                ActorLane lane = _authority.FindLane(unitId);
                var working = lane == null ? new List<ActionPlan>() : new List<ActionPlan>(lane.Plans);
                working.Sort(ActorLane.Compare);
                projections[unitId.Value] = working;
                for (int p = 0; p < working.Count; p++)
                {
                    previousStart[working[p].ActionPlanId.Value] = working[p].StartTick;
                    previousEnd[working[p].ActionPlanId.Value] = working[p].EndTick;
                }
            }

            var affected = new HashSet<long>();
            for (int i = 0; i < seeds.Count; i++)
            {
                if (seeds[i] == null) continue;
                affected.Add(seeds[i].ActionPlanId.Value);
            }

            ExpandPositionDependencyClosure(lanes, projections, previousStart, previousEnd, affected, out _);

            for (int i = 0; i < seeds.Count; i++)
            {
                if (seeds[i] != null) affected.Remove(seeds[i].ActionPlanId.Value);
            }

            for (int i = 0; i < lanes.Count; i++)
            {
                List<ActionPlan> lanePlans = projections[lanes[i].Value];
                for (int p = 0; p < lanePlans.Count; p++)
                {
                    ActionPlan plan = lanePlans[p];
                    if (!affected.Contains(plan.ActionPlanId.Value)) continue;
                    if (plan.IsReaction) continue;
                    if (movementOnly && !plan.IsMovementFamily) continue;
                    previousStart.TryGetValue(plan.ActionPlanId.Value, out long start);
                    previousEnd.TryGetValue(plan.ActionPlanId.Value, out long end);
                    result.Add(new ScheduleOperationEvaluation(
                        plan.ActionPlanId, plan.OwnerUnitId, start, start, end, false));
                }
            }

            result.Sort((a, b) => a.PlanId.Value.CompareTo(b.PlanId.Value));
            return result;
        }

        // ————————————————————————————————————————————————————————————
        // 单 Lane 求值：唯一的"只向右 ripple"实现
        // ————————————————————————————————————————————————————————————

        private string EvaluateLane(
            List<ActionPlan> working,
            Dictionary<long, long> desired,
            HashSet<long> pinnedPlanIds,
            Dictionary<long, long> dependencyFloor,
            Dictionary<long, long> previousStart,
            Dictionary<long, long> finalStart,
            Dictionary<long, long> finalEnd,
            out List<ActionPlan> ordering)
        {
            ordering = null;

            for (int i = 0; i < working.Count; i++)
            {
                long id = working[i].ActionPlanId.Value;
                if (!previousStart.ContainsKey(id)) previousStart[id] = working[i].StartTick;
            }

            // —— 分层 ——
            //   障碍：Locked/Running/终态与固定反应区间（不可变，永不移动、永不被压）
            //   定点：被<strong>直接移动</strong>的计划（唯一可以早于旧投影的计划）
            //   可动：其余 Editable 普通计划（只向右避让，取"不下于自身当前起点"的最早可行位点）
            var obstacles = new List<ActionPlan>();
            var pins = new List<ActionPlan>();
            var movables = new List<ActionPlan>();
            for (int i = 0; i < working.Count; i++)
            {
                ActionPlan plan = working[i];
                if (!plan.IsEditable) obstacles.Add(plan);
                else if (pinnedPlanIds.Contains(plan.ActionPlanId.Value)) pins.Add(plan);
                else movables.Add(plan);
            }

            // 障碍彼此重叠是 Lane 结构矛盾：显式失败，绝不静默挪动不可变区间。
            for (int i = 0; i < obstacles.Count; i++)
            {
                ActionPlan left = obstacles[i];
                long leftStart = left.StartTick;
                long leftEnd = leftStart + left.IntervalLength;
                for (int j = i + 1; j < obstacles.Count; j++)
                {
                    ActionPlan right = obstacles[j];
                    long rightStart = right.StartTick;
                    long rightEnd = rightStart + right.IntervalLength;
                    if (leftStart < rightEnd && rightStart < leftEnd)
                        return ScheduleCodes.SCHEDULE_LANE_OVERLAP;
                }
            }

            var placed = new List<ActionPlan>();

            // 最早可行位点：从 candidate 起向右跳过全部已占位区间（半开区间，相邻不算重叠）。
            long EarliestFeasible(long candidate, ActionPlan plan)
            {
                if (candidate < 0L) candidate = 0L;
                int guard = 0;
                bool advanced = true;
                while (advanced)
                {
                    advanced = false;
                    if (++guard > placed.Count + 2) break;
                    for (int i = 0; i < placed.Count; i++)
                    {
                        ActionPlan other = placed[i];
                        long otherStart = finalStart[other.ActionPlanId.Value];
                        long otherEnd = otherStart + other.IntervalLength;
                        if (candidate >= otherEnd || otherStart >= candidate + plan.IntervalLength) continue;
                        candidate = otherEnd;
                        advanced = true;
                    }
                }
                return candidate;
            }

            for (int i = 0; i < obstacles.Count; i++)
            {
                ActionPlan plan = obstacles[i];
                finalStart[plan.ActionPlanId.Value] = plan.StartTick;
                finalEnd[plan.ActionPlanId.Value] = plan.StartTick + plan.IntervalLength;
                placed.Add(plan);
            }

            pins.Sort((a, b) =>
            {
                long pa = desired.TryGetValue(a.ActionPlanId.Value, out long wa) ? wa : a.StartTick;
                long pb = desired.TryGetValue(b.ActionPlanId.Value, out long wb) ? wb : b.StartTick;
                int byStart = pa.CompareTo(pb);
                return byStart != 0 ? byStart : a.ActionPlanId.Value.CompareTo(b.ActionPlanId.Value);
            });
            for (int i = 0; i < pins.Count; i++)
            {
                ActionPlan plan = pins[i];
                long requested = desired.TryGetValue(plan.ActionPlanId.Value, out long wished) ? wished : plan.StartTick;
                long resolved = EarliestFeasible(requested, plan);
                finalStart[plan.ActionPlanId.Value] = resolved;
                finalEnd[plan.ActionPlanId.Value] = resolved + plan.IntervalLength;
                placed.Add(plan);
            }

            movables.Sort(ActorLane.Compare);
            long cursor = 0L;
            for (int i = 0; i < movables.Count; i++)
            {
                ActionPlan plan = movables[i];
                long candidate = plan.StartTick;
                if (dependencyFloor.TryGetValue(plan.ActionPlanId.Value, out long floor) && floor > candidate)
                    candidate = floor;
                if (cursor > candidate) candidate = cursor;

                long resolved = EarliestFeasible(candidate, plan);
                finalStart[plan.ActionPlanId.Value] = resolved;
                finalEnd[plan.ActionPlanId.Value] = resolved + plan.IntervalLength;
                placed.Add(plan);
                if (finalEnd[plan.ActionPlanId.Value] > cursor) cursor = finalEnd[plan.ActionPlanId.Value];
            }

            // 只有直接移动的计划会左移，因此新起点顺序可能与旧顺序不同：按新起点重新规范排序。
            var resorted = new List<ActionPlan>(working);
            resorted.Sort((a, b) =>
            {
                int byStart = finalStart[a.ActionPlanId.Value].CompareTo(finalStart[b.ActionPlanId.Value]);
                return byStart != 0 ? byStart : a.ActionPlanId.Value.CompareTo(b.ActionPlanId.Value);
            });

            ordering = resorted;
            return null;
        }

        /// <summary>
        /// 抬高"依赖下界"：若某计划在<strong>求值前</strong>落在另一个<strong>已位移</strong>计划的执行期内，
        /// 它就必须留在那个计划的新终点之后（位置依赖的传递闭包）。
        /// 返回是否发生了新的抬高（调用方据此决定是否再求值一轮）。
        /// </summary>
        private static bool RaiseDependencyFloors(
            List<UnitId> lanes,
            Dictionary<long, List<ActionPlan>> workingByLane,
            Dictionary<long, long> previousStart,
            Dictionary<long, long> previousEnd,
            Dictionary<long, long> dependencyFloor,
            Dictionary<long, long> finalStart,
            Dictionary<long, long> finalEnd)
        {
            bool raised = false;

            for (int i = 0; i < lanes.Count; i++)
            {
                List<ActionPlan> lanePlans = workingByLane[lanes[i].Value];
                for (int p = 0; p < lanePlans.Count; p++)
                {
                    ActionPlan candidate = lanePlans[p];
                    long candidateId = candidate.ActionPlanId.Value;
                    if (!candidate.IsOrdinary) continue;
                    if (!previousStart.TryGetValue(candidateId, out long candidateOldStart)) continue;

                    long floor = dependencyFloor.TryGetValue(candidateId, out long current) ? current : 0L;

                    for (int q = 0; q < lanePlans.Count; q++)
                    {
                        ActionPlan other = lanePlans[q];
                        long otherId = other.ActionPlanId.Value;
                        if (otherId == candidateId) continue;
                        if (!previousStart.TryGetValue(otherId, out long otherOldStart)) continue;
                        if (!previousEnd.TryGetValue(otherId, out long otherOldEnd)) continue;
                        if (!finalStart.TryGetValue(otherId, out long otherNewStart)) continue;
                        if (otherNewStart == otherOldStart) continue;    // 没有位移 ⇒ 不改变下界

                        if (candidateOldStart < otherOldStart || candidateOldStart >= otherOldEnd) continue;
                        if (!finalEnd.TryGetValue(otherId, out long otherNewEnd)) continue;
                        if (otherNewEnd > floor) floor = otherNewEnd;
                    }

                    if (floor <= current) continue;
                    dependencyFloor[candidateId] = floor;
                    raised = true;
                }
            }

            return raised;
        }

        // ————————————————————————————————————————————————————————————
        // 位置依赖闭包：传递闭包，跨过非移动计划
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 位置依赖判定（冻结语义）：计划 <c>P</c> 依赖计划 <c>Q</c>，当且仅当
        /// <c>P.StartTick</c> 落在 <c>Q</c> 求值前的投影区间 <c>[Q.StartTick, Q.EndTick)</c> 内，
        /// 或 <c>P</c> 被显式锚定在 <c>Q</c> 之后。依赖关系<strong>传递</strong>，
        /// 且<strong>跨过 Attack/Guard 等非移动计划</strong>——不以"紧邻下一项"代替依赖关系。
        /// </summary>
        private string ExpandPositionDependencyClosure(
            List<UnitId> lanes,
            Dictionary<long, List<ActionPlan>> projections,
            Dictionary<long, long> previousStart,
            Dictionary<long, long> previousEnd,
            HashSet<long> affected,
            out long closureCount)
        {
            int cap = Limits.MaxDependencyClosurePlans;
            int guard = 0;
            closureCount = affected.Count;

            for (int laneIndex = 0; laneIndex < lanes.Count; laneIndex++)
            {
                if (!projections.TryGetValue(lanes[laneIndex].Value, out List<ActionPlan> lanePlans)) continue;
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    if (++guard > 1 << 20)
                    {
                        closureCount = affected.Count;
                        return ScheduleCodes.SCHEDULE_DEPENDENCY_CLOSURE_TOO_LARGE;
                    }

                    for (int i = 0; i < lanePlans.Count; i++)
                    {
                        ActionPlan candidate = lanePlans[i];
                        if (!candidate.IsOrdinary) continue;
                        if (affected.Contains(candidate.ActionPlanId.Value)) continue;
                        if (!DependsOnAffected(candidate, lanePlans, affected, previousStart, previousEnd)) continue;
                        affected.Add(candidate.ActionPlanId.Value);
                        changed = true;
                    }
                }

                if (affected.Count > cap)
                {
                    closureCount = affected.Count;
                    return ScheduleCodes.SCHEDULE_DEPENDENCY_CLOSURE_TOO_LARGE;
                }
            }

            closureCount = affected.Count;
            return closureCount > cap ? ScheduleCodes.SCHEDULE_DEPENDENCY_CLOSURE_TOO_LARGE : null;
        }

        private static bool DependsOnAffected(
            ActionPlan candidate,
            List<ActionPlan> lanePlans,
            HashSet<long> affected,
            Dictionary<long, long> previousStart,
            Dictionary<long, long> previousEnd)
        {
            long candidateStart = candidate.StartTick;
            if (previousStart.TryGetValue(candidate.ActionPlanId.Value, out long recorded)) candidateStart = recorded;

            for (int i = 0; i < lanePlans.Count; i++)
            {
                ActionPlan other = lanePlans[i];
                if (other.ActionPlanId == candidate.ActionPlanId) continue;
                if (!affected.Contains(other.ActionPlanId.Value)) continue;

                if (!previousStart.TryGetValue(other.ActionPlanId.Value, out long otherStart)) otherStart = other.StartTick;
                if (!previousEnd.TryGetValue(other.ActionPlanId.Value, out long otherEnd)) otherEnd = other.EndTick;

                // 位置依赖：候选起点落在受影响计划求值前的投影区间内（半开区间）。
                if (candidateStart >= otherStart && candidateStart < otherEnd) return true;
            }

            return false;
        }

        // ————————————————————————————————————————————————————————————
        // 辅助
        // ————————————————————————————————————————————————————————————

        private static void CollectLanes(IReadOnlyList<UnitId> units, List<UnitId> lanes, HashSet<long> seen)
        {
            if (units == null) return;
            for (int i = 0; i < units.Count; i++) CollectLane(units[i], lanes, seen);
        }

        private static void CollectLane(UnitId unitId, List<UnitId> lanes, HashSet<long> seen)
        {
            if (!unitId.IsValid) return;
            if (!seen.Add(unitId.Value)) return;
            lanes.Add(unitId);
            lanes.Sort((a, b) => a.Value.CompareTo(b.Value));
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public static string Describe(ScheduleEvaluationResult result)
        {
            if (result == null) return "<null>";
            if (!result.Succeeded) return "rejected(" + result.RejectionCode + ")";
            return "evaluated=" + result.Evaluations.Count.ToString(CultureInfo.InvariantCulture) +
                   " closure=" + result.DependencyClosurePlanCount.ToString(CultureInfo.InvariantCulture);
        }
    }
}
