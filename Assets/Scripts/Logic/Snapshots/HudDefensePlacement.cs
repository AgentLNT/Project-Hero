using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Snapshots
{
    public sealed record ReactionPlacement(long OpportunityId, ActionSpecId Action, long StartTick, GridPoint? Destination = null);
    public sealed record DodgeLandingPreview(long Tick, bool DestinationCommitted, int StillCoveredAttacks, int RetainedUndodgeableAttacks);
    public sealed record DefensePlanPreview(ActionPlanSnapshot Plan, DefenseContactPreview Contact, long TemporaryPlanKey = 0,
        DodgeLandingPreview DodgeLanding = null, IReadOnlyList<long> ConditionalMovePlanIds = null);
    public sealed record DefensePlacementPreview(string RejectionCode, IReadOnlyList<DefensePlanPreview> Defenses);

    public sealed partial class HudPreviewService
    {
        private ScheduleEditOperation[] _defenseOperations;
        private ReactionPlacement[] _defenseReactions;
        private DefensePlacementPreview _defensePlacement;

        // Matches the HUD's resume order: ordinary transaction, then reactions in player order.
        // Every prediction uses the existing filtered fork, acceptance rules and Step solver.
        private string ApplyPlacements(BattleSimulation world, IReadOnlyList<ScheduleEditOperation> operations,
            IReadOnlyList<ReactionPlacement> reactions, Dictionary<long, long> temporary = null)
        {
            long tick = Decision.Tick + world.Definition.ReactionRules.CommandIngressLeadTicks;
            if (operations.Count > 0)
            {
                var applied = world.ScheduleEditor.Apply(operations, tick, world.ScheduleRevision, world.ScheduleRevision,
                    expectedWindowId: world.CurrentTurnWindow?.WindowId, issuer: Decision.ControllerId);
                if (!applied.Succeeded) return applied.RejectionCode;
                world.CloseHudScheduleTransaction(applied, tick);
                for (int i = 0; i < operations.Count; i++)
                    if (operations[i] is AddOrdinaryPlanOperation add && temporary != null)
                        temporary[applied.AppliedEdits[i].PlanId.Value] = add.TemporaryPlanKey;
            }
            foreach (var placement in reactions)
            {
                var opportunity = world.ReactionOpportunities.ActiveOpportunities.FirstOrDefault(o => o.Id.Value == placement.OpportunityId);
                if (opportunity == null || !Controls(opportunity.DefenderUnitId.Value)) return ReactionCodes.OPPORTUNITY_NOT_OPEN;
                string error = world.ReactionOpportunities.TryAcceptById(opportunity.Id, placement.Action, tick,
                    placement.Destination, out _, placement.StartTick);
                if (error != null) return error;
            }
            return null;
        }

        public ReactionFeasibility PreviewReactionPlacement(ReactionPlacement placement,
            IReadOnlyList<ScheduleEditOperation> operations, IReadOnlyList<ReactionPlacement> staged)
        {
            using var world = _fork();
            string error = ApplyPlacements(world, operations, staged.Where(r => r.OpportunityId != placement.OpportunityId).ToArray());
            var opportunity = world.ReactionOpportunities.ActiveOpportunities.FirstOrDefault(o => o.Id.Value == placement.OpportunityId);
            if (opportunity == null || !Controls(opportunity.DefenderUnitId.Value))
                return new ReactionFeasibility(placement.OpportunityId, placement.Action.Value, 0, 0,
                    ReactionCodes.OPPORTUNITY_NOT_OPEN, Array.Empty<ActionPlanSnapshot>(), 0, 0);
            var result = EvaluateReaction(world, opportunity, placement.Action, placement.StartTick);
            return error == null ? result : result with { RejectionCode = error };
        }

        public DefensePlacementPreview PreviewDefensePlacements(IReadOnlyList<ScheduleEditOperation> operations,
            IReadOnlyList<ReactionPlacement> reactions)
        {
            if (_defenseOperations != null && _defenseOperations.SequenceEqual(operations) && _defenseReactions.SequenceEqual(reactions)) return _defensePlacement;
            _defenseOperations = operations.ToArray(); _defenseReactions = reactions.ToArray();
            using var world = _fork();
            var temporary = new Dictionary<long, long>();
            string error = ApplyPlacements(world, operations, reactions, temporary);
            if (error != null) return _defensePlacement = new DefensePlacementPreview(error, Array.Empty<DefensePlanPreview>());
            var plans = world.ScheduleAuthority.Registry.ActivePlans.Where(p => p.ActionType == ActionType.Guard
                || p.ActionType == ActionType.Block || p.ActionType == ActionType.Dodge).OrderBy(p => p.ActionPlanId.Value).ToArray();
            var snapshots = plans.ToDictionary(p => p.ActionPlanId.Value, p => ActionPlanSnapshot.From(p));
            var moves = plans.Where(p => p.ActionType == ActionType.Dodge).ToDictionary(p => p.ActionPlanId.Value,
                p => (IReadOnlyList<long>)world.DodgeMovementInvalidation.QueryFutureEditableMoves(p.OwnerUnitId, p.StartTick).Select(m => m.ActionPlanId.Value).ToArray());
            var landings = new Dictionary<long, DodgeLandingPreview>();
            var guards = plans.ToDictionary(p => p.ActionPlanId.Value, _ => new List<GuardContactPreview>());
            var triggers = new Dictionary<long, long>(); var sources = new Dictionary<long, IReadOnlyList<long>>();
            long end = Math.Min(plans.Select(p => p.EndTick).DefaultIfEmpty(Decision.Tick).Max(), KnownHorizonTick ?? long.MaxValue);
            end = Math.Min(end, Decision.Tick + 3600);
            while (!world.IsEnded && world.Tick + 1 < end && plans.Any(p => !p.IsTerminal))
            {
                var step = Step(world);
                foreach (var fact in step.Events.EventsInSequenceOrder)
                    if (fact is DodgeResolvedEvent dodge && snapshots.ContainsKey(dodge.DodgePlanId.Value))
                        landings[dodge.DodgePlanId.Value] = new DodgeLandingPreview(dodge.Tick, dodge.DestinationCommitted,
                            dodge.StillHitAttackPlanIds.Except(dodge.RetainedUndodgeablePlanIds).Count(), dodge.RetainedUndodgeablePlanIds.Count);
                    else if (fact is ReactionTriggeredEvent reaction && snapshots.ContainsKey(reaction.ReactionActionPlanId))
                    {
                        triggers[reaction.ReactionActionPlanId] = reaction.Tick;
                        sources[reaction.ReactionActionPlanId] = SourcesFor(world, plans.First(p => p.ActionPlanId.Value == reaction.ReactionActionPlanId));
                    }
                    else if (fact is GuardQuotaConsumedEvent guard && guards.ContainsKey(guard.PlanId.Value))
                        guards[guard.PlanId.Value].Add(new GuardContactPreview(guard.Tick, guard.DebitQ10, guard.RemainingQ10,
                            SourcesFor(world, plans.First(p => p.ActionPlanId == guard.PlanId))));
            }
            var results = plans.Select(p => {
                long id = p.ActionPlanId.Value; long? trigger = triggers.TryGetValue(id, out var t) ? t : (long?)null;
                return new DefensePlanPreview(snapshots[id], new DefenseContactPreview(trigger,
                    sources.TryGetValue(id, out var s) ? s : Array.Empty<long>(), trigger.HasValue && trigger < p.SourceImpactTick,
                    guards[id].AsReadOnly(), p.TerminationReason == ActionTerminationReason.ReactionYieldedLane ? p.TerminalTick : (long?)null,
                    KnownHorizonTick, null), temporary.TryGetValue(id, out var key) ? key : 0,
                    landings.TryGetValue(id, out var landing) ? landing : null, moves.TryGetValue(id, out var invalidated) ? invalidated : Array.Empty<long>());
            }).ToArray();
            return _defensePlacement = new DefensePlacementPreview(null, results);
        }
    }
}
