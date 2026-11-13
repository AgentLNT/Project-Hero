using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
#if !PROJECT_HERO_STANDALONE
using UnityEditor.SceneManagement;
#endif
using Debug = UnityEngine.Debug;
using JsonUtility = UnityEngine.JsonUtility;

namespace ProjectHero.Editor
{
    /// <summary>Task 21: formal-content ordered matchups, distance expansion and independent replay audit.</summary>
    public static class FormalAiMatchupTools
    {
        public const string Root = "优化任务/执行记录/偏好互打长局-2026-11-11";
        [Serializable] public sealed class Input
        {
            public string protocol = "formal-ordered-matchup-v1", sourceHash, definitionHash, sourceFingerprint, contentFingerprint;
            public int heroPolicy, enemyPolicy, layout, maxTicks = 3600, metaResource; public bool mirror; public long seed;
            public string Pair => ((AiPolicyKind)heroPolicy) + "-vs-" + ((AiPolicyKind)enemyPolicy);
            public string Id => Pair + "-" + (FormalAiLayout)layout + "-mirror" + (mirror ? 1 : 0);
        }
        static readonly AiPolicyKind[] Policies = { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense };
        static IEnumerable<Input> Matrix() => from hero in Policies from enemy in Policies
            from layout in new[] { FormalAiLayout.Near, FormalAiLayout.Main, FormalAiLayout.Far }
            from mirror in new[] { false, true }
            select new Input { heroPolicy = (int)hero, enemyPolicy = (int)enemy, layout = (int)layout, mirror = mirror };
        static BattleSimulationSeed Source()
        {
#if PROJECT_HERO_STANDALONE
            return FormalAiSourceExport.Seed();
#else
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/CombatSampleScene.unity", OpenSceneMode.Single);
            var source = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<NewBattleSimulationSource>(true)).Single();
            return source.BuildSeed() ?? throw new InvalidOperationException(source.LastConfigurationError);
#endif
        }
#if !PROJECT_HERO_STANDALONE
        public static void Export() => FormalAiSourceExporter.Export(Source());
#endif
        static BattleDefinition Build(BattleSimulationSeed source, Input i)
            => FormalAiMatchup.Build(source.Definition, (AiPolicyKind)i.heroPolicy, (AiPolicyKind)i.enemyPolicy, (FormalAiLayout)i.layout, i.mirror);
        static void Stamp(Input input, BattleSimulationSeed source, BattleDefinition definition, string code, string content)
        {
            input.sourceHash = source.BattleDefinitionHash; input.definitionHash = definition.BattleDefinitionHashValue;
            input.sourceFingerprint = code; input.contentFingerprint = content;
            input.seed = unchecked((long)source.RuntimeInputs.InitialRngSeed); input.metaResource = source.RuntimeInputs.InitialMetaResource;
        }
        static void VerifyInput(Input expected, string path)
        {
            var saved = JsonUtility.FromJson<Input>(File.ReadAllText(path));
            if (JsonUtility.ToJson(saved) != JsonUtility.ToJson(expected)) throw new InvalidOperationException("MATCHUP_INPUT_CHANGED: " + path);
        }
        static long Distance(LogicSnapshot snapshot)
        {
            var u = snapshot.Units.OrderBy(x => x.UnitId).ToArray();
            return Math.Abs((long)u[0].X - u[1].X) + Math.Abs((long)u[0].Y - u[1].Y);
        }
        const string Header = "case,pair,layout,mirror,ticks,ended,result,heroHealthQ10,enemyHealthQ10,commands,rejections,attackStarts,movesStarted,hits,healthLossQ10,initialDistance,minimumDistance,finalDistance,firstDamageTick,lastDamageTick,lastCommandTick,lastProgressTick,goalMoves,uncoveredEvaluations,definitionHash";
        const string CandidateHeader = "tick,controller,policy,key,type,chosen,score,reason,rejection,damageQ10,incomingQ10,progress,noDefenseScore,noDefenseDamageQ10,worstDefense,defenseCount,start,impact,end,coverage";
        const string DecisionHeader = "tick,controller,policy,selected,reason,candidates,bestScore,ordinaryCursor,reactionCursor";
        const string CheckpointHeader = "tick,ended,heroHealthQ10,enemyHealthQ10,healthLossQ10,attackStarts,hits,commands,minimumDistance,finalDistance,lastDamageTick";

