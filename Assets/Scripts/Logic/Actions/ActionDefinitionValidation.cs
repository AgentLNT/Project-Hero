using System;
using System.Collections.Generic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Actions
{
    /// <summary>
    /// 动作定义校验稳定错误码（任务 02 冻结；非法配置以稳定原因码拒绝，不得钳制后继续模拟）。
    /// </summary>
    public static class ActionDefinitionCodes
    {
        public const string ACTION_ID_INVALID = "ACTION_ID_INVALID";
        public const string INVALID_ACTION_TYPE_COMBINATION = "INVALID_ACTION_TYPE_COMBINATION";
        public const string ATTACK_TIMING_OUT_OF_RANGE = "ATTACK_TIMING_OUT_OF_RANGE";
        public const string ATTACK_ACTION_SPEED_INVALID = "ATTACK_ACTION_SPEED_INVALID";
        public const string ATTACK_DAMAGE_COMPONENT_INVALID = "ATTACK_DAMAGE_COMPONENT_INVALID";
        public const string ATTACK_FORCE_MULTIPLIER_INVALID = "ATTACK_FORCE_MULTIPLIER_INVALID";
        public const string ATTACK_TARGET_RELATION_MASK_EMPTY = "ATTACK_TARGET_RELATION_MASK_EMPTY";
        public const string ATTACK_TARGET_RELATION_MASK_INVALID = "ATTACK_TARGET_RELATION_MASK_INVALID";
        public const string ATTACK_TAGS_INVALID = "ATTACK_TAGS_INVALID";
        public const string ATTACK_PATTERN_INVALID = "ATTACK_PATTERN_INVALID";
        public const string GUARD_TIMING_INVALID = "GUARD_TIMING_INVALID";
        public const string GUARD_RESISTANCE_MUST_BE_PARTIAL = "GUARD_RESISTANCE_MUST_BE_PARTIAL";
        public const string GUARD_ELIGIBLE_TAGS_INVALID = "GUARD_ELIGIBLE_TAGS_INVALID";
        public const string MOVE_TIMING_INVALID = "MOVE_TIMING_INVALID";
        public const string MOVE_TIMING_OUT_OF_RANGE = "MOVE_TIMING_OUT_OF_RANGE";
        public const string MOVE_MAX_PATH_WEIGHT_INVALID = "MOVE_MAX_PATH_WEIGHT_INVALID";
        public const string MOVE_MAX_PATH_WEIGHT_EXCEEDS_SEARCH_LIMIT = "MOVE_MAX_PATH_WEIGHT_EXCEEDS_SEARCH_LIMIT";
        public const string MOVE_PATTERN_INVALID = "MOVE_PATTERN_INVALID";
        public const string BLOCK_TIMING_INVALID = "BLOCK_TIMING_INVALID";
        public const string BLOCK_ELIGIBLE_TAGS_INVALID = "BLOCK_ELIGIBLE_TAGS_INVALID";
        public const string DODGE_TIMING_INVALID = "DODGE_TIMING_INVALID";
        public const string DODGE_MAX_DISTANCE_INVALID = "DODGE_MAX_DISTANCE_INVALID";
        public const string ORDINARY_ACTION_CANNOT_COST_ADRENALINE = "ORDINARY_ACTION_CANNOT_COST_ADRENALINE";
        public const string REACTION_ACTION_REQUIRES_POSITIVE_COST = "REACTION_ACTION_REQUIRES_POSITIVE_COST";
    }

    /// <summary>
    /// ActionSpec 定义级校验。返回首个稳定错误码（null = 通过）。
    /// 速度相关校验（ActionSpeed/MoveSpeed）由 ValidateAttack / ValidateMove 的调用方
    /// 传入单位采样值；量化只在计划首次进入 Editable 时执行（任务 05），此处只做定义合法性。
    /// </summary>
    public static class ActionDefinitionValidation
    {
        public const AttackTagMask AllKnownAttackTags =
            AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable;

        public const DefenseTagMask AllKnownDefenseTags =
            DefenseTagMask.Blockable | DefenseTagMask.Guardable;

        /// <summary>闭合匹配矩阵：ActionType 必须与 Timing/Payload 具体类型一致。</summary>
        public static string ValidateTypePairing(ActionSpec spec)
        {
            switch (spec.Type)
            {
                case ActionType.Attack:
                    return spec.Timing is AttackTimingSpec && spec.Payload is AttackPayloadSpec
                        ? null : ActionDefinitionCodes.INVALID_ACTION_TYPE_COMBINATION;
                case ActionType.Guard:
                    return spec.Timing is GuardTimingSpec && spec.Payload is GuardPayloadSpec
                        ? null : ActionDefinitionCodes.INVALID_ACTION_TYPE_COMBINATION;
                case ActionType.Move:
                    return spec.Timing is MoveTimingSpec && spec.Payload is MovePayloadSpec
                        ? null : ActionDefinitionCodes.INVALID_ACTION_TYPE_COMBINATION;
                case ActionType.Block:
                    return spec.Timing is BlockReactionTimingSpec && spec.Payload is BlockPayloadSpec
                        ? null : ActionDefinitionCodes.INVALID_ACTION_TYPE_COMBINATION;
                case ActionType.Dodge:
                    return spec.Timing is DodgeReactionTimingSpec && spec.Payload is DodgePayloadSpec
                        ? null : ActionDefinitionCodes.INVALID_ACTION_TYPE_COMBINATION;
                default:
                    return ActionDefinitionCodes.INVALID_ACTION_TYPE_COMBINATION;
            }
        }

        /// <summary>普通动作（Attack/Guard/Move）费用必须为 0；Block/Dodge 必须为正。</summary>
        public static string ValidateAdrenalineCost(ActionSpec spec)
        {
            switch (spec.Type)
            {
                case ActionType.Attack:
                case ActionType.Guard:
                case ActionType.Move:
                    return spec.AdrenalineCost != 0
                        ? ActionDefinitionCodes.ORDINARY_ACTION_CANNOT_COST_ADRENALINE : null;
                case ActionType.Block:
                case ActionType.Dodge:
                    return spec.AdrenalineCost <= 0
                        ? ActionDefinitionCodes.REACTION_ACTION_REQUIRES_POSITIVE_COST : null;
                default:
                    return ActionDefinitionCodes.INVALID_ACTION_TYPE_COMBINATION;
            }
        }

        /// <summary>攻击定义校验（不含速度：速度合法性见 <see cref="ValidateAttackSpeed"/>）。</summary>
        public static string ValidateAttack(ActionSpec spec)
        {
            string pairing = ValidateTypePairing(spec);
            if (pairing != null) return pairing;

            var timing = (AttackTimingSpec)spec.Timing;
            var payload = (AttackPayloadSpec)spec.Payload;

            if (timing.BaseWindupTicks <= 0 || timing.RecoveryTicks <= 0)
                return ActionDefinitionCodes.ATTACK_TIMING_OUT_OF_RANGE;

            if (!IsFinitePositive(payload.ForceMultiplier))
                return ActionDefinitionCodes.ATTACK_FORCE_MULTIPLIER_INVALID;

            if (payload.DamageComponents == null || payload.DamageComponents.Count == 0)
                return ActionDefinitionCodes.ATTACK_DAMAGE_COMPONENT_INVALID;
            foreach (var component in payload.DamageComponents)
            {
                string componentError = DamageComponentValidation.Validate(component);
                if (componentError != null)
                    return componentError == DamageTagCodes.CONTRADICTORY_DAMAGE_TAGS ||
                           componentError == DamageTagCodes.DAMAGE_TAGS_INVALID
                        ? componentError
                        : ActionDefinitionCodes.ATTACK_DAMAGE_COMPONENT_INVALID;
            }

            if (payload.AllowedTargetRelations == TargetRelationMask.None)
                return ActionDefinitionCodes.ATTACK_TARGET_RELATION_MASK_EMPTY;
            if (!TargetRelationMasks.IsValid(payload.AllowedTargetRelations))
                return ActionDefinitionCodes.ATTACK_TARGET_RELATION_MASK_INVALID;

            if (((int)payload.Tags & ~(int)AllKnownAttackTags) != 0)
                return ActionDefinitionCodes.ATTACK_TAGS_INVALID;
            if ((payload.Tags & AttackTagMask.Reactable) != 0 &&
                (payload.Tags & (AttackTagMask.Blockable | AttackTagMask.Dodgeable)) == 0)
                return ActionDefinitionCodes.ATTACK_TAGS_INVALID;

            if (payload.Pattern == null ||
                DirectionalSpecValidation.ValidateDirections(payload.Pattern.Directions) != null)
                return ActionDefinitionCodes.ATTACK_PATTERN_INVALID;

            if (DefinitionIdValidation.ValidateFormat(spec.ActionSpecId.Value) != null)
                return ActionDefinitionCodes.ACTION_ID_INVALID;
            if (DefinitionIdValidation.ValidateFormat(payload.ImpactProfileId.Value) != null)
                return ActionDefinitionCodes.ATTACK_DAMAGE_COMPONENT_INVALID;

            return null;
        }

        /// <summary>ActionSpeed 必须为有限正数（主方案 3.1.2）。</summary>
        public static string ValidateAttackSpeed(float actionSpeed)
            => IsFinitePositive(actionSpeed) ? null : ActionDefinitionCodes.ATTACK_ACTION_SPEED_INVALID;

        /// <summary>Guard 定义校验：时序全正、合格标签合法、分通道/动量抵抗严格位于 [1,1023]。</summary>
        public static string ValidateGuard(ActionSpec spec)
        {
            string pairing = ValidateTypePairing(spec);
            if (pairing != null) return pairing;

            var timing = (GuardTimingSpec)spec.Timing;
            if (timing.WindupTicks <= 0 || timing.ActiveTicks <= 0 || timing.RecoveryTicks <= 0)
                return ActionDefinitionCodes.GUARD_TIMING_INVALID;

            var payload = (GuardPayloadSpec)spec.Payload;
            string tagsError = ValidateDefenseTags(payload.EligibleTags, ActionDefinitionCodes.GUARD_ELIGIBLE_TAGS_INVALID);
            if (tagsError != null) return tagsError;

            if (payload.MomentumResistanceQ10 < 1 || payload.MomentumResistanceQ10 > 1023)
                return ActionDefinitionCodes.GUARD_RESISTANCE_MUST_BE_PARTIAL;

            if (payload.DamageResistanceQ10 != null)
            {
                foreach (var pair in payload.DamageResistanceQ10)
                {
                    if (DefinitionIdValidation.ValidateFormat(pair.Key.Value) != null)
                        return ActionDefinitionCodes.GUARD_RESISTANCE_MUST_BE_PARTIAL;
                    if (pair.Value < 1 || pair.Value > 1023)
                        return ActionDefinitionCodes.GUARD_RESISTANCE_MUST_BE_PARTIAL;
                }
            }

            return null;
        }

        /// <summary>Move 定义校验（不含 MoveSpeed 采样；速度合法性见 <see cref="ValidateMoveSpeed"/>）。</summary>
        public static string ValidateMove(ActionSpec spec, BattleRules rules)
        {
            string pairing = ValidateTypePairing(spec);
            if (pairing != null) return pairing;

            var timing = (MoveTimingSpec)spec.Timing;
            if (timing.BaseStepTicks <= 0 || timing.RecoveryTicks <= 0)
                return ActionDefinitionCodes.MOVE_TIMING_INVALID;

            var payload = (MovePayloadSpec)spec.Payload;
            if (payload.MaxPathWeightUnits <= 0)
                return ActionDefinitionCodes.MOVE_MAX_PATH_WEIGHT_INVALID;
            if (payload.MaxPathWeightUnits > rules.PathSearchRules.MaxPathWeightUnits)
                return ActionDefinitionCodes.MOVE_MAX_PATH_WEIGHT_EXCEEDS_SEARCH_LIMIT;

            if (payload.Pattern == null ||
                DirectionalSpecValidation.ValidateDirections(payload.Pattern.Directions) != null)
                return ActionDefinitionCodes.MOVE_PATTERN_INVALID;

            return null;
        }

        /// <summary>MoveSpeed 必须为有限正数。</summary>
        public static string ValidateMoveSpeed(float moveSpeed)
            => IsFinitePositive(moveSpeed) ? null : ActionDefinitionCodes.MOVE_TIMING_OUT_OF_RANGE;

        /// <summary>Block 定义校验：时序全正、合格标签合法。合格抵抗固定 1024，Authoring 无降低字段。</summary>
        public static string ValidateBlock(ActionSpec spec)
        {
            string pairing = ValidateTypePairing(spec);
            if (pairing != null) return pairing;

            var timing = (BlockReactionTimingSpec)spec.Timing;
            if (timing.ReactionWindupTicks <= 0 || timing.RecoveryTicks <= 0)
                return ActionDefinitionCodes.BLOCK_TIMING_INVALID;

            var payload = (BlockPayloadSpec)spec.Payload;
            string tagsError = ValidateDefenseTags(payload.EligibleTags, ActionDefinitionCodes.BLOCK_ELIGIBLE_TAGS_INVALID);
            if (tagsError != null) return tagsError;

            return null;
        }

        /// <summary>Dodge 定义校验：时序全正、最大距离为正。</summary>
        public static string ValidateDodge(ActionSpec spec)
        {
            string pairing = ValidateTypePairing(spec);
            if (pairing != null) return pairing;

            var timing = (DodgeReactionTimingSpec)spec.Timing;
            if (timing.ReactionWindupTicks <= 0 || timing.RecoveryTicks <= 0)
                return ActionDefinitionCodes.DODGE_TIMING_INVALID;

            var payload = (DodgePayloadSpec)spec.Payload;
            if (payload.MaxDistanceSteps <= 0)
                return ActionDefinitionCodes.DODGE_MAX_DISTANCE_INVALID;

            if (payload.Pattern == null ||
                DirectionalSpecValidation.ValidateDirections(payload.Pattern.Directions) != null)
                return ActionDefinitionCodes.MOVE_PATTERN_INVALID;

            return null;
        }

        /// <summary>完整定义校验（分派）。速度参数为“计划创建时”的采样值；非本任务范围时传 null 跳过。</summary>
        public static string ValidateActionSpec(
            ActionSpec spec, BattleRules rules, float? actionSpeed, float? moveSpeed)
        {
            string pairing = ValidateTypePairing(spec);
            if (pairing != null) return pairing;

            string costError = ValidateAdrenalineCost(spec);
            if (costError != null) return costError;

            switch (spec.Type)
            {
                case ActionType.Attack:
                    if (actionSpeed.HasValue)
                    {
                        string speedError = ValidateAttackSpeed(actionSpeed.Value);
                        if (speedError != null) return speedError;
                    }
                    return ValidateAttack(spec);
                case ActionType.Guard:
                    return ValidateGuard(spec);
                case ActionType.Move:
                    if (moveSpeed.HasValue)
                    {
                        string speedError = ValidateMoveSpeed(moveSpeed.Value);
                        if (speedError != null) return speedError;
                    }
                    return ValidateMove(spec, rules);
                case ActionType.Block:
                    return ValidateBlock(spec);
                case ActionType.Dodge:
                    return ValidateDodge(spec);
                default:
                    return ActionDefinitionCodes.INVALID_ACTION_TYPE_COMBINATION;
            }
        }

        private static string ValidateDefenseTags(DefenseTagMask tags, string errorCode)
        {
            if (tags == DefenseTagMask.None) return errorCode;
            if (((int)tags & ~(int)AllKnownDefenseTags) != 0)
                return errorCode;
            return null;
        }

        private static bool IsFinitePositive(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }
}
