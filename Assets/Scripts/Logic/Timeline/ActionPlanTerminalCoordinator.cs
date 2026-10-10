using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// <strong>唯一 <c>ActionPlanTerminalCoordinator</c></strong>（任务包「必须产出」4）：
    /// 计划进入终态的<strong>唯一入口</strong>。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item><strong>第一次请求胜出</strong>：同一计划的第一次成功终态请求确定最终
    /// 状态/原因/<c>TerminalTick</c>；后续死亡、战斗结束、控制打断等请求不得覆盖它。</item>
    /// <item><strong>幂等</strong>：重复或冲突请求返回
    /// <see cref="ActionPlanTerminalOutcome.WasAlreadyTerminal"/> = true，
    /// <strong>不</strong>重复发事件、<strong>不</strong>重复清理、<strong>不</strong>回卷 ID/Sequence。</item>
    /// <item><strong>固定清理参与者顺序</strong>：按 <see cref="IActionPlanCleanupParticipant.Order"/> 升序执行，
    /// 同 Order 时按 <see cref="IActionPlanCleanupParticipant.ParticipantId"/> 的 Ordinal 序
    /// （因此顺序与装配枚举顺序无关）。</item>
    /// <item><strong>不改修订号</strong>：终态不是排程编辑，因此
    /// <c>ScheduleRevision</c> 不变（任务包「核心规则」）。</item>
    /// <item>全部终态（自然完成、排程删除、目标失效、来源威胁取消、交互/控制打断、
    /// 强制位移、Owner 死亡、战斗结束）都只能经这里。</item>
    /// </list>
    ///
    /// 归档：协调器登记本 Tick 的终态候选；候选在本 Tick 全部清理与只读不变量检查完成后，
    /// 由 Step 阶段 19 通过任务 03 的归档端口冻结一次（见 <see cref="ActionPlanTerminalArchiveSource"/>）。
    /// 归档不产生新的玩法事件、不改变计划 ID 或修订号。
    /// </summary>
    public sealed class ActionPlanTerminalCoordinator
    {
        private readonly ActionScheduleAuthority _authority;
        private readonly List<IActionPlanCleanupParticipant> _participants = new List<IActionPlanCleanupParticipant>();
        private readonly List<ActionPlanId> _frozenTickCandidates = new List<ActionPlanId>();
        private readonly HashSet<long> _frozenTickCandidateIds = new HashSet<long>();
        private bool _archiveCandidatesConsumed;

        public ActionPlanTerminalCoordinator(
            ActionScheduleAuthority authority, IReadOnlyList<IActionPlanCleanupParticipant> participants = null)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            if (participants != null)
            {
                for (int i = 0; i < participants.Count; i++)
                {
                    if (participants[i] != null) _participants.Add(participants[i]);
                }
            }
            SortParticipants();
        }

        /// <summary>固定顺序的参与者（只读；顺序见类型文档）。</summary>
        public IReadOnlyList<IActionPlanCleanupParticipant> Participants => _participants;

        /// <summary>本 Tick 已进入终态并等待归档冻结的计划（按 <c>ActionPlanId</c> 升序）。</summary>
        public IReadOnlyList<ActionPlanId> FrozenTickCandidates => _frozenTickCandidates;

        /// <summary>
        /// 生命周期<strong>语义事件</strong>端口（装配方回填；未回填时只做终态清理、不发事件）。
        ///
        /// 它只在<strong>第一次请求胜出</strong>（<see cref="EnterTerminal"/> /
        /// <see cref="EnterCompletion"/> 真正写入终态字段）时被调用一次，因此
        /// "每个计划恰好一条终态/完成事件"是结构事实，而不是各调用点各自记得发。
        /// 参数顺序：计划、原因（<c>None</c> = 自然完成）、Tick。
        /// </summary>
        public Action<ActionPlan, ActionTerminationReason, long> TerminalLifecycleSink { get; set; }

        /// <summary>
        /// 进入终态。<paramref name="reason"/> 为 <see cref="ActionTerminationReason.None"/>
        /// 表示<strong>自然完成</strong>（<c>Completed</c>）；否则为带原因的 <c>Terminated</c>。
        /// </summary>
        public ActionPlanTerminalOutcome EnterTerminal(ActionPlan plan, ActionTerminationReason reason, long tick)
        {
            if (plan == null)
                throw new LogicDefinitionException(ScheduleCodes.SCHEDULE_TERMINAL_PLAN_UNKNOWN, "plan is null");
            if (!_authority.Registry.Contains(plan.ActionPlanId))
                throw new LogicDefinitionException(ScheduleCodes.SCHEDULE_TERMINAL_PLAN_UNKNOWN,
                    plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));

            if (reason == ActionTerminationReason.None)
                return EnterCompletion(plan, tick);

            ActionTerminationReasons.CodeOf(reason);   // 未知原因以稳定码失败

            // 第一次请求胜出：已是终态 ⇒ 幂等返回，什么都不改。
            if (plan.IsTerminal)
                return new ActionPlanTerminalOutcome(plan.ActionPlanId, false, plan.State, plan.TerminationReason, plan.TerminalTick);

            plan.State = ActionPlanState.Terminated;
            plan.TerminationReason = reason;
            plan.TerminalTick = tick;
            plan.ReservedTurnBudgetTicks = 0;   // 终态不再持有任何未消费预留

            _authority.MarkPlanTerminal(plan, tick);
            _authority.RecordTerminalOrder(plan.ActionPlanId);

            var context = new ActionPlanTerminalContext(plan, reason, tick, firstRequest: true);
            for (int i = 0; i < _participants.Count; i++) _participants[i].Cleanup(context);

            RecordArchiveCandidate(plan.ActionPlanId);
            TerminalLifecycleSink?.Invoke(plan, reason, tick);
            return new ActionPlanTerminalOutcome(plan.ActionPlanId, true, plan.State, plan.TerminationReason, plan.TerminalTick);
        }

        /// <summary>自然完成（<c>Completed</c>，<strong>没有</strong>终止原因）。</summary>
        public ActionPlanTerminalOutcome EnterCompletion(ActionPlan plan, long tick)
        {
            if (plan == null)
                throw new LogicDefinitionException(ScheduleCodes.SCHEDULE_TERMINAL_PLAN_UNKNOWN, "plan is null");
            if (!_authority.Registry.Contains(plan.ActionPlanId))
                throw new LogicDefinitionException(ScheduleCodes.SCHEDULE_TERMINAL_PLAN_UNKNOWN,
                    plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));

            if (plan.IsTerminal)
                return new ActionPlanTerminalOutcome(plan.ActionPlanId, false, plan.State, plan.TerminationReason, plan.TerminalTick);

            plan.State = ActionPlanState.Completed;
            plan.TerminationReason = ActionTerminationReason.None;
            plan.TerminalTick = tick;
            plan.ReservedTurnBudgetTicks = 0;

            _authority.MarkPlanTerminal(plan, tick);
            _authority.RecordTerminalOrder(plan.ActionPlanId);

            var context = new ActionPlanTerminalContext(plan, ActionTerminationReason.None, tick, firstRequest: true);
            for (int i = 0; i < _participants.Count; i++) _participants[i].Cleanup(context);

            RecordArchiveCandidate(plan.ActionPlanId);
            TerminalLifecycleSink?.Invoke(plan, ActionTerminationReason.None, tick);
            return new ActionPlanTerminalOutcome(plan.ActionPlanId, true, plan.State, plan.TerminationReason, plan.TerminalTick);
        }

        /// <summary>
        /// 强制位移的<strong>原因优先级</strong>去重（任务包「核心规则」）：
        /// 同一计划同时满足 <c>MovementOriginInvalidated</c> 与
        /// <c>ReservationPreemptedByForcedDisplacement</c> 时前者胜出。
        /// 返回优先级更高的原因。
        /// </summary>
        public static ActionTerminationReason ResolveForcedDisplacementReason(
            bool movementOriginInvalidated, bool reservationPreempted)
        {
            if (movementOriginInvalidated) return ActionTerminationReason.MovementOriginInvalidated;
            if (reservationPreempted) return ActionTerminationReason.ReservationPreemptedByForcedDisplacement;
            return ActionTerminationReason.None;
        }

        /// <summary>按 <c>ActionPlanId</c> 升序终止某单位的全部非终态计划（死亡清理）。</summary>
        public IReadOnlyList<ActionPlanTerminalOutcome> TerminateAllPlansOfUnit(
            UnitId unitId, ActionTerminationReason reason, long tick)
        {
            List<ActionPlan> plans = _authority.CollectNonTerminalPlansOfUnit(unitId);
            var outcomes = new List<ActionPlanTerminalOutcome>(plans.Count);
            for (int i = 0; i < plans.Count; i++) outcomes.Add(EnterTerminal(plans[i], reason, tick));
            return outcomes;
        }

        /// <summary>
        /// 按 <c>UnitId -&gt; ActionPlanId</c> 升序终止全部非终态计划（战斗结束 Finalizer）。
        /// </summary>
        public IReadOnlyList<ActionPlanTerminalOutcome> TerminateAllPlans(
            ActionTerminationReason reason, long tick)
        {
            List<ActionPlan> plans = _authority.CollectAllNonTerminalPlansOrdered();
            var outcomes = new List<ActionPlanTerminalOutcome>(plans.Count);
            for (int i = 0; i < plans.Count; i++) outcomes.Add(EnterTerminal(plans[i], reason, tick));
            return outcomes;
        }

        /// <summary>Step 阶段 0：开始归档批次；保留上次冻结后尚未被归档的终态候选。</summary>
        public void BeginTick(long tick)
        {
            _authority.BeginTerminalBatch();
            if (_archiveCandidatesConsumed) ClearArchiveCandidates();
        }

        /// <summary>
        /// Step 阶段 19：本 Tick 全部清理与只读不变量检查完成后，把候选交给归档端口
        /// <strong>冻结一次</strong>。无新增候选时不读取、不复制、不哈希任何旧归档记录。
        /// </summary>
        public void EndTick(long tick)
        {
            // 归档冻结由 ActionPlanTerminalArchiveSource 在阶段 19 完成；
            // 协调器只负责把"本 Tick 候选"整理为稳定顺序。
            _frozenTickCandidates.Sort((a, b) => a.Value.CompareTo(b.Value));
        }

        private void RecordArchiveCandidate(ActionPlanId actionPlanId)
        {
            if (_archiveCandidatesConsumed) ClearArchiveCandidates();
            if (_frozenTickCandidateIds.Add(actionPlanId.Value)) _frozenTickCandidates.Add(actionPlanId);
        }
        internal void MarkArchiveCandidatesConsumed() => _archiveCandidatesConsumed = true;
        private void ClearArchiveCandidates()
        {
            _frozenTickCandidates.Clear(); _frozenTickCandidateIds.Clear(); _archiveCandidatesConsumed = false;
        }

        private void SortParticipants()
        {
            _participants.Sort((a, b) =>
            {
                int byOrder = a.Order.CompareTo(b.Order);
                if (byOrder != 0) return byOrder;
                return string.CompareOrdinal(a.ParticipantId, b.ParticipantId);
            });
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
            => "terminal-candidates=" + _frozenTickCandidates.Count.ToString(CultureInfo.InvariantCulture) +
               " participants=" + _participants.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 任务 05 的"停止继续调度"清理参与者：它把计划标记为终态之后不再产出任何 Intent。
    ///
    /// 任务 05 没有 Intent 队列（那是任务 08），因此这里只登记语义槽位并保持幂等；
    /// 任务的<strong>权威事实</strong>已经由 <see cref="ActionScheduleAuthority.MarkPlanTerminal"/>
    /// 完成（离开活动索引 + 离开 Lane），本参与者不重复那一份工作。
    /// </summary>
    public sealed class StopSchedulingCleanupParticipant : IActionPlanCleanupParticipant
    {
        public static readonly StopSchedulingCleanupParticipant Instance = new StopSchedulingCleanupParticipant();

        public int Order => ActionPlanCleanupOrder.StopScheduling;

        public string ParticipantId => "actionplan.stop-scheduling";

        public void Cleanup(ActionPlanTerminalContext context) { }
    }

    /// <summary>
    /// 任务 05 的"机会绑定清理"参与者：一个计划进入终态时，清除它与反应机会之间的绑定。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>只有<strong>普通 Attack</strong> 的终止才会触发"来源威胁取消"路径
    /// （<c>CancelForSourceThreat</c>）：关闭机会、经<strong>同一</strong>协调器以
    /// <c>SourceThreatCancelled</c> 终止绑定反应，并向任务 07 请求释放未消费预留；</item>
    /// <item>自然完成（<c>reason == None</c>）与战斗结束（<c>BattleEnded</c>）<strong>不</strong>走该路径：
    /// 前者发生时机会早已按截止关闭，后者由唯一 Finalizer 的
    /// <c>CloseAllForBattleEnd</c> 统一关闭（否则关闭原因会被误写成来源取消）；</item>
    /// <item>反应计划自身的终态<strong>不</strong>触发该例外（规格：「其他反应终态不触发该例外」）；</item>
    /// <item>清理幂等：<c>CancelForSourceThreat</c> 对已关闭的机会是安全无操作。</item>
    /// </list>
    ///
    /// 系统引用由装配方在构造完 <see cref="ReactionOpportunitySystem"/> 之后回填
    /// （两者互相引用：系统需要协调器执行终态，协调器需要系统清理机会绑定）；
    /// 未回填时本参与者是显式无操作，绝不猜测机会状态。
    /// </summary>
    public sealed class OpportunityBindingCleanupParticipant : IActionPlanCleanupParticipant
    {
        public static readonly OpportunityBindingCleanupParticipant Instance = new OpportunityBindingCleanupParticipant();

        public int Order => ActionPlanCleanupOrder.OpportunityBindingCleanup;

        public string ParticipantId => "actionplan.opportunity-binding";

        /// <summary>机会系统（装配方回填；为 null 时本参与者无操作）。</summary>
        public ReactionOpportunitySystem Opportunities { get; set; }

        public void Cleanup(ActionPlanTerminalContext context)
        {
            if (context == null || !context.IsFirstRequest) return;
            if (Opportunities == null) return;

            ActionPlan plan = context.Plan;
            if (plan == null) return;
            if (plan.ActionType != ActionType.Attack) return;
            if (plan.Origin != ActionPlanOrigin.Ordinary) return;
            if (context.Reason == ActionTerminationReason.None) return;
            if (context.Reason == ActionTerminationReason.BattleEnded) return;

            Opportunities.CancelForSourceThreat(plan.ActionPlanId, context.Tick);
        }
    }
}