        sealed class Metrics
        {
            public int Commands, Rejections, AttackStarts, MovesStarted, Hits, GoalMoves, Uncovered;
            public long HealthLoss, FirstDamage = -1, LastDamage = -1, LastCommand = -1, LastProgress = -1, FirstWindowOwner;
            public long InitialDistance, MinimumDistance, FinalDistance;
            readonly long heroId, enemyId;
            readonly SortedDictionary<string, int> waits = new(StringComparer.Ordinal);
            readonly SortedDictionary<string, long[]> actions = new(StringComparer.Ordinal);
            readonly SortedDictionary<long, long[]> resources = new();
            public readonly List<string> Checkpoints = new() { CheckpointHeader };
            public Metrics(BattleDefinition d, BattleSimulationSeed source, LogicSnapshot snapshot)
            {
                var initial = BattleInitializer.BuildInitialState(d, source.EncounterId, source.RuntimeInputs);
                heroId = initial.SlotToUnitId[new EncounterSlotId("hero")].Value;
                enemyId = initial.SlotToUnitId[new EncounterSlotId("enemy")].Value;
                InitialDistance = MinimumDistance = FinalDistance = Distance(snapshot);
                foreach (var u in snapshot.Units) resources.Add(u.UnitId, new long[5]);
            }
            long[] Action(long owner, string id)
            {
                string key = owner + ":" + id;
                if (!actions.TryGetValue(key, out var values)) actions.Add(key, values = new long[3]);
                return values;
            }
            public void Step(BattleSimulation sim, LogicSnapshot before, IReadOnlyList<LogicEvent> events)
            {
                long tick = sim.Tick; var snapshot = sim.CurrentSnapshot;
                int commands = sim.LastCommandSet.Envelopes.Count; Commands += commands; if (commands > 0) LastCommand = tick;
                foreach (var e in events)
                {
                    if (e is CommandRejectedEvent || e is CommandIngressRejectedEvent) Rejections++;
                    if (e is TurnWindowOpenedEvent window && FirstWindowOwner == 0) FirstWindowOwner = window.OwnerUnitId.Value;
                    if (e is ActionPlanLockedEvent started)
                    {
                        var plan = sim.AiActionPlanLookup.FindPlan(new ActionPlanId(started.ActionPlanId));
                        Action(started.OwnerUnitId, plan.ActionSpecId.Value)[0]++;
                        if (plan.ActionType == ActionType.Attack) AttackStarts++;
                        if (plan.ActionType == ActionType.Move) MovesStarted++;
                    }
                    if (e is DamageChannelResolvedEvent damage && damage.AfterBlockDamageQ10 > 0)
                    {
                        Hits++; var plan = sim.AiActionPlanLookup.FindPlan(damage.AttackPlanId);
                        var totals = Action(damage.AttackerUnitId.Value, plan.ActionSpecId.Value);
                        totals[1]++; totals[2] += damage.AfterBlockDamageQ10;
                    }
                    if (e is AdrenalineLedgerChangedEvent resource)
                    {
                        var totals = resources[resource.UnitId.Value];
                        if (resource.ChangeKind == AdrenalineChangeKind.Accrued) totals[0] += resource.AvailableAfter - resource.AvailableBefore;
                        if (resource.ChangeKind == AdrenalineChangeKind.Consumed) totals[1] += resource.ReservationAmount;
                        if (resource.ChangeKind == AdrenalineChangeKind.CycleReset) { totals[2] += resource.AvailableBefore; totals[3]++; }
                        if (resource.ChangeKind == AdrenalineChangeKind.Refunded) totals[4] += resource.AvailableAfter - resource.AvailableBefore;
                    }
                }
                long loss = snapshot.Units.Sum(u => Math.Max(0L, before.Units.Single(b => b.UnitId == u.UnitId).HealthQ10 - u.HealthQ10));
                HealthLoss += loss;
                bool moved = snapshot.Units.Any(u => before.Units.Any(b => b.UnitId == u.UnitId && (b.X != u.X || b.Y != u.Y)));
                if (loss > 0) { if (FirstDamage < 0) FirstDamage = tick; LastDamage = tick; }
                if (loss > 0 || moved) LastProgress = tick;
                FinalDistance = Distance(snapshot); MinimumDistance = Math.Min(MinimumDistance, FinalDistance);
                long elapsed = tick + 1;
                if (elapsed == 240 || elapsed == 900 || elapsed == 1800 || elapsed == 3600 || sim.IsEnded)
                    Checkpoints.Add(Csv(elapsed, sim.IsEnded, Health(snapshot, heroId), Health(snapshot, enemyId), HealthLoss,
                        AttackStarts, Hits, Commands, MinimumDistance, FinalDistance, LastDamage));
            }
            public void Decisions(ProductionBattleComposition comp, long tick, Action<string> candidate, Action<string> decision)
            {
                foreach (var ai in comp.AiControllers)
                {
                    string controller = ai.RegisteredControllerIds[0]; var value = ai.LastDecisionOf(controller);
                    if (value?.DecisionTick != tick) continue;
                    var scores = value.Evaluations.Where(e => e.Score.HasValue).Select(e => e.Score.Value).ToArray();
                    decision(Csv(tick, controller, value.Policy, value.HasSelection, value.NoSelectionReason,
                        value.Evaluations.Count, scores.Length == 0 ? (long?)null : scores.Max(), value.Behavior?.Ordinary.Cursor, value.Behavior?.Reaction.Cursor));
                    if (!value.HasSelection)
                    {
                        string key = controller + ":" + (value.NoSelectionReason ?? "UNSPECIFIED");
                        waits.TryGetValue(key, out int count); waits[key] = count + 1;
                    }
                    foreach (var e in value.Evaluations)
                    {
                        if (e.Chosen && e.Reason == AiApproachPolicy.SelectedReason) GoalMoves++;
                        if (e.Coverage == AiForecastCoverage.Uncovered) Uncovered++;
                        candidate(Csv(tick, controller, value.Policy, e.Key, e.Type, e.Chosen, e.Score, e.Reason,
                            e.RejectionCode, e.DamageDeltaQ10, e.IncomingDeltaQ10, e.PositionProgress, e.NoDefenseScore,
                            e.NoDefenseDamageDeltaQ10, e.WorstDefenseKey, e.DefenseResponses.Count, e.StartTick, e.ImpactTick, e.EndTick, e.Coverage));
                    }
                }
            }
            static long Health(LogicSnapshot s, long id) => s.Units.Single(u => u.UnitId == id).HealthQ10;
            public string Row(Input i, BattleSimulation sim) => Csv(i.Id, i.Pair, (FormalAiLayout)i.layout, i.mirror, sim.Tick + 1,
                sim.IsEnded, sim.CurrentSnapshot.BattleEnd.ResultCode, Health(sim.CurrentSnapshot, heroId), Health(sim.CurrentSnapshot, enemyId),
                Commands, Rejections, AttackStarts, MovesStarted, Hits, HealthLoss, InitialDistance, MinimumDistance, FinalDistance,
                FirstDamage, LastDamage, LastCommand, LastProgress, GoalMoves, Uncovered, i.definitionHash);
            public string Waits() => "controller:reason,decisionTicks\n" + string.Join("\n", waits.Select(p => Csv(p.Key, p.Value))) + "\n";
            public string Actions() => "unit:action,starts,positiveDamageContacts,resolvedDamageQ10\n" + string.Join("\n", actions.Select(p => Csv(p.Key, p.Value[0], p.Value[1], p.Value[2]))) + "\n";
            public string Resources() => "unit,actualAccrued,consumed,expiredAtCycleReset,cycles,refunded\n" + string.Join("\n", resources.Select(p => Csv(p.Key, p.Value[0], p.Value[1], p.Value[2], p.Value[3], p.Value[4]))) + "\n";
            public string Owners() => Csv(heroId, enemyId, FirstWindowOwner) + "\n";
        }

