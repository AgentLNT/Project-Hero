using UnityEngine;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Compatibility.Runtime.Input;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Input;
using ProjectHero.Visuals;
using ProjectHero.UI; // Added UI namespace
using ProjectHero.UI.Timeline;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Core.Gameplay
{
    /// <summary>
    /// Handles high-level player interactions: Selection, Command Issuing.
    /// Decoupled from specific demos.
    /// </summary>
    public class TacticsController : MonoBehaviour
    {
        private enum PlanStep
        {
            None,
            Targeting,
            Placing
        }
        [Header("References")]
        public BattleTimeline Timeline;
        public GridCursor Cursor;

        [Header("Defensive Action Settings")]
        public float BlockDuration = 1.0f; // Block window duration
        public float DodgeDuration = 0.5f; // Dodge window duration

        // ─────────────────────────────────────────────────────────────────────
        // 任务 09：本组件降级为**转发/适配**壳（裁定 R-9）。
        //
        // * 输入模式由 `ViewInputController` 持有：选择单位、选择动作、选择方向/目标、
        //   取消手势、时间线放置/重排都是**视图状态**。
        // * 本组件不再写旧 `InputManager.IgnoreUnitClicks` 布尔量；旧布尔量已删除。
        // * 注入命令端口后（`InputController.HasPorts`），本组件**禁用**全部直接写逻辑的
        //   旧捷径（旧时间线排程、直接 `unit.SetGridPosition`、直接改 `IsActing`）：
        //   那些写入必须改为经唯一入口提交 `CommandRequest`（由任务 10 接线）。
        // ─────────────────────────────────────────────────────────────────────
        [Header("Task 09 输入模式（可空）")]
        [Tooltip("注入后：选择/取消只是视图状态；未注入命令端口时保持旧 Legacy 行为（AssetScheduler 快捷路径）。")]
        public ViewInputController InputController;

        /// <summary>旧"直接写逻辑"的捷径是否仍然启用（仅当没有注入命令端口时为 true）。</summary>
        public bool LegacyImmediateWritesEnabled => ProjectHero.Core.Compatibility.Runtime.BattleRuntimeBootstrap.LegacyWritesAllowed && (InputController == null || !InputController.HasPorts);

        private void EnsureInputController()
        {
            if (InputController == null) InputController = new ViewInputController();
            var timelineUI = UIManager.Instance != null ? UIManager.Instance.TimelineUI : null;
            if (timelineUI != null && timelineUI.InputController != null)
                InputController.BindPorts(timelineUI.InputPorts);
            if (InputManager.Instance != null) InputManager.Instance.InputController = InputController;
        }

        private CombatUnit _selectedUnit;
        private Action _selectedAction; // New: Track selected action
        private bool _isMoveMode;
        private bool _isDodgeCounterMode = false;
        private PlanStep _planStep = PlanStep.None;

        // Cached target info for Step 2 -> Step 3 -> Step back
        private GridPoint _plannedTarget;
        private GridDirection _plannedDirection;
        private System.Collections.Generic.List<GridPoint> _plannedPath;

        private void Start()
        {
            EnsureInputController();

            if (Timeline == null) Timeline = FindFirstObjectByType<BattleTimeline>();
            if (Timeline == null)
            {
                Debug.LogWarning("TacticsController: No BattleTimeline found in scene.");
            }

            Timeline.OnDodgeSuccessRequestMove += HandleDodgeCounterMove;

            // Ensure Cursor exists
            if (Cursor == null)
            {
                Cursor = FindFirstObjectByType<GridCursor>();
                if (Cursor == null)
                {
                    var cursorObj = new GameObject("GridCursor");
                    Cursor = cursorObj.AddComponent<GridCursor>();
                }
            }

            // Subscribe to Input Events
            if (InputManager.Instance != null)
            {
                InputManager.Instance.OnUnitClick += HandleUnitClick;
                InputManager.Instance.OnGroundClick += HandleGroundClick;
                InputManager.Instance.OnGroundHover += HandleGroundHover;
                InputManager.Instance.OnCancel += HandleCancel;
            }
            else
            {
                Debug.LogError("TacticsController: InputManager instance not found!");
            }

            if (Timeline != null)
            {
                Timeline.OnDodgeSuccessRequestMove += HandleDodgeCounterMove;
            }
        }

        // Public API for UI to select an action
        public void SelectAction(Action action)
        {
            ResetPlanningFlow(clearPlacement: true);
            _selectedAction = action;
            _isMoveMode = false;
            _planStep = PlanStep.Targeting;
            Debug.Log($"[Tactics] Selected Action: {action.Name}");

            // 任务 09：进入"选择动作"模式（纯视图状态；不再写旧布尔量）。
            //
            // 这里**不**把旧 CombatUnit 映射成 UnitId：旧组件当前没有稳定的 Logic 单位标识，
            // 而 GetInstanceID()/GetEntityId() 属于"对象地址/注册顺序"一类不稳定键
            // （00 号规则 16 明确禁止其进入任何参与决策的路径）。
            // 旧→Logic 的显式单位映射随任务 10 的适配器注入，
            // 届时改为 InputController.SelectUnit(unitId)。
            EnsureInputController();
            InputController.SelectAction(action != null ? action.Name : string.Empty);
        }

        // Public API for UI to select Move mode
        public void SelectMove()
        {
            ResetPlanningFlow(clearPlacement: true);
            _selectedAction = null;
            _isMoveMode = true;
            _planStep = PlanStep.Targeting;
            Debug.Log("[Tactics] Selected Move Mode");

            EnsureInputController();
        }

        // Public API for UI to execute Block
        public void ExecuteBlock()
        {
            if (!ProjectHero.Core.Compatibility.Runtime.BattleRuntimeBootstrap.LegacyWritesAllowed)
            {
                EnsureInputController();
                if (InputController.HasReactionSelection) InputController.ConfirmReaction();
                return;
            }

            ResetPlanningFlow(clearPlacement: true);
            if (_selectedUnit == null)
            {
                Debug.LogWarning("[Tactics] No unit selected for Block.");
                return;
            }

            if (_selectedUnit.IsStaggered || _selectedUnit.IsKnockedDown)
            {
                Debug.LogWarning($"[Tactics] {_selectedUnit.name} cannot act (Staggered/KnockedDown).");
                return;
            }

            var timelineUI = UIManager.Instance != null ? UIManager.Instance.TimelineUI : null;
            if (timelineUI == null)
            {
                Debug.LogWarning("[Tactics] Timeline UI missing; falling back to immediate Block.");
                ActionScheduler.ScheduleBlock(Timeline, _selectedUnit, 0f, BlockDuration);
                return;
            }

            // Step 3: Placement on timeline (no world targeting required)
            _planStep = PlanStep.Placing;
            timelineUI.PlacementCommitted -= OnPlacementCommitted;
            timelineUI.PlacementCancelled -= OnPlacementCancelled;
            timelineUI.PlacementCommitted += OnPlacementCommitted;
            timelineUI.PlacementCancelled += OnPlacementCancelled;

            var placement = new TimelineActionPlacement
            {
                Owner = _selectedUnit,
                Kind = TimelineActionKind.Block,
                Label = "Block",
                DurationSeconds = BlockDuration,
                Lane = TimelineLane.Player,
                Schedule = (startDelay, groupId) =>
                {
                    ActionScheduler.ScheduleBlock(Timeline, _selectedUnit, startDelay, BlockDuration, focusCost: 2f, groupId: groupId);
                }
            };
            timelineUI.BeginPlacement(placement);
        }

        // Public API for UI to execute Dodge
        public void ExecuteDodge()
        {
            if (!ProjectHero.Core.Compatibility.Runtime.BattleRuntimeBootstrap.LegacyWritesAllowed)
            {
                EnsureInputController();
                if (InputController.HasReactionSelection) InputController.ConfirmReaction();
                return;
            }

            ResetPlanningFlow(clearPlacement: true);
            if (_selectedUnit == null)
            {
                Debug.LogWarning("[Tactics] No unit selected for Dodge.");
                return;
            }

            if (_selectedUnit.IsStaggered || _selectedUnit.IsKnockedDown)
            {
                Debug.LogWarning($"[Tactics] {_selectedUnit.name} cannot act (Staggered/KnockedDown).");
                return;
            }

            var timelineUI = UIManager.Instance != null ? UIManager.Instance.TimelineUI : null;
            if (timelineUI == null)
            {
                Debug.LogWarning("[Tactics] Timeline UI missing; falling back to immediate Dodge.");
                ActionScheduler.ScheduleDodge(Timeline, _selectedUnit, 0f, DodgeDuration);
                return;
            }

            // Step 3: Placement on timeline (no world targeting required)
            _planStep = PlanStep.Placing;
            timelineUI.PlacementCommitted -= OnPlacementCommitted;
            timelineUI.PlacementCancelled -= OnPlacementCancelled;
            timelineUI.PlacementCommitted += OnPlacementCommitted;
            timelineUI.PlacementCancelled += OnPlacementCancelled;

            var placement = new TimelineActionPlacement
            {
                Owner = _selectedUnit,
                Kind = TimelineActionKind.Dodge,
                Label = "Dodge",
                DurationSeconds = DodgeDuration,
                Lane = TimelineLane.Player,
                Schedule = (startDelay, groupId) =>
                {
                    ActionScheduler.ScheduleDodge(Timeline, _selectedUnit, startDelay, DodgeDuration, focusCost: 1f, groupId: groupId);
                }
            };
            timelineUI.BeginPlacement(placement);
        }

        // Public API for UI to execute Recover (stand up / regain balance)
        public void ExecuteRecover()
        {
            if (!ProjectHero.Core.Compatibility.Runtime.BattleRuntimeBootstrap.LegacyWritesAllowed)
            {
                return;
            }

            ResetPlanningFlow(clearPlacement: true);
            if (_selectedUnit == null)
            {
                Debug.LogWarning("[Tactics] No unit selected for Recover.");
                return;
            }

            // Recover is specifically allowed when staggered/knocked down, but not while already acting.
            if (_selectedUnit.IsActing)
            {
                Debug.LogWarning($"[Tactics] {_selectedUnit.name} cannot recover while acting.");
                return;
            }

            if (!_selectedUnit.IsStaggered && !_selectedUnit.IsKnockedDown)
            {
                Debug.LogWarning($"[Tactics] {_selectedUnit.name} is not staggered/knocked down.");
                return;
            }

            var timelineUI = UIManager.Instance != null ? UIManager.Instance.TimelineUI : null;
            float duration = ActionScheduler.EstimateRecoverDuration();

            if (timelineUI == null)
            {
                Debug.LogWarning("[Tactics] Timeline UI missing; falling back to immediate Recover.");
                ActionScheduler.ScheduleRecover(Timeline, _selectedUnit, 0f, staminaCost: 10f, duration: duration);
                return;
            }

            _planStep = PlanStep.Placing;
            timelineUI.PlacementCommitted -= OnPlacementCommitted;
            timelineUI.PlacementCancelled -= OnPlacementCancelled;
            timelineUI.PlacementCommitted += OnPlacementCommitted;
            timelineUI.PlacementCancelled += OnPlacementCancelled;

            var placement = new TimelineActionPlacement
            {
                Owner = _selectedUnit,
                Kind = TimelineActionKind.Recover,
                Label = "Recover",
                DurationSeconds = duration,
                Lane = TimelineLane.Player,
                Schedule = (startDelay, groupId) =>
                {
                    ActionScheduler.ScheduleRecover(Timeline, _selectedUnit, startDelay, staminaCost: 10f, duration: duration, groupId: groupId);
                }
            };
            timelineUI.BeginPlacement(placement);
        }

        private void OnPlacementCommitted()
        {
            // After placing a block, exit planning mode.
            ResetPlanningFlow(clearPlacement: false);
        }

        private void OnPlacementCancelled()
        {
            // Step back from timeline placement to world targeting.
            if (_selectedAction != null || _isMoveMode)
            {
                _planStep = PlanStep.Targeting;
                EnsureInputController();
                InputController.EnterDirectionTargetSelection();
            }
            else
            {
                _planStep = PlanStep.None;
            }
        }

        private void OnDestroy()
        {
            if (InputManager.Instance != null)
            {
                InputManager.Instance.OnUnitClick -= HandleUnitClick;
                InputManager.Instance.OnGroundClick -= HandleGroundClick;
                InputManager.Instance.OnGroundHover -= HandleGroundHover;
                InputManager.Instance.OnCancel -= HandleCancel;
                InputManager.Instance.InputController = null;
            }
        }

        private void HandleCancel()
        {
            if (_isDodgeCounterMode)
            {
                Debug.Log("[Tactics] Cancelled Dodge Counter. Resuming time.");

                _isDodgeCounterMode = false;
                _isMoveMode = false;

                Timeline.SetSystemPaused(false);
                Time.timeScale = 1.0f; 

                if (UIManager.Instance) UIManager.Instance.OnUnitSelected(_selectedUnit);
                return;
            }

            var timelineUI = UIManager.Instance != null ? UIManager.Instance.TimelineUI : null;
            if (_planStep == PlanStep.Placing && timelineUI != null && timelineUI.HasPendingPlacement)
            {
                timelineUI.CancelPlacement();
                return;
            }

            if (_selectedAction != null)
            {
                Debug.Log($"[Tactics] Cancelled Action: {_selectedAction.Name}");
                _selectedAction = null;
                _planStep = PlanStep.None;

                EnsureInputController();
                InputController.CancelGesture();
                return;
            }

            if (_isMoveMode)
            {
                Debug.Log("[Tactics] Cancelled Move Mode");
                _isMoveMode = false;
                _planStep = PlanStep.None;
                EnsureInputController();
                InputController.CancelGesture();
                return;
            }

            if (_selectedUnit != null)
            {
                Debug.Log($"[Tactics] Deselected Unit: {_selectedUnit.name}");
                _selectedUnit = null;
                if (Cursor != null) Cursor.Hide();

                if (UIManager.Instance != null) UIManager.Instance.OnUnitDeselected();

                // 任务 09：取消/清除选择只是视图状态——不写逻辑、不暂停执行。
                EnsureInputController();
                InputController.ClearSelection();
            }
        }
        private void HandleGroundHover(Vector3 worldPos)
        {
            if (Cursor == null || GridManager.Instance == null) return;

            // Step 3: timeline placement should not update world previews.
            if (_planStep == PlanStep.Placing)
            {
                Cursor.Hide();
                return;
            }

            if (_selectedUnit != null)
            {
                var targetGridPos = GridManager.Instance.WorldToGrid(worldPos);

                if (_selectedAction != null)
                {
                    // Mode: Action Selected -> Show Attack Pattern
                    // 1. Calculate Direction from Unit to Mouse
                    var dir = GridMath.GetDirection(_selectedUnit.GridPosition, targetGridPos);
                    
                    // 2. Get Pattern for that direction
                    if (_selectedAction.Pattern != null)
                    {
                        var attackVolume = _selectedAction.Pattern.GetAffectedTriangles(_selectedUnit.GridPosition, dir);
                        Cursor.cursorColor = Color.red; // Temporary visual feedback
                        Cursor.ShowVolume(attackVolume);
                    }
                }
                else if (_isMoveMode)
                {
                    // Mode: Unit Selected (Movement) -> Show Projected Volume at Vertex
                    // Use the unit's current facing for the preview
                    var projectedVolume = _selectedUnit.GetProjectedOccupancy(targetGridPos, _selectedUnit.FacingDirection);
                    Cursor.cursorColor = Color.yellow; // Revert color
                    Cursor.ShowVolume(projectedVolume);
                }
                else
                {
                    // Mode: unit selected but not in any targeting mode -> no auto movement preview
                    Cursor.Hide();
                }
            }
            else
            {
                // Mode: No Selection -> Snap to Triangle
                var tile = GridManager.Instance.WorldToTriangle(worldPos);
                Cursor.cursorColor = Color.yellow;
                Cursor.Show(tile);
            }
        }

        private void HandleUnitClick(CombatUnit unit)
        {
            var timelineUI = UIManager.Instance != null ? UIManager.Instance.TimelineUI : null;

            // Only the designated player-controlled unit can be selected for issuing commands.
            // Any other unit click should only update ObservedUnit.
            if (timelineUI != null && (unit == null || !unit.IsPlayerControlled))
            {
                timelineUI.SetObservedUnit(unit);
                Debug.Log($"[Tactics] Observed Unit: {unit.name}");
                if (Cursor != null) Cursor.Hide();
                return;
            }

            _selectedUnit = unit;
            ResetPlanningFlow(clearPlacement: true);
            Debug.Log($"[Tactics] Selected Unit: {unit.name}");

            // 任务 09：把"选中"同步给输入模式状态机（纯视图状态）。
            // 单位标识的显式映射随任务 10 注入（见 SelectAction 处的说明），
            // 此处不构造任何不稳定键。
            EnsureInputController();

            // Notify UI (this also sets PlayerUnit on the timeline UI)
            if (UIManager.Instance != null)
            {
                UIManager.Instance.OnUnitSelected(unit);
            }

            // Hide cursor immediately upon selection (will be updated by next hover)
            if (Cursor != null) Cursor.Hide();
        }

        private void ResetPlanningFlow(bool clearPlacement)
        {
            // Clear any cached targeting/placement state so switching actions is always clean.
            _selectedAction = null;
            _isMoveMode = false;
            _planStep = PlanStep.None;
            _plannedTarget = default;
            _plannedDirection = default;
            _plannedPath = null;

            var timelineUI = UIManager.Instance != null ? UIManager.Instance.TimelineUI : null;
            if (timelineUI != null)
            {
                timelineUI.PlacementCommitted -= OnPlacementCommitted;
                timelineUI.PlacementCancelled -= OnPlacementCancelled;

                if (clearPlacement && timelineUI.HasPendingPlacement)
                {
                    timelineUI.CancelPlacement();
                }
            }

            // 任务 09：清除规划流是纯视图回退（不再写旧布尔量）。
            EnsureInputController();
            InputController.CancelGesture();
            if (Cursor != null) Cursor.Hide();
        }

        private void HandleGroundClick(Vector3 worldPos)
        {
            // Step 3: timeline placement should not accept world clicks.
            if (_planStep == PlanStep.Placing)
            {
                Debug.Log("[Tactics] Ground click ignored (timeline placement active).");
                return;
            }

            if (_selectedUnit == null)
            {
                // Optional: Select Tile Info?
                var tile = GridManager.Instance.WorldToTriangle(worldPos);
                Debug.Log($"[Tactics] Clicked Tile (No Unit Selected): {tile}");
                return;
            }

            // Check if unit can act
            if (_selectedUnit.IsStaggered || _selectedUnit.IsKnockedDown)
            {
                Debug.LogWarning($"[Tactics] Unit {_selectedUnit.name} cannot act (Staggered/KnockedDown).");
                return;
            }

            var targetGridPos = GridManager.Instance.WorldToGrid(worldPos);

            if (_selectedAction != null)
            {
                // Prepare Attack Block for timeline placement
                var timelineUI = UIManager.Instance != null ? UIManager.Instance.TimelineUI : null;
                if (timelineUI == null)
                {
                    // 任务 09：注入命令端口后**禁止**直接排程逻辑动作。
                    // 攻击计划必须经 ScheduleEdit 的 AddOrdinaryPlanOperation 提交（任务 10 接线）。
                    if (!LegacyImmediateWritesEnabled)
                    {
                        Debug.LogWarning(
                            "[Tactics] 已注入命令端口：拒绝直接排程攻击。请经 CommandRequest(ScheduleEdit/AddOrdinaryPlan) 提交。");
                        return;
                    }

                    Debug.LogWarning("[Tactics] Timeline UI missing; falling back to immediate Attack.");
                    var dirNow = GridMath.GetDirection(_selectedUnit.GridPosition, targetGridPos);
                    ActionScheduler.ScheduleAttack(Timeline, _selectedUnit, _selectedAction, 0f, dirNow);
                }
                else
                {
                    var dir = GridMath.GetDirection(_selectedUnit.GridPosition, targetGridPos);
                    float duration = ActionScheduler.EstimateAttackDuration(_selectedUnit, _selectedAction);
                    var actionCopy = _selectedAction;
                    var ownerCopy = _selectedUnit;
                    var dirCopy = dir;

                    _plannedTarget = targetGridPos;
                    _planStep = PlanStep.Placing;

                    timelineUI.PlacementCommitted -= OnPlacementCommitted;
                    timelineUI.PlacementCancelled -= OnPlacementCancelled;
                    timelineUI.PlacementCommitted += OnPlacementCommitted;
                    timelineUI.PlacementCancelled += OnPlacementCancelled;

                    var placement = new TimelineActionPlacement
                    {
                        Owner = ownerCopy,
                        Kind = TimelineActionKind.Attack,
                        Label = actionCopy.Name,
                        DurationSeconds = duration,
                        Lane = TimelineLane.Player,
                        AttackFacingAbsolute = dirCopy,
                        Schedule = (startDelay, groupId) =>
                        {
                            // Store absolute facing (not relative) so chaining won't accumulate rotations.
                            ActionScheduler.ScheduleAttack(Timeline, ownerCopy, actionCopy, startDelay, targetDirection: dirCopy, groupId: groupId);
                        }
                    };
                    timelineUI.BeginPlacement(placement);
                }

                // Do not clear selection here; clearing happens on placement commit.
                if (Cursor != null) Cursor.Hide();

                // Reset targeting mode（纯视图状态）
                EnsureInputController();
                InputController.EnterDirectionTargetSelection();
            }
            else
            {
                // Only allow moving when Move mode is explicitly selected.
                if (!_isMoveMode)
                {
                    Debug.Log("[Tactics] Ground click ignored (no action selected).");
                    return;
                }

                // Step 2: confirm target point in world, then Step 3: placement on timeline
                _plannedTarget = targetGridPos;
                IssueMoveCommand(_selectedUnit, targetGridPos);

                // Reset targeting mode（纯视图状态）
                EnsureInputController();
                InputController.EnterDirectionTargetSelection();
            }
        }

        private void HandleDodgeCounterMove(CombatUnit unit)
        {
            if (!unit.IsPlayerControlled) 
                return;

            Debug.Log($"[Tactics] Dodge Success! Choose counter step for {unit.name}");

            _selectedUnit = unit;
            _isMoveMode = true; 
            _isDodgeCounterMode = true;

            Timeline.SetSystemPaused(true);

        }

        private void IssueMoveCommand(CombatUnit unit, GridPoint targetGridPos)
        {
            if (Timeline == null) return;

            if (_isDodgeCounterMode)
            {
                // 任务 09：注入命令端口后**禁止**直接写逻辑位置。
                // Dodge 的换位只能由 Logic 在 TriggerTick 经统一事务提交
                // （ReactionCommand(Dodge, 目的格) -> ReactionPlanner -> DodgeRelocationAuthority）。
                if (!LegacyImmediateWritesEnabled)
                {
                    Debug.LogWarning(
                        "[Tactics] 已注入命令端口：拒绝直接写单位位置。Dodge 换位必须由 Logic 在 TriggerTick 提交。");
                    return;
                }

                var pathfinder = new Pathfinder();
                var dodgePath = pathfinder.FindPath(unit.GridPosition, targetGridPos, unit.UnitVolumeDefinition, null);

                if (dodgePath == null || dodgePath.Count > 2)
                {
                    Debug.LogWarning("[Tactics] Dodge step must be exactly 1 tile!");
                    return;
                }

                unit.SetGridPosition(targetGridPos);
                Debug.Log($"[Tactics] Dodge Counter Executed to {targetGridPos}");

                Timeline.SetSystemPaused(false);
                Time.timeScale = 1.0f; 
                _isDodgeCounterMode = false;
                _isMoveMode = false;

                if (UIManager.Instance) UIManager.Instance.OnUnitSelected(unit);
                return;
            }
            if (!unit.CanAct) return;

            Debug.Log($"[Tactics] Moving {unit.name} to {targetGridPos}");

            var obstacles = GridManager.Instance.GetGlobalObstacles(unit);

            var pathfinderNormal = new Pathfinder();
            var path = pathfinderNormal.FindPath(unit.GridPosition, targetGridPos, unit.UnitVolumeDefinition, obstacles);

            if (path != null)
            {
                Debug.Log($"[Tactics] Path found! Length: {path.Count}");

                var timelineUI = UIManager.Instance != null ? UIManager.Instance.TimelineUI : null;
                if (timelineUI == null)
                {
                    // 任务 09：注入命令端口后**禁止**直接置 IsActing 或直接排程移动。
                    // 普通 Move 计划必须经 ScheduleEdit 的 AddOrdinaryPlanOperation 提交。
                    if (!LegacyImmediateWritesEnabled)
                    {
                        Debug.LogWarning(
                            "[Tactics] 已注入命令端口：拒绝直接排程移动。请经 CommandRequest(ScheduleEdit/AddOrdinaryPlan) 提交。");
                        return;
                    }

                    Debug.LogWarning("[Tactics] Timeline UI missing; falling back to immediate Move.");
                    unit.IsActing = true;
                    ActionScheduler.ScheduleMove(Timeline, unit, path);
                    return;
                }

                _planStep = PlanStep.Placing;
                timelineUI.PlacementCommitted -= OnPlacementCommitted;
                timelineUI.PlacementCancelled -= OnPlacementCancelled;
                timelineUI.PlacementCommitted += OnPlacementCommitted;
                timelineUI.PlacementCancelled += OnPlacementCancelled;

                float duration = ActionScheduler.EstimateMoveDuration(unit, path);
                var ownerCopy = unit;
                var destCopy = targetGridPos;
                _plannedPath = new System.Collections.Generic.List<GridPoint>(path);

                var placement = new TimelineActionPlacement
                {
                    Owner = ownerCopy,
                    Kind = TimelineActionKind.Move,
                    Label = "Move",
                    DurationSeconds = duration,
                    Lane = TimelineLane.Player,
                    MoveDestination = destCopy,
                    Schedule = (startDelay, groupId) =>
                    {
                        ActionScheduler.ScheduleMoveTo(Timeline, ownerCopy, destCopy, startTime: startDelay, groupId: groupId);
                    }
                };
                timelineUI.BeginPlacement(placement);
            }
            else
            {
                Debug.LogWarning("[Tactics] No path found!");
            }
        }
    }
}
