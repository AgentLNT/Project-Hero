using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Core.Compatibility.Runtime.Input
{
    /// <summary>
    /// 输入模式状态机 + 本地草稿 → 原子 Operations 编译层（任务 09「必须产出」8）。
    ///
    /// <strong>它是视图对象，不是逻辑对象</strong>：类中没有任何 <c>UnityEditor</c>、
    /// 没有 <c>MonoBehaviour</c>、没有任何对 ActionPlan/Intent/MovementSegment/
    /// Reservation/预算/位置/窗口的写方法。它唯一能触碰 Logic 的路径是
    /// <see cref="IViewLogicPort.SubmitCommand"/>，且只接受
    /// <see cref="CommandRequest"/>（只有 <c>TargetTick</c>/<c>Scope</c>/<c>Payload</c> 三样）。
    ///
    /// 语义要点（逐条对应任务包「核心命令语义」）：
    /// <list type="bullet">
    /// <item><strong>默认目标 Tick = CurrentTick + 1</strong>：由 <see cref="IViewLogicPort.CurrentTick"/>
    /// 推导（<see cref="IViewCommandFactory"/> 计算），视图<strong>不自造 Tick 桶</strong>，
    /// 也不把过期命令顺延到当前 Tick。</item>
    /// <item><strong>本地草稿可含多次鼠标移动/吸附/预览</strong>：每次
    /// <see cref="PreviewPlacementTick"/> 只改视图状态，<strong>零</strong> Logic 写入、
    /// 零命令提交；只有 <see cref="ConfirmDraft"/> 才产生一次原子 Operations 提交。</item>
    /// <item><strong>手势取消/返回/关闭面板只是视图状态</strong>：<see cref="CancelGesture"/>
    /// 不写逻辑、不暂停执行、不产生命令。</item>
    /// <item><strong>锁定线显示 = 快照 CurrentTick + 1</strong>：<see cref="LockedLineTick"/>
    /// 只做提前反馈；越线编辑由 Logic 以命令目标 Tick、计划 State 与启动门禁最终拒绝。</item>
    /// <item><strong>Block/Dodge 不会出现在普通时间线的 Move/Remove/TriggerTick 编辑面</strong>：
    /// 重排/删除只接受 Editable <strong>普通</strong>计划；反应载荷类型上没有 TriggerTick 字段。</item>
    /// </list>
    /// </summary>
    public sealed class ViewInputController
    {
        private ViewInputPorts _ports;

        private ViewPlacementDraft _placementDraft;
        private ViewReorderDraft _reorderDraft;
        private long _removeDraftPlanId;
        private bool _lastDraftConfirmed;

        private ViewReactionSelection _reactionSelection;

        private long _selectedUnitId;
        private bool _hasSelectedUnit;
        private string _selectedActionSpecId = string.Empty;

        private ViewInputMode _mode = ViewInputMode.SelectUnit;
        private readonly List<ViewInputMode> _modeHistory = new List<ViewInputMode>();

        private bool _panelOpen;
        private bool _panelEverOpened;
        private long _lastPreviewedTick;
        private int _previewStepCount;
        private int _confirmedGestureCount;
        private ViewSubmissionOutcome _lastOutcome;

        public ViewInputController()
            : this(null)
        {
        }

        public ViewInputController(ViewInputPorts ports)
        {
            _ports = ports;
        }

        /// <summary>宿主（New 驱动器 / 旧壳适配器）注入端口；可在战斗中重绑定。</summary>
        public void BindPorts(ViewInputPorts ports) => _ports = ports;

        public bool HasPorts => _ports != null && _ports.CanSubmit;

        public IViewLogicPort Logic => _ports != null ? _ports.Logic : null;

        public IViewCommandFactory Commands => _ports != null ? _ports.Commands : null;

        // ————————————————————————————————————————————————————————————
        // 只读视图状态
        // ————————————————————————————————————————————————————————————

        public ViewInputMode CurrentMode => _mode;

        public ViewGestureKind ActiveGesture => ActiveGestureOf();

        public bool HasSelectedUnit => _hasSelectedUnit;

        public long SelectedUnitId => _selectedUnitId;

        public string SelectedActionSpecId => _selectedActionSpecId;

        public bool HasActiveDraft => _placementDraft != null || _reorderDraft != null || _removeDraftPlanId > 0L;

        public bool HasPlacementDraft => _placementDraft != null;

        public bool HasReorderDraft => _reorderDraft != null;

        public bool HasRemoveDraft => _removeDraftPlanId > 0L;

        public bool HasReactionSelection => _reactionSelection != null;

        /// <summary>行动面板是否打开（纯视图状态）。</summary>
        public bool IsPanelOpen => _panelOpen;

        /// <summary>行动面板是否被打开过（用于证明"打开面板"本身不建逻辑草稿）。</summary>
        public bool PanelEverOpened => _panelEverOpened;

        /// <summary>
        /// 锁定线显示 Tick（= 快照 <c>CurrentTick + 1</c>）。
        /// <strong>它不是权限判定</strong>：越线编辑的最终拒绝来自 Logic。
        /// </summary>
        public long LockedLineTick
        {
            get
            {
                IViewLogicPort logic = Logic;
                return logic == null ? 0L : logic.CurrentTick + 1L;
            }
        }

        /// <summary>本次手势已经解析过多少次鼠标移动/吸附预览（用于证明逐帧预览不提交）。</summary>
        public int PreviewStepCount => _previewStepCount;

        /// <summary>本次会话已经确认（并成功发声）过多少次手势。</summary>
        public int ConfirmedGestureCount => _confirmedGestureCount;

        public ViewSubmissionOutcome LastOutcome => _lastOutcome;

        /// <summary>
        /// 是否处于"吞掉单位点击"的视图模式。它<strong>只</strong>是视图路由判断，
        /// 不再通过旧 <c>InputManager.IgnoreUnitClicks</c> 布尔量控制任何逻辑路径。
        /// </summary>
        public bool SuppressesUnitClicks
            => _mode == ViewInputMode.SelectAction
               || _mode == ViewInputMode.SelectDirectionOrTarget
               || _mode == ViewInputMode.TimelinePlacement
               || _mode == ViewInputMode.TimelineReorder
               || _mode == ViewInputMode.ReactionSelection
               || _mode == ViewInputMode.DodgeDestinationSelection;

        /// <summary>是否处于"吞掉世界点击"的视图模式（放置/重排/反应选择期间世界点击不生效）。</summary>
        public bool SuppressesWorldClicks
            => _mode == ViewInputMode.TimelinePlacement
               || _mode == ViewInputMode.TimelineReorder
               || _mode == ViewInputMode.ReactionSelection
               || _mode == ViewInputMode.DodgeDestinationSelection;

        /// <summary>当前模式是否允许开始选择单位（放置/重排/反应选择期间不允许）。</summary>
        public bool IsUnitSelectionAllowed
            => _mode == ViewInputMode.SelectUnit
               || _mode == ViewInputMode.SelectAction
               || _mode == ViewInputMode.SelectDirectionOrTarget
               || _mode == ViewInputMode.CancelGesture;

        public ViewInputState CaptureState()
        {
            long draftTick = _placementDraft != null
                ? _placementDraft.TargetStartTick
                : (_reorderDraft != null ? _reorderDraft.TargetStartTick : 0L);

            return new ViewInputState(
                _mode,
                ActiveGestureOf(),
                _selectedUnitId,
                _hasSelectedUnit,
                _selectedActionSpecId,
                ActiveDraftPlanIdText(),
                draftTick,
                _lastPreviewedTick,
                _reactionSelection != null ? _reactionSelection.OpportunityId : 0L,
                _reactionSelection != null ? _reactionSelection.ReactionActionSpecId : string.Empty,
                _reactionSelection != null && _reactionSelection.HasDestination,
                _reactionSelection != null && _reactionSelection.DestinationX.HasValue ? _reactionSelection.DestinationX.Value : 0,
                _reactionSelection != null && _reactionSelection.DestinationY.HasValue ? _reactionSelection.DestinationY.Value : 0,
                _panelOpen,
                LockedLineTick);
        }

        // ————————————————————————————————————————————————————————————
        // 单位 / 动作 / 面板（纯视图）
        // ————————————————————————————————————————————————————————————

        /// <summary>选中一个单位。<strong>不写任何逻辑状态</strong>。</summary>
        public void SelectUnit(UnitId unitId)
        {
            if (!unitId.IsValid) return;
            SelectUnitForView(unitId.Value);
        }

        /// <summary>
        /// 用<strong>视图侧</strong>单位标识选中（旧 <c>CombatUnit</c> 尚无 Logic <c>UnitId</c> 字段，
        /// 迁移期由宿主提供映射）。它同样不写任何逻辑状态；
        /// 真正的 <c>UnitId</c> 会在确认提交时由命令载荷携带，并由 Logic 重新校验控制权。
        /// </summary>
        public void SelectUnitForView(long unitId)
        {
            if (unitId <= 0L) return;
            if (!IsUnitSelectionAllowed) return;
            _selectedUnitId = unitId;
            _hasSelectedUnit = true;
            _selectedActionSpecId = string.Empty;
            EnterMode(ViewInputMode.SelectAction);
        }

        /// <summary>清除单位选择（视图状态）。</summary>
        public void ClearSelection()
        {
            _selectedUnitId = 0L;
            _hasSelectedUnit = false;
            _selectedActionSpecId = string.Empty;
            DiscardDrafts();
            ClearReactionSelection();
            EnterMode(ViewInputMode.SelectUnit);
        }

        /// <summary>选择一个动作规格（视图侧只存稳定 ID；权限由 Logic 在确认时重验）。</summary>
        public void SelectAction(string actionSpecId)
        {
            if (string.IsNullOrEmpty(actionSpecId)) return;
            _selectedActionSpecId = actionSpecId;
            EnterMode(ViewInputMode.SelectAction);
        }

        /// <summary>进入"选择方向/目标"。</summary>
        public void EnterDirectionTargetSelection()
        {
            if (!_hasSelectedUnit) return;
            EnterMode(ViewInputMode.SelectDirectionOrTarget);
        }

        /// <summary>打开行动面板：<strong>纯视图状态</strong>——不建立逻辑草稿、不暂停执行。</summary>
        public void OpenActionPanel()
        {
            _panelOpen = true;
            _panelEverOpened = true;
            if (!_hasSelectedUnit) EnterMode(ViewInputMode.SelectUnit);
        }

        /// <summary>关闭行动面板：同样是纯视图状态。</summary>
        public void CloseActionPanel()
        {
            _panelOpen = false;
        }

        // ————————————————————————————————————————————————————————————
        // 手势取消 / 模式栈
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 取消当前手势（右键 / 返回 / 关闭面板）：<strong>只</strong>回退视图状态。
        /// 不提交命令、不写逻辑、不暂停执行、不影响回放输入记录。
        /// </summary>
        public void CancelGesture()
        {
            DiscardDrafts();
            ClearReactionSelection();
            _panelOpen = false;
            _selectedActionSpecId = string.Empty;

            if (_modeHistory.Count > 0)
            {
                _mode = _modeHistory[_modeHistory.Count - 1];
                _modeHistory.RemoveAt(_modeHistory.Count - 1);
            }
            else
            {
                _mode = _hasSelectedUnit ? ViewInputMode.SelectAction : ViewInputMode.SelectUnit;
            }
            if (_mode == ViewInputMode.CancelGesture) _mode = ViewInputMode.SelectUnit;
        }

        private void EnterMode(ViewInputMode mode)
        {
            if (_mode == mode) return;
            _modeHistory.Add(_mode);
            if (_modeHistory.Count > 16) _modeHistory.RemoveAt(0);
            _mode = mode;
        }

        private ViewGestureKind ActiveGestureOf()
        {
            if (_placementDraft != null) return ViewGestureKind.PlaceOrdinaryPlan;
            if (_reorderDraft != null) return ViewGestureKind.ReorderEditablePlan;
            if (_removeDraftPlanId > 0L) return ViewGestureKind.RemoveEditablePlan;
            return ViewGestureKind.None;
        }

        private string ActiveDraftPlanIdText()
        {
            if (_reorderDraft != null) return _reorderDraft.PlanId;
            if (_removeDraftPlanId > 0L)
                return _removeDraftPlanId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.Empty;
        }

        private void DiscardDrafts()
        {
            _placementDraft = null;
            _reorderDraft = null;
            _removeDraftPlanId = 0L;
            _lastPreviewedTick = 0L;
            _previewStepCount = 0;
        }

        // ————————————————————————————————————————————————————————————
        // 本地草稿：放置 / 重排 / 删除
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 开始一次放置手势，建立<strong>本地草稿</strong>。
        ///
        /// <paramref name="requestedStartTick"/> 是第一次吸附/预览落到的请求 Tick（视图给出）；
        /// 本次确认的<strong>目标 Tick</strong>仍然是 <c>CurrentTick + 1</c>，与它无关。
        /// </summary>
        public bool BeginPlacement(
            UnitId owner, string actionSpecId, long requestedStartTick,
            string insertAfterPlanId = null, long primaryTargetUnitId = 0L,
            int facing = (int)GridDirection.East)
        {
            if (!_hasSelectedUnit || !owner.IsValid) return false;
            if (string.IsNullOrEmpty(actionSpecId)) return false;

            _placementDraft = new ViewPlacementDraft(
                owner.Value, actionSpecId, requestedStartTick, requestedStartTick,
                insertAfterPlanId ?? string.Empty, primaryTargetUnitId, facing, primaryTargetUnitId > 0L);
            _reorderDraft = null;
            _removeDraftPlanId = 0L;
            _lastDraftConfirmed = false;
            _lastPreviewedTick = requestedStartTick;
            _previewStepCount = 0;
            EnterMode(ViewInputMode.TimelinePlacement);
            return true;
        }

        /// <summary>开始一次重排手势（目标必须是 Editable 普通计划；只读判定，失败即不进入手势）。</summary>
        public bool BeginReorder(ActionPlanId planId, long requestedStartTick)
        {
            if (!CanEditEditableOrdinary(planId)) return false;

            _reorderDraft = new ViewReorderDraft(
                PlanIdText(planId), requestedStartTick, requestedStartTick, string.Empty);
            _placementDraft = null;
            _removeDraftPlanId = 0L;
            _lastDraftConfirmed = false;
            _lastPreviewedTick = requestedStartTick;
            _previewStepCount = 0;
            EnterMode(ViewInputMode.TimelineReorder);
            return true;
        }

        /// <summary>开始一次删除手势（目标必须是 Editable 普通计划；Locked/Running/反应计划返回 false）。</summary>
        public bool BeginRemoval(ActionPlanId planId)
        {
            if (!CanEditEditableOrdinary(planId)) return false;

            _removeDraftPlanId = planId.Value;
            _placementDraft = null;
            _reorderDraft = null;
            _lastDraftConfirmed = false;
            EnterMode(ViewInputMode.TimelineReorder);
            return true;
        }

        /// <summary>
        /// 一次鼠标移动/吸附预览：只改草稿的预览 Tick。<strong>零命令、零逻辑写入</strong>。
        /// 返回值表示"这次预览是否仍在允许区间内"（提前反馈，不是权限）。
        /// </summary>
        public bool PreviewPlacementTick(long requestedStartTick)
        {
            if (_placementDraft == null && _reorderDraft == null) return false;

            _lastPreviewedTick = requestedStartTick;
            _previewStepCount++;

            if (_placementDraft != null) _placementDraft.TargetStartTick = requestedStartTick;
            else _reorderDraft.TargetStartTick = requestedStartTick;

            // 逐帧预览是纯本地事实：记录下来，供"草稿不进回放/不进逻辑"的观测面使用。
            _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.LOCAL_DRAFT_ONLY);

            return !IsAtOrBeforeLockedLine(requestedStartTick);
        }

        /// <summary>把候选 Tick 吸附到锁定线之上（视图侧提前反馈；Logic 才是最终权威）。</summary>
        public long SnapToEditableBoundary(long candidateTick)
        {
            long floor = LockedLineTick;
            return candidateTick < floor ? floor : candidateTick;
        }

        /// <summary>视图侧放置校验（提前反馈）。返回 null 表示"看起来可以确认"。</summary>
        public string EvaluatePlacement(long requestedStartTick)
        {
            if (_placementDraft == null) return ViewInputCodes.NO_ACTIVE_DRAFT;
            if (IsAtOrBeforeLockedLine(requestedStartTick))
                return ViewInputCodes.TARGET_AT_OR_BEFORE_LOCKED_LINE;
            return null;
        }

        /// <summary>视图侧重排校验（提前反馈）。</summary>
        public string EvaluateReorder(ActionPlanId planId, long requestedStartTick)
        {
            if (_reorderDraft == null || _reorderDraft.PlanId != PlanIdText(planId))
                return ViewInputCodes.NO_ACTIVE_DRAFT;
            if (!CanEditEditableOrdinary(planId)) return ViewInputCodes.PLAN_NOT_EDITABLE_ORDINARY;
            if (IsAtOrBeforeLockedLine(requestedStartTick))
                return ViewInputCodes.TARGET_AT_OR_BEFORE_LOCKED_LINE;
            return null;
        }

        /// <summary>视图侧删除校验（提前反馈）。</summary>
        public string EvaluateRemoval(ActionPlanId planId)
        {
            if (_removeDraftPlanId != planId.Value) return ViewInputCodes.NO_ACTIVE_DRAFT;
            return CanEditEditableOrdinary(planId) ? null : ViewInputCodes.PLAN_NOT_EDITABLE_ORDINARY;
        }

        private bool IsAtOrBeforeLockedLine(long tick)
        {
            IViewLogicPort logic = Logic;
            if (logic == null) return true;
            return tick < logic.CurrentTick + 1L;
        }

        private bool CanEditEditableOrdinary(ActionPlanId planId)
        {
            IViewLogicPort logic = Logic;
            if (logic == null || !planId.IsValid) return false;
            ActionPlanSnapshot ignored;
            return logic.TryFindEditablePlan(planId, out ignored);
        }

        private static string PlanIdText(ActionPlanId planId)
            => planId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // ————————————————————————————————————————————————————————————
        // 确认：唯一产生命令的路径
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 确认当前草稿 → 编译为<strong>原子 Operations</strong> → 经唯一入口提交。
        /// 这是本地草稿进入逻辑世界的<strong>唯一</strong>出口。
        /// </summary>
        public ViewSubmissionOutcome ConfirmDraft()
        {
            if (_lastDraftConfirmed)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.DRAFT_ALREADY_CONFIRMED);
                return _lastOutcome;
            }
            if (_placementDraft != null) return ConfirmPlacementDraft();
            if (_reorderDraft != null) return ConfirmReorderDraft();
            if (_removeDraftPlanId > 0L) return ConfirmRemoveDraft();

            _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_ACTIVE_DRAFT);
            return _lastOutcome;
        }

        private ViewSubmissionOutcome ConfirmPlacementDraft()
        {
            ViewPlacementDraft draft = _placementDraft;
            IViewLogicPort logic = Logic;
            IViewCommandFactory commands = Commands;
            if (logic == null || commands == null)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_LOGIC_PORT);
                return _lastOutcome;
            }

            ActionType ignoredType;
            if (string.IsNullOrEmpty(draft.ActionSpecId) || !logic.TryGetActionType(draft.ActionSpecId, out ignoredType))
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.ACTION_SPEC_NOT_FOUND);
                return _lastOutcome;
            }

            ActionPlanId anchor = string.IsNullOrEmpty(draft.InsertAfterPlanId)
                ? default
                : new ActionPlanId(ViewInputController.ParsePlanId(draft.InsertAfterPlanId));

            var operation = new AddOrdinaryPlanOperation(
                TemporaryPlanKey: draft.FirstRequestedStartTick,
                OwnerUnitId: new UnitId(draft.OwnerUnitId),
                ActionSpecId: new ActionSpecId(draft.ActionSpecId),
                RequestedStartTick: draft.TargetStartTick,
                AnchorAfterPlanId: anchor,
                PrimaryTargetUnitId: draft.HasPrimaryTarget ? new UnitId?(new UnitId(draft.PrimaryTargetUnitId)) : null,
                Facing: (GridDirection)draft.Facing,
                Destination: null);

            _placementDraft = null;
            _lastPreviewedTick = 0L;
            _lastDraftConfirmed = true;

            return SubmitOperation(commands, logic, operation, true);
        }

        private ViewSubmissionOutcome ConfirmReorderDraft()
        {
            ViewReorderDraft draft = _reorderDraft;
            IViewLogicPort logic = Logic;
            IViewCommandFactory commands = Commands;
            if (logic == null || commands == null)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_LOGIC_PORT);
                return _lastOutcome;
            }

            long planIdValue = ViewInputController.ParsePlanId(draft.PlanId);
            if (planIdValue <= 0L)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.PLAN_ID_UNPARSEABLE);
                return _lastOutcome;
            }

            var planId = new ActionPlanId(planIdValue);
            ActionPlanSnapshot ignored;
            bool editable = logic.TryFindEditablePlan(planId, out ignored);
            if (!editable)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.PLAN_NOT_EDITABLE_ORDINARY);
                return _lastOutcome;
            }

            ActionPlanId anchor = string.IsNullOrEmpty(draft.InsertAfterPlanId)
                ? default
                : new ActionPlanId(ViewInputController.ParsePlanId(draft.InsertAfterPlanId));

            var operation = new MoveEditablePlanOperation(planId, draft.TargetStartTick, anchor);

            _reorderDraft = null;
            _lastPreviewedTick = 0L;
            _lastDraftConfirmed = true;

            return SubmitOperation(commands, logic, operation, editable);
        }

        private ViewSubmissionOutcome ConfirmRemoveDraft()
        {
            long planIdValue = _removeDraftPlanId;
            IViewLogicPort logic = Logic;
            IViewCommandFactory commands = Commands;
            if (logic == null || commands == null)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_LOGIC_PORT);
                return _lastOutcome;
            }

            var planId = new ActionPlanId(planIdValue);
            ActionPlanSnapshot ignored;
            bool editable = logic.TryFindEditablePlan(planId, out ignored);
            if (!editable)
            {
                // 关键边界：Locked/Running/反应计划在 UI 侧就地不可删——这里连命令都不构造。
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.PLAN_NOT_EDITABLE_ORDINARY);
                return _lastOutcome;
            }

            var operation = new RemoveEditablePlanOperation(planId, ActionTerminationReason.CancelledByCommand);

            _removeDraftPlanId = 0L;
            _lastDraftConfirmed = true;

            return SubmitOperation(commands, logic, operation, editable);
        }

        private ViewSubmissionOutcome SubmitOperation(
            IViewCommandFactory commands, IViewLogicPort logic,
            ScheduleEditOperation operation, bool planStateAllowsSubmission)
        {
            ScheduleEditPayload payload = ViewCommandFactory.EditPayload(operation);
            CommandRequest request = commands.CreateScheduleEdit(payload);
            if (request == null)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_LOGIC_PORT);
                return _lastOutcome;
            }

            CommandIngressRejection rejection = logic.SubmitCommand(request);
            _confirmedGestureCount++;
            _lastOutcome = rejection == null
                ? ViewSubmissionOutcome.Accepted(planStateAllowsSubmission)
                : ViewSubmissionOutcome.Rejected(rejection.ReasonCode, planStateAllowsSubmission);
            return _lastOutcome;
        }

        // ————————————————————————————————————————————————————————————
        // 反应选择（Block / Dodge）与窗口命令
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 进入反应选择：<strong>只能</strong>从已公开且仍开放的机会进入，
        /// 并且选中的反应动作必须是该机会公开的选项。
        /// </summary>
        public bool BeginReactionSelection(ReactionOpportunityId opportunityId, ActionSpecId reactionActionSpecId)
        {
            IViewLogicPort logic = Logic;
            if (logic == null || !opportunityId.IsValid) return false;

            IReadOnlyList<ReactionOpportunitySnapshot> opportunities = logic.ReactionOpportunitiesOf(logic.ControllerId);
            if (opportunities == null) return false;

            for (int i = 0; i < opportunities.Count; i++)
            {
                ReactionOpportunitySnapshot opportunity = opportunities[i];
                if (opportunity == null || opportunity.ReactionOpportunityId != opportunityId.Value) continue;
                if (opportunity.State != (int)ReactionOpportunityState.Open) return false;
                if (!HasOpenOption(opportunity, reactionActionSpecId.Value)) return false;

                _reactionSelection = new ViewReactionSelection(
                    opportunityId.Value, opportunity.DefenderUnitId, reactionActionSpecId.Value);
                EnterMode(ViewInputMode.ReactionSelection);
                return true;
            }

            return false;
        }

        private static bool HasOpenOption(ReactionOpportunitySnapshot opportunity, string actionSpecId)
        {
            if (string.IsNullOrEmpty(actionSpecId) || opportunity.Options == null) return false;
            for (int i = 0; i < opportunity.Options.Count; i++)
            {
                ReactionOptionSnapshot option = opportunity.Options[i];
                if (option == null || !option.IsOpen) continue;
                if (string.Equals(option.ActionSpecId, actionSpecId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 选择 Dodge 目的格。只有通过 <see cref="IViewLogicPort.DescribeDodgeDestinationRejection"/>
        /// （与 Logic 提交共用的同一个求值函数）的格子才会被接受。
        /// </summary>
        public bool SelectDodgeDestination(GridPoint destination)
        {
            if (_reactionSelection == null) return false;

            IViewLogicPort logic = Logic;
            if (logic == null) return false;

            var defender = new UnitId(_reactionSelection.DefenderUnitId);
            var specId = new ActionSpecId(_reactionSelection.ReactionActionSpecId);
            string rejection = logic.DescribeDodgeDestinationRejection(defender, specId, destination);
            if (rejection != null)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(rejection);
                return false;
            }

            _reactionSelection.DestinationX = destination.X;
            _reactionSelection.DestinationY = destination.Y;
            EnterMode(ViewInputMode.DodgeDestinationSelection);
            return true;
        }

        /// <summary>确认反应选择 → 一次 <c>ReactionCommandPayload</c> 提交（Block 不带目的格，Dodge 必须带）。</summary>
        public ViewSubmissionOutcome ConfirmReaction()
        {
            ViewReactionSelection selection = _reactionSelection;
            IViewLogicPort logic = Logic;
            IViewCommandFactory commands = Commands;
            if (selection == null)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_ACTIVE_DRAFT);
                return _lastOutcome;
            }
            if (logic == null || commands == null)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_LOGIC_PORT);
                return _lastOutcome;
            }

            var specId = new ActionSpecId(selection.ReactionActionSpecId);
            ActionType reactionType;
            if (!logic.TryGetActionType(selection.ReactionActionSpecId, out reactionType)
                || (reactionType != ActionType.Block && reactionType != ActionType.Dodge))
            {
                // Block/Dodge 只能由存在且未过期的 Opportunity 创建；非反应规格绝不猜。
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.REACTION_NOT_OPEN_FOR_SELECTION);
                return _lastOutcome;
            }

            ReactionCommandKind kind = reactionType == ActionType.Block
                ? ReactionCommandKind.Block
                : ReactionCommandKind.Dodge;

            if (kind == ReactionCommandKind.Block && selection.HasDestination)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.BLOCK_MUST_NOT_CARRY_DESTINATION);
                return _lastOutcome;
            }
            if (kind == ReactionCommandKind.Dodge && !selection.HasDestination)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.DODGE_REQUIRES_DESTINATION);
                return _lastOutcome;
            }

            GridPoint? destination = selection.HasDestination
                ? new GridPoint?(new GridPoint(selection.DestinationX.Value, selection.DestinationY.Value))
                : null;

            var payload = new ReactionCommandPayload(kind, specId, destination);
            CommandRequest request = commands.CreateReaction(new ReactionOpportunityId(selection.OpportunityId), payload);
            if (request == null)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.REACTION_NOT_OPEN_FOR_SELECTION);
                return _lastOutcome;
            }

            CommandIngressRejection rejection = logic.SubmitCommand(request);
            _confirmedGestureCount++;
            _reactionSelection = null;
            _lastOutcome = rejection == null
                ? ViewSubmissionOutcome.Accepted()
                : ViewSubmissionOutcome.Rejected(rejection.ReasonCode, true);
            return _lastOutcome;
        }

        /// <summary>并发行动激活：<c>WindowCommandScope(ExpectedWindowId)</c>；费用由 Logic 从权威定义读取。</summary>
        public ViewSubmissionOutcome ActivateConcurrentAction()
        {
            IViewLogicPort logic = Logic;
            IViewCommandFactory commands = Commands;
            if (logic == null || commands == null)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_LOGIC_PORT);
                return _lastOutcome;
            }

            WindowId? window = logic.OpenWindowId;
            if (!window.HasValue || !window.Value.IsValid)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_OPEN_WINDOW);
                return _lastOutcome;
            }

            CommandRequest request = commands.CreateActivateConcurrent(window.Value);
            if (request == null)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_OPEN_WINDOW);
                return _lastOutcome;
            }

            CommandIngressRejection rejection = logic.SubmitCommand(request);
            _confirmedGestureCount++;
            _lastOutcome = rejection == null
                ? ViewSubmissionOutcome.Accepted()
                : ViewSubmissionOutcome.Rejected(rejection.ReasonCode, true);
            return _lastOutcome;
        }

        /// <summary>关闭自己拥有的窗口。</summary>
        public ViewSubmissionOutcome CloseOwnWindow()
        {
            IViewLogicPort logic = Logic;
            IViewCommandFactory commands = Commands;
            if (logic == null || commands == null)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_LOGIC_PORT);
                return _lastOutcome;
            }

            WindowId? window = logic.OpenWindowId;
            if (!window.HasValue || !window.Value.IsValid)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_OPEN_WINDOW);
                return _lastOutcome;
            }

            CommandRequest request = commands.CreateCloseWindow(window.Value);
            if (request == null)
            {
                _lastOutcome = ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_OPEN_WINDOW);
                return _lastOutcome;
            }

            CommandIngressRejection rejection = logic.SubmitCommand(request);
            _confirmedGestureCount++;
            _lastOutcome = rejection == null
                ? ViewSubmissionOutcome.Accepted()
                : ViewSubmissionOutcome.Rejected(rejection.ReasonCode, true);
            return _lastOutcome;
        }

        private void ClearReactionSelection()
        {
            _reactionSelection = null;
        }

        /// <summary>计划 ID 文本 → 数值（不可解析返回 0；绝不猜测）。</summary>
        public static long ParsePlanId(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0L;
            long value;
            return long.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value)
                ? value
                : 0L;
        }
    }
}
