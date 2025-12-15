using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Gameplay;
using ProjectHero.Core.Actions;
using ProjectHero.UI.Timeline;
using UnityEngine.UI;
using TMPro;
using ProjectHero.Core.Timeline;

namespace ProjectHero.UI
{
    public class UIManager : MonoBehaviour
    {
        public static UIManager Instance { get; private set; }

        [Header("References")]
        public GameObject ActionPanel; // The parent container (Horizontal Layout Group)
        public ActionButton ButtonPrefab;
        public TacticsController Controller;

        [Header("Timeline UI")]
        public TimelineEditorUI TimelineUI;

        [Header("Pause UI")]
        public Button PauseButton;

        private BattleTimeline _timeline;
        private GameObject _pauseBorderRoot;
        private TextMeshProUGUI _pauseButtonLabel;

        private List<ActionButton> _spawnedButtons = new List<ActionButton>();

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        private void Start()
        {
            if (Controller == null) Controller = FindFirstObjectByType<TacticsController>();

            EnsureTimelineUI();
            EnsurePauseUI();
            
            // Hide panel initially
            if (ActionPanel != null) ActionPanel.SetActive(false);
        }

        private void Update()
        {
            if (_timeline == null) _timeline = FindFirstObjectByType<BattleTimeline>();
            bool paused = _timeline != null && _timeline.Paused;

            if (_pauseBorderRoot != null) _pauseBorderRoot.SetActive(paused);
            // Use ASCII-safe icons to avoid missing TMP glyphs.
            if (_pauseButtonLabel != null) _pauseButtonLabel.text = paused ? ">" : "||";
        }

        private void EnsurePauseUI()
        {
            var canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null) return;

            if (_timeline == null) _timeline = FindFirstObjectByType<BattleTimeline>();

            // Border overlay (bright purple), non-blocking.
            if (_pauseBorderRoot == null)
            {
                const float thickness = 8f;
                var color = new Color(0.85f, 0.25f, 1.0f, 1f);

                var root = new GameObject("PauseBorder", typeof(RectTransform));
                root.transform.SetParent(canvas.transform, false);
                root.transform.SetAsLastSibling();

                var rootRect = root.GetComponent<RectTransform>();
                rootRect.anchorMin = Vector2.zero;
                rootRect.anchorMax = Vector2.one;
                rootRect.offsetMin = Vector2.zero;
                rootRect.offsetMax = Vector2.zero;

                CreateBorderEdge(root.transform, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, thickness), Vector2.zero, color);
                CreateBorderEdge(root.transform, "Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, thickness), Vector2.zero, color);
                CreateBorderEdge(root.transform, "Left", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(thickness, 0f), Vector2.zero, color);
                CreateBorderEdge(root.transform, "Right", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(thickness, 0f), Vector2.zero, color);

                root.SetActive(false);
                _pauseBorderRoot = root;
            }

            if (PauseButton == null)
            {
                var btnGo = new GameObject("PauseButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Outline), typeof(Button));
                // Prefer placing relative to the timeline UI so it sits right above it.
                if (TimelineUI != null) btnGo.transform.SetParent(TimelineUI.transform, false);
                else btnGo.transform.SetParent(canvas.transform, false);
                btnGo.transform.SetAsLastSibling();

                var rect = btnGo.GetComponent<RectTransform>();
                if (TimelineUI != null)
                {
                    // TimelineUI root is bottom-anchored; its top edge is where we want the button.
                    rect.anchorMin = new Vector2(0.5f, 1f);
                    rect.anchorMax = new Vector2(0.5f, 1f);
                    rect.pivot = new Vector2(0.5f, 0f);
                    rect.anchoredPosition = new Vector2(0f, 10f);
                }
                else
                {
                    rect.anchorMin = new Vector2(1f, 1f);
                    rect.anchorMax = new Vector2(1f, 1f);
                    rect.pivot = new Vector2(1f, 1f);
                    rect.anchoredPosition = new Vector2(-16f, -16f);
                }
                rect.sizeDelta = new Vector2(72f, 52f);

                var img = btnGo.GetComponent<Image>();
                img.color = new Color(0f, 0f, 0f, 0.55f);

                var outline = btnGo.GetComponent<Outline>();
                outline.effectColor = new Color(1f, 1f, 1f, 0.85f);
                outline.effectDistance = new Vector2(2f, -2f);
                outline.useGraphicAlpha = true;

                PauseButton = btnGo.GetComponent<Button>();

                var labelGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                labelGo.transform.SetParent(btnGo.transform, false);
                var labelRect = labelGo.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;

                var tmp = labelGo.GetComponent<TextMeshProUGUI>();
                tmp.text = "||";
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.fontSize = 28;
                tmp.color = Color.white;
                _pauseButtonLabel = tmp;

                PauseButton.onClick.AddListener(TogglePause);
            }

            if (PauseButton != null && _pauseButtonLabel == null)
            {
                _pauseButtonLabel = PauseButton.GetComponentInChildren<TextMeshProUGUI>();
            }
        }

        private static void CreateBorderEdge(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 sizeDelta, Vector2 anchoredPos, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.sizeDelta = sizeDelta;
            rt.anchoredPosition = anchoredPos;

            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }

