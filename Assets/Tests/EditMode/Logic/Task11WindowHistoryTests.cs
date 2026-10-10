using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Replay;
using System.IO;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.AI;

namespace ProjectHero.Logic.Tests
{
    public sealed class Task11WindowHistoryTests
    {
        [Test] public void AiUsesTheRealSchedulePreviewAndClosesUnaffordableRemainderThroughItsTrustedEntry()
        {
            using (var sim = Task09A2Fixture.NewSim())
            {
                sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Monster, 13);
                Step(sim);
                var ai = new AiControllerLogic(Task09A2Fixture.Inputs.InitialRngSeed);
                ai.AttachPorts(sim.AiReactionOpportunities, sim.AiActionPlanLookup, sim.MovementPathCalculator,
                    (issuer, request) => {
                        var scope = (ScheduleEditScope)request.Scope;
                        return sim.ScheduleEditor.Apply(((ScheduleEditPayload)request.Payload).Operations, request.TargetTick,
                            scope.ExpectedScheduleRevision, sim.ScheduleRevision, preview: true,
                            expectedWindowId: scope.ExpectedWindowId, issuer: issuer).RejectionCode;
                    });
                var first = AiDecisionFunction.Decide(ai.BuildContext(sim.DecisionSnapshotFor(Task09A2Fixture.AiId), Task09A2Fixture.AiId), ai.BattleSeed);
                Assert.That(first.Selected.Payload, Is.TypeOf<ScheduleEditPayload>());
                Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, first.Selected);
                Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1)), Is.Empty);
                Assert.That(sim.CurrentTurnWindow.AvailableBudgetTicks, Is.EqualTo(1));
                var plans = Task09A2Fixture.ScheduleFingerprint(sim);
                var window = Task09A2Fixture.WindowFingerprint(sim, sim.CurrentTurnWindow.WindowId);
                var second = AiDecisionFunction.Decide(ai.BuildContext(sim.DecisionSnapshotFor(Task09A2Fixture.AiId), Task09A2Fixture.AiId), ai.BattleSeed);
                Assert.That(second.Selected.Payload, Is.TypeOf<WindowCommandPayload>());
                Assert.That(((WindowCommandPayload)second.Selected.Payload).WindowKind, Is.EqualTo(WindowCommandKind.CloseOwnWindow));
                Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(plans), "Candidate preview cannot schedule plans.");
                Assert.That(Task09A2Fixture.WindowFingerprint(sim, sim.CurrentTurnWindow.WindowId), Is.EqualTo(window), "AI cannot directly close a window.");
                Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, second.Selected);
                var closed = Task09A2Fixture.Step(sim, 2);
                Assert.That(Task09A2Fixture.AllRejectionCodes(closed), Is.Empty);
                Assert.That(closed.Events.Events.OfType<TurnWindowClosedEvent>().Single().Reason, Is.EqualTo(TurnWindowCloseReason.OwnerRequested));
                Assert.That(sim.CurrentTurnWindow, Is.Null);
                Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.EqualTo(1), "Execution survives submission-window closure.");
            }
        }
        [Test] public void FullyReservedWindowClosesWithBudgetExhaustedWithoutCancellingItsFutureAttack()
        {
            using (var sim = Task09A2Fixture.NewSim())
            {
                sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero,
                    Task09A2Fixture.AttackWindupTicks + Task09A2Fixture.AttackRecoveryTicks);
                Step(sim);
                Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(1, 0, sim.CurrentTurnWindow.WindowId,
                    Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Monster, 10));
                var result = Task09A2Fixture.Step(sim, 1);
                Assert.That(Task09A2Fixture.AllRejectionCodes(result), Is.Empty);
                Assert.That(sim.CurrentTurnWindow, Is.Null);
                var closed = result.Events.Events.OfType<TurnWindowClosedEvent>().Single();
                Assert.That(closed.Reason, Is.EqualTo(TurnWindowCloseReason.BudgetExhausted));
                var plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
                Assert.That(plan.State, Is.EqualTo(ActionPlanState.Editable));
                Assert.That(plan.ReservedTurnBudgetTicks, Is.EqualTo(12));
                Assert.That(sim.CurrentSnapshot.WindowManager.Windows.Single().ReservedBudgetTicks, Is.EqualTo(12));
                while (sim.Tick < plan.EndTick) Step(sim);
                Assert.That(plan.State, Is.EqualTo(ActionPlanState.Completed));
                Assert.That(sim.WindowManager.FindWindow(new WindowId(1)).SpentBudgetTicks, Is.EqualTo(12));
            }
        }
        [Test] public void ExpiredOpportunityFreezesCompletePayloadOnlyAfterItsSourceCompletes()
        {
            using (var sim = Task09A2Fixture.NewSim())
            {
                var attack = Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);
                var opportunity = Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);
                while (sim.Tick < attack.ImpactTick) Step(sim);
                Assert.That(opportunity.State, Is.EqualTo(ReactionOpportunityState.Expired));
                Assert.That(sim.CurrentSnapshot.ReactionOpportunities.Count, Is.EqualTo(1), "Source recovery is still active.");
                Assert.That(sim.ReactionOpportunities.FindFrozenOpportunity(opportunity.Id), Is.Null);
                while (sim.Tick < attack.EndTick) Step(sim);
                AssertFrozenReaction(sim, opportunity, ReactionOpportunityState.Expired);
            }
        }

        [Test] public void TriggeredBlockRemainsMutableUntilSourceAndBoundPlanFinish()
            => CheckTriggeredReaction(ReactionCommandKind.Block, Task09A2Fixture.BlockSpecId, null);

        [Test] public void TriggeredDodgeRemainsMutableUntilSourceAndBoundPlanFinish()
            => CheckTriggeredReaction(ReactionCommandKind.Dodge, Task09A2Fixture.DodgeSpecId, new GridPoint(6, 2));

        private static void CheckTriggeredReaction(ReactionCommandKind kind, string spec, GridPoint? destination)
        {
            using (var sim = Task09A2Fixture.NewSim())
            {
                var attack = Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
                var opportunity = Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);
                Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                    Task09A2Fixture.Reaction(2, opportunity.Id, kind, Task09A2Fixture.Spec(spec), destination));
                var result = Task09A2Fixture.Step(sim, 2);
                Assert.That(Task09A2Fixture.AllRejectionCodes(result), Is.Empty);
                Assert.That(opportunity.State, Is.EqualTo(ReactionOpportunityState.Accepted));
                while (sim.Tick < opportunity.TriggerTick) Step(sim);
                Assert.That(opportunity.State, Is.EqualTo(ReactionOpportunityState.Triggered));
                Assert.That(sim.CurrentSnapshot.ReactionOpportunities.Count, Is.EqualTo(1));
                while (sim.Tick < attack.EndTick) Step(sim);
                AssertFrozenReaction(sim, opportunity, ReactionOpportunityState.Triggered);
                var history = sim.History;
                Assert.That(sim.ReactionOpportunities.ConfirmTrigger(opportunity.Id, sim.Tick), Is.Null);
                Assert.That(sim.History, Is.EqualTo(history), "Duplicate trigger must not append or consume again.");
            }
        }

        [Test] public void SourceCancellationArchivesOnlyAfterReservationReleaseAndKeepsClosedCommandAuthorization()
        {
            using (var sim = Task09A2Fixture.NewSim())
            {
                var attack = Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
                var opportunity = Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);
                Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                    Task09A2Fixture.Reaction(2, opportunity.Id, ReactionCommandKind.Block, Task09A2Fixture.Spec(Task09A2Fixture.BlockSpecId)));
                Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 2)), Is.Empty);
                sim.TerminalCoordinator.EnterTerminal(attack, ActionTerminationReason.CancelledByCommand, 3);
                Assert.That(opportunity.State, Is.EqualTo(ReactionOpportunityState.SourceCancelled));
                Assert.That(sim.ReactionOpportunities.PendingReservationReleases, Is.Empty);
                Step(sim);
                AssertFrozenReaction(sim, opportunity, ReactionOpportunityState.SourceCancelled);
                Assert.That(sim.ReactionCommandPlanner.TryGetDefenderUnitId(opportunity.Id, out var defender), Is.True);
                Assert.That(defender, Is.EqualTo(Task09A2Fixture.Monster));
                Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                    Task09A2Fixture.Reaction(sim.Tick + 1, opportunity.Id, ReactionCommandKind.Block, Task09A2Fixture.Spec(Task09A2Fixture.BlockSpecId)));
                var result = Task09A2Fixture.Step(sim, sim.Tick + 1);
                Assert.That(Task09A2Fixture.RejectionCodes(result), Is.EqualTo(new[] { ReactionOpportunityCodes.OPPORTUNITY_NOT_OPEN }));
            }
        }

        [Test] public void PendingReleasePreventsOpportunitySealingUntilTheReceiverConsumesIt()
        {
            using (var sim = Task09A2Fixture.NewSim())
            {
                var attack = Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
                var opportunity = Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);
                Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                    Task09A2Fixture.Reaction(2, opportunity.Id, ReactionCommandKind.Block, Task09A2Fixture.Spec(Task09A2Fixture.BlockSpecId)));
                Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 2)), Is.Empty);
                var adrenaline = sim.ReactionOpportunities.AdrenalinePort;
                sim.ReactionOpportunities.AdrenalinePort = null;
                sim.ReactionOpportunities.ReservationReleaseSink = null;
                sim.ReactionOpportunities.DestinationReservationPort = null;
                sim.TerminalCoordinator.EnterTerminal(attack, ActionTerminationReason.CancelledByCommand, 3);
                Assert.That(sim.ReactionOpportunities.PendingReservationReleases.Count, Is.EqualTo(1));
                Step(sim);
                Assert.That(sim.ReactionOpportunities.FindFrozenOpportunity(opportunity.Id), Is.Null);
                Assert.That(sim.CurrentSnapshot.ReactionOpportunities.Count, Is.EqualTo(1));
                sim.ReactionOpportunities.AdrenalinePort = adrenaline;
                Assert.That(sim.ReactionOpportunities.DispatchPendingReservationReleases(), Is.EqualTo(1));
                Step(sim);
                AssertFrozenReaction(sim, opportunity, ReactionOpportunityState.SourceCancelled);
            }
        }

        private static void AssertFrozenReaction(BattleSimulation sim, ReactionOpportunityRuntime original, ReactionOpportunityState state)
        {
            Assert.That(sim.CurrentSnapshot.ReactionOpportunities, Is.Empty);
            Assert.That(sim.ReactionOpportunities.ActiveOpportunities, Is.Empty);
            var snapshot = sim.ReactionOpportunities.FindFrozenOpportunity(original.Id);
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot.State, Is.EqualTo((int)state));
            Assert.That(snapshot.SourceAttackPlanId, Is.EqualTo(original.SourceAttackPlanId.Value));
            Assert.That(snapshot.BoundActionPlanId, Is.EqualTo(original.BoundActionPlanId.Value));
            Assert.That(snapshot.Options.Select(o => o.OutcomeCode), Is.EqualTo(original.Options.Select(o => o.OutcomeCode)));
            var records = sim.ReadHistoryForDiagnostics();
            Assert.That(records.Count(r => r.Kind == HistoryRecordKind.ReactionOpportunityBinding), Is.EqualTo(1));
            Assert.That(records.Single(r => r.Kind == HistoryRecordKind.ReactionOpportunityBinding).PayloadLength, Is.GreaterThan(0));
            Assert.That(sim.RecomputeHistoryDigestForDiagnostics().ToString("x16"), Is.EqualTo(sim.History.Digest));
            var prefix = sim.History;
            var counters = sim.HistoryAccessCounters;
            for (int i = 0; i < 100; i++) Step(sim);
            Assert.That(sim.ReactionOpportunities.EmittedEvents, Is.Empty, "The diagnostic event buffer must not accumulate old terminal/closure events.");
            Assert.That(sim.History, Is.EqualTo(prefix));
            var delta = sim.HistoryAccessCounters.DeltaFrom(counters);
            Assert.That(delta.OldRecordReads + delta.OldRecordCopies + delta.OldRecordHashes, Is.Zero);
            Assert.Throws<System.NotSupportedException>(() => ((System.Collections.Generic.IList<ProjectHero.Logic.Snapshots.ReactionOptionSnapshot>)snapshot.Options)[0] = null);
        }

        [Test] public void FileReplayRegeneratesFrozenClosedLedgersAndCompleteTerminalPayloadsOneHundredTimes()
        {
            var definition = Task09A2Fixture.BuildDefinition();
            BattleReplay replay;
            ProjectHero.Logic.Snapshots.HistorySummary history;
            using (var original = Create())
            {
                var recorder = new ReplayRecorder(original);
                for (int tick = 0; tick < 26; tick++)
                {
                    if (tick == 1)
                    {
                        var request = new CommandRequest(1, new ScheduleEditScope(0, new WindowId(1)),
                            new ScheduleEditPayload(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero,
                                Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), 10, PrimaryTargetUnitId: Task09A2Fixture.Monster) }));
                        Assert.That(original.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(request), Is.Null);
                    }
                    recorder.CaptureBeforeStep();
                    var result = original.Step(tick, original.CommandIngress.FreezeTick(tick));
                    recorder.RecordCommittedStep(result);
                }
                history = original.History;
                var archived = original.ReadHistoryForDiagnostics();
                Assert.That(archived.Count, Is.EqualTo(3));
                Assert.That(archived.Count(r => r.Kind == HistoryRecordKind.ReactionOpportunityBinding), Is.EqualTo(1));
                Assert.That(archived.All(r => r.PayloadLength > 0), Is.True);
                Assert.That(original.RecomputeHistoryDigestForDiagnostics().ToString("x16"), Is.EqualTo(history.Digest));
                replay = recorder.BuildReplay();
            }
            using (var file = new MemoryStream())
            {
                ReplayFile.Save(file, replay); file.Position = 0;
                var loaded = ReplayFile.Load(file, definition);
                using (var player = new ReplayPlayer(definition, header => BattleSimulation.Create(definition,
                    header.EncounterId, header.RuntimeInputs, new BattleSimulationAssembly(turnWindowSchedule: new Schedule()))))
                {
                    player.Load(loaded);
                    for (int run = 0; run < 100; run++)
                    {
                        if (run > 0) player.Restart();
                        player.Play();
                        while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                        Assert.That(player.Deviation, Is.Null, "run=" + run);
                        Assert.That(player.IsComplete, Is.True);
                        Assert.That(player.CurrentSnapshot.History, Is.EqualTo(history));
                    }
                }
            }
        }
        private sealed class Schedule : ITurnWindowSchedule
        {
            public WindowOpenRequest TryOpenDue(long tick) => tick == 0 ? new WindowOpenRequest(Task09A2Fixture.Hero, 400) : null;
            public bool ShouldCloseCurrentWindow(long tick) => tick == 1;
        }
        private static BattleSimulation Create() => BattleSimulation.Create(Task09A2Fixture.BuildDefinition(),
            Task09A2Fixture.EncounterId, Task09A2Fixture.Inputs, new BattleSimulationAssembly(turnWindowSchedule: new Schedule()));
        private static void Step(BattleSimulation sim) { long tick = sim.Tick + 1; sim.Step(tick, sim.CommandIngress.FreezeTick(tick)); }
        [Test] public void FrozenClosedWindowLeavesActiveSnapshotAndEmptyTicksNeverReadOldHistory()
        {
            using (var sim = Create())
            {
                Step(sim); Step(sim);
                Assert.That(sim.CurrentSnapshot.WindowManager.Windows, Is.Empty, "Frozen closed ledgers belong in history, not each Tick's active projection.");
                Assert.That(sim.History.RecordCount, Is.EqualTo(1));
                var before = sim.HistoryAccessCounters;
                var prefix = sim.History;
                for (int i = 0; i < 100; i++) Step(sim);
                Assert.That(sim.History, Is.EqualTo(prefix));
                Assert.That(sim.HistoryAccessCounters.OldRecordReads, Is.EqualTo(before.OldRecordReads));
                Assert.That(sim.HistoryAccessCounters.OldRecordCopies, Is.EqualTo(before.OldRecordCopies));
                Assert.That(sim.HistoryAccessCounters.OldRecordHashes, Is.EqualTo(before.OldRecordHashes));
                Assert.That(sim.RecomputeHistoryDigestForDiagnostics().ToString("x16"), Is.EqualTo(sim.History.Digest));
            }
        }
        [Test] public void ClosedWindowWithEditableReservationRemainsActiveUntilItsFinalConsumption()
        {
            using (var sim = Create())
            {
                Step(sim);
                var request = new CommandRequest(1, new ScheduleEditScope(0, new WindowId(1)),
                    new ScheduleEditPayload(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero,
                        Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), 10, PrimaryTargetUnitId: Task09A2Fixture.Monster) }));
                Assert.That(sim.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(request), Is.Null);
                Step(sim);
                var ledger = sim.CurrentSnapshot.WindowManager.Windows.Single();
                Assert.That(ledger.IsOpen, Is.False); Assert.That(ledger.ReservedBudgetTicks, Is.GreaterThan(0));
                Assert.That(sim.History.RecordCount, Is.Zero, "A closed ledger that may still consume or refund must not freeze.");
                while (sim.Tick < 10) Step(sim);
                Assert.That(sim.CurrentSnapshot.WindowManager.Windows, Is.Empty);
                Assert.That(sim.WindowManager.FindWindow(new WindowId(1)).SpentBudgetTicks, Is.GreaterThan(0));
                Assert.That(sim.History.RecordCount, Is.EqualTo(1));
            }
        }
        [Test] public void TerminalCleanupPendingBetweenTicksIsNotLostAtTheNextBeginTick()
        {
            using (var sim = Create())
            {
                Step(sim);
                var request = new CommandRequest(1, new ScheduleEditScope(0, new WindowId(1)),
                    new ScheduleEditPayload(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero,
                        Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), 10, PrimaryTargetUnitId: Task09A2Fixture.Monster) }));
                Assert.That(sim.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(request), Is.Null);
                Step(sim);
                var plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
                sim.TerminalCoordinator.EnterTerminal(plan, ProjectHero.Logic.Actions.ActionTerminationReason.CancelledByCommand, 2);
                Step(sim);
                var records = sim.ReadHistoryForDiagnostics();
                Assert.That(records.Count, Is.EqualTo(2), "Both the released closed ledger and the pending terminal payload must freeze.");
                var terminal = records.Single(r => r.Kind == ProjectHero.Logic.Determinism.HistoryRecordKind.ActionPlanTerminal);
                Assert.That(terminal.PayloadLength, Is.GreaterThan(0));
                Assert.That(terminal.ArchivedAtTick, Is.EqualTo(2));
                Assert.That(sim.ScheduleAuthority.Registry.FindFrozenTerminal(plan.ActionPlanId).TerminationReason,
                    Is.EqualTo((int)ProjectHero.Logic.Actions.ActionTerminationReason.CancelledByCommand));
                Step(sim);
                Assert.That(sim.History.RecordCount, Is.EqualTo(2), "Consumed candidates must not append twice.");
            }
        }
        [Test] public void EmptyInteractionFastPathKeepsAllEventsAndHashesAgainstFullReferencePipeline()
        {
            var definition = Task09A2Fixture.BuildDefinition();
            using (var fast = BattleSimulation.Create(definition, Task09A2Fixture.EncounterId, Task09A2Fixture.Inputs,
                new BattleSimulationAssembly(turnWindowSchedule: new Schedule(), useEmptyInteractionFastPath: true)))
            using (var reference = BattleSimulation.Create(definition, Task09A2Fixture.EncounterId, Task09A2Fixture.Inputs,
                new BattleSimulationAssembly(turnWindowSchedule: new Schedule(), useEmptyInteractionFastPath: false)))
            {
                for (int tick = 0; tick < 100; tick++)
                {
                    if (tick == 1)
                    {
                        var request = new CommandRequest(1, new ScheduleEditScope(0, new WindowId(1)),
                            new ScheduleEditPayload(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero,
                                Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), 10, PrimaryTargetUnitId: Task09A2Fixture.Monster) }));
                        Assert.That(fast.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(request), Is.Null);
                        Assert.That(reference.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(request), Is.Null);
                    }
                    var actual = fast.Step(tick, fast.CommandIngress.FreezeTick(tick));
                    var expected = reference.Step(tick, reference.CommandIngress.FreezeTick(tick));
                    Assert.That(actual.Snapshot.ComputeHash(), Is.EqualTo(expected.Snapshot.ComputeHash()), "tick=" + tick);
                    Assert.That(actual.Events.Events.Select(ProjectHero.Logic.Replay.ReplayEventComparison.Canonical),
                        Is.EqualTo(expected.Events.Events.Select(ProjectHero.Logic.Replay.ReplayEventComparison.Canonical)), "tick=" + tick);
                    Assert.That(fast.LastStepPhaseTrace.Select(p => p.Phase), Is.EqualTo(reference.LastStepPhaseTrace.Select(p => p.Phase)));
                }
            }
        }
        [Test] public void FutureGuardUsesEmptyFastPathUntilStartAndStillMatchesFullPipeline()
        {
            var definition = Task09A2Fixture.BuildDefinition();
            var guardId = new ActionSpecId("action.fixture_guard");
            var guard = new ActionSpec(guardId, ActionType.Guard, new GuardTimingSpec(2, 2, 2),
                new GuardPayloadSpec(new System.Collections.Generic.Dictionary<DamageChannelId, int> {
                    [DamageChannels.PhysicalBlunt] = 512 }, 512, DefenseTagMask.Blockable | DefenseTagMask.Guardable), 0);
            definition = definition with { Actions = definition.Actions.Concat(new[] { guard }).ToArray(),
                ActionSets = definition.ActionSets.Select(set => set with { ActionSpecIds = set.ActionSpecIds.Concat(new[] { guardId }).ToArray() }).ToArray() };
            using (var fast = BattleSimulation.Create(definition, Task09A2Fixture.EncounterId, Task09A2Fixture.Inputs,
                new BattleSimulationAssembly(turnWindowSchedule: new Schedule(), useEmptyInteractionFastPath: true)))
            using (var reference = BattleSimulation.Create(definition, Task09A2Fixture.EncounterId, Task09A2Fixture.Inputs,
                new BattleSimulationAssembly(turnWindowSchedule: new Schedule(), useEmptyInteractionFastPath: false)))
            {
                for (int tick = 0; tick < 40; tick++)
                {
                    if (tick == 1)
                    {
                        var request = new CommandRequest(1, new ScheduleEditScope(0, new WindowId(1)),
                            new ScheduleEditPayload(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero, guardId, 20) }));
                        Assert.That(fast.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(request), Is.Null);
                        Assert.That(reference.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(request), Is.Null);
                    }
                    var actual = fast.Step(tick, fast.CommandIngress.FreezeTick(tick));
                    var expected = reference.Step(tick, reference.CommandIngress.FreezeTick(tick));
                    Assert.That(actual.Snapshot.ComputeHash(), Is.EqualTo(expected.Snapshot.ComputeHash()), "tick=" + tick);
                    Assert.That(actual.Events.Events.Select(ProjectHero.Logic.Replay.ReplayEventComparison.Canonical),
                        Is.EqualTo(expected.Events.Events.Select(ProjectHero.Logic.Replay.ReplayEventComparison.Canonical)), "tick=" + tick);
                    if (tick == 10) Assert.That(actual.Snapshot.Plans.Single().State, Is.EqualTo((int)ActionPlanState.Editable));
                    if (tick == 20) Assert.That(actual.Snapshot.Plans.Single().State, Is.EqualTo((int)ActionPlanState.Running));
                    Assert.That(fast.LastStepPhaseTrace.Select(p => p.Phase), Is.EqualTo(reference.LastStepPhaseTrace.Select(p => p.Phase)));
                }
            }
        }
    }
}
