using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Entities;

namespace ProjectHero.Core.Grid
{
    public class GridManager : MonoBehaviour
    {
        public static GridManager Instance { get; private set; }

        [Header("Grid Settings")]
        public float HexSize = 1.0f; // Radius of the hex

        [Header("Ground Layer")]
        public LayerMask groundLayer;

        // --- Occupancy System ---
        // Maps every occupied TrianglePoint to the Unit that occupies it.
        // If value is null, it means it's occupied by a static obstacle (terrain).
        private Dictionary<TrianglePoint, CombatUnit> OccupancyMap = new Dictionary<TrianglePoint, CombatUnit>();

        // --- Reservation System ---
        // Temporary claims used for in-progress movement steps, without changing logical GridPosition.
        private Dictionary<TrianglePoint, CombatUnit> ReservationMap = new Dictionary<TrianglePoint, CombatUnit>();
        
        // Track all active units for global systems (like visualization)
        private HashSet<CombatUnit> _activeUnits = new HashSet<CombatUnit>();

        public void RegisterUnit(CombatUnit unit)
        {
            if (unit != null) _activeUnits.Add(unit);
        }

        public void UnregisterUnit(CombatUnit unit)
        {
            if (unit != null) _activeUnits.Remove(unit);
        }

        public HashSet<CombatUnit> GetAllUnits()
        {
            return _activeUnits;
        }

        public void RegisterOccupancy(CombatUnit owner, List<TrianglePoint> volume)
        {
            if (volume == null) return;
            foreach (var point in volume)
            {
                if (OccupancyMap.ContainsKey(point))
                {
                    // Debug.LogWarning($"Grid Collision: {point} is already occupied by {OccupancyMap[point]?.name ?? "Static Obstacle"}!");
                    // In a robust system, we might handle this (e.g. push logic), but for now we overwrite or ignore.
                    OccupancyMap[point] = owner;
                }
                else
                {
                    OccupancyMap.Add(point, owner);
                }
            }
        }

        public void RegisterReservation(CombatUnit owner, List<TrianglePoint> volume)
        {
            if (owner == null || volume == null) return;
            foreach (var point in volume)
            {
                ReservationMap[point] = owner;
            }
        }

        public void UnregisterReservation(CombatUnit owner, List<TrianglePoint> volume)
        {
            if (owner == null || volume == null) return;
            foreach (var point in volume)
            {
                if (ReservationMap.TryGetValue(point, out CombatUnit existing) && existing == owner)
                {
                    ReservationMap.Remove(point);
                }
            }
        }

        public void UnregisterOccupancy(List<TrianglePoint> volume)
        {
            if (volume == null) return;
            foreach (var point in volume)
            {
                if (OccupancyMap.ContainsKey(point))
                {
                    OccupancyMap.Remove(point);
                }
            }
        }

        public bool IsOccupied(TrianglePoint point, CombatUnit ignoreUnit = null)
        {
            if (OccupancyMap.TryGetValue(point, out CombatUnit owner))
            {
                // If owner is the unit asking (ignoreUnit), then it's NOT considered blocked (Self-Overlap)
                if (owner == ignoreUnit) return false;
                return true;
            }

            if (ReservationMap.TryGetValue(point, out CombatUnit reservingOwner))
            {
                if (reservingOwner == ignoreUnit) return false;
                return true;
            }
            return false;
        }

        public bool IsSpaceOccupied(List<TrianglePoint> space, CombatUnit ignoreUnit = null)
        {
            if (space == null) return false;
            foreach (var point in space)
            {
                if (IsOccupied(point, ignoreUnit)) return true;
            }
            return false;
        }

        // New: Get all units within a specific area (for AoE attacks)
        public HashSet<CombatUnit> GetUnitsInArea(List<TrianglePoint> area, CombatUnit ignoreUnit = null)
        {
            var units = new HashSet<CombatUnit>();
            if (area == null) return units;

            foreach (var point in area)
            {
                if (OccupancyMap.TryGetValue(point, out CombatUnit owner))
                {
                    if (owner != null && owner != ignoreUnit)
                    {
                        units.Add(owner);
                    }
                }
            }
            return units;
        }

