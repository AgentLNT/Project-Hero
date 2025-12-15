using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Interactions;

namespace ProjectHero.Core.Actions.Intents
{
    /// <summary>
    /// Clears stagger/knockdown status and returns the unit to a normal actionable state.
    /// Intended to be uninterruptible by arbiter interactions (ActionType.None).
    /// </summary>
    public sealed class RecoverIntent : CombatIntent
    {
        public RecoverIntent(CombatUnit owner) : base(owner, ActionType.None)
        {
        }

        public override void ExecuteSuccess()
        {
            if (Owner == null) return;

            Owner.IsStaggered = false;
            Owner.IsKnockedDown = false;
            Owner.IsRecoveringAction = false;

            Debug.Log($"[Recover] {Owner.name} recovered.");
        }
    }
}
