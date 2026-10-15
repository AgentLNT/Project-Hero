using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.UnityView
{
    /// <summary>HUD-only selection/history. Unit IDs follow ordinal encounter slots, then monotonic spawn order.</summary>
    public sealed class TrackSelectionModel
    {
        private readonly HashSet<long> _selected = new HashSet<long>(), _temporary = new HashSet<long>();
        private readonly List<long> _history = new List<long>();
        public TrackLayoutModel Layout { get; } = new TrackLayoutModel();
        public long Primary { get; private set; }
        public int Revision { get; private set; }
        public bool Contains(long id) => _selected.Contains(id);
        public bool IsTemporary(long id) => _temporary.Contains(id);
        public void Reset() { _selected.Clear(); _temporary.Clear(); _history.Clear(); Primary = 0; Revision++; }
        public void Click(long id, bool control)
        {
            if (Contains(id)) { Remove(id); return; }
            if (!control) { _selected.Clear(); _temporary.Clear(); _history.Clear(); }
            Focus(id);
        }
        public void Focus(long id, bool user = true)
        {
            if (id <= 0) return;
            _selected.Add(id); if (user) _temporary.Remove(id);
            _history.Remove(id); _history.Add(id); Primary = id; Revision++;
        }
        public void Remove(long id)
        {
            if (!_selected.Remove(id)) return;
            _temporary.Remove(id); _history.Remove(id);
            if (Primary == id) Primary = _history.LastOrDefault(v => _selected.Contains(v));
            Revision++;
        }
        public void AddTemporary(long id)
        { if (id <= 0 || Contains(id)) return; _selected.Add(id); _temporary.Add(id); Revision++; }
        public void SyncTemporary(IEnumerable<long> active)
        { var keep = new HashSet<long>(active); foreach (long id in _temporary.ToArray()) if (!keep.Contains(id)) Remove(id); }
        public void Prune(DecisionSnapshot decision)
        { var alive = new HashSet<long>(decision.VisibleUnits.Where(u => u.IsAlive).Select(u => u.UnitId)); foreach (long id in _selected.ToArray()) if (!alive.Contains(id)) Remove(id); }
        public IReadOnlyList<UnitSnapshot> Ordered(DecisionSnapshot decision, bool selectedOnly = true)
            => Layout.Order(decision, id => !selectedOnly || Contains(id));
        public void Cycle(DecisionSnapshot decision, bool reverse)
        {
            var ordered = Ordered(decision); if (ordered.Count == 0) return;
            int index = ordered.ToList().FindIndex(u => u.UnitId == Primary);
            Focus(ordered[(index + (reverse ? -1 : 1) + ordered.Count) % ordered.Count].UnitId);
        }
        public static int Group(DecisionSnapshot decision, long unit)
        {
            if (decision.ControlledUnitIds.Any(u => u.Value == unit)) return 0;
            if (decision.ControlledUnitIds.Count == 0) return 2;
            var relation = decision.Classify(decision.ControlledUnitIds[0], new UnitId(unit));
            return relation == UnitRelation.Allied || relation == UnitRelation.Self ? 1 : relation == UnitRelation.Hostile ? 3 : 2;
        }
    }

    public readonly struct TimelineSnapResult
    {
        public readonly long StartTick, AnchorTick;
        public readonly bool Snapped;
        public TimelineSnapResult(long start, long anchor = 0, bool snapped = false) { StartTick = start; AnchorTick = anchor; Snapped = snapped; }
    }
    public static class TimelineSnap
    {
        /// <summary>Only projected/public plans supplied by the HUD may be anchors. No overlap/layout simulation.</summary>
        public static TimelineSnapResult Resolve(ActionPlanSnapshot moving, long desired, IEnumerable<ActionPlanSnapshot> visible,
            long minimum, long maximum, int thresholdTicks, Func<string, bool> isAttack)
        {
            if (desired < minimum || desired > maximum) return new TimelineSnapResult(desired);
            var offsets = new List<long> { 0, moving.EndTick - moving.StartTick };
            if (isAttack(moving.ActionSpecId)) offsets.Insert(0, moving.ImpactTick - moving.StartTick);
            long bestDistance = long.MaxValue, bestStart = desired, anchor = 0;
            foreach (var other in visible.OrderBy(p => p.ActionPlanId))
            {
                if (other.ActionPlanId == moving.ActionPlanId || other.State == (int)ProjectHero.Logic.Actions.ActionPlanState.Completed
                    || other.State == (int)ProjectHero.Logic.Actions.ActionPlanState.Terminated) continue;
                var keys = new List<long> { other.StartTick, other.EndTick };
                if (isAttack(other.ActionSpecId)) keys.Insert(0, other.ImpactTick);
                foreach (long offset in offsets)
                    foreach (long key in keys)
                    {
                        long start = key - offset, distance = Math.Abs(start - desired);
                        if (start < minimum || start > maximum || distance > thresholdTicks || distance >= bestDistance) continue;
                        bestDistance = distance; bestStart = start; anchor = key;
                    }
            }
            return new TimelineSnapResult(bestStart, anchor, bestDistance != long.MaxValue);
        }
    }
}
