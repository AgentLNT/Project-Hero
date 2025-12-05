using System.Collections.Generic;
using UnityEngine;

namespace ProjectHero.Core.Grid
{
    [CreateAssetMenu(fileName = "NewUnitVolume", menuName = "ProjectHero/Grid/UnitVolume")]
    public class UnitVolume : ScriptableObject
    {
        [System.Serializable]
        public class DirectionalVolume
        {
            public GridDirection Direction;
            public List<TrianglePoint> RelativeTriangles;
        }

        public List<DirectionalVolume> Volumes = new List<DirectionalVolume>();

        public List<TrianglePoint> GetVolumeFor(GridDirection direction)
        {
            // 1. Try to find explicit definition
            var vol = Volumes.Find(v => v.Direction == direction);
            if (vol != null && vol.RelativeTriangles.Count > 0) 
                return vol.RelativeTriangles;

            // 2. Procedural Generation
            // Determine if we are in an Even (Vertex-Aligned) or Odd (Face-Aligned) direction
            int dirInt = (int)direction;
            bool isOdd = (dirInt % 2 != 0);

            // Base direction to look for: East (0) for Even, EastNorth (1) for Odd
            GridDirection baseDirection = isOdd ? GridDirection.EastNorth : GridDirection.East;
            
            var baseVol = Volumes.Find(v => v.Direction == baseDirection);
            
            // Fallback: If Odd base is missing, maybe use Even base? 
            // (Physics Warning: This might look weird, but better than nothing)
            if (baseVol == null && isOdd)
            {
                baseVol = Volumes.Find(v => v.Direction == GridDirection.East);
                Debug.LogWarning($"UnitVolume '{name}': Missing Odd base volume for direction {direction}. Falling back to Even base volume.");
            }

            if (baseVol != null)
            {
                // Calculate rotation steps relative to the base
                // Note: GridMath.Rotate assumes 60-degree steps (Vertex-to-Vertex).
                // If we are rotating from Odd to Odd (e.g. 30 -> 90), that is a 60 degree step.
                // Steps = (Target - Base) / 2
                
                int steps = (dirInt - (int)baseVol.Direction) / 2;
                
                List<TrianglePoint> rotated = new List<TrianglePoint>();
                foreach (var p in baseVol.RelativeTriangles)
                {
                    rotated.Add(GridMath.Rotate(p, steps));
                }
                return rotated;
            }

            return new List<TrianglePoint>();
        }
    }
}
