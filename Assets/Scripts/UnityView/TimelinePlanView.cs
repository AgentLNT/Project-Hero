using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
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
        private TMPro.TMP_Text _label;
        public void Initialize(BattlePresentationView view, long id) { _view = view; _id = id; _label = GetComponentInChildren<TMPro.TMP_Text>(); }
        public void Refresh(ActionPlanSnapshot plan, bool editable, bool conditional)
        {
            _editable = editable; _start = plan.StartTick;
            _label.text = "#" + _id + "  " + (ProjectHero.Logic.Actions.ActionPlanState)plan.State + "  " + plan.StartTick + " -> " + plan.EndTick
                + (conditional ? "  [may cancel after Dodge]" : "");
        }
        public void OnBeginDrag(PointerEventData data) { if (!_editable) return; _origin = data.position.x; _dragStart = _start; _dragRevision = _view.Model.Decision.ScheduleRevision; _dragging = true; }
        public void OnDrag(PointerEventData data)
        {
            if (!_dragging || !_view.Model.CanEdit(_id)) return;
            if (_view.Model.Decision.ScheduleRevision != _dragRevision) { _dragging = false; return; }
            long tick = System.Math.Max(_view.Model.LockLine, _dragStart + Mathf.RoundToInt((data.position.x - _origin) / 4f));
            _view.Model.SetDraft(new ScheduleEditOperation[] { new MoveEditablePlanOperation(new ActionPlanId(_id), tick) });
        }
        public void OnEndDrag(PointerEventData data) { _dragging = false; }
        public void OnPointerClick(PointerEventData data)
        {
            if (_editable && data.button == PointerEventData.InputButton.Right)
                _view.Model.SetDraft(new ScheduleEditOperation[] { new RemoveEditablePlanOperation(new ActionPlanId(_id)) });
        }
    }
}
