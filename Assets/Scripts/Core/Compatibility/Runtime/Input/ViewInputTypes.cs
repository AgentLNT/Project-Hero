using System;

namespace ProjectHero.Core.Compatibility.Runtime.Input
{
    /// <summary>
    /// 输入模式状态机（任务 09「必须产出」8；主方案 3.6.3）。
    ///
    /// <strong>只描述设备输入当前处在哪一步</strong>：它不表达权限、不表达计划状态、
    /// 不表达锁定线。权限的最终权威永远是 Logic（命令目标 Tick + 计划 State + 启动门禁），
    /// 本枚举只用于"提前反馈 + 决定这一次鼠标事件归谁处理"。
    ///
    /// 与主方案 3.6.3 示例的差异只在命名：<c>ReactionSelection</c> 直接采用主方案原文，
    /// 与之并列的 Dodge 目的格模式名为 <see cref="DodgeDestinationSelection"/>（任务包原文），
    /// 普通选择/取消/放置/重排按任务包原文归入本枚举。
    /// </summary>
    public enum ViewInputMode
    {
        /// <summary>无手势：只做单位选择。</summary>
        SelectUnit = 0,

        /// <summary>已选动作，等待选择方向/目标（世界侧）。</summary>
        SelectAction = 1,

        /// <summary>已确定方向/目标，等待在世界侧确认原子操作参数。</summary>
        SelectDirectionOrTarget = 2,

        /// <summary>取消手势：只回退<strong>视图</strong>状态，不产生任何战斗命令。</summary>
        CancelGesture = 3,

        /// <summary>时间线放置：本地草稿（可含多次鼠标移动/吸附/预览），确认时才编译为原子 Operations。</summary>
        TimelinePlacement = 4,

        /// <summary>时间线重排：拖动<strong>仍为 Editable 的普通计划</strong>改变其请求 Tick。</summary>
        TimelineReorder = 5,

        /// <summary>高阶反应选择：只能从<strong>已公开且仍开放</strong>的机会进入。</summary>
        ReactionSelection = 6,

        /// <summary>Dodge 目的格选择：只能选择 Logic 校验通过的离散格。</summary>
        DodgeDestinationSelection = 7
    }

    /// <summary>当前本地草稿属于哪一类手势。</summary>
    public enum ViewGestureKind
    {
        /// <summary>没有进行中的手势。</summary>
        None = 0,

        /// <summary>放置新计划（编译为 <c>AddOrdinaryPlanOperation</c>）。</summary>
        PlaceOrdinaryPlan = 1,

        /// <summary>重排仍为 Editable 的普通计划（编译为 <c>MoveEditablePlanOperation</c>）。</summary>
        ReorderEditablePlan = 2,

        /// <summary>删除仍为 Editable 的普通计划（编译为 <c>RemoveEditablePlanOperation</c>）。</summary>
        RemoveEditablePlan = 3
    }

    /// <summary>
    /// 一次放置手势的<strong>本地草稿</strong>（纯视图数据）。
    ///
    /// 关键边界（不变量 28 / 任务包「禁止事项」）：
    /// <list type="bullet">
    /// <item>它不是逻辑对象，不注册到任何 Logic 集合，不占用任何 Logic ID；</item>
    /// <item>它<strong>不</strong>进入回放输入记录（回放只记确认后的最终 Operations）；</item>
    /// <item>鼠标轨迹、吸附预览、逐帧数值只活在这里，只有 <see cref="ViewInputController.ConfirmDraft"/>
    /// 才把它编译成原子 Operations。</item>
    /// </list>
    /// </summary>
    public sealed class ViewPlacementDraft
    {
        public ViewPlacementDraft(
            long ownerUnitId, string actionSpecId, long firstRequestedStartTick,
            long targetStartTick, string insertAfterPlanId, long primaryTargetUnitId,
            int facing, bool hasPrimaryTarget)
        {
            OwnerUnitId = ownerUnitId;
            ActionSpecId = actionSpecId;
            FirstRequestedStartTick = firstRequestedStartTick;
            TargetStartTick = targetStartTick;
            InsertAfterPlanId = insertAfterPlanId;
            PrimaryTargetUnitId = primaryTargetUnitId;
            HasPrimaryTarget = hasPrimaryTarget;
            Facing = facing;
        }

