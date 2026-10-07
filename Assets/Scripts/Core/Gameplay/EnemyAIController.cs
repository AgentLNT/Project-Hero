using UnityEngine;
using ProjectHero.Authoring.Legacy;
using System.Collections.Generic;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Compatibility.Runtime.Input;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;

namespace ProjectHero.Core.Gameplay
{
    /// <summary>
    /// 旧敌人 AI。任务 03B：<c>Update()</c> 保留旧 10Hz think 语义，但被登记为
    /// <strong>Legacy 从属写入者</strong>——它写时间线计划表并依赖顶层时钟推进，
    /// 由 <c>BattleRuntimeBootstrap</c> 按模式整体门控（New 全部禁用）。
    /// 迁移到 CommandRequest/AI 属任务 09。
    /// </summary>
    [DefaultExecutionOrder(ProjectHero.Core.Compatibility.Runtime.RuntimeCallbackRegistry.LegacyWriterExecutionOrder)]
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

        // ─────────────────────────────────────────────────────────────────────
        // 任务 09：本组件是 Legacy 从属写入者，只做**转发/适配**（裁定 R-9）。
        //
        // * New 模式下 AI 决策的唯一生产者是 Logic 的 AIControllerLogic（任务 09 产出 9/10，
        //   由架构师流实现在 `Assets/Scripts/Logic/AI/**`）。
        // * 一旦宿主注入新 AI 决策端口（任务 10 接线），本组件的旧 `Update()` 决策路径
        //   立即**整体停用**：它不再写旧时间线计划表，也不再自建第二套权威状态。
        // * 未注入端口时保持旧行为，供 Legacy 模式与既有 PlayMode 用例使用。
        // ─────────────────────────────────────────────────────────────────────
        [Header("Task 09 AI 决策端口（可空，由任务 10 注入）")]
        [Tooltip("注入后：旧决策路径整体停用；AI 命令只能由 Logic 的 AIControllerLogic 经命令入口产生。")]
        public IViewLogicPort DecisionPort;

        /// <summary>旧决策写入路径是否仍然启用（仅在未注入新 AI 端口时为 true）。</summary>
        public bool LegacyDecisionWritesEnabled => DecisionPort == null;

        /// <summary>宿主注入新 AI 决策端口（任务 10）。</summary>
        public void BindDecisionPort(IViewLogicPort port) => DecisionPort = port;

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
            // 任务 09：注入新 AI 决策端口后，旧决策路径整体停用。
            // AI 的命令只能由 Logic 的 AIControllerLogic 在 Step N 完成后按 TargetTick = N+1
            // 经已注册入口提交；表现层/旧壳不得自建第二套决策或直接写计划表。
            if (!LegacyDecisionWritesEnabled)
            {
                DebugState = "NewAiDriven";
                return;
            }

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

        private (bool found, GridPoint dest, List<GridPoint> path) FindBestPositionNearTarget(int maxRings)
        {
            if (GridManager.Instance == null) return (false, default, null);

            var obstacles = GridManager.Instance.GetGlobalObstacles(ControlledUnit);
            var pathfinder = new Pathfinder();

            GridPoint bestDest = default;
            List<GridPoint> bestPath = null;
            float bestScore = float.MaxValue;
            bool anyFound = false;

            var visited = new HashSet<GridPoint>();
            var queue = new Queue<(GridPoint point, int depth)>();

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
