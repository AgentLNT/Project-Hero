using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Units;

// These internal copy seams are exclusively for the Logic read port. They never restore a live
// battle, expose a simulation to View/AI, or copy ingresses, AI state, hidden plans or RNG state.
namespace ProjectHero.Logic.Units
{
    public sealed partial class UnitStateMachine
    {
        internal UnitStateMachine CopyForHud(LogicEventOutbox outbox) => new UnitStateMachine(_unitId, outbox)
        { CurrentState = CurrentState, PreviousState = PreviousState, StateStartTick = StateStartTick,
            StateEndTick = StateEndTick, NextOnExpire = NextOnExpire };
    }
}
namespace ProjectHero.Logic.Resources
{
    public sealed partial class AdrenalineLedger
    {
        internal void CopyPublicFactsTo(AdrenalineLedger target)
        {
            target.AvailableAdrenaline = AvailableAdrenaline; target.CycleId = CycleId;
            foreach (var r in _reservations) target._reservations.Add(r.Key, new ReactionReservation(r.Value.Amount, r.Value.CycleId));
        }
    }
}
namespace ProjectHero.Logic.Status
{
    public sealed partial class BuffSystem
    {
        internal void CopyPublicEffectsTo(BuffSystem target)
        {
            // Active effect instances/specifications are immutable public combat facts.
            foreach (var pair in _effectsById) target._effectsById.Add(pair.Key, pair.Value);
            foreach (var pair in _effectsByUnit) target._effectsByUnit.Add(pair.Key, new List<StatusEffectInstance>(pair.Value));
        }
    }
}
namespace ProjectHero.Logic.Movement
{
    public sealed partial class LogicGridMovementAuthority
    {
        internal void CopyVisiblePathsTo(LogicGridMovementAuthority target, HashSet<long> visible)
        {
            foreach (var segment in AllSegmentsOrdered())
                if (visible.Contains(segment.ActionPlanId.Value)) target._segments.Add(new ReservationKey(segment.ActionPlanId, segment.StepIndex), segment);
            foreach (var reservation in _grid.AllReservationsOrdered())
                if (visible.Contains(reservation.ActionPlanId.Value))
                {
                    string error = target._grid.TryReserve(reservation);
                    if (error != null) throw new LogicDefinitionException(error);
                }
        }
    }
    public sealed partial class DodgeRelocationAuthority
    {
        internal void CopyVisibleDestinationsTo(DodgeRelocationAuthority target, HashSet<long> visible)
        {
            foreach (var pair in _reservations)
                if (visible.Contains(pair.Value.ReactionPlanId.Value)) target._reservations.Add(pair.Key, pair.Value);
        }
    }
}
namespace ProjectHero.Logic.Timeline
{
    public sealed partial class ReactionOpportunitySystem
    {
        internal IReadOnlyList<UnitId> HudTargets(ActionPlan sourcePlan)
            => _definition.FindAction(sourcePlan.ActionSpecId)?.Payload is AttackPayloadSpec attack
                ? CollectDefenders(sourcePlan, attack, sourcePlan.StartTick) : Array.Empty<UnitId>();

