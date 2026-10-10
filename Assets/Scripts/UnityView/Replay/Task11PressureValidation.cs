#if DEVELOPMENT_BUILD && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using Unity.Profiling;

namespace ProjectHero.UnityView.Replay
{
    /// <summary>Explicit Development Player future-state pressure fixture. No normal-session entry point.</summary>
    public static class Task11PressureValidation
    {
        private sealed class AllocationMeter : IDisposable
        {
            private ProfilerRecorder _recorder;
            private bool _counter, _byteCounter; private long _before;
            public string Source => _counter ? "GC Allocated In Frame current-value delta; unit=" + Unit : "GC.Alloc current-thread samples; unit=" + Unit;
            public string Unit => _recorder.Valid ? _recorder.UnitType.ToString() : "Unavailable";
            public long CurrentBytes() => _byteCounter ? _recorder.CurrentValue : 0;
            public AllocationMeter()
            {
                _recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 16384,
                    ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                if (_recorder.Valid && Unit != "Bytes")
                {
                    _recorder.Dispose(); _counter = true;
                    _recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
                }
                if (_recorder.Valid && !_counter) _recorder.Stop();
                _byteCounter = _counter && _recorder.Valid && Unit == "Bytes";
            }
            public void Begin()
            {
                if (!_recorder.Valid) return;
                if (_counter) _before = _recorder.CurrentValue;
                else { _recorder.Reset(); _recorder.Start(); }
            }
            public long End()
            {
                if (!_recorder.Valid) return -1;
                if (_counter) return _recorder.CurrentValue - _before;
                _recorder.Stop();
                Require(_recorder.Count < _recorder.Capacity, "ALLOCATION_RECORDER_CAPACITY_EXCEEDED");
                long bytes = 0;
                for (int i = 0; i < _recorder.Count; i++) bytes += _recorder.GetSample(i).Value;
                return bytes;
            }
            public void Dispose() => _recorder.Dispose();
        }
        private sealed class Sample
        {
            public readonly long[] Step = new long[1800], Hash = new long[1800];
            public int Count, GcStart; public long Bytes;
            public double Percentile(long[] values, int percent)
            { Array.Sort(values, 0, Count); return values[(Count * percent + 99) / 100 - 1] * 1000d / Stopwatch.Frequency; }
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        public static void Run(BattleSimulationSeed seed, StringBuilder report, bool quick)
        {
            bool profilerAlloc = Environment.GetCommandLineArgs().Contains("-heroPressureAlloc");
            report.AppendLine("Unity=" + UnityEngine.Application.unityVersion + "; CPU=" + UnityEngine.SystemInfo.processorType
                + "; MemoryMB=" + UnityEngine.SystemInfo.systemMemorySize + "; allocation recorder=" + profilerAlloc);
            var csv = new StringBuilder("runtime,load,simulated_minute,units,active_plans,reservations,step_p50_ms,step_p95_ms,step_p99_ms,hash_p50_ms,hash_p95_ms,hash_p99_ms,allocated_step_bytes,allocation_probe_bytes,allocation_empty_probe_bytes,allocation_source,gc0_in_burst_sample,managed_bytes,history_records,history_payload_bytes,old_reads,old_copies,old_hashes,peak_schedule_operations,rebase_operations,wall_seconds\n");
            RunLoad(seed, 2, 6, 3, quick, profilerAlloc, csv, report);
            RunLoad(seed, 8, 8, 4, quick, profilerAlloc, csv, report);
            File.WriteAllText(Path.GetFullPath("优化任务/执行记录/11-development-future-pressure" + (profilerAlloc ? "-allocation" : "") + ".csv"), csv.ToString(), new UTF8Encoding(false));
            report.AppendLine("Limits: derived future-plan fixture; full canonical bodies, formal 180 Tick budget, real Player ingress and ProductionBattleComposition."
                + " No fabricated running plans or reservations. All ordinary plans remain Editable through explicit schedule rebase."
                + " This measures future-state/schedule pressure and window archive growth; no contact graph, forced displacement, AI reactions,"
                + " real-time 60Hz GC frequency or GC pauses. Allocation is unavailable if its probe fails. It cannot authorize final cutover.");
        }

