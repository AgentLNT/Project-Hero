using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;

namespace ProjectHero.Core.Gameplay
{
    public class EnemyAIController : MonoBehaviour
    {
        [Header("Refs")]
        public BattleTimeline Timeline;
        public CombatUnit ControlledUnit;
        public CombatUnit TargetUnit;

        [Header("AI Personality")]
        public float MinActionInterval = 0.5f;
        public float MaxActionInterval = 1.2f;
        public float ReactionDelay = 0.3f;
        public float StaminaSafetyMargin = 20f;

        [Header("Debug")]
        public bool EnableDebugLogs = false;
        public string DebugState = "Init";

        private float _nextThinkTimeReal;
        private float _nextAvailableTickTime;
        private const float THINK_HZ = 0.1f;

        private void Awake()
        {
            if (ControlledUnit == null) ControlledUnit = GetComponent<CombatUnit>();
        }

        private void Start()
        {
            _nextThinkTimeReal = Time.unscaledTime + Random.Range(0f, 1f);
        }

        private void Update()
        {
            if (Timeline == null) Timeline = FindFirstObjectByType<BattleTimeline>();

            if (TargetUnit == null)
            {
                var units = FindObjectsByType<CombatUnit>(FindObjectsSortMode.None);
                foreach (var u in units)
                {
                    if (u.IsPlayerControlled && u != ControlledUnit)
                    {
                        TargetUnit = u;
                        break;
                    }
                }
            }

            if (ControlledUnit == null || TargetUnit == null)
            {
                DebugState = "No Target";
                return;
            }

            if (Timeline.Paused) return;

            if (Time.unscaledTime < _nextThinkTimeReal) return;
            _nextThinkTimeReal = Time.unscaledTime + THINK_HZ;

            if (Timeline.CurrentTime < _nextAvailableTickTime)
            {
                DebugState = "Cooling Down";
                return;
            }

            MakeDecision();
        }

        private void MakeDecision()
        {
            if (ControlledUnit.IsStaggered || ControlledUnit.IsKnockedDown)
            {
                if (!ControlledUnit.IsRecoveringAction) ScheduleRecovery();
                else DebugState = "Recovering";
                return;
            }

            if (ControlledUnit.IsActing)
            {
                DebugState = "Acting";
                return;
            }

            if (ControlledUnit.CurrentStamina < StaminaSafetyMargin || ControlledUnit.IsExhausted)
            {
                if (EnableDebugLogs) Debug.Log($"[AI] {name} Resting (Stamina).");
                DebugState = "Resting";
                _nextAvailableTickTime = Timeline.CurrentTime + Random.Range(1.0f, 1.5f);
                return;
            }

            var bestAttack = PickBestAttack();
            if (bestAttack != null)
            {
                DebugState = "Attacking";
                ScheduleAttack(bestAttack);
                return;
            }

            DebugState = "Thinking Move";
            ScheduleMovement();
        }

        private void ScheduleRecovery()
        {
            float reaction = Random.Range(0.1f, 0.3f);
            float duration = ActionScheduler.EstimateRecoverDuration();

            long groupId = Timeline.ReserveGroupId();
            ActionScheduler.ScheduleRecover(Timeline, ControlledUnit, reaction, staminaCost: 5f, duration: duration, groupId: groupId);

            _nextAvailableTickTime = Timeline.CurrentTime + reaction + duration + 0.1f;
        }

        private void ScheduleAttack(Action action)
        {
            float startDelay = ReactionDelay + Random.Range(0f, 0.1f);
            float duration = ActionScheduler.EstimateAttackDuration(ControlledUnit, action);
            var dir = GridMath.GetDirection(ControlledUnit.GridPosition, TargetUnit.GridPosition);

            long groupId = Timeline.ReserveGroupId();
            ActionScheduler.ScheduleAttack(Timeline, ControlledUnit, action, startDelay, targetDirection: dir, groupId: groupId);

            float cooldown = Random.Range(MinActionInterval, MaxActionInterval);
            _nextAvailableTickTime = Timeline.CurrentTime + startDelay + duration + cooldown;
        }

