using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Resources;

namespace ProjectHero.Logic.Turns
{
    /// <summary>
    /// 一次排程事务里<strong>单个计划</strong>的预算变化请求（值语义；不携带任何身份或费用来源的"自报"字段）。
    ///
    /// 语义（任务 07「必须产出」4）：
    /// <list type="bullet">
    /// <item><see cref="IsNewReservation"/> = true：Editable 普通计划首次创建，
    /// 把 <see cref="NewCost"/> 从窗口 <c>Available</c> 转入按计划归属的 <c>Reserved</c>；</item>
    /// <item><see cref="Delta"/> &gt; 0：既有计划成本上升，只允许从<strong>同一个仍为当前且开放</strong>的
    /// 来源窗口追加预留；</item>
    /// <item><see cref="Delta"/> &lt; 0：成本下降或 Remove，释放额回到
    /// <see cref="WindowId"/>（= 该计划的 <c>SubmittedWindowId</c>）的账本；窗口已关闭时<strong>只</strong>更新历史账本。</item>
    /// </list>
    /// </summary>
    public readonly struct TurnBudgetChangeRequest
    {
        public TurnBudgetChangeRequest(
            ActionPlanId planId, WindowId windowId, int newCost, int delta,
            int expectedReserved, bool isNewReservation,
            TurnBudgetChangeKind kind)
        {
            PlanId = planId;
            WindowId = windowId;
            NewCost = newCost;
            Delta = delta;
            ExpectedReserved = expectedReserved;
            IsNewReservation = isNewReservation;
            Kind = kind;
        }

        public ActionPlanId PlanId { get; }

        /// <summary>来源窗口：新计划 = 当前窗口；既有计划 = 计划自己的 <c>SubmittedWindowId</c>。</summary>
        public WindowId WindowId { get; }

        /// <summary>本次求值后计划的权威成本（<c>ActionPlan.BudgetCostTicks</c>）。</summary>
        public int NewCost { get; }

        /// <summary>&gt; 0 追加预留；&lt; 0 释放；= 0 无账本变化。</summary>
        public int Delta { get; }

        /// <summary>变更前该窗口账本中该计划的预留额（用于检测账本漂移）。</summary>
        public int ExpectedReserved { get; }

        public bool IsNewReservation { get; }

        /// <summary>进入 <c>TurnBudgetChangedEvent</c> 的变化类别。</summary>
        public TurnBudgetChangeKind Kind { get; }

        public bool HasLedgerEffect => IsNewReservation || Delta != 0;
    }

    /// <summary>
    /// 一次排程事务的<strong>预算上下文</strong>（由模拟方按命令 scope 创建，事务只读它）。
    ///
    /// <see cref="Authority"/> 为 null 表示本场没有接入 TurnBudget 账本
    /// （任务 05/06 的既有装配），此时事务保持"不建模预算"的既有语义。
    /// </summary>
    public sealed class ScheduleBudgetContext
    {
        private static readonly TurnBudgetChangeRequest[] NoChanges = new TurnBudgetChangeRequest[0];

        public ScheduleBudgetContext(
            ResourceChangeSource source,
            ITurnBudgetAuthority authority,
            WindowId? expectedWindowId,
            WindowId? currentWindowId,
            ControllerId issuer)
        {
            Source = source;
            Authority = authority;
            ExpectedWindowId = expectedWindowId;
            CurrentWindowId = currentWindowId;
            Issuer = issuer;
        }

        /// <summary>显式 <c>ScheduleEdit</c> 还是系统自动延期。</summary>
        public ResourceChangeSource Source { get; }

        public ITurnBudgetAuthority Authority { get; }

        /// <summary>命令 scope 声明的窗口（<c>ScheduleEditScope.ExpectedWindowId</c>）。</summary>
        public WindowId? ExpectedWindowId { get; }

        /// <summary>事务执行时的当前窗口（未打开时为 null）。</summary>
        public WindowId? CurrentWindowId { get; }

        /// <summary>命令网关绑定的发行者（<strong>不</strong>来自命令载荷）。</summary>
        public ControllerId Issuer { get; }

