using System;
using System.Linq;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Experiments;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ProjectHero.Editor;
using ProjectHero.Authoring;
using ProjectHero.Logic.Replay;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            string mode=args.Length==0?"probe":args[0];
            if(mode=="diagnose"){Diagnosis.Run();return 0;}
            if(mode=="pressure-probe"){PressureProbe.Run();return 0;}
            if(mode=="legacy-p"){Diagnosis.VerifyLegacy();return 0;}
            if(mode=="run"){ContinuationMatchupTools.Run();return 0;}
            if(mode=="pilot"){ContinuationMatchupTools.Pilot();return 0;}
            if(mode=="audit"){ContinuationMatchupTools.Audit();return 0;}
            if(mode=="reference"){ContinuationMatchupTools.AuditUnityReference();return 0;}
            if(mode=="legacy"){Legacy();return 0;}
            if(mode=="mechanisms"){Mechanisms();return 0;}
            foreach(var policy in new[]{AiPolicyKind.SustainedOffense,AiPolicyKind.SeekInterrupt,AiPolicyKind.CautiousDefense})
            {
                var plans=true;var input=new AiPolicyExperimentInput(policy,AiPolicySituation.Approach,MaxTicks:360,
                    Sampling:new AiSamplingSettings(SignedDamage:true,HealthDefense:true),ShortPlans:new AiShortPlanSettings(),Continuations:new AiContinuationSettings(plans));
                var r=AiPolicyExperiment.Run(input);AiPolicyExperiment.VerifyReplay(r);
                Console.WriteLine("PROBE "+policy+" events="+r.Events.Count+" damage="+r.Events.OfType<DamageChannelResolvedEvent>().Sum(e=>e.AfterBlockDamageQ10));
                foreach(var d in r.Decisions.Where(d=>d.HasSelection))
                    Console.WriteLine(d.DecisionTick+" "+d.Evaluations.Single(e=>e.Chosen));
            }
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }

    static void Mechanisms()
    {
        const string root="优化任务/执行记录/连续施压规划-2026-11-14/mechanisms";Directory.CreateDirectory(root);
        foreach(bool plans in new[]{false,true})
        foreach(AiPolicySituation situation in Enum.GetValues(typeof(AiPolicySituation)))
            foreach(var policy in new[]{AiPolicyKind.SustainedOffense,AiPolicyKind.SeekInterrupt,AiPolicyKind.CautiousDefense})
                foreach(int distance in situation==AiPolicySituation.Approach?new[]{14,18}:new[]{14})
                {
                    var input=new AiPolicyExperimentInput(policy,situation,MaxTicks:480,ApproachDistance:distance,
                        Sampling:new AiSamplingSettings(SignedDamage:true,HealthDefense:true),ShortPlans:new AiShortPlanSettings(),Continuations:new AiContinuationSettings(plans));
                    var r=AiPolicyExperiment.Run(input);AiPolicyExperiment.VerifyReplay(r);
                    string path=root+"/"+(plans?"C":"V")+"-"+situation+"-"+policy+"-d"+distance;
                    File.WriteAllLines(path+".canonical.txt",AiPressureEvidence.Canonical(r));
                    using(var file=File.Create(path+".heroReplay"))ReplayFile.Save(file,r.Replay);
                    File.WriteAllText(path+".json",JsonSerializer.Serialize(new {input,hash=r.Definition.BattleDefinitionHashValue,decisions=r.Decisions,
                        events=r.Events.Select(e=>e.ToString()).ToArray(),units=r.FinalUnits}));
                    Console.WriteLine("SHORT_PLAN_MECHANISM "+path);
                }
    }

    static void Legacy()
    {
        const string root="优化任务/执行记录/温度采样与磨血防御-2026-11-13/baseline/cases";
        var source=FormalAiSourceExport.Seed();long ticks=0;int cases=0;
        Parallel.ForEach(Directory.GetFiles(root,"D-*.input.json"),new ParallelOptions{MaxDegreeOfParallelism=8},path=>{
            var i=JsonSerializer.Deserialize<ContinuationMatchupTools.Input>(File.ReadAllText(path),new JsonSerializerOptions{IncludeFields=true});
            var d=FormalAiMatchup.Build(source.Definition,(AiPolicyKind)i.heroPolicy,(AiPolicyKind)i.enemyPolicy,(FormalAiLayout)i.layout,i.mirror,
                new AiForecastSettings(360,true,true,new AiSamplingSettings(i.temperature,TopPMilli:i.topP,SignedDamage:true,HealthDefense:true)));
            if(d.BattleDefinitionHashValue!=i.definitionHash)throw new InvalidOperationException("LEGACY_DEFINITION_CHANGED");
            using var file=File.OpenRead(path.Replace(".input.json",".heroReplay"));var replay=ReplayFile.Load(file,d);
            using var player=new ReplayPlayer(d);player.Load(replay);player.Play();
            while(!player.IsComplete && player.Deviation==null)player.AdvanceOneTick();
            if(!player.IsComplete || player.Deviation!=null)throw new InvalidOperationException("LEGACY_REPLAY_DEVIATION "+player.Deviation);
            lock(source){cases++;ticks+=replay.Records.Count;}
            Console.WriteLine("LEGACY_D_AUDITED "+Path.GetFileName(path));
        });
        if(cases!=54)throw new InvalidOperationException("LEGACY_CASES_MISSING");
        File.WriteAllText("优化任务/执行记录/连续施压规划-2026-11-14/legacy-d-audit.txt",$"cases={cases};ticks={ticks};eventsAndSnapshots=True\n");
    }
}
