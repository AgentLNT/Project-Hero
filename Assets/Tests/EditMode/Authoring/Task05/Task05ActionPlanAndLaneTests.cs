using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Authoring.Tests.Task05
{
    /// <summary>
    /// 任务 05 阶段 A：<c>ActionPlan</c> 数据与生命周期、全局注册表、<c>ActorLane</c>。
    ///
    /// 覆盖任务包「必需测试」中属于本阶段的部分（时序解析、单一身份、Lane 串行、
    /// 障碍不可变、目标关系、快照字段）。排程事务/门禁/终态协调器/反应机会的用例
    /// 分别属于阶段 B/C/D。
    /// </summary>
    [TestFixture]
    public sealed class Task05ActionPlanAndLaneTests
    {
        private const string Attack = Task05Farm.AttackId;
        private const string Guard = Task05Farm.GuardId;
        private const string Move = Task05Farm.MoveId;
        private const string Block = Task05Farm.BlockId;
        private const string Dodge = Task05Farm.DodgeId;

        // ————————————————————————————————————————————————————————————
        // 单一身份：不存在草稿/计划副本
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>OrdinaryPlanUsesSingleIdentityAcrossEditableLockedAndRunning</c>（阶段 A 部分）：
        /// 普通计划创建即获得<strong>稳定</strong> ID 并进入 Editable；
        /// 装配里<strong>不存在</strong> DraftPlan/ScheduledAction 这类第二形态，
        /// 因此"执行时复制字段"在类型层面不可能发生。
        /// </summary>
        [Test]
        public void OrdinaryPlanUsesSingleIdentityAcrossEditableLockedAndRunning()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            ActionPlan plan = factory.OrdinaryAttack(100L);

            Assert.That(plan.ActionPlanId.IsValid, Is.True, "创建即必须获得稳定 ActionPlanId");
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Editable), "普通计划创建即 Editable");
            Assert.That(plan.Origin, Is.EqualTo(ActionPlanOrigin.Ordinary));
            Assert.That(plan.IsOrdinary, Is.True);
            Assert.That(plan.IsReaction, Is.False);

            // 同一对象在注册前后是同一个引用：注册不产生第二个计划。
            var authority = new ActionScheduleAuthority();
            authority.RegisterPlan(plan);
            Assert.That(authority.Registry.Find(plan.ActionPlanId), Is.SameAs(plan),
                "注册表必须返回同一个 ActionPlan 实例，不得复制");
            Assert.That(authority.Registry.ActiveCount, Is.EqualTo(1));

            // 类型层面的证据：Logic 程序集里没有第二种计划形态。
            var logicAssembly = typeof(ActionPlan).Assembly;
            var forbidden = logicAssembly.GetTypes()
                .Where(t => t.Name.Contains("DraftPlan") || t.Name.Contains("ScheduledAction"))
                .Select(t => t.FullName)
                .ToArray();
            Assert.That(forbidden, Is.Empty,
                "不得存在独立草稿计划类型或执行时复制对象：" + string.Join(",", forbidden));
        }

        /// <summary>
        /// <c>ReactionPlanIsCreatedLockedWithoutEditablePhase</c>：
        /// 反应计划创建时直接 Locked，没有可观察的 Editable 阶段。
        /// </summary>
        [Test]
        public void ReactionPlanIsCreatedLockedWithoutEditablePhase()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            ActionPlanCreationResult result = factory.CreateReaction(Block, triggerTick: 200L, responseDeadlineTick: 140L);

            Assert.That(result.Succeeded, Is.True, "反应计划创建必须成功：" + result.RejectionCode);
            ActionPlan plan = result.Plan;

            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Locked),
                "反应计划创建后必须直接 Locked，绝不能停留在 Editable");
            Assert.That(plan.IsEditable, Is.False, "反应计划不得有可观察的 Editable 阶段");
            Assert.That(plan.IsReaction, Is.True);
            Assert.That(plan.LockedAtTick, Is.EqualTo(0L), "反应锁定 Tick = 创建 Tick（可审计）");
        }

        /// <summary>
        /// <c>ReactionPlanUsesOpportunityBindingAndNullSubmittedWindowId</c>：
        /// 反应计划带 <c>TriggerBinding</c>、<c>SubmittedWindowId</c> 恒为空、预算恒为 0，
        /// 且固定区间严格等于 <c>[TriggerTick - ReactionWindup, TriggerTick + Recovery)</c>。
        /// </summary>
        [Test]
        public void ReactionPlanUsesOpportunityBindingAndNullSubmittedWindowId()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            ActionPlan block = factory.CreateReaction(Block, 200L, 140L).Plan;
            ActionPlan dodge = factory.CreateReaction(Dodge, 500L, 470L, 2L, 7L).Plan;

            Assert.That(block.SubmittedWindowId.HasValue, Is.False, "高阶反应的 SubmittedWindowId 必须为空");
            Assert.That(block.ReactionOpportunityId.HasValue, Is.True);
            Assert.That(block.ReactionOpportunityId.Value.Value, Is.EqualTo(1L));
            Assert.That(block.SourceThreatPlanId.HasValue, Is.True);
            Assert.That(block.SourceThreatPlanId.Value.Value, Is.EqualTo(1L));
            Assert.That(block.TriggerTick, Is.EqualTo(200L));

            Assert.That(block.StartTick, Is.EqualTo(200L - Task05Farm.BlockReactionWindup),
                "Block：StartTick = TriggerTick - ReactionWindupTicks");
            Assert.That(block.EndTick, Is.EqualTo(200L + Task05Farm.BlockRecovery),
                "Block：EndTick = TriggerTick + RecoveryTicks");
            Assert.That(block.BudgetCostTicks, Is.EqualTo(0), "反应计划预算成本恒为 0");
            Assert.That(block.ReservedTurnBudgetTicks, Is.EqualTo(0), "反应计划不参与窗口预算");

            Assert.That(dodge.StartTick, Is.EqualTo(500L - Task05Farm.DodgeReactionWindup));
            Assert.That(dodge.EndTick, Is.EqualTo(500L + Task05Farm.DodgeRecovery));
            Assert.That(dodge.ReactionOpportunityId.Value.Value, Is.EqualTo(7L));
            Assert.That(dodge.SourceThreatPlanId.Value.Value, Is.EqualTo(2L));
            Assert.That(dodge.TriggerTick, Is.EqualTo(500L));
        }

        // ————————————————————————————————————————————————————————————
        // 相对时序一次性解析
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>AttackRelativeTimingResolvesOnceWhenPlanBecomesEditable</c>：
        /// 攻击首次进入 Editable 时一次性解析前摇；二次解析被稳定码拒绝，
        /// 且创建后改写单位 ActionSpeed 不影响已解析值。
        /// </summary>
        [Test]
        public void AttackRelativeTimingResolvesOnceWhenPlanBecomesEditable()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            ActionPlan plan = factory.OrdinaryAttack(100L);

            Assert.That(plan.IsTimingResolved, Is.True, "首次进入 Editable 必须解析时序");
            Assert.That(plan.ResolvedWindupTicks, Is.EqualTo(Task05Farm.AttackWindup));
            Assert.That(plan.RecoveryTicks, Is.EqualTo(Task05Farm.AttackRecovery));

            // 第二次解析必须被稳定码拒绝（"只解析一次"是契约，不是约定）。
            ActionSpec spec = factory.Definition.FindAction(new ActionSpecId(Attack));
            var rejected = Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() =>
                plan.ResolveTimingOnce(
                    factory.Definition.Rules, Task05Farm.ReferenceSpeed, Task05Farm.ReferenceSpeed, spec));
            Assert.That(rejected.ErrorCode, Is.EqualTo(ActionPlanCodes.ACTION_PLAN_TIMING_ALREADY_RESOLVED),
                "二次解析必须给出 ACTION_PLAN_TIMING_ALREADY_RESOLVED");

            // 速度改写不再影响已解析值（一次性采样的真实含义）。
            factory.Facts.SetActionSpeed(Task05Farm.Hero, 40f);
            Assert.That(plan.ResolvedWindupTicks, Is.EqualTo(Task05Farm.AttackWindup),
                "创建后改写 ActionSpeed 不得改变已解析前摇");
            Assert.That(plan.EndTick - plan.StartTick,
                Is.EqualTo(Task05Farm.AttackWindup + Task05Farm.AttackRecovery));
        }

        /// <summary>
        /// <c>ActionSpeedChangesWindupButNotRecovery</c>：
        /// <c>ResolvedWindupTicks = max(1, RoundHalfUp(BaseWindup × Reference / ActionSpeed))</c>，
        /// 而后摇<strong>不</strong>随速度缩短。
        /// </summary>
        [Test]
        public void ActionSpeedChangesWindupButNotRecovery()
        {
            // 速度 = 基准 20 ⇒ 前摇不变。
            var baseline = Task05Farm.NewFactory();
            ActionPlan normal = baseline.OrdinaryAttack(0L);
            Assert.That(normal.ResolvedWindupTicks, Is.EqualTo(30));
            Assert.That(normal.RecoveryTicks, Is.EqualTo(30));

            // 速度 = 10（更慢）⇒ 前摇翻倍 = 60，后摇仍为 30。
            var slowFacts = Task05Farm.Facts();
            slowFacts.SetActionSpeed(Task05Farm.Hero, 10f);
            ActionPlan slow = Task05Farm.NewFactory(slowFacts).OrdinaryAttack(0L);
            Assert.That(slow.ResolvedWindupTicks, Is.EqualTo(60), "30 × 20 / 10 = 60");
            Assert.That(slow.RecoveryTicks, Is.EqualTo(30), "后摇不随速度缩短（恒为 AttackTimingSpec 值）");

            // 速度 = 40（更快）⇒ 前摇减半 = 15，后摇仍为 30。
            var fastFacts = Task05Farm.Facts();
            fastFacts.SetActionSpeed(Task05Farm.Hero, 40f);
            ActionPlan fast = Task05Farm.NewFactory(fastFacts).OrdinaryAttack(0L);
            Assert.That(fast.ResolvedWindupTicks, Is.EqualTo(15), "30 × 20 / 40 = 15");
            Assert.That(fast.RecoveryTicks, Is.EqualTo(30));
        }

        /// <summary>
        /// <c>AttackImpactOccursAtResolvedWindupEnd</c> 与
        /// <c>AttackBudgetCostEqualsResolvedWindupPlusRecovery</c>：
        /// <c>ImpactTick</c> 恰好在解析前摇的右端，预算成本严格等于 前摇 + 后摇。
        /// </summary>
        [Test]
        public void AttackImpactOccursAtResolvedWindupEndAndBudgetEqualsWindupPlusRecovery()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            ActionPlan plan = factory.OrdinaryAttack(120L);

            Assert.That(plan.StartTick, Is.EqualTo(120L));
            Assert.That(plan.ImpactTick, Is.EqualTo(120L + plan.ResolvedWindupTicks),
                "ImpactTick 必须恰好在 [StartTick, ImpactTick) 的右端");
            Assert.That(plan.EndTick, Is.EqualTo(plan.ImpactTick + plan.RecoveryTicks),
                "Recovery 半开区间 [ImpactTick, EndTick)");
            Assert.That(plan.BudgetCostTicks, Is.EqualTo(plan.ResolvedWindupTicks + plan.RecoveryTicks),
                "预算成本严格等于 ResolvedWindupTicks + RecoveryTicks");
            Assert.That(plan.BudgetCostTicks, Is.EqualTo(60), "冻结夹具下 = 30 + 30");

            // 速度变慢时预算也随之改变（预算用的是解析后的前摇，不是基准值）。
            var slowFacts = Task05Farm.Facts();
            slowFacts.SetActionSpeed(Task05Farm.Hero, 10f);
            ActionPlan slow = Task05Farm.NewFactory(slowFacts).OrdinaryAttack(120L);
            Assert.That(slow.BudgetCostTicks, Is.EqualTo(60 + 30), "60 + 30");
            Assert.That(slow.ImpactTick, Is.EqualTo(120L + 60));
        }

        /// <summary>
        /// <c>MovingEditableAttackRebindsAbsoluteTicksWithoutResamplingSpeed</c>：
        /// 排程编辑只重绑<strong>绝对</strong> Tick；已解析的相对时序一个字节都不变。
        /// </summary>
        [Test]
        public void MovingEditableAttackRebindsAbsoluteTicksWithoutResamplingSpeed()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            ActionPlan plan = factory.OrdinaryAttack(100L);
            int windup = plan.ResolvedWindupTicks;
            int recovery = plan.RecoveryTicks;
            int budget = plan.BudgetCostTicks;

            // 直接重绑绝对 Tick（阶段 B 的 ScheduleEditor 会调用同一条内部入口）。
            plan.RebindAbsoluteTicks(300L);

            Assert.That(plan.StartTick, Is.EqualTo(300L));
            Assert.That(plan.ImpactTick, Is.EqualTo(300L + windup));
            Assert.That(plan.EndTick, Is.EqualTo(300L + windup + recovery));
            Assert.That(plan.ResolvedWindupTicks, Is.EqualTo(windup), "重绑不得重采样前摇");
            Assert.That(plan.RecoveryTicks, Is.EqualTo(recovery), "重绑不得重采样后摇");
            Assert.That(plan.BudgetCostTicks, Is.EqualTo(budget), "重绑不得改变预算成本");
        }

        /// <summary>
        /// <c>AcceptedAttackTimingDoesNotChangeAfterSpeedMutation</c>：
        /// 计划被接受（此处以注册进权威 Lane 表示）之后改写单位速度，
        /// 计划的全部时序字段必须保持逐字不变。
        /// </summary>
        [Test]
        public void AcceptedAttackTimingDoesNotChangeAfterSpeedMutation()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            var authority = factory.NewAuthority();
            ActionPlan plan = factory.OrdinaryAttack(64L);
            authority.RegisterPlan(plan);

            long start = plan.StartTick, impact = plan.ImpactTick, end = plan.EndTick;
            int windup = plan.ResolvedWindupTicks, recovery = plan.RecoveryTicks, budget = plan.BudgetCostTicks;

            // 速度 Buff/Debuff、窗口切换与视觉缩放都归结为"事实变化"：不得改已接受计划。
            factory.Facts.SetActionSpeed(Task05Farm.Hero, 5f);
            factory.Facts.SetActionSpeed(Task05Farm.Hero, 80f);

            Assert.That(plan.StartTick, Is.EqualTo(start));
            Assert.That(plan.ImpactTick, Is.EqualTo(impact));
            Assert.That(plan.EndTick, Is.EqualTo(end));
            Assert.That(plan.ResolvedWindupTicks, Is.EqualTo(windup));
            Assert.That(plan.RecoveryTicks, Is.EqualTo(recovery));
            Assert.That(plan.BudgetCostTicks, Is.EqualTo(budget));
        }

        /// <summary>
        /// <c>GuardTimingResolvesWindupActiveRecoveryAndBudgetOnce</c>：
        /// Guard 的 Windup / Active / Recovery 是<strong>半开</strong>区间，三段都固定
        /// （不读 ActionSpeed），预算等于三段之和。
        /// </summary>
        [Test]
        public void GuardTimingResolvesWindupActiveRecoveryAndBudgetOnce()
        {
            var slowFacts = Task05Farm.Facts();
            slowFacts.SetActionSpeed(Task05Farm.Hero, 4f);   // 若 Guard 误读速度，前摇会变成 50
            Task05Factory factory = Task05Farm.NewFactory(slowFacts);

            ActionPlan guard = factory.CreatePlan(Guard, 200L).Plan;
            Assert.That(guard.ActionType, Is.EqualTo(ActionType.Guard));
            Assert.That(guard.ResolvedWindupTicks, Is.EqualTo(Task05Farm.GuardWindup),
                "Guard 的前摇固定，不读 ActionSpeed");
            Assert.That(guard.ActiveTicks, Is.EqualTo(Task05Farm.GuardActive));
            Assert.That(guard.RecoveryTicks, Is.EqualTo(Task05Farm.GuardRecovery));

            Assert.That(guard.ActiveStartTick, Is.EqualTo(200L + Task05Farm.GuardWindup));
            Assert.That(guard.ActiveEndTick, Is.EqualTo(guard.ActiveStartTick + Task05Farm.GuardActive));
            Assert.That(guard.EndTick, Is.EqualTo(guard.ActiveEndTick + Task05Farm.GuardRecovery));
            Assert.That(guard.BudgetCostTicks,
                Is.EqualTo(Task05Farm.GuardWindup + Task05Farm.GuardActive + Task05Farm.GuardRecovery));

            // 半开区间：Active 起点在内、右端点在外。
            Assert.That(guard.IsActiveAt(guard.ActiveStartTick), Is.True);
            Assert.That(guard.IsActiveAt(guard.ActiveEndTick - 1L), Is.True);
            Assert.That(guard.IsActiveAt(guard.ActiveEndTick), Is.False,
                "Active 是半开区间 [ActiveStartTick, ActiveEndTick)：右端不属于 Active");
            Assert.That(guard.IsActiveAt(guard.ActiveStartTick - 1L), Is.False);
        }

        /// <summary>
        /// <c>MoveStepTicksResolveOnceButEditablePathCountCanRecompute</c>：
        /// <c>ResolvedBaseStepTicks</c> 一次性采样后固定；Editable 期内路径边数/权重可重算，
        /// 且预算与 <c>EndTick</c> 使用<strong>路径权重</strong>而不是边数。
        /// </summary>
        [Test]
        public void MoveStepTicksResolveOnceButEditablePathCountCanRecompute()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            ActionPlan move = factory.CreatePlan(Move, 100L, null, null, pathEdgeCount: 4, pathWeightUnits: 4).Plan;

            Assert.That(move.ResolvedBaseStepTicks, Is.EqualTo(Task05Farm.MoveBaseStepTicks),
                "MoveSpeed = 基准 ⇒ BaseStepTicks 不变（一次性采样）");
            Assert.That(move.ResolvedPathEdgeCount, Is.EqualTo(4));
            Assert.That(move.ResolvedPathWeightUnits, Is.EqualTo(4));
            Assert.That(move.MoveDurationTicks, Is.EqualTo(4 * 5));
            Assert.That(move.EndTick, Is.EqualTo(100L + 20 + Task05Farm.MoveRecovery));
            Assert.That(move.BudgetCostTicks, Is.EqualTo(20 + Task05Farm.MoveRecovery));

            // 采样后的基准固定：改写 MoveSpeed 不改 ResolvedBaseStepTicks。
            factory.Facts.Set(Task05Farm.Hero, FactionIds.Hero, Task05Farm.ReferenceSpeed, moveSpeed: 10f);
            Assert.That(move.ResolvedBaseStepTicks, Is.EqualTo(Task05Farm.MoveBaseStepTicks),
                "MoveSpeed 只在首次进入 Editable 时采样一次");

            // Editable 期重算路径：边数变、权重也变 ⇒ 预算与 EndTick 用权重（不是边数）。
            move.SetPathProjection(edgeCount: 7, pathWeightUnits: 9);
            Assert.That(move.ResolvedPathEdgeCount, Is.EqualTo(7));
            Assert.That(move.ResolvedPathWeightUnits, Is.EqualTo(9));
            Assert.That(move.MoveDurationTicks, Is.EqualTo(9 * 5), "时长 = 路径权重 × BaseStepTicks");
            Assert.That(move.EndTick, Is.EqualTo(100L + 45 + Task05Farm.MoveRecovery));
            Assert.That(move.BudgetCostTicks, Is.EqualTo(45 + Task05Farm.MoveRecovery),
                "预算必须用路径权重而不是边数（7 边 ⇒ 若误用边数会得到 35）");
            Assert.That(move.BudgetCostTicks, Is.Not.EqualTo(7 * Task05Farm.MoveBaseStepTicks + Task05Farm.MoveRecovery));
        }

        // ————————————————————————————————————————————————————————————
        // ActorLane：串行、重叠、障碍、锁定
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>SameActorPlansAreSerializedByLane</c>：同一单位的计划在 Lane 上串行
        /// （规范顺序 + 无重叠），并且顺序键是 <c>StartTick -&gt; ActionPlanId</c>。
        /// </summary>
        [Test]
        public void SameActorPlansAreSerializedByLane()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            var authority = factory.NewAuthority();

            ActionPlan third = factory.AddOrdinary(authority, Attack, 300L);
            ActionPlan first = factory.AddOrdinary(authority, Attack, 100L);
            ActionPlan second = factory.AddOrdinary(authority, Attack, 200L);

            ActorLane lane = authority.FindLane(Task05Farm.Hero);
            Assert.That(lane, Is.Not.Null, "注册计划必须创建对应单位的 Lane");
            Assert.That(lane.Count, Is.EqualTo(3));

            var order = lane.Plans.Select(p => p.ActionPlanId.Value).ToArray();
            Assert.That(order, Is.EqualTo(new[] { first.ActionPlanId.Value, second.ActionPlanId.Value, third.ActionPlanId.Value }),
                "Lane 必须按 StartTick 排序（插入顺序不影响规范顺序）");

            Assert.That(lane.ValidateNonOverlapping(), Is.Null,
                "同单位计划必须串行：Lane 内不得出现区间重叠");
            Assert.That(authority.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null);
        }

        /// <summary>
        /// <c>SameActorPlansAreSerializedByLane</c>（同一 StartTick 的稳定次序）：
        /// 起点相同时按 <c>ActionPlanId</c> 升序，因此顺序是确定的而不是容器偶然顺序。
        /// </summary>
        [Test]
        public void SameActorPlansWithEqualStartTickAreOrderedByPlanId()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            var authority = factory.NewAuthority();

            ActionPlan a = factory.AddOrdinary(authority, Attack, 500L);
            ActionPlan b = factory.AddOrdinary(authority, Attack, 500L);

            var order = authority.FindLane(Task05Farm.Hero).Plans.Select(p => p.ActionPlanId.Value).ToArray();
            Assert.That(order, Is.EqualTo(new[] { a.ActionPlanId.Value, b.ActionPlanId.Value }),
                "起点相同时必须按 ActionPlanId 升序（稳定键）");
        }

        /// <summary>
        /// <c>DifferentActorPlansMayOverlap</c>：不同单位的计划可以在连续时间轴上重叠——
        /// 单位之间<strong>没有</strong>串行约束。
        /// </summary>
        [Test]
        public void DifferentActorPlansMayOverlap()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            var authority = factory.NewAuthority();

            ActionPlan heroPlan = factory.AddOrdinary(authority, Attack, 100L);
            ActionPlan enemyPlan = factory.AddOrdinary(authority, Attack, 100L, owner: Task05Farm.Enemy);

            Assert.That(heroPlan.OverlapsInterval(enemyPlan.StartTick, enemyPlan.EndTick), Is.True,
                "不同单位的同名区间必须允许重叠");
            Assert.That(heroPlan.StartTick, Is.EqualTo(enemyPlan.StartTick));
            Assert.That(heroPlan.EndTick, Is.EqualTo(enemyPlan.EndTick));

            ActorLane heroLane = authority.FindLane(Task05Farm.Hero);
            ActorLane enemyLane = authority.FindLane(Task05Farm.Enemy);
            Assert.That(heroLane.Count, Is.EqualTo(1));
            Assert.That(enemyLane.Count, Is.EqualTo(1));
            Assert.That(heroLane.ValidateNonOverlapping(), Is.Null);
            Assert.That(enemyLane.ValidateNonOverlapping(), Is.Null);
        }

        /// <summary>
        /// <c>LockedAndReactionIntervalsRemainFixedObstacles</c>：
        /// 反应计划的固定区间与 Locked 普通计划都是 Lane 上的不可变障碍，
        /// 避让只会向右越过它们。
        /// </summary>
        [Test]
        public void LockedAndReactionIntervalsRemainFixedObstacles()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            var authority = factory.NewAuthority();

            ActionPlan reaction = factory.CreateReaction(Block, triggerTick: 200L, responseDeadlineTick: 140L).Plan;
            authority.RegisterPlan(reaction);

            // 障碍判定：反应计划无论状态如何都是障碍。
            Assert.That(reaction.IsLaneObstacle, Is.True, "固定反应区间必须始终是 Lane 障碍");
            Assert.That(reaction.IsEditable, Is.False);

            // Locked 普通计划也是障碍。
            ActionPlan locked = factory.CreatePlan(Attack, 400L).Plan;
            locked.State = ActionPlanState.Locked;
            locked.RebindAbsoluteTicks(400L);
            authority.RegisterPlan(locked);
            Assert.That(locked.IsLaneObstacle, Is.True, "Locked 普通计划是 Lane 障碍");
            Assert.That(locked.IsEditable, Is.False);

            // Editable 计划不是障碍（它可被权威 ScheduleEditor 移动）。
            ActionPlan editable = factory.AddOrdinary(authority, Attack, 600L);
            Assert.That(editable.IsEditable, Is.True);
            Assert.That(editable.IsLaneObstacle, Is.False,
                "Editable 普通计划可由 ScheduleEditor 编辑，因此不是不可变障碍");

            ActorLane lane = authority.FindLane(Task05Farm.Hero);
            var obstacles = lane.CollectObstacles().Select(p => p.ActionPlanId.Value).ToArray();
            Assert.That(obstacles, Is.EquivalentTo(new[] { reaction.ActionPlanId.Value, locked.ActionPlanId.Value }));
            Assert.That(lane.CollectEditablePlans().Select(p => p.ActionPlanId.Value),
                Is.EqualTo(new[] { editable.ActionPlanId.Value }));

            // 向右避让：请求起点落在反应区间内部时必须被推到区间右端之后。
            long free = lane.FindEarliestFreeStartTick(
                reaction.StartTick + 1L, reaction.IntervalLength, ignore: null);
            Assert.That(free, Is.GreaterThanOrEqualTo(reaction.EndTick),
                "避让必须越过不可变障碍的右端，且只向右");
        }

        /// <summary>
        /// <c>SubmissionLockDoesNotDeleteExistingPlans</c>：
        /// 锁定 Lane 只阻止<strong>新提交</strong>，已有计划逐字保留。
        /// </summary>
        [Test]
        public void SubmissionLockDoesNotDeleteExistingPlans()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            var authority = factory.NewAuthority();

            ActionPlan editable = factory.AddOrdinary(authority, Attack, 100L);
            ActionPlan later = factory.AddOrdinary(authority, Attack, 400L);

            ActorLane lane = authority.FindLane(Task05Farm.Hero);
            int before = lane.Count;

            authority.LockLaneSubmissions(Task05Farm.Hero, "TEST_LOCK");

            Assert.That(lane.IsSubmissionLocked, Is.True);
            Assert.That(lane.SubmissionLockReason, Is.EqualTo("TEST_LOCK"));
            Assert.That(lane.Count, Is.EqualTo(before), "锁定不得删除任何已有计划");
            Assert.That(lane.Contains(editable.ActionPlanId), Is.True);
            Assert.That(lane.Contains(later.ActionPlanId), Is.True);
            Assert.That(editable.IsEditable, Is.True, "锁定不清除已有计划的可编辑性");
            Assert.That(later.State, Is.EqualTo(ActionPlanState.Editable));

            // 再次锁定幂等，原因以第一次为准。
            authority.LockLaneSubmissions(Task05Farm.Hero, "SECOND_REASON");
            Assert.That(lane.SubmissionLockReason, Is.EqualTo("TEST_LOCK"), "锁定原因以第一次为准");

            // 战斗结束锁全部 Lane：同样只锁提交。
            authority.LockAllLaneSubmissions("BATTLE_END");
            Assert.That(authority.FindLane(Task05Farm.Hero).Count, Is.EqualTo(before));
        }

        /// <summary>
        /// <c>NonIdleStateDoesNotByItselfRejectFuturePlan</c>：
        /// 单位当前状态<strong>不</strong>参与 Lane 接受未来计划的判定——
        /// Lane 的结构完全不读 <c>UnitStateMachine</c>。
        /// </summary>
        [Test]
        public void NonIdleStateDoesNotByItselfRejectFuturePlan()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            var authority = factory.NewAuthority();

            // 排一个很远的未来计划，此时单位处于任意非 Idle 状态都与 Lane 无关。
            ActionPlan future = factory.AddOrdinary(authority, Attack, 5000L);
            Assert.That(future.IsEditable, Is.True, "当前状态不是 Idle 不代表 Lane 拒绝排在未来的计划");
            Assert.That(authority.FindLane(Task05Farm.Hero).Contains(future.ActionPlanId), Is.True);

            // 结构证据：ActorLane 的任何成员都不引用 UnitStateMachine / UnitState。
            var laneMembers = typeof(ActorLane)
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            var stateTypes = new[] { typeof(ProjectHero.Logic.Units.UnitStateMachine), typeof(ProjectHero.Logic.Units.UnitState) };
            foreach (var member in laneMembers)
            {
                Type memberType = member is FieldInfo f ? f.FieldType
                    : member is PropertyInfo p ? p.PropertyType
                    : member is MethodInfo m ? m.ReturnType : null;
                if (memberType == null) continue;
                Assert.That(stateTypes, Has.No.Member(memberType),
                    "ActorLane 不得持有单位状态类型：" + member.Name);
            }
        }

        /// <summary>
        /// <c>SubmissionLockDoesNotDeleteExistingPlans</c> 的补充：
        /// 锁定只影响"是否接受新提交"这一事实，且该事实可从快照读到。
        /// </summary>
        [Test]
        public void SubmissionLockAppearsInLaneSnapshotWithoutRemovingPlans()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            var authority = factory.NewAuthority();
            factory.AddOrdinary(authority, Attack, 100L);

            IReadOnlyList<ActorLaneSnapshot> before = authority.BuildLaneSnapshots();
            Assert.That(before.Count, Is.EqualTo(1));
            Assert.That(before[0].PendingPlanCount, Is.EqualTo(1));
            Assert.That(before[0].Locked, Is.False);

            authority.LockLaneSubmissions(Task05Farm.Hero, "TEST_LOCK");

            IReadOnlyList<ActorLaneSnapshot> after = authority.BuildLaneSnapshots();
            Assert.That(after[0].Locked, Is.True, "锁定必须出现在 Lane 快照里");
            Assert.That(after[0].PendingPlanCount, Is.EqualTo(1), "锁定不减少待处理计划数");
        }

        // ————————————————————————————————————————————————————————————
        // PrimaryTarget 关系校验（创建时）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>PrimaryTargetMustSatisfyAllowedRelationAtAddAndStartGate</c>（Add 部分）：
        /// PrimaryTargetOnly 动作在<strong>创建时</strong>就必须用 Attack 的
        /// <c>AllowedTargetRelations</c> 与唯一 FactionRelationResolver 权威校验。
        /// </summary>
        [Test]
        public void PrimaryTargetMustSatisfyAllowedRelationAtAddAndStartGate()
        {
            Task05Factory factory = Task05Farm.NewFactory();

            // 正例：夹具攻击掩码 = Hostile，hero(hero) → enemy(monster) 恰好是 Hostile。
            ActionPlanCreationResult ok = factory.CreatePlan(Attack, 100L, target: Task05Farm.Enemy);
            Assert.That(ok.Succeeded, Is.True, "合法关系掩码必须通过创建校验：" + ok.RejectionCode);
            Assert.That(ok.Plan.PrimaryTargetUnitId.HasValue, Is.True);
            Assert.That(ok.Plan.PrimaryTargetUnitId.Value, Is.EqualTo(Task05Farm.Enemy));

            // 反例：掩码改成只允许友军 ⇒ 对 Hostile 目标必须在创建时被拒绝。
            BattleDefinition patched = factory.Definition with
            {
                Actions = factory.Definition.Actions.Select(a =>
                    a.ActionSpecId.Value == Attack
                        ? a with
                        {
                            Payload = ((AttackPayloadSpec)a.Payload) with
                            {
                                AllowedTargetRelations = TargetRelationMask.Allied
                            }
                        }
                        : a).ToList()
            };
            var strict = new ActionPlanFactory(patched, factory.Factions, factory.Facts, factory.Ids);
            ActionPlanCreationResult rejected = strict.TryCreateOrdinary(
                new OrdinaryPlanRequest(
                    Task05Farm.Hero, new ActionSpecId(Attack), GridDirection.East,
                    Task05Farm.Enemy, null, null, 100L),
                0L);

            Assert.That(rejected.Succeeded, Is.False, "关系不合法的 PrimaryTarget 必须在创建时被拒绝");
            Assert.That(rejected.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_PRIMARY_TARGET_RELATION_REJECTED));
            Assert.That(rejected.Plan, Is.Null, "被拒绝时不得产出任何计划对象");

            // PrimaryTargetOnly 动作缺少目标也必须在创建时拒绝。
            ActionPlanCreationResult missingTarget = strict.TryCreateOrdinary(
                new OrdinaryPlanRequest(
                    Task05Farm.Hero, new ActionSpecId(Attack), GridDirection.East,
                    null, null, null, 100L),
                0L);
            Assert.That(missingTarget.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_PRIMARY_TARGET_REQUIRED),
                "PrimaryTargetOnly 动作缺少目标必须被拒绝");
        }

        /// <summary>
        /// <c>InvalidPrimaryTargetRelationTerminatesAsTargetInvalidWithoutRetargeting</c>（阶段 A 部分）：
        /// 关系不合法时<strong>不会</strong>改选另一个单位——系统既不产出替代计划，
        /// 也不把目标重写成其他单位。
        /// </summary>
        [Test]
        public void InvalidPrimaryTargetRelationIsNeverRetargeted()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            BattleDefinition patched = factory.Definition with
            {
                Actions = factory.Definition.Actions.Select(a =>
                    a.ActionSpecId.Value == Attack
                        ? a with
                        {
                            Payload = ((AttackPayloadSpec)a.Payload) with
                            {
                                AllowedTargetRelations = TargetRelationMask.Allied
                            }
                        }
                        : a).ToList()
            };
            var strict = new ActionPlanFactory(patched, factory.Factions, factory.Facts, factory.Ids);

            // 没有任何计划被创建 ⇒ 也就不存在"改选后的目标"。
            ActionPlanCreationResult rejected = strict.TryCreateOrdinary(
                new OrdinaryPlanRequest(
                    Task05Farm.Hero, new ActionSpecId(Attack), GridDirection.East,
                    Task05Farm.Enemy, null, null, 100L),
                0L);
            Assert.That(rejected.Plan, Is.Null);

            // ID 也未被消耗：被拒绝的创建不得占用 ActionPlanId。
            Assert.That(factory.Ids.NextActionPlanIdValue, Is.EqualTo(1L),
                "被拒绝的创建不得消耗 ActionPlanId");
        }

        // ————————————————————————————————————————————————————————————
        // 快照
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>PlanAndLaneAppearInCanonicalSnapshot</c>：
        /// 活动计划与 Lane 都进入快照，且计划快照按 <c>ActionPlanId</c> 规范排序。
        /// </summary>
        [Test]
        public void PlanAndLaneAppearInCanonicalSnapshot()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            var authority = factory.NewAuthority();
            ActionPlan b = factory.AddOrdinary(authority, Attack, 200L);
            ActionPlan a = factory.AddOrdinary(authority, Attack, 100L);
            ActionPlan enemyPlan = factory.AddOrdinary(authority, Attack, 100L, owner: Task05Farm.Enemy);

            IReadOnlyList<ActionPlanSnapshot> plans = authority.BuildPlanSnapshots();
            // 规范键是 ActionPlanId（不是 StartTick）：b 先创建（ID 1、StartTick 200），
            // a 后创建（ID 2、StartTick 100），因此快照按 ID 升序得到 [b, a, enemy]。
            // 这条同时证明"快照顺序来自稳定的 ID 键，而不是插入顺序或 StartTick"。
            Assert.That(plans.Select(p => p.ActionPlanId).ToArray(),
                Is.EqualTo(new[] { b.ActionPlanId.Value, a.ActionPlanId.Value, enemyPlan.ActionPlanId.Value }),
                "计划快照必须按 ActionPlanId 升序");

            IReadOnlyList<ActorLaneSnapshot> lanes = authority.BuildLaneSnapshots();
            Assert.That(lanes.Select(l => l.UnitId).ToArray(),
                Is.EqualTo(new[] { Task05Farm.Enemy.Value, Task05Farm.Hero.Value }),
                "Lane 快照必须按 UnitId 升序");
            Assert.That(lanes.Sum(l => l.PendingPlanCount), Is.EqualTo(3));
        }

        /// <summary>
        /// <c>PlanTimingAppearsInCanonicalSnapshot</c>：
        /// 五类动作的解析时序字段（前摇/后摇/命中/Active/路径/预算）都必须进入计划快照，
        /// 否则"定时序差异"会撞成同一摘要。
        /// </summary>
        [Test]
        public void PlanTimingAppearsInCanonicalSnapshot()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            var authority = factory.NewAuthority();

            ActionPlan attack = factory.AddOrdinary(authority, Attack, 100L);
            ActionPlan guard = factory.AddOrdinary(authority, Guard, 200L);
            ActionPlan move = factory.AddOrdinary(authority, Move, 300L, pathEdgeCount: 3, pathWeightUnits: 5);
            ActionPlan blockPlan = factory.CreateReaction(Block, 500L, 440L).Plan;
            authority.RegisterPlan(blockPlan);
            ActionPlan dodgePlan = factory.CreateReaction(Dodge, 900L, 870L, 2L, 2L).Plan;
            authority.RegisterPlan(dodgePlan);

            Dictionary<long, ActionPlanSnapshot> byId = authority.BuildPlanSnapshots()
                .ToDictionary(p => p.ActionPlanId);

            ActionPlanSnapshot attackSnap = byId[attack.ActionPlanId.Value];
            Assert.That(attackSnap.ImpactTick, Is.EqualTo(attack.ImpactTick));
            Assert.That(attackSnap.ResolvedWindupTicks, Is.EqualTo(Task05Farm.AttackWindup));
            Assert.That(attackSnap.RecoveryTicks, Is.EqualTo(Task05Farm.AttackRecovery));
            Assert.That(attackSnap.BudgetCostTicks, Is.EqualTo(Task05Farm.AttackWindup + Task05Farm.AttackRecovery));

            ActionPlanSnapshot guardSnap = byId[guard.ActionPlanId.Value];
            Assert.That(guardSnap.ActiveStartTick, Is.EqualTo(guard.ActiveStartTick));
            Assert.That(guardSnap.ActiveEndTick, Is.EqualTo(guard.ActiveEndTick));
            Assert.That(guardSnap.ActiveTicks, Is.EqualTo(Task05Farm.GuardActive));

            ActionPlanSnapshot moveSnap = byId[move.ActionPlanId.Value];
            Assert.That(moveSnap.ResolvedPathEdgeCount, Is.EqualTo(3));
            Assert.That(moveSnap.ResolvedPathWeightUnits, Is.EqualTo(5));
            Assert.That(moveSnap.ResolvedBaseStepTicks, Is.EqualTo(Task05Farm.MoveBaseStepTicks));
            Assert.That(moveSnap.MoveDurationTicks, Is.EqualTo(5 * Task05Farm.MoveBaseStepTicks));

            ActionPlanSnapshot blockSnap = byId[blockPlan.ActionPlanId.Value];
            Assert.That(blockSnap.ReactionWindupTicks, Is.EqualTo(Task05Farm.BlockReactionWindup));
            Assert.That(blockSnap.TriggerTick, Is.EqualTo(500L));
            Assert.That(blockSnap.ResponseDeadlineTick, Is.EqualTo(440L));
            Assert.That(blockSnap.ReactionOpportunityId, Is.EqualTo(1L));
            Assert.That(blockSnap.SourceThreatPlanId, Is.EqualTo(1L));
            Assert.That(blockSnap.Origin, Is.EqualTo((int)ActionPlanOrigin.Reaction));
            Assert.That(blockSnap.State, Is.EqualTo((int)ActionPlanState.Locked));
            Assert.That(blockSnap.SubmittedWindowId, Is.EqualTo(0L), "反应计划的 SubmittedWindowId 必须为空");
        }

        /// <summary>
        /// <c>ScheduleRevisionAndPlanEditLockTicksAppearInCanonicalSnapshot</c> 的
        /// "锁定 Tick 与编辑修订进入快照"部分（阶段 A 可验证的一半）：
        /// 创建 Tick、锁定 Tick、自动延期次数与最后编辑修订都必须在计划快照里。
        /// </summary>
        [Test]
        public void PlanEditAndLockTickFieldsAppearInCanonicalSnapshot()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            var authority = factory.NewAuthority();

            ActionPlan plan = factory.CreatePlan(Attack, 100L, createdAtTick: 42L).Plan;
            if (plan == null) Assert.Fail("夹具创建失败：攻击计划必须创建成功");
            authority.RegisterPlan(plan);
            ActionPlanSnapshot snap = authority.BuildPlanSnapshots().Single();

            Assert.That(snap.CreatedAtTick, Is.EqualTo(42L), "创建 Tick 必须进入快照");
            Assert.That(snap.LockedAtTick, Is.EqualTo(-1L), "未锁定的计划 LockedAtTick = -1");
            Assert.That(snap.AutomaticDeferralCount, Is.EqualTo(0));
            Assert.That(snap.LastEditedScheduleRevision, Is.EqualTo(0L));
            Assert.That(snap.StartTick, Is.EqualTo(100L));
            Assert.That(snap.EndTick, Is.EqualTo(plan.EndTick));
            Assert.That(snap.LastRequestedStartTick, Is.EqualTo(plan.LastRequestedStartTick));
            Assert.That(snap.State, Is.EqualTo((int)ActionPlanState.Editable));
            Assert.That(snap.Origin, Is.EqualTo((int)ActionPlanOrigin.Ordinary));
        }

        /// <summary>
        /// <c>StepEndHasNoActiveArtifactReferencingTerminalPlan</c>（阶段 A 部分）：
        /// 权威对象在干净状态下通过"无活动对象引用终态计划"的不变量检查；
        /// 并且该检查是<strong>可失败</strong>的——人为把终态计划放回活动索引即报错。
        /// </summary>
        [Test]
        public void StepEndHasNoActiveArtifactReferencingTerminalPlan()
        {
            Task05Factory factory = Task05Farm.NewFactory();
            var authority = factory.NewAuthority();
            ActionPlan plan = factory.AddOrdinary(authority, Attack, 100L);

            Assert.That(authority.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null,
                "干净状态必须通过不变量检查");

            // 负控制：把计划标为终态却仍留在活动索引 ⇒ 检查必须失败（证明它真的在读索引）。
            plan.State = ActionPlanState.Terminated;
            plan.TerminationReason = ActionTerminationReason.CancelledByCommand;
            plan.TerminalTick = 10L;

            string failure = authority.CheckNoActiveArtifactReferencesTerminalPlan();
            Assert.That(failure, Is.Not.Null, "活动索引引用终态计划必须被检出，而不是静默通过");
            Assert.That(failure, Does.Contain("terminal"),
                "失败描述必须指出终态引用：" + failure);
        }

        // ————————————————————————————————————————————————————————————
        // 终态原因与优先级（数据层）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 冻结的 <c>ActionTerminationReason</c> 全集与两个强制位移原因的优先级：
        /// <c>MovementOriginInvalidated</c> 优先于
        /// <c>ReservationPreemptedByForcedDisplacement</c>。
        /// </summary>
        [Test]
        public void TerminationReasonsAndForcedDisplacementPriorityAreFrozen()
        {
            Assert.That(ActionTerminationReasons.All.Count, Is.EqualTo(13),
                "冻结原因集必须恰好 13 项");
            Assert.That(ActionTerminationReasons.All, Does.Contain(ActionTerminationReason.MovementOriginInvalidatedByDodge),
                "Dodge 终态接缝的原因必须存在");
            Assert.That(ActionTerminationReasons.All, Does.Contain(ActionTerminationReason.TargetInvalid));
            Assert.That(ActionTerminationReasons.All, Does.Contain(ActionTerminationReason.OwnerDied));
            Assert.That(ActionTerminationReasons.All, Does.Contain(ActionTerminationReason.BattleEnded));

            Assert.That(ActionTerminationReasons.PriorityOf(ActionTerminationReason.MovementOriginInvalidated),
                Is.LessThan(ActionTerminationReasons.PriorityOf(
                    ActionTerminationReason.ReservationPreemptedByForcedDisplacement)),
                "MovementOriginInvalidated 必须优先于 ReservationPreemptedByForcedDisplacement");

            foreach (ActionTerminationReason reason in ActionTerminationReasons.All)
            {
                string code = ActionTerminationReasons.CodeOf(reason);
                Assert.That(code, Is.Not.Null.And.Not.Empty, "每个原因必须有稳定字符串码：" + reason);
                Assert.That(code, Does.StartWith("ACTION_TERMINATION_"),
                    "稳定码前缀必须保持：" + code);
            }

            Assert.That(ActionTerminationReasons.CodeOf(ActionTerminationReason.None), Is.Null,
                "Completed 可以没有原因：None 映射为 null");
        }
    }
}
