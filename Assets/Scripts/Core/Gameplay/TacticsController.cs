using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Input;
using ProjectHero.Visuals;
using ProjectHero.UI; // Added UI namespace

namespace ProjectHero.Core.Gameplay
{
    /// <summary>
    /// Handles high-level player interactions: Selection, Command Issuing.
    /// Decoupled from specific demos.
    /// </summary>
    public class TacticsController : MonoBehaviour
    {
        [Header("References")]
        public BattleTimeline Timeline;
        public GridCursor Cursor;

        private CombatUnit _selectedUnit;
        private Action _selectedAction; // New: Track selected action

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
            _selectedAction = action;
            Debug.Log($"[Tactics] Selected Action: {action.Name}");
            
            // Enable targeting mode (ignore unit clicks)
            if (InputManager.Instance != null) InputManager.Instance.IgnoreUnitClicks = true;
        }

        // Public API for UI to select Move mode
        public void SelectMove()
        {
            _selectedAction = null;
            Debug.Log("[Tactics] Selected Move Mode");
            
            // Enable targeting mode (ignore unit clicks) to allow moving to tiles occupied by units
            if (InputManager.Instance != null) InputManager.Instance.IgnoreUnitClicks = true;
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
            if (_selectedAction != null)
            {
                Debug.Log($"[Tactics] Cancelled Action: {_selectedAction.Name}");
                _selectedAction = null;
                
                // Disable targeting mode
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
                else
                {
                    // Mode: Unit Selected (Movement) -> Show Projected Volume at Vertex
                    // Use the unit's current facing for the preview
                    var projectedVolume = _selectedUnit.GetProjectedOccupancy(targetGridPos, _selectedUnit.FacingDirection);
                    Cursor.cursorColor = Color.yellow; // Revert color
                    Cursor.ShowVolume(projectedVolume);
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
            _selectedUnit = unit;
            _selectedAction = null; // Reset action on new unit selection
            Debug.Log($"[Tactics] Selected Unit: {unit.name}");
            
            // Notify UI
            if (UIManager.Instance != null)
            {
                UIManager.Instance.OnUnitSelected(unit);
            }

            // Hide cursor immediately upon selection (will be updated by next hover)
            if (Cursor != null) Cursor.Hide();
        }

        private void HandleGroundClick(Vector3 worldPos)
        {
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
                // Execute Attack
                // 1. Determine Direction
                var dir = GridMath.GetDirection(_selectedUnit.GridPosition, targetGridPos);
                
                Debug.Log($"[Tactics] Executing Attack {_selectedAction.Name} facing {dir}");
                
                // Mark as acting immediately to prevent double-clicks
                _selectedUnit.IsActing = true;

                // 2. Schedule Attack (Start immediately at T=0 relative to now)
                AttackAction.ScheduleAttack(Timeline, _selectedUnit, _selectedAction, 0f, dir);
                
                _selectedAction = null; // Deselect action after use
                Cursor.Hide();
                
                // Reset targeting mode
                if (InputManager.Instance != null) InputManager.Instance.IgnoreUnitClicks = false;
            }
            else
            {
                // Execute Move
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
                
                // Mark as acting immediately
                unit.IsActing = true;

                MovementAction.SchedulePath(Timeline, unit, path);
            }
            else
            {
                Debug.LogWarning("[Tactics] No path found!");
            }
        }
    }
}
