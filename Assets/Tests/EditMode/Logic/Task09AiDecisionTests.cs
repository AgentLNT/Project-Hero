using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 09 · A3 轮（A 流最后一轮）：<strong>AI 控制器、提交权限边界与候选排序不变性</strong>
    /// （任务包「必须产出」9/10）。
    ///
    /// 逐条对应必需测试：<c>AiDoesNotSchedulePlanOrCloseWindowDirectly</c>、
    /// <c>AiCannotCreateOpportunityOrReactionPlanDirectly</c>、<c>AiCanReactDuringAnotherUnitsWindow</c>、
    /// <c>PlayerAndAiShareOneTickReactionIngressLead</c>、<c>AiCannotReactToOpportunityInItsOpeningTick</c>、
    /// <c>AiAndPlayerAvailabilityUsesSameActionSetRules</c>、
    /// <c>AiReactionCandidatePermutationDoesNotChangeDecisionWithSameSeed</c>、
    /// <c>AiCandidatePermutationDoesNotChangeDecisionWithSameSeed</c>。
    ///
    /// <para>
    /// 全部用例都走<strong>真实定义 → 真实 <see cref="BattleSimulation"/> → 阶段 18 既有观察接缝
    /// → 真实命令入口 → 真实 <c>BattleCommandProcessor</c></strong>：
    /// AI 的每一条产出都由 <see cref="AiControllerLogic"/> 经它自己注册的入口提交，
    /// 没有任何一条用例直接调用 <c>ReactionOpportunitySystem.TryAcceptById</c> 或其他旁路。
    /// </para>
    /// </summary>
    public class Task09AiDecisionTests
    {
        private const CommandSourceKind Player = CommandSourceKind.Player;
        private const CommandSourceKind Ai = CommandSourceKind.Ai;

        private static ActionSpecId Spec(string id) => Task09A2Fixture.Spec(id);
        private static ActionSpecId AttackSpec() => Spec(Task09A2Fixture.AttackSpecId);

        /// <summary>
        /// A3 轮的真实装配：同一个 <see cref="AiControllerLogic"/> 既充当
        /// <c>IDecisionObserver</c>（阶段 18 的既有接缝）又充当 <c>IAiRuntimeStateSource</c>
        /// （快照里的 AI 未来状态来源），因此"AI 决策"与"快照里的 AI 状态"读的是同一个对象。
        /// </summary>
        private static BattleSimulation NewAiSim(
            BattleDefinition definition,
            out AiControllerLogic ai,
            out List<DecisionSnapshot> delivered,
            out List<CommandIngressRejection> ingressRejections)
        {
            AiControllerLogic controller = new AiControllerLogic(Task09A2Fixture.Inputs.InitialRngSeed);
            controller.RegisterController(AiBindingOf(definition));

            var observations = new List<DecisionSnapshot>();
            var observer = new ForwardingObserver(controller, observations);
            var assembly = new BattleSimulationAssembly(
                decisionObservers: new IDecisionObserver[] { observer },
                aiRuntimeStates: controller);

            BattleSimulation sim = Task09A2Fixture.NewSim(definition, assembly);
            // 端口用本场真实现接线（观察面 vs 已公开机会；只读检索面 vs 权威注册表）：
            // 这也是生产装配口径，因此用例观察到的就是真实路径。
            controller.AttachPorts(sim.AiReactionOpportunities, sim.AiActionPlanLookup, sim.MovementPathCalculator);
            ai = controller;
            delivered = observations;
            ingressRejections = new List<CommandIngressRejection>(controller.IngressRejections);
            return sim;
        }

        /// <summary>取定义里唯一的 AI 绑定（注册入口<strong>只</strong>接受已验证绑定，不接受自报身份）。</summary>
        private static ControllerBinding AiBindingOf(BattleDefinition definition)
            => ControllerBindingOf(definition, CommandSourceKind.Ai);

        /// <summary>取定义里第一条指定来源种类的绑定（唯一装配来源 = 已验证定义）。</summary>
        private static ControllerBinding ControllerBindingOf(
            BattleDefinition definition, CommandSourceKind sourceKind)
        {
            EncounterDefinition encounter = definition.FindEncounter(Task09A2Fixture.EncounterId);
            Assert.That(encounter, Is.Not.Null, "夹具前提：定义里必须有该 Encounter");
            foreach (ControllerBinding binding in encounter.Controllers)
            {
                if (binding.SourceKind == sourceKind) return binding;
            }
            Assert.Fail("夹具前提：定义里必须有一条 " + sourceKind + " 绑定");
            return null;
        }

        /// <summary>
        /// 把 AI 的过滤视图原样转发给同一个 <see cref="AiControllerLogic"/>，并记录投递内容与入口拒绝。
        /// 它<strong>不是</strong>被测实现：AI 的决策与提交全部发生在 <c>AiControllerLogic</c> 内部。
        /// </summary>
        private sealed class ForwardingObserver : IDecisionObserver
        {
            private readonly AiControllerLogic _controller;
            private readonly List<DecisionSnapshot> _observed;
            private readonly List<CommandIngressRejection> _rejections = new List<CommandIngressRejection>();

            public ForwardingObserver(AiControllerLogic controller, List<DecisionSnapshot> observed)
            {
                _controller = controller;
                _observed = observed;
            }

            public IReadOnlyList<CommandIngressRejection> Rejections => _rejections;

            public ControllerId ObserverControllerId(DecisionSnapshot snapshot)
                => _controller.ObserverControllerId(snapshot);

            public void ObserveOrdered(DecisionSnapshot snapshot, CommandIngressRegistry ingress, long nextTick)
            {
                _observed.Add(snapshot);
                int before = _controller.IngressRejections.Count;
                _controller.ObserveOrdered(snapshot, ingress, nextTick);
                for (int i = before; i < _controller.IngressRejections.Count; i++)
                {
                    _rejections.Add(_controller.IngressRejections[i]);
                }
            }
        }

        /// <summary>按 ID 重新读取权威机会对象（保持引用，观察它随 Tick 推进的状态变化）。</summary>
        private static ReactionOpportunityRuntime ReReadOpportunity(
            BattleSimulation sim, ReactionOpportunityId opportunityId)
        {
            ReactionOpportunityRuntime runtime = sim.ReactionOpportunities.FindOpportunity(opportunityId);
            Assert.That(runtime, Is.Not.Null, "夹具前提：机会必须仍在本场活动记录里");
            return runtime;
        }

        private static void StepTo(BattleSimulation sim, long tick)
        {
            for (long t = sim.Tick + 1L; t <= tick; t++) Task09A2Fixture.Step(sim, t);
        }

        private static IReadOnlyList<ActionPlan> NonTerminalPlans(BattleSimulation sim)
            => sim.ScheduleAuthority.Registry.ActivePlans;

        private static int ReactionPlanCount(BattleSimulation sim)
            => NonTerminalPlans(sim).Count(p => p.IsReaction);

        // =====================================================================
        // 必需测试 31：AiDoesNotSchedulePlanOrCloseWindowDirectly
        // =====================================================================

        /// <summary>
        /// AI 层在<strong>实现上</strong>完全不依赖排程写入面与窗口写方法：IL 里不存在对
        /// <c>ScheduleEditor</c>、<c>ActionScheduleAuthority</c>、<c>ActionPlanFactory</c>、
        /// <c>ActionPlanTerminalCoordinator</c>、<c>TurnWindowManager</c>、
        /// <c>TurnWindowBudgetAuthority</c>/<c>ActionScheduler</c> 与
        /// <c>UnityEngine.Random</c>/<c>UnityEngine.Time</c> 的任何引用；
        /// 行为上，AI 决策之后排程增量<strong>只</strong>来自命令事务，且没有任何窗口被打开或关闭。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：AI 直接调 <c>ScheduleEditor</c> 排计划、直接调
        /// <c>TurnWindowManager.ScheduleWindow</c>/<c>RequestClose</c> 开窗关窗、
        /// 或引用 <c>UnityEngine.Random</c>/<c>Time</c> 取随机与时间。
        /// </para>
        /// </summary>
        [Test]
        public void AiDoesNotSchedulePlanOrCloseWindowDirectly()
        {
            // —— 结构面：AI 命名空间的 IL 里不得出现任何写入面或引擎随机/时间类型 ——
            IReadOnlyList<MethodReference> references = AiIlAudit.AllMethodReferences();
            Assert.That(references.Count, Is.GreaterThan(0),
                "扫描前提：AI 命名空间必须至少有一个方法体（否则扫描等于空转）");

            foreach (string forbidden in AiIlAudit.ForbiddenTypeNames)
            {
                MethodReference hit = references.FirstOrDefault(r => AiIlAudit.IsOfType(r, forbidden));
                Assert.That(hit, Is.Null,
                    "AI 层不得引用 " + forbidden + "；实测命中 " + (hit == null ? "<none>" : hit.ToString()));
            }

            // —— 行为面：AI 拥有**自己**的窗口，因此它必须在窗口里提交并只经事务写入 ——
            var observations = new List<DecisionSnapshot>();
            AiControllerLogic controller = new AiControllerLogic(Task09A2Fixture.Inputs.InitialRngSeed);
            BattleDefinition definition = Task09A2Fixture.BuildDefinition(Player, Ai);
            controller.RegisterController(AiBindingOf(definition));
            var forwarding = new ForwardingObserver(controller, observations);
            var assembly = new BattleSimulationAssembly(
                decisionObservers: new IDecisionObserver[] { forwarding },
                aiRuntimeStates: controller);
            BattleSimulation sim = Task09A2Fixture.NewSim(definition, assembly);

            // 窗口属于 **AI 的**单位（mon）：AI 因此拥有当前窗口的提交权限。
            sim.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Monster, Task09A2Fixture.WindowBudget);
            StepTo(sim, 0L);
            Assert.That(observations.Count, Is.EqualTo(1), "夹具前提：Tick 0 必须投递一次决策快照");
            Assert.That(controller.RegisteredControllerIds.Count, Is.EqualTo(1), "夹具前提：恰好一个 AI 控制者");
            Assert.That(controller.BuildContext(sim.LastDecisionSnapshot, Task09A2Fixture.AiId)
                    .HasSubmissionWindow,
                Is.True, "夹具前提：AI 在 Tick 0 拥有仍接收提交的窗口");

            long revisionAfterTickZero = sim.ScheduleAuthority.ScheduleRevision;
            int closedWindowsAfterTickZero = sim.WindowManager.ClosedWindows.Count;
            int opportunitiesAfterTickZero = sim.ReactionOpportunities.ActiveOpportunities.Count;

            StepTo(sim, 1L);

            // AI 在 Tick 0 决策、Tick 1 生效：mon 对 hero 的合法攻击**确实**被提交并被接受。
            Assert.That(forwarding.Rejections, Is.Empty,
                "AI 的合法提交不得被入口拒绝；实测=" + DescribeRejections(forwarding.Rejections));
            Assert.That(NonTerminalPlans(sim).Count, Is.EqualTo(1),
                "夹具前提：本世界只有 AI 的这一条新增计划（排程写入只经事务发生）");
            ActionPlan aiPlan = NonTerminalPlans(sim)[0];
            Assert.That(aiPlan.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Monster),
                "计划所有者必须是 AI 控制者自己的单位");
            Assert.That(aiPlan.SubmittedWindowId.HasValue, Is.True,
                "新增计划的窗口归属由事务记录（不是 AI 自己写窗口账本）");
            Assert.That(aiPlan.SubmittedWindowId.Value.Value, Is.EqualTo(sim.CurrentTurnWindow.WindowId.Value),
                "计划的窗口归属必须等于权威窗口 ID（AI 无法自报窗口身份）");

            // 排程修订号在 AI 决策 + 提交之后只前进一次：
            // 它证明"排程写入只发生在命令事务里"，而不是 AI 在观察回调里直接写排程。
            Assert.That(sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revisionAfterTickZero + 1L),
                "AI 的一条排程命令必须恰好令修订号 +1（没有第二次隐式写入）");

            // 窗口面：AI 既没有开窗，也没有关窗——窗口状态逐项不变。
            Assert.That(sim.CurrentTurnWindow, Is.Not.Null, "AI 不得直接关闭窗口");
            Assert.That(sim.CurrentTurnWindow.IsOpen, Is.True, "AI 不得直接改窗口位");
            Assert.That(sim.WindowManager.ClosedWindows.Count, Is.EqualTo(closedWindowsAfterTickZero),
                "AI 未提交关窗命令时不得出现新的已关闭窗口（直接关窗的痕迹）");
            Assert.That(sim.WindowManager.CurrentWindow.WindowId.Value,
                Is.EqualTo(aiPlan.SubmittedWindowId.Value.Value),
                "当前窗口仍是 AI 提交时的那一个（AI 没有另开窗口）");

            // 机会面：AI 的**唯一**影响路径是它的计划；任何新增机会都必须挂在那条计划上，
            // 因此"AI 直接创建机会"被结构性排除（机会的来源计划 ID 就是它）。
            IReadOnlyList<ReactionOpportunityRuntime> live = sim.ReactionOpportunities.ActiveOpportunities;
            for (int i = 0; i < live.Count; i++)
            {
                Assert.That(live[i].SourceAttackPlanId.Value, Is.EqualTo(aiPlan.ActionPlanId.Value),
                    "本案中出现的任何机会都只能由 AI 自己的那条攻击计划派生（不是 AI 直接创建）");
            }
            Assert.That(live.Count, Is.EqualTo(opportunitiesAfterTickZero + 1),
                "AI 的攻击派生**恰好一条**机会；AI 不得凭空多造或销毁机会");
        }

        // =====================================================================
        // 必需测试 32：AiCannotCreateOpportunityOrReactionPlanDirectly
        // =====================================================================

        /// <summary>
        /// AI 只能产出标准 <c>ReactionCommand</c>：IL 里不存在对 <c>ReactionOpportunitySystem</c>、
        /// <c>ReactionPlanner</c>、<c>ReactionCommandPlanner</c> 的任何引用
        /// （<c>TryAcceptById</c> 与"造一条反应计划"在类型上不可达）；
        /// 行为上，AI 的反应成功之后，反应计划与机会的绑定由<strong>本场唯一</strong>机会系统写入。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：AI 直接调 <c>ReactionOpportunitySystem.TryAcceptById</c>/
        /// <c>TryAcceptByIdWithCommandSequence</c>，或自己 <c>new ActionPlan</c>/调
        /// <c>ActionPlanFactory</c> 造一条反应计划（于是 <c>ReactionOpportunityId</c> 绑定缺失
        /// 或机会计数被凭空改变）。
        /// </para>
        /// </summary>
        [Test]
        public void AiCannotCreateOpportunityOrReactionPlanDirectly()
        {
            IReadOnlyList<MethodReference> references = AiIlAudit.AllMethodReferences();
            foreach (string forbidden in new[]
                     {
                         "ProjectHero.Logic.Timeline.ReactionOpportunitySystem",
                         "ProjectHero.Logic.Timeline.ReactionPlanner",
                         "ProjectHero.Logic.Timeline.ReactionCommandPlanner"
                     })
            {
                MethodReference hit = references.FirstOrDefault(r => AiIlAudit.IsOfType(r, forbidden));
                Assert.That(hit, Is.Null,
                    "AI 层不得引用 " + forbidden + "；实测命中 " + (hit == null ? "<none>" : hit.ToString()));
            }

            // —— 行为面：真实机会 + 真实额度 + 真实入口 ——
            BattleDefinition definition = Task09A2Fixture.BuildDefinition(Player, Ai);
            var sim = NewAiSim(definition, out AiControllerLogic ai,
                out List<DecisionSnapshot> delivered, out List<CommandIngressRejection> ingressRejections);

            Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);
            Task09A2Fixture.SeedAdrenaline(sim, Task09A2Fixture.Monster, FrozenDesignValues.BlockAdrenalineCost);

            ReactionOpportunityRuntime opportunity =
                Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);
            opportunity = ReReadOpportunity(sim, opportunity.Id);
            int opportunityCountBefore = sim.ReactionOpportunities.ActiveOpportunities.Count;

            StepTo(sim, 2L);
            StepTo(sim, 3L);
            StepTo(sim, 4L);

            Assert.That(ingressRejections, Is.Empty,
                "AI 的反应命令必须被入口接受；实测=" + DescribeRejections(ingressRejections));
            Assert.That(delivered.Count, Is.EqualTo(5), "夹具前提：Tick 0..4 各投递一次快照");

            var trace = new System.Text.StringBuilder();
            for (long t = 0L; t <= 5L; t++)
            {
                AiDecision d = ai.DecisionAt(t);
                trace.Append(" [t").Append(t).Append(":sel=")
                    .Append(d == null ? "<nod>" : Describe(d.Selected))
                    .Append(":rc=").Append(d == null ? -1 : d.ReactionCandidateCount)
                    .Append(']');
            }

            // —— 反假绿：AI 真的产出了**一条**标准反应命令（不是"什么都没做"）——
            AiDecision reactionDecision = ai.Decisions.FirstOrDefault(d => d.Selected != null);
            Assert.That(reactionDecision, Is.Not.Null, "AI 必须真正提出过命令；diag=" + trace);
            Assert.That(reactionDecision.Selected.Scope, Is.InstanceOf<ReactionCommandScope>(),
                "AI 的产出只能是一条 Reaction scope 的标准命令");
            Assert.That(reactionDecision.ReactionCandidateCount, Is.EqualTo(2),
                "候选只能来自已公开机会的 Block 与 Dodge（机会系统不会为 AI 造第二套选项）；tick=" +
                reactionDecision.DecisionTick + " diag=" + trace);

            Assert.That(ReactionPlanCount(sim), Is.EqualTo(1),
                "AI 必须建出恰好一条反应计划——但它只能经处理器 → ReactionPlanner 建；diag=" + trace +
                " opportunity=" + opportunity.State + "/" + opportunity.CloseReason +
                " plans=" + string.Join(";", NonTerminalPlans(sim).Select(p =>
                    p.ActionPlanId.Value + ":" + p.ActionSpecId.Value + ":" + p.State + ":r" + p.IsReaction)));
            Assert.That(sim.ReactionOpportunities.ActiveOpportunities.Count, Is.EqualTo(opportunityCountBefore),
                "AI 不得创建（也不得销毁）任何机会");

            ActionPlan reaction = NonTerminalPlans(sim).Single(p => p.IsReaction);
            Assert.That(reaction.ReactionOpportunityId.HasValue, Is.True);
            Assert.That(reaction.ReactionOpportunityId.Value, Is.EqualTo(opportunity.Id),
                "反应计划必须绑定**已公开的那一条**机会，而不是 AI 自造的来源");
            Assert.That(opportunity.BoundActionPlanId.Value, Is.EqualTo(reaction.ActionPlanId.Value),
                "机会与计划必须互相绑定（由同一个权威机会系统写入）");
            Assert.That(reaction.ReservedTurnBudgetTicks, Is.Zero,
                "反应计划不参与窗口预算（08 交接 §4.5 冻结口径）");
        }

        // =====================================================================
        // 必需测试 33：AiCanReactDuringAnotherUnitsWindow
        // =====================================================================

        /// <summary>
        /// 反应权与窗口<strong>正交</strong>：当前打开的窗口属于玩家控制的 hero，
        /// AI（mon）仍在 hero 的窗口期间提出 Block 并被接受；
        /// 而且 AI 自己的决策上下文里<strong>没有</strong>任何窗口。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：在 AI 的反应路径（候选构建或提交前置）里加
        /// "必须有自己拥有的窗口"这一条（于是 AI 一条反应都提不出），
        /// 或把 AI 的反应候选来源接到"自己的窗口/当前窗口"而不是"已公开机会"。
        /// </para>
        /// </summary>
        [Test]
        public void AiCanReactDuringAnotherUnitsWindow()
        {
            BattleDefinition definition = Task09A2Fixture.BuildDefinition(Player, Ai);
            var sim = NewAiSim(definition, out AiControllerLogic ai,
                out List<DecisionSnapshot> delivered, out List<CommandIngressRejection> ingressRejections);

            Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);
            Task09A2Fixture.SeedAdrenaline(sim, Task09A2Fixture.Monster, FrozenDesignValues.BlockAdrenalineCost);

            TurnWindow window = sim.CurrentTurnWindow;
            Assert.That(window, Is.Not.Null, "夹具前提：英雄的窗口必须打开");
            Assert.That(window.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Hero),
                "夹具前提：窗口属于**玩家控制的 hero**，不是 AI 的窗口");
            Assert.That(sim.CommandAuthority.ControlledUnitsOf(Task09A2Fixture.AiId).Count, Is.GreaterThan(0),
                "夹具前提：AI 确实控制着单位");

            StepTo(sim, 2L);
            StepTo(sim, 3L);

            var d2 = new System.Text.StringBuilder();
            for (long t = 0L; t <= 3L; t++)
            {
                AiDecision d = ai.DecisionAt(t);
                d2.Append(" [t").Append(t).Append(":sel=").Append(d == null ? "<nod>" : Describe(d.Selected))
                  .Append(":rc=").Append(d == null ? -1 : d.ReactionCandidateCount)
                  .Append(":rev=").Append(sim.ScheduleAuthority.ScheduleRevision).Append(']');
            }
            d2.Append(" rev=").Append(sim.ScheduleAuthority.ScheduleRevision)
              .Append(" heroPlan=").Append(sim.ScheduleAuthority.Registry.ActivePlans.Count)
              .Append(" adr=").Append(sim.AdrenalineLedgerOf(Task09A2Fixture.Monster)?.AvailableAdrenaline ?? -1)
              .Append(" opps=").Append(string.Join(";", sim.ReactionOpportunities.ActiveOpportunities.Select(o =>
                  o.Id.Value + ":" + o.State + ":bound" + o.BoundActionPlanId.Value + ":" + o.CloseReason)));

            Assert.That(ReactionPlanCount(sim), Is.EqualTo(1),
                "AI 必须在玩家窗口期间成功建立反应计划；diag=" + d2 + " opp=" +
                sim.ReactionOpportunities.ActiveOpportunities.Count);
            Assert.That(ingressRejections, Is.Empty,
                "他人窗口期间 AI 的反应命令不得被入口拒绝；实测=" + DescribeRejections(ingressRejections));

            // AI 的决策确实发生在 hero 的窗口**打开期间**，而它自己从未拥有窗口。
            DecisionSnapshot atReaction = delivered.Last(s => s.Tick == 1L);
            AiDecisionContext context = ai.BuildContext(atReaction, Task09A2Fixture.AiId);
            Assert.That(context.OwnWindow, Is.Null, "AI 自己没有任何窗口");
            Assert.That(context.HasSubmissionWindow, Is.False,
                "因此任何\"必须有自己的窗口才能反应\"的实现都会在本用例里红；diag=" + d2);
            Assert.That(ai.DecisionAt(1L).ReactionCandidateCount, Is.EqualTo(2),
                "在该窗口打开期间，AI 必须已经拿到两条反应候选（来源只能是已公开机会）");
            Assert.That(ai.DecisionAt(1L).Selected, Is.Not.Null, "AI 必须在该窗口期间提出反应命令");
            Assert.That(ai.DecisionAt(1L).Selected.TargetTick, Is.EqualTo(2L));
            Assert.That(ai.DecisionAt(1L).Selected.Scope, Is.InstanceOf<ReactionCommandScope>());
            Assert.That(window.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Hero),
                "整个过程中窗口始终属于 hero，AI 从未拥有窗口");
            _ = ingressRejections;
        }

        // =====================================================================
        // 必需测试 34：PlayerAndAiShareOneTickReactionIngressLead
        // =====================================================================

        /// <summary>
        /// 玩家与 AI 的入口提前量是<strong>同一个值</strong>（A1 冻结为 1）：
        /// 同一条 <c>CommandIngressLeadTicks</c> 常量、同一个
        /// <c>NextDefaultTargetTick = FrozenThroughTick + CommandIngressLeadTicks</c> 口径；
        /// 而且默认投递入口<strong>没有</strong> <c>long tick</c> 参数，
        /// 因此"为 AI 单独加提前量"在类型上就不可能。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：为 AI 单独加提前量（例如"AI 提前两 Tick 决策"），
        /// 或把默认投递目标写成"当前 Tick"而不是"已冻结 Tick + 1"。
        /// </para>
        /// </summary>
        [Test]
        public void PlayerAndAiShareOneTickReactionIngressLead()
        {
            BattleDefinition definition = Task09A2Fixture.BuildDefinition(Player, Ai);
            var sim = NewAiSim(definition, out AiControllerLogic ai,
                out List<DecisionSnapshot> delivered, out List<CommandIngressRejection> ingressRejections);
            StepTo(sim, 0L);

            Assert.That(CommandIngressRegistry.CommandIngressLeadTicks, Is.EqualTo(1),
                "A1 冻结口径：提前量恒为 1");

            CommandIngressEntry playerEntry = Task09A2Fixture.PlayerEntry(sim);
            CommandIngressEntry aiEntry = Task09A2Fixture.AiEntry(sim);
            Assert.That(aiEntry.SourceKind, Is.EqualTo(CommandSourceKind.Ai));
            Assert.That(playerEntry.SourceKind, Is.EqualTo(CommandSourceKind.Player));
            Assert.That(aiEntry.SourcePriority, Is.EqualTo(CommandSourcePriority.Ai));
            Assert.That(playerEntry.SourcePriority, Is.EqualTo(CommandSourcePriority.Player));

            // 窗口属于 AI 的 mon ⇒ AI 每个 Tick 都有合法窗口，可逐 Tick 决策。
            sim.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Monster, Task09A2Fixture.WindowBudget);

            // 逐 Tick 断言：同一个冻结水位上，两个入口读的是同一条默认目标 Tick 公式。
            for (long frozen = 0L; frozen <= 3L; frozen++)
            {
                if (frozen > 0L) StepTo(sim, frozen);
                Assert.That(sim.CommandIngress.FrozenThroughTick, Is.EqualTo(frozen),
                    "夹具前提：Tick " + frozen + " 已冻结");
                Assert.That(sim.CommandIngress.NextDefaultTargetTick,
                    Is.EqualTo(sim.CommandIngress.FrozenThroughTick + CommandIngressRegistry.CommandIngressLeadTicks),
                    "玩家与 AI 共用同一条默认目标 Tick 公式");
                Assert.That(delivered.Count, Is.EqualTo((int)frozen + 1),
                    "Tick " + frozen + " 必须向 AI 投递一次快照；delivered=" +
                    string.Join(",", delivered.Select(s => s.Tick)) + " decisions=" +
                    string.Join(",", ai.Decisions.Select(d => d.DecisionTick)) + " state=" +
                    ai.RuntimeStateOf(Task09A2Fixture.AiId));
                Assert.That(ai.Decisions.Count, Is.EqualTo((int)frozen + 1),
                    "Tick " + frozen + " 必须做出一次决策（逐 Tick 决策）；delivered=" +
                    string.Join(",", delivered.Select(s => s.Tick)) + " decisions=" +
                    string.Join(",", ai.Decisions.Select(d => d.DecisionTick)) + " state=" +
                    ai.RuntimeStateOf(Task09A2Fixture.AiId));
            }

            // 结构性事实：默认投递路径只有一条实现，且它不接受调用方声明的 Tick。
            MethodInfo submitDefault = typeof(CommandIngressEntry)
                .GetMethod(nameof(CommandIngressEntry.SubmitAtDefaultTick));
            Assert.That(submitDefault, Is.Not.Null);
            Assert.That(submitDefault.GetParameters().Length, Is.EqualTo(2),
                "默认投递只接受 (scope, payload)：生产者无法声明目标 Tick");
            Assert.That(submitDefault.GetParameters().Any(p => p.ParameterType == typeof(long)), Is.False,
                "默认投递路径上不存在 long tick 参数，因此不可能有第二套提前量");

            Assert.That(ingressRejections, Is.Empty,
                "AI 的合法提交不得被入口拒绝；实测=" + DescribeRejections(ingressRejections));
            Assert.That(ai.Decisions.Count, Is.EqualTo(4), "Tick 0..3 必须逐 Tick 决策一次");
            for (int i = 0; i < delivered.Count; i++)
            {
                AiDecision decision = ai.Decisions[i];
                if (decision.Selected == null) continue;
                Assert.That(decision.Selected.TargetTick, Is.EqualTo(delivered[i].Tick + 1L),
                    "每条 AI 命令的目标 Tick 必须恰好是 nextTick（同一入口提前量）");
            }
        }

        // =====================================================================
        // 必需测试 35：AiCannotReactToOpportunityInItsOpeningTick
        // =====================================================================

        /// <summary>
        /// AI 只能对<strong>快照里已经公开</strong>的机会反应，而且命令只能投到<strong>下一 Tick</strong>。
        /// 攻击在 Tick 1 提交 ⇒ 机会在 Tick 1 公开；AI 在 Tick 1 的快照里才第一次看到它，
        /// 但产出的命令目标 Tick 恒为 <c>快照 Tick + 1</c>，因此"同 Tick 抢跑"在结构上不可能。
        /// 三段证据：
        /// <list type="number">
        /// <item><strong>机会出现之前</strong>（Tick 0）AI 的反应候选恰好为 0；</item>
        /// <item><strong>机会公开 Tick</strong>（Tick 1）候选出现（2 条）且选中的命令目标 Tick = 2；</item>
        /// <item><strong>Tick 2</strong> 的命令阶段才真正建立反应计划，Tick 1 结束时计划数仍为 0。</item>
        /// </list>
        ///
        /// <para>
        /// 会让它失败的实现缺陷：AI 直接查询机会系统而不读快照（Tick 0 就会出现候选）；
        /// 或把命令投到当前 Tick 而不是 <c>nextTick</c>（Tick 1 结束时计划数不为 0）。
        /// </para>
        /// </summary>
        [Test]
        public void AiCannotReactToOpportunityInItsOpeningTick()
        {
            BattleDefinition definition = Task09A2Fixture.BuildDefinition(Player, Ai);
            var sim = NewAiSim(definition, out AiControllerLogic ai,
                out List<DecisionSnapshot> delivered, out List<CommandIngressRejection> ingressRejections);

            // 攻击在 Tick 1 提交 ⇒ 机会在 Tick 1 公开（= AI 在 Tick 1 的快照里第一次看到它）。
            Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);
            Task09A2Fixture.SeedAdrenaline(sim, Task09A2Fixture.Monster, FrozenDesignValues.BlockAdrenalineCost);
            ReactionOpportunityRuntime opportunity =
                Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);

            // ① 机会出现之前：Tick 0 的 AI 快照里根本没有这条机会。
            AiDecision beforeOpening = ai.DecisionAt(0L);
            Assert.That(beforeOpening, Is.Not.Null, "夹具前提：AI 必须在 Tick 0 决策");
            Assert.That(beforeOpening.ReactionCandidateCount, Is.Zero,
                "机会公开之前，AI 的反应候选必须恰好为 0（不得绕开快照直接查机会系统）");
            Assert.That(beforeOpening.Selected, Is.Null, "机会存在之前 AI 不得提出反应命令");

            // ② 机会公开 Tick（Tick 1）**结束时**仍不得有反应计划：
            //    命令的目标 Tick 恒为 nextTick，因此最早只能在 Tick 2 的命令阶段生效。
            StepTo(sim, 1L);
            Assert.That(ReactionPlanCount(sim), Is.Zero,
                "机会公开的同 Tick 结束时不得出现任何反应计划（抢跑被结构性排除）");

            // ③ 命令确实是在 Tick 1 提出、目标 Tick = 2，并在 Tick 2 的命令阶段生效。
            StepTo(sim, 2L);
            Assert.That(ReactionPlanCount(sim), Is.EqualTo(1),
                "命令在**下一 Tick** 的命令阶段才生效（这就是\"抢不到同 Tick\"的直接证据）");
            AiDecision openingDecision = ai.DecisionAt(1L);
            Assert.That(openingDecision, Is.Not.Null, "夹具前提：AI 必须在 Tick 1 决策");
            Assert.That(openingDecision.ReactionCandidateCount, Is.EqualTo(2),
                "机会公开 Tick 上，AI 必须从**快照里**拿到这两条候选（Block 与 Dodge）");
            Assert.That(openingDecision.Selected, Is.Not.Null, "AI 在这一 Tick 提出反应命令");
            Assert.That(openingDecision.Selected.TargetTick, Is.EqualTo(2L),
                "命令目标 Tick 恒为 nextTick ⇒ 不可能在机会公开的同一 Tick 生效");
            Assert.That(ingressRejections, Is.Empty,
                "AI 不得产出任何会被入口拒绝的\"抢跑\"请求");

            // 反假绿：机会必须真的被利用（否则本用例只证明了一个哑 AI）。
            StepTo(sim, 3L);
            Assert.That(ReactionPlanCount(sim), Is.EqualTo(1),
                "机会必须在下一 Tick 的命令阶段被正常利用");
            Assert.That(ai.DecisionAt(2L), Is.Not.Null, "夹具前提：AI 必须继续逐 Tick 决策");
            Assert.That(opportunity.IsOpen, Is.False, "机会必须已被消费（不是一直挂着）");
        }

        // =====================================================================
        // 必需测试 36：AiAndPlayerAvailabilityUsesSameActionSetRules
        // =====================================================================

        /// <summary>
        /// AI 与玩家的理论动作可用性来自<strong>同一个</strong> <c>ActionSetDefinition</c>：
        /// 同一场里 hero（玩家）与 mon（AI）的动作集内容相同 ⇒ 对称的候选单位集合；
        /// 并且对<strong>同一个</strong>非法目标给出<strong>同一个</strong>稳定拒绝码。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：AI 侧的候选筛面用自己复制的关系矩阵或自建"敌人列表"
        /// （候选集合分叉），或按 <c>CommandSourceKind</c> 分叉出第二条可用性规则
        /// （同一非法目标在两侧得到不同拒绝码）。
        /// </para>
        /// </summary>
        [Test]
        public void AiAndPlayerAvailabilityUsesSameActionSetRules()
        {
            BattleDefinition definition = Task09A2Fixture.BuildDefinition(Player, Ai);
            var sim = NewAiSim(definition, out AiControllerLogic ai,
                out List<DecisionSnapshot> delivered, out List<CommandIngressRejection> ingressRejections);
            Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);

            DecisionSnapshot snapshot = delivered.Last(s => s.Tick == 1L);
            Assert.That(snapshot.Definition, Is.Not.Null,
                "决策快照必须持有命令层同一个 BattleDefinition（不是 AI 自带的动作表）");
            Assert.That(snapshot.Definition.FindAction(AttackSpec()), Is.Not.Null,
                "同一个定义里必须能查到该攻击 ActionSpec");

            IReadOnlyList<ActionSpecId> heroSpecs = snapshot.ActionSetOf(Task09A2Fixture.Hero);
            IReadOnlyList<ActionSpecId> monSpecs = snapshot.ActionSetOf(Task09A2Fixture.Monster);
            Assert.That(heroSpecs.Count, Is.GreaterThan(0), "夹具前提：动作集必须非空");
            Assert.That(monSpecs.Select(s => s.Value).ToArray(),
                Is.EqualTo(heroSpecs.Select(s => s.Value).ToArray()),
                "同一份 ActionSet 内容必须给出逐字相同的动作列表");

            // 同一套候选判据：按**权威阵营关系**对称地给出候选（不按 Controller/来源种类）。
            var universe = new List<UnitId>
            {
                Task09A2Fixture.Hero, Task09A2Fixture.Monster,
                Task09A2Fixture.Neutral, Task09A2Fixture.Ally
            };
            ActionSpec attack = snapshot.FindAction(AttackSpec());
            TargetCandidateQuery candidates = TargetCandidateQuery.From(snapshot);

            Assert.That(candidates.Classify(Task09A2Fixture.Hero, Task09A2Fixture.Monster),
                Is.EqualTo(UnitRelation.Hostile));
            Assert.That(candidates.Classify(Task09A2Fixture.Monster, Task09A2Fixture.Hero),
                Is.EqualTo(UnitRelation.Hostile));
            Assert.That(candidates.Classify(Task09A2Fixture.Hero, Task09A2Fixture.Ally),
                Is.EqualTo(UnitRelation.Allied));
            Assert.That(candidates.Classify(Task09A2Fixture.Monster, Task09A2Fixture.Ally),
                Is.EqualTo(UnitRelation.Hostile),
                "d_ally 属于 hero 阵营 ⇒ 对 mon 是敌对（关系矩阵本身必须对称）");

            IReadOnlyList<UnitId> heroTargets =
                candidates.CandidatesFor(attack, Task09A2Fixture.Hero, universe);
            IReadOnlyList<UnitId> monTargets =
                candidates.CandidatesFor(attack, Task09A2Fixture.Monster, universe);
            Assert.That(heroTargets.Select(u => u.Value).ToArray(), Is.EqualTo(new[] { 2L }),
                "Hostile 掩码下 hero 只能打敌对单位（mon）");
            Assert.That(monTargets.Select(u => u.Value).ToArray(), Is.EqualTo(new[] { 1L, 4L }),
                "同一条掩码规则对 mon 给出它自己的敌对集合（hero 与 d_ally），" +
                "二者的差别完全来自阵营关系而不是 Controller；实测 hero→" +
                string.Join(",", heroTargets.Select(u => u.Value)) + " mon→" +
                string.Join(",", monTargets.Select(u => u.Value)));
            Assert.That(candidates.CandidatesFor(attack, Task09A2Fixture.Hero, universe)
                    .Select(u => candidates.Classify(Task09A2Fixture.Hero, u)),
                Is.EqualTo(new[] { UnitRelation.Hostile }));
            Assert.That(candidates.CandidatesFor(attack, Task09A2Fixture.Monster, universe)
                    .Select(u => candidates.Classify(Task09A2Fixture.Monster, u)),
                Is.EqualTo(new[] { UnitRelation.Hostile, UnitRelation.Hostile }),
                "两侧的候选都恰好是各自唯一的敌对关系分类（同一条规则的对称应用）");

            // 同一个非法目标（Ally 需要显式 Allied 位，本 spec 没有）⇒ 两侧同一个稳定码。
            string heroCode = candidates.EligibilityOf(attack, Task09A2Fixture.Hero, Task09A2Fixture.Ally);
            string monCode = candidates.EligibilityOf(attack, Task09A2Fixture.Monster, Task09A2Fixture.Neutral);
            Assert.That(heroCode, Is.EqualTo(ScheduleCodes.TARGET_RELATION_NOT_ALLOWED));
            Assert.That(monCode, Is.EqualTo(heroCode),
                "AI 与玩家对各自关系不允许的目标必须得到同一个拒绝码");

            _ = ai;
            _ = ingressRejections;
        }

        // =====================================================================
        // 必需测试 37：AiReactionCandidatePermutationDoesNotChangeDecisionWithSameSeed
        // =====================================================================

        /// <summary>
        /// 候选<strong>稳定排序之后</strong>才使用 RNG：把同一条机会的选项集合做两种输入排列，
        /// 先断言两种输入<strong>确实不同</strong>，再断言同 seed 下参与选择的候选序列、
        /// 选中的命令与 RNG 状态<strong>逐字段相同</strong>。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：用 <c>PickIndex(原始集合.Count)</c> 取"第几个候选"
        /// （原始枚举序成为末级键）；或在排序前就用 RNG 排除候选（排列会改变被排除者）。
        /// </para>
        /// </summary>
        [Test]
        public void AiReactionCandidatePermutationDoesNotChangeDecisionWithSameSeed()
        {
            var observations = new List<DecisionSnapshot>();
            var observer = new SnapshotObserver(observations);
            var assembly = new BattleSimulationAssembly(
                decisionObservers: new IDecisionObserver[] { observer });
            BattleSimulation sim = Task09A2Fixture.NewSim(assembly: assembly);
            Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);
            Task09A2Fixture.SeedAdrenaline(sim, Task09A2Fixture.Monster, FrozenDesignValues.BlockAdrenalineCost);
            Task09A2Fixture.Step(sim, 2L);
            DecisionSnapshot snapshot = observations.Last(s => s.Tick == 2L);
            Assert.That(snapshot, Is.Not.Null, "夹具前提：必须已经投递过决策快照");

            AiDecisionContext baseline = BuildAiContext(sim, snapshot, Task09A2Fixture.AiId);
            Assert.That(baseline.Opportunities.Count, Is.EqualTo(1),
                "夹具前提：恰好一条已公开机会（mon 是防御者）");
            AiOpportunityView realOpportunity = baseline.Opportunities[0];
            Assert.That(realOpportunity.Options.Count, Is.GreaterThanOrEqualTo(2),
                "夹具前提：该机会必须公开两个及以上反应选项（Block 与 Dodge）");

            // —— 第一段：真实机会的**原始输入排列**必须互不相同，且稳定排序后的候选与决策相同 ——
            AiDecisionContext permuted = PermuteOpportunityOptions(baseline);
            Assert.That(permuted.Opportunities[0].Options.Select(Describe).ToArray(),
                Is.Not.EqualTo(realOpportunity.Options.Select(Describe).ToArray()),
                "排列前提：两种原始输入排列必须**确实不同**，否则本用例是空洞的");

            ulong realSeed = 20261007UL;
            AiDecision realFirst = AiDecisionFunction.Decide(baseline, realSeed);
            AiDecision realSecond = AiDecisionFunction.Decide(permuted, realSeed);
            Assert.That(realSecond.ReactionCandidates.Select(Describe).ToArray(),
                Is.EqualTo(realFirst.ReactionCandidates.Select(Describe).ToArray()),
                "真实机会：稳定排序后的反应候选序列必须与输入排列无关");
            Assert.That(realSecond.ReactionCandidateCount, Is.EqualTo(2),
                "夹具前提：真实机会必须给出 2 条候选（Block 与 Dodge）");
            Assert.That(Describe(realSecond.Selected), Is.EqualTo(Describe(realFirst.Selected)),
                "真实机会：同 seed 下选中的命令必须完全相同");
            Assert.That(realFirst.Selected, Is.Not.Null, "夹具前提：本世界必须有可选的反应候选");
            Assert.That(realFirst.Selected.Scope, Is.InstanceOf<ReactionCommandScope>());
            Assert.That(realSecond.Selected.TargetTick, Is.EqualTo(realFirst.Selected.TargetTick));

            // —— 第二段：**排序键完全决定顺序**（构造带重复排序键的合成候选集）——
            // 真实机会的选项在机会系统内部已按 ActionSpecId 规范化去重，因此只有"两个不同排序键"
            // 的候选；要观察"排序真的发生"必须构造排序键重复的输入（重复 ≠ 歧义：
            // 末级键仍由稳定键整体给出，原始枚举序永远不是末级键）。
            AiReactionOptionView block = realOpportunity.Options.Single(o => o.ReactionKind == ReactionCommandKind.Block);
            AiReactionOptionView dodge = realOpportunity.Options.Single(o => o.ReactionKind == ReactionCommandKind.Dodge);

            var six = new List<AiReactionOptionView>
            {
                block with { ResponseDeadlineTick = 2L },
                dodge with { ResponseDeadlineTick = 5L },
                block with { ResponseDeadlineTick = 1L },
                dodge with { ResponseDeadlineTick = 4L },
                block with { ResponseDeadlineTick = 3L },
                dodge with { ResponseDeadlineTick = 6L }
            };

            var inputA = new List<AiReactionOptionView>
            {
                six[0], six[1], six[2], six[3], six[4], six[5]
            };
            var inputB = new List<AiReactionOptionView>
            {
                six[2], six[4], six[0], six[1], six[3], six[5]
            };
            Assert.That(inputB.Select(Describe).ToArray(),
                Is.Not.EqualTo(inputA.Select(Describe).ToArray()),
                "排列前提：合成集合的两种输入必须**确实不同**");

            IReadOnlyList<AiReactionOptionView> sortedA =
                AiReactionCandidateBuilder.OrderReactions(inputA);
            IReadOnlyList<AiReactionOptionView> sortedB =
                AiReactionCandidateBuilder.OrderReactions(inputB);
            Assert.That(sortedA.Select(Describe).ToArray(),
                Is.EqualTo(new[]
                {
                    Describe(block with { ResponseDeadlineTick = 1L }),
                    Describe(block with { ResponseDeadlineTick = 2L }),
                    Describe(block with { ResponseDeadlineTick = 3L }),
                    Describe(dodge with { ResponseDeadlineTick = 4L }),
                    Describe(dodge with { ResponseDeadlineTick = 5L }),
                    Describe(dodge with { ResponseDeadlineTick = 6L })
                }),
                "排序键必须完全决定顺序：ActionSpecId → ReactionKind → ResponseDeadlineTick");
            Assert.That(sortedA.Select(Describe).ToArray(),
                Is.EqualTo(sortedB.Select(Describe).ToArray()),
                "两种输入排列必须得到同一条规范顺序（原始枚举序不是末级键）");

            long tick = snapshot.Tick;
            long targetTick = tick + 1L;
            AiDecision syntheticA = AiDecisionFunction.DecideOrderedReactions(
                sortedA, realSeed, tick, targetTick, Task09A2Fixture.AiId.Value);
            AiDecision syntheticB = AiDecisionFunction.DecideOrderedReactions(
                sortedB, realSeed, tick, targetTick, Task09A2Fixture.AiId.Value);
            Assert.That(Describe(syntheticA.Selected), Is.EqualTo(Describe(syntheticB.Selected)),
                "同 seed 下两种排列必须选中同一条命令");
            Assert.That(syntheticA.Rng.State, Is.EqualTo(syntheticB.Rng.State),
                "同 seed 下 RNG 消耗必须相同");
            Assert.That(syntheticA.Selected.TargetTick, Is.EqualTo(targetTick));
            Assert.That(realFirst.Selected.TargetTick, Is.EqualTo(targetTick),
                "真实路径与合成路径必须给出同一个目标 Tick");

            // 两张表在"规范顺序 → 选中项"上是同一条规则：
            // 合成表与真实机会都只有 Block/Dodge 两族，同 seed 选中同一族（ActionSpecId 相同）。
            Assert.That(syntheticA.Selected.Scope, Is.InstanceOf<ReactionCommandScope>());
            Assert.That(
                ((ReactionCommandScope)syntheticA.Selected.Scope).ReactionOpportunityId.Value,
                Is.EqualTo(realOpportunity.ReactionOpportunityId.Value),
                "选中的命令必须指向同一条已公开机会");
        }

        // =====================================================================
        // 必需测试 38：AiCandidatePermutationDoesNotChangeDecisionWithSameSeed
        // =====================================================================

        /// <summary>
        /// 普通动作候选的同一个不变量：两种<strong>受控单位顺序</strong>排列下，
        /// 稳定排序后的候选序列与同 seed 的决策必须逐字段相同；
        /// 并且两场同 seed 的真实模拟给出一致的排程事实与 Snapshot 决策。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：候选排序缺少末级稳定键（例如只按 <c>ActionSpecId</c> 排序，
        /// 而目标顺序来自容器枚举序），于是排列会改变 <c>PickIndex</c> 选中的目标。
        /// </para>
        /// </summary>
        [Test]
        public void AiCandidatePermutationDoesNotChangeDecisionWithSameSeed()
        {
            // 一个**AI 控制两个单位**的世界：候选枚举顺序因此有两种真实排列
            // （hero 先 / mon 先），而不是靠"把单元素列表反转"这种空洞手法。
            var bothAi = Task09A2Fixture.BuildDefinition(Ai, Ai);
            var probe = new Task09A2Fixture.CanonicalSnapshotProbe();
            AiControllerLogic ai = new AiControllerLogic(Task09A2Fixture.Inputs.InitialRngSeed);
            ai.RegisterController(ControllerBindingOf(bothAi, CommandSourceKind.Ai));
            BattleSimulation sim = Task09A2Fixture.NewSim(bothAi, probe.Assembly);
            ai.AttachPorts(sim.AiReactionOpportunities, sim.AiActionPlanLookup, sim.MovementPathCalculator);

            // 两个单位各一扇窗口 ⇒ 两个 AI 控制者都有合法提交面。
            sim.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Hero, Task09A2Fixture.WindowBudget);
            sim.WindowManager.ScheduleWindow(1L, Task09A2Fixture.Monster, Task09A2Fixture.WindowBudget);
            StepTo(sim, 0L);
            StepTo(sim, 1L);

            DecisionSnapshot snapshot = probe.At(1L);
            Assert.That(snapshot, Is.Not.Null, "夹具前提：Tick 1 必须已经投递过决策快照");

            var twoUnits = new List<UnitId> { Task09A2Fixture.Hero, Task09A2Fixture.Monster };
            var opportunities = new List<AiOpportunityView>();
            for (int i = 0; i < twoUnits.Count; i++)
            {
                IReadOnlyList<AiOpportunityView> found =
                    sim.AiReactionOpportunities.PublishedOpportunitiesFor(twoUnits[i], snapshot.Tick);
                for (int j = 0; j < (found?.Count ?? 0); j++) opportunities.Add(found[j]);
            }
            AiDecisionContext baseline = new AiDecisionContext(
                snapshot.WithDefinition(bothAi), Task09A2Fixture.AiId, twoUnits,
                snapshot.EditablePlansOf(Task09A2Fixture.AiId), snapshot.OwnWindow,
                opportunities, sim.MovementPathCalculator, sim.AiActionPlanLookup);
            IReadOnlyList<AiCandidate> forward = AiCandidateBuilder.BuildNormalCandidatesOrdered(baseline);
            Assert.That(forward.Count, Is.GreaterThanOrEqualTo(4),
                "夹具前提：两个受控单位 × 各自候选 ⇒ 候选集必须足够大");

            // 第二种排列：把**受控单位顺序**整体反转（真实执行路径按另一种顺序枚举候选）。
            AiDecisionContext permuted = baseline with
            {
                ControlledUnits = baseline.ControlledUnits.Reverse().ToList()
            };

            // 排列前提：**原始枚举序**确实不同（否则本用例是空洞的）。
            Assert.That(
                AiCandidateBuilder.BuildNormalCandidates(permuted).Select(Describe).ToArray(),
                Is.Not.EqualTo(AiCandidateBuilder.BuildNormalCandidates(baseline).Select(Describe).ToArray()),
                "排列前提：两种受控单位顺序必须产出**不同**的候选枚举序，否则本用例是空洞的");

            ulong seed = 20261007UL;
            AiDecision first = AiDecisionFunction.Decide(baseline, seed);
            AiDecision second = AiDecisionFunction.Decide(permuted, seed);

            Assert.That(second.NormalCandidates.Select(Describe).ToArray(),
                Is.EqualTo(first.NormalCandidates.Select(Describe).ToArray()),
                "稳定排序后的普通候选序列必须与输入排列无关");
            Assert.That(second.NormalCandidateCount, Is.EqualTo(first.NormalCandidateCount));
            Assert.That(Describe(second.Selected), Is.EqualTo(Describe(first.Selected)),
                "同 seed 下选中的命令必须完全相同");
            Assert.That(second.Rng.State, Is.EqualTo(first.Rng.State));

            // —— 端到端：两场同 seed 的真实模拟必须给出同一条 AI 决策 ——
            BattleDefinition twinDefinition = Task09A2Fixture.BuildDefinition(Ai, Ai);
            var twinProbe = new Task09A2Fixture.CanonicalSnapshotProbe();
            BattleSimulation twin = Task09A2Fixture.NewSim(twinDefinition, twinProbe.Assembly);
            twin.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Hero, Task09A2Fixture.WindowBudget);
            twin.WindowManager.ScheduleWindow(1L, Task09A2Fixture.Monster, Task09A2Fixture.WindowBudget);
            StepTo(twin, 0L);
            StepTo(twin, 1L);

            AiDecisionContext twinContext = baseline with
            {
                Snapshot = twinProbe.At(1L).WithDefinition(twinDefinition)
            };
            Assert.That(Describe(AiDecisionFunction.Decide(twinContext, seed).Selected),
                Is.EqualTo(Describe(first.Selected)),
                "两场同 seed 的模拟必须给出同一条 AI 决策");

            _ = ai;
        }

        // ================= 助手 =================

        /// <summary>
        /// 从真实模拟构造一次 AI 决策上下文（真实定义、真实单位、真实机会来源），
        /// 但<strong>不</strong>经 <see cref="AiControllerLogic"/> —— 它服务的是"排列不变性"这类
        /// 需要显式构造输入排列的用例。
        /// </summary>
        private static AiDecisionContext BuildAiContext(
            BattleSimulation sim, DecisionSnapshot snapshot, ControllerId controllerId)
        {
            IReadOnlyList<UnitId> units = new List<UnitId>
            {
                controllerId.Value == Task09A2Fixture.AiId.Value
                    ? Task09A2Fixture.Monster
                    : Task09A2Fixture.Hero
            };

            return new AiDecisionContext(
                snapshot.WithDefinition(sim.Definition),
                controllerId,
                units,
                snapshot.EditablePlansOf(controllerId),
                snapshot.OwnWindow,
                sim.AiReactionOpportunities.PublishedOpportunitiesFor(units[0], snapshot.Tick),
                sim.MovementPathCalculator,
                sim.AiActionPlanLookup);
        }

        /// <summary>把机会的选项顺序整体倒过来（唯一变化的只有输入排列）。</summary>
        private static AiDecisionContext PermuteOpportunityOptions(AiDecisionContext context)
        {
            var permuted = new List<AiOpportunityView>(context.Opportunities.Count);
            foreach (AiOpportunityView opportunity in context.Opportunities)
            {
                var options = new List<AiReactionOptionView>(opportunity.Options);
                options.Reverse();
                permuted.Add(opportunity with { Options = options });
            }
            permuted.Reverse();
            return context with { Opportunities = permuted };
        }

        private static string Describe(AiReactionOptionView option)
            => option == null
                ? "<null>"
                : option.ActionSpecId.Value + ":" + option.ReactionKind + ":" + option.ResponseDeadlineTick;

        private static string Describe(AiCandidate candidate)
            => candidate == null ? "<null>" : candidate.ToString();

        private static string Describe(CommandRequest request)
            => request == null ? "<none>" : CommandScopes.Describe(request.Scope) + "|tick=" + request.TargetTick;

        private static string Describe(AiDecision decision)
            => decision == null ? "<none>" : decision.ToString();

        private static string Describe(ActionPlanSnapshot plan)
            => plan == null
                ? "<null>"
                : plan.ActionPlanId + ":" + (plan.ActionSpecId ?? "<null>") + ":" + plan.State + ":" +
                  plan.StartTick + ":" + plan.OwnerUnitId;

        private static string DescribeRejections(IReadOnlyList<CommandIngressRejection> rejections)
            => rejections.Count == 0 ? "<none>" : string.Join(",", rejections.Select(r => r.ReasonCode));

        /// <summary>只记录 Canonical 视角快照的诊断观察者（不声明 Controller）。</summary>
        private sealed class SnapshotObserver : IDecisionObserver
        {
            private readonly List<DecisionSnapshot> _observed;

            public SnapshotObserver(List<DecisionSnapshot> observed) => _observed = observed;

            public void ObserveOrdered(DecisionSnapshot snapshot, CommandIngressRegistry ingress, long nextTick)
                => _observed.Add(snapshot);
        }

        /// <summary>
        /// <strong>AI 洁净性</strong>（R-7）钉死用的 IL 审计：把 <c>ProjectHero.Logic.AI</c> 命名空间下
        /// 每个类型（含嵌套类型）的每个方法体的 IL 反汇编成方法引用，再逐条比对
        /// <strong>声明类型</strong>。
        ///
        /// <para>
        /// 它检查的是"实现里到底调用了谁"，而不是"源码里有没有某个字符串"：
        /// 把写入面藏进私有方法、局部函数或嵌套类型都逃不掉。
        /// </para>
        /// </summary>
        private static class AiIlAudit
        {
            public static readonly string[] ForbiddenTypeNames =
            {
                "ProjectHero.Logic.Timeline.ScheduleEditor",
                "ProjectHero.Logic.Timeline.ActionScheduleAuthority",
                "ProjectHero.Logic.Timeline.ActionPlanFactory",
                "ProjectHero.Logic.Timeline.ActionPlanTerminalCoordinator",
                "ProjectHero.Logic.Timeline.ReactionOpportunitySystem",
                "ProjectHero.Logic.Timeline.ReactionPlanner",
                "ProjectHero.Logic.Timeline.ReactionCommandPlanner",
                "ProjectHero.Logic.Turns.TurnWindowManager",
                "ProjectHero.Logic.Turns.TurnWindowBudgetAuthority",
                "ProjectHero.Logic.Turns.ActionScheduler",
                "UnityEngine.Random",
                "UnityEngine.Time",
                "UnityEngine.GameObject",
                "UnityEngine.MonoBehaviour"
            };

            public static IReadOnlyList<MethodReference> AllMethodReferences()
            {
                var references = new List<MethodReference>();
                Assembly assembly = typeof(AiControllerLogic).Assembly;
                foreach (Type type in assembly.GetTypes())
                {
                    if (type == null || type.Namespace == null) continue;
                    if (!type.Namespace.StartsWith("ProjectHero.Logic.AI", StringComparison.Ordinal)) continue;
                    CollectType(type, references);
                }
                return references;
            }

            private static void CollectType(Type type, List<MethodReference> references)
            {
                foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    CollectType(nested, references);
                }

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Instance | BindingFlags.Static |
                                           BindingFlags.DeclaredOnly;
                foreach (MethodBase method in type.GetMethods(flags))
                {
                    CollectMethod(type, method, references);
                }
                foreach (ConstructorInfo constructor in type.GetConstructors(flags))
                {
                    CollectMethod(type, constructor, references);
                }
            }

            private static void CollectMethod(
                Type declaringType, MethodBase method, List<MethodReference> references)
            {
                MethodBody body;
                try
                {
                    body = method.GetMethodBody();
                }
                catch (Exception)
                {
                    return;    // 抽象/外部方法没有方法体
                }
                if (body == null) return;
                byte[] il = body.GetILAsByteArray();
                if (il == null || il.Length == 0) return;

                Module module = method.Module;
                int index = 0;
                while (index < il.Length)
                {
                    int offset = index;
                    ushort value = il[index++];
                    if (value == 0xFE)
                    {
                        if (index >= il.Length) break;
                        value = (ushort)(0xFE00 | il[index++]);
                    }

                    OpCode opcode = (value & 0xFF00) == 0xFE00
                        ? MultiByteOpCodes[value & 0xFF]
                        : SingleByteOpCodes[value & 0xFF];
                    if (opcode.Size == 0) continue;

                    switch (opcode.OperandType)
                    {
                        case OperandType.InlineNone:
                            break;
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineVar:
                        case OperandType.ShortInlineBrTarget:
                            index += 1;
                            break;
                        case OperandType.InlineVar:
                            index += 2;
                            break;
                        case OperandType.InlineMethod:
                        {
                            int token = BitConverter.ToInt32(il, index);
                            index += 4;
                            MethodBase resolved = Resolve(module, token);
                            if (resolved != null)
                            {
                                references.Add(new MethodReference(
                                    declaringType.FullName + "." + method.Name + "@il" + offset,
                                    resolved.DeclaringType == null ? null : resolved.DeclaringType.FullName,
                                    resolved.Name));
                            }
                            break;
                        }
                        case OperandType.InlineI:
                        case OperandType.InlineBrTarget:
                        case OperandType.InlineField:
                        case OperandType.InlineSig:
                        case OperandType.InlineString:
                        case OperandType.InlineTok:
                        case OperandType.InlineType:
                        case OperandType.InlineSwitch:
                            index += 4;
                            break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR:
                            index += 8;
                            break;
                        case OperandType.ShortInlineR:
                            index += 4;
                            break;
                        default:
                            index = il.Length;
                            break;
                    }
                }
            }

            private static MethodBase Resolve(Module module, int token)
            {
                try
                {
                    return module.ResolveMethod(token);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            public static bool IsOfType(MethodReference reference, string typeName)
                => reference != null && string.Equals(reference.DeclaringType, typeName, StringComparison.Ordinal);

            private static readonly OpCode[] SingleByteOpCodes = BuildSingleByteOpCodes();
            private static readonly OpCode[] MultiByteOpCodes = BuildMultiByteOpCodes();

            private static OpCode[] BuildSingleByteOpCodes()
            {
                var table = new OpCode[256];
                foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var opcode = (OpCode)field.GetValue(null);
                    if (opcode.Size != 1) continue;
                    table[(byte)opcode.Value] = opcode;
                }
                return table;
            }

            private static OpCode[] BuildMultiByteOpCodes()
            {
                var table = new OpCode[256];
                foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var opcode = (OpCode)field.GetValue(null);
                    if (opcode.Size != 2) continue;
                    table[(byte)(opcode.Value & 0xFF)] = opcode;
                }
                return table;
            }
        }

        /// <summary>一条 IL 方法引用（含它出现在哪个方法体的哪个偏移）。</summary>
        private sealed class MethodReference
        {
            public MethodReference(string site, string declaringType, string name)
            {
                Site = site;
                DeclaringType = declaringType;
                Name = name;
            }

            public string Site { get; }
            public string DeclaringType { get; }
            public string Name { get; }

            public override string ToString() => Site + " -> " + DeclaringType + "." + Name;
        }
    }
}
