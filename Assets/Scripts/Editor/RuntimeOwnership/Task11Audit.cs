using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Replay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ProjectHero.Editor.RuntimeOwnership
{
    /// <summary>Read-only project audit and explicitly labelled Editor diagnostics, not a release cutover.</summary>
    public static class Task11Audit
    {
        private static readonly string Output = Path.GetFullPath("优化任务/执行记录");
        public static void Run()
        {
            Directory.CreateDirectory(Output);
            var report = new StringBuilder("Task10/11 audit: NOT FINAL ACCEPTANCE\n");
            var scenes = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            foreach (string path in scenes)
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                int missing = 0, bootstraps = 0;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                        foreach (var bootstrap in t.GetComponents<BattleRuntimeBootstrap>())
                        {
                            bootstraps++;
                            report.AppendLine(path + "|mode=" + new SerializedObject(bootstrap).FindProperty("_requestedMode").enumValueIndex);
                        }
                    }
                report.AppendLine(path + "|bootstrapCount=" + bootstraps + "|missingScripts=" + missing);
            }
            int missingPrefabScripts = 0, prefabCount = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    prefabCount++;
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        missingPrefabScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            report.AppendLine("prefabs=" + prefabCount + "|missingPrefabScripts=" + missingPrefabScripts);
            report.AppendLine("UnityViewAssembly=" + typeof(BattleRuntimeBootstrap).Assembly.GetName().Name);
            report.AppendLine("UnityViewReferences=" + string.Join(",", typeof(BattleRuntimeBootstrap).Assembly.GetReferencedAssemblies().Select(a => a.Name)));
            EditorSceneManager.OpenScene(RuntimeOwnershipSceneTool.MainScenePath, OpenSceneMode.Single);
            var source = UnityEngine.Object.FindFirstObjectByType<BattleSimulationSourceFactory>();
            if (source == null) throw new InvalidOperationException("Main scene source missing");
            var seed = source.BuildSeed();
            if (seed.Validate() != null || source.LastConfigurationError != null) throw new InvalidOperationException(source.LastConfigurationError ?? seed.Validate());
            report.AppendLine("definition=" + seed.BattleDefinitionHash + "|rules=" + seed.RulesVersion + "|encounter=" + seed.EncounterId.Value);
            report.AppendLine("rng=" + seed.RuntimeInputs.InitialRngSeed + "|meta=" + seed.RuntimeInputs.InitialMetaResource);
            var initial = BattleInitializer.BuildInitialState(seed.Definition, seed.EncounterId, seed.RuntimeInputs);
            foreach (var slot in initial.SlotToUnitId.OrderBy(p => p.Key.Value, StringComparer.Ordinal))
                report.AppendLine("binding=" + slot.Key.Value + "|unit=" + slot.Value.Value + "|faction=" + initial.SlotToFaction[slot.Key].Value);
            using (var sim = ProductionBattleComposition.Create(seed.Definition, seed.EncounterId, seed.RuntimeInputs))
                report.AppendLine("replayFormat=" + ReplayFormat.Version + "|initialHash=" + sim.InitialStateHashHex);
            var policy = ShadowCasePolicy.CreateDefault("task09-command-and-ai-shadow-profile", seed.RulesVersion, true, true, true, true);
            report.AppendLine("fullShadowPolicyTemporaryFields=" + policy.TemporarilyUncomparable.Count);
            foreach (var field in policy.TemporarilyUncomparable) report.AppendLine("BLOCKED_TEMPORARY|" + field.Id + "|" + field.OwnerTask + "|" + field.Reason);
            File.WriteAllText(Path.Combine(Output, "11-project-audit.txt"), report.ToString(), new UTF8Encoding(false));
            Benchmark(seed);
        }

        private static void Benchmark(BattleSimulationSeed seed)
        {
            // This is a reproducible low-load diagnostic of the actual 2B definition in the Editor.
            // It does not satisfy the Development Build, maximum-load or complete combat performance gates.
            long probeBefore = GC.GetAllocatedBytesForCurrentThread();
            var probe = new byte[4096];
            long probeDelta = GC.GetAllocatedBytesForCurrentThread() - probeBefore;
            GC.KeepAlive(probe);
            bool allocationCounterAvailable = probeDelta >= 4096;
            var csv = new StringBuilder("environment,minute,units,plans,reservations,p50_ms,p95_ms,p99_ms,allocated_bytes_per_tick,allocation_probe_delta,gc0_delta,managed_bytes,history_records,old_reads,old_copies,old_hashes,replay_file_bytes\n");
            var selected = new[] { 3600, 36000, 108000 };
            using (var sim = ProductionBattleComposition.Create(seed.Definition, seed.EncounterId, seed.RuntimeInputs))
            {
                var recorder = new ReplayRecorder(sim);
                for (int tick = 0; tick < 108000; tick++)
                {
                    int window = Array.FindIndex(selected, end => tick >= end - 1800 && tick < end);
                    bool measure = window >= 0;
                    long beforeBytes = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
                    long started = measure ? Stopwatch.GetTimestamp() : 0;
                    recorder.CaptureBeforeStep();
                    var result = sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                    long elapsed = measure ? Stopwatch.GetTimestamp() - started : 0;
                    long allocated = measure ? GC.GetAllocatedBytesForCurrentThread() - beforeBytes : 0;
                    recorder.RecordCommittedStep(result);
                    if (measure) Samples[window].Add(elapsed, allocated);
                    if (sim.IsEnded) throw new InvalidOperationException("Diagnostic ended before 30 simulated minutes");
                    if (selected.Contains(tick + 1))
                    {
                        int index = Array.IndexOf(selected, tick + 1); var sample = Samples[index];
                        using (var bytes = new MemoryStream())
                        {
                            ReplayFile.Save(bytes, recorder.BuildReplay());
                            var counters = sim.HistoryAccessCounters;
                            csv.AppendLine(string.Join(",", "Unity Editor low-load diagnostic", (tick + 1) / 3600,
                                sim.CurrentSnapshot.Units.Count, sim.CurrentSnapshot.Plans.Count, sim.CurrentSnapshot.Reservations.Count,
                                sample.Percentile(50).ToString("F6", CultureInfo.InvariantCulture),
                                sample.Percentile(95).ToString("F6", CultureInfo.InvariantCulture),
                                sample.Percentile(99).ToString("F6", CultureInfo.InvariantCulture),
                                allocationCounterAvailable ? (sample.Allocations / sample.Count).ToString(CultureInfo.InvariantCulture) : "unavailable",
                                probeDelta, GC.CollectionCount(0) - sample.GcBaseline,
                                GC.GetTotalMemory(false), sim.History.RecordCount,
                                counters.OldRecordReads, counters.OldRecordCopies, counters.OldRecordHashes, bytes.Length));
                        }
                    }
                }
            }
            File.WriteAllText(Path.Combine(Output, "11-editor-performance-diagnostic.csv"), csv.ToString(), new UTF8Encoding(false));
        }

        private static readonly Sample[] Samples = { new Sample(), new Sample(), new Sample() };
        private sealed class Sample
        {
            private readonly int[] _histogram = new int[100001]; // 1 microsecond bins, fixed memory.
            public int Count, GcBaseline;
            public long Allocations;
            public void Add(long stopwatchTicks, long allocations)
            {
                if (Count == 0) GcBaseline = GC.CollectionCount(0);
                int bin = Math.Min(_histogram.Length - 1, (int)(stopwatchTicks * 1000000L / Stopwatch.Frequency));
                _histogram[bin]++; Count++; Allocations += allocations;
            }
            public double Percentile(int percentile)
            {
                int threshold = (Count * percentile + 99) / 100, cumulative = 0;
                for (int bin = 0; bin < _histogram.Length; bin++)
                { cumulative += _histogram[bin]; if (cumulative >= threshold) return bin / 1000d; }
                return 100;
            }
        }
    }
}
