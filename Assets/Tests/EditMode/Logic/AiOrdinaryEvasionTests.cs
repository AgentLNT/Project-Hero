using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Logic.Tests
{
    public sealed class AiOrdinaryEvasionTests
    {
        [Test]
        public void PublishedCrossWindowAttackHasBoundedRealMoveRepliesThatReduceItsScore()
        {
            using var rig=new Rig(114);
            var hash=rig.Sim.CurrentSnapshot.ComputeHash();
            using var forecast=rig.Context.ForecastFactory(rig.Context.Snapshot);
            var control=forecast.Evaluate(rig.Attack,rig.Context.ForecastSettings);
            var replies=((IAiDefenseForecast)forecast).EvaluateDefenses(rig.Attack,rig.Context.ForecastSettings).Where(r=>r.IsOrdinaryEvasion).ToArray();
            Assert.That(replies.Length,Is.InRange(1,2));
            Assert.That(replies.All(r=>r.SubmittedAfterTick>=120 && r.SubmittedAfterTick%AiTacticalPolicy.OrdinaryThinkInterval==0
                && r.Request.Payload is ScheduleEditPayload edit && edit.Operations.Count==1 && r.Outcome.Coverage==AiForecastCoverage.Complete),Is.True);
            Assert.That(replies.Any(r=>r.Outcome.Units.Single(u=>u.UnitId==2).HealthQ10>control.Units.Single(u=>u.UnitId==2).HealthQ10),Is.True);
            var decision=AiTacticalPolicy.Decide(rig.Context,31,AiPolicyKind.CautiousDefense);
            var scored=decision.Evaluations.Single(e=>e.PlanKind=="DirectAttack" && e.Key.Contains("action.experiment.quick/") && !e.Key.Contains("/align/"));
            Assert.That(scored.OrdinaryEvasionScoreCap,Is.Not.Null);
            Assert.That(scored.Score.Value,Is.LessThanOrEqualTo(scored.OrdinaryEvasionScoreCap.Value));
            Assert.That(rig.Sim.CurrentSnapshot.ComputeHash(),Is.EqualTo(hash));
        }

        [TestCase(60,true)] // Defender cannot use an ordinary window before impact.
        [TestCase(114,false)] // The exact same attack under the historical configuration.
        public void NoMoveReplyWithoutARealWindowOrAnEnabledExperiment(int at,bool enable)
        {
            using var rig=new Rig(at,enable);
            using var forecast=rig.Context.ForecastFactory(rig.Context.Snapshot);
            var replies=((IAiDefenseForecast)forecast).EvaluateDefenses(rig.Attack,rig.Context.ForecastSettings);
            Assert.That(replies.Any(r=>r.IsOrdinaryEvasion),Is.False);
        }

        sealed class Rig:IDisposable
        {
            public readonly BattleSimulation Sim;
            public readonly AiDecisionContext Context;
            public readonly CommandRequest Attack;
            public Rig(long at,bool enable=true)
            {
                var input=new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense,AiPolicySituation.Approach,
                    Sampling:new AiSamplingSettings(SignedDamage:true,HealthDefense:true),
                    ShortPlans:new AiShortPlanSettings(ConsiderOrdinaryEvasion:enable),ApproachDistance:6);
                var definition=AiPolicyExperiment.Build(input);
                var runtime=new BattleRuntimeInputs(31,4);
                var composition=new ProductionBattleComposition(definition,CombatExperimentDefinition.Encounter,runtime);
                Sim=BattleSimulation.Create(definition,CombatExperimentDefinition.Encounter,runtime,new BattleSimulationAssembly(turnWindowSchedule:composition));
                composition.Attach(Sim);
                for(long tick=0;tick<=at;tick++)Sim.Step(tick,Sim.CommandIngress.FreezeTick(tick));
                var id=CombatExperimentDefinition.Controller(1);
                Context=composition.AiControllers.Single().BuildContext(Sim.DecisionSnapshotFor(id),id);
                Attack=new CommandRequest(Context.NextTick,new ScheduleEditScope(Context.Snapshot.ScheduleRevision,Context.ExpectedWindowId),
                    new ScheduleEditPayload(new ScheduleEditOperation[]{new AddOrdinaryPlanOperation(1,new UnitId(1),CombatExperimentDefinition.Action("quick"),
                        PrimaryTargetUnitId:new UnitId(2),Facing:GridDirection.East)}));
            }
            public void Dispose()=>Sim.Dispose();
        }
    }
}
