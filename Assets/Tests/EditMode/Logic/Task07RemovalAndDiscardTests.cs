using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 07 本轮两项生产修复的<strong>仓库内自动化证据</strong>（文件所有权：本文件是本轮唯一新增的测试文件）。
    ///
    /// <list type="number">
    /// <item><strong>裁定 A：排程删除必须经唯一终态协调器收口</strong>
    /// （00 号规则 19：排程删除是终态之一，Step 返回时不得有任何活动产物引用它）。
    /// 分工：<c>ScheduleEditor</c> 独占预算释放（事务里"移出 Lane + 释放未消费预留 + 归零计划字段"），
    /// <c>BattleSimulation.TerminateRemovedPlans</c> 只把计划对象置为终态
    /// （<c>_terminalCoordinator.EnterTerminal(plan, CancelledByCommand, tick)</c>），
    /// 二者挂在<strong>同一个</strong> <c>_planCommandProcessor.CommittedTransactionSink</c> 上。
    /// 证据路线 = <strong>真实 <see cref="BattleSimulation"/></strong>：命令网关冻结批次 → Step 阶段 6
    /// 真实排程事务 → 真实 sink → 真实协调器，因此本文件的用例 1–3
    /// <strong>对本轮那个 sink 调用点本身敏感</strong>（去掉它，用例 2 立刻变红，见文件末 §回归敏感性）。</item>
    /// <item><strong>裁定 B：肾上腺素新增"终态作废"入口（删除但不退款）</strong>
    /// （<c>AdrenalineChangeKind.ReservationDiscarded = 7</c>、<c>IAdrenalineLedgerPort.DiscardReservation</c>、
    /// <see cref="BudgetAndAdrenalineCleanupParticipant"/> 的反应分支二分）。
    /// 证据路线 = <strong>真实机会系统</strong>（<c>TryOpenForTelegraph</c> + <c>TryAcceptById</c>）
    /// 造出真实 Locked 反应计划与真实肾上腺素预留，再由<strong>真实协调器</strong>以非来源取消原因终结，
    /// 观察真实账本与真实端口载荷。</item>
    /// </list>
    ///
    /// 本文件<strong>不</strong>复制任何生产算法、<strong>不</strong>改 <c>Assets/Scripts/**</c>、
    /// 不建立第二套预算/肾上腺素模型；断言观察点全部是权威对象
    /// （<c>ActionPlan</c> 字段、<c>ActionScheduleAuthority</c> 注册表/Lane/不变量、
    /// <c>TurnWindow</c> 账本、<c>AdrenalineLedger</c>、以及各生产端口发射的载荷/事件）。
    /// </summary>
    public class Task07RemovalAndDiscardTests
    {
        // ================= 夹具常量 =================

        private const string MoveSpecId = "action.t07rd.move";
        private const string GuardSpecId = "action.t07rd.guard";
        private const string AttackSpecId = "action.t07rd.attack";
        private const string BlockSpecId = "action.t07rd.block";
        private const string DodgeSpecId = "action.t07rd.dodge";
        private const string ActionSetIdValue = "action_set.t07rd";
        private const string EncounterIdValue = "encounter.t07rd";
        private const string HeroSlotId = "hero";
        private const string MonsterSlotId = "monster";
        private const string EncounterDefinitionIdValue = "battle-definition.task07.removal-discard";

        /// <summary>单位速度恒等于基准速度（20）⇒ 前摇/每权重单位 Tick 的解析是恒等映射。</summary>
        private static readonly BattleRules Rules = BattleRules.FrozenV1;

        private const int MoveBaseStepTicks = 5;
        private const int MoveRecoveryTicks = 5;
        private const int GuardWindupTicks = 10;
        private const int GuardActiveTicks = 20;
        private const int GuardRecoveryTicks = 5;

        /// <summary>Guard 的权威成本（Windup + Active + Recovery = 35 Tick）。</summary>
        private const int GuardCost = GuardWindupTicks + GuardActiveTicks + GuardRecoveryTicks;

        private const int AttackBaseWindupTicks = 100;
        private const int AttackRecoveryTicks = 10;

        /// <summary>窗口预算：必须 &gt; GuardCost，且为构造"锁定前释放"留出可观察差额。</summary>
        private const int WindowBudget = 60;

        /// <summary>AdrenalineRules.FrozenV1 的 Block 费用（生产 <c>ActionSpec.AdrenalineCost</c>）。</summary>
        private const int BlockCost = FrozenDesignValues.BlockAdrenalineCost;   // 2

        private static readonly GridBoundaryDefinition Wide =
            new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40));

        private static readonly UnitId Hero = new UnitId(1L);
        private static readonly UnitId Monster = new UnitId(2L);
        private static readonly FactionId HeroFaction = new FactionId("faction.hero");
        private static readonly FactionId MonsterFaction = new FactionId("faction.monster");
        private static readonly ControllerId Player = new ControllerId("controller.player");
        private static readonly ControllerId MonsterAi = new ControllerId("controller.monster_ai");

        private static ActionPlanId P(long id) => new ActionPlanId(id);
        private static ActionSpecId Spec(string id) => new ActionSpecId(id);
        private static ActionSetId ActionSet => new ActionSetId(ActionSetIdValue);
        private static EncounterDefinitionId EncounterId => new EncounterDefinitionId(EncounterIdValue);
        private static EncounterSlotId Slot(string id) => new EncounterSlotId(id);
        private static BattleRuntimeInputs Inputs => new BattleRuntimeInputs(InitialRngSeed: 7UL, InitialMetaResource: 0);

        // =====================================================================
        // 定义夹具（任务 02B 的纯数据定义；本文件自建，不依赖 Authoring 程序集）
        // =====================================================================

        private static IReadOnlyList<DirectionalTriangleSet> Directions()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        private static MovementPatternSpec MovementPattern()
            => new MovementPatternSpec(new MovementPatternId("movement_pattern.t07rd"), Directions());

        /// <summary>
        /// 唯一一份定义：它同时喂给"全量模拟装配"（用例 1–3，需要真实 Encounter/槽位/控制者绑定）
        /// 与"轻量 Logic 装配"（用例 4–6，只用到动作/单位/体积/动作集合）。
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

            // 长前摇攻击：StartTick + 100 = ImpactTick ⇒ 反应选项截止 Tick 远晚于
            // TelegraphTick + CommandIngressLeadTicks(2)，因此 Block 会被公开。
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
                AdrenalineCost: BlockCost);

            var dodgeSpec = new ActionSpec(
                Spec(DodgeSpecId), ActionType.Dodge,
                new DodgeReactionTimingSpec(FrozenDesignValues.DodgeReactionWindupTicks, 10),
                new DodgePayloadSpec(MaxDistanceSteps: 4, Pattern: MovementPattern()),
                AdrenalineCost: FrozenDesignValues.DodgeAdrenalineCost);

            var volume = new VolumeSpec(new VolumeSpecId("unit_volume.t07rd"), Directions());
            var actionSet = new ActionSetDefinition(
                ActionSet,
                new List<ActionSpecId>
                {
                    moveSpec.ActionSpecId, guardSpec.ActionSpecId, attackSpec.ActionSpecId,
                    blockSpec.ActionSpecId, dodgeSpec.ActionSpecId
                });

            var heroDefinitionId = new UnitDefinitionId("unit.t07rd.hero");
            var monsterDefinitionId = new UnitDefinitionId("unit.t07rd.monster");

            var units = new List<UnitDefinition>
            {
                new UnitDefinition(heroDefinitionId, 10f, 10f,
                    Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), 200f,
                    actionSet.ActionSetId, volume.VolumeSpecId),
                new UnitDefinition(monsterDefinitionId, 10f, 10f,
                    Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), 200f,
                    actionSet.ActionSetId, volume.VolumeSpecId)
            };

            // SlotId 的 Ordinal 顺序 = UnitId 分配顺序（"hero" < "monster" ⇒ Hero=UnitId(1)）。
            // 该映射不靠"猜"：夹具在 NewSimRig 里用 BattleInitializer 的只读结果显式断言。
            var encounter = new EncounterDefinition(
                EncounterId,
                Wide,
                new List<EncounterUnitSlot>
                {
                    new EncounterUnitSlot(Slot(HeroSlotId), heroDefinitionId, HeroFaction,
                        new GridPoint(0, 0), GridDirection.East),
                    new EncounterUnitSlot(Slot(MonsterSlotId), monsterDefinitionId, MonsterFaction,
                        new GridPoint(0, 30), GridDirection.East)
                },
                new List<ControllerBinding>
                {
                    new ControllerBinding(Player, CommandSourceKind.Player,
                        new List<EncounterSlotId> { Slot(HeroSlotId) }),
                    new ControllerBinding(MonsterAi, CommandSourceKind.Ai,
                        new List<EncounterSlotId> { Slot(MonsterSlotId) })
                },
                new VictoryDefinition(
                    new List<FactionId> { HeroFaction },
                    new List<FactionId> { MonsterFaction },
                    "result.t07rd.victory", "result.t07rd.defeat", "result.t07rd.draw"));

            return new BattleDefinition(
                "battle-definition.task07.removal-discard",
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
                new List<EncounterDefinition> { encounter },
                null,
                "test-definition-hash.t07.removal-discard");
        }

        private sealed class Facts : IActionPlanFactsSource
        {
            private readonly Dictionary<long, ActionPlanOwnerFacts> _map =
                new Dictionary<long, ActionPlanOwnerFacts>();

            public void Add(UnitId unitId, FactionId factionId)
                => _map[unitId.Value] = new ActionPlanOwnerFacts(
                    unitId, factionId, Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed, ActionSet, true);

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

        private static IFactionRelationResolver BuildResolver(BattleDefinition definition)
        {
            var unitFactions = new Dictionary<UnitId, FactionId>
            {
                [Hero] = HeroFaction,
                [Monster] = MonsterFaction
            };
            return new FactionRelationResolver(definition.FactionModel, unitFactions);
        }

        // =====================================================================
        // 装配 A：全量模拟（用例 1–3）
        //
        // 这是本文件对"裁定 A"的首选证据路线：sink 的调用点是 BattleSimulation 的构造闭包，
        // 因此只有**真实驱动一次命令阶段**才能证明该调用点存在且生效。
        // 装配方式 = BattleSimulation.Create（唯一创建路径）+ 唯一命令入口的 Submit/FreezeTick + Step。
        // =====================================================================

        private sealed class SimRig
        {
            public BattleDefinition Definition;
            public BattleSimulation Sim;
            public UnitId HeroId;
            public UnitId MonsterId;
            public CommandIngressEntry PlayerEntry;

            /// <summary>逐 Tick 累积的公开事件（Step 只在 Tick 末一次性发布）。</summary>
            public readonly List<LogicEvent> Events = new List<LogicEvent>();

            public TurnWindow Window;
        }

        private static SimRig NewSimRig()
        {
            BattleDefinition definition = BuildDefinition();

            // 只读地把"槽位 → UnitId"的权威分配结果取出来（BattleInitializer 是任务 02B 的
            // 唯一配置/初始化真相）。夹具不假设分配顺序，只断言本定义下 hero 恰好拿到 UnitId(1)。
            BattleInitializationResult initialization =
                BattleInitializer.BuildInitialState(definition, EncounterId, Inputs);
            Assert.That(initialization.InitialUnits.Count, Is.EqualTo(2), "夹具前提：本定义恰好两个出场单位");
            UnitId heroId = initialization.SlotToUnitId[Slot(HeroSlotId)];
            UnitId monsterId = initialization.SlotToUnitId[Slot(MonsterSlotId)];
            Assert.That(heroId, Is.EqualTo(Hero), "夹具前提：SlotId(\"hero\") 必须拿到 UnitId(1)");
            Assert.That(monsterId, Is.EqualTo(Monster), "夹具前提：SlotId(\"monster\") 必须拿到 UnitId(2)");

            BattleSimulation sim = BattleSimulation.Create(definition, EncounterId, Inputs);
            CommandIngressEntry playerEntry = sim.CommandIngress.FindEntry(Player);
            Assert.That(playerEntry, Is.Not.Null,
                "夹具前提：定义的 ControllerBinding 必须注册出玩家命令入口");

            var rig = new SimRig
            {
                Definition = definition,
                Sim = sim,
                HeroId = heroId,
                MonsterId = monsterId,
                PlayerEntry = playerEntry
            };

            // Tick 0：打开主角自己的窗口（窗口必须在**排程事务之前**处于"接受提交"状态）。
            sim.WindowManager.ScheduleWindow(0L, heroId, WindowBudget);
            StepSim(rig, 0L);
            Assert.That(sim.CurrentTurnWindow, Is.Not.Null, "夹具前提：Tick 0 必须打开主角窗口");
            Assert.That(sim.CurrentTurnWindow.OwnerUnitId, Is.EqualTo(heroId));
            Assert.That(sim.CurrentTurnWindow.TotalBudgetTicks, Is.EqualTo(WindowBudget));
            Assert.That(sim.CurrentTurnWindow.IsAcceptingSubmissions, Is.True);
            rig.Window = sim.CurrentTurnWindow;
            return rig;
        }

        /// <summary>冻结批次 → Step → 累积事件；同时断言命令在<strong>入口</strong>与<strong>处理器</strong>两级都未被拒绝。</summary>
        private static StepResult StepSim(SimRig rig, long tick, params CommandRequest[] requests)
        {
            for (int i = 0; i < requests.Length; i++)
            {
                CommandIngressRejection rejection = rig.PlayerEntry.Submit(requests[i]);
                Assert.That(rejection, Is.Null,
                    "夹具前提：命令入口必须接受请求（" + (rejection == null ? "<null>" : rejection.ReasonCode) + "）");
            }

            FrozenCommandBatch batch = rig.Sim.CommandIngress.FreezeTick(tick);
            StepResult result = rig.Sim.Step(tick, batch);
            for (int i = 0; i < result.Events.Count; i++) rig.Events.Add(result.Events.Events[i]);
            return result;
        }

        /// <summary>
        /// 夹具不变量：本 Tick 的命令不得被<strong>处理器</strong>拒绝。
        /// 它让"命令被拒 → 后续断言以奇怪方式失败"变成一条直白的失败信息。
        /// </summary>
        private static void AssertNoProcessorRejection(StepResult result)
        {
            for (int i = 0; i < result.Events.Count; i++)
            {
                if (result.Events.Events[i] is CommandRejectedEvent rejected)
                {
                    Assert.Fail("夹具前提：命令被处理器拒绝：" + rejected.ReasonCode +
                                " commandSequence=" + rejected.CommandSequence);
                }
                if (result.Events.Events[i] is CommandIngressRejectedEvent ingressRejected)
                {
                    Assert.Fail("夹具前提：命令被入口拒绝：" + ingressRejected.ReasonCode);
                }
            }
        }

        private static ScheduleEditPayload GuardAddPayload(SimRig rig, long startTick)
            => new ScheduleEditPayload(new ScheduleEditOperation[]
            {
                new AddOrdinaryPlanOperation(
                    1L, rig.HeroId, Spec(GuardSpecId), startTick,
                    default, null, GridDirection.East, null)
            });

        private static ScheduleEditPayload RemovePayload(ActionPlanId planId)
            => new ScheduleEditPayload(new ScheduleEditOperation[]
            {
                new RemoveEditablePlanOperation(planId, ActionTerminationReason.CancelledByCommand)
            });

        /// <summary>经真实命令阶段新增一个普通 Guard（预算由任务 07 的账本端口原子预留）。</summary>
        private static ActionPlan AddGuardThroughCommand(SimRig rig, long tick, long startTick)
        {
            long revisionBefore = rig.Sim.ScheduleAuthority.ScheduleRevision;
            var request = new CommandRequest(
                tick,
                new ScheduleEditScope(revisionBefore, rig.Window.WindowId),
                GuardAddPayload(rig, startTick));

            StepResult result = StepSim(rig, tick, request);
            AssertNoProcessorRejection(result);
            Assert.That(result.IsAdvanced, Is.True);
            Assert.That(rig.Sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(revisionBefore + 1L),
                "夹具前提：一次成功排程事务恰好令 ScheduleRevision +1");

            IReadOnlyList<ActionPlan> active = rig.Sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(active.Count, Is.EqualTo(1), "夹具前提：本 Tick 恰好新增一个活动计划");
            ActionPlan plan = active[0];
            Assert.That(plan.OwnerUnitId, Is.EqualTo(rig.HeroId));
            Assert.That(plan.ActionSpecId, Is.EqualTo(Spec(GuardSpecId)));
            Assert.That(plan.IsEditable, Is.True, "夹具前提：新增的普通计划处于 Editable");
            Assert.That(plan.SubmittedWindowId, Is.Not.Null);
            Assert.That(plan.SubmittedWindowId.Value, Is.EqualTo(rig.Window.WindowId),
                "夹具前提：计划的预算来源窗口 = 命令 scope 声明的窗口");
            return plan;
        }

        /// <summary>经真实命令阶段提交 Remove（与生产同一条命令路径）。</summary>
        private static StepResult RemoveThroughCommand(SimRig rig, long tick, ActionPlanId planId)
        {
            var request = new CommandRequest(
                tick,
                new ScheduleEditScope(rig.Sim.ScheduleAuthority.ScheduleRevision, rig.Window.WindowId),
                RemovePayload(planId));

            StepResult result = StepSim(rig, tick, request);
            AssertNoProcessorRejection(result);
            Assert.That(result.IsAdvanced, Is.True);
            return result;
        }

        /// <summary>用例 1–3 共用的真实场景：Add（tick 1）→ Remove（tick 2）。</summary>
        private sealed class RemovalScenario
        {
            public SimRig Rig;
            public ActionPlan Plan;
            public long AddTick = 1L;
            public long RemoveTick = 2L;
            public int AvailableAfterAdd;
            public int ReservedAfterAdd;
            public int SpentAfterAdd;
            public StepResult RemoveStep;
        }

        private static RemovalScenario ArrangeRemovedPlan()
        {
            SimRig rig = NewSimRig();
            ActionPlan plan = AddGuardThroughCommand(rig, tick: 1L, startTick: 400L);

            var scenario = new RemovalScenario { Rig = rig, Plan = plan };
            scenario.AvailableAfterAdd = rig.Window.AvailableBudgetTicks;
            scenario.ReservedAfterAdd = rig.Window.ReservedBudgetTicks;
            scenario.SpentAfterAdd = rig.Window.SpentBudgetTicks;
            Assert.That(scenario.ReservedAfterAdd, Is.EqualTo(GuardCost), "夹具前提：新增计划预留了权威成本");
            Assert.That(scenario.AvailableAfterAdd, Is.EqualTo(WindowBudget - GuardCost));
            Assert.That(scenario.SpentAfterAdd, Is.Zero, "夹具前提：锁前预留不是消费");

            scenario.RemoveStep = RemoveThroughCommand(rig, scenario.RemoveTick, plan.ActionPlanId);
            return scenario;
        }

        private static int CountBudgetEvents(
            SimRig rig, TurnBudgetChangeKind kind, ActionPlanId planId)
        {
            int count = 0;
            for (int i = 0; i < rig.Events.Count; i++)
            {
                if (!(rig.Events[i] is TurnBudgetChangedEvent change)) continue;
                if (change.ChangeKind != kind) continue;
                if (change.ActionPlanId != planId.Value) continue;
                count++;
            }
            return count;
        }

        private static int CountBudgetEventsBySource(
            SimRig rig, ResourceChangeSource source, ActionPlanId planId)
        {
            int count = 0;
            for (int i = 0; i < rig.Events.Count; i++)
            {
                if (!(rig.Events[i] is TurnBudgetChangedEvent change)) continue;
                if (change.Source != source) continue;
                if (change.ActionPlanId != planId.Value) continue;
                count++;
            }
            return count;
        }

        // =====================================================================
        // 装配 B：轻量 Logic（用例 4–6）
        //
        // 反应计划由**真实机会系统**（TryOpenForTelegraph + TryAcceptById）创建并注册，
        // 肾上腺素预留由**真实账本端口**（IAdrenalineLedgerPort）在同一条接受事务里完成，
        // 终态由**真实协调器**（含固定槽位 600 的 BudgetAndAdrenalineCleanupParticipant）提交。
        // 本夹具不构造 BattleSimulation：反应计划的终态原因在生产里由控制/死亡等阶段驱动，
        // 无法在"纯 Logic + 无阶段驱动"的边界内稳定复现（见 §反射点说明）。
        // =====================================================================

        private sealed class ReactionRig
        {
            public BattleDefinition Definition;
            public LogicGrid Grid;
            public ActionScheduleAuthority Schedule;
            public LogicIdGenerator Ids;
            public ActionPlanFactory Factory;
            public LogicGridMovementAuthority Movement;
            public ScheduleEditor Editor;
            public TurnWindowManager Windows;
            public TurnWindowBudgetAuthority Budget;
            public AdrenalineLedgerRegistry Adrenaline;
            public BudgetAndAdrenalineCleanupParticipant BudgetParticipant;
            public ActionPlanTerminalCoordinator Coordinator;
            public ReactionOpportunitySystem Reactions;

            /// <summary>肾上腺素账本端口发射的变化载荷（逐次迁移的前后值都在这里）。</summary>
            public readonly List<AdrenalineLedgerChange> AdrenalineChanges = new List<AdrenalineLedgerChange>();
        }

        private static ReactionRig NewReactionRig()
        {
            BattleDefinition definition = BuildDefinition();
            var facts = new Facts();
            facts.Add(Hero, HeroFaction);
            facts.Add(Monster, MonsterFaction);

            var schedule = new ActionScheduleAuthority();
            var ids = new LogicIdGenerator();
            IFactionRelationResolver resolver = BuildResolver(definition);
            var factory = new ActionPlanFactory(definition, resolver, facts, ids);
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
            var bindingParticipant = new OpportunityBindingCleanupParticipant();
            var coordinator = new ActionPlanTerminalCoordinator(schedule, new IActionPlanCleanupParticipant[]
            {
                StopSchedulingCleanupParticipant.Instance,
                bindingParticipant,
                movement,
                participant
            });
            var reactions = new ReactionOpportunitySystem(
                schedule, factory, definition, resolver, ids, null, coordinator);
            reactions.AdrenalinePort = adrenaline;

            var rig = new ReactionRig
            {
                Definition = definition,
                Grid = grid,
                Schedule = schedule,
                Ids = ids,
                Factory = factory,
                Movement = movement,
                Editor = editor,
                Windows = windows,
                Budget = budget,
                Adrenaline = adrenaline,
                BudgetParticipant = participant,
                Coordinator = coordinator,
                Reactions = reactions
            };

            adrenaline.ChangedSink = rig.AdrenalineChanges.Add;
            // 与生产同一装配模式：先建参与者 → 建协调器 → 依赖就绪后回填。
            participant.Bind(budget, windows, adrenaline);
            bindingParticipant.Opportunities = reactions;

            windows.RegisterControllerBinding(Player, Hero);
            windows.RegisterControllerBinding(MonsterAi, Monster);

            Assert.That(grid.RegisterUnit(Hero, new GridPoint(0, 0), GridDirection.East, Directions()),
                Is.Null, "夹具前提：hero 必须落在 (0,0)");
            Assert.That(grid.RegisterUnit(Monster, new GridPoint(0, 30), GridDirection.East, Directions()),
                Is.Null, "夹具前提：monster 必须落在 (0,30)");

            // 防御者开局持有 5 点肾上腺素：足够支付一次 Block（2），且退款与否可区分。
            adrenaline.Register(Hero, initialAvailable: 5, initialCycleId: 0L);
            adrenaline.Register(Monster, initialAvailable: 0, initialCycleId: 0L);
            return rig;
        }

        private sealed class AcceptedReaction
        {
            public ActionPlan SourceAttack;
            public ActionPlan Reaction;
            public ReactionOpportunityRuntime Opportunity;
        }

        /// <summary>经真实排程事务新增来源攻击（Monster → Hero）。</summary>
        private static ActionPlan AddSourceAttack(ReactionRig rig, long currentTick, long startTick)
        {
            rig.Editor.BeginBatch(currentTick, rig.Schedule.ScheduleRevision);
            ScheduleEditTransactionResult result = rig.Editor.Apply(
                new ScheduleEditOperation[]
                {
                    new AddOrdinaryPlanOperation(
                        1L, Monster, Spec(AttackSpecId), startTick,
                        default, Hero, GridDirection.East, null)
                },
                currentTick: currentTick,
                batchBaseScheduleRevision: rig.Schedule.ScheduleRevision,
                expectedScheduleRevision: rig.Schedule.ScheduleRevision);
            Assert.That(result.RejectionCode, Is.Null,
                "夹具前提：来源攻击必须经真实事务提交（" + result.RejectionCode + "）");
            Assert.That(result.AddedPlanIds.Count, Is.EqualTo(1));
            ActionPlan plan = rig.Schedule.Registry.Find(result.AddedPlanIds[0]);
            Assert.That(plan, Is.Not.Null);
            return plan;
        }

        /// <summary>
        /// 造出"真实 Locked 反应计划 + 真实肾上腺素预留"：攻击启动 → 公开机会 → 接受 Block。
        /// 全程只调用生产公共入口，不使用任何反射注册计划或伪造预留。
        /// </summary>
        private static AcceptedReaction ArrangeAcceptedBlock(ReactionRig rig, long telegraphTick)
        {
            ActionPlan attack = AddSourceAttack(rig, currentTick: 0L, startTick: telegraphTick);
            Assert.That(attack.StartTick, Is.EqualTo(telegraphTick),
                "夹具前提：空 Lane 里新增攻击的权威起点 = 请求起点");
            Assert.That(attack.ImpactTick, Is.EqualTo(telegraphTick + AttackBaseWindupTicks),
                "夹具前提：ImpactTick = StartTick + 解析前摇（= 反应 TriggerTick）");
            MarkLockedAndRunning(attack, telegraphTick);
            Assert.That(attack.IsEditable, Is.False, "夹具前提：已 Telegraph 的来源攻击不是 Editable");

            var opened = new List<ReactionOpportunityRuntime>();
            string openError = rig.Reactions.TryOpenForTelegraph(attack, telegraphTick, opened);
            Assert.That(openError, Is.Null, "夹具前提：来源攻击必须公开反应机会（" + openError + "）");
            Assert.That(opened.Count, Is.EqualTo(1), "夹具前提：恰好一个防御者候选（PrimaryTargetOnly）");
            ReactionOpportunityRuntime opportunity = opened[0];
            Assert.That(opportunity.DefenderUnitId, Is.EqualTo(Hero));

            string acceptError = rig.Reactions.TryAcceptById(
                opportunity.Id, Spec(BlockSpecId), telegraphTick, null, out ActionPlan reaction);
            Assert.That(acceptError, Is.Null, "夹具前提：Block 接受事务必须成功（" + acceptError + "）");
            Assert.That(reaction, Is.Not.Null);
            Assert.That(reaction.IsReaction, Is.True, "夹具前提：接受事务必须产出反应计划");
            Assert.That(rig.Schedule.LaneOfPlan(reaction.ActionPlanId), Is.Not.Null,
                "夹具前提：已接受的反应计划必须在 Lane 中（否则无法证明它随后离开 Lane）");

            AdrenalineLedger hero = rig.Adrenaline.Find(Hero);
            Assert.That(hero.ReservationOf(reaction.ActionPlanId), Is.Not.Null,
                "夹具前提：接受事务必须建立真实肾上腺素预留");
            Assert.That(hero.ReservationOf(reaction.ActionPlanId).ReservedAmount, Is.EqualTo(BlockCost));
            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(5 - BlockCost));
            Assert.That(hero.ReservedAdrenaline, Is.EqualTo(BlockCost));

            return new AcceptedReaction { SourceAttack = attack, Reaction = reaction, Opportunity = opportunity };
        }

        private static int CountAdrenalineEvents(
            ReactionRig rig, AdrenalineChangeKind kind, ActionPlanId planId)
        {
            int count = 0;
            for (int i = 0; i < rig.AdrenalineChanges.Count; i++)
            {
                AdrenalineLedgerChange change = rig.AdrenalineChanges[i];
                if (change.ChangeKind != kind) continue;
                if (!change.ReservationPlanId.HasValue || change.ReservationPlanId.Value != planId) continue;
                count++;
            }
            return count;
        }

        private static AdrenalineLedgerChange LastAdrenalineEvent(ReactionRig rig)
            => rig.AdrenalineChanges[rig.AdrenalineChanges.Count - 1];

        // =====================================================================
        // 反射点说明（本文件唯一的非公共访问，共一处：两个生命周期标记的写入面）
        //
        // ActionPlan.State / ActionPlan.LockedAtTick 的 setter 是 internal，生产路径里唯一写它们的
        // 位置是 BattleSimulation.CommitStart（启动门禁的原子提交收尾）。ProjectHero.Logic.Tests
        // **没有** InternalsVisibleTo（见 Assets/Scripts/Logic/AssemblyInfo.cs：该授权只给
        // ProjectHero.Authoring.Tests），而"来源攻击必须已经 Locked/Running"是
        // ReactionOpportunitySystem.TryOpenForTelegraph 的硬前提。
        //
        // 这里与 Task07BudgetTransactionTests / Task07ForcedDisplacementBudgetTests 采用**同一取舍**：
        // 只写这两个生命周期标记（不复制、不绕过任何生产校验，也不触碰任何资源事实）——
        // 用例 4–6 的肾上腺素预留与释放全部由真实账本端口完成。用例 1–3 不使用本反射点。
        // =====================================================================

        private static readonly PropertyInfo StateSetter =
            typeof(ActionPlan).GetProperty(nameof(ActionPlan.State));

        private static readonly PropertyInfo LockedAtTickSetter =
            typeof(ActionPlan).GetProperty(nameof(ActionPlan.LockedAtTick));

        /// <summary>写入"启动门禁已经成功"的两个生命周期标记（见 §反射点说明）。</summary>
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
        // 用例 1（裁定 A）：
        // RemovedPlanEntersTerminalThroughCoordinatorWithCancelledByCommand
        //
        // 失败模式：把这个用例变红的（真实）回归形态有三种，都必须能让本用例失败：
        //   ① 删掉 BattleSimulation.TerminateRemovedPlans 的**调用点** ⇒ 计划永远不进入终态
        //      （IsTerminal 仍为 false、TerminationReason 仍为 None、无 ActionPlanTerminatedEvent）；
        //   ② 把 EnterTerminal 的原因换成别的值 ⇒ TerminationReason / 事件 ReasonCode 不匹配；
        //   ③ 让事务不再把计划移出 Lane ⇒ LaneOfPlan 不为 null。
        // =====================================================================
        [Test]
        public void RemovedPlanEntersTerminalThroughCoordinatorWithCancelledByCommand()
        {
            RemovalScenario scenario = ArrangeRemovedPlan();
            ActionPlan plan = scenario.Plan;
            SimRig rig = scenario.Rig;

            Assert.That(plan.IsTerminal, Is.True,
                "排程删除必须经唯一终态协调器收口（IsTerminal 仍为 false = TerminateRemovedPlans 没有生效）");
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Terminated));
            Assert.That(plan.TerminationReason, Is.EqualTo(ActionTerminationReason.CancelledByCommand));
            Assert.That(plan.TerminalTick, Is.EqualTo(scenario.RemoveTick),
                "TerminalTick 必须是提交该 Remove 事务的那个 Tick");
            Assert.That(plan.IsEditable, Is.False, "终态计划不再处于 Editable");

            Assert.That(rig.Sim.ScheduleAuthority.LaneOfPlan(plan.ActionPlanId), Is.Null,
                "排程删除必须把计划移出 Lane（编辑器的分工）");
            Assert.That(rig.Sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.Zero,
                "终态计划必须离开活动索引");

            // 生命周期事件只发一次，且原因码来自权威原因表。
            int terminatedEvents = 0;
            for (int i = 0; i < rig.Events.Count; i++)
            {
                if (!(rig.Events[i] is ActionPlanTerminatedEvent terminated)) continue;
                if (terminated.ActionPlanId != plan.ActionPlanId.Value) continue;
                terminatedEvents++;
                Assert.That(terminated.Reason, Is.EqualTo((int)ActionTerminationReason.CancelledByCommand));
                Assert.That(terminated.ReasonCode, Is.EqualTo(ActionTerminationReasons.CancelledByCommand));
                Assert.That(terminated.TerminalTick, Is.EqualTo(scenario.RemoveTick));
            }
            Assert.That(terminatedEvents, Is.EqualTo(1), "每个计划恰好一条终止事件（协调器是唯一判定点）");

            Assert.That(rig.Sim.ScheduleAuthority.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null);
            Assert.That(rig.Sim.ScheduleAuthority.ScheduleRevision, Is.EqualTo(2L),
                "Remove 也是一个成功的排程事务（Add + Remove = 2 次修订），终态本身不推进修订号");
        }

        // =====================================================================
        // 用例 2（裁定 A 的回归钉子）：
        // RemovedPlanLeavesNoActiveArtifactReference
        //
        // 这是"修复前会报 active-plan-missing-from-lane 的那条路径"：
        // ScheduleEditor 在事务里只把计划移出 Lane（并释放预留），**不**写终态；
        // 若 TerminateRemovedPlans 不存在/未被 sink 调用，则该计划仍是"活动计划但不在 Lane"，
        // 阶段 19 的只读不变量会以 LogicDefinitionException(STEP_INVARIANT_VIOLATION,
        // "active-plan-missing-from-lane plan=N") 令本次 Step **抛出**。
        //
        // 失败模式（回归敏感性）：把 Assets/Scripts/Logic/Simulation/BattleSimulation.cs 里
        // `TerminateRemovedPlans(result, tick);` 这一行注释掉，
        // 本用例会在 StepSim(...) 内直接以 STEP_INVARIANT_VIOLATION 失败——
        // 它对本用例是"先抛异常、再看断言"，因此不依赖任何断言措辞。
        // 已实测（本轮探针 A，随后已逐字节还原）：抛出的正是
        //   LogicDefinitionException: STEP_INVARIANT_VIOLATION: active-plan-missing-from-lane plan=1
        // 抛点 = BattleSimulation.BuildStepResult（阶段 19 的 1b 段只读不变量）。
        // =====================================================================
        [Test]
        public void RemovedPlanLeavesNoActiveArtifactReference()
        {
            RemovalScenario scenario = ArrangeRemovedPlan();
            ActionPlan plan = scenario.Plan;
            SimRig rig = scenario.Rig;

            // Step 正常返回（Advanced）本身就是证据：阶段 19 的只读不变量已经通过，
            // 否则 Step 会抛出 STEP_INVARIANT_VIOLATION。
            Assert.That(scenario.RemoveStep.Status, Is.EqualTo(StepStatus.Advanced));
            Assert.That(rig.Sim.LastStepExecuted(StepPhase.InvariantCheckArchiveAndOutput), Is.True,
                "Remove 的那个 Tick 必须真的走到了阶段 19（只读不变量 + 归档）");

            string failure = rig.Sim.ScheduleAuthority.CheckNoActiveArtifactReferencesTerminalPlan();
            Assert.That(failure, Is.Null,
                "裁定 A 的回归钉子：Step 返回时不得有任何活动产物引用该计划（否则=" + failure + "）");

            // 修复前正是这两条同时成立才会触发 "active-plan-missing-from-lane"。
            bool inLane = rig.Sim.ScheduleAuthority.LaneOfPlan(plan.ActionPlanId) != null;
            bool inActiveIndex = false;
            IReadOnlyList<ActionPlan> active = rig.Sim.ScheduleAuthority.Registry.ActivePlans;
            for (int i = 0; i < active.Count; i++)
            {
                if (active[i].ActionPlanId == plan.ActionPlanId) inActiveIndex = true;
            }
            Assert.That(inLane, Is.False);
            Assert.That(inActiveIndex, Is.False);
            Assert.That(plan.IsTerminal, Is.True,
                "它之所以能同时离开 Lane 与活动索引，唯一原因是被协调器置为终态");
        }

        // =====================================================================
        // 用例 3（裁定 A 的分工）：
        // RemovalReleasesReservationExactlyOnceAndEditorOwnsRelease
        //
        // 分工断言：唯一的释放事件必须来自显式排程编辑（ResourceChangeSource.ExplicitScheduleEdit），
        // 且该计划在整个场景里**没有**任何 TerminalCleanup 来源的账本事件——
        // 600 槽位的参与者只是幂等空转（账本已无该计划的预留 ⇒ held &lt;= 0 ⇒ return）。
        //
        // 失败模式：若把释放挪进终态参与者（或让参与者第二次释放），
        // ReleasedBeforeLock 计数会变成 2、或出现 TerminalCleanup 来源事件、或 Spent/Available 被改写。
        // =====================================================================
        [Test]
        public void RemovalReleasesReservationExactlyOnceAndEditorOwnsRelease()
        {
            RemovalScenario scenario = ArrangeRemovedPlan();
            ActionPlan plan = scenario.Plan;
            SimRig rig = scenario.Rig;
            TurnWindow window = rig.Window;

            // —— 账本事实：预留归零、Spent 不变、释放额回到原窗口 ——
            Assert.That(window.ReservedFor(plan.ActionPlanId), Is.Zero);
            Assert.That(window.HasReservation(plan.ActionPlanId), Is.False);
            Assert.That(window.ReservedBudgetTicks, Is.Zero, "该窗口只有这一个计划，释放后预留必须归零");
            Assert.That(window.SpentBudgetTicks, Is.EqualTo(scenario.SpentAfterAdd),
                "删除是锁定前释放，绝不是消费");
            Assert.That(window.SpentBudgetTicks, Is.Zero);
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(scenario.AvailableAfterAdd + GuardCost),
                "释放额必须恰好回到原窗口账本一次");
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(window.TotalBudgetTicks));
            Assert.That(plan.ReservedTurnBudgetTicks, Is.Zero, "被删除计划的预留投影必须归零");

            // —— 事件事实：恰好一条释放，且来源是显式排程编辑 ——
            Assert.That(CountBudgetEvents(rig, TurnBudgetChangeKind.ReleasedBeforeLock, plan.ActionPlanId),
                Is.EqualTo(1), "编辑器独占释放：恰好一次 ReleasedBeforeLock");
            Assert.That(CountBudgetEvents(rig, TurnBudgetChangeKind.ConsumedAtLock, plan.ActionPlanId), Is.Zero);
            Assert.That(CountBudgetEvents(rig, TurnBudgetChangeKind.ReservationAdjusted, plan.ActionPlanId), Is.Zero);

            int terminalCleanupEvents = CountBudgetEventsBySource(
                rig, ResourceChangeSource.TerminalCleanup, plan.ActionPlanId);
            Assert.That(terminalCleanupEvents, Is.Zero,
                "600 槽位的预算参与者必须幂等空转：不得出现第二次释放（TerminalCleanup 来源事件）");

            int releasedByEditor = 0;
            for (int i = 0; i < rig.Events.Count; i++)
            {
                if (!(rig.Events[i] is TurnBudgetChangedEvent change)) continue;
                if (change.ActionPlanId != plan.ActionPlanId.Value) continue;
                if (change.ChangeKind != TurnBudgetChangeKind.ReleasedBeforeLock) continue;
                Assert.That(change.Source, Is.EqualTo(ResourceChangeSource.ExplicitScheduleEdit));
                Assert.That(change.WindowId, Is.EqualTo(window.WindowId));
                Assert.That(change.ReservedBefore, Is.EqualTo(GuardCost));
                Assert.That(change.ReservedAfter, Is.Zero);
                releasedByEditor++;
            }
            Assert.That(releasedByEditor, Is.EqualTo(1));

            // 该计划总共恰好两条账本事件：Add 时的 Reserved + Remove 时的 ReleasedBeforeLock。
            int totalLedgerEventsForPlan = 0;
            for (int i = 0; i < rig.Events.Count; i++)
            {
                if (rig.Events[i] is TurnBudgetChangedEvent change &&
                    change.ActionPlanId == plan.ActionPlanId.Value) totalLedgerEventsForPlan++;
            }
            Assert.That(totalLedgerEventsForPlan, Is.EqualTo(2),
                "该计划的账本事件 = Reserved(Add) + ReleasedBeforeLock(Remove)，不存在第二次释放");
            Assert.That(CountBudgetEvents(rig, TurnBudgetChangeKind.Reserved, plan.ActionPlanId), Is.EqualTo(1));

            // 账本恒等式（窗口级）：Reserved + Spent + Available == Total。
            Assert.That(window.ReservedBudgetTicks + window.SpentBudgetTicks + window.AvailableBudgetTicks,
                Is.EqualTo(window.TotalBudgetTicks));

            Assert.That(rig.Sim.ScheduleAuthority.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null);
        }

        // =====================================================================
        // 用例 4（裁定 B 的回归钉子）：
        // ReactionTerminalWithoutSourceCancelDiscardsReservationWithoutRefund
        //
        // 非 SourceThreatCancelled 的终态（被控制打断 / 拥有者死亡等）⇒
        // 预留被**删除**（不留悬挂预留），但 Available **不变**（不退款），
        // 事件类别是 ReservationDiscarded（既不是 Refunded，也不是 StaleCycleReservationRemoved）。
        //
        // 失败模式（回归敏感性）：把 BudgetAndAdrenalineCleanupParticipant 的反应分支改回
        // "非来源取消什么都不做"（即注释掉 `_adrenaline?.DiscardReservation(...)`），
        // 本用例立刻变红：预留仍在账本里（ReservationOf != null、ReservedAdrenaline == BlockCost）、
        // 事件计数为 0（CountAdrenalineEvents(..., ReservationDiscarded, ...) == 1 失败）。
        // 该断言不依赖"退款"实现的细节，因此不存在"换个写法也能过"的空隙。
        // 已实测（本轮探针 B，随后已逐字节还原）：
        //   total=6 passed=5 failed=1，唯一失败就是本用例，报
        //   Expected: null / But was: <AdrenalineReservationEntry { ActionPlanId = 2, ReservedAmount = 2,
        //   ReservationCycleId = 0 }>，错误信息为"终态作废必须删除预留"。
        // =====================================================================
        [Test]
        public void ReactionTerminalWithoutSourceCancelDiscardsReservationWithoutRefund()
        {
            // 两种"非来源取消"的终态各跑一遍（被控制打断 / 拥有者死亡）。
            ActionTerminationReason[] reasons =
            {
                ActionTerminationReason.InterruptedByControl,
                ActionTerminationReason.OwnerDied
            };

            for (int r = 0; r < reasons.Length; r++)
            {
                ActionTerminationReason reason = reasons[r];
                ReactionRig rig = NewReactionRig();
                AcceptedReaction accepted = ArrangeAcceptedBlock(rig, telegraphTick: 100L);
                ActionPlan reaction = accepted.Reaction;
                AdrenalineLedger hero = rig.Adrenaline.Find(Hero);
                int availableBefore = hero.AvailableAdrenaline;

                ActionPlanTerminalOutcome outcome = rig.Coordinator.EnterTerminal(reaction, reason, 150L);

                Assert.That(outcome.EnteredTerminal, Is.True, "原因 " + reason + " 必须能进入终态");
                Assert.That(reaction.IsTerminal, Is.True);
                Assert.That(reaction.TerminationReason, Is.EqualTo(reason));
                Assert.That(reaction.TerminalTick, Is.EqualTo(150L));
                Assert.That(rig.Schedule.LaneOfPlan(reaction.ActionPlanId), Is.Null,
                    "原因 " + reason + " 的终态计划必须离开 Lane");

                // ① 预留被删除（不留悬挂预留）——这正是裁定 B 新增的入口。
                Assert.That(hero.ReservationOf(reaction.ActionPlanId), Is.Null,
                    "原因 " + reason + "：终态作废必须删除预留（否则 Step 返回时预留引用了终态计划）");
                Assert.That(hero.ReservedAdrenaline, Is.Zero);

                // ② 不退款：Available 全程不变。
                Assert.That(hero.AvailableAdrenaline, Is.EqualTo(availableBefore),
                    "原因 " + reason + "：不退款终态绝不允许返还 Available");
                Assert.That(hero.AvailableAdrenaline, Is.EqualTo(5 - BlockCost));

                // ③ 事件类别必须是 ReservationDiscarded（不是 Refunded / StaleCycleReservationRemoved）。
                Assert.That(CountAdrenalineEvents(rig, AdrenalineChangeKind.ReservationDiscarded,
                    reaction.ActionPlanId), Is.EqualTo(1),
                    "原因 " + reason + "：必须恰好一条 ReservationDiscarded");
                Assert.That(CountAdrenalineEvents(rig, AdrenalineChangeKind.Refunded,
                    reaction.ActionPlanId), Is.Zero);
                Assert.That(CountAdrenalineEvents(rig, AdrenalineChangeKind.StaleCycleReservationRemoved,
                    reaction.ActionPlanId), Is.Zero);
                Assert.That(CountAdrenalineEvents(rig, AdrenalineChangeKind.Consumed,
                    reaction.ActionPlanId), Is.Zero, "从未触发 ⇒ 不得被写成消费");

                AdrenalineLedgerChange last = LastAdrenalineEvent(rig);
                Assert.That(last.ChangeKind, Is.EqualTo(AdrenalineChangeKind.ReservationDiscarded));
                Assert.That(last.ReservationPlanId, Is.EqualTo(reaction.ActionPlanId));
                Assert.That(last.AvailableBefore, Is.EqualTo(availableBefore));
                Assert.That(last.AvailableAfter, Is.EqualTo(availableBefore),
                    "作废事件的 Available 前后值必须相等（不注入任何额度）");
                Assert.That(last.ReservationAmount, Is.EqualTo(BlockCost));

                Assert.That(rig.Schedule.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null);
            }
        }

        // =====================================================================
        // 用例 5（裁定 B 的负控制）：
        // SourceThreatCancellationStillRefundsThroughReleasePath
        //
        // 二分没有把来源取消那条路径改坏：
        //   · 当前周期 ⇒ ReleaseForSourceThreatCancelled ⇒ 返还 Available（Refunded）；
        //   · 跨过拥有者下一次窗口边界 ⇒ StaleCycleReservationRemoved 且**不**返还。
        //
        // 失败模式：若把二分的判别写反（例如一律 DiscardReservation），
        // 当前周期分支会失去 Refunded（Available 停在 3 而非回到 5），
        // 跨周期分支会变成 ReservationDiscarded 而不是 StaleCycleReservationRemoved。
        // =====================================================================
        [Test]
        public void SourceThreatCancellationStillRefundsThroughReleasePath()
        {
            // —— ① 当前周期：返还 Available ——
            ReactionRig currentCycleRig = NewReactionRig();
            AcceptedReaction currentCycle = ArrangeAcceptedBlock(currentCycleRig, telegraphTick: 100L);
            AdrenalineLedger currentHero = currentCycleRig.Adrenaline.Find(Hero);
            Assert.That(currentHero.AvailableAdrenaline, Is.EqualTo(5 - BlockCost));

            ActionPlanTerminalOutcome currentOutcome = currentCycleRig.Coordinator.EnterTerminal(
                currentCycle.Reaction, ActionTerminationReason.SourceThreatCancelled, 150L);

            Assert.That(currentOutcome.EnteredTerminal, Is.True);
            Assert.That(currentCycle.Reaction.TerminationReason,
                Is.EqualTo(ActionTerminationReason.SourceThreatCancelled));
            Assert.That(currentHero.AvailableAdrenaline, Is.EqualTo(5),
                "来源取消且预留仍属当前周期 ⇒ 必须返还 Available");
            Assert.That(currentHero.ReservedAdrenaline, Is.Zero);
            Assert.That(currentHero.ReservationOf(currentCycle.Reaction.ActionPlanId), Is.Null);
            Assert.That(CountAdrenalineEvents(currentCycleRig, AdrenalineChangeKind.Refunded,
                currentCycle.Reaction.ActionPlanId), Is.EqualTo(1));
            Assert.That(CountAdrenalineEvents(currentCycleRig, AdrenalineChangeKind.ReservationDiscarded,
                currentCycle.Reaction.ActionPlanId), Is.Zero,
                "来源取消绝不能走作废分支");
            Assert.That(CountAdrenalineEvents(currentCycleRig,
                AdrenalineChangeKind.StaleCycleReservationRemoved, currentCycle.Reaction.ActionPlanId), Is.Zero);

            // —— ② 跨周期：只删除旧周期预留，不注入新周期 ——
            ReactionRig staleCycleRig = NewReactionRig();
            AcceptedReaction staleCycle = ArrangeAcceptedBlock(staleCycleRig, telegraphTick: 100L);
            AdrenalineLedger staleHero = staleCycleRig.Adrenaline.Find(Hero);
            Assert.That(staleHero.ReservationOf(staleCycle.Reaction.ActionPlanId).ReservationCycleId,
                Is.EqualTo(0L), "夹具前提：预留属于周期 0");

            // 拥有者自己的窗口打开：周期 +1、Available 清零；既有合法预留继续存在。
            staleCycleRig.Adrenaline.BeginOwnWindow(Hero);
            Assert.That(staleHero.CycleId, Is.EqualTo(1L));
            Assert.That(staleHero.AvailableAdrenaline, Is.Zero);
            Assert.That(staleHero.ReservedAdrenaline, Is.EqualTo(BlockCost),
                "夹具前提：窗口打开不清零既有预留");

            ActionPlanTerminalOutcome staleOutcome = staleCycleRig.Coordinator.EnterTerminal(
                staleCycle.Reaction, ActionTerminationReason.SourceThreatCancelled, 150L);

            Assert.That(staleOutcome.EnteredTerminal, Is.True);
            Assert.That(staleHero.ReservationOf(staleCycle.Reaction.ActionPlanId), Is.Null);
            Assert.That(staleHero.ReservedAdrenaline, Is.Zero);
            Assert.That(staleHero.AvailableAdrenaline, Is.Zero,
                "旧周期预留不得把过期资源带入新周期");
            Assert.That(CountAdrenalineEvents(staleCycleRig,
                AdrenalineChangeKind.StaleCycleReservationRemoved, staleCycle.Reaction.ActionPlanId),
                Is.EqualTo(1));
            Assert.That(CountAdrenalineEvents(staleCycleRig, AdrenalineChangeKind.Refunded,
                staleCycle.Reaction.ActionPlanId), Is.Zero);
            Assert.That(CountAdrenalineEvents(staleCycleRig, AdrenalineChangeKind.ReservationDiscarded,
                staleCycle.Reaction.ActionPlanId), Is.Zero,
                "跨周期的来源取消仍然走释放路径（只是不返还），不是作废路径");
            Assert.That(staleCycleRig.Schedule.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null);
        }

        // =====================================================================
        // 用例 6：
        // DiscardReservationIsIdempotentAndNeverInjectsAdrenaline
        //
        // 对同一计划重复 DiscardReservation ⇒ 第二次是无操作（事件只发一次）、
        // AvailableAdrenaline 全程不变、ReservedAdrenaline 归零。
        // 追加：连"全部可用额度的唯一增长入口"也不受影响（不得靠退款/新周期注入）。
        //
        // 失败模式：若 DiscardReservation 实现成"删除 + 返还"（等于 Refunded）、
        // 或实现成"只在第一次删除、第二次仍发事件"、或清空 Available，
        // 本用例的对应断言立刻失败。
        // =====================================================================
        [Test]
        public void DiscardReservationIsIdempotentAndNeverInjectsAdrenaline()
        {
            var events = new List<AdrenalineLedgerChange>();
            var registry = new AdrenalineLedgerRegistry(AdrenalineRules.FrozenV1) { ChangedSink = events.Add };
            AdrenalineLedger hero = registry.Register(Hero, initialAvailable: 5, initialCycleId: 0L);

            ActionPlanId planId = P(7L);
            Assert.That(registry.TryReserveForReaction(Hero, planId, BlockCost), Is.Null);

            int availableAfterReserve = hero.AvailableAdrenaline;   // 3
            int eventsBeforeDiscard = events.Count;
            Assert.That(availableAfterReserve, Is.EqualTo(5 - BlockCost));
            Assert.That(hero.ReservedAdrenaline, Is.EqualTo(BlockCost));

            // —— 第一次：删除预留、不返还 ——
            registry.DiscardReservation(planId);
            Assert.That(hero.ReservationOf(planId), Is.Null);
            Assert.That(hero.ReservedAdrenaline, Is.Zero, "作废必须把预留归零（不留悬挂）");
            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(availableAfterReserve), "作废不返还 Available");
            Assert.That(events.Count, Is.EqualTo(eventsBeforeDiscard + 1));
            Assert.That(events[events.Count - 1].ChangeKind,
                Is.EqualTo(AdrenalineChangeKind.ReservationDiscarded));

            // —— 第二次：无操作（不重复发事件、不改任何数值）——
            registry.DiscardReservation(planId);
            Assert.That(events.Count, Is.EqualTo(eventsBeforeDiscard + 1),
                "重复 DiscardReservation 必须是无操作（只允许一条事件）");
            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(availableAfterReserve));
            Assert.That(hero.ReservedAdrenaline, Is.Zero);
            Assert.That(hero.ReservationOf(planId), Is.Null);

            int discards = 0;
            int nonDiscardEvents = 0;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].ChangeKind == AdrenalineChangeKind.ReservationDiscarded) discards++;
                else nonDiscardEvents++;
            }
            Assert.That(discards, Is.EqualTo(1));
            // 本场景只允许两条事件：Reserved + ReservationDiscarded。
            Assert.That(nonDiscardEvents, Is.EqualTo(1));
            Assert.That(events[0].ChangeKind, Is.EqualTo(AdrenalineChangeKind.Reserved));
            Assert.That(hero.CycleId, Is.Zero, "作废永不注入新周期");

            // 作废的资源不可被别的计划重新预留（= 确实没有回到 Available）。
            Assert.That(registry.TryReserveForReaction(Hero, P(8L), availableAfterReserve + 1),
                Is.EqualTo(AdrenalineLedgerCodes.INSUFFICIENT_ADRENALINE));

            // —— 跨过拥有者下一次窗口边界之后重复作废：仍然无操作、绝不注入 ——
            registry.BeginOwnWindow(Hero);
            Assert.That(hero.CycleId, Is.EqualTo(1L));
            Assert.That(hero.AvailableAdrenaline, Is.Zero);

            registry.DiscardReservation(planId);
            Assert.That(events.Count(e => e.ChangeKind == AdrenalineChangeKind.ReservationDiscarded),
                Is.EqualTo(1), "跨周期重复作废仍然是无操作");
            Assert.That(hero.AvailableAdrenaline, Is.Zero, "作废绝不为新周期注入任何额度");
            Assert.That(hero.ReservedAdrenaline, Is.Zero);
        }

        // =====================================================================
        // §回归敏感性（本轮实测记录；两次探针都在同一次会话内完成并逐字节还原）
        //
        // 探针 A —— 把 BattleSimulation.cs 的 `TerminateRemovedPlans(result, tick);` 调用点
        //   注掉后重跑本类：
        //     total=6 passed=3 failed=3，三条失败全部是
        //     `LogicDefinitionException: STEP_INVARIANT_VIOLATION: active-plan-missing-from-lane plan=1`
        //     抛自 BattleSimulation.BuildStepResult（阶段 19 的 1b 段）。
        //     失败用例 = RemovedPlanEntersTerminalThroughCoordinatorWithCancelledByCommand、
        //     RemovedPlanLeavesNoActiveArtifactReference、RemovalReleasesReservationExactlyOnceAndEditorOwnsRelease。
        //   还原后用 SHA-256 复核 BattleSimulation.cs 与探针前**完全一致**
        //   （6EBC703039E9F57D069E295691BFD1A9236274410CC43903B22E4ABB72E6619D）。
        //
        // 探针 B —— 把 BudgetAndAdrenalineCleanupParticipant 的反应分支改回
        //   "非来源取消什么都不做"（注掉 `_adrenaline?.DiscardReservation(...)`）后重跑本类：
        //     total=6 passed=5 failed=1，唯一失败 = ReactionTerminalWithoutSourceCancelDiscardsReservationWithoutRefund
        //     （"Expected: null / But was: AdrenalineReservationEntry { ActionPlanId = 2, ReservedAmount = 2, ... }"）；
        //     用例 5/6 仍然全绿 ⇒ 二分里"来源取消"那条路径没有被这次回退影响。
        //   还原后用 SHA-256 复核该文件与探针前**完全一致**
        //   （619D666B5E4B15F3BBE953091432E4E9B9DC56CF7A122833E3D022866055442A）。
        //
        // 结论：用例 1–3 对"sink 调用点是否存在"敏感（不是对断言措辞敏感），
        // 用例 4 对"参与者是否调用 DiscardReservation"敏感。两次探针期间本文件以外的
        // Assets/Scripts/** 均未留下改动（探针结束即以相同的 edit 反向还原 + 摘要复核）。
        // =====================================================================
    }
}