        private void ScheduleMovement()
        {
            var (found, dest, path) = FindBestPositionNearTarget(maxRings: 3);

            if (!found)
            {
                DebugState = "No Path Found";
                _nextAvailableTickTime = Timeline.CurrentTime + 0.5f;
                return;
            }

            if (path != null && path.Count <= 1)
            {
                DebugState = "At Position (Holding)";
                _nextAvailableTickTime = Timeline.CurrentTime + 0.3f;
                return;
            }

            DebugState = $"Moving to {dest}";
            float startDelay = ReactionDelay;
            float moveDuration = ActionScheduler.EstimateMoveDuration(ControlledUnit, path);

            long groupId = Timeline.ReserveGroupId();
            ActionScheduler.ScheduleMoveTo(Timeline, ControlledUnit, dest, startTime: startDelay, groupId: groupId);

            float cooldown = Random.Range(MinActionInterval * 0.5f, MaxActionInterval * 0.8f);
            _nextAvailableTickTime = Timeline.CurrentTime + startDelay + moveDuration + cooldown;
        }

        private Action PickBestAttack()
        {
            if (ControlledUnit.ActionLibrary == null) return null;
            var usable = new List<Action>();
            var dir = GridMath.GetDirection(ControlledUnit.GridPosition, TargetUnit.GridPosition);

            foreach (var entry in ControlledUnit.ActionLibrary.Actions)
            {
                var action = entry.Data;
                if (action.StaminaCost > ControlledUnit.CurrentStamina) continue;
                if (CanHitTarget(action, dir)) usable.Add(action);
            }

            if (usable.Count == 0) return null;
            return usable[Random.Range(0, usable.Count)];
        }

        private bool CanHitTarget(Action action, GridDirection dir)
        {
            if (action.Pattern == null) return false;
            var attackArea = action.Pattern.GetAffectedTriangles(ControlledUnit.GridPosition, dir);
            var targetOcc = TargetUnit.GetOccupiedTriangles();
            foreach (var tTri in targetOcc)
            {
                foreach (var aTri in attackArea)
                {
                    if (tTri.Equals(aTri)) return true;
                }
            }
            return false;
        }

        private (bool found, Pathfinder.GridPoint dest, List<Pathfinder.GridPoint> path) FindBestPositionNearTarget(int maxRings)
        {
            if (GridManager.Instance == null) return (false, default, null);

            var obstacles = GridManager.Instance.GetGlobalObstacles(ControlledUnit);
            var pathfinder = new Pathfinder();

            Pathfinder.GridPoint bestDest = default;
            List<Pathfinder.GridPoint> bestPath = null;
            float bestScore = float.MaxValue;
            bool anyFound = false;

            var visited = new HashSet<Pathfinder.GridPoint>();
            var queue = new Queue<(Pathfinder.GridPoint point, int depth)>();

            visited.Add(TargetUnit.GridPosition);
            queue.Enqueue((TargetUnit.GridPosition, 0));

            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();

                if (depth > maxRings) continue;

                if (depth > 0)
                {
                    var projectedVol = ControlledUnit.GetProjectedOccupancy(current, GridDirection.East);

                    if (!GridManager.Instance.IsSpaceOccupied(projectedVol, ControlledUnit))
                    {
                        var path = pathfinder.FindPath(ControlledUnit.GridPosition, current, ControlledUnit.UnitVolumeDefinition, obstacles);
                        if (path != null)
                        {
                            float score = path.Count;

                            if (score < bestScore)
                            {
                                bestScore = score;
                                bestDest = current;
                                bestPath = path;
                                anyFound = true;
                            }
                        }
                    }
                }

                for (int i = 0; i < 12; i++)
                {
                    var neighbor = GridMath.GetNeighbor(current, (GridDirection)i);
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue((neighbor, depth + 1));
                    }
                }
            }

            return (anyFound, bestDest, bestPath);
        }
    }
}
