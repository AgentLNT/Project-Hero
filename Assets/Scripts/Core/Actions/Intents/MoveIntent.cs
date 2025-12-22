using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Interactions;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;

namespace ProjectHero.Core.Actions.Intents
{
    public class MoveIntent : CombatIntent
    {
        public Pathfinder.GridPoint From { get; private set; }
        public Pathfinder.GridPoint To { get; private set; }
        public float Duration { get; private set; }
        public int StepIndex { get; private set; }
        public bool Rotate { get; private set; }
        public bool IsForced { get; private set; }

        private BattleTimeline _timeline;
        private readonly long _groupId;
        private System.Collections.Generic.List<TrianglePoint> _reservedVolume;

        public MoveIntent(CombatUnit owner, Pathfinder.GridPoint from, Pathfinder.GridPoint to, float duration, int index, BattleTimeline timeline, long groupId = 0, bool rotate = true, bool isForced = false)
            : base(owner, ActionType.Move)
        {
            From = from;
            To = to;
            Duration = duration;
            StepIndex = index;
            _timeline = timeline;
            _groupId = groupId;
            Rotate = rotate;
            IsForced = isForced;
        }

        public override void ExecuteSuccess()
        {
            if (!IsForced && (Owner.IsStaggered || Owner.IsKnockedDown)) return;

            // Åö×²¼ì²â
            var dir = GridMath.GetDirection(From, To);
            var projectedVolume = Owner.GetProjectedOccupancy(To, dir);

            if (GridManager.Instance.IsSpaceOccupied(projectedVolume, Owner))
            {
                Debug.LogWarning($"[Movement] Path blocked for {Owner.name} at step {StepIndex}!");
                _timeline.CancelEvents(Owner);
                Owner.ResetActionState();
                return;
            }

            // Ô¤Ô¼
            _reservedVolume = projectedVolume;
            if (GridManager.Instance != null) GridManager.Instance.RegisterReservation(Owner, projectedVolume);

            // ÊÓ¾õ²åÖµ
            var mover = Owner.GetComponent<Visuals.UnitMovement>();
            if (mover != null)
            {
                Vector3 targetWorldPos = GridManager.Instance.GridToWorld(To);
                mover.MoveVisuals(targetWorldPos, Duration, null, Rotate, expectedLogicEnd: To);
            }

            // Âß¼­Ìá½» (Priority > State)
            if (_timeline != null)
            {
                var commit = new CommitMoveStepIntent(Owner, From, To, _reservedVolume != null ? new System.Collections.Generic.List<TrianglePoint>(_reservedVolume) : null);
                // State(50) + 1 = 51
                _timeline.Schedule(Duration, commit, $"Commit {StepIndex}", _groupId, TimelinePriority.State + 1);
            }
        }

        public override void ExecuteInterruption(InteractionType interactionType)
        {
            base.ExecuteInterruption(interactionType);
            if (GridManager.Instance != null && _reservedVolume != null) GridManager.Instance.UnregisterReservation(Owner, _reservedVolume);
            var mover = Owner != null ? Owner.GetComponent<Visuals.UnitMovement>() : null;
            if (mover != null) mover.CancelVisualMoveAndSnapToLogic();
            _timeline.CancelEvents(Owner);
            Owner.ResetActionState();
        }
    }
}
