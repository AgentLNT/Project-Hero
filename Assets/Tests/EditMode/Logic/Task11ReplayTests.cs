using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    public sealed class Task11ReplayTests
    {
        [Test] public void AllocationDiagnosticProviderDoesNotChangeEventsOrSnapshotHash()
        {
            long counter = 0;
            var timing = new StepPhaseTimingRecorder(() => counter += 10);
            var definition = Task09A2Fixture.BuildDefinition();
            using (var observed = Task09A2Fixture.NewSim(definition, new BattleSimulationAssembly(phaseTiming: timing)))
            using (var reference = Task09A2Fixture.NewSim(definition, new BattleSimulationAssembly()))
            {
                for (int tick = 0; tick < 20; tick++)
                {
                    var a = Task09A2Fixture.StepNext(observed); var b = Task09A2Fixture.StepNext(reference);
                    Assert.That(a.SnapshotHash, Is.EqualTo(b.SnapshotHash));
                    Assert.That(a.Events.Events.Select(ReplayEventComparison.Canonical), Is.EqualTo(b.Events.Events.Select(ReplayEventComparison.Canonical)));
                }
            }
            Assert.That(timing.MeasuredTicks, Is.EqualTo(20));
            Assert.That(timing.Snapshot().All(p => p.Invocations == 20 && p.TotalAllocatedBytes > 0), Is.True);
        }
        [Test] public void PriorV3ReplayIsRejectedBeforeCreatingWorld()
        {
            var definition = Task09A2Fixture.BuildDefinition(); var replay = Record(definition); int created = 0;
            using (var player = new ReplayPlayer(definition, header => { created++; return Create(definition, header); }))
            {
                var error = Assert.Throws<LogicDefinitionException>(() => player.Load(new BattleReplay(
                    replay.Header with { ReplayFormatVersion = 3 }, replay.Records, replay.Submissions)));
                Assert.That(error.ErrorCode, Is.EqualTo(ReplayCodes.REPLAY_FORMAT_VERSION_MISMATCH));
                Assert.That(created, Is.Zero);
            }
        }
        [Test] public void ReplayCannotPlayBeforeACompatibleFileHasBeenLoaded()
        {
            using (var player = new ReplayPlayer(Task09A2Fixture.BuildDefinition()))
            {
                var error = Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() => player.Play());
                Assert.That(error.ErrorCode, Is.EqualTo("REPLAY_NOT_LOADED"));
                Assert.That(player.IsPaused, Is.True);
                Assert.That(player.CurrentSnapshot, Is.Null);
            }
        }
        private sealed class Window : ITurnWindowSchedule
        {
            public WindowOpenRequest TryOpenDue(long tick) => tick == 0
                ? new WindowOpenRequest(Task09A2Fixture.Hero, 400) : null;
            public bool ShouldCloseCurrentWindow(long tick) => false;
        }
        private static BattleSimulation Create(BattleDefinition definition, ReplayHeader header = null)
            => BattleSimulation.Create(definition, Task09A2Fixture.EncounterId,
                header?.RuntimeInputs ?? Task09A2Fixture.Inputs,
                new BattleSimulationAssembly(turnWindowSchedule: new Window()));
        private static CommandRequest Add(long targetTick, long start, UnitId? target = null)
            => new CommandRequest(targetTick, new ScheduleEditScope(0, new WindowId(1)),
                new ScheduleEditPayload(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1,
                    Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), start,
                    PrimaryTargetUnitId: target ?? Task09A2Fixture.Monster) }));
        private static StepResult Step(BattleSimulation sim, ReplayRecorder recorder)
        {
            recorder.CaptureBeforeStep();
            long tick = sim.Tick + 1;
            var result = sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
            recorder.RecordCommittedStep(result); return result;
        }
        private static BattleReplay Record(BattleDefinition definition, bool future = true)
        {
            using (var sim = Create(definition))
            {
                var recorder = new ReplayRecorder(sim);
                Step(sim, recorder);
                Assert.That(sim.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(Add(future ? 5 : 1, 6)), Is.Null);
                for (int tick = 1; tick < 22; tick++) Step(sim, recorder);
                Assert.That(recorder.BuildReplay().Records.SelectMany(r => r.Events)
                    .Any(e => ReplayEventComparison.Canonical(e).Contains("DamageChannelResolvedEvent")), Is.True,
                    "The replay must contain a real impact, not just empty ticks or rejected requests.");
                return recorder.BuildReplay();
            }
        }
        private static void PlayAll(ReplayPlayer player)
        {
            player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            Assert.That(player.Deviation, Is.Null);
            Assert.That(player.IsComplete, Is.True);
        }

        [Test] public void FutureRequestKeepsOriginalSubmissionBoundaryAndEveryCheckpoint()
        {
            var def = Task09A2Fixture.BuildDefinition(); var replay = Record(def);
            Assert.That(replay.Submissions.Single().SubmittedAtTick, Is.EqualTo(0));
            Assert.That(replay.Submissions.Single().Fact.Request.TargetTick, Is.EqualTo(5));
            Assert.That(replay.Records.Select(r => r.Tick), Is.EqualTo(Enumerable.Range(0, 22).Select(i => (long)i)));
            using (var player = new ReplayPlayer(def, h => Create(def, h))) { player.Load(replay); PlayAll(player); }
        }
        [Test] public void ReplayRepeatsOneHundredTimesWithFullEventPayloadAndHashes()
        {
            var def = Task09A2Fixture.BuildDefinition(); var replay = Record(def);
            using (var player = new ReplayPlayer(def, h => Create(def, h)))
            {
                player.Load(replay);
                for (int run = 0; run < 100; run++)
                { if (run > 0) player.Restart(); player.SetSpeed((run % 8) + 1); PlayAll(player); }
            }
        }
        [Test] public void SavedReplayLoadsAndReplaysWithoutRuntimeTypeDeserialization()
        {
            var def = Task09A2Fixture.BuildDefinition(); var original = Record(def);
            using (var stream = new MemoryStream())
            {
                ReplayFile.Save(stream, original); Assert.That(stream.Length, Is.GreaterThan(100)); stream.Position = 0;
                var loaded = ReplayFile.Load(stream, def);
                Assert.That(loaded.Header, Is.EqualTo(original.Header));
                using (var player = new ReplayPlayer(def, h => Create(def, h))) { player.Load(loaded); PlayAll(player); }
            }
        }
        [Test] public void PauseDoesNotAdvanceReplayAndRestartCreatesFreshWorld()
        {
            var def = Task09A2Fixture.BuildDefinition(); var replay = Record(def);
            using (var player = new ReplayPlayer(def, h => Create(def, h)))
            {
                player.Load(replay); ulong initial = player.CurrentSnapshot.ComputeHash();
                Assert.That(player.AdvanceOneTick(), Is.False); player.Play(); Assert.That(player.AdvanceOneTick(), Is.True);
                player.Pause(); ulong paused = player.CurrentSnapshot.ComputeHash();
                Assert.That(player.AdvanceOneTick(), Is.False); Assert.That(player.CurrentSnapshot.ComputeHash(), Is.EqualTo(paused));
                player.Restart(); Assert.That(player.CurrentSnapshot.ComputeHash(), Is.EqualTo(initial)); PlayAll(player);
            }
        }
        [TestCase("format")] [TestCase("rules")] [TestCase("definition")]
        public void IncompatibleHeaderIsRejectedBeforeCreatingWorld(string kind)
        {
            var def = Task09A2Fixture.BuildDefinition(); var replay = Record(def); int created = 0;
            var header = kind == "format" ? replay.Header with { ReplayFormatVersion = 2 }
                : kind == "rules" ? replay.Header with { RulesVersion = "different" }
                : replay.Header with { BattleDefinitionHash = "different" };
            using (var player = new ReplayPlayer(def, h => { created++; return Create(def, h); }))
                Assert.Throws<LogicDefinitionException>(() => player.Load(new BattleReplay(header, replay.Records, replay.Submissions)));
            Assert.That(created, Is.Zero);
        }
        [Test] public void WrongInitialHashIsRejectedBeforeFirstStep()
        {
            var def = Task09A2Fixture.BuildDefinition(); var replay = Record(def);
            using (var player = new ReplayPlayer(def, h => Create(def, h)))
                Assert.Throws<LogicDefinitionException>(() => player.Load(new BattleReplay(
                    replay.Header with { InitialStateHash = replay.Header.InitialStateHash ^ 1 }, replay.Records, replay.Submissions)));
        }
        [TestCase(CommandSourceKind.Ai)] [TestCase(CommandSourceKind.System)]
        public void ReplayCannotAuthorizeAiOrSystemInputs(CommandSourceKind source)
        {
            var def = Task09A2Fixture.BuildDefinition(); var replay = Record(def); int created = 0;
            var submission = replay.Submissions[0] with { Fact = replay.Submissions[0].Fact with { SourceKind = source } };
            using (var player = new ReplayPlayer(def, h => { created++; return Create(def, h); }))
                Assert.Throws<LogicDefinitionException>(() => player.Load(new BattleReplay(replay.Header, replay.Records, new[] { submission })));
            Assert.That(created, Is.Zero);
        }
        [Test] public void DuplicateOrRegressingProducerOrdinalsCannotLoad()
        {
            var def = Task09A2Fixture.BuildDefinition(); var replay = Record(def);
            using (var player = new ReplayPlayer(def, h => Create(def, h)))
                Assert.Throws<LogicDefinitionException>(() => player.Load(new BattleReplay(replay.Header, replay.Records,
                    new[] { replay.Submissions[0], replay.Submissions[0] })));
        }
        [Test] public void UnknownControllerCannotLoad()
        {
            var def = Task09A2Fixture.BuildDefinition(); var replay = Record(def);
            using (var player = new ReplayPlayer(def, h => Create(def, h)))
                Assert.Throws<LogicDefinitionException>(() => player.Load(new BattleReplay(replay.Header, replay.Records,
                    new[] { replay.Submissions[0] with { Fact = replay.Submissions[0].Fact with { Issuer = new ControllerId("unknown") } } })));
        }
        [Test] public void FirstSnapshotDeviationStopsAtExactTick()
        {
            var def = Task09A2Fixture.BuildDefinition(); var replay = Record(def); var records = replay.Records.ToArray();
            records[3] = records[3] with { SnapshotHash = records[3].SnapshotHash ^ 1 };
            using (var player = new ReplayPlayer(def, h => Create(def, h)))
            {
                player.Load(new BattleReplay(replay.Header, records, replay.Submissions)); player.Play();
                while (player.Deviation == null) player.AdvanceOneTick();
                Assert.That(player.Deviation.Tick, Is.EqualTo(3)); Assert.That(player.IsPaused, Is.True);
            }
        }
        [Test] public void EventPayloadMutationIsDetectedEvenWithMatchingSnapshotHash()
        {
            var def = Task09A2Fixture.BuildDefinition(); var replay = Record(def); var records = replay.Records.ToArray();
            var events = records[0].Events.ToArray(); var first = (StoredReplayEvent)events[0];
            events[0] = first with { CanonicalPayload = first.CanonicalPayload + "changed" };
            records[0] = records[0] with { Events = events };
            using (var player = new ReplayPlayer(def, h => Create(def, h)))
            {
                player.Load(new BattleReplay(replay.Header, records, replay.Submissions)); player.Play();
                Assert.That(player.AdvanceOneTick(), Is.False); Assert.That(player.Deviation.Tick, Is.Zero);
                Assert.That(player.Deviation.EventIndex, Is.Zero); Assert.That(player.Deviation.ReasonCode, Is.EqualTo("REPLAY_EVENT_MISMATCH"));
            }
        }
        [Test] public void FutureBucketsWithEqualCountsAndDifferentPayloadsHaveDifferentHashes()
        {
            var def = Task09A2Fixture.BuildDefinition();
            using (var a = Create(def)) using (var b = Create(def))
            {
                Task09A2Fixture.StepNext(a); Task09A2Fixture.StepNext(b);
                a.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(Add(5, 6));
                b.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(Add(5, 7));
                Assert.That(Task09A2Fixture.StepNext(a).SnapshotHash, Is.Not.EqualTo(Task09A2Fixture.StepNext(b).SnapshotHash));
            }
        }
        [Test] public void CanonicalEventComparisonIncludesGridPointPublicFields()
        {
            var a = new ForcedDisplacementResolvedEvent(1, 1, new UnitId(1), new GridPoint(0, 0), new GridPoint(2, 0),
                GridDirection.East, 1, 1, 1, 1, ForcedDisplacementStopReason.Completed, Array.Empty<ActionPlanId>());
            var b = a with { To = new GridPoint(4, 0) };
            Assert.That(ReplayEventComparison.Canonical(a), Is.Not.EqualTo(ReplayEventComparison.Canonical(b)));
        }
        [Test] public void AiDecisionDiagnosticsAreBoundedWhileAuthoritativeCountsRemainComplete()
        {
            var def = Task09A2Fixture.BuildDefinition();
            var ai = new ProjectHero.Logic.AI.AiControllerLogic(Task09A2Fixture.Inputs.InitialRngSeed);
            ai.RegisterController(def.FindEncounter(Task09A2Fixture.EncounterId).Controllers.Single(c => c.SourceKind == CommandSourceKind.Ai));
            using (var sim = Task09A2Fixture.NewSim(def, new BattleSimulationAssembly(
                decisionObservers: new IDecisionObserver[] { ai }, aiRuntimeStates: ai)))
            {
                ai.AttachPorts(sim.AiReactionOpportunities, sim.AiActionPlanLookup, sim.MovementPathCalculator);
                for (int tick = 0; tick < 800; tick++) Task09A2Fixture.StepNext(sim);
                Assert.That(ai.Decisions.Count, Is.EqualTo(ProjectHero.Logic.AI.AiControllerLogic.MaximumDiagnosticDecisions));
                Assert.That(sim.CurrentSnapshot.AiControllers.Single().DecisionCount, Is.EqualTo(800));
            }
        }
    }
}
