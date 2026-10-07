using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 09 · A2 轮：<strong>排程编辑的 scope 判别、批内基线、成本中性编辑、
    /// 锁定/反应计划不可编辑、窗口与并发的提交授权、PrimaryTargetOnly 目标语义</strong>
    /// （任务包「必须产出」4/6/7 与「核心命令语义」第一段）。
    ///
    /// 逐条对应必需测试：<c>ScheduleScopePayloadMismatchIsRejected</c>、
    /// <c>ScopePayloadMismatchIsRejected</c>、
    /// <c>PlayerAndAiDisjointScheduleEditsFromSameSnapshotCanBothSucceed</c>、
    /// <c>CostNeutralMoveOrRemoveDoesNotRequireOriginWindowOpen</c>、
    /// <c>ScheduleEditCannotTouchLockedRunningOrReactionPlan</c>、
    /// <c>RunningPlanFromPastWindowDoesNotBlockAiWindow</c>、
    /// <c>ConcurrentActionScheduleEditUsesNormalSubmissionPath</c>、
    /// <c>PrimaryTargetOnlyCommandRequiresStableTargetUnitId</c>、
    /// <c>PrimaryTargetRelationMustBeAllowedByActionSpec</c>、
    /// <c>PrimaryTargetRelationRejectionDoesNotAutoRetarget</c>，
    /// 以及冻结件 §8.4 第 1 条的 R-A1-D1 回归用例与第 2 条的 Lane 尾部专测
    /// （<c>RejectedTransactionRollbackDoesNotCorruptExistingNonMovePlan</c>、
    /// <c>NullRequestedStartTickResolvesToLaneTail</c>）。
    /// </summary>
    public class Task09ScheduleSemanticsTests
    {
        private static ActionSpecId Attack() => Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId);
        private static ActionSpecId AlliedAttack() => Task09A2Fixture.Spec(Task09A2Fixture.AlliedAttackSpecId);

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
        // 必需测试 11：ScheduleScopePayloadMismatchIsRejected
        // =====================================================================

        /// <summary>
        /// <c>ScheduleEditPayload</c> 配上非排程 scope（窗口 / 反应）时必须<strong>精确</strong>拒绝：
        /// 判别不匹配是入口级拒绝（不分配 <c>CommandSequence</c>），零局部写入。
        /// 正对照：同一载荷配上正确的 <c>ScheduleEditScope</c> 立即被接受。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把 scope 判别写成"取当前窗口/当前机会"的隐式回退；
        /// 让不匹配的请求获得序号（那样它就会进入处理器并有机会写入）。
        /// </para>
        /// </summary>
        [Test]
        public void ScheduleScopePayloadMismatchIsRejected()
        {
            var sim = Task09A2Fixture.NewSim();
            WindowId window = OpenHeroWindow(sim);
            long revision = sim.ScheduleAuthority.ScheduleRevision;
            long sequencesBefore = sim.CurrentSnapshot.NextCommandSequence;

            var payload = new ScheduleEditPayload(new ScheduleEditOperation[]
            {
                Task09A2Fixture.AddHeroAttack(1L, 40L, Task09A2Fixture.Hero, Attack(),
                    Task09A2Fixture.Monster)
            });

            // —— 被测 1：排程载荷 + 窗口 scope ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId,
                new CommandRequest(1L, new WindowCommandScope(window), payload));
            StepResult windowScope = Task09A2Fixture.Step(sim, 1L);

            // —— 被测 2：排程载荷 + 反应 scope ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId,
                new CommandRequest(2L, new ReactionCommandScope(new ReactionOpportunityId(7L)), payload));
            StepResult reactionScope = Task09A2Fixture.Step(sim, 2L);

            foreach (StepResult result in new[] { windowScope, reactionScope })
            {
                Assert.That(Task09A2Fixture.IngressRejectionCodes(result).ToArray(),
                    Is.EqualTo(new[] { CommandCodes.SCOPE_PAYLOAD_MISMATCH }),
                    "排程载荷配非排程 scope 必须精确拒绝");
                Assert.That(Task09A2Fixture.RejectionCodes(result), Is.Empty,
                    "判别不匹配不得进入处理器");
            }

            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.Zero);
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revision));
            Assert.That(sim.CurrentSnapshot.NextCommandSequence, Is.EqualTo(sequencesBefore),
                "被入口拒绝对请求不得消耗任何 CommandSequence");

            // —— 正对照：正确的 scope 立即被接受 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId,
                new CommandRequest(3L, new ScheduleEditScope(revision, window), payload));
            StepResult accepted = Task09A2Fixture.Step(sim, 3L);

            Assert.That(Task09A2Fixture.AllRejectionCodes(accepted), Is.Empty,
                "正对照：正确的排程 scope 必须被接受");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.EqualTo(1));
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revision + 1L));
        }

        // =====================================================================
        // 必需测试 16：ScopePayloadMismatchIsRejected
        // =====================================================================

        /// <summary>
        /// 三类 scope 与三类载荷<strong>两两错配</strong>（共 6 种非法组合）全部由
        /// <c>SCOPE_PAYLOAD_MISMATCH</c> 稳定拒绝，且一条都不消耗 <c>CommandSequence</c>。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：任一非法组合被静默接受或落到别的码上；
        /// 用 <c>is</c> 链之外的顺序判定让某些组合漏检。
        /// </para>
        /// </summary>
        [Test]
        public void ScopePayloadMismatchIsRejected()
        {
            var sim = Task09A2Fixture.NewSim();
            WindowId window = OpenHeroWindow(sim);
            long sequencesBefore = sim.CurrentSnapshot.NextCommandSequence;
            long revision = sim.ScheduleAuthority.ScheduleRevision;

            var scheduleScope = new ScheduleEditScope(revision, window);
            var windowScope = new WindowCommandScope(window);
            var reactionScope = new ReactionCommandScope(new ReactionOpportunityId(11L));

            ICommandPayload schedulePayload = new ScheduleEditPayload(new ScheduleEditOperation[]
            {
                Task09A2Fixture.AddHeroAttack(1L, 40L, Task09A2Fixture.Hero, Attack(), Task09A2Fixture.Monster)
            });
            ICommandPayload windowPayload = new WindowCommandPayload(WindowCommandKind.CloseOwnWindow);
            ICommandPayload reactionPayload =
                new ReactionCommandPayload(ReactionCommandKind.Block, Task09A2Fixture.Spec(Task09A2Fixture.BlockSpecId));

            var illegal = new List<CommandRequest>
            {
                // 排程载荷 + 窗口/反应 scope
                new CommandRequest(0L, windowScope, schedulePayload),
                new CommandRequest(0L, reactionScope, schedulePayload),
                // 窗口载荷 + 排程/反应 scope
                new CommandRequest(0L, scheduleScope, windowPayload),
                new CommandRequest(0L, reactionScope, windowPayload),
                // 反应载荷 + 排程/窗口 scope
                new CommandRequest(0L, scheduleScope, reactionPayload),
                new CommandRequest(0L, windowScope, reactionPayload)
            };

            var observed = new List<string[]>();
            for (int i = 0; i < illegal.Count; i++)
            {
                long tick = i + 1L;
                CommandRequest request = illegal[i] with { TargetTick = tick };
                Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, request);
                StepResult result = Task09A2Fixture.Step(sim, tick);
                observed.Add(Task09A2Fixture.IngressRejectionCodes(result).ToArray());
            }

            for (int i = 0; i < observed.Count; i++)
            {
                Assert.That(observed[i], Is.EqualTo(new[] { CommandCodes.SCOPE_PAYLOAD_MISMATCH }),
                    "第 " + i + " 种 scope/payload 错配必须精确拒绝");
            }

            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.Zero);
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revision));
            Assert.That(sim.CurrentSnapshot.NextCommandSequence, Is.EqualTo(sequencesBefore),
                "六种错配一条都不得消耗 CommandSequence");
            Assert.That(sim.ReactionOpportunities.ActiveOpportunities.Count, Is.Zero,
                "反应载荷配错 scope 时绝不临时找一个机会");
        }

        // =====================================================================
        // 必需测试 7：PlayerAndAiDisjointScheduleEditsFromSameSnapshotCanBothSucceed
        // =====================================================================

        /// <summary>
        /// 同一冻结批次内，玩家与 AI 各自编辑<strong>互不相交</strong>的 Lane，且两条命令
        /// 携带<strong>同一个</strong> <c>ExpectedScheduleRevision</c>（= 该 Step 冻结时的
        /// <c>BatchBaseScheduleRevision</c>）：两条都必须成功。
        /// 同批内触及已改写依赖闭包的第三条命令则按 <c>CommandSequence</c> 拒绝。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把 <c>ExpectedScheduleRevision</c> 与本批前一事务递增后的
        /// <strong>实时</strong>修订号比较（第二条会以 <c>STALE_SCHEDULE_REVISION</c> 被误拒）；
        /// 或者批内冲突集合按"Lane"而不是"已改写计划"判定（互不相交的编辑被误判为冲突）。
        /// </para>
        /// </summary>
        [Test]
        public void PlayerAndAiDisjointScheduleEditsFromSameSnapshotCanBothSucceed()
        {
            var sim = Task09A2Fixture.NewSim();

            // —— 准备：mon 的窗口里建立 mon 的计划；hero 的窗口里建立 hero 的计划 ——
            sim.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Monster, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(sim, 0L);
            WindowId aiWindow = sim.CurrentTurnWindow.WindowId;
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, aiWindow, Task09A2Fixture.Monster,
                Attack(), Task09A2Fixture.Hero, 80L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1L)), Is.Empty);

            // mon 关窗 → hero 开窗 → hero 建立自己的计划。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.CloseWindow(2L, aiWindow));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 2L)), Is.Empty);
            sim.WindowManager.ScheduleWindow(3L, Task09A2Fixture.Hero, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(sim, 3L);
            WindowId heroWindow = sim.CurrentTurnWindow.WindowId;
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                4L, sim.ScheduleAuthority.ScheduleRevision, heroWindow, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, 80L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 4L)), Is.Empty);

            ActionPlan heroPlan = sim.ScheduleAuthority.Registry.ActivePlans
                .Single(p => p.OwnerUnitId == Task09A2Fixture.Hero);
            ActionPlan monPlan = sim.ScheduleAuthority.Registry.ActivePlans
                .Single(p => p.OwnerUnitId == Task09A2Fixture.Monster);
            Assert.That(heroPlan.IsEditable, Is.True);
            Assert.That(monPlan.IsEditable, Is.True);

            // —— 被测：同一个冻结批次、同一个 BatchBase 修订号、两条互不相交的 Lane ——
            long batchBase = sim.ScheduleAuthority.ScheduleRevision;
            long sequenceBefore = sim.CurrentSnapshot.NextCommandSequence;

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Edit(
                5L, batchBase, null, new MoveEditablePlanOperation(heroPlan.ActionPlanId, 200L)));
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Edit(
                5L, batchBase, null, new MoveEditablePlanOperation(heroPlan.ActionPlanId, 210L)));
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Edit(
                5L, batchBase, null, new MoveEditablePlanOperation(monPlan.ActionPlanId, 220L)));

            StepResult result = Task09A2Fixture.Step(sim, 5L);

            Assert.That(sim.LastCommandSet.Envelopes.Select(e => e.CommandSequence).ToArray(),
                Is.EqualTo(new[] { sequenceBefore, sequenceBefore + 1L, sequenceBefore + 2L }),
                "夹具前提：本批三条命令按规范顺序获得序号");
            Assert.That(Task09A2Fixture.RejectionCodes(result).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.SCHEDULE_EDIT_CONFLICT_IN_BATCH }),
                "只有同批内触及同一依赖闭包的第三条被拒绝");

            Assert.That(heroPlan.StartTick, Is.EqualTo(200L),
                "玩家对 hero Lane 的编辑必须成功（同一 BatchBase 不比实时修订号）");
            Assert.That(monPlan.StartTick, Is.EqualTo(220L),
                "AI 对 mon Lane 的编辑必须成功（不相交 Lane 依序成功）");
            Assert.That(result.Snapshot.ScheduleRevision, Is.EqualTo(batchBase + 2L),
                "两个成功事务各令修订号 +1；失败事务零局部写入");
            Assert.That(sim.BatchBaseScheduleRevision, Is.EqualTo(batchBase),
                "ExpectedScheduleRevision 与目标 Step 冻结时的 BatchBase 比较");
        }

        // =====================================================================
        // 必需测试 10：CostNeutralMoveOrRemoveDoesNotRequireOriginWindowOpen
        // =====================================================================

        /// <summary>
        /// 纯 Move / Remove 在<strong>成本不增加</strong>时不要求原窗口仍打开：
        /// 窗口关闭后仍可移动、仍可删除（释放额回到它自己的 <c>SubmittedWindowId</c> 账本，
        /// 绝不转移、绝不重开）。对照：Add 缺窗口 scope 时被稳定拒绝。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把"窗口必须开放"当成所有排程编辑的统一前置
        /// （关闭窗口后连删除都做不了）；或者让释放额落到"当前窗口"而不是原窗口。
        /// </para>
        /// </summary>
        [Test]
        public void CostNeutralMoveOrRemoveDoesNotRequireOriginWindowOpen()
        {
            var sim = Task09A2Fixture.NewSim();
            WindowId window = OpenHeroWindow(sim);

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, window, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, 80L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1L)), Is.Empty);

            ActionPlan plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            int cost = plan.BudgetCostTicks;
            Assert.That(sim.WindowManager.FindWindow(window).ReservedFor(plan.ActionPlanId), Is.EqualTo(cost));

            // 关窗。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.CloseWindow(2L, window));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 2L)), Is.Empty);
            TurnWindow closed = sim.WindowManager.FindWindow(window);
            Assert.That(closed.IsOpen, Is.False, "夹具前提：原窗口已关闭");

            // —— 被测 1：纯 Move（成本中性）且 scope 不声明窗口 ⇒ 接受 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Edit(
                3L, sim.ScheduleAuthority.ScheduleRevision, null,
                new MoveEditablePlanOperation(plan.ActionPlanId, 90L)));
            StepResult moved = Task09A2Fixture.Step(sim, 3L);

            Assert.That(Task09A2Fixture.AllRejectionCodes(moved), Is.Empty,
                "成本中性的 Move 不要求原窗口仍打开");
            Assert.That(plan.StartTick, Is.EqualTo(90L));
            Assert.That(plan.BudgetCostTicks, Is.EqualTo(cost), "成本中性：预算一位不变");
            Assert.That(sim.WindowManager.FindWindow(window).ReservedFor(plan.ActionPlanId), Is.EqualTo(cost));

            // —— 被测 2：纯 Remove 且 scope 不声明窗口 ⇒ 接受，释放额回到原窗口账本 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Edit(
                4L, sim.ScheduleAuthority.ScheduleRevision, null,
                new RemoveEditablePlanOperation(plan.ActionPlanId)));
            StepResult removed = Task09A2Fixture.Step(sim, 4L);

            Assert.That(Task09A2Fixture.AllRejectionCodes(removed), Is.Empty,
                "成本中性的 Remove 不要求原窗口仍打开");
            Assert.That(plan.IsTerminal, Is.True, "Remove 必须经统一终态协调器收口");
            Assert.That(sim.WindowManager.FindWindow(window).ReservedFor(plan.ActionPlanId), Is.Zero,
                "释放额回到计划自己的 SubmittedWindowId 账本");
            Assert.That(sim.WindowManager.FindWindow(window).IsOpen, Is.False,
                "释放不得重开已关闭的窗口");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.Zero);

            // —— 对照：窗口**已重新打开**，但命令不声明窗口 scope ⇒ 新增/增费必须被稳定拒绝 ——
            sim.WindowManager.ScheduleWindow(5L, Task09A2Fixture.Hero, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(sim, 5L);
            Assert.That(sim.CurrentTurnWindow, Is.Not.Null, "夹具前提：新窗口已打开");

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                6L, sim.ScheduleAuthority.ScheduleRevision, null, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, 120L));
            StepResult addWithoutWindow = Task09A2Fixture.Step(sim, 6L);

            Assert.That(Task09A2Fixture.RejectionCodes(addWithoutWindow).ToArray(),
                Is.EqualTo(new[] { TurnWindowCodes.BUDGET_SOURCE_CLOSED_OR_MISMATCH }),
                "窗口打开时缺窗口 scope 的新增必须稳定拒绝，绝不回退到当前窗口"
                + "（对照：成本中性的 Move/Remove 根本不需要声明窗口）");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.Zero);
        }

        // =====================================================================
        // 必需测试 12：ScheduleEditCannotTouchLockedRunningOrReactionPlan
        // =====================================================================

        /// <summary>
        /// 排程编辑<strong>只</strong>能触及 Editable 的普通计划：
        /// 已锁定/运行中的普通计划以 <c>SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED</c> 拒绝，
        /// 反应计划以 <c>SCHEDULE_REACTION_CANNOT_BE_EDITED</c> 拒绝，一律零局部写入。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把"是否 Editable"的判定放在 Lane 查找之后或被跳过；
        /// 让反应计划走上普通编辑路径（它没有 Editable 阶段，接受后直接 Locked）。
        /// </para>
        /// </summary>
        [Test]
        public void ScheduleEditCannotTouchLockedRunningOrReactionPlan()
        {
            var sim = Task09A2Fixture.NewSim();
            WindowId window = OpenHeroWindow(sim);

            // 1. 建立一个普通攻击计划并让它启动（开始门禁把它锁进 Running）。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, window, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, 4L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1L)), Is.Empty);
            ActionPlan attack = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Task09A2Fixture.AdvanceTo(sim, 4L);
            Assert.That(attack.IsEditable, Is.False, "夹具前提：攻击已在阶段 7 启动");
            Assert.That(attack.State, Is.EqualTo(ActionPlanState.Running));

            // 2. 建立一条真实反应计划（Block），它接受后直接 Locked。
            Task09A2Fixture.SeedAdrenaline(sim, Task09A2Fixture.Monster,
                FrozenDesignValues.BlockAdrenalineCost);
            ReactionOpportunityRuntime opportunity =
                Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.Reaction(
                5L, opportunity.Id, ReactionCommandKind.Block,
                Task09A2Fixture.Spec(Task09A2Fixture.BlockSpecId)));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 5L)), Is.Empty);
            ActionPlan reaction = sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.IsReaction);
            Assert.That(reaction.IsEditable, Is.False);

            string scheduleBefore = Task09A2Fixture.ScheduleFingerprint(sim);
            long revision = sim.ScheduleAuthority.ScheduleRevision;

            // —— 被测 1：移动/删除 Running 的普通计划 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Edit(
                6L, revision, null, new MoveEditablePlanOperation(attack.ActionPlanId, 300L)));
            StepResult moveRunning = Task09A2Fixture.Step(sim, 6L);

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Edit(
                7L, revision, null, new RemoveEditablePlanOperation(attack.ActionPlanId)));
            StepResult removeRunning = Task09A2Fixture.Step(sim, 7L);

            // —— 被测 2：移动/删除反应计划 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Edit(
                8L, revision, null, new MoveEditablePlanOperation(reaction.ActionPlanId, 300L)));
            StepResult moveReaction = Task09A2Fixture.Step(sim, 8L);

            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Edit(
                9L, revision, null, new RemoveEditablePlanOperation(reaction.ActionPlanId)));
            StepResult removeReaction = Task09A2Fixture.Step(sim, 9L);

            Assert.That(Task09A2Fixture.RejectionCodes(moveRunning).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED }),
                "Running 的普通计划不可被移动");
            Assert.That(Task09A2Fixture.RejectionCodes(removeRunning).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED }),
                "Running 的普通计划不可被删除");
            Assert.That(Task09A2Fixture.RejectionCodes(moveReaction).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.SCHEDULE_REACTION_CANNOT_BE_EDITED }),
                "反应计划不可被移动");
            Assert.That(Task09A2Fixture.RejectionCodes(removeReaction).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.SCHEDULE_REACTION_CANNOT_BE_EDITED }),
                "反应计划不可被删除");

            Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(scheduleBefore),
                "四条被拒命令必须零局部写入");
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revision));
            Assert.That(attack.IsTerminal, Is.False);
            Assert.That(reaction.IsTerminal, Is.False);
            Assert.That(sim.ScheduleAuthority.LaneOfPlan(reaction.ActionPlanId), Is.Not.Null,
                "反应计划仍必须在 Lane 中（没有被越权移除）");
        }

        // =====================================================================
        // 必需测试 42：RunningPlanFromPastWindowDoesNotBlockAiWindow
        // =====================================================================

        /// <summary>
        /// 不变量 5（<c>WindowId</c> 只出现在命令校验与审计字段，不构成动作生命周期边界）：
        /// 一个在<strong>已关闭窗口</strong>里启动、仍在 Running 的普通计划，
        /// <strong>不妨碍</strong>另一个单位在自己的新窗口里新增计划。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把"当前窗口 / 计划来源窗口"当作计划存续或新窗口打开的条件；
        /// 新窗口的预算被旧窗口的预留污染。
        /// </para>
        /// </summary>
        [Test]
        public void RunningPlanFromPastWindowDoesNotBlockAiWindow()
        {
            var sim = Task09A2Fixture.NewSim();
            WindowId heroWindow = OpenHeroWindow(sim);

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, heroWindow, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, 4L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1L)), Is.Empty);

            Task09A2Fixture.AdvanceTo(sim, 4L);
            ActionPlan heroPlan = sim.ScheduleAuthority.Registry.ActivePlans
                .Single(p => p.OwnerUnitId == Task09A2Fixture.Hero);
            Assert.That(heroPlan.State, Is.EqualTo(ActionPlanState.Running),
                "夹具前提：hero 的攻击已启动（计划在旧窗口里 Running）");

            // 关闭 hero 的窗口。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId,
                Task09A2Fixture.CloseWindow(5L, heroWindow));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 5L)), Is.Empty);
            Assert.That(sim.CurrentTurnWindow, Is.Null);
            TurnWindow pastWindow = sim.WindowManager.FindWindow(heroWindow);
            Assert.That(pastWindow.IsOpen, Is.False);
            int pastReserved = pastWindow.ReservedFor(heroPlan.ActionPlanId);

            // mon 的新窗口在 Tick 6 打开。
            sim.WindowManager.ScheduleWindow(6L, Task09A2Fixture.Monster, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(sim, 6L);
            TurnWindow aiWindow = sim.CurrentTurnWindow;
            Assert.That(aiWindow, Is.Not.Null, "旧窗口关闭后必须能打开新窗口");
            Assert.That(aiWindow.WindowId, Is.Not.EqualTo(heroWindow));
            Assert.That(aiWindow.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Monster));

            // —— 被测：AI 在自己的窗口里新增 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.AddPlan(
                7L, sim.ScheduleAuthority.ScheduleRevision, aiWindow.WindowId,
                Task09A2Fixture.Monster, Attack(), Task09A2Fixture.Hero, 20L));
            StepResult added = Task09A2Fixture.Step(sim, 7L);

            Assert.That(Task09A2Fixture.AllRejectionCodes(added), Is.Empty,
                "过去窗口中 Running 的计划不得阻止 AI 在自己的窗口里新增");
            ActionPlan aiPlan = sim.ScheduleAuthority.Registry.ActivePlans
                .Single(p => p.OwnerUnitId == Task09A2Fixture.Monster);
            Assert.That(aiPlan.SubmittedWindowId.Value, Is.EqualTo(aiWindow.WindowId));
            Assert.That(aiWindow.ReservedFor(aiPlan.ActionPlanId), Is.EqualTo(aiPlan.BudgetCostTicks),
                "新窗口的账本只包含它自己的计划");

            // 旧计划与旧窗口账本一位不变，且它仍然是活动计划（未被关窗终止）。
            Assert.That(heroPlan.State, Is.EqualTo(ActionPlanState.Running));
            Assert.That(heroPlan.IsTerminal, Is.False);
            Assert.That(pastWindow.ReservedFor(heroPlan.ActionPlanId), Is.EqualTo(pastReserved),
                "旧窗口账本不得被新窗口的提交污染");
        }

        // =====================================================================
        // 必需测试 51：ConcurrentActionScheduleEditUsesNormalSubmissionPath
        // =====================================================================

        /// <summary>
        /// 并发行动排程编辑走<strong>普通提交路径</strong>：拿到并发授权后，
        /// 一条普通的 <c>ScheduleEditPayload</c> 经同一个 <c>BattleCommandProcessor</c>
        /// 与同一个 <c>ScheduleEditor</c> 事务被接受，计划带 <c>SubmittedWindowId</c> 并预留窗口预算。
        /// 对照：没有并发授权时同一条编辑以 <c>NO_SUBMISSION_AUTHORITY</c> 稳定拒绝。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：为并发行动建立第二套动作调度器/写入路径；
        /// 把并发授权当成"跳过窗口与预算校验"的万能钥匙（那样预订额会漏记）。
        /// </para>
        /// </summary>
        [Test]
        public void ConcurrentActionScheduleEditUsesNormalSubmissionPath()
        {
            // —— 世界 1：先激活并发授权，再提交普通排程编辑 ——
            var assembly = new BattleSimulationAssembly(concurrentHeroUnitId: Task09A2Fixture.Hero);
            BattleSimulation sim = Task09A2Fixture.NewSimWithMeta(
                ConcurrentActionDefinition.FrozenV1.MetaResourceCost, assembly: assembly);
            sim.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Monster, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(sim, 0L);
            WindowId enemyWindow = sim.CurrentTurnWindow.WindowId;

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, new CommandRequest(
                1L, new WindowCommandScope(enemyWindow),
                new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction)));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1L)), Is.Empty,
                "夹具前提：并发激活本身必须成功");
            Assert.That(sim.CurrentSnapshot.ConcurrentAction.HasActiveAuthorization, Is.True);

            long revision = sim.ScheduleAuthority.ScheduleRevision;
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                2L, revision, enemyWindow, Task09A2Fixture.Hero, Attack(), Task09A2Fixture.Monster, 20L));
            StepResult accepted = Task09A2Fixture.Step(sim, 2L);

            Assert.That(Task09A2Fixture.AllRejectionCodes(accepted), Is.Empty,
                "并发授权下的普通排程编辑必须走普通提交路径并被接受");
            ActionPlan plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(plan.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Hero));
            Assert.That(plan.SubmittedWindowId.Value, Is.EqualTo(enemyWindow),
                "并发编辑的计划仍记录它实际预留的窗口");
            Assert.That(sim.WindowManager.FindWindow(enemyWindow).ReservedFor(plan.ActionPlanId),
                Is.EqualTo(plan.BudgetCostTicks),
                "并发编辑必须照常预留窗口预算（不是绕过账本）");
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revision + 1L));
            Assert.That(sim.CommandProcessor.CommittedTransactionCount, Is.EqualTo(1),
                "同一次提交由唯一的命令处理器计数");
            Assert.That(((PayloadRoutedCommandPort)sim.CommandProcessor.ScheduleEditSink).Target,
                Is.SameAs(((PayloadRoutedCommandPort)sim.CommandProcessor.WindowSink).Target),
                "并发编辑走的是与普通排程完全相同的实现实例");

            // —— 世界 2：对照，不激活并发授权 ——
            var noAuthorityAssembly = new BattleSimulationAssembly(concurrentHeroUnitId: Task09A2Fixture.Hero);
            BattleSimulation control = Task09A2Fixture.NewSimWithMeta(
                ConcurrentActionDefinition.FrozenV1.MetaResourceCost, assembly: noAuthorityAssembly);
            control.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Monster, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(control, 0L);
            WindowId controlWindow = control.CurrentTurnWindow.WindowId;
            long controlRevision = control.ScheduleAuthority.ScheduleRevision;

            Task09A2Fixture.Submit(control, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, controlRevision, controlWindow, Task09A2Fixture.Hero, Attack(),
                Task09A2Fixture.Monster, 20L));
            StepResult rejected = Task09A2Fixture.Step(control, 1L);

            Assert.That(Task09A2Fixture.RejectionCodes(rejected).ToArray(),
                Is.EqualTo(new[] { TurnWindowCodes.NO_SUBMISSION_AUTHORITY }),
                "没有并发授权时，他人窗口里的新增必须以稳定码拒绝");
            Assert.That(control.ScheduleAuthority.Registry.ActivePlans.Count, Is.Zero);
            Assert.That(control.ScheduleAuthority.ScheduleRevision, Is.EqualTo(controlRevision));
            Assert.That(control.WindowManager.FindWindow(controlWindow).ReservedBudgetTicks, Is.Zero);
        }

        // =====================================================================
        // 必需测试 53：PrimaryTargetOnlyCommandRequiresStableTargetUnitId
        // =====================================================================

        /// <summary>
        /// <c>PrimaryTargetOnly</c> 必须显式给出稳定的 <c>PrimaryTargetUnitId</c>：
        /// 缺目标 ⇒ <c>SCHEDULE_PRIMARY_TARGET_REQUIRED</c>；
        /// 目标不存在 ⇒ <c>FACTION_RELATION_UNKNOWN_ID</c>；
        /// 目标已死 ⇒ <c>SCHEDULE_PRIMARY_TARGET_DEAD</c>。
        /// 三个原因<strong>分别</strong>稳定，且没有一个会在成功时被"补一个"目标。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把缺失目标当成"无目标攻击"放行；把三个原因折叠成同一个码；
        /// 在失败后仍创建计划（半提交）。
        /// </para>
        /// </summary>
        [Test]
        public void PrimaryTargetOnlyCommandRequiresStableTargetUnitId()
        {
            var sim = Task09A2Fixture.NewSim();
            WindowId window = OpenHeroWindow(sim);
            long revision = sim.ScheduleAuthority.ScheduleRevision;

            // —— 被测 1：缺目标 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, revision, window, Task09A2Fixture.Hero, Attack(), null, 20L));
            StepResult missing = Task09A2Fixture.Step(sim, 1L);

            // —— 被测 2：目标不存在（稳定的未知单位 ID）——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                2L, revision, window, Task09A2Fixture.Hero, Attack(), new UnitId(99L), 20L));
            StepResult unknown = Task09A2Fixture.Step(sim, 2L);

            // —— 被测 3：目标已死（经真实伤害提交杀死一个低血量敌对单位）——
            BattleDefinition definition = Task09A2Fixture.BuildDefinition(withFragileHostile: true);
            BattleSimulation dead = Task09A2Fixture.NewSim(definition);
            WindowId deadWindow = OpenHeroWindow(dead);
            long deadRevision = dead.ScheduleAuthority.ScheduleRevision;
            Task09A2Fixture.Submit(dead, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, deadRevision, deadWindow, Task09A2Fixture.Hero, Attack(),
                Task09A2Fixture.FragileHostile, 1L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(dead, 1L)), Is.Empty,
                "夹具前提：杀死低血量敌对单位的攻击必须被接受");
            for (long tick = 2L; tick <= Task09A2Fixture.AttackImpactTick; tick++)
                Task09A2Fixture.Step(dead, tick);
            Assert.That(HealthQ10Of(dead, Task09A2Fixture.FragileHostile), Is.LessThanOrEqualTo(0),
                "夹具前提：目标必须已经因真实伤害提交而死亡");
            Assert.That(dead.CurrentSnapshot.BattleEnd.IsEnded, Is.False,
                "夹具前提：战斗未结束（同阵营的 b_mon 仍存活），否则命令会以 BATTLE_ALREADY_ENDED 拒绝");

            long deadRevisionAtKill = dead.ScheduleAuthority.ScheduleRevision;
            int deadPlansBefore = dead.ScheduleAuthority.Registry.ActivePlans.Count;
            Task09A2Fixture.Submit(dead, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                Task09A2Fixture.AttackImpactTick + 1L, deadRevisionAtKill, null,
                Task09A2Fixture.Hero, Attack(), Task09A2Fixture.FragileHostile, 400L));
            StepResult deadTarget = Task09A2Fixture.Step(dead, Task09A2Fixture.AttackImpactTick + 1L);

            Assert.That(Task09A2Fixture.RejectionCodes(missing).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.SCHEDULE_PRIMARY_TARGET_REQUIRED }),
                "缺目标必须有自己的稳定码");
            Assert.That(Task09A2Fixture.RejectionCodes(unknown).ToArray(),
                Is.EqualTo(new[] { FactionCodes.FACTION_RELATION_UNKNOWN_ID }),
                "目标不存在必须是稳定的未知 ID 码");
            Assert.That(Task09A2Fixture.RejectionCodes(deadTarget).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.SCHEDULE_PRIMARY_TARGET_DEAD }),
                "目标已死必须有自己的稳定码");

            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.Zero);
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revision));
            Assert.That(dead.ScheduleAuthority.Registry.ActivePlans.Count, Is.EqualTo(deadPlansBefore),
                "目标已死的请求同样不得创建计划");

            // —— 正对照：给出合法目标时接受，且目标就是载荷声明的那个 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                3L, revision, window, Task09A2Fixture.Hero, Attack(), Task09A2Fixture.Monster, 20L));
            StepResult accepted = Task09A2Fixture.Step(sim, 3L);

            Assert.That(Task09A2Fixture.AllRejectionCodes(accepted), Is.Empty);
            ActionPlan plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(plan.PrimaryTargetUnitId.Value, Is.EqualTo(Task09A2Fixture.Monster),
                "目标由载荷显式声明，Logic 不替它补一个");

            // 稳定性：同一条缺目标命令重复提交两次得到同一个码。
            long afterAccept = sim.ScheduleAuthority.ScheduleRevision;
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                4L, afterAccept, window, Task09A2Fixture.Hero, Attack(), null, 20L));
            StepResult repeatOne = Task09A2Fixture.Step(sim, 4L);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                5L, afterAccept, window, Task09A2Fixture.Hero, Attack(), null, 20L));
            StepResult repeatTwo = Task09A2Fixture.Step(sim, 5L);

            Assert.That(Task09A2Fixture.RejectionCodes(repeatOne).ToArray(),
                Is.EqualTo(Task09A2Fixture.RejectionCodes(repeatTwo).ToArray()));
        }

        /// <summary>权威快照里的生命值（Q10）。</summary>
        private static int HealthQ10Of(BattleSimulation sim, UnitId unitId)
            => sim.CurrentSnapshot.Units.Single(u => u.UnitId == unitId.Value).HealthQ10;

        // =====================================================================
        // 必需测试 54：PrimaryTargetRelationMustBeAllowedByActionSpec
        // =====================================================================

        /// <summary>
        /// 关系不允许时<strong>固定</strong>使用 <c>TARGET_RELATION_NOT_ALLOWED</c>：
        /// 唯一的 <c>FactionRelationResolver</c> 给出的分类不在该 Attack 的
        /// <c>AllowedTargetRelations</c> 中即拒绝；置位后同一目标立即被接受。
        /// 该码是<strong>唯一</strong>的关系拒绝码（别名与原码恒等）。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把友军/中立当成"默认允许"；用第二个关系拒绝码表示同一件事；
        /// 让"打不到"和"关系不允许"共用同一个码。
        /// </para>
        /// </summary>
        [Test]
        public void PrimaryTargetRelationMustBeAllowedByActionSpec()
        {
            Assert.That(ScheduleCodes.SCHEDULE_PRIMARY_TARGET_RELATION_REJECTED,
                Is.EqualTo(ScheduleCodes.TARGET_RELATION_NOT_ALLOWED),
                "关系不允许只有一个稳定码，别名与原码恒等");

            var sim = Task09A2Fixture.NewSim();
            WindowId window = OpenHeroWindow(sim);
            long revision = sim.ScheduleAuthority.ScheduleRevision;

            // —— 被测 1：Hostile 掩码打友军 ⇒ TARGET_RELATION_NOT_ALLOWED ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, revision, window, Task09A2Fixture.Hero, Attack(), Task09A2Fixture.Ally, 20L));
            StepResult friendlyFire = Task09A2Fixture.Step(sim, 1L);

            // —— 被测 2：Hostile 掩码打中立 ⇒ 同一个码 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                2L, revision, window, Task09A2Fixture.Hero, Attack(), Task09A2Fixture.Neutral, 20L));
            StepResult neutralFire = Task09A2Fixture.Step(sim, 2L);

            Assert.That(Task09A2Fixture.RejectionCodes(friendlyFire).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.TARGET_RELATION_NOT_ALLOWED }));
            Assert.That(Task09A2Fixture.RejectionCodes(neutralFire).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.TARGET_RELATION_NOT_ALLOWED }));
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.Zero);
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revision));

            // —— 正对照 1：同一动作打敌人（掩码内置位）⇒ 接受 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                3L, revision, window, Task09A2Fixture.Hero, Attack(), Task09A2Fixture.Monster, 20L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 3L)), Is.Empty);

            // —— 正对照 2：显式加上 Allied 位后打友军 ⇒ 接受 ——
            long afterHostile = sim.ScheduleAuthority.ScheduleRevision;
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                4L, afterHostile, window, Task09A2Fixture.Hero, AlliedAttack(), Task09A2Fixture.Ally, 60L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 4L)), Is.Empty,
                "显式置位后关系校验必须放行");

            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.EqualTo(2));
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans
                    .Select(p => p.PrimaryTargetUnitId.Value).OrderBy(u => u.Value).ToArray(),
                Is.EqualTo(new[] { Task09A2Fixture.Monster, Task09A2Fixture.Ally }
                    .OrderBy(u => u.Value).ToArray()));
        }

        // =====================================================================
        // 必需测试 55：PrimaryTargetRelationRejectionDoesNotAutoRetarget
        // =====================================================================

        /// <summary>
        /// 关系不允许时<strong>绝不</strong>自动换目标：即使场上存在一个合法敌对目标，
        /// 请求的非法目标仍以稳定码拒绝，且<strong>不</strong>产生任何计划
        /// （既不是"打到合法目标"，也不是"打到最近的敌人"）。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：在目标关系校验失败后回退到"最近的敌对单位"；
        /// 或者把 <c>PrimaryTargetUnitId</c> 静默改写成合法值并继续提交。
        /// </para>
        /// </summary>
        [Test]
        public void PrimaryTargetRelationRejectionDoesNotAutoRetarget()
        {
            var sim = Task09A2Fixture.NewSim();
            WindowId window = OpenHeroWindow(sim);
            long revision = sim.ScheduleAuthority.ScheduleRevision;
            long nextPlanIdBefore = sim.CurrentSnapshot.NextActionPlanId;

            // 场上确实存在一个**合法**敌对目标（mon，与 hero 的位置最近），
            // 因此"自动换目标"如果存在，就一定会选中它。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, revision, window, Task09A2Fixture.Hero, Attack(), Task09A2Fixture.Ally, 20L));
            StepResult rejected = Task09A2Fixture.Step(sim, 1L);

            Assert.That(Task09A2Fixture.RejectionCodes(rejected).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.TARGET_RELATION_NOT_ALLOWED }));
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.Zero,
                "被拒的请求不得产生任何计划（包括改打合法目标的那种）");
            Assert.That(sim.CurrentSnapshot.NextActionPlanId, Is.EqualTo(nextPlanIdBefore),
                "被拒的事务不得消耗任何 ActionPlanId");
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revision));

            // —— 正对照：同一个非法目标 + 显式放行掩码 ⇒ 计划就是该目标，绝不换人 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                2L, revision, window, Task09A2Fixture.Hero, AlliedAttack(), Task09A2Fixture.Ally, 20L));
            StepResult accepted = Task09A2Fixture.Step(sim, 2L);

            Assert.That(Task09A2Fixture.AllRejectionCodes(accepted), Is.Empty);
            ActionPlan plan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(plan.PrimaryTargetUnitId.Value, Is.EqualTo(Task09A2Fixture.Ally),
                "计划的目标必须逐字等于命令声明的目标");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.EqualTo(1));
        }

        // =====================================================================
        // 冻结件 §8.4 第 1 条：R-A1-D1 回归用例
        // =====================================================================

        /// <summary>
        /// <strong>R-A1-D1 回归</strong>：构造「事务被拒 → 求值覆盖到一个既有 Attack 计划 → 回滚」，
        /// 断言该计划的 <c>EndTick</c>/<c>BudgetCostTicks</c>/<c>ReservedTurnBudgetTicks</c>
        /// 与其窗口预留额<strong>逐字段不变</strong>，且它到期仍能通过 <c>ActionStartGate</c>。
        ///
        /// <para>
        /// 构造方式：同一个批次里 [移动既有 Attack 计划] + [新增一条计划使窗口预算不足]。
        /// 求值阶段把既有计划重绑到新起点，随后预算校验以
        /// <c>INSUFFICIENT_WINDOW_BUDGET</c> 整批拒绝并回滚投影。
        /// </para>
        ///
        /// <para>
        /// <strong>变异探针（冻结件 §8.4 第 1 条）</strong>：去掉
        /// <c>ScheduleEditor.SpaceProjectionSnapshot.Restore()</c> 里的动作族门控
        /// （改回无条件 <c>SetPathProjection</c>）⇒ 本用例必须红：既有 Attack 计划的
        /// <c>EndTick</c> 会被写成 <c>StartTick + RecoveryTicks</c>、
        /// <c>BudgetCostTicks</c> 会被写成 <c>RecoveryTicks</c>，与窗口预留额不再相等，
        /// 到期门禁也会以资源提交错误失败。
        /// </para>
        /// </summary>
        [Test]
        public void RejectedTransactionRollbackDoesNotCorruptExistingNonMovePlan()
        {
            // 窗口预算刻意只够第一份计划：第二条新增必定以预算不足整批拒绝。
            var sim = Task09A2Fixture.NewSim();
            const long firstStart = 100L;
            WindowId window = OpenHeroWindow(sim, budget: 20);

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, window, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, firstStart));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1L)), Is.Empty);

            ActionPlan existing = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(existing.IsEditable, Is.True);

            long startTick = existing.StartTick;
            long endTick = existing.EndTick;
            long impactTick = existing.ImpactTick;
            int budgetCost = existing.BudgetCostTicks;
            int reserved = existing.ReservedTurnBudgetTicks;
            long lastRequested = existing.LastRequestedStartTick;
            int windowReserved = sim.WindowManager.FindWindow(window).ReservedFor(existing.ActionPlanId);

            Assert.That(budgetCost, Is.EqualTo(Task09A2Fixture.AttackWindupTicks + Task09A2Fixture.AttackRecoveryTicks),
                "夹具前提：攻击的权威成本 = 前摇 + 后摇");
            Assert.That(windowReserved, Is.EqualTo(budgetCost), "夹具前提：窗口预留额 = 计划成本");

            long revision = sim.ScheduleAuthority.ScheduleRevision;

            // —— 被测事务（**单条命令**同时移动既有计划并新增一条使预算不足的计划）——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Edit(
                2L, revision, window,
                new MoveEditablePlanOperation(existing.ActionPlanId, 150L),
                Task09A2Fixture.AddHeroAttack(9L, 200L, Task09A2Fixture.Hero, Attack(),
                    Task09A2Fixture.Monster)));
            StepResult rejected = Task09A2Fixture.Step(sim, 2L);

            Assert.That(Task09A2Fixture.RejectionCodes(rejected).ToArray(),
                Is.EqualTo(new[] { TurnWindowCodes.INSUFFICIENT_WINDOW_BUDGET }),
                "夹具前提：这条命令必须因预算不足被拒（而不是别的码）");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.EqualTo(1),
                "被拒事务不得新增计划");
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revision),
                "失败事务零局部写入，不推进修订号");

            // —— 核心断言：逐字段不变 ——
            Assert.That(existing.StartTick, Is.EqualTo(startTick), "StartTick 必须回滚");
            Assert.That(existing.EndTick, Is.EqualTo(endTick),
                "EndTick 必须逐字段不变（无条件路径投影会把它写成 StartTick + RecoveryTicks）");
            Assert.That(existing.ImpactTick, Is.EqualTo(impactTick));
            Assert.That(existing.BudgetCostTicks, Is.EqualTo(budgetCost),
                "BudgetCostTicks 必须逐字段不变（无条件路径投影会把它写成 RecoveryTicks）");
            Assert.That(existing.ReservedTurnBudgetTicks, Is.EqualTo(reserved));
            Assert.That(existing.LastRequestedStartTick, Is.EqualTo(lastRequested));
            Assert.That(sim.WindowManager.FindWindow(window).ReservedFor(existing.ActionPlanId),
                Is.EqualTo(windowReserved),
                "窗口账本的预留额必须与计划自己的成本仍然相等");
            Assert.That(sim.WindowManager.FindWindow(window).ReservedFor(existing.ActionPlanId),
                Is.EqualTo(existing.BudgetCostTicks),
                "冻结契约：预留额 == BudgetCostTicks（不相等即触发资源提交错误）");

            // —— 到期仍能通过启动门禁 ——
            for (long tick = 3L; tick <= startTick; tick++) Task09A2Fixture.Step(sim, tick);

            Assert.That(existing.State, Is.EqualTo(ActionPlanState.Running),
                "到期必须能通过 ActionStartGate 进入 Running（RESOURCE_COMMIT_ERROR 会让它停在 Editable）");
            Assert.That(existing.LockedAtTick, Is.EqualTo(startTick));
            Assert.That(existing.ReservedTurnBudgetTicks, Is.Zero);
            Assert.That(sim.WindowManager.FindWindow(window).SpentBudgetTicks, Is.EqualTo(budgetCost),
                "启动提交必须把预留原子转成 Spent");
            Assert.That(sim.WindowManager.FindWindow(window).ReservedBudgetTicks, Is.Zero);
        }

        // =====================================================================
        // 冻结件 §8.4 第 2 条：RequestedStartTick == null（Lane 尾部）专测
        // =====================================================================

        /// <summary>
        /// <c>AddOrdinaryPlanOperation.RequestedStartTick == null</c> 的唯一解析口径是
        /// <strong>本次事务该 Lane 的尾部</strong>：
        /// <list type="bullet">
        /// <item>Lane 为空 ⇒ 尾部的下界回到目标 Tick（不是 0、不是某个隐式默认）；</item>
        /// <item>Lane 已有计划 ⇒ 新计划被追加在该 Lane 的最大 <c>EndTick</c> 之后，
        /// 绝不覆盖或左移既有计划；</item>
        /// <item>两个计划的 <c>LastRequestedStartTick</c> 都等于自己解析后的起点。</item>
        /// </list>
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把 null 当成 0（新计划排进过去）；
        /// 把它当成"当前 Tick"（与既有计划重叠 ⇒ Lane 冲突或左移）；
        /// 或者让生产者通过 null 间接声明绝对排程。
        /// </para>
        /// </summary>
        [Test]
        public void NullRequestedStartTickResolvesToLaneTail()
        {
            var sim = Task09A2Fixture.NewSim();
            WindowId window = OpenHeroWindow(sim);

            // —— 被测 1：空 Lane ⇒ 尾部退化为目标 Tick ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, window, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, requestedStartTick: null, temporaryKey: 1L));
            StepResult first = Task09A2Fixture.Step(sim, 1L);
            Assert.That(Task09A2Fixture.AllRejectionCodes(first), Is.Empty,
                "空 Lane 的 null 起点必须被接受");

            ActionPlan firstPlan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(firstPlan.StartTick, Is.EqualTo(1L),
                "空 Lane 时 Lane 尾部 = 目标 Tick（不是 0，也不是任何隐式默认）");
            Assert.That(firstPlan.LastRequestedStartTick, Is.EqualTo(firstPlan.StartTick));

            // —— 被测 2：Lane 已有计划 ⇒ 追加到尾部（不覆盖、不左移）——
            long firstEnd = firstPlan.EndTick;
            long revision = sim.ScheduleAuthority.ScheduleRevision;

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                2L, revision, window, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, requestedStartTick: null, temporaryKey: 2L));
            StepResult second = Task09A2Fixture.Step(sim, 2L);
            Assert.That(Task09A2Fixture.AllRejectionCodes(second), Is.Empty,
                "Lane 尾部的 null 起点必须被接受");

            IReadOnlyList<ActionPlan> plans = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(plans.Count, Is.EqualTo(2));
            ActionPlan secondPlan = plans.Single(p => p.ActionPlanId != firstPlan.ActionPlanId);
            Assert.That(secondPlan.StartTick, Is.EqualTo(firstEnd),
                "新计划必须被追加在该 Lane 的尾部（= 既有计划的最大 EndTick）");
            Assert.That(secondPlan.StartTick, Is.Not.EqualTo(2L),
                "null 绝不解析成当前 Tick：那会与既有计划重叠");
            Assert.That(secondPlan.StartTick, Is.GreaterThan(firstPlan.StartTick));
            Assert.That(firstPlan.StartTick, Is.EqualTo(1L), "既有计划绝不被左移或覆盖");
            Assert.That(firstPlan.EndTick, Is.EqualTo(firstEnd), "既有计划的时长不变");
            Assert.That(secondPlan.LastRequestedStartTick, Is.EqualTo(secondPlan.StartTick));

            // —— 对照：显式起点仍被原样尊重（Lane 尾部不是"只能追加"）——
            long revisionAfterTail = sim.ScheduleAuthority.ScheduleRevision;
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                3L, revisionAfterTail, window, Task09A2Fixture.Hero,
                Attack(), Task09A2Fixture.Monster, requestedStartTick: 400L, temporaryKey: 3L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 3L)), Is.Empty);
            ActionPlan explicitPlan = sim.ScheduleAuthority.Registry.ActivePlans
                .Single(p => p.StartTick == 400L);
            Assert.That(explicitPlan.LastRequestedStartTick, Is.EqualTo(400L));
        }
    }
}
