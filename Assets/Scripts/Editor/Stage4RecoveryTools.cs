using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Replay;
using UnityEditor;
using UnityEngine;

namespace ProjectHero.Editor
{
    public static class Stage4RecoveryTools
    {
        public const string Root = "优化任务/执行记录/阶段四后摇策略对照-2026-11-03";
        public const string CorrectedRoot = Root + "/corrected-preview-v2";
        [Serializable] public sealed class SavedInput
        {
            public string sceneVersion, policyVersion, sourceFingerprint, frozenLogicFingerprint, definitionHash;
            public int layout, recovery, bluntPolicy, opponentPolicy, seed, windowTicks, maxTicks;
            public bool opponentFirst, swapSpawns;
            public Stage4Input ToInput() => new Stage4Input((Stage4Layout)layout, recovery, (AiPolicyKind)bluntPolicy,
                (AiPolicyKind)opponentPolicy, opponentFirst, swapSpawns, checked((ulong)seed), windowTicks);
        }

        public static void RunSmoke()
        {
            var all = Stage4Matrix.All();
            var cases = all.Where(s => (s.RecoveryTicks == 31 || s.RecoveryTicks == 40) && !s.OpponentFirst && !s.SwapSpawns && s.Seed == 31 && s.WindowTicks == 180
                && ((s.Layout == Stage4Layout.ProbeOpen && s.BluntPolicy == AiPolicyKind.SustainedOffense && s.OpponentPolicy == AiPolicyKind.SustainedOffense)
                    || s.Layout == Stage4Layout.TeamSweepOpen || s.Layout == Stage4Layout.HeavyQuickWall)).ToArray();
            RunCases("smoke", cases);
        }

        public static void RunClashSmoke() => RunCases("clash-smoke", Stage4Matrix.All().Where(s =>
            s.Layout == Stage4Layout.HeavyQuickOpen && s.BluntPolicy == AiPolicyKind.SustainedOffense && s.OpponentPolicy == AiPolicyKind.SeekInterrupt
            && (s.RecoveryTicks == 31 || s.RecoveryTicks == 40) && !s.OpponentFirst && !s.SwapSpawns).ToArray());

        // Eight deterministic shards allow independent restarts without silently skipping failures.
        public static void RunShard()
        {
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-stage4Shard");
            if (index < 0 || index + 1 >= args.Length) throw new InvalidOperationException("Pass -stage4Shard 0..7");
            int shard = int.Parse(args[index + 1], CultureInfo.InvariantCulture);
            if (shard < 0 || shard >= 8) throw new InvalidOperationException("STAGE4_SHARD_INVALID");
            RunCases("cases", Stage4Matrix.All().Where((s, n) => n % 8 == shard).ToArray(), shard);
        }

        public static IReadOnlyList<Stage4Input> CorrectedCases() => Stage4Matrix.All().Where(s =>
            s.Seed == 31 && s.WindowTicks == 180 && Stage4Matrix.CoreTicks.Contains(s.RecoveryTicks)
            && ((s.Layout == Stage4Layout.ProbeOpen && s.BluntPolicy == AiPolicyKind.SustainedOffense && s.OpponentPolicy == AiPolicyKind.SustainedOffense)
                || (s.Layout == Stage4Layout.HeavyQuickOpen && s.BluntPolicy == AiPolicyKind.SustainedOffense && s.OpponentPolicy == AiPolicyKind.SeekInterrupt)
                || (s.Layout == Stage4Layout.HeavyQuickWall && s.BluntPolicy == AiPolicyKind.CautiousDefense && s.OpponentPolicy == AiPolicyKind.SustainedOffense)
                || (s.Layout == Stage4Layout.TeamSweepWall && s.BluntPolicy == AiPolicyKind.CautiousDefense && s.OpponentPolicy == AiPolicyKind.SeekInterrupt))).ToArray();

