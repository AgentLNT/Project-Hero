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

        [Header("Generation Settings")]
        [Tooltip("Hex radius in 'triangle radius' units. Supports 0.5 steps.")]
        public float SelectedRadius = 1.0f;

        [Tooltip("Generate UnitVolume assets for R1, R2, R3.")]
        public bool GenerateUnitVolumes = true;

        [Tooltip("Generate AttackPattern assets for R1, R2, R3.")]
        public bool GenerateAllRadiiPatterns = true;

        // --- Configuration for Shapes ---
        // Defined in Triangle Units (Side Length)
        private const float REACH_SHORT = 1.5f;
        private const float REACH_MEDIUM = 2.0f;
        private const float REACH_LONG = 4.0f; // Increased for better Thrust visibility
        
        private const float WIDTH_NARROW = 1.5f; // Widened to prevent "rhombus" artifacts on Thrust
        
        private const float ARC_NARROW = 20f; 
        private const float ARC_WIDE = 40f; // Reduced from 60f to make Slash tighter
        private const float ARC_FULL = 120f; // Reduced base for Cleave calculations

        [ContextMenu("Generate Library")]
        public void Generate()
        {
#if UNITY_EDITOR
            if (targetLibrary == null)
            {
                Debug.LogError("Please assign a Target Library!");
                return;
            }

            EnsureFolders();

            // 1. Generate Unit Volumes (R1, R2, R3)
            if (GenerateUnitVolumes)
            {
                GenerateUnitVolumeAsset(1.0f);
                GenerateUnitVolumeAsset(2.0f);
                GenerateUnitVolumeAsset(3.0f);
            }

            // 2. Generate Patterns
            // We always generate patterns for the selected radius to bind them.
            // Optionally generate for others too.
            if (GenerateAllRadiiPatterns)
            {
                GeneratePatternsForRadius(1.0f);
                GeneratePatternsForRadius(2.0f);
                GeneratePatternsForRadius(3.0f);
            }

            // 3. Bind Actions for Selected Radius
            var patterns = GeneratePatternsForRadius(SelectedRadius);
            BindActionsToLibrary(patterns);

            Debug.Log($"Library Generation Complete for Radius {SelectedRadius}");
#endif
        }