        public bool HasAuthority => Authority != null;

        public bool IsSystemAutoDeferral => Source == ResourceChangeSource.SystemAutoDeferral;

        /// <summary>没有账本接入时的空上下文（任务 05/06 语义）。</summary>
        public static ScheduleBudgetContext None(ResourceChangeSource source)
            => new ScheduleBudgetContext(source, null, null, null, default);

        public static IReadOnlyList<TurnBudgetChangeRequest> EmptyChanges => NoChanges;
    }

    /// <summary>
    /// <strong>TurnBudget 权威端口</strong>（任务 07「必须产出」4 与 12）。
    ///
    /// 它是窗口账本<strong>唯一</strong>的写入通道：排程事务只提交"候选变化"，由本端口
    /// 完成"校验 → 应用 → （仅在事务未提交成功时）回滚"三段式，因此排程、Lane、Timing、
    /// 路径、空间 Reservation 与预算要么全部提交，要么全部回滚。
    ///
    /// 它<strong>不</strong>：成为动作容器、成为执行边界、在窗口关闭时结算任何计划。
    /// </summary>
    public interface ITurnBudgetAuthority
    {
        /// <summary>
        /// 提交授权：窗口拥有者天然可提交普通动作；非拥有者必须已获得独立系统的并发授权。
        /// 返回 null = 允许；否则为稳定拒绝码。
        /// </summary>
        string ValidateSubmissionAuthority(ControllerId issuer, UnitId ownerUnitId, ActionType actionType, WindowId windowId);

        /// <summary>零写入预检。返回 null = 本批候选可满足；否则为稳定拒绝码（整批拒绝、零局部写入）。</summary>
        string ValidateBudget(IReadOnlyList<TurnBudgetChangeRequest> changes, ScheduleBudgetContext context);

        /// <summary>应用预算变化（<see cref="ValidateBudget"/> 通过后<strong>不得</strong>失败）。</summary>
        void ApplyBudget(IReadOnlyList<TurnBudgetChangeRequest> changes, ScheduleBudgetContext context, long tick);

        /// <summary>
        /// 回滚一次<strong>尚未提交成功</strong>的排程/系统候选的预算变化。
        /// 终态协调器与强制位移路径<strong>不得</strong>调用本方法（它们只能经状态感知的清理参与者）。
        /// </summary>
        void RollbackBudget(IReadOnlyList<TurnBudgetChangeRequest> changes, ScheduleBudgetContext context, long tick);
    }

    /// <summary>
    /// <strong>窗口预算权威实现</strong>（任务 07「必须产出」4、10、12）。
    ///
    /// 它把 <see cref="ITurnBudgetAuthority"/>（排程事务）与
    /// <c>IActionPlanStartCommitPort</c>（启动门禁的 <c>Reserved -&gt; Spent</c> 原子提交）接在
    /// <strong>同一份</strong> <see cref="TurnWindowManager"/> 账本上，使"预算"只有一套状态。
    /// </summary>
    public sealed class TurnWindowBudgetAuthority : ITurnBudgetAuthority, Timeline.IActionPlanStartCommitPort
    {
        private readonly TurnWindowManager _windows;
        private readonly IActionAuthority _submissionAuthority;

        public TurnWindowBudgetAuthority(TurnWindowManager windows, IActionAuthority submissionAuthority = null)
        {
            _windows = windows ?? throw new ArgumentNullException(nameof(windows));
            _submissionAuthority = submissionAuthority;
        }

        /// <summary>账本变化端口（由模拟方接到 Outbox；本类型不自行发射逻辑事件）。</summary>
        public Action<TurnBudgetChange> ChangedSink { get; set; }

        /// <summary>并发授权系统（用于"非窗口拥有者"的提交授权判定）；未接入时为 null。</summary>
        public IActionAuthority SubmissionAuthority => _submissionAuthority;

