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
        public IEnumerator DefaultEnemyApproachesThroughProductionClockWithoutReplacingSceneSource()
        {
            yield return LoadMainScene();
            var driver = Bootstrap.NewDriver;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var source = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<UnityEngine.MonoBehaviour>(true))
                .OfType<IBattleSimulationSource>().Single().BuildSeed();
            var encounter = source.Definition.FindEncounter(source.EncounterId);
            var enemy = encounter.Controllers.Single(binding => binding.SourceKind == CommandSourceKind.Ai);
            Assert.That(enemy.AiExecution, Is.EqualTo(AiExecutionKind.UtilityBehaviorTree));
            Assert.That(enemy.AiForecast.RequireSubmissionPreflight, Is.True);
            var initial = driver.CurrentSnapshot.Units.ToDictionary(unit => unit.UnitId);
            for (int tick = 0; tick < 420; tick++)
            {
                Bootstrap.Clock.ResumeTactically();
                Bootstrap.DriveFrameForTests(0, 1f / 60);
            }

            Assert.That(driver.CurrentSnapshot.AiControllers.Single().DecisionCount, Is.GreaterThan(0));
            Assert.That(driver.CurrentSnapshot.Units.Any(unit => unit.X != initial[unit.UnitId].X || unit.Y != initial[unit.UnitId].Y), Is.True);
            var replay = driver.RecordedReplay;
            Assert.That(replay.Submissions, Is.Empty);
            Assert.That(replay.Records.SelectMany(record => record.Events).Select(ReplayEventComparison.Canonical)
                .Any(fact => fact.Contains("CommandRejectedEvent{") || fact.Contains("CommandIngressRejectedEvent{")), Is.False);
            using var player = new ReplayPlayer(source.Definition);
            player.Load(replay);
            player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            Assert.That(player.Deviation, Is.Null);
            Assert.That(player.IsComplete, Is.True);
        }

        [UnityTest]
        public IEnumerator FormalEnemyHybridUsesProductionClockAndReplaysFromSceneContent()
            => RunScene(false);

        [UnityTest]
        public IEnumerator FormalEnemyDefenseAwareHybridUsesProductionClockAndReplaysFromSceneContent()
            => RunScene(true);

        [UnityTest]
        public IEnumerator CautiousEnemyApproachesThroughProductionClockAndReplaysFromSceneContent()
            => RunScene(true, true);

        [UnityTest]
        public IEnumerator MixedFormalPoliciesShareProductionClockAndReplayBothControllers()
            => RunScene(true, true, true);

        [UnityTest]
        public IEnumerator ShortPlansUseProductionClockAndReplayBothControllers()
            => RunScene(true, true, true, true);

        IEnumerator RunScene(bool defenses, bool approach = false, bool mixed = false, bool shortPlans = false)
        {
            yield return LoadMainScene();
            Bootstrap.StopBattle("hybrid-test-prepare"); Bootstrap.ReleaseBattle();
            var field = Bootstrap.GetType().GetField("_simulationSourceSlot", BindingFlags.Instance | BindingFlags.NonPublic);
            var original = ((IBattleSimulationSource)field.GetValue(Bootstrap)).BuildSeed();
            var d = original.Definition;
            var encounters = d.Encounters.Select(e => e with { Controllers = e.Controllers.Select(c =>
                mixed ? c with { SourceKind = CommandSourceKind.Ai,
                    AiPolicy = c.ControlledSlots.Any(s => s.Value == "hero") ? AiPolicyKind.SustainedOffense : AiPolicyKind.CautiousDefense,
                    AiExecution = AiExecutionKind.UtilityBehaviorTree, AiForecast = new AiForecastSettings(360, true, true,
                        shortPlans ? new AiSamplingSettings(SignedDamage:true,HealthDefense:true) : null,
                        shortPlans ? new AiShortPlanSettings() : null) }
                : c.SourceKind == CommandSourceKind.Ai ? c with { AiPolicy = approach ? AiPolicyKind.CautiousDefense : AiPolicyKind.SustainedOffense,
                    AiExecution = AiExecutionKind.UtilityBehaviorTree, AiForecast = new AiForecastSettings(360, defenses, approach) } : c).ToArray() }).ToArray();
            // Explicit scene-test envelope; every action, unit, shape and rule comes from the real scene source.
            var hash = BattleDefinitionHash.Compute(d.RulesVersion, d.TicksPerSecond, 0, d.Rules, d.ConcurrentAction,
                d.ReactionRules, d.AdrenalineRules, d.FactionModel, d.DamageChannels, d.ImpactProfiles, d.Units, d.Actions,
                d.AttackPatterns, d.Volumes, d.MovementPatterns, d.ActionSets, d.StatusEffects, encounters, d.DefaultDynamicSpawnPolicy);
            var definition = d with { Encounters = encounters, BattleDefinitionHashValue = hash };
            var source = NewGameObject("FormalHybridTestSource").AddComponent<Task11SceneScenarioSource>();
            source.Seed = new BattleSimulationSeed(definition, original.EncounterId, original.RuntimeInputs, "formal hybrid scene test");
            field.SetValue(Bootstrap, source);
            // A fully autonomous clock test has no Player entry to bind to the scene's player HUD.
            // Player presentation remains covered by the other real-scene cases in this fixture.
            if (mixed) Bootstrap.GetType().GetField("_viewConsumerSlot", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(Bootstrap, null);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            long initialHealth = Bootstrap.NewDriver.CurrentSnapshot.Units.Sum(u => (long)u.HealthQ10);
            bool executed = false;
            for (int tick = 0; tick < 360; tick++)
            {
                Bootstrap.Clock.ResumeTactically(); Bootstrap.DriveFrameForTests(0, 1f / 60);
                executed |= Bootstrap.NewDriver.CurrentSnapshot.AiControllers.Any(c => c.Behavior?.Ordinary.PlanId > 0);
            }
            Assert.That(executed, Is.True);
            if (mixed)
            {
                Assert.That(Bootstrap.NewDriver.CurrentSnapshot.AiControllers.Count, Is.EqualTo(2));
                Assert.That(Bootstrap.NewDriver.CurrentSnapshot.AiControllers.All(c => c.DecisionCount > 0), Is.True);
            }
            Assert.That(BattleRuntimeBootstrap.EnumerateLiveInstances().Count(b => b.OwnsTopLevelClock), Is.EqualTo(1));
            var replay = Bootstrap.NewDriver.RecordedReplay; Assert.That(replay.Submissions, Is.Empty);
            if (shortPlans)
            {
                long healthLoss = initialHealth - Bootstrap.NewDriver.CurrentSnapshot.Units.Sum(u => (long)u.HealthQ10);
                TestContext.WriteLine("SHORT_PLAN_SCENE observedTick=" + Bootstrap.NewDriver.CurrentSnapshot.Tick
                    + " healthLossQ10=" + healthLoss);
                // Recorder stores canonical StoredReplayEvent values, not the original concrete event types.
                var facts = replay.Records.SelectMany(r => r.Events).Select(ReplayEventComparison.Canonical).ToArray();
                Assert.That(facts.Any(e => e.StartsWith(typeof(ProjectHero.Logic.Events.CommandRejectedEvent).FullName + "{", System.StringComparison.Ordinal)
                    || e.StartsWith(typeof(ProjectHero.Logic.Events.CommandIngressRejectedEvent).FullName + "{", System.StringComparison.Ordinal)), Is.False);
                Assert.That(healthLoss, Is.GreaterThan(0));
            }
            using var player = new ReplayPlayer(definition); player.Load(replay); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            Assert.That(player.Deviation, Is.Null);
            Bootstrap.StopBattle("hybrid-test-complete"); Bootstrap.ReleaseBattle();
        }
    }
}
