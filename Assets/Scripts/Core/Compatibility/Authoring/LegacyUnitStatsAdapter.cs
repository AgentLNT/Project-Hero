using System;
using System.Collections.Generic;
using ProjectHero.Core.Entities;
using ProjectHero.Logic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Core.Compatibility.Authoring
{
    /// <summary>
    /// 旧 CombatUnit 属性 → 纯逻辑 UnitDefinition 的兼容转换（主方案 3.1.1 / 00 号规则 24）。
    /// 映射一次性完成，战斗开始后不得回读旧 MonoBehaviour：
    /// - 旧 TotalMass → Mass；
    /// - 旧 Swiftness → 一次性拆为 MomentumSpeed（冲击阈值）与 ActionSpeed / MoveSpeed（动作时序）三个独立新字段；
    /// - 旧 ArmorDefense → 只写入明确物理通道（physical.blunt/slash/pierce）的 BaseDamageResistanceQ10
    ///   （1024 × armor / (armor + 100)）；
    /// - 旧 MagicResistance → elemental.fire/frost/lightning 三通道同公式（01B 拍板 B1）；
    /// - arcane 通道默认 0（本期无旧字段对应）。
    /// 不生成通用 DamageReduction；非法输入以稳定原因码拒绝。
    /// </summary>
    public static class LegacyUnitStatsAdapter
    {
        public const string LEGACY_MOMENTUM_INPUT_INVALID = LegacyAuthoringCodes.LEGACY_MOMENTUM_INPUT_INVALID;

        /// <summary>纯输入版本（可测试、无场景依赖）。</summary>
        public static UnitDefinition ConvertStats(
            string unitDefinitionId,
            float totalMass,
            float swiftness,
            float armorDefense,
            float magicResistance)
        {
            if (!IsFinitePositive(totalMass) || !IsFinitePositive(swiftness))
                throw new LogicDefinitionException(LEGACY_MOMENTUM_INPUT_INVALID,
                    $"totalMass={totalMass}, swiftness={swiftness}");

            var resistances = new Dictionary<DamageChannelId, int>
            {
                [DamageChannels.PhysicalBlunt] = LegacyDamageMapping.ResistanceQ10FromLegacyDefense(armorDefense),
                [DamageChannels.PhysicalSlash] = LegacyDamageMapping.ResistanceQ10FromLegacyDefense(armorDefense),
                [DamageChannels.PhysicalPierce] = LegacyDamageMapping.ResistanceQ10FromLegacyDefense(armorDefense),
                [DamageChannels.ElementalFire] = LegacyDamageMapping.ResistanceQ10FromLegacyDefense(magicResistance),
                [DamageChannels.ElementalFrost] = LegacyDamageMapping.ResistanceQ10FromLegacyDefense(magicResistance),
                [DamageChannels.ElementalLightning] = LegacyDamageMapping.ResistanceQ10FromLegacyDefense(magicResistance),
                [DamageChannels.Arcane] = 0
            };

            return new UnitDefinition(
                new UnitDefinitionId(unitDefinitionId),
                totalMass,      // Mass
                swiftness,      // MomentumSpeed（冲击阈值）
                swiftness,      // ActionSpeed（动作时序）
                swiftness,      // MoveSpeed（动作时序）
                resistances);
        }

        /// <summary>场景 CombatUnit 版本：读取旧字段并委托纯输入版本。</summary>
        public static UnitDefinition ConvertCombatUnit(string unitDefinitionId, CombatUnit unit)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            return ConvertStats(
                unitDefinitionId,
                unit.TotalMass,
                unit.Swiftness,
                unit.ArmorDefense,
                unit.MagicResistance);
        }

        private static bool IsFinitePositive(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }
}
