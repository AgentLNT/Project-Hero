using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    public sealed class Task11FinalMatrixReplayTests
    {
        // Fixture windows are deterministic authoring input. All scripted commands
        // use Player registrations, so saved replay contains every authoritative input.
        private sealed class Windows : ITurnWindowSchedule
        {
            public WindowOpenRequest TryOpenDue(long tick) => tick == 0 || tick == 14
                ? new WindowOpenRequest(Task09A2Fixture.Hero, 400)
                : tick == 3 ? new WindowOpenRequest(Task09A2Fixture.Monster, 400)
                : tick == 6 ? new WindowOpenRequest(Task09A2Fixture.Ally, 400) : null;
            public bool ShouldCloseCurrentWindow(long tick) => tick == 2 || tick == 5 || tick == 8;
        }
        private static BattleDefinition Definition(string kind)
        {
            var d = kind == "Clash3" || kind == "Flank" ? Task08MultiPartyCoverageTests.MultiDefinition(kind == "Clash3")
                : Task08DefenseCoverageTests.Definition(area: kind == "AoeDodge" || kind == "FriendlyAoe" || kind == "FacingAoe", partialBlock: kind == "PartialBlock",
                    force: kind == "AutoDeferral" || kind == "DisplacementDeath" || kind == "Reservation" ? 4f : 0.000001f,
                    health: kind == "DisplacementDeath" ? 5 : 2000);
            d = d with { Encounters = d.Encounters.Select(e => e with {
                Controllers = new[] {
                    new ControllerBinding(Task09A2Fixture.PlayerId, CommandSourceKind.Player,
                        e.Slots.Where(s => s.SlotId.Value != "b_mon").Select(s => s.SlotId).ToArray()),
                    new ControllerBinding(Task09A2Fixture.AiId, CommandSourceKind.Player, new[] { new EncounterSlotId("b_mon") }) }
            }).ToArray() };
            if (kind == "Reservation") d = d with { Encounters = d.Encounters.Select(e => e with {
                Slots = e.Slots.Select(s => s.SlotId.Value == "c_neut" ? s with { InitialPosition = new GridPoint(30, 0) } : s).ToArray() }).ToArray() };
            if (kind == "FriendlyAoe") d = d with { Actions = d.Actions.Select(a => a.Type == ActionType.Attack
                ? a with { Payload = ((AttackPayloadSpec)a.Payload) with { Pattern = Task09A2Fixture.AttackPattern,
                    AllowedTargetRelations = TargetRelationMask.Allied | TargetRelationMask.Neutral | TargetRelationMask.Hostile } } : a).ToArray() };
            if (kind == "MixedDodge")
            {
                var retained = new AttackPatternSpec(new AttackPatternId("attack.pattern.task11.undodgeable"),
                    Enumerable.Range(0, 12).Select(f => new DirectionalTriangleSet((GridDirection)f, new[] { new TrianglePoint(-13, 0, 1) })).ToArray());
                d = d with { Actions = d.Actions.Select(a => a.ActionSpecId.Value == Task09A2Fixture.AlliedAttackSpecId
                    ? a with { Payload = ((AttackPayloadSpec)a.Payload) with { Pattern = retained,
                        Tags = AttackTagMask.Reactable | AttackTagMask.Blockable } } : a).ToArray() };
            }
            if (kind == "FacingAoe")
            {
                var original = ((AttackPayloadSpec)d.FindAction(new ActionSpecId(Task09A2Fixture.AttackSpecId)).Payload).Pattern;
                var facingPattern = new AttackPatternSpec(new AttackPatternId("attack.pattern.task11.plan_facing"),
                    Enumerable.Range(0, 12).Select(f => new DirectionalTriangleSet((GridDirection)f,
                        f == (int)GridDirection.West ? original.Directions.Single(p => p.Direction == GridDirection.East).Triangles
                            : new[] { new TrianglePoint(101, 0, 1) })).ToArray());
                d = d with { Actions = d.Actions.Select(a => a.Type == ActionType.Attack
                    ? a with { Payload = ((AttackPayloadSpec)a.Payload) with { Pattern = facingPattern } } : a).ToArray() };
            }
            return d with { BattleDefinitionHashValue = BattleDefinitionHash.Compute(d.RulesVersion, d.TicksPerSecond, 0,
                d.Rules, d.ConcurrentAction, d.ReactionRules, d.AdrenalineRules, d.FactionModel, d.DamageChannels,
                d.ImpactProfiles, d.Units, d.Actions, d.AttackPatterns, d.Volumes, d.MovementPatterns, d.ActionSets,
                d.StatusEffects, d.Encounters, d.DefaultDynamicSpawnPolicy) };
        }
        private static BattleSimulation Create(BattleDefinition d, ReplayHeader header = null)
            => BattleSimulation.Create(d, Task09A2Fixture.EncounterId, header?.RuntimeInputs ?? Task09A2Fixture.Inputs,
                new BattleSimulationAssembly(turnWindowSchedule: new Windows()));
        private static void Add(BattleSimulation sim, UnitId owner, long targetTick, params ScheduleEditOperation[] operations)
        {
            var controller = owner == Task09A2Fixture.Monster ? Task09A2Fixture.AiId : Task09A2Fixture.PlayerId;
            Assert.That(sim.CommandIngress.FindEntry(controller).Submit(new CommandRequest(targetTick,
                new ScheduleEditScope(sim.ScheduleRevision, sim.CurrentTurnWindow.WindowId), new ScheduleEditPayload(operations))), Is.Null);
        }
        private static AddOrdinaryPlanOperation Attack(UnitId owner, UnitId target, long start, GridDirection facing = GridDirection.East)
            => new AddOrdinaryPlanOperation(1, owner, new ActionSpecId(Task09A2Fixture.AttackSpecId), start,
                PrimaryTargetUnitId: target, Facing: facing);
        private static BattleReplay Record(string kind, BattleDefinition d)
        {
            using var sim = Create(d); var recorder = new ReplayRecorder(sim); var facts = new List<LogicEvent>();
            for (int tick = 0; tick < 210 && !sim.IsEnded; tick++)
            {
                if (tick == 1) Add(sim, Task09A2Fixture.Hero, tick, Attack(Task09A2Fixture.Hero, Task09A2Fixture.Monster,
                    kind == "Clash3" || kind == "Flank" ? 10 : 1,
                    kind == "FacingAoe" ? GridDirection.West : GridDirection.East));
                if (tick == 4)
                {
                    if (kind == "Clash3") Add(sim, Task09A2Fixture.Monster, tick,
                        Attack(Task09A2Fixture.Monster, Task09A2Fixture.Hero, 10, GridDirection.West));
                    if (kind == "Guard" || kind == "AutoDeferral") Add(sim, Task09A2Fixture.Monster, tick,
                        new AddOrdinaryPlanOperation(1, Task09A2Fixture.Monster, Task08DefenseCoverageTests.GuardId,
                            kind == "Guard" ? 4 : 12));
                    if (kind == "DodgeChain") Add(sim, Task09A2Fixture.Monster, tick,
                        new AddOrdinaryPlanOperation(1, Task09A2Fixture.Monster, Task08DefenseCoverageTests.MoveId, 60, Destination: new GridPoint(6, 2)),
                        new AddOrdinaryPlanOperation(2, Task09A2Fixture.Monster, Task08DefenseCoverageTests.GuardId, 110),
                        new AddOrdinaryPlanOperation(3, Task09A2Fixture.Monster, Task08DefenseCoverageTests.MoveId, 150, Destination: new GridPoint(6, 4)));
                }
                if (tick == 7)
                {
                    if (kind == "Clash3" || kind == "Flank") Add(sim, Task09A2Fixture.Ally, tick,
                        Attack(Task09A2Fixture.Ally, Task09A2Fixture.Monster, 10, kind == "Clash3" ? (GridDirection)4 : GridDirection.West));
                    if (kind == "Reservation") Add(sim, Task09A2Fixture.Ally, tick,
                        new AddOrdinaryPlanOperation(1, Task09A2Fixture.Ally, Task08DefenseCoverageTests.MoveId, 60, Destination: new GridPoint(14, 0)));
                    if (kind == "MixedDodge") Add(sim, Task09A2Fixture.Ally, tick,
                        new AddOrdinaryPlanOperation(1, Task09A2Fixture.Ally, new ActionSpecId(Task09A2Fixture.AlliedAttackSpecId), 20,
                            PrimaryTargetUnitId: Task09A2Fixture.Monster, Facing: GridDirection.East));
                }
                if (tick == 15 && (kind == "Block" || kind == "PartialBlock" || kind.Contains("Dodge")))
                    Add(sim, Task09A2Fixture.Hero, tick, Attack(Task09A2Fixture.Hero, Task09A2Fixture.Monster, 20));
                if (tick == 22 && (kind == "Block" || kind == "PartialBlock" || kind.Contains("Dodge")))
                {
                    var opportunity = sim.ReactionOpportunities.ActiveOpportunities.Single(o => o.DefenderUnitId == Task09A2Fixture.Monster
                        && o.TriggerTick == 30 && o.State == ProjectHero.Logic.Combat.ReactionOpportunityState.Open
                        && (kind != "MixedDodge" || o.Options.Any(option => option.ReactionActionSpecId.Value == Task09A2Fixture.DodgeSpecId)));
                    bool dodge = kind.Contains("Dodge");
                    Assert.That(sim.AdrenalineLedgerOf(Task09A2Fixture.Monster).AvailableAdrenaline, Is.GreaterThanOrEqualTo(dodge ? 1 : 2), "Resource must come from the warm-up hit.");
                    Assert.That(sim.CommandIngress.FindEntry(Task09A2Fixture.AiId).Submit(Task09A2Fixture.Reaction(tick,
                        opportunity.Id, dodge ? ReactionCommandKind.Dodge : ReactionCommandKind.Block,
                        new ActionSpecId(dodge ? Task09A2Fixture.DodgeSpecId : Task09A2Fixture.BlockSpecId),
                        dodge ? new GridPoint(6, 2) : (GridPoint?)null)), Is.Null);
                }
                recorder.CaptureBeforeStep(); var result = sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                recorder.RecordCommittedStep(result); facts.AddRange(result.Events.Events);
                Assert.That(result.Events.Events.OfType<CommandRejectedEvent>(), Is.Empty, kind + " tick=" + tick);
            }
            Assert.That(facts.OfType<DamageChannelResolvedEvent>().Any() || facts.OfType<ClashParticipantResolvedEvent>().Any(), Is.True, "Non-empty real combat required.");
            if (kind == "Clash3") Assert.That(facts.OfType<ActionPlanTerminatedEvent>().Count(e => e.Reason == (int)ActionTerminationReason.InterruptedByClash), Is.EqualTo(3));
            if (kind == "Block" || kind == "PartialBlock") Assert.That(facts.OfType<BlockResolvedEvent>().Any(), Is.True);
            if (kind.Contains("Dodge")) Assert.That(facts.OfType<DodgeResolvedEvent>().Count(), Is.EqualTo(1));
            if (kind == "AoeDodge") Assert.That(facts.OfType<DamageChannelResolvedEvent>().Any(e => e.Tick == 30 && e.TargetUnitId != Task09A2Fixture.Monster), Is.True);
            if (kind == "DisplacementDeath") { Assert.That(facts.OfType<ForcedDisplacementResolvedEvent>().Any(e => e.AppliedSteps > 0), Is.True); Assert.That(facts.OfType<UnitDiedEvent>().Any(), Is.True); }
            if (kind == "AutoDeferral") Assert.That(facts.OfType<ActionPlanAutoDeferredEvent>().Any(), Is.True);
            if (kind == "DodgeChain") Assert.That(facts.OfType<ActionPlanTerminatedEvent>().Count(e => e.Reason == (int)ActionTerminationReason.MovementOriginInvalidatedByDodge), Is.EqualTo(2));
            if (kind == "Reservation") Assert.That(facts.OfType<ForcedDisplacementResolvedEvent>().Any(e => e.InvalidatedPlanIds.Count > 0), Is.True);
            if (kind == "MixedDodge")
            {
                var dodge = facts.OfType<DodgeResolvedEvent>().Single();
                Assert.That(dodge.InvalidatedAttackPlanIds.Count, Is.EqualTo(1));
                Assert.That(dodge.RetainedUndodgeablePlanIds.Count, Is.EqualTo(1));
                Assert.That(facts.OfType<DamageChannelResolvedEvent>().Any(e => e.Tick == 30 && e.TargetUnitId == Task09A2Fixture.Monster), Is.True);
            }
            if (kind == "FriendlyAoe") foreach (var target in new[] { Task09A2Fixture.Monster, Task09A2Fixture.Neutral, Task09A2Fixture.Ally })
                Assert.That(facts.OfType<DamageChannelResolvedEvent>().Select(e => e.TargetUnitId), Does.Contain(target));
            if (kind == "FacingAoe")
            {
                Assert.That(facts.OfType<ReactionOpportunityOpenedEvent>().Any(e => e.DefenderUnitId == Task09A2Fixture.Monster.Value), Is.True,
                    "Plan faces West while unit faces East: every actual area target must receive its telegraphed opportunity.");
                Assert.That(facts.OfType<DamageChannelResolvedEvent>().Any(e => e.TargetUnitId == Task09A2Fixture.Monster), Is.True);
            }
            if (sim.IsEnded)
            {
                var end = facts.OfType<BattleEndedEvent>().Single();
                Assert.That(end, Is.SameAs(facts.Last()), "The unique end event is the last committed event.");
            }
            return recorder.BuildReplay();
        }
        [TestCase("Block")] [TestCase("PartialBlock")] [TestCase("Dodge")] [TestCase("AoeDodge")]
        [TestCase("DodgeChain")] [TestCase("Guard")] [TestCase("Clash3")] [TestCase("Flank")]
        [TestCase("DisplacementDeath")] [TestCase("Reservation")] [TestCase("AutoDeferral")]
        [TestCase("MixedDodge")] [TestCase("FriendlyAoe")] [TestCase("FacingAoe")]
        public void FinalCombatClassFileReplaysOneHundredTimesFromTickZero(string kind)
        {
            var d = Definition(kind); var recorded = Record(kind, d);
            var path = Path.GetFullPath("优化任务/执行记录/11-final-replay-" + kind + ".heroReplay");
            using (var file = File.Create(path)) ReplayFile.Save(file, recorded);
            BattleReplay loaded; using (var file = File.OpenRead(path)) loaded = ReplayFile.Load(file, d);
            using var player = new ReplayPlayer(d, h => Create(d, h)); player.Load(loaded);
            for (int run = 0; run < 100; run++)
            {
                if (run > 0) player.Restart(); player.Play();
                while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                Assert.That(player.Deviation, Is.Null, kind + " run=" + run + " " + player.Deviation);
                Assert.That(player.IsComplete, Is.True);
                Assert.That(player.CurrentSnapshot.ComputeHash(), Is.EqualTo(recorded.Records.Last().SnapshotHash));
            }
            TestContext.WriteLine(kind + ": file restarts=100; ticks=" + loaded.Records.Count + "; events="
                + loaded.Records.Sum(r => r.Events.Count) + "; bytes=" + new FileInfo(path).Length + "; definition=" + d.BattleDefinitionHashValue);
        }
    }
}
