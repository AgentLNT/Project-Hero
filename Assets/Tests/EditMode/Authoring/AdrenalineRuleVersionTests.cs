using NUnit.Framework;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Logic.Combat;

namespace ProjectHero.Authoring.Tests
{
    public class AdrenalineRuleVersionTests
    {
        [Test]
        public void CorrectedMainDefinitionHasDistinctHashAndCanRebuildHistoricalDefinition()
        {
            var d = BattleDefinitionFixture.Definition;
            Assert.That(d.AdrenalineRules.DamageGainQuantization, Is.EqualTo(AdrenalineDamageGainQuantization.BinaryRoundHalfUpV3));
            var old = d with { RulesVersion = "battle-def-v5-clash-recovery", AdrenalineRules = AdrenalineRules.FrozenV1 };
            string oldHash = BattleDefinitionHash.Compute(old.RulesVersion, old.TicksPerSecond,
                LegacyIdMigrationManifest.LibraryVolumes.Count, old.Rules, old.ConcurrentAction, old.ReactionRules,
                old.AdrenalineRules, old.FactionModel, old.DamageChannels, old.ImpactProfiles, old.Units, old.Actions,
                old.AttackPatterns, old.Volumes, old.MovementPatterns, old.ActionSets, old.StatusEffects, old.Encounters, old.DefaultDynamicSpawnPolicy);
            Assert.That(oldHash, Is.EqualTo("de4be447760a2266"));
            Assert.That(oldHash, Is.Not.EqualTo(d.BattleDefinitionHashValue));
            var v1 = new CanonicalHashWriter(); var v2 = new CanonicalHashWriter();
            AdrenalineRules.FrozenV1.WriteHashComponents(v1); AdrenalineRules.BinaryQ10V3.WriteHashComponents(v2);
            Assert.That(v1.ToDigestHex(), Is.Not.EqualTo(v2.ToDigestHex()), "Quantization enters the hash independently of RulesVersion.");
        }
    }
}
