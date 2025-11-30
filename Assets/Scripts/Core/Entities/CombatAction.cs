using ProjectHero.Core.Physics;

namespace ProjectHero.Core.Entities
{
    [System.Serializable]
    public class CombatAction
    {
        public string Name;
        public float BaseTime;      // T_action
        public float BaseMomentum;  // P_base
        public float BaseDamage;    // D_base
        public WeaponType WeaponType;
        public float StaminaCost;

        public CombatAction(string name, float time, float momentum, float damage, WeaponType type, float stamina)
        {
            Name = name;
            BaseTime = time;
            BaseMomentum = momentum;
            BaseDamage = damage;
            WeaponType = type;
            StaminaCost = stamina;
        }
    }
}
