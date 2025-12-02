using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Actions;

namespace ProjectHero.Core.Physics
{
    // Renamed from WeaponType to ImpactType to reflect the Action's nature, not the tool
    public enum ImpactType { Blunt, Slash, Pierce }

    public static class PhysicsEngine
    {
        public static void ResolveCollision(CombatUnit attacker, CombatUnit target, CombatAction action)
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

            // 2. Impact Damage: D_impact = P_delivered * 0.1
            float impactDamage = deliveredMomentum * 0.1f;

            float totalDamage = physicalDamage + impactDamage;
            
            Debug.Log($"[Physics] {attacker.name} uses {action.Name} on {target.name}");
            Debug.Log($"[Calc] M_atk={attacker.TotalMass}, v_atk={attacker.Swiftness}, Kw={Kw}, Mult={action.ForceMultiplier} => P_delivered={deliveredMomentum:F2}");
            Debug.Log($"[Calc] M_target={target.TotalMass} => v_impact={impactVelocity:F2}");
            Debug.Log($"[Calc] D_base={action.BaseDamage}, Armor={target.ArmorDefense} => D_phys={physicalDamage:F2}");
            Debug.Log($"[Calc] D_impact={impactDamage:F2} (P*0.1) => Total={totalDamage:F2}");

            // Thresholds (Design Section IV.B)
            // Thresholds are relative to target's Swiftness (v_Target)
            float v_target = target.Swiftness;

            // Displacement (Design Section IV.B)
            // D_hexes = floor(v_impact / (v_target * mu))
            float friction = 1.0f; // Default friction mu
            int displacementHexes = Mathf.FloorToInt(impactVelocity / (v_target * friction));

            if (impactVelocity >= v_target * 2.0f)
            {
                target.IsKnockedDown = true;
                target.OnImpact(impactVelocity, totalDamage); 
                Debug.Log($"[Result] {target.name} KNOCKED DOWN! (v_impact {impactVelocity:F2} >= 2.0 * {v_target:F2})");
            }
            else if (impactVelocity >= v_target * 1.5f)
            {
                target.IsStaggered = true;
                target.OnImpact(impactVelocity, totalDamage);
                Debug.Log($"[Result] {target.name} STAGGERED! (v_impact {impactVelocity:F2} >= 1.5 * {v_target:F2})");
            }
            else if (impactVelocity > v_target * 1.0f)
            {
                 Debug.Log($"[Result] {target.name} Pushed Back {displacementHexes} Hexes! (v_impact {impactVelocity:F2} > {v_target:F2})");
                 target.OnImpact(impactVelocity, totalDamage);
            }
            else
            {
                Debug.Log($"[Result] {target.name} withstood the blow. Damage: {totalDamage:F2}");
                target.OnImpact(impactVelocity, totalDamage);
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
