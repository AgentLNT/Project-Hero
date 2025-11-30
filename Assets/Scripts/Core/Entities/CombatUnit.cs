using UnityEngine;

namespace ProjectHero.Core.Entities
{
    public class CombatUnit : MonoBehaviour
    {
        [Header("Attributes")]
        public float Mass = 70f; // kg
        public float Strength = 10f;
        public float Dexterity = 10f;
        public float Constitution = 10f;
        public float Wisdom = 10f;

        [Header("State")]
        public Vector3 Velocity;
        public float CurrentStamina = 100f;
        public float MaxStamina = 100f;
        
        public bool IsStaggered;
        public bool IsKnockedDown;

        // Derived stats
        public float Swiftness => Dexterity * 1.5f + Strength * 0.5f;
        public float ReactionWindow => Wisdom * 0.1f; // Seconds

        public void ApplyForce(Vector3 force, float duration)
        {
            // F = ma -> a = F/m
            Vector3 acceleration = force / Mass;
            Velocity += acceleration * duration;
        }

        public void OnImpact(float impactVelocity, float damage)
        {
            Debug.Log($"{name} took impact: {impactVelocity} m/s, Damage: {damage}");
            // Health reduction logic here
        }
    }
}
