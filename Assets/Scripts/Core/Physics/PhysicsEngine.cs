using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Timeline;
using ProjectHero.Visuals;

namespace ProjectHero.Core.Physics
{
    public enum ImpactType { Blunt, Slash, Pierce }

    public static class PhysicsEngine
    {
        public static bool CheckIntersection(List<TrianglePoint> volumeA, List<TrianglePoint> volumeB)
        {
            if (volumeA == null || volumeB == null) return false;
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
            float deliveredMomentum = attacker.TotalMass * attacker.Swiftness * Kw * action.ForceMultiplier;
            float impactVelocity = deliveredMomentum / target.TotalMass;

            float armorReduction = target.ArmorDefense / (target.ArmorDefense + 100f);
            float physicalDamage = action.BaseDamage * (1.0f - armorReduction);
            float impactDamage = deliveredMomentum * 0.02f;
            float totalDamage = physicalDamage + impactDamage;

            attacker.CurrentAdrenaline += totalDamage * 0.1f;
            target.CurrentAdrenaline += totalDamage * 0.2f;
            attacker.CurrentAdrenaline = Mathf.Min(attacker.CurrentAdrenaline, 100f);
            target.CurrentAdrenaline = Mathf.Min(target.CurrentAdrenaline, 100f);

            Debug.Log($"[Physics] {attacker.name} hits {target.name} | Damage: {totalDamage:F1} | ImpactV: {impactVelocity:F1}");

            // --- JUICE & FEEDBACK ---
            ApplyGameFeel(attacker, target, totalDamage, impactVelocity);
            // -----------------------

            float v_target = target.Swiftness;
            float friction = 1.0f;
            int displacementHexes = Mathf.FloorToInt(impactVelocity / (v_target * friction));
            GridDirection pushDir = GridMath.GetDirection(attacker.GridPosition, target.GridPosition);

            if (impactVelocity >= v_target * 1.5f)
            {
                target.IsKnockedDown = true;
                if (GameFeelManager.Instance != null)
                    GameFeelManager.Instance.ShowStatusText(target.transform.position, "KNOCKDOWN!", Color.yellow);

                target.OnImpact(timeline, impactVelocity, totalDamage, displacementHexes, pushDir);
            }
            else if (impactVelocity >= v_target * 1.0f)
            {
                target.IsStaggered = true;
                if (GameFeelManager.Instance != null)
                    GameFeelManager.Instance.ShowStatusText(target.transform.position, "STAGGER", Color.cyan);

                target.OnImpact(timeline, impactVelocity, totalDamage, displacementHexes, pushDir);
            }
            else if (impactVelocity > v_target * 0.5f)
            {
                target.OnImpact(timeline, impactVelocity, totalDamage, displacementHexes, pushDir);
            }
            else
            {
                target.OnImpact(timeline, impactVelocity, totalDamage, 0, pushDir);
            }
        }

        private static void ApplyGameFeel(CombatUnit attacker, CombatUnit target, float damage, float impactVelocity)
        {
            if (GameFeelManager.Instance == null) return;

            bool isBigHit = impactVelocity > 15f || damage > 40f;
            GameFeelManager.Instance.ShowDamageNumber(target.transform.position, damage, isBigHit);

            float intensity = Mathf.Clamp01(damage / 50f + impactVelocity / 20f);
            if (intensity > 0.1f)
            {
                GameFeelManager.Instance.ScreenShake(intensity * 0.5f, 0.1f + intensity * 0.3f);
            }

            if (intensity > 0.2f)
            {
                GameFeelManager.Instance.HitStop(0.05f + intensity * 0.1f);
            }

            var targetBounce = target.GetComponent<UnitBounce>();
            if (targetBounce != null)
            {
                targetBounce.OnImpact(intensity);
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
