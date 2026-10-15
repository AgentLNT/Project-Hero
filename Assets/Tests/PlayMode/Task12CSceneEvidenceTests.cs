using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.UnityView;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class Task12CSceneEvidenceTests : ProductionRuntimeTestBase
    {
        protected override bool UseLegacyComparisonScene => false;
        static object Field(object value,string name) => value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);
        static string Text(object value) => (string)value.GetType().GetProperty("text").GetValue(value);
        void Drive(long tick) {while(((BattleSimulation)Field(Bootstrap.NewDriver,"_simulation")).Tick<tick) Bootstrap.DriveFrameForTests(0,1f/60);}
        [UnityTest] public IEnumerator MainSceneShowsBothSidesAdrenaline() {yield return Scenario(false);}
        [UnityTest] public IEnumerator MainSceneInsufficientAdrenalinePreviewMatchesHeavyResult() {yield return Scenario(true);}
        IEnumerator Scenario(bool resolve)
        {
            yield return LoadMainScene(); Bootstrap.StopBattle("12C-prepare"); Bootstrap.ReleaseBattle();
            var original=((IBattleSimulationSource)Field(Bootstrap,"_simulationSourceSlot")).BuildSeed();
            var seed=(BattleSimulationSeed)typeof(Task12BSceneEvidenceTests).GetMethod("Configure",BindingFlags.Static|BindingFlags.NonPublic)
                .Invoke(null,new object[] {original});
            var source=NewGameObject("Task12CSceneSource").AddComponent<Task11SceneScenarioSource>(); source.Seed=seed;
            Bootstrap.GetType().GetField("_simulationSourceSlot",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(Bootstrap,source);
            var presentation=UnityEngine.Object.FindFirstObjectByType<BattlePresentationView>();
            var views=(CombatUnitView[])Field(presentation.Registry,"_views");
            var third=NewGameObject("Task12CThirdView").AddComponent<CombatUnitView>();
            third.Configure("t12b_third",UnityEngine.Object.FindFirstObjectByType<GridView>());
            var body=GameObject.CreatePrimitive(PrimitiveType.Capsule); body.transform.SetParent(third.transform,false);
            body.transform.localScale=Vector3.one*.7f; body.GetComponent<Renderer>().sharedMaterial=
                views.SelectMany(v=>v.GetComponentsInChildren<Renderer>(true)).First().sharedMaterial;
            third.ConfigureBody(body.transform,null); presentation.Registry.Configure(views.Concat(new[] {third}).ToArray());
            var capture=NewGameObject("Task12CEvents").AddComponent<Task12BRecordingView>(); capture.Target=presentation;
            Bootstrap.GetType().GetField("_viewConsumerSlot",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(Bootstrap,capture);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New),Is.True,Bootstrap.StartupRejection);
            Drive(360); var simulation=(BattleSimulation)Field(Bootstrap.NewDriver,"_simulation"); var model=presentation.Model;
            var hero=model.Decision.ControlledUnitIds.Single(); Assert.That(simulation.CurrentTurnWindow.OwnerUnitId,Is.EqualTo(hero));
            var enemy=new UnitId(model.Decision.VisibleUnits.Single(u=>u.UnitId!=hero.Value && u.DefinitionId!="unit.t12b.scene.third").UnitId);
            Assert.That(model.Adrenaline.Count,Is.EqualTo(3));
            foreach(var ledger in model.Adrenaline) Assert.That(ledger.AvailableAdrenaline,Is.Zero);
            var heavy=new ActionSpecId("action.t12b.scene.heavy");
            Assert.That(model.SetDraft(new[] {new AddOrdinaryPlanOperation(1,hero,heavy,361,PrimaryTargetUnitId:enemy)}).Succeeded,Is.True);
            Assert.That(model.ConfirmDraft(),Is.Null); Drive(361);
            var preview=model.HudPreviews.OpponentCapabilities.Single(p=>p.TargetUnitId==enemy.Value);
            Assert.That(preview.IsWithinKnownHorizon,Is.True); Assert.That(preview.CanBlock,Is.False); Assert.That(preview.CanDodge,Is.False);
            Assert.That(preview.Options.All(o=>o.RejectionCode=="ADRENALINE_INSUFFICIENT"),Is.True);
            typeof(Task10WiredPresentationTests).GetMethod("CaptureView",BindingFlags.NonPublic|BindingFlags.Static)
                .Invoke(null,new object[] {presentation,resolve ? "12C-main-opponent-cannot-react" : "12C-main-all-adrenaline"});
            string status=Text(Field(presentation,"_status"));
            Assert.That(status,Does.Contain("单位 "+hero.Value)); Assert.That(status,Does.Contain("单位 "+enemy.Value));
            Assert.That(status.Split(new[] {"肾上腺素"},StringSplitOptions.None).Length-1,Is.EqualTo(3));
            Assert.That(Text(Field(presentation,"_previewText")),Does.Contain("Opponent cannot react"));
            long impact=simulation.CurrentSnapshot.Plans.Single(p=>p.ActionSpecId==heavy.Value).ImpactTick;
            if(resolve)
            {
                Drive(impact);
                Assert.That(capture.Facts.OfType<ReactionTriggeredEvent>().Where(e=>e.DefenderUnitId==enemy.Value),Is.Empty);
                var hit=capture.Facts.OfType<DamageChannelResolvedEvent>().Single(e=>e.Tick==impact && e.TargetUnitId==enemy);
                Assert.That(hit.Channels.Sum(c=>c.AfterActionResistanceQ10),Is.EqualTo(20*1024));
                var replay=Bootstrap.NewDriver.RecordedReplay;
                using(var file=File.Create("优化任务/执行记录/M1-regression/12C-main-opponent-cannot-react.heroReplay")) ReplayFile.Save(file,replay);
                using(var player=new ReplayPlayer(seed.Definition,h=>ProductionBattleComposition.Create(seed.Definition,h.EncounterId,h.RuntimeInputs)))
                    for(int run=0;run<100;run++) {if(run==0) player.Load(replay); else player.Restart(); player.Play();
                        while(!player.IsComplete && player.Deviation==null) player.AdvanceOneTick(); Assert.That(player.Deviation,Is.Null); Assert.That(player.IsComplete,Is.True);}
            }
            File.WriteAllText("优化任务/执行记录/M1-regression/12C-main-"+(resolve ? "opponent-cannot-react" : "all-adrenaline")+".txt",
                seed.InputSummary+"\n"+status+"\npreview=Opponent cannot react; enemy Available=0; input deadline is in future; no hard conflict\n"
                +"impact="+impact+"; resolved="+resolve+"; replayRestarts="+(resolve?100:0)+"\n"
                +string.Join("\n",capture.Facts.Where(e=>e is DamageChannelResolvedEvent || e is ReactionTriggeredEvent).Select(ReplayEventComparison.Canonical)));
            Bootstrap.StopBattle("12C-complete"); Bootstrap.ReleaseBattle(); yield return null;
        }
    }
}
