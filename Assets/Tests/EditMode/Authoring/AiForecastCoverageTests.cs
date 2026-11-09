using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Authoring.Tests
{
    public sealed class AiForecastCoverageTests
    {
        sealed class Rig : IDisposable
        {
            public readonly BattleSimulation Sim;
            public readonly AiDecisionContext Context;
            public Rig(int ticks)
            {
                var d = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, AiPolicyKind.SustainedOffense,
                    AiExecutionKind.UtilityBehaviorTree, true, false, new AiForecastSettings(ticks));
                var runtime = new BattleRuntimeInputs(20260922, 10);
                var composition = new ProductionBattleComposition(d, d.Encounters[0].EncounterId, runtime);
                // Genuine windows/ingress, with no additional commands: exactly the forecast's conditional assumption.
                Sim = BattleSimulation.Create(d, d.Encounters[0].EncounterId, runtime,
                    new BattleSimulationAssembly(turnWindowSchedule: composition));
                composition.Attach(Sim); Sim.Step(0, Sim.CommandIngress.FreezeTick(0));
                var owner = Sim.CurrentTurnWindow.OwnerUnitId;
                var ai = composition.AiControllers.Single(c => c.BuildContext(Sim.DecisionSnapshotFor(
                    new ControllerId(c.RegisteredControllerIds[0])), new ControllerId(c.RegisteredControllerIds[0])).ControlledUnits.Contains(owner));
                var id = new ControllerId(ai.RegisteredControllerIds[0]);
                Context = ai.BuildContext(Sim.DecisionSnapshotFor(id), id);
            }
            public AiTacticalCandidate Candidate(string name) => AiTacticalCandidateBuilder.Ordinary(Context)
                .Single(c => c.Type == ActionType.Attack && c.Key.Contains("/action." + name + ".radius_2/"));
            public AiActionForecast Predict(AiTacticalCandidate candidate, int? ticks = null)
            {
                using var forecast = Context.ForecastFactory(Context.Snapshot);
                return forecast.Evaluate(candidate?.Request, new AiForecastSettings(ticks ?? Context.ForecastSettings.LookaheadTicks));
            }
            public void Dispose() => Sim.Dispose();
        }

        [TestCase("spear_thrust", 120, false, 127)]
        [TestCase("spear_thrust", 126, false, 127)]
        [TestCase("spear_thrust", 127, true, 127)]
        [TestCase("wide_cleave", 120, false, 151)]
        [TestCase("wide_cleave", 180, true, 151)]
        [TestCase("heavy_smash", 180, false, 211)]
        [TestCase("heavy_smash", 240, true, 211)]
        [TestCase("whirlwind", 240, false, 271)]
        [TestCase("whirlwind", 241, false, 271)]
        [TestCase("whirlwind", 271, true, 271)]
        [TestCase("whirlwind", 360, true, 271)]
        public void CoverageRequiresObservedOutcomeNotJustImpact(string action, int horizon, bool complete, long end)
        {
            using var rig = new Rig(horizon); var candidate = rig.Candidate(action);
            var outcome = rig.Predict(candidate); var baseline = rig.Predict(null);
            Assert.That(outcome.RejectionCode, Is.Null); Assert.That(outcome.RequiredThroughTick, Is.EqualTo(end));
            Assert.That(outcome.Coverage, Is.EqualTo(complete ? AiForecastCoverage.Complete : AiForecastCoverage.Uncovered));
            Assert.That(outcome.ObservedThroughTick, Is.EqualTo(horizon));
            var evaluation = AiTacticalPolicy.Evaluate(rig.Context, AiPolicyKind.SustainedOffense, candidate, baseline, outcome);
            Assert.That(evaluation.Score.HasValue, Is.EqualTo(complete));
            Assert.That(evaluation.DamageDeltaQ10.HasValue, Is.EqualTo(complete));
            if (!complete) Assert.That(evaluation.Reason, Is.EqualTo("OUTCOME_NOT_COVERED"));
        }

        [Test]
        public void PartialObservedHitStillDoesNotBecomeComparableFullOutcome()
        {
            using var rig = new Rig(120); var candidate = rig.Candidate("spear_thrust");
            var outcome = rig.Predict(candidate); var baseline = rig.Predict(null);
            Assert.That(baseline.Units.Single(u => u.UnitId == 2).HealthQ10 - outcome.Units.Single(u => u.UnitId == 2).HealthQ10, Is.EqualTo(25600));
            var score = AiTacticalPolicy.Evaluate(rig.Context, AiPolicyKind.SustainedOffense, candidate, baseline, outcome);
            Assert.That(score.Score, Is.Null); Assert.That(score.DamageDeltaQ10, Is.Null);
            Assert.That(AiTacticalPolicy.Decide(rig.Context, 20260922, AiPolicyKind.SustainedOffense).Evaluations
                .Where(e => e.Coverage == AiForecastCoverage.Uncovered).All(e => !e.Chosen && e.Score == null), Is.True);
        }

        [TestCase("heavy_smash")]
        [TestCase("whirlwind")]
        public void CoveredForecastMatchesRealConditionalKernel(string action)
        {
            using var rig = new Rig(360); var candidate = rig.Candidate(action);
            string before = rig.Sim.CurrentSnapshot.ComputeHash().ToString();
            var outcome = rig.Predict(candidate);
            Assert.That(rig.Sim.CurrentSnapshot.ComputeHash().ToString(), Is.EqualTo(before));
            Assert.That(rig.Sim.CommandIngress.FindEntry(rig.Context.ControllerId).Submit(candidate.Request), Is.Null);
            for (long tick = 1; tick <= 360 && !rig.Sim.IsEnded; tick++) rig.Sim.Step(tick, rig.Sim.CommandIngress.FreezeTick(tick));
            Assert.That(outcome.Coverage, Is.EqualTo(AiForecastCoverage.Complete));
            Assert.That(outcome.Units.Select(u => (u.UnitId, u.HealthQ10, u.X, u.Y, u.State)),
                Is.EqualTo(rig.Sim.CurrentSnapshot.Units.Select(u => (u.UnitId, u.HealthQ10, u.X, u.Y, u.State))));
        }

        [Test]
        public void BaselineAndCandidateMustUseSameHorizon()
        {
            using var rig = new Rig(360); var candidate = rig.Candidate("heavy_smash");
            Assert.Throws<LogicDefinitionException>(() => AiTacticalPolicy.Evaluate(rig.Context, AiPolicyKind.SustainedOffense,
                candidate, rig.Predict(null, 120), rig.Predict(candidate, 360)));
        }

        [Test]
        public void InvalidCommandIsRejectedRatherThanMerelyUncovered()
        {
            using var rig = new Rig(360); var candidate = rig.Candidate("heavy_smash");
            var payload = (ScheduleEditPayload)candidate.Request.Payload;
            candidate = candidate with { Request = candidate.Request with { Payload = new ScheduleEditPayload(new ScheduleEditOperation[] {
                ((AddOrdinaryPlanOperation)payload.Operations[0]) with { RequestedStartTick = 180 } }) } };
            var forecast = rig.Predict(candidate);
            Assert.That(forecast.RejectionCode, Is.Not.Null); Assert.That(forecast.Coverage, Is.EqualTo(AiForecastCoverage.Rejected));
        }

        [Test]
        public void SettingsAreVersionedAndValidatedWithoutChangingFrozenDefault()
        {
            var source = BattleDefinitionFixture.Definition;
            var old = FormalAiBaseline.Build(source, AiPolicyKind.SustainedOffense, AiExecutionKind.UtilityBehaviorTree, true, false);
            var hashes = new[] { 120, 180, 240, 360 }.Select(t => FormalAiBaseline.Build(source, AiPolicyKind.SustainedOffense,
                AiExecutionKind.UtilityBehaviorTree, true, false, new AiForecastSettings(t)).BattleDefinitionHashValue).ToArray();
            Assert.That(hashes.Distinct().Count(), Is.EqualTo(4)); Assert.That(hashes, Does.Not.Contain(old.BattleDefinitionHashValue));
            var binding = old.Encounters[0].Controllers[0];
            foreach (int invalid in new[] { 0, 361 }) Assert.Throws<LogicDefinitionException>(() => new AiControllerLogic(1)
                .RegisterController(binding with { AiForecast = new AiForecastSettings(invalid) }));
            Assert.Throws<LogicDefinitionException>(() => new CommandIngressRegistry().RegisterExternalEntry(binding with {
                SourceKind = CommandSourceKind.Player, AiPolicy = AiPolicyKind.Legacy, AiForecast = new AiForecastSettings(120) }));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void MovingOpponentCannotInvalidateAcceptedMove(bool forecast)
        {
            var d = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, AiPolicyKind.SustainedOffense,
                AiExecutionKind.UtilityBehaviorTree, false, false, new AiForecastSettings(120));
            var runtime = new BattleRuntimeInputs(20260922, 10);
            var composition = new ProductionBattleComposition(d, d.Encounters[0].EncounterId, runtime);
            using var sim = BattleSimulation.Create(d, d.Encounters[0].EncounterId, runtime,
                new BattleSimulationAssembly(turnWindowSchedule: composition));
            composition.Attach(sim);
            AiDecisionContext Context(string controller)
            {
                var id = new ControllerId(controller);
                return composition.AiControllers.Single(a => a.RegisteredControllerIds[0] == controller)
                    .BuildContext(sim.DecisionSnapshotFor(id), id);
            }
            for (long tick = 0; tick <= 180; tick++)
            {
                sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                string destination = tick == 0 ? "/-1/3" : tick == 102 ? "/-3/1" : tick == 156 ? "/-5/-1" : null;
                if (destination == null) continue;
                var context = Context("controller.enemy_ai");
                var request = AiTacticalCandidateBuilder.Ordinary(context).Single(c => c.Type == ActionType.Move
                    && c.Key.EndsWith(destination, StringComparison.Ordinal)).Request;
                Assert.That(sim.CommandIngress.FindEntry(context.ControllerId).Submit(request), Is.Null);
            }
            var actor = Context("controller.player");
            var move = AiTacticalCandidateBuilder.Ordinary(actor).Single(c => c.Type == ActionType.Move
                && c.Key.EndsWith("/-8/-4", StringComparison.Ordinal));
            if (forecast)
            {
                using var preview = actor.ForecastFactory(actor.Snapshot);
                Assert.That(preview.Evaluate(move.Request, actor.ForecastSettings).RejectionCode, Is.EqualTo(PathSearchCodes.PATH_INVALID_DESTINATION));
            }
            else
            {
                Assert.That(sim.CommandIngress.FindEntry(actor.ControllerId).Submit(move.Request), Is.Null);
                var result = sim.Step(181, sim.CommandIngress.FreezeTick(181));
                Assert.That(result.Events.Events.OfType<CommandRejectedEvent>().Single().ReasonCode, Is.EqualTo(PathSearchCodes.PATH_INVALID_DESTINATION));
                for (long tick = 182; tick <= 300; tick++) sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                Assert.That(sim.CurrentSnapshot.Units.Single(u => u.UnitId == 2).X, Is.EqualTo(-5));
                Assert.That(sim.CurrentSnapshot.Units.Single(u => u.UnitId == 2).Y, Is.EqualTo(-5));
            }
        }
    }
}
