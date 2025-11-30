using UnityEngine;
using ProjectHero.Core.Entities;

namespace ProjectHero.Core.Physics
{
    public static class PhysicsEngine
    {
        public enum WeaponType { Blunt, Slash, Pierce }

        public static void ResolveCollision(CombatUnit attacker, CombatUnit target, WeaponType weapon, float baseMomentum)
        {
            float transferCoefficient = GetTransferCoefficient(weapon);
            
            // P_delivered = P_base * (M_attacker / 100) * K_w
            float deliveredMomentum = baseMomentum * (attacker.Mass / 100f) * transferCoefficient;
            
            // v_impact = P / M_target
            float impactVelocity = deliveredMomentum / target.Mass;
            
            // Thresholds (relative to target's swiftness/stability)
            // Simplified: using a base stability value or target's velocity if moving
            float stabilityThreshold = 5.0f; // Base stability
            
            Debug.Log($"Collision: P={deliveredMomentum}, V_impact={impactVelocity}");

            if (impactVelocity >= stabilityThreshold * 2.0f)
            {
                target.IsKnockedDown = true;
                target.OnImpact(impactVelocity, 10); // Bonus damage
                Debug.Log($"{target.name} KNOCKED DOWN!");
            }
            else if (impactVelocity >= stabilityThreshold * 1.5f)
            {
                target.IsStaggered = true;
                target.OnImpact(impactVelocity, 0);
                Debug.Log($"{target.name} STAGGERED!");
            }
            else
            {
                // Just displacement or minor hit
                target.OnImpact(impactVelocity, 0);
            }
        }

        private static float GetTransferCoefficient(WeaponType weapon)
        {
            switch (weapon)
            {
                case WeaponType.Blunt: return 1.0f;
                case WeaponType.Slash: return 0.6f;
                case WeaponType.Pierce: return 0.3f;
                default: return 1.0f;
            }
        }
    }
}
