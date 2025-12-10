using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Pathfinding;

namespace ProjectHero.Core.Actions
{
    public static class MovementAction
    {
        /// <summary>
        /// Schedules a movement action on the timeline.
        /// The visual movement happens over time.
        /// The logical grid position updates ONLY when the movement finishes.
        /// </summary>
        //public static void ScheduleMove(BattleTimeline timeline, CombatUnit unit, Pathfinder.GridPoint targetGridPos)
        //{
        //    if (GridManager.Instance == null)
        //    {
        //        Debug.LogError("GridManager instance not found!");
        //        return;
        //    }

        //    // 1. Calculate Duration based on Speed/Distance
        //    // Distance is roughly 1.0 for neighbors.
        //    // Formula: Time = Distance / Speed.
        //    // We use unit.Swiftness as a speed factor.
        //    // Adjust divisor (e.g., 5.0f) to tune game speed.
        //    float distance = 1.0f; 
        //    float speed = Mathf.Max(1f, unit.Swiftness); // Prevent divide by zero
        //    float duration = distance / (speed * 0.2f); // Example scaling
            
        //    // Clamp duration to keep it snappy
        //    duration = Mathf.Clamp(duration, 0.2f, 2.0f);

        //    // 2. Get Target World Position
        //    Vector3 targetWorldPos = GridManager.Instance.GridToWorld(targetGridPos);

        //    // 3. Define the Action
        //    System.Action moveAction = () => 
        //    {
        //        var mover = unit.GetComponent<Visuals.UnitMovement>();
        //        if (mover != null)
        //        {
        //            Debug.Log($"[Action] {unit.name} starts moving to {targetGridPos} (Duration: {duration:F2}s)");
                    
        //            mover.MoveVisuals(targetWorldPos, duration, () => 
        //            {
        //                // --- CRITICAL: Logic Update happens HERE ---
        //                unit.SetGridPosition(targetGridPos);
        //                Debug.Log($"[Action] {unit.name} logically arrived at {targetGridPos}");
        //            });
        //        }
        //        else
        //        {
        //            Debug.LogWarning($"Unit {unit.name} has no UnitMovement component!");
        //        }
        //    };

        //    // 4. Schedule it on the Timeline
        //    // '0' delay means it starts as soon as the timeline processes this event.
        //    timeline.ScheduleEvent(0f, $"Move {unit.name} to {targetGridPos}", moveAction, unit);
        //}

        /// <summary>
        /// Schedules a sequence of moves along a path.
        /// </summary>
        public static void SchedulePath(BattleTimeline timeline, CombatUnit unit, System.Collections.Generic.List<Pathfinder.GridPoint> path)
        {
            if (path == null || path.Count < 2) return;
            if (GridManager.Instance == null) return;

            // Start from index 1 because index 0 is the current position
            float accumulatedDelay = 0f;

            for (int i = 1; i < path.Count; i++)
            {
                var targetPoint = path[i];
                var previousPoint = path[i-1];
                
                // Calculate duration based on LOGICAL distance (User Request: 30-degree move = 2x cost)
                // Even direction (0, 60...) = 1.0 distance
                // Odd direction (30, 90...) = 2.0 distance
                var moveDir = GridMath.GetDirection(previousPoint, targetPoint);
                float distance = ((int)moveDir % 2 != 0) ? 2.0f : 1.0f;

                // Stamina Check & Consumption
                float staminaCost = distance * 5f; // Base cost per distance unit
                if (unit.CurrentStamina < staminaCost)
                {
                    Debug.LogWarning($"[Movement] {unit.name} is too exhausted to move further!");
                    break; // Stop path here
                }
                unit.CurrentStamina -= staminaCost;

                float speed = Mathf.Max(1f, unit.Swiftness);
                float stepDuration = distance / (speed * 0.2f);
                stepDuration = Mathf.Clamp(stepDuration, 0.2f, 4.0f);

                // Define timing points
                float startDelay = accumulatedDelay;
                float endDelay = accumulatedDelay + stepDuration;
                   
                Vector3 targetWorldPos = GridManager.Instance.GridToWorld(targetPoint);

                // 1. Start of Step (Visuals + Pre-check)
                System.Action startStepAction = () => 
                {
                    // --- Dynamic Obstacle Check ---
                    // Before starting the move, check if the target space is still free.
                    var dir = GridMath.GetDirection(previousPoint, targetPoint);
                    var projectedVolume = unit.GetProjectedOccupancy(targetPoint, dir);
                    
                    if (GridManager.Instance.IsSpaceOccupied(projectedVolume, unit))
                    {
                        Debug.LogWarning($"[Movement] Path blocked for {unit.name} at step {i} ({targetPoint})! Cancelling path.");
                        
                        // CRITICAL FIX: Immediately cancel all future events for this unit.
                        // This stops the unit dead in its tracks at the previous valid position.
                        timeline.CancelEvents(unit);
                        
                        // Reset state immediately since the "End" event is cancelled
                        unit.ResetActionState();

                        // Optional: Trigger "Bump" animation or Stagger here
                        return;
                    }
                    // ------------------------------

                    var mover = unit.GetComponent<Visuals.UnitMovement>();
                    if (mover != null)
                    {
                        // Pass null for onComplete because logic is handled separately
                        mover.MoveVisuals(targetWorldPos, stepDuration, null);
                    }
                };
                timeline.ScheduleEvent(startDelay, $"Start Move Step {i}", startStepAction, unit);

                // 2. End of Step (Logic Commit)
                System.Action endStepAction = () => 
                {
                    // --- Double Check (Race Condition Handling) ---
                    // Check if the target space was occupied DURING the movement animation.
                    var dir = GridMath.GetDirection(previousPoint, targetPoint);
                    var projectedVolume = unit.GetProjectedOccupancy(targetPoint, dir);

                    if (GridManager.Instance.IsSpaceOccupied(projectedVolume, unit))
                    {
                        Debug.LogWarning($"[Collision] {unit.name} crashed into something at {targetPoint} during movement!");
                        
                        // Visual Correction: Snap back to the previous valid position
                        // Since we haven't updated GridPosition yet, the unit is logically still at 'previousPoint'.
                        // We just need to reset the visual transform.
                        Vector3 prevWorldPos = GridManager.Instance.GridToWorld(previousPoint);
                        var mover = unit.GetComponent<Visuals.UnitMovement>();
                        if (mover != null)
                        {
                            // Force stop any tweens and snap
                            mover.transform.position = GridManager.GetGroundPosition(prevWorldPos);
                        }

                        // Cancel all future steps
                        timeline.CancelEvents(unit);
                        
                        // Reset state immediately
                        unit.ResetActionState();
                        return;
                    }
                    // ----------------------------------------------

                    // Update Position and Facing Atomically
                    // This ensures the Occupancy Map is updated with the correct rotation
                    unit.SetGridPositionAndFacing(targetPoint, dir);

                    Debug.Log($"[Logic] {unit.name} arrived at {targetPoint} facing {dir}");
                };
                // Use High Priority for Movement Logic to ensure position updates BEFORE attacks
                timeline.ScheduleEvent(endDelay, $"End Move Step {i}", endStepAction, unit, TimelinePriority.Movement);

                // Add this step's duration to the delay for the NEXT step
                accumulatedDelay += stepDuration;
            }

            // Final Event: Reset Action State
            timeline.ScheduleEvent(accumulatedDelay, $"Movement Complete for {unit.name}", () => 
            {
                unit.ResetActionState();
                Debug.Log($"[Action] {unit.name} finished moving.");
            }, unit);
        }
    }
}
