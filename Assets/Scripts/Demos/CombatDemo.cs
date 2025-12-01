using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Physics;
using ProjectHero.Core.Timeline;

namespace ProjectHero.Demos
{
    public class CombatDemo : MonoBehaviour
    {
        public CombatUnit Player;
        public CombatUnit Enemy;
        public BattleTimeline Timeline;

        void Start()
        {
            if (Timeline == null) Timeline = gameObject.AddComponent<BattleTimeline>();
            
            // Create visible units with colors
            if (Player == null) Player = CreateDummyUnit("Player", 70, 10, Color.blue);
            if (Enemy == null) Enemy = CreateDummyUnit("Enemy", 90, 5, Color.red); // Heavier, slower

            // Position them for the demo
            Player.transform.position = new Vector3(-5, 0, 0);
            Enemy.transform.position = new Vector3(5, 0, 0);

            Debug.Log("--- Starting Combat Demo ---");

            // Define a "Charge" action
            // Name: Charge, Time: 1.0s, Damage: 50, Type: Blunt, Stamina: 20, ForceMult: 1.5 (Heavy Impact)
            var chargeAction = new CombatAction("Heavy Charge", 1.0f, 50f, ImpactType.Blunt, 20f, 1.5f);            // Scenario: Player charges Enemy
            Timeline.ScheduleEvent(0.5f, "Player Charge Start", () => 
            {
                Debug.Log("Player starts charging...");
                // Visual movement would happen here
            });

            Timeline.ScheduleEvent(1.5f, "Impact Moment", () => 
            {
                // Resolve collision using the Action
                PhysicsEngine.ResolveCollision(Player, Enemy, chargeAction);
            });

            // Enemy tries to block just before impact
            Timeline.InsertReaction(1.4f, "Enemy Block Attempt", () => 
            {
                Debug.Log("Enemy raises shield!");
                // Logic to reduce damage or increase stability would go here
            });
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Space))
            {
                Timeline.AdvanceTime();
            }
        }

        private CombatUnit CreateDummyUnit(string name, float mass, float dex, Color color)
        {
            // 1. Create a primitive (Capsule) so it has a Mesh and Renderer
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = name;

            // 2. Get the MeshRenderer to change the material color
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                // In 3D, we modify the Material attached to the Renderer
                // accessing .material creates a unique instance for this object
                renderer.material.color = color;
            }

            // 3. Add our combat logic component
            var unit = go.AddComponent<CombatUnit>();
            // Set attributes to derive stats
            unit.Strength = 15;
            unit.Constitution = 15;
            unit.Dexterity = dex;
            unit.ArmorWeight = mass - 50 - (15*2) - (15*2); // Reverse engineer mass for demo
            if (unit.ArmorWeight < 0) unit.ArmorWeight = 0;
            
            return unit;
        }
    }
}
