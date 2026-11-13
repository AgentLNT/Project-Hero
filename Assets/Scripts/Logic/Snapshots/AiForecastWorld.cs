using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Logic.Simulation
{
    public sealed partial class BattleSimulation
    {
        // Stage 18 is a decision boundary, before _tick/_currentSnapshot are committed in stage 19.
        // Reuse the HUD sanitizer with this explicit boundary; never hand AI a live simulation.
        internal IAiForecast CreateAiForecast(Snapshots.DecisionSnapshot decision)
        {
            if (decision == null || string.IsNullOrEmpty(decision.ControllerId.Value) || decision.Tick != _eventTick)
                throw new InvalidOperationException("AI_FORECAST_BOUNDARY_MISMATCH");
            var visible = new HashSet<long>(decision.VisiblePlans.Concat(decision.OwnPlans).Select(p => p.ActionPlanId));
            var template = CopyHudWorld(visible, decision.Tick);
            return new Snapshots.AiForecastWorld(decision, template, () => template.CopyHudWorld(visible));
        }

        // Called only on an already sanitized forecast world. Admit the proposed reply through
        // a real next Step, including window resets, without replaying the whole attack prefix.
        internal string PreviewAiDefenseReply(ControllerId controller, CommandRequest request)
        {
            var visible = new HashSet<long>(_scheduleAuthority.Registry.ActivePlans.Select(p => p.ActionPlanId.Value));
            using var preview = CopyHudWorld(visible);
            var entry = preview.CommandIngress.FindEntry(controller);
            if (entry == null || entry.Submit(request) != null) return "AI_DEFENSE_INGRESS_REJECTED";
            long tick = preview.Tick + 1;
            var result = preview.Step(tick, preview.CommandIngress.FreezeTick(tick));
            return result.Events.Events.OfType<CommandRejectedEvent>().FirstOrDefault()?.ReasonCode;
        }
    }
}

namespace ProjectHero.Logic.Snapshots
{
    internal sealed class AiForecastWorld : IAiDefenseForecast
    {
        internal const int LookaheadTicks = 120;
        private readonly DecisionSnapshot _decision;
        private readonly BattleSimulation _template;
        private readonly Func<BattleSimulation> _fork;
        public AiForecastWorld(DecisionSnapshot decision, BattleSimulation template, Func<BattleSimulation> fork)
        { _decision = decision; _template = template; _fork = fork; }
        public void Dispose() => _template.Dispose();

        public AiActionForecast Evaluate(CommandRequest request, AiForecastSettings settings = null)
            => Run(request, settings);

        private sealed record Reply(string Key, ControllerId Controller, long Boundary, CommandRequest Request, string RejectionCode);

        public IReadOnlyList<AiDefenseForecast> EvaluateDefenses(CommandRequest attack, AiForecastSettings settings)
        {
            if (settings == null || settings.Validate() != null) throw new LogicDefinitionException("AI_FORECAST_SETTINGS_INVALID");
            var replies = new List<Reply>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var contexts = new HashSet<string>(StringComparer.Ordinal);
            var control = Run(attack, settings, observe: Collect);
            if (control.RejectionCode != null || control.Coverage != AiForecastCoverage.Complete) return Array.Empty<AiDefenseForecast>();
            return replies.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r =>
            {
                var outcome = r.RejectionCode != null ? new AiActionForecast(r.RejectionCode, control.HorizonTick,
                    Array.Empty<UnitSnapshot>(), -1, 0, 0, Coverage: AiForecastCoverage.Rejected, ObservedThroughTick: r.Boundary + 1)
                    : Run(attack, settings, r);
                if (r.RejectionCode == null && outcome.RejectionCode != null) throw new LogicDefinitionException("AI_DEFENSE_ADMISSION_MISMATCH");
                return new AiDefenseForecast(r.Key, r.Controller, r.Boundary, r.Request, outcome);
            }).ToArray();

