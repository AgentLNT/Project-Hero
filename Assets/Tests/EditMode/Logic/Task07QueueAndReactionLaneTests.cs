using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 07 <strong>两项已落地生产修复</strong>的仓库内回归证据（纯 Logic 侧、单一文件）：
    ///
    /// <list type="number">
    /// <item><strong>裁定 B：首版支持排队 Move 链。</strong>
    /// <c>LogicGridMovementAuthority.EstablishMovement</c> 新增 <c>expectedOrigin</c> 尾参，
    /// 并且 <c>RebuildMovementSpace</c> 把 <c>ProjectedChainOriginOf(working, i)</c> 同时用于
    /// <strong>算路径</strong>与<strong>校验起点</strong>。此前只用于算路径 ⇒ 链内第二条 Move
    /// 稳定以 <c>MOVEMENT_SEGMENT_ORIGIN_MISMATCH</c> 被拒（本文件用例 1 钉住"修好之后能提交"，
    /// 用例 2 钉住"放宽不是取消校验"）。</item>
    /// <item><strong>裁定 A：Dodge 的固定反应区间允许覆盖"它自己在 TriggerTick 会失效掉的
    /// 后续 Editable 移动族"。</strong><c>ActorLane.TryFindOverlapExcept</c> + 唯一共享容忍判据
    /// <c>DodgeMovementInvalidationSeam.DodgeMayInvalidate</c>（接受预检与规划器复核共用它，
    /// 且它与 <c>QueryInvalidatedMoves</c> 的闭包过滤器逐字一致）。
    /// 用例 3/6 钉住"容忍 + 一定被清理"，用例 4/5 钉住"Block 与不可失效者绝不容忍"。</item>
    /// </list>
    ///
    /// 装配口径<strong>逐字沿用</strong>仓库既有夹具（不发明第三套）：
    /// <c>Task07BudgetTransactionTests</c> 的真实定义 + 真实
    /// <see cref="ActionScheduleAuthority"/>/<see cref="ActionPlanFactory"/>/
    /// <see cref="ScheduleEditor"/>（含真实 <see cref="LogicGridMovementAuthority"/> 空间端口）+
    /// 真实 <see cref="TurnWindowManager"/>/<see cref="TurnWindowBudgetAuthority"/>
    /// （它同时是 <c>IActionPlanStartCommitPort</c> 的真实实现）+ 真实
    /// <see cref="ActionPlanTerminalCoordinator"/> 与真实 <see cref="ReactionOpportunitySystem"/>/
    /// <see cref="DodgeMovementInvalidationSeam"/>。
    ///
    /// 全部观察点都是生产对象：计划的 <c>IsEditable</c>/<c>State</c>/<c>StartTick</c>、
    /// Lane 的 <c>Plans</c>、窗口账本的 <c>ReservedFor</c>、
    /// <see cref="LogicGridMovementAuthority.SegmentsOfPlanOrdered"/> 与
    /// <c>Grid.ReservationsOfPlanOrdered</c>、以及生产接受入口返回的稳定拒绝码。
    ///
    /// <para>
    /// ⚠ <strong>本文件曾实测定位的第三个独立缺口（已由生产修复，保留为记录）</strong>：
    /// 用例 1 的 (B) 段（回程链：链内第二条重新经过前一条进入过的格）在修复前稳定报
    /// <c>LOGIC_GRID_RESERVED_BY_OTHER</c>——它既不是链内起点判据（那一半由
    /// <c>expectedOrigin</c> 修复，见 (A) 段与用例 2），也不是夹具问题，
    /// 而是"网格 Reservation 一格一持有者 + <c>LogicPathfinder</c> 对本单位自己的预留放行"
    /// 这一对语义的组合。该形态现在通过（生产侧已允许同一格上的共存预留），
    /// 机制记录见文件末尾 §生产缺陷登记 ①。
    /// </para>
    /// </summary>
    public class Task07QueueAndReactionLaneTests
    {
        // ================= 夹具常量 =================

        private const string MoveSpecId = "action.t07ql.move";
        private const string GuardSpecId = "action.t07ql.guard";
        private const string AttackSpecId = "action.t07ql.attack";
        private const string BlockSpecId = "action.t07ql.block";
        private const string DodgeSpecId = "action.t07ql.dodge";
        private const string ActionSetIdValue = "action_set.t07ql";

        /// <summary>单位速度恒等于基准速度（20）⇒ 前摇/每权重单位 Tick 的解析是恒等映射。</summary>
        private static readonly BattleRules Rules = BattleRules.FrozenV1;

        private const int MoveBaseStepTicks = 5;
        private const int MoveRecoveryTicks = 5;
        private const int GuardWindupTicks = 10;
        private const int GuardActiveTicks = 20;
        private const int GuardRecoveryTicks = 5;
        private const int AttackBaseWindupTicks = 100;
        private const int AttackRecoveryTicks = 10;

        /// <summary>反应后摇（Block/Dodge 共用；前摇取冻结设计值）。</summary>
        private const int ReactionRecoveryTicks = 10;

        /// <summary>夹具时序（全部由生产规则推导，见 <see cref="OpenTelegraphedAttack"/> 的断言）。</summary>
        private const long AttackStartTick = 0L;

        private const long TelegraphTick = 0L;

        /// <summary>攻击 <c>ImpactTick</c> = 0 + 100 ⇒ 反应 <c>TriggerTick</c> 恒等于它。</summary>
        private const long TriggerTick = AttackBaseWindupTicks;

        /// <summary>被容忍/被容忍地重叠的 Editable Move 的起点（必须落在 Dodge 固定区间内）。</summary>
        private const long OverlappingMoveStartTick = 80L;

        private static readonly GridBoundaryDefinition Wide =
            new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40));

        private static readonly UnitId Hero = new UnitId(1L);
        private static readonly UnitId Enemy = new UnitId(2L);
        private static readonly FactionId HeroFaction = new FactionId("faction.hero");
        private static readonly FactionId MonsterFaction = new FactionId("faction.monster");
        private static readonly ControllerId Player = new ControllerId("controller.player");
        private static readonly ControllerId EnemyAi = new ControllerId("controller.enemy_ai");

        private static ActionPlanId P(long id) => new ActionPlanId(id);
        private static ActionSpecId Spec(string id) => new ActionSpecId(id);

        // ================= 定义 =================

        private static IReadOnlyList<DirectionalTriangleSet> Directions()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        private static MovementPatternSpec MovementPattern()
            => new MovementPatternSpec(new MovementPatternId("movement_pattern.t07ql"), Directions());

        /// <summary>
        /// 与 <c>Task07BudgetTransactionTests.BuildDefinition</c> 同形的定义，只有两处任务 07 需要的差异：
        /// 攻击的 <c>StartTick = 0</c>（TelegraphTick 0、ImpactTick = 前摇 100）被本文件当作
        /// "已经锁定/启动的来源攻击"，并且 Dodge/Block 的固定区间由冻结前摇唯一决定。
        /// </summary>
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

            // 长前摇攻击：StartTick + 100 = ImpactTick ⇒ 反应选项截止 Tick = ImpactTick - 前摇，
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
                new BlockReactionTimingSpec(FrozenDesignValues.BlockReactionWindupTicks, ReactionRecoveryTicks),
                new BlockPayloadSpec(DefenseTagMask.Blockable),
                AdrenalineCost: FrozenDesignValues.BlockAdrenalineCost);

            var dodgeSpec = new ActionSpec(
                Spec(DodgeSpecId), ActionType.Dodge,
                new DodgeReactionTimingSpec(FrozenDesignValues.DodgeReactionWindupTicks, ReactionRecoveryTicks),
                new DodgePayloadSpec(MaxDistanceSteps: 4, Pattern: MovementPattern()),
                AdrenalineCost: FrozenDesignValues.DodgeAdrenalineCost);

            var volume = new VolumeSpec(new VolumeSpecId("unit_volume.t07ql"), Directions());
            var actionSet = new ActionSetDefinition(
                new ActionSetId(ActionSetIdValue),
                new List<ActionSpecId>
                {
                    moveSpec.ActionSpecId, guardSpec.ActionSpecId, attackSpec.ActionSpecId,
                    blockSpec.ActionSpecId, dodgeSpec.ActionSpecId
                });

            var units = new List<UnitDefinition>
            {
                new UnitDefinition(new UnitDefinitionId("unit.t07ql.hero"), 10f, 10f,
                    Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), 200f,
                    actionSet.ActionSetId, volume.VolumeSpecId),
                new UnitDefinition(new UnitDefinitionId("unit.t07ql.enemy"), 10f, 10f,
                    Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), 200f,
                    actionSet.ActionSetId, volume.VolumeSpecId)
            };

            return new BattleDefinition(
                "battle-definition.task07.queue-and-reaction-lane",
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
                "test-definition-hash.t07.queue-and-reaction-lane");
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
            public ScheduleEvaluator Evaluator;
            public ActionPlanTerminalCoordinator Coordinator;
            public ReactionOpportunitySystem Reactions;
            public DodgeMovementInvalidationSeam Seam;
            public TurnWindowManager Windows;
            public WorldView World;
            public TurnWindowBudgetAuthority Budget;

            /// <summary>生产预算端口发射的账本变化载荷。</summary>
            public readonly List<TurnBudgetChange> Ledger = new List<TurnBudgetChange>();

            /// <summary>最近一次公开机会时新建的机会对象（生产入口的 out 收集器）。</summary>
            public readonly List<ReactionOpportunityRuntime> Opened = new List<ReactionOpportunityRuntime>();
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

        /// <summary>
        /// 生产形状的装配：真实排程事务（<c>MovementSpacePort = LogicGridMovementAuthority</c>，
        /// 与 <c>BattleSimulation</c> 第 385 行同一行代码）、真实预算端口、真实机会系统与真实 Dodge 接缝。
        /// 肾上腺素端口刻意留空：<c>ReactionOpportunitySystem</c> 的文档化语义是"未设置时保持
        /// 任务 05/06 既有语义"（既不改变任何既有拒绝码、也不改变预留行为），
        /// 因此本文件聚焦的"Lane 固定区间占位判据"与"链内预测起点"两条被测行为
        /// 不会被肾上腺素账本的预留/回滚分支干扰（本文件也不对肾上腺素做任何断言）。
        /// </summary>
        private static Rig NewRig()
        {
            BattleDefinition definition = BuildDefinition();
            var actionSetId = new ActionSetId(ActionSetIdValue);
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
            // 生产装配：排程事务 → 空间整批替换（裁定 B 的被测端口）。
            editor.MovementSpacePort = movement;

            var world = new WorldView();
            var windows = new TurnWindowManager(world, ids.NextWindowId);
            var budget = new TurnWindowBudgetAuthority(windows, null);
            var coordinator = new ActionPlanTerminalCoordinator(schedule, new IActionPlanCleanupParticipant[]
            {
                StopSchedulingCleanupParticipant.Instance,
                movement
            });
            var reactions = new ReactionOpportunitySystem(
                schedule, factory, definition, BuildResolver(definition), ids, null, coordinator);
            var evaluator = new ScheduleEvaluator(schedule, calculator);
            var seam = new DodgeMovementInvalidationSeam(schedule, evaluator, coordinator, reactions);

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
                Evaluator = evaluator,
                Coordinator = coordinator,
                Reactions = reactions,
                Seam = seam,
                Windows = windows,
                World = world,
                Budget = budget
            };

            budget.ChangedSink = rig.Ledger.Add;

            // 排程事务的预算上下文 = 生产装配的同一形状（BattleSimulation 构造函数里的那个闭包）。
            editor.BudgetContextFactory = (source, targetTick, expectedWindowId, issuer) =>
                new ScheduleBudgetContext(
                    source, budget, expectedWindowId,
                    windows.CurrentWindow != null ? windows.CurrentWindow.WindowId : (WindowId?)null,
                    issuer);

            windows.RegisterControllerBinding(Player, Hero);
            windows.RegisterControllerBinding(EnemyAi, Enemy);

            Assert.That(grid.RegisterUnit(Hero, new GridPoint(0, 0), GridDirection.East, Directions()),
                Is.Null, "夹具前提：hero 必须落在 (0,0)（网格锚点）");
            Assert.That(grid.RegisterUnit(Enemy, new GridPoint(0, 30), GridDirection.East, Directions()),
                Is.Null, "夹具前提：enemy 必须落在 (0,30)");
            return rig;
        }

        // ================= 夹具动作 =================

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
            Rig rig, long tick, WindowId? expectedWindow, ControllerId issuer, ScheduleEditOperation operation)
        {
            ScheduleEditTransactionResult result = Apply(rig, tick, expectedWindow, issuer, operation);
            Assert.That(result.RejectionCode, Is.Null,
                "夹具前提：排程事务必须成功（" + result.RejectionCode + "）");
            Assert.That(result.Committed, Is.True);
            Assert.That(result.AddedPlanIds.Count, Is.EqualTo(1));
            ActionPlan plan = rig.Schedule.Registry.Find(result.AddedPlanIds[0]);
            Assert.That(plan, Is.Not.Null, "新增计划必须进入权威注册表");
            return plan;
        }

        /// <summary>经真实排程事务新增普通 Move（路径投影由生产求值器 + 真实 Pathfinder 重算）。</summary>
        private static ActionPlan AddMove(
            Rig rig, long tick, long startTick, int toX, int toY,
            UnitId owner = default(UnitId), WindowId? expectedWindow = null)
            => AddOrFail(rig, tick, expectedWindow, owner.IsValid && owner == Enemy ? EnemyAi : Player,
                new AddOrdinaryPlanOperation(
                    startTick + 2000L, owner.IsValid ? owner : Hero, Spec(MoveSpecId), startTick,
                    AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                    Facing: GridDirection.East, Destination: new GridPoint(toX, toY)));

        /// <summary>经真实排程事务新增普通 Guard。</summary>
        private static ActionPlan AddGuard(Rig rig, long tick, long startTick, WindowId? expectedWindow = null)
            => AddOrFail(rig, tick, expectedWindow, Player,
                new AddOrdinaryPlanOperation(
                    startTick + 1000L, Hero, Spec(GuardSpecId), startTick,
                    AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                    Facing: GridDirection.East, Destination: null));

        /// <summary>经真实排程事务新增普通 Attack（PrimaryTargetOnly + Hostile 关系）。</summary>
        private static ActionPlan AddAttack(
            Rig rig, long tick, long startTick, UnitId owner, UnitId target, WindowId? expectedWindow = null)
            => AddOrFail(rig, tick, expectedWindow, owner == Enemy ? EnemyAi : Player,
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
        }

        /// <summary>
        /// 经<strong>生产</strong>启动提交端口（<see cref="TurnWindowBudgetAuthority.Commit"/>，
        /// 即 <c>IActionPlanStartCommitPort</c> 的真实实现）完成 <c>Reserved -&gt; Spent</c>，
        /// 再写入"启动门禁已经成功"的两个生命周期标记（见 §反射点说明）。
        /// </summary>
        private static void StartAndLock(Rig rig, ActionPlan plan, long tick)
        {
            Assert.That(plan.IsEditable, Is.True, "夹具前提：只有 Editable 计划需要启动提交");
            Assert.That(plan.SubmittedWindowId.HasValue, Is.True, "夹具前提：计划必须有来源窗口");
            int cost = plan.BudgetCostTicks;
            Assert.That(cost, Is.GreaterThan(0), "夹具前提：普通计划成本必须为正整数 Tick");

            string error = rig.Budget.Commit(plan, tick, _ => { });
            Assert.That(error, Is.Null, "生产启动提交端口必须成功：" + error);
            MarkLockedAndRunning(plan, tick);
        }

        // ================= 反射点说明（本文件唯一的非公共访问：两处属性写入）=================
        //
        // ActionPlan.State / ActionPlan.LockedAtTick 的写入面是 internal，而
        // ProjectHero.Logic.Tests 没有 InternalsVisibleTo（见 Assets/Scripts/Logic/AssemblyInfo.cs：
        // 该授权只给 ProjectHero.Authoring.Tests）。生产路径里唯一写这两个字段的位置是
        // BattleSimulation.CommitStart，它需要完整的 Encounter/Controller/资产初始化链。
        // 因此这里按 Task07BudgetTransactionTests / Task07ForcedDisplacementBudgetTests 的
        // 既有口径，只写这两个生命周期标记；资源侧的 Reserved -> Spent 完全由生产端口
        // TurnWindowBudgetAuthority.Commit 完成，本文件不做任何账本/排程推断。
        // =====================================================================================

        private static readonly PropertyInfo StateSetter = typeof(ActionPlan).GetProperty(nameof(ActionPlan.State));

        private static readonly PropertyInfo LockedAtTickSetter =
            typeof(ActionPlan).GetProperty(nameof(ActionPlan.LockedAtTick));

        private static void MarkLockedAndRunning(ActionPlan plan, long lockedAtTick)
        {
            Assert.That(StateSetter, Is.Not.Null, "ActionPlan.State 必须存在（夹具不变量）");
            Assert.That(LockedAtTickSetter, Is.Not.Null, "ActionPlan.LockedAtTick 必须存在（夹具不变量）");
            StateSetter.SetValue(plan, ActionPlanState.Running);
            LockedAtTickSetter.SetValue(plan, lockedAtTick);
            Assert.That(plan.IsRunning, Is.True, "夹具前提：标记写入必须生效");
            Assert.That(plan.IsEditable, Is.False);
        }

        // ================= 夹具动作（反应几何）=================

        /// <summary>把整条链的段拼回一条路径（首点 + 每一段的终点）。</summary>
        private static List<GridPoint> PathOf(IReadOnlyList<MovementSegment> segments)
        {
            var path = new List<GridPoint>(segments.Count + 1);
            if (segments.Count == 0) return path;
            path.Add(segments[0].From);
            for (int i = 0; i < segments.Count; i++) path.Add(segments[i].To);
            return path;
        }

        /// <summary>只读诊断：某单位 Lane 的计划投影（失败消息里用来定位几何）。</summary>
        private static string LaneDump(Rig rig, UnitId unitId)
        {
            ActorLane lane = rig.Schedule.FindLane(unitId);
            if (lane == null) return "lane#" + unitId.Value + "=null";
            var parts = new List<string>();
            IReadOnlyList<ActionPlan> plans = lane.Plans;
            for (int i = 0; i < plans.Count; i++)
            {
                parts.Add("#" + plans[i].ActionPlanId.Value + ":" + plans[i].ActionType +
                          "[" + plans[i].StartTick + "," + plans[i].EndTick + ")" +
                          " editable=" + plans[i].IsEditable +
                          " moveFam=" + plans[i].IsMovementFamily);
            }
            return "lane#" + unitId.Value + "[" + string.Join(" | ", parts) + "]";
        }

        /// <summary>
        /// 公开一条"已锁定/启动的来源攻击"的反应机会（攻击者是 Enemy，防御者是 Hero，
        /// 因此 Dodge/Block 计划落在 <strong>Hero</strong> 的 Lane 上）。
        ///
        /// 夹具时序（由生产规则唯一推导，方法内逐条断言）：
        /// <c>StartTick = 0</c>、<c>ImpactTick = 100 = TriggerTick</c>、
        /// Dodge 固定区间 = <c>[TriggerTick - 30, TriggerTick + 10) = [70, 110)</c>、
        /// Block 固定区间 = <c>[TriggerTick - 60, TriggerTick + 10) = [40, 110)</c>。
        /// </summary>
        private static ReactionOpportunityId OpenTelegraphedAttack(Rig rig)
        {
            TurnWindow window = OpenWindow(rig, 0L, Enemy, 400);
            ActionPlan attack = AddAttack(rig, 0L, AttackStartTick, Enemy, Hero, window.WindowId);
            StartAndLock(rig, attack, AttackStartTick);
            Assert.That(attack.ImpactTick, Is.EqualTo(TriggerTick),
                "夹具前提：TriggerTick 只能等于来源攻击的 ImpactTick");
            Assert.That(attack.EndTick, Is.EqualTo(TriggerTick + AttackRecoveryTicks));

            rig.Opened.Clear();
            string openError = rig.Reactions.TryOpenForTelegraph(attack, TelegraphTick, rig.Opened);
            Assert.That(openError, Is.Null, "已锁定/启动的反应攻击必须公开机会：" + openError);
            Assert.That(rig.Opened.Count, Is.EqualTo(1));
            Assert.That(rig.Opened[0].DefenderUnitId, Is.EqualTo(Hero), "夹具前提：防御者必须是 Hero");
            Assert.That(rig.Opened[0].TriggerTick, Is.EqualTo(TriggerTick));
            Assert.That(rig.Opened[0].OpenOptionCount, Is.EqualTo(2),
                "夹具前提：Block 与 Dodge 两个选项都必须被公开");

            // 攻击的预算来源窗口关闭后再打开 Hero 的窗口：Hero 的计划（Move/Guard）
            // 必须记在 Hero 自己的窗口账本上。
            CloseWindow(rig, window, AttackStartTick);
            return rig.Opened[0].Id;
        }

        /// <summary>Dodge 的固定反应区间左端（= TriggerTick - 反应前摇）。</summary>
        private static long DodgeIntervalStart()
            => TriggerTick - FrozenDesignValues.DodgeReactionWindupTicks;

        /// <summary>Dodge 的固定反应区间右端（半开区间 = TriggerTick + 后摇）。</summary>
        private static long DodgeIntervalEnd() => TriggerTick + ReactionRecoveryTicks;

        /// <summary>Block 的固定反应区间左端。</summary>
        private static long BlockIntervalStart()
            => TriggerTick - FrozenDesignValues.BlockReactionWindupTicks;

        // =====================================================================
        // 1. QueuedMoveChainCommitsBothPlansThroughRealSpacePort（裁定 B 的回归钉子）
        //
        // 会让它失败的实现缺陷：把 EstablishMovement 的 expectedOrigin 尾参去掉
        // （或 RebuildMovementSpace 不再把 ProjectedChainOriginOf(working, i) 传给它）⇒
        // 链内第二条 Move 的路径首点 = 前一条的目的格 ≠ 单位当前锚点 ⇒
        // 空间端口以 MOVEMENT_SEGMENT_ORIGIN_MISMATCH 返回 ⇒ ScheduleEditor 把已改写的排程投影
        // 整批回滚并拒绝该事务 ⇒ 本用例在"第二条 Move 的排程事务必须提交"这一步立刻变红。
        // 该等价性同时由用例 2 的 (a) 分支以公共 API 直接钉住（不传 expectedOrigin 就是修复前的调用形态）。
        //
        // 本用例含两段（各自独立夹具，互不干扰）：
        //   (A) 链内第二条的目的格离开前一条的已占格（(0,0)->(0,4) 接 (0,4)->(0,8)）：
        //       直接孤立地验证"链内预测起点"这一半，今天即可通过。
        //   (B) 回程链（(0,0)->(0,4) 接 (0,4)->(0,-4)：后一条重新进入前一条已占的格）：
        //       同一条链在空间侧还要求"同一单位可复用同一格（不相交时间窗）"的预留语义；
        //       该语义落地前，这一段稳定报 LOGIC_GRID_RESERVED_BY_OTHER
        //       （而不是 MOVEMENT_SEGMENT_ORIGIN_MISMATCH）——它如实区分了
        //       "起点校验那一半"与"预留复用那一半"是两个独立缺口。
        //       生产修复落地后本段已通过（见文件末尾 §生产缺陷登记 ①）。
        // =====================================================================
        [Test]
        public void QueuedMoveChainCommitsBothPlansThroughRealSpacePort()
        {
            // ————————————————————————————————————————————————————————————
            // (A) 链内起点离开前一条的已占格 ⇒ 只依赖 expectedOrigin（今天可验证）
            // ————————————————————————————————————————————————————————————
            Rig rig = NewRig();
            Assert.That(rig.Editor.MovementSpacePort, Is.SameAs(rig.Movement),
                "装配前提：排程事务必须接真实空间端口（生产装配）");

            // 窗口额度 90 覆盖链的两条计划（各 25）。
            TurnWindow window = OpenWindow(rig, 0L, Hero, 90);
            // 链几何：x 从网格锚点 (0,0) 向北 4 格；y 从 x 的目的格 (0,4) 继续向北 4 格。
            // 这一段刻意让两条路径的**已占格集合不相交**（(0,2)/(0,4) 与 (0,6)/(0,8)），
            // 从而把"链内预测起点"这一半与下面的 (B) 预留批处理那一半隔离开。
            ActionPlan first = AddMove(rig, 0L, 0L, 0, 4, expectedWindow: window.WindowId);
            ActionPlan second = AddMove(rig, 0L, 100L, 0, 8, expectedWindow: window.WindowId);

            // —— 两条排队 Move 都提交成功且仍在 Editable（AddMove 已断言事务提交）——
            Assert.That(first.IsEditable, Is.True);
            Assert.That(second.IsEditable, Is.True);
            Assert.That(first.ActionType, Is.EqualTo(ActionType.Move));
            Assert.That(second.ActionType, Is.EqualTo(ActionType.Move));

            // —— 窗口账本：各自在窗口里持有自己的预留 ——
            Assert.That(window.ReservedFor(first.ActionPlanId), Is.EqualTo(first.BudgetCostTicks));
            Assert.That(window.ReservedFor(second.ActionPlanId), Is.EqualTo(second.BudgetCostTicks));
            Assert.That(first.ReservedTurnBudgetTicks, Is.EqualTo(first.BudgetCostTicks));
            Assert.That(second.ReservedTurnBudgetTicks, Is.EqualTo(second.BudgetCostTicks));
            Assert.That(window.SpentBudgetTicks, Is.Zero, "Editable 阶段绝不消费预算");
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(first.BudgetCostTicks + second.BudgetCostTicks));

            // 账本变化载荷：恰好两条 Reserved（每条计划一条），且都来自显式排程编辑、都落在同一个窗口。
            int reservedEvents = 0;
            for (int i = 0; i < rig.Ledger.Count; i++)
            {
                TurnBudgetChange change = rig.Ledger[i];
                Assert.That(change.SpentAfter, Is.Zero, "Editable 阶段不得出现任何消费");
                if (change.ChangeKind != TurnBudgetChangeKind.Reserved) continue;
                reservedEvents++;
                Assert.That(change.Source, Is.EqualTo(ResourceChangeSource.ExplicitScheduleEdit));
                Assert.That(change.WindowId, Is.EqualTo(window.WindowId));
                Assert.That(change.ActionPlanId.HasValue, Is.True);
            }
            Assert.That(reservedEvents, Is.EqualTo(2), "两条排队 Move 各产生恰好一次首次预留");

            // —— 链几何前提：第二条从"前一条的终点"起算（不是从锚点起算）——
            Assert.That(first.Destination, Is.EqualTo(new GridPoint(0, 4)));
            Assert.That(second.Destination, Is.EqualTo(new GridPoint(0, 8)));
            Assert.That(first.ResolvedPathWeightUnits, Is.EqualTo(4),
                "夹具前提：(0,0) -> (0,4) 的路径权重是 4（两条奇数方向边，各权重 2）");
            Assert.That(second.ResolvedPathWeightUnits, Is.EqualTo(4),
                "链内第二条的路径权重是 4：(0,4) -> (0,8) 是两条边；" +
                "若它按锚点 (0,0) 起算就会是 8（四条边）⇒ 该数字证明路径确实是用链内起点算出来的");
            Assert.That(first.BudgetCostTicks, Is.EqualTo((4 * MoveBaseStepTicks) + MoveRecoveryTicks));
            Assert.That(second.BudgetCostTicks, Is.EqualTo((4 * MoveBaseStepTicks) + MoveRecoveryTicks));

            // —— 段链：第二条的段链首点 == 第一条的目的格 ——
            IReadOnlyList<MovementSegment> firstSegments = rig.Movement.SegmentsOfPlanOrdered(first.ActionPlanId);
            IReadOnlyList<MovementSegment> secondSegments = rig.Movement.SegmentsOfPlanOrdered(second.ActionPlanId);
            Assert.That(firstSegments.Count, Is.EqualTo(2), "夹具前提：(0,0)->(0,4) 是 2 段");
            Assert.That(secondSegments.Count, Is.EqualTo(2), "夹具前提：(0,4)->(0,8) 是 2 段");
            Assert.That(firstSegments[0].From, Is.EqualTo(new GridPoint(0, 0)),
                "链首计划仍然按单位当前权威锚点起算");
            Assert.That(secondSegments[0].From, Is.EqualTo(first.Destination.Value),
                "裁定 B：链内第二条的段链首点必须等于前一条链内计划的目的格");
            Assert.That(secondSegments[0].From, Is.EqualTo(firstSegments[firstSegments.Count - 1].To),
                "整条链必须连续（前一条的末端 == 后一条的首端）");
            Assert.That(PathOf(secondSegments)[0], Is.EqualTo(new GridPoint(0, 4)));

            // —— 空间不变量：段链连续、每段都有同键 Reservation、段数 == 预留数 ——
            Assert.That(rig.Movement.VerifyInvariants(), Is.Null, "空间批次必须自洽");
            Assert.That(rig.Grid.ReservationsOfPlanOrdered(first.ActionPlanId).Count,
                Is.EqualTo(firstSegments.Count));
            Assert.That(rig.Grid.ReservationsOfPlanOrdered(second.ActionPlanId).Count,
                Is.EqualTo(secondSegments.Count));

            // —— 排程：两条都在 Hero 的 Lane 上、修订号恰好 +2（两次成功事务）——
            ActorLane lane = rig.Schedule.FindLane(Hero);
            Assert.That(lane, Is.Not.Null);
            Assert.That(lane.Count, Is.EqualTo(2));
            Assert.That(lane.ValidateNonOverlapping(), Is.Null);
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(2L));

            // ————————————————————————————————————————————————————————————
            // (B) 回程链：后一条重新进入前一条已经进入过（并仍持有 Reservation）的格
            //     (0,0) -> (0,4) 接 (0,4) -> (0,-4)：第二条会重新经过 (0,2)。
            //     这一段是"排队 Move 链"的真实语义（同一单位的连续段链），
            //     它额外要求空间侧支持同一单位在同一格上的共存预留（见 §生产缺陷登记 ①）。
            // ————————————————————————————————————————————————————————————
            Rig reentrant = NewRig();
            TurnWindow reentrantWindow = OpenWindow(reentrant, 0L, Hero, 90);
            ActionPlan head = AddMove(reentrant, 0L, 0L, 0, 4, expectedWindow: reentrantWindow.WindowId);

            ScheduleEditTransactionResult tailTx = Apply(
                reentrant, 0L, reentrantWindow.WindowId, Player,
                new AddOrdinaryPlanOperation(
                    2100L, Hero, Spec(MoveSpecId), 100L,
                    AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                    Facing: GridDirection.East, Destination: new GridPoint(0, -4)));

            Assert.That(tailTx.RejectionCode, Is.Null,
                "裁定 B（真实链 + 真实空间端口）：链内第二条 Move 必须与前一条共存于同一个空间批次。" +
                " head=[" + head.StartTick + "," + head.EndTick + ") dest=" + head.Destination +
                " tail=start100 dest=(0,-4) lane=" + LaneDump(reentrant, Hero) +
                " reservations=" + reentrant.Grid.AllReservationsOrdered().Count +
                " rejection=" + tailTx.RejectionCode);

            ActionPlan tail = reentrant.Schedule.Registry.Find(tailTx.AddedPlanIds[0]);
            Assert.That(tail, Is.Not.Null);
            Assert.That(tail.IsEditable, Is.True);
            IReadOnlyList<MovementSegment> tailSegments =
                reentrant.Movement.SegmentsOfPlanOrdered(tail.ActionPlanId);
            Assert.That(tailSegments.Count, Is.GreaterThan(0));
            Assert.That(tailSegments[0].From, Is.EqualTo(head.Destination.Value),
                "回程链的第二条也必须从链内预测起点（前一条的目的格）接上");
            Assert.That(reentrant.Movement.VerifyInvariants(), Is.Null);
            Assert.That(reentrantWindow.ReservedFor(tail.ActionPlanId), Is.EqualTo(tail.BudgetCostTicks));
        }

        // =====================================================================
        // 2. QueuedMoveChainOriginMismatchIsStillRejectedForNonChainedStart（负控制）
        //
        // 会让它失败的实现缺陷：把"起点校验"整体删掉（而不是把合法起点放宽到链内预测起点）⇒
        // (a)/(b)/(c) 三个分支都会变成"提交成功"，断言随之变红。
        // 反过来，如果实现把放宽做成"任何起点都接受"，(a) 也会变红——它正是修复前的调用形态。
        //
        // 夹具口径：链首经**真实排程事务 + 真实空间端口**提交（段与 Reservation 都是真的），
        // 链内第二条用同一个生产端口的**纯数据入口**
        // <c>EstablishMovement(planId, unitId, path, startTick, baseStepTicks, destination, expectedOrigin)</c>
        // 建立——这正是本用例要钉住的那个入口本身（Task06 系列夹具也用同一形态直接驱动它），
        // 路径由生产 <see cref="LogicPathfinder"/> 求出，没有替身、没有绕过端口。
        // 链几何选"回程"（(0,0)->(0,2) 接 (0,2)->(0,0)）：它满足"链式起点 = 前一条终点"，
        // 又不触发"一格一持有者"的预留复用（后一条只进入前一条从未进入过的 (0,0)），
        // 从而让本用例只依赖"起点判据"这一件事。
        // =====================================================================
        [Test]
        public void QueuedMoveChainOriginMismatchIsStillRejectedForNonChainedStart()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, 0L, Hero, 90);
            // 链首：经真实事务提交（1 段：进入 (0,2)）。
            ActionPlan first = AddMove(rig, 0L, 0L, 0, 2, expectedWindow: window.WindowId);
            Assert.That(first.Destination, Is.EqualTo(new GridPoint(0, 2)));
            Assert.That(rig.Movement.SegmentsOfPlanOrdered(first.ActionPlanId).Count, Is.EqualTo(1));

            Assert.That(rig.Grid.TryGetAnchor(Hero, out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(anchor, Is.Not.EqualTo(first.Destination.Value),
                "夹具前提：网格锚点与前一条终点必须是两个不同的格，否则本用例失去意义");

            // 链内第二条：纯数据入口（尚未接入权威的计划没有段/预留，因此"零写入"可直接观察）。
            var secondId = new ActionPlanId(rig.Ids.NextActionPlanIdValue);
            var secondDestination = new GridPoint(0, 0);
            const long secondStartTick = 100L;
            PathSearchResult path = rig.Movement.Pathfinder.FindPath(
                first.Destination.Value, secondDestination, Hero, secondId);
            Assert.That(path.Succeeded, Is.True, "夹具前提：生产寻路必须给出链内路径：" + path.FailureCode);
            var chainedPath = new List<GridPoint>(path.Path);
            Assert.That(chainedPath[0], Is.EqualTo(first.Destination.Value));

            int allReservationsBefore = rig.Grid.AllReservationsOrdered().Count;
            int headSegmentsBefore = rig.Movement.SegmentsOfPlanOrdered(first.ActionPlanId).Count;

            // (a) 修复前的调用形态：不传 expectedOrigin ⇒ 校验单位当前锚点 (0,0)，
            //     而链内路径的首点是 (0,2) ⇒ 必须仍然以 MOVEMENT_SEGMENT_ORIGIN_MISMATCH 拒绝。
            MovementReplacementResult preFixCall = rig.Movement.EstablishMovement(
                secondId, Hero, chainedPath, secondStartTick, MoveBaseStepTicks, secondDestination);
            Assert.That(preFixCall.Succeeded, Is.False,
                "去掉 expectedOrigin 后链式起点必须被拒（这正是裁定 B 修复前后的行为差）");
            Assert.That(preFixCall.FailureCode, Is.EqualTo(MovementCodes.MOVEMENT_SEGMENT_ORIGIN_MISMATCH));

            // (b) 显式声明一个既不是锚点也不是前一条终点的起点 ⇒ 仍然拒绝。
            MovementReplacementResult wrongDeclaredOrigin = rig.Movement.EstablishMovement(
                secondId, Hero, chainedPath, secondStartTick, MoveBaseStepTicks, secondDestination,
                new GridPoint(3, 3));
            Assert.That(wrongDeclaredOrigin.Succeeded, Is.False);
            Assert.That(wrongDeclaredOrigin.FailureCode,
                Is.EqualTo(MovementCodes.MOVEMENT_SEGMENT_ORIGIN_MISMATCH));

            // (c) 路径首点既不是网格锚点也不是前一条终点（(2,2)）：无论声明链内起点还是锚点都必须拒绝。
            var strayPath = new List<GridPoint>
            {
                new GridPoint(2, 2), new GridPoint(2, 4), new GridPoint(2, 6)
            };
            MovementReplacementResult strayWithChainOrigin = rig.Movement.EstablishMovement(
                secondId, Hero, strayPath, secondStartTick, MoveBaseStepTicks, new GridPoint(2, 6),
                first.Destination);
            Assert.That(strayWithChainOrigin.Succeeded, Is.False,
                "放宽只对「链内预测起点」生效：路径首点与声明的起点不一致时仍必须拒绝");
            Assert.That(strayWithChainOrigin.FailureCode,
                Is.EqualTo(MovementCodes.MOVEMENT_SEGMENT_ORIGIN_MISMATCH));

            MovementReplacementResult strayWithAnchor = rig.Movement.EstablishMovement(
                secondId, Hero, strayPath, secondStartTick, MoveBaseStepTicks, new GridPoint(2, 6));
            Assert.That(strayWithAnchor.Succeeded, Is.False);
            Assert.That(strayWithAnchor.FailureCode, Is.EqualTo(MovementCodes.MOVEMENT_SEGMENT_ORIGIN_MISMATCH));

            // 全部拒绝路径必须零写入：链内第二条既没有段也没有 Reservation，
            // 链首的段与其它 Reservation 一个都不能被释放/改写。
            Assert.That(rig.Movement.SegmentsOfPlanOrdered(secondId).Count, Is.Zero);
            Assert.That(rig.Grid.ReservationsOfPlanOrdered(secondId).Count, Is.Zero);
            Assert.That(rig.Grid.AllReservationsOrdered().Count, Is.EqualTo(allReservationsBefore));
            Assert.That(rig.Movement.SegmentsOfPlanOrdered(first.ActionPlanId).Count,
                Is.EqualTo(headSegmentsBefore));
            Assert.That(rig.Movement.VerifyInvariants(), Is.Null);
            Assert.That(first.IsEditable, Is.True);

            // (d) 对照：完全相同的链内路径 + 显式链内起点 ⇒ 成功。
            //     它证明 (a) 的失败只源于"起点判据"，不是路径形状/时长/预留冲突。
            MovementReplacementResult chainedAccepted = rig.Movement.EstablishMovement(
                secondId, Hero, chainedPath, secondStartTick, MoveBaseStepTicks, secondDestination,
                first.Destination);
            Assert.That(chainedAccepted.Succeeded, Is.True, chainedAccepted.FailureCode);
            IReadOnlyList<MovementSegment> after = rig.Movement.SegmentsOfPlanOrdered(secondId);
            Assert.That(after.Count, Is.EqualTo(chainedPath.Count - 1));
            Assert.That(after[0].From, Is.EqualTo(first.Destination.Value));
            Assert.That(rig.Movement.VerifyInvariants(), Is.Null);
        }

        // =====================================================================
        // 3. DodgeAcceptanceToleratesSubsequentEditableMoveOverlap（裁定 A 的回归钉子）
        //
        // 会让它失败的实现缺陷：把 ReactionOpportunitySystem 的接受预检（或 ReactionPlanner 的复核）
        // 换回 lane.TryFindOverlap(...)（即撤销裁定 A / 让两处判据分叉）⇒ 接受事务在创建计划之前
        // 就返回 REACTION_LANE_INTERVAL_OCCUPIED，本用例的 error == null 立刻变红。
        // 反向的"假通过"同样被钉住：容忍判据若被放宽成"忽略一切重叠"，用例 4/5 会变红。
        // 生产侧的两处 Lane 检查共用唯一判据
        // DodgeMovementInvalidationSeam.DodgeMayInvalidate（本用例直接调用它做判据对照）。
        // =====================================================================
        [Test]
        public void DodgeAcceptanceToleratesSubsequentEditableMoveOverlap()
        {
            Rig rig = NewRig();
            ReactionOpportunityId opportunityId = OpenTelegraphedAttack(rig);

            // Hero 的窗口：承载"后续 Editable Move"（起点落在 Dodge 固定区间内）。
            TurnWindow heroWindow = OpenWindow(rig, 1L, Hero, 400);
            ActionPlan move = AddMove(rig, 1L, OverlappingMoveStartTick, 0, 4, expectedWindow: heroWindow.WindowId);

            long intervalStart = DodgeIntervalStart();
            long intervalEnd = DodgeIntervalEnd();
            Assert.That(intervalStart, Is.EqualTo(70L));
            Assert.That(intervalEnd, Is.EqualTo(110L));

            // —— 几何前提（否则用例退化成空断言）——
            Assert.That(move.IsEditable, Is.True);
            Assert.That(move.IsMovementFamily, Is.True);
            Assert.That(move.StartTick, Is.EqualTo(OverlappingMoveStartTick));
            Assert.That(move.StartTick, Is.GreaterThanOrEqualTo(intervalStart),
                "夹具前提：被容忍的 Move 起点必须落在 Dodge 固定区间内（且必须是「后续」的）");
            Assert.That(move.StartTick, Is.LessThan(intervalEnd),
                "夹具前提：被容忍的 Move 必须与 Dodge 固定区间相交（半开区间）");

            ActorLane lane = rig.Schedule.FindLane(Hero);
            Assert.That(lane, Is.Not.Null, "夹具前提：Dodge 的所有者必须已有 Lane，否则占位判据根本不会被求值");
            Assert.That(lane.Contains(move.ActionPlanId), Is.True);
            Assert.That(lane.Count, Is.EqualTo(1), "夹具前提：Hero 的 Lane 上只有这一条 Move");

            // —— 判据对照（只读）：共享容忍判据看不到冲突，旧判据看得到 ——
            // 它同时是"本用例不是空断言"的证据：区间确实与一条 Lane 内计划重叠。
            // 容忍判据就是生产收敛后的唯一实现
            // （DodgeMovementInvalidationSeam.DodgeMayInvalidate，被接受预检与规划器复核共用）。
            Assert.That(
                lane.TryFindOverlapExcept(
                    intervalStart, intervalEnd - intervalStart, null,
                    overlapping => DodgeMovementInvalidationSeam.DodgeMayInvalidate(
                        overlapping, ActionType.Dodge, intervalStart),
                    out _),
                Is.False, "共享容忍判据必须容忍这条后续 Editable 移动族");
            Assert.That(
                DodgeMovementInvalidationSeam.DodgeMayInvalidate(move, ActionType.Dodge, intervalStart),
                Is.True, "判据本身必须对这条 Move 返回 true");
            Assert.That(
                DodgeMovementInvalidationSeam.DodgeMayInvalidate(move, ActionType.Dodge, move.StartTick + 1L),
                Is.False, "判据必须要求重叠者是「后续」的（起点不早于区间起点）");
            Assert.That(lane.TryFindOverlap(intervalStart, intervalEnd - intervalStart, null,
                out ActionPlan legacyConflict), Is.True, "旧判据必须看到同一个重叠");
            Assert.That(legacyConflict.ActionPlanId, Is.EqualTo(move.ActionPlanId));

            // —— 生产接受入口 ——
            string error = rig.Reactions.TryAcceptById(
                opportunityId, Spec(DodgeSpecId), 1L, null, out ActionPlan dodge);

            Assert.That(error, Is.Null,
                "裁定 A：Dodge 的固定反应区间必须容忍它自己在 TriggerTick 会失效掉的后续 Editable 移动族。" +
                " geometry=" + LaneDump(rig, Hero) +
                " move=[" + move.StartTick + "," + move.EndTick + ")" +
                " dodgeInterval=[" + intervalStart + "," + intervalEnd + ")" +
                " error=" + error);

            Assert.That(dodge, Is.Not.Null);
            Assert.That(dodge.ActionType, Is.EqualTo(ActionType.Dodge));
            Assert.That(dodge.IsReaction, Is.True);
            Assert.That(dodge.IsLocked, Is.True, "反应计划接受后直接 Locked");
            Assert.That(dodge.OwnerUnitId, Is.EqualTo(Hero));
            Assert.That(dodge.StartTick, Is.EqualTo(intervalStart));
            Assert.That(dodge.EndTick, Is.EqualTo(intervalEnd));
            Assert.That(dodge.BudgetCostTicks, Is.Zero, "反应计划不消费 TurnBudget");

            // —— 接受**不**失效任何移动：失效只发生在 TriggerTick 的终态接缝 ——
            Assert.That(move.IsEditable, Is.True, "接受 Dodge 之后那条 Move 必须仍然是 Editable");
            Assert.That(move.StartTick, Is.EqualTo(OverlappingMoveStartTick));
            Assert.That(lane.Contains(move.ActionPlanId), Is.True);
            Assert.That(lane.Contains(dodge.ActionPlanId), Is.True);
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(2L),
                "反应接受不是排程编辑事务（只有两条 Move 事务各 +1）");
            Assert.That(rig.Reactions.FindOpportunity(opportunityId).State,
                Is.EqualTo(ReactionOpportunityState.Accepted));
        }

        // =====================================================================
        // 4. BlockAcceptanceStillRejectsSubsequentMoveOverlap
        //
        // 会让它失败的实现缺陷：把容忍判据写成"忽略全部重叠"（例如丢掉
        // optionSpec.Type == ActionType.Dodge 这一项，或直接传 tolerated: _ => true）⇒
        // Block 会被放行，本用例立刻变红。
        // =====================================================================
        [Test]
        public void BlockAcceptanceStillRejectsSubsequentMoveOverlap()
        {
            Rig rig = NewRig();
            ReactionOpportunityId opportunityId = OpenTelegraphedAttack(rig);

            TurnWindow heroWindow = OpenWindow(rig, 1L, Hero, 400);
            ActionPlan move = AddMove(rig, 1L, OverlappingMoveStartTick, 0, 4, expectedWindow: heroWindow.WindowId);

            long blockStart = BlockIntervalStart();
            long blockEnd = DodgeIntervalEnd();
            Assert.That(blockStart, Is.EqualTo(40L));
            Assert.That(blockEnd, Is.EqualTo(110L));
            Assert.That(move.StartTick, Is.GreaterThanOrEqualTo(blockStart));
            Assert.That(move.StartTick, Is.LessThan(blockEnd),
                "夹具前提：同一个几何下那条 Move 也与 Block 固定区间相交");

            ActorLane lane = rig.Schedule.FindLane(Hero);
            Assert.That(lane, Is.Not.Null);
            Assert.That(lane.Count, Is.EqualTo(1));

            int laneCountBefore = lane.Count;
            long revisionBefore = rig.Schedule.ScheduleRevision;

            string error = rig.Reactions.TryAcceptById(
                opportunityId, Spec(BlockSpecId), 1L, null, out ActionPlan block);

            Assert.That(error, Is.EqualTo(ReactionCodes.REACTION_LANE_INTERVAL_OCCUPIED),
                "Block 不换位 ⇒ 绝不容忍任何重叠。 geometry=" + LaneDump(rig, Hero) + " error=" + error);
            Assert.That(block, Is.Null, "被拒绝的接受事务不得创建计划");

            // 零写入：Lane 不变、计划字段不变、修订号不变、机会仍开放且选项仍开放。
            Assert.That(lane.Count, Is.EqualTo(laneCountBefore));
            Assert.That(move.IsEditable, Is.True);
            Assert.That(move.StartTick, Is.EqualTo(OverlappingMoveStartTick));
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBefore),
                "被拒绝的接受事务不得推进修订号");
            Assert.That(rig.Reactions.FindOpportunity(opportunityId).State,
                Is.EqualTo(ReactionOpportunityState.Open), "区间被占用是零写入拒绝：机会必须仍然开放");
        }

        // =====================================================================
        // 5. DodgeAcceptanceStillRejectsLockedPlanOverlap
        //
        // 会让它失败的实现缺陷：容忍判据丢掉 overlapping.IsEditable 一项
        // （例如只按 IsMovementFamily 判断）⇒ 不可失效的 Locked/Running 计划会被容忍，
        // 本用例立刻变红。
        // =====================================================================
        [Test]
        public void DodgeAcceptanceStillRejectsLockedPlanOverlap()
        {
            Rig rig = NewRig();
            ReactionOpportunityId opportunityId = OpenTelegraphedAttack(rig);

            // 重叠者是 Hero 自己的一条已经锁定/启动的计划（非 Editable ⇒ 不可被 Dodge 失效）。
            TurnWindow heroWindow = OpenWindow(rig, 1L, Hero, 400);
            ActionPlan guard = AddGuard(rig, 1L, OverlappingMoveStartTick, heroWindow.WindowId);
            StartAndLock(rig, guard, 1L);

            long intervalStart = DodgeIntervalStart();
            long intervalEnd = DodgeIntervalEnd();
            Assert.That(guard.IsEditable, Is.False, "夹具前提：重叠者必须是非 Editable（Locked/Running）");
            Assert.That(guard.IsRunning, Is.True);
            Assert.That(guard.IsLaneObstacle, Is.True, "Locked/Running 计划是 Lane 的不可变障碍");
            Assert.That(guard.StartTick, Is.EqualTo(OverlappingMoveStartTick));
            Assert.That(guard.StartTick, Is.LessThan(intervalEnd));
            Assert.That(guard.EndTick, Is.GreaterThan(intervalStart),
                "夹具前提：这条非 Editable 计划必须与 Dodge 固定区间相交");

            ActorLane lane = rig.Schedule.FindLane(Hero);
            Assert.That(lane, Is.Not.Null);
            Assert.That(lane.Contains(guard.ActionPlanId), Is.True);
            Assert.That(lane.Count, Is.EqualTo(1));

            long revisionBefore = rig.Schedule.ScheduleRevision;
            int spentBefore = heroWindow.SpentBudgetTicks;

            string error = rig.Reactions.TryAcceptById(
                opportunityId, Spec(DodgeSpecId), 1L, null, out ActionPlan dodge);

            Assert.That(error, Is.EqualTo(ReactionCodes.REACTION_LANE_INTERVAL_OCCUPIED),
                "不可失效者（Locked/Running）的重叠绝不能被容忍。 geometry=" + LaneDump(rig, Hero) +
                " error=" + error);
            Assert.That(dodge, Is.Null);
            Assert.That(guard.IsRunning, Is.True, "被拒绝的接受事务不得改动任何计划");
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBefore));
            Assert.That(heroWindow.SpentBudgetTicks, Is.EqualTo(spentBefore));
            Assert.That(rig.Reactions.FindOpportunity(opportunityId).State,
                Is.EqualTo(ReactionOpportunityState.Open));
        }

        // =====================================================================
        // 6. DodgeInvalidationClosureCoversToleratedPlan（判据一致性的证据）
        //
        // 会让它失败的实现缺陷：容忍判据与 DodgeMovementInvalidationSeam.QueryInvalidatedMoves
        // 的闭包过滤器分叉（例如容忍判据漏掉 IsMovementFamily，或闭包过滤器漏掉 IsEditable）
        // ⇒ "被容忍的重叠"不会出现在失效集合里 ⇒ 本用例变红（那正是"既重叠又不会被清理"的漏洞）。
        // =====================================================================
        [Test]
        public void DodgeInvalidationClosureCoversToleratedPlan()
        {
            Rig rig = NewRig();
            ReactionOpportunityId opportunityId = OpenTelegraphedAttack(rig);

            TurnWindow heroWindow = OpenWindow(rig, 1L, Hero, 400);
            ActionPlan tolerated = AddMove(rig, 1L, OverlappingMoveStartTick, 0, 4,
                expectedWindow: heroWindow.WindowId);
            Assert.That(tolerated.IsEditable, Is.True);

            // 另加一条隔着时间间隙的后续Move；它仍依赖旧起点，必须进入闭包。
            ActionPlan independent = AddMove(rig, 1L, 900L, 4, 0, expectedWindow: heroWindow.WindowId);
            Assert.That(independent.StartTick, Is.GreaterThan(DodgeIntervalEnd()));

            long revisionBefore = rig.Schedule.ScheduleRevision;
            string error = rig.Reactions.TryAcceptById(
                opportunityId, Spec(DodgeSpecId), 1L, null, out ActionPlan dodge);
            Assert.That(error, Is.Null,
                "本用例的前提是「接受上述 Dodge 成功」。" + LaneDump(rig, Hero) + " error=" + error);
            Assert.That(dodge, Is.Not.Null);
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBefore),
                "反应接受不是排程编辑事务（修订号不变）");

            // 接受之后（移动仍在 Editable、尚未被失效）闭包必须已经包含那条被容忍的 Move。
            Assert.That(tolerated.IsEditable, Is.True);
            IReadOnlyList<ActionPlan> closure = rig.Seam.QueryInvalidatedMoves(dodge);
            Assert.That(closure.Count, Is.EqualTo(2),
                "闭包必须包含重叠Move与隔着时间间隙的后续依赖Move。 " + LaneDump(rig, Hero));
            Assert.That(closure[0].ActionPlanId, Is.EqualTo(tolerated.ActionPlanId));
            Assert.That(closure[0].IsEditable, Is.True);
            Assert.That(closure[0].IsMovementFamily, Is.True);
            Assert.That(closure[0].StartTick, Is.GreaterThanOrEqualTo(dodge.StartTick));
            Assert.That(closure[0].StartTick, Is.LessThan(dodge.EndTick),
                "被容忍的重叠一定落在 Dodge 固定区间内 ⇒ 一定属于位置依赖闭包");

            // 只读：查询不得改动任何状态。
            Assert.That(tolerated.IsEditable, Is.True);
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBefore));
        }

        // =====================================================================
        // §生产缺陷登记（本轮测试实测定位、随后由生产修复的两个独立缺口；
        //   本文件未修改任何 Assets/Scripts/** 文件，只保留判定口径与实测事实）
        //
        // ① 【曾阻断用例 1 的 (B) 段：回程链；现已修复】修复前的机制是：
        //    网格 Reservation 索引"一格一持有者"（<c>LogicGrid.TryReserve</c> 的
        //    <c>_reservationOwner[cell]</c> 只有一个键，<c>Reservation</c> 自带的
        //    Start/EndTick 不参与占用判定），而 <c>LogicPathfinder.IsBlocked</c> 又对
        //    "本单位自己的 Reservation"放行（<c>reservation.UnitId == mover</c> ⇒ 不阻塞）——
        //    于是同一单位的排队 Move 链一旦让后一条重新经过前一条**进入过**的格
        //    （(0,4) -> (0,-4) 会重新经过 (0,2)），后一条在整批预留阶段必然以
        //    <c>LOGIC_GRID_RESERVED_BY_OTHER</c> 失败，即使两条计划的时间窗完全不重叠。
        //    实测证据（修复前）：用例 1 的 (B) 段报 <c>LOGIC_GRID_RESERVED_BY_OTHER</c>
        //    （而不是 <c>MOVEMENT_SEGMENT_ORIGIN_MISMATCH</c>）⇒ "起点校验"那一半与
        //    "同格共存预留"那一半是**两个独立缺口**，不能用同一处修改冒充。
        //    生产修复落地后本段通过：同一格现在可持有多条预留，但共存被**刻意收紧**为
        //    「**同一单位** + 时间窗互不重叠」；其他单位的一切预留（哪怕时间窗完全不相交）
        //    仍照旧 <c>LOGIC_GRID_RESERVED_BY_OTHER</c>、不抢占。调度者已裁定**维持**该口径
        //    （不做跨单位一般化），因此本条不得被读成"跨单位也可共存"。
        //
        // ② 【曾阻断用例 3/6；现已修复】修复前 <c>ReactionPlanner.TryPlanCore</c> 用非容忍的
        //    <c>lane.TryFindOverlap(...)</c> 复核同一个固定反应区间，导致"接受预检放行、
        //    规划器复核拒绝"，裁定 A 在生产路径上不可达。现在两处共用唯一判据
        //    <c>DodgeMovementInvalidationSeam.DodgeMayInvalidate</c>，用例 3/6 通过。
        // =====================================================================
    }
}
