using System.Collections.Generic;
using ProjectHero.Logic.Actions;

namespace ProjectHero.Logic.Events
{
    /// <summary>
    /// 计划生命周期事件族（任务包「必须产出」8 / §11）。
    ///
    /// 边界与 <see cref="LogicEvent"/> 完全一致：只携带不可变值与稳定 ID，
    /// 不携带任何表现策略，不持有 MonoBehaviour / ScriptableObject / 视图引用。
    /// 全部事件都只在<strong>已经提交</strong>的事实上发射。
    /// </summary>
    public sealed record ActionPlanCreatedEvent(
        long Tick,
        long Sequence,
        long ActionPlanId,
        int Origin,
        long OwnerUnitId,
        string ActionSpecId,
        int ActionType,
        long StartTick,
        long EndTick,
        long LastRequestedStartTick,
        long CreatedAtTick,
        long ScheduleRevision) : LogicEvent(Tick, Sequence);

    /// <summary>普通计划在启动门禁的原子提交里建立 Locked 边界并立即 Running。</summary>
    public sealed record ActionPlanLockedEvent(
        long Tick,
        long Sequence,
        long ActionPlanId,
        long OwnerUnitId,
        long StartTick,
        long LockedAtTick,
        long ScheduleRevision) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 系统自动延期（任务包「必须产出」5 第二段）。必须包含：
    /// 阻塞原因码、旧/新 <c>StartTick</c>、<c>RetryAtTick</c>、延期次数、
    /// <strong>稳定 ripple PlanId 列表</strong>与<strong>提交后修订号</strong>。
    /// </summary>
    public sealed record ActionPlanAutoDeferredEvent(
        long Tick,
        long Sequence,
        long ActionPlanId,
        long OwnerUnitId,
        string BlockerReasonCode,
        long OldStartTick,
        long NewStartTick,
        long RetryAtTick,
        int AutomaticDeferralCount,
        IReadOnlyList<long> RipplePlanIds,
        long ScheduleRevision) : LogicEvent(Tick, Sequence);

    /// <summary>带明确原因的计划终止（只能由统一终态协调器触发）。</summary>
    public sealed record ActionPlanTerminatedEvent(
        long Tick,
        long Sequence,
        long ActionPlanId,
        long OwnerUnitId,
        int Reason,
        string ReasonCode,
        long TerminalTick,
        long ScheduleRevision) : LogicEvent(Tick, Sequence);

    /// <summary>自然完成（没有终止原因）。</summary>
    public sealed record ActionPlanCompletedEvent(
        long Tick,
        long Sequence,
        long ActionPlanId,
        long OwnerUnitId,
        long TerminalTick,
        long ScheduleRevision) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 反应机会公开（任务包「必须产出」11）。<see cref="PublishedOptionSpecIds"/> 只列出
    /// 真正公开的选项（已满足 <c>ResponseDeadlineTick &gt;= TelegraphTick + CommandIngressLeadTicks</c>），
    /// 按 <c>ActionSpecId</c> Ordinal 升序。
    /// </summary>
    public sealed record ReactionOpportunityOpenedEvent(
        long Tick,
        long Sequence,
        long ReactionOpportunityId,
        long SourceAttackPlanId,
        long DefenderUnitId,
        long TelegraphTick,
        long TriggerTick,
        IReadOnlyList<string> PublishedOptionSpecIds) : LogicEvent(Tick, Sequence);

    /// <summary>第一次离开 <c>Open</c> 时恰好发射一次。</summary>
    public sealed record ReactionOpportunityClosedEvent(
        long Tick,
        long Sequence,
        long ReactionOpportunityId,
        long SourceAttackPlanId,
        long DefenderUnitId,
        string CloseReason,
        long ClosedAtTick,
        long BoundActionPlanId) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 单个选项过期。每个选项在其截止 Tick 的命令阶段结束后<strong>只发一次</strong>；
    /// <see cref="ClosedOpportunity"/> 表示这是最后一个仍然开放的选项（该次过期关闭了机会）。
    /// </summary>
    public sealed record ReactionOptionExpiredEvent(
        long Tick,
        long Sequence,
        long ReactionOpportunityId,
        long DefenderUnitId,
        string ActionSpecId,
        long ResponseDeadlineTick,
        bool ClosedOpportunity) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 反应<strong>实际触发</strong>。它必须等到任务 06/08 的 TriggerTick 位置事务成功之后才发射：
    /// "生成反应 Intent"不等于"实际触发"，不允许伪造。
    /// </summary>
    public sealed record ReactionTriggeredEvent(
        long Tick,
        long Sequence,
        long ReactionOpportunityId,
        long ReactionActionPlanId,
        long SourceAttackPlanId,
        long DefenderUnitId,
        long TriggerTick,
        int ReactionType) : LogicEvent(Tick, Sequence);
}
