using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>Dodge 终态接缝的稳定码（不改名、不复用既有码）。</summary>
    public static class DodgeSeamCodes
    {
        /// <summary>接缝只接受"由 ReactionOpportunity 创建、直接 Locked 的 Dodge 计划"。</summary>
        public const string DODGE_SEAM_NOT_A_DODGE = "DODGE_SEAM_NOT_A_DODGE";

        /// <summary>没有装配换位事务端口（任务 06/08 尚未接入）⇒ 明确拒绝，绝不伪造触发。</summary>
        public const string DODGE_SEAM_TRANSACTION_UNAVAILABLE = "DODGE_SEAM_TRANSACTION_UNAVAILABLE";

        /// <summary>机会不是 <c>Accepted</c>（已关闭/已触发/来源已取消）⇒ 换位事务不得启动。</summary>
        public const string DODGE_SEAM_OPPORTUNITY_NOT_TRIGGERABLE = "DODGE_SEAM_OPPORTUNITY_NOT_TRIGGERABLE";

        /// <summary>终态清理<strong>不得</strong>改动 <c>ScheduleRevision</c>（内部矛盾 ⇒ 令 Step 失败）。</summary>
        public const string DODGE_SEAM_REVISION_CHANGED = "DODGE_SEAM_REVISION_CHANGED";

        /// <summary>换位事务已成功但触发确认失败（内部矛盾 ⇒ 令 Step 失败；不得静默降级）。</summary>
        public const string DODGE_SEAM_TRIGGER_CONFIRM_INCONSISTENT = "DODGE_SEAM_TRIGGER_CONFIRM_INCONSISTENT";
    }

    /// <summary>
    /// <strong>TriggerTick 换位事务端口</strong>（任务 06/08 实现；任务 05 只冻结接缝）。
    ///
    /// 契约（冻结）：
    /// <list type="bullet">
    /// <item>它在<strong>一个</strong>事务内完成"Dodge 所有者从旧格换到目的格 + 空间 Reservation 调整"；
    /// Dodge 计划的 <see cref="ActionPlan.Destination"/> 就是目的格，不由本端口另行声明；</item>
    /// <item>返回 null = 换位成功；否则返回稳定拒绝码，且<strong>不得留下任何副作用</strong>
    /// （包括不得改动任何计划的终态、不得消费预留）；</item>
    /// <item>它<strong>不得</strong>写计划终态：被失效的后续移动统一由
    /// <see cref="ActionPlanTerminalCoordinator"/> 以
    /// <see cref="ActionTerminationReason.MovementOriginInvalidatedByDodge"/> 终止；
    /// </item>
    /// <item>它<strong>不得</strong>改动 <c>ScheduleRevision</c>、不得左吸、不得自动改路或
    /// ripple 非依赖计划。</item>
    /// </list>
    /// </summary>
    public interface IDodgeRelocationTransaction
    {
        /// <param name="dodgePlan">已接受、直接 Locked 的 Dodge 反应计划（目的格见其 <c>Destination</c>）。</param>
        /// <param name="invalidatedCandidates">
        /// 只读位置依赖闭包给出的候选（按 <c>ActionPlanId</c> 升序）。它是<strong>预检输入</strong>：
        /// 事务成功之后才由接缝终止它们；事务失败时它们必须保持原样。
        /// </param>
        /// <param name="tick">TriggerTick。</param>
        /// <returns>null = 换位成功；否则稳定拒绝码。</returns>
        string TryRelocate(ActionPlan dodgePlan, IReadOnlyList<ActionPlan> invalidatedCandidates, long tick);
    }

    /// <summary>
    /// 一次 Dodge 终态接缝提交的结果。第一次换位事务胜出；重复调用<strong>幂等</strong>返回同一结果。
    /// </summary>
    public sealed record DodgeRelocationOutcome(
        bool Committed,
        string RejectionCode,
        IReadOnlyList<ActionPlanId> InvalidatedCandidates,
        IReadOnlyList<ActionPlanId> NewlyTerminatedPlans,
        long ScheduleRevision)
    {
        public static DodgeRelocationOutcome Rejected(string code, long revision)
            => new DodgeRelocationOutcome(
                false, code, Array.Empty<ActionPlanId>(), Array.Empty<ActionPlanId>(), revision);
    }

    /// <summary>
    /// <strong>Dodge 对后续移动的终态接缝</strong>（任务包「Dodge 对后续移动的终态接缝」）。
    ///
    /// 它只做三件事，顺序固定：
    /// <list type="number">
    /// <item><strong>只读</strong>位置依赖闭包查询：复用 <see cref="ScheduleEvaluator.QueryPositionDependencyClosure"/>
    /// 找出"实际 From 变化会失效"的后续 Editable 移动及其<strong>传递闭包</strong>
    /// （跨过 Attack/Guard 等非移动计划，不以"紧邻下一项"代替依赖关系），
    /// 并且每次都读取<strong>当前</strong>排程 ⇒ 自然覆盖接受 Dodge 之后新增/编辑的移动；</item>
    /// <item>调用任务 06/08 的换位事务端口；<strong>只有它成功</strong>才继续，
    /// 失败/缺端口 ⇒ 本方法<strong>不</strong>修改任何计划（不因本次 Dodge 修改移动链）；</item>
    /// <item>换位成功后经<strong>统一终态协调器</strong>按 <c>ActionPlanId</c> 升序把候选从
    /// Editable 终止为 <see cref="ActionTerminationReason.MovementOriginInvalidatedByDodge"/>，
    /// 再确认机会触发。<strong>不</strong>增加 <c>ScheduleRevision</c>、<strong>不</strong>左吸、
    /// <strong>不</strong>涟漪非依赖计划、不回卷或复用任何 ID/Sequence。</item>
    /// </list>
    ///
    /// 它<strong>不</strong>：自己换位、自己改计划终态、自己发玩法事件（终态事件由 Step/事件族统一发射）、
    /// 也不把"生成反应 Intent"当作触发。
    /// </summary>
    public sealed class DodgeMovementInvalidationSeam
    {
        private readonly ActionScheduleAuthority _authority;
        private readonly ScheduleEvaluator _evaluator;
        private readonly ActionPlanTerminalCoordinator _coordinator;
        private readonly ReactionOpportunitySystem _reactions;
        private readonly IDodgeRelocationTransaction _transaction;

        /// <summary>已提交的机会 ⇒ 结果（第一次换位事务胜出；重复调用幂等）。</summary>
        private readonly Dictionary<long, DodgeRelocationOutcome> _committed =
            new Dictionary<long, DodgeRelocationOutcome>();

        public DodgeMovementInvalidationSeam(
            ActionScheduleAuthority authority,
            ScheduleEvaluator evaluator,
            ActionPlanTerminalCoordinator coordinator,
            ReactionOpportunitySystem reactions,
            IDodgeRelocationTransaction transaction = null)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            // 任务 06 处置（任务 05 交接记录 §27.3 缺陷 D3）：
            // 机会系统由可选改为**必填**——"机会必须仍 Accepted"这一步不允许被 null 短路。
            // 否则以 null 构造接缝时会静默跳过该校验，换位事务可能在机会已关闭后仍被调用。
            _reactions = reactions ?? throw new ArgumentNullException(nameof(reactions));
            _transaction = transaction;
        }

        /// <summary>换位事务端口（未装配时为 null ⇒ 提交明确拒绝且零写入）。</summary>
        public IDodgeRelocationTransaction Transaction => _transaction;

        /// <summary>是否已装配换位事务（任务 06/08 接入的证据面）。</summary>
        public bool HasTransaction => _transaction != null;

        /// <summary>
        /// <strong>只读</strong>查询：实际 From 变化会失效的后续 Editable 移动（含传递闭包，
        /// 跨过非移动计划；按 <c>ActionPlanId</c> 升序）。它不写任何状态、不改修订号。
        /// </summary>
        public IReadOnlyList<ActionPlan> QueryInvalidatedMoves(ActionPlan dodgePlan)
        {
            if (dodgePlan == null) return Array.Empty<ActionPlan>();
            return QueryInvalidatedMoves(new[] { dodgePlan });
        }

        /// <summary>只读查询的多种子重载（预检与 TriggerTick 共用同一实现）。</summary>
        public IReadOnlyList<ActionPlan> QueryInvalidatedMoves(IReadOnlyList<ActionPlan> seeds)
        {
            if (seeds == null || seeds.Count == 0) return Array.Empty<ActionPlan>();

            IReadOnlyList<ScheduleOperationEvaluation> closure =
                _evaluator.QueryPositionDependencyClosure(seeds, movementOnly: true);

            var result = new List<ActionPlan>(closure.Count);
            for (int i = 0; i < closure.Count; i++)
            {
                ActionPlan plan = _authority.Registry.Find(closure[i].PlanId);
                if (plan == null) continue;
                // 只有"尚未锁定、尚未终态"的移动族才可能被失效：Locked/Running 的计划
                // 已经消费了预留，不能因为一次 Dodge 被回退（规格：Locked 后不退款、不回卷）。
                if (!plan.IsEditable) continue;
                if (!plan.IsMovementFamily) continue;
                result.Add(plan);
            }
            return result;
        }

        /// <summary>
        /// 触发时读取<strong>当前</strong>排程并把整条链一次提交。
        /// 返回结果永远非 null；<c>Committed=false</c> 时保证<strong>零写入</strong>。
        /// </summary>
        public DodgeRelocationOutcome TryCommit(
            ActionPlan dodgePlan, ReactionOpportunityId opportunityId, long tick)
        {
            long revision = _authority.ScheduleRevision;

            if (dodgePlan == null || dodgePlan.ActionType != ActionType.Dodge || !dodgePlan.IsReaction)
                return DodgeRelocationOutcome.Rejected(DodgeSeamCodes.DODGE_SEAM_NOT_A_DODGE, revision);

            // 第一次换位事务胜出：重复调用幂等返回首次结果，绝不第二次调用事务端口。
            if (_committed.TryGetValue(opportunityId.Value, out DodgeRelocationOutcome previous))
                return previous;

            if (_transaction == null)
                return DodgeRelocationOutcome.Rejected(
                    DodgeSeamCodes.DODGE_SEAM_TRANSACTION_UNAVAILABLE, revision);

            // 机会必须仍处于 Accepted（来源未取消、未被战斗结束关闭）；否则换位事务不得启动。
            // 该校验**始终**执行：机会系统是必填依赖，不允许用 null 短路（缺陷 D3 的处置）。
            {
                ReactionOpportunityRuntime opportunity = _reactions.FindOpportunity(opportunityId);
                if (opportunity == null || opportunity.State != ReactionOpportunityState.Accepted)
                    return DodgeRelocationOutcome.Rejected(
                        DodgeSeamCodes.DODGE_SEAM_OPPORTUNITY_NOT_TRIGGERABLE, revision);
            }

            IReadOnlyList<ActionPlan> candidates = QueryInvalidatedMoves(dodgePlan);
            var candidateIds = new List<ActionPlanId>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++) candidateIds.Add(candidates[i].ActionPlanId);

            // —— 换位事务：失败 ⇒ 零写入（不因本次 Dodge 修改任何移动计划）——
            string error = _transaction.TryRelocate(dodgePlan, candidates, tick);
            if (error != null)
                return DodgeRelocationOutcome.Rejected(error, _authority.ScheduleRevision);

            // 终态清理不推进修订号：这不是排程编辑事务。
            if (_authority.ScheduleRevision != revision)
                throw new LogicDefinitionException(
                    DodgeSeamCodes.DODGE_SEAM_REVISION_CHANGED,
                    "before=" + revision.ToString(CultureInfo.InvariantCulture) +
                    " after=" + _authority.ScheduleRevision.ToString(CultureInfo.InvariantCulture));

            // —— 统一终态协调器（唯一入口）：按 ActionPlanId 升序终止仍为 Editable 的候选 ——
            var terminated = new List<ActionPlanId>();
            for (int i = 0; i < candidates.Count; i++)
            {
                ActionPlan candidate = candidates[i];
                ActionPlan current = _authority.Registry.Find(candidate.ActionPlanId);
                if (current == null || !current.IsEditable) continue;
                ActionPlanTerminalOutcome outcome = _coordinator.EnterTerminal(
                    current, ActionTerminationReason.MovementOriginInvalidatedByDodge, tick);
                if (outcome.EnteredTerminal) terminated.Add(current.ActionPlanId);
            }

            // —— 触发确认：只有换位事务成功之后才允许 Accepted -> Triggered ——
            {
                string confirm = _reactions.ConfirmTrigger(opportunityId, tick);
                if (confirm != null)
                    throw new LogicDefinitionException(
                        DodgeSeamCodes.DODGE_SEAM_TRIGGER_CONFIRM_INCONSISTENT, confirm);
            }

            var outcomeResult = new DodgeRelocationOutcome(
                true, null, candidateIds, terminated, _authority.ScheduleRevision);
            _committed[opportunityId.Value] = outcomeResult;
            return outcomeResult;
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
            => "dodge-seam-transaction=" + (HasTransaction ? "yes" : "no") +
               " committed=" + _committed.Count.ToString(CultureInfo.InvariantCulture);
    }
}
