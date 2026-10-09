using ProjectHero.Logic.Grid;
using UnityEngine;

namespace ProjectHero.UnityView
{
    /// <summary>Presentation coordinates and picking only; holds no LogicGrid write capability.</summary>
    public sealed class GridView : MonoBehaviour
    {
        [SerializeField, Min(0.001f)] private float _sideLength = 1;
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
            { cell = WorldToGrid(ray.GetPoint(distance)); return true; }
            cell = default; return false;
        }
    }
}
