using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Authoring;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Authoring.Tests
{
    public class Task10ProductionTests
    {
        private sealed class Source : IBattleSimulationSource
        {
            public string SourceName => "real-definition";
            public string LastConfigurationError => null;
            public BattleSimulationSeed BuildSeed() => new BattleSimulationSeed(Task03.Task03.Definition,
                Task03.Task03.EncounterId, Task03.Task03.Inputs, "real authoring");
        }
        [Test] public void ProductionWindowUsesExplicit180TickBudgetAndInitializerHeroSlot()
        {
            var definition = Task03.Task03.Definition; var encounter = Task03.Task03.Encounter;
            Assert.That(encounter.TurnSubmission.BudgetTicks, Is.EqualTo(180));
            Assert.That(encounter.TurnSubmission.ConcurrentHeroSlot.Value, Is.EqualTo("hero"));
            using (var sim = ProductionBattleComposition.Create(definition, encounter.EncounterId, Task03.Task03.Inputs))
            {
                var result = Task03.Task03.StepNext(sim);
                Assert.That(sim.CurrentTurnWindow.OwnerUnitId, Is.EqualTo(Task03.Task03.HeroUnitId));
                Assert.That(sim.CurrentTurnWindow.TotalBudgetTicks, Is.EqualTo(180));
                Assert.That(result.Snapshot.AiControllers.Count, Is.EqualTo(1));
                Assert.That(result.Events.Events.All(e => e.Tick == result.Tick), Is.True,
                    "Resource events must carry the executing Tick, not the previously completed Tick.");
            }
        }
        [Test] public void ProductionWindowClosesWithoutWaitingForActionsAndAiSubmitsThroughIngress()
        {
            using (var sim = ProductionBattleComposition.Create(Task03.Task03.Definition, Task03.Task03.EncounterId, Task03.Task03.Inputs))
            {
                Task03.Task03.StepNext(sim);
                Task03.Task03.PlayerEntry(sim).Submit(new CommandRequest(1, new WindowCommandScope(sim.CurrentTurnWindow.WindowId),
                    new WindowCommandPayload(WindowCommandKind.CloseOwnWindow)));
                Task03.Task03.StepNext(sim); Assert.That(sim.CurrentTurnWindow, Is.Null);
                Task03.Task03.StepNext(sim); Assert.That(sim.CurrentTurnWindow.OwnerUnitId, Is.EqualTo(Task03.Task03.EnemyUnitId));
                Assert.That(sim.CurrentSnapshot.CommandIngresses.FutureBuckets.Any(b => b.TargetTick == 3 && b.PendingCount > 0), Is.True);
                Task03.Task03.StepNext(sim);
                Assert.That(sim.LastCommandSet.Envelopes.Any(e => e.SourceKind == CommandSourceKind.Ai), Is.True);
            }
        }
        [Test] public void RuntimePlayerPortDropsPausedInputBeforeCommandConstruction()
        {
            var source = new Source(); var driver = new UnityBattleDriver();
            driver.Initialize(new BattleRuntimeContext(BattleRuntimeMode.New, new RuntimeCallLedger(), new ShadowWriteCounters(), source));
            driver.CreateSimulation(source);
            try
            {
                driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true));
                var ports = driver.CreatePlayerInputPorts(new ControllerId("controller.player"));
                Assert.That(ports.Logic.DecisionSnapshot.ControllerId, Is.EqualTo(new ControllerId("controller.player")));
                driver.SetPaused(true);
                Assert.That(ports.Commands.CreateCloseWindow(ports.Logic.OpenWindowId.Value), Is.Null);
                Assert.That(ports.Commands.CreateScheduleEdit(new ScheduleEditPayload(Array.Empty<ScheduleEditOperation>())), Is.Null);
                driver.SetPaused(false);
                var request = ports.Commands.CreateCloseWindow(ports.Logic.OpenWindowId.Value);
                Assert.That(ports.Logic.SubmitCommand(request), Is.Null);
                driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true));
                Assert.That(driver.RecordedReplay.Submissions.Count, Is.EqualTo(1));
            }
            finally { driver.ResetForNewBattle(); }
        }
        [Test] public void ProductionCompositionReplaysRealDefinitionWithRebuiltAi()
        {
            var definition = Task03.Task03.Definition; BattleReplay replay;
            using (var sim = ProductionBattleComposition.Create(definition, Task03.Task03.EncounterId, Task03.Task03.Inputs))
            {
                var recorder = new ReplayRecorder(sim);
                for (int tick = 0; tick < 120; tick++)
                {
                    if (tick == 1) Task03.Task03.PlayerEntry(sim).Submit(new CommandRequest(1,
                        new WindowCommandScope(sim.CurrentTurnWindow.WindowId), new WindowCommandPayload(WindowCommandKind.CloseOwnWindow)));
                    recorder.CaptureBeforeStep(); var result = Task03.Task03.StepNext(sim); recorder.RecordCommittedStep(result);
                    if (sim.IsEnded) break;
                }
                replay = recorder.BuildReplay();
                Assert.That(sim.CurrentSnapshot.AiControllers.Single().DecisionCount, Is.GreaterThan(2));
            }
            using (var player = new ReplayPlayer(definition))
            {
                player.Load(replay); player.Play();
                while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                Assert.That(player.Deviation, Is.Null); Assert.That(player.IsComplete, Is.True);
                Assert.That(replay.Submissions.All(s => s.Fact.SourceKind == CommandSourceKind.Player), Is.True);
            }
        }
        [Test] public void BudgetAndConcurrentHeroSlotParticipateInDefinitionHash()
        {
            var encounter = Task03.Task03.Encounter;
            var baseline = new CanonicalHashWriter(); encounter.WriteHashComponents(baseline);
            var changed = new CanonicalHashWriter();
            (encounter with { TurnSubmission = encounter.TurnSubmission with { BudgetTicks = 181 } }).WriteHashComponents(changed);
            Assert.That(changed.ToDigestHex(), Is.Not.EqualTo(baseline.ToDigestHex()));
            Assert.That((encounter.TurnSubmission with { ConcurrentHeroSlot = new EncounterSlotId("unknown") }).Validate(encounter),
                Is.EqualTo("ENCOUNTER_CONCURRENT_HERO_SLOT_UNKNOWN"));
        }
        [Test] public void NewDefinitionAnchorChangesOnlyWithExplicitTurnRulesAndRetainsOldCharacterization()
        {
            var definition = Task03.Task03.Definition;
            string Compute(string version, IReadOnlyList<EncounterDefinition> encounters)
                => BattleDefinitionHash.Compute(version, definition.TicksPerSecond, BattleDefinitionFixture.LibraryVolumeBindingCount,
                    definition.Rules, definition.ConcurrentAction, definition.ReactionRules, definition.AdrenalineRules,
                    definition.FactionModel, definition.DamageChannels, definition.ImpactProfiles, definition.Units,
                    definition.Actions, definition.AttackPatterns, definition.Volumes, definition.MovementPatterns,
                    definition.ActionSets, definition.StatusEffects, encounters, definition.DefaultDynamicSpawnPolicy);
            Assert.That(Compute("battle-def-v1", definition.Encounters.Select(e => e with { TurnSubmission = null }).ToArray()),
                Is.EqualTo("d997b13b18573e15"), "Retain the task08 characterization anchor with its original inputs.");
            Assert.That(Compute(definition.RulesVersion, definition.Encounters), Is.EqualTo("ed4c3e21b1488e60"));
        }
        [Test] public void EmptyShadowReportCannotAuthorizeNewCutover()
        {
            var report = new ShadowComparisonReport("hash", "encounter", BattleRuntimeMode.Shadow, "inputs", "rules", 1);
            Assert.That(NewCutoverGate.Rejections(report, "hash", "rules", "encounter"), Contains.Item("CUTOVER_SHADOW_RUN_INVALID"));
            Assert.That(NewCutoverGate.Rejections(null, "hash", "rules", "encounter"), Contains.Item("CUTOVER_SHADOW_REPORT_MISSING"));
        }
        [Test] public void TemporarilyUncomparableShadowFieldBlocksNewCutover()
        {
            using (var sim = Task03.Task03.NewSim())
            {
                var snapshot = Task03.Task03.StepNext(sim).Snapshot;
                var report = ShadowDifferenceDetector.Compare(new ShadowComparisonInput(sim.BattleDefinitionHash,
                    sim.EncounterId.Value, BattleRuntimeMode.Shadow, "test", sim.RulesVersion,
                    ShadowComparisonConfig.Default(), new[] { snapshot }, new[] { snapshot }),
                    ShadowCasePolicy.CreateDefault("task10-cutover-temporary", sim.RulesVersion));
                Assert.That(report.TemporarilyUncomparable.Count, Is.GreaterThan(0));
                Assert.That(NewCutoverGate.Rejections(report, sim.BattleDefinitionHash, sim.RulesVersion, sim.EncounterId.Value)
                    .Any(e => e.StartsWith("CUTOVER_TEMPORARY_FIELD|", StringComparison.Ordinal)), Is.True);
            }
        }
        [Test] public void InvalidOrBudgetExceededShadowRunCannotAuthorizeNewCutover()
        {
            using (var sim = Task03.Task03.NewSim())
            {
                var snapshot = Task03.Task03.StepNext(sim).Snapshot;
                var many = Enumerable.Repeat(snapshot, 65).ToArray();
                var report = ShadowDifferenceDetector.Compare(new ShadowComparisonInput(sim.BattleDefinitionHash,
                    sim.EncounterId.Value, BattleRuntimeMode.Shadow, "test", sim.RulesVersion,
                    ShadowComparisonConfig.Default(), many, many),
                    ShadowCasePolicy.CreateDefault("task10-cutover-over-budget", sim.RulesVersion));
                Assert.That(report.BudgetOverrun, Is.True);
                Assert.That(NewCutoverGate.Rejections(report, sim.BattleDefinitionHash, sim.RulesVersion, sim.EncounterId.Value),
                    Contains.Item("CUTOVER_SHADOW_RUN_INVALID"));
            }
        }
        [Test] public void BroadShadowDifferenceAllowlistIsRejected()
        {
            var policy = ShadowCasePolicy.CreateWithCandidateApprovals("task10-cutover-broad", "rules",
                new[] { new KeyValuePair<string, string>("units[*].health", "ignore") });
            Assert.That(policy.Rejections, Is.Not.Empty);
        }
        [Test] public void UnityViewAssemblyDoesNotReferenceAssemblyCSharpLegacyTypes()
        {
            var runtimeAssembly = typeof(BattleRuntimeBootstrap).Assembly;
            Assert.That(runtimeAssembly.GetName().Name, Is.EqualTo("ProjectHero.UnityView"));
            Assert.That(runtimeAssembly.GetReferencedAssemblies().Any(a => a.Name.StartsWith("Assembly-CSharp", StringComparison.Ordinal)), Is.False);
            Assert.That(typeof(IBattleFrameAdapter).Assembly, Is.SameAs(runtimeAssembly));
        }
        [Test] public void CustomAssemblyDoesNotReferenceAssemblyCSharp()
        {
            Assert.That(typeof(BattleDefinitionBuilder).Assembly.GetReferencedAssemblies()
                .Any(a => a.Name.StartsWith("Assembly-CSharp", StringComparison.Ordinal)), Is.False);
            Assert.That(typeof(BattleSimulation).Assembly.GetReferencedAssemblies()
                .Any(a => a.Name.StartsWith("UnityEngine", StringComparison.Ordinal)), Is.False);
        }
    }
}
