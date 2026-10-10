using System.Collections.Generic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Events
{
    public sealed record UnitCreatedEvent(long Tick, long Sequence, string SpawnId,
        UnitId UnitId, UnitDefinitionId DefinitionId, UnitId SourceUnitId, FactionId FactionId,
        GridPoint Position, GridDirection Facing) : LogicEvent(Tick, Sequence);

    public sealed record UnitCreationRejectedEvent(long Tick, long Sequence, string SpawnId,
        UnitId SourceUnitId, string RejectionCode) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 战斗结束事件。它是结束 Tick 的<strong>最后一个</strong>逻辑事件，整场只出现一次。
    /// </summary>
    public sealed record BattleEndedEvent(long Tick, long Sequence, string ResultCode)
        : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 入口级拒绝事件（任务 03「必须产出」2）。
    ///
    /// 每个非法规范键只产生<strong>一个</strong>事件（碰撞组只报告数量），
    /// 不选择、不回显任何成员载荷；事件顺序按组键
    /// （<c>SourcePriority -&gt; ControllerId(Ordinal) -&gt; ProducerOrdinal</c>）排序，
    /// 因此与碰撞成员的原始排列无关。
    ///
    /// <see cref="ProducerOrdinal"/> = 0 表示该请求从未获得本批序号
    /// （例如批次冻结后才到达的迟到请求）——此时更不会伪造 CommandSequence。
    /// </summary>
    public sealed record CommandIngressRejectedEvent(
        long Tick,
        long Sequence,
        ControllerId ControllerId,
        CommandSourceKind SourceKind,
        int SourcePriority,
        long ProducerOrdinal,
        string ReasonCode,
        int CollisionCount) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 处理器级命令拒绝（已获得 <see cref="CommandSequence"/> 之后由权威状态判定）。
    /// 与入口级拒绝的区别：入口级拒绝根本没有 CommandSequence。
    /// </summary>
    public sealed record CommandRejectedEvent(long Tick, long Sequence, long CommandSequence, string ReasonCode)
        : LogicEvent(Tick, Sequence);

    /// <summary>窗口打开。窗口只控制提交权限与预算，不拥有动作，也不是结算边界。</summary>
    public sealed record TurnWindowOpenedEvent(
        long Tick, long Sequence, WindowId WindowId, UnitId OwnerUnitId, int BudgetTicks)
        : LogicEvent(Tick, Sequence);

    /// <summary>窗口关闭。关闭不清零既有肾上腺素预留，也不取消任何计划。</summary>
    public sealed record TurnWindowClosedEvent(
        long Tick, long Sequence, WindowId WindowId, UnitId OwnerUnitId, TurnWindowCloseReason Reason)
        : LogicEvent(Tick, Sequence);

    /// <summary>
    /// TurnBudget 账本变化（任务 07「必须产出」8）。
    ///
    /// <see cref="ChangeKind"/> 区分 <c>Reserved</c>/<c>ReservationAdjusted</c>/
    /// <c>ConsumedAtLock</c>/<c>ReleasedBeforeLock</c>/<c>BattleEndCleared</c>；
    /// <see cref="Source"/> 再区分显式 <c>ScheduleEdit</c> 与 <c>SystemAutoDeferral</c>。
    /// <c>ConsumedAtLock</c> 只会在任务 05 的原子启动提交成功时出现。
    /// 事件记录 <c>WindowId</c>、<c>PlanId</c> 与 Available/Reserved/Spent 的前后值。
    /// </summary>
    public sealed record TurnBudgetChangedEvent(
        long Tick,
        long Sequence,
        WindowId WindowId,
        UnitId OwnerUnitId,
        long ActionPlanId,
        TurnBudgetChangeKind ChangeKind,
        ResourceChangeSource Source,
        int AvailableBefore,
        int AvailableAfter,
        int ReservedBefore,
        int ReservedAfter,
        int SpentBefore,
        int SpentAfter) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 肾上腺素账本变化（任务 07「必须产出」8）。
    ///
    /// <see cref="ChangeKind"/> 区分 <c>Accrued</c>/<c>Reserved</c>/<c>Consumed</c>/
    /// <c>Refunded</c>/<c>CycleReset</c>/<c>BattleEndCleared</c>（另有
    /// <c>StaleCycleReservationRemoved</c> 表示"旧周期预留只删除、不注入新周期"）。
    /// 事件记录 <c>Cycle</c>、Available 前后值、可选计划 ID 与预留量。
    /// </summary>
    public sealed record AdrenalineLedgerChangedEvent(
        long Tick,
        long Sequence,
        UnitId UnitId,
        long CycleId,
        AdrenalineChangeKind ChangeKind,
        int AvailableBefore,
        int AvailableAfter,
        long ReservationPlanId,
        int ReservationAmount) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 并发行动授权激活（任务 07「必须产出」5）。窗口关闭即撤销，但已接受计划继续存在。
    /// </summary>
    public sealed record ConcurrentActionActivatedEvent(
        long Tick, long Sequence, WindowId WindowId, UnitId PlayerUnitId, UnitId WindowOwnerUnitId)
        : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 并发行动授权撤销（窗口关闭、战斗结束或拥有者死亡）。它<strong>不</strong>取消任何已接受计划。
    /// </summary>
    public sealed record ConcurrentActionDeactivatedEvent(
        long Tick, long Sequence, WindowId WindowId, UnitId PlayerUnitId)
        : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 单位死亡（阶段 2 或阶段 15 提交）。同 Tick 致死单位先完成强制位移再从最终 footprint 移除。
    /// </summary>
    public sealed record UnitDiedEvent(long Tick, long Sequence, UnitId UnitId, int RemainingHealth)
        : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 单位状态转换（任务 04）。显式转换与自动到期<strong>走同一入口</strong>并发同类事件，
    /// 只有 <see cref="ReasonCode"/> 不同（<c>STATE_TRANSITION_EXPLICIT</c> /
    /// <c>STATE_TRANSITION_AUTOMATIC_EXPIRY</c> / <c>STATE_TRANSITION_DEATH</c>）。
    ///
    /// <see cref="DurationTicks"/> == 0 表示无限持续，此时 <see cref="EndTick"/> 为
    /// <c>long.MaxValue</c>；有限持续使用半开区间 <c>[StartTick, EndTick)</c>。
    /// 事件不携带动画时长、插值或任何表现策略（00 号规则 10）。
    /// </summary>
    public sealed record UnitStateChangedEvent(
        long Tick,
        long Sequence,
        UnitId UnitId,
        UnitState FromState,
        UnitState ToState,
        int DurationTicks,
        long EndTick,
        string ReasonCode) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 持续效果被施加到单位（任务 04；阶段 1 的状态/持续效果推进）。
    /// <see cref="EffectId"/> 是实例身份、<see cref="StatusEffectSpecId"/> 是配置类型；
    /// 二者不得混用。有限持续时间同样用半开区间 <c>[AppliedAtTick, EndTick)</c>。
    /// </summary>
    public sealed record StatusEffectAppliedEvent(
        long Tick,
        long Sequence,
        EffectId EffectId,
        UnitId UnitId,
        StatusEffectSpecId SpecId,
        long AppliedAtTick,
        long EffectSequence,
        int DurationTicks,
        long EndTick) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 持续效果被移除（任务 04）。<see cref="Expired"/> 区分"到期移除"与"显式移除"：
    /// 到期 Tick <strong>先移除</strong>，不额外触发一次效果 Tick。
    /// </summary>
    public sealed record StatusEffectRemovedEvent(
        long Tick,
        long Sequence,
        EffectId EffectId,
        UnitId UnitId,
        StatusEffectSpecId SpecId,
        bool Expired) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// 供任务 05 消费的<strong>一次性</strong>生命周期清理通知（任务 04「必须产出」4）。
    ///
    /// 死亡系统<strong>只</strong>发出本通知：它不直接修改 ActionPlan、Intent、
    /// MovementSegment、Reservation 或 ActorLane。任务 05 必须消费本通知，
    /// 按 <c>ActionPlanId</c> 通过<strong>统一终态协调器</strong>终止死者的
    /// 全部非终态计划（<c>Editable</c>、<c>Locked</c> 与 <c>Running</c>），
    /// 而不是只清理"未来计划"或直接删除从属对象。
    ///
    /// 每个单位在整场战斗中恰好产生一条（<see cref="UnitDiedEvent"/> 与本通知一一对应）。
    /// </summary>
    public sealed record UnitLifecycleCleanupNotice(
        long Tick,
        UnitId UnitId,
        string ReasonCode,
        GridPoint FinalPosition,
        int FinalHealthQ10);

    /// <summary>生命周期清理通知的稳定原因码。</summary>
    public static class UnitLifecycleNoticeReasons
    {
        /// <summary>单位在死亡阶段进入终态 Dead。</summary>
        public const string Death = "LIFECYCLE_CLEANUP_UNIT_DIED";
    }

    /// <summary>
    /// 强制位移结果事件。位置由<strong>单次</strong> <c>ApplyBatchRelocation</c> 提交后发射，
    /// 逐单位只读其最终格，不携带插值或表现参数。
    /// </summary>
    public sealed record ForcedDisplacementResolvedEvent(
        long Tick,
        long Sequence,
        UnitId TargetUnitId,
        GridPoint From,
        GridPoint To,
        GridDirection Direction,
        int RequestedSteps,
        int AppliedSteps,
        long MomentumUnits,
        long ConflictGroupKey,
        ForcedDisplacementStopReason StopReason,
        IReadOnlyList<ActionPlanId> InvalidatedPlanIds) : LogicEvent(Tick, Sequence);
}
