using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 09 · A2 轮：<strong>统一命令处理器、载荷控制权与目标资格查询面</strong>
    /// （任务包「必须产出」4 第三/四段与 6 第二段）。
    ///
    /// 逐条对应必需测试：<c>PlayerAndAiActionsUseSameProcessor</c>、
    /// <c>PlayerCannotCommandAnotherControllersUnit</c>、<c>AiCannotCommandAnotherControllersUnit</c>、
    /// <c>UiAndAiCandidateFiltersUseDecisionSnapshotFactionResolver</c>、
    /// <c>ControllerKindChangeDoesNotChangeUiAiOrCommandTargetEligibility</c>、
    /// <c>FriendlyAndNeutralTargetsRequireExplicitActionMaskBits</c>。
    /// </summary>
    public class Task09ProcessorAndAuthorityTests
    {
        private static readonly UnitId[] Universe =
        {
            Task09A2Fixture.Hero, Task09A2Fixture.Monster,
            Task09A2Fixture.Neutral, Task09A2Fixture.Ally
        };

        private static readonly string[] AttackSpecIds =
        {
            Task09A2Fixture.AttackSpecId,
            Task09A2Fixture.AlliedAttackSpecId,
            Task09A2Fixture.NeutralAttackSpecId
        };

        /// <summary>
        /// 与夹具定义<strong>逐字段同源</strong>的 ActionSpec 视图（掩码是唯一变量）。
        /// 它只用于"读取规格里的掩码"，不参与任何权威判定。
        /// </summary>
        private static ActionSpec BuildSpec(string specId)
        {
            TargetRelationMask mask;
            switch (specId)
            {
                case Task09A2Fixture.AttackSpecId: mask = TargetRelationMask.Hostile; break;
                case Task09A2Fixture.AlliedAttackSpecId:
                    mask = TargetRelationMask.Hostile | TargetRelationMask.Allied; break;
                case Task09A2Fixture.NeutralAttackSpecId:
                    mask = TargetRelationMask.Hostile | TargetRelationMask.Neutral; break;
                default: throw new AssertionException("夹具不变量：未知攻击规格 " + specId);
            }
            return new ActionSpec(
                Task09A2Fixture.Spec(specId), ActionType.Attack,
                new AttackTimingSpec(Task09A2Fixture.AttackWindupTicks, Task09A2Fixture.AttackRecoveryTicks),
                new AttackPayloadSpec(
                    null, ImpactProfiles.Blunt, 1f, TargetPolicy.PrimaryTargetOnly, mask,
                    MomentumDirectionOffsetSteps: 0, Pattern: Task09A2Fixture.AttackPattern,
                    Tags: AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable),
                AdrenalineCost: 0);
        }

        private static CommandRequest Edit(
            long targetTick, long revision, WindowId? window, params ScheduleEditOperation[] operations)
            => new CommandRequest(targetTick, new ScheduleEditScope(revision, window),
                new ScheduleEditPayload(operations));

        // =====================================================================
        // 必需测试 21：PlayerAndAiActionsUseSameProcessor
        // =====================================================================

        /// <summary>
        /// 玩家与 AI 各发行一条<strong>同形</strong>的普通动作排程命令，两条都经
        /// <strong>同一个</strong> <c>BattleCommandProcessor</c> 与<strong>同一个</strong>
        /// <c>ActionPlanCommandProcessor</c>（内部是唯一的 <c>ScheduleEditor</c> 事务）处理，
        /// 产出的计划形状逐字段相同。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：按来源种类分叉出第二条处理路径（某一侧被拒、或被另一套排程
        /// 写入）；控制权校验只对 AI 生效而放过玩家（或反之）；窗口端口与排程端口指向不同对象
        /// ⇒ "同一入口"不成立。
        /// </para>
        /// </summary>
        [Test]
        public void PlayerAndAiActionsUseSameProcessor()
        {
            // —— 世界 1：玩家发行（hero 攻击 mon）——
            var playerWorld = Task09A2Fixture.NewSim();
            playerWorld.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Hero, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(playerWorld, 0L);
            WindowId playerWindow = playerWorld.CurrentTurnWindow.WindowId;
            Task09A2Fixture.Submit(playerWorld, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, playerWorld.ScheduleAuthority.ScheduleRevision, playerWindow,
                Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId),
                Task09A2Fixture.Monster, 20L));
            StepResult playerStep = Task09A2Fixture.Step(playerWorld, 1L);

            // —— 世界 2：AI 发行（mon 攻击 hero）——
            var aiWorld = Task09A2Fixture.NewSim();
            aiWorld.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Monster, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(aiWorld, 0L);
            WindowId aiWindow = aiWorld.CurrentTurnWindow.WindowId;
            Task09A2Fixture.Submit(aiWorld, Task09A2Fixture.AiId, Task09A2Fixture.AddPlan(
                1L, aiWorld.ScheduleAuthority.ScheduleRevision, aiWindow,
                Task09A2Fixture.Monster, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId),
                Task09A2Fixture.Hero, 20L));
            StepResult aiStep = Task09A2Fixture.Step(aiWorld, 1L);

            Assert.That(Task09A2Fixture.AllRejectionCodes(playerStep), Is.Empty,
                "玩家的普通动作必须经统一入口被接受");
            Assert.That(Task09A2Fixture.AllRejectionCodes(aiStep), Is.Empty,
                "AI 的普通动作必须经统一入口被接受");

            // 结构事实：两侧都是同一类型的 BattleCommandProcessor，且排程端口与窗口端口
            // 指向**同一个** ActionPlanCommandProcessor（不存在第二套排程处理路径）。
            Assert.That(playerWorld.CommandProcessor.GetType(),
                Is.EqualTo(aiWorld.CommandProcessor.GetType()));
            Assert.That(playerWorld.CommandProcessor.ScheduleEditSink, Is.Not.Null);
            Assert.That(playerWorld.CommandProcessor.ScheduleEditSink.GetType(),
                Is.EqualTo(playerWorld.CommandProcessor.WindowSink.GetType()),
                "排程端口与窗口端口是同一种按载荷分派的适配器");
            Assert.That(playerWorld.CommandProcessor.ScheduleEditSink, Is.Not.SameAs(playerWorld.CommandProcessor.WindowSink),
                "两个端口必须是不同的端口对象（各自只接受自己的载荷判别）");
            Assert.That(((PayloadRoutedCommandPort)playerWorld.CommandProcessor.ScheduleEditSink).Kind,
                Is.EqualTo(CommandScopeKind.ScheduleEdit));
            Assert.That(((PayloadRoutedCommandPort)playerWorld.CommandProcessor.WindowSink).Kind,
                Is.EqualTo(CommandScopeKind.Window));
            Assert.That(((PayloadRoutedCommandPort)playerWorld.CommandProcessor.ScheduleEditSink).Target,
                Is.SameAs(((PayloadRoutedCommandPort)playerWorld.CommandProcessor.WindowSink).Target),
                "排程与窗口背后的权威实现必须是同一个 ActionPlanCommandProcessor 实例");
            Assert.That(playerWorld.CommandProcessor.ScheduleEditSink.GetType(),
                Is.EqualTo(aiWorld.CommandProcessor.ScheduleEditSink.GetType()));
            Assert.That(playerWorld.CommandProcessor.Authority.GetType(),
                Is.EqualTo(aiWorld.CommandProcessor.Authority.GetType()),
                "控制权校验玩家与 AI 共用同一实现");
            Assert.That(playerWorld.CommandProcessor.CommittedTransactionCount, Is.EqualTo(1));
            Assert.That(aiWorld.CommandProcessor.CommittedTransactionCount, Is.EqualTo(1));

            // 两侧计划形状逐字段相同（差异只在所有者/目标/窗口身份上）。
            ActionPlan playerPlan = playerWorld.ScheduleAuthority.Registry.ActivePlans.Single();
            ActionPlan aiPlan = aiWorld.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(playerPlan.ActionSpecId, Is.EqualTo(aiPlan.ActionSpecId));
            Assert.That(playerPlan.ActionType, Is.EqualTo(aiPlan.ActionType));
            Assert.That(playerPlan.StartTick, Is.EqualTo(aiPlan.StartTick));
            Assert.That(playerPlan.EndTick, Is.EqualTo(aiPlan.EndTick));
            Assert.That(playerPlan.ImpactTick, Is.EqualTo(aiPlan.ImpactTick));
            Assert.That(playerPlan.BudgetCostTicks, Is.EqualTo(aiPlan.BudgetCostTicks));
            Assert.That(playerPlan.ReservedTurnBudgetTicks, Is.EqualTo(aiPlan.ReservedTurnBudgetTicks));
            Assert.That(playerPlan.State, Is.EqualTo(aiPlan.State));

            // 两侧的窗口账本变化也相同（预算语义同源）。
            Assert.That(playerWorld.WindowManager.FindWindow(playerWindow).ReservedFor(playerPlan.ActionPlanId),
                Is.EqualTo(aiWorld.WindowManager.FindWindow(aiWindow).ReservedFor(aiPlan.ActionPlanId)));
        }

        // =====================================================================
        // 必需测试 28：PlayerCannotCommandAnotherControllersUnit
        // =====================================================================

        /// <summary>
        /// 玩家不能命令别人控制的单位：载荷里的单位 ID 只表达意图目标。
        /// 与"自己的单位"的对照只差一个 OwnerUnitId，得到的却是稳定拒绝 + 零局部写入。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把载荷里的单位 ID 当作权限依据（"提交方说他控制 mon"）；
        /// 控制权解析失败时跳过校验继续处理；拒绝时留下半提交（计划/预算/修订号任一变化）。
        /// </para>
        /// </summary>
        [Test]
        public void PlayerCannotCommandAnotherControllersUnit()
        {
            var sim = Task09A2Fixture.NewSim();
            sim.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Hero, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(sim, 0L);
            WindowId window = sim.CurrentTurnWindow.WindowId;
            long revision = sim.ScheduleAuthority.ScheduleRevision;

            // —— 对照 A：同一 scope、同一动作、同一目标，唯一差别是 OwnerUnitId = 自己的单位 ⇒ 接受 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, revision, window, Task09A2Fixture.Hero,
                Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Monster, 80L));
            StepResult ownUnit = Task09A2Fixture.Step(sim, 1L);
            Assert.That(Task09A2Fixture.AllRejectionCodes(ownUnit), Is.Empty,
                "对照：玩家命令自己的单位必须成功");

            ActionPlan heroPlan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(heroPlan.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Hero));
            Assert.That(heroPlan.IsEditable, Is.True, "夹具前提：hero 的计划仍可编辑");

            string scheduleBefore = Task09A2Fixture.ScheduleFingerprint(sim);
            long revisionBefore = sim.ScheduleAuthority.ScheduleRevision;

            // —— 被测：OwnerUnitId = 别人控制的单位 ⇒ COMMAND_ISSUER_CANNOT_CONTROL_UNIT ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                2L, revisionBefore, window, Task09A2Fixture.Monster,
                Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Hero, 60L));
            StepResult otherUnit = Task09A2Fixture.Step(sim, 2L);

            Assert.That(Task09A2Fixture.RejectionCodes(otherUnit).ToArray(),
                Is.EqualTo(new[] { CommandCodes.COMMAND_ISSUER_CANNOT_CONTROL_UNIT }),
                "载荷里的单位 ID 只表达意图目标，不证明权限");
            Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(scheduleBefore),
                "越权命令必须零局部写入");
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revisionBefore));

            // —— 对照 B：玩家移动**自己的**计划必须成功（同一入口、同一修订号语义）——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Edit(
                3L, revisionBefore, window,
                new MoveEditablePlanOperation(heroPlan.ActionPlanId, 90L)));
            StepResult ownMove = Task09A2Fixture.Step(sim, 3L);
            Assert.That(Task09A2Fixture.AllRejectionCodes(ownMove), Is.Empty,
                "对照：玩家移动自己的计划必须成功");
            Assert.That(heroPlan.StartTick, Is.EqualTo(90L));
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revisionBefore + 1L));
        }

        // =====================================================================
        // 必需测试 29：AiCannotCommandAnotherControllersUnit
        // =====================================================================

        /// <summary>
        /// AI 不能命令别人控制的单位：与玩家<strong>完全相同的</strong>拒绝码与零写入，
        /// 且"计划所有者"是 Move/Remove 的作用单位（用别人的计划 ID 同样被拦住）。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：AI 路径绕开 <c>CommandAuthority</c>（例如旧壳直接调
        /// <c>ScheduleEditor</c>），或者 AI 的"敌人列表"被当成权限来源；
        /// Move/Remove 只校验 Lane 而不校验计划所有者。
        /// </para>
        /// </summary>
        [Test]
        public void AiCannotCommandAnotherControllersUnit()
        {
            var sim = Task09A2Fixture.NewSim();

            // Tick 0：mon 自己的窗口；Tick 1 AI 建立一个属于自己的可编辑计划。
            sim.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Monster, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(sim, 0L);
            WindowId aiWindow = sim.CurrentTurnWindow.WindowId;
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, aiWindow, Task09A2Fixture.Monster,
                Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Hero, 80L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 1L)), Is.Empty,
                "夹具前提：AI 在自己的窗口里新增必须成功");
            ActionPlan aiPlan = sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(aiPlan.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Monster));
            Assert.That(aiPlan.IsEditable, Is.True);

            long revision = sim.ScheduleAuthority.ScheduleRevision;

            // —— 被测 1：AI 替玩家的单位新增 ⇒ 与玩家完全相同的稳定码 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Task09A2Fixture.AddPlan(
                2L, revision, aiWindow, Task09A2Fixture.Hero,
                Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Monster, 60L));
            StepResult impersonation = Task09A2Fixture.Step(sim, 2L);
            Assert.That(Task09A2Fixture.RejectionCodes(impersonation).ToArray(),
                Is.EqualTo(new[] { CommandCodes.COMMAND_ISSUER_CANNOT_CONTROL_UNIT }),
                "AI 与玩家必须有完全相同的控制权拒绝码");

            // Tick 2：AI 关掉自己的窗口，Tick 3 打开 hero 的窗口，Tick 4 hero 建立自己的计划。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                Task09A2Fixture.CloseWindow(3L, aiWindow));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 3L)), Is.Empty);
            Assert.That(sim.CurrentTurnWindow, Is.Null, "夹具前提：Tick 3 结束时 AI 的窗口已关闭");

            sim.WindowManager.ScheduleWindow(4L, Task09A2Fixture.Hero, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(sim, 4L);
            WindowId heroWindow = sim.CurrentTurnWindow.WindowId;
            Assert.That(sim.CurrentTurnWindow.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Hero));

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                5L, sim.ScheduleAuthority.ScheduleRevision, heroWindow, Task09A2Fixture.Hero,
                Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Monster, 150L));
            Assert.That(Task09A2Fixture.AllRejectionCodes(Task09A2Fixture.Step(sim, 5L)), Is.Empty,
                "夹具前提：hero 在自己的窗口里新增必须成功");

            ActionPlan heroPlan = sim.ScheduleAuthority.Registry.ActivePlans
                .Single(p => p.OwnerUnitId == Task09A2Fixture.Hero);
            long revisionAtSix = sim.ScheduleAuthority.ScheduleRevision;
            string scheduleBefore = Task09A2Fixture.ScheduleFingerprint(sim);

            // —— 被测 2：AI 用 hero 的计划 ID 做 Remove（计划所有者就是作用单位）——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Edit(
                6L, revisionAtSix, heroWindow,
                new RemoveEditablePlanOperation(heroPlan.ActionPlanId)));
            StepResult crossRemove = Task09A2Fixture.Step(sim, 6L);

            Assert.That(Task09A2Fixture.RejectionCodes(crossRemove).ToArray(),
                Is.EqualTo(new[] { CommandCodes.COMMAND_ISSUER_CANNOT_CONTROL_UNIT }),
                "AI 不得删除其他控制者的计划");

            // —— 被测 3：AI 用 hero 的计划 ID 做 Move ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, Edit(
                7L, revisionAtSix, heroWindow,
                new MoveEditablePlanOperation(heroPlan.ActionPlanId, 200L)));
            StepResult crossMove = Task09A2Fixture.Step(sim, 7L);

            Assert.That(Task09A2Fixture.RejectionCodes(crossMove).ToArray(),
                Is.EqualTo(new[] { CommandCodes.COMMAND_ISSUER_CANNOT_CONTROL_UNIT }),
                "AI 不得移动其他控制者的计划");
            Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(scheduleBefore),
                "越权命令必须零局部写入");
            Assert.That(heroPlan.IsTerminal, Is.False);
            Assert.That(heroPlan.StartTick, Is.EqualTo(150L));
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revisionAtSix));

            // —— 对照：AI 移动自己的计划必须成功 ——
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                Edit(8L, revisionAtSix, null, new MoveEditablePlanOperation(aiPlan.ActionPlanId, 300L)));
            Task09A2Fixture.Step(sim, 8L);
            Assert.That(aiPlan.StartTick, Is.EqualTo(300L), "对照：AI 移动自己的计划必须成功");
        }

        // =====================================================================
        // 追加装配端口（A2.1 回归修复引入）：不能变成绕过控制权的后门
        // =====================================================================

        /// <summary>
        /// <c>BattleSimulation.RegisterControllerBinding</c> 是<strong>唯一</strong>的控制权追加装配端口：
        /// 它写的是命令入口、窗口提交授权与审计视图读的<strong>同一份</strong>事实，且
        /// <strong>不能</strong>让装配方绕开任何既有判定。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：该端口只写窗口管理器（命令入口仍拒合法命令——A2.1 修复前的真实
        /// 漂移）；重复调用覆盖而不是并集（悄悄丢掉已登记的单位）；允许自封 <c>System</c> 来源；
        /// 允许一个单位挂两个控制者（审计字段取决于扫描顺序）；"登记了就等于能提交"（映射写错
        /// 依然要被拒绝）。
        /// </para>
        /// </summary>
        [Test]
        public void ControllerBindingRegistrationIsUnionAndCannotBypassAuthority()
        {
            var sim = Task09A2Fixture.NewSim();

            // 夹具前提：定义声明的两个绑定各自独立，hero 与 mon 分属两个控制者。
            Assert.That(sim.CommandAuthority.CanControl(Task09A2Fixture.PlayerId, Task09A2Fixture.Hero), Is.True);
            Assert.That(sim.CommandAuthority.CanControl(Task09A2Fixture.PlayerId, Task09A2Fixture.Monster), Is.False);

            // —— 并集语义：给玩家追加中立单位，原有 hero 不得丢；窗口授权同源同结果 ——
            sim.RegisterControllerBinding(Task09A2Fixture.PlayerId, new[] { Task09A2Fixture.Neutral });
            IReadOnlyList<UnitId> afterUnion =
                sim.CommandAuthority.ControlledUnitsOf(Task09A2Fixture.PlayerId);
            Assert.That(afterUnion.Select(u => u.Value).ToArray(),
                Is.EqualTo(new[] { Task09A2Fixture.Hero.Value, Task09A2Fixture.Neutral.Value }
                    .OrderBy(v => v).ToArray()),
                "重复登记必须是并集（按 UnitId 升序），绝不覆盖已登记的单位");
            Assert.That(sim.WindowManager.CanControl(Task09A2Fixture.PlayerId, Task09A2Fixture.Neutral),
                Is.True,
                "窗口的提交授权必须读同一份事实（否则两条授权面会漂移）");

            // —— 幂等：同一调用再来一次不产生重复项 ——
            sim.RegisterControllerBinding(Task09A2Fixture.PlayerId, new[] { Task09A2Fixture.Neutral });
            Assert.That(sim.CommandAuthority.ControlledUnitsOf(Task09A2Fixture.PlayerId).Count,
                Is.EqualTo(2));

            // —— 空集合不覆盖已登记的非空集合 ——
            sim.RegisterControllerBinding(Task09A2Fixture.PlayerId, Array.Empty<UnitId>());
            Assert.That(sim.CommandAuthority.CanControl(Task09A2Fixture.PlayerId, Task09A2Fixture.Hero), Is.True,
                "空集合是 no-op，不能把已有控制权清空");

            // —— 不可自封 System 来源 ——
            LogicDefinitionException systemSelfGrant = Assert.Throws<LogicDefinitionException>(() =>
                sim.RegisterControllerBinding(
                    new ControllerId(BattleSimulation.SystemControllerId),
                    new[] { Task09A2Fixture.Neutral }));
            Assert.That(systemSelfGrant.ErrorCode,
                Is.EqualTo(CommandCodes.EXTERNAL_INGRESS_SYSTEM_SOURCE_REJECTED),
                "外部不得把自己登记成 System 来源");

            // —— 一个单位不能有两个控制者：AI 也登记中立 ⇒ 下一个 Step 就稳定报矛盾 ——
            // （判据在阶段 8 的占用/冲突图投影里求值，因此不需要任何命令。）
            sim.RegisterControllerBinding(Task09A2Fixture.AiId, new[] { Task09A2Fixture.Neutral });
            LogicDefinitionException contradiction =
                Assert.Throws<LogicDefinitionException>(() => Task09A2Fixture.Step(sim, 0L));
            if (contradiction == null) Assert.Fail("一个单位挂两个控制者必须在 Step 时被拒绝");
            Assert.That(contradiction.ErrorCode,
                Is.EqualTo(SimulationCodes.STEP_OCCUPANCY_CONTROLLER_CONTRADICTION),
                "追加装配端口不能让一个单位获得两个控制者");
        }

        // =====================================================================
        // 必需测试 56：UiAndAiCandidateFiltersUseDecisionSnapshotFactionResolver
        // =====================================================================

        /// <summary>
        /// UI 与 AI 的候选筛面从<strong>同一个</strong>只读决策快照取<strong>同一个</strong>
        /// <c>IFactionRelationResolver</c> 实例；分类结果与命令层的放行判据逐对相同。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：UI 自己复制一份关系矩阵、或者某一侧按 Controller/玩家标志
        /// 推断敌我（分类不同）；候选筛面用 <c>Allows</c> 之外的第二套掩码语义；
        /// 命令层与筛面给出不同的拒绝原因。
        /// </para>
        /// </summary>
        [Test]
        public void UiAndAiCandidateFiltersUseDecisionSnapshotFactionResolver()
        {
            var observer = new Task09A2Fixture.SnapshotCapturingObserver();
            var assembly = new BattleSimulationAssembly(decisionObservers: new[] { (IDecisionObserver)observer });
            BattleSimulation sim = Task09A2Fixture.NewSim(assembly: assembly);
            Task09A2Fixture.Step(sim, 0L);

            Assert.That(observer.Snapshots.Count, Is.GreaterThan(0), "夹具前提：必须收到决策快照");
            DecisionSnapshot snapshot = observer.Snapshots[observer.Snapshots.Count - 1];
            Assert.That(sim.LastDecisionSnapshot, Is.SameAs(snapshot),
                "观察者与模拟读数必须是同一个快照实例");

            // 唯一的解析器实例（不是副本）。
            Assert.That(snapshot.FactionResolver, Is.SameAs(sim.FactionResolver),
                "决策快照必须持有整场唯一的阵营关系解析器实例");

            // UI 与 AI 各自的筛面 —— 两者都必须从同一个快照构造。
            TargetCandidateQuery uiQuery = TargetCandidateQuery.From(snapshot);
            TargetCandidateQuery aiQuery = TargetCandidateQuery.From(snapshot);

            Assert.That(uiQuery.Factions, Is.SameAs(aiQuery.Factions),
                "UI 与 AI 必须引用同一个解析器实例");
            Assert.That(uiQuery.Factions, Is.SameAs(snapshot.FactionResolver));
            Assert.That(sim.TargetCandidates.Factions, Is.SameAs(snapshot.FactionResolver),
                "模拟侧暴露的共用筛面也必须是同一个解析器");

            // 全单位对 × 全攻击掩码：UI 筛面、AI 筛面、快照与命令层判据四处必须一致。
            foreach (string specId in AttackSpecIds)
            {
                ActionSpec spec = BuildSpec(specId);
                foreach (UnitId owner in Universe)
                {
                    foreach (UnitId candidate in Universe)
                    {
                        UnitRelation uiRelation = uiQuery.Classify(owner, candidate);
                        Assert.That(aiQuery.Classify(owner, candidate), Is.EqualTo(uiRelation),
                            "两侧分类必须逐对相同");
                        Assert.That(snapshot.Classify(owner, candidate), Is.EqualTo(uiRelation),
                            "筛选面的分类必须就是快照解析器的分类");

                        bool uiEligible = uiQuery.IsEligible(spec, owner, candidate);
                        Assert.That(aiQuery.IsEligible(spec, owner, candidate), Is.EqualTo(uiEligible));
                        bool snapshotAllows = snapshot.Allows(
                            ((AttackPayloadSpec)spec.Payload).AllowedTargetRelations, owner, candidate);
                        Assert.That(uiEligible, Is.EqualTo(snapshotAllows),
                            "候选筛面必须与快照的 Allows 判定同源");
                        Assert.That(uiQuery.EligibilityOf(spec, owner, candidate) == null,
                            Is.EqualTo(snapshotAllows),
                            "筛面给出的稳定码必须与放行判据一致（不是第二套关系语义）");
                    }
                }
            }

            // 候选集合 = 逐个通过的单元（顺序按 UnitId 升序）。
            IReadOnlyList<UnitId> hostileOnly = uiQuery.CandidatesFor(
                BuildSpec(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Hero, Universe);
            Assert.That(hostileOnly.Select(u => u.Value).ToArray(), Is.EqualTo(new[] { 2L }),
                "Hostile 掩码下 hero 只能打 mon");
            Assert.That(aiQuery.CandidatesFor(BuildSpec(Task09A2Fixture.AttackSpecId),
                    Task09A2Fixture.Hero, Universe).Select(u => u.Value).ToArray(),
                Is.EqualTo(hostileOnly.Select(u => u.Value).ToArray()));

            // 命令层与筛面对同一个非法目标给同一个拒绝码。
            sim.WindowManager.ScheduleWindow(1L, Task09A2Fixture.Hero, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(sim, 1L);
            WindowId window = sim.CurrentTurnWindow.WindowId;

            Assert.That(uiQuery.EligibilityOf(BuildSpec(Task09A2Fixture.AttackSpecId),
                    Task09A2Fixture.Hero, Task09A2Fixture.Ally),
                Is.EqualTo(ScheduleCodes.TARGET_RELATION_NOT_ALLOWED),
                "展示面给出的拒绝原因必须与命令层同一个常量");

            string scheduleBefore = Task09A2Fixture.ScheduleFingerprint(sim);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                2L, sim.ScheduleAuthority.ScheduleRevision, window,
                Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId),
                Task09A2Fixture.Ally, 30L));
            StepResult rejected = Task09A2Fixture.Step(sim, 2L);
            Assert.That(Task09A2Fixture.RejectionCodes(rejected).ToArray(),
                Is.EqualTo(new[] { ScheduleCodes.TARGET_RELATION_NOT_ALLOWED }),
                "命令层对同一个非法目标必须给出同一个码");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.Zero);
            Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(scheduleBefore),
                "被拒绝的新增零局部写入");
        }

        // =====================================================================
        // 必需测试 57：ControllerKindChangeDoesNotChangeUiAiOrCommandTargetEligibility
        // =====================================================================

        /// <summary>
        /// 同一份定义、同一批单位，只把两个槽位的 <c>CommandSourceKind</c> 互换
        /// （hero 由 Player 变 Ai、mon 由 Ai 变 Player）：目标资格查询结果<strong>逐对不变</strong>，
        /// 命令层对同一非法目标的拒绝码也不变。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：任何把 <c>ControllerId</c>/<c>CommandSourceKind</c>/玩家标志
        /// 纳入敌我判定的实现（例如"Player 控制的都是友军"）；或者让候选筛面按调用方身份分叉。
        /// </para>
        /// </summary>
        [Test]
        public void ControllerKindChangeDoesNotChangeUiAiOrCommandTargetEligibility()
        {
            BattleDefinition normal = Task09A2Fixture.BuildDefinition(
                CommandSourceKind.Player, CommandSourceKind.Ai);
            BattleDefinition swapped = Task09A2Fixture.BuildDefinition(
                CommandSourceKind.Ai, CommandSourceKind.Player);

            string eligibilityNormal = EligibilityFingerprint(normal);
            string eligibilitySwapped = EligibilityFingerprint(swapped);

            Assert.That(eligibilitySwapped, Is.EqualTo(eligibilityNormal),
                "换一种来源种类不得改变任何单位对的分类或可用性");

            // 结构性证据：查询面不接受任何身份参数（"换 Controller 不改资格"在类型上成立）。
            foreach (System.Reflection.MethodInfo method in typeof(TargetCandidateQuery)
                         .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                foreach (System.Reflection.ParameterInfo parameter in method.GetParameters())
                {
                    string name = (parameter.Name ?? string.Empty).ToLowerInvariant();
                    string typeName = (parameter.ParameterType.Name ?? string.Empty).ToLowerInvariant();
                    Assert.That(name.Contains("controller") || name.Contains("sourcekind") || name.Contains("issuer"),
                        Is.False,
                        "TargetCandidateQuery." + method.Name + " 不得接受身份参数：" + parameter.Name);
                    Assert.That(typeName.Contains("controller") || typeName.Contains("sourcekind"),
                        Is.False,
                        "TargetCandidateQuery." + method.Name + " 不得接受身份类型：" + parameter.ParameterType.Name);
                }
            }

            // 命令层：同一条非法目标（Ally）在两种来源种类下得到同一个稳定码。
            string codeNormal = CommandLayerRejectionFor(normal, Task09A2Fixture.Ally);
            string codeSwapped = CommandLayerRejectionFor(swapped, Task09A2Fixture.Ally);
            Assert.That(codeNormal, Is.EqualTo(ScheduleCodes.TARGET_RELATION_NOT_ALLOWED));
            Assert.That(codeSwapped, Is.EqualTo(codeNormal));

            // 命令层：合法目标（Monster）在两种来源种类下都被接受。
            Assert.That(CommandLayerRejectionFor(normal, Task09A2Fixture.Monster), Is.Null);
            Assert.That(CommandLayerRejectionFor(swapped, Task09A2Fixture.Monster), Is.Null);
        }

        private static string EligibilityFingerprint(BattleDefinition definition)
        {
            BattleSimulation sim = Task09A2Fixture.NewSim(definition);
            var builder = new System.Text.StringBuilder();
            foreach (UnitId owner in Universe)
            {
                foreach (UnitId candidate in Universe)
                {
                    builder.Append(owner.Value).Append('>').Append(candidate.Value).Append('=')
                        .Append((int)sim.TargetCandidates.Classify(owner, candidate));
                    foreach (string specId in AttackSpecIds)
                    {
                        builder.Append('/').Append(
                            sim.TargetCandidates.EligibilityOf(BuildSpec(specId), owner, candidate) ?? "OK");
                    }
                    builder.Append(';');
                }
            }
            return builder.ToString();
        }

        /// <summary>在同一条命令路径上取得拒绝码；null = 被接受。</summary>
        private static string CommandLayerRejectionFor(BattleDefinition definition, UnitId target)
        {
            BattleSimulation sim = Task09A2Fixture.NewSim(definition);
            sim.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Hero, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(sim, 0L);
            WindowId window = sim.CurrentTurnWindow.WindowId;

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, window,
                Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId),
                target, 30L));
            StepResult result = Task09A2Fixture.Step(sim, 1L);

            IReadOnlyList<string> codes = Task09A2Fixture.AllRejectionCodes(result);
            return codes.Count == 0 ? null : codes[0];
        }

        // =====================================================================
        // 必需测试 58：FriendlyAndNeutralTargetsRequireExplicitActionMaskBits
        // =====================================================================

        /// <summary>
        /// Self/Allied/Neutral/Hostile 各占一个<strong>显式</strong>掩码位：只有置位的关系
        /// 才可能成为候选；友军与中立绝不因为"不是敌人"而被默认放行。
        /// 掩码本身非法（空/含未定义位）与"关系不在掩码内"是两个不同的稳定原因。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把"非敌对"实现成隐式放行（友军/中立可被选中）；
        /// 用"不等于 Hostile"之类的取反代替掩码位；把空掩码当成"全部允许"；
        /// 命令层不复用同一份掩码语义（筛面说不行、命令层却放行）。
        /// </para>
        /// </summary>
        [Test]
        public void FriendlyAndNeutralTargetsRequireExplicitActionMaskBits()
        {
            BattleSimulation sim = Task09A2Fixture.NewSim();
            TargetCandidateQuery query = sim.TargetCandidates;

            // 分类与掩码位映射。
            Assert.That(query.Classify(Task09A2Fixture.Hero, Task09A2Fixture.Hero), Is.EqualTo(UnitRelation.Self));
            Assert.That(query.Classify(Task09A2Fixture.Hero, Task09A2Fixture.Ally), Is.EqualTo(UnitRelation.Allied));
            Assert.That(query.Classify(Task09A2Fixture.Hero, Task09A2Fixture.Neutral), Is.EqualTo(UnitRelation.Neutral));
            Assert.That(query.Classify(Task09A2Fixture.Hero, Task09A2Fixture.Monster), Is.EqualTo(UnitRelation.Hostile));
            Assert.That(TargetCandidateQuery.MaskBitOf(UnitRelation.Self), Is.EqualTo(TargetRelationMask.Self));
            Assert.That(TargetCandidateQuery.MaskBitOf(UnitRelation.Allied), Is.EqualTo(TargetRelationMask.Allied));
            Assert.That(TargetCandidateQuery.MaskBitOf(UnitRelation.Neutral), Is.EqualTo(TargetRelationMask.Neutral));
            Assert.That(TargetCandidateQuery.MaskBitOf(UnitRelation.Hostile), Is.EqualTo(TargetRelationMask.Hostile));

            // 候选集合：掩码位是唯一开关。
            IReadOnlyList<UnitId> hostileOnly = query.CandidatesFor(
                BuildSpec(Task09A2Fixture.AttackSpecId), Task09A2Fixture.Hero, Universe);
            IReadOnlyList<UnitId> withAllied = query.CandidatesFor(
                BuildSpec(Task09A2Fixture.AlliedAttackSpecId), Task09A2Fixture.Hero, Universe);
            IReadOnlyList<UnitId> withNeutral = query.CandidatesFor(
                BuildSpec(Task09A2Fixture.NeutralAttackSpecId), Task09A2Fixture.Hero, Universe);

            Assert.That(hostileOnly.Select(u => u.Value).ToArray(),
                Is.EqualTo(new[] { Task09A2Fixture.Monster.Value }),
                "只置 Hostile 位 ⇒ 友军与中立都不可选");
            Assert.That(withAllied.Select(u => u.Value).ToArray(),
                Is.EqualTo(new[] { Task09A2Fixture.Monster.Value, Task09A2Fixture.Ally.Value }),
                "加上 Allied 位 ⇒ 友军可选，中立仍不可选");
            Assert.That(withNeutral.Select(u => u.Value).ToArray(),
                Is.EqualTo(new[] { Task09A2Fixture.Monster.Value, Task09A2Fixture.Neutral.Value }),
                "加上 Neutral 位 ⇒ 中立可选，友军仍不可选");
            Assert.That(query.IsEligible(BuildSpec(Task09A2Fixture.AttackSpecId),
                Task09A2Fixture.Hero, Task09A2Fixture.Hero), Is.False,
                "Self 位未置 ⇒ 不能选中自己");

            // 掩码非法与关系不允许是两个不同的稳定原因。
            ActionSpec emptyMask = new ActionSpec(
                Task09A2Fixture.Spec("action.t09a2.attack_empty"), ActionType.Attack,
                new AttackTimingSpec(Task09A2Fixture.AttackWindupTicks, Task09A2Fixture.AttackRecoveryTicks),
                new AttackPayloadSpec(
                    null, ImpactProfiles.Blunt, 1f, TargetPolicy.PrimaryTargetOnly,
                    TargetRelationMask.None, MomentumDirectionOffsetSteps: 0,
                    Pattern: Task09A2Fixture.AttackPattern, Tags: AttackTagMask.None),
                AdrenalineCost: 0);
            Assert.That(TargetRelationMasks.IsValid(TargetRelationMask.None), Is.False);
            Assert.That(query.EligibilityOf(emptyMask, Task09A2Fixture.Hero, Task09A2Fixture.Monster),
                Is.EqualTo(FactionCodes.TARGET_RELATION_MASK_INVALID),
                "空掩码是非法掩码，与关系不允许是两个不同的稳定原因");
            Assert.That(query.EligibilityOf(BuildSpec(Task09A2Fixture.AttackSpecId),
                    Task09A2Fixture.Hero, Task09A2Fixture.Ally),
                Is.EqualTo(ScheduleCodes.TARGET_RELATION_NOT_ALLOWED));

            // 命令层：友军/中立目标必须靠显式位放行（同一个掩码语义）。
            sim.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Hero, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(sim, 0L);
            WindowId window = sim.CurrentTurnWindow.WindowId;

            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, window,
                Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId),
                Task09A2Fixture.Ally, 30L, temporaryKey: 1L));
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, sim.ScheduleAuthority.ScheduleRevision, window,
                Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId),
                Task09A2Fixture.Neutral, 60L, temporaryKey: 2L));
            StepResult rejected = Task09A2Fixture.Step(sim, 1L);

            Assert.That(Task09A2Fixture.RejectionCodes(rejected).ToArray(),
                Is.EqualTo(new[]
                {
                    ScheduleCodes.TARGET_RELATION_NOT_ALLOWED, ScheduleCodes.TARGET_RELATION_NOT_ALLOWED
                }),
                "友军与中立在 Hostile 掩码下都必须被拒绝");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.Zero);

            // 显式位置位后必须可以通过命令层（否则"显式位"就没有执行力）。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                2L, sim.ScheduleAuthority.ScheduleRevision, window,
                Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AlliedAttackSpecId),
                Task09A2Fixture.Ally, 90L, temporaryKey: 3L));
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                2L, sim.ScheduleAuthority.ScheduleRevision, window,
                Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.NeutralAttackSpecId),
                Task09A2Fixture.Neutral, 120L, temporaryKey: 4L));
            StepResult accepted = Task09A2Fixture.Step(sim, 2L);

            Assert.That(Task09A2Fixture.RejectionCodes(accepted), Is.Empty,
                "显式置位后友军与中立目标必须可被选中");
            long[] acceptedTargets = sim.ScheduleAuthority.Registry.ActivePlans
                .Select(p => p.PrimaryTargetUnitId.Value.Value).OrderBy(v => v).ToArray();
            Assert.That(acceptedTargets,
                Is.EqualTo(new[] { Task09A2Fixture.Ally.Value, Task09A2Fixture.Neutral.Value }
                    .OrderBy(v => v).ToArray()),
                "放行的必须恰好是显式置位的那两个目标");
        }
    }
}
