using System.Collections.Generic;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
using UnityEngine;

namespace ProjectHero.UnityView
{
    /// <summary>Canonical triangle tables rendered only when committed geometry changes. Never queries Legacy grid.</summary>
    public sealed class BattleFootprintView : MonoBehaviour
    {
        [SerializeField] private GridView _grid;
        [SerializeField] private MeshFilter _filter;
        [SerializeField] private MeshRenderer _renderer;
        private BattleDefinition _definition;
        private Mesh _mesh;
        private readonly Dictionary<long, (int X, int Y, int Facing, bool Alive)> _previous = new Dictionary<long, (int, int, int, bool)>();
        public void Configure(GridView grid, MeshFilter filter, MeshRenderer renderer, Material material)
        { _grid = grid; _filter = filter; _renderer = renderer; _renderer.sharedMaterial = material; }
        public void Bind(BattleDefinition definition)
        {
            _definition = definition; _previous.Clear();
            _renderer.enabled = true;
            if (_mesh == null) { _mesh = new Mesh { name = "LogicFootprints" }; _filter.sharedMesh = _mesh; }
        }
        public void ReleaseVisuals() { _definition = null; _previous.Clear(); if (_mesh != null) _mesh.Clear(); if (_renderer != null) _renderer.enabled = false; }
        public void Consume(IReadOnlyList<UnitSnapshot> units)
        {
            if (_definition == null) return;
            bool changed = units.Count != _previous.Count;
            foreach (var unit in units)
                if (!_previous.TryGetValue(unit.UnitId, out var old) || old != (unit.X, unit.Y, unit.Facing, unit.IsAlive)) changed = true;
            if (!changed) return;
            _previous.Clear(); var vertices = new List<Vector3>(); var indices = new List<int>();
            foreach (var unit in units)
            {
                _previous.Add(unit.UnitId, (unit.X, unit.Y, unit.Facing, unit.IsAlive));
                if (!unit.IsAlive) continue;
                var spec = _definition.FindUnit(new UnitDefinitionId(unit.DefinitionId));
                var volume = _definition.FindVolume(spec.VolumeSpecId);
                foreach (var direction in volume.Directions)
                {
                    if ((int)direction.Direction != unit.Facing) continue;
                    foreach (var local in direction.Triangles)
                    {
                        int x = unit.X + local.X, y = unit.Y + local.Y, first = vertices.Count;
                        vertices.Add(ToVertex(new GridPoint(x - 1, y))); vertices.Add(ToVertex(new GridPoint(x + 1, y)));
                        vertices.Add(ToVertex(new GridPoint(x, y + local.T)));
                        indices.Add(first); indices.Add(first + (local.T > 0 ? 2 : 1)); indices.Add(first + (local.T > 0 ? 1 : 2));
                    }
                }
            }
            _mesh.Clear(); _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _mesh.SetVertices(vertices); _mesh.SetTriangles(indices, 0); _mesh.RecalculateNormals(); _mesh.RecalculateBounds();
        }
        private Vector3 ToVertex(GridPoint point) => transform.InverseTransformPoint(_grid.GridToWorld(point) + Vector3.up * .1f);
        private void OnDestroy() { if (_mesh != null) Destroy(_mesh); }
    }
}
