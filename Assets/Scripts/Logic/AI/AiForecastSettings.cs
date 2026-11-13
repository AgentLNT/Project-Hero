using ProjectHero.Logic.Combat;

namespace ProjectHero.Logic.AI
{
    /// <summary>Opt-in complete-outcome scoring. Null settings retain the exact V2 policy and 120-Tick encoding.</summary>
    public sealed record AiForecastSettings(int LookaheadTicks, bool ConsiderDefenses = false, bool ApproachWhenUnthreatened = false)
    {
        public const string Version = "public-kernel-complete-outcome-v3";
        public const string DefenseVersion = "public-kernel-single-defense-v1";
        public const int MaximumLookaheadTicks = 360;
        public string Validate() => LookaheadTicks < 1 || LookaheadTicks > MaximumLookaheadTicks ? "AI_FORECAST_SETTINGS_INVALID" : null;
        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("controller_binding.ai_forecast_version", Version);
            writer.Write("controller_binding.ai_forecast_ticks", LookaheadTicks);
            // Omitted for historical definitions: their hashes and decisions remain reproducible.
            if (ConsiderDefenses) writer.Write("controller_binding.ai_defense_version", DefenseVersion);
            if (ApproachWhenUnthreatened) writer.Write("controller_binding.ai_approach_version", AiApproachPolicy.Version);
        }
    }
    public enum AiForecastCoverage { Unspecified = 0, Complete = 1, Uncovered = 2, Rejected = 3 }
}
