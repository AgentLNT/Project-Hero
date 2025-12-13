using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Actions.Intents; // Added
using ProjectHero.Core.Actions;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;


namespace ProjectHero.Core.Interactions
{
    public static class CombatArbiter
    {
        public static void Resolve(List<CombatIntent> intents)
        {
            if (intents == null || intents.Count == 0) return;

            // 1. Interaction Detection Phase
            for (int i = 0; i < intents.Count; i++)
            {
                for (int j = i + 1; j < intents.Count; j++)
                {
                    var intentA = intents[i];
                    var intentB = intents[j];

                    if (intentA.IsCancelled || intentB.IsCancelled) continue;

                    InteractionType interaction = CheckInteraction(intentA, intentB);

                    if (interaction != InteractionType.None)
                    {
                        ApplyInteraction(intentA, intentB, interaction);
                    }
                }
            }

            // 2. Execution Phase
            foreach (var intent in intents)
            {
                if (!intent.IsCancelled)
                {
                    intent.ExecuteSuccess();
                }
            }
        }

        private static InteractionType CheckInteraction(CombatIntent a, CombatIntent b)
        {
            // --- Attack vs Attack (Clash) ---
            if (a.Type == ActionType.Attack && b.Type == ActionType.Attack)
            {
                bool aHitsB = IsTargeting(a, b.Owner);
                bool bHitsA = IsTargeting(b, a.Owner);

                if (aHitsB && bHitsA) return InteractionType.Clash;
            }

            // --- Attack vs Block (Parry) ---
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
            bool hitsStart = IsTargeting(attack, move.Owner);
            bool hitsEnd = false;
            
            // Pattern Matching for MoveIntent
            if (move is MoveIntent moveIntent)
            {
                 hitsEnd = IsPointTargeted(attack, moveIntent.To);
            }

            if (hitsEnd) return InteractionType.Intercept;
            else if (hitsStart && !hitsEnd) return InteractionType.Dodge; 
            
            return InteractionType.None;
        }

        private static bool IsTargeting(CombatIntent attackerIntent, CombatUnit potentialTarget)
        {
            // Pattern Matching for AttackIntent
            if (attackerIntent is AttackIntent attackIntent)
            {
                var action = attackIntent.ActionDefinition;
                if (action != null && action.Pattern != null)
                {
                    var area = action.Pattern.GetAffectedTriangles(attackerIntent.Owner.GridPosition, attackerIntent.Owner.FacingDirection);
                    var units = GridManager.Instance.GetUnitsInArea(area, attackerIntent.Owner);
                    return units.Contains(potentialTarget);
                }
            }
            return false;
        }

        private static bool IsPointTargeted(CombatIntent attackerIntent, Pathfinder.GridPoint targetPoint)
        {
            if (attackerIntent is AttackIntent attackIntent)
            {
                var action = attackIntent.ActionDefinition;
                if (action != null && action.Pattern != null)
                {
                    var area = action.Pattern.GetAffectedTriangles(attackerIntent.Owner.GridPosition, attackerIntent.Owner.FacingDirection);
                    foreach (var tri in area)
                    {
                        if (tri.X == targetPoint.X && tri.Y == targetPoint.Y) return true;
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
                    a.Cancel();
                    b.Cancel();
                    a.ExecuteInterruption(InteractionType.Clash);
                    b.ExecuteInterruption(InteractionType.Clash);
                    break;

                case InteractionType.Parry:
                    var attacker = (a.Type == ActionType.Attack) ? a : b;
                    var defender = (a.Type == ActionType.Attack) ? b : a;
                    attacker.Cancel();
                    attacker.ExecuteInterruption(InteractionType.Parry);
                    defender.ExecuteSuccess(); 
                    break;

                case InteractionType.Dodge:
                    attacker = (a.Type == ActionType.Attack) ? a : b;
                    defender = (a.Type == ActionType.Attack) ? b : a;
                    attacker.Cancel();
                    attacker.ExecuteInterruption(InteractionType.Dodge);
                    defender.ExecuteSuccess();
                    break;

                case InteractionType.Intercept:
                    var movingUnit = (a.Type == ActionType.Move) ? a : b;
                    movingUnit.Cancel();
                    movingUnit.ExecuteInterruption(InteractionType.Intercept);
                    break;
            }
        }
    }
}