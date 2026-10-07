using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic;
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
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 09 A 流（架构师）的共享夹具：真实 <see cref="BattleDefinition"/> +
    /// 真实 <see cref="BattleSimulation"/> + 真实命令入口，<strong>不走任何手工喂值路径</strong>。
    ///
    /// 几何与任务 08 集成段同表（hero <c>(0,0)</c> 朝 East、monster <c>(8,0)</c> 朝 NorthWest、
    /// 前摇 10 / 后摇 2）——刻意复用，避免"每个任务一套几何"。
    ///
    /// <para>
    /// 两个 Controller 各自控制<strong>一个</strong>槽位（Player → hero、AI → monster）：
    /// 一个单位同时被两个 Controller 绑定是生产不变量错误
    /// （<c>STEP_OCCUPANCY_CONTROLLER_CONTRADICTION</c>），因此"同一单位对可以被两个来源提交"
    /// 不是本项目的合法形态。跨入口的规范顺序改用<strong>同一冻结批次内的两条真实命令</strong>
    /// （Player 的排程命令 + AI 的窗口命令）来验证。
    /// </para>
    /// </summary>
    internal static class Task09Fixture
    {
        public const string AttackSpecId = "action.t09.attack";
        public const string ActionSetIdValue = "action_set.t09";
        public const string EncounterIdValue = "encounter.t09";
        public const int AttackBaseWindupTicks = 10;
        public const int AttackRecoveryTicks = 2;
        public const int WindowBudget = 400;
        public const int InitialHealth = 200;

        /// <summary>夹具固定的窗口打开 Tick（脚本窗口计划）。</summary>
        public const long WindowOpenTick = 0L;

        public static BattleRules Rules => BattleRules.FrozenV1;

        public static UnitId Hero => new UnitId(1L);
        public static UnitId Monster => new UnitId(2L);
        public static FactionId HeroFaction => new FactionId("faction.hero");
        public static FactionId MonsterFaction => new FactionId("faction.monster");
        public static ControllerId PlayerId => new ControllerId("controller.player");
        public static ControllerId AiId => new ControllerId("controller.enemy_ai");
        public static EncounterDefinitionId EncounterId => new EncounterDefinitionId(EncounterIdValue);
        public static BattleRuntimeInputs Inputs => new BattleRuntimeInputs(InitialRngSeed: 29UL, InitialMetaResource: 0);

        public static GridPoint HeroAnchor => new GridPoint(0, 0);
        public static GridPoint MonsterAnchor => new GridPoint(8, 0);

        private static GridBoundaryDefinition Wide
            => new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40));

        private static ActionSpecId Spec(string id) => new ActionSpecId(id);
        private static ActionSetId ActionSet => new ActionSetId(ActionSetIdValue);
        private static EncounterSlotId Slot(string id) => new EncounterSlotId(id);

        // ================= 脚本窗口计划 / 命令前胜负判据 =================

        /// <summary>脚本窗口计划（唯一入口 <see cref="ITurnWindowSchedule"/>）。</summary>
        public sealed class ScriptedWindowSchedule : ITurnWindowSchedule
        {
            private readonly Dictionary<long, WindowOpenRequest> _opens = new Dictionary<long, WindowOpenRequest>();
            private readonly HashSet<long> _closes = new HashSet<long>();

            public ScriptedWindowSchedule Open(long tick, UnitId owner, int budget)
            {
                _opens[tick] = new WindowOpenRequest(owner, budget);
                return this;
            }

            public ScriptedWindowSchedule Close(long tick)
            {
                _closes.Add(tick);
                return this;
            }

            public WindowOpenRequest TryOpenDue(long tick)
                => _opens.TryGetValue(tick, out WindowOpenRequest request) ? request : null;

            public bool ShouldCloseCurrentWindow(long tick) => _closes.Contains(tick);
        }

        /// <summary>在指定的第 N 次求值（0 基）返回结果码的命令前胜负判据。</summary>
        public sealed class GateAfterEvaluations : IPreCommandVictoryGate
        {
            private readonly int _fireAtEvaluation;
            private readonly string _resultCode;
            public int Evaluations;

            public GateAfterEvaluations(int fireAtEvaluation, string resultCode)
            {
                _fireAtEvaluation = fireAtEvaluation;
                _resultCode = resultCode;
            }

            public string EvaluatePreCommandResult()
            {
                int current = Evaluations;
                Evaluations = current + 1;
                return current == _fireAtEvaluation ? _resultCode : null;
            }
        }

        // ================= 定义 =================

        private static IReadOnlyList<DirectionalTriangleSet> Directions()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        private static AttackPatternSpec BuildAttackPattern()
        {
            var directions = new List<DirectionalTriangleSet>(GridDirectionInfo.DirectionCount);
            for (int f = 0; f < GridDirectionInfo.DirectionCount; f++)
            {
                var facing = (GridDirection)f;
                int dx = GridNeighborTable.OffsetX(facing);
                int dy = GridNeighborTable.OffsetY(facing);
                var points = new List<TrianglePoint>(4);
                for (int j = 0; j < 4; j++) points.Add(new TrianglePoint(2 * j + dx, 1 + dy, 1));
                directions.Add(new DirectionalTriangleSet(facing, points));
            }
            return new AttackPatternSpec(new AttackPatternId("attack.pattern.t09.grid"), directions);
        }

        public static BattleDefinition BuildDefinition()
        {
            var factionModel = new FactionModelDefinition(
                new List<FactionDefinition>
                {
                    new FactionDefinition(HeroFaction), new FactionDefinition(MonsterFaction)
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
                    ImpactProfileId: ImpactProfiles.Blunt,
                    ForceMultiplier: 1f,
                    TargetPolicy: TargetPolicy.PrimaryTargetOnly,
                    AllowedTargetRelations: TargetRelationMask.Hostile,
                    MomentumDirectionOffsetSteps: 0,
                    Pattern: pattern,
                    Tags: AttackTagMask.Reactable),
                AdrenalineCost: 0);

            var volume = new VolumeSpec(new VolumeSpecId("unit_volume.t09"), Directions());
            var actionSet = new ActionSetDefinition(
                ActionSet, new List<ActionSpecId> { attackSpec.ActionSpecId });

            var heroDefinitionId = new UnitDefinitionId("unit.t09.hero");
            var monsterDefinitionId = new UnitDefinitionId("unit.t09.monster");

            var units = new List<UnitDefinition>
            {
                new UnitDefinition(heroDefinitionId, 10f, 10f,
                    Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), InitialHealth, actionSet.ActionSetId, volume.VolumeSpecId),
                new UnitDefinition(monsterDefinitionId, 10f, 10f,
                    Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), InitialHealth, actionSet.ActionSetId, volume.VolumeSpecId)
            };

            var controllers = new List<ControllerBinding>
            {
                new ControllerBinding(PlayerId, CommandSourceKind.Player,
                    new List<EncounterSlotId> { Slot("hero") }),
                new ControllerBinding(AiId, CommandSourceKind.Ai,
                    new List<EncounterSlotId> { Slot("monster") })
            };

            var encounter = new EncounterDefinition(
                EncounterId, Wide,
                new List<EncounterUnitSlot>
                {
                    new EncounterUnitSlot(Slot("hero"), heroDefinitionId, HeroFaction,
                        HeroAnchor, GridDirection.East),
                    new EncounterUnitSlot(Slot("monster"), monsterDefinitionId, MonsterFaction,
                        MonsterAnchor, GridDirection.NorthWest)
                },
                controllers,
                new VictoryDefinition(
                    new List<FactionId> { HeroFaction }, new List<FactionId> { MonsterFaction },
                    "result.t09.victory", "result.t09.defeat", "result.t09.draw"));

            return new BattleDefinition(
                "battle-definition.task09", Rules.TicksPerSecond, Rules, ConcurrentActionDefinition.FrozenV1,
                new ReactionRules(CommandIngressLeadTicks: 1, MinimumReactionLeadTicks: 1),
                AdrenalineRules.FrozenV1, factionModel,
                new List<DamageChannelDefinition>(), new List<ImpactProfileDefinition>(),
                units, new List<ActionSpec> { attackSpec }, new List<AttackPatternSpec> { pattern },
                new List<VolumeSpec> { volume }, new List<MovementPatternSpec>(),
                new List<ActionSetDefinition> { actionSet }, new List<StatusEffectSpec>(),
                new List<EncounterDefinition> { encounter }, null,
                "test-definition-hash.t09");
        }

        // ================= 模拟与入口 =================

        public static BattleSimulation NewSim(
            ITurnWindowSchedule schedule = null,
            IPreCommandVictoryGate gate = null,
            IUnitStateAdvanceSystem unitStateAdvance = null)
            => BattleSimulation.Create(
                BuildDefinition(), EncounterId, Inputs,
                new BattleSimulationAssembly(
                    unitStateAdvance: unitStateAdvance,
                    turnWindowSchedule: schedule,
                    preCommandVictoryGate: gate));

        public static CommandIngressEntry PlayerEntry(BattleSimulation sim)
        {
            CommandIngressEntry entry = sim.CommandIngress.FindEntry(PlayerId);
            Assert.That(entry, Is.Not.Null, "夹具前提：Player ControllerBinding 必须注册出命令入口");
            return entry;
        }

        public static CommandIngressEntry AiEntry(BattleSimulation sim)
        {
            CommandIngressEntry entry = sim.CommandIngress.FindEntry(AiId);
            Assert.That(entry, Is.Not.Null, "夹具前提：AI ControllerBinding 必须注册出命令入口");
            return entry;
        }

        /// <summary>推进到下一个 Tick（批次由本场注册表冻结）。</summary>
        public static StepResult StepNext(BattleSimulation sim)
        {
            long tick = sim.Tick + 1L;
            return sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
        }

        /// <summary>推进到下一个 Tick，并在冻结前经 Player 入口提交一批请求。</summary>
        public static StepResult StepNext(BattleSimulation sim, params CommandRequest[] requests)
        {
            CommandIngressEntry player = PlayerEntry(sim);
            for (int i = 0; i < requests.Length; i++)
            {
                Assert.That(player.Submit(requests[i]), Is.Null, "夹具前提：命令入口必须接受请求");
            }
            return StepNext(sim);
        }

        // ================= 载荷工厂 =================

        public static WindowCommandPayload CloseWindowPayload()
            => new WindowCommandPayload(WindowCommandKind.CloseOwnWindow);

        public static CommandRequest CloseWindow(long targetTick, WindowId windowId)
            => new CommandRequest(targetTick, new WindowCommandScope(windowId), CloseWindowPayload());

        /// <summary>对 <see cref="Hero"/> 的普通攻击 Add（PrimaryTargetOnly ⇒ 目标必填）。</summary>
        public static AddOrdinaryPlanOperation AddHeroAttack(
            long temporaryKey, long? requestedStartTick, ActionPlanId anchor = default,
            UnitId? target = null, WindowId? submittedWindow = null)
            => new AddOrdinaryPlanOperation(
                temporaryKey, Hero, Spec(AttackSpecId), requestedStartTick, anchor,
                target ?? Monster, GridDirection.East, null);

        public static CommandRequest ScheduleAdd(
            long targetTick, long? requestedStartTick, long expectedRevision, WindowId? window,
            long temporaryKey = 1L, ActionPlanId anchor = default)
            => new CommandRequest(
                targetTick, new ScheduleEditScope(expectedRevision, window),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    AddHeroAttack(temporaryKey, requestedStartTick, anchor)
                }));

        public static CommandRequest ScheduleEdit(
            long targetTick, long expectedRevision, WindowId? window, params ScheduleEditOperation[] operations)
            => new CommandRequest(
                targetTick, new ScheduleEditScope(expectedRevision, window),
                new ScheduleEditPayload(operations));

        public static ScheduleEditScope ScheduleScope(long expectedRevision, WindowId? window)
            => new ScheduleEditScope(expectedRevision, window);
    }

    /// <summary>
    /// 任务 09 A 流：<strong>命令入口与时间桶</strong>（任务包「必须产出」2/5/12/13）。
    ///
    /// 逐条对应必需测试：<c>DeviceInputTargetsNextTick</c>、
    /// <c>StepReceivesOnlyFrozenCurrentTickBatch</c>、<c>LateCommandProducesStableRejection</c>、
    /// <c>InputAfterFreezeTargetsNPlusTwoAndCannotEditNPlusOnePlan</c>、
    /// <c>DuplicateProducerOrdinalRejectsEntireGroup</c>、
    /// <c>CommandSequenceMatchesAcrossFreshTickZeroRuns</c>、
    /// <c>CommandAfterBattleEndIsRejectedBeforeTickBucketAndReplayLog</c>、
    /// <c>FrozenRequestsAreRejectedWhenBattleEndsBeforeCommandPhase</c>、
    /// <c>BattleEndFinalizerClearsFutureRequestBuckets</c>。
    ///
    /// <para>
    /// <strong>反射接缝（唯一一处）</strong>：<c>CommandIngressEntry.InjectRecordedFact</c> 是
    /// <c>internal</c>（"生产者可见 API 中不存在可填写 ProducerOrdinal 的入口"是编译期事实），
    /// 而 <c>ProjectHero.Logic.Tests</c> 没有 <c>InternalsVisibleTo</c>（见
    /// <c>Assets/Scripts/Logic/AssemblyInfo.cs</c>）。因此"重复 ProducerOrdinal 碰撞组"这一条
    /// 只能经反射注入记录事实，与 <c>Task08ForcedDisplacementBatchTests</c> 的既有做法一致。
    /// 被测实现<strong>没有</strong>任何放宽：注入的仍是回放记录的 DTO 事实，网关照样重新校验。
    /// </para>
    /// </summary>
    public class Task09CommandIngressTests
    {
        private static readonly MethodInfo InjectOrdinalMethod = typeof(CommandIngressEntry).GetMethod(
            "InjectRecordedFact", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(long), typeof(CommandRequest) }, null);

        private static CommandIngressRejection InjectRecordedFact(
            CommandIngressEntry entry, long producerOrdinal, CommandRequest request)
        {
            Assert.That(InjectOrdinalMethod, Is.Not.Null,
                "CommandIngressEntry.InjectRecordedFact(long, CommandRequest) 必须存在（回放驱动需要它）");
            Assert.That(InjectOrdinalMethod.IsPublic, Is.False,
                "InjectRecordedFact 不得是 public：生产者可见 API 中不存在可填写 ProducerOrdinal 的入口");
            return (CommandIngressRejection)InjectOrdinalMethod.Invoke(
                entry, new object[] { producerOrdinal, request });
        }

        private static List<T> EventsOf<T>(StepResult result) where T : LogicEvent
            => result.Events.Events.OfType<T>().ToList();

        private static long[] FutureBucketTicks(BattleSimulation sim)
            => sim.CommandIngress.CaptureSnapshot().FutureBuckets.Select(b => b.TargetTick).ToArray();

        // =====================================================================
        // 必需测试 1：设备输入默认投递 CurrentTick + 1
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>默认投递回落到"当前已经冻结过的 Tick"（本 Tick 被静默修改）⇒ 首段与第二段桶断言红；</item>
        /// <item>默认投递使用 <c>TargetTick + 2</c> 或任意常数 ⇒ 每段的目标 Tick 断言红；</item>
        /// <item>入口在冻结之后仍让默认投递落回 N+1 ⇒ 第二段红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void DeviceInputTargetsNextTick()
        {
            var sim = Task09Fixture.NewSim();
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);

            // 开局：尚未推进任何 Tick ⇒ 当前 Tick = -1，默认投递 = 0。
            Assert.That(sim.CommandIngress.CurrentTick, Is.EqualTo(-1L));
            Assert.That(sim.CommandIngress.NextDefaultTargetTick, Is.EqualTo(0L));
            Assert.That(CommandIngressRegistry.CommandIngressLeadTicks, Is.EqualTo(1));

            Assert.That(player.SubmitAtDefaultTick(
                new WindowCommandScope(new WindowId(1L)), Task09Fixture.CloseWindowPayload()), Is.Null);
            Assert.That(FutureBucketTicks(sim), Is.EqualTo(new[] { 0L }),
                "设备输入默认投递到 CurrentTick + 1 = 0");

            // 完成 Tick 0 ⇒ 默认投递 = 1。
            Task09Fixture.StepNext(sim);
            Assert.That(sim.CommandIngress.CurrentTick, Is.EqualTo(0L));
            Assert.That(sim.CommandIngress.NextDefaultTargetTick, Is.EqualTo(1L));
            Assert.That(player.SubmitAtDefaultTick(
                new WindowCommandScope(new WindowId(1L)), Task09Fixture.CloseWindowPayload()), Is.Null);
            Assert.That(FutureBucketTicks(sim), Is.EqualTo(new[] { 1L }));

            // 完成 Tick 1 ⇒ 默认投递 = 2；且默认投递的请求真的出现在 Tick 2 的冻结批次里。
            Task09Fixture.StepNext(sim);
            Assert.That(sim.CommandIngress.NextDefaultTargetTick, Is.EqualTo(2L));
            Assert.That(player.SubmitAtDefaultTick(
                new WindowCommandScope(new WindowId(1L)), Task09Fixture.CloseWindowPayload()), Is.Null);

            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(2L);
            Assert.That(batch.TargetTick, Is.EqualTo(2L));
            Assert.That(batch.Count, Is.EqualTo(1));
            Assert.That(batch.Requests[0].Request.TargetTick, Is.EqualTo(2L),
                "默认投递的请求必须带着 CurrentTick + 1 进入冻结批次");
            Assert.That(batch.Requests[0].ProducerOrdinal, Is.EqualTo(3L),
                "每入口严格递增的 ProducerOrdinal");
        }

        // =====================================================================
        // 必需测试 2：Step 只收到当前 Tick 的不透明冻结批次
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>冻结批次按"全部未处理请求"而不是"目标 Tick 分桶"构造 ⇒ 计数断言红；</item>
        /// <item><c>Step</c> 不校验 <c>RegistryStamp</c> ⇒ 伪造批次断言红；</item>
        /// <item><c>FrozenCommandBatch</c> / <c>CommandEnvelope</c> 出现 public 构造器 ⇒ 反射断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void StepReceivesOnlyFrozenCurrentTickBatch()
        {
            var sim = Task09Fixture.NewSim();
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);

            player.Submit(Task09Fixture.CloseWindow(0L, new WindowId(1L)));
            player.Submit(Task09Fixture.CloseWindow(1L, new WindowId(1L)));
            player.Submit(Task09Fixture.CloseWindow(2L, new WindowId(1L)));

            FrozenCommandBatch batch0 = sim.CommandIngress.FreezeTick(0L);
            Assert.That(batch0.TargetTick, Is.EqualTo(0L));
            Assert.That(batch0.Count, Is.EqualTo(1), "冻结批次只含本 Tick 的请求");
            Assert.That(batch0.Requests[0].Request.TargetTick, Is.EqualTo(0L));

            StepResult result = sim.Step(0L, batch0);
            Assert.That(result.Status, Is.EqualTo(StepStatus.Advanced));
            Assert.That(sim.LastCommandSet.Tick, Is.EqualTo(0L));
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(1),
                "阶段 5/6 只看到本 Tick 冻结批次里的命令");
            Assert.That(sim.LastCommandSet.Envelopes[0].TargetTick, Is.EqualTo(0L));

            // 剩余两个桶仍未被动过。
            Assert.That(FutureBucketTicks(sim), Is.EqualTo(new[] { 1L, 2L }));

            // 批次来自别的注册表 ⇒ 稳定拒绝（驱动器无法伪造来源事实）。
            var foreignSim = Task09Fixture.NewSim();
            FrozenCommandBatch foreign = new CommandIngressRegistry().FreezeTick(0L);
            var notFromRegistry = Assert.Throws<LogicDefinitionException>(
                () => foreignSim.Step(0L, foreign));
            Assert.That(notFromRegistry.ErrorCode, Is.EqualTo(SimulationCodes.STEP_BATCH_NOT_FROM_REGISTRY));
            Assert.That(foreignSim.Tick, Is.EqualTo(-1L), "被拒绝的调用不得推进 Tick");

            // 同一注册表但目标 Tick 不匹配 ⇒ 稳定拒绝。
            var mismatchSim = Task09Fixture.NewSim();
            FrozenCommandBatch batchThree = mismatchSim.CommandIngress.FreezeTick(3L);
            var mismatched = Assert.Throws<LogicDefinitionException>(
                () => mismatchSim.Step(0L, batchThree));
            Assert.That(mismatched.ErrorCode, Is.EqualTo(CommandCodes.COMMAND_BATCH_TICK_MISMATCH));

            // null 批次同样稳定拒绝（不静默当作空批次）。
            var nullBatch = Assert.Throws<LogicDefinitionException>(() => mismatchSim.Step(0L, null));
            Assert.That(nullBatch.ErrorCode, Is.EqualTo(CommandCodes.COMMAND_BATCH_TICK_MISMATCH));

            // 不透明性：可信类型都不可被生产者构造，印记也不是 public。
            foreach (Type trusted in new[]
                     {
                         typeof(SourcedCommandRequest), typeof(FrozenCommandBatch), typeof(CommandEnvelope)
                     })
            {
                Assert.That(trusted.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty,
                    trusted.Name + " 不得有 public 构造器");
            }
            Assert.That(typeof(FrozenCommandBatch)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Any(p => p.Name.Contains("RegistryStamp")), Is.False,
                "RegistryStamp 不得出现在 public 面上");
        }

        // =====================================================================
        // 必需测试 3：迟到命令产生稳定拒绝
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>迟到请求被静默丢弃（没有拒绝事件）⇒ 事件断言红；</item>
        /// <item>迟到请求被静默修正到下一 Tick ⇒ 桶断言红；</item>
        /// <item>迟到请求消耗 ProducerOrdinal 或 CommandSequence ⇒ 水位/序号断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void LateCommandProducesStableRejection()
        {
            var sim = Task09Fixture.NewSim();
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);

            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(0L);

            CommandIngressRejection late = player.Submit(
                Task09Fixture.CloseWindow(0L, new WindowId(1L)));
            Assert.That(late, Is.Not.Null);
            Assert.That(late.ReasonCode, Is.EqualTo(CommandCodes.LATE_REQUEST_FOR_FROZEN_TICK));
            Assert.That(late.ProducerOrdinal, Is.EqualTo(0L), "迟到请求不得获得本批序号");
            Assert.That(player.NextProducerOrdinal, Is.EqualTo(1L), "被拒绝的迟到请求不消耗序号");

            StepResult result = sim.Step(0L, batch);

            List<CommandIngressRejectedEvent> ingressRejections =
                EventsOf<CommandIngressRejectedEvent>(result);
            Assert.That(ingressRejections.Count, Is.EqualTo(1));
            Assert.That(ingressRejections[0].ReasonCode, Is.EqualTo(CommandCodes.LATE_REQUEST_FOR_FROZEN_TICK));
            Assert.That(ingressRejections[0].ProducerOrdinal, Is.EqualTo(0L));
            Assert.That(result.Snapshot.NextCommandSequence, Is.EqualTo(1L),
                "迟到请求不得消耗 CommandSequence");

            // 稳定：同一迟到场景重复构造出完全相同的拒绝事实。
            var repeated = Task09Fixture.NewSim();
            CommandIngressEntry repeatedPlayer = Task09Fixture.PlayerEntry(repeated);
            FrozenCommandBatch repeatedBatch = repeated.CommandIngress.FreezeTick(0L);
            CommandIngressRejection repeatedLate = repeatedPlayer.Submit(
                Task09Fixture.CloseWindow(0L, new WindowId(1L)));
            StepResult repeatedResult = repeated.Step(0L, repeatedBatch);
            Assert.That(repeatedLate.ReasonCode, Is.EqualTo(late.ReasonCode));
            Assert.That(repeatedLate.GroupKey, Is.EqualTo(late.GroupKey));
            Assert.That(repeatedResult.SnapshotHashHex, Is.EqualTo(result.SnapshotHashHex),
                "迟到拒绝必须是稳定事实，且进入规范化快照与哈希");
        }

        // =====================================================================
        // 必需测试 5：批次冻结后的输入只能目标 N+2，且改不动 N+1 的计划
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>冻结后默认投递仍指向 N+1 ⇒ 目标 Tick 断言红；</item>
        /// <item>已锁定计划仍可被排程编辑（缺 <c>IsEditable</c> 判定）⇒ 拒绝码断言红；</item>
        /// <item>编辑把锁定计划强行改回 Editable ⇒ 计划状态断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void InputAfterFreezeTargetsNPlusTwoAndCannotEditNPlusOnePlan()
        {
            var schedule = new Task09Fixture.ScriptedWindowSchedule()
                .Open(Task09Fixture.WindowOpenTick, Task09Fixture.Hero, Task09Fixture.WindowBudget);
            var sim = Task09Fixture.NewSim(schedule);
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);

            StepResult atZero = Task09Fixture.StepNext(sim, Task09Fixture.ScheduleAdd(1L, 1L, 0L, new WindowId(1L)));
            Assert.That(atZero.Status, Is.EqualTo(StepStatus.Advanced));
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(0), "Tick 0 没有目标本 Tick 的命令");
            Assert.That(FutureBucketTicks(sim), Is.EqualTo(new[] { 1L }));

            // Tick 1 冻结之后：默认投递只能目标 2。
            FrozenCommandBatch batch1 = sim.CommandIngress.FreezeTick(1L);
            Assert.That(batch1.Count, Is.EqualTo(1));
            Assert.That(sim.CommandIngress.NextDefaultTargetTick, Is.EqualTo(2L));
            Assert.That(player.SubmitAtDefaultTick(
                new WindowCommandScope(new WindowId(1L)), Task09Fixture.CloseWindowPayload()), Is.Null);
            Assert.That(FutureBucketTicks(sim), Is.EqualTo(new[] { 2L }),
                "批次冻结后的输入只能目标 N+2");

            StepResult atOne = sim.Step(1L, batch1);
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(1),
                "目标 Tick = 1 的排程编辑在 Tick 1 的冻结批次里被处理");
            Assert.That(EventsOf<CommandRejectedEvent>(atOne).Count, Is.EqualTo(0));
            IReadOnlyList<ActionPlan> plans = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(plans.Count, Is.EqualTo(1));
            ActionPlan plan = plans[0];
            Assert.That(plan.StartTick, Is.EqualTo(1L));
            Assert.That(plan.IsEditable, Is.False, "Tick 1 的启动门禁已经把它锁进 Running");
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Running));
            Assert.That(plan.LockedAtTick, Is.EqualTo(1L));
            Assert.That(atOne.Snapshot.Plans.Count, Is.EqualTo(1));

            // Tick 2：试图移动这个已经锁定的计划 ⇒ 稳定拒绝，零局部写入。
            // 处理器级拒绝以 CommandRejectedEvent 公开（不是授权层拒绝集合）。
            StepResult atTwo = Task09Fixture.StepNext(sim,
                Task09Fixture.ScheduleEdit(2L, 1L, new WindowId(1L),
                    new MoveEditablePlanOperation(plan.ActionPlanId, 5L)));

            List<CommandRejectedEvent> moveRejections = EventsOf<CommandRejectedEvent>(atTwo);
            Assert.That(moveRejections.Count, Is.EqualTo(1),
                "已经被启动门禁锁定的计划不得被排程编辑");
            Assert.That(moveRejections[0].ReasonCode,
                Is.EqualTo(ScheduleCodes.SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED));
            Assert.That(plan.StartTick, Is.EqualTo(1L), "被拒绝的编辑必须零局部写入");
            Assert.That(plan.IsEditable, Is.False);
        }

        // =====================================================================
        // 必需测试 12：重复 ProducerOrdinal 整组拒绝
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>碰撞组里"先枚举者"获胜 ⇒ 组拒绝与"没有赢家"断言红；</item>
        /// <item>按原始枚举顺序选择赢家 ⇒ 反向排列的哈希比较红；</item>
        /// <item>碰撞组消耗 CommandSequence ⇒ 序号断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void DuplicateProducerOrdinalRejectsEntireGroup()
        {
            string first = RunDuplicateOrdinalGroup(scheduleAddFirst: true, out long firstSequence);
            string second = RunDuplicateOrdinalGroup(scheduleAddFirst: false, out long secondSequence);

            Assert.That(first, Is.EqualTo(second),
                "碰撞组的原始排列不得改变拒绝事实、事件顺序或规范化快照哈希");
            Assert.That(firstSequence, Is.EqualTo(1L), "碰撞组不得伪造 CommandSequence");
            Assert.That(secondSequence, Is.EqualTo(1L));
        }

        private static string RunDuplicateOrdinalGroup(bool scheduleAddFirst, out long nextCommandSequence)
        {
            var sim = Task09Fixture.NewSim();
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);

            CommandRequest add = Task09Fixture.ScheduleAdd(0L, 0L, 0L, null);
            CommandRequest close = Task09Fixture.CloseWindow(0L, new WindowId(1L));

            // 两条"已记录事实"携带同一 ProducerOrdinal ⇒ 同一规范键碰撞。
            if (scheduleAddFirst)
            {
                InjectRecordedFact(player, 3L, add);
                InjectRecordedFact(player, 3L, close);
            }
            else
            {
                InjectRecordedFact(player, 3L, close);
                InjectRecordedFact(player, 3L, add);
            }

            StepResult result = Task09Fixture.StepNext(sim);
            nextCommandSequence = result.Snapshot.NextCommandSequence;

            List<CommandIngressRejectedEvent> rejections = EventsOf<CommandIngressRejectedEvent>(result);
            Assert.That(rejections.Count, Is.EqualTo(1), "每个非法规范键只产生一个拒绝事件");
            Assert.That(rejections[0].ReasonCode, Is.EqualTo(CommandCodes.DUPLICATE_COMMAND_ORDINAL));
            Assert.That(rejections[0].CollisionCount, Is.EqualTo(2), "整个碰撞组被拒绝");
            Assert.That(rejections[0].ProducerOrdinal, Is.EqualTo(3L));
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(0), "碰撞组没有赢家");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.EqualTo(0));

            return string.Join("|", result.Events.Events.Select(e => e.GetType().Name)) +
                   "#" + result.SnapshotHashHex;
        }

        // =====================================================================
        // 必需测试 13：Tick 0 重演序号一致
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>CommandSequence 来自非确定性来源（字典/哈希/时间/随机）⇒ 两次运行的序号序列不同；</item>
        /// <item>拒绝也会消耗序号（或相反：被拒绝的键也推进计数器）⇒ 序号或快照哈希红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void CommandSequenceMatchesAcrossFreshTickZeroRuns()
        {
            string first = RunTickZeroReplay(out long[] firstSequences);
            string second = RunTickZeroReplay(out long[] secondSequences);

            Assert.That(firstSequences, Is.EqualTo(new[] { 1L, 2L, 3L, 4L }),
                "CommandSequence 整场唯一、单调、不复用；Tick 0 重演必须一致");
            Assert.That(secondSequences, Is.EqualTo(firstSequences));
            Assert.That(second, Is.EqualTo(first), "两次全新 Tick 0 运行的规范化快照哈希必须一致");
        }

        private static string RunTickZeroReplay(out long[] commandSequences)
        {
            var schedule = new Task09Fixture.ScriptedWindowSchedule()
                .Open(Task09Fixture.WindowOpenTick, Task09Fixture.Hero, Task09Fixture.WindowBudget);
            var sim = Task09Fixture.NewSim(schedule);
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);
            var observed = new List<long>();

            for (long tick = 0L; tick < 4L; tick++)
            {
                Assert.That(player.Submit(
                    Task09Fixture.ScheduleAdd(tick, tick + 4L, tick, new WindowId(1L), tick + 1L)), Is.Null);

                StepResult result = Task09Fixture.StepNext(sim);
                Assert.That(sim.LastCommandSet.RejectedCommands.Count, Is.EqualTo(0),
                    "夹具前提：本用例的每条命令都必须被处理器接受");
                foreach (CommandEnvelope envelope in sim.LastCommandSet.Envelopes)
                    observed.Add(envelope.CommandSequence);
                Assert.That(result.Snapshot.ScheduleRevision, Is.EqualTo(tick + 1L));
            }

            commandSequences = observed.ToArray();
            return sim.CurrentSnapshot.ComputeHashHex();
        }

        // =====================================================================
        // 必需测试 14：战斗结束后的命令在入口被拒绝（不进桶、不分配序号、不进回放）
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>结束后入口仍把请求塞进 Tick 桶（等待一个永不到来的 Step）⇒ 桶断言红；</item>
        /// <item>结束后仍分配 ProducerOrdinal / CommandSequence ⇒ 水位与序号断言红；</item>
        /// <item>被拒绝的请求进入"最近一次冻结批次的 Player 事实"⇒ 回放命令流断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void CommandAfterBattleEndIsRejectedBeforeTickBucketAndReplayLog()
        {
            var sim = Task09Fixture.NewSim();
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);

            Task09Fixture.StepNext(sim, Task09Fixture.CloseWindow(0L, new WindowId(1L)));
            long sequenceAfterTickZero = sim.CurrentSnapshot.NextCommandSequence;

            sim.RequestStop();
            StepResult ended = Task09Fixture.StepNext(sim);
            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded));
            Assert.That(sim.CommandIngress.CurrentTick, Is.EqualTo(1L));

            int frozenPlayerFactsBefore = sim.CommandIngress.LastFrozenPlayerFacts.Count;
            long ordinalBefore = player.NextProducerOrdinal;
            long defaultTickAfterEnd = sim.CommandIngress.NextDefaultTargetTick;

            CommandIngressRejection rejected = player.Submit(
                Task09Fixture.CloseWindow(defaultTickAfterEnd, new WindowId(1L)));
            Assert.That(rejected, Is.Not.Null, "结束后的命令必须在入口立即返回稳定拒绝");
            Assert.That(rejected.ReasonCode, Is.EqualTo(CommandCodes.BATTLE_ALREADY_ENDED));
            Assert.That(rejected.ProducerOrdinal, Is.EqualTo(0L));
            Assert.That(player.NextProducerOrdinal, Is.EqualTo(ordinalBefore),
                "结束后的请求不得分配 ProducerOrdinal");
            Assert.That(sim.CommandIngress.CaptureSnapshot().FutureBuckets, Is.Empty,
                "结束后的请求不得进入 Tick 桶");
            Assert.That(sim.CurrentSnapshot.NextCommandSequence, Is.EqualTo(sequenceAfterTickZero),
                "结束后的请求不得分配 CommandSequence");
            Assert.That(sim.CommandIngress.LastFrozenPlayerFacts.Count, Is.EqualTo(frozenPlayerFactsBefore),
                "结束后的请求不得进入回放命令流（最近一次冻结批次的 Player 事实）");
            Assert.That(sim.CommandIngress.LastFrozenPlayerFacts
                    .Any(f => f.Request.TargetTick == defaultTickAfterEnd), Is.False);

            // 结束后再次 Step 是既不推进也不处理命令的 AlreadyEnded 幂等返回。
            StepResult again = Task09Fixture.StepNext(sim);
            Assert.That(again.Status, Is.EqualTo(StepStatus.AlreadyEnded));
            Assert.That(again.Events.Count, Is.EqualTo(0));
        }

        // =====================================================================
        // 必需测试 15：命令阶段前战斗结束 ⇒ 已冻结请求按序获得稳定拒绝码
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>已冻结请求被静默丢弃（没有拒绝事件）⇒ 事件断言红；</item>
        /// <item>拒绝发生在分配 CommandSequence 之前（"结束后不编号"被错误地扩到本 Tick 已冻结请求）
        /// ⇒ 序号断言红；</item>
        /// <item>该分支仍然跑命令处理器 ⇒ "零权威写入"断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void FrozenRequestsAreRejectedWhenBattleEndsBeforeCommandPhase()
        {
            // 第 1 次阶段 2 求值（= Step Tick 1）时判据返回结果码 ⇒ 战斗在命令阶段之前结束。
            var gate = new Task09Fixture.GateAfterEvaluations(1, "result.t09.victory");
            var schedule = new Task09Fixture.ScriptedWindowSchedule()
                .Open(Task09Fixture.WindowOpenTick, Task09Fixture.Hero, Task09Fixture.WindowBudget);
            var sim = Task09Fixture.NewSim(schedule, gate);
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);
            CommandIngressEntry ai = Task09Fixture.AiEntry(sim);

            Task09Fixture.StepNext(sim);   // Tick 0：判据第 0 次求值，返回 null

            // Tick 1 的冻结批次里放两条命令：Player（优先级 0）在 AI（优先级 10）之前。
            Assert.That(ai.Submit(Task09Fixture.CloseWindow(1L, new WindowId(1L))), Is.Null);
            Assert.That(player.Submit(Task09Fixture.ScheduleAdd(1L, 1L, 1L, new WindowId(1L), 1L)), Is.Null);
            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(1L);

            StepResult ended = sim.Step(1L, batch);
            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded));

            List<CommandRejectedEvent> rejected = EventsOf<CommandRejectedEvent>(ended);
            Assert.That(rejected.Count, Is.EqualTo(2),
                "本 Tick 已冻结的每条请求都必须获得一条可见拒绝，不得静默吞掉");
            Assert.That(rejected.Select(r => r.ReasonCode).ToArray(),
                Is.EqualTo(new[]
                {
                    CommandCodes.COMMAND_BATTLE_ENDED_BEFORE_COMMAND_PHASE,
                    CommandCodes.COMMAND_BATTLE_ENDED_BEFORE_COMMAND_PHASE
                }));
            Assert.That(rejected.Select(r => r.CommandSequence).ToArray(), Is.EqualTo(new[] { 1L, 2L }),
                "已冻结请求仍经网关规范化、分配 CommandSequence 并按规范顺序返回");
            Assert.That(sim.LastCommandSet.Envelopes.Select(e => e.ControllerId.Value).ToArray(),
                Is.EqualTo(new[] { "controller.player", "controller.enemy_ai" }),
                "规范顺序 = SourcePriority -> ControllerId(Ordinal) -> ProducerOrdinal");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.EqualTo(0),
                "结束分支不运行命令处理器，不写任何权威状态");
            Assert.That(ended.Snapshot.ScheduleRevision, Is.EqualTo(0L));
        }

        // =====================================================================
        // 必需测试 16：Finalizer 清空未来 Tick 桶
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>Finalizer 只清当前 Tick 而不清未来桶 ⇒ 桶断言红；</item>
        /// <item>清空但不记账（无审计计数）⇒ 报告断言红；</item>
        /// <item>清空后入口又接受新请求 ⇒ 结束后拒绝断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void BattleEndFinalizerClearsFutureRequestBuckets()
        {
            var sim = Task09Fixture.NewSim();
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);

            Task09Fixture.StepNext(sim);   // 完成 Tick 0

            // 未来 Tick 桶：5 与 6（当前 Tick = 0）。
            Assert.That(player.Submit(Task09Fixture.CloseWindow(5L, new WindowId(1L))), Is.Null);
            Assert.That(player.Submit(Task09Fixture.CloseWindow(6L, new WindowId(1L))), Is.Null);
            Assert.That(FutureBucketTicks(sim), Is.EqualTo(new[] { 5L, 6L }));

            sim.RequestStop();
            StepResult ended = Task09Fixture.StepNext(sim);   // Tick 1：唯一 Finalizer 闭合

            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded));
            Assert.That(sim.FinalizerReport.ClearedFutureTickBucketFacts, Is.EqualTo(2),
                "被清空的未来请求事实必须被审计计数，而不是静默消失");
            Assert.That(sim.FinalizerReport.EndedAtTick, Is.EqualTo(1L));
            Assert.That(sim.CommandIngress.CaptureSnapshot().FutureBuckets, Is.Empty,
                "Finalizer 必须清空全部未来 Tick 桶");
            Assert.That(sim.CommandIngress.FrozenThroughTick, Is.EqualTo(1L));
            Assert.That(sim.CommandIngress.NextDefaultTargetTick, Is.EqualTo(2L));

            CommandIngressRejection afterEnd = player.Submit(
                Task09Fixture.CloseWindow(sim.CommandIngress.NextDefaultTargetTick, new WindowId(1L)));
            Assert.That(afterEnd, Is.Not.Null);
            Assert.That(afterEnd.ReasonCode, Is.EqualTo(CommandCodes.BATTLE_ALREADY_ENDED));
            Assert.That(sim.CommandIngress.CaptureSnapshot().FutureBuckets, Is.Empty);
        }
    }
}
