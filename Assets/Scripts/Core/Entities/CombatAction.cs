using ProjectHero.Core.Physics;

namespace ProjectHero.Core.Entities
{
    [System.Serializable]
    public class CombatAction
    {
        public string Name;
        public float BaseTime;      // T_action
        public float BaseDamage;    // D_base
        public ImpactType ImpactType; // Changed from WeaponType
        public float StaminaCost;
        public float ForceMultiplier = 1.0f; // New "Knob" for designers

        public CombatAction(string name, float time, float damage, ImpactType type, float stamina, float forceMult = 1.0f)
        {
            Name = name;
            BaseTime = time;
            BaseDamage = damage;
            ImpactType = type;
            StaminaCost = stamina;
            ForceMultiplier = forceMult;
        }
    }
}
