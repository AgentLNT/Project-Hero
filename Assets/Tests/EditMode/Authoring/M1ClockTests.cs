using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
using ProjectHero.UnityView;

namespace ProjectHero.Authoring.Tests
{
    public sealed class M1ClockTests
    {
        private sealed class Source : IBattleSimulationSource
        {
            public string SourceName => "M1 production definitions";
            public string LastConfigurationError => null;
            public BattleSimulationSeed BuildSeed() => new BattleSimulationSeed(Task03.Task03.Definition, Task03.Task03.EncounterId, Task03.Task03.Inputs, "M1");
        }
        private sealed class Receiver : IBattleViewConsumer
        {
            public Action<EventBatch> OnTick;
            public void Bind(ProjectHero.Logic.Initialization.BattleInitializationResult initial, LogicSnapshot snapshot) { }
            public void Consume(EventBatch events, LogicSnapshot snapshot) => OnTick?.Invoke(events);
        }
        private static UnityBattleDriver Driver(IBattleViewConsumer view = null)
        {
            var source = new Source(); var driver = new UnityBattleDriver();
            driver.BindView(view);
            driver.Initialize(new BattleRuntimeContext(source));
            driver.CreateSimulation(source); return driver;
        }
        [Test] public void SystemPauseRestoresTacticalStateAndCannotBeResumedBySpace()
        {
            var clock = new BattleClockController(); clock.PauseTactically(); clock.SetSystemPaused(true); clock.ResumeTactically();
            Assert.That(clock.State, Is.EqualTo(BattleClockState.SystemPaused)); clock.SetSystemPaused(false);
            Assert.That(clock.State, Is.EqualTo(BattleClockState.TacticalPlanning)); clock.End(); clock.SetSystemPaused(false);
            Assert.That(clock.State, Is.EqualTo(BattleClockState.Ended));
        }
        [TestCase(BattleClockState.Running, HudSubmissionPermission.OwnWindow, false, false)]
        [TestCase(BattleClockState.TacticalPlanning, HudSubmissionPermission.OwnWindow, true, false)]
        [TestCase(BattleClockState.TacticalPlanning, HudSubmissionPermission.CanIntervene, false, false)]
        [TestCase(BattleClockState.TacticalPlanning, HudSubmissionPermission.Intervened, true, false)]
        [TestCase(BattleClockState.SystemPaused, HudSubmissionPermission.OwnWindow, false, true)]
        [TestCase(BattleClockState.Ended, HudSubmissionPermission.OwnWindow, false, true)]
        public void LayoutUsesIndependentFacts(BattleClockState clock, HudSubmissionPermission permission, bool configure, bool overlay)
        {
            var result = HudLayoutResolver.Resolve(new HudFacts(clock, permission, true));
            Assert.That(result.Configurator, Is.EqualTo(configure)); Assert.That(result.Overlay, Is.EqualTo(overlay));
        }
        [Test] public void FirstWindowPausesAtCommitAndDiscardsSameFrameDebt()
        {
            var driver = Driver();
            try
            {
                driver.EnableHudClock(new ControllerId("controller.player")); driver.AdvanceFrame(new BattleFrameDelta(1, false, true));
                Assert.That(driver.CurrentSnapshot.Tick, Is.Zero); Assert.That(driver.Clock.State, Is.EqualTo(BattleClockState.TacticalPlanning));
                var hash = driver.CurrentSnapshot.ComputeHash(); driver.AdvanceFrame(new BattleFrameDelta(10, false, true));
                Assert.That(driver.CurrentSnapshot.ComputeHash(), Is.EqualTo(hash));
                driver.ResumeTactically(); driver.AdvanceFrame(new BattleFrameDelta(0, false, true)); Assert.That(driver.CurrentSnapshot.Tick, Is.Zero);
                driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true)); Assert.That(driver.CurrentSnapshot.Tick, Is.EqualTo(1));
            }
            finally { driver.ResetForNewBattle(); }
        }
        [Test] public void TacticalDraftsRemainLocalThenSubmitOneAtomicTransactionAndReceiveReceipt()
        {
            var driver = Driver();
            try
            {
                driver.EnableHudClock(new ControllerId("controller.player")); driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true));
                var ports = driver.CreatePlayerInputPorts(new ControllerId("controller.player")); var model = new BattlePresentationModel();
                model.BindClock(driver.Clock); model.Bind(ports); var hero = model.Decision.ControlledUnitIds.First();
                var guard = model.Decision.ActionSetOf(hero).First(id => model.Decision.FindAction(id).Type == ProjectHero.Logic.Actions.ActionType.Guard);
                var before = driver.CurrentSnapshot.ComputeHash();
                long first = model.AppendDraft(new AddOrdinaryPlanOperation(1, hero, guard, 5));
                long second = model.AppendDraft(new AddOrdinaryPlanOperation(1, hero, guard, 80));
                Assert.That(first, Is.Not.EqualTo(second)); Assert.That(model.Preview.Succeeded, Is.True, model.Preview.RejectionCode);
                Assert.That(model.ConfirmDraft(), Is.Null); Assert.That(model.Drafts.State, Is.EqualTo(ScheduleDraftState.AwaitingResume));
                Assert.That(driver.CurrentSnapshot.ComputeHash(), Is.EqualTo(before)); Assert.That(model.SubmittedCommands, Is.Zero);
                driver.ResumeTactically(); Assert.That(model.SubmitOnResume(), Is.Null); Assert.That(model.Drafts.State, Is.EqualTo(ScheduleDraftState.AwaitingAcceptance));
                Assert.That(model.ConfirmDraft(), Is.Not.Null); Assert.That(model.SubmittedCommands, Is.EqualTo(1));
                driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true));
                var receipts = (ProjectHero.Core.Compatibility.Runtime.Input.IViewCommandReceiptPort)ports.Logic;
                Assert.That(driver.CurrentSnapshot.Plans.Count(p => p.OwnerUnitId == hero.Value), Is.EqualTo(2));
                Assert.That(receipts.ReceiptFor(1, receipts.LastSubmittedOrdinal, new EventBatch(1, Array.Empty<LogicEvent>())).Accepted, Is.True);
                model.Synchronize(new EventBatch(1, Array.Empty<LogicEvent>()));
                Assert.That(model.Drafts.State, Is.EqualTo(ScheduleDraftState.Accepted)); Assert.That(model.Drafts.Entries, Is.Empty);
            }
            finally { driver.ResetForNewBattle(); }
        }
        [Test] public void StableDraftRemovalAndReplacementKeepOtherIntent()
        {
            var drafts = new ScheduleDraftModel(); var a = new UnitId(1); var action = new ActionSpecId("move");
            long first = drafts.Append(new AddOrdinaryPlanOperation(1, a, action)); long second = drafts.Append(new AddOrdinaryPlanOperation(1, a, action));
            drafts.Replace(first, new AddOrdinaryPlanOperation(1, a, action, 10)); drafts.Remove(first);
            Assert.That(drafts.Entries.Single().Id, Is.EqualTo(second)); Assert.That(((AddOrdinaryPlanOperation)drafts.Operations.Single()).TemporaryPlanKey, Is.EqualTo(second));
        }
        [Test] public void SystemPauseKeepsLocalIntentButRejectsEveryWriteEntrance()
        {
            var driver = Driver();
            try
            {
                driver.EnableHudClock(new ControllerId("controller.player")); driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true));
                var ports = driver.CreatePlayerInputPorts(new ControllerId("controller.player")); var model = new BattlePresentationModel();
                model.BindClock(driver.Clock); model.Bind(ports); var hero = model.Decision.ControlledUnitIds.Single();
                var guard = model.Decision.ActionSetOf(hero).First(id => model.Decision.FindAction(id).Type == ProjectHero.Logic.Actions.ActionType.Guard);
                var operation = new AddOrdinaryPlanOperation(1, hero, guard, 10); model.AppendDraft(operation);
                driver.SetPaused(true); Assert.That(model.CanPlan, Is.False); Assert.That(model.AppendDraft(operation), Is.Zero);
                Assert.That(model.ConfirmDraft(), Is.EqualTo("VIEW_INPUT_UNAVAILABLE"));
                Assert.That(ports.Commands.CreateScheduleEdit(new ScheduleEditPayload(new[] {operation})), Is.Null);
                Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() => ports.Logic.SubmitCommand(
                    new CommandRequest(1, new ScheduleEditScope(0, new WindowId(1)), new ScheduleEditPayload(new[] {operation}))));
                Assert.That(model.Drafts.Entries.Count, Is.EqualTo(1)); driver.SetPaused(false);
                Assert.That(driver.Clock.State, Is.EqualTo(BattleClockState.TacticalPlanning)); Assert.That(model.CanPlan, Is.True);
            }
            finally { driver.ResetForNewBattle(); }
        }
        [Test] public void AuthorityRejectionRetainsIntentAndDoesNotRetryOrMisattributeOtherCommands()
        {
            var receiver = new Receiver(); var driver = Driver(receiver);
            try
            {
                driver.EnableHudClock(new ControllerId("controller.player")); driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true));
                var ports = driver.CreatePlayerInputPorts(new ControllerId("controller.player")); var model = new BattlePresentationModel();
                model.BindClock(driver.Clock); model.Bind(ports); receiver.OnTick = model.Synchronize;
                var hero = model.Decision.ControlledUnitIds.Single();
                var guard = model.Decision.ActionSetOf(hero).First(id => model.Decision.FindAction(id).Type == ProjectHero.Logic.Actions.ActionType.Guard);
                model.AppendDraft(new AddOrdinaryPlanOperation(1, hero, guard, 20)); model.ConfirmDraft();
                driver.ResumeTactically(); model.SubmitOnResume(); driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true));
                var plan = model.Decision.OwnPlans.Single(); driver.Clock.PauseTactically();
                model.AppendDraft(new MoveEditablePlanOperation(new ActionPlanId(plan.ActionPlanId), 90)); model.ConfirmDraft();
                driver.ResumeTactically();
                Assert.That(ports.Logic.SubmitCommand(ports.Commands.CreateScheduleEdit(new ScheduleEditPayload(new[] {new RemoveEditablePlanOperation(new ActionPlanId(plan.ActionPlanId))}))), Is.Null);
                Assert.That(model.SubmitOnResume(), Is.Null); driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true));
                Assert.That(model.Drafts.State, Is.EqualTo(ScheduleDraftState.Rejected)); Assert.That(model.Drafts.Entries.Count, Is.EqualTo(1));
                Assert.That(model.LastRejection, Is.Not.Null); long count = model.SubmittedCommands;
                driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true)); Assert.That(model.SubmittedCommands, Is.EqualTo(count));
            }
            finally { driver.ResetForNewBattle(); }
        }
    }
}
