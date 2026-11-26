using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ProjectHero.Authoring;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Editor;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;

internal static class FullMoves
{
    const string Root = "优化任务/执行记录/统一动量缩放与完整招式对照-2026-11-19";
    const int Hp = 50 * 1024;
    const int Limit = 720;
    static readonly BattleDefinition Source = FormalAiSourceExport.Definition();
    static readonly AiPolicyKind[] Policies = { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense };
    static readonly Dictionary<string,(PhysicalRecipe Recipe,int Q10)> Moves = new() {
        ["quick_slash"] = (PhysicalRecipe.Slash,1024), ["spear_thrust"] = (PhysicalRecipe.Pierce,1024),
        ["wide_cleave"] = (PhysicalRecipe.Slash,768), ["heavy_smash"] = (PhysicalRecipe.Blunt,2048)
    };
    static bool Allowed(ActionSpec a) => a.Type != ActionType.Attack || Moves.Keys.Any(n => a.ActionSpecId.Value.Contains("." + n + "."));
    static string Name(string spec) => Moves.Keys.SingleOrDefault(n => spec.Contains("." + n + ".")) ?? spec;
    static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    static BattleDefinition Define(int strength, int action, int move, FormalAiLayout layout, AiPolicyKind hero, AiPolicyKind enemy,
        bool mirror, bool walls)
    {
        var d = FormalAiMatchup.Build(Source, hero, enemy, layout, mirror,
            new AiForecastSettings(360, true, true, new AiSamplingSettings(SignedDamage:true,HealthDefense:true),
                new AiShortPlanSettings(PressureValueQ10:0)));
        var units = d.Units.Select(u => CombatValuesV05.Unit(u.UnitDefinitionId,
            new CombatAttributesV05(ProbeStabilityQ10:20*1024),u.ActionSetId,u.VolumeSpecId) with
            { InitialHealth=50,ActionSpeed=action,MoveSpeed=move }).ToArray();
        var attacks = d.Actions.Where(Allowed).Select(a => {
            if(a.Type is ActionType.Block or ActionType.Dodge) return a with { AdrenalineCost=CombatValuesV05.ResourceUnitsPerPoint };
            if(a.Payload is not AttackPayloadSpec p) return a;
            var sample = Moves[Name(a.ActionSpecId.Value)];
            return a with { Payload=p with { DamageComponents=Array.Empty<ProjectHero.Logic.Damage.DamageComponentSpec>(),ForceMultiplier=1f,
                Strength=new AttackStrengthV05(sample.Recipe,sample.Q10*strength,
                    sample.Recipe == PhysicalRecipe.Blunt ? AttackGrowthAttribute.Strength : AttackGrowthAttribute.Agility) } };
        }).ToArray();
        var allowed = attacks.Select(a => a.ActionSpecId).ToHashSet();
        var sets = d.ActionSets.Select(s => s with { ActionSpecIds=s.ActionSpecIds.Where(allowed.Contains).ToArray() }).ToArray();
        var e=d.Encounters[0];
        if(walls) {
            var cells=new SortedSet<(int X,int Y)>();
            foreach(var slot in e.Slots) {
                bool atRight=slot.InitialPosition.X>0;
                var vol=d.Volumes.Single(v=>v.VolumeSpecId==d.FindUnit(slot.DefinitionId).VolumeSpecId);
                var footprint=VolumeFootprint.ResolveCells(vol.Directions,slot.InitialPosition,slot.InitialFacing);
                int edge=atRight?footprint.Max(p=>p.X):footprint.Min(p=>p.X);
                foreach(int y in Enumerable.Range(-16,33).Where(y=>((edge+y)&1)==0)) cells.Add((edge+(atRight?2:-2),y));
            }
            e=e with { StaticWalls=cells.Select(p=>new GridPoint(p.X,p.Y)).ToArray() };
        }
        d=d with { RulesVersion=CombatValuesV05.Version,Rules=BattleRules.WalkingDisplacementV2,
            Units=units,Actions=attacks,ActionSets=sets,Encounters=new[]{e},AdrenalineRules=AdrenalineRules.CombatValuesV05Candidate };
        Check(CombatValuesV05.ValidateDefinition(d)==null,"v0.5 validation failed");
        return d with { BattleDefinitionHashValue=BattleDefinitionHash.Compute(d.RulesVersion,d.TicksPerSecond,
            LegacyIdMigrationManifest.LibraryVolumes.Count,d.Rules,d.ConcurrentAction,d.ReactionRules,d.AdrenalineRules,
            d.FactionModel,d.DamageChannels,d.ImpactProfiles,d.Units,d.Actions,d.AttackPatterns,d.Volumes,d.MovementPatterns,d.ActionSets,
            d.StatusEffects,d.Encounters,d.DefaultDynamicSpawnPolicy) };
    }

