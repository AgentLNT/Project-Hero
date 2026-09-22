using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;

namespace ProjectHero.Logic.Tests
{
    /// <summary>全部版本化规则参与 BattleDefinitionHash 的验证（任务 02「必须产出」10/12/15）。</summary>
    public class HashParticipationTests
    {
        [Test]
        public void AttackTimingRulesParticipateInBattleDefinitionHash()
        {
            var rules = BattleRules.FrozenV1;
            var timing = new AttackTimingSpec(30, 30);
            string baseline = BattleDefinitionHash.OfAttackTimingRules(rules, timing);

            // ReferenceActionSpeed、DefaultAttackRecoveryTicks、舍入方式与攻击时序字段结构均参与哈希。
            Assert.That(
                BattleDefinitionHash.OfAttackTimingRules(rules with { ReferenceActionSpeed = 21 }, timing),
                Is.Not.EqualTo(baseline), "ReferenceActionSpeed 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfAttackTimingRules(rules with { DefaultAttackRecoveryTicks = 31 }, timing),
                Is.Not.EqualTo(baseline), "默认后摇必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfAttackTimingRules(rules, timing with { BaseWindupTicks = 31 }),
                Is.Not.EqualTo(baseline), "BaseWindupTicks 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfAttackTimingRules(rules, timing with { RecoveryTicks = 31 }),
                Is.Not.EqualTo(baseline), "RecoveryTicks 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfAttackTimingRules(rules with { TicksPerSecond = 59 }, timing),
                Is.Not.EqualTo(baseline), "TicksPerSecond 必须参与哈希");

            Assert.That(BattleDefinitionHash.OfAttackTimingRules(rules, timing), Is.EqualTo(baseline),
                "相同输入必须得到相同摘要（确定性）");

            var writer = new CanonicalHashWriter();
            rules.WriteHashComponents(writer);
            Assert.That(writer.ToCanonicalText(),
                Does.Contain("rules.reference_action_speed=20")
                    .And.Contain("rules.default_attack_recovery_ticks=30")
                    .And.Contain("rules.tick_rounding_mode=RoundHalfUp:0"));
        }

        [Test]
        public void MoveTimingAndReferenceSpeedParticipateInDefinitionHash()
        {
            var rules = BattleRules.FrozenV1;
            var timing = new MoveTimingSpec(30, 10);
            string baseline = BattleDefinitionHash.OfMoveTimingRules(rules, timing);

            Assert.That(
                BattleDefinitionHash.OfMoveTimingRules(rules with { ReferenceMoveSpeed = 21 }, timing),
                Is.Not.EqualTo(baseline), "ReferenceMoveSpeed 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfMoveTimingRules(rules, timing with { BaseStepTicks = 31 }),
                Is.Not.EqualTo(baseline), "BaseStepTicks 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfMoveTimingRules(rules, timing with { RecoveryTicks = 11 }),
                Is.Not.EqualTo(baseline), "RecoveryTicks 必须参与哈希");

            var writer = new CanonicalHashWriter();
            rules.WriteHashComponents(writer);
            Assert.That(writer.ToCanonicalText(), Does.Contain("rules.reference_move_speed=20"));
        }

        [Test]
        public void ForcedDisplacementProtocolAndStopReasonEncodingParticipateInDefinitionHash()
        {
            var baseline = BattleRules.FrozenV1;
            string digest = BattleDefinitionHash.OfBattleRules(baseline);

            string otherVersion = BattleDefinitionHash.OfBattleRules(
                baseline with
                {
                    ForcedDisplacementProtocolVersion = new ForcedDisplacementProtocolVersion(2)
                });
            Assert.That(otherVersion, Is.Not.EqualTo(digest), "协议版本必须参与哈希");

            string invalidVersion = BattleDefinitionHash.OfBattleRules(
                baseline with
                {
                    ForcedDisplacementProtocolVersion = new ForcedDisplacementProtocolVersion(0)
                });
            Assert.That(invalidVersion, Is.Not.EqualTo(digest));

            // 全部停止原因编码（名称+数值）写入规范哈希输入。
            var writer = new CanonicalHashWriter();
            baseline.WriteHashComponents(writer);
            string text = writer.ToCanonicalText();
            Assert.That(text, Does.Contain("forced_displacement.protocol_version=1"));
            Assert.That(text, Does.Contain("forced_displacement.stop_reason.Completed=0"));
            Assert.That(text, Does.Contain("forced_displacement.stop_reason.Boundary=1"));
            Assert.That(text, Does.Contain("forced_displacement.stop_reason.StaticObstacle=2"));
            Assert.That(text, Does.Contain("forced_displacement.stop_reason.OccupiedUnit=3"));
            Assert.That(text, Does.Contain("forced_displacement.stop_reason.DestinationContention=4"));
            Assert.That(text, Does.Contain("forced_displacement.stop_reason.DependencyCycle=5"));
            Assert.That(text, Does.Contain("forced_displacement.stop_reason.VolumeOverlap=6"));
        }

