using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Replay;
using UnityEditor;
using UnityEngine;

namespace ProjectHero.Editor
{
    [Serializable]
    public sealed class CombatExperimentInput
    {
        public string id, attack, seed, rulesVersion, numericalVersion, scriptVersion, scenarioVersion, definitionHash, sourceFingerprint;
        public int growth, targetHealth, stabilityQ10, script, maxTicks, bluntRecoveryDelta;
        public bool shortRange, wall;
        public int healthUnitsPerPoint=1024, momentumUnitsPerPoint=1024, resourceUnitsPerPoint=CombatValuesV05.ResourceUnitsPerPoint,
            initialMetaResource=4, windowDurationTicks=180;
        public CombatExperimentScenario Scenario() => new CombatExperimentScenario(id,attack,growth,targetHealth,stabilityQ10,
            (ExperimentScript)script,shortRange,wall,maxTicks,ulong.Parse(seed,CultureInfo.InvariantCulture),bluntRecoveryDelta);
        public static CombatExperimentInput From(CombatExperimentResult result, string fingerprint) => new CombatExperimentInput {
            id=result.Scenario.Id, attack=result.Scenario.Attack, seed=result.Scenario.Seed.ToString(CultureInfo.InvariantCulture),
            growth=result.Scenario.Growth,targetHealth=result.Scenario.TargetHealth,stabilityQ10=result.Scenario.StabilityQ10,
            script=(int)result.Scenario.Script,maxTicks=result.Scenario.MaxTicks,shortRange=result.Scenario.ShortRange,wall=result.Scenario.Wall,
            bluntRecoveryDelta=result.Scenario.BluntRecoveryDelta,
            rulesVersion=result.Definition.RulesVersion,numericalVersion=CombatValuesV05.Version,
            scriptVersion=CombatExperimentScenario.ScriptVersion,scenarioVersion=CombatExperimentScenario.ScenarioVersion,
            definitionHash=result.Definition.BattleDefinitionHashValue,sourceFingerprint=fingerprint };
    }

    public static class CombatExperimentTools
    {
        public static string EvidenceRoot => Path.GetFullPath(Path.Combine(Application.dataPath,"..","优化任务","执行记录","数值AI-2026-10-28"));

        [MenuItem("Project Hero/Combat Experiments/Run First Round")]
        public static void RunBatch()
        {
            string output=Path.Combine(EvidenceRoot,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+"Z");
            Directory.CreateDirectory(output);
            string fingerprint=SourceFingerprint();
            var summary=new StringBuilder("scenario,rules_version,numerical_version,script_version,scenario_version,definition_hash,seed,outcome,end_tick,damaged_target_ticks,positive_damage_contacts,started_attacks,submitted_commands,rejected_commands,block_uses,dodge_uses,damage_q10,replay_verified,source_fingerprint\n");
            int failures=0;
            foreach (var scenario in CombatExperimentRunner.FirstRound())
            {
                try
                {
                    var result=CombatExperimentRunner.Run(scenario);
                    Save(output,result,fingerprint);
                    CombatExperimentRunner.VerifyReplay(result);
                    if(result.Events.OfType<CommandRejectedEvent>().Any() || result.Decisions.Any(d=>d.IngressRejection!=null&&d.IngressRejection!="not-submitted"))
                        throw new InvalidOperationException("Fixed script command rejected; inspect the saved decision and event records.");
                    var damage=result.Diagnostics.SelectMany(t=>t.Damage.Units).ToArray();
                    var attackPlans=result.Events.OfType<ActionPlanCreatedEvent>().Where(e=>e.ActionType==(int)Logic.Actions.ActionType.Attack)
                        .Select(e=>e.ActionPlanId).ToArray();
                    summary.Append(string.Join(",",new[]{scenario.Id,result.Definition.RulesVersion,CombatValuesV05.Version,
                        CombatExperimentScenario.ScriptVersion,CombatExperimentScenario.ScenarioVersion,result.Definition.BattleDefinitionHashValue,
                        scenario.Seed.ToString(CultureInfo.InvariantCulture),result.Outcome,result.FinalSnapshot.Tick.ToString(CultureInfo.InvariantCulture),
                        damage.Count(d=>d.DamageQ10>0).ToString(),result.Events.OfType<DamageChannelResolvedEvent>().Count(e=>e.TotalDamageQ10>0).ToString(),
                        result.Events.OfType<ActionPlanLockedEvent>().Count(e=>attackPlans.Contains(e.ActionPlanId)).ToString(),
                        result.Decisions.Count(d=>d.IngressRejection!="not-submitted").ToString(),result.Events.OfType<CommandRejectedEvent>().Count().ToString(),
                        result.Decisions.Count(d=>d.Action=="Block").ToString(),result.Decisions.Count(d=>d.Action=="Dodge").ToString(),
                        damage.Sum(d=>d.DamageQ10).ToString(CultureInfo.InvariantCulture),"true",fingerprint})).Append('\n');
                }
                catch(Exception exception)
                {
                    failures++;
                    File.WriteAllText(Path.Combine(output,scenario.Id+".failure.txt"),exception.ToString(),Encoding.UTF8);
                    summary.Append(scenario.Id).Append(",,,,,,,error,,,,,,,,,,false,").Append(fingerprint).Append('\n');
                    Debug.LogException(exception);
                }
            }
            File.WriteAllText(Path.Combine(output,"summary.csv"),summary.ToString(),Encoding.UTF8);
            File.WriteAllText(Path.Combine(EvidenceRoot,"latest-run.txt"),Path.GetFileName(output),Encoding.UTF8);
            Debug.Log("Combat experiments: "+CombatExperimentRunner.FirstRound().Count+" cases; failures="+failures+"; "+output);
            if(Application.isBatchMode) EditorApplication.Exit(failures==0?0:1);
            else if(failures>0) throw new InvalidOperationException("Combat experiment failures: "+failures+". See "+output);
        }

