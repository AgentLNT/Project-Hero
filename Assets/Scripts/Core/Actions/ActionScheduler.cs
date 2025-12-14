using System.Collections.Generic;
using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Actions.Intents;

namespace ProjectHero.Core.Actions
{
    /// <summary>
    /// Factory class to generate and schedule CombatIntents.
    /// Replaces the old static Action classes.
    /// </summary>
    public static class ActionScheduler
    {
        public static float EstimateAttackDuration(CombatUnit attacker, Action action)
        {
            float speedFactor = 20f / Mathf.Max(1f, attacker.Swiftness);
            float impactDuration = action.BaseTime * speedFactor;
            float recoveryDuration = 0.5f;
            return impactDuration + recoveryDuration;
        }

        public static float EstimateMoveDuration(CombatUnit unit, List<Pathfinder.GridPoint> path)
        {
            if (path == null || path.Count < 2) return 0f;

            float total = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                var from = path[i - 1];
                var to = path[i];

                var moveDir = GridMath.GetDirection(from, to);
                float distance = ((int)moveDir % 2 != 0) ? 2.0f : 1.0f;
                float speed = Mathf.Max(1f, unit.Swiftness);
                float duration = Mathf.Clamp(distance / (speed * 0.1f), 0.2f, 4.0f);

                total += duration;
            }
            return total;
        }

        public static void ScheduleAttack(BattleTimeline timeline, CombatUnit attacker, Action action, float startTime, GridDirection? targetDirection = null, long groupId = 0)
        {
            // 1. Windup (Start)
            var windupIntent = new StateChangeIntent(attacker, "Windup")
            {
                StaminaCost = action.StaminaCost,
                SetIsActing = true,
                ForceFacing = targetDirection
            };
            timeline.Schedule(startTime, windupIntent, $"{attacker.name} starts {action.Name}", groupId);

            // 2. Impact (Attack)
            float speedFactor = 20f / Mathf.Max(1f, attacker.Swiftness);
            float impactTime = startTime + (action.BaseTime * speedFactor);

            var attackIntent = new AttackIntent(attacker, action, timeline);
            timeline.Schedule(impactTime, attackIntent, $"{attacker.name} hits with {action.Name}", groupId);

            // 3. Recovery (End)
            float recoveryDuration = 0.5f; // Could be in Action definition
            float endTime = impactTime + recoveryDuration;

            var recoveryIntent = new StateChangeIntent(attacker, "Idle"); // Idle resets state
            timeline.Schedule(endTime, recoveryIntent, $"{attacker.name} recovers", groupId);
        }

        /// <summary>
        /// Attack scheduling that computes facing at execution time based on an aim point.
        /// Useful when earlier planned moves may change the attacker's start position.
        /// </summary>
        public static void ScheduleAttackAtPoint(BattleTimeline timeline, CombatUnit attacker, Action action, float startTime, Pathfinder.GridPoint aimPoint, long groupId = 0)
        {
            var windupIntent = new StateChangeIntent(attacker, "Windup")
            {
                StaminaCost = action.StaminaCost,
                SetIsActing = true,
                ForceFacing = null
            };
            timeline.Schedule(startTime, windupIntent, $"{attacker.name} starts {action.Name}", groupId);

            // Face dynamically, using current position at that time.
            var face = new PlanFacingIntent(attacker, aimPoint);
            timeline.Schedule(startTime + 0.0001f, face, $"{attacker.name} aims", groupId);

            float speedFactor = 20f / Mathf.Max(1f, attacker.Swiftness);
            float impactTime = startTime + (action.BaseTime * speedFactor);

            var attackIntent = new AttackIntent(attacker, action, timeline);
            timeline.Schedule(impactTime, attackIntent, $"{attacker.name} hits with {action.Name}", groupId);

            float recoveryDuration = 0.5f;
            float endTime = impactTime + recoveryDuration;
            var recoveryIntent = new StateChangeIntent(attacker, "Idle");
            timeline.Schedule(endTime, recoveryIntent, $"{attacker.name} recovers", groupId);
        }

        public static void ScheduleMove(BattleTimeline timeline, CombatUnit unit, List<Pathfinder.GridPoint> path, float startTime = 0f, long groupId = 0)
        {
            if (path == null || path.Count < 2) return;

            // Mark busy at the scheduled start time (not at planning time)
            var startIntent = new StateChangeIntent(unit, "Busy")
            {
                SetIsActing = true
            };
            timeline.Schedule(startTime, startIntent, "Move Start", groupId);

            float accumulatedDelay = startTime;

            for (int i = 1; i < path.Count; i++)
            {
                var from = path[i-1];
                var to = path[i];
                
                // Calculate Duration
                // User Feedback: Movement was too fast. Adjusted formula.
                // Old: speed * 0.2f (20 speed -> 0.25s)
                // New: speed * 0.1f (20 speed -> 0.50s)
                var moveDir = GridMath.GetDirection(from, to);
                float distance = ((int)moveDir % 2 != 0) ? 2.0f : 1.0f;
                float speed = Mathf.Max(1f, unit.Swiftness);
                float duration = Mathf.Clamp(distance / (speed * 0.1f), 0.2f, 4.0f);

                // Stamina (Pre-check, though execution will also check if we added logic there)
                // For now, we assume stamina is checked or consumed per step.
                // Let's add stamina cost to the intent if needed, but MoveIntent logic currently doesn't consume it.
                // We can add it later.

                // Schedule Step
                var moveIntent = new MoveIntent(unit, from, to, duration, i, timeline);
                timeline.Schedule(accumulatedDelay, moveIntent, $"Move Step {i}", groupId);

                accumulatedDelay += duration;
            }

            // End of Move Sequence
            // We must reset the unit's Acting state so it can act again.
            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(accumulatedDelay, endIntent, "Move End", groupId);
        }

