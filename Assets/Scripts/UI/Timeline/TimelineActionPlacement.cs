using System;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;

namespace ProjectHero.UI.Timeline
{
    public enum TimelineLane
    {
        Player,
        Observed
    }

    public enum TimelineActionKind
    {
        None,
        Move,
        Attack,
        Block,
        Dodge,
        Recover
    }

    public sealed class TimelineActionPlacement
    {
        public CombatUnit Owner;
        public TimelineActionKind Kind;
        public string Label;
        public float DurationSeconds;
        public TimelineLane Lane;

        // Optional metadata to support prediction/visualization.
        public Pathfinder.GridPoint? MoveDestination;
        public GridDirection? AttackFacingAbsolute;

        // startDelaySeconds is relative to current time when placed.
        // groupId is the group that should be used for all scheduled intents.
        public Action<float, long> Schedule;
    }
}
