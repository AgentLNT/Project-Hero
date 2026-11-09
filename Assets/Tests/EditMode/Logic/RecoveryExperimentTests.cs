using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Experiments;

namespace ProjectHero.Logic.Tests
{
    public sealed class RecoveryExperimentTests
    {
        [TestCase(30)] [TestCase(33)] [TestCase(36)] [TestCase(39)] [TestCase(42)] [TestCase(48)] [TestCase(60)]
        public void PublicIncrementChangesOnlyBluntTimingAndTheDefinitionHash(int recovery)
        {
            var baseline = CombatExperimentDefinition.Build(new CombatExperimentScenario("base"));
            var selected = CombatExperimentDefinition.Build(new CombatExperimentScenario("selected", BluntRecoveryDelta: recovery - 30));
            foreach (var action in selected.Actions)
            {
                var old = baseline.FindAction(action.ActionSpecId);
                if (action.Timing is AttackTimingSpec timing)
                {
                    var strength = ((AttackPayloadSpec)action.Payload).Strength;
                    Assert.That(timing.RecoveryTicks, Is.EqualTo(strength.Recipe == PhysicalRecipe.Blunt ? recovery : 30));
                    Assert.That(timing.BaseWindupTicks, Is.EqualTo(((AttackTimingSpec)old.Timing).BaseWindupTicks));
                }
                else Assert.That(action.Timing, Is.EqualTo(old.Timing));
            }
            var normalized = selected with { Actions = selected.Actions.Select(a => a.Timing is AttackTimingSpec t
                ? a with { Timing = t with { RecoveryTicks = 30 } } : a).ToArray() };
            Assert.That(CombatExperimentDefinition.Hash(normalized), Is.EqualTo(baseline.BattleDefinitionHashValue),
                "After removing only the recovery increment, every hashed input must match the baseline.");
            Assert.That(selected.BattleDefinitionHashValue == baseline.BattleDefinitionHashValue, Is.EqualTo(recovery == 30));
        }

        [TestCase(30,1,false)] [TestCase(33,1,false)] [TestCase(36,1,false)] [TestCase(39,1,false)]
        [TestCase(42,1,false)] [TestCase(48,1,false)] [TestCase(60,1,false)]
        [TestCase(30,2,true)] [TestCase(33,2,true)] [TestCase(36,2,true)] [TestCase(39,2,true)]
        [TestCase(42,2,true)] [TestCase(48,2,true)] [TestCase(60,2,true)]
        public void RealCommandsReproduceEarliestRepeatImpactsForEveryCoarseGroup(int recovery, int blunt, bool swap)
        {
            var result = RecoveryExperimentRunner.Run(new RecoveryExperimentScenario("repeat", recovery,
                BluntOwner: blunt, SwapSpawns: swap, MaxTicks: 360));
            var clashes = result.Events.OfType<ClashParticipantResolvedEvent>().ToArray();
            Assert.That(clashes.Count(e => e.Tick == 213), Is.EqualTo(2));
            foreach (long owner in new long[] { 1,2 })
            {
                var next = result.Actions.Where(a => a.Owner == owner && a.Type == (int)ActionType.Attack && a.Locked > 213).OrderBy(a => a.Start).First();
                Assert.That(next.Start, Is.EqualTo(213 + (owner == blunt ? recovery : 30)));
                Assert.That(next.ExecutedImpact, Is.EqualTo(next.Start + 30));
                Assert.That(next.End - next.Impact, Is.EqualTo(owner == blunt ? recovery : 30));
            }
            if (recovery == 30) Assert.That(clashes.Count(e => e.Tick == 273), Is.EqualTo(2));
            else
            {
                Assert.That(clashes.Any(e => e.Tick > 213), Is.False);
                var incoming = result.Diagnostics.Single(t => t.Tick == 273).Damage.Units.Single(d => d.UnitId.Value == blunt);
                Assert.That(incoming.DamageQ10, Is.EqualTo(4 * 1024));
                var outgoing = result.Diagnostics.Single(t => t.Tick == 243 + recovery).Damage.Units.Single(d => d.UnitId.Value != blunt);
                Assert.That(outgoing.DamageQ10, Is.EqualTo(3 * 1024));
            }
            Assert.That(result.Events.OfType<ConcurrentActionActivatedEvent>().Count(), Is.EqualTo(1));
            Assert.That(result.Events.OfType<CommandRejectedEvent>(), Is.Empty);
            RecoveryExperimentRunner.VerifyReplay(result);
        }

        [TestCase(36)] [TestCase(60)]
        public void WaitingCanRealignAndEqualBluntTimingsStillClash(int recovery)
        {
            foreach (var scenario in new[] {
                new RecoveryExperimentScenario("wait",recovery,Response:RecoveryResponse.WaitRealign,MaxTicks:360),
                new RecoveryExperimentScenario("same_blunt",recovery,Pair:RecoveryPair.ProbeBlunt,MaxTicks:360) })
            {
                var r = RecoveryExperimentRunner.Run(scenario);
                Assert.That(r.Events.OfType<ClashParticipantResolvedEvent>().Count(e => e.Tick == 243 + recovery), Is.EqualTo(2));
                RecoveryExperimentRunner.VerifyReplay(r);
            }
        }

