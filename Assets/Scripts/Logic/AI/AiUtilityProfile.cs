namespace ProjectHero.Logic.AI
{
    /// <summary>V2 preferences in Q10 health / grid-distance / Tick units; scoring preferences, not combat rewards.</summary>
    public sealed record AiUtilityProfile(int Damage, int Incoming, int Position, int Interrupt, int Clash,
        int Duration = 8, int Resource = 32)
    {
        public static AiUtilityProfile For(AiPolicyKind policy) => policy switch
        {
            AiPolicyKind.SustainedOffense => new(8, 2, 256, 1024, 256),
            AiPolicyKind.SeekInterrupt => new(2, 3, 128, 65536, 24576),
            AiPolicyKind.CautiousDefense => new(1, 12, 64, 2048, 0),
            _ => throw new System.ArgumentOutOfRangeException(nameof(policy))
        };
    }
}
