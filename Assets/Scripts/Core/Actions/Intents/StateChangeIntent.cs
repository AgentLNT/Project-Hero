using ProjectHero.Core.Entities;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Interactions;
using ProjectHero.Core.Timeline;
using ProjectHero.Logic.Units;
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
            // 存储为 Tick
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

            // 任务 04 兼容接缝：把同一事实同时写成新状态机状态与旧字段，
            // 让 Legacy 从属写入者与新 Logic 路径不会各自演化出不同状态。
            // 只做单向转发（旧状态名 -> UnitState），不反推、不猜默认值；
            // 未接入状态机（Legacy 模式）时本段整体跳过。
            ApplyStateToLogicStateMachine(Owner);

            if (ForceFacing.HasValue) Owner.SetFacingDirection(ForceFacing.Value);
        }

        /// <summary>
        /// 旧状态名 -> UnitState 的唯一映射落在 CombatUnit.MapLegacyStateName。
        /// "Busy" 没有对应状态（它只表示"占位不改变状态"），因此不写状态机。
        /// 转换被状态机拒绝时不抛异常也不回滚旧字段：拒绝是运行时事实
        /// （例如终态 Dead 不允许再转换），旧字段保持原值即可。
        /// </summary>
        private void ApplyStateToLogicStateMachine(CombatUnit owner)
        {
            if (owner == null || !owner.HasLogicStateMachine) return;
            if (StateName == "Busy") return;

            UnitState? mapped = CombatUnit.MapLegacyStateName(StateName);
            if (!mapped.HasValue) return;

            owner.LogicStateMachine.TryTransition(
                StateTransitionSpec.Open(mapped.Value),
                Owner.CurrentStateStartTick,
                UnitStateTransitionReasons.Explicit);
        }
    }
}
