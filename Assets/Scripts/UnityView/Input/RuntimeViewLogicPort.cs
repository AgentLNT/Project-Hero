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
    public sealed class RuntimeViewLogicPort : IViewLogicPort, IViewPreviewPort
    {
        private readonly BattleSimulation _simulation;
        private readonly CommandIngressEntry _entry;
        private readonly Func<bool> _paused;
        private LogicSnapshot _projectedSnapshot;
        private DecisionSnapshot _decision;
        private IReadOnlyList<AdrenalineLedgerSnapshot> _adrenaline;
        private IReadOnlyList<ReactionOpportunitySnapshot> _reactions;
        private void RefreshProjection()
        {
            if (ReferenceEquals(_projectedSnapshot, _simulation.CurrentSnapshot) && _decision != null) return;
            var current = _simulation.CurrentSnapshot;
            var decision = _simulation.DecisionSnapshotFor(ControllerId);
            var adrenaline = new List<AdrenalineLedgerSnapshot>();
            foreach (var ledger in current.Resources.AdrenalineLedgers)
                if (_simulation.CommandAuthority.CanControl(ControllerId, new UnitId(ledger.UnitId))) adrenaline.Add(ledger);
            _adrenaline = adrenaline.AsReadOnly();
            var reactions = new List<ReactionOpportunitySnapshot>();
            foreach (var opportunity in current.ReactionOpportunities)
                if (_simulation.CommandAuthority.CanControl(ControllerId, new UnitId(opportunity.DefenderUnitId))) reactions.Add(opportunity);
            _reactions = reactions.AsReadOnly();
            _decision = decision; _projectedSnapshot = current;
        }
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
        public DecisionSnapshot DecisionSnapshot { get { RefreshProjection(); return _decision; } }
        public WindowId? OpenWindowId => _simulation.CurrentSnapshot.WindowManager.Windows.FindOpenWindow();
        public TurnWindowSnapshot CurrentWindow
        {
            get
            {
                foreach (var window in _simulation.CurrentSnapshot.WindowManager.Windows)
                    if (window.IsOpen) return window;
                return null;
            }
        }
        public IReadOnlyList<AdrenalineLedgerSnapshot> OwnAdrenaline
        {
            get
            {
                RefreshProjection(); return _adrenaline;
            }
        }
        public bool TryGetOwnTerminalPlan(ActionPlanId id, out ActionPlanSnapshot snapshot)
        {
            var plan = _simulation.ScheduleAuthority.Registry.Find(id);
            if (plan == null || !plan.IsTerminal || !_simulation.CommandAuthority.CanControl(ControllerId, plan.OwnerUnitId))
            { snapshot = null; return false; }
            snapshot = _simulation.ScheduleAuthority.Registry.FindFrozenTerminal(id); return snapshot != null;
        }
        public IReadOnlyList<GridPoint> DodgeDestinations(ReactionOpportunityId opportunity, ActionSpecId action)
        {
            foreach (var unit in DecisionSnapshot.ControlledUnitIds)
                foreach (var candidate in _simulation.AiReactionOpportunities.PublishedOpportunitiesFor(unit, CurrentTick))
                    if (candidate.ReactionOpportunityId == opportunity)
                        foreach (var option in candidate.Options)
                            if (option.ActionSpecId == action)
                                return candidate.DodgeDestinations?.Candidates ?? Array.Empty<GridPoint>();
            return Array.Empty<GridPoint>();
        }
        public DodgeCancellationPreview PreviewDodge(ReactionOpportunityId id, ActionSpecId action, GridPoint destination)
        {
            ReactionOpportunitySnapshot opportunity = null;
            foreach (var item in ReactionOpportunitiesOf(ControllerId))
                if (item.ReactionOpportunityId == id.Value) opportunity = item;
            var spec = _simulation.Definition.FindAction(action);
            if (opportunity == null || !(spec?.Timing is DodgeReactionTimingSpec timing))
                return new DodgeCancellationPreview("REACTION_OPPORTUNITY_CLOSED", Array.Empty<ActionPlanId>(), Array.Empty<ConditionalBudgetRelease>());
            bool offered = false;
            foreach (var option in opportunity.Options ?? Array.Empty<ReactionOptionSnapshot>())
                if (option.ActionSpecId == action.Value && option.IsOpen && option.IsPublished
                    && _simulation.CommandIngress.NextDefaultTargetTick <= option.ResponseDeadlineTick) offered = true;
            if (!offered) return new DodgeCancellationPreview("REACTION_OPPORTUNITY_CLOSED", Array.Empty<ActionPlanId>(), Array.Empty<ConditionalBudgetRelease>());
            string error = EvaluateDodgeDestination(opportunity, action, destination);
            if (error != null) return new DodgeCancellationPreview(error, Array.Empty<ActionPlanId>(), Array.Empty<ConditionalBudgetRelease>());
            var ids = new List<ActionPlanId>();
            var totals = new SortedDictionary<long, long>();
            foreach (var move in _simulation.DodgeMovementInvalidation.QueryFutureEditableMoves(
                new UnitId(opportunity.DefenderUnitId), opportunity.TriggerTick - timing.ReactionWindupTicks))
            {
                ids.Add(move.ActionPlanId);
                if (!move.SubmittedWindowId.HasValue) continue;
                long key = move.SubmittedWindowId.Value.Value;
                totals.TryGetValue(key, out long value);
                totals[key] = checked(value + move.ReservedTurnBudgetTicks);
            }
            var releases = new List<ConditionalBudgetRelease>();
            foreach (var total in totals)
                releases.Add(new ConditionalBudgetRelease(new WindowId(total.Key), total.Value,
                    _simulation.WindowManager.FindWindow(new WindowId(total.Key))?.IsOpen == true));
            return new DodgeCancellationPreview(null, ids.AsReadOnly(), releases.AsReadOnly());
        }

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
            RefreshProjection(); return _reactions;
        }
        public string DescribeDodgeDestinationRejection(UnitId defender, ActionSpecId specId, GridPoint destination)
        {
            if (!_simulation.CommandAuthority.CanControl(ControllerId, defender)) return "ISSUER_CANNOT_CONTROL_UNIT";
            var spec = _simulation.Definition.FindAction(specId);
            if (!(spec?.Payload is DodgePayloadSpec)) return "REACTION_ACTION_NOT_DODGE";
            foreach (var opportunity in ReactionOpportunitiesOf(ControllerId))
                if (opportunity.DefenderUnitId == defender.Value && opportunity.Options != null)
                    foreach (var option in opportunity.Options)
                        if (option.ActionSpecId == specId.Value && option.IsOpen)
                            return EvaluateDodgeDestination(opportunity, specId, destination);
            return "REACTION_OPPORTUNITY_CLOSED";
        }
        public string DescribeDodgeDestinationRejectionForOpportunity(ReactionOpportunityId id,
            UnitId defender, ActionSpecId specId, GridPoint destination)
        {
            foreach (var opportunity in ReactionOpportunitiesOf(ControllerId))
            {
                if (opportunity.ReactionOpportunityId != id.Value || opportunity.DefenderUnitId != defender.Value) continue;
                foreach (var option in opportunity.Options ?? Array.Empty<ReactionOptionSnapshot>())
                    if (option.ActionSpecId == specId.Value && option.IsOpen && option.IsPublished
                        && _simulation.CommandIngress.NextDefaultTargetTick <= option.ResponseDeadlineTick)
                        return EvaluateDodgeDestination(opportunity, specId, destination);
            }
            return "REACTION_OPPORTUNITY_CLOSED";
        }
        private string EvaluateDodgeDestination(ReactionOpportunitySnapshot opportunity, ActionSpecId specId, GridPoint destination)
        {
            var defender = new UnitId(opportunity.DefenderUnitId);
            if (!_simulation.CommandAuthority.CanControl(ControllerId, defender)) return "ISSUER_CANNOT_CONTROL_UNIT";
            if (!(_simulation.Definition.FindAction(specId)?.Payload is DodgePayloadSpec dodge)) return "REACTION_ACTION_NOT_DODGE";
            if (!_simulation.LogicGrid.TryGetAnchor(defender, out var from)
                || !_simulation.LogicGrid.TryGetFacing(defender, out var facing)) return "LOGIC_GRID_UNIT_UNKNOWN";
            return _simulation.DodgeRelocation.EvaluateDestination(new DodgeDestinationReservation(
                new ReactionOpportunityId(opportunity.ReactionOpportunityId), default, defender, facing,
                from, destination, dodge.MaxDistanceSteps, dodge.Pattern.Directions,
                opportunity.TriggerTick, _simulation.CommandIngress.NextDefaultTargetTick, 0));
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
