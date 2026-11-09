using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Tests
{
    public sealed class ClashRecoveryTests
    {
        const long Impact = 393;

        // Real production windows and registered player inputs. Different windups allow both
        // owners to submit in their own windows and meet at the same impact Tick.
        sealed class Rig : IDisposable
        {
            public readonly BattleDefinition Definition;
            public readonly BattleSimulation Sim;
            public readonly ReplayRecorder Recorder;
            public readonly List<LogicEvent> Events = new List<LogicEvent>();
            public ActionPlan First, Second;

            public Rig(int balance = 0, int firstRecovery = 30, int secondRecovery = 45,
                int stability = 10, int controlDuration = 30, bool three = false,
                bool prepareResource = false, IUnitStateAdvanceSystem stateSource = null, long impact = Impact)
            {
                var definition = CombatExperimentDefinition.Build(new CombatExperimentScenario(
                    "clash_recovery", StabilityQ10: stability * 1024,
                    Script: three ? ExperimentScript.SweepTwo : ExperimentScript.Clash));
                var actions = definition.Actions.Select(a =>
                {
                    if (a.ActionSpecId == CombatExperimentDefinition.Action("heavy"))
                        return a with { Timing = new AttackTimingSpec(checked((int)(impact - 123)), firstRecovery),
                            Payload = ((AttackPayloadSpec)a.Payload) with { Strength = new AttackStrengthV05(
                                balance > 0 ? PhysicalRecipe.Blunt : PhysicalRecipe.Slash,
                                balance > 0 ? 2048 : 1024, AttackGrowthAttribute.Strength) } };
                    if (a.ActionSpecId == CombatExperimentDefinition.Action("quick"))
                        return a with { Timing = new AttackTimingSpec(checked((int)(impact - 182)), secondRecovery),
                            Payload = ((AttackPayloadSpec)a.Payload) with { Strength = new AttackStrengthV05(
                                balance < 0 ? PhysicalRecipe.Blunt : PhysicalRecipe.Slash,
                                balance < 0 ? 2048 : 1024, AttackGrowthAttribute.Agility) } };
                    if (prepareResource && a.ActionSpecId == CombatExperimentDefinition.Action("probe_pierce"))
                        return a with { Payload = ((AttackPayloadSpec)a.Payload) with {
                            Strength = new AttackStrengthV05(PhysicalRecipe.Pierce, 3072, AttackGrowthAttribute.Agility) } };
                    if (three && !prepareResource && a.ActionSpecId == CombatExperimentDefinition.Action("probe_slash"))
                        return a with { Timing = new AttackTimingSpec(30, 60) };
                    return a;
                }).ToArray();
                definition = definition with { Actions = actions, Rules = definition.Rules with {
                    StaggerAutoRecoveryTicks = controlDuration, KnockdownAutoRecoveryTicks = controlDuration } };
                Definition = definition with { BattleDefinitionHashValue = CombatExperimentDefinition.Hash(definition) };
                var inputs = new BattleRuntimeInputs(31, 4);
                var composition = new ProductionBattleComposition(Definition, CombatExperimentDefinition.Encounter, inputs);
                Sim = BattleSimulation.Create(Definition, CombatExperimentDefinition.Encounter, inputs,
                    new BattleSimulationAssembly(turnWindowSchedule: composition, unitStateAdvance: stateSource));
                composition.Attach(Sim);
                Recorder = new ReplayRecorder(Sim);
                Advance(0);
                if (prepareResource)
                {
                    Add(1, "probe_pierce", 1, 3); Advance(1);
                }
                Add(1, "heavy", 123, 2); Advance(122);
                First = Sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.OwnerUnitId.Value == 1);
                Advance(181); Add(2, "quick", 182, 1); Advance(182);
                Second = Sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.OwnerUnitId.Value == 2);
                if (three && !prepareResource)
                {
                    Advance(362); Add(3, "probe_slash", impact - 30, 1);
                }
                Advance(impact);
                Assert.That(Events.OfType<CommandRejectedEvent>(), Is.Empty);
                Assert.That(Events.OfType<ClashParticipantResolvedEvent>().Count(), Is.EqualTo(three && !prepareResource ? 3 : 2));
            }

            public void Add(long owner, string action, long start, long target = 2, GridPoint? destination = null)
                => Submit(owner, new CommandRequest(Sim.Tick + 1,
                    new ScheduleEditScope(Sim.ScheduleRevision, Sim.CurrentTurnWindow.WindowId),
                    new ScheduleEditPayload(new ScheduleEditOperation[] {
                        Operation(owner, action, start, target, destination) })));

            public static AddOrdinaryPlanOperation Operation(long owner, string action, long start,
                long target = 2, GridPoint? destination = null)
                => new AddOrdinaryPlanOperation(1, new UnitId(owner), CombatExperimentDefinition.Action(action), start,
                    PrimaryTargetUnitId: action == "move" || action == "guard" ? (UnitId?)null : new UnitId(target),
                    Facing: owner == 1 ? GridDirection.East : GridDirection.West, Destination: destination);

            public void Submit(long owner, CommandRequest request)
                => Assert.That(Sim.CommandIngress.FindEntry(CombatExperimentDefinition.Controller(owner)).Submit(request), Is.Null);

            public void Advance(long target)
            {
                while (Sim.Tick < target)
                {
                    long tick = Sim.Tick + 1;
                    Recorder.CaptureBeforeStep();
                    var step = Sim.Step(tick, Sim.CommandIngress.FreezeTick(tick));
                    Assert.That(step.Status, Is.Not.EqualTo(StepStatus.AlreadyEnded));
                    Recorder.RecordCommittedStep(step);
                    Events.AddRange(step.Events.Events);
                }
            }

            public void VerifyReplay()
            {
                using var stream = new MemoryStream();
                ReplayFile.Save(stream, Recorder.BuildReplay()); stream.Position = 0;
                using var player = new ReplayPlayer(Definition);
                player.Load(ReplayFile.Load(stream, Definition)); player.Play();
                while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                Assert.That(player.Deviation, Is.Null);
                Assert.That(player.IsComplete, Is.True);
            }
            public void Dispose() => Sim.Dispose();
        }

        [TestCase(-1)] [TestCase(0)] [TestCase(1)]
        public void EveryParticipantKeepsItsOwnRecoveryRegardlessOfClashOutcome(int balance)
        {
            using var r = new Rig(balance);
            foreach (var plan in new[] { r.First, r.Second })
            {
                Assert.That(plan.IsRunning, Is.True);
                Assert.That(plan.ClashResolvedAtTick, Is.EqualTo(Impact));
                Assert.That(r.Sim.ScheduleAuthority.FindLane(plan.OwnerUnitId).Contains(plan.ActionPlanId), Is.True);
                Assert.That(r.Sim.CurrentSnapshot.Plans.Single(p => p.ActionPlanId == plan.ActionPlanId.Value).ClashResolvedAtTick,
                    Is.EqualTo(Impact));
            }
            Assert.That(r.Events.OfType<ActionPlanTerminatedEvent>(), Is.Empty);
            long damage = r.Events.OfType<DamageChannelResolvedEvent>().Sum(e => e.TotalDamageQ10);
            r.Advance(Impact + 29); Assert.That(r.First.IsRunning, Is.True);
            r.Advance(Impact + 30);
            Assert.That(r.First.TerminalTick, Is.EqualTo(Impact + 30));
            Assert.That(r.First.TerminationReason, Is.EqualTo(ActionTerminationReason.InterruptedByClash));
            Assert.That(r.Second.IsRunning, Is.True);
            r.Advance(Impact + 45);
            Assert.That(r.Second.TerminalTick, Is.EqualTo(Impact + 45));
            Assert.That(r.Sim.ScheduleAuthority.Registry.ActivePlans, Is.Empty);
            Assert.That(r.Events.OfType<DamageChannelResolvedEvent>().Sum(e => e.TotalDamageQ10), Is.EqualTo(damage));
            Assert.That(r.Events.OfType<ClashParticipantResolvedEvent>().Count(), Is.EqualTo(2));
            Assert.That(r.Events.OfType<ActionPlanTerminatedEvent>().Count(), Is.EqualTo(2));
            r.VerifyReplay();
        }

        [Test]
        public void MinimumOneTickRecoveryEndsWithoutAddingAnotherRecovery()
        {
            using var r = new Rig(firstRecovery: 1);
            Assert.That(r.First.IsRunning, Is.True);
            r.Advance(Impact + 1);
            Assert.That(r.First.TerminalTick, Is.EqualTo(Impact + 1));
            Assert.That(r.Second.IsRunning, Is.True);
            r.Advance(Impact + 45); r.VerifyReplay();
        }

        [Test]
        public void ThreeWayClashKeepsDifferentRecoveryDeadlinesAndReplaysThemIndependently()
        {
            using var r = new Rig(three: true);
            var plans = r.Sim.ScheduleAuthority.Registry.ActivePlans.OrderBy(p => p.OwnerUnitId.Value).ToArray();
            Assert.That(plans.Select(p => p.EndTick), Is.EqualTo(new[] { Impact + 30, Impact + 45, Impact + 60 }));
            foreach (var plan in plans)
            {
                r.Advance(plan.EndTick - 1); Assert.That(plan.IsRunning, Is.True);
                Assert.That(r.Sim.ScheduleAuthority.FindLane(plan.OwnerUnitId).Contains(plan.ActionPlanId), Is.True);
                r.Advance(plan.EndTick); Assert.That(plan.TerminalTick, Is.EqualTo(plan.EndTick));
            }
            Assert.That(r.Sim.ScheduleAuthority.Registry.ActivePlans, Is.Empty);
            Assert.That(r.Events.OfType<ClashParticipantResolvedEvent>().Count(), Is.EqualTo(3));
            r.VerifyReplay();
        }

        [TestCase(540L)] [TestCase(541L)] [TestCase(542L)]
        public void WindowClosureAndTheNextOwnersWindowDoNotReleaseRecovery(long impact)
        {
            using var r = new Rig(impact: impact);
            r.Advance(impact + 1);
            Assert.That(r.First.IsRunning && r.Second.IsRunning, Is.True);
            Assert.That(r.Sim.ScheduleAuthority.FindLane(r.First.OwnerUnitId).Contains(r.First.ActionPlanId), Is.True);
            r.Advance(impact + 30);
            Assert.That(r.First.TerminalTick, Is.EqualTo(impact + 30));
            Assert.That(r.Second.IsRunning, Is.True);
            r.Advance(impact + 45); r.VerifyReplay();
        }

        [TestCase("probe_slash")] [TestCase("guard")] [TestCase("move")]
        public void OrdinaryActionsCannotStartInsideRecoveryAndCanStartAtTheExactEnd(string action)
        {
            using var r = new Rig();
            var destination = action == "move" ? new GridPoint(-6, 0) : (GridPoint?)null;
            var preview = r.Sim.ScheduleEditor.Apply(new[] {
                Rig.Operation(1, action, r.First.EndTick - 1, destination: destination) }, r.Sim.Tick + 1,
                r.Sim.ScheduleRevision, r.Sim.ScheduleRevision, preview: true,
                expectedWindowId: r.Sim.CurrentTurnWindow.WindowId, issuer: CombatExperimentDefinition.Controller(1));
            Assert.That(!preview.Succeeded || preview.AppliedEdits.Single().ResultingStartTick >= r.First.EndTick, Is.True);
            r.Add(1, action, r.First.EndTick, destination: destination); r.Advance(Impact + 1);
            Assert.That(r.Events.OfType<CommandRejectedEvent>(), Is.Empty);
            var next = r.Sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.OwnerUnitId.Value == 1 && p != r.First);
            r.Advance(r.First.EndTick - 1); Assert.That(next.IsEditable, Is.True);
            r.Advance(r.First.EndTick); Assert.That(next.IsRunning, Is.True);
            Assert.That(next.LockedAtTick, Is.EqualTo(r.First.EndTick));
            r.VerifyReplay();
        }

        [TestCase(6, 12, UnitState.Staggered)] [TestCase(6, 60, UnitState.Staggered)]
        [TestCase(4, 12, UnitState.KnockedDown)] [TestCase(4, 60, UnitState.KnockedDown)]
        public void SameTickControlAndRecoveryBothHaveToExpire(int stability, int controlTicks, UnitState control)
        {
            using var r = new Rig(balance: 1, stability: stability, controlDuration: controlTicks);
            Assert.That(r.Sim.FindUnitStateMachine(new UnitId(2)).CurrentState, Is.EqualTo(control));
            Assert.That(r.Second.IsRunning, Is.True);
            r.Advance(Impact + Math.Min(controlTicks, 45));
            Assert.That(r.Sim.FindUnitStateMachine(new UnitId(2)).CurrentState,
                Is.EqualTo(controlTicks < 45 ? UnitState.Recovery : control));
            if (controlTicks < 45) Assert.That(r.Second.IsRunning, Is.True);
            else Assert.That(r.Second.IsTerminal, Is.True);
            r.Advance(Impact + Math.Max(controlTicks, 45));
            Assert.That(r.Sim.FindUnitStateMachine(new UnitId(2)).CurrentState, Is.EqualTo(UnitState.Idle));
            Assert.That(r.Second.TerminalTick, Is.EqualTo(Impact + 45));
            r.VerifyReplay();
        }

        sealed class ScheduledState : IUnitStateAdvanceSystem
        {
            readonly long _tick; readonly int _duration; readonly bool _death;
            public ScheduledState(long tick, int duration = 0, bool death = false)
            { _tick = tick; _duration = duration; _death = death; }
            public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(IReadOnlyList<UnitSnapshot> units, long tick)
                => tick != _tick ? Array.Empty<UnitStateAdvanceRequest>() : new[] {
                    _death ? new UnitStateAdvanceRequest(1, 0) : new UnitStateAdvanceRequest(1, null,
                        StateTransitionSpec.Timed(UnitState.Staggered, _duration, UnitState.Idle)) };
        }

        [TestCase(12)] [TestCase(60)]
        public void LaterControlCannotEraseTheUnfinishedRecovery(int duration)
        {
            using var r = new Rig(stateSource: new ScheduledState(Impact + 1, duration));
            r.Advance(Impact + 2); Assert.That(r.First.IsRunning, Is.True);
            r.Advance(Impact + 30);
            Assert.That(r.First.TerminalTick, Is.EqualTo(Impact + 30));
            Assert.That(r.Sim.FindUnitStateMachine(new UnitId(1)).CurrentState,
                Is.EqualTo(duration < 30 ? UnitState.Idle : UnitState.Staggered));
            r.Advance(Impact + 1 + Math.Max(30, duration));
            Assert.That(r.Sim.FindUnitStateMachine(new UnitId(1)).CurrentState, Is.EqualTo(UnitState.Idle));
        }

        [Test]
        public void DeathAndBattleEndClearRecoveryWithoutWaitingForItsDeadline()
        {
            using var r = new Rig(stateSource: new ScheduledState(Impact + 1, death: true));
            r.Advance(Impact + 1);
            Assert.That(r.Sim.IsEnded, Is.True);
            Assert.That(r.First.TerminationReason, Is.EqualTo(ActionTerminationReason.OwnerDied));
            Assert.That(r.Second.TerminationReason, Is.EqualTo(ActionTerminationReason.BattleEnded));
            Assert.That(r.Sim.ScheduleAuthority.Registry.ActivePlans, Is.Empty);
        }

        [TestCase(ReactionCommandKind.Block)] [TestCase(ReactionCommandKind.Dodge)]
        public void ReactionsUseTheSameRecoveryBoundaryAndCannotOverlapTheAttack(ReactionCommandKind kind)
        {
            using var r = new Rig(three: true, prepareResource: true);
            Assert.That(r.Sim.AdrenalineLedgerOf(new UnitId(1)).AvailableAdrenaline,
                Is.GreaterThanOrEqualTo(CombatValuesV05.ResourceUnitsPerPoint));
            r.Add(3, "probe_slash", Impact + 1, 1); r.Advance(Impact + 1);
            var opportunity = r.Sim.ReactionOpportunities.ActiveOpportunities.Single(o =>
                o.DefenderUnitId.Value == 1 && o.IsOpen && o.SourceImpactTick == Impact + 31);
            string action = kind == ReactionCommandKind.Block ? "block" : "dodge";
            var destination = kind == ReactionCommandKind.Dodge ? new GridPoint(-2, 0) : (GridPoint?)null;
            CommandRequest Request(long start) => new CommandRequest(r.Sim.Tick + 1,
                new ReactionCommandScope(opportunity.Id), new ReactionCommandPayload(kind,
                    CombatExperimentDefinition.Action(action), destination, start));
            r.Submit(1, Request(r.First.EndTick - 1)); r.Advance(Impact + 2);
            Assert.That(r.Events.OfType<CommandRejectedEvent>().Count(), Is.EqualTo(1));
            Assert.That(r.Sim.AdrenalineLedgerOf(new UnitId(1)).ReservedAdrenaline, Is.Zero);
            r.Submit(1, Request(r.First.EndTick)); r.Advance(Impact + 3);
            Assert.That(r.Events.OfType<CommandRejectedEvent>().Count(), Is.EqualTo(1));
            var reaction = r.Sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.IsReaction);
            Assert.That(reaction.StartTick, Is.EqualTo(r.First.EndTick));
            r.Advance(r.First.EndTick - 1); Assert.That(reaction.IsLocked, Is.True);
            r.Advance(r.First.EndTick); Assert.That(reaction.IsRunning, Is.True);
            Assert.That(r.First.IsTerminal, Is.True);
            r.Advance(Impact + 31);
            Assert.That(r.Events.OfType<ReactionTriggeredEvent>().Count(), Is.EqualTo(1));
            r.VerifyReplay();
        }

        [Test]
        public void RecoveryMarkerParticipatesInSnapshotHashAndOldReplayFormatsAreRejected()
        {
            using var r = new Rig();
            var plan = ActionPlanSnapshot.From(r.First);
            ulong Hash(ActionPlanSnapshot value) => new LogicSnapshot(0, "clash-recovery", "definition", "encounter",
                BattleEndSnapshot.Active(), Array.Empty<UnitSnapshot>(), Array.Empty<StatusEffectSnapshot>(),
                TurnWindowManagerSnapshot.None(), ConcurrentActionSnapshot.None(), BattleResourceSnapshot.None(0), 0,
                new[] { value }, Array.Empty<ReactionOpportunitySnapshot>(), Array.Empty<ActorLaneSnapshot>(),
                Array.Empty<IntentSnapshot>(), Array.Empty<MovementSegmentSnapshot>(), Array.Empty<ReservationSnapshot>(),
                Array.Empty<AiControllerSnapshot>(), null, new RngSnapshot(DeterministicRng.AlgorithmVersion, 1),
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, HistorySummary.Empty(), CommandSourcePriority.MappingVersion).ComputeHash();
            Assert.That(Hash(plan), Is.Not.EqualTo(Hash(plan with { ClashResolvedAtTick = -1 })));
            Assert.That(ReplayHeaderValidation.Validate(r.Sim.BuildReplayHeader() with { ReplayFormatVersion = 6 }, r.Definition),
                Is.EqualTo(ReplayCodes.REPLAY_FORMAT_VERSION_MISMATCH));
            Assert.That(ReplayFormat.Version, Is.EqualTo(7));
        }
    }
}
