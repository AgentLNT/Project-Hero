using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Physics;
using ProjectHero.Core.Grid;

namespace ProjectHero.Core.Actions
{
    /// <summary>
    /// Helper class to schedule CombatActions on the Timeline.
    /// Similar to MovementAction, but for attacks.
    /// </summary>
    public static class AttackAction
    {
        public static void ScheduleAttack(BattleTimeline timeline, CombatUnit attacker, CombatUnit target, Action action, float startTime)
        {
            // 1. Schedule the "Start" (Windup)
            timeline.ScheduleEvent(startTime, $"{attacker.name} starts {action.Name}", () => 
            {
                Debug.Log($"[Action] {attacker.name} begins {action.Name} (Windup: {action.BaseTime}s)");
                // Trigger animation trigger here in the future
            }, attacker);

            // 2. Schedule the "Impact"
            // The delay is exactly the action's BaseTime
            float impactTime = startTime + action.BaseTime;

            // Use Attack Priority (Low) so it happens AFTER movement updates
            timeline.ScheduleEvent(impactTime, $"{attacker.name} hits with {action.Name}", () => 
            {
                // Check Pattern Intersection if pattern exists
                bool hit = true;
                if (action.Pattern != null)
                {
                    var attackArea = action.Pattern.GetAffectedTriangles(attacker.GridPosition, attacker.FacingDirection);
                    var targetArea = target.GetOccupiedTriangles();
                    
                    // Use the centralized Physics Engine for intersection check
                    hit = PhysicsEngine.CheckIntersection(attackArea, targetArea);
                }

                if (hit)
                {
                    // Perform the physics calculation
                    PhysicsEngine.ResolveCollision(attacker, target, action);

                    // Automatic Interruption Logic
                    // If the attack successfully staggered or knocked down the target, cancel their pending actions.
                    if (target.IsStaggered || target.IsKnockedDown)
                    {
                        Debug.Log($"[Tactics] {target.name} was interrupted! Cancelling pending events.");
                        timeline.CancelEvents(target);
                    }
                }
                else
                {
                    Debug.Log($"[Combat] {attacker.name} missed {target.name} (Target moved out of pattern)");
                }

            }, attacker, TimelinePriority.Attack);
        }
    }
}