        public HashSet<TrianglePoint> GetGlobalObstacles(CombatUnit ignoreUnit = null)
        {
            // This is expensive to generate every frame. 
            // Ideally, Pathfinder should call IsOccupied() directly.
            // But for compatibility with current Pathfinder signature:
            var set = new HashSet<TrianglePoint>();
            foreach (var kvp in OccupancyMap)
            {
                if (kvp.Value != ignoreUnit)
                {
                    set.Add(kvp.Key);
                }
            }

            foreach (var kvp in ReservationMap)
            {
                if (kvp.Value != ignoreUnit)
                {
                    set.Add(kvp.Key);
                }
            }
            return set;
        }
        // ------------------------

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        // Convert GridPoint (Doubled Coordinates) to World Position
        public Vector3 GridToWorld(Pathfinder.GridPoint gridPoint)
        {
            // Corrected for Triangular Grid (Doubled Coordinates)
            // HexSize is treated as the Side Length (L) of the triangle.
            
            float L = HexSize;
            
            // X-axis: Logical 1 unit = 0.5 * Side Length
            float x = gridPoint.X * (L * 0.5f);

            // Z-axis: Logical 1 unit = Triangle Height = (sqrt(3)/2) * Side Length
            float z = gridPoint.Y * (L * Mathf.Sqrt(3) / 2f);

            return new Vector3(x, 0, z);
        }

        public Vector3 GetTriangleCenter(TrianglePoint tri)
        {
            float L = HexSize;
            float height = L * Mathf.Sqrt(3) / 2f;
            
            // Get the position of the reference point (Edge Center)
            Vector3 edgeCenter = GridToWorld(new Pathfinder.GridPoint(tri.X, tri.Y));

            // Offset based on T (1 for Up, -1 for Down)
            // Centroid is at 1/3 of the height from the edge
            float zOffset = tri.T * (height / 3.0f);

            return new Vector3(edgeCenter.x, edgeCenter.y, edgeCenter.z + zOffset);
        }

        public Vector3[] GetTriangleCorners(TrianglePoint tri)
        {
             // Calculate corners for visualization
             // If T=1 (Up), vertices are (X-1, Y), (X+1, Y), (X, Y+1) relative to edge center?
             // Let's verify with the (1,0) example.
             // Edge Center (1,0). T=1. Vertices: (0,0), (2,0), (1,1).
             // (0,0) = (X-1, Y)
             // (2,0) = (X+1, Y)
             // (1,1) = (X, Y+1)
             
             // If T=-1 (Down) at (1,0). Vertices: (0,0), (2,0), (1,-1).
             // (0,0) = (X-1, Y)
             // (2,0) = (X+1, Y)
             // (1,-1) = (X, Y-1)

             var p1 = GridToWorld(new Pathfinder.GridPoint(tri.X - 1, tri.Y));
             var p2 = GridToWorld(new Pathfinder.GridPoint(tri.X + 1, tri.Y));
             var p3 = GridToWorld(new Pathfinder.GridPoint(tri.X, tri.Y + tri.T));

             return new Vector3[] { GetGroundPosition(p1), GetGroundPosition(p2), GetGroundPosition(p3) };
        }

        // Convert World Position to GridPoint (Doubled Coordinates)
        public Pathfinder.GridPoint WorldToGrid(Vector3 worldPos)
        {
            float L = HexSize;

            // Reverse the formulas:
            // Grid.Y = World.Z / TriangleHeight
            int y = Mathf.RoundToInt(worldPos.z / (L * Mathf.Sqrt(3) / 2f));

            // Grid.X = World.X / (0.5 * L)
            int x = Mathf.RoundToInt(worldPos.x / (L * 0.5f));

            // Enforce the constraint that x and y must have the same parity (x+y is even) for doubled coords
            // (x + y) % 2 == 0
            
            if ((x + y) % 2 != 0)
            {
                // If invalid, nudge to nearest valid. 
                // Usually just adding 1 to x fixes it.
                x += 1; 
            }

            return new Pathfinder.GridPoint(x, y);
        }

