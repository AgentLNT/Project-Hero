using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;

namespace ProjectHero.Core.Interactions
{
    /// <summary>
    /// The central authority for resolving conflicts between CombatIntents.
    /// It receives a list of intents for the current frame, determines interactions (Clash, Parry, etc.),
    /// and executes the survivors.
    /// </summary>
    public static class CombatArbiter
    {
        public static void Resolve(List<CombatIntent> intents)
        {
            if (intents == null || intents.Count == 0) return;

            // 1. Interaction Detection Phase
            // We check every pair of intents to see if they interact.
            // O(N^2) is fine because N (units acting in the exact same frame) is usually very small (< 5).
            
            // We use a set to avoid processing the same pair twice (A vs B, then B vs A).
            HashSet<string> processedPairs = new HashSet<string>();

            for (int i = 0; i < intents.Count; i++)
            {
                for (int j = i + 1; j < intents.Count; j++)
                {
                    var intentA = intents[i];
                    var intentB = intents[j];

                    // Skip if either is already cancelled (optional, depends on if cancelled intents can still cause interactions)
                    // Usually, a cancelled attack (e.g. by a faster hit) shouldn't clash with a third attack.
                    if (intentA.IsCancelled || intentB.IsCancelled) continue;

                    InteractionType interaction = CheckInteraction(intentA, intentB);

                    if (interaction != InteractionType.None)
                    {
                        ApplyInteraction(intentA, intentB, interaction);
                    }
                }
            }

            // 2. Execution Phase
            // Run the OnSuccess callback for any intent that survived.
            foreach (var intent in intents)
            {
                if (!intent.IsCancelled)
                {
                    intent.OnSuccess?.Invoke();
                }
            }
        }

        private static InteractionType CheckInteraction(CombatIntent a, CombatIntent b)
        {
            // --- Attack vs Attack (Clash) ---
            if (a.Type == ActionType.Attack && b.Type == ActionType.Attack)
            {
                // Check Mutual Targeting
                // We need to know if A hits B AND B hits A.
                // The 'TargetUnit' field in Intent is a simplification. 
                // Realistically, we need to check the AttackPattern in 'Data'.
                
                bool aHitsB = IsTargeting(a, b.Owner);
                bool bHitsA = IsTargeting(b, a.Owner);

                if (aHitsB && bHitsA)
                {
                    return InteractionType.Clash;
                }
            }

            // --- Attack vs Block (Parry) ---
            // Check A attacking B, while B is Blocking
            if (a.Type == ActionType.Attack && b.Type == ActionType.Block)
            {
                if (IsTargeting(a, b.Owner)) return InteractionType.Parry;
            }
            if (b.Type == ActionType.Attack && a.Type == ActionType.Block)
            {
                if (IsTargeting(b, a.Owner)) return InteractionType.Parry;
            }

            // --- Attack vs Dodge (Dodge) ---
            if (a.Type == ActionType.Attack && b.Type == ActionType.Dodge)
            {
                if (IsTargeting(a, b.Owner)) return InteractionType.Dodge;
            }
            if (b.Type == ActionType.Attack && a.Type == ActionType.Dodge)
            {
                if (IsTargeting(b, a.Owner)) return InteractionType.Dodge;
            }

            // --- Attack vs Move (Intercept / Escape) ---
            if (a.Type == ActionType.Attack && b.Type == ActionType.Move)
            {
                return CheckAttackVsMove(a, b);
            }
            if (b.Type == ActionType.Attack && a.Type == ActionType.Move)
            {
                return CheckAttackVsMove(b, a);
            }

            return InteractionType.None;
        }

        private static InteractionType CheckAttackVsMove(CombatIntent attack, CombatIntent move)
        {
            // 1. Check if Attack covers the Start Position (Current GridPosition)
            bool hitsStart = IsTargeting(attack, move.Owner);
            
            // 2. Check if Attack covers the End Position (Intent Data)
            bool hitsEnd = false;
            if (move.Data is Pathfinder.GridPoint dest)
            {
                 hitsEnd = IsPointTargeted(attack, dest);
            }

            if (hitsEnd)
            {
                // Moving INTO attack -> Intercept
                // The attack hits the mover at the destination.
                // The move might be cancelled or just result in damage.
                // For now, let's call it Intercept.
                return InteractionType.Intercept;
            }
            else if (hitsStart && !hitsEnd)
            {
                // Moving OUT of attack -> Escape (Dodge)
                // The attack misses because the unit moved away.
                return InteractionType.Dodge; 
            }
            
            // If hitsStart is true but hitsEnd is false, it's an escape.
            // If hitsStart is false and hitsEnd is false, no interaction.
            
            return InteractionType.None;
        }