        /// <summary>草稿所有者单位（确认时 Logic 会重新校验控制权）。</summary>
        public long OwnerUnitId { get; }

        /// <summary>动作规格稳定 ID（Add 的载荷字段）。</summary>
        public string ActionSpecId { get; }

        /// <summary>第一次吸附/预览落到的请求 Tick（手势起点，便于诊断）。</summary>
        public long FirstRequestedStartTick { get; }

        /// <summary>
        /// 当前预览的请求 Tick。手势期间可以多次改变（每次鼠标移动都只改这里）；
        /// <strong>目标 Tick（命令时间桶）永远是 <c>CurrentTick + 1</c></strong>，
        /// 由 <see cref="ViewInputController"/> 从权威只读来源推导，不自造 Tick 桶。
        /// </summary>
        public long TargetStartTick { get; internal set; }

        /// <summary>可空插入锚点（空串 = 无锚点，由 Logic 按 Lane 尾部处理）。</summary>
        public string InsertAfterPlanId { get; internal set; }

        /// <summary>可空主目标单位 ID（<c>0</c> = 无主目标）。</summary>
        public long PrimaryTargetUnitId { get; internal set; }

        /// <summary>是否存在显式主目标。</summary>
        public bool HasPrimaryTarget { get; internal set; }

        /// <summary>离散朝向（<c>GridDirection</c> 的整数值，视图不解释其语义）。</summary>
        public int Facing { get; internal set; }
    }

    /// <summary>
    /// 一次重排手势的本地草稿：只保存"哪个计划、从哪个 Tick 拖到哪个 Tick"。
    /// 确认时编译为 <c>MoveEditablePlanOperation</c>（同一入口、同一修订号、同一原子事务）。
    /// </summary>
    public sealed class ViewReorderDraft
    {
        public ViewReorderDraft(string planId, long originalStartTick, long targetStartTick, string insertAfterPlanId)
        {
            PlanId = planId;
            OriginalStartTick = originalStartTick;
            TargetStartTick = targetStartTick;
            InsertAfterPlanId = insertAfterPlanId;
        }

        public string PlanId { get; }

        /// <summary>手势开始时读到的权威 <c>StartTick</c>（仅用于视图回显/复位）。</summary>
        public long OriginalStartTick { get; }

        /// <summary>当前预览的请求 Tick（手势期间可变）。</summary>
        public long TargetStartTick { get; internal set; }

        /// <summary>可空插入锚点。</summary>
        public string InsertAfterPlanId { get; internal set; }
    }

    /// <summary>
    /// 反应选择（<see cref="ViewInputMode.ReactionSelection"/> / <see cref="ViewInputMode.DodgeDestinationSelection"/>）
    /// 的纯数据状态：指向一个<strong>已公开</strong>的机会与其中一项反应规格。
    /// 它<strong>没有</strong> TriggerTick 字段——触发 Tick 只能由 Logic 从来源攻击的 ImpactTick 推导。
    /// </summary>
    public sealed class ViewReactionSelection
    {
        public ViewReactionSelection(long opportunityId, long defenderUnitId, string reactionActionSpecId)
        {
            OpportunityId = opportunityId;
            DefenderUnitId = defenderUnitId;
            ReactionActionSpecId = reactionActionSpecId;
        }

        public long OpportunityId { get; }

        public long DefenderUnitId { get; }

        /// <summary>被选中的反应动作规格 ID（Block 或 Dodge）。</summary>
        public string ReactionActionSpecId { get; }

        /// <summary>Dodge 目的格（Block 恒为 null）。选择前为 null。</summary>
        public int? DestinationX { get; internal set; }

        public int? DestinationY { get; internal set; }

        public bool HasDestination => DestinationX.HasValue && DestinationY.HasValue;
    }

