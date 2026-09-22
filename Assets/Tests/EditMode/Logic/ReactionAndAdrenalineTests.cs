using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Tests
{
    /// <summary>反应规则、肾上腺素规则与伤害标签校验（任务 02「必须产出」11/12）。</summary>
    public class ReactionAndAdrenalineTests
    {
        [Test]
        public void AdrenalineRulesRejectBlockOrDodgeRewardAtOrAboveCost()
        {
            // 01B 拍板 B3 冻结值：Block 费 2 / 奖励 1、Dodge 费 1 / 奖励 0 —— 合法。
            var frozen = AdrenalineRules.FrozenV1;
            Assert.That(frozen.Validate(), Is.Null);
            Assert.That(frozen.ValidateReactionCost(ActionType.Block, FrozenDesignValues.BlockAdrenalineCost), Is.Null);
            Assert.That(frozen.ValidateReactionCost(ActionType.Dodge, FrozenDesignValues.DodgeAdrenalineCost), Is.Null);

            // 奖励 ≥ 费用必须拒绝。
            var blockAtCost = frozen with { SuccessfulBlockReward = 2 };
            Assert.That(blockAtCost.ValidateReactionCost(ActionType.Block, 2),
                Is.EqualTo(AdrenalineRules.ADRENALINE_BLOCK_REWARD_NOT_BELOW_COST));
            var blockAboveCost = frozen with { SuccessfulBlockReward = 3 };
            Assert.That(blockAboveCost.ValidateReactionCost(ActionType.Block, 2),
                Is.EqualTo(AdrenalineRules.ADRENALINE_BLOCK_REWARD_NOT_BELOW_COST));
            var dodgeAtCost = frozen with { SuccessfulDodgeReward = 1 };
            Assert.That(dodgeAtCost.ValidateReactionCost(ActionType.Dodge, 1),
                Is.EqualTo(AdrenalineRules.ADRENALINE_DODGE_REWARD_NOT_BELOW_COST));

            // 动作集合整体校验（普通动作不参与反应费用校验）。
            var blockSpec = new ActionSpec(
                new ActionSpecId("action.block.test"), ActionType.Block,
                new BlockReactionTimingSpec(60, 10), new BlockPayloadSpec(DefenseTagMask.Blockable),
                FrozenDesignValues.BlockAdrenalineCost);
            var dodgeSpec = new ActionSpec(
                new ActionSpecId("action.dodge.test"), ActionType.Dodge,
                new DodgeReactionTimingSpec(30, 10),
                new DodgePayloadSpec(2,
                    new ProjectHero.Logic.Grid.MovementPatternSpec(
                        new MovementPatternId("pattern.dodge.test"),
                        ProjectHero.Logic.Grid.DirectionalGeometry.ExpandFromBases(
                            new[] { new ProjectHero.Logic.Grid.TrianglePoint(3, 0, 1) },
                            new[] { new ProjectHero.Logic.Grid.TrianglePoint(4, 1, -1) }))),
                FrozenDesignValues.DodgeAdrenalineCost);
            Assert.That(frozen.ValidateAgainstActionCosts(new[] { blockSpec, dodgeSpec }), Is.Null);
            Assert.That(blockAtCost.ValidateAgainstActionCosts(new[] { blockSpec }),
                Is.EqualTo(AdrenalineRules.ADRENALINE_BLOCK_REWARD_NOT_BELOW_COST));
        }

        [Test]
        public void AdrenalineRulesRejectCostAboveCycleMaximum()
        {
            var frozen = AdrenalineRules.FrozenV1;
            Assert.That(frozen.MaxAvailablePerCycle, Is.EqualTo(100));

            Assert.That(frozen.ValidateReactionCost(ActionType.Block, 101),
                Is.EqualTo(AdrenalineRules.ADRENALINE_COST_EXCEEDS_CYCLE_MAXIMUM));
            Assert.That(frozen.ValidateReactionCost(ActionType.Dodge, 101),
                Is.EqualTo(AdrenalineRules.ADRENALINE_COST_EXCEEDS_CYCLE_MAXIMUM));
            Assert.That(frozen.ValidateReactionCost(ActionType.Block, 100), Is.Null, "恰好等于上限合法");

            // 基础校验：上限必须为正、获得率/奖励非负。
            Assert.That((frozen with { MaxAvailablePerCycle = 0 }).Validate(),
                Is.EqualTo(AdrenalineRules.ADRENALINE_MAX_AVAILABLE_INVALID));
            Assert.That((frozen with { DamageDealtGainQ10 = -1 }).Validate(),
                Is.EqualTo(AdrenalineRules.ADRENALINE_GAIN_NEGATIVE));
            Assert.That((frozen with { DamageReceivedGainQ10 = -1 }).Validate(),
                Is.EqualTo(AdrenalineRules.ADRENALINE_GAIN_NEGATIVE));
            Assert.That((frozen with { ClashReward = -1 }).Validate(),
                Is.EqualTo(AdrenalineRules.ADRENALINE_REWARD_NEGATIVE));
            Assert.That((frozen with { SuccessfulBlockReward = -1 }).Validate(),
                Is.EqualTo(AdrenalineRules.ADRENALINE_REWARD_NEGATIVE));
        }

        [Test]
        public void TrueDamageBypassesPassiveAndActionResistance()
        {
            // true 通道默认同时绕过被动/动作抵抗，且不可 Block/Guard。
            var defaults = DamageChannelCatalog.GetDefaultTags(DamageChannels.True);
            Assert.That(defaults & DamageTagMask.BypassPassiveResistance, Is.Not.EqualTo(DamageTagMask.None));
            Assert.That(defaults & DamageTagMask.BypassActionResistance, Is.Not.EqualTo(DamageTagMask.None));
            Assert.That(defaults & DamageTagMask.Blockable, Is.EqualTo(DamageTagMask.None));
            Assert.That(defaults & DamageTagMask.Guardable, Is.EqualTo(DamageTagMask.None));

            var component = new DamageComponentSpec(DamageChannels.True, 50f, defaults);
            Assert.That(DamageComponentValidation.Validate(component), Is.Null, "true 通道默认标签必须合法");

            // 普通通道默认 Blockable | Guardable。
            var normal = DamageChannelCatalog.GetDefaultTags(DamageChannels.PhysicalBlunt);
            Assert.That(normal & DamageTagMask.Blockable, Is.Not.EqualTo(DamageTagMask.None));
            Assert.That(normal & DamageTagMask.Guardable, Is.Not.EqualTo(DamageTagMask.None));
            Assert.That(normal & DamageTagMask.BypassPassiveResistance, Is.EqualTo(DamageTagMask.None));
        }

        [Test]
        public void ContradictoryDamageEligibilityAndBypassTagsAreRejected()
        {
            // 动作绕过与 Blockable/Guardable 同置 = 矛盾标签。
            var withBlockable = new DamageComponentSpec(
                DamageChannels.PhysicalSlash, 10f,
                DamageTagMask.BypassActionResistance | DamageTagMask.Blockable);
            Assert.That(DamageComponentValidation.Validate(withBlockable),
                Is.EqualTo(DamageTagCodes.CONTRADICTORY_DAMAGE_TAGS));

            var withGuardable = new DamageComponentSpec(
                DamageChannels.PhysicalSlash, 10f,
                DamageTagMask.BypassActionResistance | DamageTagMask.Guardable);
            Assert.That(DamageComponentValidation.Validate(withGuardable),
                Is.EqualTo(DamageTagCodes.CONTRADICTORY_DAMAGE_TAGS));

            // 被动绕过 + Blockable 不矛盾（分别跳过对应层）。
            var passiveOnly = new DamageComponentSpec(
                DamageChannels.PhysicalSlash, 10f,
                DamageTagMask.BypassPassiveResistance | DamageTagMask.Blockable);
            Assert.That(DamageComponentValidation.Validate(passiveOnly), Is.Null);

            // 未知标签位 / 非法伤害量。
            Assert.That(DamageComponentValidation.Validate(new DamageComponentSpec(
                    DamageChannels.PhysicalSlash, 10f, (DamageTagMask)(1 << 4))),
                Is.EqualTo(DamageTagCodes.DAMAGE_TAGS_INVALID));
            Assert.That(DamageComponentValidation.Validate(new DamageComponentSpec(
                    DamageChannels.PhysicalSlash, -1f, DamageTagMask.Blockable)),
                Is.EqualTo(DamageTagCodes.DAMAGE_COMPONENT_INVALID));
            Assert.That(DamageComponentValidation.Validate(new DamageComponentSpec(
                    DamageChannels.PhysicalSlash, float.NaN, DamageTagMask.Blockable)),
                Is.EqualTo(DamageTagCodes.DAMAGE_COMPONENT_INVALID));
            Assert.That(DamageComponentValidation.Validate(new DamageComponentSpec(
                    DamageChannels.PhysicalSlash, float.PositiveInfinity, DamageTagMask.Blockable)),
                Is.EqualTo(DamageTagCodes.DAMAGE_COMPONENT_INVALID));
        }

        [Test]
        public void ReactionOptionsAreSortedByActionSpecId()
        {
            var options = new[]
            {
                new ReactionOption(new ActionSpecId("action.block.test"), ActionType.Block, 100),
                new ReactionOption(new ActionSpecId("action.dodge.test"), ActionType.Dodge, 95),
                new ReactionOption(new ActionSpecId("action.block.fast"), ActionType.Block, 90)
            };
            var canonical = ReactionOpportunityData.CanonicalizeOptions(options);
            Assert.That(canonical.Select(o => o.ReactionActionSpecId.Value).ToArray(),
                Is.EqualTo(new[] { "action.block.fast", "action.block.test", "action.dodge.test" }),
                "选项必须按 ActionSpecId（Ordinal）升序");

            Assert.That(ReactionOpportunityData.ValidateOptions(
                    new[] { options[0], options[0] }),
                Is.EqualTo(ReactionOpportunityData.REACTION_OPTION_DUPLICATE));
            Assert.That(ReactionOpportunityData.ValidateOptions(System.Array.Empty<ReactionOption>()),
                Is.EqualTo(ReactionOpportunityData.REACTION_OPTION_EMPTY));
            Assert.That(ReactionOpportunityData.ValidateOptions(
                    new[] { new ReactionOption(new ActionSpecId("action.block.test"), ActionType.Block, 0) }),
                Is.EqualTo(ReactionOpportunityData.REACTION_OPTION_INVALID_DEADLINE));

            Assert.Throws<LogicDefinitionException>(() =>
                ReactionOpportunityData.CanonicalizeOptions(System.Array.Empty<ReactionOption>()));
        }
    }
}
