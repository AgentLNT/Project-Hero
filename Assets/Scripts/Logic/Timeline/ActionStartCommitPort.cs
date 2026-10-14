using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// 统一终态清理参与者的稳定执行顺序（显式常量；<strong>不是</strong>容器或反射注册顺序）。
    ///
    /// 顺序语义（任务包「必须产出」4）：
    /// <list type="number">
    /// <item>先阻止计划继续调度（Lane/Intent 层面的"不再产出"）；</item>
    /// <item>再清除未冻结/未来 Intent、Lane 项、机会绑定与活动索引；</item>
    /// <item>最后由任务 06/07 在同一入口接入 MovementSegment/空间 Reservation 与肾上腺素预留。</item>
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

        public const int Adrenaline = 600;
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