        public static void Pilot() => Generate(Matrix().Where(i => i.layout == (int)FormalAiLayout.Near && !i.mirror));
        public static void Run() => Generate(Matrix());
        static void Generate(IEnumerable<Input> matrix)
        {
            Directory.CreateDirectory(Root + "/cases"); var source = Source(); string code = SourceFingerprint(), content = FormalAiBaselineTools.ContentFingerprint();
            Parallel.ForEach(matrix, new ParallelOptions { MaxDegreeOfParallelism = Workers }, input =>
            {
                var d = Build(source, input); Stamp(input, source, d, code, content); string path = Root + "/cases/" + input.Id;
                if (File.Exists(path + ".input.json")) { VerifyInput(input, path + ".input.json"); Debug.Log("MATCHUP_REUSED " + input.Id); return; }
                var stopwatch = Stopwatch.StartNew();
                var comp = new ProductionBattleComposition(d, source.EncounterId, source.RuntimeInputs);
                using var sim = BattleSimulation.Create(d, source.EncounterId, source.RuntimeInputs, comp.Assembly); comp.Attach(sim);
                var metrics = new Metrics(d, source, sim.CurrentSnapshot); var recorder = new ReplayRecorder(sim);
                using (var candidates = Gzip(path + ".candidates.csv.gz"))
                using (var decisions = Gzip(path + ".decisions.csv.gz"))
                using (var events = Gzip(path + ".events.csv.gz"))
                {
                    candidates.WriteLine(CandidateHeader); decisions.WriteLine(DecisionHeader); events.WriteLine("tick,event");
                    for (long tick = 0; tick < input.maxTicks && !sim.IsEnded; tick++)
                    {
                        var before = sim.CurrentSnapshot; recorder.CaptureBeforeStep();
                        var step = sim.Step(tick, sim.CommandIngress.FreezeTick(tick)); recorder.RecordCommittedStep(step);
                        metrics.Step(sim, before, step.Events.Events); metrics.Decisions(comp, tick, candidates.WriteLine, decisions.WriteLine);
                        foreach (var e in step.Events.Events) events.WriteLine(Csv(tick, ReplayEventComparison.Canonical(e)));
                        if ((tick + 1) % 300 == 0) Debug.Log("MATCHUP_PROGRESS " + input.Id + " ticks=" + (tick + 1));
                    }
                }
                var replay = recorder.BuildReplay(); if (replay.Submissions.Count != 0) throw new InvalidOperationException("MATCHUP_EXTERNAL_INPUT");
                using (var file = File.Create(path + ".heroReplay")) ReplayFile.Save(file, replay);
                WriteMetrics(path, input, sim, metrics, false);
                File.WriteAllText(path + ".timing.txt", "diagnosticWallSeconds=" + stopwatch.Elapsed.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "\n", Encoding.UTF8);
                File.WriteAllText(path + ".input.json", JsonUtility.ToJson(input, true), Encoding.UTF8);
                Debug.Log("MATCHUP_COMPLETE " + input.Id + " ticks=" + replay.Records.Count + " lossQ10=" + metrics.HealthLoss + " rejections=" + metrics.Rejections);
            });
            var rows = Matrix().Select(i => Root + "/cases/" + i.Id + ".summary.csv").Where(File.Exists).OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => File.ReadAllLines(p)[1]);
            File.WriteAllText(Root + "/summary.csv", Header + "\n" + string.Join("\n", rows) + "\n", Encoding.UTF8);
        }
        static void WriteMetrics(string path, Input input, BattleSimulation sim, Metrics metrics, bool compare)
        {
            CheckOrWrite(path + ".summary.csv", Header + "\n" + metrics.Row(input, sim) + "\n", compare);
            CheckOrWrite(path + ".checkpoints.csv", string.Join("\n", metrics.Checkpoints) + "\n", compare);
            CheckOrWrite(path + ".waits.csv", metrics.Waits(), compare); CheckOrWrite(path + ".actions.csv", metrics.Actions(), compare);
            CheckOrWrite(path + ".resources.csv", metrics.Resources(), compare);
            CheckOrWrite(path + ".owners.csv", "heroUnit,enemyUnit,firstWindowOwner\n" + metrics.Owners(), compare);
        }
        public static void Audit() => AuditMatrix(Root + "/cases", Matrix(), false);
        public static void AuditUnityReference()
            => AuditMatrix(Root + "/unity-reference/cases", Directory.GetFiles(Root + "/unity-reference/cases", "*.input.json")
                .OrderBy(p => p, StringComparer.Ordinal).Select(p => JsonUtility.FromJson<Input>(File.ReadAllText(p))), true);
        [Serializable] sealed class SourceSeal { public string fingerprint; }
        static void AuditMatrix(string folder, IEnumerable<Input> matrix, bool reference)
        {
            var source = Source(); string code = SourceFingerprint(), content = FormalAiBaselineTools.ContentFingerprint();
            if (reference) code = JsonUtility.FromJson<SourceSeal>(File.ReadAllText(Root + "/unity-reference/frozen-source.json")).fingerprint;
            int cases = 0; long ticks = 0;
            Parallel.ForEach(matrix, new ParallelOptions { MaxDegreeOfParallelism = Workers }, input =>
            {
                var d = Build(source, input); Stamp(input, source, d, code, content); string path = folder + "/" + input.Id;
                VerifyInput(input, path + ".input.json"); using var file = File.OpenRead(path + ".heroReplay"); var replay = ReplayFile.Load(file, d);
                if (replay.Submissions.Count != 0) throw new InvalidOperationException("MATCHUP_REPLAY_HAS_EXTERNAL_INPUT");
                string error = ReplayHeaderValidation.Validate(replay.Header, d);
                if (error != null || replay.Records.Count > input.maxTicks) throw new InvalidOperationException("MATCHUP_REPLAY_HEADER: " + error);
                var comp = new ProductionBattleComposition(d, replay.Header.EncounterId, replay.Header.RuntimeInputs);
                using var sim = BattleSimulation.Create(d, replay.Header.EncounterId, replay.Header.RuntimeInputs, comp.Assembly); comp.Attach(sim);
                var metrics = new Metrics(d, source, sim.CurrentSnapshot);
                using var candidates = ReadGzip(path + ".candidates.csv.gz"); using var decisions = ReadGzip(path + ".decisions.csv.gz");
                using var events = ReadGzip(path + ".events.csv.gz");
                EqualLine(candidates, CandidateHeader); EqualLine(decisions, DecisionHeader); EqualLine(events, "tick,event");
                foreach (var expected in replay.Records)
                {
                    if (expected.Tick != sim.Tick + 1 || sim.IsEnded) throw new InvalidOperationException("MATCHUP_REPLAY_TICK_SEQUENCE");
                    var before = sim.CurrentSnapshot; var actual = sim.Step(expected.Tick, sim.CommandIngress.FreezeTick(expected.Tick));
                    if (actual.Events.Count != expected.Events.Count || actual.Snapshot.ComputeHash() != expected.SnapshotHash)
                        throw new InvalidOperationException("MATCHUP_REPLAY_DEVIATION: " + input.Id + " tick=" + expected.Tick);
                    for (int e = 0; e < actual.Events.Count; e++)
                        if (ReplayEventComparison.Canonical(actual.Events.Events[e]) != ReplayEventComparison.Canonical(expected.Events[e]))
                            throw new InvalidOperationException("MATCHUP_EVENT_DEVIATION: " + input.Id + " tick=" + expected.Tick);
                    // Disk events are StoredReplayEvent records. Metrics must use the newly committed typed events.
                    metrics.Step(sim, before, actual.Events.Events); metrics.Decisions(comp, sim.Tick,
                        line => EqualLine(candidates, line), line => EqualLine(decisions, line));
                    foreach (var e in actual.Events.Events) EqualLine(events, Csv(sim.Tick, ReplayEventComparison.Canonical(e)));
                }
                if (candidates.ReadLine() != null || decisions.ReadLine() != null || events.ReadLine() != null)
                    throw new InvalidOperationException("MATCHUP_DIAGNOSTIC_TRAILING_ROWS");
                WriteMetrics(path, input, sim, metrics, true);
                lock (source) { cases++; ticks += replay.Records.Count; }
                Debug.Log("MATCHUP_AUDITED " + input.Id + " ticks=" + replay.Records.Count);
            });
            File.WriteAllText(Root + (reference ? "/unity-reference-audit.txt" : "/disk-audit.txt"),
                $"cases={cases};ticks={ticks};eventsAndSnapshots=True;candidatesAndDecisions=True;metricsRebuilt=True\n", Encoding.UTF8);
        }
        static int Workers
        {
            get
            {
#if PROJECT_HERO_STANDALONE
                return int.TryParse(Environment.GetEnvironmentVariable("HERO_MATCHUP_WORKERS"), out int value) ? Math.Max(1, Math.Min(16, value)) : 8;
#else
                return 4;
#endif
            }
        }
        static void EqualLine(StreamReader reader, string expected)
        { if (reader.ReadLine() != expected) throw new InvalidOperationException("MATCHUP_DIAGNOSTIC_CHANGED"); }
        static void CheckOrWrite(string path, string value, bool compare)
        {
            if (compare) { if (File.ReadAllText(path) != value) throw new InvalidOperationException("MATCHUP_METRICS_CHANGED: " + path); }
            else File.WriteAllText(path, value, Encoding.UTF8);
        }
        static string SourceFingerprint() => Sha(Encoding.UTF8.GetBytes(string.Concat(new[] { "Assets/Scripts", "Assets/Tests", "Assets/Diagnostics" }
            .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)).Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => p + ":" + Sha(File.ReadAllBytes(p)) + "\n"))));
        static string Sha(byte[] bytes) { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static StreamWriter Gzip(string path) => new(new GZipStream(File.Create(path), CompressionLevel.Optimal), Encoding.UTF8);
        static StreamReader ReadGzip(string path) => new(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), Encoding.UTF8);
        static string Csv(params object[] values) => string.Join(",", values.Select(v => "\"" + Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\""));
    }
}
