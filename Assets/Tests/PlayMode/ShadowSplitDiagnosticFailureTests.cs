using System.Collections;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class InvalidMigrationSource : MonoBehaviour, IBattleSimulationSource
    {
        public string SourceName => "negative-control";
        public string LastConfigurationError => "NEGATIVE_CONTROL_INVALID_SOURCE";
        public BattleSimulationSeed BuildSeed() => throw new System.InvalidOperationException("Invalid sources must not be built.");
    }

    public sealed class ShadowSplitDiagnosticFailureTests : RuntimeOwnershipTestBase
    {
        [UnityTest]
        public IEnumerator FailedNewDiagnosticRestoresLegacyVisualsAndCanRetry()
        {
            yield return LoadHiddenValidationScene();
            var canvas = NewGameObject("LegacyCanvasNegativeControl");
            Bootstrap.ConfigureLegacyPresentation(new[] {canvas}, System.Array.Empty<Behaviour>());
            var field = typeof(MigrationBattleHost).GetField("_simulationSourceSlot", BindingFlags.Instance | BindingFlags.NonPublic);
            var original = field.GetValue(Bootstrap);
            var invalid = NewGameObject("InvalidMigrationSource").AddComponent<InvalidMigrationSource>();
            field.SetValue(Bootstrap, invalid);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.False);
            Assert.That(Bootstrap.StartupRejection, Does.Contain("NEGATIVE_CONTROL_INVALID_SOURCE"));
            Assert.That(Bootstrap.NewDriver.SimulationCreated, Is.False);
            Assert.That(canvas.activeSelf, Is.True, "Failed diagnostics must restore the old presentation.");
            field.SetValue(Bootstrap, original);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            Assert.That(Bootstrap.NewDriver.SimulationCreated, Is.True);
            Assert.That(canvas.activeSelf, Is.False);
            Bootstrap.StopBattle("diagnostic-retry-complete"); Bootstrap.ReleaseBattle();
            Assert.That(canvas.activeSelf, Is.True);
        }
    }
}