            void Collect(BattleSimulation world, long planId)
            {
                var plan = world.AiActionPlanLookup.FindPlan(new ActionPlanId(planId));
                if (plan == null || plan.ActionType != ActionType.Attack || plan.IsTerminal || world.Tick >= plan.ImpactTick) return;
                foreach (var binding in world.Definition.FindEncounter(world.EncounterId).Controllers.OrderBy(c => c.ControllerId.Value, StringComparer.Ordinal))
                {
                    var snapshot = world.DecisionSnapshotFor(binding.ControllerId);
                    var hostile = snapshot.ControlledUnitIds.Where(id => _decision.ControlledUnitIds.Any(own =>
                        _decision.Classify(own, id) == Factions.UnitRelation.Hostile)).ToArray();
                    // A reply can be prompted only by the candidate's genuinely published attack.
                    if (hostile.Length == 0 || !snapshot.VisiblePlans.Any(p => p.ActionPlanId == planId && DecisionSnapshot.IsPubliclyRevealed(p))) continue;
                    // Building public Dodge destinations is expensive. Reuse identical admission
                    // contexts before calling that port, while retaining next-Tick expiry boundaries.
                    string units = string.Join(";", snapshot.VisibleUnits.OrderBy(u => u.UnitId).Select(u =>
                        FormattableString.Invariant($"{u.UnitId}:{u.IsAlive}:{u.HealthQ10}:{u.X}:{u.Y}:{u.Facing}:{u.State}:{u.StateEndTick}:{u.StateEndTick <= snapshot.Tick + 1}")));
                    string resources = string.Join(";", snapshot.AdrenalineLedgers.OrderBy(l => l.UnitId).Select(l =>
                        FormattableString.Invariant($"{l.UnitId}:{l.CycleId}:{l.AvailableAdrenaline}:{l.ReservedTotal}")));
                    string effects = string.Join(";", world.CurrentSnapshot.Effects.OrderBy(e => e.EffectId).Select(e =>
                        FormattableString.Invariant($"{e.EffectId}:{e.UnitId}:{e.SpecId}:{e.EndTick}:{e.EndTick <= snapshot.Tick + 1}")));
                    string plans = string.Join(";", snapshot.VisiblePlans.Concat(snapshot.OwnPlans).GroupBy(p => p.ActionPlanId)
                        .Select(g => g.First()).OrderBy(p => p.ActionPlanId).Select(p =>
                            FormattableString.Invariant($"{p.ActionPlanId}:{p.State}:{p.StartTick}:{p.ImpactTick}:{p.EndTick}:{p.EndTick <= snapshot.Tick + 1}:{p.SourceImpactTick < snapshot.Tick + 1}")));
                    string opportunities = string.Join(";", world.ReactionOpportunities.ActiveOpportunities
                        .Where(o => o.SourceAttackPlanId.Value == planId && hostile.Contains(o.DefenderUnitId) && o.Options.Any(p => p.IsPublished))
                        .OrderBy(o => o.Id.Value).Select(o => o.Id.Value.ToString(CultureInfo.InvariantCulture) + ":" + o.State + ":"
                            + string.Join(",", o.Options.Where(p => p.IsPublished).OrderBy(p => p.ReactionActionSpecId.Value, StringComparer.Ordinal)
                                .Select(p => FormattableString.Invariant($"{p.ReactionActionSpecId.Value}:{p.IsOpen}:{p.ResponseDeadlineTick >= snapshot.Tick + 1}")))));
                    string admission = FormattableString.Invariant($"{binding.ControllerId.Value}/{snapshot.ScheduleRevision}/{snapshot.OwnWindow?.WindowId}/{snapshot.OwnWindow?.IsAcceptingSubmissions}/{world.Tick == plan.ImpactTick - 1}/{units}/{resources}/{effects}/{plans}/{opportunities}");
                    if (!contexts.Add(admission)) continue;
                    var context = AiDecisionContextFactory.Build(snapshot, binding.ControllerId, world.AiReactionOpportunities,
                        world.AiActionPlanLookup, world.MovementPathCalculator);
                    context = context with { Opportunities = context.Opportunities.Where(o => o.SourceAttackPlanId.Value == planId
                        && hostile.Contains(o.DefenderUnitId)).ToArray() };
                    var candidates = new List<AiTacticalCandidate>(AiTacticalCandidateBuilder.Reactions(context));
                    foreach (var guard in AiTacticalCandidateBuilder.Ordinary(context).Where(c => c.Type == ActionType.Guard))
                    {
                        var add = ((ScheduleEditPayload)guard.Request.Payload).Operations.OfType<AddOrdinaryPlanOperation>().Single();
                        if (!hostile.Contains(add.OwnerUnitId)) continue;
                        // Latest start permitted by this real window. The kernel resolves duration, lane and coverage.
                        long start = Math.Min(plan.ImpactTick, context.OwnWindow.EndTick - 1);
                        if (start < context.NextTick) continue;
                        candidates.Add(guard with { Key = guard.Key + "/at/" + start.ToString(CultureInfo.InvariantCulture),
                            Request = guard.Request with { Payload = new ScheduleEditPayload(new ScheduleEditOperation[] { add with { RequestedStartTick = start } }) } });
                    }
                    foreach (var candidate in candidates.OrderBy(c => c.Key, StringComparer.Ordinal))
                    {
                        // Retry only when public admission facts change, not at every identical boundary.
                        // This bounds a one-reply protocol; it is not a search over every legal submission time.
                        string facts = string.Join(";", snapshot.VisibleUnits.Where(u => hostile.Any(id => id.Value == u.UnitId))
                            .OrderBy(u => u.UnitId).Select(u => FormattableString.Invariant($"{u.UnitId}:{u.State}:{u.StateEndTick}:{u.X}:{u.Y}")));
                        string key = FormattableString.Invariant($"{binding.ControllerId.Value}/{candidate.Key}/w{context.OwnWindow?.WindowId}/r{snapshot.ScheduleRevision}/{facts}/resources/{resources}");
                        if (seen.Add(key)) replies.Add(new Reply(key, binding.ControllerId, world.Tick, candidate.Request,
                            world.PreviewAiDefenseReply(binding.ControllerId, candidate.Request)));
                    }
                }
            }
        }

