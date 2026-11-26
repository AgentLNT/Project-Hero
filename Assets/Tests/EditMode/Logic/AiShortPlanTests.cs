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
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using System.Collections.Generic;
using ProjectHero.Logic.Damage;

namespace ProjectHero.Logic.Tests
{
    public sealed class AiShortPlanTests
    {
        sealed class Rig : IDisposable
        {
            public readonly BattleSimulation Sim;
            public readonly ProductionBattleComposition Composition;
            public readonly AiDecisionContext Context;
            public Rig(long at=60, int horizon=360,Func<BattleDefinition,BattleDefinition> transform=null,
                Action<BattleSimulation,long> beforeStep=null,AiShortPlanSettings plans=null,AiPolicySituation situation=AiPolicySituation.Approach)
            {
                var input=new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense,situation,
                    Sampling:new AiSamplingSettings(SignedDamage:true,HealthDefense:true),ShortPlans:plans??new AiShortPlanSettings());
                var d=AiPolicyExperiment.Build(input);
                if(transform!=null){d=transform(d) with {BattleDefinitionHashValue=""};d=d with {BattleDefinitionHashValue=CombatExperimentDefinition.Hash(d)};}
                var runtime=new BattleRuntimeInputs(31,4);
                Composition=new ProductionBattleComposition(d,CombatExperimentDefinition.Encounter,runtime);
                // Forecast unit tests suppress autonomous observers while retaining the production window schedule and ingress.
                Sim=BattleSimulation.Create(d,CombatExperimentDefinition.Encounter,runtime,new BattleSimulationAssembly(turnWindowSchedule:Composition));
                Composition.Attach(Sim);
                for(long tick=0;tick<=at;tick++){beforeStep?.Invoke(Sim,tick);Sim.Step(tick,Sim.CommandIngress.FreezeTick(tick));}
                var id=CombatExperimentDefinition.Controller(1);
                Context=Composition.AiControllers.Single().BuildContext(Sim.DecisionSnapshotFor(id),id) with {
                    ForecastSettings=new AiForecastSettings(horizon,true,true,input.Sampling,input.ShortPlans) };
            }
            public CommandRequest Move(GridPoint destination)=>new CommandRequest(Context.NextTick,
                new ScheduleEditScope(Context.Snapshot.ScheduleRevision,Context.ExpectedWindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[]{new AddOrdinaryPlanOperation(1,new UnitId(1),
                    CombatExperimentDefinition.Action("move"),Destination:destination,Facing:GridDirection.East)}));
            public void Dispose()=>Sim.Dispose();
        }

        [Test]
        public void UsefulPlansTakePriorityOverNoEffectAttacksAndNegativeAttacksRemainSampleable()
        {
            using(var far=new Rig())
            {
                var decision=AiTacticalPolicy.Decide(far.Context,31,AiPolicyKind.CautiousDefense);
                var misses=decision.Evaluations.Where(e=>e.PlanKind=="DirectAttack" && e.Coverage==AiForecastCoverage.Complete && e.DamageDeltaQ10==0).ToArray();
                Assert.That(misses,Is.Not.Empty);
                Assert.That(misses.All(e=>e.RejectionCode==null && !e.IntentEligible && e.ProbabilityAfterQ32==0),Is.True);
                Assert.That(decision.Selected,Is.Not.Null);
                Assert.That(decision.Evaluations.Any(e=>e.Key.StartsWith("WAIT/",StringComparison.Ordinal)),Is.False);
            }
            using var near=new Rig(plans:new AiShortPlanSettings(PressureValueQ10:0),transform:d=>d with {
                Encounters=d.Encounters.Select(e=>e with {Slots=e.Slots.Select(s=>s.SlotId.Value=="slot.experiment.2"?s with {InitialPosition=new GridPoint(6,0)}:s).ToArray()}).ToArray(),
                Actions=d.Actions.Select(a=>a.ActionSpecId==CombatExperimentDefinition.Action("quick")?a with {
                    Payload=((AttackPayloadSpec)a.Payload) with {Strength=new AttackStrengthV05(PhysicalRecipe.Slash,64,AttackGrowthAttribute.Agility)}}:a).ToArray() });
            var weak=AiTacticalPolicy.Decide(near.Context,31,AiPolicyKind.CautiousDefense).Evaluations
                .Single(e=>e.Key.StartsWith("N/1/action.experiment.quick/",StringComparison.Ordinal));
            Assert.That(weak.DamageDeltaQ10,Is.GreaterThan(0));Assert.That(weak.Score,Is.LessThan(0));
            Assert.That(weak.IntentEligible,Is.True);Assert.That(weak.ProbabilityAfterQ32,Is.GreaterThan(0));
        }

