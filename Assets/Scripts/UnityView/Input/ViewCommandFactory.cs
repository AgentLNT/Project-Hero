using System.Collections.Generic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Core.Compatibility.Runtime.Input
{
    /// <summary>
    /// <see cref="IViewCommandFactory"/> 的生产实现：把"已确认的原子编辑"编译为
    /// 唯一合法的 <see cref="CommandRequest"/>。
    ///
    /// 它是视图侧<strong>唯一</strong>接触 scope 类型的地方，因此：
    /// <list type="bullet">
    /// <item>设备输入的目标 Tick 恒为 <c>CurrentTick + 1</c>
    /// （任务包「必须产出」5：设备输入默认投递到 <c>CurrentTick + 1</c>），
    /// 视图<strong>不自造 Tick 桶</strong>；</item>
    /// <item><c>ExpectedScheduleRevision</c> 原样取自权威只读快照的
    /// <c>ScheduleRevision</c>，<strong>不在客户端合并或递增</strong>；</item>
    /// <item>窗口 scope 只在存在打开窗口时填写；没有窗口时留 <c>null</c>
    /// （纯 Move/Remove 在成本不增加时不要求原窗口仍打开），
    /// 绝不用"当前窗口"顶替。</item>
    /// </list>
    /// 视图<strong>不</strong>填写身份、来源、优先级、ProducerOrdinal、CommandSequence、
    /// 规则费用或反应 TriggerTick——这些字段在类型上就不存在。
    /// </summary>
    public sealed class ViewCommandFactory : IViewCommandFactory
    {
        private readonly IViewLogicPort _logic;

        public ViewCommandFactory(IViewLogicPort logic)
        {
            _logic = logic;
        }

        /// <summary>设备输入的默认目标 Tick（唯一推导点；不允许调用方覆盖）。</summary>
        public long NextTargetTick => _logic == null ? 0L : _logic.CurrentTick + 1L;

        public CommandRequest CreateScheduleEdit(ScheduleEditPayload payload)
        {
            if (_logic == null || _logic.IsPaused || _logic.IsBattleEnded || payload == null) return null;
            return new CommandRequest(
                NextTargetTick,
                new ScheduleEditScope(_logic.ScheduleRevision, _logic.OpenWindowId),
                payload);
        }

        public CommandRequest CreateReaction(ReactionOpportunityId opportunityId, ReactionCommandPayload payload)
        {
            if (_logic == null || _logic.IsPaused || _logic.IsBattleEnded || payload == null || !opportunityId.IsValid) return null;

            // 反应 scope 与窗口 scope 严格互斥：反应权不查当前窗口或并发授权（不变量 26 / R-6）。
            return new CommandRequest(
                NextTargetTick,
                new ReactionCommandScope(opportunityId),
                payload);
        }

        public CommandRequest CreateCloseWindow(WindowId windowId)
        {
            if (_logic == null || _logic.IsPaused || _logic.IsBattleEnded || !windowId.IsValid) return null;
            return new CommandRequest(
                NextTargetTick,
                new WindowCommandScope(windowId),
                new WindowCommandPayload(WindowCommandKind.CloseOwnWindow));
        }

        public CommandRequest CreateActivateConcurrent(WindowId windowId)
        {
            if (_logic == null || _logic.IsPaused || _logic.IsBattleEnded || !windowId.IsValid) return null;
            return new CommandRequest(
                NextTargetTick,
                new WindowCommandScope(windowId),
                new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction));
        }

        /// <summary>把一条（或一组稳定顺序的）原子操作包装成唯一合法的排程编辑载荷。</summary>
        public static ScheduleEditPayload EditPayload(params ScheduleEditOperation[] operations)
            => new ScheduleEditPayload(operations);

        /// <summary>把稳定顺序的操作列表包装成载荷（列表顺序即 Logic 看到的处理顺序）。</summary>
        public static ScheduleEditPayload EditPayload(IReadOnlyList<ScheduleEditOperation> operations)
            => new ScheduleEditPayload(operations);
    }
}
