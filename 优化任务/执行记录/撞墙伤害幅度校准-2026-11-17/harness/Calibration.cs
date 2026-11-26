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
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;

static class Calibration
{
    const string Root = "优化任务/执行记录/撞墙伤害幅度校准-2026-11-17";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly BattleDefinition Source = FormalAiSourceExport.Definition();
    static readonly AiPolicyKind[] Policies = { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense };
    static readonly string[] Attacks = { "quick_slash", "spear_thrust", "wide_cleave", "heavy_smash", "whirlwind" };
    record Model(float R, bool LongPush = false, bool SoftHeavy = false)
    {
        public string Id => "R" + R.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (LongPush ? "-long" : SoftHeavy ? "-soft-heavy" : "-ratio");
    }
    record Loss(long Tick, long Source, long Target, long Q10, bool Wall);
    static string SpecName(ActionSpec action) => Attacks.Single(n => action.ActionSpecId.Value.Contains("." + n + "."));
    static double Ratio(string name, Model model) => name switch {
        "quick_slash" => .48, "spear_thrust" => .30, "wide_cleave" => .72,
        "heavy_smash" => model.LongPush ? 2.5 : model.SoftHeavy ? 9.75 / model.R : 1.6,
        "whirlwind" => model.LongPush ? 2.2 : 1.5, _ => throw new Exception(name)
    };
    static ActionSpec TuneAttack(ActionSpec action, Model model)
    {
        var p = (AttackPayloadSpec)action.Payload;
        double transfer = p.ImpactProfileId.Value switch { "impact.blunt" => 1, "impact.slash" => .6, "impact.pierce" => .3, _ => throw new Exception("profile") };
        return action with { Payload = p with { ForceMultiplier = (float)(Ratio(SpecName(action), model) / transfer) } };
    }
    static BattleDefinition Tune(BattleDefinition d, Model m, int actionSpeed, int moveSpeed)
    {
        d = d with {
            RulesVersion = "calibration.wall-impact.1", Rules = BattleRules.WalkingDisplacementV2,
            Units = d.Units.Select(u => u with { Mass = 1, MomentumSpeed = m.R, InitialHealth = 50,
                ActionSpeed = actionSpeed, MoveSpeed = moveSpeed, CombatAttributes = null }).ToArray(),
            Actions = d.Actions.Select(a => a.Type == ActionType.Attack ? TuneAttack(a, m)
                : a.Type == ActionType.Block ? a with { AdrenalineCost = 3 }
                : a.Type == ActionType.Dodge ? a with { AdrenalineCost = 2 } : a).ToArray()
        };
        return Stamp(d);
    }
    static BattleDefinition Stamp(BattleDefinition d) => d with { BattleDefinitionHashValue = BattleDefinitionHash.Compute(
        d.RulesVersion, d.TicksPerSecond, LegacyIdMigrationManifest.LibraryVolumes.Count, d.Rules, d.ConcurrentAction,
        d.ReactionRules, d.AdrenalineRules, d.FactionModel, d.DamageChannels, d.ImpactProfiles, d.Units, d.Actions,
        d.AttackPatterns, d.Volumes, d.MovementPatterns, d.ActionSets, d.StatusEffects, d.Encounters, d.DefaultDynamicSpawnPolicy) };
    static void Write(string path, object value) => File.WriteAllText(Root + "/" + path, JsonSerializer.Serialize(value, Json) + "\n");
    static void Main(string[] args)
    {
        Directory.CreateDirectory(Root + "/replays");
        if (args.Length == 0 || args[0] == "fixed") Fixed();
        else if (args[0] == "ai") Ai(new Model(float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture),
            args.Contains("long"), args.Contains("soft-heavy")), args.Contains("walls"));
        else throw new Exception("mode");
    }
    static BattleDefinition FixedDefinition(Model model, int a, int m, int gap)
    {
        var d = CombatExperimentDefinition.Build(new CombatExperimentScenario("calibration"));
        var pattern = ((AttackPayloadSpec)d.Actions.First(x => x.Type == ActionType.Attack).Payload).Pattern;
        var attacks = Source.Actions.Where(x => x.Type == ActionType.Attack && x.ActionSpecId.Value.EndsWith("radius_1"))
            .Select(x => x with { Payload = ((AttackPayloadSpec)x.Payload) with { Pattern = pattern } }).ToArray();
        var actions = d.Actions.Where(x => x.Type != ActionType.Attack).Concat(attacks).OrderBy(x => x.ActionSpecId.Value).ToArray();
        var set = d.ActionSets[0] with { ActionSpecIds = actions.Select(x => x.ActionSpecId).ToArray() };
        var e = d.Encounters[0];
        if (gap >= 0)
        {
            int edge = VolumeFootprint.ResolveCells(d.Volumes[0].Directions, e.Slots[1].InitialPosition, e.Slots[1].InitialFacing).Max(p => p.X);
            e = e with { StaticWalls = Enumerable.Range(-20, 41).Where(y => ((edge+y)&1)==0).Select(y => new GridPoint(edge + 2 * (gap + 1), y)).ToArray() };
        }
        d = d with { Actions = actions, ActionSets = new[] { set }, Encounters = new[] { e },
            ImpactProfiles = Source.ImpactProfiles, AdrenalineRules = Source.AdrenalineRules };
        return Tune(d, model, a, m);
    }
    static void Submit(BattleSimulation sim, long owner, ActionSpecId action, long start, long target, GridDirection facing)
    {
        var binding = sim.Definition.Encounters[0].Controllers.Single(c => c.ControlledSlots[0] == sim.Definition.Encounters[0].Slots[(int)owner - 1].SlotId);
        var context = AiDecisionContextFactory.Build(sim.DecisionSnapshotFor(binding.ControllerId), binding.ControllerId,
            sim.AiReactionOpportunities, sim.AiActionPlanLookup, sim.MovementPathCalculator);
        var payload = new ScheduleEditPayload(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, new UnitId(owner), action,
            start, PrimaryTargetUnitId: new UnitId(target), Facing: facing) });
        var rejected = sim.CommandIngress.FindEntry(binding.ControllerId).Submit(new CommandRequest(context.NextTick,
            new ScheduleEditScope(context.Snapshot.ScheduleRevision, context.ExpectedWindowId), payload));
        if (rejected != null) throw new Exception(rejected.ReasonCode);
    }
    static void Verify(BattleDefinition d, BattleReplay replay, string? save)
    {
        using var stream = new MemoryStream(); ReplayFile.Save(stream, replay); stream.Position = 0;
        var loaded = ReplayFile.Load(stream, d);
        using var player = new ReplayPlayer(d); player.Load(loaded); player.Play();
        while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
        if (player.Deviation != null || !player.IsComplete) throw new Exception("replay " + player.Deviation);
        if (save != null) File.WriteAllBytes(Root + "/replays/" + save + ".heroReplay", stream.ToArray());
    }
    static void Fixed()
    {
        var models = new[] { 6f, 10f, 12.5f, 15f, 20f, 25f, 30f, 40f, 50f, 1000f }.Select(r => new Model(r))
            .Concat(new[] { new Model(20, true), new Model(25, true), new Model(9, SoftHeavy: true) }).ToArray();
        var rows = new List<object>();
        foreach (var model in models) foreach (var speed in new[] { (20,30), (24,20) })
        foreach (int gap in new[] { -1,0,1,2 }) foreach (string name in Attacks) foreach (bool guard in new[] { false, true })
        {
            var d = FixedDefinition(model, speed.Item1, speed.Item2, gap);
            var attack = d.Actions.Single(x => x.Type == ActionType.Attack && SpecName(x) == name);
            int impact = 160 + TickQuantization.ResolveWindupTicks(((AttackTimingSpec)attack.Timing).BaseWindupTicks, 20, speed.Item1);
            using var sim = ProductionBattleComposition.Create(d, d.Encounters[0].EncounterId, new BattleRuntimeInputs(31,4));
            var rec = new ReplayRecorder(sim); var events = new List<LogicEvent>();
            for (int t = 0; t < 350 && !sim.IsEnded; t++)
            {
                if (t == 1) Submit(sim, 1, attack.ActionSpecId, 160, 2, GridDirection.East);
                if (guard && t == 182) Submit(sim, 2, CombatExperimentDefinition.Action("guard"), impact - 1, 1, GridDirection.West);
                rec.CaptureBeforeStep(); var step = sim.Step(t, sim.CommandIngress.FreezeTick(t));
                rec.RecordCommittedStep(step); events.AddRange(step.Events.Events);
            }
            if (events.Any(e => e is CommandRejectedEvent || e is CommandIngressRejectedEvent)) throw new Exception("fixed command rejected");
            var hit = events.OfType<DamageChannelResolvedEvent>().Single();
            int resistance = MomentumQuantizer.QuantizeControlResistanceUnits(1, model.R);
            long momentum = hit.AfterMomentumResistanceUnits;
            int expectedSteps = (int)(momentum / resistance);
            bool contact = gap >= 0 && expectedSteps > gap;
            long expectedWallQ10 = contact ? ((momentum - gap * resistance) * 1024 + 50) / 100 : 0;
            long rawWall = events.OfType<WallImpactDamageEvent>().Sum(x => x.RawDamageQ10);
            int steps = events.OfType<ForcedDisplacementResolvedEvent>().Sum(x => x.AppliedSteps);
            int expectedActualSteps = contact ? gap : expectedSteps;
            if (rawWall != expectedWallQ10 || steps != expectedActualSteps)
                throw new Exception($"fixed mismatch {model.Id} {name} gap{gap} guard{guard}: wall {rawWall}/{expectedWallQ10}, steps {steps}/{expectedActualSteps}");
            string id = $"{model.Id}-A{speed.Item1}-M{speed.Item2}-{name}-gap{gap}-guard{guard}";
            bool save = speed.Item1 == 20 && name == "heavy_smash" &&
                (model.R == 25 || gap == 0 && !guard && (model.R == 6 || model.SoftHeavy));
            Verify(d, rec.BuildReplay(), save ? id : null);
            if (save) Write("replays/" + id + ".facts.json", events.Select(ReplayEventComparison.Canonical).ToArray());
            rows.Add(new { Id=id, Model=model, A=speed.Item1, Move=speed.Item2, Gap=gap, Attack=name, Guard=guard,
                DirectQ10=hit.AfterBlockDamageQ10, MomentumUnits=momentum, RUnits=resistance, Steps=steps,
                WallRawQ10=rawWall, WallAppliedQ10=events.OfType<WallImpactDamageEvent>().Sum(x => x.AppliedDamageQ10),
                WallTick=events.OfType<WallImpactDamageEvent>().Select(x => (long?)x.Tick).SingleOrDefault(), ImpactTick=hit.Tick,
                RemainingHpQ10=sim.CurrentSnapshot.Units.Single(x=>x.UnitId==2).HealthQ10, ReplayVerified=true, Hash=d.BattleDefinitionHashValue });
        }
        Write("fixed.json", rows); Console.WriteLine($"fixed {rows.Count} passed");
        var clash = new List<object>();
        foreach (var model in models) foreach (string left in Attacks) foreach (string right in Attacks)
        {
            int lm=(int)Math.Round(model.R*Ratio(left,model)*100,MidpointRounding.AwayFromZero);
            int rm=(int)Math.Round(model.R*Ratio(right,model)*100,MidpointRounding.AwayFromZero);
            var input=new MomentumClashInput(new[] {
                new ClashParticipant(new UnitId(1),new ActionPlanId(1),MomentumPacket.Require(GridDirection.East,lm,ProjectHero.Logic.Damage.ImpactProfiles.Blunt)),
                new ClashParticipant(new UnitId(2),new ActionPlanId(2),MomentumPacket.Require(GridDirection.West,rm,ProjectHero.Logic.Damage.ImpactProfiles.Blunt)) },
                new[] { new ClashPair(new ActionPlanId(1),new ActionPlanId(2)) });
            var outcome=MomentumClashSolver.Solve(input);
            clash.Add(new { Model=model, Left=left,Right=right, Residuals=outcome.ResidualImpacts.Select(x=>new { Target=x.RecipientUnitId.Value,
                x.ResidualMomentumUnits, LegacyDamage=MomentumQuantizer.ClashResidualComponent(x.ResidualMomentumUnits).RawAmount }).ToArray() });
        }
        Write("clash.json",clash);
    }
    static void Ai(Model model, bool walls)
    {
        string filename="ai-"+model.Id+(walls?"-walls":"-open")+".jsonl";
        using var output=new StreamWriter(Root+"/"+filename,false);
        var layouts=walls?new[]{FormalAiLayout.Near}:new[]{FormalAiLayout.Near,FormalAiLayout.Main,FormalAiLayout.Far};
        int count=0;
        foreach(var speed in new[]{(20,30),(24,20)}) foreach(var layout in layouts)
        foreach(var hero in Policies) foreach(var enemy in Policies) foreach(bool mirror in new[]{false,true})
        {
            string id=$"{model.Id}-A{speed.Item1}-M{speed.Item2}-{layout}-{hero}-{enemy}-{mirror}-{walls}";
            try
            {
                var d=FormalAiMatchup.Build(Source,hero,enemy,layout,mirror,new AiForecastSettings(360,true,true,
                    new AiSamplingSettings(SignedDamage:true,HealthDefense:true),new AiShortPlanSettings(PressureValueQ10:0)));
                if(walls)
                {
                    var e=d.Encounters[0]; var right=e.Slots.OrderByDescending(x=>x.InitialPosition.X).First();
                    var u=d.FindUnit(right.DefinitionId); var vol=d.Volumes.Single(v=>v.VolumeSpecId==u.VolumeSpecId);
                    int edge=VolumeFootprint.ResolveCells(vol.Directions,right.InitialPosition,right.InitialFacing).Max(p=>p.X);
                    var rightWall=Enumerable.Range(-16,33).Where(y=>((edge+y)&1)==0).Select(y=>new GridPoint(edge+2,y));
                    var left=e.Slots.OrderBy(x=>x.InitialPosition.X).First(); var lu=d.FindUnit(left.DefinitionId);
                    var lv=d.Volumes.Single(v=>v.VolumeSpecId==lu.VolumeSpecId);
                    int ledge=VolumeFootprint.ResolveCells(lv.Directions,left.InitialPosition,left.InitialFacing).Min(p=>p.X);
                    e=e with{StaticWalls=rightWall.Concat(Enumerable.Range(-16,33).Where(y=>((ledge+y)&1)==0).Select(y=>new GridPoint(ledge-2,y))).ToArray()};
                    d=d with{Encounters=new[]{e}};
                }
                d=Tune(d,model,speed.Item1,speed.Item2);
                using var sim=ProductionBattleComposition.Create(d,d.Encounters[0].EncounterId,new BattleRuntimeInputs(31,4));
                var rec=new ReplayRecorder(sim);var windows=sim.CurrentSnapshot.Units.ToDictionary(u=>u.UnitId,_=>new List<long>());
                var remaining=windows.Keys.ToDictionary(x=>x,_=>50*1024L);var owners=new Dictionary<long,long>();
                var losses=new List<Loss>();var deaths=new Dictionary<long,long>();int rej=0,wallCount=0,starts=0,clashes=0;
                long wallRaw=0,wallApplied=0;
                for(int t=0;t<720&&!sim.IsEnded;t++)
                {
                    rec.CaptureBeforeStep();var result=sim.Step(t,sim.CommandIngress.FreezeTick(t));rec.RecordCommittedStep(result);
                    foreach(var ev in result.Events.Events)
                    {
                        if(ev is ActionPlanCreatedEvent plan) owners[plan.ActionPlanId]=plan.OwnerUnitId;
                        if(ev is TurnWindowOpenedEvent win) windows[win.OwnerUnitId.Value].Add(t+win.DurationTicks);
                        if(ev is UnitDiedEvent dead) deaths[dead.UnitId.Value]=t;
                        if(ev is CommandRejectedEvent||ev is CommandIngressRejectedEvent)rej++;
                        if(ev is ForcedDisplacementStartedEvent)starts++;
                        if(ev is ClashParticipantResolvedEvent)clashes++;
                        if(ev is DamageChannelResolvedEvent hit && hit.AfterBlockDamageQ10>0)
                        {long amount=Math.Min(remaining[hit.TargetUnitId.Value],hit.AfterBlockDamageQ10);remaining[hit.TargetUnitId.Value]-=amount;
                         losses.Add(new Loss(t,hit.AttackerUnitId.Value,hit.TargetUnitId.Value,amount,false));}
                        if(ev is WallImpactDamageEvent wall)
                        {wallCount++;wallRaw+=wall.RawDamageQ10;wallApplied+=wall.AppliedDamageQ10;remaining[wall.TargetUnitId.Value]-=wall.AppliedDamageQ10;
                         if(!owners.TryGetValue(wall.SourcePlanId.Value,out long attacker))throw new Exception("wall source not found");
                         losses.Add(new Loss(t,attacker,wall.TargetUnitId.Value,wall.AppliedDamageQ10,true));}
                    }
                }
                foreach(var u in sim.CurrentSnapshot.Units)if(Math.Max(0,u.HealthQ10)!=remaining[u.UnitId])throw new Exception("damage accounting mismatch");
                var actors=windows.Select(p=>new{Id=p.Key,Windows=p.Value.Count,Deadline=p.Value.Count>=2?(long?)p.Value[1]:null,
                    DamageQ10=losses.Where(x=>x.Source==p.Key&&x.Tick<(p.Value.Count>=2?p.Value[1]:720)).Sum(x=>x.Q10)}).ToArray();
                bool pass=sim.IsEnded&&actors.Any(x=>x.DamageQ10>=51200);
                bool verify=layout==FormalAiLayout.Near&&hero==enemy&&!mirror;
                if(verify)Verify(d,rec.BuildReplay(),id);
                var row=new{Id=id,Model=model,A=speed.Item1,Move=speed.Item2,Layout=layout.ToString(),Hero=hero.ToString(),Enemy=enemy.ToString(),Mirror=mirror,Walls=walls,
                    Ended=sim.IsEnded,Ticks=sim.Tick+1,Pass=pass,Rejections=rej,WallCount=wallCount,WallRawQ10=wallRaw,WallAppliedQ10=wallApplied,Displacements=starts,ClashParticipants=clashes,
                    DeathsBeforeFirstWindow=deaths.Count(p=>windows[p.Key].Count==0),Actors=actors,ReplayVerified=verify,Hash=d.BattleDefinitionHashValue};
                output.WriteLine(JsonSerializer.Serialize(row));output.Flush();
            }
            catch(Exception error){output.WriteLine(JsonSerializer.Serialize(new{Id=id,Error=error.ToString()}));output.Flush();}
            if(++count%18==0)Console.WriteLine(filename+" "+count);
        }
    }
}
