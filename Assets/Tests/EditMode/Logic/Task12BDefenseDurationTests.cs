using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Tests
{
    public class Task12BDefenseDurationTests
    {
        static readonly ActionSpecId Guard = Task08DefenseCoverageTests.GuardId;
        static readonly ActionSpecId Light = new ActionSpecId("action.t12b.light");
        static readonly UnitId Hero = Task09A2Fixture.Hero, Defender = Task09A2Fixture.Monster;
        static readonly GridPoint Destination = Task08DefenseCoverageTests.Destination;
        static TargetContactKey Key => new TargetContactKey(TargetContactType.DirectHit,Hero,new ActionPlanId(1),Defender,new ActionPlanId(100));
        static BattleDefinition Definition(int blockDuration = 60, int guardRatio = 512)
        {
            var d = Task08DefenseCoverageTests.Definition(force: .001f);
            var heavy = d.Actions.First(a => a.Type == ActionType.Attack);
            var light = heavy with { ActionSpecId = Light, Timing = new AttackTimingSpec(2, 1),
                Payload = ((AttackPayloadSpec)heavy.Payload) with { Pattern = new AttackPatternSpec(
                    new AttackPatternId("attack.pattern.t12b.light"), Enumerable.Range(0,12).Select(i =>
                        new DirectionalTriangleSet((GridDirection)i, new[] { new TrianglePoint(-13,0,1),
                            new TrianglePoint(-5,0,1), new TrianglePoint(9,0,1) })).ToArray()),
                    AllowedTargetRelations = TargetRelationMask.Hostile | TargetRelationMask.Neutral } };
            return d with { Actions = d.Actions.Select(a => a.Type == ActionType.Block ? a with { Timing = new BlockReactionTimingSpec(blockDuration) }
                : a.Type == ActionType.Dodge ? a with { Timing = new DodgeReactionTimingSpec(30) }
                : a.Type == ActionType.Guard ? a with { Timing = new GuardTimingSpec(60), Payload = new GuardPayloadSpec(
                    new Dictionary<DamageChannelId,int> { [DamageChannels.PhysicalBlunt] = guardRatio }, guardRatio, DefenseTagMask.Guardable) } : a).Concat(new[] { light }).ToArray(),
                ActionSets = d.ActionSets.Select(s => s with { ActionSpecIds = s.ActionSpecIds.Concat(new[] { Light }).ToArray() }).ToArray() };
        }
        static StepResult Advance(BattleSimulation s, long tick) { StepResult result=null; while(s.Tick < tick) result=Task09A2Fixture.StepNext(s); return result; }
        // Explicit fixture seeding of secondary attackers/defenders, followed by the real start gate and Step pipeline.
        // No defense implementation, intent, trigger decision, or result is replaced.
        static ActionPlan Seed(BattleSimulation s, UnitId owner, ActionSpecId spec, long start, UnitId? target = null)
        {
            var result = s.PlanFactory.TryCreateOrdinary(new OrdinaryPlanRequest(owner, spec, GridDirection.East,
                target, null, s.CurrentTurnWindow.WindowId, start), s.Tick);
            Assert.That(result.Succeeded, Is.True, result.RejectionCode);
            typeof(ActionScheduleAuthority).GetMethod("RegisterPlan", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Invoke(s.ScheduleAuthority, new object[] { result.Plan });
            return result.Plan;
        }
        static BattleSimulation Prepared(out ReactionOpportunityRuntime opportunity, int duration = 60)
        {
            var s = Task09A2Fixture.NewSim(Definition(duration));
            Task09A2Fixture.ArrangeStartedAttack(s, Defender);
            opportunity = Task09A2Fixture.OpenOpportunityFor(s, Defender);
            return s;
        }
        static string Accept(BattleSimulation s, ReactionOpportunityRuntime o, ReactionCommandKind kind,
            long? start, out ActionPlan plan)
        {
            string id = kind == ReactionCommandKind.Block ? Task09A2Fixture.BlockSpecId : Task09A2Fixture.DodgeSpecId;
            Task09A2Fixture.Submit(s, Task09A2Fixture.AiId, new CommandRequest(s.Tick+1,
                new ReactionCommandScope(o.Id), new ReactionCommandPayload(kind, new ActionSpecId(id),
                    kind == ReactionCommandKind.Dodge ? Destination : (GridPoint?)null, start)));
            var result = Task09A2Fixture.StepNext(s);
            plan = s.ScheduleAuthority.Registry.ActivePlans.FirstOrDefault(p=>p.IsReaction);
            return Task09A2Fixture.AllRejectionCodes(result).FirstOrDefault();
        }
        static GuardPayloadSpec Payload(int damage, int momentum) => new GuardPayloadSpec(
            new Dictionary<DamageChannelId,int> { [DamageChannels.PhysicalBlunt] = damage,
                [DamageChannels.ElementalFire] = damage/2 }, momentum, DefenseTagMask.Guardable);
        static DamageComponentSpec[] Components => new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10,
            DamageTagMask.Guardable | DamageTagMask.Blockable) };

        [Test] public void GuardHasNoWindupAndIsEffectiveFromStartTick()
        {
            using var s = Task09A2Fixture.NewSim(Definition()); s.WindowManager.ScheduleWindow(0,Defender,180); Task09A2Fixture.Step(s,0);
            var guard=Seed(s,Defender,Guard,1); Task09A2Fixture.StepNext(s);
            Assert.That(guard.ResolvedWindupTicks,Is.Zero); Assert.That(guard.RecoveryTicks,Is.Zero);
            Assert.That(guard.EndTick,Is.EqualTo(61)); Assert.That(guard.DefenseQuotaRemainingQ10,Is.EqualTo(1024));
            Assert.That(s.FindUnitStateMachine(Defender).CurrentState,Is.EqualTo(UnitState.Guarding));
        }
        [Test] public void GuardEndTickExclusiveGivesNoResistance()
        {
            using var s=Task09A2Fixture.NewSim(Definition()); s.WindowManager.ScheduleWindow(0,Defender,180); Task09A2Fixture.Step(s,0);
            var g=Seed(s,Defender,Guard,1); Seed(s,Hero,Light,59,Defender); Advance(s,61);
            Assert.That(g.IsActiveAt(61),Is.False); Assert.That(s.StagedResolution.Aggregations.Single().TotalDamageQ10,Is.EqualTo(10*1024));
        }
        [Test] public void GuardQuotaDeductsOncePerTickRegardlessOfContactCount()
        {
            using var s=Task09A2Fixture.NewSim(Definition()); s.WindowManager.ScheduleWindow(0,Defender,180); Task09A2Fixture.Step(s,0);
            var g=Seed(s,Defender,Guard,1); Seed(s,Hero,Light,3,Defender); Seed(s,Task09A2Fixture.Ally,Light,3,Defender); var result=Advance(s,5);
            Assert.That(g.DefenseQuotaRemainingQ10,Is.EqualTo(512));
            Assert.That(result.Events.Events.OfType<GuardQuotaConsumedEvent>().Count(),Is.EqualTo(1));
            Assert.That(s.StagedResolution.Aggregations.Single().TotalDamageQ10,Is.EqualTo(10*1024));
        }
        [Test] public void GuardQuotaUsesMaxActualRatioAcrossChannelsAndMomentum()
        {
            var effective=GuardQuota.EffectivePayload(Payload(800,900),300);
            Assert.That(GuardQuota.ContactDebit(effective,Components,AttackTagMask.Blockable,10),Is.EqualTo(300));
            Assert.That(GuardQuota.ContactDebit(Payload(400,800),Components,AttackTagMask.Blockable,10),Is.EqualTo(800));
            Assert.That(GuardQuota.ContactDebit(Payload(400,800),Components,AttackTagMask.None,10),Is.EqualTo(400));
            var channels=new GuardPayloadSpec(new Dictionary<DamageChannelId,int> { [DamageChannels.PhysicalBlunt]=200,
                [DamageChannels.ElementalFire]=900 },400,DefenseTagMask.Guardable);
            Assert.That(GuardQuota.ContactDebit(GuardQuota.EffectivePayload(channels,700),Components.Concat(new[] {
                new DamageComponentSpec(DamageChannels.ElementalFire,10,DamageTagMask.Guardable|DamageTagMask.Blockable)}).ToArray(),
                AttackTagMask.Blockable,10),Is.EqualTo(700));
        }
        [Test] public void GuardTerminatesWithQuotaExhaustedAfterResolvingThatTick()
        {
            using var s=Task09A2Fixture.NewSim(Definition()); s.WindowManager.ScheduleWindow(0,Defender,180); Task09A2Fixture.Step(s,0);
            var g=Seed(s,Defender,Guard,1); Seed(s,Hero,Light,3,Defender); Seed(s,Task09A2Fixture.Ally,Light,4,Defender);
            Seed(s,Task09A2Fixture.Neutral,Light,5,Defender); Advance(s,6);
            Assert.That(g.TerminationReason,Is.EqualTo(ActionTerminationReason.GuardQuotaExhausted)); Assert.That(g.TerminalTick,Is.EqualTo(6));
            Assert.That(s.StagedResolution.Aggregations.Single().TotalDamageQ10,Is.EqualTo(5*1024)); Advance(s,7);
            Assert.That(s.StagedResolution.Aggregations.Single().TotalDamageQ10,Is.EqualTo(10*1024));
        }
        [Test] public void GuardCumulativeResistanceNeverExceedsLargestSingleTickPayload()
        {
            var rng=new Random(1202);
            for(int sample=0;sample<2000;sample++)
            {
                var ids=new[] {DamageChannels.PhysicalBlunt,DamageChannels.ElementalFire};
                var p=new GuardPayloadSpec(new Dictionary<DamageChannelId,int> { [ids[0]]=rng.Next(0,1024),[ids[1]]=rng.Next(0,1024) },
                    rng.Next(0,1024),DefenseTagMask.Guardable); int quota=1024;
                var passive=new Dictionary<DamageChannelId,int> { [ids[0]]=rng.Next(0,1025),[ids[1]]=rng.Next(0,1025) };
                var resistedDamage=new long[2]; var maxDamage=new long[2]; long resistedMomentum=0,maxMomentum=0;
                for(int tick=0;tick<64;tick++)
                {
                    var e=GuardQuota.EffectivePayload(p,quota);
                    int debit=0; long tickMomentum=0; var contacts=new List<TargetContact>();
                    int count=rng.Next(1,5);
                    for(int i=0;i<count;i++)
                    {
                        var components=ids.Select(id=>new DamageComponentSpec(id,rng.Next(0,10001)/1024f,
                            DamageTagMask.Blockable|(rng.Next(2)==0 ? DamageTagMask.Guardable : DamageTagMask.None))).ToArray();
                        int momentum=rng.Next(0,10001); var tags=rng.Next(2)==0 ? AttackTagMask.Blockable : AttackTagMask.None;
                        if(tags==AttackTagMask.Blockable) tickMomentum+=momentum;
                        var key=new TargetContactKey(TargetContactType.DirectHit,Hero,new ActionPlanId(10+i),Defender,new ActionPlanId(100));
                        contacts.Add(TargetAggregator.BuildDefendedContact(key,GridDirection.East,momentum,components,tags,e,null));
                        debit=Math.Max(debit,GuardQuota.ContactDebit(e,components,tags,momentum));
                    }
                    var actual=TargetAggregator.Aggregate(new TargetAggregationInput(Defender,passive,100000,contacts));
                    for(int channel=0;channel<2;channel++)
                    {
                        var components=actual.Contacts.SelectMany(c=>c.Components).Where(c=>c.ChannelId==ids[channel]).ToArray();
                        maxDamage[channel]=Math.Max(maxDamage[channel],components.Sum(c=>c.AfterPassiveResistanceQ10));
                        resistedDamage[channel]+=components.Sum(c=>c.AfterPassiveResistanceQ10-c.AfterActionResistanceQ10);
                    }
                    resistedMomentum+=actual.IncomingMomentumUnits-actual.TotalImpactUnits;
                    maxMomentum=Math.Max(maxMomentum,tickMomentum); quota-=debit;
                    Assert.That(quota,Is.InRange(0,1024));
                }
                for(int channel=0;channel<2;channel++) Assert.That(resistedDamage[channel],Is.LessThanOrEqualTo(maxDamage[channel]));
                Assert.That(resistedMomentum,Is.LessThanOrEqualTo(maxMomentum));
            }
        }
        [Test] public void GuardRoundingCannotOverResistRepeatedTwoUnitPayloads()
        {
            int quota=1024; long damage=0,momentum=0;
            for(int tick=0;tick<4;tick++)
            {
                var p=GuardQuota.EffectivePayload(Payload(341,341),quota);
                var components=new[] {new DamageComponentSpec(DamageChannels.PhysicalBlunt,2/1024f,DamageTagMask.Guardable|DamageTagMask.Blockable)};
                var c=TargetAggregator.BuildDefendedContact(Key,GridDirection.East,2,components,AttackTagMask.Blockable,p,null);
                var result=TargetAggregator.Aggregate(new TargetAggregationInput(Defender,new Dictionary<DamageChannelId,int>(),1000,new[] {c}));
                damage+=2-result.TotalDamageQ10; momentum+=2-result.TotalImpactUnits;
                quota-=GuardQuota.ContactDebit(p,components,AttackTagMask.Blockable,2);
            }
            Assert.That(damage,Is.LessThanOrEqualTo(2)); Assert.That(momentum,Is.LessThanOrEqualTo(2)); Assert.That(quota,Is.Zero);
        }
        [Test] public void GuardAndBlockRespectEachComponentsTagEvenWithinSameChannel()
        {
            var components=new[] {new DamageComponentSpec(DamageChannels.PhysicalBlunt,10,DamageTagMask.Guardable|DamageTagMask.Blockable),
                new DamageComponentSpec(DamageChannels.PhysicalBlunt,20,DamageTagMask.None)};
            foreach(bool block in new[] {false,true})
            {
                var c=TargetAggregator.BuildDefendedContact(Key,GridDirection.East,0,components,AttackTagMask.None,
                    block?null:Payload(512,512),block?new BlockPayloadSpec(DefenseTagMask.Blockable):null);
                var result=TargetAggregator.Aggregate(new TargetAggregationInput(Defender,new Dictionary<DamageChannelId,int>(),1000,new[] {c}));
                Assert.That(result.TotalDamageQ10,Is.EqualTo((block?20:25)*1024));
            }
        }
        [Test] public void GuardMomentumResistanceRequiresBlockableAttack()
        {
            var c=TargetAggregator.BuildDefendedContact(default,GridDirection.East,100,Components,AttackTagMask.Dodgeable,Payload(512,512),null);
            Assert.That(c.ActionMomentumResistanceQ10,Is.Zero);
        }
        [Test] public void BuilderRejectsGuardResistanceAtOrAboveFull()
        {
            foreach(int ratio in new[] {1024,1025,int.MaxValue})
            {
                var spec=Definition().Actions.Single(a=>a.ActionSpecId==Guard) with {Payload=Payload(ratio,512)};
                Assert.That(ActionDefinitionValidation.ValidateGuard(spec),Is.EqualTo(ActionDefinitionCodes.GUARD_RESISTANCE_NOT_BELOW_FULL));
                Assert.That(ActionDefinitionValidation.ValidateGuard(spec with {Payload=Payload(512,ratio)}),Is.EqualTo(ActionDefinitionCodes.GUARD_RESISTANCE_NOT_BELOW_FULL));
            }
        }
        [Test] public void BuilderRejectsGuardableComponentWithoutBlockable()
        {
            var a=Definition().Actions.First(x=>x.Type==ActionType.Attack);
            a=a with {Payload=((AttackPayloadSpec)a.Payload) with {DamageComponents=new[] {new DamageComponentSpec(DamageChannels.PhysicalBlunt,1,DamageTagMask.Guardable)}}};
            Assert.That(ActionDefinitionValidation.ValidateAttack(a),Is.EqualTo(ActionDefinitionCodes.GUARDABLE_COMPONENT_NOT_BLOCKABLE));
        }
        [Test] public void ReactionDefaultStartIsSourceImpactTick()
        { using var s=Prepared(out var o); Assert.That(Accept(s,o,ReactionCommandKind.Block,null,out var p),Is.Null); Assert.That(p.StartTick,Is.EqualTo(o.SourceImpactTick)); Assert.That(p.EndTick,Is.EqualTo(p.StartTick+60)); }
        [TestCase(2)] [TestCase(12)] public void ReactionStartBeforeNextTickOrAfterImpactIsRejected(long start)
        { using var s=Prepared(out var o); long id=s.CurrentSnapshot.NextActionPlanId; Assert.That(Accept(s,o,ReactionCommandKind.Block,start,out _),Is.EqualTo(ReactionCodes.REACTION_START_OUT_OF_RANGE)); Assert.That(s.CurrentSnapshot.NextActionPlanId,Is.EqualTo(id)); Assert.That(s.AdrenalineLedgerOf(Defender).ReservationCount,Is.Zero); }
        [Test] public void ReactionIntervalMustCoverSourceImpact()
        { using var s=Prepared(out var o,3); Assert.That(Accept(s,o,ReactionCommandKind.Block,8,out _),Is.EqualTo(ReactionCodes.REACTION_START_OUT_OF_RANGE)); }
        [Test] public void OptionDeadlineIsImpactMinusIngressLead()
        { using var s=Prepared(out var o); Assert.That(o.Options.All(x=>x.ResponseDeadlineTick==o.SourceImpactTick-s.Definition.ReactionRules.CommandIngressLeadTicks),Is.True); }
        [Test] public void BlockTriggersOnFirstEligibleContactEvenIfNotSource()
        {
            using var s=Prepared(out var o); Assert.That(Accept(s,o,ReactionCommandKind.Block,3,out var p),Is.Null);
            Seed(s,Task09A2Fixture.Ally,Light,3,Defender); Advance(s,5);
            Assert.That(p.ReactionTriggeredAtTick,Is.EqualTo(5)); Assert.That(p.TerminationReason,Is.EqualTo(ActionTerminationReason.ReactionTriggered));
            Assert.That(o.State,Is.EqualTo(ReactionOpportunityState.Triggered)); Advance(s,11);
            Assert.That(s.StagedResolution.BlockPlans,Is.Empty); Assert.That(s.StagedResolution.Aggregations.Single().TotalDamageQ10,Is.EqualTo(10*1024));
        }
        [Test] public void BlockResistsAllEligiblePayloadsInTriggerTickThenTerminates()
        {
            using var s=Prepared(out var o); Assert.That(Accept(s,o,ReactionCommandKind.Block,null,out var p),Is.Null);
            Seed(s,Task09A2Fixture.Ally,Light,9,Defender); Advance(s,11);
            Assert.That(s.StagedResolution.BlockPlans.Single().Contacts.Count,Is.EqualTo(2));
            Assert.That(s.StagedResolution.BlockPlans.Single().Contacts.All(c=>c.AfterBlockDamageQ10==0),Is.True);
            Assert.That(p.TerminationReason,Is.EqualTo(ActionTerminationReason.ReactionTriggered));
            Assert.That(s.AdrenalineLedgerOf(Defender).ReservationCount,Is.Zero);
        }
        [Test] public void BlockNotTriggeredUntilEndExpiresWithoutRefund()
        {
            using var s=Prepared(out var o,6); Assert.That(Accept(s,o,ReactionCommandKind.Block,null,out var p),Is.Null);
            Assert.That(s.LogicGrid.CommitAnchor(Defender,new GridPoint(6,4),GridDirection.East),Is.Null);
            int available=s.AdrenalineLedgerOf(Defender).AvailableAdrenaline; Advance(s,p.EndTick);
            Assert.That(p.TerminationReason,Is.EqualTo(ActionTerminationReason.ReactionExpired)); Assert.That(p.ReactionTriggeredAtTick,Is.EqualTo(-1));
            Assert.That(s.AdrenalineLedgerOf(Defender).AvailableAdrenaline,Is.EqualTo(available)); Assert.That(s.AdrenalineLedgerOf(Defender).ReservationCount,Is.Zero);
        }
        [Test] public void DodgeTriggersOnFirstDodgeableOldCellContact()
        {
            using var s=Prepared(out var o); Assert.That(Accept(s,o,ReactionCommandKind.Dodge,3,out var p),Is.Null);
            Seed(s,Task09A2Fixture.Ally,Light,3,Defender); Advance(s,5);
            Assert.That(p.ReactionTriggeredAtTick,Is.EqualTo(5)); Assert.That(s.LastDodgeCommitReport.Results.Single().Moved,Is.True);
            Assert.That(p.TerminationReason,Is.EqualTo(ActionTerminationReason.ReactionTriggered));
        }
        [Test] public void DodgeDestinationRecheckFailureConsumesAndTerminates()
        {
            using var s=Prepared(out var o); Assert.That(Accept(s,o,ReactionCommandKind.Dodge,null,out var p),Is.Null);
            int available=s.AdrenalineLedgerOf(Defender).AvailableAdrenaline;
            Assert.That(s.LogicGrid.CommitAnchor(Task09A2Fixture.Neutral,Destination,GridDirection.East),Is.Null); var result=Advance(s,11);
            Assert.That(s.LastDodgeCommitReport.Results.Single().Committed,Is.False); Assert.That(p.TerminationReason,Is.EqualTo(ActionTerminationReason.ReactionTriggered));
            Assert.That(s.AdrenalineLedgerOf(Defender).ReservationCount,Is.Zero);
            Assert.That(s.AdrenalineLedgerOf(Defender).AvailableAdrenaline,Is.GreaterThanOrEqualTo(available));
            Assert.That(result.Events.Events.OfType<ReactionTriggeredEvent>().Count(),Is.EqualTo(1));
        }
        [Test] public void OrdinaryPlanMayStartInsideReactionSoftSegment()
        {
            using var s=Prepared(out var o); var guard=Seed(s,Defender,Guard,12);
            Assert.That(Accept(s,o,ReactionCommandKind.Block,null,out var p),Is.Null);
            Assert.That(s.ScheduleAuthority.FindLane(Defender).ValidateNonOverlapping(),Is.Null);
            Advance(s,12); Assert.That(guard.IsRunning,Is.True); Assert.That(p.TerminationReason,Is.EqualTo(ActionTerminationReason.ReactionTriggered));
        }
        [Test] public void OrdinaryPlanOverlappingReactionHardSegmentIsRejected()
        {
            using var s=Prepared(out var o); Seed(s,Defender,Guard,11);
            Assert.That(Accept(s,o,ReactionCommandKind.Block,null,out _),Is.EqualTo(ReactionCodes.REACTION_LANE_INTERVAL_OCCUPIED));
        }
        [Test] public void UntriggeredReactionYieldsLaneWhenOrdinaryPlanStarts()
        {
            using var s=Prepared(out var o); var guard=Seed(s,Defender,Guard,12);
            Assert.That(Accept(s,o,ReactionCommandKind.Block,null,out var p),Is.Null);
            Assert.That(s.LogicGrid.CommitAnchor(Defender,new GridPoint(6,4),GridDirection.East),Is.Null); Advance(s,12);
            Assert.That(p.TerminationReason,Is.EqualTo(ActionTerminationReason.ReactionYieldedLane)); Assert.That(guard.IsRunning,Is.True);
            Assert.That(s.FindUnitStateMachine(Defender).CurrentState,Is.EqualTo(UnitState.Guarding));
        }
        [Test] public void TriggeredReactionFreesLaneImmediately()
        { using var s=Prepared(out var o); Assert.That(Accept(s,o,ReactionCommandKind.Block,3,out var p),Is.Null); Seed(s,Task09A2Fixture.Ally,Light,3,Defender); Advance(s,5); Assert.That(s.ScheduleAuthority.FindLane(Defender).Contains(p.ActionPlanId),Is.False); Assert.That(s.FindUnitStateMachine(Defender).CurrentState,Is.EqualTo(UnitState.Idle)); }
        [Test] public void NoCounterWindowEventIsEverEmitted()
        { using var r=Task08DefenseCoverageTests.ReactionRig(); Assert.That(r.Result.Events.Events.Any(e=>e.GetType().Name.Contains("Counter"+"Window")),Is.False); Assert.That(typeof(LogicEvent).Assembly.GetTypes().Any(t=>t.Name.Contains("Counter"+"Window")),Is.False); }
        [Test] public void SuccessfulDodgeRewardStillAccruesOnce()
        { using var r=Task08DefenseCoverageTests.ReactionRig(); Assert.That(r.Result.Events.Events.OfType<DodgeResolvedEvent>().Count(e=>e.RewardedSuccess),Is.EqualTo(1)); }
        [Test] public void MultiAttackMultiDefenseInputPermutationInvariance()
        {
            foreach(ActionType type in new[] {ActionType.Guard,ActionType.Block,ActionType.Dodge})
            {
                var a=ProjectHero.Logic.Interactions.StagedConflictResolver.Resolve(Task08RemainingCoverageTests.DefenseContext(type,attacks:3));
                var b=ProjectHero.Logic.Interactions.StagedConflictResolver.Resolve(Task08RemainingCoverageTests.DefenseContext(type,attacks:3,reverse:true));
                Assert.That(a.GuardQuotaConsumptions,Is.EqualTo(b.GuardQuotaConsumptions)); Assert.That(a.BlockPlans.SelectMany(p=>p.Contacts),Is.EqualTo(b.BlockPlans.SelectMany(p=>p.Contacts)));
                Assert.That(a.DodgeContacts,Is.EqualTo(b.DodgeContacts)); Assert.That(a.Aggregations.Select(x=>x.TotalDamageQ10),Is.EqualTo(b.Aggregations.Select(x=>x.TotalDamageQ10)));
            }
        }
        static AiDecisionContext AiContext(BattleSimulation s) => new AiDecisionContext(
            s.DecisionSnapshotFor(Task09A2Fixture.AiId),Task09A2Fixture.AiId,new[] {Defender},Array.Empty<ActionPlanSnapshot>(),null,
            s.AiReactionOpportunities.PublishedOpportunitiesFor(Defender,s.Tick),null,null);
        [Test] public void AiReactionCommandCarriesExplicitStartTick()
        {
            using var s=Prepared(out var o); var context=AiContext(s);
            var options=AiReactionCandidateBuilder.BuildReactionCandidatesOrdered(context,s.Tick);
            Assert.That(options.Count,Is.GreaterThan(0)); Assert.That(options.All(x=>x.StartTick==o.SourceImpactTick),Is.True);
            var decision=AiDecisionFunction.Decide(context,31);
            Assert.That(((ReactionCommandPayload)decision.Selected.Payload).StartTick,Is.EqualTo(o.SourceImpactTick));
        }
        [Test] public void AiDoesNotProposeReactionPreemptedByKnownEarlierContact()
        {
            using var s=Prepared(out var o); var light=Seed(s,Task09A2Fixture.Ally,Light,3,Defender); Advance(s,3);
            var context=AiContext(s); Assert.That(context.Snapshot.VisiblePlans.Any(p=>p.ActionPlanId==light.ActionPlanId.Value),Is.True,
                "light="+light+" reason="+light.TerminationReason+" tick="+s.Tick+" visible="+string.Join(",",context.Snapshot.VisiblePlans.Select(p=>p.ActionPlanId)));
            var options=AiReactionCandidateBuilder.BuildReactionCandidatesOrdered(context,s.Tick).Where(x=>x.ReactionOpportunityId==o.Id).ToArray();
            Assert.That(options.Length,Is.GreaterThan(0)); Assert.That(options.All(x=>x.StartTick>light.ImpactTick && x.StartTick==o.SourceImpactTick),Is.True);
            Seed(s,Defender,Guard,11);
            Assert.That(AiReactionCandidateBuilder.BuildReactionCandidatesOrdered(AiContext(s),s.Tick).Where(x=>x.ReactionOpportunityId==o.Id),Is.Empty);
        }
        [TestCase(false)] [TestCase(true)] public void SourceCancelledBeforeImpactRefundsSameCycleOnly(bool staleCycle)
        {
            using var s=Prepared(out var o); var ledger=s.AdrenalineLedgerOf(Defender); int before=ledger.AvailableAdrenaline;
            Assert.That(Accept(s,o,ReactionCommandKind.Dodge,3,out var p),Is.Null);
            if(staleCycle) ledger.BeginOwnWindow();
            int available=ledger.AvailableAdrenaline;
            var source=s.ScheduleAuthority.Registry.Find(o.SourceAttackPlanId);
            s.TerminalCoordinator.EnterTerminal(source,ActionTerminationReason.CancelledByCommand,3); Advance(s,3);
            Assert.That(p.TerminationReason,Is.EqualTo(ActionTerminationReason.SourceThreatCancelled));
            Assert.That(ledger.AvailableAdrenaline,Is.EqualTo(staleCycle ? available : before));
            Assert.That(ledger.ReservationCount,Is.Zero); Assert.That(s.DodgeRelocation.AllReservationsOrdered(),Is.Empty);
        }
        [Test] public void MissedSourceImpactAndLaterSourceCompletionDoNotCancelReaction()
        {
            using var s=Prepared(out var o); Assert.That(Accept(s,o,ReactionCommandKind.Block,null,out var p),Is.Null);
            Assert.That(s.LogicGrid.CommitAnchor(Defender,new GridPoint(6,4),GridDirection.East),Is.Null); Advance(s,41);
            Assert.That(s.ScheduleAuthority.Registry.Find(o.SourceAttackPlanId).IsCompleted,Is.True);
            Assert.That(p.IsRunning,Is.True); Assert.That(p.ReactionTriggeredAtTick,Is.EqualTo(-1));
            Assert.That(s.LogicGrid.CommitAnchor(Defender,new GridPoint(6,0),GridDirection.East),Is.Null);
            Seed(s,Task09A2Fixture.Ally,Light,42,Defender); Advance(s,44);
            Assert.That(p.ReactionTriggeredAtTick,Is.EqualTo(44)); Assert.That(p.TerminationReason,Is.EqualTo(ActionTerminationReason.ReactionTriggered));
        }
        [Test] public void ReactionIntervalPreviewIsReadOnlyAndSharesCommitBoundaries()
        {
            using var s=Prepared(out var o); Seed(s,Defender,Guard,12);
            int count=s.ScheduleAuthority.Registry.ActiveCount; long next=s.CurrentSnapshot.NextActionPlanId;
            var spec=new ActionSpecId(Task09A2Fixture.BlockSpecId);
            var preview=s.ReactionOpportunities.PreviewStart(o.Id,spec,2);
            Assert.That(preview.Succeeded,Is.True); Assert.That(preview.StartTick,Is.EqualTo(11)); Assert.That(preview.EndTick,Is.EqualTo(71));
            Assert.That(preview.HardEndTick,Is.EqualTo(12)); Assert.That(preview.KnownYieldTick,Is.EqualTo(12));
            Assert.That(s.ReactionOpportunities.PreviewStart(o.Id,spec,2,2).RejectionCode,Is.EqualTo(ReactionCodes.REACTION_START_OUT_OF_RANGE));
            Assert.That(s.ScheduleAuthority.Registry.ActiveCount,Is.EqualTo(count)); Assert.That(s.CurrentSnapshot.NextActionPlanId,Is.EqualTo(next));
            Assert.That(Accept(s,o,ReactionCommandKind.Block,null,out var p),Is.Null);
            Assert.That(p.StartTick,Is.EqualTo(preview.StartTick)); Assert.That(p.EndTick,Is.EqualTo(preview.EndTick));
        }
        [Test] public void SoftOverlappingBlocksConsumeEachReservationAndAggregateBypassDamageOnce()
        {
            var d=Definition(); d=d with {Actions=d.Actions.Select(a=>a.Type==ActionType.Attack ? a with {Payload=((AttackPayloadSpec)a.Payload) with {
                DamageComponents=((AttackPayloadSpec)a.Payload).DamageComponents.Concat(new[] {new DamageComponentSpec(DamageChannels.True,3,
                    DamageTagMask.BypassActionResistance|DamageTagMask.BypassPassiveResistance)}).ToArray()}} : a).ToArray()};
            using var s=Task09A2Fixture.NewSim(d); Task09A2Fixture.ArrangeStartedAttack(s,Defender);
            var first=s.ReactionOpportunities.ActiveOpportunities.Single(); var ledger=s.AdrenalineLedgerOf(Defender);
            ledger.ApplyAccrualFacts(new ProjectHero.Logic.Resources.AdrenalineAccrualFacts(Defender,0,100*1024,0,0,0));
            Assert.That(Accept(s,first,ReactionCommandKind.Block,null,out var one),Is.Null);
            Assert.That(s.LogicGrid.CommitAnchor(Defender,new GridPoint(6,4),GridDirection.East),Is.Null); Advance(s,15);
            Assert.That(one.IsRunning,Is.True); Assert.That(s.LogicGrid.CommitAnchor(Defender,new GridPoint(6,0),GridDirection.East),Is.Null);
            var attack=Seed(s,Task09A2Fixture.Ally,Light,20,Defender); Advance(s,20);
            var second=s.ReactionOpportunities.ActiveOpportunities.Single(o=>o.SourceAttackPlanId==attack.ActionPlanId && o.DefenderUnitId==Defender);
            Assert.That(Accept(s,second,ReactionCommandKind.Block,null,out var two),Is.Null); Advance(s,22);
            Assert.That(one.ReactionTriggeredAtTick,Is.EqualTo(22)); Assert.That(two.ReactionTriggeredAtTick,Is.EqualTo(22));
            Assert.That(ledger.ReservationCount,Is.Zero); Assert.That(s.StagedResolution.Aggregations.Single().TotalDamageQ10,Is.EqualTo(3*1024));
            Assert.That(s.StagedResolution.BlockPlans.Count,Is.EqualTo(2));
        }
        [TestCase(ActionType.Guard)] [TestCase(ActionType.Block)]
        public void MultiUnitDefensePermutationsPreserveEventsQuotaTerminationAndHashes(ActionType defense)
        {
            BattleSimulation World(bool reverse)
            {
                var d=Definition(); var pattern=new AttackPatternSpec(new AttackPatternId("attack.pattern.t12b.permutation"),
                    Enumerable.Range(0,12).Select(f=>new DirectionalTriangleSet((GridDirection)f,new[] {
                        new TrianglePoint(9,0,1),new TrianglePoint(17,0,1),new TrianglePoint(-13,0,1),new TrianglePoint(-5,0,1)})).ToArray());
                var controller=new ControllerId("controller.t12b.neutral");
                d=d with {Actions=d.Actions.Select(a=>a.Type==ActionType.Attack ? a with {Payload=((AttackPayloadSpec)a.Payload) with {
                    Pattern=pattern,TargetPolicy=TargetPolicy.AllTargetsInArea,AllowedTargetRelations=TargetRelationMask.Hostile|TargetRelationMask.Neutral}} : a).ToArray(),
                    Encounters=d.Encounters.Select(e=>e with {Controllers=e.Controllers.Concat(new[] {new ControllerBinding(controller,
                        CommandSourceKind.Player,new[] {new EncounterSlotId("c_neut")})}).ToArray()}).ToArray()};
                var s=Task09A2Fixture.NewSim(d); Task09A2Fixture.ArrangeStartedAttack(s,Defender);
                var requests=new List<OrdinaryPlanRequest> {new OrdinaryPlanRequest(Task09A2Fixture.Ally,Light,GridDirection.East,Defender,null,s.CurrentTurnWindow.WindowId,9)};
                if(defense==ActionType.Guard)
                {
                    requests.Add(new OrdinaryPlanRequest(Defender,Guard,GridDirection.East,null,null,s.CurrentTurnWindow.WindowId,2));
                    requests.Add(new OrdinaryPlanRequest(Task09A2Fixture.Neutral,Guard,GridDirection.East,null,null,s.CurrentTurnWindow.WindowId,2));
                }
                var candidates=requests.Select(r=>s.PlanFactory.TryCreateOrdinary(r,s.Tick).Plan).ToArray();
                foreach(var p in reverse ? candidates.Reverse() : candidates)
                    typeof(ActionScheduleAuthority).GetMethod("RegisterPlan",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(s.ScheduleAuthority,new object[] {p});
                if(defense==ActionType.Block)
                {
                    Task09A2Fixture.SeedAdrenaline(s,Task09A2Fixture.Neutral,2);
                    var options=s.ReactionOpportunities.ActiveOpportunities.Where(o=>o.DefenderUnitId==Defender || o.DefenderUnitId==Task09A2Fixture.Neutral).ToArray();
                    foreach(var o in reverse ? options.Reverse() : options)
                        Task09A2Fixture.Submit(s,o.DefenderUnitId==Defender ? Task09A2Fixture.AiId : controller,new CommandRequest(2,new ReactionCommandScope(o.Id),
                            new ReactionCommandPayload(ReactionCommandKind.Block,new ActionSpecId(Task09A2Fixture.BlockSpecId),StartTick:o.SourceImpactTick)));
                }
                return s;
            }
            using var first=World(false); using var second=World(true);
            for(int tick=2;tick<=12;tick++)
            {
                var a=Task09A2Fixture.Step(first,tick); var b=Task09A2Fixture.Step(second,tick);
                Assert.That(a.Snapshot.ComputeHash(),Is.EqualTo(b.Snapshot.ComputeHash()),"tick="+tick);
                Assert.That(a.Events.Events.Select(ReplayEventComparison.Canonical),Is.EqualTo(b.Events.Events.Select(ReplayEventComparison.Canonical)),"tick="+tick);
            }
            if(defense==ActionType.Guard) Assert.That(first.ScheduleAuthority.Registry.ActivePlans.Where(p=>p.ActionType==ActionType.Guard).Select(p=>p.DefenseQuotaRemainingQ10),Is.EqualTo(new[] {512,512}));
            else Assert.That(first.CurrentSnapshot.Resources.AdrenalineLedgers.Where(l=>l.UnitId==Defender.Value || l.UnitId==Task09A2Fixture.Neutral.Value).All(l=>l.ReservedTotal==0),Is.True);
        }
        [TestCase(11,false)] [TestCase(12,true)]
        public void OrdinaryPreviewAndCommitAfterReactionUseSameHardSoftRule(long start,bool accepted)
        {
            var d=Definition(); var pattern=new AttackPatternSpec(new AttackPatternId("attack.pattern.t12b.reverse"),Enumerable.Range(0,12).Select(f=>
                new DirectionalTriangleSet((GridDirection)f,new[] {new TrianglePoint(9,0,1),new TrianglePoint(-3,0,1)})).ToArray());
            d=d with {Actions=d.Actions.Select(a=>a.Type==ActionType.Attack ? a with {Timing=new AttackTimingSpec(20,1),Payload=((AttackPayloadSpec)a.Payload) with {Pattern=pattern}} : a).ToArray(),
                Units=d.Units.Select(u=>u with {ActionSpeed=u.UnitDefinitionId==d.FindEncounter(Task09A2Fixture.EncounterId).Slots[1].DefinitionId ? 40 : 20}).ToArray(),
                Encounters=d.Encounters.Select(e=>e with {TurnSubmission=new TurnSubmissionDefinition(400,new EncounterSlotId("a_hero")),
                    Controllers=e.Controllers.Select(c=>c with {SourceKind=CommandSourceKind.Player}).ToArray()}).ToArray()};
            using var s=ProductionBattleComposition.Create(d,Task09A2Fixture.EncounterId,Task09A2Fixture.InputsWithMeta(d.ConcurrentAction.MetaResourceCost));
            Task09A2Fixture.ArrangeStartedAttack(s,Hero,new ActionSpecId(Task09A2Fixture.AttackSpecId),Defender,true);
            var o=s.ReactionOpportunities.ActiveOpportunities.Single(x=>x.DefenderUnitId==Hero);
            Task09A2Fixture.Submit(s,Task09A2Fixture.PlayerId,new CommandRequest(2,new WindowCommandScope(s.CurrentTurnWindow.WindowId),
                new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction))); Task09A2Fixture.Step(s,2);
            Assert.That(s.LastCommandSet.RejectedCommands,Is.Empty,"Concurrent admission prerequisite");
            Assert.That(s.CurrentSnapshot.ConcurrentAction.HasActiveAuthorization,Is.True);
            Task09A2Fixture.Submit(s,Task09A2Fixture.PlayerId,new CommandRequest(3,new ReactionCommandScope(o.Id),
                new ReactionCommandPayload(ReactionCommandKind.Block,new ActionSpecId(Task09A2Fixture.BlockSpecId)))); Task09A2Fixture.Step(s,3);
            Assert.That(s.LastCommandSet.RejectedCommands,Is.Empty);
            var ops=new[] {new AddOrdinaryPlanOperation(1,Hero,Guard,start)};
            long revision=s.ScheduleAuthority.ScheduleRevision; var window=s.CurrentTurnWindow.WindowId;
            var preview=s.ScheduleEditor.Apply(ops,4,revision,revision,preview:true,expectedWindowId:window,issuer:Task09A2Fixture.PlayerId);
            Assert.That(s.ScheduleAuthority.ScheduleRevision,Is.EqualTo(revision));
            var commit=s.ScheduleEditor.Apply(ops,4,revision,revision,expectedWindowId:window,issuer:Task09A2Fixture.PlayerId);
            Assert.That(preview.Succeeded,Is.EqualTo(accepted),"preview="+preview.RejectionCode); Assert.That(commit.Succeeded,Is.EqualTo(accepted),"commit="+commit.RejectionCode);
            Assert.That(preview.RejectionCode,Is.EqualTo(commit.RejectionCode));
            if(accepted) Assert.That(s.ScheduleAuthority.Registry.Find(commit.AddedPlanIds.Single()).StartTick,Is.EqualTo(start));
            else Assert.That(commit.RejectionCode,Is.EqualTo(ScheduleCodes.SCHEDULE_LANE_OVERLAP));
        }
        [Test] public void DefenseDurationReplayRejectsV5InsteadOfReinterpretingItsPayload()
        {
            using var bytes=new System.IO.MemoryStream(); using(var writer=new System.IO.BinaryWriter(bytes,System.Text.Encoding.UTF8,true)) writer.Write("ProjectHero.DeveloperReplay.v5");
            bytes.Position=0; var error=Assert.Throws<LogicDefinitionException>(()=>ReplayFile.Load(bytes,Definition()));
            Assert.That(error.ErrorCode,Is.EqualTo(ReplayCodes.REPLAY_FORMAT_VERSION_MISMATCH));
        }
    }
}
