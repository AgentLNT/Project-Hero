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
        private static int SecToTick(float seconds) => Mathf.Max(1, Mathf.RoundToInt(seconds * BattleTimeline.TicksPerSecond));
        private static float TickToSec(int ticks) => ticks * BattleTimeline.SecondsPerTick;

        public static float EstimateAttackDuration(CombatUnit attacker, Action action)
        {
            float speedFactor = 20f / Mathf.Max(1f, attacker.Swiftness);
            return action.BaseTime * speedFactor + 0.5f; // Impact + Recovery
        }

        public static float EstimateMoveDuration(CombatUnit unit, List<Pathfinder.GridPoint> path)
        {
            if (path == null || path.Count < 2) return 0f;
            float total = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                var moveDir = GridMath.GetDirection(path[i - 1], path[i]);
                float distance = ((int)moveDir % 2 != 0) ? 2.0f : 1.0f;
                float speed = Mathf.Max(1f, unit.Swiftness);
                total += Mathf.Clamp(distance / (speed * 0.1f), 0.2f, 4.0f);
            }
            return total;
        }

        public static void ScheduleAttack(BattleTimeline timeline, CombatUnit attacker, Action action, float startTime, GridDirection? targetDirection = null, long groupId = 0)
        {
            float speedFactor = 20f / Mathf.Max(1f, attacker.Swiftness);
            float impactDurationRaw = action.BaseTime * speedFactor;
            float recoveryDurationRaw = 0.5f;

            var windupIntent = new StateChangeIntent(attacker, "Windup", impactDurationRaw)
            {
                StaminaCost = action.StaminaCost,
                SetIsActing = true,
                ForceFacing = targetDirection
            };
            timeline.Schedule(startTime, windupIntent, $"{attacker.name} starts {action.Name}", groupId, TimelinePriority.State);

            float impactTime = startTime + impactDurationRaw;

            // ¹¥»÷ÅÐ¶¨ (Priority 0)
            var attackIntent = new AttackIntent(attacker, action, timeline);
            timeline.Schedule(impactTime, attackIntent, $"{attacker.name} hits", groupId, TimelinePriority.Attack);

            // ×´Ì¬ÇÐ»» (Priority 50)£¬±£Ö¤Í¬Ò»Ö¡ÏÈÇÐ»»×´Ì¬
            var startRecoveryIntent = new StateChangeIntent(attacker, "Recovery", recoveryDurationRaw);
            timeline.Schedule(impactTime, startRecoveryIntent, "Start Recovery", groupId, TimelinePriority.State);

            float endTime = impactTime + recoveryDurationRaw;
            var recoveryIntent = new StateChangeIntent(attacker, "Idle");
            timeline.Schedule(endTime, recoveryIntent, $"{attacker.name} recovers", groupId, TimelinePriority.State);
        }

        public static void ScheduleMove(BattleTimeline timeline, CombatUnit unit, List<Pathfinder.GridPoint> path, float startTime = 0f, long groupId = 0)
        {
            if (path == null || path.Count < 2) return;

            var startIntent = new StateChangeIntent(unit, "Busy") { SetIsActing = true };
            timeline.Schedule(startTime, startIntent, "Move Start", groupId, TimelinePriority.State);

            float accumulatedDelay = startTime;
            for (int i = 1; i < path.Count; i++)
            {
                var from = path[i - 1];
                var to = path[i];
                var moveDir = GridMath.GetDirection(from, to);
                float distance = ((int)moveDir % 2 != 0) ? 2.0f : 1.0f;
                float speed = Mathf.Max(1f, unit.Swiftness);
                float stepDuration = Mathf.Clamp(distance / (speed * 0.1f), 0.2f, 4.0f);

                var moveIntent = new MoveIntent(unit, from, to, stepDuration, i, timeline, groupId);
                timeline.Schedule(accumulatedDelay, moveIntent, $"Move Step {i}", groupId, TimelinePriority.State);

                accumulatedDelay += stepDuration;
            }

            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(accumulatedDelay, endIntent, "Move End", groupId, TimelinePriority.State);
        }

        public static void ScheduleMoveTo(BattleTimeline timeline, CombatUnit unit, Pathfinder.GridPoint destination, float startTime = 0f, long groupId = 0)
        {
            var startIntent = new StateChangeIntent(unit, "Busy") { SetIsActing = true };
            timeline.Schedule(startTime, startIntent, "Move Start", groupId, TimelinePriority.State);

            var plan = new PlanMoveIntent(unit, destination, timeline, groupId);
            timeline.Schedule(startTime, plan, "Plan Move", groupId, TimelinePriority.State - 1);
        }

        public static void ScheduleDodge(BattleTimeline timeline, CombatUnit unit, float startTime, float duration = 0.5f, float focusCost = 1f, long groupId = 0)
        {
            var startIntent = new StateChangeIntent(unit, "Busy", duration) { SetIsActing = true };
            timeline.Schedule(startTime, startIntent, "Dodge Window", groupId, TimelinePriority.State);

            int totalTicks = SecToTick(duration);
            for (int i = 0; i < totalTicks; i++)
            {
                float delay = TickToSec(i);
                bool isFirst = (i == 0);
                var intent = new DodgeIntent(unit, isFirst ? focusCost : 0f, isFirst);
                // Priority.Defense > Attack
                timeline.Schedule(startTime + delay, intent, $"Dodge Tick {i}", groupId, TimelinePriority.Defense);
            }

            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(startTime + duration, endIntent, "Dodge End", groupId, TimelinePriority.State);
        }

        public static void ScheduleBlock(BattleTimeline timeline, CombatUnit unit, float startTime, float duration = 1.0f, float focusCost = 2f, long groupId = 0)
        {
            var startIntent = new StateChangeIntent(unit, "Busy", duration) { SetIsActing = true };
            timeline.Schedule(startTime, startIntent, "Block Window", groupId, TimelinePriority.State);

            int totalTicks = SecToTick(duration);
            for (int i = 0; i < totalTicks; i++)
            {
                float delay = TickToSec(i);
                bool isFirst = (i == 0);
                var intent = new BlockIntent(unit, isFirst ? focusCost : 0f, isFirst);
                timeline.Schedule(startTime + delay, intent, $"Block Tick {i}", groupId, TimelinePriority.Defense);
            }

            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(startTime + duration, endIntent, "Block End", groupId, TimelinePriority.State);
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
            timeline.Schedule(0f, startIntent, "Knockback Start", groupId, TimelinePriority.System);

            float accumulatedDelay = 0f;
            float stepDuration = Mathf.Clamp(10f / Mathf.Max(1f, impactSpeed), 0.05f, 0.5f);
            var currentPos = unit.GridPosition;

            for (int i = 0; i < distance; i++)
            {
                var nextPos = GridMath.GetNeighbor(currentPos, direction);
                var moveIntent = new MoveIntent(unit, currentPos, nextPos, stepDuration, i, timeline, groupId, rotate: false, isForced: true);
                timeline.Schedule(accumulatedDelay, moveIntent, $"KB Step {i}", groupId, TimelinePriority.State);
                accumulatedDelay += stepDuration;
                currentPos = nextPos;
            }

            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(accumulatedDelay, endIntent, "Knockback End", groupId, TimelinePriority.State);
        }

        public static float EstimateRecoverDuration() { return 0.35f; }

        public static void ScheduleRecover(BattleTimeline timeline, CombatUnit unit, float startTime, float staminaCost = 10f, float duration = 0.35f, long groupId = 0)
        {
            var startIntent = new StateChangeIntent(unit, "Busy", duration)
            {
                SetIsActing = true,
                StaminaCost = staminaCost
            };
            timeline.Schedule(startTime, startIntent, "Recover Start", groupId, TimelinePriority.State);

            var setFlag = new SetRecoveringFlagIntent(unit, true);
            timeline.Schedule(startTime, setFlag, "Flag On", groupId, TimelinePriority.System);

            var recover = new RecoverIntent(unit);
            timeline.Schedule(startTime + duration, recover, "Recover", groupId, TimelinePriority.State);

            var endIntent = new StateChangeIntent(unit, "Idle");
            timeline.Schedule(startTime + duration, endIntent, "Recover End", groupId, TimelinePriority.Cleanup);
        }
    }
}
