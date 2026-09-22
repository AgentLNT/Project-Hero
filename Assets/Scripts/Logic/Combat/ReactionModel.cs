using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 反应规则（主方案 2.3 / 0.4.1）。输入提前量与最小反应提前量必须为正；
    /// 进入 BattleDefinitionHash。机会运行时创建与命令处理由任务 05/09 实现。
    /// </summary>
    public sealed record ReactionRules(int CommandIngressLeadTicks, int MinimumReactionLeadTicks)
    {
        public const string REACTION_INGRESS_LEAD_INVALID = "REACTION_INGRESS_LEAD_INVALID";
        public const string REACTION_MINIMUM_LEAD_INVALID = "REACTION_MINIMUM_LEAD_INVALID";

        public string Validate()
            => CommandIngressLeadTicks <= 0 ? REACTION_INGRESS_LEAD_INVALID
                : MinimumReactionLeadTicks <= 0 ? REACTION_MINIMUM_LEAD_INVALID
                : null;

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("reaction_rules.command_ingress_lead_ticks", CommandIngressLeadTicks);
            writer.Write("reaction_rules.minimum_reaction_lead_ticks", MinimumReactionLeadTicks);
        }
    }

    /// <summary>
    /// 肾上腺素规则（主方案 2.3 / 00 号规则 27）。冻结值（01B 拍板 B3）：
    /// MaxAvailablePerCycle = 100、DamageDealtGainQ10 = 102、DamageReceivedGainQ10 = 205、
    /// SuccessfulBlockReward = 1、SuccessfulDodgeReward = 0、ClashReward = 50；
    /// Block 费 = 2、Dodge 费 = 1。获得率/奖励非负；每个反应成本不超过上限；
    /// Block/Dodge 成功奖励分别小于所有对应动作成本。全部进入 BattleDefinitionHash。
    /// </summary>
    public sealed record AdrenalineRules(
        int MaxAvailablePerCycle,
        int DamageDealtGainQ10,
        int DamageReceivedGainQ10,
        int SuccessfulBlockReward,
        int SuccessfulDodgeReward,
        int ClashReward)
    {
        public const string ADRENALINE_MAX_AVAILABLE_INVALID = "ADRENALINE_MAX_AVAILABLE_INVALID";
        public const string ADRENALINE_GAIN_NEGATIVE = "ADRENALINE_GAIN_NEGATIVE";
        public const string ADRENALINE_REWARD_NEGATIVE = "ADRENALINE_REWARD_NEGATIVE";
        public const string ADRENALINE_COST_EXCEEDS_CYCLE_MAXIMUM = "ADRENALINE_COST_EXCEEDS_CYCLE_MAXIMUM";
        public const string ADRENALINE_BLOCK_REWARD_NOT_BELOW_COST = "ADRENALINE_BLOCK_REWARD_NOT_BELOW_COST";
        public const string ADRENALINE_DODGE_REWARD_NOT_BELOW_COST = "ADRENALINE_DODGE_REWARD_NOT_BELOW_COST";

        /// <summary>01B 拍板 B3 冻结值。</summary>
        public static readonly AdrenalineRules FrozenV1 = new(100, 102, 205, 1, 0, 50);

        /// <summary>基础校验：上限为正、获得率与奖励非负。</summary>
        public string Validate()
        {
            if (MaxAvailablePerCycle <= 0) return ADRENALINE_MAX_AVAILABLE_INVALID;
            if (DamageDealtGainQ10 < 0 || DamageReceivedGainQ10 < 0) return ADRENALINE_GAIN_NEGATIVE;
            if (SuccessfulBlockReward < 0 || SuccessfulDodgeReward < 0 || ClashReward < 0)
                return ADRENALINE_REWARD_NEGATIVE;
            return null;
        }

        /// <summary>
        /// 单个反应动作费用校验：费用不得超过周期上限；
        /// Block/Dodge 成功奖励必须严格小于对应动作费用。
        /// </summary>
        public string ValidateReactionCost(ActionType reactionType, int cost)
        {
            if (cost > MaxAvailablePerCycle) return ADRENALINE_COST_EXCEEDS_CYCLE_MAXIMUM;
            switch (reactionType)
            {
                case ActionType.Block:
                    return SuccessfulBlockReward >= cost ? ADRENALINE_BLOCK_REWARD_NOT_BELOW_COST : null;
                case ActionType.Dodge:
                    return SuccessfulDodgeReward >= cost ? ADRENALINE_DODGE_REWARD_NOT_BELOW_COST : null;
                default:
                    return ActionDefinitionCodes.INVALID_ACTION_TYPE_COMBINATION;
            }
        }

        /// <summary>对动作集合逐一校验「每个反应成本不超过上限、奖励小于所有对应动作成本」。</summary>
        public string ValidateAgainstActionCosts(IEnumerable<ActionSpec> specs)
        {
            foreach (var spec in specs)
            {
                if (spec.Type == ActionType.Block || spec.Type == ActionType.Dodge)
                {
                    string error = ValidateReactionCost(spec.Type, spec.AdrenalineCost);
                    if (error != null) return error;
                }
            }
            return null;
        }

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("adrenaline_rules.max_available_per_cycle", MaxAvailablePerCycle);
            writer.Write("adrenaline_rules.damage_dealt_gain_q10", DamageDealtGainQ10);
            writer.Write("adrenaline_rules.damage_received_gain_q10", DamageReceivedGainQ10);
            writer.Write("adrenaline_rules.successful_block_reward", SuccessfulBlockReward);
            writer.Write("adrenaline_rules.successful_dodge_reward", SuccessfulDodgeReward);
            writer.Write("adrenaline_rules.clash_reward", ClashReward);
        }
    }

    /// <summary>反应选项：按 ActionSpecId 排序，各带截止 Tick（ResponseDeadlineTick）。</summary>
    public sealed record ReactionOption(
        ActionSpecId ReactionActionSpecId,
        ActionType ReactionType,
        long ResponseDeadlineTick);

    public enum ReactionOpportunityState
    {
        Open = 0,
        Accepted = 1,
        Triggered = 2,
        Expired = 3,
        SourceCancelled = 4,
        BattleEnded = 5
    }

    /// <summary>
    /// 反应机会纯数据（主方案 0.4.1）。TriggerTick = 来源攻击 ImpactTick（Logic 固定推导）；
    /// 选项按 ActionSpecId（StringComparer.Ordinal）排序。机会运行时创建与命令处理由任务 05/09 实现。
    /// </summary>
    public sealed record ReactionOpportunity(
        ReactionOpportunityId ReactionOpportunityId,
        ActionPlanId SourceAttackPlanId,
        UnitId DefenderUnitId,
        long TelegraphTick,
        long TriggerTick,
        IReadOnlyList<ReactionOption> Options,
        ReactionOpportunityState State);

    public static class ReactionOpportunityData
    {
        public const string REACTION_OPTION_EMPTY = "REACTION_OPTION_EMPTY";
        public const string REACTION_OPTION_DUPLICATE = "REACTION_OPTION_DUPLICATE";
        public const string REACTION_OPTION_INVALID_DEADLINE = "REACTION_OPTION_INVALID_DEADLINE";

        /// <summary>
        /// 选项校验：非空、ActionSpecId 唯一、截止 Tick 为正；
        /// 规范形式按 ActionSpecId 使用 StringComparer.Ordinal 升序。
        /// </summary>
        public static string ValidateOptions(IReadOnlyList<ReactionOption> options)
        {
            if (options == null || options.Count == 0) return REACTION_OPTION_EMPTY;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in options)
            {
                if (DefinitionIdValidation.ValidateFormat(option.ReactionActionSpecId.Value) != null)
                    return REACTION_OPTION_DUPLICATE;
                if (option.ResponseDeadlineTick <= 0) return REACTION_OPTION_INVALID_DEADLINE;
                if (!seen.Add(option.ReactionActionSpecId.Value)) return REACTION_OPTION_DUPLICATE;
            }
            return null;
        }

        /// <summary>生成按 ActionSpecId（Ordinal）升序的规范选项列表。</summary>
        public static List<ReactionOption> CanonicalizeOptions(IReadOnlyList<ReactionOption> options)
        {
            string error = ValidateOptions(options);
            if (error != null)
                throw new ProjectHero.Logic.LogicDefinitionException(error);

            var list = new List<ReactionOption>(options);
            list.Sort((a, b) => StringComparer.Ordinal.Compare(
                a.ReactionActionSpecId.Value, b.ReactionActionSpecId.Value));
            return list;
        }
    }

    /// <summary>命令判别式纯数据（主方案 2.2 / 00 号规则 18）。</summary>
    public abstract record CommandScope;

    /// <summary>普通排程编辑：必须验证 ExpectedScheduleRevision；新增/增加预算时窗口必填。</summary>
    public sealed record ScheduleEditScope(long ExpectedScheduleRevision, WindowId? ExpectedWindowId) : CommandScope;

    /// <summary>反应命令：必须验证 ReactionOpportunityId。</summary>
    public sealed record ReactionCommandScope(ReactionOpportunityId ReactionOpportunityId) : CommandScope;

    /// <summary>
    /// 反应肾上腺素预留（按 ActionPlan 归属、带 ReservationCycleId 的独立数据；00 号规则 27）。
    /// 接受时从 Available 转入预留，TriggerTick 消费；账本事务由任务 07 实现。
    /// </summary>
    public sealed record AdrenalineReservation(
        ActionPlanId ActionPlanId,
        int ReservedAmount,
        long ReservationCycleId)
    {
        public const string ADRENALINE_RESERVATION_INVALID = "ADRENALINE_RESERVATION_INVALID";

        public string Validate()
            => !ActionPlanId.IsValid || ReservedAmount <= 0 || ReservationCycleId < 0
                ? ADRENALINE_RESERVATION_INVALID : null;
    }
}
