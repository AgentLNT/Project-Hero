using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Authoring.Tests
{
    public sealed class FormalAiBaselineTests
    {
        [Test]
        public void CompletingTaskDoesNotSkipTheNextDueUtilityDecision()
        {
            var source = BattleDefinitionFixture.Definition;
            string[] Trace(AiExecutionKind kind)
            {
                var d = FormalAiBaseline.Build(source, AiPolicyKind.SustainedOffense, kind, true, false);
                var composition = new ProductionBattleComposition(d, d.Encounters[0].EncounterId, new BattleRuntimeInputs(20260922, 10));
                using var sim = BattleSimulation.Create(d, d.Encounters[0].EncounterId, new BattleRuntimeInputs(20260922, 10), composition.Assembly);
                composition.Attach(sim); var trace = new System.Collections.Generic.List<string>();
                for (int tick = 0; tick < 180; tick++)
                {
                    sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                    foreach (var ai in composition.AiControllers)
                    {
                        string id = ai.RegisteredControllerIds[0]; var decision = ai.LastDecisionOf(id);
                        if (decision?.DecisionTick == tick && decision.HasSelection)
                            trace.Add(tick + ":" + id + ":" + decision.Evaluations.Single(e => e.Chosen).Key);
                    }
                }
                return trace.ToArray();
            }
            var baseline = Trace(AiExecutionKind.SingleCommand);
            Assert.That(baseline.Any(s => s.StartsWith("126:controller.enemy_ai:")), Is.True, "Fixture must exercise the completion/cadence boundary");
            Assert.That(Trace(AiExecutionKind.UtilityBehaviorTree), Is.EqualTo(baseline),
                "Acknowledging task completion must permit a new choice on the same due Tick, without an extra six-Tick wait");
        }
        sealed class Feedback : IAiExecutionFeedback
        {
            public string Error;
            public bool Missing;
            public ActionPlanState State = ActionPlanState.Running;
            public ActionTerminationReason Reason = ActionTerminationReason.CancelledByCommand;
            public int ReadCount;
            public AiSubmissionResult ReadSubmission(ControllerId controller, long tick, long ordinal)
            { ReadCount++; return Missing ? null : new AiSubmissionResult(Error, new[] { ordinal }); }
            public ActionPlanSnapshot ReadPlan(ControllerId controller, long id)
                => new ActionPlanSnapshot(id, 1, State: (int)State, TerminationReason: State == ActionPlanState.Terminated ? (int)Reason : 0);
        }
        static AiDecisionContext QuietContext(BattleSimulation sim)
        {
            var controller = sim.Definition.Encounters[0].Controllers[0].ControllerId;
            return new AiControllerLogic(31).BuildContext(sim.DecisionSnapshotFor(controller), controller)
                with { OwnWindow = null, Opportunities = Array.Empty<AiOpportunityView>() };
        }
        [TestCase(false)] [TestCase(true)]
        public void MissingOrRejectedCommandNeverBecomesSuccessfulTask(bool missing)
        {
            var d = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, AiPolicyKind.SustainedOffense, AiExecutionKind.SingleCommand, true, false);
            using var sim = ProductionBattleComposition.Create(d, d.Encounters[0].EncounterId, new BattleRuntimeInputs(31, 10));
            sim.Step(0, sim.CommandIngress.FreezeTick(0));
            var feedback = new Feedback { Missing = missing, Error = "SCHEDULE_REVISION_MISMATCH" };
            var memory = AiBehaviorMemory.Empty with { Ordinary = new AiBehaviorTask(1, 0, 7) };
            var decision = AiBehaviorTree.Tick(QuietContext(sim), 31, AiPolicyKind.SustainedOffense, memory, feedback, 8);
            Assert.That(decision.HasSelection, Is.False); Assert.That(decision.Behavior.Ordinary.Status, Is.EqualTo(AiBehaviorStatus.Failure));
            Assert.That(decision.Behavior.Ordinary.PlanId, Is.Zero); Assert.That(decision.Behavior.Ordinary.Cursor, Is.Zero);
        }
        [Test]
        public void ResponsePriorityStillAcknowledgesOrdinarySubmission()
        {
            var d = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, AiPolicyKind.SustainedOffense, AiExecutionKind.SingleCommand, true, false);
            using var sim = ProductionBattleComposition.Create(d, d.Encounters[0].EncounterId, new BattleRuntimeInputs(31, 10));
            sim.Step(0, sim.CommandIngress.FreezeTick(0));
            var feedback = new Feedback();
            var memory = new AiBehaviorMemory(new AiBehaviorTask(1, 0, 7), new AiBehaviorTask(1, 0, 8));
            var decision = AiBehaviorTree.Tick(QuietContext(sim), 31, AiPolicyKind.SustainedOffense, memory, feedback, 9);
            Assert.That(feedback.ReadCount, Is.EqualTo(2)); Assert.That(decision.HasSelection, Is.False);
            Assert.That(decision.Behavior.Ordinary.PlanId, Is.EqualTo(7)); Assert.That(decision.Behavior.Ordinary.Cursor, Is.EqualTo(2));
            Assert.That(decision.Behavior.Reaction.PlanId, Is.EqualTo(8)); Assert.That(decision.Behavior.Reaction.Cursor, Is.EqualTo(2));
            Assert.That(decision.Behavior.Ordinary.Status, Is.EqualTo(AiBehaviorStatus.Running));
        }
        [TestCase(ActionPlanState.Completed, AiBehaviorStatus.Success, ActionTerminationReason.None)]
        [TestCase(ActionPlanState.Terminated, AiBehaviorStatus.Failure, ActionTerminationReason.CancelledByCommand)]
        [TestCase(ActionPlanState.Terminated, AiBehaviorStatus.Success, ActionTerminationReason.ReactionTriggered)]
        [TestCase(ActionPlanState.Terminated, AiBehaviorStatus.Success, ActionTerminationReason.GuardQuotaExhausted)]
        public void RealTerminalStateDeterminesTreeResult(ActionPlanState state, AiBehaviorStatus expected, ActionTerminationReason reason)
        {
            var d = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, AiPolicyKind.SustainedOffense, AiExecutionKind.SingleCommand, true, false);
            using var sim = ProductionBattleComposition.Create(d, d.Encounters[0].EncounterId, new BattleRuntimeInputs(31, 10));
            sim.Step(0, sim.CommandIngress.FreezeTick(0));
            var memory = AiBehaviorMemory.Empty with { Ordinary = new AiBehaviorTask(2, 0, 7, 7) };
            var decision = AiBehaviorTree.Tick(QuietContext(sim), 31, AiPolicyKind.SustainedOffense, memory, new Feedback { State = state, Reason = reason }, 8);
            Assert.That(decision.Behavior.Ordinary.Status, Is.EqualTo(expected)); Assert.That(decision.Behavior.Ordinary.Cursor, Is.Zero);
        }
        [Test]
        public void EveryAttackInTheActorsFormalSetGetsCandidates()
        {
            var d = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, AiPolicyKind.SustainedOffense, AiExecutionKind.SingleCommand, true, false);
            var composition = new ProductionBattleComposition(d, d.Encounters[0].EncounterId, new BattleRuntimeInputs(31, 10));
            using var sim = BattleSimulation.Create(d, d.Encounters[0].EncounterId, new BattleRuntimeInputs(31, 10), composition.Assembly); composition.Attach(sim);
            sim.Step(0, sim.CommandIngress.FreezeTick(0));
            var contexts = composition.AiControllers.Select(ai => ai.BuildContext(sim.DecisionSnapshotFor(new ControllerId(ai.RegisteredControllerIds[0])), new ControllerId(ai.RegisteredControllerIds[0])));
            var context = contexts.Single(c => c.HasSubmissionWindow); var owner = new UnitId(context.OwnWindow.OwnerUnitId);
            var expected = context.Snapshot.ActionSetOf(owner).Where(id => context.Snapshot.FindAction(id).Type == ActionType.Attack).ToArray();
            var actual = AiTacticalCandidateBuilder.Ordinary(context).Where(c => c.Type == ActionType.Attack)
                .Select(c => ((AddOrdinaryPlanOperation)((ScheduleEditPayload)c.Request.Payload).Operations[0]).ActionSpecId).Distinct().ToArray();
            Assert.That(expected.Length, Is.EqualTo(5)); Assert.That(actual, Is.EquivalentTo(expected));
            Assert.That(AiBehaviorTree.Unsupported(context), Is.Empty);
        }
        [Test]
        public void BaselineKeepsEveryFormalActionAndUnitUnchanged()
        {
            var source = BattleDefinitionFixture.Definition;
            var d = FormalAiBaseline.Build(source, AiPolicyKind.SustainedOffense, AiExecutionKind.UtilityBehaviorTree, true, false);
            Assert.That(d.Actions, Is.SameAs(source.Actions));
            Assert.That(d.ActionSets, Is.SameAs(source.ActionSets));
            Assert.That(d.Units, Is.SameAs(source.Units));
            Assert.That(d.AttackPatterns, Is.SameAs(source.AttackPatterns));
            Assert.That(d.AdrenalineRules, Is.SameAs(source.AdrenalineRules));
            Assert.That(d.Actions.All(a => !a.ActionSpecId.Value.StartsWith("action.experiment.")), Is.True);
            Assert.That(d.ActionSets.All(s => s.ActionSpecIds.Count(id => d.FindAction(id).Type == ActionType.Attack) > 1), Is.True);
            Assert.That(d.BattleDefinitionHashValue, Is.Not.EqualTo(source.BattleDefinitionHashValue));
        }

        [TestCase(AiPolicyKind.SustainedOffense)]
        [TestCase(AiPolicyKind.SeekInterrupt)]
        [TestCase(AiPolicyKind.CautiousDefense)]
        public void FormalHybridWaitsForRealPlansAndReplaysAllAi(AiPolicyKind policy)
        {
            var d = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, policy, AiExecutionKind.UtilityBehaviorTree, true, false);
            var encounter = d.Encounters[0]; var inputs = new BattleRuntimeInputs(20260922, 10);
            var composition = new ProductionBattleComposition(d, encounter.EncounterId, inputs);
            using var sim = BattleSimulation.Create(d, encounter.EncounterId, inputs, composition.Assembly); composition.Attach(sim);
            var recorder = new ReplayRecorder(sim); int queued = 0, running = 0, completed = 0;
            for (int tick = 0; tick < 480 && !sim.IsEnded; tick++)
            {
                recorder.CaptureBeforeStep(); var step = sim.Step(tick, sim.CommandIngress.FreezeTick(tick)); recorder.RecordCommittedStep(step);
                Assert.That(step.Events.Events.OfType<CommandRejectedEvent>(), Is.Empty);
                if (sim.IsEnded) continue; // Finalization deliberately skips the decision phase.
                foreach (var state in step.Snapshot.AiControllers)
                {
                    Assert.That(state.Behavior, Is.Not.Null);
                    foreach (var task in new[] { state.Behavior.Ordinary, state.Behavior.Reaction })
                    {
                        if (task.Cursor == 1) { queued++; Assert.That(task.PlanId, Is.Zero); Assert.That(task.RequestTick, Is.EqualTo(tick + 1)); }
                        if (task.Cursor == 2)
                        {
                            running++; var plan = sim.AiExecutionFeedback.ReadPlan(new ControllerId(state.ControllerId), task.PlanId);
                            Assert.That(plan, Is.Not.Null); Assert.That(task.Status, Is.EqualTo(AiBehaviorStatus.Running));
                        }
                        if (task.Status == AiBehaviorStatus.Success && task.Cursor == 0)
                        {
                            completed++;
                            var plan = sim.AiExecutionFeedback.ReadPlan(new ControllerId(state.ControllerId), task.PlanId);
                            if (plan.State != (int)ActionPlanState.Completed)
                            {
                                Assert.That(plan.State, Is.EqualTo((int)ActionPlanState.Terminated));
                                Assert.That(plan.TerminationReason, Is.EqualTo((int)ActionTerminationReason.ReactionTriggered)
                                    .Or.EqualTo((int)ActionTerminationReason.GuardQuotaExhausted));
                                if (plan.TerminationReason == (int)ActionTerminationReason.ReactionTriggered)
                                    Assert.That(plan.ReactionTriggeredAtTick, Is.GreaterThanOrEqualTo(0));
                                else Assert.That(plan.DefenseQuotaRemainingQ10, Is.Zero);
                            }
                        }
                    }
                }
            }
            Assert.That(queued, Is.GreaterThan(0)); Assert.That(running, Is.GreaterThan(0)); Assert.That(completed, Is.GreaterThan(0));
            var replay = recorder.BuildReplay(); Assert.That(replay.Submissions, Is.Empty);
            using var player = new ReplayPlayer(d); player.Load(replay); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            Assert.That(player.Deviation, Is.Null);
        }

        [Test]
        public void FeedbackIsBoundToOwnerAndProducerOrdinal()
        {
            var d = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, AiPolicyKind.SustainedOffense, AiExecutionKind.UtilityBehaviorTree, true, false);
            using var sim = ProductionBattleComposition.Create(d, d.Encounters[0].EncounterId, new BattleRuntimeInputs(31, 10));
            sim.Step(0, sim.CommandIngress.FreezeTick(0));
            var queued = sim.CurrentSnapshot.AiControllers.Single(c => c.Behavior.Ordinary.Cursor == 1);
            var controller = new ControllerId(queued.ControllerId); var task = queued.Behavior.Ordinary;
            sim.Step(1, sim.CommandIngress.FreezeTick(1));
            var result = sim.AiExecutionFeedback.ReadSubmission(controller, task.RequestTick, task.ProducerOrdinal);
            Assert.That(result.RejectionCode, Is.Null); Assert.That(result.PlanIds.Count, Is.EqualTo(1));
            var other = d.Encounters[0].Controllers.Single(c => c.ControllerId != controller).ControllerId;
            Assert.That(sim.AiExecutionFeedback.ReadSubmission(other, task.RequestTick, task.ProducerOrdinal), Is.Null);
            Assert.That(sim.AiExecutionFeedback.ReadPlan(other, result.PlanIds[0]), Is.Null);
            Assert.That(sim.AiExecutionFeedback.ReadSubmission(controller, task.RequestTick, task.ProducerOrdinal + 1), Is.Null);
        }

        [Test]
        public void HybridModeIsVersionedAndCannotBeAssignedToAPlayerOrLegacyPolicy()
        {
            var source = BattleDefinitionFixture.Definition;
            var a = FormalAiBaseline.Build(source, AiPolicyKind.SustainedOffense, AiExecutionKind.SingleCommand, false, false);
            var b = FormalAiBaseline.Build(source, AiPolicyKind.SustainedOffense, AiExecutionKind.UtilityBehaviorTree, false, false);
            Assert.That(a.BattleDefinitionHashValue, Is.Not.EqualTo(b.BattleDefinitionHashValue));
            var binding = b.Encounters[0].Controllers[0];
            var ingress = new CommandIngressRegistry();
            Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() => ingress.RegisterExternalEntry(binding with { SourceKind = CommandSourceKind.Player }));
            Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() => new AiControllerLogic(1).RegisterController(binding with { AiPolicy = AiPolicyKind.Legacy }));
        }
    }
}
