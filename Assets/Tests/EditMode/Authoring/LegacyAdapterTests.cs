using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Combat;
using ProjectHero.Core.Physics;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Factions;
using UnityEngine;

namespace ProjectHero.Authoring.Tests
{
    /// <summary>
    /// 旧资产兼容适配器的 EditMode 测试。
    ///
    /// 任务 02 期间本文件位于 Assembly-CSharp-Editor（当时 Authoring 程序集还不存在）。
    /// 任务 02B 把 <c>ActionLibrarySO</c>/<c>Action</c>/<c>AttackPattern</c>/<c>UnitVolume</c>/<c>ImpactType</c>
    /// 搬入 <c>ProjectHero.Authoring</c> 后，这些测试随类型一起进入 <c>ProjectHero.Authoring.Tests</c>。
    /// 唯一需要旧运行时类型（<c>CombatUnit</c>）的用例拆到
    /// <c>Assets/Tests/EditMode/Compatibility/Editor/LegacyCombatUnitAdapterTests.cs</c>，
    /// 因为自定义 asmdef 不能引用 Assembly-CSharp（00 号规则 17）。
    ///
    /// 资产改为经 <c>Resources.Load</c> 读取（与 Builder 使用同一加载入口），
    /// 不再依赖 UnityEditor.AssetDatabase。
    /// </summary>
    public class LegacyAdapterTests
    {
        private const string ActionLibraryResourcePath = "ActionLibrary/ForRadius1";
        private const string SlashPatternR1ResourcePath = "GeneratedActions/Pattern_Slash_R1_0";

        private static AttackPattern LoadRealPattern()
        {
            var pattern = Resources.Load<AttackPattern>(SlashPatternR1ResourcePath);
            Assume.That(pattern, Is.Not.Null, $"真实 Pattern 资产缺失：{SlashPatternR1ResourcePath}");
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
            var library = Resources.Load<ActionLibrarySO>(ActionLibraryResourcePath);
            Assume.That(library, Is.Not.Null, $"真实动作库资产缺失：{ActionLibraryResourcePath}");
            var entry = library.Actions.FirstOrDefault(a => a.ID == "QuickSlash");
            Assume.That(entry, Is.Not.Null, "ForRadius1 缺少 QuickSlash 条目");
            Assume.That(entry.Data, Is.Not.Null);

            var converter = new LegacyAttackDefinitionConverter(BattleRules.FrozenV1);
            var spec = converter.ConvertAttack(
                "action.quick_slash.radius_1",
                "pattern.slash.radius_1",
                entry.Data,
                TargetRelationMask.Hostile);

            // 秒 → Tick 只量化一次：一次转换恰好一次秒量化调用（BaseTime）。
            Assert.That(converter.QuantizationCallCount, Is.EqualTo(1), "秒→Tick 必须在边界恰好量化一次");

            var timing = (AttackTimingSpec)spec.Timing;
            Assert.That(timing.BaseWindupTicks, Is.EqualTo(30), "BaseTime 0.5s → BaseWindupTicks 30");
            Assert.That(timing.RecoveryTicks, Is.EqualTo(BattleRules.FrozenDefaultAttackRecoveryTicks),
                "旧资产无独立后摇 → 版本化默认 30 Tick");

            AssertTimingSpecsHoldNoSecondsFields();

            var payload = (AttackPayloadSpec)spec.Payload;
            Assert.That(payload.DamageComponents.Count, Is.EqualTo(1));
            Assert.That(payload.DamageComponents[0].ChannelId, Is.EqualTo(DamageChannels.PhysicalSlash));
            Assert.That(payload.DamageComponents[0].RawAmount, Is.EqualTo(15f));
            Assert.That(payload.ImpactProfileId, Is.EqualTo(ImpactProfiles.Slash));
            Assert.That(payload.ForceMultiplier, Is.EqualTo(0.8f));
            Assert.That(payload.AllowedTargetRelations, Is.EqualTo(TargetRelationMask.Hostile),
                "01B 拍板 A3：垂直切片攻击掩码 = {Hostile}");

            Assert.That(payload.Pattern.Directions.Count, Is.EqualTo(12));

            Assert.That(spec.AdrenalineCost, Is.EqualTo(0));
            Assert.That(ActionDefinitionValidation.ValidateActionSpec(
                spec, BattleRules.FrozenV1, actionSpeed: 10f, moveSpeed: null), Is.Null);

            var second = new LegacyAttackDefinitionConverter(BattleRules.FrozenV1);
            var specAgain = second.ConvertAttack(
                "action.quick_slash.radius_1", "pattern.slash.radius_1", entry.Data, TargetRelationMask.Hostile);
            Assert.That(second.QuantizationCallCount, Is.EqualTo(1));
            Assert.That(specAgain.Timing, Is.EqualTo(spec.Timing));
        }

        [Test]
        public void LegacyAttackWithoutRecoveryUsesThirtyTickDefault()
        {
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

            var exResistance = Assert.Throws<LogicDefinitionException>(() =>
                LegacyUnitStatsAdapter.ConvertStats("unit.test", 90f, 10f, float.NaN, 0f));
            Assert.That(exResistance.ErrorCode, Is.EqualTo(LegacyAuthoringCodes.LEGACY_RESISTANCE_INPUT_INVALID));

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

            var zero = LegacyUnitStatsAdapter.ConvertStats("unit.test", 90f, 10f, 0f, 0f);
            Assert.That(zero.BaseDamageResistanceQ10[DamageChannels.PhysicalBlunt], Is.EqualTo(0));
            Assert.That(zero.BaseDamageResistanceQ10[DamageChannels.ElementalFire], Is.EqualTo(0));

            var high = LegacyUnitStatsAdapter.ConvertStats("unit.test", 90f, 10f, 300f, 0f);
            Assert.That(high.BaseDamageResistanceQ10[DamageChannels.PhysicalSlash], Is.EqualTo(768));

            foreach (var member in typeof(ProjectHero.Logic.Combat.UnitDefinition).GetMembers(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(member.Name, Does.Not.Contain("DamageReduction"));
            }
        }
    }
}
