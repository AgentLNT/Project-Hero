using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Timeline;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.UnityView;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class M4HudPlayModeTests : ProductionRuntimeTestBase
    {
        protected override bool UseHudAutoPause => true;
        private object _keyboard;
        private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        private BattleSimulation Simulation => (BattleSimulation)Field(Bootstrap.NewDriver, "_simulation");
        private void Drive(long tick)
        {
            int frames = 0;
            while (Simulation.Tick < tick && frames++ < 2000) { Bootstrap.Clock.ResumeTactically(); Bootstrap.DriveFrameForTests(0, 1f / 60); }
            Assert.That(Simulation.Tick, Is.EqualTo(tick));
        }
        [TearDown] public void RemoveKeyboard()
        {
            if (_keyboard != null) Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true).GetMethod("RemoveDevice").Invoke(null, new[] {_keyboard});
            _keyboard = null;
        }
        private void Key(params string[] names)
        {
            var input = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true);
            var keyboard = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem", true);
            if (_keyboard == null) _keyboard = input.GetMethods().Single(m => m.Name == "AddDevice" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                .MakeGenericMethod(keyboard).Invoke(null, new object[] {"M4TestKeyboard"});
            var key = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem", true); var keys = Array.CreateInstance(key, names.Length);
            for (int i = 0; i < names.Length; i++) keys.SetValue(Enum.Parse(key, names[i]), i);
            var state = Type.GetType("UnityEngine.InputSystem.LowLevel.KeyboardState, Unity.InputSystem", true);
            input.GetMethods().Single(m => m.Name == "QueueStateEvent" && m.IsGenericMethodDefinition).MakeGenericMethod(state)
                .Invoke(null, new[] {_keyboard, Activator.CreateInstance(state, new object[] {keys}), (object)(-1d)});
        }
        [UnityTest] public IEnumerator CursorDragAndConfiguredAttackFollowDraftMovementWithoutSubmitting()
        {
            yield return LoadMainScene(); var view = UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>(); var model = view.Model;
            var hero = model.Decision.ControlledUnitIds[0]; var unit = model.Decision.VisibleUnits.Single(u => u.UnitId == hero.Value);
            var move = model.Decision.ActionSetOf(hero).First(a => model.Decision.FindAction(a).Type == ActionType.Move);
            var options = new[] {new GridPoint(unit.X - 2, unit.Y), new GridPoint(unit.X + 2, unit.Y), new GridPoint(unit.X - 1, unit.Y + 1)};
            var destination = options.First(p => model.PreviewCandidate(new AddOrdinaryPlanOperation(1, hero, move, 1, Destination: p)).Succeeded);
            long draft = model.AppendDraft(new AddOrdinaryPlanOperation(1, hero, move, 1, Destination: destination));
            Assert.That(model.Preview.Succeeded, Is.True, model.Preview.RejectionCode);
            var plan = model.PreviewPlanFor(draft); var hash = Bootstrap.NewDriver.CurrentSnapshot.ComputeHash();
            view.SpatialView.SetCursor(60);
            Assert.That(view.SpatialView.Preview.Units.Single(u => u.UnitId == hero.Value).Position, Is.EqualTo(destination));
            Assert.That(view.SpatialView.Following, Is.False); Assert.That(model.SubmittedCommands, Is.Zero);
            var attack = model.Decision.ActionSetOf(hero).First(a => model.Decision.FindAction(a).Type == ActionType.Attack);
            var target = model.Decision.VisibleUnits.First(u => u.UnitId != hero.Value && u.IsAlive);
            view.SelectAction(attack); Assert.That(view.SelectAttackCell(new GridPoint(target.X, target.Y)), Is.True);
            Assert.That(view.CandidatePreview.Succeeded, Is.True, view.CandidatePreview.RejectionCode);
            Assert.That(view.ConfiguredAttack.Origin, Is.EqualTo(destination));
            view.AddConfiguredDraft(); view.TimelineView.Refresh(); Canvas.ForceUpdateCanvases();
            var rail = view.GetComponentsInChildren<TimelineCursorInput>().Single(c => c.name == "TimeCursorRail"); var rect = (RectTransform)rail.transform;
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            var pointer = new PointerEventData(EventSystem.current) { position = Vector2.Lerp(corners[0], corners[2], .25f), button = PointerEventData.InputButton.Left };
            ExecuteEvents.Execute(rail.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            pointer.position = Vector2.Lerp(corners[0], corners[2], .3f); ExecuteEvents.Execute(rail.gameObject, pointer, ExecuteEvents.dragHandler);
            Assert.That(view.SpatialView.CursorTick, Is.InRange(100, 110));
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.ComputeHash(), Is.EqualTo(hash)); Assert.That(model.SubmittedCommands, Is.Zero);
            view.SpatialView.SetCursor(model.PreviewPlanFor(model.Drafts.Entries.Last().Id).StartTick);
            Assert.That(view.SpatialView.Preview.Attacks.Any(a => a.Origin == destination), Is.True);
            yield return new WaitForEndOfFrame();
            var screenshot = ScreenCapture.CaptureScreenshotAsTexture();
            System.IO.File.WriteAllBytes($"优化任务/执行记录/M4-space-{Screen.width}x{Screen.height}.png", screenshot.EncodeToPNG()); UnityEngine.Object.Destroy(screenshot);
            view.TimelineView.ReturnToNow(); Assert.That(view.SpatialView.Following, Is.True);
            Assert.That(model.Drafts.Entries.Count, Is.EqualTo(2));
        }
        [UnityTest] public IEnumerator VisibleCursorHandleAndLineReceiveRaycastsAndScrubWithoutCommands()
        {
            yield return LoadMainScene(); var view = UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>();
            var inputs = view.GetComponentsInChildren<TimelineCursorInput>();
            var rail = (RectTransform)inputs.Single(c => c.name == "TimeCursorRail").transform;
            var handle = inputs.Single(c => c.name == "CursorHandle");
            var line = inputs.Single(c => c.name == "CursorDragArea");
            var hash = Bootstrap.NewDriver.CurrentSnapshot.ComputeHash();
            var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
            var corners = new Vector3[4];
            foreach (var input in new[] {handle, line})
            {
                Canvas.ForceUpdateCanvases(); yield return new WaitForEndOfFrame();
                if (input == handle)
                {
                    var label = handle.GetComponentsInChildren<Component>().Single(c => c.GetType().FullName == "TMPro.TextMeshProUGUI");
                    Assert.That((bool)label.GetType().GetProperty("isTextTruncated").GetValue(label), Is.False, "The drag handle must display its arrow with the actual HUD font.");
                    var textInfo = label.GetType().GetProperty("textInfo").GetValue(label);
                    Assert.That((int)textInfo.GetType().GetField("characterCount").GetValue(textInfo), Is.GreaterThan(0));
                }
                ((RectTransform)input.transform).GetWorldCorners(corners);
                pointer.position = (corners[0] + corners[2]) * .5f;
                var hits = new System.Collections.Generic.List<RaycastResult>(); EventSystem.current.RaycastAll(pointer, hits);
                Assert.That(hits.First().gameObject.GetComponentInParent<TimelineCursorInput>(), Is.SameAs(input), "The visible cursor must receive the pointer instead of the track viewport.");
                ExecuteEvents.Execute(input.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                rail.GetWorldCorners(corners);
                pointer.position = Vector2.Lerp(corners[0], corners[2], input == handle ? .5f : .75f);
                ExecuteEvents.Execute(input.gameObject, pointer, ExecuteEvents.beginDragHandler);
                ExecuteEvents.Execute(input.gameObject, pointer, ExecuteEvents.dragHandler);
                Assert.That(view.SpatialView.CursorTick, Is.InRange(input == handle ? 179 : 269, input == handle ? 181 : 271));
            }
            Bootstrap.SetPaused(true); long cursor = view.SpatialView.CursorTick;
            pointer.position = Vector2.Lerp(corners[0], corners[2], .9f);
            ExecuteEvents.Execute(handle.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(line.gameObject, pointer, ExecuteEvents.dragHandler);
            Assert.That(view.SpatialView.CursorTick, Is.EqualTo(cursor));
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.ComputeHash(), Is.EqualTo(hash));
            Assert.That(view.Model.SubmittedCommands, Is.Zero);
        }
        [UnityTest] public IEnumerator KeyboardCursorFocusPauseAndAccessibilityUseNativeHudWithoutCommands()
        {
            yield return LoadMainScene(); var view = UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>(); var space = view.SpatialView;
            var hash = Bootstrap.NewDriver.CurrentSnapshot.ComputeHash(); EventSystem.current.SetSelectedGameObject(null);
            Key("RightArrow"); yield return null; Key(); yield return null;
            Assert.That(space.CursorTick, Is.EqualTo(view.Model.Decision.Tick + 6)); Assert.That(space.Following, Is.False);
            Key("Home"); yield return null; Key(); yield return null; Assert.That(space.Following, Is.True);
            var input = typeof(M1HudView).GetProperty("StartInput").GetValue(view.GetComponent<M1HudView>());
            EventSystem.current.SetSelectedGameObject(((Component)input).gameObject); input.GetType().GetMethod("ActivateInputField").Invoke(input, null); yield return null; yield return null;
            Assert.That((bool)input.GetType().GetProperty("isFocused").GetValue(input), Is.True);
            Key("RightArrow"); yield return null; Key(); yield return null; Assert.That(space.Following, Is.True);
            input.GetType().GetMethod("DeactivateInputField").Invoke(input, new object[] {false}); EventSystem.current.SetSelectedGameObject(null);
            space.SetTextScale(1.2f); space.SetHighContrast(true); space.Refresh();
            Assert.That(space.TextScale, Is.EqualTo(1.2f)); Assert.That(space.HighContrast, Is.True);
            Assert.That(((Component)input).GetComponent<UnityEngine.UI.Image>().color.maxColorComponent, Is.GreaterThan(.1f), "High contrast must keep the start input field visually distinguishable from its panel.");
            Key("F3"); yield return null; Key(); yield return null; Assert.That(space.DiagnosticsVisible, Is.True);
            Bootstrap.SetPaused(true); space.SetCursor(60); Assert.That(space.Following, Is.True);
            Key("RightArrow"); yield return null; Key(); yield return null; Assert.That(space.Following, Is.True);
            Bootstrap.SetPaused(false); Assert.That(view.Clock.State, Is.EqualTo(BattleClockState.TacticalPlanning));
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.ComputeHash(), Is.EqualTo(hash)); Assert.That(view.Model.SubmittedCommands, Is.Zero);
            yield return new WaitForEndOfFrame();
            var screenshot = ScreenCapture.CaptureScreenshotAsTexture();
            System.IO.File.WriteAllBytes($"优化任务/执行记录/M4-accessibility-{Screen.width}x{Screen.height}.png", screenshot.EncodeToPNG()); UnityEngine.Object.Destroy(screenshot);
        }
        [UnityTest] public IEnumerator OneSecondCursorShowsPublishedEnemyRangeAndSharedContactSelection()
        {
            yield return LoadMainScene(); Bootstrap.StopBattle("M4-public-range-fixture"); Bootstrap.ReleaseBattle();
            var original = ((IBattleSimulationSource)Field(Bootstrap, "_simulationSourceSlot")).BuildSeed();
            var seed = Task12BSceneEvidenceTests.Configure(original); var d = seed.Definition;
            var actions = d.Actions.Select(a => a.ActionSpecId.Value == "action.t12b.scene.heavy" ? a with { Timing = new AttackTimingSpec(240, 1) } : a).ToArray();
            string definitionHash = ProjectHero.Logic.Combat.BattleDefinitionHash.Compute(d.RulesVersion, d.TicksPerSecond, 0, d.Rules, d.ConcurrentAction,
                d.ReactionRules, d.AdrenalineRules, d.FactionModel, d.DamageChannels, d.ImpactProfiles, d.Units, actions, d.AttackPatterns,
                d.Volumes, d.MovementPatterns, d.ActionSets, d.StatusEffects, d.Encounters, d.DefaultDynamicSpawnPolicy);
            d = d with { Actions = actions, BattleDefinitionHashValue = definitionHash };
            var source = NewGameObject("M4ExplicitRangeSource").AddComponent<Task11SceneScenarioSource>();
            source.Seed = new BattleSimulationSeed(d, seed.EncounterId, seed.RuntimeInputs, "M4 explicit long public telegraph fixture");
            Bootstrap.GetType().GetField("_simulationSourceSlot", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Bootstrap, source);
            var view = UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>();
            var third = NewGameObject("M4ThirdAttacker").AddComponent<CombatUnitView>(); third.Configure("t12b_third", UnityEngine.Object.FindFirstObjectByType<GridView>());
            view.Registry.Configure(((CombatUnitView[])Field(view.Registry, "_views")).Concat(new[] {third}).ToArray());
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            Drive(180); var enemy = Simulation.CurrentTurnWindow.OwnerUnitId; var hero = view.Model.Decision.ControlledUnitIds.Single();
            var controller = d.FindEncounter(seed.EncounterId).Controllers.Single(c => c.ControllerId.Value != view.PlayerControllerId).ControllerId;
            Assert.That(Simulation.CommandIngress.FindEntry(controller).Submit(new CommandRequest(181,
                new ScheduleEditScope(Simulation.ScheduleRevision, Simulation.CurrentTurnWindow.WindowId), new ScheduleEditPayload(new[] {
                    new AddOrdinaryPlanOperation(1, enemy, new ActionSpecId("action.t12b.scene.heavy"), 181, PrimaryTargetUnitId: hero) }))), Is.Null);
            Drive(181); Bootstrap.Clock.PauseTactically("M4 公开范围验收"); view.TimelineView.Refresh(); view.SpatialView.SetCursor(241);
            var hash = Simulation.CurrentSnapshot.ComputeHash();
            Assert.That(view.SpatialView.Preview.Attacks.Any(a => a.OwnerUnitId == enemy.Value), Is.True,
                $"enemy={enemy.Value}; cursor={view.SpatialView.CursorTick}; horizon={view.Model.HudPreviews.KnownHorizonTick}; clock={view.Clock.State}; rejection={view.SpatialView.Preview.RejectionCode}; "
                + "plans=" + string.Join(";", Simulation.CurrentSnapshot.Plans) + "; units=" + string.Join(";", view.SpatialView.Preview.Units));
            Assert.That(view.SpatialView.Preview.Units.Single(u => u.UnitId == enemy.Value).IsUnknown, Is.False);
            Assert.That(view.SpatialView.CursorTick - Simulation.Tick, Is.EqualTo(60));
            yield return new WaitForEndOfFrame(); var screenshot = ScreenCapture.CaptureScreenshotAsTexture();
            System.IO.File.WriteAllBytes($"优化任务/执行记录/M4-public-range-{Screen.width}x{Screen.height}.png", screenshot.EncodeToPNG()); UnityEngine.Object.Destroy(screenshot);
            var contact = view.TimelineView.Contacts.First(c => c.OwnerUnitId == enemy.Value && c.TargetUnitId == hero.Value);
            view.SpatialView.SelectContact(contact);
            Assert.That(view.SpatialView.SelectedContact, Is.EqualTo(contact)); Assert.That(view.SpatialView.CursorTick, Is.EqualTo(contact.Tick));
            Assert.That(view.TrackSelection.Contains(enemy.Value) && view.TrackSelection.Contains(hero.Value), Is.True);
            Assert.That(Simulation.CurrentSnapshot.ComputeHash(), Is.EqualTo(hash)); Assert.That(view.Model.SubmittedCommands, Is.Zero);
            view.SpatialView.ReturnToNow(); yield return null;
            var placementSlider = view.GetComponentsInChildren<UnityEngine.UI.Slider>().First();
            EventSystem.current.SetSelectedGameObject(placementSlider.gameObject);
            Key("RightArrow"); yield return null; Key(); yield return null;
            Assert.That(view.SpatialView.Following, Is.True, "Reaction placement slider owns arrow navigation while focused.");
        }
    }
}
