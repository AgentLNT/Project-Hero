using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectHero.UI.Timeline
{
    public class TimelineEditorUI : MonoBehaviour
    {
        public event System.Action PlacementCommitted;
        public event System.Action PlacementCancelled;

        [Header("Refs")]
        public BattleTimeline Timeline;
        public Canvas Canvas;

        [Header("Lanes")]
        public RectTransform PlayerLane;
        public RectTransform ObservedLane;

        [Header("Units")]
        public CombatUnit PlayerUnit;
        public CombatUnit ObservedUnit;

        [Header("Mapping")]
        public float PixelsPerSecond = 240f;
        public float MinBlockWidthPx = 24f;

        [Header("Colors")]
        public Color MoveColor = new Color(0.25f, 0.55f, 1.00f, 1f);
        public Color AttackColor = new Color(1.00f, 0.30f, 0.30f, 1f);
        public Color BlockColor = new Color(1.00f, 0.75f, 0.15f, 1f);
        public Color DodgeColor = new Color(0.25f, 0.95f, 0.65f, 1f);
        public Color RecoverColor = new Color(0.75f, 0.65f, 1.00f, 1f);

        [Header("Depth By Length")]
        public float DeepenAtSeconds = 3.0f;
        public float MaxDarkenFactor = 0.45f;

        private readonly Dictionary<long, TimelineBlockView> _blocksByGroup = new();
        private readonly Dictionary<long, TimelineActionPlacement> _placementsByGroupId = new();

        // Snapshot-only grouped events (AI or system scheduled). Separate so they don't interfere with UI-authored blocks.
        private readonly Dictionary<long, TimelineBlockView> _snapshotBlocksByGroupId = new();

        private struct PlacedBlockModel
        {
            public long GroupId;
            public CombatUnit Owner;
            public TimelineLane Lane;
            public TimelineActionKind Kind;
            public float StartTimeAbs;
            public float Duration;

            // Optional metadata for prediction.
            public Pathfinder.GridPoint? MoveDestination;
            public GridDirection? AttackFacingAbsolute;
        }

        private readonly Dictionary<long, PlacedBlockModel> _placedByGroupId = new();
        private TimelineActionPlacement _pendingPlacement;
        private TimelineBlockView _pendingGhost;
        private RectTransform _pendingLane;
        private float _pendingLastResolvedCenterX;
        private float _pendingLastMouseXFromLeft;

        private bool _isRepositioning;
        private long _repositionGroupId;
        private PlacedBlockModel _repositionOriginalModel;

        private bool _isDraggingExisting;
        private long _draggingGroupId;
        private TimelineBlockView _draggingView;
        private float _dragOriginalX;

        private Image _currentTimeLinePlayer;
        private Image _currentTimeLineObserved;
        private Image _mouseLinePlayer;
        private Image _mouseLineObserved;
        private Text _mouseTimeText;
        private Text _nowTimeText;

        private Image _placementShield;

        private bool _layoutDirty;

        private float _suppressBlockClicksUntilUnscaled;

        public bool SuppressBlockClicks => Time.unscaledTime < _suppressBlockClicksUntilUnscaled;

        private const float PredictionTimeQuantumSeconds = 0.05f;
        private const float DurationQuantumSeconds = 0.05f;
        private const float PredictionEpsilonSeconds = 0.0005f;

        private void Awake()
        {
            if (Timeline == null) Timeline = FindFirstObjectByType<BattleTimeline>();
            if (Canvas == null) Canvas = GetComponentInParent<Canvas>();

            EnsureTimeLines();
            EnsureLaneMasks();
        }

        public Color GetBaseColor(TimelineActionKind kind)
        {
            return kind switch
            {
                TimelineActionKind.Move => MoveColor,
                TimelineActionKind.Block => BlockColor,
                TimelineActionKind.Dodge => DodgeColor,
                TimelineActionKind.Attack => AttackColor,
                TimelineActionKind.Recover => RecoverColor,
                _ => new Color(1f, 1f, 1f, 1f)
            };
        }

        public void SetPlayerUnit(CombatUnit unit)
        {
            if (PlayerUnit == unit) return;
            PlayerUnit = unit;
            ClearSnapshotBlocks();
            _layoutDirty = true;
        }

        public void SetObservedUnit(CombatUnit unit)
        {
            if (ObservedUnit == unit) return;
            ObservedUnit = unit;
            ClearSnapshotBlocks();
            _layoutDirty = true;
        }

        private void ClearSnapshotBlocks()
        {
            foreach (var kvp in _snapshotBlocksByGroupId)
            {
                if (kvp.Value != null) Destroy(kvp.Value.gameObject);
            }
            _snapshotBlocksByGroupId.Clear();

            // Also clear snapshot single-event views (these are stored in _blocksByGroup using negative keys).
            var keys = _blocksByGroup.Keys.ToList();
            foreach (var k in keys)
            {
                if (k >= 0) continue;
                if (_blocksByGroup[k] != null) Destroy(_blocksByGroup[k].gameObject);
                _blocksByGroup.Remove(k);
            }
        }

        public void BeginPlacement(TimelineActionPlacement placement)
        {
            if (placement == null || placement.Owner == null) return;
            if (PlayerLane == null || ObservedLane == null) return;

            _pendingPlacement = placement;
            _pendingLastResolvedCenterX = 0f;
            _pendingLastMouseXFromLeft = 0f;
            _isRepositioning = false;
            _repositionGroupId = 0;

            // Create a ghost block in the proper lane.
            var lane = placement.Lane == TimelineLane.Player ? PlayerLane : ObservedLane;
            _pendingLane = lane;
            EnsurePlacementShield(lane);
            var ghost = CreateBlockGO(lane);
            ghost.SetModel(groupId: 0, eventId: 0, startTime: 0f, duration: placement.DurationSeconds, label: string.Empty, isGhost: true);
            ghost.SetWidth(Mathf.Max(MinBlockWidthPx, placement.DurationSeconds * PixelsPerSecond));

            var baseColor = GetBaseColor(placement.Kind);
            ghost.SetColor(ApplyDepth(baseColor, placement.DurationSeconds, isGhost: true));

            // Initial placement: start at lane's left edge; Update() will follow mouse.
            float halfWidth = ghost.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
            ghost.SetX(halfWidth);
            _pendingLastResolvedCenterX = halfWidth;
            _pendingLastMouseXFromLeft = halfWidth;
            _pendingGhost = ghost;
        }

        public bool HasPendingPlacement => _pendingGhost != null;

        public void CancelPlacement()
        {
            // Prevent the same click from affecting underlying blocks.
            _suppressBlockClicksUntilUnscaled = Time.unscaledTime + 0.05f;

            // If we were repositioning an existing block, restore it.
            if (_isRepositioning && Timeline != null && _repositionGroupId != 0)
            {
                if (_placementsByGroupId.TryGetValue(_repositionGroupId, out var placement) && placement != null)
                {
                    float delay = Mathf.Max(0f, _repositionOriginalModel.StartTimeAbs - Timeline.CurrentTime);
                    placement.Schedule?.Invoke(delay, _repositionGroupId);
                    _placedByGroupId[_repositionGroupId] = _repositionOriginalModel;
                    _layoutDirty = true;
                }

                _isRepositioning = false;
                _repositionGroupId = 0;
            }

            if (_pendingGhost != null)
            {
                Destroy(_pendingGhost.gameObject);
            }
            DestroyPlacementShield();
            _pendingGhost = null;
            _pendingPlacement = null;
            _pendingLane = null;
            PlacementCancelled?.Invoke();
        }

        public void FinalizePlacement(TimelineBlockView ghost)
        {
            if (_pendingPlacement == null || ghost == null || Timeline == null) return;

            // Prevent the same click from affecting underlying blocks.
            _suppressBlockClicksUntilUnscaled = Time.unscaledTime + 0.05f;

            // Recompute predicted duration based on the final ghost center and resolve any dependency
            // between start time and width (because left edge depends on width).
            for (int iter = 0; iter < 2; iter++)
            {
                float halfWidthIter = ghost.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
                float leftEdgeXIter = ghost.GetX() - halfWidthIter;
                float startDelayIter = Mathf.Max(0f, leftEdgeXIter / Mathf.Max(1f, PixelsPerSecond));
                float startAbsIter = Timeline.CurrentTime + startDelayIter;
                UpdatePendingPlacementDynamics(startAbsIter);
            }

            float halfWidth = ghost.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
            float leftEdgeX = ghost.GetX() - halfWidth;
            float startDelay = Mathf.Max(0f, leftEdgeX / Mathf.Max(1f, PixelsPerSecond));

            float startAbs = Timeline.CurrentTime + startDelay;
            float endAbs = startAbs + Mathf.Max(0f, _pendingPlacement.DurationSeconds);

            long groupId = _isRepositioning ? _repositionGroupId : Timeline.ReserveGroupId();

            _pendingPlacement.Schedule?.Invoke(startDelay, groupId);

            // Remember how to reschedule this group later.
            _placementsByGroupId[groupId] = _pendingPlacement;

            _placedByGroupId[groupId] = new PlacedBlockModel
            {
                GroupId = groupId,
                Owner = _pendingPlacement.Owner,
                Lane = _pendingPlacement.Lane,
                Kind = _pendingPlacement.Kind,
                StartTimeAbs = startAbs,
                Duration = _pendingPlacement.DurationSeconds,
                MoveDestination = _pendingPlacement.MoveDestination,
                AttackFacingAbsolute = _pendingPlacement.AttackFacingAbsolute
            };

            var placedOwner = _pendingPlacement.Owner;
            var placedLane = _pendingPlacement.Lane;

            _isRepositioning = false;
            _repositionGroupId = 0;

            Destroy(ghost.gameObject);
            DestroyPlacementShield();
            _pendingGhost = null;
            _pendingPlacement = null;
            _pendingLane = null;

            // Recompute durations and push-right to avoid overlaps immediately.
            RecomputePlacedBlocksForLane(placedOwner, placedLane);

            PlacementCommitted?.Invoke();
        }

        public void RequestDelete(TimelineBlockView block)
        {
            if (block == null || Timeline == null) return;

            if (block.GroupId != 0)
            {
                Timeline.CancelGroup(block.GroupId);
                _placementsByGroupId.Remove(block.GroupId);
                _placedByGroupId.Remove(block.GroupId);

                if (_blocksByGroup.TryGetValue(block.GroupId, out var view) && view != null)
                {
                    Destroy(view.gameObject);
                }
                _blocksByGroup.Remove(block.GroupId);

                _layoutDirty = true;
            }
            else if (block.EventId != 0)
            {
                Timeline.CancelEvent(block.EventId);

                long key = -block.EventId;
                if (_blocksByGroup.TryGetValue(key, out var view) && view != null)
                {
                    Destroy(view.gameObject);
                }
                _blocksByGroup.Remove(key);
            }
        }

        public void RequestReposition(TimelineBlockView block)
        {
            if (block == null || Timeline == null) return;
            if (block.GroupId == 0) return;
            if (!_placementsByGroupId.TryGetValue(block.GroupId, out var placement) || placement == null) return;
            if (!_placedByGroupId.TryGetValue(block.GroupId, out var model)) return;

            // If another placement is active, cancel it first.
            if (_pendingGhost != null)
            {
                CancelPlacement();
            }

            // Cancel the old scheduled intents immediately, and remove the visible block.
            Timeline.CancelGroup(block.GroupId);
            _placedByGroupId.Remove(block.GroupId);
            if (_blocksByGroup.TryGetValue(block.GroupId, out var view) && view != null)
            {
                Destroy(view.gameObject);
            }
            _blocksByGroup.Remove(block.GroupId);

            _pendingPlacement = placement;
            _pendingLane = model.Lane == TimelineLane.Player ? PlayerLane : ObservedLane;
            EnsurePlacementShield(_pendingLane);

            _isRepositioning = true;
            _repositionGroupId = block.GroupId;
            _repositionOriginalModel = model;
            _pendingLastResolvedCenterX = 0f;
            _pendingLastMouseXFromLeft = 0f;

            var ghost = CreateBlockGO(_pendingLane);
            ghost.SetModel(groupId: 0, eventId: 0, startTime: model.StartTimeAbs, duration: model.Duration, label: string.Empty, isGhost: true);
            float width = Mathf.Max(MinBlockWidthPx, model.Duration * PixelsPerSecond);
            ghost.SetWidth(width);
            ghost.SetColor(ApplyDepth(GetBaseColor(model.Kind), model.Duration, isGhost: true));

            float xLeftEdge = (model.StartTimeAbs - Timeline.CurrentTime) * PixelsPerSecond;
            float xCenter = xLeftEdge + width * 0.5f;
            ghost.SetX(xCenter);
            _pendingLastResolvedCenterX = xCenter;
            _pendingLastMouseXFromLeft = xCenter;
            _pendingGhost = ghost;
        }

        internal void BeginDragExisting(TimelineBlockView block)
        {
            if (block == null) return;
            if (block.GroupId == 0) return;
            if (!_placementsByGroupId.ContainsKey(block.GroupId)) return;

            _isDraggingExisting = true;
            _draggingGroupId = block.GroupId;
            _draggingView = block;
            _dragOriginalX = block.GetX();
        }

        internal void EndDragExisting(TimelineBlockView block)
        {
            if (!_isDraggingExisting) return;
            if (block == null || block.GroupId != _draggingGroupId) { ResetDrag(); return; }
            if (Timeline == null) { ResetDrag(); return; }
            if (!_placementsByGroupId.TryGetValue(_draggingGroupId, out var placement) || placement == null) { ResetDrag(); return; }

            float halfWidth = block.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
            float leftEdgeX = block.GetX() - halfWidth;
            float startDelay = leftEdgeX / Mathf.Max(1f, PixelsPerSecond);
            if (startDelay < 0f) startDelay = 0f;

            float startAbs = Timeline.CurrentTime + startDelay;
            float endAbs = startAbs + Mathf.Max(0f, placement.DurationSeconds);

            if (WouldOverlap(owner: placement.Owner, lane: placement.Lane, startAbs: startAbs, endAbs: endAbs, ignoreGroupId: _draggingGroupId))
            {
                // Reject move and snap back.
                block.SetX(_dragOriginalX);
                ResetDrag();
                return;
            }

            Timeline.CancelGroup(_draggingGroupId);
            placement.Schedule?.Invoke(startDelay, _draggingGroupId);

            if (_placedByGroupId.ContainsKey(_draggingGroupId))
            {
                var model = _placedByGroupId[_draggingGroupId];
                model.StartTimeAbs = startAbs;
                _placedByGroupId[_draggingGroupId] = model;
            }

            ResetDrag();
        }

        private void ResetDrag()
        {
            _isDraggingExisting = false;
            _draggingGroupId = 0;
            _draggingView = null;
        }

        private void Update()
        {
            if (Timeline == null || PlayerLane == null || ObservedLane == null) return;

            EnsureTimeLines();
            UpdateCurrentTimeLines();
            UpdateMouseLineAndTime();
            EnsureLaneMasks();

            // Placement mode: ghost follows mouse X; left click commits; right click cancels.
            if (_pendingGhost != null && _pendingLane != null)
            {
                var mousePos = UnityEngine.Input.mousePosition;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_pendingLane, mousePos, null, out var localPoint))
                {
                    float xFromLeft = LocalXToXFromLeft(_pendingLane, localPoint.x);
                    float mouseClamped = Mathf.Clamp(xFromLeft, 0f, _pendingLane.rect.width);

                    float resolvedCenterFinal = _pendingLastResolvedCenterX;

                    // Small fixed-point solve: resolve position -> compute startAbs -> predict duration/width -> resolve again.
                    for (int iter = 0; iter < 2; iter++)
                    {
                        float halfWidth = _pendingGhost.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
                        // Allow right overflow; only clamp to left bound.
                        float desiredCenter = Mathf.Max(0f + halfWidth, mouseClamped);

                        // Snap based on current duration.
                        desiredCenter = ApplyKeyframeSnap(desiredCenter, halfWidth);

                        bool movingRight = mouseClamped > _pendingLastMouseXFromLeft + 0.001f;
                        bool movingLeft = mouseClamped < _pendingLastMouseXFromLeft - 0.001f;

                        float resolvedCenter = ResolveGhostCenterInsert(
                            desiredCenter,
                            _pendingPlacement,
                            halfWidth,
                            _pendingLane.rect.width,
                            movingRight,
                            movingLeft,
                            _pendingLastResolvedCenterX);

                        resolvedCenterFinal = resolvedCenter;
                        _pendingGhost.SetX(resolvedCenterFinal);

                        // Predict duration using the resolved center (because being "pushed" changes start time).
                        float leftEdgeX = resolvedCenterFinal - halfWidth;
                        float startDelaySeconds = Mathf.Max(0f, leftEdgeX / Mathf.Max(1f, PixelsPerSecond));
                        float startAbs = Timeline.CurrentTime + startDelaySeconds;
                        UpdatePendingPlacementDynamics(startAbs);
                    }

                    _pendingGhost.SetKeyframeOffsetsSeconds(GetKeyframeOffsetsSeconds(_pendingPlacement.Kind, _pendingPlacement.DurationSeconds), PixelsPerSecond);

                    _pendingLastResolvedCenterX = resolvedCenterFinal;
                    _pendingLastMouseXFromLeft = mouseClamped;
                }

                // Only react to clicks if mouse is over the lane region
                bool overLane = RectTransformUtility.RectangleContainsScreenPoint(_pendingLane, mousePos, null);
                if (overLane)
                {
                    if (UnityEngine.Input.GetMouseButtonDown(1))
                    {
                        CancelPlacement();
                        return;
                    }
                    if (UnityEngine.Input.GetMouseButtonDown(0))
                    {
                        FinalizePlacement(_pendingGhost);
                        return;
                    }
                }
            }

            var snapshot = Timeline.GetScheduledIntentsSnapshot();

            // Only show blocks for player and observed unit.
            var owners = new HashSet<CombatUnit>();
            if (PlayerUnit != null) owners.Add(PlayerUnit);
            if (ObservedUnit != null) owners.Add(ObservedUnit);

            var relevant = snapshot
                .Where(e => e.Owner != null && owners.Contains(e.Owner))
                .ToList();

            // Render placed blocks (UI-authored) from our own models so they don't disappear when intents execute.
            RenderPlacedBlocks();

            if (_layoutDirty)
            {
                RecomputePlacedBlocksForLane(PlayerUnit, TimelineLane.Player);
                RecomputePlacedBlocksForLane(ObservedUnit, TimelineLane.Observed);
                _layoutDirty = false;
            }

            // Render snapshot groups that are not UI-authored (e.g., AI actions on observed lane).
            RenderGroupedSnapshotEvents(relevant);

            // Optionally, render snapshot-only single events (no group) that involve relevant owners.
            RenderUngroupedSnapshotEvents(relevant);

            CleanupOrphanViews();
        }

        private void RenderGroupedSnapshotEvents(List<BattleTimeline.ScheduledIntentInfo> relevant)
        {
            if (Timeline == null) return;
            if (PlayerLane == null || ObservedLane == null) return;

            var grouped = relevant
                .Where(e => e.GroupId != 0 && e.Owner != null)
                .GroupBy(e => e.GroupId)
                .ToList();

            var liveGroupIds = new HashSet<long>(grouped.Select(g => g.Key));

            // Remove stale snapshot group blocks
            var existing = _snapshotBlocksByGroupId.Keys.ToList();
            foreach (var gid in existing)
            {
                if (!liveGroupIds.Contains(gid))
                {
                    if (_snapshotBlocksByGroupId[gid] != null) Destroy(_snapshotBlocksByGroupId[gid].gameObject);
                    _snapshotBlocksByGroupId.Remove(gid);
                }
            }

            foreach (var g in grouped)
            {
                long groupId = g.Key;

                // If this group is UI-authored (placed/repositioned), do not duplicate.
                if (_placedByGroupId.ContainsKey(groupId) || _placementsByGroupId.ContainsKey(groupId))
                {
                    continue;
                }

                var owners = g.Select(e => e.Owner).Where(o => o != null).Distinct().ToList();
                if (owners.Count != 1) continue;
                var owner = owners[0];

                // Only show for the two lanes we support.
                RectTransform lane = null;
                if (owner == PlayerUnit) lane = PlayerLane;
                else if (owner == ObservedUnit) lane = ObservedLane;
                else continue;

                float start = g.Min(e => e.Time);
                float end = g.Max(e => e.Time);
                float duration = Mathf.Max(0.05f, end - start);

                // Determine kind by priority.
                var types = g.Select(e => e.Type).ToList();
                TimelineActionKind kind = TimelineActionKind.None;
                if (types.Contains(ActionType.Move)) kind = TimelineActionKind.Move;
                else if (types.Contains(ActionType.Attack)) kind = TimelineActionKind.Attack;
                else if (types.Contains(ActionType.Block)) kind = TimelineActionKind.Block;
                else if (types.Contains(ActionType.Dodge)) kind = TimelineActionKind.Dodge;
                else kind = TimelineActionKind.None;

                if (kind == TimelineActionKind.None)
                {
                    // Pure state-change groups aren't meaningful as blocks.
                    continue;
                }

                float width = Mathf.Max(MinBlockWidthPx, duration * PixelsPerSecond);
                float startXFromLeft = (start - Timeline.CurrentTime) * PixelsPerSecond;
                float xLeftEdge = startXFromLeft;

                if (!_snapshotBlocksByGroupId.TryGetValue(groupId, out var view) || view == null)
                {
                    view = CreateBlockGO(lane);
                    _snapshotBlocksByGroupId[groupId] = view;

                    // Snapshot blocks are informational only: do not allow delete/reposition.
                    if (view.Background != null) view.Background.raycastTarget = false;
                }

                view.SetModel(groupId: groupId, eventId: 0, startTime: start, duration: duration, label: string.Empty, isGhost: false);
                view.SetWidth(width);
                view.SetX(xLeftEdge + width * 0.5f);
                view.SetColor(ApplyDepth(GetBaseColor(kind), duration, isGhost: false));

                // Set keyframe markers for snapshot blocks (same as placed blocks).
                view.SetKeyframeOffsetsSeconds(GetKeyframeOffsetsSeconds(kind, duration), PixelsPerSecond);

                // Clean up blocks only when RIGHT edge passes lane's LEFT edge.
                float rightEdgeX = startXFromLeft + width;
                if (rightEdgeX < 0f)
                {
                    Destroy(view.gameObject);
                    _snapshotBlocksByGroupId.Remove(groupId);
                }
            }
        }

        private void UpdatePendingPlacementDynamics(float startAbs)
        {
            if (_pendingPlacement == null || _pendingGhost == null) return;
            if (_pendingPlacement.Owner == null) return;

            // Only Move needs dynamic duration prediction; others have fixed estimates.
            if (_pendingPlacement.Kind != TimelineActionKind.Move) return;
            if (!_pendingPlacement.MoveDestination.HasValue) return;

            // Predict the owner's position at this start time using already-placed blocks.
            float t = QuantizeSeconds(startAbs, PredictionTimeQuantumSeconds);
            var predictedStart = PredictUnitGridPositionAt(_pendingPlacement.Owner, t, ignoreGroupId: _isRepositioning ? _repositionGroupId : 0);

            // Compute path & duration using the same rules as ActionScheduler.ScheduleMove.
            var obstacles = GridManager.Instance != null ? GridManager.Instance.GetGlobalObstacles(_pendingPlacement.Owner) : null;
            var pathfinder = new Pathfinder();
            var path = pathfinder.FindPath(predictedStart, _pendingPlacement.MoveDestination.Value, _pendingPlacement.Owner.UnitVolumeDefinition, obstacles);

            float newDuration = ProjectHero.Core.Actions.ActionScheduler.EstimateMoveDuration(_pendingPlacement.Owner, path);
            if (newDuration <= 0f) newDuration = _pendingPlacement.DurationSeconds;

            newDuration = QuantizeSeconds(newDuration, DurationQuantumSeconds);

            // Apply if changed.
            if (Mathf.Abs(newDuration - _pendingPlacement.DurationSeconds) > 0.001f)
            {
                _pendingPlacement.DurationSeconds = newDuration;
                _pendingGhost.SetModel(groupId: 0, eventId: 0, startTime: startAbs, duration: newDuration, label: string.Empty, isGhost: true);

                float width = Mathf.Max(MinBlockWidthPx, newDuration * PixelsPerSecond);
                _pendingGhost.SetWidth(width);
                _pendingGhost.SetColor(ApplyDepth(GetBaseColor(_pendingPlacement.Kind), newDuration, isGhost: true));
            }
        }

        private Pathfinder.GridPoint PredictUnitGridPositionAt(CombatUnit unit, float timeAbs, long ignoreGroupId)
        {
            // Start from the unit's current logical position at "now".
            var pos = unit.GridPosition;

            // Apply all completed moves scheduled before timeAbs.
            foreach (var kvp in _placedByGroupId.OrderBy(k => k.Value.StartTimeAbs))
            {
                if (kvp.Key == ignoreGroupId) continue;
                var m = kvp.Value;
                if (m.Owner != unit) continue;
                if (m.Kind != TimelineActionKind.Move) continue;
                if (!m.MoveDestination.HasValue) continue;

                float end = m.StartTimeAbs + Mathf.Max(0f, m.Duration);
                if (end <= timeAbs - PredictionEpsilonSeconds)
                {
                    pos = m.MoveDestination.Value;
                }
            }

            return pos;
        }

        private float QuantizeSeconds(float value, float quantum)
        {
            if (quantum <= 0f) return value;
            return Mathf.Round(value / quantum) * quantum;
        }

        private float ApplyKeyframeSnap(float desiredCenterX, float halfWidth)
        {
            if (_pendingPlacement == null) return desiredCenterX;
            if (Timeline == null) return desiredCenterX;

            // Snap when a keyframe is close enough to another keyframe.
            const float snapSeconds = 0.08f;
            float snapPx = snapSeconds * Mathf.Max(1f, PixelsPerSecond);

            float leftEdgeX = desiredCenterX - halfWidth;
            float startDelay = Mathf.Max(0f, leftEdgeX / Mathf.Max(1f, PixelsPerSecond));
            float ghostStartAbs = Timeline.CurrentTime + startDelay;

            ghostStartAbs = QuantizeSeconds(ghostStartAbs, PredictionTimeQuantumSeconds);

            var ghostOffsets = GetKeyframeOffsetsSeconds(_pendingPlacement.Kind, _pendingPlacement.DurationSeconds);
            if (ghostOffsets.Count == 0) return desiredCenterX;

            // Collect candidate key times from placed blocks AND snapshot blocks (including Observed lane).
            var candidateTimes = new List<float>();
            foreach (var kvp in _placedByGroupId)
            {
                // Ignore the block being repositioned.
                if (_isRepositioning && kvp.Key == _repositionGroupId) continue;
                var m = kvp.Value;
                if (m.Owner == null) continue;

                var offsets = GetKeyframeOffsetsSeconds(m.Kind, m.Duration);
                for (int i = 0; i < offsets.Count; i++)
                {
                    candidateTimes.Add(m.StartTimeAbs + offsets[i]);
                }
            }

            // Also collect keyframes from snapshot blocks (AI/Observed lane actions).
            if (Timeline != null)
            {
                var snapshot = Timeline.GetScheduledIntentsSnapshot();
                var grouped = snapshot
                    .Where(e => e.GroupId != 0 && e.Owner != null && !_placedByGroupId.ContainsKey(e.GroupId))
                    .GroupBy(e => e.GroupId);

                foreach (var g in grouped)
                {
                    float start = g.Min(e => e.Time);
                    float end = g.Max(e => e.Time);
                    float dur = Mathf.Max(0.05f, end - start);

                    var types = g.Select(e => e.Type).ToList();
                    TimelineActionKind kind = TimelineActionKind.None;
                    if (types.Contains(ActionType.Move)) kind = TimelineActionKind.Move;
                    else if (types.Contains(ActionType.Attack)) kind = TimelineActionKind.Attack;
                    else if (types.Contains(ActionType.Block)) kind = TimelineActionKind.Block;
                    else if (types.Contains(ActionType.Dodge)) kind = TimelineActionKind.Dodge;

                    if (kind == TimelineActionKind.None) continue;

                    var offsets = GetKeyframeOffsetsSeconds(kind, dur);
                    for (int i = 0; i < offsets.Count; i++)
                    {
                        candidateTimes.Add(start + offsets[i]);
                    }
                }
            }

            float bestDeltaSeconds = 0f;
            float bestDeltaPx = float.MaxValue;

            for (int i = 0; i < ghostOffsets.Count; i++)
            {
                float ghostKeyAbs = ghostStartAbs + ghostOffsets[i];
                for (int j = 0; j < candidateTimes.Count; j++)
                {
                    float delta = candidateTimes[j] - ghostKeyAbs;
                    float deltaPx = Mathf.Abs(delta * Mathf.Max(1f, PixelsPerSecond));
                    if (deltaPx <= snapPx && deltaPx < bestDeltaPx)
                    {
                        bestDeltaPx = deltaPx;
                        bestDeltaSeconds = delta;
                    }
                }
            }

            if (bestDeltaPx == float.MaxValue) return desiredCenterX;
            float snapped = desiredCenterX + bestDeltaSeconds * Mathf.Max(1f, PixelsPerSecond);

            // Clamp to left bound only; allow right overflow.
            return Mathf.Max(0f + halfWidth, snapped);
        }

        private List<float> GetKeyframeOffsetsSeconds(TimelineActionKind kind, float durationSeconds)
        {
            var offsets = new List<float>();
            float d = Mathf.Max(0f, durationSeconds);

            switch (kind)
            {
                case TimelineActionKind.Move:
                    // Keyframe: move ends.
                    offsets.Add(d);
                    break;
                case TimelineActionKind.Attack:
                    // Keyframe: impact moment (duration minus recovery).
                    offsets.Add(Mathf.Clamp(d - 0.5f, 0f, d));
                    break;
            }

            return offsets;
        }

        private void CleanupOrphanViews()
        {
            // Remove any placed-group views that no longer exist in the placed model set.
            var keys = _blocksByGroup.Keys.ToList();
            foreach (var key in keys)
            {
                // Negative keys are snapshot-only singles; keep them (they manage their own lifetime).
                if (key < 0) continue;

                // Ghost is not stored in _blocksByGroup.
                if (!_placedByGroupId.ContainsKey(key))
                {
                    if (_blocksByGroup.TryGetValue(key, out var view) && view != null)
                    {
                        Destroy(view.gameObject);
                    }
                    _blocksByGroup.Remove(key);
                }
            }
        }

        private float ConstrainGhostX(float desiredCenterX, float previousCenterX, TimelineActionPlacement placement, float halfWidth)
        {
            return desiredCenterX;
        }

        private float ResolveGhostCenterInsert(float desiredCenterX, TimelineActionPlacement placement, float halfWidth, float laneWidth, bool movingRight, bool movingLeft, float lastResolvedCenterX)
        {
            if (placement == null || placement.Owner == null) return desiredCenterX;

            float width = halfWidth * 2f;
            float left = desiredCenterX - halfWidth;
            // Allow right overflow; only clamp to 0.
            left = Mathf.Max(0f, left);

            var intervals = GetOccupiedIntervals(placement);
            if (intervals.Count == 0) return left + halfWidth;

            // If direction is ambiguous, infer from the resolved center history.
            if (!movingRight && !movingLeft)
            {
                movingRight = desiredCenterX > _pendingLastResolvedCenterX + 0.001f;
                movingLeft = desiredCenterX < _pendingLastResolvedCenterX - 0.001f;
            }

            // Insertion mode:
            // - Never overlap with blocks on the LEFT (blocks that start before our left edge).
            // - Allow overlap with blocks on the RIGHT; they will be shifted on commit.
            //
            // This preserves the "small gap insert" behavior while fixing the near-left-neighbor case:
            // when halfWidth makes the computed left edge fall inside the left neighbor, we push to its end.
            for (int safety = 0; safety < intervals.Count + 2; safety++)
            {
                float right = left + width;

                (float oLeft, float oRight)? overlapLeftNeighbor = null;
                foreach (var iv in intervals)
                {
                    // Only consider blocks that start before our left edge (left-neighbors).
                    // Touching edges is allowed.
                    if (iv.left < left - 0.001f && left < iv.right && right > iv.left)
                    {
                        overlapLeftNeighbor = (iv.left, iv.right);
                        break;
                    }
                }

                if (overlapLeftNeighbor == null) break;

                // Push just after the left neighbor.
                left = overlapLeftNeighbor.Value.oRight;
            }

            return left + halfWidth;
        }

        private List<(float left, float right)> GetOccupiedIntervals(TimelineActionPlacement placement)
        {
            var list = new List<(float left, float right)>();
            foreach (var b in _placedByGroupId.Values)
            {
                if (b.Owner != placement.Owner) continue;
                if (b.Lane != placement.Lane) continue;

                float width = Mathf.Max(MinBlockWidthPx, b.Duration * PixelsPerSecond);
                float left = (b.StartTimeAbs - Timeline.CurrentTime) * PixelsPerSecond;
                float right = left + width;
                list.Add((left, right));
            }

            list.Sort((a, c) => a.left.CompareTo(c.left));
            return list;
        }

        private void RecomputePlacedBlocksForLane(CombatUnit owner, TimelineLane lane)
        {
            if (Timeline == null) return;
            if (owner == null) return;

            // Get blocks for this owner+lane, sorted by time.
            var ids = _placedByGroupId
                .Where(kvp => kvp.Value.Owner == owner && kvp.Value.Lane == lane)
                .OrderBy(kvp => kvp.Value.StartTimeAbs)
                .Select(kvp => kvp.Key)
                .ToList();

            if (ids.Count == 0) return;

            // Update move durations predictably (based on chain order), and when overlaps occur,
            // shift ALL blocks to the right by the required delta (preserving gaps among them).
            var predictedPos = owner.GridPosition;

            // First: compute durations (and predictedPos chain) in current order.
            for (int i = 0; i < ids.Count; i++)
            {
                long id = ids[i];
                var model = _placedByGroupId[id];

                float duration = Mathf.Max(0f, model.Duration);
                if (model.Kind == TimelineActionKind.Move && model.MoveDestination.HasValue)
                {
                    var obstacles = GridManager.Instance != null ? GridManager.Instance.GetGlobalObstacles(owner) : null;
                    var pathfinder = new Pathfinder();
                    var path = pathfinder.FindPath(predictedPos, model.MoveDestination.Value, owner.UnitVolumeDefinition, obstacles);
                    float d = ProjectHero.Core.Actions.ActionScheduler.EstimateMoveDuration(owner, path);
                    if (d > 0f) duration = QuantizeSeconds(d, DurationQuantumSeconds);
                    predictedPos = model.MoveDestination.Value;
                }

                if (Mathf.Abs(model.Duration - duration) > 0.0001f)
                {
                    model.Duration = duration;
                    _placedByGroupId[id] = model;
                }
            }

            // Second: resolve overlaps by shifting the suffix.
            for (int i = 0; i < ids.Count - 1; i++)
            {
                long leftId = ids[i];
                long rightId = ids[i + 1];

                var left = _placedByGroupId[leftId];
                var right = _placedByGroupId[rightId];

                float leftEnd = left.StartTimeAbs + Mathf.Max(0f, left.Duration);
                if (right.StartTimeAbs < leftEnd)
                {
                    float delta = leftEnd - right.StartTimeAbs;
                    for (int j = i + 1; j < ids.Count; j++)
                    {
                        long id = ids[j];
                        var m = _placedByGroupId[id];
                        m.StartTimeAbs += delta;
                        _placedByGroupId[id] = m;
                    }
                }
            }

            // Finally: reschedule any groups whose start/duration changed.
            for (int i = 0; i < ids.Count; i++)
            {
                long id = ids[i];
                var model = _placedByGroupId[id];

                // Do not reschedule actions that are already in the past, otherwise they would re-execute.
                if (model.StartTimeAbs < Timeline.CurrentTime + 0.0001f) continue;

                if (_placementsByGroupId.TryGetValue(id, out var placement) && placement != null)
                {
                    // Keep placement duration in sync with model.
                    placement.DurationSeconds = model.Duration;
                    Timeline.CancelGroup(id);
                    float delay = Mathf.Max(0f, model.StartTimeAbs - Timeline.CurrentTime);
                    placement.Schedule?.Invoke(delay, id);
                }
            }
        }

        private void EnsurePlacementShield(RectTransform lane)
        {
            if (lane == null) return;
            if (_placementShield != null)
            {
                // Move to the current lane.
                if (_placementShield.transform.parent != lane)
                {
                    _placementShield.transform.SetParent(lane, false);
                }

                _placementShield.transform.SetAsLastSibling();
                return;
            }

            var go = new GameObject("PlacementShield", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(lane, false);
            go.transform.SetAsLastSibling();

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var img = go.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);
            img.raycastTarget = true;
            _placementShield = img;
        }

        private void DestroyPlacementShield()
        {
            if (_placementShield == null) return;
            Destroy(_placementShield.gameObject);
            _placementShield = null;
        }

        private void EnsureLaneMasks()
        {
            if (PlayerLane != null && PlayerLane.GetComponent<RectMask2D>() == null)
            {
                PlayerLane.gameObject.AddComponent<RectMask2D>();
            }
            if (ObservedLane != null && ObservedLane.GetComponent<RectMask2D>() == null)
            {
                ObservedLane.gameObject.AddComponent<RectMask2D>();
            }
        }

        private void RenderPlacedBlocks()
        {
            var groupIds = _placedByGroupId.Keys.ToList();
            foreach (var groupId in groupIds)
            {
                if (!_placedByGroupId.TryGetValue(groupId, out var model)) continue;
                if (model.Owner == null) { _placedByGroupId.Remove(groupId); continue; }

                var lane = model.Lane == TimelineLane.Player ? PlayerLane : ObservedLane;
                if (lane == null) continue;

                float width = Mathf.Max(MinBlockWidthPx, model.Duration * PixelsPerSecond);
                float startXFromLeft = (model.StartTimeAbs - Timeline.CurrentTime) * PixelsPerSecond;
                float xLeftEdge = startXFromLeft;

                // Cull only when RIGHT edge passes lane's left edge.
                if (startXFromLeft + width < 0f)
                {
                    if (_blocksByGroup.TryGetValue(groupId, out var stale) && stale != null) Destroy(stale.gameObject);
                    _blocksByGroup.Remove(groupId);
                    _placedByGroupId.Remove(groupId);
                    _placementsByGroupId.Remove(groupId);
                    continue;
                }

                if (!_blocksByGroup.TryGetValue(groupId, out var view) || view == null)
                {
                    view = CreateBlockGO(lane);
                    _blocksByGroup[groupId] = view;
                }

                view.SetModel(groupId: groupId, eventId: 0, startTime: model.StartTimeAbs, duration: model.Duration, label: string.Empty, isGhost: false);
                view.SetWidth(width);
                view.SetX(xLeftEdge + width * 0.5f);

                var color = ApplyDepth(GetBaseColor(model.Kind), model.Duration, isGhost: false);
                view.SetColor(color);

                view.SetKeyframeOffsetsSeconds(GetKeyframeOffsetsSeconds(model.Kind, model.Duration), PixelsPerSecond);
            }
        }

        private void RenderUngroupedSnapshotEvents(List<BattleTimeline.ScheduledIntentInfo> relevant)
        {
            // Only single events (GroupId==0) are displayed as a tiny block so the UI stays stable.
            // These are not reschedulable, and will naturally disappear once executed.
            var singles = relevant.Where(e => e.GroupId == 0).ToList();
            var singleKeys = new HashSet<long>(singles.Select(e => e.Id));

            // Remove stale single-event blocks
            var existingSingleKeys = _blocksByGroup.Keys.Where(k => k < 0).ToList();
            foreach (var key in existingSingleKeys)
            {
                long eventId = -key;
                if (!singleKeys.Contains(eventId))
                {
                    if (_blocksByGroup[key] != null) Destroy(_blocksByGroup[key].gameObject);
                    _blocksByGroup.Remove(key);
                }
            }

            foreach (var e in singles)
            {
                var owner = e.Owner;
                if (owner == null) continue;

                var lane = owner == PlayerUnit ? PlayerLane : ObservedLane;
                if (lane == null) continue;

                float duration = 0.05f;
                float width = Mathf.Max(MinBlockWidthPx, duration * PixelsPerSecond);
                float startXFromLeft = (e.Time - Timeline.CurrentTime) * PixelsPerSecond;
                float xLeftEdge = startXFromLeft;

                long key = -e.Id;
                if (!_blocksByGroup.TryGetValue(key, out var view) || view == null)
                {
                    view = CreateBlockGO(lane);
                    _blocksByGroup[key] = view;
                }

                view.SetModel(groupId: 0, eventId: e.Id, startTime: e.Time, duration: duration, label: string.Empty, isGhost: false);
                view.SetWidth(width);
                view.SetX(xLeftEdge + width * 0.5f);
                view.SetColor(new Color(1f, 1f, 1f, 0.35f));

                if (startXFromLeft + width < 0f)
                {
                    Destroy(view.gameObject);
                    _blocksByGroup.Remove(key);
                }
            }
        }

        private bool WouldOverlap(CombatUnit owner, TimelineLane lane, float startAbs, float endAbs, long ignoreGroupId)
        {
            foreach (var kvp in _placedByGroupId)
            {
                if (kvp.Key == ignoreGroupId) continue;
                var other = kvp.Value;
                if (other.Owner != owner) continue;
                if (other.Lane != lane) continue;

                float otherStart = other.StartTimeAbs;
                float otherEnd = other.StartTimeAbs + Mathf.Max(0f, other.Duration);

                // Overlap if intervals intersect (touching edges is allowed).
                if (startAbs < otherEnd && endAbs > otherStart)
                {
                    return true;
                }
            }
            return false;
        }

        private float LocalXToXFromLeft(RectTransform lane, float localX)
        {
            // ScreenPointToLocalPointInRectangle returns local coords with origin at pivot.
            // Our UI elements are anchored from the left edge (anchorX=0), so convert to "from left".
            return localX + lane.rect.width * lane.pivot.x;
        }

        private Color ApplyDepth(Color baseColor, float durationSeconds, bool isGhost)
        {
            float t = DeepenAtSeconds <= 0f ? 1f : Mathf.Clamp01(durationSeconds / DeepenAtSeconds);
            float darken = Mathf.Lerp(0f, MaxDarkenFactor, t);
            var darker = new Color(
                Mathf.Clamp01(baseColor.r * (1f - darken)),
                Mathf.Clamp01(baseColor.g * (1f - darken)),
                Mathf.Clamp01(baseColor.b * (1f - darken)),
                1f);

            darker.a = isGhost ? 0.45f : 0.90f;
            return darker;
        }

        private void EnsureTimeLines()
        {
            if (PlayerLane == null || ObservedLane == null) return;

            if (_currentTimeLinePlayer == null) _currentTimeLinePlayer = CreateLine(PlayerLane, "CurrentTimeLine");
            if (_currentTimeLineObserved == null) _currentTimeLineObserved = CreateLine(ObservedLane, "CurrentTimeLine");
            if (_mouseLinePlayer == null) _mouseLinePlayer = CreateLine(PlayerLane, "MouseLine");
            if (_mouseLineObserved == null) _mouseLineObserved = CreateLine(ObservedLane, "MouseLine");

            if (_mouseTimeText == null)
            {
                var go = new GameObject("MouseTimeText", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                go.transform.SetParent(transform, false);
                var rect = go.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.anchoredPosition = new Vector2(0f, -6f);
                rect.sizeDelta = new Vector2(220f, 24f);

                var text = go.GetComponent<Text>();
                text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                text.fontSize = 14;
                text.alignment = TextAnchor.UpperLeft;
                text.color = new Color(1f, 1f, 1f, 0.9f);
                text.text = string.Empty;
                _mouseTimeText = text;
            }

            if (_nowTimeText == null)
            {
                var go = new GameObject("NowTimeText", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                go.transform.SetParent(transform, false);
                var rect = go.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(8f, -6f);
                rect.sizeDelta = new Vector2(180f, 24f);

                var text = go.GetComponent<Text>();
                text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                text.fontSize = 14;
                text.alignment = TextAnchor.UpperLeft;
                text.color = new Color(1f, 1f, 1f, 0.9f);
                text.text = string.Empty;
                _nowTimeText = text;
            }

            // Default hidden for mouse lines
            if (_mouseLinePlayer != null) _mouseLinePlayer.enabled = false;
            if (_mouseLineObserved != null) _mouseLineObserved.enabled = false;
        }

        private Image CreateLine(RectTransform lane, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(lane, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(2f, 0f);

            var img = go.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.8f);

            return img;
        }

        private void UpdateCurrentTimeLines()
        {
            if (_currentTimeLinePlayer == null || _currentTimeLineObserved == null) return;

            // Current time is lane's left edge.
            SetLineX(_currentTimeLinePlayer.rectTransform, 0f);
            SetLineX(_currentTimeLineObserved.rectTransform, 0f);

            if (_nowTimeText != null && Timeline != null)
            {
                _nowTimeText.text = $"now {Timeline.CurrentTime:F2}s";
            }
        }

        private void UpdateMouseLineAndTime()
        {
            if (_mouseLinePlayer == null || _mouseLineObserved == null || _mouseTimeText == null) return;

            var mousePos = UnityEngine.Input.mousePosition;
            bool overPlayer = RectTransformUtility.RectangleContainsScreenPoint(PlayerLane, mousePos, null);
            bool overObserved = RectTransformUtility.RectangleContainsScreenPoint(ObservedLane, mousePos, null);

            if (!overPlayer && !overObserved)
            {
                _mouseLinePlayer.enabled = false;
                _mouseLineObserved.enabled = false;
                _mouseTimeText.text = string.Empty;
                return;
            }

            RectTransform lane = overPlayer ? PlayerLane : ObservedLane;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(lane, mousePos, null, out var localPoint))
            {
                _mouseLinePlayer.enabled = false;
                _mouseLineObserved.enabled = false;
                _mouseTimeText.text = string.Empty;
                return;
            }

            float xFromLeft = LocalXToXFromLeft(lane, localPoint.x);
            float x = Mathf.Clamp(xFromLeft, 0f, lane.rect.width);

            _mouseLinePlayer.enabled = true;
            _mouseLineObserved.enabled = true;

            SetLineX(_mouseLinePlayer.rectTransform, x);
            SetLineX(_mouseLineObserved.rectTransform, x);

            float offsetSeconds = x / Mathf.Max(1f, PixelsPerSecond);
            float now = Timeline != null ? Timeline.CurrentTime : 0f;
            float offset = Mathf.Max(0f, offsetSeconds);
            float absTime = now + offset;
            _mouseTimeText.text = $"{now:F2}s + {offset:F2}s";

            // Follow the mouse line (in screen space of this UI)
            var rect = _mouseTimeText.GetComponent<RectTransform>();
            if (rect != null)
            {
                // Put the label near the top, centered on the mouse line.
                rect.anchoredPosition = new Vector2(x, -6f);
            }

            AvoidTimeTextOverlap();
            ClampMouseTimeTextToScreen();
        }

        private void ClampMouseTimeTextToScreen()
        {
            if (_mouseTimeText == null) return;
            var rect = _mouseTimeText.GetComponent<RectTransform>();
            if (rect == null) return;

            var parent = transform as RectTransform;
            if (parent == null) return;

            float pad = 8f;
            float half = rect.rect.width * 0.5f;
            float minX = half + pad;
            float maxX = Mathf.Max(minX, parent.rect.width - half - pad);

            var pos = rect.anchoredPosition;
            pos.x = Mathf.Clamp(pos.x, minX, maxX);
            rect.anchoredPosition = pos;
        }

        private void AvoidTimeTextOverlap()
        {
            if (_mouseTimeText == null || _nowTimeText == null) return;

            var mouseRect = _mouseTimeText.GetComponent<RectTransform>();
            var nowRect = _nowTimeText.GetComponent<RectTransform>();
            if (mouseRect == null || nowRect == null) return;

            float pad = 6f;
            float nowLeft = nowRect.anchoredPosition.x - nowRect.pivot.x * nowRect.sizeDelta.x;
            float nowRight = nowLeft + nowRect.sizeDelta.x;
            float mouseLeft = mouseRect.anchoredPosition.x - mouseRect.pivot.x * mouseRect.sizeDelta.x;
            float mouseRight = mouseLeft + mouseRect.sizeDelta.x;

            bool overlapX = mouseRight + pad > nowLeft && mouseLeft - pad < nowRight;
            if (!overlapX) return;

            // Move mouse label to a second line when overlapping.
            mouseRect.anchoredPosition = new Vector2(mouseRect.anchoredPosition.x, -30f);
        }

        private void SetLineX(RectTransform rect, float x)
        {
            var pos = rect.anchoredPosition;
            pos.x = x;
            rect.anchoredPosition = pos;
        }

        private TimelineBlockView CreateBlockGO(RectTransform lane)
        {
            var go = new GameObject("TimelineBlock", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(TimelineBlockView));
            go.transform.SetParent(lane, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(MinBlockWidthPx, lane.rect.height * 0.8f);

            var img = go.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.9f);

            var view = go.GetComponent<TimelineBlockView>();
            view.Background = img;
            view.Label = null;
            view.Init(this, lane, Canvas);

            return view;
        }
    }
}
