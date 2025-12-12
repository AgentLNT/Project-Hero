using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Interactions; // Added

namespace ProjectHero.Core.Actions
{
    public static class MovementAction
    {
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

                // 1. Start of Step (Intent Registration + Visuals)
                System.Action startStepAction = () => 
                {
                    // Create Move Intent
                    var intent = new CombatIntent(unit, ActionType.Move, targetPoint)
                    {
                        OnSuccess = () => 
                        {
                            // --- Dynamic Obstacle Check ---
                            // Before starting the move, check if the target space is still free.
                            var dir = GridMath.GetDirection(previousPoint, targetPoint);
                            var projectedVolume = unit.GetProjectedOccupancy(targetPoint, dir);
                            
                            if (GridManager.Instance.IsSpaceOccupied(projectedVolume, unit))
                            {
                                Debug.LogWarning($"[Movement] Path blocked for {unit.name} at step {i} ({targetPoint})! Cancelling path.");
                                timeline.CancelEvents(unit);
                                unit.ResetActionState();
                                return;
                            }

                            var mover = unit.GetComponent<Visuals.UnitMovement>();
                            if (mover != null)
                            {
                                mover.MoveVisuals(targetWorldPos, stepDuration, null);
                            }
                        },
                        OnInterrupted = (type) =>
                        {
                            Debug.Log($"[Movement] {unit.name} interrupted by {type} at start of step {i}");
                            timeline.CancelEvents(unit);
                            unit.ResetActionState();
                        }
                    };

                    // Register Intent
                    timeline.RegisterIntent(intent);
                };
                timeline.ScheduleEvent(startDelay, $"Start Move Step {i}", startStepAction, unit, 0, false, ActionType.Move);

                // 2. End of Step (Logic Commit)
                // Note: We don't necessarily need an Intent for "Arriving" unless we want "Traps" to trigger.
                // For now, we keep it as a logic update event.
                System.Action endStepAction = () => 
                {
                    // --- Double Check (Race Condition Handling) ---
                    var dir = GridMath.GetDirection(previousPoint, targetPoint);
                    var projectedVolume = unit.GetProjectedOccupancy(targetPoint, dir);

                    if (GridManager.Instance.IsSpaceOccupied(projectedVolume, unit))
                    {
                        Debug.LogWarning($"[Collision] {unit.name} crashed into something at {targetPoint} during movement!");
                        
                        Vector3 prevWorldPos = GridManager.Instance.GridToWorld(previousPoint);
                        var mover = unit.GetComponent<Visuals.UnitMovement>();
                        if (mover != null)
                        {
                            mover.transform.position = GridManager.GetGroundPosition(prevWorldPos);
                        }

                        timeline.CancelEvents(unit);
                        unit.ResetActionState();
                        return;
                    }

                    // Update Position and Facing Atomically
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
