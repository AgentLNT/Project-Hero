using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Logic.Tests
{
    public sealed class Task11DynamicSpawnTests
    {
        private static BattleDefinition Definition(bool inherit = true)
        {
            var d = Task08DefenseCoverageTests.Definition(health: 5, force: 0.000001f);
            var e = d.Encounters.Single();
            var template = e.Slots.Single(s => s.SlotId.Value == "b_mon").DefinitionId;
            e = e with {
                TurnSubmission = new TurnSubmissionDefinition(400, new EncounterSlotId("a_hero")),
                Controllers = e.Controllers.Select(c => c with { SourceKind = CommandSourceKind.Player }).ToArray(),
                DynamicSpawns = new[] { new DynamicUnitSpawnDefinition("spawn.first", 2,
                    new EncounterSlotId(inherit ? "b_mon" : "a_hero"), template,
                    new GridPoint(10, 0), GridDirection.East,
                    inherit ? DynamicSpawnFactionPolicy.InheritSource() : DynamicSpawnFactionPolicy.Fixed(Task09A2Fixture.MonsterFaction)) }
            };
            return Rehash(d with { Encounters = new[] { e } });
        }
        private static BattleDefinition Rehash(BattleDefinition d) => d with {
            BattleDefinitionHashValue = BattleDefinitionHash.Compute(d.RulesVersion, d.TicksPerSecond, 0,
                d.Rules, d.ConcurrentAction, d.ReactionRules, d.AdrenalineRules, d.FactionModel, d.DamageChannels,
                d.ImpactProfiles, d.Units, d.Actions, d.AttackPatterns, d.Volumes, d.MovementPatterns,
                d.ActionSets, d.StatusEffects, d.Encounters, d.DefaultDynamicSpawnPolicy)
        };
        private static BattleSimulation Create(BattleDefinition d, ReplayHeader h = null)
            => ProductionBattleComposition.Create(d, Task09A2Fixture.EncounterId, h?.RuntimeInputs ?? Task09A2Fixture.Inputs);
        private static void Attack(BattleSimulation s, long tick, UnitId target)
        {
            var request = new CommandRequest(tick, new ScheduleEditScope(s.ScheduleRevision, s.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new[] { new AddOrdinaryPlanOperation(1, Task09A2Fixture.Hero,
                    new ActionSpecId(Task09A2Fixture.AttackSpecId), tick, PrimaryTargetUnitId: target) }));
            Assert.That(s.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(request), Is.Null);
        }

        [TestCase(true)] [TestCase(false)]
        public void DynamicSpawnCreatesAuthoritativeUnitAndKeepsVictoryPendingUntilItDies(bool inherit)
        {
            var d = Definition(inherit);
            using var s = Create(d);
            var recorder = new ReplayRecorder(s);
            var all = new List<LogicEvent>();
            for (int tick = 0; tick < 45 && !s.IsEnded; tick++)
            {
                if (tick == 1) Attack(s, tick, Task09A2Fixture.Monster);
                if (tick == 15) Attack(s, tick, new UnitId(5));
                recorder.CaptureBeforeStep();
                var result = s.Step(tick, s.CommandIngress.FreezeTick(tick));
                recorder.RecordCommittedStep(result); all.AddRange(result.Events.EventsInSequenceOrder);
                if (tick == 2)
                {
                    var unit = result.Snapshot.Units.Single(u => u.UnitId == 5);
                    Assert.That(unit.FactionId, Is.EqualTo(Task09A2Fixture.MonsterFaction.Value));
                    Assert.That(unit.StateStartTick, Is.EqualTo(2));
                    Assert.That(s.FactionResolver.Classify(Task09A2Fixture.Hero, new UnitId(5)), Is.EqualTo(UnitRelation.Hostile));
                    Assert.That(s.LogicGrid.TryGetAnchor(new UnitId(5), out var anchor), Is.True);
                    Assert.That(anchor, Is.EqualTo(new GridPoint(10, 0)));
                    var owner = inherit ? Task09A2Fixture.AiId : Task09A2Fixture.PlayerId;
                    Assert.That(s.DecisionSnapshotFor(owner).ControlledUnitIds, Does.Contain(new UnitId(5)));
                    Assert.That(s.DecisionSnapshotFor(inherit ? Task09A2Fixture.PlayerId : Task09A2Fixture.AiId)
                        .ControlledUnitIds.Contains(new UnitId(5)), Is.False);
                    Assert.That(s.AdrenalineLedgerOf(new UnitId(5)), Is.Not.Null);
                }
                if (tick == 13) Assert.That(s.IsEnded, Is.False, "Created hostile must participate in elimination.");
            }
            var created = all.OfType<UnitCreatedEvent>().Single();
            Assert.That(created.UnitId, Is.EqualTo(new UnitId(5)));
            Assert.That(created.Tick, Is.EqualTo(2));
            Assert.That(all.OfType<UnitDiedEvent>().Any(e => e.UnitId == new UnitId(5)), Is.True);
            Assert.That(s.IsEnded, Is.True);
            Assert.That(s.BattleEnd.ResultCode, Is.EqualTo(d.Encounters.Single().Victory.VictoryResultCode));
            var replay = recorder.BuildReplay();
            string path = Path.Combine(Path.GetTempPath(), "hero-spawn-" + Guid.NewGuid().ToString("N") + ".heroReplay");
            try
            {
                using (var file = File.Create(path)) ReplayFile.Save(file, replay);
                BattleReplay loaded; using (var file = File.OpenRead(path)) loaded = ReplayFile.Load(file, d);
                using var player = new ReplayPlayer(d, h => Create(d, h));
                for (int i = 0; i < 100; i++)
                {
                    if (i == 0) player.Load(loaded); else player.Restart();
                    player.Play();
                    while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                    Assert.That(player.Deviation, Is.Null, "iteration=" + i);
                    Assert.That(player.IsComplete, Is.True);
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test] public void DynamicSpawnFailureConsumesNoUnitIdAndDoesNotRetryOnLaterTicks()
        {
            var d = Definition(); var e = d.Encounters.Single();
            e = e with { DynamicSpawns = new[] { e.DynamicSpawns[0] with { Position = Task09A2Fixture.MonsterAnchor } } };
            using var s = Create(Rehash(d with { Encounters = new[] { e } }));
            var all = new List<LogicEvent>();
            for (int tick = 0; tick < 5; tick++) all.AddRange(s.Step(tick, s.CommandIngress.FreezeTick(tick)).Events.EventsInSequenceOrder);
            Assert.That(all.OfType<UnitCreationRejectedEvent>().Count(), Is.EqualTo(1));
            Assert.That(all.OfType<UnitCreatedEvent>(), Is.Empty);
            Assert.That(s.CurrentSnapshot.NextUnitId, Is.EqualTo(5));
            Assert.That(s.CurrentSnapshot.Units.Count, Is.EqualTo(4));
        }

        [Test] public void DynamicSpawnDefinitionRejectsUnknownConflictingFactionAndSourceBeforeWorldCreation()
        {
            var d = Definition(); var e = d.Encounters.Single(); var spawn = e.DynamicSpawns[0];
            foreach (var invalid in new[] {
                spawn with { FactionPolicy = DynamicSpawnFactionPolicy.Fixed(new FactionId("faction.unknown")) },
                spawn with { FactionPolicy = new DynamicSpawnFactionPolicy(true, Task09A2Fixture.HeroFaction) },
                spawn with { FactionPolicy = null }, spawn with { Tick = -1 },
                spawn with { SourceSlotId = new EncounterSlotId("missing") },
                spawn with { DefinitionId = new UnitDefinitionId("missing") },
                spawn with { Facing = (GridDirection)12 }, spawn with { Position = new GridPoint(500, 0) }
            })
                Assert.Throws<LogicDefinitionException>(() => BattleInitializer.BuildInitialState(
                    d with { Encounters = new[] { e with { DynamicSpawns = new[] { invalid } } } },
                    Task09A2Fixture.EncounterId, Task09A2Fixture.Inputs));
        }

        [Test] public void DynamicSpawnCanonicalOrderAndDefinitionHashIgnoreInputEnumeration()
        {
            var d = Definition(); var e = d.Encounters.Single();
            var spawns = new[] { e.DynamicSpawns[0], e.DynamicSpawns[0] with { SpawnId = "spawn.second", Position = new GridPoint(-30, 20) } };
            var a = Rehash(d with { Encounters = new[] { e with { DynamicSpawns = spawns } } });
            var b = Rehash(d with { Encounters = new[] { e with { DynamicSpawns = spawns.Reverse().ToArray() } } });
            Assert.That(a.BattleDefinitionHashValue, Is.EqualTo(b.BattleDefinitionHashValue));
            using var sa = Create(a); using var sb = Create(b);
            for (int tick = 0; tick < 5; tick++)
            {
                var ra = sa.Step(tick, sa.CommandIngress.FreezeTick(tick)); var rb = sb.Step(tick, sb.CommandIngress.FreezeTick(tick));
                Assert.That(ra.Snapshot.ComputeHashHex(), Is.EqualTo(rb.Snapshot.ComputeHashHex()));
                Assert.That(ra.Events.EventsInSequenceOrder.Select(ReplayEventComparison.Canonical),
                    Is.EqualTo(rb.Events.EventsInSequenceOrder.Select(ReplayEventComparison.Canonical)));
            }
            Assert.That(sa.CurrentSnapshot.Units.Count, Is.EqualTo(6));
        }

        [Test] public void DynamicSpawnReceivesProductionWindowAndAcceptsOnlyItsControllersCommands()
        {
            var d = Definition(false); using var s = Create(d);
            for (int tick = 0; tick < 3; tick++) s.Step(tick, s.CommandIngress.FreezeTick(tick));
            var close = new CommandRequest(3, new WindowCommandScope(s.CurrentTurnWindow.WindowId),
                new WindowCommandPayload(WindowCommandKind.CloseOwnWindow));
            Assert.That(s.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(close), Is.Null);
            s.Step(3, s.CommandIngress.FreezeTick(3)); s.Step(4, s.CommandIngress.FreezeTick(4));
            Assert.That(s.CurrentTurnWindow.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Monster));
            Assert.That(s.CommandIngress.FindEntry(Task09A2Fixture.AiId).Submit(new CommandRequest(5,
                new WindowCommandScope(s.CurrentTurnWindow.WindowId), new WindowCommandPayload(WindowCommandKind.CloseOwnWindow))), Is.Null);
            s.Step(5, s.CommandIngress.FreezeTick(5)); s.Step(6, s.CommandIngress.FreezeTick(6));
            Assert.That(s.CurrentTurnWindow.OwnerUnitId, Is.EqualTo(new UnitId(5)));
            Assert.That(s.CurrentSnapshot.Units.Single(u => u.UnitId == 5).AdrenalineCycleId, Is.EqualTo(1));
            var request = new CommandRequest(7, new ScheduleEditScope(s.ScheduleRevision, s.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new[] { new AddOrdinaryPlanOperation(1, new UnitId(5), Task08DefenseCoverageTests.GuardId, 7) }));
            Assert.That(s.CommandIngress.FindEntry(Task09A2Fixture.AiId).Submit(request), Is.Null);
            Assert.That(s.CommandIngress.FindEntry(Task09A2Fixture.PlayerId).Submit(request), Is.Null);
            var result = s.Step(7, s.CommandIngress.FreezeTick(7));
            Assert.That(result.Events.EventsInSequenceOrder.OfType<CommandRejectedEvent>()
                .Any(f => f.ReasonCode == CommandCodes.COMMAND_ISSUER_CANNOT_CONTROL_UNIT), Is.True);
            Assert.That(s.ScheduleAuthority.Registry.ActivePlans.Count(p => p.OwnerUnitId == new UnitId(5)), Is.EqualTo(1));
            Assert.That(s.CurrentSnapshot.Units.Single(u => u.UnitId == 5).FactionId,
                Is.EqualTo(Task09A2Fixture.MonsterFaction.Value), "Player control must not turn a fixed hostile faction into an ally.");
        }

        [Test] public void DynamicSpawnCannotReviveDeadSourceAndKeepsOtherFactionAlive()
        {
            var d = Task09A2Fixture.BuildDefinition(withFragileHostile: true);
            d = d with { Units = d.Units.Select(u => u with { InitialHealth = 5 }).ToArray() };
            var e = d.Encounters.Single(); var source = e.Slots.Single(s => s.SlotId.Value == "b_mon");
            e = e with { TurnSubmission = new TurnSubmissionDefinition(400, new EncounterSlotId("a_hero")),
                Controllers = e.Controllers.Select(c => c with { SourceKind = CommandSourceKind.Player }).ToArray(),
                DynamicSpawns = new[] { new DynamicUnitSpawnDefinition("spawn.dead_source", 15, source.SlotId,
                    source.DefinitionId, new GridPoint(30, 20), GridDirection.East, DynamicSpawnFactionPolicy.InheritSource()) } };
            d = Rehash(d with { Encounters = new[] { e } });
            using var s = Create(d); var facts = new List<LogicEvent>();
            for (int tick = 0; tick < 17; tick++)
            {
                if (tick == 1) Attack(s, tick, Task09A2Fixture.Monster);
                facts.AddRange(s.Step(tick, s.CommandIngress.FreezeTick(tick)).Events.EventsInSequenceOrder);
            }
            Assert.That(facts.OfType<UnitDiedEvent>().Any(f => f.UnitId == Task09A2Fixture.Monster), Is.True);
            Assert.That(s.IsEnded, Is.False);
            Assert.That(facts.OfType<UnitCreationRejectedEvent>().Single().RejectionCode, Is.EqualTo("DYNAMIC_SPAWN_SOURCE_UNAVAILABLE"));
            Assert.That(facts.OfType<UnitCreatedEvent>(), Is.Empty);
            Assert.That(s.CurrentSnapshot.NextUnitId, Is.EqualTo(6));
        }
    }
}
