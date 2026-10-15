using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProjectHero.UnityView
{
    /// <summary>M2 controller-filtered tracks. All rows share one coordinate mapping and one Logic preview.</summary>
    public sealed class M2TimelineView : MonoBehaviour
    {
        private sealed class Track
        {
            public RectTransform Root, Chart, Draft, Allowed, Unknown, Divider;
            public TMP_Text Title;
            public UnityEngine.UI.LayoutElement Size;
        }
        private BattlePresentationView _view;
        private TMP_FontAsset _font;
        private RectTransform _root, _header, _content, _viewport, _overlay, _roster, _snapLine, _cursor, _horizon;
        private UnityEngine.UI.ScrollRect _scroll;
        private TMP_Text _summary, _overflow, _snapText;
        private readonly Dictionary<long, Track> _tracks = new Dictionary<long, Track>();
        private readonly Dictionary<string, TimelinePlanView> _blocks = new Dictionary<string, TimelinePlanView>();
        private readonly List<GameObject> _headerMarks = new List<GameObject>(), _links = new List<GameObject>(), _rosterButtons = new List<GameObject>();
        private readonly Dictionary<long, ActionPlanSnapshot> _lastDraftPlans = new Dictionary<long, ActionPlanSnapshot>();
        private IReadOnlyList<ActionPlanSnapshot> _plans = Array.Empty<ActionPlanSnapshot>();
        private IReadOnlyList<TimelineContactPreview> _contacts = Array.Empty<TimelineContactPreview>();
        private string _renderKey, _rosterKey;
        private string _liveGate;
        private double _lastLiveRefresh;
        private long _startTick, _lastPrimary, _snapTick;
        private double _spanSeconds = 6;
        private bool _follow = true;
        private const float TitleWidth = 184;
        public bool SnapVisible => _snapLine != null && _snapLine.gameObject.activeSelf;
        public long SnapTick => _snapTick;
        public IReadOnlyList<ActionPlanSnapshot> DisplayPlans => _plans;
        public float PixelsPerSecond => Mathf.Max(1, (_viewport.rect.width - TitleWidth) / (float)_spanSeconds);
        public double StartTick => _startTick;
        public double SpanTicks => _spanSeconds * _view.Model.Decision.Definition.TicksPerSecond;
        public RectTransform Content => _content;
        public IReadOnlyList<TimelineContactPreview> Contacts => _contacts;

        public void Bind(BattlePresentationView view, RectTransform timelineContent, TMP_FontAsset font, TMP_Text status)
        {
            _view = view; _font = font;
            if (_root != null) { ResetView(); return; }
            var panel = timelineContent.parent.parent as RectTransform;
            // Retire only the old list region; reuse its authored panel and M1's surrounding regions.
            timelineContent.parent.gameObject.SetActive(false);
            _root = Rect(panel, "M2Timeline"); Stretch(_root);
            _header = Rect(_root, "FixedHeader"); Place(_header, 0, 1, 1, 1); _header.pivot = new Vector2(.5f, 1); _header.sizeDelta = new Vector2(0, 62);
            var toolbar = Rect(_header, "Tools"); Top(toolbar, 0, 0, 0, 24);
            Button(Pixels(toolbar, "Back", 0, 0, 38, 24), "←", () => Pan(-.5));
            Button(Pixels(toolbar, "Forward", 40, 0, 38, 24), "→", () => Pan(.5));
            Button(Pixels(toolbar, "ZoomIn", 80, 0, 30, 24), "+", () => Zoom(.75));
            Button(Pixels(toolbar, "ZoomOut", 112, 0, 30, 24), "−", () => Zoom(1.333333));
            Button(Pixels(toolbar, "Now", 145, 0, 104, 24), "回到当前", ReturnToNow);
            _overflow = Button(Pixels(toolbar, "Overflow", 252, 0, 180, 24), "", OpenOverflow).GetComponentInChildren<TMP_Text>();
            var scrollRoot = Rect(_root, "TrackScroll"); Stretch(scrollRoot); scrollRoot.offsetMax = new Vector2(0, -62);
            _scroll = scrollRoot.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();
            _viewport = Rect(scrollRoot, "Viewport"); Stretch(_viewport);
            Image(_viewport, new Color(.025f, .04f, .065f), true); _viewport.gameObject.AddComponent<UnityEngine.UI.Mask>().showMaskGraphic = false;
            _content = Rect(_viewport, "Content"); Place(_content, 0, 1, 1, 1); _content.pivot = new Vector2(.5f, 1);
            var layout = _content.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
            layout.childControlHeight = layout.childControlWidth = true; layout.childForceExpandHeight = false; layout.childForceExpandWidth = true; layout.spacing = 2;
            _content.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>().verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            _overlay = Rect(_viewport, "ContactOverlay"); Stretch(_overlay);
            _scroll.content = _content; _scroll.viewport = _viewport; _scroll.horizontal = false;
            _scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped; _scroll.scrollSensitivity = 32;
            _scroll.onValueChanged.AddListener(_ => RefreshLines());
            _summary = Text(Rect(_viewport, "EmptySelection"), "", 16); Stretch((RectTransform)_summary.transform);
            _cursor = Image(Rect(_overlay, "CurrentTime"), new Color(.3f, 1, .85f)).rectTransform;
            _horizon = Image(Rect(_overlay, "KnownHorizon"), new Color(.65f, .72f, .85f)).rectTransform;
            _snapLine = Image(Rect(_overlay, "SnapIndicator"), new Color(1, .8f, .2f)).rectTransform;
            _snapText = Text(Rect(_snapLine, "SnapLabel"), "吸附", 14); Pixels((RectTransform)_snapText.transform, 4, 0, 210, 22);
            ClearSnap();
            status.gameObject.SetActive(false);
            var rosterRoot = Rect(status.transform.parent, "M2Units"); Stretch(rosterRoot);
            var rosterScroll = rosterRoot.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();
            var rosterViewport = Rect(rosterRoot, "Viewport"); Image(rosterViewport, Color.white, true); rosterViewport.gameObject.AddComponent<UnityEngine.UI.Mask>().showMaskGraphic = false;
            _roster = Rect(rosterViewport, "Content"); Place(_roster, 0, 1, 1, 1); _roster.pivot = new Vector2(.5f, 1);
            var rosterLayout = _roster.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
            rosterLayout.childControlWidth = rosterLayout.childControlHeight = true; rosterLayout.childForceExpandWidth = true; rosterLayout.childForceExpandHeight = false; rosterLayout.spacing = 2;
            _roster.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>().verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            rosterScroll.content = _roster; rosterScroll.viewport = rosterViewport; rosterScroll.horizontal = false; rosterScroll.scrollSensitivity = 32;
            rosterScroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
            ResetView();
        }
        public void ResetView()
        { _renderKey = _rosterKey = _liveGate = null; _lastPrimary = 0; _follow = true; _spanSeconds = 6; _lastDraftPlans.Clear(); ClearSnap(); }
        public void ReturnToNow() { _follow = true; _renderKey = null; Refresh(); }
        public void Pan(double fraction) { _follow = false; _startTick = Math.Max(0, _startTick + (long)(SpanTicks * fraction)); _renderKey = null; ClearSnap(); Refresh(); }
        public void Zoom(double factor) { _spanSeconds = Math.Max(1, Math.Min(30, _spanSeconds * factor)); _renderKey = null; ClearSnap(); Refresh(); }
        private float X(long tick) => (float)((tick - _startTick) / SpanTicks) * Mathf.Max(1, _viewport.rect.width - TitleWidth);
        public void Refresh()
        {
            if (_root == null || _view.Model.Decision == null) return;
            var model = _view.Model; var selection = _view.TrackSelection; var decision = model.Decision;
            string operationsKey = string.Join(";", model.Drafts.Operations);
            string liveGate = decision.ScheduleRevision + ":" + selection.Revision + ":" + _view.Clock.State + ":" + model.Drafts.State + ":" + operationsKey
                + ":" + _follow + ":" + (_follow ? 0 : _startTick) + ":" + _spanSeconds + ":" + _viewport.rect.width + ":" + _viewport.rect.height + ":" + model.Window?.WindowId;
            // Driver callbacks can consume many committed Ticks in one Unity frame. Render/forecast at HUD cadence,
            // while every edit, selection, revision and pause transition refreshes immediately.
            double now = Time.realtimeSinceStartupAsDouble;
            if (_view.Clock.State == BattleClockState.Running && liveGate == _liveGate && now - _lastLiveRefresh < .1) return;
            _liveGate = liveGate; _lastLiveRefresh = now;
            if (_follow) _startTick = Math.Max(0, decision.Tick);
            string key = decision.Tick + ":" + decision.ScheduleRevision + ":" + selection.Revision + ":" + _startTick + ":" + _spanSeconds
                + ":" + _viewport.rect.width + ":" + _viewport.rect.height + ":" + model.Drafts.State + ":" + _view.Clock.State
                + ":" + operationsKey + ":" + model.Preview?.RejectionCode;
            RefreshRoster();
            if (key == _renderKey) return; _renderKey = key;
            var projected = new Dictionary<long, ActionPlanSnapshot>();
            foreach (var plan in model.Timeline) projected[plan.ActionPlanId] = plan;
            if (model.Preview?.Succeeded == true && model.Drafts.State != ScheduleDraftState.Stale)
                foreach (var preview in model.Preview.PreviewPlans ?? Array.Empty<ProjectHero.Logic.Timeline.SchedulePlanPreview>()) projected[preview.Plan.ActionPlanId] = preview.Plan;
            foreach (var entry in model.Drafts.Entries)
                if (entry.Operation is RemoveEditablePlanOperation) projected.Remove(entry.Operation.PlanId.Value);
                else if (entry.Operation is MoveEditablePlanOperation move && model.Preview?.Succeeded == false && projected.TryGetValue(move.PlanId.Value, out var existing))
                {
                    long delta = move.RequestedStartTick - existing.StartTick;
                    projected[move.PlanId.Value] = existing with {StartTick = move.RequestedStartTick, EndTick = existing.EndTick + delta, ImpactTick = existing.ImpactTick + delta};
                }
            foreach (long id in _lastDraftPlans.Keys.ToArray()) if (!model.Drafts.Entries.Any(e => e.Id == id)) _lastDraftPlans.Remove(id);
            foreach (var entry in model.Drafts.Entries)
                if (entry.Operation is AddOrdinaryPlanOperation add)
                {
                    var draftPlan = model.PreviewPlanFor(entry.Id);
                    if (model.Preview?.Succeeded == true && draftPlan != null) _lastDraftPlans[entry.Id] = draftPlan;
                    else if (_lastDraftPlans.TryGetValue(entry.Id, out var previous))
                    {
                        // Draw the requested invalid placement using the last resolved duration, never predict a new schedule in View.
                        long start = add.RequestedStartTick ?? previous.StartTick, delta = start - previous.StartTick;
                        projected[previous.ActionPlanId] = previous with {StartTick = start, EndTick = previous.EndTick + delta, ImpactTick = previous.ImpactTick + delta};
                    }
                }
            _plans = projected.Values.ToArray();
            _contacts = (model.HudPreviews?.PreviewTimelineContacts(model.Drafts.State == ScheduleDraftState.Stale || model.Preview?.Succeeded == false
                ? Array.Empty<ScheduleEditOperation>() : model.Drafts.Operations)?.Contacts)?.Select(c => c.TemporaryPlanKey > 0
                    && model.PreviewPlanFor(c.TemporaryPlanKey) is ActionPlanSnapshot draftPlan ? c with {PlanId = draftPlan.ActionPlanId} : c).ToArray()
                ?? Array.Empty<TimelineContactPreview>();
            BuildHeader();
            var ordered = selection.Ordered(decision); var live = new HashSet<long>(); var liveBlocks = new HashSet<string>();
            int index = 0, previousGroup = -1;
            foreach (var unit in ordered)
            {
                live.Add(unit.UnitId);
                if (!_tracks.TryGetValue(unit.UnitId, out var track))
                {
                    track = CreateTrack(unit.UnitId); _tracks.Add(unit.UnitId, track);
                }
                track.Root.gameObject.SetActive(true); track.Root.SetSiblingIndex(index++);
                bool primary = unit.UnitId == selection.Primary;
                int group = TrackSelectionModel.Group(decision, unit.UnitId);
                track.Divider.gameObject.SetActive(previousGroup >= 0 && group != previousGroup);
                track.Size.preferredHeight = primary ? 88 : 32; // 64px main track + its 24px draft strip.
                track.Root.GetComponent<UnityEngine.UI.Image>().color = group != previousGroup ? new Color(.09f, .16f, .22f) : new Color(.045f, .075f, .11f); previousGroup = group;
                track.Title.text = (selection.IsTemporary(unit.UnitId) ? "┄ 临时 " : "") + (primary ? "▌" : "") + _view.UnitLabel(unit.UnitId)
                    + (primary ? $"\nHP {unit.HealthQ10 / 1024d:0.#} · 肾上 {unit.AvailableAdrenaline}" : $" · 肾上 {unit.AvailableAdrenaline}");
                track.Title.fontSize = primary ? 16 : 13;
                track.Allowed.gameObject.SetActive(model.CanPlan && group == 0 && (model.OwnsWindow || model.Facts.Permission == HudSubmissionPermission.Intervened));
                if (model.Window != null) Interval(track.Allowed, model.LockLine, model.Window.EndTick, 0, primary ? 64 : 32);
                long? horizon = model.HudPreviews?.KnownHorizonTick;
                track.Unknown.gameObject.SetActive(group != 0 && horizon.HasValue);
                if (horizon.HasValue) Interval(track.Unknown, horizon.Value, _startTick + (long)SpanTicks, 0, primary ? 64 : 32);
                track.Draft.gameObject.SetActive(primary);
                foreach (var plan in _plans.Where(p => p.OwnerUnitId == unit.UnitId))
                {
                    long draft = 0;
                    foreach (var entry in model.Drafts.Entries)
                        if (entry.Operation is AddOrdinaryPlanOperation && (model.PreviewPlanFor(entry.Id)?.ActionPlanId == plan.ActionPlanId
                            || _lastDraftPlans.TryGetValue(entry.Id, out var last) && last.ActionPlanId == plan.ActionPlanId)) draft = entry.Id;
                    string blockKey = draft == 0 ? "plan:" + plan.ActionPlanId : "draft:" + draft;
                    liveBlocks.Add(blockKey);
                    if (!_blocks.TryGetValue(blockKey, out var block))
                    {
                        var rect = Rect(draft == 0 ? track.Chart : track.Draft, "Action-" + blockKey);
                        Image(rect, new Color(.2f, .35f, .5f), true); Text(Rect(rect, "Label"), "", 14);
                        block = rect.gameObject.AddComponent<TimelinePlanView>(); block.Initialize(_view, plan.ActionPlanId, draft); _blocks.Add(blockKey, block);
                    }
                    block.transform.SetParent(draft == 0 || !primary ? track.Chart : track.Draft, false);
                    block.gameObject.SetActive(true);
                    block.Refresh(plan, draft != 0 || model.CanEdit(plan.ActionPlanId), model.IsConditionallyInvalidated(plan.ActionPlanId));
                    float y = draft != 0 && primary ? 0 : primary ? 18 : 4;
                    float height = draft != 0 && primary ? 22 : primary ? 42 : 24;
                    Interval((RectTransform)block.transform, plan.StartTick, plan.EndTick, y, height);
                    var type = decision.FindAction(new ActionSpecId(plan.ActionSpecId))?.Type;
                    bool terminal = plan.State == (int)ActionPlanState.Completed || plan.State == (int)ActionPlanState.Terminated;
                    block.GetComponent<UnityEngine.UI.Image>().color = terminal ? new Color(.14f, .17f, .2f) : draft != 0 ? new Color(.19f, .28f, .4f)
                        : type == ActionType.Attack ? new Color(.55f, .2f, .23f) : type == ActionType.Move ? new Color(.13f, .32f, .5f) : new Color(.36f, .3f, .14f);
                    if (model.Preview?.Succeeded == false && (draft != 0 || model.Drafts.Operations.Any(o => o.PlanId.Value == plan.ActionPlanId)))
                        block.GetComponent<UnityEngine.UI.Image>().color = new Color(.7f, .13f, .16f);
                    block.SetTrackLabel(primary, draft != 0, _contacts.Where(c => c.PlanId == plan.ActionPlanId).Select(c => c.Outcome).Distinct());
                    block.SetImpact(type == ActionType.Attack, (float)((plan.ImpactTick - plan.StartTick) / (double)Math.Max(1, plan.EndTick - plan.StartTick)), primary,
                        horizon.HasValue && plan.ImpactTick >= horizon.Value);
                }
            }
            foreach (var pair in _tracks) if (!live.Contains(pair.Key)) pair.Value.Root.gameObject.SetActive(false);
            foreach (var pair in _blocks.ToArray())
                if (!liveBlocks.Contains(pair.Key)) { pair.Value.gameObject.SetActive(false); Destroy(pair.Value.gameObject); _blocks.Remove(pair.Key); }
            _summary.gameObject.SetActive(ordered.Count == 0);
            _summary.text = "未选择单位 · 指向我方的已公开威胁：" + model.Reactions.Count(r => r.Options?.Any(o => o.IsOpen && o.IsPublished) == true);
            Canvas.ForceUpdateCanvases();
            if (_lastPrimary != selection.Primary) { _lastPrimary = selection.Primary; ScrollPrimaryIntoView(); }
            RefreshLines();
        }
        private Track CreateTrack(long id)
        {
            var root = Rect(_content, "UnitTrack-" + id); root.pivot = new Vector2(0, 1); Image(root, Color.black);
            var track = new Track { Root = root, Size = root.gameObject.AddComponent<UnityEngine.UI.LayoutElement>() };
            track.Divider = Rect(root, "FactionDivider"); Top(track.Divider, 0, 0, 0, 2); Image(track.Divider, new Color(.48f, .55f, .64f));
            var title = Pixels(root, "Title", 0, 0, TitleWidth - 22, 64); title.anchorMin = new Vector2(0, 0); title.anchorMax = new Vector2(0, 1); title.offsetMin = Vector2.zero; title.offsetMax = new Vector2(TitleWidth - 22, 0);
            track.Title = Button(title, "", () => _view.FocusTrack(id)).GetComponentInChildren<TMP_Text>();
            Button(Pixels(root, "Close", TitleWidth - 22, 0, 22, 28), "×", () => _view.RemoveTrack(id));
            track.Chart = Rect(root, "Chart"); Stretch(track.Chart); track.Chart.offsetMin = new Vector2(TitleWidth, 0);
            track.Chart.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
            track.Allowed = Rect(track.Chart, "StartAllowed"); Image(track.Allowed, new Color(.1f, .45f, .4f, .18f));
            track.Unknown = Rect(track.Chart, "Unknown"); Image(track.Unknown, new Color(.04f, .05f, .08f, .7f)); Text(Rect(track.Unknown, "Question"), "？未知", 14);
            track.Draft = Rect(root, "DraftStrip"); Pixels(track.Draft, TitleWidth, 64, 0, 24); track.Draft.anchorMax = new Vector2(1, 1); track.Draft.offsetMax = new Vector2(0, -64); track.Draft.offsetMin = new Vector2(TitleWidth, -88);
            track.Draft.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
            return track;
        }
        private void BuildHeader()
        {
            Clear(_headerMarks);
            var model = _view.Model; int tps = model.Decision.Definition.TicksPerSecond;
            foreach (var window in model.HudPreviews?.WindowSequence ?? Array.Empty<ProjectedWindow>())
            {
                float left = Mathf.Max(0, X(window.OpenedAtTick)), right = Mathf.Min(_viewport.rect.width - TitleWidth, X(window.EndTick));
                if (right <= left) continue;
                var mark = Pixels(_header, "Window", TitleWidth + left, 24, right - left, 19); Image(mark, window.IsEstimated ? new Color(.15f, .2f, .27f) : new Color(.12f, .36f, .42f));
                Text(Rect(mark, "Label"), _view.UnitLabel(window.OwnerUnitId) + (window.IsEstimated ? " · 预计" : " · 窗口"), 12); _headerMarks.Add(mark.gameObject);
            }
            double interval = _spanSeconds > 12 ? 2 : _spanSeconds > 5 ? 1 : .5;
            for (double seconds = Math.Ceiling(_startTick / (double)tps / interval) * interval; seconds <= (_startTick + SpanTicks) / tps; seconds += interval)
            {
                var mark = Pixels(_header, "Tick", TitleWidth + X((long)Math.Round(seconds * tps)), 43, 68, 19);
                Text(mark, seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s", 12); _headerMarks.Add(mark.gameObject);
            }
        }
        private void RefreshRoster()
        {
            var ordered = _view.TrackSelection.Ordered(_view.Model.Decision, false);
            string key = string.Join(";", ordered.Select(u => u.UnitId + ":" + u.HealthQ10 + ":" + u.AvailableAdrenaline)) + ":" + _view.TrackSelection.Revision;
            if (key == _rosterKey) return; _rosterKey = key; Clear(_rosterButtons);
            TextEntry("单位 · 单击选择 / Ctrl 多选", 0, false, 0);
            int row = 1;
            foreach (var unit in ordered) TextEntry((_view.TrackSelection.Contains(unit.UnitId) ? "[已选] " : "") + _view.UnitLabel(unit.UnitId)
                + $"\nHP {unit.HealthQ10 / 1024d:0.#} · 肾上 {unit.AvailableAdrenaline}", row++, true, unit.UnitId);
        }
        private void TextEntry(string label, int index, bool button, long id)
        {
            var rect = Rect(_roster, "Unit-" + id);
            rect.gameObject.AddComponent<UnityEngine.UI.LayoutElement>().preferredHeight = index == 0 ? 28 : 44;
            if (button) Button(rect, label, () => _view.SelectTrack(id, ControlPressed())); else Text(rect, label, 14);
            _rosterButtons.Add(rect.gameObject);
        }
        public static bool ControlPressed() => UnityEngine.InputSystem.Keyboard.current?.ctrlKey.isPressed == true;
        private void ScrollPrimaryIntoView()
        {
            if (!_tracks.TryGetValue(_view.TrackSelection.Primary, out var track)) return;
            float top = -track.Root.anchoredPosition.y, bottom = top + track.Root.rect.height, offset = _content.anchoredPosition.y;
            float desired = top < offset ? top : bottom > offset + _viewport.rect.height ? bottom - _viewport.rect.height : offset;
            _content.anchoredPosition = new Vector2(0, Mathf.Clamp(desired, 0, Mathf.Max(0, _content.rect.height - _viewport.rect.height)));
        }
        private RectTransform _overflowMenu;
        private void OpenOverflow()
        {
            if (_overflowMenu != null) { Destroy(_overflowMenu.gameObject); _overflowMenu = null; return; }
            var hidden = _view.TrackSelection.Ordered(_view.Model.Decision).Where(u =>
            {
                var row = _tracks[u.UnitId].Root; float top = row.anchoredPosition.y + _content.anchoredPosition.y;
                return top >= row.rect.height || top < -_viewport.rect.height;
            }).ToArray();
            if (hidden.Length == 0) return;
            _overflowMenu = Pixels(_root, "HiddenSelectedTracks", 252, 24, 200, hidden.Length * 26);
            Image(_overflowMenu, new Color(.05f, .09f, .13f), true);
            for (int i = 0; i < hidden.Length; i++)
            {
                long id = hidden[i].UnitId;
                Button(Pixels(_overflowMenu, "Hidden-" + id, 0, i * 26, 200, 25), _view.UnitLabel(id), () =>
                { Destroy(_overflowMenu.gameObject); _overflowMenu = null; _view.FocusTrack(id); });
            }
            _overflowMenu.SetAsLastSibling();
        }
        private void RefreshLines()
        {
            if (_view?.Model.Decision == null || _viewport == null) return;
            Clear(_links);
            var selected = _view.TrackSelection.Ordered(_view.Model.Decision); int hidden = 0;
            foreach (var unit in selected)
            {
                var row = _tracks[unit.UnitId].Root; float y = row.anchoredPosition.y + _content.anchoredPosition.y;
                if (y >= row.rect.height || y < -_viewport.rect.height) hidden++;
            }
            _overflow.text = hidden == 0 ? "" : "+" + hidden + " ▾ 已选轨道";
            foreach (var contact in _contacts)
            {
                if (!_tracks.TryGetValue(contact.OwnerUnitId, out var source) || !source.Root.gameObject.activeSelf) continue;
                float x = TitleWidth + X(contact.Tick);
                if (x < TitleWidth || x > _viewport.rect.width) continue;
                float sy = source.Root.anchoredPosition.y + _content.anchoredPosition.y - 16;
                if (_tracks.TryGetValue(contact.TargetUnitId, out var target) && target.Root.gameObject.activeSelf)
                {
                    float ty = target.Root.anchoredPosition.y + _content.anchoredPosition.y - 16;
                    var line = Pixels(_overlay, "ContactLink", x, -Mathf.Max(sy, ty), 2, Mathf.Abs(sy - ty) + 2); Image(line, new Color(.95f, .63f, .3f, .75f)); _links.Add(line.gameObject);
                    var label = Pixels(_overlay, "ContactResult", x + 3, -sy, 100, 20); Text(label, "◆ " + contact.Outcome, 12); _links.Add(label.gameObject);
                }
                else
                {
                    var tag = Pixels(_overlay, "ContactTarget", x, -sy, 140, 22);
                    long id = contact.TargetUnitId; Button(tag, "◆ " + _view.UnitLabel(id) + " +", () => _view.AddContactTrack(id)); _links.Add(tag.gameObject);
                }
            }
            Line(_cursor, _view.Model.Decision.Tick, new Color(.3f, 1, .85f));
            long? horizon = _view.Model.HudPreviews?.KnownHorizonTick;
            _horizon.gameObject.SetActive(horizon.HasValue); if (horizon.HasValue) Line(_horizon, horizon.Value, Color.gray);
            if (horizon.HasValue && _horizon.gameObject.activeSelf)
            {
                var label = Pixels(_overlay, "HorizonLabel", TitleWidth + X(horizon.Value) + 4, 0, 120, 20);
                Text(label, "视界 " + (horizon.Value / (double)_view.Model.Decision.Definition.TicksPerSecond).ToString("0.00") + "s", 12); _links.Add(label.gameObject);
                _horizon.GetComponent<UnityEngine.UI.Image>().enabled = false;
                for (float y = 0; y < _viewport.rect.height; y += 12)
                { var dash = Pixels(_overlay, "HorizonDash", TitleWidth + X(horizon.Value), y, 2, 6); Image(dash, new Color(.65f, .72f, .85f)); _links.Add(dash.gameObject); }
            }
            if (SnapVisible) { Line(_snapLine, _snapTick, Color.yellow); _snapLine.SetAsLastSibling(); }
        }
        private void Line(RectTransform line, long tick, Color color)
        {
            float x = TitleWidth + X(tick); line.gameObject.SetActive(x >= TitleWidth && x <= _viewport.rect.width);
            Place(line, 0, 0, 0, 1); line.sizeDelta = new Vector2(2, 0); line.anchoredPosition = new Vector2(x, 0);
        }
        public void ShowSnap(long tick)
        { _snapTick = tick; _snapLine.gameObject.SetActive(true); _snapText.text = "吸附 · " + (tick / (double)_view.Model.Decision.Definition.TicksPerSecond).ToString("0.000") + "s"; RefreshLines(); }
        public void ClearSnap() { if (_snapLine != null) _snapLine.gameObject.SetActive(false); }
        private void Interval(RectTransform rect, long start, long end, float y, float height)
        { Pixels(rect, X(start), y, Mathf.Max(2, X(end) - X(start)), height); }
        private void OnDisable() { ClearSnap(); }
        private static void Clear(List<GameObject> objects) { foreach (var go in objects) { go.SetActive(false); Destroy(go); } objects.Clear(); }
        private static RectTransform Rect(Transform parent, string name)
        { var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false); var rect = (RectTransform)go.transform; Stretch(rect); return rect; }
        private static void Stretch(RectTransform rect) { Place(rect, 0, 0, 1, 1); }
        private static void Place(RectTransform rect, float x0, float y0, float x1, float y1)
        { rect.anchorMin = new Vector2(x0, y0); rect.anchorMax = new Vector2(x1, y1); rect.offsetMin = rect.offsetMax = Vector2.zero; }
        private static void Top(RectTransform rect, float left, float right, float top, float height)
        { Place(rect, 0, 1, 1, 1); rect.pivot = new Vector2(.5f, 1); rect.offsetMin = new Vector2(left, -top - height); rect.offsetMax = new Vector2(-right, -top); }
        private static RectTransform Pixels(Transform parent, string name, float x, float y, float width, float height) => Pixels(Rect(parent, name), x, y, width, height);
        private static RectTransform Pixels(RectTransform rect, float x, float y, float width, float height)
        { Place(rect, 0, 1, 0, 1); rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); return rect; }
        private static UnityEngine.UI.Image Image(RectTransform rect, Color color, bool raycast = false)
        { var image = rect.gameObject.AddComponent<UnityEngine.UI.Image>(); image.color = color; image.raycastTarget = raycast; return image; }
        private TMP_Text Text(RectTransform rect, string text, int size)
        {
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>(); label.font = _font; label.fontSize = size; label.color = Color.white;
            label.text = text; label.raycastTarget = false; label.alignment = TextAlignmentOptions.MidlineLeft; label.overflowMode = TextOverflowModes.Truncate;
            if (rect.anchorMin == Vector2.zero && rect.anchorMax == Vector2.one) return label;
            return label;
        }
        private UnityEngine.UI.Button Button(RectTransform rect, string label, UnityEngine.Events.UnityAction action)
        {
            Image(rect, new Color(.1f, .19f, .27f), true); var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
            var text = Rect(rect, "Label"); Stretch(text); text.offsetMin = new Vector2(4, 0); text.offsetMax = new Vector2(-3, 0);
            Text(text, label, 14); button.onClick.AddListener(action); return button;
        }
    }
}
