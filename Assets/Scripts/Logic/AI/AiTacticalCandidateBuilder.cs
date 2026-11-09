using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.AI
{
    public sealed record AiTacticalCandidate(string Key, ActionType Type, CommandRequest Request, bool IsReaction, int Cost);

    /// <summary>Candidate generation has no policy weights. Every candidate still needs the authoritative preview.</summary>
    public static class AiTacticalCandidateBuilder
    {
        public static IReadOnlyList<AiTacticalCandidate> Reactions(AiDecisionContext context)
        {
            var result = new List<AiTacticalCandidate>();
            foreach (var option in AiReactionCandidateBuilder.BuildReactionCandidatesOrdered(context, context.Snapshot.Tick))
            {
                var destinations = option.ReactionKind == ReactionCommandKind.Dodge
                    ? option.PublishedDodgeDestinations.Select(p => (GridPoint?)p).Distinct().ToArray()
                    : new GridPoint?[] { null };
                foreach (var destination in destinations)
                {
                    string key = string.Format(CultureInfo.InvariantCulture, "R/{0}/{1}/{2}/{3}/{4}",
                        option.ReactionOpportunityId.Value, option.ActionSpecId.Value, option.StartTick, destination?.X, destination?.Y);
                    var request = new CommandRequest(context.NextTick, new ReactionCommandScope(option.ReactionOpportunityId),
                        new ReactionCommandPayload(option.ReactionKind, option.ActionSpecId, destination, option.StartTick));
                    var action = context.Snapshot.FindAction(option.ActionSpecId);
                    result.Add(new AiTacticalCandidate(key, action.Type, request, true, action.AdrenalineCost));
                }
            }
            return result.OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
        }

        public static IReadOnlyList<AiTacticalCandidate> Ordinary(AiDecisionContext context)
        {
            var result = new List<AiTacticalCandidate>();
            if (!context.HasSubmissionWindow) return result;
            foreach (var owner in context.ControlledUnits.OrderBy(u => u.Value))
            {
                // One commitment at a time: retain recovery/defense instead of queuing speculative plans.
                if (context.OwnWindow.OwnerUnitId != owner.Value || context.LaneTailStartTickOf(owner) > context.NextTick) continue;
                var unit = context.Snapshot.VisibleUnits.FirstOrDefault(u => u.UnitId == owner.Value && u.IsAlive);
                if (unit == null) continue;
                foreach (var id in context.Snapshot.ActionSetOf(owner).OrderBy(a => a.Value, StringComparer.Ordinal))
                {
                    var spec = context.Snapshot.FindAction(id);
                    if (spec?.Type == ActionType.Attack)
                    {
                        foreach (var target in context.Candidates.Inspect(spec, owner, context.Snapshot.VisibleUnitIds).OrderBy(t => t.UnitId.Value))
                        {
                            if (context.Candidates.EligibilityOf(spec, owner, target.UnitId) != null) continue;
                            var targetUnit = context.Snapshot.VisibleUnits.First(t => t.UnitId == target.UnitId.Value);
                            Add(owner, spec, target.UnitId, Aim(targetUnit.X - unit.X, targetUnit.Y - unit.Y), null);
                        }
                    }
                    else if (spec?.Type == ActionType.Guard) Add(owner, spec, null, (GridDirection)unit.Facing, null);
                    else if (spec?.Type == ActionType.Move)
                    {
                        // Proactive destinations, not edits of an existing Move. The shared solver decides paths/occupancy.
                        for (int radius = 1; radius <= 2; radius++)
                            for (int d = 0; d < GridNeighborTable.DirectionCount; d++)
                                Add(owner, spec, null, (GridDirection)unit.Facing,
                                    new GridPoint(unit.X + radius * GridNeighborTable.OffsetX((GridDirection)d),
                                        unit.Y + radius * GridNeighborTable.OffsetY((GridDirection)d)));
                    }
                }
            }
            return result.OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();

            void Add(UnitId owner, ActionSpec spec, UnitId? target, GridDirection facing, GridPoint? destination)
            {
                string key = string.Format(CultureInfo.InvariantCulture, "N/{0}/{1}/{2}/{3}/{4}/{5}",
                    owner.Value, spec.ActionSpecId.Value, target?.Value, (int)facing, destination?.X, destination?.Y);
                var operation = new AddOrdinaryPlanOperation(1, owner, spec.ActionSpecId,
                    PrimaryTargetUnitId: target, Facing: facing, Destination: destination);
                result.Add(new AiTacticalCandidate(key, spec.Type,
                    new CommandRequest(context.NextTick, new ScheduleEditScope(context.Snapshot.ScheduleRevision, context.ExpectedWindowId),
                        new ScheduleEditPayload(new ScheduleEditOperation[] { operation })), false, 0));
            }
        }

        public static IReadOnlyList<AiTacticalCandidate> Aligned(AiDecisionContext context, AiTacticalCandidate attack, AiActionForecast preview)
        {
            var result = new List<AiTacticalCandidate>();
            if (attack.Type != ActionType.Attack || preview.RejectionCode != null) return result;
            var payload = (ScheduleEditPayload)attack.Request.Payload;
            var add = (AddOrdinaryPlanOperation)payload.Operations[0];
            // Resolve the windup through ScheduleEditor first; do not reproduce speed/timing formulas here.
            foreach (long due in context.Snapshot.VisiblePlans.Where(p => context.Snapshot.Classify(add.OwnerUnitId, new UnitId(p.OwnerUnitId)) == Factions.UnitRelation.Hostile
                && Snapshots.DecisionSnapshot.IsPubliclyRevealed(p) && p.ImpactTick > preview.PlanImpactTick
                && context.Snapshot.FindAction(new ActionSpecId(p.ActionSpecId))?.Type == ActionType.Attack)
                .Select(p => p.ImpactTick).Distinct().OrderBy(t => t).Take(2))
            {
                long requested = preview.PlanStartTick + due - preview.PlanImpactTick;
                if (requested >= context.OwnWindow.EndTick || due > preview.HorizonTick) continue;
                result.Add(attack with { Key = attack.Key + "/align/" + due.ToString(CultureInfo.InvariantCulture),
                    Request = attack.Request with { Payload = new ScheduleEditPayload(new ScheduleEditOperation[] {
                        add with { RequestedStartTick = requested } }) } });
            }
            return result;
        }

        private static GridDirection Aim(int dx, int dy)
        {
            int best = 0; long bestDot = -1, bestLength = 1;
            for (int d = 0; d < GridNeighborTable.DirectionCount; d++)
            {
                long x = GridNeighborTable.OffsetX((GridDirection)d), y = GridNeighborTable.OffsetY((GridDirection)d);
                long dot = dx * x + dy * y, length = x * x + y * y;
                if (dot > 0 && (bestDot < 0 || dot * dot * bestLength > bestDot * bestDot * length))
                { best = d; bestDot = dot; bestLength = length; }
            }
            return (GridDirection)best;
        }
    }
}
