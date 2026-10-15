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
        DodgeCancellationPreview PreviewDodge(ReactionOpportunityId opportunity, ActionSpecId action, GridPoint destination, long? startTick = null);
        IReadOnlyList<GridPoint> DodgeDestinations(ReactionOpportunityId opportunity, ActionSpecId action);
        TurnWindowSnapshot CurrentWindow { get; }
        IReadOnlyList<AdrenalineLedgerSnapshot> AllAdrenaline { get; }
        HudPreviewService HudPreviews { get; }
        ConcurrentActionSnapshot ConcurrentAction => ConcurrentActionSnapshot.None();
        bool TryGetOwnTerminalPlan(ActionPlanId planId, out ActionPlanSnapshot snapshot);
    }
    public sealed class ViewCommandReceipt
    {
        public long TargetTick { get; } public long ProducerOrdinal { get; } public bool Accepted { get; } public string Reason { get; }
        public ViewCommandReceipt(long targetTick, long ordinal, bool accepted, string reason)
        { TargetTick = targetTick; ProducerOrdinal = ordinal; Accepted = accepted; Reason = reason; }
    }
    public interface IViewCommandReceiptPort
    {
        long LastSubmittedOrdinal { get; }
        ViewCommandReceipt ReceiptFor(long targetTick, long ordinal, ProjectHero.Logic.Events.EventBatch events);
    }
}
