using System.Collections;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Definitions;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class Task11ProductionSceneTests : ProductionRuntimeTestBase
    {
        protected override bool UseLegacyComparisonScene => false;
        [UnityTest] public IEnumerator ProductionSceneUsesOnlyNewAndRejectsLegacyStartup()
        {
            yield return LoadMainScene();
            Assert.That(Bootstrap.BattleMode, Is.EqualTo(BattleRuntimeMode.New));
            Assert.That(Bootstrap.NewDriver.SimulationCreated, Is.True);
            Assert.That(Bootstrap.NewDriver.ReplayHeader.RulesVersion, Is.EqualTo("battle-def-v11-calibrated-tempo-utility-ai"));
            Assert.That(UnityEngine.Object.FindObjectsByType<MigrationBattleHost>(FindObjectsInactive.Include), Is.Empty);

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var scripts = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MonoBehaviour>(true)).ToArray();
            var definition = scripts.OfType<IBattleSimulationSource>().Single().BuildSeed().Definition;
            Assert.That(definition.Rules.UseExplicitControlEffects, Is.True);
            Assert.That(definition.AdrenalineRules.UseHealthThresholds, Is.True);
            Assert.That(definition.Units.All(unit => unit.InitialHealth == 50 && unit.ActionSpeed == 20 && unit.MoveSpeed == 30), Is.True);
            var enemy = definition.Encounters.Single().Controllers.Single(controller => controller.SourceKind == CommandSourceKind.Ai);
            Assert.That(enemy.AiExecution, Is.EqualTo(AiExecutionKind.UtilityBehaviorTree));
            Assert.That(enemy.AiForecast.RequireSubmissionPreflight, Is.True);
            var prior = definition.PriorReplayDefinition;
            Assert.That(prior.RulesVersion, Is.EqualTo("battle-def-v11-calibrated-tempo"));
            Assert.That(prior.Encounters.Single().Controllers.Single(controller => controller.SourceKind == CommandSourceKind.Ai).AiPolicy,
                Is.EqualTo(AiPolicyKind.Legacy));
            Assert.That(prior.PriorReplayDefinition.RulesVersion, Is.EqualTo("battle-def-v10-explicit-control"));
            Assert.That(prior.PriorReplayDefinition.Units.All(unit => unit.InitialHealth == 200), Is.True);
            Assert.That(prior.PriorReplayDefinition.PriorReplayDefinition.RulesVersion, Is.EqualTo("battle-def-v9-threshold-adrenaline"));
            Assert.That(prior.PriorReplayDefinition.PriorReplayDefinition.PriorReplayDefinition.RulesVersion, Is.EqualTo("battle-def-v8-wall-impact"));
            foreach (var action in definition.Actions.Where(action => action.Type == ActionType.Attack))
            {
                var id = action.ActionSpecId.Value;
                var expected = id.Contains(".whirlwind.") ? AttackControlEffects.Displacement
                    : id.Contains(".wide_cleave.") ? AttackControlEffects.Interrupt
                    : id.Contains(".heavy_smash.") ? AttackControlEffects.All : AttackControlEffects.None;
                Assert.That(((AttackPayloadSpec)action.Payload).ControlEffects, Is.EqualTo(expected), id);
            }
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
