using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Timeline;
using System.Reflection;

namespace ProjectHero.Logic.Tests
{
    public sealed class M2TimelineContactTests
    {
        [Test] public void NewDraftAttackAlsoProjectsItsContactBeforeAnyCommittedAttackExists()
        {
            using var sim = Task09A2Fixture.NewSim(Task08DefenseCoverageTests.Definition());
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 180); Task09A2Fixture.Step(sim, 0);
            var before = sim.CurrentSnapshot.ComputeHash();
            var contacts = sim.HudPreviewFor(Task09A2Fixture.PlayerId).PreviewTimelineContacts(new ScheduleEditOperation[] {
                new AddOrdinaryPlanOperation(77, Task09A2Fixture.Hero, new ActionSpecId(Task09A2Fixture.AttackSpecId), 1,
                    PrimaryTargetUnitId: Task09A2Fixture.Monster) });
            Assert.That(contacts.RejectionCode, Is.Null);
            Assert.That(contacts.Contacts.Any(c => c.TemporaryPlanKey == 77 && c.Outcome == "命中"), Is.True);
            Assert.That(sim.CurrentSnapshot.ComputeHash(), Is.EqualTo(before)); Assert.That(sim.CurrentSnapshot.Plans, Is.Empty);
        }
        [Test] public void AlignedOpposingAttacksShowClashOnlyWhenExactLogicResolvesIt()
        {
            var d = Task08DefenseCoverageTests.Definition();
            d = d with { Actions = d.Actions.Select(a => a.Type != ActionType.Attack ? a : a with {
                Payload = ((AttackPayloadSpec)a.Payload) with { Pattern = new AttackPatternSpec(new AttackPatternId("m2.clash"),
                    Enumerable.Range(0, 12).Select(f => new DirectionalTriangleSet((GridDirection)f,
                        Enumerable.Range(-41, 83).Where(x => (x & 1) != 0).Select(x => new TrianglePoint(x, 0, 1)).ToArray())).ToArray()) }
            }).ToArray() };
            using var sim = Task09A2Fixture.NewSim(d);
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 180); Task09A2Fixture.Step(sim, 0);
            foreach (var owner in new[] {Task09A2Fixture.Hero, Task09A2Fixture.Monster})
            {
                var target = owner == Task09A2Fixture.Hero ? Task09A2Fixture.Monster : Task09A2Fixture.Hero;
                var plan = sim.PlanFactory.TryCreateOrdinary(new OrdinaryPlanRequest(owner, new ActionSpecId(Task09A2Fixture.AttackSpecId),
                    owner == Task09A2Fixture.Hero ? GridDirection.East : GridDirection.West, target, null, sim.CurrentTurnWindow.WindowId, 1), sim.Tick);
                Assert.That(plan.Succeeded, Is.True, plan.RejectionCode);
                typeof(ActionScheduleAuthority).GetMethod("RegisterPlan", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(sim.ScheduleAuthority, new object[] {plan.Plan});
            }
            Task09A2Fixture.StepNext(sim); var before = sim.CurrentSnapshot.ComputeHash();
            var contacts = sim.HudPreviewFor(Task09A2Fixture.PlayerId).PreviewTimelineContacts(Array.Empty<ScheduleEditOperation>());
            Assert.That(contacts.Contacts.Count(c => c.Outcome == "拼刀"), Is.EqualTo(2));
            Assert.That(sim.CurrentSnapshot.ComputeHash(), Is.EqualTo(before));
            Task09A2Fixture.AdvanceTo(sim, Task09A2Fixture.AttackImpactTick - 1);
            Assert.That(Task09A2Fixture.StepNext(sim).Events.Events.OfType<ClashParticipantResolvedEvent>().Count(), Is.EqualTo(2));
        }
        [Test] public void TimelineContactComesFromExactResolutionAndDoesNotWriteAuthoritativeWorld()
        {
            using var sim = Task09A2Fixture.NewSim(Task08DefenseCoverageTests.Definition());
            var attack = Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            var before = sim.CurrentSnapshot.ComputeHash(); long tick = sim.Tick;
            var port = sim.HudPreviewFor(Task09A2Fixture.PlayerId);
            var result = port.PreviewTimelineContacts(Array.Empty<ScheduleEditOperation>());
            Assert.That(result.RejectionCode, Is.Null);
            Assert.That(result.Contacts.Any(c => c.PlanId == attack.ActionPlanId.Value && c.TargetUnitId == Task09A2Fixture.Monster.Value
                && c.Tick == attack.ImpactTick && c.Outcome == "命中"), Is.True);
            Assert.That(sim.CurrentSnapshot.ComputeHash(), Is.EqualTo(before)); Assert.That(sim.Tick, Is.EqualTo(tick));
            Assert.That(port.PreviewTimelineContacts(Array.Empty<ScheduleEditOperation>()), Is.SameAs(result));
            Task09A2Fixture.AdvanceTo(sim, attack.ImpactTick - 1);
            var actual = Task09A2Fixture.StepNext(sim);
            Assert.That(actual.Events.Events.OfType<DamageChannelResolvedEvent>().Any(d => d.AttackPlanId == attack.ActionPlanId), Is.True);
        }
        [Test] public void RejectedTimelineEditDoesNotProduceInventedContacts()
        {
            using var sim = Task09A2Fixture.NewSim(Task08DefenseCoverageTests.Definition());
            var attack = Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            var before = sim.CurrentSnapshot.ComputeHash();
            var result = sim.HudPreviewFor(Task09A2Fixture.PlayerId).PreviewTimelineContacts(new ScheduleEditOperation[] {
                new MoveEditablePlanOperation(attack.ActionPlanId, sim.Tick + 1) });
            Assert.That(result.RejectionCode, Is.Not.Null); Assert.That(result.Contacts, Is.Empty);
            Assert.That(sim.CurrentSnapshot.ComputeHash(), Is.EqualTo(before));
        }
    }
}
