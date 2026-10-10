using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 07 <strong>预算三段式事务</strong>（「必须产出」4/10/12/13）的模拟级证据：
    /// <c>Available -&gt; Reserved -&gt; Spent</c> 的每一次迁移都发生在<strong>生产对象</strong>上——
    /// 真实 <see cref="ActionScheduleAuthority"/> + <see cref="ActionPlanFactory"/> +
    /// <see cref="ScheduleEditor"/>（含真实 <see cref="LogicGridMovementAuthority"/> 空间端口）、
    /// 真实 <see cref="TurnWindowManager"/> + <see cref="TurnWindowBudgetAuthority"/>
    /// （它同时是 <see cref="IActionPlanStartCommitPort"/> 的真实实现）、
    /// 真实 <see cref="ActionStartGate"/> 与唯一 <see cref="ActionPlanTerminalCoordinator"/>
    /// （含固定槽位 600 的 <see cref="BudgetAndAdrenalineCleanupParticipant"/>）。
    ///
    /// 装配风格与 <c>Task06AssemblyIntegrationTests</c> / <c>Task07ForcedDisplacementBudgetTests</c>
    /// 一致：不建立第二套预算模型、不复制生产算法，只把生产类按 <c>BattleSimulation</c> 的顺序接起来，
    /// 然后驱动它们。断言的观察点全部是权威账本（<see cref="TurnWindow"/> 的
    /// <c>Reserved/Spent/Available/Reservations</c>）、权威计划字段、权威注册表/Lane、
    /// 以及生产端口发射的 <see cref="TurnBudgetChange"/> 载荷。
    ///
    /// <strong>本文件不构造 <c>BattleSimulation</c></strong>，原因有两条，都必须如实记录：
    /// <list type="number">
    /// <item><c>ProjectHero.Logic.Tests</c> 没有 <c>InternalsVisibleTo</c>，
    /// 而自动延期的唯一事务入口 <c>ScheduleEditor.ApplySystemAutoDeferral</c> 目前是 <c>internal</c>，
    /// 只能经 <c>BattleSimulation</c> 到达；</item>
    /// <item>当前工作区里 <c>BattleSimulation</c> 从未把 <c>Encounter.Controllers</c> 的
    /// <c>ControllerBinding</c> 注册进 <c>TurnWindowManager</c>（见下方 §已知生产接缝），
    /// 因此生产装配下<strong>任何</strong>带账本效果的显式排程编辑都会被
    /// <c>TurnWindowCodes.ISSUER_CANNOT_CONTROL_UNIT</c> 拒绝；用它当夹具不可能得到绿色用例。
    /// </item>
    /// </list>
    /// 因此本文件用反射<strong>只调用真实事务方法 / 只写两个生命周期标记</strong>
    /// （见 §反射点说明），不伪造任何预算事实。
    /// </summary>
    public class Task07BudgetTransactionTests
    {
        // ================= 夹具常量 =================

        private const string MoveSpecId = "action.t07bt.move";
        private const string GuardSpecId = "action.t07bt.guard";
        private const string AttackSpecId = "action.t07bt.attack";
        private const string BlockSpecId = "action.t07bt.block";
        private const string DodgeSpecId = "action.t07bt.dodge";

        /// <summary>单位速度恒等于基准速度（20）⇒ 前摇/每权重单位 Tick 的解析是恒等映射。</summary>
        private static readonly BattleRules Rules = BattleRules.FrozenV1;

        private const int MoveBaseStepTicks = 5;
        private const int MoveRecoveryTicks = 5;
        private const int GuardWindupTicks = 10;
        private const int GuardActiveTicks = 20;
        private const int GuardRecoveryTicks = 5;
        private const int GuardCost = GuardWindupTicks + GuardActiveTicks + GuardRecoveryTicks;   // 35
        private const int AttackBaseWindupTicks = 100;
        private const int AttackRecoveryTicks = 10;

        private static readonly GridBoundaryDefinition Wide =
            new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40));

        private static readonly UnitId Hero = new UnitId(1L);
        private static readonly UnitId Enemy = new UnitId(2L);
        private static readonly FactionId HeroFaction = new FactionId("faction.hero");
        private static readonly FactionId MonsterFaction = new FactionId("faction.monster");
        private static readonly ControllerId Player = new ControllerId("controller.player");
        private static readonly ControllerId EnemyAi = new ControllerId("controller.enemy_ai");

        private static UnitId U(long id) => new UnitId(id);
        private static ActionPlanId P(long id) => new ActionPlanId(id);
        private static WindowId W(long id) => new WindowId(id);
        private static ActionSpecId Spec(string id) => new ActionSpecId(id);

        // ================= 定义 =================

        private static IReadOnlyList<DirectionalTriangleSet> Directions()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        private static MovementPatternSpec MovementPattern()
            => new MovementPatternSpec(new MovementPatternId("movement_pattern.t07bt"), Directions());

        private static BattleDefinition BuildDefinition()
        {
            var factionModel = new FactionModelDefinition(
                new List<FactionDefinition>
                {
                    new FactionDefinition(HeroFaction),
                    new FactionDefinition(MonsterFaction)
                },
                new List<FactionRelationDefinition>
                {
                    new FactionRelationDefinition(HeroFaction, MonsterFaction, FactionDisposition.Hostile)
                });

            var moveSpec = new ActionSpec(
                Spec(MoveSpecId), ActionType.Move,
                new MoveTimingSpec(BaseStepTicks: MoveBaseStepTicks, RecoveryTicks: MoveRecoveryTicks),
                new MovePayloadSpec(200, MovementPattern()),
                AdrenalineCost: 0);

            var guardSpec = new ActionSpec(
                Spec(GuardSpecId), ActionType.Guard,
                new GuardTimingSpec(GuardWindupTicks, GuardActiveTicks, GuardRecoveryTicks),
                new GuardPayloadSpec(
                    new Dictionary<DamageChannelId, int>(), 512,
                    DefenseTagMask.Blockable | DefenseTagMask.Guardable),
                AdrenalineCost: 0);

            // 长前摇攻击：StartTick + 100 = ImpactTick ⇒ 反应选项截止 Tick = ImpactTick - 60，
            // 远晚于 TelegraphTick + CommandIngressLeadTicks(2)，因此 Block/Dodge 都被公开。
            var attackSpec = new ActionSpec(
                Spec(AttackSpecId), ActionType.Attack,
                new AttackTimingSpec(AttackBaseWindupTicks, AttackRecoveryTicks),
                new AttackPayloadSpec(
                    null, default, 1f, TargetPolicy.PrimaryTargetOnly,
                    TargetRelationMask.Hostile, 0, null,
                    AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable),
                AdrenalineCost: 0);

            var blockSpec = new ActionSpec(
                Spec(BlockSpecId), ActionType.Block,
                new BlockReactionTimingSpec(FrozenDesignValues.BlockReactionWindupTicks, 10),
                new BlockPayloadSpec(DefenseTagMask.Blockable),
                AdrenalineCost: FrozenDesignValues.BlockAdrenalineCost);

            var dodgeSpec = new ActionSpec(
                Spec(DodgeSpecId), ActionType.Dodge,
                new DodgeReactionTimingSpec(FrozenDesignValues.DodgeReactionWindupTicks, 10),
                new DodgePayloadSpec(MaxDistanceSteps: 4, Pattern: MovementPattern()),
                AdrenalineCost: FrozenDesignValues.DodgeAdrenalineCost);

            var volume = new VolumeSpec(new VolumeSpecId("unit_volume.t07bt"), Directions());
            var actionSet = new ActionSetDefinition(
                new ActionSetId("action_set.t07bt"),
                new List<ActionSpecId>
                {
                    moveSpec.ActionSpecId, guardSpec.ActionSpecId, attackSpec.ActionSpecId,
                    blockSpec.ActionSpecId, dodgeSpec.ActionSpecId
                });

            var units = new List<UnitDefinition>
            {
                new UnitDefinition(new UnitDefinitionId("unit.t07bt.hero"), 10f, 10f,
                    Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), 200f,
                    actionSet.ActionSetId, volume.VolumeSpecId),
                new UnitDefinition(new UnitDefinitionId("unit.t07bt.enemy"), 10f, 10f,
                    Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), 200f,
                    actionSet.ActionSetId, volume.VolumeSpecId)
            };

            return new BattleDefinition(
                "battle-definition.task07.budget-transaction",
                Rules.TicksPerSecond,
                Rules,
                ConcurrentActionDefinition.FrozenV1,
                new ReactionRules(CommandIngressLeadTicks: 2, MinimumReactionLeadTicks: 1),
                AdrenalineRules.FrozenV1,
                factionModel,
                new List<DamageChannelDefinition>(),
                new List<ImpactProfileDefinition>(),
                units,
                new List<ActionSpec> { moveSpec, guardSpec, attackSpec, blockSpec, dodgeSpec },
                new List<AttackPatternSpec>(),
                new List<VolumeSpec> { volume },
                new List<MovementPatternSpec> { MovementPattern() },
                new List<ActionSetDefinition> { actionSet },
                new List<StatusEffectSpec>(),
                new List<EncounterDefinition>(),
                null,
                "test-definition-hash.t07.budget-transaction");
        }

        private sealed class Facts : IActionPlanFactsSource
        {
            private readonly Dictionary<long, ActionPlanOwnerFacts> _map =
                new Dictionary<long, ActionPlanOwnerFacts>();

            public void Add(UnitId unitId, FactionId factionId, ActionSetId actionSetId)
                => _map[unitId.Value] = new ActionPlanOwnerFacts(
                    unitId, factionId, Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed, actionSetId, true);

            public bool TryGetOwnerFacts(UnitId unitId, out ActionPlanOwnerFacts facts)
                => _map.TryGetValue(unitId.Value, out facts);
        }

        private sealed class WorldView : ITurnWindowWorldView
        {
            public readonly HashSet<long> DeadUnits = new HashSet<long>();
            public bool Ended;

            public bool IsUnitAliveForWindow(UnitId unitId) => !DeadUnits.Contains(unitId.Value);

            public bool IsBattleEnded => Ended;
        }

        // ================= 装配夹具 =================

        private sealed class Rig
        {
            public BattleDefinition Definition;
            public Facts Facts;
            public LogicGrid Grid;
            public ActionScheduleAuthority Schedule;
            public LogicIdGenerator Ids;
            public ActionPlanFactory Factory;
            public LogicGridMovementAuthority Movement;
            public LogicGridMovementPathCalculator Calculator;
            public ScheduleEditor Editor;
            public TurnWindowManager Windows;
            public WorldView World;
            public TurnWindowBudgetAuthority Budget;
            public BudgetAndAdrenalineCleanupParticipant BudgetParticipant;
            public ActionPlanTerminalCoordinator Coordinator;
            public AdrenalineLedgerRegistry Adrenaline;
            public ReactionOpportunitySystem Reactions;
            public ActionStartGate Gate = new ActionStartGate();

            /// <summary>生产预算端口发射的账本变化载荷（逐次迁移的前后值都在这里）。</summary>
            public readonly List<TurnBudgetChange> Ledger = new List<TurnBudgetChange>();

            /// <summary>肾上腺素账本变化载荷。</summary>
            public readonly List<AdrenalineLedgerChange> AdrenalineChanges = new List<AdrenalineLedgerChange>();

            /// <summary>
            /// 启动门禁的只读事实：单位当前状态是否构成<strong>有限</strong>阻塞
            /// （生产来源是 <c>UnitStateMachine.BlockingUntilTick</c>）。null = 无阻塞。
            /// </summary>
            public long? BlockingUntilTick;

            public bool IsAlive = true;

            /// <summary>启动门禁的无空间阻塞事实（生产来源是 <c>LogicGridMovementAuthority.SpaceBlockUntilTickOf</c>）。</summary>
            public long? SpaceBlockUntilTick;
        }

        private static IFactionRelationResolver BuildResolver(BattleDefinition definition)
        {
            var unitFactions = new Dictionary<UnitId, FactionId>
            {
                [Hero] = HeroFaction,
                [Enemy] = MonsterFaction
            };
            return new FactionRelationResolver(definition.FactionModel, unitFactions);
        }

        private static Rig NewRig()
        {
            BattleDefinition definition = BuildDefinition();
            var actionSetId = new ActionSetId("action_set.t07bt");
            var facts = new Facts();
            facts.Add(Hero, HeroFaction, actionSetId);
            facts.Add(Enemy, MonsterFaction, actionSetId);

            var schedule = new ActionScheduleAuthority();
            var ids = new LogicIdGenerator();
            var factory = new ActionPlanFactory(definition, BuildResolver(definition), facts, ids);
            var grid = new LogicGrid(Wide);
            var movement = new LogicGridMovementAuthority(
                grid, schedule, definition.Rules.PathCostRules, definition.Rules.PathSearchRules);
            var calculator = new LogicGridMovementPathCalculator(
                grid, schedule, definition.Rules.PathCostRules, definition.Rules.PathSearchRules);
            var editor = new ScheduleEditor(schedule, factory, ids, calculator);
            editor.MovementSpacePort = movement;

            var world = new WorldView();
            var windows = new TurnWindowManager(world, ids.NextWindowId);
            var budget = new TurnWindowBudgetAuthority(windows, null);
            var adrenaline = new AdrenalineLedgerRegistry(definition.AdrenalineRules);
            var participant = new BudgetAndAdrenalineCleanupParticipant();
            var coordinator = new ActionPlanTerminalCoordinator(schedule, new IActionPlanCleanupParticipant[]
            {
                StopSchedulingCleanupParticipant.Instance,
                movement,
                participant
            });
            var reactions = new ReactionOpportunitySystem(
                schedule, factory, definition, BuildResolver(definition), ids, null, coordinator);
            reactions.AdrenalinePort = adrenaline;

            var rig = new Rig
            {
                Definition = definition,
                Facts = facts,
                Grid = grid,
                Schedule = schedule,
                Ids = ids,
                Factory = factory,
                Movement = movement,
                Calculator = calculator,
                Editor = editor,
                Windows = windows,
                World = world,
                Budget = budget,
                BudgetParticipant = participant,
                Coordinator = coordinator,
                Adrenaline = adrenaline,
                Reactions = reactions
            };

            budget.ChangedSink = rig.Ledger.Add;
            adrenaline.ChangedSink = rig.AdrenalineChanges.Add;
            participant.Bind(budget, windows, adrenaline);

            // 排程事务的预算上下文 = 生产装配的同一形状（BattleSimulation 构造函数里的那个闭包）。
            editor.BudgetContextFactory = (source, targetTick, expectedWindowId, issuer) =>
                new ScheduleBudgetContext(
                    source, budget, expectedWindowId,
                    windows.CurrentWindow != null ? windows.CurrentWindow.WindowId : (WindowId?)null,
                    issuer);

            // 提交授权矩阵（定义级控制权）：本文件聚焦预算三段式事务，
            // 因此预算端口按"任务 05/06 装配"语义创建（submissionAuthority = null ⇒ 由调用方保证提交权）；
            // 这里仍然登记控制权，使夹具与生产装配同形，且租户身份在断言里可用。
            windows.RegisterControllerBinding(Player, Hero);
            windows.RegisterControllerBinding(EnemyAi, Enemy);

            Assert.That(grid.RegisterUnit(Hero, new GridPoint(0, 0), GridDirection.East, Directions()),
                Is.Null, "夹具前提：hero 必须落在 (0,0)");
            Assert.That(grid.RegisterUnit(Enemy, new GridPoint(0, 30), GridDirection.East, Directions()),
                Is.Null, "夹具前提：enemy 必须落在 (0,30)");

            adrenaline.Register(Hero, 0, 0L);
            adrenaline.Register(Enemy, 0, 0L);
            return rig;
        }

        // ================= 夹具动作 =================

        private static WindowId? CurrentWindowId(Rig rig)
            => rig.Windows.CurrentWindow != null ? rig.Windows.CurrentWindow.WindowId : (WindowId?)null;

        /// <summary>
        /// <strong>只测预算差额、不接空间端口</strong>的装配变体：它保留真实的求值器与真实
        /// <see cref="LogicGridMovementPathCalculator"/>（路径/权重/成本全部真实重算），
        /// 但**不**注入 <c>MovementSpacePort</c>。
        ///
        /// 为什么需要它：本文件需要"Editable Move 的路径成本在事务中变化"这一事实，而目前
        /// 唯一可达的成因是<strong>移动链</strong>（第二个移动计划的预测起点 = 前一计划的目的格）。
        /// <c>LogicGridMovementAuthority.RebuildMovementSpace</c> 在链上会把"链内起点"的路径交给
        /// 断言"路径首点 == 单位当前锚点"的 <c>EstablishMovement</c>，因此**任何**链式候选都会被
        /// <c>MOVEMENT_SEGMENT_ORIGIN_MISMATCH</c> 拒绝（见简报中的独立缺陷登记）。
        /// 该缺陷属于任务 06 的空间端口，不属于本文件被测的预算事务，因此这里按
        /// <c>ScheduleEditor.MovementSpacePort</c> 文档化的"未接缝 = 安全无操作（排程与空间解耦）"
        /// 语义把它显式留空：被测的预算三段式事务、路径重算与差额调整全部仍然是生产代码。
        /// </summary>
        private static Rig NewChainRig()
        {
            Rig rig = NewRig();
            rig.Editor.MovementSpacePort = null;
            return rig;
        }

        /// <summary>模拟 Step 入口冻结批次边界（乐观并发的唯一状态）。</summary>
        private static void BeginTick(Rig rig, long tick)
            => rig.Editor.BeginBatch(tick, rig.Schedule.ScheduleRevision);

        private static ScheduleEditTransactionResult Apply(
            Rig rig, long tick, WindowId? expectedWindow, ControllerId issuer,
            params ScheduleEditOperation[] operations)
        {
            BeginTick(rig, tick);
            return rig.Editor.Apply(
                operations, tick,
                batchBaseScheduleRevision: rig.Schedule.ScheduleRevision,
                expectedScheduleRevision: rig.Schedule.ScheduleRevision,
                preview: false,
                expectedWindowId: expectedWindow,
                issuer: issuer);
        }

        private static ActionPlan AddOrFail(
            Rig rig, long tick, WindowId? expectedWindow, ScheduleEditOperation operation)
        {
            ScheduleEditTransactionResult result = Apply(rig, tick, expectedWindow, Player, operation);
            Assert.That(result.RejectionCode, Is.Null, "夹具前提：排程事务必须成功（" + result.RejectionCode + "）");
            Assert.That(result.Committed, Is.True);
            Assert.That(result.AddedPlanIds.Count, Is.EqualTo(1));
            ActionPlan plan = rig.Schedule.Registry.Find(result.AddedPlanIds[0]);
            Assert.That(plan, Is.Not.Null, "新增计划必须进入权威注册表");
            return plan;
        }

        /// <summary>经真实排程事务新增普通 Guard（预算由任务 07 的账本端口原子预留）。</summary>
        private static ActionPlan AddGuard(
            Rig rig, long tick, long startTick, UnitId owner = default(UnitId), WindowId? expectedWindow = null)
            => AddOrFail(rig, tick, expectedWindow ?? CurrentWindowId(rig),
                new AddOrdinaryPlanOperation(
                    startTick + 1000L, owner.IsValid ? owner : Hero, Spec(GuardSpecId), startTick,
                    AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                    Facing: GridDirection.East, Destination: null));

        /// <summary>经真实排程事务新增普通 Move（路径投影与预算都由生产代码重算）。</summary>
        private static ActionPlan AddMove(
            Rig rig, long tick, long startTick, int toX, int toY,
            UnitId owner = default(UnitId), WindowId? expectedWindow = null)
            => AddOrFail(rig, tick, expectedWindow ?? CurrentWindowId(rig),
                new AddOrdinaryPlanOperation(
                    startTick + 2000L, owner.IsValid ? owner : Hero, Spec(MoveSpecId), startTick,
                    AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                    Facing: GridDirection.East, Destination: new GridPoint(toX, toY)));

        /// <summary>经真实排程事务新增普通 Attack（PrimaryTargetOnly + Hostile 关系）。</summary>
        private static ActionPlan AddAttack(Rig rig, long tick, long startTick, UnitId owner, UnitId target)
            => AddOrFail(rig, tick, CurrentWindowId(rig),
                new AddOrdinaryPlanOperation(
                    startTick + 3000L, owner, Spec(AttackSpecId), startTick,
                    AnchorAfterPlanId: default, PrimaryTargetUnitId: target,
                    Facing: GridDirection.East, Destination: null));

        private static TurnWindow OpenWindow(Rig rig, long tick, UnitId owner, int budget)
        {
            rig.Windows.ScheduleWindow(tick, owner, budget);
            TurnWindow window = rig.Windows.TryOpenDueWindow(tick);
            Assert.That(window, Is.Not.Null, "夹具前提：tick " + tick + " 应当打开窗口");
            Assert.That(window.OwnerUnitId, Is.EqualTo(owner));
            Assert.That(window.TotalBudgetTicks, Is.EqualTo(budget));
            return window;
        }

        private static void CloseWindow(Rig rig, TurnWindow window, long tick)
        {
            window.RequestClose(TurnWindowCloseReason.OwnerRequested);
            Assert.That(rig.Windows.FinalizeRequestedClose(tick), Is.SameAs(window));
            Assert.That(window.IsOpen, Is.False);
            Assert.That(window.IsAcceptingSubmissions, Is.False);
        }

        /// <summary>生产启动门禁决策（纯函数；只读事实来自夹具）。</summary>
        private static ActionStartGateResult EvaluateGate(Rig rig, ActionPlan plan, long tick)
        {
            var context = new ActionStartGateContext(
                tick,
                _ => rig.BlockingUntilTick,
                _ => rig.IsAlive,
                rig.Factory,
                rig.Definition.Rules.MaxAutomaticDeferralsPerPlan,
                isActorAvailable: null,
                spaceBlockUntilTickOf: _ => rig.SpaceBlockUntilTick);
            return rig.Gate.Evaluate(plan, context);
        }

        /// <summary>
        /// 经<strong>生产</strong>启动提交端口（<see cref="TurnWindowBudgetAuthority.Commit"/>，
        /// 即 <c>IActionPlanStartCommitPort</c> 的真实实现）完成 <c>Reserved -&gt; Spent</c>，
        /// 并按生产 <c>BattleSimulation.CommitStart</c> 的同一原子提交语义写入
        /// <c>LockedAtTick</c> 与 <c>Running</c>（见 §反射点说明）。
        /// </summary>
        private static void StartAndLock(Rig rig, ActionPlan plan, long tick)
        {
            Assert.That(plan.IsEditable, Is.True, "夹具前提：只有 Editable 计划需要启动提交");
            int cost = plan.BudgetCostTicks;
            Assert.That(cost, Is.GreaterThan(0), "夹具前提：普通计划成本必须为正整数 Tick");

            string error = rig.Budget.Commit(plan, tick, _ => { });
            Assert.That(error, Is.Null, "生产启动提交端口必须成功：" + error);
            MarkLockedAndRunning(plan, tick);
        }

        /// <summary>账本恒等式：每个窗口的 <c>Reserved + Spent + Available == Total</c>。</summary>
        private static void AssertLedgerIdentity(Rig rig)
        {
            var all = new List<TurnWindow>();
            if (rig.Windows.CurrentWindow != null) all.Add(rig.Windows.CurrentWindow);
            all.AddRange(rig.Windows.ClosedWindows);
            for (int i = 0; i < all.Count; i++)
            {
                TurnWindow window = all[i];
                Assert.That(window.ReservedBudgetTicks + window.SpentBudgetTicks + window.AvailableBudgetTicks,
                    Is.EqualTo(window.TotalBudgetTicks),
                    "窗口 " + window.WindowId.Value + " 的账本恒等式被破坏");
                Assert.That(window.ReservedBudgetTicks, Is.GreaterThanOrEqualTo(0));
                Assert.That(window.SpentBudgetTicks, Is.GreaterThanOrEqualTo(0));
                Assert.That(window.AvailableBudgetTicks, Is.GreaterThanOrEqualTo(0));
            }
        }

        private static int CountKind(Rig rig, TurnBudgetChangeKind kind, ActionPlanId planId)
        {
            int count = 0;
            for (int i = 0; i < rig.Ledger.Count; i++)
            {
                if (rig.Ledger[i].ChangeKind != kind) continue;
                if (rig.Ledger[i].ActionPlanId.HasValue && rig.Ledger[i].ActionPlanId.Value == planId) count++;
            }
            return count;
        }

        private static TurnBudgetChange LastKind(Rig rig, TurnBudgetChangeKind kind)
        {
            for (int i = rig.Ledger.Count - 1; i >= 0; i--)
            {
                if (rig.Ledger[i].ChangeKind == kind) return rig.Ledger[i];
            }
            Assert.Fail("不存在 " + kind + " 类账本变化");
            return null;
        }

        // =====================================================================
        // 反射点说明（本文件唯一的非公共访问，共两处）
        //
        // ① ScheduleEditor.ApplySystemAutoDeferral：系统自动延期的唯一事务入口目前是
        //    internal，而 ProjectHero.Logic.Tests 没有 InternalsVisibleTo
        //    （见 Assets/Scripts/Logic/AssemblyInfo.cs：该授权只给 ProjectHero.Authoring.Tests）。
        //    生产装配里它只被 BattleSimulation.ApplyAutoDeferral 调用，而该模拟入口在本工作区
        //    因 §已知生产接缝 无法产出任何普通计划。这里反射调用的就是那个真实事务方法
        //    （不复制它的任何逻辑、不绕过它的任何校验），因此"预算差额 / 回滚 / 重开窗口"
        //    这些判定仍然全部由生产代码做出。
        //
        // ② ActionPlan.State / LockedAtTick：这两个生命周期标记的写入面是 internal，
        //    生产路径里唯一写它们的位置是 BattleSimulation.CommitStart。
        //    这里只写这两个标记（与 Task07ForcedDisplacementBudgetTests 的同一取舍），
        //    资源侧的 Reserved -> Spent 完全由生产端口 TurnWindowBudgetAuthority.Commit 完成。
        // =====================================================================

        private static readonly MethodInfo AutoDeferralMethod = typeof(ScheduleEditor).GetMethod(
            "ApplySystemAutoDeferral",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(ActionPlan), typeof(long), typeof(long), typeof(IReadOnlyList<ActionPlanId>) },
            modifiers: null);

        private static readonly PropertyInfo StateSetter = typeof(ActionPlan).GetProperty(nameof(ActionPlan.State));

        private static readonly PropertyInfo LockedAtTickSetter =
            typeof(ActionPlan).GetProperty(nameof(ActionPlan.LockedAtTick));

        /// <summary>调用真实系统自动延期事务（见 §反射点说明 ①）。</summary>
        private static ScheduleEditTransactionResult AutoDefer(
            Rig rig, ActionPlan plan, long retryAtTick, long currentTick, IReadOnlyList<ActionPlanId> claimed = null)
        {
            Assert.That(AutoDeferralMethod, Is.Not.Null,
                "ScheduleEditor.ApplySystemAutoDeferral 必须存在（夹具不变量）");
            try
            {
                return (ScheduleEditTransactionResult)AutoDeferralMethod.Invoke(
                    rig.Editor, new object[] { plan, retryAtTick, currentTick, claimed ?? Array.Empty<ActionPlanId>() });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        /// <summary>写入"启动门禁已经成功"的两个生命周期标记（见 §反射点说明 ②）。</summary>
        private static void MarkLockedAndRunning(ActionPlan plan, long lockedAtTick)
        {
            Assert.That(StateSetter, Is.Not.Null, "ActionPlan.State 必须存在（夹具不变量）");
            Assert.That(LockedAtTickSetter, Is.Not.Null, "ActionPlan.LockedAtTick 必须存在（夹具不变量）");
            StateSetter.SetValue(plan, ActionPlanState.Running);
            LockedAtTickSetter.SetValue(plan, lockedAtTick);
            Assert.That(plan.IsRunning, Is.True, "夹具前提：标记写入必须生效");
            Assert.That(plan.LockedAtTick, Is.EqualTo(lockedAtTick));
        }

        // =====================================================================
        // 1. EditablePlanReservesBudgetWithoutSpendingIt
        // =====================================================================
        [Test]
        public void EditablePlanReservesBudgetWithoutSpendingIt()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 60);

            ActionPlan plan = AddGuard(rig, 0L, 100L);

            // 账本：整数预算从 Available 转入按计划归属的 Reserved，Spent 必须仍为 0。
            Assert.That(window.ReservedFor(plan.ActionPlanId), Is.EqualTo(GuardCost),
                "Editable 计划必须恰好预留自己的整数成本");
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(GuardCost));
            Assert.That(window.SpentBudgetTicks, Is.Zero, "Editable 阶段绝不能消费预算");
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(60 - GuardCost));
            Assert.That(window.Reservations.Count, Is.EqualTo(1));
            Assert.That(window.Reservations[0].ActionPlanId, Is.EqualTo(plan.ActionPlanId));

            // 计划侧：仍在 Editable，且自身的未消费预留投影等于成本（不是"已消费"）。
            Assert.That(plan.IsEditable, Is.True);
            Assert.That(plan.ReservedTurnBudgetTicks, Is.EqualTo(GuardCost));
            Assert.That(plan.LockedAtTick, Is.LessThanOrEqualTo(0L), "Editable 计划不可能有锁定 Tick");
            Assert.That(plan.SubmittedWindowId, Is.EqualTo(window.WindowId), "预算来源窗口必须是提交时的当前窗口");

            // 事件：恰好一条 Reserved，且前后值可审计、源为显式排程编辑。
            Assert.That(CountKind(rig, TurnBudgetChangeKind.Reserved, plan.ActionPlanId), Is.EqualTo(1));
            TurnBudgetChange change = LastKind(rig, TurnBudgetChangeKind.Reserved);
            Assert.That(change.ChangeKind, Is.EqualTo(TurnBudgetChangeKind.Reserved));
            Assert.That(change.Source, Is.EqualTo(ResourceChangeSource.ExplicitScheduleEdit));
            Assert.That(change.WindowId, Is.EqualTo(window.WindowId));
            Assert.That(change.OwnerUnitId, Is.EqualTo(Hero));
            Assert.That(change.AvailableBefore, Is.EqualTo(60));
            Assert.That(change.AvailableAfter, Is.EqualTo(60 - GuardCost));
            Assert.That(change.ReservedBefore, Is.Zero);
            Assert.That(change.ReservedAfter, Is.EqualTo(GuardCost));
            Assert.That(change.SpentBefore, Is.Zero);
            Assert.That(change.SpentAfter, Is.Zero);
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ConsumedAtLock, plan.ActionPlanId), Is.Zero);

            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 2. StartableCommitConvertsReservedBudgetToSpentExactlyOnce
        // =====================================================================
        [Test]
        public void StartableCommitConvertsReservedBudgetToSpentExactlyOnce()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 60);
            ActionPlan plan = AddGuard(rig, 0L, 100L);
            Assert.That(rig.Ledger.Count, Is.EqualTo(1));

            ActionStartGateResult gate = EvaluateGate(rig, plan, 100L);
            Assert.That(gate.IsStartable, Is.True, "门禁必须判定 Startable：" + ActionStartGate.Describe(gate));

            StartAndLock(rig, plan, 100L);

            // Reserved -> Spent 恰好一次：Reserved 归零、Spent 等于成本、Available 不变。
            Assert.That(window.ReservedFor(plan.ActionPlanId), Is.Zero);
            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(GuardCost));
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(60 - GuardCost));
            Assert.That(plan.ReservedTurnBudgetTicks, Is.Zero);
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ConsumedAtLock, plan.ActionPlanId), Is.EqualTo(1));
            Assert.That(CountKind(rig, TurnBudgetChangeKind.Reserved, plan.ActionPlanId), Is.EqualTo(1),
                "启动提交绝不能产生第二次预留");

            TurnBudgetChange consumed = LastKind(rig, TurnBudgetChangeKind.ConsumedAtLock);
            Assert.That(consumed.Source, Is.EqualTo(ResourceChangeSource.StartCommit));
            Assert.That(consumed.WindowId, Is.EqualTo(window.WindowId));
            Assert.That(consumed.ReservedBefore, Is.EqualTo(GuardCost));
            Assert.That(consumed.ReservedAfter, Is.Zero);
            Assert.That(consumed.SpentBefore, Is.Zero);
            Assert.That(consumed.SpentAfter, Is.EqualTo(GuardCost));
            Assert.That(consumed.AvailableBefore, Is.EqualTo(consumed.AvailableAfter),
                "启动提交只搬移已有预留，不改变可用额度");

            // 重放同一个提交：不得二次扣费（账本与计划都不得再变）。
            string replay = rig.Budget.Commit(plan, 100L, _ => { });
            Assert.That(replay, Is.Not.Null, "预留已经不存在时重复提交必须以稳定码失败，绝不二次收费");
            Assert.That(replay, Does.StartWith(ActionStartGateCommitCodes.RESOURCE_COMMIT_ERROR));
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(GuardCost), "重复提交不得再次消费");
            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ConsumedAtLock, plan.ActionPlanId), Is.EqualTo(1));

            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 3. StartableCommitAtomicallyIncludesSpentLockedAndRunning
        // =====================================================================
        [Test]
        public void StartableCommitAtomicallyIncludesSpentLockedAndRunning()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 60);
            ActionPlan plan = AddGuard(rig, 0L, 100L);

            ActionStartGateResult gate = EvaluateGate(rig, plan, 100L);
            Assert.That(gate.IsStartable, Is.True, ActionStartGate.Describe(gate));

            // 提交端口只搬移资源，不写生命周期字段：因此"Spent + Locked + Running"必须同批出现。
            string error = rig.Budget.Commit(plan, 100L, _ => { });
            Assert.That(error, Is.Null);
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Editable),
                "端口不得自行写生命周期字段（它是资源端口，不是状态机）");
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(GuardCost));

            MarkLockedAndRunning(plan, 100L);

            // 四个事实同时成立，且都指向同一个提交 Tick。
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(GuardCost), "Spent 必须已包含该计划");
            Assert.That(plan.LockedAtTick, Is.EqualTo(100L));
            Assert.That(plan.IsRunning, Is.True);
            Assert.That(plan.IsEditable, Is.False);
            Assert.That(plan.IsLocked, Is.False, "不存在可观察的 Locked-but-not-Running 中间态");
            Assert.That(plan.StartTick, Is.EqualTo(100L));
            Assert.That(plan.ReservedTurnBudgetTicks, Is.Zero, "Running 计划不再持有未消费预留");
            Assert.That(LastKind(rig, TurnBudgetChangeKind.ConsumedAtLock).Source,
                Is.EqualTo(ResourceChangeSource.StartCommit));
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock, plan.ActionPlanId), Is.Zero,
                "锁定路径不得出现 ReleasedBeforeLock");
            AssertLedgerIdentity(rig);

            // 锁定后自然完成：Spent 不退（Locked 之后任何终态都不退款）。
            int ledgerCount = rig.Ledger.Count;
            rig.Coordinator.EnterCompletion(plan, plan.EndTick);
            Assert.That(plan.IsCompleted, Is.True);
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(GuardCost));
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(60 - GuardCost));
            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount), "完成后不得产生任何账本变化");
        }

        // =====================================================================
        // 4. FailedStartableCommitLeavesBudgetReservedAndRaisesInvariant
        // =====================================================================
        [Test]
        public void FailedStartableCommitLeavesBudgetReservedAndRaisesInvariant()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 100);
            ActionPlan doomed = AddGuard(rig, 0L, 100L);
            ActionPlan survivor = AddGuard(rig, 0L, 200L);
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(GuardCost * 2));

            // 合法序列制造"计划已没有预留"的真实形态：Editable 排程删除释放未消费预留。
            ScheduleEditTransactionResult removed = Apply(
                rig, 0L, window.WindowId, Player, new RemoveEditablePlanOperation(doomed.ActionPlanId));
            Assert.That(removed.RejectionCode, Is.Null, removed.RejectionCode);
            Assert.That(doomed.IsEditable, Is.True, "夹具事实：被删除的计划对象仍处于 Editable");
            Assert.That(window.ReservedFor(doomed.ActionPlanId), Is.Zero);

            int reservedBefore = window.ReservedBudgetTicks;
            int spentBefore = window.SpentBudgetTicks;
            int availableBefore = window.AvailableBudgetTicks;
            int ledgerCount = rig.Ledger.Count;

            ActionStartGateResult gate = EvaluateGate(rig, doomed, 100L);
            Assert.That(gate.IsStartable, Is.True, ActionStartGate.Describe(gate));

            int rollbacks = 0;
            string error = rig.Budget.Commit(doomed, 100L, _ => rollbacks++);

            Assert.That(error, Is.Not.Null, "预留与权威成本不一致时启动提交必须失败");
            Assert.That(error, Does.StartWith(ActionStartGateCommitCodes.RESOURCE_COMMIT_ERROR));
            Assert.That(rollbacks, Is.EqualTo(1), "失败必须先调用调用方的回滚端口（不得留下任何已完成副作用）");

            // 失败的提交不改变任何账本值：不消费、不部分扣费、不产生负值。
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(reservedBefore));
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(spentBefore));
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(availableBefore));
            Assert.That(window.ReservedFor(survivor.ActionPlanId), Is.EqualTo(GuardCost),
                "同窗口其它计划的预留绝不能被牵连");
            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount), "失败的提交不得发射任何账本变化");
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ConsumedAtLock, doomed.ActionPlanId), Is.Zero);

            // 不得提交 Spent/Locked/Running 的任何子集：计划仍是 Editable、没有锁定 Tick。
            Assert.That(doomed.IsEditable, Is.True);
            Assert.That(doomed.IsRunning, Is.False);
            Assert.That(doomed.LockedAtTick, Is.LessThanOrEqualTo(0L), "绝不留下 Locked-but-not-Running 或半消费");
            Assert.That(doomed.TerminationReason, Is.EqualTo(ActionTerminationReason.None),
                "启动提交失败不是普通终态：不得把计划终止后继续");
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 5. SystemAutoDeferralKeepsBudgetReserved
        // =====================================================================
        [Test]
        public void SystemAutoDeferralKeepsBudgetReserved()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 60);
            ActionPlan plan = AddGuard(rig, 0L, 0L);
            long startBefore = plan.StartTick;
            long requestedBefore = plan.LastRequestedStartTick;
            int ledgerCount = rig.Ledger.Count;

            rig.BlockingUntilTick = 150L;
            ActionStartGateResult gate = EvaluateGate(rig, plan, 0L);
            Assert.That(gate is ActionStartGateResult.Retryable, Is.True,
                "有限的控制状态阻塞必须判定为 Retryable：" + ActionStartGate.Describe(gate));
            Assert.That(((ActionStartGateResult.Retryable)gate).RetryAtTick, Is.EqualTo(150L));

            ScheduleEditTransactionResult deferred = AutoDefer(rig, plan, 150L, 0L);

            Assert.That(deferred.Committed, Is.True, deferred.RejectionCode);
            Assert.That(plan.IsEditable, Is.True, "系统自动延期必须保持 Editable");
            Assert.That(plan.StartTick, Is.EqualTo(150L), "计划必须被原子延期到 RetryAtTick");
            Assert.That(plan.StartTick, Is.Not.EqualTo(startBefore));
            Assert.That(plan.LastRequestedStartTick, Is.EqualTo(requestedBefore),
                "系统自动延期绝不覆盖请求起点");
            Assert.That(plan.AutomaticDeferralCount, Is.EqualTo(1));

            // 预算：仍然是 Reserved，绝不出现 Spent / ReleasedBeforeLock / 二次预留。
            Assert.That(window.ReservedFor(plan.ActionPlanId), Is.EqualTo(GuardCost));
            Assert.That(plan.ReservedTurnBudgetTicks, Is.EqualTo(GuardCost));
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(GuardCost));
            Assert.That(window.SpentBudgetTicks, Is.Zero, "系统自动延期绝不能消费预算");
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(60 - GuardCost));
            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount),
                "成本中性的延期不产生任何账本变化（既不消费也不释放）");
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ConsumedAtLock, plan.ActionPlanId), Is.Zero);
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock, plan.ActionPlanId), Is.Zero);
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(2L), "成功事务恰好 +1（新增 + 延期）");
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 6. SystemAutoDeferralAdjustsBudgetWithScheduleAndReservationAtomically
        // =====================================================================
        [Test]
        public void SystemAutoDeferralAdjustsBudgetWithScheduleAndReservationAtomically()
        {
            // 链几何（全部由真实 Pathfinder 的 1/2 权重规则推出，见 MoveBudgetUsesPathWeightUnits…）：
            //   X：@0  -> (0,4)，起点 = 锚点 (0,0)，2 条奇数方向边（各权重 2）⇒ 权重 4 ⇒ 成本 25；
            //   Y：@100 -> (0,-4)，起点 = X 的目的格 (0,4)（X 在其起点前结束）⇒ 权重 8 ⇒ 成本 45。
            // 延期把 X 推到 Y 之后 ⇒ X 的预测起点变为 Y 的目的格 (0,-4) ⇒ 权重 4 -> 8、成本 25 -> 45。
            Rig rig = NewChainRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 90);
            ActionPlan x = AddMove(rig, 0L, 0L, 0, 4);
            ActionPlan y = AddMove(rig, 0L, 100L, 0, -4);
            Assert.That(x.ResolvedPathWeightUnits, Is.EqualTo(4), "夹具前提：X 的初始权重");
            Assert.That(x.BudgetCostTicks, Is.EqualTo(25));
            Assert.That(y.BudgetCostTicks, Is.EqualTo(45));
            int xCostBefore = x.BudgetCostTicks;
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(70));
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(20),
                "夹具前提：可用额度恰好覆盖本次成本上升（20）");
            int ledgerCount = rig.Ledger.Count;

            rig.BlockingUntilTick = 150L;
            ActionStartGateResult gate = EvaluateGate(rig, x, 0L);
            Assert.That(gate is ActionStartGateResult.Retryable, Is.True, ActionStartGate.Describe(gate));

            ScheduleEditTransactionResult deferred = AutoDefer(rig, x, 150L, 0L);
            Assert.That(deferred.Committed, Is.True, deferred.RejectionCode);

            // 排程与预算同批落定：新起点、新成本、新预留三者必须一致。
            Assert.That(x.StartTick, Is.EqualTo(150L));
            Assert.That(x.IsEditable, Is.True, "自动延期后仍是 Editable");
            Assert.That(x.BudgetCostTicks, Is.EqualTo(45),
                "起点移到前序移动之后 ⇒ 路径权重 4 -> 8 ⇒ 成本 25 -> 45");
            Assert.That(x.ResolvedPathWeightUnits, Is.EqualTo(8));
            Assert.That(x.ReservedTurnBudgetTicks, Is.EqualTo(x.BudgetCostTicks),
                "计划自身的未消费预留投影必须与新成本一致");
            Assert.That(window.ReservedFor(x.ActionPlanId), Is.EqualTo(x.BudgetCostTicks),
                "账本里的预留必须与新成本一致（否则就是排程与预算分叉）");
            Assert.That(window.ReservedFor(y.ActionPlanId), Is.EqualTo(y.BudgetCostTicks));
            // Reordering also changes Y's predecessor: anchor -> (0,-4) costs 25, not the stale X -> Y cost 45.
            Assert.That(y.ResolvedPathWeightUnits, Is.EqualTo(4));
            Assert.That(y.BudgetCostTicks, Is.EqualTo(25));
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(70));
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(20));
            Assert.That(window.SpentBudgetTicks, Is.Zero, "自动延期绝不允许消费");
            Assert.That(x.AutomaticDeferralCount, Is.EqualTo(1));

            // Both members of the position dependency chain adjust, in stable PlanId order.
            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount + 2));
            TurnBudgetChange adjusted = rig.Ledger[ledgerCount];
            Assert.That(adjusted.ChangeKind, Is.EqualTo(TurnBudgetChangeKind.ReservationAdjusted));
            Assert.That(adjusted.Source, Is.EqualTo(ResourceChangeSource.SystemAutoDeferral));
            Assert.That(adjusted.ActionPlanId, Is.EqualTo(x.ActionPlanId));
            Assert.That(adjusted.WindowId, Is.EqualTo(window.WindowId));
            Assert.That(adjusted.ReservedBefore, Is.EqualTo(70),
                "账本事件的 Reserved 前后值是**窗口总额**：25 只是该计划自己的预留");
            Assert.That(adjusted.ReservedAfter, Is.EqualTo(90));
            Assert.That(adjusted.SpentBefore, Is.Zero);
            Assert.That(adjusted.SpentAfter, Is.Zero);
            var successorAdjusted = rig.Ledger[ledgerCount + 1];
            Assert.That(successorAdjusted.ActionPlanId, Is.EqualTo(y.ActionPlanId));
            Assert.That(successorAdjusted.Source, Is.EqualTo(ResourceChangeSource.SystemAutoDeferral));
            Assert.That(successorAdjusted.ChangeKind, Is.EqualTo(TurnBudgetChangeKind.ReservationAdjusted));
            Assert.That(successorAdjusted.ReservedBefore, Is.EqualTo(90));
            Assert.That(successorAdjusted.ReservedAfter, Is.EqualTo(70));
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ConsumedAtLock, x.ActionPlanId), Is.Zero);
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock, x.ActionPlanId), Is.Zero);
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(3L));
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 7. AutoDeferralCannotIncreaseCostFromClosedSourceWindow
        // =====================================================================
        [Test]
        public void AutoDeferralCannotIncreaseCostFromClosedSourceWindow()
        {
            Rig rig = NewChainRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 90);
            ActionPlan x = AddMove(rig, 0L, 0L, 0, 4);
            ActionPlan y = AddMove(rig, 0L, 100L, 0, -4);
            long sourceWindowId = window.WindowId.Value;
            int xCostBefore = x.BudgetCostTicks;
            int xReservedBefore = window.ReservedFor(x.ActionPlanId);
            long revisionBefore = rig.Schedule.ScheduleRevision;
            int ledgerCount = rig.Ledger.Count;

            CloseWindow(rig, window, 0L);
            Assert.That(rig.Windows.CurrentWindow, Is.Null);

            rig.BlockingUntilTick = 150L;
            ActionStartGateResult gate = EvaluateGate(rig, x, 0L);
            Assert.That(gate is ActionStartGateResult.Retryable, Is.True, ActionStartGate.Describe(gate));

            ScheduleEditTransactionResult deferred = AutoDefer(rig, x, 150L, 0L);

            // 成本上升（25 -> 45）只允许从"同一个仍为当前且开放"的来源窗口追加：来源已关闭 ⇒ 整个候选失败。
            Assert.That(deferred.Committed, Is.False, "关闭窗口不得追加预留");
            Assert.That(deferred.RejectionCode, Is.EqualTo(TurnWindowCodes.BUDGET_SOURCE_CLOSED_OR_MISMATCH));

            // 失败候选零局部写入：起点、路径权重、成本、预留、延期计数、修订号全部复原。
            Assert.That(x.StartTick, Is.Zero, "被拒绝的延期必须把排程投影恢复原状");
            Assert.That(x.BudgetCostTicks, Is.EqualTo(xCostBefore));
            Assert.That(x.ResolvedPathWeightUnits, Is.EqualTo(4));
            Assert.That(x.ReservedTurnBudgetTicks, Is.EqualTo(xReservedBefore));
            Assert.That(x.AutomaticDeferralCount, Is.Zero);
            Assert.That(x.IsEditable, Is.True);
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBefore));
            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount), "失败候选不得留下任何账本变化");

            // 关闭的窗口保持关闭：不重开、不转移、不接受提交。
            TurnWindow closed = rig.Windows.FindWindow(new WindowId(sourceWindowId));
            Assert.That(closed, Is.SameAs(window));
            Assert.That(closed.IsOpen, Is.False);
            Assert.That(closed.IsAcceptingSubmissions, Is.False);
            Assert.That(rig.Windows.OpenWindow, Is.Null);
            Assert.That(rig.Windows.IsAcceptingSubmissions, Is.False);
            Assert.That(closed.ReservedFor(x.ActionPlanId), Is.EqualTo(xReservedBefore),
                "失败候选必须把预留留在原窗口账本里（不消费、不转移）");
            Assert.That(closed.ReservedFor(y.ActionPlanId), Is.EqualTo(y.BudgetCostTicks));
            Assert.That(closed.SpentBudgetTicks, Is.Zero);
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 8. FailedAutoDeferralTerminalReleasesReservationExactlyOnce
        // =====================================================================
        [Test]
        public void FailedAutoDeferralTerminalReleasesReservationExactlyOnce()
        {
            Rig rig = NewChainRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 90);
            ActionPlan x = AddMove(rig, 0L, 0L, 0, 4);
            ActionPlan y = AddMove(rig, 0L, 100L, 0, -4);
            int xReserved = window.ReservedFor(x.ActionPlanId);
            int yReserved = window.ReservedFor(y.ActionPlanId);
            int availableBefore = window.AvailableBudgetTicks;
            Assert.That(xReserved, Is.EqualTo(25));
            Assert.That(yReserved, Is.EqualTo(45));

            // 来源窗口已关闭：成本上升（25 -> 45）无处追加 ⇒ 整个候选失败。
            CloseWindow(rig, window, 0L);

            rig.BlockingUntilTick = 150L;
            ScheduleEditTransactionResult deferred = AutoDefer(rig, x, 150L, 0L);
            Assert.That(deferred.Committed, Is.False, "夹具前提：本次自动延期必须失败");
            Assert.That(deferred.RejectionCode, Is.EqualTo(TurnWindowCodes.BUDGET_SOURCE_CLOSED_OR_MISMATCH));

            // 任务 05 在延期失败后经统一终态协调器处理到期计划：预算参与者只执行一次 ReleasedBeforeLock。
            ActionPlanTerminalOutcome outcome = rig.Coordinator.EnterTerminal(
                x, ActionTerminationReason.ActorUnavailableAtStart, 0L);
            Assert.That(outcome.EnteredTerminal, Is.True);
            Assert.That(x.IsTerminated, Is.True);
            Assert.That(x.TerminalTick, Is.Zero);

            Assert.That(window.ReservedFor(x.ActionPlanId), Is.Zero, "未消费预留必须被释放");
            Assert.That(window.ReservedFor(y.ActionPlanId), Is.EqualTo(yReserved), "其它计划不受牵连");
            Assert.That(window.SpentBudgetTicks, Is.Zero, "自动延期失败不是 Locked 后退款，也不产生消费");
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(availableBefore + xReserved));
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock, x.ActionPlanId), Is.EqualTo(1));
            TurnBudgetChange release = LastKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock);
            Assert.That(release.Source, Is.EqualTo(ResourceChangeSource.TerminalCleanup));
            Assert.That(release.ReservedBefore, Is.EqualTo(70),
                "账本事件的 Reserved 前后值是窗口总额（70 -> 45：只释放 X 的 25）");
            Assert.That(release.ReservedAfter, Is.EqualTo(45));
            Assert.That(release.WindowId, Is.EqualTo(window.WindowId));
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ConsumedAtLock, x.ActionPlanId), Is.Zero);

            // 重复清理：幂等，不得第二次释放、不得改状态。
            int ledgerCount = rig.Ledger.Count;
            int availableAfter = window.AvailableBudgetTicks;
            ActionPlanTerminalOutcome repeated = rig.Coordinator.EnterTerminal(
                x, ActionTerminationReason.OwnerDied, 1L);
            Assert.That(repeated.EnteredTerminal, Is.False, "重复终态请求必须幂等返回");
            Assert.That(x.TerminationReason, Is.EqualTo(ActionTerminationReason.ActorUnavailableAtStart),
                "第一次请求胜出，后续请求不得覆盖原因");
            Assert.That(x.TerminalTick, Is.Zero);
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(availableAfter));
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(yReserved));
            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount), "重复清理不得再释放一次");
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock, x.ActionPlanId), Is.EqualTo(1));
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 9. SystemAutoDeferralDoesNotReopenClosedWindow
        // =====================================================================
        [Test]
        public void SystemAutoDeferralDoesNotReopenClosedWindow()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 60);
            ActionPlan plan = AddGuard(rig, 0L, 0L);
            CloseWindow(rig, window, 0L);
            int ledgerCount = rig.Ledger.Count;
            long revisionBefore = rig.Schedule.ScheduleRevision;
            long closedAtTick = rig.Windows.LastClosedAtTick;

            rig.BlockingUntilTick = 150L;
            ScheduleEditTransactionResult deferred = AutoDefer(rig, plan, 150L, 0L);

            Assert.That(deferred.Committed, Is.True, deferred.RejectionCode);
            Assert.That(plan.StartTick, Is.EqualTo(150L));
            Assert.That(plan.IsEditable, Is.True);

            // 排程成功，但来源窗口仍然关闭：不重开、不接受提交、不排定新窗口、不转移额度。
            Assert.That(window.IsOpen, Is.False, "系统自动延期绝不能重开已关闭的窗口");
            Assert.That(window.IsAcceptingSubmissions, Is.False);
            Assert.That(rig.Windows.CurrentWindow, Is.Null);
            Assert.That(rig.Windows.OpenWindow, Is.Null);
            Assert.That(rig.Windows.IsAcceptingSubmissions, Is.False);
            Assert.That(rig.Windows.LastClosedAtTick, Is.EqualTo(closedAtTick));
            Assert.That(rig.Windows.ScheduledWindowCount, Is.Zero, "系统自动延期不得排定任何未来窗口");
            Assert.That(rig.Windows.ClosedWindows.Count, Is.EqualTo(1));

            // 未消费预留留在原窗口的历史账本里，可继续审计。
            Assert.That(window.ReservedFor(plan.ActionPlanId), Is.EqualTo(GuardCost));
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(GuardCost));
            Assert.That(window.SpentBudgetTicks, Is.Zero);
            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount),
                "成本中性的自动延期不产生账本变化，也不得把释放额变成新授权");
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBefore + 1L));
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 10. RejectedCommandDoesNotReserveOrSpendBudget
        // =====================================================================
        [Test]
        public void RejectedCommandDoesNotReserveOrSpendBudget()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 20);

            // ① 命令声明的 ExpectedWindowId 不是当前窗口 ⇒ 稳定拒绝。
            ScheduleEditTransactionResult stale = Apply(
                rig, 0L, W(999L), Player,
                new AddOrdinaryPlanOperation(
                    1L, Hero, Spec(GuardSpecId), 100L, AnchorAfterPlanId: default));
            Assert.That(stale.Succeeded, Is.False);
            Assert.That(stale.RejectionCode, Is.Not.Null);
            AssertZeroWrite(rig, window, "陈旧窗口");

            // ② 可用预算不足 ⇒ 稳定拒绝（且不得部分预留）。
            ScheduleEditTransactionResult tooExpensive = Apply(
                rig, 0L, window.WindowId, Player,
                new AddOrdinaryPlanOperation(
                    2L, Hero, Spec(GuardSpecId), 100L, AnchorAfterPlanId: default));
            Assert.That(tooExpensive.Succeeded, Is.False);
            Assert.That(tooExpensive.RejectionCode, Is.EqualTo(TurnWindowCodes.INSUFFICIENT_WINDOW_BUDGET));
            AssertZeroWrite(rig, window, "预算不足");

            // ③ 同一批次里第二条操作非法 ⇒ 整批拒绝（第一条候选也不得留下任何写入）。
            ScheduleEditTransactionResult mixed = Apply(
                rig, 0L, window.WindowId, Player,
                new AddOrdinaryPlanOperation(
                    3L, Hero, Spec(GuardSpecId), 100L, AnchorAfterPlanId: default),
                new MoveEditablePlanOperation(P(4242L), 100L));
            Assert.That(mixed.Succeeded, Is.False);
            AssertZeroWrite(rig, window, "批内第二条非法");

            Assert.That(rig.Schedule.Registry.ActiveCount, Is.Zero, "被拒绝的命令不得创建任何计划");
            Assert.That(rig.Schedule.Lanes.Count, Is.Zero, "被拒绝的命令不得创建 Lane");
            Assert.That(rig.Schedule.ScheduleRevision, Is.Zero, "失败事务零局部写入，修订号不变");
            Assert.That(rig.Ledger.Count, Is.Zero, "被拒绝的命令不得发射任何账本变化");
            AssertLedgerIdentity(rig);
        }

        private static void AssertZeroWrite(Rig rig, TurnWindow window, string what)
        {
            Assert.That(window.ReservedBudgetTicks, Is.Zero, what + "：不得留下预留");
            Assert.That(window.SpentBudgetTicks, Is.Zero, what + "：不得消费");
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(window.TotalBudgetTicks));
            Assert.That(window.Reservations.Count, Is.Zero);
            Assert.That(rig.Schedule.Registry.ActiveCount, Is.Zero, what + "：不得创建计划");
            Assert.That(rig.Ledger.Count, Is.Zero, what + "：不得发射账本变化");
        }

        // =====================================================================
        // 11. ScheduleEditFailureRollsBackBudgetLanePlanAndReservation
        // =====================================================================
        [Test]
        public void ScheduleEditFailureRollsBackBudgetLanePlanAndReservation()
        {
            Rig rig = NewChainRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 70);
            ActionPlan x = AddMove(rig, 0L, 0L, 0, 4);
            ActionPlan y = AddMove(rig, 0L, 100L, 0, -4);

            int xCost = x.BudgetCostTicks;
            int yCost = y.BudgetCostTicks;
            int reservedTotal = window.ReservedBudgetTicks;
            Assert.That(xCost, Is.EqualTo(25));
            Assert.That(yCost, Is.EqualTo(45));
            Assert.That(reservedTotal, Is.EqualTo(70));
            Assert.That(window.AvailableBudgetTicks, Is.Zero,
                "夹具前提：本用例的窗口额度恰好被两条计划吃满，因此任何成本上升都必须失败");

            long xStartBefore = x.StartTick;
            long yStartBefore = y.StartTick;
            int xWeightBefore = x.ResolvedPathWeightUnits;
            long xRequestedBefore = x.LastRequestedStartTick;
            long revisionBefore = rig.Schedule.ScheduleRevision;
            int ledgerCount = rig.Ledger.Count;
            int laneCount = rig.Schedule.FindLane(Hero).Count;
            long laneFirstId = rig.Schedule.FindLane(Hero).Plans[0].ActionPlanId.Value;

            ScheduleEditTransactionResult failed = Apply(
                rig, 0L, window.WindowId, Player,
                new MoveEditablePlanOperation(x.ActionPlanId, 200L));

            Assert.That(failed.Succeeded, Is.False, "成本上升但可用额度为 0 ⇒ 整批拒绝");
            Assert.That(failed.RejectionCode, Is.EqualTo(TurnWindowCodes.INSUFFICIENT_WINDOW_BUDGET),
                "事务必须走到预算校验（求值已把候选投影写进计划），再整批拒绝");

            // ① 预算零写入。
            Assert.That(window.ReservedFor(x.ActionPlanId), Is.EqualTo(xCost));
            Assert.That(window.ReservedFor(y.ActionPlanId), Is.EqualTo(yCost));
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(reservedTotal));
            Assert.That(window.SpentBudgetTicks, Is.Zero);
            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount));

            // ② 计划投影零写入（依赖闭包被改写后必须被恢复）。
            Assert.That(x.StartTick, Is.EqualTo(xStartBefore));
            Assert.That(x.ResolvedPathWeightUnits, Is.EqualTo(xWeightBefore));
            Assert.That(x.BudgetCostTicks, Is.EqualTo(xCost));
            Assert.That(x.ReservedTurnBudgetTicks, Is.EqualTo(xCost));
            Assert.That(x.LastRequestedStartTick, Is.EqualTo(xRequestedBefore));
            Assert.That(y.StartTick, Is.EqualTo(yStartBefore));
            Assert.That(y.BudgetCostTicks, Is.EqualTo(yCost));
            Assert.That(y.ReservedTurnBudgetTicks, Is.EqualTo(yCost));

            // ③ Lane 与修订号零写入。
            Assert.That(rig.Schedule.FindLane(Hero).Count, Is.EqualTo(laneCount));
            Assert.That(rig.Schedule.FindLane(Hero).Plans[0].ActionPlanId.Value, Is.EqualTo(laneFirstId));
            Assert.That(rig.Schedule.FindLane(Hero).ValidateNonOverlapping(), Is.Null);
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBefore));
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 12. RemovingEditablePlanReleasesUnspentBudgetReservation
        // =====================================================================
        [Test]
        public void RemovingEditablePlanReleasesUnspentBudgetReservation()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 60);
            ActionPlan plan = AddGuard(rig, 0L, 100L);
            int availableAfterReserve = window.AvailableBudgetTicks;

            ScheduleEditTransactionResult removed = Apply(
                rig, 1L, window.WindowId, Player, new RemoveEditablePlanOperation(plan.ActionPlanId));

            Assert.That(removed.Succeeded, Is.True, removed.RejectionCode);
            Assert.That(removed.RemovedPlanIds.Count, Is.EqualTo(1));
            Assert.That(removed.RemovedPlanIds[0], Is.EqualTo(plan.ActionPlanId));

            Assert.That(window.ReservedFor(plan.ActionPlanId), Is.Zero);
            Assert.That(window.HasReservation(plan.ActionPlanId), Is.False);
            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(window.SpentBudgetTicks, Is.Zero, "删除是锁定前释放，绝不是消费");
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(availableAfterReserve + GuardCost));
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(window.TotalBudgetTicks),
                "释放额必须回到原窗口账本");

            Assert.That(CountKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock, plan.ActionPlanId), Is.EqualTo(1));
            TurnBudgetChange release = LastKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock);
            Assert.That(release.Source, Is.EqualTo(ResourceChangeSource.ExplicitScheduleEdit));
            Assert.That(release.ReservedBefore, Is.EqualTo(GuardCost));
            Assert.That(release.ReservedAfter, Is.Zero);

            Assert.That(rig.Schedule.FindLane(Hero) == null || rig.Schedule.FindLane(Hero).Count == 0,
                Is.True, "唯一计划被删除后 Lane 不得残留条目");
            Assert.That(rig.Schedule.LaneOfPlan(plan.ActionPlanId), Is.Null, "已删除计划不得留在任何 Lane");
            Assert.That(plan.ReservedTurnBudgetTicks, Is.Zero, "被删除计划的预留投影必须归零");
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 13. PreLockTerminalReleasesUnspentBudgetReservation
        // =====================================================================
        [Test]
        public void PreLockTerminalReleasesUnspentBudgetReservation()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 60);
            ActionPlan plan = AddGuard(rig, 0L, 100L);
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(GuardCost));
            int ledgerCount = rig.Ledger.Count;

            // 锁定前终态：目标在到期前失效（从未经过启动门禁的原子提交）。
            ActionPlanTerminalOutcome outcome = rig.Coordinator.EnterTerminal(
                plan, ActionTerminationReason.TargetInvalid, 40L);

            Assert.That(outcome.EnteredTerminal, Is.True);
            Assert.That(plan.IsTerminated, Is.True);
            Assert.That(plan.LockedAtTick, Is.LessThanOrEqualTo(0L), "从未锁定 ⇒ 释放的是未消费预留");
            Assert.That(window.ReservedFor(plan.ActionPlanId), Is.Zero);
            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(window.SpentBudgetTicks, Is.Zero, "锁定前终态不得产生任何消费");
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(window.TotalBudgetTicks),
                "释放额必须回到计划自己的来源窗口账本");
            Assert.That(plan.ReservedTurnBudgetTicks, Is.Zero);

            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount + 1));
            TurnBudgetChange release = LastKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock);
            Assert.That(release.Source, Is.EqualTo(ResourceChangeSource.TerminalCleanup));
            Assert.That(release.ActionPlanId, Is.EqualTo(plan.ActionPlanId));
            Assert.That(release.WindowId, Is.EqualTo(window.WindowId));
            Assert.That(release.ReservedBefore, Is.EqualTo(GuardCost));
            Assert.That(release.ReservedAfter, Is.Zero);
            Assert.That(release.AvailableBefore, Is.EqualTo(60 - GuardCost));
            Assert.That(release.AvailableAfter, Is.EqualTo(60));
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ConsumedAtLock, plan.ActionPlanId), Is.Zero);
            Assert.That(rig.Schedule.LaneOfPlan(plan.ActionPlanId), Is.Null, "终态计划必须离开 Lane");
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 14. LockedOrRunningTerminalDoesNotRefundSpentBudget
        // =====================================================================
        [Test]
        public void LockedOrRunningTerminalDoesNotRefundSpentBudget()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 60);
            ActionPlan plan = AddGuard(rig, 0L, 100L);
            StartAndLock(rig, plan, 100L);
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(GuardCost));

            int ledgerCount = rig.Ledger.Count;
            int spentBefore = window.SpentBudgetTicks;
            int availableBefore = window.AvailableBudgetTicks;

            ActionPlanTerminalOutcome outcome = rig.Coordinator.EnterTerminal(
                plan, ActionTerminationReason.InterruptedByControl, 120L);

            Assert.That(outcome.EnteredTerminal, Is.True);
            Assert.That(plan.IsTerminated, Is.True);
            Assert.That(plan.LockedAtTick, Is.EqualTo(100L));
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(spentBefore), "锁定后的 Spent 一律不退");
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(availableBefore),
                "不得出现任何退款额度");
            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(window.ReservedFor(plan.ActionPlanId), Is.Zero);
            Assert.That(window.HasReservation(plan.ActionPlanId), Is.False);
            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount),
                "Locked/Running 终态不得产生任何账本变化（既无释放也无消费）");
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock, plan.ActionPlanId), Is.Zero,
                "绝不把'不退款'实现成释放未消费预留");
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ConsumedAtLock, plan.ActionPlanId), Is.EqualTo(1));
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 15. AllLockedPlanTerminalReasonsPreserveSpentBudget
        // =====================================================================
        [Test]
        public void AllLockedPlanTerminalReasonsPreserveSpentBudget()
        {
            for (int i = 0; i < ActionTerminationReasons.All.Count; i++)
            {
                ActionTerminationReason reason = ActionTerminationReasons.All[i];
                Rig rig = NewRig();
                TurnWindow window = OpenWindow(rig, 0L, Hero, 60);
                ActionPlan plan = AddGuard(rig, 0L, 100L);
                StartAndLock(rig, plan, 100L);
                int ledgerCount = rig.Ledger.Count;

                ActionPlanTerminalOutcome outcome = rig.Coordinator.EnterTerminal(plan, reason, 150L);

                Assert.That(outcome.EnteredTerminal, Is.True, "原因 " + reason + " 必须能进入终态");
                Assert.That(window.SpentBudgetTicks, Is.EqualTo(GuardCost),
                    "原因 " + reason + " 不得退还已消费的 Spent");
                Assert.That(window.AvailableBudgetTicks, Is.EqualTo(60 - GuardCost),
                    "原因 " + reason + " 不得产生退款额度");
                Assert.That(window.ReservedBudgetTicks, Is.Zero);
                Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount),
                    "原因 " + reason + " 不得触发任何账本变化");
                Assert.That(plan.TerminationReason, Is.EqualTo(reason));
                AssertLedgerIdentity(rig);
            }

            // 自然完成（无终止原因）同样保留 Spent。
            Rig completionRig = NewRig();
            TurnWindow completionWindow = OpenWindow(completionRig, 0L, Hero, 60);
            ActionPlan completing = AddGuard(completionRig, 0L, 100L);
            StartAndLock(completionRig, completing, 100L);
            int completionLedger = completionRig.Ledger.Count;
            completionRig.Coordinator.EnterCompletion(completing, completing.EndTick);
            Assert.That(completing.IsCompleted, Is.True);
            Assert.That(completionWindow.SpentBudgetTicks, Is.EqualTo(GuardCost));
            Assert.That(completionWindow.AvailableBudgetTicks, Is.EqualTo(60 - GuardCost));
            Assert.That(completionRig.Ledger.Count, Is.EqualTo(completionLedger));
        }

        // =====================================================================
        // 16. EditableMoveCostDeltaAdjustsReservationAtomically
        // =====================================================================
        [Test]
        public void EditableMoveCostDeltaAdjustsReservationAtomically()
        {
            Rig rig = NewChainRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 90);
            ActionPlan x = AddMove(rig, 0L, 0L, 0, 4);
            ActionPlan y = AddMove(rig, 0L, 100L, 0, -4);
            Assert.That(x.BudgetCostTicks, Is.EqualTo(25), "夹具前提：X 的初始成本（权重 4）");
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(70));
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(20));
            int ledgerCount = rig.Ledger.Count;

            // 显式排程编辑：把 X 移到 Y 之后 ⇒ 预测起点变成 Y 的目的格 ⇒ 权重 4 -> 8、成本 25 -> 45。
            ScheduleEditTransactionResult edit = Apply(
                rig, 0L, window.WindowId, Player, new MoveEditablePlanOperation(x.ActionPlanId, 200L));

            Assert.That(edit.Succeeded, Is.True, edit.RejectionCode);
            Assert.That(x.StartTick, Is.EqualTo(200L));
            Assert.That(x.LastRequestedStartTick, Is.EqualTo(200L), "显式移动必须更新请求起点");
            Assert.That(x.ResolvedPathWeightUnits, Is.EqualTo(8));
            Assert.That(x.BudgetCostTicks, Is.EqualTo(45));

            // 差额原子落定：计划侧投影与账本预留必须同时等于新成本。
            Assert.That(x.ReservedTurnBudgetTicks, Is.EqualTo(45));
            Assert.That(window.ReservedFor(x.ActionPlanId), Is.EqualTo(45));
            Assert.That(window.ReservedFor(y.ActionPlanId), Is.EqualTo(y.BudgetCostTicks));
            Assert.That(y.ResolvedPathWeightUnits, Is.EqualTo(4), "Reordered Y now starts from the authoritative anchor.");
            Assert.That(y.BudgetCostTicks, Is.EqualTo(25));
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(70));
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(20));
            Assert.That(window.SpentBudgetTicks, Is.Zero, "成本上升仍然是 Reserved，绝不是 Spent");
            Assert.That(x.IsEditable, Is.True, "成本差额调整不改变计划生命周期");

            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount + 2));
            TurnBudgetChange adjusted = rig.Ledger[ledgerCount];
            Assert.That(adjusted.ChangeKind, Is.EqualTo(TurnBudgetChangeKind.ReservationAdjusted));
            Assert.That(adjusted.Source, Is.EqualTo(ResourceChangeSource.ExplicitScheduleEdit));
            Assert.That(adjusted.ActionPlanId, Is.EqualTo(x.ActionPlanId));
            Assert.That(adjusted.WindowId, Is.EqualTo(window.WindowId), "差额只能记在计划自己的来源窗口");
            Assert.That(adjusted.ReservedBefore, Is.EqualTo(70),
                "账本事件的 Reserved 前后值是窗口总额（70 -> 90），计划的预留由 ReservedFor 断言");
            Assert.That(adjusted.ReservedAfter, Is.EqualTo(90));
            Assert.That(adjusted.SpentBefore, Is.Zero);
            Assert.That(adjusted.SpentAfter, Is.Zero);
            var successorAdjusted = rig.Ledger[ledgerCount + 1];
            Assert.That(successorAdjusted.ActionPlanId, Is.EqualTo(y.ActionPlanId));
            Assert.That(successorAdjusted.Source, Is.EqualTo(ResourceChangeSource.ExplicitScheduleEdit));
            Assert.That(successorAdjusted.ChangeKind, Is.EqualTo(TurnBudgetChangeKind.ReservationAdjusted));
            Assert.That(successorAdjusted.ReservedBefore, Is.EqualTo(90));
            Assert.That(successorAdjusted.ReservedAfter, Is.EqualTo(70));
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(3L), "成功事务恰好 +1");
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 17. MoveBudgetUsesPathWeightUnitsInsteadOfEdgeCount
        // =====================================================================
        [Test]
        public void MoveBudgetUsesPathWeightUnitsInsteadOfEdgeCount()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 100);

            // hero：(0,0) -> (3,1) 是 1 条**奇数方向**边（EastNorth，权重 2）。
            ActionPlan heavy = AddMove(rig, 0L, 100L, 3, 1);
            // enemy：(0,30) -> (1,31) 是 1 条**偶数方向**边（NorthEast，权重 1）。
            ActionPlan light = AddMove(rig, 0L, 100L, 1, 31, owner: Enemy);

            // 两条路径的边数完全相同（都是 1），预算成本必须不同 ⇒ 计费单位只能是路径权重。
            Assert.That(heavy.ResolvedPathEdgeCount, Is.EqualTo(1));
            Assert.That(light.ResolvedPathEdgeCount, Is.EqualTo(1));
            Assert.That(heavy.ResolvedPathWeightUnits, Is.EqualTo(2));
            Assert.That(light.ResolvedPathWeightUnits, Is.EqualTo(1));

            Assert.That(heavy.BudgetCostTicks,
                Is.EqualTo((2 * MoveBaseStepTicks) + MoveRecoveryTicks),
                "预算 = 路径权重单位 × 每权重单位 Tick + 后摇（= 2×5+5 = 15），不是边数×基准（= 10）");
            Assert.That(heavy.BudgetCostTicks, Is.EqualTo(15));
            Assert.That(light.BudgetCostTicks, Is.EqualTo((1 * MoveBaseStepTicks) + MoveRecoveryTicks));
            Assert.That(light.BudgetCostTicks, Is.EqualTo(10));

            // 时长与结束边界同样使用权重：EndTick = StartTick + 权重×每权重 Tick + 后摇。
            Assert.That(heavy.ResolvedBaseStepTicks, Is.EqualTo(MoveBaseStepTicks));
            Assert.That(heavy.MoveDurationTicks, Is.EqualTo(2 * MoveBaseStepTicks));
            Assert.That(heavy.EndTick, Is.EqualTo(100L + (2 * MoveBaseStepTicks) + MoveRecoveryTicks));

            // 账本必须按同一个数字预留。
            Assert.That(window.ReservedFor(heavy.ActionPlanId), Is.EqualTo(15));
            Assert.That(window.ReservedFor(light.ActionPlanId), Is.EqualTo(10));
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(25));
            Assert.That(window.SpentBudgetTicks, Is.Zero);

            int heavyEvents = 0;
            int lightEvents = 0;
            for (int i = 0; i < rig.Ledger.Count; i++)
            {
                TurnBudgetChange change = rig.Ledger[i];
                if (change.ChangeKind != TurnBudgetChangeKind.Reserved) continue;
                Assert.That(change.SpentAfter, Is.Zero);
                if (change.ActionPlanId == heavy.ActionPlanId)
                {
                    heavyEvents++;
                    Assert.That(change.ReservedBefore, Is.Zero);
                    Assert.That(change.ReservedAfter, Is.EqualTo(15),
                        "事件的 Reserved 前后值是窗口总额");
                    Assert.That(change.ReservedAfter - change.ReservedBefore, Is.EqualTo(15));
                }
                else if (change.ActionPlanId == light.ActionPlanId)
                {
                    lightEvents++;
                    Assert.That(change.ReservedBefore, Is.EqualTo(15));
                    Assert.That(change.ReservedAfter, Is.EqualTo(25),
                        "事件的 Reserved 前后值是窗口总额；该计划自己的预留由 ReservedFor 断言");
                    Assert.That(change.ReservedAfter - change.ReservedBefore, Is.EqualTo(10));
                }
            }
            Assert.That(heavyEvents, Is.EqualTo(1));
            Assert.That(lightEvents, Is.EqualTo(1));
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 18. WindowSwitchDoesNotMutatePlanLaneIntentOrReservation
        // =====================================================================
        [Test]
        public void WindowSwitchDoesNotMutatePlanLaneIntentOrReservation()
        {
            Rig rig = NewRig();
            TurnWindow first = OpenWindow(rig, 0L, Hero, 100);
            ActionPlan guard = AddGuard(rig, 0L, 100L);
            ActionPlan move = AddMove(rig, 0L, 200L, 4, 0);
            ActionPlan enemyGuard = AddGuard(rig, 0L, 300L, owner: Enemy);
            long revisionBefore = rig.Schedule.ScheduleRevision;

            string plansBefore = PlanFingerprint(guard) + "|" + PlanFingerprint(move) + "|" + PlanFingerprint(enemyGuard);
            string lanesBefore = LaneFingerprint(rig);
            int ledgerCount = rig.Ledger.Count;
            Assert.That(first.ReservedBudgetTicks, Is.EqualTo(GuardCost + move.BudgetCostTicks + GuardCost));

            // 窗口切换：关闭 W1 → 打开别人的 W2 → 关闭 W2 → 打开 hero 的 W3。
            CloseWindow(rig, first, 1L);
            TurnWindow second = OpenWindow(rig, 2L, Enemy, 40);
            Assert.That(first.ReservedBudgetTicks, Is.EqualTo(GuardCost + move.BudgetCostTicks + GuardCost),
                "关闭不得结算或丢弃任何未消费预留");
            string plansAfterFirst = PlanFingerprint(guard) + "|" + PlanFingerprint(move) + "|" + PlanFingerprint(enemyGuard);
            Assert.That(plansAfterFirst, Is.EqualTo(plansBefore), "关闭窗口不得改变任何计划字段");

            CloseWindow(rig, second, 3L);
            TurnWindow third = OpenWindow(rig, 4L, Hero, 40);

            // 切换窗口前后：ID/Tick/状态/Lane/ScheduleRevision/预算/预留完全不变。
            Assert.That(PlanFingerprint(guard) + "|" + PlanFingerprint(move) + "|" + PlanFingerprint(enemyGuard),
                Is.EqualTo(plansBefore));
            Assert.That(LaneFingerprint(rig), Is.EqualTo(lanesBefore));
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBefore),
                "窗口切换不是排程编辑：修订号不变");
            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount), "窗口切换不得产生任何账本变化");

            Assert.That(first.ReservedFor(guard.ActionPlanId), Is.EqualTo(GuardCost));
            Assert.That(first.ReservedFor(move.ActionPlanId), Is.EqualTo(move.BudgetCostTicks));
            Assert.That(first.ReservedFor(enemyGuard.ActionPlanId), Is.EqualTo(GuardCost),
                "别人窗口里的计划仍然只占用它自己的来源窗口账本");
            Assert.That(second.ReservedBudgetTicks, Is.Zero, "新窗口不得承载任何既有预留");
            Assert.That(third.ReservedBudgetTicks, Is.Zero);
            Assert.That(second.SpentBudgetTicks, Is.Zero);
            Assert.That(third.SpentBudgetTicks, Is.Zero);
            Assert.That(guard.SubmittedWindowId, Is.EqualTo(first.WindowId));
            Assert.That(move.SubmittedWindowId, Is.EqualTo(first.WindowId));
            Assert.That(enemyGuard.SubmittedWindowId, Is.EqualTo(first.WindowId));
            Assert.That(guard.IsEditable && move.IsEditable && enemyGuard.IsEditable, Is.True,
                "窗口切换不得锁定、取消或结算任何计划");
            AssertLedgerIdentity(rig);
        }

        private static string PlanFingerprint(ActionPlan plan)
            => "id=" + plan.ActionPlanId.Value
               + ",state=" + plan.State
               + ",start=" + plan.StartTick
               + ",end=" + plan.EndTick
               + ",req=" + plan.LastRequestedStartTick
               + ",cost=" + plan.BudgetCostTicks
               + ",res=" + plan.ReservedTurnBudgetTicks
               + ",edges=" + plan.ResolvedPathEdgeCount
               + ",weight=" + plan.ResolvedPathWeightUnits
               + ",locked=" + plan.LockedAtTick
               + ",defer=" + plan.AutomaticDeferralCount
               + ",rev=" + plan.LastEditedScheduleRevision
               + ",win=" + (plan.SubmittedWindowId.HasValue ? plan.SubmittedWindowId.Value.Value : 0L)
               + ",dest=" + (plan.Destination.HasValue
                   ? plan.Destination.Value.X + ":" + plan.Destination.Value.Y
                   : "-");

        private static string LaneFingerprint(Rig rig)
        {
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < rig.Schedule.Lanes.Count; i++)
            {
                ActorLane lane = rig.Schedule.Lanes[i];
                builder.Append("lane#").Append(lane.UnitId.Value).Append('[');
                for (int p = 0; p < lane.Plans.Count; p++)
                {
                    if (p > 0) builder.Append(',');
                    builder.Append(lane.Plans[p].ActionPlanId.Value);
                }
                builder.Append(']');
            }
            return builder.ToString();
        }

        // =====================================================================
        // 19. PlanFromWindowAStartsAndEndsDuringLaterWindows
        // =====================================================================
        [Test]
        public void PlanFromWindowAStartsAndEndsDuringLaterWindows()
        {
            Rig rig = NewRig();
            TurnWindow first = OpenWindow(rig, 0L, Hero, 60);
            ActionPlan plan = AddGuard(rig, 0L, 50L);
            Assert.That(first.ReservedFor(plan.ActionPlanId), Is.EqualTo(GuardCost));

            CloseWindow(rig, first, 1L);
            TurnWindow second = OpenWindow(rig, 2L, Enemy, 40);
            CloseWindow(rig, second, 3L);
            TurnWindow third = OpenWindow(rig, 4L, Hero, 40);

            // 计划跨过两次窗口切换仍然存在（窗口不是动作容器，也不是执行边界）。
            Assert.That(plan.IsEditable, Is.True);
            Assert.That(plan.StartTick, Is.EqualTo(50L));
            Assert.That(plan.SubmittedWindowId, Is.EqualTo(first.WindowId));
            Assert.That(rig.Schedule.LaneOfPlan(plan.ActionPlanId), Is.Not.Null);

            // 到期：在 W3 期间启动，但资源只能从它自己的来源窗口 W1 消费。
            ActionStartGateResult gate = EvaluateGate(rig, plan, 50L);
            Assert.That(gate.IsStartable, Is.True, ActionStartGate.Describe(gate));
            StartAndLock(rig, plan, 50L);

            Assert.That(plan.IsRunning, Is.True);
            Assert.That(plan.LockedAtTick, Is.EqualTo(50L));
            Assert.That(first.SpentBudgetTicks, Is.EqualTo(GuardCost),
                "Reserved -> Spent 必须记在计划的来源窗口 W1 上");
            Assert.That(first.ReservedBudgetTicks, Is.Zero);
            Assert.That(first.AvailableBudgetTicks, Is.EqualTo(60 - GuardCost));
            Assert.That(second.SpentBudgetTicks, Is.Zero, "后来打开的窗口不得被牵连");
            Assert.That(second.ReservedBudgetTicks, Is.Zero);
            Assert.That(third.SpentBudgetTicks, Is.Zero);
            Assert.That(third.ReservedBudgetTicks, Is.Zero);

            // 结束：计划在自己的 EndTick 上自然完成，Spent 依旧不退。
            int ledgerCount = rig.Ledger.Count;
            ActionPlanTerminalOutcome outcome = rig.Coordinator.EnterCompletion(plan, plan.EndTick);
            Assert.That(outcome.EnteredTerminal, Is.True);
            Assert.That(plan.IsCompleted, Is.True);
            Assert.That(first.SpentBudgetTicks, Is.EqualTo(GuardCost), "完成后 Spent 不退");
            Assert.That(first.AvailableBudgetTicks, Is.EqualTo(60 - GuardCost));
            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount));
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock, plan.ActionPlanId), Is.Zero);
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 20. RepeatedEditableTerminalCleanupCannotReleaseReservationTwice
        // =====================================================================
        [Test]
        public void RepeatedEditableTerminalCleanupCannotReleaseReservationTwice()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 60);
            ActionPlan plan = AddGuard(rig, 0L, 100L);

            ActionPlanTerminalOutcome first = rig.Coordinator.EnterTerminal(
                plan, ActionTerminationReason.CancelledByCommand, 5L);
            Assert.That(first.EnteredTerminal, Is.True);
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock, plan.ActionPlanId), Is.EqualTo(1));
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(60));
            int ledgerCount = rig.Ledger.Count;

            // 重复终态请求（不同原因、不同 Tick）必须是幂等无操作。
            ActionPlanTerminalOutcome second = rig.Coordinator.EnterTerminal(
                plan, ActionTerminationReason.OwnerDied, 6L);
            ActionPlanTerminalOutcome third = rig.Coordinator.EnterTerminal(
                plan, ActionTerminationReason.BattleEnded, 7L);

            Assert.That(second.EnteredTerminal, Is.False);
            Assert.That(third.EnteredTerminal, Is.False);
            Assert.That(plan.TerminationReason, Is.EqualTo(ActionTerminationReason.CancelledByCommand),
                "第一次请求胜出");
            Assert.That(plan.TerminalTick, Is.EqualTo(5L));
            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(window.SpentBudgetTicks, Is.Zero);
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(60),
                "重复清理绝不能让释放额再次流入账本（否则会超过 Total）");
            Assert.That(window.HasReservation(plan.ActionPlanId), Is.False);
            Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerCount), "重复清理不得发射任何账本变化");
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock, plan.ActionPlanId), Is.EqualTo(1));
            Assert.That(CountKind(rig, TurnBudgetChangeKind.BattleEndCleared, plan.ActionPlanId), Is.Zero);
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 21. RepeatedLockedTerminalCleanupCannotInvokeBudgetRollback
        // =====================================================================
        [Test]
        public void RepeatedLockedTerminalCleanupCannotInvokeBudgetRollback()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 60);
            ActionPlan plan = AddGuard(rig, 0L, 100L);
            StartAndLock(rig, plan, 100L);
            CloseWindow(rig, window, 101L);
            int ledgerCount = rig.Ledger.Count;

            // 第一次终态请求胜出（打断），之后的重复请求必须是幂等无操作。
            ActionPlanTerminalOutcome entered = rig.Coordinator.EnterTerminal(
                plan, ActionTerminationReason.InterruptedByControl, 120L);
            Assert.That(entered.EnteredTerminal, Is.True);
            int ledgerAfterFirst = rig.Ledger.Count;

            // 终态协调器只有"状态感知参与者"，没有事务回滚通道：任何原因都不得让 Spent 变动、
            // 不得让预留复活（回滚会让 ReleasedBeforeLock 变成 +Cost 的追加，在已关闭窗口上必然失败）。
            for (int i = 0; i < ActionTerminationReasons.All.Count; i++)
            {
                ActionTerminationReason reason = ActionTerminationReasons.All[i];
                ActionPlanTerminalOutcome outcome = rig.Coordinator.EnterTerminal(plan, reason, 130L + i);
                Assert.That(outcome.EnteredTerminal, Is.False, "重复清理必须幂等（原因 " + reason + "）");
                Assert.That(window.SpentBudgetTicks, Is.EqualTo(GuardCost), "Spent 不得被回滚（" + reason + "）");
                Assert.That(window.ReservedBudgetTicks, Is.Zero);
                Assert.That(window.HasReservation(plan.ActionPlanId), Is.False, "预留不得被回滚复活（" + reason + "）");
                Assert.That(window.AvailableBudgetTicks, Is.EqualTo(60 - GuardCost));
                Assert.That(rig.Ledger.Count, Is.EqualTo(ledgerAfterFirst),
                    "终态清理不得调用 RollbackBudget（" + reason + "）");
            }

            Assert.That(plan.TerminationReason, Is.EqualTo(ActionTerminationReason.InterruptedByControl),
                "第一次终态请求胜出");
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ReleasedBeforeLock, plan.ActionPlanId), Is.Zero);
            Assert.That(CountKind(rig, TurnBudgetChangeKind.ConsumedAtLock, plan.ActionPlanId), Is.EqualTo(1),
                "ConsumedAtLock 恰好一次");
            AssertLedgerIdentity(rig);
        }

        // =====================================================================
        // 22. ReactionAcceptanceDoesNotRequireWindowOrConcurrentAuthority
        // =====================================================================
        [Test]
        public void ReactionAcceptanceDoesNotRequireWindowOrConcurrentAuthority()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 200);
            ActionPlan attack = AddAttack(rig, 0L, 0L, Hero, Enemy);

            // 反应不要求窗口：先把唯一的窗口正式关闭，之后不存在任何开放窗口。
            CloseWindow(rig, window, 0L);
            Assert.That(rig.Windows.CurrentWindow, Is.Null);
            Assert.That(rig.Windows.OpenWindow, Is.Null);

            // 攻击经生产启动提交端口原子锁定/启动（TelegraphTick = StartTick = 0、ImpactTick = 100）。
            StartAndLock(rig, attack, 0L);
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(attack.BudgetCostTicks));

            // 防御者的肾上腺素只经规范入账入口获得（本装配没有任务 08，所以由用例提供规范事实）。
            rig.Adrenaline.ApplyTickEndAccrual(new[] { new AdrenalineAccrualFacts(Enemy, 0, 10, 0, 0, 0) });
            int adrenalineBefore = rig.Adrenaline.Find(Enemy).AvailableAdrenaline;
            Assert.That(adrenalineBefore, Is.EqualTo(20));

            var opened = new List<ReactionOpportunityRuntime>();
            Assert.That(rig.Reactions.TryOpenForTelegraph(attack, 0L, opened), Is.Null,
                "已锁定/启动的攻击必须公开反应机会");
            Assert.That(opened.Count, Is.EqualTo(1));
            ReactionOpportunityId opportunityId = opened[0].Id;
            Assert.That(opened[0].DefenderUnitId, Is.EqualTo(Enemy));
            Assert.That(opened[0].TriggerTick, Is.EqualTo(attack.ImpactTick));

            // 凭证 ①：本装配根本没有并发授权系统（预算端口的提交授权实现为 null）。
            Assert.That(rig.Budget.SubmissionAuthority, Is.Null,
                "夹具事实：本装配没有 ConcurrentActionSystem");
            // 凭证 ②：反应命令的 scope 在类型上就没有窗口字段。
            Assert.That(typeof(ReactionCommandScope).GetProperties().Length, Is.EqualTo(1),
                "ReactionCommandScope 只能携带 ReactionOpportunityId：不存在 ExpectedWindowId");
            Assert.That(typeof(ReactionCommandScope).GetProperties()[0].Name,
                Is.EqualTo(nameof(ReactionCommandScope.ReactionOpportunityId)));

            int budgetEventsBefore = rig.Ledger.Count;
            int adrenalineChangesBefore = rig.AdrenalineChanges.Count;

            string error = rig.Reactions.TryAcceptById(
                opportunityId, Spec(BlockSpecId), 0L, null, out ActionPlan block);

            Assert.That(error, Is.Null, "反应接受不得要求窗口或并发授权：" + error);
            Assert.That(block, Is.Not.Null);
            Assert.That(block.IsReaction, Is.True);
            Assert.That(block.IsLocked, Is.True, "反应计划接受后直接 Locked");
            Assert.That(block.BudgetCostTicks, Is.Zero, "反应不消费 TurnBudget");
            Assert.That(block.SubmittedWindowId, Is.Null, "反应计划没有窗口来源");
            Assert.That(block.ReservedTurnBudgetTicks, Is.Zero);

            // TurnBudget 账本完全不被触碰（窗口已关闭且没有任何开放窗口）。
            Assert.That(rig.Ledger.Count, Is.EqualTo(budgetEventsBefore),
                "反应接受不得产生任何 TurnBudget 变化");
            Assert.That(window.ReservedFor(block.ActionPlanId), Is.Zero);
            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(attack.BudgetCostTicks));
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(200 - attack.BudgetCostTicks));

            // 费用只从肾上腺素账本消费/预留：Available -> 带周期的计划预留。
            AdrenalineLedger enemy = rig.Adrenaline.Find(Enemy);
            Assert.That(rig.AdrenalineChanges.Count, Is.EqualTo(adrenalineChangesBefore + 1));
            AdrenalineLedgerChange reserveChange = rig.AdrenalineChanges[rig.AdrenalineChanges.Count - 1];
            Assert.That(reserveChange.ChangeKind, Is.EqualTo(AdrenalineChangeKind.Reserved));
            Assert.That(reserveChange.ReservationPlanId, Is.EqualTo(block.ActionPlanId));
            Assert.That(reserveChange.ReservationAmount, Is.EqualTo(FrozenDesignValues.BlockAdrenalineCost));
            Assert.That(enemy.AvailableAdrenaline,
                Is.EqualTo(adrenalineBefore - FrozenDesignValues.BlockAdrenalineCost));
            Assert.That(enemy.ReservedAdrenaline, Is.EqualTo(FrozenDesignValues.BlockAdrenalineCost));
            Assert.That(enemy.ReservationOf(block.ActionPlanId).ReservationCycleId, Is.Zero,
                "预留必须携带接受时的个人周期号");
            AssertLedgerIdentity(rig);
        }
    }
}
