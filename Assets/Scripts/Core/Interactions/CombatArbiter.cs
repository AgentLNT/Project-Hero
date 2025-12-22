using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Actions.Intents;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Physics;
using ProjectHero.Visuals;
using ProjectHero.Core.Timeline;

namespace ProjectHero.Core.Interactions
{
    public static class CombatArbiter
    {
        public static void Resolve(List<CombatIntent> intents, BattleTimeline timeline)
        {
            if (intents == null || intents.Count == 0) return;

            HashSet<CombatIntent> processed = new HashSet<CombatIntent>();

            for (int i = 0; i < intents.Count; i++)
            {
                for (int j = i + 1; j < intents.Count; j++)
                {
                    var a = intents[i];
                    var b = intents[j];

                    if (processed.Contains(a) || processed.Contains(b)) continue;
                    if (a.IsCancelled || b.IsCancelled) continue;

                    InteractionType type = CheckInteraction(a, b);

                    if (type != InteractionType.None)
                    {
                        ApplyInteraction(a, b, type, timeline);

                        processed.Add(a);
                        processed.Add(b);
                    }
                }
            }
        }

        private static InteractionType CheckInteraction(CombatIntent a, CombatIntent b)
        {
            if (a.Type == ActionType.Attack && b.Type == ActionType.Attack)
            {
                if (IsTargeting(a, b.Owner) && IsTargeting(b, a.Owner)) return InteractionType.Clash;
            }

            if (IsAttackVsType(a, b, ActionType.Block, out var atk, out var blk))
            {
                if (IsTargeting(atk, blk.Owner)) return InteractionType.Parry;
            }

            if (IsAttackVsType(a, b, ActionType.Dodge, out atk, out var ddg))
            {
                if (IsTargeting(atk, ddg.Owner)) return InteractionType.Dodge;
            }

            if (IsAttackVsType(a, b, ActionType.Move, out atk, out var mov))
            {
                var moveIntent = (MoveIntent)mov;
                bool hitsStart = IsPointTargeted(atk, moveIntent.From);
                bool hitsEnd = IsPointTargeted(atk, moveIntent.To);

                if (hitsEnd) return InteractionType.Intercept;
                if (hitsStart && !hitsEnd) return InteractionType.Escape;
            }

            return InteractionType.None;
        }


        private static void ApplyInteraction(CombatIntent a, CombatIntent b, InteractionType type, BattleTimeline timeline)
        {
            Debug.Log($"[Arbiter] Resolving {type}...");

            switch (type)
            {
                case InteractionType.Clash:
                    var atk1 = (AttackIntent)a;
                    var atk2 = (AttackIntent)b;

                    float p1 = PhysicsEngine.CalculateMomentum(atk1.Owner, atk1.ActionDefinition);
                    float p2 = PhysicsEngine.CalculateMomentum(atk2.Owner, atk2.ActionDefinition);

                    atk1.Owner.CurrentFocus += 1;
                    atk2.Owner.CurrentFocus += 1;

                    atk1.Cancel();
                    atk2.Cancel();

                    float residual = p1 - p2;
                    if (residual > 0)
                    {
                        PhysicsEngine.ApplyClashResult(timeline, atk2.Owner, residual, atk1.Owner.GridPosition);
                    }
                    else if (residual < 0)
                    {
                        PhysicsEngine.ApplyClashResult(timeline, atk1.Owner, Mathf.Abs(residual), atk2.Owner.GridPosition);
                    }
                    else
                    {
                        if (GameFeelManager.Instance) GameFeelManager.Instance.ShowStatusText(atk1.Owner.transform.position, "EVEN!", Color.white);
                    }
                    break;


                case InteractionType.Parry:
                    GetAttackAndDefender(a, b, ActionType.Block, out var pAtk, out var pDef);

                    pAtk.Cancel();
                    pDef.Cancel();

                    if (GameFeelManager.Instance)
                    {
                        GameFeelManager.Instance.HitStop(0.1f);
                        GameFeelManager.Instance.ShowStatusText(pDef.Owner.transform.position, "PARRY", Color.cyan);
                    }
                    break;

                case InteractionType.Dodge:
                    GetAttackAndDefender(a, b, ActionType.Dodge, out var dAtk, out var dDef);

                    dAtk.Cancel();
                    if (timeline != null)
                    {
                        timeline.TriggerSlowMotion(0.05f, 2.0f);
                        timeline.RequestDodgeCounterMove(dDef.Owner); 
                    }

                    if (GameFeelManager.Instance) GameFeelManager.Instance.ShowStatusText(dDef.Owner.transform.position, "DODGE!", Color.green);
                    break;

                case InteractionType.Intercept:
                    GetAttackAndDefender(a, b, ActionType.Move, out var iAtk, out var iMov);
                    iAtk.Owner.CurrentFocus += 1;

                    iMov.Cancel();

                    iMov.Owner.IsKnockedDown = true;

                    if (GameFeelManager.Instance) GameFeelManager.Instance.ShowStatusText(iMov.Owner.transform.position, "INTERCEPT!", Color.red);
                    break;

                case InteractionType.Escape:
                    GetAttackAndDefender(a, b, ActionType.Move, out var eAtk, out var eMov);

                    eMov.Owner.CurrentFocus += 1;

                    if (GameFeelManager.Instance) GameFeelManager.Instance.ShowStatusText(eMov.Owner.transform.position, "ESCAPE", Color.green);
                    break;
            }
        }


        private static bool IsAttackVsType(CombatIntent a, CombatIntent b, ActionType targetType, out CombatIntent atk, out CombatIntent target)
        {
            if (a.Type == ActionType.Attack && b.Type == targetType) { atk = a; target = b; return true; }
            if (b.Type == ActionType.Attack && a.Type == targetType) { atk = b; target = a; return true; }
            atk = null; target = null; return false;
        }

        private static void GetAttackAndDefender(CombatIntent a, CombatIntent b, ActionType defType, out AttackIntent atk, out CombatIntent def)
        {
            if (a.Type == ActionType.Attack) { atk = (AttackIntent)a; def = b; }
            else { atk = (AttackIntent)b; def = a; }
        }

        private static bool IsTargeting(CombatIntent attackerIntent, CombatUnit potentialTarget)
        {
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

        private static bool IsPointTargeted(CombatIntent attackerIntent, Pathfinder.GridPoint point)
        {
            if (attackerIntent is AttackIntent attackIntent)
            {
                var action = attackIntent.ActionDefinition;
                if (action != null && action.Pattern != null)
                {
                    var area = action.Pattern.GetAffectedTriangles(attackerIntent.Owner.GridPosition, attackerIntent.Owner.FacingDirection);
                    foreach (var tri in area)
                    {
                        if (tri.X == point.X && tri.Y == point.Y) return true;
                    }
                }
            }
            return false;
        }
    }
}
