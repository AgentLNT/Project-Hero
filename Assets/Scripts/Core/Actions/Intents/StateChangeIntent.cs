using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Interactions;
using ProjectHero.Core.Grid;

namespace ProjectHero.Core.Actions.Intents
{
    /// <summary>
    /// Handles state transitions like Windup Start, Recovery End, or Stance changes.
    /// Usually has ActionType.None as it doesn't interact with others, but sets internal flags.
    /// </summary>
    public class StateChangeIntent : CombatIntent
    {
        public string StateName; // "Windup", "Recovery", "Idle"
        public float StaminaCost;
        public bool SetIsActing;
        public GridDirection? ForceFacing;

        public StateChangeIntent(CombatUnit owner, string stateName) : base(owner, ActionType.None)
        {
            StateName = stateName;
        }

        public override void ExecuteSuccess()
        {
            // Stamina Check
            if (StaminaCost > 0)
            {
                if (Owner.CurrentStamina < StaminaCost)
                {
                    Debug.LogWarning($"[Action] {Owner.name} not enough stamina.");
                    Owner.ResetActionState();
                    return;
                }
                Owner.CurrentStamina -= StaminaCost;
            }

            // State Flags
            if (SetIsActing) Owner.IsActing = true;
            
            switch (StateName)
            {
                case "Windup":
                    Owner.InWindup = true;
                    break;
                case "Recovery":
                    Owner.InRecovery = true;
                    Owner.InWindup = false;
                    break;
                case "Busy":
                    // Generic marker state. Actual IsActing flag is set via SetIsActing.
                    break;
                case "Idle":
                    Owner.ResetActionState();
                    break;
            }

            // Facing
            if (ForceFacing.HasValue)
            {
                Owner.SetFacingDirection(ForceFacing.Value);
            }

            Debug.Log($"[State] {Owner.name} entered state: {StateName}");
        }
    }
}
