using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Initialization;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class Task11IndependentSourceSceneTests : RuntimeOwnershipTestBase
    {
        [UnityTest] public IEnumerator NewAuthoringSourceKeepsDefinitionAndStartsAfterLegacyFactoryIsDestroyed()
        {
            yield return LoadLegacyComparisonScene(); Bootstrap.StopBattle("source-prepare"); Bootstrap.ReleaseBattle();
            var sourceSlot = typeof(BattleRuntimeBootstrap).GetField("_simulationSourceSlot", BindingFlags.Instance | BindingFlags.NonPublic);
            var oldSource = (MonoBehaviour)sourceSlot.GetValue(Bootstrap);
            var original = ((IBattleSimulationSource)oldSource).BuildSeed();
            var reader = ResolveProductionType("ProjectHero.Core.Compatibility.Authoring.LegacyCombatUnitStatsReader");
            object hero = reader.GetMethod("Read").Invoke(null, new[] { oldSource.GetType().GetField("_heroUnit", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(oldSource) });
            object enemy = reader.GetMethod("Read").Invoke(null, new[] { oldSource.GetType().GetField("_enemyUnit", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(oldSource) });
            var type = ResolveProductionType("ProjectHero.Core.Compatibility.Authoring.NewBattleSimulationSource");
            var source = NewGameObject("IndependentAuthoringSource").AddComponent(type);
            type.GetMethod("Configure").Invoke(source, new[] { hero, enemy, (object)original.RuntimeInputs.InitialRngSeed,
                original.RuntimeInputs.InitialMetaResource });
            var seed = ((IBattleSimulationSource)source).BuildSeed();
            Assert.That(((IBattleSimulationSource)source).LastConfigurationError, Is.Null);
            Assert.That(seed.BattleDefinitionHash, Is.EqualTo(original.BattleDefinitionHash));
            Assert.That(seed.EncounterId, Is.EqualTo(original.EncounterId));
            Assert.That(seed.RuntimeInputs, Is.EqualTo(original.RuntimeInputs));
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                Assert.That(field.FieldType.Name, Is.Not.EqualTo("CombatUnit"));
            sourceSlot.SetValue(Bootstrap, source);
            UnityEngine.Object.Destroy(oldSource); yield return null;
            Assert.That(oldSource == null, Is.True);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            Assert.That(Bootstrap.NewDriver.ReplayHeader.BattleDefinitionHash, Is.EqualTo(seed.BattleDefinitionHash));
            var initial = Bootstrap.NewDriver.ReplayHeader.InitialStateHash;
            using (var reference = ProjectHero.Logic.Simulation.ProductionBattleComposition.Create(original.Definition,
                original.EncounterId, original.RuntimeInputs)) Assert.That(initial, Is.EqualTo(reference.InitialStateHash));
            long before = LegacyTimelineAdvanceTimeCalls();
            for (int tick = 0; tick < 12; tick++) Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot.Tick, Is.GreaterThanOrEqualTo(10));
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(before));
            Bootstrap.StopBattle("source-complete"); Bootstrap.ReleaseBattle(); yield return null;
        }

        [UnityTest] public IEnumerator IndependentSourceRejectsMissingAuthoringInsteadOfReadingSceneUnits()
        {
            var type = ResolveProductionType("ProjectHero.Core.Compatibility.Authoring.NewBattleSimulationSource");
            var component = NewGameObject("MissingAuthoringSource").AddComponent(type);
            var source = (IBattleSimulationSource)component;
            Assert.That(source.BuildSeed(), Is.Null);
            Assert.That(source.LastConfigurationError, Is.EqualTo("NEW_SOURCE_AUTHORED_STATS_MISSING"));
            yield return null;
        }
    }
}
