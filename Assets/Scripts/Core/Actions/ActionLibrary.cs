using ProjectHero.Core.Entities;
using ProjectHero.Core.Physics;

namespace ProjectHero.Core.Actions
{
    /// <summary>
    /// A repository of predefined CombatActions.
    /// In a full game, this might be replaced by ScriptableObjects or a Database.
    /// </summary>
    public static class ActionLibrary
    {
        // Name, Time, Damage, Type, Stamina, ForceMult
        
        public static Action HeavyCharge => new Action(
            "Heavy Charge", 
            1.0f,   // Slow windup
            50f,    // High damage
            ImpactType.Blunt, 
            20f, 
            1.5f    // High impact force
        );

        public static Action QuickStab => new Action(
            "Quick Stab", 
            0.3f,   // Fast
            15f,    // Low damage
            ImpactType.Pierce, 
            5f, 
            0.5f    // Low impact force
        );

        public static Action WideSlash => new Action(
            "Wide Slash", 
            0.6f, 
            30f, 
            ImpactType.Slash, 
            15f, 
            1.0f
        );
    }
}
