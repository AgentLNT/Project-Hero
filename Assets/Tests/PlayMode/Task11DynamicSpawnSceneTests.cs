using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.UnityView;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class Task11DynamicSpawnSceneTests : RuntimeOwnershipTestBase
    {
        protected override bool UseLegacyComparisonScene => false;
        [UnityTest] public IEnumerator MainSceneDynamicUnitBindsImmutableFactionAndReleasesViewAndReplays()
        {
            yield return LoadMainScene(); Bootstrap.StopBattle("spawn-prepare"); Bootstrap.ReleaseBattle();
            var slotField = typeof(BattleRuntimeBootstrap).GetField("_simulationSourceSlot", BindingFlags.Instance | BindingFlags.NonPublic);
            var original = ((IBattleSimulationSource)slotField.GetValue(Bootstrap)).BuildSeed();
            var d = original.Definition; var e = d.FindEncounter(original.EncounterId);
            var heroSlot = e.TurnSubmission.ConcurrentHeroSlot;
            var hero = e.Slots.Single(s => s.SlotId == heroSlot);
            var volume = d.FindVolume(d.FindUnit(hero.DefinitionId).VolumeSpecId);
            GridPoint position = default; bool found = false;
            using (var probe = ProductionBattleComposition.Create(d, original.EncounterId, original.RuntimeInputs))
                foreach (var point in e.GridBoundary.EnumerateValidPoints())
                {
                    var cells = VolumeFootprint.ResolveCells(volume.Directions, point, hero.InitialFacing);
                    if (cells.Any(c => !e.GridBoundary.Contains(c))) continue;
                    if (probe.LogicGrid.RegisterUnit(new UnitId(e.Slots.Count + 1), point, hero.InitialFacing, volume.Directions) == null)
                    { position = point; found = true; break; }
                }
            Assert.That(found, Is.True);
            e = e with { DynamicSpawns = new[] { new DynamicUnitSpawnDefinition("spawn.scene", 2,
                heroSlot, hero.DefinitionId, position, hero.InitialFacing, DynamicSpawnFactionPolicy.InheritSource()) } };
            d = d with { Encounters = d.Encounters.Select(x => x.EncounterId == e.EncounterId ? e : x).ToArray() };
            d = d with { BattleDefinitionHashValue = BattleDefinitionHash.Compute(d.RulesVersion, d.TicksPerSecond, 0,
                d.Rules, d.ConcurrentAction, d.ReactionRules, d.AdrenalineRules, d.FactionModel, d.DamageChannels,
                d.ImpactProfiles, d.Units, d.Actions, d.AttackPatterns, d.Volumes, d.MovementPatterns,
                d.ActionSets, d.StatusEffects, d.Encounters, d.DefaultDynamicSpawnPolicy) };
            var source = NewGameObject("Task11DynamicSceneSource").AddComponent<Task11SceneScenarioSource>();
            source.Seed = new BattleSimulationSeed(d, original.EncounterId, original.RuntimeInputs,
                "Explicit dynamic creation Encounter fixture; original production unit definitions, geometry and composition");
            slotField.SetValue(Bootstrap, source);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            long oldAdvance = LegacyTimelineAdvanceTimeCalls();
            for (int tick = 0; tick < 4; tick++) Bootstrap.DriveFrameForTests(0, 1f / 60);
            var snapshot = Bootstrap.NewDriver.CurrentSnapshot;
            var created = snapshot.Units.Single(u => u.UnitId == e.Slots.Count + 1);
            var registry = Object.FindFirstObjectByType<BattleViewRegistry>();
            Assert.That(registry.TryGetView(created.UnitId, out var view), Is.True);
            Assert.That(view.FactionId.Value, Is.EqualTo(hero.FactionId.Value));
            Assert.That(view.LatestSnapshot, Is.SameAs(created));
            Assert.Throws<LogicDefinitionException>(() => view.ApplySnapshot(created with { FactionId = "forged" }));
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(oldAdvance));
            var presentation = Object.FindFirstObjectByType<BattlePresentationView>();
            Assert.That(presentation.Model.CloseWindow(), Is.Null);
            Bootstrap.DriveFrameForTests(0, 1f / 60); Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(presentation.Model.Window.OwnerUnitId, Is.EqualTo(created.UnitId));
            Assert.That(presentation.SelectedActingUnit.Value, Is.EqualTo(created.UnitId));
            var guard = presentation.Model.Decision.ActionSetOf(new UnitId(created.UnitId))
                .Single(id => presentation.Model.Decision.FindAction(id).Type == ProjectHero.Logic.Actions.ActionType.Guard);
            presentation.SelectAction(guard);
            Assert.That(presentation.Model.Preview.Succeeded, Is.True);
            Assert.That(presentation.Model.ConfirmDraft(), Is.Null); Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(presentation.Model.Decision.OwnPlans.Any(p => p.OwnerUnitId == created.UnitId), Is.True,
                "The actual Canvas must be able to act when a newly created controlled unit owns the window.");
            Assert.That(Bootstrap.NewDriver.RecordedReplay.Records.SelectMany(r => r.Events)
                .Count(f => ReplayEventComparison.Canonical(f).StartsWith(typeof(UnitCreatedEvent).FullName + "{")), Is.EqualTo(1));
            string path = Path.GetFullPath("优化任务/执行记录/11-dynamic-scene.heroReplay");
            using (var file = File.Create(path)) ReplayFile.Save(file, Bootstrap.NewDriver.RecordedReplay);
            BattleReplay replay; using (var file = File.OpenRead(path)) replay = ReplayFile.Load(file, d);
            using (var player = new ReplayPlayer(d, h => ProductionBattleComposition.Create(d, h.EncounterId, h.RuntimeInputs)))
                for (int run = 0; run < 100; run++)
                {
                    if (run == 0) player.Load(replay); else player.Restart(); player.Play();
                    while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                    Assert.That(player.Deviation, Is.Null); Assert.That(player.IsComplete, Is.True);
                }
            Bootstrap.StopBattle("spawn-complete"); Bootstrap.ReleaseBattle();
            Assert.That(registry.TryGetView(created.UnitId, out _), Is.False);
            yield return null;
            Assert.That(view == null, Is.True, "Created graphics must be destroyed on release.");
        }
    }
}
