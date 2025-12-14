using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Interactions;

namespace ProjectHero.Core.Actions.Intents
{
    /// <summary>
    /// Sets the owner's facing at execution time, based on its then-current position and an aim point.
    /// Used to keep attacks consistent when previous planned moves change the starting position.
    /// </summary>
    public sealed class PlanFacingIntent : CombatIntent
    {
        public Pathfinder.GridPoint AimPoint { get; }

        public PlanFacingIntent(CombatUnit owner, Pathfinder.GridPoint aimPoint)
            : base(owner, ActionType.None)
        {
            AimPoint = aimPoint;
        }

        public override void ExecuteSuccess()
        {
            if (Owner == null) return;

            var dir = GridMath.GetDirection(Owner.GridPosition, AimPoint);
            Owner.SetFacingDirection(dir);
        }

        public override void ExecuteInterruption(InteractionType interactionType)
        {
            base.ExecuteInterruption(interactionType);
        }
    }
}
