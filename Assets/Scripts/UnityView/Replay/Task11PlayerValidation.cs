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
            if (!pressure && !arguments.Contains("-heroValidation")) return;
            var report = new StringBuilder("Development Player diagnostic; NOT complete performance acceptance\n");
            int exit = 0;
            try
            {
                if (pressure)
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
            File.WriteAllText(Path.Combine(directory, pressure ? "11-development-future-pressure-validation.txt" : "11-development-player-validation.txt"), report.ToString(), new UTF8Encoding(false));
            UnityEngine.Debug.Log(report.ToString()); Application.Quit(exit);
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

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
