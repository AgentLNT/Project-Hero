using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Input;
using ProjectHero.Visuals;

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

        private void OnDestroy()
        {
            if (InputManager.Instance != null)
            {
                InputManager.Instance.OnUnitClick -= HandleUnitClick;
                InputManager.Instance.OnGroundClick -= HandleGroundClick;
                InputManager.Instance.OnGroundHover -= HandleGroundHover;
                InputManager.Instance.OnCancel -= HandleCancel;
            }
        }

        private void HandleCancel()
        {
            if (_selectedUnit != null)
            {
                Debug.Log($"[Tactics] Deselected Unit: {_selectedUnit.name}");
                _selectedUnit = null;
                if (Cursor != null) Cursor.Hide();
            }
        }

        private void HandleGroundHover(Vector3 worldPos)
        {
            if (Cursor == null || GridManager.Instance == null) return;

            if (_selectedUnit != null)
            {
                // Mode: Unit Selected -> Show Projected Volume at Vertex
                var targetGridPos = GridManager.Instance.WorldToGrid(worldPos);
                
                // Use the unit's current facing for the preview
                // (Future: Could rotate based on mouse gesture or path)
                var projectedVolume = _selectedUnit.GetProjectedOccupancy(targetGridPos, _selectedUnit.FacingDirection);
                
                Cursor.ShowVolume(projectedVolume);
            }
            else
            {
                // Mode: No Selection -> Snap to Triangle
                var tile = GridManager.Instance.WorldToTriangle(worldPos);
                Cursor.Show(tile);
            }
        }

        private void HandleUnitClick(CombatUnit unit)
        {
            _selectedUnit = unit;
            Debug.Log($"[Tactics] Selected Unit: {unit.name}");
            // Hide cursor immediately upon selection
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

            // Unit Selected -> Move to Vertex
            var targetGridPos = GridManager.Instance.WorldToGrid(worldPos);
            
            // Basic Move Command
            IssueMoveCommand(_selectedUnit, targetGridPos);
        }

        private void IssueMoveCommand(CombatUnit unit, Pathfinder.GridPoint targetGridPos)
        {
            if (Timeline == null) return;
            
            Debug.Log($"[Tactics] Moving {unit.name} to {targetGridPos}");

            // Get Obstacles (ignoring self)
            var obstacles = GridManager.Instance.GetGlobalObstacles(unit);
            
            // Find Path
            var pathfinder = new Pathfinder();
            var path = pathfinder.FindPath(unit.GridPosition, targetGridPos, unit.UnitVolumeDefinition, obstacles);

            if (path != null)
            {
                Debug.Log($"[Tactics] Path found! Length: {path.Count}");
                MovementAction.SchedulePath(Timeline, unit, path);
            }
            else
            {
                Debug.LogWarning("[Tactics] No path found!");
            }
        }
    }
}
