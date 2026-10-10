using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Core.Compatibility.Runtime.Input
{
    /// <summary>
    /// 表现/输入层与 Logic 之间的<strong>唯一</strong>只读观察面 + 唯一写入口
    /// （任务 09「必须产出」8；00 号规则 18 的"生产者只能提交 CommandRequest"）。
    ///
    /// 边界（逐条可复核）：
    /// <list type="bullet">
    /// <item><strong>唯一写路径</strong>是 <see cref="SubmitCommand"/>：它只接受
    /// <see cref="CommandRequest"/>（只有 <c>TargetTick</c>/<c>Scope</c>/<c>Payload</c> 三样），
    /// 由宿主把它接到已注册的 <c>CommandIngressEntry.Submit</c>。
    /// 本接口<strong>没有</strong>任何直接写计划/预算/位置/预留/窗口的方法。</item>
    /// <item>其余成员全部是<strong>只读</strong>查询：当前 Tick、可编辑计划、
    /// 已公开的反应机会、Dodge 目的格合法性。它们是"提前反馈"，
    /// <strong>不是</strong>授权——确认时 Logic 必须重验。</item>
    /// <item>UI 依赖的阵营分类来自 <see cref="DecisionSnapshot.FactionResolver"/> 本身
    /// （整场唯一实例），不复制矩阵、不按 Controller/名称/Tag 猜关系。</item>
    /// <item>本接口<strong>不含</strong>任何 Legacy 具体类型（<c>CombatUnit</c>/
    /// <c>TacticsController</c>/<c>BattleTimeline</c>），因此
    /// <c>ProjectHero.Compatibility.Runtime</c> 不反向依赖旧程序集。</item>
    /// </list>
    /// </summary>
    public interface IViewLogicPort
    {
        /// <summary>权威当前 Tick（本 Tick 已完成）。设备输入默认目标 <c>CurrentTick + 1</c>。</summary>
        long CurrentTick { get; }

        /// <summary>
        /// 最近一次权威快照里的 <c>ScheduleRevision</c>。
        /// 视图把它原样填进 <c>ScheduleEditScope.ExpectedScheduleRevision</c>；
        /// 不采样、不推算、不在客户端合并——过期就由 Logic 以
        /// <c>STALE_SCHEDULE_REVISION</c> 整批拒绝。
        /// </summary>
        long ScheduleRevision { get; }

        /// <summary>战斗是否已进入终态。终态后 UI 不再产生新命令（入口也会稳定拒绝）。</summary>
        bool IsBattleEnded { get; }
        bool IsPaused => false;

        /// <summary>
        /// 唯一写入口。返回 null 表示已被入口接受并占用一个 ProducerOrdinal；
        /// 否则返回稳定拒绝记录。<strong>调用方不能填写身份/优先级/序号/费用</strong>。
        /// </summary>
        CommandIngressRejection SubmitCommand(CommandRequest request);

        /// <summary>本视图绑定的 Controller（入口身份由 Logic 派生，这里只用于过滤只读投影）。</summary>
        ControllerId ControllerId { get; }

        /// <summary>计划对所有者的可见投影（只读；不存在返回 false）。</summary>
        bool TryGetPlanSnapshot(ActionPlanId planId, out ActionPlanSnapshot plan);

        /// <summary>
        /// 计划对**当前视图 Controller** 是否可编辑（存在 + 属主受控 + 普通计划 + Editable）。
        /// 它只做提前反馈；Logic 在采纳时会用修订号/计划状态/控制权重新校验。
        /// </summary>
        bool TryFindEditablePlan(ActionPlanId planId, out ActionPlanSnapshot plan);

        /// <summary>
        /// 该 Controller 当前<strong>可编辑的普通计划</strong>投影（只读；任务 09 / B2）。
        ///
        /// 它是时间线块的唯一身份来源：块的标识<strong>就是</strong>
        /// <c>ActionPlanSnapshot.ActionPlanId</c>，<strong>不是</strong>旧时间线的
        /// <c>BattleTimeline.ReserveGroupId()</c>、更不是 <c>GetInstanceID()</c>/
        /// <c>GetEntityId()</c>/注册顺序（00 号规则 16 禁止不稳定键参与决策路径）。
        ///
        /// 过滤口径与 <see cref="TryFindEditablePlan"/> <strong>逐字相同</strong>
        /// （存在 + 属主受控 + 普通计划 + <c>Editable</c>）：Locked/Running/反应计划
        /// 天然<strong>不在</strong>这个集合里，因此"UI 上不可删/不可拖"不需要 UI 自己再判一次状态。
        /// 与 <see cref="ReactionOpportunitiesOf"/> 同一形态（显式 Controller 参数的只读枚举）。
        ///
        /// 顺序即 <c>LogicSnapshot.Plans</c> 的规范顺序（<c>ActionPlanId</c> 升序），
        /// 视图不得重排后当成权威顺序使用。
        /// </summary>
        IReadOnlyList<ActionPlanSnapshot> EditablePlansOf(ControllerId controllerId);

        /// <summary>权威动作类型（视图不复制"哪个规格是 Block、哪个是 Dodge"的表）。</summary>
        bool TryGetActionType(string actionSpecId, out ActionType actionType);

        /// <summary>该 Controller 当前可见的已公开反应机会（只读；含已关闭者用于审计展示）。</summary>
        IReadOnlyList<ReactionOpportunitySnapshot> ReactionOpportunitiesOf(ControllerId controllerId);

        /// <summary>当前已打开的回合窗口 ID（无窗口时 null）。</summary>
        WindowId? OpenWindowId { get; }

        /// <summary>
        /// Dodge 目的格合法性（<strong>只读预检</strong>，与提交共用 Logic 的同一个求值函数）：
        /// 返回 null 表示可选；否则返回稳定原因码（越界/太远/方向不被 Pattern 覆盖/被占位/被预留）。
        /// </summary>
        string DescribeDodgeDestinationRejection(UnitId defenderUnitId, ActionSpecId dodgeSpecId, GridPoint destination);
        string DescribeDodgeDestinationRejectionForOpportunity(ReactionOpportunityId opportunityId,
            UnitId defenderUnitId, ActionSpecId dodgeSpecId, GridPoint destination)
            => DescribeDodgeDestinationRejection(defenderUnitId, dodgeSpecId, destination);

        /// <summary>当前决策快照（与 AI 同时公开的同一个只读实例；可为 null）。</summary>
        DecisionSnapshot DecisionSnapshot { get; }
    }

    /// <summary>
    /// 命令构造器（<strong>窄接口</strong>）：把"已确认的原子编辑"编译为唯一合法的
    /// <see cref="CommandRequest"/>，并填充 <c>ExpectedScheduleRevision</c> / <c>ExpectedWindowId</c>。
    ///
    /// 为什么单独抽出：视图层手里只有纯数据（计划 ID、Tick、动作规格 ID），
    /// 而"预期修订号/窗口 ID"必须从权威只读快照采样。把它放在一个可注入的窄接口里，
    /// 视图就不需要认识 <c>ScheduleEditScope</c>/<c>ReactionCommandScope</c> 等 scope 类型，
    /// 也无法绕过它们自造身份字段。
    /// </summary>
    public interface IViewCommandFactory
    {
        /// <summary>为排程编辑采样 scope（<c>ExpectedScheduleRevision</c> + 可空窗口）。</summary>
        CommandRequest CreateScheduleEdit(ScheduleEditPayload payload);

        /// <summary>反应命令：<c>ReactionCommandScope(ReactionOpportunityId)</c>；不带窗口 scope。</summary>
        CommandRequest CreateReaction(ReactionOpportunityId opportunityId, ReactionCommandPayload payload);

        /// <summary>关闭自己拥有的窗口：<c>WindowCommandScope(ExpectedWindowId)</c>。</summary>
        CommandRequest CreateCloseWindow(WindowId windowId);

        /// <summary>并发行动激活：<c>WindowCommandScope(ExpectedWindowId)</c>；费用来自权威定义。</summary>
        CommandRequest CreateActivateConcurrent(WindowId windowId);
    }

    /// <summary>
    /// 端口组合：一次注入视图层需要的全部接缝，避免每个组件各自持有半个权限面。
    /// 任一项都可以为 null——此时对应功能按"不可用"处理（绝不回退到直接写逻辑）。
    /// </summary>
    public sealed class ViewInputPorts
    {
        public ViewInputPorts(IViewLogicPort logic, IViewCommandFactory commands)
        {
            Logic = logic;
            Commands = commands;
        }

        public IViewLogicPort Logic { get; }

        public IViewCommandFactory Commands { get; }

        /// <summary>提交链路完整（可构造请求 + 可提交）时才可用。</summary>
        public bool CanSubmit => Logic != null && Commands != null;
    }
}
