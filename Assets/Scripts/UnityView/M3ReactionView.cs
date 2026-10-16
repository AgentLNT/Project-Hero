using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace ProjectHero.UnityView
{
    /// <summary>Local reaction configuration. All feasibility, contact and quota results come from Logic.</summary>
    public sealed class M3ReactionView : MonoBehaviour
    {
        private sealed class Card
        {
            public RectTransform Root, Marks;
            public TMP_Text Title, Warning, Reset, Status;
            public UnityEngine.UI.Slider Start;
            public UnityEngine.UI.Button Confirm, Ignore;
            public readonly Dictionary<string, UnityEngine.UI.Button> Options = new Dictionary<string, UnityEngine.UI.Button>();
            public ActionSpecId Action;
            public long StartTick;
            public GridPoint? Destination;
            public bool Ignored;
            public string Error;
        }
        private BattlePresentationView _view;
        private TMP_FontAsset _font;
        private RectTransform _root, _content, _markers;
        private TMP_Text _summary, _shortcutHint;
        private readonly Dictionary<long, Card> _cards = new Dictionary<long, Card>();
        private readonly Dictionary<long, UnityEngine.UI.Button> _threatMarkers = new Dictionary<long, UnityEngine.UI.Button>();
        private string _key;
        private string _liveGate;
        private double _lastLiveRefresh;
        private long _active;
        private bool _refreshing;
        private GameObject _landing;
        public bool AwaitingDestination => _active != 0 && _cards.TryGetValue(_active, out var card)
            && _view.Model.Decision.FindAction(card.Action)?.Type == ActionType.Dodge && !card.Destination.HasValue && !card.Ignored;
        public long ActiveOpportunity => _active;
        public string ShortcutHintText => _shortcutHint?.text;
        public DefensePlacementPreview DefensePreview { get; private set; }
        public IReadOnlyList<ReactionPlacement> PreviewPlacements { get; private set; } = Array.Empty<ReactionPlacement>();
        public ReactionPlacement Candidate => _active != 0 && _cards.TryGetValue(_active, out var card) && !card.Ignored
            ? new ReactionPlacement(_active, card.Action, card.StartTick, card.Destination) : null;

        public void Bind(BattlePresentationView view, RectTransform canvas, TMP_FontAsset font, RectTransform oldReactions)
        {
            _view = view; _font = font; oldReactions.parent.parent.gameObject.SetActive(false);
            if (_root != null) { Release(); _key = null; return; }
            _markers = Rect(canvas, "M3ThreatMarkers", 0, 0, 1, 1);
            _root = Rect(canvas, "M3ReactionCards", .29f, .48f, .70f, .89f);
            Background(_root, new Color(.035f, .065f, .095f, .98f));
            _summary = Text(Rect(_root, "Summary", 0, .91f, 1, 1), "", 18);
            var help = Rect(canvas, "M3ReactionHelp", .72f, .08f, .98f, .30f);
            Background(help, new Color(.035f, .065f, .095f, .98f), false);
            _shortcutHint = Text(Rect(help, "ShortcutHint", .03f, .04f, .97f, .96f), "", 16);
            _shortcutHint.alignment = TextAlignmentOptions.TopLeft;
            var scrollRoot = Rect(_root, "Scroll", 0, 0, 1, .91f);
            var scroll = scrollRoot.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();
            var viewport = Rect(scrollRoot, "Viewport", 0, 0, 1, 1);
            Background(viewport, Color.white); viewport.gameObject.AddComponent<UnityEngine.UI.Mask>().showMaskGraphic = false;
            _content = Rect(viewport, "Content", 0, 1, 1, 1); _content.pivot = new Vector2(.5f, 1);
            var layout = _content.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>(); layout.spacing = 8;
            layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            _content.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>().verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = _content; scroll.viewport = viewport; scroll.horizontal = false; scroll.scrollSensitivity = 36;
            scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
        }
        private Card CreateCard(ReactionOpportunitySnapshot opportunity)
        {
            long id = opportunity.ReactionOpportunityId;
            var card = new Card { StartTick = opportunity.SourceImpactTick };
            card.Root = Rect(_content, "Reaction-" + id, 0, 0, 1, 1);
            card.Root.gameObject.AddComponent<UnityEngine.UI.LayoutElement>().preferredHeight = 380;
            Background(card.Root, new Color(.075f, .115f, .16f));
            card.Title = Text(Rect(card.Root, "Title", .02f, .84f, .98f, 1), "", 17);
            int index = 0;
            foreach (var option in opportunity.Options.Where(o => o.IsPublished))
            {
                var action = new ActionSpecId(option.ActionSpecId);
                var button = Button(Rect(card.Root, "Option-" + action.Value, .02f, .69f - index * .15f, .98f, .83f - index * .15f), "", () => Select(id, action));
                card.Options.Add(action.Value, button); index++;
                if (string.IsNullOrEmpty(card.Action.Value)) card.Action = action;
            }
            var sliderRect = Rect(card.Root, "StartPlacement", .04f, .33f, .96f, .40f);
            Background(sliderRect, new Color(.15f, .23f, .30f));
            card.Start = sliderRect.gameObject.AddComponent<UnityEngine.UI.Slider>(); card.Start.wholeNumbers = true;
            var handle = Rect(sliderRect, "Handle", 0, 0, 0, 1); handle.sizeDelta = new Vector2(12, 0);
            var image = Background(handle, new Color(1, .8f, .25f)); card.Start.handleRect = handle; card.Start.targetGraphic = image;
            card.Start.onValueChanged.AddListener(value => { if (!_refreshing) SetStart(id, (long)value); });
            card.Marks = Rect(card.Root, "EarlierContactMarks", .04f, .33f, .96f, .40f);
            card.Warning = Text(Rect(card.Root, "Warning", .02f, .14f, .98f, .32f), "", 15);
            card.Reset = Text(Rect(card.Root, "Reset", .02f, .07f, .98f, .14f), "", 13);
            card.Status = Text(Rect(card.Root, "Status", .02f, 0, .98f, .07f), "", 13);
            card.Confirm = Button(Rect(card.Root, "ConfirmReaction", .02f, .42f, .35f, .51f), "确认反应", () => Confirm(id));
            card.Ignore = Button(Rect(card.Root, "IgnoreReaction", .37f, .42f, .62f, .51f), "不响应", () => Ignore(id));
            Button(Rect(card.Root, "FocusConflict", .64f, .42f, .98f, .51f), "跳到冲突计划", () => FocusConflict(id));
            return card;
        }
        public void Select(long id, ActionSpecId action)
        {
            if (!_view.Model.CanPlan || _view.ResumeChoiceVisible || !_cards.TryGetValue(id, out var card)) return;
            if (_view.SpatialView?.Following == false) _view.SpatialView.ReturnToNow();
            _view.CancelConfiguration(); card.Action = action; card.Destination = null; card.Ignored = false; card.Error = null;
            var opportunity = _view.Model.Reactions.First(r => r.ReactionOpportunityId == id);
            _view.AddContactTrack(opportunity.DefenderUnitId);
            var source = _view.Model.Timeline.FirstOrDefault(p => p.ActionPlanId == opportunity.SourceAttackPlanId);
            if (source != null) _view.AddContactTrack(source.OwnerUnitId);
            _view.FocusTrack(opportunity.DefenderUnitId); _active = id;
            Refresh(true); _view.TimelineView.FocusTick(opportunity.SourceImpactTick);
            ScrollActive();
        }
        public void SetStart(long id, long tick)
        {
            if (!_view.Model.CanPlan || !_cards.TryGetValue(id, out var card)) return;
            var opportunity = _view.Model.Reactions.FirstOrDefault(r => r.ReactionOpportunityId == id); if (opportunity == null) return;
            _active = id; card.Ignored = false; card.StartTick = Math.Max(_view.Model.LockLine, Math.Min(opportunity.SourceImpactTick, tick));
            card.Error = null; Refresh(true); ScrollActive();
        }
        private void ScrollActive()
        {
            if (!_cards.TryGetValue(_active, out var card) || !_root.gameObject.activeSelf) return;
            Canvas.ForceUpdateCanvases();
            float top = -card.Root.anchoredPosition.y - card.Root.rect.height * (1 - card.Root.pivot.y);
            _content.anchoredPosition = new Vector2(0, Mathf.Clamp(top, 0,
                Mathf.Max(0, _content.rect.height - ((RectTransform)_content.parent).rect.height)));
            _content.parent.parent.GetComponent<UnityEngine.UI.ScrollRect>().StopMovement();
        }
        public bool SelectDestination(GridPoint point)
        {
            if (!AwaitingDestination || !_view.Model.CanPlan) return false;
            var card = _cards[_active];
            var model = _view.Model;
            var proposed = model.StagedReactions.Where(r => r.OpportunityId != _active)
                .Append(new ReactionPlacement(_active, card.Action, card.StartTick, point)).ToArray();
            card.Error = model.HudPreviews.PreviewDefensePlacements(model.Drafts.HasIntent ? model.Drafts.Operations : Array.Empty<ScheduleEditOperation>(), proposed).RejectionCode;
            if (card.Error == null) { card.Destination = point; ShowLanding(point); }
            Refresh(true); if (card.Error == null) ScrollActive(); return card.Error == null;
        }
        public void Confirm(long id)
        {
            if (!_cards.TryGetValue(id, out var card) || !_view.Model.CanPlan) return;
            card.Error = _view.Model.ConfirmReaction(id, card.Action, card.Destination, card.StartTick);
            if (card.Error == null) { _active = 0; HideLanding(); }
            Refresh(true);
        }
        public void Ignore(long id)
        {
            if (!_view.Model.CanPlan || !_cards.TryGetValue(id, out var card)) return;
            if (_view.Model.PendingReactions.Any(r => r.OpportunityId == id)
                || _view.Model.Reactions.Any(r => r.ReactionOpportunityId == id && r.BoundActionPlanId != 0)) return;
            card.Ignored = true; _view.Model.RemoveStagedReaction(id);
            if (_active == id) { _active = 0; HideLanding(); } Refresh(true);
        }
        public bool CancelSelection()
        { if (_active == 0) return false; _active = 0; HideLanding(); Refresh(true); return true; }
        public void FocusConflict(long id)
        {
            var card = _cards[id]; var feasible = _view.Model.ReactionFeasibility(new ReactionPlacement(id, card.Action, card.StartTick, card.Destination));
            var conflict = feasible.ConflictingPlans.FirstOrDefault(); if (conflict == null) return;
            CancelSelection(); _view.AddContactTrack(conflict.OwnerUnitId); _view.TimelineView.FocusTick(conflict.StartTick);
        }
        public void Refresh(bool force = false)
        {
            if (_root == null || _view.Model.Decision == null) return;
            var model = _view.Model;
            string liveGate = model.Decision.ScheduleRevision + ":" + _view.Clock.State + ":" + model.Drafts.State + ":" + _active
                + ":" + string.Join(";", model.Drafts.Operations) + ":" + string.Join(";", model.StagedReactions) + ":" + string.Join(";", model.PendingReactions)
                + ":" + string.Join(";", model.Reactions.Select(r => r.ReactionOpportunityId + ":" + r.State + ":" + r.BoundActionPlanId + ":" + r.OpenOptionCount))
                + ":" + string.Join(";", model.Adrenaline.Select(l => l.UnitId + ":" + l.AvailableAdrenaline + ":" + l.ReservedTotal)) + ":" + model.LastCommandStatus;
            double now = Time.realtimeSinceStartupAsDouble;
            if (!force && _view.Clock.State == BattleClockState.Running && liveGate == _liveGate && now - _lastLiveRefresh < .1) return;
            _liveGate = liveGate; _lastLiveRefresh = now;
            string key = model.Decision.Tick + ":" + model.Decision.ScheduleRevision + ":" + _view.Clock.State + ":"
                + string.Join(";", model.Drafts.Operations) + ":" + model.Drafts.State + ":" + string.Join(";", model.StagedReactions)
                + ":" + _active + ":" + model.LastCommandStatus + ":" + string.Join(";", model.PendingReactions);
            if (!force && key == _key) return; _key = key;
            bool blocked = _view.Clock.State == BattleClockState.SystemPaused || _view.Clock.State == BattleClockState.Ended;
            _markers.gameObject.SetActive(!blocked); if (blocked) HideLanding();
            var opportunities = model.Reactions.Where(r => r.Options?.Any(o => o.IsPublished) == true)
                .OrderBy(r => r.Options.Where(o => o.IsPublished).Min(o => o.ResponseDeadlineTick)).ThenBy(r => r.ReactionOpportunityId).ToArray();
            foreach (long id in _cards.Keys.Except(opportunities.Select(o => o.ReactionOpportunityId)).ToArray())
            { Destroy(_cards[id].Root.gameObject); _cards.Remove(id); if (_active == id) { _active = 0; HideLanding(); } }
            foreach (long id in _threatMarkers.Keys.Except(opportunities.Select(o => o.ReactionOpportunityId)).ToArray())
            { Destroy(_threatMarkers[id].gameObject); _threatMarkers.Remove(id); }
            foreach (var opportunity in opportunities)
                if (!_cards.ContainsKey(opportunity.ReactionOpportunityId))
                {
                    _cards.Add(opportunity.ReactionOpportunityId, CreateCard(opportunity));
                    if (_active == 0 && _view.Clock.State == BattleClockState.TacticalPlanning && opportunity.BoundActionPlanId == 0)
                        _active = opportunity.ReactionOpportunityId;
                }
            var operations = model.Drafts.HasIntent && model.Drafts.State != ScheduleDraftState.Stale ? model.Drafts.Operations : Array.Empty<ScheduleEditOperation>();
            var placements = model.StagedReactions.Concat(model.PendingReactions).ToList();
            var candidate = Candidate;
            if (candidate != null && model.ReactionFeasibility(candidate).IsFeasible
                && (model.Decision.FindAction(candidate.Action)?.Type != ActionType.Dodge || candidate.Destination.HasValue))
            { placements.RemoveAll(r => r.OpportunityId == candidate.OpportunityId); placements.Add(candidate); }
            DefensePreview = model.HudPreviews.PreviewDefensePlacements(operations, placements);
            PreviewPlacements = placements.AsReadOnly();
            _refreshing = true; int index = 0;
            foreach (var opportunity in opportunities)
            {
                long id = opportunity.ReactionOpportunityId;
                if (!_cards.TryGetValue(id, out var card)) { card = CreateCard(opportunity); _cards.Add(id, card); }
                card.Root.SetSiblingIndex(index++);
                var source = model.Timeline.FirstOrDefault(p => p.ActionPlanId == opportunity.SourceAttackPlanId);
                string sourceName = M1HudView.ActionName(source?.ActionSpecId);
                int tps = model.Decision.Definition.TicksPerSecond;
                long deadline = opportunity.Options.Where(o => o.IsPublished).Max(o => o.ResponseDeadlineTick);
                card.Title.text = $"{_view.UnitLabel(source?.OwnerUnitId ?? 0)} · {sourceName} → {_view.UnitLabel(opportunity.DefenderUnitId)}\n"
                    + $"{Math.Max(0, opportunity.SourceImpactTick - model.Decision.Tick) / (double)tps:0.00}s 后命中 · 输入截止 {Math.Max(0, deadline - model.LockLine) / (double)tps:0.00}s";
                card.Start.minValue = model.LockLine; card.Start.maxValue = Math.Max(model.LockLine, opportunity.SourceImpactTick);
                card.Start.SetValueWithoutNotify(card.StartTick); card.Start.interactable = model.CanPlan && model.LockLine <= deadline && !card.Ignored && opportunity.BoundActionPlanId == 0;
                foreach (var option in opportunity.Options.Where(o => o.IsPublished))
                {
                    var action = new ActionSpecId(option.ActionSpecId); var spec = model.Decision.FindAction(action);
                    var feasible = model.ReactionFeasibility(new ReactionPlacement(id, action, card.StartTick, card.Destination));
                    var button = card.Options[option.ActionSpecId];
                    button.interactable = model.CanPlan && feasible.IsFeasible && !card.Ignored && opportunity.BoundActionPlanId == 0;
                    button.GetComponentInChildren<TMP_Text>().text = (card.Action == action ? "已选 · " : "") + M1HudView.ActionName(option.ActionSpecId)
                        + $" · 肾上 {feasible.RequiredAdrenaline}（有 {feasible.AvailableAdrenaline}） · 持续 {Duration(spec) / (double)tps:0.00}s"
                        + $"\n输入截止 {Math.Max(0, option.ResponseDeadlineTick - model.LockLine) / (double)tps:0.00}s" + (feasible.IsFeasible ? "" : " · " + Explain(feasible));
                }
                var selected = model.ReactionFeasibility(new ReactionPlacement(id, card.Action, card.StartTick, card.Destination));
                var defense = DefensePreview.Defenses.FirstOrDefault(d => d.Plan.ReactionOpportunityId == id);
                bool dodgeAction = model.Decision.FindAction(card.Action)?.Type == ActionType.Dodge;
                card.Warning.text = $"起点 {card.StartTick / (double)tps:0.000}s · 可放 [{model.LockLine / (double)tps:0.000}, {opportunity.SourceImpactTick / (double)tps:0.000}]s · 默认=命中点\n";
                if (defense?.Contact.HasEarlierPreemptingContact == true)
                    card.Warning.text += "注意：将被" + string.Join("、", defense.Contact.SourcePlanIds.Select(p => M1HudView.ActionName(model.Timeline.FirstOrDefault(a => a.ActionPlanId == p)?.ActionSpecId)))
                        + "先消耗，" + sourceName + "未被挡住\n";
                else if (defense?.Contact.TriggerTick is long trigger) card.Warning.text += $"◆ 预计 {trigger / (double)tps:0.000}s 已生效\n";
                if (defense?.Contact.YieldTick is long yield) card.Warning.text += $"{yield / (double)tps:0.000}s 在此让出轨道\n";
                if (dodgeAction)
                {
                    if (!card.Destination.HasValue) card.Warning.text += "战场点击选择闪避落点（D）；Esc 退出";
                    else
                    {
                        var preview = model.PreviewDodge(id, card.Action, card.Destination.Value, card.StartTick);
                        card.Warning.text += $"落点 {card.Destination} · 成功换位将使 {defense?.ConditionalMovePlanIds?.Count ?? preview.AffectedPlanIds.Count} 条未来移动失效";
                        card.Warning.text += defense?.DodgeLanding is DodgeLandingPreview landing
                            ? $" · 新格仍被 {landing.StillCoveredAttacks} 条覆盖，不可闪避 {landing.RetainedUndodgeableAttacks} 条" : " · 视界内未预告换位结果";
                    }
                }
                card.Warning.color = defense?.Contact.HasEarlierPreemptingContact == true ? new Color(1, .68f, .3f) : Color.white;
                var next = model.HudPreviews.WindowSequence.FirstOrDefault(w => w.IsEstimated && w.OwnerUnitId == opportunity.DefenderUnitId);
                card.Reset.text = "下次自身窗口打开时，可用肾上腺素清零" + (next == null ? " · 时间未知" : $" · {Math.Max(0, next.OpenedAtTick - model.Decision.Tick) / (double)tps:0.00}s 后（预计）");
                bool staged = model.StagedReactions.Any(r => r.OpportunityId == id);
                card.Status.text = card.Error != null ? M1HudView.Explain(card.Error) : card.Ignored ? "不响应 · 可点此卡重新选择" : opportunity.BoundActionPlanId != 0 ? "权威已接受 · 反应已锁定"
                    : staged ? "待恢复提交 · 继续时间后送达权威" : !selected.IsFeasible ? Explain(selected) : "本地预览 · 尚未提交";
                if (model.ReactionStatus(id) != null && !card.Ignored) card.Status.text = model.ReactionStatus(id);
                card.Confirm.interactable = model.CanPlan && !card.Ignored && selected.IsFeasible && (!dodgeAction || card.Destination.HasValue)
                    && opportunity.BoundActionPlanId == 0 && !model.PendingReactions.Any(r => r.OpportunityId == id) && DefensePreview.RejectionCode == null && _active == id;
                card.Ignore.GetComponentInChildren<TMP_Text>().text = card.Ignored ? "重新查看" : staged ? "撤回待提交" : "不响应";
                card.Ignore.interactable = model.CanPlan && opportunity.BoundActionPlanId == 0 && !model.PendingReactions.Any(r => r.OpportunityId == id);
                card.Ignore.onClick.RemoveAllListeners(); card.Ignore.onClick.AddListener(() => { if (card.Ignored) { card.Ignored = false; Refresh(true); } else Ignore(id); });
                DrawEarlierMarks(card, opportunity, operations);
                if (!_threatMarkers.TryGetValue(id, out var marker))
                {
                    marker = Button(Rect(_markers, "ThreatMarker-" + id, 0, 0, 0, 0), "", () =>
                    { _view.Clock.PauseTactically("查看公开威胁", opportunity.DefenderUnitId); Select(id, card.Action); });
                    _threatMarkers.Add(id, marker);
                }
                marker.GetComponentInChildren<TMP_Text>().text = $"◆ {sourceName} · {Math.Max(0, opportunity.SourceImpactTick - model.Decision.Tick) / (double)tps:0.00}s"
                    + (selected.IsFeasible ? "" : " · " + Explain(selected));
                marker.gameObject.SetActive(!card.Ignored && opportunity.BoundActionPlanId == 0 && opportunity.SourceImpactTick >= model.LockLine);
                _view.PlaceThreatMarker((RectTransform)marker.transform, opportunity.DefenderUnitId, index - 1);
            }
            _refreshing = false;
            _root.gameObject.SetActive(!blocked && opportunities.Length > 0 && _view.Clock.State == BattleClockState.TacticalPlanning
                && _view.SpatialView?.Following != false);
            bool picking = AwaitingDestination;
            _root.anchorMin = new Vector2(.29f, picking ? .80f : .48f);
            _content.parent.parent.gameObject.SetActive(!picking);
            _summary.rectTransform.anchorMin = new Vector2(0, picking ? 0 : .91f);
            _summary.text = picking ? "在战场点击闪避落点 · Esc 取消\n落点选定后返回反应卡确认" : "公开威胁 " + opportunities.Length + " · 按输入截止排序 · 滚动查看 · B 格挡 / D 闪避";
            RefreshShortcutHint(opportunities, blocked);
            if (_root.gameObject.activeSelf && !_view.ResumeChoiceVisible) _root.SetAsLastSibling();
            _view.TimelineView?.Refresh();
        }
        private void DrawEarlierMarks(Card card, ReactionOpportunitySnapshot opportunity, IReadOnlyList<ScheduleEditOperation> operations)
        {
            foreach (Transform child in card.Marks) Destroy(child.gameObject);
            // Each mark is verified by proposing a start exactly at that public impact; time alignment alone is insufficient.
            foreach (long tick in _view.Model.Timeline.Where(p => p.ImpactTick >= _view.Model.LockLine && p.ImpactTick < opportunity.SourceImpactTick)
                .Select(p => p.ImpactTick).Distinct())
            {
                var proposed = new ReactionPlacement(opportunity.ReactionOpportunityId, card.Action, tick, card.Destination);
                if (_view.Model.Decision.FindAction(card.Action)?.Type == ActionType.Dodge && !card.Destination.HasValue) continue;
                var placements = _view.Model.StagedReactions.Where(r => r.OpportunityId != opportunity.ReactionOpportunityId).Append(proposed).ToArray();
                var result = _view.Model.HudPreviews.PreviewDefensePlacements(operations, placements);
                if (result.Defenses.FirstOrDefault(d => d.Plan.ReactionOpportunityId == opportunity.ReactionOpportunityId)?.Contact.HasEarlierPreemptingContact != true) continue;
                float x = (float)((tick - _view.Model.LockLine) / (double)Math.Max(1, opportunity.SourceImpactTick - _view.Model.LockLine));
                var mark = Rect(card.Marks, "Earlier-" + tick, x, 0, x, 1); mark.sizeDelta = new Vector2(3, 0);
                Background(mark, new Color(1, .55f, .2f), false);
            }
        }
        private void RefreshShortcutHint(ReactionOpportunitySnapshot[] opportunities, bool blocked)
        {
            _shortcutHint.transform.parent.gameObject.SetActive(!blocked);
            bool open = opportunities.Any(r => r.BoundActionPlanId == 0 && r.Options.Any(o => o.IsPublished && o.IsOpen));
            string state = !open ? "尚无可响应的公开威胁"
                : _view.Model.StagedReactionCount > 0 ? "反应已暂存 · 恢复时间后提交"
                : AwaitingDestination ? "闪避选点中 · 在战场点击合法落点"
                : "公开威胁已出现 · 查看卡片中的可用选项";
            _shortcutHint.text = "反应快捷键\n" + state + "\n"
                + "B 格挡 / D 闪避：有公开威胁时选择\n"
                + "Enter：确认反应（闪避须先选落点）\n"
                + "Esc：取消当前选择\n"
                + "资源、截止或排程冲突会使选项不可用；输入框中不触发。";
        }
        private bool SelectShortcut(ActionType type)
        {
            // Use the same availability as the visible option buttons, and keep
            // the currently selected threat ahead of unrelated earlier cards.
            foreach (var opportunity in _view.Model.Reactions.Where(r => r.BoundActionPlanId == 0 && r.Options?.Any(o => o.IsPublished && o.IsOpen) == true)
                .OrderBy(r => r.ReactionOpportunityId == _active ? 0 : 1)
                .ThenBy(r => r.Options.Min(o => o.ResponseDeadlineTick)).ThenBy(r => r.ReactionOpportunityId))
            {
                long id = opportunity.ReactionOpportunityId;
                if (!_cards.TryGetValue(id, out var card) || card.Ignored || _view.Model.PendingReactions.Any(r => r.OpportunityId == id)) continue;
                var option = opportunity.Options.FirstOrDefault(o => o.IsPublished && o.IsOpen
                    && _view.Model.Decision.FindAction(new ActionSpecId(o.ActionSpecId))?.Type == type
                    && card.Options.TryGetValue(o.ActionSpecId, out var button) && button.interactable);
                if (option == null) continue;
                _view.Clock.PauseTactically("选择反应", opportunity.DefenderUnitId);
                Select(id, new ActionSpecId(option.ActionSpecId)); return true;
            }
            return false;
        }
        private void Update()
        {
            if (_view == null || !_view.Model.CanPlan || _view.ResumeChoiceVisible || Keyboard.current == null) return;
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (selected != null && selected.TryGetComponent<TMP_InputField>(out var input) && input.isFocused) return;
            var keyboard = Keyboard.current;
            if (keyboard.bKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame)
            {
                var type = keyboard.bKey.wasPressedThisFrame ? ActionType.Block : ActionType.Dodge;
                SelectShortcut(type);
            }
            if (_active != 0 && keyboard.enterKey.wasPressedThisFrame && _cards.TryGetValue(_active, out var card) && card.Confirm.interactable) Confirm(_active);
        }
        private void ShowLanding(GridPoint point)
        {
            if (_landing == null) { _landing = GameObject.CreatePrimitive(PrimitiveType.Sphere); _landing.name = "M3DodgeLanding"; Destroy(_landing.GetComponent<Collider>()); _landing.transform.SetParent(transform, false); }
            _landing.transform.position = _view.GridWorld(point) + Vector3.up * .15f; _landing.transform.localScale = Vector3.one * .25f; _landing.SetActive(true);
        }
        private void HideLanding() { if (_landing != null) _landing.SetActive(false); }
        public void Release()
        { _active = 0; _key = _liveGate = null; DefensePreview = null; PreviewPlacements = Array.Empty<ReactionPlacement>(); HideLanding(); if (_shortcutHint != null) _shortcutHint.transform.parent.gameObject.SetActive(false); foreach (var c in _cards.Values) if (c.Root != null) Destroy(c.Root.gameObject); _cards.Clear(); foreach (var m in _threatMarkers.Values) if (m != null) Destroy(m.gameObject); _threatMarkers.Clear(); }
        private static int Duration(ActionSpec spec) => spec.Timing is BlockReactionTimingSpec block ? block.DurationTicks : spec.Timing is DodgeReactionTimingSpec dodge ? dodge.DurationTicks : 0;
        public static string Explain(ReactionFeasibility result) => result.RejectionCode == "ADRENALINE_INSUFFICIENT"
            ? $"肾上腺素不足（需 {result.RequiredAdrenaline}，有 {result.AvailableAdrenaline}）" : result.ConflictingPlans.Count > 0
            ? "需先调整" + string.Join("、", result.ConflictingPlans.Select(p => M1HudView.ActionName(p.ActionSpecId) + (p.State == (int)ActionPlanState.Editable ? "（可修改）" : "（已锁定/执行中）")))
            : M1HudView.Explain(result.RejectionCode);
        private static RectTransform Rect(Transform parent, string name, float x0, float y0, float x1, float y1)
        { var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false); var r = (RectTransform)go.transform; r.anchorMin = new Vector2(x0, y0); r.anchorMax = new Vector2(x1, y1); r.offsetMin = r.offsetMax = Vector2.zero; return r; }
        private static UnityEngine.UI.Image Background(RectTransform rect, Color color, bool raycast = true)
        { var image = rect.gameObject.AddComponent<UnityEngine.UI.Image>(); image.color = color; image.raycastTarget = raycast; return image; }
        private TMP_Text Text(RectTransform rect, string label, int size)
        { var text = rect.gameObject.AddComponent<TextMeshProUGUI>(); text.font = _font; text.fontSize = size; text.text = label; text.raycastTarget = false; text.alignment = TextAlignmentOptions.MidlineLeft; text.overflowMode = TextOverflowModes.Truncate; return text; }
        private UnityEngine.UI.Button Button(RectTransform rect, string label, UnityEngine.Events.UnityAction click)
        { Background(rect, new Color(.13f, .22f, .3f)); var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>(); var text = Rect(rect, "Label", .02f, 0, .98f, 1); Text(text, label, 15); button.onClick.AddListener(click); return button; }
    }
}
