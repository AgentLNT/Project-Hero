using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Combat;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Physics;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ProjectHero.Demos
{
    public class LibraryGenerator : MonoBehaviour
    {
        public ActionLibrarySO targetLibrary;
        public string generatedPath = "Assets/Resources/GeneratedActions";

        [ContextMenu("Generate Default Actions")]
        public void Generate()
        {
#if UNITY_EDITOR
            if (targetLibrary == null)
            {
                Debug.LogError("Please assign a Target Library!");
                return;
            }

            if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder(generatedPath)) AssetDatabase.CreateFolder("Assets/Resources", "GeneratedActions");

            // 1. Create Patterns
            // Assumes Standard Unit Volume (Hexagon centered at 0,0)
            // Occupied: (1,0,1), (1,0,-1), (-1,0,1), (-1,0,-1), (0,1,-1), (0,-1,1)
            
            // Single Target
            // Even (0 deg - East): Tip of unit is at (2,0). Target starts at x=3.
            // Odd (30 deg - EastNorth): Face of unit is (1,0,1). Target is neighbor (2,1,-1).
            var p_single = CreatePattern("Pattern_SingleFront", 
                new List<TrianglePoint> { new TrianglePoint(3, 0, -1) },
                new List<TrianglePoint> { new TrianglePoint(2, 1, -1) }
            );
            
            // Wide Cleave
            var p_cleave = CreatePattern("Pattern_Cleave", 
                new List<TrianglePoint> 
                { 
                    new TrianglePoint(3, 0, -1),  // Front
                    new TrianglePoint(3, 0, 1),   // Front-Pair
                    new TrianglePoint(2, 1, -1),  // Front-Top (30 deg)
                    new TrianglePoint(2, -1, 1)   // Front-Bottom (-30 deg)
                },
                new List<TrianglePoint>
                {
                    new TrianglePoint(2, 1, -1),  // Front (30 deg)
                    new TrianglePoint(3, 0, 1),   // Front-Right (0 deg)
                    new TrianglePoint(1, 2, 1)    // Front-Left (60 deg)
                }
            );

            // Line (Range 2)
            var p_line = CreatePattern("Pattern_Line2", 
                new List<TrianglePoint> 
                { 
                    new TrianglePoint(3, 0, -1), 
                    new TrianglePoint(4, 0, 1) 
                },
                new List<TrianglePoint> 
                { 
                    new TrianglePoint(2, 1, -1), 
                    new TrianglePoint(2, 1, 1) 
                }
            );

            // Surround (Ring around unit)
            var p_aoe = CreatePattern("Pattern_Surround", 
                new List<TrianglePoint> 
                { 
                    new TrianglePoint(3, 0, -1), new TrianglePoint(3, 0, 1), // East
                    new TrianglePoint(-3, 0, 1), new TrianglePoint(-3, 0, -1), // West
                    new TrianglePoint(0, 3, -1), new TrianglePoint(0, -3, 1), // North/South (Approx)
                    new TrianglePoint(2, 1, -1), new TrianglePoint(2, -1, 1),
                    new TrianglePoint(-2, 1, 1), new TrianglePoint(-2, -1, -1)
                },
                new List<TrianglePoint> 
                { 
                    // Odd pattern can be similar for full surround
                    new TrianglePoint(3, 0, -1), new TrianglePoint(3, 0, 1),
                    new TrianglePoint(-3, 0, 1), new TrianglePoint(-3, 0, -1),
                    new TrianglePoint(2, 1, -1), new TrianglePoint(2, -1, 1),
                    new TrianglePoint(-2, 1, 1), new TrianglePoint(-2, -1, -1)
                }
            );

            // 2. Create Actions
            targetLibrary.Actions.Clear();

            // Rebalanced Actions for New Physics Model (v_impact vs v_target)
            // Thresholds: Push > 0.5x, Stagger >= 1.0x, Knockdown >= 1.5x
            // Base Impact Ratio = Kw * ForceMult (assuming equal mass/speed)

            // Quick Slash (Slash Kw=0.6)
            // Ratio = 0.6 * 0.8 = 0.48 (< 0.5). No control effect usually. Pure damage.
            AddAction("QuickSlash", "Quick Slash", ActionType.Attack, 0.5f, 15f, ImpactType.Slash, 10f, 0.8f, p_single);

            // Heavy Smash (Blunt Kw=1.0)
            // Ratio = 1.0 * 1.6 = 1.6 (> 1.5). Guaranteed Knockdown on equal footing.
            AddAction("HeavySmash", "Heavy Smash", ActionType.Attack, 1.5f, 40f, ImpactType.Blunt, 25f, 1.6f, p_single);

            // Wide Cleave (Slash Kw=0.6)
            // Ratio = 0.6 * 1.2 = 0.72 (> 0.5). Causes Push back. Good for crowd control.
            AddAction("WideCleave", "Wide Cleave", ActionType.Attack, 1.0f, 20f, ImpactType.Slash, 20f, 1.2f, p_cleave);

            // Spear Thrust (Pierce Kw=0.3)
            // Ratio = 0.3 * 1.0 = 0.3. No control. High penetration/precision (future mechanic).
            AddAction("SpearThrust", "Spear Thrust", ActionType.Attack, 0.8f, 25f, ImpactType.Pierce, 15f, 1.0f, p_line);

            // Whirlwind (Slash Kw=0.6)
            // Ratio = 0.6 * 2.5 = 1.5. Knockdown AoE. Ultimate move.
            AddAction("Whirlwind", "Whirlwind", ActionType.Attack, 2.0f, 30f, ImpactType.Slash, 40f, 2.5f, p_aoe);

            EditorUtility.SetDirty(targetLibrary);
            AssetDatabase.SaveAssets();
            Debug.Log("Actions Generated Successfully!");
#endif
        }

#if UNITY_EDITOR
        private AttackPattern CreatePattern(string name, List<TrianglePoint> points, List<TrianglePoint> pointsOdd = null)
        {
            string path = $"{generatedPath}/{name}.asset";
            var pattern = AssetDatabase.LoadAssetAtPath<AttackPattern>(path);
            if (pattern == null)
            {
                pattern = ScriptableObject.CreateInstance<AttackPattern>();
                AssetDatabase.CreateAsset(pattern, path);
            }
            
            pattern.RelativeTriangles = points;
            pattern.RelativeTrianglesOdd = pointsOdd ?? new List<TrianglePoint>();
            EditorUtility.SetDirty(pattern);
            return pattern;
        }

        private void AddAction(string id, string name, ActionType actionType, float time, float dmg, ImpactType type, float stamina, float force, AttackPattern pattern)
        {
            var action = new Action(name, actionType, time, dmg, type, stamina, force, pattern);
            targetLibrary.Actions.Add(new ActionLibrarySO.ActionEntry { ID = id, Data = action });
        }
#endif
    }
}
