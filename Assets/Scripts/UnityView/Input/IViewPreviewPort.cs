using System.Collections.Generic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Core.Compatibility.Runtime.Input
{
    public interface IViewPreviewPort
    {
        ScheduleEditTransactionResult Preview(IReadOnlyList<ScheduleEditOperation> operations);
        DodgeCancellationPreview PreviewDodge(ReactionOpportunityId opportunity, ActionSpecId action, GridPoint destination);
        IReadOnlyList<GridPoint> DodgeDestinations(ReactionOpportunityId opportunity, ActionSpecId action);
        TurnWindowSnapshot CurrentWindow { get; }
        IReadOnlyList<AdrenalineLedgerSnapshot> OwnAdrenaline { get; }
        bool TryGetOwnTerminalPlan(ActionPlanId planId, out ActionPlanSnapshot snapshot);
    }
}
