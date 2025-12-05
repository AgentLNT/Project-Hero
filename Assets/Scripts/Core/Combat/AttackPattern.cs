using System.Collections.Generic;
using UnityEngine;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;

namespace ProjectHero.Core.Combat
{
    [CreateAssetMenu(fileName = "NewAttackPattern", menuName = "ProjectHero/Combat/AttackPattern")]
    public class AttackPattern : ScriptableObject
    {
        [Header("Pattern Definition")]
        [Tooltip("Define the attack shape assuming the unit is at (0,0) facing East (0 deg).")]
        public List<TrianglePoint> RelativeTriangles = new List<TrianglePoint>();
        
        [Tooltip("Optional: Define the attack shape for Face-Aligned directions (e.g. 30 deg). If empty, will try to approximate from East.")]
        public List<TrianglePoint> RelativeTrianglesOdd = new List<TrianglePoint>();

        public List<TrianglePoint> GetAffectedTriangles(Pathfinder.GridPoint attackerPos, GridDirection facing)
        {
            List<TrianglePoint> result = new List<TrianglePoint>();
            int dirInt = (int)facing;
            bool isOdd = (dirInt % 2 != 0);

            // Select source pattern
            List<TrianglePoint> sourcePattern = RelativeTriangles;
            int baseDir = 0; // East

            if (isOdd)
            {
                if (RelativeTrianglesOdd != null && RelativeTrianglesOdd.Count > 0)
                {
                    sourcePattern = RelativeTrianglesOdd;
                    baseDir = 1; // EastNorth
                }
                else
                {
                    // Fallback: Use Even pattern (might be inaccurate but prevents crash)
                    sourcePattern = RelativeTriangles;
                    baseDir = 0;
                }
            }

            // Calculate rotation steps (each step is 60 degrees)
            // Steps = (Target - Base) / 2
            int rotationSteps = (dirInt - baseDir) / 2;

            foreach (var relativePoint in sourcePattern)
            {
                // 1. Rotate the relative point based on facing
                TrianglePoint rotatedPoint = GridMath.Rotate(relativePoint, rotationSteps);

                // 2. Translate to absolute position
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
