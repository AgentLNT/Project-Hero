using System.Collections.Generic;
using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Interactions;

namespace ProjectHero.Core.Actions.Intents
{
    public sealed class PlanMoveIntent : CombatIntent
    {
        public Pathfinder.GridPoint Destination { get; }
        private readonly BattleTimeline _timeline;
        private readonly long _groupId;

        public PlanMoveIntent(CombatUnit owner, Pathfinder.GridPoint destination, BattleTimeline timeline, long groupId)
            : base(owner, ActionType.Move)
        {
            Destination = destination;
            _timeline = timeline;
            _groupId = groupId;
        }

        public override void ExecuteSuccess()
        {
            if (Owner == null || _timeline == null || GridManager.Instance == null) return;
            if (!Owner.gameObject.activeInHierarchy) return;

            var obstacles = GridManager.Instance.GetGlobalObstacles(Owner);
            var pathfinder = new Pathfinder();
            List<Pathfinder.GridPoint> path = pathfinder.FindPath(Owner.GridPosition, Destination, Owner.UnitVolumeDefinition, obstacles);

            if (path == null || path.Count < 2)
            {
                var endIntent = new StateChangeIntent(Owner, "Idle");
                _timeline.Schedule(0f, endIntent, "Move End (No Path)", _groupId, TimelinePriority.State);
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

                var step = new MoveIntent(Owner, from, to, duration, i, _timeline, _groupId);

                // Priority: State (50)
                _timeline.Schedule(accumulatedDelay, step, $"Move Step {i}", _groupId, TimelinePriority.State);

                accumulatedDelay += duration;
            }

            var end = new StateChangeIntent(Owner, "Idle");
            _timeline.Schedule(accumulatedDelay, end, "Move End", _groupId, TimelinePriority.State);
        }

        public override void ExecuteInterruption(InteractionType interactionType)
        {
            if (_timeline != null && _groupId != 0)
                _timeline.CancelGroup(_groupId);
            base.ExecuteInterruption(interactionType);
        }
    }
}
