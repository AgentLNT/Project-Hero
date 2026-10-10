// Managed storage diagnostic using the existing Task09A2 fixture. Not a Development Build performance gate.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Actions;

public static class WindowHistoryBenchmark
{
    private sealed class Windows : ITurnWindowSchedule
    {
        public WindowOpenRequest TryOpenDue(long tick) => tick % 60 == 0 ? new WindowOpenRequest(new UnitId(1), 400) : null;
        public bool ShouldCloseCurrentWindow(long tick) => tick % 60 == 59;
    }
    private sealed class Samples
    {
        public readonly int[] Step = new int[100001], Hash = new int[100001];
        public int Count, GcStart;
        public long Bytes;
        public void Add(long step, long hash, long bytes)
        {
            Step[Math.Min(100000, (int)(step * 1000000L / Stopwatch.Frequency))]++;
            Hash[Math.Min(100000, (int)(hash * 1000000L / Stopwatch.Frequency))]++;
            Count++; Bytes += bytes;
        }
        public double Percentile(int[] histogram, int percent)
        {
            int needed = (Count * percent + 99) / 100, sum = 0;
            for (int i = 0; i < histogram.Length; i++) { sum += histogram[i]; if (sum >= needed) return i / 1000d; }
            return 100;
        }
    }
    public static int Main(string[] args)
    {
        var fixture = Assembly.LoadFrom(args[0]).GetType("ProjectHero.Logic.Tests.Task09A2Fixture", true);
        var builder = fixture.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "BuildDefinition" && m.GetParameters().Length == 3);
        var definition = (BattleDefinition)builder.Invoke(null, new object[] { CommandSourceKind.Player, CommandSourceKind.Ai, false });
        var encounter = (EncounterDefinitionId)fixture.GetProperty("EncounterId", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        var inputs = (BattleRuntimeInputs)fixture.GetProperty("Inputs", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        bool reactionLoad = args.Length > 3 && args[3] == "reactions";
        if (reactionLoad)
            definition = definition with { Actions = definition.Actions.Select(a => a.ActionSpecId.Value == "action.t09a2.attack_allied"
                ? a with { Payload = ((AttackPayloadSpec)a.Payload) with { Tags = AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable } }
                : a).ToArray() };
        long probeStart = GC.GetAllocatedBytesForCurrentThread(); var probe = new byte[4096];
        long probeBytes = GC.GetAllocatedBytesForCurrentThread() - probeStart; GC.KeepAlive(probe);
        bool allocationsAvailable = probeBytes >= 4096;
        var csv = new StringBuilder("environment,simulated_minute,units,active_plans,reservations,step_p50_ms,step_p95_ms,step_p99_ms,hash_p50_ms,hash_p95_ms,hash_p99_ms,allocated_step_bytes,allocation_probe_bytes,gc0_in_1800_tick_sample,managed_bytes,history_records,history_payload_bytes,old_reads_between_diagnostics,old_copies_between_diagnostics,old_hashes_between_diagnostics,active_windows,active_opportunities,peak_active_opportunities\n");
        var endpoints = new[] { 3600, 36000, 108000 }; var samples = new[] { new Samples(), new Samples(), new Samples() };
        bool terminalLoad = reactionLoad || args.Length > 3 && args[3] == "terminal";
        int peakOpportunities = 0;
        var phaseTiming = args.Length > 2 && args[2] != "no-phases" ? new StepPhaseTimingRecorder() : null;
        using (var sim = BattleSimulation.Create(definition, encounter, inputs, new BattleSimulationAssembly(turnWindowSchedule: new Windows(), phaseTiming: phaseTiming)))
        {
            var baseline = sim.HistoryAccessCounters;
            for (int tick = 0; tick < 108000; tick++)
            {
                // One naturally completed ordinary plan per window. The far allied target is outside
                // the attack footprint: this measures non-empty terminal storage, not contact resolution.
                if (terminalLoad && tick % 60 == 1)
                {
                    var issuer = definition.FindEncounter(encounter).Controllers.Single(c => c.SourceKind == CommandSourceKind.Player).ControllerId;
                    var request = new CommandRequest(tick, new ScheduleEditScope(sim.ScheduleRevision, sim.CurrentTurnWindow.WindowId),
                        new ScheduleEditPayload(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, new UnitId(1),
                            new ActionSpecId((string)fixture.GetField("AlliedAttackSpecId").GetRawConstantValue()), tick,
                            PrimaryTargetUnitId: new UnitId(4)) }));
                    var rejection = sim.CommandIngress.FindEntry(issuer).Submit(request);
                    if (rejection != null) throw new InvalidOperationException(rejection.ReasonCode);
                }
                int window = Array.FindIndex(endpoints, e => tick >= e - 1800 && tick < e);
                bool measure = window >= 0;
                if (measure && samples[window].Count == 0) samples[window].GcStart = GC.CollectionCount(0);
                long allocated = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
                long start = measure ? Stopwatch.GetTimestamp() : 0;
                var result = sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                peakOpportunities = Math.Max(peakOpportunities, result.Snapshot.ReactionOpportunities.Count);
                long step = measure ? Stopwatch.GetTimestamp() - start : 0;
                long bytes = measure ? GC.GetAllocatedBytesForCurrentThread() - allocated : 0;
                start = measure ? Stopwatch.GetTimestamp() : 0; result.Snapshot.ComputeHash();
                long hash = measure ? Stopwatch.GetTimestamp() - start : 0;
                if (measure) samples[window].Add(step, hash, bytes);
                if (sim.IsEnded) throw new InvalidOperationException("Fixture unexpectedly ended");
                int endpoint = Array.IndexOf(endpoints, tick + 1);
                if (endpoint < 0) continue;
                var sample = samples[endpoint]; var access = sim.HistoryAccessCounters;
                // Explicit diagnostic reads happen after the measured Tick window. Exclude them from hot-path counters.
                long payloadBytes = sim.ReadHistoryForDiagnostics().Sum(r => (long)r.PayloadLength);
                var actual = sim.History;
                if (sim.RecomputeHistoryDigestForDiagnostics().ToString("x16") != actual.Digest)
                    throw new InvalidOperationException("Incremental archive digest differs from independent full recomputation");
                if (actual.RecordCount != (tick + 1) / 60 * (reactionLoad ? 3 : terminalLoad ? 2 : 1)) throw new InvalidOperationException("Lost or repeated frozen history record");
                if (terminalLoad && sim.CurrentSnapshot.TerminalPlanRecordCount != (tick + 1) / 60)
                    throw new InvalidOperationException("Terminal plans did not naturally complete as configured");
                csv.AppendLine(string.Join(",", reactionLoad ? "Managed Mono fixture; reactable allied no-contact Attack and expired opportunity per 60 ticks; no Unity player" : terminalLoad ? "Managed Mono fixture; one terminal no-contact Attack per 60 ticks; no Unity player" : "Managed Mono fixture; no Unity player", (tick + 1) / 3600,
                    result.Snapshot.Units.Count, result.Snapshot.Plans.Count, result.Snapshot.Reservations.Count,
                    Format(sample.Percentile(sample.Step, 50)), Format(sample.Percentile(sample.Step, 95)), Format(sample.Percentile(sample.Step, 99)),
                    Format(sample.Percentile(sample.Hash, 50)), Format(sample.Percentile(sample.Hash, 95)), Format(sample.Percentile(sample.Hash, 99)),
                    allocationsAvailable ? (sample.Bytes / sample.Count).ToString(CultureInfo.InvariantCulture) : "unavailable", probeBytes,
                    GC.CollectionCount(0) - sample.GcStart, GC.GetTotalMemory(false), actual.RecordCount, payloadBytes,
                    access.OldRecordReads - baseline.OldRecordReads, access.OldRecordCopies - baseline.OldRecordCopies,
                    access.OldRecordHashes - baseline.OldRecordHashes, result.Snapshot.WindowManager.Windows.Count,
                    result.Snapshot.ReactionOpportunities.Count, peakOpportunities));
                baseline = sim.HistoryAccessCounters;
            }
        }
        if (phaseTiming != null)
        {
            var phases = new StringBuilder("phase,invocations,mean_stopwatch_ticks,mean_allocated_bytes\n");
            foreach (var phase in phaseTiming.Snapshot()) phases.AppendLine(string.Join(",", phase.Phase, phase.Invocations,
                phase.MeanTicks.ToString("F3", CultureInfo.InvariantCulture), (phase.Invocations == 0 ? 0 : phase.TotalAllocatedBytes / phase.Invocations)));
            File.WriteAllText(args[2], phases.ToString(), new UTF8Encoding(false));
        }
        File.WriteAllText(args[1], csv.ToString(), new UTF8Encoding(false)); Console.WriteLine(csv); return 0;
    }
    private static string Format(double value) => value.ToString("F6", CultureInfo.InvariantCulture);
}
