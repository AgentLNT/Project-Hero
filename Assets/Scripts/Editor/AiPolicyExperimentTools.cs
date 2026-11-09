using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Replay;
using UnityEditor;
using UnityEngine;

namespace ProjectHero.Editor
{
    public static class AiPolicyExperimentTools
    {
        public const string Root = "优化任务/执行记录/规则AI阶段三-2026-11-03";
        [Serializable] public sealed class SavedInput
        {
            public string scenarioVersion, policyVersion, sourceFingerprint, definitionHash;
            public int policy, situation, seed, maxTicks;
            public bool swapSpawns;
            public AiPolicyExperimentInput ToInput() => new AiPolicyExperimentInput((AiPolicyKind)policy,
                (AiPolicySituation)situation, checked((ulong)seed), swapSpawns, maxTicks);
        }

        [MenuItem("Tools/Project Hero/Experiments/Stage 3 AI mechanisms")]
        public static void Run()
        {
            string folder = Path.Combine(Root, "cases"); Directory.CreateDirectory(folder);
            string fingerprint = SourceFingerprint(); int count = 0;
            using var summary = new StreamWriter(Path.Combine(Root, "summary.csv"), false, Encoding.UTF8);
            summary.WriteLine("case,policy,situation,swap,seed,definitionHash,selectedAttack,selectedGuard,selectedMove,selectedReaction,waitTicks,rejectedCommands,firstChoice,finalHealth1Q10,finalHealth2Q10,replayTicks");
            foreach (AiPolicyKind policy in Enum.GetValues(typeof(AiPolicyKind)))
                foreach (AiPolicySituation situation in Enum.GetValues(typeof(AiPolicySituation)))
                    foreach (bool swap in policy == AiPolicyKind.Legacy ? new[] { false } : new[] { false, true })
                    foreach (int seed in policy != AiPolicyKind.Legacy && situation == AiPolicySituation.VisibleThreat ? new[] { 31, 97 } : new[] { 31 })
                    {
                        var input = new AiPolicyExperimentInput(policy, situation, Seed: (ulong)seed, SwapSpawns: swap);
                        var result = AiPolicyExperiment.Run(input);
                        string id = policy + "-" + situation + "-" + (swap ? "swapped" : "normal") + "-" + seed;
                        string path = Path.Combine(folder, id);
                        var dto = new SavedInput { scenarioVersion = AiPolicyExperiment.Version, policyVersion = AiTacticalPolicy.Version,
                            sourceFingerprint = fingerprint, definitionHash = result.Definition.BattleDefinitionHashValue,
                            policy = (int)policy, situation = (int)situation, seed = seed, maxTicks = input.MaxTicks, swapSpawns = swap };
                        File.WriteAllText(path + ".input.json", JsonUtility.ToJson(dto, true), Encoding.UTF8);
                        using (var file = File.Create(path + ".heroReplay")) ReplayFile.Save(file, result.Replay);
                        File.WriteAllLines(path + ".events.txt", result.Events.Select(ReplayEventComparison.Canonical), Encoding.UTF8);
                        using (var choices = new StreamWriter(path + ".choices.csv", false, Encoding.UTF8))
                        {
                            choices.WriteLine("tick,policy,hasSelection,waitReason,selection,normalCandidateCount,reactionCandidateCount");
                            foreach (var decision in result.Decisions)
                                choices.WriteLine(Csv(decision.DecisionTick, policy, decision.HasSelection, decision.NoSelectionReason,
                                    decision.Evaluations.FirstOrDefault(e => e.Chosen)?.Key ?? SelectionKey(decision),
                                    decision.NormalCandidateCount, decision.ReactionCandidateCount));
                        }
                        using (var details = new StreamWriter(path + ".decisions.csv", false, Encoding.UTF8))
                        {
                            details.WriteLine("tick,key,type,reaction,chosen,score,rejection,reason,damageDeltaQ10,incomingDeltaQ10,positionProgress,interrupted,clashes,cost,start,impact,end,horizon");
                            foreach (var decision in result.Decisions)
                            {
                                if (decision.Evaluations.Count == 0)
                                    details.WriteLine(Csv(decision.DecisionTick, SelectionKey(decision), SelectedType(decision),
                                        decision.Selected?.Payload is Logic.Commands.ReactionCommandPayload, decision.HasSelection, "", "",
                                        decision.HasSelection ? "LEGACY_SEEDED_BASELINE" : decision.NoSelectionReason));
                                foreach (var e in decision.Evaluations)
                                    details.WriteLine(Csv(decision.DecisionTick, e.Key, e.Type, e.IsReaction, e.Chosen, e.Score, e.RejectionCode,
                                        e.Reason, e.DamageDeltaQ10, e.IncomingDeltaQ10, e.PositionProgress, e.InterruptedThreats,
                                        e.Clashes, e.Cost, e.StartTick, e.ImpactTick, e.EndTick, e.HorizonTick));
                            }
                        }
                        var chosen = result.Decisions.SelectMany(d => d.Evaluations).Where(e => e.Chosen).ToArray();
                        summary.WriteLine(Csv(id, policy, situation, swap, input.Seed, result.Definition.BattleDefinitionHashValue,
                            result.Decisions.Count(d => SelectedType(d) == "Attack"), result.Decisions.Count(d => SelectedType(d) == "Guard"),
                            result.Decisions.Count(d => SelectedType(d) == "Move"), result.Decisions.Count(d => d.Selected?.Payload is Logic.Commands.ReactionCommandPayload),
                            result.Decisions.Count(d => !d.HasSelection), result.Events.OfType<CommandRejectedEvent>().Count(),
                            chosen.FirstOrDefault()?.Key ?? result.Decisions.Where(d => d.HasSelection).Select(SelectionKey).FirstOrDefault(), result.FinalUnits.Single(u => u.UnitId == 1).HealthQ10,
                            result.FinalUnits.Single(u => u.UnitId == 2).HealthQ10, result.Replay.Records.Count)); summary.Flush();
                        VerifySaved(path, fingerprint); count++;
                        File.WriteAllText(Path.Combine(Root, "progress.json"), "{\"completed\":" + count + ",\"total\":62}", Encoding.UTF8);

                        string SelectedType(AiDecision decision)
                        {
                            if (decision.Selected?.Payload is Logic.Commands.ReactionCommandPayload reaction)
                                return result.Definition.FindAction(reaction.ReactionActionSpecId).Type.ToString();
                            if (decision.Selected?.Payload is Logic.Commands.ScheduleEditPayload edits && edits.Operations[0] is Logic.Commands.AddOrdinaryPlanOperation add)
                                return result.Definition.FindAction(add.ActionSpecId).Type.ToString();
                            return decision.HasSelection ? "Edit" : "";
                        }
                        string SelectionKey(AiDecision decision) => decision.Selected?.Payload is Logic.Commands.ReactionCommandPayload reaction
                            ? reaction.ReactionActionSpecId.Value : decision.Selected?.Payload is Logic.Commands.ScheduleEditPayload edits
                                && edits.Operations[0] is Logic.Commands.AddOrdinaryPlanOperation add ? add.ActionSpecId.Value : "";
                    }
            summary.Dispose();
            File.WriteAllText(Path.Combine(Root, "source-fingerprint.txt"), fingerprint, Encoding.UTF8);
            File.WriteAllText(Path.Combine(Root, "native-generation.txt"), "cases=" + count + "; saved-input-reconstruction+per-tick-replay=passed", Encoding.UTF8);
            Debug.Log("Stage 3 AI: " + count + " cases, saved-input replay verified.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        public static void AuditSaved()
        {
            string fingerprint = SourceFingerprint(); int count = 0;
            foreach (string input in Directory.GetFiles(Path.Combine(Root, "cases"), "*.input.json").OrderBy(p => p, StringComparer.Ordinal))
            { VerifySaved(input.Substring(0, input.Length - ".input.json".Length), fingerprint); count++; }
            if (count != 62) throw new InvalidOperationException("AI_CASE_COUNT: " + count);
            File.WriteAllText(Path.Combine(Root, "native-disk-replay-audit.txt"), "fresh Unity process; cases=" + count
                + "; sourceFingerprint=" + fingerprint + "; definition rebuild + per-tick events/snapshots + regenerated AI=passed", Encoding.UTF8);
            Debug.Log("Stage 3 AI saved audit passed: " + count);
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        private static void VerifySaved(string path, string fingerprint)
        {
            var dto = JsonUtility.FromJson<SavedInput>(File.ReadAllText(path + ".input.json", Encoding.UTF8));
            if (dto.scenarioVersion != AiPolicyExperiment.Version || dto.policyVersion != AiTacticalPolicy.Version
                || dto.sourceFingerprint != fingerprint) throw new InvalidOperationException("AI_INPUT_VERSION_MISMATCH");
            var definition = AiPolicyExperiment.Build(dto.ToInput());
            if (definition.BattleDefinitionHashValue != dto.definitionHash) throw new InvalidOperationException("AI_DEFINITION_HASH_MISMATCH");
            using var file = File.OpenRead(path + ".heroReplay"); using var player = new ReplayPlayer(definition);
            player.Load(ReplayFile.Load(file, definition)); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            if (!player.IsComplete || player.Deviation != null) throw new InvalidOperationException("AI_DISK_REPLAY: " + player.Deviation);
        }

        public static string SourceFingerprint()
        {
            var text = new StringBuilder();
            foreach (string path in Directory.GetFiles("Assets/Scripts", "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("Assets/Tests", "*.cs", SearchOption.AllDirectories)).OrderBy(p => p.Replace('\\', '/'), StringComparer.Ordinal))
            { using var sha = SHA256.Create(); text.Append(path.Replace('\\', '/')).Append(':').Append(Hex(sha.ComputeHash(File.ReadAllBytes(path)))).Append('\n'); }
            using var final = SHA256.Create(); return Hex(final.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        private static string Csv(params object[] values) => string.Join(",", values.Select(v => "\"" + Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\""));
    }
}
