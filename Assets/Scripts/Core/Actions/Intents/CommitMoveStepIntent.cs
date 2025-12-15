using System.Collections.Generic;
using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Interactions;
using ProjectHero.Core.Pathfinding;

namespace ProjectHero.Core.Actions.Intents
{
    /// <summary>
    /// Delayed logical commit for a movement step.
    /// Keeps the unit's logical GridPosition unchanged during the visual move,
    /// then applies the logical position update and releases any reservations.
    /// </summary>
    public sealed class CommitMoveStepIntent : CombatIntent
    {
        public Pathfinder.GridPoint From { get; }
        public Pathfinder.GridPoint To { get; }

        private List<TrianglePoint> _reservedVolume;

        public CommitMoveStepIntent(CombatUnit owner, Pathfinder.GridPoint from, Pathfinder.GridPoint to, List<TrianglePoint> reservedVolume)
            : base(owner, ActionType.None)
        {
            From = from;
            To = to;
            _reservedVolume = reservedVolume;
        }

        public override void ExecuteSuccess()
        {
            if (Owner == null)
            {
                ReleaseReservation();
                return;
            }

            // Release reservation first so the new occupancy isn't left with stale reservations.
            ReleaseReservation();

            // Apply the logical move.
            Owner.SetGridPosition(To);
        }

        public void ReleaseReservation()
        {
            if (_reservedVolume == null || _reservedVolume.Count == 0) return;

            if (GridManager.Instance != null && Owner != null)
            {
                GridManager.Instance.UnregisterReservation(Owner, _reservedVolume);
            }

            _reservedVolume = null;
        }
    }
}
