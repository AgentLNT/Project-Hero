using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Interactions;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;

namespace ProjectHero.Core.Actions.Intents
{
    /// <summary>
    /// Represents a single step of movement.
    /// </summary>
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
            Debug.Log($"[MoveIntent] Step {StepIndex} executing for {Owner?.name}: {From} -> {To} (Duration={Duration:F2}s)");

            // Check for Status Effects that prevent movement (e.g. Stagger, Knockdown)
            // This prevents a MoveIntent from executing if the unit was interrupted in the same frame.
            // EXCEPTION: If the move is FORCED (Knockback), we ignore status effects.
            if (!IsForced && (Owner.IsStaggered || Owner.IsKnockedDown))
            {
                Debug.Log($"[MoveIntent] {Owner.name} cannot move due to status (Staggered/KnockedDown). Cancelling step.");
                return;
            }

            // Dynamic Obstacle Check
            var dir = GridMath.GetDirection(From, To);
            var projectedVolume = Owner.GetProjectedOccupancy(To, dir);
            
            if (GridManager.Instance.IsSpaceOccupied(projectedVolume, Owner))
            {
                Debug.LogWarning($"[Movement] Path blocked for {Owner.name} at step {StepIndex} ({To})! Cancelling path.");
                _timeline.CancelEvents(Owner);
                Owner.ResetActionState();
                return;
            }

            // Reserve destination immediately (without changing logical GridPosition yet).
            // This prevents other units from pathing/moving into the space during the visual step.
            _reservedVolume = projectedVolume;
            if (GridManager.Instance != null)
            {
                GridManager.Instance.RegisterReservation(Owner, projectedVolume);
            }

            // Visuals
            var mover = Owner.GetComponent<Visuals.UnitMovement>();
            if (mover != null)
            {
                Vector3 targetWorldPos = GridManager.Instance.GridToWorld(To);
                Debug.Log($"[MoveIntent] Starting visual move to world pos {targetWorldPos}");
                mover.MoveVisuals(targetWorldPos, Duration, null, Rotate, expectedLogicEnd: To);
            }
            else
            {
                Debug.LogWarning($"[MoveIntent] No UnitMovement component on {Owner?.name}! Visual movement will not occur.");
            }

            // Logical Update (Delayed)
            // Start the visual move now, then commit the logical grid position after the step duration.
            // If the move is interrupted, CancelEvents(unit) will remove this commit intent.
            if (_timeline != null)
            {
                var commit = new CommitMoveStepIntent(Owner, From, To, _reservedVolume != null ? new System.Collections.Generic.List<TrianglePoint>(_reservedVolume) : null);
                // IMPORTANT: Commit slightly before the next step's MoveIntent (which is scheduled at +Duration)
                // so that the subsequent visual step isn't canceled due to a same-timestamp logic update.
                const float epsilon = 0.0001f;
                float commitDelay = Mathf.Max(0f, Duration - epsilon);
                _timeline.Schedule(commitDelay, commit, $"Move Commit {StepIndex}", _groupId);
            }
        }

        public override void ExecuteInterruption(InteractionType interactionType)
        {
            base.ExecuteInterruption(interactionType);

            // Release reservation for this step (if any)
            if (GridManager.Instance != null && _reservedVolume != null)
            {
                GridManager.Instance.UnregisterReservation(Owner, _reservedVolume);
            }

            // Stop visuals and snap back to current logical position
            var mover = Owner != null ? Owner.GetComponent<Visuals.UnitMovement>() : null;
            if (mover != null)
            {
                mover.CancelVisualMoveAndSnapToLogic();
            }

            _timeline.CancelEvents(Owner);
            Owner.ResetActionState();
        }
    }
}