        private AiActionForecast Run(CommandRequest request, AiForecastSettings settings, Reply reply = null,
            Action<BattleSimulation, long> observe = null)
        {
            if (settings?.Validate() is string invalid) throw new LogicDefinitionException(invalid);
            using var world = _fork();
            long horizon = checked(_decision.Tick + (settings?.LookaheadTicks ?? LookaheadTicks));
            string error = null;
            long start = 0, impact = 0, end = 0;
            long candidatePlanId = 0;
            long coveragePlanId = 0;
            long replyPlanId = 0;
            var own = new HashSet<long>(_decision.ControlledUnitIds.Select(u => u.Value));
            var hostile = new HashSet<long>(_decision.VisibleUnits.Where(u => own.Any(id =>
                _decision.Classify(new UnitId(id), new UnitId(u.UnitId)) == Factions.UnitRelation.Hostile)).Select(u => u.UnitId));
            if (request != null)
            {
                error = request.ValidateStaticStructure();
                if (error == null && request.TargetTick != _decision.Tick + 1) error = "AI_FORECAST_TARGET_TICK";
                if (error == null && !(request.Payload is ScheduleEditPayload) && !(request.Payload is ReactionCommandPayload))
                    error = CommandCodes.SCOPE_PAYLOAD_MISMATCH;
                if (error == null)
                {
                    // A candidate is a real next-tick submission in this isolated world. Directly
                    // applying it before Step misses window/resource resets and command-batch claims.
                    var entry = world.CommandIngress.FindEntry(_decision.ControllerId);
                    if (entry == null) error = "AI_FORECAST_CONTROLLER_MISSING";
                    else if (entry.Submit(request) != null) error = "AI_FORECAST_INGRESS_REJECTED";
                }
            }
            if (error != null) return new AiActionForecast(error, horizon, Array.Empty<UnitSnapshot>(), -1, 0, 0,
                Coverage: AiForecastCoverage.Rejected, ObservedThroughTick: world.Tick);
            var threats = _decision.VisiblePlans.Where(p => hostile.Contains(p.OwnerUnitId)
                && _decision.FindAction(new ActionSpecId(p.ActionSpecId))?.Type == ActionType.Attack)
                .ToDictionary(p => p.ActionPlanId, p => p.ImpactTick);
            var interrupted = new HashSet<long>(); var clashes = new HashSet<long>();
            long firstHit = -1;
            while (!world.IsEnded && world.Tick < horizon)
            {
                var before = world.CurrentSnapshot.Units.Where(u => hostile.Contains(u.UnitId)).Sum(u => (long)u.HealthQ10);
                long tick = world.Tick + 1;
                var result = world.Step(tick, world.CommandIngress.FreezeTick(tick));
                if (request != null && tick == request.TargetTick)
                {
                    var rejected = result.Events.Events.OfType<CommandRejectedEvent>().FirstOrDefault();
                    if (rejected != null)
                        return new AiActionForecast(rejected.ReasonCode, horizon, Array.Empty<UnitSnapshot>(), -1, 0, 0,
                            Coverage: AiForecastCoverage.Rejected, ObservedThroughTick: world.Tick);
                    var accepted = result.Events.Events.OfType<ActionPlanCreatedEvent>().LastOrDefault();
                    if (accepted != null) coveragePlanId = accepted.ActionPlanId;
                    if (request.Payload is ScheduleEditPayload)
                    {
                        var created = result.Events.Events.OfType<ActionPlanCreatedEvent>().LastOrDefault(e => e.Origin == (int)ActionPlanOrigin.Ordinary);
                        if (created != null)
                        {
                            candidatePlanId = created.ActionPlanId; start = created.StartTick; end = created.EndTick;
                            var timing = world.Definition.FindAction(new ActionSpecId(created.ActionSpecId)).Timing as AttackTimingSpec;
                            impact = timing == null ? 0 : end - timing.RecoveryTicks;
                        }
                    }
                }
                if (candidatePlanId != 0)
                {
                    var plan = result.Snapshot.Plans.FirstOrDefault(p => p.ActionPlanId == candidatePlanId);
                    if (plan != null) { start = plan.StartTick; impact = plan.ImpactTick; end = plan.EndTick; }
                }
                long after = result.Snapshot.Units.Where(u => hostile.Contains(u.UnitId)).Sum(u => (long)u.HealthQ10);
                if (firstHit < 0 && after < before) firstHit = tick;
                foreach (var fact in result.Events.Events)
                {
                    if (fact is ActionPlanTerminatedEvent terminated && threats.TryGetValue(terminated.ActionPlanId, out long due)
                        && terminated.Tick < due) interrupted.Add(terminated.ActionPlanId);
                    if (fact is ClashParticipantResolvedEvent clash && hostile.Contains(clash.OwnerUnitId.Value))
                        clashes.Add(clash.ActionPlanId.Value);
                }
                if (reply != null && tick == reply.Request.TargetTick)
                {
                    var rejected = result.Events.Events.OfType<CommandRejectedEvent>().FirstOrDefault();
                    if (rejected != null) return new AiActionForecast(rejected.ReasonCode, horizon, Array.Empty<UnitSnapshot>(), -1, 0, 0,
                        Coverage: AiForecastCoverage.Rejected, ObservedThroughTick: world.Tick);
                    replyPlanId = result.Events.Events.OfType<ActionPlanCreatedEvent>().LastOrDefault()?.ActionPlanId ?? 0;
                    if (replyPlanId == 0) throw new LogicDefinitionException("AI_DEFENSE_REPLY_NOT_CREATED");
                }
                if (reply != null && tick == reply.Boundary)
                {
                    var entry = world.CommandIngress.FindEntry(reply.Controller);
                    if (entry == null || entry.Submit(reply.Request) != null)
                        return new AiActionForecast("AI_DEFENSE_INGRESS_REJECTED", horizon, Array.Empty<UnitSnapshot>(), -1, 0, 0,
                            Coverage: AiForecastCoverage.Rejected, ObservedThroughTick: world.Tick);
                }
                if (!world.IsEnded && candidatePlanId != 0) observe?.Invoke(world, candidatePlanId);
            }
            var candidatePlan = coveragePlanId == 0 ? null : world.AiActionPlanLookup.FindPlan(new ActionPlanId(coveragePlanId));
            bool complete = request == null || world.IsEnded || candidatePlan?.IsTerminal == true;
            var replyPlan = replyPlanId == 0 ? null : world.AiActionPlanLookup.FindPlan(new ActionPlanId(replyPlanId));
            if (reply != null && !world.IsEnded) complete &= replyPlan?.IsTerminal == true;
            return new AiActionForecast(null, horizon, world.CurrentSnapshot.Units, firstHit, interrupted.Count, clashes.Count, start, impact, end,
                complete ? AiForecastCoverage.Complete : AiForecastCoverage.Uncovered, world.Tick,
                Math.Max(candidatePlan?.EndTick ?? 0, replyPlan?.EndTick ?? 0), candidatePlan == null ? 0 : (int)candidatePlan.TerminationReason);
        }
    }
}
