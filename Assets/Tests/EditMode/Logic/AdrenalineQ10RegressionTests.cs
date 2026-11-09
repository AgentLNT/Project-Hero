using NUnit.Framework;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Resources;

namespace ProjectHero.Logic.Tests
{
    public class AdrenalineQ10RegressionTests
    {
        [TestCase(20, 2, 4)]
        [TestCase(25, 2, 5)]
        [TestCase(40, 4, 8)]
        public void RealDamageCreditsWholeResourcePoints(int damage, int dealt, int received)
        {
            var attacker = Ledger(AdrenalineRules.BinaryQ10V3);
            var defender = Ledger(AdrenalineRules.BinaryQ10V3);
            attacker.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1), damage * 1024, 0, 0, 0, 0));
            defender.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1), 0, damage * 1024, 0, 0, 0));
            Assert.That(attacker.AvailableAdrenaline, Is.EqualTo(dealt));
            Assert.That(defender.AvailableAdrenaline, Is.EqualTo(received));
        }

        [Test]
        public void AggregatedDamageRoundsOnceAndRetainsFixedRewardsAndCap()
        {
            var ledger = Ledger(AdrenalineRules.BinaryQ10V3);
            // Each 5-point contact would round to zero; their 10-point aggregate rounds to one.
            ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1), 10 * 1024, 0, 1, 1, 1));
            Assert.That(ledger.AvailableAdrenaline, Is.EqualTo(52));
            ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1), 0, 1000 * 1024, 0, 0, 0));
            Assert.That(ledger.AvailableAdrenaline, Is.EqualTo(100));
        }

        [Test]
        public void LegacyDefinitionRetainsObservedOverCreditForHistoricalReplay()
        {
            var ledger = Ledger(AdrenalineRules.FrozenV1);
            int requested = 0;
            ledger.Changed += c => requested = c.ReservationAmount;
            ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1), 0, 20 * 1024, 0, 0, 0));
            Assert.That(ledger.AvailableAdrenaline, Is.EqualTo(100));
            Assert.That(requested, Is.EqualTo(41984));
        }

        [Test]
        public void EarnedPointsFundRealCostAndReservationSurvivesOwnWindowReset()
        {
            var ledger = Ledger(AdrenalineRules.BinaryQ10V3);
            ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1), 0, 20 * 1024, 0, 0, 0));
            Assert.That(ledger.TryReserveForReaction(new ActionPlanId(1), 2), Is.Null);
            Assert.That(ledger.AvailableAdrenaline, Is.EqualTo(2));
            Assert.That(ledger.ReservedAdrenaline, Is.EqualTo(2));
            ledger.BeginOwnWindow();
            Assert.That(ledger.AvailableAdrenaline, Is.Zero);
            Assert.That(ledger.ReservedAdrenaline, Is.EqualTo(2));
            ledger.ConsumeAtTrigger(new ActionPlanId(1));
            Assert.That(ledger.ReservedAdrenaline, Is.Zero);
            Assert.That(ledger.TryReserveForReaction(new ActionPlanId(2), 1), Is.EqualTo(AdrenalineLedgerCodes.INSUFFICIENT_ADRENALINE));
        }

        [Test]
        public void CandidateScaledResourceProtocolIsNotCombinedWithWholePointQuantization()
        {
            Assert.That(AdrenalineRules.CombatValuesV05Candidate.Validate(), Is.Null);
            Assert.That((AdrenalineRules.CombatValuesV05Candidate with { DamageGainQuantization = AdrenalineDamageGainQuantization.BinaryRoundHalfUpV3 }).Validate(),
                Is.EqualTo(CombatValuesV05.Invalid));
        }

        [TestCase(511, 0)]
        [TestCase(512, 1)]
        [TestCase(513, 1)]
        public void HalfPointBoundaryRoundsUpAfterBinaryUnitConversion(int damageQ10, int expected)
        {
            var ledger = Ledger(AdrenalineRules.BinaryQ10V3 with { DamageDealtGainQ10 = 1024 });
            ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1), damageQ10, 0, 0, 0, 0));
            Assert.That(ledger.AvailableAdrenaline, Is.EqualTo(expected));
        }

        [Test]
        public void UnknownQuantizationVersionIsRejected()
        {
            Assert.That((AdrenalineRules.BinaryQ10V3 with { DamageGainQuantization = (AdrenalineDamageGainQuantization)99 }).Validate(),
                Is.EqualTo(AdrenalineRules.ADRENALINE_DAMAGE_GAIN_QUANTIZATION_INVALID));
        }

        static AdrenalineLedger Ledger(AdrenalineRules rules) => new AdrenalineLedger(new UnitId(1), rules);
    }
}
