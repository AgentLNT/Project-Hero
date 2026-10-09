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
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Tests
{
    public class Task08RemainingCoverageTests
    {
        internal static StagedResolutionContext DefenseContext(ActionType defense, bool ineffective = false,
            long triggerTick = 11, bool committed = true, bool coversAfter = false,
            bool undodgeable = false, bool eligible = true, bool reverse = false, int attacks = 1,
            bool clash = false, long activeStart = 10, long activeEnd = 12,
            bool beforeCovered = true, int passive = 0, int otherMomentum = 100)
        {
            var definition = Task08DefenseCoverageTests.Definition();
            var defender = Task09A2Fixture.Monster;
            var intents = new List<CombatIntent>(); var facts = new List<InteractionPlanFacts>();
            var specs = new List<PlanSpecEntry>();
            var pattern = new AttackPatternSpec(new AttackPatternId("attack.pattern.t08.staged"),
                Enumerable.Range(0, 12).Select(f => new DirectionalTriangleSet((GridDirection)f,
                    coversAfter ? new[] { new TrianglePoint(9, 0, 1), new TrianglePoint(9, 2, 1) }
                        : new[] { new TrianglePoint(9, 0, 1) })).ToArray());
            for (int i = 0; i < attacks; i++)
            {
                var p = new ActionPlanId(i + 1); var u = new UnitId(i == 0 ? 1 : 4);
                var dir = clash && i > 0 ? GridDirection.West : GridDirection.East;
                intents.Add(CombatIntentFactory.Create(new AttackIntentRequest(p, new ActionSpecId("action.t08.staged.attack"),
                    u, TargetPolicy.PrimaryTargetOnly, defender, TargetRelationMask.Hostile,
                    ineffective ? AttackTagMask.Dodgeable : undodgeable ? AttackTagMask.Blockable : AttackTagMask.Blockable | AttackTagMask.Dodgeable,
                    dir, pattern, new GridPoint(0, 0), 11, 6, i + 1,
                    new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f, ineffective
                        ? DamageTagMask.BypassActionResistance : DamageTagMask.Blockable | DamageTagMask.Guardable) },
                    MomentumPacket.Require(dir, i == 0 ? 100 : otherMomentum, ImpactProfiles.Blunt), 0, null)));
                facts.Add(new InteractionPlanFacts(p, u, ActionType.Attack, 1, 13, 11, 0, 0, 0, defender, false));
                specs.Add(new PlanSpecEntry(p, "action.t08.staged.attack"));
            }
            var defensePlan = new ActionPlanId(10);
            facts.Add(new InteractionPlanFacts(defensePlan, defender, defense, 1, 15, 0,
                activeStart, activeEnd, triggerTick, null, false));
            specs.Add(new PlanSpecEntry(defensePlan, "action.t08.staged.defense"));
            var before = new[] { new UnitOccupancy(new UnitId(1), new[] { new TrianglePoint(3, 0, 1) }, default),
                new UnitOccupancy(defender, new[] { new TrianglePoint(9, beforeCovered ? 0 : 4, 1) }, default),
                new UnitOccupancy(new UnitId(4), new[] { new TrianglePoint(25, 0, 1) }, default) };
            var after = before.Select(u => u.UnitId == defender && committed && defense == ActionType.Dodge
                ? new UnitOccupancy(defender, new[] { new TrianglePoint(9, 2, 1) }, default) : u).ToArray();
            var resolver = new FactionRelationResolver(definition.FactionModel,
                new Dictionary<UnitId, FactionId> { { new UnitId(1), Task09A2Fixture.HeroFaction },
                    { defender, Task09A2Fixture.MonsterFaction }, { new UnitId(4), Task09A2Fixture.HeroFaction } });
            if (reverse) { intents.Reverse(); facts.Reverse(); specs.Reverse(); Array.Reverse(before); Array.Reverse(after); }
            var graph = ConflictGraphBuilder.Build(new ConflictGraphInput(11, intents, facts, before, after, resolver));
            var dodgeReport = defense != ActionType.Dodge ? DodgeCommitReport.Empty(11)
                : new DodgeCommitReport(11, DodgeSpaceSnapshot.Empty,
                    committed ? new[] { new DodgeCommitResult(defensePlan, new ReactionOpportunityId(1), defender,
                        new GridPoint(6, 0), new GridPoint(6, 2), true, null, ActionTerminationReason.None, 11) }
                        : Array.Empty<DodgeCommitResult>(), DodgeSpaceSnapshot.Empty);
            var units = definition.Units.Select((u, i) => new UnitSnapshot(i + 1, u.UnitDefinitionId.Value,
                i == 1 ? Task09A2Fixture.MonsterFaction.Value : Task09A2Fixture.HeroFaction.Value,
                0, 0, 0, 2000 * 1024, true, 0, 0, CanReceiveDirectHit: eligible)).ToArray();
            var defs = definition.Units.Select(u => u with { BaseDamageResistanceQ10 =
                new Dictionary<DamageChannelId, int> { { DamageChannels.PhysicalBlunt, passive } } }).ToArray();
            return new StagedResolutionContext(graph, null, 0, facts, before, after, dodgeReport, units, defs, specs,
                new[] { new BlockPayloadEntry("action.t08.staged.defense", new BlockPayloadSpec(DefenseTagMask.Blockable)) },
                new[] { new GuardPayloadEntry("action.t08.staged.defense", new GuardPayloadSpec(
                    new Dictionary<DamageChannelId, int> { { DamageChannels.PhysicalBlunt, 512 } }, 512, DefenseTagMask.Guardable)) },
                new MomentumClashQuota(InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED, 256, 32640));
        }

        [Test]
        public void UndodgeableRetainsPreDodgeContactOutsideNewArea()
        {
            var r = StagedConflictResolver.Resolve(DefenseContext(ActionType.Dodge, undodgeable: true));
            Assert.That(r.DodgeContacts.Single().Outcome, Is.EqualTo(DodgeContactOutcome.RetainedUndodgeable));
            Assert.That(r.Aggregations.Single().TotalDamageQ10, Is.EqualTo(10 * 1024));
            Assert.That(r.DodgePlans.Single().AvoidedAnyContact, Is.False);
        }
        [Test]
        public void OrdinaryMoveOutsideAreaBeforeDodgeDoesNotCreateTrackingContact()
        {
            var r = StagedConflictResolver.Resolve(DefenseContext(ActionType.Dodge, undodgeable: true, beforeCovered: false));
            Assert.That(r.DodgeContacts, Is.Empty); Assert.That(r.RemainingHits, Is.Empty);
        }
        [Test]
        public void RetainedUndodgeableContactStillObeysClashAndResistanceRules()
        {
            var r = StagedConflictResolver.Resolve(DefenseContext(ActionType.Dodge, undodgeable: true, passive: 512));
            Assert.That(r.Aggregations.Single().TotalDamageQ10, Is.EqualTo(5 * 1024));
            var clash = StagedConflictResolver.Resolve(DefenseContext(ActionType.Dodge, undodgeable: true, attacks: 2, clash: true));
            Assert.That(clash.Clashes.Count, Is.EqualTo(1)); Assert.That(clash.RemainingHits, Is.Empty);
        }
        [Test]
        public void BlockHandlesAllSameTickEligibleContactsIndependentOfOrder()
        {
            var a = StagedConflictResolver.Resolve(DefenseContext(ActionType.Block, attacks: 2));
            var b = StagedConflictResolver.Resolve(DefenseContext(ActionType.Block, attacks: 2, reverse: true));
            Assert.That(a.BlockPlans.Single().Contacts.Count, Is.EqualTo(2));
            Assert.That(a.BlockPlans.Single().Contacts, Is.EqualTo(b.BlockPlans.Single().Contacts));
            Assert.That(a.BlockPlans.Single().Contacts.All(c => c.AfterBlockDamageQ10 == 0 && c.AfterBlockMomentumUnits == 0), Is.True);
        }
        [Test]
        public void GuardPartiallyReducesEligibleDamageAndMomentumOnlyWhileActive()
        {
            var r = StagedConflictResolver.Resolve(DefenseContext(ActionType.Guard));
            Assert.That(r.Aggregations.Single().TotalDamageQ10, Is.EqualTo(5 * 1024));
            Assert.That(r.Aggregations.Single().TotalImpactUnits, Is.EqualTo(50));
            foreach (var bounds in new[] { (12L, 14L), (1L, 11L) })
            {
                var outside = StagedConflictResolver.Resolve(DefenseContext(ActionType.Guard, activeStart: bounds.Item1, activeEnd: bounds.Item2));
                Assert.That(outside.Aggregations.Single().TotalDamageQ10, Is.EqualTo(10 * 1024));
                Assert.That(outside.Aggregations.Single().TotalImpactUnits, Is.EqualTo(100));
            }
        }
        [Test]
        public void GuardDoesNotInvalidateContact()
        {
            var r = StagedConflictResolver.Resolve(DefenseContext(ActionType.Guard));
            Assert.That(r.RemainingHits.Count, Is.EqualTo(1)); Assert.That(r.RemainingHits.Single().IsDirectHitSuppressed, Is.False);
            Assert.That(r.ClashTerminatedPlanIds, Is.Empty);
        }
        [Test]
        public void DefenseEffectsComeFromPlanTickAndPayloadNotStateEligibility()
        {
            var block = StagedConflictResolver.Resolve(DefenseContext(ActionType.Block, eligible: false));
            Assert.That(block.BlockPlans.Single().AnyEligibleContact, Is.True);
            var dodge = StagedConflictResolver.Resolve(DefenseContext(ActionType.Dodge, eligible: false));
            Assert.That(dodge.DodgePlans.Single().AvoidedAnyContact, Is.True);
            var ordinary = StagedConflictResolver.Resolve(DefenseContext(ActionType.Guard, eligible: false));
            Assert.That(ordinary.RemainingHits.Single().IsDirectHitSuppressed, Is.True);
            Assert.That(ordinary.Aggregations.Single().TotalDamageQ10, Is.Zero);
        }
        [Test]
        public void DirectHitEligibilityIsCheckedOnlyAfterSpecialInteractionStages()
        {
            var r = StagedConflictResolver.Resolve(DefenseContext(ActionType.Dodge, coversAfter: true, eligible: false));
            Assert.That(r.DodgeContacts.Single().Outcome, Is.EqualTo(DodgeContactOutcome.StillHit));
            Assert.That(r.RemainingHits.Single().IsDirectHitSuppressed, Is.True);
            Assert.That(r.Aggregations.Single().TotalDamageQ10, Is.Zero);
        }
        [Test]
        public void DodgeContactSetsEventsAndRewardsArePermutationInvariant()
        {
            var a = StagedConflictResolver.Resolve(DefenseContext(ActionType.Dodge, attacks: 2));
            var b = StagedConflictResolver.Resolve(DefenseContext(ActionType.Dodge, attacks: 2, reverse: true));
            Assert.That(a.DodgeContacts.Count, Is.EqualTo(2)); Assert.That(a.DodgeContacts, Is.EqualTo(b.DodgeContacts));
            Assert.That(a.DodgePlans.Single().AvoidedAnyContact, Is.True);
            using var one = Task08DefenseCoverageTests.ReactionRig(); using var two = Task08DefenseCoverageTests.ReactionRig();
            Assert.That(one.Result.Snapshot.ComputeHash(), Is.EqualTo(two.Result.Snapshot.ComputeHash()));
            Assert.That(Task08DefenseCoverageTests.Events<DodgeResolvedEvent>(one).Single().InvalidatedAttackPlanIds,
                Is.EqualTo(Task08DefenseCoverageTests.Events<DodgeResolvedEvent>(two).Single().InvalidatedAttackPlanIds));
            Assert.That(one.Sim.LastAdrenalineAccrualFacts, Is.EqualTo(two.Sim.LastAdrenalineAccrualFacts));
        }

        internal static Task08DefenseCoverageTests.Rig Duel(bool lethal = false, bool clash = true, float heroMass = 10f, float force = 0f)
        {
            var d = Task08DefenseCoverageTests.Definition(health: lethal ? 5 : 2000, force: force > 0 ? force : clash ? 1f : 0.1f);
            d = d with { Units = d.Units.Select((u, i) => i == 0 ? u with { Mass = heroMass } : u).ToArray() };
            var pattern = new AttackPatternSpec(new AttackPatternId("attack.pattern.t08.duel"),
                Enumerable.Range(0, 12).Select(f => new DirectionalTriangleSet((GridDirection)f,
                    clash ? new[] { new TrianglePoint(3, 0, 1), new TrianglePoint(9, 0, 1), new TrianglePoint(-3, 0, 1) }
                        : f == (int)GridDirection.West ? new[] { new TrianglePoint(-3, 0, 1) }
                        : new[] { new TrianglePoint(9, 0, 1) })).ToArray());
            d = d with { Actions = d.Actions.Select(a => a.Type == ActionType.Attack
                    ? a with { Payload = ((AttackPayloadSpec)a.Payload) with { Pattern = pattern } } : a).ToArray() };
            if (lethal) d = d with { Encounters = d.Encounters.Select(e => e with { Slots = e.Slots.Take(2).ToArray() }).ToArray() };
            var rig = new Task08DefenseCoverageTests.Rig { Sim = Task09A2Fixture.NewSim(d) };
            var sim = rig.Sim;
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 400); Task09A2Fixture.Step(sim, 0);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(1, 0,
                sim.CurrentTurnWindow.WindowId, Task09A2Fixture.Hero, new ActionSpecId(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Monster, 6));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1)), Is.Empty);
            rig.Attack = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.CloseWindow(2, sim.CurrentTurnWindow.WindowId));
            Task09A2Fixture.Step(sim, 2);
            sim.WindowManager.ScheduleWindow(3, Task09A2Fixture.Monster, 400); Task09A2Fixture.Step(sim, 3);
            var add = new AddOrdinaryPlanOperation(1, Task09A2Fixture.Monster, new ActionSpecId(Task09A2Fixture.AttackSpecId),
                6, default, Task09A2Fixture.Hero, GridDirection.West, null);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, new CommandRequest(4,
                new ScheduleEditScope(sim.ScheduleAuthority.ScheduleRevision, sim.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[] { add })));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 4)), Is.Empty);
            rig.Reaction = sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.OwnerUnitId == Task09A2Fixture.Monster);
            Task09A2Fixture.AdvanceTo(sim, 15); rig.Result = Task09A2Fixture.StepNext(sim);
            return rig;
        }

        [TestCase(100, 1L, 1L)] [TestCase(200, 4L, 2L)]
        public void DodgeAgainstMultipleAttacksGrantsOneStableCounterWindow(int otherMomentum, long targetUnit, long targetPlan)
        {
            var a = StagedConflictResolver.Resolve(DefenseContext(ActionType.Dodge, attacks: 3, otherMomentum: otherMomentum));
            var b = StagedConflictResolver.Resolve(DefenseContext(ActionType.Dodge, attacks: 3, reverse: true, otherMomentum: otherMomentum));
            var window = a.CounterWindows.Single();
            Assert.That(window.CounterTargetUnitId, Is.EqualTo(new UnitId(targetUnit)));
            Assert.That(window.CounterTargetPlanId, Is.EqualTo(new ActionPlanId(targetPlan)));
            Assert.That(window.AvoidedAttackPlanIds, Is.EqualTo(new[] { new ActionPlanId(1), new ActionPlanId(2), new ActionPlanId(3) }));
            Assert.That(b.CounterWindows.Single().CounterTargetPlanId, Is.EqualTo(window.CounterTargetPlanId));
            Assert.That(StagedConflictResolver.Resolve(DefenseContext(ActionType.Dodge, coversAfter: true)).CounterWindows, Is.Empty);
            using var r = Task08DefenseCoverageTests.ReactionRig();
            Assert.That(r.Result.Events.Events.OfType<DodgeCounterWindowOpenedEvent>().Count(), Is.EqualTo(1));
            Assert.That(r.Result.Events.Events.OfType<DodgeCounterWindowOpenedEvent>().Single().CounterTargetPlanId, Is.EqualTo(r.Attack.ActionPlanId));
        }

        [Test]
        public void ActionTerminalResolutionUsesUnifiedLifecycleEntry()
        {
            using var r = Duel();
            Assert.That(r.Sim.StagedResolution.ClashTerminatedPlanIds.Count, Is.EqualTo(2));
            Assert.That(r.Attack.TerminationReason, Is.EqualTo(ActionTerminationReason.InterruptedByClash));
            Assert.That(r.Reaction.TerminationReason, Is.EqualTo(ActionTerminationReason.InterruptedByClash));
            Assert.That(r.Result.Events.Events.OfType<ActionPlanTerminatedEvent>().Count(), Is.EqualTo(2));
            Assert.That(r.Sim.ScheduleAuthority.Registry.ActivePlans, Is.Empty);
        }

        [Test]
        public void ClashResidualReachesHealthWithoutReapplyingBaseDamage()
        {
            using var r = Duel(heroMass: 20);
            Assert.That(r.Sim.StagedResolution.Clashes.Single().Clash.TotalRemainingMomentumUnits, Is.EqualTo(10000));
            Assert.That(r.Sim.StagedResolution.RemainingHits.Single().ContactKind, Is.EqualTo(TargetContactType.ClashResidualImpact));
            Assert.That(r.Sim.LastDamageCommitReport.Units.Single().DamageQ10, Is.EqualTo(10240));
            Assert.That(r.Sim.StagedResolution.Aggregations.Single().Contacts.Single().Key.TargetActionPlanId,
                Is.EqualTo(r.Reaction.ActionPlanId.Value));
            Assert.That(r.Result.Events.Events.OfType<DamageChannelResolvedEvent>().Single().ParticipatingPlanIds,
                Is.EqualTo(new[] { r.Attack.ActionPlanId, r.Reaction.ActionPlanId }));
            Assert.That(Task08DefenseCoverageTests.Health(r.Sim, Task09A2Fixture.Monster), Is.EqualTo(1990 * 1024));
            Assert.That(r.Attack.TerminationReason, Is.EqualTo(ActionTerminationReason.InterruptedByClash));
            Assert.That(r.Reaction.TerminationReason, Is.EqualTo(ActionTerminationReason.InterruptedByClash));
        }
        [Test]
        public void ClashTerminationRemovesUnfrozenAndFutureIntents()
        {
            using var r = Duel();
            Assert.That(r.Sim.IntentQueue.Intents.Count, Is.EqualTo(2), "Already frozen current impacts remain auditable");
            Assert.That(r.Sim.ScheduleAuthority.Registry.ActivePlans, Is.Empty);
            Task09A2Fixture.StepNext(r.Sim); Assert.That(r.Sim.IntentQueue.Intents, Is.Empty);
        }
        [Test]
        public void SimultaneousLethalHitsAllowMutualDeath()
        {
            using var r = Duel(lethal: true, clash: false);
            Assert.That(r.Result.Events.Events.OfType<UnitDiedEvent>().Select(e => e.UnitId),
                Is.EqualTo(new[] { Task09A2Fixture.Hero, Task09A2Fixture.Monster }));
            Assert.That(r.Sim.BattleEnd.IsEnded, Is.True);
            Assert.That(r.Sim.BattleEnd.ResultCode, Is.EqualTo("result.t09a2.draw"));
        }
        [Test]
        public void InterruptedPlanLeavesLaneGapAndDoesNotRefundBudget()
        {
            using var r = Duel();
            Assert.That(r.Attack.IsTerminal && r.Reaction.IsTerminal, Is.True);
            Assert.That(r.Sim.WindowManager.FindWindow(r.Reaction.SubmittedWindowId.Value).SpentBudgetTicks, Is.GreaterThan(0));
            Assert.That(r.Sim.ScheduleAuthority.FindLane(Task09A2Fixture.Monster).Plans, Is.Empty);
        }
        [Test]
        public void ResolutionCommitLeavesNoActiveArtifactReferencingTerminalPlan()
        {
            using var r = Duel();
            Assert.That(r.Sim.ScheduleAuthority.Registry.ActivePlans, Is.Empty);
            Assert.That(r.Sim.DodgeRelocation.CaptureSpaceSnapshot(r.Sim.Tick).MovementReservations, Is.Empty);
            Assert.That(r.Sim.DodgeRelocation.CaptureSpaceSnapshot(r.Sim.Tick).Segments, Is.Empty);
            Assert.That(r.Sim.DodgeRelocation.CaptureSpaceSnapshot(r.Sim.Tick).DodgeReservations, Is.Empty);
            Assert.That(r.Sim.CurrentSnapshot.MovementSegments, Is.Empty);
        }
        [Test]
        public void TargetDamageAndImpulseCommitOnceInStableOrder()
        {
            using var r = Task08DefenseCoverageTests.ReactionRig(definition: Task08DefenseCoverageTests.Definition(area: true, coversDestination: true));
            var units = r.Sim.LastDamageCommitReport.Units;
            Assert.That(units.Select(u => u.UnitId.Value), Is.EqualTo(new long[] { 2, 5 }));
            Assert.That(units.Select(u => u.DamageQ10), Is.EqualTo(new long[] { 10240, 10240 }));
            Assert.That(units.Select(u => u.UnitId).Distinct().Count(), Is.EqualTo(units.Count));
        }
        [Test]
        public void AdrenalineAccrualUsesCommittedFinalDamageAggregatedOncePerUnit()
        {
            using var r = Task08DefenseCoverageTests.ReactionRig(definition: Task08DefenseCoverageTests.Definition(area: true, coversDestination: true));
            var facts = r.Sim.LastAdrenalineAccrualFacts;
            Assert.That(facts.Select(f => f.UnitId).Distinct().Count(), Is.EqualTo(facts.Count));
            Assert.That(facts.Single(f => f.UnitId == Task09A2Fixture.Hero).TotalFinalDamageDealtQ10, Is.EqualTo(20480));
            Assert.That(facts.Single(f => f.UnitId == Task09A2Fixture.Monster).TotalFinalDamageReceivedQ10, Is.EqualTo(10240));
            Assert.That(facts.Single(f => f.UnitId == Task09A2Fixture.Monster).SuccessfulDodgeCount, Is.Zero);
        }
        [Test]
        public void ClashAndSuccessfulReactionAccrualKeysCannotDuplicate()
        {
            using var clash = Duel();
            Assert.That(clash.Sim.LastAdrenalineAccrualFacts.Count(f => f.ClashSuccessCount == 1), Is.EqualTo(2));
            Assert.That(clash.Sim.LastAdrenalineAccrualFacts.All(f => f.ClashSuccessCount <= 1), Is.True);
            using var dodge = Task08DefenseCoverageTests.ReactionRig();
            Assert.That(dodge.Sim.LastAdrenalineAccrualFacts.Single(f => f.UnitId == Task09A2Fixture.Monster).SuccessfulDodgeCount, Is.EqualTo(1));
        }
    }
}
