using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Tests
{
    // All reactions in this suite enter through the registered command gateway and real Step pipeline.
    // SeedAdrenaline is explicitly a fixture prerequisite; the natural accrual chain has a separate test.
    public class Task08DefenseCoverageTests
    {
        internal static readonly UnitId Hero = Task09A2Fixture.Hero;
        internal static readonly UnitId Defender = Task09A2Fixture.Monster;
        internal static readonly GridPoint Destination = new GridPoint(6, 2);
        internal static readonly ActionSpecId GuardId = new ActionSpecId("action.t08.coverage.guard");
        internal static readonly ActionSpecId MoveId = new ActionSpecId("action.t08.coverage.move");

        internal static BattleDefinition Definition(bool coversDestination = false, bool area = false,
            bool partialBlock = false, bool ineffectiveBlock = false, int health = 2000,
            float force = 1f, bool farArea = false)
        {
            var definition = Task09A2Fixture.BuildDefinition(withFragileHostile: area);
            var points = farArea ? new[] { new TrianglePoint(-31, 0, 1) }
                : coversDestination ? new[] { new TrianglePoint(9, 0, 1), new TrianglePoint(9, 2, 1), new TrianglePoint(13, 0, 1) }
                : new[] { new TrianglePoint(9, 0, 1), new TrianglePoint(13, 0, 1) };
            var pattern = new AttackPatternSpec(new AttackPatternId("attack.pattern.t08.coverage"),
                Enumerable.Range(0, 12).Select(f => new DirectionalTriangleSet((GridDirection)f, points)).ToArray());
            var components = ineffectiveBlock
                ? new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f, DamageTagMask.BypassActionResistance) }
                : partialBlock ? new[] {
                    new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f, DamageTagMask.Blockable | DamageTagMask.Guardable),
                    new DamageComponentSpec(DamageChannels.True, 3f, DamageTagMask.BypassActionResistance | DamageTagMask.BypassPassiveResistance) }
                : new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f, DamageTagMask.Blockable | DamageTagMask.Guardable) };
            var actions = definition.Actions.Select(a => a.Type == ActionType.Attack
                ? a with { Payload = ((AttackPayloadSpec)a.Payload) with { Pattern = pattern,
                    TargetPolicy = area ? TargetPolicy.AllTargetsInArea : TargetPolicy.PrimaryTargetOnly,
                    DamageComponents = components, ForceMultiplier = force,
                    Tags = ineffectiveBlock ? AttackTagMask.Reactable | AttackTagMask.Dodgeable
                        : AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable } } : a).ToList();
            actions.Add(new ActionSpec(GuardId, ActionType.Guard, new GuardTimingSpec(2, 10, 2),
                new GuardPayloadSpec(new Dictionary<DamageChannelId, int> { { DamageChannels.PhysicalBlunt, 512 } },
                    512, DefenseTagMask.Guardable), 0));
            actions.Add(new ActionSpec(MoveId, ActionType.Move, new MoveTimingSpec(20, 1),
                new MovePayloadSpec(200, definition.MovementPatterns[0]), 0));
            return definition with { Actions = actions,
                Units = definition.Units.Select(u => u with { InitialHealth = health }).ToArray(),
                ActionSets = definition.ActionSets.Select(s => s with {
                    ActionSpecIds = s.ActionSpecIds.Concat(new[] { GuardId, MoveId }).ToArray() }).ToArray() };
        }

        internal sealed class Rig : IDisposable
        {
            public BattleSimulation Sim;
            public ActionPlan Attack;
            public ActionPlan Reaction;
            public StepResult Result;
            public void Dispose() => Sim.Dispose();
        }

        internal static Rig ReactionRig(ReactionCommandKind kind = ReactionCommandKind.Dodge,
            BattleDefinition definition = null, Action<Rig> beforeImpact = null)
        {
            var rig = new Rig { Sim = Task09A2Fixture.NewSim(definition ?? Definition()) };
            rig.Attack = Task09A2Fixture.ArrangeStartedAttack(rig.Sim, Defender);
            var opportunity = Task09A2Fixture.OpenOpportunityFor(rig.Sim, Defender);
            Task09A2Fixture.Submit(rig.Sim, Task09A2Fixture.AiId,
                Task09A2Fixture.Reaction(2, opportunity.Id, kind,
                    new ActionSpecId(kind == ReactionCommandKind.Dodge ? Task09A2Fixture.DodgeSpecId : Task09A2Fixture.BlockSpecId),
                    kind == ReactionCommandKind.Dodge ? Destination : (GridPoint?)null));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(rig.Sim, 2)), Is.Empty);
            rig.Reaction = rig.Sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.IsReaction);
            Task09A2Fixture.AdvanceTo(rig.Sim, 10);
            beforeImpact?.Invoke(rig);
            rig.Result = Task09A2Fixture.StepNext(rig.Sim);
            return rig;
        }

        internal static IReadOnlyList<T> Events<T>(Rig r) where T : LogicEvent => r.Result.Events.Events.OfType<T>().ToArray();
        internal static int Health(BattleSimulation sim, UnitId u) => sim.CurrentSnapshot.Units.Single(x => x.UnitId == u.Value).HealthQ10;
        internal static void AssertNoSuccess(Rig r) => Assert.That(Events<ReactionPlanResolvedEvent>(r).Any(e => e.RewardsSuccess), Is.False);

        [Test]
        public void DodgeInvalidatesOnlyDodgeableContactsMissingCommittedDestination()
        {
            using var r = ReactionRig();
            var contact = r.Sim.StagedResolution.DodgeContacts.Single();
            Assert.That(contact.CoveredBefore, Is.True); Assert.That(contact.CoveredAfter, Is.False);
            Assert.That(contact.Outcome, Is.EqualTo(DodgeContactOutcome.Dodged));
            Assert.That(Health(r.Sim, Defender), Is.EqualTo(2000 * 1024));
            Assert.That(Events<DodgeResolvedEvent>(r).Single().RewardedSuccess, Is.True);
        }

        [Test]
        public void DodgeStillGetsHitWhenAttackCoversCommittedDestination()
        {
            using var r = ReactionRig(definition: Definition(coversDestination: true));
            Assert.That(r.Sim.StagedResolution.DodgeContacts.Single().Outcome, Is.EqualTo(DodgeContactOutcome.StillHit));
            Assert.That(Health(r.Sim, Defender), Is.EqualTo(1990 * 1024)); AssertNoSuccess(r);
        }

        [Test]
        public void DodgeCommitEmitsOneTriggerEvenWithoutSuccessfulAvoidance()
        {
            using var r = ReactionRig(definition: Definition(coversDestination: true));
            Assert.That(Events<ReactionTriggeredEvent>(r).Count, Is.EqualTo(1));
            Assert.That(Events<DodgeResolvedEvent>(r).Count, Is.EqualTo(1));
            Assert.That(Events<DodgeResolvedEvent>(r).Single().StillHitAttackPlanIds, Is.EqualTo(new[] { r.Attack.ActionPlanId }));
            AssertNoSuccess(r);
        }

        [Test]
        public void FailedDodgeCommitEmitsResolutionButNoTriggerOrReward()
        {
            using var r = ReactionRig(beforeImpact: rig => Assert.That(rig.Sim.LogicGrid.CommitAnchor(
                Task09A2Fixture.Neutral, Destination, GridDirection.East), Is.Null));
            Assert.That(Events<ReactionTriggeredEvent>(r), Is.Empty);
            Assert.That(Events<DodgeResolvedEvent>(r).Single().DestinationCommitted, Is.False); AssertNoSuccess(r);
            Assert.That(r.Reaction.TerminationReason, Is.EqualTo(ActionTerminationReason.TargetInvalid));
            Assert.That(Health(r.Sim, Defender), Is.EqualTo(1990 * 1024));
        }

        [Test]
        public void DodgeWithoutContactsEmitsOneResolutionWithNullGroup()
        {
            using var r = ReactionRig(definition: Definition(farArea: true));
            var e = Events<DodgeResolvedEvent>(r).Single();
            Assert.That(e.ConflictGroupKey, Is.Zero); Assert.That(e.DestinationCommitted, Is.True);
            Assert.That(e.ParticipatingPlanIds, Is.EqualTo(new[] { r.Reaction.ActionPlanId })); AssertNoSuccess(r);
        }

        [Test]
        public void DodgeCancelledBeforeTriggerDoesNotEmitTriggerOrResolution()
        {
            using var r = ReactionRig(beforeImpact: rig => rig.Sim.TerminalCoordinator.EnterTerminal(
                rig.Reaction, ActionTerminationReason.InterruptedByControl, 10));
            Assert.That(Events<DodgeResolvedEvent>(r), Is.Empty); Assert.That(Events<ReactionTriggeredEvent>(r), Is.Empty);
            Assert.That(r.Sim.LogicGrid.TryGetAnchor(Defender, out var anchor), Is.True);
            Assert.That(anchor, Is.Not.EqualTo(Destination));
        }

        [Test]
        public void DodgedAoeTargetDoesNotProtectOtherTargets()
        {
            using var r = ReactionRig(definition: Definition(area: true));
            Assert.That(Health(r.Sim, Defender), Is.EqualTo(2000 * 1024));
            Assert.That(Health(r.Sim, Task09A2Fixture.FragileHostile), Is.EqualTo(1990 * 1024));
            Assert.That(r.Sim.LastDamageCommitReport.Units.Select(u => u.UnitId), Is.EqualTo(new[] { Task09A2Fixture.FragileHostile }));
        }

        [TestCase(ReactionCommandKind.Block)] [TestCase(ReactionCommandKind.Dodge)]
        public void BlockOrDodgeDoesNotTerminateAttackingPlan(ReactionCommandKind kind)
        {
            using var r = ReactionRig(kind);
            Assert.That(r.Attack.IsRunning, Is.True); Assert.That(r.Attack.TerminationReason, Is.EqualTo(ActionTerminationReason.None));
            Task09A2Fixture.AdvanceTo(r.Sim, r.Attack.EndTick); Assert.That(r.Attack.IsCompleted, Is.True);
        }

        [Test]
        public void BlockZeroesAllEligibleDamageAndMomentumOnTriggerTick()
        {
            using var r = ReactionRig(ReactionCommandKind.Block);
            var c = r.Sim.StagedResolution.BlockPlans.Single().Contacts.Single();
            Assert.That(c.Outcome, Is.EqualTo(BlockContactOutcome.Blocked));
            Assert.That(c.RawDamageQ10, Is.EqualTo(10 * 1024)); Assert.That(c.IncomingMomentumUnits, Is.GreaterThan(0));
            Assert.That(c.AfterBlockDamageQ10, Is.Zero); Assert.That(c.AfterBlockMomentumUnits, Is.Zero);
            Assert.That(Health(r.Sim, Defender), Is.EqualTo(2000 * 1024));
        }

        [Test]
        public void PartiallyBlockedContactKeepsBypassComponents()
        {
            using var r = ReactionRig(ReactionCommandKind.Block, Definition(partialBlock: true));
            Assert.That(r.Sim.StagedResolution.BlockPlans.Single().Contacts.Single().Outcome, Is.EqualTo(BlockContactOutcome.PartiallyBlocked));
            Assert.That(Health(r.Sim, Defender), Is.EqualTo(1997 * 1024));
            Assert.That(Events<BlockResolvedEvent>(r).Single().AfterBlockDamageQ10, Is.EqualTo(3 * 1024));
        }

        [Test]
        public void FullyUnblockableContactIsIneffectiveHitAndDoesNotRewardBlock()
        {
            // A Block accepted against a valid source also sees an independent bypass-only contact in the staged fixture.
            var context = Task08RemainingCoverageTests.DefenseContext(ActionType.Block, ineffective: true);
            var result = StagedConflictResolver.Resolve(context);
            Assert.That(result.BlockPlans.Single().Contacts.Single().Outcome, Is.EqualTo(BlockContactOutcome.BlockIneffective));
            Assert.That(result.BlockPlans.Single().AnyEligibleContact, Is.False);
            Assert.That(result.Aggregations.Single().TotalDamageQ10, Is.EqualTo(10 * 1024));
        }

        [Test]
        public void BlockingStateOutsideTriggerTickProvidesNoResistance()
        {
            var context = Task08RemainingCoverageTests.DefenseContext(ActionType.Block, triggerTick: 12);
            var result = StagedConflictResolver.Resolve(context);
            Assert.That(result.BlockPlans, Is.Empty);
            Assert.That(result.Aggregations.Single().TotalDamageQ10, Is.EqualTo(10 * 1024));
        }

        [Test]
        public void DodgingStateWithoutTriggerCommitProvidesNoAvoidance()
        {
            var context = Task08RemainingCoverageTests.DefenseContext(ActionType.Dodge, committed: false);
            var result = StagedConflictResolver.Resolve(context);
            Assert.That(result.DodgeContacts, Is.Empty);
            Assert.That(result.Aggregations.Single().TotalDamageQ10, Is.EqualTo(10 * 1024));
        }

        [Test]
        public void DamageEventContainsConflictGroupPlanAndUnitIds()
        {
            using var sim = Task09A2Fixture.NewSim(Definition());
            var plan = Task09A2Fixture.ArrangeStartedAttackOnly(sim, Defender);
            Task09A2Fixture.AdvanceTo(sim, 10); var result = Task09A2Fixture.StepNext(sim);
            var e = result.Events.Events.OfType<DamageChannelResolvedEvent>().Single();
            Assert.That(e.ConflictGroupKey, Is.EqualTo(sim.ConflictGraph.Groups.Single().GroupKey));
            Assert.That(e.AttackPlanId, Is.EqualTo(plan.ActionPlanId)); Assert.That(e.AttackerUnitId, Is.EqualTo(Hero));
            Assert.That(e.TargetUnitId, Is.EqualTo(Defender)); Assert.That(e.ParticipatingPlanIds, Does.Contain(plan.ActionPlanId));
        }

        [Test]
        public void DamageEventContainsPerChannelAndMomentumBeforeAfterValues()
        {
            using var r = ReactionRig(ReactionCommandKind.Block, Definition(partialBlock: true));
            var e = Events<DamageChannelResolvedEvent>(r).Single();
            Assert.That(e.Channels.Single(c => c.ChannelId == DamageChannels.PhysicalBlunt).RawQ10, Is.EqualTo(10 * 1024));
            Assert.That(e.Channels.Single(c => c.ChannelId == DamageChannels.PhysicalBlunt).AfterActionResistanceQ10, Is.Zero);
            Assert.That(e.Channels.Single(c => c.ChannelId == DamageChannels.True).AfterActionResistanceQ10, Is.EqualTo(3 * 1024));
            Assert.That(e.IncomingMomentumUnits, Is.GreaterThan(0)); Assert.That(e.AfterMomentumResistanceUnits, Is.Zero);
        }

        [Test]
        public void FailedBlockOrDodgeProducesNoSuccessAccrualFact()
        {
            using var emptyBlock = ReactionRig(ReactionCommandKind.Block, Definition(farArea: true)); AssertNoSuccess(emptyBlock);
            using var hitDodge = ReactionRig(definition: Definition(coversDestination: true)); AssertNoSuccess(hitDodge);
            using var failedDodge = ReactionRig(beforeImpact: rig => Assert.That(rig.Sim.LogicGrid.CommitAnchor(
                Task09A2Fixture.Neutral, Destination, GridDirection.East), Is.Null)); AssertNoSuccess(failedDodge);
        }
    }
}
