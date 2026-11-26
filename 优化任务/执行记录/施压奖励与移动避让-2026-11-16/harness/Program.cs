using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ProjectHero.Authoring;
using ProjectHero.Editor;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Replay;
using UnityEngine;

static class Program
{
    const string Root="优化任务/执行记录/施压奖励与移动避让-2026-11-16/";
    static int Main(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("HERO25_BATCH",Environment.GetEnvironmentVariable("HERO25_BATCH")??"formal");
            switch(args.Single())
            {
                case "run":ContinuationMatchupTools.Run();break;
                case "pilot":ContinuationMatchupTools.Pilot();break;
                case "audit":ContinuationMatchupTools.Audit();break;
                case "reference":ContinuationMatchupTools.AuditUnityReference();break;
                case "legacy":Legacy();break;
                default:throw new ArgumentException("Unknown mode");
            }
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static void Legacy()
    {
        string records="优化任务/执行记录/";
        var folders=new[]{records+"短组合规划-2026-11-13/formal/cases",records+"连续施压规划-2026-11-14/formal/cases"};
        var source=FormalAiSourceExport.Seed();long ticks=0;int cases=0;
        Parallel.ForEach(folders.SelectMany(f=>Directory.GetFiles(f,"*.input.json")),new ParallelOptions{MaxDegreeOfParallelism=4},path=>{
            var i=JsonUtility.FromJson<ContinuationMatchupTools.Input>(File.ReadAllText(path));
            var def=FormalAiMatchup.Build(source.Definition,(AiPolicyKind)i.heroPolicy,(AiPolicyKind)i.enemyPolicy,(FormalAiLayout)i.layout,i.mirror,
                new AiForecastSettings(360,true,true,new AiSamplingSettings(i.temperature,TopPMilli:i.topP,SignedDamage:true,HealthDefense:true),
                    new AiShortPlanSettings(),i.stage=="P"?null:new AiContinuationSettings(i.stage=="C")));
            if(def.BattleDefinitionHashValue!=i.definitionHash)throw new Exception("LEGACY_DEFINITION_CHANGED "+path);
            using var file=File.OpenRead(path.Replace(".input.json",".heroReplay"));var replay=ReplayFile.Load(file,def);
            using var player=new ReplayPlayer(def);player.Load(replay);player.Play();
            while(!player.IsComplete && player.Deviation==null)player.AdvanceOneTick();
            if(!player.IsComplete || player.Deviation!=null)throw new Exception("LEGACY_REPLAY_DEVIATION "+path+" "+player.Deviation);
            lock(source){cases++;ticks+=replay.Records.Count;}
            Console.WriteLine("LEGACY_AUDITED "+i.Id);
        });
        if(cases!=162)throw new Exception("LEGACY_CASES_MISSING");
        File.WriteAllText(Root+"legacy-audit.txt",$"cases={cases};ticks={ticks};definitionHashes=True;eventsAndSnapshots=True\n");
    }
}
