using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Timeline;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.UnityView;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class M3HudPlayModeTests : ProductionRuntimeTestBase
    {
        private object _keyboard;
        [TearDown] public void RemoveKeyboard()
        {
            if (_keyboard == null) return;
            Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true).GetMethod("RemoveDevice").Invoke(null, new[] {_keyboard});
            _keyboard = null;
        }
        private void QueueKey(params string[] names)
        {
            var input = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true);
            var keyboard = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem", true);
            if (_keyboard == null) _keyboard = input.GetMethods().Single(m => m.Name == "AddDevice" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                .MakeGenericMethod(keyboard).Invoke(null, new object[] {"M3TestKeyboard"});
            var key = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem", true);
            var keys = Array.CreateInstance(key, names.Length);
            for (int i = 0; i < names.Length; i++) keys.SetValue(Enum.Parse(key, names[i]), i);
            var state = Type.GetType("UnityEngine.InputSystem.LowLevel.KeyboardState, Unity.InputSystem", true);
            input.GetMethods().Single(m => m.Name == "QueueStateEvent" && m.IsGenericMethodDefinition).MakeGenericMethod(state)
                .Invoke(null, new[] {_keyboard, Activator.CreateInstance(state, new object[] {keys}), (object)(-1d)});
        }
        protected override bool UseHudAutoPause => true;
        private void Drive(long tick)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Simulation.Tick < tick)
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline));
                Bootstrap.Clock.ResumeTactically(); Bootstrap.DriveFrameForTests(0, 1f / 60);
            }
        }
        private static UnityEngine.UI.Button Button(BattlePresentationView view, string name) => view.GetComponentsInChildren<UnityEngine.UI.Button>(true).Single(b => b.name == name);
        private static void Click(UnityEngine.UI.Button button) => ExecuteEvents.Execute(button.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
        static object Field(object o, string name) => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
        private BattlePresentationView View => UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>();
        private BattleSimulation Simulation => (BattleSimulation)Field(Bootstrap.NewDriver, "_simulation");
        private void SubmitEnemy(UnitId owner, params ScheduleEditOperation[] operations)
        {
            var s = Simulation;
            var controller = s.Definition.FindEncounter(Bootstrap.NewDriver.ReplayHeader.EncounterId).Controllers.Single(c => c.ControllerId.Value != View.PlayerControllerId).ControllerId;
            Assert.That(s.CommandIngress.FindEntry(controller).Submit(new CommandRequest(s.Tick + 1,
                new ScheduleEditScope(s.ScheduleRevision, s.CurrentTurnWindow.WindowId), new ScheduleEditPayload(operations))), Is.Null);
        }
        private IEnumerator ReactionFixture(bool funds, bool earlier)
        {
            yield return LoadMainScene(); Bootstrap.StopBattle("M3-fixture"); Bootstrap.ReleaseBattle();
            var original = ((IBattleSimulationSource)Field(Bootstrap, "_simulationSourceSlot")).BuildSeed();
            var source = NewGameObject("M3ExplicitSceneFixture").AddComponent<Task11SceneScenarioSource>();
            source.Seed = Task12BSceneEvidenceTests.Configure(original);
            Bootstrap.GetType().GetField("_simulationSourceSlot", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Bootstrap, source);
            var view = View; var oldViews = (CombatUnitView[])Field(view.Registry, "_views");
            var thirdView = NewGameObject("M3ThirdAttacker").AddComponent<CombatUnitView>(); thirdView.Configure("t12b_third", UnityEngine.Object.FindFirstObjectByType<GridView>());
            view.Registry.Configure(oldViews.Concat(new[] {thirdView}).ToArray());
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection); Drive(0);
            var hero = View.Model.Decision.ControlledUnitIds.Single(); var third = Simulation.CurrentTurnWindow.OwnerUnitId;
            var warm = new ActionSpecId("action.t12b.scene.warm"); var light = new ActionSpecId("action.t12b.scene.light");
            var operations = new System.Collections.Generic.List<ScheduleEditOperation>();
            if (funds) operations.Add(new AddOrdinaryPlanOperation(1, third, warm, 1, PrimaryTargetUnitId: hero));
            if (earlier) operations.Add(new AddOrdinaryPlanOperation(2, third, light, 179, PrimaryTargetUnitId: hero));
            if (operations.Count > 0) SubmitEnemy(third, operations.ToArray());
            Drive(180); var enemy = Simulation.CurrentTurnWindow.OwnerUnitId;
            SubmitEnemy(enemy, new AddOrdinaryPlanOperation(1, enemy, new ActionSpecId("action.t12b.scene.heavy"), 181, PrimaryTargetUnitId: hero));
            Drive(181); View.ReactionView.Refresh(true);
        }
        private static string Texts(BattlePresentationView view) => string.Join(";", view.GetComponentsInChildren<Component>(true)
            .Where(t => t.GetType().FullName == "TMPro.TextMeshProUGUI").Select(t => (string)t.GetType().GetProperty("text").GetValue(t)));
        [UnityTest] public IEnumerator QueuedKeyboardEventsSelectCancelAndConfirmOnlyWhilePlanning()
        {
            yield return ReactionFixture(true, true); var view = View;
            QueueKey("D"); yield return null; yield return null;
            Assert.That(view.ReactionView.AwaitingDestination, Is.True);
            var candidate = view.ReactionView.Candidate;
            Assert.That(view.Model.DodgeDestinations(candidate.OpportunityId, candidate.Action).Any(view.ReactionView.SelectDestination), Is.True);
            var card = view.GetComponentsInChildren<RectTransform>(true).Single(r => r.name == "Reaction-" + candidate.OpportunityId);
            var corners = new Vector3[4]; var viewport = new Vector3[4];
            card.GetWorldCorners(corners); ((RectTransform)card.parent.parent).GetWorldCorners(viewport);
            Assert.That(corners[1].y, Is.LessThanOrEqualTo(viewport[1].y + 1), "With multiple cards, the selected first card's title must stay visible after picking a destination.");
            Assert.That(corners[0].y, Is.GreaterThanOrEqualTo(viewport[0].y - 1));
            QueueKey(); yield return null; QueueKey("Escape"); yield return null; yield return null;
            Assert.That(view.ReactionView.Candidate, Is.Null); Assert.That(view.Clock.State, Is.EqualTo(BattleClockState.TacticalPlanning));
            QueueKey(); yield return null; QueueKey("B"); yield return null; yield return null;
            var selected = view.ReactionView.Candidate;
            Assert.That(view.Model.Decision.FindAction(selected.Action).Type, Is.EqualTo(ActionType.Block));
            QueueKey(); yield return null; Bootstrap.SetPaused(true);
            QueueKey("D"); yield return null; yield return null;
            Assert.That(view.ReactionView.Candidate, Is.EqualTo(selected)); Assert.That(view.Model.StagedReactionCount, Is.Zero);
            QueueKey(); yield return null; Bootstrap.SetPaused(false);
            QueueKey("Enter"); yield return null; yield return null;
            Assert.That(view.Model.StagedReactionCount, Is.EqualTo(1)); Assert.That(view.Model.SubmittedCommands, Is.Zero);
            QueueKey(); yield return null;
        }
        [UnityTest] public IEnumerator ConcurrentWaitsForAuthoritativeReceiptThenAllowsGuardInForeignWindow()
        {
            yield return LoadMainScene(); var view = UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>(); var model = view.Model;
            Drive(181); view.Clock.PauseTactically();
            var hero = model.Decision.ControlledUnitIds.Single(); Assert.That(model.Window.OwnerUnitId, Is.Not.EqualTo(hero.Value));
            long tick = model.Decision.Tick; string hash = Bootstrap.NewDriver.CurrentSnapshot.ComputeHashHex();
            Assert.That(model.ActivateConcurrent(), Is.Null); Assert.That(model.ConcurrentStatus, Does.Contain("待恢复"));
            Assert.That(model.SubmittedCommands, Is.Zero); Assert.That(model.Facts.Permission, Is.EqualTo(HudSubmissionPermission.CanIntervene));
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.ComputeHashHex(), Is.EqualTo(hash));
            view.ResumePlanning(false); Assert.That(model.ConcurrentStatus, Does.Contain("待采纳"));
            Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(model.Facts.Permission, Is.EqualTo(HudSubmissionPermission.Intervened));
            Assert.That(model.ConcurrentStatus, Does.Contain("已接受")); Assert.That(view.Clock.State, Is.EqualTo(BattleClockState.TacticalPlanning));
            var guard = model.Decision.ActionSetOf(hero).First(a => model.Decision.FindAction(a).Type == ActionType.Guard);
            view.FocusTrack(hero.Value); view.SelectAction(guard); view.AddConfiguredDraft();
            Assert.That(model.Preview.Succeeded, Is.True, model.Preview.RejectionCode);
            Assert.That(model.ConfirmDraft(), Is.Null); view.ResumePlanning(false); Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(model.Drafts.State, Is.EqualTo(ScheduleDraftState.Accepted));
            Assert.That(model.Timeline.Any(p => p.OwnerUnitId == hero.Value && p.ActionSpecId == guard.Value), Is.True);
            yield return null;
        }
        [UnityTest] public IEnumerator InsufficientReactionShowsDisabledReasonsAndDoesNotAutoPause()
        {
            yield return ReactionFixture(false, false); var view = View;
            Assert.That(view.Model.Reactions, Is.Not.Empty);
            Assert.That(view.Model.HudPreviews.HasFeasibleReaction, Is.False);
            Assert.That(view.Clock.State, Is.EqualTo(BattleClockState.Running));
            view.Clock.PauseTactically(); view.ReactionView.Refresh(true);
            var options = view.GetComponentsInChildren<UnityEngine.UI.Button>(true).Where(b => b.name.StartsWith("Option-", StringComparison.Ordinal)).ToArray();
            Assert.That(options.Length, Is.GreaterThanOrEqualTo(2)); Assert.That(options.All(b => !b.interactable), Is.True);
            Assert.That(Texts(view), Does.Contain("肾上腺素不足"));
            Bootstrap.SetPaused(true); view.ReactionView.Refresh(true);
            Assert.That(view.GetComponentsInChildren<RectTransform>(true).Single(r => r.name == "M3ReactionCards").gameObject.activeSelf, Is.False);
            Assert.That(view.Model.CanPlan, Is.False);
            yield return null;
        }
        [UnityTest] public IEnumerator FundedHeavyAutoPausesDefaultsAtImpactAndEarlierSliderWarnsBeforeStagedAcceptance()
        {
            yield return ReactionFixture(true, true); var view = View; var model = view.Model;
            Assert.That(view.Clock.State, Is.EqualTo(BattleClockState.TacticalPlanning)); Assert.That(view.Clock.PauseReason, Does.Contain("公开威胁"));
            var opportunity = model.Reactions.Single(r => r.SourceImpactTick == 221);
            var block = model.Decision.ActionSetOf(model.Decision.ControlledUnitIds.Single()).Single(a => model.Decision.FindAction(a).Type == ActionType.Block);
            view.ReactionView.Select(opportunity.ReactionOpportunityId, block);
            Assert.That(view.ReactionView.Candidate.StartTick, Is.EqualTo(221));
            var root = view.GetComponentsInChildren<RectTransform>(true).Single(r => r.name == "Reaction-" + opportunity.ReactionOpportunityId);
            var slider = root.GetComponentInChildren<UnityEngine.UI.Slider>(); slider.value = 183;
            Assert.That(Texts(view), Does.Contain("先消耗")); Assert.That(Texts(view), Does.Contain("重击未被挡住"));
            Assert.That(root.Find("EarlierContactMarks").childCount, Is.GreaterThan(0));
            Assert.That(view.TimelineView.DisplayPlans.Any(p => p.ReactionOpportunityId == opportunity.ReactionOpportunityId && p.StartTick == 183), Is.True);
            yield return new WaitForEndOfFrame();
            var screenshot = ScreenCapture.CaptureScreenshotAsTexture();
            System.IO.File.WriteAllBytes($"优化任务/执行记录/M3-reaction-{Screen.width}x{Screen.height}.png", screenshot.EncodeToPNG()); UnityEngine.Object.Destroy(screenshot);
            string hash = Bootstrap.NewDriver.CurrentSnapshot.ComputeHashHex(); Click(root.Find("ConfirmReaction").GetComponent<UnityEngine.UI.Button>());
            Assert.That(model.StagedReactionCount, Is.EqualTo(1)); Assert.That(model.SubmittedCommands, Is.Zero);
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.ComputeHashHex(), Is.EqualTo(hash));
            view.ResumePlanning(false); Assert.That(model.ReactionStatus(opportunity.ReactionOpportunityId), Is.EqualTo("待采纳"));
            Bootstrap.DriveFrameForTests(0, 1f / 60); Assert.That(model.ReactionStatus(opportunity.ReactionOpportunityId), Is.EqualTo("权威已接受"));
            Drive(200); var actual = model.Timeline.Single(p => p.ReactionOpportunityId == opportunity.ReactionOpportunityId);
            Assert.That(actual.ReactionTriggeredAtTick, Is.EqualTo(199), actual.ToString() + "\n" + string.Join(";", model.Timeline));
            yield return null;
        }
        [UnityTest] public IEnumerator DodgePreviewsFutureMoveCancellationAndUsesSelectedBattlefieldDestination()
        {
            yield return ReactionFixture(true, false); var view = View; var model = view.Model; var hero = model.Decision.ControlledUnitIds.Single();
            var opportunity = model.Reactions.Single(r => r.SourceImpactTick == 221);
            var dodge = model.Decision.ActionSetOf(hero).Single(a => model.Decision.FindAction(a).Type == ActionType.Dodge);
            Assert.That(model.ActivateConcurrent(), Is.Null); view.ResumePlanning(false); Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(model.Facts.Permission, Is.EqualTo(HudSubmissionPermission.Intervened));
            var move = model.Decision.ActionSetOf(hero).Single(a => model.Decision.FindAction(a).Type == ActionType.Move);
            var owner = model.Decision.VisibleUnits.Single(u => u.UnitId == hero.Value);
            foreach (var delta in new[] { new GridPoint(2, 0), new GridPoint(-2, 0), new GridPoint(0, 2), new GridPoint(0, -2) })
                if (model.SetDraft(new ScheduleEditOperation[] {
                    new AddOrdinaryPlanOperation(1, hero, move, 222, Destination: new GridPoint(owner.X + delta.X, owner.Y + delta.Y)),
                    new AddOrdinaryPlanOperation(2, hero, move, 250, Destination: new GridPoint(owner.X + 2 * delta.X, owner.Y + 2 * delta.Y)) }).Succeeded) break;
            Assert.That(model.Preview.Succeeded, Is.True, model.Preview.RejectionCode); Assert.That(model.ConfirmDraft(), Is.Null);
            view.ResumePlanning(false); Bootstrap.DriveFrameForTests(0, 1f / 60); view.Clock.PauseTactically();
            var moves = model.Decision.OwnPlans.Where(p => p.ActionSpecId == move.Value).Select(p => p.ActionPlanId).ToArray(); Assert.That(moves.Length, Is.EqualTo(2));
            long submitted = model.SubmittedCommands;
            view.ReactionView.Select(opportunity.ReactionOpportunityId, dodge);
            Assert.That(view.IsAwaitingWorldSelection, Is.True);
            Assert.That(view.ReactionView.SelectDestination(new GridPoint(999,999)), Is.False);
            var legal = model.DodgeDestinations(opportunity.ReactionOpportunityId, dodge); Assert.That(legal, Is.Not.Empty);
            Assert.That(view.ReactionView.SelectDestination(legal[0]), Is.True);
            var card = view.GetComponentsInChildren<RectTransform>(true).Single(r => r.name == "Reaction-" + opportunity.ReactionOpportunityId);
            var cardCorners = new Vector3[4]; var viewportCorners = new Vector3[4];
            card.GetWorldCorners(cardCorners); ((RectTransform)card.parent.parent).GetWorldCorners(viewportCorners);
            Assert.That(cardCorners[1].y, Is.LessThanOrEqualTo(viewportCorners[1].y + 1), "Selected card title stays below the viewport top after world selection.");
            Assert.That(cardCorners[0].y, Is.GreaterThanOrEqualTo(viewportCorners[0].y - 1), "The complete selected card fits in the viewport.");
            var statusLabel = card.Find("Status").GetComponents<Component>().Single(t => t.GetType().FullName == "TMPro.TextMeshProUGUI");
            Assert.That((string)statusLabel.GetType().GetProperty("text").GetValue(statusLabel), Does.Contain("本地预览"),
                "Prior ordinary-plan receipts must never report an unsubmitted reaction as accepted.");
            Assert.That(view.ReactionView.Candidate.Destination, Is.EqualTo(legal[0])); Assert.That(model.SubmittedCommands, Is.EqualTo(submitted));
            Assert.That(Texts(view), Does.Contain("未来移动失效"));
            Assert.That(view.ReactionView.DefensePreview.Defenses.Single(p => p.Plan.ReactionOpportunityId == opportunity.ReactionOpportunityId).ConditionalMovePlanIds, Is.EquivalentTo(moves));
            Assert.That(model.Decision.OwnPlans.Count(p => moves.Contains(p.ActionPlanId)), Is.EqualTo(2));
            yield return new WaitForEndOfFrame();
            var screenshot = ScreenCapture.CaptureScreenshotAsTexture();
            System.IO.File.WriteAllBytes($"优化任务/执行记录/M3-dodge-{Screen.width}x{Screen.height}.png", screenshot.EncodeToPNG()); UnityEngine.Object.Destroy(screenshot);
            Assert.That(view.ReactionView.CancelSelection(), Is.True); Assert.That(model.StagedReactionCount, Is.Zero);
            view.ReactionView.Select(opportunity.ReactionOpportunityId, dodge); Assert.That(view.ReactionView.SelectDestination(legal[0]), Is.True);
            view.ReactionView.Confirm(opportunity.ReactionOpportunityId); Assert.That(model.StagedReactionCount, Is.EqualTo(1));
            view.ResumePlanning(false); Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(model.ReactionStatus(opportunity.ReactionOpportunityId), Is.EqualTo("权威已接受"));
            Drive(222);
            Assert.That(model.Timeline.Where(p => moves.Contains(p.ActionPlanId)).All(p => p.TerminationReason == (int)ActionTerminationReason.MovementOriginInvalidatedByDodge), Is.True);
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.Reservations.Any(r => moves.Contains(r.ActionPlanId)), Is.False);
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.MovementSegments.Any(r => moves.Contains(r.ActionPlanId)), Is.False);
            yield return null;
        }
    }
}
