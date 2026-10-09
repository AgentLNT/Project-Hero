using ProjectHero.Core.Entities;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Timeline;

namespace ProjectHero.Core.Actions
{
    public sealed class LegacyScheduledAction
    {
        public LegacyScheduledAction(BattleTimeline timeline, CombatUnit owner, ProjectHero.Logic.Actions.ActionType kind,
            Action attack, float startDelay, GridDirection facing, GridPoint? destination)
        { Timeline = timeline; Owner = owner; Kind = kind; Attack = attack; StartDelay = startDelay; Facing = facing; Destination = destination; }
        public BattleTimeline Timeline { get; }
        public CombatUnit Owner { get; }
        public ProjectHero.Logic.Actions.ActionType Kind { get; }
        public Action Attack { get; }
        public float StartDelay { get; }
        public GridDirection Facing { get; }
        public GridPoint? Destination { get; }
    }
}
