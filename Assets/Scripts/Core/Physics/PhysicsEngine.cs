using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Timeline;

namespace ProjectHero.Core.Physics
{
    // Renamed from WeaponType to ImpactType to reflect the Action's nature, not the tool
    public enum ImpactType { Blunt, Slash, Pierce }

    public static class PhysicsEngine
    {
        /// <summary>
        /// Checks if two sets of triangles have any overlap.
        /// This is the core collision detection method for the entire game.
        /// </summary>
        public static bool CheckIntersection(List<TrianglePoint> volumeA, List<TrianglePoint> volumeB)
        {
            if (volumeA == null || volumeB == null) return false;

            // Optimization: Use a HashSet for O(1) lookups if volumes are large.
            // For small volumes (e.g. < 10 triangles), nested loops are fine and generate less garbage.
            
            foreach (var triA in volumeA)
            {
                foreach (var triB in volumeB)
                {
                    if (triA == triB) return true;
                }
            }
            return false;
        }

        public static void ResolveCollision(BattleTimeline timeline, CombatUnit attacker, CombatUnit target, Action action)
        {
            float Kw = GetTransferCoefficient(action.ImpactType);
            
            // Formula: P_delivered = M_attacker * v_attacker * Kw * ForceMultiplier
            // v_attacker is Swiftness
            float deliveredMomentum = attacker.TotalMass * attacker.Swiftness * Kw * action.ForceMultiplier;
            
            // Formula: v_impact = P / M_target
            float impactVelocity = deliveredMomentum / target.TotalMass;

            // --- Damage Calculation (Design Section V.A) ---
            
            // 1. Physical Damage: D_phys = D_base * (1 - Armor / (Armor + 100))
            float armorReduction = target.ArmorDefense / (target.ArmorDefense + 100f);
            float physicalDamage = action.BaseDamage * (1.0f - armorReduction);

            // 2. Impact Damage: D_impact = P_delivered * 0.02 (Reduced from 0.1 to prevent one-shots)
            float impactDamage = deliveredMomentum * 0.02f;

            float totalDamage = physicalDamage + impactDamage;

            // --- Adrenaline Gain (Design Section III) ---
            // Gain Adrenaline based on damage dealt/received
            // Attacker gains: 10% of damage dealt
            // Defender gains: 20% of damage taken
            attacker.CurrentAdrenaline += totalDamage * 0.1f;
            target.CurrentAdrenaline += totalDamage * 0.2f;
            
            // Clamp to Max (assuming 100 for now)
            attacker.CurrentAdrenaline = Mathf.Min(attacker.CurrentAdrenaline, 100f);
            target.CurrentAdrenaline = Mathf.Min(target.CurrentAdrenaline, 100f);

            Debug.Log($"[Physics] {attacker.name} uses {action.Name} on {target.name}");
            Debug.Log($"[Calc] M_atk={attacker.TotalMass}, v_atk={attacker.Swiftness}, Kw={Kw}, Mult={action.ForceMultiplier} => P_delivered={deliveredMomentum:F2}");
            Debug.Log($"[Calc] M_target={target.TotalMass} => v_impact={impactVelocity:F2}");
            Debug.Log($"[Calc] D_base={action.BaseDamage}, Armor={target.ArmorDefense} => D_phys={physicalDamage:F2}");
            Debug.Log($"[Calc] D_impact={impactDamage:F2} (P*0.02) => Total={totalDamage:F2}");
            Debug.Log($"[Adrenaline] {attacker.name}: +{totalDamage * 0.1f:F1}, {target.name}: +{totalDamage * 0.2f:F1}");

            // Thresholds (Design Section IV.B)
            // Thresholds are relative to target's Swiftness (v_Target)
            float v_target = target.Swiftness;

            // Displacement (Design Section IV.B)
            // D_hexes = floor(v_impact / (v_target * mu))
            float friction = 1.0f; // Default friction mu
            int displacementHexes = Mathf.FloorToInt(impactVelocity / (v_target * friction));
            
            // Calculate Push Direction
            GridDirection pushDir = GridMath.GetDirection(attacker.GridPosition, target.GridPosition);

            // Adjusted Thresholds for better gameplay feel
            // Knockdown: 1.5x (was 2.0x)
            // Stagger: 1.0x (was 1.5x)
            // Push: 0.5x (was 1.0x)
            if (impactVelocity >= v_target * 1.5f)
            {
                displacementHexes = Mathf.Max(displacementHexes, 2); // Ensure at least 2 hexes for knockdown
                target.IsKnockedDown = true;
                target.OnImpact(timeline, impactVelocity, totalDamage, displacementHexes, pushDir); 
                Debug.Log($"[Result] {target.name} KNOCKED DOWN! (v_impact {impactVelocity:F2} >= 1.5 * {v_target:F2}) -> Dist: {displacementHexes}");
            }
            else if (impactVelocity >= v_target * 1.0f)
            {
                displacementHexes = Mathf.Max(displacementHexes, 1); // Ensure at least 1 hex for stagger
                target.IsStaggered = true;
                target.OnImpact(timeline, impactVelocity, totalDamage, displacementHexes, pushDir);
                Debug.Log($"[Result] {target.name} STAGGERED! (v_impact {impactVelocity:F2} >= 1.0 * {v_target:F2}) -> Dist: {displacementHexes}");
            }
            else if (impactVelocity > v_target * 0.5f)
            {
                 displacementHexes = Mathf.Max(displacementHexes, 1); // Ensure at least 1 hex for push
                 Debug.Log($"[Result] {target.name} Pushed Back {displacementHexes} Hexes! (v_impact {impactVelocity:F2} > 0.5 * {v_target:F2})");
                 target.OnImpact(timeline, impactVelocity, totalDamage, displacementHexes, pushDir);
            }
            else
            {
                Debug.Log($"[Result] {target.name} withstood the blow. Damage: {totalDamage:F2}");
                target.OnImpact(timeline, impactVelocity, totalDamage, 0, pushDir);
            }
        }

        private static float GetTransferCoefficient(ImpactType impact)
        {
            switch (impact)
            {
                case ImpactType.Blunt: return 1.0f;
                case ImpactType.Slash: return 0.6f;
                case ImpactType.Pierce: return 0.3f;
                default: return 1.0f;
            }
        }
    }
}
