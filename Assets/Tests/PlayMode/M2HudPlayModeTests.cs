using System.Collections;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Ids;
using ProjectHero.UnityView;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using System.Reflection;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class M2HudPlayModeTests : ProductionRuntimeTestBase
    {
        protected override bool UseLegacyComparisonScene => false;
        protected override bool UseHudAutoPause => true;
        static void Keys(params string[] keys) => typeof(M1HudPlayModeTests).GetMethod("KeyboardState", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] {keys});
        [TearDown] public void RemoveKeyboard() => new M1HudPlayModeTests().RemoveTestKeyboard();
        [UnityTest] public IEnumerator NativeCtrlClickTabShiftTabAndFocusedInputUseActualHudCallbacks()
        {
            yield return LoadMainScene(); var view = Object.FindFirstObjectByType<BattlePresentationView>(); var model = view.Model;
            var hero = model.Decision.ControlledUnitIds[0]; var enemy = model.Decision.VisibleUnits.First(u => u.UnitId != hero.Value);
            EventSystem.current.SetSelectedGameObject(null);
            Keys("LeftCtrl");
            var roster = view.GetComponentsInChildren<UnityEngine.UI.Button>().Single(b => b.name == "Unit-" + enemy.UnitId);
            ExecuteEvents.Execute(roster.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
            Keys(); yield return null;
            Assert.That(view.TrackSelection.Ordered(model.Decision).Count, Is.EqualTo(2));
            Assert.That(view.TrackSelection.Primary, Is.EqualTo(enemy.UnitId));
            Keys("Tab"); yield return null; Keys();
            Assert.That(view.TrackSelection.Primary, Is.EqualTo(hero.Value));
            Keys("LeftShift", "Tab"); yield return null; Keys();
            Assert.That(view.TrackSelection.Primary, Is.EqualTo(enemy.UnitId));
            view.FocusTrack(hero.Value);
            var input = typeof(M1HudView).GetProperty("StartInput").GetValue(view.GetComponent<M1HudView>());
            EventSystem.current.SetSelectedGameObject(((Component)input).gameObject); input.GetType().GetMethod("ActivateInputField").Invoke(input, null); yield return null; yield return null;
            Assert.That((bool)input.GetType().GetProperty("isFocused").GetValue(input), Is.True); Keys("Tab"); yield return null; Keys();
            Assert.That(view.TrackSelection.Primary, Is.EqualTo(hero.Value));
            input.GetType().GetMethod("DeactivateInputField").Invoke(input, new object[] {false}); EventSystem.current.SetSelectedGameObject(null);
            var attack = model.Decision.ActionSetOf(hero).First(a => model.Decision.FindAction(a).Type == ActionType.Attack);
            view.SelectAction(attack); Keys("Tab"); yield return null; Keys();
            Assert.That(view.TrackSelection.Primary, Is.EqualTo(hero.Value));
            Keys("Escape"); yield return null; Keys();
            Assert.That(view.IsAwaitingWorldSelection, Is.False); Assert.That(view.Clock.State, Is.EqualTo(BattleClockState.TacticalPlanning));
        }
        [UnityTest] public IEnumerator MultiSelectionFocusEmptySelectionAndTargetingKeepDraftsAndSnapshot()
        {
            yield return LoadMainScene(); var view = Object.FindFirstObjectByType<BattlePresentationView>(); var model = view.Model;
            var hero = model.Decision.ControlledUnitIds[0]; var enemy = model.Decision.VisibleUnits.First(u => u.UnitId != hero.Value);
            var guard = model.Decision.ActionSetOf(hero).First(a => model.Decision.FindAction(a).Type == ActionType.Guard);
            model.AppendDraft(new AddOrdinaryPlanOperation(1, hero, guard, 10));
            var hash = Bootstrap.NewDriver.CurrentSnapshot.ComputeHash();
            view.SelectTrack(enemy.UnitId, true);
            Assert.That(view.TrackSelection.Ordered(model.Decision).Select(u => u.UnitId), Is.EqualTo(new[] {hero.Value, enemy.UnitId}));
            Assert.That(view.SelectedActingUnit.Value, Is.EqualTo(enemy.UnitId)); Assert.That(view.CanConfigurePrimary, Is.False);
            view.CyclePrimary(false); Assert.That(view.SelectedActingUnit, Is.EqualTo(hero));
            view.TimelineView.Refresh(); Canvas.ForceUpdateCanvases();
            var rows = view.TimelineView.Content;
            Assert.That(rows.Find("UnitTrack-" + hero.Value).GetComponent<UnityEngine.UI.LayoutElement>().preferredHeight, Is.EqualTo(88));
            Assert.That(rows.Find("UnitTrack-" + enemy.UnitId).GetComponent<UnityEngine.UI.LayoutElement>().preferredHeight, Is.EqualTo(32));
            var attack = model.Decision.ActionSetOf(hero).First(a => model.Decision.FindAction(a).Type == ActionType.Attack);
            view.SelectAction(attack); view.CyclePrimary(false); view.SelectTrack(enemy.UnitId, false);
            Assert.That(view.TrackSelection.Primary, Is.EqualTo(hero.Value), "Target selection owns clicks and Tab.");
            view.CancelConfiguration(); view.RemoveTrack(hero.Value); Assert.That(view.TrackSelection.Primary, Is.EqualTo(enemy.UnitId));
            view.RemoveTrack(enemy.UnitId); Assert.That(view.TrackSelection.Primary, Is.Zero);
            Assert.That(rows.GetComponentsInChildren<UnityEngine.UI.LayoutElement>().Length, Is.Zero);
            Assert.That(model.Drafts.Entries.Count, Is.EqualTo(1)); Assert.That(Bootstrap.NewDriver.CurrentSnapshot.ComputeHash(), Is.EqualTo(hash));
            yield return null;
        }
        [UnityTest] public IEnumerator DragSnapsInCanvasCoordinatesCancelsWithoutLosingOtherDraftAndClearsOnSystemPause()
        {
            yield return LoadMainScene(); var view = Object.FindFirstObjectByType<BattlePresentationView>(); var model = view.Model;
            var hero = model.Decision.ControlledUnitIds[0];
            var guard = model.Decision.ActionSetOf(hero).First(a => model.Decision.FindAction(a).Type == ActionType.Guard);
            long first = model.AppendDraft(new AddOrdinaryPlanOperation(1, hero, guard, 10));
            long second = model.AppendDraft(new AddOrdinaryPlanOperation(2, hero, guard, 100));
            Assert.That(model.Preview.Succeeded, Is.True, model.Preview.RejectionCode);
            view.TimelineView.Refresh(); Canvas.ForceUpdateCanvases();
            var block = view.GetComponentsInChildren<TimelinePlanView>().Single(b => b.DraftId == first);
            var hash = Bootstrap.NewDriver.CurrentSnapshot.ComputeHash();
            foreach (float scale in new[] {.75f, 1.25f})
            {
                var chart = (RectTransform)block.transform.parent; var originalScale = chart.localScale; chart.localScale = Vector3.one * scale;
                var pointer = new PointerEventData(EventSystem.current) { position = RectTransformUtility.WorldToScreenPoint(null, chart.TransformPoint(new Vector3(100, -10, 0))) };
                block.OnBeginDrag(pointer);
                float dx = 29 * view.TimelinePixelsPerSecond / model.Decision.Definition.TicksPerSecond;
                pointer.position = RectTransformUtility.WorldToScreenPoint(null, chart.TransformPoint(new Vector3(100 + dx, -10, 0)));
                block.OnDrag(pointer);
                Assert.That(view.TimelineView.SnapVisible, Is.True); Assert.That(view.TimelineView.SnapTick, Is.EqualTo(100));
                Assert.That(model.PreviewPlanFor(first).StartTick, Is.EqualTo(40));
                Assert.That(model.PreviewPlanFor(second).StartTick, Is.EqualTo(100));
                block.CancelDrag(); Assert.That(view.TimelineView.SnapVisible, Is.False);
                Assert.That(model.PreviewPlanFor(first).StartTick, Is.EqualTo(10)); Assert.That(model.Drafts.Entries[1].Id, Is.EqualTo(second));
                chart.localScale = originalScale;
            }
            view.TimelineView.ShowSnap(100); Bootstrap.SetPaused(true);
            Assert.That(view.TimelineView.SnapVisible, Is.False);
            Assert.That(model.SubmittedCommands, Is.Zero); Assert.That(Bootstrap.NewDriver.CurrentSnapshot.ComputeHash(), Is.EqualTo(hash));
            yield return null;
        }
    }
}
