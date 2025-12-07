using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;

namespace ProjectHero.Visuals
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class GridVisuals : MonoBehaviour
    {
        public int width = 20;
        public int height = 20;
        public Color gridColor = Color.white;

        private void Start()
        {
            GenerateGridMesh();
        }

        [ContextMenu("Generate Grid")]
        public void GenerateGridMesh()
        {
            var gridManager = GridManager.Instance;
            if (gridManager == null) gridManager = FindFirstObjectByType<GridManager>();
            
            if (gridManager == null)
            {
                Debug.LogWarning("GridManager instance not found.");
                return;
            }

            List<Vector3> vertices = new List<Vector3>();
            List<int> indices = new List<int>();
            Dictionary<Vector2Int, int> vertexMap = new Dictionary<Vector2Int, int>();

            int halfWidth = width / 2;
            int halfHeight = height / 2;

            // 1. Generate Unique Vertices
            // Instead of generating triangles, we generate the unique vertices of the grid.
            // This avoids recalculating world positions and raycasts for shared vertices (approx 6x reduction).
            for (int x = -halfWidth; x <= halfWidth; x++)
            {
                for (int y = -halfHeight; y <= halfHeight; y++)
                {
                    // Parity Check: In Doubled Coordinates, (x+y)%2 == 0 implies a Vertex.
                    // If (x+y)%2 != 0, it's a triangle center, which we don't need for the mesh vertices.
                    if ((x + y) % 2 != 0) continue;

                    Vector3 worldPos = gridManager.GridToWorld(new Pathfinder.GridPoint(x, y));
                    
                    // Snap to ground
                    // Replicating GetGroundPosition logic to avoid internal access issues and support Editor time execution
                    if (Physics.Raycast(new Vector3(worldPos.x, 100f, worldPos.z), Vector3.down, out RaycastHit hit, 200f, gridManager.groundLayer))
                    {
                        worldPos.y = hit.point.y;
                    }

                    worldPos += Vector3.up * 0.05f; // Slight offset to avoid z-fighting

                    vertexMap[new Vector2Int(x, y)] = vertices.Count;
                    vertices.Add(worldPos);
                }
            }

            // 2. Generate Lines (Connections)
            // Iterate through our valid vertices and connect them to neighbors.
            // We only connect in positive directions (Right, Up-Right, Up-Left) to avoid drawing edges twice.
            foreach (var kvp in vertexMap)
            {
                Vector2Int gridPos = kvp.Key;
                int currentIndex = kvp.Value;

                // Connection 1: Horizontal Right -> (x+2, y)
                TryConnect(gridPos.x + 2, gridPos.y, currentIndex, vertexMap, indices);

                // Connection 2: Diagonal Up-Right (60 deg) -> (x+1, y+1)
                TryConnect(gridPos.x + 1, gridPos.y + 1, currentIndex, vertexMap, indices);

                // Connection 3: Diagonal Up-Left (120 deg) -> (x-1, y+1)
                TryConnect(gridPos.x - 1, gridPos.y + 1, currentIndex, vertexMap, indices);
            }

            Mesh mesh = new Mesh();
            // Use 32-bit indices if we have many vertices
            if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            
            mesh.vertices = vertices.ToArray();
            mesh.SetIndices(indices.ToArray(), MeshTopology.Lines, 0);
            
            GetComponent<MeshFilter>().mesh = mesh;
            
            // Setup Material
            var renderer = GetComponent<MeshRenderer>();
            if (renderer.material == null)
            {
                renderer.material = new Material(Shader.Find("Sprites/Default"));
                renderer.material.color = gridColor;
            }
        }

        private void TryConnect(int x, int y, int fromIndex, Dictionary<Vector2Int, int> map, List<int> indices)
        {
            if (map.TryGetValue(new Vector2Int(x, y), out int toIndex))
            {
                indices.Add(fromIndex);
                indices.Add(toIndex);
            }
        }
    }
}
