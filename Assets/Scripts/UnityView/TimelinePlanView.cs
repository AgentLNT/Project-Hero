using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Actions;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProjectHero.UnityView
{
    public sealed class TimelinePlanView : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        private BattlePresentationView _view;
        private long _id, _start, _dragRevision, _dragStart;
        private bool _editable, _dragging;
        private float _origin;
        private RectTransform _dragSpace;
        private TMPro.TMP_Text _label;
        private long _draftId, _dragDraftId;
        private ActionPlanSnapshot _plan, _dragPlan;
        private ScheduleEditOperation _previousOperation;
        private RectTransform _impact, _recovery;
        private TMPro.TMP_Text _windupLabel, _recoveryLabel;
        private readonly List<GameObject> _defenseMarks = new List<GameObject>();
        private string _defenseKey;
        private string _defenseLabelSuffix;
        public long PlanId => _id;
        public long DraftId => _draftId;
        public bool IsDragging => _dragging;
        public void Initialize(BattlePresentationView view, long id, long draftId = 0) { _view = view; _id = id; _draftId = draftId; _label = GetComponentInChildren<TMPro.TMP_Text>(); }
        public void Refresh(ActionPlanSnapshot plan, bool editable, bool conditional)
        {
            _editable = editable; _start = plan.StartTick; _plan = plan; _id = plan.ActionPlanId;
            string state = plan.State == (int)ProjectHero.Logic.Actions.ActionPlanState.Editable ? "已安排·可修改"
                : plan.State == (int)ProjectHero.Logic.Actions.ActionPlanState.Running ? "执行中"
                : plan.State == (int)ProjectHero.Logic.Actions.ActionPlanState.Completed ? "完成"
                : plan.State == (int)ProjectHero.Logic.Actions.ActionPlanState.Terminated ? "已中止" : "已锁定";
            _label.text = "#" + _id + "  " + M1HudView.ActionName(plan.ActionSpecId) + " · " + state
                + $"  {plan.StartTick / (double)_view.Model.Decision.Definition.TicksPerSecond:0.000}s → {plan.EndTick / (double)_view.Model.Decision.Definition.TicksPerSecond:0.000}s"
                + (conditional ? "  [闪避成功后可能取消]" : "");
        }
        public void OnBeginDrag(PointerEventData data)
        {
            if (!_editable || !_view.Model.CanPlan || _view.IsAwaitingWorldSelection || _view.Model.Drafts.State == ScheduleDraftState.AwaitingAcceptance
                || data.button != PointerEventData.InputButton.Left) return;
            _dragSpace = transform.parent as RectTransform;
            if (_dragSpace == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_dragSpace, data.position, data.pressEventCamera, out var point)) return;
            _origin = point.x; _dragStart = _start; _dragPlan = _plan; _dragRevision = _view.Model.Decision.ScheduleRevision; _dragging = true;
            _dragDraftId = _draftId;
            if (_dragDraftId == 0) _dragDraftId = _view.Model.Drafts.Entries.FirstOrDefault(e => e.Operation.PlanId.Value == _id)?.Id ?? 0;
            _previousOperation = _view.Model.Drafts.Entries.FirstOrDefault(e => e.Id == _dragDraftId)?.Operation;
        }
        public void OnDrag(PointerEventData data)
        {
            if (!_dragging) return;
            if (!_view.Model.CanPlan || _draftId == 0 && !_view.Model.CanEdit(_id)
                || _view.Model.Decision.ScheduleRevision != _dragRevision || _view.Model.Drafts.State == ScheduleDraftState.Stale)
            { StopDrag(false); return; }
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_dragSpace, data.position, data.pressEventCamera, out var point)) return;
            double seconds = (point.x - _origin) / _view.TimelinePixelsPerSecond;
            long delta = (long)System.Math.Round(seconds * _view.Model.Decision.Definition.TicksPerSecond, System.MidpointRounding.AwayFromZero);
            long desired = _dragStart + delta;
            var snap = TimelineSnap.Resolve(_dragPlan, desired, _view.TimelineView.DisplayPlans, _view.Model.LockLine,
                (_view.Model.Window?.EndTick ?? long.MaxValue) - 1, Mathf.RoundToInt(_view.Model.Decision.Definition.TicksPerSecond * .15f),
                id => _view.Model.Decision.FindAction(new ActionSpecId(id))?.Type == ActionType.Attack);
            if (snap.Snapped) _view.TimelineView.ShowSnap(snap.AnchorTick); else _view.TimelineView.ClearSnap();
            long tick = System.Math.Max(_view.Model.LockLine, snap.StartTick);
            if (_draftId != 0)
            {
                if (_view.Model.Drafts.Entries.FirstOrDefault(e => e.Id == _draftId)?.Operation is AddOrdinaryPlanOperation add)
                    _view.Model.EditDraft(_draftId, add with { RequestedStartTick = tick });
            }
            else
            {
                _view.Model.UpsertPlanEdit(new MoveEditablePlanOperation(new ActionPlanId(_id), tick));
                _dragDraftId = _view.Model.Drafts.Entries.FirstOrDefault(e => e.Operation.PlanId.Value == _id)?.Id ?? 0;
            }
            var actual = _draftId != 0 ? _view.Model.PreviewPlanFor(_draftId)
                : _view.Model.Preview?.PreviewPlans?.FirstOrDefault(p => p.Plan.ActionPlanId == _id)?.Plan;
            if (_view.Model.Preview?.Succeeded != true || actual?.StartTick != tick) _view.TimelineView.ClearSnap();
            _view.TimelineView.Refresh();
            _view.RefreshPlanningHud();
        }
        public void OnEndDrag(PointerEventData data) { StopDrag(false); }
        private void Update()
        {
            if (_dragging && (!_view.Model.CanPlan || _view.Model.Decision.ScheduleRevision != _dragRevision
                || _view.Model.Drafts.State == ScheduleDraftState.Stale || _view.Model.Drafts.State == ScheduleDraftState.AwaitingAcceptance
                || _draftId == 0 && !_view.Model.CanEdit(_id))) StopDrag(false);
        }
        public void CancelDrag() => StopDrag(true);
        private void StopDrag(bool restore)
        {
            if (!_dragging) return; _dragging = false; _view.TimelineView.ClearSnap();
            if (restore && _view.Model.CanPlan && _view.Model.Decision.ScheduleRevision == _dragRevision)
            {
                if (_previousOperation != null) _view.Model.EditDraft(_dragDraftId, _previousOperation);
                else if (_dragDraftId != 0) _view.Model.RemoveDraft(_dragDraftId);
                _view.TimelineView.Refresh();
                _view.RefreshPlanningHud();
            }
        }
        private void OnDisable() { if (_dragging) StopDrag(false); }
        public void OnPointerClick(PointerEventData data)
        {
            if (_editable && _view.Model.CanPlan && data.button == PointerEventData.InputButton.Right)
            {
                if (_dragging) { CancelDrag(); return; }
                if (_draftId != 0) _view.Model.RemoveDraft(_draftId);
                else _view.Model.UpsertPlanEdit(new RemoveEditablePlanOperation(new ActionPlanId(_id)));
                _view.TimelineView.Refresh();
                _view.RefreshPlanningHud();
            }
        }
        public void SetTrackLabel(bool primary, bool draft, IEnumerable<string> outcomes)
        {
            string state = _plan.ActionPlanId < 0 ? " · 预放置" : _plan.State == (int)ActionPlanState.Editable ? "" : _plan.State == (int)ActionPlanState.Running ? " · 执行中"
                : _plan.State == (int)ActionPlanState.Locked ? " · 已锁定" : " · 历史";
            _label.text = (draft ? "┄ 草稿 " : "") + M1HudView.ActionName(_plan.ActionSpecId) + state;
            if (primary) _label.text += " " + string.Join(" / ", outcomes);
            _label.fontSize = primary ? 14 : 12; _label.overflowMode = TMPro.TextOverflowModes.Truncate;
            _label.alignment = primary && _view.Model.Decision.FindAction(new ActionSpecId(_plan.ActionSpecId))?.Type == ActionType.Attack
                ? TMPro.TextAlignmentOptions.TopLeft : TMPro.TextAlignmentOptions.MidlineLeft;
            _label.raycastTarget = false;
        }
        public void SetImpact(bool attack, float ratio, bool primary, bool unknown)
        {
            if (_impact == null)
            {
                var go = new GameObject("Impact", typeof(RectTransform)); go.transform.SetParent(transform, false); _impact = (RectTransform)go.transform;
                var text = go.AddComponent<TMPro.TextMeshProUGUI>(); text.font = _label.font; text.fontSize = 16; text.raycastTarget = false;
                var recovery = new GameObject("Recovery", typeof(RectTransform)); recovery.transform.SetParent(transform, false); _recovery = (RectTransform)recovery.transform;
                var image = recovery.AddComponent<UnityEngine.UI.Image>(); image.color = new Color(.1f, .02f, .03f, .35f); image.raycastTarget = false;
                _recovery.SetAsFirstSibling();
                _windupLabel = PhaseLabel("Windup"); _recoveryLabel = PhaseLabel("RecoveryLabel");
            }
            _impact.gameObject.SetActive(attack); _recovery.gameObject.SetActive(attack);
            _windupLabel.gameObject.SetActive(attack && primary); _recoveryLabel.gameObject.SetActive(attack && primary);
            if (!attack) return;
            _impact.anchorMin = _impact.anchorMax = new Vector2(Mathf.Clamp01(ratio), .5f); _impact.pivot = new Vector2(.5f, .5f); _impact.anchoredPosition = Vector2.zero; _impact.sizeDelta = new Vector2(22, 24);
            _impact.GetComponent<TMPro.TMP_Text>().text = unknown ? "◇" : "◆";
            _recovery.anchorMin = new Vector2(Mathf.Clamp01(ratio), 0); _recovery.anchorMax = Vector2.one; _recovery.offsetMin = _recovery.offsetMax = Vector2.zero;
            Phase(_windupLabel, 0, ratio, "前摇"); Phase(_recoveryLabel, ratio, 1, "后摇·可反击");
        }
        public void SetDefense(ActionType? type, DefensePlanPreview preview, bool primary)
        {
            string key = type + ":" + _plan + ":" + primary + ":" + preview?.Contact + ":" + string.Join(";", preview?.Contact.GuardContacts ?? System.Array.Empty<GuardContactPreview>());
            if (_defenseKey == key) { _label.text += _defenseLabelSuffix; return; } _defenseKey = key; _defenseLabelSuffix = "";
            foreach (var mark in _defenseMarks) { mark.SetActive(false); Destroy(mark); } _defenseMarks.Clear();
            if (type != ActionType.Guard && type != ActionType.Block && type != ActionType.Dodge) return;
            string baseLabel = _label.text;
            bool reaction = type != ActionType.Guard;
            long duration = System.Math.Max(1, _plan.EndTick - _plan.StartTick);
            float Ratio(long tick) => Mathf.Clamp01((float)((tick - _plan.StartTick) / (double)duration));
            if (reaction)
            {
                float hardEnd = Ratio(_plan.SourceImpactTick + 1);
                // Inclusive source impact belongs to the hard interval; the soft interval starts at the next Tick.
                for (float x = hardEnd; x < 1; x += .06f)
                {
                    DefenseMark("SoftHatching", x, Mathf.Min(x + .018f, 1), .1f, .9f, "", new Color(.7f, .8f, .9f, .5f));
                    _defenseMarks[_defenseMarks.Count - 1].transform.localRotation = Quaternion.Euler(0, 0, -35);
                }
                if (primary) _label.text += _plan.ActionPlanId < 0 ? " · 预放置/待恢复" : " · 硬占用 / 软占用";
                if (preview?.Contact.HasEarlierPreemptingContact == true)
                { GetComponent<UnityEngine.UI.Image>().color = new Color(.62f, .3f, .08f); if (primary) _label.text += " · 提前消耗"; }
                long? actualTrigger = _plan.ReactionTriggeredAtTick >= 0 ? _plan.ReactionTriggeredAtTick : (long?)null;
                long? actualYield = _plan.TerminationReason == (int)ActionTerminationReason.ReactionYieldedLane ? _plan.TerminalTick : (long?)null;
                if ((preview?.Contact.TriggerTick ?? actualTrigger) is long trigger)
                { DefenseMark("DefenseTrigger", Ratio(trigger), Ratio(trigger) + .03f, 0, 1, "◆", Color.white); DashedTail(Ratio(trigger + 1), "已生效"); }
                else if ((preview?.Contact.YieldTick ?? actualYield) is long yield)
                { DefenseMark("YieldPoint", Ratio(yield), 1, 0, .55f, "在此让出轨道", Color.white); DashedTail(Ratio(yield), ""); }
            }
            else
            {
                var payload = _view.Model.Decision.FindAction(new ActionSpecId(_plan.ActionSpecId))?.Payload as GuardPayloadSpec;
                var ratios = (payload?.DamageResistanceQ10?.Values ?? System.Array.Empty<int>()).Append(payload?.MomentumResistanceQ10 ?? 0).Where(r => r > 0).Distinct().OrderBy(r => r).ToArray();
                if (primary)
                    _label.text += " · 额度 " + (_plan.DefenseQuotaRemainingQ10 / 1024d).ToString("0.##") + " "
                        + (ratios.Length == 0 ? "" : new string('■', System.Math.Min(16, _plan.DefenseQuotaRemainingQ10 / ratios.Max()))) + " "
                        + string.Join(" / ", ratios.Select(r => (r * 100d / 1024).ToString("0.#") + "%×" + (_plan.DefenseQuotaRemainingQ10 / r)));
                foreach (var contact in preview?.Contact.GuardContacts ?? System.Array.Empty<GuardContactPreview>())
                {
                    DefenseMark("GuardContact-" + contact.Tick, Ratio(contact.Tick), Mathf.Min(1, Ratio(contact.Tick) + .34f), 0, .5f,
                        "◆ 抵 " + (contact.DebitQ10 * 100d / 1024).ToString("0.#") + "%", Color.white);
                    if (contact.RemainingQ10 == 0) DashedTail(Ratio(contact.Tick + 1), "额度耗尽");
                }
            }
            if (_plan.State == (int)ActionPlanState.Terminated && primary)
                _label.text += _plan.TerminationReason == (int)ActionTerminationReason.ReactionTriggered ? " · 已生效"
                    : _plan.TerminationReason == (int)ActionTerminationReason.ReactionYieldedLane ? " · 已让出轨道"
                    : _plan.TerminationReason == (int)ActionTerminationReason.ReactionExpired ? " · 未遇到命中，已结束" : "";
            _defenseLabelSuffix = _label.text.Substring(baseLabel.Length);
        }
        private void DashedTail(float start, string text)
        {
            DefenseMark("InactiveTail", start, 1, 0, 1, "", new Color(.02f, .04f, .06f, .82f));
            for (float x = start; x < 1; x += .09f) DefenseMark("TailDash", x, Mathf.Min(1, x + .04f), .02f, .09f, "", new Color(.7f, .75f, .8f));
            if (!string.IsNullOrEmpty(text)) DefenseMark("TailReason", start, 1, 0, .45f, text, Color.white);
        }
        private void DefenseMark(string name, float start, float end, float bottom, float top, string text, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(transform, false); _defenseMarks.Add(go);
            var rect = (RectTransform)go.transform; rect.anchorMin = new Vector2(Mathf.Clamp01(start), bottom); rect.anchorMax = new Vector2(Mathf.Clamp01(end), top); rect.offsetMin = rect.offsetMax = Vector2.zero;
            if (string.IsNullOrEmpty(text)) { var image = go.AddComponent<UnityEngine.UI.Image>(); image.color = color; image.raycastTarget = false; }
            else { var label = go.AddComponent<TMPro.TextMeshProUGUI>(); label.font = _label.font; label.fontSize = 11; label.text = text; label.color = color; label.raycastTarget = false; label.overflowMode = TMPro.TextOverflowModes.Truncate; }
        }
        private TMPro.TMP_Text PhaseLabel(string name)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(transform, false);
            var label = go.AddComponent<TMPro.TextMeshProUGUI>(); label.font = _label.font; label.fontSize = 11;
            label.raycastTarget = false; label.overflowMode = TMPro.TextOverflowModes.Truncate; return label;
        }
        private static void Phase(TMPro.TMP_Text label, float start, float end, string text)
        {
            label.text = text; label.rectTransform.anchorMin = new Vector2(Mathf.Clamp01(start), 0);
            label.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(end), .45f);
            label.rectTransform.offsetMin = new Vector2(3, 0); label.rectTransform.offsetMax = new Vector2(-2, 0);
        }
    }
}
