using System;
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
        /// 创建并注册绑定到该机会的 Locked 反应计划。
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

            ActorLane lane = _authority.FindLane(reaction.OwnerUnitId);
            if (lane != null && lane.IsSubmissionLocked)
                return ReactionCodes.REACTION_LANE_SUBMISSION_LOCKED;
            if (lane != null && lane.TryFindOverlap(reaction.StartTick, reaction.IntervalLength, null, out _))
                return ReactionCodes.REACTION_LANE_INTERVAL_OCCUPIED;

            _authority.RegisterPlan(reaction);
            plan = reaction;
            return null;
        }
    }
}
