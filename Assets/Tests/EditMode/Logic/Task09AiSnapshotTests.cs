using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 09 · A3 轮：<strong>AI 未来状态入规范化快照与哈希</strong>（产出 15）
    /// 与 <strong>DecisionSnapshot 按 Controller 过滤</strong>（产出 17）。
    ///
    /// 逐条对应必需测试：<c>AiRuntimeStateAppearsInCanonicalSnapshot</c>、
    /// <c>DecisionSnapshotDoesNotLeakOtherControllerEditablePlans</c>。
    /// </summary>
    public class Task09AiSnapshotTests
    {
        private const CommandSourceKind Player = CommandSourceKind.Player;
        private const CommandSourceKind Ai = CommandSourceKind.Ai;

        private static ActionSpecId Spec(string id) => Task09A2Fixture.Spec(id);
        private static ActionSpecId AttackSpec() => Spec(Task09A2Fixture.AttackSpecId);

        // =====================================================================
        // 必需测试 39：AiRuntimeStateAppearsInCanonicalSnapshot
        // =====================================================================

        /// <summary>
        /// AI 的未来决策状态<strong>整体</strong>进入规范化快照与哈希：
        /// 真实模拟里能看到注册后的 AI 控制者，且
        /// （身份、决策计数、最后决策 Tick、下一次思考 Tick、RNG 算法版本与状态）
        /// <strong>每一个字段单独改动都会改变摘要</strong>，集合顺序也不得影响摘要。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把 AI 状态留在 <c>AiControllerLogic</c> 里而不发进快照
        /// （重演时 AI 决策分叉）；只哈希 <c>NextThinkTick</c> 而漏掉决策计数或 RNG 状态
        /// （"全部字段进哈希"退化为部分哈希）；集合按插入序哈希（顺序敏感）。
        /// </para>
        /// </summary>
        [Test]
        public void AiRuntimeStateAppearsInCanonicalSnapshot()
        {
            BattleDefinition definition = Task09A2Fixture.BuildDefinition(Player, Ai);
            var sim = NewAiSim(definition, out AiControllerLogic ai,
                out List<DecisionSnapshot> delivered, out List<CommandIngressRejection> rejections);
            Task09A2Fixture.Step(sim, 0L);

            // —— 真实模拟：AI 运行态必须在规范化快照里 ——
            LogicSnapshot snapshot = sim.CurrentSnapshot;
            Assert.That(snapshot.AiControllers.Count, Is.EqualTo(1),
                "注册过的 AI 控制者必须出现在规范化快照里");
            AiControllerSnapshot controller = snapshot.AiControllers[0];
            Assert.That(controller.ControllerId, Is.EqualTo(Task09A2Fixture.AiId.Value),
                "快照里的身份必须是入口绑定的那个 ControllerId（不是自报字符串）");
            Assert.That(controller.LastDecisionTick, Is.EqualTo(0L),
                "最后决策 Tick 必须是本 Tick（决策确实发生在快照构建之前）");
            Assert.That(controller.DecisionCount, Is.EqualTo(1L));
            Assert.That(controller.NextThinkTick, Is.EqualTo(1L),
                "Tick 0 决策完成后，下一次允许决策的 Tick = 本条命令的目标 Tick = 1" +
                "（因此每个 Tick 恰好决策一次，且同一 Tick 不会重复）");
            Assert.That(controller.Rng, Is.Not.Null,
                "AI 自己的版本化 RNG 状态必须进快照（它是决策序列的直接输入）");
            Assert.That(controller.Rng.AlgorithmVersion, Is.EqualTo(DeterministicRng.AlgorithmVersion));

            // 同一份运行态的独立投影必须逐字段一致（快照来源与控制器是同一个对象）。
            AiControllerRuntimeState state = ai.RuntimeStateOf(Task09A2Fixture.AiId);
            Assert.That(state, Is.Not.Null);
            Assert.That(state.NextThinkTick, Is.EqualTo(controller.NextThinkTick));
            Assert.That(state.LastDecisionTick, Is.EqualTo(controller.LastDecisionTick));
            Assert.That(state.DecisionCount, Is.EqualTo(controller.DecisionCount));
            Assert.That(state.Rng.State, Is.EqualTo(controller.Rng.State));

            Assert.That(rejections, Is.Empty, "夹具前提：AI 的提交不得被入口拒绝");
            Assert.That(delivered.Count, Is.EqualTo(1));

            // —— 哈希面：每一个字段单独改动都必须改变摘要 ——
            ulong baseline = snapshot.ComputeHash();
            Assert.That(HashWith(snapshot, controller.ControllerId, controller.NextThinkTick,
                    controller.DecisionCount, controller.LastDecisionTick, controller.Rng),
                Is.EqualTo(baseline), "同一份 AI 状态必须给出同一个摘要（自反性前提）");

            AssertDifferent(baseline, "ControllerId", HashWith(snapshot, controller.ControllerId + ".x",
                controller.NextThinkTick, controller.DecisionCount, controller.LastDecisionTick, controller.Rng));
            AssertDifferent(baseline, "NextThinkTick", HashWith(snapshot, controller.ControllerId,
                controller.NextThinkTick + 1L, controller.DecisionCount, controller.LastDecisionTick, controller.Rng));
            AssertDifferent(baseline, "DecisionCount", HashWith(snapshot, controller.ControllerId,
                controller.NextThinkTick, controller.DecisionCount + 1L, controller.LastDecisionTick, controller.Rng));
            AssertDifferent(baseline, "LastDecisionTick", HashWith(snapshot, controller.ControllerId,
                controller.NextThinkTick, controller.DecisionCount, controller.LastDecisionTick + 1L, controller.Rng));
            AssertDifferent(baseline, "Rng.State", HashWith(snapshot, controller.ControllerId,
                controller.NextThinkTick, controller.DecisionCount, controller.LastDecisionTick,
                new RngSnapshot(controller.Rng.AlgorithmVersion, controller.Rng.State ^ 1UL)));
            AssertDifferent(baseline, "Rng.AlgorithmVersion", HashWith(snapshot, controller.ControllerId,
                controller.NextThinkTick, controller.DecisionCount, controller.LastDecisionTick,
                new RngSnapshot(controller.Rng.AlgorithmVersion + 1, controller.Rng.State)));
            AssertDifferent(baseline, "AiControllers.Count", HashWith(snapshot, controller.ControllerId,
                controller.NextThinkTick, controller.DecisionCount, controller.LastDecisionTick,
                controller.Rng, extra: true));

            // 集合顺序不得影响摘要：逆序插入的两个 AI 控制者必须给出同一个摘要。
            var twoOrdered = new List<AiControllerSnapshot>
            {
                new AiControllerSnapshot("controller.ai.a", 3L, 2L, 1L, new RngSnapshot(1, 7UL)),
                new AiControllerSnapshot("controller.ai.b", 4L, 5L, 2L, new RngSnapshot(1, 9UL))
            };
            var twoReversed = new List<AiControllerSnapshot>(twoOrdered);
            twoReversed.Reverse();
            ulong orderedHash = HashWithControllers(snapshot, twoOrdered);
            Assert.That(HashWithControllers(snapshot, twoReversed), Is.EqualTo(orderedHash),
                "AiControllers 必须按 ControllerId 规范排序后再哈希（插入顺序不得影响摘要）");
            AssertDifferent(baseline, "AiControllers.Set", orderedHash);
        }

        // =====================================================================
        // 必需测试 40：DecisionSnapshotDoesNotLeakOtherControllerEditablePlans
        // =====================================================================

        /// <summary>
        /// 按 Controller 过滤：某个 Controller 的决策快照里
        /// <strong>不得</strong>出现其他 Controller <strong>尚未按玩法公开</strong>的
        /// <c>Editable</c> 普通计划——它的 <c>ActionSpecId</c>、目标、<c>Destination</c>
        /// 与精确 Tick 一个都不能泄露；同时，自己的 Editable 计划仍必须可编辑、
        /// 已锁定（按玩法公开）的计划仍必须可见、Canonical 哈希面也不得被过滤。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：过滤只看"是不是我的单位"而把别人的 Editable 计划照单全收
        /// （泄露）；或反过来把 Locked/Running 计划也裁掉（玩法公开面被误删）；
        /// 或用过滤后的快照去算规范化摘要（把控制者视图混进哈希面）。
        /// </para>
        /// </summary>
        [Test]
        public void DecisionSnapshotDoesNotLeakOtherControllerEditablePlans()
        {
            // —— 世界 A：玩家（hero）拥有一条仍未公开的 Editable 计划 ——
            var playerWorld = new SnapshotWorld(ownUnit: Task09A2Fixture.Hero);
            playerWorld.ScheduleOwnWindow(0L);
            Task09A2Fixture.Step(playerWorld.Sim, 0L);
            WindowId playerWindow = playerWorld.Sim.CurrentTurnWindow.WindowId;

            Task09A2Fixture.Submit(playerWorld.Sim, Task09A2Fixture.PlayerId, Task09A2Fixture.AddPlan(
                1L, playerWorld.Sim.ScheduleAuthority.ScheduleRevision, playerWindow,
                Task09A2Fixture.Hero, AttackSpec(), Task09A2Fixture.Monster, 30L));
            Task09A2Fixture.Step(playerWorld.Sim, 1L);

            ActionPlan heroPlan = playerWorld.Sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(heroPlan.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Hero), "夹具前提：计划属于 hero");
            Assert.That(heroPlan.StartTick, Is.GreaterThan(1L), "夹具前提：起点确实排在未来，尚未被锁定");
            Assert.That(heroPlan.StartTick, Is.GreaterThan(1L), "夹具前提：起点确实排在未来，尚未被锁定");

            DecisionSnapshot canonical = playerWorld.CanonicalAt(1L);
            Assert.That(canonical.ControllerId.Value, Is.Null,
                "夹具前提：诊断观察者收到的是 Canonical（未过滤）视角");
            Assert.That(canonical.VisiblePlans.Count, Is.EqualTo(1),
                "Canonical 视角仍包含全部权威计划（开发者视图不得被过滤）");

            // 玩家视角：自己的 Editable 计划必须可见且可编辑。
            DecisionSnapshot playerSnapshot = FilterFor(playerWorld.Sim, canonical,
                Task09A2Fixture.PlayerId, Task09A2Fixture.Hero, playerWindow);
            Assert.That(playerSnapshot.EditablePlansOf(Task09A2Fixture.PlayerId)
                    .Select(p => p.ActionPlanId).ToArray(),
                Is.EqualTo(new[] { heroPlan.ActionPlanId.Value }),
                "玩家必须能看到并编辑自己仍为 Editable 的普通计划");

            // AI 视角：玩家的 Editable 计划必须**整条消失**（字段一个都不泄露）。
            DecisionSnapshot aiViewOfPlayer = FilterFor(playerWorld.Sim, canonical,
                Task09A2Fixture.AiId, Task09A2Fixture.Monster, playerWindow);
            Assert.That(aiViewOfPlayer.VisiblePlans.Select(Describe).ToArray(), Is.Empty,
                "玩家尚未公开的 Editable 计划 ⇒ AI 的可见计划集合必须为空（无任何字段泄露）");
            Assert.That(aiViewOfPlayer.OwnPlans, Is.Empty, "AI 的私有计划面同样不得出现玩家的计划");
            Assert.That(aiViewOfPlayer.EditablePlansOf(Task09A2Fixture.AiId), Is.Empty,
                "AI 不得通过过滤面看到玩家的 Editable 计划");
            Assert.That(aiViewOfPlayer.VisiblePlans.Any(p => p.ActionSpecId == heroPlan.ActionSpecId.Value),
                Is.False, "不得通过 ActionSpecId 泄露玩家计划");
            Assert.That(aiViewOfPlayer.VisiblePlans.Any(
                    p => p.PrimaryTargetUnitId == Task09A2Fixture.Monster.Value),
                Is.False, "不得通过 PrimaryTarget 泄露玩家计划");
            Assert.That(aiViewOfPlayer.VisibleUnits.Count, Is.EqualTo(canonical.VisibleUnits.Count),
                "单位面不裁剪（存活/位置/血量是玩法公开事实，裁剪会让关系分类随视角漂移）");
            Assert.That(aiViewOfPlayer.ControlledUnitIds.Select(u => u.Value).ToArray(),
                Is.EqualTo(new[] { Task09A2Fixture.Monster.Value }),
                "控制权投影只放自己的单位");
            Assert.That(aiViewOfPlayer.OwnWindow, Is.Null, "mon 不拥有世界 A 的窗口");
            Assert.That(playerSnapshot.OwnWindow, Is.Not.Null, "hero 拥有世界 A 的窗口");
            // —— 世界 B（反向）：AI（mon）拥有一条仍未公开的 Editable 计划 ——
            var aiWorld = new SnapshotWorld(ownUnit: Task09A2Fixture.Monster);
            aiWorld.ScheduleOwnWindow(0L);
            Task09A2Fixture.Step(aiWorld.Sim, 0L);
            WindowId aiWindow = aiWorld.Sim.CurrentTurnWindow.WindowId;

            Task09A2Fixture.Submit(aiWorld.Sim, Task09A2Fixture.AiId, Task09A2Fixture.AddPlan(
                1L, aiWorld.Sim.ScheduleAuthority.ScheduleRevision, aiWindow,
                Task09A2Fixture.Monster, AttackSpec(), Task09A2Fixture.Hero, 40L));
            Task09A2Fixture.Step(aiWorld.Sim, 1L);

            ActionPlan monsterPlan = aiWorld.Sim.ScheduleAuthority.Registry.ActivePlans.Single();
            Assert.That(monsterPlan.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Monster),
                "夹具前提：计划属于 mon（AI 控制的单位）");
            Assert.That(monsterPlan.IsEditable, Is.True, "夹具前提：AI 自己的计划此刻仍是 Editable");

            DecisionSnapshot aiCanonical = aiWorld.CanonicalAt(1L);
            DecisionSnapshot aiSnapshot = FilterFor(aiWorld.Sim, aiCanonical,
                Task09A2Fixture.AiId, Task09A2Fixture.Monster, aiWindow);
            DecisionSnapshot playerViewOfAi = FilterFor(aiWorld.Sim, aiCanonical,
                Task09A2Fixture.PlayerId, Task09A2Fixture.Hero, aiWindow);

            Assert.That(aiSnapshot.EditablePlansOf(Task09A2Fixture.AiId)
                    .Select(p => p.ActionPlanId).ToArray(),
                Is.EqualTo(new[] { monsterPlan.ActionPlanId.Value }),
                "AI 必须能看到并编辑自己仍为 Editable 的计划");
            Assert.That(aiSnapshot.VisiblePlans.Select(Describe).ToArray(), Is.Empty,
                "AI 自己未公开的计划仍然不进入公开面（公开面只放已锁定/已公开者）；实测=" +
                string.Join("|", aiSnapshot.VisiblePlans.Select(p =>
                    Describe(p) + "@ctrl=" + aiSnapshot.ControllerId.Value + " own=" + aiSnapshot.OwnPlans.Count +
                    " ctrlUnits=" + aiSnapshot.ControlledUnitIds.Count +
                    " start=" + p.StartTick + " origin=" + p.Origin)));
            Assert.That(playerViewOfAi.VisiblePlans.Select(Describe).ToArray(), Is.Empty,
                "过滤必须是双向的：玩家也看不到 AI 未公开的 Editable 计划");
            Assert.That(playerViewOfAi.OwnPlans, Is.Empty);
            Assert.That(playerViewOfAi.EditablePlansOf(Task09A2Fixture.PlayerId), Is.Empty);

            // —— 按玩法公开后必须重新可见（过滤不是"一律隐藏"）——
            BattleSimulation locked = LockedAttackWorldWithSnapshot(out DecisionSnapshot lockedCanonical);
            DecisionSnapshot lockedAiView = FilterFor(locked, lockedCanonical, Task09A2Fixture.AiId,
                Task09A2Fixture.Monster, locked.CurrentTurnWindow.WindowId);
            Assert.That(lockedAiView.VisiblePlans.Count(p => p.State != (int)ActionPlanState.Editable),
                Is.EqualTo(1),
                "已锁定并按玩法公开的计划必须重新出现在 AI 的可见面里");
            Assert.That(lockedAiView.VisiblePlans.Single().ActionSpecId, Is.EqualTo(AttackSpec().Value),
                "公开后 ActionSpec 是可见事实（那是反应权的依据）");
            Assert.That(lockedAiView.VisiblePlans.Single().PrimaryTargetUnitId,
                Is.EqualTo(Task09A2Fixture.Monster.Value),
                "公开后目标也是可见事实");

            // —— Canonical 哈希面不受过滤影响 ——
            Assert.That(locked.CurrentSnapshot.Plans.Count(p => p.State != (int)ActionPlanState.Editable),
                Is.EqualTo(1), "LogicSnapshot 仍含全部权威状态（两者分型，不合并）");
        }

        /// <summary>
        /// 一个"某个单位拥有窗口"的最小世界：它带一个只记录 Canonical 快照的观察者，
        /// 因此能在任意 Tick 取到未被过滤的权威视图（过滤动作由用例显式调用）。
        /// </summary>
        private sealed class SnapshotWorld
        {
            private readonly List<DecisionSnapshot> _observed = new List<DecisionSnapshot>();

            public SnapshotWorld(UnitId ownUnit)
            {
                OwnUnit = ownUnit;
                var assembly = new BattleSimulationAssembly(
                    decisionObservers: new IDecisionObserver[] { new SnapshotObserver(_observed) });
                Sim = Task09A2Fixture.NewSim(assembly: assembly);
            }

            public UnitId OwnUnit { get; }
            public BattleSimulation Sim { get; }

            public void ScheduleOwnWindow(long tick)
                => Sim.WindowManager.ScheduleWindow(tick, OwnUnit, Task09A2Fixture.WindowBudget);

            public DecisionSnapshot CanonicalAt(long tick)
            {
                Assert.That(_observed.Any(s => s.Tick == tick), Is.True,
                    "夹具前提：Tick " + tick + " 必须已经投递过 Canonical 决策快照");
                return _observed.Last(s => s.Tick == tick);
            }
        }

        // ================= 助手 =================

        private static BattleSimulation NewAiSim(
            BattleDefinition definition,
            out AiControllerLogic ai,
            out List<DecisionSnapshot> delivered,
            out List<CommandIngressRejection> rejections)
        {
            AiControllerLogic controller = new AiControllerLogic(Task09A2Fixture.Inputs.InitialRngSeed);
            controller.RegisterController(AiBindingOf(definition));

            var observations = new List<DecisionSnapshot>();
            var forwarding = new ForwardingObserver(controller, observations);
            var assembly = new BattleSimulationAssembly(
                decisionObservers: new IDecisionObserver[] { forwarding },
                aiRuntimeStates: controller);

            BattleSimulation sim = Task09A2Fixture.NewSim(definition, assembly);
            controller.AttachPorts(sim.AiReactionOpportunities, sim.AiActionPlanLookup, sim.MovementPathCalculator);
            ai = controller;
            delivered = observations;
            rejections = new List<CommandIngressRejection>(controller.IngressRejections);
            return sim;
        }

        private static ControllerBinding AiBindingOf(BattleDefinition definition)
        {
            EncounterDefinition encounter = definition.FindEncounter(Task09A2Fixture.EncounterId);
            Assert.That(encounter, Is.Not.Null, "夹具前提：定义里必须有该 Encounter");
            foreach (ControllerBinding binding in encounter.Controllers)
            {
                if (binding.SourceKind == CommandSourceKind.Ai) return binding;
            }
            Assert.Fail("夹具前提：定义里必须有一条 Ai 绑定");
            return null;
        }

        /// <summary>把 Canonical 快照按某个 Controller 过滤（过滤入口就是被测的那一个）。</summary>
        private static DecisionSnapshot FilterFor(
            BattleSimulation sim, DecisionSnapshot canonical, ControllerId controllerId,
            UnitId unitId, WindowId windowId)
            => canonical.ForController(
                controllerId,
                new List<UnitId> { unitId },
                OwnWindowSnapshotOf(sim, windowId, unitId));

        private static TurnWindowSnapshot OwnWindowSnapshotOf(
            BattleSimulation sim, WindowId windowId, UnitId unitId)
        {
            TurnWindow window = sim.CurrentTurnWindow;
            if (window == null || window.WindowId.Value != windowId.Value) return null;
            if (window.OwnerUnitId.Value != unitId.Value) return null;
            return new TurnWindowSnapshot(
                window.WindowId.Value, window.OwnerUnitId.Value, window.OpenedAtTick,
                window.TotalBudgetTicks, window.ReservedBudgetTicks, window.SpentBudgetTicks,
                window.AvailableBudgetTicks, window.IsOpen, window.IsAcceptingSubmissions,
                (int)(window.CloseReason ?? TurnWindowCloseReason.OwnerRequested),
                Array.Empty<TurnWindowReservationSnapshot>());
        }

        /// <summary>
        /// "玩家攻击已锁定 + 决策快照已投递"的世界（过滤的公开面证据）。
        /// 它必须带一个诊断观察者，否则阶段 18 不会投递任何决策快照。
        /// </summary>
        private static BattleSimulation LockedAttackWorldWithSnapshot(out DecisionSnapshot canonical)
        {
            var probe = new Task09A2Fixture.CanonicalSnapshotProbe();
            BattleSimulation sim = Task09A2Fixture.NewSim(assembly: probe.Assembly);
            Task09A2Fixture.ArrangeStartedAttackOnly(sim, Task09A2Fixture.Monster);
            Assert.That(sim.ReactionOpportunities.ActiveOpportunities.Count, Is.GreaterThan(0),
                "夹具前提：锁定后的攻击必须公开反应机会（那条机会本身也是公开事实）");
            canonical = probe.Last;
            Assert.That(canonical, Is.Not.Null, "夹具前提：必须已经投递过决策快照");
            return sim;
        }

        private static ulong HashWith(
            LogicSnapshot source, string controllerId, long nextThinkTick, long decisionCount,
            long lastDecisionTick, RngSnapshot rng, bool extra = false)
        {
            var controllers = new List<AiControllerSnapshot>
            {
                new AiControllerSnapshot(controllerId, nextThinkTick, decisionCount, lastDecisionTick, rng)
            };
            if (extra)
            {
                controllers.Add(new AiControllerSnapshot("controller.ai.extra", 0L, 0L, -1L, null));
            }
            return HashWithControllers(source, controllers);
        }

        /// <summary>用同一份权威状态 + 指定的 AI 集合重算摘要（其余字段逐字复刻真实快照）。</summary>
        private static ulong HashWithControllers(
            LogicSnapshot source, IReadOnlyList<AiControllerSnapshot> controllers)
            => new LogicSnapshot(
                tick: source.Tick,
                rulesVersion: source.RulesVersion,
                battleDefinitionHash: source.BattleDefinitionHash,
                encounterId: source.EncounterId,
                battleEnd: source.BattleEnd,
                units: source.Units,
                effects: source.Effects,
                windowManager: source.WindowManager,
                concurrentAction: source.ConcurrentAction,
                resources: source.Resources,
                scheduleRevision: source.ScheduleRevision,
                plans: source.Plans,
                reactionOpportunities: source.ReactionOpportunities,
                actorLanes: source.ActorLanes,
                intents: source.Intents,
                movementSegments: source.MovementSegments,
                reservations: source.Reservations,
                aiControllers: controllers,
                commandIngresses: source.CommandIngresses,
                rng: source.Rng,
                nextUnitId: source.NextUnitId,
                nextActionPlanId: source.NextActionPlanId,
                nextReactionOpportunityId: source.NextReactionOpportunityId,
                nextWindowId: source.NextWindowId,
                nextEffectId: source.NextEffectId,
                nextCommandSequence: source.NextCommandSequence,
                nextIntentSequence: source.NextIntentSequence,
                nextResolutionSequence: source.NextResolutionSequence,
                nextEventSequence: source.NextEventSequence,
                nextEffectSequence: source.NextEffectSequence,
                history: source.History,
                commandSourcePriorityMappingVersion: source.CommandSourcePriorityMappingVersion,
                terminalPlanRecordCount: source.TerminalPlanRecordCount,
                terminalPlanDigest: source.TerminalPlanDigest,
                nextReactionOptionSequence: source.NextReactionOptionSequence,
                conflictGroups: source.ConflictGroups,
                contacts: source.Contacts).ComputeHash();

        private static void AssertDifferent(ulong baseline, string field, ulong candidate)
            => Assert.That(candidate, Is.Not.EqualTo(baseline),
                "AI 快照字段 " + field + " 必须参与规范化哈希（改动它必须改变摘要）");

        private static string Describe(ActionPlanSnapshot plan)
            => plan == null
                ? "<null>"
                : plan.ActionPlanId.ToString(CultureInfo.InvariantCulture) + ":" +
                  (plan.ActionSpecId ?? "<null>") + ":" + plan.State;

        /// <summary>
        /// 转发 AI 的过滤视图给 <see cref="AiControllerLogic"/>，并记录投递给它的快照。
        /// 它是装配胶水，<strong>不是</strong>被测实现。
        /// </summary>
        private sealed class ForwardingObserver : IDecisionObserver
        {
            private readonly AiControllerLogic _controller;
            private readonly List<DecisionSnapshot> _observed;

            public ForwardingObserver(AiControllerLogic controller, List<DecisionSnapshot> observed)
            {
                _controller = controller;
                _observed = observed;
            }

            public ControllerId ObserverControllerId(DecisionSnapshot snapshot)
                => _controller.ObserverControllerId(snapshot);

            public void ObserveOrdered(DecisionSnapshot snapshot, CommandIngressRegistry ingress, long nextTick)
            {
                _observed.Add(snapshot);
                _controller.ObserveOrdered(snapshot, ingress, nextTick);
            }
        }

        /// <summary>只记录 Canonical 视角快照的诊断观察者（不声明 Controller）。</summary>
        private sealed class SnapshotObserver : IDecisionObserver
        {
            private readonly List<DecisionSnapshot> _observed;

            public SnapshotObserver(List<DecisionSnapshot> observed) => _observed = observed;

            public void ObserveOrdered(DecisionSnapshot snapshot, CommandIngressRegistry ingress, long nextTick)
                => _observed.Add(snapshot);
        }
    }
}
