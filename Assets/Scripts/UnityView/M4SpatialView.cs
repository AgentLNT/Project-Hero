using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Snapshots;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace ProjectHero.UnityView
{
    /// <summary>Read-only time cursor, world geometry and HUD accessibility. Owns no simulation.</summary>
    public sealed class M4SpatialView : MonoBehaviour
    {
        private BattlePresentationView _view;
        private GridView _grid;
        private Camera _camera;
        private RectTransform _canvas, _tools, _labels, _diagnostics;
        private TMP_Text _caption, _diagnosticText;
        private Mesh _ghostMesh, _rangeMesh, _pathMesh, _configuredMesh;
        private Material _ghostMaterial, _rangeMaterial, _pathMaterial, _configuredMaterial;
        private GameObject _world;
        private string _key;
        private long? _tick;
        private bool _highContrast, _diagnosticsVisible;
        private float _textScale = 1;
        private readonly Dictionary<TMP_Text, float> _baseSizes = new Dictionary<TMP_Text, float>();
        private readonly Dictionary<UnityEngine.UI.Image, Color> _baseColors = new Dictionary<UnityEngine.UI.Image, Color>();
        private readonly List<GameObject> _worldLabels = new List<GameObject>();
        public long CursorTick => _tick ?? _view.Model.Decision.Tick;
        public bool Following => !_tick.HasValue;
        public SpatialPreview Preview { get; private set; }
        public float TextScale => _textScale;
        public bool HighContrast => _highContrast;
        public bool DiagnosticsVisible => _diagnosticsVisible;
        public TimelineContactPreview SelectedContact { get; private set; }
        public void SelectContact(TimelineContactPreview contact)
        {
            if (_view.Clock.State != BattleClockState.TacticalPlanning || _view.ResumeChoiceVisible) return;
            SelectedContact = contact; _key = null;
            _view.TrackSelection.AddTemporary(contact.OwnerUnitId); _view.TrackSelection.AddTemporary(contact.TargetUnitId);
            _view.TimelineView.FocusTick(contact.Tick); SetCursor(contact.Tick);
        }

        public void Bind(BattlePresentationView view, GridView grid, Camera camera, RectTransform canvas, TMP_FontAsset font)
        {
            _view = view; _grid = grid; _camera = camera; _canvas = canvas;
            _tick = null; _key = null; Preview = null; SelectedContact = null;
            if (_tools != null) { _tools.gameObject.SetActive(true); return; }
            _tools = Region(canvas, "M4SpatialTools", .28f, .84f, .70f, .895f);
            _tools.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(.025f, .045f, .07f, .97f);
            _caption = Label(Region(_tools, "CursorCaption", 0, .48f, 1, 1), "", font, 16);
            MakeButton(Region(_tools, "TextSmaller", 0, 0, .19f, .48f), "字号−", () => SetTextScale(_textScale - .1f), font);
            MakeButton(Region(_tools, "TextLarger", .20f, 0, .39f, .48f), "字号＋", () => SetTextScale(_textScale + .1f), font);
            MakeButton(Region(_tools, "Contrast", .40f, 0, .69f, .48f), "高对比", () => SetHighContrast(!_highContrast), font);
            var diagnosticButton = MakeButton(Region(_tools, "Diagnostics", .70f, 0, 1, .48f), "诊断 F3", ToggleDiagnostics, font);
            diagnosticButton.gameObject.SetActive(Debug.isDebugBuild || Application.isEditor);
            _labels = Region(canvas, "M4WorldLabels", 0, 0, 1, 1); _labels.SetAsFirstSibling();
            _diagnostics = Region(canvas, "M4DiagnosticLayer", .02f, .48f, .27f, .84f);
            _diagnostics.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(0, 0, 0, .98f);
            var diagnosticCanvas = _diagnostics.gameObject.AddComponent<Canvas>(); diagnosticCanvas.overrideSorting = true; diagnosticCanvas.sortingOrder = 200;
            _diagnosticText = Label(Region(_diagnostics, "Facts", .03f, .03f, .97f, .97f), "", font, 16);
            _diagnostics.gameObject.SetActive(false);
            _world = new GameObject("M4ReadOnlySpace"); _world.transform.SetParent(_grid.transform, false);
            _ghostMesh = MakeMesh("PlannedFootprints", new Color(.18f, .95f, 1, .38f), out _ghostMaterial);
            _rangeMesh = MakeMesh("PublishedAttackOutlines", new Color(1, .55f, .15f, .9f), out _rangeMaterial);
            _pathMesh = MakeMesh("PlannedPaths", new Color(.3f, 1, .9f, .9f), out _pathMaterial);
            _configuredMesh = MakeMesh("ConfiguredAttack", new Color(1, .7f, .15f, .3f), out _configuredMaterial);
        }
        public void SetCursor(long tick)
        {
            if (_view.Clock.State != BattleClockState.TacticalPlanning || _view.ResumeChoiceVisible) return;
            _tick = Math.Max(_view.Model.Decision.Tick, Math.Min(_view.Model.Decision.Tick + 3600, tick));
            Refresh(); _view.TimelineView?.RefreshCursor(); _view.ReactionView?.Refresh(true);
        }
        public void Nudge(int direction) => SetCursor(CursorTick + direction * Math.Max(1, _view.Model.Decision.Definition.TicksPerSecond / 10));
        public void ReturnToNow() { _tick = null; _key = null; Refresh(); _view.ReactionView?.Refresh(true); }
        public void SetTextScale(float scale) { _textScale = Mathf.Clamp(scale, 1, 1.3f); ApplyAccessibility(); }
        public void SetHighContrast(bool enabled) { _highContrast = enabled; ApplyAccessibility(); }
        public void ToggleDiagnostics()
        {
            if (!Debug.isDebugBuild && !Application.isEditor) return;
            _diagnosticsVisible = !_diagnosticsVisible; _diagnostics.gameObject.SetActive(_diagnosticsVisible); Refresh();
        }
        private void Update()
        {
            if (_view?.Model.Decision == null || _tools == null || !_tools.gameObject.activeInHierarchy) return;
            var keyboard = Keyboard.current;
            var selected = EventSystem.current?.currentSelectedGameObject;
            bool typing = selected?.GetComponent<TMP_InputField>()?.isFocused == true;
            bool sliderFocused = selected != null && selected.GetComponent<UnityEngine.UI.Slider>() != null;
            if (!typing && !_view.ResumeChoiceVisible && _view.Clock.State != BattleClockState.SystemPaused && _view.Clock.State != BattleClockState.Ended)
            {
                if (keyboard?.f3Key.wasPressedThisFrame == true) ToggleDiagnostics();
                if (_view.Clock.State == BattleClockState.TacticalPlanning && !sliderFocused)
                {
                    if (keyboard?.leftArrowKey.wasPressedThisFrame == true) Nudge(-1);
                    if (keyboard?.rightArrowKey.wasPressedThisFrame == true) Nudge(1);
                    if (keyboard?.homeKey.wasPressedThisFrame == true) { ReturnToNow(); _view.TimelineView?.RefreshCursor(); }
                }
            }
        }
        public void Refresh()
        {
            if (_view?.Model.Decision == null || _tools == null || _camera == null || _world == null || _canvas == null) return;
            bool planning = _view.Clock.State == BattleClockState.TacticalPlanning;
            bool blocked = _view.Clock.State == BattleClockState.SystemPaused || _view.Clock.State == BattleClockState.Ended;
            if (!planning) _tick = null;
            if (_tick < _view.Model.Decision.Tick) _tick = _view.Model.Decision.Tick;
            _tools.gameObject.SetActive(!blocked); _world.SetActive(!blocked && planning); _labels.gameObject.SetActive(!blocked && planning);
            _diagnostics.gameObject.SetActive(_diagnosticsVisible && !blocked);
            _caption.text = $"{(Following ? "当前" : "游标")} {CursorTick / (double)_view.Model.Decision.Definition.TicksPerSecond:0.00}s · 拖动▼/竖线 · ←/→ · Home 归位";
            string key = _view.Model.Decision.Tick + ":" + CursorTick + ":" + string.Join(";", _view.SpaceOperations())
                + ":" + string.Join(";", _view.ReactionView?.PreviewPlacements ?? Array.Empty<ReactionPlacement>())
                + ":" + _camera.pixelRect + ":" + planning + ":" + _view.TrackSelection.Primary + ":" + string.Join(";", _view.SpaceOperations(true));
            if (key != _key && planning && !blocked)
            {
                _key = key;
                Preview = _view.Model.HudPreviews.PreviewSpace(CursorTick, _view.SpaceOperations(), _view.ReactionView?.PreviewPlacements);
                Draw();
            }
            if (planning && Preview?.RejectionCode != null) _caption.text += " · 草稿不可用，请重验";
            _diagnosticText.text = $"诊断（F3 收起）\nTick {_view.Model.Decision.Tick}\n游标 {CursorTick}\n修订 {_view.Model.Decision.ScheduleRevision}\n视界 {_view.Model.HudPreviews.KnownHorizonTick}\n主选 {_view.TrackSelection.Primary}\n草稿 {_view.Model.Drafts.State}\n预览 {Preview?.RejectionCode ?? "OK"}\n命令 {_view.Model.LastRejection ?? "OK"}";
            ApplyAccessibility();
        }
        private void Draw()
        {
            foreach (var label in _worldLabels) if (label != null) Destroy(label); _worldLabels.Clear();
            _ghostMesh.Clear(); _rangeMesh.Clear(); _pathMesh.Clear(); _configuredMesh.Clear();
            if (Preview?.RejectionCode != null) return;
            var ghostVertices = new List<Vector3>(); var ghostIndices = new List<int>();
            var configuredVertices = new List<Vector3>(); var configuredIndices = new List<int>();
            var configured = _view.ConfiguredAttack;
            if (configured != null)
            {
                foreach (var tri in configured.Area) Triangle(tri, configuredVertices, configuredIndices);
                WorldLabel(configured.Origin, "已选方向 · 添加后进入草稿", -44);
            }
            var lines = new List<Vector3>(); var path = new List<Vector3>();
            foreach (var unit in Preview.Units)
            {
                bool own = _view.Model.Decision.ControlledUnitIds.Any(u => u.Value == unit.UnitId);
                if (own && !Following) foreach (var tri in unit.Footprint) Triangle(tri, ghostVertices, ghostIndices);
                if (own && !Following)
                {
                    var from = Vertex(unit.Position);
                    var to = Vertex(new GridPoint(unit.Position.X + GridNeighborTable.OffsetX(unit.Facing), unit.Position.Y + GridNeighborTable.OffsetY(unit.Facing)));
                    var direction = (to - from).normalized; var side = Vector3.Cross(Vector3.up, direction) * .12f;
                    path.Add(from); path.Add(to); path.Add(to); path.Add(to - direction * .3f + side); path.Add(to); path.Add(to - direction * .3f - side);
                }
                if (own) for (int i = 1; i < unit.Path.Count; i++) { path.Add(Vertex(unit.Path[i - 1])); path.Add(Vertex(unit.Path[i])); }
                if (!Following) WorldLabel(unit.Position, _view.UnitLabel(unit.UnitId) + (unit.IsUnknown ? " · ？未知" : " · 计划位置"));
            }
            foreach (var attack in Preview.Attacks)
            {
                foreach (var tri in attack.Area)
                {
                    var a = Vertex(new GridPoint(tri.X - 1, tri.Y)); var b = Vertex(new GridPoint(tri.X + 1, tri.Y)); var c = Vertex(new GridPoint(tri.X, tri.Y + tri.T));
                    lines.Add(a); lines.Add(b); lines.Add(b); lines.Add(c); lines.Add(c); lines.Add(a);
                }
                WorldLabel(attack.Origin, (attack.IsDraft ? "草稿·" : "公开·") + "攻击范围", -22);
            }
            foreach (var contact in _view.TimelineView.Contacts.Where(c => c.Tick == CursorTick))
            {
                var target = Preview.Units.FirstOrDefault(u => u.UnitId == contact.TargetUnitId);
                if (target != null) WorldLabel(target.Position, (SelectedContact == contact ? "▶ " : "") + "◆ " + contact.Outcome, 44, () => SelectContact(contact));
            }
            _ghostMesh.SetVertices(ghostVertices); _ghostMesh.SetTriangles(ghostIndices, 0); _ghostMesh.RecalculateBounds();
            _configuredMesh.SetVertices(configuredVertices); _configuredMesh.SetTriangles(configuredIndices, 0); _configuredMesh.RecalculateBounds();
            SetLines(_rangeMesh, lines); SetLines(_pathMesh, path);
        }
        private void WorldLabel(GridPoint point, string value, float offset = 18, UnityEngine.Events.UnityAction select = null)
        {
            var screen = _camera.WorldToScreenPoint(_grid.GridToWorld(point));
            if (!_camera.pixelRect.Contains(screen)) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvas, screen, null, out var local);
            var position = local + new Vector2(0, offset);
            var occupied = new List<Rect>();
            foreach (var label in _worldLabels.Where(l => l != null))
            {
                var previous = (RectTransform)label.transform;
                occupied.Add(new Rect(previous.anchoredPosition - previous.sizeDelta * .5f, previous.sizeDelta));
            }
            foreach (var marker in _canvas.GetComponentsInChildren<RectTransform>().Where(r => r.name.StartsWith("ThreatMarker-", StringComparison.Ordinal)))
            {
                var corners = new Vector3[4]; marker.GetWorldCorners(corners);
                var bottom = (Vector2)_canvas.InverseTransformPoint(corners[0]); var top = (Vector2)_canvas.InverseTransformPoint(corners[2]);
                occupied.Add(new Rect(bottom, top - bottom));
            }
            for (int row = 0; row < 10 && occupied.Any(r => r.Overlaps(new Rect(position - new Vector2(110, 12.5f), new Vector2(220, 25)))); row++)
                position.y -= 28;
            var rect = Region(_labels, "SpatialLabel", .5f, .5f, .5f, .5f); rect.sizeDelta = new Vector2(220, 25); rect.anchoredPosition = position;
            rect.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(.01f, .025f, .04f, .9f);
            rect.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
            if (select != null) { rect.GetComponent<UnityEngine.UI.Image>().raycastTarget = true; rect.gameObject.AddComponent<UnityEngine.UI.Button>().onClick.AddListener(select); }
            Label(rect, value, _caption.font, 14); _worldLabels.Add(rect.gameObject);
        }
        private void ApplyAccessibility()
        {
            foreach (var dead in _baseSizes.Keys.Where(t => t == null).ToArray()) _baseSizes.Remove(dead);
            foreach (var text in _canvas.GetComponentsInChildren<TMP_Text>(true))
            {
                if (!_baseSizes.TryGetValue(text, out float baseline)) _baseSizes[text] = baseline = text.fontSize;
                text.fontSize = baseline * _textScale;
            }
            foreach (var dead in _baseColors.Keys.Where(i => i == null).ToArray()) _baseColors.Remove(dead);
            foreach (var image in _canvas.GetComponentsInChildren<UnityEngine.UI.Image>(true))
            {
                if (!_baseColors.TryGetValue(image, out var color)) _baseColors[image] = color = image.color;
                // Only dark panel surfaces; action colors, disabled affordances and transparent hit areas retain their meaning.
                if (color.a > .5f && color.maxColorComponent < .4f && image.GetComponent<UnityEngine.UI.Selectable>() == null)
                    image.color = _highContrast ? new Color(.005f, .01f, .02f, 1) : color;
            }
        }
        private Vector3 Vertex(GridPoint point) => _world.transform.InverseTransformPoint(_grid.GridToWorld(point) + _grid.transform.up * .18f);
        private void Triangle(TrianglePoint t, List<Vector3> vertices, List<int> indices)
        {
            int n = vertices.Count; vertices.Add(Vertex(new GridPoint(t.X - 1, t.Y))); vertices.Add(Vertex(new GridPoint(t.X + 1, t.Y))); vertices.Add(Vertex(new GridPoint(t.X, t.Y + t.T)));
            indices.Add(n); indices.Add(n + (t.T > 0 ? 2 : 1)); indices.Add(n + (t.T > 0 ? 1 : 2));
        }
        private static void SetLines(Mesh mesh, List<Vector3> vertices)
        { mesh.SetVertices(vertices); mesh.SetIndices(Enumerable.Range(0, vertices.Count).ToArray(), MeshTopology.Lines, 0); mesh.RecalculateBounds(); }
        private Mesh MakeMesh(string name, Color color, out Material material)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer)); go.transform.SetParent(_world.transform, false);
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 }; go.GetComponent<MeshFilter>().sharedMesh = mesh;
            material = new Material(Shader.Find("Sprites/Default")) { color = color }; go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return mesh;
        }
        public void Release()
        { _tick = null; _key = null; Preview = null; if (_world != null) _world.SetActive(false); if (_tools != null) _tools.gameObject.SetActive(false); if (_labels != null) _labels.gameObject.SetActive(false); if (_diagnostics != null) _diagnostics.gameObject.SetActive(false); }
        private void OnDestroy()
        {
            if (_world != null) Destroy(_world); if (_tools != null) Destroy(_tools.gameObject); if (_labels != null) Destroy(_labels.gameObject); if (_diagnostics != null) Destroy(_diagnostics.gameObject);
            foreach (var mesh in new[] {_ghostMesh, _rangeMesh, _pathMesh, _configuredMesh}) if (mesh != null) Destroy(mesh);
            foreach (var material in new[] {_ghostMaterial, _rangeMaterial, _pathMaterial, _configuredMaterial}) if (material != null) Destroy(material);
        }
        private static RectTransform Region(Transform parent, string name, float x0, float y0, float x1, float y1)
        { var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false); var rect = (RectTransform)go.transform; rect.anchorMin = new Vector2(x0, y0); rect.anchorMax = new Vector2(x1, y1); rect.offsetMin = rect.offsetMax = Vector2.zero; return rect; }
        private static TMP_Text Label(RectTransform parent, string value, TMP_FontAsset font, float size)
        { var rect = Region(parent, "Text", 0, 0, 1, 1); var text = rect.gameObject.AddComponent<TextMeshProUGUI>(); text.font = font; text.fontSize = size; text.text = value; text.raycastTarget = false; text.alignment = TextAlignmentOptions.MidlineLeft; return text; }
        private static UnityEngine.UI.Button MakeButton(RectTransform rect, string title, UnityEngine.Events.UnityAction action, TMP_FontAsset font)
        { rect.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(.12f, .2f, .29f); var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>(); button.onClick.AddListener(action); Label(rect, title, font, 14); return button; }
    }
}
