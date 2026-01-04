using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ProjectHero.Core.Actions.Intents;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;

namespace ProjectHero.Visuals
{
    [RequireComponent(typeof(NextActionPreviewRenderer))]
    public sealed class NextActionPreviewSystem : MonoBehaviour
    {
        public bool showMovePreview = true;
        public bool showAttackPreview = true;

        private BattleTimeline _timeline;
        private GridManager _grid;
        private NextActionPreviewRenderer _renderer;

        private long _lastTick = -1;
        private bool _dirty = true;
        private float _nextAllowedRefreshTime = 0f;

        private sealed class UnitPreviewCache
        {
            public long GroupKey;
            public long PlannedStartTick;
            public int PlannedEventCount;
            public long PlannedMaxEventId;
            public HashSet<TrianglePoint> Move = new HashSet<TrianglePoint>();
            public HashSet<TrianglePoint> Attack = new HashSet<TrianglePoint>();
        }

        private readonly Dictionary<CombatUnit, UnitPreviewCache> _cache = new Dictionary<CombatUnit, UnitPreviewCache>();

        private void Awake()
        {
            _renderer = GetComponent<NextActionPreviewRenderer>();
        }

        private void OnEnable()
        {
            _grid = GridManager.Instance;
            _timeline = FindFirstObjectByType<BattleTimeline>();
            if (_timeline != null)
            {
                _timeline.OnScheduleChanged += MarkDirty;
                _lastTick = _timeline.CurrentTick;
            }

            MarkDirty();
        }

        private void OnDisable()
        {
            if (_timeline != null)
            {
                _timeline.OnScheduleChanged -= MarkDirty;
            }
        }

        private void Update()
        {
            if (_renderer == null) return;
            if (_grid == null) _grid = GridManager.Instance;
            if (_timeline == null) _timeline = FindFirstObjectByType<BattleTimeline>();

            if (_grid == null || _timeline == null)
            {
                _renderer.SetVolumes(null, null);
                return;
            }

            if (_timeline.CurrentTick != _lastTick)
            {
                _lastTick = _timeline.CurrentTick;
                _dirty = true;
            }

            if (!_dirty) return;
            if (Time.unscaledTime < _nextAllowedRefreshTime) return;

            RebuildPreview();
            _dirty = false;
        }

        private void MarkDirty()
        {
            _dirty = true;
            _nextAllowedRefreshTime = Time.unscaledTime + 0.02f; // coalesce burst schedules
        }

        private void RebuildPreview()
        {
            var moveSet = new HashSet<TrianglePoint>();
            var attackSet = new HashSet<TrianglePoint>();

            var snapshot = _timeline.GetScheduledIntentsDetailedSnapshot();
            var units = _grid.GetAllUnits();
            if (units == null || units.Count == 0)
            {
                _renderer.SetVolumes(null, null);
                return;
            }

            long currentTick = _timeline.CurrentTick;

            foreach (var unit in units)
            {
                if (unit == null) continue;
                if (!unit.gameObject.activeInHierarchy) continue;
                if (unit.CurrentHealth <= 0) continue;

                var unitFuture = snapshot
                    .Where(e => e.Intent != null && e.Intent.Owner == unit && e.Tick >= currentTick)
                    .ToList();

                if (unitFuture.Count == 0)
                {
                    _cache.Remove(unit);
                    continue;
                }

                long chosenGroupKey = ChooseNextGroupKey(unitFuture);

                if (_cache.TryGetValue(unit, out var existing) && existing != null)
                {
                    bool existingGroupStillPresent = unitFuture.Any(e => GroupKey(e) == existing.GroupKey);
                    if (!existingGroupStillPresent)
                    {
                        _cache.Remove(unit);
                    }
                }

                if (!_cache.TryGetValue(unit, out var cache) || cache == null)
                {
                    cache = new UnitPreviewCache();
                    _cache[unit] = cache;
                }

                var groupEvents = unitFuture
                    .Where(e => GroupKey(e) == chosenGroupKey)
                    .OrderBy(e => e.Tick)
                    .ThenByDescending(e => e.Priority)
                    .ToList();

                long plannedStartTick = groupEvents.Count > 0 ? groupEvents[0].Tick : long.MaxValue;
                int plannedCount = groupEvents.Count;
                long plannedMaxEventId = 0;
                for (int i = 0; i < groupEvents.Count; i++)
                {
                    if (groupEvents[i].Id > plannedMaxEventId) plannedMaxEventId = groupEvents[i].Id;
                }

                bool groupChanged = cache.GroupKey != chosenGroupKey;
                bool groupRescheduled =
                    cache.GroupKey == chosenGroupKey &&
                    currentTick < cache.PlannedStartTick &&
                    (cache.PlannedStartTick != plannedStartTick || cache.PlannedEventCount != plannedCount || cache.PlannedMaxEventId != plannedMaxEventId);

                if (groupChanged || groupRescheduled)
                {
                    cache.GroupKey = chosenGroupKey;
                    cache.Move.Clear();
                    cache.Attack.Clear();

                    cache.PlannedStartTick = plannedStartTick;
                    cache.PlannedEventCount = plannedCount;
                    cache.PlannedMaxEventId = plannedMaxEventId;

                    SimulateAndCollect(unit, groupEvents, cache.Move, cache.Attack);
                }

                if (showMovePreview && cache.Move.Count > 0)
                {
                    foreach (var tri in cache.Move) moveSet.Add(tri);
                }

                if (showAttackPreview && cache.Attack.Count > 0)
                {
                    foreach (var tri in cache.Attack) attackSet.Add(tri);
                }
            }

            _renderer.SetVolumes(showMovePreview ? moveSet : null, showAttackPreview ? attackSet : null);
        }

