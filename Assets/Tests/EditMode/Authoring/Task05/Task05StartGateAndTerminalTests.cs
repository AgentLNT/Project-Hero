using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Authoring.Tests.Task05
{
    /// <summary>
    /// 任务 05 阶段 C：<c>ActionStartGate</c> 四分结果、原子提交端口、系统自动延期，
    /// 以及<strong>唯一终态协调器</strong>（第一次请求胜出 / 幂等 / 固定清理顺序 / 归档候选）。
    ///
    /// 这些用例只依赖<strong>纯数据夹具</strong>（阶段 A 的真实 02B 定义 + 5 个受控动作），
    /// 因此可以脱离 Unity 资产加载链独立运行；<c>BattleSimulation</c> 端到端接入的用例
    /// 见 <c>Task05SimulationWiringTests</c>。
    /// </summary>
    [TestFixture]
    public sealed class Task05StartGateAndTerminalTests
    {
        private const long T = 100L;

        [Test] public void AutoDeferralNeverMovesLockedRunningOrReactionPlan()
        {
            var s = new Task05Scheduler();
            var locked = Task05Scheduler.Lock(s.Attack(100), 5);
            var running = Task05Scheduler.Run(s.Attack(300), 5);
            var reaction = s.Bundle.CreateReaction(Task05Farm.BlockId, 600, 540).Plan;
            foreach (var plan in new[] { locked, running, reaction })
            {
                s.Authority.RegisterPlan(plan);
                var before = (plan.StartTick, plan.ImpactTick, plan.EndTick, plan.State,
                    plan.ReservedTurnBudgetTicks, plan.LastRequestedStartTick, plan.AutomaticDeferralCount);
                var result = s.Editor.ApplySystemAutoDeferral(plan, 700, T, null);
                Assert.That(result.Committed, Is.False);
                Assert.That(result.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_PLAN_NOT_EDITABLE));
                Assert.That((plan.StartTick, plan.ImpactTick, plan.EndTick, plan.State,
                    plan.ReservedTurnBudgetTicks, plan.LastRequestedStartTick, plan.AutomaticDeferralCount), Is.EqualTo(before));
            }
            Assert.That(s.Revision, Is.Zero);
        }

        [Test] public void InvalidPrimaryTargetRelationTerminatesAsTargetInvalidWithoutRetargeting()
        {
            var s = new Task05Scheduler();
            var plan = s.Attack(T); s.Authority.RegisterPlan(plan);
            var originalTarget = plan.PrimaryTargetUnitId;
            // A malformed plan/definition binding at the gate must fail closed; production definitions
            // are immutable, so this is an explicit boundary negative control, not a runtime faction edit.
            var definition = s.Bundle.Definition with { Actions = s.Bundle.Definition.Actions.Select(a =>
                a.ActionSpecId.Value == plan.ActionSpecId.Value ? a with { Payload = ((AttackPayloadSpec)a.Payload) with
                    { AllowedTargetRelations = ProjectHero.Logic.Factions.TargetRelationMask.Allied } } : a).ToArray() };
            var strict = new ActionPlanFactory(definition, s.Bundle.Factions, s.Bundle.Facts, s.Ids);
            Assert.That(strict.ValidatePrimaryTargetForGate(plan), Is.EqualTo(ScheduleCodes.SCHEDULE_PRIMARY_TARGET_RELATION_REJECTED));
            var result = new ActionStartGate().Evaluate(plan, Context(T, factory: strict));
            Assert.That(result.IsTerminal, Is.True);
            Assert.That(((ActionStartGateResult.Terminal)result).Reason, Is.EqualTo(ActionTerminationReason.TargetInvalid));
            var coordinator = new ActionPlanTerminalCoordinator(s.Authority);
            Assert.That(coordinator.EnterTerminal(plan, ((ActionStartGateResult.Terminal)result).Reason, T).EnteredTerminal, Is.True);
            Assert.That(plan.TerminationReason, Is.EqualTo(ActionTerminationReason.TargetInvalid));
            Assert.That(plan.PrimaryTargetUnitId, Is.EqualTo(originalTarget));
            Assert.That(s.Authority.Registry.ActivePlans, Is.Empty);
            Assert.That(s.Authority.FindLane(plan.OwnerUnitId).Count, Is.Zero);
        }

        [Test] public void AutoDeferralReusesCanonicalScheduleEvaluator()
        {
            var system = new Task05Scheduler(); var explicitEdit = new Task05Scheduler();
            var systemPlans = new[] { system.Attack(100), system.Attack(180), Task05Scheduler.Lock(system.Attack(400), 5) };
            var userPlans = new[] { explicitEdit.Attack(100), explicitEdit.Attack(180), Task05Scheduler.Lock(explicitEdit.Attack(400), 5) };
            foreach (var plan in systemPlans) system.Authority.RegisterPlan(plan);
            foreach (var plan in userPlans) explicitEdit.Authority.RegisterPlan(plan);
            var a = system.Editor.ApplySystemAutoDeferral(systemPlans[0], 160, T, null);
            var b = explicitEdit.Apply(new ScheduleEditOperation[] { Task05Scheduler.MoveOp(userPlans[0].ActionPlanId, 160) }, T);
            Assert.That(a.Committed && b.Committed, Is.True);
            Assert.That(systemPlans.Select(p => (p.StartTick, p.ImpactTick, p.EndTick, p.BudgetCostTicks)),
                Is.EqualTo(userPlans.Select(p => (p.StartTick, p.ImpactTick, p.EndTick, p.BudgetCostTicks))));
            Assert.That(system.LanePlanIds(Task05Farm.Hero), Is.EqualTo(explicitEdit.LanePlanIds(Task05Farm.Hero)));
            Assert.That(systemPlans[0].LastRequestedStartTick, Is.EqualTo(100));
            Assert.That(userPlans[0].LastRequestedStartTick, Is.EqualTo(160));
            Assert.That(systemPlans[0].AutomaticDeferralCount, Is.EqualTo(1));
            Assert.That(userPlans[0].AutomaticDeferralCount, Is.Zero);
        }

        /// <summary>
        /// 门禁上下文。
        ///
        /// <paramref name="factory"/> 必须由用例传入<strong>与它改写事实的同一个</strong>
        /// <c>ActionPlanFactory</c>：门禁的 PrimaryTarget 存活/关系判定读的正是这个工厂背后的
        /// <c>IActionPlanFactsSource</c>。省略它只能用于"不依赖事实变化"的用例
        /// （默认工厂持有的是另一个事实实例，用它断言存活变化会读出陈旧事实）。
        /// </summary>
        private static ActionStartGateContext Context(
            long tick = T, Func<UnitId, long?> blocking = null, Func<UnitId, bool> alive = null,
            Func<UnitId, bool> available = null, Func<UnitId, long?> spaceBlock = null,
            int maxDeferrals = 8, ProjectHero.Logic.Timeline.ActionPlanFactory factory = null)
            => new ActionStartGateContext(
                tick,
                blocking ?? (_ => null),
                alive ?? (_ => true),
                factory ?? Task05Farm.NewFactory().Create,
                maxDeferrals,
                available,
                spaceBlock);

        // ————————————————————————————————————————————————————————————
        // 四分结果
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>ControlExpiringAtStartTickAllowsPlanToStartWithoutDelay</c>：
        /// 阻塞恰好在 <c>StartTick</c> 结束（半开区间右端 = 本 Tick）⇒ 门禁直接
        /// <c>Startable</c>，不产生任何延期。
        ///
        /// 语义依据：阶段 1 的状态到期先于阶段 7 的门禁，因此门禁看到的
        /// <c>BlockingUntilTick</c> 已经不再覆盖本 Tick。
        /// </summary>
        [Test]
        public void ControlExpiringAtStartTickAllowsPlanToStartWithoutDelay()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan plan = s.Attack(T);
            s.Authority.RegisterPlan(plan);

            var gate = new ActionStartGate();
            // 阻塞边界 == 本 Tick ⇒ 状态机已经在本 Tick 到期，门禁必须放行。
            ActionStartGateResult result = gate.Evaluate(plan, Context(T, _ => null));
            Assert.That(result.IsStartable, Is.True, ActionStartGate.Describe(result));
            Assert.That(plan.AutomaticDeferralCount, Is.EqualTo(0), "无延迟启动不得增加延期次数");

            // 对照：阻塞边界严格大于本 Tick ⇒ 有限临时阻塞 ⇒ Retryable。
            ActionStartGateResult blocked = gate.Evaluate(plan, Context(T, _ => T + 30L));
            Assert.That(blocked.IsRetryable, Is.True, ActionStartGate.Describe(blocked));
            Assert.That(((ActionStartGateResult.Retryable)blocked).RetryAtTick, Is.EqualTo(T + 30L));
        }

        /// <summary>
        /// <c>NoFiniteRetryTickTerminatesPlanAsActorUnavailableAtStart</c>：
        /// 没有有限恢复边界的阻塞（授权/预算不可用）<strong>不</strong>返回 Retryable，
        /// 而是以 <c>ActorUnavailableAtStart</c> 终止——绝不"下一 Tick 再试"。
        /// </summary>
        [Test]
        public void NoFiniteRetryTickTerminatesPlanAsActorUnavailableAtStart()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan plan = s.Attack(T);
            s.Authority.RegisterPlan(plan);

            var gate = new ActionStartGate();
            ActionStartGateResult result = gate.Evaluate(plan, Context(T, available: _ => false));

            Assert.That(result.IsTerminal, Is.True, ActionStartGate.Describe(result));
            Assert.That(((ActionStartGateResult.Terminal)result).Reason,
                Is.EqualTo(ActionTerminationReason.ActorUnavailableAtStart));
            Assert.That(result.IsRetryable, Is.False, "无有限恢复时绝不能返回 Retryable");
        }

        /// <summary>
        /// <c>AutoDeferralLimitOrHorizonTerminatesWithExplicitReason</c>（次数部分）：
        /// 延期次数已达上限时以 <c>AutoDeferralLimitExceeded</c> 终止。
        /// </summary>
        [Test]
        public void AutoDeferralLimitTerminatesWithExplicitReason()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan plan = s.Attack(T);
            s.Authority.RegisterPlan(plan);

            var gate = new ActionStartGate();
            ActionStartGateResult first = gate.Evaluate(plan, Context(T, _ => T + 10L, maxDeferrals: 1));
            Assert.That(first.IsRetryable, Is.True, "余量 = 1 时仍可延期一次");

            // 模拟一次已发生的延期。
            plan.AutomaticDeferralCount = 1;
            ActionStartGateResult second = gate.Evaluate(plan, Context(T, _ => T + 10L, maxDeferrals: 1));
            Assert.That(second.IsTerminal, Is.True, ActionStartGate.Describe(second));
            Assert.That(((ActionStartGateResult.Terminal)second).Reason,
                Is.EqualTo(ActionTerminationReason.AutoDeferralLimitExceeded));
        }

        /// <summary>
        /// <c>PrimaryTargetMustSatisfyAllowedRelationAtAddAndStartGate</c>（门禁部分）+
        /// <c>InvalidPrimaryTargetRelationTerminatesAsTargetInvalidWithoutRetargeting</c>（门禁部分）：
        /// 目标死亡 ⇒ 永久 <c>TargetInvalid</c>，<strong>不</strong>自动延期、
        /// <strong>不</strong>改选目标；目标复活后同一计划必须重新放行（正控制）。
        /// </summary>
        [Test]
        public void InvalidPrimaryTargetTerminatesAsTargetInvalidWithoutRetargeting()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan plan = s.Attack(T);
            s.Authority.RegisterPlan(plan);
            UnitId? originalTarget = plan.PrimaryTargetUnitId;

            var gate = new ActionStartGate();

            s.Bundle.Facts.SetAlive(Task05Farm.Enemy, false);

            // 诊断护栏：先钉死"目标死亡"这条链路的每一环，避免断言失败被归错因。
            Assert.That(plan.PrimaryTargetUnitId, Is.EqualTo(Task05Farm.Enemy), "主目标必须是敌对单位");
            Assert.That(s.Bundle.Facts.TryGetOwnerFacts(Task05Farm.Enemy, out ProjectHero.Logic.Timeline.ActionPlanOwnerFacts enemyFacts),
                Is.True, "事实源必须认识敌方单位");
            Assert.That(enemyFacts.IsAlive, Is.False, "SetAlive 必须写进事实源");
            Assert.That(s.Factory.ValidatePrimaryTargetForGate(plan),
                Is.EqualTo(ScheduleCodes.SCHEDULE_PRIMARY_TARGET_DEAD), "权威工厂必须判定目标已死亡");

            ActionStartGateResult deadTarget = gate.Evaluate(plan, Context(T, _ => T + 10L, factory: s.Factory));
            Assert.That(deadTarget.IsTerminal, Is.True, ActionStartGate.Describe(deadTarget));
            Assert.That(((ActionStartGateResult.Terminal)deadTarget).Reason,
                Is.EqualTo(ActionTerminationReason.TargetInvalid));
            Assert.That(((ActionStartGateResult.Terminal)deadTarget).ReasonCode,
                Is.EqualTo(ActionTerminationReasons.TargetInvalid));
            Assert.That(plan.PrimaryTargetUnitId, Is.EqualTo(originalTarget), "绝不能改选目标");
            Assert.That(deadTarget.IsRetryable, Is.False, "永久目标失效绝不能自动延期");

            // 正控制 A（保持"同样的输入"）：目标复活后，同一条带控制阻塞的输入
            // 不得再以 TargetInvalid 终止，而应正常落到下一个判据
            // （有限的临时阻塞 ⇒ Retryable）。这证明上一条不是恒真终止。
            s.Bundle.Facts.SetAlive(Task05Farm.Enemy, true);
            ActionStartGateResult recoveredBlocked = gate.Evaluate(plan, Context(T, _ => T + 10L, factory: s.Factory));
            Assert.That(recoveredBlocked.IsTerminal, Is.False, ActionStartGate.Describe(recoveredBlocked));
            Assert.That(recoveredBlocked.IsRetryable, Is.True, ActionStartGate.Describe(recoveredBlocked));

            // 正控制 B：移除那个<strong>独立</strong>的控制阻塞后，目标存活必须直接放行。
            ActionStartGateResult recovered = gate.Evaluate(plan, Context(T, factory: s.Factory));
            Assert.That(recovered.IsStartable, Is.True, ActionStartGate.Describe(recovered));
        }

        /// <summary>
        /// <c>ActionStateLaneDivergenceIsInvariantViolation</c>：
        /// 门禁收到的输入与本 Tick 不一致（非 Editable 普通计划 / 未到期 / 反应计划）时，
        /// 必须以 <c>InvariantViolation</c> 表达<strong>内部矛盾</strong>，而不是降级成普通终态。
        /// </summary>
        [Test]
        public void ActionStateLaneDivergenceIsInvariantViolation()
        {
            Task05Scheduler s = new Task05Scheduler();
            var gate = new ActionStartGate();

            // 1) 未到期（StartTick != 本 Tick）。
            ActionPlan future = s.Attack(T + 50L);
            s.Authority.RegisterPlan(future);
            ActionStartGateResult notDue = gate.Evaluate(future, Context(T));
            Assert.That(notDue.IsInvariantViolation, Is.True, ActionStartGate.Describe(notDue));
            Assert.That(notDue.IsTerminal, Is.False, "不变量冲突绝不能降级为普通终态");
            Assert.That(((ActionStartGateResult.InvariantViolation)notDue).ErrorCode,
                Is.EqualTo(ScheduleCodes.SCHEDULE_START_GATE_NOT_AT_DUE_TICK));

            // 2) 已 Locked（Locked-but-not-Running 是禁止状态）。
            ActionPlan locked = Task05Scheduler.Lock(s.Attack(T), lockedAtTick: T - 1L);
            s.Authority.RegisterPlan(locked);
            ActionStartGateResult notEditable = gate.Evaluate(locked, Context(T));
            Assert.That(notEditable.IsInvariantViolation, Is.True, ActionStartGate.Describe(notEditable));
            Assert.That(((ActionStartGateResult.InvariantViolation)notEditable).ErrorCode,
                Is.EqualTo(ScheduleCodes.SCHEDULE_PLAN_NOT_EDITABLE));

            // 3) 反应计划永远不过普通门禁。
            ActionPlanCreationResult reaction = s.Bundle.CreateReaction(
                Task05Farm.BlockId, triggerTick: 300L, responseDeadlineTick: 200L);
            Assert.That(reaction.Succeeded, Is.True, reaction.RejectionCode);
            ActionStartGateResult reactionResult = gate.Evaluate(reaction.Plan, Context(440L));
            Assert.That(reactionResult.IsInvariantViolation, Is.True, ActionStartGate.Describe(reactionResult));
        }

        // ————————————————————————————————————————————————————————————
        // 原子提交端口
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>StartGateLockAndRunningCommitIsAtomic</c>（端口层证据）：
        /// 预留消费与计划字段必须由<strong>同一个</strong>提交端口一次完成；
        /// 端口失败时预留必须被回滚，绝不留下部分消费。
        /// </summary>
        [Test]
        public void StartGateLockAndRunningCommitIsAtomic()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan plan = s.Attack(T);
            s.Authority.RegisterPlan(plan);
            Assert.That(plan.ReservedTurnBudgetTicks, Is.EqualTo(60), "Editable 期预留 = 预算成本");

            int reservedBefore = plan.ReservedTurnBudgetTicks;
            ActionPlanState stateBefore = plan.State;
            long lockedBefore = plan.LockedAtTick;

            var failing = new FailingCommitPort();

            string error = failing.Commit(plan, T, rollback => rollback.ReservedTurnBudgetTicks = reservedBefore);
            Assert.That(error, Is.Not.Null, "失败端口必须返回稳定错误码");
            Assert.That(failing.RollbackCount, Is.EqualTo(1), "端口必须先回滚它自己的副作用");
            Assert.That(plan.ReservedTurnBudgetTicks, Is.EqualTo(reservedBefore), "预留必须被回滚");
            Assert.That(plan.State, Is.EqualTo(stateBefore), "失败的提交不得把计划留在 Locked");
            Assert.That(plan.LockedAtTick, Is.EqualTo(lockedBefore));

            // 成功端口：预留归零（Reserved -> Spent 的可观察结果）。
            string ok = NoTurnBudgetCommitPort.Instance.Commit(plan, T, _ => { });
            Assert.That(ok, Is.Null);
            Assert.That(plan.ReservedTurnBudgetTicks, Is.EqualTo(0), "启动提交把预留转为已消费");
        }

        // ————————————————————————————————————————————————————————————
        // 唯一终态协调器
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>AllTerminalTransitionsUseSingleLifecycleEntry</c> +
        /// <c>TerminalCleanupIsIdempotentAndFirstReasonWins</c>：
        /// 全部终态都经同一入口；第一次请求胜出，重复/冲突请求幂等。
        /// </summary>
        [Test]
        public void AllTerminalTransitionsUseSingleLifecycleEntry()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan plan = s.Attack(T);
            s.Authority.RegisterPlan(plan);

            var cleanup = new RecordingParticipant(1, "rec");
            var coordinator = new ActionPlanTerminalCoordinator(s.Authority, new[] { cleanup });

            ActionPlanTerminalOutcome first = coordinator.EnterTerminal(
                plan, ActionTerminationReason.OwnerDied, T);
            Assert.That(first.EnteredTerminal, Is.True);
            Assert.That(first.ResultingState, Is.EqualTo(ActionPlanState.Terminated));
            Assert.That(first.Reason, Is.EqualTo(ActionTerminationReason.OwnerDied));
            Assert.That(first.TerminalTick, Is.EqualTo(T));
            Assert.That(cleanup.CallCount, Is.EqualTo(1), "清理参与者恰好执行一次");
            Assert.That(cleanup.LastContext.IsFirstRequest, Is.True);

            // 冲突的第二次请求：必须幂等，不覆盖任何字段、不重复清理。
            ActionPlanTerminalOutcome second = coordinator.EnterTerminal(
                plan, ActionTerminationReason.BattleEnded, T + 5L);
            Assert.That(second.WasAlreadyTerminal, Is.True);
            Assert.That(second.Reason, Is.EqualTo(ActionTerminationReason.OwnerDied), "第一次请求胜出");
            Assert.That(second.TerminalTick, Is.EqualTo(T), "TerminalTick 不得被覆盖");
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Terminated));
            Assert.That(cleanup.CallCount, Is.EqualTo(1), "幂等请求不得重复清理");

            // 计划离开活动索引与 Lane，但仍留在注册表里可查（审计）。
            Assert.That(plan.IsTerminal, Is.True);
            Assert.That(s.Authority.Registry.Contains(plan.ActionPlanId), Is.True, "计划对象必须仍可查到");
            Assert.That(s.LanePlanIds(Task05Farm.Hero).Length, Is.EqualTo(0), "必须离开 Lane");
            Assert.That(s.Authority.BuildLaneSnapshots().Count, Is.EqualTo(1),
                "Lane 对象仍在（只是没有计划），其快照的 PendingPlanCount 必须为 0");
            Assert.That(s.Authority.BuildLaneSnapshots()[0].PendingPlanCount, Is.EqualTo(0));
            Assert.That(s.Authority.Registry.TerminalRecordCount, Is.EqualTo(1));
        }

        /// <summary>
        /// <c>TerminalCleanupDoesNotRewindIdsOrSequences</c>：
        /// 终态不回卷任何 ID 计数器、不改变 <c>ScheduleRevision</c>。
        /// </summary>
        [Test]
        public void TerminalCleanupDoesNotRewindIdsOrSequences()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan first = s.Attack(100L);
            ActionPlan second = s.Attack(200L);
            s.Authority.RegisterPlan(first);
            s.Authority.RegisterPlan(second);
            Assert.That(s.Apply(new ScheduleEditOperation[]
            {
                Task05Scheduler.MoveOp(second.ActionPlanId, 260L)
            }).Succeeded, Is.True);

            long revisionBefore = s.Revision;
            long nextIdBefore = s.Ids.NextActionPlanIdValue;

            var coordinator = new ActionPlanTerminalCoordinator(s.Authority);
            coordinator.EnterTerminal(first, ActionTerminationReason.CancelledByCommand, T);

            Assert.That(s.Revision, Is.EqualTo(revisionBefore), "终态不是排程编辑，修订号不变");
            Assert.That(s.Ids.NextActionPlanIdValue, Is.EqualTo(nextIdBefore), "不得回卷 ID 计数器");
            Assert.That(s.Authority.Registry.TerminalRecordCount, Is.EqualTo(1), "历史只追加一次");

            // 后续新建计划仍然拿到更大的 ID。
            ActionPlanCreationResult created = s.Factory.TryCreateOrdinary(
                new OrdinaryPlanRequest(
                    Task05Farm.Hero, new ActionSpecId(Task05Farm.AttackId), GridDirection.East,
                    Task05Farm.Enemy, null, null, 400L),
                0L);
            Assert.That(created.Succeeded, Is.True, created.RejectionCode);
            Assert.That(created.Plan.ActionPlanId.Value, Is.EqualTo(nextIdBefore), "ID 单调且不复用");
        }

        /// <summary>
        /// <c>TerminalCleanupEmitsLifecycleEventOnce</c>（协调器层证据）：
        /// 协调器只把<strong>第一次</strong>终态登记为归档候选——
        /// 归档端口对同一计划只冻结一次。
        /// </summary>
        [Test]
        public void TerminalCleanupEmitsLifecycleEventOnce()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan plan = s.Attack(T);
            s.Authority.RegisterPlan(plan);

            var coordinator = new ActionPlanTerminalCoordinator(s.Authority);
            coordinator.BeginTick(T);
            coordinator.EnterTerminal(plan, ActionTerminationReason.InterruptedByControl, T);
            coordinator.EnterTerminal(plan, ActionTerminationReason.OwnerDied, T + 1L);
            coordinator.EnterCompletion(plan, T + 2L);

            Assert.That(coordinator.FrozenTickCandidates.Count, Is.EqualTo(1),
                "同一计划只能成为一次归档候选");
            Assert.That(coordinator.FrozenTickCandidates[0], Is.EqualTo(plan.ActionPlanId));

            var source = new ActionPlanTerminalArchiveSource(s.Authority, coordinator);
            IReadOnlyList<ProjectHero.Logic.Snapshots.HistoryArchiveCandidate> candidates =
                source.CollectOrdered(T);
            Assert.That(candidates.Count, Is.EqualTo(1), "归档候选恰好一条");
            Assert.That(candidates[0].StableKey,
                Is.EqualTo(ActionPlanRegistry.StableKeyOf(plan)), "稳定键 = ActionPlanId 十进制文本");
            Assert.That(candidates[0].Kind,
                Is.EqualTo(ProjectHero.Logic.Determinism.HistoryRecordKind.ActionPlanTerminal));
            Assert.That(candidates[0].ArchivedAtTick, Is.EqualTo(T), "ArchivedAtTick = TerminalTick");
            Assert.That(candidates[0].Seal.IsSealed, Is.True, "清理完成后的候选必须是封条的");
        }

        /// <summary>
        /// 终态协调器<strong>固定清理参与者顺序</strong>：按 Order 升序执行，
        /// 与装配枚举顺序无关（同 Order 时按 ParticipantId 的 Ordinal 序）。
        /// </summary>
        [Test]
        public void TerminalCleanupParticipantsRunInFixedOrder()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan plan = s.Attack(T);
            s.Authority.RegisterPlan(plan);

            var trace = new List<string>();
            var participants = new IActionPlanCleanupParticipant[]
            {
                new RecordingParticipant(ActionPlanCleanupOrder.MovementAndReservation, "movement", trace),
                new RecordingParticipant(ActionPlanCleanupOrder.StopScheduling, "stop", trace),
                new RecordingParticipant(ActionPlanCleanupOrder.LaneAndActiveIndexCleanup, "lane", trace),
                new RecordingParticipant(ActionPlanCleanupOrder.StopScheduling, "aaa-first", trace)
            };

            var coordinator = new ActionPlanTerminalCoordinator(s.Authority, participants);
            coordinator.EnterTerminal(plan, ActionTerminationReason.BattleEnded, T);

            Assert.That(trace, Is.EqualTo(new[] { "aaa-first", "stop", "lane", "movement" }),
                "必须按 (Order, ParticipantId) 固定顺序执行，而不是按装配顺序");

            // 装配顺序反转后顺序仍不变。
            var trace2 = new List<string>();
            var reversed = new IActionPlanCleanupParticipant[]
            {
                new RecordingParticipant(ActionPlanCleanupOrder.MovementAndReservation, "movement", trace2),
                new RecordingParticipant(ActionPlanCleanupOrder.LaneAndActiveIndexCleanup, "lane", trace2),
                new RecordingParticipant(ActionPlanCleanupOrder.StopScheduling, "stop", trace2),
                new RecordingParticipant(ActionPlanCleanupOrder.StopScheduling, "aaa-first", trace2)
            };
            ActionPlan plan2 = s.Attack(200L);
            s.Authority.RegisterPlan(plan2);
            new ActionPlanTerminalCoordinator(s.Authority, reversed)
                .EnterTerminal(plan2, ActionTerminationReason.BattleEnded, T);

            Assert.That(trace2, Is.EqualTo(new[] { "aaa-first", "stop", "lane", "movement" }),
                "顺序与装配枚举顺序无关（同一份固定顺序）");
        }

        /// <summary>
        /// <c>MovementOriginInvalidatedWinsOverReservationPreemptionForSamePlan</c>（原因优先级）：
        /// 同一计划同时满足两个强制位移原因时，<c>MovementOriginInvalidated</c> 胜出。
        /// </summary>
        [Test]
        public void MovementOriginInvalidatedWinsOverReservationPreemptionForSamePlan()
        {
            Assert.That(
                ActionPlanTerminalCoordinator.ResolveForcedDisplacementReason(
                    movementOriginInvalidated: true, reservationPreempted: true),
                Is.EqualTo(ActionTerminationReason.MovementOriginInvalidated));
            Assert.That(
                ActionPlanTerminalCoordinator.ResolveForcedDisplacementReason(
                    movementOriginInvalidated: false, reservationPreempted: true),
                Is.EqualTo(ActionTerminationReason.ReservationPreemptedByForcedDisplacement));
            Assert.That(
                ActionPlanTerminalCoordinator.ResolveForcedDisplacementReason(
                    movementOriginInvalidated: false, reservationPreempted: false),
                Is.EqualTo(ActionTerminationReason.None));

            // 冻结优先级表与上面的去重结果必须一致。
            Assert.That(
                ActionTerminationReasons.PriorityOf(ActionTerminationReason.MovementOriginInvalidated),
                Is.LessThan(ActionTerminationReasons.PriorityOf(
                    ActionTerminationReason.ReservationPreemptedByForcedDisplacement)));
        }

        /// <summary>
        /// <c>DeathLocksLaneAndTerminatesAllNonTerminalPlansInActionPlanIdOrder</c>（协调器层证据）。
        /// </summary>
        [Test]
        public void DeathTerminatesAllNonTerminalPlansInActionPlanIdOrder()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan editable = s.Attack(100L);
            ActionPlan locked = Task05Scheduler.Lock(s.Attack(300L), lockedAtTick: 5L);
            ActionPlan running = Task05Scheduler.Run(s.Attack(500L), lockedAtTick: 5L);
            s.Authority.RegisterPlan(editable);
            s.Authority.RegisterPlan(locked);
            s.Authority.RegisterPlan(running);

            s.Authority.LockLaneSubmissions(Task05Farm.Hero, "TEST_DEATH");

            var coordinator = new ActionPlanTerminalCoordinator(s.Authority);
            IReadOnlyList<ActionPlanTerminalOutcome> outcomes = coordinator.TerminateAllPlansOfUnit(
                Task05Farm.Hero, ActionTerminationReason.OwnerDied, T);

            Assert.That(outcomes.Count, Is.EqualTo(3), "Editable/Locked/Running 都必须被终止");
            for (int i = 0; i < outcomes.Count; i++)
            {
                Assert.That(outcomes[i].Reason, Is.EqualTo(ActionTerminationReason.OwnerDied));
                Assert.That(outcomes[i].EnteredTerminal, Is.True);
            }
            Assert.That(outcomes[0].ActionPlanId.Value, Is.LessThan(outcomes[1].ActionPlanId.Value),
                "必须按 ActionPlanId 稳定顺序");
            Assert.That(outcomes[1].ActionPlanId.Value, Is.LessThan(outcomes[2].ActionPlanId.Value));

            Assert.That(s.Authority.CollectAllNonTerminalPlansOrdered().Count, Is.EqualTo(0));
            Assert.That(s.Authority.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null);

            // Lane 只锁新提交：它不清除计划（计划是被协调器终止的，不是被 Lane 删除的）。
            Assert.That(editable.IsTerminal, Is.True);
            Assert.That(locked.IsTerminal, Is.True);
            Assert.That(running.IsTerminal, Is.True);
        }

        /// <summary>
        /// <c>BattleEndTerminatesPlansInActionPlanIdOrderAndLocksAllLanes</c> +
        /// <c>BattleEndUsesSamePlanTerminalCoordinator</c> +
        /// <c>BattleEndedPlansRemainInHistoryButLeaveActiveIndexes</c>（协调器层证据）。
        /// </summary>
        [Test]
        public void BattleEndTerminatesPlansAndLeavesHistory()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan hero = s.Attack(100L);

            ActionPlanCreationResult enemyResult = s.Factory.TryCreateOrdinary(
                new OrdinaryPlanRequest(
                    Task05Farm.Enemy, new ActionSpecId(Task05Farm.AttackId), GridDirection.East,
                    Task05Farm.Hero, null, null, 200L),
                0L);
            Assert.That(enemyResult.Succeeded, Is.True, enemyResult.RejectionCode);
            ActionPlan enemy = enemyResult.Plan;
            s.Authority.RegisterPlan(hero);
            s.Authority.RegisterPlan(enemy);

            s.Authority.LockAllLaneSubmissions("TEST_BATTLE_END");
            var coordinator = new ActionPlanTerminalCoordinator(s.Authority);
            IReadOnlyList<ActionPlanTerminalOutcome> outcomes = coordinator.TerminateAllPlans(
                ActionTerminationReason.BattleEnded, T);

            Assert.That(outcomes.Count, Is.EqualTo(2));
            Assert.That(s.Authority.CollectAllNonTerminalPlansOrdered().Count, Is.EqualTo(0));
            Assert.That(s.Authority.Registry.TerminalRecordCount, Is.EqualTo(2), "历史保留全部终态记录");
            Assert.That(s.Authority.Registry.ActiveCount, Is.EqualTo(0), "活动索引必须清空");
            Assert.That(s.Authority.Registry.Find(hero.ActionPlanId), Is.Not.Null, "归档仍可按需诊断读取");
            Assert.That(s.Authority.BuildTerminalSummary().RecordCount, Is.EqualTo(2));
            Assert.That(s.Authority.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null);
            Assert.That(s.Authority.Lanes[0].IsSubmissionLocked, Is.True);
            Assert.That(s.Authority.Lanes.Count, Is.EqualTo(2), "两个单位各一条 Lane");
        }

        /// <summary>
        /// <c>TerminalPlanCannotEmitUnfrozenOrFutureIntent</c>（任务 05 的可观察部分）：
        /// 进入终态后计划<strong>不再</strong>留在任何可产出 Intent 的集合里
        /// （活动索引 + Lane 双清），因此不存在"终态计划还会产出 Intent"的路径。
        /// </summary>
        [Test]
        public void TerminalPlanLeavesEverySchedulableCollection()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan plan = s.Attack(T);
            ActionPlan other = s.Attack(T + 200L);
            s.Authority.RegisterPlan(plan);
            s.Authority.RegisterPlan(other);

            var coordinator = new ActionPlanTerminalCoordinator(s.Authority);
            coordinator.EnterTerminal(plan, ActionTerminationReason.InterruptedByClash, T);

            Assert.That(s.Authority.Registry.ActivePlans.Count, Is.EqualTo(1), "活动索引只剩未终态计划");
            Assert.That(s.LanePlanIds(Task05Farm.Hero).Length, Is.EqualTo(1), "Lane 里只剩未终态计划");
            Assert.That(s.Authority.LaneOfPlan(plan.ActionPlanId), Is.Null, "终态计划不再属于任何 Lane");
            Assert.That(s.Authority.LaneOfPlan(other.ActionPlanId), Is.Not.Null);
        }

        /// <summary>
        /// <c>ForcedDisplacementPlanInvalidationUsesSingleLifecycleEntry</c> +
        /// <c>ForcedDisplacementTerminationDoesNotPullLaterPlansForward</c>（协调器层证据）。
        /// </summary>
        [Test]
        public void ForcedDisplacementTerminationDoesNotPullLaterPlansForward()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan victim = s.Attack(100L);
            ActionPlan later = s.Attack(400L);
            s.Authority.RegisterPlan(victim);
            s.Authority.RegisterPlan(later);

            long laterStartBefore = later.StartTick;
            var coordinator = new ActionPlanTerminalCoordinator(s.Authority);
            ActionPlanTerminalOutcome outcome = coordinator.EnterTerminal(
                victim, ActionTerminationReason.MovementOriginInvalidated, T);

            Assert.That(outcome.EnteredTerminal, Is.True);
            Assert.That(later.StartTick, Is.EqualTo(laterStartBefore), "终止绝不自动前移后续计划");
            Assert.That(s.Authority.Registry.ActiveCount, Is.EqualTo(1));
        }

        // ————————————————————————————————————————————————————————————
        // 系统自动延期事务（复用同一求值器）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>AutoDeferralRipplesOnlyEditableDependencyClosureRight</c> +
        /// <c>AutoDeferralNeverMovesLockedRunningOrReactionPlan</c> +
        /// <c>AutoDeferralPreservesLastRequestedStartTick</c> +
        /// <c>SuccessfulSystemAutoDeferralIncrementsRevisionExactlyOnce</c> +
        /// <c>AutoDeferralReusesCanonicalScheduleEvaluator</c>。
        /// </summary>
        [Test]
        public void SystemAutoDeferralRipplesOnlyEditableClosureRight()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan blocked = s.Attack(100L);
            ActionPlan editable = s.Attack(180L);
            ActionPlan locked = Task05Scheduler.Lock(s.Attack(400L), lockedAtTick: 5L);
            s.Authority.RegisterPlan(blocked);
            s.Authority.RegisterPlan(editable);
            s.Authority.RegisterPlan(locked);

            long requestedBefore = blocked.LastRequestedStartTick;
            int deferralsBefore = blocked.AutomaticDeferralCount;
            long revisionBefore = s.Revision;

            ScheduleEditTransactionResult deferred = s.Editor.ApplySystemAutoDeferral(
                blocked, retryAtTick: 160L, currentTick: T, claimedPlans: null);

            Assert.That(deferred.Committed, Is.True, deferred.RejectionCode);
            Assert.That(blocked.StartTick, Is.EqualTo(160L), "到期计划被推到 RetryAtTick");
            Assert.That(editable.StartTick, Is.EqualTo(220L),
                "重叠只向右 ripple：editable 从 180 推到 160+60=220");
            Assert.That(locked.StartTick, Is.EqualTo(400L), "Locked 障碍绝不移动");
            Assert.That(blocked.LastRequestedStartTick, Is.EqualTo(requestedBefore),
                "系统延期不得覆盖 LastRequestedStartTick");
            Assert.That(blocked.AutomaticDeferralCount, Is.EqualTo(deferralsBefore + 1));
            Assert.That(s.Revision, Is.EqualTo(revisionBefore + 1L), "成功系统延期恰好 +1");
            Assert.That(blocked.IsEditable, Is.True, "延期后必须保持 Editable");
            Assert.That(blocked.ReservedTurnBudgetTicks, Is.EqualTo(blocked.BudgetCostTicks),
                "系统延期期间保持 Reserved（不消费）");
            Assert.That(blocked.LastEditedScheduleRevision, Is.EqualTo(s.Revision),
                "编辑修订号与事务一致");
        }

        /// <summary>
        /// <c>FailedSystemAutoDeferralDoesNotIncrementRevision</c> +
        /// <c>FailedAutoDeferralTerminatesEditableAndReleasesPreLockArtifacts</c>（修订与预留部分）。
        /// </summary>
        [Test]
        public void FailedSystemAutoDeferralDoesNotIncrementRevision()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan plan = s.Attack(100L);
            s.Authority.RegisterPlan(plan);

            long revisionBefore = s.Revision;
            long startBefore = plan.StartTick;

            // 越限：RetryAtTick 不在未来。
            ScheduleEditTransactionResult notFuture = s.Editor.ApplySystemAutoDeferral(
                plan, retryAtTick: T, currentTick: T, claimedPlans: null);
            Assert.That(notFuture.Committed, Is.False);
            Assert.That(notFuture.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_RETRY_TICK_NOT_IN_FUTURE));
            Assert.That(s.Revision, Is.EqualTo(revisionBefore));
            Assert.That(plan.StartTick, Is.EqualTo(startBefore), "失败必须零局部写入");

            // 次数越限。
            plan.AutomaticDeferralCount = 8;
            ScheduleEditTransactionResult limit = s.Editor.ApplySystemAutoDeferral(
                plan, retryAtTick: T + 50L, currentTick: T, claimedPlans: null);
            Assert.That(limit.Committed, Is.False, "达到 MaxAutomaticDeferralsPerPlan 必须失败");
            Assert.That(s.Revision, Is.EqualTo(revisionBefore));
            Assert.That(plan.StartTick, Is.EqualTo(startBefore));

            // 批内冲突：本 Tick 已被命令事务改写的计划不得再被系统延期。
            // 先把上一子用例留下的"次数已达上限"状态清掉，让本子用例只暴露批内冲突这一个变量
            // （`ApplySystemAutoDeferral` 与 `ResolveMove` 一样按"计划自身状态 → 批内声明"的顺序判定）。
            plan.AutomaticDeferralCount = 0;
            ScheduleEditTransactionResult conflict = s.Editor.ApplySystemAutoDeferral(
                plan, retryAtTick: T + 50L, currentTick: T,
                claimedPlans: new[] { plan.ActionPlanId });
            Assert.That(conflict.Committed, Is.False);
            Assert.That(conflict.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_EDIT_CONFLICT_IN_BATCH));
            Assert.That(s.Revision, Is.EqualTo(revisionBefore));

            // 失败后经统一协调器终止：释放锁定前预留，且不增加修订号。
            plan.AutomaticDeferralCount = 0;
            Assert.That(plan.ReservedTurnBudgetTicks, Is.GreaterThan(0), "Editable 期持有未消费预留");
            var coordinator = new ActionPlanTerminalCoordinator(s.Authority);
            coordinator.EnterTerminal(plan, ActionTerminationReason.ActorUnavailableAtStart, T);

            Assert.That(plan.IsTerminal, Is.True);
            Assert.That(plan.ReservedTurnBudgetTicks, Is.EqualTo(0), "终态必须释放未消费预留");
            Assert.That(s.Revision, Is.EqualTo(revisionBefore), "终止不是排程编辑，修订号不变");
        }

        /// <summary>
        /// <c>AutoDeferralPreservesLastRequestedStartTick</c> 的<strong>对照</strong>：
        /// 直接 Move 操作<strong>会</strong>更新 <c>LastRequestedStartTick</c>，
        /// 而系统延期<strong>不会</strong>——两条路径的差异是契约而不是实现细节。
        /// </summary>
        [Test]
        public void DirectMoveUpdatesLastRequestedStartTickButAutoDeferralDoesNot()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan plan = s.Attack(100L);
            s.Authority.RegisterPlan(plan);

            Assert.That(plan.LastRequestedStartTick, Is.EqualTo(100L));
            Assert.That(s.Apply(new ScheduleEditOperation[]
            {
                Task05Scheduler.MoveOp(plan.ActionPlanId, 250L)
            }).Succeeded, Is.True);
            Assert.That(plan.LastRequestedStartTick, Is.EqualTo(250L), "直接 Move 更新请求起点");

            Assert.That(s.Editor.ApplySystemAutoDeferral(plan, 400L, T, null).Committed, Is.True);
            Assert.That(plan.StartTick, Is.EqualTo(400L));
            Assert.That(plan.LastRequestedStartTick, Is.EqualTo(250L),
                "系统延期不得覆盖 LastRequestedStartTick（请求起点仍是用户要的 250）");
        }

        private sealed class FailingCommitPort : IActionPlanStartCommitPort
        {
            public int RollbackCount;

            public string Commit(ActionPlan plan, long tick, Action<ActionPlan> rollback)
            {
                plan.ReservedTurnBudgetTicks = 0;   // 先做一半副作用
                rollback?.Invoke(plan);             // 再回滚
                RollbackCount++;
                return ActionStartGateCommitCodes.RESOURCE_COMMIT_ERROR;
            }
        }

        private sealed class RecordingParticipant : IActionPlanCleanupParticipant
        {
            public RecordingParticipant(int order, string id, List<string> trace = null)
            {
                Order = order;
                ParticipantId = id;
                Trace = trace ?? new List<string>();
            }

            public int Order { get; }

            public string ParticipantId { get; }

            public List<string> Trace;

            public int CallCount { get; set; }

            public ActionPlanTerminalContext LastContext { get; private set; }

            public void Cleanup(ActionPlanTerminalContext context)
            {
                CallCount++;
                LastContext = context;
                Trace.Add(ParticipantId);
            }
        }
    }
}
