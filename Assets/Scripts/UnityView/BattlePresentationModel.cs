using System;
using System.Collections.Generic;
using ProjectHero.Core.Compatibility.Runtime.Input;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.UnityView
{
    /// <summary>Local interaction state. Its only write capability is a trusted Player command port.</summary>
    public sealed class BattlePresentationModel
    {
        private ViewInputPorts _ports;
        private IViewPreviewPort _preview;
        private IReadOnlyList<ScheduleEditOperation> _draft;
        private long _draftRevision;
        private readonly Dictionary<long, ActionPlanSnapshot> _terminal = new Dictionary<long, ActionPlanSnapshot>();
        private readonly Dictionary<long, ActionPlanSnapshot> _previous = new Dictionary<long, ActionPlanSnapshot>();
        private readonly Dictionary<long, IReadOnlyList<ActionPlanId>> _pendingDodge = new Dictionary<long, IReadOnlyList<ActionPlanId>>();
        private IReadOnlyList<ActionPlanSnapshot> _timeline = Array.Empty<ActionPlanSnapshot>();
        public DecisionSnapshot Decision => _ports?.Logic.DecisionSnapshot;
        public IReadOnlyList<ActionPlanSnapshot> Timeline => _timeline;
        public ScheduleEditTransactionResult Preview { get; private set; }
        public bool HasDraft => _draft != null;
        public long LockLine => (Decision?.Tick ?? -1) + 1;
        public long SubmittedCommands { get; private set; }
        public string LastRejection { get; private set; }
        public bool ActionPanelVisible { get; private set; } = true;
        public void SetActionPanelVisible(bool visible) => ActionPanelVisible = visible;
        public TurnWindowSnapshot Window => _preview?.CurrentWindow;
        public IReadOnlyList<AdrenalineLedgerSnapshot> Adrenaline => _preview?.OwnAdrenaline ?? Array.Empty<AdrenalineLedgerSnapshot>();

        public void Bind(ViewInputPorts ports)
        {
            if (ports?.CanSubmit != true || !(ports.Logic is IViewPreviewPort preview))
                throw new InvalidOperationException("VIEW_PREVIEW_PORT_MISSING");
            _ports = ports; _preview = preview; CancelDraft();
            _terminal.Clear(); _previous.Clear(); _pendingDodge.Clear(); SubmittedCommands = 0;
            Synchronize(new EventBatch(-1, Array.Empty<LogicEvent>()));
        }
        public bool CanEdit(long planId) => _ports != null && _ports.Logic.TryFindEditablePlan(new ActionPlanId(planId), out _);
        public ScheduleEditTransactionResult SetDraft(IReadOnlyList<ScheduleEditOperation> operations)
        {
            if (_ports == null || _ports.Logic.IsPaused || _ports.Logic.IsBattleEnded)
            { CancelDraft(); return null; }
            _draft = Array.AsReadOnly(new List<ScheduleEditOperation>(operations).ToArray());
            _draftRevision = _ports.Logic.ScheduleRevision;
            Preview = _preview.Preview(_draft);
            LastRejection = Preview.RejectionCode;
            return Preview;
        }
        public void CancelDraft() { _draft = null; Preview = null; }
        public string ConfirmDraft()
        {
            if (_draft == null) return "VIEW_DRAFT_MISSING";
            if (_ports.Logic.ScheduleRevision != _draftRevision)
            { CancelDraft(); return LastRejection = "STALE_SCHEDULE_REVISION"; }
            if (Preview?.Succeeded != true) return LastRejection = Preview?.RejectionCode ?? "VIEW_PREVIEW_MISSING";
            var request = _ports.Commands.CreateScheduleEdit(new ScheduleEditPayload(_draft));
            CancelDraft(); return Submit(request);
        }
        private string Submit(CommandRequest request)
        {
            if (request == null) return LastRejection = "VIEW_INPUT_UNAVAILABLE";
            var error = _ports.Logic.SubmitCommand(request);
            LastRejection = error?.ReasonCode;
            if (error == null) SubmittedCommands++;
            return LastRejection;
        }
        public string CloseWindow() => Window == null ? "VIEW_WINDOW_MISSING" : Submit(_ports.Commands.CreateCloseWindow(new WindowId(Window.WindowId)));
        public string ActivateConcurrent() => Window == null ? "VIEW_WINDOW_MISSING" : Submit(_ports.Commands.CreateActivateConcurrent(new WindowId(Window.WindowId)));
        public DodgeCancellationPreview PreviewDodge(long opportunity, ActionSpecId action, GridPoint destination)
            => _preview.PreviewDodge(new ReactionOpportunityId(opportunity), action, destination);
        public IReadOnlyList<GridPoint> DodgeDestinations(long opportunity, ActionSpecId action)
            => _preview.DodgeDestinations(new ReactionOpportunityId(opportunity), action);
        public string ConfirmReaction(long opportunity, ActionSpecId action, GridPoint? destination)
        {
            var spec = Decision.FindAction(action);
            if (spec == null || (spec.Type != ActionType.Block && spec.Type != ActionType.Dodge)) return "REACTION_ACTION_INVALID";
            DodgeCancellationPreview conditional = null;
            if (spec.Type == ActionType.Dodge)
            {
                if (!destination.HasValue) return "DODGE_DESTINATION_MISSING";
                conditional = PreviewDodge(opportunity, action, destination.Value);
                if (conditional.RejectionCode != null) return conditional.RejectionCode;
            }
            string code = Submit(_ports.Commands.CreateReaction(new ReactionOpportunityId(opportunity),
                new ReactionCommandPayload(spec.Type == ActionType.Dodge ? ReactionCommandKind.Dodge : ReactionCommandKind.Block, action, destination)));
            if (code == null && conditional != null) _pendingDodge[opportunity] = conditional.AffectedPlanIds;
            return code;
        }
        public bool IsConditionallyInvalidated(long planId)
        {
            foreach (var pending in _pendingDodge.Values)
                foreach (var id in pending) if (id.Value == planId) return true;
            return false;
        }
        public IReadOnlyList<UnitSnapshot> Targets(UnitId owner, ActionSpecId action)
        {
            var result = new List<UnitSnapshot>(); var spec = Decision?.FindAction(action);
            if (!(spec?.Payload is AttackPayloadSpec attack)) return result.AsReadOnly();
            foreach (var unit in Decision.VisibleUnits)
                if (unit.IsAlive && Decision.Allows(attack.AllowedTargetRelations, owner, new UnitId(unit.UnitId))) result.Add(unit);
            return result.AsReadOnly();
        }
        public IReadOnlyList<ReactionOpportunitySnapshot> Reactions => _ports?.Logic.ReactionOpportunitiesOf(_ports.Logic.ControllerId)
            ?? Array.Empty<ReactionOpportunitySnapshot>();

        public void Synchronize(EventBatch batch)
        {
            if (_ports == null) return;
            foreach (var fact in batch.Events)
            {
                long id = fact is ActionPlanTerminatedEvent terminal ? terminal.ActionPlanId
                    : fact is ActionPlanCompletedEvent completed ? completed.ActionPlanId : 0;
                if (id != 0 && _preview.TryGetOwnTerminalPlan(new ActionPlanId(id), out var frozen)) _terminal[id] = frozen;
                else if (id != 0 && _previous.TryGetValue(id, out var old))
                    _terminal[id] = old with { State = fact is ActionPlanCompletedEvent ? (int)ActionPlanState.Completed : (int)ActionPlanState.Terminated,
                        TerminalTick = fact.Tick, TerminationReason = fact is ActionPlanTerminatedEvent reason ? reason.Reason : 0 };
                if (fact is CommandRejectedEvent rejected) LastRejection = rejected.ReasonCode;
            }
            if (_draft != null && (_ports.Logic.ScheduleRevision != _draftRevision || _ports.Logic.IsBattleEnded)) CancelDraft();
            // Locking and natural Tick advancement do not change ScheduleRevision. Revalidate
            // the local candidate against the new boundary before leaving confirmation enabled.
            if (_draft != null)
            {
                var refreshed = _preview.Preview(_draft);
                if (!refreshed.Succeeded)
                { LastRejection = refreshed.RejectionCode; CancelDraft(); }
                else Preview = refreshed;
            }
            var active = new SortedDictionary<long, ActionPlanSnapshot>();
            foreach (var plan in Decision.VisiblePlans) active[plan.ActionPlanId] = plan;
            foreach (var plan in Decision.OwnPlans) active[plan.ActionPlanId] = plan;
            _previous.Clear(); foreach (var pair in active) _previous.Add(pair.Key, pair.Value);
            // Recent terminal rows are display-only. The full archive remains in Logic and replay.
            while (_terminal.Count > 64)
            { long oldest = long.MaxValue; foreach (var key in _terminal.Keys) if (key < oldest) oldest = key; _terminal.Remove(oldest); }
            foreach (var pair in _terminal) active[pair.Key] = pair.Value;
            _timeline = Array.AsReadOnly(new List<ActionPlanSnapshot>(active.Values).ToArray());
            var remove = new List<long>();
            foreach (var pending in _pendingDodge)
            {
                ReactionOpportunitySnapshot current = null;
                foreach (var reaction in Reactions) if (reaction.ReactionOpportunityId == pending.Key) current = reaction;
                if (current == null || current.BoundActionPlanId == 0 && batch.Tick >= LockLine - 1) remove.Add(pending.Key);
                else if (!_previous.ContainsKey(current.BoundActionPlanId)) remove.Add(pending.Key);
            }
            foreach (var id in remove) _pendingDodge.Remove(id);
        }
    }
}
