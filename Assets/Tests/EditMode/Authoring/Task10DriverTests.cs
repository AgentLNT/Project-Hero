using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.UnityView;
using UnityEngine;

namespace ProjectHero.Authoring.Tests
{
    public class Task10DriverTests
    {
        private sealed class Source : IBattleSimulationSource, INewBattleAssemblySource
        {
            public bool EndAtThree;
            public string SourceName => "real-authoring-test";
            public string LastConfigurationError => null;
            public BattleSimulationSeed BuildSeed() => new BattleSimulationSeed(Task03.Task03.Definition,
                Task03.Task03.EncounterId, Task03.Task03.Inputs, "real assets");
            public BattleSimulationAssembly BuildNewAssembly(BattleSimulationSeed seed)
                => new BattleSimulationAssembly(victoryEvaluator: EndAtThree ? new EndAtThree() : null);
            public void AttachNewSimulation(BattleSimulation simulation) { }
        }
        private sealed class EndAtThree : IVictoryEvaluator
        {
            public string Evaluate(IReadOnlyList<UnitSnapshot> units, VictoryDefinition victory, long tick)
                => tick >= 3 ? victory.VictoryResultCode : null;
        }
        private sealed class View : IBattleViewConsumer
        {
            public int BindCount, TickCount, EndCount;
            public long LastTick;
            public void Bind(BattleInitializationResult initialization, LogicSnapshot snapshot) { BindCount++; }
            public void Consume(EventBatch events, LogicSnapshot snapshot)
            { TickCount++; LastTick = snapshot.Tick; EndCount += events.Events.OfType<BattleEndedEvent>().Count(); }
        }
        private static UnityBattleDriver Create(Source source, View view = null)
        {
            var driver = new UnityBattleDriver();
            driver.BindView(view);
            driver.Initialize(new BattleRuntimeContext(BattleRuntimeMode.New, new RuntimeCallLedger(), new ShadowWriteCounters(), source));
            driver.CreateSimulation(source); return driver;
        }