        private void TogglePause()
        {
            if (_timeline == null) _timeline = FindFirstObjectByType<BattleTimeline>();
            if (_timeline == null) return;
            _timeline.SetPaused(!_timeline.Paused);
        }

        public void OnUnitSelected(CombatUnit unit)
        {
            if (ActionPanel == null || ButtonPrefab == null) return;

            ActionPanel.SetActive(true);
            ClearButtons();

            var palette = TimelineUI;

            // 1. Add "Move" Button
            CreateButton("Move", null, (a) => Controller.SelectMove(), palette != null ? palette.GetBaseColor(TimelineActionKind.Move) : (Color?)null);

            // 2. Add Action Buttons
            if (unit.ActionLibrary != null)
            {
                foreach (var entry in unit.ActionLibrary.Actions)
                {
                    CreateButton(entry.Data.Name, entry.Data, (a) => Controller.SelectAction(a), palette != null ? palette.GetBaseColor(TimelineActionKind.Attack) : (Color?)null);
                }
            }
            
            // 3. Add Defensive Actions
            CreateButton("Block", null, (a) => Controller.ExecuteBlock(), palette != null ? palette.GetBaseColor(TimelineActionKind.Block) : (Color?)null);
            CreateButton("Dodge", null, (a) => Controller.ExecuteDodge(), palette != null ? palette.GetBaseColor(TimelineActionKind.Dodge) : (Color?)null);

            // 3.5 Recover (stand up / regain balance)
            CreateButton("Recover", null, (a) => Controller.ExecuteRecover(), palette != null ? palette.GetBaseColor(TimelineActionKind.Recover) : (Color?)null);
            
            // 4. Add "Wait" Button (End Turn)
            // CreateButton("Wait", null, (a) => Debug.Log("Wait clicked"));

            if (TimelineUI != null)
            {
                TimelineUI.SetPlayerUnit(unit);
            }
        }

        public void OnUnitDeselected()
        {
            if (ActionPanel != null) ActionPanel.SetActive(false);
            ClearButtons();
        }

        private void CreateButton(string name, Action action, System.Action<Action> callback, Color? colorOverride = null)
        {
            var btnObj = Instantiate(ButtonPrefab, ActionPanel.transform);

            // Ensure buttons are large enough to click.
            var layout = btnObj.GetComponent<LayoutElement>();
            if (layout == null) layout = btnObj.gameObject.AddComponent<LayoutElement>();
            layout.minWidth = 140f;
            layout.minHeight = 56f;
            layout.preferredWidth = 160f;
            layout.preferredHeight = 56f;

            var btnScript = btnObj.GetComponent<ActionButton>();
            if (btnScript != null)
            {
                btnScript.Setup(name, action, callback, colorOverride);
                _spawnedButtons.Add(btnScript);
            }
        }

        private void ClearButtons()
        {
            foreach (var btn in _spawnedButtons)
            {
                if (btn != null) Destroy(btn.gameObject);
            }
            _spawnedButtons.Clear();
        }

        private void EnsureTimelineUI()
        {
            if (TimelineUI != null) return;

            TimelineUI = FindFirstObjectByType<TimelineEditorUI>();
            if (TimelineUI != null) return;

            var canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null)
            {
                var canvasObj = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas = canvasObj.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvasObj.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
            }

            var root = new GameObject("TimelineUI", typeof(RectTransform));
            root.transform.SetParent(canvas.transform, false);
            var rootRect = root.GetComponent<RectTransform>();
            // Stretch horizontally with screen; keep a fixed height.
            rootRect.anchorMin = new Vector2(0f, 0f);
            rootRect.anchorMax = new Vector2(1f, 0f);
            rootRect.pivot = new Vector2(0.5f, 0f);
            rootRect.offsetMin = new Vector2(10f, 10f);
            rootRect.offsetMax = new Vector2(-10f, 150f);

            TimelineUI = root.AddComponent<TimelineEditorUI>();
            TimelineUI.Canvas = canvas;

            var bg = root.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.35f);

            // Observed lane (top)
            var observedLane = new GameObject("ObservedLane", typeof(RectTransform), typeof(Image));
            observedLane.transform.SetParent(root.transform, false);
            var observedRect = observedLane.GetComponent<RectTransform>();
            observedRect.anchorMin = new Vector2(0f, 0.5f);
            observedRect.anchorMax = new Vector2(1f, 1f);
            observedRect.offsetMin = new Vector2(10f, 8f);
            observedRect.offsetMax = new Vector2(-10f, -8f);
            observedLane.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.06f);

            // Player lane (bottom)
            var playerLane = new GameObject("PlayerLane", typeof(RectTransform), typeof(Image));
            playerLane.transform.SetParent(root.transform, false);
            var playerRect = playerLane.GetComponent<RectTransform>();
            playerRect.anchorMin = new Vector2(0f, 0f);
            playerRect.anchorMax = new Vector2(1f, 0.5f);
            playerRect.offsetMin = new Vector2(10f, 8f);
            playerRect.offsetMax = new Vector2(-10f, -8f);
            playerLane.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.08f);

            TimelineUI.PlayerLane = playerRect;
            TimelineUI.ObservedLane = observedRect;
        }
    }
}