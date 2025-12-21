using System.Collections.Generic;
using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Actions.Intents;

namespace ProjectHero.Core.Actions
{
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
            float speedFactor = 20f / Mathf.Max(1f, attacker.Swiftness);
            float impactDuration = action.BaseTime * speedFactor;
            float recoveryDuration = 0.5f;

            // 1. Windup - 传入时长
            var windupIntent = new StateChangeIntent(attacker, "Windup", impactDuration)
            {
                StaminaCost = action.StaminaCost,
                SetIsActing = true,
                ForceFacing = targetDirection
            };
            timeline.Schedule(startTime, windupIntent, $"{attacker.name} starts {action.Name}", groupId);

            float impactTime = startTime + impactDuration;

            var attackIntent = new AttackIntent(attacker, action, timeline);
            timeline.Schedule(impactTime, attackIntent, $"{attacker.name} hits", groupId);

            // 2. Recovery - 传入时长
            var startRecoveryIntent = new StateChangeIntent(attacker, "Recovery", recoveryDuration);
            timeline.Schedule(impactTime + 0.0001f, startRecoveryIntent, "Start Recovery", groupId);

            float endTime = impactTime + recoveryDuration;

            var recoveryIntent = new StateChangeIntent(attacker, "Idle");
            timeline.Schedule(endTime, recoveryIntent, $"{attacker.name} recovers", groupId);
        }

        public static void ScheduleAttackAtPoint(BattleTimeline timeline, CombatUnit attacker, Action action, float startTime, Pathfinder.GridPoint aimPoint, long groupId = 0)
        {
            float speedFactor = 20f / Mathf.Max(1f, attacker.Swiftness);
            float impactDuration = action.BaseTime * speedFactor;
            float recoveryDuration = 0.5f;

            var windupIntent = new StateChangeIntent(attacker, "Windup", impactDuration)
            {
                StaminaCost = action.StaminaCost,
                SetIsActing = true
            };
            timeline.Schedule(startTime, windupIntent, $"{attacker.name} starts {action.Name}", groupId);

            var face = new PlanFacingIntent(attacker, aimPoint);
            timeline.Schedule(startTime + 0.0001f, face, $"{attacker.name} aims", groupId);

            float impactTime = startTime + impactDuration;

            var attackIntent = new AttackIntent(attacker, action, timeline);
            timeline.Schedule(impactTime, attackIntent, $"{attacker.name} hits", groupId);

            var startRecoveryIntent = new StateChangeIntent(attacker, "Recovery", recoveryDuration);
            timeline.Schedule(impactTime + 0.0001f, startRecoveryIntent, "Start Recovery", groupId);

            float endTime = impactTime + recoveryDuration;
            var recoveryIntent = new StateChangeIntent(attacker, "Idle");
            timeline.Schedule(endTime, recoveryIntent, $"{attacker.name} recovers", groupId);
        }

        public static void ScheduleMove(BattleTimeline timeline, CombatUnit unit, List<Pathfinder.GridPoint> path, float startTime = 0f, long groupId = 0)
        {
            if (path == null || path.Count < 2) return;

            var startIntent = new StateChangeIntent(unit, "Busy") { SetIsActing = true };
            timeline.Schedule(startTime, startIntent, "Move Start", groupId);

            float accumulatedDelay = startTime;
            for (int i = 1; i < path.Count; i++)
            {
                var from = path[i - 1];
                var to = path[i];
                var moveDir = GridMath.GetDirection(from, to);
                float distance = ((int)moveDir % 2 != 0) ? 2.0f : 1.0f;
                float speed = Mathf.Max(1f, unit.Swiftness);
                float duration = Mathf.Clamp(distance / (speed * 0.1f), 0.2f, 4.0f);

                var moveIntent = new MoveIntent(unit, from, to, duration, i, timeline, groupId);
                timeline.Schedule(accumulatedDelay, moveIntent, $"Move Step {i}", groupId);
                accumulatedDelay += duration;
            }

            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(accumulatedDelay, endIntent, "Move End", groupId);
        }

        public static void ScheduleMoveTo(BattleTimeline timeline, CombatUnit unit, Pathfinder.GridPoint destination, float startTime = 0f, long groupId = 0)
        {
            var startIntent = new StateChangeIntent(unit, "Busy") { SetIsActing = true };
            timeline.Schedule(startTime, startIntent, "Move Start", groupId);
            var plan = new PlanMoveIntent(unit, destination, timeline, groupId);
            timeline.Schedule(startTime + 0.0001f, plan, "Plan Move", groupId);
        }

        public static void ScheduleDodge(BattleTimeline timeline, CombatUnit unit, float startTime, float duration = 0.5f, float focusCost = 1f, long groupId = 0)
        {
            var startIntent = new StateChangeIntent(unit, "Busy", duration) { SetIsActing = true };
            timeline.Schedule(startTime, startIntent, $"{unit.name} Dodge Window", groupId);

            float interval = 0.05f;
            int steps = Mathf.CeilToInt(duration / interval);

            for (int i = 0; i <= steps; i++)
            {
                float delay = i * interval;
                if (delay > duration) break;
                bool isFirst = (i == 0);
                var intent = new DodgeIntent(unit, isFirst ? focusCost : 0f, isFirst);
                timeline.Schedule(startTime + delay, intent, $"Dodge {delay:F2}s", groupId);
            }

            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(startTime + duration, endIntent, "Dodge End", groupId);
        }

        public static void ScheduleBlock(BattleTimeline timeline, CombatUnit unit, float startTime, float duration = 1.0f, float focusCost = 2f, long groupId = 0)
        {
            var startIntent = new StateChangeIntent(unit, "Busy", duration) { SetIsActing = true };
            timeline.Schedule(startTime, startIntent, $"{unit.name} Block Window", groupId);

            float interval = 0.05f;
            int steps = Mathf.CeilToInt(duration / interval);

            for (int i = 0; i <= steps; i++)
            {
                float delay = i * interval;
                if (delay > duration) break;
                bool isFirst = (i == 0);
                var intent = new BlockIntent(unit, isFirst ? focusCost : 0f, isFirst);
                timeline.Schedule(startTime + delay, intent, $"Block {delay:F2}s", groupId);
            }

            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(startTime + duration, endIntent, "Block End", groupId);
        }

        public static void ScheduleKnockback(BattleTimeline timeline, CombatUnit unit, GridDirection direction, int distance, float impactSpeed, long groupId = 0)
        {
            if (distance <= 0) return;
            if (!unit.IsRecoveringAction)
            {
                timeline.CancelEvents(unit);
                unit.ResetActionState();
            }

            var startIntent = new StateChangeIntent(unit, "Busy") { SetIsActing = true };
            timeline.Schedule(0f, startIntent, "Knockback Start", groupId);

            float accumulatedDelay = 0f;
            float stepDuration = Mathf.Clamp(10f / Mathf.Max(1f, impactSpeed), 0.05f, 0.5f);
            var currentPos = unit.GridPosition;

            for (int i = 0; i < distance; i++)
            {
                var nextPos = GridMath.GetNeighbor(currentPos, direction);
                var moveIntent = new MoveIntent(unit, currentPos, nextPos, stepDuration, i, timeline, groupId, rotate: false, isForced: true);
                timeline.Schedule(accumulatedDelay, moveIntent, $"Knockback Step {i}", groupId);
                accumulatedDelay += stepDuration;
                currentPos = nextPos;
            }

            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(accumulatedDelay, endIntent, "Knockback End", groupId);
        }

        public static float EstimateRecoverDuration() { return 0.35f; }

        public static void ScheduleRecover(BattleTimeline timeline, CombatUnit unit, float startTime, float staminaCost = 10f, float duration = 0.35f, long groupId = 0)
        {
            var startIntent = new StateChangeIntent(unit, "Busy", duration)
            {
                SetIsActing = true,
                StaminaCost = staminaCost
            };
            timeline.Schedule(startTime, startIntent, "Recover Start", groupId);

            var setFlag = new SetRecoveringFlagIntent(unit, true);
            timeline.Schedule(startTime + 0.0001f, setFlag, "Flag On", groupId);

            var recover = new RecoverIntent(unit);
            timeline.Schedule(startTime + duration, recover, "Recover", groupId);

            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(startTime + duration + 0.0001f, endIntent, "Recover End", groupId);
        }
    }
}