        [Test] public void DriverAdvancesExpectedTicksFromUnscaledTime()
        {
            var driver = Create(new Source());
            try
            { driver.AdvanceFrame(new BattleFrameDelta(0.1f, false, true)); Assert.That(driver.TicksAdvanced, Is.EqualTo(6)); }
            finally { driver.ResetForNewBattle(); }
        }
        [Test] public void DriverRetainsCatchUpDebtInsteadOfDroppingTicks()
        {
            var driver = Create(new Source());
            try
            {
                driver.AdvanceFrame(new BattleFrameDelta(1, false, true)); Assert.That(driver.TicksAdvanced, Is.EqualTo(8));
                for (int i = 0; i < 8; i++) driver.AdvanceFrame(new BattleFrameDelta(0, false, true));
                Assert.That(driver.TicksAdvanced, Is.EqualTo(60));
            }
            finally { driver.ResetForNewBattle(); }
        }
        [Test] public void DriverStopsAccumulatedTickLoopImmediatelyAfterBattleEndedResult()
        {
            var view = new View(); var driver = Create(new Source { EndAtThree = true }, view);
            try
            {
                driver.AdvanceFrame(new BattleFrameDelta(1, false, true));
                Assert.That(driver.TicksAdvanced, Is.EqualTo(4)); Assert.That(driver.IsStopped, Is.True);
                Assert.That(view.LastTick, Is.EqualTo(3)); Assert.That(view.EndCount, Is.EqualTo(1)); Assert.That(view.TickCount, Is.EqualTo(4));
                ulong hash = driver.CurrentSnapshot.ComputeHash(); driver.AdvanceFrame(new BattleFrameDelta(1, false, true));
                Assert.That(driver.CurrentSnapshot.ComputeHash(), Is.EqualTo(hash)); Assert.That(view.EndCount, Is.EqualTo(1));
                driver.StopBattle("release"); driver.StopBattle("duplicate"); Assert.That(driver.StopCallCount, Is.EqualTo(1));
            }
            finally { driver.ResetForNewBattle(); }
        }
        [Test] public void PauseStopsStepsAndDoesNotAccumulatePausedTime()
        {
            var driver = Create(new Source());
            try
            {
                driver.AdvanceFrame(new BattleFrameDelta(1, true, true)); Assert.That(driver.TicksAdvanced, Is.Zero);
                driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true)); Assert.That(driver.TicksAdvanced, Is.EqualTo(1));
            }
            finally { driver.ResetForNewBattle(); }
        }
        [Test] public void ExternalLifecycleStopConsumesEndFeedbackButCannotExportUnreproducibleReplay()
        {
            var view = new View(); var driver = Create(new Source(), view);
            try
            {
                driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true));
                Assert.That(driver.RecordedReplay.Records.Count, Is.EqualTo(1));
                driver.StopBattle("BOOTSTRAP_DESTROYED");
                Assert.That(view.EndCount, Is.EqualTo(1));
                Assert.That(driver.CurrentSnapshot.BattleEnd.IsEnded, Is.True);
                Assert.That(driver.RecordedReplay, Is.Null);
                Assert.That(driver.ReplayRecordingError, Is.EqualTo("REPLAY_EXTERNAL_LIFECYCLE_STOP_UNRECORDED"));
                driver.ReleaseSimulation();
                Assert.That(driver.RecordedReplay, Is.Null);
                Assert.That(driver.SimulationCreated, Is.False);
                driver.ResetForNewBattle();
                Assert.That(driver.ReplayRecordingError, Is.Null);
            }
            finally { driver.ResetForNewBattle(); }
        }
        [Test] public void NaturallyEndedReplayKeepsFinalTickAfterStopAndRelease()
        {
            var driver = Create(new Source { EndAtThree = true });
            try
            {
                driver.AdvanceFrame(new BattleFrameDelta(1, false, true));
                driver.StopBattle("RELEASE"); driver.ReleaseSimulation();
                Assert.That(driver.ReplayRecordingError, Is.Null);
                Assert.That(driver.RecordedReplay.Records.Count, Is.EqualTo(4));
                Assert.That(driver.RecordedReplay.Records.Last().Tick, Is.EqualTo(3));
                using (var player = new ProjectHero.Logic.Replay.ReplayPlayer(Task03.Task03.Definition,
                    h => BattleSimulation.Create(Task03.Task03.Definition, h.EncounterId, h.RuntimeInputs,
                        new BattleSimulationAssembly(victoryEvaluator: new EndAtThree()))))
                {
                    player.Load(driver.RecordedReplay); player.Play();
                    while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                    Assert.That(player.Deviation, Is.Null);
                    Assert.That(player.IsComplete, Is.True);
                }
            }
            finally { driver.ResetForNewBattle(); }
        }
        [Test] public void ReplayHeaderUsesFormatVersionAndPreStepInitialStateHash()
        {
            var source = new Source(); var driver = Create(source);
            try
            {
                Assert.That(driver.ReplayHeader.ReplayFormatVersion, Is.EqualTo(ProjectHero.Logic.Determinism.ReplayFormat.Version));
                Assert.That(driver.ReplayHeader.InitialStateHash, Is.EqualTo(driver.CurrentSnapshot.ComputeHash()));
                Assert.That(driver.ReplayHeader.RuntimeInputs, Is.EqualTo(source.BuildSeed().RuntimeInputs));
                driver.AdvanceFrame(new BattleFrameDelta(1f / 60, false, true));
                Assert.That(driver.RecordedReplay.Records[0].Tick, Is.Zero);
            }
            finally { driver.ResetForNewBattle(); }
        }
        [Test] public void InvalidFrameDeltaFailsWithoutAdvancingLogic()
        {
            var driver = Create(new Source());
            try
            {
                foreach (float value in new[] { float.NaN, float.PositiveInfinity, -1f })
                    Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() => driver.AdvanceFrame(new BattleFrameDelta(value, false, true)));
                Assert.That(driver.TicksAdvanced, Is.Zero);
            }
            finally { driver.ResetForNewBattle(); }
        }
        [Test] public void UnitViewFactionBindingComesFromInitializationMapping()
        {
            var gridObject = new GameObject("grid"); var viewObject = new GameObject("display-name");
            try
            {
                var grid = gridObject.AddComponent<GridView>(); var view = viewObject.AddComponent<CombatUnitView>();
                var source = new Source(); var seed = source.BuildSeed();
                var initial = BattleInitializer.BuildInitialState(seed.Definition, seed.EncounterId, seed.RuntimeInputs);
                using (var sim = BattleSimulation.Create(seed.Definition, seed.EncounterId, seed.RuntimeInputs))
                {
                    view.Configure("hero", grid); var id = initial.SlotToUnitId[new ProjectHero.Logic.Ids.EncounterSlotId("hero")];
                    view.Bind(initial, sim.CurrentSnapshot.Units.Single(u => u.UnitId == id.Value));
                    Assert.That(view.UnitId, Is.EqualTo(id)); Assert.That(view.FactionId.Value, Is.EqualTo(view.LatestSnapshot.FactionId));
                    viewObject.name = "renamed"; Assert.That(view.UnitId, Is.EqualTo(id));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(viewObject); UnityEngine.Object.DestroyImmediate(gridObject); }
        }
        [Test] public void SemanticEventsMapToFeedbackWithoutLogicWriteback()
        {
            var mapper = new CombatFeedbackMapper(); int feedback = 0; mapper.FeedbackRequested += _ => feedback++;
            var end = new BattleEndedEvent(0, 1, "done"); mapper.Consume(end); mapper.Consume(end);
            Assert.That(feedback, Is.EqualTo(1)); Assert.That(mapper.BattleEndFeedbackCount, Is.EqualTo(1));
            Assert.That(typeof(CombatFeedbackMapper).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Any(f => f.FieldType == typeof(BattleSimulation)), Is.False);
        }
    }
}
