using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Interactions;
using ProjectHero.Core.Timeline;
using UnityEngine;

namespace ProjectHero.Core.Actions.Intents
{
    public class StateChangeIntent : CombatIntent
    {
        public string StateName;
        public float StaminaCost;
        public bool SetIsActing;
        public GridDirection? ForceFacing;

        public float DurationSeconds;

        public StateChangeIntent(CombatUnit owner, string stateName, float durationSeconds = 0f) : base(owner, ActionType.None)
        {
            StateName = stateName;
            DurationSeconds = durationSeconds;
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

            var timeline = Object.FindFirstObjectByType<BattleTimeline>();
            if (timeline != null)
            {
                Owner.CurrentStateStartTick = timeline.CurrentTick;
            }
            // �洢Ϊ Tick
            Owner.CurrentStateDurationTicks = Mathf.RoundToInt(DurationSeconds * BattleTimeline.TicksPerSecond);

            switch (StateName)
            {
                case "Windup":
                    Owner.InWindup = true;
                    break;
                case "Recovery":
                    Owner.InRecovery = true;
                    Owner.InWindup = false;
                    break;
                case "Busy": break;
                case "Idle":
                    Owner.ResetActionState();
                    Owner.CurrentStateDurationTicks = 0;
                    break;
            }

            if (ForceFacing.HasValue) Owner.SetFacingDirection(ForceFacing.Value);
        }
    }
}