    /// <summary>
    /// 输入模式的只读快照。测试用它做"操作前后逐字段比较"，
    /// 也是唯一允许外部读取当前手势状态的形态（不暴露可写草稿引用）。
    /// </summary>
    public sealed class ViewInputState
    {
        public ViewInputState(
            ViewInputMode mode, ViewGestureKind gestureKind,
            long selectedUnitId, bool hasSelectedUnit,
            string selectedActionSpecId, string activeDraftPlanId,
            long draftTargetTick, long draftLastTargetTick,
            long selectedOpportunityId, string selectedReactionActionSpecId,
            bool hasDodgeDestination, int dodgeDestinationX, int dodgeDestinationY,
            bool hasPanelOpen, long lockedLineTick)
        {
            Mode = mode;
            GestureKind = gestureKind;
            SelectedUnitId = selectedUnitId;
            HasSelectedUnit = hasSelectedUnit;
            SelectedActionSpecId = selectedActionSpecId ?? string.Empty;
            ActiveDraftPlanId = activeDraftPlanId ?? string.Empty;
            DraftTargetTick = draftTargetTick;
            DraftLastTargetTick = draftLastTargetTick;
            SelectedOpportunityId = selectedOpportunityId;
            SelectedReactionActionSpecId = selectedReactionActionSpecId ?? string.Empty;
            HasDodgeDestination = hasDodgeDestination;
            DodgeDestinationX = dodgeDestinationX;
            DodgeDestinationY = dodgeDestinationY;
            HasPanelOpen = hasPanelOpen;
            LockedLineTick = lockedLineTick;
        }

        public ViewInputMode Mode { get; }

        public ViewGestureKind GestureKind { get; }

        public long SelectedUnitId { get; }

        public bool HasSelectedUnit { get; }

        public string SelectedActionSpecId { get; }

        /// <summary>进行中的重排/删除目标计划 ID（空串 = 无）。</summary>
        public string ActiveDraftPlanId { get; }

        /// <summary>放置草稿的当前预览请求 Tick（无草稿时为 0）。</summary>
        public long DraftTargetTick { get; }

        /// <summary>最近一次鼠标移动解析出的预览 Tick（用于证明逐帧预览<strong>不</strong>写逻辑）。</summary>
        public long DraftLastTargetTick { get; }

        public long SelectedOpportunityId { get; }

        public string SelectedReactionActionSpecId { get; }

        public bool HasDodgeDestination { get; }

        public int DodgeDestinationX { get; }

        public int DodgeDestinationY { get; }

        /// <summary>行动面板是否打开（纯 UI 状态；打开它不建立逻辑草稿、不暂停执行）。</summary>
        public bool HasPanelOpen { get; }

        /// <summary>锁定线显示位置 = 快照 <c>CurrentTick + 1</c>（只做提前反馈，不是权限判定）。</summary>
        public long LockedLineTick { get; }
    }

    /// <summary>一次确认尝试的结果（只用于诊断/UI 反馈；权威拒绝事实仍由入口汇总成事件）。</summary>
    public sealed class ViewSubmissionOutcome
    {
        private ViewSubmissionOutcome(
            bool submitted, string reasonCode, string rejectionReasonCode, bool planStateAllowsSubmission)
        {
            Submitted = submitted;
            ReasonCode = reasonCode;
            RejectionReasonCode = rejectionReasonCode;
            PlanStateAllowsSubmission = planStateAllowsSubmission;
        }

        /// <summary>是否真的调用了一次 <c>SubmitCommand</c>（被接受）。</summary>
        public bool Submitted { get; }

        /// <summary>视图侧稳定原因码（未提交时非空；<c>null</c> = 已提交）。</summary>
        public string ReasonCode { get; }

        /// <summary>入口返回的稳定拒绝码（入口拒绝时非空）。</summary>
        public string RejectionReasonCode { get; }

        /// <summary>提交前视图侧读到的计划状态是否允许该编辑（只做提前反馈）。</summary>
        public bool PlanStateAllowsSubmission { get; }

        public static ViewSubmissionOutcome Accepted()
            => new ViewSubmissionOutcome(true, null, null, true);

        /// <summary>已提交且视图侧事前读到的计划状态允许该编辑。</summary>
        public static ViewSubmissionOutcome Accepted(bool planStateAllowsSubmission)
            => new ViewSubmissionOutcome(true, null, null, planStateAllowsSubmission);

