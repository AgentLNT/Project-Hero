using System;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Turns
{
    /// <summary>
    /// <strong>排程事务的窗口权威端口</strong>（R1）。
    ///
    /// 普通计划不再有时间费用；窗口只约束两件事：
    /// <list type="number">
    /// <item><strong>谁能新增普通计划</strong>：必须存在当前开放窗口，命令显式声明的
    /// <c>ExpectedWindowId</c> 必须就是它（00 号规则 18：绝不替调用方补身份），
    /// 且发行者是窗口拥有者或持有该窗口的并发授权；</item>
    /// <item><strong>起点落在哪里</strong>：普通计划的 <c>StartTick</c> 必须落在其来源窗口
    /// <c>[OpenedAtTick, EndTick)</c> 内（00 号规则 36）。动作本身可以延伸到窗口之外。</item>
    /// </list>
    ///
    /// 它是只读校验：不写窗口、不写计划，也不发事件。排程编辑器在预览与提交两条路径上共用它。
    /// </summary>
    public interface IScheduleWindowAuthority
    {
        /// <summary>
        /// 事务执行时的当前窗口（含本 Tick 已请求关闭者；没有窗口时为 null）。
        /// 新增普通计划把它写进 <c>SubmittedWindowId</c>；能否真正提交由
        /// <see cref="ValidateNewSubmission"/> 判定。
        /// </summary>
        WindowId? CurrentWindowId { get; }

        /// <summary>
        /// 显式新增普通计划的提交校验。成功时 <paramref name="sourceWindowId"/> 为当前开放窗口
        /// （写入新计划的 <c>SubmittedWindowId</c>）；返回非 null = 稳定拒绝码。
        /// </summary>
        string ValidateNewSubmission(
            ControllerId issuer, UnitId ownerUnitId, ActionType actionType,
            WindowId? expectedWindowId, out WindowId sourceWindowId);

        /// <summary>
        /// <paramref name="startTick"/> 是否落在来源窗口区间内。返回 null = 合法；
        /// 否则为 <see cref="Timeline.ScheduleCodes.SCHEDULE_START_OUTSIDE_SOURCE_WINDOW"/>。
        /// </summary>
        string ValidateStartInSourceWindow(WindowId sourceWindowId, long startTick);
    }

    /// <summary>
    /// <see cref="IScheduleWindowAuthority"/> 的唯一实现：读同一个 <see cref="TurnWindowManager"/>
    /// 与并发授权系统，不保存第二份窗口状态。
    /// </summary>
    public sealed class TurnWindowSubmissionAuthority : IScheduleWindowAuthority
    {
        private readonly TurnWindowManager _windows;
        private readonly IActionAuthority _submissionAuthority;

        public TurnWindowSubmissionAuthority(TurnWindowManager windows, IActionAuthority submissionAuthority = null)
        {
            _windows = windows ?? throw new ArgumentNullException(nameof(windows));
            _submissionAuthority = submissionAuthority;
        }

        /// <inheritdoc />
        public WindowId? CurrentWindowId
            => _windows.CurrentWindow != null ? _windows.CurrentWindow.WindowId : (WindowId?)null;

        /// <inheritdoc />
        public string ValidateNewSubmission(
            ControllerId issuer, UnitId ownerUnitId, ActionType actionType,
            WindowId? expectedWindowId, out WindowId sourceWindowId)
        {
            sourceWindowId = default;
            TurnWindow open = _windows.OpenWindow;
            if (open == null) return TurnWindowCodes.NO_OPEN_WINDOW;
            if (!expectedWindowId.HasValue) return TurnWindowCodes.EXPECTED_WINDOW_REQUIRED;
            if (expectedWindowId.Value != open.WindowId) return TurnWindowCodes.STALE_OR_CLOSED_WINDOW;

            // 未接入授权模型（任务 05/06 装配）时保持既有语义：由调用方自行保证。
            if (_submissionAuthority != null)
            {
                if (!_windows.CanControl(issuer, ownerUnitId))
                    return TurnWindowCodes.ISSUER_CANNOT_CONTROL_UNIT;
                if (!_submissionAuthority.CanSubmitOrdinaryAction(issuer, ownerUnitId, open.WindowId, actionType))
                    return TurnWindowCodes.NO_SUBMISSION_AUTHORITY;
            }

            sourceWindowId = open.WindowId;
            return null;
        }

        /// <inheritdoc />
        public string ValidateStartInSourceWindow(WindowId sourceWindowId, long startTick)
        {
            TurnWindow window = _windows.FindWindow(sourceWindowId);
            if (window == null || !window.Contains(startTick))
                return Timeline.ScheduleCodes.SCHEDULE_START_OUTSIDE_SOURCE_WINDOW;
            return null;
        }
    }
}
