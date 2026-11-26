using System.Collections.Generic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;

namespace ProjectHero.Logic.AI
{
    public sealed record AiFollowUpAttack(ActionSpecId ActionSpecId,UnitId TargetUnitId,bool AttackPrefix=false);
    public sealed record AiApproachGoal(UnitId TargetUnitId,ActionSpecId AttackSpecId,GridPoint AttackOrigin,
        int InitialPathWeight,int RemainingPathWeight);

    public interface IAiSequenceForecast : IAiDefenseForecast
    {
        AiActionForecast EvaluateSequence(CommandRequest first,AiFollowUpAttack followUp,AiForecastSettings settings);
        IReadOnlyList<AiDefenseForecast> EvaluateSequenceDefenses(CommandRequest first,AiFollowUpAttack followUp,AiForecastSettings settings);
    }
    /// <summary>Read-only path query. Reuses the same production A* and occupancy rules as Move preview.</summary>
    public interface IAiMovementPaths
    {
        PathSearchResult FindAiPath(UnitId owner,GridPoint destination);
    }
}
