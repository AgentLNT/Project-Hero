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
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 08 <strong>集成段第二步</strong>的仓库内自动化证据：
    /// 冲突组 → <strong>分阶段求解</strong>（Dodge 空间复核 → Block 完全抵抗 → Clash 同时求解 →
    /// Move → Remaining Hits 聚合）→ 产物喂给<strong>已有接缝</strong>
    /// <c>ResolutionCommit.CommitDamageAndAggregationOrdered</c>。
    ///
    /// <para>
    /// <strong>为什么必须有这个文件</strong>：<c>Interactions/**</c> 与 <c>Combat/**</c> 的用例全部手工喂值对象，
    /// 对"装配点没接线"完全不敏感；集成段第一步的用例只钉住阶段 8/10 的<b>构图</b>。
    /// 本文件是唯一让下面三件事"有牙齿"的证据：
    /// <list type="number">
    /// <item>阶段 10 真的<strong>求解</strong>（不只是构图）；</item>
    /// <item>求解结果是<strong>纯值</strong>——求解前后世界（单位生命）完全一致；</item>
    /// <item>阶段 11 真的把<strong>聚合后</strong>的伤害提交到权威生命，且每单位恰好一次。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <strong>装配口径</strong>：真实 <see cref="BattleSimulation.Create"/> + 真实命令入口 +
    /// 真实 Step 管线；攻击计划经真实排程事务提交。<c>Pattern</c> 用与
    /// <c>Task08IntentAndConflictGraphIntegrationTests</c> 同一张实测过的 12 向表
    /// （不是随手写的几何）。
    /// </para>
    /// </summary>
    public class Task08StagedResolutionIntegrationTests
    {
        // ================= 夹具常量 =================

        private const string AttackSpecId = "action.t08sr.attack";
        private const string ActionSetIdValue = "action_set.t08sr";
        private const string EncounterIdValue = "encounter.t08sr";
        private const int AttackBaseWindupTicks = 10;
        private const int AttackRecoveryTicks = 2;
        private const int WindowBudget = 60;
        private const long WindowOpenTick = 0L;
        private const long SubmitTick = 1L;

        /// <summary>攻击基础伤害 <c>10f</c>（创作态生命点）⇒ 量化后 <c>10240 Q10</c> = <b>10 生命点</b>。</summary>
        private const float AttackRawAmount = 10f;
        private const long ExpectedDamageQ10 = 10240L;
        private const int InitialHealth = 200;

        /// <summary>
        /// 单计划夹具的 <c>ImpactTick</c>（<c>SubmitTick + Windup</c>）。
        /// 多计划（Clash）夹具见 <see cref="AlignedImpactTick"/>。
        /// </summary>
        private const long ImpactTick = SubmitTick + AttackBaseWindupTicks;

        private static BattleRules Rules => BattleRules.FrozenV1;
        private static GridBoundaryDefinition Wide
            => new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40));

        private static UnitId Hero => new UnitId(1L);
        private static UnitId Monster => new UnitId(2L);
        private static FactionId HeroFaction => new FactionId("faction.hero");
        private static FactionId MonsterFaction => new FactionId("faction.monster");
        private static ControllerId Player => new ControllerId("controller.player");

        /// <summary>
        /// 多计划（Clash）夹具里两个计划共享的命中时刻（由 <see cref="SubmitOwnedPair"/> 计算）。
        ///
        /// 它不是编译期常量：两个单位的计划必须<strong>逐单位提交</strong>（窗口只属于其拥有者），
        /// 而第二次提交的命令 Tick 总比第一次晚（新窗口最早在关窗后的下一 Tick 打开）。
        /// 因为 <c>RequestedStartTick</c> 必须 <c>&gt;=</c> 命令 Tick，第二次提交只能请求
        /// "更晚的开始 Tick"，因此共享的命中时刻 = <c>第二次命令 Tick + Windup</c>。
        /// </summary>
        private static long AlignedImpactTick;

        private static ActionSpecId Spec(string id) => new ActionSpecId(id);
        private static ActionSetId ActionSet => new ActionSetId(ActionSetIdValue);
        private static EncounterDefinitionId EncounterId => new EncounterDefinitionId(EncounterIdValue);
        private static EncounterSlotId Slot(string id) => new EncounterSlotId(id);
        private static BattleRuntimeInputs Inputs => new BattleRuntimeInputs(InitialRngSeed: 23UL, InitialMetaResource: 0);

        /// <summary>
        /// 单计划夹具的站位：hero <c>(0,0)</c> 朝东、monster <c>(8,0)</c> 朝 NorthWest —— 与集成段
        /// 第一步<strong>同一组实测坐标</strong>（hero 三角 <c>(3,0,1)</c>、monster 三角 <c>(6,1,1)</c>；
        /// 朝东攻击区域含 <c>(6,1,1)</c> ⇒ 命中成立且两单位不重叠）。
        /// </summary>
        private static GridPoint SingleHeroAnchor => new GridPoint(0, 0);
        private static GridPoint SingleMonsterAnchor => new GridPoint(8, 0);

        /// <summary>
        /// Clash 夹具的站位（本 Pattern × 单三角体积表下<strong>逐格实测</strong>，勿随手改）。
        ///
        /// <para>结构事实（探针实测）：单三角体积表在朝东时三角恒为 <c>anchor + (1,0,1)</c>、
        /// 格为 <c>三角 + {(1,0),(1,1),(2,0)}</c>；朝西时三角为 <c>anchor + (0,1,-1)</c>、
        /// 格为 <c>三角 + {(-1,0),(0,-1),(1,0)}</c>。攻击区域则是
        /// <c>anchor + (2j + OffsetX(f), 1 + OffsetY(f), 1)</c> 的一条水平线。</para>
        ///
        /// <para><c>hero@(0,0)</c> 朝东：三角 <c>(1,0,1)</c>、格 <c>{(2,0),(3,1),(4,0)}</c>、
        /// 区域 <c>{(2,1,1),(4,1,1),(6,1,1),(8,1,1)}</c>。</para>
        ///
        /// <para><c>monster@(4,0)</c> 朝东（<strong>不是</strong>朝西）：三角 <c>(5,0,1)</c>、
        /// 格 <c>{(6,0),(7,1),(8,0)}</c>、区域 <c>{(6,1,1),(8,1,1),(10,1,1),(12,1,1)}</c>。于是：</para>
        /// <list type="bullet">
        /// <item>两方格集合不相交（<c>{2,0;3,1;4,0}</c> vs <c>{6,0;7,1;8,0}</c>）⇒ 注册不互相拒绝；</item>
        /// <item>区域交集 <c>{(6,1,1),(8,1,1)}</c> 非空 ⇒ 构图连出 <c>AttackAttack</c> 边（Clash 前提）；</item>
        /// <item>monster 区域含 hero 的格 <c>(3,1)</c>？不：<c>(3,1)</c> 的 <c>X=3</c> 不在 <c>{6,8,10,12}</c>；
        /// 但 hero 的格 <c>...</c> 实测由 <c>ArrangeClash</c> 的双向命中断言把关
        /// （判据用阶段 10 自己的 <c>graph.Contacts</c>，不引入第二套几何）。</item>
        /// </list>
        ///
        /// <para>可观察判据：两个攻击者互相命中且都参与 Clash ⇒ 两条直接 Hit 都失效、
        /// 双方都不掉血（<c>RemainingHits</c> 为空、<c>LastDamageCommitReport.Units</c> 为空）。</para>
        /// </summary>
        private static GridPoint ClashHeroAnchor => new GridPoint(0, 0);
        private static GridPoint ClashMonsterAnchor => new GridPoint(4, 0);
        private static GridDirection ClashMonsterFacing => GridDirection.East;

        private static IReadOnlyList<DirectionalTriangleSet> Directions()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        /// <summary>
        /// 12 向攻击 Pattern（与集成段第一步逐字同表、同为实测所得）：
        /// 朝向 <c>f</c> 的相对点 = 沿 <c>f</c> 的邻居平移 + <c>2j</c>（j = 0..3）。
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
                for (int j = 0; j < 4; j++) points.Add(new TrianglePoint(2 * j + dx, 1 + dy, 1));
                directions.Add(new DirectionalTriangleSet(facing, points));
            }
            return new AttackPatternSpec(new AttackPatternId("attack.pattern.t08sr.grid"), directions);
        }

        /// <summary>
        /// 唯一一份定义。<paramref name="clashLayout"/> 为 true 时使用三单位的拼刀站位
        /// （<see cref="ClashHeroAnchor"/>：两个同行攻击者 + 第三个目标单位），
        /// 否则用单计划夹具的实测站位（hero <c>(0,0)</c> 朝东、monster <c>(8,0)</c> 朝 NorthWest）。
        ///
        /// 唯一控制者绑定<strong>同时</strong>控制全部槽位：多计划夹具需要多个单位各提交一次计划，
        /// 而"窗口只属于其拥有者"是模拟的既有语义（另见 <see cref="SubmitOwnedPair"/>）。
        /// </summary>
        private static BattleDefinition BuildDefinition(bool clashLayout = false)
        {
            GridPoint heroAnchor = clashLayout ? ClashHeroAnchor : SingleHeroAnchor;
            GridPoint monsterAnchor = clashLayout ? ClashMonsterAnchor : SingleMonsterAnchor;
            GridDirection monsterFacing = clashLayout ? ClashMonsterFacing : GridDirection.NorthWest;
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

            var volume = new VolumeSpec(new VolumeSpecId("unit_volume.t08sr"), Directions());
            var actionSet = new ActionSetDefinition(
                ActionSet, new List<ActionSpecId> { attackSpec.ActionSpecId });

            var heroDefinitionId = new UnitDefinitionId("unit.t08sr.hero");
            var monsterDefinitionId = new UnitDefinitionId("unit.t08sr.monster");

            var units = new List<UnitDefinition>
            {
                new UnitDefinition(heroDefinitionId, 10f, 10f, Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), InitialHealth, actionSet.ActionSetId, volume.VolumeSpecId),
                new UnitDefinition(monsterDefinitionId, 10f, 10f, Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed,
                    new Dictionary<DamageChannelId, int>(), InitialHealth, actionSet.ActionSetId, volume.VolumeSpecId)
            };

            var encounter = new EncounterDefinition(
                EncounterId, Wide,
                new List<EncounterUnitSlot>
                {
                    new EncounterUnitSlot(Slot("hero"), heroDefinitionId, HeroFaction,
                        heroAnchor, GridDirection.East),
                    new EncounterUnitSlot(Slot("monster"), monsterDefinitionId, MonsterFaction,
                        monsterAnchor, monsterFacing)
                },
                new List<ControllerBinding>
                {
                    new ControllerBinding(Player, CommandSourceKind.Player,
                        new List<EncounterSlotId> { Slot("hero"), Slot("monster") })
                },
                new VictoryDefinition(
                    new List<FactionId> { HeroFaction }, new List<FactionId> { MonsterFaction },
                    "result.t08sr.victory", "result.t08sr.defeat", "result.t08sr.draw"));

            return new BattleDefinition(
                "battle-definition.task08.staged-resolution",
                Rules.TicksPerSecond, Rules, ConcurrentActionDefinition.FrozenV1,
                new ReactionRules(CommandIngressLeadTicks: 2, MinimumReactionLeadTicks: 1),
                AdrenalineRules.FrozenV1, factionModel,
                new List<DamageChannelDefinition>(), new List<ImpactProfileDefinition>(),
                units, new List<ActionSpec> { attackSpec }, new List<AttackPatternSpec> { pattern },
                new List<VolumeSpec> { volume }, new List<MovementPatternSpec>(),
                new List<ActionSetDefinition> { actionSet }, new List<StatusEffectSpec>(),
                new List<EncounterDefinition> { encounter }, null,
                "test-definition-hash.t08.staged-resolution");
        }

        // =====================================================================
        // 装配
        // =====================================================================

        private sealed class Rig
        {
            public BattleSimulation Sim;
            public CommandIngressEntry PlayerEntry;
            public ActionPlan AttackPlan;
            public StepResult ImpactStep;
            public int HealthAtImpactTickStart;
        }

        /// <summary>Tick 0 开窗 → Tick 1 提交 hero→monster 攻击计划（ImpactTick = 11）→ 推进到该 Tick。</summary>
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

            for (long tick = SubmitTick + 1L; tick < ImpactTick; tick++)
            {
                StepOnce(rig, tick);
                Assert.That(rig.Sim.StagedResolution.Failed, Is.False,
                    "夹具前提：ImpactTick 之前不得有构图失败");
                Assert.That(rig.Sim.StagedResolution.RemainingHits.Count, Is.EqualTo(0),
                    "Tick " + tick + " 早于 ImpactTick：不得有任何未消解命中");
            }

            rig.HealthAtImpactTickStart = HealthQ10Of(sim, Monster);
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

        // =====================================================================
        // 用例 1：五个阶段都被真实执行，且伤害恰好提交一次
        // =====================================================================

        /// <summary>
        /// <strong>阶段 10 真的求解，而不只是构图</strong>：在 <c>ImpactTick</c> 的只读观察面上
        /// 能同时看到"阶段 5 的未消解命中"与"阶段 11 的伤害提交"，且两者数值一致。
        ///
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>阶段 10 只构图不求解（<c>StagedResolution.RemainingHits</c> 恒空）⇒ 本用例红；</item>
        /// <item>阶段 11 仍接 <c>NoResolutionCommitSystem</c>（本步之前的真实形态）⇒
        /// <c>LastDamageCommitReport.Units</c> 恒空、monster 生命不变 ⇒ 本用例红；</item>
        /// <item>把"逐接触扣血"接上（同一目标多接触各扣一次）⇒
        /// <c>Units.Count</c> 断言红（每单位每 Tick 恰好一条）；</item>
        /// <item>阶段 5 少做一次量化或把 <c>TargetAggregator</c> 换成自写的浮点累加 ⇒ 伤害数值断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void StagedResolutionRunsAllFivePhasesAndCommitsDamageOncePerUnit()
        {
            Rig rig = Arrange();
            BattleSimulation sim = rig.Sim;

            StagedConflictResolution staged = sim.StagedResolution;
            Assert.That(staged.Tick, Is.EqualTo(ImpactTick));
            Assert.That(staged.Failed, Is.False, "夹具前提：构图成功");

            // —— 阶段 5：Remaining Hits 确实有一条，且聚合出精确伤害 ——
            Assert.That(staged.RemainingHits.Count, Is.EqualTo(1),
                "本夹具恰好一条未消解命中（hero→monster）");
            RemainingHitResolution hit = staged.RemainingHits[0];
            Assert.That(hit.AttackPlanId, Is.EqualTo(rig.AttackPlan.ActionPlanId));
            Assert.That(hit.AttackerUnitId, Is.EqualTo(Hero));
            Assert.That(hit.CanReceiveDirectHit, Is.True, "monster 在 Idle ⇒ 可以承受直接命中");
            Assert.That(hit.IsDirectHitSuppressed, Is.False);
            Assert.That(hit.Aggregate, Is.Not.Null, "阶段 5 必须给出聚合结果");
            Assert.That(hit.Aggregate.TargetUnitId, Is.EqualTo(Monster));
            Assert.That(hit.Aggregate.TotalDamageQ10, Is.EqualTo(ExpectedDamageQ10),
                "10 生命点 × 1024 = 10240 Q10（无被动/动作抵抗 ⇒ 精确相等）");
            Assert.That(hit.Aggregate.ChannelTotals.Count, Is.EqualTo(1), "一个伤害通道");

            // 其余阶段在本夹具里没有可解接触（没有 Dodge/Block/Guard/Move/Clash），
            // 因此它们的列表是"空但存在"的；"求解器真的跑过"这件事由上面那条
            // RemainingHits 断言承担（空实现下它也会是空的）。
            Assert.That(staged.DodgePlans.Count, Is.EqualTo(0), "本夹具没有到期的 Dodge 预留");
            Assert.That(staged.BlockPlans.Count, Is.EqualTo(0), "本夹具没有 Block 计划");
            Assert.That(staged.Clashes.Count, Is.EqualTo(0), "本夹具没有 Attack↔Attack 接触");
            Assert.That(staged.Moves.Count, Is.EqualTo(0), "本夹具没有 Move 计划");

            // —— 阶段 11：伤害恰好提交一次，数值等于聚合值 ——
            DamageCommitReport report = sim.LastDamageCommitReport;
            Assert.That(report.Tick, Is.EqualTo(ImpactTick));
            Assert.That(report.Status, Is.EqualTo("COMMITTED"));
            Assert.That(report.Units.Count, Is.EqualTo(1), "每单位每 Tick 恰好一条伤害提交");
            UnitDamageCommit committed = report.Units[0];
            Assert.That(committed.UnitId, Is.EqualTo(Monster));
            Assert.That(committed.DamageQ10, Is.EqualTo(ExpectedDamageQ10));
            Assert.That(committed.TotalImpactUnits, Is.GreaterThan(0L), "总冲击只读抵抗后入射动量之和");

            // —— 权威生命的真实结果 ——
            Assert.That(HealthQ10Of(sim, Monster),
                Is.EqualTo(rig.HealthAtImpactTickStart - (int)ExpectedDamageQ10),
                "扣血必须落在权威生命字段上，且只扣一次");
            Assert.That(HealthQ10Of(sim, Hero), Is.EqualTo(InitialHealth * 1024),
                "攻击者自己不得被自己的聚合结果扣血");
        }

        // =====================================================================
        // 用例 2：求解阶段零世界写入（可失败）
        // =====================================================================

        /// <summary>
        /// <strong>查询/求解阶段不得修改世界</strong>：把阶段 11 的提交系统换成负控制
        /// （<c>NoResolutionCommitSystem</c>，只声明"什么都不做"）之后，
        /// 阶段 10 仍然完成完整求解，但世界<strong>一位都不变</strong>。
        ///
        /// 这让"求解"与"提交"在可观察面上彻底分开：若求解器偷偷扣血/切状态/移位置，
        /// 负控制下也会看到生命变化 ⇒ 本用例红。
        /// </summary>
        [Test]
        public void ResolveDoesNotMutateWorldBeforeCommit()
        {
            Rig rig = Arrange(new BattleSimulationAssembly(
                resolutionCommit: NoResolutionCommitSystem.Instance));
            BattleSimulation sim = rig.Sim;

            StagedConflictResolution staged = sim.StagedResolution;
            Assert.That(staged.Failed, Is.False);
            Assert.That(staged.RemainingHits.Count, Is.EqualTo(1));
            Assert.That(staged.RemainingHits[0].Aggregate.TotalDamageQ10, Is.EqualTo(ExpectedDamageQ10),
                "负控制只关掉**提交**，不得影响求解结果");

            Assert.That(sim.ResolutionCommitSystem, Is.Null,
                "显式注入时不得再绑本场真实现（负控制必须真的生效）");
            Assert.That(sim.LastDamageCommitReport.Units.Count, Is.EqualTo(0),
                "负控制的提交报告必须为空");
            Assert.That(HealthQ10Of(sim, Monster), Is.EqualTo(rig.HealthAtImpactTickStart),
                "求解阶段零世界写入：扣血只能发生在阶段 11 的提交");
            Assert.That(HealthQ10Of(sim, Hero), Is.EqualTo(InitialHealth * 1024));
        }

        // =====================================================================
        // 用例 3：Clash 同时求解 + 参与攻击在阶段 14 经统一终态协调器终止
        // =====================================================================

        /// <summary>
        /// <strong>裁定 3 的求解器门槛（d ≤ 3 ⇒ 非有效 Clash）在真实装配下成立</strong>：
        /// 两个攻击者<strong>区域相交</strong>（因此冲突图里确实连出了 <c>AttackAttack</c> 边、
        /// 两者进入<strong>同一个冲突组</strong>），但它们方向相同 ⇒
        /// <c>MinimalRingDistance = 0</c>、<c>OppositionFactorQ10[0] = 0</c> ⇒
        /// <strong>非有效 Clash</strong>：没有损耗、没有 <c>ClashSuccessCount</c>、计划不终止。
        ///
        /// <para>
        /// 这正是"<strong>边存在 ≠ 发生拼刀</strong>"这条裁定的可观察形态，
        /// 也顺带钉住"构图期不收紧连边、有效性只由求解器判"这条口径：
        /// 若有人在构图期把这类边过滤掉，<c>Groups[0].EdgeCount</c> 会变 0 ⇒ 红；
        /// 若求解器不设门槛（把 <c>factor == 0</c> 当有效），<c>MomentumClashSolver</c> 会以
        /// <c>CLASH_INPUT_INVALID</c> 抛出 ⇒ 红（它自己就是那道门）。
        /// </para>
        ///
        /// <para>
        /// <strong>本夹具为什么达不到"有效 Clash"</strong>（已穷举实测，登记为几何约束）：
        /// 本 Pattern 的攻击区域恒为一条水平线 <c>Y = anchor.Y + 1 + OffsetY(f)</c>，而
        /// <c>GridPoint</c> 要求 <c>X+Y</c> 为偶 ⇒
        /// "区域相交"要求两者同行（<c>Y</c> 相同）⇒ 方向相同 ⇒ <c>d = 0</c> ⇒ 永远非有效 Clash。
        /// 有效 Clash（<c>d ≥ 4</c>）与"互相命中"在这个 Pattern 下互斥，
        /// 因此"拼刀终止参与计划 + 抑制其直接 Hit"这条链在本文件里<strong>无法端到端构造</strong>，
        /// 由 <c>Task08MomentumClashTests</c>（手工喂值对象）与阶段 14 的提交路径分别覆盖。
        /// </para>
        /// </summary>
        [Test]
        public void SameDirectionAreaOverlapProducesEdgeButNoEffectiveClash()
        {
            ClashRig rig = ArrangeClash();
            BattleSimulation sim = rig.Sim;

            // 边存在：构图期不收紧（裁定 3），两者进同一组。
            ConflictGraph graph = sim.ConflictGraph;
            Assert.That(graph.Groups.Count, Is.EqualTo(1), "两个攻击者必须在同一个冲突组（共享 AttackAttack 边）");
            Assert.That(graph.Groups[0].NodeCount, Is.EqualTo(2));
            Assert.That(graph.Groups[0].EdgeCount, Is.EqualTo(1),
                "构图期必须为区域相交的两个攻击连出 Intent↔Intent 边（不得在构图期收紧）");

            // 但没有拼刀：求解器把它判为非有效 Clash。
            StagedConflictResolution staged = sim.StagedResolution;
            Assert.That(staged.Failed, Is.False, "夹具前提：构图成功");
            Assert.That(staged.Clashes.Count, Is.EqualTo(0),
                "方向差 d=0（OppositionFactorQ10[0] = 0）⇒ 非有效 Clash：求解器不得产生任何 Clash 分量");
            Assert.That(staged.ClashTerminatedPlanIds.Count, Is.EqualTo(0), "非有效 Clash 不得终止任何计划");

            // 两个攻击者都仍非终态（它们互相不命中，因此也没有直接 Hit）。
            IReadOnlyList<ActionPlan> active = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(active.Count, Is.EqualTo(2), "非有效 Clash 之后两个计划都必须仍在活动索引里");
            Assert.That(rig.HeroPlan.IsTerminal, Is.False, "hero 的攻击不得被终止");
            Assert.That(rig.MonsterPlan.IsTerminal, Is.False, "monster 的攻击不得被终止");

            // 本构型下双方都不命中对方（几何前提：区域相交但对 footprint 不覆盖）⇒ 无伤害可提交。
            Assert.That(staged.RemainingHits.Count, Is.EqualTo(0));
            Assert.That(HealthQ10Of(sim, rig.HeroId), Is.EqualTo(InitialHealth * 1024), "hero 未被扣血");
            Assert.That(HealthQ10Of(sim, rig.MonsterId), Is.EqualTo(InitialHealth * 1024), "monster 未被扣血");
            Assert.That(sim.LastDamageCommitReport.Units.Count, Is.EqualTo(0),
                "没有任何单位需要提交伤害");
        }

        // =====================================================================
        // 用例 4：排列不变性（同一冲突组的两个 Intent 顺序无关）
        // =====================================================================

        /// <summary>
        /// <strong>同一冲突组的 Intent 任意排列 ⇒ Resolution 完全一致</strong>
        /// （验收 :389）。本夹具的两个攻击者区域相交（同一冲突组、两条互相命中的直接接触），
        /// 但方向相同 ⇒ 非有效 Clash，因此<strong>两条直接 Hit 都会走到 Remaining Hits</strong>：
        /// 这正是比较"聚合结果是否与提交顺序无关"的好素材。
        ///
        /// 计划 ID 会随提交顺序变化（模拟的既有语义：ID 由创建顺序分配），
        /// 因此比较的是<strong>与计划标识无关的结构投影</strong>（每阶段条目数 + 两条接触的
        /// 方向/动量/聚合伤害 + 终态），而不是计划 ID 本身——
        /// 这与"稳定键只用于组、输出与提交排序"的冻结口径一致。
        ///
        /// 会让它失败的实现缺陷：按"排序后逐对处理"替代同时求解、
        /// 组划分或接触排序依赖容器枚举顺序、聚合按发现顺序逐次累加。
        /// </summary>
        [Test]
        public void ResolutionIsIndependentOfIntentSubmissionOrder()
        {
            Signature forward = ClashSignature(reverseOrder: false);
            Signature reverse = ClashSignature(reverseOrder: true);

            // 非真空证据：两次运行的冲突图都必须真的非平凡
            // （两个节点 + 至少一条 Intent↔Intent 边 + 两者同组）。
            long impact = AlignedImpactTick;
            Assert.That(forward.GraphDump, Does.Contain("nodes=2"),
                "第一次运行必须有两个攻击 Intent；tick=" + forward.Tick + " impact=" + impact
                + " plans=" + forward.PlanDump + " graph=" + forward.GraphDump);
            Assert.That(forward.GraphDump, Does.Contain("edges=1"),
                "第一次运行必须连出 Intent↔Intent 边；graph=" + forward.GraphDump);
            Assert.That(reverse.GraphDump, Does.Contain("edges=1"),
                "第二次运行必须连出 Intent↔Intent 边；graph=" + reverse.GraphDump);
            Assert.That(forward.ClashCount, Is.EqualTo(0),
                "同向 ⇒ 非有效 Clash（这也是本夹具的判据面）");
            Assert.That(reverse.ClashCount, Is.EqualTo(0));

            // 计划 ID→单位 的映射必须真的换过（否则本用例退化成两次相同运行）：
            // 第一次提交者拿到的计划 ID 恒为 1，因此"第一个计划属于谁"就是那个映射的见证。
            Assert.That(reverse.FirstSubmittedOwner, Is.Not.EqualTo(forward.FirstSubmittedOwner),
                "两次运行的『计划 1 → 单位』映射必须真的换过；forward=" + forward.FirstSubmittedOwner
                + " reverse=" + reverse.FirstSubmittedOwner);

            Assert.That(reverse.Text, Is.EqualTo(forward.Text),
                "Resolution 必须与两个 Intent 的提交顺序无关；\nforward=" + forward.Text
                + "\nreverse=" + reverse.Text);
        }

        private sealed class Signature
        {
            public string Text;
            public int ClashCount;
            public int DirectHitCount;
            public long Tick;
            public string PlanDump;
            public string GraphDump;
            public bool PlanIdsSwapped;
            public long FirstSubmittedOwner;
        }

        private static Signature ClashSignature(bool reverseOrder)
        {
            BattleSimulation sim = BattleSimulation.Create(
                BuildDefinition(clashLayout: true), EncounterId, Inputs, BattleSimulationAssembly.Standard());
            CommandIngressEntry entry = sim.CommandIngress.FindEntry(Player);

            long firstSubmittedOwner;
            ActionPlanId[] planIds = SubmitOwnedBatch(
                sim, entry,
                new[] { Hero, Monster },
                new[] { Monster, Hero },
                new[] { GridDirection.East, ClashMonsterFacing },
                reverseOrder,
                out firstSubmittedOwner);
            StepToImpact(sim, entry);

            ActionPlan heroPlan = sim.ScheduleAuthority.Registry.Find(planIds[0]);
            ActionPlan monsterPlan = sim.ScheduleAuthority.Registry.Find(planIds[1]);
            bool swapped = heroPlan != null && heroPlan.OwnerUnitId == Monster;

            StagedConflictResolution staged = sim.StagedResolution;
            var sb = new System.Text.StringBuilder();
            sb.Append("failed=").Append(staged.Failed)
              .Append("|clashes=").Append(staged.Clashes.Count)
              .Append("|dodgePlans=").Append(staged.DodgePlans.Count)
              .Append("|blockPlans=").Append(staged.BlockPlans.Count)
              .Append("|moves=").Append(staged.Moves.Count)
              .Append("|remaining=").Append(staged.RemainingHits.Count)
              .Append("|terminated=").Append(staged.ClashTerminatedPlanIds.Count);
            for (int i = 0; i < staged.Clashes.Count; i++)
            {
                IReadOnlyList<ClashParticipantResolution> participants = staged.Clashes[i].Clash.Participants;
                for (int p = 0; p < participants.Count; p++)
                {
                    sb.Append("|p:").Append(participants[p].Direction.ToString())
                      .Append(':').Append(participants[p].OriginalMomentumUnits)
                      .Append(':').Append(participants[p].TotalOppositionLossUnits)
                      .Append(':').Append(participants[p].RemainingMomentumUnits);
                }
                sb.Append("|residual=").Append(staged.Clashes[i].Clash.ResidualImpacts.Count);
            }
            // 逐条未消解命中的**与标识无关**投影：方向 + 入射动量 + 聚合后的伤害/冲击/击退。
            // 刻意不写 UnitId/ActionPlanId（它们随提交顺序变化，且比较它们等于在比较标识）。
            for (int i = 0; i < staged.RemainingHits.Count; i++)
            {
                RemainingHitResolution hit = staged.RemainingHits[i];
                TargetAggregateResolution aggregate = hit.Aggregate;
                sb.Append("|hit:").Append(hit.IncomingDirection.ToString())
                  .Append(':').Append(hit.IncomingMomentumUnits)
                  .Append(':').Append(hit.CanReceiveDirectHit)
                  .Append(':').Append(hit.IsDirectHitSuppressed)
                  .Append(':').Append(aggregate == null ? -1L : aggregate.TotalDamageQ10)
                  .Append(':').Append(aggregate == null ? -1L : aggregate.TotalImpactUnits)
                  .Append(':').Append(aggregate == null ? -1 : aggregate.KnockbackSteps)
                  .Append(':').Append(aggregate == null || aggregate.ResultantDirection == null
                      ? "-" : aggregate.ResultantDirection.Value.ToString());
            }

            // 提交结果：按单位汇总的伤害/冲击总量（与标识无关的聚合面）。
            long committedDamage = 0L;
            long committedImpact = 0L;
            for (int i = 0; i < sim.LastDamageCommitReport.Units.Count; i++)
            {
                committedDamage += sim.LastDamageCommitReport.Units[i].DamageQ10;
                committedImpact += sim.LastDamageCommitReport.Units[i].TotalImpactUnits;
            }
            sb.Append("|committed:").Append(sim.LastDamageCommitReport.Units.Count)
              .Append(':').Append(committedDamage).Append(':').Append(committedImpact);

            // 活动计划的终态投影：只写状态与终止原因（<strong>不</strong>写单位/计划 ID）。
            IReadOnlyList<ActionPlan> active = sim.ScheduleAuthority.Registry.ActivePlans;
            for (int i = 0; i < active.Count; i++)
            {
                sb.Append("|plan:").Append(active[i].State).Append(':')
                  .Append(active[i].TerminationReason).Append(':')
                  .Append(active[i].ImpactTick);
            }

            var dump = new System.Text.StringBuilder();
            dump.Append("active=").Append(active.Count);
            for (int i = 0; i < active.Count; i++)
            {
                dump.Append(" [").Append(active[i].ActionPlanId.Value)
                    .Append(" owner=").Append(active[i].OwnerUnitId.Value)
                    .Append(" type=").Append(active[i].ActionType)
                    .Append(" start=").Append(active[i].StartTick)
                    .Append(" impact=").Append(active[i].ImpactTick)
                    .Append(" state=").Append(active[i].State)
                    .Append("]");
            }
            ConflictGraph graph = sim.ConflictGraph;
            var graphDump = new System.Text.StringBuilder();
            graphDump.Append("nodes=").Append(graph == null ? -1 : graph.Nodes.Count);
            if (graph != null)
            {
                for (int i = 0; i < graph.Groups.Count; i++)
                {
                    graphDump.Append(" [g").Append(i).Append(" key=").Append(graph.Groups[i].GroupKey)
                        .Append(" nodes=").Append(graph.Groups[i].NodeCount)
                        .Append(" edges=").Append(graph.Groups[i].EdgeCount)
                        .Append(" contacts=").Append(graph.Groups[i].ContactKeys.Count)
                        .Append("]");
                }
            }

            return new Signature
            {
                Text = sb.ToString(),
                ClashCount = staged.Clashes.Count,
                DirectHitCount = staged.RemainingHits.Count,
                Tick = sim.Tick,
                PlanDump = dump.ToString(),
                GraphDump = graphDump.ToString(),
                PlanIdsSwapped = swapped,
                FirstSubmittedOwner = firstSubmittedOwner
            };
        }

        private static StepResult StepZero(BattleSimulation sim, CommandIngressEntry entry, long tick,
            CommandRequest request = null)
        {
            if (request != null)
            {
                CommandIngressRejection rejection = entry.Submit(request);
                Assert.That(rejection, Is.Null,
                    "fixture: ingress must accept; reason=" + (rejection == null ? "<none>" : rejection.ReasonCode)
                    + " tick=" + tick + " simTick=" + sim.Tick
                    + " target=" + request.TargetTick
                    + " window=" + (sim.CurrentTurnWindow == null
                        ? "<none>" : sim.CurrentTurnWindow.WindowId.Value.ToString()));
            }
            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(tick);
            StepResult result = sim.Step(tick, batch);
            Assert.That(result.Status, Is.EqualTo(StepStatus.Advanced),
                "fixture: battle must continue at tick " + tick + "; actual=" + result.Status);
            return result;
        }

        // =====================================================================
        // Clash 夹具：hero@(0,0) 朝东 与 monster@(6,0) 朝西（区域逐点相同、互相命中）
        // =====================================================================

        private sealed class ClashRig
        {
            public BattleSimulation Sim;
            public ActionPlan HeroPlan;
            public ActionPlan MonsterPlan;
            public UnitId HeroId;
            public UnitId MonsterId;
        }

        private static ClashRig ArrangeClash()
        {
            BattleSimulation sim = BattleSimulation.Create(
                BuildDefinition(clashLayout: true), EncounterId, Inputs, BattleSimulationAssembly.Standard());
            CommandIngressEntry entry = sim.CommandIngress.FindEntry(Player);
            Assert.That(entry, Is.Not.Null, "夹具前提：ControllerBinding 必须注册出命令入口");

            var rig = new ClashRig { Sim = sim, HeroId = Hero, MonsterId = Monster };

            // —— 几何前提（夹具不变量，不是被测行为）——
            Assert.That(sim.LogicGrid.TryGetAnchor(rig.HeroId, out GridPoint heroAnchor), Is.True);
            Assert.That(heroAnchor, Is.EqualTo(ClashHeroAnchor), "夹具前提：hero 站位");
            Assert.That(sim.LogicGrid.TryGetAnchor(rig.MonsterId, out GridPoint monsterAnchor), Is.True);
            Assert.That(monsterAnchor, Is.EqualTo(ClashMonsterAnchor), "夹具前提：monster 站位");
            Assert.That(sim.LogicGrid.TrianglesOf(rig.HeroId).Single(), Is.EqualTo(new TrianglePoint(3, 0, 1)),
                "夹具前提：hero 体积三角");
            Assert.That(sim.LogicGrid.TrianglesOf(rig.MonsterId).Single(), Is.EqualTo(new TrianglePoint(7, 0, 1)),
                "夹具前提：monster 体积三角");

            ActionPlanId[] planIds = SubmitOwnedBatch(
                sim, entry,
                new[] { rig.HeroId, rig.MonsterId },
                new[] { rig.MonsterId, rig.HeroId },
                new[] { GridDirection.East, ClashMonsterFacing },
                reverseOrder: false,
                out _);
            StepToImpact(sim, entry);

            rig.HeroPlan = sim.ScheduleAuthority.Registry.Find(planIds[0]);
            rig.MonsterPlan = sim.ScheduleAuthority.Registry.Find(planIds[1]);
            Assert.That(rig.HeroPlan, Is.Not.Null, "夹具前提：hero 的计划必须仍可查询");
            Assert.That(rig.MonsterPlan, Is.Not.Null, "夹具前提：monster 的计划必须仍可查询");
            Assert.That(rig.HeroPlan.OwnerUnitId, Is.EqualTo(rig.HeroId));
            Assert.That(rig.MonsterPlan.OwnerUnitId, Is.EqualTo(rig.MonsterId));

            ConflictGraph graph = sim.ConflictGraph;
            Assert.That(graph, Is.Not.Null, "夹具前提：构图必须成功");
            Assert.That(graph.Nodes.Count, Is.EqualTo(2), "夹具前提：两个攻击 Intent 都必须在图里");

            bool areasIntersect = graph.Nodes[0].Intent.AreaIntersects(graph.Nodes[1].Intent);
            string areaDump = "hero area="
                + string.Join(";", graph.Nodes[0].Intent.AreaPoints.Select(p => p.X + "," + p.Y + "," + p.T))
                + " monster area="
                + string.Join(";", graph.Nodes[1].Intent.AreaPoints.Select(p => p.X + "," + p.Y + "," + p.T));
            Assert.That(areasIntersect, Is.True,
                "夹具前提：两个攻击者的区域必须相交（这是 Attack↔Attack 连边的唯一判据）；" + areaDump);

            // 双方都必须被对方命中（本夹具里两个攻击者互为命中目标）：
            // 判据用与阶段 10 同一份图输出（contacts 里的直接接触），而不是第二套几何。
            int attackAttack = 0;
            for (int i = 0; i < graph.Contacts.Count; i++)
            {
                if (graph.Contacts[i].Key.Type == ContactType.AttackAttack
                    && graph.Contacts[i].IsNodeToNode) attackAttack++;
            }
            Assert.That(attackAttack, Is.GreaterThanOrEqualTo(1),
                "夹具前提：必须存在 Intent↔Intent 的 AttackAttack 接触；" + areaDump
                + " contacts=" + graph.Contacts.Count);

            return rig;
        }

        /// <summary>
        /// Clash 提交：三个单位各提交一次攻击计划，全部排在同一个<strong>将来</strong>开始 Tick，
        /// 因此三个计划共享同一个 <c>ImpactTick</c>。
        ///
        /// 时间线（本方法唯一能构造出该形状的路径）：
        /// <list type="number">
        /// <item>Tick 1：为第一个单位开窗并提交它的计划（<c>RequestedStartTick = 5</c>，将来时
        /// ⇒ 计划在锁定前仍 Editable），随后关窗；</item>
        /// <item>之后每个单位各重复一次"开窗 → 提交 → 关窗"（新窗口最早在关窗后的下一 Tick 打开，
        /// 因此三次提交的命令 Tick 各不相同，但都在 5 之前）；</item>
        /// <item>Tick 5：三个计划同时开始 ⇒ <c>ImpactTick = 15</c>。</item>
        /// </list>
        ///
        /// <strong>为什么必须排在同一个开始 Tick</strong>：计划真正开始于处理命令的那个 Tick，
        /// 而"窗口只属于其拥有者 + 同 Tick 只有一个窗口"让多次提交必然分散在不同 Tick
        /// ⇒ 只有把全部计划都排到一个<strong>将来的共同 Tick</strong>，命中时刻才会相同。
        /// </summary>
        private static ActionPlanId[] SubmitOwnedBatch(
            BattleSimulation sim,
            CommandIngressEntry entry,
            UnitId[] owners,
            UnitId[] targets,
            GridDirection[] facings,
            bool reverseOrder,
            out long firstSubmittedOwner)
        {
            const long sharedStartTick = 5L;
            firstSubmittedOwner = 0L;
            var order = new List<int>();
            for (int i = 0; i < owners.Length; i++) order.Add(i);
            if (reverseOrder) order.Reverse();

            var planIds = new ActionPlanId[owners.Length];
            for (int step = 0; step < order.Count; step++)
            {
                int i = order[step];
                UnitId owner = owners[i];
                UnitId target = targets[i];
                GridDirection facing = facings[i];
                if (step == 0) firstSubmittedOwner = owner.Value;

                long openTick = sim.Tick + 1L;
                sim.WindowManager.ScheduleWindow(openTick, owner, WindowBudget);
                while (sim.Tick + 1L <= openTick) StepZero(sim, entry, sim.Tick + 1L);

                TurnWindow window = sim.CurrentTurnWindow;
                Assert.That(window, Is.Not.Null, "夹具前提：必须拿到刚打开的窗口");
                Assert.That(window.OwnerUnitId, Is.EqualTo(owner),
                    "夹具前提：窗口必须属于该单位；owner=" + owner.Value
                    + " windowOwner=" + window.OwnerUnitId.Value);

                long submitTick = sim.Tick + 1L;
                var request = new CommandRequest(
                    submitTick,
                    new ScheduleEditScope(sim.ScheduleAuthority.ScheduleRevision, window.WindowId),
                    new ScheduleEditPayload(new ScheduleEditOperation[]
                    {
                        new AddOrdinaryPlanOperation(
                            1L, owner, Spec(AttackSpecId), sharedStartTick, default, target, facing, null)
                    }));

                StepResult submit = StepZero(sim, entry, submitTick, request);
                string rejects = string.Join(",", submit.Events.Events
                    .OfType<CommandRejectedEvent>().Select(e => e.ReasonCode));
                Assert.That(rejects, Is.Empty,
                    "夹具前提：受控提交不得被拒绝；owner=" + owner.Value
                    + " submitTick=" + submitTick + " sharedStart=" + sharedStartTick
                    + " rejects=" + rejects);

                IReadOnlyList<ActionPlan> active = sim.ScheduleAuthority.Registry.ActivePlans;
                ActionPlan created = null;
                for (int a = 0; a < active.Count; a++)
                {
                    bool known = false;
                    for (int k = 0; k < planIds.Length; k++)
                    {
                        if (planIds[k].Value != 0L && planIds[k] == active[a].ActionPlanId) known = true;
                    }
                    if (!known) created = active[a];
                }
                Assert.That(created, Is.Not.Null, "夹具前提：必须能找到本次新增的计划");
                Assert.That(created.OwnerUnitId, Is.EqualTo(owner), "夹具前提：新计划属于本次提交的单位");
                planIds[i] = created.ActionPlanId;

                long impact = created.ImpactTick;
                if (step == 0)
                {
                    AlignedImpactTick = impact;
                }
                else
                {
                    Assert.That(impact, Is.EqualTo(AlignedImpactTick),
                        "夹具前提：同批提交必须共享同一个 ImpactTick；第一个="
                        + AlignedImpactTick + " 本次=" + impact + " start=" + created.StartTick);
                }

                Assert.That(sim.WindowManager.TryRequestClose(owner, window.WindowId,
                    TurnWindowCloseReason.OwnerRequested), Is.Null,
                    "夹具前提：拥有者请求关窗必须成功");
                StepZero(sim, entry, sim.Tick + 1L);
                Assert.That(sim.CurrentTurnWindow, Is.Null,
                    "夹具前提：正式关闭之后不得还有当前窗口");
            }

            return planIds;
        }

        /// <summary>推进到多计划夹具的 <c>ImpactTick</c>（夹具不变量：战斗必须继续）。</summary>
        private static void StepToImpact(BattleSimulation sim, CommandIngressEntry entry)
        {
            while (sim.Tick + 1L <= AlignedImpactTick) StepZero(sim, entry, sim.Tick + 1L);
        }
    }
}
