using System.Collections;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class Task11ProductionSceneTests : RuntimeOwnershipTestBase
    {
        protected override bool UseLegacyComparisonScene => false;
        [UnityTest] public IEnumerator ProductionSceneUsesOnlyNewAndRejectsLegacyStartup()
        {
            yield return LoadMainScene();
            Assert.That(Bootstrap.BattleMode, Is.EqualTo(BattleRuntimeMode.New));
            Assert.That(Bootstrap.NewDriver.SimulationCreated, Is.True);
            Assert.That(Bootstrap.Adapters.Legacy, Is.Null);
            Assert.That(Bootstrap.LegacyWriters.Count, Is.Zero);
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var scripts = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MonoBehaviour>(true)).ToArray();
            Assert.That(scripts.All(s => s != null), Is.True, "No Missing Script in production hierarchy.");
            Assert.That(scripts.Any(s => s.GetType().Assembly.GetName().Name == "Assembly-CSharp"), Is.False,
                "All retired scene logic/UI belongs to the Editor-only diagnostic assembly and must be removed from production.");
            Bootstrap.StopBattle("production-mode-audit"); Bootstrap.ReleaseBattle();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Legacy), Is.False);
            Assert.That(Bootstrap.StartupRejection, Is.EqualTo("PRODUCTION_LEGACY_MODE_REMOVED"));
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.False);
            Assert.That(Bootstrap.StartupRejection, Is.EqualTo("PRODUCTION_LEGACY_MODE_REMOVED"));
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            Bootstrap.StopBattle("production-mode-complete"); Bootstrap.ReleaseBattle();
        }
    }
}
