using System;
using System.Collections.Generic;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Core.Compatibility.Runtime.Input
{
    /// <summary>Player port with a fixed registered identity, read projections, and the single ingress.</summary>
    public sealed class RuntimeViewLogicPort : IViewLogicPort
    {
        private readonly BattleSimulation _simulation;
        private readonly CommandIngressEntry _entry;
        private readonly Func<bool> _paused;
        internal RuntimeViewLogicPort(BattleSimulation simulation, ControllerId controller, Func<bool> paused)
        {
            _simulation = simulation;
            _entry = simulation.CommandIngress.FindEntry(controller);
            if (_entry == null || _entry.SourceKind != ProjectHero.Logic.Definitions.CommandSourceKind.Player)
                throw new LogicDefinitionException("VIEW_PLAYER_CONTROLLER_INVALID", controller.Value);
            ControllerId = controller; _paused = paused;
        }
        public long CurrentTick => _simulation.Tick;
        public long ScheduleRevision => _simulation.ScheduleRevision;
        public bool IsBattleEnded => _simulation.IsEnded || _simulation.IsDisposed;
        public bool IsPaused => _paused();
        public ControllerId ControllerId { get; }
        public DecisionSnapshot DecisionSnapshot => _simulation.DecisionSnapshotFor(ControllerId);
        public WindowId? OpenWindowId => _simulation.CurrentSnapshot.WindowManager.Windows.FindOpenWindow();

        public CommandIngressRejection SubmitCommand(CommandRequest request)
        {
            if (IsPaused) throw new LogicDefinitionException("VIEW_INPUT_PAUSED", "");
            if (request.TargetTick != _simulation.CommandIngress.NextDefaultTargetTick)
                throw new LogicDefinitionException("VIEW_REQUEST_TARGET_NOT_NEXT_BOUNDARY", "");
            return _entry.Submit(request);
        }
        public bool TryGetPlanSnapshot(ActionPlanId id, out ActionPlanSnapshot plan)
        {
            foreach (var p in DecisionSnapshot.VisiblePlans)
                if (p.ActionPlanId == id.Value) { plan = p; return true; }
            plan = null; return false;
        }
        public bool TryFindEditablePlan(ActionPlanId id, out ActionPlanSnapshot plan)
        {
            foreach (var p in DecisionSnapshot.EditablePlansOf(ControllerId))
                if (p.ActionPlanId == id.Value) { plan = p; return true; }
            plan = null; return false;
        }
        public IReadOnlyList<ActionPlanSnapshot> EditablePlansOf(ControllerId controller)
            => controller == ControllerId ? DecisionSnapshot.EditablePlansOf(controller) : Array.Empty<ActionPlanSnapshot>();
        public bool TryGetActionType(string id, out ActionType type)
        {
            var spec = _simulation.Definition.FindAction(new ActionSpecId(id));
            type = spec == null ? default : spec.Type; return spec != null;
        }
        public IReadOnlyList<ReactionOpportunitySnapshot> ReactionOpportunitiesOf(ControllerId controller)
        {
            if (controller != ControllerId) return Array.Empty<ReactionOpportunitySnapshot>();
            var result = new List<ReactionOpportunitySnapshot>();
            foreach (var opportunity in _simulation.CurrentSnapshot.ReactionOpportunities)
                if (_simulation.CommandAuthority.CanControl(controller, new UnitId(opportunity.DefenderUnitId))) result.Add(opportunity);
            return result.AsReadOnly();
        }
        public string DescribeDodgeDestinationRejection(UnitId defender, ActionSpecId specId, GridPoint destination)
        {
            if (!_simulation.CommandAuthority.CanControl(ControllerId, defender)) return "ISSUER_CANNOT_CONTROL_UNIT";
            var spec = _simulation.Definition.FindAction(specId);
            if (!(spec?.Payload is DodgePayloadSpec dodge)) return "REACTION_ACTION_NOT_DODGE";
            if (!_simulation.LogicGrid.TryGetAnchor(defender, out var from)
                || !_simulation.LogicGrid.TryGetFacing(defender, out var facing)) return "LOGIC_GRID_UNIT_UNKNOWN";
            foreach (var opportunity in ReactionOpportunitiesOf(ControllerId))
                if (opportunity.DefenderUnitId == defender.Value && opportunity.Options != null)
                    foreach (var option in opportunity.Options)
                        if (option.ActionSpecId == specId.Value && option.IsOpen)
                            return _simulation.DodgeRelocation.EvaluateDestination(new DodgeDestinationReservation(
                                new ReactionOpportunityId(opportunity.ReactionOpportunityId), default, defender, facing,
                                from, destination, dodge.MaxDistanceSteps, dodge.Pattern.Directions,
                                opportunity.TriggerTick, _simulation.CommandIngress.NextDefaultTargetTick, 0));
            return "REACTION_OPPORTUNITY_CLOSED";
        }
        public ScheduleEditTransactionResult Preview(IReadOnlyList<ScheduleEditOperation> operations)
        {
            foreach (var operation in operations)
            {
                UnitId owner;
                if (operation is AddOrdinaryPlanOperation add) owner = add.OwnerUnitId;
                else if (TryFindEditablePlan(operation.PlanId, out var plan)) owner = new UnitId(plan.OwnerUnitId);
                else return ScheduleEditTransactionResult.Rejected("PLAN_NOT_EDITABLE", ScheduleRevision);
                if (!_simulation.CommandAuthority.CanControl(ControllerId, owner))
                    return ScheduleEditTransactionResult.Rejected("ISSUER_CANNOT_EDIT_PLAN", ScheduleRevision);
            }
            return _simulation.ScheduleEditor.Apply(operations, _simulation.CommandIngress.NextDefaultTargetTick,
                ScheduleRevision, ScheduleRevision, preview: true, expectedWindowId: OpenWindowId, issuer: ControllerId);
        }
    }
    internal static class ViewWindowLookup
    {
        internal static WindowId? FindOpenWindow(this IReadOnlyList<TurnWindowSnapshot> windows)
        {
            foreach (var window in windows)
                if (window.IsOpen && window.IsAcceptingSubmissions) return new WindowId(window.WindowId);
            return null;
        }
    }
}
