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
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 08 <strong>集成段第三步</strong>的仓库内自动化证据：
    /// <list type="bullet">
    /// <item><strong>A · 语义事件族</strong>（任务包「必须产出」15）：分通道伤害明细、目标聚合冲击、
    /// 强制位移最终结果在真实 Step 管线里真的被发射，且载荷值等于只读观察面上的值；</item>
    /// <item><strong>B · <see cref="AdrenalineAccrualFacts"/></strong>（「必须产出」16）：
    /// 每单位每 Tick 至多一条、每反应计划至多一次成功键、<strong>失败反应零奖励</strong>、
    /// 造成/承受配对与 <c>UnitId</c> 严格升序；</item>
    /// <item><strong>C · <see cref="DisplacementRequestBuilder"/></strong>（R4 第三装配点）：
    /// 按 <c>DamageCommitReport.Units[]</c> 生成每单位唯一请求，并真的驱动阶段 13 的批量换位。</item>
    /// </list>
    ///
    /// <para>
    /// <strong>为什么必须有这个文件</strong>：<c>Interactions/**</c> 与 <c>Combat/**</c> 的用例
    /// 全部手工喂值对象，对"装配点没接线"完全不敏感；本文件里 A 段用例走真实模拟
    /// （<c>BattleSimulation.Create</c> + 真实命令入口 + 真实 Step 管线），
    /// B/C 段用例直接驱动两个<strong>真实现</strong>并断言其规格（"哪种实现缺陷会让它红"逐条写在用例上）。
    /// </para>
    /// </summary>
    public class Task08EventAndAccrualIntegrationTests
    {
        // ================= 夹具常量（与集成段第二步逐字同表，勿随手改几何） =================

        private const string AttackSpecId = "action.t08ev.attack";
        private const string ActionSetIdValue = "action_set.t08ev";
        private const string EncounterIdValue = "encounter.t08ev";
        private const int AttackBaseWindupTicks = 10;
        private const int AttackRecoveryTicks = 2;
        private const int WindowBudget = 60;
        private const long WindowOpenTick = 0L;
        private const long SubmitTick = 1L;

        /// <summary>攻击基础伤害 <c>10f</c>（创作态生命点）⇒ 量化后 <c>10240 Q10</c> = <b>10 生命点</b>。</summary>
        private const float AttackRawAmount = 10f;
        private const long ExpectedDamageQ10 = 10240L;

        /// <summary>
        /// 单位质量/动量速度 <c>10f</c>（与集成段第二步逐字同表）⇒
        /// 动量 <c>RoundHalfUp(10 × 10 × 1 × 100) = 10000</c>、
        /// 控制阻力 <c>RoundHalfUp(10 × 10 × 100) = 1000</c> ⇒ <c>KnockbackSteps = 1</c>。
        ///
        /// <para>
        /// <strong>实测事实（几何坑 3，写进交接）</strong>：<c>GridPoint</c> 的 <c>X+Y</c> 偶校验
        /// 让"一步"在格坐标上表现为 <c>ΔX = 2</c>（East 邻居偏移），因此
        /// <c>AppliedSteps == 1</c> 时落点是 <c>MonsterAnchor + (2,0)</c>，不是 <c>(+1,0)</c>。
        /// </para>
        /// </summary>
        private const float ReferenceMass = 10f;
        private const float ReferenceMomentumSpeed = 10f;
        private const int ExpectedMomentumUnits = 10000;
        private const int ExpectedKnockbackSteps = 1;
        private const int InitialHealth = 200;

        private const long ImpactTick = SubmitTick + AttackBaseWindupTicks;

        private static BattleRules Rules => BattleRules.FrozenV1;
        private static GridBoundaryDefinition Wide
            => new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40));

        private static UnitId Hero => new UnitId(1L);
        private static UnitId Monster => new UnitId(2L);
        private static UnitId Third => new UnitId(3L);
        private static FactionId HeroFaction => new FactionId("faction.hero");
        private static FactionId MonsterFaction => new FactionId("faction.monster");
        private static ControllerId Player => new ControllerId("controller.player");

        private static ActionSpecId Spec(string id) => new ActionSpecId(id);
        private static ActionSetId ActionSet => new ActionSetId(ActionSetIdValue);
        private static EncounterDefinitionId EncounterId => new EncounterDefinitionId(EncounterIdValue);
        private static EncounterSlotId Slot(string id) => new EncounterSlotId(id);
        private static BattleRuntimeInputs Inputs => new BattleRuntimeInputs(InitialRngSeed: 29UL, InitialMetaResource: 0);

        /// <summary>实测站位（与集成段第二步同表）：hero <c>(0,0)</c> 朝东、monster <c>(8,0)</c> 朝 NorthWest。</summary>
        private static GridPoint HeroAnchor => new GridPoint(0, 0);
        private static GridPoint MonsterAnchor => new GridPoint(8, 0);

        /// <summary>
        /// 单计划夹具里 monster 的强制位移落点 = <c>MonsterAnchor</c> 沿请求方向推进实际步数
        /// （<strong>实测固化</strong>：动量方向由<strong>攻击者朝向</strong>决定，
        /// hero 朝 East ⇒ 沿 +X 推开；<c>GridPoint</c> 的 <c>X+Y</c> 偶校验让位移按 2 格一步落下）。
        /// </summary>
        private static readonly GridPoint ExpectedMonsterDestination = new GridPoint(10, 0);

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
            return new AttackPatternSpec(new AttackPatternId("attack.pattern.t08ev.grid"), directions);
        }

        private static BattleDefinition BuildDefinition()
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
                    new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, AttackRawAmount,
                        DamageChannelCatalog.GetDefaultTags(DamageChannels.PhysicalBlunt)) },
                    ImpactProfileId: ImpactProfiles.Blunt,
                    ForceMultiplier: 1f,
                    TargetPolicy: TargetPolicy.PrimaryTargetOnly,
                    AllowedTargetRelations: TargetRelationMask.Hostile,
                    MomentumDirectionOffsetSteps: 0,
                    Pattern: pattern,
                    Tags: AttackTagMask.Reactable),
                AdrenalineCost: 0);

            var volume = new VolumeSpec(new VolumeSpecId("unit_volume.t08ev"), Directions());
            var actionSet = new ActionSetDefinition(
                ActionSet, new List<ActionSpecId> { attackSpec.ActionSpecId });

            var heroDefinitionId = new UnitDefinitionId("unit.t08ev.hero");
            var monsterDefinitionId = new UnitDefinitionId("unit.t08ev.monster");

            var units = new List<UnitDefinition>
            {
                new UnitDefinition(heroDefinitionId, ReferenceMass, ReferenceMomentumSpeed,
                    Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), InitialHealth, actionSet.ActionSetId, volume.VolumeSpecId),
                new UnitDefinition(monsterDefinitionId, ReferenceMass, ReferenceMomentumSpeed,
                    Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), InitialHealth, actionSet.ActionSetId, volume.VolumeSpecId)
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
                new List<ControllerBinding>
                {
                    new ControllerBinding(Player, CommandSourceKind.Player,
                        new List<EncounterSlotId> { Slot("hero"), Slot("monster") })
                },
                new VictoryDefinition(
                    new List<FactionId> { HeroFaction }, new List<FactionId> { MonsterFaction },
                    "result.t08ev.victory", "result.t08ev.defeat", "result.t08ev.draw"));

            return new BattleDefinition(
                "battle-definition.task08.events-accrual",
                Rules.TicksPerSecond, Rules, ConcurrentActionDefinition.FrozenV1,
                new ReactionRules(CommandIngressLeadTicks: 2, MinimumReactionLeadTicks: 1),
                AdrenalineRules.FrozenV1, factionModel,
                new List<DamageChannelDefinition>(), new List<ImpactProfileDefinition>(),
                units, new List<ActionSpec> { attackSpec }, new List<AttackPatternSpec> { pattern },
                new List<VolumeSpec> { volume }, new List<MovementPatternSpec>(),
                new List<ActionSetDefinition> { actionSet }, new List<StatusEffectSpec>(),
                new List<EncounterDefinition> { encounter }, null,
                "test-definition-hash.t08.events-accrual");
        }

        // =====================================================================
        // 真实装配夹具
        // =====================================================================

        private sealed class Rig
        {
            public BattleSimulation Sim;
            public CommandIngressEntry PlayerEntry;
            public ActionPlan AttackPlan;
            public StepResult ImpactStep;
            public int MonsterHealthBefore;
            public int HeroHealthBefore;
            public int HeroAdrenalineBefore;
            public int MonsterAdrenalineBefore;
        }

        private static Rig Arrange(BattleSimulationAssembly assembly = null)
        {
            BattleSimulation sim = BattleSimulation.Create(
                BuildDefinition(), EncounterId, Inputs, assembly ?? BattleSimulationAssembly.Standard());

            CommandIngressEntry playerEntry = sim.CommandIngress.FindEntry(Player);
            Assert.That(playerEntry, Is.Not.Null, "夹具前提：ControllerBinding 必须注册出命令入口");

            var rig = new Rig { Sim = sim, PlayerEntry = playerEntry };
            sim.WindowManager.ScheduleWindow(WindowOpenTick, Hero, WindowBudget);
            StepOnce(rig, WindowOpenTick);

            var request = new CommandRequest(
                SubmitTick,
                new ScheduleEditScope(sim.ScheduleAuthority.ScheduleRevision, sim.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    new AddOrdinaryPlanOperation(
                        1L, Hero, Spec(AttackSpecId), SubmitTick,
                        default, Monster, GridDirection.East, null)
                }));
            StepOnce(rig, SubmitTick, request);

            IReadOnlyList<ActionPlan> active = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(active.Count, Is.EqualTo(1), "夹具前提：恰好一个活动攻击计划");
            rig.AttackPlan = active[0];
            Assert.That(rig.AttackPlan.ImpactTick, Is.EqualTo(ImpactTick), "夹具前提：ImpactTick = 1 + 10");

            for (long tick = SubmitTick + 1L; tick < ImpactTick; tick++) StepOnce(rig, tick);

            rig.MonsterHealthBefore = HealthQ10Of(sim, Monster);
            rig.HeroHealthBefore = HealthQ10Of(sim, Hero);
            rig.HeroAdrenalineBefore = AdrenalineOf(sim, Hero);
            rig.MonsterAdrenalineBefore = AdrenalineOf(sim, Monster);
            rig.ImpactStep = StepOnce(rig, ImpactTick);
            return rig;
        }

        private static StepResult StepOnce(Rig rig, long tick, params CommandRequest[] requests)
        {
            for (int i = 0; i < requests.Length; i++)
            {
                CommandIngressRejection rejection = rig.PlayerEntry.Submit(requests[i]);
                Assert.That(rejection, Is.Null, "夹具前提：命令入口必须接受请求");
            }
            FrozenCommandBatch batch = rig.Sim.CommandIngress.FreezeTick(tick);
            StepResult result = rig.Sim.Step(tick, batch);
            for (int i = 0; i < result.Events.Count; i++)
            {
                if (result.Events.Events[i] is CommandRejectedEvent rejected)
                    Assert.Fail("夹具前提：命令被处理器拒绝：" + rejected.ReasonCode);
            }
            return result;
        }

        private static int HealthQ10Of(BattleSimulation sim, UnitId unitId)
            => sim.CurrentSnapshot.Units.First(u => u.UnitId == unitId.Value).HealthQ10;

        private static int AdrenalineOf(BattleSimulation sim, UnitId unitId)
            => sim.CurrentSnapshot.Units.First(u => u.UnitId == unitId.Value).AvailableAdrenaline;

        private static T Single<T>(StepResult result) where T : LogicEvent
        {
            var matches = result.Events.Events.OfType<T>().ToList();
            Assert.That(matches.Count, Is.EqualTo(1),
                "本 Tick 必须恰好一条 " + typeof(T).Name + "；实际 " + matches.Count
                + "，全部事件=" + Dump(result));
            return matches[0];
        }

        private static string Dump(StepResult result)
            => string.Join(",", result.Events.Events.Select(e => e.GetType().Name));

        // =====================================================================
        // A1：分通道伤害明细 + 目标聚合冲击（真实 Step）
        // =====================================================================

        /// <summary>
        /// <strong>分通道伤害明细与目标聚合冲击真的被发射，且载荷等于求解面上的值</strong>。
        ///
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>阶段 10 只求解不发射（本步之前的真实形态）⇒ 两条事件都缺失 ⇒ 红；</item>
        /// <item>发射点被放进 <c>StagedConflictResolver</c>（纯函数）⇒ 它没有 Outbox，
        /// 只能靠"顺手改世界"或第二份数据源，事件数值与 <c>StagedResolution</c> 分叉 ⇒ 红；</item>
        /// <item>把 <c>Raw/被动后/动作后</c> 三段压成一个数（或写死 0）⇒ 三段断言红；</item>
        /// <item>冲突组键没接线（恒 0）⇒ 组键断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void StageTenEmitsDamageChannelAndTargetAggregateEvents()
        {
            Rig rig = Arrange();
            DamageChannelResolvedEvent damage = Single<DamageChannelResolvedEvent>(rig.ImpactStep);
            TargetAggregateResolvedEvent aggregate = Single<TargetAggregateResolvedEvent>(rig.ImpactStep);

            TargetAggregateResolution solved = rig.Sim.StagedResolution.RemainingHits[0].Aggregate;

            // 冲突组键：本夹具里攻击计划确实入了图 ⇒ 必须是该组的真实键（不是占位 0）。
            long groupKey = rig.Sim.ConflictGraph.GroupKeyOfNode(
                rig.Sim.ConflictGraph.NodeIndexOf(rig.AttackPlan.ActionPlanId));
            Assert.That(groupKey, Is.GreaterThan(0L), "夹具前提：攻击计划必须在冲突图里");
            Assert.That(damage.ConflictGroupKey, Is.EqualTo(groupKey),
                "分通道伤害事件必须携带该接触所属冲突组的组键");
            Assert.That(aggregate.ConflictGroupKey, Is.EqualTo(groupKey));

            // 三段伤害：本夹具无被动/动作抵抗 ⇒ 三段相等且等于 10240 Q10。
            Assert.That(damage.Channels.Count, Is.EqualTo(1), "一个伤害通道");
            DamageChannelEntry channel = damage.Channels[0];
            Assert.That(channel.ChannelId, Is.EqualTo(DamageChannels.PhysicalBlunt));
            Assert.That(channel.RawQ10, Is.EqualTo(ExpectedDamageQ10));
            Assert.That(channel.AfterPassiveResistanceQ10, Is.EqualTo(ExpectedDamageQ10));
            Assert.That(channel.AfterActionResistanceQ10, Is.EqualTo(ExpectedDamageQ10));
            Assert.That(channel.PassiveResistanceQ10, Is.EqualTo(0));
            Assert.That(channel.ActionResistanceQ10, Is.EqualTo(0));
            Assert.That(damage.TotalDamageQ10, Is.EqualTo(solved.TotalDamageQ10));
            Assert.That(damage.AttackerUnitId, Is.EqualTo(Hero));
            Assert.That(damage.TargetUnitId, Is.EqualTo(Monster));
            Assert.That(damage.IncomingMomentumUnits, Is.EqualTo(ExpectedMomentumUnits));
            Assert.That(damage.ParticipatingPlanIds, Is.EqualTo(new[] { rig.AttackPlan.ActionPlanId }),
                "参与计划 ID 必须稳定升序、去重");

            // 聚合冲击：数值与求解面逐位一致（事件不是第二份真值）。
            Assert.That(aggregate.TotalDamageQ10, Is.EqualTo(solved.TotalDamageQ10));
            Assert.That(aggregate.TotalImpactUnits, Is.EqualTo(solved.TotalImpactUnits));
            Assert.That(aggregate.ResultantDirection, Is.EqualTo(solved.ResultantDirection));
            Assert.That(aggregate.KnockbackSteps, Is.EqualTo(ExpectedKnockbackSteps));
            Assert.That(aggregate.IncomingMomentumUnits, Is.EqualTo((long)ExpectedMomentumUnits),
                "总入射动量 = 接触动量之和（未被抵抗）");
            Assert.That(aggregate.TotalImpactUnits, Is.EqualTo((long)ExpectedMomentumUnits),
                "本夹具无动作抵抗 ⇒ 抵抗后入射动量之和等于入射动量");
            Assert.That(damage.AfterMomentumResistanceUnits, Is.EqualTo(ExpectedMomentumUnits));
        }

        // =====================================================================
        // A2：强制位移最终结果（真实 Step；同时是 C 的端到端抓手）
        // =====================================================================

        /// <summary>
        /// <strong>阶段 11 的请求构建器真的产生请求，阶段 12 真的求解，阶段 13 真的换位并发射事件</strong>。
        ///
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item><c>DisplacementRequestBuilder</c> 仍接 <c>NoForcedDisplacementRequests</c>
        /// （本步之前的真实形态）⇒ 无请求、无位移、无事件 ⇒ 红；</item>
        /// <item>构建器把 <c>KnockbackSteps</c> 与 <c>ResultantDirection</c> 填反、
        /// 或用 <c>KnockbackSteps</c> 当方向 ⇒ 位移方向/落点断言红；</item>
        /// <item>构建器漏填 <c>ConflictGroupKey</c>（取 0）⇒ 组键断言红
        /// （事件里的组键直接来自请求）；</item>
        /// <item>构建器为同一单位产生两条请求 ⇒ 阶段 11/12 的重复键守卫抛
        /// <c>STEP_DISPLACEMENT_REQUEST_DUPLICATE</c> ⇒ 红（而不是"静默挑一个"）。</item>
        /// </list>
        /// </summary>
        [Test]
        public void DisplacementRequestBuilderDrivesRealRelocationAndEmitsResolvedEvent()
        {
            Rig rig = Arrange();
            BattleSimulation sim = rig.Sim;

            // —— 阶段 11：每单位至多一条请求，数值取聚合结果 ——
            IReadOnlyList<ForcedDisplacementRequest> requests = sim.LastBuiltDisplacementRequests;
            Assert.That(requests.Count, Is.EqualTo(1), "本夹具只有一个被击退的目标");
            ForcedDisplacementRequest request = requests[0];
            Assert.That(request.TargetUnitId, Is.EqualTo(Monster));
            Assert.That(request.RequestedSteps, Is.EqualTo(ExpectedKnockbackSteps));
            Assert.That(request.MomentumUnits, Is.EqualTo((long)ExpectedMomentumUnits),
                "总冲击 = 抵抗后入射动量之和");
            long groupKey = sim.ConflictGraph.GroupKeyOfNode(
                sim.ConflictGraph.NodeIndexOf(rig.AttackPlan.ActionPlanId));
            Assert.That(request.ConflictGroupKey, Is.EqualTo(groupKey),
                "请求的冲突组键必须取该组的 GroupKey，而不是占位 0");

            // 提交报告本身也带上组键（唯一来源，事件与请求都取它）。
            UnitDamageCommit commit = sim.LastDamageCommitReport.Units.Single();
            Assert.That(commit.UnitId, Is.EqualTo(Monster));
            Assert.That(commit.ConflictGroupKey, Is.EqualTo(groupKey));
            Assert.That(commit.KnockbackSteps, Is.EqualTo(ExpectedKnockbackSteps));
            Assert.That(commit.ResultantDirection, Is.EqualTo(request.Direction));

            // —— 阶段 12/13：真的换位并发射"强制位移最终结果"事件 ——
            ForcedDisplacementResolvedEvent moved = Single<ForcedDisplacementResolvedEvent>(rig.ImpactStep);
            Assert.That(moved.TargetUnitId, Is.EqualTo(Monster));
            Assert.That(moved.From, Is.EqualTo(MonsterAnchor));
            Assert.That(moved.To, Is.EqualTo(ExpectedMonsterDestination), "沿请求方向移满请求步数");
            Assert.That(moved.Direction, Is.EqualTo(request.Direction));
            Assert.That(moved.RequestedSteps, Is.EqualTo(ExpectedKnockbackSteps));
            Assert.That(moved.AppliedSteps, Is.EqualTo(ExpectedKnockbackSteps));
            Assert.That(moved.StopReason, Is.EqualTo(ForcedDisplacementStopReason.Completed));
            Assert.That(moved.ConflictGroupKey, Is.EqualTo(groupKey));
            Assert.That(moved.InvalidatedPlanIds, Is.Empty, "本夹具没有依赖旧起点的移动计划");

            // 权威位置与网格锚点都必须落在新格上（不允许只有镜像变了）。
            Assert.That(sim.LogicGrid.TryGetAnchor(Monster, out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(ExpectedMonsterDestination));
            Assert.That(sim.CurrentSnapshot.Units.Single(u => u.UnitId == Monster.Value).X,
                Is.EqualTo(ExpectedMonsterDestination.X), "单位快照的权威位置必须与网格一致");

            // 伤害仍然只结算一次。
            Assert.That(HealthQ10Of(sim, Monster),
                Is.EqualTo(rig.MonsterHealthBefore - (int)ExpectedDamageQ10));
        }

        // =====================================================================
        // A3：一次真实 Step 的目标聚合事件确实存在（负控制：注入空提交系统时没有事件）
        // =====================================================================

        /// <summary>
        /// <strong>负控制</strong>：把阶段 11 的提交系统换成 <c>NoResolutionCommitSystem</c> 之后，
        /// 阶段 10 的求解事件<strong>仍然</strong>发射（它们描述求解事实），
        /// 但"每单位唯一请求"与该 Tick 的位移事件<strong>必然消失</strong>——
        /// 因为请求的唯一来源是提交报告，而负控制下它是空的。
        ///
        /// 会让它失败的实现缺陷：把请求构建器改成"直接读 <c>StagedResolution</c> 或网格"
        /// ⇒ 负控制下仍会位移 ⇒ 红；或把求解事件放到提交系统里 ⇒ 负控制下事件消失 ⇒ 红。
        /// </summary>
        [Test]
        public void NegativeControlCommitSystemSilencesRequestsButNotResolveEvents()
        {
            Rig rig = Arrange(new BattleSimulationAssembly(
                resolutionCommit: NoResolutionCommitSystem.Instance));
            BattleSimulation sim = rig.Sim;

            Assert.That(sim.ResolutionCommitSystem, Is.Null, "显式注入时不得再绑本场真实现");
            Assert.That(sim.LastDamageCommitReport.Units.Count, Is.EqualTo(0));
            Assert.That(sim.LastBuiltDisplacementRequests.Count, Is.EqualTo(0),
                "提交报告为空 ⇒ 请求必须为空（请求不从网格或求解器另取一份真值）");
            Assert.That(rig.ImpactStep.Events.Events.OfType<ForcedDisplacementResolvedEvent>().Count(),
                Is.EqualTo(0), "无请求 ⇒ 无位移结果事件");
            Assert.That(HealthQ10Of(sim, Monster), Is.EqualTo(rig.MonsterHealthBefore),
                "负控制下求解阶段仍然零世界写入");
            Assert.That(rig.ImpactStep.Events.Events.OfType<DamageChannelResolvedEvent>().Count(),
                Is.EqualTo(1), "求解事实事件不受提交负控制影响");
        }

        // =====================================================================
        // B1：真实 Step 的肾上腺素入账（唯一入口 + 造成/承受配对）
        // =====================================================================

        /// <summary>
        /// <strong>默认装配即真实现</strong>：<c>Available</c> 通过任务 07 的唯一批量入口增长，
        /// 造成方与承受方各得一份事实，且最终伤害取的是<strong>提交后</strong>的值。
        ///
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item><c>AdrenalineAccrualFactSource</c> 保持 null（本步之前的真实形态）⇒
        /// <c>Available</c> 一动不动 ⇒ 红；</item>
        /// <item>事实来源逐接触加 <c>Available</c>（绕开唯一入口）⇒ 本用例的
        /// <c>LastAdrenalineAccrualFacts</c> 面为空且数值按接触重复计 ⇒ 红；</item>
        /// <item>造成/承受只记一侧、或把两侧记成同一个数 ⇒ 配对断言红；</item>
        /// <item>用"攻击基础伤害"而不是聚合后的最终伤害 ⇒ 10240 断言红（例如把 10 生命点当 Q10）。</item>
        /// </list>
        /// </summary>
        [Test]
        public void AdrenalineAccrualUsesFinalCommittedDamageForBothSides()
        {
            Rig rig = Arrange();
            BattleSimulation sim = rig.Sim;

            IReadOnlyList<AdrenalineAccrualFacts> facts = sim.LastAdrenalineAccrualFacts;
            Assert.That(facts.Count, Is.EqualTo(2), "本 Tick 只有两个单位有事可记（每单位至多一条）");
            Assert.That(facts[0].UnitId, Is.EqualTo(Hero), "输出必须按 UnitId 严格升序");
            Assert.That(facts[1].UnitId, Is.EqualTo(Monster));

            Assert.That(facts[0].TotalFinalDamageDealtQ10, Is.EqualTo((int)ExpectedDamageQ10));
            Assert.That(facts[0].TotalFinalDamageReceivedQ10, Is.EqualTo(0));
            Assert.That(facts[1].TotalFinalDamageReceivedQ10, Is.EqualTo((int)ExpectedDamageQ10));
            Assert.That(facts[1].TotalFinalDamageDealtQ10, Is.EqualTo(0));
            Assert.That(facts[0].SuccessfulBlockCount, Is.EqualTo(0));
            Assert.That(facts[0].SuccessfulDodgeCount, Is.EqualTo(0));
            Assert.That(facts[0].ClashSuccessCount, Is.EqualTo(0));

            // 账本侧的可观察后果：Task07 的量化是一次性的（量化公式与规则常量一致）。
            // 量化 = floor(最终伤害 Q10 × 获得率 Q10 / 100)（AdrenalineLedger.QuantizeGain）。
            AdrenalineRules rules = AdrenalineRules.FrozenV1;
            int expectedHeroGain = (int)((long)(int)ExpectedDamageQ10 * rules.DamageDealtGainQ10 / 100L);
            int expectedMonsterGain = (int)((long)(int)ExpectedDamageQ10 * rules.DamageReceivedGainQ10 / 100L);
            Assert.That(expectedHeroGain, Is.EqualTo(10444), "10240 × 102 / 100 向下取整");
            Assert.That(expectedMonsterGain, Is.EqualTo(20992), "10240 × 205 / 100 向下取整");

            AdrenalineLedger heroLedger = sim.AdrenalineLedgerOf(Hero);
            AdrenalineLedger monsterLedger = sim.AdrenalineLedgerOf(Monster);
            Assert.That(heroLedger.AvailableAdrenaline,
                Is.EqualTo(Math.Min(rules.MaxAvailablePerCycle, rig.HeroAdrenalineBefore + expectedHeroGain)));
            Assert.That(monsterLedger.AvailableAdrenaline,
                Is.EqualTo(Math.Min(rules.MaxAvailablePerCycle, rig.MonsterAdrenalineBefore + expectedMonsterGain)));
            Assert.That(AdrenalineOf(sim, Hero), Is.EqualTo(heroLedger.AvailableAdrenaline),
                "单位快照镜像必须与账本一致");
        }

        /// <summary>
        /// <strong>负控制</strong>：显式注入 <see cref="NoAdrenalineAccrualFacts"/> 之后
        /// 即便有真实的伤害提交，<c>Available</c> 也必须一位不变。
        ///
        /// 会让它失败的实现缺陷：装配点忽略显式注入（总是用本场真实现）⇒ 负控制下仍然入账 ⇒ 红。
        /// </summary>
        [Test]
        public void NegativeControlAccrualSourceLeavesAvailableUntouched()
        {
            Rig rig = Arrange(new BattleSimulationAssembly(
                adrenalineAccrualFactSource: NoAdrenalineAccrualFacts.Instance));
            BattleSimulation sim = rig.Sim;

            Assert.That(sim.LastDamageCommitReport.Units.Count, Is.EqualTo(1), "伤害提交本身仍发生");
            Assert.That(HealthQ10Of(sim, Monster),
                Is.EqualTo(rig.MonsterHealthBefore - (int)ExpectedDamageQ10));
            Assert.That(sim.LastAdrenalineAccrualFacts.Count, Is.EqualTo(0),
                "负控制来源不给任何事实（只读观察面必须如实反映）");
            Assert.That(AdrenalineOf(sim, Hero), Is.EqualTo(rig.HeroAdrenalineBefore));
            Assert.That(AdrenalineOf(sim, Monster), Is.EqualTo(rig.MonsterAdrenalineBefore));
        }

        // =====================================================================
        // B2：每单位每 Tick 至多一条（可失败用例）
        // =====================================================================

        /// <summary>
        /// <strong>同一单位在同一 Tick 有多条命中时，事实仍然只有一条</strong>，
        /// 且"造成方"必须按聚合结果记（AOE 形态：一个攻击者打两个目标）。
        ///
        /// 会让它失败的实现缺陷：事实来源按"接触/命中"逐条 append（这是最自然的错误实现）⇒
        /// 同一单位出现两条 <c>AdrenalineAccrualFacts</c> ⇒ 本用例红，
        /// 而且任务 07 的唯一入口会以 <c>ADRENALINE_LEDGER_INVARIANT</c> 抛异常（重复键）。
        /// </summary>
        [Test]
        public void AccrualFactSourceProducesAtMostOneEntryPerUnitPerTick()
        {
            DamageCommitReport report = new DamageCommitReport(ImpactTick, "COMMITTED",
                new[]
                {
                    new UnitDamageCommit(new UnitId(2L), 300L, 10L, 0, null, new ActionPlanId(1L), 1L),
                    new UnitDamageCommit(new UnitId(3L), 500L, 20L, 0, null, new ActionPlanId(1L), 1L)
                },
                Array.Empty<ClashTerminalRequest>());

            // 同一个攻击计划对两个目标各有一条未消解命中（AOE 形态）。
            StagedConflictResolution resolution = ResolutionStub(
                ImpactTick, 1L,
                remainingHits: new[]
                {
                    RemainingHit(1L, 1L, 2L, 300L),
                    RemainingHit(1L, 1L, 3L, 500L)
                });

            var source = new AdrenalineAccrualFactSource(() => resolution, () => report);
            IReadOnlyList<AdrenalineAccrualFacts> facts = source.BuildAccrualFactsOrdered(ImpactTick);

            Assert.That(facts.Count, Is.EqualTo(3), "三个单位各一条，而不是四条（按命中数）");
            Assert.That(facts.Select(f => f.UnitId).ToArray(),
                Is.EqualTo(new[] { new UnitId(1L), new UnitId(2L), new UnitId(3L) }),
                "必须按 UnitId 严格升序");

            AdrenalineAccrualFacts attacker = facts[0];
            Assert.That(attacker.TotalFinalDamageDealtQ10, Is.EqualTo(800),
                "造成方一次记满本 Tick 对全部目标的最终伤害之和");
            Assert.That(attacker.TotalFinalDamageReceivedQ10, Is.EqualTo(0));
            Assert.That(facts[1].TotalFinalDamageReceivedQ10, Is.EqualTo(300));
            Assert.That(facts[2].TotalFinalDamageReceivedQ10, Is.EqualTo(500));

            // 幂等：第二次构建逐位相同（只读消费者，不产生"第二份累积"）。
            IReadOnlyList<AdrenalineAccrualFacts> again = source.BuildAccrualFactsOrdered(ImpactTick);
            Assert.That(Dump(again), Is.EqualTo(Dump(facts)));
        }

        // =====================================================================
        // B3：失败反应不得获得成功奖励（可失败用例）
        // =====================================================================

        /// <summary>
        /// <strong>失败的 Block 不生成成功事实</strong>：
        /// <c>BlockIneffective</c>（完全没有可降低载荷）必须 <c>SuccessfulBlockCount == 0</c>，
        /// 并且事件侧的 <c>RewardsBlock</c>/<c>RewardsSuccess</c> 也必须是 false。
        ///
        /// 让人格面可判读：受保护单位本 Tick <strong>仍被扣了血</strong>（同一单位同时有一条
        /// 绕过格挡的伤害提交 ⇒ 它一定会有一条事实条目），因此本用例的
        /// <c>SuccessfulBlockCount</c> 断言不会被"该单位根本没有事实条目"掩盖。
        ///
        /// 会让它失败的实现缺陷：把"计划存在"当成"成功"（只为 <c>BlockPlans</c> 非空就 +1）、
        /// 或按 <c>Contacts.Count</c> 计数而不看 <c>RewardsBlock</c> ⇒ 红。
        /// </summary>
        [Test]
        public void FailedBlockProducesNoSuccessFact()
        {
            var blockPlan = new BlockPlanResolution(new ActionPlanId(9L), new UnitId(2L),
                new[]
                {
                    new BlockContactResolution(
                        StubContactKey(ContactType.AttackBlock, 1L, 7L, 2L, 9L),
                        new ActionPlanId(7L), new UnitId(1L), new ActionPlanId(9L), new UnitId(2L),
                        BlockContactOutcome.BlockIneffective, 10240L, 10240L, 1000, 1000)
                });

            StagedConflictResolution resolution = ResolutionStub(ImpactTick, 0L, blockPlans: new[] { blockPlan });
            DamageCommitReport report = new DamageCommitReport(ImpactTick, "COMMITTED",
                new[] { new UnitDamageCommit(new UnitId(2L), 700L, 1000L, 0, null, new ActionPlanId(7L), 0L) },
                Array.Empty<ClashTerminalRequest>());
            var source = new AdrenalineAccrualFactSource(() => resolution, () => report);

            IReadOnlyList<AdrenalineAccrualFacts> facts = source.BuildAccrualFactsOrdered(ImpactTick);

            Assert.That(blockPlan.AnyEligibleContact, Is.False, "夹具前提：无效格挡计划");
            Assert.That(facts.Count, Is.EqualTo(1), "只有受保护单位有一条事实（它确实被扣了血）");
            Assert.That(facts[0].UnitId, Is.EqualTo(new UnitId(2L)));
            Assert.That(facts[0].TotalFinalDamageReceivedQ10, Is.EqualTo(700));
            Assert.That(facts[0].SuccessfulBlockCount, Is.EqualTo(0),
                "无效格挡**不得**产生成功 Block 奖励（失败反应零奖励）");
            Assert.That(facts[0].SuccessfulDodgeCount, Is.EqualTo(0));
            Assert.That(facts[0].ClashSuccessCount, Is.EqualTo(0));
        }

        /// <summary>
        /// <strong>未提交的 Dodge 不生成成功事实</strong>：位置未提交（<c>FailureCode</c> 非空）
        /// 时即便四格表里出现了"旧格成立"的接触也不能算成功——不伪造触发。
        ///
        /// 会让它失败的实现缺陷：只看 <c>AvoidedAnyContact</c> 而忽略 <c>DestinationCommitted</c> ⇒ 红。
        /// </summary>
        [Test]
        public void UncommittedDodgeProducesNoSuccessFact()
        {
            var dodgePlan = new DodgePlanResolution(
                new ActionPlanId(9L), new UnitId(2L), new GridPoint(0, 0), new GridPoint(2, 0),
                DestinationCommitted: false, FailureCode: "DODGE_DESTINATION_REJECTED",
                Contacts: new[]
                {
                    new DodgeContactResolution(
                        StubContactKey(ContactType.AttackDodge, 1L, 7L, 2L, 9L),
                        new ActionPlanId(7L), new UnitId(1L), new ActionPlanId(9L), new UnitId(2L),
                        CoveredBefore: true, CoveredAfter: false, DestinationCommitted: false,
                        FailureCode: "DODGE_DESTINATION_REJECTED", Outcome: DodgeContactOutcome.Dodged)
                });

            StagedConflictResolution resolution = ResolutionStub(ImpactTick, 0L, dodgePlans: new[] { dodgePlan });
            DamageCommitReport report = new DamageCommitReport(ImpactTick, "COMMITTED",
                new[] { new UnitDamageCommit(new UnitId(2L), 640L, 1000L, 0, null, new ActionPlanId(7L), 0L) },
                Array.Empty<ClashTerminalRequest>());
            var source = new AdrenalineAccrualFactSource(() => resolution, () => report);

            IReadOnlyList<AdrenalineAccrualFacts> facts = source.BuildAccrualFactsOrdered(ImpactTick);

            Assert.That(dodgePlan.AvoidedAnyContact, Is.True, "夹具前提：四格表确实出现了失效接触");
            Assert.That(dodgePlan.DestinationCommitted, Is.False, "夹具前提：位置未提交");
            Assert.That(facts.Count, Is.EqualTo(1));
            Assert.That(facts[0].TotalFinalDamageReceivedQ10, Is.EqualTo(640),
                "位置未提交 ⇒ 该攻击照常命中（伤害仍在）");
            Assert.That(facts[0].SuccessfulDodgeCount, Is.EqualTo(0),
                "未提交位置的 Dodge **不得**获得成功奖励（不伪造触发）");
        }

        /// <summary>
        /// <strong>已提交但没有任何接触失效的 Dodge 不生成成功事实</strong>
        /// （"新旧都不命中的攻击不能算成功避开"）。
        ///
        /// 会让它失败的实现缺陷：把"提交成功"当"回避成功" ⇒ 红。
        /// </summary>
        [Test]
        public void CommittedDodgeWithoutInvalidationProducesNoSuccessFact()
        {
            var dodgePlan = new DodgePlanResolution(
                new ActionPlanId(9L), new UnitId(2L), new GridPoint(0, 0), new GridPoint(2, 0),
                DestinationCommitted: true, FailureCode: null,
                Contacts: new[]
                {
                    new DodgeContactResolution(
                        StubContactKey(ContactType.AttackDodge, 1L, 7L, 2L, 9L),
                        new ActionPlanId(7L), new UnitId(1L), new ActionPlanId(9L), new UnitId(2L),
                        CoveredBefore: true, CoveredAfter: true, DestinationCommitted: true,
                        FailureCode: null, Outcome: DodgeContactOutcome.StillHit)
                });

            StagedConflictResolution resolution = ResolutionStub(ImpactTick, 0L, dodgePlans: new[] { dodgePlan });
            DamageCommitReport report = new DamageCommitReport(ImpactTick, "COMMITTED",
                new[] { new UnitDamageCommit(new UnitId(2L), 900L, 1000L, 0, null, new ActionPlanId(7L), 0L) },
                Array.Empty<ClashTerminalRequest>());
            var source = new AdrenalineAccrualFactSource(() => resolution, () => report);

            IReadOnlyList<AdrenalineAccrualFacts> facts = source.BuildAccrualFactsOrdered(ImpactTick);

            Assert.That(dodgePlan.DestinationCommitted, Is.True);
            Assert.That(dodgePlan.AvoidedAnyContact, Is.False, "夹具前提：换位没有让任何接触失效");
            Assert.That(facts.Count, Is.EqualTo(1));
            Assert.That(facts[0].SuccessfulDodgeCount, Is.EqualTo(0),
                "换位没有救下任何攻击 ⇒ 不是成功回避");
        }

        /// <summary>
        /// <strong>成功的 Dodge 才产生成功键</strong>：与上面三条构成正/负对照
        /// （否则"永远为 0"的实现也能通过全部负例）。
        ///
        /// 会让它失败的实现缺陷：把成功键写成恒 0（怕多算就干脆不算）⇒ 本用例红。
        /// </summary>
        [Test]
        public void CommittedDodgeWithInvalidationGrantsExactlyOneSuccessKey()
        {
            var dodgePlan = new DodgePlanResolution(
                new ActionPlanId(9L), new UnitId(2L), new GridPoint(0, 0), new GridPoint(2, 0),
                DestinationCommitted: true, FailureCode: null,
                Contacts: new[]
                {
                    new DodgeContactResolution(
                        StubContactKey(ContactType.AttackDodge, 1L, 7L, 2L, 9L),
                        new ActionPlanId(7L), new UnitId(1L), new ActionPlanId(9L), new UnitId(2L),
                        CoveredBefore: true, CoveredAfter: false, DestinationCommitted: true,
                        FailureCode: null, Outcome: DodgeContactOutcome.Dodged),
                    new DodgeContactResolution(
                        StubContactKey(ContactType.AttackDodge, 3L, 8L, 2L, 9L),
                        new ActionPlanId(8L), new UnitId(3L), new ActionPlanId(9L), new UnitId(2L),
                        CoveredBefore: true, CoveredAfter: false, DestinationCommitted: true,
                        FailureCode: null, Outcome: DodgeContactOutcome.Dodged)
                });

            StagedConflictResolution resolution = ResolutionStub(ImpactTick, 0L, dodgePlans: new[] { dodgePlan });
            var source = new AdrenalineAccrualFactSource(
                () => resolution, () => DamageCommitReport.Empty(ImpactTick));

            IReadOnlyList<AdrenalineAccrualFacts> facts = source.BuildAccrualFactsOrdered(ImpactTick);

            Assert.That(facts.Count, Is.EqualTo(1));
            Assert.That(facts[0].UnitId, Is.EqualTo(new UnitId(2L)));
            Assert.That(facts[0].SuccessfulDodgeCount, Is.EqualTo(1),
                "两条失效接触属于**同一个反应计划** ⇒ 仍然是 1 个成功键");
        }

        // =====================================================================
        // B4：每反应计划至多一次成功键 / 每单位每 ConflictGroup 至多一个 Clash 键
        // =====================================================================

        /// <summary>
        /// <strong>同一个 Block 计划面对两条合格接触只加一次</strong>，
        /// 且<strong>同一单位在两场不同的 Clash 里各算一次</strong>（"每单位每 ConflictGroup 至多一个"）。
        ///
        /// 会让它失败的实现缺陷：按接触计数（成功 Block 变成 2）、
        /// 或把 Clash 成功摊平成"每参与记录一次"（同一组两个连通块 ⇒ 2）⇒ 红。
        /// </summary>
        [Test]
        public void SuccessKeysAreDeduplicatedPerPlanAndPerConflictGroup()
        {
            var blockPlan = new BlockPlanResolution(new ActionPlanId(9L), new UnitId(6L),
                new[]
                {
                    new BlockContactResolution(
                        StubContactKey(ContactType.AttackBlock, 5L, 8L, 6L, 9L),
                        new ActionPlanId(8L), new UnitId(5L), new ActionPlanId(9L), new UnitId(6L),
                        BlockContactOutcome.Blocked, 1024L, 0L, 1000, 0),
                    new BlockContactResolution(
                        StubContactKey(ContactType.AttackBlock, 4L, 7L, 6L, 9L),
                        new ActionPlanId(7L), new UnitId(4L), new ActionPlanId(9L), new UnitId(6L),
                        BlockContactOutcome.PartiallyBlocked, 1024L, 512L, 1000, 500)
                });

            // 同一单位 (6) 在**同一个冲突组**(100) 的两个连通块里各参战一次 ⇒ 只算 1 个 Clash 成功键；
            // 在另一个冲突组 (200) 再参战一次 ⇒ 再算 1 个。
            StagedConflictResolution resolution = ResolutionStub(ImpactTick, 0L,
                blockPlans: new[] { blockPlan },
                clashes: new[]
                {
                    ClashStub(100L, new UnitId(6L)),
                    ClashStub(100L, new UnitId(6L)),
                    ClashStub(200L, new UnitId(6L))
                });

            var source = new AdrenalineAccrualFactSource(
                () => resolution,
                () => new DamageCommitReport(ImpactTick, "COMMITTED",
                    new[] { new UnitDamageCommit(new UnitId(6L), 120L, 800L, 0, null, new ActionPlanId(8L), 0L) },
                    Array.Empty<ClashTerminalRequest>()));
            IReadOnlyList<AdrenalineAccrualFacts> facts = source.BuildAccrualFactsOrdered(ImpactTick);

            AdrenalineAccrualFacts unit6 = facts.Single(f => f.UnitId == new UnitId(6L));
            Assert.That(unit6.SuccessfulBlockCount, Is.EqualTo(1),
                "每反应计划至多一次成功 Block 键（两条合格接触 ⇒ 仍是 1）");
            Assert.That(unit6.SuccessfulDodgeCount, Is.EqualTo(0));
            Assert.That(unit6.ClashSuccessCount, Is.EqualTo(2),
                "同一冲突组的两个连通块只算一次；两个不同组各算一次");
        }

        // =====================================================================
        // C2：构建器的重复键拒绝（可失败用例）
        // =====================================================================

        /// <summary>
        /// <strong>同一单位出现两条提交条目 ⇒ 稳定拒绝</strong>，绝不"按顺序挑一个"。
        ///
        /// 会让它失败的实现缺陷：构建器只做"遍历 + Add"（把唯一性完全托付给上游）⇒
        /// 本用例不抛 ⇒ 红；或抛非稳定码 ⇒ 红。
        /// </summary>
        [Test]
        public void DuplicateUnitCommitIsRejectedWithStableCode()
        {
            DamageCommitReport report = new DamageCommitReport(ImpactTick, "COMMITTED",
                new[]
                {
                    new UnitDamageCommit(new UnitId(2L), 100L, 1000L, 1, GridDirection.West, new ActionPlanId(1L), 1L),
                    new UnitDamageCommit(new UnitId(2L), 50L, 1000L, 1, GridDirection.West, new ActionPlanId(1L), 1L)
                },
                Array.Empty<ClashTerminalRequest>());

            var builder = new DisplacementRequestBuilder(() => report);
            LogicDefinitionException error = Assert.Throws<LogicDefinitionException>(
                () => builder.BuildOrdered(ImpactTick, Array.Empty<UnitSnapshot>()));
            Assert.That(error.ErrorCode, Is.EqualTo(SimulationCodes.STEP_DISPLACEMENT_REQUEST_DUPLICATE));
        }

        /// <summary>
        /// <strong>零击退不产生请求</strong>（含"反向合力互相抵消"）：<c>KnockbackSteps == 0</c>
        /// 或没有合力方向时不得伪造请求。
        ///
        /// 会让它失败的实现缺陷：无条件为每个提交条目生成请求 ⇒ 阶段 12 会多出零步结果与事件、
        /// 甚至把 <c>ResultantDirection == null</c> 当成 East ⇒ 红。
        /// </summary>
        [Test]
        public void ZeroKnockbackProducesNoRequest()
        {
            DamageCommitReport report = new DamageCommitReport(ImpactTick, "COMMITTED",
                new[]
                {
                    new UnitDamageCommit(new UnitId(2L), 100L, 1000L, 0, GridDirection.West, new ActionPlanId(1L), 1L),
                    new UnitDamageCommit(new UnitId(3L), 100L, 1000L, 2, null, new ActionPlanId(1L), 1L),
                    new UnitDamageCommit(new UnitId(4L), 100L, 1000L, 2, GridDirection.East, new ActionPlanId(1L), 1L)
                },
                Array.Empty<ClashTerminalRequest>());

            var builder = new DisplacementRequestBuilder(() => report);
            IReadOnlyList<ForcedDisplacementRequest> requests =
                builder.BuildOrdered(ImpactTick, Array.Empty<UnitSnapshot>());

            Assert.That(requests.Count, Is.EqualTo(1), "只有一条真正有方向且步数 > 0 的请求");
            Assert.That(requests[0].TargetUnitId, Is.EqualTo(new UnitId(4L)));
            Assert.That(requests[0].Direction, Is.EqualTo(GridDirection.East));
            Assert.That(requests[0].RequestedSteps, Is.EqualTo(2));
            Assert.That(requests[0].ConflictGroupKey, Is.EqualTo(1L));
        }

        // =====================================================================
        // C3：请求数值与聚合结果逐位一致（请求不是第二份真值）
        // =====================================================================

        /// <summary>
        /// <strong>请求的步数/动量等于聚合结果，且位移真的落在合同允许的范围内</strong>：
        /// <c>RequestedSteps == Aggregate.KnockbackSteps</c>、
        /// <c>MomentumUnits == Aggregate.TotalImpactUnits</c>、<c>AppliedSteps &lt;= RequestedSteps</c>。
        ///
        /// 会让它失败的实现缺陷：构建器自己再算一次击退（例如用
        /// <c>ResultantMomentumUnits / ControlResistanceUnits</c> 得到另一个数）、
        /// 或把 <c>DamageQ10</c> 当动量填进请求 ⇒ 数值断言红。
        /// </summary>
        [Test]
        public void RequestNumbersEqualAggregateProduct()
        {
            Rig rig = Arrange();
            TargetAggregateResolution aggregate = rig.Sim.StagedResolution.RemainingHits[0].Aggregate;
            ForcedDisplacementRequest request = rig.Sim.LastBuiltDisplacementRequests.Single();

            Assert.That(request.RequestedSteps, Is.EqualTo(aggregate.KnockbackSteps));
            Assert.That(request.MomentumUnits, Is.EqualTo(aggregate.TotalImpactUnits));
            Assert.That(request.Direction, Is.EqualTo(aggregate.ResultantDirection.Value));
            Assert.That(request.RequestedSteps, Is.GreaterThan(0), "夹具前提：本构型确实有击退");

            ForcedDisplacementResolvedEvent moved = Single<ForcedDisplacementResolvedEvent>(rig.ImpactStep);
            Assert.That(moved.AppliedSteps, Is.LessThanOrEqualTo(moved.RequestedSteps),
                "实际步数不得超过请求步数");
            Assert.That(moved.AppliedSteps, Is.EqualTo(request.RequestedSteps),
                "无阻挡 ⇒ 请求步数全部落实");
        }

        // =====================================================================
        // 手工值对象辅助（B/C 段用例的输入面；不参与真实模拟）
        // =====================================================================

        private static string Dump(IReadOnlyList<AdrenalineAccrualFacts> facts)
            => string.Join(";", facts.Select(f =>
                f.UnitId.Value + ":" + f.TotalFinalDamageDealtQ10 + ":" + f.TotalFinalDamageReceivedQ10
                + ":" + f.SuccessfulBlockCount + ":" + f.SuccessfulDodgeCount + ":" + f.ClashSuccessCount));

        private static ContactKey StubContactKey(ContactType type, long firstUnit, long firstPlan,
            long secondUnit, long secondPlan)
            => ContactKey.Create(type, new UnitId(firstUnit), new ActionPlanId(firstPlan),
                new UnitId(secondUnit), new ActionPlanId(secondPlan), new UnitId(secondUnit));

        private static RemainingHitResolution RemainingHit(long attackPlan, long attackerUnit, long targetUnit,
            long damageQ10)
            => new RemainingHitResolution(
                StubContactKey(ContactType.AttackTarget, attackerUnit, attackPlan, targetUnit, attackPlan),
                new ActionPlanId(attackPlan), new UnitId(attackerUnit), AttackSpecId,
                GridDirection.East, 1000, Array.Empty<DamageComponentSpec>(), AttackTagMask.Reactable,
                default(ActionPlanId), null, true, false,
                AggregateStub(targetUnit, damageQ10));

        private static TargetAggregateResolution AggregateStub(long targetUnit, long damageQ10)
            => new TargetAggregateResolution(
                new UnitId(targetUnit),
                Array.Empty<TargetContactResolution>(),
                Array.Empty<AggregatedChannelDamage>(),
                damageQ10, damageQ10, 1000L, 0L, 0L, null, 0L, 1000,
                1000L, 1500L, false, false, 0);

        private static ClashComponentResolution ClashStub(long groupKey, UnitId participantUnit)
        {
            var participant = new ClashParticipantResolution(
                participantUnit, new ActionPlanId(participantUnit.Value * 10L), GridDirection.East,
                1000, 200L, 800, Array.Empty<ClashOppositionLoss>(), Array.Empty<ClashResidualImpact>());
            var clash = new MomentumClashResolution(
                new[] { participant }, Array.Empty<ClashResidualImpact>(),
                new[] { participant.ActionPlanId }, 800L, 0L);
            return new ClashComponentResolution(groupKey, clash);
        }

        private static StagedConflictResolution ResolutionStub(
            long tick,
            long groupKey,
            IReadOnlyList<DodgePlanResolution> dodgePlans = null,
            IReadOnlyList<BlockPlanResolution> blockPlans = null,
            IReadOnlyList<ClashComponentResolution> clashes = null,
            IReadOnlyList<RemainingHitResolution> remainingHits = null)
            => new StagedConflictResolution(tick, null, groupKey,
                Array.Empty<DodgeContactResolution>(),
                dodgePlans ?? Array.Empty<DodgePlanResolution>(),
                blockPlans ?? Array.Empty<BlockPlanResolution>(),
                clashes ?? Array.Empty<ClashComponentResolution>(),
                Array.Empty<MoveContactResolution>(),
                remainingHits ?? Array.Empty<RemainingHitResolution>());
    }
}
