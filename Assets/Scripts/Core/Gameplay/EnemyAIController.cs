using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;

namespace ProjectHero.Core.Gameplay
{
    /// <summary>
    /// Very simple enemy AI: if it has no queued actions, it will move toward the target and attack.
    /// Designed for demo/prototyping.
    /// </summary>
    public class EnemyAIController : MonoBehaviour
    {
        [Header("Refs")]
        public BattleTimeline Timeline;
        public CombatUnit ControlledUnit;
        public CombatUnit TargetUnit;

        [Header("Timing")]
        public float ThinkIntervalSeconds = 0.6f;
        public float MinLeadTimeSeconds = 0.2f;

        [Header("Behavior")]
        public float PostMoveAttackDelaySeconds = 0.05f;

        [Header("Diagnostics")]
        public bool EnableDebugLogs = false;

        private float _nextThinkAtUnscaled;
        private float _suppressUntilTimelineTime;
        private float _lastTimelineTime;
        private float _lastTimelineTimeCheckUnscaled;
        private bool _warnedNoAttackAction;
        private bool _warnedNoGrid;
        private bool _warnedTimelineNotAdvancing;

        private void Awake()
        {
            if (ControlledUnit == null) ControlledUnit = GetComponent<CombatUnit>();
        }

        private void Update()
        {
            if (Timeline == null) Timeline = FindFirstObjectByType<BattleTimeline>();
            if (ControlledUnit == null) ControlledUnit = GetComponent<CombatUnit>();
            if (Timeline == null || ControlledUnit == null) return;

            if (Timeline.Paused) return;
            if (TargetUnit == null) return;

            // Detect a common setup problem: timeline exists but nobody is advancing it.
            if (Time.unscaledTime - _lastTimelineTimeCheckUnscaled > 1.0f)
            {
                _lastTimelineTimeCheckUnscaled = Time.unscaledTime;
                if (Mathf.Abs(Timeline.CurrentTime - _lastTimelineTime) < 0.0001f)
                {
                    if (!_warnedTimelineNotAdvancing && EnableDebugLogs)
                    {
                        _warnedTimelineNotAdvancing = true;
                        Debug.LogWarning("[EnemyAI] BattleTimeline.CurrentTime is not advancing. Scheduled intents will never execute. Ensure something calls Timeline.AdvanceTime(...).");
                    }
                }
                _lastTimelineTime = Timeline.CurrentTime;
            }

            if (Time.unscaledTime < _nextThinkAtUnscaled) return;
            _nextThinkAtUnscaled = Time.unscaledTime + ThinkIntervalSeconds;

            // Prevent spamming new plans while our last plan hasn't had time to execute.
            if (Timeline.CurrentTime < _suppressUntilTimelineTime) return;

            var action = FindFirstUsableAttackAction();
            if (action == null && !_warnedNoAttackAction)
            {
                _warnedNoAttackAction = true;
                if (EnableDebugLogs)
                {
                    Debug.LogWarning($"[EnemyAI] {ControlledUnit.name} has no usable attack Action (missing ActionLibrary/Pattern). Will only move.");
                }
            }

            float startDelay = MinLeadTimeSeconds;

            if (action != null && CanHitTargetNow(action))
            {
                var dir = GridMath.GetDirection(ControlledUnit.GridPosition, TargetUnit.GridPosition);
                long groupId = Timeline.ReserveGroupId();
                ActionScheduler.ScheduleAttack(Timeline, ControlledUnit, action, startDelay, targetDirection: dir, groupId: groupId);
                _suppressUntilTimelineTime = Timeline.CurrentTime + startDelay + ActionScheduler.EstimateAttackDuration(ControlledUnit, action);
                return;
            }

            // Move toward the target: choose a reachable neighbor around the target.
            if (GridManager.Instance == null)
            {
                if (!_warnedNoGrid)
                {
                    _warnedNoGrid = true;
                    if (EnableDebugLogs) Debug.LogWarning("[EnemyAI] GridManager.Instance is null; cannot move.");
                }
                return;
            }

            var best = FindBestDestinationNearTarget(maxRings: 6);
            if (!best.hasPath)
            {
                // Try again soon.
                _suppressUntilTimelineTime = Timeline.CurrentTime + 0.25f;
                return;
            }

            long moveGroup = Timeline.ReserveGroupId();
            ActionScheduler.ScheduleMoveTo(Timeline, ControlledUnit, best.destination, startTime: startDelay, groupId: moveGroup);

            // Schedule a follow-up attack using predicted end position (destination) to compute facing.
            float moveDuration = ActionScheduler.EstimateMoveDuration(ControlledUnit, best.path);
            float totalPlanned = startDelay + moveDuration;

            if (action != null)
            {
                float attackStart = startDelay + moveDuration + PostMoveAttackDelaySeconds;
                var face = GridMath.GetDirection(best.destination, TargetUnit.GridPosition);
                ActionScheduler.ScheduleAttack(Timeline, ControlledUnit, action, attackStart, targetDirection: face, groupId: moveGroup);
                totalPlanned = attackStart + ActionScheduler.EstimateAttackDuration(ControlledUnit, action);
            }

            _suppressUntilTimelineTime = Timeline.CurrentTime + totalPlanned;
        }

