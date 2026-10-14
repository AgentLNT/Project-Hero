using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.UnityView;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    // Passive capture of the real committed batch; all normal rendering/input consumers still run.
    public sealed class Task12BRecordingView : MonoBehaviour, IBattleViewConsumer, IBattleInputConsumer,
        IBattleViewLifetimeConsumer, IBattleDiagnosticFrameConsumer
    {
        public BattlePresentationView Target;
        public readonly List<LogicEvent> Facts=new List<LogicEvent>();
        public string PlayerControllerId=>Target.PlayerControllerId;
        public void Bind(BattleInitializationResult mapping,ProjectHero.Logic.Snapshots.LogicSnapshot snapshot)=>Target.Bind(mapping,snapshot);
        public void Consume(EventBatch events,ProjectHero.Logic.Snapshots.LogicSnapshot snapshot) {Facts.AddRange(events.EventsInSequenceOrder);Target.Consume(events,snapshot);}
        public void BindInput(ProjectHero.Core.Compatibility.Runtime.Input.ViewInputPorts ports)=>Target.BindInput(ports);
        public void ReleaseVisuals()=>Target.ReleaseVisuals();
        public void AdvanceDiagnostics(double seconds)=>Target.AdvanceDiagnostics(seconds);
    }
    public sealed class Task12BSceneEvidenceTests : RuntimeOwnershipTestBase
    {
        protected override bool UseLegacyComparisonScene => false;
        static readonly ActionSpecId Warm = new ActionSpecId("action.t12b.scene.warm");
        static readonly ActionSpecId Light = new ActionSpecId("action.t12b.scene.light");
        static readonly ActionSpecId Heavy = new ActionSpecId("action.t12b.scene.heavy");
        static object Field(object o,string name) => o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);
        static BattleSimulationSeed Configure(BattleSimulationSeed original)
        {
            var d=original.Definition; var e=d.FindEncounter(original.EncounterId);
            var heroSlot=e.Slots.Single(s=>s.SlotId==e.TurnSubmission.ConcurrentHeroSlot);
            var enemySlot=e.Slots.First(s=>s.SlotId!=heroSlot.SlotId);
            var thirdId=new UnitDefinitionId("unit.t12b.scene.third"); var thirdSlot=new EncounterSlotId("t12b_third");
            var volume=new VolumeSpec(new VolumeSpecId("volume.t12b.scene.point"),Enumerable.Range(0,12)
                .Select(f=>new DirectionalTriangleSet((GridDirection)f,new[] {new TrianglePoint(3,0,1)})).ToArray());
            var pattern=new AttackPatternSpec(new AttackPatternId("attack.pattern.t12b.scene"),Enumerable.Range(0,12)
                .Select(f=>new DirectionalTriangleSet((GridDirection)f,new[] {new TrianglePoint(-9,0,1),new TrianglePoint(-3,0,1),new TrianglePoint(9,0,1)})).ToArray());
            var payload=new AttackPayloadSpec(new[] {new DamageComponentSpec(DamageChannels.PhysicalBlunt,20,
                DamageTagMask.Blockable|DamageTagMask.Guardable)},ImpactProfiles.Blunt,.000001f,TargetPolicy.PrimaryTargetOnly,
                TargetRelationMask.Hostile,0,pattern,AttackTagMask.Reactable|AttackTagMask.Blockable|AttackTagMask.Dodgeable);
            var actions=d.Actions.Where(a=>a.Type!=ActionType.Attack).Concat(new[] {
                new ActionSpec(Warm,ActionType.Attack,new AttackTimingSpec(4,1),payload,0),
                new ActionSpec(Light,ActionType.Attack,new AttackTimingSpec(60,1),payload,0),
                new ActionSpec(Heavy,ActionType.Attack,new AttackTimingSpec(80,1),payload,0)}).ToArray();
            var units=d.Units.Select(u=>u with {InitialHealth=2000,ActionSpeed=u.UnitDefinitionId==enemySlot.DefinitionId ? 40 : 20,
                BaseDamageResistanceQ10=new Dictionary<DamageChannelId,int>(),VolumeSpecId=volume.VolumeSpecId}).ToList();
            units.Add(units.Single(u=>u.UnitDefinitionId==enemySlot.DefinitionId) with {UnitDefinitionId=thirdId,ActionSpeed=60});
            var sets=d.ActionSets.Select(s=>s with {ActionSpecIds=actions.Select(a=>a.ActionSpecId).ToArray()}).ToArray();
            var slots=e.Slots.Select(s=>s with {InitialPosition=s.SlotId==heroSlot.SlotId ? new GridPoint(0,0) : new GridPoint(6,0),
                InitialFacing=GridDirection.East}).Concat(new[] {new EncounterUnitSlot(thirdSlot,thirdId,enemySlot.FactionId,new GridPoint(12,0),GridDirection.East)}).ToArray();
            var controllers=e.Controllers.Select(c=>c with {SourceKind=CommandSourceKind.Player,
                ControlledSlots=c.ControlledSlots.Contains(enemySlot.SlotId) ? c.ControlledSlots.Concat(new[] {thirdSlot}).ToArray() : c.ControlledSlots}).ToArray();
            e=e with {Slots=slots,Controllers=controllers,DynamicSpawns=null};
            var patterns=new[] {pattern}; var volumes=d.Volumes.Concat(new[] {volume}).ToArray(); var encounters=new[] {e};
            string hash=BattleDefinitionHash.Compute(d.RulesVersion,d.TicksPerSecond,0,d.Rules,d.ConcurrentAction,d.ReactionRules,d.AdrenalineRules,
                d.FactionModel,d.DamageChannels,d.ImpactProfiles,units,actions,patterns,volumes,d.MovementPatterns,sets,d.StatusEffects,encounters,d.DefaultDynamicSpawnPolicy);
            d=new BattleDefinition(d.RulesVersion,d.TicksPerSecond,d.Rules,d.ConcurrentAction,d.ReactionRules,d.AdrenalineRules,d.FactionModel,
                d.DamageChannels,d.ImpactProfiles,units,actions,patterns,volumes,d.MovementPatterns,sets,d.StatusEffects,encounters,d.DefaultDynamicSpawnPolicy,hash);
            return new BattleSimulationSeed(d,e.EncounterId,new BattleRuntimeInputs(original.RuntimeInputs.InitialRngSeed,d.ConcurrentAction.MetaResourceCost),
                "12B actual main-scene fixture: three explicit actors, Player command entries, 180 tick windows, defense 60/60/30, damage-funded adrenaline, deterministic attack timings; hash="+hash);
        }
        void Drive(long tick) {while(((BattleSimulation)Field(Bootstrap.NewDriver,"_simulation")).Tick<tick) Bootstrap.DriveFrameForTests(0,1f/60);}
        static void Submit(BattleSimulation s,ControllerId controller,CommandRequest request)
        {
            var entry=s.CommandIngress.FindEntry(controller); Assert.That(entry,Is.Not.Null);
            Assert.That(entry.Submit(request),Is.Null);
        }
        static void Add(BattleSimulation s,ControllerId controller,UnitId owner,IReadOnlyList<ScheduleEditOperation> ops)
            =>Submit(s,controller,new CommandRequest(s.Tick+1,new ScheduleEditScope(s.ScheduleAuthority.ScheduleRevision,s.CurrentTurnWindow.WindowId),new ScheduleEditPayload(ops)));
        [UnityTest] public IEnumerator MainSceneBlockAtImpactCompletelyResistsHeavy() {yield return Scenario(1);}
        [UnityTest] public IEnumerator MainSceneEarlierLightConsumesBlockBeforeHeavy() {yield return Scenario(2);}
        [UnityTest] public IEnumerator MainSceneCounterAttackStartsInsideReactionSoftSegment() {yield return Scenario(3);}
        [UnityTest] public IEnumerator MainSceneGuardResistsTwiceThenThirdHitIsFull() {yield return Scenario(4);}
        IEnumerator Scenario(int scenario)
        {
            yield return LoadMainScene(); Bootstrap.StopBattle("12B-scene-prepare"); Bootstrap.ReleaseBattle();
            var original=((IBattleSimulationSource)Field(Bootstrap,"_simulationSourceSlot")).BuildSeed();
            var source=NewGameObject("Task12BSceneSource").AddComponent<Task11SceneScenarioSource>(); source.Seed=Configure(original);
            Bootstrap.GetType().GetField("_simulationSourceSlot",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(Bootstrap,source);
            var authoredPresentation=UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>();
            var oldViews=(CombatUnitView[])Field(authoredPresentation.Registry,"_views");
            var thirdView=NewGameObject("Task12BThirdAttackerView").AddComponent<CombatUnitView>();
            thirdView.Configure("t12b_third",UnityEngine.Object.FindFirstObjectByType<GridView>());
            var thirdBody=GameObject.CreatePrimitive(PrimitiveType.Capsule); thirdBody.name="ThirdAttackerBody";
            thirdBody.transform.SetParent(thirdView.transform,false); thirdBody.transform.localScale=Vector3.one*.7f;
            thirdBody.GetComponent<Renderer>().sharedMaterial=oldViews.SelectMany(v=>v.GetComponentsInChildren<Renderer>(true)).First().sharedMaterial;
            thirdView.ConfigureBody(thirdBody.transform,null);
            authoredPresentation.Registry.Configure(oldViews.Concat(new[] {thirdView}).ToArray());
            var capture=NewGameObject("Task12BCommittedEventCapture").AddComponent<Task12BRecordingView>(); capture.Target=authoredPresentation;
            Bootstrap.GetType().GetField("_viewConsumerSlot",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(Bootstrap,capture);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New),Is.True,Bootstrap.StartupRejection); Drive(0);
            var simulation=(BattleSimulation)Field(Bootstrap.NewDriver,"_simulation"); var e=source.Seed.Definition.FindEncounter(source.Seed.EncounterId);
            var presentation=UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>(); var model=presentation.Model;
            UnitId hero=model.Decision.ControlledUnitIds.Single();
            UnitId third=new UnitId(simulation.CurrentSnapshot.Units.Single(u=>u.DefinitionId=="unit.t12b.scene.third").UnitId);
            UnitId enemy=new UnitId(simulation.CurrentSnapshot.Units.Single(u=>u.UnitId!=hero.Value && u.UnitId!=third.Value).UnitId);
            var heroController=e.Controllers.Single(c=>c.ControlledSlots.Contains(e.TurnSubmission.ConcurrentHeroSlot)).ControllerId;
            var enemyController=e.Controllers.Single(c=>c.ControllerId!=heroController).ControllerId;
            Assert.That(simulation.CurrentTurnWindow.OwnerUnitId,Is.EqualTo(third));
            var warm=new AddOrdinaryPlanOperation(1,third,Warm,1,PrimaryTargetUnitId:hero);
            var first=new List<ScheduleEditOperation>{warm};
            if(scenario==2) first.Add(new AddOrdinaryPlanOperation(2,third,Light,179,PrimaryTargetUnitId:hero));
            Add(simulation,enemyController,third,first); Drive(3);
            Assert.That(model.Decision.VisibleUnits.Single(u=>u.UnitId==hero.Value).AvailableAdrenaline,Is.GreaterThanOrEqualTo(2));
            Drive(180); Assert.That(simulation.CurrentTurnWindow.OwnerUnitId,Is.EqualTo(enemy));
            if(scenario==4) Add(simulation,enemyController,enemy,new ScheduleEditOperation[] {
                new AddOrdinaryPlanOperation(1,enemy,Warm,181,PrimaryTargetUnitId:hero),
                new AddOrdinaryPlanOperation(2,enemy,Warm,184,PrimaryTargetUnitId:hero),
                new AddOrdinaryPlanOperation(3,enemy,Warm,187,PrimaryTargetUnitId:hero)});
            else Add(simulation,enemyController,enemy,new[] {new AddOrdinaryPlanOperation(1,enemy,Heavy,181,PrimaryTargetUnitId:hero)});
            Submit(simulation,heroController,new CommandRequest(181,new WindowCommandScope(simulation.CurrentTurnWindow.WindowId),
                new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction))); Drive(181);
            Assert.That(simulation.LastCommandSet.RejectedCommands,Is.Empty);
            ActionPlan reaction=null;
            if(scenario==4)
            {
                var guard=source.Seed.Definition.Actions.Single(a=>a.Type==ActionType.Guard);
                Assert.That(model.SetDraft(new[] {new AddOrdinaryPlanOperation(1,hero,guard.ActionSpecId,182)}).Succeeded,Is.True);
                Assert.That(model.ConfirmDraft(),Is.Null); Drive(190);
                var debits=capture.Facts.OfType<GuardQuotaConsumedEvent>().ToArray();
                Assert.That(debits.Select(x=>x.DebitQ10),Is.EqualTo(new[] {512,512}));
                Assert.That(debits.Last().RemainingQ10,Is.Zero);
                var hits=capture.Facts.OfType<DamageChannelResolvedEvent>().Where(x=>x.Tick>180 && x.TargetUnitId==hero).ToArray();
                Assert.That(hits.Select(x=>x.Channels.Sum(c=>c.AfterActionResistanceQ10)),Is.EqualTo(new long[] {10*1024,10*1024,20*1024}));
            }
            else
            {
                var opportunity=model.Reactions.Single(o=>o.DefenderUnitId==hero.Value && o.SourceImpactTick==221);
                Assert.That(opportunity.SourceImpactTick,Is.EqualTo(221));
                var block=source.Seed.Definition.Actions.Single(a=>a.Type==ActionType.Block);
                if(scenario==3)
                {
                    Assert.That(model.SetDraft(new[] {new AddOrdinaryPlanOperation(1,hero,Warm,222,PrimaryTargetUnitId:enemy)}).Succeeded,Is.True);
                    Assert.That(model.ConfirmDraft(),Is.Null);
                }
                if(scenario==2) Assert.That(model.ConfirmReaction(opportunity.ReactionOpportunityId,block.ActionSpecId,null,183),Is.Null);
                else Assert.That(model.ConfirmReaction(opportunity.ReactionOpportunityId,block.ActionSpecId,null),Is.Null);
                Drive(182); Assert.That(simulation.LastCommandSet.RejectedCommands,Is.Empty);
                reaction=simulation.ScheduleAuthority.Registry.ActivePlans.Single(p=>p.IsReaction); Drive(scenario==2 ? 222 : 221);
                Assert.That(reaction.ReactionTriggeredAtTick,Is.EqualTo(scenario==2 ? 199 : 221));
                Assert.That(reaction.TerminationReason,Is.EqualTo(ActionTerminationReason.ReactionTriggered));
                if(scenario==2) Assert.That(capture.Facts.OfType<DamageChannelResolvedEvent>()
                    .Single(x=>x.Tick==221 && x.TargetUnitId==hero).Channels.Sum(c=>c.AfterActionResistanceQ10),Is.EqualTo(20*1024));
                else Assert.That(capture.Facts.OfType<BlockResolvedEvent>()
                    .Any(x=>x.Tick==221 && x.AfterBlockDamageQ10==0),Is.True);
                if(scenario==3) {Drive(227); Assert.That(capture.Facts.OfType<ActionPlanLockedEvent>().Any(x=>x.OwnerUnitId==hero.Value && x.StartTick==222),Is.True);}
            }
            typeof(Task10WiredPresentationTests).GetMethod("CaptureView",BindingFlags.NonPublic|BindingFlags.Static)
                .Invoke(null,new object[] {presentation,"12B-main-defense-case-"+scenario});
            var replay=Bootstrap.NewDriver.RecordedReplay; string path="优化任务/执行记录/12B-main-defense-case-"+scenario+".heroReplay";
            using(var file=File.Create(path)) ReplayFile.Save(file,replay);
            using(var player=new ReplayPlayer(source.Seed.Definition,h=>ProductionBattleComposition.Create(source.Seed.Definition,h.EncounterId,h.RuntimeInputs)))
                for(int run=0;run<100;run++) { if(run==0) player.Load(replay); else player.Restart(); player.Play();
                    while(!player.IsComplete && player.Deviation==null) player.AdvanceOneTick(); Assert.That(player.Deviation,Is.Null); Assert.That(player.IsComplete,Is.True); }
            File.WriteAllText("优化任务/执行记录/12B-main-defense-case-"+scenario+".txt",source.Seed.InputSummary+"\nscenario="+scenario+"\ntick="+simulation.Tick
                +"\nreactionTriggeredAtTick="+(reaction?.ReactionTriggeredAtTick.ToString()??"Guard quota 512,512,0")+"\nreplayRestarts=100\n"
                +string.Join("\n",capture.Facts.Where(f=>f is BlockResolvedEvent || f is GuardQuotaConsumedEvent || f is ActionPlanTerminatedEvent || f is DamageChannelResolvedEvent).Select(ReplayEventComparison.Canonical)));
            Bootstrap.StopBattle("12B-scene-complete"); Bootstrap.ReleaseBattle(); yield return null;
        }
    }
}
