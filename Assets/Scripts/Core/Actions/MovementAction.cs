using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Visuals;

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

            // Start from index 1 because index 0 is the current position
            float accumulatedDelay = 0f;

            for (int i = 1; i < path.Count; i++)
            {
                var targetPoint = path[i];
                
                // Calculate duration for this step (same logic as single move)
                float distance = 1.0f; 
                float speed = Mathf.Max(1f, unit.Swiftness);
                float stepDuration = distance / (speed * 0.2f);
                stepDuration = Mathf.Clamp(stepDuration, 0.2f, 2.0f);

                // Define timing points
                float startDelay = accumulatedDelay;
                float endDelay = accumulatedDelay + stepDuration;
                   
                Vector3 targetWorldPos = GridManager.Instance.GridToWorld(targetPoint);

                // 1. Visual Event (Starts at beginning of step)
                System.Action visualAction = () => 
                {
                    var mover = unit.GetComponent<Visuals.UnitMovement>();
                    if (mover != null)
                    {
                        // Pass null for onComplete because logic is handled separately
                        mover.MoveVisuals(targetWorldPos, stepDuration, null);
                    }
                };
                timeline.ScheduleEvent(startDelay, $"Visual Move Step {i}", visualAction, unit);

                // 2. Logic Event (Happens exactly at end of step)
                System.Action logicAction = () => 
                {
                    unit.SetGridPosition(targetPoint);
                    Debug.Log($"[Logic] {unit.name} arrived at {targetPoint}");
                };
                // Use High Priority for Movement Logic to ensure position updates BEFORE attacks
                timeline.ScheduleEvent(endDelay, $"Logic Arrive Step {i}", logicAction, unit, TimelinePriority.Movement);

                // Add this step's duration to the delay for the NEXT step
                accumulatedDelay += stepDuration;
            }
        }
    }
}
