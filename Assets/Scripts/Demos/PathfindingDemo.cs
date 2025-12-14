using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Input;
using ProjectHero.Visuals;
using ProjectHero.Core.Gameplay;

namespace ProjectHero.Demos
{
    public class PathfindingDemo : MonoBehaviour
    {
        public CombatUnit Mover;
        public CombatUnit Blocker;
        public Transform Target;
        public BattleTimeline Timeline;

        private List<Pathfinder.GridPoint> currentPath;

        void Start()
        {
            if (Timeline == null) Timeline = gameObject.AddComponent<BattleTimeline>();

            // Ensure InputManager exists
            if (InputManager.Instance == null)
            {
                var inputObj = new GameObject("InputManager");
                inputObj.AddComponent<InputManager>();
            }

            // Ensure TacticsController exists (The Brain)
            if (Object.FindAnyObjectByType<TacticsController>() == null)
            {
                var tacticsObj = new GameObject("TacticsController");
                var controller = tacticsObj.AddComponent<TacticsController>();
                controller.Timeline = Timeline;
            }

            // Ensure Units have Colliders for clicking
            EnsureCollider(Mover);
            EnsureCollider(Blocker);

            // Setup Visuals if missing
            SetupVisuals();
        }

        void EnsureCollider(CombatUnit unit)
        {
            if (unit != null && unit.GetComponent<Collider>() == null)
            {
                var col = unit.gameObject.AddComponent<CapsuleCollider>();
                col.height = 2.0f;
                col.radius = 0.5f;
                col.center = Vector3.up * 1.0f;
            }
        }

        // OnDestroy removed as we no longer subscribe to events here

        void SetupVisuals()
        {
            // Add GridVisuals to GridManager if missing
            if (GridManager.Instance != null)
            {
                if (GridManager.Instance.GetComponent<GridVisuals>() == null)
                    GridManager.Instance.gameObject.AddComponent<GridVisuals>();
                
                // Add UnitVolumeRenderer to GridManager if missing (Centralized Volume Rendering)
                if (GridManager.Instance.GetComponent<UnitVolumeRenderer>() == null)
                    GridManager.Instance.gameObject.AddComponent<UnitVolumeRenderer>();
            }
        }

        // HandleUnitClick and HandleTileClick removed (Moved to TacticsController)

        void Update()
        {
            if (Timeline != null && Input.GetKeyDown(KeyCode.P))
            {
                Timeline.SetPaused(!Timeline.Paused);
                Debug.Log($"[Demo] Timeline Paused = {Timeline.Paused}");
            }

            if (Timeline != null && Timeline.Paused)
            {
                // Simulation paused; UI and editing still work.
                return;
            }

            if (Input.GetKeyDown(KeyCode.Space))
            {
                // Legacy test
                if (Mover != null && Target != null)
                    RunPathfinding(Mover, GridManager.Instance.WorldToGrid(Target.position));
            }

            // --- Real-time Obstacle Control ---
            // If we move the Blocker's transform in the Scene View, update its Grid Logic.
            SyncUnitLogic(Blocker);
            SyncUnitLogic(Mover); // Also allow dragging the Mover
            // ----------------------------------

            // Simple timer for demo
            Timeline.AdvanceTime(Time.time);
        }

        void SyncUnitLogic(CombatUnit unit)
        {
            if (unit != null && GridManager.Instance != null)
            {
                var currentGridPos = GridManager.Instance.WorldToGrid(unit.transform.position);
                
                // If the transform has moved to a new tile
                if (!currentGridPos.Equals(unit.GridPosition))
                {
                    // Update Logic to match Visuals
                    unit.SetGridPosition(currentGridPos);
                    // Debug.Log($"[Demo] Updated {unit.name} position to {currentGridPos}");
                }
            }
        }

        void RunPathfinding(CombatUnit unit, Pathfinder.GridPoint targetPos)
        {
            if (unit == null) return;

            var start = unit.GridPosition;
            var end = targetPos;

            Debug.Log($"Pathfinding from {start} to {end}");

            // Get Obstacles (ignoring self)
            var obstacles = GridManager.Instance.GetGlobalObstacles(unit);
            
            // Find Path
            var pathfinder = new Pathfinder();
            currentPath = pathfinder.FindPath(start, end, unit.UnitVolumeDefinition, obstacles);

            if (currentPath != null)
            {
                Debug.Log($"Path found! Length: {currentPath.Count}");
                ActionScheduler.ScheduleMove(Timeline, unit, currentPath);
            }
            else
            {
                Debug.LogWarning("No path found!");
            }
        }

        //private void OnDrawGizmos()
        //{
        //    if (currentPath != null && GridManager.Instance != null)
        //    {
        //        Gizmos.color = Color.green;
        //        for (int i = 0; i < currentPath.Count - 1; i++)
        //        {
        //            Vector3 p1 = GridManager.Instance.GridToWorld(currentPath[i]);
        //            Vector3 p2 = GridManager.Instance.GridToWorld(currentPath[i+1]);
        //            Gizmos.DrawLine(p1 + Vector3.up, p2 + Vector3.up);
        //            Gizmos.DrawSphere(p1 + Vector3.up, 0.2f);
        //        }
        //        if (currentPath.Count > 0)
        //        {
        //            Vector3 last = GridManager.Instance.GridToWorld(currentPath[currentPath.Count - 1]);
        //            Gizmos.DrawSphere(last + Vector3.up, 0.2f);
        //        }
        //    }
        //}
    }
}