        [TestCase(RecoveryResponse.Guard)] [TestCase(RecoveryResponse.Move)] [TestCase(RecoveryResponse.Switch)]
        [TestCase(RecoveryResponse.Block)] [TestCase(RecoveryResponse.Dodge)]
        public void AlternativeScriptsUseLegalCommandsAndSerializableInputs(RecoveryResponse response)
        {
            var r = RecoveryExperimentRunner.Run(new RecoveryExperimentScenario("response",36,Response:response,BluntOwner:2));
            Assert.That(r.Events.OfType<CommandRejectedEvent>(), Is.Empty);
            string action = response == RecoveryResponse.Guard ? "guard" : response == RecoveryResponse.Move ? "move"
                : response == RecoveryResponse.Switch ? "quick" : response.ToString();
            Assert.That(r.Decisions.Any(d => d.UnitId == 2 && d.Action == action), Is.True, action);
            RecoveryExperimentRunner.VerifyReplay(r);
        }

        [Test]
        public void MatrixHasAllCoarseValuesAndExplicitControlWindowAndGrowthCoverage()
        {
            var matrix = RecoveryExperimentRunner.Matrix(RecoveryExperimentRunner.CoarseTicks);
            Assert.That(matrix.Select(s => s.Id).Distinct().Count(), Is.EqualTo(matrix.Count));
            foreach (int value in RecoveryExperimentRunner.CoarseTicks)
            {
                var slice = matrix.Where(s => s.RecoveryTicks == value).ToArray();
                Assert.That(slice.Select(s => s.Growth).Distinct().OrderBy(v => v), Is.EqualTo(new[]{0,1,2,3,4}));
                Assert.That(slice.Select(s => s.Health).Distinct().OrderBy(v => v), Is.EqualTo(new[]{24,27,30,36}));
                Assert.That(slice.Any(s => s.ShortRange && s.Wall), Is.True);
                Assert.That(slice.Any(s => s.FirstImpact == 360 && s.SwapSpawns), Is.True);
            }
        }

        [TestCase(30,RecoveryPair.UnopposedQuick,6)] [TestCase(36,RecoveryPair.UnopposedQuick,6)] [TestCase(60,RecoveryPair.UnopposedQuick,6)]
        [TestCase(30,RecoveryPair.UnopposedHeavy,4)] [TestCase(36,RecoveryPair.UnopposedHeavy,4)] [TestCase(60,RecoveryPair.UnopposedHeavy,4)]
        [TestCase(30,RecoveryPair.UnopposedSweep,8)] [TestCase(36,RecoveryPair.UnopposedSweep,8)] [TestCase(60,RecoveryPair.UnopposedSweep,8)]
        public void RecoveryDoesNotAlterDamageOrRequiredEffectiveHits(int recovery, RecoveryPair pair, int hits)
        {
            var r = RecoveryExperimentRunner.Run(new RecoveryExperimentScenario("htk", recovery, Pair:pair, MaxTicks:1800));
            Assert.That(r.Outcome, Is.EqualTo("victory"));
            Assert.That(r.Diagnostics.SelectMany(t => t.Damage.Units).Count(d => d.UnitId.Value == 2 && d.DamageQ10 > 0), Is.EqualTo(hits));
            RecoveryExperimentRunner.VerifyReplay(r);
        }

        [Test]
        public void SameTickControlCancellationRetainsAcceptedImpactAndSourceWindowInDiagnostics()
        {
            var r = RecoveryExperimentRunner.Run(new RecoveryExperimentScenario("cancel_metadata",30,
                Pair:RecoveryPair.HeavySweep,BluntOwner:2,MaxTicks:500));
            var cancelled = r.Actions.Where(a => a.Locked >= 0 && a.Terminal == a.Locked
                && a.Reason == (int)ActionTerminationReason.InterruptedByControl).ToArray();
            Assert.That(cancelled, Is.Not.Empty);
            Assert.That(cancelled.All(a => a.Impact > a.Terminal && a.Window > 0), Is.True);
            RecoveryExperimentRunner.VerifyReplay(r);
        }

        [TestCase(30,true)] [TestCase(31,false)]
        public void OneTickCanCrossTheLastLegalStartOfTheSourceWindow(int recovery, bool canRepeat)
        {
            var r = RecoveryExperimentRunner.Run(new RecoveryExperimentScenario("window_edge", recovery,
                BluntOwner:2,FirstImpact:329,MaxTicks:500));
            var follow = r.Actions.Where(a => a.Owner == 2 && a.Start > 329 && a.Locked >= 0).ToArray();
            Assert.That(follow.Any(), Is.EqualTo(canRepeat));
            if (canRepeat) Assert.That(follow.First().Start, Is.EqualTo(359));
            else Assert.That(r.Decisions.Any(d => d.UnitId == 2 && d.Reason == "start-outside-current-window"), Is.True);
            RecoveryExperimentRunner.VerifyReplay(r);
        }
    }
}
