using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Resources;

namespace ProjectHero.Logic.Tests
{
    public sealed class AiContinuationTests
    {
        sealed class Rig:IDisposable
        {
            public readonly BattleSimulation Sim;
            public readonly AiDecisionContext Context;
            public Rig(int horizon=360,bool enabled=true,Func<BattleDefinition,BattleDefinition> transform=null,int at=60,bool fund=false,bool shortRange=false)
            {
                var input=new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense,shortRange?AiPolicySituation.Approach:AiPolicySituation.OpenAttack,
                    Sampling:new AiSamplingSettings(SignedDamage:true,HealthDefense:true),ShortPlans:new AiShortPlanSettings(),
                    Continuations:new AiContinuationSettings(enabled),ApproachDistance:6);
                var d=AiPolicyExperiment.Build(input);
                if(transform!=null){d=transform(d) with {BattleDefinitionHashValue=""};d=d with{BattleDefinitionHashValue=CombatExperimentDefinition.Hash(d)};}
                var runtime=new BattleRuntimeInputs(31,4);var composition=new ProductionBattleComposition(d,CombatExperimentDefinition.Encounter,runtime);
                Sim=BattleSimulation.Create(d,CombatExperimentDefinition.Encounter,runtime,new BattleSimulationAssembly(turnWindowSchedule:composition));
                composition.Attach(Sim);
                for(long tick=0;tick<=at;tick++)
                {
                    if(fund && tick==100)Assert.That(Sim.CommandIngress.FindEntry(CombatExperimentDefinition.Controller(1)).SubmitAtDefaultTick(
                        new ScheduleEditScope(Sim.ScheduleRevision,Sim.CurrentTurnWindow.WindowId),new ScheduleEditPayload(new ScheduleEditOperation[]{
                            new AddOrdinaryPlanOperation(1,new UnitId(1),CombatExperimentDefinition.Action("quick"),PrimaryTargetUnitId:new UnitId(2),Facing:GridDirection.East)})),Is.Null);
                    Sim.Step(tick,Sim.CommandIngress.FreezeTick(tick));
                }
                var id=CombatExperimentDefinition.Controller(1);
                Context=composition.AiControllers.Single().BuildContext(Sim.DecisionSnapshotFor(id),id) with {
                    ForecastSettings=new AiForecastSettings(horizon,true,true,input.Sampling,input.ShortPlans,input.Continuations)};
            }
            public CommandRequest Attack()=>new CommandRequest(Context.NextTick,
                new ScheduleEditScope(Context.Snapshot.ScheduleRevision,Context.ExpectedWindowId),new ScheduleEditPayload(new ScheduleEditOperation[]{
                    new AddOrdinaryPlanOperation(1,new UnitId(1),CombatExperimentDefinition.Action("quick"),PrimaryTargetUnitId:new UnitId(2),Facing:GridDirection.East)}));
            public void Dispose()=>Sim.Dispose();
        }
        static AiFollowUpAttack Follow=>new AiFollowUpAttack(CombatExperimentDefinition.Action("quick"),new UnitId(2),true);

