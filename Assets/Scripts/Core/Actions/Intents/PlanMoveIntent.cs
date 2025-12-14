using System.Collections.Generic;
using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Interactions;

namespace ProjectHero.Core.Actions.Intents
{
    /// <summary>
    /// Computes a move path at execution time (based on the unit's then-current position)
    /// and schedules the concrete MoveIntent steps into the timeline.
    /// This enables sequential plans to chain correctly.
    /// </summary>
    public sealed class PlanMoveIntent : CombatIntent
    {
        public Pathfinder.GridPoint Destination { get; }

        private readonly BattleTimeline _timeline;
        private readonly long _groupId;

        public PlanMoveIntent(CombatUnit owner, Pathfinder.GridPoint destination, BattleTimeline timeline, long groupId)
            : base(owner, ActionType.None)
        {
            Destination = destination;
            _timeline = timeline;
            _groupId = groupId;
        }

        public override void ExecuteSuccess()
        {
            if (Owner == null || _timeline == null || GridManager.Instance == null) return;

            // If the unit is dead/disabled, just bail.
            if (!Owner.gameObject.activeInHierarchy) return;

            // Compute obstacles (ignore self)
            var obstacles = GridManager.Instance.GetGlobalObstacles(Owner);

            var pathfinder = new Pathfinder();
            List<Pathfinder.GridPoint> path = pathfinder.FindPath(Owner.GridPosition, Destination, Owner.UnitVolumeDefinition, obstacles);

            if (path == null || path.Count < 2)
            {
                // Nothing to do; ensure we don't remain Busy forever.
                var endIntent = new StateChangeIntent(Owner, "Idle");
                _timeline.Schedule(0f, endIntent, "Move End (No Path)", _groupId);
                return;
            }

            float accumulatedDelay = 0f;

            for (int i = 1; i < path.Count; i++)
            {
                var from = path[i - 1];
                var to = path[i];

                var moveDir = GridMath.GetDirection(from, to);
                float distance = ((int)moveDir % 2 != 0) ? 2.0f : 1.0f;
                float speed = Mathf.Max(1f, Owner.Swiftness);
                float duration = Mathf.Clamp(distance / (speed * 0.1f), 0.2f, 4.0f);

                var step = new MoveIntent(Owner, from, to, duration, i, _timeline);
                _timeline.Schedule(accumulatedDelay, step, $"Move Step {i}", _groupId);
                accumulatedDelay += duration;
            }

            var end = new StateChangeIntent(Owner, "Idle");
            _timeline.Schedule(accumulatedDelay, end, "Move End", _groupId);
        }

        public override void ExecuteInterruption(InteractionType interactionType)
        {
            // If planning was interrupted, cancel the whole group so we don't leave stray steps.
            if (_timeline != null && _groupId != 0)
            {
                _timeline.CancelGroup(_groupId);
            }
            base.ExecuteInterruption(interactionType);
        }
    }
}
