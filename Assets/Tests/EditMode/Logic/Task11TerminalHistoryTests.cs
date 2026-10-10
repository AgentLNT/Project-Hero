using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Combat;

namespace ProjectHero.Logic.Tests
{
    public sealed class Task11TerminalHistoryTests
    {
        [Test] public void SpaceProjectionReusePreservesBeforeMovementAndMatchesFreshSourcesAfterCommit()
        {
            using (var sim = Task09A2Fixture.NewSim())
            {
                sim.LogicGrid.TryGetAnchor(Task09A2Fixture.Hero, out var origin);
                var id = new ActionPlanId(999); var path = new[] { origin, origin.Translate(-2, 0), origin.Translate(-4, 0), origin.Translate(-6, 0) };
                Assert.That(sim.MovementAuthority.EstablishMovement(id, Task09A2Fixture.Hero, path, 10, 2).Succeeded, Is.True);
                var before = sim.DodgeRelocation.CaptureSpaceSnapshot(0);
                Assert.That(sim.DodgeRelocation.CaptureSpaceSnapshot(1).Units, Is.SameAs(before.Units));
                var old = sim.Step(0, sim.CommandIngress.FreezeTick(0)).Snapshot;
                var unchanged = sim.Step(1, sim.CommandIngress.FreezeTick(1)).Snapshot;
                Assert.That(unchanged.MovementSegments[0], Is.SameAs(old.MovementSegments[0]));
                Assert.That(unchanged.Units.Single(u => u.UnitId == Task09A2Fixture.Hero.Value), Is.SameAs(old.Units.Single(u => u.UnitId == Task09A2Fixture.Hero.Value)));
                for (int tick = 2; tick <= 12; tick++) sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                var after = sim.DodgeRelocation.CaptureSpaceSnapshot(12);
                before.TryGetAnchor(Task09A2Fixture.Hero, out var oldAnchor); after.TryGetAnchor(Task09A2Fixture.Hero, out var newAnchor);
                Assert.That(oldAnchor, Is.EqualTo(origin)); Assert.That(newAnchor, Is.EqualTo(path[1]));
                Assert.That(after.Units, Is.Not.SameAs(before.Units));
                Assert.That(old.MovementSegments.Count, Is.EqualTo(3)); Assert.That(old.Reservations.Count, Is.EqualTo(3));
                Assert.That(old.Units.Single(u => u.UnitId == Task09A2Fixture.Hero.Value).X, Is.EqualTo(origin.X));
                Assert.That(sim.CurrentSnapshot.Units.Single(u => u.UnitId == Task09A2Fixture.Hero.Value).X, Is.EqualTo(path[1].X));
                Assert.That(sim.CurrentSnapshot.MovementSegments, Is.EqualTo(MovementSnapshotProjection.Segments(sim.MovementAuthority.AllSegmentsOrdered())));
                Assert.That(sim.CurrentSnapshot.Reservations, Is.EqualTo(MovementSnapshotProjection.Reservations(sim.LogicGrid.AllReservationsOrdered())));
                Assert.That(sim.MovementAuthority.VerifyInvariants(), Is.Null);
            }
        }
        [Test] public void WindowProjectionReusesUnchangedRowsAndKeepsOldCloseAndRefundFactsImmutable()
        {
            using (var sim = BattleSimulation.Create(Task09A2Fixture.BuildDefinition(), Task09A2Fixture.EncounterId,
                Task09A2Fixture.Inputs, new BattleSimulationAssembly()))
            {
                sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 400); sim.WindowManager.TryOpenDueWindow(0);
                var added = sim.ScheduleEditor.Apply(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1,
                    Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), 100,
                    PrimaryTargetUnitId: Task09A2Fixture.Monster) }, 1, 0, 0, expectedWindowId: new WindowId(1), issuer: Task09A2Fixture.PlayerId);
                Assert.That(added.Succeeded, Is.True);
                var open = sim.Step(0, sim.CommandIngress.FreezeTick(0)).Snapshot.WindowManager.Windows.Single();
                Assert.That(sim.Step(1, sim.CommandIngress.FreezeTick(1)).Snapshot.WindowManager.Windows.Single(), Is.SameAs(open));
                sim.CurrentTurnWindow.RequestClose(ProjectHero.Logic.Turns.TurnWindowCloseReason.OwnerRequested);
                var closed = sim.Step(2, sim.CommandIngress.FreezeTick(2)).Snapshot.WindowManager.Windows.Single();
                Assert.That(closed.IsOpen, Is.False); Assert.That(closed.ClosedAtTick, Is.EqualTo(2));
                Assert.That(open.IsOpen, Is.True); Assert.That(open.ClosedAtTick, Is.EqualTo(-1));
                Assert.That(sim.Step(3, sim.CommandIngress.FreezeTick(3)).Snapshot.WindowManager.Windows.Single(), Is.SameAs(closed));
                var plan = sim.ScheduleAuthority.Registry.Find(added.AddedPlanIds.Single());
                sim.TerminalCoordinator.EnterTerminal(plan, ActionTerminationReason.CancelledByCommand, 4);
                Assert.That(sim.Step(4, sim.CommandIngress.FreezeTick(4)).Snapshot.WindowManager.Windows, Is.Empty);
                Assert.That(closed.ReservedBudgetTicks, Is.GreaterThan(0)); Assert.That(closed.Reservations.Count, Is.EqualTo(1));
                Assert.That(sim.WindowManager.FindWindow(new WindowId(1)).ReservedBudgetTicks, Is.Zero);
            }
        }

        [Test] public void MovementInvariantRejectsInternalGapBeforeCountMismatchButAllowsCommittedPrefix()
        {
            using (var sim = Task09A2Fixture.NewSim())
            {
                Assert.That(sim.LogicGrid.TryGetAnchor(Task09A2Fixture.Hero, out var origin), Is.True);
                var id = new ActionPlanId(999);
                var path = new[] { origin, origin.Translate(-2, 0), origin.Translate(-4, 0), origin.Translate(-6, 0) };
                Assert.That(sim.MovementAuthority.EstablishMovement(id, Task09A2Fixture.Hero, path, 0, 2).Succeeded, Is.True);
                sim.MovementAuthority.ApplyPreCommandBoundary(2);
                Assert.That(sim.MovementAuthority.VerifyInvariants(), Is.Null, "Committed Step 0 is an allowed missing prefix.");
                var segments = (System.Collections.Generic.Dictionary<ReservationKey, MovementSegment>)typeof(LogicGridMovementAuthority)
                    .GetField("_segments", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(sim.MovementAuthority);
                // Add a new three-step chain so a middle gap and an extra reservation coexist.
                sim.MovementAuthority.ReleasePlanMovement(id);
                sim.LogicGrid.TryGetAnchor(Task09A2Fixture.Hero, out origin);
                path = new[] { origin, origin.Translate(-2, 0), origin.Translate(-4, 0), origin.Translate(-6, 0) };
                Assert.That(sim.MovementAuthority.EstablishMovement(id, Task09A2Fixture.Hero, path, 10, 2).Succeeded, Is.True);
                segments.Remove(new ReservationKey(id, 1));
                Assert.That(sim.MovementAuthority.VerifyInvariants(), Does.StartWith(MovementCodes.MOVEMENT_SEGMENT_CHAIN_DISCONTINUOUS));
            }
        }
        [Test] public void ActivePlanProjectionReuseChecksEveryFrozenField()
        {
            using (var sim = BattleSimulation.Create(Task09A2Fixture.BuildDefinition(), Task09A2Fixture.EncounterId,
                Task09A2Fixture.Inputs, new BattleSimulationAssembly()))
            {
                sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 400); sim.WindowManager.TryOpenDueWindow(0);
                var added = sim.ScheduleEditor.Apply(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1,
                    Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), 100,
                    PrimaryTargetUnitId: Task09A2Fixture.Monster) }, 1, 0, 0, expectedWindowId: new WindowId(1), issuer: Task09A2Fixture.PlayerId);
                Assert.That(added.Succeeded, Is.True);
                var plan = sim.ScheduleAuthority.Registry.Find(added.AddedPlanIds.Single());
                var match = typeof(ActionPlanSnapshot).GetMethod("MatchesAuthoritative", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var properties = typeof(ActionPlanSnapshot).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                Assert.That(properties.Length, Is.EqualTo(38), "A new canonical field requires a cache invalidation check.");
                foreach (var property in properties)
                {
                    var independentCopy = ActionPlanSnapshot.From(plan, sim.FactionResolver);
                    Assert.That(match.Invoke(independentCopy, new object[] { plan, sim.FactionResolver }), Is.EqualTo(true));
                    object old = property.GetValue(independentCopy);
                    object changed = property.PropertyType == typeof(long) ? (object)((long)old + 1)
                        : property.PropertyType == typeof(int) ? (object)((int)old + 1)
                        : property.PropertyType == typeof(bool) ? !(bool)old : (string)old + "-probe";
                    // Alter a separately constructed test copy, never the actual cached world snapshot.
                    property.SetValue(independentCopy, changed);
                    Assert.That(match.Invoke(independentCopy, new object[] { plan, sim.FactionResolver }), Is.EqualTo(false), property.Name);
                }
            }
        }

        [Test] public void ActiveProjectionReusesImmutableRowsAndDropsTerminalCacheEntry()
        {
            using (var sim = BattleSimulation.Create(Task09A2Fixture.BuildDefinition(), Task09A2Fixture.EncounterId,
                Task09A2Fixture.Inputs, new BattleSimulationAssembly()))
            {
                sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 400); sim.WindowManager.TryOpenDueWindow(0);
                var added = sim.ScheduleEditor.Apply(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1,
                    Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), 100,
                    PrimaryTargetUnitId: Task09A2Fixture.Monster) }, 1, 0, 0, expectedWindowId: new WindowId(1), issuer: Task09A2Fixture.PlayerId);
                Assert.That(added.Succeeded, Is.True);
                var first = sim.Step(0, sim.CommandIngress.FreezeTick(0)).Snapshot.Plans.Single();
                var second = sim.Step(1, sim.CommandIngress.FreezeTick(1)).Snapshot.Plans.Single();
                Assert.That(second, Is.SameAs(first));
                var plan = sim.ScheduleAuthority.Registry.Find(added.AddedPlanIds.Single());
                sim.TerminalCoordinator.EnterTerminal(plan, ActionTerminationReason.CancelledByCommand, 2);
                Assert.That(sim.Step(2, sim.CommandIngress.FreezeTick(2)).Snapshot.Plans, Is.Empty);
                Assert.That(first.State, Is.EqualTo((int)ActionPlanState.Editable));
                Assert.That(first.TerminalTick, Is.EqualTo(-1));
                var cache = (System.Collections.IDictionary)typeof(ActionPlanRegistry).GetField("_activeProjection",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(sim.ScheduleAuthority.Registry);
                Assert.That(cache.Count, Is.Zero, "Active projection cache must not grow with frozen history.");
            }
        }
        [Test] public void MovementSnapshotPreservesStartTickAfterEarlierSegmentsHaveBeenCommitted()
        {
            var segment = MovementSegment.Create(new ActionPlanId(1), 3, Task09A2Fixture.Hero,
                new GridPoint(0, 0), new GridPoint(2, 0), 75, PathCostRules.FrozenV1, 5);
            var projection = MovementSnapshotProjection.Segments(new[] { segment }).Single();
            Assert.That(projection.StartTick, Is.EqualTo(75));
            Assert.That(projection.EndTick, Is.EqualTo(80));
            Assert.That(projection.StepIndex, Is.EqualTo(3));
        }
        private static HistoryArchiveCandidate Freeze(ActionTerminationReason reason, long start,
            out ActionPlanSnapshot snapshot)
        {
            using (var sim = BattleSimulation.Create(Task09A2Fixture.BuildDefinition(), Task09A2Fixture.EncounterId,
                Task09A2Fixture.Inputs, new BattleSimulationAssembly()))
            {
                sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 400);
                sim.WindowManager.TryOpenDueWindow(0);
                var result = sim.ScheduleEditor.Apply(new ScheduleEditOperation[] {
                    new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId),
                        start, PrimaryTargetUnitId: Task09A2Fixture.Monster) }, 1, 0, 0,
                    expectedWindowId: new WindowId(1), issuer: Task09A2Fixture.PlayerId);
                Assert.That(result.Succeeded, Is.True, result.RejectionCode);
                var plan = sim.ScheduleAuthority.Registry.Find(result.AddedPlanIds.Single());
                sim.TerminalCoordinator.BeginTick(2);
                sim.TerminalCoordinator.EnterTerminal(plan, reason, 2);
                sim.TerminalCoordinator.EndTick(2);
                var source = new ActionPlanTerminalArchiveSource(sim.ScheduleAuthority, sim.TerminalCoordinator, sim.FactionResolver);
                var candidate = source.CollectOrdered(2).Single();
                snapshot = sim.ScheduleAuthority.Registry.FindFrozenTerminal(plan.ActionPlanId);
                Assert.That(snapshot.ReservedTurnBudgetTicks, Is.Zero, "Freeze must occur after budget cleanup.");
                Assert.That(snapshot.TerminationReason, Is.EqualTo((int)reason));
                Assert.That(snapshot.StartTick, Is.EqualTo(start));
                Assert.That(source.CollectOrdered(2).Single().Payload, Is.EqualTo(candidate.Payload));
                return candidate;
            }
        }
        [Test] public void FullTerminalHistoryDistinguishesReasonAndFinalTimingForTheSameKeyAndTick()
        {
            var cancelled = Freeze(ActionTerminationReason.CancelledByCommand, 10, out _);
            var invalid = Freeze(ActionTerminationReason.TargetInvalid, 10, out _);
            var moved = Freeze(ActionTerminationReason.CancelledByCommand, 20, out _);
            Assert.That(cancelled.StableKey, Is.EqualTo(invalid.StableKey));
            Assert.That(cancelled.ArchivedAtTick, Is.EqualTo(invalid.ArchivedAtTick));
            Assert.That(cancelled.Payload.Length, Is.GreaterThan(0));
            Assert.That(cancelled.Payload.SequenceEqual(invalid.Payload), Is.False);
            Assert.That(cancelled.Payload.SequenceEqual(moved.Payload), Is.False);
            var a = new HistoryArchive(); a.AppendFinalizedOrdered(2, new[] { cancelled });
            var b = new HistoryArchive(); b.AppendFinalizedOrdered(2, new[] { invalid });
            Assert.That(a.Summary.Digest, Is.Not.EqualTo(b.Summary.Digest));
            var records = a.BuildDiagnosticView(1).Records;
            Assert.That(CanonicalHash.ToHex(HistoryDigestProtocol.RecomputeFromSeed(records)), Is.EqualTo(a.Summary.Digest));
        }
        [Test] public void TerminalArchiveOwnsItsPayloadAndEmptyTicksLeaveOldRecordsUntouched()
        {
            var candidate = Freeze(ActionTerminationReason.CancelledByCommand, 10, out var snapshot);
            var archive = new HistoryArchive(); archive.AppendFinalizedOrdered(2, new[] { candidate });
            var original = archive.BuildDiagnosticView(1).Records.Single().PayloadCopy();
            candidate.Payload[0] ^= 0xff;
            var copy = archive.BuildDiagnosticView(1).Records.Single().PayloadCopy();
            Assert.That(copy, Is.EqualTo(original));
            copy[0] ^= 0xff;
            Assert.That(archive.BuildDiagnosticView(1).Records.Single().PayloadCopy(), Is.EqualTo(original));
            var baseline = archive.AccessCounters;
            for (int i = 3; i < 103; i++) archive.AppendFinalizedOrdered(i, null);
            var delta = archive.AccessCounters.DeltaFrom(baseline);
            Assert.That(delta.OldRecordReads + delta.OldRecordCopies + delta.OldRecordHashes, Is.Zero);
            Assert.That(snapshot.TerminalTick, Is.EqualTo(2));
        }
        [Test] public void PublicArchivedRecordConstructorCannotAliasTheCallersMutableArray()
        {
            var bytes = new byte[] { 1, 2, 3 };
            var record = new ArchivedRecord(1, 2, HistoryRecordKind.ActionPlanTerminal, "1", bytes, 0);
            bytes[0] = 99;
            Assert.That(record.PayloadCopy(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
    }
}
