using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Physics;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Pathfinding;
using System.Threading;

namespace ProjectHero.Demos
{
    public class CombatDemo : MonoBehaviour
    {
        public CombatUnit Player;
        public CombatUnit Enemy;
        public BattleTimeline Timeline;
        public float timer = 0f;

        private void Awake()
        {
            // Position them for the demo using Grid Coordinates
            // Player at roughly -5 world x -> -10 grid x
            if (Player != null)
                Player.InitialGridPosition = new Pathfinder.GridPoint(-10, 0);
            
            // Enemy at roughly 5 world x -> 10 grid x
            if (Enemy != null)
                Enemy.InitialGridPosition = new Pathfinder.GridPoint(10, 0);

            // We don't need to set transform.position manually anymore, CombatUnit.Start() will handle it.
            // But since CombatUnit might have already Started if it was in the scene, we force update if needed.
        }

        void Start()
        {
            timer = 0;

            if (Timeline == null) Timeline = gameObject.AddComponent<BattleTimeline>();
            
            Debug.Log("--- Starting Combat Demo ---");

            // 1. Get an action from the library
            var chargeAction = ActionLibrary.HeavyCharge;

            // 2. Schedule the attack using the helper
            // This automatically schedules the "Start" and "Impact" events based on the action's BaseTime.
            // We start at T=0.5s
            AttackAction.ScheduleAttack(Timeline, Player, Enemy, chargeAction, 0.5f);

            // 3. Enemy tries to block just before impact (Impact is at 0.5 + 1.0 = 1.5s)
            Timeline.InsertReaction(1.4f, "Enemy Block Attempt", () => 
            {
                Debug.Log("Enemy raises shield!");
                // Logic to reduce damage or increase stability would go here
            }, Enemy);
            
            // 4. Enemy tries to counter-attack AFTER impact (should be cancelled if staggered)
            Timeline.ScheduleEvent(2.0f, "Enemy Counter Attack", () => 
            {
                Debug.Log("Enemy swings back!");
            }, Enemy);
        }

        void Update()
        {
            timer += Time.deltaTime;
            Timeline.AdvanceTime(timer);
        }
    }
}
