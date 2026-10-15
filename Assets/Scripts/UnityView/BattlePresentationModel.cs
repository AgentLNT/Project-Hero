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
        public ScheduleDraftModel Drafts { get; } = new ScheduleDraftModel();
        private BattleClockController _clock;
        private long _pendingTick, _pendingOrdinal;
        private readonly List<(long Tick, long Ordinal)> _pendingCommands = new List<(long, long)>();
        public string LastCommandStatus { get; private set; }
        private readonly List<(long Opportunity, ActionSpecId Action, GridPoint? Destination, long? Start)> _stagedReactions = new List<(long, ActionSpecId, GridPoint?, long?)>();
        public int StagedReactionCount => _stagedReactions.Count;
        public bool CanPlan => _ports != null && !_ports.Logic.IsBattleEnded
            && (!_ports.Logic.IsPaused || _clock?.State == BattleClockState.TacticalPlanning);
        public void BindClock(BattleClockController clock) => _clock = clock;
        public HudFacts Facts
        {
            get
            {
                var permission = Window == null ? HudSubmissionPermission.None
                    : OwnsWindow ? HudSubmissionPermission.OwnWindow
                    : _preview.ConcurrentAction.HasActiveAuthorization ? HudSubmissionPermission.Intervened : HudSubmissionPermission.CanIntervene;
                bool threat = false;
                foreach (var r in Reactions) if (r.Options != null) foreach (var o in r.Options)
                    if (o.IsOpen && o.IsPublished && LockLine <= o.ResponseDeadlineTick) threat = true;
                return new HudFacts(_clock?.State ?? (_ports?.Logic.IsPaused == true ? BattleClockState.SystemPaused : BattleClockState.Running), permission, threat);
            }
        }
        public bool OwnsWindow
        {
            get { if (Window != null && Decision != null) foreach (var unit in Decision.ControlledUnitIds) if (unit.Value == Window.OwnerUnitId) return true; return false; }
        }
        private readonly Dictionary<long, ActionPlanSnapshot> _terminal = new Dictionary<long, ActionPlanSnapshot>();
        private readonly Dictionary<long, ActionPlanSnapshot> _previous = new Dictionary<long, ActionPlanSnapshot>();
        private readonly Dictionary<long, IReadOnlyList<ActionPlanId>> _pendingDodge = new Dictionary<long, IReadOnlyList<ActionPlanId>>();
        private IReadOnlyList<ActionPlanSnapshot> _timeline = Array.Empty<ActionPlanSnapshot>();
        public DecisionSnapshot Decision => _ports?.Logic.DecisionSnapshot;
        public IReadOnlyList<ActionPlanSnapshot> Timeline => _timeline;
        public ScheduleEditTransactionResult Preview { get; private set; }
        public bool HasDraft => Drafts.HasIntent && Drafts.State != ScheduleDraftState.AwaitingAcceptance;
        public long LockLine => (Decision?.Tick ?? -1) + 1;
        public long SubmittedCommands { get; private set; }
        public string LastRejection { get; private set; }
        public bool ActionPanelVisible { get; private set; } = true;
        public void SetActionPanelVisible(bool visible) => ActionPanelVisible = visible;
        public TurnWindowSnapshot Window => _preview?.CurrentWindow;
        public IReadOnlyList<AdrenalineLedgerSnapshot> Adrenaline => _preview?.AllAdrenaline ?? Array.Empty<AdrenalineLedgerSnapshot>();
        public HudPreviewService HudPreviews => _preview?.HudPreviews;

        public void Bind(ViewInputPorts ports)
        {
            if (ports?.CanSubmit != true || !(ports.Logic is IViewPreviewPort preview))
                throw new InvalidOperationException("VIEW_PREVIEW_PORT_MISSING");
            _ports = ports; _preview = preview; Drafts.Reset(); Preview = null; _pendingTick = _pendingOrdinal = 0; _stagedReactions.Clear(); LastRejection = null;
            _terminal.Clear(); _previous.Clear(); _pendingDodge.Clear(); SubmittedCommands = 0;
            _pendingCommands.Clear(); LastCommandStatus = null;
            Synchronize(new EventBatch(-1, Array.Empty<LogicEvent>()));
        }
        public bool CanEdit(long planId) => _ports != null && _ports.Logic.TryFindEditablePlan(new ActionPlanId(planId), out _);
        public ScheduleEditTransactionResult SetDraft(IReadOnlyList<ScheduleEditOperation> operations)
        {
            if (!CanPlan || Drafts.State == ScheduleDraftState.AwaitingAcceptance) return null;
            Drafts.Clear(); foreach (var operation in operations) Drafts.Append(operation);
            return RevalidateDraft();
        }
        public long AppendDraft(ScheduleEditOperation operation)
        {
            if (!CanPlan || Drafts.State == ScheduleDraftState.AwaitingAcceptance) return 0;
            long id = Drafts.Append(operation); RevalidateDraft(); return id;
        }
        public bool EditDraft(long id, ScheduleEditOperation operation)
        { if (!CanPlan || !Drafts.Replace(id, operation)) return false; RevalidateDraft(); return true; }
        public bool RemoveDraft(long id)
        { if (!CanPlan || !Drafts.Remove(id)) return false; RevalidateDraft(); return true; }
        public void UpsertPlanEdit(ScheduleEditOperation operation)
        {
            foreach (var entry in Drafts.Entries)
                if (entry.Operation.PlanId == operation.PlanId && operation.PlanId.IsValid)
                { EditDraft(entry.Id, operation); return; }
            AppendDraft(operation);
        }
        public ScheduleEditTransactionResult RevalidateDraft()
        {
            if (!CanPlan || Drafts.State == ScheduleDraftState.AwaitingAcceptance) return Preview;
            Drafts.BaseTick = Decision.Tick; Drafts.BaseRevision = _ports.Logic.ScheduleRevision; Drafts.BaseWindowId = Window?.WindowId ?? 0;
            Drafts.State = ScheduleDraftState.Editing; Drafts.Reason = null;
            Preview = Drafts.HasIntent ? _preview.Preview(Drafts.Operations) : null;
            LastRejection = Preview?.RejectionCode; return Preview;
        }
        public ScheduleEditTransactionResult PreviewCandidate(ScheduleEditOperation candidate)
        {
            if (!CanPlan || Drafts.State == ScheduleDraftState.AwaitingAcceptance) return null;
            var operations = new List<ScheduleEditOperation>(Drafts.Operations);
            if (candidate is AddOrdinaryPlanOperation add) candidate = add with { TemporaryPlanKey = long.MaxValue };
            operations.Add(candidate); return _preview.Preview(operations);
        }
        public ActionPlanSnapshot PreviewPlanFor(long draftId)
        {
            if (Preview?.AppliedEdits == null || Preview.PreviewPlans == null) return null;
            for (int i = 0; i < Drafts.Entries.Count && i < Preview.AppliedEdits.Count; i++)
                if (Drafts.Entries[i].Id == draftId)
                    foreach (var plan in Preview.PreviewPlans) if (plan.Plan.ActionPlanId == Preview.AppliedEdits[i].PlanId.Value) return plan.Plan;
            return null;
        }
        public void CancelDraft()
        { if (Drafts.State == ScheduleDraftState.AwaitingAcceptance) return; Drafts.Clear(); Preview = null; LastRejection = null; }
        public string ConfirmDraft()
        {
            if (!CanPlan) return LastRejection = "VIEW_INPUT_UNAVAILABLE";
            if (!HasDraft) return "VIEW_DRAFT_MISSING";
            if (Drafts.State == ScheduleDraftState.Stale || _ports.Logic.ScheduleRevision != Drafts.BaseRevision
                || (Window?.WindowId ?? 0) != Drafts.BaseWindowId)
            { Drafts.State = ScheduleDraftState.Stale; return LastRejection = Drafts.Reason = "STALE_SCHEDULE_REVISION"; }
            Preview = _preview.Preview(Drafts.Operations);
            if (Preview?.Succeeded != true) return LastRejection = Preview?.RejectionCode ?? "VIEW_PREVIEW_MISSING";
            if (_clock?.State == BattleClockState.TacticalPlanning)
            { Drafts.State = ScheduleDraftState.AwaitingResume; return null; }
            var request = _ports.Commands.CreateScheduleEdit(new ScheduleEditPayload(Drafts.Operations));
            var code = Submit(request);
            if (code == null)
            {
                Drafts.State = ScheduleDraftState.AwaitingAcceptance; _pendingTick = request.TargetTick;
                _pendingOrdinal = (_ports.Logic as IViewCommandReceiptPort)?.LastSubmittedOrdinal ?? 0;
            }
            return code;
        }
        public string SubmitOnResume()
        {
            string error = null;
            if (Drafts.State == ScheduleDraftState.AwaitingResume) error = ConfirmDraft();
            if (error != null) return error;
            foreach (var r in _stagedReactions.ToArray())
            {
                error = ConfirmReaction(r.Opportunity, r.Action, r.Destination, r.Start);
                if (error != null) return error;
                _stagedReactions.Remove(r);
            }
            return null;
        }
        private string Submit(CommandRequest request)
        {
            if (request == null) return LastRejection = "VIEW_INPUT_UNAVAILABLE";
            var error = _ports.Logic.SubmitCommand(request);
            LastRejection = error?.ReasonCode;
            if (error == null)
            {
                SubmittedCommands++; LastCommandStatus = "待采纳";
                if (_ports.Logic is IViewCommandReceiptPort receipts) _pendingCommands.Add((request.TargetTick, receipts.LastSubmittedOrdinal));
            }
            return LastRejection;
        }
        public string ActivateConcurrent() => Window == null ? "VIEW_WINDOW_MISSING" : Submit(_ports.Commands.CreateActivateConcurrent(new WindowId(Window.WindowId)));
        public DodgeCancellationPreview PreviewDodge(long opportunity, ActionSpecId action, GridPoint destination, long? startTick = null)
            => _preview.PreviewDodge(new ReactionOpportunityId(opportunity), action, destination, startTick);
        public IReadOnlyList<GridPoint> DodgeDestinations(long opportunity, ActionSpecId action)
            => _preview.DodgeDestinations(new ReactionOpportunityId(opportunity), action);
        public string ConfirmReaction(long opportunity, ActionSpecId action, GridPoint? destination, long? startTick = null)
        {
            var spec = Decision.FindAction(action);
            if (spec == null || (spec.Type != ActionType.Block && spec.Type != ActionType.Dodge)) return "REACTION_ACTION_INVALID";
            DodgeCancellationPreview conditional = null;
            if (spec.Type == ActionType.Dodge)
            {
                if (!destination.HasValue) return "DODGE_DESTINATION_MISSING";
                conditional = PreviewDodge(opportunity, action, destination.Value, startTick);
                if (conditional.RejectionCode != null) return conditional.RejectionCode;
            }
            if (_clock?.State == BattleClockState.TacticalPlanning)
            {
                var feasible = HudPreviews?.PreviewReaction(new ReactionOpportunityId(opportunity), action, startTick);
                if (feasible != null && !feasible.IsFeasible) return LastRejection = feasible.RejectionCode;
                _stagedReactions.RemoveAll(r => r.Opportunity == opportunity);
                _stagedReactions.Add((opportunity, action, destination, startTick)); return null;
            }
            string code = Submit(_ports.Commands.CreateReaction(new ReactionOpportunityId(opportunity),
                new ReactionCommandPayload(spec.Type == ActionType.Dodge ? ReactionCommandKind.Dodge : ReactionCommandKind.Block, action, destination, startTick)));
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
            if (_ports.Logic is IViewCommandReceiptPort commandReceipts)
                foreach (var pending in _pendingCommands.ToArray())
                {
                    var receipt = commandReceipts.ReceiptFor(pending.Tick, pending.Ordinal, batch);
                    if (receipt == null) continue;
                    LastCommandStatus = receipt.Accepted ? "权威已接受" : "权威已拒绝";
                    if (!receipt.Accepted) LastRejection = receipt.Reason;
                    _pendingCommands.Remove(pending);
                }
            foreach (var fact in batch.Events)
            {
                long id = fact is ActionPlanTerminatedEvent terminal ? terminal.ActionPlanId
                    : fact is ActionPlanCompletedEvent completed ? completed.ActionPlanId : 0;
                if (id != 0 && _preview.TryGetOwnTerminalPlan(new ActionPlanId(id), out var frozen)) _terminal[id] = frozen;
                else if (id != 0 && _previous.TryGetValue(id, out var old))
                    _terminal[id] = old with { State = fact is ActionPlanCompletedEvent ? (int)ActionPlanState.Completed : (int)ActionPlanState.Terminated,
                        TerminalTick = fact.Tick, TerminationReason = fact is ActionPlanTerminatedEvent reason ? reason.Reason : 0 };
            }
            if (Drafts.State == ScheduleDraftState.AwaitingAcceptance && _ports.Logic is IViewCommandReceiptPort receipts)
            {
                var receipt = receipts.ReceiptFor(_pendingTick, _pendingOrdinal, batch);
                if (receipt != null)
                {
                    if (receipt.Accepted) { Drafts.Clear(); Drafts.State = ScheduleDraftState.Accepted; Preview = null; LastRejection = null; }
                    else { Drafts.State = ScheduleDraftState.Rejected; LastRejection = Drafts.Reason = receipt.Reason; }
                }
            }
            if (HasDraft && Drafts.State != ScheduleDraftState.Rejected)
            {
                if (_ports.Logic.ScheduleRevision != Drafts.BaseRevision || (Window?.WindowId ?? 0) != Drafts.BaseWindowId || _ports.Logic.IsBattleEnded)
                { Drafts.State = ScheduleDraftState.Stale; LastRejection = Drafts.Reason = "STALE_SCHEDULE_REVISION"; }
                else if (Drafts.State != ScheduleDraftState.Stale && Drafts.State != ScheduleDraftState.Rejected)
                {
                    Preview = _preview.Preview(Drafts.Operations);
                    if (!Preview.Succeeded) { Drafts.State = ScheduleDraftState.Stale; LastRejection = Drafts.Reason = Preview.RejectionCode; }
                }
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
