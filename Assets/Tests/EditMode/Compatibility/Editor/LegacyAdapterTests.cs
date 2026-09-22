using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Combat;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Physics;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Factions;
using UnityEditor;
using UnityEngine;

namespace ProjectHero.Tests.Compatibility.Editor
{
    /// <summary>
    /// 旧程序集（Assembly-CSharp）兼容适配器的 EditMode 测试。
    /// 该目录无 asmdef（编译进 Assembly-CSharp-Editor，可引用旧运行时类型）；
    /// 自定义 asmdef 不能引用 Assembly-CSharp，因此适配器测试只能放这里（任务 02 规格）。
    /// </summary>
    public class LegacyAdapterTests
    {
        private const string RealLibraryPath = "Assets/Resources/ActionLibrary/ForRadius1.asset";
        private const string SlashPatternR1Path = "Assets/Resources/GeneratedActions/Pattern_Slash_R1_0.asset";

        private static AttackPattern LoadRealPattern()
        {
            var pattern = AssetDatabase.LoadAssetAtPath<AttackPattern>(SlashPatternR1Path);
            Assume.That(pattern, Is.Not.Null, $"真实 Pattern 资产缺失：{SlashPatternR1Path}");
            return pattern;
        }

        private static void AssertTimingSpecsHoldNoSecondsFields()
        {
            var timingTypes = new[]
            {
                typeof(AttackTimingSpec), typeof(GuardTimingSpec), typeof(MoveTimingSpec),
                typeof(BlockReactionTimingSpec), typeof(DodgeReactionTimingSpec)
            };
            foreach (var type in timingTypes)
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance |
                                                     BindingFlags.DeclaredOnly))
                {
                    Assert.That(field.FieldType, Is.EqualTo(typeof(int)),
                        $"{type.Name}.{field.Name} 必须是整数 Tick，不得持有秒值");
                }
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance |
                                                             BindingFlags.DeclaredOnly))
                {
                    Assert.That(property.Name, Does.Not.Contain("Second").And.Not.Contain("Time"),
                        $"{type.Name}.{property.Name} 不得保留秒字段");
                    Assert.That(property.PropertyType, Is.EqualTo(typeof(int)),
                        $"{type.Name}.{property.Name} 必须是整数 Tick");
                }
            }
        }

        [Test]
        public void LegacyAdapterConvertsAttackWindupAndRecoverySecondsExactlyOnce()
        {
            // 真实资产垂直切片：ForRadius1 库的 QuickSlash（BaseTime = 0.5s、BaseDamage = 15、Slash）。
            var library = AssetDatabase.LoadAssetAtPath<ActionLibrarySO>(RealLibraryPath);
            Assume.That(library, Is.Not.Null, $"真实动作库资产缺失：{RealLibraryPath}");
            var entry = library.Actions.FirstOrDefault(a => a.ID == "QuickSlash");
            Assume.That(entry, Is.Not.Null, "ForRadius1 缺少 QuickSlash 条目");
            Assume.That(entry.Data, Is.Not.Null);

            var converter = new LegacyAttackDefinitionConverter(BattleRules.FrozenV1);
            var spec = converter.ConvertAttack(
                "action.quick_slash.r1",
                "pattern.quick_slash.r1",
                entry.Data,
                TargetRelationMask.Hostile);

            // 秒 → Tick 只量化一次：一次转换恰好一次秒量化调用（BaseTime）。
            Assert.That(converter.QuantizationCallCount, Is.EqualTo(1), "秒→Tick 必须在边界恰好量化一次");

            var timing = (AttackTimingSpec)spec.Timing;
            Assert.That(timing.BaseWindupTicks, Is.EqualTo(30), "BaseTime 0.5s → BaseWindupTicks 30");
            Assert.That(timing.RecoveryTicks, Is.EqualTo(BattleRules.FrozenDefaultAttackRecoveryTicks),
                "旧资产无独立后摇 → 版本化默认 30 Tick");

            // 转换后的时序定义全部是整数 Tick，不再保留秒。
            AssertTimingSpecsHoldNoSecondsFields();

            // 载荷正交：伤害通道 physical.slash + 冲击 Profile impact.slash 独立配置。
            var payload = (AttackPayloadSpec)spec.Payload;
            Assert.That(payload.DamageComponents.Count, Is.EqualTo(1));
            Assert.That(payload.DamageComponents[0].ChannelId, Is.EqualTo(DamageChannels.PhysicalSlash));
            Assert.That(payload.DamageComponents[0].RawAmount, Is.EqualTo(15f));
            Assert.That(payload.ImpactProfileId, Is.EqualTo(ImpactProfiles.Slash));
            Assert.That(payload.ForceMultiplier, Is.EqualTo(0.8f));
            Assert.That(payload.AllowedTargetRelations, Is.EqualTo(TargetRelationMask.Hostile),
                "01B 拍板 A3：垂直切片攻击掩码 = {Hostile}");

            // Pattern 经整数展开为规范 12 向表。
            Assert.That(payload.Pattern.Directions.Count, Is.EqualTo(12));

            // 定义校验通过、费用为 0。
            Assert.That(spec.AdrenalineCost, Is.EqualTo(0));
            Assert.That(ActionDefinitionValidation.ValidateActionSpec(
                spec, BattleRules.FrozenV1, actionSpeed: 10f, moveSpeed: null), Is.Null);

            // 重复转换产生相同结果；每次转换都恰好量化一次。
            var second = new LegacyAttackDefinitionConverter(BattleRules.FrozenV1);
            var specAgain = second.ConvertAttack(
                "action.quick_slash.r1", "pattern.quick_slash.r1", entry.Data, TargetRelationMask.Hostile);
            Assert.That(second.QuantizationCallCount, Is.EqualTo(1));
            Assert.That(specAgain.Timing, Is.EqualTo(spec.Timing));
        }

        [Test]
        public void LegacyAttackWithoutRecoveryUsesThirtyTickDefault()
        {
            // 旧 Action 根本没有独立后摇字段：转换后必须使用版本化默认 30 Tick，
            // 不得继续由运行时调度器硬编码 0.5f。
            var legacy = new Action(
                "Test Slash", ProjectHero.Core.Actions.ActionType.Attack,
                0.5f, 15f, ImpactType.Slash, 10f, 0.8f, LoadRealPattern());

            var converter = new LegacyAttackDefinitionConverter(BattleRules.FrozenV1);
            var spec = converter.ConvertAttack(
                "action.test_slash", "pattern.test_slash", legacy, TargetRelationMask.Hostile);

            var timing = (AttackTimingSpec)spec.Timing;
            Assert.That(timing.BaseWindupTicks, Is.EqualTo(30));
            Assert.That(timing.RecoveryTicks, Is.EqualTo(30), "缺失后摇 → DefaultAttackRecoveryTicks = 30");
            Assert.That(timing.RecoveryTicks, Is.EqualTo(BattleRules.FrozenDefaultAttackRecoveryTicks));

            // 非 0.5s 前摇的资产同样只量化一次且用默认后摇。
            var longLegacy = new Action(
                "Heavy", ProjectHero.Core.Actions.ActionType.Attack,
                1.5f, 40f, ImpactType.Blunt, 25f, 1.6f, LoadRealPattern());
            var heavy = converter.ConvertAttack(
                "action.test_heavy", "pattern.test_heavy", longLegacy, TargetRelationMask.Hostile);
            Assert.That(((AttackTimingSpec)heavy.Timing).BaseWindupTicks, Is.EqualTo(90), "1.5s → 90 Tick");
            Assert.That(((AttackTimingSpec)heavy.Timing).RecoveryTicks, Is.EqualTo(30));
        }

        [Test]
        public void AuthoringRejectsInvalidMomentumOrDamageInputs()
        {
            // 动量输入非法（旧 TotalMass / Swiftness）：稳定拒绝，不钳制。
            foreach (float badMass in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            {
                var ex = Assert.Throws<LogicDefinitionException>(() =>
                    LegacyUnitStatsAdapter.ConvertStats("unit.test", badMass, 10f, 0f, 0f));
                Assert.That(ex.ErrorCode, Is.EqualTo(LegacyAuthoringCodes.LEGACY_MOMENTUM_INPUT_INVALID));
            }
            foreach (float badSwiftness in new[] { 0f, -1f, float.NaN, float.NegativeInfinity })
            {
                var ex = Assert.Throws<LogicDefinitionException>(() =>
                    LegacyUnitStatsAdapter.ConvertStats("unit.test", 90f, badSwiftness, 0f, 0f));
                Assert.That(ex.ErrorCode, Is.EqualTo(LegacyAuthoringCodes.LEGACY_MOMENTUM_INPUT_INVALID));
            }

            // 伤害输入非法（BaseDamage / ForceMultiplier）：定义级校验稳定拒绝。
            var pattern = LoadRealPattern();
            var converter = new LegacyAttackDefinitionConverter(BattleRules.FrozenV1);
            var badDamage = new Action(
                "Bad", ProjectHero.Core.Actions.ActionType.Attack,
                0.5f, -5f, ImpactType.Slash, 10f, 0.8f, pattern);
            var exDamage = Assert.Throws<LogicDefinitionException>(() =>
                converter.ConvertAttack("action.bad", "pattern.bad", badDamage, TargetRelationMask.Hostile));
            Assert.That(exDamage.ErrorCode, Is.EqualTo(ActionDefinitionCodes.ATTACK_DAMAGE_COMPONENT_INVALID));

            var nanDamage = new Action(
                "Bad", ProjectHero.Core.Actions.ActionType.Attack,
                0.5f, float.NaN, ImpactType.Slash, 10f, 0.8f, pattern);
            Assert.Throws<LogicDefinitionException>(() =>
                converter.ConvertAttack("action.bad", "pattern.bad", nanDamage, TargetRelationMask.Hostile));

            var badForce = new Action(
                "Bad", ProjectHero.Core.Actions.ActionType.Attack,
                0.5f, 15f, ImpactType.Slash, 10f, float.PositiveInfinity, pattern);
            var exForce = Assert.Throws<LogicDefinitionException>(() =>
                converter.ConvertAttack("action.bad", "pattern.bad", badForce, TargetRelationMask.Hostile));
            Assert.That(exForce.ErrorCode, Is.EqualTo(ActionDefinitionCodes.ATTACK_FORCE_MULTIPLIER_INVALID));

            // 防御输入非法（ArmorDefense/MagicResistance 非有限或负）：稳定拒绝。
            var exResistance = Assert.Throws<LogicDefinitionException>(() =>
                LegacyUnitStatsAdapter.ConvertStats("unit.test", 90f, 10f, float.NaN, 0f));
            Assert.That(exResistance.ErrorCode, Is.EqualTo(LegacyAuthoringCodes.LEGACY_RESISTANCE_INPUT_INVALID));

            // 合法输入通过。
            var valid = LegacyUnitStatsAdapter.ConvertStats("unit.test", 90f, 10f, 0f, 0f);
            Assert.That(valid.Mass, Is.EqualTo(90f));
            Assert.That(valid.MomentumSpeed, Is.EqualTo(10f));
            Assert.That(valid.ActionSpeed, Is.EqualTo(10f));
            Assert.That(valid.MoveSpeed, Is.EqualTo(10f));
        }

        [Test]
        public void LegacyStatsAdapterMapsArmorToExplicitPhysicalChannels()
        {
            // 旧非线性公式只在 Authoring 边界一次性折算：R = RoundHalfUp(1024 × d / (d + 100))。
            var def = LegacyUnitStatsAdapter.ConvertStats("unit.test", 90f, 10f, armorDefense: 100f, magicResistance: 100f);

            Assert.That(def.BaseDamageResistanceQ10[DamageChannels.PhysicalBlunt], Is.EqualTo(512));
            Assert.That(def.BaseDamageResistanceQ10[DamageChannels.PhysicalSlash], Is.EqualTo(512));
            Assert.That(def.BaseDamageResistanceQ10[DamageChannels.PhysicalPierce], Is.EqualTo(512));
            Assert.That(def.BaseDamageResistanceQ10[DamageChannels.ElementalFire], Is.EqualTo(512),
                "01B 拍板 B1：MagicResistance → elemental.fire 同公式");
            Assert.That(def.BaseDamageResistanceQ10[DamageChannels.ElementalFrost], Is.EqualTo(512));
            Assert.That(def.BaseDamageResistanceQ10[DamageChannels.ElementalLightning], Is.EqualTo(512));
            Assert.That(def.BaseDamageResistanceQ10[DamageChannels.Arcane], Is.EqualTo(0),
                "01B 拍板 B1：arcane 默认 0");
            Assert.That(def.BaseDamageResistanceQ10.Count, Is.EqualTo(7), "只写入明确通道，无通用 DamageReduction");

            // 当前场景值 armor=0 / MR=0 → R=0。
            var zero = LegacyUnitStatsAdapter.ConvertStats("unit.test", 90f, 10f, 0f, 0f);
            Assert.That(zero.BaseDamageResistanceQ10[DamageChannels.PhysicalBlunt], Is.EqualTo(0));
            Assert.That(zero.BaseDamageResistanceQ10[DamageChannels.ElementalFire], Is.EqualTo(0));

            // 边界：armor=300 → 1024×300/400 = 768。
            var high = LegacyUnitStatsAdapter.ConvertStats("unit.test", 90f, 10f, 300f, 0f);
            Assert.That(high.BaseDamageResistanceQ10[DamageChannels.PhysicalSlash], Is.EqualTo(768));

            // 单位定义不含通用 DamageReduction 成员。
            foreach (var member in typeof(ProjectHero.Logic.Combat.UnitDefinition).GetMembers(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(member.Name, Does.Not.Contain("DamageReduction"));
            }

            // CombatUnit 接缝：旧字段（TotalMass/Swiftness/ArmorDefense/MagicResistance）一次性映射。
            var go = new GameObject("LegacyStatsAdapterTestUnit", typeof(CombatUnit));
            go.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var unit = go.GetComponent<CombatUnit>();
                // 默认字段：Strength/Constitution/ArmorWeight = 10 → TotalMass = 50+20+20+10 = 100；
                // Swiftness = 7.5 + 2.5 = 10；ArmorDefense/MagicResistance = 0。
                var fromUnit = LegacyUnitStatsAdapter.ConvertCombatUnit("unit.from_combat_unit", unit);
                Assert.That(fromUnit.Mass, Is.EqualTo(100f));
                Assert.That(fromUnit.MomentumSpeed, Is.EqualTo(10f));
                Assert.That(fromUnit.BaseDamageResistanceQ10[DamageChannels.PhysicalBlunt], Is.EqualTo(0));
                Assert.That(fromUnit.BaseDamageResistanceQ10[DamageChannels.Arcane], Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
