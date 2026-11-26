using ProjectHero.Logic.Combat;

namespace ProjectHero.Logic.AI
{
    // All values are immutable definition inputs. Temperature is in thousandths (0 = greedy).
    public sealed record AiSamplingSettings(int TemperatureMilli = 1000, int ScoreScale = 16384,
        int TopPMilli = 1000, bool SignedDamage = false, bool HealthDefense = false,
        int LowHealthBoostMilli = 4000, int ResourceValue = 8192, int SurvivalValue = 262144)
    {
        public const string Version = "pressure-sampling-v1";
        public const string Grouping = "owner-action-mean-exp-then-conditional-parameters-v1";
        public string Validate() => TemperatureMilli < 0 || TemperatureMilli > 10000 || ScoreScale < 1 || ScoreScale > 10000000
            || TopPMilli < 1 || TopPMilli > 1000 || LowHealthBoostMilli < 0 || LowHealthBoostMilli > 10000
            || ResourceValue < 0 || ResourceValue > 1000000 || SurvivalValue < 0 || SurvivalValue > 10000000
            ? "AI_SAMPLING_SETTINGS_INVALID" : null;
        public void WriteHashComponents(CanonicalHashWriter w)
        {
            w.Write("ai.sampling.version", Version); w.Write("ai.sampling.grouping", Grouping);
            w.Write("ai.sampling.temperature_milli", TemperatureMilli); w.Write("ai.sampling.scale", ScoreScale);
            w.Write("ai.sampling.top_p_milli", TopPMilli); w.Write("ai.sampling.signed_damage", SignedDamage ? 1 : 0);
            w.Write("ai.sampling.health_defense", HealthDefense ? 1 : 0); w.Write("ai.sampling.low_health_boost", LowHealthBoostMilli);
            w.Write("ai.sampling.resource_value", ResourceValue); w.Write("ai.sampling.survival_value", SurvivalValue);
        }
    }
}
