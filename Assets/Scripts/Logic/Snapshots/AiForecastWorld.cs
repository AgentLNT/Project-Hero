using System;
using System.Collections.Generic;
using System.Linq;
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
    }
}

namespace ProjectHero.Logic.Snapshots
{
    internal sealed class AiForecastWorld : IAiForecast
    {
        internal const int LookaheadTicks = 120;
        private readonly DecisionSnapshot _decision;
        private readonly BattleSimulation _template;
        private readonly Func<BattleSimulation> _fork;
        public AiForecastWorld(DecisionSnapshot decision, BattleSimulation template, Func<BattleSimulation> fork)
        { _decision = decision; _template = template; _fork = fork; }
        public void Dispose() => _template.Dispose();

        public AiActionForecast Evaluate(CommandRequest request, AiForecastSettings settings = null)
        {
            if (settings?.Validate() is string invalid) throw new LogicDefinitionException(invalid);
            using var world = _fork();
            long horizon = checked(_decision.Tick + (settings?.LookaheadTicks ?? LookaheadTicks));
            string error = null;
            long start = 0, impact = 0, end = 0;
            long candidatePlanId = 0;
            long coveragePlanId = 0;
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
            }
            var candidatePlan = coveragePlanId == 0 ? null : world.AiActionPlanLookup.FindPlan(new ActionPlanId(coveragePlanId));
            bool complete = request == null || world.IsEnded || candidatePlan?.IsTerminal == true;
            return new AiActionForecast(null, horizon, world.CurrentSnapshot.Units, firstHit, interrupted.Count, clashes.Count, start, impact, end,
                complete ? AiForecastCoverage.Complete : AiForecastCoverage.Uncovered, world.Tick,
                candidatePlan?.EndTick ?? 0, candidatePlan == null ? 0 : (int)candidatePlan.TerminationReason);
        }
    }
}
