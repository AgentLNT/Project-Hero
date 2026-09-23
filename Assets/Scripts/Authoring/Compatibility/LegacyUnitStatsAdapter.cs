using System;
using System.Collections.Generic;
using ProjectHero.Logic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Authoring.Compatibility
{
    /// <summary>
    /// 旧单位属性（纯输入版本）→ Logic <c>UnitDefinition</c>。
    ///
    /// 本适配器只接受<strong>数值</strong>，不接受 MonoBehaviour：
    /// 组件字段由 Assembly-CSharp 侧的读取器解析成 <see cref="LegacyUnitStatInputs"/>
    /// （自定义 asmdef 不能引用 Assembly-CSharp，00 号规则 17）。
    ///
    /// 映射（任务 02「必须产出」9 + 02B 扩展）：
    /// <list type="bullet">
    /// <item>旧 TotalMass → Mass；</item>
    /// <item>旧 Swiftness → MomentumSpeed（冲击阈值）/ ActionSpeed / MoveSpeed 三路拆分（当前同一值）；</item>
    /// <item>旧 ArmorDefense → physical.blunt/slash/pierce 的 BaseDamageResistanceQ10；</item>
    /// <item>旧 MagicResistance → elemental.fire/frost/lightning 同公式（01B 拍板 B1）；arcane = 0；</item>
    /// <item>旧 MaxHealth → InitialHealth（首版初始生命，进入哈希）；</item>
    /// <item>动作集合 ID 由调用方显式注入（不按控制者类型挑选）。</item>
    /// </list>
    /// 非法输入以稳定原因码拒绝；不生成通用 DamageReduction。
    /// </summary>
    public static class LegacyUnitStatsAdapter
    {
        public const string LEGACY_MOMENTUM_INPUT_INVALID = LegacyAuthoringCodes.LEGACY_MOMENTUM_INPUT_INVALID;

        /// <summary>纯数值输入版本（可测试、无场景依赖）。</summary>
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

            return new UnitDefinition(
                new UnitDefinitionId(unitDefinitionId),
                totalMass,
                swiftness,
                swiftness,
                swiftness,
                BuildResistances(armorDefense, magicResistance));
        }

        /// <summary>
        /// 02B 扩展版本：显式给出 <paramref name="maxHealth"/> 与 <paramref name="actionSetId"/>。
        /// 两者都以参数注入而不是在 Logic 侧推导（Logic 不得实现第二套生命公式，
        /// 也不得按玩家/AI 控制类型挑选动作集合）。
        /// </summary>
        public static UnitDefinition ConvertStats(
            string unitDefinitionId,
            float totalMass,
            float swiftness,
            float armorDefense,
            float magicResistance,
            float maxHealth,
            string actionSetId)
        {
            if (!IsFinitePositive(maxHealth))
                throw new LogicDefinitionException(LegacyAuthoringCodes.LEGACY_HEALTH_INPUT_INVALID,
                    $"maxHealth={maxHealth}");

            if (!IsFinitePositive(totalMass) || !IsFinitePositive(swiftness))
                throw new LogicDefinitionException(LEGACY_MOMENTUM_INPUT_INVALID,
                    $"totalMass={totalMass}, swiftness={swiftness}");

            return new UnitDefinition(
                new UnitDefinitionId(unitDefinitionId),
                totalMass,
                swiftness,
                swiftness,
                swiftness,
                BuildResistances(armorDefense, magicResistance),
                maxHealth,
                new ActionSetId(actionSetId));
        }

        /// <summary>由旧组件数值子集直接构建（02B 正式路径）。</summary>
        public static UnitDefinition ConvertStats(
            string unitDefinitionId,
            LegacyUnitStatInputs inputs,
            string actionSetId)
            => LegacyUnitStatsMapping.Convert(unitDefinitionId, inputs, actionSetId);

        private static Dictionary<DamageChannelId, int> BuildResistances(float armorDefense, float magicResistance)
        {
            int armor = LegacyUnitStatsMapping.ResistanceQ10FromLegacyDefense(armorDefense);
            int magic = LegacyUnitStatsMapping.ResistanceQ10FromLegacyDefense(magicResistance);
            return new Dictionary<DamageChannelId, int>
            {
                [DamageChannels.PhysicalBlunt] = armor,
                [DamageChannels.PhysicalSlash] = armor,
                [DamageChannels.PhysicalPierce] = armor,
                [DamageChannels.ElementalFire] = magic,
                [DamageChannels.ElementalFrost] = magic,
                [DamageChannels.ElementalLightning] = magic,
                [DamageChannels.Arcane] = 0
            };
        }

        private static bool IsFinitePositive(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }
}
