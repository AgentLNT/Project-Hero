using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ProjectHero.Authoring;
using ProjectHero.Editor;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;

static class Diagnosis
{
    const string Old="优化任务/执行记录/短组合规划-2026-11-13";
    const string Root="优化任务/执行记录/连续施压规划-2026-11-14";
    sealed record Change(long tick,long unit,string cause,int fromX,int fromY,int toX,int toY);
    public static void VerifyLegacy()
    {
        int cases=0;long ticks=0;object gate=new object();
        Parallel.ForEach(Directory.GetFiles(Old+"/formal/cases","*.input.json"),new ParallelOptions{MaxDegreeOfParallelism=4},path=>
        {
            var i=JsonSerializer.Deserialize<ShortPlanMatchupTools.Input>(File.ReadAllText(path),new JsonSerializerOptions{IncludeFields=true});
            var source=FormalAiSourceExport.Seed();
            var d=FormalAiMatchup.Build(source.Definition,(AiPolicyKind)i.heroPolicy,(AiPolicyKind)i.enemyPolicy,(FormalAiLayout)i.layout,i.mirror,
                new AiForecastSettings(360,true,true,new AiSamplingSettings(i.temperature,TopPMilli:i.topP,SignedDamage:true,HealthDefense:true),new AiShortPlanSettings()));
            if(d.BattleDefinitionHashValue!=i.definitionHash)throw new Exception("OLD_P_DEFINITION_CHANGED");
            using var file=File.OpenRead(path.Replace(".input.json",".heroReplay"));var replay=ReplayFile.Load(file,d);
            using var player=new ReplayPlayer(d);player.Load(replay);player.Play();
            while(!player.IsComplete && player.Deviation==null)player.AdvanceOneTick();
            if(!player.IsComplete || player.Deviation!=null)throw new Exception("OLD_P_DEVIATION "+player.Deviation);
            lock(gate){cases++;ticks+=replay.Records.Count;}
            Console.WriteLine("LEGACY_P_AUDITED "+Path.GetFileName(path));
        });
        if(cases!=54)throw new Exception("OLD_P_MISSING_CASES");
        File.WriteAllText(Root+"/legacy-p-audit.txt",$"cases={cases};ticks={ticks};definitionHashes=True;eventsAndSnapshots=True\n");
    }
    public static void Run()
    {
        var expected=JsonDocument.Parse(File.ReadAllText(Old+"/actual-attack-outcomes.json")).RootElement.GetProperty("actions");
        var wanted=expected.EnumerateArray().Where(e=>e.GetProperty("category").GetString()=="no-damage-contact-recorded")
            .GroupBy(e=>e.GetProperty("case").GetString()).ToDictionary(g=>g.Key,g=>g.Select(e=>e.GetProperty("plan").GetInt64()).ToHashSet());
        var rows=new List<object>();long ticks=0;int cases=0;
        Parallel.ForEach(Directory.GetFiles(Old+"/formal/cases","*.input.json"),new ParallelOptions{MaxDegreeOfParallelism=8},path=>
        {
            string name=Path.GetFileName(path).Replace(".input.json","");
            var input=JsonSerializer.Deserialize<ShortPlanMatchupTools.Input>(File.ReadAllText(path),new JsonSerializerOptions{IncludeFields=true});
            var source=FormalAiSourceExport.Seed();
            var d=FormalAiMatchup.Build(source.Definition,(AiPolicyKind)input.heroPolicy,(AiPolicyKind)input.enemyPolicy,(FormalAiLayout)input.layout,input.mirror,
                new AiForecastSettings(360,true,true,new AiSamplingSettings(input.temperature,TopPMilli:input.topP,SignedDamage:true,HealthDefense:true),new AiShortPlanSettings()));
            if(d.BattleDefinitionHashValue!=input.definitionHash)throw new Exception("OLD_P_DEFINITION_CHANGED");
            using var file=File.OpenRead(path.Replace(".input.json",".heroReplay"));var replay=ReplayFile.Load(file,d);
            BattleSimulation sim=null;
            using var player=new ReplayPlayer(d,h=>sim=ProductionBattleComposition.Create(d,h.EncounterId,h.RuntimeInputs));
            player.Load(replay);player.Play();
            var initial=new Dictionary<long,LogicSnapshot>();var changes=new List<Change>();var found=new HashSet<long>();
            while(!player.IsComplete && player.Deviation==null)
            {
                var before=sim.CurrentSnapshot;player.AdvanceOneTick();var after=sim.CurrentSnapshot;
                if(player.Deviation!=null)throw new Exception("OLD_P_DEVIATION "+name+" "+player.Deviation);
                foreach(var plan in after.Plans.Where(p=>wanted.GetValueOrDefault(name)?.Contains(p.ActionPlanId)==true))
                    if(!initial.ContainsKey(plan.ActionPlanId))initial.Add(plan.ActionPlanId,before);
                foreach(var unit in after.Units)
                {
                    var previous=before.Units.First(u=>u.UnitId==unit.UnitId);
                    if(previous.X==unit.X && previous.Y==unit.Y)continue;
                    var events=replay.Records[(int)sim.Tick].Events.Select(ReplayEventComparison.Canonical).ToArray();
                    string cause=events.Any(e=>e.StartsWith("ForcedDisplacementResolvedEvent|"))?"forced-displacement"
                        : events.Any(e=>e.StartsWith("DodgeResolvedEvent|"))?"dodge"
                        :before.Plans.Concat(after.Plans).Any(p=>p.OwnerUnitId==unit.UnitId && d.FindAction(new ActionSpecId(p.ActionSpecId))?.Type==ActionType.Move
                            && p.StartTick<=sim.Tick && p.EndTick>=sim.Tick)?"ordinary-move":"other";
                    changes.Add(new Change(sim.Tick,unit.UnitId,cause,previous.X,previous.Y,unit.X,unit.Y));
                }
                foreach(long id in wanted.GetValueOrDefault(name)??new HashSet<long>())
                {
                    var plan=sim.AiActionPlanLookup.FindPlan(new ActionPlanId(id));
                    if(plan==null || plan.ImpactTick!=sim.Tick || !found.Add(id))continue;
                    var start=initial[id];long targetId=plan.PrimaryTargetUnitId?.Value??after.Units.Single(u=>u.UnitId!=plan.OwnerUnitId.Value).UnitId;
                    var actor0=start.Units.Single(u=>u.UnitId==plan.OwnerUnitId.Value);var target0=start.Units.Single(u=>u.UnitId==targetId);
                    var target=after.Units.Single(u=>u.UnitId==targetId);
                    var payload=(AttackPayloadSpec)d.FindAction(plan.ActionSpecId).Payload;
                    var originalArea=payload.Pattern.Directions[(int)plan.Facing].GetTranslated(new GridPoint(actor0.X,actor0.Y));
                    var originalTarget=VolumeFootprint.ResolveTriangles(d.VolumeDirectionsOf(new UnitDefinitionId(target0.DefinitionId)),new GridPoint(target0.X,target0.Y),(GridDirection)target0.Facing);
                    var intent=sim.IntentQueue.Intents.FirstOrDefault(i=>i.ActionPlanId.Value==id);
                    var contacts=sim.ConflictGraph?.Contacts.Where(c=>c.AttackerPlanId.Value==id || c.CounterpartyPlanId.Value==id)
                        .Select(c=>new{type=c.Type.ToString(),target=c.TargetUnitId.Value,c.CoveredBefore,c.CoveredAfter}).ToArray();
                    var motion=changes.Where(c=>c.tick>start.Tick && c.tick<=sim.Tick && (c.unit==targetId || c.unit==plan.OwnerUnitId.Value)).ToArray();
                    var row=new{ @case=name,plan=id,spec=plan.ActionSpecId.Value,submittedAfter=start.Tick,impact=sim.Tick,
                        originalCoverage=originalArea.Intersect(originalTarget).Any(),intentPresent=intent!=null,
                        targetMoved=motion.Any(c=>c.unit==targetId),actorMoved=motion.Any(c=>c.unit==plan.OwnerUnitId.Value),
                        targetAtSubmission=target0,targetAtImpact=target,contacts,motion};
                    lock(rows)rows.Add(row);
                }
            }
            if(found.Count!=(wanted.GetValueOrDefault(name)?.Count??0))throw new Exception("MISSED_DIAGNOSTIC "+name);
            lock(rows){ticks+=replay.Records.Count;cases++;}
            Console.WriteLine("DIAGNOSED "+name+" "+found.Count);
        });
        File.WriteAllText(Root+"/miss-diagnosis.json",JsonSerializer.Serialize(new{cases,ticks,misses=rows.Count,rows},new JsonSerializerOptions{WriteIndented=true}));
    }
}

