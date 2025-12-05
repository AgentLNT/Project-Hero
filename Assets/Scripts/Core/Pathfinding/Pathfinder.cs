using System.Collections.Generic;
using UnityEngine;
using ProjectHero.Core.Grid;

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
            public override string ToString() => $"({X}, {Y})";
        }

        // Updated to support Volume-based Collision
        public List<GridPoint> FindPath(GridPoint start, GridPoint goal, UnitVolume unitVolume = null, HashSet<TrianglePoint> volumeObstacles = null)
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
                    // --- Volume Collision Check ---
                    if (volumeObstacles != null && unitVolume != null)
                    {
                        // 1. Determine Facing Direction at destination
                        // Note: We use the direction FROM current TO neighbor
                        GridDirection facing = GridMath.GetDirection(current, neighbor);

                        // 2. Get Occupied Triangles for that orientation
                        var relativeVol = unitVolume.GetVolumeFor(facing);

                        // 3. Check if any triangle is blocked
                        bool isBlocked = false;
                        foreach (var rel in relativeVol)
                        {
                            // Absolute Position = NeighborPos + Relative
                            var absTri = new TrianglePoint(neighbor.X + rel.X, neighbor.Y + rel.Y, rel.T);
                            if (volumeObstacles.Contains(absTri))
                            {
                                isBlocked = true;
                                break;
                            }
                        }

                        if (isBlocked) continue; // Skip this neighbor
                    }
                    // ------------------------------

                    // Calculate Movement Cost
                    // Primary Neighbor (dx=2 or dx=1,dy=1) -> Cost 1
                    // Secondary Neighbor (dx=3,dy=1 or dx=0,dy=2) -> Cost 2
                    
                    int dx = Mathf.Abs(neighbor.X - current.X);
                    int dy = Mathf.Abs(neighbor.Y - current.Y);
                    
                    // Standard step is roughly dx+dy <= 2 in doubled coords logic?
                    // Let's be explicit:
                    // Primary: (+-2, 0) or (+-1, +-1). Max coordinate diff is 2.
                    // Secondary: (+-3, +-1) or (0, +-2). Max coordinate diff is 3.
                    
                    float stepCost = (dx > 2 || dy > 1) ? 2.0f : 1.0f;

                    float tentativeG = gScore[current] + stepCost;

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
            var neighbors = new List<GridPoint>();
            
            // 1. Primary Neighbors (Distance = 1 step, Cost = 1)
            // Standard Hex neighbors in doubled coords
            neighbors.Add(new GridPoint(p.X + 2, p.Y));
            neighbors.Add(new GridPoint(p.X - 2, p.Y));
            neighbors.Add(new GridPoint(p.X + 1, p.Y + 1));
            neighbors.Add(new GridPoint(p.X - 1, p.Y + 1));
            neighbors.Add(new GridPoint(p.X + 1, p.Y - 1));
            neighbors.Add(new GridPoint(p.X - 1, p.Y - 1));

            // 2. Secondary Neighbors (Distance = sqrt(3), Cost = 2)
            // These are the "Corner" directions (30, 90, 150...)
            // We allow direct movement to them (skipping the intermediate step),
            // but the cost is equal to taking 2 standard steps.
            
            // EastNorth (30 deg)
            neighbors.Add(new GridPoint(p.X + 3, p.Y + 1));
            // North (90 deg)
            neighbors.Add(new GridPoint(p.X, p.Y + 2));
            // WestNorth (150 deg)
            neighbors.Add(new GridPoint(p.X - 3, p.Y + 1));
            // WestSouth (210 deg)
            neighbors.Add(new GridPoint(p.X - 3, p.Y - 1));
            // South (270 deg)
            neighbors.Add(new GridPoint(p.X, p.Y - 2));
            // EastSouth (330 deg)
            neighbors.Add(new GridPoint(p.X + 3, p.Y - 1));

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