        /// <summary>被入口稳定拒绝：命令已发出，但 Logic 拒绝了它（此时才可能出现拒绝原因）。</summary>
        public static ViewSubmissionOutcome Rejected(string entryReasonCode, bool planStateAllows)
            => new ViewSubmissionOutcome(false, ViewInputCodes.INGRESS_REJECTED, entryReasonCode, planStateAllows);

        /// <summary>视图侧<strong>没有</strong>构造命令（例如目标不是 Editable 普通计划、草稿非法）。</summary>
        public static ViewSubmissionOutcome Blocked(string reasonCode, bool planStateAllows = false)
            => new ViewSubmissionOutcome(false, reasonCode, null, planStateAllows);
    }

    /// <summary>视图侧稳定原因码（不是 Logic 拒绝码，两者不得混用）。</summary>
    public static class ViewInputCodes
    {
        /// <summary>没有注入端口，或端口不完整。</summary>
        public const string NO_LOGIC_PORT = "VIEW_NO_LOGIC_PORT";

        /// <summary>没有选中单位。</summary>
        public const string NO_SELECTED_UNIT = "VIEW_NO_SELECTED_UNIT";

        /// <summary>单位不属于当前 Controller 可控制集合。</summary>
        public const string UNIT_NOT_CONTROLLED = "VIEW_UNIT_NOT_CONTROLLED";

        /// <summary>没有进行中的草稿。</summary>
        public const string NO_ACTIVE_DRAFT = "VIEW_NO_ACTIVE_DRAFT";

        /// <summary>草稿已经在本次手势中确认过（不重复提交）。</summary>
        public const string DRAFT_ALREADY_CONFIRMED = "VIEW_DRAFT_ALREADY_CONFIRMED";

        /// <summary>动作规格 ID 为空或不在权威定义中。</summary>
        public const string ACTION_SPEC_NOT_FOUND = "VIEW_ACTION_SPEC_NOT_FOUND";

        /// <summary>目标 Tick 越过锁定线（目标 Tick 必须 &gt;= 快照 <c>CurrentTick + 1</c>）。</summary>
        public const string TARGET_AT_OR_BEFORE_LOCKED_LINE = "VIEW_TARGET_AT_OR_BEFORE_LOCKED_LINE";

        /// <summary>计划不存在、不属于本 Controller、不是普通计划或不是 Editable。</summary>
        public const string PLAN_NOT_EDITABLE_ORDINARY = "VIEW_PLAN_NOT_EDITABLE_ORDINARY";

        /// <summary>反应机会不存在、不开放或没有该选项。</summary>
        public const string REACTION_NOT_OPEN_FOR_SELECTION = "VIEW_REACTION_NOT_OPEN_FOR_SELECTION";

        /// <summary>Block 不允许携带目的格。</summary>
        public const string BLOCK_MUST_NOT_CARRY_DESTINATION = "VIEW_BLOCK_MUST_NOT_CARRY_DESTINATION";

        /// <summary>Dodge 必须携带目的格。</summary>
        public const string DODGE_REQUIRES_DESTINATION = "VIEW_DODGE_REQUIRES_DESTINATION";

        /// <summary>命令已被入口接受，但被 Logic 稳定拒绝（附 <c>RejectionReasonCode</c>）。</summary>
        public const string INGRESS_REJECTED = "VIEW_INGRESS_REJECTED";

        /// <summary>当前没有打开的窗口，无法构造窗口类命令。</summary>
        public const string NO_OPEN_WINDOW = "VIEW_NO_OPEN_WINDOW";

        /// <summary>战斗已结束：入口立即拒绝，不进入 Tick 桶。</summary>
        public const string BATTLE_ALREADY_ENDED = "VIEW_BATTLE_ALREADY_ENDED";

        /// <summary>本地草稿被忽略（例如逐帧移动只更新预览，从不提交）。</summary>
        public const string LOCAL_DRAFT_ONLY = "VIEW_LOCAL_DRAFT_ONLY";

        /// <summary>计划 ID 文本无法解析为 <c>ActionPlanId</c>。</summary>
        public const string PLAN_ID_UNPARSEABLE = "VIEW_PLAN_ID_UNPARSEABLE";
    }
}
