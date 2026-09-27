using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Turns
{
    /// <summary>
    /// <strong>状态感知的预算与肾上腺素终态清理参与者</strong>
    /// （任务 07「必须产出」10、13；固定槽位 <see cref="ActionPlanCleanupOrder.BudgetAndAdrenaline"/> = 600）。
    ///
    /// 它<strong>只</strong>按"计划在进入终态时的真实资源状态"结算，并且只经权威账本结算：
    /// <list type="bullet">
    /// <item><strong>普通计划在 Editable 阶段终结</strong>（从未锁定成功）⇒ 释放其<strong>未消费</strong>预留，
    /// 释放额只回到计划自己的 <c>SubmittedWindowId</c> 账本；来源窗口已关闭时只更新历史账本——
    /// 不重开提交权限、不转移给其他窗口、不前移后续计划；</item>
    /// <item><strong>普通计划已经 Locked/Running/Completed</strong> ⇒ 其 <c>Spent</c> 保持不变，
    /// 无论以自然完成、死亡、打断、抢占还是战斗结束收场都<strong>不退款</strong>；
    /// 判别依据是 <c>LockedAtTick</c>（启动门禁原子提交的唯一痕迹），
    /// <strong>不是</strong>"账本里还剩多少"——两者不一致本身就是不变量错误；</item>
    /// <item><strong>反应计划</strong> ⇒ 只有"来源威胁在 TriggerTick 前取消"才释放肾上腺素预留
    /// （由账本按 <c>ReservationCycleId</c> 决定返还 Available 还是只删除旧周期预留）；
    /// 防御者主动取消、被控制、死亡、目的格失效或已经触发一律<strong>不退款</strong>。</item>
    /// </list>
    ///
    /// 幂等性有两道保证：①账本里已经没有该计划的预留时是无操作；
    /// ②重复的终态请求本身由协调器幂等处理，且 <c>ReleasedBeforeLock</c> 只会在第一次清理时发生。
    ///
    /// 它<strong>不</strong>调用排程事务回滚接口、不伪造回滚、不重开窗口，
    /// 也不改变协调器已经写好的终态字段。
    /// </summary>
    public sealed class BudgetAndAdrenalineCleanupParticipant : IActionPlanCleanupParticipant
    {
        private TurnWindowBudgetAuthority _budget;
        private TurnWindowManager _windows;
        private AdrenalineLedgerRegistry _adrenaline;

        /// <summary>
        /// 回填依赖（与机会绑定参与者同一装配模式：先建参与者 → 建协调器 → 依赖就绪后回填）。
        /// 回填之前不会发生任何终态请求，因此不存在"漏清理"的中间态。
        /// </summary>
        public void Bind(
            TurnWindowBudgetAuthority budget, TurnWindowManager windows, AdrenalineLedgerRegistry adrenaline)
        {
            _budget = budget;
            _windows = windows;
            _adrenaline = adrenaline;
        }

        /// <inheritdoc />
        public int Order => ActionPlanCleanupOrder.BudgetAndAdrenaline;

        /// <inheritdoc />
        public string ParticipantId => "turnbudget-and-adrenaline";

        /// <inheritdoc />
        public void Cleanup(ActionPlanTerminalContext context)
        {
            if (context == null || context.Plan == null) return;
            ActionPlan plan = context.Plan;

            if (plan.IsReaction)
            {
                // 反应资源不参与窗口预算。两条终态路径互斥且都必须把预留从账本里消除
                // （00 号规则 19：Step 返回时资源预留不得引用终态计划）：
                //   · 来源威胁在触发前取消 ⇒ 按周期规则释放（**可能**返还 Available）；
                //   · 其余终态（防御者主动取消 / 被控制 / 死亡 / 目的格失效 / 已触发）⇒
                //     一律**不退款**，但也不是"留着悬挂"：删除预留、作废、不注入新周期。
                // 已经触发过的反应其预留早已被 TriggerTick 消费，因此作废是幂等无操作。
                if (context.Reason == ActionTerminationReason.SourceThreatCancelled)
                    _adrenaline?.ReleaseForSourceThreatCancelled(plan.ActionPlanId);
                else
                    _adrenaline?.DiscardReservation(plan.ActionPlanId);
                return;
            }

            if (!plan.IsOrdinary) return;
            if (_budget == null || _windows == null) return;

            // 已经锁定成功（原子启动提交把 Reserved 转成了 Spent）⇒ 终态一律不退款。
            // 判据是 LockedAtTick：初始值为 -1，"在 Tick 0 锁定"是合法的 0，
            // 因此必须用 >= 0 而不是 > 0（否则 Tick 0 启动的计划会被误判为"从未锁定"并被错误释放）。
            if (plan.LockedAtTick >= 0L) return;

            if (!plan.SubmittedWindowId.HasValue) return;
            TurnWindow window = _windows.FindWindow(plan.SubmittedWindowId.Value);
            if (window == null) return;

            int held = window.ReservedFor(plan.ActionPlanId);
            if (held <= 0) return;   // 幂等：已释放过，或本就没有预留

            var change = new TurnBudgetChangeRequest(
                plan.ActionPlanId, window.WindowId,
                newCost: 0, delta: -held, expectedReserved: held,
                isNewReservation: false, kind: TurnBudgetChangeKind.ReleasedBeforeLock);

            var changes = new[] { change };
            var budgetContext = new ScheduleBudgetContext(
                ResourceChangeSource.TerminalCleanup, _budget, null, null, default);

            string error = _budget.ValidateBudget(changes, budgetContext);
            if (error != null)
                throw new LogicDefinitionException(error,
                    "terminal budget release for plan " + plan.ActionPlanId.Value);

            _budget.ApplyBudget(changes, budgetContext, context.Tick);
            plan.ReservedTurnBudgetTicks = 0;
        }
    }
}
