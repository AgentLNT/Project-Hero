using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProjectHero.Authoring;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ProjectHero.Editor
{
    public static class FormalAiBaselineTools
    {
        public const string Root = "优化任务/执行记录/正式动作混合AI-2026-11-05";
        const string Scene = "Assets/Scenes/CombatSampleScene.unity";
        [Serializable] public sealed class Input
        {
            public string sourceHash, definitionHash, contentFingerprint, sourceFingerprint, version;
            public int policy, execution, maxTicks = 900;
            public bool near, mirror;
            public long seed;
            public int metaResource;
        }
        static ProjectHero.Core.Compatibility.Runtime.BattleSimulationSeed LoadSource()
        {
            var scene = EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
            var source = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<NewBattleSimulationSource>(true)).Single();
            return source.BuildSeed() ?? throw new InvalidOperationException(source.LastConfigurationError);
        }
        public static string ContentFingerprint()
        {
            var files = Directory.GetFiles("Assets/Resources", "*", SearchOption.AllDirectories).Append(Scene).OrderBy(p => p, StringComparer.Ordinal);
            var text = new StringBuilder();
            using var sha = SHA256.Create();
            foreach (var path in files) text.Append(path.Replace('\\', '/')).Append(':').Append(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "")).Append('\n');
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
        public static void Run()
        {
            Directory.CreateDirectory(Root + "/cases");
            var source = LoadSource(); string content = ContentFingerprint(), code = AiPolicyExperimentTools.SourceFingerprint();
            File.WriteAllText(Root + "/formal-actions.txt", "source=" + source.BattleDefinitionHash + "\nrules=" + source.RulesVersion + "\n" +
                string.Join("\n", source.Definition.Actions.Select(a => a.ActionSpecId + " | " + a.Type + " | " + a.Timing + " | " + a.Payload)) +
                "\nSETS\n" + string.Join("\n", source.Definition.ActionSets.Select(s => s.ActionSetId + " | " + string.Join(",", s.ActionSpecIds))), Encoding.UTF8);
            using var summary = new StreamWriter(Root + "/summary.csv", false, Encoding.UTF8);
            summary.WriteLine("case,policy,execution,near,mirror,ticks,outcome,health,commands,rejections,attackStarts,distinctAttacks,moves,reactions,treeCompletions,treeFailures,maxCandidates,definitionHash");
            var scenarios = (from policy in new[] { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense }
                from execution in new[] { AiExecutionKind.SingleCommand, AiExecutionKind.UtilityBehaviorTree }
                from near in new[] { false, true } from mirror in new[] { false, true }
                select (policy, execution, near, mirror)).ToArray();
            Parallel.ForEach(scenarios, new ParallelOptions { MaxDegreeOfParallelism = 4 }, scenario =>
            {
                var (policy, execution, near, mirror) = scenario;
                string id = policy + "-" + execution + "-" + (near ? "near" : "main") + "-mirror" + (mirror ? 1 : 0);
                var d = FormalAiBaseline.Build(source.Definition, policy, execution, near, mirror);
                var input = new Input { sourceHash = source.BattleDefinitionHash, definitionHash = d.BattleDefinitionHashValue,
                    contentFingerprint = content, sourceFingerprint = code, version = FormalAiBaseline.Version,
                    policy = (int)policy, execution = (int)execution, near = near, mirror = mirror,
                    seed = unchecked((long)source.RuntimeInputs.InitialRngSeed), metaResource = source.RuntimeInputs.InitialMetaResource };
                var runtime = new BattleRuntimeInputs(unchecked((ulong)input.seed), input.metaResource);
                var composition = new ProductionBattleComposition(d, source.EncounterId, runtime);
                using var sim = BattleSimulation.Create(d, source.EncounterId, runtime, composition.Assembly);
                composition.Attach(sim); var recorder = new ReplayRecorder(sim);
                int commands = 0, rejected = 0, attacks = 0, moves = 0, reactions = 0, successes = 0, failures = 0, maxCandidates = 0;
                var attackIds = new HashSet<string>(); var terminalTasks = new HashSet<string>();
                using var decisions = new StreamWriter(Root + "/cases/" + id + ".decisions.csv", false, Encoding.UTF8);
                decisions.WriteLine("tick,controller,selected,wait,ordinaryNode,ordinaryPlan,ordinaryStatus,ordinaryReason,reactionNode,reactionPlan,reactionStatus,reactionReason,candidates,unsupported");
                using var candidates = new StreamWriter(Root + "/cases/" + id + ".candidates.csv", false, Encoding.UTF8);
                candidates.WriteLine("tick,controller,key,type,chosen,score,rejection,damageQ10,incomingQ10,progress,interrupts,clashes,start,impact,end");
                for (long tick = 0; tick < input.maxTicks && !sim.IsEnded; tick++)
                {
                    recorder.CaptureBeforeStep(); var step = sim.Step(tick, sim.CommandIngress.FreezeTick(tick)); recorder.RecordCommittedStep(step);
                    commands += sim.LastCommandSet.Envelopes.Count;
                    reactions += step.Events.Events.OfType<ActionPlanCreatedEvent>().Count(e => e.Origin == (int)ActionPlanOrigin.Reaction);
                    rejected += step.Events.Events.Count(e => e is CommandRejectedEvent || e is CommandIngressRejectedEvent);
                    foreach (var e in step.Events.Events.OfType<ActionPlanLockedEvent>())
                    {
                        var plan = sim.AiActionPlanLookup.FindPlan(new ProjectHero.Logic.Ids.ActionPlanId(e.ActionPlanId));
                        if (plan.ActionType == ActionType.Attack) { attacks++; attackIds.Add(plan.ActionSpecId.Value); }
                        if (plan.ActionType == ActionType.Move) moves++;
                        
                    }
                    foreach (var ai in composition.AiControllers)
                    {
                        string controller = ai.RegisteredControllerIds[0]; var decision = ai.LastDecisionOf(controller);
                        if (decision == null || decision.DecisionTick != tick) continue;
                        var o = decision.Behavior?.Ordinary; var r = decision.Behavior?.Reaction;
                        foreach (var task in new[] { o, r })
                            if (task?.Cursor == 0 && task.Reason != "IDLE" && terminalTasks.Add(controller + ":" + task.RequestTick + ":" + task.ProducerOrdinal + ":" + task.Status))
                            { if (task.Status == AiBehaviorStatus.Success) successes++; else if (task.Status == AiBehaviorStatus.Failure) failures++; }
                        maxCandidates = Math.Max(maxCandidates, decision.Evaluations.Count);
                        decisions.WriteLine(Csv(tick, controller, decision.Selected?.Payload, decision.NoSelectionReason,
                            o?.Cursor, o?.PlanId, o?.Status, o?.Reason, r?.Cursor, r?.PlanId, r?.Status, r?.Reason,
                            decision.Evaluations.Count, string.Join(";", decision.UnsupportedActionIds)));
                        foreach (var e in decision.Evaluations) candidates.WriteLine(Csv(tick, controller, e.Key, e.Type,
                            e.Chosen, e.Score, e.RejectionCode, e.DamageDeltaQ10, e.IncomingDeltaQ10, e.PositionProgress,
                            e.InterruptedThreats, e.Clashes, e.StartTick, e.ImpactTick, e.EndTick));
                    }
                }
                var replay = recorder.BuildReplay();
                if (replay.Submissions.Count != 0) throw new InvalidOperationException("AI_INPUT_MUST_BE_REGENERATED");
                File.WriteAllText(Root + "/cases/" + id + ".input.json", JsonUtility.ToJson(input, true), Encoding.UTF8);
                using (var file = File.Create(Root + "/cases/" + id + ".heroReplay")) ReplayFile.Save(file, replay);
                Verify(d, replay);
                lock (summary) { summary.WriteLine(Csv(id, policy, execution, near, mirror, replay.Records.Count,
                    sim.CurrentSnapshot.BattleEnd.ResultCode, string.Join(";", sim.CurrentSnapshot.Units.Select(u => u.UnitId + ":" + u.HealthQ10)),
                    commands, rejected, attacks, attackIds.Count, moves, reactions, successes, failures, maxCandidates, d.BattleDefinitionHashValue));
                summary.Flush(); } Debug.Log("FORMAL_AI_CASE " + id + " ticks=" + replay.Records.Count);
            });
        }
        public static void Audit()
        {
            var source = LoadSource(); string content = ContentFingerprint(), code = AiPolicyExperimentTools.SourceFingerprint();
            int count = 0, ticks = 0;
            Parallel.ForEach(Directory.GetFiles(Root + "/cases", "*.input.json"), new ParallelOptions { MaxDegreeOfParallelism = 4 }, file =>
            {
                var input = JsonUtility.FromJson<Input>(File.ReadAllText(file));
                if (input.sourceHash != source.BattleDefinitionHash || input.contentFingerprint != content || input.sourceFingerprint != code)
                    throw new InvalidOperationException("FORMAL_BASELINE_INPUT_CHANGED");
                var d = FormalAiBaseline.Build(source.Definition, (AiPolicyKind)input.policy, (AiExecutionKind)input.execution, input.near, input.mirror);
                if (input.definitionHash != d.BattleDefinitionHashValue) throw new InvalidOperationException("FORMAL_BASELINE_HASH_MISMATCH");
                using var stream = File.OpenRead(file.Replace(".input.json", ".heroReplay")); var replay = ReplayFile.Load(stream, d);
                Verify(d, replay); Interlocked.Increment(ref count); Interlocked.Add(ref ticks, replay.Records.Count);
            });
            if (count != 24) throw new InvalidOperationException("FORMAL_BASELINE_CASE_COUNT");
            File.WriteAllText(Root + "/native-disk-audit.txt", "Passed; cases=" + count + "; ticks=" + ticks + "; content=" + content + "; source=" + code);
        }
        public static void AuditV2Compatibility()
        {
            int count = 0, ticks = 0;
            var files = Directory.GetFiles(Stage4RecoveryTools.CorrectedRoot + "/cases", "*.input.json").Where(file =>
            {
                var input = JsonUtility.FromJson<Stage4RecoveryTools.SavedInput>(File.ReadAllText(file));
                return (input.recovery == 31 || input.recovery == 40) && input.opponentFirst == input.swapSpawns;
            }).ToArray();
            // Four fixture families, two values and both mirrored initiative/spawn combinations.
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, file =>
            {
                var input = JsonUtility.FromJson<Stage4RecoveryTools.SavedInput>(File.ReadAllText(file));
                var d = ProjectHero.Logic.Experiments.Stage4RecoveryExperiment.Build(input.ToInput());
                if (d.BattleDefinitionHashValue != input.definitionHash) throw new InvalidOperationException("V2_COMPATIBILITY_DEFINITION_CHANGED");
                using var stream = File.OpenRead(file.Replace(".input.json", ".heroReplay"));
                var replay = ReplayFile.Load(stream, d); Verify(d, replay); Interlocked.Increment(ref count); Interlocked.Add(ref ticks, replay.Records.Count);
            });
            if (count != 16) throw new InvalidOperationException("V2_COMPATIBILITY_CASE_COUNT");
            File.WriteAllText(Root + "/native-v2-compatibility.txt", "Passed; cases=" + count + "; ticks=" + ticks + "; currentSource=" + AiPolicyExperimentTools.SourceFingerprint());
        }
        static void Verify(BattleDefinition definition, BattleReplay replay)
        {
            using var player = new ReplayPlayer(definition); player.Load(replay); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            if (player.Deviation != null) throw new InvalidOperationException(player.Deviation.ToString());
        }
        static string Csv(params object[] values) => string.Join(",", values.Select(v => "\"" +
            Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\""));
    }
}
