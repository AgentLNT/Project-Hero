using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Turns
{
    public sealed class AdrenalineCleanupParticipant : IActionPlanCleanupParticipant
    {
        private AdrenalineLedgerRegistry _adrenaline;

        /// <summary>
        /// 回填依赖（与机会绑定参与者同一装配模式：先建参与者 → 建协调器 → 依赖就绪后回填）。
        /// 回填之前不会发生任何终态请求，因此不存在"漏清理"的中间态。
        /// </summary>
        public void Bind(AdrenalineLedgerRegistry adrenaline)
        {
            _adrenaline = adrenaline;
        }

        /// <inheritdoc />
        public int Order => ActionPlanCleanupOrder.Adrenaline;

        /// <inheritdoc />
        public string ParticipantId => "adrenaline";

        /// <inheritdoc />
        public void Cleanup(ActionPlanTerminalContext context)
        {
            if (context == null || context.Plan == null) return;
            ActionPlan plan = context.Plan;
            if (!plan.IsReaction) return;

            // 已经触发过的反应其预留早已被 SourceImpactTick 消费，因此作废是幂等无操作。
            if (context.Reason == ActionTerminationReason.SourceThreatCancelled)
                _adrenaline?.ReleaseForSourceThreatCancelled(plan.ActionPlanId);
            else
                _adrenaline?.DiscardReservation(plan.ActionPlanId);
        }
    }
}
