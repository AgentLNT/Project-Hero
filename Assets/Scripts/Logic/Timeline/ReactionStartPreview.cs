namespace ProjectHero.Logic.Timeline
{
    // Read-only timing/occupancy projection for the HUD and the following public-resource task.
    public sealed record ReactionStartPreview(long StartTick, long EndTick, long SourceImpactTick,
        long HardEndTick, long? KnownYieldTick, string RejectionCode)
    {
        public bool Succeeded => RejectionCode == null;
    }
}
