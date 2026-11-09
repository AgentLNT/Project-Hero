using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Units;
using UnityEditor;
using UnityEngine;

namespace ProjectHero.Editor
{
    [Serializable]
    public sealed class RecoveryExperimentInput
    {
        public string kind = "recovery-scan", id, scenarioVersion, scriptVersion, numericalVersion,
            rulesVersion, definitionHash, sourceFingerprint, seed;
        public int recoveryTicks, pair, response, bluntOwner, firstImpact, growth, health, stabilityQ10, maxTicks;
        public bool swapSpawns, shortRange, wall;
        public RecoveryExperimentScenario Scenario() => new RecoveryExperimentScenario(id, recoveryTicks,
            (RecoveryPair)pair, (RecoveryResponse)response, bluntOwner, swapSpawns, firstImpact, growth, health,
            stabilityQ10, shortRange, wall, maxTicks, ulong.Parse(seed, CultureInfo.InvariantCulture));
        public BattleDefinition Definition()
        {
            if (scenarioVersion != RecoveryExperimentScenario.Version || scriptVersion != RecoveryExperimentScenario.ScriptVersion
                || numericalVersion != CombatValuesV05.Version) throw new InvalidOperationException("Recovery experiment version mismatch");
            var definition = RecoveryExperimentRunner.BuildDefinition(Scenario());
            if (definition.RulesVersion != rulesVersion || definition.BattleDefinitionHashValue != definitionHash)
                throw new InvalidOperationException("Recovery experiment definition mismatch");
            return definition;
        }
        public static RecoveryExperimentInput From(RecoveryExperimentResult r, string fingerprint)
        {
            var s = r.Scenario;
            return new RecoveryExperimentInput { id = s.Id, scenarioVersion = RecoveryExperimentScenario.Version,
                scriptVersion = RecoveryExperimentScenario.ScriptVersion, numericalVersion = CombatValuesV05.Version,
                rulesVersion = r.Definition.RulesVersion, definitionHash = r.Definition.BattleDefinitionHashValue,
                sourceFingerprint = fingerprint, seed = s.Seed.ToString(CultureInfo.InvariantCulture), recoveryTicks = s.RecoveryTicks,
                pair = (int)s.Pair, response = (int)s.Response, bluntOwner = s.BluntOwner, swapSpawns = s.SwapSpawns,
                firstImpact = s.FirstImpact, growth = s.Growth, health = s.Health, stabilityQ10 = s.StabilityQ10,
                shortRange = s.ShortRange, wall = s.Wall, maxTicks = s.MaxTicks };
        }
    }