    record Loss(int Tick,long Source,long Amount,bool Wall);
    static void Replay(BattleDefinition d,ReplayRecorder rec,string id)
    {
        using var stream=new MemoryStream();ReplayFile.Save(stream,rec.BuildReplay());stream.Position=0;
        var replay=ReplayFile.Load(stream,d);using var player=new ReplayPlayer(d);player.Load(replay);player.Play();
        while(!player.IsComplete&&player.Deviation==null)player.AdvanceOneTick();
        Check(player.IsComplete&&player.Deviation==null,"replay: "+player.Deviation);
        File.WriteAllBytes(Path.Combine(Root,"replays",id+".heroReplay"),stream.ToArray());
    }

    static Dictionary<string,int> Counter() => Moves.Keys.ToDictionary(k=>k,_=>0);
    static void Increment(Dictionary<string,int> counts,string name)
    {
        if(counts.ContainsKey(name))counts[name]++;
    }

    internal static void Run(int strength,bool walls,int percent)
    {
        Check(strength is 3 or 4,"Expected common strength scale 3 or 4");
        string scene=walls?"wall":"open";
        using var output=File.CreateText(Path.Combine(Root,$"ai-S{strength}-M{percent}-{scene}.jsonl"));
        int cases=0;
        foreach(var speed in new[]{(A:20,M:30),(A:24,M:20)})
        foreach(var layout in walls?new[]{FormalAiLayout.Near}:new[]{FormalAiLayout.Near,FormalAiLayout.Main,FormalAiLayout.Far})
        foreach(var hero in Policies)
        foreach(var enemy in Policies)
        foreach(bool mirror in new[]{false,true})
        {
            string id=$"S{strength}-M{percent}-{scene}-A{speed.A}M{speed.M}-{layout}-{hero}-{enemy}-mirror{mirror}";
            try {
                var d=Define(strength,speed.A,speed.M,layout,hero,enemy,mirror,walls);
                if(cases==0) {
                    var attr=d.Units[0].CombatAttributes;
                    var parameters=d.Actions.Where(a=>a.Type==ActionType.Attack&&a.ActionSpecId.Value.EndsWith(".radius_1")).Select(a=>{
                        var p=(AttackPayloadSpec)a.Payload;return new { Spec=a.ActionSpecId.Value,Recipe=p.Strength.Recipe.ToString(),
                            Q10=p.Strength.BaseStrengthQ10,DamageQ10=CombatValuesV05.DamageQ10(attr,p.Strength),
                            MomentumUnits=CombatValuesV05.MomentumUnits(attr,p.Strength),Timing=a.Timing,Pattern=p.Pattern.AttackPatternId.Value,
                            TargetPolicy=p.TargetPolicy.ToString() };}).ToArray();
                    File.WriteAllText(Path.Combine(Root,$"parameters-S{strength}-M{percent}.json"),JsonSerializer.Serialize(
                        new{Strength=strength,Percent=percent,Hp=50,R=20,SourceHash=Source.BattleDefinitionHashValue,Parameters=parameters}));
                }
                using var sim=ProductionBattleComposition.Create(d,d.Encounters[0].EncounterId,new BattleRuntimeInputs(31,4));
                var rec=new ReplayRecorder(sim);var windows=sim.CurrentSnapshot.Units.ToDictionary(u=>u.UnitId,_=>new List<long>());
                var remaining=windows.Keys.ToDictionary(x=>x,_=>(long)Hp);
                var specs=new Dictionary<long,string>();var owners=new Dictionary<long,long>();
                var deaths=new Dictionary<long,int>();var losses=new List<Loss>();
                var planned=Counter();var impacts=Counter();var damageByAttack=Moves.Keys.ToDictionary(k=>k,_=>0L);
                long wallRaw=0,wallApplied=0;
                int wallCount=0,displaced=0,staggered=0,knockdowns=0,clashes=0,blocks=0,dodges=0;
                var trace=new List<string>();
                bool verify=layout==FormalAiLayout.Near&&hero==enemy&&!mirror;
                for(int t=0;t<Limit&&!sim.IsEnded;t++) {
                    rec.CaptureBeforeStep();var step=sim.Step(t,sim.CommandIngress.FreezeTick(t));rec.RecordCommittedStep(step);
                    foreach(var ev in step.Events.Events) {
                        if(ev is CommandRejectedEvent||ev is CommandIngressRejectedEvent)throw new Exception("rejected: "+ev);
                        if(verify&&(ev is DamageChannelResolvedEvent||ev is WallImpactDamageEvent||ev is TurnWindowOpenedEvent
                            ||ev is UnitDiedEvent||ev is AdrenalineLedgerChangedEvent||ev is TargetAggregateResolvedEvent
                            ||ev is ClashParticipantResolvedEvent||ev is ReactionPlanResolvedEvent))
                            trace.Add(ReplayEventComparison.Canonical(ev));
                        if(ev is ActionPlanCreatedEvent plan) {
                            owners[plan.ActionPlanId]=plan.OwnerUnitId;specs[plan.ActionPlanId]=Name(plan.ActionSpecId);
                            Increment(planned,specs[plan.ActionPlanId]);
                        }
                        if(ev is TurnWindowOpenedEvent win)windows[win.OwnerUnitId.Value].Add(t+win.DurationTicks);
                        if(ev is UnitDiedEvent die)deaths[die.UnitId.Value]=t;
                        if(ev is ForcedDisplacementStartedEvent)displaced++;
                        if(ev is TargetAggregateResolvedEvent target) { if(target.IsStaggered)staggered++;if(target.IsKnockedDown)knockdowns++; }
                        if(ev is ClashParticipantResolvedEvent)clashes++;
                        if(ev is ReactionPlanResolvedEvent reaction&&reaction.RewardsSuccess) {
                            if(reaction.Kind is ReactionResolutionKind.BlockedFully or ReactionResolutionKind.BlockedPartially)blocks++;
                            if(reaction.Kind==ReactionResolutionKind.DodgeAvoidedContact)dodges++;
                        }
                        if(ev is DamageChannelResolvedEvent hit) {
                            string name=specs[hit.AttackPlanId.Value];Increment(impacts,name);
                            long applied=Math.Min(remaining[hit.TargetUnitId.Value],hit.AfterBlockDamageQ10);
                            Check(applied>=0,"negative damage");
                            remaining[hit.TargetUnitId.Value]-=applied;
                            losses.Add(new Loss(t,hit.AttackerUnitId.Value,applied,false));
                            if(damageByAttack.ContainsKey(name))damageByAttack[name]+=applied;
                        }
                        if(ev is WallImpactDamageEvent wall) {
                            wallCount++;wallRaw+=wall.RawDamageQ10;wallApplied+=wall.AppliedDamageQ10;
                            remaining[wall.TargetUnitId.Value]-=wall.AppliedDamageQ10;
                            losses.Add(new Loss(t,owners[wall.SourcePlanId.Value],wall.AppliedDamageQ10,true));
                            string name=specs[wall.SourcePlanId.Value];if(damageByAttack.ContainsKey(name))damageByAttack[name]+=wall.AppliedDamageQ10;
                        }
                    }
                }
                foreach(var u in sim.CurrentSnapshot.Units)Check(Math.Max(0,u.HealthQ10)==remaining[u.UnitId],"HP accounting "+u.UnitId);
                var actors=windows.Select(pair=>new{Id=pair.Key,Windows=pair.Value.Count,Deadline=pair.Value.Count>=2?(long?)pair.Value[1]:null,
                    DamageQ10=losses.Where(l=>l.Source==pair.Key&&l.Tick<(pair.Value.Count>=2?pair.Value[1]:Limit)).Sum(l=>l.Amount),
                    TotalQ10=losses.Where(l=>l.Source==pair.Key).Sum(l=>l.Amount),
                    RemainingQ10=remaining[pair.Key],DeathTick=deaths.TryGetValue(pair.Key,out int tick)?(int?)tick:null}).ToArray();
                bool pass=sim.IsEnded&&actors.Any(a=>a.DamageQ10==Hp);
                if(verify) {
                    Replay(d,rec,id);
                    File.WriteAllText(Path.Combine(Root,"replays",id+".facts.json"),JsonSerializer.Serialize(trace));
                }
                output.WriteLine(JsonSerializer.Serialize(new{Id=id,Strength=strength,Percent=percent,Scene=scene,A=speed.A,Move=speed.M,
                    Layout=layout.ToString(),Hero=hero.ToString(),Enemy=enemy.ToString(),Mirror=mirror,Ended=sim.IsEnded,Tick=sim.Tick,Pass=pass,
                    EarlyDeaths=actors.Count(a=>a.Windows==0&&a.DeathTick!=null),WallCount=wallCount,WallRawQ10=wallRaw,WallAppliedQ10=wallApplied,
                    Displacements=displaced,Staggers=staggered,Knockdowns=knockdowns,ClashParticipants=clashes,BlockSuccess=blocks,DodgeSuccess=dodges,
                    Planned=planned,Impacts=impacts,DamageByAttackQ10=damageByAttack,Actors=actors,ReplayVerified=verify,Hash=d.BattleDefinitionHashValue}));
                output.Flush();
                if(++cases%18==0)Console.WriteLine($"{id}: {cases}");
            } catch(Exception error) { throw new InvalidOperationException(id,error); }
        }
    }
}
