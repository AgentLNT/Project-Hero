using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Definitions;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectHero.UnityView
{
    /// <summary>Presentation coordinates and picking only; holds no LogicGrid write capability.</summary>
    public sealed class GridView : MonoBehaviour
    {
        [SerializeField, Min(0.001f)] private float _sideLength = 1;
        private GridBoundaryDefinition _boundary;
        private Mesh _backdropMesh;
        private Material _backdropMaterial;
        private GameObject _backdrop;
        private Mesh _attackMesh;
        private Material _attackMaterial;
        private GameObject _attackPreview;
        /// <summary>Presentation-only mouse direction mapping; Logic still uses the canonical integer direction tables.</summary>
        public static GridDirection DirectionTo(GridPoint from, GridPoint to)
        {
            float angle = Mathf.Atan2((to.Y - from.Y) * Mathf.Sqrt(3), to.X - from.X) * Mathf.Rad2Deg;
            return (GridDirection)((Mathf.RoundToInt(angle / 30f) + GridDirectionInfo.DirectionCount) % GridDirectionInfo.DirectionCount);
        }
        public void ShowAttackPreview(IReadOnlyList<TrianglePoint> triangles)
        {
            if (_attackPreview == null)
            {
                _attackPreview = new GameObject("AttackDirectionPreview", typeof(MeshFilter), typeof(MeshRenderer));
                _attackPreview.transform.SetParent(transform, false);
                _attackMesh = new Mesh { name = "ReadOnlyAttackPattern", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                _attackPreview.GetComponent<MeshFilter>().sharedMesh = _attackMesh;
                _attackMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(1, .12f, .12f, .4f) };
                _attackPreview.GetComponent<MeshRenderer>().sharedMaterial = _attackMaterial;
            }
            var vertices = new List<Vector3>(); var indices = new List<int>();
            foreach (var triangle in triangles)
            {
                int first = vertices.Count;
                vertices.Add(AttackVertex(new GridPoint(triangle.X - 1, triangle.Y)));
                vertices.Add(AttackVertex(new GridPoint(triangle.X + 1, triangle.Y)));
                vertices.Add(AttackVertex(new GridPoint(triangle.X, triangle.Y + triangle.T)));
                indices.Add(first); indices.Add(first + (triangle.T > 0 ? 2 : 1)); indices.Add(first + (triangle.T > 0 ? 1 : 2));
            }
            _attackMesh.Clear(); _attackMesh.SetVertices(vertices); _attackMesh.SetTriangles(indices, 0);
            _attackMesh.RecalculateBounds(); _attackPreview.SetActive(true);
        }
        private Vector3 AttackVertex(GridPoint point) => transform.InverseTransformPoint(GridToWorld(point) + transform.up * .12f);
        public void ClearAttackPreview() { if (_attackPreview != null) _attackPreview.SetActive(false); }
        public void BindBoundary(GridBoundaryDefinition boundary)
        {
            if (_boundary == boundary && _backdrop != null) { _backdrop.SetActive(true); return; }
            _boundary = boundary;
            if (_backdrop == null)
            {
                _backdrop = new GameObject("LogicGridBackdrop", typeof(MeshFilter), typeof(MeshRenderer));
                _backdrop.transform.SetParent(transform, false);
                _backdropMesh = new Mesh { name = "ReadOnlyLogicGrid", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                _backdrop.GetComponent<MeshFilter>().sharedMesh = _backdropMesh;
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                _backdropMaterial = new Material(shader); _backdropMaterial.color = new Color(.35f, .35f, .35f, 1);
                _backdrop.GetComponent<MeshRenderer>().sharedMaterial = _backdropMaterial;
            }
            var vertices = new List<Vector3>(); var indices = new List<int>();
            foreach (var point in boundary.EnumerateValidPoints())
                for (int direction = 0; direction < 6; direction += 2)
                {
                    var neighbor = new GridPoint(point.X + GridNeighborTable.OffsetX((GridDirection)direction),
                        point.Y + GridNeighborTable.OffsetY((GridDirection)direction));
                    if (!boundary.Contains(neighbor)) continue;
                    indices.Add(vertices.Count); vertices.Add(transform.InverseTransformPoint(GridToWorld(point)) + Vector3.up * .03f);
                    indices.Add(vertices.Count); vertices.Add(transform.InverseTransformPoint(GridToWorld(neighbor)) + Vector3.up * .03f);
                }
            _backdropMesh.Clear(); _backdropMesh.SetVertices(vertices); _backdropMesh.SetIndices(indices, MeshTopology.Lines, 0);
            _backdropMesh.RecalculateBounds(); _backdrop.SetActive(true);
        }
        public void ReleaseBackdrop() { if (_backdrop != null) _backdrop.SetActive(false); ClearAttackPreview(); }
        private void OnDestroy()
        {
            if (_backdropMesh != null) Destroy(_backdropMesh); if (_backdropMaterial != null) Destroy(_backdropMaterial);
            if (_attackMesh != null) Destroy(_attackMesh); if (_attackMaterial != null) Destroy(_attackMaterial);
        }
        public Vector3 GridToWorld(GridPoint cell) => transform.TransformPoint(
            new Vector3(cell.X * _sideLength * 0.5f, 0, cell.Y * _sideLength * Mathf.Sqrt(3) * 0.5f));
        public GridPoint WorldToGrid(Vector3 position)
        {
            Vector3 p = transform.InverseTransformPoint(position);
            int y = Mathf.RoundToInt(p.z / (_sideLength * Mathf.Sqrt(3) * 0.5f));
            int x = Mathf.RoundToInt(p.x / (_sideLength * 0.5f));
            if (((x + y) & 1) != 0) x++;
            return new GridPoint(x, y);
        }
        public bool TryPick(Ray ray, out GridPoint cell)
        {
            var plane = new Plane(transform.up, transform.position);
            if (plane.Raycast(ray, out float distance))
            { cell = WorldToGrid(ray.GetPoint(distance)); return _boundary == null || _boundary.Contains(cell); }
            cell = default; return false;
        }
    }
}
