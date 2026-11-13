using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Authoring.Tests
{
    public sealed class AiDefenseEvaluationTests
    {
        sealed class Rig : IDisposable
        {
            public readonly BattleSimulation Sim;
            public readonly AiDecisionContext Context;
            public readonly ProductionBattleComposition Composition;
            public readonly List<(ControllerId controller, CommandRequest request)> Prefix = new();
            static readonly Dictionary<(long, bool), List<(ControllerId, CommandRequest)>> Prefixes = new();
            public Rig(bool defenses, bool mirror = false, int lookahead = 360, long checkpoint = 0, bool near = true)
            {
                var d = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, AiPolicyKind.SustainedOffense,
                    AiExecutionKind.UtilityBehaviorTree, near, mirror, new AiForecastSettings(lookahead, defenses));
                var runtime = new BattleRuntimeInputs(20260922, 10);
                var composition = new ProductionBattleComposition(d, d.Encounters[0].EncounterId, runtime);
                Composition = composition;
                Sim = BattleSimulation.Create(d, d.Encounters[0].EncounterId, runtime,
                    new BattleSimulationAssembly(turnWindowSchedule: composition));
                composition.Attach(Sim);
                if (checkpoint > 0)
                {
                    Assert.That(near, Is.True, "Recorded prefix fixtures cover the near layout.");
                    if (!Prefixes.TryGetValue((checkpoint, mirror), out var cached))
                    {
                        cached = new List<(ControllerId, CommandRequest)>();
                        var original = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, AiPolicyKind.SustainedOffense,
                            AiExecutionKind.UtilityBehaviorTree, true, mirror, new AiForecastSettings(360));
                        using var source = ProductionBattleComposition.Create(original, original.Encounters[0].EncounterId, runtime);
                        for (long tick = 0; tick <= checkpoint; tick++)
                        {
                            source.Step(tick, source.CommandIngress.FreezeTick(tick));
                            cached.AddRange(source.LastCommandSet.Envelopes.Select(e => (e.ControllerId, e.Request)));
                        }
                        Prefixes.Add((checkpoint, mirror), cached);
                    }
                    Prefix.AddRange(cached);
                }
                for (long tick = 0; tick <= checkpoint; tick++)
                {
                    foreach (var command in Prefix.Where(c => c.request.TargetTick == tick))
                        Assert.That(Sim.CommandIngress.FindEntry(command.controller).Submit(command.request), Is.Null);
                    Sim.Step(tick, Sim.CommandIngress.FreezeTick(tick));
                }
                Context = composition.AiControllers.Select(ai =>
                {
                    var id = new ControllerId(ai.RegisteredControllerIds[0]);
                    return ai.BuildContext(Sim.DecisionSnapshotFor(id), id);
                }).Single(c => c.HasSubmissionWindow);
            }
            public void Dispose() => Sim.Dispose();
            public AiTacticalCandidate Attack(string name) => AiTacticalCandidateBuilder.Ordinary(Context)
                .Single(c => c.Type == ActionType.Attack && c.Key.Contains(name));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LegalGuardChangesHeavyVersusThrustChoice(bool mirror)
        {
            using var old = new Rig(false, mirror);
            using var revised = new Rig(true, mirror);
            var before = revised.Sim.CurrentSnapshot.ComputeHash();
            var original = AiTacticalPolicy.Decide(old.Context, 20260922, AiPolicyKind.SustainedOffense);
            var decision = AiTacticalPolicy.Decide(revised.Context, 20260922, AiPolicyKind.SustainedOffense);
            Assert.That(original.Evaluations.Single(e => e.Chosen).Key, Does.Contain("heavy_smash"));
            Assert.That(decision.Evaluations.Single(e => e.Chosen).Key, Does.Contain("spear_thrust"));
            Assert.That(decision.Evaluations.Single(e => e.Key.Contains("heavy_smash")).DamageDeltaQ10, Is.EqualTo(20 * 1024));
            Assert.That(decision.Evaluations.Single(e => e.Key.Contains("spear_thrust")).DamageDeltaQ10, Is.EqualTo(25 * 1024));
            Assert.That(revised.Sim.CurrentSnapshot.ComputeHash(), Is.EqualTo(before), "Forecasts must not mutate the source world or ingress.");
        }

        [Test]
        public void DefenseSettingHasIndependentHashAndKeepsFormalContent()
        {
            using var old = new Rig(false);
            using var revised = new Rig(true);
            Assert.That(revised.Sim.Definition.BattleDefinitionHashValue, Is.Not.EqualTo(old.Sim.Definition.BattleDefinitionHashValue));
            Assert.That(revised.Sim.Definition.Actions, Is.EqualTo(old.Sim.Definition.Actions));
            Assert.That(revised.Sim.Definition.Units, Is.EqualTo(old.Sim.Definition.Units));
            Assert.That(revised.Sim.Definition.AdrenalineRules, Is.EqualTo(old.Sim.Definition.AdrenalineRules));
        }

        [TestCase(0)]
        [TestCase(240)]
        [TestCase(426)]
        public void DefenseRepliesMatchRealCommandsAndResourceAdmission(long checkpoint)
        {
            using var rig = new Rig(true, checkpoint: checkpoint);
            var attack = rig.Attack("heavy_smash");
            using var forecast = rig.Context.ForecastFactory(rig.Context.Snapshot);
            var before = rig.Sim.CurrentSnapshot.ComputeHash();
            var ingress = rig.Sim.CommandIngress.CaptureSnapshot();
            var replies = ((IAiDefenseForecast)forecast).EvaluateDefenses(attack.Request, rig.Context.ForecastSettings);
            Assert.That(rig.Sim.CurrentSnapshot.ComputeHash(), Is.EqualTo(before));
            Assert.That(rig.Sim.CommandIngress.CaptureSnapshot().Entries, Is.EqualTo(ingress.Entries));
            var accepted = replies.Where(r => r.Outcome.RejectionCode == null).ToArray();
            if (checkpoint == 0)
            {
                Assert.That(accepted.Any(r => r.Request.Payload is ScheduleEditPayload), Is.True);
                Assert.That(accepted.Any(r => r.Request.Payload is ReactionCommandPayload), Is.False);
                Assert.That(replies.Any(r => r.Request.Payload is ReactionCommandPayload && r.Outcome.RejectionCode != null), Is.True);
            }
            if (checkpoint == 426)
                foreach (var kind in new[] { ReactionCommandKind.Block, ReactionCommandKind.Dodge })
                    Assert.That(accepted.Any(r => r.Request.Payload is ReactionCommandPayload p && p.ReactionKind == kind), Is.True, kind.ToString());
            foreach (var reply in accepted)
            {
                using var actual = new Rig(true, checkpoint: checkpoint);
                Assert.That(actual.Sim.CommandIngress.FindEntry(actual.Context.ControllerId).Submit(attack.Request), Is.Null);
                long attackId = 0;
                while (!actual.Sim.IsEnded && actual.Sim.Tick < reply.Outcome.HorizonTick)
                {
                    long tick = actual.Sim.Tick + 1;
                    var result = actual.Sim.Step(tick, actual.Sim.CommandIngress.FreezeTick(tick));
                    Assert.That(result.Events.Events.OfType<CommandRejectedEvent>(), Is.Empty, reply.Key);
                    if (tick == attack.Request.TargetTick) attackId = result.Events.Events.OfType<ActionPlanCreatedEvent>().Single().ActionPlanId;
                    if (tick != reply.SubmittedAfterTick) continue;
                    Assert.That(actual.Sim.DecisionSnapshotFor(reply.Defender).VisiblePlans.Any(p => p.ActionPlanId == attackId
                        && DecisionSnapshot.IsPubliclyRevealed(p)), Is.True, "Never respond to an unpublished attack.");
                    var request = reply.Request;
                    if (request.Payload is ReactionCommandPayload)
                    {
                        var opportunities = actual.Sim.DecisionSnapshotFor(reply.Defender).ControlledUnitIds
                            .SelectMany(id => actual.Sim.AiReactionOpportunities.PublishedOpportunitiesFor(id, tick))
                            .Single(o => o.SourceAttackPlanId.Value == attackId);
                        request = request with { Scope = new ReactionCommandScope(opportunities.ReactionOpportunityId) };
                    }
                    Assert.That(actual.Sim.CommandIngress.FindEntry(reply.Defender).Submit(request), Is.Null);
                }
                Assert.That(reply.Outcome.Coverage, Is.EqualTo(AiForecastCoverage.Complete), reply.Key);
                Assert.That(reply.Outcome.Units, Is.EqualTo(actual.Sim.CurrentSnapshot.Units), reply.Key);
            }
        }

        [Test]
        public void UncoveredAttackCannotAcquireAComparableDefenseScore()
        {
            using var rig = new Rig(true, lookahead: 180);
            var decision = AiTacticalPolicy.Decide(rig.Context, 20260922, AiPolicyKind.SustainedOffense);
            var heavy = decision.Evaluations.Single(e => e.Key.Contains("heavy_smash"));
            Assert.That(heavy.Score, Is.Null); Assert.That(heavy.DefenseResponses, Is.Empty);
            Assert.That(heavy.Coverage, Is.EqualTo(AiForecastCoverage.Uncovered));
        }

        sealed class ForecastWithoutDefense : IAiForecast
        {
            readonly IAiForecast _inner;
            public ForecastWithoutDefense(IAiForecast inner) => _inner = inner;
            public AiActionForecast Evaluate(CommandRequest request, AiForecastSettings settings = null) => _inner.Evaluate(request, settings);
            public void Dispose() => _inner.Dispose();
        }
        [Test]
        public void MissingDefenseCapabilityCannotSilentlyUseUnopposedAttackScore()
        {
            using var rig = new Rig(true);
            var context = rig.Context with { ForecastFactory = s => new ForecastWithoutDefense(rig.Context.ForecastFactory(s)) };
            var decision = AiTacticalPolicy.Decide(context, 20260922, AiPolicyKind.SustainedOffense);
            Assert.That(decision.Evaluations.Where(e => e.Type == ActionType.Attack).All(e => !e.Chosen &&
                (e.Score == null && e.Reason == "DEFENSE_FORECAST_UNAVAILABLE" || e.Score <= 0)), Is.True);
        }

        sealed class IncompleteDefenseForecast : IAiDefenseForecast
        {
            readonly IAiForecast _inner;
            readonly bool _mismatchedHorizon;
            public IncompleteDefenseForecast(IAiForecast inner, bool mismatchedHorizon)
            { _inner = inner; _mismatchedHorizon = mismatchedHorizon; }
            public AiActionForecast Evaluate(CommandRequest request, AiForecastSettings settings = null) => _inner.Evaluate(request, settings);
            public IReadOnlyList<AiDefenseForecast> EvaluateDefenses(CommandRequest request, AiForecastSettings settings)
            {
                var outcome = _inner.Evaluate(request, settings);
                outcome = _mismatchedHorizon ? outcome with { HorizonTick = outcome.HorizonTick + 1 }
                    : outcome with { Coverage = AiForecastCoverage.Uncovered, RequiredThroughTick = outcome.HorizonTick + 1 };
                return new[] { new AiDefenseForecast("contract-probe", new ControllerId("opponent"), request.TargetTick, request, outcome) };
            }
            public void Dispose() => _inner.Dispose();
        }
        [TestCase(false)]
        [TestCase(true)]
        public void ReplyNeedsCompleteCoverageAndTheSameComparisonEndpoint(bool mismatchedHorizon)
        {
            using var rig = new Rig(true);
            var context = rig.Context with { ForecastFactory = s => new IncompleteDefenseForecast(rig.Context.ForecastFactory(s), mismatchedHorizon) };
            if (mismatchedHorizon)
                Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() => AiTacticalPolicy.Decide(context, 20260922, AiPolicyKind.SustainedOffense));
            else
            {
                var decision = AiTacticalPolicy.Decide(context, 20260922, AiPolicyKind.SustainedOffense);
                Assert.That(decision.Evaluations.Where(e => e.Type == ActionType.Attack && e.NoDefenseScore > 0).All(e => e.Score == null && !e.Chosen
                    && e.Reason == "DEFENSE_OUTCOME_NOT_COVERED" && e.RequiredThroughTick == 361), Is.True);
            }
        }

        [Test]
        public void ForecastIgnoresPendingInputWithoutConsumingIt()
        {
            using var rig = new Rig(true);
            var first = AiTacticalPolicy.Decide(rig.Context, 20260922, AiPolicyKind.SustainedOffense);
            Assert.That(rig.Sim.CommandIngress.FindEntry(rig.Context.ControllerId).Submit(rig.Attack("heavy_smash").Request with { TargetTick = 10 }), Is.Null);
            var before = rig.Sim.CommandIngress.CaptureSnapshot();
            var second = AiTacticalPolicy.Decide(rig.Context, 20260922, AiPolicyKind.SustainedOffense);
            var after = rig.Sim.CommandIngress.CaptureSnapshot();
            Assert.That(second.Evaluations.Select(e => (e.Key, e.Score, e.Chosen, e.WorstDefenseKey)),
                Is.EqualTo(first.Evaluations.Select(e => (e.Key, e.Score, e.Chosen, e.WorstDefenseKey))));
            Assert.That(after.Entries, Is.EqualTo(before.Entries));
            Assert.That(after.FutureBuckets.Select(b => (b.TargetTick, b.PendingCount, string.Join("|", b.CanonicalRequests))),
                Is.EqualTo(before.FutureBuckets.Select(b => (b.TargetTick, b.PendingCount, string.Join("|", b.CanonicalRequests)))));
            Assert.That(after.FrozenThroughTick, Is.EqualTo(before.FrozenThroughTick));
        }

        [Test]
        public void NonpositiveUnopposedUpperBoundCannotSelectAttackOrRequireDefenseExpansion()
        {
            using var rig = new Rig(true, near: false);
            var decision = AiTacticalPolicy.Decide(rig.Context, 20260922, AiPolicyKind.SustainedOffense);
            var attacks = decision.Evaluations.Where(e => e.Type == ActionType.Attack).ToArray();
            Assert.That(attacks, Is.Not.Empty);
            Assert.That(attacks.All(e => e.NoDefenseScore <= 0 && e.Score == e.NoDefenseScore && !e.Chosen && e.DefenseResponses.Count == 0), Is.True);
        }

        [Test]
        public void KnownDamageIncomeReopensDefenseWithoutAStateOrWindowChange()
        {
            var source = BattleDefinitionFixture.Definition;
            var encounter = source.Encounters[0];
            var enemy = encounter.Slots.Single(s => s.SlotId.Value != "hero");
            var hero = encounter.Slots.Single(s => s.SlotId.Value == "hero");
            var ally = enemy with { SlotId = new EncounterSlotId("zz_ally"), InitialPosition = new GridPoint(-12, 0), InitialFacing = GridDirection.East };
            encounter = encounter with { GridBoundary = new GridBoundaryDefinition(new GridPoint(-32, -16), new GridPoint(32, 16)),
                Slots = encounter.Slots.Select(s => s with { InitialPosition = new GridPoint(s.SlotId == hero.SlotId ? -4 : 4, 0) })
                    .Concat(new[] { ally }).ToArray(),
                Controllers = encounter.Controllers.Select(c => c.ControlledSlots.Contains(enemy.SlotId)
                    ? c with { ControlledSlots = c.ControlledSlots.Concat(new[] { ally.SlotId }).ToArray() } : c).ToArray() };
            source = source with { Encounters = new[] { encounter },
                Units = source.Units.Select(u => u.UnitDefinitionId == hero.DefinitionId
                    ? u with { Mass = 10000, MomentumSpeed = 1 } : u with { ActionSpeed = source.Rules.ReferenceActionSpeed }).ToArray(),
                Actions = source.Actions.Select(a => a.ActionSpecId.Value.Contains("heavy_smash")
                    ? a with { Timing = new AttackTimingSpec(90, 30) } : a).ToArray() };
            var d = FormalAiBaseline.Build(source, AiPolicyKind.SustainedOffense, AiExecutionKind.UtilityBehaviorTree,
                false, false, new AiForecastSettings(360, true));
            var inputs = new BattleRuntimeInputs(20260922, 10);
            var initial = BattleInitializer.BuildInitialState(d, encounter.EncounterId, inputs);
            var attacker = initial.SlotToUnitId[enemy.SlotId]; var helper = initial.SlotToUnitId[ally.SlotId];
            var defender = initial.SlotToUnitId[hero.SlotId];
            var controller = d.Encounters[0].Controllers.Single(c => c.ControlledSlots.Contains(enemy.SlotId)).ControllerId;
            var composition = new ProductionBattleComposition(d, encounter.EncounterId, inputs);
            using var sim = BattleSimulation.Create(d, encounter.EncounterId, inputs, new BattleSimulationAssembly());
            composition.Attach(sim); sim.WindowManager.ScheduleWindow(0, helper, 60); sim.WindowManager.ScheduleWindow(61, attacker, 180);
            sim.Step(0, sim.CommandIngress.FreezeTick(0));
            var action = d.Actions.Single(a => a.ActionSpecId.Value.Contains("heavy_smash.radius_2")).ActionSpecId;
            Assert.That(sim.CommandIngress.FindEntry(controller).Submit(new CommandRequest(1,
                new ScheduleEditScope(sim.ScheduleRevision, sim.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, helper, action,
                    PrimaryTargetUnitId: defender, Facing: GridDirection.East) }))), Is.Null);
            for (long tick = 1; tick <= 61; tick++) sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
            var context = composition.AiControllers.Single(a => a.IsRegistered(controller)).BuildContext(sim.DecisionSnapshotFor(controller), controller);
            Assert.That(sim.AdrenalineLedgerOf(defender).AvailableAdrenaline, Is.Zero);
            var request = new CommandRequest(62, new ScheduleEditScope(sim.ScheduleRevision, sim.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, attacker, action,
                    PrimaryTargetUnitId: defender, Facing: GridDirection.West) }));
            using var forecast = context.ForecastFactory(context.Snapshot);
            var replies = ((IAiDefenseForecast)forecast).EvaluateDefenses(request, context.ForecastSettings);
            bool Block(AiDefenseForecast r) => r.Request.Payload is ReactionCommandPayload p && p.ReactionKind == ReactionCommandKind.Block;
            Assert.That(replies.Any(r => Block(r) && r.SubmittedAfterTick < 91 && r.Outcome.RejectionCode != null), Is.True);
            Assert.That(replies.Any(r => Block(r) && r.SubmittedAfterTick >= 91 && r.Outcome.RejectionCode == null), Is.True,
                "Known damage can fund a later reply even while the defender remains idle in the same window context.");
            var before = sim.CurrentSnapshot.Units.Single(u => u.UnitId == defender.Value);
            for (long tick = 62; tick <= 93; tick++) sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
            var after = sim.CurrentSnapshot.Units.Single(u => u.UnitId == defender.Value);
            Assert.That(after.State, Is.EqualTo(before.State)); Assert.That((after.X, after.Y), Is.EqualTo((before.X, before.Y)));
            Assert.That(sim.AdrenalineLedgerOf(defender).AvailableAdrenaline, Is.GreaterThanOrEqualTo(2));
        }

        [TestCase(AiPolicyKind.SustainedOffense)]
        [TestCase(AiPolicyKind.SeekInterrupt)]
        [TestCase(AiPolicyKind.CautiousDefense)]
        public void RevisedHybridSerializesAndReplaysAndRejectsOldDefinition(AiPolicyKind policy)
        {
            var d = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, policy, AiExecutionKind.UtilityBehaviorTree,
                true, false, new AiForecastSettings(360, true));
            using var sim = ProductionBattleComposition.Create(d, d.Encounters[0].EncounterId, new BattleRuntimeInputs(20260922, 10));
            var recorder = new ReplayRecorder(sim);
            for (long tick = 0; tick < 480 && !sim.IsEnded; tick++)
            {
                recorder.CaptureBeforeStep(); var result = sim.Step(tick, sim.CommandIngress.FreezeTick(tick)); recorder.RecordCommittedStep(result);
                Assert.That(result.Events.Events.OfType<CommandRejectedEvent>(), Is.Empty);
            }
            using var disk = new MemoryStream(); ReplayFile.Save(disk, recorder.BuildReplay()); disk.Position = 0;
            var replay = ReplayFile.Load(disk, d); Assert.That(replay.Submissions, Is.Empty);
            using var player = new ReplayPlayer(d); player.Load(replay); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            Assert.That(player.Deviation, Is.Null);
            var old = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, policy, AiExecutionKind.UtilityBehaviorTree,
                true, false, new AiForecastSettings(360));
            using var wrong = new ReplayPlayer(old);
            Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() => wrong.Load(replay));
        }
    }
}
