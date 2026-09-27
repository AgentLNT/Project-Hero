using System;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// <strong><c>ReactionPlanner</c></strong>（任务包「必须产出」11 第四段）。
    ///
    /// 它是"把一条已公开、仍可达、已通过全部权威校验的选项变成一个真实计划"的<strong>唯一</strong>入口：
    /// <list type="bullet">
    /// <item>调用 <see cref="ActionPlanFactory.TryCreateReaction"/> —— 因此反应计划
    /// <strong>直接处于 Locked</strong>、<c>SubmittedWindowId</c> 为空、<c>BudgetCostTicks = 0</c>、
    /// 固定区间为 <c>StartTick = TriggerTick - ReactionWindupTicks</c>、
    /// <c>EndTick = TriggerTick + RecoveryTicks</c>；</item>
    /// <item>固定区间必须先通过 Lane 重叠检查：首版<strong>不抢占</strong>，
    /// 也不因反应插入而重排普通计划；</item>
    /// <item>它<strong>不</strong>自己拼装 <c>ActionPlan</c>，也不写终态。</item>
    /// </list>
    /// 资源/目的格的冻结属于任务 07/06 的接缝（<see cref="IReactionPreparationPort"/>），
    /// 由调用方在调用本类之前完成。
    ///
    /// <strong>任务 07 追加（「必须产出」7 第二段 + §原子性与关闭语义）</strong>：
    /// <see cref="TryPlanWithReservation"/> 把"候选计划解析 → 肾上腺素预留 → 创建并注册计划"
    /// 合成<strong>一个</strong>接受事务。预留<strong>只</strong>发生在全部可预期校验之后、任何写入之前，
    /// 因此预留失败时整条接受失败且<strong>零副作用</strong>：不注册计划、不写 Lane、
    /// 不占用 <c>ActionPlanId</c>、不消费反应机会。它<strong>不</strong>校验窗口、
    /// <c>ExpectedWindowId</c> 或 <c>ConcurrentActionSystem</c> 授权（00 号规则 26）。
    /// </summary>
    public sealed class ReactionPlanner
    {
        private readonly ActionScheduleAuthority _authority;
        private readonly ActionPlanFactory _factory;
        private readonly BattleDefinition _definition;

        public ReactionPlanner(
            ActionScheduleAuthority authority, ActionPlanFactory factory, BattleDefinition definition)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        }

        /// <summary>
        /// 07-E：肾上腺素事务端口（<c>IAdrenalineLedgerPort</c>）。
        ///
        /// 未设置时（任务 05/06 的既有装配）本类型<strong>保持既有语义</strong>：只创建/注册计划，
        /// 不触碰任何资源、不抛异常、不改变任何既有拒绝码。
        /// </summary>
        public Resources.IAdrenalineLedgerPort AdrenalinePort { get; set; }

        /// <summary>
        /// 既有入口（任务 05 冻结的签名与语义，<strong>逐字保留</strong>）：只创建并注册计划，
        /// 不触碰肾上腺素（未装配端口时的语义面）。
        /// 返回 null = 成功；否则返回稳定拒绝码且<strong>不写任何状态</strong>。
        /// </summary>
        /// <param name="destination">
        /// Dodge 的目的格（命令携带；Block 必须为 null）。它只被记录到计划的
        /// <c>Destination</c> 字段：真正的换位属于任务 06/08 的 TriggerTick 事务，
        /// 本方法<strong>不</strong>换位、也<strong>不</strong>伪造触发。
        /// </param>
        public string TryPlan(
            ReactionOpportunityRuntime opportunity, ReactionOptionRuntime option,
            ActionPlan sourcePlan, long tick, GridPoint? destination, out ActionPlan plan)
            => TryPlanCore(opportunity, option, sourcePlan, tick, destination, default, out plan);

        /// <summary>
        /// <strong>07-A：接受反应（Block/Dodge）的原子提交入口。</strong>
        ///
        /// 提交顺序（失败即整条接受失败，且不留半提交状态）：
        /// <list type="number">
        /// <item><strong>候选解析</strong>：调用方已按下一个待分配值解析出
        /// <paramref name="candidatePlanId"/>（只读观察，<strong>不</strong>推进 ID 计数器）；
        /// 本方法再复核"计划是否可创建"的全部可预期前置条件；</item>
        /// <item><strong>预留</strong>：费用<strong>只</strong>来自权威
        /// <c>ActionSpec.AdrenalineCost</c>（命令载荷与调用参数都不携带、也不能覆盖它），
        /// 并先经 <c>AdrenalineRules.ValidateReactionCost</c> 约束；非 null 的返回值即整条接受失败，
        /// 此时尚未创建计划、尚未注册、尚未占用 ID、尚未消费机会；</item>
        /// <item><strong>提交</strong>：创建计划（必须恰好得到 <paramref name="candidatePlanId"/>）→
        /// Lane 固定区间复核 → <c>RegisterPlan</c>。这一步不再包含任何"可预期失败"分支，
        /// 因此不存在"已预留但未提交"的静默空洞（内部矛盾以稳定码显式失败）。</item>
        /// </list>
        /// 它<strong>不</strong>要求窗口/<c>ExpectedWindowId</c>/并发授权（00 号规则 26、
        /// 任务 07 §验收标准第 4 条）。
        /// </summary>
        public string TryPlanWithReservation(
            ReactionOpportunityRuntime opportunity, ReactionOptionRuntime option, ActionPlan sourcePlan,
            long tick, GridPoint? destination, ActionPlanId candidatePlanId, out ActionPlan plan)
        {
            plan = null;
            if (opportunity == null || option == null || sourcePlan == null || !candidatePlanId.IsValid)
                return ScheduleCodes.SCHEDULE_OPERATION_INVALID;

            ActionSpec spec = _definition.FindAction(option.ReactionActionSpecId);
            if (spec == null) return ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND;
            if (spec.Type != ActionType.Block && spec.Type != ActionType.Dodge)
                return ReactionCodes.REACTION_ACTION_NOT_BLOCK_OR_DODGE;

            // ① 候选解析：创建动作计划的**全部可预期**前置条件在预留之前判定
            //    （与 ActionPlanFactory 同规则、同拒绝码），因此预留之后不再有可失败的校验。
            string preconditionError = ValidateBeforeReservation(opportunity.DefenderUnitId, spec);
            if (preconditionError != null) return preconditionError;

            bool reserved = false;
            if (AdrenalinePort != null)
            {
                // 权威费用：ActionSpec.AdrenalineCost（AdrenalineRules.ValidateReactionCost 约束的同一字段）。
                string costError = _definition.AdrenalineRules.ValidateReactionCost(spec.Type, spec.AdrenalineCost);
                if (costError != null) return costError;

                // ② 预留：Available -> 带个人周期的计划预留（账本原子完成，失败时自身零副作用）。
                string reserveError = AdrenalinePort.TryReserveForReaction(
                    opportunity.DefenderUnitId, candidatePlanId, spec.AdrenalineCost);
                if (reserveError != null) return reserveError;
                reserved = true;
            }

            // ③ 提交：创建并注册计划（ID 必须恰好是候选值）。
            string commitError = TryPlanCore(
                opportunity, option, sourcePlan, tick, destination, candidatePlanId, out plan);
            if (commitError == null) return null;

            plan = null;
            if (!reserved) return commitError;

            // 已预留却无法提交 = 内部矛盾（前置条件与工厂判定分叉）。绝不静默留下"已扣费但无计划"的状态。
            throw new LogicDefinitionException(
                ReactionOpportunityCodes.REACTION_ADRENALINE_COMMIT_INCONSISTENT,
                commitError + " plan=" + candidatePlanId.Value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// <strong>07-A：候选固定区间的只读推导</strong>（与 <c>ActionPlan.ApplyReactionInterval</c> 同规则）：
        /// <c>StartTick = TriggerTick - ReactionWindupTicks</c>、<c>EndTick = TriggerTick + RecoveryTicks</c>。
        ///
        /// 它让调用方在<strong>创建计划之前</strong>完成 Lane 固定区间占位判定，
        /// 因此"区间被占用"这一拒绝路径既不占用 <c>ActionPlanId</c>，也不触碰任何资源。
        /// 返回 null = 成功；否则返回稳定拒绝码。
        /// </summary>
        public static string TryResolveReactionInterval(
            ActionSpec spec, long triggerTick, out long startTick, out long intervalLength)
        {
            startTick = 0L;
            intervalLength = 0L;
            if (spec == null) return ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND;

            int windup;
            int recovery;
            switch (spec.Timing)
            {
                case BlockReactionTimingSpec block:
                    windup = block.ReactionWindupTicks;
                    recovery = block.RecoveryTicks;
                    break;
                case DodgeReactionTimingSpec dodge:
                    windup = dodge.ReactionWindupTicks;
                    recovery = dodge.RecoveryTicks;
                    break;
                default:
                    return ReactionCodes.REACTION_ACTION_NOT_BLOCK_OR_DODGE;
            }

            startTick = triggerTick - windup;
            if (startTick < 0L) return ActionPlanCodes.ACTION_PLAN_REACTION_INTERVAL_MISMATCH;

            intervalLength = checked(triggerTick + recovery) - startTick;
            return null;
        }

        /// <summary>
        /// 创建并注册绑定到该机会的 Locked 反应计划（提交段；无资源事务）。
        ///
        /// <paramref name="expectedPlanId"/> 有效时要求工厂分配到的 ID <strong>恰好</strong>等于它：
        /// 候选 ID 由调用方按"下一个待分配值"解析，二者分叉即 <c>ScheduleEditor</c> 同款的
        /// 稳定不变量错误（<c>ACTION_PLAN_ID_RESERVATION_MISMATCH</c>），绝不静默接受。
        /// </summary>
        private string TryPlanCore(
            ReactionOpportunityRuntime opportunity, ReactionOptionRuntime option,
            ActionPlan sourcePlan, long tick, GridPoint? destination,
            ActionPlanId expectedPlanId, out ActionPlan plan)
        {
            plan = null;
            if (opportunity == null || option == null || sourcePlan == null)
                return ScheduleCodes.SCHEDULE_OPERATION_INVALID;

            ActionSpec spec = _definition.FindAction(option.ReactionActionSpecId);
            if (spec == null) return ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND;
            if (spec.Type != ActionType.Block && spec.Type != ActionType.Dodge)
                return ReactionCodes.REACTION_ACTION_NOT_BLOCK_OR_DODGE;

            ActionPlanCreationResult created = _factory.TryCreateReaction(
                new ReactionPlanRequest(
                    opportunity.DefenderUnitId, option.ReactionActionSpecId, sourcePlan.Facing, destination,
                    opportunity.Id, opportunity.SourceAttackPlanId,
                    opportunity.TriggerTick, option.ResponseDeadlineTick),
                tick);
            if (!created.Succeeded) return created.RejectionCode;

            ActionPlan reaction = created.Plan;

            // 07-A：候选 ID 与工厂实际分配值必须一致（"先解析候选 → 预留 → 提交"的收尾校验）。
            if (expectedPlanId.IsValid && reaction.ActionPlanId != expectedPlanId)
            {
                throw new LogicDefinitionException(
                    ActionPlanCodes.ACTION_PLAN_ID_RESERVATION_MISMATCH,
                    "expected=" + expectedPlanId.Value.ToString(CultureInfo.InvariantCulture) +
                    " allocated=" + reaction.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
            }

            ActorLane lane = _authority.FindLane(reaction.OwnerUnitId);
            if (lane != null && lane.IsSubmissionLocked)
                return ReactionCodes.REACTION_LANE_SUBMISSION_LOCKED;
            // 任务 07 裁定 A：本复核与 ReactionOpportunitySystem 的接受预检**共用同一容忍判据**
            // （DodgeMovementInvalidationSeam.DodgeMayInvalidate，与失效闭包过滤器逐字一致）。
            // 判据必须一致，否则会出现"预检放行、复核拒绝"的死路，或容忍了不会被清理的 Move。
            if (lane != null && lane.TryFindOverlapExcept(
                    reaction.StartTick, reaction.IntervalLength, null,
                    overlapping => DodgeMovementInvalidationSeam.DodgeMayInvalidate(
                        overlapping, reaction.ActionType, reaction.StartTick),
                    out _))
                return ReactionCodes.REACTION_LANE_INTERVAL_OCCUPIED;

            _authority.RegisterPlan(reaction);
            plan = reaction;
            return null;
        }

        /// <summary>
        /// 07-A：把 <c>ActionPlanFactory.TryCreateReaction</c>（含私有
        /// <c>ValidateActionSetMembership</c>）的<strong>同一规则、同一拒绝码</strong>提前到预留之前。
        ///
        /// 该重复是刻意的：工厂在预留之后仍会再校验一次，因此二者分叉只会让"本应成功的接受"
        /// 更早被拒绝，而<strong>绝不</strong>会留下"已预留但未注册"的空洞。
        /// </summary>
        private string ValidateBeforeReservation(UnitId ownerUnitId, ActionSpec spec)
        {
            if (!ownerUnitId.IsValid) return ActionPlanCodes.ACTION_PLAN_OWNER_INVALID;
            if (!_factory.TryGetOwnerFacts(ownerUnitId, out ActionPlanOwnerFacts facts))
                return ScheduleCodes.SCHEDULE_LANE_UNKNOWN_UNIT;

            string actionSetId = facts.ActionSetId.Value;
            if (string.IsNullOrEmpty(actionSetId)) return null;

            ActionSetDefinition actionSet = _definition.FindActionSet(facts.ActionSetId);
            if (actionSet == null) return null;

            return actionSet.Contains(spec.ActionSpecId)
                ? null
                : ScheduleCodes.SCHEDULE_ACTION_NOT_IN_ACTION_SET;
        }
    }
}