        private static long GroupKey(BattleTimeline.ScheduledIntentDetailedInfo e)
        {
            return e.GroupId != 0 ? e.GroupId : e.Id;
        }

        private static long ChooseNextGroupKey(List<BattleTimeline.ScheduledIntentDetailedInfo> future)
        {
            long bestKey = 0;
            long bestTick = long.MaxValue;

            var groups = future.GroupBy(GroupKey);
            foreach (var g in groups)
            {
                long minTick = long.MaxValue;
                foreach (var evt in g)
                {
                    if (evt.Tick < minTick) minTick = evt.Tick;
                }

                if (minTick < bestTick)
                {
                    bestTick = minTick;
                    bestKey = g.Key;
                }
                else if (minTick == bestTick && g.Key < bestKey)
                {
                    bestKey = g.Key;
                }
            }

            return bestKey;
        }

        private static void SimulateAndCollect(
            CombatUnit unit,
            List<BattleTimeline.ScheduledIntentDetailedInfo> orderedEvents,
            HashSet<TrianglePoint> moveOut,
            HashSet<TrianglePoint> attackOut)
        {
            var predictedPos = unit.GridPosition;
            var predictedFacing = unit.FacingDirection;

            foreach (var evt in orderedEvents)
            {
                var intent = evt.Intent;
                if (intent == null) continue;

                switch (intent)
                {
                    case PlanMoveIntent planMove:
                    {
                        if (GridManager.Instance == null) break;
                        if (unit.UnitVolumeDefinition == null) break;

                        var obstacles = GridManager.Instance.GetGlobalObstacles(unit);
                        var pathfinder = new Pathfinder();
                        var path = pathfinder.FindPath(predictedPos, planMove.Destination, unit.UnitVolumeDefinition, obstacles);
                        if (path == null || path.Count < 2) break;

                        for (int i = 1; i < path.Count; i++)
                        {
                            var from = path[i - 1];
                            var to = path[i];
                            var dir = GridMath.GetDirection(from, to);
                            foreach (var tri in unit.GetProjectedOccupancy(to, dir))
                            {
                                moveOut.Add(tri);
                            }
                            predictedPos = to;
                            predictedFacing = dir;
                        }

                        break;
                    }

                    case MoveIntent move:
                    {
                        var dir = GridMath.GetDirection(move.From, move.To);
                        foreach (var tri in unit.GetProjectedOccupancy(move.To, dir))
                        {
                            moveOut.Add(tri);
                        }

                        predictedPos = move.To;
                        if (move.Rotate) predictedFacing = dir;
                        break;
                    }

                    case CommitMoveStepIntent commit:
                    {
                        predictedPos = commit.To;
                        break;
                    }

                    case StateChangeIntent state:
                    {
                        if (state.ForceFacing.HasValue)
                        {
                            predictedFacing = state.ForceFacing.Value;
                        }
                        break;
                    }

                    case PlanFacingIntent planFacing:
                    {
                        predictedFacing = GridMath.GetDirection(predictedPos, planFacing.AimPoint);
                        break;
                    }

                    case AttackIntent attack:
                    {
                        var pattern = attack.ActionDefinition != null ? attack.ActionDefinition.Pattern : null;
                        if (pattern == null) break;

                        foreach (var tri in pattern.GetAffectedTriangles(predictedPos, predictedFacing))
                        {
                            attackOut.Add(tri);
                        }
                        break;
                    }
                }
            }
        }
    }
}
