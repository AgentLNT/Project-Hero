using System;
using System.Collections.Generic;
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
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 08 <strong>集成段第一步</strong>的仓库内自动化证据：
    /// <c>BattleSimulation</c> 的<strong>两个此前为空的接线点</strong>——
    /// 阶段 8 <c>DrainIntents</c>（物化到期攻击 Intent + 冻结全局队列）与
    /// 阶段 10 <c>BuildConflictGraphAndResolve</c>（接触候选 → 冲突图）——
    /// 在<strong>真实 <see cref="BattleSimulation"/> 装配</strong>下确实被执行。
    ///
    /// <para>
    /// <strong>为什么必须有这个文件</strong>（不是"再抄一遍单元测试"）：
    /// 文件 <c>Interactions/**</c> 的 25 条用例全部直接调用
    /// <see cref="CombatIntentFactory"/> / <see cref="GlobalIntentQueue"/> /
    /// <see cref="ConflictGraphBuilder"/> 并手工喂入值对象，
    /// 因此它们<strong>对"装配点没接线"完全不敏感</strong>——两个空壳方法体只留一行注释时，
    /// 那 25 条<strong>照样全绿</strong>。本文件是唯一让这两处接线"有牙齿"的证据：
    /// 把 <c>DrainIntents</c> 改回空实现 ⇒ 用例 1 立刻红；
    /// 把 <c>BuildConflictGraphAndResolve</c> 改回空实现 ⇒ 用例 2/3 立刻红。
    /// </para>
    ///
    /// <para>
    /// <strong>装配口径</strong>：真实 <see cref="BattleSimulation.Create"/>（唯一创建路径）
    /// + 真实命令入口 <c>Submit</c>/<c>FreezeTick</c> + 真实 <c>Step</c> 阶段链；
    /// 攻击计划经<strong>真实排程事务</strong>（<c>ScheduleEditPayload</c>）提交，
    /// 不使用任何反射注册计划、伪造状态或第二份空间权威。
    /// 定义夹具自建（沿用 <c>Task07RemovalAndDiscardTests</c> 的纯数据形态），
    /// 但它比 07 的夹具多一样东西：<strong>合法的 12 向攻击 Pattern</strong>——
    /// 07 的攻击载荷 <c>Pattern = null</c> 且 <c>AttackPatterns</c> 为空，
    /// 那是因为 07 从不物化 Intent；本文件必须物化，因此 Pattern 是必需输入
    /// （<see cref="InteractionCodes.INTENT_PATTERN_DIRECTIONS_DEGENERATE"/> 是缺它时的稳定拒绝码）。
    /// </para>
    /// </summary>
    public class Task08IntentAndConflictGraphIntegrationTests
    {
        // ================= 夹具常量 =================

        private const string AttackSpecId = "action.t08ig.attack";
        private const string ActionSetIdValue = "action_set.t08ig";
        private const string EncounterIdValue = "encounter.t08ig";
        private const string HeroSlotId = "hero";
        private const string MonsterSlotId = "monster";

        /// <summary>攻击前摇：<c>ImpactTick = StartTick + 100</c>（与 07 夹具同量级，便于对着读）。</summary>
        private const int AttackBaseWindupTicks = 100;
        private const int AttackRecoveryTicks = 10;

        /// <summary>窗口预算必须覆盖攻击的权威成本（前摇 100 + 后摇 10）。</summary>
        private const int WindowBudget = 200;

        private const long WindowOpenTick = 0L;
        private const long SubmitTick = 1L;

        /// <summary>两个单位同在一格的三角上（doubled coordinates 下相差 4 ⇒ 约 1.5 格）。</summary>
        private const long ImpactTick = SubmitTick + AttackBaseWindupTicks;

        private static readonly BattleRules Rules = BattleRules.FrozenV1;

        private static readonly GridBoundaryDefinition Wide =
            new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40));

        private static readonly UnitId Hero = new UnitId(1L);
        private static readonly UnitId Monster = new UnitId(2L);
        private static readonly FactionId HeroFaction = new FactionId("faction.hero");
        private static readonly FactionId MonsterFaction = new FactionId("faction.monster");
        private static readonly ControllerId Player = new ControllerId("controller.player");
        private static readonly ControllerId MonsterAi = new ControllerId("controller.monster_ai");

        private static ActionSpecId Spec(string id) => new ActionSpecId(id);
        private static ActionSetId ActionSet => new ActionSetId(ActionSetIdValue);
        private static EncounterDefinitionId EncounterId => new EncounterDefinitionId(EncounterIdValue);
        private static EncounterSlotId Slot(string id) => new EncounterSlotId(id);
        private static BattleRuntimeInputs Inputs => new BattleRuntimeInputs(InitialRngSeed: 7UL, InitialMetaResource: 0);

        // =====================================================================
        // 定义夹具
        // =====================================================================

        /// <summary>
        /// 单三角体积：点满足 <c>X + Y</c> 为奇数 ⇒ <c>T = ±1</c> 合法（与 07 夹具逐字同形）。
        /// </summary>
        private static IReadOnlyList<DirectionalTriangleSet> Directions()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        /// <summary>
        /// 12 向攻击 Pattern：朝向 <c>f</c> 的相对点 = 沿 <c>f</c> 的邻居平移 + <c>2j</c>（j = 0..3）。
        ///
        /// 本表由<strong>实测</strong>得到：<c>f=East</c> 时为
        /// <c>(2,1,1) (4,1,1) (6,1,1) (8,1,1)</c>——覆盖 <c>GridPoint(0,0)</c> 以东 1–3 格的中心半格。
        /// 三点足以覆盖几何夹具里那个离散的合法命中位置（见定义夹具的几何前提）。
        /// </summary>
        private static AttackPatternSpec BuildAttackPattern()
        {
            var directions = new List<DirectionalTriangleSet>(GridDirectionInfo.DirectionCount);
            for (int f = 0; f < GridDirectionInfo.DirectionCount; f++)
            {
                var facing = (GridDirection)f;
                int dx = GridNeighborTable.OffsetX(facing);
                int dy = GridNeighborTable.OffsetY(facing);
                var points = new List<TrianglePoint>(4);
                for (int j = 0; j < 4; j++)
                {
                    points.Add(new TrianglePoint(2 * j + dx, 1 + dy, 1));
                }
                directions.Add(new DirectionalTriangleSet(facing, points));
            }
            return new AttackPatternSpec(new AttackPatternId("attack.pattern.t08ig.grid"), directions);
        }

        /// <summary>
        /// 唯一一份定义。<strong>攻击载荷的 Pattern 非空是本文件与 07 夹具的关键差异</strong>：
        /// 没有它，阶段 8 的物化会以
        /// <see cref="InteractionCodes.INTENT_PATTERN_DIRECTIONS_DEGENERATE"/> 稳定拒绝，
        /// 这正是"07 夹具从未物化过 Intent"的直接推论。
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

            AttackPatternSpec pattern = BuildAttackPattern();

            var attackSpec = new ActionSpec(
                Spec(AttackSpecId), ActionType.Attack,
                new AttackTimingSpec(AttackBaseWindupTicks, AttackRecoveryTicks),
                new AttackPayloadSpec(
                    new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f,
                        DamageChannelCatalog.GetDefaultTags(DamageChannels.PhysicalBlunt)) },
                    // 冲击 Profile 必须已知：空 Id 会被动量量化器以
                    // MOMENTUM_OUT_OF_RANGE 稳定拒绝（那是"缺输入"，不是公式问题）。
                    ImpactProfileId: ImpactProfiles.Blunt,
                    ForceMultiplier: 1f,
                    TargetPolicy: TargetPolicy.PrimaryTargetOnly,
                    AllowedTargetRelations: TargetRelationMask.Hostile,
                    MomentumDirectionOffsetSteps: 0,
                    Pattern: pattern,
                    Tags: AttackTagMask.Reactable),
                AdrenalineCost: 0);

            var volume = new VolumeSpec(new VolumeSpecId("unit_volume.t08ig"), Directions());
            var actionSet = new ActionSetDefinition(
                ActionSet, new List<ActionSpecId> { attackSpec.ActionSpecId });

            var heroDefinitionId = new UnitDefinitionId("unit.t08ig.hero");
            var monsterDefinitionId = new UnitDefinitionId("unit.t08ig.monster");

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

            // 几何前提（全部由<strong>实测</strong>穷举得出，任一处都不得随手改；探针已随本步删除）：
            //   · <c>DirectionalGeometry.ExpandFromBases</c> <strong>不</strong>保留传入基点的 T：
            //     它按 12 向偏移生成一张固定表（East/T=+1、West/T=-1、NorthWest/T=+1 …）。
            //     因此"单位朝西一定占 T=+1 三角"这类直觉是错的，本夹具的坐标必须按这张表推。
            //   · <see cref="UnitOccupancy.IntersectsCanonical"/> 与
            //     <see cref="CombatIntent.AreaIntersects"/> 都是<strong>三角点精确相等</strong>
            //     （含 T），不是"格重叠"⇒ 命中位置是离散的，必须实测选定；
            //   · hero 在 (0,0) 朝东 ⇒ 三角 (3,0,1)，格 {(2,0),(3,1),(4,0)}；
            //   · 朝东的攻击区域（本 Pattern） = {(2,1,1),(4,1,1),(6,1,1),(8,1,1)}；
            //   · monster 在 (8,0) 朝 NorthWest ⇒ 三角 (6,1,1)，格 {(6,0),(7,-1),(8,0)}：
            //     与 hero 的格集合<strong>不相交</strong>（不会被 LOGIC_GRID_OCCUPIED_BY_OTHER 拒绝），
            //     而它的三角 (6,1,1) <strong>恰好等于</strong>攻击区域里的 (6,1,1) ⇒ 接触成立。
            //   · 该构型是本 Pattern 下唯一同时满足"两单位不重叠"与"体积级命中"的形状之一，
            //     下面 3 处断言（朝向 / 位置 / 格集合）就是它的回归钉子。
            var encounter = new EncounterDefinition(
                EncounterId,
                Wide,
                new List<EncounterUnitSlot>
                {
                    new EncounterUnitSlot(Slot(HeroSlotId), heroDefinitionId, HeroFaction,
                        new GridPoint(0, 0), GridDirection.East),
                    new EncounterUnitSlot(Slot(MonsterSlotId), monsterDefinitionId, MonsterFaction,
                        new GridPoint(8, 0), GridDirection.NorthWest)
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
                    "result.t08ig.victory", "result.t08ig.defeat", "result.t08ig.draw"));

            return new BattleDefinition(
                "battle-definition.task08.intent-conflictgraph",
                Rules.TicksPerSecond,
                Rules,
                ConcurrentActionDefinition.FrozenV1,
                new ReactionRules(CommandIngressLeadTicks: 2, MinimumReactionLeadTicks: 1),
                AdrenalineRules.FrozenV1,
                factionModel,
                new List<DamageChannelDefinition>(),
                new List<ImpactProfileDefinition>(),
                units,
                new List<ActionSpec> { attackSpec },
                new List<AttackPatternSpec> { pattern },
                new List<VolumeSpec> { volume },
                new List<MovementPatternSpec>(),
                new List<ActionSetDefinition> { actionSet },
                new List<StatusEffectSpec>(),
                new List<EncounterDefinition> { encounter },
                null,
                "test-definition-hash.t08.intent-conflictgraph");
        }

        // =====================================================================
        // 装配
        // =====================================================================

        private sealed class Rig
        {
            public BattleSimulation Sim;
            public CommandIngressEntry PlayerEntry;
            public TurnWindow Window;
            public ActionPlan AttackPlan;
            public readonly List<LogicEvent> Events = new List<LogicEvent>();
            public StepResult ImpactStep;
        }

        /// <summary>
        /// Tick 0 打开主角窗口 → Tick 1 经真实命令阶段新增 hero→monster 的攻击计划
        /// （<c>StartTick = 1</c> ⇒ <c>ImpactTick = 101</c>）。
        /// 之后由 <see cref="StepToImpactTick"/> 逐 Tick 推进到该 Tick。
        /// </summary>
        private static Rig Arrange()
        {
            BattleDefinition definition = BuildDefinition();
            BattleSimulation sim = BattleSimulation.Create(definition, EncounterId, Inputs);

            // 几何前提的自检（夹具不变量，不是被测行为）：命中位置是离散的，
            // 一旦这两条不成立，后面的"接触数 ≥ 1"会以一条难以定位的信息失败。
            IReadOnlyList<TrianglePoint> heroTriangles =
                sim.LogicGrid.ResolveDestinationTriangles(Hero, new GridPoint(0, 0), GridDirection.East);
            IReadOnlyList<TrianglePoint> monsterTriangles =
                sim.LogicGrid.ResolveDestinationTriangles(Monster, new GridPoint(8, 0), GridDirection.NorthWest);
            Assert.That(heroTriangles.Count, Is.EqualTo(1), "夹具前提：单三角体积表");
            Assert.That(heroTriangles[0], Is.EqualTo(new TrianglePoint(3, 0, 1)));
            Assert.That(monsterTriangles.Count, Is.EqualTo(1), "夹具前提：单三角体积表");
            Assert.That(monsterTriangles[0], Is.EqualTo(new TrianglePoint(6, 1, 1)),
                "夹具前提：monster 的三角必须等于攻击区域里的 (6,1,1)（命中位置是离散的，见几何前提）");

            CommandIngressEntry playerEntry = sim.CommandIngress.FindEntry(Player);
            Assert.That(playerEntry, Is.Not.Null, "夹具前提：ControllerBinding 必须注册出玩家命令入口");

            var rig = new Rig { Sim = sim, PlayerEntry = playerEntry };

            sim.WindowManager.ScheduleWindow(WindowOpenTick, Hero, WindowBudget);
            StepOnce(rig, WindowOpenTick);
            Assert.That(sim.CurrentTurnWindow, Is.Not.Null, "夹具前提：Tick 0 必须打开主角窗口");
            rig.Window = sim.CurrentTurnWindow;

            var request = new CommandRequest(
                SubmitTick,
                new ScheduleEditScope(sim.ScheduleAuthority.ScheduleRevision, rig.Window.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    new AddOrdinaryPlanOperation(
                        1L, Hero, Spec(AttackSpecId), SubmitTick,
                        default, Monster, GridDirection.East, null)
                }));

            StepOnce(rig, SubmitTick, request);

            IReadOnlyList<ActionPlan> active = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(active.Count, Is.EqualTo(1), "夹具前提：本 Tick 恰好新增一个活动计划");
            rig.AttackPlan = active[0];
            Assert.That(rig.AttackPlan.OwnerUnitId, Is.EqualTo(Hero));
            Assert.That(rig.AttackPlan.PrimaryTargetUnitId, Is.EqualTo(Monster));
            Assert.That(rig.AttackPlan.IsTerminal, Is.False, "夹具前提：攻击计划在物化时仍非终态");
            Assert.That(rig.AttackPlan.ImpactTick, Is.EqualTo(ImpactTick),
                "夹具前提：ImpactTick = StartTick + 解析前摇（100）");
            return rig;
        }

        private static StepResult StepOnce(Rig rig, long tick, params CommandRequest[] requests)
        {
            for (int i = 0; i < requests.Length; i++)
            {
                CommandIngressRejection rejection = rig.PlayerEntry.Submit(requests[i]);
                Assert.That(rejection, Is.Null,
                    "夹具前提：命令入口必须接受请求（" + (rejection == null ? "<null>" : rejection.ReasonCode) + "）");
            }

            FrozenCommandBatch batch = rig.Sim.CommandIngress.FreezeTick(tick);
            StepResult result = rig.Sim.Step(tick, batch);
            for (int i = 0; i < result.Events.Count; i++)
            {
                if (result.Events.Events[i] is CommandRejectedEvent rejected)
                {
                    Assert.Fail("夹具前提：命令被处理器拒绝：" + rejected.ReasonCode);
                }
                if (result.Events.Events[i] is CommandIngressRejectedEvent ingressRejected)
                {
                    Assert.Fail("夹具前提：命令被入口拒绝：" + ingressRejected.ReasonCode);
                }
                rig.Events.Add(result.Events.Events[i]);
            }
            return result;
        }

        /// <summary>推进到攻击的 <c>ImpactTick</c>，返回该 Tick 的 Step 结果。</summary>
        private static StepResult StepToImpactTick(Rig rig)
        {
            StepResult result = null;
            for (long tick = SubmitTick + 1L; tick <= ImpactTick; tick++)
            {
                result = StepOnce(rig, tick);
                Assert.That(result.IsAdvanced, Is.True,
                    "夹具前提：本夹具里战斗不得在 Tick " + tick + " 结束");
            }
            rig.ImpactStep = result;
            return result;
        }

        // =====================================================================
        // 用例 1：阶段 8 物化 + 冻结
        // =====================================================================

        /// <summary>
        /// <strong>物化发生在 ImpactTick 那一个 Tick，且队列不按窗口过滤</strong>。
        ///
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item><c>DrainIntents</c> 保持空实现 ⇒ 队列恒空（本用例直接红）；</item>
        /// <item>用"当前 <c>WindowId</c>"或 <c>SubmittedWindowId</c> 过滤 ⇒
        /// 计划的提交窗口（Tick 0）与物化时刻（Tick 101）不同，队列会空；</item>
        /// <item>把到期判定写成 <c>ImpactTick &lt;= tick</c> ⇒ 在 ImpactTick 之前的某个 Tick
        /// 就已经产出 Intent（用例 1b 的逐 Tick 断言会红）；</item>
        /// <item>物化两次（重复取号）⇒ <see cref="CombatIntentContract.ValidateProducedOnce"/>
        /// 以 <c>COMBAT_INTENT_SEQUENCE_DUPLICATE</c> / <c>COMBAT_INTENT_DUPLICATE</c> 抛出。</item>
        /// </list>
        /// </summary>
        [Test]
        public void DrainIntentsMaterializesExactlyAtImpactTickAndDoesNotFilterByWindow()
        {
            Rig rig = Arrange();

            // 1b：ImpactTick 之前的每一个 Tick 都必须没有到期 Intent（"恰好一次"的另一半）。
            for (long tick = SubmitTick + 1L; tick < ImpactTick; tick++)
            {
                StepOnce(rig, tick);
                Assert.That(rig.Sim.IntentQueue, Is.Not.Null, "阶段 8 每个 Tick 都必须冻结出一个队列对象");
                Assert.That(rig.Sim.IntentQueue.Tick, Is.EqualTo(tick));
                Assert.That(rig.Sim.IntentQueue.Count, Is.EqualTo(0),
                    "Tick " + tick + " 早于 ImpactTick(" + ImpactTick + ")：不得有任何到期 Intent");
            }

            StepOnce(rig, ImpactTick);

            FrozenIntentQueue queue = rig.Sim.IntentQueue;
            Assert.That(queue.Tick, Is.EqualTo(ImpactTick));
            Assert.That(queue.Count, Is.EqualTo(1), "ImpactTick 必须恰好产出一个攻击 Intent");
            Assert.That(queue.Intents[0].ActionPlanId, Is.EqualTo(rig.AttackPlan.ActionPlanId));
            Assert.That(queue.Intents[0].OwnerUnitId, Is.EqualTo(Hero));
            Assert.That(queue.Intents[0].ImpactTick, Is.EqualTo(ImpactTick));
            Assert.That(queue.Intents[0].IntentSequence, Is.GreaterThan(0L),
                "IntentSequence 必须来自逻辑层单调分配器（0 保留为无效值）");

            // 不按窗口过滤：计划的预算来源窗口是 Tick 0 打开的窗口，而物化发生在 Tick 101。
            // 若实现按"当前窗口 / SubmittedWindowId"过滤，本断言上面的 Count==1 就已经红了；
            // 这里把"窗口确实换过"这一夹具前提钉住，避免用例在窗口恰好未变的退化场景下变绿。
            Assert.That(rig.AttackPlan.SubmittedWindowId, Is.Not.Null, "夹具前提：普通计划必须带提交窗口");
            Assert.That(rig.AttackPlan.SubmittedWindowId.Value, Is.EqualTo(rig.Window.WindowId));
            Assert.That(rig.Window.WindowId.Value, Is.Not.EqualTo(ImpactTick),
                "夹具前提：提交窗口的 WindowId 与物化 Tick 不是同一个数（避免同值造成的假证据）");

            // 队列的冻结顺序与输入顺序无关这件事由 GlobalIntentQueue 的单元用例承担；
            // 这里只钉"物化确实发生"。
            Assert.That(queue.CanonicalText, Does.Contain("count=1"));
        }

        // =====================================================================
        // 用例 2：阶段 10 构图
        // =====================================================================

        /// <summary>
        /// <strong>冲突图真的被构建出来，且可直接观察</strong>：节点 = 冻结队列的到期 Intent，
        /// 接触 = 攻击区域 ∩ 防守者体积，组 = 该节点的连通分量。
        ///
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item><c>BuildConflictGraphAndResolve</c> 保持空实现 ⇒ <see cref="BattleSimulation.ConflictGraph"/>
        /// 恒为 <c>null</c>；</item>
        /// <item>占用投影用了空表 / 只传了旧表 ⇒ 单位集不一致（<c>INTERACTION_CONTACT_INPUT_INVALID</c>）
        /// 或接触为空 ⇒ <c>Contacts</c> 为 0；</item>
        /// <item>忘记把 <c>DodgeCommitReport.Before</c> 一起传 ⇒ 「旧格接触」证据缺失
        /// （本用例的 <c>CoveredBefore</c> 断言会红）；</item>
        /// <item>把阶段 9 的 Dodge 报告换成"当前网格状态"⇒ 仍然全在，但那是后续 Dodge 用例的判据面。</item>
        /// </list>
        /// </summary>
        [Test]
        public void BuildConflictGraphAndResolveBuildsGraphFromFrozenQueueAndDodgeSnapshot()
        {
            Rig rig = Arrange();
            StepToImpactTick(rig);

            Assert.That(rig.Sim.ConflictGraphBuildError, Is.Null,
                "夹具前提：本夹具不得触发任何构图失败码");
            ConflictGraph graph = rig.Sim.ConflictGraph;
            Assert.That(graph, Is.Not.Null, "阶段 10 必须真的构建出冲突图（空实现时恒为 null）");

            Assert.That(graph.Tick, Is.EqualTo(ImpactTick));
            Assert.That(graph.Nodes.Count, Is.EqualTo(1), "节点 = 本 Tick 冻结的到期 Intent");
            Assert.That(graph.Nodes[0].Intent.ActionPlanId, Is.EqualTo(rig.AttackPlan.ActionPlanId));
            Assert.That(graph.Nodes[0].Intent.OwnerUnitId, Is.EqualTo(Hero));

            Assert.That(graph.Contacts.Count, Is.GreaterThanOrEqualTo(1),
                "攻击区域与防守者体积相交 ⇒ 至少一条 Attack→Unit 接触");
            InteractionContact contact = graph.Contacts[0];
            Assert.That(contact.Type, Is.EqualTo(ContactType.AttackTarget),
                "monster 本 Tick 没有 Block/Dodge/Guard/Move 计划 ⇒ 接触类型是普通命中");
            // 规范键按 (UnitId, ActionPlanId) 升序落位：Hero(1) < Monster(2) ⇒ 攻击侧就是 First 侧。
            Assert.That(contact.AttackerUnitId, Is.EqualTo(Hero));
            Assert.That(contact.AttackerPlanId, Is.EqualTo(rig.AttackPlan.ActionPlanId));
            Assert.That(contact.CounterpartyUnitId, Is.EqualTo(Monster),
                "普通命中接触的对手侧 = 目标单位（该侧没有对手计划）");
            Assert.That(contact.CounterpartyPlanId.Value, Is.EqualTo(0L),
                "ActionPlanId(0) = 该侧没有计划");
            Assert.That(contact.TargetUnitId, Is.EqualTo(Monster));
            Assert.That(contact.CoveredBefore, Is.True,
                "本夹具没有任何 Dodge 提交 ⇒ 攻击区域在'提交前快照'里也必须覆盖目标（旧格证据）");
            Assert.That(contact.CoveredAfter, Is.True,
                "本夹具没有任何 Dodge 提交 ⇒ 新格也必须覆盖（两张表都由真实快照投影而来）");

            Assert.That(graph.Groups.Count, Is.EqualTo(1));
            Assert.That(graph.Groups[0].NodeCount, Is.EqualTo(1));
            Assert.That(graph.Groups[0].GroupKey, Is.EqualTo(graph.Nodes[0].IntentSequence),
                "组键 = 组内最小 IntentSequence");
            Assert.That(graph.Groups[0].TargetUnitIds.Count, Is.EqualTo(1));
            Assert.That(graph.Groups[0].TargetUnitIds[0], Is.EqualTo(Monster));

            // 图与快照可观察性：冲突图是本步唯一的新增只读产物，它必须逐 Tick 被替换
            // （不得把上一 Tick 的图留在字段上）。
            rig.Sim.Step(ImpactTick + 1L, rig.Sim.CommandIngress.FreezeTick(ImpactTick + 1L));
            Assert.That(rig.Sim.ConflictGraph, Is.Not.Null);
            Assert.That(rig.Sim.ConflictGraph.Tick, Is.EqualTo(ImpactTick + 1L),
                "阶段 10 每个 Tick 都必须重建图（不得残留上一 Tick 的图）");
            Assert.That(rig.Sim.ConflictGraph.Nodes.Count, Is.EqualTo(0),
                "攻击计划已过 ImpactTick ⇒ 下一个 Tick 没有到期 Intent，图必须为空");
        }

        // =====================================================================
        // 用例 3：占用投影的接线证据
        // =====================================================================

        /// <summary>
        /// <strong>占用投影来自阶段 9 的 Dodge 提交报告</strong>，而不是"零写重建"：
        /// 两个单位的占用表都必须非空，且覆盖到本 Tick 之前的位置。
        ///
        /// 会让它失败的实现缺陷：把 <c>UnitsBefore</c>/<c>UnitsAfter</c> 传成
        /// <c>Array.Empty</c> ⇒ <c>ValidateSameUnitSets</c> 会先以
        /// <c>INTERACTION_CONTACT_INPUT_INVALID</c> 抛出（单位集不一致），本用例不会"静默变绿"。
        /// 该抛出本身就是投影接线正确的强证据：两张表都来自同一个真实快照来源。
        /// </summary>
        [Test]
        public void OccupancyProjectionComesFromDodgeCommitReportSnapshots()
        {
            Rig rig = Arrange();
            StepToImpactTick(rig);

            DodgeCommitReport report = rig.Sim.LastDodgeCommitReport;
            Assert.That(report, Is.Not.Null);
            Assert.That(report.Tick, Is.EqualTo(ImpactTick), "阶段 9 的报告属于当前 Tick");
            Assert.That(report.Before.Units.Count, Is.EqualTo(2),
                "提交前快照必须含两个已注册单位（网格注册后不会因死亡被注销：本 Tick 无死亡）");
            Assert.That(report.After.Units.Count, Is.EqualTo(2));
            Assert.That(report.Results.Count, Is.EqualTo(0), "本夹具没有任何到期 Dodge 预留");

            // 投影点集非空：没有它，"新旧两份"就退化成两片空表（构图会以单位集不一致抛出，
            // 也就是说这条断言失败与构图失败是同一件事的两个面）。
            for (int i = 0; i < report.After.Units.Count; i++)
            {
                Assert.That(report.After.Units[i].Triangles.Count, Is.GreaterThan(0),
                    "单位 " + report.After.Units[i].UnitId.Value + " 的体积三角不得为空");
            }

            Assert.That(rig.Sim.ConflictGraph, Is.Not.Null,
                "两张占用表都来自真实快照 ⇒ 构图必须成功");
        }

        // =====================================================================
        // 用例 4：合法"无控制者槽位"（回归钉：裁定 6.4 守卫的假阳性）
        // =====================================================================

        /// <summary>
        /// 在唯一一份定义上追加一个<strong>没有任何 <c>ControllerBinding</c></strong> 的第三槽位
        /// （<c>outsider</c> ⇒ UnitId = 3）：UnitId 由初始化器按 SlotId 的 Ordinal 升序分配，
        /// <c>hero</c>(1) &lt; <c>monster</c>(2) &lt; <c>outsider</c>(3)。
        /// 位置 <c>(-8,0)</c> 朝 East ⇒ 格集合与 hero/monster 的格集合不相交（网格注册不会拒绝）。
        /// </summary>
        private static BattleDefinition DefinitionWithUnboundOutsiderSlot()
        {
            BattleDefinition baseline = BuildDefinition();
            EncounterDefinition original = baseline.Encounters[0];
            var slots = new List<EncounterUnitSlot>(original.Slots)
            {
                new EncounterUnitSlot(
                    Slot("outsider"), original.Slots[0].DefinitionId, MonsterFaction,
                    new GridPoint(-8, 0), GridDirection.East)
            };
            return baseline with
            {
                Encounters = new List<EncounterDefinition> { original with { Slots = slots } }
            };
        }

        /// <summary>
        /// <strong>定义层面的合法形态：某个出场槽位没有任何 <c>ControllerBinding</c></strong>。
        ///
        /// 这不是"没接线"：<c>EncounterDefinition</c> 的校验只拒绝悬空 / 歧义 / 重复绑定与
        /// <c>System</c> 来源，<strong>不</strong>要求每个槽位都被绑定；任务 04 的
        /// <c>Task04OutsiderVariant</c>（"目标外单位不被任何外部入口控制"）就是这种形态。
        /// 而 <see cref="UnitOccupancy.ControllerId"/> 只是审计字段——任何过滤、连边、
        /// 共享目标判定与稳定键都<strong>不得</strong>读取它（08-多方仲裁与伤害.md:62/:409；
        /// 不变量 30；裁定 6.4）⇒ 阶段 10 的占用投影必须取 <c>default</c> 而不是抛异常。
        ///
        /// <para>
        /// <strong>会让它失败的实现缺陷</strong>：把 <c>ControllerOf</c> 的缺映射分支写成
        /// "只要 <c>_controllerToUnitIds</c> 查不到就抛 <c>STEP_OCCUPANCY_CONTROLLER_MISSING</c>"
        /// ⇒ 本用例在<strong>第一个</strong> <c>Step</c> 就抛 <c>…MISSING: unit=3</c>。
        /// 这正是任务 08 把阶段 10 填实之后，
        /// <c>Task04.Task04LifecycleTests.RepeatedDeathProcessingDoesNotDuplicateLifecycleNotice</c> 与
        /// <c>Task05.Task05SimulationWiringTests.DeathLocksLaneAndTerminatesAllNonTerminalPlansInActionPlanIdOrder</c>
        /// 在 Unity EditMode 下变红的原因：两者都用 outsider 变体，
        /// 且都在<strong>第一个</strong> <c>StepNext</c>（Tick 0，早于任何死亡与注销）就抛。
        /// </para>
        ///
        /// <para>
        /// 本用例与"死亡不得重复通知"无关，因此也钉住了"该回归不是裁定 6.2 引起的"：
        /// 这里没有任何单位死亡。
        /// </para>
        /// </summary>
        [Test]
        public void EncounterSlotWithoutControllerBindingDoesNotFailOccupancyProjection()
        {
            BattleDefinition definition = DefinitionWithUnboundOutsiderSlot();
            BattleSimulation sim = BattleSimulation.Create(definition, EncounterId, Inputs);

            // 夹具前提：三个真实单位，第三个（outsider = UnitId 3）没有任何 ControllerBinding；
            // 被声明的两个槽位照旧各自注册出命令入口。
            Assert.That(sim.CurrentSnapshot.Units.Count, Is.EqualTo(3), "夹具前提：三槽位 ⇒ 三个单位");
            for (int i = 0; i < sim.CurrentSnapshot.Units.Count; i++)
            {
                Assert.That(sim.CurrentSnapshot.Units[i].UnitId, Is.EqualTo((long)(i + 1)),
                    "夹具前提：UnitId 按 SlotId 的 Ordinal 升序分配（hero=1 / monster=2 / outsider=3）");
            }
            Assert.That(sim.CommandIngress.FindEntry(Player), Is.Not.Null,
                "夹具前提：被声明的槽位必须照旧获得命令入口");
            Assert.That(sim.CommandIngress.FindEntry(MonsterAi), Is.Not.Null);

            // 判据面：无控制者单位在场时，阶段 10 必须正常执行。
            StepResult result = sim.Step(0L, sim.CommandIngress.FreezeTick(0L));
            Assert.That(result.IsAdvanced, Is.True, "无控制者槽位不得让 Step 失败");
            Assert.That(sim.LastStepExecuted(StepPhase.ConflictGraphAndResolution), Is.True,
                "阶段 10 必须真实执行（本用例的判据面就是它的占用投影）");

            // 占用投影必须<strong>包含</strong>那个无控制者单位，而不是把它静默跳过：
            // 它照样占格、照样是可以被命中的合法目标，只是没有控制者。
            DodgeCommitReport report = sim.LastDodgeCommitReport;
            Assert.That(report, Is.Not.Null);
            Assert.That(report.Before.Units.Count, Is.EqualTo(3), "提交前快照必须含三个已注册单位");
            Assert.That(report.After.Units.Count, Is.EqualTo(3));
            bool outsiderProjected = false;
            for (int i = 0; i < report.After.Units.Count; i++)
            {
                if (report.After.Units[i].UnitId.Value != 3L) continue;
                outsiderProjected = true;
                Assert.That(report.After.Units[i].Triangles.Count, Is.GreaterThan(0),
                    "无控制者单位的体积三角不得为空（它照样占格）");
            }
            Assert.That(outsiderProjected, Is.True, "outsider(3) 必须出现在占用投影里");

            // 构图本身必须成功：三个单位互不重叠 ⇒ 不得留下任何构图失败码。
            Assert.That(sim.ConflictGraphBuildError, Is.Null, "本夹具不得触发任何构图失败码");
            Assert.That(sim.ConflictGraph, Is.Not.Null, "阶段 10 必须真的构建出冲突图");
            Assert.That(sim.ConflictGraph.Tick, Is.EqualTo(0L));
            Assert.That(sim.ConflictGraph.Nodes.Count, Is.EqualTo(0), "本 Tick 没有到期 Intent");
        }

        // =====================================================================
        // 用例 5：快照契约（本 Tick 冻结队列 + 构图产物进快照与哈希）
        // =====================================================================

        /// <summary>
        /// <strong><c>BuildSnapshot</c> 真的取"本 Tick 冻结后的队列"与"本 Tick 的构图产物"</strong>，
        /// 且<strong>构造期（Tick 0）没有队列/图是合法状态</strong>（空集合，不抛）。
        ///
        /// <para>
        /// 会让它失败的实现缺陷（每条都直接对应一个可观察差异）：
        /// <list type="bullet">
        /// <item><c>BuildSnapshot</c> 仍传 <c>Array.Empty&lt;IntentSnapshot&gt;()</c> ⇒
        /// 本用例在 ImpactTick 的三条集合断言立刻红（旧三字段形态也不可能带上载荷）；</item>
        /// <item>投影改为"从当前网格/计划索引重新推导"而不是读冻结队列 ⇒
        /// 快照与 <see cref="BattleSimulation.IntentQueue"/> 分叉，逐字段比较红；</item>
        /// <item>把"没有队列"写成抛异常（例如对 <c>null</c> 直接解引用）⇒
        /// <c>BattleSimulation.Create</c> 的构造期快照就崩，第一条断言红；</item>
        /// <item>忘记在下一 Tick 替换上一 Tick 的产物 ⇒ 末尾三条清空断言红。</item>
        /// </list>
        /// </para>
        /// </summary>
        [Test]
        public void SnapshotCarriesFrozenQueueAndConflictGraphAndTickZeroStaysEmpty()
        {
            // —— 构造期（Tick 0）：没有队列、没有图 —— 空集合合法，且快照仍可哈希。
            // 刻意另起一个从未 Step 过的模拟：Arrange() 已经推进了两个 Tick，
            // 它的 CurrentSnapshot 不是构造期快照（用它会把这个用例变成"检查第 1 Tick"）。
            BattleSimulation fresh = BattleSimulation.Create(BuildDefinition(), EncounterId, Inputs);
            LogicSnapshot initial = fresh.CurrentSnapshot;
            Assert.That(initial.Tick, Is.EqualTo(0L), "构造期快照的 Tick 必须是 0");
            Assert.That(initial.Intents.Count, Is.EqualTo(0), "构造期没有冻结队列 ⇒ Intent 集合为空");
            Assert.That(initial.ConflictGroups.Count, Is.EqualTo(0), "构造期没有冲突图 ⇒ 组划分为空");
            Assert.That(initial.Contacts.Count, Is.EqualTo(0), "构造期没有冲突图 ⇒ 接触集合为空");
            Assert.That(fresh.InitialStateHash, Is.EqualTo(initial.ComputeHash()),
                "Tick 0 快照必须照旧可哈希（InitialStateHash 与之一致）");

            Rig rig = Arrange();
            StepToImpactTick(rig);

            FrozenIntentQueue queue = rig.Sim.IntentQueue;
            ConflictGraph graph = rig.Sim.ConflictGraph;
            Assert.That(queue.Count, Is.EqualTo(1), "夹具前提：ImpactTick 恰好一个到期 Intent");
            Assert.That(graph, Is.Not.Null, "夹具前提：本 Tick 构图成功");

            LogicSnapshot snapshot = rig.Sim.CurrentSnapshot;
            Assert.That(snapshot.Tick, Is.EqualTo(ImpactTick));

            // —— Intent 快照 = 冻结队列的规范化投影（逐字段对着活对象核）——
            CombatIntent live = queue.Intents[0];
            Assert.That(snapshot.Intents.Count, Is.EqualTo(1));
            IntentSnapshot intent = snapshot.Intents[0];
            Assert.That(intent.IntentSequence, Is.EqualTo(live.IntentSequence));
            Assert.That(intent.ActionPlanId, Is.EqualTo(rig.AttackPlan.ActionPlanId.Value));
            Assert.That(intent.OwnerUnitId, Is.EqualTo(Hero.Value));
            Assert.That(intent.ActionSpecId, Is.EqualTo(AttackSpecId));
            Assert.That(intent.ImpactTick, Is.EqualTo(ImpactTick));
            Assert.That(intent.InteractionPriority, Is.EqualTo(InteractionPriorities.AttackVsAttack));
            Assert.That(intent.TargetPolicy, Is.EqualTo((int)TargetPolicy.PrimaryTargetOnly));
            Assert.That(intent.PrimaryTargetUnitId, Is.EqualTo(Monster.Value));
            Assert.That(intent.AllowedTargetRelations, Is.EqualTo((int)TargetRelationMask.Hostile));
            Assert.That(intent.Tags, Is.EqualTo((int)AttackTagMask.Reactable));
            Assert.That(intent.Facing, Is.EqualTo((int)GridDirection.East));
            Assert.That(intent.SubmittedWindowId, Is.EqualTo(rig.Window.WindowId.Value),
                "提交窗口是审计字段，必须进快照（但队列/图不得按它过滤）");
            Assert.That(intent.AreaPoints.Count, Is.EqualTo(live.AreaPoints.Count),
                "区域点集必须逐个搬进快照");
            Assert.That(intent.DamageComponents.Count, Is.EqualTo(1));
            Assert.That(intent.DamageComponents[0].RawAmountBits,
                Is.EqualTo(BitConverter.SingleToInt32Bits(10f)),
                "夹具定义的伤害量是 10f；快照只按二进制位搬运它");
            Assert.That(intent.Momentum.Units, Is.EqualTo(live.Momentum.MomentumUnits));
            Assert.That(intent.Momentum.Direction, Is.EqualTo((int)live.Momentum.Direction));

            // —— 组划分与接触集合 = 构图产物的规范化投影 ——
            Assert.That(snapshot.ConflictGroups.Count, Is.EqualTo(graph.Groups.Count));
            Assert.That(snapshot.ConflictGroups[0].GroupKey, Is.EqualTo(graph.Groups[0].GroupKey));
            Assert.That(snapshot.ConflictGroups[0].EdgeCount, Is.EqualTo(graph.Groups[0].EdgeCount));
            Assert.That(snapshot.ConflictGroups[0].NodeIntentSequences.Count, Is.EqualTo(1));
            Assert.That(snapshot.ConflictGroups[0].NodeIntentSequences[0], Is.EqualTo(live.IntentSequence));
            Assert.That(snapshot.ConflictGroups[0].TargetUnitIds.Count, Is.EqualTo(1));
            Assert.That(snapshot.ConflictGroups[0].TargetUnitIds[0], Is.EqualTo(Monster.Value));

            Assert.That(snapshot.Contacts.Count, Is.EqualTo(graph.Contacts.Count));
            Assert.That(snapshot.Contacts.Count, Is.GreaterThanOrEqualTo(1));
            ContactSnapshot contact = snapshot.Contacts[0];
            ContactKey liveKey = graph.Contacts[0].Key;
            Assert.That(contact.Type, Is.EqualTo((int)liveKey.Type));
            Assert.That(contact.FirstUnitId, Is.EqualTo(liveKey.FirstUnitId.Value));
            Assert.That(contact.FirstPlanId, Is.EqualTo(liveKey.FirstPlanId.Value));
            Assert.That(contact.SecondUnitId, Is.EqualTo(liveKey.SecondUnitId.Value));
            Assert.That(contact.SecondPlanId, Is.EqualTo(liveKey.SecondPlanId.Value));
            Assert.That(contact.TargetUnitId, Is.EqualTo(liveKey.TargetUnitId.Value));

            // —— 下一 Tick 必须替换上一 Tick 的产物（不得把旧队列/旧图留在快照里）——
            rig.Sim.Step(ImpactTick + 1L, rig.Sim.CommandIngress.FreezeTick(ImpactTick + 1L));
            LogicSnapshot next = rig.Sim.CurrentSnapshot;
            Assert.That(next.Tick, Is.EqualTo(ImpactTick + 1L));
            Assert.That(next.Intents.Count, Is.EqualTo(0), "下一 Tick 没有到期 Intent ⇒ 快照必须清空");
            Assert.That(next.ConflictGroups.Count, Is.EqualTo(0));
            Assert.That(next.Contacts.Count, Is.EqualTo(0));
        }
    }
}
