using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Units;
using ProjectHero.Logic.AI;
using System.Collections.Generic;

namespace ProjectHero.Logic.Tests
{
    public sealed class Task12AWindowDurationTests
    {
        private static BattleSimulation Open(int duration = 20)
        {
            var sim = Task09A2Fixture.NewSim();
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, duration);
            Task09A2Fixture.Step(sim, 0);
            return sim;
        }
        private static ScheduleEditTransactionResult Add(BattleSimulation sim, long start, bool preview = false, WindowId? expected = null)
            => sim.ScheduleEditor.Apply(new ScheduleEditOperation[] {
                new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero,
                    Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), start,
                    PrimaryTargetUnitId: Task09A2Fixture.Monster) }, sim.Tick + 1,
                sim.ScheduleAuthority.ScheduleRevision, sim.ScheduleAuthority.ScheduleRevision, preview: preview,
                expectedWindowId: expected ?? sim.CurrentTurnWindow?.WindowId, issuer: Task09A2Fixture.PlayerId);
        private static ScheduleEditTransactionResult Defer(BattleSimulation sim, ActionPlan plan, long retry, long tick)
            => (ScheduleEditTransactionResult)typeof(ScheduleEditor).GetMethod("ApplySystemAutoDeferral",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(sim.ScheduleEditor, new object[] { plan, retry, tick, Array.Empty<ActionPlanId>() });

        [Test] public void WindowEndTickEqualsOpenedAtTickPlusDuration()
        {
            var window = new TurnWindow(new WindowId(1), Task09A2Fixture.Hero, 13, 180);
            Assert.That(window.EndTick, Is.EqualTo(193));
            Assert.That(window.Contains(13) && window.Contains(192), Is.True);
            Assert.That(window.Contains(12) || window.Contains(193), Is.False);
        }
        [Test] public void WindowClosesAtEndOfLastTickAndNextOpensAtEndTick()
        {
            using (var sim = Open(3))
            {
                sim.WindowManager.ScheduleWindow(3, Task09A2Fixture.Monster, 3);
                Task09A2Fixture.Step(sim, 1);
                Assert.That(sim.CurrentTurnWindow.IsOpen, Is.True);
                var result = Task09A2Fixture.Step(sim, 2);
                Assert.That(result.Events.Events.OfType<TurnWindowClosedEvent>().Single().Reason, Is.EqualTo(TurnWindowCloseReason.Expired));
                Assert.That(sim.CurrentTurnWindow, Is.Null);
                Task09A2Fixture.Step(sim, 3);
                Assert.That(sim.CurrentTurnWindow.OpenedAtTick, Is.EqualTo(3));
            }
        }
        [TestCase(true)] [TestCase(false)]
        public void StartTickAtEndTickIsRejectedWithStableCode(bool preview)
        {
            using (var sim = Open())
            {
                string before = Task09A2Fixture.ScheduleFingerprint(sim);
                Assert.That(Add(sim, 20, preview).RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_START_OUTSIDE_SOURCE_WINDOW));
                Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(before));
                Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.Zero);
            }
        }
        [Test] public void StartTickAtEndTickMinusOneIsAccepted()
        {
            using (var sim = Open()) Assert.That(Add(sim, 19).Committed, Is.True);
        }
        [Test] public void UnlimitedActionsWithinWindowAreAcceptedWithoutCost()
        {
            using var sim = Open();
            Assert.That(Add(sim, 1).Committed, Is.True);
            Assert.That(Add(sim, 13).Committed, Is.True);
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Sum(p => p.EndTick - p.StartTick), Is.GreaterThan(20));
            Assert.That(sim.CurrentTurnWindow.IsAcceptingSubmissions, Is.True);
        }
        [Test] public void ConsecutiveWindowsTileWithoutGapOrOverlap()
        {
            using var sim = Task09A2Fixture.NewSim();
            for (int i = 0; i < 5; i++) sim.WindowManager.ScheduleWindow(3 * i,
                i % 2 == 0 ? Task09A2Fixture.Hero : Task09A2Fixture.Monster, 3);
            var opened = new List<long>(); var closed = new List<long>();
            for (int tick = 0; tick < 15; tick++)
            {
                var result = Task09A2Fixture.Step(sim, tick);
                opened.AddRange(result.Events.Events.OfType<TurnWindowOpenedEvent>().Select(e => e.Tick));
                closed.AddRange(result.Events.Events.OfType<TurnWindowClosedEvent>().Select(e => e.Tick));
            }
            Assert.That(opened, Is.EqualTo(new long[] { 0, 3, 6, 9, 12 }));
            Assert.That(closed, Is.EqualTo(new long[] { 2, 5, 8, 11, 14 }));
            for (int i = 1; i < opened.Count; i++) Assert.That(opened[i], Is.EqualTo(closed[i - 1] + 1));
        }
        [Test] public void LongActionStartingInLastTickExtendsBeyondWindowAndContinues()
        {
            using (var sim = Open())
            {
                Assert.That(Add(sim, 19).Committed, Is.True);
                var plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
                while (sim.Tick < 19) Task09A2Fixture.Step(sim, sim.Tick + 1);
                Assert.That(sim.CurrentTurnWindow, Is.Null);
                Assert.That(plan.IsRunning, Is.True);
                Assert.That(plan.EndTick, Is.GreaterThan(20));
                while (sim.Tick < plan.EndTick) Task09A2Fixture.Step(sim, sim.Tick + 1);
                Assert.That(plan.State, Is.EqualTo(ActionPlanState.Completed));
            }
        }
        [Test] public void AddWhoseLaneTailIsBeyondWindowEndIsRejected()
        {
            using (var sim = Open())
            {
                Assert.That(Add(sim, 19).Committed, Is.True);
                var tail = sim.ScheduleAuthority.Registry.ActivePlans.Single().EndTick;
                Assert.That(Add(sim, tail).RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_START_OUTSIDE_SOURCE_WINDOW));
                Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.EqualTo(1));
            }
        }
        [TestCase(true)] [TestCase(false)]
        public void WindowValidationSurvivesBudgetRemovalForPreviewAndCommit(bool preview)
        {
            using (var sim = Open())
            {
                Assert.That(Add(sim, 1, preview, new WindowId(999)).RejectionCode, Is.EqualTo(TurnWindowCodes.STALE_OR_CLOSED_WINDOW));
                Assert.That(sim.ScheduleAuthority.Registry.ActivePlans, Is.Empty);
            }
        }
        [Test] public void EditMovingStartTickOutsideSourceWindowIsRejected()
        {
            using (var sim = Open())
            {
                Add(sim, 5);
                var plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
                foreach (bool preview in new[] { true, false })
                {
                    var result = sim.ScheduleEditor.Apply(new ScheduleEditOperation[] {
                        new MoveEditablePlanOperation(plan.ActionPlanId, 20) }, 1,
                        sim.ScheduleAuthority.ScheduleRevision, sim.ScheduleAuthority.ScheduleRevision, preview: preview, issuer: Task09A2Fixture.PlayerId);
                    Assert.That(result.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_START_OUTSIDE_SOURCE_WINDOW));
                    Assert.That(plan.StartTick, Is.EqualTo(5));
                    Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(1));
                }
            }
        }
        [Test] public void AutoDeferralWithinSourceWindowStillDefers()
        {
            using (var sim = Open())
            {
                Add(sim, 1); var plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
                var result = Defer(sim, plan, 4, 1);
                Assert.That(result.Committed, Is.True);
                Assert.That(plan.StartTick, Is.EqualTo(4));
                Assert.That(plan.AutomaticDeferralCount, Is.EqualTo(1));
                Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(2));
            }
        }
        [Test] public void RippleDeferralBeyondWindowCommitsNoPartialCandidate()
        {
            using (var sim = Open(20))
            {
                Add(sim, 1); Add(sim, 13);
                var plans = sim.ScheduleAuthority.Registry.ActivePlans.OrderBy(p => p.StartTick).ToArray();
                string before = Task09A2Fixture.ScheduleFingerprint(sim);
                var result = Defer(sim, plans[0], 9, 1);
                Assert.That(result.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_START_OUTSIDE_SOURCE_WINDOW));
                Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(before));
                Assert.That(result.OutOfWindowPlanIds, Is.EqualTo(new[] { plans[1].ActionPlanId }));
            }
        }
        [Test] public void WindowCannotBeClosedEarlyByPlayerOrAi()
        {
            using (var sim = Open())
            {
                foreach (var issuer in new[] { Task09A2Fixture.PlayerId, Task09A2Fixture.AiId })
                {
                    var result = sim.CommandIngress.FindEntry(issuer).Submit(new CommandRequest(1,
                        new WindowCommandScope(sim.CurrentTurnWindow.WindowId), new WindowCommandPayload((WindowCommandKind)0)));
                    Assert.That(result, Is.Null);
                }
                Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1)),
                    Is.EqualTo(new[] { TurnWindowCodes.WINDOW_CLOSE_NOT_PERMITTED, TurnWindowCodes.WINDOW_CLOSE_NOT_PERMITTED }));
                Assert.That(sim.CurrentTurnWindow.IsAcceptingSubmissions, Is.True);
                Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() => sim.CurrentTurnWindow.RequestClose(TurnWindowCloseReason.OwnerRequested));
                Assert.That((int)WindowCommandKind.ActivateConcurrentAction, Is.EqualTo(1));
            }
        }
        [Test] public void ReplayFormatV4IsRejected()
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                { writer.Write("ProjectHero.DeveloperReplay.v4"); writer.Write(4); }
                stream.Position = 0;
                var ex = Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() => ReplayFile.Load(stream, Task09A2Fixture.BuildDefinition()));
                Assert.That(ex.ErrorCode, Is.EqualTo(ReplayCodes.REPLAY_FORMAT_VERSION_MISMATCH));
            }
        }
        private sealed class StateAtOne : IUnitStateAdvanceSystem
        {
            public bool Kill;
            public int Duration = 20;
            public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(IReadOnlyList<UnitSnapshot> units, long tick)
                => tick == 1 ? new[] { Kill ? new UnitStateAdvanceRequest(1, 0)
                    : new UnitStateAdvanceRequest(1, null, UnitStateMachine.StaggerFor(Duration)) }
                    : Array.Empty<UnitStateAdvanceRequest>();
        }
        [Test] public void AutoDeferralBeyondSourceWindowTerminatesWithDelayedBeyondSourceWindow()
        {
            using var sim = Task09A2Fixture.NewSim(assembly: new BattleSimulationAssembly(unitStateAdvance: new StateAtOne()));
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 20);
            Task09A2Fixture.Step(sim, 0); Assert.That(Add(sim, 1).Committed, Is.True);
            var plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Task09A2Fixture.Step(sim, 1);
            Assert.That(plan.TerminationReason, Is.EqualTo(ActionTerminationReason.DelayedBeyondSourceWindow));
            Assert.That(plan.AutomaticDeferralCount, Is.Zero);
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(1));
            Assert.That(sim.ScheduleAuthority.LaneOfPlan(plan.ActionPlanId), Is.Null);
        }
        [Test] public void OwnerDeathClosesWindowEarlyAndNextOpensNextTick()
        {
            using var sim = Task09A2Fixture.NewSim(assembly: new BattleSimulationAssembly(unitStateAdvance: new StateAtOne { Kill = true }));
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 20);
            sim.WindowManager.ScheduleWindow(2, Task09A2Fixture.Monster, 20);
            Task09A2Fixture.Step(sim, 0);
            Assert.That(Add(sim, 5).Committed, Is.True);
            var result = Task09A2Fixture.Step(sim, 1);
            Assert.That(result.Events.Events.OfType<TurnWindowClosedEvent>().Single().Reason, Is.EqualTo(TurnWindowCloseReason.OwnerDied));
            Assert.That(sim.CurrentTurnWindow, Is.Null);
            Task09A2Fixture.Step(sim, 2);
            Assert.That(sim.CurrentTurnWindow.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Monster));
            Assert.That(sim.CurrentTurnWindow.OpenedAtTick, Is.EqualTo(2));
        }
        [Test] public void RippleOverflowTerminatesOffendingPlanAndRetriesDuePlanAtomically()
        {
            using var sim = Task09A2Fixture.NewSim(assembly: new BattleSimulationAssembly(unitStateAdvance: new StateAtOne { Duration = 8 }));
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 20); Task09A2Fixture.Step(sim, 0);
            Assert.That(Add(sim, 1).Committed, Is.True); Assert.That(Add(sim, 13).Committed, Is.True);
            var plans = sim.ScheduleAuthority.Registry.ActivePlans.OrderBy(p => p.StartTick).ToArray();
            Task09A2Fixture.Step(sim, 1);
            Assert.That(plans[1].TerminationReason, Is.EqualTo(ActionTerminationReason.DelayedBeyondSourceWindow));
            Assert.That(plans[1].StartTick, Is.EqualTo(13), "The rejected candidate cannot rebind its offending projection.");
            Assert.That(plans[0].IsEditable, Is.True); Assert.That(plans[0].StartTick, Is.EqualTo(9));
            Assert.That(plans[0].AutomaticDeferralCount, Is.EqualTo(1));
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(3));
        }
        [Test] public void MoveChainRecomputePushingStartBeyondWindowRejectsWholeTransaction()
        {
            using var sim = Task09A2Fixture.NewSim(definition: Task08DefenseCoverageTests.Definition());
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 35); Task09A2Fixture.Step(sim, 0);
            ScheduleEditTransactionResult MoveAdd(long key, long start, int x)
                => sim.ScheduleEditor.Apply(new ScheduleEditOperation[] {
                    new AddOrdinaryPlanOperation(key, Task09A2Fixture.Hero, Task08DefenseCoverageTests.MoveId, start,
                        Destination: new ProjectHero.Logic.Grid.GridPoint(x, 0)) }, 1, sim.ScheduleAuthority.ScheduleRevision,
                    sim.ScheduleAuthority.ScheduleRevision, expectedWindowId: sim.CurrentTurnWindow.WindowId, issuer: Task09A2Fixture.PlayerId);
            Assert.That(MoveAdd(1, 1, -2).Committed, Is.True);
            var first = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(MoveAdd(2, first.EndTick, -4).Committed, Is.True);
            var second = sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.ActionPlanId != first.ActionPlanId);
            string before = Task09A2Fixture.ScheduleFingerprint(sim);
            var reservations = sim.LogicGrid.AllReservationsOrdered().Select(r => r.ToString()).ToArray();
            foreach (bool preview in new[] { true, false })
            {
                var result = sim.ScheduleEditor.Apply(new ScheduleEditOperation[] {
                    new MoveEditablePlanOperation(first.ActionPlanId, 20) },
                    2, sim.ScheduleAuthority.ScheduleRevision, sim.ScheduleAuthority.ScheduleRevision, preview: preview, issuer: Task09A2Fixture.PlayerId);
                Assert.That(result.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_START_OUTSIDE_SOURCE_WINDOW),
                    "sourceEnd=" + sim.CurrentTurnWindow.EndTick + " durations=" + first.MoveDurationTicks + "," + second.MoveDurationTicks
                    + " projections=" + string.Join(",", result.Evaluations.Select(e => e.PlanId + ":" + e.NewStartTick)));
                Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(before));
                Assert.That(sim.LogicGrid.AllReservationsOrdered().Select(r => r.ToString()), Is.EqualTo(reservations));
            }
        }
        [Test] public void ClosedWindowCommandKindZeroIsRejectedAndNotReused()
        {
            Assert.That((int)WindowCommandKind.ActivateConcurrentAction, Is.EqualTo(1));
            Assert.That(Enum.IsDefined(typeof(WindowCommandKind), 0), Is.False);
            var copy = typeof(ReplayFile).GetMethod("CopyRequest", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => copy.Invoke(null, new object[] {
                new CommandRequest(1, new WindowCommandScope(new WindowId(1)), new WindowCommandPayload((WindowCommandKind)0)) }));
            Assert.That(((ProjectHero.Logic.LogicDefinitionException)exception.InnerException).ErrorCode,
                Is.EqualTo(TurnWindowCodes.WINDOW_CLOSE_NOT_PERMITTED));
        }
        [Test] public void ConcurrentHeroPlanIsBoundToOwnersWindowRange()
        {
            using var sim = Task09A2Fixture.NewSimWithMeta(1, assembly: new BattleSimulationAssembly(concurrentHeroUnitId: Task09A2Fixture.Hero));
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Monster, 20);
            Task09A2Fixture.Step(sim, 0);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, new CommandRequest(1,
                new WindowCommandScope(sim.CurrentTurnWindow.WindowId), new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction)));
            Task09A2Fixture.Step(sim, 1);
            Assert.That(Add(sim, 19).Committed, Is.True);
            var plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(plan.SubmittedWindowId, Is.EqualTo(sim.CurrentTurnWindow.WindowId));
            Assert.That(Add(sim, 20, true).RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_START_OUTSIDE_SOURCE_WINDOW));
        }
        [Test] public void NoEditableOrdinaryPlanSurvivesItsSourceWindowClose()
        {
            using var sim = Open(); Assert.That(Add(sim, 5).Committed, Is.True);
            var plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            typeof(ActionPlan).GetMethod("RebindAbsoluteTicks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(plan, new object[] { 21L });
            while (sim.Tick < 18) Task09A2Fixture.Step(sim, sim.Tick + 1);
            var error = Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() => Task09A2Fixture.Step(sim, 19));
            Assert.That(error.ErrorCode, Is.EqualTo(TurnWindowCodes.WINDOW_INVARIANT));
            Assert.That(plan.IsEditable, Is.True, "Closure must check, never silently cancel the offending plan.");
        }
        [Test] public void SnapshotHashAndEventsContainNoBudgetState()
        {
            foreach (var type in new[] { typeof(TurnWindowSnapshot), typeof(BattleResourceSnapshot), typeof(ActionPlanSnapshot), typeof(TurnWindow) })
                Assert.That(type.GetProperties().Where(p => p.Name.IndexOf("budget", StringComparison.OrdinalIgnoreCase) >= 0), Is.Empty, type.Name);
            Assert.That(typeof(LogicEvent).Assembly.GetTypes().Where(t => typeof(LogicEvent).IsAssignableFrom(t)
                && t.Name.IndexOf("budget", StringComparison.OrdinalIgnoreCase) >= 0), Is.Empty);
            using var sim = Open(); using var longer = Open(21);
            Assert.That(longer.CurrentSnapshot.ComputeHash(), Is.Not.EqualTo(sim.CurrentSnapshot.ComputeHash()));
        }
        [Test] public void AiNeverProposesStartBeyondWindowEnd()
        {
            using var sim = Task09A2Fixture.NewSim();
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Monster, 20); Task09A2Fixture.Step(sim, 0);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.AddPlan(1, 0, sim.CurrentTurnWindow.WindowId,
                Task09A2Fixture.Monster, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Hero, 19));
            Task09A2Fixture.Step(sim, 1);
            var ai = new AiControllerLogic(31); var context = ai.BuildContext(sim.DecisionSnapshotFor(Task09A2Fixture.AiId), Task09A2Fixture.AiId);
            Assert.That(AiCandidateBuilder.BuildNormalCandidatesOrdered(context).Any(c => c.Kind == AiCandidateKind.Add), Is.False);
        }
        [Test] public void StartCommitRemainsAtomicWithoutBudget()
        {
            using var sim = Open(); Assert.That(Add(sim, 1).Committed, Is.True);
            var plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            string before = Task09A2Fixture.ScheduleFingerprint(sim);
            var commit = typeof(BattleSimulation).GetMethod("CommitStart", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.Throws<System.Reflection.TargetInvocationException>(() => commit.Invoke(sim, new object[] { plan, 2L }));
            Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(before));
            Assert.That(plan.IsEditable, Is.True); Assert.That(plan.LockedAtTick, Is.EqualTo(-1));
            var result = Task09A2Fixture.Step(sim, 1);
            Assert.That(plan.IsRunning, Is.True); Assert.That(plan.LockedAtTick, Is.EqualTo(1));
            Assert.That(result.Events.Events.OfType<ActionPlanLockedEvent>().Count(), Is.EqualTo(1));
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(1));
        }
        [Test] public void WindowSwitchDoesNotMutatePlanLaneIntentOrReservation()
        {
            using var sim = Task09A2Fixture.NewSim(definition: Task08DefenseCoverageTests.Definition());
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 2); Task09A2Fixture.Step(sim, 0);
            Assert.That(sim.ScheduleEditor.Apply(new ScheduleEditOperation[] {
                new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero, Task08DefenseCoverageTests.MoveId, 1,
                    Destination: new ProjectHero.Logic.Grid.GridPoint(-2, 0)) }, 1, 0, 0,
                expectedWindowId: sim.CurrentTurnWindow.WindowId, issuer: Task09A2Fixture.PlayerId).Committed, Is.True);
            Task09A2Fixture.Step(sim, 1);
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Single().IsRunning, Is.True);
            string plans = Task09A2Fixture.ScheduleFingerprint(sim);
            var reservations = MovementSnapshotProjection.Reservations(sim.LogicGrid.AllReservationsOrdered()).ToArray();
            Assert.That(reservations, Is.Not.Empty);
            var intents = sim.CurrentSnapshot.Intents;
            sim.WindowManager.ScheduleWindow(2, Task09A2Fixture.Monster, 2);
            Assert.That(sim.WindowManager.TryOpenDueWindow(2), Is.Not.Null);
            Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(plans));
            Assert.That(MovementSnapshotProjection.Reservations(sim.LogicGrid.AllReservationsOrdered()), Is.EqualTo(reservations));
            Assert.That(sim.CurrentSnapshot.Intents, Is.SameAs(intents));
        }
    }
}
