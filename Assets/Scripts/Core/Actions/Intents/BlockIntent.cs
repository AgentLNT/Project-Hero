using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Interactions;

namespace ProjectHero.Core.Actions.Intents
{
    public class BlockIntent : CombatIntent
    {
        public float FocusCost { get; private set; }
        public bool IsFirstInWindow { get; private set; }

        public BlockIntent(CombatUnit owner, float focusCost = 0f, bool isFirstInWindow = false) 
            : base(owner, ActionType.Block) 
        {
            FocusCost = focusCost;
            IsFirstInWindow = isFirstInWindow;
        }

        public override void ExecuteSuccess()
        {
            // Only consume Focus on the first intent in the window
            if (IsFirstInWindow)
            {
                if (Owner.CurrentFocus < FocusCost)
                {
                    Debug.LogWarning($"[Block] {Owner.name} not enough Focus ({Owner.CurrentFocus}/{FocusCost}).");
                    Owner.ResetActionState();
                    return;
                }
                Owner.CurrentFocus -= FocusCost;
                Debug.Log($"[Block] {Owner.name} consumed {FocusCost} Focus. Remaining: {Owner.CurrentFocus}");
            }

            Debug.Log($"[Action] {Owner.name} blocks successfully.");
            // Trigger Block Animation
        }
    }
}
