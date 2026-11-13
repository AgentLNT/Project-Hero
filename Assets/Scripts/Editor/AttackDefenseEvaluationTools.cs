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
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ProjectHero.Editor
{
    /// <summary>Task 19: frozen-content paired AI cases and independently executed single-defense branches.</summary>
    public static class AttackDefenseEvaluationTools
    {
        public const string Root = "优化任务/执行记录/进攻防御评价-2026-11-09";
        [Serializable] public sealed class Input
        {
            public string protocol = "attack-defense-evaluation-v1", definitionHash, sourceHash, sourceFingerprint, contentFingerprint;
            public long seed; public int metaResource, policy, maxTicks = 360; public bool near, mirror;
            public string Id => ((AiPolicyKind)policy) + "-" + (near ? "near" : "main") + "-mirror" + (mirror ? 1 : 0);
        }
        sealed class Prefix
        {
            public readonly List<(ControllerId controller, CommandRequest request)> Commands = new();
            public string Projection;
        }
        sealed class Fixture : IDisposable
        {
            public readonly ProductionBattleComposition Composition; public readonly BattleSimulation Sim;
            public Fixture(BattleDefinition d, BattleSimulationSeed source, bool autonomous = false)
            {
                Composition = new ProductionBattleComposition(d, source.EncounterId, source.RuntimeInputs);
                Sim = BattleSimulation.Create(d, source.EncounterId, source.RuntimeInputs,
                    autonomous ? Composition.Assembly : new BattleSimulationAssembly(turnWindowSchedule: Composition));
                Composition.Attach(Sim);
            }
            public AiDecisionContext Context(ControllerId id) => Composition.AiControllers.Single(a => a.RegisteredControllerIds.Contains(id.Value))
                .BuildContext(Sim.DecisionSnapshotFor(id), id);
            public void Submit(ControllerId id, CommandRequest request)
            { var error = Sim.CommandIngress.FindEntry(id).Submit(request); if (error != null) throw new InvalidOperationException("INGRESS: " + error); }
            public void Dispose() => Sim.Dispose();
        }
        static BattleSimulationSeed Source()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/CombatSampleScene.unity", OpenSceneMode.Single);
            var host = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<NewBattleSimulationSource>(true)).Single();
            return host.BuildSeed() ?? throw new InvalidOperationException(host.LastConfigurationError);
        }
        static BattleDefinition Build(BattleSimulationSeed source, Input input, bool defenses = true) => FormalAiBaseline.Build(source.Definition,
            (AiPolicyKind)input.policy, AiExecutionKind.UtilityBehaviorTree, input.near, input.mirror, new AiForecastSettings(360, defenses));
        static IEnumerable<Input> Matrix()
        {
            foreach (var p in new[] { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense })
            foreach (bool near in new[] { false, true }) foreach (bool mirror in new[] { false, true })
                yield return new Input { policy = (int)p, near = near, mirror = mirror };
        }
        public static void Run()
        {
            Directory.CreateDirectory(Root + "/cases"); var source = Source();
            string code = SourceFingerprint(), content = FormalAiBaselineTools.ContentFingerprint();
            var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
            Parallel.ForEach(Matrix(), new ParallelOptions { MaxDegreeOfParallelism = 4 }, input =>
            {
                var d = Build(source, input); input.definitionHash = d.BattleDefinitionHashValue;
                input.sourceHash = source.BattleDefinitionHash; input.sourceFingerprint = code; input.contentFingerprint = content;
                input.seed = unchecked((long)source.RuntimeInputs.InitialRngSeed); input.metaResource = source.RuntimeInputs.InitialMetaResource;
                using var f = new Fixture(d, source, true); var recorder = new ReplayRecorder(f.Sim);
                string path = Root + "/cases/" + input.Id;
                using var candidates = Gzip(path + ".candidates.csv.gz");
                using var replies = Gzip(path + ".defenses.csv.gz");
                candidates.WriteLine("tick,controller,key,type,chosen,score,noDefenseScore,damageQ10,noDefenseDamageQ10,worstDefense,rejection,reason,coverage,replyCount");
                replies.WriteLine("tick,controller,attack,key,defender,submitAfter,type,score,damageQ10,rejection,coverage,requiredThrough");
                long loss = 0, spent = 0; int commands = 0, rejected = 0, attacks = 0, guards = 0, reactions = 0;
                for (long tick = 0; tick < input.maxTicks && !f.Sim.IsEnded; tick++)
                {
                    var before = f.Sim.CurrentSnapshot; recorder.CaptureBeforeStep();
                    var result = f.Sim.Step(tick, f.Sim.CommandIngress.FreezeTick(tick)); recorder.RecordCommittedStep(result);
                    loss += result.Snapshot.Units.Sum(u => Math.Max(0L, before.Units.Single(b => b.UnitId == u.UnitId).HealthQ10 - u.HealthQ10));
                    commands += f.Sim.LastCommandSet.Envelopes.Count;
                    foreach (var e in result.Events.Events)
                    {
                        if (e is CommandRejectedEvent || e is CommandIngressRejectedEvent) rejected++;
                        if (e is AdrenalineLedgerChangedEvent ledger && ledger.ChangeKind == AdrenalineChangeKind.Consumed) spent += ledger.ReservationAmount;
                        if (e is ActionPlanCreatedEvent created && created.Origin == (int)ActionPlanOrigin.Reaction) reactions++;
                        if (e is ActionPlanLockedEvent locked)
                        {
                            var p = f.Sim.AiActionPlanLookup.FindPlan(new ActionPlanId(locked.ActionPlanId));
                            if (p.ActionType == ActionType.Attack) attacks++;
                            if (p.ActionType == ActionType.Guard) guards++;
                        }
                    }
                    foreach (var ai in f.Composition.AiControllers)
                    {
                        string id = ai.RegisteredControllerIds[0]; var decision = ai.LastDecisionOf(id);
                        if (decision?.DecisionTick != tick) continue;
                        foreach (var e in decision.Evaluations)
                        {
                            candidates.WriteLine(Csv(tick, id, e.Key, e.Type, e.Chosen, e.Score, e.NoDefenseScore, e.DamageDeltaQ10,
                                e.NoDefenseDamageDeltaQ10, e.WorstDefenseKey, e.RejectionCode, e.Reason, e.Coverage, e.DefenseResponses.Count));
                            foreach (var r in e.DefenseResponses) replies.WriteLine(Csv(tick, id, e.Key, r.Key, r.Defender, r.SubmittedAfterTick,
                                r.Type, r.Score, r.DamageDeltaQ10, r.RejectionCode, r.Coverage, r.RequiredThroughTick));
                        }
                    }
                }
                var replay = recorder.BuildReplay(); if (replay.Submissions.Count != 0 || rejected != 0) throw new InvalidOperationException("AUTONOMOUS_CASE_INVALID");
                using (var file = File.Create(path + ".heroReplay")) ReplayFile.Save(file, replay);
                File.WriteAllText(path + ".input.json", JsonUtility.ToJson(input, true), Encoding.UTF8);
                lock (rows) rows.Add(input.Id, Csv(input.Id, (AiPolicyKind)input.policy, input.near, input.mirror, replay.Records.Count,
                    f.Sim.IsEnded, commands, rejected, attacks, guards, reactions, loss, spent, d.BattleDefinitionHashValue));
                Debug.Log("ATTACK_DEFENSE_CASE " + input.Id);
            });
            File.WriteAllText(Root + "/summary.csv", "case,policy,near,mirror,ticks,ended,commands,rejections,attackStarts,guards,reactions,healthLossQ10,spent,definitionHash\n" + string.Join("\n", rows.Values) + "\n", Encoding.UTF8);
            Baseline(source, false);
            Probes(source, false);
        }

        static void Baseline(BattleSimulationSeed source, bool audit)
        {
            var rows = new List<string> { "case,ticks,ended,commands,healthLossQ10" };
            foreach (var input in Matrix())
            {
                var d = Build(source, input, false);
                using var stream = File.OpenRead(DefenseAuditTools.Root + "/cases/" + input.Id + ".heroReplay");
                var replay = ReplayFile.Load(stream, d); BattleSimulation sim = null;
                using var player = new ReplayPlayer(d, header =>
                {
                    var comp = new ProductionBattleComposition(d, header.EncounterId, header.RuntimeInputs);
                    sim = BattleSimulation.Create(d, header.EncounterId, header.RuntimeInputs, comp.Assembly); comp.Attach(sim); return sim;
                });
                player.Load(replay); player.Play(); long loss = 0; int commands = 0;
                for (long tick = 0; tick < input.maxTicks && !player.IsComplete; tick++)
                {
                    var before = sim.CurrentSnapshot; player.AdvanceOneTick();
                    if (player.Deviation != null) throw new InvalidOperationException(player.Deviation.ToString());
                    loss += sim.CurrentSnapshot.Units.Sum(u => Math.Max(0L, before.Units.Single(b => b.UnitId == u.UnitId).HealthQ10 - u.HealthQ10));
                    commands += sim.LastCommandSet.Envelopes.Count;
                }
                rows.Add(Csv(input.Id, sim.Tick + 1, sim.IsEnded, commands, loss));
            }
            CompareOrWrite("baseline-summary.csv", string.Join("\n", rows) + "\n", audit);
        }
        static Prefix ReadPrefix(BattleDefinition d, Input input, long checkpoint)
        {
            using var stream = File.OpenRead(DefenseAuditTools.Root + "/cases/" + input.Id + ".heroReplay");
            var replay = ReplayFile.Load(stream, d); var prefix = new Prefix(); BattleSimulation sim = null;
            using var player = new ReplayPlayer(d, header =>
            {
                var composition = new ProductionBattleComposition(d, header.EncounterId, header.RuntimeInputs);
                sim = BattleSimulation.Create(d, header.EncounterId, header.RuntimeInputs, composition.Assembly); composition.Attach(sim); return sim;
            });
            player.Load(replay); player.Play();
            for (long tick = 0; tick <= checkpoint; tick++)
            {
                player.AdvanceOneTick(); if (player.Deviation != null) throw new InvalidOperationException(player.Deviation.ToString());
                prefix.Commands.AddRange(sim.LastCommandSet.Envelopes.Select(e => (e.ControllerId, e.Request)));
            }
            prefix.Projection = Projection(sim.CurrentSnapshot.Units); return prefix;
        }
        static Fixture Branch(BattleDefinition d, BattleSimulationSeed source, Prefix prefix, long checkpoint)
        {
            var f = new Fixture(d, source);
            for (long tick = 0; tick <= checkpoint; tick++)
            {
                foreach (var command in prefix.Commands.Where(c => c.request.TargetTick == tick)) f.Submit(command.controller, command.request);
                f.Sim.Step(tick, f.Sim.CommandIngress.FreezeTick(tick));
            }
            if (Projection(f.Sim.CurrentSnapshot.Units) != prefix.Projection) throw new InvalidOperationException("PREFIX_MISMATCH");
            return f;
        }
        static void Probes(BattleSimulationSeed source, bool audit)
        {
            var scores = new List<string> { "case,checkpoint,attack,chosen,score,noDefenseScore,damageQ10,noDefenseDamageQ10,worstDefense,replies,coverage" };
            var rows = new List<string> { "case,checkpoint,attack,reply,defender,submitAfter,rejection,coverage,actualMatches,eventDigest,projection" };
            foreach (var input in Matrix().Where(i => i.policy == (int)AiPolicyKind.SustainedOffense))
            foreach (long checkpoint in input.near ? new long[] { 0, 240, 426 } : new long[] { 0 })
            {
                var d = Build(source, input); var prefix = ReadPrefix(Build(source, input, false), input, checkpoint);
                using var origin = Branch(d, source, prefix, checkpoint);
                var ctx = origin.Composition.AiControllers.Select(ai => origin.Context(new ControllerId(ai.RegisteredControllerIds[0]))).Single(c => c.HasSubmissionWindow);
                string before = origin.Sim.CurrentSnapshot.ComputeHash().ToString();
                var decision = AiTacticalPolicy.Decide(ctx, source.RuntimeInputs.InitialRngSeed, AiPolicyKind.SustainedOffense);
                using var forecast = ctx.ForecastFactory(ctx.Snapshot);
                foreach (var attack in AiTacticalCandidateBuilder.Ordinary(ctx).Where(c => c.Type == ActionType.Attack))
                {
                    var e = decision.Evaluations.Single(x => x.Key == attack.Key);
                    scores.Add(Csv(input.Id, checkpoint, attack.Key, e.Chosen, e.Score, e.NoDefenseScore, e.DamageDeltaQ10,
                        e.NoDefenseDamageDeltaQ10, e.WorstDefenseKey, e.DefenseResponses.Count, e.Coverage));
                    var replies = ((IAiDefenseForecast)forecast).EvaluateDefenses(attack.Request, ctx.ForecastSettings);
                    var control = forecast.Evaluate(attack.Request, ctx.ForecastSettings);
                    rows.Add(Execute(d, source, input, prefix, checkpoint, ctx, attack, null, control));
                    foreach (var reply in replies) rows.Add(Execute(d, source, input, prefix, checkpoint, ctx, attack, reply, reply.Outcome));
                }
                if (origin.Sim.CurrentSnapshot.ComputeHash().ToString() != before) throw new InvalidOperationException("FORECAST_MUTATED_SOURCE");
            }
            CompareOrWrite("probe-scores.csv", string.Join("\n", scores) + "\n", audit);
            CompareOrWrite("probe-branches.csv", string.Join("\n", rows) + "\n", audit);
        }
        static string Execute(BattleDefinition d, BattleSimulationSeed source, Input input, Prefix prefix, long checkpoint,
            AiDecisionContext context, AiTacticalCandidate attack, AiDefenseForecast reply, AiActionForecast expected)
        {
            using var f = Branch(d, source, prefix, checkpoint); f.Submit(context.ControllerId, attack.Request);
            long attackId = 0; string rejection = null; var events = new StringBuilder();
            while (!f.Sim.IsEnded && f.Sim.Tick < expected.HorizonTick)
            {
                long tick = f.Sim.Tick + 1; var result = f.Sim.Step(tick, f.Sim.CommandIngress.FreezeTick(tick));
                foreach (var fact in result.Events.Events) events.AppendLine(ReplayEventComparison.Canonical(fact));
                var rejected = result.Events.Events.OfType<CommandRejectedEvent>().FirstOrDefault();
                if (rejected != null) { rejection = rejected.ReasonCode; break; }
                if (tick == attack.Request.TargetTick) attackId = result.Events.Events.OfType<ActionPlanCreatedEvent>().Single().ActionPlanId;
                if (reply == null || tick != reply.SubmittedAfterTick) continue;
                var ctx = f.Context(reply.Defender);
                if (!ctx.Snapshot.VisiblePlans.Any(p => p.ActionPlanId == attackId && DecisionSnapshot.IsPubliclyRevealed(p)))
                    throw new InvalidOperationException("REPLY_TO_UNPUBLISHED_ATTACK");
                var request = reply.Request;
                if (request.Payload is ReactionCommandPayload)
                {
                    // Isolated forecasts derive IDs from visible facts. Bind the equivalent real published opportunity.
                    var opportunity = ctx.Opportunities.Single(o => o.SourceAttackPlanId.Value == attackId);
                    request = request with { Scope = new ReactionCommandScope(opportunity.ReactionOpportunityId) };
                }
                f.Submit(reply.Defender, request);
            }
            bool matches = rejection == expected.RejectionCode && (rejection != null || Projection(f.Sim.CurrentSnapshot.Units) == Projection(expected.Units));
            if (!matches) throw new InvalidOperationException("REAL_DEFENSE_MISMATCH: " + input.Id + "/" + checkpoint + "/" + attack.Key + "/" + reply?.Key
                + ": expected=" + expected.RejectionCode + ";actual=" + rejection);
            return Csv(input.Id, checkpoint, attack.Key, reply?.Key ?? "None", reply?.Defender.Value, reply?.SubmittedAfterTick,
                rejection, expected.Coverage, matches, Sha(Encoding.UTF8.GetBytes(events.ToString())), Projection(f.Sim.CurrentSnapshot.Units));
        }
        public static void Audit()
        {
            var source = Source(); string code = SourceFingerprint(), content = FormalAiBaselineTools.ContentFingerprint();
            int cases = 0, oldCases = 0; long ticks = 0, oldTicks = 0;
            Parallel.ForEach(Matrix(), new ParallelOptions { MaxDegreeOfParallelism = 4 }, input =>
            {
                string path = Root + "/cases/" + input.Id;
                var saved = JsonUtility.FromJson<Input>(File.ReadAllText(path + ".input.json")); var d = Build(source, input);
                if (saved.protocol != input.protocol || saved.definitionHash != d.BattleDefinitionHashValue || saved.sourceHash != source.BattleDefinitionHash
                    || saved.sourceFingerprint != code || saved.contentFingerprint != content || saved.seed != unchecked((long)source.RuntimeInputs.InitialRngSeed)
                    || saved.metaResource != source.RuntimeInputs.InitialMetaResource || saved.maxTicks != input.maxTicks
                    || saved.policy != input.policy || saved.near != input.near || saved.mirror != input.mirror) throw new InvalidOperationException("AUDIT_INPUT_CHANGED");
                long count = Verify(d, path + ".heroReplay");
                long oldCount = Verify(Build(source, input, false), DefenseAuditTools.Root + "/cases/" + input.Id + ".heroReplay");
                lock (source) { cases++; ticks += count; oldCases++; oldTicks += oldCount; }
            });
            Baseline(source, true);
            Probes(source, true);
            File.WriteAllText(Root + "/native-disk-audit.txt", $"cases={cases};ticks={ticks};historicalCases={oldCases};historicalTicks={oldTicks};allProbeBranchesIndependentlyRebuilt=True\n", Encoding.UTF8);
        }
        static long Verify(BattleDefinition d, string path)
        {
            using var stream = File.OpenRead(path); var replay = ReplayFile.Load(stream, d);
            using var player = new ReplayPlayer(d); player.Load(replay); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            if (player.Deviation != null) throw new InvalidOperationException(player.Deviation.ToString());
            return replay.Records.Count;
        }
        static void CompareOrWrite(string name, string text, bool audit)
        {
            string path = Root + "/" + name;
            if (audit) { if (File.ReadAllText(path) != text) throw new InvalidOperationException("DIAGNOSTIC_REBUILD_MISMATCH: " + name); }
            else File.WriteAllText(path, text, Encoding.UTF8);
        }
        static string Projection(IEnumerable<UnitSnapshot> units) => string.Join(";", units.Select(u =>
            FormattableString.Invariant($"{u.UnitId}:{u.HealthQ10}:{u.X}:{u.Y}:{u.State}:{u.StateStartTick}:{u.StateEndTick}:{u.AvailableAdrenaline}:{u.AdrenalineCycleId}")));
        static string SourceFingerprint() => Sha(Encoding.UTF8.GetBytes(string.Concat(new[] { "Assets/Scripts", "Assets/Tests", "Assets/Diagnostics" }
            .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)).Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => p + ":" + Sha(File.ReadAllBytes(p)) + "\n"))));
        static string Sha(byte[] bytes) { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static StreamWriter Gzip(string path) => new StreamWriter(new GZipStream(File.Create(path), System.IO.Compression.CompressionLevel.Optimal), Encoding.UTF8);
        static string Csv(params object[] values) => string.Join(",", values.Select(v => "\"" + Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\""));
    }
}
