using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Authoring.Tests
{
    public sealed class FormalAiMatchupTests
    {
        [TestCase(1, 1)] [TestCase(1, 2)] [TestCase(1, 3)]
        [TestCase(2, 1)] [TestCase(2, 2)] [TestCase(2, 3)]
        [TestCase(3, 1)] [TestCase(3, 2)] [TestCase(3, 3)]
        public void AllOrderedPairsKeepFormalCombatContentAndAssignEachRole(int hero, int enemy)
        {
            var source = BattleDefinitionFixture.Definition;
            var d = FormalAiMatchup.Build(source, (AiPolicyKind)hero, (AiPolicyKind)enemy, FormalAiLayout.Near, false);
            var controllers = d.Encounters[0].Controllers;
            Assert.That(controllers.Single(c => c.ControlledSlots.Any(s => s.Value == "hero")).AiPolicy, Is.EqualTo((AiPolicyKind)hero));
            Assert.That(controllers.Single(c => c.ControlledSlots.Any(s => s.Value == "enemy")).AiPolicy, Is.EqualTo((AiPolicyKind)enemy));
            Assert.That(controllers.All(c => c.SourceKind == Logic.Definitions.CommandSourceKind.Ai
                && c.AiExecution == AiExecutionKind.UtilityBehaviorTree && c.AiForecast == new AiForecastSettings(360, true, true)), Is.True);
            Assert.That(d.Actions, Is.SameAs(source.Actions)); Assert.That(d.Units, Is.SameAs(source.Units));
            Assert.That(d.ActionSets, Is.SameAs(source.ActionSets)); Assert.That(d.AdrenalineRules, Is.SameAs(source.AdrenalineRules));
            Assert.That(d.AttackPatterns, Is.SameAs(source.AttackPatterns)); Assert.That(d.Volumes, Is.SameAs(source.Volumes));
            Assert.That(source.Encounters[0].Controllers.Any(c => c.SourceKind == Logic.Definitions.CommandSourceKind.Player), Is.True);
            Assert.That(d.BattleDefinitionHashValue, Is.EqualTo(FormalAiMatchup.Build(source, (AiPolicyKind)hero,
                (AiPolicyKind)enemy, FormalAiLayout.Near, false).BattleDefinitionHashValue));
            if (hero != enemy)
                Assert.That(d.BattleDefinitionHashValue, Is.Not.EqualTo(FormalAiMatchup.Build(source, (AiPolicyKind)enemy,
                    (AiPolicyKind)hero, FormalAiLayout.Near, false).BattleDefinitionHashValue));
        }

        [TestCase(FormalAiLayout.Near, 8)] [TestCase(FormalAiLayout.Main, 20)] [TestCase(FormalAiLayout.Far, 40)]
        public void DistancesAndMirrorsAreExplicitValidatedEncounterInputs(FormalAiLayout layout, int distance)
        {
            var source = BattleDefinitionFixture.Definition;
            var a = FormalAiMatchup.Build(source, AiPolicyKind.SustainedOffense, AiPolicyKind.CautiousDefense, layout, false);
            var b = FormalAiMatchup.Build(source, AiPolicyKind.SustainedOffense, AiPolicyKind.CautiousDefense, layout, true);
            var slots = a.Encounters[0].Slots;
            Assert.That(Math.Abs(slots[0].InitialPosition.X - slots[1].InitialPosition.X)
                + Math.Abs(slots[0].InitialPosition.Y - slots[1].InitialPosition.Y), Is.EqualTo(distance));
            foreach (var s in slots)
            {
                var mirrored = b.Encounters[0].Slots.Single(x => x.SlotId == s.SlotId);
                Assert.That(mirrored.InitialPosition.X, Is.EqualTo(-s.InitialPosition.X));
                Assert.That(mirrored.InitialPosition.Y, Is.EqualTo(-s.InitialPosition.Y));
                Assert.That((int)mirrored.InitialFacing, Is.EqualTo(((int)s.InitialFacing + 6) % 12));
                Assert.That(a.Encounters[0].GridBoundary.Contains(s.InitialPosition), Is.True);
            }
            Assert.That(a.BattleDefinitionHashValue, Is.Not.EqualTo(b.BattleDefinitionHashValue));
            using var simA = ProductionBattleComposition.Create(a, a.Encounters[0].EncounterId, BattleDefinitionFixture.RuntimeInputs);
            using var simB = ProductionBattleComposition.Create(b, b.Encounters[0].EncounterId, BattleDefinitionFixture.RuntimeInputs);
            Assert.That(simA.CurrentSnapshot.Units.Count, Is.EqualTo(2)); Assert.That(simB.CurrentSnapshot.Units.Count, Is.EqualTo(2));
        }

        [TestCase(0, 1, 0)] [TestCase(1, 99, 0)] [TestCase(1, 2, 99)]
        public void InvalidPairsOrLayoutsDoNotSilentlyBecomeLegacy(int hero, int enemy, int layout)
            => Assert.Throws<LogicDefinitionException>(() => FormalAiMatchup.Build(BattleDefinitionFixture.Definition,
                (AiPolicyKind)hero, (AiPolicyKind)enemy, (FormalAiLayout)layout, false));

        [Test]
        public void MixedPoliciesUseRegisteredProductionIngressAndReplayTheirOwnDecisions()
        {
            var d = FormalAiMatchup.Build(BattleDefinitionFixture.Definition, AiPolicyKind.SustainedOffense,
                AiPolicyKind.CautiousDefense, FormalAiLayout.Near, false);
            var input = BattleDefinitionFixture.RuntimeInputs;
            using var sim = ProductionBattleComposition.Create(d, d.Encounters[0].EncounterId, input);
            var recorder = new ReplayRecorder(sim); int commands = 0;
            for (int tick = 0; tick < 120 && !sim.IsEnded; tick++)
            {
                recorder.CaptureBeforeStep(); var step = sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                recorder.RecordCommittedStep(step); commands += sim.LastCommandSet.Envelopes.Count;
                Assert.That(step.Events.Events.Any(e => e is Logic.Events.CommandRejectedEvent || e is Logic.Events.CommandIngressRejectedEvent), Is.False);
            }
            var replay = recorder.BuildReplay(); Assert.That(commands, Is.GreaterThan(0)); Assert.That(replay.Submissions, Is.Empty);
            using var player = new ReplayPlayer(d); player.Load(replay); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            Assert.That(player.Deviation, Is.Null); Assert.That(player.IsComplete, Is.True);
        }
    }
}
