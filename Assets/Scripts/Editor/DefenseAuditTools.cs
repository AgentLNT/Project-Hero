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
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ProjectHero.Editor
{
    /// <summary>Task 18 diagnostics. Scripted response branches are separate from autonomous replay cases.</summary>
    public static class DefenseAuditTools
    {
        public const string Root = "优化任务/执行记录/防御应对核对-2026-11-09";
        const string Previous = LongActionForecastTools.Root;
        [Serializable] public sealed class Input
        {
            public string protocol = "defense-audit-v2", definitionHash, sourceHash, sourceFingerprint, contentFingerprint;
            public long seed; public int metaResource;
            public int policy, maxTicks = 900; public bool near, mirror;
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
            {
                var error = Sim.CommandIngress.FindEntry(id).Submit(request);
                if (error != null) throw new InvalidOperationException("DIAGNOSTIC_INGRESS: " + error);
            }
            public void Dispose() => Sim.Dispose();
        }
        static BattleSimulationSeed Source()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/CombatSampleScene.unity", OpenSceneMode.Single);
            var host = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<NewBattleSimulationSource>(true)).Single();
            return host.BuildSeed() ?? throw new InvalidOperationException(host.LastConfigurationError);
        }
        static BattleDefinition Build(BattleSimulationSeed source, Input input) => FormalAiBaseline.Build(source.Definition,
            (AiPolicyKind)input.policy, AiExecutionKind.UtilityBehaviorTree, input.near, input.mirror, new AiForecastSettings(360));
        static IEnumerable<Input> Matrix()
        {
            foreach (var policy in new[] { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense })
            foreach (bool near in new[] { false, true }) foreach (bool mirror in new[] { false, true })
                yield return new Input { policy = (int)policy, near = near, mirror = mirror };
        }
        public static BattleDefinition Rehash(BattleDefinition d) => d with { BattleDefinitionHashValue = BattleDefinitionHash.Compute(
            d.RulesVersion, d.TicksPerSecond, LegacyIdMigrationManifest.LibraryVolumes.Count, d.Rules, d.ConcurrentAction,
            d.ReactionRules, d.AdrenalineRules, d.FactionModel, d.DamageChannels, d.ImpactProfiles, d.Units, d.Actions,
            d.AttackPatterns, d.Volumes, d.MovementPatterns, d.ActionSets, d.StatusEffects, d.Encounters, d.DefaultDynamicSpawnPolicy) };

        public static void Run()
        {
            Directory.CreateDirectory(Root + "/cases"); var source = Source();
            string code = SourceFingerprint(), content = FormalAiBaselineTools.ContentFingerprint();
            var lines = new SortedDictionary<string, string>(StringComparer.Ordinal);
            Parallel.ForEach(Matrix(), new ParallelOptions { MaxDegreeOfParallelism = 4 }, input =>
            {
                var d = Build(source, input); input.definitionHash = d.BattleDefinitionHashValue;
                input.sourceFingerprint = code; input.contentFingerprint = content;
                input.seed = unchecked((long)source.RuntimeInputs.InitialRngSeed); input.metaResource = source.RuntimeInputs.InitialMetaResource;
                input.sourceHash = source.BattleDefinitionHash;
                using var f = new Fixture(d, source, true); var sim = f.Sim; var recorder = new ReplayRecorder(sim);
                string path = Root + "/cases/" + input.Id;
                using var events = Gzip(path + ".events.txt.gz"); using var candidates = Gzip(path + ".candidates.csv.gz");
                candidates.WriteLine("tick,controller,key,type,chosen,score,rejection,reason,damageQ10,incomingQ10,progress,cost,start,impact,end,coverage");
                int commands = 0, rejects = 0, attacks = 0, moves = 0, reactions = 0, guards = 0; long spent = 0, loss = 0;
                for (long tick = 0; tick < input.maxTicks && !sim.IsEnded; tick++)
                {
                    var before = sim.CurrentSnapshot; recorder.CaptureBeforeStep();
                    var step = sim.Step(tick, sim.CommandIngress.FreezeTick(tick)); recorder.RecordCommittedStep(step);
                    commands += sim.LastCommandSet.Envelopes.Count;
                    loss += step.Snapshot.Units.Sum(u => Math.Max(0L, before.Units.Single(b => b.UnitId == u.UnitId).HealthQ10 - u.HealthQ10));
                    foreach (var e in step.Events.Events)
                    {
                        events.WriteLine(ReplayEventComparison.Canonical(e));
                        if (e is CommandRejectedEvent || e is CommandIngressRejectedEvent) rejects++;
                        if (e is AdrenalineLedgerChangedEvent ledger && ledger.ChangeKind == AdrenalineChangeKind.Consumed) spent += ledger.ReservationAmount;
                        if (e is ActionPlanCreatedEvent created && created.Origin == (int)ActionPlanOrigin.Reaction) reactions++;
                        if (e is ActionPlanLockedEvent started)
                        {
                            var p = sim.AiActionPlanLookup.FindPlan(new ActionPlanId(started.ActionPlanId));
                            if (p.ActionType == ActionType.Attack) attacks++;
                            if (p.ActionType == ActionType.Move) moves++;
                            if (p.ActionType == ActionType.Guard) guards++;
                        }
                    }
                    foreach (var ai in f.Composition.AiControllers)
                    {
                        string id = ai.RegisteredControllerIds[0]; var decision = ai.LastDecisionOf(id);
                        if (decision?.DecisionTick != tick) continue;
                        foreach (var e in decision.Evaluations)
                            candidates.WriteLine(Csv(tick, id, e.Key, e.Type, e.Chosen, e.Score, e.RejectionCode, e.Reason,
                                e.DamageDeltaQ10, e.IncomingDeltaQ10, e.PositionProgress, e.Cost, e.StartTick, e.ImpactTick, e.EndTick, e.Coverage));
                    }
                }
                var replay = recorder.BuildReplay(); if (replay.Submissions.Count != 0) throw new InvalidOperationException("EXTERNAL_INPUT_IN_AI_CASE");
                using (var output = File.Create(path + ".heroReplay")) ReplayFile.Save(output, replay);
                File.WriteAllText(path + ".input.json", JsonUtility.ToJson(input, true), Encoding.UTF8);
                lock (lines) lines.Add(input.Id, Csv(input.Id, (AiPolicyKind)input.policy, input.near, input.mirror, replay.Records.Count,
                    sim.IsEnded, commands, rejects, attacks, moves, reactions, guards, loss, spent, Projection(sim.CurrentSnapshot), d.BattleDefinitionHashValue));
                Debug.Log("DEFENSE_AI_CASE " + input.Id);
            });
            File.WriteAllText(Root + "/summary.csv", "case,policy,near,mirror,ticks,ended,commands,rejections,attackStarts,moves,reactions,guards,healthLossQ10,spent,finalProjection,definitionHash\n" + string.Join("\n", lines.Values) + "\n", Encoding.UTF8);
            RunDiagnostics(source, false);
        }
        static Prefix ReadPrefix(BattleDefinition d, string path, long checkpoint)
        {
            using var stream = File.OpenRead(path); var replay = ReplayFile.Load(stream, d); var prefix = new Prefix(); BattleSimulation sim = null;
            using var player = new ReplayPlayer(d, header =>
            {
                var comp = new ProductionBattleComposition(d, header.EncounterId, header.RuntimeInputs);
                sim = BattleSimulation.Create(d, header.EncounterId, header.RuntimeInputs, comp.Assembly);
                comp.Attach(sim); return sim;
            });
            player.Load(replay); player.Play();
            // InitialState's diagnostic snapshot is labelled Tick 0 before the first Step.
            // Advance the recorded boundaries explicitly, including the committed Tick 0.
            for (long tick = 0; tick <= checkpoint && player.Deviation == null; tick++)
            {
                player.AdvanceOneTick();
                prefix.Commands.AddRange(sim.LastCommandSet.Envelopes.Select(e => (e.ControllerId, e.Request)));
            }
            if (player.Deviation != null) throw new InvalidOperationException(player.Deviation.ToString());
            prefix.Projection = Projection(player.CurrentSnapshot); return prefix;
        }
        static Fixture Branch(BattleDefinition d, BattleSimulationSeed source, Prefix prefix, long checkpoint)
        {
            var f = new Fixture(d, source);
            for (long tick = 0; tick <= checkpoint; tick++)
            {
                foreach (var command in prefix.Commands.Where(c => c.request.TargetTick == tick)) f.Submit(command.controller, command.request);
                f.Sim.Step(tick, f.Sim.CommandIngress.FreezeTick(tick));
            }
            if (Projection(f.Sim.CurrentSnapshot) != prefix.Projection) throw new InvalidOperationException("PREFIX_PUBLIC_STATE_MISMATCH");
            return f;
        }
        static string Projection(LogicSnapshot s) => string.Join(";", s.Units.Select(u => $"{u.UnitId}:{u.HealthQ10}:{u.X}:{u.Y}:{u.State}"))
            + "|" + string.Join(";", s.Resources.AdrenalineLedgers.Select(l => $"{l.UnitId}:{l.CycleId}:{l.AvailableAdrenaline}:{l.ReservedTotal}"));
        static string UnitProjection(IEnumerable<UnitSnapshot> units) => string.Join(";", units.Select(u => $"{u.UnitId}:{u.HealthQ10}:{u.X}:{u.Y}:{u.State}"));

        static void RunDiagnostics(BattleSimulationSeed source, bool audit)
        {
            var branches = new List<string> { "case,checkpoint,attacker,action,response,predictedDamageQ10,actualAttackDamageQ10,status,responseReason,responseSubmitAt,responseKey,responseScore,spent,noResponseMatches,eventDigest,finalProjection" };
            foreach (var input in Matrix().Where(i => i.policy == (int)AiPolicyKind.SustainedOffense))
            {
                var d = Build(source, input);
                foreach (long checkpoint in input.near ? new long[] { 0, 240, 426 } : new long[] { 0 })
                {
                    var prefix = ReadPrefix(d, Root + "/cases/" + input.Id + ".heroReplay", checkpoint);
                    using var origin = Branch(d, source, prefix, checkpoint);
                    var context = origin.Composition.AiControllers.Select(ai => origin.Context(new ControllerId(ai.RegisteredControllerIds[0]))).Single(c => c.HasSubmissionWindow);
                    using var forecast = context.ForecastFactory(context.Snapshot); var baseline = forecast.Evaluate(null, context.ForecastSettings);
                    foreach (var attack in AiTacticalCandidateBuilder.Ordinary(context).Where(c => c.Type == ActionType.Attack))
                    {
                        var predicted = forecast.Evaluate(attack.Request, context.ForecastSettings);
                        var eval = AiTacticalPolicy.Evaluate(context, AiPolicyKind.SustainedOffense, attack, baseline, predicted);
                        if (predicted.RejectionCode != null) throw new InvalidOperationException("ATTACK_BRANCH_REJECTED");
                        foreach (string response in new[] { "None", "Guard", "Block", "Dodge" })
                            branches.Add(ResponseBranch(d, source, input, prefix, checkpoint, context, attack, predicted, eval, response));
                    }
                }
            }
            CompareOrWrite("responses.csv", string.Join("\n", branches) + "\n", audit);
            CompareOrWrite("wait-scores.csv", WaitScores(source), audit);
        }
        public static void RunDiagnosticsOnly() => RunDiagnostics(Source(), false);
        static string ResponseBranch(BattleDefinition d, BattleSimulationSeed source, Input input, Prefix prefix, long checkpoint,
            AiDecisionContext origin, AiTacticalCandidate attack, AiActionForecast predicted, AiCandidateEvaluation evaluation, string response)
        {
            using var f = Branch(d, source, prefix, checkpoint); var sim = f.Sim; f.Submit(origin.ControllerId, attack.Request);
            var defender = new ControllerId(d.Encounters[0].Controllers.Single(c => c.ControllerId != origin.ControllerId).ControllerId.Value);
            long planId = 0, damage = 0, spent = 0, submittedAt = -1; long? responseScore = null; bool sent = false, accepted = false;
            string reason = response == "None" ? "NO_FURTHER_COMMANDS" : "NO_PUBLIC_LEGAL_RESPONSE", key = "";
            var events = new StringBuilder();
            for (long tick = checkpoint + 1; tick <= predicted.HorizonTick && !sim.IsEnded; tick++)
            {
                var step = sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                foreach (var e in step.Events.Events)
                {
                    events.AppendLine(ReplayEventComparison.Canonical(e));
                    if (e is CommandRejectedEvent rejection) throw new InvalidOperationException("RESPONSE_ACTUAL_REJECTION: " + rejection.ReasonCode);
                    if (e is CommandIngressRejectedEvent) throw new InvalidOperationException("RESPONSE_INGRESS_REJECTION");
                    if (e is ActionPlanCreatedEvent created && tick == attack.Request.TargetTick && created.OwnerUnitId == origin.ControlledUnits[0].Value
                        && created.ActionSpecId == ((ScheduleEditPayload)attack.Request.Payload).Operations.OfType<AddOrdinaryPlanOperation>().Single().ActionSpecId.Value) planId = created.ActionPlanId;
                    if (e is ActionPlanCreatedEvent createdResponse && submittedAt >= 0 && tick == submittedAt + 1 && createdResponse.OwnerUnitId != origin.ControlledUnits[0].Value) accepted = true;
                    if (e is DamageChannelResolvedEvent hit && hit.AttackPlanId.Value == planId) damage += hit.AfterBlockDamageQ10;
                    if (e is AdrenalineLedgerChangedEvent ledger && ledger.ChangeKind == AdrenalineChangeKind.Consumed) spent += ledger.ReservationAmount;
                }
                if (response == "None" || sent || tick >= predicted.PlanImpactTick || tick < attack.Request.TargetTick) continue;
                var ctx = f.Context(defender);
                if (response == "Guard")
                {
                    // Submit at the last committed boundary, only if this controller has its real submission window.
                    if (tick != predicted.PlanImpactTick - 1) continue;
                    var guard = AiTacticalCandidateBuilder.Ordinary(ctx).FirstOrDefault(c => c.Type == ActionType.Guard);
                    if (guard == null) { reason = ctx.HasSubmissionWindow ? "GUARD_LANE_COMMITTED" : "GUARD_NO_SUBMISSION_WINDOW"; continue; }
                    string error = ctx.NormalPreview(defender, guard.Request);
                    if (error != null) { reason = error; continue; }
                    f.Submit(defender, guard.Request); sent = true; submittedAt = tick; key = guard.Key; reason = "LEGAL_GUARD_AT_IMPACT";
                }
                else
                {
                    // Force the requested response type, then use the existing public utility policy for its destination.
                    var opportunities = ctx.Opportunities.Where(o => o.SourceAttackPlanId.Value == planId).ToArray();
                    if (opportunities.Length == 0) continue;
                    using var world = ctx.ForecastFactory(ctx.Snapshot);
                    var noResponse = world.Evaluate(null, ctx.ForecastSettings);
                    AiTacticalCandidate selected = null; AiCandidateEvaluation best = null;
                    foreach (var candidate in AiTacticalCandidateBuilder.Reactions(ctx with { Opportunities = opportunities })
                        .Where(c => c.Type.ToString() == response))
                    {
                        var outcome = world.Evaluate(candidate.Request, ctx.ForecastSettings);
                        if (outcome.RejectionCode != null) { reason = outcome.RejectionCode; continue; }
                        var e = AiTacticalPolicy.Evaluate(ctx, AiPolicyKind.SustainedOffense, candidate, noResponse, outcome);
                        if (!e.Score.HasValue) { reason = e.Reason; continue; }
                        if (best == null || e.Score > best.Score || e.Score == best.Score && string.CompareOrdinal(e.Key, best.Key) < 0)
                        { selected = candidate; best = e; }
                    }
                    if (selected != null)
                    {
                        f.Submit(defender, selected.Request); sent = true; submittedAt = tick; key = selected.Key;
                        responseScore = best.Score; reason = "MAX_PUBLIC_UTILITY_FOR_REQUESTED_RESPONSE_TYPE";
                    }
                    // With no additional commands or remaining plans here, a failed funding check cannot improve before this impact.
                    if (!sent && reason == AdrenalineLedgerCodes.INSUFFICIENT_ADRENALINE) sent = true;
                }
            }
            bool? matches = response == "None" ? UnitProjection(predicted.Units) == UnitProjection(sim.CurrentSnapshot.Units) : null;
            if (matches == false) throw new InvalidOperationException("NO_RESPONSE_FORECAST_MISMATCH");
            if (planId == 0) throw new InvalidOperationException("ATTACK_PLAN_NOT_TRACKED");
            string digest = BitConverter.ToString(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(events.ToString()))).Replace("-", "").ToLowerInvariant();
            return Csv(input.Id, checkpoint, origin.ControlledUnits[0].Value, attack.Key, response, evaluation.DamageDeltaQ10,
                damage, response == "None" ? "CONTROL" : accepted ? "ACCEPTED" : "UNAVAILABLE", reason, submittedAt, key, responseScore, spent, matches, digest, Projection(sim.CurrentSnapshot));
        }
        static string WaitScores(BattleSimulationSeed source)
        {
            var rows = new List<string> { "case,key,type,score,damageTerm,incomingTerm,positionTerm,interruptTerm,clashTerm,durationPenalty,resourcePenalty,latencyPenalty,start,impact,end,reason" };
            foreach (var input in Matrix())
            {
                var d = Build(source, input); using var f = new Fixture(d, source); f.Sim.Step(0, f.Sim.CommandIngress.FreezeTick(0));
                var ctx = f.Composition.AiControllers.Select(ai => f.Context(new ControllerId(ai.RegisteredControllerIds[0]))).Single(c => c.HasSubmissionWindow);
                using var forecast = ctx.ForecastFactory(ctx.Snapshot); var baseline = forecast.Evaluate(null, ctx.ForecastSettings);
                foreach (var candidate in AiTacticalCandidateBuilder.Ordinary(ctx))
                {
                    var outcome = forecast.Evaluate(candidate.Request, ctx.ForecastSettings);
                    var policy = (AiPolicyKind)input.policy; var e = AiTacticalPolicy.Evaluate(ctx, policy, candidate, baseline, outcome);
                    if (!e.Score.HasValue || e.RejectionCode != null) continue;
                    var w = AiUtilityProfile.For(policy);
                    long damage = e.DamageDeltaQ10.Value * w.Damage, incoming = -e.IncomingDeltaQ10.Value * w.Incoming,
                        position = e.PositionProgress.Value * w.Position, interrupt = e.InterruptedThreats.Value * (long)w.Interrupt,
                        clash = e.Clashes.Value * (long)w.Clash, duration = Math.Max(0, outcome.PlanEndTick - ctx.NextTick) * w.Duration,
                        resource = (long)candidate.Cost * w.Resource, latency = e.DamageDeltaQ10 > 0 && outcome.FirstHostileDamageTick >= 0 ? outcome.FirstHostileDamageTick - ctx.Snapshot.Tick : 0;
                    if (damage + incoming + position + interrupt + clash - duration - resource - latency != e.Score) throw new InvalidOperationException("SCORE_BREAKDOWN_MISMATCH");
                    rows.Add(Csv(input.Id, candidate.Key, candidate.Type, e.Score, damage, incoming, position, interrupt, clash, duration, resource, latency,
                        e.StartTick, e.ImpactTick, e.EndTick, e.Reason));
                }
            }
            return string.Join("\n", rows) + "\n";
        }
        static void CompareOrWrite(string name, string value, bool audit)
        {
            if (audit) { if (File.ReadAllText(Root + "/" + name, Encoding.UTF8) != value) throw new InvalidOperationException("DIAGNOSTIC_DISK_MISMATCH: " + name); }
            else File.WriteAllText(Root + "/" + name, value, Encoding.UTF8);
        }
        public static void Audit()
        {
            var source = Source(); int count = 0; long ticks = 0;
            string code = SourceFingerprint(), content = FormalAiBaselineTools.ContentFingerprint();
            Parallel.ForEach(Matrix(), new ParallelOptions { MaxDegreeOfParallelism = 4 }, input =>
            {
                string path = Root + "/cases/" + input.Id;
                var saved = JsonUtility.FromJson<Input>(File.ReadAllText(path + ".input.json")); var d = Build(source, input);
                if (saved.protocol != "defense-audit-v2" || saved.definitionHash != d.BattleDefinitionHashValue || saved.sourceFingerprint != code || saved.contentFingerprint != content
                    || saved.sourceHash != source.BattleDefinitionHash || saved.seed != unchecked((long)source.RuntimeInputs.InitialRngSeed) || saved.metaResource != source.RuntimeInputs.InitialMetaResource)
                    throw new InvalidOperationException("DEFENSE_AUDIT_INPUT_CHANGED");
                using var stream = File.OpenRead(path + ".heroReplay"); var replay = ReplayFile.Load(stream, d); Verify(d, replay);
                lock (source) { count++; ticks += replay.Records.Count; }
            });
            RunDiagnostics(source, true);
            File.WriteAllText(Root + "/native-disk-audit.txt", $"cases={count};ticks={ticks};responseAndWaitDiagnosticsRebuilt=True\n", Encoding.UTF8);
        }
        public static void AuditLegacy()
        {
            var source = Source(); var old = Rehash(source.Definition with { RulesVersion = "battle-def-v5-clash-recovery", AdrenalineRules = AdrenalineRules.FrozenV1 });
            if (old.BattleDefinitionHashValue != "de4be447760a2266") throw new InvalidOperationException("LEGACY_SOURCE_HASH_CHANGED");
            var legacySeed = new BattleSimulationSeed(old, source.EncounterId, source.RuntimeInputs, source.InputSummary); int count = 0; long ticks = 0;
            Parallel.ForEach(Matrix(), new ParallelOptions { MaxDegreeOfParallelism = 4 }, input =>
            {
                var d = Build(legacySeed, input); string prefix = Previous + "/cases/" + input.Id + "-h360";
                var saved = JsonUtility.FromJson<LongActionForecastTools.Input>(File.ReadAllText(prefix + ".input.json"));
                if (saved.definitionHash != d.BattleDefinitionHashValue) throw new InvalidOperationException("LEGACY_AI_HASH_CHANGED");
                using var stream = File.OpenRead(prefix + ".heroReplay"); var replay = ReplayFile.Load(stream, d); Verify(d, replay);
                lock (source) { count++; ticks += replay.Records.Count; }
            });
            File.WriteAllText(Root + "/native-legacy-audit.txt", $"cases={count};ticks={ticks};legacySourceHash={old.BattleDefinitionHashValue}\n", Encoding.UTF8);
        }
        public static void AuditAll() { Audit(); AuditLegacy(); }
        static void Verify(BattleDefinition d, BattleReplay replay)
        {
            using var player = new ReplayPlayer(d); player.Load(replay); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            if (player.Deviation != null) throw new InvalidOperationException(player.Deviation.ToString());
        }
        static string SourceFingerprint()
        {
            var text = new StringBuilder();
            foreach (string path in new[] { "Assets/Scripts", "Assets/Tests", "Assets/Diagnostics" }
                .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)).OrderBy(p => p.Replace('\\', '/'), StringComparer.Ordinal))
            {
                using var sha = SHA256.Create(); text.Append(path.Replace('\\', '/')).Append(':')
                    .Append(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant()).Append('\n');
            }
            using var final = SHA256.Create(); return BitConverter.ToString(final.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
        static StreamWriter Gzip(string path) => new StreamWriter(new GZipStream(File.Create(path), System.IO.Compression.CompressionLevel.Optimal), Encoding.UTF8);
        static string Csv(params object[] values) => string.Join(",", values.Select(v => "\"" + Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\""));
    }
}