        private Action FindFirstUsableAttackAction()
        {
            if (ControlledUnit.ActionLibrary == null) return null;
            if (ControlledUnit.ActionLibrary.Actions == null) return null;

            for (int i = 0; i < ControlledUnit.ActionLibrary.Actions.Count; i++)
            {
                var data = ControlledUnit.ActionLibrary.Actions[i].Data;
                if (data == null) continue;
                if (data.Pattern == null) continue;
                return data;
            }
            return null;
        }

        private bool CanHitTargetNow(Action action)
        {
            if (action.Pattern == null) return false;

            var dir = GridMath.GetDirection(ControlledUnit.GridPosition, TargetUnit.GridPosition);
            var attackArea = action.Pattern.GetAffectedTriangles(ControlledUnit.GridPosition, dir);
            if (attackArea == null || attackArea.Count == 0) return false;

            var targetOcc = TargetUnit.GetOccupiedTriangles();
            for (int i = 0; i < targetOcc.Count; i++)
            {
                for (int j = 0; j < attackArea.Count; j++)
                {
                    if (targetOcc[i].Equals(attackArea[j])) return true;
                }
            }
            return false;
        }

        private (bool hasPath, Pathfinder.GridPoint destination, List<Pathfinder.GridPoint> path) FindBestDestinationNearTarget(int maxRings)
        {
            var obstacles = GridManager.Instance.GetGlobalObstacles(ControlledUnit);
            var pathfinder = new Pathfinder();

            bool found = false;
            float bestScore = float.MaxValue;
            Pathfinder.GridPoint bestDest = default;
            List<Pathfinder.GridPoint> bestPath = null;

            // Consider expanding rings around the target. This is required for large unit volumes.
            var seen = new HashSet<Pathfinder.GridPoint>();
            var frontier = new List<Pathfinder.GridPoint> { TargetUnit.GridPosition };
            seen.Add(TargetUnit.GridPosition);

            for (int ring = 1; ring <= Mathf.Max(1, maxRings); ring++)
            {
                var next = new List<Pathfinder.GridPoint>();
                for (int i = 0; i < frontier.Count; i++)
                {
                    var cur = frontier[i];
                    for (int d = 0; d < 12; d++)
                    {
                        var nb = GridMath.GetNeighbor(cur, (GridDirection)d);
                        if (seen.Add(nb)) next.Add(nb);
                    }
                }
                frontier = next;

                // Evaluate this ring only (keeps the AI local and prevents weird long walks).
                for (int i = 0; i < frontier.Count; i++)
                {
                    var dest = frontier[i];

                    var projDir = GridMath.GetDirection(ControlledUnit.GridPosition, dest);
                    var projected = ControlledUnit.GetProjectedOccupancy(dest, projDir);
                    if (GridManager.Instance.IsSpaceOccupied(projected, ControlledUnit)) continue;

                    var path = pathfinder.FindPath(ControlledUnit.GridPosition, dest, ControlledUnit.UnitVolumeDefinition, obstacles);
                    if (path == null || path.Count < 2) continue;

                    float score = path.Count;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestDest = dest;
                        bestPath = path;
                        found = true;
                    }
                }

                if (found) break;
            }

            return (found, bestDest, bestPath);
        }
    }
}
