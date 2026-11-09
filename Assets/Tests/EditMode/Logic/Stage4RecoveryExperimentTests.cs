using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Replay;

namespace ProjectHero.Logic.Tests
{
    public sealed class Stage4RecoveryExperimentTests
    {
        [Test]
        public void MatrixFreezesPairedInputsAndIndependentFirstAndSpawnExchange()
        {
            var matrix = Stage4Matrix.All();
            Assert.That(matrix.Count, Is.EqualTo(304));
            Assert.That(matrix.Select(s => s.Id).Distinct().Count(), Is.EqualTo(matrix.Count));
            foreach (var group in matrix.GroupBy(s => new { s.Layout, s.BluntPolicy, s.OpponentPolicy, s.Seed, s.WindowTicks, s.RecoveryTicks }))
                Assert.That(group.Select(s => (s.OpponentFirst, s.SwapSpawns)).Distinct().Count(), Is.EqualTo(4));
            Assert.That(matrix.Where(s => s.Holdout).Select(s => s.RecoveryTicks).Distinct(), Is.EquivalentTo(Stage4Matrix.BoundaryTicks));
            Assert.That(matrix.Any(s => s.Team && s.Wall), Is.True);
            Assert.That(matrix.Any(s => s.OpponentPolicy == AiPolicyKind.Legacy), Is.True);
            Assert.That(matrix.Any(s => s.Seed == 97), Is.True);
            Assert.That(matrix.Select(s => s.WindowTicks).Distinct(), Is.EquivalentTo(new[] { 179, 180, 181 }));
        }

        [TestCase(Stage4Layout.ProbeOpen)] [TestCase(Stage4Layout.ProbeWall)]
        [TestCase(Stage4Layout.HeavyQuickOpen)] [TestCase(Stage4Layout.HeavyQuickWall)]
        [TestCase(Stage4Layout.TeamSweepOpen)] [TestCase(Stage4Layout.TeamSweepWall)]
        [TestCase(Stage4Layout.HoldoutDiagonal)] [TestCase(Stage4Layout.HoldoutStaggered)]
        public void RecoveryIsTheOnlyDefinitionChangeWithinAMatchedBlock(Stage4Layout layout)
        {
            var s = new Stage4Input(layout, 30, AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt);
            var reference = Stage4RecoveryExperiment.Build(s);
            var candidate = Stage4RecoveryExperiment.Build(s with { RecoveryTicks = 40 });
            Assert.That(candidate.BattleDefinitionHashValue, Is.Not.EqualTo(reference.BattleDefinitionHashValue));
            var restored = candidate with { Actions = candidate.Actions.Select(a => a.Payload is AttackPayloadSpec attack
                && attack.Strength.Recipe == PhysicalRecipe.Blunt ? a with { Timing = ((AttackTimingSpec)a.Timing) with { RecoveryTicks = 30 } } : a).ToArray() };
            Assert.That(CombatExperimentDefinition.Hash(restored), Is.EqualTo(reference.BattleDefinitionHashValue));
            Assert.That(candidate.Encounters[0].Controllers.Count, Is.EqualTo(2));
            Assert.That(candidate.Units.Count, Is.EqualTo(s.Team ? 4 : 2));
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void FirstAndSpawnDoNotChangeFactionOrLoadout(bool opponentFirst, bool swap)
        {
            var d = Stage4RecoveryExperiment.Build(new Stage4Input(Stage4Layout.ProbeOpen, 31,
                AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, opponentFirst, swap));
            var e = d.Encounters[0]; var a = e.Slots.First(s => e.Controllers[0].ControlledSlots.Contains(s.SlotId));
            Assert.That(a.InitialPosition.X, Is.EqualTo(swap ? 6 : 0));
            Assert.That(a.SlotId.Value, Is.EqualTo(opponentFirst ? "slot.stage4.2" : "slot.stage4.1"));
            Assert.That(d.FindUnit(a.DefinitionId).ActionSetId.Value, Is.EqualTo("action_set.stage4.a"));
        }

        [TestCase(Stage4Layout.ProbeOpen, 31)] [TestCase(Stage4Layout.TeamSweepOpen, 40)]
        public void LongMatchesCaptureAllDecisionsAndConserveActualHealthAndResources(Stage4Layout layout, int recovery)
        {
            int streamed = 0;
            var result = Stage4RecoveryExperiment.Run(new Stage4Input(layout, recovery, AiPolicyKind.SustainedOffense, AiPolicyKind.CautiousDefense), _ => streamed++);
            Assert.That(streamed, Is.EqualTo(result.Blunt.Decisions + result.Opponent.Decisions));
            Assert.That(streamed, Is.GreaterThan(512), "Do not truncate the long match at the AI diagnostic ring buffer");
            foreach (var side in new[] { result.Blunt, result.Opponent })
            {
                long initial = (result.Input.Team ? 2 : 1) * 24L * 1024;
                Assert.That(side.DamageReceivedQ10 + side.HealthQ10, Is.EqualTo(initial));
                Assert.That(side.IncomeCredited + side.IncomeOverflow, Is.EqualTo(side.IncomeRequested));
                Assert.That(side.IncomeCredited + side.Refunded - side.Reserved - side.CycleExpired - side.BattleEndAvailableCleared,
                    Is.EqualTo(side.FinalAvailable));
                Assert.That(side.WindowBusyTicks, Is.LessThanOrEqualTo(side.WindowTicks));
            }
            Assert.That(result.Plans.Any(p => p.ExecutedImpact > 160), Is.True);
            Assert.That(result.Replay.Submissions.Count, Is.EqualTo(0), "AI commands must be regenerated rather than injected as recorded Player facts");
            using var stream = new MemoryStream(); ReplayFile.Save(stream, result.Replay); stream.Position = 0;
            using var player = new ReplayPlayer(result.Definition); player.Load(ReplayFile.Load(stream, result.Definition)); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            Assert.That(player.Deviation, Is.Null); Assert.That(player.IsComplete, Is.True);
        }

        [Test]
        public void IndependentParallelWorldsProduceIdenticalFullReplays()
        {
            var input = new Stage4Input(Stage4Layout.HeavyQuickOpen, 31, AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt);
            var first = Task.Run(() => Stage4RecoveryExperiment.Run(input));
            var second = Task.Run(() => Stage4RecoveryExperiment.Run(input));
            Task.WaitAll(first, second);
            Assert.That(first.Result.FinalSnapshot.ComputeHash(), Is.EqualTo(second.Result.FinalSnapshot.ComputeHash()));
            using var a = new MemoryStream(); using var b = new MemoryStream();
            ReplayFile.Save(a, first.Result.Replay); ReplayFile.Save(b, second.Result.Replay);
            Assert.That(a.ToArray(), Is.EqualTo(b.ToArray()));
        }
    }
}
