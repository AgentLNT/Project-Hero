using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// 全局 <c>ActionPlan</c> 注册表（任务包「必须产出」2）。
    ///
    /// 契约：
    /// <list type="bullet">
    /// <item><strong>单例全局、按稳定键访问</strong>：唯一键是 <see cref="ActionPlanId"/>；
    /// <strong>没有</strong>按 <c>WindowId</c> 分桶的查询入口，也没有"切换窗口时遍历并结算计划"的入口。</item>
    /// <item><strong>活动索引</strong>（<see cref="ActivePlans"/>）只包含非终态计划，按
    /// <c>ActionPlanId</c> 升序；进入终态的计划<strong>立即</strong>离开活动索引，
    /// 只留在历史计数与不可变归档里。</item>
    /// <item><strong>历史</strong>只保留 <c>RecordCount + Digest</c>（不遍历、不复制旧终态记录）；
    /// 完整字段由统一终态协调器经任务 03 的归档端口冻结一次。</item>
    /// <item>注册表<strong>不</strong>持有 Lane：Lane 由 <c>ActionScheduleAuthority</c> 管理；
    /// 注册表只回答"这个 ID 对应的计划是什么、它是否活动"。</item>
    /// </list>
    /// </summary>
    public sealed class ActionPlanRegistry
    {
        private readonly Dictionary<long, ActionPlan> _byId = new Dictionary<long, ActionPlan>();
        private readonly List<ActionPlan> _active = new List<ActionPlan>();
        private readonly Dictionary<long, ActionPlanSnapshot> _frozenTerminal = new Dictionary<long, ActionPlanSnapshot>();
        private readonly Dictionary<long, ActionPlanSnapshot> _activeProjection = new Dictionary<long, ActionPlanSnapshot>();
        private IReadOnlyList<ActionPlanSnapshot> _activeSnapshots = Array.Empty<ActionPlanSnapshot>();
        private bool _projectionMembershipChanged;
        private bool _activeMembershipChanged;
        private IReadOnlyList<ActionPlan> _activeView = Array.Empty<ActionPlan>();

        internal IReadOnlyList<ActionPlanSnapshot> BuildActiveSnapshots(ProjectHero.Logic.Definitions.IFactionRelationResolver factions)
        {
            bool changed = _projectionMembershipChanged;
            foreach (var plan in _active)
            {
                if (_activeProjection.TryGetValue(plan.ActionPlanId.Value, out var snapshot) && snapshot.MatchesAuthoritative(plan, factions)) continue;
                _activeProjection[plan.ActionPlanId.Value] = ActionPlanSnapshot.From(plan, factions); changed = true;
            }
            if (changed)
            {
                var snapshots = new List<ActionPlanSnapshot>(_activeProjection.Values);
                snapshots.Sort((a, b) => a.ActionPlanId.CompareTo(b.ActionPlanId));
                _activeSnapshots = snapshots.Count == 0 ? Array.Empty<ActionPlanSnapshot>() : snapshots.AsReadOnly();
                _projectionMembershipChanged = false;
            }
            return _activeSnapshots;
        }

        /// <summary>活动计划数（非终态）。</summary>
        public int ActiveCount => _active.Count;
        internal bool HasDefensePlans
        {
            get
            {
                foreach (var plan in _active)
                    if (!plan.IsEditable && (plan.ActionType == ActionType.Guard || plan.ActionType == ActionType.Block || plan.ActionType == ActionType.Dodge)) return true;
                return false;
            }
        }

        /// <summary>已进入终态并离开活动索引的计划累计数（不展开明细）。</summary>
        public long TerminalRecordCount { get; private set; }

        /// <summary>
        /// 终态历史的增量摘要（与任务 03 的 <c>HistoryDigestProtocol</c> 同源：
        /// 每次追加一条终态记录后更新）。逐 Tick 快照只使用
        /// <see cref="TerminalRecordCount"/> 与 <see cref="TerminalDigestValue"/>，
        /// <strong>不</strong>展开旧终态记录。
        /// </summary>
        public ulong TerminalDigestValue { get; private set; } = ReplayFormat.HistorySeedDigest();

        /// <summary>全部活动计划的规范视图（按 <c>ActionPlanId</c> 升序的只读拷贝）。</summary>
        public IReadOnlyList<ActionPlan> ActivePlans
        {
            get
            {
                if (_active.Count == 0) return Array.Empty<ActionPlan>();
                if (!_activeMembershipChanged) return _activeView;
                var copy = new List<ActionPlan>(_active);
                copy.Sort((a, b) => a.ActionPlanId.Value.CompareTo(b.ActionPlanId.Value));
                _activeView = copy.AsReadOnly(); _activeMembershipChanged = false;
                return _activeView;
            }
        }

        /// <summary>按 ID 查找，保留终态对象以支持重复终态请求的幂等检查。</summary>
        public ActionPlan Find(ActionPlanId actionPlanId)
            => _byId.TryGetValue(actionPlanId.Value, out ActionPlan plan) ? plan : null;

        public bool Contains(ActionPlanId actionPlanId) => _byId.ContainsKey(actionPlanId.Value);

        /// <summary>只读终态明细；在阶段 19 全部清理结束后冻结，与活动索引分离。</summary>
        public ActionPlanSnapshot FindFrozenTerminal(ActionPlanId id)
            => _frozenTerminal.TryGetValue(id.Value, out var snapshot) ? snapshot : null;

        internal ActionPlanSnapshot FreezeTerminal(ActionPlan plan, ProjectHero.Logic.Definitions.IFactionRelationResolver factions)
        {
            if (!plan.IsTerminal) throw new InvalidOperationException("Cannot freeze an active plan");
            if (!_frozenTerminal.TryGetValue(plan.ActionPlanId.Value, out var snapshot))
            {
                snapshot = ActionPlanSnapshot.From(plan, factions);
                _frozenTerminal.Add(plan.ActionPlanId.Value, snapshot);
            }
            return snapshot;
        }

        /// <summary>注册一个新计划（ID 必须唯一且有效）。</summary>
        internal void Register(ActionPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (!plan.ActionPlanId.IsValid)
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_ID_INVALID, "0");
            if (_byId.ContainsKey(plan.ActionPlanId.Value))
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_DUPLICATE_ID,
                    plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));

            _byId[plan.ActionPlanId.Value] = plan;
            _active.Add(plan);
            _projectionMembershipChanged = true;
            _activeMembershipChanged = true;
        }

        /// <summary>
        /// 把计划移出活动索引并累计历史计数与摘要。
        /// 它<strong>不</strong>删除 <c>_byId</c> 中的条目：重复终态请求必须仍然能查到该计划、
        /// 并因"已是终态"而幂等返回。计划对象本身不复制、ID 与修订号都不变。
        /// </summary>
        internal void MarkTerminal(ActionPlan plan, long terminalTick)
        {
            if (plan == null) return;
            if (!_active.Remove(plan)) return;
            _activeProjection.Remove(plan.ActionPlanId.Value); _projectionMembershipChanged = true;
            _activeMembershipChanged = true;

            TerminalRecordCount++;
            TerminalDigestValue = HistoryDigestProtocol.Append(
                TerminalDigestValue, TerminalRecordCount, terminalTick,
                HistoryRecordKind.ActionPlanTerminal, StableKeyOf(plan), null);
        }

        /// <summary>终态记录的稳定键（<c>ActionPlanId</c> 的十进制文本；唯一且稳定）。</summary>
        public static string StableKeyOf(ActionPlan plan)
            => plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture);

        /// <summary>历史摘要（固定大小；不持有可变归档引用）。</summary>
        public HistorySummary BuildTerminalSummary()
            => new HistorySummary(TerminalRecordCount, CanonicalHash.ToHex(TerminalDigestValue));
    }
}
