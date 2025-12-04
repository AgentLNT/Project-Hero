using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Pathfinding;

namespace ProjectHero.Core.Grid
{
    public class GridManager : MonoBehaviour
    {
        public static GridManager Instance { get; private set; }

        [Header("Grid Settings")]
        public float HexSize = 1.0f; // Radius of the hex

        [Header("Ground Layer")]
        public LayerMask groundLayer;

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

        internal static Vector3 GetGroundPosition(Vector3 pos)
        {
            if (UnityEngine.Physics.Raycast(new Vector3(pos.x, 100f, pos.z), Vector3.down, out RaycastHit hit, 200f, Instance.groundLayer))
            {
                pos.y = hit.point.y;
            }
            return pos;
        }

        public List<TrianglePoint> GetTrianglesAroundVertex(Pathfinder.GridPoint vertex)
        {
            if ((vertex.X + vertex.Y) % 2 != 0)
            {
                Debug.LogError($"GridPoint {vertex.X},{vertex.Y} is not a valid Vertex (Parity must be Even).");
                return new List<TrianglePoint>();
            }

            List<TrianglePoint> triangles = new List<TrianglePoint>();

            triangles.Add(new TrianglePoint(vertex.X + 1, vertex.Y, 1));
            triangles.Add(new TrianglePoint(vertex.X + 1, vertex.Y, -1));
            triangles.Add(new TrianglePoint(vertex.X - 1, vertex.Y, 1));
            triangles.Add(new TrianglePoint(vertex.X - 1, vertex.Y, -1));
            triangles.Add(new TrianglePoint(vertex.X, vertex.Y + 1, -1));
            triangles.Add(new TrianglePoint(vertex.X, vertex.Y - 1, 1));

            return triangles;
        }
    }
}