        internal void CopyVisibleOpportunitiesTo(ReactionOpportunitySystem target, HashSet<long> visible)
        {
            foreach (var source in _active)
            {
                if (!visible.Contains(source.SourceAttackPlanId.Value) && !visible.Contains(source.BoundActionPlanId.Value)) continue;
                var options = source.Options.Select(o => new ReactionOptionRuntime(o.Option, o.IsPublished)
                    { IsOpen = o.IsOpen, OutcomeCode = o.OutcomeCode }).ToList();
                target._active.Add(new ReactionOpportunityRuntime(source.Id, source.SourceAttackPlanId,
                    source.DefenderUnitId, source.TelegraphTick, source.SourceImpactTick, options)
                { State = source.State, CloseReason = source.CloseReason, ClosedAtTick = source.ClosedAtTick,
                    BoundActionPlanId = source.BoundActionPlanId, AcceptedActionSpecId = source.AcceptedActionSpecId });
            }
        }
    }
}
namespace ProjectHero.Logic.Turns
{
    public sealed partial class ConcurrentActionSystem
    {
        internal void CopyPublicAuthorizationTo(ConcurrentActionSystem target)
        { target.HeroUnitId = HeroUnitId; target.IsActive = IsActive;
            target.ActiveWindowId = ActiveWindowId; target.ActivePlayerUnitId = ActivePlayerUnitId; }
    }
    public sealed partial class TurnWindowManager
    {
        internal void CopyPublicWindowsTo(TurnWindowManager target)
        {
            TurnWindow Copy(TurnWindow w)
            {
                var copy = new TurnWindow(w.WindowId, w.OwnerUnitId, w.OpenedAtTick, w.DurationTicks);
                if (!w.IsAcceptingSubmissions) copy.RequestClose(w.CloseReason.Value);
                if (!w.IsOpen) copy.FinalizeClose();
                return copy;
            }
            target.CurrentWindow = CurrentWindow == null ? null : Copy(CurrentWindow);
            target.LastClosedWindowId = LastClosedWindowId; target.LastClosedAtTick = LastClosedAtTick;
            target.LastClosedOwnerUnitId = LastClosedOwnerUnitId; target._nextWindowOrdinal = _nextWindowOrdinal;
            foreach (var pair in _closedIndex) target._closedIndex.Add(pair.Key, Copy(pair.Value));
            foreach (var w in _closedWindows) target._closedWindows.Add(target._closedIndex[w.WindowId.Value]);
            foreach (var pair in _futureSchedule) target._futureSchedule.Add(pair.Key, new List<ScheduledTurnWindowRequest>(pair.Value));
            target.RefreshNextWindowTick();
        }
    }
}
namespace ProjectHero.Logic.Simulation
{
    public sealed partial class BattleSimulation
    {
        internal void CloseHudScheduleTransaction(ProjectHero.Logic.Timeline.ScheduleEditTransactionResult result, long tick)
        { TerminateRemovedPlans(result, tick); SyncScheduleRevision(); }
        private LogicSnapshot _hudCacheSnapshot;
        private readonly Dictionary<ControllerId, HudPreviewService> _hudServices = new Dictionary<ControllerId, HudPreviewService>();

        /// <summary>Read port, available only at committed boundaries. No Step system calls this port.</summary>
        public HudPreviewService HudPreviewFor(ControllerId controller)
        {
            if (_eventTick != _tick || (_tick >= 0 && _currentSnapshot.Tick != _tick)) throw new InvalidOperationException("HUD_REQUIRES_COMMITTED_SNAPSHOT");
            if (!ReferenceEquals(_hudCacheSnapshot, _currentSnapshot))
            { _hudServices.Clear(); _hudCacheSnapshot = _currentSnapshot; }
            if (!_hudServices.TryGetValue(controller, out var service))
            {
                if (string.IsNullOrEmpty(controller.Value)) throw new ArgumentException("HUD requires a controller");
                var decision = DecisionSnapshotFor(controller);
                var visible = new HashSet<long>(decision.VisiblePlans.Concat(decision.OwnPlans).Select(p => p.ActionPlanId));
                // Freeze the sanitized input once. Later queries on this service remain about this boundary.
                var template = CopyHudWorld(visible);
                service = new HudPreviewService(decision, template, () => template.CopyHudWorld(visible));
                _hudServices.Add(controller, service);
            }
            return service;
        }