#if UNITY_EDITOR
        private void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder(generatedPath)) AssetDatabase.CreateFolder("Assets/Resources", "GeneratedActions");
        }

        private void GenerateUnitVolumeAsset(float radius)
        {
            string name = $"UnitVolume_Hex_{RadiusSuffix(radius)}";
            string path = $"{generatedPath}/{name}.asset";

            var vol = AssetDatabase.LoadAssetAtPath<UnitVolume>(path);
            if (vol == null)
            {
                vol = ScriptableObject.CreateInstance<UnitVolume>();
                AssetDatabase.CreateAsset(vol, path);
            }

            vol.Volumes.Clear();
            
            // 0 Degree (Even) - Vertex Aligned
            vol.Volumes.Add(new UnitVolume.DirectionalVolume
            {
                Direction = GridDirection.East,
                RelativeTriangles = RasterizeHexagon(radius, 0f)
            });

            // 30 Degree (Odd) - Edge Aligned
            vol.Volumes.Add(new UnitVolume.DirectionalVolume
            {
                Direction = GridDirection.EastNorth,
                RelativeTriangles = RasterizeHexagon(radius, 30f)
            });

            EditorUtility.SetDirty(vol);
            AssetDatabase.SaveAssets();
        }

        private Dictionary<string, AttackPattern> GeneratePatternsForRadius(float radius)
        {
            var results = new Dictionary<string, AttackPattern>();
            string suffix = RadiusSuffix(radius);

            // Define Archetypes
            // 1. Quick Slash (Short Cone)
            results["Slash"] = CreatePatternAsset($"Pattern_Slash_{suffix}", 
                GenerateCone(radius, 0f, REACH_SHORT, ARC_WIDE),
                GenerateCone(radius, 30f, REACH_SHORT, ARC_WIDE));

            // 2. Heavy Smash (Medium Box/Impact)
            results["Smash"] = CreatePatternAsset($"Pattern_Smash_{suffix}",
                GenerateBox(radius, 0f, REACH_MEDIUM, WIDTH_NARROW * 1.5f),
                GenerateBox(radius, 30f, REACH_MEDIUM, WIDTH_NARROW * 1.5f));

            // 3. Wide Cleave (Wide Cone)
            results["Cleave"] = CreatePatternAsset($"Pattern_Cleave_{suffix}",
                GenerateCone(radius, 0f, REACH_MEDIUM, ARC_FULL * 0.7f), // ~84 deg half-angle
                GenerateCone(radius, 30f, REACH_MEDIUM, ARC_FULL * 0.7f));

            // 4. Spear Thrust (Long Narrow Box)
            results["Thrust"] = CreatePatternAsset($"Pattern_Thrust_{suffix}",
                GenerateBox(radius, 0f, REACH_LONG, WIDTH_NARROW),
                GenerateBox(radius, 30f, REACH_LONG, WIDTH_NARROW));

            // 5. Whirlwind (Ring)
            results["Whirlwind"] = CreatePatternAsset($"Pattern_Whirlwind_{suffix}",
                GenerateRing(radius, 0f, REACH_SHORT),
                GenerateRing(radius, 30f, REACH_SHORT));

            return results;
        }

        private AttackPattern CreatePatternAsset(string name, List<TrianglePoint> even, List<TrianglePoint> odd)
        {
            string path = $"{generatedPath}/{name}.asset";
            var pattern = AssetDatabase.LoadAssetAtPath<AttackPattern>(path);
            if (pattern == null)
            {
                pattern = ScriptableObject.CreateInstance<AttackPattern>();
                AssetDatabase.CreateAsset(pattern, path);
            }

            pattern.RelativeTriangles = even;
            pattern.RelativeTrianglesOdd = odd;
            EditorUtility.SetDirty(pattern);
            return pattern;
        }

        private void BindActionsToLibrary(Dictionary<string, AttackPattern> patterns)
        {
            targetLibrary.Actions.Clear();

            AddAction("QuickSlash", "Quick Slash", ActionType.Attack, 0.5f, 15f, ImpactType.Slash, 10f, 0.8f, patterns["Slash"]);
            AddAction("HeavySmash", "Heavy Smash", ActionType.Attack, 1.5f, 40f, ImpactType.Blunt, 25f, 1.6f, patterns["Smash"]);
            AddAction("WideCleave", "Wide Cleave", ActionType.Attack, 1.0f, 20f, ImpactType.Slash, 20f, 1.2f, patterns["Cleave"]);
            AddAction("SpearThrust", "Spear Thrust", ActionType.Attack, 0.8f, 25f, ImpactType.Pierce, 15f, 1.0f, patterns["Thrust"]);
            AddAction("Whirlwind", "Whirlwind", ActionType.Attack, 2.0f, 30f, ImpactType.Slash, 40f, 2.5f, patterns["Whirlwind"]);
            
            EditorUtility.SetDirty(targetLibrary);
            AssetDatabase.SaveAssets();
        }

        private void AddAction(string id, string name, ActionType type, float time, float dmg, ImpactType impact, float stamina, float force, AttackPattern pattern)
        {
            var action = new Action(name, type, time, dmg, impact, stamina, force, pattern);
            targetLibrary.Actions.Add(new ActionLibrarySO.ActionEntry { ID = id, Data = action });
        }

        // --- Rasterization & Geometry ---

        private List<TrianglePoint> RasterizeHexagon(float radius, float rotationDeg)
        {
            // Hexagon is defined by Radius R.
            // We scan a box and check IsInsideHex.
            // We enforce connectivity from center.
            
            var candidates = ScanGrid(radius + 1f, (pt, pos) => IsInsideHex(pos, radius, rotationDeg));
            return FilterConnected(candidates, Vector2.zero);
        }

        private List<TrianglePoint> GenerateCone(float unitRadius, float facingDeg, float reach, float halfAngleDeg)
        {
            // Cone originates from Unit Center, but we exclude Unit Volume.
            // Radius = unitRadius + reach.
            // Angle = facingDeg +/- halfAngleDeg.
            
            float totalRadius = unitRadius + reach;
            Vector2 facingDir = DegToVector(facingDeg);

            var candidates = ScanGrid(totalRadius + 1f, (pt, pos) => 
            {
                // 1. Check Distance
                float d = pos.magnitude;
                if (d > totalRadius) return false;
                if (d < unitRadius - 0.1f) return false; // Optimization: Don't check deep inside

                // 2. Check Angle
                Vector2 dir = pos.normalized;
                float angle = Vector2.Angle(facingDir, dir);
                if (angle > halfAngleDeg) return false;

                // 3. Exclude Unit Volume (Strict)
                if (IsInsideHex(pos, unitRadius, facingDeg)) return false;

                return true;
            });

            // Seed from a point just in front of the unit
            Vector2 seedPos = facingDir * (unitRadius + 0.5f);
            return FilterConnected(candidates, seedPos);
        }

        private List<TrianglePoint> GenerateBox(float unitRadius, float facingDeg, float length, float width)
        {
            // Box starts at unitRadius distance.
            // Length extends forward. Width extends sideways.
            
            Vector2 facingDir = DegToVector(facingDeg);
            Vector2 rightDir = new Vector2(facingDir.y, -facingDir.x); // Clockwise perp? (x,y) -> (y, -x) is -90 deg

            float startDist = unitRadius;
            float endDist = unitRadius + length;
            float halfWidth = width * 0.5f;

            var candidates = ScanGrid(endDist + 1f, (pt, pos) =>
            {
                // Project pos onto facingDir and rightDir
                float forward = Vector2.Dot(pos, facingDir);
                float side = Mathf.Abs(Vector2.Dot(pos, rightDir));

                if (forward < startDist || forward > endDist) return false;
                if (side > halfWidth) return false;

                // Exclude Unit Volume
                if (IsInsideHex(pos, unitRadius, facingDeg)) return false;

                return true;
            });

            Vector2 seedPos = facingDir * (unitRadius + 0.5f);
            return FilterConnected(candidates, seedPos);
        }

        private List<TrianglePoint> GenerateRing(float unitRadius, float facingDeg, float reach)
        {
            float inner = unitRadius;
            float outer = unitRadius + reach;

            var candidates = ScanGrid(outer + 1f, (pt, pos) =>
            {
                float d = pos.magnitude;
                if (d < inner || d > outer) return false;
                if (IsInsideHex(pos, unitRadius, facingDeg)) return false;
                return true;
            });

            // Seed from front (ring should be connected all around, so any point outside works)
            Vector2 seedPos = DegToVector(facingDeg) * (unitRadius + 0.5f);
            return FilterConnected(candidates, seedPos);
        }

        // --- Helpers ---

        private delegate bool PointPredicate(TrianglePoint pt, Vector2 pos);

        private List<TrianglePoint> ScanGrid(float range, PointPredicate predicate)
        {
            var list = new List<TrianglePoint>();
            int bound = Mathf.CeilToInt(range * 2.5f) + 2; // Heuristic for grid coords

            for (int x = -bound; x <= bound; x++)
            {
                for (int y = -bound; y <= bound; y++)
                {
                    if (((x + y) & 1) == 0) continue; // Parity check

                    // Check both T=1 and T=-1
                    CheckAndAdd(x, y, 1, list, predicate);
                    CheckAndAdd(x, y, -1, list, predicate);
                }
            }
            return list;
        }

        private void CheckAndAdd(int x, int y, int t, List<TrianglePoint> list, PointPredicate predicate)
        {
            var pt = new TrianglePoint(x, y, t);
            // Use Corners for strict continuity
            if (CheckTriangleCorners(pt, predicate))
            {
                list.Add(pt);
            }
        }

        private bool CheckTriangleCorners(TrianglePoint pt, PointPredicate predicate)
        {
            // If ANY corner is inside, we count it? Or ALL? or Centroid?
            // For "Continuous", usually "Any part overlaps" is best.
            // But for "IsInsideHex", we used "All corners" to avoid jagged edges sticking out.
            // For Attacks, "Centroid" is standard.
            
            // Let's use Centroid for now as it's standard for "Tile Selection".
            Vector2 center = GetTriCentroid(pt);
            return predicate(pt, center);
        }

        private List<TrianglePoint> FilterConnected(List<TrianglePoint> candidates, Vector2 seedWorldPos)
        {
            if (candidates.Count == 0) return candidates;

            // Find closest candidate to seed
            int seedIndex = -1;
            float minDst = float.MaxValue;

            for (int i = 0; i < candidates.Count; i++)
            {
                float d = (GetTriCentroid(candidates[i]) - seedWorldPos).sqrMagnitude;
                if (d < minDst)
                {
                    minDst = d;
                    seedIndex = i;
                }
            }

            if (seedIndex == -1) return new List<TrianglePoint>();

            // BFS
            return KeepConnectedComponentNearOrigin(candidates, seedIndex);
        }

        private List<TrianglePoint> KeepConnectedComponentNearOrigin(List<TrianglePoint> tris, int seedIndex)
        {
            // Build Graph
            var triToIndex = new Dictionary<TrianglePoint, int>();
            for (int i = 0; i < tris.Count; i++) triToIndex[tris[i]] = i;

            var adj = new List<List<int>>(tris.Count);
            for(int i=0; i<tris.Count; i++) adj.Add(new List<int>());

            // Map Vertex -> List of TriIndices
            var vertToTris = new Dictionary<Vector2Int, List<int>>();
            
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                AddVert(vertToTris, t.X - 1, t.Y, i);
                AddVert(vertToTris, t.X + 1, t.Y, i);
                AddVert(vertToTris, t.X, t.Y + t.T, i);
            }

            // Build Adjacency
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                var neighbors = new HashSet<int>();
                CheckNeighbors(vertToTris, t.X - 1, t.Y, i, neighbors);
                CheckNeighbors(vertToTris, t.X + 1, t.Y, i, neighbors);
                CheckNeighbors(vertToTris, t.X, t.Y + t.T, i, neighbors);
                
                foreach(var n in neighbors)
                {
                    // Must share 2 vertices to be edge-connected
                    if (IsEdgeConnected(tris[i], tris[n]))
                    {
                        adj[i].Add(n);
                    }
                }
            }

            // BFS
            var result = new List<TrianglePoint>();
            var visited = new bool[tris.Count];
            var q = new Queue<int>();
            
            visited[seedIndex] = true;
            q.Enqueue(seedIndex);

            while(q.Count > 0)
            {
                int u = q.Dequeue();
                result.Add(tris[u]);

                foreach(var v in adj[u])
                {
                    if (!visited[v])
                    {
                        visited[v] = true;
                        q.Enqueue(v);
                    }
                }
            }

            return result;
        }

        private void AddVert(Dictionary<Vector2Int, List<int>> map, int x, int y, int idx)
        {
            var v = new Vector2Int(x, y);
            if (!map.ContainsKey(v)) map[v] = new List<int>();
            map[v].Add(idx);
        }

        private void CheckNeighbors(Dictionary<Vector2Int, List<int>> map, int x, int y, int selfIdx, HashSet<int> outNeighbors)
        {
            var v = new Vector2Int(x, y);
            if (map.TryGetValue(v, out var list))
            {
                foreach (var idx in list)
                {
                    if (idx != selfIdx) outNeighbors.Add(idx);
                }
            }
        }

        private bool IsEdgeConnected(TrianglePoint a, TrianglePoint b)
        {
            // Count shared vertices
            int shared = 0;
            var av = GetVerts(a);
            var bv = GetVerts(b);
            
            foreach(var v1 in av)
            {
                foreach(var v2 in bv)
                {
                    if (v1 == v2) shared++;
                }
            }
            return shared >= 2;
        }

        private Vector2Int[] GetVerts(TrianglePoint t)
        {
            return new Vector2Int[]
            {
                new Vector2Int(t.X - 1, t.Y),
                new Vector2Int(t.X + 1, t.Y),
                new Vector2Int(t.X, t.Y + t.T)
            };
        }

        // --- Math ---

        private Vector2 DegToVector(float deg)
        {
            float rad = deg * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        }

        private Vector2 GetTriCentroid(TrianglePoint t)
        {
            // X = x * 0.5
            // Z = y * h + t * h/3
            float h = 0.8660254f; // sqrt(3)/2
            return new Vector2(t.X * 0.5f, t.Y * h + t.T * h / 3f);
        }

        private bool IsInsideHex(Vector2 pos, float R, float rotDeg)
        {
            // Point Right Hex (Vertex at +X)
            // Rotate point by -rotDeg
            float rad = -rotDeg * Mathf.Deg2Rad;
            float c = Mathf.Cos(rad);
            float s = Mathf.Sin(rad);
            float x = pos.x * c - pos.y * s;
            float y = pos.x * s + pos.y * c;

            float ax = Mathf.Abs(x);
            float ay = Mathf.Abs(y);
            
            // Point Right Hex Inequalities:
            // 1. |y| <= sqrt(3)/2 * R
            // 2. |x| + |y|/sqrt(3) <= R
            
            float h = 0.8660254f; // sqrt(3)/2
            float invSqrt3 = 0.5773503f; // 1/sqrt(3)

            if (ay > h * R + 1e-4f) return false;
            if (ax + ay * invSqrt3 > R + 1e-4f) return false;
            
            return true;
        }

        private string RadiusSuffix(float r)
        {
            int tenths = Mathf.RoundToInt(r * 10f);
            return $"R{tenths/10}_{tenths%10}";
        }
#endif
    }
}
