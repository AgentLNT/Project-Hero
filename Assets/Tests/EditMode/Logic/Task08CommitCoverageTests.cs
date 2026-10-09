using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Units;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Tests
{
    public class Task08CommitCoverageTests
    {
        private static Task08DefenseCoverageTests.Rig MovingTarget(bool dodge = false, bool stillHit = false, bool lethal = false)
        {
            var d = Task08DefenseCoverageTests.Definition(coversDestination: stillHit, health: lethal ? 5 : 2000);
            var pattern = new AttackPatternSpec(new AttackPatternId("attack.pattern.t08.moving"),
                Enumerable.Range(0, 12).Select(f => new DirectionalTriangleSet((GridDirection)f,
                    stillHit ? new[] { new TrianglePoint(-3, 0, 1), new TrianglePoint(-3, 2, 1) }
                        : new[] { new TrianglePoint(-3, 0, 1) })).ToArray());
            d = d with { Actions = d.Actions.Select(a => a.Type == ActionType.Attack
                ? a with { Payload = ((AttackPayloadSpec)a.Payload) with { Pattern = pattern } } : a).ToArray() };
            var r = new Task08DefenseCoverageTests.Rig { Sim = Task09A2Fixture.NewSimWithMeta(1, d,
                new BattleSimulationAssembly(concurrentHeroUnitId: Task09A2Fixture.Hero)) };
            r.Attack = Task09A2Fixture.ArrangeStartedAttack(r.Sim, Task09A2Fixture.Hero,
                new ActionSpecId(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Monster, dodge);
            var sim = r.Sim; var window = sim.CurrentTurnWindow;
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, new CommandRequest(2,
                new WindowCommandScope(window.WindowId), new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction)));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 2)), Is.Empty);
            var moves = dodge ? new ScheduleEditOperation[] {
                new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero, Task08DefenseCoverageTests.MoveId, 30, default,
                    null, GridDirection.East, new GridPoint(0, 2)),
                new AddOrdinaryPlanOperation(2, Task09A2Fixture.Hero, Task08DefenseCoverageTests.MoveId, 75, default,
                    null, GridDirection.East, new GridPoint(0, 4)) }
                : new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero,
                    Task08DefenseCoverageTests.MoveId, 3, default, null, GridDirection.East, new GridPoint(0, 2)) };
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, new CommandRequest(3,
                new ScheduleEditScope(sim.ScheduleAuthority.ScheduleRevision, window.WindowId), new ScheduleEditPayload(moves)));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 3)), Is.Empty);
            r.Reaction = sim.ScheduleAuthority.Registry.ActivePlans.First(p => p.ActionType == ActionType.Move);
            if (dodge)
            {
                var o = Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Hero);
                Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.Reaction(4, o.Id,
                    ReactionCommandKind.Dodge, new ActionSpecId(Task09A2Fixture.DodgeSpecId), new GridPoint(0, 2)));
                Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 4)), Is.Empty);
            }
            Task09A2Fixture.AdvanceTo(sim, 10); r.Result = Task09A2Fixture.StepNext(sim); return r;
        }

        [Test]
        public void DodgeRelocationInvalidatesDependentMovesBeforeContactGraphBuild()
        {
            using var r = MovingTarget(dodge: true);
            var moves = r.Result.Events.Events.OfType<ActionPlanTerminatedEvent>().Where(e => e.Reason == (int)ActionTerminationReason.MovementOriginInvalidatedByDodge).ToArray();
            Assert.That(moves.Length, Is.EqualTo(2));
            Assert.That(r.Sim.StagedResolution.DodgeContacts.Single().Invalidated, Is.True);
            Assert.That(r.Sim.CurrentSnapshot.MovementSegments, Is.Empty); Assert.That(r.Sim.CurrentSnapshot.Reservations, Is.Empty);
            Assert.That(r.Sim.CurrentTurnWindow.ReservedBudgetTicks, Is.Zero);
            Assert.That(moves.Max(e => e.Sequence), Is.LessThan(r.Result.Events.Events.OfType<DodgeResolvedEvent>().Single().Sequence));
        }
        [Test]
        public void DodgeStillHitByAreaAttackInvalidatesOldOriginMovesWithoutSuccessReward()
        {
            using var r = MovingTarget(dodge: true, stillHit: true);
            Assert.That(r.Result.Events.Events.OfType<ActionPlanTerminatedEvent>().Count(e => e.Reason == (int)ActionTerminationReason.MovementOriginInvalidatedByDodge), Is.EqualTo(2));
            Assert.That(r.Result.Events.Events.OfType<DodgeResolvedEvent>().Single().RewardedSuccess, Is.False);
            Assert.That(Task08DefenseCoverageTests.Health(r.Sim, Task09A2Fixture.Hero), Is.EqualTo(1990 * 1024));
            Assert.That(r.Sim.CurrentTurnWindow.ReservedBudgetTicks, Is.Zero);
        }
        [Test]
        public void ForcedDisplacementPlanInvalidationPrecedesLaterInterceptTerminalRequest()
        {
            using var r = MovingTarget();
            Assert.That(r.Sim.StagedResolution.Moves.Single().Outcome, Is.EqualTo(MoveContactOutcome.Intercepted));
            Assert.That(r.Reaction.TerminationReason, Is.EqualTo(ActionTerminationReason.MovementOriginInvalidated));
            Assert.That(r.Result.Events.Events.OfType<ActionPlanTerminatedEvent>().Count(e => e.ActionPlanId == r.Reaction.ActionPlanId.Value), Is.EqualTo(1));
        }
        [Test]
        public void AttackUsesMovementSegmentInsteadOfVisualPosition()
        {
            using var r = MovingTarget();
            var report = r.Sim.LastDamageCommitReport.Units.Single();
            Assert.That(report.UnitId, Is.EqualTo(Task09A2Fixture.Hero)); Assert.That(report.DamageQ10, Is.EqualTo(10240));
            var move = r.Sim.StagedResolution.Moves.Single(); Assert.That(move.CoveredBefore && move.CoveredAfter, Is.True);
            // The future destination (0,2) was never committed; the attack saw the occupied origin, then knockback relocated it.
            Assert.That(r.Result.Events.Events.OfType<ForcedDisplacementResolvedEvent>().Single().From, Is.EqualTo(new GridPoint(0, 0)));
        }
        [Test]
        public void ForcedDisplacementBatchCommitsBeforeEventsStateControlAndDeath()
        {
            using var r = MovingTarget(lethal: true);
            var relocation = r.Result.Events.Events.OfType<ForcedDisplacementResolvedEvent>().Single();
            var control = r.Result.Events.Events.OfType<UnitStateChangedEvent>().First(e => e.Tick == 11 && e.ToState == UnitState.Staggered);
            var death = r.Result.Events.Events.OfType<UnitDiedEvent>().Single();
            Assert.That(relocation.Sequence, Is.LessThan(control.Sequence)); Assert.That(control.Sequence, Is.LessThan(death.Sequence));
            Assert.That(relocation.From, Is.Not.EqualTo(relocation.To));
            Assert.That(r.Sim.LogicGrid.CellsOf(death.UnitId), Is.Empty);
        }
        [Test]
        public void LethalTargetIsDisplacedBeforeDeathRemovesFinalFootprint()
        {
            using var r = MovingTarget(lethal: true);
            var e = r.Result.Events.Events.OfType<ForcedDisplacementResolvedEvent>().Single();
            Assert.That(e.AppliedSteps, Is.EqualTo(1)); Assert.That(e.To, Is.Not.EqualTo(e.From));
            Assert.That(r.Sim.CurrentSnapshot.Units.Single(u => u.UnitId == Task09A2Fixture.Hero.Value).X, Is.EqualTo(e.To.X));
            Assert.That(r.Sim.LogicGrid.CellsOf(Task09A2Fixture.Hero), Is.Empty);
        }
        [Test]
        public void LethalHitTerminatesVictimsEditableLockedAndRunningPlans()
        {
            using var running = MovingTarget(lethal: true);
            Assert.That(running.Reaction.IsTerminal, Is.True); Assert.That(running.Sim.ScheduleAuthority.FindLane(Task09A2Fixture.Hero).Plans, Is.Empty);
            using var editable = MovingTarget(dodge: true, stillHit: true, lethal: true);
            Assert.That(editable.Sim.ScheduleAuthority.FindLane(Task09A2Fixture.Hero).Plans, Is.Empty);
            Assert.That(editable.Sim.CurrentSnapshot.Reservations, Is.Empty);
            Assert.That(editable.Result.Events.Events.OfType<UnitDiedEvent>().Count(e => e.UnitId == Task09A2Fixture.Hero), Is.EqualTo(1));
        }

        [Test]
        public void AllDodgesUseSamePreCommitContactSnapshot()
        {
            using var sim = Task09A2Fixture.NewSim(Task08DefenseCoverageTests.Definition(area: true));
            sim.RegisterControllerBinding(Task09A2Fixture.AiId, new[] { Task09A2Fixture.FragileHostile });
            Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            Task09A2Fixture.SeedAdrenaline(sim, Task09A2Fixture.FragileHostile, 2);
            foreach (var pair in new[] { (Task09A2Fixture.Monster, new GridPoint(6, 2)), (Task09A2Fixture.FragileHostile, new GridPoint(10, 2)) })
            {
                var o = Task09A2Fixture.OpenOpportunityFor(sim, pair.Item1);
                Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.Reaction(2, o.Id, ReactionCommandKind.Dodge,
                    new ActionSpecId(Task09A2Fixture.DodgeSpecId), pair.Item2));
            }
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 2)), Is.Empty);
            Task09A2Fixture.AdvanceTo(sim, 11);
            Assert.That(sim.LastDodgeCommitReport.Results.Count, Is.EqualTo(2));
            Assert.That(sim.StagedResolution.DodgeContacts.Count, Is.EqualTo(2));
            Assert.That(sim.StagedResolution.DodgeContacts.All(c => c.CoveredBefore && !c.CoveredAfter && c.Invalidated), Is.True);
            Assert.That(sim.LastDodgeCommitReport.Before.Units.Where(u => u.UnitId == Task09A2Fixture.Monster || u.UnitId == Task09A2Fixture.FragileHostile).All(u => u.Anchor.Y == 0), Is.True);
            Assert.That(sim.LastDodgeCommitReport.After.Units.Where(u => u.UnitId == Task09A2Fixture.Monster || u.UnitId == Task09A2Fixture.FragileHostile).All(u => u.Anchor.Y == 2), Is.True);
        }

        [Test]
        public void NaturalDamageAccrualFundsNextRealBlockWithoutFixtureSeeding()
        {
            using var sim = Task09A2Fixture.NewSim(Task08DefenseCoverageTests.Definition(force: 0.1f));
            Assert.That(sim.AdrenalineLedgerOf(Task09A2Fixture.Monster).AvailableAdrenaline, Is.Zero);
            Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);
            Task09A2Fixture.AdvanceTo(sim, 13);
            Assert.That(sim.AdrenalineLedgerOf(Task09A2Fixture.Monster).AvailableAdrenaline, Is.GreaterThanOrEqualTo(2));
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(14, sim.ScheduleAuthority.ScheduleRevision,
                sim.CurrentTurnWindow.WindowId, Task09A2Fixture.Hero, new ActionSpecId(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Monster, 14));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 14)), Is.Empty);
            var o = Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.Reaction(15, o.Id, ReactionCommandKind.Block, new ActionSpecId(Task09A2Fixture.BlockSpecId)));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 15)), Is.Empty);
            Task09A2Fixture.AdvanceTo(sim, 24);
            Assert.That(sim.StagedResolution.BlockPlans.Single().AnyEligibleContact, Is.True);
            Assert.That(sim.AdrenalineLedgerOf(Task09A2Fixture.Monster).ReservationCount, Is.Zero);
            Assert.That(Task08DefenseCoverageTests.Health(sim, Task09A2Fixture.Monster), Is.EqualTo(1990 * 1024));
        }

        [Test]
        public void GuardBlockOrDodgeDoesNotTerminateSourceAttackPlan()
        {
            foreach (var kind in new[] { ReactionCommandKind.Block, ReactionCommandKind.Dodge })
            {
                using var r = Task08DefenseCoverageTests.ReactionRig(kind);
                Assert.That(r.Attack.IsRunning, Is.True);
                Assert.That(r.Result.Events.Events.OfType<ActionPlanTerminatedEvent>().Any(e => e.ActionPlanId == r.Attack.ActionPlanId.Value), Is.False);
            }
            using var guard = Guard();
            Assert.That(guard.Result.Events.Events.OfType<GuardPartiallyResistedEvent>().Count(), Is.EqualTo(1));
            Assert.That(Task08DefenseCoverageTests.Health(guard.Sim, Task09A2Fixture.Monster), Is.EqualTo(1995 * 1024));
            Assert.That(guard.Attack.IsRunning, Is.True);
        }

        internal static Task08DefenseCoverageTests.Rig Guard()
        {
            var sim = Task09A2Fixture.NewSim(Task08DefenseCoverageTests.Definition(force: 0.1f));
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Monster, 400); Task09A2Fixture.Step(sim, 0);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.AddPlan(1, 0, sim.CurrentTurnWindow.WindowId,
                Task09A2Fixture.Monster, Task08DefenseCoverageTests.GuardId, null, 9));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1)), Is.Empty);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.CloseWindow(2, sim.CurrentTurnWindow.WindowId));
            Task09A2Fixture.Step(sim, 2); sim.WindowManager.ScheduleWindow(3, Task09A2Fixture.Hero, 400); Task09A2Fixture.Step(sim, 3);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(4, sim.ScheduleAuthority.ScheduleRevision,
                sim.CurrentTurnWindow.WindowId, Task09A2Fixture.Hero, new ActionSpecId(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Monster, 4));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 4)), Is.Empty);
            var attack = sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.ActionType == ActionType.Attack);
            Task09A2Fixture.AdvanceTo(sim, 13); var impact = Task09A2Fixture.StepNext(sim);
            return new Task08DefenseCoverageTests.Rig { Sim = sim, Attack = attack, Result = impact };
        }
    }
}
