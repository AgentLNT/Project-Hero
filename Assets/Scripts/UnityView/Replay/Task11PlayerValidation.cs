#if DEVELOPMENT_BUILD && !UNITY_EDITOR
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using UnityEngine;

namespace ProjectHero.UnityView.Replay
{
    /// <summary>Explicit headless Development Player diagnostic; never starts in a normal game session.</summary>
    public static class Task11PlayerValidation
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunWhenRequested()
        {
            var arguments = Environment.GetCommandLineArgs();
            bool pressure = arguments.Contains("-heroPressure");
            bool migration = arguments.Contains("-heroMigrationValidation");
            if (!pressure && !migration && !arguments.Contains("-heroValidation")) return;
            var report = new StringBuilder("Development Player diagnostic; NOT complete performance acceptance\n");
            int exit = 0;
            try
            {
                if (migration) RunMigrationOnly(report);
                else if (pressure)
                {
                    var source = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None).OfType<IBattleSimulationSource>().Single();
                    var seed = source.BuildSeed(); Require(seed.Validate() == null, "INVALID_PRESSURE_BASIS");
                    Task11PressureValidation.Run(seed, report, arguments.Contains("-heroPressureQuick"));
                }
                else Run(report);
                report.AppendLine("Result=Passed");
            }
            catch (Exception error) { exit = 1; report.AppendLine("Result=Failed\n" + error); }
            string directory = Path.GetFullPath("优化任务/执行记录"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, migration ? "11-native-migration-validation.txt"
                : pressure ? "11-development-future-pressure-validation.txt" : "11-development-player-validation.txt"), report.ToString(), new UTF8Encoding(false));
            UnityEngine.Debug.Log(report.ToString()); Application.Quit(exit);
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        private static void RunMigrationOnly(StringBuilder report)
        {
            var source = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None).OfType<IBattleSimulationSource>().Single();
            var seed = source.BuildSeed(); Require(seed != null && seed.Validate() == null && source.LastConfigurationError == null, "MIGRATION_SOURCE_INVALID");
            Require(source.GetType().Assembly.GetName().Name == "ProjectHero.UnityAuthoring", "PRODUCTION_SOURCE_NOT_INDEPENDENT");
            Require(Type.GetType("ProjectHero.Core.Entities.CombatUnit, Assembly-CSharp", false) == null
                && Type.GetType("ProjectHero.Core.Timeline.BattleTimeline, Assembly-CSharp", false) == null
                && Type.GetType("ProjectHero.Demos.CombatDemo, Assembly-CSharp", false) == null, "LEGACY_RUNTIME_TYPE_IN_PLAYER");
            var bootstrap = BattleRuntimeBootstrap.EnumerateLiveInstances().Single();
            bootstrap.StopBattle("native-migration-prepare"); bootstrap.ReleaseBattle();
            Require(!bootstrap.StartBattle(BattleRuntimeMode.Legacy), "NATIVE_LEGACY_START_ALLOWED");
            Require(!bootstrap.StartBattle(BattleRuntimeMode.Shadow), "NATIVE_SHADOW_START_ALLOWED");
            Require(bootstrap.StartBattle(BattleRuntimeMode.New), "NATIVE_NEW_START_REJECTED:" + bootstrap.StartupRejection);
            var presentation = UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>();
            Require(presentation != null && bootstrap.Adapters.Legacy == null, "NATIVE_LEGACY_ADAPTER_OR_VIEW_INVALID");
            bootstrap.DriveFrameForTests(0, 1f / 60);
            var model = presentation.Model; long lastClose = 0;
            for (int frame = 0; frame < 12000 && !model.Decision.BattleEnd.IsEnded; frame++)
            {
                var owner = model.Decision.ControlledUnitIds.Single();
                if (model.Window?.OwnerUnitId == owner.Value && model.Window.WindowId != lastClose)
                {
                    if (!model.Decision.OwnPlans.Any(p => p.OwnerUnitId == owner.Value) && DraftMigrationCombatAction(model))
                        Require(model.ConfirmDraft() == null, "NATIVE_PLAYER_CONFIRM_FAILED");
                    else { lastClose = model.Window.WindowId; Require(model.CloseWindow() == null, "NATIVE_CLOSE_FAILED"); }
                }
                bootstrap.DriveFrameForTests(0, 1f / 60);
            }
            Require(model.Decision.BattleEnd.IsEnded && model.SubmittedCommands > 0, "NATIVE_COMPLETE_BATTLE_MISSING");
            Require(UnityEngine.Object.FindFirstObjectByType<BattleFeedbackPlayer>().EndFeedbackCount == 1, "NATIVE_END_FEEDBACK_DUPLICATED");
            var replay = bootstrap.NewDriver.RecordedReplay;
            using var file = new MemoryStream(); ReplayFile.Save(file, replay); file.Position = 0;
            var loaded = ReplayFile.Load(file, seed.Definition);
            using var player = new ReplayPlayer(seed.Definition, h => ProductionBattleComposition.Create(seed.Definition, h.EncounterId, h.RuntimeInputs));
            for (int run = 0; run < 100; run++)
            {
                if (run == 0) player.Load(loaded); else player.Restart(); player.Play();
                while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                Require(player.IsComplete && player.Deviation == null, "NATIVE_MIGRATION_REPLAY_DIVERGENCE:" + player.Deviation);
            }
            var metadata = ReplayEventComparison.Canonical(new UnitCreatedEvent(2, 1, "spawn.native-probe", new UnitId(3),
                new UnitDefinitionId("unit.native-probe"), new UnitId(2), new FactionId("faction.hero"), new GridPoint(2, 4), GridDirection.East));
            Require(metadata.Contains("SpawnId=") && metadata.Contains("FactionId=") && metadata.Contains("Position="), "NATIVE_CREATION_METADATA_STRIPPED");
            ValidateNativeDynamicCreation(seed, report);
            report.AppendLine("Mode=New\nSource=" + source.GetType().FullName + "\nDefinition=" + seed.BattleDefinitionHash
                + "\nLegacyRuntimeTypes=Absent\nLegacyStartup=Rejected\nShadowStartup=Rejected\nLegacyAdapter=Absent"
                + "\nCompleteBattleTick=" + model.Decision.Tick + "\nPlayerCommands=" + model.SubmittedCommands
                + "\nReplayRestarts=100\nDynamicCreationMetadata=Passed\nPerformance=Deferred; no timing/allocation/GC measurement in this run");
            bootstrap.ReleaseBattle();
        }

        private static void ValidateNativeDynamicCreation(BattleSimulationSeed seed, StringBuilder report)
        {
            var definition = seed.Definition; var encounter = definition.FindEncounter(seed.EncounterId);
            var hero = encounter.Slots.Single(s => s.SlotId == encounter.TurnSubmission.ConcurrentHeroSlot);
            var enemy = encounter.Slots.Single(s => s.SlotId != hero.SlotId);
            var spawns = new System.Collections.Generic.List<ProjectHero.Logic.Definitions.DynamicUnitSpawnDefinition>();
            using (var geometry = ProductionBattleComposition.Create(definition, seed.EncounterId, seed.RuntimeInputs))
                foreach (var pair in new[] { ("spawn.native.fixed", enemy), ("spawn.native.inherited", hero) })
                {
                    var volume = definition.FindVolume(definition.FindUnit(pair.Item2.DefinitionId).VolumeSpecId);
                    bool found = false;
                    foreach (var point in encounter.GridBoundary.EnumerateValidPoints())
                    {
                        if (VolumeFootprint.ResolveCells(volume.Directions, point, pair.Item2.InitialFacing)
                            .Any(p => !encounter.GridBoundary.Contains(p))) continue;
                        if (geometry.LogicGrid.RegisterUnit(new UnitId(encounter.Slots.Count + spawns.Count + 1),
                            point, pair.Item2.InitialFacing, volume.Directions) != null) continue;
                        spawns.Add(new ProjectHero.Logic.Definitions.DynamicUnitSpawnDefinition(pair.Item1, 2,
                            pair.Item2.SlotId, pair.Item2.DefinitionId, point, pair.Item2.InitialFacing,
                            spawns.Count == 0 ? ProjectHero.Logic.Definitions.DynamicSpawnFactionPolicy.Fixed(hero.FactionId)
                                : ProjectHero.Logic.Definitions.DynamicSpawnFactionPolicy.InheritSource()));
                        found = true; break;
                    }
                    Require(found, "NATIVE_SPAWN_FIXTURE_SPACE_MISSING");
                }
            var changed = encounter with { DynamicSpawns = spawns.ToArray() };
            definition = definition with { Encounters = definition.Encounters.Select(e => e.EncounterId == changed.EncounterId ? changed : e).ToArray() };
            definition = definition with { BattleDefinitionHashValue = BattleDefinitionHash.Compute(definition.RulesVersion,
                definition.TicksPerSecond, 0, definition.Rules, definition.ConcurrentAction, definition.ReactionRules,
                definition.AdrenalineRules, definition.FactionModel, definition.DamageChannels, definition.ImpactProfiles,
                definition.Units, definition.Actions, definition.AttackPatterns, definition.Volumes, definition.MovementPatterns,
                definition.ActionSets, definition.StatusEffects, definition.Encounters, definition.DefaultDynamicSpawnPolicy) };
            BattleReplay recorded; int creationCount = 0;
            using (var simulation = ProductionBattleComposition.Create(definition, seed.EncounterId, seed.RuntimeInputs))
            {
                var recorder = new ReplayRecorder(simulation);
                for (int tick = 0; tick < 6; tick++)
                {
                    recorder.CaptureBeforeStep(); var result = simulation.Step(tick, simulation.CommandIngress.FreezeTick(tick));
                    recorder.RecordCommittedStep(result);
                    creationCount += result.Events.Events.OfType<UnitCreatedEvent>().Count();
                }
                Require(creationCount == 2 && simulation.CurrentSnapshot.Units.Count == encounter.Slots.Count + 2
                    && simulation.CurrentSnapshot.Units.Where(u => u.UnitId > encounter.Slots.Count).All(u => u.FactionId == hero.FactionId.Value),
                    "NATIVE_DYNAMIC_CREATION_FACTS_INVALID");
                recorded = recorder.BuildReplay();
            }
            using var file = new MemoryStream(); ReplayFile.Save(file, recorded); file.Position = 0;
            var loaded = ReplayFile.Load(file, definition);
            using var replay = new ReplayPlayer(definition, h => ProductionBattleComposition.Create(definition, h.EncounterId, h.RuntimeInputs));
            for (int run = 0; run < 100; run++)
            {
                if (run == 0) replay.Load(loaded); else replay.Restart(); replay.Play();
                while (!replay.IsComplete && replay.Deviation == null) replay.AdvanceOneTick();
                Require(replay.IsComplete && replay.Deviation == null, "NATIVE_DYNAMIC_REPLAY_DIVERGENCE:" + replay.Deviation);
            }
            report.AppendLine("NativeDynamicCreation=Passed; fixed/inherit events and snapshots; explicit Encounter fixture, file restarts=100");
        }

        private static bool DraftMigrationCombatAction(BattlePresentationModel model)
        {
            var decision = model.Decision; var hero = decision.ControlledUnitIds.Single();
            var owner = decision.VisibleUnits.Single(u => u.UnitId == hero.Value);
            var attacks = decision.ActionSetOf(hero).Select(decision.FindAction).Where(s => s.Type == ActionType.Attack).ToArray();
            var destinations = new System.Collections.Generic.HashSet<GridPoint>();
            foreach (var spec in attacks)
                foreach (var target in model.Targets(hero, spec.ActionSpecId))
                {
                    var attack = (AttackPayloadSpec)spec.Payload; if (attack.Pattern == null) continue;
                    var volume = decision.Definition.FindVolume(decision.Definition.FindUnit(new UnitDefinitionId(target.DefinitionId)).VolumeSpecId);
                    var targetCells = volume.Directions.Single(d => (int)d.Direction == target.Facing).Triangles;
                    for (int f = 0; f < 12; f++)
                    {
                        var facing = (GridDirection)f; var area = attack.Pattern.Directions.Single(d => d.Direction == facing).Triangles;
                        if (area.Any(a => targetCells.Any(b => owner.X + a.X == target.X + b.X && owner.Y + a.Y == target.Y + b.Y && a.T == b.T))
                            && model.SetDraft(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, hero, spec.ActionSpecId,
                                PrimaryTargetUnitId: new UnitId(target.UnitId), Facing: facing) })?.Succeeded == true) return true;
                        foreach (var a in area) foreach (var b in targetCells)
                            if (a.T == b.T && ((target.X + b.X - a.X + target.Y + b.Y - a.Y) & 1) == 0)
                                destinations.Add(new GridPoint(target.X + b.X - a.X, target.Y + b.Y - a.Y));
                    }
                }
            foreach (var spec in decision.ActionSetOf(hero).Select(decision.FindAction).Where(s => s.Type == ActionType.Move))
                foreach (var destination in destinations.OrderBy(p => Math.Abs(p.X - owner.X) + Math.Abs(p.Y - owner.Y)).ThenBy(p => p.X).ThenBy(p => p.Y))
                    if (model.SetDraft(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, hero, spec.ActionSpecId, Destination: destination) })?.Succeeded == true) return true;
            model.CancelDraft(); return false;
        }

        private static void Run(StringBuilder report)
        {
            report.AppendLine("Unity=" + Application.unityVersion + "\nPlatform=" + Application.platform
                + "\nProcessor=" + SystemInfo.processorType + "\nMemoryMB=" + SystemInfo.systemMemorySize);
            var source = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None).OfType<IBattleSimulationSource>().Single();
            var seed = source.BuildSeed(); Require(seed.Validate() == null && source.LastConfigurationError == null, "INVALID_SCENE_SOURCE");
            Require(seed.BattleDefinitionHash == "ed4c3e21b1488e60", "DEFINITION_ANCHOR_CHANGED");
            report.AppendLine("Rules=" + seed.RulesVersion + "\nDefinition=" + seed.BattleDefinitionHash);
            // This checks properties used only through reflection after actual IL2CPP/linker processing.
            var metadata = ReplayEventComparison.Canonical(new DamageChannelResolvedEvent(0, 1, 1, new UnitId(1),
                new ActionPlanId(1), new UnitId(2), default, GridDirection.East, 200, 150,
                new[] { new DamageChannelEntry(DamageChannels.PhysicalBlunt, 1024, 900, 800, 100, 200, DamageTagMask.Blockable) },
                800, 800, "METADATA_PROBE", Array.Empty<ActionPlanId>()));
            Require(metadata.Contains("RawQ10=1024;") && metadata.Contains("AfterPassiveResistanceQ10=900;")
                && metadata.Contains("AfterActionResistanceQ10=800;") && metadata.Contains("TargetUnitId="), "EVENT_METADATA_STRIPPED");
            report.AppendLine("IL2CPP damage-event metadata probe=Passed");

            BattleReplay replay;
            long[] elapsed = new long[1200]; long allocated = 0, eventCount = 0;
            int peakPlans = 0, peakReservations = 0;
            long probeStart = GC.GetAllocatedBytesForCurrentThread(); var probe = new byte[4096];
            long probeDelta = GC.GetAllocatedBytesForCurrentThread() - probeStart; GC.KeepAlive(probe);
            using (var sim = ProductionBattleComposition.Create(seed.Definition, seed.EncounterId, seed.RuntimeInputs))
            {
                Require(sim.InitialStateHashHex == "bc70adb81214a364", "PRE_STEP_HASH_CHANGED");
                var recorder = new ReplayRecorder(sim);
                var player = seed.Definition.FindEncounter(seed.EncounterId).Controllers.Single(c => c.SourceKind == ProjectHero.Logic.Definitions.CommandSourceKind.Player);
                var entry = sim.CommandIngress.FindEntry(player.ControllerId);
                int gcStart = GC.CollectionCount(0);
                var wall = Stopwatch.StartNew();
                for (int tick = 0; tick < elapsed.Length; tick++)
                {
                    // Empty-player combat diagnostic: close own windows; production AI still creates
                    // its real plans/terminal history. No fabricated health, contact or reaction facts.
                    var window = sim.CurrentTurnWindow;
                    if (window != null && sim.CommandAuthority.CanControl(player.ControllerId, window.OwnerUnitId))
                        Require(entry.Submit(new CommandRequest(tick, new WindowCommandScope(window.WindowId),
                            new WindowCommandPayload(WindowCommandKind.CloseOwnWindow))) == null, "PLAYER_SUBMISSION_REJECTED");
                    recorder.CaptureBeforeStep();
                    long before = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
                    var result = sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                    elapsed[tick] = Stopwatch.GetTimestamp() - start;
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                    recorder.RecordCommittedStep(result);
                    eventCount += result.Events.Events.Count;
                    peakPlans = Math.Max(peakPlans, result.Snapshot.Plans.Count);
                    peakReservations = Math.Max(peakReservations, result.Snapshot.Reservations.Count);
                    Require(!sim.IsEnded, "DIAGNOSTIC_UNEXPECTEDLY_ENDED");
                }
                wall.Stop(); Array.Sort(elapsed);
                var access = sim.HistoryAccessCounters;
                Require(access.OldRecordReads + access.OldRecordCopies + access.OldRecordHashes == 0, "HISTORY_READ_ON_HOT_PATH");
                Require(sim.History.RecordCount > 0, "EMPTY_HISTORY_IS_NOT_PROOF");
                Require(sim.RecomputeHistoryDigestForDiagnostics().ToString("x16") == sim.History.Digest, "HISTORY_DIGEST_MISMATCH");
                double Ms(long value) => value * 1000d / Stopwatch.Frequency;
                report.AppendLine("DiagnosticTicks=1200\nUnits=" + sim.CurrentSnapshot.Units.Count + "\nPeakPlans=" + peakPlans
                    + "\nPeakReservations=" + peakReservations + "\nEvents=" + eventCount
                    + "\nHistoryRecords=" + sim.History.RecordCount + "\nOldHistoryHotReadsCopiesHashes=0/0/0"
                    + "\nStepP50Ms=" + Ms(elapsed[599]).ToString("F6", CultureInfo.InvariantCulture)
                    + "\nStepP95Ms=" + Ms(elapsed[1139]).ToString("F6", CultureInfo.InvariantCulture)
                    + "\nStepP99Ms=" + Ms(elapsed[1187]).ToString("F6", CultureInfo.InvariantCulture)
                    + "\nAllocationProbeBytes=" + probeDelta + "\nAllocatedStepBytesPerTick="
                    + (probeDelta >= 4096 ? (allocated / elapsed.Length).ToString(CultureInfo.InvariantCulture) : "unavailable")
                    + "\nGC0InBurst=" + (GC.CollectionCount(0) - gcStart) + "\nBurstWallSeconds=" + wall.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
                replay = recorder.BuildReplay();
            }
            using (var file = new MemoryStream())
            {
                ReplayFile.Save(file, replay); file.Position = 0;
                var loaded = ReplayFile.Load(file, seed.Definition);
                using (var player = new ReplayPlayer(seed.Definition, h => ProductionBattleComposition.Create(seed.Definition, h.EncounterId, h.RuntimeInputs)))
                {
                    player.Load(loaded);
                    for (int run = 0; run < 100; run++)
                    {
                        if (run != 0) player.Restart(); player.Play();
                        while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                        Require(player.Deviation == null && player.IsComplete, "PLAYER_FILE_REPLAY_DIVERGENCE:run=" + run + ":" + player.Deviation);
                    }
                }
                report.AppendLine("NativePlayerFileReplayRestarts=100\nReplayBytes=" + file.Length);
            }
            report.AppendLine("Limits: no player attacks/movement, no complete battle, no 8/64/32 load or GC pause/real-time-minute gate.");
            var bootstrap = BattleRuntimeBootstrap.EnumerateLiveInstances().Single();
            bootstrap.StopBattle("development-view-prepare"); bootstrap.ReleaseBattle();
            Require(bootstrap.StartBattle(BattleRuntimeMode.New), "NATIVE_NEW_START_REJECTED:" + bootstrap.StartupRejection);
            var presentation = UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>();
            Require(presentation != null, "NATIVE_PRESENTATION_MISSING");
            bootstrap.DriveFrameForTests(0, 1f / 60);
            var model = presentation.Model;
            var hero = model.Decision.ControlledUnitIds.Single();
            CombatUnitView unitView = null;
            Require(model.Window?.TotalBudgetTicks == 180 && presentation.Registry.TryGetView(hero.Value, out unitView)
                && unitView.UnitId == hero, "NATIVE_SLOT_OR_WINDOW_BINDING_FAILED");
            bool drafted = false;
            foreach (var id in model.Decision.ActionSetOf(hero))
            {
                if (model.Decision.FindAction(id).Type != ActionType.Attack) continue;
                var targets = model.Targets(hero, id); if (targets.Count == 0) continue;
                var preview = model.SetDraft(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, hero, id,
                    PrimaryTargetUnitId: new UnitId(targets[0].UnitId)) });
                if (preview?.Succeeded != true) continue;
                drafted = true; break;
            }
            Require(drafted && model.ConfirmDraft() == null, "NATIVE_PLAYER_CONFIRM_FAILED");
            model.ConfirmDraft(); Require(model.SubmittedCommands == 1, "NATIVE_DUPLICATE_COMMAND");
            bootstrap.DriveFrameForTests(0, 1f / 60);
            Require(model.Decision.OwnPlans.Any(p => p.State == (int)ActionPlanState.Running), "NATIVE_PLAN_NOT_STARTED");
            Require(bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime) == 0, "NATIVE_OLD_CLOCK_ADVANCED");
            bootstrap.StopBattle("development-view-complete"); bootstrap.ReleaseBattle();
            Require(!unitView.UnitId.IsValid, "NATIVE_VISUAL_ID_NOT_RELEASED");
            report.AppendLine("Native UnityView smoke=Passed; real serialized slot/window, Player preview/confirm, single command, old-clock zero, release\nNative view smoke is not a complete native battle or visual capture.");
        }
    }
}
#endif