        /// <inheritdoc />
        public string ValidateSubmissionAuthority(
            ControllerId issuer, UnitId ownerUnitId, ActionType actionType, WindowId windowId)
        {
            // 未接入授权模型（任务 05/06 装配）时保持既有语义：由调用方自行保证。
            if (_submissionAuthority == null) return null;
            if (!_windows.CanControl(issuer, ownerUnitId))
                return TurnWindowCodes.ISSUER_CANNOT_CONTROL_UNIT;
            return _submissionAuthority.CanSubmitOrdinaryAction(issuer, ownerUnitId, windowId, actionType)
                ? null
                : TurnWindowCodes.NO_SUBMISSION_AUTHORITY;
        }

        /// <inheritdoc />
        public string ValidateBudget(IReadOnlyList<TurnBudgetChangeRequest> changes, ScheduleBudgetContext context)
        {
            if (changes == null || changes.Count == 0) return null;

            for (int i = 0; i < changes.Count; i++)
            {
                TurnBudgetChangeRequest change = changes[i];
                if (!change.HasLedgerEffect) continue;
                if (!change.PlanId.IsValid)
                    return TurnWindowCodes.BUDGET_LEDGER_INVARIANT;

                TurnWindow window = _windows.FindWindow(change.WindowId);
                if (window == null)
                    return TurnWindowCodes.BUDGET_SOURCE_CLOSED_OR_MISMATCH;

                if (change.Delta > 0)
                {
                    // 成本上升/新增只在"同一个仍为当前且开放"的来源窗口上合法。
                    if (context != null && context.HasAuthority)
                    {
                        // 00 号规则 18：**显式**排程命令的新增/增费窗口必填，缺失即稳定拒绝——
                        // 绝不用"当前窗口"替调用方补一个身份。
                        // 系统自动延期（SystemAutoDeferral）不是玩家提交、没有命令 scope，
                        // 因此它不受"必填"约束，仍只要求来源窗口是同一个当前开放窗口。
                        if (context.Source == ResourceChangeSource.ExplicitScheduleEdit
                            && !context.ExpectedWindowId.HasValue)
                            return TurnWindowCodes.BUDGET_SOURCE_CLOSED_OR_MISMATCH;
                        if (!context.CurrentWindowId.HasValue || context.CurrentWindowId.Value != change.WindowId)
                            return TurnWindowCodes.BUDGET_SOURCE_CLOSED_OR_MISMATCH;
                        if (context.ExpectedWindowId.HasValue && context.ExpectedWindowId.Value != change.WindowId)
                            return TurnWindowCodes.STALE_OR_CLOSED_WINDOW;
                    }
                    if (!window.IsOpen || !window.IsAcceptingSubmissions)
                        return TurnWindowCodes.BUDGET_SOURCE_CLOSED_OR_MISMATCH;
                    if (_windows.OpenWindow == null || _windows.OpenWindow.WindowId != change.WindowId)
                        return TurnWindowCodes.NO_OPEN_WINDOW;

                    int held = window.ReservedFor(change.PlanId);
                    if (held != change.ExpectedReserved)
                        return TurnWindowCodes.BUDGET_LEDGER_INVARIANT;
                    if (change.IsNewReservation && held != 0)
                        return TurnWindowCodes.BUDGET_LEDGER_INVARIANT;
                    if (change.Delta > window.AvailableBudgetTicks)
                        return TurnWindowCodes.INSUFFICIENT_WINDOW_BUDGET;
                }
                else if (change.Delta < 0)
                {
                    // 释放对已关闭窗口同样合法：只更新历史账本，不重开、不转移。
                    int held = window.ReservedFor(change.PlanId);
                    if (held != change.ExpectedReserved)
                        return TurnWindowCodes.BUDGET_LEDGER_INVARIANT;
                }
            }

            return null;
        }

