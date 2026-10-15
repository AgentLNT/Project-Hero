using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Snapshots
{
    public sealed record ProjectedWindow(long OwnerUnitId, long OpenedAtTick, long EndTick, bool IsEstimated);
    public sealed record ReactionFeasibility(long OpportunityId, string ActionSpecId, long StartTick, long EndTick,
        string RejectionCode, IReadOnlyList<ActionPlanSnapshot> ConflictingPlans, int RequiredAdrenaline, int AvailableAdrenaline,
        ActionType ReactionType = ActionType.Block)
    { public bool IsFeasible => RejectionCode == null; }
    public sealed record GuardContactPreview(long Tick, int DebitQ10, int RemainingQ10, IReadOnlyList<long> SourcePlanIds);
    public sealed record DefenseContactPreview(long? TriggerTick, IReadOnlyList<long> SourcePlanIds,
        bool HasEarlierPreemptingContact, IReadOnlyList<GuardContactPreview> GuardContacts,
        long? YieldTick, long? KnownHorizonTick, string RejectionCode);
    public sealed record YieldPointPreview(long ReactionPlanId, long? YieldTick);
    public sealed record OpponentReactionCapability(long AttackPlanId, long TargetUnitId,
        IReadOnlyList<ReactionFeasibility> Options, bool IsInRecovery, bool IsWithinKnownHorizon)
    {
        public bool CanBlock => Options.Any(o => o.IsFeasible && o.ReactionType == ActionType.Block);
        public bool CanDodge => Options.Any(o => o.IsFeasible && o.ReactionType == ActionType.Dodge);
    }

    /// <summary>
    /// One committed controller projection. Lazy queries run the exact Step evaluators in isolated
    /// Logic worlds containing only public and own facts, without AI, ingress input or live RNG.
    /// Each value/request is memoized here; nothing is written back to the authoritative world.
    /// Results are conditional on no new input: unrevealed reactions, private foreign plans and
    /// information beyond the next foreign window can change the outcome.
    /// </summary>
    public sealed partial class HudPreviewService
    {
        private readonly BattleSimulation _template;
        private readonly Func<BattleSimulation> _fork;
        private IReadOnlyList<ProjectedWindow> _windows;
        private long? _horizon;
        private readonly Dictionary<(long, string, long?), ReactionFeasibility> _feasibility = new Dictionary<(long, string, long?), ReactionFeasibility>();
        private readonly Dictionary<(long, string, long?, GridPoint?), DefenseContactPreview> _contacts = new Dictionary<(long, string, long?, GridPoint?), DefenseContactPreview>();
        private readonly Dictionary<OrdinaryPlanRequest, DefenseContactPreview> _ordinaryContacts = new Dictionary<OrdinaryPlanRequest, DefenseContactPreview>();
        private readonly Dictionary<long, DefenseContactPreview> _existingContacts = new Dictionary<long, DefenseContactPreview>();
        private readonly Dictionary<OrdinaryPlanRequest, IReadOnlyList<YieldPointPreview>> _yields = new Dictionary<OrdinaryPlanRequest, IReadOnlyList<YieldPointPreview>>();
        private IReadOnlyList<OpponentReactionCapability> _opponents;
        private IReadOnlyList<ReactionFeasibility> _reactionOptions;
        internal HudPreviewService(DecisionSnapshot decision, BattleSimulation template, Func<BattleSimulation> fork)
        { Decision = decision; _template = template; _fork = fork; }
        public DecisionSnapshot Decision { get; }
        public IReadOnlyList<ProjectedWindow> WindowSequence { get { ProjectWindows(); return _windows; } }
        public long? KnownHorizonTick { get { ProjectWindows(); return _horizon; } }
        public IReadOnlyList<ReactionFeasibility> ReactionOptions
        {
            get
            {
                if (_reactionOptions == null)
                    _reactionOptions = _template.ReactionOpportunities.ActiveOpportunities
                        .Where(o => o.IsOpen && Controls(o.DefenderUnitId.Value)).OrderBy(o => o.Id.Value)
                        .SelectMany(o => o.Options.Where(p => p.IsPublished).OrderBy(p => p.ReactionActionSpecId.Value, StringComparer.Ordinal)
                            .Select(p => PreviewReaction(o.Id, p.ReactionActionSpecId))).ToList().AsReadOnly();
                return _reactionOptions;
            }
        }
        public bool HasFeasibleReaction => ReactionOptions.Any(o => o.IsFeasible);
        private bool Controls(long unit) => Decision.ControlledUnitIds.Any(u => u.Value == unit);
        private static StepResult Step(BattleSimulation s)
        { long tick = s.Tick + 1; return s.Step(tick, s.CommandIngress.FreezeTick(tick)); }

        private void ProjectWindows()
        {
            if (_windows != null) return;
            var windows = new List<ProjectedWindow>();
            var current = _template.CurrentTurnWindow;
            if (current != null) windows.Add(new ProjectedWindow(current.OwnerUnitId.Value, current.OpenedAtTick, current.EndTick, false));
            using var world = _fork();
            long duration = world.Definition.FindEncounter(world.EncounterId).TurnSubmission?.WindowDurationTicks ?? 180;
            long limit = checked(world.Tick + duration * (Decision.VisibleUnits.Count + 3L) + 3);
            long lastId = current?.WindowId.Value ?? 0;
            while (!world.IsEnded && world.Tick < limit && (windows.Count < 3 || !_horizon.HasValue))
            {
                if (!world.HudHasRecurringWindows && world.WindowManager.ScheduledWindowCount == 0) break;
                Step(world);
                var window = world.CurrentTurnWindow;
                if (window == null || window.WindowId.Value == lastId) continue;
                lastId = window.WindowId.Value;
                windows.Add(new ProjectedWindow(window.OwnerUnitId.Value, window.OpenedAtTick, window.EndTick, true));
                if (!_horizon.HasValue && !Controls(window.OwnerUnitId.Value)) _horizon = window.OpenedAtTick;
            }
            _windows = windows.AsReadOnly();
        }

        public ReactionFeasibility PreviewReaction(ReactionOpportunityId opportunity, ActionSpecId action, long? startTick = null)
        {
            var key = (opportunity.Value, action.Value, startTick);
            if (_feasibility.TryGetValue(key, out var cached)) return cached;
            var source = _template.ReactionOpportunities.FindOpportunity(opportunity);
            if (source == null || !Controls(source.DefenderUnitId.Value))
                return _feasibility[key] = new ReactionFeasibility(opportunity.Value, action.Value, 0, 0,
                    ReactionCodes.OPPORTUNITY_NOT_OPEN, Array.Empty<ActionPlanSnapshot>(), 0, 0);
            return _feasibility[key] = EvaluateReaction(_template, source, action, startTick);
        }
        private ReactionFeasibility EvaluateReaction(BattleSimulation world, ReactionOpportunityRuntime opportunity,
            ActionSpecId action, long? startTick)
        {
            long commandTick = Decision.Tick + world.Definition.ReactionRules.CommandIngressLeadTicks;
            var interval = world.ReactionOpportunities.PreviewStart(opportunity.Id, action, commandTick, startTick);
            var spec = world.Definition.FindAction(action);
            var ledger = world.AdrenalineLedgerOf(opportunity.DefenderUnitId);
            var conflicts = new List<ActionPlanSnapshot>();
            if (spec != null && interval.EndTick > interval.StartTick)
            {
                var lane = world.ScheduleAuthority.FindLane(opportunity.DefenderUnitId);
                if (lane != null)
                    foreach (var plan in lane.Plans)
                        if (plan.StartTick < interval.HardEndTick && interval.StartTick < plan.HardEndTick
                            && !DodgeMovementInvalidationSeam.DodgeMayInvalidate(plan, spec.Type, interval.StartTick))
                            conflicts.Add(ActionPlanSnapshot.From(plan));
            }
            string error = interval.RejectionCode;
            if (error == null && (ledger?.AvailableAdrenaline ?? 0) < (spec?.AdrenalineCost ?? 0)) error = "ADRENALINE_INSUFFICIENT";
            if (error == null && spec?.Type == ActionType.Dodge)
            {
                var offered = world.AiReactionOpportunities.PublishedOpportunitiesFor(opportunity.DefenderUnitId, Decision.Tick)
                    .FirstOrDefault(o => o.ReactionOpportunityId == opportunity.Id);
                if (offered?.DodgeDestinations?.Candidates == null || offered.DodgeDestinations.Candidates.Count == 0)
                    error = "DODGE_NO_LEGAL_DESTINATION";
            }
            conflicts.Sort((a, b) => a.ActionPlanId.CompareTo(b.ActionPlanId));
            return new ReactionFeasibility(opportunity.Id.Value, action.Value, interval.StartTick, interval.EndTick,
                error, conflicts.AsReadOnly(), spec?.AdrenalineCost ?? 0, ledger?.AvailableAdrenaline ?? 0, spec?.Type ?? ActionType.Block);
        }

        public DefenseContactPreview PreviewDefense(ActionPlanId planId)
        {
            if (_existingContacts.TryGetValue(planId.Value, out var cached)) return cached;
            using var world = _fork();
            var plan = world.ScheduleAuthority.Registry.Find(planId);
            if (plan == null) return _existingContacts[planId.Value] = Rejected("PLAN_NOT_VISIBLE");
            return _existingContacts[planId.Value] = ProjectDefense(world, plan);
        }
        public DefenseContactPreview PreviewDefense(ReactionOpportunityId opportunity, ActionSpecId action,
            long? startTick = null, GridPoint? destination = null)
        {
            var key = (opportunity.Value, action.Value, startTick, destination);
            if (_contacts.TryGetValue(key, out var cached)) return cached;
            var feasible = PreviewReaction(opportunity, action, startTick);
            if (!feasible.IsFeasible) return _contacts[key] = Rejected(feasible.RejectionCode);
            using var world = _fork();
            string error = world.ReactionOpportunities.TryAcceptById(opportunity, action,
                Decision.Tick + world.Definition.ReactionRules.CommandIngressLeadTicks, destination, out var plan, startTick);
            return _contacts[key] = error != null ? Rejected(error) : ProjectDefense(world, plan);
        }
        public DefenseContactPreview PreviewGuard(OrdinaryPlanRequest request)
        {
            if (_ordinaryContacts.TryGetValue(request, out var cached)) return cached;
            using var world = _fork();
            string error = AddOrdinary(world, request, out var plan);
            if (error == null && plan.ActionType != ActionType.Guard) error = "PREVIEW_ACTION_NOT_GUARD";
            return _ordinaryContacts[request] = error != null ? Rejected(error) : ProjectDefense(world, plan);
        }
        private string AddOrdinary(BattleSimulation world, OrdinaryPlanRequest request, out ActionPlan plan)
        {
            plan = null;
            if (!Controls(request.OwnerUnitId.Value)) return "ISSUER_CANNOT_CONTROL_UNIT";
            var operation = new AddOrdinaryPlanOperation(1, request.OwnerUnitId, request.ActionSpecId,
                request.StartTick, PrimaryTargetUnitId: request.PrimaryTargetUnitId,
                Facing: request.Facing, Destination: request.Destination);
            var transaction = world.ScheduleEditor.Apply(new[] { operation },
                Decision.Tick + world.Definition.ReactionRules.CommandIngressLeadTicks, world.ScheduleRevision,
                world.ScheduleRevision, expectedWindowId: request.SubmittedWindowId, issuer: Decision.ControllerId);
            if (!transaction.Succeeded) return transaction.RejectionCode;
            plan = world.ScheduleAuthority.Registry.ActivePlans.First(p => p.ActionPlanId.Value == transaction.AddedPlanIds[0].Value);
            return null;
        }
        private DefenseContactPreview Rejected(string code) => new DefenseContactPreview(null, Array.Empty<long>(), false,
            Array.Empty<GuardContactPreview>(), null, KnownHorizonTick, code);
        private DefenseContactPreview ProjectDefense(BattleSimulation world, ActionPlan plan)
        {
            if (plan.ActionType != ActionType.Guard && plan.ActionType != ActionType.Block && plan.ActionType != ActionType.Dodge)
                return Rejected("PREVIEW_ACTION_NOT_DEFENSE");
            long end = Math.Min(plan.EndTick, KnownHorizonTick ?? plan.EndTick);
            var guards = new List<GuardContactPreview>();
            long? trigger = null, yield = null;
            IReadOnlyList<long> sources = Array.Empty<long>();
            while (!world.IsEnded && world.Tick + 1 < end && !plan.IsTerminal)
            {
                var result = Step(world);
                var contactSources = SourcesFor(world, plan);
                foreach (var fact in result.Events.EventsInSequenceOrder)
                {
                    if (fact is ReactionTriggeredEvent r && r.ReactionActionPlanId == plan.ActionPlanId.Value)
                    { trigger = r.Tick; sources = contactSources; }
                    if (fact is GuardQuotaConsumedEvent g && g.PlanId == plan.ActionPlanId)
                        guards.Add(new GuardContactPreview(g.Tick, g.DebitQ10, g.RemainingQ10, contactSources));
                }
                if (plan.TerminationReason == ActionTerminationReason.ReactionYieldedLane) yield = plan.TerminalTick;
            }
            return new DefenseContactPreview(trigger, sources, trigger.HasValue && trigger < plan.SourceImpactTick,
                guards.AsReadOnly(), yield, KnownHorizonTick, null);
        }
        private static IReadOnlyList<long> SourcesFor(BattleSimulation world, ActionPlan plan)
        {
            var result = new SortedSet<long>();
            var intents = (world.ConflictGraph?.Nodes ?? Array.Empty<ConflictNode>()).ToDictionary(n => n.Intent.ActionPlanId.Value, n => n.Intent);
            foreach (var contact in world.ConflictGraph?.Contacts ?? Array.Empty<InteractionContact>())
            {
                long source = contact.Key.FirstPlanId == plan.ActionPlanId ? contact.Key.SecondPlanId.Value
                    : contact.Key.SecondPlanId == plan.ActionPlanId ? contact.Key.FirstPlanId.Value : 0;
                if (!intents.TryGetValue(source, out var intent)) continue;
                bool qualifies = plan.ActionType == ActionType.Block ? contact.CoveredAfter && DefenseContactDetector.HasBlockablePayload(intent)
                    : plan.ActionType == ActionType.Dodge ? contact.CoveredBefore && (intent.Tags & AttackTagMask.Dodgeable) != 0
                    : contact.CoveredAfter && world.Definition.FindAction(plan.ActionSpecId)?.Payload is GuardPayloadSpec guard
                        && GuardQuota.ContactDebit(guard, intent.DamageComponents, intent.Tags, intent.Momentum.MomentumUnits) > 0;
                if (qualifies) result.Add(source);
            }
            return result.ToList().AsReadOnly();
        }

        public IReadOnlyList<YieldPointPreview> PreviewYield(OrdinaryPlanRequest request)
        {
            if (_yields.TryGetValue(request, out var cached)) return cached;
            using var world = _fork();
            string error = AddOrdinary(world, request, out var ordinary);
            if (error != null) return _yields[request] = Array.Empty<YieldPointPreview>();
            return _yields[request] = ProjectYields(world, ordinary);
        }
        public IReadOnlyList<YieldPointPreview> PreviewYield(ActionPlanId planId)
        {
            var p = Decision.OwnPlans.Concat(Decision.VisiblePlans).FirstOrDefault(plan => plan.ActionPlanId == planId.Value);
            if (p == null || p.Origin != (int)ActionPlanOrigin.Ordinary) return Array.Empty<YieldPointPreview>();
            // Existing plans use their committed identity/projection, including edits and movement dependencies.
            return ExistingYield(planId);
        }
        private readonly Dictionary<long, IReadOnlyList<YieldPointPreview>> _existingYields = new Dictionary<long, IReadOnlyList<YieldPointPreview>>();
        private IReadOnlyList<YieldPointPreview> ExistingYield(ActionPlanId id)
        {
            if (_existingYields.TryGetValue(id.Value, out var cached)) return cached;
            using var world = _fork();
            return _existingYields[id.Value] = ProjectYields(world, world.ScheduleAuthority.Registry.Find(id));
        }
        private IReadOnlyList<YieldPointPreview> ProjectYields(BattleSimulation world, ActionPlan ordinary)
        {
            var reactions = world.ScheduleAuthority.Registry.ActivePlans.Where(p => p.IsReaction && p.OwnerUnitId == ordinary.OwnerUnitId
                && p.SourceImpactTick < ordinary.StartTick && ordinary.StartTick < p.EndTick).ToArray();
            long end = Math.Min(ordinary.StartTick + 1, KnownHorizonTick ?? long.MaxValue);
            while (!world.IsEnded && world.Tick + 1 < end) Step(world);
            return reactions.OrderBy(p => p.ActionPlanId.Value).Select(p => new YieldPointPreview(p.ActionPlanId.Value,
                p.TerminationReason == ActionTerminationReason.ReactionYieldedLane ? p.TerminalTick : (long?)null)).ToList().AsReadOnly();
        }

        public IReadOnlyList<OpponentReactionCapability> OpponentCapabilities
        {
            get
            {
                if (_opponents != null) return _opponents;
                var results = new List<OpponentReactionCapability>();
                foreach (var attack in Decision.VisiblePlans.Where(p => Controls(p.OwnerUnitId)
                    && Decision.FindAction(new ActionSpecId(p.ActionSpecId))?.Type == ActionType.Attack).OrderBy(p => p.ActionPlanId))
                {
                    var source = _template.ScheduleAuthority.Registry.Find(new ActionPlanId(attack.ActionPlanId));
                    if (source == null) continue;
                    foreach (var target in _template.ReactionOpportunities.HudTargets(source).Where(t => !Controls(t.Value)))
                    {
                        var opportunity = _template.ReactionOpportunities.ActiveOpportunities.FirstOrDefault(o =>
                            o.SourceAttackPlanId.Value == attack.ActionPlanId && o.DefenderUnitId == target);
                        IReadOnlyList<ReactionFeasibility> options = opportunity == null ? Array.Empty<ReactionFeasibility>()
                            : opportunity.Options.Where(o => o.IsPublished)
                                .Select(o => EvaluateReaction(_template, opportunity, o.ReactionActionSpecId, null)).ToList().AsReadOnly();
                        bool recovery = Decision.VisiblePlans.Any(p => p.OwnerUnitId == target.Value
                            && Decision.FindAction(new ActionSpecId(p.ActionSpecId))?.Type == ActionType.Attack
                            && p.ImpactTick <= attack.ImpactTick && attack.ImpactTick < p.EndTick);
                        var result = new OpponentReactionCapability(attack.ActionPlanId, target.Value, options, recovery,
                            !KnownHorizonTick.HasValue || attack.ImpactTick < KnownHorizonTick);
                        results.Add(result);
                    }
                }
                return _opponents = results.AsReadOnly();
            }
        }
    }
}