        // Helper to convert World Position to the specific Triangle (X, Y, T)
        public TrianglePoint WorldToTriangle(Vector3 worldPos)
        {
            // 1. Find nearest Grid Vertex (Even Parity)
            var v = WorldToGrid(worldPos);
            
            // 2. The point is in one of the 6 triangles surrounding this vertex.
            // We define the 6 candidates (Edge Centers + Orientation).
            // Valid TrianglePoints have Odd parity (X+Y is Odd).
            
            var candidates = new TrianglePoint[]
            {
                new TrianglePoint(v.X + 1, v.Y, 1),  // Right-Up
                new TrianglePoint(v.X + 1, v.Y, -1), // Right-Down
                new TrianglePoint(v.X - 1, v.Y, 1),  // Left-Up
                new TrianglePoint(v.X - 1, v.Y, -1), // Left-Down
                new TrianglePoint(v.X, v.Y + 1, -1), // Top-Down (Base at Y+1)
                new TrianglePoint(v.X, v.Y - 1, 1)   // Bottom-Up (Base at Y-1)
            };

            TrianglePoint bestTri = candidates[0];
            float minDst = float.MaxValue;

            // We ignore Y height (3D) for distance check, only XZ
            Vector3 flatWorld = new Vector3(worldPos.x, 0, worldPos.z);

            foreach (var tri in candidates)
            {
                Vector3 center = GetTriangleCenter(tri);
                Vector3 flatCenter = new Vector3(center.x, 0, center.z);
                
                float dst = Vector3.SqrMagnitude(flatWorld - flatCenter);
                if (dst < minDst)
                {
                    minDst = dst;
                    bestTri = tri;
                }
            }

            return bestTri;
        }

        internal static Vector3 GetGroundPosition(Vector3 pos)
        {
            if (UnityEngine.Physics.Raycast(new Vector3(pos.x, 100f, pos.z), Vector3.down, out RaycastHit hit, 200f, Instance.groundLayer))
            {
                pos.y = hit.point.y;
            }
            return pos;
        }

        private void OnDrawGizmos()
        {
            if (OccupancyMap == null) return;

            foreach (var kvp in OccupancyMap)
            {
                TrianglePoint tri = kvp.Key;
                CombatUnit unit = kvp.Value;

                Gizmos.color = unit != null ? new Color(1, 0, 0, 0.5f) : new Color(0.5f, 0.5f, 0.5f, 0.5f);
                
                Vector3[] corners = GetTriangleCorners(tri);
                if (corners.Length == 3)
                {
                    Gizmos.DrawLine(corners[0], corners[1]);
                    Gizmos.DrawLine(corners[1], corners[2]);
                    Gizmos.DrawLine(corners[2], corners[0]);
                    
                    // Fill (Optional, using mesh or just lines)
                    // Gizmos.DrawMesh...
                }
            }
        }

        //public List<TrianglePoint> GetTrianglesAroundVertex(Pathfinder.GridPoint vertex)
        //{
        //    if ((vertex.X + vertex.Y) % 2 != 0)
        //    {
        //        Debug.LogError($"GridPoint {vertex.X},{vertex.Y} is not a valid Vertex (Parity must be Even).");
        //        return new List<TrianglePoint>();
        //    }

        //    List<TrianglePoint> triangles = new List<TrianglePoint>();

        //    triangles.Add(new TrianglePoint(vertex.X + 1, vertex.Y, 1));
        //    triangles.Add(new TrianglePoint(vertex.X + 1, vertex.Y, -1));
        //    triangles.Add(new TrianglePoint(vertex.X - 1, vertex.Y, 1));
        //    triangles.Add(new TrianglePoint(vertex.X - 1, vertex.Y, -1));
        //    triangles.Add(new TrianglePoint(vertex.X, vertex.Y + 1, -1));
        //    triangles.Add(new TrianglePoint(vertex.X, vertex.Y - 1, 1));

        //    return triangles;
        //}
    }
}