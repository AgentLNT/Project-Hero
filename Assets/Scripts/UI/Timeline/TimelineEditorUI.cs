using ProjectHero.Core.Actions;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;
using System;
using System.Collections.Generic;
using System.Linq;
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
        public Font UiFont;

        [Header("Lanes")]
        public RectTransform PlayerLane;
        public RectTransform ObservedLane;

        [Header("Time Ruler")]
        public RectTransform RulerArea;

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

        [Header("Snapping")]
        public float SnapThresholdSeconds = 0.15f;

        private readonly Dictionary<long, TimelineBlockView> _activeViews = new();
        private readonly Dictionary<long, TimelineActionPlacement> _placementsByGroupId = new();
        private readonly Dictionary<long, BlockRenderModel> _playerBlocks = new();
        private readonly Dictionary<long, BlockRenderModel> _observedBlocks = new();

        private class BlockRenderModel
        {
            public long GroupId;
            public long OriginalGroupId;
            public CombatUnit Owner;
            public TimelineLane Lane;
            public TimelineActionKind Kind;
            public float StartTimeAbs;
            public float Duration;
            public bool IsInteractable;
            public Pathfinder.GridPoint? MoveDestination;
        }

        private TimelineActionPlacement _pendingPlacement;
        private TimelineBlockView _pendingGhost;
        private RectTransform _pendingLane;
        private float _pendingLastResolvedCenterX;
        private float _pendingLastMouseXFromLeft;

        private bool _isRepositioning;
        private long _repositionGroupId;
        private BlockRenderModel _repositionOriginalModel;

        private bool _isDraggingExisting;
        private long _draggingGroupId;
        private TimelineBlockView _draggingView;
        private float _dragOriginalX;

        private Image _currentTimeLinePlayer;
        private Image _currentTimeLineObserved;
        private Image _mouseLinePlayer;
        private Image _mouseLineObserved;
        private Image _snapIndicatorLine;

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

            if (RulerArea == null)
            {
                var go = new GameObject("RulerArea", typeof(RectTransform));
                go.transform.SetParent(transform, false);
                RulerArea = go.GetComponent<RectTransform>();
                RulerArea.anchorMin = new Vector2(0, 1);
                RulerArea.anchorMax = new Vector2(1, 1);
                RulerArea.pivot = new Vector2(0.5f, 1f);
                RulerArea.sizeDelta = new Vector2(0, 30);
                RulerArea.anchoredPosition = Vector2.zero;
            }

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
            ClearAllBlocks();
            _layoutDirty = true;
        }

        public void SetObservedUnit(CombatUnit unit)
        {
            if (ObservedUnit == unit) return;
            ObservedUnit = unit;
            _observedBlocks.Clear();
            _layoutDirty = true;
        }

        private void ClearAllBlocks()
        {
            foreach (var view in _activeViews.Values) if (view != null) Destroy(view.gameObject);
            _activeViews.Clear();
            _playerBlocks.Clear();
            _observedBlocks.Clear();
            _placementsByGroupId.Clear();
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

            var lane = placement.Lane == TimelineLane.Player ? PlayerLane : ObservedLane;
            _pendingLane = lane;
            EnsurePlacementShield(lane);

            var ghost = CreateBlockGO(lane);
            ghost.SetModel(groupId: 0, eventId: 0, startTime: 0f, duration: placement.DurationSeconds, label: placement.Label, isGhost: true);
            ghost.SetWidth(Mathf.Max(MinBlockWidthPx, placement.DurationSeconds * PixelsPerSecond));
            ghost.SetColor(ApplyDepth(GetBaseColor(placement.Kind), placement.DurationSeconds, isGhost: true));

            float halfWidth = ghost.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
            ghost.SetX(halfWidth);
            _pendingLastResolvedCenterX = halfWidth;
            _pendingLastMouseXFromLeft = halfWidth;
            _pendingGhost = ghost;
        }

        public bool HasPendingPlacement => _pendingGhost != null;

        public void CancelPlacement()
        {
            _suppressBlockClicksUntilUnscaled = Time.unscaledTime + 0.05f;

            if (_isRepositioning && Timeline != null && _repositionGroupId != 0)
            {
                if (_placementsByGroupId.TryGetValue(_repositionGroupId, out var placement) && placement != null)
                {
                    float delay = Mathf.Max(0f, _repositionOriginalModel.StartTimeAbs - Timeline.CurrentTime);
                    placement.Schedule?.Invoke(delay, _repositionGroupId);
                    _playerBlocks[_repositionGroupId] = _repositionOriginalModel;
                    _layoutDirty = true;
                }
                _isRepositioning = false;
                _repositionGroupId = 0;
            }

            if (_pendingGhost != null) Destroy(_pendingGhost.gameObject);
            DestroyPlacementShield();
            _pendingGhost = null;
            _pendingPlacement = null;
            _pendingLane = null;
            if (_snapIndicatorLine != null) _snapIndicatorLine.enabled = false;
            PlacementCancelled?.Invoke();
        }

        public void FinalizePlacement(TimelineBlockView ghost)
        {
            if (_pendingPlacement == null || ghost == null || Timeline == null) return;
            _suppressBlockClicksUntilUnscaled = Time.unscaledTime + 0.05f;

            float halfWidth = ghost.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
            float leftEdgeX = ghost.GetX() - halfWidth;
            float startDelay = Mathf.Max(0f, leftEdgeX / Mathf.Max(1f, PixelsPerSecond));
            float startAbs = Timeline.CurrentTime + startDelay;

            UpdatePendingPlacementDynamics(startAbs);

            long groupId = _isRepositioning ? _repositionGroupId : Timeline.ReserveGroupId();

            _pendingPlacement.Schedule?.Invoke(startDelay, groupId);
            _placementsByGroupId[groupId] = _pendingPlacement;

            _playerBlocks[groupId] = new BlockRenderModel
            {
                GroupId = groupId,
                Owner = _pendingPlacement.Owner,
                Lane = _pendingPlacement.Lane,
                Kind = _pendingPlacement.Kind,
                StartTimeAbs = startAbs,
                Duration = _pendingPlacement.DurationSeconds,
                IsInteractable = true,
                MoveDestination = _pendingPlacement.MoveDestination
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
            if (_snapIndicatorLine != null) _snapIndicatorLine.enabled = false;

            RecomputePlayerBlocksForLane(placedOwner, placedLane);
            PlacementCommitted?.Invoke();
        }

        public void RequestDelete(TimelineBlockView block)
        {
            if (block == null || Timeline == null) return;
            if (block.GroupId != 0 && _playerBlocks.ContainsKey(block.GroupId))
            {
                Timeline.CancelGroup(block.GroupId);
                _placementsByGroupId.Remove(block.GroupId);
                _playerBlocks.Remove(block.GroupId);
                _layoutDirty = true;
            }
        }

        public void RequestReposition(TimelineBlockView block)
        {
            if (block == null || Timeline == null) return;
            if (block.GroupId == 0) return;
            if (!_playerBlocks.TryGetValue(block.GroupId, out var model)) return;
            if (!_placementsByGroupId.TryGetValue(block.GroupId, out var placement)) return;

            if (_pendingGhost != null) CancelPlacement();

            Timeline.CancelGroup(block.GroupId);
            _playerBlocks.Remove(block.GroupId);

            if (_activeViews.TryGetValue(block.GroupId, out var view))
            {
                Destroy(view.gameObject);
                _activeViews.Remove(block.GroupId);
            }

            _pendingPlacement = placement;
            _pendingLane = model.Lane == TimelineLane.Player ? PlayerLane : ObservedLane;
            EnsurePlacementShield(_pendingLane);

            _isRepositioning = true;
            _repositionGroupId = block.GroupId;
            _repositionOriginalModel = model;
            _pendingLastResolvedCenterX = 0f;
            _pendingLastMouseXFromLeft = 0f;

            var ghost = CreateBlockGO(_pendingLane);
            ghost.SetModel(groupId: 0, eventId: 0, startTime: model.StartTimeAbs, duration: model.Duration, label: placement.Label, isGhost: true);
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
            if (block == null || block.GroupId == 0) return;
            if (!_playerBlocks.ContainsKey(block.GroupId)) return;

            _isDraggingExisting = true;
            _draggingGroupId = block.GroupId;
            _draggingView = block;
            _dragOriginalX = block.GetX();
        }

        internal void EndDragExisting(TimelineBlockView block)
        {
            if (!_isDraggingExisting) return;
            if (block == null || block.GroupId != _draggingGroupId) { ResetDrag(); return; }
            if (!_placementsByGroupId.TryGetValue(_draggingGroupId, out var placement)) { ResetDrag(); return; }

            float halfWidth = block.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
            float leftEdgeX = block.GetX() - halfWidth;
            float startDelay = leftEdgeX / Mathf.Max(1f, PixelsPerSecond);
            if (startDelay < 0f) startDelay = 0f;

            float startAbs = Timeline.CurrentTime + startDelay;
            float endAbs = startAbs + Mathf.Max(0f, placement.DurationSeconds);

            if (WouldOverlap(owner: placement.Owner, lane: placement.Lane, startAbs: startAbs, endAbs: endAbs, ignoreGroupId: _draggingGroupId))
            {
                block.SetX(_dragOriginalX);
                ResetDrag();
                return;
            }

            Timeline.CancelGroup(_draggingGroupId);
            placement.Schedule?.Invoke(startDelay, _draggingGroupId);

            if (_playerBlocks.TryGetValue(_draggingGroupId, out var model))
            {
                model.StartTimeAbs = startAbs;
                _playerBlocks[_draggingGroupId] = model;
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
            UpdatePlacementGhost();

            if (_layoutDirty)
            {
                RecomputePlayerBlocksForLane(PlayerUnit, TimelineLane.Player);
                _layoutDirty = false;
            }

            SyncObservedBlocks();
            RenderAllBlocks();
        }

        private void UpdatePlacementGhost()
        {
            if (_pendingGhost == null || _pendingLane == null) return;

            var mousePos = UnityEngine.Input.mousePosition;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_pendingLane, mousePos, null, out var localPoint))
            {
                float xFromLeft = LocalXToXFromLeft(_pendingLane, localPoint.x);
                float mouseClamped = Mathf.Clamp(xFromLeft, 0f, _pendingLane.rect.width);
                float resolvedCenterFinal = _pendingLastResolvedCenterX;

                for (int iter = 0; iter < 2; iter++)
                {
                    float halfWidth = _pendingGhost.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
                    float desiredCenter = Mathf.Max(0f + halfWidth, mouseClamped);

                    desiredCenter = ApplyKeyframeSnap(desiredCenter, halfWidth, out bool snapped, out float snapX);

                    if (_snapIndicatorLine != null)
                    {
                        if (snapped)
                        {
                            _snapIndicatorLine.enabled = true;
                            SetLineX(_snapIndicatorLine.rectTransform, snapX);
                        }
                        else
                        {
                            _snapIndicatorLine.enabled = false;
                        }
                    }

                    bool movingRight = mouseClamped > _pendingLastMouseXFromLeft + 0.001f;
                    bool movingLeft = mouseClamped < _pendingLastMouseXFromLeft - 0.001f;

                    float resolvedCenter = ResolveGhostCenterInsert(
                        desiredCenter, _pendingPlacement, halfWidth, _pendingLane.rect.width,
                        movingRight, movingLeft, _pendingLastResolvedCenterX);

                    resolvedCenterFinal = resolvedCenter;
                    _pendingGhost.SetX(resolvedCenterFinal);

                    float leftEdgeX = resolvedCenterFinal - halfWidth;
                    float startDelaySeconds = Mathf.Max(0f, leftEdgeX / Mathf.Max(1f, PixelsPerSecond));
                    float startAbs = Timeline.CurrentTime + startDelaySeconds;
                    UpdatePendingPlacementDynamics(startAbs);
                }

                _pendingGhost.SetKeyframeOffsetsSeconds(GetKeyframeOffsetsSeconds(_pendingPlacement.Kind, _pendingPlacement.DurationSeconds), PixelsPerSecond);
                _pendingLastResolvedCenterX = resolvedCenterFinal;
                _pendingLastMouseXFromLeft = mouseClamped;
            }

            bool overLane = RectTransformUtility.RectangleContainsScreenPoint(_pendingLane, mousePos, null);
            if (overLane)
            {
                if (UnityEngine.Input.GetMouseButtonDown(1)) { CancelPlacement(); return; }
                if (UnityEngine.Input.GetMouseButtonDown(0)) { FinalizePlacement(_pendingGhost); return; }
            }
        }

        private void SyncObservedBlocks()
        {
            var snapshot = Timeline.GetScheduledIntentsSnapshot();
            var grouped = snapshot.Where(e => e.GroupId != 0 && e.Owner != null).GroupBy(e => e.GroupId);
            var seenGroups = new HashSet<long>();

            foreach (var g in grouped)
            {
                long groupId = g.Key;
                if (_playerBlocks.ContainsKey(groupId)) continue;

                var owners = g.Select(e => e.Owner).Distinct().ToList();
                if (owners.Count != 1) continue;
                var owner = owners[0];

                TimelineLane lane;
                if (owner == PlayerUnit) lane = TimelineLane.Player;
                else if (owner == ObservedUnit) lane = TimelineLane.Observed;
                else continue;

                seenGroups.Add(groupId);

                float minTime = g.Min(e => e.Time);
                float maxTime = g.Max(e => e.Time);

                var types = g.Select(e => e.Type).ToList();
                TimelineActionKind kind = TimelineActionKind.None;
                if (types.Contains(ActionType.Attack)) kind = TimelineActionKind.Attack;
                else if (types.Contains(ActionType.Block)) kind = TimelineActionKind.Block;
                else if (types.Contains(ActionType.Dodge)) kind = TimelineActionKind.Dodge;
                else if (types.Contains(ActionType.Move)) kind = TimelineActionKind.Move;
                else if (types.Contains(ActionType.Cast)) kind = TimelineActionKind.Attack;

                if (kind == TimelineActionKind.None) continue;

                if (!_observedBlocks.TryGetValue(groupId, out var model))
                {
                    model = new BlockRenderModel
                    {
                        GroupId = groupId,
                        Owner = owner,
                        Lane = lane,
                        IsInteractable = false
                    };
                    model.StartTimeAbs = minTime;
                }

                float currentEnd = model.StartTimeAbs + model.Duration;
                float newEnd = maxTime;

                if (Mathf.Abs(newEnd - currentEnd) > 0.01f)
                {
                    model.Duration = Mathf.Max(0.05f, newEnd - model.StartTimeAbs);
                }

                model.Kind = kind;
                _observedBlocks[groupId] = model;
            }

            var keys = _observedBlocks.Keys.ToList();
            foreach (var key in keys)
            {
                bool isFinished = !seenGroups.Contains(key);
                if (isFinished)
                {
                    var model = _observedBlocks[key];
                    float xLeft = (model.StartTimeAbs - Timeline.CurrentTime) * PixelsPerSecond;
                    float width = model.Duration * PixelsPerSecond;
                    if (xLeft + width < -50f)
                    {
                        _observedBlocks.Remove(key);
                    }
                }
            }
        }

        private void RenderAllBlocks()
        {
            var validViewIds = new HashSet<long>();

            foreach (var model in _playerBlocks.Values) RenderBlockModel(model, validViewIds);
            foreach (var model in _observedBlocks.Values) RenderBlockModel(model, validViewIds);

            var snapshot = Timeline.GetScheduledIntentsSnapshot();
            var singles = snapshot.Where(e => e.GroupId == 0 && (e.Owner == PlayerUnit || e.Owner == ObservedUnit));
            foreach (var e in singles)
            {
                long viewId = -e.Id;
                validViewIds.Add(viewId);
                RectTransform lane = (e.Owner == PlayerUnit) ? PlayerLane : ObservedLane;
                float startX = (e.Time - Timeline.CurrentTime) * PixelsPerSecond;
                float width = MinBlockWidthPx;
                if (startX + width < -50f) continue;

                if (!_activeViews.TryGetValue(viewId, out var view))
                {
                    view = CreateBlockGO(lane);
                    _activeViews[viewId] = view;
                }
                view.SetModel(0, e.Id, e.Time, 0.05f, "", false);
                view.SetWidth(width);
                view.SetX(startX + width * 0.5f);
                view.SetColor(new Color(1f, 1f, 1f, 0.35f));
                view.Background.raycastTarget = false;
            }

            var allKeys = _activeViews.Keys.ToList();
            foreach (var key in allKeys)
            {
                if (!validViewIds.Contains(key))
                {
                    Destroy(_activeViews[key].gameObject);
                    _activeViews.Remove(key);
                }
            }
        }

        private void RenderBlockModel(BlockRenderModel model, HashSet<long> validViewIds)
        {
            if (model.Owner == null) return;

            RectTransform lane = (model.Lane == TimelineLane.Player) ? PlayerLane : ObservedLane;
            if (lane == null) return;

            float width = Mathf.Max(MinBlockWidthPx, model.Duration * PixelsPerSecond);
            float startX = (model.StartTimeAbs - Timeline.CurrentTime) * PixelsPerSecond;

            if (startX + width < -50f)
            {
                if (model.IsInteractable)
                {
                    _playerBlocks.Remove(model.GroupId);
                    _placementsByGroupId.Remove(model.GroupId);
                }
                return;
            }

            validViewIds.Add(model.GroupId);

            if (!_activeViews.TryGetValue(model.GroupId, out var view))
            {
                view = CreateBlockGO(lane);
                _activeViews[model.GroupId] = view;
            }

            if (view.transform.parent != lane)
            {
                view.transform.SetParent(lane, false);
            }

            var rt = view.GetComponent<RectTransform>();
            var pos = rt.anchoredPosition;
            pos.y = 0;
            rt.anchoredPosition = pos;

            string label = "";
            if (model.IsInteractable && _placementsByGroupId.TryGetValue(model.GroupId, out var placement))
            {
                label = placement.Label;
            }

            view.SetModel(model.GroupId, 0, model.StartTimeAbs, model.Duration, label, false);
            view.SetWidth(width);
            view.SetX(startX + width * 0.5f);

            var baseColor = GetBaseColor(model.Kind);
            var finalColor = ApplyDepth(baseColor, model.Duration, isGhost: false);
            if (!model.IsInteractable) finalColor.a = 0.85f;
            view.SetColor(finalColor);

            view.SetKeyframeOffsetsSeconds(GetKeyframeOffsetsSeconds(model.Kind, model.Duration), PixelsPerSecond);
            if (view.Background != null) view.Background.raycastTarget = model.IsInteractable;
        }

        // --- Helpers ---

        private void EnsureTimeLines()
        {
            if (PlayerLane == null || ObservedLane == null) return;

            if (_currentTimeLinePlayer == null) _currentTimeLinePlayer = CreateLine(PlayerLane, "CurrentTimeLine");
            if (_currentTimeLineObserved == null) _currentTimeLineObserved = CreateLine(ObservedLane, "CurrentTimeLine");
            if (_mouseLinePlayer == null) _mouseLinePlayer = CreateLine(PlayerLane, "MouseLine");
            if (_mouseLineObserved == null) _mouseLineObserved = CreateLine(ObservedLane, "MouseLine");

            if (_snapIndicatorLine == null)
            {
                var go = new GameObject("SnapLine", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(transform, false);
                go.transform.SetAsLastSibling();
                var img = go.GetComponent<Image>();
                img.color = new Color(0f, 1f, 1f, 0.8f);
                img.enabled = false;
                var r = go.GetComponent<RectTransform>();
                r.anchorMin = new Vector2(0, 0);
                r.anchorMax = new Vector2(0, 1);
                r.sizeDelta = new Vector2(3, 0);
                _snapIndicatorLine = img;
            }

            if (_mouseTimeText == null && RulerArea != null)
            {
                _mouseTimeText = CreateTimeText("MouseTimeText", RulerArea, Color.yellow);
            }
            if (_nowTimeText == null && RulerArea != null)
            {
                _nowTimeText = CreateTimeText("NowTimeText", RulerArea, Color.white);
            }

            if (_mouseLinePlayer != null) _mouseLinePlayer.enabled = false;
            if (_mouseLineObserved != null) _mouseLineObserved.enabled = false;
        }

        private Text CreateTimeText(string name, RectTransform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();

            // Safe Font Fallback
            if (UiFont != null) t.font = UiFont;
            else
            {
                try { t.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                catch { t.font = Resources.Load<Font>("Arial"); }
            }

            t.fontSize = 14;
            t.color = color;
            t.alignment = TextAnchor.LowerLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;

            var r = go.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 0);
            r.anchorMax = new Vector2(0, 0);
            r.pivot = new Vector2(0f, 0f);
            return t;
        }

        private void UpdateCurrentTimeLines()
        {
            if (_currentTimeLinePlayer == null) return;
            SetLineX(_currentTimeLinePlayer.rectTransform, 0f);
            SetLineX(_currentTimeLineObserved.rectTransform, 0f);

            if (_nowTimeText != null && Timeline != null)
            {
                _nowTimeText.text = $"NOW: {Timeline.CurrentTime:F2}";
                float rulerX = ConvertLaneXToRulerX(0f);
                SetTextX(_nowTimeText.rectTransform, rulerX);
            }
        }

        private void UpdateMouseLineAndTime()
        {
            if (_mouseLinePlayer == null) return;
            var mousePos = UnityEngine.Input.mousePosition;
            bool overP = RectTransformUtility.RectangleContainsScreenPoint(PlayerLane, mousePos, null);
            bool overO = RectTransformUtility.RectangleContainsScreenPoint(ObservedLane, mousePos, null);

            if (!overP && !overO)
            {
                _mouseLinePlayer.enabled = false;
                _mouseLineObserved.enabled = false;
                if (_mouseTimeText != null) _mouseTimeText.enabled = false;
                return;
            }

            RectTransform lane = overP ? PlayerLane : ObservedLane;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(lane, mousePos, null, out var lp)) return;

            float x = Mathf.Clamp(LocalXToXFromLeft(lane, lp.x), 0f, lane.rect.width);
            _mouseLinePlayer.enabled = true;
            _mouseLineObserved.enabled = true;
            SetLineX(_mouseLinePlayer.rectTransform, x);
            SetLineX(_mouseLineObserved.rectTransform, x);

            float t = Timeline != null ? Timeline.CurrentTime + x / PixelsPerSecond : 0f;

            if (_mouseTimeText != null)
            {
                _mouseTimeText.enabled = true;
                _mouseTimeText.text = $"{t:F2}s";
                float rulerX = ConvertLaneXToRulerX(x);
                SetTextX(_mouseTimeText.rectTransform, rulerX);

                if (_nowTimeText != null)
                {
                    float dist = Mathf.Abs(_nowTimeText.rectTransform.anchoredPosition.x - rulerX);
                    _nowTimeText.enabled = dist > 60f;
                }
            }
        }

        private float ConvertLaneXToRulerX(float laneX)
        {
            if (PlayerLane == null || RulerArea == null) return laneX;
            float localXInLane = laneX - PlayerLane.rect.width * PlayerLane.pivot.x;
            Vector3 worldPos = PlayerLane.TransformPoint(new Vector3(localXInLane, 0, 0));
            Vector2 localPoint;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(RulerArea, RectTransformUtility.WorldToScreenPoint(null, worldPos), null, out localPoint);
            return localPoint.x;
        }

        private void SetLineX(RectTransform r, float x) { var p = r.anchoredPosition; p.x = x; r.anchoredPosition = p; }
        private void SetTextX(RectTransform r, float x) { var p = r.anchoredPosition; p.x = x; r.anchoredPosition = p; }

        private void UpdatePendingPlacementDynamics(float startAbs)
        {
            if (_pendingPlacement == null || _pendingGhost == null) return;
            if (_pendingPlacement.Kind != TimelineActionKind.Move || !_pendingPlacement.MoveDestination.HasValue) return;

            float t = QuantizeSeconds(startAbs, PredictionTimeQuantumSeconds);
            var predictedStart = PredictUnitGridPositionAt(_pendingPlacement.Owner, t, _isRepositioning ? _repositionGroupId : 0);

            var obstacles = GridManager.Instance != null ? GridManager.Instance.GetGlobalObstacles(_pendingPlacement.Owner) : null;
            var pathfinder = new Pathfinder();
            var path = pathfinder.FindPath(predictedStart, _pendingPlacement.MoveDestination.Value, _pendingPlacement.Owner.UnitVolumeDefinition, obstacles);

            float newDuration = ActionScheduler.EstimateMoveDuration(_pendingPlacement.Owner, path);
            if (newDuration <= 0f) newDuration = _pendingPlacement.DurationSeconds;
            newDuration = QuantizeSeconds(newDuration, DurationQuantumSeconds);

            if (Mathf.Abs(newDuration - _pendingPlacement.DurationSeconds) > 0.001f)
            {
                _pendingPlacement.DurationSeconds = newDuration;
                _pendingGhost.SetModel(0, 0, startAbs, newDuration, _pendingPlacement.Label, true);
                float width = Mathf.Max(MinBlockWidthPx, newDuration * PixelsPerSecond);
                _pendingGhost.SetWidth(width);
                _pendingGhost.SetColor(ApplyDepth(GetBaseColor(_pendingPlacement.Kind), newDuration, true));
            }
        }

        private Pathfinder.GridPoint PredictUnitGridPositionAt(CombatUnit unit, float timeAbs, long ignoreGroupId)
        {
            var pos = unit.GridPosition;
            foreach (var kvp in _playerBlocks.OrderBy(k => k.Value.StartTimeAbs))
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

        private bool WouldOverlap(CombatUnit owner, TimelineLane lane, float startAbs, float endAbs, long ignoreGroupId)
        {
            foreach (var model in _playerBlocks.Values)
            {
                if (model.GroupId == ignoreGroupId) continue;
                if (model.Owner != owner || model.Lane != lane) continue;

                float otherStart = model.StartTimeAbs;
                float otherEnd = model.StartTimeAbs + Mathf.Max(0f, model.Duration);
                if (startAbs < otherEnd && endAbs > otherStart) return true;
            }
            return false;
        }

        private float ResolveGhostCenterInsert(float desiredCenterX, TimelineActionPlacement placement, float halfWidth, float laneWidth, bool movingRight, bool movingLeft, float lastResolvedCenterX)
        {
            float width = halfWidth * 2f;
            float left = Mathf.Max(0f, desiredCenterX - halfWidth);
            var intervals = new List<(float left, float right)>();

            foreach (var b in _playerBlocks.Values)
            {
                if (b.Owner != placement.Owner || b.Lane != placement.Lane) continue;
                float w = Mathf.Max(MinBlockWidthPx, b.Duration * PixelsPerSecond);
                float l = (b.StartTimeAbs - Timeline.CurrentTime) * PixelsPerSecond;
                intervals.Add((l, l + w));
            }
            intervals.Sort((a, b) => a.left.CompareTo(b.left));

            for (int i = 0; i < intervals.Count + 2; i++)
            {
                float right = left + width;
                (float oLeft, float oRight)? overlapLeftNeighbor = null;
                foreach (var iv in intervals)
                {
                    if (iv.left < left - 0.001f && left < iv.right && right > iv.left)
                    {
                        overlapLeftNeighbor = (iv.left, iv.right);
                        break;
                    }
                }
                if (overlapLeftNeighbor == null) break;
                left = overlapLeftNeighbor.Value.oRight;
            }
            return left + halfWidth;
        }

        private void RecomputePlayerBlocksForLane(CombatUnit owner, TimelineLane lane)
        {
            if (Timeline == null || owner == null) return;

            var ids = _playerBlocks.Values
                .Where(b => b.Owner == owner && b.Lane == lane)
                .OrderBy(b => b.StartTimeAbs)
                .Select(b => b.GroupId)
                .ToList();

            if (ids.Count == 0) return;

            var predictedPos = owner.GridPosition;

            for (int i = 0; i < ids.Count; i++)
            {
                long id = ids[i];
                var model = _playerBlocks[id];

                float duration = Mathf.Max(0f, model.Duration);
                if (model.Kind == TimelineActionKind.Move && model.MoveDestination.HasValue)
                {
                    var obstacles = GridManager.Instance != null ? GridManager.Instance.GetGlobalObstacles(owner) : null;
                    var pathfinder = new Pathfinder();
                    var path = pathfinder.FindPath(predictedPos, model.MoveDestination.Value, owner.UnitVolumeDefinition, obstacles);
                    float d = ActionScheduler.EstimateMoveDuration(owner, path);
                    if (d > 0f) duration = QuantizeSeconds(d, DurationQuantumSeconds);
                    predictedPos = model.MoveDestination.Value;
                }

                if (Mathf.Abs(model.Duration - duration) > 0.001f)
                {
                    model.Duration = duration;
                    _playerBlocks[id] = model;
                }
            }

            for (int i = 0; i < ids.Count - 1; i++)
            {
                long leftId = ids[i];
                long rightId = ids[i + 1];
                var left = _playerBlocks[leftId];
                var right = _playerBlocks[rightId];
                float leftEnd = left.StartTimeAbs + Mathf.Max(0f, left.Duration);
                if (right.StartTimeAbs < leftEnd)
                {
                    float delta = leftEnd - right.StartTimeAbs;
                    for (int j = i + 1; j < ids.Count; j++)
                    {
                        long id = ids[j];
                        var m = _playerBlocks[id];
                        m.StartTimeAbs += delta;
                        _playerBlocks[id] = m;
                    }
                }
            }

            for (int i = 0; i < ids.Count; i++)
            {
                long id = ids[i];
                var model = _playerBlocks[id];
                if (model.StartTimeAbs < Timeline.CurrentTime + 0.0001f) continue;
                if (_placementsByGroupId.TryGetValue(id, out var placement) && placement != null)
                {
                    placement.DurationSeconds = model.Duration;
                    Timeline.CancelGroup(id);
                    float delay = Mathf.Max(0f, model.StartTimeAbs - Timeline.CurrentTime);
                    placement.Schedule?.Invoke(delay, id);
                }
            }
        }

        private float ApplyKeyframeSnap(float desiredCenterX, float halfWidth, out bool snapped, out float snapX)
        {
            snapped = false;
            snapX = 0f;

            float snapPx = SnapThresholdSeconds * Mathf.Max(1f, PixelsPerSecond);
            float leftEdgeX = desiredCenterX - halfWidth;
            float ghostStartAbs = Timeline.CurrentTime + Mathf.Max(0f, leftEdgeX / Mathf.Max(1f, PixelsPerSecond));

            var ghostOffsets = GetKeyframeOffsetsSeconds(_pendingPlacement.Kind, _pendingPlacement.DurationSeconds);
            var candidateTimes = new List<float>();

            foreach (var m in _playerBlocks.Values)
            {
                if (_isRepositioning && m.GroupId == _repositionGroupId) continue;
                var offsets = GetKeyframeOffsetsSeconds(m.Kind, m.Duration);
                foreach (var o in offsets) candidateTimes.Add(m.StartTimeAbs + o);
            }
            foreach (var m in _observedBlocks.Values)
            {
                var offsets = GetKeyframeOffsetsSeconds(m.Kind, m.Duration);
                foreach (var o in offsets) candidateTimes.Add(m.StartTimeAbs + o);
            }

            float bestDeltaPx = float.MaxValue;
            float bestSnapShiftSeconds = 0f;
            float bestTargetTime = 0f;

            foreach (var gk in ghostOffsets)
            {
                float ghostKeyAbs = ghostStartAbs + gk;
                foreach (var ct in candidateTimes)
                {
                    float d = ct - ghostKeyAbs;
                    float dPx = Mathf.Abs(d * PixelsPerSecond);

                    if (dPx <= snapPx && dPx < bestDeltaPx)
                    {
                        bestDeltaPx = dPx;
                        bestSnapShiftSeconds = d;
                        bestTargetTime = ct;
                    }
                }
            }

            if (bestDeltaPx != float.MaxValue)
            {
                snapped = true;
                float shiftPx = bestSnapShiftSeconds * PixelsPerSecond;
                float finalCenterX = desiredCenterX + shiftPx;
                snapX = (bestTargetTime - Timeline.CurrentTime) * PixelsPerSecond;
                return finalCenterX;
            }

            float quantizedStart = QuantizeSeconds(ghostStartAbs, PredictionTimeQuantumSeconds);
            float qDelay = quantizedStart - Timeline.CurrentTime;
            float qCenterX = (qDelay * PixelsPerSecond) + halfWidth;

            return qCenterX;
        }

        private float QuantizeSeconds(float value, float quantum) => quantum <= 0f ? value : Mathf.Round(value / quantum) * quantum;

        private List<float> GetKeyframeOffsetsSeconds(TimelineActionKind kind, float durationSeconds)
        {
            var offsets = new List<float>();
            float d = Mathf.Max(0f, durationSeconds);
            if (kind == TimelineActionKind.Move) offsets.Add(d);
            else if (kind == TimelineActionKind.Attack) offsets.Add(Mathf.Clamp(d - 0.5f, 0f, d));
            return offsets;
        }

        private Color ApplyDepth(Color baseColor, float durationSeconds, bool isGhost)
        {
            float t = DeepenAtSeconds <= 0f ? 1f : Mathf.Clamp01(durationSeconds / DeepenAtSeconds);
            float darken = Mathf.Lerp(0f, MaxDarkenFactor, t);
            var c = new Color(baseColor.r * (1f - darken), baseColor.g * (1f - darken), baseColor.b * (1f - darken), 1f);
            c.a = isGhost ? 0.45f : 0.90f;
            return c;
        }

        private float LocalXToXFromLeft(RectTransform lane, float localX) => localX + lane.rect.width * lane.pivot.x;

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
            view.Init(this, lane, Canvas);
            return view;
        }

        private void EnsurePlacementShield(RectTransform lane)
        {
            if (lane == null) return;
            if (_placementShield != null)
            {
                if (_placementShield.transform.parent != lane) _placementShield.transform.SetParent(lane, false);
                _placementShield.transform.SetAsLastSibling();
                return;
            }
            var go = new GameObject("PlacementShield", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(lane, false);
            go.transform.SetAsLastSibling();
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            var img = go.GetComponent<Image>();
            img.color = Color.clear;
            img.raycastTarget = true;
            _placementShield = img;
        }
        private void DestroyPlacementShield() { if (_placementShield != null) { Destroy(_placementShield.gameObject); _placementShield = null; } }
        private void EnsureLaneMasks() { if (PlayerLane != null && PlayerLane.GetComponent<RectMask2D>() == null) PlayerLane.gameObject.AddComponent<RectMask2D>(); if (ObservedLane != null && ObservedLane.GetComponent<RectMask2D>() == null) ObservedLane.gameObject.AddComponent<RectMask2D>(); }
        private Image CreateLine(RectTransform lane, string name) { var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)); go.transform.SetParent(lane, false); var img = go.GetComponent<Image>(); img.color = new Color(1f, 1f, 1f, 0.8f); var r = go.GetComponent<RectTransform>(); r.anchorMin = new Vector2(0, 0); r.anchorMax = new Vector2(0, 1); r.sizeDelta = new Vector2(2, 0); return img; }
    }
}