        /// <summary>
        /// Schedules a move that computes its path at execution time based on the unit's then-current position.
        /// This makes chained timeline actions reference the correct predicted state.
        /// </summary>
        public static void ScheduleMoveTo(BattleTimeline timeline, CombatUnit unit, Pathfinder.GridPoint destination, float startTime = 0f, long groupId = 0)
        {
            var startIntent = new StateChangeIntent(unit, "Busy")
            {
                SetIsActing = true
            };
            timeline.Schedule(startTime, startIntent, "Move Start", groupId);

            var plan = new PlanMoveIntent(unit, destination, timeline, groupId);
            timeline.Schedule(startTime + 0.0001f, plan, "Plan Move", groupId);
        }

        public static void ScheduleDodge(BattleTimeline timeline, CombatUnit unit, float startTime, float duration = 0.5f, float focusCost = 1f, long groupId = 0)
        {
            // Mark busy at the scheduled start time
            var startIntent = new StateChangeIntent(unit, "Busy")
            {
                SetIsActing = true
            };
            timeline.Schedule(startTime, startIntent, $"{unit.name} Dodge Window Start", groupId);

            // Interaction Window Implementation:
            // Submit a Dodge request every cycle (approx every 0.05s) for the duration.
            float interval = 0.05f;
            int steps = Mathf.CeilToInt(duration / interval);

            for (int i = 0; i <= steps; i++)
            {
                float delay = i * interval;
                if (delay > duration) break;

                // First intent consumes Focus, subsequent ones are free (same window)
                bool isFirst = (i == 0);
                var intent = new DodgeIntent(unit, isFirst ? focusCost : 0f, isFirst);
                timeline.Schedule(startTime + delay, intent, $"{unit.name} Dodges (Window {delay:F2}s)", groupId);
            }

            // Schedule window end - reset IsActing state
            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(startTime + duration, endIntent, $"{unit.name} Dodge Window End", groupId);
        }

        public static void ScheduleBlock(BattleTimeline timeline, CombatUnit unit, float startTime, float duration = 1.0f, float focusCost = 2f, long groupId = 0)
        {
            // Mark busy at the scheduled start time
            var startIntent = new StateChangeIntent(unit, "Busy")
            {
                SetIsActing = true
            };
            timeline.Schedule(startTime, startIntent, $"{unit.name} Block Window Start", groupId);

            // Interaction Window Implementation:
            // Submit a Block request every cycle (approx every 0.05s) for the duration.
            float interval = 0.05f;
            int steps = Mathf.CeilToInt(duration / interval);

            for (int i = 0; i <= steps; i++)
            {
                float delay = i * interval;
                if (delay > duration) break;

                // First intent consumes Focus, subsequent ones are free (same window)
                bool isFirst = (i == 0);
                var intent = new BlockIntent(unit, isFirst ? focusCost : 0f, isFirst);
                timeline.Schedule(startTime + delay, intent, $"{unit.name} Blocks (Window {delay:F2}s)", groupId);
            }

            // Schedule window end - reset IsActing state
            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(startTime + duration, endIntent, $"{unit.name} Block Window End", groupId);
        }

        public static void ScheduleKnockback(BattleTimeline timeline, CombatUnit unit, GridDirection direction, int distance, float impactSpeed, long groupId = 0)
        {
            if (distance <= 0) return;

            // 1. Interrupt Existing Actions
            timeline.CancelEvents(unit);
            unit.ResetActionState();
            // Do not lock immediately; lock at scheduled start.
            var startIntent = new StateChangeIntent(unit, "Busy")
            {
                SetIsActing = true
            };
            timeline.Schedule(0f, startIntent, "Knockback Start", groupId);

            // 2. Calculate Path & Schedule Steps
            float accumulatedDelay = 0f;
            
            // Calculate Duration based on Impact Speed (v_impact)
            // Formula: Time = Distance / Velocity
            // Velocity (tiles/s) = impactSpeed * 0.1f (Consistent with Move Logic)
            // Duration per tile = 1 / (impactSpeed * 0.1f) = 10 / impactSpeed
            float stepDuration = Mathf.Clamp(10f / Mathf.Max(1f, impactSpeed), 0.05f, 0.5f);
            
            var currentPos = unit.GridPosition;

            for (int i = 0; i < distance; i++)
            {
                var nextPos = GridMath.GetNeighbor(currentPos, direction);
                
                // Schedule Move (No Rotation, Forced)
                // Note: MoveIntent checks for obstacles. If blocked, it cancels subsequent steps.
                var moveIntent = new MoveIntent(unit, currentPos, nextPos, stepDuration, i, timeline, rotate: false, isForced: true);
                timeline.Schedule(accumulatedDelay, moveIntent, $"Knockback Step {i}", groupId);

                accumulatedDelay += stepDuration;
                currentPos = nextPos;
            }

            // 3. End State
            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(accumulatedDelay, endIntent, "Knockback End", groupId);
        }
    }
}
