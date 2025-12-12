using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Interactions;

namespace ProjectHero.Core.Actions
{
    public static class DodgeAction
    {
        public static void ScheduleDodge(BattleTimeline timeline, CombatUnit unit)
        {
            // Similar to Block, Dodge is a reaction scheduled to coincide with an attack.
            
            timeline.ScheduleEvent(0f, $"{unit.name} Dodges", () => 
            {
                var intent = new CombatIntent(unit, ActionType.Dodge)
                {
                    OnSuccess = () => 
                    {
                        Debug.Log($"[Action] {unit.name} dodges.");
                        // Trigger Dodge Animation / Movement
                    },
                    OnInterrupted = (type) => 
                    {
                        // Dodge caught?
                    }
                };
                timeline.RegisterIntent(intent);
            }, unit, 10, false, ActionType.Dodge);
        }
    }
}