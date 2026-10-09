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
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Tests
{
    public class Task0108LifecycleRegressionTests
    {
        private sealed class StateInput : IUnitStateAdvanceSystem
        {
            public long AtTick;
            public UnitId Unit = Task09A2Fixture.Monster;
            public bool Kill;
            public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(IReadOnlyList<UnitSnapshot> units, long tick)
                => tick != AtTick ? Array.Empty<UnitStateAdvanceRequest>()
                    : new[] { new UnitStateAdvanceRequest(Unit.Value, Kill ? (int?)0 : null,
                        Kill ? null : StateTransitionSpec.Timed(UnitState.Staggered, 30, UnitState.Idle)) };
        }

        private static void Advance(BattleSimulation sim, long tick)
        {
            while (sim.Tick < tick) Task09A2Fixture.StepNext(sim);
        }

        private static ActionPlan AcceptReaction(BattleSimulation sim, ReactionCommandKind kind,
            out ReactionOpportunityRuntime opportunity, GridPoint? destination = null)
        {
            Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            opportunity = Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);
            string id = kind == ReactionCommandKind.Block ? Task09A2Fixture.BlockSpecId : Task09A2Fixture.DodgeSpecId;
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                Task09A2Fixture.Reaction(2, opportunity.Id, kind, new ActionSpecId(id), destination));
            var result = Task09A2Fixture.Step(sim, 2);
            Assert.That(Task09A2Fixture.AllRejectionCodes(result), Is.Empty);
            return sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.IsReaction);
        }

        [Test]
        public void BlockStartsConsumesAtTriggerAndCompletesThroughProductionStep()
        {
            using var sim = Task09A2Fixture.NewSim();
            ActionPlan block = AcceptReaction(sim, ReactionCommandKind.Block, out var opportunity);
            Advance(sim, block.StartTick);
            Assert.That(block.IsRunning, Is.True);
            Assert.That(sim.AdrenalineLedgerOf(block.OwnerUnitId).ReservationCount, Is.EqualTo(1));
            Advance(sim, block.TriggerTick);
            Assert.That(opportunity.State, Is.EqualTo(ReactionOpportunityState.Triggered));
            Assert.That(sim.AdrenalineLedgerOf(block.OwnerUnitId).ReservationCount, Is.Zero);
            Assert.That(sim.StagedResolution.BlockPlans.Count, Is.EqualTo(1));
            Advance(sim, block.EndTick);
            Assert.That(block.IsCompleted, Is.True);
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Any(p => p.ActionPlanId == block.ActionPlanId), Is.False);
        }

        [Test]
        public void ControlInvalidatesLockedReactionAsInterruptedByControl()
        {
            using var sim = Task09A2Fixture.NewSim(assembly: new BattleSimulationAssembly(
                unitStateAdvance: new StateInput { AtTick = 9 }));
            ActionPlan block = AcceptReaction(sim, ReactionCommandKind.Block, out _);
            int available = sim.AdrenalineLedgerOf(block.OwnerUnitId).AvailableAdrenaline;
            Advance(sim, block.TriggerTick);
            Assert.That(block.TerminationReason, Is.EqualTo(ActionTerminationReason.InterruptedByControl));
            Assert.That(sim.StagedResolution.BlockPlans, Is.Empty);
            Assert.That(sim.AdrenalineLedgerOf(block.OwnerUnitId).ReservationCount, Is.Zero);
            Assert.That(sim.AdrenalineLedgerOf(block.OwnerUnitId).AvailableAdrenaline, Is.EqualTo(available));
        }

        [Test]
        public void RunningReactionInterruptedBeforeTriggerNeverBlocksOrRefunds()
        {
            using var sim = Task09A2Fixture.NewSim(assembly: new BattleSimulationAssembly(
                unitStateAdvance: new StateInput { AtTick = 11 }));
            ActionPlan block = AcceptReaction(sim, ReactionCommandKind.Block, out _);
            Advance(sim, 10);
            Assert.That(block.IsRunning, Is.True);
            Advance(sim, 11);
            Assert.That(block.TerminationReason, Is.EqualTo(ActionTerminationReason.InterruptedByControl));
            Assert.That(sim.StagedResolution.BlockPlans, Is.Empty);
            Assert.That(sim.AdrenalineLedgerOf(block.OwnerUnitId).ReservationCount, Is.Zero);
        }

        [Test]
        public void SuccessfulDodgeConsumesAndCompletesWithoutCancellingSourceAttack()
        {
            using var sim = Task09A2Fixture.NewSim();
            ActionPlan dodge = AcceptReaction(sim, ReactionCommandKind.Dodge, out var opportunity, new GridPoint(6, 2));
            Advance(sim, dodge.TriggerTick);
            Assert.That(opportunity.State, Is.EqualTo(ReactionOpportunityState.Triggered));
            Assert.That(sim.AdrenalineLedgerOf(dodge.OwnerUnitId).ReservationCount, Is.Zero);
            Assert.That(sim.LogicGrid.TryGetAnchor(dodge.OwnerUnitId, out var anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(6, 2)));
            Assert.That(sim.ScheduleAuthority.Registry.Find(dodge.SourceThreatPlanId.Value).IsRunning, Is.True);
            Advance(sim, dodge.EndTick);
            Assert.That(dodge.IsCompleted, Is.True);
        }

        [Test]
        public void ResolutionControlIsCommittedAfterDamageAndExpiresAtConfiguredTick()
        {
            using var sim = Task09A2Fixture.NewSim();
            Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);
            Advance(sim, 11);
            var aggregate = sim.StagedResolution.Aggregations.Single(a => a.TargetUnitId == Task09A2Fixture.Monster);
            Assert.That(aggregate.IsStaggered, Is.True, "Fixture must actually produce control");
            var state = sim.FindUnitStateMachine(Task09A2Fixture.Monster);
            Assert.That(state.CurrentState, Is.EqualTo(UnitState.Staggered));
            Assert.That(state.StateEndTick, Is.EqualTo(11 + sim.Definition.Rules.StaggerAutoRecoveryTicks));
            Advance(sim, state.StateEndTick);
            Assert.That(state.CurrentState, Is.EqualTo(UnitState.Idle));
        }

        [Test]
        public void PreCommandDeathWithoutVictoryCannotOpenWindowOrAcceptDeadOwnersCommand()
        {
            var definition = Task09A2Fixture.BuildDefinition(withFragileHostile: true);
            using var sim = Task09A2Fixture.NewSim(definition, new BattleSimulationAssembly(
                unitStateAdvance: new StateInput { AtTick = 1, Kill = true }));
            sim.WindowManager.ScheduleWindow(1, Task09A2Fixture.Monster, 400);
            Task09A2Fixture.Step(sim, 0);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.AddPlan(1, 0, new WindowId(1),
                Task09A2Fixture.Monster, new ActionSpecId(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Hero, 1));
            var result = Task09A2Fixture.Step(sim, 1);
            Assert.That(sim.BattleEnd.IsEnded, Is.False);
            Assert.That(result.Events.Events.OfType<TurnWindowOpenedEvent>()
                .Any(e => e.OwnerUnitId == Task09A2Fixture.Monster), Is.False);
            Assert.That(Task09A2Fixture.AllRejectionCodes(result), Is.Not.Empty);
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Any(p => p.OwnerUnitId == Task09A2Fixture.Monster), Is.False);
        }

        [Test]
        public void DeathTerminatesOwnersPlansBeforeReturningTheSameStep()
        {
            using var sim = Task09A2Fixture.NewSim(assembly: new BattleSimulationAssembly(
                unitStateAdvance: new StateInput { AtTick = 2, Unit = Task09A2Fixture.Hero, Kill = true }));
            var attack = Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);
            Task09A2Fixture.Step(sim, 2);
            Assert.That(attack.TerminationReason, Is.EqualTo(ActionTerminationReason.OwnerDied));
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans, Is.Empty);
        }

        [Test]
        public void AttackPhaseStateAdvancesFromWindupThroughRecoveryToIdle()
        {
            using var sim = Task09A2Fixture.NewSim();
            var attack = Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);
            var state = sim.FindUnitStateMachine(Task09A2Fixture.Hero);
            Assert.That(state.CurrentState, Is.EqualTo(UnitState.Windup));
            Advance(sim, attack.ImpactTick);
            Assert.That(state.CurrentState, Is.EqualTo(UnitState.Recovery));
            Assert.That(sim.IntentQueue.Intents.Any(i => i.ActionPlanId == attack.ActionPlanId), Is.True);
            Advance(sim, attack.EndTick);
            Assert.That(state.CurrentState, Is.EqualTo(UnitState.Idle));
        }

        [Test]
        public void AttackAreaAndMomentumUseFrozenPlanFacing()
        {
            var definition = Task09A2Fixture.BuildDefinition();
            var directions = Enumerable.Range(0, GridDirectionInfo.DirectionCount).Select(f =>
                new DirectionalTriangleSet((GridDirection)f, f == (int)GridDirection.West
                    ? new[] { new TrianglePoint(-3, 0, 1) } : new[] { new TrianglePoint(9, 0, 1) })).ToArray();
            var pattern = new AttackPatternSpec(new AttackPatternId("attack.audit.asymmetric"), directions);
            definition = definition with { Actions = definition.Actions.Select(a => a.Type == ActionType.Attack
                ? a with { Payload = ((AttackPayloadSpec)a.Payload) with { Pattern = pattern } } : a).ToArray() };
            using var sim = Task09A2Fixture.NewSim(definition);
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 400);
            Task09A2Fixture.Step(sim, 0);
            var add = new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero,
                new ActionSpecId(Task09A2Fixture.AttackSpecId), 1, default, Task09A2Fixture.Monster,
                GridDirection.West, null);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, new CommandRequest(1,
                new ScheduleEditScope(0, sim.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[] { add })));
            Task09A2Fixture.Step(sim, 1);
            Advance(sim, 11);
            var intent = sim.IntentQueue.Intents.Single();
            Assert.That(intent.Facing, Is.EqualTo(GridDirection.West));
            Assert.That(intent.Momentum.Direction, Is.EqualTo(GridDirection.West));
            Assert.That(intent.AreaPoints, Is.EqualTo(new[] { new TrianglePoint(-3, 0, 1) }));
            Assert.That(sim.LastDamageCommitReport.Units, Is.Empty, "East target must not be hit by West pattern");
        }

        [Test]
        public void FailedDodgeAtTriggerReleasesItsReservationWithoutTriggerOrAvoidance()
        {
            using var sim = Task09A2Fixture.NewSim();
            var dodge = AcceptReaction(sim, ReactionCommandKind.Dodge, out var opportunity, new GridPoint(6, 2));
            Advance(sim, 10);
            // Arrange a late occupied destination through the real grid primitive, without replacing Dodge code.
            Assert.That(sim.LogicGrid.CommitAnchor(Task09A2Fixture.Neutral, new GridPoint(6, 2), GridDirection.East), Is.Null);
            var result = Task09A2Fixture.StepNext(sim);
            Assert.That(dodge.TerminationReason, Is.EqualTo(ActionTerminationReason.TargetInvalid));
            Assert.That(sim.AdrenalineLedgerOf(dodge.OwnerUnitId).ReservationCount, Is.Zero);
            Assert.That(opportunity.State, Is.Not.EqualTo(ReactionOpportunityState.Triggered));
            Assert.That(result.Events.Events.OfType<ReactionTriggeredEvent>(), Is.Empty);
            Assert.That(sim.LastDodgeCommitReport.Results.Single().Committed, Is.False);
            Assert.That(sim.LastDodgeCommitReport.Results.Single().Moved, Is.False,
                "Dodge失败不得换位；之后的攻击强制位移由另一阶段处理");
        }

        [Test]
        public void BlockWithoutContactsStillConsumesAtTriggerAndDoesNotEarnSuccessReward()
        {
            var definition = Task09A2Fixture.BuildDefinition();
            var farPattern = new AttackPatternSpec(new AttackPatternId("attack.audit.far"),
                Enumerable.Range(0, 12).Select(i => new DirectionalTriangleSet((GridDirection)i,
                    new[] { new TrianglePoint(-31, 0, 1) })).ToArray());
            definition = definition with { Actions = definition.Actions.Select(a => a.Type == ActionType.Attack
                ? a with { Payload = ((AttackPayloadSpec)a.Payload) with { Pattern = farPattern } } : a).ToArray() };
            using var sim = Task09A2Fixture.NewSim(definition);
            var block = AcceptReaction(sim, ReactionCommandKind.Block, out var opportunity);
            int available = sim.AdrenalineLedgerOf(block.OwnerUnitId).AvailableAdrenaline;
            Advance(sim, block.TriggerTick);
            Assert.That(sim.StagedResolution.BlockPlans, Is.Empty);
            Assert.That(opportunity.State, Is.EqualTo(ReactionOpportunityState.Triggered));
            Assert.That(sim.AdrenalineLedgerOf(block.OwnerUnitId).ReservationCount, Is.Zero);
            Assert.That(sim.AdrenalineLedgerOf(block.OwnerUnitId).AvailableAdrenaline, Is.EqualTo(available));
            Advance(sim, block.EndTick);
            Assert.That(block.IsCompleted, Is.True);
        }

        [Test]
        public void ZeroWindupOrRecoveryReactionIsNotOfferedOrReserved()
        {
            var definition = Task09A2Fixture.BuildDefinition();
            definition = definition with { Actions = definition.Actions.Select(a => a.Type == ActionType.Block
                ? a with { Timing = new BlockReactionTimingSpec(0, 0) } : a).ToArray() };
            using var sim = Task09A2Fixture.NewSim(definition);
            Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            var opportunity = Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);
            Assert.That(opportunity.Options.Any(o => o.ReactionActionSpecId.Value == Task09A2Fixture.BlockSpecId), Is.False);
            Assert.That(sim.AdrenalineLedgerOf(Task09A2Fixture.Monster).ReservationCount, Is.Zero);
        }

        [Test]
        public void BatchRelocationCannotMistakePlanIdForReservationOwnerUnitId()
        {
            var grid = new LogicGrid(new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40)));
            Assert.That(grid.RegisterUnitWithPointFootprint(new UnitId(1), new GridPoint(0, 0), GridDirection.East), Is.Null);
            Assert.That(grid.RegisterUnitWithPointFootprint(new UnitId(2), new GridPoint(30, 0), GridDirection.East), Is.Null);
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(new ActionPlanId(1), 0), new UnitId(2),
                new GridPoint(6, 0), 0, 50)), Is.Null);
            Assert.That(grid.ApplyBatchRelocation(new[] { new BatchRelocation(new UnitId(1), new GridPoint(0, 0),
                new GridPoint(6, 0)) }), Does.StartWith(LogicGridCodes.LOGIC_GRID_BATCH_RESERVATION_NOT_CLEARED));
            Assert.That(grid.TryGetAnchor(new UnitId(1), out var anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(0, 0)));
        }

        [Test]
        public void KnockdownUsesConfiguredDurationAndInterruptsRunningVictimPlan()
        {
            var definition = Task09A2Fixture.BuildDefinition();
            definition = definition with { Units = definition.Units.Select((u, i) => i == 0 ? u with { Mass = 20f } : u).ToArray() };
            using var sim = Task09A2Fixture.NewSim(definition);
            Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);
            Advance(sim, 11);
            Assert.That(sim.StagedResolution.Aggregations.Single(a => a.TargetUnitId == Task09A2Fixture.Monster).IsKnockedDown, Is.True);
            var machine = sim.FindUnitStateMachine(Task09A2Fixture.Monster);
            Assert.That(machine.CurrentState, Is.EqualTo(UnitState.KnockedDown));
            Assert.That(machine.StateEndTick, Is.EqualTo(11 + definition.Rules.KnockdownAutoRecoveryTicks));
        }

        private static BattleDefinition AddSpec(BattleDefinition definition, ActionSpec spec)
            => definition with { Actions = definition.Actions.Concat(new[] { spec }).ToArray(),
                ActionSets = definition.ActionSets.Select(s => s with {
                    ActionSpecIds = s.ActionSpecIds.Concat(new[] { spec.ActionSpecId }).ToArray() }).ToArray() };

        [Test]
        public void GuardPhaseUsesItsActiveAndRecoveryBoundaries()
        {
            var guardId = new ActionSpecId("action.audit.guard");
            var definition = AddSpec(Task09A2Fixture.BuildDefinition(), new ActionSpec(guardId, ActionType.Guard,
                new GuardTimingSpec(2, 3, 2), new GuardPayloadSpec(
                    new Dictionary<DamageChannelId, int> { { DamageChannels.PhysicalBlunt, 512 } }, 512, DefenseTagMask.Guardable), 0));
            using var sim = Task09A2Fixture.NewSim(definition);
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Hero, 400);
            Task09A2Fixture.Step(sim, 0);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(1, 0,
                sim.CurrentTurnWindow.WindowId, Task09A2Fixture.Hero, guardId, null, 1));
            Task09A2Fixture.Step(sim, 1);
            var state = sim.FindUnitStateMachine(Task09A2Fixture.Hero);
            Assert.That(state.CurrentState, Is.EqualTo(UnitState.Windup));
            Advance(sim, 3); Assert.That(state.CurrentState, Is.EqualTo(UnitState.Guarding));
            Advance(sim, 6); Assert.That(state.CurrentState, Is.EqualTo(UnitState.Recovery));
            Advance(sim, 8); Assert.That(state.CurrentState, Is.EqualTo(UnitState.Idle));
        }

        [Test]
        public void InterceptedMovementReleasesReservationAndNeverCommitsFormerDestination()
        {
            var definition = Task09A2Fixture.BuildDefinition();
            var westArea = new AttackPatternSpec(new AttackPatternId("attack.audit.backward"),
                Enumerable.Range(0, 12).Select(i => new DirectionalTriangleSet((GridDirection)i,
                    new[] { new TrianglePoint(-3, 0, 1) })).ToArray());
            definition = definition with { Actions = definition.Actions.Select(a => a.Type == ActionType.Attack
                ? a with { Payload = ((AttackPayloadSpec)a.Payload) with { Pattern = westArea, ForceMultiplier = 0.1f } } : a).ToArray() };
            var moveId = new ActionSpecId("action.audit.move");
            definition = AddSpec(definition, new ActionSpec(moveId, ActionType.Move,
                new MoveTimingSpec(20, 1), new MovePayloadSpec(200, definition.MovementPatterns[0]), 0));
            using var sim = Task09A2Fixture.NewSimWithMeta(1, definition,
                new BattleSimulationAssembly(concurrentHeroUnitId: Task09A2Fixture.Hero));
            Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Hero,
                new ActionSpecId(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Monster, false);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, new CommandRequest(2,
                new WindowCommandScope(sim.CurrentTurnWindow.WindowId), new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction)));
            Task09A2Fixture.Step(sim, 2);
            var add = new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero, moveId, 3, default,
                null, GridDirection.East, new GridPoint(0, 2));
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, new CommandRequest(3,
                new ScheduleEditScope(sim.ScheduleAuthority.ScheduleRevision, sim.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[] { add })));
            var accepted = Task09A2Fixture.Step(sim, 3);
            Assert.That(Task09A2Fixture.AllRejectionCodes(accepted), Is.Empty);
            var move = sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.ActionType == ActionType.Move);
            Assert.That(sim.FindUnitStateMachine(Task09A2Fixture.Hero).CurrentState, Is.EqualTo(UnitState.Moving));
            Advance(sim, 11);
            Assert.That(sim.StagedResolution.Moves.Single().Outcome, Is.EqualTo(MoveContactOutcome.Intercepted));
            Assert.That(move.TerminationReason, Is.EqualTo(ActionTerminationReason.InterruptedByIntercept));
            Assert.That(sim.LogicGrid.ReservationsOfPlanOrdered(move.ActionPlanId), Is.Empty);
            Advance(sim, 45);
            Assert.That(sim.LogicGrid.TryGetAnchor(move.OwnerUnitId, out var anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(0, 0)));
        }

        [Test]
        public void BattleEndClosesWindowRevokesAuthorityAndClearsFutureScheduleWithoutRefund()
        {
            using var sim = Task09A2Fixture.NewSimWithMeta(1, assembly: new BattleSimulationAssembly(
                concurrentHeroUnitId: Task09A2Fixture.Hero,
                unitStateAdvance: new StateInput { AtTick = 3, Kill = true }));
            sim.WindowManager.ScheduleWindow(0, Task09A2Fixture.Monster, 400);
            Task09A2Fixture.Step(sim, 0);
            var window = sim.CurrentTurnWindow;
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, new CommandRequest(1,
                new WindowCommandScope(window.WindowId), new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction)));
            Task09A2Fixture.Step(sim, 1);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(2,
                sim.ScheduleAuthority.ScheduleRevision, window.WindowId, Task09A2Fixture.Hero,
                new ActionSpecId(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Monster, 2));
            Task09A2Fixture.Step(sim, 2);
            int spent = window.SpentBudgetTicks;
            Assert.That(spent, Is.GreaterThan(0));
            sim.WindowManager.ScheduleWindow(100, Task09A2Fixture.Hero, 400);
            Assert.That(sim.WindowManager.ScheduledWindowCount, Is.EqualTo(1), "先安排真实未来窗口再检查Finalizer清除");
            Task09A2Fixture.Step(sim, 3);
            Assert.That(sim.BattleEnd.IsEnded, Is.True);
            Assert.That(window.IsOpen, Is.False);
            Assert.That(window.CloseReason, Is.EqualTo(TurnWindowCloseReason.BattleEnded));
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(spent));
            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(sim.WindowManager.ScheduledWindowCount, Is.Zero);
            Assert.That(sim.CurrentSnapshot.ConcurrentAction.HasActiveAuthorization, Is.False);
            Assert.That(sim.CurrentSnapshot.Resources.MetaResource, Is.Zero);
        }
    }
}
