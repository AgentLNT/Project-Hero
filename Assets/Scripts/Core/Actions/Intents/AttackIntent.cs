using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Interactions;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Physics;
using ProjectHero.Core.Timeline;
using System.Collections.Generic;
using ProjectHero.Visuals;

namespace ProjectHero.Core.Actions.Intents
{
    /// <summary>
    /// Represents the moment an attack actually hits (Impact Frame).
    /// </summary>
    public class AttackIntent : CombatIntent
    {
        public Action ActionDefinition { get; private set; }
        private BattleTimeline _timeline; // Needed to schedule effects or cancel targets

        private readonly HashSet<CombatUnit> _ignoredTargets = new HashSet<CombatUnit>();

        public AttackIntent(CombatUnit owner, Action action, BattleTimeline timeline) 
            : base(owner, ActionType.Attack)
        {
            ActionDefinition = action;
            _timeline = timeline;
        }

        public void IgnoreTarget(CombatUnit unit)
        {
            if (unit == null) return;
            _ignoredTargets.Add(unit);
        }

        public override void ExecuteSuccess()
        {
            // Check for Status Effects that prevent attacking
            if (Owner.IsStaggered || Owner.IsKnockedDown)
            {
                Debug.Log($"[AttackIntent] {Owner.name} cannot attack due to status. Cancelling.");
                return;
            }

            // 1. Determine Area
            if (ActionDefinition.Pattern == null)
            {
                Debug.LogWarning($"[Combat] Action {ActionDefinition.Name} has no AttackPattern!");
                return;
            }

            var area = ActionDefinition.Pattern.GetAffectedTriangles(Owner.GridPosition, Owner.FacingDirection);
            var targets = GridManager.Instance.GetUnitsInArea(area, Owner);

            // Build a fast lookup for area containment.
            var areaSet = new HashSet<TrianglePoint>(area);

            if (targets.Count == 0)
            {
                Debug.Log($"[Combat] {Owner.name} swung {ActionDefinition.Name} but hit nothing.");
                return;
            }

            Debug.Log($"[Combat] {Owner.name} hits {targets.Count} targets with {ActionDefinition.Name}!");

            // 2. Apply Damage / Effects
            foreach (var target in targets)
            {
                if (target.CurrentHealth <= 0) continue;
                if (_ignoredTargets.Contains(target)) continue;

                // If the target is currently moving visually, its logical occupancy may still be at the old cell.
                // Use the target's current visual position to decide whether it is actually inside the hit area.
                var movement = target.GetComponent<UnitMovement>();
                if (movement != null && movement.IsMoving)
                {
                    var visualGrid = GridManager.Instance.WorldToGrid(target.transform.position);
                    var visualOcc = target.GetProjectedOccupancy(visualGrid, target.FacingDirection);

                    bool anyInside = false;
                    for (int i = 0; i < visualOcc.Count; i++)
                    {
                        if (areaSet.Contains(visualOcc[i])) { anyInside = true; break; }
                    }

                    if (!anyInside) continue;
                }

                // PhysicsEngine handles everything: damage, status, and knockback scheduling.
                // CancelEvents is already called inside ScheduleKnockback if needed.
                PhysicsEngine.ResolveCollision(_timeline, Owner, target, ActionDefinition);
            }
        }
    }
}
