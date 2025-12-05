using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Timeline;

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

            // Initialize Positions
            // Note: Actual initialization happens in CombatUnit.Start(), 
            // so we just set the InitialGridPosition here if needed, 
            // but usually we set it in the Inspector.
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Space))
            {
                RunPathfinding();
            }

            // Simple timer for demo
            Timeline.AdvanceTime(Time.time);
        }

        void RunPathfinding()
        {
            if (Mover == null || Target == null) return;

            var start = Mover.GridPosition;
            var end = GridManager.Instance.WorldToGrid(Target.position);

            Debug.Log($"Pathfinding from {start} to {end}");

            // Get Obstacles (ignoring self)
            var obstacles = GridManager.Instance.GetGlobalObstacles(Mover);
            
            // Find Path
            var pathfinder = new Pathfinder();
            currentPath = pathfinder.FindPath(start, end, Mover.UnitVolumeDefinition, obstacles);

            if (currentPath != null)
            {
                Debug.Log($"Path found! Length: {currentPath.Count}");
                MovementAction.SchedulePath(Timeline, Mover, currentPath);
            }
            else
            {
                Debug.LogWarning("No path found!");
            }
        }

        private void OnDrawGizmos()
        {
            if (currentPath != null && GridManager.Instance != null)
            {
                Gizmos.color = Color.green;
                for (int i = 0; i < currentPath.Count - 1; i++)
                {
                    Vector3 p1 = GridManager.Instance.GridToWorld(currentPath[i]);
                    Vector3 p2 = GridManager.Instance.GridToWorld(currentPath[i+1]);
                    Gizmos.DrawLine(p1 + Vector3.up, p2 + Vector3.up);
                    Gizmos.DrawSphere(p1 + Vector3.up, 0.2f);
                }
                if (currentPath.Count > 0)
                {
                    Vector3 last = GridManager.Instance.GridToWorld(currentPath[currentPath.Count - 1]);
                    Gizmos.DrawSphere(last + Vector3.up, 0.2f);
                }
            }
        }
    }
}
