using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
static class PressureProbe
{
    public static void Run()
    {
        foreach(bool funded in new[]{false,true})
        {
            var input=new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense,funded?AiPolicySituation.Approach:AiPolicySituation.OpenAttack,
                Sampling:new AiSamplingSettings(SignedDamage:true,HealthDefense:true),ShortPlans:new AiShortPlanSettings(),Continuations:new AiContinuationSettings(),ApproachDistance:6);
            var d=AiPolicyExperiment.Build(input);
            d=d with {Actions=d.Actions.Select(a=>a.ActionSpecId==CombatExperimentDefinition.Action("quick")?a with{Timing=new AttackTimingSpec(35,1)}:a).ToArray(),BattleDefinitionHashValue=""};
            d=d with {BattleDefinitionHashValue=CombatExperimentDefinition.Hash(d)};
            var runtime=new BattleRuntimeInputs(31,4);var comp=new ProductionBattleComposition(d,CombatExperimentDefinition.Encounter,runtime);
            using var sim=BattleSimulation.Create(d,CombatExperimentDefinition.Encounter,runtime,new BattleSimulationAssembly(turnWindowSchedule:comp));comp.Attach(sim);
            for(long tick=0;tick<=(funded?180:60);tick++)
            {
                if(funded && tick==100)sim.CommandIngress.FindEntry(CombatExperimentDefinition.Controller(1)).SubmitAtDefaultTick(new ScheduleEditScope(sim.ScheduleRevision,sim.CurrentTurnWindow.WindowId),
                    new ScheduleEditPayload(new ScheduleEditOperation[]{new AddOrdinaryPlanOperation(1,new UnitId(1),CombatExperimentDefinition.Action("quick"),PrimaryTargetUnitId:new UnitId(2),Facing:GridDirection.East)}));
                sim.Step(tick,sim.CommandIngress.FreezeTick(tick));
            }
            var id=CombatExperimentDefinition.Controller(1);var c=comp.AiControllers.Single().BuildContext(sim.DecisionSnapshotFor(id),id);
            using var forecast=c.ForecastFactory(c.Snapshot);
            var request=new CommandRequest(c.NextTick,new ScheduleEditScope(c.Snapshot.ScheduleRevision,c.ExpectedWindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[]{new AddOrdinaryPlanOperation(1,new UnitId(1),CombatExperimentDefinition.Action("quick"),PrimaryTargetUnitId:new UnitId(2),Facing:GridDirection.East)}));
            var follow=new AiFollowUpAttack(CombatExperimentDefinition.Action("quick"),new UnitId(2),true);
            var control=((IAiSequenceForecast)forecast).EvaluateSequence(request,follow,c.ForecastSettings);
            var replies=((IAiSequenceForecast)forecast).EvaluateSequenceDefenses(request,follow,c.ForecastSettings);
            File.WriteAllText("优化任务/执行记录/连续施压规划-2026-11-14/probe-"+funded+".json",JsonSerializer.Serialize(new{c.Snapshot.VisibleUnits,control,replies},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PROBE {funded} Aend={control.FirstStepEndTick} B={control.FollowUpRequestTick}/{control.PlanImpactTick}/{control.PlanEndTick} replies={replies.Count}");
        }
    }
}