        [Test]
        public void ReactionTimingAndMinimumLeadParticipateInDefinitionHash()
        {
            var reactionRules = new ReactionRules(6, 30);
            var blockTiming = new BlockReactionTimingSpec(60, 10);
            var dodgeTiming = new DodgeReactionTimingSpec(30, 10);
            string baseline = BattleDefinitionHash.OfReactionTimingRules(reactionRules, blockTiming, dodgeTiming);

            Assert.That(
                BattleDefinitionHash.OfReactionTimingRules(
                    reactionRules with { MinimumReactionLeadTicks = 31 }, blockTiming, dodgeTiming),
                Is.Not.EqualTo(baseline), "MinimumReactionLeadTicks 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfReactionTimingRules(
                    reactionRules with { CommandIngressLeadTicks = 7 }, blockTiming, dodgeTiming),
                Is.Not.EqualTo(baseline), "CommandIngressLeadTicks 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfReactionTimingRules(
                    reactionRules, blockTiming with { ReactionWindupTicks = 61 }, dodgeTiming),
                Is.Not.EqualTo(baseline), "Block ReactionWindupTicks 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfReactionTimingRules(
                    reactionRules, blockTiming, dodgeTiming with { ReactionWindupTicks = 31 }),
                Is.Not.EqualTo(baseline), "Dodge ReactionWindupTicks 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfReactionTimingRules(
                    reactionRules, blockTiming with { RecoveryTicks = 11 }, dodgeTiming),
                Is.Not.EqualTo(baseline), "Block RecoveryTicks 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfReactionTimingRules(
                    reactionRules, blockTiming, dodgeTiming with { RecoveryTicks = 11 }),
                Is.Not.EqualTo(baseline), "Dodge RecoveryTicks 必须参与哈希");
        }

        [Test]
        public void AdrenalineRulesParticipateInDefinitionHash()
        {
            var rules = AdrenalineRules.FrozenV1;
            string baseline = BattleDefinitionHash.OfAdrenalineRules(rules);

            Assert.That(
                BattleDefinitionHash.OfAdrenalineRules(rules with { MaxAvailablePerCycle = 101 }),
                Is.Not.EqualTo(baseline), "MaxAvailablePerCycle 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfAdrenalineRules(rules with { DamageDealtGainQ10 = 103 }),
                Is.Not.EqualTo(baseline), "DamageDealtGainQ10 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfAdrenalineRules(rules with { DamageReceivedGainQ10 = 206 }),
                Is.Not.EqualTo(baseline), "DamageReceivedGainQ10 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfAdrenalineRules(rules with { SuccessfulBlockReward = 2 }),
                Is.Not.EqualTo(baseline), "SuccessfulBlockReward 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfAdrenalineRules(rules with { SuccessfulDodgeReward = 1 }),
                Is.Not.EqualTo(baseline), "SuccessfulDodgeReward 必须参与哈希");
            Assert.That(
                BattleDefinitionHash.OfAdrenalineRules(rules with { ClashReward = 51 }),
                Is.Not.EqualTo(baseline), "ClashReward 必须参与哈希");
        }
    }

    /// <summary>强制位移停止原因稳定编码（任务 02「必须产出」15）。</summary>
    public class ForcedDisplacementTests
    {
        [Test]
        public void ForcedDisplacementStopReasonEncodingIsStableAndUnique()
        {
            Assert.That((int)ForcedDisplacementStopReason.Completed, Is.EqualTo(0));
            Assert.That((int)ForcedDisplacementStopReason.Boundary, Is.EqualTo(1));
            Assert.That((int)ForcedDisplacementStopReason.StaticObstacle, Is.EqualTo(2));
            Assert.That((int)ForcedDisplacementStopReason.OccupiedUnit, Is.EqualTo(3));
            Assert.That((int)ForcedDisplacementStopReason.DestinationContention, Is.EqualTo(4));
            Assert.That((int)ForcedDisplacementStopReason.DependencyCycle, Is.EqualTo(5));
            Assert.That((int)ForcedDisplacementStopReason.VolumeOverlap, Is.EqualTo(6));

            var values = (ForcedDisplacementStopReason[])System.Enum.GetValues(
                typeof(ForcedDisplacementStopReason));
            Assert.That(values.Length, Is.EqualTo(7));
            var distinct = new System.Collections.Generic.HashSet<int>();
            foreach (var reason in values)
                Assert.That(distinct.Add((int)reason), Is.True, $"停止原因数值必须唯一：{reason}");
        }
    }
}