        [Test]
        public void SecondAttackUsesRealWindowCadenceAndDoesNotMutateAuthority()
        {
            using var r=new Rig();var before=r.Sim.CurrentSnapshot.ComputeHash();
            using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);var sequences=(IAiSequenceForecast)forecast;
            var single=forecast.Evaluate(r.Attack(),r.Context.ForecastSettings);
            var both=sequences.EvaluateSequence(r.Attack(),Follow,r.Context.ForecastSettings);
            Assert.That(both.RejectionCode,Is.Null);Assert.That(both.Coverage,Is.EqualTo(AiForecastCoverage.Complete));
            Assert.That(both.SequenceCompleted,Is.True);Assert.That(both.FollowUpPlanId,Is.Not.EqualTo(both.FirstPlanId));
            Assert.That(both.FollowUpRequestTick,Is.GreaterThan(both.FirstStepEndTick));
            Assert.That((both.FollowUpRequestTick-1)%AiTacticalPolicy.OrdinaryThinkInterval,Is.Zero);
            Assert.That(both.Units.Single(u=>u.UnitId==2).HealthQ10,Is.LessThan(single.Units.Single(u=>u.UnitId==2).HealthQ10));
            Assert.That(r.Sim.CurrentSnapshot.ComputeHash(),Is.EqualTo(before));
        }

        [Test]
        public void DamageFromFirstAttackCanFundARealSecondAttackResponse()
        {
            using var r=new Rig(transform:d=>d with {Actions=d.Actions.Select(a=>a.ActionSpecId==Follow.ActionSpecId
                ?a with{Timing=new AttackTimingSpec(35,1),Payload=((AttackPayloadSpec)a.Payload) with{
                    Strength=new AttackStrengthV05(PhysicalRecipe.Slash,2048,AttackGrowthAttribute.Agility)}}:a).ToArray()});
            Assert.That(r.Context.Snapshot.VisibleUnits.Single(u=>u.UnitId==2).AvailableAdrenaline,Is.Zero);
            using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);var sequences=(IAiSequenceForecast)forecast;
            var replies=sequences.EvaluateSequenceDefenses(r.Attack(),Follow,r.Context.ForecastSettings);
            var second=replies.Where(p=>p.Key.StartsWith("NO_FIRST_REPLY/THEN/",StringComparison.Ordinal)
                && p.Request.Payload is ReactionCommandPayload).ToArray();
            Assert.That(second,Is.Not.Empty);
            Assert.That(second.All(p=>p.Outcome.RejectionCode==null && p.Outcome.Coverage==AiForecastCoverage.Complete),Is.True);
            Assert.That(second.All(p=>p.Outcome.ResourceExchanges.Any(e=>e.UnitId==2 && e.Amount>0)),Is.True);
            Assert.That(second.SelectMany(p=>p.Outcome.ResourceExchanges).All(e=>e.AvailableAfter>=0),Is.True);
        }

        [Test]
        public void ResetBeforeNextOwnWindowDoesNotInventSecondAttackFunding()
        {
            using var r=new Rig();using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);
            var replies=((IAiSequenceForecast)forecast).EvaluateSequenceDefenses(r.Attack(),Follow,r.Context.ForecastSettings);
            Assert.That(replies.Any(p=>p.Request.Payload is ReactionCommandPayload),Is.False);
            var outcome=((IAiSequenceForecast)forecast).EvaluateSequence(r.Attack(),Follow,r.Context.ForecastSettings);
            Assert.That(outcome.FollowUpRequestTick,Is.GreaterThan(120));
            Assert.That(outcome.ResourceExchanges.Any(e=>e.UnitId==2 && e.AvailableBefore>e.AvailableAfter),Is.True);
        }

        [Test]
        public void DefenderDodgeCanCauseOneRealApproachBeforeSecondAttack()
        {
            using var r=new Rig(at:180,fund:true,shortRange:true,transform:d=>d with {Actions=d.Actions.Select(a=>a.ActionSpecId==Follow.ActionSpecId
                ?a with{Timing=new AttackTimingSpec(35,1),Payload=((AttackPayloadSpec)a.Payload) with{
                    Strength=new AttackStrengthV05(PhysicalRecipe.Slash,2048,AttackGrowthAttribute.Agility)}}:a).ToArray()});
            Assert.That(r.Context.Snapshot.VisibleUnits.Single(u=>u.UnitId==2).AvailableAdrenaline,Is.GreaterThan(0));
            using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);
            var replies=((IAiSequenceForecast)forecast).EvaluateSequenceDefenses(r.Attack(),Follow,r.Context.ForecastSettings);
            Assert.That(replies.Any(p=>p.Outcome.BridgePlanId>0 && p.Outcome.SequenceCompleted && p.Outcome.Coverage==AiForecastCoverage.Complete),Is.True);
            Assert.That(replies.All(p=>p.Outcome.RejectionCode==null),Is.True);
        }

        [Test]
        public void TwoResponsesAreAdmittedAgainstTheirActualResourceAndReservationHistory()
        {
            using var r=new Rig(at:180,fund:true,transform:d=>d with {Actions=d.Actions.Select(a=>a.ActionSpecId==Follow.ActionSpecId
                ?a with{Timing=new AttackTimingSpec(35,1),Payload=((AttackPayloadSpec)a.Payload) with{
                    Strength=new AttackStrengthV05(PhysicalRecipe.Slash,2048,AttackGrowthAttribute.Agility)}}:a).ToArray()});
            using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);
            var replies=((IAiSequenceForecast)forecast).EvaluateSequenceDefenses(r.Attack(),Follow,r.Context.ForecastSettings);
            var twice=replies.Where(p=>p.Key.Contains("/THEN/") && !p.Key.StartsWith("NO_FIRST_REPLY/",StringComparison.Ordinal)).ToArray();
            Assert.That(twice,Is.Not.Empty);
            Assert.That(twice.All(p=>p.Outcome.RejectionCode==null && p.Outcome.Coverage==AiForecastCoverage.Complete),Is.True);
            Assert.That(twice.Any(p=>p.Outcome.ResourceExchanges.Count(e=>e.UnitId==2 && e.ChangeKind==(int)AdrenalineChangeKind.Consumed)>=2),Is.True);
            Assert.That(twice.SelectMany(p=>p.Outcome.ResourceExchanges).All(e=>e.AvailableAfter>=0),Is.True);
        }

        [Test]
        public void DisabledTemplatesRejectAttackPrefixButKeepValuationExperimentValid()
        {
            using var r=new Rig(enabled:false);Assert.That(r.Context.ForecastSettings.Validate(),Is.Null);
            using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);
            Assert.That(((IAiSequenceForecast)forecast).EvaluateSequence(r.Attack(),Follow,r.Context.ForecastSettings).RejectionCode,
                Is.EqualTo("AI_CONTINUATION_DISABLED"));
            Assert.That(AiTacticalPolicy.Decide(r.Context,31,AiPolicyKind.CautiousDefense).Evaluations.Any(e=>e.PlanKind=="AttackAttack"),Is.False);
        }

        [Test]
        public void UnobservedSecondAttackCannotEnterSamplingPool()
        {
            using var r=new Rig(horizon:70);using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);
            var both=((IAiSequenceForecast)forecast).EvaluateSequence(r.Attack(),Follow,r.Context.ForecastSettings);
            Assert.That(both.Coverage,Is.EqualTo(AiForecastCoverage.Uncovered));Assert.That(both.SequenceCompleted,Is.False);
            var decision=AiTacticalPolicy.Decide(r.Context,31,AiPolicyKind.CautiousDefense);
            Assert.That(decision.Evaluations.Where(e=>e.PlanKind=="AttackAttack").All(e=>!e.Score.HasValue && e.ProbabilityAfterQ32==0),Is.True);
        }

        [Test]
        public void ContinuationSwitchesAreHashedAndRequireSignedShortPlanDefenseConfiguration()
        {
            var common=new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense,AiPolicySituation.OpenAttack,
                Sampling:new AiSamplingSettings(SignedDamage:true,HealthDefense:true),ShortPlans:new AiShortPlanSettings());
            var p=AiPolicyExperiment.Build(common);var v=AiPolicyExperiment.Build(common with{Continuations=new AiContinuationSettings(false)});
            var c=AiPolicyExperiment.Build(common with{Continuations=new AiContinuationSettings()});
            Assert.That(new[]{p.BattleDefinitionHashValue,v.BattleDefinitionHashValue,c.BattleDefinitionHashValue}.Distinct().Count(),Is.EqualTo(3));
            Assert.That(new AiForecastSettings(360,Continuations:new AiContinuationSettings()).Validate(),Is.Not.Null);
            Assert.That(new AiContinuationSettings(MaxFirstAttacks:5).Validate(),Is.Not.Null);
        }
    }
}
