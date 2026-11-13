using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Authoring.Tests
{
    public sealed class AiApproachGoalTests
    {
        sealed class Rig : IDisposable
        {
            public readonly BattleSimulation Sim;
            public readonly AiDecisionContext Context;
            public Rig(bool approach, bool near = false, bool mirror = false)
            {
                var d = FormalAiBaseline.Build(BattleDefinitionFixture.Definition, AiPolicyKind.CautiousDefense,
                    AiExecutionKind.UtilityBehaviorTree, near, mirror, new AiForecastSettings(360, true, approach));
                var inputs = new BattleRuntimeInputs(20260922, 10);
                var composition = new ProductionBattleComposition(d, d.Encounters[0].EncounterId, inputs);
                Sim = BattleSimulation.Create(d, d.Encounters[0].EncounterId, inputs,
                    new BattleSimulationAssembly(turnWindowSchedule: composition));
                composition.Attach(Sim); Sim.Step(0, Sim.CommandIngress.FreezeTick(0));
                Context = composition.AiControllers.Select(ai =>
                {
                    var id = new ControllerId(ai.RegisteredControllerIds[0]);
                    return ai.BuildContext(Sim.DecisionSnapshotFor(id), id);
                }).Single(c => c.HasSubmissionWindow);
            }
            public void Dispose() => Sim.Dispose();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnthreatenedCautiousUnitApproachesDespiteNegativeLegacyUtility(bool mirror)
        {
            using var old = new Rig(false, mirror: mirror); using var revised = new Rig(true, mirror: mirror);
            var original = AiTacticalPolicy.Decide(old.Context, 20260922, AiPolicyKind.CautiousDefense);
            var ingress = revised.Sim.CommandIngress.CaptureSnapshot();
            var next = AiTacticalPolicy.Decide(revised.Context, 20260922, AiPolicyKind.CautiousDefense);
            Assert.That(original.Selected, Is.Null);
            Assert.That(next.Selected, Is.Not.Null);
            var chosen = next.Evaluations.Single(e => e.Chosen);
            Assert.That(chosen.Type, Is.EqualTo(ActionType.Move)); Assert.That(chosen.Score, Is.LessThanOrEqualTo(0));
            Assert.That(chosen.PositionProgress, Is.GreaterThan(0)); Assert.That(chosen.IncomingDeltaQ10, Is.LessThanOrEqualTo(0));
            Assert.That(chosen.Score, Is.EqualTo(original.Evaluations.Single(e => e.Key == chosen.Key).Score));
            Assert.That(revised.Sim.CommandIngress.CaptureSnapshot().Entries, Is.EqualTo(ingress.Entries));
            Assert.That(revised.Sim.CommandIngress.FindEntry(revised.Context.ControllerId).Submit(next.Selected), Is.Null);
            while (revised.Sim.Tick < chosen.EndTick && !revised.Sim.IsEnded)
            {
                long tick = revised.Sim.Tick + 1; var result = revised.Sim.Step(tick, revised.Sim.CommandIngress.FreezeTick(tick));
                Assert.That(result.Events.Events.OfType<CommandRejectedEvent>(), Is.Empty);
            }
            var owner = revised.Context.ControlledUnits.Single();
            var before = revised.Context.Snapshot.VisibleUnits.Single(u => u.UnitId == owner.Value);
            var after = revised.Sim.CurrentSnapshot.Units.Single(u => u.UnitId == owner.Value);
            Assert.That((after.X, after.Y), Is.Not.EqualTo((before.X, before.Y)));
        }

        [Test]
        public void InReachAttackAndFormalContentKeepTheirExistingEvaluation()
        {
            using var old = new Rig(false, near: true); using var revised = new Rig(true, near: true);
            var a = AiTacticalPolicy.Decide(old.Context, 20260922, AiPolicyKind.CautiousDefense);
            var b = AiTacticalPolicy.Decide(revised.Context, 20260922, AiPolicyKind.CautiousDefense);
            Assert.That(b.Evaluations.Single(e => e.Chosen).Type, Is.EqualTo(ActionType.Attack));
            Assert.That(b.Evaluations.Select(e => (e.Key, e.Score, e.Chosen)), Is.EqualTo(a.Evaluations.Select(e => (e.Key, e.Score, e.Chosen))));
            Assert.That(revised.Sim.Definition.Actions, Is.EqualTo(old.Sim.Definition.Actions));
            Assert.That(revised.Sim.Definition.Units, Is.EqualTo(old.Sim.Definition.Units));
            Assert.That(revised.Sim.Definition.BattleDefinitionHashValue, Is.Not.EqualTo(old.Sim.Definition.BattleDefinitionHashValue));
        }

        static (AiTacticalCandidate[] candidates, AiCandidateEvaluation[] values, Dictionary<string, AiActionForecast> previews) Inputs(Rig rig)
        {
            var candidates = AiTacticalCandidateBuilder.Ordinary(rig.Context).ToArray();
            using var forecast = rig.Context.ForecastFactory(rig.Context.Snapshot);
            var baseline = forecast.Evaluate(null, rig.Context.ForecastSettings);
            var previews = candidates.ToDictionary(c => c.Key, c => forecast.Evaluate(c.Request, rig.Context.ForecastSettings));
            var values = candidates.Select(c => AiTacticalPolicy.Evaluate(rig.Context, AiPolicyKind.CautiousDefense, c, baseline, previews[c.Key])).ToArray();
            return (candidates, values, previews);
        }

        [TestCase("incoming")]
        [TestCase("damage")]
        [TestCase("progress")]
        [TestCase("uncovered")]
        [TestCase("rejected")]
        [TestCase("terminated")]
        public void UnsafeOrIncompleteMovementCannotReplaceWait(string condition)
        {
            using var rig = new Rig(true); var data = Inputs(rig);
            var values = data.values.Select(e => e.Type != ActionType.Move ? e : condition switch
            {
                "incoming" => e with { IncomingDeltaQ10 = 1 },
                "damage" => e with { DamageDeltaQ10 = -1 },
                "progress" => e with { PositionProgress = 0 },
                "uncovered" => e with { Coverage = AiForecastCoverage.Uncovered, Score = null },
                "rejected" => e with { RejectionCode = "fixture-grid-conflict" },
                _ => e
            }).ToArray();
            if (condition == "terminated")
                foreach (var c in data.candidates.Where(c => c.Type == ActionType.Move))
                    data.previews[c.Key] = data.previews[c.Key] with { CandidateTerminalReason = 1 };
            var choice = AiApproachPolicy.Choose(rig.Context, data.candidates, values, data.previews);
            Assert.That(choice.Candidate, Is.Null); Assert.That(choice.Reason, Is.EqualTo("WAIT_APPROACH_NO_SAFE_PROGRESS"));
        }

        [TestCase("no-attack")]
        [TestCase("uncovered-attack")]
        [TestCase("in-reach")]
        [TestCase("no-window")]
        public void ApproachRequiresAnAvailableUnthreatenedEngagementGoal(string condition)
        {
            using var rig = new Rig(true); var data = Inputs(rig);
            var context = rig.Context;
            var values = condition == "no-attack" ? data.values.Where(e => e.Type != ActionType.Attack).ToArray()
                : data.values.Select(e => e.Type != ActionType.Attack ? e : condition == "uncovered-attack"
                    ? e with { Coverage = AiForecastCoverage.Uncovered, Score = null }
                    : condition == "in-reach" ? e with { NoDefenseDamageDeltaQ10 = 1024, Score = -1 } : e).ToArray();
            if (condition == "no-window") context = context with { OwnWindow = null };
            var choice = AiApproachPolicy.Choose(context, data.candidates, values, data.previews);
            Assert.That(choice.Candidate, Is.Null);
            Assert.That(choice.Reason, Is.EqualTo(condition switch { "no-attack" => "WAIT_APPROACH_NO_ATTACK_OPTION",
                "uncovered-attack" => "WAIT_APPROACH_ATTACK_OUTCOME_UNCOVERED", "in-reach" => "WAIT_APPROACH_ATTACK_AVAILABLE",
                _ => "WAIT_APPROACH_NO_OWN_WINDOW" }));
        }

        [Test]
        public void PublishedReactionOpportunityPreventsGoalOverrideEvenWhenItsSourcePortIsPartial()
        {
            using var rig = new Rig(true); var data = Inputs(rig);
            var opportunity = new ReactionOpportunityId(999);
            var block = rig.Sim.Definition.Actions.First(a => a.Type == ActionType.Block).ActionSpecId;
            var context = rig.Context with { Opportunities = new[] { new AiOpportunityView(opportunity, rig.Context.ControlledUnits[0],
                new ActionPlanId(999), 90, new[] { new AiReactionOptionView(opportunity, block, ReactionCommandKind.Block,
                    89, true, true, Array.Empty<GridPoint>()) }) } };
            var choice = AiApproachPolicy.Choose(context, data.candidates, data.values, data.previews);
            Assert.That(choice.Candidate, Is.Null); Assert.That(choice.Reason, Is.EqualTo("WAIT_APPROACH_PUBLIC_THREAT"));
        }

        [Test]
        public void EqualProgressRatePrefersShorterCommitmentAndOrdinalTiesAreStable()
        {
            using var rig = new Rig(true); var data = Inputs(rig);
            var shortMove = data.values.Where(e => e.Type == ActionType.Move && e.RejectionCode == null && e.PositionProgress > 0
                && e.IncomingDeltaQ10 == 0).OrderBy(e => e.EndTick).First();
            var shortCandidate = data.candidates.Single(c => c.Key == shortMove.Key);
            // Selection-port fixture: double both progress and commitment while retaining the same rate.
            var longCandidate = shortCandidate with { Key = shortMove.Key + "/long-summary" };
            var longMove = shortMove with { Key = longCandidate.Key, PositionProgress = shortMove.PositionProgress * 2,
                EndTick = rig.Context.NextTick + 2 * (shortMove.EndTick - rig.Context.NextTick) };
            data.previews[longMove.Key] = data.previews[shortMove.Key] with { PlanEndTick = longMove.EndTick };
            var values = data.values.Where(e => e.Type == ActionType.Attack).Concat(new[] { shortMove, longMove }).ToArray();
            var subset = data.candidates.Where(c => c.Type == ActionType.Attack).Concat(new[] { shortCandidate, longCandidate }).ToArray();
            foreach (var candidates in new[] { subset, subset.Reverse().ToArray() })
                Assert.That(AiApproachPolicy.Choose(rig.Context, candidates, values, data.previews).Candidate.Key, Is.EqualTo(shortMove.Key));
            var tie = shortCandidate with { Key = shortCandidate.Key + "/tie-summary" };
            data.previews[tie.Key] = data.previews[shortMove.Key];
            Assert.That(AiApproachPolicy.Choose(rig.Context, new[] { tie, shortCandidate },
                values.Concat(new[] { shortMove with { Key = tie.Key } }).ToArray(), data.previews).Candidate.Key, Is.EqualTo(shortMove.Key));
        }
    }
}
