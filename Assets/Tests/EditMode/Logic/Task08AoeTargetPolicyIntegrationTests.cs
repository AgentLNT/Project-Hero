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
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 08「必须产出」<strong>12（AOE 目标策略）</strong>的<strong>端到端</strong>自动化证据。
    ///
    /// <para>
    /// <strong>为什么必须有这个文件</strong>：<c>Task08ConflictGraphTests</c> 已经覆盖了 AOE 的
    /// <em>候选/构图层</em>（<c>HostileOnlyAreaAttackExcludesSelfAlliedAndNeutralUnits</c>、
    /// <c>ExplicitFriendlyFireMaskIncludesAlliedButNotNeutralTargets</c>、
    /// <c>NeutralTargetRequiresExplicitNeutralRelationBit</c>、
    /// <c>PrimaryAndAreaPoliciesUseSameFactionRelationResolver</c>）——那些用例全部手工喂值对象，
    /// 对"装配点没接线 / 策略没从 ActionSpec 取 / 多目标被压平"完全不敏感。
    /// 本文件用<strong>真实</strong> <see cref="BattleSimulation.Create"/> + 真实命令入口 + 真实排程事务 +
    /// 真实五阶段求解 + 真实阶段 11 提交，钉住下面这条链：
    /// <c>ActionSpec.TargetPolicy → Intent.TargetPolicy → 候选构建 → 五阶段求解 → 每目标独立抵抗 → 聚合 → 提交</c>。
    /// </para>
    ///
    /// <para>
    /// <strong>几何夹具（实测，勿随手改）</strong>——三个已知坑都在这里踩过：
    /// <list type="number">
    /// <item>接触判定是<strong>三角点精确相等（含 T）</strong>，不是格重叠 ⇒ "区域命中"要求攻击区域的某个点
    /// 与目标的某个占用三角<strong>逐值相同</strong>；</item>
    /// <item><see cref="GridPoint"/> 锚点必须 <c>X+Y</c> 为偶、<see cref="TrianglePoint"/> 必须
    /// <c>X+Y+T</c> 为偶 ⇒ 单三角体积表在朝东时三角恒为 <c>anchor+(3,0,1)</c>；</item>
    /// <item>本夹具因此把 hero 与全部目标放在<strong>同一行 y=0</strong>、全部朝东，
    /// 攻击区域取 <c>{(2j+3, 0, 1)}</c> 的<strong>一条水平线</strong>——它同时包含
    /// hero 自己的三角 <c>(3,0,1)</c> 与每个目标单位的三角中心
    /// （锚点 <c>(6,0)</c> → <c>(9,0,1)</c>、<c>(14,0)</c> → <c>(17,0,1)</c>、<c>(22,0)</c> → <c>(25,0,1)</c>）。</item>
    /// </list>
    /// </para>
    /// </summary>
    public class Task08AoeTargetPolicyIntegrationTests
    {
        // ================= 夹具常量 =================

        internal const string AttackSpecId = "action.t08aoe.attack";
        internal const string PrimarySpecId = "action.t08aoe.primary";
        internal const string SelfSpecId = "action.t08aoe.self";
        internal const string FriendlySpecId = "action.t08aoe.friendly";
        internal const string EverythingSpecId = "action.t08aoe.everything";
        /// <summary>与 <see cref="SelfSpecId"/> <strong>同一张自覆盖模式</strong>，唯一区别是掩码含 Self 位。</summary>
        internal const string SelfEnabledSpecId = "action.t08aoe.self_enabled";
        internal const string BlockSpecId = "action.t08aoe.block";
        internal const string AoePatternId = "attack.pattern.t08aoe.aoe";
        internal const string SelfPatternId = "attack.pattern.t08aoe.self";
        internal const string EncounterIdValue = "encounter.t08aoe";
        internal const string VolumeSpecIdValue = "unit_volume.t08aoe";

        internal const int AttackWindupTicks = 10;
        internal const int AttackRecoveryTicks = 2;
        internal const int BlockReactionWindupTicks = 1;
        internal const int BlockRecoveryTicks = 1;
        internal const long ImpactTick = 11L;
        internal const long SubmitTick = 1L;

        /// <summary>攻击基础伤害 <c>10f</c>（创作态生命点）⇒ 量化后 <c>10240 Q10</c>。</summary>
        internal const float AttackRawAmount = 10f;
        internal const long RawDamageQ10 = 10240L;
        internal const int BluntResistanceQ10 = 512;

        /// <summary>本夹具只用到这一个伤害通道；被动抵抗按该通道配置。</summary>
        internal static DamageChannelId Channel => DamageChannels.PhysicalBlunt;

        internal const int InitialHealth = 2000;

        internal static BattleRules Rules => BattleRules.FrozenV1;
        internal static GridBoundaryDefinition Wide
            => new GridBoundaryDefinition(new GridPoint(-60, -60), new GridPoint(60, 60));

        // 单位 ID 按 EncounterSlotId 的 Ordinal 升序分配：hero, hero2, mon, mon2。
        internal static UnitId Hero => new UnitId(1L);
        internal static UnitId Hero2 => new UnitId(2L);
        internal static UnitId Monster => new UnitId(3L);
        internal static UnitId Monster2 => new UnitId(4L);

        internal static FactionId HeroFaction => new FactionId("faction.hero");
        internal static FactionId MonsterFaction => new FactionId("faction.monster");
        internal static ControllerId Player => new ControllerId("controller.player");
        internal static ActionSpecId Spec(string id) => new ActionSpecId(id);
        internal static ActionSetId ActionSet(string slotId) => new ActionSetId("action_set.t08aoe." + slotId);
        internal static EncounterDefinitionId EncounterId => new EncounterDefinitionId(EncounterIdValue);
        internal static EncounterSlotId Slot(string id) => new EncounterSlotId(id);
        internal static UnitDefinitionId UnitDef(string slotId) => new UnitDefinitionId("unit.t08aoe." + slotId);
        internal static BattleRuntimeInputs Inputs => new BattleRuntimeInputs(InitialRngSeed: 23UL, InitialMetaResource: 0);

        /// <summary>站位：全部一行、全部朝东（见类型注释的几何推导）。</summary>
        internal static GridPoint HeroAnchor => new GridPoint(0, 0);
        internal static GridPoint Hero2Anchor => new GridPoint(22, 0);
        internal static GridPoint MonsterAnchor => new GridPoint(6, 0);
        internal static GridPoint Monster2Anchor => new GridPoint(14, 0);

        /// <summary>实测三角：锚点 + (3,0,1)。它是"是否命中"的唯一判据点。</summary>
        internal static TrianglePoint HeroTriangle => new TrianglePoint(3, 0, 1);
        internal static TrianglePoint MonsterTriangle => new TrianglePoint(9, 0, 1);
        internal static TrianglePoint Monster2Triangle => new TrianglePoint(17, 0, 1);
        internal static TrianglePoint Hero2Triangle => new TrianglePoint(25, 0, 1);

        internal static IReadOnlyList<DirectionalTriangleSet> VolumeDirections()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        /// <summary>
        /// AOE 区域模式：每个朝向的相对点集都是 <c>{(2j+3, 0, 1)}</c>（j = 0..12）。
        /// 朝东、锚点 <c>(0,0)</c> ⇒ 绝对区域 <c>{(3,0,1),(5,0,1),…,(27,0,1)}</c>。
        ///
        /// <strong>唯一实例</strong>：定义与断言必须用同一个对象——每次调用都新建会让
        /// "定义里的模式"与"测试翻译出来的区域"不是同一张表（本文件曾因此吃过一次红灯）。
        /// </summary>
        internal static readonly AttackPatternSpec AoePattern = BuildAoePatternCore();

        private static AttackPatternSpec BuildAoePatternCore()
        {
            var directions = new List<DirectionalTriangleSet>(GridDirectionInfo.DirectionCount);
            for (int f = 0; f < GridDirectionInfo.DirectionCount; f++)
            {
                var points = new List<TrianglePoint>(13);
                for (int j = 0; j < 13; j++) points.Add(new TrianglePoint(2 * j + 3, 0, 1));
                directions.Add(new DirectionalTriangleSet((GridDirection)f, points));
            }
            return new AttackPatternSpec(new AttackPatternId(AoePatternId), directions);
        }

        internal static AttackPatternSpec BuildAoePattern() => AoePattern;

        /// <summary>
        /// "只包含施法者自己三角"的模式：区域恒为 <c>{(3,0,1)}</c>。
        /// 它让 <c>Self</c> 位成为<strong>唯一</strong>能决定"是否打到自己"的变量——
        /// 与真实 AOE 模式无关，因此这条链不会与"区域恰不覆盖自己"混为一谈。
        /// </summary>
        internal static readonly AttackPatternSpec SelfPattern = BuildSelfPatternCore();

        private static AttackPatternSpec BuildSelfPatternCore()
        {
            var directions = new List<DirectionalTriangleSet>(GridDirectionInfo.DirectionCount);
            for (int f = 0; f < GridDirectionInfo.DirectionCount; f++)
            {
                directions.Add(new DirectionalTriangleSet((GridDirection)f,
                    new List<TrianglePoint>(1) { new TrianglePoint(3, 0, 1) }));
            }
            return new AttackPatternSpec(new AttackPatternId(SelfPatternId), directions);
        }

        internal static AttackPatternSpec BuildSelfPattern() => SelfPattern;

        internal static ActionSpec AttackSpecOf(string id, AttackPatternSpec pattern, TargetPolicy policy,
            TargetRelationMask mask)
            => new ActionSpec(
                Spec(id), ActionType.Attack,
                new AttackTimingSpec(AttackWindupTicks, AttackRecoveryTicks),
                new AttackPayloadSpec(
                    new[] { new DamageComponentSpec(Channel, AttackRawAmount, DamageChannelCatalog.GetDefaultTags(Channel)) },
                    ImpactProfileId: ImpactProfiles.Blunt,
                    ForceMultiplier: 1f,
                    TargetPolicy: policy,
                    AllowedTargetRelations: mask,
                    MomentumDirectionOffsetSteps: 0,
                    Pattern: pattern,
                    Tags: AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable),
                AdrenalineCost: 0);

        internal static ActionSpec BlockSpec()
            => new ActionSpec(
                Spec(BlockSpecId), ActionType.Block,
                new BlockReactionTimingSpec(BlockReactionWindupTicks, BlockRecoveryTicks),
                new BlockPayloadSpec(DefenseTagMask.Blockable),
                AdrenalineCost: 2);

        internal sealed class SlotSpec
        {
            public SlotSpec(string slotId, GridPoint anchor, GridDirection facing, FactionId faction, int resistanceQ10)
            {
                SlotId = slotId;
                Anchor = anchor;
                Facing = facing;
                Faction = faction;
                ResistanceQ10 = resistanceQ10;
            }

            public string SlotId;
            public GridPoint Anchor;
            public GridDirection Facing;
            public FactionId Faction;
            public int ResistanceQ10;
        }

        internal static IReadOnlyList<SlotSpec> Slots(int monsterResistanceQ10 = 0, int monster2ResistanceQ10 = 0)
            => new List<SlotSpec>
            {
                new SlotSpec("hero", HeroAnchor, GridDirection.East, HeroFaction, 0),
                new SlotSpec("hero2", Hero2Anchor, GridDirection.East, HeroFaction, 0),
                new SlotSpec("mon", MonsterAnchor, GridDirection.East, MonsterFaction, monsterResistanceQ10),
                new SlotSpec("mon2", Monster2Anchor, GridDirection.East, MonsterFaction, monster2ResistanceQ10)
            };

        /// <summary>
        /// 唯一一份定义。全部攻击动作都进每个单位的 ActionSet——这不是可选的：
        /// 计划创建会用<strong>主目标单位</strong>的 ActionSet 复核动作归属
        /// （<c>ActionPlanFactory.ValidateActionSetMembership</c>），目标单位不持有该动作时
        /// 会以 <c>SCHEDULE_PRIMARY_TARGET_RELATION_REJECTED</c> 稳定拒绝。
        /// </summary>
        internal static BattleDefinition BuildDefinition(IReadOnlyList<SlotSpec> slots)
        {
            var factionModel = new FactionModelDefinition(
                new List<FactionDefinition> { new FactionDefinition(HeroFaction), new FactionDefinition(MonsterFaction) },
                new List<FactionRelationDefinition>
                {
                    new FactionRelationDefinition(HeroFaction, MonsterFaction, FactionDisposition.Hostile)
                });

            AttackPatternSpec aoePattern = BuildAoePattern();
            AttackPatternSpec selfPattern = BuildSelfPattern();

            var specs = new List<ActionSpec>
            {
                // 唯一的"正常 AOE"：区域全部目标 + 只打敌对。
                AttackSpecOf(AttackSpecId, aoePattern, TargetPolicy.AllTargetsInArea, TargetRelationMask.Hostile),
                // 同一区域、同一掩码，只有策略不同 —— 用于隔离"策略来自 ActionSpec"。
                AttackSpecOf(PrimarySpecId, aoePattern, TargetPolicy.PrimaryTargetOnly, TargetRelationMask.Hostile),
                // 区域只覆盖施法者自己：Self 位是唯一变量。
                AttackSpecOf(SelfSpecId, selfPattern, TargetPolicy.AllTargetsInArea, TargetRelationMask.Hostile),
                // 显式友伤：Hostile | Allied（Neutral 位仍缺失）。
                AttackSpecOf(FriendlySpecId, aoePattern, TargetPolicy.AllTargetsInArea,
                    TargetRelationMask.Hostile | TargetRelationMask.Allied),
                // 正对照：全掩码（含 Self）。
                AttackSpecOf(EverythingSpecId, aoePattern, TargetPolicy.AllTargetsInArea, TargetRelationMasks.All),
                // 正对照（自覆盖模式）：与 SelfSpecId 同模式、同行数，只多一个 Self 位。
                AttackSpecOf(SelfEnabledSpecId, selfPattern, TargetPolicy.AllTargetsInArea, TargetRelationMasks.All),
                BlockSpec()
            };

            var allSpecIds = new List<ActionSpecId>();
            for (int s = 0; s < specs.Count; s++) allSpecIds.Add(specs[s].ActionSpecId);

            var volume = new VolumeSpec(new VolumeSpecId(VolumeSpecIdValue), VolumeDirections());
            var actionSets = new List<ActionSetDefinition>();
            var units = new List<UnitDefinition>();
            var encounterSlots = new List<EncounterUnitSlot>();
            for (int i = 0; i < slots.Count; i++)
            {
                actionSets.Add(new ActionSetDefinition(ActionSet(slots[i].SlotId), new List<ActionSpecId>(allSpecIds)));
                var resistance = slots[i].ResistanceQ10 == 0
                    ? new Dictionary<DamageChannelId, int>()
                    : new Dictionary<DamageChannelId, int> { { Channel, slots[i].ResistanceQ10 } };
                units.Add(new UnitDefinition(UnitDef(slots[i].SlotId), 10f, 10f,
                    Rules.ReferenceActionSpeed, Rules.ReferenceMoveSpeed, resistance,
                    InitialHealth: InitialHealth,
                    ActionSetId: ActionSet(slots[i].SlotId),
                    VolumeSpecId: volume.VolumeSpecId));
                encounterSlots.Add(new EncounterUnitSlot(Slot(slots[i].SlotId), UnitDef(slots[i].SlotId),
                    slots[i].Faction, slots[i].Anchor, slots[i].Facing));
            }

            var encounter = new EncounterDefinition(
                EncounterId, Wide, encounterSlots,
                new List<ControllerBinding>
                {
                    new ControllerBinding(Player, CommandSourceKind.Player,
                        new List<EncounterSlotId>(encounterSlots.ConvertAll(s => s.SlotId)))
                },
                new VictoryDefinition(new List<FactionId> { HeroFaction }, new List<FactionId> { MonsterFaction },
                    "result.t08aoe.victory", "result.t08aoe.defeat", "result.t08aoe.draw"));

            return new BattleDefinition(
                "battle-definition.task08.aoe-target-policy", Rules.TicksPerSecond, Rules, ConcurrentActionDefinition.FrozenV1,
                new ReactionRules(CommandIngressLeadTicks: 2, MinimumReactionLeadTicks: 1),
                AdrenalineRules.FrozenV1, factionModel,
                new List<DamageChannelDefinition>(), new List<ImpactProfileDefinition>(),
                units, specs, new List<AttackPatternSpec> { aoePattern, selfPattern },
                new List<VolumeSpec> { volume }, new List<MovementPatternSpec>(),
                actionSets, new List<StatusEffectSpec>(), new List<EncounterDefinition> { encounter }, null,
                "test-definition-hash.t08.aoe-target-policy");
        }

        // =====================================================================
        // 夹具
        // =====================================================================

        internal sealed class Rig
        {
            public BattleSimulation Sim;
            public CommandIngressEntry Entry;
            public ActionPlan Plan;
            public int HeroHealthBefore;
            public int MonsterHealthBefore;
            public int Monster2HealthBefore;
            public int Hero2HealthBefore;
            /// <summary>ImpactTick 那一步的事件批次（Outbox 只在本 Step 内保留事件）。</summary>
            public StepResult ImpactEvents;
        }

        /// <summary>开窗 → 提交一个攻击计划 →（可选 <paramref name="afterFirstStep"/>）→ 推进到 <c>ImpactTick</c>。
        ///
        /// <para>
        /// <paramref name="afterFirstStep"/> 只服务"在<b>攻击已启动/锁定、反应机会已公开、尚未结算</b>时
        /// 插入一个真实反应事务"的用例（见 <see cref="Task08AoeRealBlockIntegrationTests"/>）：
        /// 它在提交那一步（<c>SubmitTick</c>，攻击在阶段 7 原子启动并 <c>TryOpenForTelegraph</c>）
        /// <strong>之后</strong>、推进到 <c>ImpactTick</c> 之前被调用一次。
        /// 它<strong>不</strong>改变本夹具的任何既有路径。
        /// </para>
        /// </summary>
        internal static Rig Arrange(string specId, UnitId owner, UnitId target,
            int monsterResistanceQ10 = 0, int monster2ResistanceQ10 = 0,
            BattleSimulationAssembly assembly = null, bool stopBeforeImpact = false,
            Action<Rig> afterFirstStep = null)
        {
            BattleSimulation sim = BattleSimulation.Create(
                BuildDefinition(Slots(monsterResistanceQ10, monster2ResistanceQ10)),
                EncounterId, Inputs, assembly ?? BattleSimulationAssembly.Standard());

            CommandIngressEntry entry = sim.CommandIngress.FindEntry(Player);
            Assert.That(entry, Is.Not.Null, "夹具前提：ControllerBinding 必须注册出命令入口");

            var rig = new Rig { Sim = sim, Entry = entry };

            // —— 几何前提（夹具不变量，不是被测行为）——
            Assert.That(sim.LogicGrid.TryGetAnchor(Hero, out GridPoint heroAnchor), Is.True);
            Assert.That(heroAnchor, Is.EqualTo(HeroAnchor), "夹具前提：hero 站位");
            Assert.That(sim.LogicGrid.TrianglesOf(Hero).Single(), Is.EqualTo(HeroTriangle),
                "夹具前提：hero 的体积三角（单三角表朝东 = 锚点 + (3,0,1)）");
            Assert.That(sim.LogicGrid.TrianglesOf(Monster).Single(), Is.EqualTo(MonsterTriangle),
                "夹具前提：mon 的体积三角");
            Assert.That(sim.LogicGrid.TrianglesOf(Monster2).Single(), Is.EqualTo(Monster2Triangle),
                "夹具前提：mon2 的体积三角");
            Assert.That(sim.LogicGrid.TrianglesOf(Hero2).Single(), Is.EqualTo(Hero2Triangle),
                "夹具前提：hero2（同阵营）的体积三角");

            sim.WindowManager.ScheduleWindow(0L, owner, 60);
            Step(rig, 0L);

            var window = sim.CurrentTurnWindow;            Assert.That(window, Is.Not.Null, "夹具前提：必须拿到刚打开的窗口");
            Assert.That(window.OwnerUnitId, Is.EqualTo(owner),
                "夹具前提：窗口必须属于拥有者；owner=" + owner.Value + " windowOwner=" + window.OwnerUnitId.Value);

            var request = new CommandRequest(
                SubmitTick,
                new ScheduleEditScope(sim.ScheduleAuthority.ScheduleRevision, window.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    new AddOrdinaryPlanOperation(1L, owner, Spec(specId), SubmitTick,
                        default, target, GridDirection.East, null)
                }));
            Step(rig, SubmitTick, request);

            IReadOnlyList<ActionPlan> active = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(active.Count, Is.EqualTo(1), "夹具前提：恰好一个活动攻击计划（命令不得被拒绝）");
            rig.Plan = active[0];
            Assert.That(rig.Plan.ActionSpecId, Is.EqualTo(Spec(specId)), "夹具前提：计划的动作就是请求的动作");
            Assert.That(rig.Plan.PrimaryTargetUnitId, Is.EqualTo(target), "夹具前提：PrimaryTarget 由排程事务固定");
            Assert.That(rig.Plan.ImpactTick, Is.EqualTo(ImpactTick), "夹具前提：ImpactTick = 1 + 10");

            rig.HeroHealthBefore = HealthQ10Of(sim, Hero);
            rig.MonsterHealthBefore = HealthQ10Of(sim, Monster);
            rig.Monster2HealthBefore = HealthQ10Of(sim, Monster2);
            rig.Hero2HealthBefore = HealthQ10Of(sim, Hero2);

            // 反应注入点：提交那一步已把攻击原子锁定/启动并公开机会，攻击尚未结算。
            afterFirstStep?.Invoke(rig);

            if (!stopBeforeImpact)
            {
                for (long tick = SubmitTick + 1L; tick <= ImpactTick; tick++)
                {
                    StepResult stepResult = Step(rig, tick);
                    if (tick == ImpactTick) rig.ImpactEvents = stepResult;
                }
            }
            return rig;
        }

        internal static StepResult Step(Rig rig, long tick, params CommandRequest[] requests)
        {
            for (int i = 0; i < requests.Length; i++)
            {
                CommandIngressRejection rejection = rig.Entry.Submit(requests[i]);
                Assert.That(rejection, Is.Null, "夹具前提：命令入口必须接受请求");
            }
            FrozenCommandBatch batch = rig.Sim.CommandIngress.FreezeTick(tick);
            StepResult result = rig.Sim.Step(tick, batch);
            for (int i = 0; i < result.Events.Count; i++)
            {
                if (result.Events.Events[i] is CommandRejectedEvent rejected)
                    Assert.Fail("夹具前提：命令被处理器拒绝：" + rejected.ReasonCode + " @tick " + tick);
            }
            return result;
        }

        internal static int HealthQ10Of(BattleSimulation sim, UnitId unitId)
            => sim.CurrentSnapshot.Units.First(u => u.UnitId == unitId.Value).HealthQ10;

        internal static long CommittedDamageOf(BattleSimulation sim, UnitId unitId)        {
            long total = 0L;
            IReadOnlyList<UnitDamageCommit> units = sim.LastDamageCommitReport.Units;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].UnitId == unitId) total += units[i].DamageQ10;
            }
            return total;
        }

        internal static int CommitEntriesOf(BattleSimulation sim, UnitId unitId)
        {
            int count = 0;
            IReadOnlyList<UnitDamageCommit> units = sim.LastDamageCommitReport.Units;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].UnitId == unitId) count++;
            }
            return count;
        }

        /// <summary>
        /// 把单位的 Available 安排到至少 <paramref name="atLeast"/>（<strong>测试前置</strong>，不是被测行为）。
        ///
        /// <para>
        /// <strong>为什么需要它</strong>：开局 Available 按规格恒为 0（主方案 3.1.1 的
        /// <c>AvailableAdrenaline = 0</c>；<c>UnitDefinition</c>/<c>EncounterUnitSlot</c> 都<strong>没有</strong>
        /// 肾上腺素字段），而"真实 Block"要求 Available ≥ 权威费用。这份额度在真实战斗里只能靠
        /// 造成/承受伤害挣得；本夹具把"额度从哪来"与"额度到了之后事务是否成立"<strong>解耦</strong>，
        /// 因此被测行为（机会公开 / 接受事务 / 预留 / 求解 / 抵抗 / 伤害提交）一个都没有被替身替换。
        /// </para>
        ///
        /// <para>
        /// 它走的是<strong>公开</strong>生产入口（<c>AdrenalineLedgerOf</c> → 账本自身的规范入账方法），
        /// 不新开写入通道、不改任何生产契约；增益由账本按 <c>AdrenalineRules</c> 自己量化（测试不自己算 gain）。
        /// </para>
        /// </summary>
        internal static AdrenalineLedger SeedAdrenaline(BattleSimulation sim, UnitId unitId, int atLeast)
        {
            AdrenalineLedger ledger = sim.AdrenalineLedgerOf(unitId);
            Assert.That(ledger, Is.Not.Null, "夹具前提：单位必须有唯一账本");
            Assert.That(ledger.AvailableAdrenaline, Is.Zero,
                "夹具前提：本夹具只从规格规定的开局 0 额度出发");

            // 承受伤害是规格内的获得途径（AdrenalineRules.DamageReceivedGainQ10）；
            // 每轮补一次直到额度达到目标，避免测试自己复算增益公式。
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
            return ledger;
        }

        // =====================================================================
        // 用例 1（必须产出 12 + 任务包用例 AreaAttackKeepsHitsOnTargetsThatDidNotBlockOrDodge）
        // =====================================================================

        /// <summary>
        /// 任务包「必需测试」<c>AreaAttackKeepsHitsOnTargetsThatDidNotBlockOrDodge</c> 的<strong>端到端</strong>形态
        /// （在本步的判据面上等价于"<strong>AOE 中部分目标被防御</strong>"）：
        /// 一个 <c>AllTargetsInArea</c> 计划在同一 <c>ImpactTick</c> 上对<strong>两个</strong>敌对目标各产生
        /// <strong>一条独立</strong>未消解命中、每目标<strong>独立</strong>聚合、<strong>独立</strong>提交，
        /// 两个目标都被真实扣血；同一批事件里<strong>每目标各一条</strong>分通道明细与目标聚合事件。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item><c>AllTargetsInArea</c> 在装配点被当成单体（只取 <c>PrimaryTargetUnitId</c>）⇒
        /// <c>RemainingHits.Count</c> 与 <c>CommitEntriesOf(Monster2)</c> 变 0/1 ⇒ 红；</item>
        /// <item>把多目标<strong>压平成一个总量</strong>（先求和的"区域总伤害"再一次性扣给一个单位）⇒
        /// 每单位条目数的断言红（会出现 1 条 20480 而不是 2 条 10240），
        /// "每目标一条目标聚合事件"的断言同时红；</item>
        /// <item>目标策略来自调用方/运行时推断（例如恒取 <c>PrimaryTargetOnly</c> 或按 Controller 猜敌我）⇒
        /// 目标集合断言红；</item>
        /// <item>阶段 11 仍接 <c>NoResolutionCommitSystem</c> ⇒ 两侧生命都不变 ⇒ 红。</item>
        /// </list>
        /// </para>
        /// </summary>
        [Test]
        public void AreaAttackKeepsHitsOnTargetsThatDidNotBlockOrDodge()
        {
            Rig rig = Arrange(AttackSpecId, Hero, Monster);
            BattleSimulation sim = rig.Sim;

            // —— 阶段 8：Intent 的策略与掩码必须逐字来自 ActionSpec（不是调用方）——
            Assert.That(sim.IntentQueue, Is.Not.Null, "阶段 8 必须冻结本 Tick 的 Intent 队列");
            Assert.That(sim.IntentQueue.Intents.Count, Is.EqualTo(1), "恰好一个到期攻击 Intent");
            CombatIntent intent = sim.IntentQueue.Intents[0];
            Assert.That(intent.TargetPolicy, Is.EqualTo(TargetPolicy.AllTargetsInArea),
                "策略必须来自 ActionSpec 的 Payload.TargetPolicy");
            Assert.That(intent.AllowedTargetRelations, Is.EqualTo(TargetRelationMask.Hostile),
                "掩码必须来自 ActionSpec 的 Payload.AllowedTargetRelations");
            Assert.That(intent.AreaPoints, Does.Contain(MonsterTriangle),
                "区域必须包含 mon 的三角");
            Assert.That(intent.AreaPoints, Does.Contain(Monster2Triangle),
                "区域必须包含 mon2 的三角");

            // —— 构图：一个节点、两条 Attack→Unit 接触 ——
            ConflictGraph graph = sim.ConflictGraph;
            Assert.That(graph, Is.Not.Null, "夹具前提：构图成功");
            Assert.That(graph.Nodes.Count, Is.EqualTo(1));
            Assert.That(graph.Contacts.Count(c => c.Key.Type == ContactType.AttackTarget), Is.EqualTo(2),
                "两个敌对目标各产生一条 Attack→Unit 接触");

            // —— 阶段 5：两条未消解命中，各自聚合出精确伤害 ——
            StagedConflictResolution staged = sim.StagedResolution;
            Assert.That(staged.Failed, Is.False);
            Assert.That(staged.RemainingHits.Count, Is.EqualTo(2),
                "多目标 AOE 必须留下**两条**未消解命中（压平成一个总量的实现会留下 1 条）");
            RemainingHitResolution monHit = staged.RemainingHits.Single(h => h.Key.TargetUnitId == Monster);
            RemainingHitResolution mon2Hit = staged.RemainingHits.Single(h => h.Key.TargetUnitId == Monster2);
            Assert.That(monHit.Aggregate.TotalDamageQ10, Is.EqualTo(RawDamageQ10), "无抵抗 ⇒ 精确等于量化值");
            Assert.That(mon2Hit.Aggregate.TotalDamageQ10, Is.EqualTo(RawDamageQ10));
            Assert.That(mon2Hit.AttackPlanId, Is.EqualTo(monHit.AttackPlanId),
                "两条命中来自**同一个**攻击计划（这才是 AOE，而不是两次攻击）");

            // —— 阶段 11：每单位恰好一条提交，且落在权威生命上 ——
            DamageCommitReport report = sim.LastDamageCommitReport;
            Assert.That(report.Tick, Is.EqualTo(ImpactTick));
            Assert.That(report.Status, Is.EqualTo("COMMITTED"));
            Assert.That(report.Units.Count, Is.EqualTo(2), "两个目标 ⇒ 两条提交（每单位恰好一次）");
            Assert.That(CommitEntriesOf(sim, Monster), Is.EqualTo(1));
            Assert.That(CommitEntriesOf(sim, Monster2), Is.EqualTo(1));
            Assert.That(CommittedDamageOf(sim, Monster), Is.EqualTo(RawDamageQ10));
            Assert.That(CommittedDamageOf(sim, Monster2), Is.EqualTo(RawDamageQ10));

            Assert.That(HealthQ10Of(sim, Monster), Is.EqualTo(rig.MonsterHealthBefore - (int)RawDamageQ10),
                "mon 必须真的掉血");
            Assert.That(HealthQ10Of(sim, Monster2), Is.EqualTo(rig.Monster2HealthBefore - (int)RawDamageQ10),
                "mon2 必须真的掉血 —— 且是**它自己**的那一份");
            Assert.That(HealthQ10Of(sim, Hero), Is.EqualTo(rig.HeroHealthBefore),
                "同阵营的施法者不在掩码内 ⇒ 不掉血");
            Assert.That(HealthQ10Of(sim, Hero2), Is.EqualTo(rig.Hero2HealthBefore),
                "同阵营的第三方盟友不在 Hostile 掩码内 ⇒ 不掉血");

            // —— 事件族：多目标 AOE 必须**每目标**各发一条，而不是一条"区域总量" ——
            // 事件只在 Outbox 里保留一个 Step，因此断言取 ImpactTick 那一步的批次。
            Assert.That(rig.ImpactEvents, Is.Not.Null, "夹具前提：ImpactTick 的 StepResult 必须被记下");
            EventBatch reportEvents = rig.ImpactEvents.Events;
            var aggregateByTarget = new Dictionary<long, long>();
            int channelEvents = 0;
            for (int i = 0; i < reportEvents.Count; i++)
            {
                LogicEvent ev = reportEvents.Events[i];
                if (ev is DamageChannelResolvedEvent channel)
                {
                    channelEvents++;
                    Assert.That(channel.AttackPlanId, Is.EqualTo(rig.Plan.ActionPlanId));
                    Assert.That(channel.TotalDamageQ10, Is.EqualTo(RawDamageQ10));
                }
                if (ev is TargetAggregateResolvedEvent aggregate)
                {
                    aggregateByTarget[aggregate.TargetUnitId.Value] = aggregate.TotalDamageQ10;
                    Assert.That(aggregate.Tick, Is.EqualTo(ImpactTick));
                }
            }
            Assert.That(channelEvents, Is.EqualTo(2), "分通道伤害明细必须**每目标一条**");
            Assert.That(aggregateByTarget.Count, Is.EqualTo(2),
                "目标聚合事件必须**每目标一条**（压平成一条区域总量事件的实现会得到 1）");
            Assert.That(aggregateByTarget[Monster.Value], Is.EqualTo(RawDamageQ10));
            Assert.That(aggregateByTarget[Monster2.Value], Is.EqualTo(RawDamageQ10));
        }

        // =====================================================================
        // 用例 2：PrimaryTargetOnly 由计划固定的 PrimaryTargetUnitId 决定
        // =====================================================================

        /// <summary>
        /// <strong>同一区域、同一掩码，只换策略</strong>：<c>PrimaryTargetOnly</c> 下只有计划固定的
        /// <c>PrimaryTargetUnitId</c> 被命中，区域内的另一个敌对单位<strong>完全不受影响</strong>。
        ///
        /// <para>
        /// 本用例与用例 1 构成"策略的隔离见证"：两者除 <c>ActionSpec.TargetPolicy</c> 外完全相同，
        /// 因此目标集合的差异只能由策略解释。
        /// </para>
        ///
        /// <para>
        /// 会让它失败的实现缺陷：装配点把策略硬编码成 <c>AllTargetsInArea</c>（两侧都掉血 ⇒ 红）；
        /// 目标取自"区域内第一个敌对单位"而不是计划固定的 <c>PrimaryTargetUnitId</c>（可能打错单位 ⇒ 红）；
        /// 建图阶段按 Controller/玩家标志猜敌我（掩码失效 ⇒ 红）。
        /// </para>
        /// </summary>
        [Test]
        public void PrimaryTargetOnlyHitsExactlyThePlanFixedPrimaryTargetAndSparesOtherHostilesInArea()
        {
            Rig rig = Arrange(PrimarySpecId, Hero, Monster);
            BattleSimulation sim = rig.Sim;

            Assert.That(sim.IntentQueue.Intents.Count, Is.EqualTo(1));
            CombatIntent intent = sim.IntentQueue.Intents[0];
            Assert.That(intent.TargetPolicy, Is.EqualTo(TargetPolicy.PrimaryTargetOnly),
                "策略必须逐字来自 ActionSpec（与用例 1 同一张区域表，只有策略不同）");
            Assert.That(intent.PrimaryTargetUnitId, Is.EqualTo(Monster),
                "固定目标必须来自 ActionPlan.PrimaryTargetUnitId");
            Assert.That(intent.AreaPoints, Does.Contain(Monster2Triangle),
                "夹具前提：单目标策略下的区域**仍然覆盖** mon2 —— 它没被打只能是策略造成的");

            Assert.That(sim.ConflictGraph.Contacts.Count(c => c.Key.Type == ContactType.AttackTarget),
                Is.EqualTo(1), "单目标策略只允许一条 Attack→Unit 接触");
            Assert.That(sim.StagedResolution.RemainingHits.Count, Is.EqualTo(1));
            Assert.That(sim.StagedResolution.RemainingHits[0].Key.TargetUnitId, Is.EqualTo(Monster));

            Assert.That(CommittedDamageOf(sim, Monster), Is.EqualTo(RawDamageQ10));
            Assert.That(HealthQ10Of(sim, Monster), Is.EqualTo(rig.MonsterHealthBefore - (int)RawDamageQ10));
            Assert.That(CommittedDamageOf(sim, Monster2), Is.EqualTo(0L),
                "区域内的另一个敌对单位不得被单目标策略命中");
            Assert.That(HealthQ10Of(sim, Monster2), Is.EqualTo(rig.Monster2HealthBefore),
                "mon2 必须在区域里、并且必须一点都不掉血（隔离见证）");
        }

        // =====================================================================
        // 用例 3：同一 AllowedTargetRelations 解析器（与命令层拒绝共用）
        // =====================================================================

        /// <summary>
        /// <strong>攻击接触与命令层目标校验走同一个 <c>FactionRelationResolver</c> +
        /// 同一份 <c>AllowedTargetRelations</c></strong>：
        /// <list type="bullet">
        /// <item>盟友被显式设为 <c>PrimaryTarget</c> ⇒ 命令层以
        /// <c>SCHEDULE_PRIMARY_TARGET_RELATION_REJECTED</c> 稳定拒绝（掩码里没有 Allied 位）；</item>
        /// <item>同一份掩码下，<c>AllTargetsInArea</c> 的候选构建也排除该盟友（不被 AOE 命中）。</item>
        /// </list>
        ///
        /// <para>
        /// 会让它失败的实现缺陷：候选构建另有一套敌我判断（例如"同阵营就跳过"或"Controller 不同就算敌对"）⇒
        /// 两处判定分叉，本用例的任一侧断言红；命令层放宽成"只要有单位就接受" ⇒ 第一条断言红。
        /// </para>
        /// </summary>
        [Test]
        public void AreaAndPrimaryTargetPoliciesShareTheSameFactionRelationResolverAsTheCommandLayer()
        {
            // ① 命令层：把盟友当 PrimaryTarget ⇒ 必须按关系掩码稳定拒绝。
            BattleSimulation sim = BattleSimulation.Create(
                BuildDefinition(Slots()), EncounterId, Inputs, BattleSimulationAssembly.Standard());
            CommandIngressEntry entry = sim.CommandIngress.FindEntry(Player);
            sim.WindowManager.ScheduleWindow(0L, Hero, 60);
            Step(new Rig { Sim = sim, Entry = entry }, 0L);

            var allyRequest = new CommandRequest(
                SubmitTick,
                new ScheduleEditScope(sim.ScheduleAuthority.ScheduleRevision, sim.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    // Hostile 掩码 + 盟友目标：命令层必须拒绝。
                    new AddOrdinaryPlanOperation(1L, Hero, Spec(AttackSpecId), SubmitTick,
                        default, Hero2, GridDirection.East, null)
                }));
            Assert.That(entry.Submit(allyRequest), Is.Null, "命令入口本身接受请求（拒绝发生在处理器）");
            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(SubmitTick);
            StepResult result = sim.Step(SubmitTick, batch);
            string[] codes = result.Events.Events.OfType<CommandRejectedEvent>().Select(e => e.ReasonCode).ToArray();
            Assert.That(codes, Does.Contain(ScheduleCodes.SCHEDULE_PRIMARY_TARGET_RELATION_REJECTED),
                "Hostile 掩码必须拒绝盟友目标；实际=" + string.Join(",", codes));
            Assert.That(sim.ScheduleAuthority.Registry.ActiveCount, Is.EqualTo(0), "被拒绝的事务不得创建计划");

            // ② 同一份掩码下，AOE 候选也排除该盟友（hero2 在区域内）。
            Rig rig = Arrange(AttackSpecId, Hero, Monster);
            Assert.That(rig.Sim.IntentQueue.Intents[0].AllowedTargetRelations,
                Is.EqualTo(TargetRelationMask.Hostile), "两处使用的是同一个掩码值");
            Assert.That(rig.Sim.IntentQueue.Intents[0].AreaPoints, Does.Contain(Hero2Triangle),
                "夹具前提：盟友 hero2 的三角在区域里");
            Assert.That(rig.Sim.StagedResolution.RemainingHits.Select(h => h.Key.TargetUnitId),
                Is.EquivalentTo(new[] { Monster, Monster2 }),
                "AOE 命中的只能是掩码允许的敌对单位（盟友即使站在区域里也不得被命中）");
            Assert.That(HealthQ10Of(rig.Sim, Hero2), Is.EqualTo(rig.Hero2HealthBefore));
        }

        // =====================================================================
        // 用例 4：AOE 是否伤害自身/盟友由掩码显式决定（不存在隐含 friendly-fire 开关）
        // =====================================================================

        /// <summary>
        /// <strong>掩码是唯一开关</strong>——三种掩码在<strong>同一份区域</strong>上给出三种不同结果：
        /// <list type="bullet">
        /// <item><c>Hostile</c>：既不打自己、也不打盟友（即使盟友站在区域里）；</item>
        /// <item><c>Hostile|Allied</c>（显式友伤）：打盟友、仍不打自己；</item>
        /// <item><c>All</c>（含 Self，正对照）：连自己一起打。</item>
        /// </list>
        ///
        /// <para>
        /// 会让它失败的实现缺陷：在 AOE 分支另写一套"友伤布尔值"或"施法者永远免疫"的隐含规则 ⇒
        /// 三种掩码会塌成两种结果；把 <c>Self</c> 位当成"自动包含"或"自动排除"⇒ 第二条或第三条断言红。
        /// </para>
        /// </summary>
        [Test]
        public void AoeSelfAndFriendlyFireAreDecidedOnlyByTheExplicitRelationMask()
        {
            // ① Hostile：自己与盟友都不掉血（hero 自己的三角 (3,0,1) 就在区域里）。
            Rig hostile = Arrange(AttackSpecId, Hero, Monster);
            Assert.That(hostile.Sim.IntentQueue.Intents[0].AreaPoints, Does.Contain(HeroTriangle),
                "夹具前提：施法者自己的三角在区域里 ⇒ '没打到自己'只可能是掩码造成的");
            Assert.That(HealthQ10Of(hostile.Sim, Hero), Is.EqualTo(hostile.HeroHealthBefore),
                "Hostile 掩码 ⇒ 不得打到自己");
            Assert.That(HealthQ10Of(hostile.Sim, Hero2), Is.EqualTo(hostile.Hero2HealthBefore),
                "Hostile 掩码 ⇒ 不得打到同阵营盟友");
            Assert.That(HealthQ10Of(hostile.Sim, Monster), Is.EqualTo(hostile.MonsterHealthBefore - (int)RawDamageQ10),
                "正对照：敌对目标必须掉血（否则本用例只是'什么都没发生'）");

            // ② Hostile|Allied：盟友掉血，自己仍然不掉血。
            Rig friendly = Arrange(FriendlySpecId, Hero, Monster);
            Assert.That(HealthQ10Of(friendly.Sim, Hero2), Is.EqualTo(friendly.Hero2HealthBefore - (int)RawDamageQ10),
                "显式打开 Allied 位 ⇒ 盟友必须掉血");
            Assert.That(HealthQ10Of(friendly.Sim, Hero), Is.EqualTo(friendly.HeroHealthBefore),
                "只打开 Allied（没有 Self 位）⇒ 仍然不得打到自己");
            Assert.That(HealthQ10Of(friendly.Sim, Monster), Is.EqualTo(friendly.MonsterHealthBefore - (int)RawDamageQ10));

            // ③ All（含 Self）：正对照 —— 连自己一起打，证明 Self 位真的被读取。
            Rig everything = Arrange(EverythingSpecId, Hero, Monster);
            Assert.That(HealthQ10Of(everything.Sim, Hero), Is.EqualTo(everything.HeroHealthBefore - (int)RawDamageQ10),
                "All 掩码含 Self ⇒ 施法者必须被自己的 AOE 命中");
            Assert.That(HealthQ10Of(everything.Sim, Hero2), Is.EqualTo(everything.Hero2HealthBefore - (int)RawDamageQ10));
            Assert.That(HealthQ10Of(everything.Sim, Monster), Is.EqualTo(everything.MonsterHealthBefore - (int)RawDamageQ10));
            Assert.That(HealthQ10Of(everything.Sim, Monster2), Is.EqualTo(everything.Monster2HealthBefore - (int)RawDamageQ10));
        }

        /// <summary>
        /// <strong>Self 位是唯一变量</strong>：同一张"区域只覆盖施法者自己三角"的模式，
        /// <c>Hostile</c> 掩码下命中数为 <b>0</b>；换成 <c>All</c> 掩码后自己被打。
        ///
        /// <para>
        /// 与上一个用例互补：那里"自己没被打"可能被辩解成"区域恰好没覆盖自己"，
        /// 这里的区域<strong>只</strong>覆盖自己，因此"没被打"只能由掩码解释。
        /// </para>
        ///
        /// <para>
        /// 会让它失败的实现缺陷：施法者被无条件排除（第二种掩码也 0 命中 ⇒ 红）；
        /// 或 Self 被无条件包含（第一种掩码就掉血 ⇒ 红）。
        /// </para>
        /// </summary>
        [Test]
        public void SelfTargetingInAreaRequiresTheExplicitSelfBit()
        {
            Rig hostile = Arrange(SelfSpecId, Hero, Monster);
            CombatIntent intent = hostile.Sim.IntentQueue.Intents[0];
            Assert.That(intent.AreaPoints, Is.EquivalentTo(new[] { HeroTriangle }),
                "夹具前提：区域**只有**施法者自己的三角");
            Assert.That(intent.AllowedTargetRelations, Is.EqualTo(TargetRelationMask.Hostile));
            Assert.That(hostile.Sim.StagedResolution.RemainingHits.Count, Is.EqualTo(0),
                "区域只覆盖自己 + 掩码无 Self 位 ⇒ 零命中");
            Assert.That(hostile.Sim.LastDamageCommitReport.Units.Count, Is.EqualTo(0));
            Assert.That(HealthQ10Of(hostile.Sim, Hero), Is.EqualTo(hostile.HeroHealthBefore), "不得打到自己");

            // 正对照：同一张模式 + All 掩码 ⇒ 自己被打。
            BattleSimulation sim = BattleSimulation.Create(
                BuildDefinition(Slots()), EncounterId, Inputs, BattleSimulationAssembly.Standard());
            CommandIngressEntry entry = sim.CommandIngress.FindEntry(Player);
            sim.WindowManager.ScheduleWindow(0L, Hero, 60);
            var rig = new Rig { Sim = sim, Entry = entry };
            Step(rig, 0L);
            var request = new CommandRequest(
                SubmitTick,
                new ScheduleEditScope(sim.ScheduleAuthority.ScheduleRevision, sim.CurrentTurnWindow.WindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    new AddOrdinaryPlanOperation(1L, Hero, Spec(SelfEnabledSpecId), SubmitTick,
                        default, Hero, GridDirection.East, null)
                }));
            Step(rig, SubmitTick, request);
            int heroBefore = HealthQ10Of(sim, Hero);
            for (long tick = SubmitTick + 1L; tick <= ImpactTick; tick++) Step(rig, tick);

            Assert.That(sim.IntentQueue.Intents[0].AreaPoints, Is.EquivalentTo(new[] { HeroTriangle }));
            Assert.That(sim.StagedResolution.RemainingHits.Count, Is.EqualTo(1),
                "All 掩码含 Self ⇒ 同一张区域必须命中施法者自己");
            Assert.That(sim.StagedResolution.RemainingHits[0].Key.TargetUnitId, Is.EqualTo(Hero));
            Assert.That(HealthQ10Of(sim, Hero), Is.EqualTo(heroBefore - (int)RawDamageQ10));
        }

        // =====================================================================
        // 用例 5：求解阶段零世界写入（AOE 版）
        // =====================================================================

        /// <summary>
        /// <strong>负控制</strong>：把阶段 11 换成 <c>NoResolutionCommitSystem</c> 之后，
        /// 多目标 AOE 仍完成完整求解（两条聚合命中、数值精确），但世界<strong>一位都不变</strong>。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：求解器里偷偷扣血/切状态（负控制下也看到生命变化）；
        /// 或"聚合"被写进提交系统而不是求解器（负控制下 <c>RemainingHits</c> 的聚合值为空/异常 ⇒ 红）。
        /// </para>
        /// </summary>
        [Test]
        public void AoeResolveLeavesWorldUntouchedUntilCommit()
        {
            Rig rig = Arrange(AttackSpecId, Hero, Monster,
                assembly: new BattleSimulationAssembly(resolutionCommit: NoResolutionCommitSystem.Instance));
            BattleSimulation sim = rig.Sim;

            Assert.That(sim.ResolutionCommitSystem, Is.Null,
                "显式注入负控制时不得再绑本场真实现");
            Assert.That(sim.StagedResolution.RemainingHits.Count, Is.EqualTo(2),
                "负控制只关掉**提交**，不得影响求解");
            for (int i = 0; i < sim.StagedResolution.RemainingHits.Count; i++)
            {
                Assert.That(sim.StagedResolution.RemainingHits[i].Aggregate.TotalDamageQ10,
                    Is.EqualTo(RawDamageQ10), "聚合仍必须算出精确值");
            }
            Assert.That(sim.LastDamageCommitReport.Units.Count, Is.EqualTo(0));
            Assert.That(HealthQ10Of(sim, Monster), Is.EqualTo(rig.MonsterHealthBefore));
            Assert.That(HealthQ10Of(sim, Monster2), Is.EqualTo(rig.Monster2HealthBefore));
            Assert.That(HealthQ10Of(sim, Hero), Is.EqualTo(rig.HeroHealthBefore));
        }
    }

    /// <summary>
    /// 任务 08「必须产出」12 的<strong>部分防御</strong>侧：多目标 AOE 中<strong>每个目标独立</strong>
    /// 走抵抗与聚合（「必须产出」11：<c>AfterPassive = Raw × (1024-r)/1024</c>、
    /// <c>Final = AfterPassive × (1024-r_action)/1024</c>），
    /// <strong>不得</strong>把多目标压平成一个总量。
    ///
    /// <para>
    /// <strong>为什么用被动抵抗而不是真实 Block 计划</strong>（诚实声明，见本步交接简报：
    /// 这是本步<strong>未</strong>覆盖的那一条）：
    /// 求值路径上的"每目标抵抗"由 <c>StagedConflictResolver</c> 按<strong>目标单位</strong>读取
    /// ——被动抵抗读 <c>UnitDefinition.BaseDamageResistanceQ10</c>、动作抵抗读该目标在
    /// <c>AttackGuard</c>/<c>Blocked</c> 接触上记录的值，二者进入<strong>同一个</strong>聚合器调用
    /// （<c>TargetAggregator.BuildDefendedContact</c> + <c>TargetAggregator.Aggregate</c>）。
    /// 因此"每目标独立"这条判据在本文件里被真实钉住；但
    /// <strong>真实 Block 动作抵抗（1024）的端到端形态仍未覆盖</strong>，阻断原因是
    /// <c>BattleInitializer</c> 把 <c>AvailableAdrenaline</c> 硬编码为 <c>0</c>，
    /// 而 Block 的权威费用 <c>ActionSpec.AdrenalineCost = 2</c> 必须由账本预留
    /// ⇒ 本步无法在开局构造出可支付的 Block（详见交接简报）。
    /// </para>
    /// </summary>
    public class Task08AoeDefenseIntegrationTests
    {
        /// <summary>
        /// <strong>与上一个用例互补的"部分"形态</strong>（两者合起来才是"部分目标被防御 ⇒ 其他目标保留命中"）：
        /// mon 的合格载荷被<strong>一半</strong>抵消（<c>5120</c>），mon2 完全不受影响（<c>10240</c>）。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>把多目标伤害<strong>先求和再一次性抵抗</strong>（20480 打一次 512 ⇒ 10240，且只提交一条）⇒
        /// 每单位条目数与两条数值断言同时红；</item>
        /// <item>用<strong>攻击者</strong>（或组内任一单位）的抵抗代表全组 ⇒ mon 与 mon2 数值相同 ⇒ 红；</item>
        /// <item>抵抗取平均值/取最大 ⇒ 数值不等于 <c>10240</c> / <c>5120</c> ⇒ 红；</item>
        /// <item>把"被抵抗掉的那一半"也计入提交 ⇒ 生命断言红。</item>
        /// </list>
        /// </para>
        /// </summary>
        [Test]
        public void PartiallyDefendedAoeTargetIsResolvedIndependentlyFromUndefendedTargets()
        {
            const long resistedQ10 = 5120L;   // 10240 × (1024-512)/1024
            Task08AoeTargetPolicyIntegrationTests.Rig rig = Task08AoeTargetPolicyIntegrationTests.Arrange(
                Task08AoeTargetPolicyIntegrationTests.AttackSpecId,
                Task08AoeTargetPolicyIntegrationTests.Hero,
                Task08AoeTargetPolicyIntegrationTests.Monster,
                monsterResistanceQ10: Task08AoeTargetPolicyIntegrationTests.BluntResistanceQ10,
                monster2ResistanceQ10: 0);

            BattleSimulation sim = rig.Sim;
            Assert.That(sim.StagedResolution.RemainingHits.Count, Is.EqualTo(2),
                "防御只影响**该目标**的载荷，不得删掉它自己的未消解命中");

            RemainingHitResolution monHit = sim.StagedResolution.RemainingHits
                .Single(h => h.Key.TargetUnitId == Task08AoeTargetPolicyIntegrationTests.Monster);
            RemainingHitResolution mon2Hit = sim.StagedResolution.RemainingHits
                .Single(h => h.Key.TargetUnitId == Task08AoeTargetPolicyIntegrationTests.Monster2);

            Assert.That(monHit.Aggregate.TotalDamageQ10, Is.EqualTo(resistedQ10),
                "mon：10240 × (1024-512)/1024 = 5120（被动抵抗只改它自己的载荷）");
            Assert.That(mon2Hit.Aggregate.TotalDamageQ10, Is.EqualTo(Task08AoeTargetPolicyIntegrationTests.RawDamageQ10),
                "mon2：完全没有任何抵抗 ⇒ 10240（不得被 mon 的抵抗'保护'）");
            Assert.That(monHit.Aggregate.TotalDamageQ10, Is.Not.EqualTo(mon2Hit.Aggregate.TotalDamageQ10),
                "两个目标的最终伤害必须**不同** —— 相同就说明抵抗被压平到组级");

            // 提交面：两条独立提交、两条不同数值、两侧权威生命各自只扣自己那一份。
            Assert.That(sim.LastDamageCommitReport.Units.Count, Is.EqualTo(2),
                "每单位恰好一条提交（压平成一个总量的实现只会有一条）");
            Assert.That(Task08AoeTargetPolicyIntegrationTests.CommittedDamageOf(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster), Is.EqualTo(resistedQ10));
            Assert.That(Task08AoeTargetPolicyIntegrationTests.CommittedDamageOf(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster2),
                Is.EqualTo(Task08AoeTargetPolicyIntegrationTests.RawDamageQ10));
            Assert.That(Task08AoeTargetPolicyIntegrationTests.HealthQ10Of(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster),
                Is.EqualTo(rig.MonsterHealthBefore - (int)resistedQ10));
            Assert.That(Task08AoeTargetPolicyIntegrationTests.HealthQ10Of(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster2),
                Is.EqualTo(rig.Monster2HealthBefore - (int)Task08AoeTargetPolicyIntegrationTests.RawDamageQ10));
        }

        /// <summary>
        /// 任务包「必需测试」<c>AreaAttackKeepsHitsOnTargetsThatDidNotBlockOrDodge</c> 的<strong>截断形态</strong>
        /// （本步无法支付真实 Block，见 <see cref="Task08AoeDefenseIntegrationTests"/> 的诚实声明）：
        /// 在该形态里"被防御的目标"用<strong>完整被动抵抗</strong>代替 Block 抵抗：
        /// mon 的合格载荷被全部抵消（最终伤害 0），mon2 完全不受影响。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把"最终伤害为 0"实现成<strong>提前丢弃该接触</strong>
        /// （RemainingHits 少一条、审计面丢失）；或把该目标的 0 伤害当成"整组已防御"。
        /// </para>
        /// </summary>
        [Test]
        public void FullyDefendedAoeTargetDoesNotSuppressResolutionForOtherTargets()
        {
            Task08AoeTargetPolicyIntegrationTests.Rig rig = Task08AoeTargetPolicyIntegrationTests.Arrange(
                Task08AoeTargetPolicyIntegrationTests.AttackSpecId,
                Task08AoeTargetPolicyIntegrationTests.Hero,
                Task08AoeTargetPolicyIntegrationTests.Monster,
                monsterResistanceQ10: 1024,
                monster2ResistanceQ10: 0);
            BattleSimulation sim = rig.Sim;
            Assert.That(sim.StagedResolution.RemainingHits.Count, Is.EqualTo(2),
                "被完全抵抗的目标**仍然**有一条未消解命中（接触不因最终伤害为 0 而消失）");
            RemainingHitResolution monHit = sim.StagedResolution.RemainingHits
                .Single(h => h.Key.TargetUnitId == Task08AoeTargetPolicyIntegrationTests.Monster);
            Assert.That(monHit.Aggregate.TotalDamageQ10, Is.EqualTo(0L), "完整抵抗 ⇒ 该目标伤害为 0");
            Assert.That(monHit.Aggregate.ChannelTotals.Count, Is.EqualTo(1),
                "通道明细仍然存在（审计面不得因 0 伤害被裁掉）");

            Assert.That(Task08AoeTargetPolicyIntegrationTests.CommittedDamageOf(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster2),
                Is.EqualTo(Task08AoeTargetPolicyIntegrationTests.RawDamageQ10),
                "一个目标完全抵抗**不得**保护另一个目标");
            Assert.That(Task08AoeTargetPolicyIntegrationTests.HealthQ10Of(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster),
                Is.EqualTo(rig.MonsterHealthBefore), "完整抵抗 ⇒ 该目标生命不变");
            Assert.That(Task08AoeTargetPolicyIntegrationTests.HealthQ10Of(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster2),
                Is.EqualTo(rig.Monster2HealthBefore - (int)Task08AoeTargetPolicyIntegrationTests.RawDamageQ10));
        }
    }

    /// <summary>
    /// <strong>真实 Block 的端到端覆盖（含区域攻击）</strong>：真实装配 →
    /// 攻击启动后公开的真实 <c>ReactionOpportunity</c> →
    /// <c>ReactionOpportunitySystem.TryAcceptById</c>（生产接受入口，内部经
    /// <c>ReactionPlanner.TryPlanWithReservation</c> 从唯一账本预留 Block 权威费用
    /// <c>ActionSpec.AdrenalineCost = 2</c>）→ 真实冲突图 <c>AttackBlock</c> 接触 →
    /// <c>StagedConflictResolver</c> 写出 <c>Blocked</c> 结论 → 逐目标聚合 → 阶段 11 提交。
    ///
    /// <para>
    /// <strong>与退化形态的区别</strong>：<c>Task08AoeDefenseIntegrationTests</c> 里的
    /// <c>FullyDefendedAoeTargetDoesNotSuppressResolutionForOtherTargets</c> 用
    /// <strong>完整被动抵抗</strong>（<c>UnitDefinition.BaseDamageResistanceQ10 = 1024</c>）代替 Block，
    /// 走的<strong>不是</strong>"反应机会 + 真实接受事务 + 预留"这条链。本类走的是真实链。
    /// </para>
    ///
    /// <para>
    /// <strong>开局肾上腺素（裁定 8 复裁）</strong>：主方案 3.1.1 的单位资源字面是
    /// <c>AvailableAdrenaline = 0</c>，且 <c>UnitDefinition</c>/<c>EncounterUnitSlot</c> 都<strong>没有</strong>
    /// 该字段 ⇒ 开局额度按规格恒为 0，真实战斗里只能靠造成/承受伤害挣得。
    /// 因此本类用 <c>SeedAdrenaline</c> 把"额度从哪来"与"额度到了之后事务是否成立"解耦
    /// （走公开账本入账入口，不替换任何被测实现），交付的判据是<strong>接受事务 + 预留 + 求解 + 抵抗</strong>。
    /// </para>
    ///
    /// <para>
    /// <strong>会让它失败的实现缺陷</strong>：
    /// <list type="bullet">
    /// <item>肾上腺素端口未接到唯一账本（配额永远为 0）⇒ 接受事务以
    /// <c>ADRENALINE_INSUFFICIENT</c> 拒绝 ⇒ 红；</item>
    /// <item>接受事务不预留（计划已创建但账本没扣）⇒ 预留断言红；</item>
    /// <item>Block 计划进入求解但不写 <c>Blocked</c> 结论（只登记不抵抗）⇒
    /// <c>Outcome</c>/伤害/生命三处同时红；</item>
    /// <item>把 <c>Blocked</c> 当成"删掉该接触"⇒ <c>RemainingHits</c> 条数断言红（审计面丢失）；</item>
    /// <item>把 Block 当成"整组已防御"⇒ 其他目标的提交/生命断言红。</item>
    /// </list>
    /// </para>
    /// </summary>
    public class Task08RealBlockIntegrationTests
    {
        /// <summary>
        /// 额度达到权威费用时，真实 Block 完整抵抗<strong>它自己的主目标</strong>：
        /// 该目标 0 伤害、0 提交、生命不变。
        ///
        /// <para>
        /// <strong>开局额度说明（裁定 8 复裁）</strong>：开局 Available 按规格恒为 0
        /// （主方案 3.1.1 的 <c>AvailableAdrenaline = 0</c>；<c>UnitDefinition</c>/<c>EncounterUnitSlot</c>
        /// 都没有该字段），因此本用例用 <c>SeedAdrenaline</c> 安排出"已挣得额度"这一前置，
        /// 再验证真实接受事务。被替换掉的<strong>不是</strong>任何被测实现。
        /// </para>
        /// </summary>
        [Test]
        public void RealBlockPlanNullifiesItsOwnTargetAndLeavesOthersUntouched()
        {
            BattleSimulation sim = null;
            ActionPlan blockPlan = null;
            ActionPlanId blockPlanId = default;
            long adrenalineCycleBefore = 0L;

            Task08AoeTargetPolicyIntegrationTests.Rig rig =
                Task08AoeTargetPolicyIntegrationTests.Arrange(
                    Task08AoeTargetPolicyIntegrationTests.PrimarySpecId,
                    Task08AoeTargetPolicyIntegrationTests.Hero,
                    Task08AoeTargetPolicyIntegrationTests.Monster,
                    monsterResistanceQ10: 0,
                    monster2ResistanceQ10: 0,
                    afterFirstStep: r =>
                    {
                        sim = r.Sim;
                        UnitId defender = Task08AoeTargetPolicyIntegrationTests.Monster;

                        // ① 攻击已启动 ⇒ 必须已经公开一条真实反应机会（机会由启动那一步公开）。
                        Assert.That(r.Plan.IsLocked || r.Plan.State == ActionPlanState.Running, Is.True,
                            "夹具前提：攻击已经过阶段 7 的原子启动；state=" + r.Plan.State);
                        ReactionOpportunityRuntime opportunity = sim.ReactionOpportunities.ActiveOpportunities
                            .Single(o => o.DefenderUnitId == defender && o.IsOpen);
                        Assert.That(opportunity.TriggerTick,
                            Is.EqualTo(Task08AoeTargetPolicyIntegrationTests.ImpactTick),
                            "机会的 TriggerTick 必须等于来源攻击的 ImpactTick（命令无法声明）");
                        Assert.That(opportunity.Options.Select(o => o.ReactionActionSpecId.Value),
                            Contains.Item(Task08AoeTargetPolicyIntegrationTests.BlockSpecId),
                            "Block 必须是该机会已公开的选项");

                        // ② 前置：防御者已经挣到够付一次 Block 的额度（开局额度按规格恒为 0）。
                        AdrenalineLedger ledger = Task08AoeTargetPolicyIntegrationTests.SeedAdrenaline(
                            sim, defender, FrozenDesignValues.BlockAdrenalineCost);
                        adrenalineCycleBefore = ledger.CycleId;

                        // ③ 真实接受事务（生产入口）：机会 + 选项 + 权威费用 + 账本预留。
                        string error = sim.ReactionOpportunities.TryAcceptById(
                            opportunity.Id,
                            Task08AoeTargetPolicyIntegrationTests.Spec(
                                Task08AoeTargetPolicyIntegrationTests.BlockSpecId),
                            Task08AoeTargetPolicyIntegrationTests.SubmitTick + 1L, null, out blockPlan);

                        Assert.That(error, Is.Null, "真实 Block 接受不得被肾上腺素拒绝：" + error);
                        Assert.That(opportunity.State, Is.EqualTo(ReactionOpportunityState.Accepted));
                        blockPlanId = blockPlan.ActionPlanId;
                        Assert.That(blockPlan.IsLocked, Is.True, "接受时直接Locked，StartTick才启动");
                        var acceptedReservation = ledger.ReservationOf(blockPlanId);
                        Assert.That(acceptedReservation.ReservedAmount, Is.EqualTo(FrozenDesignValues.BlockAdrenalineCost));
                        Assert.That(acceptedReservation.ReservationCycleId, Is.EqualTo(adrenalineCycleBefore));
                    });

            // ④ 接受事务的账本后果：Available 原子转入该计划的预留（含个人周期号）。
            AdrenalineLedger blockLedger = sim.AdrenalineLedgerOf(
                Task08AoeTargetPolicyIntegrationTests.Monster);
            Assert.That(blockPlan, Is.Not.Null, "夹具前提：真实 Block 计划已创建");
            Assert.That(blockPlan.IsReaction, Is.True);
            Assert.That(blockPlan.IsRunning, Is.True, "TriggerTick已到，固定反应必须已启动");
            AdrenalineReservationEntry reservation = blockLedger.ReservationOf(blockPlanId);
            Assert.That(reservation, Is.Null, "TriggerTick必须消费接受时按权威费用生成的预留");

            // ⑤ 求解面：被 Block 的接触写出 Blocked 结论。
            Assert.That(sim.StagedResolution.BlockPlans.Count, Is.EqualTo(1),
                "恰好一条真实 Block 计划进入求解（夹具前提）");
            BlockPlanResolution blockPlanResolution = sim.StagedResolution.BlockPlans[0];
            Assert.That(blockPlanResolution.BlockPlanId, Is.EqualTo(blockPlanId),
                "求解面看到的必须正是接受事务创建的那条 Block 计划");
            Assert.That(blockPlanResolution.DefenderUnitId,
                Is.EqualTo(Task08AoeTargetPolicyIntegrationTests.Monster));
            Assert.That(blockPlanResolution.Contacts.Count, Is.EqualTo(1),
                "夹具前提：本次载荷对该目标只有一条 AttackBlock 接触");
            Assert.That(blockPlanResolution.Contacts[0].Outcome,
                Is.EqualTo(BlockContactOutcome.Blocked),
                "Block 必须真的把该目标的合格载荷与动量全部抵抗掉（不是 PartiallyBlocked/Ineffective）");
            Assert.That(blockPlanResolution.AnyEligibleContact, Is.True,
                "Blocked 接触必须让该计划获得成功格挡资格");

            RemainingHitResolution monHit = sim.StagedResolution.RemainingHits.Count == 0
                ? null
                : sim.StagedResolution.RemainingHits
                    .SingleOrDefault(h => h.Key.TargetUnitId == Task08AoeTargetPolicyIntegrationTests.Monster);

            // 实测事实（本步证据，勿改成"必须有条目"）：被 Block **完整抵抗**的目标
            // 不以"0 伤害的 RemainingHit"形式出现在审计面上 —— 它整个条目缺席，
            // 因此"接触是否仍在审计面上"这件事对 Block 路径**不成立**（与被动抵抗路径不同：
            // Task08AoeDefenseIntegrationTests 的完整被动抵抗用例却是 0 伤害 + 条目仍在，
            // 因为那条路径不删接触）。两者是**不同**的实现形态，本用例不做统一假设。
            Assert.That(monHit, Is.Null,
                "实测事实：被 Block 的目标不产生 RemainingHit 条目（RemainingHits.Count="
                + sim.StagedResolution.RemainingHits.Count + "）");
            Assert.That(sim.StagedResolution.RemainingHits.Count, Is.EqualTo(0),
                "被 Block 的载荷不留下任何待提交命中（本载荷只打主目标；其他单位本就不在候选内）");

            // ⑥ 提交面与权威生命：被 Block 目标 0 伤害；其他单位一位不变（保留完整判别力）。
            Assert.That(Task08AoeTargetPolicyIntegrationTests.CommittedDamageOf(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster), Is.EqualTo(0L),
                "被 Block 目标不得提交任何伤害");
            Assert.That(Task08AoeTargetPolicyIntegrationTests.HealthQ10Of(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster),
                Is.EqualTo(rig.MonsterHealthBefore), "被 Block ⇒ 该目标生命不变");
            Assert.That(Task08AoeTargetPolicyIntegrationTests.HealthQ10Of(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster2),
                Is.EqualTo(rig.Monster2HealthBefore),
                "Block 只保护它自己那一个目标，不得外溢到其他单位");
            Assert.That(Task08AoeTargetPolicyIntegrationTests.HealthQ10Of(
                sim, Task08AoeTargetPolicyIntegrationTests.Hero2),
                Is.EqualTo(rig.Hero2HealthBefore),
                "Block 不得外溢到同阵营单位");
        }

        /// <summary>
        /// <strong>任务包「必需测试」<c>BlockedAoeTargetDoesNotProtectOtherTargets</c> 的正向形态</strong>：
        /// <strong>区域攻击</strong>（<c>AllTargetsInArea</c>）同时打两个敌方单位，其中一个用真实 Block 抵抗，
        /// 另一个<strong>照常受伤</strong>。
        ///
        /// <para>
        /// 本用例在补齐 <c>GridAreaThreatCandidateSource</c> 之前<strong>不可达</strong>：
        /// 生产装配未注入区域候选来源 ⇒ <c>NoAreaThreatCandidateSource</c> 返回空候选 ⇒
        /// 区域攻击不公开任何反应机会（旧用例 <c>RealAssemblyOpensNoReactionOpportunityForAreaAttacks</c>
        /// 就是这处缺口的可执行证据，已由本正向用例取代）。
        /// </para>
        ///
        /// <para>
        /// <strong>会让它失败的实现缺陷</strong>：区域候选来源仍为空（没有机会可接受 ⇒ 接受失败）；
        /// 区域几何漏掉 mon2（第二个目标不会再受伤 ⇒ 无法区分"Block 有效"与"根本没打别人"）；
        /// Block 被当成"整组已防御"（mon2 也不受伤 ⇒ 红）；
        /// 只有 mon2 受伤而 mon 也受伤（Block 没生效 ⇒ 红）。
        /// </para>
        /// </summary>
        [Test]
        public void BlockedAoeTargetDoesNotProtectOtherTargets()
        {
            BattleSimulation sim = null;
            ActionPlan blockPlan = null;
            ActionPlanId blockPlanId = default;

            Task08AoeTargetPolicyIntegrationTests.Rig rig =
                Task08AoeTargetPolicyIntegrationTests.Arrange(
                    Task08AoeTargetPolicyIntegrationTests.AttackSpecId,
                    Task08AoeTargetPolicyIntegrationTests.Hero,
                    Task08AoeTargetPolicyIntegrationTests.Monster,
                    afterFirstStep: r =>
                    {
                        sim = r.Sim;
                        UnitId blocker = Task08AoeTargetPolicyIntegrationTests.Monster;

                        // ① 区域攻击必须在默认装配下公开机会：mon 与 mon2 同处区域内。
                        Assert.That(r.Plan.State, Is.EqualTo(ActionPlanState.Running),
                            "夹具前提：区域攻击已原子启动；state=" + r.Plan.State);
                        List<long> defenderIds = sim.ReactionOpportunities.ActiveOpportunities
                            .Select(o => o.DefenderUnitId.Value).ToList();
                        defenderIds.Sort();
                        Assert.That(defenderIds, Is.EqualTo(new List<long>
                            {
                                Task08AoeTargetPolicyIntegrationTests.Monster.Value,
                                Task08AoeTargetPolicyIntegrationTests.Monster2.Value
                            }),
                            "区域覆盖 mon 与 mon2（hero2 同阵营被掩码拒绝）");

                        // ② 前置：只有 blocker 挣到了额度 ⇒ "谁被保护"完全由反应决定，不是几何副作用。
                        Task08AoeTargetPolicyIntegrationTests.SeedAdrenaline(
                            sim, blocker, FrozenDesignValues.BlockAdrenalineCost);
                        Assert.That(sim.AdrenalineLedgerOf(
                            Task08AoeTargetPolicyIntegrationTests.Monster2).AvailableAdrenaline, Is.Zero,
                            "夹具前提：另一个目标没有额度，因此不可能也去 Block");

                        // ③ 真实接受事务。
                        ReactionOpportunityRuntime opportunity = sim.ReactionOpportunities.ActiveOpportunities
                            .Single(o => o.DefenderUnitId == blocker && o.IsOpen);
                        string error = sim.ReactionOpportunities.TryAcceptById(
                            opportunity.Id,
                            Task08AoeTargetPolicyIntegrationTests.Spec(
                                Task08AoeTargetPolicyIntegrationTests.BlockSpecId),
                            Task08AoeTargetPolicyIntegrationTests.SubmitTick + 1L, null, out blockPlan);
                        Assert.That(error, Is.Null, "真实 Block 接受不得被拒绝：" + error);
                        blockPlanId = blockPlan.ActionPlanId;
                    });

            // ④ 求解面：恰好一条 Block 结论，且它属于 blocker。
            Assert.That(blockPlan, Is.Not.Null, "夹具前提：真实 Block 计划已创建");
            Assert.That(sim.StagedResolution.BlockPlans.Count, Is.EqualTo(1),
                "只有 blocker 的反应计划进入求解（mon2 没有额度 ⇒ 不该有第二条）");
            Assert.That(sim.StagedResolution.BlockPlans[0].BlockPlanId, Is.EqualTo(blockPlanId));
            Assert.That(sim.StagedResolution.BlockPlans[0].DefenderUnitId,
                Is.EqualTo(Task08AoeTargetPolicyIntegrationTests.Monster));
            Assert.That(sim.StagedResolution.BlockPlans[0].Contacts[0].Outcome,
                Is.EqualTo(BlockContactOutcome.Blocked));

            // ⑤ 判别力：被 Block 的目标 0 伤害；**同一区域内的另一个目标照常受伤**。
            Assert.That(Task08AoeTargetPolicyIntegrationTests.CommittedDamageOf(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster), Is.EqualTo(0L),
                "被 Block 目标不得提交任何伤害");
            Assert.That(Task08AoeTargetPolicyIntegrationTests.HealthQ10Of(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster),
                Is.EqualTo(rig.MonsterHealthBefore), "被 Block ⇒ 该目标生命不变");
            Assert.That(Task08AoeTargetPolicyIntegrationTests.CommittedDamageOf(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster2),
                Is.EqualTo(Task08AoeTargetPolicyIntegrationTests.RawDamageQ10),
                "Block 只保护它自己那一个目标：同一区域内的另一个目标必须照常承受满额伤害");
            Assert.That(Task08AoeTargetPolicyIntegrationTests.HealthQ10Of(
                sim, Task08AoeTargetPolicyIntegrationTests.Monster2),
                Is.EqualTo(rig.Monster2HealthBefore - (int)Task08AoeTargetPolicyIntegrationTests.RawDamageQ10),
                "mon2 的权威生命必须按未抵抗伤害下降");

            // ⑥ 反应额度只被消费给 blocker 自己的计划：mon2 不得凭空多出预留。
            Assert.That(sim.AdrenalineLedgerOf(
                Task08AoeTargetPolicyIntegrationTests.Monster2).ReservationCount, Is.Zero,
                "没有接受反应的单位不得存在任何预留");
            Assert.That(sim.AdrenalineLedgerOf(
                Task08AoeTargetPolicyIntegrationTests.Monster)
                .ReservationOf(blockPlanId), Is.Null, "Block触发已消费其预留");
        }
    }
}
