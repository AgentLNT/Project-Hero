using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ProjectHero.Authoring;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ProjectHero.Editor
{
    public static class ApproachGoalTools
    {
        public const string Root = "优化任务/执行记录/主动接近目标-2026-11-11";
        [Serializable] public sealed class Input
        {
            public string protocol = "unthreatened-approach-audit-v1", definitionHash, sourceHash, sourceFingerprint, contentFingerprint;
            public bool near, mirror; public int maxTicks = 240, metaResource; public long seed;
            public string Id => (near ? "near" : "main") + "-mirror" + (mirror ? 1 : 0);
        }
        static IEnumerable<Input> Matrix()
        {
            foreach (bool near in new[] { false, true }) foreach (bool mirror in new[] { false, true })
                yield return new Input { near = near, mirror = mirror };
        }
        static BattleSimulationSeed Source()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/CombatSampleScene.unity", OpenSceneMode.Single);
            var host = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<NewBattleSimulationSource>(true)).Single();
            return host.BuildSeed() ?? throw new InvalidOperationException(host.LastConfigurationError);
        }
        static BattleDefinition Build(BattleSimulationSeed source, Input input, bool enabled = true, AiPolicyKind policy = AiPolicyKind.CautiousDefense)
            => FormalAiBaseline.Build(source.Definition, policy, AiExecutionKind.UtilityBehaviorTree, input.near, input.mirror,
                new AiForecastSettings(360, true, enabled));
        static readonly string Header = "case,ticks,ended,commands,moves,attacks,goalMoves,rejections,initialDistance,minimumDistance,finalDistance,healthLossQ10,definitionHash";
        static string DiagnosticHeader => "tick,controller,key,type,chosen,score,reason,progress,incomingQ10,damageQ10,noDefenseDamageQ10,start,end,coverage";
        static IEnumerable<string> Diagnostics(ProductionBattleComposition composition, long tick)
        {
            foreach (var ai in composition.AiControllers)
            {
                string id = ai.RegisteredControllerIds[0]; var decision = ai.LastDecisionOf(id);
                if (decision?.DecisionTick != tick) continue;
                foreach (var e in decision.Evaluations)
                    yield return Csv(tick, id, e.Key, e.Type, e.Chosen, e.Score, e.Reason, e.PositionProgress, e.IncomingDeltaQ10,
                        e.DamageDeltaQ10, e.NoDefenseDamageDeltaQ10, e.StartTick, e.EndTick, e.Coverage);
            }
        }
        sealed class Metrics
        {
            public int Commands, Moves, Attacks, GoalMoves, Rejections; public long Loss, InitialDistance, MinimumDistance, FinalDistance;
            public Metrics(LogicSnapshot initial) { InitialDistance = Distance(initial); MinimumDistance = InitialDistance; }
            public void Step(BattleSimulation sim, LogicSnapshot before, IEnumerable<LogicEvent> events, BattleDefinition definition)
            {
                Commands += sim.LastCommandSet.Envelopes.Count;
                foreach (var envelope in sim.LastCommandSet.Envelopes)
                    if (envelope.Request.Payload is ScheduleEditPayload payload)
                        foreach (var add in payload.Operations.OfType<AddOrdinaryPlanOperation>())
                        {
                            var type = definition.FindAction(add.ActionSpecId).Type;
                            if (type == ActionType.Move) Moves++; if (type == ActionType.Attack) Attacks++;
                        }
                if (events != null) Rejections += events.Count(e => e is CommandRejectedEvent || e is CommandIngressRejectedEvent);
                Loss += sim.CurrentSnapshot.Units.Sum(u => Math.Max(0L, before.Units.Single(b => b.UnitId == u.UnitId).HealthQ10 - u.HealthQ10));
                FinalDistance = Distance(sim.CurrentSnapshot); MinimumDistance = Math.Min(MinimumDistance, FinalDistance);
            }
            public string Row(string id, BattleSimulation sim, string hash) => Csv(id, sim.Tick + 1, sim.IsEnded, Commands, Moves,
                Attacks, GoalMoves, Rejections, InitialDistance, MinimumDistance, FinalDistance, Loss, hash);
        }
        static long Distance(LogicSnapshot snapshot)
        {
            var units = snapshot.Units.OrderBy(u => u.UnitId).ToArray();
            return Math.Abs((long)units[0].X - units[1].X) + Math.Abs((long)units[0].Y - units[1].Y);
        }
        public static void Run()
        {
            Directory.CreateDirectory(Root + "/cases"); var source = Source();
            string fingerprint = SourceFingerprint(), content = FormalAiBaselineTools.ContentFingerprint();
            var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
            Parallel.ForEach(Matrix(), new ParallelOptions { MaxDegreeOfParallelism = 2 }, input =>
            {
                var d = Build(source, input); input.sourceHash = source.BattleDefinitionHash; input.definitionHash = d.BattleDefinitionHashValue;
                input.sourceFingerprint = fingerprint; input.contentFingerprint = content;
                input.seed = unchecked((long)source.RuntimeInputs.InitialRngSeed); input.metaResource = source.RuntimeInputs.InitialMetaResource;
                var comp = new ProductionBattleComposition(d, source.EncounterId, source.RuntimeInputs);
                using var sim = BattleSimulation.Create(d, source.EncounterId, source.RuntimeInputs, comp.Assembly); comp.Attach(sim);
                var recorder = new ReplayRecorder(sim); var metrics = new Metrics(sim.CurrentSnapshot);
                string path = Root + "/cases/" + input.Id;
                using var log = Gzip(path + ".candidates.csv.gz"); log.WriteLine(DiagnosticHeader);
                for (long tick = 0; tick < input.maxTicks && !sim.IsEnded; tick++)
                {
                    var before = sim.CurrentSnapshot; recorder.CaptureBeforeStep();
                    var result = sim.Step(tick, sim.CommandIngress.FreezeTick(tick)); recorder.RecordCommittedStep(result);
                    metrics.Step(sim, before, result.Events.Events, d);
                    foreach (var line in Diagnostics(comp, tick)) log.WriteLine(line);
                    foreach (var ai in comp.AiControllers)
                    {
                        var decision = ai.LastDecisionOf(ai.RegisteredControllerIds[0]);
                        if (decision?.DecisionTick == tick) metrics.GoalMoves += decision.Evaluations.Count(e => e.Chosen && e.Reason == AiApproachPolicy.SelectedReason);
                    }
                }
                if (metrics.Rejections != 0 || (!input.near && (metrics.GoalMoves == 0 || metrics.MinimumDistance >= metrics.InitialDistance)))
                    throw new InvalidOperationException("APPROACH_CASE_FAILED: " + input.Id);
                var replay = recorder.BuildReplay(); if (replay.Submissions.Count != 0) throw new InvalidOperationException("EXTERNAL_INPUT_IN_AI_CASE");
                using (var file = File.Create(path + ".heroReplay")) ReplayFile.Save(file, replay);
                File.WriteAllText(path + ".input.json", JsonUtility.ToJson(input, true), Encoding.UTF8);
                lock (rows) rows.Add(input.Id, metrics.Row(input.Id, sim, d.BattleDefinitionHashValue));
                Debug.Log("APPROACH_CASE " + input.Id);
            });
            File.WriteAllText(Root + "/summary.csv", Header + "\n" + string.Join("\n", rows.Values) + "\n", Encoding.UTF8);
            Baseline(source, false);
        }
        static void Baseline(BattleSimulationSeed source, bool audit)
        {
            var rows = new List<string> { Header };
            foreach (var input in Matrix())
            {
                var d = Build(source, input, false); string path = AttackDefenseEvaluationTools.Root + "/cases/CautiousDefense-" + input.Id + ".heroReplay";
                using var file = File.OpenRead(path); var replay = ReplayFile.Load(file, d); BattleSimulation sim = null;
                using var player = new ReplayPlayer(d, h =>
                {
                    var comp = new ProductionBattleComposition(d, h.EncounterId, h.RuntimeInputs);
                    sim = BattleSimulation.Create(d, h.EncounterId, h.RuntimeInputs, comp.Assembly); comp.Attach(sim); return sim;
                });
                player.Load(replay); var metrics = new Metrics(sim.CurrentSnapshot); player.Play();
                for (long tick = 0; tick < input.maxTicks && !player.IsComplete; tick++)
                {
                    var before = sim.CurrentSnapshot; player.AdvanceOneTick();
                    if (player.Deviation != null) throw new InvalidOperationException(player.Deviation.ToString());
                    metrics.Step(sim, before, null, d);
                }
                rows.Add(metrics.Row(input.Id, sim, d.BattleDefinitionHashValue));
            }
            CompareOrWrite("baseline-summary.csv", string.Join("\n", rows) + "\n", audit);
        }
        public static void Audit()
        {
            var source = Source(); string fingerprint = SourceFingerprint(), content = FormalAiBaselineTools.ContentFingerprint();
            int cases = 0, historical = 0; long ticks = 0, historicalTicks = 0;
            Parallel.ForEach(Matrix(), new ParallelOptions { MaxDegreeOfParallelism = 2 }, input =>
            {
                string path = Root + "/cases/" + input.Id; var saved = JsonUtility.FromJson<Input>(File.ReadAllText(path + ".input.json"));
                var d = Build(source, input);
                if (saved.protocol != input.protocol || saved.near != input.near || saved.mirror != input.mirror || saved.maxTicks != input.maxTicks
                    || saved.definitionHash != d.BattleDefinitionHashValue || saved.sourceHash != source.BattleDefinitionHash
                    || saved.sourceFingerprint != fingerprint || saved.contentFingerprint != content
                    || saved.seed != unchecked((long)source.RuntimeInputs.InitialRngSeed) || saved.metaResource != source.RuntimeInputs.InitialMetaResource)
                    throw new InvalidOperationException("APPROACH_INPUT_CHANGED");
                var lines = new List<string> { DiagnosticHeader }; long count = Verify(d, path + ".heroReplay", lines);
                using var csv = new StreamReader(new GZipStream(File.OpenRead(path + ".candidates.csv.gz"), CompressionMode.Decompress), Encoding.UTF8);
                if (csv.ReadToEnd().Replace("\r\n", "\n") != string.Join("\n", lines) + "\n") throw new InvalidOperationException("APPROACH_DIAGNOSTICS_CHANGED");
                lock (source) { cases++; ticks += count; }
            });
            foreach (var policy in new[] { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense })
            foreach (var input in Matrix())
            {
                var d = Build(source, input, false, policy);
                historicalTicks += Verify(d, AttackDefenseEvaluationTools.Root + "/cases/" + policy + "-" + input.Id + ".heroReplay"); historical++;
            }
            Baseline(source, true);
            File.WriteAllText(Root + "/native-disk-audit.txt", $"cases={cases};ticks={ticks};historicalCases={historical};historicalTicks={historicalTicks};candidateLogsRebuilt=True;baselineRebuilt=True\n", Encoding.UTF8);
        }
        static long Verify(BattleDefinition d, string path, List<string> diagnostics = null)
        {
            using var file = File.OpenRead(path); var replay = ReplayFile.Load(file, d); ProductionBattleComposition comp = null;
            using var player = new ReplayPlayer(d, h =>
            {
                comp = new ProductionBattleComposition(d, h.EncounterId, h.RuntimeInputs);
                var sim = BattleSimulation.Create(d, h.EncounterId, h.RuntimeInputs, comp.Assembly); comp.Attach(sim); return sim;
            });
            player.Load(replay); player.Play();
            while (!player.IsComplete && player.Deviation == null)
            {
                player.AdvanceOneTick();
                if (diagnostics != null) diagnostics.AddRange(Diagnostics(comp, player.CurrentSnapshot.Tick));
            }
            if (player.Deviation != null) throw new InvalidOperationException(player.Deviation.ToString());
            return replay.Records.Count;
        }
        static void CompareOrWrite(string name, string value, bool audit)
        {
            if (audit) { if (File.ReadAllText(Root + "/" + name) != value) throw new InvalidOperationException("BASELINE_CHANGED"); }
            else File.WriteAllText(Root + "/" + name, value, Encoding.UTF8);
        }
        static string SourceFingerprint() => Sha(Encoding.UTF8.GetBytes(string.Concat(new[] { "Assets/Scripts", "Assets/Tests", "Assets/Diagnostics" }
            .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)).Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => p + ":" + Sha(File.ReadAllBytes(p)) + "\n"))));
        static string Sha(byte[] bytes) { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static StreamWriter Gzip(string path) => new StreamWriter(new GZipStream(File.Create(path), System.IO.Compression.CompressionLevel.Optimal), Encoding.UTF8);
        static string Csv(params object[] values) => string.Join(",", values.Select(v => "\"" + Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\""));
    }
}
