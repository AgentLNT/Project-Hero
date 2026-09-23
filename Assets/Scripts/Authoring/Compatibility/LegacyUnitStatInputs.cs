using System;
using ProjectHero.Logic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Authoring.Compatibility
{
    /// <summary>
    /// 旧 <c>CombatUnit</c> 的纯数值子集。
    ///
    /// 为什么需要它：<c>CombatUnit</c> 是 MonoBehaviour，编译在 Assembly-CSharp 里，
    /// 而自定义 asmdef <strong>不允许</strong>引用 Assembly-CSharp（00 号规则 17）。
    /// 因此 Authoring 不能直接吃 MonoBehaviour：旧程序集负责把组件字段读成这个数值结构
    /// （见 <c>Assets/Scripts/Core/Compatibility/Authoring/LegacyCombatUnitStatsReader.cs</c>），
    /// Authoring 只负责纯数值 → <see cref="UnitDefinition"/> 的映射。
    ///
    /// 旧派生公式在这里一次性复刻（characterization，任务 01 §1.3 / 运行时记录）：
    /// <code>
    /// TotalMass  = 50 + Strength×2 + Constitution×2 + ArmorWeight
    /// Swiftness  = (Dexterity×0.75 + Strength×0.25)   // 力竭时 ×0.5
    /// MaxHealth  = Constitution×20
    /// </code>
    /// 这些公式只在加载边界求值一次，之后不得回读旧组件，也不得在 Logic 里再实现第二套。
    /// </summary>
    public readonly struct LegacyUnitStatInputs
    {
        public readonly float Strength;
        public readonly float Dexterity;
        public readonly float Constitution;
        public readonly float ArmorWeight;
        public readonly float ArmorDefense;
        public readonly float MagicResistance;
        public readonly bool IsExhausted;

        public LegacyUnitStatInputs(
            float strength,
            float dexterity,
            float constitution,
            float armorWeight,
            float armorDefense,
            float magicResistance,
            bool isExhausted)
        {
            Strength = strength;
            Dexterity = dexterity;
            Constitution = constitution;
            ArmorWeight = armorWeight;
            ArmorDefense = armorDefense;
            MagicResistance = magicResistance;
            IsExhausted = isExhausted;
        }

        /// <summary>旧 <c>CombatUnit.TotalMass</c>。</summary>
        public float TotalMass => 50f + (Strength * 2f) + (Constitution * 2f) + ArmorWeight;

        /// <summary>旧 <c>CombatUnit.Swiftness</c>（力竭时减半）。</summary>
        public float Swiftness
        {
            get
            {
                float baseValue = (Dexterity * 0.75f) + (Strength * 0.25f);
                return IsExhausted ? baseValue * 0.5f : baseValue;
            }
        }

        /// <summary>旧 <c>CombatUnit.MaxHealth</c>，即首版初始生命。</summary>
        public float MaxHealth => Constitution * 20f;
    }

    /// <summary>
    /// 旧单位属性 → Logic <c>UnitDefinition</c> 的纯数值转换。
    /// 一次性映射；不生成通用 DamageReduction；非法输入以稳定原因码拒绝。
    /// </summary>
    public static class LegacyUnitStatsMapping
    {
        public const string LEGACY_MOMENTUM_INPUT_INVALID = "LEGACY_MOMENTUM_INPUT_INVALID";
        public const string LEGACY_RESISTANCE_INPUT_INVALID = "LEGACY_RESISTANCE_INPUT_INVALID";
        public const string LEGACY_HEALTH_INPUT_INVALID = "LEGACY_HEALTH_INPUT_INVALID";

        public static UnitDefinition Convert(
            string unitDefinitionId,
            LegacyUnitStatInputs inputs,
            string actionSetId)
        {
            float totalMass = inputs.TotalMass;
            float swiftness = inputs.Swiftness;
            float maxHealth = inputs.MaxHealth;

            if (!IsFinitePositive(totalMass) || !IsFinitePositive(swiftness))
                throw new LogicDefinitionException(LEGACY_MOMENTUM_INPUT_INVALID,
                    $"totalMass={totalMass}, swiftness={swiftness}");
            if (!IsFinitePositive(maxHealth))
                throw new LogicDefinitionException(LEGACY_HEALTH_INPUT_INVALID, $"maxHealth={maxHealth}");

            int armorResistance = ResistanceQ10FromLegacyDefense(inputs.ArmorDefense);
            int magicResistance = ResistanceQ10FromLegacyDefense(inputs.MagicResistance);

            var resistances = new System.Collections.Generic.Dictionary<DamageChannelId, int>
            {
                [DamageChannels.PhysicalBlunt] = armorResistance,
                [DamageChannels.PhysicalSlash] = armorResistance,
                [DamageChannels.PhysicalPierce] = armorResistance,
                [DamageChannels.ElementalFire] = magicResistance,
                [DamageChannels.ElementalFrost] = magicResistance,
                [DamageChannels.ElementalLightning] = magicResistance,
                [DamageChannels.Arcane] = 0
            };

            return new UnitDefinition(
                new UnitDefinitionId(unitDefinitionId),
                totalMass,      // Mass
                swiftness,      // MomentumSpeed（冲击阈值）
                swiftness,      // ActionSpeed（动作时序）
                swiftness,      // MoveSpeed（动作时序）
                resistances,
                maxHealth,
                new ActionSetId(actionSetId));
        }

        /// <summary>
        /// 旧非线性护甲/魔抗公式只在 Authoring 边界一次性折算为 Q10 被动抵抗：
        /// <c>R = RoundHalfUp(1024 × d / (d + 100))</c>，d 为有限非负浮点，结果限定 [0, 1024]。
        /// 只写入明确通道；不再生成通用 DamageReduction（00 号规则 24 / 01B 拍板 B1）。
        /// </summary>
        public static int ResistanceQ10FromLegacyDefense(float defense)
        {
            if (float.IsNaN(defense) || float.IsInfinity(defense) || defense < 0f)
                throw new LogicDefinitionException(LEGACY_RESISTANCE_INPUT_INVALID, $"defense={defense}");

            double q10 = 1024.0 * defense / (defense + 100.0);
            long rounded = (long)Math.Round(q10, MidpointRounding.AwayFromZero);
            if (rounded < 0L || rounded > 1024L)
                throw new LogicDefinitionException(LEGACY_RESISTANCE_INPUT_INVALID,
                    $"defense={defense}, q10={rounded}");
            return (int)rounded;
        }

        private static bool IsFinitePositive(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }
}
