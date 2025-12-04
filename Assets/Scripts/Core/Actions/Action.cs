using ProjectHero.Core.Physics;
using ProjectHero.Core.Combat;

namespace ProjectHero.Core.Actions
{
    [System.Serializable]
    public class Action
    {
        public string Name;
        public float BaseTime;      // T_action
        public float BaseDamage;    // D_base
        public ImpactType ImpactType; // Changed from WeaponType
        public float StaminaCost;
        public float ForceMultiplier = 1.0f; // New "Knob" for designers
        public AttackPattern Pattern; // The shape of the attack

        public Action(string name, float time, float damage, ImpactType type, float stamina, float forceMult = 1.0f, AttackPattern pattern = null)
        {
            Name = name;
            BaseTime = time;
            BaseDamage = damage;
            ImpactType = type;
            StaminaCost = stamina;
            ForceMultiplier = forceMult;
            Pattern = pattern;
        }
    }
}
