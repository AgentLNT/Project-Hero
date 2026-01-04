using System.Collections.Generic;
using UnityEngine;
using ProjectHero.Core.Grid;

namespace ProjectHero.Visuals
{
    public sealed class NextActionPreviewRenderer : MonoBehaviour
    {
        [Header("Move Preview")]
        public Color moveColor = new Color(1f, 1f, 0f, 0.25f);
        public float moveHeightOffset = 0.18f;

        [Header("Render Order")]
        public int sortingOrderBase = 200;
        public int renderQueueBase = 3100;

        [Header("Attack Preview")]
        public Color attackColor = new Color(1f, 0f, 0f, 0.25f);
        public float attackHeightOffset = 0.20f;

        private GridManager _grid;

        private Mesh _moveMesh;
        private Mesh _attackMesh;

        private MeshFilter _moveFilter;
        private MeshRenderer _moveRenderer;

        private MeshFilter _attackFilter;
        private MeshRenderer _attackRenderer;

        private void Awake()
        {
            _grid = GridManager.Instance;

            var moveObj = new GameObject("NextActionMovePreview");
            moveObj.transform.SetParent(transform, false);
            _moveFilter = moveObj.AddComponent<MeshFilter>();
            _moveRenderer = moveObj.AddComponent<MeshRenderer>();
            _moveRenderer.material = new Material(Shader.Find("Sprites/Default"));
            _moveRenderer.material.renderQueue = renderQueueBase;
            _moveRenderer.material.color = moveColor;
            _moveRenderer.sortingOrder = sortingOrderBase;
            _moveMesh = new Mesh { name = "NextActionMovePreviewMesh" };
            _moveFilter.mesh = _moveMesh;

            var attackObj = new GameObject("NextActionAttackPreview");
            attackObj.transform.SetParent(transform, false);
            _attackFilter = attackObj.AddComponent<MeshFilter>();
            _attackRenderer = attackObj.AddComponent<MeshRenderer>();
            _attackRenderer.material = new Material(Shader.Find("Sprites/Default"));
            _attackRenderer.material.renderQueue = renderQueueBase + 1;
            _attackRenderer.material.color = attackColor;
            _attackRenderer.sortingOrder = sortingOrderBase + 1;
            _attackMesh = new Mesh { name = "NextActionAttackPreviewMesh" };
            _attackFilter.mesh = _attackMesh;

            SetVolumes(null, null);
        }

        private void LateUpdate()
        {
            if (_moveRenderer != null && _moveRenderer.material != null)
            {
                if (_moveRenderer.material.color != moveColor) _moveRenderer.material.color = moveColor;
                if (_moveRenderer.material.renderQueue != renderQueueBase) _moveRenderer.material.renderQueue = renderQueueBase;
                if (_moveRenderer.sortingOrder != sortingOrderBase) _moveRenderer.sortingOrder = sortingOrderBase;
            }
            if (_attackRenderer != null && _attackRenderer.material != null)
            {
                if (_attackRenderer.material.color != attackColor) _attackRenderer.material.color = attackColor;
                if (_attackRenderer.material.renderQueue != renderQueueBase + 1) _attackRenderer.material.renderQueue = renderQueueBase + 1;
                if (_attackRenderer.sortingOrder != sortingOrderBase + 1) _attackRenderer.sortingOrder = sortingOrderBase + 1;
            }
        }

        public void SetVolumes(IReadOnlyCollection<TrianglePoint> moveVolume, IReadOnlyCollection<TrianglePoint> attackVolume)
        {
            UpdateLayer(_moveMesh, _moveFilter != null ? _moveFilter.transform : null, moveVolume, moveHeightOffset);
            if (_moveFilter != null) _moveFilter.gameObject.SetActive(moveVolume != null && moveVolume.Count > 0);

            UpdateLayer(_attackMesh, _attackFilter != null ? _attackFilter.transform : null, attackVolume, attackHeightOffset);
            if (_attackFilter != null) _attackFilter.gameObject.SetActive(attackVolume != null && attackVolume.Count > 0);
        }

        private void UpdateLayer(Mesh mesh, Transform visTransform, IReadOnlyCollection<TrianglePoint> volume, float heightOffset)
        {
            if (mesh == null) return;
            if (_grid == null) _grid = GridManager.Instance;
            if (_grid == null || visTransform == null || volume == null || volume.Count == 0)
            {
                mesh.Clear();
                return;
            }

            var vertices = new List<Vector3>(volume.Count * 3);
            var indices = new List<int>(volume.Count * 3);

            foreach (var tri in volume)
            {
                Vector3[] corners = _grid.GetTriangleCorners(tri);

                int baseIndex = vertices.Count;
                for (int i = 0; i < 3; i++)
                {
                    Vector3 worldPos = corners[i];
                    worldPos.y += heightOffset;
                    vertices.Add(visTransform.InverseTransformPoint(worldPos));
                }

                indices.Add(baseIndex + 0);
                indices.Add(baseIndex + 1);
                indices.Add(baseIndex + 2);
            }

            mesh.Clear();
            if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = vertices.ToArray();
            mesh.triangles = indices.ToArray();
            mesh.RecalculateNormals();
        }
    }
}
