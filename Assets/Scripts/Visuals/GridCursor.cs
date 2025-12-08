using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Grid;

namespace ProjectHero.Visuals
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class GridCursor : MonoBehaviour
    {
        public Color cursorColor = new Color(1, 1, 0, 0.5f);
        public float heightOffset = 0.15f;

        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Mesh _mesh;

        private void Awake()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();
            
            if (_meshRenderer.material == null)
            {
                _meshRenderer.material = new Material(Shader.Find("Sprites/Default"));
            }
            _meshRenderer.material.color = cursorColor;

            _mesh = new Mesh();
            _meshFilter.mesh = _mesh;
        }

        public void Show(TrianglePoint tile)
        {
            ShowVolume(new List<TrianglePoint> { tile });
        }

        public void ShowVolume(List<TrianglePoint> volume)
        {
            gameObject.SetActive(true);
            UpdateMesh(volume);
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }

        private void UpdateMesh(List<TrianglePoint> volume)
        {
            if (GridManager.Instance == null || volume == null || volume.Count == 0) return;

            // Each triangle needs 6 vertices (3 top, 3 bottom) and 6 indices (2 faces)
            int triCount = volume.Count;
            Vector3[] vertices = new Vector3[triCount * 6];
            int[] triangles = new int[triCount * 6];

            // Keep transform at zero and use world coordinates for vertices
            transform.position = Vector3.zero; 
            transform.rotation = Quaternion.identity;

            for (int t = 0; t < triCount; t++)
            {
                TrianglePoint tile = volume[t];
                Vector3[] corners = GridManager.Instance.GetTriangleCorners(tile);
                
                int vBase = t * 6;
                int tBase = t * 6;

                for (int i = 0; i < 3; i++)
                {
                    Vector3 pos = corners[i] + Vector3.up * heightOffset;
                    vertices[vBase + i] = pos;     // Top face vertices
                    vertices[vBase + i + 3] = pos; // Bottom face vertices
                }

                // Front Face (Top) - Uses vertices 0, 1, 2 (relative to vBase)
                triangles[tBase + 0] = vBase + 0;
                triangles[tBase + 1] = vBase + 1;
                triangles[tBase + 2] = vBase + 2;

                // Back Face (Bottom) - Uses vertices 3, 4, 5 (relative to vBase) with reverse winding
                // 3->5->4
                triangles[tBase + 3] = vBase + 3;
                triangles[tBase + 4] = vBase + 5;
                triangles[tBase + 5] = vBase + 4;
            }

            _mesh.Clear();
            _mesh.vertices = vertices;
            _mesh.triangles = triangles;
            _mesh.RecalculateNormals();
        }
    }
}
