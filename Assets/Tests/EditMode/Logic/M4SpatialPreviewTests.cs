using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Tests
{
    public sealed class M4SpatialPreviewTests
    {
        [Test] public void MoveThenAttackUsesProjectedOriginAndExactDiscreteBoundaryWithoutWritingWorld()
        {
            using var sim = Task09A2Fixture.NewSim(Task08DefenseCoverageTests.Definition());
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 180); Task09A2Fixture.Step(sim, 0);
            var before = sim.CurrentSnapshot.ComputeHash();
            var operations = new ScheduleEditOperation[] {
                new AddOrdinaryPlanOperation(10, Task09A2Fixture.Hero, Task08DefenseCoverageTests.MoveId, 1, Destination: new GridPoint(-2, 0)),
                new AddOrdinaryPlanOperation(20, Task09A2Fixture.Hero, new ActionSpecId(Task09A2Fixture.AttackSpecId),
                    PrimaryTargetUnitId: Task09A2Fixture.Monster, Facing: GridDirection.West) };
            var port = sim.HudPreviewFor(Task09A2Fixture.PlayerId);
            var projected = sim.ScheduleEditor.Apply(operations, 1, sim.ScheduleRevision, sim.ScheduleRevision, preview: true,
                expectedWindowId: sim.CurrentTurnWindow.WindowId, issuer: Task09A2Fixture.PlayerId);
            Assert.That(projected.Succeeded, Is.True, projected.RejectionCode);
            var move = projected.PreviewPlans.Single(p => p.Plan.ActionSpecId == Task08DefenseCoverageTests.MoveId.Value).Plan;
            long boundary = move.EndTick - move.RecoveryTicks;
            var early = port.PreviewSpace(boundary - 1, operations);
            var late = port.PreviewSpace(boundary, operations);
            Assert.That(early.RejectionCode, Is.Null);
            Assert.That(early.Units.Single(u => u.UnitId == Task09A2Fixture.Hero.Value).Position, Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(late.Units.Single(u => u.UnitId == Task09A2Fixture.Hero.Value).Position, Is.EqualTo(new GridPoint(-2, 0)));
            var attack = projected.PreviewPlans.Single(p => p.Plan.ActionSpecId == Task09A2Fixture.AttackSpecId).Plan;
            var aimed = port.PreviewSpace(attack.StartTick, operations);
            Assert.That(aimed.Attacks.Single().Origin, Is.EqualTo(new GridPoint(-2, 0)));
            Assert.That(aimed.Attacks.Single().Facing, Is.EqualTo(GridDirection.West));
            Assert.That(aimed.Attacks.Single().TemporaryPlanKey, Is.EqualTo(20));
            Assert.That(port.PreviewSpace(attack.StartTick, operations), Is.SameAs(aimed));
            Assert.That(sim.CurrentSnapshot.ComputeHash(), Is.EqualTo(before)); Assert.That(sim.Tick, Is.Zero);
        }

        [Test] public void HiddenEnemyEditableAttackNeverAppearsAndHorizonFreezesItsPose()
        {
            using var sim = Task09A2Fixture.NewSim(Task08DefenseCoverageTests.Definition());
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Monster, 30);
            sim.WindowManager.ScheduleWindow(31, Task09A2Fixture.Hero, 30);
            sim.WindowManager.ScheduleWindow(62, Task09A2Fixture.Monster, 30);
            Task09A2Fixture.Step(sim, 0);
            var created = sim.PlanFactory.TryCreateOrdinary(new ProjectHero.Logic.Timeline.OrdinaryPlanRequest(Task09A2Fixture.Monster,
                new ActionSpecId(Task09A2Fixture.AttackSpecId), GridDirection.West, Task09A2Fixture.Hero, null, sim.CurrentTurnWindow.WindowId, 10), sim.Tick);
            Assert.That(created.Succeeded, Is.True, created.RejectionCode);
            typeof(ProjectHero.Logic.Timeline.ActionScheduleAuthority).GetMethod("RegisterPlan", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(sim.ScheduleAuthority, new object[] {created.Plan});
            // Refresh the committed snapshot without starting the private future plan.
            Task09A2Fixture.StepNext(sim);
            var port = sim.HudPreviewFor(Task09A2Fixture.PlayerId);
            Assert.That(port.PreviewSpace(10).Attacks, Is.Empty);
            Assert.That(port.KnownHorizonTick, Is.EqualTo(62));
            var frame = port.PreviewSpace(62);
            var enemy = frame.Units.Single(u => u.UnitId == Task09A2Fixture.Monster.Value);
            Assert.That(enemy.IsUnknown, Is.True); Assert.That(enemy.Position, Is.EqualTo(new GridPoint(6, 0)));
            Assert.That(frame.Attacks, Is.Empty);
        }

        [Test] public void PublishedAttackHasRangeDuringWindupAndNotInRecovery()
        {
            using var sim = Task09A2Fixture.NewSim(Task08DefenseCoverageTests.Definition());
            var attack = Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            var port = sim.HudPreviewFor(Task09A2Fixture.AiId);
            Assert.That(port.PreviewSpace(sim.Tick).Attacks.Single().PlanId, Is.EqualTo(attack.ActionPlanId.Value));
            Assert.That(port.PreviewSpace(attack.ImpactTick).Attacks.Single().PlanId, Is.EqualTo(attack.ActionPlanId.Value));
            Assert.That(port.PreviewSpace(attack.ImpactTick + 1).Attacks, Is.Empty);
        }

        [Test] public void CompletedPublishedEnemyMoveKeepsItsDestinationInsideKnownHorizon()
        {
            using var sim = Task09A2Fixture.NewSim(Task08DefenseCoverageTests.Definition());
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Monster, 30);
            sim.WindowManager.ScheduleWindow(31, Task09A2Fixture.Hero, 30);
            sim.WindowManager.ScheduleWindow(62, Task09A2Fixture.Monster, 30);
            Task09A2Fixture.Step(sim, 0);
            Assert.That(sim.CommandIngress.FindEntry(Task09A2Fixture.AiId).Submit(new CommandRequest(1,
                new ScheduleEditScope(sim.ScheduleRevision, sim.CurrentTurnWindow.WindowId), new ScheduleEditPayload(new[] {
                    new AddOrdinaryPlanOperation(1, Task09A2Fixture.Monster, Task08DefenseCoverageTests.MoveId, 1, Destination: new GridPoint(6, 2)) }))), Is.Null);
            var submitted = Task09A2Fixture.StepNext(sim); Assert.That(Task09A2Fixture.AllRejectionCodes(submitted), Is.Empty);
            var port = sim.HudPreviewFor(Task09A2Fixture.PlayerId);
            var pose = port.PreviewSpace(50).Units.Single(u => u.UnitId == Task09A2Fixture.Monster.Value);
            Assert.That(pose.IsUnknown, Is.False, "horizon=" + port.KnownHorizonTick + "; plans=" + string.Join(";", sim.CurrentSnapshot.Plans));
            Assert.That(pose.Position, Is.EqualTo(new GridPoint(6, 2)));
            Assert.That(port.PreviewSpace(62).Units.Single(u => u.UnitId == Task09A2Fixture.Monster.Value).IsUnknown, Is.True);
            Task09A2Fixture.AdvanceTo(sim, 50);
            var actual = sim.CurrentSnapshot.Units.Single(u => u.UnitId == Task09A2Fixture.Monster.Value);
            Assert.That(pose.Position, Is.EqualTo(new GridPoint(actual.X, actual.Y)));
        }

        [TestCase(false)] [TestCase(true)]
        public void KnockbackStopMatchesProductionStepIncludingExplicitObstacleGeometry(bool obstacle)
        {
            using var sim = Task09A2Fixture.NewSim(Task08DefenseCoverageTests.Definition(force: 5));
            // Explicit static geometry fixture: the sample encounter has no authored terrain obstacles.
            // Configure the existing production solver before querying the read port; no alternate solver.
            if (obstacle) ((HashSet<GridPoint>)typeof(ForcedDisplacementSolver).GetField("_staticObstacles", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(sim.ForcedDisplacementSolver)).Add(new GridPoint(12, 0));
            var attack = Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            var before = sim.CurrentSnapshot.ComputeHash();
            var port = sim.HudPreviewFor(Task09A2Fixture.PlayerId); var result = port.PreviewSpatialResults();
            var stop = result.Knockbacks.Single(k => k.TargetUnitId == Task09A2Fixture.Monster.Value);
            if (obstacle) Assert.That(stop.StopReason, Is.EqualTo(ForcedDisplacementStopReason.StaticObstacle));
            Assert.That(stop.SourcePlanIds, Does.Contain(attack.ActionPlanId.Value));
            Assert.That(port.PreviewSpatialResults(), Is.SameAs(result)); Assert.That(sim.CurrentSnapshot.ComputeHash(), Is.EqualTo(before));
            // Cursor geometry deliberately excludes damage and forced displacement outcomes.
            Assert.That(port.PreviewSpace(attack.ImpactTick).Units.Single(u => u.UnitId == Task09A2Fixture.Monster.Value).Position, Is.EqualTo(new GridPoint(6, 0)));
            Task09A2Fixture.AdvanceTo(sim, attack.ImpactTick - 1);
            var actual = Task09A2Fixture.StepNext(sim).Events.Events.OfType<ForcedDisplacementResolvedEvent>().Single(e => e.TargetUnitId == Task09A2Fixture.Monster);
            Assert.That(stop.From, Is.EqualTo(actual.From)); Assert.That(stop.To, Is.EqualTo(actual.To));
            Assert.That(stop.AppliedSteps, Is.EqualTo(actual.AppliedSteps)); Assert.That(stop.StopReason, Is.EqualTo(actual.StopReason));
        }

        [Test] public void InvalidEditsAndOutOfRangeCursorsReturnNoInventedGeometry()
        {
            using var sim = Task09A2Fixture.NewSim(Task08DefenseCoverageTests.Definition());
            var attack = Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            var port = sim.HudPreviewFor(Task09A2Fixture.PlayerId); var hash = sim.CurrentSnapshot.ComputeHash();
            var operations = new ScheduleEditOperation[] { new MoveEditablePlanOperation(attack.ActionPlanId, 20) };
            var invalid = port.PreviewSpace(20, operations);
            Assert.That(invalid.RejectionCode, Is.Not.Null); Assert.That(invalid.Units, Is.Empty); Assert.That(invalid.Attacks, Is.Empty);
            Assert.That(port.PreviewSpatialResults(operations).RejectionCode, Is.Not.Null);
            Assert.That(port.PreviewSpace(sim.Tick - 1).RejectionCode, Is.EqualTo("SPATIAL_CURSOR_OUT_OF_RANGE"));
            Assert.That(port.PreviewSpace(sim.Tick + 3601).RejectionCode, Is.EqualTo("SPATIAL_CURSOR_OUT_OF_RANGE"));
            Assert.That(sim.CurrentSnapshot.ComputeHash(), Is.EqualTo(hash));
        }
    }
}