        /// <inheritdoc />
        public void ApplyBudget(IReadOnlyList<TurnBudgetChangeRequest> changes, ScheduleBudgetContext context, long tick)
        {
            if (changes == null || changes.Count == 0) return;
            var source = context == null ? ResourceChangeSource.ExplicitScheduleEdit : context.Source;

            for (int i = 0; i < changes.Count; i++)
            {
                TurnBudgetChangeRequest change = changes[i];
                if (!change.HasLedgerEffect) continue;

                TurnWindow window = _windows.FindWindow(change.WindowId);
                if (window == null)
                    throw new LogicDefinitionException(
                        TurnWindowCodes.BUDGET_LEDGER_INVARIANT,
                        "unknown window " + change.WindowId.Value.ToString(CultureInfo.InvariantCulture));

                int availableBefore = window.AvailableBudgetTicks;
                int reservedBefore = window.ReservedBudgetTicks;
                int spentBefore = window.SpentBudgetTicks;

                if (change.IsNewReservation)
                {
                    window.ReserveForEditablePlan(change.PlanId, change.NewCost);
                }
                else if (change.Delta > 0)
                {
                    window.AdjustReservation(change.PlanId, change.Delta);
                }
                else if (change.Delta < 0)
                {
                    int requested = -change.Delta;
                    int held = window.ReservedFor(change.PlanId);
                    int release = requested < held ? requested : held;
                    if (release > 0) window.AdjustReservation(change.PlanId, -release);
                }

                Emit(window, change, source, tick, availableBefore, reservedBefore, spentBefore);
            }
        }

        /// <inheritdoc />
        public void RollbackBudget(IReadOnlyList<TurnBudgetChangeRequest> changes, ScheduleBudgetContext context, long tick)
        {
            if (changes == null || changes.Count == 0) return;
            var source = context == null ? ResourceChangeSource.ExplicitScheduleEdit : context.Source;

            // 逆序回滚，使同一计划在同一事务内的多次变化按栈顺序复原。
            for (int i = changes.Count - 1; i >= 0; i--)
            {
                TurnBudgetChangeRequest change = changes[i];
                if (!change.HasLedgerEffect) continue;

                TurnWindow window = _windows.FindWindow(change.WindowId);
                if (window == null) continue;

                int availableBefore = window.AvailableBudgetTicks;
                int reservedBefore = window.ReservedBudgetTicks;
                int spentBefore = window.SpentBudgetTicks;

                if (change.IsNewReservation)
                {
                    window.ReleaseEditableReservation(change.PlanId);
                }
                else if (change.Delta > 0)
                {
                    window.AdjustReservation(change.PlanId, -change.Delta);
                }
                else if (change.Delta < 0)
                {
                    window.AdjustReservation(change.PlanId, -change.Delta);
                }

                Emit(window, change, source, tick, availableBefore, reservedBefore, spentBefore);
            }
        }

