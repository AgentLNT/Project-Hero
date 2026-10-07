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

        /// <summary>
        /// 本块代表的 <strong>Logic 普通计划 ID</strong>（任务 09 / B2 时间线块标识语义）。
        ///
        /// <list type="bullet">
        /// <item><strong>端口接线后</strong>：它<strong>就是</strong>
        /// <c>ActionPlanSnapshot.ActionPlanId</c>（唯一只读投影来源
        /// <c>IViewLogicPort.EditablePlansOf</c>）。删除/重排直接拿它构造
        /// <c>RemoveEditablePlanOperation</c> / <c>MoveEditablePlanOperation</c>。</item>
        /// <item><strong>未接线</strong>（旧 Legacy 场景）：它仍是旧
        /// <c>BattleTimeline.ReserveGroupId()</c> 的组号，但那时它是<strong>本地渲染键</strong>——
        /// 旧场景里不存在任何 Logic 计划，因此任何"把它当 Logic 计划 ID"的路径都会在
        /// <c>IViewLogicPort</c> 的只读判定上失败，绝不会落到旧时间线写入。</item>
        /// <item>它<strong>不是</strong> <c>GetInstanceID()</c>/<c>GetEntityId()</c>/注册顺序
        /// （00 号规则 16 禁止不稳定键参与决策路径）。</item>
        /// </list>
        /// </summary>
        public long ActionPlanId { get; private set; }

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

        /// <summary>
        /// 设置块的渲染模型。
        ///
        /// <paramref name="actionPlanId"/> 的语义见 <see cref="ActionPlanId"/>：
        /// 接线后 = Logic 计划 ID；未接线 = 旧时间线本地组号（仅渲染键）。
        /// </summary>
        public void SetModel(long actionPlanId, long eventId, float startTime, float duration, string label, bool isGhost)
        {
            ActionPlanId = actionPlanId;
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
                // 任务 09：删除 Editable 普通计划必须经唯一命令入口（RemoveEditablePlanOperation）。
                // 未注入端口时编辑器会拒绝删除，因此这里不再回退到直接删计划的旧路径。
                // 接线后 Locked/Running/反应计划在 UI 侧即不可删：右键直接不响应
                // （与 Background.raycastTarget 的门槛同一判据，见 TimelineEditorUI.CanEditBlockInUi）。
                if (_editor != null && _editor.IsInputPortsBound && !_editor.IsPlanEditableInUi(ActionPlanId)) return;
                _editor?.RequestDelete(this);
                return;
            }

            if (eventData.button == PointerEventData.InputButton.Left)
            {
                if (IsGhost) return;
                // 任务 09：接线后 Locked/Running/反应计划在 UI 侧即不可拖；
                // 未接线时不带 plan id 的旧块保持原行为（只改本地渲染）。
                if (_editor != null && _editor.IsInputPortsBound && !_editor.IsPlanEditableInUi(ActionPlanId)) return;
                _editor?.RequestReposition(this);
            }
        }
    }
}
