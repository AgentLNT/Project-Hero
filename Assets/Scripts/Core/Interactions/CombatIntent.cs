using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Actions;

namespace ProjectHero.Core.Interactions
{
    /// <summary>
    /// Base class for all combat intentions.
    /// Subclasses encapsulate specific execution logic and data.
    /// </summary>
    public abstract class CombatIntent
    {
        public string ID;
        public CombatUnit Owner;
        public ActionType Type;
        public bool IsCancelled { get; private set; } = false;

        public CombatIntent(CombatUnit owner, ActionType type)
        {
            Owner = owner;
            Type = type;
            ID = $"{owner.name}_{type}_{Time.frameCount}";
        }

        public void Cancel()
        {
            IsCancelled = true;
        }

        /// <summary>
        /// Executed by the Timeline if the intent is NOT cancelled by the Arbiter.
        /// </summary>
        public abstract void ExecuteSuccess();

        /// <summary>
        /// Executed by the Arbiter if the intent IS cancelled (e.g. Clash, Parry).
        /// </summary>
        public virtual void ExecuteInterruption(InteractionType interactionType)
        {
            Debug.Log($"[Combat] {Owner.name}'s {Type} interrupted by {interactionType}");
        }
    }
}