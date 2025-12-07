using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Entities;

namespace ProjectHero.Visuals
{
    [RequireComponent(typeof(GridManager))]
    public class UnitVolumeRenderer : MonoBehaviour
    {
        public Color volumeColor = new Color(0, 1, 0, 0.3f);
        public float heightOffset = 0.1f;

        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Mesh _mesh;
        private GridManager _gridManager;

        private void Awake()
        {
            _gridManager = GetComponent<GridManager>();
            
            // Create a child object for visualization
            GameObject visObj = new GameObject("GlobalUnitVolumes");
            visObj.transform.SetParent(transform, false);
            
            _meshFilter = visObj.AddComponent<MeshFilter>();
            _meshRenderer = visObj.AddComponent<MeshRenderer>();
            
            _meshRenderer.material = new Material(Shader.Find("Sprites/Default"));
            _meshRenderer.material.color = volumeColor;
            
            _mesh = new Mesh();
            _meshFilter.mesh = _mesh;
        }

        private void LateUpdate()
        {
            UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            if (_gridManager == null) return;

            var units = _gridManager.GetAllUnits();
            if (units == null || units.Count == 0)
            {
                _mesh.Clear();
                return;
            }

            List<Vector3> vertices = new List<Vector3>();
            List<int> indices = new List<int>();

            foreach (var unit in units)
            {
                if (unit == null) continue;
                
                // Get current volume (Global TrianglePoints)
                var volume = unit.GetOccupiedTriangles(); // Or unit.CurrentVolume if cached
                if (volume == null) continue;

                foreach (var tri in volume)
                {
                    AddTriangleToMesh(tri, vertices, indices);
                }
            }

            _mesh.Clear();
            // Use 32-bit indices if needed
            if (vertices.Count > 65000) _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            
            _mesh.vertices = vertices.ToArray();
            _mesh.triangles = indices.ToArray();
            _mesh.RecalculateNormals();
        }

        private void AddTriangleToMesh(TrianglePoint tri, List<Vector3> vertices, List<int> indices)
        {
            Vector3[] corners = _gridManager.GetTriangleCorners(tri);
            
            // Transform to local space of the GridManager (assuming GridManager is at 0,0,0 usually, but let's be safe)
            // Actually, GridManager.GetTriangleCorners returns World Positions.
            // Our mesh is on a child of GridManager.
            // If GridManager moves, the mesh moves.
            // So we need to InverseTransformPoint from World to Local of the child object.
            
            Transform visTransform = _meshFilter.transform;

            int baseIndex = vertices.Count;
            
            for (int i = 0; i < 3; i++)
            {
                Vector3 worldPos = corners[i];
                worldPos.y += heightOffset;
                vertices.Add(visTransform.InverseTransformPoint(worldPos));
            }

            // Triangle indices
            indices.Add(baseIndex + 0);
            indices.Add(baseIndex + 1);
            indices.Add(baseIndex + 2);
        }
    }
}
