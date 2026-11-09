using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Logic.Tests
{
    public sealed class CombatValuesV05Tests
    {
        [Test] public void RealUnopposedHitsKillInSixFourAndEightContacts()
        {
            foreach (var sample in new[]{("quick",6),("heavy",4),("sweep",8)})
            {
                var result = CombatExperimentRunner.Run(new CombatExperimentScenario("htk_"+sample.Item1,sample.Item1));
                Assert.That(result.Outcome,Is.EqualTo("victory"),sample.Item1);
                var loss = result.Diagnostics.SelectMany(t=>t.Damage.Units).Where(u=>u.UnitId.Value == 2 && u.DamageQ10 > 0).ToArray();
                Assert.That(loss.Length,Is.EqualTo(sample.Item2),sample.Item1);
                Assert.That(loss.Sum(u=>u.DamageQ10),Is.EqualTo(24*1024));
                Assert.That(result.FinalSnapshot.Units.Single(u=>u.UnitId == 2).HealthQ10,Is.Zero);
                Assert.That(result.Events.OfType<CommandRejectedEvent>(),Is.Empty);
                CombatExperimentRunner.VerifyReplay(result);
            }
        }

        [Test] public void GrowthSharesStrengthAcrossBothLoadsAndEquipmentOnlyChangesHealth()
        {
            for (int p = 0; p <= 4; p++)
                foreach (var recipe in new[]{PhysicalRecipe.Pierce,PhysicalRecipe.Slash,PhysicalRecipe.Blunt})
                {
                    var strength = new AttackStrengthV05(recipe,768,AttackGrowthAttribute.Agility);
                    var plain = new CombatAttributesV05(1,1+p,1);
                    var armored = plain with {EquipmentHealth = 6};
                    Assert.That(CombatValuesV05.DamageQ10(plain,strength),Is.EqualTo(CombatValuesV05.DamageQ10(armored,strength)));
                    Assert.That(CombatValuesV05.MomentumUnits(plain,strength),Is.EqualTo(CombatValuesV05.MomentumUnits(armored,strength)));
                    Assert.That(armored.StabilityQ10,Is.EqualTo(plain.StabilityQ10));
                    Assert.That(armored.MaximumHealth,Is.EqualTo(plain.MaximumHealth+6));
                    Assert.That((long)CombatValuesV05.DamageQ10(plain,strength)*CombatValuesV05.MomentumCoefficient(recipe),
                        Is.EqualTo((long)CombatValuesV05.MomentumUnits(plain,strength)*CombatValuesV05.DamageCoefficient(recipe)));
                }
            Assert.That(CombatValuesV05.MomentumUnits(new CombatAttributesV05(),new AttackStrengthV05(PhysicalRecipe.Slash,768,AttackGrowthAttribute.Agility)),Is.EqualTo(2304));
        }

        [Test] public void IndependentStabilityStraddlesHeavyThresholdAndKnockdownRoundsUp()
        {
            foreach (var test in new[]{(10240,true),(10241,false),(10752,false)})
            {
                var result = CombatExperimentRunner.Run(new CombatExperimentScenario("threshold","heavy",StabilityQ10:test.Item1,MaxTicks:100));
                var hit = result.Events.OfType<DamageChannelResolvedEvent>().Single();
                Assert.That(hit.IncomingMomentumUnits,Is.EqualTo(10240));
                bool stagger = result.Events.OfType<UnitStateChangedEvent>().Any(e=>e.UnitId.Value == 2 && e.ToState == Units.UnitState.Staggered);
                Assert.That(stagger,Is.EqualTo(test.Item2));
            }
            var aggregate = TargetAggregator.Aggregate(new TargetAggregationInput(new UnitId(2),null,7,Array.Empty<TargetContact>()));
            Assert.That(aggregate.KnockdownThresholdUnits,Is.EqualTo(11));
        }

        [Test] public void ResourceFractionsAccumulateExactlyAndCapsIncludeReservations()
        {
            int point = CombatValuesV05.ResourceUnitsPerPoint;
            var ledger = new AdrenalineLedger(new UnitId(1),AdrenalineRules.CombatValuesV05Candidate);
            for (int i = 0; i < 3; i++) ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1),4096,0,0,0,0));
            Assert.That(ledger.AvailableAdrenaline,Is.EqualTo(point));
            Assert.That(ledger.TryReserveForReaction(new ActionPlanId(1),point),Is.Null);
            ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1),100000,0,0,0,0));
            Assert.That(ledger.AvailableAdrenaline+ledger.ReservedAdrenaline,Is.EqualTo(point*2));
            ledger.ConsumeAtTrigger(new ActionPlanId(1));
            ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1),0,0,0,0,0));
            Assert.That(ledger.AvailableAdrenaline,Is.EqualTo(point),"overflow cannot return after consumption");
            ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1),1,0,0,0,0));
            Assert.That(ledger.AvailableAdrenaline,Is.EqualTo(point+2),"one HealthQ10 quantum remains spendable");
        }

        [Test] public void CycleResetRetainsReservationAndOldRefundCannotCreateIncome()
        {
            int point=CombatValuesV05.ResourceUnitsPerPoint;
            var ledger=new AdrenalineLedger(new UnitId(1),AdrenalineRules.CombatValuesV05Candidate);
            ledger.InitializeFromBattleStart(point*2,0);
            ledger.TryReserveForReaction(new ActionPlanId(1),point);
            ledger.BeginOwnWindow();
            Assert.That(ledger.AvailableAdrenaline,Is.Zero); Assert.That(ledger.ReservedAdrenaline,Is.EqualTo(point));
            ledger.ReleaseForSourceThreatCancelled(new ActionPlanId(1));
            Assert.That(ledger.AvailableAdrenaline,Is.Zero);
            ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1),0,8192,0,0,0));
            Assert.That(ledger.AvailableAdrenaline,Is.EqualTo(point));
            ledger.TryReserveForReaction(new ActionPlanId(2),point);
            ledger.ReleaseForSourceThreatCancelled(new ActionPlanId(2));
            Assert.That(ledger.AvailableAdrenaline,Is.EqualTo(point));
        }

        [Test] public void RealClashReplacesDirectDamageWithBoundedSourceDamage()
        {
            var result=CombatExperimentRunner.Run(new CombatExperimentScenario("clash",Script:ExperimentScript.Clash,MaxTicks:250));
            Assert.That(result.Events.OfType<ClashParticipantResolvedEvent>().Count(),Is.EqualTo(2));
            var hit=result.Diagnostics.Single(t=>t.Tick==212).Damage.Units;
            Assert.That(hit.Single(u=>u.UnitId.Value==2).DamageQ10,Is.EqualTo(4300),"floor(6*1024*7/10)");
            Assert.That(hit.Where(u=>u.UnitId.Value==1).Sum(u=>u.DamageQ10),Is.Zero);
            Assert.That(result.Events.OfType<CommandRejectedEvent>(),Is.Empty);
            CombatExperimentRunner.VerifyReplay(result);
        }

        [Test] public void EarlierControlCancelsWindupButLaterControlKeepsCommittedDamage()
        {
            var early=CombatExperimentRunner.Run(new CombatExperimentScenario("early",Script:ExperimentScript.EarlyInterrupt,MaxTicks:250));
            var late=CombatExperimentRunner.Run(new CombatExperimentScenario("late",Script:ExperimentScript.LateInterrupt,MaxTicks:250));
            Assert.That(early.Diagnostics.SelectMany(t=>t.Damage.Units).Where(u=>u.UnitId.Value==1).Sum(u=>u.DamageQ10),Is.Zero);
            Assert.That(late.Diagnostics.SelectMany(t=>t.Damage.Units).Where(u=>u.UnitId.Value==1).Sum(u=>u.DamageQ10),Is.EqualTo(4096));
            Assert.That(early.Events.OfType<ClashParticipantResolvedEvent>(),Is.Empty);
        }

        [Test] public void FundedReactionUsesPublishedOpportunityAndNoSuccessfulDefenseReward()
        {
            foreach (var script in new[]{ExperimentScript.BlockWhenFunded,ExperimentScript.DodgeWhenFunded})
            {
                var result=CombatExperimentRunner.Run(new CombatExperimentScenario(script.ToString(),Script:script,MaxTicks:800));
                Assert.That(result.Decisions.Any(d=>d.Reason=="fixed-response-to-published-opportunity"),Is.True,script.ToString());
                Assert.That(result.Events.OfType<CommandRejectedEvent>(),Is.Empty,script.ToString());
                Assert.That(result.Events.OfType<AdrenalineLedgerChangedEvent>().Any(e=>e.ChangeKind==AdrenalineChangeKind.Reserved),Is.True,script.ToString());
                CombatExperimentRunner.VerifyReplay(result);
            }
        }

        [Test] public void SweepDealsPerTargetDamageAndIncomeSumsActualLoss()
        {
            var result=CombatExperimentRunner.Run(new CombatExperimentScenario("sweep_two","sweep",Script:ExperimentScript.SweepTwo,MaxTicks:70));
            var tick=result.Diagnostics.Single(t=>t.Tick==61);
            Assert.That(tick.Damage.Units.Sum(u=>u.DamageQ10),Is.EqualTo(6144));
            Assert.That(tick.Income.Single(f=>f.UnitId.Value==1).TotalFinalDamageDealtQ10,Is.EqualTo(6144));
        }

        [Test] public void NewDefinitionFieldsChangeHashAndMixedLegacyInputFailsBeforeStepZero()
        {
            var definition=CombatExperimentDefinition.Build(new CombatExperimentScenario("hash"));
            var units=definition.Units.ToArray(); units[0]=units[0] with {CombatAttributes=units[0].CombatAttributes with {Strength=2}};
            Assert.That(CombatExperimentDefinition.Hash(definition with {Units=units}),Is.Not.EqualTo(definition.BattleDefinitionHashValue));
            var actions=definition.Actions.ToArray(); int index=Array.FindIndex(actions,a=>a.ActionSpecId==CombatExperimentDefinition.Action("quick"));
            var payload=(AttackPayloadSpec)actions[index].Payload;
            actions[index]=actions[index] with {Payload=payload with {Strength=payload.Strength with {GrowthAttribute=AttackGrowthAttribute.Strength}}};
            Assert.That(CombatExperimentDefinition.Hash(definition with {Actions=actions}),Is.Not.EqualTo(definition.BattleDefinitionHashValue));
            units[0]=units[0] with {CombatAttributes=null};
            Assert.Throws<LogicDefinitionException>(()=>BattleInitializer.BuildInitialState(definition with {Units=units},CombatExperimentDefinition.Encounter,new BattleRuntimeInputs(31,4)));
        }

        [Test] public void SimultaneousOverkillCreditsOnlyOneTargetHealthPoolAcrossSources()
        {
            var definition=CombatExperimentDefinition.Build(new CombatExperimentScenario("overkill",Script:ExperimentScript.FocusTwo));
            var actions=definition.Actions.Select(a=>a.Payload is AttackPayloadSpec attack && a.ActionSpecId==CombatExperimentDefinition.Action("heavy")
                ? a with {Payload=attack with {Strength=attack.Strength with {BaseStrengthQ10=8192}}}
                : a.Payload is AttackPayloadSpec quick && a.ActionSpecId==CombatExperimentDefinition.Action("quick")
                    ? a with {Payload=quick with {Strength=quick.Strength with {BaseStrengthQ10=4096}}} : a).ToArray();
            definition=definition with {Actions=actions};definition=definition with {BattleDefinitionHashValue=CombatExperimentDefinition.Hash(definition)};
            using(var sim=ProductionBattleComposition.Create(definition,CombatExperimentDefinition.Encounter,new BattleRuntimeInputs(31,4)))
            {
                Advance(sim,0);Add(sim,1,"heavy",122,3,GridDirection.East);
                Advance(sim,181);Add(sim,2,"quick",182,3,GridDirection.East);
                Advance(sim,212);
                var report=sim.LastDamageCommitReport.Units.Single(u=>u.UnitId.Value==3);
                Assert.That(report.DamageQ10,Is.EqualTo(24*1024));
                Assert.That(sim.CurrentSnapshot.Units.Single(u=>u.UnitId==3).HealthQ10,Is.Zero);
                var facts=sim.LastAdrenalineAccrualFacts;
                Assert.That(facts.Sum(f=>f.TotalFinalDamageDealtQ10),Is.EqualTo(24*1024));
                Assert.That(facts.Sum(f=>f.TotalFinalDamageReceivedQ10),Is.EqualTo(24*1024));
                Assert.That(facts.Single(f=>f.UnitId.Value==1).TotalFinalDamageDealtQ10,Is.EqualTo(14746));
                Assert.That(facts.Single(f=>f.UnitId.Value==2).TotalFinalDamageDealtQ10,Is.EqualTo(9830));
            }
        }

        [Test] public void MultiOpponentClashDistributesResidualWithoutRepeatingOriginalDamage()
        {
            var definition=CombatExperimentDefinition.Build(new CombatExperimentScenario("multi_clash",Script:ExperimentScript.SweepTwo));
            var actions=definition.Actions.Select(a=>a.ActionSpecId==CombatExperimentDefinition.Action("heavy")?a with {Timing=new AttackTimingSpec(270,30)}
                :a.ActionSpecId==CombatExperimentDefinition.Action("quick")?a with {Timing=new AttackTimingSpec(211,30)}:a).ToArray();
            definition=definition with {Actions=actions};definition=definition with {BattleDefinitionHashValue=CombatExperimentDefinition.Hash(definition)};
            using(var sim=ProductionBattleComposition.Create(definition,CombatExperimentDefinition.Encounter,new BattleRuntimeInputs(31,4)))
            {
                Advance(sim,0);Add(sim,1,"heavy",123,2,GridDirection.East);
                Advance(sim,181);Add(sim,2,"quick",182,1,GridDirection.West);
                Advance(sim,362);Add(sim,3,"probe_slash",363,1,GridDirection.West);
                Advance(sim,393);
                Assert.That(sim.StagedResolution.ClashTerminatedPlanIds.Count,Is.EqualTo(3));
                Assert.That(sim.LastDamageCommitReport.Units.Where(u=>u.UnitId.Value!=1).Sum(u=>u.DamageQ10),Is.EqualTo(2456));
                Assert.That(sim.LastAdrenalineAccrualFacts.Sum(f=>f.TotalFinalDamageDealtQ10),Is.EqualTo(2456));
                Assert.That(sim.LastDamageCommitReport.Units.Where(u=>u.UnitId.Value==1).Sum(u=>u.DamageQ10),Is.Zero);
            }
        }

        [Test] public void GuardAndRangeConsequencesOccurInsideActualSolver()
        {
            var guard=CombatExperimentRunner.Run(new CombatExperimentScenario("guard",Script:ExperimentScript.Guard,MaxTicks:250));
            Assert.That(guard.Diagnostics.Single(t=>t.Tick==212).Damage.Units.Single(u=>u.UnitId.Value==2).DamageQ10,Is.EqualTo(3072));
            var open=CombatExperimentRunner.Run(new CombatExperimentScenario("open","heavy",ShortRange:true,MaxTicks:800));
            var wall=CombatExperimentRunner.Run(new CombatExperimentScenario("wall","heavy",ShortRange:true,Wall:true,MaxTicks:800));
            Assert.That(open.Outcome,Is.EqualTo("timeout"));Assert.That(wall.Outcome,Is.EqualTo("victory"));
            Assert.That(open.FinalSnapshot.Units.Single(u=>u.UnitId==2).X,Is.GreaterThan(6));
            Assert.That(wall.FinalSnapshot.Units.Single(u=>u.UnitId==2).X,Is.EqualTo(6));
        }

        [Test] public void ReplayFromTickZeroRepeatsOneHundredTimesIncludingFractionalResidualIncome()
        {
            var result=CombatExperimentRunner.Run(new CombatExperimentScenario("repeated",Script:ExperimentScript.Clash,MaxTicks:215));
            using(var player=new ReplayPlayer(result.Definition))
            {
                player.Load(result.Replay);
                for(int i=0;i<100;i++)
                {
                    if(i>0)player.Restart();player.Play();
                    while(!player.IsComplete&&player.Deviation==null)player.AdvanceOneTick();
                    Assert.That(player.Deviation,Is.Null);Assert.That(player.IsComplete,Is.True);
                }
            }
        }

        private static void Advance(BattleSimulation sim,long target)
        {
            while(sim.Tick<target){long tick=sim.Tick+1;sim.Step(tick,sim.CommandIngress.FreezeTick(tick));}
        }
        private static void Add(BattleSimulation sim,long owner,string action,long start,long target,GridDirection facing)
        {
            var request=new CommandRequest(sim.Tick+1,new ScheduleEditScope(sim.ScheduleRevision,sim.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[]{new AddOrdinaryPlanOperation(1,new UnitId(owner),CombatExperimentDefinition.Action(action),start,
                    PrimaryTargetUnitId:new UnitId(target),Facing:facing)}));
            Assert.That(sim.CommandIngress.FindEntry(CombatExperimentDefinition.Controller(owner)).Submit(request),Is.Null);
        }
    }
}
