using System.Collections;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class Task10MigrationTests : RuntimeOwnershipTestBase
    {
        [UnityTest]
        public IEnumerator NewModeBlocksLegacyUiWritesEvenWhenPortsAreMissing()
        {
            yield return LoadHiddenValidationScene();
            ResolveBootstrap();
            Bootstrap.StopBattle("task10-gate-prepare"); Bootstrap.ReleaseBattle();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            Assert.That(BattleRuntimeBootstrap.LegacyWritesAllowed, Is.False);
            var tacticsType = ResolveProductionType("ProjectHero.Core.Gameplay.TacticsController");
            Assert.That(tacticsType, Is.Not.Null);
            var go = NewGameObject("UnboundNewTactics");
            var tactics = go.AddComponent(tacticsType);
            var units = FindLegacyUnits();
            var timeline = FindProductionComponent("ProjectHero.Core.Timeline.BattleTimeline");
            tacticsType.GetField("_selectedUnit", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(tactics, units.Hero);
            tacticsType.GetField("Timeline", BindingFlags.Public | BindingFlags.Instance).SetValue(tactics, timeline);
            var scheduledCount = timeline.GetType().GetProperty("ScheduledEventCount");
            Assert.That(scheduledCount, Is.Not.Null);
            int pendingBefore = (int)scheduledCount.GetValue(timeline);
            Assert.That((bool)tacticsType.GetProperty("LegacyImmediateWritesEnabled").GetValue(tactics), Is.False);
            long previous = LegacyTimelineCurrentTick();
            foreach (var method in new[] { "ExecuteBlock", "ExecuteDodge", "ExecuteRecover" })
                tacticsType.GetMethod(method, BindingFlags.Instance | BindingFlags.Public).Invoke(tactics, null);
            Assert.That(LegacyTimelineCurrentTick(), Is.EqualTo(previous));
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.Zero);
            Assert.That((int)scheduledCount.GetValue(timeline), Is.EqualTo(pendingBefore),
                "Unbound New UI must not create old Timeline events even for a selected real unit.");
        }
    }
}
