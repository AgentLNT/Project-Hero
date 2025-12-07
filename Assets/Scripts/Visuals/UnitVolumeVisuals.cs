using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;

namespace ProjectHero.Visuals
{
    [RequireComponent(typeof(CombatUnit))]
    public class UnitVolumeVisuals : MonoBehaviour
    {
        public Color volumeColor = new Color(0, 1, 0, 0.3f);
        public float heightOffset = 0.1f;

        private CombatUnit _unit;
        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Mesh _mesh;

        private void Awake()
        {
            _unit = GetComponent<CombatUnit>();
            
            // Create a child object for visualization to keep the main object clean
            GameObject visObj = new GameObject("VolumeVisuals");
            visObj.transform.SetParent(transform, false);
            
            _meshFilter = visObj.AddComponent<MeshFilter>();
            _meshRenderer = visObj.AddComponent<MeshRenderer>();
            
            _meshRenderer.material = new Material(Shader.Find("Sprites/Default")); // Simple transparent shader
            _meshRenderer.material.color = volumeColor;
            
            _mesh = new Mesh();
            _meshFilter.mesh = _mesh;
        }

        private void Update()
        {
            UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            if (_unit == null || _unit.GetOccupiedTriangles() == null) return;

            List<Vector3> vertices = new List<Vector3>();
            List<int> indices = new List<int>();

            // The volume is a list of TrianglePoints.
            // These points are in Global coordinates (if CurrentVolume returns global).
            // Let's check CombatUnit.cs to be sure.
            // Usually CurrentVolume is calculated based on position and rotation.
            
            foreach (var tri in _unit.GetOccupiedTriangles())
            {
                AddTriangleToMesh(tri, vertices, indices);
            }

            _mesh.Clear();
            _mesh.vertices = vertices.ToArray();
            _mesh.triangles = indices.ToArray();
            _mesh.RecalculateNormals();
        }

        private void AddTriangleToMesh(TrianglePoint tri, List<Vector3> vertices, List<int> indices)
        {
            Vector3[] corners = GridManager.Instance.GetTriangleCorners(tri);
            
            // Convert to local space if the visual object is child of unit?
            // No, GridManager returns World positions.
            // If the visual object is a child, we should set its local position to 0,0,0 (which we did)
            // But if we put world vertices into the mesh, the mesh will move with the parent (double movement).
            // So we need to InverseTransformPoint.
            
            Transform visTransform = _meshFilter.transform;

            int baseIndex = vertices.Count;
            
            for (int i = 0; i < 3; i++)
            {
                Vector3 worldPos = corners[i];
                worldPos.y += heightOffset;
                vertices.Add(visTransform.InverseTransformPoint(worldPos));
            }

            // Triangle indices (Clockwise or Counter-Clockwise?)
            // Unity is usually Clockwise.
            indices.Add(baseIndex + 0);
            indices.Add(baseIndex + 1);
            indices.Add(baseIndex + 2);
        }
    }
}