        /// <summary>
        /// <strong>启动门禁的原子提交端口</strong>（任务 05 冻结接口，任务 07 提供真实实现）。
        ///
        /// 它把该计划<strong>已经持有</strong>的预留从 <c>Reserved</c> 原子转为 <c>Spent</c>：
        /// <list type="bullet">
        /// <item><strong>不</strong>二次收费（费用恰好等于预留，二者不一致即不变量错误）；</item>
        /// <item><strong>不</strong>转移来源窗口、<strong>不</strong>重开关闭的窗口；</item>
        /// <item>任一步失败 ⇒ 先调用 <paramref name="rollback"/> 撤销已完成的副作用，再返回稳定错误码
        /// （调用方据此令整个启动提交失败，绝不留下 <c>Locked-but-not-Running</c> 或半消费）。</item>
        /// </list>
        /// </summary>
        public string Commit(ActionPlan plan, long tick, System.Action<ActionPlan> rollback)
        {
            if (plan == null) return Timeline.ActionStartGateCommitCodes.RESOURCE_COMMIT_ERROR;

            try
            {
                int cost = plan.BudgetCostTicks;

                // 反应计划不消费 TurnBudget（它们的费用在肾上腺素账本上）。
                if (plan.IsReaction)
                {
                    if (cost != 0)
                    {
                        rollback?.Invoke(plan);
                        return Timeline.ActionStartGateCommitCodes.RESOURCE_COMMIT_ERROR;
                    }
                    plan.ReservedTurnBudgetTicks = 0;
                    return null;
                }

                if (!plan.SubmittedWindowId.HasValue)
                {
                    rollback?.Invoke(plan);
                    return Timeline.ActionStartGateCommitCodes.RESOURCE_COMMIT_ERROR;
                }

                TurnWindow window = _windows.FindWindow(plan.SubmittedWindowId.Value);
                if (window == null)
                {
                    rollback?.Invoke(plan);
                    return Timeline.ActionStartGateCommitCodes.RESOURCE_COMMIT_ERROR;
                }

                int held = window.ReservedFor(plan.ActionPlanId);
                if (held != cost)
                {
                    // 预留与权威成本必须一致：不一致代表预算账本已被破坏，
                    // 此时不得"顺手"消费或差额补扣——失败并交回调用方。
                    rollback?.Invoke(plan);
                    return Timeline.ActionStartGateCommitCodes.RESOURCE_COMMIT_ERROR;
                }

                int availableBefore = window.AvailableBudgetTicks;
                int reservedBefore = window.ReservedBudgetTicks;
                int spentBefore = window.SpentBudgetTicks;

                window.ConsumeReservationAtLock(plan.ActionPlanId, cost);
                plan.ReservedTurnBudgetTicks = 0;

                ChangedSink?.Invoke(new TurnBudgetChange(
                    window.WindowId, window.OwnerUnitId, plan.ActionPlanId,
                    TurnBudgetChangeKind.ConsumedAtLock, ResourceChangeSource.StartCommit,
                    availableBefore, window.AvailableBudgetTicks,
                    reservedBefore, window.ReservedBudgetTicks,
                    spentBefore, window.SpentBudgetTicks));
                return null;
            }
            catch (System.Exception ex)
            {
                rollback?.Invoke(plan);
                return Timeline.ActionStartGateCommitCodes.RESOURCE_COMMIT_ERROR + ":" + ex.GetType().Name;
            }
        }

        /// <summary>
        /// 战斗结束：清空全部窗口（当前窗口 + 已关闭窗口的历史账本）的<strong>未消费预留</strong>，
        /// <strong>保留</strong> <c>Spent</c>，并逐窗口发射
        /// <see cref="TurnBudgetChangeKind.BattleEndCleared"/>（含 WindowId 与前后值）。
        ///
        /// 它<strong>不</strong>退款、不重开窗口、不清零已消费额度，也不形成可继续使用的额度。
        /// </summary>
        public int ClearReservationsForBattleEnd(long tick)
        {
            int total = 0;
            TurnWindow current = _windows.CurrentWindow;
            if (current != null) total += ClearWindowReservations(current, tick);
            for (int i = 0; i < _windows.ClosedWindows.Count; i++)
                total += ClearWindowReservations(_windows.ClosedWindows[i], tick);
            return total;
        }

        private int ClearWindowReservations(TurnWindow window, long tick)
        {
            int availableBefore = window.AvailableBudgetTicks;
            int reservedBefore = window.ReservedBudgetTicks;
            int spentBefore = window.SpentBudgetTicks;
            int released = window.ClearReservationsForBattleEnd();
            if (released <= 0) return 0;

            ChangedSink?.Invoke(new TurnBudgetChange(
                window.WindowId, window.OwnerUnitId, null,
                TurnBudgetChangeKind.BattleEndCleared, ResourceChangeSource.BattleEndFinalizer,
                availableBefore, window.AvailableBudgetTicks,
                reservedBefore, window.ReservedBudgetTicks,
                spentBefore, window.SpentBudgetTicks));
            return released;
        }

        private void Emit(
            TurnWindow window, TurnBudgetChangeRequest change, ResourceChangeSource source, long tick,
            int availableBefore, int reservedBefore, int spentBefore)
        {
            if (ChangedSink == null) return;
            ChangedSink(new TurnBudgetChange(
                window.WindowId,
                window.OwnerUnitId,
                change.PlanId.IsValid ? change.PlanId : (ActionPlanId?)null,
                change.Kind,
                source,
                availableBefore,
                window.AvailableBudgetTicks,
                reservedBefore,
                window.ReservedBudgetTicks,
                spentBefore,
                window.SpentBudgetTicks));
        }
    }
}