    public static class RecoveryExperimentTools
    {
        public static string EvidenceRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "优化任务", "执行记录", "后摇多档验证-2026-10-29"));

        [MenuItem("Project Hero/Combat Experiments/Recovery Coarse Scan")]
        public static void RunCoarse() => RunBatch("coarse", RecoveryExperimentRunner.CoarseTicks);

        // Explicit command-line integers, e.g. -recoveryTicks 31,32,34,35. No implicit 36-Tick winner.
        public static void RunSelected()
        {
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-recoveryTicks");
            if (index < 0 || index + 1 >= args.Length) throw new InvalidOperationException("Pass -recoveryTicks 31,32,...");
            var values = args[index + 1].Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            RunBatch("fine", values);
        }

        public static void RunSmoke()
            => RunCases("smoke", RecoveryExperimentRunner.Matrix(new[] { 30, 36, 60 }).Where(s =>
                s.Growth == 0 && s.Health == 24 && s.StabilityQ10 == 10240 && !s.SwapSpawns
                && !s.ShortRange && s.OpeningImpact <= 273).ToArray());

        static void RunBatch(string stage, int[] values)
        {
            if (values.Length == 0 || values.Any(v => v < 30 || v > 330)) throw new InvalidOperationException("Invalid recovery sweep");
            RunCases(stage, RecoveryExperimentRunner.Matrix(values));
        }

        static void RunCases(string stage, IReadOnlyList<RecoveryExperimentScenario> cases)
        {
            string folder = Path.Combine(EvidenceRoot, stage + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z");
            Directory.CreateDirectory(folder); string fingerprint = SourceFingerprint();
            File.WriteAllText(Path.Combine(EvidenceRoot, "latest-" + stage + ".txt"), Path.GetFileName(folder), Encoding.UTF8);
            int failures = 0, completed = 0;
            using (var summary = new StreamWriter(Path.Combine(folder, "summary.csv"), false, Encoding.UTF8))
            {
                string[] columns = null;
                foreach (var scenario in cases)
                {
                    try
                    {
                        var result = RecoveryExperimentRunner.Run(scenario);
                        Save(folder, result, fingerprint);
                        VerifySaved(Path.Combine(folder, scenario.Id));
                        var values = Metrics(result, fingerprint);
                        if (columns == null) { columns = values.Keys.ToArray(); summary.WriteLine(string.Join(",", columns.Select(Csv))); }
                        summary.WriteLine(string.Join(",", columns.Select(c => Csv(values[c])))); summary.Flush();
                    }
                    catch (Exception error)
                    {
                        failures++; File.WriteAllText(Path.Combine(folder, scenario.Id + ".failure.txt"), error.ToString(), Encoding.UTF8);
                        Debug.LogException(error);
                    }
                    completed++;
                    if (completed % 25 == 0 || completed == cases.Count)
                        File.WriteAllText(Path.Combine(folder, "progress.json"), "{\"completed\":" + completed
                            + ",\"total\":" + cases.Count + ",\"failed\":" + failures + "}", Encoding.UTF8);
                }
            }
            Debug.Log("Recovery " + stage + ": " + completed + "/" + cases.Count + "; failures=" + failures + "; " + folder);
            if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
            else if (failures > 0) throw new InvalidOperationException("Recovery experiment failures: " + failures);
        }

        public static Dictionary<string, string> Metrics(RecoveryExperimentResult r, string fingerprint)
        {
            var s = r.Scenario; long b = s.BluntOwner, o = 3 - b;
            var clashes = r.Events.OfType<ClashParticipantResolvedEvent>().Select(e => e.Tick).Distinct().OrderBy(t => t).ToArray();
            long first = clashes.DefaultIfEmpty(-1L).First();
            bool IsAttack(RecoveryActionTrace a) => a.Type == (int)ActionType.Attack;
            long Received(long unit, bool? residual = null) => r.Diagnostics.Where(t => !residual.HasValue || clashes.Contains(t.Tick) == residual.Value)
                .Sum(t => t.Damage.Units.Where(d => d.UnitId.Value == unit).Sum(d => d.DamageQ10));
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            void Put(string key, object value) => map.Add(key, Convert.ToString(value, CultureInfo.InvariantCulture));
            Put("scenario", s.Id); Put("pair", s.Pair); Put("response", s.Response); Put("recovery_ticks", s.RecoveryTicks);
            Put("delta", s.RecoveryTicks - 30); Put("blunt_owner", b); Put("swap_spawns", s.SwapSpawns);
            Put("opening_impact", s.OpeningImpact); Put("growth", s.Growth); Put("health", s.Health); Put("stability_q10", s.StabilityQ10);
            Put("short_range", s.ShortRange); Put("wall", s.Wall); Put("max_ticks", s.MaxTicks); Put("seed", s.Seed);
            Put("outcome", r.Outcome); Put("end_tick", r.FinalSnapshot.Tick); Put("first_clash", first);
            Put("clashes", clashes.Length); Put("post_first_clashes", Math.Max(0, clashes.Length - 1));
            long firstNonclash = r.Actions.Where(a => IsAttack(a) && a.ExecutedImpact > first && !clashes.Contains(a.ExecutedImpact))
                .Select(a => a.ExecutedImpact).DefaultIfEmpty(long.MaxValue).Min();
            Put("consecutive_clashes", clashes.Count(t => t < firstNonclash));
            foreach (var role in new[] { ("blunt", b), ("opponent", o) })
            {
                string p = role.Item1; long owner = role.Item2;
                var own = r.Actions.Where(a => a.Owner == owner).ToArray();
                var nextAttack = own.Where(a => IsAttack(a) && a.Locked > first).OrderBy(a => a.Locked).FirstOrDefault();
                var unit = r.FinalSnapshot.Units.Single(u => u.UnitId == owner);
                Put(p + "_health_q10", unit.HealthQ10); Put(p + "_received_q10", Received(owner));
                Put(p + "_dealt_q10", Received(3 - owner)); Put(p + "_direct_dealt_q10", Received(3 - owner, false));
                Put(p + "_residual_dealt_q10", Received(3 - owner, true));
                Put(p + "_first_residual_q10", r.Diagnostics.Where(t => t.Tick == first).Sum(t => t.Damage.Units.Where(d => d.UnitId.Value != owner).Sum(d => d.DamageQ10)));
                Put(p + "_attack_starts", own.Count(a => IsAttack(a) && a.Locked >= 0));
                Put(p + "_window2_attacks", own.Count(a => IsAttack(a) && a.Locked >= 0 && a.Window == 2));
                Put(p + "_next_attack_start", nextAttack?.Start ?? -1); Put(p + "_next_attack_impact", nextAttack?.ExecutedImpact ?? -1);
                Put(p + "_next_scheduled_impact", nextAttack?.Impact ?? -1); Put(p + "_next_source_window", nextAttack?.Window ?? 0);
                Put(p + "_control_ticks", r.Frames.Count(f => f.Unit == owner && (f.State == (int)UnitState.Staggered
                    || f.State == (int)UnitState.KnockedDown || f.State == (int)UnitState.Recovering)));
                Put(p + "_cancelled_windups", own.Count(a => IsAttack(a) && a.Locked >= 0 && a.Terminal >= 0 && a.Terminal < a.Impact));
                Put(p + "_guard_starts", own.Count(a => a.Type == (int)ActionType.Guard && a.Locked >= 0));
                Put(p + "_move_starts", own.Count(a => a.Type == (int)ActionType.Move && a.Locked >= 0));
                Put(p + "_block_uses", r.Events.OfType<ReactionTriggeredEvent>().Count(e => e.DefenderUnitId == owner && e.ReactionType == (int)ActionType.Block));
                Put(p + "_dodge_uses", r.Events.OfType<ReactionTriggeredEvent>().Count(e => e.DefenderUnitId == owner && e.ReactionType == (int)ActionType.Dodge));
                Put(p + "_resource_consumed", r.Events.OfType<AdrenalineLedgerChangedEvent>().Where(e => e.UnitId.Value == owner && e.ChangeKind == AdrenalineChangeKind.Consumed).Sum(e => e.ReservationAmount));
                Put(p + "_cycle_loss", r.Events.OfType<AdrenalineLedgerChangedEvent>().Where(e => e.UnitId.Value == owner && e.ChangeKind == AdrenalineChangeKind.CycleReset).Sum(e => e.AvailableBefore));
                Put(p + "_no_resource", r.Decisions.Count(e => e.UnitId == owner && e.Reason == "no-resource"));
                Put(p + "_no_opportunity", r.Decisions.Count(e => e.UnitId == owner && e.Reason == "no-published-opportunity"));
                Put(p + "_no_dodge", r.Decisions.Count(e => e.UnitId == owner && e.Reason == "no-safe-or-legal-dodge"));
                Put(p + "_no_reaction_timing", r.Decisions.Count(e => e.UnitId == owner && e.Reason == "no-legal-reaction-timing"));
                Put(p + "_no_permission", r.Decisions.Count(e => e.UnitId == owner && (e.Reason == "no-submission-permission" || e.Reason == "start-outside-current-window")));
                Put(p + "_final_x", unit.X); Put(p + "_final_y", unit.Y);
            }
            Put("guard_contacts", r.Events.OfType<GuardPartiallyResistedEvent>().Count());
            Put("dodge_avoids", r.Events.OfType<DodgeResolvedEvent>().Count(e => e.RewardedSuccess));
            Put("block_contacts", r.Events.OfType<BlockResolvedEvent>().Count(e => e.RewardsBlock));
            Put("concurrent_activations", r.Events.OfType<ConcurrentActionActivatedEvent>().Count());
            Put("command_rejections", r.Events.OfType<CommandRejectedEvent>().Count());
            Put("replay_verified", true); Put("definition_hash", r.Definition.BattleDefinitionHashValue);
            Put("rules_version", r.Definition.RulesVersion); Put("scenario_version", RecoveryExperimentScenario.Version);
            Put("script_version", RecoveryExperimentScenario.ScriptVersion); Put("source_fingerprint", fingerprint);
            return map;
        }

        static void Save(string folder, RecoveryExperimentResult r, string fingerprint)
        {
            string path = Path.Combine(folder, r.Scenario.Id);
            File.WriteAllText(path + ".input.json", JsonUtility.ToJson(RecoveryExperimentInput.From(r, fingerprint), true), Encoding.UTF8);
            using (var stream = File.Create(path + ".heroReplay")) ReplayFile.Save(stream, r.Replay);
            using (var writer = new StreamWriter(path + ".events.jsonl", false, Encoding.UTF8))
                foreach (var e in r.Events) writer.WriteLine("{\"tick\":" + e.Tick + ",\"sequence\":" + e.Sequence
                    + ",\"event\":" + CombatExperimentTools.Json(ReplayEventComparison.Canonical(e)) + "}");
            using (var writer = new StreamWriter(path + ".decisions.jsonl", false, Encoding.UTF8))
                foreach (var e in r.Decisions) writer.WriteLine("{\"tick\":" + e.Tick + ",\"unit\":" + e.UnitId
                    + ",\"reason\":" + CombatExperimentTools.Json(e.Reason) + ",\"action\":" + CombatExperimentTools.Json(e.Action)
                    + ",\"ingress\":" + CombatExperimentTools.Json(e.IngressRejection) + "}");
            using (var writer = new StreamWriter(path + ".actions.csv", false, Encoding.UTF8))
            {
                writer.WriteLine("plan,unit,action,type,start,impact,end,source_window,locked,executed_impact,terminal,reason");
                foreach (var a in r.Actions) writer.WriteLine(string.Join(",", new object[] {
                    a.PlanId,a.Owner,a.Action,a.Type,a.Start,a.Impact,a.End,a.Window,a.Locked,a.ExecutedImpact,a.Terminal,a.Reason }));
            }
            using (var writer = new StreamWriter(path + ".frames.csv", false, Encoding.UTF8))
            {
                writer.WriteLine("tick,unit,health_q10,state,state_end,x,y,available,reserved,window,window_owner,concurrent");
                foreach (var f in r.Frames) writer.WriteLine(string.Join(",", new object[] {
                    f.Tick,f.Unit,f.HealthQ10,f.State,f.StateEnd,f.X,f.Y,f.Available,f.Reserved,f.Window,f.WindowOwner,f.Concurrent }));
            }
            using (var writer = new StreamWriter(path + ".damage-income.csv", false, Encoding.UTF8))
            {
                writer.WriteLine("tick,unit,actual_health_loss_q10,actual_dealt_q10,actual_received_q10");
                foreach (var tick in r.Diagnostics) foreach (var income in tick.Income)
                    writer.WriteLine(string.Join(",", new long[] { tick.Tick,income.UnitId.Value,
                        tick.Damage.Units.FirstOrDefault(d => d.UnitId == income.UnitId)?.DamageQ10 ?? 0,
                        income.TotalFinalDamageDealtQ10,income.TotalFinalDamageReceivedQ10 }));
            }
        }
        public static void VerifySaved(string prefix)
        {
            var input = JsonUtility.FromJson<RecoveryExperimentInput>(File.ReadAllText(prefix + ".input.json"));
            var definition = input.Definition();
            using var file = File.OpenRead(prefix + ".heroReplay");
            using var player = new ReplayPlayer(definition);
            player.Load(ReplayFile.Load(file, definition)); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            if (player.Deviation != null || !player.IsComplete)
                throw new InvalidOperationException("Exported recovery input/replay mismatch: " + input.id + "|" + player.Deviation);
        }
        static string Csv(string value) => value != null && (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            ? "\"" + value.Replace("\"", "\"\"") + "\"" : value ?? "";
        public static string SourceFingerprint()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..")); var text = new StringBuilder();
            foreach (string file in Directory.GetFiles(Path.Combine(root, "Assets", "Scripts"), "*.cs", SearchOption.AllDirectories).OrderBy(v => v, StringComparer.Ordinal))
            {
                string relative = file.Substring(root.Length).Replace('\\', '/');
                text.Append(relative.Length).Append(':').Append(relative).Append('\n').Append(File.ReadAllText(file, Encoding.UTF8)).Append('\n');
            }
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())).Select(v => v.ToString("x2")));
        }
    }
}
