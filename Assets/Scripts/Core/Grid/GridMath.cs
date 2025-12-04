using UnityEngine;

namespace ProjectHero.Core.Grid
{
    public static class GridMath
    {
        // Rotates a TrianglePoint by (steps * 60) degrees Counter-Clockwise around (0,0)
        // Optimized using Center-of-Mass Rotation and Parity Conservation.
        public static TrianglePoint Rotate(TrianglePoint point, int steps)
        {
            steps = steps % 6;
            if (steps < 0) steps += 6;
            if (steps == 0) return point;

            // 1. Calculate New T (Parity Conservation)
            // T flips every 60 degrees (odd steps).
            int newT = point.T * ((steps % 2 != 0) ? -1 : 1);

            // 2. Convert Logical (X, Y, T) to Cartesian Center
            // We assume HexSize = 1 for calculation (Scale Invariant)
            // X_log, Y_log -> World X, Z
            // X_world = X_log * 0.5
            // Z_world = Y_log * (sqrt(3)/2)
            // Center_Z = Z_world + T * (height / 3) = Z_world + T * (sqrt(3)/6)
            
            float sqrt3 = Mathf.Sqrt(3f);
            float wx = point.X * 0.5f;
            float wz = point.Y * (sqrt3 / 2f) + point.T * (sqrt3 / 6f);

            // 3. Rotate Cartesian Vector (wx, wz)
            // Angle = steps * 60 * Deg2Rad
            float angle = steps * 60f * Mathf.Deg2Rad;
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);

            float new_wx = wx * cos - wz * sin;
            float new_wz = wx * sin + wz * cos;

            // 4. Convert Back to Logical Reference Point
            // We have New Center (new_wx, new_wz) and New T.
            // Reference_Z = Center_Z - NewT * (height / 3)
            // Reference_Z = new_wz - newT * (sqrt(3)/6)
            
            float ref_wz = new_wz - newT * (sqrt3 / 6f);
            float ref_wx = new_wx;

            // 5. World to Grid (Inverse of Step 2)
            // Y_log = Reference_Z / (sqrt(3)/2)
            // X_log = Reference_X / 0.5
            
            int newY = Mathf.RoundToInt(ref_wz / (sqrt3 / 2f));
            int newX = Mathf.RoundToInt(ref_wx / 0.5f);

            return new TrianglePoint(newX, newY, newT);
        }

        // Legacy method kept for reference, but Rotate() now uses the optimized path.
        public static TrianglePoint Rotate60(TrianglePoint point)
        {
            return Rotate(point, 1);
        }

        // --- Helpers (Legacy / Verification) ---

        private static Vector2Int GetVertex(TrianglePoint t, int index)
        {
            // T=1 (Up): (X-1, Y), (X+1, Y), (X, Y+1)
            // T=-1 (Down): (X-1, Y), (X+1, Y), (X, Y-1)
            
            if (t.T == 1)
            {
                if (index == 1) return new Vector2Int(t.X - 1, t.Y);
                if (index == 2) return new Vector2Int(t.X + 1, t.Y);
                return new Vector2Int(t.X, t.Y + 1);
            }
            else
            {
                if (index == 1) return new Vector2Int(t.X - 1, t.Y);
                if (index == 2) return new Vector2Int(t.X + 1, t.Y);
                return new Vector2Int(t.X, t.Y - 1);
            }
        }

        private static Vector2Int RotateVertex60(Vector2Int v)
        {
            // Convert Doubled (x, y) to Cube (q, r, s)
            // q = (x - y) / 2
            // r = y
            // s = -q - r
            
            int q = (v.x - v.y) / 2;
            int r = v.y;
            int s = -q - r;

            // Rotate CCW: (q, r, s) -> (-r, -s, -q)
            int q_new = -r;
            int r_new = -s;
            // int s_new = -q; // Not needed for conversion back

            // Convert back to Doubled
            // y = r
            // x = 2q + y
            
            int y_final = r_new;
            int x_final = 2 * q_new + y_final;

            return new Vector2Int(x_final, y_final);
        }

        private static TrianglePoint FromVertices(Vector2Int v1, Vector2Int v2, Vector2Int v3)
        {
            // Find the two vertices with the same Y (Horizontal Edge)
            Vector2Int p1, p2, p3;

            if (v1.y == v2.y) { p1 = v1; p2 = v2; p3 = v3; }
            else if (v1.y == v3.y) { p1 = v1; p2 = v3; p3 = v2; }
            else { p1 = v2; p2 = v3; p3 = v1; } // v2.y == v3.y

            // Midpoint of p1 and p2 is the Triangle Center (X, Y)
            // Since p1 and p2 are vertices, their X sum is even?
            // Wait, midpoint X = (x1 + x2) / 2.
            // In doubled coords, vertices are always even parity? No.
            // Vertices are (X+Y)%2 == 0.
            // Example: (0,0) and (2,0). Midpoint (1,0).
            
            int centerX = (p1.x + p2.x) / 2;
            int centerY = p1.y; // Same Y

            // Determine T based on p3
            // If p3.y > centerY, T=1. Else T=-1.
            int t = (p3.y > centerY) ? 1 : -1;

            return new TrianglePoint(centerX, centerY, t);
        }
    }
}
