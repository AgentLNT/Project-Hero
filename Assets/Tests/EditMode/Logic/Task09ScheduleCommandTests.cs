using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 09 A 流：<strong>排程编辑的修订号、批内冲突、窗口 scope 与「N+1 编辑先于 N+1 启动门禁」</strong>
    /// （任务包「必须产出」6/7 与「核心命令语义」第一段）。
    ///
    /// 逐条对应必需测试：<c>TickNEditTargetsNPlusOneAndRunsBeforeNPlusOneLock</c>、
    /// <c>ScheduleEditUsesExpectedRevisionAndRejectsStaleWholeBatch</c>、
    /// <c>LaterOverlappingScheduleEditIsRejectedByCanonicalCommandOrder</c>、
    /// <c>AddOrBudgetIncreaseRequiresCurrentWindowScope</c>、
    /// <c>StaleWindowCommandCannotSpendNewWindowBudget</c>。
    ///
    /// <para>
    /// 全部用例都走真实 <c>BattleSimulation</c> + 真实命令入口 + 真实 <c>ScheduleEditor</c> 事务，
    /// 不使用 <c>ScheduleEditor.Apply</c> 的旁路调用，因此"阶段 5 → 阶段 6 → 阶段 7"的顺序
    /// 是被真实管线观察到的，而不是夹具约定。
    /// </para>
    /// </summary>
    public class Task09ScheduleCommandTests
    {
        // ================= 反射接缝（与 Task09CommandIngressTests 同一处、同一理由） =================

        private static readonly MethodInfo InjectOrdinalMethod = typeof(CommandIngressEntry).GetMethod(
            "InjectRecordedFact", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(long), typeof(CommandRequest) }, null);

        private static void InjectRecordedFact(
            CommandIngressEntry entry, long producerOrdinal, CommandRequest request)
        {
            Assert.That(InjectOrdinalMethod, Is.Not.Null);
            Assert.That(InjectOrdinalMethod.IsPublic, Is.False,
                "InjectRecordedFact 不得是 public（生产者可见 API 中没有任何可填写 ProducerOrdinal 的入口）");
            InjectOrdinalMethod.Invoke(entry, new object[] { producerOrdinal, request });
        }

        private static List<T> EventsOf<T>(StepResult result) where T : LogicEvent
            => result.Events.Events.OfType<T>().ToList();

        private static Task09Fixture.ScriptedWindowSchedule WindowAtZero(int budget = Task09Fixture.WindowBudget)
            => new Task09Fixture.ScriptedWindowSchedule()
                .Open(Task09Fixture.WindowOpenTick, Task09Fixture.Hero, budget);

        // =====================================================================
        // 必需测试 4：Tick N 的 N+1 编辑先于 N+1 的启动门禁
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>阶段 6 与阶段 7 被合并（或编辑被推迟到门禁之后）⇒ 计划在 Tick 1 结束时仍是
        /// <c>Editable</c>，状态/锁定 Tick/预算消费三条断言全红；</item>
        /// <item>阶段 5 不冻结批次基线（或冻结晚于阶段 6）⇒ 阶段顺序断言红；</item>
        /// <item>启动提交没有把 <c>Reserved</c> 原子转成 <c>Spent</c> ⇒ 账本断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void TickNEditTargetsNPlusOneAndRunsBeforeNPlusOneLock()
        {
            var sim = Task09Fixture.NewSim(WindowAtZero());
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);

            // Tick 0：打开窗口，排程修订仍为 0。
            StepResult atZero = Task09Fixture.StepNext(sim);
            Assert.That(atZero.Snapshot.ScheduleRevision, Is.EqualTo(0L));
            Assert.That(sim.CurrentTurnWindow, Is.Not.Null);

            // Tick N 的快照产生的 N+1 ScheduleEdit：目标 Tick = 1，起点 = 1。
            Assert.That(player.Submit(Task09Fixture.ScheduleAdd(1L, 1L, 0L, new WindowId(1L))), Is.Null);

            StepResult atOne = Task09Fixture.StepNext(sim);

            // 1. 阶段顺序：冻结批次基线 < 命令校验与排程 < 启动门禁。
            List<StepPhase> phases = sim.LastStepPhaseTrace.Select(e => e.Phase).ToList();
            Assert.That(phases.IndexOf(StepPhase.FrozenBatchMergeAndBaseRevision),
                Is.LessThan(phases.IndexOf(StepPhase.CommandValidationAndScheduling)));
            Assert.That(phases.IndexOf(StepPhase.CommandValidationAndScheduling),
                Is.LessThan(StepPhaseOrderIndex(phases, StepPhase.DuePlanStartGateAndReactionTrigger)),
                "目标 Tick 的排程编辑必须早于同 Tick 的启动门禁");
            Assert.That(StepPhaseOrderIndex(phases, StepPhase.DuePlanStartGateAndReactionTrigger),
                Is.LessThan(StepPhaseOrderIndex(phases, StepPhase.IntentDrain)));

            // 2. 编辑确实在 Tick 1 的冻结批次里被处理。
            Assert.That(sim.BatchBaseScheduleRevision, Is.EqualTo(0L));
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(1));
            Assert.That(sim.LastCommandSet.Envelopes[0].TargetTick, Is.EqualTo(1L));
            Assert.That(EventsOf<CommandRejectedEvent>(atOne).Count, Is.EqualTo(0),
                "夹具前提：本 Tick 的排程编辑必须被接受");

            // 3. 行为证据：同一个 Tick 内编辑先落地、门禁随后把它锁进 Running。
            IReadOnlyList<ActionPlan> plans = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(plans.Count, Is.EqualTo(1));
            ActionPlan plan = plans[0];
            Assert.That(plan.CreatedAtTick, Is.EqualTo(1L), "计划在 Tick 1 的阶段 6 被创建");
            Assert.That(plan.StartTick, Is.EqualTo(1L));
            Assert.That(plan.IsEditable, Is.False, "Tick 1 结束时不得留下 Editable 计划");
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Running));
            Assert.That(plan.LockedAtTick, Is.EqualTo(1L), "锁定发生在 Tick 1 的阶段 7");

            // 4. 启动提交的预算迁移：Reserved -> Spent，且计划不再持有未消费预留。
            TurnWindow window = sim.WindowManager.FindWindow(new WindowId(1L));
            Assert.That(window, Is.Not.Null);
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(plan.BudgetCostTicks),
                "启动门禁必须把该计划的预留原子转成 Spent");
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(0));
            Assert.That(plan.ReservedTurnBudgetTicks, Is.EqualTo(0));
            Assert.That(atOne.Snapshot.ScheduleRevision, Is.EqualTo(1L),
                "每个成功命令事务令全局 ScheduleRevision 恰好 +1");
        }

        private static int StepPhaseOrderIndex(List<StepPhase> phases, StepPhase phase)
        {
            int index = phases.IndexOf(phase);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), "阶段 " + phase + " 必须在本 Tick 执行");
            return index;
        }

        // =====================================================================
        // 必需测试 6：ExpectedScheduleRevision 与 BatchBase 比较，过期整批拒绝
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>把 <c>ExpectedScheduleRevision</c> 与"当前实时修订号"比较（或与上一事务后的值比较）
        /// ⇒ 陈旧命令可能被放行 ⇒ 拒绝码/计划数断言红；</item>
        /// <item>过期命令被"尽量部分合并"（只应用其中一条操作）⇒ 计划数与 ID 断言红；</item>
        /// <item>失败事务消耗 <c>ActionPlanId</c> ⇒ ID 连续性断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void ScheduleEditUsesExpectedRevisionAndRejectsStaleWholeBatch()
        {
            var sim = Task09Fixture.NewSim(WindowAtZero());
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);

            Task09Fixture.StepNext(sim);   // Tick 0：窗口打开，ScheduleRevision = 0

            long batchBase = sim.ScheduleAuthority.ScheduleRevision;
            Assert.That(batchBase, Is.EqualTo(0L));

            // 命令 1（规范顺序在先）：过期修订号 + 两条操作 —— 整批都必须被拒绝。
            var staleOperations = new ScheduleEditOperation[]
            {
                Task09Fixture.AddHeroAttack(101L, 5L),
                Task09Fixture.AddHeroAttack(102L, 40L)
            };
            Assert.That(player.Submit(Task09Fixture.ScheduleEdit(
                1L, batchBase + 5L, new WindowId(1L), staleOperations)), Is.Null);

            // 命令 2（规范顺序在后）：正确修订号 —— 必须成功，且只创建它自己的两条计划。
            var freshOperations = new ScheduleEditOperation[]
            {
                Task09Fixture.AddHeroAttack(201L, 5L),
                Task09Fixture.AddHeroAttack(202L, 40L)
            };
            Assert.That(player.Submit(Task09Fixture.ScheduleEdit(
                1L, batchBase, new WindowId(1L), freshOperations)), Is.Null);

            StepResult result = Task09Fixture.StepNext(sim);

            List<CommandRejectedEvent> rejections = EventsOf<CommandRejectedEvent>(result);
            Assert.That(rejections.Count, Is.EqualTo(1), "只有过期的那一条命令被拒绝");
            Assert.That(rejections[0].ReasonCode, Is.EqualTo(ScheduleCodes.STALE_SCHEDULE_REVISION));
            Assert.That(rejections[0].CommandSequence, Is.EqualTo(1L),
                "过期判定按规范顺序（CommandSequence）报告");

            IReadOnlyList<ActionPlan> plans = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(plans.Count, Is.EqualTo(2), "过期命令的两条操作一条都不得落地");
            Assert.That(plans.Select(p => p.StartTick).ToArray(), Is.EqualTo(new[] { 5L, 40L }));
            Assert.That(sim.BatchBaseScheduleRevision, Is.EqualTo(0L),
                "ExpectedScheduleRevision 与目标 Step 冻结的 BatchBase 比较");
            Assert.That(result.Snapshot.ScheduleRevision, Is.EqualTo(1L),
                "只有成功事务令修订号 +1；失败事务零局部写入");

            // 失败事务不消耗 ActionPlanId：下一个事务拿到的 ID 必须紧接成功事务之后。
            Assert.That(plans.Select(p => p.ActionPlanId.Value).ToArray(), Is.EqualTo(new[] { 1L, 2L }));
            Assert.That(result.Snapshot.NextActionPlanId, Is.EqualTo(3L),
                "被拒绝的过期事务不得消耗任何 ActionPlanId");

            // 预算：只有成功事务的两条计划持有预留。
            TurnWindow window = sim.WindowManager.FindWindow(new WindowId(1L));
            Assert.That(window.ReservedBudgetTicks,
                Is.EqualTo(plans[0].BudgetCostTicks + plans[1].BudgetCostTicks));
        }

        // =====================================================================
        // 必需测试 7：后处理的重复编辑按规范顺序（CommandSequence）被拒绝
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>批内冲突按"原始投递顺序"而不是规范顺序判定 ⇒ 两种原始排列得到不同的计划位点；
        /// </item>
        /// <item>两条编辑都成功（缺少 <c>_batchClaimedPlans</c>）⇒ 计划位点与拒绝事件断言红；</item>
        /// <item>拒绝码被换成别的（例如 <c>STALE_SCHEDULE_REVISION</c>）⇒ 拒绝码断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void LaterOverlappingScheduleEditIsRejectedByCanonicalCommandOrder()
        {
            RunOverlappingEdits(antiCanonicalRawOrder: true, out long firstPosition, out long firstRejectedSequence);
            RunOverlappingEdits(antiCanonicalRawOrder: false, out long secondPosition, out long secondRejectedSequence);

            Assert.That(firstPosition, Is.EqualTo(20L),
                "胜出者必须是规范顺序在前的命令（ProducerOrdinal 2 请求 20），不是原始排列在前的命令");
            Assert.That(secondPosition, Is.EqualTo(firstPosition));
            Assert.That(firstRejectedSequence, Is.EqualTo(3L),
                "后处理事务（规范顺序第 2 条，CommandSequence 3）被拒绝：Tick 0 的 Add 已占用序号 1");
            Assert.That(secondRejectedSequence, Is.EqualTo(firstRejectedSequence));
        }

        private static void RunOverlappingEdits(
            bool antiCanonicalRawOrder, out long resultingStartTick, out long rejectedCommandSequence)
        {
            var sim = Task09Fixture.NewSim(WindowAtZero());
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);

            // Tick 0：建立一个仍是 Editable 的普通计划（起点 10）。
            Task09Fixture.StepNext(sim, Task09Fixture.ScheduleAdd(0L, 10L, 0L, new WindowId(1L)));
            IReadOnlyList<ActionPlan> plans = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(plans.Count, Is.EqualTo(1));
            ActionPlan plan = plans[0];
            Assert.That(plan.IsEditable, Is.True);
            Assert.That(plan.StartTick, Is.EqualTo(10L));

            // Tick 1：两条"已记录事实"携带不同 ProducerOrdinal，且刻意按反规范顺序投递。
            var moveEarly = new CommandRequest(1L, Task09Fixture.ScheduleScope(1L, new WindowId(1L)),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    new MoveEditablePlanOperation(plan.ActionPlanId, 20L)
                }));
            var moveLate = new CommandRequest(1L, Task09Fixture.ScheduleScope(1L, new WindowId(1L)),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    new MoveEditablePlanOperation(plan.ActionPlanId, 30L)
                }));

            if (antiCanonicalRawOrder)
            {
                InjectRecordedFact(player, 5L, moveLate);
                InjectRecordedFact(player, 2L, moveEarly);
            }
            else
            {
                InjectRecordedFact(player, 2L, moveEarly);
                InjectRecordedFact(player, 5L, moveLate);
            }

            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(1L);
            Assert.That(batch.Count, Is.EqualTo(2));

            StepResult result = sim.Step(1L, batch);
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(2),
                "两条编辑都通过入口与网关（批内冲突是处理器级判定）");
            Assert.That(sim.LastCommandSet.Envelopes.Select(e => e.ProducerOrdinal).ToArray(),
                Is.EqualTo(new[] { 2L, 5L }),
                "规范顺序 = SourcePriority -> ControllerId(Ordinal) -> ProducerOrdinal");
            Assert.That(sim.LastCommandSet.Envelopes.Select(e => e.CommandSequence).ToArray(),
                Is.EqualTo(new[] { 2L, 3L }),
                "Tick 0 的 Add 已占用序号 1；两条 Move 依次获得 2、3");

            List<CommandRejectedEvent> rejections = EventsOf<CommandRejectedEvent>(result);
            Assert.That(rejections.Count, Is.EqualTo(1));
            Assert.That(rejections[0].ReasonCode, Is.EqualTo(ScheduleCodes.SCHEDULE_EDIT_CONFLICT_IN_BATCH),
                "触及本批已改写依赖闭包的后处理事务按 CommandSequence 拒绝");
            rejectedCommandSequence = rejections[0].CommandSequence;

            resultingStartTick = plan.StartTick;
            Assert.That(plan.StartTick, Is.EqualTo(20L), "规范顺序在前的命令改写的位点必须保留");
        }

        // =====================================================================
        // 必需测试 8：Add / 增费必须携带当前窗口 scope
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>缺失 <c>ExpectedWindowId</c> 时回退成"当前窗口"⇒ 缺失断言红（命令会成功）；</item>
        /// <item>陈旧 <c>ExpectedWindowId</c> 被接受 ⇒ 拒绝码断言红；</item>
        /// <item>被拒绝的新增仍然预留了预算 ⇒ 账本断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void AddOrBudgetIncreaseRequiresCurrentWindowScope()
        {
            var sim = Task09Fixture.NewSim(WindowAtZero());
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);

            Task09Fixture.StepNext(sim);   // Tick 0：窗口 W1 打开

            WindowId windowId = sim.CurrentTurnWindow.WindowId;
            Assert.That(windowId.Value, Is.EqualTo(1L));

            // 1. 完全缺失窗口 scope：不猜当前窗口，稳定拒绝。
            Assert.That(player.Submit(Task09Fixture.ScheduleEdit(
                1L, 0L, null,
                Task09Fixture.AddHeroAttack(1L, 5L))), Is.Null);
            // 2. 陈旧/未知窗口 scope：稳定拒绝。
            Assert.That(player.Submit(Task09Fixture.ScheduleEdit(
                1L, 0L, new WindowId(9999L),
                Task09Fixture.AddHeroAttack(2L, 5L))), Is.Null);
            // 3. 当前窗口 scope：成功。
            Assert.That(player.Submit(Task09Fixture.ScheduleEdit(
                1L, 0L, windowId,
                Task09Fixture.AddHeroAttack(3L, 5L))), Is.Null);

            StepResult result = Task09Fixture.StepNext(sim);

            List<CommandRejectedEvent> rejections = EventsOf<CommandRejectedEvent>(result);
            Assert.That(rejections.Select(r => r.ReasonCode).ToArray(), Is.EqualTo(new[]
            {
                TurnWindowCodes.BUDGET_SOURCE_CLOSED_OR_MISMATCH,
                TurnWindowCodes.STALE_OR_CLOSED_WINDOW
            }), "缺失窗口 scope 与陈旧窗口 scope 是两个不同的稳定拒绝码");

            IReadOnlyList<ActionPlan> plans = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(plans.Count, Is.EqualTo(1), "只有携带当前窗口 scope 的新增落地");
            TurnWindow window = sim.WindowManager.FindWindow(windowId);
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(plans[0].BudgetCostTicks),
                "被拒绝的新增必须零预算写入");
            Assert.That(window.AvailableBudgetTicks,
                Is.EqualTo(Task09Fixture.WindowBudget - plans[0].BudgetCostTicks));
        }

        // =====================================================================
        // 必需测试 9：旧窗口 scope 不能花新窗口的预算
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>陈旧 scope 被放行 ⇒ 拒绝码断言红；</item>
        /// <item>新增计划把预算记到旧窗口（或记到"当前窗口"而忽略 scope）⇒ 两个账本断言红；</item>
        /// <item>失败事务仍创建了计划 ⇒ 计划数/修订号断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void StaleWindowCommandCannotSpendNewWindowBudget()
        {
            var schedule = new Task09Fixture.ScriptedWindowSchedule()
                .Open(0L, Task09Fixture.Hero, Task09Fixture.WindowBudget)
                .Close(1L)
                .Open(2L, Task09Fixture.Hero, Task09Fixture.WindowBudget);
            var sim = Task09Fixture.NewSim(schedule);
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);

            // Tick 0：在 W1 上建立一个 Editable 计划（持有 W1 的预留）。
            Task09Fixture.StepNext(sim, Task09Fixture.ScheduleAdd(0L, 10L, 0L, new WindowId(1L)));
            IReadOnlyList<ActionPlan> plans = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(plans.Count, Is.EqualTo(1));
            ActionPlan first = plans[0];
            Assert.That(first.SubmittedWindowId, Is.EqualTo(new WindowId(1L)));

            Task09Fixture.StepNext(sim);   // Tick 1：W1 在阶段 17 正式关闭
            Assert.That(sim.CurrentTurnWindow, Is.Null, "夹具前提：Tick 1 结束时 W1 已关闭");

            StepResult atTwo = Task09Fixture.StepNext(sim);   // Tick 2：W2 打开
            Assert.That(sim.CurrentTurnWindow, Is.Not.Null, "夹具前提：Tick 2 打开新窗口");
            WindowId newWindowId = sim.CurrentTurnWindow.WindowId;
            Assert.That(newWindowId.Value, Is.EqualTo(2L));
            Assert.That(atTwo.Snapshot.ScheduleRevision, Is.EqualTo(1L));

            // Tick 3：拿着旧窗口 scope 提交新增 ⇒ 稳定拒绝，绝不花新窗口预算。
            StepResult atThree = Task09Fixture.StepNext(sim, Task09Fixture.ScheduleEdit(
                3L, 1L, new WindowId(1L),
                Task09Fixture.AddHeroAttack(2L, 12L)));

            List<CommandRejectedEvent> rejections = EventsOf<CommandRejectedEvent>(atThree);
            Assert.That(rejections.Count, Is.EqualTo(1));
            Assert.That(rejections[0].ReasonCode, Is.EqualTo(TurnWindowCodes.STALE_OR_CLOSED_WINDOW));

            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.EqualTo(1),
                "被拒绝的事务不得创建计划");
            Assert.That(atThree.Snapshot.ScheduleRevision, Is.EqualTo(1L),
                "被拒绝的事务零局部写入，不推进修订号");

            TurnWindow newWindow = sim.WindowManager.FindWindow(newWindowId);
            Assert.That(newWindow.ReservedBudgetTicks, Is.EqualTo(0),
                "旧窗口 scope 绝不能消费新窗口预算");
            Assert.That(newWindow.AvailableBudgetTicks, Is.EqualTo(Task09Fixture.WindowBudget));

            TurnWindow oldWindow = sim.WindowManager.FindWindow(new WindowId(1L));
            Assert.That(oldWindow.ReservedFor(first.ActionPlanId), Is.EqualTo(first.BudgetCostTicks),
                "原窗口账本必须保持原样");
        }
    }
}
