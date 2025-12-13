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

        public MoveIntent(CombatUnit owner, Pathfinder.GridPoint from, Pathfinder.GridPoint to, float duration, int index, BattleTimeline timeline, bool rotate = true, bool isForced = false) 
            : base(owner, ActionType.Move)
        {
            From = from;
            To = to;
            Duration = duration;
            StepIndex = index;
            _timeline = timeline;
            Rotate = rotate;
            IsForced = isForced;
        }

        public override void ExecuteSuccess()
        {
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

            // Visuals
            var mover = Owner.GetComponent<Visuals.UnitMovement>();
            if (mover != null)
            {
                Vector3 targetWorldPos = GridManager.Instance.GridToWorld(To);
                mover.MoveVisuals(targetWorldPos, Duration, null, Rotate);
            }

            // Logical Update
            // We update the grid position immediately at the start of the step.
            // This ensures the tile is claimed and prevents other units from moving there.
            // The Arbiter handles "Intercept" logic if an attack hits this destination in the same frame.
            Owner.SetGridPosition(To);
        }

        public override void ExecuteInterruption(InteractionType interactionType)
        {
            base.ExecuteInterruption(interactionType);
            _timeline.CancelEvents(Owner);
            Owner.ResetActionState();
        }
    }
}
