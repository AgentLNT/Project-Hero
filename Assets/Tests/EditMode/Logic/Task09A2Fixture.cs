using System;
using System.Collections.Generic;
using System.Linq;
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
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 09 · A2 轮的共享夹具：<strong>真实 <see cref="BattleDefinition"/> +
    /// 真实 <see cref="BattleSimulation"/> + 真实命令入口</strong>，不走任何手工喂值路径。
    ///
    /// <para>
    /// 与 A1 的 <c>Task09Fixture</c> <strong>刻意分开</strong>：A1 的文件已验收冻结，
    /// 本夹具需要额外的东西（Block/Dodge 定义、中立阵营、AI 控制者、决策快照观察者），
    /// 因此在独立文件里自建定义，避免改动 A1 已验收的资产。
    /// </para>
    ///
    /// <para>
    /// 几何沿用任务 08 集成段已验证的单三角体积表（<c>ExpandFromBases((3,0,1),(4,1,-1))</c>），
    /// 站位一行排开、互不重叠：hero <c>(0,0)</c>、mon <c>(6,0)</c>、neutral <c>(14,0)</c>、
    /// ally <c>(22,0)</c>。PrimaryTargetOnly 的反应候选只读
    /// <c>ActionPlan.PrimaryTargetUnitId</c>（不读几何），因此本夹具的攻击模式可以为 null。
    /// </para>
    /// </summary>
    internal static class Task09A2Fixture
    {
        public const string AttackSpecId = "action.t09a2.attack";
        public const string AlliedAttackSpecId = "action.t09a2.attack_allied";
        public const string NeutralAttackSpecId = "action.t09a2.attack_neutral";
        public const string BlockSpecId = "action.t09a2.block";
        public const string DodgeSpecId = "action.t09a2.dodge";

        public const int AttackWindupTicks = 10;
        public const int AttackRecoveryTicks = 2;
        public const int BlockReactionWindupTicks = 1;
        public const int BlockRecoveryTicks = 1;
        public const int DodgeReactionWindupTicks = 1;
        public const int DodgeRecoveryTicks = 1;

        /// <summary>攻击起点 = 1 ⇒ ImpactTick = 11；Block 选项截止 = 11 − 1 = 10。</summary>
        public const long WindowOpenTick = 0L;
        public const long AttackSubmitTick = 1L;
        public const long AttackImpactTick = AttackSubmitTick + AttackWindupTicks;
        public const long BlockOptionDeadlineTick = AttackImpactTick - BlockReactionWindupTicks;

        public const int WindowBudget = 400;
        public const int InitialHealth = 2000;
        public const string EncounterIdValue = "encounter.t09a2";
        public const string VolumeSpecIdValue = "unit_volume.t09a2";
        public const string MovementPatternIdValue = "movement_pattern.t09a2";

        public static BattleRules Rules => BattleRules.FrozenV1;

        /// <summary>单位 ID 按 EncounterSlotId 的 Ordinal 顺序分配（a_hero, b_mon, c_neut, d_ally）。</summary>
        public static UnitId Hero => new UnitId(1L);
        public static UnitId Monster => new UnitId(2L);
        public static UnitId Neutral => new UnitId(3L);
        public static UnitId Ally => new UnitId(4L);

        public static FactionId HeroFaction => new FactionId("faction.hero");
        public static FactionId MonsterFaction => new FactionId("faction.monster");
        public static FactionId NeutralFaction => new FactionId("faction.neutral");

        public static ControllerId PlayerId => new ControllerId("controller.player");
        public static ControllerId AiId => new ControllerId("controller.enemy_ai");

        public static EncounterDefinitionId EncounterId => new EncounterDefinitionId(EncounterIdValue);

        public static GridPoint HeroAnchor => new GridPoint(0, 0);
        public static GridPoint MonsterAnchor => new GridPoint(6, 0);
        public static GridPoint NeutralAnchor => new GridPoint(14, 0);
        public static GridPoint AllyAnchor => new GridPoint(22, 0);

        /// <summary>第五个槽位（仅 <c>withFragileHostile</c>）：锚点 <c>(10,0)</c> ⇒ 三角 <c>(13,0,1)</c>。</summary>
        public static GridPoint FragileHostileAnchor => new GridPoint(10, 0);

        /// <summary>该槽位单位 ID（按 EncounterSlotId 的 Ordinal 顺序排在最后）。</summary>
        public static UnitId FragileHostile => new UnitId(5L);

        /// <summary>只够挨一次权威攻击的血量（攻击原始值 10 生命点 ⇒ 10240 Q10）。</summary>
        public const int FragileHostileHealth = 5;

        public static BattleRuntimeInputs Inputs => new BattleRuntimeInputs(InitialRngSeed: 31UL, InitialMetaResource: 0);

        /// <summary>带开局局外资源的输入（并发行动激活用；费用只来自权威定义）。</summary>
        public static BattleRuntimeInputs InputsWithMeta(int metaResource)
            => new BattleRuntimeInputs(InitialRngSeed: 31UL, InitialMetaResource: metaResource);

        private static GridBoundaryDefinition Wide
            => new GridBoundaryDefinition(new GridPoint(-60, -60), new GridPoint(60, 60));

        public static ActionSpecId Spec(string id) => new ActionSpecId(id);
        private static EncounterSlotId Slot(string id) => new EncounterSlotId(id);
        private static UnitDefinitionId UnitDef(string id) => new UnitDefinitionId("unit.t09a2." + id);
        private static ActionSetId ActionSetOf(string slotId) => new ActionSetId("action_set.t09a2." + slotId);

        private static IReadOnlyList<DirectionalTriangleSet> VolumeDirections()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        private static MovementPatternSpec MovementPattern()
            => new MovementPatternSpec(new MovementPatternId(MovementPatternIdValue), VolumeDirections());

        /// <summary>
        /// 攻击区域模式：每个朝向的相对点集 <c>{(2j+3, 0, 1)}</c>（j = 0..12），
        /// 朝东、锚点 <c>(0,0)</c> ⇒ 绝对区域 <c>{(3,0,1),(5,0,1),…,(27,0,1)}</c>
        /// （与任务 08 集成段同一张表：它覆盖 mon <c>(9,0,1)</c>、neut <c>(17,0,1)</c>、ally <c>(25,0,1)</c>）。
        /// 只服务"攻击真的能走到 ImpactTick 并完成结算"这一事实——本夹具的目标资格判定
        /// 完全走 <c>PrimaryTargetUnitId</c>，不读几何。
        /// </summary>
        public static readonly AttackPatternSpec AttackPattern = BuildAttackPatternCore();

        private static AttackPatternSpec BuildAttackPatternCore()
        {
            var directions = new List<DirectionalTriangleSet>(GridDirectionInfo.DirectionCount);
            for (int f = 0; f < GridDirectionInfo.DirectionCount; f++)
            {
                var points = new List<TrianglePoint>(13);
                for (int j = 0; j < 13; j++) points.Add(new TrianglePoint(2 * j + 3, 0, 1));
                directions.Add(new DirectionalTriangleSet((GridDirection)f, points));
            }
            return new AttackPatternSpec(new AttackPatternId("attack.pattern.t09a2.grid"), directions);
        }

        /// <summary>本夹具唯一的伤害通道（被动抵抗与分通道明细都按它配置）。</summary>
        public static DamageChannelId Channel => DamageChannels.PhysicalBlunt;

        // ================= 定义 =================

        private static ActionSpec AttackSpecOf(string id, TargetRelationMask mask, bool reactable)
            => new ActionSpec(
                Spec(id), ActionType.Attack,
                new AttackTimingSpec(AttackWindupTicks, AttackRecoveryTicks),
                new AttackPayloadSpec(
                    new[]
                    {
                        new DamageComponentSpec(Channel, 10f, DamageChannelCatalog.GetDefaultTags(Channel))
                    },
                    ImpactProfileId: ImpactProfiles.Blunt,
                    ForceMultiplier: 1f,
                    TargetPolicy: TargetPolicy.PrimaryTargetOnly,
                    AllowedTargetRelations: mask,
                    MomentumDirectionOffsetSteps: 0,
                    Pattern: AttackPattern,
                    Tags: reactable
                        ? AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable
                        : AttackTagMask.None),
                AdrenalineCost: 0);

        /// <summary>
        /// 唯一一份定义。
        /// </summary>
        /// <param name="heroSourceKind">
        /// hero 槽位所属控制者的 <c>CommandSourceKind</c>（默认 Player）。
        /// 它是「换一种来源种类不改变任何单位资格」这条用例的<strong>唯一</strong>变量。
        /// </param>
        /// <param name="aiSourceKind">mon 槽位所属控制者的 <c>CommandSourceKind</c>（默认 Ai）。</param>
        /// <param name="withFragileHostile">
        /// 是否追加第 5 个槽位：敌对阵营、无人控制、血量只够挨一次权威攻击。
        /// 它让「目标已死」这条拒绝链可以在<strong>真实伤害提交</strong>之后被观察到，
        /// 而不需要用反射伪造生命。默认 <c>false</c>（其余用例的四槽位定义保持不变）。
        /// </param>
        public static BattleDefinition BuildDefinition(
            CommandSourceKind heroSourceKind = CommandSourceKind.Player,
            CommandSourceKind aiSourceKind = CommandSourceKind.Ai,
            bool withFragileHostile = false)
            => BuildDefinition(heroSourceKind, aiSourceKind, withFragileHostile, aiActionSpecIds: null);

        /// <summary>
        /// 唯一一份定义的完整重载。A3 轮新增 <paramref name="aiActionSpecIds"/>：
        /// 它只改变 <c>b_mon</c> 槽位的 <c>ActionSet</c> 内容（<c>null</c> = 与其余槽位相同的全量集合），
        /// 用于让「AI 只能反应、不能主动进攻」这一世界可构造——它不是"第二套动作表"，
        /// 而是同一个 <see cref="ActionSetDefinition"/> 类型上的内容配置。
        /// </summary>
        /// <param name="heroSourceKind">
        /// hero 槽位所属控制者的 <c>CommandSourceKind</c>（默认 Player）。
        /// 它是「换一种来源种类不改变任何单位资格」这条用例的<strong>唯一</strong>变量。
        /// </param>
        /// <param name="aiSourceKind">mon 槽位所属控制者的 <c>CommandSourceKind</c>（默认 Ai）。</param>
        /// <param name="withFragileHostile">
        /// 是否追加第 5 个槽位：敌对阵营、无人控制、血量只够挨一次权威攻击。
        /// 它让「目标已死」这条拒绝链可以在<strong>真实伤害提交</strong>之后被观察到，
        /// 而不需要用反射伪造生命。默认 <c>false</c>（其余用例的四槽位定义保持不变）。
        /// </param>
        /// <param name="aiActionSpecIds">
        /// <c>b_mon</c> 槽位 <c>ActionSet</c> 的内容；<c>null</c> = 与其余槽位相同的全量集合。
        /// </param>
        public static BattleDefinition BuildDefinition(
            CommandSourceKind heroSourceKind,
            CommandSourceKind aiSourceKind,
            bool withFragileHostile,
            IReadOnlyList<ActionSpecId> aiActionSpecIds)
        {
            var factionModel = new FactionModelDefinition(
                new List<FactionDefinition>
                {
                    new FactionDefinition(HeroFaction),
                    new FactionDefinition(MonsterFaction),
                    new FactionDefinition(NeutralFaction)
                },
                new List<FactionRelationDefinition>
                {
                    // 规范化上三角：按 StringComparer.Ordinal，"faction.hero" < "faction.monster" < "faction.neutral"。
                    new FactionRelationDefinition(HeroFaction, MonsterFaction, FactionDisposition.Hostile),
                    new FactionRelationDefinition(HeroFaction, NeutralFaction, FactionDisposition.Neutral),
                    new FactionRelationDefinition(MonsterFaction, NeutralFaction, FactionDisposition.Neutral)
                });

            var specs = new List<ActionSpec>
            {
                AttackSpecOf(AttackSpecId, TargetRelationMask.Hostile, reactable: true),
                // 只多一个 Allied 位：用于隔离"友军需要显式位"。
                AttackSpecOf(AlliedAttackSpecId, TargetRelationMask.Hostile | TargetRelationMask.Allied, reactable: false),
                // 只多一个 Neutral 位：用于隔离"中立需要显式位"。
                AttackSpecOf(NeutralAttackSpecId, TargetRelationMask.Hostile | TargetRelationMask.Neutral, reactable: false),
                new ActionSpec(
                    Spec(BlockSpecId), ActionType.Block,
                    new BlockReactionTimingSpec(BlockReactionWindupTicks, BlockRecoveryTicks),
                    new BlockPayloadSpec(DefenseTagMask.Blockable),
                    AdrenalineCost: FrozenDesignValues.BlockAdrenalineCost),
                new ActionSpec(
                    Spec(DodgeSpecId), ActionType.Dodge,
                    new DodgeReactionTimingSpec(DodgeReactionWindupTicks, DodgeRecoveryTicks),
                    new DodgePayloadSpec(MaxDistanceSteps: 4, Pattern: MovementPattern()),
                    AdrenalineCost: FrozenDesignValues.DodgeAdrenalineCost)
            };

            var allSpecIds = new List<ActionSpecId>();
            for (int i = 0; i < specs.Count; i++) allSpecIds.Add(specs[i].ActionSpecId);

            var volume = new VolumeSpec(new VolumeSpecId(VolumeSpecIdValue), VolumeDirections());

            var slotIds = new List<string> { "a_hero", "b_mon", "c_neut", "d_ally" };
            var anchors = new List<GridPoint> { HeroAnchor, MonsterAnchor, NeutralAnchor, AllyAnchor };
            var factions = new List<FactionId> { HeroFaction, MonsterFaction, NeutralFaction, HeroFaction };
            var health = new List<int> { InitialHealth, InitialHealth, InitialHealth, InitialHealth };

            if (withFragileHostile)
            {
                // 第 5 个槽位：敌对阵营、无人控制、低血量 —— 专门用于"目标已死"这条链。
                // 它死亡不会结束战斗（同阵营的 b_mon 仍存活），因此"目标已死"不会被
                // BATTLE_ALREADY_ENDED 掩盖。锚点 (10,0) ⇒ 三角 (13,0,1) 落在攻击区域内。
                slotIds.Add("e_fragile");
                anchors.Add(FragileHostileAnchor);
                factions.Add(MonsterFaction);
                health.Add(FragileHostileHealth);
            }

            var actionSets = new List<ActionSetDefinition>();
            var units = new List<UnitDefinition>();
            var slots = new List<EncounterUnitSlot>();
            for (int i = 0; i < slotIds.Count; i++)
            {
                actionSets.Add(new ActionSetDefinition(ActionSetOf(slotIds[i]),
                    aiActionSpecIds != null && slotIds[i] == "b_mon"
                        ? aiActionSpecIds
                        : new List<ActionSpecId>(allSpecIds)));
                units.Add(new UnitDefinition(UnitDef(slotIds[i]), 10f, 10f,
                    Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), health[i],
                    ActionSetOf(slotIds[i]), volume.VolumeSpecId));
                slots.Add(new EncounterUnitSlot(Slot(slotIds[i]), UnitDef(slotIds[i]), factions[i],
                    anchors[i], GridDirection.East));
            }

            var encounter = new EncounterDefinition(
                EncounterId, Wide, slots,
                new List<ControllerBinding>
                {
                    new ControllerBinding(PlayerId, heroSourceKind, new List<EncounterSlotId> { Slot("a_hero") }),
                    new ControllerBinding(AiId, aiSourceKind, new List<EncounterSlotId> { Slot("b_mon") })
                },
                new VictoryDefinition(
                    new List<FactionId> { HeroFaction }, new List<FactionId> { MonsterFaction },
                    "result.t09a2.victory", "result.t09a2.defeat", "result.t09a2.draw"));

            return new BattleDefinition(
                "battle-definition.task09.a2", Rules.TicksPerSecond, Rules, ConcurrentActionDefinition.FrozenV1,
                new ReactionRules(CommandIngressLeadTicks: 1, MinimumReactionLeadTicks: 1),
                AdrenalineRules.FrozenV1, factionModel,
                new List<DamageChannelDefinition>(), new List<ImpactProfileDefinition>(),
                units, specs, new List<AttackPatternSpec> { AttackPattern },
                new List<VolumeSpec> { volume }, new List<MovementPatternSpec> { MovementPattern() },
                actionSets, new List<StatusEffectSpec>(),
                new List<EncounterDefinition> { encounter }, null,
                "test-definition-hash.t09.a2");
        }

        // ================= 装配与入口 =================

        public static BattleSimulation NewSim(
            BattleDefinition definition = null,
            BattleSimulationAssembly assembly = null,
            ITurnWindowSchedule schedule = null)
            => BattleSimulation.Create(
                definition ?? BuildDefinition(),
                EncounterId, Inputs,
                assembly ?? BattleSimulationAssembly.Standard());

        /// <summary>带开局局外资源的模拟（并发行动激活用）。</summary>
        public static BattleSimulation NewSimWithMeta(int metaResource, BattleDefinition definition = null,
            BattleSimulationAssembly assembly = null)
            => BattleSimulation.Create(
                definition ?? BuildDefinition(),
                EncounterId, InputsWithMeta(metaResource),
                assembly ?? BattleSimulationAssembly.Standard());

        public static CommandIngressEntry Entry(BattleSimulation sim, ControllerId controllerId)
        {
            CommandIngressEntry entry = sim.CommandIngress.FindEntry(controllerId);
            Assert.That(entry, Is.Not.Null,
                "夹具前提：ControllerBinding 必须注册出命令入口；controller=" + controllerId.Value);
            return entry;
        }

        public static CommandIngressEntry PlayerEntry(BattleSimulation sim) => Entry(sim, PlayerId);

        public static CommandIngressEntry AiEntry(BattleSimulation sim) => Entry(sim, AiId);

        /// <summary>冻结 <paramref name="tick"/> 并推进（批次来自本场注册表）。</summary>
        public static StepResult Step(BattleSimulation sim, long tick)
        {
            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(tick);
            return sim.Step(tick, batch);
        }

        /// <summary>推进到下一个 Tick。</summary>
        public static StepResult StepNext(BattleSimulation sim) => Step(sim, sim.Tick + 1L);

        /// <summary>逐 Tick 推进到 <paramref name="tick"/>（Step 契约要求 Tick 严格连续）。</summary>
        public static void AdvanceTo(BattleSimulation sim, long tick)
        {
            for (long t = sim.Tick + 1L; t <= tick; t++) Step(sim, t);
        }

        /// <summary>经指定入口提交一条请求，并断言入口接受它（入口级拒绝在本夹具里恒为夹具错误）。</summary>
        public static void Submit(BattleSimulation sim, ControllerId controllerId, CommandRequest request)
        {
            CommandIngressRejection rejection = Entry(sim, controllerId).Submit(request);
            Assert.That(rejection, Is.Null,
                "夹具前提：命令入口必须接受请求（入口级拒绝 = 夹具错误）；code="
                + (rejection == null ? "<null>" : rejection.ReasonCode));
        }

        // ================= 载荷工厂 =================

        public static AddOrdinaryPlanOperation AddHeroAttack(
            long temporaryKey, long? requestedStartTick, UnitId owner, ActionSpecId specId, UnitId? target,
            ActionPlanId anchor = default)
            => new AddOrdinaryPlanOperation(
                temporaryKey, owner, specId, requestedStartTick, anchor, target, GridDirection.East, null);

        public static CommandRequest AddPlan(
            long targetTick, long expectedRevision, WindowId? window,
            UnitId owner, ActionSpecId specId, UnitId? target,
            long? requestedStartTick, long temporaryKey = 1L)
            => new CommandRequest(
                targetTick, new ScheduleEditScope(expectedRevision, window),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    AddHeroAttack(temporaryKey, requestedStartTick, owner, specId, target)
                }));

        public static CommandRequest Reaction(
            long targetTick, ReactionOpportunityId opportunityId, ReactionCommandKind kind,
            ActionSpecId specId, GridPoint? dodgeDestination = null)
            => new CommandRequest(
                targetTick, new ReactionCommandScope(opportunityId),
                new ReactionCommandPayload(kind, specId, dodgeDestination));

        public static CommandRequest CloseWindow(long targetTick, WindowId windowId)
            => new CommandRequest(
                targetTick, new WindowCommandScope(windowId),
                new WindowCommandPayload(WindowCommandKind.CloseOwnWindow));

        // ================= 事件与账本助手 =================

        public static List<T> EventsOf<T>(StepResult result) where T : LogicEvent
            => result.Events.Events.OfType<T>().ToList();

        public static IReadOnlyList<string> RejectionCodes(StepResult result)
            => EventsOf<CommandRejectedEvent>(result).Select(e => e.ReasonCode).ToArray();

        /// <summary>入口级拒绝的稳定码（未获得 CommandSequence 的请求）。</summary>
        public static IReadOnlyList<string> IngressRejectionCodes(StepResult result)
            => EventsOf<CommandIngressRejectedEvent>(result).Select(e => e.ReasonCode).ToArray();

        /// <summary>
        /// 本 Tick 的<strong>全部</strong>稳定拒绝码（入口级 + 处理器级），按两族各自的规范顺序拼接。
        /// scope/payload 判别不匹配属<strong>入口级</strong>拒绝（不分配序号），
        /// 而控制权/机会/机会截止属<strong>处理器级</strong>拒绝（已分配序号）——
        /// 断言"稳定拒绝"时必须同时覆盖两族，否则会漏判。
        /// </summary>
        public static IReadOnlyList<string> AllRejectionCodes(StepResult result)
            => IngressRejectionCodes(result).Concat(RejectionCodes(result)).ToArray();

        public static long[] RejectionSequences(StepResult result)
            => EventsOf<CommandRejectedEvent>(result).Select(e => e.CommandSequence).ToArray();

        /// <summary>把单位的 Available 安排到至少 <paramref name="atLeast"/>（测试前置，不是被测行为）。</summary>
        public static void SeedAdrenaline(BattleSimulation sim, UnitId unitId, int atLeast)
        {
            AdrenalineLedger ledger = sim.AdrenalineLedgerOf(unitId);
            Assert.That(ledger, Is.Not.Null, "夹具前提：单位必须有唯一账本");
            Assert.That(ledger.AvailableAdrenaline, Is.Zero, "夹具前提：开局 Available 按规格恒为 0");
            for (int round = 0; round < 64 && ledger.AvailableAdrenaline < atLeast; round++)
            {
                ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(
                    unitId,
                    TotalFinalDamageDealtQ10: 0,
                    TotalFinalDamageReceivedQ10: 1024,
                    SuccessfulBlockCount: 0,
                    SuccessfulDodgeCount: 0,
                    ClashSuccessCount: 0));
            }
            Assert.That(ledger.AvailableAdrenaline, Is.GreaterThanOrEqualTo(atLeast),
                "夹具前提：安排后的额度必须够付目标费用；available=" + ledger.AvailableAdrenaline);
        }

        /// <summary>捕获本场最近一次决策快照的观察者（任务 05 的装配接缝，不替换任何被测实现）。</summary>
        internal sealed class SnapshotCapturingObserver : IDecisionObserver
        {
            public readonly List<DecisionSnapshot> Snapshots = new List<DecisionSnapshot>();

            public void ObserveOrdered(
                DecisionSnapshot snapshot, CommandIngressRegistry ingress, long nextThinkTick)
                => Snapshots.Add(snapshot);
        }

        /// <summary>
        /// A3 轮：只记录 Canonical 视角决策快照的诊断观察者（不声明 Controller），
        /// 与一个 <c>BattleSimulationAssembly</c> 打包返回，供"需要一个只读决策快照"的用例直接使用。
        /// </summary>
        internal sealed class CanonicalSnapshotProbe
        {
            private readonly List<DecisionSnapshot> _observed = new List<DecisionSnapshot>();

            public BattleSimulationAssembly Assembly
                => new BattleSimulationAssembly(
                    decisionObservers: new IDecisionObserver[] { new Probe(_observed) });

            public DecisionSnapshot Last
            {
                get
                {
                    Assert.That(_observed.Count, Is.GreaterThan(0),
                        "夹具前提：必须已经投递过决策快照（装配里没有观察者时不会投递）");
                    return _observed[_observed.Count - 1];
                }
            }

            public DecisionSnapshot At(long tick)
            {
                for (int i = _observed.Count - 1; i >= 0; i--)
                {
                    if (_observed[i].Tick == tick) return _observed[i];
                }
                return null;
            }

            private sealed class Probe : IDecisionObserver
            {
                private readonly List<DecisionSnapshot> _sink;

                public Probe(List<DecisionSnapshot> sink) => _sink = sink;

                public void ObserveOrdered(
                    DecisionSnapshot snapshot, CommandIngressRegistry ingress, long nextTick)
                    => _sink.Add(snapshot);
            }
        }

        // ================= 场景助手 =================

        /// <summary>
        /// 开窗（Tick 0）→ 在 <see cref="AttackSubmitTick"/> 提交 hero 对 <paramref name="target"/> 的攻击
        /// → 推进到攻击已启动、反应机会已公开的那一步。
        /// 返回时：<c>sim.Tick == 1</c>、攻击已 Locked/Running、monster 的机会已公开。
        /// </summary>
        public static ActionPlan ArrangeStartedAttack(
            BattleSimulation sim, UnitId target)
            => ArrangeStartedAttack(sim, target, Spec(AttackSpecId), Hero, seedDefenderAdrenaline: true);

        public static ActionPlan ArrangeStartedAttack(
            BattleSimulation sim, UnitId target, ActionSpecId specId)
            => ArrangeStartedAttack(sim, target, specId, Hero, seedDefenderAdrenaline: true);

        public static ActionPlan ArrangeStartedAttack(
            BattleSimulation sim, UnitId target, bool seedDefenderAdrenaline)
            => ArrangeStartedAttack(sim, target, Spec(AttackSpecId), Hero, seedDefenderAdrenaline);

        public static ActionPlan ArrangeStartedAttack(
            BattleSimulation sim, UnitId target, ActionSpecId specId, UnitId owner,
            bool seedDefenderAdrenaline)
        {
            UnitId attacker = owner;
            // 窗口属于攻击者自己（提交授权的最简合法形态）；窗口与反应权正交这一点
            // 由 ReactionDoesNotRequireOwnWindowOrConcurrentAuthority 单独覆盖。
            sim.WindowManager.ScheduleWindow(WindowOpenTick, attacker, WindowBudget);
            Step(sim, WindowOpenTick);
            Assert.That(sim.CurrentTurnWindow, Is.Not.Null, "夹具前提：Tick 0 必须打开窗口");

            WindowId windowId = sim.CurrentTurnWindow.WindowId;
            Submit(sim, EntryControllerOf(sim, attacker),
                AddPlan(AttackSubmitTick, sim.ScheduleAuthority.ScheduleRevision, windowId,
                    attacker, specId, target, AttackSubmitTick));

            Step(sim, AttackSubmitTick);

            IReadOnlyList<ActionPlan> active = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(active.Count, Is.EqualTo(1),
                "夹具前提：恰好一个活动攻击计划（命令不得被拒绝）；rejections="
                + string.Join(",", sim.LastCommandSet.RejectedCommands.Select(r => r.ReasonCode)));
            ActionPlan plan = active[0];
            Assert.That(plan.IsEditable, Is.False, "夹具前提：攻击已在阶段 7 原子启动");
            Assert.That(plan.ImpactTick, Is.EqualTo(AttackImpactTick),
                "夹具前提：ImpactTick = 起点 + 权威前摇");
            Assert.That(plan.PrimaryTargetUnitId, Is.EqualTo(target), "夹具前提：PrimaryTarget 被事务固定");

            if (seedDefenderAdrenaline)
                SeedAdrenaline(sim, target, FrozenDesignValues.BlockAdrenalineCost);

            return plan;
        }

        /// <summary>
        /// 与 <see cref="ArrangeStartedAttack(BattleSimulation,UnitId)"/> 完全同形，但<strong>不</strong>为
        /// <paramref name="target"/> 预置肾上腺素——它专供 A3 轮"AI 只做反应"的世界：
        /// 那时防御者还没有任何可用额度，若预置了额度会掩盖"额度从哪来"的事实。
        /// </summary>
        public static ActionPlan ArrangeStartedAttackOnly(BattleSimulation sim, UnitId target)
            => ArrangeStartedAttack(sim, target, Spec(AttackSpecId), Hero, seedDefenderAdrenaline: false);

        /// <summary>该单位所属的控制者（唯一来源 = 已验证定义里的 ControllerBinding）。</summary>
        public static ControllerId EntryControllerOf(BattleSimulation sim, UnitId unitId)
        {
            if (unitId == Hero) return PlayerId;
            if (unitId == Monster) return AiId;
            throw new AssertionException("夹具不变量：本夹具只有 hero/mon 两个受控单位，unit=" + unitId.Value);
        }

        /// <summary>公开且仍开放的机会里选取指定防御者的那一条（找不到即夹具错误）。</summary>
        public static ReactionOpportunityRuntime OpenOpportunityFor(BattleSimulation sim, UnitId defender)
        {
            List<ReactionOpportunityRuntime> matches = sim.ReactionOpportunities.ActiveOpportunities
                .Where(o => o.DefenderUnitId == defender && o.IsOpen).ToList();
            Assert.That(matches.Count, Is.EqualTo(1),
                "夹具前提：恰好一条仍开放的机会；defender=" + defender.Value
                + " active=" + sim.ReactionOpportunities.ActiveOpportunities.Count);
            return matches[0];
        }

        public static ReactionOptionRuntime PublishedOption(
            ReactionOpportunityRuntime opportunity, string specId)
        {
            List<ReactionOptionRuntime> matches = opportunity.Options
                .Where(o => o.ReactionActionSpecId.Value == specId && o.IsPublished).ToList();
            Assert.That(matches.Count, Is.EqualTo(1),
                "夹具前提：选项必须已公开；spec=" + specId);
            return matches[0];
        }

        /// <summary>权威排程指纹（用于"零局部写入"断言）。</summary>
        public static string ScheduleFingerprint(BattleSimulation sim)
        {
            var builder = new System.Text.StringBuilder();
            IReadOnlyList<ActionPlan> plans = sim.ScheduleAuthority.Registry.ActivePlans;
            for (int i = 0; i < plans.Count; i++)
            {
                ActionPlan plan = plans[i];
                builder.Append(plan.ActionPlanId.Value).Append(':')
                    .Append(plan.OwnerUnitId.Value).Append(':')
                    .Append(plan.ActionSpecId.Value).Append(':')
                    .Append(plan.State).Append(':')
                    .Append(plan.StartTick).Append(':')
                    .Append(plan.EndTick).Append(':')
                    .Append(plan.ImpactTick).Append(':')
                    .Append(plan.BudgetCostTicks).Append(':')
                    .Append(plan.ReservedTurnBudgetTicks).Append(':')
                    .Append(plan.LastRequestedStartTick).Append(':')
                    .Append(plan.SubmittedWindowId.HasValue ? plan.SubmittedWindowId.Value.Value : -1L)
                    .Append(';');
            }
            return builder.ToString();
        }

        /// <summary>权威窗口账本指纹。</summary>
        public static string WindowFingerprint(BattleSimulation sim, WindowId windowId)
        {
            TurnWindow window = sim.WindowManager.FindWindow(windowId);
            if (window == null) return "<none>";
            return window.WindowId.Value + "|" + window.OwnerUnitId.Value + "|" +
                   window.AvailableBudgetTicks + "|" + window.ReservedBudgetTicks + "|" +
                   window.SpentBudgetTicks;
        }
    }
}
