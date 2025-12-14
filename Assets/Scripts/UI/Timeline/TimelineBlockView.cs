using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System.Collections.Generic;

namespace ProjectHero.UI.Timeline
{
    [RequireComponent(typeof(RectTransform))]
    public class TimelineBlockView : MonoBehaviour, IPointerClickHandler
    {
        [Header("UI")]
        public Image Background;
        public Text Label;

        public long GroupId { get; private set; }
        public long EventId { get; private set; }
        public float StartTime { get; private set; }
        public float Duration { get; private set; }
        public bool IsGhost { get; private set; }

        private RectTransform _rect;
        private RectTransform _parentRect;
        private Canvas _canvas;
        private TimelineEditorUI _editor;

        private readonly List<Image> _keyframeMarkers = new();

        public void Init(TimelineEditorUI editor, RectTransform parentRect, Canvas canvas)
        {
            _editor = editor;
            _parentRect = parentRect;
            _canvas = canvas;
            _rect = GetComponent<RectTransform>();
        }

        public void SetModel(long groupId, long eventId, float startTime, float duration, string label, bool isGhost)
        {
            GroupId = groupId;
            EventId = eventId;
            StartTime = startTime;
            Duration = duration;
            IsGhost = isGhost;

            if (Label != null) Label.text = label;
            // Background color is controlled by TimelineEditorUI.
        }

        public void SetColor(Color color)
        {
            if (Background != null) Background.color = color;
        }

        public void SetKeyframeOffsetsSeconds(IReadOnlyList<float> offsetsSeconds, float pixelsPerSecond)
        {
            if (_rect == null) _rect = GetComponent<RectTransform>();

            int targetCount = offsetsSeconds == null ? 0 : offsetsSeconds.Count;

            // Grow
            while (_keyframeMarkers.Count < targetCount)
            {
                var go = new GameObject("Keyframe", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(transform, false);

                var r = go.GetComponent<RectTransform>();
                r.anchorMin = new Vector2(0f, 0f);
                r.anchorMax = new Vector2(0f, 1f);
                r.pivot = new Vector2(0.5f, 0.5f);
                r.sizeDelta = new Vector2(2f, 0f);

                var img = go.GetComponent<Image>();
                img.raycastTarget = false;
                img.color = new Color(1f, 1f, 1f, 0.85f);

                _keyframeMarkers.Add(img);
            }

            // Shrink
            for (int i = _keyframeMarkers.Count - 1; i >= targetCount; i--)
            {
                if (_keyframeMarkers[i] != null) Destroy(_keyframeMarkers[i].gameObject);
                _keyframeMarkers.RemoveAt(i);
            }

            // Position
            for (int i = 0; i < targetCount; i++)
            {
                float t = Mathf.Clamp(offsetsSeconds[i], 0f, Mathf.Max(0f, Duration));
                float x = t * Mathf.Max(1f, pixelsPerSecond);

                var mr = _keyframeMarkers[i].rectTransform;
                mr.anchoredPosition = new Vector2(x, 0f);
            }
        }

        public void SetWidth(float width)
        {
            if (_rect == null) _rect = GetComponent<RectTransform>();
            var size = _rect.sizeDelta;
            size.x = width;
            _rect.sizeDelta = size;
        }

        public void SetX(float x)
        {
            if (_rect == null) _rect = GetComponent<RectTransform>();
            var pos = _rect.anchoredPosition;
            pos.x = x;
            _rect.anchoredPosition = pos;
        }

        public float GetX()
        {
            if (_rect == null) _rect = GetComponent<RectTransform>();
            return _rect.anchoredPosition.x;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            // While placing a ghost, clicks should be handled by TimelineEditorUI (place/cancel),
            // not by underlying blocks.
            if (_editor != null && (_editor.HasPendingPlacement || _editor.SuppressBlockClicks))
            {
                return;
            }

            if (eventData.button == PointerEventData.InputButton.Right)
            {
                _editor?.RequestDelete(this);
                return;
            }

            if (eventData.button == PointerEventData.InputButton.Left)
            {
                if (IsGhost) return;
                _editor?.RequestReposition(this);
            }
        }
    }
}
