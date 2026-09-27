using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// <strong>启动门禁的原子提交端口</strong>（任务包「必须产出」5 第二段）。
    ///
    /// 契约：<see cref="Commit"/> 必须在<strong>同一个事务</strong>内完成
    /// "Timing/Path/Reservation 冻结 + TurnBudget <c>Reserved -&gt; Spent</c>
    /// + <c>LockedAtTick</c> + <c>Running</c>"。
    /// 任一步失败 ⇒ 全部失败，且<strong>不得</strong>留下
    /// Locked-but-not-Running、部分资源消费或 Telegraph。
    ///
    /// 实现约束（冻结）：
    /// <list type="bullet">
    /// <item>它可以<strong>读</strong>计划的全部字段（含 <c>ReservedTurnBudgetTicks</c> 与
    /// <c>BudgetCostTicks</c>），并<strong>只</strong>消费这些预留；</item>
    /// <item>它<strong>不得</strong>二次收费（门禁只把已有预留转为消费）、
    /// 不得转移来源、不得重开关闭的窗口、不得写计划的生命周期字段；</item>
    /// <item>它<strong>不得</strong>发射玩法事件——事件由 Step 在阶段 7 统一经 Outbox 发射；</item>
    /// <item>它<strong>不得</strong>在失败前留下不可回退的副作用。失败时必须先用
    /// <paramref name="rollback"/> 撤销已完成的副作用，再返回非 null 错误码。</item>
    /// </list>
    ///
    /// 任务 07 提供真实实现（TurnBudget 账本 + 肾上腺素）；任务 05 提供
    /// <see cref="NoTurnBudgetCommitPort"/> 作为默认（只把预留清零，不建模账本）。
    /// </summary>
    public interface IActionPlanStartCommitPort
    {
        /// <summary>
        /// 原子提交一个 Startable 计划的资源消费。
        /// </summary>
        /// <param name="plan">即将从 Editable 进入 Locked/Running 的计划。</param>
        /// <param name="tick">启动 Tick（= 计划的 <c>StartTick</c>）。</param>
        /// <param name="rollback">
        /// 由调用方提供：撤销本端口<strong>已经</strong>完成的全部副作用
        /// （它只撤销预留消费，不碰计划状态）。
        /// </param>
        /// <returns>null = 成功；否则为稳定错误码（调用方据此令整个提交失败）。</returns>
        string Commit(ActionPlan plan, long tick, Action<ActionPlan> rollback);
    }

    /// <summary>
    /// 任务 05 的默认端口：它把"未消费预留"清零并返回成功。
    ///
    /// 为什么这是忠实的而不是"假装"：任务 05 <strong>没有</strong> TurnBudget 账本
    /// （那是任务 07），而 <c>ActionPlan.ReservedTurnBudgetTicks</c> 是计划自带的、
    /// 任务 05 唯一拥有的预留事实。把预留 <c>Reserved -&gt; Spent</c> 的可观察结果
    /// （预留归零 + 计划进入 Running）原子完成，正是本任务能负责的全部内容；
    /// 任务 07 替换本实现即可接入真实账本，<strong>不需要</strong>改动门禁或计划。
    /// </summary>
    public sealed class NoTurnBudgetCommitPort : IActionPlanStartCommitPort
    {
        public static readonly NoTurnBudgetCommitPort Instance = new NoTurnBudgetCommitPort();

        public string Commit(ActionPlan plan, long tick, Action<ActionPlan> rollback)
        {
            if (plan == null) return ActionStartGateCommitCodes.RESOURCE_COMMIT_ERROR;
            try
            {
                plan.ReservedTurnBudgetTicks = 0;
                return null;
            }
            catch (Exception ex)
            {
                rollback?.Invoke(plan);
                return ActionStartGateCommitCodes.RESOURCE_COMMIT_ERROR + ":" + ex.GetType().Name;
            }
        }
    }

    /// <summary>
    /// 统一终态清理参与者的稳定执行顺序（显式常量；<strong>不是</strong>容器或反射注册顺序）。
    ///
    /// 顺序语义（任务包「必须产出」4）：
    /// <list type="number">
    /// <item>先阻止计划继续调度（Lane/Intent 层面的"不再产出"）；</item>
    /// <item>再清除未冻结/未来 Intent、Lane 项、机会绑定与活动索引；</item>
    /// <item>最后由任务 06/07 在同一入口接入 MovementSegment/空间 Reservation 与
    /// TurnBudget/肾上腺素预留。</item>
    /// </list>
    /// 任务 05 只登记 <see cref="PlannedIntentCleanup"/> 与 <see cref="LaneAndActiveIndexCleanup"/>
    /// 两个参与者所代表的槽位；任务 06/07 在<strong>同一入口</strong>按各自 Order 插入。
    /// </summary>
    public static class ActionPlanCleanupOrder
    {
        /// <summary>停止继续调度：把计划从"可产出 Intent"的集合中移除（最先）。</summary>
        public const int StopScheduling = 100;

        /// <summary>清除未冻结/未来 Intent（本 Tick 已冻结进冲突图的 Intent 不得删除）。</summary>
        public const int PlannedIntentCleanup = 200;

        /// <summary>清除 Lane 项与活动计划索引（计划对象仍留在注册表与历史里）。</summary>
        public const int LaneAndActiveIndexCleanup = 300;

        /// <summary>清除机会绑定（反应计划与来源威胁的绑定关系）。</summary>
        public const int OpportunityBindingCleanup = 400;

        /// <summary>任务 06：MovementSegment 与空间 Reservation。</summary>
        public const int MovementAndReservation = 500;

        /// <summary>任务 07：TurnBudget 账本与肾上腺素预留。</summary>
        public const int BudgetAndAdrenaline = 600;
    }

    /// <summary>
    /// 统一终态清理参与者。参与者<strong>显式装配且顺序固定</strong>；
    /// 它<strong>不得</strong>改变计划的终态字段（协调器已经写过），
    /// 也不得发射玩法事件（协调器负责恰好一次）。
    /// </summary>
    public interface IActionPlanCleanupParticipant
    {
        /// <summary>固定执行顺序（见 <see cref="ActionPlanCleanupOrder"/>）。</summary>
        int Order { get; }

        /// <summary>参与者标识（诊断与去重用；稳定字符串）。</summary>
        string ParticipantId { get; }

        /// <summary>幂等清理。重复调用必须是安全的无操作。</summary>
        void Cleanup(ActionPlanTerminalContext context);
    }

    /// <summary>一次终态清理的只读上下文。</summary>
    public sealed class ActionPlanTerminalContext
    {
        public ActionPlanTerminalContext(ActionPlan plan, ActionTerminationReason reason, long tick, bool firstRequest)
        {
            Plan = plan;
            Reason = reason;
            Tick = tick;
            IsFirstRequest = firstRequest;
        }

        public ActionPlan Plan { get; }

        public ActionTerminationReason Reason { get; }

        public long Tick { get; }

        /// <summary>是否为该计划的<strong>第一次</strong>终态请求（幂等重复时为 false）。</summary>
        public bool IsFirstRequest { get; }
    }

    /// <summary>
    /// 一次终态请求的结果。第一次请求胜出并返回 <see cref="EnteredTerminal"/> = true；
    /// 重复或冲突的请求<strong>幂等</strong>返回 false，且不覆盖状态、原因、TerminalTick，
    /// 不重复发事件、不重复清理。
    /// </summary>
    public sealed record ActionPlanTerminalOutcome(
        ActionPlanId ActionPlanId,
        bool EnteredTerminal,
        ActionPlanState ResultingState,
        ActionTerminationReason Reason,
        long TerminalTick)
    {
        public bool WasAlreadyTerminal => !EnteredTerminal;
    }
}
