using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    public class Task08MultiPartyCoverageTests
    {
        private static BattleDefinition Grant(BattleDefinition d, string slot)
            => d with { Encounters = d.Encounters.Select(e => e with { Controllers = e.Controllers.Select(b =>
                b.ControllerId == Task09A2Fixture.PlayerId ? b with { ControlledSlots = b.ControlledSlots.Concat(new[] { new EncounterSlotId(slot) }).ToArray() } : b).ToArray() }).ToArray() };

        internal static BattleDefinition MultiDefinition(bool clash)
        {
            var d = Grant(Task08DefenseCoverageTests.Definition(), "d_ally");
            var pattern = new AttackPatternSpec(new AttackPatternId("attack.pattern.t08.multi"), Enumerable.Range(0, 12).Select(f =>
                new DirectionalTriangleSet((GridDirection)f, clash
                    ? new[] { new TrianglePoint(3, 0, 1), new TrianglePoint(9, 0, 1), new TrianglePoint(-3, 0, 1), new TrianglePoint(-13, 0, 1), new TrianglePoint(25, 0, 1), new TrianglePoint(-19, 0, 1) }
                    : f == (int)GridDirection.West ? new[] { new TrianglePoint(-11, 0, 1) } : new[] { new TrianglePoint(9, 0, 1) })).ToArray());
            d = d with { Actions = d.Actions.Select(a => a.Type == ActionType.Attack
                ? a with { Payload = ((AttackPayloadSpec)a.Payload) with { Pattern = pattern } } : a).ToArray() };
            if (!clash)
            {
                var volume = new VolumeSpec(new VolumeSpecId("unit_volume.t08.two_contacts"), Enumerable.Range(0, 12).Select(f =>
                    new DirectionalTriangleSet((GridDirection)f, new[] { new TrianglePoint(3, 0, 1), new TrianglePoint(5, 0, 1) })).ToArray());
                d = d with { Volumes = d.Volumes.Concat(new[] { volume }).ToArray(), Units = d.Units.Select((u, i) =>
                    i == 1 ? u with { VolumeSpecId = volume.VolumeSpecId } : u).ToArray() };
            }
            return d;
        }

        internal static Task08DefenseCoverageTests.Rig Multi(bool clash)
        {
            var r = new Task08DefenseCoverageTests.Rig { Sim = Task09A2Fixture.NewSim(MultiDefinition(clash)) };
            var sim = r.Sim;
            var owners = clash ? new[] { Task09A2Fixture.Hero, Task09A2Fixture.Monster, Task09A2Fixture.Ally }
                : new[] { Task09A2Fixture.Hero, Task09A2Fixture.Ally };
            for (int i = 0; i < owners.Length; i++)
            {
                long openTick = i * 3; sim.WindowManager.ScheduleWindow(openTick, owners[i], 400);
                Task09A2Fixture.Step(sim, openTick);
                var controller = owners[i] == Task09A2Fixture.Monster ? Task09A2Fixture.AiId : Task09A2Fixture.PlayerId;
                var target = owners[i] == Task09A2Fixture.Monster ? Task09A2Fixture.Hero : Task09A2Fixture.Monster;
                var facing = i == 0 ? GridDirection.East : clash && i == 2 ? (GridDirection)4 : GridDirection.West;
                var add = new AddOrdinaryPlanOperation(1, owners[i], new ActionSpecId(Task09A2Fixture.AttackSpecId), 10, default, target, facing, null);
                Task09A2Fixture.Submit(sim, controller, new CommandRequest(openTick + 1,
                    new ScheduleEditScope(sim.ScheduleAuthority.ScheduleRevision, sim.CurrentTurnWindow.WindowId),
                    new ScheduleEditPayload(new ScheduleEditOperation[] { add })));
                Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.StepNext(sim)), Is.Empty);
                if (i < owners.Length - 1)
                {
                    Task09A2Fixture.Submit(sim, controller, Task09A2Fixture.CloseWindow(openTick + 2, sim.CurrentTurnWindow.WindowId));
                    Task09A2Fixture.StepNext(sim);
                }
            }
            Task09A2Fixture.AdvanceTo(sim, 19); r.Result = Task09A2Fixture.StepNext(sim); return r;
        }

        internal static Task08DefenseCoverageTests.Rig Reservation()
        {
            var d = Grant(Task08DefenseCoverageTests.Definition(force: 4), "c_neut");
            d = d with { Encounters = d.Encounters.Select(e => e with { Slots = e.Slots.Select(s =>
                s.SlotId.Value == "c_neut" ? s with { InitialPosition = new GridPoint(30, 0) } : s).ToArray() }).ToArray() };
            var r = new Task08DefenseCoverageTests.Rig { Sim = Task09A2Fixture.NewSim(d) };
            var sim = r.Sim; r.Attack = Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.CloseWindow(2, sim.CurrentTurnWindow.WindowId));
            Task09A2Fixture.StepNext(sim);
            sim.WindowManager.ScheduleWindow(3, Task09A2Fixture.Neutral, 400); Task09A2Fixture.StepNext(sim);
            var move = new AddOrdinaryPlanOperation(1, Task09A2Fixture.Neutral, Task08DefenseCoverageTests.MoveId, 30, default,
                null, GridDirection.East, new GridPoint(14, 0));
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, new CommandRequest(4,
                new ScheduleEditScope(sim.ScheduleAuthority.ScheduleRevision, sim.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[] { move })));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.StepNext(sim)), Is.Empty);
            r.Reaction = sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.ActionType == ActionType.Move);
            Assert.That(sim.LogicGrid.ReservationsOfPlanOrdered(r.Reaction.ActionPlanId), Is.Not.Empty);
            Task09A2Fixture.AdvanceTo(sim, 10); r.Result = Task09A2Fixture.StepNext(sim); return r;
        }
        [Test]
        public void ThreeWayClashTerminatesThreeRealScheduledAttacks()
        {
            using var r = Multi(true);
            Assert.That(r.Sim.StagedResolution.ClashTerminatedPlanIds.Count, Is.EqualTo(3));
            Assert.That(r.Sim.StagedResolution.Clashes.Single().Clash.Participants.Count, Is.EqualTo(3));
            Assert.That(r.Result.Events.Events.OfType<ActionPlanTerminatedEvent>().Count(e => e.Reason == (int)ActionTerminationReason.InterruptedByClash), Is.EqualTo(3));
        }
        [Test]
        public void OppositeFlankingContactsKeepPerAttackEventsAndZeroResultant()
        {
            using var r = Multi(false);
            Assert.That(r.Sim.StagedResolution.Clashes, Is.Empty);
            var aggregate = r.Sim.StagedResolution.Aggregations.Single();
            Assert.That(aggregate.TotalDamageQ10, Is.EqualTo(20480)); Assert.That(aggregate.TotalImpactUnits, Is.EqualTo(20000));
            Assert.That(aggregate.ResultantMomentumUnits, Is.Zero); Assert.That(aggregate.KnockbackSteps, Is.Zero);
            var events = r.Result.Events.Events.OfType<DamageChannelResolvedEvent>().ToArray();
            Assert.That(events.Length, Is.EqualTo(2)); Assert.That(events.Select(e => e.TotalDamageQ10), Is.EqualTo(new long[] { 10240, 10240 }));
            Assert.That(events.SelectMany(e => e.Channels).Select(c => c.RawQ10), Is.EqualTo(new long[] { 10240, 10240 }));
            Assert.That(r.Result.Events.Events.OfType<TargetAggregateResolvedEvent>().Count(), Is.EqualTo(1));
        }
        [Test]
        public void ActualImpactPreemptsFutureReservationInProductionStep()
        {
            using var r = Reservation();
            Assert.That(r.Reaction.TerminationReason, Is.EqualTo(ActionTerminationReason.ReservationPreemptedByForcedDisplacement));
            Assert.That(r.Sim.LogicGrid.ReservationsOfPlanOrdered(r.Reaction.ActionPlanId), Is.Empty);
            Assert.That(r.Result.Events.Events.OfType<ForcedDisplacementResolvedEvent>().Single().InvalidatedPlanIds, Does.Contain(r.Reaction.ActionPlanId));
        }

        [Test]
        public void MixedSameTickBlockReportsPartialSuccessAndAccruesOnce()
        {
            var d = Grant(Task08DefenseCoverageTests.Definition(force: 0.1f), "d_ally");
            var pattern = new AttackPatternSpec(new AttackPatternId("attack.pattern.t08.mixed_block"),
                Enumerable.Range(0, 12).Select(f => new DirectionalTriangleSet((GridDirection)f,
                    new[] { new TrianglePoint(9, 0, 1), new TrianglePoint(-13, 0, 1) })).ToArray());
            d = d with { Actions = d.Actions.Select(a => a.Type != ActionType.Attack ? a : a with {
                Timing = a.ActionSpecId.Value == Task09A2Fixture.AttackSpecId ? new AttackTimingSpec(7, 2) : a.Timing,
                Payload = a.ActionSpecId.Value == Task09A2Fixture.AlliedAttackSpecId
                    ? ((AttackPayloadSpec)a.Payload) with { Pattern = pattern, Tags = AttackTagMask.Reactable | AttackTagMask.Dodgeable,
                        DamageComponents = new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10, DamageTagMask.BypassActionResistance) } }
                    : ((AttackPayloadSpec)a.Payload) with { Pattern = pattern } }).ToArray() };
            using var sim = Task09A2Fixture.NewSim(d);
            Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster, new ActionSpecId(Task09A2Fixture.AlliedAttackSpecId));
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.CloseWindow(2, sim.CurrentTurnWindow.WindowId));
            Task09A2Fixture.StepNext(sim);
            sim.WindowManager.ScheduleWindow(3, Task09A2Fixture.Ally, 400); Task09A2Fixture.StepNext(sim);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(4, sim.ScheduleAuthority.ScheduleRevision,
                sim.CurrentTurnWindow.WindowId, Task09A2Fixture.Ally, new ActionSpecId(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Monster, 4));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.StepNext(sim)), Is.Empty);
            var ally = sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.OwnerUnitId == Task09A2Fixture.Ally);
            var opportunity = sim.ReactionOpportunities.ActiveOpportunities.Single(o => o.DefenderUnitId == Task09A2Fixture.Monster && o.SourceAttackPlanId == ally.ActionPlanId);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.Reaction(5, opportunity.Id,
                ReactionCommandKind.Block, new ActionSpecId(Task09A2Fixture.BlockSpecId)));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.StepNext(sim)), Is.Empty);
            Task09A2Fixture.AdvanceTo(sim, 10); var result = Task09A2Fixture.StepNext(sim);
            Assert.That(sim.StagedResolution.BlockPlans.Single().Contacts.Select(c => c.Outcome),
                Is.EqualTo(new[] { ProjectHero.Logic.Interactions.BlockContactOutcome.BlockIneffective, ProjectHero.Logic.Interactions.BlockContactOutcome.Blocked }));
            var summary = result.Events.Events.OfType<ReactionPlanResolvedEvent>().Single();
            Assert.That(summary.Kind, Is.EqualTo(ReactionResolutionKind.BlockedPartially));
            Assert.That(summary.ReasonCode, Is.EqualTo(InteractionEventCodes.CONTACT_PARTIALLY_RESISTED));
            Assert.That(summary.RewardsSuccess, Is.True); Assert.That(summary.AttackPlanIds.Count, Is.EqualTo(2));
            Assert.That(sim.LastAdrenalineAccrualFacts.Single(f => f.UnitId == Task09A2Fixture.Monster).SuccessfulBlockCount, Is.EqualTo(1));
            Assert.That(Task08DefenseCoverageTests.Health(sim, Task09A2Fixture.Monster), Is.EqualTo(1990 * 1024));
        }
    }
}
