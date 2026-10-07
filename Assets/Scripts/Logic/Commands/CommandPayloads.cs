using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Commands
{
    /// <summary>
    /// 命令载荷标记接口。载荷只描述<strong>意图</strong>：
    /// <list type="bullet">
    /// <item>不携带任何应由权威定义读取的费用（肾上腺素、并发行动局外资源、预算 Tick）；</item>
    /// <item>不携带反应 TriggerTick（它只能由来源攻击的 ImpactTick 推导）；</item>
    /// <item>不携带 ControllerId、来源种类、来源优先级、ProducerOrdinal 或 CommandSequence。</item>
    /// </list>
    /// 因此"生产者自报身份/顺序/费用"在类型上不可能发生。
    /// <see cref="Kind"/> 必须与其 scope 的 <see cref="CommandScopeKind"/> 一致。
    /// </summary>
    public interface ICommandPayload
    {
        CommandScopeKind Kind { get; }
    }

    /// <summary>排程编辑操作判别标签。</summary>
    public enum ScheduleEditOperationKind
    {
        Add = 0,
        Move = 1,
        Remove = 2
    }

    /// <summary>
    /// 一条排程编辑操作。任务 03 只冻结<strong>判别结构</strong>与
    /// "不携带费用/反应 Tick"的边界；权威时序、路径、目标与预算字段由任务 05
    /// 在同一判别联合上扩展（新增字段必须在 <see cref="ScheduleEditScope"/> 语义内）。
    /// </summary>
    public abstract record ScheduleEditOperation
    {
        public abstract ScheduleEditOperationKind Kind { get; }

        /// <summary>本操作针对的计划（Add 时为预分配的计划 ID 或临时标识，由任务 05 冻结）。</summary>
        public abstract ActionPlanId PlanId { get; }
    }

    public sealed record ScheduleAddOperation : ScheduleEditOperation
    {
        public ScheduleAddOperation(ActionPlanId planId, long requestedStartTick)
        {
            PlanId = planId;
            RequestedStartTick = requestedStartTick;
        }

        public override ActionPlanId PlanId { get; }

        public long RequestedStartTick { get; }

        public override ScheduleEditOperationKind Kind => ScheduleEditOperationKind.Add;
    }

    public sealed record ScheduleMoveOperation : ScheduleEditOperation
    {
        public ScheduleMoveOperation(ActionPlanId planId, long requestedStartTick)
        {
            PlanId = planId;
            RequestedStartTick = requestedStartTick;
        }

        public override ActionPlanId PlanId { get; }

        public long RequestedStartTick { get; }

        public override ScheduleEditOperationKind Kind => ScheduleEditOperationKind.Move;
    }

    public sealed record ScheduleRemoveOperation : ScheduleEditOperation
    {
        public ScheduleRemoveOperation(ActionPlanId planId)
        {
            PlanId = planId;
        }

        public override ActionPlanId PlanId { get; }

        public override ScheduleEditOperationKind Kind => ScheduleEditOperationKind.Remove;
    }

    /// <summary>
    /// 【任务 05 在任务 03 冻结的同一判别联合上扩展的权威排程编辑操作】
    ///
    /// 任务 03 原文：「任务 03 只冻结判别结构与"不携带费用/反应 Tick"的边界；
    /// 权威时序、路径、目标与预算字段由任务 05 在同一判别联合上扩展
    /// （新增字段必须在 <see cref="ScheduleEditScope"/> 语义内）」。
    /// 因此这里<strong>只做纯新增</strong>，不改动既有的
    /// <see cref="ScheduleAddOperation"/>/<see cref="ScheduleMoveOperation"/>/
    /// <see cref="ScheduleRemoveOperation"/> 三个记录与
    /// <see cref="ScheduleEditPayload"/> 本身。
    ///
    /// 边界仍然成立：这些操作<strong>不</strong>携带 ControllerId、来源种类、优先级、
    /// ProducerOrdinal、CommandSequence、规则费用或反应 TriggerTick。
    ///
    /// 任务 09 A 流补齐（产出 1）：<see cref="AddOrdinaryPlanOperation"/> 的
    /// <c>RequestedStartTick</c> 为<strong>可空</strong>（<c>null</c> = 本次事务该 Lane 的尾部，
    /// 由排程事务解析，生产者不声明绝对排程）。可空插入锚点仍用
    /// <c>default(ActionPlanId)</c> 哨兵表达（<c>IsValid</c> 为 false = 无锚点，
    /// <c>HasAnchor</c> 是唯一判据）；Facing / Destination / PrimaryTargetUnitId 为判别载荷字段，
    /// 不含费用、不含反应 TriggerTick、不含任何身份或顺序。
    /// <see cref="ScheduleEditOperation.PlanId"/> 对 Add 恒为
    /// <c>default</c>（无效值）：正式 <c>ActionPlanId</c> 由 Logic 在事务校验通过后分配。
    /// </summary>
    public sealed record AddOrdinaryPlanOperation(
        long TemporaryPlanKey,
        UnitId OwnerUnitId,
        ActionSpecId ActionSpecId,
        long? RequestedStartTick = null,
        ActionPlanId AnchorAfterPlanId = default,
        UnitId? PrimaryTargetUnitId = null,
        GridDirection Facing = GridDirection.East,
        GridPoint? Destination = null) : ScheduleEditOperation
    {
        /// <summary>Add 不预分配正式计划 ID（"只有事务校验通过后才拿到 ID"）。</summary>
        public override ActionPlanId PlanId => default;

        public override ScheduleEditOperationKind Kind => ScheduleEditOperationKind.Add;

        /// <summary>是否存在显式插入锚点（把新计划插到该计划之后）。</summary>
        public bool HasAnchor => AnchorAfterPlanId.IsValid;

        /// <summary>
        /// 是否显式声明了请求起点。
        ///
        /// <c>false</c>（<c>RequestedStartTick == null</c>）表示<strong>本次事务该 Lane 的尾部</strong>：
        /// 由排程事务按"生产者不能声明绝对排程"的同一条口径解析成
        /// <c>max(目标 Tick, 该 Lane 的 LaneTailTick)</c>，绝不回退成"当前 Tick"以外的隐式默认值，
        /// 也不允许越过启动门禁或把新计划排进过去。
        /// </summary>
        public bool HasRequestedStartTick => RequestedStartTick.HasValue;
    }

    /// <summary>移动仍为 Editable 的普通计划；可选把目标位置锚定在另一个计划之后。</summary>
    /// <remarks>
    /// 显式构造 + <c>override PlanId { get; }</c>，与同文件的
    /// <see cref="ScheduleMoveOperation"/> 完全同形。
    /// <strong>不得</strong>改回位置记录参数（<c>record MoveEditablePlanOperation(ActionPlanId PlanId, ...)</c>）：
    /// 那会让编译器合成带 <c>init</c> 访问器的覆盖属性，而基类
    /// <see cref="ScheduleEditOperation.PlanId"/> 只有 <c>get</c>，从而产生 CS0546 编译错误。
    /// </remarks>
    public sealed record MoveEditablePlanOperation : ScheduleEditOperation
    {
        public MoveEditablePlanOperation(
            ActionPlanId planId, long requestedStartTick, ActionPlanId anchorAfterPlanId = default)
        {
            PlanId = planId;
            RequestedStartTick = requestedStartTick;
            AnchorAfterPlanId = anchorAfterPlanId;
        }

        public override ActionPlanId PlanId { get; }

        public long RequestedStartTick { get; }

        public ActionPlanId AnchorAfterPlanId { get; }

        public override ScheduleEditOperationKind Kind => ScheduleEditOperationKind.Move;

        public bool HasAnchor => AnchorAfterPlanId.IsValid;
    }

    /// <summary>删除仍为 Editable 的普通计划；必须给出统一终态协调器使用的终止原因。</summary>
    /// <remarks>同 <see cref="MoveEditablePlanOperation"/>：必须显式覆盖 <c>PlanId</c>，不得用位置记录参数。</remarks>
    public sealed record RemoveEditablePlanOperation : ScheduleEditOperation
    {
        public RemoveEditablePlanOperation(
            ActionPlanId planId,
            ActionTerminationReason terminationReason = ActionTerminationReason.CancelledByCommand)
        {
            PlanId = planId;
            TerminationReason = terminationReason;
        }

        public override ActionPlanId PlanId { get; }

        public ActionTerminationReason TerminationReason { get; }

        public override ScheduleEditOperationKind Kind => ScheduleEditOperationKind.Remove;
    }

    /// <summary>排程编辑载荷：操作列表非空。要求 <see cref="ScheduleEditScope"/>。</summary>
    public sealed record ScheduleEditPayload(IReadOnlyList<ScheduleEditOperation> Operations) : ICommandPayload
    {
        public CommandScopeKind Kind => CommandScopeKind.ScheduleEdit;
    }

    /// <summary>窗口命令种类。</summary>
    public enum WindowCommandKind
    {
        /// <summary>关闭自己拥有的窗口（正式关闭在战斗仍继续时的第 17 阶段执行）。</summary>
        CloseOwnWindow = 0,

        /// <summary>在其他单位窗口期间激活一次并发行动（费用来自权威定义）。</summary>
        ActivateConcurrentAction = 1
    }

    /// <summary>窗口载荷：关窗/并发激活。要求 <see cref="WindowCommandScope"/>。</summary>
    public sealed record WindowCommandPayload(WindowCommandKind WindowKind) : ICommandPayload
    {
        public CommandScopeKind Kind => CommandScopeKind.Window;
    }

    /// <summary>高阶反应种类（首版只有这两种；它们的 TriggerTick 只能由规则推导）。</summary>
    public enum ReactionCommandKind
    {
        Block = 0,
        Dodge = 1
    }

    /// <summary>
    /// 反应载荷：Block/Dodge。要求 <see cref="ReactionCommandScope"/>。
    /// <list type="bullet">
    /// <item>没有 TriggerTick 字段——它等于来源攻击的 <c>ImpactTick</c>，生产者无法声明。</item>
    /// <item>没有费用字段——Block/Dodge 的肾上腺素费用来自权威动作定义。</item>
    /// <item>Dodge 必须给出目的格；Block 不得携带目的格。</item>
    /// </list>
    /// </summary>
    public sealed record ReactionCommandPayload(
        ReactionCommandKind ReactionKind,
        ActionSpecId ReactionActionSpecId,
        GridPoint? DodgeDestination = null) : ICommandPayload
    {
        public CommandScopeKind Kind => CommandScopeKind.Reaction;
    }
}