        private static bool IsTargeting(CombatIntent attackerIntent, CombatUnit potentialTarget)
        {
            // If explicit target is set
            if (attackerIntent.TargetUnit == potentialTarget) return true;

            // If using Area Pattern (more accurate)
            var action = attackerIntent.Data as Action;
            if (action != null && action.Pattern != null)
            {
                // We need the attacker's current position/facing.
                // Assuming the Owner is at the correct spot when the intent is generated.
                var area = action.Pattern.GetAffectedTriangles(attackerIntent.Owner.GridPosition, attackerIntent.Owner.FacingDirection);
                var units = GridManager.Instance.GetUnitsInArea(area, attackerIntent.Owner);
                return units.Contains(potentialTarget);
            }

            return false;
        }

        private static bool IsPointTargeted(CombatIntent attackerIntent, Pathfinder.GridPoint targetPoint)
        {
            var action = attackerIntent.Data as Action;
            if (action != null && action.Pattern != null)
            {
                var area = action.Pattern.GetAffectedTriangles(attackerIntent.Owner.GridPosition, attackerIntent.Owner.FacingDirection);
                // Check if any triangle in the area corresponds to the targetPoint
                // This requires mapping GridPoint to Triangles.
                // A GridPoint usually consists of 6 triangles (or fewer for edges).
                // But GetAffectedTriangles returns a list of TrianglePoints.
                // We need to check if any of those triangles belong to the targetPoint.
                
                foreach (var tri in area)
                {
                    if (tri.X == targetPoint.X && tri.Y == targetPoint.Y)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static void ApplyInteraction(CombatIntent a, CombatIntent b, InteractionType type)
        {
            Debug.Log($"[Arbiter] Interaction Resolved: {type} between {a.Owner.name} and {b.Owner.name}");

            switch (type)
            {
                case InteractionType.Clash:
                    // Both attacks are cancelled
                    a.Cancel();
                    b.Cancel();
                    a.OnInterrupted?.Invoke(InteractionType.Clash);
                    b.OnInterrupted?.Invoke(InteractionType.Clash);
                    // TODO: Spawn Clash VFX at midpoint
                    break;

                case InteractionType.Parry:
                    // Identify Attacker and Defender
                    var attacker = (a.Type == ActionType.Attack) ? a : b;
                    var defender = (a.Type == ActionType.Attack) ? b : a;

                    attacker.Cancel();
                    attacker.OnInterrupted?.Invoke(InteractionType.Parry);
                    defender.OnSuccess?.Invoke(); // Block succeeds
                    break;

                case InteractionType.Dodge:
                    // Attacker cancelled (missed), Dodger succeeds
                    attacker = (a.Type == ActionType.Attack) ? a : b;
                    defender = (a.Type == ActionType.Attack) ? b : a;

                    attacker.Cancel();
                    attacker.OnInterrupted?.Invoke(InteractionType.Dodge);
                    defender.OnSuccess?.Invoke();
                    break;

                case InteractionType.Intercept:
                    // Mover moves INTO attack.
                    // Attack succeeds (hits). Move might be interrupted?
                    // Let's say Attack hits, Move is interrupted (Knockback/Stagger logic handles the rest).
                    // Or maybe Move continues but takes damage?
                    // User said: "Winner is Attacker".
                    // So Attack succeeds, Move fails?
                    
                    var movingUnit = (a.Type == ActionType.Move) ? a : b;
                    var attackingUnit = (a.Type == ActionType.Move) ? b : a;
                    
                    // Attack proceeds (do nothing to attackingUnit)
                    // Move is interrupted
                    movingUnit.Cancel();
                    movingUnit.OnInterrupted?.Invoke(InteractionType.Intercept);
                    break;
            }
        }
    }
}