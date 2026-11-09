using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Tests
{
    public sealed class AiForecastIngressTests
    {
        sealed class Rig : IDisposable
        {
            public readonly BattleSimulation Sim;
            readonly ProductionBattleComposition _composition;
            public Rig(bool fastHeavy = false)
            {
                var d = Stage4RecoveryExperiment.Build(new Stage4Input(Stage4Layout.HeavyQuickWall, 31,
                    AiPolicyKind.CautiousDefense, AiPolicyKind.SustainedOffense));
                if (fastHeavy)
                {
                    d = d with { Actions = d.Actions.Select(a => a.ActionSpecId == CombatExperimentDefinition.Action("heavy")
                        ? a with { Timing = new AttackTimingSpec(30, 31) } : a).ToArray() };
                    d = d with { BattleDefinitionHashValue = CombatExperimentDefinition.Hash(d) };
                }
                var inputs = new BattleRuntimeInputs(31, 4);
                _composition = new ProductionBattleComposition(d, CombatExperimentDefinition.Encounter, inputs);
                // Keep the genuine production window schedule and ingress registrations, with no autonomous input.
                Sim = BattleSimulation.Create(d, CombatExperimentDefinition.Encounter, inputs,
                    new BattleSimulationAssembly(turnWindowSchedule: _composition));
                _composition.Attach(Sim); Advance(0);
            }
            public void AddHeavies(params long[] starts)
            {
                var operations = starts.Select((t, index) => (ScheduleEditOperation)new AddOrdinaryPlanOperation(index + 1,
                    new UnitId(1), CombatExperimentDefinition.Action("heavy"), t, PrimaryTargetUnitId: new UnitId(2), Facing: GridDirection.East)).ToArray();
                var request = new CommandRequest(Sim.Tick + 1, new ScheduleEditScope(Sim.ScheduleRevision, Sim.CurrentTurnWindow.WindowId), new ScheduleEditPayload(operations));
                Assert.That(Sim.CommandIngress.FindEntry(Stage4RecoveryExperiment.BluntController).Submit(request), Is.Null);
            }
            public CommandRequest Quick(long? start = null) => new CommandRequest(Sim.Tick + 1,
                new ScheduleEditScope(Sim.ScheduleRevision, Sim.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, new UnitId(2),
                    CombatExperimentDefinition.Action("quick"), start, PrimaryTargetUnitId: new UnitId(1), Facing: GridDirection.West) }));
            public AiActionForecast Forecast(CommandRequest request)
            {
                var snapshot = Sim.DecisionSnapshotFor(Stage4RecoveryExperiment.OpponentController);
                var ai = _composition.AiControllers.Single(c => c.IsRegistered(snapshot.ControllerId));
                using var forecast = ai.BuildContext(snapshot, snapshot.ControllerId).ForecastFactory(snapshot);
                return forecast.Evaluate(request);
            }
            public void Submit(CommandRequest request)
                => Assert.That(Sim.CommandIngress.FindEntry(Stage4RecoveryExperiment.OpponentController).Submit(request), Is.Null);
            public void Advance(long until)
            { while (Sim.Tick < until) { long tick = Sim.Tick + 1; Sim.Step(tick, Sim.CommandIngress.FreezeTick(tick)); } }
            public void Dispose() => Sim.Dispose();
        }

        [Test]
        public void KnownControlAndSameTickClaimMustNotProducePhantomAttackDamage()
        {
            using var r = new Rig(); r.AddHeavies(1, 127); r.Advance(222);
            Assert.That(r.Sim.FindUnitStateMachine(new UnitId(2)).StateEndTick, Is.EqualTo(247));
            int before = r.Sim.CurrentSnapshot.Units.Single(u => u.UnitId == 1).HealthQ10;
            var request = r.Quick(); var prediction = r.Forecast(request);
            Assert.That(prediction.Coverage, Is.EqualTo(AiForecastCoverage.Complete),
                "An observed early termination closes the result even when the originally scheduled end is later.");
            r.Submit(request); r.Advance(342);
            int actual = r.Sim.CurrentSnapshot.Units.Single(u => u.UnitId == 1).HealthQ10;
            Assert.That(actual, Is.EqualTo(before));
            Assert.That(prediction.Units.Single(u => u.UnitId == 1).HealthQ10, Is.EqualTo(actual),
                "The forecast must retain real command-batch claims, not invent an auto-deferred hit");
        }

        [Test]
        public void FutureUnclaimedStartCanStillDeferToTheKnownControlBoundary()
        {
            using var r = new Rig(); r.AddHeavies(1, 127); r.Advance(222);
            int before = r.Sim.CurrentSnapshot.Units.Single(u => u.UnitId == 1).HealthQ10;
            var request = r.Quick(230); var prediction = r.Forecast(request);
            r.Submit(request); r.Advance(342);
            int actual = r.Sim.CurrentSnapshot.Units.Single(u => u.UnitId == 1).HealthQ10;
            Assert.That(actual, Is.EqualTo(before - 4096));
            Assert.That(prediction.Units.Single(u => u.UnitId == 1).HealthQ10, Is.EqualTo(actual));
            Assert.That(prediction.PlanStartTick, Is.EqualTo(247));
            Assert.That(prediction.PlanImpactTick, Is.EqualTo(277));
        }

        [Test]
        public void NextTickWindowResetMustHappenBeforeReactionResourceAdmission()
        {
            using var r = new Rig(fastHeavy: true); r.AddHeavies(1, 63, 151); r.Advance(179);
            Assert.That(r.Sim.AdrenalineLedgerOf(new UnitId(2)).AvailableAdrenaline, Is.GreaterThanOrEqualTo(CombatValuesV05.ResourceUnitsPerPoint));
            var opportunity = r.Sim.ReactionOpportunities.ActiveOpportunities.Single(o => o.DefenderUnitId.Value == 2 && o.SourceImpactTick == 181);
            var request = new CommandRequest(180, new ReactionCommandScope(opportunity.Id),
                new ReactionCommandPayload(ReactionCommandKind.Block, CombatExperimentDefinition.Action("block"), StartTick: 181));
            var prediction = r.Forecast(request);
            r.Submit(request); r.Advance(180);
            Assert.That(r.Sim.AdrenalineLedgerOf(new UnitId(2)).AvailableAdrenaline, Is.EqualTo(0));
            Assert.That(prediction.RejectionCode, Is.EqualTo("ADRENALINE_INSUFFICIENT"));
        }
    }
}
