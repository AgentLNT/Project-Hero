namespace ProjectHero.Core.Interactions
{
    public enum InteractionType
    {
        None,
        Parry,      // Block vs Attack
        Dodge,      // Dodge vs Attack
        Clash,      // Attack vs Attack (Mutual)
        Intercept,  // Attack vs Move (Into Range)
        Hit         // Attack connects (Default)
    }
}