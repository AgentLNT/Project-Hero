namespace ProjectHero.Core.Timeline
{
    /// <summary>
    /// Defines execution order for events happening at the exact same Tick.
    /// Higher values execute FIRST.
    /// </summary>
    public static class TimelinePriority
    {
        // 1. Meta / System (Start of frame cleanup, flag resets)
        public const int System = 100;

        // 2. State Changes (Movement commit, Stance changes, Windup starts)
        // Must happen before interactions so the unit is in the correct state/position.
        public const int State = 50;

        // 3. Defensive Reactions (Block start, Dodge start)
        // Must be active before the attack hits in the same frame.
        public const int Defense = 25;

        // 4. Offensive Interactions (Attack Impact, Spell Hit)
        // Happens last to query the final state of the frame.
        public const int Attack = 0;

        // 5. Post-Process (Cleanup, UI updates, Death processing)
        public const int Cleanup = -50;
    }
}
