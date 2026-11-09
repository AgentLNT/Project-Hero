using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProjectHero.Authoring;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Units;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ProjectHero.Editor
{
    public static class LongActionForecastTools
    {
        public const string Root = "优化任务/执行记录/长动作预演覆盖-2026-11-05";
        [Serializable] public sealed class Input
        {
            public string version = AiForecastSettings.Version, sourceHash, definitionHash, sourceFingerprint, contentFingerprint;
            public int horizon, policy, maxTicks = 900, metaResource;
            public long seed;
            public bool near, mirror;
            public string Id => ((AiPolicyKind)policy) + "-" + (near ? "near" : "main") + "-mirror" + (mirror ? 1 : 0) + "-h" + horizon;
        }
        static BattleSimulationSeed Source()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/CombatSampleScene.unity", OpenSceneMode.Single);
            var host = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<NewBattleSimulationSource>(true)).Single();
            return host.BuildSeed() ?? throw new InvalidOperationException(host.LastConfigurationError);
        }
        static BattleDefinition Build(BattleSimulationSeed source, Input input) => FormalAiBaseline.Build(source.Definition,
            (AiPolicyKind)input.policy, AiExecutionKind.UtilityBehaviorTree, input.near, input.mirror, new AiForecastSettings(input.horizon));
        static IEnumerable<Input> Matrix(BattleSimulationSeed source, string code, string content)
        {
            foreach (int horizon in new[] { 120, 180, 240, 360 })
            foreach (var policy in new[] { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense })
            foreach (bool near in new[] { false, true }) foreach (bool mirror in new[] { false, true })
                yield return new Input { horizon = horizon, policy = (int)policy, near = near, mirror = mirror,
                    seed = unchecked((long)source.RuntimeInputs.InitialRngSeed), metaResource = source.RuntimeInputs.InitialMetaResource,
                    sourceHash = source.BattleDefinitionHash, sourceFingerprint = code, contentFingerprint = content };
        }
        public static void Run()
        {
            Directory.CreateDirectory(Root + "/cases"); var source = Source();
            string code = AiPolicyExperimentTools.SourceFingerprint(), content = FormalAiBaselineTools.ContentFingerprint();
            File.WriteAllText(Root + "/conditional-probe.csv", Probe(source), Encoding.UTF8);
            using var summary = new StreamWriter(Root + "/summary.csv", false, Encoding.UTF8);
            summary.WriteLine("case,horizon,policy,near,mirror,ticks,ended,outcome,health,commands,rejections,attackStarts,moves,reactions,guardStarts,controlTicks,healthLossQ10,resourceSpent,candidateRows,uncovered,covered,rejectedCandidates,maxCandidates,elapsedMs,definitionHash");
            Parallel.ForEach(Matrix(source, code, content), new ParallelOptions { MaxDegreeOfParallelism = 4 }, input =>
            {
                var clock = Stopwatch.StartNew(); var d = Build(source, input); input.definitionHash = d.BattleDefinitionHashValue;
                var runtime = new BattleRuntimeInputs(unchecked((ulong)input.seed), input.metaResource);
                var composition = new ProductionBattleComposition(d, source.EncounterId, runtime);
                using var sim = BattleSimulation.Create(d, source.EncounterId, runtime, composition.Assembly); composition.Attach(sim);
                var recorder = new ReplayRecorder(sim); string prefix = Root + "/cases/" + input.Id;
                using var candidates = Gzip(prefix + ".candidates.csv.gz");
                using var decisions = Gzip(prefix + ".decisions.csv.gz");
                using var events = Gzip(prefix + ".events.txt.gz");
                candidates.WriteLine("tick,controller,key,type,reaction,chosen,score,rejection,reason,coverage,damageQ10,incomingQ10,progress,interrupts,clashes,cost,start,impact,end,horizon,observed,required");
                decisions.WriteLine("tick,controller,selected,wait,ordinaryNode,ordinaryPlan,ordinaryStatus,ordinaryReason,reactionNode,reactionPlan,reactionStatus,reactionReason");
                var plans = new SortedDictionary<long, PlanTrace>();
                int commands = 0, rejects = 0, attacks = 0, moves = 0, reactions = 0, guards = 0, maxCandidates = 0;
                long control = 0, healthLoss = 0, spent = 0, candidateRows = 0, uncovered = 0, covered = 0, candidateRejected = 0;
                for (long tick = 0; tick < input.maxTicks && !sim.IsEnded; tick++)
                {
                    var before = sim.CurrentSnapshot;
                    recorder.CaptureBeforeStep(); var step = sim.Step(tick, sim.CommandIngress.FreezeTick(tick)); recorder.RecordCommittedStep(step);
                    commands += sim.LastCommandSet.Envelopes.Count;
                    healthLoss += step.Snapshot.Units.Sum(u => Math.Max(0L, before.Units.Single(b => b.UnitId == u.UnitId).HealthQ10 - u.HealthQ10));
                    control += step.Snapshot.Units.Count(u => u.State == (int)UnitState.Staggered || u.State == (int)UnitState.KnockedDown || u.State == (int)UnitState.Recovering);
                    foreach (var fact in step.Events.Events)
                    {
                        events.WriteLine(ReplayEventComparison.Canonical(fact));
                        if (fact is CommandRejectedEvent || fact is CommandIngressRejectedEvent) rejects++;
                        if (fact is AdrenalineLedgerChangedEvent ledger && ledger.ChangeKind == AdrenalineChangeKind.Consumed)
                            spent += ledger.ReservationAmount;
                        if (fact is ActionPlanCreatedEvent created)
                        {
                            plans.Add(created.ActionPlanId, new PlanTrace(created));
                            if (created.Origin == (int)ActionPlanOrigin.Reaction) reactions++;
                        }
                        if (fact is ActionPlanLockedEvent started)
                        {
                            var plan = sim.AiActionPlanLookup.FindPlan(new ActionPlanId(started.ActionPlanId));
                            if (plan.ActionType == ActionType.Attack) attacks++;
                            if (plan.ActionType == ActionType.Move) moves++;
                            if (plan.ActionType == ActionType.Guard) guards++;
                        }
                        if (fact is DamageChannelResolvedEvent damage && plans.TryGetValue(damage.AttackPlanId.Value, out var trace))
                            trace.ResolvedDamage += damage.AfterBlockDamageQ10;
                    }
                    foreach (var ai in composition.AiControllers)
                    {
                        string controller = ai.RegisteredControllerIds[0]; var decision = ai.LastDecisionOf(controller);
                        if (decision?.DecisionTick != tick) continue;
                        var o = decision.Behavior.Ordinary; var r = decision.Behavior.Reaction;
                        decisions.WriteLine(Csv(tick, controller, decision.HasSelection, decision.NoSelectionReason,
                            o.Cursor, o.PlanId, o.Status, o.Reason, r.Cursor, r.PlanId, r.Status, r.Reason));
                        maxCandidates = Math.Max(maxCandidates, decision.Evaluations.Count);
                        foreach (var e in decision.Evaluations)
                        {
                            candidateRows++; if (e.Coverage == AiForecastCoverage.Uncovered) uncovered++;
                            if (e.Coverage == AiForecastCoverage.Complete) covered++;
                            if (e.RejectionCode != null) candidateRejected++;
                            if (e.Chosen && (e.Coverage != AiForecastCoverage.Complete || !e.Score.HasValue)) throw new InvalidOperationException("UNCOVERED_CANDIDATE_SELECTED");
                            candidates.WriteLine(Csv(tick, controller, e.Key, e.Type, e.IsReaction, e.Chosen, e.Score, e.RejectionCode, e.Reason,
                                e.Coverage, e.DamageDeltaQ10, e.IncomingDeltaQ10, e.PositionProgress, e.InterruptedThreats, e.Clashes, e.Cost,
                                e.StartTick, e.ImpactTick, e.EndTick, e.HorizonTick, e.ObservedThroughTick, e.RequiredThroughTick));
                        }
                    }
                }
                using (var output = new StreamWriter(prefix + ".plans.csv", false, Encoding.UTF8))
                {
                    output.WriteLine("plan,owner,action,type,start,impact,end,state,terminalReason,terminalTick,resolvedDamageAfterBlockQ10");
                    foreach (var trace in plans.Values)
                    {
                        var p = sim.AiActionPlanLookup.FindPlan(new ActionPlanId(trace.Id));
                        output.WriteLine(Csv(trace.Id, p.OwnerUnitId.Value, p.ActionSpecId.Value, p.ActionType, p.StartTick, p.ImpactTick,
                            p.EndTick, p.State, p.TerminationReason, p.TerminalTick, trace.ResolvedDamage));
                    }
                }
                var replay = recorder.BuildReplay(); if (replay.Submissions.Count != 0) throw new InvalidOperationException("EXTERNAL_INPUT_IN_AI_EXPERIMENT");
                File.WriteAllText(prefix + ".input.json", JsonUtility.ToJson(input, true), Encoding.UTF8);
                using (var file = File.Create(prefix + ".heroReplay")) ReplayFile.Save(file, replay);
                lock (summary)
                {
                    summary.WriteLine(Csv(input.Id, input.horizon, (AiPolicyKind)input.policy, input.near, input.mirror, replay.Records.Count,
                        sim.IsEnded, sim.CurrentSnapshot.BattleEnd.ResultCode, string.Join(";", sim.CurrentSnapshot.Units.Select(u => u.UnitId + ":" + u.HealthQ10)),
                        commands, rejects, attacks, moves, reactions, guards, control, healthLoss, spent, candidateRows, uncovered, covered, candidateRejected,
                        maxCandidates, clock.ElapsedMilliseconds, d.BattleDefinitionHashValue)); summary.Flush();
                }
                Debug.Log("FORECAST_CASE " + input.Id + " ticks=" + replay.Records.Count);
            });
        }
        sealed class PlanTrace
        {
            public readonly long Id; public readonly string Action; public readonly bool IsReaction; public long ResolvedDamage;
            public PlanTrace(ActionPlanCreatedEvent created) { Id = created.ActionPlanId; Action = created.ActionSpecId; IsReaction = created.Origin == (int)ActionPlanOrigin.Reaction; }
        }
        static string Probe(BattleSimulationSeed source)
        {
            var lines = new List<string> { "near,mirror,horizon,key,coverage,start,impact,end,observed,required,damageQ10,score,conditionalActualMatches" };
            foreach (bool near in new[] { false, true }) foreach (bool mirror in new[] { false, true })
            foreach (int horizon in new[] { 120, 180, 240, 360 })
            {
                var input = new Input { policy = (int)AiPolicyKind.SustainedOffense, horizon = horizon, near = near, mirror = mirror };
                var d = Build(source, input); var composition = new ProductionBattleComposition(d, source.EncounterId, source.RuntimeInputs);
                using var sim = BattleSimulation.Create(d, source.EncounterId, source.RuntimeInputs, new BattleSimulationAssembly(turnWindowSchedule: composition));
                composition.Attach(sim); sim.Step(0, sim.CommandIngress.FreezeTick(0));
                var ai = composition.AiControllers.Single(a => a.BuildContext(sim.DecisionSnapshotFor(new ControllerId(a.RegisteredControllerIds[0])),
                    new ControllerId(a.RegisteredControllerIds[0])).HasSubmissionWindow);
                var controller = new ControllerId(ai.RegisteredControllerIds[0]); var context = ai.BuildContext(sim.DecisionSnapshotFor(controller), controller);
                foreach (var candidate in AiTacticalCandidateBuilder.Ordinary(context).Where(c => c.Type == ActionType.Attack))
                {
                    using var forecast = context.ForecastFactory(context.Snapshot);
                    var baseline = forecast.Evaluate(null, context.ForecastSettings); var outcome = forecast.Evaluate(candidate.Request, context.ForecastSettings);
                    var evaluation = AiTacticalPolicy.Evaluate(context, AiPolicyKind.SustainedOffense, candidate, baseline, outcome);
                    bool? matches = null;
                    if (horizon == 360)
                    {
                        var actualComposition = new ProductionBattleComposition(d, source.EncounterId, source.RuntimeInputs);
                        using var observed = BattleSimulation.Create(d, source.EncounterId, source.RuntimeInputs,
                            new BattleSimulationAssembly(turnWindowSchedule: actualComposition)); actualComposition.Attach(observed);
                        observed.Step(0, observed.CommandIngress.FreezeTick(0));
                        if (observed.CommandIngress.FindEntry(controller).Submit(candidate.Request) != null) throw new InvalidOperationException("PROBE_INGRESS_FAILED");
                        for (long tick = 1; tick <= horizon && !observed.IsEnded; tick++) observed.Step(tick, observed.CommandIngress.FreezeTick(tick));
                        matches = outcome.Units.Select(u => (u.UnitId, u.HealthQ10, u.X, u.Y, u.State)).SequenceEqual(
                            observed.CurrentSnapshot.Units.Select(u => (u.UnitId, u.HealthQ10, u.X, u.Y, u.State)));
                        if (matches != true) throw new InvalidOperationException("CONDITIONAL_FORECAST_MISMATCH");
                    }
                    lines.Add(Csv(near, mirror, horizon, candidate.Key, outcome.Coverage, outcome.PlanStartTick, outcome.PlanImpactTick,
                        outcome.PlanEndTick, outcome.ObservedThroughTick, outcome.RequiredThroughTick, evaluation.DamageDeltaQ10, evaluation.Score, matches));
                }
            }
            return string.Join("\n", lines) + "\n";
        }
        public static void RunProbe()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Root + "/conditional-probe.csv", Probe(Source()), Encoding.UTF8);
        }
        public static void Audit()
        {
            var source = Source(); string code = AiPolicyExperimentTools.SourceFingerprint(), content = FormalAiBaselineTools.ContentFingerprint();
            int count = 0; long ticks = 0;
            Parallel.ForEach(Directory.GetFiles(Root + "/cases", "*.input.json"), new ParallelOptions { MaxDegreeOfParallelism = 4 }, file =>
            {
                var input = JsonUtility.FromJson<Input>(File.ReadAllText(file));
                if (input.version != AiForecastSettings.Version || input.sourceHash != source.BattleDefinitionHash || input.sourceFingerprint != code || input.contentFingerprint != content)
                    throw new InvalidOperationException("FORECAST_INPUT_CHANGED");
                var d = Build(source, input); if (d.BattleDefinitionHashValue != input.definitionHash) throw new InvalidOperationException("FORECAST_DEFINITION_CHANGED");
                using var stream = File.OpenRead(file.Replace(".input.json", ".heroReplay")); var replay = ReplayFile.Load(stream, d); Verify(d, replay);
                Interlocked.Increment(ref count); Interlocked.Add(ref ticks, replay.Records.Count); Debug.Log("FORECAST_AUDIT " + input.Id);
            });
            if (count != 48) throw new InvalidOperationException("FORECAST_CASE_COUNT");
            if (File.ReadAllText(Root + "/conditional-probe.csv", Encoding.UTF8) != Probe(source)) throw new InvalidOperationException("PROBE_REBUILD_MISMATCH");
            File.WriteAllText(Root + "/native-disk-audit.txt", "Passed; cases=" + count + "; ticks=" + ticks + "; conditionalProbes=80; source=" + code);
        }
        public static void AuditCompatibility()
        {
            var source = Source(); int count = 0; long ticks = 0;
            Parallel.ForEach(Directory.GetFiles(FormalAiBaselineTools.Root + "/cases", "*.input.json"), new ParallelOptions { MaxDegreeOfParallelism = 4 }, file =>
            {
                var input = JsonUtility.FromJson<FormalAiBaselineTools.Input>(File.ReadAllText(file));
                if (input.execution != (int)AiExecutionKind.UtilityBehaviorTree) return;
                var d = FormalAiBaseline.Build(source.Definition, (AiPolicyKind)input.policy, (AiExecutionKind)input.execution, input.near, input.mirror);
                if (d.BattleDefinitionHashValue != input.definitionHash) throw new InvalidOperationException("FROZEN_DEFINITION_CHANGED");
                using var stream = File.OpenRead(file.Replace(".input.json", ".heroReplay")); var replay = ReplayFile.Load(stream, d); Verify(d, replay);
                Interlocked.Increment(ref count); Interlocked.Add(ref ticks, replay.Records.Count);
            });
            if (count != 12) throw new InvalidOperationException("FROZEN_COMPATIBILITY_CASE_COUNT");
            File.WriteAllText(Root + "/native-compatibility.txt", "Passed; cases=" + count + "; ticks=" + ticks + "; source=" + AiPolicyExperimentTools.SourceFingerprint());
        }
        static void Verify(BattleDefinition definition, BattleReplay replay)
        {
            using var player = new ReplayPlayer(definition); player.Load(replay); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            if (player.Deviation != null) throw new InvalidOperationException(player.Deviation.ToString());
        }
        static StreamWriter Gzip(string path) => new StreamWriter(new GZipStream(File.Create(path), System.IO.Compression.CompressionLevel.Optimal), Encoding.UTF8);
        static string Csv(params object[] values) => string.Join(",", values.Select(v => "\"" + Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\""));
    }
}
