using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Movement;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// 一条<strong>已由事务解析</strong>的排程编辑操作记录（供审计、预览结果与语义事件使用）。
    /// <see cref="PlanId"/>：Add 为该批事务正式分配并已提交的 <c>ActionPlanId</c>；
    /// 预览事务尚未提交时它仍然是本批的<strong>候选键</strong>（<c>TemporaryPlanKey</c>）。
    /// </summary>
    public sealed record AppliedScheduleEdit(
        ScheduleEditOperationKind Kind,
        ActionPlanId PlanId,
        UnitId OwnerUnitId,
        long RequestedStartTick,
        long ResultingStartTick);

    /// <summary>
    /// 一次权威排程事务的结果。
    ///
    /// <list type="bullet">
    /// <item><see cref="Committed"/> = false 表示这是<strong>预览</strong>或<strong>失败</strong>：
    /// 两者都<strong>不</strong>改变任何计划、Lane 或修订号，因此
    /// <see cref="ResultingScheduleRevision"/> 与 <see cref="BaseScheduleRevision"/> 相等。</item>
    /// <item><see cref="Evaluation"/> 是求值器的原始输出——预览与提交返回<strong>同一份算法结果</strong>，
    /// 因此"预览与提交同源"是结构事实。</item>
    /// </list>
    /// </summary>
    public sealed record ScheduleEditTransactionResult(
        bool Committed,
        bool IsPreview,
        string RejectionCode,
        long BaseScheduleRevision,
        long ResultingScheduleRevision,
        IReadOnlyList<AppliedScheduleEdit> AppliedEdits,
        IReadOnlyList<ScheduleOperationEvaluation> Evaluations,
        IReadOnlyList<ActionPlanId> AddedPlanIds,
        IReadOnlyList<ActionPlanId> RemovedPlanIds,
        IReadOnlyList<ActionPlanId> RipplePlanIds,
        IReadOnlyList<SchedulePlanPreview> PreviewPlans = null)
    {
        public bool Succeeded => RejectionCode == null;

        public static ScheduleEditTransactionResult Rejected(string code, long baseRevision)
            => new ScheduleEditTransactionResult(
                Committed: false, IsPreview: false, RejectionCode: code,
                BaseScheduleRevision: baseRevision, ResultingScheduleRevision: baseRevision,
                AppliedEdits: Array.Empty<AppliedScheduleEdit>(),
                Evaluations: Array.Empty<ScheduleOperationEvaluation>(),
                AddedPlanIds: Array.Empty<ActionPlanId>(),
                RemovedPlanIds: Array.Empty<ActionPlanId>(),
                RipplePlanIds: Array.Empty<ActionPlanId>());
    }

    /// <summary>
    /// <strong>权威 <c>ScheduleEditor</c></strong>（任务包「必须产出」6 与「核心规则」）。
    ///
    /// 它是普通计划 Add/Move/Remove 的<strong>唯一</strong>排程权威入口：
    /// <list type="bullet">
    /// <item><strong>先算后写</strong>：全部校验与求值都在写之前完成；
    /// 任一操作失败即<strong>整批回滚</strong>（零局部写入、零孤立计划、零残留 Lane 项）。</item>
    /// <item><strong>只 +1 一次</strong>：一个成功的命令排程事务令 <c>ScheduleRevision</c> 恰好 +1；
    /// 预览与任意失败<strong>不</strong>改变修订号。</item>
    /// <item><strong>乐观并发</strong>：<c>ExpectedScheduleRevision</c> 与
    /// <c>BatchBaseScheduleRevision</c>（Step 入口冻结）比较，而<strong>不是</strong>与
    /// 本批前一事务递增后的实时值比较 ⇒ 冻结基线不同的批次以 <c>STALE_SCHEDULE_REVISION</c> 拒绝。</item>
    /// <item><strong>批内冲突</strong>：已在本批被改写过<strong>依赖闭包</strong>的计划/Lane
    /// 再次被后处理事务触及 ⇒ <c>SCHEDULE_EDIT_CONFLICT_IN_BATCH</c>（按 CommandSequence 顺序，
    /// 后处理者被拒，先处理者保持已提交结果）。</item>
    /// <item><strong>Add 的 ID 归属</strong>：正式 <c>ActionPlanId</c> 由 Logic 在
    /// <strong>求值与全部校验通过之后</strong>才分配并注册；失败的事务<strong>不消耗</strong> ID。</item>
    /// <item><strong>同源求值</strong>：位置、路径边数、路径权重、绝对 Tick 与 ripple 全部来自
    /// <see cref="ScheduleEvaluator"/>；预览走完全相同的代码路径。</item>
    /// </list>
    ///
    /// 它<strong>不</strong>：启动计划（那是 <c>ActionStartGate</c>）、创建反应计划
    /// （那是 <c>ReactionPlanner</c>）、写终态（那是统一终态协调器）。
    /// </summary>
    public sealed class ScheduleEditor
    {
        private readonly ActionScheduleAuthority _authority;
        private readonly ScheduleEvaluator _evaluator;
        private readonly ActionPlanFactory _factory;
        private readonly LogicIdGenerator _ids;

        // —— 批次边界（乐观并发的唯一状态）——
        //
        // 一个"批次"= 同一 Tick 上、以同一个 Step 入口冻结的 BatchBaseScheduleRevision 为准的
        // 全部命令事务。批内续作允许修订号已经被本批自己推进；跨批次（尤其是跨 Tick）的旧基线
        // 则以 STALE_SCHEDULE_REVISION 拒绝。_batchClaimedPlans 是本批已改写的依赖闭包。
        private long _batchTick = long.MinValue;
        private long _batchBaseRevision = long.MinValue;
        private long _batchCommittedCount;
        private readonly HashSet<long> _batchClaimedPlans = new HashSet<long>();

        /// <param name="pathCalculator">
        /// 任务 06 的路径重算端口。它必须与 <see cref="ScheduleEvaluator"/> 用的是同一个实例，
        /// 否则注入的端口会被一个"没有端口"的内部求值器悄悄忽略。
        /// </param>
        public ScheduleEditor(
            ActionScheduleAuthority authority, ActionPlanFactory factory, LogicIdGenerator ids,
            IMovementPathCalculator pathCalculator = null)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
            _evaluator = new ScheduleEvaluator(authority, pathCalculator);
        }

        public ActionScheduleAuthority Authority => _authority;

        public ScheduleEvaluator Evaluator => _evaluator;

        /// <summary>
        /// <strong>排程事务 → 空间整批替换</strong>的接缝（任务 06「必须产出」6 第二段）。
        ///
        /// 显式编辑与系统自动延期在这里<strong>复用同一个</strong>实现：事务先把整批计划重绑到
        /// 新绝对 Tick 并应用路径投影，然后把这批计划交给本端口，由端口在<strong>一个原子批次</strong>
        /// 内整体替换它们的未来 <c>MovementSegment</c> 与 Reservation。
        ///
        /// 返回非 null 的稳定错误码 ⇒ 本事务把<strong>已改写</strong>的这批计划恢复成提交前的投影
        /// 并整批拒绝（零局部空间写入 ⇒ 旧段/预留继续权威）。
        /// 未接缝时为安全无操作（保持既有"排程与空间解耦"的既有语义）。
        /// </summary>
        public Movement.IEditableMovementSpacePort MovementSpacePort { get; set; }

        /// <summary>
        /// 本事务已把工作集交给空间端口并成功提交时的回调
        /// （<c>(planId, newStartTick, scheduleRevision, currentTick)</c>）。
        /// 系统自动延期用它把"计划被延期"这一事实同步到空间侧消费者。
        /// </summary>
        public Action<ActionPlanId, long, long, long> CommittedSpacePlanSink { get; set; }

        /// <summary>
        /// <strong>TurnBudget 上下文工厂</strong>（任务 07「必须产出」4：统一 ScheduleEdit 预算事务）。
        ///
        /// 参数顺序 =（来源、目标 Tick、命令 scope 的 <c>ExpectedWindowId</c>、发行者 <c>ControllerId</c>）。
        /// 由模拟方在装配时注入；<strong>返回 null 或未设置 ⇒ 本事务不建模预算</strong>
        /// （任务 05/06 既有语义：只维护 <c>ActionPlan.ReservedTurnBudgetTicks</c> 投影）。
        ///
        /// 工厂必须只读、可重复调用且无副作用：本编辑器在提交路径上按需调用它，
        /// 但账本的唯一权威始终是 <see cref="ITurnBudgetAuthority"/>——编辑器
        /// <strong>不</strong>读窗口对象、<strong>不</strong>自行扣费，也不保存第二套预算状态。
        /// </summary>
        public Func<ResourceChangeSource, long, WindowId?, ControllerId, ScheduleBudgetContext> BudgetContextFactory { get; set; }

        public ScheduleLimits Limits => _authority.Limits;

        /// <summary>本批已被成功事务改写的计划（只读；诊断与测试用）。</summary>
        public IReadOnlyCollection<long> BatchClaimedPlanIds => _batchClaimedPlans;

        /// <summary>
        /// 显式开启一个新批次（Step 入口冻结 <c>BatchBaseScheduleRevision</c> 之后调用）。
        /// 未显式调用时由 <see cref="ApplyInternal"/> 隐式识别（首个事务的基线等于实时修订号）。
        /// </summary>
        public void BeginBatch(long tick, long batchBaseScheduleRevision)
        {
            _batchTick = tick;
            _batchBaseRevision = batchBaseScheduleRevision;
            _batchCommittedCount = 0L;
            _batchClaimedPlans.Clear();
        }

        /// <summary>
        /// 应用一批排程编辑操作。
        /// </summary>
        /// <param name="operations">已由命令网关规范化排序后的操作列表（按 <c>CommandSequence</c> 顺序）。</param>
        /// <param name="currentTick">本 Step 的处理 Tick（命令目标 Tick）。</param>
        /// <param name="batchBaseScheduleRevision">Step 入口冻结的 <c>BatchBaseScheduleRevision</c>。</param>
        /// <param name="expectedScheduleRevision">命令 scope 里的 <c>ExpectedScheduleRevision</c>。</param>
        /// <param name="preview">
        /// 预览模式：<strong>不</strong>分配正式 ID、<strong>不</strong>改修订号、<strong>不</strong>写任何权威状态。
        /// </param>
        /// <param name="expectedWindowId">
        /// 命令 scope（<c>ScheduleEditScope</c>）声明的期望窗口；新增/增费时窗口必填（00 号规则 18）。
        /// 为 null 表示命令没有声明窗口：预算校验按"没有期望窗口"处理（见 <c>ValidateBudget</c>），
        /// <strong>不</strong>回退成"当前窗口"。
        /// </param>
        /// <param name="issuer">
        /// 由命令网关绑定的发行者 <c>ControllerId</c>（<strong>不</strong>来自命令载荷）。
        /// 只有显式 <c>ScheduleEdit</c> 才用它做提交授权校验；系统自动延期不走这里。
        /// </param>
        public ScheduleEditTransactionResult Apply(
            IReadOnlyList<ScheduleEditOperation> operations,
            long currentTick,
            long batchBaseScheduleRevision,
            long expectedScheduleRevision,
            bool preview = false,
            WindowId? expectedWindowId = null,
            ControllerId issuer = default)
            => ApplyInternal(operations, currentTick, batchBaseScheduleRevision, expectedScheduleRevision,
                preview, null, expectedWindowId, issuer);

        /// <summary>
        /// 系统自动延期事务（任务包「必须产出」5 第二段）。
        ///
        /// 它<strong>复用同一 <see cref="ScheduleEvaluator"/></strong>，把到期计划整体推到
        /// <paramref name="retryAtTick"/> 并只向右 ripple 后续 Editable 依赖闭包。
        /// 成功时 <c>AutomaticDeferralCount</c> 与 <c>ScheduleRevision</c> 各 +1，
        /// <c>LastRequestedStartTick</c> <strong>不变</strong>（系统延期不得覆盖它）。
        /// </summary>
        /// <param name="claimedPlans">
        /// 本次 Step 内的批内冲突集合（命令事务已改写的依赖闭包）。
        /// 自动延期发生在显式命令阶段<strong>之后</strong>，因此它必须避开这些计划。
        /// </param>
        internal ScheduleEditTransactionResult ApplySystemAutoDeferral(
            ActionPlan plan, long retryAtTick, long currentTick, IReadOnlyList<ActionPlanId> claimedPlans)
        {
            if (plan == null) return ScheduleEditTransactionResult.Rejected(ScheduleCodes.SCHEDULE_OPERATION_INVALID, _authority.ScheduleRevision);
            if (!plan.IsEditable || !plan.IsOrdinary)
                return ScheduleEditTransactionResult.Rejected(ScheduleCodes.SCHEDULE_PLAN_NOT_EDITABLE, _authority.ScheduleRevision);
            if (retryAtTick <= currentTick)
                return ScheduleEditTransactionResult.Rejected(ScheduleCodes.SCHEDULE_RETRY_TICK_NOT_IN_FUTURE, _authority.ScheduleRevision);
            if (plan.AutomaticDeferralCount + 1 > _factory.Rules.MaxAutomaticDeferralsPerPlan)
                return ScheduleEditTransactionResult.Rejected(ScheduleCodes.SCHEDULE_HORIZON_EXCEEDED, _authority.ScheduleRevision);

            var claimed = new HashSet<long>();
            if (claimedPlans != null)
            {
                for (int i = 0; i < claimedPlans.Count; i++) claimed.Add(claimedPlans[i].Value);
            }
            if (claimed.Contains(plan.ActionPlanId.Value))
                return ScheduleEditTransactionResult.Rejected(ScheduleCodes.SCHEDULE_EDIT_CONFLICT_IN_BATCH, _authority.ScheduleRevision);

            var intents = new List<ScheduleOperationIntent>
            {
                // IsExplicitUserRequest = false：系统自动延期只改绝对 Tick，
                // 绝不覆盖 LastRequestedStartTick（冻结契约：只有直接 Move 操作能更新它）。
                new ScheduleOperationIntent(
                    plan, retryAtTick, IsDirectMove: true, AnchoredAfterPlanId: default,
                    IsExplicitUserRequest: false)
            };

            ScheduleEvaluationResult evaluation = _evaluator.Evaluate(intents, new[] { plan.OwnerUnitId });
            if (!evaluation.Succeeded)
                return ScheduleEditTransactionResult.Rejected(evaluation.RejectionCode, _authority.ScheduleRevision);

            string horizonError = CheckHorizon(evaluation, currentTick);
            if (horizonError != null)
                return ScheduleEditTransactionResult.Rejected(horizonError, _authority.ScheduleRevision);

            long baseRevision = _authority.ScheduleRevision;
            plan.AutomaticDeferralCount++;

            // —— 任务 07「必须产出」4 第四段：系统自动延期的预算差额 ——
            //
            // 上下文来源 = SystemAutoDeferral（它让 ValidateSubmissionAuthority 不被要求）；
            // expectedWindowId = null、issuer = default（系统延期不是玩家提交，没有 scope 身份）。
            // CurrentWindowId 必须取"当时真实的当前窗口"：成本增加时
            // TurnWindowBudgetAuthority.ValidateBudget 会要求 change.WindowId == context.CurrentWindowId，
            // 于是"关闭窗口不得因系统延期重开"由账本端口判定，本编辑器不自行猜测。
            ScheduleBudgetContext budgetContext = BudgetContextFactory == null
                ? null
                : BudgetContextFactory(
                    ResourceChangeSource.SystemAutoDeferral, currentTick, null, default);

            // 差额必须在**求值改写之前**捕获旧预留（同 <see cref="ApplyInternal"/> 的理由）。
            var capturedReserved = new Dictionary<long, int>();
            var evaluatedPlans = new List<ActionPlan>();
            var projectionSnapshots = new List<SpaceProjectionSnapshot>();
            for (int i = 0; i < evaluation.Evaluations.Count; i++)
            {
                ActionPlan evaluated = _authority.Registry.Find(evaluation.Evaluations[i].PlanId);
                if (evaluated == null || !evaluated.IsOrdinary) continue;
                if (!capturedReserved.ContainsKey(evaluated.ActionPlanId.Value))
                {
                    capturedReserved[evaluated.ActionPlanId.Value] = evaluated.ReservedTurnBudgetTicks;
                    evaluatedPlans.Add(evaluated);
                }
                projectionSnapshots.Add(new SpaceProjectionSnapshot(evaluated));
            }
            if (plan.IsOrdinary && !capturedReserved.ContainsKey(plan.ActionPlanId.Value))
            {
                capturedReserved[plan.ActionPlanId.Value] = plan.ReservedTurnBudgetTicks;
                evaluatedPlans.Add(plan);
                projectionSnapshots.Add(new SpaceProjectionSnapshot(plan));
            }

            CommitEvaluatedPlans(evaluation, baseRevision + 1L, null);

            // 候选变化（纯计算）；自动延期只改绝对 Tick，但路径重算可能改变成本 ⇒ 必须重算差额。
            List<TurnBudgetChangeRequest> budgetChanges = BuildBudgetChanges(
                budgetContext, null, evaluatedPlans, null, capturedReserved);
            bool needsBudgetCommit = budgetChanges.Count > 0;

            // 校验必须先于空间端口提交（空间提交成功后无法撤销空间侧）。
            // 注意：系统自动延期**不**校验发行者授权——它不是玩家提交，不得要求 ControllerId 授权。
            if (needsBudgetCommit && budgetContext != null && budgetContext.HasAuthority)
            {
                string budgetError = budgetContext.Authority.ValidateBudget(budgetChanges, budgetContext);
                if (budgetError != null)
                {
                    // 失败：回滚本次候选的全部局部写入（延期计数 + 排程投影），零预算变化。
                    plan.AutomaticDeferralCount--;
                    RestoreProjections(projectionSnapshots);
                    return ScheduleEditTransactionResult.Rejected(budgetError, baseRevision);
                }
            }

            // 空间整批替换（任务 06）在 Lane 投影替换之前完成：失败即整批拒绝并撤销延期计数。
            if (MovementSpacePort != null)
            {
                string spaceError = CommitMovementSpace(
                    evaluation, baseRevision + 1L, currentTick, null, null);
                if (spaceError != null)
                {
                    // 此处预算尚未应用（应用在空间端口成功之后），因此无需 RollbackBudget。
                    plan.AutomaticDeferralCount--;
                    RestoreProjections(projectionSnapshots);
                    return ScheduleEditTransactionResult.Rejected(spaceError, baseRevision);
                }
            }

            // 空间成功后立即应用预算差额；ApplyBudget 契约上不得失败，失败即不变量错误。
            if (needsBudgetCommit && budgetContext != null && budgetContext.HasAuthority)
            {
                try
                {
                    budgetContext.Authority.ApplyBudget(budgetChanges, budgetContext, currentTick);
                }
                catch (Exception)
                {
                    plan.AutomaticDeferralCount--;
                    RestoreProjections(projectionSnapshots);
                    throw;
                }
            }

            try
            {
                CommitLaneProjections(evaluation, null);
                _authority.IncrementRevision();
            }
            catch (Exception)
            {
                if (needsBudgetCommit && budgetContext != null && budgetContext.HasAuthority)
                {
                    try
                    {
                        budgetContext.Authority.RollbackBudget(budgetChanges, budgetContext, currentTick);
                    }
                    catch (Exception)
                    {
                        // 同上：回滚异常不得掩盖原始异常。
                    }
                }
                plan.AutomaticDeferralCount--;
                RestoreProjections(projectionSnapshots);
                throw;
            }

            // 成功：计划保持 Editable、账本保持 Reserved（绝不产生 ConsumedAtLock）。
            return BuildCommitted(baseRevision, evaluation, null, null);
        }

        // ————————————————————————————————————————————————————————————
        // 事务主体
        // ————————————————————————————————————————————————————————————

        private ScheduleEditTransactionResult ApplyInternal(
            IReadOnlyList<ScheduleEditOperation> operations,
            long currentTick,
            long batchBaseScheduleRevision,
            long expectedScheduleRevision,
            bool preview,
            IReadOnlyList<ActionPlanId> preClaimed,
            WindowId? expectedWindowId = null,
            ControllerId issuer = default)
        {
            long baseRevision = _authority.ScheduleRevision;

            // —— 任务 07「必须产出」4：TurnBudget 上下文（只读；null ⇒ 本事务不建模预算）——
            //
            // 上下文在**纯校验阶段之前**取一次并复用：它的 CurrentWindowId 同时是
            // ①本次新增计划携带的 SubmittedWindowId（预算来源与审计，见 ResolveAdd）
            // ②预算候选的窗口来源。取一次即可保证"同一事务内的事实一致"，
            // 也保证预览路径拿到与提交路径完全同源的上下文（预览绝不写账本，见第 9 条）。
            ScheduleBudgetContext budgetContext = BudgetContextFactory == null
                ? null
                : BudgetContextFactory(
                    ResourceChangeSource.ExplicitScheduleEdit, currentTick, expectedWindowId, issuer);
            WindowId? currentWindowId = budgetContext == null ? (WindowId?)null : budgetContext.CurrentWindowId;

            if (operations == null || operations.Count == 0)
                return ScheduleEditTransactionResult.Rejected(ScheduleCodes.SCHEDULE_OPERATION_INVALID, baseRevision);
            if (operations.Count > Limits.MaxBatchOperations)
                return ScheduleEditTransactionResult.Rejected(ScheduleCodes.SCHEDULE_BATCH_TOO_LARGE, baseRevision);
            if (expectedScheduleRevision != batchBaseScheduleRevision)
                return ScheduleEditTransactionResult.Rejected(ScheduleCodes.STALE_SCHEDULE_REVISION, baseRevision);

            // —— 批次边界（乐观并发）——
            // 同一批次内的续作允许修订号已被本批自己推进（冻结契约：Expected 与 Step 冻结的
            // BatchBaseScheduleRevision 比较，<strong>不</strong>与本批前一事务递增后的实时值比较）。
            // 若不能识别为续作，就必须重新开启批次，而新批次的冻结基线必须<strong>恰好</strong>等于
            // 当时的实时修订号；否则这条命令拿着的是上（若干）个 Tick 的旧基线
            // ⇒ STALE_SCHEDULE_REVISION（整批零局部写入）。
            if (!preview)
            {
                bool sameBatch = currentTick == _batchTick
                    && batchBaseScheduleRevision == _batchBaseRevision
                    && _authority.ScheduleRevision == _batchBaseRevision + _batchCommittedCount;
                if (!sameBatch)
                {
                    if (batchBaseScheduleRevision != _authority.ScheduleRevision)
                        return ScheduleEditTransactionResult.Rejected(ScheduleCodes.STALE_SCHEDULE_REVISION, baseRevision);

                    _batchTick = currentTick;
                    _batchBaseRevision = batchBaseScheduleRevision;
                    _batchCommittedCount = 0L;
                    _batchClaimedPlans.Clear();
                }
            }

            // 批内已改写的计划（用于 SCHEDULE_EDIT_CONFLICT_IN_BATCH）。
            var claimedPlans = new HashSet<long>();
            if (!preview)
            {
                foreach (long claimedId in _batchClaimedPlans) claimedPlans.Add(claimedId);
            }
            if (preClaimed != null)
            {
                for (int i = 0; i < preClaimed.Count; i++) claimedPlans.Add(preClaimed[i].Value);
            }

            var intents = new List<ScheduleOperationIntent>(operations.Count);
            var appliedEdits = new List<AppliedScheduleEdit>(operations.Count);
            var addedPlans = new List<ActionPlan>();
            var removedPlans = new List<ActionPlan>();
            var temporaryKeys = new Dictionary<long, ActionPlan>();
            var touchedLanes = new List<UnitId>();

            // —— 阶段 1：结构校验与操作解析（零写入）——
            //
            // ID 只<strong>准备</strong>不占用：候选计划先按"下一个待分配值 + 本批偏移"建出来，
            // 计数器保持不变；只有提交阶段才真正占用（失败事务不消耗 ID）。
            long nextIdCandidate = _ids.NextActionPlanIdValue;
            int preparedAdds = 0;
            for (int i = 0; i < operations.Count; i++)
            {
                ScheduleEditOperation operation = operations[i];
                if (operation == null)
                    return ScheduleEditTransactionResult.Rejected(ScheduleCodes.SCHEDULE_OPERATION_INVALID, baseRevision);

                string error;
                switch (operation)
                {
                    case AddOrdinaryPlanOperation add:
                        error = ResolveAdd(
                            add, currentTick, new ActionPlanId(nextIdCandidate + preparedAdds),
                            temporaryKeys, addedPlans, intents, appliedEdits, currentWindowId);
                        if (error == null) preparedAdds++;
                        break;
                    case MoveEditablePlanOperation move:
                        error = ResolveMove(move, currentTick, claimedPlans, intents, appliedEdits, touchedLanes);
                        break;
                    case RemoveEditablePlanOperation remove:
                        error = ResolveRemove(remove, claimedPlans, removedPlans, appliedEdits, touchedLanes);
                        break;
                    default:
                        error = ScheduleCodes.SCHEDULE_OPERATION_INVALID;
                        break;
                }

                if (error != null) return ScheduleEditTransactionResult.Rejected(error, baseRevision);
            }

            // —— 阶段 2：锚点唯一性与成环检测（整批拒绝）——
            string anchorError = ValidateAnchors(operations, temporaryKeys);
            if (anchorError != null) return ScheduleEditTransactionResult.Rejected(anchorError, baseRevision);

            // —— 阶段 3：Lane 可编辑性（提交锁定只阻止新提交）——
            //
            // 注意 lane == null 表示"该单位还没有 Lane"（首个计划），它<strong>不是</strong>提交锁定：
            // Lane 由 RegisterPlan 在提交点按需创建。
            for (int i = 0; i < addedPlans.Count; i++)
            {
                ActorLane lane = _authority.FindLane(addedPlans[i].OwnerUnitId);
                if (lane != null && lane.IsSubmissionLocked)
                    return ScheduleEditTransactionResult.Rejected(ScheduleCodes.SCHEDULE_LANE_SUBMISSION_LOCKED, baseRevision);
            }

            // —— 阶段 4：链上计划的可编辑性（锁定/反应不得被 ripple 推移）——
            var directIds = new HashSet<long>();
            for (int i = 0; i < intents.Count; i++) directIds.Add(intents[i].Plan.ActionPlanId.Value);
            for (int i = 0; i < touchedLanes.Count; i++)
            {
                ActorLane lane = _authority.FindLane(touchedLanes[i]);
                if (lane == null) continue;
                IReadOnlyList<ActionPlan> plans = lane.Plans;
                for (int p = 0; p < plans.Count; p++)
                {
                    if (plans[p].IsEditable || directIds.Contains(plans[p].ActionPlanId.Value)) continue;
                    // 障碍永远不被推移；只有当它出现在直接操作目标的前方时才可能被触及，
                    // 而那属于 Lane 结构矛盾（不变量错误），而不是可编辑性失败。
                    if (plans[p].StartTick < 0L)
                        return ScheduleEditTransactionResult.Rejected(ScheduleCodes.SCHEDULE_LANE_OVERLAP, baseRevision);
                }
            }

            // 只有<strong>显式命令</strong>的直接移动才更新 LastRequestedStartTick：
            // 系统自动延期虽然也把计划直接放到某个位点，但它不覆盖请求起点。
            var explicitRequestPlanIds = new HashSet<long>();
            for (int i = 0; i < intents.Count; i++)
            {
                if (intents[i].IsDirectMove && intents[i].IsExplicitUserRequest)
                    explicitRequestPlanIds.Add(intents[i].Plan.ActionPlanId.Value);
            }

            // —— 阶段 5：求值（纯函数；预览与提交共用）——
            ScheduleEvaluationResult evaluation = _evaluator.Evaluate(intents, touchedLanes, removedPlans);
            if (!evaluation.Succeeded)
                return ScheduleEditTransactionResult.Rejected(evaluation.RejectionCode, baseRevision);

            // —— 阶段 6：全局上限（视野、单 Lane 队列）——
            string horizonError = CheckHorizon(evaluation, currentTick);
            if (horizonError != null) return ScheduleEditTransactionResult.Rejected(horizonError, baseRevision);

            string queueError = CheckLaneQueueLimits(removedPlans, addedPlans);
            if (queueError != null) return ScheduleEditTransactionResult.Rejected(queueError, baseRevision);

            if (preview)
            {
                var candidateLookup = new Dictionary<long, ActionPlan>();
                var originals = new Dictionary<long, int>();
                var candidateAdded = new List<ActionPlan>();
                var candidateEvaluated = new List<ActionPlan>();
                foreach (var candidate in addedPlans)
                {
                    var copy = candidate.CopyForPreview();
                    candidateLookup.Add(copy.ActionPlanId.Value, copy);
                    originals[copy.ActionPlanId.Value] = 0;
                    candidateAdded.Add(copy);
                }
                foreach (var item in evaluation.Evaluations)
                {
                    if (!candidateLookup.TryGetValue(item.PlanId.Value, out var copy))
                    {
                        var original = _authority.Registry.Find(item.PlanId);
                        copy = original.CopyForPreview();
                        originals[copy.ActionPlanId.Value] = original.ReservedTurnBudgetTicks;
                        candidateLookup.Add(copy.ActionPlanId.Value, copy);
                    }
                    candidateEvaluated.Add(copy);
                }
                foreach (var removed in removedPlans) originals[removed.ActionPlanId.Value] = removed.ReservedTurnBudgetTicks;
                ApplyEvaluatedProjections(evaluation, baseRevision, explicitRequestPlanIds, candidateLookup);
                var spaceCandidates = new List<ActionPlan>(candidateAdded);
                var spaceSeen = new HashSet<long>();
                foreach (var candidate in candidateAdded) spaceSeen.Add(candidate.ActionPlanId.Value);
                var previewRemovedIds = new HashSet<long>();
                foreach (var removed in removedPlans) previewRemovedIds.Add(removed.ActionPlanId.Value);
                foreach (var lane in _authority.Lanes)
                {
                    if (!evaluation.CoversLane(lane.UnitId)) continue;
                    foreach (var original in evaluation.OrderingOf(lane.UnitId))
                    {
                        if (previewRemovedIds.Contains(original.ActionPlanId.Value) || !spaceSeen.Add(original.ActionPlanId.Value)) continue;
                        if (!candidateLookup.TryGetValue(original.ActionPlanId.Value, out var candidate)) candidate = original.CopyForPreview();
                        spaceCandidates.Add(candidate);
                    }
                }
                var previewChanges = BuildBudgetChanges(budgetContext, candidateAdded, candidateEvaluated, removedPlans, originals);
                if (budgetContext != null && budgetContext.HasAuthority)
                {
                    foreach (var change in previewChanges)
                    {
                        if (!change.IsNewReservation && change.Delta <= 0) continue;
                        var owner = candidateLookup[change.PlanId.Value];
                        string permission = budgetContext.Authority.ValidateSubmissionAuthority(
                            budgetContext.Issuer, owner.OwnerUnitId, owner.ActionType, change.WindowId);
                        if (permission != null) return ScheduleEditTransactionResult.Rejected(permission, baseRevision);
                    }
                    string budgetError = budgetContext.Authority.ValidateBudget(previewChanges, budgetContext);
                    if (budgetError != null) return ScheduleEditTransactionResult.Rejected(budgetError, baseRevision);
                }
                var projections = new List<SchedulePlanPreview>();
                if (MovementSpacePort is IEditableMovementSpacePreviewPort spacePreview)
                {
                    string spaceError = spacePreview.ValidateMovementSpacePreview(spaceCandidates, baseRevision, currentTick);
                    if (spaceError != null) return ScheduleEditTransactionResult.Rejected(spaceError, baseRevision);
                }
                foreach (var item in evaluation.Evaluations)
                {
                    var copy = candidateLookup[item.PlanId.Value];
                    IReadOnlyList<GridPoint> path = Array.Empty<GridPoint>();
                    if (copy.IsOrdinaryMove && _evaluator.PathCalculator is LogicGridMovementPathCalculator calculator)
                    {
                        var result = calculator.FindPathFor(copy, copy.StartTick, spaceCandidates);
                        if (!result.Succeeded) return ScheduleEditTransactionResult.Rejected(result.FailureCode, baseRevision);
                        path = Array.AsReadOnly(new List<GridPoint>(result.Path).ToArray());
                    }
                    projections.Add(new SchedulePlanPreview(ActionPlanSnapshot.From(copy), path));
                }
                var previewEdits = new List<AppliedScheduleEdit>(appliedEdits.Count);
                for (int i = 0; i < appliedEdits.Count; i++)
                {
                    AppliedScheduleEdit edit = appliedEdits[i];
                    long resulting = edit.ResultingStartTick;
                    for (int e = 0; e < evaluation.Evaluations.Count; e++)
                    {
                        if (evaluation.Evaluations[e].PlanId == edit.PlanId)
                        {
                            resulting = evaluation.Evaluations[e].NewStartTick;
                            break;
                        }
                    }
                    previewEdits.Add(edit with { ResultingStartTick = resulting });
                }

                return new ScheduleEditTransactionResult(
                    Committed: false, IsPreview: true, RejectionCode: null,
                    BaseScheduleRevision: baseRevision, ResultingScheduleRevision: baseRevision,
                    AppliedEdits: previewEdits, Evaluations: evaluation.Evaluations,
                    AddedPlanIds: Array.Empty<ActionPlanId>(),
                    RemovedPlanIds: ToIds(removedPlans),
                    RipplePlanIds: RippleIds(evaluation), PreviewPlans: projections.AsReadOnly());
            }

            // —— 阶段 7：提交（此时才占用正式 ID、注册、写 Lane、+1 修订号）——
            long nextRevision = baseRevision + 1L;

            // 提交前的排程投影快照：空间整批替换失败时用它把本批已改写的计划恢复原状。
            var planLookup = new Dictionary<long, ActionPlan>();
            var projectionSnapshots = new List<SpaceProjectionSnapshot>();
            // 任务 07「必须产出」4 第三段第 1 条：为本次求值覆盖到的每个 IsOrdinary 计划捕获
            // **改写前**的 ReservedTurnBudgetTicks。捕获必须早于 ApplyEvaluatedProjections，
            // 因为那里会调用 plan.SyncEditableReservation() 把该字段改写成新成本（晚取就丢旧值）。
            var capturedReserved = new Dictionary<long, int>();
            var evaluatedPlans = new List<ActionPlan>();
            for (int i = 0; i < addedPlans.Count; i++) planLookup[addedPlans[i].ActionPlanId.Value] = addedPlans[i];
            for (int i = 0; i < evaluation.Evaluations.Count; i++)
            {
                ActionPlan plan;
                if (!planLookup.TryGetValue(evaluation.Evaluations[i].PlanId.Value, out plan))
                {
                    plan = _authority.Registry.Find(evaluation.Evaluations[i].PlanId);
                    if (plan == null) continue;
                    planLookup[plan.ActionPlanId.Value] = plan;
                }
                if (!plan.IsOrdinary) continue;
                if (!capturedReserved.ContainsKey(plan.ActionPlanId.Value))
                {
                    capturedReserved[plan.ActionPlanId.Value] = plan.ReservedTurnBudgetTicks;
                    evaluatedPlans.Add(plan);
                }
                projectionSnapshots.Add(new SpaceProjectionSnapshot(plan));
            }

            // Add 候选同样要在求值改写前捕获旧预留：新计划刚由工厂创建，
            // ReservedTurnBudgetTicks 恒为 0（工厂不分配账本），这里显式记录以保证
            // "ExpectedReserved 来自捕获值"这一规则对所有候选一致。
            for (int i = 0; i < addedPlans.Count; i++)
            {
                if (!addedPlans[i].IsOrdinary) continue;
                if (capturedReserved.ContainsKey(addedPlans[i].ActionPlanId.Value)) continue;
                capturedReserved[addedPlans[i].ActionPlanId.Value] = addedPlans[i].ReservedTurnBudgetTicks;
            }

            // Remove 候选：删除后计划不再可读，因此旧预留必须在这里（任何写入之前）捕获。
            for (int i = 0; i < removedPlans.Count; i++)
            {
                if (!removedPlans[i].IsOrdinary) continue;
                if (capturedReserved.ContainsKey(removedPlans[i].ActionPlanId.Value)) continue;
                capturedReserved[removedPlans[i].ActionPlanId.Value] = removedPlans[i].ReservedTurnBudgetTicks;
            }

            // 逐计划重绑绝对 Tick + 应用路径投影（此时注册表与 Lane 仍未被触碰）。
            ApplyEvaluatedProjections(evaluation, nextRevision, explicitRequestPlanIds, planLookup);

            // —— 任务 07「必须产出」4 第三段第 3 条：构造候选变化（纯计算，零写入）——
            //
            // 变化列表按 ActionPlanId 升序稳定排序后再提交，因此提交顺序与
            // 操作顺序、求值顺序、字典枚举顺序都无关（确定性）。
            List<TurnBudgetChangeRequest> budgetChanges = BuildBudgetChanges(
                budgetContext, addedPlans, evaluatedPlans, removedPlans, capturedReserved);
            bool needsBudgetCommit = budgetChanges.Count > 0;

            // —— 任务 07「必须产出」4 第三段第 4 条：校验（零写入）——
            //
            // 必须早于空间端口：空间端口一旦提交成功就无法"撤销空间侧"，
            // 因此预算拒绝只能在它之前发生，才能保证排程、Lane、空间与预算同时零局部写入。
            if (needsBudgetCommit && budgetContext != null && budgetContext.HasAuthority)
            {
                // 提交授权只对**显式**排程编辑成立（00 号规则 18）：系统自动延期不是玩家提交，
                // 不得要求发行者授权，因此这里用 Source 显式区分（任务 07「必须产出」4 第三段第 4 条）。
                if (budgetContext.Source == ResourceChangeSource.ExplicitScheduleEdit)
                {
                    for (int i = 0; i < budgetChanges.Count; i++)
                    {
                        TurnBudgetChangeRequest change = budgetChanges[i];
                        if (!change.IsNewReservation && change.Delta <= 0) continue;
                        ActionPlan owner;
                        if (!planLookup.TryGetValue(change.PlanId.Value, out owner))
                            owner = _authority.Registry.Find(change.PlanId);
                        if (owner == null)
                        {
                            RestoreProjections(projectionSnapshots);
                            return ScheduleEditTransactionResult.Rejected(
                                TurnWindowCodes.BUDGET_LEDGER_INVARIANT, baseRevision);
                        }

                        string authorityError = budgetContext.Authority.ValidateSubmissionAuthority(
                            budgetContext.Issuer, owner.OwnerUnitId, owner.ActionType, change.WindowId);
                        if (authorityError != null)
                        {
                            RestoreProjections(projectionSnapshots);
                            return ScheduleEditTransactionResult.Rejected(authorityError, baseRevision);
                        }
                    }
                }

                string budgetError = budgetContext.Authority.ValidateBudget(budgetChanges, budgetContext);
                if (budgetError != null)
                {
                    RestoreProjections(projectionSnapshots);
                    return ScheduleEditTransactionResult.Rejected(budgetError, baseRevision);
                }
            }

            // 空间整批替换（任务 06）仍在**写注册表与 Lane 之前**完成：它只依赖刚重绑好的
            // 计划投影。失败时 ScheduleEditor 自身把投影恢复原状并整批拒绝，
            // 因此空间侧与排程侧都零局部写入；Lane 与注册表尚未被触碰。
            if (MovementSpacePort != null)
            {
                string spaceError = CommitMovementSpace(
                    evaluation, nextRevision, currentTick, addedPlans, removedPlans);
                if (spaceError != null)
                {
                    // 此时预算**尚未应用**（应用在空间端口成功之后），因此无需 RollbackBudget。
                    RestoreProjections(projectionSnapshots);
                    return ScheduleEditTransactionResult.Rejected(spaceError, baseRevision);
                }
            }

            // —— 任务 07「必须产出」4 第三段第 6 条：空间端口成功后立即应用预算 ——
            if (needsBudgetCommit && budgetContext != null && budgetContext.HasAuthority)
            {
                try
                {
                    budgetContext.Authority.ApplyBudget(budgetChanges, budgetContext, currentTick);
                }
                catch (Exception)
                {
                    // 接口契约：ValidateBudget 通过后 ApplyBudget 不得失败。走到这里是不变量错误：
                    // 排程投影必须恢复原状（账本侧由 ApplyBudget 自身的原子性负责），
                    // 并且异常必须向上抛，不得吞掉或以"继续执行"收场。
                    RestoreProjections(projectionSnapshots);
                    throw;
                }
            }

            // —— 任务 07「必须产出」4 第三段第 7 条：写入段全部包在 try/catch 内 ——
            //
            // 该分支代表不变量错误（例如 ID 预留跳号）。捕获到异常时：
            // ① RollbackBudget——回滚**本次尚未提交成功**的预算变化（唯一允许调用它的场景之一）；
            // ② 恢复投影快照；
            // ③ 把原异常重新抛出（绝不吞掉、绝不以"继续执行"收场）。
            try
            {
                for (int i = 0; i < addedPlans.Count; i++) _ids.ReserveActionPlanId(addedPlans[i].ActionPlanId);
                for (int i = 0; i < addedPlans.Count; i++) _authority.RegisterPlan(addedPlans[i]);
                for (int i = 0; i < removedPlans.Count; i++)
                {
                    _authority.RemoveFromLane(removedPlans[i].OwnerUnitId, removedPlans[i].ActionPlanId);
                }

                CommitLaneProjections(evaluation, removedPlans);

                _authority.IncrementRevision();
            }
            catch (Exception)
            {
                if (needsBudgetCommit && budgetContext != null && budgetContext.HasAuthority)
                {
                    try
                    {
                        budgetContext.Authority.RollbackBudget(budgetChanges, budgetContext, currentTick);
                    }
                    catch (Exception)
                    {
                        // 回滚本身失败是更严重的不变量错误，但绝不能掩盖原始异常：
                        // 原异常携带真正的根因，这里显式忽略回滚异常并继续抛出原异常。
                    }
                }
                RestoreProjections(projectionSnapshots);
                throw;
            }

            // 任务 07「必须产出」4 第三段第 8 条：Remove 的预算释放成功后把计划字段归零，
            // 使 ActionPlan.ReservedTurnBudgetTicks 与账本（已无该计划预留）保持一致。
            for (int i = 0; i < removedPlans.Count; i++)
            {
                if (removedPlans[i].IsOrdinary) removedPlans[i].ReservedTurnBudgetTicks = 0;
            }

            _batchCommittedCount++;
            for (int i = 0; i < evaluation.Evaluations.Count; i++)
                _batchClaimedPlans.Add(evaluation.Evaluations[i].PlanId.Value);
            for (int i = 0; i < removedPlans.Count; i++)
                _batchClaimedPlans.Add(removedPlans[i].ActionPlanId.Value);

            return BuildCommitted(baseRevision, evaluation, addedPlans, removedPlans, appliedEdits);
        }

        // ————————————————————————————————————————————————————————————
        // 操作解析（纯校验，零写入）
        // ————————————————————————————————————————————————————————————

        /// <param name="currentWindowId">
        /// 本次事务的当前窗口（任务 07）：普通计划创建时把它写进 <c>SubmittedWindowId</c>，
        /// 使"计划自己的预算来源记录"与"本事务实际预留的窗口"恒等，
        /// 后续成本差额（<c>ReservationAdjusted</c>）与释放（<c>ReleasedBeforeLock</c>）
        /// 才能回到<strong>原账本</strong>而不是回退到当前窗口。
        /// 为 null（未接入窗口模型，任务 05/06 语义）时沿用既有的空来源。
        /// </param>
        private string ResolveAdd(
            AddOrdinaryPlanOperation add,
            long currentTick,
            ActionPlanId allocated,
            Dictionary<long, ActionPlan> temporaryKeys,
            List<ActionPlan> addedPlans,
            List<ScheduleOperationIntent> intents,
            List<AppliedScheduleEdit> appliedEdits,
            WindowId? currentWindowId)
        {
            if (!add.OwnerUnitId.IsValid) return ActionPlanCodes.ACTION_PLAN_OWNER_INVALID;
            // 任务 09 A 流（产出 1）：RequestedStartTick 可空，null = 本次事务该 Lane 的尾部。
            // 解析口径与"生产者不声明绝对排程"一致：只按只读 Lane 事实给出放置提示，
            // 真正的位点仍由 ScheduleEvaluator 统一求值（只向右避让），失败即稳定码整批拒绝。
            long requestedStartTick = add.RequestedStartTick ?? ResolveLaneTailStartTick(add.OwnerUnitId, currentTick);
            if (requestedStartTick < 0L) return ScheduleCodes.SCHEDULE_OPERATION_INVALID;
            // 不允许把新计划排进过去：它永远到不了自己的到期门禁（见 ScheduleCodes 的说明）。
            if (requestedStartTick < currentTick)
                return ScheduleCodes.SCHEDULE_START_TICK_BEFORE_COMMAND_TICK;
            if (temporaryKeys.ContainsKey(add.TemporaryPlanKey)) return ScheduleCodes.SCHEDULE_OPERATION_DUPLICATE;

            ActionPlanCreationResult created = _factory.TryCreateOrdinary(
                new OrdinaryPlanRequest(
                    add.OwnerUnitId, add.ActionSpecId, add.Facing, add.PrimaryTargetUnitId,
                    add.Destination, currentWindowId, requestedStartTick),
                currentTick,
                allocated);
            if (!created.Succeeded) return created.RejectionCode;

            ActionPlan plan = created.Plan;
            temporaryKeys[add.TemporaryPlanKey] = plan;
            addedPlans.Add(plan);
            intents.Add(new ScheduleOperationIntent(plan, requestedStartTick, IsDirectMove: false, add.AnchorAfterPlanId));
            appliedEdits.Add(new AppliedScheduleEdit(
                ScheduleEditOperationKind.Add, allocated, add.OwnerUnitId, requestedStartTick, requestedStartTick));
            return null;
        }

        /// <summary>
        /// <c>AddOrdinaryPlanOperation.RequestedStartTick == null</c> 时的唯一解析口径：
        /// <strong>本次事务该 Lane 的尾部</strong>。
        ///
        /// 取 <c>max(本次事务目标 Tick, Lane 的 LaneTailTick)</c>：
        /// <list type="bullet">
        /// <item>Lane 尚不存在（该单位首个计划）或 Lane 为空 ⇒ 尾部为 0 ⇒ 回到目标 Tick；</item>
        /// <item>已有计划 ⇒ 取该 Lane 全部计划的最大 <c>EndTick</c>，因此新计划被追加在尾部；</item>
        /// <item>它<strong>不</strong>读取窗口、不读取计划属性、不重采样任何时长，
        /// 也不构成"下一个可提交 Tick"的权威判据——权威判据仍是 <c>ScheduleEvaluator</c> 的求值结果。</item>
        /// </list>
        /// </summary>
        private long ResolveLaneTailStartTick(UnitId ownerUnitId, long currentTick)
        {
            ActorLane lane = _authority.FindLane(ownerUnitId);
            long tail = lane == null ? 0L : lane.LaneTailTick;
            return tail > currentTick ? tail : currentTick;
        }

        private string ResolveMove(
            MoveEditablePlanOperation move,
            long currentTick,
            HashSet<long> claimedPlans,
            List<ScheduleOperationIntent> intents,
            List<AppliedScheduleEdit> appliedEdits,
            List<UnitId> touchedLanes)
        {
            ActionPlan plan = _authority.Registry.Find(move.PlanId);
            if (plan == null) return ScheduleCodes.SCHEDULE_PLAN_NOT_IN_LANE;
            if (plan.IsReaction) return ScheduleCodes.SCHEDULE_REACTION_CANNOT_BE_EDITED;
            if (!plan.IsEditable) return ScheduleCodes.SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED;
            if (move.RequestedStartTick < 0L) return ScheduleCodes.SCHEDULE_OPERATION_INVALID;
            // 只有"直接移动的计划早于其旧投影"是允许的；早于当前 Tick 一律整批拒绝。
            if (move.RequestedStartTick < currentTick)
                return ScheduleCodes.SCHEDULE_START_TICK_BEFORE_COMMAND_TICK;
            if (!claimedPlans.Add(plan.ActionPlanId.Value))
                return ScheduleCodes.SCHEDULE_EDIT_CONFLICT_IN_BATCH;
            if (_authority.LaneOfPlan(plan.ActionPlanId) == null)
                return ScheduleCodes.SCHEDULE_PLAN_NOT_IN_LANE;

            touchedLanes.Add(plan.OwnerUnitId);
            intents.Add(new ScheduleOperationIntent(plan, move.RequestedStartTick, IsDirectMove: true, move.AnchorAfterPlanId));
            appliedEdits.Add(new AppliedScheduleEdit(
                ScheduleEditOperationKind.Move, plan.ActionPlanId, plan.OwnerUnitId,
                move.RequestedStartTick, move.RequestedStartTick));
            return null;
        }

        private string ResolveRemove(
            RemoveEditablePlanOperation remove,
            HashSet<long> claimedPlans,
            List<ActionPlan> removedPlans,
            List<AppliedScheduleEdit> appliedEdits,
            List<UnitId> touchedLanes)
        {
            ActionPlan plan = _authority.Registry.Find(remove.PlanId);
            if (plan == null) return ScheduleCodes.SCHEDULE_PLAN_NOT_IN_LANE;
            if (plan.IsReaction) return ScheduleCodes.SCHEDULE_REACTION_CANNOT_BE_EDITED;
            if (!plan.IsEditable) return ScheduleCodes.SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED;
            if (!claimedPlans.Add(plan.ActionPlanId.Value))
                return ScheduleCodes.SCHEDULE_EDIT_CONFLICT_IN_BATCH;
            if (_authority.LaneOfPlan(plan.ActionPlanId) == null)
                return ScheduleCodes.SCHEDULE_PLAN_NOT_IN_LANE;

            touchedLanes.Add(plan.OwnerUnitId);
            removedPlans.Add(plan);
            appliedEdits.Add(new AppliedScheduleEdit(
                ScheduleEditOperationKind.Remove, plan.ActionPlanId, plan.OwnerUnitId,
                plan.StartTick, plan.StartTick));
            return null;
        }

        /// <summary>
        /// 显式插入锚点校验：锚点必须存在、不得指向自身，且锚点关系<strong>不得成环</strong>。
        /// 任一失败即整批拒绝（<c>SCHEDULE_ANCHOR_*</c>）。
        /// </summary>
        private string ValidateAnchors(
            IReadOnlyList<ScheduleEditOperation> operations, Dictionary<long, ActionPlan> temporaryKeys)
        {
            var anchors = new Dictionary<long, long>();
            for (int i = 0; i < operations.Count; i++)
            {
                ScheduleEditOperation operation = operations[i];
                ActionPlanId planId;
                ActionPlanId anchorId;
                switch (operation)
                {
                    case AddOrdinaryPlanOperation add:
                        if (!temporaryKeys.TryGetValue(add.TemporaryPlanKey, out ActionPlan added)) continue;
                        planId = added.ActionPlanId;
                        anchorId = add.AnchorAfterPlanId;
                        break;
                    case MoveEditablePlanOperation move:
                        planId = move.PlanId;
                        anchorId = move.AnchorAfterPlanId;
                        break;
                    default:
                        continue;
                }

                if (!anchorId.IsValid) continue;
                if (anchorId == planId) return ScheduleCodes.SCHEDULE_ANCHOR_AFTER_SELF;
                if (_authority.Registry.Find(anchorId) == null) return ScheduleCodes.SCHEDULE_ANCHOR_UNKNOWN;
                anchors[planId.Value] = anchorId.Value;
            }

            foreach (KeyValuePair<long, long> entry in anchors)
            {
                long current = entry.Value;
                int guard = 0;
                while (current != 0L)
                {
                    if (current == entry.Key) return ScheduleCodes.SCHEDULE_ANCHOR_CYCLIC;
                    if (++guard > anchors.Count + 1) return ScheduleCodes.SCHEDULE_ANCHOR_CYCLIC;
                    if (!anchors.TryGetValue(current, out long next)) break;
                    current = next;
                }
            }

            return null;
        }

        // ————————————————————————————————————————————————————————————
        // 全局上限（消费 ScheduleLimits 的三个上限与最大排程视野）
        // ————————————————————————————————————————————————————————————

        private string CheckHorizon(ScheduleEvaluationResult evaluation, long currentTick)
        {
            long limit = currentTick + Limits.MaxPreScheduleHorizonTicks;
            for (int i = 0; i < evaluation.Evaluations.Count; i++)
            {
                ScheduleOperationEvaluation item = evaluation.Evaluations[i];
                if (item.NewStartTick > limit) return ScheduleCodes.SCHEDULE_HORIZON_EXCEEDED;
            }
            return null;
        }

        private string CheckLaneQueueLimits(IReadOnlyList<ActionPlan> removed, IReadOnlyList<ActionPlan> added)
        {
            var removedIds = new HashSet<long>();
            for (int i = 0; i < removed.Count; i++) removedIds.Add(removed[i].ActionPlanId.Value);

            var addedByLane = new Dictionary<long, int>();
            var addedIds = new HashSet<long>();
            for (int i = 0; i < added.Count; i++)
            {
                addedIds.Add(added[i].ActionPlanId.Value);
                addedByLane.TryGetValue(added[i].OwnerUnitId.Value, out int count);
                addedByLane[added[i].OwnerUnitId.Value] = count + 1;
            }

            for (int i = 0; i < _authority.Lanes.Count; i++)
            {
                ActorLane lane = _authority.Lanes[i];
                IReadOnlyList<ActionPlan> plans = lane.Plans;
                int pending = 0;
                for (int p = 0; p < plans.Count; p++)
                {
                    if (removedIds.Contains(plans[p].ActionPlanId.Value)) continue;
                    if (plans[p].IsTerminal) continue;
                    if (addedIds.Contains(plans[p].ActionPlanId.Value)) continue;
                    pending++;
                }
                if (addedByLane.TryGetValue(lane.UnitId.Value, out int extra)) pending += extra;
                if (pending > Limits.MaxQueuedPlansPerLane)
                    return ScheduleCodes.SCHEDULE_LANE_QUEUE_LIMIT_EXCEEDED;
            }

            foreach (KeyValuePair<long, int> entry in addedByLane)
            {
                if (_authority.FindLane(new UnitId(entry.Key)) != null) continue;
                if (entry.Value > Limits.MaxQueuedPlansPerLane)
                    return ScheduleCodes.SCHEDULE_LANE_QUEUE_LIMIT_EXCEEDED;
            }

            return null;
        }

        // ————————————————————————————————————————————————————————————
        // 提交：把求值结果落到权威投影
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 逐计划重绑绝对 Tick 并应用路径投影。
        ///
        /// 注意：<strong>本批新增</strong>的计划此时可能尚未写进注册表（注册发生在提交阶段），
        /// 因此调用方必须把新计划作为 <paramref name="lookup"/> 的一部分传进来；
        /// 已注册计划仍以注册表为准。
        /// </summary>
        // ————————————————————————————————————————————————————————————
        // 提交：TurnBudget 候选变化（任务 07「必须产出」4 第三段）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 把本次事务的候选计划投影翻译成<strong>稳定的</strong>预算变化列表（纯计算、零写入）。
        ///
        /// 规则（任务 07「必须产出」4 第三段第 3 条）：
        /// <list type="bullet">
        /// <item>Add 的普通计划：<c>IsNewReservation = true</c>、窗口 = 当前窗口
        /// （为 null ⇒ 用"无效窗口"候选，由 <c>ValidateBudget</c> 以
        /// <c>BUDGET_SOURCE_CLOSED_OR_MISMATCH</c> 整批拒绝）、<c>Delta = 成本</c>、<c>ExpectedReserved = 0</c>、
        /// <c>Kind = Reserved</c>；</item>
        /// <item>既有普通计划且成本相对捕获值变化：窗口 = <strong>计划自己的 SubmittedWindowId</strong>
        /// （为 null ⇒ 同样整批拒绝；<strong>绝不</strong>回退到当前窗口）、<c>Delta = 新成本 - 旧预留</c>、
        /// <c>Kind = ReservationAdjusted</c>；</item>
        /// <item>Remove 的普通计划且旧预留 &gt; 0：窗口 = 计划的 <c>SubmittedWindowId</c>
        /// （为 null ⇒ 跳过释放并继续）、<c>Delta = -旧预留</c>、<c>Kind = ReleasedBeforeLock</c>；</item>
        /// <item>反应计划（<c>IsReaction</c>）<strong>一律不参与</strong>窗口预算——它们用肾上腺素账本。</item>
        /// </list>
        ///
        /// 同一计划在多处出现时按"Add → 求值 → Remove"的固定优先级取<strong>第一次</strong>出现，
        /// 从而保证排序键（<c>ActionPlanId</c> 升序）唯一、结果与遍历顺序无关。
        /// </summary>
        private static List<TurnBudgetChangeRequest> BuildBudgetChanges(
            ScheduleBudgetContext context,
            IReadOnlyList<ActionPlan> addedPlans,
            IReadOnlyList<ActionPlan> evaluatedPlans,
            IReadOnlyList<ActionPlan> removedPlans,
            IReadOnlyDictionary<long, int> capturedReserved)
        {
            var changes = new List<TurnBudgetChangeRequest>();
            var seen = new HashSet<long>();

            // —— Add 候选：首次预留（从当前窗口的 Available 转入该计划名下的 Reserved）——
            if (addedPlans != null)
            {
                for (int i = 0; i < addedPlans.Count; i++)
                {
                    ActionPlan plan = addedPlans[i];
                    if (plan == null || !plan.IsOrdinary) continue;
                    if (!seen.Add(plan.ActionPlanId.Value)) continue;

                    int cost = plan.BudgetCostTicks;
                    changes.Add(new TurnBudgetChangeRequest(
                        plan.ActionPlanId,
                        plan.SubmittedWindowId ?? WindowIdOf(context),
                        cost,
                        cost,
                        // 冻结契约（任务 07「必须产出」4 第三段第 3 条）：Add 候选的
                        // ExpectedReserved 恒为 0——该计划 ID 是本次事务新分配的，账本里
                        // 不可能存在它的预留，因此端口会同时校验 held == 0 与 held != 0 为漂移。
                        // 这里**不得**读 plan.ReservedTurnBudgetTicks：那是计划自己的投影字段
                        // （创建时即等于成本），不是账本事实。
                        expectedReserved: 0,
                        isNewReservation: true,
                        TurnBudgetChangeKind.Reserved));
                }
            }

            // —— 既有计划：成本差额（Move 链重算可能改变路径权重 ⇒ 必须重算差额）——
            if (evaluatedPlans != null)
            {
                for (int i = 0; i < evaluatedPlans.Count; i++)
                {
                    ActionPlan plan = evaluatedPlans[i];
                    if (plan == null || !plan.IsOrdinary) continue;
                    if (!seen.Add(plan.ActionPlanId.Value)) continue;

                    int oldReserved = ExpectedReservedOf(capturedReserved, plan.ActionPlanId);
                    int newCost = plan.BudgetCostTicks;
                    if (newCost == oldReserved) continue;   // 成本中性：不产生任何账本变化

                    changes.Add(new TurnBudgetChangeRequest(
                        plan.ActionPlanId,
                        plan.SubmittedWindowId ?? WindowIdOf(context),
                        newCost,
                        newCost - oldReserved,
                        oldReserved,
                        isNewReservation: false,
                        TurnBudgetChangeKind.ReservationAdjusted));
                }
            }

            // —— Remove 候选：锁定前终态释放未消费预留 ——
            if (removedPlans != null)
            {
                for (int i = 0; i < removedPlans.Count; i++)
                {
                    ActionPlan plan = removedPlans[i];
                    if (plan == null || !plan.IsOrdinary) continue;
                    if (!seen.Add(plan.ActionPlanId.Value)) continue;

                    int oldReserved = ExpectedReservedOf(capturedReserved, plan.ActionPlanId);
                    if (oldReserved <= 0) continue;
                    // 来源窗口缺失 ⇒ 跳过释放并继续（冻结契约：找不到来源就不释放，
                    // 也不把释放额转移到当前窗口）。
                    if (!plan.SubmittedWindowId.HasValue) continue;

                    changes.Add(new TurnBudgetChangeRequest(
                        plan.ActionPlanId,
                        plan.SubmittedWindowId.Value,
                        0,
                        -oldReserved,
                        oldReserved,
                        isNewReservation: false,
                        TurnBudgetChangeKind.ReleasedBeforeLock));
                }
            }

            // 确定性：按 ActionPlanId 升序稳定排序（同一计划理论上只出现一次，平局再按窗口/类别定序）。
            changes.Sort(CompareBudgetChanges);
            return changes;
        }

        private static int CompareBudgetChanges(TurnBudgetChangeRequest a, TurnBudgetChangeRequest b)
        {
            int byPlan = a.PlanId.Value.CompareTo(b.PlanId.Value);
            if (byPlan != 0) return byPlan;
            int byWindow = a.WindowId.Value.CompareTo(b.WindowId.Value);
            if (byWindow != 0) return byWindow;
            return ((int)a.Kind).CompareTo((int)b.Kind);
        }

        private static int ExpectedReservedOf(IReadOnlyDictionary<long, int> captured, ActionPlanId planId)
            => captured != null && captured.TryGetValue(planId.Value, out int value) ? value : 0;

        private static WindowId WindowIdOf(ScheduleBudgetContext context)
            => context != null && context.CurrentWindowId.HasValue
                ? context.CurrentWindowId.Value
                : default;

        /// <summary>把本批已改写的排程投影恢复成提交前的形状（零局部写入的排程侧一半）。</summary>
        private static void RestoreProjections(IReadOnlyList<SpaceProjectionSnapshot> snapshots)
        {
            if (snapshots == null) return;
            for (int i = 0; i < snapshots.Count; i++) snapshots[i].Restore();
        }

        private void ApplyEvaluatedProjections(
            ScheduleEvaluationResult evaluation,
            long revision,
            ICollection<long> explicitRequestPlanIds,
            IReadOnlyDictionary<long, ActionPlan> lookup)
        {
            for (int i = 0; i < evaluation.Evaluations.Count; i++)
            {
                ScheduleOperationEvaluation item = evaluation.Evaluations[i];
                ActionPlan plan;
                if (lookup == null || !lookup.TryGetValue(item.PlanId.Value, out plan))
                    plan = _authority.Registry.Find(item.PlanId);
                if (plan == null) continue;
                if (!plan.IsOrdinary) continue;
                plan.RebindAbsoluteTicks(item.NewStartTick);

                // 路径重算在绝对 Tick 重绑<strong>之后</strong>应用：它按新的 StartTick
                // 重算 EndTick 与预算，且预算使用路径权重而不是边数。
                MovementPathProjection projection = evaluation.PathProjectionOf(item.PlanId);
                if (projection != null) plan.SetPathProjection(projection.EdgeCount, projection.WeightUnits);

                plan.LastEditedScheduleRevision = revision;
                plan.SyncEditableReservation();
                // 请求起点<strong>只</strong>由显式命令的直接 Move 更新：系统自动延期不改它。
                if (explicitRequestPlanIds != null && explicitRequestPlanIds.Contains(item.PlanId.Value))
                    plan.LastRequestedStartTick = item.NewStartTick;
            }
        }

        private void CommitEvaluatedPlans(
            ScheduleEvaluationResult evaluation,
            long revision,
            ICollection<long> explicitRequestPlanIds)
            => ApplyEvaluatedProjections(evaluation, revision, explicitRequestPlanIds, null);

        /// <summary>
        /// Lane 投影整体替换为<strong>求值器给出的规范顺序</strong>：
        /// 它是唯一来源，因此 Lane 与计划投影在任何时刻都不会分叉。
        ///
        /// 两条边界（缺一即产生残留）：
        /// （a）只替换<strong>被本次求值覆盖</strong>的 Lane——未被求值的 Lane 必须原样保留；
        /// （b）投影里必须<strong>剔除本次删除的计划</strong>——删除只移除目标，绝不重新放回。
        /// </summary>
        private void CommitLaneProjections(
            ScheduleEvaluationResult evaluation, IReadOnlyList<ActionPlan> removedPlans)
        {
            var removedIds = new HashSet<long>();
            if (removedPlans != null)
            {
                for (int i = 0; i < removedPlans.Count; i++) removedIds.Add(removedPlans[i].ActionPlanId.Value);
            }

            for (int i = 0; i < _authority.Lanes.Count; i++)
            {
                ActorLane lane = _authority.Lanes[i];
                if (!evaluation.CoversLane(lane.UnitId)) continue;

                IReadOnlyList<ActionPlan> ordering = evaluation.OrderingOf(lane.UnitId);
                if (removedIds.Count == 0)
                {
                    _authority.ReplaceLaneProjection(lane.UnitId, ordering);
                    continue;
                }

                var kept = new List<ActionPlan>(ordering.Count);
                for (int p = 0; p < ordering.Count; p++)
                {
                    if (removedIds.Contains(ordering[p].ActionPlanId.Value)) continue;
                    kept.Add(ordering[p]);
                }
                _authority.ReplaceLaneProjection(lane.UnitId, kept);
            }
        }

        /// <summary>单个计划在一次空间整批替换<strong>之前</strong>的排程投影（失败回滚用）。</summary>
        private readonly struct SpaceProjectionSnapshot
        {
            private readonly ActionPlan _plan;
            private readonly Actions.ActionType _actionType;
            private readonly long _startTick;
            private readonly int _edgeCount;
            private readonly int _weightUnits;
            private readonly long _lastRequestedStartTick;
            private readonly long _lastEditedScheduleRevision;
            private readonly int _reservedTurnBudgetTicks;

            public SpaceProjectionSnapshot(ActionPlan plan)
            {
                _plan = plan;
                _actionType = plan.ActionType;
                _startTick = plan.StartTick;
                _edgeCount = plan.ResolvedPathEdgeCount;
                _weightUnits = plan.ResolvedPathWeightUnits;
                _lastRequestedStartTick = plan.LastRequestedStartTick;
                _lastEditedScheduleRevision = plan.LastEditedScheduleRevision;
                _reservedTurnBudgetTicks = plan.ReservedTurnBudgetTicks;
            }

            public void Restore()
            {
                // 起点重绑会按动作族重算 EndTick / BudgetCostTicks（Attack = 前摇 + 后摇）。
                _plan.RebindAbsoluteTicks(_startTick);

                // 任务 09 A 流（R-A1-D1，最小修复）：只有**路径型**动作（Move/Dodge）才有路径投影。
                // 原先这里无条件调用 SetPathProjection，会把 Move 的时长公式
                // （MoveDuration + Recovery）套到 Attack/Guard 上：被拒绝的事务一旦求值覆盖到
                // 一个既有攻击计划，它的 EndTick 与 BudgetCostTicks 就会被静默改写成
                // "StartTick + RecoveryTicks"，与窗口账本里仍然持有的预留额不再相等，
                // 于是该计划在到期门禁处必定以 RESOURCE_COMMIT_ERROR 失败。
                // 这与 00 号规则 28「失败事务零局部写入」以及
                // ActionPlanFactory（只对 Move/Dodge 建路径投影）的口径都矛盾。
                if (_actionType == Actions.ActionType.Move || _actionType == Actions.ActionType.Dodge)
                    _plan.SetPathProjection(_edgeCount, _weightUnits);

                _plan.LastRequestedStartTick = _lastRequestedStartTick;
                _plan.LastEditedScheduleRevision = _lastEditedScheduleRevision;
                _plan.ReservedTurnBudgetTicks = _reservedTurnBudgetTicks;
            }
        }

        /// <summary>
        /// 把本次求值覆盖到的普通计划交给空间端口<strong>整批替换</strong>段与预留。
        ///
        /// 失败语义（任务包「必须产出」6）：端口必须已自行回滚它的空间写入；
        /// 本方法再把已改写的排程投影恢复成提交前的形状并返回稳定错误码，
        /// 于是本事务整批拒绝且<strong>零局部写入</strong>（空间侧旧段/预留继续权威）。
        /// </summary>
        private string CommitMovementSpace(
            ScheduleEvaluationResult evaluation, long revision, long currentTick,
            IReadOnlyList<ActionPlan> addedPlans, IReadOnlyList<ActionPlan> removedPlans)
        {
            var snapshots = new List<SpaceProjectionSnapshot>();
            var candidates = new List<ActionPlan>();
            var seen = new HashSet<long>();
            var removedIds = new HashSet<long>();
            if (removedPlans != null)
            {
                for (int i = 0; i < removedPlans.Count; i++) removedIds.Add(removedPlans[i].ActionPlanId.Value);
            }

            // 本次新增的计划（注册表与 Lane 都还没写，因此不能只从 Lane 投影里取）。
            if (addedPlans != null)
            {
                for (int i = 0; i < addedPlans.Count; i++)
                {
                    AddSpaceCandidate(addedPlans[i], snapshots, candidates, seen);
                }
            }

            for (int i = 0; i < _authority.Lanes.Count; i++)
            {
                ActorLane lane = _authority.Lanes[i];
                if (!evaluation.CoversLane(lane.UnitId)) continue;
                IReadOnlyList<ActionPlan> ordering = evaluation.OrderingOf(lane.UnitId);
                for (int p = 0; p < ordering.Count; p++)
                {
                    ActionPlan plan = ordering[p];
                    if (plan == null) continue;
                    if (removedIds.Contains(plan.ActionPlanId.Value)) continue;
                    AddSpaceCandidate(plan, snapshots, candidates, seen);
                }
            }
            if (candidates.Count == 0) return null;

            string error = MovementSpacePort.RebuildMovementSpace(candidates, revision, currentTick);
            if (error == null)
            {
                if (CommittedSpacePlanSink != null)
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        CommittedSpacePlanSink(candidates[i].ActionPlanId, candidates[i].StartTick,
                            revision, currentTick);
                    }
                }
                return null;
            }

            for (int i = 0; i < snapshots.Count; i++) snapshots[i].Restore();
            return error;
        }

        private static void AddSpaceCandidate(
            ActionPlan plan, List<SpaceProjectionSnapshot> snapshots,
            List<ActionPlan> candidates, HashSet<long> seen)
        {
            if (plan == null) return;
            if (!plan.IsOrdinaryMove) return;
            if (!plan.IsEditable) return;
            if (!seen.Add(plan.ActionPlanId.Value)) return;
            snapshots.Add(new SpaceProjectionSnapshot(plan));
            candidates.Add(plan);
        }

        private static ScheduleEditTransactionResult BuildCommitted(
            long baseRevision,
            ScheduleEvaluationResult evaluation,
            IReadOnlyList<ActionPlan> added,
            IReadOnlyList<ActionPlan> removed,
            IReadOnlyList<AppliedScheduleEdit> edits = null)
        {
            return new ScheduleEditTransactionResult(
                Committed: true, IsPreview: false, RejectionCode: null,
                BaseScheduleRevision: baseRevision, ResultingScheduleRevision: baseRevision + 1L,
                AppliedEdits: edits ?? Array.Empty<AppliedScheduleEdit>(),
                Evaluations: evaluation.Evaluations,
                AddedPlanIds: ToIds(added),
                RemovedPlanIds: ToIds(removed),
                RipplePlanIds: RippleIds(evaluation));
        }

        private static IReadOnlyList<ActionPlanId> ToIds(IReadOnlyList<ActionPlan> plans)
        {
            if (plans == null || plans.Count == 0) return Array.Empty<ActionPlanId>();
            var ids = new List<ActionPlanId>(plans.Count);
            for (int i = 0; i < plans.Count; i++) ids.Add(plans[i].ActionPlanId);
            ids.Sort((a, b) => a.Value.CompareTo(b.Value));
            return ids;
        }

        private static IReadOnlyList<ActionPlanId> RippleIds(ScheduleEvaluationResult evaluation)
        {
            var ids = new List<ActionPlanId>();
            for (int i = 0; i < evaluation.Evaluations.Count; i++)
            {
                if (evaluation.Evaluations[i].IsDirectMove) continue;
                if (!evaluation.Evaluations[i].Moved) continue;
                ids.Add(evaluation.Evaluations[i].PlanId);
            }
            ids.Sort((a, b) => a.Value.CompareTo(b.Value));
            return ids;
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public static string Describe(ScheduleEditTransactionResult result)
        {
            if (result == null) return "<null>";
            if (!result.Succeeded) return "rejected(" + result.RejectionCode + ")";
            return (result.IsPreview ? "preview" : "commit") +
                   " rev=" + result.BaseScheduleRevision.ToString(CultureInfo.InvariantCulture) +
                   "->" + result.ResultingScheduleRevision.ToString(CultureInfo.InvariantCulture) +
                   " edits=" + result.AppliedEdits.Count.ToString(CultureInfo.InvariantCulture) +
                   " added=" + result.AddedPlanIds.Count.ToString(CultureInfo.InvariantCulture) +
                   " removed=" + result.RemovedPlanIds.Count.ToString(CultureInfo.InvariantCulture) +
                   " ripple=" + result.RipplePlanIds.Count.ToString(CultureInfo.InvariantCulture);
        }
    }
}
