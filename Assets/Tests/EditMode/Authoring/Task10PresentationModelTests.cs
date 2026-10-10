using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime.Input;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.UnityView;

namespace ProjectHero.Authoring.Tests
{
    /// <summary>Managed interaction tests, with no scene/native Unity calls. Logic evaluation has separate real-world tests.</summary>
    public sealed class Task10PresentationModelTests
    {
        private sealed class Relations : IFactionRelationResolver
        {
            public UnitRelation Classify(UnitId source, UnitId target) => source == target ? UnitRelation.Self : target.Value == 20 ? UnitRelation.Hostile : UnitRelation.Allied;
            public bool Allows(TargetRelationMask mask, UnitId source, UnitId target) => ((int)mask & (1 << (int)Classify(source, target))) != 0;
        }
        private sealed class Port : IViewLogicPort, IViewPreviewPort
        {
            public long CurrentTick { get; set; } = 5;
            public long ScheduleRevision { get; set; }
            public bool IsBattleEnded { get; set; }
            public bool IsPaused { get; set; }
            public ControllerId ControllerId => new ControllerId("controller.player");
            public WindowId? OpenWindowId => new WindowId(1);
            public List<CommandRequest> Submitted { get; } = new List<CommandRequest>();
            public List<ActionPlanSnapshot> Plans { get; } = new List<ActionPlanSnapshot> { new ActionPlanSnapshot(1, 10, "move", StartTick: 20, EndTick: 30),
                new ActionPlanSnapshot(2, 20, "secret-attack", StartTick: 40, EndTick: 50) };
            public List<ReactionOpportunitySnapshot> Offered { get; } = new List<ReactionOpportunitySnapshot>();
            public TurnWindowSnapshot CurrentWindow { get; } = new TurnWindowSnapshot(1, 10, 0, 180, 20, 30, 130, true, true, 0, Array.Empty<TurnWindowReservationSnapshot>());
            public IReadOnlyList<AdrenalineLedgerSnapshot> OwnAdrenaline { get; } = new[] { new AdrenalineLedgerSnapshot(10, 7, 2, Array.Empty<AdrenalineReservationSnapshot>(), 3) };
            public DecisionSnapshot DecisionSnapshot => new DecisionSnapshot(CurrentTick, "view-fixture", "view-hash",
                new[] { new UnitSnapshot(10, "unit", "hero", 0, 0, 0, 10240, true, 7, 2), new UnitSnapshot(20, "unit", "enemy", 2, 0, 0, 10240, true, 0, 0) },
                Plans, ScheduleRevision, new BattleEndSnapshot(IsBattleEnded, -1, null), new Relations())
                .ForController(ControllerId, new[] { new UnitId(10) }, CurrentWindow).WithDefinition(Definition());
            private static BattleDefinition Definition() => new BattleDefinition("view-fixture", 60, null, null, null, null, null, null, null, null,
                new[] { new ActionSpec(new ActionSpecId("dodge"), ActionType.Dodge, new DodgeReactionTimingSpec(2, 3), null, 1) },
                null, null, null, null, null, null, null, "view-hash");
            public CommandIngressRejection SubmitCommand(CommandRequest request) { Submitted.Add(request); return null; }
            public bool TryGetPlanSnapshot(ActionPlanId id, out ActionPlanSnapshot plan) { plan = Plans.FirstOrDefault(p => p.ActionPlanId == id.Value && p.OwnerUnitId == 10); return plan != null; }
            public bool TryFindEditablePlan(ActionPlanId id, out ActionPlanSnapshot plan)
            { return TryGetPlanSnapshot(id, out plan) && plan.State == (int)ActionPlanState.Editable && plan.Origin == (int)ActionPlanOrigin.Ordinary; }
            public IReadOnlyList<ActionPlanSnapshot> EditablePlansOf(ControllerId controller) => Plans.Where(p => p.OwnerUnitId == 10 && p.State == 0 && p.Origin == 0).ToArray();
            public bool TryGetActionType(string id, out ActionType type) { type = ActionType.Move; return true; }
            public IReadOnlyList<ReactionOpportunitySnapshot> ReactionOpportunitiesOf(ControllerId controller) => Offered.AsReadOnly();
            public string DescribeDodgeDestinationRejection(UnitId defender, ActionSpecId spec, GridPoint destination) => null;
            public ScheduleEditTransactionResult Preview(IReadOnlyList<ScheduleEditOperation> operations) => new ScheduleEditTransactionResult(false, true, null,
                ScheduleRevision, ScheduleRevision, Array.Empty<AppliedScheduleEdit>(), Array.Empty<ScheduleOperationEvaluation>(), Array.Empty<ActionPlanId>(), Array.Empty<ActionPlanId>(), Array.Empty<ActionPlanId>());
            public DodgeCancellationPreview PreviewDodge(ReactionOpportunityId id, ActionSpecId action, GridPoint destination)
                => new DodgeCancellationPreview(null, new[] { new ActionPlanId(1) }, new[] { new ConditionalBudgetRelease(new WindowId(1), 20, false) });
            public IReadOnlyList<GridPoint> DodgeDestinations(ReactionOpportunityId id, ActionSpecId action) => new[] { new GridPoint(2, 0) };
            public bool TryGetOwnTerminalPlan(ActionPlanId id, out ActionPlanSnapshot snapshot) { snapshot = null; return false; }
        }
        private static BattlePresentationModel Create(out Port port)
        { port = new Port(); var model = new BattlePresentationModel(); model.Bind(new ViewInputPorts(port, new ViewCommandFactory(port))); return model; }
        private static ScheduleEditOperation[] Move(long tick) => new ScheduleEditOperation[] { new MoveEditablePlanOperation(new ActionPlanId(1), tick) };
        [Test] public void TimelineShowsLockLineAtSnapshotTickPlusOne()
        { var model = Create(out var port); Assert.That(model.LockLine, Is.EqualTo(6)); port.CurrentTick = 13; Assert.That(model.LockLine, Is.EqualTo(14)); }
        [Test] public void TimelineDoesNotRevealOtherControllerUnpublishedEditablePlans()
        { var model = Create(out _); Assert.That(model.Timeline.Select(p => p.ActionPlanId), Is.EqualTo(new long[] { 1 })); }
        [Test] public void TimelineDragTrajectoryDoesNotProduceCombatCommands()
        { var model = Create(out var port); for (int i = 0; i < 50; i++) model.SetDraft(Move(40 + i)); Assert.That(port.Submitted, Is.Empty); Assert.That(port.ScheduleRevision, Is.Zero); }
        [Test] public void TimelineDragProducesOneAtomicScheduleEditOnConfirm()
        {
            var model = Create(out var port); for (int i = 0; i < 50; i++) model.SetDraft(Move(40 + i));
            Assert.That(model.ConfirmDraft(), Is.Null); Assert.That(port.Submitted.Count, Is.EqualTo(1));
            Assert.That(((ScheduleEditPayload)port.Submitted[0].Payload).Operations.Count, Is.EqualTo(1));
            Assert.That(((MoveEditablePlanOperation)((ScheduleEditPayload)port.Submitted[0].Payload).Operations[0]).RequestedStartTick, Is.EqualTo(89));
            Assert.That(model.HasDraft, Is.False); model.ConfirmDraft(); Assert.That(port.Submitted.Count, Is.EqualTo(1));
        }
        [Test] public void OpeningOrClosingActionPanelDoesNotCreateOrCommitDraftPhase()
        { var model = Create(out var port); for (int i = 0; i < 50; i++) model.SetActionPanelVisible(i % 2 == 0); Assert.That(model.HasDraft, Is.False); Assert.That(port.Submitted, Is.Empty); }
        [Test] public void StaleRevisionOrLockRejectionRebasesFromLatestSnapshot()
        {
            var model = Create(out var port); model.SetDraft(Move(40)); port.ScheduleRevision++;
            Assert.That(model.ConfirmDraft(), Is.EqualTo("STALE_SCHEDULE_REVISION")); Assert.That(model.HasDraft, Is.False); Assert.That(port.Submitted, Is.Empty);
            model.SetDraft(Move(45)); Assert.That(model.ConfirmDraft(), Is.Null);
            Assert.That(((ScheduleEditScope)port.Submitted.Single().Scope).ExpectedScheduleRevision, Is.EqualTo(1));
        }
        [Test] public void SystemAutoDeferralCancelsAffectedDraftAndRebasesTimeline()
        {
            var model = Create(out var port); model.SetDraft(Move(40)); port.ScheduleRevision = 1; port.CurrentTick = 6;
            port.Plans[0] = port.Plans[0] with { StartTick = 50, EndTick = 60, AutomaticDeferralCount = 1 };
            model.Synchronize(new EventBatch(6, new LogicEvent[] { new ActionPlanAutoDeferredEvent(6, 1, 1, 10, "CONTROL", 20, 50, 50, 1, new long[] { 1 }, 1) }));
            Assert.That(model.HasDraft, Is.False); Assert.That(model.Timeline.Single().StartTick, Is.EqualTo(50)); Assert.That(port.Submitted, Is.Empty);
        }
        [Test] public void TimelineCannotEditReactionLockedRunningOrTerminalPlans()
        {
            var model = Create(out var port); Assert.That(model.CanEdit(1), Is.True);
            foreach (var state in new[] { ActionPlanState.Locked, ActionPlanState.Running, ActionPlanState.Completed, ActionPlanState.Terminated })
            { port.Plans[0] = port.Plans[0] with { State = (int)state }; Assert.That(model.CanEdit(1), Is.False); }
            port.Plans[0] = port.Plans[0] with { State = 0, Origin = (int)ActionPlanOrigin.Reaction }; Assert.That(model.CanEdit(1), Is.False);
        }
        [Test] public void TurnBudgetHudSeparatesAvailableReservedAndSpent()
        { var model = Create(out var port); Assert.That(model.Window.AvailableBudgetTicks, Is.EqualTo(130)); Assert.That(model.Window.ReservedBudgetTicks, Is.EqualTo(20)); Assert.That(model.Window.SpentBudgetTicks, Is.EqualTo(30)); Assert.That(port.Submitted, Is.Empty); }
        [Test] public void AdrenalineHudSeparatesAvailableAndReservedWithoutLocalMutation()
        { var model = Create(out var port); Assert.That(model.Adrenaline.Single().AvailableAdrenaline, Is.EqualTo(7)); Assert.That(model.Adrenaline.Single().ReservedTotal, Is.EqualTo(3)); Assert.That(port.Submitted, Is.Empty); }
        [Test] public void DodgeConfirmationShowsLogicProvidedConditionalMoveCancellationAndBudgetByWindow()
        {
            var model = Create(out var port); var preview = model.PreviewDodge(1, new ActionSpecId("dodge"), new GridPoint(2, 0));
            Assert.That(preview.AffectedPlanIds.Single().Value, Is.EqualTo(1)); Assert.That(preview.ReleasesByWindow.Single().BudgetTicks, Is.EqualTo(20));
            Assert.That(preview.ReleasesByWindow.Single().WindowIsOpen, Is.False); Assert.That(model.Timeline.Count, Is.EqualTo(1)); Assert.That(port.Submitted, Is.Empty);
        }
        [Test] public void PendingDodgeMarksDependentMovesWithoutRemovingTimelineItems()
        {
            var model = Create(out var port); Assert.That(model.ConfirmReaction(1, new ActionSpecId("dodge"), new GridPoint(2, 0)), Is.Null);
            Assert.That(model.IsConditionallyInvalidated(1), Is.True); Assert.That(model.Timeline.Count, Is.EqualTo(1)); Assert.That(port.Submitted.Count, Is.EqualTo(1));
        }
    }
}
