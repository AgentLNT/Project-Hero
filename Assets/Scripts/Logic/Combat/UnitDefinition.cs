using System.Collections.Generic;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 单位定义（主方案 3.1.1）。Mass × MomentumSpeed 只服务动量/硬直/击倒/击退量化；
    /// ActionSpeed 只修正攻击前摇；MoveSpeed 只修正普通 Move 每权重单位 Tick，不参与选路。
    /// 被动抵抗按 DamageChannelId 索引，范围 [0, 1024]（Q10），缺失通道视为 0；
    /// 不存在通用 DamageReduction。
    /// </summary>
    public sealed record UnitDefinition(
        UnitDefinitionId UnitDefinitionId,
        float Mass,
        float MomentumSpeed,
        float ActionSpeed,
        float MoveSpeed,
        IReadOnlyDictionary<DamageChannelId, int> BaseDamageResistanceQ10)
    {
        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("unit.definition_id", UnitDefinitionId.Value ?? string.Empty);
            writer.Write("unit.mass", (double)Mass);
            writer.Write("unit.momentum_speed", (double)MomentumSpeed);
            writer.Write("unit.action_speed", (double)ActionSpeed);
            writer.Write("unit.move_speed", (double)MoveSpeed);

            if (BaseDamageResistanceQ10 != null)
            {
                var channels = new List<DamageChannelId>(BaseDamageResistanceQ10.Keys);
                channels.Sort((a, b) => System.StringComparer.Ordinal.Compare(a.Value, b.Value));
                foreach (var channel in channels)
                {
                    writer.Write("unit.base_resistance." + channel.Value, BaseDamageResistanceQ10[channel]);
                }
            }
        }
    }

    /// <summary>
    /// 单位初始快照（主方案 3.1.1 / 00 号规则 31）：
    /// 携带首版不可变的 FactionId、初始位置/朝向、AvailableAdrenaline 与 AdrenalineCycleId。
    /// </summary>
    public sealed record UnitInitialSnapshot(
        UnitDefinitionId DefinitionId,
        FactionId FactionId,
        GridPoint InitialPosition,
        GridDirection InitialFacing,
        float InitialHealth,
        int AvailableAdrenaline,
        long AdrenalineCycleId);

    public static class UnitDefinitionCodes
    {
        public const string UNIT_ID_INVALID = "UNIT_ID_INVALID";
        public const string UNIT_MASS_INVALID = "UNIT_MASS_INVALID";
        public const string UNIT_MOMENTUM_SPEED_INVALID = "UNIT_MOMENTUM_SPEED_INVALID";
        public const string UNIT_ACTION_SPEED_INVALID = "UNIT_ACTION_SPEED_INVALID";
        public const string UNIT_MOVE_SPEED_INVALID = "UNIT_MOVE_SPEED_INVALID";
        public const string UNIT_RESISTANCE_OUT_OF_RANGE = "UNIT_RESISTANCE_OUT_OF_RANGE";
        public const string UNIT_RESISTANCE_CHANNEL_INVALID = "UNIT_RESISTANCE_CHANNEL_INVALID";
    }

    public static class UnitDefinitionValidation
    {
        /// <summary>
        /// 单位定义校验：Mass/MomentumSpeed/ActionSpeed/MoveSpeed 有限正数；
        /// 分通道被动抵抗位于 [0, 1024]；通道 ID 格式合法。
        /// </summary>
        public static string Validate(UnitDefinition definition)
        {
            if (DefinitionIdValidation.ValidateFormat(definition.UnitDefinitionId.Value) != null)
                return UnitDefinitionCodes.UNIT_ID_INVALID;

            if (!IsFinitePositive(definition.Mass)) return UnitDefinitionCodes.UNIT_MASS_INVALID;
            if (!IsFinitePositive(definition.MomentumSpeed)) return UnitDefinitionCodes.UNIT_MOMENTUM_SPEED_INVALID;
            if (!IsFinitePositive(definition.ActionSpeed)) return UnitDefinitionCodes.UNIT_ACTION_SPEED_INVALID;
            if (!IsFinitePositive(definition.MoveSpeed)) return UnitDefinitionCodes.UNIT_MOVE_SPEED_INVALID;

            if (definition.BaseDamageResistanceQ10 != null)
            {
                foreach (var pair in definition.BaseDamageResistanceQ10)
                {
                    if (DefinitionIdValidation.ValidateFormat(pair.Key.Value) != null)
                        return UnitDefinitionCodes.UNIT_RESISTANCE_CHANNEL_INVALID;
                    if (pair.Value < 0 || pair.Value > 1024)
                        return UnitDefinitionCodes.UNIT_RESISTANCE_OUT_OF_RANGE;
                }
            }
            return null;
        }

        private static bool IsFinitePositive(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }
}
