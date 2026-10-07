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
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 08 裁定 6.2 的仓库内自动化证据：<strong>死亡必须移除网格 footprint</strong>。
    ///
    /// <para>
    /// <strong>为什么必须有这个文件</strong>：在本次接线之前，
    /// <see cref="LogicGrid.UnregisterUnit"/> 在<strong>生产路径零调用</strong>，
    /// <c>BattleSimulation.ProcessDeaths</c> 只改单位状态与计划——
    /// "死亡后不再占格"当时<strong>只是一句注释承诺</strong>。
    /// 而 <see cref="LogicGrid"/> 是唯一空间权威：强制位移求解器的静止占位者判定、
    /// 阶段 9 的占用快照投影、<c>ApplyBatchRelocation</c> 的全批预检全都读它。
    /// 于是死人会永久占格，与任务 08 的"静止单位硬阻挡强制位移"叠加后形成
    /// <strong>幽灵阻挡</strong>：一个永远不可能被任何后续阶段清除的障碍。
    /// </para>
    ///
    /// <para>
    /// <strong>与既有用例的分工</strong>：任务 04 的
    /// <c>LethalUnitStillParticipatesInSameTickDisplacementDependencyGraph</c>
    /// 钉住"致死单位在<strong>同一个 Tick</strong>的位移依赖图里仍然阻挡"（不变量 33 的前半），
    /// 本文件钉住它的另一半：<strong>死亡阶段结束之后</strong> footprint 必须已经消失，
    /// 而且死者腾出的格子对真实的强制位移提交原语真的变成"空的"。
    /// 只有两条合起来才能排除"提前摘除"与"永不摘除"这两种相反的实现缺陷。
    /// </para>
    ///
    /// <para>
    /// <strong>装配口径</strong>：真实 <c>BattleSimulation.Create</c> + 真实阶段链；
    /// 阶段 1 的击杀来自一个只声明"把生命清零"的 <see cref="IUnitStateAdvanceSystem"/> 夹具
    /// （死亡的<strong>提交</strong>仍由真实死亡系统完成，夹具不碰任何状态）；
    /// 空间断言全部走<strong>唯一空间权威</strong>的真实原语
    /// （<c>Contains</c> / <c>TryGetCellOwner</c> / <c>ApplyBatchRelocation</c>），
    /// 不新建第二份空间权威、不反射、不伪造状态。
    /// </para>
    ///
    /// <para>
    /// <strong>几何前提（实测得出，勿随手改）</strong>：本文件的单三角体积表下，
    /// 锚点 <c>(7,7)</c> 朝向 East 占 <c>{9,7;10,8;11,7}</c>、锚点 <c>(5,5)</c> 朝向 West 占
    /// <c>{1,5;2,4;3,5}</c>：两者格集合不相交（初始注册不会互相拒绝），
    /// 且 <c>hero(7,7) → (5,5)</c> 的批量换位在<strong>死者注销后</strong>返回 <c>ok</c>（探针实测）。
    /// </para>
    /// </summary>
    public class Task08DeathFootprintRemovalTests
    {
        // ================= 夹具常量 =================

        private const string ActionSetIdValue = "action_set.t08dfr";
        private const string EncounterIdValue = "encounter.t08dfr";

        private static readonly FactionId HeroFaction = new FactionId("faction.hero");
        private static readonly FactionId MonsterFaction = new FactionId("faction.monster");
        private static readonly ControllerId Player = new ControllerId("controller.player");
        private static readonly ControllerId MonsterAi = new ControllerId("controller.monster_ai");

        /// <summary>UnitId 由 <c>EncounterSlotId</c> 的 Ordinal 决定："hero" &lt; "monster" ⇒ 1 / 2。</summary>
        private static readonly UnitId Hero = new UnitId(1L);
        private static readonly UnitId Monster = new UnitId(2L);

        private static readonly GridPoint HeroAnchor = new GridPoint(9, 5);
        private static readonly GridPoint DeadUnitAnchor = new GridPoint(5, 5);

        /// <summary>
        /// 死者 footprint 里<strong>确实被占据的格子</strong>（探针实测：锚点 <c>(5,5)</c> 朝 West 时
        /// 三角是 <c>(2,5,-1)</c>，因此它在格级占 <c>(1,5)/(2,4)/(3,5)</c> 三格）。
        ///
        /// 无 <c>T</c> 的 <see cref="GridPoint"/> 版本在这里是<strong>故意</strong>的：
        /// 占用索引 <c>TryGetCellOwner</c> 按格投影（同一格上的任意三角都算占用），
        /// 这正是"幽灵阻挡"在格级可观察的形态。
        /// </summary>
        private static readonly GridPoint DeadUnitOccupiedCell = new GridPoint(3, 5);

        /// <summary>
        /// <strong>探针实测会撞上死者 footprint 的目的锚点</strong>，也是唯一命中的那个：
        /// 存活单位在 <c>(1,5)</c> 的目标格集是 <c>{(1,5),(2,6),(3,5)}</c>，
        /// 其中 <c>(3,5)</c> 正是死者的占用格 ⇒ 批量提交以
        /// <c>LOGIC_GRID_OCCUPIED_BY_OTHER:cell=(3, 5)</c> 失败。
        ///
        /// 把"死者仍注册 ⇒ 提交必红"这一关键前提钉在<strong>实测坐标</strong>上，
        /// 否则用例可能只是"本来就没挡"而静默失去判别力。
        /// </summary>
        private static readonly GridPoint DeadUnitBlockingDestination = new GridPoint(1, 5);

        private static IReadOnlyList<DirectionalTriangleSet> Directions()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        // ================= 定义夹具 =================

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

            // 攻击 Pattern 为 null：本文件从不物化 Intent（没有任何攻击计划被排程），
            // 而 Pattern 校验发生在物化边界，因此这里不需要 12 向表。
            var attackSpec = new ActionSpec(
                new ActionSpecId("action.t08dfr.attack"), ActionType.Attack,
                new AttackTimingSpec(10, 2),
                new AttackPayloadSpec(
                    new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f,
                        DamageChannelCatalog.GetDefaultTags(DamageChannels.PhysicalBlunt)) },
                    ImpactProfileId: ImpactProfiles.Blunt,
                    ForceMultiplier: 1f,
                    TargetPolicy: TargetPolicy.PrimaryTargetOnly,
                    AllowedTargetRelations: TargetRelationMask.Hostile,
                    MomentumDirectionOffsetSteps: 0,
                    Pattern: null,
                    Tags: AttackTagMask.Reactable),
                AdrenalineCost: 0);

            var volume = new VolumeSpec(new VolumeSpecId("unit_volume.t08dfr"), Directions());
            var actionSet = new ActionSetDefinition(
                new ActionSetId(ActionSetIdValue), new List<ActionSpecId> { attackSpec.ActionSpecId });

            var heroDefinitionId = new UnitDefinitionId("unit.t08dfr.hero");
            var monsterDefinitionId = new UnitDefinitionId("unit.t08dfr.monster");

            var units = new List<UnitDefinition>
            {
                new UnitDefinition(heroDefinitionId, 10f, 10f, 1f, 1f,
                    new Dictionary<DamageChannelId, int>(), 200f, actionSet.ActionSetId, volume.VolumeSpecId),
                new UnitDefinition(monsterDefinitionId, 10f, 10f, 1f, 1f,
                    new Dictionary<DamageChannelId, int>(), 200f, actionSet.ActionSetId, volume.VolumeSpecId)
            };

            var encounter = new EncounterDefinition(
                new EncounterDefinitionId(EncounterIdValue),
                new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40)),
                new List<EncounterUnitSlot>
                {
                    new EncounterUnitSlot(new EncounterSlotId("hero"), heroDefinitionId, HeroFaction,
                        HeroAnchor, GridDirection.East),
                    new EncounterUnitSlot(new EncounterSlotId("monster"), monsterDefinitionId, MonsterFaction,
                        DeadUnitAnchor, GridDirection.West)
                },
                new List<ControllerBinding>
                {
                    new ControllerBinding(Player, CommandSourceKind.Player,
                        new List<EncounterSlotId> { new EncounterSlotId("hero") }),
                    new ControllerBinding(MonsterAi, CommandSourceKind.Ai,
                        new List<EncounterSlotId> { new EncounterSlotId("monster") })
                },
                new VictoryDefinition(
                    new List<FactionId> { HeroFaction }, new List<FactionId> { MonsterFaction },
                    "result.t08dfr.victory", "result.t08dfr.defeat", "result.t08dfr.draw"));

            return new BattleDefinition(
                "battle-definition.task08.death-footprint",
                BattleRules.FrozenV1.TicksPerSecond,
                BattleRules.FrozenV1,
                ConcurrentActionDefinition.FrozenV1,
                new ReactionRules(CommandIngressLeadTicks: 2, MinimumReactionLeadTicks: 1),
                AdrenalineRules.FrozenV1,
                factionModel,
                new List<DamageChannelDefinition>(),
                new List<ImpactProfileDefinition>(),
                units,
                new List<ActionSpec> { attackSpec },
                new List<AttackPatternSpec>(),
                new List<VolumeSpec> { volume },
                new List<MovementPatternSpec>(),
                new List<ActionSetDefinition> { actionSet },
                new List<StatusEffectSpec>(),
                new List<EncounterDefinition> { encounter },
                null,
                "test-definition-hash.t08.death-footprint");
        }

        // ================= 阶段 1 夹具：把生命清零 =================

        /// <summary>
        /// 只声明"把指定单位的生命设为 0"的推进系统。<strong>它不写任何其他字段</strong>
        /// （不改状态、不加效果、不碰网格）：死亡的全部提交仍由真实死亡系统完成，
        /// 因此本用例验证的是生产路径，而不是夹具行为。
        /// </summary>
        private sealed class KillAtTick : IUnitStateAdvanceSystem
        {
            public long Tick = -1L;
            public long UnitId = -1L;

            public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(
                IReadOnlyList<UnitSnapshot> units, long tick)
                => tick == Tick && UnitId >= 0
                    ? new[] { new UnitStateAdvanceRequest(UnitId, (int?)0, null, null) }
                    : Array.Empty<UnitStateAdvanceRequest>();
        }

        /// <summary>
        /// 阶段 2 的胜负宣布推迟一格：否则 Tick 1 的阶段 2 直接结束战斗、
        /// 阶段 15 根本不执行，本文件就会落在"死亡阶段从未运行"的假路径上。
        /// </summary>
        private sealed class DeferVictoryAtTick : IVictoryEvaluator, IPreCommandVictoryGate
        {
            public long DeferTick = -1L;
            public int GateCalls { get; private set; }

            public string EvaluatePreCommandResult()
            {
                GateCalls++;
                return null;
            }

            public string Evaluate(IReadOnlyList<UnitSnapshot> units, VictoryDefinition victory, long tick)
                => tick == DeferTick ? null : FactionEliminationVictoryEvaluator.Instance.Evaluate(units, victory, tick);
        }

        // ================= 编排 =================

        private sealed class Rig
        {
            public BattleSimulation Sim;
            public DeferVictoryAtTick Gate;
            public readonly List<LogicEvent> Events = new List<LogicEvent>();
        }

        private static Rig Arrange()
        {
            var gate = new DeferVictoryAtTick { DeferTick = 1L };
            var kill = new KillAtTick { Tick = 1L, UnitId = Monster.Value };
            BattleSimulation sim = BattleSimulation.Create(
                BuildDefinition(), new EncounterDefinitionId(EncounterIdValue),
                new BattleRuntimeInputs(InitialRngSeed: 11UL, InitialMetaResource: 0),
                new BattleSimulationAssembly(unitStateAdvance: kill, victoryEvaluator: gate));

            // —— 几何前提自检（夹具不变量，不是被测行为）——
            Assert.That(sim.LogicGrid.TryGetAnchor(Hero, out GridPoint heroAnchor), Is.True);
            Assert.That(heroAnchor, Is.EqualTo(HeroAnchor), "夹具前提：hero 锚点来自定义");
            Assert.That(sim.LogicGrid.TryGetAnchor(Monster, out GridPoint monsterAnchor), Is.True);
            Assert.That(monsterAnchor, Is.EqualTo(DeadUnitAnchor), "夹具前提：monster 锚点来自定义");

            return new Rig { Sim = sim, Gate = gate };
        }

        private static StepResult StepOnce(Rig rig, long tick)
        {
            FrozenCommandBatch batch = rig.Sim.CommandIngress.FreezeTick(tick);
            StepResult result = rig.Sim.Step(tick, batch);
            for (int i = 0; i < result.Events.Count; i++) rig.Events.Add(result.Events.Events[i]);
            return result;
        }

        // =====================================================================
        // 用例 1：死亡阶段真的注销（接线前恒红）
        // =====================================================================

        /// <summary>
        /// <strong>死亡提交之后，死者在唯一空间权威里不再存在</strong>，
        /// 它原来的格子不再被任何单位占据；存活单位原样保留；重复处理是幂等的。
        ///
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item><c>ProcessDeaths</c> 不调 <c>UnregisterUnit</c>（本步之前的真实形态）⇒
        /// <c>Contains</c> 恒为 <c>true</c>，本用例立刻红；</item>
        /// <item>只在阶段 2 的"命令前结束"分支注销、阶段 15 分支漏掉 ⇒
        /// <c>LastStepExecuted(PostDisplacementDeath)</c> 为真而 <c>Contains</c> 仍为真（红）；</item>
        /// <item>注销错单位，或"扫一片"⇒ 存活单位的 <c>Contains</c>/锚点断言红；</item>
        /// <item>注销时机提前到位移之前 ⇒ 由任务 04 的
        /// <c>LethalUnitStillParticipatesInSameTickDisplacementDependencyGraph</c> 排除。</item>
        /// </list>
        /// </summary>
        [Test]
        public void DeathRemovesFinalFootprintFromLogicGrid()
        {
            Rig rig = Arrange();
            BattleSimulation sim = rig.Sim;

            StepOnce(rig, 0L);   // Tick 0：正常推进，无死亡
            Assert.That(sim.LogicGrid.Contains(Monster), Is.True, "Tick 0 未致死：死者仍必须在网格里");
            Assert.That(sim.LogicGrid.Contains(Hero), Is.True);

            long tick = 1L;
            while (tick <= 1L)
            {
                StepOnce(rig, tick);
                tick++;
            }

            Assert.That(sim.LastStepExecuted(StepPhase.PostDisplacementDeath), Is.True,
                "夹具前提：死亡阶段在本 Tick 真实执行（胜负被推迟）");
            Assert.That(sim.LastDeathFootprintRemovals.Select(u => u.Value).ToArray(),
                Is.EqualTo(new[] { Monster.Value }), "夹具前提：本 Tick 恰好提交一个死亡");
            Assert.That(sim.CurrentSnapshot.Units.Count(u => u.UnitId == Monster.Value && !u.IsAlive),
                Is.EqualTo(1), "夹具前提：死者已在快照里标记为不存活");

            // —— 断言（本次接线前这里是红的）——
            Assert.That(sim.LogicGrid.Contains(Monster), Is.False,
                "死亡必须移除最终 footprint：死亡阶段之后死者不得再留在唯一空间权威里（幽灵阻挡）");
            Assert.That(sim.LogicGrid.TryGetCellOwner(DeadUnitOccupiedCell, out UnitId residualOwner), Is.False,
                "死者原格不得再被任何单位占据；实测残留 owner="
                + (residualOwner.IsValid ? residualOwner.Value.ToString() : "<none>"));

            // 占用投影（冲突图 Input 的 UnitsBefore/UnitsAfter 的唯一来源，任务 08（必须产出）5）
            // 也不得再含死者：投影直接取自 RegisteredUnitsOrdered，因此"幽灵阻挡"在这条路径上
            // 表现为"死者永远出现在本 Tick 的空间快照里"（= 永远可以被攻击/被接触选中）。
            Assert.That(sim.LogicGrid.RegisteredUnitsOrdered().Select(u => u.Value).ToArray(),
                Is.EqualTo(new[] { Hero.Value }),
                "单位投影只能含存活单位");

            // 存活单位必须原样保留。
            Assert.That(sim.LogicGrid.Contains(Hero), Is.True, "注销只针对死者");
            Assert.That(sim.LogicGrid.TryGetAnchor(Hero, out GridPoint heroAnchor), Is.True);
            Assert.That(heroAnchor, Is.EqualTo(HeroAnchor));

            // —— 幂等：再跑一个 Tick，阶段 15 必然再次执行，但死者不进 IsKillable ——
            StepOnce(rig, 2L);
            Assert.That(sim.LastStepExecuted(StepPhase.PostDisplacementDeath), Is.True,
                "夹具前提：阶段 15 在 Tick 2 再次执行（幂等性才有意义）");
            Assert.That(sim.LastDeathFootprintRemovals, Is.Empty, "本 Tick 没有新死者");
            Assert.That(sim.LogicGrid.Contains(Monster), Is.False,
                "重复处理不得把死者重新注册回网格");
        }

        // =====================================================================
        // 用例 2：死者腾出的格对真实位移提交真的变成"空的"（接线前恒红）
        // =====================================================================

        /// <summary>
        /// <strong>死者腾出的格子对强制位移提交原语真的变成"空的"</strong>：
        /// 用<strong>唯一空间权威</strong>的真实批量提交原语
        /// （<see cref="LogicGrid.ApplyBatchRelocation"/>，生产路径阶段 13 调用的同一个）
        /// 把存活单位换位到死者的原锚点。
        ///
        /// 该原语的预检第 5 条明确是"移除批次内全部旧 footprint 后，新 footprint 不得与
        /// <strong>静止单位</strong>相交"——死者若仍注册，它就是一个静止占位者，
        /// 提交会以 <c>LOGIC_GRID_OCCUPIED_BY_OTHER</c> 失败。
        ///
        /// 会让它失败的实现缺陷：<c>ProcessDeaths</c> 不注销（幽灵阻挡）；
        /// 注销<strong>错格</strong>（移除死者的起始格而不是最终格）。
        /// </summary>
        [Test]
        public void DeadUnitFootprintNoLongerBlocksForcedDisplacementCommit()
        {
            Rig rig = Arrange();
            BattleSimulation sim = rig.Sim;

            // —— 接线前的对照量：死者仍注册时，指向它占用格的位移必然失败 ——
            //
            // 这条断言就是"幽灵阻挡"的可观察形态，它同时是本用例的**夹具前提**：
            // 若目标格根本没被死者占住，下面的成功断言就毫无判别力（可能只是"本来就没挡"）。
            string whileAlive = sim.LogicGrid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(Hero, HeroAnchor, DeadUnitBlockingDestination)
            });
            Assert.That(whileAlive, Is.EqualTo(LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER + ":cell=" + DeadUnitOccupiedCell),
                "夹具前提：死者仍注册时它就是静止占位者，真实批量提交必须以 OCCUPIED_BY_OTHER 失败"
                + "（这正是实现缺失时可观察到的幽灵阻挡）；实测=" + (whileAlive ?? "<null>"));

            StepOnce(rig, 0L);
            StepOnce(rig, 1L);   // 击杀 + 阶段 15 注销

            string snap = " containsMonster=" + sim.LogicGrid.Contains(Monster)
                + " registered=[" + string.Join(";", sim.LogicGrid.RegisteredUnitsOrdered().Select(u => u.Value)) + "]"
                + " removals=[" + string.Join(";", sim.LastDeathFootprintRemovals.Select(u => u.Value)) + "]"
                + " " + sim.LogicGrid.Describe();

            Assert.That(sim.LogicGrid.Contains(Monster), Is.False, "夹具前提：Tick 1 末死者已注销" + snap);

            // 死者腾出的占用格在其后的**任何**位移提交之前必须无主人。
            // （顺序很重要：下面那次成功提交会把 hero 换到死者腾出的格上，
            //  因此"格无主人"只能在提交之前断言——这也是实现缺失时的幽灵阻挡形态。）
            Assert.That(sim.LogicGrid.TryGetCellOwner(DeadUnitOccupiedCell, out UnitId residualCellOwner), Is.False,
                "死者占用格（幽灵阻挡的判据面）必须无主人；实测 owner="
                + (residualCellOwner.IsValid ? residualCellOwner.Value.ToString() : "<none>") + snap);

            // 同一个提交（同一对 From/To）在死者注销后必须成功：唯一变化的输入就是注销本身。
            string error = sim.LogicGrid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(Hero, HeroAnchor, DeadUnitBlockingDestination)
            });
            Assert.That(error, Is.Null,
                "同一个提交在死者注销后必须成功：若死者仍注册，它就是一个静止占位者 ⇒ 再次 OCCUPIED_BY_OTHER"
                + "（幽灵阻挡）；实测=" + (error ?? "<null>") + snap);
            Assert.That(sim.LogicGrid.Contains(Monster), Is.False,
                "位移提交不得把死者重新带回网格" + snap);

            // 提交确实落到了真实空间权威上：死者腾出的格现在归存活的 hero。
            Assert.That(sim.LogicGrid.TryGetCellOwner(DeadUnitOccupiedCell, out UnitId newOwner), Is.True,
                "提交成功后死者占用格必须归 hero" + snap);
            Assert.That(newOwner, Is.EqualTo(Hero),
                "死者让出的格现在的唯一主人是存活的 hero，而不是死者；实测=" + newOwner.Value + snap);
            Assert.That(sim.LogicGrid.RegisteredUnitsOrdered().Select(u => u.Value).ToArray(),
                Is.EqualTo(new[] { Hero.Value }), "唯一登记在册的单位是存活方" + snap);
        }
    }
}
