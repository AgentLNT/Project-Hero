using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Input;
using ProjectHero.Visuals;
using ProjectHero.UI; // Added UI namespace
using ProjectHero.UI.Timeline;

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

        private CombatUnit _selectedUnit;
        private Action _selectedAction; // New: Track selected action
        private bool _isMoveMode;
        private PlanStep _planStep = PlanStep.None;

        // Cached target info for Step 2 -> Step 3 -> Step back
        private Pathfinder.GridPoint _plannedTarget;
        private GridDirection _plannedDirection;
        private System.Collections.Generic.List<Pathfinder.GridPoint> _plannedPath;

        private void Start()
        {
            if (Timeline == null) Timeline = FindFirstObjectByType<BattleTimeline>();
            if (Timeline == null)
            {
                Debug.LogWarning("TacticsController: No BattleTimeline found in scene.");
            }

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
        }

        // Public API for UI to select an action
        public void SelectAction(Action action)
        {
            ResetPlanningFlow(clearPlacement: true);
            _selectedAction = action;
            _isMoveMode = false;
            _planStep = PlanStep.Targeting;
            Debug.Log($"[Tactics] Selected Action: {action.Name}");
            
            // Enable targeting mode (ignore unit clicks)
            if (InputManager.Instance != null) InputManager.Instance.IgnoreUnitClicks = true;
        }

        // Public API for UI to select Move mode
        public void SelectMove()
        {
            ResetPlanningFlow(clearPlacement: true);
            _selectedAction = null;
            _isMoveMode = true;
            _planStep = PlanStep.Targeting;
            Debug.Log("[Tactics] Selected Move Mode");
            
            // Enable targeting mode (ignore unit clicks) to allow moving to tiles occupied by units
            if (InputManager.Instance != null) InputManager.Instance.IgnoreUnitClicks = true;
        }

        // Public API for UI to execute Block
        public void ExecuteBlock()
        {
            ResetPlanningFlow(clearPlacement: true);
            if (_selectedUnit == null)
            {
                Debug.LogWarning("[Tactics] No unit selected for Block.");
                return;
            }

            if (!_selectedUnit.CanAct)
            {
                Debug.LogWarning($"[Tactics] {_selectedUnit.name} cannot act (Busy/Stunned).");
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
            ResetPlanningFlow(clearPlacement: true);
            if (_selectedUnit == null)
            {
                Debug.LogWarning("[Tactics] No unit selected for Dodge.");
                return;
            }

            if (!_selectedUnit.CanAct)
            {
                Debug.LogWarning($"[Tactics] {_selectedUnit.name} cannot act (Busy/Stunned).");
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
                if (InputManager.Instance != null) InputManager.Instance.IgnoreUnitClicks = true;
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
                
                // Reset flag
                InputManager.Instance.IgnoreUnitClicks = false;
            }
        }

        private void HandleCancel()
        {
            // Step-back logic for planning
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
                
                // Disable targeting mode
                if (InputManager.Instance != null) InputManager.Instance.IgnoreUnitClicks = false;
                
                return;
            }

            if (_isMoveMode)
            {
                Debug.Log("[Tactics] Cancelled Move Mode");
                _isMoveMode = false;
                _planStep = PlanStep.None;
                if (InputManager.Instance != null) InputManager.Instance.IgnoreUnitClicks = false;
                return;
            }

            if (_selectedUnit != null)
            {
                Debug.Log($"[Tactics] Deselected Unit: {_selectedUnit.name}");
                _selectedUnit = null;
                if (Cursor != null) Cursor.Hide();
                
                // Notify UI
                if (UIManager.Instance != null) UIManager.Instance.OnUnitDeselected();
                
                // Ensure targeting mode is off
                if (InputManager.Instance != null) InputManager.Instance.IgnoreUnitClicks = false;
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

            if (InputManager.Instance != null) InputManager.Instance.IgnoreUnitClicks = false;
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
            if (!_selectedUnit.CanAct)
            {
                Debug.LogWarning($"[Tactics] Unit {_selectedUnit.name} cannot act (Busy/Stunned).");
                return;
            }

            var targetGridPos = GridManager.Instance.WorldToGrid(worldPos);

            if (_selectedAction != null)
            {
                // Prepare Attack Block for timeline placement
                var timelineUI = UIManager.Instance != null ? UIManager.Instance.TimelineUI : null;
                if (timelineUI == null)
                {
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
                
                // Reset targeting mode
                if (InputManager.Instance != null) InputManager.Instance.IgnoreUnitClicks = false;
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

                // Reset targeting mode
                if (InputManager.Instance != null) InputManager.Instance.IgnoreUnitClicks = false;
            }
        }

        private void IssueMoveCommand(CombatUnit unit, Pathfinder.GridPoint targetGridPos)
        {
            if (Timeline == null) return;
            
            // Double check CanAct
            if (!unit.CanAct) return;

            Debug.Log($"[Tactics] Moving {unit.name} to {targetGridPos}");

            // Get Obstacles (ignoring self)
            var obstacles = GridManager.Instance.GetGlobalObstacles(unit);
            
            // Find Path
            var pathfinder = new Pathfinder();
            var path = pathfinder.FindPath(unit.GridPosition, targetGridPos, unit.UnitVolumeDefinition, obstacles);

            if (path != null)
            {
                Debug.Log($"[Tactics] Path found! Length: {path.Count}");

                var timelineUI = UIManager.Instance != null ? UIManager.Instance.TimelineUI : null;
                if (timelineUI == null)
                {
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
                _plannedPath = new System.Collections.Generic.List<Pathfinder.GridPoint>(path);
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
