using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Snapshots;
using TMPro;
using UnityEngine;

namespace ProjectHero.UnityView
{
    /// <summary>M1 regions mounted once on the authored Canvas; all battle data is controller-filtered.</summary>
    public sealed class M1HudView : MonoBehaviour
    {
        private BattlePresentationView _view;
        private RectTransform _canvas, _draftContent, _windowBar, _config, _resume, _overlay;
        private TMP_Text _top, _draftStatus, _overlayText, _axis;
        private TMP_Text _ownPauseLabel, _threatPauseLabel;
        private TMP_InputField _start;
        private UnityEngine.UI.Button _add, _refresh, _resumeSystem;
        private TMP_FontAsset _font, _dynamicFont;
        private readonly List<GameObject> _created = new List<GameObject>();
        private readonly List<GameObject> _draftRows = new List<GameObject>(), _windowSegments = new List<GameObject>();
        private RectTransform _actions, _targets, _timeline, _reactions;
        private string _draftKey, _windowKey;
        public string TopText => _top?.text;
        public TMP_FontAsset HudFont => _font;
        public string DraftStatusText => _draftStatus?.text;
        public TMP_InputField StartInput => _start;
        public bool ResumeChoiceVisible => _resume != null && _resume.gameObject.activeSelf;

        public void Bind(BattlePresentationView view, RectTransform canvas, TMP_FontAsset font,
            RectTransform actions, RectTransform targets, RectTransform timeline, RectTransform reactions, TMP_Text status, TMP_Text preview)
        {
            _view = view; _canvas = canvas; _actions = actions; _targets = targets; _timeline = timeline; _reactions = reactions;
            _targets.gameObject.SetActive(false);
            if (_top != null) { _draftKey = _windowKey = null; HideResumeChoice(); return; }
            // The Windows prototype uses the installed Chinese font; authored fonts remain a valid fallback.
            if (_dynamicFont == null && Font.GetOSInstalledFontNames().Contains("Microsoft YaHei"))
            {
                _dynamicFont = TMP_FontAsset.CreateFontAsset("Microsoft YaHei", "Regular", 48);
                if (_dynamicFont != null) _dynamicFont.name = "M1 Chinese HUD";
            }
            _font = _dynamicFont ?? font;
            foreach (var text in canvas.GetComponentsInChildren<TMP_Text>(true)) text.font = _font;
            Place(status.transform.parent as RectTransform, .02f, .48f, .27f, .89f);
            Place(actions.parent.parent as RectTransform, .72f, .65f, .98f, .89f);
            Place(preview.transform.parent as RectTransform, .72f, .31f, .98f, .51f);
            Place(reactions.parent.parent as RectTransform, .72f, .08f, .98f, .30f);
            Place(timeline.parent.parent as RectTransform, .02f, .22f, .68f, .36f);
            Place(canvas.Find("Controls") as RectTransform, .02f, .005f, .98f, .065f);
            foreach (var text in canvas.Find("Controls").GetComponentsInChildren<TMP_Text>(true))
            {
                text.font = _font;
                switch (text.text)
                {
                    case "Confirm": text.text = "应用排程"; break;
                    case "Cancel": text.text = "丢弃草稿"; break;
                    case "Concurrent": text.text = "介入"; break;
                    case "Actions": text.text = "动作面板"; break;
                    case "Replay": text.text = "回放"; break;
                }
            }
            _top = Label(Region("M1Clock", .02f, .90f, .70f, .985f), "", 23);
            var settings = Region("M1AutoPause", .72f, .90f, .98f, .985f);
            var ownPause = Button(Child(settings, "OwnWindow", 0, .5f, 1, 1), "我方窗口自动暂停：开", () => _view.Clock.AutoPauseOwnWindow = !_view.Clock.AutoPauseOwnWindow);
            var threatPause = Button(Child(settings, "Threat", 0, 0, 1, .5f), "可响应威胁自动暂停：开", () => _view.Clock.AutoPauseThreat = !_view.Clock.AutoPauseThreat);
            _ownPauseLabel = ownPause.GetComponentInChildren<TMP_Text>(); _threatPauseLabel = threatPause.GetComponentInChildren<TMP_Text>();
            var draft = Region("M1Drafts", .02f, .07f, .70f, .18f);
            _draftStatus = Label(Child(draft, "Header", 0, .76f, .76f, 1), "", 17);
            _refresh = Button(Child(draft, "Refresh", .77f, .77f, 1, 1), "重验草稿", () => { _view.Model.RevalidateDraft(); Refresh(); });
            _draftContent = Scroll(Child(draft, "List", 0, 0, 1, .75f));
            _windowBar = Region("M1Windows", .02f, .37f, .68f, .44f);
            _axis = Label(Child(_windowBar, "Axis", 0, 0, 1, .40f), "", 15);
            _config = Region("M1Configuration", .72f, .52f, .98f, .64f);
            Label(Child(_config, "Title", 0, .72f, 1, 1), "起点（绝对战斗秒；留空自动追加）", 16);
            var inputRect = Child(_config, "StartSeconds", .03f, .38f, .52f, .70f);
            inputRect.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(.1f, .16f, .23f);
            _start = inputRect.gameObject.AddComponent<TMP_InputField>(); _start.textViewport = inputRect;
            _start.textComponent = (TextMeshProUGUI)Label(inputRect, "", 18);
            _start.textComponent.raycastTarget = true;
            _start.onEndEdit.AddListener(value =>
            {
                if (string.IsNullOrWhiteSpace(value)) { _view.SetCandidateStart(null); return; }
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    && !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0 && seconds < long.MaxValue / 60d)
                    _view.SetCandidateStart((long)Math.Round(seconds * 60, MidpointRounding.AwayFromZero));
            });
            Button(Child(_config, "AtWindowEnd", .55f, .38f, .98f, .70f), "窗口末尾", () => SetStart(_view.Model.Window?.EndTick - 1));
            _add = Button(Child(_config, "Append", .03f, .03f, .98f, .34f), "添加至草稿 / 保存修改", () => _view.AddConfiguredDraft());
            _resume = Region("M1ResumeChoice", .28f, .45f, .70f, .68f);
            Label(Child(_resume, "Title", .03f, .62f, .97f, .97f), "还有未应用草稿，请选择：", 20);
            Button(Child(_resume, "ApplyAndResume", .03f, .34f, .97f, .60f), "应用并继续", () => _view.ResumePlanning(true));
            Button(Child(_resume, "KeepAndResume", .03f, .06f, .60f, .32f), "保留未提交草稿并继续", () => _view.ResumePlanning(false));
            Button(Child(_resume, "Back", .63f, .06f, .97f, .32f), "返回规划", HideResumeChoice);
            _resume.gameObject.SetActive(false);
            _overlay = Region("M1SystemOverlay", 0, 0, 1, 1);
            _overlay.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
            _overlayText = Label(Child(_overlay, "Title", .3f, .50f, .7f, .68f), "系统暂停", 30);
            _resumeSystem = Button(Child(_overlay, "Resume", .35f, .34f, .65f, .47f), "返回战斗（Esc）", _view.ResumeSystemPause);
            _overlay.gameObject.SetActive(false);
            Refresh();
        }
        public void SetStart(long? tick)
        {
            _start.SetTextWithoutNotify(tick.HasValue ? (tick.Value / 60d).ToString("0.0000", CultureInfo.InvariantCulture) : "");
            _view.SetCandidateStart(tick);
        }
        public void ResetStart() { if (_start != null) _start.SetTextWithoutNotify(""); }
        public void ShowResumeChoice() { _resume.gameObject.SetActive(true); _resume.SetAsLastSibling(); }
        public void HideResumeChoice() { if (_resume != null) _resume.gameObject.SetActive(false); }
        public void Refresh()
        {
            if (_view?.Model.Decision == null || _top == null) return;
            var model = _view.Model; var facts = model.Facts; var layout = HudLayoutResolver.Resolve(facts);
            _view.SetHudBattleViewport(layout.ExpandedTimeline);
            _ownPauseLabel.text = "我方窗口自动暂停：" + (_view.Clock.AutoPauseOwnWindow ? "开" : "关");
            _threatPauseLabel.text = "可响应威胁自动暂停：" + (_view.Clock.AutoPauseThreat ? "开" : "关");
            string clock = facts.Clock == BattleClockState.Running ? "进行中" : facts.Clock == BattleClockState.TacticalPlanning ? "战术暂停·可规划"
                : facts.Clock == BattleClockState.SystemPaused ? "系统暂停" : "战斗已结束";
            string permission = facts.Permission == HudSubmissionPermission.OwnWindow ? "我的窗口" : facts.Permission == HudSubmissionPermission.Intervened ? "已介入"
                : facts.Permission == HudSubmissionPermission.CanIntervene ? "他人窗口·可申请介入" : "当前不能下普通指令";
            double seconds = Math.Max(0, model.Decision.Tick) / 60d;
            _top.text = $"{(int)seconds / 60:00}:{seconds % 60:00.00}  {clock}  |  {permission}"
                + (model.Window == null ? "" : $"  |  单位 {model.Window.OwnerUnitId} · 剩余 {Math.Max(0, model.Window.EndTick - model.Decision.Tick - 1) / 60d:0.00}s")
                + (facts.Clock == BattleClockState.TacticalPlanning ? "\n" + _view.Clock.PauseReason : "");
            bool canConfigure = layout.Configurator || (facts.Clock == BattleClockState.Running
                && (facts.Permission == HudSubmissionPermission.OwnWindow || facts.Permission == HudSubmissionPermission.Intervened));
            _actions.parent.parent.gameObject.SetActive(canConfigure && model.ActionPanelVisible && _view.CanConfigurePrimary);
            _config.gameObject.SetActive(canConfigure && model.ActionPanelVisible && _view.CanConfigurePrimary);
            _add.interactable = model.CanPlan && _view.HasCandidate && _view.CandidatePreview?.Succeeded == true;
            _refresh.interactable = model.CanPlan && model.Drafts.HasIntent && model.Drafts.State != ScheduleDraftState.AwaitingAcceptance;
            _overlay.gameObject.SetActive(layout.Overlay);
            if (layout.Overlay)
            {
                _overlay.SetAsLastSibling(); _overlayText.text = clock; _resumeSystem.gameObject.SetActive(facts.Clock == BattleClockState.SystemPaused);
            }
            Place(_timeline.parent.parent as RectTransform, .02f, .19f, .70f, layout.ExpandedTimeline ? .46f : .36f);
            _draftStatus.text = "草稿 " + model.Drafts.Entries.Count + " 条 · " + DraftState(model.Drafts.State)
                + (model.LastCommandStatus == null ? "" : " · " + model.LastCommandStatus)
                + (model.StagedReactionCount == 0 ? "" : " · 反应待恢复 " + model.StagedReactionCount)
                + (model.LastRejection == null ? "" : "\n" + Explain(model.LastRejection));
            RefreshDrafts(); RefreshWindows();
            if (_view.TimelineView != null) { _windowBar.gameObject.SetActive(false); _view.TimelineView.Refresh(); }
        }
        private void RefreshDrafts()
        {
            var model = _view.Model;
            string key = model.Drafts.State + ":" + string.Join(";", model.Drafts.Entries.Select(e => e.Id + ":" + e.Operation))
                + ":" + model.Preview?.RejectionCode + ":" + string.Join(";", (model.Preview?.PreviewPlans ?? Array.Empty<ProjectHero.Logic.Timeline.SchedulePlanPreview>()).Select(p => p.Plan.StartTick + ":" + p.Plan.EndTick));
            if (key == _draftKey) return; _draftKey = key;
            foreach (var row in _draftRows) Destroy(row); _draftRows.Clear();
            int number = 0;
            foreach (var entry in model.Drafts.Entries)
            {
                var row = new GameObject("Draft-" + entry.Id, typeof(RectTransform), typeof(UnityEngine.UI.LayoutElement));
                row.transform.SetParent(_draftContent, false); row.GetComponent<UnityEngine.UI.LayoutElement>().preferredHeight = 40;
                _draftRows.Add(row); var rect = (RectTransform)row.transform; long id = entry.Id;
                string label = ++number + ". ";
                if (entry.Operation is AddOrdinaryPlanOperation add)
                {
                    var plan = model.PreviewPlanFor(entry.Id);
                    label += ActionName(add.ActionSpecId.Value) + " · 单位 " + add.OwnerUnitId.Value;
                    label += plan == null ? " · 待重验" : $"  {plan.StartTick / 60d:0.000}s → {plan.EndTick / 60d:0.000}s";
                }
                else label += entry.Operation.Kind + " · 计划 " + entry.Operation.PlanId.Value;
                Label(Child(rect, "Intent", 0, 0, .65f, 1), label, 16);
                var edit = Button(Child(rect, "Edit", .66f, 0, .82f, 1), "编辑", () => _view.EditConfiguredDraft(id));
                var remove = Button(Child(rect, "Remove", .83f, 0, 1, 1), "移除", () => { model.RemoveDraft(id); Refresh(); });
                edit.interactable = remove.interactable = model.CanPlan && model.Drafts.State != ScheduleDraftState.AwaitingAcceptance;
            }
        }
        private void RefreshWindows()
        {
            var model = _view.Model; var windows = model.HudPreviews?.WindowSequence ?? Array.Empty<ProjectedWindow>();
            long first = model.Window?.OpenedAtTick ?? model.Decision.Tick;
            long last = windows.Count > 1 ? windows[1].EndTick : first + 360;
            double span = Math.Max(1, last - first);
            string key = string.Join(";", windows.Select(w => w.OwnerUnitId + ":" + w.OpenedAtTick + ":" + w.EndTick + ":" + w.IsEstimated));
            if (key != _windowKey)
            {
                _windowKey = key; foreach (var segment in _windowSegments) Destroy(segment); _windowSegments.Clear();
                foreach (var window in windows)
                {
                    float min = Mathf.Clamp01((float)((window.OpenedAtTick - first) / span));
                    float max = Mathf.Clamp01((float)((window.EndTick - first) / span)); if (max <= min) continue;
                    var rect = Child(_windowBar, "Window", min, .44f, max, 1);
                    rect.gameObject.AddComponent<UnityEngine.UI.Image>().color = window.IsEstimated ? new Color(.16f, .22f, .29f) : new Color(.13f, .36f, .45f);
                    rect.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
                    Label(rect, "单位 " + window.OwnerUnitId + (window.IsEstimated ? " · 预计" : model.OwnsWindow
                        || model.Facts.Permission == HudSubmissionPermission.Intervened ? " · 起点可放区" : " · 当前窗口"), 17);
                    _windowSegments.Add(rect.gameObject);
                }
            }
            _axis.text = $"{first / 60d:0.00}s                 {(first + (long)(span / 2)) / 60d:0.00}s                  {last / 60d:0.00}s"
                + (model.HudPreviews?.KnownHorizonTick is long horizon ? $"  |  已知视界 {horizon / 60d:0.00}s" : "");
        }
        public static string DraftState(ScheduleDraftState state) => state switch
        {
            ScheduleDraftState.AwaitingResume => "待恢复提交", ScheduleDraftState.AwaitingAcceptance => "待采纳",
            ScheduleDraftState.Accepted => "权威已接受", ScheduleDraftState.Rejected => "权威已拒绝·意图保留",
            ScheduleDraftState.Stale => "已过期·意图保留", _ => "未提交"
        };
        public static string ActionName(string id)
        {
            if (id == null) return "动作";
            if (id.Contains("heavy")) return "重击";
            if (id.Contains("light") || id.Contains("quick_slash")) return "轻斩";
            if (id.Contains("move")) return "移动";
            if (id.Contains("guard")) return "防守";
            if (id.Contains("block")) return "格挡";
            if (id.Contains("dodge")) return "闪避";
            if (id.Contains("spear_thrust")) return "突刺";
            if (id.Contains("whirlwind")) return "旋风斩";
            if (id.Contains("wide_cleave")) return "横扫";
            return id;
        }
        public static string Explain(string code) => code switch
        {
            null => "", "STALE_SCHEDULE_REVISION" => "快照或窗口已变化，请重验草稿",
            "SCHEDULE_START_OUTSIDE_SOURCE_WINDOW" => "动作起点必须在来源窗口内",
            "PLAN_NOT_EDITABLE" => "计划已锁定或不可编辑", "ADRENALINE_INSUFFICIENT" => "肾上腺素不足",
            "VIEW_INPUT_UNAVAILABLE" => "当前不能提交", _ => code
        };
        private RectTransform Region(string name, float x0, float y0, float x1, float y1)
        {
            var rect = Child(_canvas, name, x0, y0, x1, y1); _created.Add(rect.gameObject);
            rect.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(.025f, .045f, .07f, .96f);
            rect.GetComponent<UnityEngine.UI.Image>().raycastTarget = false; return rect;
        }
        private static RectTransform Child(Transform parent, string name, float x0, float y0, float x1, float y1)
        { var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false); var rect = (RectTransform)go.transform; Place(rect, x0, y0, x1, y1); return rect; }
        private static void Place(RectTransform rect, float x0, float y0, float x1, float y1)
        { if (rect == null) return; rect.anchorMin = new Vector2(x0, y0); rect.anchorMax = new Vector2(x1, y1); rect.offsetMin = rect.offsetMax = Vector2.zero; }
        private TMP_Text Label(Transform parent, string text, int size)
        {
            var rect = Child(parent, "Label", 0, 0, 1, 1); rect.offsetMin = new Vector2(8, 2); rect.offsetMax = new Vector2(-8, -2);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>(); label.font = _font; label.fontSize = size;
            label.text = text; label.raycastTarget = false; label.alignment = TextAlignmentOptions.MidlineLeft; return label;
        }
        private UnityEngine.UI.Button Button(RectTransform rect, string label, UnityEngine.Events.UnityAction click)
        {
            rect.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(.12f, .22f, .31f);
            var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>(); Label(rect, label, 16); button.onClick.AddListener(click); return button;
        }
        private static RectTransform Scroll(RectTransform parent)
        {
            var scroll = parent.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();
            var viewport = Child(parent, "Viewport", 0, 0, 1, 1); viewport.gameObject.AddComponent<UnityEngine.UI.Image>();
            viewport.gameObject.AddComponent<UnityEngine.UI.Mask>().showMaskGraphic = false;
            var content = Child(viewport, "Content", 0, 1, 1, 1); content.pivot = new Vector2(.5f, 1);
            var group = content.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
            group.childControlWidth = group.childControlHeight = true; group.childForceExpandWidth = true; group.childForceExpandHeight = false; group.spacing = 4;
            content.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>().verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false; scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped; return content;
        }
        public void Release() { HideResumeChoice(); _draftKey = _windowKey = null; }
        private void OnDestroy()
        { if (_dynamicFont != null) Destroy(_dynamicFont); }
    }
}
