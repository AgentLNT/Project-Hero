using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Combat;

namespace ProjectHero.Logic.Tests
{
    public sealed class AiPressurePolicyTests
    {
        sealed class Replies : IAiDefenseForecast
        {
            public AiDefenseForecast[] Values = Array.Empty<AiDefenseForecast>();
            public AiActionForecast Evaluate(CommandRequest request, AiForecastSettings settings = null) => throw new NotSupportedException();
            public System.Collections.Generic.IReadOnlyList<AiDefenseForecast> EvaluateDefenses(CommandRequest attack, AiForecastSettings settings) => Values;
            public void Dispose() { }
        }

        [Test]
        public void RealDamageThenCycleResetCreatesEqualFundingAcrossBloodlines()
        {
            long previous = long.MinValue;
            foreach (int health in new[] { 28, 18, 6 })
            {
                var result = AiBloodlineExperiment.Run(new AiBloodlineInput(health));
                Assert.That(result.Events.OfType<CommandRejectedEvent>(), Is.Empty);
                var first = result.Decisions.First(d => d.Evaluations.Any(e => e.IsReaction && e.ProbabilityBeforeQ32 > 0));
                var block = first.Evaluations.First(e => e.Type == ActionType.Block);
                Assert.That(block.Score.Value, Is.GreaterThan(previous)); previous = block.Score.Value;
                Assert.That(first.SamplingMemory.DrawCount, Is.EqualTo(1));
                Assert.That(result.Decisions.Where(d => d.SamplingMemory?.OpportunityKey == first.SamplingMemory.OpportunityKey)
                    .All(d => d.SamplingMemory.DrawCount == 1), Is.True, "Unchanged legal opportunities must not reroll every Tick");
                Assert.That(result.Events.OfType<AdrenalineLedgerChangedEvent>().Any(e => e.Tick == 180 && e.ChangeKind == AdrenalineChangeKind.CycleReset), Is.True);
            }
        }

        [Test]
        public void AcceptedDefenseReservationSurvivesResetBeforeImpact()
        {
            var result = AiBloodlineExperiment.Run(new AiBloodlineInput(6, ThreatDamage: 8, ThreatStart: 521));
            Assert.That(result.Events.OfType<CommandRejectedEvent>(), Is.Empty);
            Assert.That(result.Events.OfType<AdrenalineLedgerChangedEvent>().Any(e => e.Tick == 540 && e.ChangeKind == AdrenalineChangeKind.CycleReset), Is.True);
            Assert.That(result.Events.OfType<AdrenalineLedgerChangedEvent>().Any(e => e.Tick > 540 && e.ChangeKind == AdrenalineChangeKind.Consumed), Is.True);
        }

