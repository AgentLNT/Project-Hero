using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Interactions;
using ProjectHero.Core.Grid;

namespace ProjectHero.Core.Actions.Intents
{
    public class StateChangeIntent : CombatIntent
    {
        public string StateName;
        public float StaminaCost;
        public bool SetIsActing;
        public GridDirection? ForceFacing;

        // [新增] 持续时间
        public float Duration;

        public StateChangeIntent(CombatUnit owner, string stateName, float duration = 0f) : base(owner, ActionType.None)
        {
            StateName = stateName;
            Duration = duration;
        }

        public override void ExecuteSuccess()
        {
            if (StaminaCost > 0)
            {
                if (Owner.CurrentStamina < StaminaCost)
                {
                    Owner.ResetActionState();
                    return;
                }
                Owner.CurrentStamina -= StaminaCost;
            }

            if (SetIsActing) Owner.IsActing = true;

            // [新增] 记录时间供 UI 使用
            Owner.CurrentStateStartTime = Time.time;
            Owner.CurrentStateDuration = Duration;

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
                    break;
                case "Idle":
                    Owner.ResetActionState();
                    Owner.CurrentStateDuration = 0f;
                    break;
            }

            if (ForceFacing.HasValue)
            {
                Owner.SetFacingDirection(ForceFacing.Value);
            }
        }
    }
}
