using System.Collections.Generic;
using UnityEngine;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;

namespace ProjectHero.Core.Combat
{
    [CreateAssetMenu(fileName = "NewAttackPattern", menuName = "ProjectHero/Combat/AttackPattern")]
    public class AttackPattern : ScriptableObject
    {
        [Header("Pattern Definition (Facing East)")]
        [Tooltip("Define the attack shape assuming the unit is at (0,0) facing East.")]
        public List<TrianglePoint> RelativeTriangles = new List<TrianglePoint>();

        public List<TrianglePoint> GetAffectedTriangles(Pathfinder.GridPoint attackerPos, GridDirection facing)
        {
            List<TrianglePoint> result = new List<TrianglePoint>();
            int rotationSteps = (int)facing; // East = 0, NE = 1, etc.

            foreach (var relativePoint in RelativeTriangles)
            {
                // 1. Rotate the relative point based on facing
                TrianglePoint rotatedPoint = GridMath.Rotate(relativePoint, rotationSteps);

                // 2. Translate to absolute position
                // Absolute = AttackerPos + RotatedRelative
                // Note: T is absolute orientation, but GridMath.Rotate handles the T flip correctly.
                
                result.Add(new TrianglePoint(
                    attackerPos.X + rotatedPoint.X, 
                    attackerPos.Y + rotatedPoint.Y, 
                    rotatedPoint.T
                ));
            }

            return result;
        }
    }
}