        [Test]
        public void SignedDamageUsesEachTargetLegalityAndKeepsGuardPartialReduction()
        {
            var d = CombatExperimentDefinition.Build(new CombatExperimentScenario("signed", Script: ExperimentScript.SweepTwo));
            using var sim = ProductionBattleComposition.Create(d, CombatExperimentDefinition.Encounter, new BattleRuntimeInputs(31, 4));
            var snapshot = sim.DecisionSnapshotFor(CombatExperimentDefinition.Controller(1));
            var c = new AiDecisionContext(snapshot, snapshot.ControllerId, snapshot.ControlledUnitIds, Array.Empty<ActionPlanSnapshot>(),
                snapshot.OwnWindow, Array.Empty<AiOpportunityView>(), null, null) with {
                ForecastSettings = new AiForecastSettings(360, true, Sampling: new AiSamplingSettings(SignedDamage: true)) };
            var before = new AiActionForecast(null, 360, snapshot.VisibleUnits, -1, 0, 0, Coverage: AiForecastCoverage.Complete);
            var outcome = before with { Units = before.Units.Select(u => u.UnitId > 1 ? u with { HealthQ10 = u.HealthQ10 - 8192 } : u).ToArray() };
            var request = new CommandRequest(1, new ScheduleEditScope(0, null), new ScheduleEditPayload(new ScheduleEditOperation[] {
                new AddOrdinaryPlanOperation(1, new UnitId(1), CombatExperimentDefinition.Action("sweep"), PrimaryTargetUnitId: new UnitId(2), Facing: GridDirection.East) }));
            var candidate = new AiTacticalCandidate("attack", ActionType.Attack, request, false, 0);
            var original = AiTacticalPolicy.Evaluate(c, AiPolicyKind.CautiousDefense, candidate, before, outcome);
            using var replies = new Replies { Values = new[] {
                new AiDefenseForecast("block", CombatExperimentDefinition.Controller(2), 1,
                    new CommandRequest(2, new ReactionCommandScope(new ReactionOpportunityId(1)), new ReactionCommandPayload(ReactionCommandKind.Block, CombatExperimentDefinition.Action("block"))), outcome) { DefenderUnitId = 2 },
                new AiDefenseForecast("guard", CombatExperimentDefinition.Controller(3), 1, request,
                    outcome with { Units = outcome.Units.Select(u => u.UnitId == 3 ? u with { HealthQ10 = u.HealthQ10 + 4096 } : u).ToArray() }) { DefenderUnitId = 3 } } };
            var scored = AiPressurePolicy.Score(c, AiPolicyKind.CautiousDefense, candidate, before, outcome, original, replies);
            Assert.That(scored.DamageUtility, Is.EqualTo(-8192 + 4096));
            Assert.That(scored.TargetDefenses.Single(t => t.UnitId == 2).LegalReaction, Is.True);
            Assert.That(scored.TargetDefenses.Single(t => t.UnitId == 3).LegalReaction, Is.False);
            replies.Values = Array.Empty<AiDefenseForecast>();
            var undefended = AiPressurePolicy.Score(c, AiPolicyKind.CautiousDefense, candidate, before, outcome, original, replies);
            Assert.That(undefended.DamageUtility, Is.EqualTo(16384));
            var shortLow = outcome with { PlanEndTick = 31, Units = before.Units.Select(u => u.UnitId == 2 ? u with { HealthQ10 = u.HealthQ10 - 4096 } : u).ToArray() };
            var longHigh = outcome with { PlanEndTick = 121, Units = before.Units.Select(u => u.UnitId == 2 ? u with { HealthQ10 = u.HealthQ10 - 8192 } : u).ToArray() };
            long Compare(AiActionForecast value) => AiPressurePolicy.Score(c, AiPolicyKind.CautiousDefense, candidate, before, value,
                AiTacticalPolicy.Evaluate(c, AiPolicyKind.CautiousDefense, candidate, before, value), replies).Score.Value;
            Assert.That(Compare(longHigh), Is.GreaterThan(Compare(shortLow)), "High damage regains positive utility when defense is unavailable");
            replies.Values = new[] { new AiDefenseForecast("block", CombatExperimentDefinition.Controller(2), 1,
                new CommandRequest(2, new ReactionCommandScope(new ReactionOpportunityId(1)), new ReactionCommandPayload(ReactionCommandKind.Block,
                    CombatExperimentDefinition.Action("block"))), outcome) { DefenderUnitId = 2 } };
            Assert.That(Compare(shortLow), Is.GreaterThan(Compare(longHigh)), "Legal Block reverses damage preference; duration also favors the short action");
            var lethal = outcome with { Units = outcome.Units.Select(u => u.UnitId > 1 ? u with { HealthQ10 = -99999 } : u).ToArray() };
            var lethalScore = AiTacticalPolicy.Evaluate(c, AiPolicyKind.CautiousDefense, candidate, before, lethal);
            Assert.That(lethalScore.DamageDeltaQ10, Is.EqualTo(before.Units.Where(u => u.UnitId > 1).Sum(u => (long)u.HealthQ10)));
        }

        [Test]
        public void NegativeScoresAreSampledAndTemperatureFlattensProbabilities()
        {
            var items = new[] { new AiSampleItem("attack", "attack", -20000), new AiSampleItem("guard", "guard", -1) };
            var low = AiSoftmax.Sample(items, new AiSamplingSettings(250), new DeterministicRng(31));
            var high = AiSoftmax.Sample(items, new AiSamplingSettings(2000), new DeterministicRng(31));
            Assert.That(low.Probabilities[0].AfterQ32, Is.GreaterThan(0));
            Assert.That(high.Probabilities[0].AfterQ32, Is.GreaterThan(low.Probabilities[0].AfterQ32));
            Assert.That(Enumerable.Range(0, 500).Any(i => AiSoftmax.Sample(items, new AiSamplingSettings(), new DeterministicRng((ulong)i)).Key == "attack"), Is.True);
        }

        [Test]
        public void DuplicatingEquivalentDestinationsDoesNotChangeActionMass()
        {
            var items = new[] { new AiSampleItem("attack", "attack", -100), new AiSampleItem("move0", "move", -100) };
            var a = AiSoftmax.Sample(items, new AiSamplingSettings(), new DeterministicRng(1));
            var many = items.Concat(Enumerable.Range(1, 99).Select(i => new AiSampleItem("move" + i, "move", -100))).ToArray();
            var b = AiSoftmax.Sample(many, new AiSamplingSettings(), new DeterministicRng(1));
            Assert.That(a.Probabilities.Single(p => p.Key == "attack").BeforeQ32, Is.EqualTo(b.Probabilities.Single(p => p.Key == "attack").BeforeQ32));
        }

