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
        // Updated: Added 'targetDirection' to lock the attack direction at the start.
        // If targetDirection is null, it uses the attacker's current facing.
        public static void ScheduleAttack(BattleTimeline timeline, CombatUnit attacker, Action action, float startTime, GridDirection? targetDirection = null)
        {
            // Calculate Recovery Time (e.g., 50% of BaseTime or defined in Action)
            // For now, let's assume Recovery is 0.5s fixed or part of the action data.
            float recoveryDuration = 0.5f; 

            // 1. Schedule the "Start" (Windup)
            timeline.ScheduleEvent(startTime, $"{attacker.name} starts {action.Name}", () => 
            {
                if (!attacker.CanAct) 
                {
                    Debug.LogWarning($"[Action] {attacker.name} cannot act (Stunned/Busy). Attack cancelled.");
                    return;
                }

                // Set State Flags
                attacker.IsActing = true;
                attacker.InWindup = true;

                // Lock Facing Direction if provided
                if (targetDirection.HasValue)
                {
                    attacker.FacingDirection = targetDirection.Value;
                    // TODO: Update Visual Rotation immediately
                }

                Debug.Log($"[Action] {attacker.name} begins {action.Name} (Windup: {action.BaseTime}s)");
                // Trigger animation trigger here in the future
            }, attacker);

            // 2. Schedule the "Impact"
            // The delay is exactly the action's BaseTime
            float impactTime = startTime + action.BaseTime;

            // Use Attack Priority (Low) so it happens AFTER movement updates
            timeline.ScheduleEvent(impactTime, $"{attacker.name} hits with {action.Name}", () => 
            {
                // Check if attack was interrupted (e.g. Staggered during windup)
                if (!attacker.InWindup) return; 

                // Transition State: Windup -> Recovery
                attacker.InWindup = false;
                attacker.InRecovery = true;

                // 1. Determine Attack Area
                // Uses the attacker's CURRENT position and facing at the moment of impact.
                if (action.Pattern == null)
                {
                    Debug.LogWarning($"[Combat] Action {action.Name} has no AttackPattern!");
                    return;
                }

                var attackArea = action.Pattern.GetAffectedTriangles(attacker.GridPosition, attacker.FacingDirection);

                // 2. Find Targets in Area
                // Query GridManager for all units standing in the blast zone.
                var targets = GridManager.Instance.GetUnitsInArea(attackArea, attacker);

                if (targets.Count > 0)
                {
                    Debug.Log($"[Combat] {attacker.name} hit {targets.Count} targets!");

                    foreach (var target in targets)
                    {
                        // 3. Apply Effects to EACH target
                        PhysicsEngine.ResolveCollision(attacker, target, action);

                        // Automatic Interruption Logic
                        if (target.IsStaggered || target.IsKnockedDown)
                        {
                            Debug.Log($"[Tactics] {target.name} was interrupted! Cancelling pending events.");
                            timeline.CancelEvents(target);
                        }
                    }
                }
                else
                {
                    Debug.Log($"[Combat] {attacker.name} missed (No targets in area)");
                }

            }, attacker, TimelinePriority.Attack);

            // 3. Schedule the "End" (Recovery Finish)
            float endTime = impactTime + recoveryDuration;
            timeline.ScheduleEvent(endTime, $"{attacker.name} finishes {action.Name}", () => 
            {
                // Reset State Flags
                attacker.InRecovery = false;
                attacker.IsActing = false;
                Debug.Log($"[Action] {attacker.name} recovered from {action.Name}");
            }, attacker);
        }
    }
}
