using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 09 · A2 轮：<strong>Editable 普通计划的删除语义</strong>
    /// （任务包「必须产出」14 与「核心命令语义」末段）。
    ///
    /// 逐条对应必需测试：<c>RemoveEditablePlanUsesCommandAndUnifiedTerminalCoordinator</c>、
    /// <c>RemoveEditablePlanDoesNotPullLaterPlansLeft</c>、
    /// <c>UiCannotRemoveLockedRunningOrReactionPlan</c>。
    /// </summary>
    public class Task09RemovalSemanticsTests
    {
        private static ActionSpecId Attack() => Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId);

        private static CommandRequest Edit(
            long targetTick, long revision, WindowId? window, params ScheduleEditOperation[] operations)
            => new CommandRequest(targetTick, new ScheduleEditScope(revision, window),
                new ScheduleEditPayload(operations));

        private static WindowId OpenHeroWindow(BattleSimulation sim, int budget = Task09A2Fixture.WindowBudget)
        {
            sim.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Hero, budget);
            Task09A2Fixture.Step(sim, 0L);
            Assert.That(sim.CurrentTurnWindow, Is.Not.Null, "夹具前提：Tick 0 必须打开窗口");
            return sim.CurrentTurnWindow.WindowId;
        }

        // =====================================================================
        // 必需测试 48：RemoveEditablePlanUsesCommandAndUnifiedTerminalCoordinator
        // =====================================================================

        /// <summary>
        /// 删除 Editable 普通计划编码为 <c>RemoveEditablePlan</c>：经同一入口、同一修订号、
        /// 同一原子事务，并<strong>只</strong>调用任务 05 的统一终态协调器收口
        /// （终态 + 终止原因 + 唯一一条终止事件 + 离开 Lane + 释放未消费预留）。
        /// 不存在能绕过命令、<c>ScheduleEditor</c> 与终态协调器的删除写入路径。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：直接改 <c>ActionPlan.State</c> / 直接删 Lane 项
        /// （计划不进终态、或没有终止事件）；删除不经修订号校验与原子事务
        /// （失败时不回滚）；释放额落到"当前窗口"而不是计划自己的 <c>SubmittedWindowId</c>。
        /// </para>
        /// </summary>
        [Test]
        public void RemoveEditablePlanUsesCommandAndUnifiedTerminalCoordinator()
        {
            var sim = Task09A2Fixture.NewSim();
            WindowId window = OpenHeroWindow(sim);

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, window, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, 80L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1L)), Is.Empty);

            ActionPlan plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(plan.IsEditable, Is.True, "夹具前提：计划仍可编辑");
            int cost = plan.BudgetCostTicks;
            int reservedBefore = sim.WindowManager.FindWindow(window).ReservedFor(plan.ActionPlanId);
            Assert.That(reservedBefore, Is.EqualTo(cost), "夹具前提：计划持有真实预留");
            Assert.That(plan.TerminationReason, Is.EqualTo(ActionTerminationReason.None));

            long revision = sim.ScheduleAuthority.ScheduleRevision;
            long sequenceBefore = sim.CurrentSnapshot.NextCommandSequence;

            // —— 被测：经命令入口删除 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Edit(
                2L, revision, null, new RemoveEditablePlanOperation(plan.ActionPlanId)));
            StepResult result = Task09A2Fixture.Step(sim, 2L);

            Assert.That(Task09A2Fixture.AllRejectionCodes(result), Is.Empty,
                "删除必须经命令入口被接受");
            Assert.That(sim.LastCommandSet.Envelopes.Select(e => e.CommandSequence).ToArray(),
                Is.EqualTo(new[] { sequenceBefore }),
                "删除走的是同一条规范命令路径（获得 CommandSequence）");
            Assert.That(sim.CommandProcessor.CommittedTransactionCount, Is.EqualTo(1),
                "本 Tick 恰好一次成功提交，且它由同一个 BattleCommandProcessor 计数（BeginTick 每 Tick 复位）");

            // 统一终态协调器的四条可观察后果。
            Assert.That(plan.IsTerminal, Is.True,
                "删除必须经唯一终态协调器收口（IsTerminal 仍为 false = 没有生效）");
            Assert.That(plan.TerminationReason, Is.EqualTo(ActionTerminationReason.CancelledByCommand),
                "终止原因必须由 RemoveEditablePlanOperation 的载荷决定");
            Assert.That(sim.ScheduleAuthority.LaneOfPlan(plan.ActionPlanId), Is.Null,
                "计划必须离开 Lane");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.Zero);

            List<ActionPlanTerminatedEvent> terminated =
                Task09A2Fixture.EventsOf<ActionPlanTerminatedEvent>(result);
            Assert.That(terminated.Count, Is.EqualTo(1), "恰好一条终止事件");
            Assert.That(terminated[0].ActionPlanId, Is.EqualTo(plan.ActionPlanId.Value));
            Assert.That(terminated[0].Reason, Is.EqualTo((int)ActionTerminationReason.CancelledByCommand));
            Assert.That(terminated[0].ReasonCode, Is.EqualTo(ActionTerminationReasons.CancelledByCommand));

            // 未消费预留在锁定前释放，且回到计划自己的窗口账本。
            TurnWindow owner = sim.WindowManager.FindWindow(window);
            Assert.That(owner.ReservedFor(plan.ActionPlanId), Is.Zero);
            Assert.That(owner.ReservedBudgetTicks, Is.Zero);
            Assert.That(owner.AvailableBudgetTicks, Is.EqualTo(Task09A2Fixture.WindowBudget),
                "释放额必须回到原窗口的 Available");
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revision + 1L),
                "删除是一个成功的排程事务：修订号恰好 +1");

            // 快照面同步：被删除的计划离开活动投影（终态计划经唯一的归档来源离开活动集，
            // 绝不以"半结算"的形态留在 Plans 里）。
            Assert.That(sim.CurrentSnapshot.Plans.Any(p => p.ActionPlanId == plan.ActionPlanId.Value),
                Is.False, "被删除的计划必须离开活动计划投影");
        }

        // =====================================================================
        // 必需测试 49：RemoveEditablePlanDoesNotPullLaterPlansLeft
        // =====================================================================

        /// <summary>
        /// 删除计划<strong>不左吸</strong>：同 Lane 之后的计划保持自己的绝对 Tick
        /// （起点、终点、预算、LastRequestedStartTick 全部不变），绝不自动前移。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把删除实现成"紧凑化/重排 Lane"；删除后触发一次
        /// 隐式 ripple 把后续计划左移；或者删除把后续计划的预留也一并释放。
        /// </para>
        /// </summary>
        [Test]
        public void RemoveEditablePlanDoesNotPullLaterPlansLeft()
        {
            var sim = Task09A2Fixture.NewSim();
            WindowId window = OpenHeroWindow(sim);

            // 同一个 Lane 上两个互不重叠的计划：前者在 40，后者在 100。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, window, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, 40L, temporaryKey: 1L));
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, window, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, 100L, temporaryKey: 2L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1L)), Is.Empty);

            IReadOnlyList<ActionPlan> plans = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(plans.Count, Is.EqualTo(2));
            ActionPlan first = plans.OrderBy(p => p.StartTick).First();
            ActionPlan later = plans.OrderBy(p => p.StartTick).Last();
            Assert.That(first.StartTick, Is.EqualTo(40L));
            Assert.That(later.StartTick, Is.EqualTo(100L));
            Assert.That(later.StartTick, Is.GreaterThan(first.EndTick),
                "夹具前提：两个计划互不重叠");

            long laterStart = later.StartTick;
            long laterEnd = later.EndTick;
            long laterImpact = later.ImpactTick;
            int laterCost = later.BudgetCostTicks;
            int laterReserved = later.ReservedTurnBudgetTicks;
            long laterLastRequested = later.LastRequestedStartTick;
            int laterWindowReservation = sim.WindowManager.FindWindow(window).ReservedFor(later.ActionPlanId);
            long revision = sim.ScheduleAuthority.ScheduleRevision;

            // —— 被测：删除**前一个**计划 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Edit(
                2L, revision, null, new RemoveEditablePlanOperation(first.ActionPlanId)));
            StepResult result = Task09A2Fixture.Step(sim, 2L);

            Assert.That(Task09A2Fixture.AllRejectionCodes(result), Is.Empty);
            Assert.That(first.IsTerminal, Is.True);

            // —— 核心断言：后续计划一位不动 ——
            Assert.That(later.StartTick, Is.EqualTo(laterStart), "后续计划绝不左吸");
            Assert.That(later.EndTick, Is.EqualTo(laterEnd));
            Assert.That(later.ImpactTick, Is.EqualTo(laterImpact));
            Assert.That(later.BudgetCostTicks, Is.EqualTo(laterCost));
            Assert.That(later.ReservedTurnBudgetTicks, Is.EqualTo(laterReserved));
            Assert.That(later.LastRequestedStartTick, Is.EqualTo(laterLastRequested));
            Assert.That(later.IsEditable, Is.True, "后续计划仍可编辑");
            Assert.That(later.IsTerminal, Is.False);
            Assert.That(sim.ScheduleAuthority.LaneOfPlan(later.ActionPlanId), Is.Not.Null);

            TurnWindow owner = sim.WindowManager.FindWindow(window);
            Assert.That(owner.ReservedFor(later.ActionPlanId), Is.EqualTo(laterWindowReservation),
                "后续计划的预留不得被前一个计划的删除波及");
            Assert.That(owner.ReservedBudgetTicks, Is.EqualTo(laterCost),
                "窗口只剩后续计划的预留");
            Assert.That(owner.AvailableBudgetTicks, Is.EqualTo(Task09A2Fixture.WindowBudget - laterCost));

            // 后续计划到期仍能正常启动（左吸会把它提前，从而错过或提前触发门禁）。
            Task09A2Fixture.AdvanceTo(sim, laterStart);
            Assert.That(later.State, Is.EqualTo(ActionPlanState.Running),
                "后续计划在自己的原定 Tick 上正常启动");
            Assert.That(later.LockedAtTick, Is.EqualTo(laterStart));
        }

        // =====================================================================
        // 必需测试 50：UiCannotRemoveLockedRunningOrReactionPlan
        // =====================================================================

        /// <summary>
        /// Locked/Running 的普通计划与反应计划<strong>都不能</strong>用 <c>RemoveEditablePlan</c> 删除，
        /// 与发行者是谁无关；被拒时零局部写入（计划不退场、预留不释放、修订号不变）。
        /// 只读快照为 UI 提供判定所需的 <c>State</c>/<c>Origin</c>，
        /// 但 UI 的预判<strong>不是</strong>权威——权威拒绝仍在 Logic 这一侧。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：删除只查"计划在不在 Lane"而不查 Editable；
        /// 反应计划被当成普通计划（它没有 Editable 阶段）；
        /// UI/AI 的来源不同导致拒绝码不同。
        /// </para>
        /// </summary>
        [Test]
        public void UiCannotRemoveLockedRunningOrReactionPlan()
        {
            var sim = Task09A2Fixture.NewSim();
            WindowId window = OpenHeroWindow(sim);

            // 1. 普通攻击在阶段 7 启动 ⇒ Locked/Running。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, window, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, 4L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1L)), Is.Empty);
            Task09A2Fixture.AdvanceTo(sim, 4L);
            ActionPlan running = sim.ScheduleAuthority.Registry.ActivePlans
                .Single(p => p.OwnerUnitId == Task09A2Fixture.Hero);
            Assert.That(running.State, Is.EqualTo(ActionPlanState.Running));
            Assert.That(running.IsEditable, Is.False);

            // 2. 真实反应计划：接受后直接 Locked。
            Task09A2Fixture.SeedAdrenaline(sim, Task09A2Fixture.Monster,
                FrozenDesignValues.BlockAdrenalineCost);
            ReactionOpportunityRuntime opportunity =
                Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.Reaction(
                5L, opportunity.Id, ReactionCommandKind.Block,
                Task09A2Fixture.Spec(Task09A2Fixture.BlockSpecId)));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 5L)), Is.Empty);
            ActionPlan reaction = sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.IsReaction);
            Assert.That(reaction.IsReaction, Is.True);
            Assert.That(reaction.IsEditable, Is.False);

            // 只读快照为 UI 提供判定所需的 State / Origin（UI 的预判不是权威，但必须有据可依）。
            ActionPlanSnapshot runningSnapshot = sim.CurrentSnapshot.Plans
                .Single(p => p.ActionPlanId == running.ActionPlanId.Value);
            ActionPlanSnapshot reactionSnapshot = sim.CurrentSnapshot.Plans
                .Single(p => p.ActionPlanId == reaction.ActionPlanId.Value);
            Assert.That(runningSnapshot.State, Is.EqualTo((int)ActionPlanState.Running));
            Assert.That(runningSnapshot.Origin, Is.EqualTo((int)ActionPlanOrigin.Ordinary));
            Assert.That(reactionSnapshot.Origin, Is.EqualTo((int)ActionPlanOrigin.Reaction));
            Assert.That(reactionSnapshot.State, Is.EqualTo((int)ActionPlanState.Locked));

            string scheduleBefore = Task09A2Fixture.ScheduleFingerprint(sim);
            long revision = sim.ScheduleAuthority.ScheduleRevision;
            int windowReservedBefore = sim.WindowManager.FindWindow(window).ReservedFor(running.ActionPlanId);

            // —— 被测：玩家删 Running 的普通计划（含"先移动再删除"的组合载荷）——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Edit(
                6L, revision, null, new RemoveEditablePlanOperation(running.ActionPlanId)));
            StepResult removeRunning = Task09A2Fixture.Step(sim, 6L);

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Edit(
                7L, revision, null,
                new MoveEditablePlanOperation(running.ActionPlanId, 300L),
                new RemoveEditablePlanOperation(running.ActionPlanId)));
            StepResult combined = Task09A2Fixture.Step(sim, 7L);

            // —— 被测：AI 删反应计划 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Edit(
                8L, revision, null, new RemoveEditablePlanOperation(reaction.ActionPlanId)));
            StepResult removeReaction = Task09A2Fixture.Step(sim, 8L);

            Assert.That(Task09A2Fixture.RejectionCodes(removeRunning).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED }),
                "Running 的普通计划不可被删除");
            Assert.That(Task09A2Fixture.RejectionCodes(combined).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED }),
                "组合载荷也删不掉 Running 的计划（整批拒绝）");
            Assert.That(Task09A2Fixture.RejectionCodes(removeReaction).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.SCHEDULE_REACTION_CANNOT_BE_EDITED }),
                "反应计划不可被删除");

            Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(scheduleBefore),
                "三条被拒命令必须零局部写入");
            Assert.That(running.IsTerminal, Is.False);
            Assert.That(reaction.IsTerminal, Is.False);
            Assert.That(sim.ScheduleAuthority.LaneOfPlan(running.ActionPlanId), Is.Not.Null);
            Assert.That(sim.ScheduleAuthority.LaneOfPlan(reaction.ActionPlanId), Is.Not.Null);
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revision));
            Assert.That(sim.WindowManager.FindWindow(window).ReservedFor(running.ActionPlanId),
                Is.EqualTo(windowReservedBefore), "被拒的删除不得释放任何预留");
        }
    }
}
