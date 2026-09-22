using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Tests
{
    /// <summary>动作定义校验（任务 02「必须产出」10/11）：时序、速度、抵抗、费用、标签与载荷正交性。</summary>
    public class ActionDefinitionTests
    {
        private static AttackPatternSpec MakePattern(string id = "pattern.attack.test")
            => new AttackPatternSpec(
                new AttackPatternId(id),
                DirectionalGeometry.ExpandFromBases(
                    new[] { new TrianglePoint(3, 0, 1) },
                    new[] { new TrianglePoint(4, 1, -1) }));

        private static MovementPatternSpec MakeMovementPattern(string id = "pattern.move.test")
            => new MovementPatternSpec(
                new MovementPatternId(id),
                DirectionalGeometry.ExpandFromBases(
                    new[] { new TrianglePoint(3, 0, 1) },
                    new[] { new TrianglePoint(4, 1, -1) }));

        private static ActionSpec MakeAttack(
            int baseWindupTicks = 30,
            int recoveryTicks = 30,
            float baseDamage = 15f,
            float forceMultiplier = 1f,
            TargetRelationMask allowed = TargetRelationMask.Hostile,
            DamageTagMask tags = DamageTagMask.Blockable | DamageTagMask.Guardable,
            int adrenalineCost = 0)
            => new ActionSpec(
                new ActionSpecId("action.attack.test"),
                ActionType.Attack,
                new AttackTimingSpec(baseWindupTicks, recoveryTicks),
                new AttackPayloadSpec(
                    new[]
                    {
                        new DamageComponentSpec(DamageChannels.PhysicalSlash, baseDamage, tags)
                    },
                    ImpactProfiles.Slash,
                    forceMultiplier,
                    TargetPolicy.AllTargetsInArea,
                    allowed,
                    MomentumDirectionOffsetSteps: 0,
                    MakePattern(),
                    AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable),
                adrenalineCost);

        private static ActionSpec MakeGuard(
            int windup = 5, int active = 10, int recovery = 5,
            int channelResistance = 512, int momentumResistance = 512,
            int adrenalineCost = 0)
            => new ActionSpec(
                new ActionSpecId("action.guard.test"),
                ActionType.Guard,
                new GuardTimingSpec(windup, active, recovery),
                new GuardPayloadSpec(
                    new Dictionary<DamageChannelId, int>
                    {
                        [DamageChannels.PhysicalSlash] = channelResistance
                    },
                    momentumResistance,
                    DefenseTagMask.Blockable | DefenseTagMask.Guardable),
                adrenalineCost);

        private static ActionSpec MakeBlock(int windup = 60, int recovery = 10, int adrenalineCost = 2)
            => new ActionSpec(
                new ActionSpecId("action.block.test"),
                ActionType.Block,
                new BlockReactionTimingSpec(windup, recovery),
                new BlockPayloadSpec(DefenseTagMask.Blockable),
                adrenalineCost);

        private static ActionSpec MakeDodge(int windup = 30, int recovery = 10, int adrenalineCost = 1)
            => new ActionSpec(
                new ActionSpecId("action.dodge.test"),
                ActionType.Dodge,
                new DodgeReactionTimingSpec(windup, recovery),
                new DodgePayloadSpec(MaxDistanceSteps: 2, MakeMovementPattern("pattern.dodge.test")),
                adrenalineCost);

        private static ActionSpec MakeMove(int baseStepTicks = 30, int recovery = 10, int maxWeight = 256)
            => new ActionSpec(
                new ActionSpecId("action.move.test"),
                ActionType.Move,
                new MoveTimingSpec(baseStepTicks, recovery),
                new MovePayloadSpec(maxWeight, MakeMovementPattern()),
                AdrenalineCost: 0);

        [Test]
        public void AttackTimingRejectsNonPositiveWindupOrRecovery()
        {
            var rules = BattleRules.FrozenV1;
            Assert.That(ActionDefinitionValidation.ValidateAttack(MakeAttack(0, 30)),
                Is.EqualTo(ActionDefinitionCodes.ATTACK_TIMING_OUT_OF_RANGE));
            Assert.That(ActionDefinitionValidation.ValidateAttack(MakeAttack(-1, 30)),
                Is.EqualTo(ActionDefinitionCodes.ATTACK_TIMING_OUT_OF_RANGE));
            Assert.That(ActionDefinitionValidation.ValidateAttack(MakeAttack(30, 0)),
                Is.EqualTo(ActionDefinitionCodes.ATTACK_TIMING_OUT_OF_RANGE));
            Assert.That(ActionDefinitionValidation.ValidateAttack(MakeAttack(30, -5)),
                Is.EqualTo(ActionDefinitionCodes.ATTACK_TIMING_OUT_OF_RANGE));
            Assert.That(ActionDefinitionValidation.ValidateAttack(MakeAttack(30, 30)), Is.Null);
            Assert.That(ActionDefinitionValidation.ValidateActionSpec(
                MakeAttack(30, 30), rules, actionSpeed: 20f, moveSpeed: null), Is.Null);
        }

        [Test]
        public void AttackTimingRejectsNonPositiveOrNonFiniteActionSpeed()
        {
            foreach (float speed in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                Assert.That(ActionDefinitionValidation.ValidateAttackSpeed(speed),
                    Is.EqualTo(ActionDefinitionCodes.ATTACK_ACTION_SPEED_INVALID),
                    $"ActionSpeed = {speed} 必须拒绝");
            }
            Assert.That(ActionDefinitionValidation.ValidateAttackSpeed(20f), Is.Null);
            Assert.That(ActionDefinitionValidation.ValidateAttackSpeed(0.1f), Is.Null);
        }

        [Test]
        public void GuardTimingRequiresPositiveWindupActiveAndRecovery()
        {
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(0, 10, 5)),
                Is.EqualTo(ActionDefinitionCodes.GUARD_TIMING_INVALID));
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(5, 0, 5)),
                Is.EqualTo(ActionDefinitionCodes.GUARD_TIMING_INVALID));
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(5, 10, 0)),
                Is.EqualTo(ActionDefinitionCodes.GUARD_TIMING_INVALID));
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(-1, 10, 5)),
                Is.EqualTo(ActionDefinitionCodes.GUARD_TIMING_INVALID));
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(5, 10, 5)), Is.Null);
        }

        [Test]
        public void GuardEligibleResistanceMustBePartial()
        {
            // 分通道动作抵抗严格位于 [1,1023]。
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(channelResistance: 0)),
                Is.EqualTo(ActionDefinitionCodes.GUARD_RESISTANCE_MUST_BE_PARTIAL));
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(channelResistance: 1024)),
                Is.EqualTo(ActionDefinitionCodes.GUARD_RESISTANCE_MUST_BE_PARTIAL));
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(channelResistance: 1)), Is.Null);
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(channelResistance: 1023)), Is.Null);

            // 动量抵抗同样严格位于 [1,1023]。
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(momentumResistance: 0)),
                Is.EqualTo(ActionDefinitionCodes.GUARD_RESISTANCE_MUST_BE_PARTIAL));
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(momentumResistance: 1024)),
                Is.EqualTo(ActionDefinitionCodes.GUARD_RESISTANCE_MUST_BE_PARTIAL));
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(momentumResistance: 1)), Is.Null);
            Assert.That(ActionDefinitionValidation.ValidateGuard(MakeGuard(momentumResistance: 1023)), Is.Null);

            // 合格标签必须非空合法。
            var guardWithNoTags = MakeGuard() with
            {
                Payload = new GuardPayloadSpec(
                    new Dictionary<DamageChannelId, int> { [DamageChannels.PhysicalSlash] = 512 },
                    512,
                    DefenseTagMask.None)
            };
            Assert.That(ActionDefinitionValidation.ValidateGuard(guardWithNoTags),
                Is.EqualTo(ActionDefinitionCodes.GUARD_ELIGIBLE_TAGS_INVALID));
        }

        [Test]
        public void BlockEligibleResistanceIsFixedFull()
        {
            // 规则固定 1024。
            Assert.That(BattleRules.FullBlockResistanceQ10, Is.EqualTo(1024));

            // BlockPayloadSpec 只有 EligibleTags 一个字段：Authoring 不存在可降低抵抗的字段。
            var properties = typeof(BlockPayloadSpec)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(properties.Select(p => p.Name).ToArray(), Is.EqualTo(new[] { "EligibleTags" }));

            // 校验面：合格标签必须非空合法；抵抗值由规则固定，无从校验/修改。
            Assert.That(ActionDefinitionValidation.ValidateBlock(MakeBlock()), Is.Null);
            var noTags = MakeBlock() with { Payload = new BlockPayloadSpec(DefenseTagMask.None) };
            Assert.That(ActionDefinitionValidation.ValidateBlock(noTags),
                Is.EqualTo(ActionDefinitionCodes.BLOCK_ELIGIBLE_TAGS_INVALID));
            var unknownTags = MakeBlock() with
            {
                Payload = new BlockPayloadSpec((DefenseTagMask)(1 << 4))
            };
            Assert.That(ActionDefinitionValidation.ValidateBlock(unknownTags),
                Is.EqualTo(ActionDefinitionCodes.BLOCK_ELIGIBLE_TAGS_INVALID));

            // 固定值进入 BattleDefinitionHash。
            var writer = new CanonicalHashWriter();
            BattleRules.FrozenV1.WriteHashComponents(writer);
            Assert.That(writer.ToCanonicalText(), Does.Contain("rules.full_block_resistance_q10=1024"));
        }

        [Test]
        public void OrdinaryActionsCannotDeclareAdrenalineCost()
        {
            var rules = BattleRules.FrozenV1;

            Assert.That(ActionDefinitionValidation.ValidateAdrenalineCost(MakeAttack(adrenalineCost: 1)),
                Is.EqualTo(ActionDefinitionCodes.ORDINARY_ACTION_CANNOT_COST_ADRENALINE));
            Assert.That(ActionDefinitionValidation.ValidateAdrenalineCost(MakeGuard(adrenalineCost: 1)),
                Is.EqualTo(ActionDefinitionCodes.ORDINARY_ACTION_CANNOT_COST_ADRENALINE));
            Assert.That(ActionDefinitionValidation.ValidateAdrenalineCost(MakeMove()),
                Is.Null);
            Assert.That(ActionDefinitionValidation.ValidateAdrenalineCost(MakeMove() with { AdrenalineCost = 5 }),
                Is.EqualTo(ActionDefinitionCodes.ORDINARY_ACTION_CANNOT_COST_ADRENALINE));

            Assert.That(ActionDefinitionValidation.ValidateAdrenalineCost(MakeBlock()), Is.Null);
            Assert.That(ActionDefinitionValidation.ValidateAdrenalineCost(MakeDodge()), Is.Null);
            Assert.That(ActionDefinitionValidation.ValidateAdrenalineCost(MakeBlock(adrenalineCost: 0)),
                Is.EqualTo(ActionDefinitionCodes.REACTION_ACTION_REQUIRES_POSITIVE_COST));
            Assert.That(ActionDefinitionValidation.ValidateAdrenalineCost(MakeDodge(adrenalineCost: 0)),
                Is.EqualTo(ActionDefinitionCodes.REACTION_ACTION_REQUIRES_POSITIVE_COST));

            // 闭合匹配矩阵：类型错配整体拒绝。
            var mismatched = MakeAttack() with { Timing = new GuardTimingSpec(5, 10, 5) };
            Assert.That(ActionDefinitionValidation.ValidateTypePairing(mismatched),
                Is.EqualTo(ActionDefinitionCodes.INVALID_ACTION_TYPE_COMBINATION));
            Assert.That(ActionDefinitionValidation.ValidateActionSpec(MakeAttack(30, 30), rules, 20f, null),
                Is.Null);
        }

        [Test]
        public void ActionPayloadKeepsDamageComponentsSeparateFromMomentumProfile()
        {
            var spec = MakeAttack();
            var payload = (AttackPayloadSpec)spec.Payload;

            // 伤害分量携带通道与原始量；动量侧只有 ImpactProfileId。两者是不同 ID 类型。
            Assert.That(payload.DamageComponents.Count, Is.EqualTo(1));
            Assert.That(payload.DamageComponents[0].ChannelId, Is.EqualTo(DamageChannels.PhysicalSlash));
            Assert.That(payload.DamageComponents[0].RawAmount, Is.EqualTo(15f));
            Assert.That(payload.ImpactProfileId, Is.EqualTo(ImpactProfiles.Slash));
            Assert.That(payload.DamageComponents[0].ChannelId.GetType(), Is.EqualTo(typeof(DamageChannelId)));
            Assert.That(payload.ImpactProfileId.GetType(), Is.EqualTo(typeof(ImpactProfileId)));

            // 不存在把两者合并回单一 ImpactType / DamageReduction 的字段。
            foreach (var type in new[]
                     {
                         typeof(AttackPayloadSpec), typeof(GuardPayloadSpec), typeof(BlockPayloadSpec),
                         typeof(UnitDefinition)
                     })
            {
                foreach (var member in type.GetMembers(
                             BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(member.Name, Does.Not.Contain("DamageReduction"),
                        $"{type.Name}.{member.Name} 不得出现通用减伤");
                }
            }

            // 不可变：改 Profile 不动伤害分量（with 产生新实例，旧实例不变）。
            var changedProfile = payload with { ImpactProfileId = ImpactProfiles.Blunt };
            Assert.That(changedProfile.DamageComponents[0].ChannelId, Is.EqualTo(DamageChannels.PhysicalSlash));
            Assert.That(payload.ImpactProfileId, Is.EqualTo(ImpactProfiles.Slash));
        }

        [Test]
        public void ResistanceRejectsValuesOutsideQ10Range()
        {
            var baseDef = new UnitDefinition(
                new UnitDefinitionId("unit.test"),
                90f, 10f, 10f, 10f,
                new Dictionary<DamageChannelId, int> { [DamageChannels.PhysicalBlunt] = 512 });

            Assert.That(UnitDefinitionValidation.Validate(baseDef), Is.Null);
            Assert.That(UnitDefinitionValidation.Validate(baseDef with
            {
                BaseDamageResistanceQ10 = new Dictionary<DamageChannelId, int>
                {
                    [DamageChannels.PhysicalBlunt] = 0
                }
            }), Is.Null, "0 合法（被动抵抗范围 [0,1024]）");
            Assert.That(UnitDefinitionValidation.Validate(baseDef with
            {
                BaseDamageResistanceQ10 = new Dictionary<DamageChannelId, int>
                {
                    [DamageChannels.PhysicalBlunt] = 1024
                }
            }), Is.Null, "1024 合法");

            Assert.That(UnitDefinitionValidation.Validate(baseDef with
            {
                BaseDamageResistanceQ10 = new Dictionary<DamageChannelId, int>
                {
                    [DamageChannels.PhysicalBlunt] = -1
                }
            }), Is.EqualTo(UnitDefinitionCodes.UNIT_RESISTANCE_OUT_OF_RANGE));
            Assert.That(UnitDefinitionValidation.Validate(baseDef with
            {
                BaseDamageResistanceQ10 = new Dictionary<DamageChannelId, int>
                {
                    [DamageChannels.PhysicalBlunt] = 1025
                }
            }), Is.EqualTo(UnitDefinitionCodes.UNIT_RESISTANCE_OUT_OF_RANGE));

            // 通道 ID 非法。
            Assert.That(UnitDefinitionValidation.Validate(baseDef with
            {
                BaseDamageResistanceQ10 = new Dictionary<DamageChannelId, int>
                {
                    [new DamageChannelId("Physical.Blunt")] = 10
                }
            }), Is.EqualTo(UnitDefinitionCodes.UNIT_RESISTANCE_CHANNEL_INVALID));

            // 动量输入非法。
            Assert.That(UnitDefinitionValidation.Validate(baseDef with { Mass = 0f }),
                Is.EqualTo(UnitDefinitionCodes.UNIT_MASS_INVALID));
            Assert.That(UnitDefinitionValidation.Validate(baseDef with { Mass = float.NaN }),
                Is.EqualTo(UnitDefinitionCodes.UNIT_MASS_INVALID));
            Assert.That(UnitDefinitionValidation.Validate(baseDef with { MomentumSpeed = -1f }),
                Is.EqualTo(UnitDefinitionCodes.UNIT_MOMENTUM_SPEED_INVALID));
        }
    }
}
