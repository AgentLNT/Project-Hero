using UnityEngine;
using ProjectHero.Core.Pathfinding;

namespace ProjectHero.Core.Grid
{
    public class GridManager : MonoBehaviour
    {
        public static GridManager Instance { get; private set; }

        [Header("Grid Settings")]
        public float HexSize = 1.0f; // Radius of the hex

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
    }
}
