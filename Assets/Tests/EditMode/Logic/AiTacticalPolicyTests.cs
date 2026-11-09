using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Logic.Tests
{
    public sealed class AiTacticalPolicyTests
    {
        [TestCase(AiPolicyKind.SustainedOffense, ActionType.Attack, false)]
        [TestCase(AiPolicyKind.SeekInterrupt, ActionType.Attack, true)]
        [TestCase(AiPolicyKind.CautiousDefense, ActionType.Guard, false)]
        public void NativeIngressAndReplayRegeneratePolicyDecisions(AiPolicyKind policy, ActionType firstType, bool clash)
        {
            var result = AiPolicyExperiment.Run(new AiPolicyExperimentInput(policy, AiPolicySituation.VisibleThreat));
            Assert.That(result.Events.OfType<CommandRejectedEvent>(), Is.Empty);
            Assert.That(result.Decisions.Any(d => d.HasSelection), Is.True);
            Assert.That(result.Decisions.Where(d => d.HasSelection).All(d => d.Evaluations.Count(e => e.Chosen) == 1), Is.True);
            var first = result.Decisions.SelectMany(d => d.Evaluations).First(e => e.Chosen);
            Assert.That(first.Type, Is.EqualTo(firstType));
            Assert.That(first.Clashes > 0, Is.EqualTo(clash));
            TestContext.WriteLine(string.Join("\n", result.Decisions.Where(d => d.HasSelection).Select(d =>
                d.DecisionTick + ": " + d.Evaluations.Single(e => e.Chosen))));
            AiPolicyExperiment.VerifyReplay(result);
        }

        [Test]
        public void SeekingInterruptUsesActualCancellationBeforeThreatImpact()
        {
            var result = AiPolicyExperiment.Run(new AiPolicyExperimentInput(AiPolicyKind.SeekInterrupt, AiPolicySituation.EarlyInterrupt));
            var selected = result.Decisions.SelectMany(d => d.Evaluations).First(e => e.Chosen);
            Assert.That(selected.InterruptedThreats, Is.GreaterThan(0));
            Assert.That(result.Events.OfType<ActionPlanTerminatedEvent>().Any(e => e.OwnerUnitId == 2 && e.Tick < 100), Is.True);
            Assert.That(result.Events.OfType<CommandRejectedEvent>(), Is.Empty);
            AiPolicyExperiment.VerifyReplay(result);
        }

        [Test]
        public void ProactiveMoveUsesRealDestinationAndSharedPathSolver()
        {
            var result = AiPolicyExperiment.Run(new AiPolicyExperimentInput(AiPolicyKind.SustainedOffense, AiPolicySituation.Approach));
            Assert.That(result.Decisions.SelectMany(d => d.Evaluations).Any(e => e.Type == ActionType.Move && e.Chosen), Is.True);
            Assert.That(result.Events.OfType<ActionPlanCreatedEvent>().Any(e => e.OwnerUnitId == 1 && e.ActionType == (int)ActionType.Move), Is.True);
            Assert.That(result.Events.OfType<CommandRejectedEvent>(), Is.Empty);
            AiPolicyExperiment.VerifyReplay(result);
        }

        [Test]
        public void GuardIsAnOrdinaryLegalSubmissionAgainstPublicThreat()
        {
            var result = AiPolicyExperiment.Run(new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense, AiPolicySituation.GuardOnly));
            Assert.That(result.Decisions.SelectMany(d => d.Evaluations).Any(e => e.Type == ActionType.Guard && e.Chosen), Is.True);
            Assert.That(result.Events.OfType<ActionPlanCreatedEvent>().Any(e => e.OwnerUnitId == 1 && e.ActionType == (int)ActionType.Guard), Is.True);
            Assert.That(result.Events.OfType<CommandRejectedEvent>(), Is.Empty);
            AiPolicyExperiment.VerifyReplay(result);
        }

        [Test]
        public void UnfundedReactionIsExplainedAndNeverSubmitted()
        {
            var result = AiPolicyExperiment.Run(new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense, AiPolicySituation.UnfundedReaction));
            var reactions = result.Decisions.SelectMany(d => d.Evaluations).Where(e => e.IsReaction).ToArray();
            Assert.That(reactions.Length, Is.GreaterThan(0));
            Assert.That(reactions.Where(e => e.Cost > 0).Any(e => e.RejectionCode != null), Is.True);
            Assert.That(result.Events.OfType<CommandRejectedEvent>(), Is.Empty);
        }

        [Test]
        public void FundedReactionPrecedesOrdinaryTierAndReplays()
        {
            var result = AiPolicyExperiment.Run(new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense, AiPolicySituation.FundedReaction));
            TestContext.WriteLine(string.Join("\n", result.Decisions.SelectMany(d => d.Evaluations).Where(e => e.IsReaction)
                .GroupBy(e => new { e.Type, e.RejectionCode, e.Score }).Select(g => g.Key + " count=" + g.Count())));
            TestContext.WriteLine(string.Join("\n", result.Events.Where(e => e is DamageChannelResolvedEvent || e is AdrenalineLedgerChangedEvent).Take(12)));
            Assert.That(result.Decisions.Any(d => d.Evaluations.Any(e => e.IsReaction && e.Chosen)), Is.True);
            Assert.That(result.Decisions.Where(d => d.Evaluations.Any(e => e.IsReaction && e.Chosen)).All(d => d.NormalCandidateCount == 0), Is.True);
            Assert.That(result.Events.OfType<CommandRejectedEvent>(), Is.Empty);
            AiPolicyExperiment.VerifyReplay(result);
        }

        [Test]
        public void ForecastOnlyReadsFilteredFactsAndDoesNotMutateAuthority()
        {
            var input = new AiPolicyExperimentInput(AiPolicyKind.SustainedOffense, AiPolicySituation.OpenAttack);
            var definition = AiPolicyExperiment.Build(input);
            var composition = new ProductionBattleComposition(definition, CombatExperimentDefinition.Encounter, new BattleRuntimeInputs(31, 4));
            using var sim = BattleSimulation.Create(definition, CombatExperimentDefinition.Encounter, new BattleRuntimeInputs(31, 4), composition.Assembly);
            composition.Attach(sim); sim.Step(0, sim.CommandIngress.FreezeTick(0));
            var hidden = new AddOrdinaryPlanOperation(1, new UnitId(2), CombatExperimentDefinition.Action("quick"), 10,
                PrimaryTargetUnitId: new UnitId(1), Facing: Grid.GridDirection.West);
            Assert.That(sim.CommandIngress.FindEntry(CombatExperimentDefinition.Controller(2)).SubmitAtDefaultTick(
                new ScheduleEditScope(sim.ScheduleRevision, sim.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[] { hidden })), Is.Null);
            sim.Step(1, sim.CommandIngress.FreezeTick(1));
            var snapshot = sim.DecisionSnapshotFor(CombatExperimentDefinition.Controller(1));
            Assert.That(sim.CurrentSnapshot.Plans.Count, Is.EqualTo(1));
            Assert.That(snapshot.VisiblePlans, Is.Empty, "Foreign Editable plans must be absent from the forecast input");
            var context = composition.AiControllers.Single().BuildContext(snapshot, snapshot.ControllerId);
            string before = sim.CurrentSnapshot.ComputeHash().ToString();
            using var forecast = context.ForecastFactory(snapshot);
            var first = forecast.Evaluate(null); var second = forecast.Evaluate(null);
            Assert.That(first.Units.Single(u => u.UnitId == 1).HealthQ10, Is.EqualTo(snapshot.VisibleUnits.Single(u => u.UnitId == 1).HealthQ10));
            Assert.That(second.Units, Is.EqualTo(first.Units));
            Assert.That(sim.CurrentSnapshot.ComputeHash().ToString(), Is.EqualTo(before));
            Assert.That(sim.Tick, Is.EqualTo(1));
        }

        [Test]
        public void PolicyIdentityIsImmutableDefinitionInputAndRejectsUnknownKinds()
        {
            var a = AiPolicyExperiment.Build(new AiPolicyExperimentInput(AiPolicyKind.SustainedOffense, AiPolicySituation.OpenAttack));
            var b = AiPolicyExperiment.Build(new AiPolicyExperimentInput(AiPolicyKind.SeekInterrupt, AiPolicySituation.OpenAttack));
            Assert.That(a.BattleDefinitionHashValue, Is.Not.EqualTo(b.BattleDefinitionHashValue));
            var ai = new AiControllerLogic(31);
            Assert.Throws<LogicDefinitionException>(() => ai.RegisterController(new ControllerBinding(new ControllerId("controller.invalid"),
                CommandSourceKind.Ai, new[] { new EncounterSlotId("slot.invalid") }, (AiPolicyKind)99)));
        }

        [Test]
        public void ReplayRejectsChangedPolicyAndPlayerCannotCarryAiPolicy()
        {
            var result = AiPolicyExperiment.Run(new AiPolicyExperimentInput(AiPolicyKind.SustainedOffense, AiPolicySituation.OpenAttack, MaxTicks: 1));
            var changed = AiPolicyExperiment.Build(new AiPolicyExperimentInput(AiPolicyKind.SeekInterrupt, AiPolicySituation.OpenAttack, MaxTicks: 1));
            using var player = new ReplayPlayer(changed);
            Assert.Throws<LogicDefinitionException>(() => player.Load(result.Replay));
            var definition = result.Definition;
            var encounter = definition.Encounters[0];
            definition = definition with { Encounters = new[] { encounter with { Controllers = encounter.Controllers.Select(c =>
                c.SourceKind == CommandSourceKind.Player ? c with { AiPolicy = AiPolicyKind.CautiousDefense } : c).ToArray() } } };
            var invalid = definition;
            Assert.Throws<LogicDefinitionException>(() => ProductionBattleComposition.Create(invalid, CombatExperimentDefinition.Encounter, new BattleRuntimeInputs(31, 4)));
        }

        [Test]
        public void WaitPreservesWindowAndCommittedLane()
        {
            var result = AiPolicyExperiment.Run(new AiPolicyExperimentInput(AiPolicyKind.SustainedOffense, AiPolicySituation.OpenAttack));
            Assert.That(result.Decisions.Any(d => d.NoSelectionReason == "WAIT_NO_OWN_WINDOW"), Is.True);
            Assert.That(result.Decisions.Any(d => d.NoSelectionReason == "WAIT_LANE_COMMITTED"), Is.True);
            Assert.That(result.Decisions.Where(d => d.HasSelection).Any(d => d.Selected.Payload is WindowCommandPayload), Is.False);
        }
    }
}
