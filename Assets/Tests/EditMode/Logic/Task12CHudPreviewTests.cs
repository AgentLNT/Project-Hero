using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Tests
{
    public sealed class Task12CHudPreviewTests
    {
        static UnitId Hero => Task09A2Fixture.Hero;
        static UnitId Enemy => Task09A2Fixture.Monster;
        static ControllerId Player => Task09A2Fixture.PlayerId;
        static ControllerId Ai => Task09A2Fixture.AiId;
        static readonly ActionSpecId Block = new ActionSpecId(Task09A2Fixture.BlockSpecId);
        static readonly ActionSpecId Light = new ActionSpecId("action.t12c.light");
        static readonly ActionSpecId Heavy = new ActionSpecId(Task09A2Fixture.AttackSpecId);
        static BattleDefinition Definition()
        {
            var d = Task08DefenseCoverageTests.Definition(force: .001f);
            var original = d.Actions.First(a => a.ActionSpecId == Heavy);
            var light = original with { ActionSpecId = Light, Timing = new AttackTimingSpec(2,1),
                Payload = ((AttackPayloadSpec)original.Payload) with {AllowedTargetRelations = TargetRelationMask.Hostile | TargetRelationMask.Neutral,
                    Pattern = new AttackPatternSpec(new AttackPatternId("attack.pattern.t12c.light"), Enumerable.Range(0,12)
                        .Select(f=>new DirectionalTriangleSet((GridDirection)f,Enumerable.Range(-31,63).Where(x=>(x & 1) != 0).Select(x=>new TrianglePoint(x,0,1)).ToArray())).ToArray())} };
            return d with {
                Actions = d.Actions.Select(a => a.Type == ActionType.Block ? a with { Timing = new BlockReactionTimingSpec(60) }
                    : a.Type == ActionType.Dodge ? a with { Timing = new DodgeReactionTimingSpec(30) }
                    : a.Type == ActionType.Guard ? a with { Timing = new GuardTimingSpec(60) } : a).Concat(new[] {light}).ToArray(),
                ActionSets = d.ActionSets.Select(a => a with {ActionSpecIds = a.ActionSpecIds.Concat(new[] {Light}).ToArray()}).ToArray() };
        }
        static BattleSimulation Prepared(bool funds = true)
        {
            var s = Task09A2Fixture.NewSim(Definition());
            Task09A2Fixture.ArrangeStartedAttack(s, Enemy, funds);
            // Commit an additional boundary so public resource facts are committed as well.
            Task09A2Fixture.StepNext(s); return s;
        }
        static ReactionOpportunityRuntime Opportunity(BattleSimulation s) => Task09A2Fixture.OpenOpportunityFor(s, Enemy);
        static ActionPlan Seed(BattleSimulation s, UnitId owner, ActionSpecId spec, long start, UnitId? target = null)
        {
            var created = s.PlanFactory.TryCreateOrdinary(new OrdinaryPlanRequest(owner,spec,GridDirection.East,target,null,
                s.CurrentTurnWindow.WindowId,start),s.Tick);
            Assert.That(created.Succeeded,Is.True,created.RejectionCode);
            typeof(ActionScheduleAuthority).GetMethod("RegisterPlan",BindingFlags.Instance|BindingFlags.NonPublic)
                .Invoke(s.ScheduleAuthority,new object[] {created.Plan}); return created.Plan;
        }
        static StepResult Advance(BattleSimulation s,long tick) {StepResult r=null; while(s.Tick<tick) r=Task09A2Fixture.StepNext(s); return r;}
        static string Facts(DecisionSnapshot d) => string.Join(";", d.AdrenalineLedgers.Select(l =>
            l.UnitId + ":" + l.AvailableAdrenaline + ":" + l.CycleId + ":" + l.ReservedTotal + ":"
            + string.Join(",", l.Reservations.Select(r => r.ActionPlanId + "/" + r.ReservedAmount + "/" + r.ReservationCycleId))));

        [Test] public void DecisionSnapshotExposesAllUnitsAdrenalineToEveryController()
        {
            using var s=Prepared(); var o=Opportunity(s);
            Assert.That(s.ReactionOpportunities.TryAcceptById(o.Id,Block,s.Tick+1,null,out var p),Is.Null);
            Task09A2Fixture.StepNext(s);
            foreach(var c in new[] {Player,Ai}) {
                var d=s.DecisionSnapshotFor(c); Assert.That(d.AdrenalineLedgers.Select(l=>l.UnitId),Is.EqualTo(new long[]{1,2,3,4}));
                var ledger=d.AdrenalineLedgers.Single(l=>l.UnitId==Enemy.Value);
                Assert.That(ledger.Reservations.Single().ActionPlanId,Is.EqualTo(p.ActionPlanId.Value));
                Assert.That(ledger.ReservedTotal,Is.EqualTo(s.Definition.FindAction(Block).AdrenalineCost));
            }
        }
        [Test] public void DecisionSnapshotStillHidesOtherControllersEditablePlans()
        {
            using var s=Prepared(); var hidden=Seed(s,Enemy,Heavy,20,Hero); Task09A2Fixture.StepNext(s);
            Assert.That(s.DecisionSnapshotFor(Player).VisiblePlans.Any(p=>p.ActionPlanId==hidden.ActionPlanId.Value),Is.False);
            Assert.That(s.DecisionSnapshotFor(Player).OwnPlansOf(Ai),Is.Empty);
            Assert.That(s.DecisionSnapshotFor(Ai).OwnPlans.Any(p=>p.ActionPlanId==hidden.ActionPlanId.Value),Is.True);
        }
        [Test] public void PlayerAndAiSeeIdenticalAdrenalineFacts()
        {
            using var s=Prepared(); Assert.That(Facts(s.DecisionSnapshotFor(Player)),Is.EqualTo(Facts(s.DecisionSnapshotFor(Ai))));
        }
        [Test] public void WindowSequenceProjectionMatchesActualOpenings()
        {
            var d=Definition(); d=d with {Encounters=d.Encounters.Select(e=>e with {
                TurnSubmission = new TurnSubmissionDefinition(30,new EncounterSlotId("a_hero")),
                Controllers=e.Controllers.Select(c=>c with {SourceKind=CommandSourceKind.Player}).ToArray() }).ToArray()};
            using var s=ProductionBattleComposition.Create(d,Task09A2Fixture.EncounterId,Task09A2Fixture.Inputs);
            var recorder=new ReplayRecorder(s);
            StepResult RecordedStep() {recorder.CaptureBeforeStep();var result=Task09A2Fixture.StepNext(s);recorder.RecordCommittedStep(result);return result;}
            RecordedStep(); var windows=s.HudPreviewFor(Player).WindowSequence.Take(3).ToArray();
            Assert.That(windows.Length,Is.EqualTo(3)); Assert.That(windows[0].IsEstimated,Is.False);
            foreach(var expected in windows.Skip(1)) {
                while(s.Tick<expected.OpenedAtTick) RecordedStep(); Assert.That(s.CurrentTurnWindow.OwnerUnitId.Value,Is.EqualTo(expected.OwnerUnitId));
                Assert.That(s.CurrentTurnWindow.OpenedAtTick,Is.EqualTo(expected.OpenedAtTick)); Assert.That(s.CurrentTurnWindow.EndTick,Is.EqualTo(expected.EndTick));
            }
            var replay=recorder.BuildReplay();
            using(var file=System.IO.File.Create("优化任务/执行记录/12C-window-preview.heroReplay")) ReplayFile.Save(file,replay);
            using var player=new ReplayPlayer(d,h=>ProductionBattleComposition.Create(d,h.EncounterId,h.RuntimeInputs));player.Load(replay);player.Play();
            while(!player.IsComplete && player.Deviation==null) {
                player.AdvanceOneTick();
                var expected=windows.FirstOrDefault(w=>w.OpenedAtTick==player.CurrentSnapshot.Tick);
                if(expected!=null) Assert.That(player.CurrentSnapshot.WindowManager.Windows.Single(w=>w.WindowId==player.CurrentSnapshot.WindowManager.CurrentWindowId).OwnerUnitId,Is.EqualTo(expected.OwnerUnitId));
            }
            Assert.That(player.Deviation,Is.Null); Assert.That(player.IsComplete,Is.True);
        }
        [Test] public void KnownHorizonEqualsNextForeignWindowOpen()
        {
            using var s=Task09A2Fixture.NewSim(Definition()); Task09A2Fixture.ArrangeStartedAttack(s,Enemy,false);
            s.WindowManager.ScheduleWindow(400,Enemy,400); Task09A2Fixture.StepNext(s);
            var preview=s.HudPreviewFor(Player); Assert.That(preview.KnownHorizonTick,Is.EqualTo(400));
            Advance(s,400); Assert.That(s.CurrentTurnWindow.OwnerUnitId,Is.EqualTo(Enemy));
        }
        [Test] public void ReactionFeasibilityReportsEachInfeasibleReasonStably()
        {
            using(var s=Prepared(false)) {var o=Opportunity(s); var p=s.HudPreviewFor(Ai).PreviewReaction(o.Id,Block);
                Assert.That(p.RejectionCode,Is.EqualTo("ADRENALINE_INSUFFICIENT")); Assert.That(p.ConflictingPlans,Is.Empty);}
            using(var s=Prepared()) {var o=Opportunity(s); var hidden=Seed(s,Enemy,Heavy,4,Hero); Task09A2Fixture.StepNext(s);
                var p=s.HudPreviewFor(Ai).PreviewReaction(o.Id,Block);
                Assert.That(p.RejectionCode,Is.EqualTo(ReactionCodes.REACTION_LANE_INTERVAL_OCCUPIED));
                Assert.That(p.ConflictingPlans.Single().ActionPlanId,Is.EqualTo(hidden.ActionPlanId.Value));
                Assert.That(p.ConflictingPlans.Single().State,Is.EqualTo((int)ActionPlanState.Editable));
                Task09A2Fixture.StepNext(s); p=s.HudPreviewFor(Ai).PreviewReaction(o.Id,Block);
                Assert.That(p.ConflictingPlans.Single().State,Is.EqualTo((int)ActionPlanState.Running));}
            using(var s=Prepared()) {var o=Opportunity(s); Advance(s,o.Options.First(x=>x.ReactionType==ActionType.Block).ResponseDeadlineTick);
                Assert.That(s.HudPreviewFor(Ai).PreviewReaction(o.Id,Block).RejectionCode,Is.EqualTo(ReactionCodes.OPTION_DEADLINE_ELAPSED));}
        }
        [Test] public void AutoPauseFeasibilityIsFalseWhenAdrenalineInsufficient()
        {using var s=Prepared(false); var p=s.HudPreviewFor(Ai); Assert.That(p.ReactionOptions,Is.Not.Empty); Assert.That(p.HasFeasibleReaction,Is.False);}
        [Test] public void ExpectedTriggerContactMatchesActualWithinHorizon()
        {
            using var s=Prepared(); var o=Opportunity(s); var preview=s.HudPreviewFor(Ai).PreviewDefense(o.Id,Block);
            Assert.That(preview.RejectionCode,Is.Null); Assert.That(preview.TriggerTick,Is.EqualTo(11));
            Assert.That(s.ReactionOpportunities.TryAcceptById(o.Id,Block,s.Tick+1,null,out var plan),Is.Null);
            Advance(s,11); Assert.That(plan.ReactionTriggeredAtTick,Is.EqualTo(preview.TriggerTick));
            Assert.That(preview.SourcePlanIds,Is.EqualTo(new[] {o.SourceAttackPlanId.Value}));
        }
        [Test] public void ExpectedTriggerContactFlagsEarlierPreemptingContact()
        {
            using var s=Prepared(); var o=Opportunity(s); var early=Seed(s,Task09A2Fixture.Neutral,Light,4,Enemy);
            Advance(s,4); var preview=s.HudPreviewFor(Ai).PreviewDefense(o.Id,Block,6);
            Assert.That(preview.RejectionCode,Is.Null); Assert.That(preview.TriggerTick,Is.EqualTo(6));
            Assert.That(preview.HasEarlierPreemptingContact,Is.True); Assert.That(preview.SourcePlanIds,Does.Contain(early.ActionPlanId.Value));
            Assert.That(s.ReactionOpportunities.TryAcceptById(o.Id,Block,s.Tick+1,null,out var plan,6),Is.Null);
            Advance(s,11); Assert.That(plan.ReactionTriggeredAtTick,Is.EqualTo(6));
        }
        [Test] public void GuardQuotaPreviewMatchesActualDeductions()
        {
            using var s=Task09A2Fixture.NewSim(Definition()); s.WindowManager.ScheduleWindow(0,Hero,400); Task09A2Fixture.Step(s,0);
            var first=Seed(s,Enemy,Light,2,Hero); var second=Seed(s,Task09A2Fixture.Neutral,Light,5,Hero);
            // Guard is already committed and running; both hostile attacks must be revealed to this controller.
            var guard=Seed(s,Hero,Task08DefenseCoverageTests.GuardId,1); Advance(s,5);
            var preview=s.HudPreviewFor(Player).PreviewDefense(guard.ActionPlanId);
            Assert.That(preview.GuardContacts.Select(g=>g.Tick),Is.EqualTo(new long[] {7}));
            Assert.That(preview.GuardContacts.Single().DebitQ10,Is.EqualTo(512)); Assert.That(preview.GuardContacts.Single().RemainingQ10,Is.Zero);
            var actual=Advance(s,7).Events.EventsInSequenceOrder.OfType<GuardQuotaConsumedEvent>().Single();
            Assert.That(actual.DebitQ10,Is.EqualTo(preview.GuardContacts.Single().DebitQ10)); Assert.That(actual.RemainingQ10,Is.Zero);
        }
        [Test] public void YieldPointPreviewMatchesActualYield()
        {
            using var s=Prepared(); var o=Opportunity(s);
            Assert.That(s.ReactionOpportunities.TryAcceptById(o.Id,Block,s.Tick+1,null,out var reaction),Is.Null);
            var ordinary=Seed(s,Enemy,Heavy,12,Hero);
            // Public displacement causes the source to miss; reaction survives until the ordinary start gate.
            Assert.That(s.LogicGrid.CommitAnchor(Enemy,new GridPoint(8,0),GridDirection.East),Is.Null); Task09A2Fixture.StepNext(s);
            var preview=s.HudPreviewFor(Ai).PreviewYield(ordinary.ActionPlanId);
            Assert.That(preview.Single().YieldTick,Is.EqualTo(12)); Advance(s,12);
            Assert.That(reaction.TerminationReason,Is.EqualTo(ActionTerminationReason.ReactionYieldedLane)); Assert.That(reaction.TerminalTick,Is.EqualTo(12));
        }
        [Test] public void OpponentReactionCapabilityUsesOnlyPublicFacts()
        {
            using var s=Prepared(); var baseline=s.HudPreviewFor(Player).OpponentCapabilities.Single(); Assert.That(baseline.CanBlock,Is.True);
            var hidden=Seed(s,Enemy,Heavy,4,Hero); Task09A2Fixture.StepNext(s);
            var visible=s.HudPreviewFor(Player).OpponentCapabilities.Single(); Assert.That(visible.CanBlock,Is.True);
            Assert.That(visible.Options.All(o=>o.ConflictingPlans.All(p=>p.ActionPlanId!=hidden.ActionPlanId.Value)),Is.True);
            Assert.That(s.HudPreviewFor(Ai).PreviewReaction(Opportunity(s).Id,Block).IsFeasible,Is.False);
        }
        [Test] public void PreviewsDoNotMutateStateIdsOrRevision()
        {
            using var s=Prepared(); var o=Opportunity(s); string before=s.CurrentSnapshot.ComputeHashHex();
            long revision=s.ScheduleRevision, next=s.ScheduleAuthority.IdGenerator.NextActionPlanIdValue;
            var p=s.HudPreviewFor(Ai); _=p.WindowSequence; _=p.ReactionOptions; _=p.PreviewDefense(o.Id,Block);
            _=s.HudPreviewFor(Player).OpponentCapabilities;
            Assert.That(s.CurrentSnapshot.ComputeHashHex(),Is.EqualTo(before)); Assert.That(s.ScheduleRevision,Is.EqualTo(revision));
            Assert.That(s.ScheduleAuthority.IdGenerator.NextActionPlanIdValue,Is.EqualTo(next));
            Assert.That(s.ReactionOpportunities.FindOpportunity(o.Id).IsOpen,Is.True);
        }
        [Test] public void PreviewIsCachedPerCommittedSnapshot()
        {
            using var s=Prepared(); var o=Opportunity(s); var p=s.HudPreviewFor(Ai);
            Assert.That(s.HudPreviewFor(Ai),Is.SameAs(p)); Assert.That(p.ReactionOptions,Is.SameAs(p.ReactionOptions));
            Assert.That(p.WindowSequence,Is.SameAs(p.WindowSequence)); Assert.That(p.PreviewDefense(o.Id,Block),Is.SameAs(p.PreviewDefense(o.Id,Block)));
            long tick=p.Decision.Tick; Task09A2Fixture.StepNext(s); Assert.That(s.HudPreviewFor(Ai),Is.Not.SameAs(p));
            Assert.That(p.Decision.Tick,Is.EqualTo(tick)); Assert.That(p.PreviewDefense(o.Id,Block).TriggerTick,Is.EqualTo(11));
        }
        [Test] public void ProposedGuardUsesTheSamePlacementAndQuotaEvaluators()
        {
            using var s=Task09A2Fixture.NewSim(Definition()); s.WindowManager.ScheduleWindow(0,Hero,400); Task09A2Fixture.Step(s,0);
            Seed(s,Enemy,Light,1,Hero); Task09A2Fixture.StepNext(s);
            var request=new OrdinaryPlanRequest(Hero,Task08DefenseCoverageTests.GuardId,GridDirection.East,null,null,s.CurrentTurnWindow.WindowId,3);
            var preview=s.HudPreviewFor(Player).PreviewGuard(request); Assert.That(preview.RejectionCode,Is.Null);
            Assert.That(preview.GuardContacts.Single().Tick,Is.EqualTo(3));
            Assert.That(preview.GuardContacts.Single().DebitQ10,Is.EqualTo(512));
            Task09A2Fixture.Submit(s,Player,new CommandRequest(2,new ScheduleEditScope(s.ScheduleRevision,s.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new[] {new AddOrdinaryPlanOperation(1,Hero,request.ActionSpecId,3)})));
            var result=Advance(s,3); var actual=result.Events.EventsInSequenceOrder.OfType<GuardQuotaConsumedEvent>().Single();
            Assert.That(actual.Tick,Is.EqualTo(preview.GuardContacts.Single().Tick)); Assert.That(actual.RemainingQ10,Is.EqualTo(preview.GuardContacts.Single().RemainingQ10));
        }
        [Test] public void VisibleForeignDefenseCanBePreviewedWithoutAccessToForeignEditablePlans()
        {
            using var s=Prepared(); var o=Opportunity(s); Assert.That(s.ReactionOpportunities.TryAcceptById(o.Id,Block,s.Tick+1,null,out var plan),Is.Null);
            Task09A2Fixture.StepNext(s); var preview=s.HudPreviewFor(Player).PreviewDefense(plan.ActionPlanId);
            Assert.That(preview.RejectionCode,Is.Null); Assert.That(preview.TriggerTick,Is.EqualTo(11));
        }
        [Test] public void HudReadWorldContainsNoAiStateFutureIngressOrHiddenPlan()
        {
            using var s=Prepared(); var hidden=Seed(s,Enemy,Heavy,20,Hero); Task09A2Fixture.StepNext(s);
            var service=s.HudPreviewFor(Player); var template=(BattleSimulation)typeof(HudPreviewService)
                .GetField("_template",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(service);
            Assert.That(template.CurrentSnapshot.AiControllers,Is.Empty);
            Assert.That(template.CommandIngress.CaptureSnapshot().FutureBuckets,Is.Empty);
            Assert.That(template.CurrentSnapshot.Plans.Any(p=>p.ActionPlanId==hidden.ActionPlanId.Value),Is.False);
            Assert.That(template.CurrentSnapshot.Plans.Count,Is.EqualTo(service.Decision.VisiblePlans.Count));
        }
        [Test] public void CachedProjectionRemainsFrozenAfterTheLiveBattleAdvances()
        {
            using var s=Prepared(); var o=Opportunity(s); var saved=s.HudPreviewFor(Ai); Advance(s,11);
            Assert.That(saved.PreviewDefense(o.Id,Block).TriggerTick,Is.EqualTo(11));
            Assert.That(saved.Decision.Tick,Is.EqualTo(2)); Assert.That(s.DecisionSnapshotFor(Ai).Tick,Is.EqualTo(11));
        }
    }
}
