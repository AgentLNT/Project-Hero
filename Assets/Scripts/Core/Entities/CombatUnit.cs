using UnityEngine;

namespace ProjectHero.Core.Entities
{
    public class CombatUnit : MonoBehaviour
    {
        [Header("Base Attributes")]
        public float Strength = 10f;
        public float Dexterity = 10f;
        public float Constitution = 10f;
        public float Wisdom = 10f;
        public float Intelligence = 10f;

        [Header("Equipment")]
        public float ArmorWeight = 10f; // W_Armor
        public float ArmorDefense = 0f; // Physical Defense
        public float MagicResistance = 0f; // Magic Defense

        [Header("State")]
        public float CurrentStamina = 100f;
        public float CurrentFocus = 0f; // Focus Points
        public float CurrentAdrenaline = 0f; // Adrenaline
        
        // Max Stamina derived from Constitution (Design Section III)
        // Formula: CON * 10 (Example: 10 CON = 100 Stamina)
        public float MaxStamina => Constitution * 10f;
        
        public bool IsStaggered;
        public bool IsKnockedDown;

        private void Start()
        {
            // Initialize stamina to max on start
            CurrentStamina = MaxStamina;
            // Focus and Adrenaline usually start at 0 or specific values
        }

        // --- Derived Stats (Design Section III) ---

        // Total Mass (M_total) = STR + CON + Armor
        // Formula approximation: Base(50) + STR*2 + CON*2 + Armor
        public float TotalMass => 50f + (Strength * 2f) + (Constitution * 2f) + ArmorWeight;

        // Swiftness (v) = DEX + STR
        // Formula approximation: DEX*1.5 + STR*0.5
        // Penalty: If Stamina < 50%, Swiftness drops.
        public float Swiftness 
        {
            get 
            {
                float baseVal = (Dexterity * 1.5f) + (Strength * 0.5f);
                if (CurrentStamina < MaxStamina * 0.5f) 
                {
                    return baseVal * 0.7f; // Exhaustion penalty
                }
                return baseVal;
            }
        }

        // Reaction (Window Width) = WIS
        public float ReactionWindow => Wisdom * 0.1f; 

        public void OnImpact(float impactVelocity, float damage)
        {
            Debug.Log($"{name} Impact Result: v_impact={impactVelocity:F2}, Damage={damage}");
            // Apply damage logic here later
        }
    }
}
