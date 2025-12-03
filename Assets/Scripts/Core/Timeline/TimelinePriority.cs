namespace ProjectHero.Core.Timeline
{
    /// <summary>
    /// Defines execution order for events happening at the exact same time.
    /// Higher values execute first.
    /// </summary>
    public static class TimelinePriority
    {
        // 1. State Updates (Movement, Teleport, Spawn)
        // Must happen first so the world is in the correct state for interactions.
        public const int Movement = 20;

        // 2. Defensive Reactions (Block, Dodge stance)
        // Must happen before damage to mitigate it.
        public const int Reaction = 10;

        // 3. Offensive Interactions (Attack Impact, Spell Hit)
        // Happens last, querying the updated state.
        public const int Attack = 0;
        
        // 4. Post-Process (Cleanup, UI updates)
        public const int Cleanup = -10;
    }
}
