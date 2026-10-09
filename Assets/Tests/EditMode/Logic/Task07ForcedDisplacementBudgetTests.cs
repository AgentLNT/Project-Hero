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
    /// 任务 07「必须产出」13 与「Dodge 移动依赖失效的预算参与者」：
    /// <strong>强制位移 / Dodge 失效路径的预算释放语义</strong>。
    ///
    /// 冻结矩阵（本文件逐条钉住的事务边界）：
    /// <list type="bullet">
    /// <item><c>LockedAtTick &gt; 0</c>（已经过启动门禁原子提交、Reserved 已转 Spent）⇒
    /// 任何终态原因（含 <c>MovementOriginInvalidated</c> /
    /// <c>ReservationPreemptedByForcedDisplacement</c>）都<strong>不退款</strong>；</item>
    /// <item>仍为 Editable 且账本里还有该计划预留 ⇒ 只释放<strong>未消费</strong>预留，
    /// 且释放额按计划自己的 <c>SubmittedWindowId</c> 回到<strong>原窗口账本</strong>：
    /// 已关闭窗口只更新历史账本，不重开、不转移、不前移后续计划；</item>
    /// <item>释放由<strong>唯一</strong>统一终态协调器的状态感知参与者完成，
    /// 强制位移路径<strong>不</strong>调用 <c>RollbackBudget</c>；</item>
    /// <item>Dodge 未换位（被拒绝）时，本 Dodge<strong>不</strong>改变任何后续 Move 的预算账本。</item>
    /// </list>
    ///
    /// 全部断言的观察点都是<strong>生产对象</strong>：<see cref="TurnWindow"/> 账本、
    /// <see cref="TurnWindowBudgetAuthority"/> 的账本变化载荷、
    /// <see cref="DodgeMovementInvalidationSeam"/> 的提交结果与
    /// <see cref="DodgeRelocationAuthority.BudgetReleaseSink"/> 的调用记录。
    /// 因此"释放额没有流到别处"这类断言不是对某个内部字段的复述。
    ///
    /// <para>
    /// ⚠ <strong>夹具必须绕开的一条生产接缝（实测结论，请调度者裁定）</strong>：
    /// 任务 05 的 <c>ActionScheduleAuthority.RegisterPlan</c> 是 <c>internal</c>，
    /// 而 <c>ProjectHero.Logic.Tests</c> <strong>没有</strong> <c>InternalsVisibleTo</c>
    /// （只有 <c>ProjectHero.Authoring.Tests</c> 有，见 <c>Assets/Scripts/Logic/AssemblyInfo.cs</c>）。
    /// 同时，生产接受入口 <c>ReactionOpportunitySystem.TryAcceptById</c> 在创建 Dodge 计划之前
    /// 执行 Lane 固定区间重叠检查；而"位置依赖闭包"要求被失效的 Move 满足
    /// <c>Move.StartTick ∈ [Dodge.StartTick, Dodge.EndTick)</c>——该 Move 必须排在来源攻击之后，
    /// 于是它的区间必然与 Dodge 固定区间相交 ⇒ 生产入口在这套几何下<strong>必然</strong>返回
    /// <c>REACTION_LANE_INTERVAL_OCCUPIED</c>。
    /// 因此本文件按 Authoring 夹具（<c>Task05DodgeSeamTests</c> /
    /// <c>Task06CommitTerminalSemanticsTests</c> 直接 <c>RegisterPlan</c>）的同一口径构造夹具：
    /// 只对"计划接入权威"和两个 <c>internal</c> 状态标记使用反射写入，
    /// 被测的预算、终态与预留代码路径<strong>没有</strong>任何放宽；
    /// 并且 <c>AcceptDodge</c> 会断言生产入口的拒绝码恰好是那条已知的 Lane 检查。
    /// </para>
    /// </summary>
    public class Task07ForcedDisplacementBudgetTests
    {
        // ================= 夹具常量 =================

        private const string MoveSpecId = "action.t07.move";
        private const string ShortMoveSpecId = "action.t07.move.short";
        private const string DodgeSpecId = "action.t07.dodge";
        private const string TelegraphSpecId = "action.t07.attack.telegraph";
        private const string GuardSpecId = "action.t07.guard";

        private const long Window1OpenTick = 0L;
        private const long Window2OpenTick = 2L;
        private const long TelegraphTick = 0L;

        /// <summary>攻击前摇（基准值；ActionSpeed = ReferenceActionSpeed ⇒ 解析后逐字相同）。</summary>
        private const int TelegraphAttackBaseWindup = 100;

        private const int TelegraphAttackRecovery = 10;

        /// <summary>Dodge 反应前摇（Dodge 固定区间 = <c>[TriggerTick - 30, TriggerTick + Recovery)</c>）。</summary>
        private const int DodgeReactionWindup = 30;

        private const int DodgeRecovery = 60;

        /// <summary>
        /// Dodge 的固定 <c>TriggerTick</c>（只能由来源攻击的 <c>ImpactTick</c> 推导）。
        ///
        /// 夹具事实：<c>BaseWindup = 100</c>、<c>ActionSpeed = ReferenceActionSpeed</c>
        /// ⇒ <c>ResolvedWindupTicks = 100</c>、<c>ImpactTick = 0 + 100 = 100</c>，
        /// 因此 <c>TriggerTick = 100</c>；<c>AcceptDodge</c> 会断言 Dodge 计划的固定区间
        /// 恰好是 <c>[TriggerTick - ReactionWindup, TriggerTick + Recovery]</c>，
        /// 所以本常量与真实推导必须是同一个值（否则夹具自身失败，而不是被测行为失败）。
        /// </summary>
        private const long TriggerTick = 100L;

        /// <summary>
        /// 依赖 Move 的请求起点。夹具时序（必须逐条成立，否则本文件的闭包用例会失去意义）：
        /// <list type="bullet">
        /// <item>攻击计划固定区间 = <c>[0, 100 + 10) = [0, 110)</c>（它占住 Lane，排在它之后的 Move 最早落在 110）；</item>
        /// <item>Dodge 固定区间 = <c>[100 - 30, 100 + 20) = [70, 120)</c>；</item>
        /// <item>请求起点 <c>110</c> 既不被攻击区间向右挤，也不早于当前 Tick，且
        /// <c>110 ∈ [70, 120)</c> ⇒ 该 Move 一定属于 <c>QueryPositionDependencyClosure</c> 的结果。</item>
        /// </list>
        /// 关键点是 <c>DodgeRecovery (20) &gt; 攻击后摇 (10)</c>：正是它让"Dodge 固定区间"
        /// 能越过攻击计划的终点，从而与依赖 Move 的起点相交——这是本夹具唯一的构造自由度。
        /// </summary>
        private const long DependentMoveStartTick = 110L;

        /// <summary>Move 起点远在任何受影响区间之外 ⇒ 不属于闭包（本文件用它对"没有转移/没有前移"做负控制）。</summary>
        private const long IndependentMoveStartTick = 900L;

        private static readonly GridBoundaryDefinition Wide =
            new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40));

        private static UnitId U(long id) => new UnitId(id);
        private static ActionPlanId P(long id) => new ActionPlanId(id);
        private static ControllerId C(string id) => new ControllerId(id);

        /// <summary>把账号的"对立单位"稳定下来：hero(1) ↔ monster(2)/monster(3) 互为 Hostile。</summary>
        private static readonly UnitId Hero = U(1L);
        private static readonly UnitId EnemyA = U(2L);
        private static readonly UnitId EnemyB = U(3L);

        /// <summary>只在占位用例里出现的一次性单位（不绑定事实；只用于占住目的格）。</summary>
        private static readonly UnitId Occupier = U(99L);

        private static readonly FactionId HeroFaction = new FactionId("faction.hero");
        private static readonly FactionId MonsterFaction = new FactionId("faction.monster");

        // ================= 定义与事实 =================

        private static IReadOnlyList<DirectionalTriangleSet> Directions()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        private static MovementPatternSpec MovementPattern()
            => new MovementPatternSpec(new MovementPatternId("movement_pattern.t07"), Directions());

        /// <summary>只追加本文件需要的四个动作；不读任何真实资产（纯逻辑用例）。</summary>
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

            BattleRules rules = BattleRules.FrozenV1;

            var moveSpec = new ActionSpec(
                new ActionSpecId(MoveSpecId), ActionType.Move,
                new MoveTimingSpec(BaseStepTicks: 5, RecoveryTicks: 5),
                new MovePayloadSpec(200, MovementPattern()),
                AdrenalineCost: 0);

            // 依赖 Move 专用规格：区间长度必须短到能整段放进 Dodge 固定区间
            // （Dodge 固定区间 = [TriggerTick - 30, TriggerTick + 60) = [70, 160)，
            // 而排在攻击计划 [0, 110) 之后的 Move 最早落在 110）。
            var shortMoveSpec = new ActionSpec(
                new ActionSpecId(ShortMoveSpecId), ActionType.Move,
                new MoveTimingSpec(BaseStepTicks: 5, RecoveryTicks: 5),
                new MovePayloadSpec(200, MovementPattern()),
                AdrenalineCost: 0);

            var dodgeSpec = new ActionSpec(
                new ActionSpecId(DodgeSpecId), ActionType.Dodge,
                new DodgeReactionTimingSpec(ReactionWindupTicks: DodgeReactionWindup, RecoveryTicks: DodgeRecovery),
                new DodgePayloadSpec(MaxDistanceSteps: 8, Pattern: MovementPattern()),
                AdrenalineCost: FrozenDesignValues.DodgeAdrenalineCost);

            // 长前摇攻击：TelegraphTick = StartTick = 0、ImpactTick = StartTick + 100 = 100 ⇒
            // 满足最小反应提前量，且 Dodge 截止 Tick = 100 - 30 = 70 仍然公开。
            var telegraphSpec = new ActionSpec(
                new ActionSpecId(TelegraphSpecId), ActionType.Attack,
                new AttackTimingSpec(BaseWindupTicks: TelegraphAttackBaseWindup, RecoveryTicks: TelegraphAttackRecovery),
                new AttackPayloadSpec(
                    new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f, DamageTagMask.Blockable) },
                    ImpactProfiles.Blunt, 1f, TargetPolicy.PrimaryTargetOnly,
                    TargetRelationMask.Hostile, 0, null,
                    AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable),
                AdrenalineCost: 0);

            var guardSpec = new ActionSpec(
                new ActionSpecId(GuardSpecId), ActionType.Guard,
                new GuardTimingSpec(WindupTicks: 10, ActiveTicks: 20, RecoveryTicks: 30),
                new GuardPayloadSpec(
                    new Dictionary<DamageChannelId, int>(), 512,
                    DefenseTagMask.Blockable | DefenseTagMask.Guardable),
                AdrenalineCost: 0);

            return new BattleDefinition(
                "battle-definition.task07.forced-displacement",
                3,
                rules,
                ConcurrentActionDefinition.FrozenV1,
                new ReactionRules(CommandIngressLeadTicks: 2, MinimumReactionLeadTicks: 1),
                AdrenalineRules.FrozenV1,
                factionModel,
                new List<DamageChannelDefinition>(),
                new List<ImpactProfileDefinition>(),
                new List<UnitDefinition>
                {
                    new UnitDefinition(new UnitDefinitionId("unit.t07.hero"), 10f, 10f, 10f, 10f,
                        new Dictionary<DamageChannelId, int>(), 200f, default, default),
                    new UnitDefinition(new UnitDefinitionId("unit.t07.enemy"), 10f, 10f, 10f, 10f,
                        new Dictionary<DamageChannelId, int>(), 200f, default, default)
                },
                new List<ActionSpec> { moveSpec, shortMoveSpec, dodgeSpec, telegraphSpec, guardSpec },
                new List<AttackPatternSpec>(),
                new List<VolumeSpec>(),
                new List<MovementPatternSpec> { MovementPattern() },
                new List<ActionSetDefinition>(),
                new List<StatusEffectSpec>(),
                new List<EncounterDefinition>(),
                null,
                "test-definition-hash.t07.forced-displacement");
        }

        /// <summary>只读单位事实来源（存活/阵营/速度）；本夹具的速度恒为基准值。</summary>
        private sealed class Facts : IActionPlanFactsSource
        {
            private readonly Dictionary<long, ActionPlanOwnerFacts> _map =
                new Dictionary<long, ActionPlanOwnerFacts>();

            public void Add(UnitId unitId, FactionId factionId, bool isAlive = true)
                => _map[unitId.Value] = new ActionPlanOwnerFacts(
                    unitId, factionId,
                    // 关键夹具事实：ActionSpeed 必须等于 ReferenceActionSpeed，
                    // 否则 ResolveWindupTicks 会按 前摇 × 基准 / 实际 缩放，
                    // 攻击的 ImpactTick（= Dodge 的 TriggerTick）就不再是 350。
                    BattleRules.FrozenV1.ReferenceActionSpeed,
                    BattleRules.FrozenV1.ReferenceMoveSpeed,
                    default, isAlive);

            public bool TryGetOwnerFacts(UnitId unitId, out ActionPlanOwnerFacts facts)
                => _map.TryGetValue(unitId.Value, out facts);
        }

        /// <summary>窗口管理器需要的只读世界事实。</summary>
        private sealed class WorldView : ITurnWindowWorldView
        {
            public readonly HashSet<long> DeadUnits = new HashSet<long>();
            public bool Ended;

            public bool IsUnitAliveForWindow(UnitId unitId) => !DeadUnits.Contains(unitId.Value);

            public bool IsBattleEnded => Ended;
        }

        private static IFactionRelationResolver BuildResolver()
        {
            BattleDefinition definition = BuildDefinition();
            var unitFactions = new Dictionary<UnitId, FactionId>
            {
                [Hero] = HeroFaction,
                [EnemyA] = MonsterFaction,
                [EnemyB] = MonsterFaction
            };
            return new FactionRelationResolver(definition.FactionModel, unitFactions);
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
            public ScheduleEditor Editor;
            public ScheduleEvaluator Evaluator;
            public LogicGridMovementAuthority Movement;
            public DodgeRelocationAuthority Dodge;
            public ActionPlanTerminalCoordinator Coordinator;
            public BudgetAndAdrenalineCleanupParticipant BudgetParticipant;
            public ReactionOpportunitySystem Reactions;
            public DodgeMovementInvalidationSeam Seam;
            public TurnWindowManager Windows;
            public WorldView World;
            public TurnWindowBudgetAuthority Budget;

            /// <summary>生产预算端口发射的账本变化载荷（释放额是否流到别处由它判定）。</summary>
            public readonly List<TurnBudgetChange> Ledger = new List<TurnBudgetChange>();

            /// <summary>任务 06 的预算释放接缝调用记录（强制位移路径的观察点）。</summary>
            public readonly List<IReadOnlyList<ActionPlanId>> ReleaseSinkCalls =
                new List<IReadOnlyList<ActionPlanId>>();
        }

        private static Rig NewRig()
        {
            BattleDefinition definition = BuildDefinition();
            var facts = new Facts();
            facts.Add(Hero, HeroFaction);
            facts.Add(EnemyA, MonsterFaction);
            facts.Add(EnemyB, MonsterFaction);
            var schedule = new ActionScheduleAuthority();
            var ids = new LogicIdGenerator();
            var factory = new ActionPlanFactory(definition, BuildResolver(), facts, ids);

            var grid = new LogicGrid(Wide);
            var movement = new LogicGridMovementAuthority(
                grid, schedule, definition.Rules.PathCostRules, definition.Rules.PathSearchRules);
            // 排程事务与 Dodge 接缝必须共用同一个路径计算器实例（否则路径投影会分叉）。
            var pathCalculator = new LogicGridMovementPathCalculator(
                grid, schedule, definition.Rules.PathCostRules, definition.Rules.PathSearchRules);
            var editor = new ScheduleEditor(schedule, factory, ids, pathCalculator);

            var world = new WorldView();
            var windows = new TurnWindowManager(world);
            var budget = new TurnWindowBudgetAuthority(windows, null);

            var budgetParticipant = new BudgetAndAdrenalineCleanupParticipant();
            var coordinator = new ActionPlanTerminalCoordinator(schedule, new IActionPlanCleanupParticipant[]
            {
                StopSchedulingCleanupParticipant.Instance,
                movement,
                budgetParticipant
            });

            var dodge = new DodgeRelocationAuthority(grid, movement, schedule, coordinator,
                specId => DodgeDestinationRules.FromPayload(
                    definition.FindAction(specId)?.Payload as DodgePayloadSpec));
            movement.DodgeDestinationReservations = dodge;

            var reactions = new ReactionOpportunitySystem(
                schedule, factory, definition, BuildResolver(), ids, null, coordinator);
            reactions.DestinationReservationPort = dodge;

            var evaluator = new ScheduleEvaluator(schedule, pathCalculator);
            var seam = new DodgeMovementInvalidationSeam(schedule, evaluator, coordinator, reactions, dodge);

            var rig = new Rig
            {
                Definition = definition,
                Facts = facts,
                Grid = grid,
                Schedule = schedule,
                Ids = ids,
                Factory = factory,
                Editor = editor,
                Evaluator = evaluator,
                Movement = movement,
                Dodge = dodge,
                Coordinator = coordinator,
                BudgetParticipant = budgetParticipant,
                Reactions = reactions,
                Seam = seam,
                Windows = windows,
                World = world,
                Budget = budget
            };

            budget.ChangedSink = rig.Ledger.Add;
            budgetParticipant.Bind(budget, windows, null);
            editor.BudgetContextFactory = (source, targetTick, expectedWindowId, issuer) =>
                new ScheduleBudgetContext(
                    source, budget, expectedWindowId,
                    windows.CurrentWindow != null ? windows.CurrentWindow.WindowId : (WindowId?)null,
                    issuer);
            dodge.BudgetReleaseSink = ids2 => rig.ReleaseSinkCalls.Add(ids2);

            // 排程事务必须能解析"计划自己的预算来源窗口"：它是释放额的唯一归属依据。
            windows.RegisterControllerBinding(C("controller.player"), Hero);

            Assert.That(grid.RegisterUnitWithPointFootprint(Hero, new GridPoint(0, 0), GridDirection.East),
                Is.Null, "hero 必须落在 (0,0)（Dodge 的 From）");
            Assert.That(grid.RegisterUnitWithPointFootprint(EnemyA, new GridPoint(10, 10), GridDirection.East),
                Is.Null);
            return rig;
        }

        // ================= 夹具动作 =================

        private static TurnWindow OpenWindow(Rig rig, long tick, UnitId owner, int budget)
        {
            rig.Windows.ScheduleWindow(tick, owner, budget);
            TurnWindow window = rig.Windows.TryOpenDueWindow(tick);
            Assert.That(window, Is.Not.Null, "夹具前提：tick " + tick + " 应当打开窗口");
            Assert.That(window.OwnerUnitId, Is.EqualTo(owner));
            return window;
        }

        private static void CloseWindow(Rig rig, TurnWindow window, long tick)
        {
            window.RequestClose(TurnWindowCloseReason.OwnerRequested);
            Assert.That(rig.Windows.FinalizeRequestedClose(tick), Is.SameAs(window));
        }

        /// <summary>经真实排程事务新增普通 Move（预算由任务 07 的账本端口原子预留）。</summary>
        private static ActionPlan AddMove(Rig rig, long temporaryKey, int toX, int toY, long startTick)
            => AddPlan(rig, temporaryKey, MoveSpecId, toX, toY, startTick);

        /// <summary>短区间 Move（用于"必须落在 Dodge 固定区间内"的依赖计划）。</summary>
        private static ActionPlan AddShortMove(Rig rig, long temporaryKey, int toX, int toY, long startTick)
            => AddPlan(rig, temporaryKey, ShortMoveSpecId, toX, toY, startTick);

        private static ActionPlan AddPlan(Rig rig, long temporaryKey, string specId, int toX, int toY, long startTick)
        {
            ScheduleEditTransactionResult result = rig.Editor.Apply(
                new ScheduleEditOperation[]
                {
                    new AddOrdinaryPlanOperation(
                        temporaryKey, Hero, new ActionSpecId(specId), startTick,
                        AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                        Facing: GridDirection.East, Destination: new GridPoint(toX, toY))
                },
                currentTick: 0L,
                batchBaseScheduleRevision: rig.Schedule.ScheduleRevision,
                expectedScheduleRevision: rig.Schedule.ScheduleRevision,
                // 00 号规则 18：显式新增/增费必须声明窗口；缺失会被预算权威整批拒绝。
                expectedWindowId: rig.Windows.CurrentWindow?.WindowId);

            Assert.That(result.RejectionCode, Is.Null,
                "Move 计划必须创建成功: code=" + result.RejectionCode +
                " startTick=" + startTick +
                " cost=" + (result.Committed && result.AddedPlanIds.Count == 1
                    ? ProbeCost(rig, result.AddedPlanIds[0]) : -1) +
                " windowAvailable=" + (rig.Windows.CurrentWindow == null
                    ? -1 : rig.Windows.CurrentWindow.AvailableBudgetTicks));
            Assert.That(result.Committed, Is.True);
            Assert.That(result.AddedPlanIds.Count, Is.EqualTo(1));
            ActionPlan plan = rig.Schedule.Registry.Find(result.AddedPlanIds[0]);
            Assert.That(plan, Is.Not.Null, "新增计划必须进入权威注册表");
            Assert.That(plan.SubmittedWindowId.HasValue, Is.True,
                "预算来源窗口必须被记录：释放额只能回到它");
            return plan;
        }

        /// <summary>失败诊断用：读取某个已创建计划的整数预算成本（不写任何状态）。</summary>
        private static int ProbeCost(Rig rig, ActionPlanId planId)
        {
            ActionPlan plan = rig.Schedule.Registry.Find(planId);
            return plan == null ? -1 : plan.BudgetCostTicks;
        }

        /// <summary>失败诊断用：闭包查询看到的 Lane 投影（与 <c>QueryPositionDependencyClosure</c> 同源）。</summary>
        private static string LaneDump(Rig rig, UnitId unitId, ActionPlan dodge, ActionPlan dependent)
        {
            var parts = new List<string>();
            ActorLane lane = rig.Schedule.FindLane(unitId);
            if (lane == null)
            {
                parts.Add("lane=null");
            }
            else
            {
                IReadOnlyList<ActionPlan> plans = lane.Plans;
                for (int i = 0; i < plans.Count; i++)
                {
                    parts.Add("#" + plans[i].ActionPlanId.Value + ":" + plans[i].ActionType +
                              "[" + plans[i].StartTick + "," + plans[i].EndTick + ")" +
                              " editable=" + plans[i].IsEditable +
                              " moveFam=" + plans[i].IsMovementFamily);
                }
            }
            return "lane=" + string.Join(" | ", parts) +
                   " dodge#" + dodge.ActionPlanId.Value + "[" + dodge.StartTick + "," + dodge.EndTick + ")" +
                   " dodgeInLane=" + (lane != null && lane.Contains(dodge.ActionPlanId)) +
                   " dependent#" + dependent.ActionPlanId.Value + "[" + dependent.StartTick + "," +
                   dependent.EndTick + ")";
        }

        /// <summary>创建一个直接 Locked 的 Dodge 反应计划并为其冻结目的格（生产 API，无内部字段写入）。</summary>
        private static ActionPlan AcceptDodge(Rig rig, int destinationX, int destinationY, out ReactionOpportunityId opportunityId)
        {
            // ① 一条长前摇攻击：TelegraphTick = StartTick = 0、ImpactTick = 0 + 前摇。
            // 攻击者刻意是**另一个单位**（EnemyB），主目标是 Hero：
            // 这样"被 Dodge 失效的后续 Move"（Hero 的）与 Dodge 计划处在**同一个 Lane**，
            // 位置依赖闭包才可能把它们连起来（契约定的是同一个 owner 的投影区间）。
            ScheduleEditTransactionResult attackTx = rig.Editor.Apply(
                new ScheduleEditOperation[]
                {
                    new AddOrdinaryPlanOperation(
                        901L, EnemyB, new ActionSpecId(TelegraphSpecId), 0L,
                        AnchorAfterPlanId: default, PrimaryTargetUnitId: Hero,
                        Facing: GridDirection.East, Destination: null)
                },
                currentTick: 0L,
                batchBaseScheduleRevision: rig.Schedule.ScheduleRevision,
                expectedScheduleRevision: rig.Schedule.ScheduleRevision,
                // 00 号规则 18：显式新增/增费必须声明窗口；缺失会被预算权威整批拒绝。
                expectedWindowId: rig.Windows.CurrentWindow?.WindowId);
            Assert.That(attackTx.RejectionCode, Is.Null,
                "Telegraph 攻击计划必须创建成功（窗口预算必须容纳它的整数 Tick 成本）: " + attackTx.RejectionCode);
            ActionPlan telegraph = rig.Schedule.Registry.Find(attackTx.AddedPlanIds[0]);
            Assert.That(telegraph, Is.Not.Null);

            // ② 公开机会（门禁的前置条件：来源攻击必须已经锁定/启动）。
            MarkLockedAndRunning(telegraph, TelegraphTick);

            var opened = new List<ReactionOpportunityRuntime>();
            string openError = rig.Reactions.TryOpenForTelegraph(telegraph, TelegraphTick, opened);
            Assert.That(openError, Is.Null, "机会必须被公开：" + openError);
            Assert.That(opened.Count, Is.EqualTo(1));
            opportunityId = opened[0].Id;
            Assert.That(telegraph.ImpactTick, Is.EqualTo(TriggerTick),
                "夹具前提：TriggerTick 必须等于来源攻击的 ImpactTick（不允许命令声明它）");
            Assert.That(opened[0].TriggerTick, Is.EqualTo(telegraph.ImpactTick));
            Assert.That(telegraph.EndTick, Is.EqualTo(TriggerTick + TelegraphAttackRecovery),
                "夹具前提：攻击固定区间必须恰好是 [0, ImpactTick + 攻击后摇)");

            // ③ 接受 Dodge：计划直接 Locked，目的格按 ReactionOpportunityId 冻结（任务 06 接缝）。
            //
            // ✅ 调度者裁定 A 已落地（本轮修复）：生产命令入口 `TryAcceptById` 的 Lane 固定区间检查
            // 现在**容忍**"它自己在 TriggerTick 会失效掉的后续 Editable 移动族"
            // （判据 = `Dodge && IsEditable && IsMovementFamily && StartTick >= intervalStart`，
            // 与 `DodgeMovementInvalidationSeam.QueryInvalidatedMoves` 的闭包过滤器逐字一致），
            // 因此本夹具的几何（来源攻击之后的 Move 与 Dodge 区间重叠）现在**能经生产入口接受**，
            // `commandPathError == null`，下面的回退分支不再触发。
            //
            // 回退分支仍然保留：它是"若入口出于任何别的原因拒绝，也不让本用例的预算断言失真"的
            // 防御性路径（按 Authoring 夹具既有口径直接 RegisterPlan）。被测的预算/终态/预留代码路径
            // 在两条分支上都没有任何放宽。
            string commandPathError = rig.Reactions.TryAcceptById(
                opportunityId, new ActionSpecId(DodgeSpecId), 1L,
                new GridPoint(destinationX, destinationY), out ActionPlan dodge);

            if (commandPathError != null)
            {
                Assert.That(commandPathError, Is.EqualTo(ReactionCodes.REACTION_LANE_INTERVAL_OCCUPIED),
                    "生产入口只应被 Lane 固定区间占用挡下：" + commandPathError);

                ReactionOpportunityRuntime runtime = rig.Reactions.FindOpportunity(opportunityId);
                ReactionOptionRuntime dodgeOption = null;
                for (int i = 0; i < runtime.Options.Count; i++)
                {
                    if (runtime.Options[i].ReactionActionSpecId.Value == DodgeSpecId)
                    {
                        dodgeOption = runtime.Options[i];
                        break;
                    }
                }
                Assert.That(dodgeOption, Is.Not.Null, "机会必须公开 Dodge 选项");

                // 生产工厂：构造直接 Locked 的 Dodge 反应计划（Lane 预检属于规划器，
                // 本夹具按 Authoring 夹具的同一口径只取"计划构造"这一段）。
                ActionPlanCreationResult created = rig.Factory.TryCreateReaction(
                    new ReactionPlanRequest(
                        runtime.DefenderUnitId, new ActionSpecId(DodgeSpecId), telegraph.Facing,
                        new GridPoint(destinationX, destinationY),
                        opportunityId, telegraph.ActionPlanId,
                        runtime.TriggerTick, dodgeOption.ResponseDeadlineTick),
                    1L);
                Assert.That(created.Succeeded, Is.True, "Dodge 计划必须可被工厂构造：" + created.RejectionCode);
                dodge = created.Plan;
                Assert.That(rig.Schedule.Registry.Contains(dodge.ActionPlanId), Is.False,
                    "被拒绝的接受事务不得注册计划（零局部写入）");

                RegisterPlanInAuthority(rig, dodge);
                string reserved = rig.Dodge.ReserveDestination(
                    opportunityId, Hero, new ActionSpecId(DodgeSpecId),
                    new GridPoint(destinationX, destinationY), TriggerTick, 1L, 1L);
                Assert.That(reserved, Is.Null, "Dodge 目的格必须被冻结：" + reserved);
                rig.Dodge.BindPlan(opportunityId, dodge.ActionPlanId);
                MarkOpportunityAccepted(runtime, dodge);
            }

            // 生产入口的结果必须落在两种之一（不是"断言它一定成功"）：
            // 裁定 A 落地后正常路径是**成功**；若仍被拒绝，只允许是 Lane 固定区间占用。
            Assert.That(
                commandPathError == null || commandPathError == ReactionCodes.REACTION_LANE_INTERVAL_OCCUPIED,
                Is.True,
                "生产接受入口的失败原因只允许是 Lane 固定区间占用（或成功）：" + commandPathError);
            Assert.That(dodge, Is.Not.Null);
            Assert.That(dodge.ActionType, Is.EqualTo(ActionType.Dodge));
            Assert.That(dodge.IsReaction, Is.True);
            Assert.That(dodge.IsLocked, Is.True, "反应计划接受后直接 Locked");
            Assert.That(dodge.OwnerUnitId, Is.EqualTo(Hero),
                "夹具前提：Dodge 的所有者必须与依赖 Move 相同（闭包按 owner 的 Lane 求值）");
            Assert.That(dodge.SubmittedWindowId.HasValue, Is.False,
                "反应计划不消费 TurnBudget（SubmittedWindowId 恒为空）");
            Assert.That(dodge.BudgetCostTicks, Is.Zero);
            Assert.That(dodge.StartTick, Is.EqualTo(TriggerTick - DodgeReactionWindup),
                "夹具前提：Dodge 固定区间左端 = TriggerTick - 反应前摇");
            Assert.That(dodge.EndTick, Is.EqualTo(TriggerTick + DodgeRecovery),
                "夹具前提：Dodge 固定区间右端 = TriggerTick + 后摇");
            Assert.That(rig.Reactions.FindOpportunity(opportunityId).State,
                Is.EqualTo(ReactionOpportunityState.Accepted));
            Assert.That(rig.Dodge.TryGetReservation(opportunityId, out DodgeDestinationReservation frozen),
                Is.True, "夹具前提：目的格必须已按机会 id 冻结");
            Assert.That(frozen.ReactionPlanId, Is.EqualTo(dodge.ActionPlanId),
                "夹具前提：目的格预留必须绑定到该计划");
            ActionPlan boundSource = rig.Schedule.Registry.Find(dodge.SourceThreatPlanId.Value);
            Assert.That(boundSource, Is.Not.Null,
                "夹具前提：Dodge 的来源威胁计划必须仍在权威注册表里（sourceId=" +
                dodge.SourceThreatPlanId.Value.Value + " telegraphId=" + telegraph.ActionPlanId.Value +
                " sameObject=" + ReferenceEquals(boundSource, telegraph) +
                " telegraphTerminal=" + telegraph.IsTerminal +
                " telegraphState=" + telegraph.State + "）");
            Assert.That(rig.Reactions.ConfirmTrigger(opportunityId, 1L), Is.Null,
                "夹具前提：触发确认必须在换位之前就是可行的（否则 TryCommit 必然抛不变量错误）" +
                " bound=" + rig.Reactions.FindOpportunity(opportunityId).BoundActionPlanId.Value +
                " boundFound=" + (rig.Schedule.Registry.Find(
                    rig.Reactions.FindOpportunity(opportunityId).BoundActionPlanId) != null));
            MarkOpportunityAccepted(rig.Reactions.FindOpportunity(opportunityId), dodge);
            return dodge;
        }

        private static readonly MethodInfo AuthorityRegisterPlan =
            typeof(ActionScheduleAuthority).GetMethod(
                "RegisterPlan", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// 把一条计划接进权威注册表与它自己的 Lane（夹具写入）。
        ///
        /// 为什么需要：任务 05 的 <c>ActionScheduleAuthority.RegisterPlan</c> 是 <c>internal</c>，
        /// 而 <c>ProjectHero.Logic.Tests</c> 没有 <c>InternalsVisibleTo</c>；
        /// 生产路径（ScheduleEditor 事务）在"依赖 Move 的起点落在 Dodge 固定区间内"的几何下
        /// <strong>必然</strong>以 <c>REACTION_LANE_INTERVAL_OCCUPIED</c> 拒绝该 Dodge
        /// （见 <c>AcceptDodge</c> 的说明），因此本夹具只能按 Authoring 夹具的同一口径接入。
        /// 它<strong>不</strong>触碰预算账本、机会状态或空间预留。
        /// </summary>
        private static void RegisterPlanInAuthority(Rig rig, ActionPlan plan)
        {
            Assert.That(AuthorityRegisterPlan, Is.Not.Null, "ActionScheduleAuthority.RegisterPlan 必须存在");
            AuthorityRegisterPlan.Invoke(rig.Schedule, new object[] { plan });
            Assert.That(rig.Schedule.Registry.Contains(plan.ActionPlanId), Is.True,
                "夹具前提：计划必须进入权威注册表");
        }

        private static readonly PropertyInfo OpportunityStateSetter =
            typeof(ReactionOpportunityRuntime).GetProperty(nameof(ReactionOpportunityRuntime.State));

        private static readonly PropertyInfo OpportunityBoundPlanSetter =
            typeof(ReactionOpportunityRuntime).GetProperty(nameof(ReactionOpportunityRuntime.BoundActionPlanId));

        private static readonly PropertyInfo OpportunityAcceptedSpecSetter =
            typeof(ReactionOpportunityRuntime).GetProperty(nameof(ReactionOpportunityRuntime.AcceptedActionSpecId));

        /// <summary>
        /// 把机会推进到 <c>Accepted</c> 并绑定被接受的反应计划
        /// （生产状态机允许 <c>Open -&gt; Accepted</c> 这一条边）。
        ///
        /// 为什么需要夹具写入：<c>ReactionOpportunitySystem.TryAcceptById</c> 会在创建计划之前
        /// 做 Lane 固定区间重叠检查，而本夹具刻意构造的"Dodge 区间与依赖 Move 区间相交"
        /// 必然被该检查拒绝（见 <c>AcceptDodge</c> 的说明）。机会状态与绑定字段的写入面是
        /// <c>internal</c>，而 <c>ProjectHero.Logic.Tests</c> 没有 <c>InternalsVisibleTo</c>，
        /// 因此这里只能以反射写入这两个<strong>状态标记</strong>；
        /// 它们不触碰任何预算、计划或空间预留。
        /// </summary>
        private static void MarkOpportunityAccepted(ReactionOpportunityRuntime runtime, ActionPlan dodge)
        {
            Assert.That(OpportunityStateSetter, Is.Not.Null, "ReactionOpportunityRuntime.State 必须存在");
            Assert.That(OpportunityBoundPlanSetter, Is.Not.Null, "BoundActionPlanId 必须存在");
            OpportunityStateSetter.SetValue(runtime, ReactionOpportunityState.Accepted);
            OpportunityBoundPlanSetter.SetValue(runtime, dodge.ActionPlanId);
            if (OpportunityAcceptedSpecSetter != null)
                OpportunityAcceptedSpecSetter.SetValue(runtime, dodge.ActionSpecId.Value);
            Assert.That(runtime.State, Is.EqualTo(ReactionOpportunityState.Accepted));
            Assert.That(runtime.BoundActionPlanId, Is.EqualTo(dodge.ActionPlanId));
        }

        /// <summary>
        /// 把一条 Editable 普通计划经<strong>生产</strong>启动提交端口
        /// （<see cref="TurnWindowBudgetAuthority.Commit"/>，即 <c>IActionPlanStartCommitPort</c> 的真实实现）
        /// 完成 <c>Reserved -&gt; Spent</c>，从而让该计划的资源状态成为
        /// "已经锁定并消费"的形态。它<strong>不</strong>触碰任何账本以外的推断。
        /// </summary>
        private static void CommitStartLock(Rig rig, ActionPlan plan, long tick)
        {
            Assert.That(plan.IsEditable, Is.True, "夹具前提：只有 Editable 计划需要启动提交");
            int cost = plan.BudgetCostTicks;
            Assert.That(cost, Is.GreaterThan(0), "夹具前提：普通计划成本必须为正整数 Tick");
            Assert.That(plan.SubmittedWindowId.HasValue, Is.True);

            string error = rig.Budget.Commit(plan, tick, rolledBack => { });
            Assert.That(error, Is.Null, "生产启动提交端口必须成功：" + error);
            Assert.That(rig.Windows.FindWindow(plan.SubmittedWindowId.Value).SpentBudgetTicks,
                Is.GreaterThanOrEqualTo(cost), "启动提交把 Reserved 原子转为 Spent");
            Assert.That(plan.ReservedTurnBudgetTicks, Is.Zero, "启动提交同时清零计划自身的未消费预留投影");
        }

        // ————————————————————————————————————————————————————————————
        // 锁定标记夹具（唯一的反射写入点，见文件末尾说明）
        // ————————————————————————————————————————————————————————————

        private static readonly PropertyInfo StateSetter =
            typeof(ActionPlan).GetProperty(nameof(ActionPlan.State));

        private static readonly PropertyInfo LockedAtTickSetter =
            typeof(ActionPlan).GetProperty(nameof(ActionPlan.LockedAtTick));

        /// <summary>
        /// 写入"启动门禁已经成功"的两个生命周期标记：<c>State = Running</c> 与
        /// <c>LockedAtTick</c>。
        ///
        /// 为什么只能这样构造：这两个标记的写入面是 <c>internal</c>
        /// （<c>ActionPlanTerminalCoordinator</c> 级别的契约），而
        /// <c>ProjectHero.Logic.Tests</c> <strong>没有</strong>
        /// <c>InternalsVisibleTo</c>（见 <c>Assets/Scripts/Logic/AssemblyInfo.cs</c>：
        /// 该授权只给 <c>ProjectHero.Authoring.Tests</c>）。
        /// 生产路径里写这两个字段的唯一位置是 <c>BattleSimulation.CommitStart</c>，
        /// 而它需要完整的 Encounter / Controller / 资产初始化链，超出纯逻辑用例的边界。
        ///
        /// 因此本方法<strong>只做标记写入</strong>，不做任何账本推断：资源侧的
        /// <c>Reserved -&gt; Spent</c> 由<strong>生产端口</strong>
        /// <see cref="CommitStartLock"/> 完成，本方法不碰任何账本。
        /// 它写入的正是被测参与者读取的那两个事实，因此"参与者是否按状态结算"仍然是真判定。
        /// </summary>
        private static void MarkLockedAndRunning(ActionPlan plan, long lockedAtTick)
        {
            Assert.That(StateSetter, Is.Not.Null, "ActionPlan.State 必须存在（夹具不变量）");
            Assert.That(LockedAtTickSetter, Is.Not.Null, "ActionPlan.LockedAtTick 必须存在（夹具不变量）");
            StateSetter.SetValue(plan, ActionPlanState.Running);
            LockedAtTickSetter.SetValue(plan, lockedAtTick);

            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Running), "夹具前提：标记写入必须生效");
            Assert.That(plan.LockedAtTick, Is.EqualTo(lockedAtTick), "夹具前提：锁定 Tick 必须生效");
            Assert.That(plan.IsEditable, Is.False);
        }

        /// <summary>账本恒等式：每个窗口的 <c>Reserved + Spent + Available == Total</c>。</summary>
        private static void AssertLedgerIdentity(IReadOnlyList<TurnWindow> windows)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                TurnWindow window = windows[i];
                Assert.That(window.ReservedBudgetTicks + window.SpentBudgetTicks + window.AvailableBudgetTicks,
                    Is.EqualTo(window.TotalBudgetTicks),
                    "窗口 " + window.WindowId.Value + " 的账本恒等式被破坏");
                Assert.That(window.ReservedBudgetTicks, Is.GreaterThanOrEqualTo(0));
                Assert.That(window.SpentBudgetTicks, Is.GreaterThanOrEqualTo(0));
                Assert.That(window.AvailableBudgetTicks, Is.GreaterThanOrEqualTo(0));
            }
        }

        /// <summary>失败诊断用：账本变化明细。</summary>
        private static string LedgerDump(Rig rig)
        {
            var dump = new List<string>();
            for (int i = 0; i < rig.Ledger.Count; i++)
            {
                TurnBudgetChange entry = rig.Ledger[i];
                dump.Add(entry.ChangeKind + "/" + entry.Source + "/win=" + entry.WindowId.Value +
                         "/plan=" + (entry.ActionPlanId.HasValue ? entry.ActionPlanId.Value.Value : 0L) +
                         "/avail=" + entry.AvailableBefore + "->" + entry.AvailableAfter +
                         "/res=" + entry.ReservedBefore + "->" + entry.ReservedAfter +
                         "/spent=" + entry.SpentBefore + "->" + entry.SpentAfter);
            }
            return string.Join(" ;; ", dump);
        }

        private static IReadOnlyList<TurnWindow> AllWindows(Rig rig)
        {
            var all = new List<TurnWindow>();
            if (rig.Windows.CurrentWindow != null) all.Add(rig.Windows.CurrentWindow);
            all.AddRange(rig.Windows.ClosedWindows);
            return all;
        }

        // =====================================================================
        // 1. DodgeMoveInvalidationReleasesReservedToEachOriginalWindowExactlyOnce
        // =====================================================================
        [Test]
        public void DodgeMoveInvalidationReleasesReservedToEachOriginalWindowExactlyOnce()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, Window1OpenTick, Hero, budget: 1200);

            ActionPlan dependent = AddShortMove(rig, 1L, 0, 4, DependentMoveStartTick);
            ActionPlan independent = AddMove(rig, 2L, 4, 0, IndependentMoveStartTick);

            int dependentReserved = window.ReservedFor(dependent.ActionPlanId);
            int independentReserved = window.ReservedFor(independent.ActionPlanId);
            Assert.That(dependentReserved, Is.GreaterThan(0), "夹具前提：Editable 计划必须持有未消费预留");
            Assert.That(independentReserved, Is.GreaterThan(0));

            ActionPlan dodge = AcceptDodge(rig, 4, 4, out ReactionOpportunityId opportunityId);

            // 基线必须在 Dodge 接受事务（它会为来源攻击计划占用同一窗口预算）之后取：
            // 否则下面会把"接受事务的正常预留"误读成"释放额异常"。
            int attackReserved = window.ReservedFor(dodge.SourceThreatPlanId.Value);
            Assert.That(attackReserved, Is.GreaterThan(0),
                "夹具事实：来源攻击也是该窗口里的 Editable 普通计划，占用自己的预留");
            int reservedBefore = window.ReservedBudgetTicks;
            int availableBefore = window.AvailableBudgetTicks;
            int spentBefore = window.SpentBudgetTicks;
            Assert.That(reservedBefore,
                Is.EqualTo(dependentReserved + independentReserved + attackReserved));
            Assert.That(spentBefore, Is.Zero, "Editable 阶段不得消费");

            // 闭包前提：夹具的时序事实必须成立（否则本用例会退化成"空断言"）。
            ActionPlan sourceAttack = rig.Schedule.Registry.Find(dodge.SourceThreatPlanId.Value);
            Assert.That(sourceAttack, Is.Not.Null);
            Assert.That(dependent.StartTick, Is.GreaterThanOrEqualTo(sourceAttack.EndTick),
                "夹具事实：依赖 Move 排在来源攻击之后（攻击区间 [0," + sourceAttack.EndTick + ")，" +
                "Move.StartTick=" + dependent.StartTick + "）");
            Assert.That(dependent.StartTick, Is.GreaterThanOrEqualTo(dodge.StartTick),
                "夹具事实：Move.StartTick 必须落在 Dodge 固定区间内");
            Assert.That(dependent.StartTick, Is.LessThan(dodge.EndTick),
                "夹具事实：Move.StartTick 必须落在 Dodge 固定区间内（半开区间右端不包含）");

            // 只读闭包包含重叠与远期的依赖Move，来源攻击不进入。
            IReadOnlyList<ActionPlan> closure = rig.Seam.QueryInvalidatedMoves(dodge);
            Assert.That(closure.Count, Is.EqualTo(2), "DIAG " + LaneDump(rig, Hero, dodge, dependent));
            Assert.That(closure[0].ActionPlanId, Is.EqualTo(dependent.ActionPlanId));

            long revisionBefore = rig.Schedule.ScheduleRevision;
            rig.Ledger.Clear();

            DodgeRelocationOutcome outcome = rig.Seam.TryCommit(dodge, opportunityId, TriggerTick);

            Assert.That(outcome.Committed, Is.True, outcome.RejectionCode);
            Assert.That(outcome.InvalidatedCandidates.Count, Is.EqualTo(2));
            Assert.That(outcome.NewlyTerminatedPlans.Count, Is.EqualTo(2),
                "唯一终态协调器对每条依赖Move各终止一次");
            Assert.That(outcome.NewlyTerminatedPlans[0], Is.EqualTo(dependent.ActionPlanId));
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBefore),
                "Dodge 终态清理不是排程编辑事务，不得推进修订号");

            Assert.That(dependent.State, Is.EqualTo(ActionPlanState.Terminated));
            Assert.That(dependent.TerminationReason,
                Is.EqualTo(ActionTerminationReason.MovementOriginInvalidatedByDodge));
            Assert.That(independent.TerminationReason, Is.EqualTo(ActionTerminationReason.MovementOriginInvalidatedByDodge), "后续Move仍依赖旧起点，时间间隙不切断位置依赖");
            Assert.That(independent.StartTick, Is.EqualTo(IndependentMoveStartTick));

            // —— 释放恰好一次，且按计划自己的 SubmittedWindowId 回到原账本 ——
            TurnWindow source = rig.Windows.FindWindow(dependent.SubmittedWindowId.Value);
            Assert.That(source, Is.SameAs(window), "释放必须回到计划自己的来源窗口");
            Assert.That(window.ReservedFor(dependent.ActionPlanId), Is.Zero);
            Assert.That(window.ReservedFor(independent.ActionPlanId), Is.Zero,
                "后续依赖移动的预留也必须释放");
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(reservedBefore - dependentReserved - independentReserved));
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(spentBefore), "Editable 阶段终止不产生消费");
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(availableBefore + dependentReserved + independentReserved),
                "释放额只回到它原来的窗口账本");

            var releases = new List<TurnBudgetChange>();
            for (int i = 0; i < rig.Ledger.Count; i++)
            {
                if (rig.Ledger[i].ChangeKind == TurnBudgetChangeKind.ReleasedBeforeLock)
                    releases.Add(rig.Ledger[i]);
            }
            Assert.That(releases.Count, Is.EqualTo(2), "每条被失效的 Move 只允许一次 ReleasedBeforeLock");
            Assert.That(releases[0].ActionPlanId, Is.EqualTo(dependent.ActionPlanId));
            Assert.That(releases[0].WindowId, Is.EqualTo(window.WindowId));
            Assert.That(releases[0].Source, Is.EqualTo(ResourceChangeSource.TerminalCleanup),
                "释放只能由状态感知终态参与者发起，不能来自排程事务");
            Assert.That(releases[0].ReservedBefore - releases[0].ReservedAfter, Is.EqualTo(dependentReserved));

            // 幂等：重复提交不得第二次释放。
            DodgeRelocationOutcome repeated = rig.Seam.TryCommit(dodge, opportunityId, TriggerTick);
            Assert.That(repeated.Committed, Is.True);
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(reservedBefore - dependentReserved - independentReserved));
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(availableBefore + dependentReserved + independentReserved));

            Assert.That(rig.Dodge.BudgetReleaseSink, Is.Not.Null);
            Assert.That(rig.ReleaseSinkCalls.Count, Is.EqualTo(1), "换位只发生一次 ⇒ 接缝只被调用一次");
            Assert.That(rig.ReleaseSinkCalls[0].Count, Is.EqualTo(2));
            Assert.That(rig.ReleaseSinkCalls[0][0], Is.EqualTo(dependent.ActionPlanId));

            AssertLedgerIdentity(AllWindows(rig));
        }

        // =====================================================================
        // 2. DodgeMoveInvalidationDoesNotReopenClosedWindowOrTransferBudget
        // =====================================================================
        [Test]
        public void DodgeMoveInvalidationDoesNotReopenClosedWindowOrTransferBudget()
        {
            Rig rig = NewRig();

            TurnWindow closed = OpenWindow(rig, Window1OpenTick, Hero, budget: 1200);
            ActionPlan dependent = AddShortMove(rig, 1L, 0, 4, DependentMoveStartTick);
            int dependentReserved = closed.ReservedFor(dependent.ActionPlanId);
            Assert.That(dependentReserved, Is.GreaterThan(0));
            Assert.That(dependent.SubmittedWindowId.Value, Is.EqualTo(closed.WindowId));

            int closedTotal = closed.TotalBudgetTicks;
            int closedReservedBefore = closed.ReservedBudgetTicks;
            int closedSpentBefore = closed.SpentBudgetTicks;
            int closedAvailableBefore = closed.AvailableBudgetTicks;

            // 正式关闭（Tick 末），再打开另一个单位的窗口：释放额只能落在历史账本上。
            CloseWindow(rig, closed, tick: 1L);
            TurnWindow current = OpenWindow(rig, Window2OpenTick, EnemyA, budget: 1200);
            Assert.That(rig.Windows.IsAcceptingSubmissions, Is.True);
            Assert.That(rig.Windows.OpenWindow, Is.SameAs(current));

            ActionPlan nextMove = AddMove(rig, 2L, 4, 0, IndependentMoveStartTick);
            int nextMoveReserved = current.ReservedFor(nextMove.ActionPlanId);
            Assert.That(nextMoveReserved, Is.GreaterThan(0));
            Assert.That(nextMove.SubmittedWindowId.Value, Is.EqualTo(current.WindowId),
                "新计划的来源窗口必须是当前窗口，而不是上一个窗口");

            long nextMoveStart = nextMove.StartTick;
            long nextMoveEnd = nextMove.EndTick;

            ActionPlan dodge = AcceptDodge(rig, 4, 4, out ReactionOpportunityId opportunityId);

            // 基线必须在 Dodge 接受事务（它会新增一条来源攻击计划并占用当前窗口预算）之后取：
            // 否则下面的断言会把"接受事务的正常预留"误读成"释放额转移"。
            int currentReservedBefore = current.ReservedBudgetTicks;
            int currentAvailableBefore = current.AvailableBudgetTicks;
            long revisionBeforeCommit = rig.Schedule.ScheduleRevision;

            DodgeRelocationOutcome outcome = rig.Seam.TryCommit(dodge, opportunityId, TriggerTick);
            Assert.That(outcome.Committed, Is.True, outcome.RejectionCode);
            Assert.That(outcome.NewlyTerminatedPlans.Count, Is.EqualTo(2));

            // —— 已关闭窗口只更新历史账本 ——
            Assert.That(closed.IsAcceptingSubmissions, Is.False, "释放不得重开提交权限");
            Assert.That(closed.IsOpen, Is.False, "释放不得重开窗口");
            Assert.That(closed.CloseReason, Is.EqualTo(TurnWindowCloseReason.OwnerRequested),
                "关闭原因不得被释放改写");
            Assert.That(closed.ReservedFor(dependent.ActionPlanId), Is.Zero);
            Assert.That(closed.ReservedBudgetTicks, Is.EqualTo(closedReservedBefore - dependentReserved));
            Assert.That(closed.AvailableBudgetTicks, Is.EqualTo(closedAvailableBefore + dependentReserved));
            Assert.That(closed.SpentBudgetTicks, Is.EqualTo(closedSpentBefore));
            Assert.That(closed.ReservedBudgetTicks + closed.SpentBudgetTicks + closed.AvailableBudgetTicks,
                Is.EqualTo(closedTotal), "关闭窗口的恒等式继续成立");

            // —— 释放额不得转移给当前窗口 ——
            Assert.That(current.ReservedBudgetTicks, Is.EqualTo(currentReservedBefore - nextMoveReserved),
                "当前窗口只释放自己下一条移动的预留");
            Assert.That(current.AvailableBudgetTicks, Is.EqualTo(currentAvailableBefore + nextMoveReserved));
            Assert.That(current.ReservedFor(dependent.ActionPlanId), Is.Zero,
                "已关闭窗口的计划预算不得挂到当前窗口");
            Assert.That(rig.Windows.FindWindow(dependent.SubmittedWindowId.Value), Is.SameAs(closed),
                "关闭窗口的账本必须继续可审计");

            for (int i = 0; i < rig.Ledger.Count; i++)
            {
                if (rig.Ledger[i].ChangeKind != TurnBudgetChangeKind.ReleasedBeforeLock) continue;
                var releasedPlan = rig.Ledger[i].ActionPlanId == dependent.ActionPlanId ? dependent : nextMove;
                Assert.That(rig.Ledger[i].WindowId, Is.EqualTo(releasedPlan.SubmittedWindowId.Value));
            }

            // —— 不前移后续计划：释放不是排程编辑 ——
            Assert.That(nextMove.TerminationReason, Is.EqualTo(ActionTerminationReason.MovementOriginInvalidatedByDodge));
            Assert.That(nextMove.StartTick, Is.EqualTo(nextMoveStart));
            Assert.That(nextMove.EndTick, Is.EqualTo(nextMoveEnd));
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBeforeCommit),
                "本次 Dodge 终态清理不得推进排程修订号");

            // —— 不得伪造新的窗口授权 ——
            Assert.That(rig.Windows.IsAcceptingSubmissions, Is.True);
            Assert.That(rig.Windows.OpenWindow, Is.SameAs(current), "释放不得改变当前窗口的归属");
            Assert.That(rig.Windows.CurrentWindow, Is.SameAs(current));
            Assert.That(rig.Windows.FindWindow(closed.WindowId), Is.SameAs(closed));
            Assert.That(closed.CanReserve(1), Is.False, "关闭窗口不得因释放重新获得预留能力");

            AssertLedgerIdentity(AllWindows(rig));
        }

        // =====================================================================
        // 3. DodgeRelocationFailureDoesNotReleaseFutureMoveBudget
        // =====================================================================
        [Test]
        public void DodgeRelocationFailureDoesNotReleaseFutureMoveBudget()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, Window1OpenTick, Hero, budget: 1200);

            ActionPlan dependent = AddShortMove(rig, 1L, 0, 4, DependentMoveStartTick);
            int dependentReserved = window.ReservedFor(dependent.ActionPlanId);
            Assert.That(dependentReserved, Is.GreaterThan(0));

            ActionPlan dodge = AcceptDodge(rig, 4, 4, out ReactionOpportunityId opportunityId);
            Assert.That(dependentReserved, Is.GreaterThan(0));

            // 目的格在预留之后被另一个单位占据 ⇒ 换位整体失败（Dodge 无抢占权限）。
            Assert.That(rig.Grid.RegisterUnitWithPointFootprint(Occupier, new GridPoint(4, 4), GridDirection.East),
                Is.Null, "占据目的格必须成功，否则本用例无法构造失败路径");

            // 基线在"接受事务与占位之后、换位之前"取：本用例只证明换位事务本身零预算影响。
            int reservedBefore = window.ReservedBudgetTicks;
            int availableBefore = window.AvailableBudgetTicks;
            int spentBefore = window.SpentBudgetTicks;
            rig.Ledger.Clear();
            long revisionBefore = rig.Schedule.ScheduleRevision;

            DodgeRelocationOutcome outcome = rig.Seam.TryCommit(dodge, opportunityId, TriggerTick);

            Assert.That(outcome.Committed, Is.False, "换位必须失败");
            Assert.That(outcome.RejectionCode, Is.Not.Null, "失败必须带稳定拒绝码");
            Assert.That(outcome.RejectionCode, Does.StartWith("LOGIC_GRID_OCCUPIED_BY_OTHER"),
                "目的格被其他单位占据 ⇒ 换位整体失败（Dodge 无抢占权限）；实际=" + outcome.RejectionCode);

            // —— 不因本次 Dodge 释放任何预算 ——
            Assert.That(window.ReservedFor(dependent.ActionPlanId), Is.EqualTo(dependentReserved),
                "换位失败时依赖 Move 的未消费预留必须原样保留");
            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(reservedBefore));
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(availableBefore));
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(spentBefore));

            Assert.That(dependent.IsEditable, Is.True, "换位失败时不得终止任何依赖 Move");
            Assert.That(dependent.TerminationReason, Is.EqualTo(ActionTerminationReason.None));
            Assert.That(dependent.StartTick, Is.EqualTo(DependentMoveStartTick));
            Assert.That(rig.Schedule.Registry.Contains(dependent.ActionPlanId), Is.True,
                "失败的换位不得把计划移出权威注册表");
            Assert.That(dependent.ReservedTurnBudgetTicks, Is.GreaterThan(0),
                "计划自身的未消费预留投影也必须原样保留");

            for (int i = 0; i < rig.Ledger.Count; i++)
            {
                Assert.That(rig.Ledger[i].ChangeKind, Is.Not.EqualTo(TurnBudgetChangeKind.ReleasedBeforeLock),
                    "失败的换位不得产生任何释放账本变化");
            }

            Assert.That(rig.ReleaseSinkCalls.Count, Is.Zero, "未换位时预算释放接缝绝不被调用");
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBefore));
            Assert.That(rig.Grid.TryGetAnchor(Hero, out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(0, 0)), "失败的 Dodge 不移动单位");

            AssertLedgerIdentity(AllWindows(rig));
        }

        // =====================================================================
        // 4. ForcedDisplacementDoesNotRefundSpentMoveBudget
        // =====================================================================
        [Test]
        public void ForcedDisplacementDoesNotRefundSpentMoveBudget()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, Window1OpenTick, Hero, budget: 1200);

            ActionPlan move = AddMove(rig, 1L, 4, 0, DependentMoveStartTick);
            int cost = move.BudgetCostTicks;
            Assert.That(cost, Is.GreaterThan(0));

            // 经生产启动提交端口：Reserved -> Spent（锁定成功之后一律不退款）；
            // 再写入同一原子提交里建立的 LockedAtTick / Running 标记。
            CommitStartLock(rig, move, tick: 290L);
            MarkLockedAndRunning(move, lockedAtTick: 290L);
            Assert.That(move.IsRunning, Is.True);
            Assert.That(move.LockedAtTick, Is.EqualTo(290L));
            Assert.That(move.IsEditable, Is.False);
            Assert.That(window.ReservedFor(move.ActionPlanId), Is.Zero);
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(cost));
            int spentBefore = window.SpentBudgetTicks;
            int availableBefore = window.AvailableBudgetTicks;

            rig.Ledger.Clear();
            long revisionBefore = rig.Schedule.ScheduleRevision;

            ActionPlanTerminalOutcome outcome = rig.Coordinator.EnterTerminal(
                move, ActionTerminationReason.ReservationPreemptedByForcedDisplacement, tick: 300L);

            Assert.That(outcome.EnteredTerminal, Is.True);
            Assert.That(move.State, Is.EqualTo(ActionPlanState.Terminated));
            Assert.That(move.TerminationReason,
                Is.EqualTo(ActionTerminationReason.ReservationPreemptedByForcedDisplacement));

            // —— 不退款 ——
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(spentBefore),
                "Locked/Running 的 Spent 在任何终态原因下都不得返还");
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(availableBefore));
            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(window.HasReservation(move.ActionPlanId), Is.False,
                "已消费的计划不得重新获得预留");
            var ledgerDump = new List<string>();
            for (int i = 0; i < rig.Ledger.Count; i++)
            {
                TurnBudgetChange entry = rig.Ledger[i];
                ledgerDump.Add(entry.ChangeKind + "/" + entry.Source + "/win=" + entry.WindowId.Value +
                               "/plan=" + (entry.ActionPlanId.HasValue ? entry.ActionPlanId.Value.Value : 0L) +
                               "/avail=" + entry.AvailableBefore + "->" + entry.AvailableAfter +
                               "/res=" + entry.ReservedBefore + "->" + entry.ReservedAfter +
                               "/spent=" + entry.SpentBefore + "->" + entry.SpentAfter);
            }
            Assert.That(rig.Ledger.Count, Is.Zero,
                "强制位移终态不得产生任何 TurnBudget 账本变化（尤其不得回滚/退款）: " +
                string.Join(" ;; ", ledgerDump));
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBefore));

            // 重复清理幂等：第二次请求不改变任何值。
            ActionPlanTerminalOutcome repeated = rig.Coordinator.EnterTerminal(
                move, ActionTerminationReason.ReservationPreemptedByForcedDisplacement, tick: 301L);
            Assert.That(repeated.WasAlreadyTerminal, Is.True);
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(spentBefore));
            Assert.That(rig.Ledger.Count, Is.Zero);

            // 以 MovementOriginInvalidated 收场的 Locked/Running 计划同样不退。
            // 注意：新增计划本身会在该窗口产生正常预留，因此基线必须在新增之后、终态之前取。
            ActionPlan other = AddMove(rig, 2L, 0, 4, IndependentMoveStartTick);
            CommitStartLock(rig, other, tick: 380L);
            MarkLockedAndRunning(other, lockedAtTick: 380L);
            int spentAfterOther = window.SpentBudgetTicks;
            Assert.That(spentAfterOther, Is.EqualTo(cost + other.BudgetCostTicks));
            rig.Ledger.Clear();
            rig.Coordinator.EnterTerminal(other, ActionTerminationReason.MovementOriginInvalidated, tick: 400L);
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(spentAfterOther),
                "MovementOriginInvalidated 同样不得返还 Locked/Running 的 Spent");
            Assert.That(rig.Ledger.Count, Is.Zero,
                "强制位移终态不得产生任何 TurnBudget 账本变化: " + LedgerDump(rig));

            AssertLedgerIdentity(AllWindows(rig));
        }

        // =====================================================================
        // 5. ForcedDisplacementReleasesOnlyUnconsumedEditableReservation
        // =====================================================================
        [Test]
        public void ForcedDisplacementReleasesOnlyUnconsumedEditableReservation()
        {
            Rig rig = NewRig();
            TurnWindow window = OpenWindow(rig, Window1OpenTick, Hero, budget: 1200);

            ActionPlan editable = AddShortMove(rig, 1L, 0, 4, DependentMoveStartTick);
            ActionPlan locked = AddMove(rig, 2L, 4, 0, IndependentMoveStartTick);
            int editableReserved = window.ReservedFor(editable.ActionPlanId);
            int lockedCost = locked.BudgetCostTicks;
            Assert.That(editableReserved, Is.GreaterThan(0));

            CommitStartLock(rig, locked, tick: 470L);
            MarkLockedAndRunning(locked, lockedAtTick: 470L);
            Assert.That(window.ReservedFor(locked.ActionPlanId), Is.Zero);
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(lockedCost));

            int reservedBefore = window.ReservedBudgetTicks;
            int spentBefore = window.SpentBudgetTicks;
            int availableBefore = window.AvailableBudgetTicks;
            Assert.That(reservedBefore, Is.EqualTo(editableReserved),
                "此刻只有仍为 Editable 的那条计划持有未消费预留: reservedBefore=" + reservedBefore +
                " editableReserved=" + editableReserved + " ledger=" + LedgerDump(rig));

            rig.Ledger.Clear();

            // 只终止仍为 Editable 的那一条：只有它的未消费预留应当被释放。
            rig.Coordinator.EnterTerminal(
                editable, ActionTerminationReason.MovementOriginInvalidated, tick: 480L);
            Assert.That(editable.State, Is.EqualTo(ActionPlanState.Terminated));

            Assert.That(window.ReservedFor(editable.ActionPlanId), Is.Zero);
            Assert.That(window.ReservedBudgetTicks, Is.Zero, "未消费预留必须被释放");
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(availableBefore + editableReserved));
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(spentBefore), "已消费额度不得被释放");
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(lockedCost));
            Assert.That(locked.IsRunning, Is.True, "已锁定/运行的邻居计划不得被本次清理触及");
            Assert.That(locked.ReservedTurnBudgetTicks, Is.Zero);
            Assert.That(locked.TerminationReason, Is.EqualTo(ActionTerminationReason.None));

            Assert.That(rig.Ledger.Count, Is.EqualTo(1),
                "第一次清理: 只有一条 Editable 计划被清理 ⇒ 恰好一次账本变化: " + LedgerDump(rig));
            Assert.That(rig.Ledger[0].ChangeKind, Is.EqualTo(TurnBudgetChangeKind.ReleasedBeforeLock));
            Assert.That(rig.Ledger[0].ActionPlanId, Is.EqualTo(editable.ActionPlanId));
            Assert.That(rig.Ledger[0].ReservedBefore - rig.Ledger[0].ReservedAfter, Is.EqualTo(editableReserved));
            Assert.That(rig.Ledger[0].SpentBefore, Is.EqualTo(rig.Ledger[0].SpentAfter),
                "释放不得触碰 Spent");

            // 重复终态请求幂等：不释放第二次。
            ActionPlanTerminalOutcome repeated = rig.Coordinator.EnterTerminal(
                editable, ActionTerminationReason.MovementOriginInvalidated, tick: 481L);
            Assert.That(repeated.WasAlreadyTerminal, Is.True);
            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(availableBefore + editableReserved));
            Assert.That(rig.Ledger.Count, Is.EqualTo(1),
                "重复终态请求之后: " + LedgerDump(rig));

            AssertLedgerIdentity(AllWindows(rig));
        }

        // =====================================================================
        // 6. ForcedDisplacementReservationReleaseDoesNotReopenOrTransferWindowBudget
        // =====================================================================
        [Test]
        public void ForcedDisplacementReservationReleaseDoesNotReopenOrTransferWindowBudget()
        {
            Rig rig = NewRig();

            TurnWindow closed = OpenWindow(rig, Window1OpenTick, Hero, budget: 1200);
            ActionPlan editable = AddShortMove(rig, 1L, 0, 4, DependentMoveStartTick);
            int editableReserved = closed.ReservedFor(editable.ActionPlanId);
            Assert.That(editableReserved, Is.GreaterThan(0));
            Assert.That(editable.SubmittedWindowId.Value, Is.EqualTo(closed.WindowId));

            int closedReservedBefore = closed.ReservedBudgetTicks;
            int closedSpentBefore = closed.SpentBudgetTicks;
            int closedAvailableBefore = closed.AvailableBudgetTicks;

            CloseWindow(rig, closed, tick: 1L);
            TurnWindow current = OpenWindow(rig, Window2OpenTick, EnemyA, budget: 1200);
            ActionPlan later = AddMove(rig, 2L, 4, 0, IndependentMoveStartTick);
            int laterReserved = current.ReservedFor(later.ActionPlanId);
            int currentReservedBefore = current.ReservedBudgetTicks;
            int currentAvailableBefore = current.AvailableBudgetTicks;
            long laterStart = later.StartTick;
            long laterEnd = later.EndTick;
            Assert.That(laterReserved, Is.GreaterThan(0));
            Assert.That(later.SubmittedWindowId.Value, Is.EqualTo(current.WindowId));

            long revisionBeforeTerminal = rig.Schedule.ScheduleRevision;

            rig.Coordinator.EnterTerminal(
                editable, ActionTerminationReason.MovementOriginInvalidated, tick: 300L);

            // —— 已关闭窗口只更新历史账本：不重开、不转移、不前移 ——
            Assert.That(closed.IsOpen, Is.False);
            Assert.That(closed.IsAcceptingSubmissions, Is.False);
            Assert.That(closed.CanReserve(1), Is.False);
            Assert.That(closed.CloseReason, Is.EqualTo(TurnWindowCloseReason.OwnerRequested));
            Assert.That(closed.ReservedBudgetTicks, Is.EqualTo(closedReservedBefore - editableReserved));
            Assert.That(closed.SpentBudgetTicks, Is.EqualTo(closedSpentBefore));
            Assert.That(closed.AvailableBudgetTicks, Is.EqualTo(closedAvailableBefore + editableReserved));

            Assert.That(current.IsOpen, Is.True);
            Assert.That(current.IsAcceptingSubmissions, Is.True);
            Assert.That(current.ReservedBudgetTicks, Is.EqualTo(currentReservedBefore),
                "释放额不得转移给当前窗口");
            Assert.That(current.AvailableBudgetTicks, Is.EqualTo(currentAvailableBefore));
            Assert.That(current.ReservedFor(editable.ActionPlanId), Is.Zero);

            Assert.That(rig.Windows.CurrentWindow, Is.SameAs(current));
            Assert.That(rig.Windows.OpenWindow, Is.SameAs(current));
            Assert.That(rig.Windows.FindWindow(closed.WindowId), Is.SameAs(closed),
                "关闭窗口的账本继续可审计");

            Assert.That(later.IsEditable, Is.True);
            Assert.That(later.StartTick, Is.EqualTo(laterStart), "后续计划不得被前移");
            Assert.That(later.EndTick, Is.EqualTo(laterEnd));
            Assert.That(later.ReservedTurnBudgetTicks, Is.GreaterThan(0),
                "后续计划自己的预留不得被清零");
            Assert.That(rig.Schedule.ScheduleRevision, Is.EqualTo(revisionBeforeTerminal),
                "强制位移终态清理不是排程编辑，不得推进修订号");

            // 未实现"因释放而重新排定下一窗口"的路径：排程桶必须为空。
            Assert.That(rig.Windows.ScheduledWindowCount, Is.Zero);
            Assert.That(rig.Windows.NextWindowTick, Is.EqualTo(-1L));

            AssertLedgerIdentity(AllWindows(rig));
        }
    }
}