        private BattleSimulation CopyHudWorld(HashSet<long> visible)
        {
            var inputs = new BattleRuntimeInputs(0, 0);
            ProductionBattleComposition composition = _assembly.TurnWindowSchedule is ProductionBattleComposition
                ? new ProductionBattleComposition(_definition, _encounterId, inputs) : null;
            var copy = Create(_definition, _encounterId, inputs, new BattleSimulationAssembly(turnWindowSchedule: composition));
            composition?.Attach(copy);
            if (_displacementSolver is ProjectHero.Logic.Interactions.ForcedDisplacementSolver geometry
                && copy._displacementSolver is ProjectHero.Logic.Interactions.ForcedDisplacementSolver targetGeometry)
                geometry.CopyStaticGeometryTo(targetGeometry);
            foreach (var unit in copy._units) copy._logicGrid.UnregisterUnit(unit.UnitId);
            copy._units.Clear(); copy._unitsById.Clear();
            foreach (var source in _units)
            {
                var unit = new UnitRuntimeState { UnitId = source.UnitId, DefinitionId = source.DefinitionId,
                    FactionId = source.FactionId, Position = source.Position, Facing = source.Facing,
                    HealthQ10 = source.HealthQ10, IsAlive = source.IsAlive, DeathProcessed = !source.IsAlive,
                    StateMachine = source.StateMachine.CopyForHud(copy._outbox) };
                copy._units.Add(unit); copy._unitsById.Add(unit.UnitId.Value, unit);
                if (copy._adrenaline.Find(unit.UnitId) == null)
                {
                    ((FactionRelationResolver)copy._factionResolver).RegisterCreatedUnit(unit.UnitId, unit.FactionId);
                    copy._adrenaline.Register(unit.UnitId, 0, 0);
                    var controller = ControllerOf(unit.UnitId);
                    copy.RegisterControllerBinding(controller, new[] { unit.UnitId });
                    composition?.RegisterCreatedUnit(unit.UnitId, _definition.FindUnit(unit.DefinitionId), controller);
                }
                _adrenaline.Find(unit.UnitId).CopyPublicFactsTo(copy._adrenaline.Find(unit.UnitId));
                if (!unit.IsAlive) { copy._notifiedLifecycleUnitIds.Add(unit.UnitId.Value); continue; }
                var directions = copy._unitVolumeTables.VolumeDirectionsOf(unit.DefinitionId);
                string error = directions == null ? copy._logicGrid.RegisterUnitWithPointFootprint(unit.UnitId, unit.Position, unit.Facing)
                    : copy._logicGrid.RegisterUnit(unit.UnitId, unit.Position, unit.Facing, directions);
                if (error != null) throw new LogicDefinitionException(error);
            }
            foreach (var source in _scheduleAuthority.Registry.ActivePlans)
                if (visible.Contains(source.ActionPlanId.Value)) copy._scheduleAuthority.RegisterPlan(source.CopyForPreview());
            _movementAuthority.CopyVisiblePathsTo(copy._movementAuthority, visible);
            _dodgeRelocation.CopyVisibleDestinationsTo(copy._dodgeRelocation, visible);
            _reactionSystem.CopyVisibleOpportunitiesTo(copy._reactionSystem, visible);
            _windowManager.CopyPublicWindowsTo(copy._windowManager);
            _concurrentAction.CopyPublicAuthorizationTo(copy._concurrentAction);
            _buffSystem.CopyPublicEffectsTo(copy._buffSystem);
            // Independent counters are derived solely from visible identifiers; never copy live ID/RNG/AI state.
            while (copy._idGenerator.NextUnitIdValue <= _units.Max(u => u.UnitId.Value)) copy._idGenerator.NextUnitId();
            while (visible.Count > 0 && copy._idGenerator.NextActionPlanIdValue <= visible.Max()) copy._idGenerator.NextActionPlanId();
            long opportunityMax = copy._reactionSystem.ActiveOpportunities.Select(o => o.Id.Value).DefaultIfEmpty().Max();
            while (copy._idGenerator.NextReactionOpportunityIdValue <= opportunityMax) copy._idGenerator.NextReactionOpportunityId();
            long windowMax = Math.Max(_windowManager.LastClosedWindowId, _windowManager.CurrentWindow?.WindowId.Value ?? 0);
            while (copy._idGenerator.NextWindowIdValue <= windowMax) copy._idGenerator.NextWindowId();
            copy._tick = _tick; copy._eventTick = _tick; copy._battleEnd = _battleEnd;
            copy._scheduleRevision = _scheduleRevision; copy._scheduleAuthority.SetRevision(_scheduleRevision);
            if (_tick >= 0) copy._ingress.FreezeTick(_tick);
            copy._currentSnapshot = copy.BuildSnapshot(Math.Max(0, _tick));
            return copy;
        }
        internal bool HudHasRecurringWindows => _assembly.TurnWindowSchedule is ProductionBattleComposition;
    }
}
