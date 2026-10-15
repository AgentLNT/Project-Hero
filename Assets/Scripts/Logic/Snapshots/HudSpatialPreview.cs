using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Movement;

namespace ProjectHero.Logic.Snapshots
{
    public sealed record SpatialUnitPreview(long UnitId, GridPoint Position, GridDirection Facing, bool IsUnknown,
        IReadOnlyList<TrianglePoint> Footprint, IReadOnlyList<GridPoint> Path);
    public sealed record SpatialAttackPreview(long PlanId, long TemporaryPlanKey, long OwnerUnitId, long ImpactTick,
        GridPoint Origin, GridDirection Facing, IReadOnlyList<TrianglePoint> Area, bool IsDraft);
    public sealed record SpatialPreview(long Tick, long? KnownHorizonTick, string RejectionCode,
        IReadOnlyList<SpatialUnitPreview> Units, IReadOnlyList<SpatialAttackPreview> Attacks);
    public sealed record AttackDamagePreview(long PlanId, long TemporaryPlanKey, long TargetUnitId, long Tick, long DamageQ10);
    public sealed record KnockbackPreview(long Tick, long TargetUnitId, GridPoint From, GridPoint To,
        GridDirection Direction, int RequestedSteps, int AppliedSteps, ForcedDisplacementStopReason StopReason,
        IReadOnlyList<long> SourcePlanIds, IReadOnlyList<long> TemporaryPlanKeys);
    public sealed record SpatialResultsPreview(string RejectionCode, long? KnownHorizonTick,
        IReadOnlyList<AttackDamagePreview> Damage, IReadOnlyList<KnockbackPreview> Knockbacks, IReadOnlyList<TimelineContactPreview> Contacts);

    public sealed partial class HudPreviewService
    {
        public long? PreviewPlanningStart(UnitId owner, long? requestedStart,
            IReadOnlyList<ScheduleEditOperation> operations)
        {
            if (!Controls(owner.Value)) return null;
            long tick;
            if (requestedStart.HasValue) tick = Math.Max(Decision.Tick, requestedStart.Value);
            else
            {
                using var world = _fork();
                if (ApplyPlacements(world, operations, Array.Empty<ReactionPlacement>()) != null) return null;
                tick = Math.Max(Decision.Tick + 1, world.ScheduleAuthority.Registry.ActivePlans
                    .Where(p => p.OwnerUnitId == owner).Select(p => p.EndTick).DefaultIfEmpty(Decision.Tick + 1).Max());
            }
            return tick;
        }
        private ScheduleEditOperation[] _spatialOperations;
        private ReactionPlacement[] _spatialReactions;
        private readonly Dictionary<long, SpatialPreview> _spatialFrames = new Dictionary<long, SpatialPreview>();
        private SpatialResultsPreview _spatialResults;
        private ScheduleEditOperation[] _resultOperations;
        private ReactionPlacement[] _resultReactions;

        private void SelectSpatialRequest(IReadOnlyList<ScheduleEditOperation> operations, IReadOnlyList<ReactionPlacement> reactions)
        {
            if (_spatialOperations != null && _spatialOperations.SequenceEqual(operations) && _spatialReactions.SequenceEqual(reactions)) return;
            _spatialOperations = operations.ToArray(); _spatialReactions = reactions.ToArray();
            _spatialFrames.Clear();
        }

        /// <summary>Planned geometry only: discrete movement boundaries and canonical direction tables.
        /// Combat outcomes deliberately remain in PreviewSpatialResults, never in cursor ghosts.</summary>
        public SpatialPreview PreviewSpace(long tick, IReadOnlyList<ScheduleEditOperation> operations = null,
            IReadOnlyList<ReactionPlacement> reactions = null)
        {
            operations ??= Array.Empty<ScheduleEditOperation>(); reactions ??= Array.Empty<ReactionPlacement>();
            SelectSpatialRequest(operations, reactions);
            if (tick < Decision.Tick || tick - Decision.Tick > 3600)
                return new SpatialPreview(tick, KnownHorizonTick, "SPATIAL_CURSOR_OUT_OF_RANGE", Array.Empty<SpatialUnitPreview>(), Array.Empty<SpatialAttackPreview>());
            if (_spatialFrames.TryGetValue(tick, out var cached)) return cached;
            using var world = _fork();
            var temporary = new Dictionary<long, long>();
            string error = ApplyPlacements(world, operations, reactions, temporary);
            if (error != null) return new SpatialPreview(tick, KnownHorizonTick, error, Array.Empty<SpatialUnitPreview>(), Array.Empty<SpatialAttackPreview>());
            var plans = world.ScheduleAuthority.Registry.ActivePlans.OrderBy(p => p.StartTick).ThenBy(p => p.ActionPlanId.Value).ToArray();
            var segments = world.MovementAuthority.AllSegmentsOrdered();
            var units = new List<SpatialUnitPreview>();
            foreach (var unit in Decision.VisibleUnits.Where(u => u.IsAlive).OrderBy(u => u.UnitId))
            {
                bool own = Controls(unit.UnitId);
                bool unknown = !own && tick > Decision.Tick && ((KnownHorizonTick.HasValue && tick >= KnownHorizonTick.Value)
                    || !plans.Any(p => p.OwnerUnitId.Value == unit.UnitId && p.StartTick <= Decision.Tick));
                var position = new GridPoint(unit.X, unit.Y); var facing = (GridDirection)unit.Facing;
                var path = new List<GridPoint> { position };
                if (!unknown)
                {
                    foreach (var segment in segments.Where(s => s.UnitId.Value == unit.UnitId).OrderBy(s => s.StartTick).ThenBy(s => s.StepIndex))
                    {
                        // Occupancy remains From until EndTick. Never interpolate logical coordinates.
                        if (segment.EndTick <= tick) position = segment.To;
                        if (own && segment.StartTick <= tick) path.Add(segment.EndTick <= tick ? segment.To : segment.From);
                    }
                    // Ordinary actions carry an aim direction; they do not rotate the unit's
                    // occupied volume. Movement CommitAnchor preserves its committed facing.
                }
                units.Add(new SpatialUnitPreview(unit.UnitId, position, facing, unknown,
                    VolumeFootprint.ResolveTriangles(Decision.Definition.VolumeDirectionsOf(new UnitDefinitionId(unit.DefinitionId)), position, facing),
                    path.ToArray()));
            }
            var attacks = new List<SpatialAttackPreview>();
            foreach (var plan in plans.Where(p => p.ActionType == ActionType.Attack && p.StartTick <= tick && tick <= p.ImpactTick))
            {
                var owner = units.FirstOrDefault(u => u.UnitId == plan.OwnerUnitId.Value);
                if (owner == null || owner.IsUnknown) continue;
                var payload = (AttackPayloadSpec)Decision.FindAction(plan.ActionSpecId).Payload;
                long key = temporary.TryGetValue(plan.ActionPlanId.Value, out long value) ? value : 0;
                attacks.Add(new SpatialAttackPreview(plan.ActionPlanId.Value, key, plan.OwnerUnitId.Value, plan.ImpactTick,
                    owner.Position, plan.Facing, VolumeFootprint.ResolveTriangles(payload.Pattern.Directions, owner.Position, plan.Facing), key != 0));
            }
            // Bound a scrub session's memoized geometry without retaining unbounded worlds or history.
            if (_spatialFrames.Count >= 64) _spatialFrames.Clear();
            return _spatialFrames[tick] = new SpatialPreview(tick, KnownHorizonTick, null, units.AsReadOnly(), attacks.AsReadOnly());
        }

