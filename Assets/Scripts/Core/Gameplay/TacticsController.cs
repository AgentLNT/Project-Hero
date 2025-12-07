using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Input;

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

        private CombatUnit _selectedUnit;

        private void Start()
        {
            if (Timeline == null) Timeline = Object.FindAnyObjectByType<BattleTimeline>();
            if (Timeline == null)
            {
                Debug.LogWarning("TacticsController: No BattleTimeline found in scene.");
            }

            // Subscribe to Input Events
            if (InputManager.Instance != null)
            {
                InputManager.Instance.OnUnitClick += HandleUnitClick;
                InputManager.Instance.OnTileClick += HandleTileClick;
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
                InputManager.Instance.OnTileClick -= HandleTileClick;
            }
        }

        private void HandleUnitClick(CombatUnit unit)
        {
            _selectedUnit = unit;
            Debug.Log($"[Tactics] Selected Unit: {unit.name}");
            // TODO: Show selection UI / Highlight
        }

        private void HandleTileClick(TrianglePoint tile)
        {
            if (_selectedUnit == null)
            {
                Debug.LogWarning("[Tactics] No unit selected. Click a unit first.");
                return;
            }

            // Basic Move Command
            IssueMoveCommand(_selectedUnit, tile);
        }

        private void IssueMoveCommand(CombatUnit unit, TrianglePoint targetTile)
        {
            if (Timeline == null) return;

            // Convert TrianglePoint to GridPoint (Vertex) for Pathfinding
            var targetGridPos = new Pathfinder.GridPoint(targetTile.X, targetTile.Y);
            
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