        [Test]
        public void MissingTargetAndNonMovePrefixFailClosed()
        {
            using var r=new Rig();using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);
            var sequences=(IAiSequenceForecast)forecast;
            var gone=sequences.EvaluateSequence(r.Move(new GridPoint(8,0)),new AiFollowUpAttack(CombatExperimentDefinition.Action("quick"),new UnitId(999)),r.Context.ForecastSettings);
            Assert.That(gone.RejectionCode,Is.EqualTo("AI_SEQUENCE_TARGET_UNAVAILABLE"));
            var invalid=r.Move(new GridPoint(8,0)) with {Payload=new ScheduleEditPayload(new ScheduleEditOperation[]{new AddOrdinaryPlanOperation(1,new UnitId(1),
                CombatExperimentDefinition.Action("quick"),PrimaryTargetUnitId:new UnitId(2),Facing:GridDirection.East)})};
            Assert.That(sequences.EvaluateSequence(invalid,new AiFollowUpAttack(CombatExperimentDefinition.Action("quick"),new UnitId(2)),r.Context.ForecastSettings)
                .RejectionCode,Is.EqualTo("AI_SEQUENCE_FIRST_STEP_NOT_MOVE"));
        }

        [Test]
        public void SequenceDefenseUsesActualFundingAndPreservedReservationAcrossReset()
        {
            var fundId=new ActionSpecId("action.experiment.fund");
            using var r=new Rig(at:180,transform:d=>{
                var pattern=CombatExperimentDefinition.Build(new CombatExperimentScenario("funding")).AttackPatterns.Single();
                var quick=d.Actions.Single(a=>a.ActionSpecId==CombatExperimentDefinition.Action("quick"));
                var fund=quick with {ActionSpecId=fundId,Payload=((AttackPayloadSpec)quick.Payload) with {Pattern=pattern,
                    Strength=new AttackStrengthV05(PhysicalRecipe.Slash,2048,AttackGrowthAttribute.Agility)}};
                return d with {Actions=d.Actions.Append(fund).OrderBy(a=>a.ActionSpecId.Value,StringComparer.Ordinal).ToArray(),
                    AttackPatterns=d.AttackPatterns.Append(pattern).OrderBy(a=>a.AttackPatternId.Value,StringComparer.Ordinal).ToArray(),
                    ActionSets=d.ActionSets.Select(s=>s with {ActionSpecIds=s.ActionSpecIds.Append(fundId).OrderBy(a=>a.Value,StringComparer.Ordinal).ToArray()}).ToArray()};
            },beforeStep:(sim,tick)=>{
                if(tick!=100)return;
                var error=sim.CommandIngress.FindEntry(CombatExperimentDefinition.Controller(1)).SubmitAtDefaultTick(
                    new ScheduleEditScope(sim.ScheduleRevision,sim.CurrentTurnWindow.WindowId),new ScheduleEditPayload(new ScheduleEditOperation[]{
                        new AddOrdinaryPlanOperation(1,new UnitId(1),fundId,100,PrimaryTargetUnitId:new UnitId(2),Facing:GridDirection.East)}));
                Assert.That(error,Is.Null);
            });
            Assert.That(r.Sim.AdrenalineLedgerOf(new UnitId(2)).AvailableAdrenaline,Is.EqualTo(CombatValuesV05.ResourceUnitsPerPoint));
            using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);
            var first=r.Move(new GridPoint(8,0));var follow=new AiFollowUpAttack(CombatExperimentDefinition.Action("quick"),new UnitId(2));
            var control=((IAiSequenceForecast)forecast).EvaluateSequence(first,follow,r.Context.ForecastSettings);
            var replies=((IAiSequenceForecast)forecast).EvaluateSequenceDefenses(first,follow,r.Context.ForecastSettings);
            Assert.That(control.FollowUpStartTick,Is.LessThan(240));Assert.That(control.PlanImpactTick,Is.GreaterThan(240));
            Assert.That(replies.Any(p=>p.DefenderUnitId==2 && p.Request.Payload is ReactionCommandPayload payload && payload.ReactionKind==ReactionCommandKind.Block
                && p.Outcome.RejectionCode==null && p.Outcome.Coverage==AiForecastCoverage.Complete
                && p.Outcome.Units.Single(u=>u.UnitId==2).HealthQ10>control.Units.Single(u=>u.UnitId==2).HealthQ10),Is.True);
        }

        [Test]
        public void ZeroDamageInterruptStillHasTacticalEffect()
        {
            // The legacy damage protocol permits passive full resistance and independent momentum.
            // Keep this a valid definition: v0.5 deliberately forbids that combination.
            using var r=new Rig(situation:AiPolicySituation.EarlyInterrupt,transform:d=>d with {
                AdrenalineRules=d.AdrenalineRules with {UseCombatValuesV05=false},
                Units=d.Units.Select(u=>u with {CombatAttributes=null,BaseDamageResistanceQ10=u.UnitDefinitionId.Value=="unit.experiment.2"
                    ?new Dictionary<DamageChannelId,int> {{DamageChannels.PhysicalBlunt,1024},{DamageChannels.PhysicalSlash,1024},{DamageChannels.PhysicalPierce,1024}}
                    :new Dictionary<DamageChannelId,int>()}).ToArray(),
                Actions=d.Actions.Select(a=>a.Payload is AttackPayloadSpec attack?a with {Payload=attack with {
                    Strength=null,ForceMultiplier=10f,DamageComponents=new[]{new DamageComponentSpec(DamageChannels.PhysicalBlunt,1f,DamageTagMask.Blockable|DamageTagMask.Guardable)}}}:a).ToArray()},
                beforeStep:(sim,tick)=>{
                    if(tick!=1)return;
                    Assert.That(sim.CommandIngress.FindEntry(CombatExperimentDefinition.Controller(2)).SubmitAtDefaultTick(
                        new ScheduleEditScope(sim.ScheduleRevision,sim.CurrentTurnWindow.WindowId),new ScheduleEditPayload(new ScheduleEditOperation[]{
                            new AddOrdinaryPlanOperation(1,new UnitId(2),CombatExperimentDefinition.Action("heavy"),1,PrimaryTargetUnitId:new UnitId(1),Facing:GridDirection.West)})),Is.Null);
                });
            var decision=AiTacticalPolicy.Decide(r.Context,31,AiPolicyKind.SeekInterrupt);
            Assert.That(decision.Evaluations.Any(e=>e.Type==ActionType.Attack && e.DamageDeltaQ10==0 && e.InterruptedThreats>0 && e.IntentEligible),Is.True);
        }

        [Test]
        public void WholeMoveAttackForecastAddsFutureHitWithoutMutatingAuthority()
        {
            using var r=new Rig();string before=r.Sim.CurrentSnapshot.ComputeHash().ToString();
            using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);
            var request=r.Move(new GridPoint(8,0));
            var move=forecast.Evaluate(request,r.Context.ForecastSettings);
            var both=((IAiSequenceForecast)forecast).EvaluateSequence(request,
                new AiFollowUpAttack(CombatExperimentDefinition.Action("quick"),new UnitId(2)),r.Context.ForecastSettings);
            Assert.That(both.Coverage,Is.EqualTo(AiForecastCoverage.Complete));
            Assert.That(both.SequenceCompleted,Is.True);
            Assert.That(both.Units.Single(u=>u.UnitId==2).HealthQ10,Is.LessThan(move.Units.Single(u=>u.UnitId==2).HealthQ10));
            Assert.That(both.PlanEndTick,Is.GreaterThan(both.FirstStepEndTick));
            Assert.That(both.FollowUpRequestTick,Is.GreaterThan(both.FirstStepEndTick));
            Assert.That(r.Sim.CurrentSnapshot.ComputeHash().ToString(),Is.EqualTo(before));
        }

        [Test]
        public void MoveCrossingWindowWaitsForNextOwnWindowBeforeFollowUp()
        {
            using var r=new Rig(110);using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);
            var value=((IAiSequenceForecast)forecast).EvaluateSequence(r.Move(new GridPoint(8,0)),
                new AiFollowUpAttack(CombatExperimentDefinition.Action("quick"),new UnitId(2)),r.Context.ForecastSettings);
            Assert.That(value.RejectionCode,Is.Null);
            Assert.That(value.SequenceCompleted,Is.True);
            Assert.That(value.FirstStepEndTick,Is.GreaterThan(120));
            Assert.That(value.FollowUpRequestTick,Is.GreaterThanOrEqualTo(181));
            Assert.That(value.FollowUpStartTick,Is.GreaterThan(value.FirstStepEndTick));
        }

        [Test]
        public void UnfinishedFollowUpIsNotReportedAsComplete()
        {
            using var r=new Rig(horizon:50);using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);
            var value=((IAiSequenceForecast)forecast).EvaluateSequence(r.Move(new GridPoint(6,0)),
                new AiFollowUpAttack(CombatExperimentDefinition.Action("quick"),new UnitId(2)),r.Context.ForecastSettings);
            Assert.That(value.Coverage,Is.EqualTo(AiForecastCoverage.Uncovered));
            Assert.That(value.SequenceCompleted,Is.False);
            Assert.That(value.FollowUpRequestTick,Is.GreaterThan(0));
        }

        [Test]
        public void InvalidDestinationStillUsesKernelRejection()
        {
            using var r=new Rig();using var forecast=r.Context.ForecastFactory(r.Context.Snapshot);
            var value=((IAiSequenceForecast)forecast).EvaluateSequence(r.Move(new GridPoint(100,0)),
                new AiFollowUpAttack(CombatExperimentDefinition.Action("quick"),new UnitId(2)),r.Context.ForecastSettings);
            Assert.That(value.RejectionCode,Is.Not.Null);
            Assert.That(value.Coverage,Is.EqualTo(AiForecastCoverage.Rejected));
        }

        [Test]
        public void AutonomousShortPlanApproachesAndHitsThroughRealIngressAndReplays()
        {
            var result=AiPolicyExperiment.Run(new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense,AiPolicySituation.Approach,
                MaxTicks:360,Sampling:new AiSamplingSettings(SignedDamage:true,HealthDefense:true),ShortPlans:new AiShortPlanSettings()));
            Assert.That(result.Decisions.SelectMany(d=>d.Evaluations).Any(e=>e.Chosen && e.Type==ActionType.Move),Is.True);
            Assert.That(result.Decisions.SelectMany(d=>d.Evaluations).Any(e=>e.Chosen && e.Type==ActionType.Attack && e.NoDefenseDamageDeltaQ10>0),Is.True);
            Assert.That(result.Decisions.SelectMany(d=>d.Evaluations).Where(e=>e.Chosen && e.Type==ActionType.Attack)
                .All(e=>e.IntentEligible),Is.True);
            AiPolicyExperiment.VerifyReplay(result);
        }

        [Test]
        public void FarTargetUsesCompletedApproachStageThenReplansAnAttack()
        {
            var result=AiPolicyExperiment.Run(new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense,AiPolicySituation.Approach,
                MaxTicks:480,ApproachDistance:18,Sampling:new AiSamplingSettings(SignedDamage:true,HealthDefense:true),ShortPlans:new AiShortPlanSettings()));
            var moves=result.Decisions.SelectMany(d=>d.Evaluations).Where(e=>e.Chosen && e.PlanKind=="Approach").ToArray();
            Assert.That(moves,Is.Not.Empty);
            Assert.That(moves.All(e=>e.Coverage==AiForecastCoverage.Complete && e.ApproachUtility>0),Is.True);
            Assert.That(result.Events.OfType<DamageChannelResolvedEvent>().Any(e=>e.AttackerUnitId.Value==1 && e.AfterBlockDamageQ10>0),Is.True);
            Assert.That(result.Events.OfType<CommandRejectedEvent>(),Is.Empty);
            AiPolicyExperiment.VerifyReplay(result);
        }

        [Test]
        public void PlanningIdentityIsVersionedAndOptional()
        {
            var i=new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense,AiPolicySituation.Approach,Sampling:new AiSamplingSettings());
            string old=AiPolicyExperiment.Build(i).BattleDefinitionHashValue;
            var settings=new AiShortPlanSettings();
            string next=AiPolicyExperiment.Build(i with {ShortPlans=settings}).BattleDefinitionHashValue;
            Assert.That(next,Is.Not.EqualTo(old));
            Assert.That(AiPolicyExperiment.Build(i with {ShortPlans=settings with {MaxMoveEdges=2}}).BattleDefinitionHashValue,Is.Not.EqualTo(next));
            Assert.That(AiPolicyExperiment.Build(i).BattleDefinitionHashValue,Is.EqualTo(old));
        }
    }
}
