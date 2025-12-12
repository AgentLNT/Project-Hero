using System.Collections.Generic;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Actions;

namespace ProjectHero.Core.Interactions
{
    /// <summary>
    /// Represents a unit's attempt to change the game state at a specific moment.
    /// This is a pure data structure used by the CombatArbiter.
    /// </summary>
    public class CombatIntent
    {
        public string ID; // Debug ID
        public CombatUnit Owner;
        public ActionType Type;
        
        // The primary target (if any)
        public CombatUnit TargetUnit;
        
        // The raw action data (e.g., the Attack Action definition)
        public object Data;

        // Callbacks for the result of the arbitration
        public System.Action OnSuccess; // Executed if the intent is not cancelled
        public System.Action<InteractionType> OnInterrupted; // Executed if the intent is cancelled by an interaction (e.g. Clash)

        public bool IsCancelled { get; private set; } = false;

        public CombatIntent(CombatUnit owner, ActionType type, object data = null)
        {
            Owner = owner;
            Type = type;
            Data = data;
            ID = $"{owner.name}_{type}_{UnityEngine.Time.frameCount}";
        }

        public void Cancel()
        {
            IsCancelled = true;
        }
    }
}