using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 08 第二条实施流：「必须产出」1（<see cref="CombatIntent"/>）、4（全局 Intent 队列）、
    /// 5 的构图部分（<see cref="InteractionCandidateBuilder"/>）、6（<see cref="ConflictGraphBuilder"/>）
    /// 与「冲突图契约」全节的纯逻辑用例。
    ///
    /// 命名规则：任务包「必需测试」里能<strong>完整</strong>实现的用例逐字同名；
    /// 依赖尚未实现的求解器/伤害公式的用例（<c>TwoAttackersFlankingOneTargetBothDealDamage</c>、
    /// <c>ThreeWayAttackResultIsIndependentOfAllIntentPermutations</c> 等）在本文件里不存在，
    /// 已在交接简报中列出。排列不变性、组键、共享目标三项另用补充命名覆盖。
    ///
    /// 每个用例前都写明"哪种实现缺陷会让它失败"，避免把断言写成同义反复。
    /// </summary>
    public class Task08ConflictGraphTests
    {
        // ================================================================= 夹具

        private const long Tick = 10L;

        private static readonly FactionId Beast = new FactionId("beast");
        private static readonly ControllerId PlayerController = new ControllerId("controller.player");
        private static readonly ControllerId AiController = new ControllerId("controller.ai.a");
        private static readonly ControllerId AiController2 = new ControllerId("controller.ai.b");

        private static readonly ActionSpecId AttackSpecId = new ActionSpecId("action.test.slash");
        private static readonly AttackPatternSpec GridPattern = BuildGridPattern();

        private static UnitId U(long id) => new UnitId(id);

        private static ActionPlanId P(long id) => new ActionPlanId(id);

        private static WindowId W(long id) => new WindowId(id);

        private static GridPoint G(int x, int y) => new GridPoint(x, y);

        /// <summary>
        /// 每个朝向 4 个相对三角形，沿 X 轴间隔 4：既能构造"故意不重叠"的两片区域
        /// （平移 2 后仍不相交），也能构造完全重合的区域。点满足 x+y 为奇数 ⇒ T=1 合法。
        /// </summary>
        private static AttackPatternSpec BuildGridPattern()
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
                    points.Add(new TrianglePoint(4 * j + dx, 1 + dy, 1));
                }
                directions.Add(new DirectionalTriangleSet(facing, points));
            }
            return new AttackPatternSpec(new AttackPatternId("attack.pattern.test.grid"), directions);
        }

        /// <summary>
        /// 故意<strong>非旋转族</strong>的 Pattern：朝向 f 的唯一相对点 = <c>(100f, 1, 1)</c>。
        /// 任何"运行时把朝向 0 的表旋转 f*30°"的实现都不可能得到这张表。
        /// </summary>
        private static AttackPatternSpec BuildNonRotationalPattern()
        {
            var directions = new List<DirectionalTriangleSet>(GridDirectionInfo.DirectionCount);
            for (int f = 0; f < GridDirectionInfo.DirectionCount; f++)
            {
                var facing = (GridDirection)f;
                directions.Add(new DirectionalTriangleSet(
                    facing, new[] { new TrianglePoint(100 * f, 1, 1) }));
            }
            return new AttackPatternSpec(new AttackPatternId("attack.pattern.test.nonrot"), directions);
        }

        private static FactionRelationResolver BuildResolver()
        {
            var model = new FactionModelDefinition(
                new[]
                {
                    new FactionDefinition(FactionIds.Hero),
                    new FactionDefinition(FactionIds.Monster),
                    new FactionDefinition(Beast)
                },
                new[]
                {
                    new FactionRelationDefinition(Beast, FactionIds.Hero, FactionDisposition.Neutral),
                    new FactionRelationDefinition(Beast, FactionIds.Monster, FactionDisposition.Hostile),
                    new FactionRelationDefinition(FactionIds.Hero, FactionIds.Monster, FactionDisposition.Hostile)
                });

            var map = new Dictionary<UnitId, FactionId>
            {
                { U(1), FactionIds.Hero },
                { U(2), FactionIds.Hero },
                { U(3), FactionIds.Monster },
                { U(4), FactionIds.Monster },
                { U(5), Beast }
            };
            return new FactionRelationResolver(model, map);
        }

        private static CombatIntent Attack(
            long planId,
            long ownerId,
            long sequence,
            GridDirection facing = GridDirection.North,
            GridPoint? anchor = null,
            long impactTick = Tick,
            TargetRelationMask mask = TargetRelationMask.Hostile,
            TargetPolicy policy = TargetPolicy.AllTargetsInArea,
            long? primary = null,
            WindowId? window = null,
            int priority = InteractionPriorities.AttackVsAttack,
            AttackPatternSpec pattern = null,
            AttackTagMask? tags = null)
        {
            return CombatIntentFactory.Create(new AttackIntentRequest(
                P(planId),
                AttackSpecId,
                U(ownerId),
                policy,
                primary.HasValue ? U(primary.Value) : (UnitId?)null,
                mask,
                tags ?? (AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable),
                facing,
                pattern ?? GridPattern,
                anchor ?? G(0, 0),
                impactTick,
                priority,
                sequence,
                new[]
                {
                    new DamageComponentSpec(DamageChannels.PhysicalSlash, 12f, DamageTagMask.Blockable)
                },
                MomentumPacket.Require(facing, 100, ImpactProfiles.Blunt),
                0,
                window));
        }

        private static InteractionPlanFacts AttackFacts(
            long planId,
            long ownerId,
            long impactTick = Tick,
            long startTick = 5L,
            long endTick = 14L,
            bool terminal = false)
        {
            return new InteractionPlanFacts(
                P(planId), U(ownerId), ActionType.Attack, startTick, endTick, impactTick, 0L, 0L, 0L, null, terminal);
        }

        private static InteractionPlanFacts BlockFacts(long planId, long ownerId, long triggerTick, bool terminal = false)
        {
            return new InteractionPlanFacts(
                P(planId), U(ownerId), ActionType.Block, 7L, 12L, 0L, 0L, 0L, triggerTick, null, terminal);
        }

        private static InteractionPlanFacts DodgeFacts(long planId, long ownerId, long triggerTick)
        {
            return new InteractionPlanFacts(
                P(planId), U(ownerId), ActionType.Dodge, 7L, 12L, 0L, 0L, 0L, triggerTick, null, false);
        }

        private static InteractionPlanFacts GuardFacts(long planId, long ownerId, long activeStart, long activeEnd)
        {
            return new InteractionPlanFacts(
                P(planId), U(ownerId), ActionType.Guard, activeStart, activeEnd + 2, 0L,
                activeStart, activeEnd, 0L, null, false);
        }

        private static InteractionPlanFacts MoveFacts(long planId, long ownerId, long startTick, long endTick)
        {
            return new InteractionPlanFacts(
                P(planId), U(ownerId), ActionType.Move, startTick, endTick, 0L, 0L, 0L, 0L, null, false);
        }

        private static UnitOccupancy Unit(long unitId, ControllerId controller, params TrianglePoint[] triangles)
            => new UnitOccupancy(U(unitId), triangles, controller);

        private static UnitOccupancy UnitAt(long unitId, ControllerId controller, CombatIntent intent, int pointIndex)
            => new UnitOccupancy(U(unitId), new[] { intent.AreaPoints[pointIndex] }, controller);

        private static ConflictGraph BuildGraph(
            IEnumerable<CombatIntent> intents,
            IEnumerable<InteractionPlanFacts> plans,
            IEnumerable<UnitOccupancy> before,
            IEnumerable<UnitOccupancy> after,
            IFactionRelationResolver resolver = null,
            long tick = Tick)
        {
            return ConflictGraphBuilder.Build(new ConflictGraphInput(
                tick,
                intents.ToList(),
                plans.ToList(),
                before.ToList(),
                after.ToList(),
                resolver ?? BuildResolver()));
        }

        private static ContactKey Key(ContactType type, long unitA, long planA, long unitB, long planB, long target)
            => ContactKey.Create(type, U(unitA), P(planA), U(unitB), P(planB), U(target));

        // ================================================================= 1. Intent 值数据与唯一性契约

        /// <summary>
        /// 会让它失败的实现缺陷：物化入口不校验 <c>tick == ImpactTick</c>（例如按
        /// <c>tick &gt;= ImpactTick</c>、"最后一次采样"或从动作速度/总时长推导命中时刻），
        /// 或把"计划已终态"当成可忽略的事实继续产生 Intent。
        /// </summary>
        [Test]
        public void AttackIntentExistsOnlyAtImpactTick()
        {
            InteractionPlanFacts facts = AttackFacts(planId: 1, ownerId: 1, impactTick: 10L);
            var payload = new AttackPayloadSpec(
                new[] { new DamageComponentSpec(DamageChannels.PhysicalSlash, 12f, DamageTagMask.Blockable) },
                ImpactProfiles.Blunt,
                1f,
                TargetPolicy.AllTargetsInArea,
                TargetRelationMask.Hostile,
                0,
                GridPattern,
                AttackTagMask.Reactable);

            CombatIntent atImpact = CombatIntentFactory.CreateAtImpactTick(
                facts, AttackSpecId, GridDirection.North, payload, G(0, 0), 10L,
                InteractionPriorities.AttackVsAttack, 1L, MomentumPacket.Require(GridDirection.North, 100, ImpactProfiles.Blunt), 0, null);
            Assert.That(atImpact.ImpactTick, Is.EqualTo(10L));

            foreach (long wrongTick in new[] { 9L, 11L })
            {
                var ex = Assert.Throws<LogicDefinitionException>(() => CombatIntentFactory.CreateAtImpactTick(
                    facts, AttackSpecId, GridDirection.North, payload, G(0, 0), wrongTick,
                    InteractionPriorities.AttackVsAttack, 1L, MomentumPacket.Require(GridDirection.North, 100, ImpactProfiles.Blunt), 0, null));
                Assert.That(ex.ErrorCode, Is.EqualTo(InteractionCodes.INTENT_TICK_MISMATCH));
            }

            InteractionPlanFacts terminated = AttackFacts(planId: 1, ownerId: 1, impactTick: 10L, terminal: true);
            var terminalEx = Assert.Throws<LogicDefinitionException>(() => CombatIntentFactory.CreateAtImpactTick(
                terminated, AttackSpecId, GridDirection.North, payload, G(0, 0), 10L,
                InteractionPriorities.AttackVsAttack, 1L, MomentumPacket.Require(GridDirection.North, 100, ImpactProfiles.Blunt), 0, null));
            Assert.That(terminalEx.ErrorCode, Is.EqualTo(InteractionCodes.INTENT_PLAN_TERMINAL));

            // "恰好一次"的显式校验入口：同计划两次物化 / 序号重复都必须被拒绝。
            CombatIntent sameTickAgain = CombatIntentFactory.CreateAtImpactTick(
                facts, AttackSpecId, GridDirection.North, payload, G(0, 0), 10L,
                InteractionPriorities.AttackVsAttack, 2L, MomentumPacket.Require(GridDirection.North, 100, ImpactProfiles.Blunt), 0, null);
            var duplicateEx = Assert.Throws<LogicDefinitionException>(
                () => CombatIntentContract.ValidateProducedOnce(new[] { atImpact, sameTickAgain }));
            Assert.That(duplicateEx.ErrorCode, Is.EqualTo(InteractionCodes.INTENT_DUPLICATE));

            CombatIntent otherPlanSameSequence = Attack(planId: 2, ownerId: 2, sequence: 1L);
            var sequenceEx = Assert.Throws<LogicDefinitionException>(
                () => CombatIntentContract.ValidateProducedOnce(new[] { atImpact, otherPlanSameSequence }));
            Assert.That(sequenceEx.ErrorCode, Is.EqualTo(InteractionCodes.INTENT_SEQUENCE_DUPLICATE));

            CombatIntentContract.ValidateProducedOnce(new[] { atImpact });
        }

        /// <summary>
        /// 会让它失败的实现缺陷：区域来自"朝向 0 的表 + 运行时旋转"、来自动作速度推导的扇形，
        /// 或任何不按 <c>Directions[(int)Facing]</c> 下标取表的近似实现。
        /// </summary>
        [Test]
        public void AttackAreaUsesCanonicalPreExpandedPatternForEveryDirection()
        {
            for (int f = 0; f < GridDirectionInfo.DirectionCount; f++)
            {
                var facing = (GridDirection)f;
                CombatIntent intent = Attack(planId: 100 + f, ownerId: 1, sequence: 100 + f, facing: facing);
                TrianglePoint[] expected = GridPattern.Directions[f].GetTranslated(G(0, 0)).ToArray();

                Assert.That(intent.AreaPoints.ToArray(), Is.EqualTo(expected),
                    "朝向 " + facing + " 必须直接取规范 12 向整数表");
            }
        }

        /// <summary>
        /// 会让它失败的实现缺陷：对点做浮点缩放/取整、把锚点当成"额外旋转中心"、
        /// 平移后不重新排序或不去重（于是区域集合顺序随 Pattern 书写顺序变化）。
        /// </summary>
        [Test]
        public void AttackAreaOnlyIndexesAndTranslatesIntegerPoints()
        {
            CombatIntent east = Attack(planId: 1, ownerId: 1, sequence: 1, facing: GridDirection.East, anchor: G(4, 2));
            TrianglePoint[] table = GridPattern.Directions[(int)GridDirection.East].Triangles.ToArray();

            Assert.That(east.AreaPoints.Count, Is.EqualTo(table.Length), "平移不得增删点");
            for (int i = 0; i < table.Length; i++)
            {
                Assert.That(east.AreaPoints[i].X, Is.EqualTo(table[i].X + 4));
                Assert.That(east.AreaPoints[i].Y, Is.EqualTo(table[i].Y + 2));
                Assert.That(east.AreaPoints[i].T, Is.EqualTo(table[i].T));
            }

            CombatIntent shifted = Attack(planId: 2, ownerId: 1, sequence: 2, facing: GridDirection.East, anchor: G(8, 4));
            for (int i = 0; i < east.AreaPoints.Count; i++)
            {
                Assert.That(shifted.AreaPoints[i].X, Is.EqualTo(east.AreaPoints[i].X + 4));
                Assert.That(shifted.AreaPoints[i].Y, Is.EqualTo(east.AreaPoints[i].Y + 2));
            }

            // 规范排序：点集升序。
            for (int i = 1; i < east.AreaPoints.Count; i++)
            {
                Assert.That(east.AreaPoints[i - 1].CompareTo(east.AreaPoints[i]), Is.LessThan(0));
            }

            // 重复点必须去重：Pattern 写了 3 个点（其中 2 个相同）⇒ 区域只剩 2 个点。
            var duplicatedDirections = new List<DirectionalTriangleSet>(GridDirectionInfo.DirectionCount);
            for (int d = 0; d < GridDirectionInfo.DirectionCount; d++)
            {
                var facing = (GridDirection)d;
                duplicatedDirections.Add(new DirectionalTriangleSet(facing, new[]
                {
                    new TrianglePoint(1, 0, 1),
                    new TrianglePoint(1, 0, 1),
                    new TrianglePoint(-1, 0, 1)
                }));
            }
            var duplicatedPattern = new AttackPatternSpec(new AttackPatternId("attack.pattern.test.dup"), duplicatedDirections);
            CombatIntent deduped = Attack(
                planId: 10, ownerId: 1, sequence: 10, facing: GridDirection.North, pattern: duplicatedPattern);
            Assert.That(deduped.AreaPoints.Count, Is.EqualTo(2), "重复点必须去重");
            Assert.That(deduped.AreaPoints[0].X, Is.EqualTo(-1));
            Assert.That(deduped.AreaPoints[1].X, Is.EqualTo(1));
        }

        /// <summary>
        /// 会让它失败的实现缺陷：借用旧 <c>DirectionalGeometry.Rotate60CounterClockwise</c>、
        /// Unity 数学或运行时旋转矩阵，把"朝向 f"实现成"朝向 0 的表旋转 f 步"。
        /// 这里的 Pattern 故意不是旋转族，因此任何旋转实现都会给出不同的区域。
        /// </summary>
        [Test]
        public void ArbitrationHasNoLegacyPatternRotationDependency()
        {
            AttackPatternSpec pattern = BuildNonRotationalPattern();

            for (int f = 0; f < GridDirectionInfo.DirectionCount; f++)
            {
                var facing = (GridDirection)f;
                CombatIntent intent = Attack(
                    planId: 200 + f, ownerId: 1, sequence: 200 + f, facing: facing, pattern: pattern);

                Assert.That(intent.AreaPoints.Count, Is.EqualTo(1));
                Assert.That(intent.AreaPoints[0].X, Is.EqualTo(100 * f + 0), "必须逐字取表，不得旋转");
                Assert.That(intent.AreaPoints[0].Y, Is.EqualTo(1));
            }

            foreach (Type type in typeof(CombatIntent).Assembly.GetTypes()
                         .Where(t => t.Namespace == "ProjectHero.Logic.Interactions"))
            {
                foreach (MethodInfo method in type.GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                             BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    Assert.That(method.Name, Does.Not.Contain("Rotate"),
                        type.FullName + "." + method.Name + " 不得引入运行时旋转");
                }
            }
        }

        // ================================================================= 冲突图契约：节点资格

        /// <summary>
        /// 会让它失败的实现缺陷：节点资格读取"计划处于 Recovery"（例如用
        /// <c>tick &gt; ImpactTick</c>、<c>EndTick - RecoveryTicks</c>、动作相位或单位状态判断）
        /// 而把 ImpactTick 上的合法 Intent 过滤掉；也包括用半开区间 <c>[StartTick, EndTick)</c>
        /// 判定有效区间，从而在 <c>RecoveryTicks == 0</c> 时把 ImpactTick 判成"区间外"。
        /// </summary>
        [Test]
        public void AttackImpactTickEntersRecoveryAndStillArbitrates()
        {
            // ImpactTick = 10，EndTick = 14 ⇒ 本 Tick 已处于 Recovery 段 [10, 14)。
            InteractionPlanFacts facts = AttackFacts(planId: 1, ownerId: 1, impactTick: 10L, startTick: 5L, endTick: 14L);
            Assert.That(facts.EndTick, Is.GreaterThan(facts.ImpactTick), "夹具前提：ImpactTick 之后仍有 Recovery 段");

            CombatIntent attack = Attack(planId: 1, ownerId: 1, sequence: 1);
            UnitOccupancy[] units = { UnitAt(3, AiController, attack, 0) };

            ConflictGraph graph = BuildGraph(new[] { attack }, new[] { facts }, units, units);

            Assert.That(graph.Nodes.Count, Is.EqualTo(1), "Recovery 状态不得过滤本 Tick 合法 Intent");
            Assert.That(graph.Contacts.Count, Is.EqualTo(1));
            Assert.That(graph.Contacts[0].Type, Is.EqualTo(ContactType.AttackTarget));
            Assert.That(graph.Contacts[0].TargetUnitId, Is.EqualTo(U(3)));

            // RecoveryTicks == 0（EndTick == ImpactTick）时同样不得丢节点。
            InteractionPlanFacts zeroRecovery = AttackFacts(planId: 2, ownerId: 1, impactTick: 10L, startTick: 8L, endTick: 10L);
            CombatIntent second = Attack(planId: 2, ownerId: 1, sequence: 2);
            UnitOccupancy[] units2 = { UnitAt(3, AiController, second, 0) };
            ConflictGraph graph2 = BuildGraph(new[] { second }, new[] { zeroRecovery }, units2, units2);
            Assert.That(graph2.Nodes.Count, Is.EqualTo(1), "RecoveryTicks == 0 不得把 ImpactTick 判成区间外");
        }

        /// <summary>
        /// 会让它失败的实现缺陷：用"计划当前是否终态"之外的启发式（例如是否还有后续计划、
        /// 是否仍在 Lane 上）决定接触，或对终态计划仍然物化 Intent；也会被
        /// "已终态计划仍入图"的写法打红。
        /// </summary>
        [Test]
        public void PreImpactTerminationProducesNoAttackContact()
        {
            CombatIntent live = Attack(planId: 1, ownerId: 1, sequence: 1);
            CombatIntent terminated = Attack(planId: 2, ownerId: 2, sequence: 2);

            // 计划 2 在 Impact 之前已终态 ⇒ 不物化 Intent（调用方遵守的唯一性契约）。
            var payload = new AttackPayloadSpec(
                new[] { new DamageComponentSpec(DamageChannels.PhysicalSlash, 12f, DamageTagMask.Blockable) },
                ImpactProfiles.Blunt, 1f, TargetPolicy.AllTargetsInArea, TargetRelationMask.Hostile,
                0, GridPattern, AttackTagMask.Reactable);
            InteractionPlanFacts terminatedFacts = AttackFacts(planId: 2, ownerId: 2, terminal: true);
            var ex = Assert.Throws<LogicDefinitionException>(() => CombatIntentFactory.CreateAtImpactTick(
                terminatedFacts, AttackSpecId, GridDirection.North, payload, G(0, 0), Tick,
                InteractionPriorities.AttackVsAttack, 2L, MomentumPacket.Require(GridDirection.North, 100, ImpactProfiles.Blunt), 0, null));
            Assert.That(ex.ErrorCode, Is.EqualTo(InteractionCodes.INTENT_PLAN_TERMINAL));

            // 即使有人硬把终态计划的 Intent 塞进输入，节点资格也必须把它挡在图外。
            UnitOccupancy[] units = { UnitAt(3, AiController, live, 0), UnitAt(4, AiController, terminated, 0) };
            ConflictGraph graph = BuildGraph(
                new[] { live, terminated },
                new[] { AttackFacts(1, 1), AttackFacts(2, 2, terminal: true) },
                units,
                units);

            Assert.That(graph.Nodes.Count, Is.EqualTo(1));
            Assert.That(graph.NodeIndexOf(P(2)), Is.EqualTo(-1), "终态计划不得留下任何攻击接触");
            Assert.That(graph.Contacts.Any(c => c.AttackerPlanId == P(2)), Is.False);
            Assert.That(graph.Groups.Count, Is.EqualTo(1));
        }

        /// <summary>
        /// 会让它失败的实现缺陷：求解/终态提交后重建或回写本 Tick 输入（例如
        /// "终止计划时顺手从本 Tick Intent/接触集合里删掉它"），或图持有计划的可变引用。
        /// </summary>
        [Test]
        public void ImpactTickClashTerminatesAfterIntentParticipates()
        {
            // 两个攻击互相覆盖对方所有者 ⇒ Attack↔Attack 边 ⇒ 同一冲突组。
            CombatIntent first = Attack(planId: 1, ownerId: 1, sequence: 1);
            CombatIntent second = Attack(planId: 2, ownerId: 2, sequence: 2);

            UnitOccupancy[] units =
            {
                UnitAt(1, PlayerController, first, 0),
                UnitAt(2, AiController, second, 0)
            };

            ConflictGraph before = BuildGraph(new[] { first, second }, new[] { AttackFacts(1, 1), AttackFacts(2, 2) }, units, units);
            Assert.That(before.Groups.Count, Is.EqualTo(1));
            Assert.That(before.FindContact(Key(ContactType.AttackAttack, 1, 1, 2, 2, 0)), Is.Not.Null,
                "夹具前提：两个攻击的区域相交");

            // Clash 在求解后终止双方计划（这里只提交终态事实，不重建本 Tick 输入）。
            var plansAfterClash = new[] { AttackFacts(1, 1, terminal: true), AttackFacts(2, 2, terminal: true) };

            Assert.That(before.Nodes.Count, Is.EqualTo(2), "已冻结输入不得被追溯删除");
            Assert.That(before.FindContact(Key(ContactType.AttackAttack, 1, 1, 2, 2, 0)), Is.Not.Null);
            Assert.That(before.Groups[0].NodeCount, Is.EqualTo(2));
            Assert.That(plansAfterClash[0].IsTerminal, Is.True, "夹具前提：求解后计划进入终态");

            // 终态只影响"未来 Tick 是否还产生 Intent"，不影响本 Tick 已经冻结的图。
            ConflictGraph rebuiltForNextTick = BuildGraph(
                Array.Empty<CombatIntent>(),
                plansAfterClash,
                Array.Empty<UnitOccupancy>(),
                Array.Empty<UnitOccupancy>(),
                tick: Tick + 1);
            Assert.That(rebuiltForNextTick.Nodes.Count, Is.EqualTo(0));
        }

        /// <summary>
        /// 会让它失败的实现缺陷：<see cref="ConflictGraph"/> 直接引用调用方的
        /// <c>List</c>/数组或计划对象，于是提交阶段的清理（终态、从集合中移除）
        /// 会追溯改写本 Tick 已经冻结的节点与接触。
        /// </summary>
        [Test]
        public void TerminalResolutionDoesNotRetroactivelyRemoveFrozenCurrentTickIntent()
        {
            CombatIntent attack = Attack(planId: 1, ownerId: 1, sequence: 1);
            var intents = new List<CombatIntent> { attack };
            var plans = new List<InteractionPlanFacts> { AttackFacts(1, 1) };
            var units = new List<UnitOccupancy> { UnitAt(3, AiController, attack, 0) };

            ConflictGraph graph = BuildGraph(intents, plans, units, units);
            string frozenText = graph.CanonicalText;
            int frozenContacts = graph.Contacts.Count;

            // 提交阶段的清理：调用方清空自己的集合、把计划标成终态。
            intents.Clear();
            plans.Clear();
            plans.Add(AttackFacts(1, 1, terminal: true));
            units.Clear();

            Assert.That(graph.Nodes.Count, Is.EqualTo(1));
            Assert.That(graph.Contacts.Count, Is.EqualTo(frozenContacts));
            Assert.That(graph.CanonicalText, Is.EqualTo(frozenText));
            Assert.That(graph.Groups[0].NodeCount, Is.EqualTo(1));
        }

        // ================================================================= 冲突组、上限与规范化

        /// <summary>
        /// 会让它失败的实现缺陷：把上限实现成"丢掉超出的边/节点"、"拆成多个组"、
        /// "截断目标集合后继续求解"，或返回非稳定原因码。
        /// 另外：<c>MaxConflictGroupEdges == 256 * 255 / 2</c> 恰好是 256 节点完全图的边数，
        /// 因此在节点上限生效时该边上限不可独立触发 —— 本用例顺带固定这一事实。
        /// </summary>
        [Test]
        public void ConflictGroupLimitsFailWithStableReasonCodes()
        {
            Assert.That(ConflictGraphLimits.MaxConflictGroupEdges,
                Is.EqualTo(ConflictGraphLimits.MaxConflictGroupNodes * (ConflictGraphLimits.MaxConflictGroupNodes - 1) / 2));

            // --- 场景 A：节点数超限。用"链式相交"把 257 个节点连成一个组（只需 256 条边）。---
            // 每个节点的区域是 {anchor, anchor+2}：只有相邻两个节点共享一个三角形，
            // 因此连通分量恰好是一条 257 节点的链，而不是完全图。
            AttackPatternSpec chainPattern = BuildChainPattern();
            var intents = new List<CombatIntent>();
            var plans = new List<InteractionPlanFacts>();
            for (int i = 0; i < ConflictGraphLimits.MaxConflictGroupNodes + 1; i++)
            {
                intents.Add(Attack(
                    planId: 1000 + i, ownerId: 1, sequence: 1000 + i,
                    anchor: G(2 * i, 0), pattern: chainPattern));
                plans.Add(AttackFacts(1000 + i, 1));
            }

            ConflictGraphBuildResult nodeLimit = ConflictGraphBuilder.TryBuild(
                new ConflictGraphInput(Tick, intents, plans, Array.Empty<UnitOccupancy>(), Array.Empty<UnitOccupancy>(), BuildResolver()),
                out ConflictGraph nodeGraph);

            Assert.That(nodeLimit.Succeeded, Is.False);
            Assert.That(nodeLimit.ErrorCode, Is.EqualTo(InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED));
            Assert.That(nodeLimit.FailingGroupKey, Is.EqualTo(1000L), "失败必须报出最小 IntentSequence 组键");
            Assert.That(nodeGraph, Is.Null, "整组失败不得产出被截断的图");

            // --- 场景 B：目标数超限。2 个节点、260 个不同目标仍在同一组。---
            var wide = new List<DirectionalTriangleSet>(GridDirectionInfo.DirectionCount);
            var triangles = new List<TrianglePoint>();
            for (int i = 0; i < ConflictGraphLimits.MaxTargetsPerConflictGroup + 4; i++)
            {
                triangles.Add(new TrianglePoint(2 * i, 1, 1));
            }
            for (int f = 0; f < GridDirectionInfo.DirectionCount; f++)
            {
                wide.Add(new DirectionalTriangleSet((GridDirection)f, triangles));
            }
            var widePattern = new AttackPatternSpec(new AttackPatternId("attack.pattern.test.wide"), wide);

            CombatIntent wideA = Attack(planId: 2001, ownerId: 1, sequence: 2001, pattern: widePattern);
            CombatIntent wideB = Attack(planId: 2002, ownerId: 2, sequence: 2002, pattern: widePattern);

            var wideUnits = new List<UnitOccupancy>();
            for (int i = 0; i < triangles.Count; i++)
            {
                // 单位 1..N 各占一个三角形：其中含攻击者自身（Self 未被掩码允许，故不产生接触）。
                wideUnits.Add(Unit(100 + i, AiController, triangles[i]));
            }

            ConflictGraphBuildResult targetLimit = ConflictGraphBuilder.TryBuild(
                new ConflictGraphInput(
                    Tick,
                    new[] { wideA, wideB },
                    new[] { AttackFacts(2001, 1), AttackFacts(2002, 2) },
                    wideUnits,
                    wideUnits,
                    BuildResolverWithExtraUnits(triangles.Count)),
                out ConflictGraph targetGraph);

            Assert.That(targetLimit.Succeeded, Is.False, "目标数超过 " + ConflictGraphLimits.MaxTargetsPerConflictGroup + " 必须整组失败");
            Assert.That(targetLimit.ErrorCode, Is.EqualTo(InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED));
            Assert.That(targetLimit.FailingGroupKey, Is.EqualTo(2001L));
            Assert.That(targetGraph, Is.Null);
        }

        /// <summary>
        /// 只用于节点上限场景：每个朝向 2 个相对三角形 <c>(0,1,1)</c> 与 <c>(2,1,1)</c>。
        /// 把锚点按 <c>2 * i</c> 平移后，节点 i 与 i+1 恰好共享一个三角形 ⇒ 分量是一条链。
        /// </summary>
        private static AttackPatternSpec BuildChainPattern()
        {
            var directions = new List<DirectionalTriangleSet>(GridDirectionInfo.DirectionCount);
            for (int f = 0; f < GridDirectionInfo.DirectionCount; f++)
            {
                directions.Add(new DirectionalTriangleSet(
                    (GridDirection)f,
                    new[] { new TrianglePoint(0, 1, 1), new TrianglePoint(2, 1, 1) }));
            }
            return new AttackPatternSpec(new AttackPatternId("attack.pattern.test.chain"), directions);
        }

        private static FactionRelationResolver BuildResolverWithExtraUnits(int targetCount)
        {
            var model = new FactionModelDefinition(
                new[]
                {
                    new FactionDefinition(FactionIds.Hero),
                    new FactionDefinition(FactionIds.Monster)
                },
                new[] { new FactionRelationDefinition(FactionIds.Hero, FactionIds.Monster, FactionDisposition.Hostile) });

            var map = new Dictionary<UnitId, FactionId>
            {
                { U(1), FactionIds.Hero },
                { U(2), FactionIds.Monster }
            };
            for (int i = 0; i < targetCount; i++)
            {
                map[U(100 + i)] = FactionIds.Monster;
            }
            return new FactionRelationResolver(model, map);
        }

        /// <summary>
        /// 会让它失败的实现缺陷：用 <c>HashSet</c>/<c>Dictionary</c> 的枚举顺序建立邻接表或分组，
        /// 用 BFS/DFS 的访问顺序决定组键，或把节点按"插入顺序"编号。
        /// —— 本用例真的构造多种输入排列并逐字比较输出。
        /// </summary>
        [Test]
        public void ConflictGroupsAreIndependentOfNodeAndEdgeInsertionOrder()
        {
            // A、B 互相覆盖（Clash），C、D 共享目标单位 3（共享目标边）。
            CombatIntent a = Attack(planId: 1, ownerId: 1, sequence: 1, anchor: G(0, 0));
            CombatIntent b = Attack(planId: 2, ownerId: 2, sequence: 2, anchor: G(0, 0));
            CombatIntent c = Attack(planId: 3, ownerId: 1, sequence: 3, anchor: G(40, 0));
            CombatIntent d = Attack(planId: 4, ownerId: 2, sequence: 4, anchor: G(40, 0));

            var plans = new[]
            {
                AttackFacts(1, 1), AttackFacts(2, 2), AttackFacts(3, 1), AttackFacts(4, 2)
            };

            var units = new List<UnitOccupancy>
            {
                UnitAt(1, PlayerController, a, 0),
                UnitAt(2, AiController, b, 0),
                UnitAt(3, AiController2, c, 0),   // C 与 D 的共同目标
                UnitAt(4, PlayerController, d, 0)
            };

            var orders = new List<CombatIntent[]>
            {
                new[] { a, b, c, d },
                new[] { d, c, b, a },
                new[] { c, a, d, b },
                new[] { b, d, a, c }
            };

            ConflictGraph reference = null;
            foreach (CombatIntent[] permutation in orders)
            {
                // 同时打乱空间表与计划表的输入顺序（此处按逆序，覆盖"表顺序 ≠ 节点顺序"）。
                var unitsShuffled = units.AsEnumerable().Reverse().ToList();
                var plansShuffled = plans.AsEnumerable().Reverse().ToList();

                ConflictGraph graph = BuildGraph(permutation, plansShuffled, unitsShuffled, unitsShuffled);
                if (reference == null)
                {
                    reference = graph;
                    continue;
                }

                Assert.That(graph.CanonicalText, Is.EqualTo(reference.CanonicalText),
                    "输入排列不得改变节点、边、组划分或任何输出");
                Assert.That(graph.Groups.Count, Is.EqualTo(reference.Groups.Count));
                for (int g = 0; g < graph.Groups.Count; g++)
                {
                    Assert.That(graph.Groups[g].GroupKey, Is.EqualTo(reference.Groups[g].GroupKey));
                    Assert.That(graph.Groups[g].NodeIndices.ToArray(), Is.EqualTo(reference.Groups[g].NodeIndices.ToArray()));
                    Assert.That(graph.Groups[g].ContactKeys.ToArray(), Is.EqualTo(reference.Groups[g].ContactKeys.ToArray()));
                    Assert.That(graph.Groups[g].TargetUnitIds.ToArray(), Is.EqualTo(reference.Groups[g].TargetUnitIds.ToArray()));
                    Assert.That(graph.Groups[g].EdgeCount, Is.EqualTo(reference.Groups[g].EdgeCount));
                }
            }

            Assert.That(reference, Is.Not.Null);
            Assert.That(reference.Groups.Count, Is.EqualTo(2), "互相覆盖的一对与共享目标的一对必须分成两组");
        }

        /// <summary>
        /// 会让它失败的实现缺陷：把"组键"实现成遍历起点、首个发现节点、组内最大序号
        /// 或任何与顺序有关的值。
        /// </summary>
        [Test]
        public void ConflictGroupKeyIsMinimumIntentSequence()
        {
            CombatIntent high = Attack(planId: 1, ownerId: 1, sequence: 9, anchor: G(0, 0));
            CombatIntent low = Attack(planId: 2, ownerId: 2, sequence: 3, anchor: G(0, 0));
            CombatIntent middle = Attack(planId: 3, ownerId: 1, sequence: 7, anchor: G(0, 0));

            UnitOccupancy[] units =
            {
                UnitAt(1, PlayerController, high, 0),
                UnitAt(2, AiController, low, 0),
                UnitAt(3, AiController2, middle, 0)
            };

            ConflictGraph graph = BuildGraph(
                new[] { high, low, middle },
                new[] { AttackFacts(1, 1), AttackFacts(2, 2), AttackFacts(3, 1) },
                units,
                units);

            Assert.That(graph.Groups.Count, Is.EqualTo(1));
            Assert.That(graph.Groups[0].GroupKey, Is.EqualTo(3L), "组键必须是组内最小 IntentSequence");
            Assert.That(graph.GroupKeyOfNode(graph.NodeIndexOf(P(1))), Is.EqualTo(3L));
        }

        /// <summary>
        /// 会让它失败的实现缺陷：只按空间相交连边而漏掉"共享目标"这条规则
        /// （两个攻击命中同一目标但彼此区域不相交时被拆成两组）。
        /// </summary>
        [Test]
        public void SharedTargetAttacksEnterOneConflictGroup()
        {
            // 两个攻击的区域相隔很远（4 格间距，平移 2 后仍不相交），但都覆盖单位 3。
            CombatIntent left = Attack(planId: 1, ownerId: 1, sequence: 1, anchor: G(0, 0));
            CombatIntent right = Attack(planId: 2, ownerId: 2, sequence: 2, anchor: G(-40, 0));

            Assert.That(left.AreaIntersects(right), Is.False, "夹具前提：两个攻击区域不相交（不构成 Clash）");

            var target = new UnitOccupancy(U(3), new[] { left.AreaPoints[0], right.AreaPoints[0] }, AiController);
            UnitOccupancy[] units =
            {
                new UnitOccupancy(U(1), new[] { left.AreaPoints[1] }, PlayerController),
                new UnitOccupancy(U(2), new[] { right.AreaPoints[1] }, AiController2),
                target
            };

            ConflictGraph graph = BuildGraph(
                new[] { left, right },
                new[] { AttackFacts(1, 1), AttackFacts(2, 2) },
                units,
                units);

            Assert.That(graph.Groups.Count, Is.EqualTo(1), "共享目标必须在同一求解组");
            Assert.That(graph.Groups[0].NodeCount, Is.EqualTo(2));
            Assert.That(graph.Groups[0].GroupKey, Is.EqualTo(1L));
            Assert.That(graph.Groups[0].TargetUnitIds.ToArray(), Is.EqualTo(new[] { U(3) }));
            Assert.That(graph.FindContact(Key(ContactType.SharedTarget, 1, 1, 2, 2, 3)), Is.Not.Null);
        }

        /// <summary>
        /// 会让它失败的实现缺陷：接触集合不排序（按发现顺序输出）、按"旧格先/新格先"去重、
        /// 或把同一条接触生成两次（旧格 + 新格各一条）。
        /// </summary>
        [Test]
        public void TypedContactsAreCanonicallyOrderedAndDeduplicatedByContactKey()
        {
            CombatIntent attack = Attack(
                planId: 1, ownerId: 1, sequence: 1, mask: TargetRelationMask.Hostile | TargetRelationMask.Allied);
            CombatIntent clash = Attack(planId: 2, ownerId: 2, sequence: 2);

            UnitOccupancy[] units =
            {
                UnitAt(1, PlayerController, attack, 0),
                UnitAt(2, AiController, clash, 0),
                UnitAt(3, AiController2, attack, 0)
            };

            var plans = new[]
            {
                AttackFacts(1, 1),
                AttackFacts(2, 2),
                BlockFacts(9, 3, Tick)
            };

            ConflictGraph graph = BuildGraph(new[] { attack, clash }, plans, units, units);

            for (int i = 1; i < graph.Contacts.Count; i++)
            {
                Assert.That(graph.Contacts[i - 1].Key.CompareTo(graph.Contacts[i].Key), Is.LessThan(0),
                    "接触必须严格按 ContactKey 升序且键唯一");
            }

            // 每个 (攻击, 目标单位) 至多一条"带类型"接触（不因旧格/新格两次查询而重复）。
            Assert.That(graph.Contacts.Count(c => c.Type == ContactType.AttackBlock
                                                  && c.AttackerPlanId == P(1)
                                                  && c.TargetUnitId == U(3)),
                Is.EqualTo(1), "Block 接触不得因旧格/新格两次查询而重复");

            ContactKey blockKey = Key(ContactType.AttackBlock, 1, 1, 3, 9, 3);
            InteractionContact block = graph.FindContact(blockKey);
            Assert.That(block, Is.Not.Null);
            Assert.That(block.CoveredBefore && block.CoveredAfter, Is.True, "两次查询都成立的接触只保留一条，覆盖标记取并集");
        }

        // ================================================================= 关系、Controller 与窗口

        /// <summary>
        /// 会让它失败的实现缺陷：用 Controller 相同/不同推断敌我（例如"不同 Controller 才连边"
        /// 或"同 Controller 直接跳过"），或让 Controller 参与稳定键/分组。
        /// </summary>
        [Test]
        public void ControllerChangesDoNotChangeAttackContactCandidates()
        {
            ConflictGraphBuildResult First(ControllerId first, ControllerId second)
            {
                CombatIntent a = Attack(planId: 1, ownerId: 1, sequence: 1, anchor: G(0, 0));
                CombatIntent b = Attack(planId: 2, ownerId: 2, sequence: 2, anchor: G(0, 0));
                UnitOccupancy[] units =
                {
                    UnitAt(1, first, a, 0),
                    UnitAt(2, second, b, 0),
                    UnitAt(3, first, a, 0)
                };
                return ConflictGraphBuilder.TryBuild(
                    new ConflictGraphInput(
                        Tick, new[] { a, b },
                        new[] { AttackFacts(1, 1), AttackFacts(2, 2) },
                        units, units, BuildResolver()),
                    out ConflictGraph graph);
            }

            ConflictGraphBuildResult same = First(PlayerController, PlayerController);
            ConflictGraphBuildResult different = First(PlayerController, AiController);

            Assert.That(same.Succeeded && different.Succeeded, Is.True);
            Assert.That(different.Graph.CanonicalText, Is.EqualTo(same.Graph.CanonicalText),
                "只改 Controller 不得改变任何接触候选");

            // 没有任何空间/目标关系的两个攻击：即使 Controller 不同也不产生边。
            CombatIntent far1 = Attack(planId: 11, ownerId: 1, sequence: 11, anchor: G(0, 0));
            CombatIntent far2 = Attack(planId: 12, ownerId: 2, sequence: 12, anchor: G(400, 0));
            UnitOccupancy[] farUnits =
            {
                UnitAt(1, PlayerController, far1, 0),
                UnitAt(2, AiController, far2, 0)
            };
            ConflictGraph far = BuildGraph(
                new[] { far1, far2 },
                new[] { AttackFacts(11, 1), AttackFacts(12, 2) },
                farUnits,
                farUnits);

            Assert.That(far.Groups.Count, Is.EqualTo(2), "Controller 不同本身不产生边");
        }

        /// <summary>
        /// 会让它失败的实现缺陷：把 Attack↔Attack 的连边实现成"敌我判定"
        /// （只连 Hostile、看到 Allied 就跳过），或从 Controller 种类推断拼刀资格。
        /// </summary>
        [Test]
        public void AttackAttackClashDoesNotInferEligibilityFromControllerKind()
        {
            var combinations = new[]
            {
                new { Name = "same-controller", First = PlayerController, Second = PlayerController },
                new { Name = "different-controller", First = PlayerController, Second = AiController }
            };

            foreach (var combination in combinations)
            {
                // 单位 1、2 同阵营（Allied），区域完全重合 ⇒ 必须连边。
                CombatIntent ally1 = Attack(planId: 1, ownerId: 1, sequence: 1, anchor: G(0, 0), mask: TargetRelationMask.Hostile);
                CombatIntent ally2 = Attack(planId: 2, ownerId: 2, sequence: 2, anchor: G(0, 0), mask: TargetRelationMask.Hostile);
                UnitOccupancy[] alliedUnits =
                {
                    UnitAt(1, combination.First, ally1, 0),
                    UnitAt(2, combination.Second, ally2, 0)
                };

                ConflictGraph allied = BuildGraph(
                    new[] { ally1, ally2 },
                    new[] { AttackFacts(1, 1), AttackFacts(2, 2) },
                    alliedUnits,
                    alliedUnits);

                Assert.That(allied.FindContact(Key(ContactType.AttackAttack, 1, 1, 2, 2, 0)), Is.Not.Null,
                    combination.Name + "：Allied 不得自动跳过 Attack↔Attack 接触");
                Assert.That(allied.Groups.Count, Is.EqualTo(1));

                // 敌对阵营（1 = hero，3 = monster）同样连边：连边不看阵营与 Controller。
                CombatIntent hostile1 = Attack(planId: 1, ownerId: 1, sequence: 1, anchor: G(0, 0), mask: TargetRelationMask.Hostile);
                CombatIntent hostile2 = Attack(planId: 2, ownerId: 3, sequence: 2, anchor: G(0, 0), mask: TargetRelationMask.Hostile);
                UnitOccupancy[] hostileUnits =
                {
                    UnitAt(1, combination.First, hostile1, 0),
                    UnitAt(3, combination.Second, hostile2, 0)
                };

                ConflictGraph hostile = BuildGraph(
                    new[] { hostile1, hostile2 },
                    new[] { AttackFacts(1, 1), AttackFacts(2, 3) },
                    hostileUnits,
                    hostileUnits);

                Assert.That(hostile.FindContact(Key(ContactType.AttackAttack, 1, 1, 3, 2, 0)), Is.Not.Null,
                    combination.Name + "：Hostile 的 Attack↔Attack 接触同样按空间连边");
                Assert.That(hostile.Groups.Count, Is.EqualTo(1));
            }
        }

        /// <summary>
        /// 会让它失败的实现缺陷：把当前窗口/提交窗口引入到期过滤
        /// （例如只保留 <c>SubmittedWindowId == 当前窗口</c> 的 Intent），
        /// 或把窗口加入交互优先级/稳定键。
        /// </summary>
        [Test]
        public void WindowAPlanHitsDuringWindowC()
        {
            CombatIntent fromWindowA = Attack(planId: 1, ownerId: 1, sequence: 5, window: W(1), priority: InteractionPriorities.AttackVsAttack);
            CombatIntent fromWindowC = Attack(planId: 2, ownerId: 2, sequence: 2, window: W(3), priority: InteractionPriorities.AttackVsAttack);

            FrozenIntentQueue queue = GlobalIntentQueue.Freeze(Tick, new[] { fromWindowA, fromWindowC });

            Assert.That(queue.Count, Is.EqualTo(2), "过去窗口提交的动作必须照常进入本 Tick 仲裁");
            Assert.That(queue.Intents[0].ActionPlanId, Is.EqualTo(P(2)), "排序只看 (Tick, 优先级, IntentSequence)");
            Assert.That(queue.Intents[1].ActionPlanId, Is.EqualTo(P(1)));

            // 只改窗口来源不得改变队列顺序或图输出。
            CombatIntent windowSwapped = Attack(planId: 1, ownerId: 1, sequence: 5, window: W(9));
            FrozenIntentQueue swapped = GlobalIntentQueue.Freeze(Tick, new[] { windowSwapped, fromWindowC });
            Assert.That(swapped.CanonicalText, Is.EqualTo(queue.CanonicalText));
        }

        /// <summary>
        /// 会让它失败的实现缺陷：把防御接触限制在"与来源攻击同窗口提交"的计划上，
        /// 从而漏掉过去窗口提交的攻击与当前窗口反应的交互。
        /// </summary>
        [Test]
        public void PastWindowAttackInteractsWithCurrentWindowDefense()
        {
            CombatIntent pastWindow = Attack(planId: 1, ownerId: 1, sequence: 1, window: W(1));
            UnitOccupancy[] units = { UnitAt(3, AiController, pastWindow, 0) };

            var plans = new[] { AttackFacts(1, 1), BlockFacts(9, 3, Tick) };
            ConflictGraph graph = BuildGraph(new[] { pastWindow }, plans, units, units);

            Assert.That(graph.Contacts.Count, Is.EqualTo(1));
            Assert.That(graph.Contacts[0].Type, Is.EqualTo(ContactType.AttackBlock));
            Assert.That(graph.Contacts[0].CounterpartyPlanId, Is.EqualTo(P(9)));

            // 同一攻击 + 同 Tick 的 Dodge 也是接触；TriggerTick 不同的 Block 则不是。
            var dodgePlans = new[] { AttackFacts(1, 1), DodgeFacts(8, 3, Tick), BlockFacts(7, 3, Tick + 1) };
            ConflictGraph withDodge = BuildGraph(new[] { pastWindow }, dodgePlans, units, units);
            Assert.That(withDodge.FindContact(Key(ContactType.AttackDodge, 1, 1, 3, 8, 3)), Is.Not.Null);
            Assert.That(withDodge.FindContact(Key(ContactType.AttackBlock, 1, 1, 3, 7, 3)), Is.Null,
                "TriggerTick 不同的 Block 不得形成本 Tick 接触");
        }

        /// <summary>
        /// 会让它失败的实现缺陷：AOE 分支里另写一套敌我判断（例如默认包含友军、
        /// 或对 Self 特判跳过），使掩码不再唯一决定目标资格。
        /// </summary>
        [Test]
        public void HostileOnlyAreaAttackExcludesSelfAlliedAndNeutralUnits()
        {
            CombatIntent attack = Attack(
                planId: 1, ownerId: 1, sequence: 1, mask: TargetRelationMask.Hostile);

            UnitOccupancy[] units =
            {
                UnitAt(1, PlayerController, attack, 0),   // Self
                UnitAt(2, PlayerController, attack, 1),   // Allied（同为 hero）
                UnitAt(3, AiController, attack, 2),       // Hostile（monster）
                UnitAt(5, AiController2, attack, 3)       // Neutral（beast）
            };

            ConflictGraph graph = BuildGraph(new[] { attack }, new[] { AttackFacts(1, 1) }, units, units);

            Assert.That(graph.Contacts.Count, Is.EqualTo(1));
            Assert.That(graph.Contacts[0].Type, Is.EqualTo(ContactType.AttackTarget));
            Assert.That(graph.Contacts[0].TargetUnitId, Is.EqualTo(U(3)));
            Assert.That(graph.Groups[0].TargetUnitIds.ToArray(), Is.EqualTo(new[] { U(3) }));
        }

        /// <summary>
        /// 会让它失败的实现缺陷：把 Allied 当成"永远不打"的硬编码（例如区域攻击自动跳过友军），
        /// 或把 Neutral 一起纳入显式友伤掩码。
        /// </summary>
        [Test]
        public void ExplicitFriendlyFireMaskIncludesAlliedButNotNeutralTargets()
        {
            CombatIntent attack = Attack(
                planId: 1, ownerId: 1, sequence: 1,
                mask: TargetRelationMask.Hostile | TargetRelationMask.Allied);

            UnitOccupancy[] units =
            {
                UnitAt(1, PlayerController, attack, 0),
                UnitAt(2, PlayerController, attack, 1),
                UnitAt(3, AiController, attack, 2),
                UnitAt(5, AiController2, attack, 3)
            };

            ConflictGraph graph = BuildGraph(new[] { attack }, new[] { AttackFacts(1, 1) }, units, units);

            Assert.That(graph.Groups[0].TargetUnitIds.ToArray(), Is.EqualTo(new[] { U(2), U(3) }),
                "显式友伤只能包含掩码允许的 Allied/Hostile，不得含 Self 与 Neutral");
        }

        /// <summary>
        /// 会让它失败的实现缺陷：把 Neutral 当成"默认敌人"或"默认忽略"，
        /// 而不是由显式 Neutral 掩码位决定。
        /// </summary>
        [Test]
        public void NeutralTargetRequiresExplicitNeutralRelationBit()
        {
            CombatIntent withoutNeutral = Attack(
                planId: 1, ownerId: 1, sequence: 1, mask: TargetRelationMask.Hostile);
            CombatIntent withNeutral = Attack(
                planId: 2, ownerId: 1, sequence: 2, mask: TargetRelationMask.Hostile | TargetRelationMask.Neutral);

            UnitOccupancy[] units =
            {
                UnitAt(3, AiController, withoutNeutral, 0),
                UnitAt(5, AiController2, withoutNeutral, 1)
            };

            ConflictGraph strict = BuildGraph(new[] { withoutNeutral }, new[] { AttackFacts(1, 1) }, units, units);
            Assert.That(strict.Groups[0].TargetUnitIds.ToArray(), Is.EqualTo(new[] { U(3) }));

            UnitOccupancy[] units2 =
            {
                UnitAt(3, AiController, withNeutral, 0),
                UnitAt(5, AiController2, withNeutral, 1)
            };
            ConflictGraph permissive = BuildGraph(new[] { withNeutral }, new[] { AttackFacts(2, 1) }, units2, units2);
            Assert.That(permissive.Groups[0].TargetUnitIds.ToArray(), Is.EqualTo(new[] { U(3), U(5) }));
        }

        /// <summary>
        /// 会让它失败的实现缺陷：PrimaryTargetOnly 走一条"跳过关系掩码"的快捷路径
        /// （固定目标直接命中），或 AOE 分支另用一套关系判定。
        /// </summary>
        [Test]
        public void PrimaryAndAreaPoliciesUseSameFactionRelationResolver()
        {
            // PrimaryTargetOnly + 固定 Neutra l目标：掩码不含 Neutral ⇒ 不得产生任何接触。
            CombatIntent primaryNeutral = Attack(
                planId: 1, ownerId: 1, sequence: 1, mask: TargetRelationMask.Hostile,
                policy: TargetPolicy.PrimaryTargetOnly, primary: 5);

            UnitOccupancy[] units =
            {
                UnitAt(3, AiController, primaryNeutral, 0),
                UnitAt(5, AiController2, primaryNeutral, 1)
            };

            ConflictGraph blocked = BuildGraph(new[] { primaryNeutral }, new[] { AttackFacts(1, 1) }, units, units);
            Assert.That(blocked.Contacts.Count, Is.EqualTo(0),
                "PrimaryTargetOnly 也必须通过同一关系掩码");
            Assert.That(blocked.Nodes.Count, Is.EqualTo(1), "节点资格与接触资格是两件事");

            // 同一掩码下 AllTargetsInArea 只命中 Hostile 的 3。
            CombatIntent area = Attack(planId: 2, ownerId: 1, sequence: 2, mask: TargetRelationMask.Hostile);
            UnitOccupancy[] units2 =
            {
                UnitAt(3, AiController, area, 0),
                UnitAt(5, AiController2, area, 1)
            };
            ConflictGraph areaGraph = BuildGraph(new[] { area }, new[] { AttackFacts(2, 1) }, units2, units2);
            Assert.That(areaGraph.Groups[0].TargetUnitIds.ToArray(), Is.EqualTo(new[] { U(3) }));

            // PrimaryTargetOnly 固定 Hostile 目标 ⇒ 即便区域也覆盖 Neutral，也只命中固定目标。
            CombatIntent primaryHostile = Attack(
                planId: 3, ownerId: 1, sequence: 3, mask: TargetRelationMask.Hostile,
                policy: TargetPolicy.PrimaryTargetOnly, primary: 3);
            UnitOccupancy[] units3 =
            {
                UnitAt(3, AiController, primaryHostile, 0),
                UnitAt(5, AiController2, primaryHostile, 1)
            };
            ConflictGraph fixedGraph = BuildGraph(new[] { primaryHostile }, new[] { AttackFacts(3, 1) }, units3, units3);
            Assert.That(fixedGraph.Groups[0].TargetUnitIds.ToArray(), Is.EqualTo(new[] { U(3) }));
        }

        // ================================================================= 旧格/新格接触集合（构图部分）

        /// <summary>
        /// 会让它失败的实现缺陷：把"提交前查询"与"提交后查询"各生成一条接触
        /// （于是同一攻击对同一目标结算两次），或按"旧格优先/新格优先"丢弃其中一次查询的事实。
        /// </summary>
        [Test]
        public void OldAndNewContactForSameAttackAndTargetResolvesOnce()
        {
            CombatIntent attack = Attack(planId: 1, ownerId: 1, sequence: 1);
            var plans = new[] { AttackFacts(1, 1), DodgeFacts(8, 3, Tick) };

            var before = new[] { UnitAt(3, AiController, attack, 0) };
            var after = new[] { UnitAt(3, AiController, attack, 0) };   // 换格后仍在区域内

            ConflictGraph graph = BuildGraph(new[] { attack }, plans, before, after);

            ContactKey key = Key(ContactType.AttackDodge, 1, 1, 3, 8, 3);
            InteractionContact contact = graph.FindContact(key);
            Assert.That(contact, Is.Not.Null, "旧格与新格都成立时必须仍只有一条接触");
            Assert.That(contact.CoveredBefore, Is.True);
            Assert.That(contact.CoveredAfter, Is.True);
            Assert.That(graph.Contacts.Count(c => c.Type == ContactType.AttackDodge), Is.EqualTo(1));
        }

        /// <summary>
        /// 会让它失败的实现缺陷：只用"提交前"或只用"提交后"一次查询
        /// （于是闪进攻击范围的 Dodge 被漏掉，或旧格接触被当成新格接触）。
        /// </summary>
        [Test]
        public void DodgeIntoAnotherAttackAreaCreatesNewContact()
        {
            // attackOld 只在旧格命中；attackNew 只在新格命中。
            CombatIntent attackOld = Attack(planId: 1, ownerId: 1, sequence: 1, anchor: G(0, 0));
            CombatIntent attackNew = Attack(planId: 2, ownerId: 1, sequence: 2, anchor: G(-40, 0));

            var plans = new[] { AttackFacts(1, 1), AttackFacts(2, 1), DodgeFacts(8, 3, Tick) };

            var before = new[] { new UnitOccupancy(U(3), new[] { attackOld.AreaPoints[0] }, AiController) };
            var after = new[] { new UnitOccupancy(U(3), new[] { attackNew.AreaPoints[0] }, AiController) };

            ConflictGraph graph = BuildGraph(new[] { attackOld, attackNew }, plans, before, after);

            InteractionContact escaped = graph.FindContact(Key(ContactType.AttackDodge, 1, 1, 3, 8, 3));
            Assert.That(escaped, Is.Not.Null, "旧格成立、新格失效的接触仍必须以原始空间事实进入求解器");
            Assert.That(escaped.CoveredBefore, Is.True);
            Assert.That(escaped.CoveredAfter, Is.False);

            InteractionContact landed = graph.FindContact(Key(ContactType.AttackDodge, 1, 2, 3, 8, 3));
            Assert.That(landed, Is.Not.Null, "闪进新攻击范围必须产生新接触");
            Assert.That(landed.CoveredBefore, Is.False);
            Assert.That(landed.CoveredAfter, Is.True);
        }

        /// <summary>
        /// 会让它失败的实现缺陷：给 Undodgeable 攻击实现"追踪目标"（例如因为
        /// <c>Undodgeable</c> 就无条件生成接触，或按 Dodge 目的格反查攻击区域）。
        /// Undodgeable 只忽略本次 Dodge 造成的空间失效，不凭空追踪两次查询都不成立的目标。
        /// </summary>
        [Test]
        public void UndodgeableDoesNotTrackTargetsAbsentFromBothContactSets()
        {
            // 去掉 Dodgeable 位 ⇒ 该攻击是 Undodgeable。
            CombatIntent undodgeable = Attack(
                planId: 1, ownerId: 1, sequence: 1,
                tags: AttackTagMask.Reactable | AttackTagMask.Blockable);

            var plans = new[] { AttackFacts(1, 1), DodgeFacts(8, 3, Tick) };
            var outside = new TrianglePoint(1000, 1, 1);
            var before = new[] { new UnitOccupancy(U(3), new[] { outside }, AiController) };
            var after = new[] { new UnitOccupancy(U(3), new[] { outside }, AiController) };

            ConflictGraph graph = BuildGraph(new[] { undodgeable }, plans, before, after);

            Assert.That(graph.Contacts.Count, Is.EqualTo(0),
                "两次查询都不成立时不得凭空生成追踪接触");
            Assert.That(graph.Nodes.Count, Is.EqualTo(1), "攻击本身仍是本 Tick 有效节点");
        }

        // ================================================================= 排列不变性（补充命名）

        /// <summary>
        /// 会让它失败的实现缺陷：任何以"输入顺序"为隐藏输入的实现
        /// —— 邻接表按发现顺序、节点下标按插入顺序、接触序列化带发现序号、
        /// 用 <c>Dictionary</c> 枚举顺序决定分组。
        /// 本用例真的构造 4! = 24 种排列并逐字比较规范化文本。
        /// </summary>
        [Test]
        public void ConflictGraphSerializationIsInvariantUnderAllIntentPermutations()
        {
            CombatIntent a = Attack(planId: 1, ownerId: 1, sequence: 1, anchor: G(0, 0));
            CombatIntent b = Attack(planId: 2, ownerId: 2, sequence: 2, anchor: G(0, 0));
            CombatIntent c = Attack(planId: 3, ownerId: 3, sequence: 3, anchor: G(0, 0));

            var plans = new[] { AttackFacts(1, 1), AttackFacts(2, 2), AttackFacts(3, 3) };
            UnitOccupancy[] units =
            {
                UnitAt(1, PlayerController, a, 0),
                UnitAt(2, AiController, b, 0),
                UnitAt(3, AiController2, c, 0)
            };

            var permutations = Permutations(new[] { a, b, c });
            string reference = null;
            foreach (CombatIntent[] permutation in permutations)
            {
                ConflictGraph graph = BuildGraph(permutation, plans, units, units);
                if (reference == null)
                {
                    reference = graph.CanonicalText;
                    Assert.That(graph.Groups.Count, Is.EqualTo(1));
                    Assert.That(graph.Groups[0].GroupKey, Is.EqualTo(1L));
                    Assert.That(graph.Nodes.Count, Is.EqualTo(3));
                    continue;
                }
                Assert.That(graph.CanonicalText, Is.EqualTo(reference),
                    "全部 3! 种排列必须得到逐字相同的冲突图序列化");
            }

            Assert.That(reference, Is.Not.Null);
            Assert.That(permutations.Count, Is.EqualTo(6));
        }

        private static List<CombatIntent[]> Permutations(CombatIntent[] items)
        {
            var result = new List<CombatIntent[]>();
            void Recurse(List<CombatIntent> remaining, List<CombatIntent> prefix)
            {
                if (remaining.Count == 0)
                {
                    result.Add(prefix.ToArray());
                    return;
                }
                for (int i = 0; i < remaining.Count; i++)
                {
                    CombatIntent picked = remaining[i];
                    var nextRemaining = new List<CombatIntent>(remaining);
                    nextRemaining.RemoveAt(i);
                    var nextPrefix = new List<CombatIntent>(prefix) { picked };
                    Recurse(nextRemaining, nextPrefix);
                }
            }
            Recurse(items.ToList(), new List<CombatIntent>());
            return result;
        }
    }
}
