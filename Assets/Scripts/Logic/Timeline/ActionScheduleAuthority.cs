using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// 全局动作排程权威（<strong>新增与编辑的唯一动作队列权威来源</strong>）。
    ///
    /// 它把任务包「必须产出」2 与 3 组合成一个单一的、可快照的权威对象：
    /// <list type="bullet">
    /// <item><see cref="Registry"/> 是唯一的全局计划注册表（按 <c>ActionPlanId</c> 稳定访问）；</item>
    /// <item><see cref="Lanes"/> 是每单位一条的 <c>ActorLane</c>，按 <c>UnitId</c> 升序稳定枚举；</item>
    /// <item><see cref="ScheduleRevision"/> 是全局乐观并发版本。</item>
    /// </list>
    ///
    /// <strong>没有按 <c>WindowId</c> 分桶的入口</strong>：窗口切换不会遍历、结算或移动任何计划。
    /// 修订号规则（任务包「核心规则」）：
    /// <list type="bullet">
    /// <item>每个成功的命令排程事务或成功的系统自动延期事务<strong>恰好 +1</strong>；</item>
    /// <item>预览、任意失败、锁定、自然推进与终态都<strong>不</strong>增加修订号。</item>
    /// </list>
    ///
    /// 写入口是 <c>internal</c>：只有同一程序集内的 <c>ScheduleEditor</c> 事务、
    /// 启动门禁的原子提交与统一终态协调器调用。
    /// </summary>
    public sealed class ActionScheduleAuthority
    {
        private readonly ActionPlanRegistry _registry;
        private readonly Dictionary<long, ActorLane> _lanesByUnit = new Dictionary<long, ActorLane>();
        private readonly List<ActorLane> _lanes = new List<ActorLane>();
        private readonly List<ActionPlanId> _lastTerminalOrder = new List<ActionPlanId>();

        public ActionScheduleAuthority(ActionPlanRegistry registry = null, ScheduleLimits limits = null)
        {
            _registry = registry ?? new ActionPlanRegistry();
            Limits = limits ?? ScheduleLimits.FrozenV1;
        }

        public ActionPlanRegistry Registry => _registry;

        public ScheduleLimits Limits { get; }

        /// <summary>全局排程修订号（成功事务各 +1；预览与失败不变）。</summary>
        public long ScheduleRevision { get; private set; }

        /// <summary>
        /// 下一个反应机会 ID 的<strong>只读镜像</strong>，由构造时注入的 <see cref="LogicIdGenerator"/>
        /// 提供（<c>IdGenerator.NextReactionOpportunityIdValue</c>）。
        ///
        /// <strong>本属性不再持有计数状态</strong>（任务 05 收尾 R1 / 缺陷 D2）：任务 05 早期版本
        /// 让本权威自持 <c>NextReactionOptionSequence</c> 计数器并自增，同时任务 03 冻结契约的
        /// <c>LogicIdGenerator.NextReactionOpportunityId()</c> 仍然存活但零调用——两个分配器并存，
        /// 后续任务按契约调用会从 1 重发而与已发出 ID 冲突。
        /// 现在 <c>ReactionOpportunityId</c> 的唯一分配器是 <see cref="LogicIdGenerator"/>，
        /// 本属性只是它的只读投影，<strong>无法</strong>自行取号，因此两条路径不可能各自取号。
        /// 快照字段 <c>NextReactionOpportunityId</c> 与 <c>NextReactionOptionSequence</c> 均由同一
        /// 唯一来源喂给（两者恒等由构造保证）。
        /// </summary>
        public long NextReactionOptionSequence
            => IdGenerator == null ? 1L : IdGenerator.NextReactionOpportunityIdValue;

        /// <summary>
        /// 反应机会 ID 的<strong>唯一</strong>分配器（任务 03 契约的
        /// <c>LogicIdGenerator</c>）。机会系统与快照都经它取值；本权威不保留第二份计数状态。
        /// </summary>
        public LogicIdGenerator IdGenerator { get; set; }

        /// <summary>按 <c>UnitId</c> 升序的全部 Lane（规范枚举）。</summary>
        public IReadOnlyList<ActorLane> Lanes => _lanes;

        /// <summary>最近一次终态事务里按稳定键处理的 <c>ActionPlanId</c> 顺序（审计）。</summary>
        public IReadOnlyList<ActionPlanId> LastTerminalOrder => _lastTerminalOrder;

        /// <summary>按 <c>UnitId</c> 查询 Lane；未创建返回 null。</summary>
        public ActorLane FindLane(UnitId unitId)
            => _lanesByUnit.TryGetValue(unitId.Value, out ActorLane lane) ? lane : null;

        /// <summary>按 <c>UnitId</c> 取（必要时创建）Lane。</summary>
        public ActorLane GetOrCreateLane(UnitId unitId)
        {
            if (!unitId.IsValid)
                throw new LogicDefinitionException(ScheduleCodes.SCHEDULE_LANE_UNKNOWN_UNIT, "unitId=0");
            if (_lanesByUnit.TryGetValue(unitId.Value, out ActorLane existing)) return existing;

            var lane = new ActorLane(unitId);
            _lanesByUnit[unitId.Value] = lane;
            _lanes.Add(lane);
            _lanes.Sort((a, b) => a.UnitId.Value.CompareTo(b.UnitId.Value));
            return lane;
        }

        /// <summary>计划所属 Lane（不在任何 Lane 时返回 null）。</summary>
        public ActorLane LaneOfPlan(ActionPlanId actionPlanId)
        {
            for (int i = 0; i < _lanes.Count; i++)
            {
                if (_lanes[i].Contains(actionPlanId)) return _lanes[i];
            }
            return null;
        }

        /// <summary>定位计划：先查注册表（含终态），再查 Lane。</summary>
        public bool TryLocate(ActionPlanId actionPlanId, out ActionPlan plan, out ActorLane lane)
        {
            plan = _registry.Find(actionPlanId);
            lane = plan == null ? null : LaneOfPlan(actionPlanId);
            return plan != null;
        }

        // —— 修订号（只有事务与自动延期能推进）——

        internal void IncrementRevision() => ScheduleRevision++;

        internal void SetRevision(long value) => ScheduleRevision = value < 0L ? 0L : value;

        /// <summary>是否已经注入权威阵营关系解析器（快照 <c>PrimaryTargetRelation</c> 的前提）。</summary>
        public bool HasFactionResolver => Factions != null;

        // —— 计划注册（事务与反应规划器共用）——

        /// <summary>
        /// 把一个新计划同时写入全局注册表与它所属的 Lane。
        /// 调用方必须先完成全部校验（否则会留下部分写入）。
        /// </summary>
        internal void RegisterPlan(ActionPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            _registry.Register(plan);
            GetOrCreateLane(plan.OwnerUnitId).InsertCanonical(plan);
        }

        /// <summary>
        /// 把一个新计划写入注册表，但<strong>不</strong>放入 Lane。
        /// 只用于反应计划：它的固定区间必须先通过 Lane 重叠检查，
        /// 检查通过后由 <see cref="AttachToLane"/> 补上。
        /// </summary>
        internal void RegisterPlanWithoutLane(ActionPlan plan) => _registry.Register(plan);

        internal void AttachToLane(ActionPlan plan) => GetOrCreateLane(plan.OwnerUnitId).InsertCanonical(plan);

        internal void ReplaceLaneProjection(UnitId unitId, IReadOnlyList<ActionPlan> ordered)
            => GetOrCreateLane(unitId).ReplaceProjection(ordered);

        internal bool RemoveFromLane(UnitId unitId, ActionPlanId actionPlanId)
        {
            ActorLane lane = FindLane(unitId);
            return lane != null && lane.RemovePlan(actionPlanId);
        }

        // —— 终态（统一协调器是唯一调用者）——

        internal void MarkPlanTerminal(ActionPlan plan, long terminalTick)
        {
            _registry.MarkTerminal(plan, terminalTick);
            RemoveFromLane(plan.OwnerUnitId, plan.ActionPlanId);
        }

        internal void BeginTerminalBatch() => _lastTerminalOrder.Clear();

        internal void RecordTerminalOrder(ActionPlanId actionPlanId) => _lastTerminalOrder.Add(actionPlanId);

        /// <summary>锁定单个单位的新提交（死亡锁 Lane）。不清除已有计划。</summary>
        internal void LockLaneSubmissions(UnitId unitId, string reason)
            => GetOrCreateLane(unitId).LockSubmissions(reason);

        /// <summary>锁定全部 Lane 的新提交（战斗结束 Finalizer）。不清除已有计划。</summary>
        internal void LockAllLaneSubmissions(string reason)
        {
            for (int i = 0; i < _lanes.Count; i++) _lanes[i].LockSubmissions(reason);
        }

        /// <summary>
        /// 按 <c>UnitId -&gt; ActionPlanId</c> 稳定顺序枚举某单位的全部非终态计划。
        /// 它是死亡清理与战斗结束清理的<strong>唯一</strong>枚举入口。
        /// </summary>
        public List<ActionPlan> CollectNonTerminalPlansOfUnit(UnitId unitId)
        {
            ActorLane lane = FindLane(unitId);
            var plans = lane == null ? new List<ActionPlan>() : new List<ActionPlan>(lane.Plans);
            plans.Sort((a, b) => a.ActionPlanId.Value.CompareTo(b.ActionPlanId.Value));
            var result = new List<ActionPlan>();
            for (int i = 0; i < plans.Count; i++)
            {
                if (!plans[i].IsTerminal) result.Add(plans[i]);
            }
            return result;
        }

        /// <summary>
        /// 按 <c>UnitId -&gt; ActionPlanId</c> 稳定顺序枚举全部非终态计划
        /// （战斗结束 Finalizer 的唯一枚举入口）。
        /// </summary>
        public List<ActionPlan> CollectAllNonTerminalPlansOrdered()
        {
            var all = new List<ActionPlan>();
            for (int i = 0; i < _lanes.Count; i++)
            {
                IReadOnlyList<ActionPlan> plans = _lanes[i].Plans;
                for (int p = 0; p < plans.Count; p++)
                {
                    if (!plans[p].IsTerminal) all.Add(plans[p]);
                }
            }
            all.Sort((a, b) =>
            {
                int byUnit = a.OwnerUnitId.Value.CompareTo(b.OwnerUnitId.Value);
                return byUnit != 0 ? byUnit : a.ActionPlanId.Value.CompareTo(b.ActionPlanId.Value);
            });
            return all;
        }

        // —— 快照与不变量 ——

        /// <summary>活动计划（非终态）的规范化快照（按 <c>ActionPlanId</c> 升序）。</summary>
        public IReadOnlyList<ActionPlanSnapshot> BuildPlanSnapshots()
        {
            if (_registry.ActiveCount == 0) return Array.Empty<ActionPlanSnapshot>();
            IReadOnlyList<ActionPlan> active = _registry.ActivePlans;
            var snapshots = new List<ActionPlanSnapshot>(active.Count);
            for (int i = 0; i < active.Count; i++) snapshots.Add(ActionPlanSnapshot.From(active[i], Factions));
            return snapshots;
        }

        /// <summary>
        /// 权威 <c>IFactionRelationResolver</c>：快照里 <c>PrimaryTargetRelation</c> 的唯一来源。
        /// 由装配方（<c>BattleSimulation</c>）在创建后立即注入；为 null 时该字段退化为 0（Self），
        /// 因此生产路径**必须**注入。
        /// </summary>
        public IFactionRelationResolver Factions { get; set; }

        /// <summary>全部 Lane 的规范化快照（按 <c>UnitId</c> 升序）。</summary>
        public IReadOnlyList<ActorLaneSnapshot> BuildLaneSnapshots()
        {
            var snapshots = new List<ActorLaneSnapshot>(_lanes.Count);
            for (int i = 0; i < _lanes.Count; i++) snapshots.Add(_lanes[i].BuildSnapshot());
            return snapshots;
        }

        /// <summary>终态历史摘要（<c>RecordCount + Digest</c>；不展开旧终态记录）。</summary>
        public HistorySummary BuildTerminalSummary() => _registry.BuildTerminalSummary();

        /// <summary>
        /// Step 末只读不变量检查：不存在活动对象引用终态计划。
        /// 返回失败描述（null = 通过）。
        /// </summary>
        public string CheckNoActiveArtifactReferencesTerminalPlan()
        {
            for (int i = 0; i < _lanes.Count; i++)
            {
                string laneError = _lanes[i].ValidateNonOverlapping();
                if (laneError != null) return laneError;
            }

            IReadOnlyList<ActionPlan> active = _registry.ActivePlans;
            for (int i = 0; i < active.Count; i++)
            {
                if (active[i].IsTerminal)
                {
                    return "active-index-holds-terminal plan=" +
                           active[i].ActionPlanId.Value.ToString(CultureInfo.InvariantCulture);
                }
                ActorLane lane = LaneOfPlan(active[i].ActionPlanId);
                if (lane == null)
                {
                    return "active-plan-missing-from-lane plan=" +
                           active[i].ActionPlanId.Value.ToString(CultureInfo.InvariantCulture);
                }
            }
            return null;
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
        {
            var parts = new List<string>(_lanes.Count);
            for (int i = 0; i < _lanes.Count; i++)
            {
                ActorLane lane = _lanes[i];
                parts.Add("lane#" + lane.UnitId.Value.ToString(CultureInfo.InvariantCulture) +
                          "[" + lane.Count.ToString(CultureInfo.InvariantCulture) +
                          (lane.IsSubmissionLocked ? ",locked" : string.Empty) + "]");
            }
            return "rev=" + ScheduleRevision.ToString(CultureInfo.InvariantCulture) +
                   " active=" + _registry.ActiveCount.ToString(CultureInfo.InvariantCulture) +
                   " terminal=" + _registry.TerminalRecordCount.ToString(CultureInfo.InvariantCulture) +
                   " " + string.Join(" ", parts);
        }
    }
}
