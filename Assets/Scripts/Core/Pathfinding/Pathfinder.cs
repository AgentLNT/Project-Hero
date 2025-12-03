using System.Collections.Generic;
using UnityEngine;

namespace ProjectHero.Core.Pathfinding
{
    public class Pathfinder
    {
        // Represents a coordinate on the triangular grid
        [System.Serializable]
        public struct GridPoint
        {
            public int X;
            public int Y;

            public GridPoint(int x, int y) { X = x; Y = y; }
            
            public override bool Equals(object obj) => obj is GridPoint other && X == other.X && Y == other.Y;
            public override int GetHashCode() => (X, Y).GetHashCode();
        }

        // Added obstacles parameter to support dynamic pathfinding around blocked tiles
        public List<GridPoint> FindPath(GridPoint start, GridPoint goal, HashSet<GridPoint> obstacles = null)
        {
            var openSet = new List<GridPoint> { start };
            var cameFrom = new Dictionary<GridPoint, GridPoint>();
            var gScore = new Dictionary<GridPoint, float> { [start] = 0 };
            var fScore = new Dictionary<GridPoint, float> { [start] = Heuristic(start, goal) };

            while (openSet.Count > 0)
            {
                // Get node with lowest fScore
                GridPoint current = openSet[0];
                float lowestF = fScore.ContainsKey(current) ? fScore[current] : float.MaxValue;

                foreach (var node in openSet)
                {
                    float f = fScore.ContainsKey(node) ? fScore[node] : float.MaxValue;
                    if (f < lowestF)
                    {
                        current = node;
                        lowestF = f;
                    }
                }

                if (current.Equals(goal))
                {
                    return ReconstructPath(cameFrom, current);
                }

                openSet.Remove(current);

                foreach (var neighbor in GetNeighbors(current))
                {
                    // If the neighbor is an obstacle, skip it
                    if (obstacles != null && obstacles.Contains(neighbor))
                        continue;

                    // Assume cost is 1 for now
                    float tentativeG = gScore[current] + 1;

                    if (!gScore.ContainsKey(neighbor) || tentativeG < gScore[neighbor])
                    {
                        cameFrom[neighbor] = current;
                        gScore[neighbor] = tentativeG;
                        fScore[neighbor] = gScore[neighbor] + Heuristic(neighbor, goal);

                        if (!openSet.Contains(neighbor))
                        {
                            openSet.Add(neighbor);
                        }
                    }
                }
            }

            return null; // No path found
        }

        private float Heuristic(GridPoint a, GridPoint b)
        {
            // Hex distance for doubled coordinates
            // dy + max(0, (dx - dy) / 2)
            int dx = Mathf.Abs(a.X - b.X);
            int dy = Mathf.Abs(a.Y - b.Y);
            return dy + Mathf.Max(0, (dx - dy) / 2);
        }

        private List<GridPoint> GetNeighbors(GridPoint p)
        {
            // 12-way movement on triangular grid vertices
            // Simplified: returning 6 hex neighbors for now as a placeholder for full 12-way
            var neighbors = new List<GridPoint>();
            
            // Standard Hex neighbors in doubled coords
            neighbors.Add(new GridPoint(p.X + 2, p.Y));
            neighbors.Add(new GridPoint(p.X - 2, p.Y));
            neighbors.Add(new GridPoint(p.X + 1, p.Y + 1));
            neighbors.Add(new GridPoint(p.X - 1, p.Y + 1));
            neighbors.Add(new GridPoint(p.X + 1, p.Y - 1));
            neighbors.Add(new GridPoint(p.X - 1, p.Y - 1));

            return neighbors;
        }

        private List<GridPoint> ReconstructPath(Dictionary<GridPoint, GridPoint> cameFrom, GridPoint current)
        {
            var path = new List<GridPoint> { current };
            while (cameFrom.ContainsKey(current))
            {
                current = cameFrom[current];
                path.Add(current);
            }
            path.Reverse();
            return path;
        }
    }
}