        private static void RunLoad(BattleSimulationSeed seed, int units, int plansPerUnit, int movesPerUnit,
            bool quick, bool profilerAlloc, StringBuilder csv, StringBuilder report)
        {
            var basis = seed.Definition.FindEncounter(seed.EncounterId);
            var hero = basis.Slots.Single(s => s.SlotId == basis.TurnSubmission.ConcurrentHeroSlot);
            var slots = new List<EncounterUnitSlot>(); var controllers = new List<ControllerBinding>();
            for (int i = 0; i < units; i++)
            {
                var id = new EncounterSlotId("pressure.unit_" + i);
                var position = new GridPoint(-12 + i % 4 * 8, -8 + i / 4 * 16);
                slots.Add(new EncounterUnitSlot(id, hero.DefinitionId, basis.Slots[i % basis.Slots.Count].FactionId, position, GridDirection.East));
                controllers.Add(new ControllerBinding(new ControllerId("controller.pressure_" + i), CommandSourceKind.Player, new[] { id }));
            }
            var encounter = new EncounterDefinition(new EncounterDefinitionId("encounter.pressure_" + units),
                basis.GridBoundary, slots.AsReadOnly(), controllers.AsReadOnly(), basis.Victory,
                new TurnSubmissionDefinition(180, slots[0].SlotId));
            // This diagnostic has its own explicit compatibility identity. It does not impersonate
            // the audited main Encounter or overwrite its Builder-produced hash.
            var writer = new CanonicalHashWriter().Write("pressure.fixture_version", 1).Write("pressure.basis_hash", seed.BattleDefinitionHash);
            encounter.WriteHashComponents(writer);
            var definition = seed.Definition with { Encounters = new[] { encounter }, BattleDefinitionHashValue = writer.ToDigestHex() };
            var actionSet = definition.ActionSets.Single(s => s.ActionSetId == definition.FindUnit(hero.DefinitionId).ActionSetId);
            var actions = definition.Actions.Where(s => actionSet.ActionSpecIds.Contains(s.ActionSpecId)).ToArray();
            var guard = actions.Single(s => s.Type == ActionType.Guard).ActionSpecId;
            var move = actions.Single(s => s.Type == ActionType.Move).ActionSpecId;
            var initial = BattleInitializer.BuildInitialState(definition, encounter.EncounterId, seed.RuntimeInputs);
            var control = controllers.ToDictionary(c => initial.SlotToUnitId[c.ControlledSlots[0]].Value, c => c.ControllerId);
            var origin = slots.ToDictionary(s => initial.SlotToUnitId[s.SlotId].Value, s => s.InitialPosition);
            int total = quick ? 3600 : 108000; var endpoints = quick ? new[] { 3600 } : new[] { 3600, 36000, 108000 };
            var samples = endpoints.Select(_ => new Sample()).ToArray();
            int peakOperations = 0, rebases = 0; long lastEditedWindow = -1;
            report.AppendLine("Future pressure load=" + units + "/" + units * plansPerUnit + "/" + units * movesPerUnit
                + "; basis=" + seed.BattleDefinitionHash + "; fixture=" + definition.BattleDefinitionHashValue);
            using (var meter = profilerAlloc ? new AllocationMeter() : null)
            {
                var phaseTiming = meter == null ? null : new StepPhaseTimingRecorder(meter.CurrentBytes);
                using (var sim = ProductionBattleComposition.Create(definition, encounter.EncounterId, seed.RuntimeInputs, phaseTiming))
                {
                meter?.Begin(); long probeStart = GC.GetAllocatedBytesForCurrentThread(); var probe = new byte[4096];
                long probeBytes = meter == null ? GC.GetAllocatedBytesForCurrentThread() - probeStart : meter.End(); GC.KeepAlive(probe);
                meter?.Begin(); long emptyStart = GC.GetAllocatedBytesForCurrentThread();
                long emptyBytes = meter == null ? GC.GetAllocatedBytesForCurrentThread() - emptyStart : meter.End();
                bool allocationAvailable = probeBytes >= 4096 && emptyBytes == 0 && (meter == null || meter.Unit == "Bytes");
                report.AppendLine("Allocation source=" + (meter?.Source ?? "GC.GetAllocatedBytesForCurrentThread")
                    + "; probes: known4096=" + probeBytes + "; empty=" + emptyBytes + "; available=" + allocationAvailable);
                var accessBaseline = sim.HistoryAccessCounters; var wall = Stopwatch.StartNew();
                for (int tick = 0; tick < total; tick++)
                {
                    var window = sim.CurrentTurnWindow;
                    if (window != null)
                    {
                        var own = sim.CurrentSnapshot.Plans.Where(p => p.OwnerUnitId == window.OwnerUnitId.Value).OrderBy(p => p.StartTick).ToArray();
                        var entry = sim.CommandIngress.FindEntry(control[window.OwnerUnitId.Value]);
                        var operations = new List<ScheduleEditOperation>();
                        if (lastEditedWindow != window.WindowId.Value && own.Length < plansPerUnit)
                        {
                            int guards = plansPerUnit - movesPerUnit;
                            int guardsToAdd = Math.Min(2, guards - own.Length);
                            for (int i = 0; i < guardsToAdd; i++)
                                operations.Add(new AddOrdinaryPlanOperation(i + 1, window.OwnerUnitId, guard, 30000 + (own.Length + i) * 90));
                            if (guardsToAdd <= 1)
                            {
                                var start = origin[window.OwnerUnitId.Value];
                                for (int i = 0; i < movesPerUnit; i++)
                                    operations.Add(new AddOrdinaryPlanOperation(guardsToAdd + i + 1, window.OwnerUnitId, move,
                                        30000 + guards * 90 + i * 25, Destination: i % 2 == 0 ? start.Translate(2, 0) : start));
                            }
                        }
                        else if (own.Length == plansPerUnit && own[0].StartTick < tick + 8000)
                        {
                            long shift = tick + 30000 - own[0].StartTick;
                            foreach (var plan in own) operations.Add(new MoveEditablePlanOperation(new ActionPlanId(plan.ActionPlanId), plan.StartTick + shift));
                            rebases += operations.Count;
                        }
                        if (operations.Count > 0)
                        {
                            Require(entry.Submit(new CommandRequest(tick, new ScheduleEditScope(sim.ScheduleRevision, window.WindowId),
                                new ScheduleEditPayload(operations.AsReadOnly()))) == null, "PRESSURE_INPUT_REJECTED");
                            peakOperations = Math.Max(peakOperations, operations.Count); lastEditedWindow = window.WindowId.Value;
                        }
                        else if (tick % 60 == 59 || own.Length < plansPerUnit && lastEditedWindow == window.WindowId.Value)
                            Require(entry.Submit(new CommandRequest(tick, new WindowCommandScope(window.WindowId),
                                new WindowCommandPayload(WindowCommandKind.CloseOwnWindow))) == null, "PRESSURE_CLOSE_REJECTED");
                    }
                    int sampleIndex = Array.FindIndex(endpoints, endpoint => tick >= endpoint - 1800 && tick < endpoint);
                    var sample = sampleIndex < 0 ? null : samples[sampleIndex];
                    if (sample?.Count == 0) { sample.GcStart = GC.CollectionCount(0); phaseTiming?.Reset(); }
                    if (sample != null) meter?.Begin();
                    long allocated = sample == null ? 0 : GC.GetAllocatedBytesForCurrentThread();
                    long time = sample == null ? 0 : Stopwatch.GetTimestamp();
                    var result = sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                    long stepTime = sample == null ? 0 : Stopwatch.GetTimestamp() - time;
                    long bytes = sample == null ? 0 : meter == null ? GC.GetAllocatedBytesForCurrentThread() - allocated : meter.End();
                    Require(!sim.IsEnded, "PRESSURE_FIXTURE_ENDED");
                    foreach (var error in result.Events.Events.OfType<ProjectHero.Logic.Events.CommandRejectedEvent>())
                        throw new InvalidOperationException("PRESSURE_TRANSACTION_REJECTED:" + error.ReasonCode + ":tick=" + tick);
                    if (sample != null)
                    {
                        Require(result.Snapshot.Units.Count == units && result.Snapshot.Plans.Count == units * plansPerUnit
                            && result.Snapshot.Reservations.Count == units * movesPerUnit,
                            "PRESSURE_SCALE_CHANGED:" + result.Snapshot.Plans.Count + "/" + result.Snapshot.Reservations.Count + ":tick=" + tick);
                        Require(result.Snapshot.Plans.All(p => p.State == (int)ActionPlanState.Editable), "PRESSURE_PLAN_UNEXPECTEDLY_STARTED");
                        time = Stopwatch.GetTimestamp(); result.Snapshot.ComputeHash();
                        sample.Step[sample.Count] = stepTime; sample.Hash[sample.Count++] = Stopwatch.GetTimestamp() - time; sample.Bytes += bytes;
                    }
                    int endpointIndex = Array.IndexOf(endpoints, tick + 1); if (endpointIndex < 0) continue;
                    var access = sim.HistoryAccessCounters;
                    Require(access.OldRecordReads == accessBaseline.OldRecordReads && access.OldRecordCopies == accessBaseline.OldRecordCopies
                        && access.OldRecordHashes == accessBaseline.OldRecordHashes, "PRESSURE_OLD_HISTORY_HOT_ACCESS");
                    long payloadBytes = sim.ReadHistoryForDiagnostics().Sum(r => (long)r.PayloadLength);
                    Require(sim.History.RecordCount > 0 && sim.RecomputeHistoryDigestForDiagnostics().ToString("x16") == sim.History.Digest,
                        "PRESSURE_HISTORY_DIGEST_MISMATCH");
                    csv.AppendLine(string.Join(",", "Windows Development IL2CPP", units + "/" + units * plansPerUnit + "/" + units * movesPerUnit,
                        (tick + 1) / 3600, units, result.Snapshot.Plans.Count, result.Snapshot.Reservations.Count,
                        F(sample.Percentile(sample.Step, 50)), F(sample.Percentile(sample.Step, 95)), F(sample.Percentile(sample.Step, 99)),
                        F(sample.Percentile(sample.Hash, 50)), F(sample.Percentile(sample.Hash, 95)), F(sample.Percentile(sample.Hash, 99)),
                        allocationAvailable ? (sample.Bytes / sample.Count).ToString(CultureInfo.InvariantCulture) : "unavailable", probeBytes, emptyBytes,
                        profilerAlloc ? meter.Source + "; instrumented timing" : "GC.GetAllocatedBytesForCurrentThread; uninstrumented timing",
                        GC.CollectionCount(0) - sample.GcStart, UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(), sim.History.RecordCount,
                        payloadBytes, 0, 0, 0, peakOperations, rebases, F(wall.Elapsed.TotalSeconds)));
                    report.AppendLine("Verified future scale " + units + "/" + units * plansPerUnit + "/" + units * movesPerUnit
                        + " at simulated minute " + (tick + 1) / 3600 + "; history=" + sim.History.RecordCount + "; independent digest matched.");
                    if (phaseTiming != null)
                    {
                        var phases = new StringBuilder("phase,invocations,mean_stopwatch_ticks,mean_allocated_bytes\n");
                        foreach (var phase in phaseTiming.Snapshot()) phases.AppendLine(string.Join(",", phase.Phase, phase.Invocations,
                            F(phase.MeanTicks), phase.Invocations == 0 ? 0 : phase.TotalAllocatedBytes / phase.Invocations));
                        File.WriteAllText(Path.GetFullPath("优化任务/执行记录/11-development-future-phase-" + units + ".csv"), phases.ToString(), new UTF8Encoding(false));
                    }
                    accessBaseline = sim.HistoryAccessCounters;
                }
                }
            }
        }
        private static string F(double value) => value.ToString("F6", CultureInfo.InvariantCulture);
    }
}
#endif
