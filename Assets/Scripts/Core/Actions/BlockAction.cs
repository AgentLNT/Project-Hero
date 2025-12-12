using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Interactions;

namespace ProjectHero.Core.Actions
{
    public static class BlockAction
    {
        public static void ScheduleBlock(BattleTimeline timeline, CombatUnit unit, float duration)
        {
            // Block is an immediate action that lasts for a duration.
            // We register an intent that covers this duration?
            // The current Intent system is frame-based (instant resolution).
            // If Block is a state, it should probably be an event that sets a state, OR a series of intents?
            
            // Wait, the Arbiter resolves intents at a specific time.
            // If I block for 1 second, I am "Blocking" for that 1 second.
            // Any attack hitting me during that 1 second should be Parried.
            
            // Current Arbiter implementation: "We check every pair of intents... N units acting in the exact same frame".
            // This implies Intents are instantaneous events (like an Attack hitting).
            // But Block is a duration.
            
            // Solution:
            // 1. The Attack Intent happens at Time T.
            // 2. The Block Action puts the unit in a "Blocking" state.
            // 3. BUT the Arbiter compares Intents.
            // 4. Does the Blocker generate a "Block Intent" every frame? No.
            
            // Alternative:
            // The Attack Intent is compared against the Target's STATE or Active Intents.
            // If the Arbiter only looks at "Intents generated THIS frame", then Block needs to generate an intent exactly when the Attack hits.
            // This is tricky.
            
            // Re-reading the Arbiter design:
            // "It receives a list of intents for the current frame... determines interactions".
            
            // If Block is a duration, we might need a "Reaction" system where the Blocker can inject a Block Intent in response to an Attack?
            // OR, we treat Block as a persistent Intent?
            
            // Let's look at how I implemented CheckInteraction in the previous "Query" system:
            // "Query Timeline for events belonging to the target in the reaction window".
            
            // In the new Arbiter system, we only look at _pendingIntents.
            // If I Block at T=0 for 1s, and Attack hits at T=0.5s.
            // At T=0.5s, there is an Attack Intent. Is there a Block Intent? No, it was at T=0.
            
            // FIX: The Arbiter needs to know about "Active States" or "Sustained Intents".
            // OR, we stick to the "Reaction" model where the Unit (AI/Player) schedules a Block *specifically* to counter the Attack.
            // Given the "Reaction Window" concept (Wisdom), it implies the latter: You see the attack, you schedule a Block to happen at Impact Time.
            
            // So, ScheduleBlock should schedule a Block Intent at a specific time.
            // Usage: "I see an attack coming at T=5. I schedule Block at T=5."
            
            timeline.ScheduleEvent(0f, $"{unit.name} Blocks", () => 
            {
                var intent = new CombatIntent(unit, ActionType.Block)
                {
                    OnSuccess = () => 
                    {
                        Debug.Log($"[Action] {unit.name} is blocking.");
                        // Trigger Block Animation
                    },
                    OnInterrupted = (type) => 
                    {
                        // Block broken?
                    }
                };
                timeline.RegisterIntent(intent);
            }, unit, 10, false, ActionType.Block); // High priority
        }
    }
}