        public static void RunCorrectedShard()
        {
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-stage4Shard");
            if (index < 0 || index + 1 >= args.Length) throw new InvalidOperationException("Pass -stage4Shard 0..3");
            int shard = int.Parse(args[index + 1], CultureInfo.InvariantCulture);
            if (shard < 0 || shard >= 4 || CorrectedCases().Count != 96) throw new InvalidOperationException("STAGE4_CORRECTED_MATRIX_INVALID");
            RunCases("cases", CorrectedCases().Where((s, n) => n % 4 == shard).ToArray(), shard, CorrectedRoot);
        }

        static void RunCases(string stage, IReadOnlyList<Stage4Input> cases, int shard = -1, string artifactRoot = Root)
        {
            string folder = Path.Combine(artifactRoot, stage); Directory.CreateDirectory(folder);
            string source = AiPolicyExperimentTools.SourceFingerprint(), frozen = FrozenFingerprint(artifactRoot);
            using (var matrix = new StreamWriter(Path.Combine(artifactRoot, "matrix.csv"), false, Encoding.UTF8))
            {
                matrix.WriteLine("case,block,layout,recovery,bluntPolicy,opponentPolicy,opponentFirst,swapSpawns,seed,windowTicks,maxTicks,holdout,policyVersion");
                foreach (var s in artifactRoot == CorrectedRoot ? CorrectedCases() : Stage4Matrix.All()) matrix.WriteLine(Csv(s.Id, s.Block, s.Layout, s.RecoveryTicks, s.BluntPolicy,
                    s.OpponentPolicy, s.OpponentFirst, s.SwapSpawns, s.Seed, s.WindowTicks, s.MaxTicks, s.Holdout, AiTacticalPolicy.Version));
            }
            int completed = 0; var progressGate = new object();
            try
            {
            Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = 4 }, input =>
            {
                string path = Path.Combine(folder, input.Id);
                try
                {
                    using var decisions = Gzip(path + ".decisions.csv.gz");
                    using var candidates = Gzip(path + ".candidates.csv.gz");
                    using var events = Gzip(path + ".events.txt.gz");
                    decisions.WriteLine("tick,controller,side,window,windowOwner,ownWindow,publicThreats,available,reserved,noDodgeDestinations,selected,waitReason,normalCount,reactionCount,eligible,resourceBlocked,chosenKey");
                    candidates.WriteLine("tick,controller,key,type,reaction,chosen,score,rejection,reason,damageDeltaQ10,incomingDeltaQ10,positionProgress,interrupts,clashes,cost,start,impact,end,horizon");
                    var result = Stage4RecoveryExperiment.Run(input, frame =>
                    {
                        var d = frame.Decision;
                        decisions.WriteLine(Csv(frame.Tick, frame.Controller, frame.BluntSide ? "A" : "B", frame.WindowId, frame.WindowOwner,
                            frame.OwnWindow, frame.PublicThreats, frame.Available, frame.Reserved, frame.NoDodgeDestinations, d.HasSelection,
                            d.NoSelectionReason, d.NormalCandidateCount, d.ReactionCandidateCount, d.Evaluations.Count(e => e.RejectionCode == null),
                            d.Evaluations.Count(e => e.RejectionCode == "ADRENALINE_INSUFFICIENT"), d.Evaluations.FirstOrDefault(e => e.Chosen)?.Key ?? d.Selected?.Payload.ToString()));
                        foreach (var e in d.Evaluations)
                            candidates.WriteLine(Csv(d.DecisionTick, frame.Controller, e.Key, e.Type, e.IsReaction, e.Chosen, e.Score,
                                e.RejectionCode, e.Reason, e.DamageDeltaQ10, e.IncomingDeltaQ10, e.PositionProgress, e.InterruptedThreats,
                                e.Clashes, e.Cost, e.StartTick, e.ImpactTick, e.EndTick, e.HorizonTick));
                    }, step => { foreach (var fact in step.Events.Events) events.WriteLine(ReplayEventComparison.Canonical(fact)); });
                    var dto = new SavedInput { sceneVersion = Stage4Input.Version, policyVersion = AiTacticalPolicy.Version,
                        sourceFingerprint = source, frozenLogicFingerprint = frozen, definitionHash = result.Definition.BattleDefinitionHashValue,
                        layout = (int)input.Layout, recovery = input.RecoveryTicks, bluntPolicy = (int)input.BluntPolicy, opponentPolicy = (int)input.OpponentPolicy,
                        seed = checked((int)input.Seed), windowTicks = input.WindowTicks, maxTicks = input.MaxTicks, opponentFirst = input.OpponentFirst, swapSpawns = input.SwapSpawns };
                    File.WriteAllText(path + ".input.json", JsonUtility.ToJson(dto, true), Encoding.UTF8);
                    using (var file = File.Create(path + ".heroReplay")) ReplayFile.Save(file, result.Replay);
                    using (var plans = new StreamWriter(path + ".plans.csv", false, Encoding.UTF8))
                    {
                        plans.WriteLine("plan,owner,spec,type,window,created,start,impact,end,locked,executedImpact,terminated,terminalReason");
                        foreach (var p in result.Plans) plans.WriteLine(Csv(p.Id, p.Owner, p.Spec, p.Type, p.Window, p.Created, p.Start, p.Impact, p.End,
                            p.Locked, p.ExecutedImpact, p.Terminated, p.TerminalReason));
                    }
                    var metrics = Metrics(result, source, frozen);
                    using (var output = new StreamWriter(path + ".metrics.csv", false, Encoding.UTF8))
                    { output.WriteLine(string.Join(",", metrics.Keys)); output.WriteLine(Csv(metrics.Values.Cast<object>().ToArray())); }
                    VerifySaved(path, source, frozen);
                    lock (progressGate)
                    {
                        completed++;
                        File.WriteAllText(Path.Combine(artifactRoot, stage + "-shard" + shard + "-progress.json"),
                            "{\"completed\":" + completed + ",\"total\":" + cases.Count + ",\"last\":\"" + input.Id + "\"}", Encoding.UTF8);
                    }
                }
                catch (Exception error)
                { File.WriteAllText(path + ".failure.txt", error.ToString(), Encoding.UTF8); throw; }
            });
            }
            catch (Exception error) { Debug.LogException(error); if (Application.isBatchMode) EditorApplication.Exit(1); throw; }
            File.WriteAllText(Path.Combine(artifactRoot, stage + "-shard" + shard + "-complete.txt"),
                "cases=" + completed + "; replay=passed; source=" + source + "; frozen=" + frozen, Encoding.UTF8);
            Debug.Log("Stage4 " + stage + " shard " + shard + ": " + completed + " passed");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        public static void AuditSaved() => Audit(Root, Stage4Matrix.All());
        public static void AuditCorrectedSaved() => Audit(CorrectedRoot, CorrectedCases());
        static void Audit(string artifactRoot, IReadOnlyList<Stage4Input> cases)
        {
            string source = AiPolicyExperimentTools.SourceFingerprint(), frozen = FrozenFingerprint(artifactRoot); int count = 0; long ticks = 0;
            Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = 4 }, s =>
            { Interlocked.Add(ref ticks, VerifySaved(Path.Combine(artifactRoot, "cases", s.Id), source, frozen)); Interlocked.Increment(ref count); });
            int files = Directory.GetFiles(Path.Combine(artifactRoot, "cases"), "*.input.json").Length;
            if (count != cases.Count || files != count) throw new InvalidOperationException("STAGE4_AUDIT_CASE_COUNT");
            File.WriteAllText(Path.Combine(artifactRoot, "native-disk-replay-audit.txt"), "fresh Unity process; cases=" + count + "; ticks=" + ticks
                + "; definition rebuild + generated AI + per-tick events/snapshots=passed; source=" + source + "; frozen=" + frozen, Encoding.UTF8);
            Debug.Log("Stage4 independent disk replay: " + count + " passed"); if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static long VerifySaved(string path, string source, string frozen)
        {
            var dto = JsonUtility.FromJson<SavedInput>(File.ReadAllText(path + ".input.json", Encoding.UTF8));
            if (dto.sceneVersion != Stage4Input.Version || dto.policyVersion != AiTacticalPolicy.Version || dto.sourceFingerprint != source
                || dto.frozenLogicFingerprint != frozen || dto.maxTicks != dto.ToInput().MaxTicks) throw new InvalidOperationException("STAGE4_INPUT_VERSION");
            var definition = Stage4RecoveryExperiment.Build(dto.ToInput());
            if (definition.BattleDefinitionHashValue != dto.definitionHash) throw new InvalidOperationException("STAGE4_DEFINITION_HASH");
            using var file = File.OpenRead(path + ".heroReplay"); using var player = new ReplayPlayer(definition);
            var replay = ReplayFile.Load(file, definition); player.Load(replay); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            if (!player.IsComplete || player.Deviation != null) throw new InvalidOperationException("STAGE4_REPLAY: " + player.Deviation);
            return replay.Records.Count;
        }

        public static Dictionary<string, string> Metrics(Stage4Result result, string source, string frozen)
        {
            var s = result.Input; var m = new Dictionary<string, string>(StringComparer.Ordinal);
            void Put(string key, object value) => m.Add(key, Convert.ToString(value, CultureInfo.InvariantCulture));
            Put("case", s.Id); Put("block", s.Block); Put("layout", s.Layout); Put("holdout", s.Holdout); Put("team", s.Team); Put("wall", s.Wall);
            Put("recovery", s.RecoveryTicks); Put("bluntDelta", s.RecoveryTicks - 30); Put("bluntPolicy", s.BluntPolicy); Put("opponentPolicy", s.OpponentPolicy);
            Put("opponentFirst", s.OpponentFirst); Put("swapSpawns", s.SwapSpawns); Put("seed", s.Seed); Put("windowTicks", s.WindowTicks);
            Put("maxTicks", s.MaxTicks); Put("endTick", result.FinalSnapshot.Tick); Put("outcome", result.Outcome);
            Put("firstClashTick", result.FirstClashTick); Put("lastClashTick", result.LastClashTick); Put("commandRejections", result.Rejections.Values.Sum());
            Put("rejectionReasons", string.Join(";", result.Rejections.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)));
            foreach (var side in new[] { ("a", result.Blunt), ("b", result.Opponent) })
                foreach (var field in typeof(Stage4SideMetrics).GetFields().OrderBy(f => f.Name, StringComparer.Ordinal)) Put(side.Item1 + "_" + field.Name, field.GetValue(side.Item2));
            Put("definitionHash", result.Definition.BattleDefinitionHashValue); Put("sourceFingerprint", source); Put("frozenLogicFingerprint", frozen);
            return m;
        }

        static string FrozenFingerprint(string artifactRoot)
        {
            // Freeze exactly the pre-stage-4 runtime paths; new experiment files do not change this hash.
            var json = File.ReadAllText(Path.Combine(artifactRoot, "frozen-existing-logic.json"), Encoding.UTF8);
            var lines = json.Split('\n').Where(l => l.TrimStart().StartsWith("\"Assets/")).ToArray(); var text = new StringBuilder();
            foreach (var line in lines)
            {
                string[] quoted = line.Split('"'); string path = quoted[1], expected = quoted[3];
                using var sha = SHA256.Create(); string actual = Hex(sha.ComputeHash(File.ReadAllBytes(path)));
                if (actual != expected) throw new InvalidOperationException("STAGE4_FROZEN_LOGIC_CHANGED: " + path);
                text.Append(path).Append(':').Append(actual).Append('\n');
            }
            using var final = SHA256.Create(); return Hex(final.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }
        static StreamWriter Gzip(string path) => new StreamWriter(new GZipStream(File.Create(path), System.IO.Compression.CompressionLevel.Fastest), new UTF8Encoding(false));
        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        static string Csv(params object[] values) => string.Join(",", values.Select(v => "\"" + (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\""));
    }
}
