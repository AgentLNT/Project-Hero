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
        public static void ScheduleMove(BattleTimeline timeline, CombatUnit unit, Pathfinder.GridPoint targetGridPos)
        {
            if (GridManager.Instance == null)
            {
                Debug.LogError("GridManager instance not found!");
                return;
            }

            // 1. Calculate Duration based on Speed/Distance
            // Distance is roughly 1.0 for neighbors.
            // Formula: Time = Distance / Speed.
            // We use unit.Swiftness as a speed factor.
            // Adjust divisor (e.g., 5.0f) to tune game speed.
            float distance = 1.0f; 
            float speed = Mathf.Max(1f, unit.Swiftness); // Prevent divide by zero
            float duration = distance / (speed * 0.2f); // Example scaling
            
            // Clamp duration to keep it snappy
            duration = Mathf.Clamp(duration, 0.2f, 2.0f);

            // 2. Get Target World Position
            Vector3 targetWorldPos = GridManager.Instance.GridToWorld(targetGridPos);

            // 3. Define the Action
            System.Action moveAction = () => 
            {
                var mover = unit.GetComponent<Visuals.UnitMovement>();
                if (mover != null)
                {
                    Debug.Log($"[Action] {unit.name} starts moving to {targetGridPos} (Duration: {duration:F2}s)");
                    
                    mover.MoveVisuals(targetWorldPos, duration, () => 
                    {
                        // --- CRITICAL: Logic Update happens HERE ---
                        unit.SetGridPosition(targetGridPos);
                        Debug.Log($"[Action] {unit.name} logically arrived at {targetGridPos}");
                    });
                }
                else
                {
                    Debug.LogWarning($"Unit {unit.name} has no UnitMovement component!");
                }
            };

            // 4. Schedule it on the Timeline
            // '0' delay means it starts as soon as the timeline processes this event.
            timeline.ScheduleEvent(0f, $"Move {unit.name} to {targetGridPos}", moveAction, unit);
        }
    }
}
