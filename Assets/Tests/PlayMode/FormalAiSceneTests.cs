using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Replay;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class FormalAiSceneTests : ProductionRuntimeTestBase
    {
        protected override bool UseLegacyComparisonScene => false;
        [UnityTest]
        public IEnumerator FormalEnemyHybridUsesProductionClockAndReplaysFromSceneContent()
        {
            yield return LoadMainScene();
            Bootstrap.StopBattle("hybrid-test-prepare"); Bootstrap.ReleaseBattle();
            var field = Bootstrap.GetType().GetField("_simulationSourceSlot", BindingFlags.Instance | BindingFlags.NonPublic);
            var original = ((IBattleSimulationSource)field.GetValue(Bootstrap)).BuildSeed();
            var d = original.Definition;
            var encounters = d.Encounters.Select(e => e with { Controllers = e.Controllers.Select(c =>
                c.SourceKind == CommandSourceKind.Ai ? c with { AiPolicy = AiPolicyKind.SustainedOffense,
                    AiExecution = AiExecutionKind.UtilityBehaviorTree, AiForecast = new AiForecastSettings(360) } : c).ToArray() }).ToArray();
            // Explicit scene-test envelope; every action, unit, shape and rule comes from the real scene source.
            var hash = BattleDefinitionHash.Compute(d.RulesVersion, d.TicksPerSecond, 0, d.Rules, d.ConcurrentAction,
                d.ReactionRules, d.AdrenalineRules, d.FactionModel, d.DamageChannels, d.ImpactProfiles, d.Units, d.Actions,
                d.AttackPatterns, d.Volumes, d.MovementPatterns, d.ActionSets, d.StatusEffects, encounters, d.DefaultDynamicSpawnPolicy);
            var definition = d with { Encounters = encounters, BattleDefinitionHashValue = hash };
            var source = NewGameObject("FormalHybridTestSource").AddComponent<Task11SceneScenarioSource>();
            source.Seed = new BattleSimulationSeed(definition, original.EncounterId, original.RuntimeInputs, "formal hybrid scene test");
            field.SetValue(Bootstrap, source);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            bool executed = false;
            for (int tick = 0; tick < 360; tick++)
            {
                Bootstrap.Clock.ResumeTactically(); Bootstrap.DriveFrameForTests(0, 1f / 60);
                executed |= Bootstrap.NewDriver.CurrentSnapshot.AiControllers.Any(c => c.Behavior?.Ordinary.PlanId > 0);
            }
            Assert.That(executed, Is.True);
            Assert.That(BattleRuntimeBootstrap.EnumerateLiveInstances().Count(b => b.OwnsTopLevelClock), Is.EqualTo(1));
            var replay = Bootstrap.NewDriver.RecordedReplay; Assert.That(replay.Submissions, Is.Empty);
            using var player = new ReplayPlayer(definition); player.Load(replay); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            Assert.That(player.Deviation, Is.Null);
            Bootstrap.StopBattle("hybrid-test-complete"); Bootstrap.ReleaseBattle();
        }
    }
}