        [Test]
        public void GreedyAndNucleusAreStableAndInputOrderIndependent()
        {
            var items = new[] { new AiSampleItem("b", "b", -30000), new AiSampleItem("a", "a", -1), new AiSampleItem("c", "c", -999999) };
            Assert.That(AiSoftmax.Sample(items, new AiSamplingSettings(0), new DeterministicRng(1)).Key, Is.EqualTo("a"));
            var settings = new AiSamplingSettings(TopPMilli: 900);
            var a = AiSoftmax.Sample(items, settings, new DeterministicRng(1));
            var b = AiSoftmax.Sample(items.Reverse().ToArray(), settings, new DeterministicRng(1));
            Assert.That(a.Key, Is.EqualTo(b.Key)); Assert.That(a.Probabilities, Is.EqualTo(b.Probabilities));
            Assert.That(a.Probabilities.Single(p => p.Key == "c").AfterQ32, Is.Zero);
        }

        [Test]
        public void HealthValueIsFiniteMonotoneAndAvoidingEqualDamageMattersMoreAtLowHealth()
        {
            decimal previous = -1;
            for (int h = 0; h <= 200; h++)
            {
                decimal v = AiPressurePolicy.HealthValue(h * 1024L, 200 * 1024L, 4000);
                Assert.That(v, Is.GreaterThan(previous)); previous = v;
            }
            decimal high = Avoided(190, 180), middle = Avoided(100, 90), low = Avoided(20, 10);
            Assert.That(middle, Is.GreaterThan(high)); Assert.That(low, Is.GreaterThan(middle));
            Assert.That(AiPressurePolicy.HealthValue(-100, 200 * 1024, 4000), Is.Zero);
            decimal Avoided(int a, int b) => AiPressurePolicy.HealthValue(a * 1024L, 200 * 1024, 4000) - AiPressurePolicy.HealthValue(b * 1024L, 200 * 1024, 4000);
        }

        [Test]
        public void ResourceRetentionFallsBeforeResetAndWhenBalanceIsAbundant()
        {
            long scarce = AiPressurePolicy.ResourceCost(1024, 1024, 120, 1024, 8192);
            Assert.That(AiPressurePolicy.ResourceCost(1024, 1024, 1, 1024, 8192), Is.LessThan(scarce));
            Assert.That(AiPressurePolicy.ResourceCost(1024, 10240, 120, 1024, 8192), Is.LessThan(scarce));
        }

        [TestCase(0, 1000)]
        [TestCase(1000, 950)]
        [TestCase(2000, 900)]
        public void SampledFundedReactionUsesProductionDamageAndReplays(int temperature, int topP)
        {
            var input = new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense, AiPolicySituation.FundedReaction,
                Sampling: new AiSamplingSettings(temperature, TopPMilli: topP, SignedDamage: true, HealthDefense: true));
            var result = AiPolicyExperiment.Run(input);
            Assert.That(result.Events.OfType<CommandRejectedEvent>(), Is.Empty);
            Assert.That(result.Events.OfType<AdrenalineLedgerChangedEvent>().Any(e => e.ChangeKind == AdrenalineChangeKind.Accrued), Is.True);
            Assert.That(result.Decisions.SelectMany(d => d.Evaluations).Any(e => e.IsReaction && e.Score.HasValue && e.RejectionCode == null), Is.True);
            Assert.That(result.Decisions.SelectMany(d => d.Evaluations).Where(e => e.RejectionCode != null || !e.Score.HasValue).All(e => e.ProbabilityAfterQ32 == 0), Is.True);
            AiPolicyExperiment.VerifyReplay(result);
        }

        [Test]
        public void AllSamplingParametersChangeDefinitionHashButNullRetainsOldHash()
        {
            var input = new AiPolicyExperimentInput(AiPolicyKind.CautiousDefense, AiPolicySituation.OpenAttack);
            var legacy = AiPolicyExperiment.Build(input);
            var s = new AiSamplingSettings();
            var values = new[] { s, s with { TemperatureMilli = 500 }, s with { TopPMilli = 900 }, s with { ScoreScale = 8192 },
                s with { SignedDamage = true }, s with { HealthDefense = true }, s with { LowHealthBoostMilli = 2000 },
                s with { ResourceValue = 4096 }, s with { SurvivalValue = 131072 } };
            var hashes = values.Select(v => AiPolicyExperiment.Build(input with { Sampling = v }).BattleDefinitionHashValue).ToArray();
            Assert.That(hashes.Distinct().Count(), Is.EqualTo(values.Length));
            Assert.That(hashes, Does.Not.Contain(legacy.BattleDefinitionHashValue));
            Assert.That(AiPolicyExperiment.Build(input).BattleDefinitionHashValue, Is.EqualTo(legacy.BattleDefinitionHashValue));
        }
    }
}
