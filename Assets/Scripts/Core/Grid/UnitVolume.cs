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

            // 2. If not found, try to find "East" (Default) and rotate it
            var baseVol = Volumes.Find(v => v.Direction == GridDirection.East);
            if (baseVol != null)
            {
                int steps = (int)direction; // East=0, NE=1, etc.
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
