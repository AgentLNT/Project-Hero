using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.UnityView;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class M1HudPlayModeTests : ProductionRuntimeTestBase
    {
        private static object _addedKeyboard;
        [TearDown] public void RemoveTestKeyboard()
        {
            if (_addedKeyboard == null) return;
            Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true).GetMethod("RemoveDevice")
                .Invoke(null, new[] {_addedKeyboard}); _addedKeyboard = null;
        }
        protected override bool UseLegacyComparisonScene => false;
        protected override bool UseHudAutoPause => true;
        static object Field(object o, string name) => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);
        static void Render(BattlePresentationView view) => view.GetType().GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, null);
        void Drive(long tick)
        {
            int frames = 0;
            while (Bootstrap.NewDriver.CurrentSnapshot.Tick < tick && frames++ < 1000)
            {
                Bootstrap.DriveFrameForTests(0, 1f / 60);
                if (Bootstrap.Clock.State == BattleClockState.TacticalPlanning) Bootstrap.NewDriver.ResumeTactically();
            }
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.Tick, Is.EqualTo(tick));
        }
        static void KeyboardState(params string[] names)
        {
            var inputType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", true);
            var keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem", true);
            var keyboard = keyboardType.GetProperty("current").GetValue(null);
            if (keyboard == null)
            {
                keyboard = inputType.GetMethods().Single(m => m.Name == "AddDevice" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                    .MakeGenericMethod(keyboardType).Invoke(null, new object[] {"M1TestKeyboard"});
                _addedKeyboard = keyboard;
            }
            // Native state monitors exercise InputAction callbacks without depending on the batch window's RawInput focus.
            var stateType = Type.GetType("UnityEngine.InputSystem.LowLevel.KeyboardState, Unity.InputSystem", true);
            var keyType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem", true);
            var keys = Array.CreateInstance(keyType, names.Length);
            for (int i = 0; i < names.Length; i++) keys.SetValue(Enum.Parse(keyType, names[i]), i);
            var state = Activator.CreateInstance(stateType, new object[] {keys});
            var inputStateType = Type.GetType("UnityEngine.InputSystem.LowLevel.InputState, Unity.InputSystem", true);
            var change = inputStateType.GetMethods().Single(m => m.Name == "Change" && m.IsGenericMethodDefinition
                && !m.GetParameters()[1].ParameterType.IsByRef).MakeGenericMethod(stateType);
            var updateType = Type.GetType("UnityEngine.InputSystem.LowLevel.InputUpdateType, Unity.InputSystem", true);
            var eventType = Type.GetType("UnityEngine.InputSystem.LowLevel.InputEventPtr, Unity.InputSystem", true);
            change.Invoke(null, new[] {keyboard, state, Enum.Parse(updateType, "Dynamic"), Activator.CreateInstance(eventType)});
        }
        [UnityTest] public IEnumerator NativeKeyboardEventsRespectInputFocusAndSystemPauseRestoresPreviousClock()
        {
            yield return LoadMainScene(); var view = UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>();
            Render(view);
            var field = view.GetComponent<M1HudView>().GetType().GetProperty("StartInput").GetValue(view.GetComponent<M1HudView>());
            var eventSystemType = Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI", true);
            var eventSystem = eventSystemType.GetProperty("current").GetValue(null);
            eventSystemType.GetMethod("SetSelectedGameObject", new[] {typeof(GameObject)}).Invoke(eventSystem, new object[] {((Component)field).gameObject});
            field.GetType().GetMethod("ActivateInputField").Invoke(field, null); yield return null; yield return null;
            Assert.That((bool)field.GetType().GetProperty("isFocused").GetValue(field), Is.True,
                "field active=" + ((Component)field).gameObject.activeInHierarchy + "; clock=" + view.Clock.State);
            KeyboardState("Space"); yield return null;
            Assert.That(view.Clock.State, Is.EqualTo(BattleClockState.TacticalPlanning), "Space belongs to the focused input.");
            KeyboardState(); yield return null;
            field.GetType().GetMethod("DeactivateInputField").Invoke(field, new object[] {false}); yield return null;
            eventSystemType.GetMethod("SetSelectedGameObject", new[] {typeof(GameObject)}).Invoke(eventSystem, new object[] {null});
            KeyboardState("Escape"); yield return null;
            var shortcut = Field(view, "_systemShortcut");
            var keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem", true);
            var keyboard = keyboardType.GetProperty("current").GetValue(null);
            var escape = keyboardType.GetProperty("escapeKey").GetValue(keyboard);
            Assert.That(view.Clock.State, Is.EqualTo(BattleClockState.SystemPaused),
                "focused=" + field.GetType().GetProperty("isFocused").GetValue(field)
                + "; escape=" + escape.GetType().GetProperty("isPressed").GetValue(escape)
                + "; enabled=" + shortcut.GetType().GetProperty("enabled").GetValue(shortcut)
                + "; phase=" + shortcut.GetType().GetProperty("phase").GetValue(shortcut));
            Assert.That(view.Model.CanPlan, Is.False);
            KeyboardState(); yield return null; KeyboardState("Escape"); yield return null;
            Assert.That(view.Clock.State, Is.EqualTo(BattleClockState.TacticalPlanning));
            KeyboardState(); yield return null; KeyboardState("Space"); yield return null;
            Assert.That(view.Clock.State, Is.EqualTo(BattleClockState.Running)); KeyboardState(); yield return null;
        }
        [UnityTest] public IEnumerator MainScenePausesPlansMultipleActionsAndExecutesHeavyAcrossWindow()
        {
            yield return LoadMainScene(); var view = UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>();
            Assert.That(Bootstrap.Clock.State, Is.EqualTo(BattleClockState.TacticalPlanning));
            long tick = view.Model.Decision.Tick; var hash = Bootstrap.NewDriver.CurrentSnapshot.ComputeHash();
            Bootstrap.DriveFrameForTests(0, 10);
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.ComputeHash(), Is.EqualTo(hash));
            var model = view.Model; var hero = model.Decision.ControlledUnitIds.Single();
            var enemy = new UnitId(model.Decision.VisibleUnits.First(u => u.UnitId != hero.Value).UnitId);
            var actions = model.Decision.ActionSetOf(hero).Select(model.Decision.FindAction).ToArray();
            var move = actions.First(a => a.Type == ActionType.Move);
            var light = actions.Where(a => a.Type == ActionType.Attack).OrderBy(a => ((AttackTimingSpec)a.Timing).BaseWindupTicks).First();
            var heavy = actions.Where(a => a.Type == ActionType.Attack).OrderByDescending(a => ((AttackTimingSpec)a.Timing).BaseWindupTicks).First();
            var unit = model.Decision.VisibleUnits.Single(u => u.UnitId == hero.Value);
            long moveId = 0;
            foreach (var cell in new[] {new GridPoint(unit.X + 2, unit.Y), new GridPoint(unit.X - 2, unit.Y), new GridPoint(unit.X + 1, unit.Y + 1)})
            {
                moveId = model.AppendDraft(new AddOrdinaryPlanOperation(1, hero, move.ActionSpecId, Destination: cell));
                if (model.Preview.Succeeded) break; model.RemoveDraft(moveId);
            }
            Assert.That(model.Preview.Succeeded, Is.True, model.Preview.RejectionCode);
            model.AppendDraft(new AddOrdinaryPlanOperation(1, hero, light.ActionSpecId, PrimaryTargetUnitId: enemy));
            model.AppendDraft(new AddOrdinaryPlanOperation(1, hero, heavy.ActionSpecId, model.Window.EndTick - 1, PrimaryTargetUnitId: enemy));
            Assert.That(model.Preview.Succeeded, Is.True, model.Preview.RejectionCode);
            Assert.That(model.Drafts.Entries.Count, Is.EqualTo(3));
            Render(view); Assert.That(view.GetComponent<M1HudView>().DraftStatusText, Does.Contain("3 条"));
            Assert.That(model.ConfirmDraft(), Is.Null); Assert.That(model.SubmittedCommands, Is.Zero);
            Assert.That(model.Drafts.State, Is.EqualTo(ScheduleDraftState.AwaitingResume));
            view.ResumePlanning(false); Assert.That(model.Drafts.State, Is.EqualTo(ScheduleDraftState.AwaitingAcceptance));
            Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(model.Drafts.State, Is.EqualTo(ScheduleDraftState.Accepted)); Assert.That(model.SubmittedCommands, Is.EqualTo(1));
            var heavyPlan = model.Timeline.Single(p => p.ActionSpecId == heavy.ActionSpecId.Value);
            Assert.That(heavyPlan.StartTick, Is.EqualTo(179)); Assert.That(heavyPlan.EndTick, Is.GreaterThan(180));
            Drive(180); Assert.That(model.Window.OwnerUnitId, Is.Not.EqualTo(hero.Value));
            Assert.That(model.Timeline.Single(p => p.ActionPlanId == heavyPlan.ActionPlanId).State, Is.EqualTo((int)ActionPlanState.Running));
            Drive(heavyPlan.EndTick); Assert.That(model.Timeline.Single(p => p.ActionPlanId == heavyPlan.ActionPlanId).State, Is.EqualTo((int)ActionPlanState.Completed));
            yield return null;
        }
        [UnityTest] public IEnumerator BattlefieldAttackSelectionUsesPointerFacingWithoutWritingSnapshot()
        {
            yield return LoadMainScene(); var view = UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>();
            var model = view.Model; var hero = model.Decision.ControlledUnitIds.Single();
            var action = model.Decision.ActionSetOf(hero).First(id => model.Decision.FindAction(id).Type == ActionType.Attack && model.Targets(hero, id).Count > 0);
            var target = model.Targets(hero, action)[0];
            var owner = model.Decision.VisibleUnits.Single(u => u.UnitId == hero.Value);
            var hash = Bootstrap.NewDriver.CurrentSnapshot.ComputeHash();
            view.SelectAction(action);
            Assert.That((IList)Field(view, "_targetButtons"), Is.Empty);
            Assert.That(((Component)Field(view, "_rotate")).gameObject.activeSelf, Is.False);
            Assert.That(view.SelectAttackCell(new GridPoint(target.X, target.Y)), Is.True);
            var candidate = (AddOrdinaryPlanOperation)Field(view, "_candidate");
            Assert.That(candidate.Facing, Is.EqualTo(GridView.DirectionTo(new GridPoint(owner.X, owner.Y), new GridPoint(target.X, target.Y))));
            Assert.That(candidate.PrimaryTargetUnitId, Is.EqualTo(new UnitId(target.UnitId)));
            Assert.That(candidate.Destination, Is.Null);
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.ComputeHash(), Is.EqualTo(hash));
            Assert.That(model.SubmittedCommands, Is.Zero);
            Assert.That(model.Drafts.Entries, Is.Empty, "Configuration still requires the explicit Add to drafts action.");
            Bootstrap.SetPaused(true);
            Assert.That(view.SelectAttackCell(new GridPoint(target.X, target.Y)), Is.False);
            yield return null;
        }
        [UnityTest] public IEnumerator MainSceneResumeChoiceKeepsIntentAndSystemPauseBlocksPlanning()
        {
            yield return LoadMainScene(); var view = UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>(); var model = view.Model;
            var hero = model.Decision.ControlledUnitIds.Single(); var guard = model.Decision.ActionSetOf(hero).First(a => model.Decision.FindAction(a).Type == ActionType.Guard);
            model.AppendDraft(new AddOrdinaryPlanOperation(1, hero, guard, 20)); view.ToggleTacticalPause();
            Assert.That(view.GetComponent<M1HudView>().ResumeChoiceVisible, Is.True); Assert.That(Bootstrap.IsPaused, Is.True);
            view.ResumePlanning(false); Assert.That(model.SubmittedCommands, Is.Zero); Assert.That(model.Drafts.Entries.Count, Is.EqualTo(1));
            Bootstrap.SetPaused(true); Assert.That(model.CanPlan, Is.False);
            Assert.That(model.AppendDraft(new AddOrdinaryPlanOperation(1, hero, guard, 90)), Is.Zero);
            Bootstrap.DriveFrameForTests(0, 10); Assert.That(model.Drafts.Entries.Count, Is.EqualTo(1));
            Bootstrap.SetPaused(false); view.ToggleTacticalPause(); Assert.That(model.CanPlan, Is.True);
            Bootstrap.SetPaused(true); Bootstrap.StopBattle("M1-release"); Bootstrap.ReleaseBattle();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            Assert.That(view.Model.Drafts.Entries, Is.Empty); Assert.That(view.Model.SubmittedCommands, Is.Zero);
            Assert.That(Bootstrap.Clock.State, Is.Not.EqualTo(BattleClockState.SystemPaused));
            yield return null;
        }
    }
}