        /// <summary>Conditional outcomes from the production Step solver, including global simultaneous
        /// displacement and its exact obstacle/boundary/occupancy stop reason. No AI or private input.</summary>
        public SpatialResultsPreview PreviewSpatialResults(IReadOnlyList<ScheduleEditOperation> operations = null,
            IReadOnlyList<ReactionPlacement> reactions = null)
        {
            operations ??= Array.Empty<ScheduleEditOperation>(); reactions ??= Array.Empty<ReactionPlacement>();
            if (_spatialResults != null && _resultOperations.SequenceEqual(operations) && _resultReactions.SequenceEqual(reactions)) return _spatialResults;
            _resultOperations = operations.ToArray(); _resultReactions = reactions.ToArray();
            using var world = _fork(); var temporary = new Dictionary<long, long>();
            string error = ApplyPlacements(world, operations, reactions, temporary);
            var damage = new List<AttackDamagePreview>(); var knockbacks = new List<KnockbackPreview>();
            if (error != null) return _spatialResults = new SpatialResultsPreview(error, KnownHorizonTick, damage.AsReadOnly(), knockbacks.AsReadOnly(), Array.Empty<TimelineContactPreview>());
            long last = world.ScheduleAuthority.Registry.ActivePlans.Where(p => p.ActionType == ActionType.Attack).Select(p => p.ImpactTick).DefaultIfEmpty(Decision.Tick).Max();
            long end = Math.Min(last, Math.Min(KnownHorizonTick.HasValue ? KnownHorizonTick.Value - 1 : Decision.Tick + 3600, Decision.Tick + 3600));
            while (!world.IsEnded && world.Tick < end)
            {
                var result = Step(world);
                var hits = result.Events.EventsInSequenceOrder.OfType<DamageChannelResolvedEvent>().ToArray();
                foreach (var hit in hits)
                    damage.Add(new AttackDamagePreview(hit.AttackPlanId.Value, temporary.TryGetValue(hit.AttackPlanId.Value, out long key) ? key : 0,
                        hit.TargetUnitId.Value, hit.Tick, hit.AfterBlockDamageQ10));
                foreach (var displacement in result.Events.EventsInSequenceOrder.OfType<ForcedDisplacementResolvedEvent>())
                {
                    var sources = hits.Where(h => h.TargetUnitId == displacement.TargetUnitId && h.AfterMomentumResistanceUnits > 0).Select(h => h.AttackPlanId.Value)
                        .Concat(result.Events.EventsInSequenceOrder.OfType<ClashResidualImpactResolvedEvent>()
                            .Where(c => c.RecipientUnitId == displacement.TargetUnitId && c.ResidualMomentumUnits > 0).Select(c => c.SourceActionPlanId.Value))
                        .Distinct().OrderBy(id => id).ToArray();
                    knockbacks.Add(new KnockbackPreview(displacement.Tick, displacement.TargetUnitId.Value, displacement.From, displacement.To,
                        displacement.Direction, displacement.RequestedSteps, displacement.AppliedSteps, displacement.StopReason, sources,
                        sources.Where(temporary.ContainsKey).Select(id => temporary[id]).ToArray()));
                }
            }
            return _spatialResults = new SpatialResultsPreview(null, KnownHorizonTick, damage.AsReadOnly(), knockbacks.AsReadOnly(),
                PreviewTimelineContacts(operations, reactions).Contacts);
        }
    }
}