        public static void Save(string output,CombatExperimentResult result,string fingerprint)
        {
            string prefix=Path.Combine(output,result.Scenario.Id);
            File.WriteAllText(prefix+".input.json",JsonUtility.ToJson(CombatExperimentInput.From(result,fingerprint),true),Encoding.UTF8);
            using(var stream=File.Create(prefix+".heroReplay")) ReplayFile.Save(stream,result.Replay);
            using(var events=new StreamWriter(prefix+".events.jsonl",false,Encoding.UTF8))
                foreach(var record in result.Replay.Records)
                    foreach(var fact in record.Events)
                        events.WriteLine("{\"tick\":"+record.Tick+",\"sequence\":"+fact.Sequence+",\"event\":"+Json(ReplayEventComparison.Canonical(fact))+"}");
            using(var decisions=new StreamWriter(prefix+".decisions.jsonl",false,Encoding.UTF8))
                foreach(var decision in result.Decisions)
                    decisions.WriteLine("{\"tick\":"+decision.Tick+",\"unit\":"+decision.UnitId+",\"action\":"+Json(decision.Action)
                        +",\"reason\":"+Json(decision.Reason)+",\"ingressRejection\":"+Json(decision.IngressRejection)+"}");
            using(var diagnostics=new StreamWriter(prefix+".damage-income.csv",false,Encoding.UTF8))
            {
                diagnostics.WriteLine("tick,unit,actual_health_loss_q10,actual_dealt_q10,actual_received_q10,income_before_cap_internal");
                foreach(var tick in result.Diagnostics)
                    foreach(var fact in tick.Income)
                        diagnostics.WriteLine(string.Join(",",new long[]{tick.Tick,fact.UnitId.Value,
                            tick.Damage.Units.FirstOrDefault(u=>u.UnitId==fact.UnitId)?.DamageQ10??0,
                            fact.TotalFinalDamageDealtQ10,fact.TotalFinalDamageReceivedQ10,
                            (long)fact.TotalFinalDamageDealtQ10*2+(long)fact.TotalFinalDamageReceivedQ10*3}));
            }
        }
        public static string Json(string value)
        {
            if(value==null)return "null";
            var text=new StringBuilder("\"");
            foreach(char c in value)
                switch(c)
                {
                    case '"':text.Append("\\\"");break;
                    case '\\':text.Append("\\\\");break;
                    case '\n':text.Append("\\n");break;
                    case '\r':text.Append("\\r");break;
                    case '\t':text.Append("\\t");break;
                    default:if(c<32)text.Append("\\u").Append(((int)c).ToString("x4"));else text.Append(c);break;
                }
            return text.Append('"').ToString();
        }
        private static string SourceFingerprint()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            var source=new StringBuilder();
            foreach(string file in Directory.GetFiles(Path.Combine(root,"Assets","Scripts"),"*.cs",SearchOption.AllDirectories).OrderBy(p=>p,StringComparer.Ordinal))
            {
                string relative=file.Substring(root.Length).Replace('\\','/');
                source.Append(relative.Length).Append(':').Append(relative).Append('\n');
                source.Append(File.ReadAllText(file,Encoding.UTF8)).Append('\n');
            }
            using(var sha=SHA256.Create())return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(source.ToString())).Select(b=>b.ToString("x2")));
        }
    }
}
