using System.Collections;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class ShadowSplitLifecycleTests : ProductionRuntimeTestBase
    {
        protected override bool UseHudAutoPause => true;

        [UnityTest]
        public IEnumerator NewRunsWithoutObserverAndPreservesPauseReleaseAndRestart()
        {
            yield return LoadMainScene();
            Assert.That(Object.FindObjectsByType<MigrationBattleHost>(FindObjectsInactive.Include), Is.Empty);
            var context = (BattleRuntimeContext)typeof(UnityBattleDriver).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Bootstrap.NewDriver);
            Assert.That(context.Observer, Is.Null, "Production must not instantiate a migration ledger or authority recorder.");
            Bootstrap.SetPaused(true);
            long tick = Bootstrap.NewDriver.CurrentSnapshot.Tick;
            Bootstrap.DriveFrameForTests(10, 10);
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.Tick, Is.EqualTo(tick));
            Bootstrap.SetPaused(false);
            Bootstrap.NewDriver.ResumeTactically();
            Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.Tick, Is.GreaterThan(tick));
            Assert.That(Bootstrap.StopBattle("isolation-stop"), Is.True);
            Assert.That(Bootstrap.StopBattle("duplicate"), Is.False);
            Assert.That(Bootstrap.NewDriver.StopCallCount, Is.EqualTo(1));
            Assert.That(Bootstrap.ReleaseBattle(), Is.True);
            Assert.That(Bootstrap.ReleaseBattle(), Is.False);
            Assert.That(Bootstrap.NewDriver.SimulationCreated, Is.False);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.False);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            Assert.That(Bootstrap.NewDriver.TicksAdvanced, Is.Zero);
        }

        [UnityTest]
        public IEnumerator InactiveDiagnosticHostCannotCreateASecondProductionWorld()
        {
            yield return LoadMainScene();
            Bootstrap.StopBattle("host-collision-prepare"); Bootstrap.ReleaseBattle();
            var go = NewGameObject("InactiveMigrationHost"); go.SetActive(false);
            var migration = go.AddComponent<MigrationBattleHost>();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.False);
            Assert.That(Bootstrap.StartupRejection, Does.StartWith(BattleRuntimeBootstrap.BOOTSTRAP_DUPLICATE));
            Assert.That(Bootstrap.NewDriver.SimulationCreated, Is.False);
            Object.Destroy(go); yield return null;
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
        }
    }
}
