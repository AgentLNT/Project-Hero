using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 08 快照契约（<c>Logic/Snapshots/**</c>）的仓库内自动化证据。
    ///
    /// <para>
    /// <strong>为什么必须有这个文件</strong>：任务包 <c>08:389</c> 要求
    /// 「相同冲突组的 Intent、接触边和目标集合<strong>任意排列</strong>，
    /// Resolution、事件序列和<strong>快照哈希完全一致</strong>」。
    /// 该断言的对象是<strong>规范化摘要</strong>，而摘要只有在
    /// (<c>IntentSnapshot</c> 完整载荷 + <c>ConflictGroupSnapshot</c> + <c>ContactSnapshot</c>)
    /// 都真的写进 <see cref="LogicSnapshot.ComputeHash()"/> 之后才可能成立；
    /// 任何"只断言不抛异常"或"只比较集合数量"的写法都无法发现
    /// 「集合没进哈希」「排序键不唯一」「用了容器枚举顺序」这三类缺陷。
    /// 因此本文件<strong>真的构造两种以上排列</strong>并逐字比较摘要，
    /// 再逐字段验证每个新增分量的哈希参与性。
    /// </para>
    ///
    /// <para>
    /// <strong>会让排列不变性用例变红的实现缺陷</strong>（逐条对应，均非同义反复）：
    /// <list type="bullet">
    /// <item><c>LogicSnapshot.ComputeHash</c> 沿用旧的
    /// <c>(IntentSequence, SourceUnitId, TargetUnitId)</c> 三字段写入 ⇒ 载荷变化不改变摘要，
    /// 本文件的"逐字段参与性"用例红；</item>
    /// <item><c>ComputeHash</c> 不写新集合（组划分/接触）⇒ 用例中"去掉组划分/去掉接触集合后摘要必须不同"
    /// 两条红；</item>
    /// <item>用 <c>Dictionary</c>/<c>HashSet</c> 枚举顺序、或 <c>List.Sort</c> 的非全序比较器
    /// （例如只按 <c>AttackerNodeIndex</c> 排序接触）⇒ 不同输入排列得到不同摘要，排列用例红；</item>
    /// <item>把 <c>BuildSnapshot</c> 的投影写成"取当前网格/计划索引重新推导"⇒ 投影与冻结队列分叉，
    /// 集成用例（<c>Task08IntentAndConflictGraphIntegrationTests</c>）红。</item>
    /// </list>
    /// </para>
    /// </summary>
    public class Task08SnapshotContractTests
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

        /// <summary>每个朝向 4 个相对三角形，沿 X 轴间隔 4（与 <c>Task08ConflictGraphTests</c> 同形）。</summary>
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
            return new AttackPatternSpec(new AttackPatternId("attack.pattern.test.snapshot"), directions);
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
            int momentumUnits = 100,
            int momentumOffsetSteps = 0,
            IReadOnlyList<DamageComponentSpec> damageComponents = null,
            AttackTagMask tags = AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable,
            AttackPatternSpec pattern = null)
        {
            return CombatIntentFactory.Create(new AttackIntentRequest(
                P(planId),
                AttackSpecId,
                U(ownerId),
                policy,
                primary.HasValue ? U(primary.Value) : (UnitId?)null,
                mask,
                tags,
                facing,
                pattern ?? GridPattern,
                anchor ?? G(0, 0),
                impactTick,
                priority,
                sequence,
                damageComponents ?? new[]
                {
                    new DamageComponentSpec(DamageChannels.PhysicalSlash, 12f, DamageTagMask.Blockable)
                },
                MomentumPacket.Require(facing, momentumUnits, ImpactProfiles.Blunt),
                momentumOffsetSteps,
                window));
        }

        private static InteractionPlanFacts AttackFacts(long planId, long ownerId, long impactTick = Tick)
            => new InteractionPlanFacts(
                P(planId), U(ownerId), ActionType.Attack, 5L, 14L, impactTick, 0L, 0L, 0L, null, false);

        private static UnitOccupancy UnitAt(long unitId, ControllerId controller, CombatIntent intent, int pointIndex)
            => new UnitOccupancy(U(unitId), new[] { intent.AreaPoints[pointIndex] }, controller);

        /// <summary>把 Intent 列表（任意排列）走完"冻结队列 → 冲突图"这条真实链路。</summary>
        private static ConflictGraph BuildGraph(
            IReadOnlyList<CombatIntent> intents,
            IReadOnlyList<InteractionPlanFacts> plans,
            IReadOnlyList<UnitOccupancy> units)
            => ConflictGraphBuilder.Build(new ConflictGraphInput(
                Tick, GlobalIntentQueue.Freeze(Tick, intents).Intents, plans, units, units, BuildResolver()));

        // ================================================================= 摘要抓手

        /// <summary>
        /// 用真实构造路径装配快照并取摘要：投影（生产用同一份实现）+ <see cref="LogicSnapshot"/> 哈希。
        /// 末尾两个集合刻意用命名参数传入，避免位置参数写错位。
        /// </summary>
        private static ulong SnapshotHash(
            IReadOnlyList<IntentSnapshot> intents,
            IReadOnlyList<ConflictGroupSnapshot> conflictGroups,
            IReadOnlyList<ContactSnapshot> contacts)
            => new LogicSnapshot(
                tick: Tick,
                rulesVersion: "rules.snapshot-contract",
                battleDefinitionHash: "definition.snapshot-contract",
                encounterId: "encounter.snapshot-contract",
                battleEnd: BattleEndSnapshot.Active(),
                units: Array.Empty<UnitSnapshot>(),
                effects: Array.Empty<StatusEffectSnapshot>(),
                windowManager: TurnWindowManagerSnapshot.None(),
                concurrentAction: ConcurrentActionSnapshot.None(),
                resources: BattleResourceSnapshot.None(0),
                scheduleRevision: 0L,
                plans: Array.Empty<ActionPlanSnapshot>(),
                reactionOpportunities: Array.Empty<ReactionOpportunitySnapshot>(),
                actorLanes: Array.Empty<ActorLaneSnapshot>(),
                intents: intents,
                movementSegments: Array.Empty<MovementSegmentSnapshot>(),
                reservations: Array.Empty<ReservationSnapshot>(),
                aiControllers: Array.Empty<AiControllerSnapshot>(),
                commandIngresses: null,
                rng: new RngSnapshot(DeterministicRng.AlgorithmVersion, 1UL),
                nextUnitId: 6L,
                nextActionPlanId: 11L,
                nextReactionOpportunityId: 1L,
                nextWindowId: 1L,
                nextEffectId: 1L,
                nextCommandSequence: 1L,
                nextIntentSequence: 12L,
                nextResolutionSequence: 1L,
                nextEventSequence: 1L,
                nextEffectSequence: 1L,
                history: HistorySummary.Empty(),
                commandSourcePriorityMappingVersion: CommandSourcePriority.MappingVersion,
                conflictGroups: conflictGroups,
                contacts: contacts).ComputeHash();

        private static string Hex(ulong digest) => digest.ToString("x16", CultureInfo.InvariantCulture);

        /// <summary>诊断文本（红的时候能直接看出是哪一段集合分叉）。</summary>
        private static string Describe(
            IReadOnlyList<IntentSnapshot> intents,
            IReadOnlyList<ConflictGroupSnapshot> conflictGroups,
            IReadOnlyList<ContactSnapshot> contacts)
        {
            var sb = new StringBuilder();
            sb.Append("intents=").Append(intents.Count);
            for (int i = 0; i < intents.Count; i++)
            {
                IntentSnapshot intent = intents[i];
                sb.Append("\n|i:").Append(intent.IntentSequence).Append(':').Append(intent.ActionPlanId)
                  .Append(':').Append(intent.OwnerUnitId).Append(':').Append(intent.ImpactTick)
                  .Append(':').Append(intent.InteractionPriority).Append(':').Append(intent.Facing)
                  .Append(':').Append(intent.Momentum == null ? "-" : intent.Momentum.Direction.ToString(CultureInfo.InvariantCulture))
                  .Append(':').Append(intent.Momentum == null ? "-" : intent.Momentum.Units.ToString(CultureInfo.InvariantCulture))
                  .Append(":area=").Append(intent.AreaPoints == null ? 0 : intent.AreaPoints.Count)
                  .Append(":dmg=").Append(intent.DamageComponents == null ? 0 : intent.DamageComponents.Count);
            }
            sb.Append("\ngroups=").Append(conflictGroups.Count);
            for (int g = 0; g < conflictGroups.Count; g++)
            {
                ConflictGroupSnapshot group = conflictGroups[g];
                sb.Append("\n|g:").Append(group.GroupKey).Append(":edges=").Append(group.EdgeCount).Append(":nodes=");
                for (int n = 0; n < group.NodeIntentSequences.Count; n++)
                {
                    if (n > 0) sb.Append(',');
                    sb.Append(group.NodeIntentSequences[n].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(":targets=");
                for (int t = 0; t < group.TargetUnitIds.Count; t++)
                {
                    if (t > 0) sb.Append(',');
                    sb.Append(group.TargetUnitIds[t].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(":contacts=").Append(group.ContactKeys.Count);
            }
            sb.Append("\ncontacts=").Append(contacts.Count);
            for (int c = 0; c < contacts.Count; c++)
            {
                ContactSnapshot contact = contacts[c];
                sb.Append("\n|c:").Append(contact.Type).Append(':').Append(contact.FirstUnitId)
                  .Append(':').Append(contact.FirstPlanId).Append(':').Append(contact.SecondUnitId)
                  .Append(':').Append(contact.SecondPlanId).Append(':').Append(contact.TargetUnitId);
            }
            return sb.ToString();
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

        // ================================================================= 1. 排列不变性（任务包 08:389）

        /// <summary>
        /// <strong>同一冲突组的任意输入排列 ⇒ 快照哈希逐字一致</strong>（任务包 <c>08:389</c> 的快照面）。
        ///
        /// 真的构造 3! = 6 种 Intent 排列，并同时打乱计划表与空间占用表的顺序；
        /// 每个排列都走"<see cref="GlobalIntentQueue.Freeze"/> → <see cref="ConflictGraphBuilder"/> →
        /// <see cref="InteractionSnapshotProjection"/> → <see cref="LogicSnapshot"/> 哈希"这条真实链路。
        ///
        /// <strong>非真空前提</strong>（否则"排列不影响摘要"会退化成空集合的平凡真）：
        /// 一个冲突组、组内 3 个节点、至少 3 条 Intent↔Intent 边、接触集合非空、Intent 载荷非零。
        /// </summary>
        [Test]
        public void SameConflictGroupInAnyInputPermutationProducesByteIdenticalSnapshotHash()
        {
            CombatIntent a = Attack(planId: 1, ownerId: 1, sequence: 1);
            CombatIntent b = Attack(planId: 2, ownerId: 2, sequence: 2);
            CombatIntent c = Attack(planId: 3, ownerId: 3, sequence: 3);

            var plans = new[] { AttackFacts(1, 1), AttackFacts(2, 2), AttackFacts(3, 3) };
            var units = new[]
            {
                UnitAt(1, PlayerController, a, 0),
                UnitAt(2, AiController, b, 0),
                UnitAt(3, AiController2, c, 0)
            };

            List<CombatIntent[]> permutations = Permutations(new[] { a, b, c });
            Assert.That(permutations.Count, Is.EqualTo(6), "必须真的构造 3! = 6 种排列，而不是两种特例");

            string referenceHash = null;
            string referenceText = null;
            for (int p = 0; p < permutations.Count; p++)
            {
                IReadOnlyList<InteractionPlanFacts> plansIn = p % 2 == 0 ? plans : plans.Reverse().ToArray();
                IReadOnlyList<UnitOccupancy> unitsIn = p % 3 == 0 ? units : units.Reverse().ToArray();

                ConflictGraph graph = BuildGraph(permutations[p], plansIn, unitsIn);
                IReadOnlyList<IntentSnapshot> intents = InteractionSnapshotProjection.Intents(
                    GlobalIntentQueue.Freeze(Tick, permutations[p]));
                IReadOnlyList<ConflictGroupSnapshot> groups = InteractionSnapshotProjection.ConflictGroups(graph);
                IReadOnlyList<ContactSnapshot> contacts = InteractionSnapshotProjection.Contacts(graph);

                string text = Describe(intents, groups, contacts);
                string hash = Hex(SnapshotHash(intents, groups, contacts));

                if (referenceHash == null)
                {
                    // 非真空证据：结构真的非平凡。
                    Assert.That(intents.Count, Is.EqualTo(3), "三个攻击 Intent 必须全部进快照");
                    Assert.That(groups.Count, Is.EqualTo(1), "三个互相覆盖的攻击必须落在同一个冲突组");
                    Assert.That(groups[0].NodeIntentSequences.Count, Is.EqualTo(3));
                    Assert.That(groups[0].EdgeCount, Is.GreaterThanOrEqualTo(3),
                        "互相覆盖 ⇒ 至少 3 条 Intent↔Intent 边；edges=" + groups[0].EdgeCount);
                    Assert.That(groups[0].ContactKeys.Count, Is.GreaterThanOrEqualTo(3));
                    Assert.That(contacts.Count, Is.GreaterThanOrEqualTo(3));
                    Assert.That(groups[0].NodeIntentSequences[0], Is.EqualTo(1L), "组键 = 组内最小 IntentSequence");
                    Assert.That(intents[0].Momentum.Units, Is.GreaterThan(0));
                    Assert.That(intents[0].AreaPoints.Count, Is.GreaterThan(0));
                    Assert.That(intents[0].DamageComponents.Count, Is.GreaterThan(0));

                    referenceHash = hash;
                    referenceText = text;
                    continue;
                }

                Assert.That(text, Is.EqualTo(referenceText),
                    "排列 " + p + " 改变了快照载荷的规范文本（排序键不是输入多重集合的纯函数）；\n"
                    + text + "\n--- reference ---\n" + referenceText);
                Assert.That(hash, Is.EqualTo(referenceHash),
                    "排列 " + p + " 改变了快照哈希（任务包 08:389 要求逐字一致）；\n"
                    + text + "\n--- reference ---\n" + referenceText);
            }
        }

        /// <summary>
        /// 两个冲突组（4 个 Intent：互相覆盖的一对 + 共享目标的一对）的全部 4! = 24 种排列，
        /// 快照哈希同样必须逐字一致 —— 这条额外覆盖"<strong>组之间的顺序</strong>"也必须是稳定键。
        ///
        /// 会让它失败的实现缺陷：组按遍历/发现顺序（并查集根下标）排序而不是按组键排序；
        /// 组内接触用"锚点节点下标"排序（下标随输入排列变化）。
        /// </summary>
        [Test]
        public void TwoConflictGroupsInAnyInputPermutationProduceByteIdenticalSnapshotHash()
        {
            CombatIntent a = Attack(planId: 1, ownerId: 1, sequence: 1, anchor: G(0, 0));
            CombatIntent b = Attack(planId: 2, ownerId: 2, sequence: 2, anchor: G(0, 0));
            CombatIntent c = Attack(planId: 3, ownerId: 1, sequence: 3, anchor: G(40, 0));
            CombatIntent d = Attack(planId: 4, ownerId: 2, sequence: 4, anchor: G(40, 0));

            var plans = new[]
            {
                AttackFacts(1, 1), AttackFacts(2, 2), AttackFacts(3, 1), AttackFacts(4, 2)
            };

            // 单位 1/2 各自落在自己攻击的首个三角上；单位 3 同时落在 c 与 d 的区域里（共享目标边）。
            var units = new[]
            {
                UnitAt(1, PlayerController, a, 0),
                UnitAt(2, AiController, b, 0),
                new UnitOccupancy(U(3), new[] { c.AreaPoints[0], d.AreaPoints[0] }, AiController2),
                UnitAt(4, PlayerController, d, 0)
            };

            List<CombatIntent[]> permutations = Permutations(new[] { a, b, c, d });
            Assert.That(permutations.Count, Is.EqualTo(24), "必须真的构造 4! = 24 种排列");

            string referenceHash = null;
            for (int p = 0; p < permutations.Count; p++)
            {
                IReadOnlyList<InteractionPlanFacts> plansIn = p % 2 == 0 ? plans : plans.Reverse().ToArray();
                IReadOnlyList<UnitOccupancy> unitsIn = p % 3 == 0 ? units : units.Reverse().ToArray();

                ConflictGraph graph = BuildGraph(permutations[p], plansIn, unitsIn);
                IReadOnlyList<IntentSnapshot> intents = InteractionSnapshotProjection.Intents(
                    GlobalIntentQueue.Freeze(Tick, permutations[p]));
                IReadOnlyList<ConflictGroupSnapshot> groups = InteractionSnapshotProjection.ConflictGroups(graph);
                IReadOnlyList<ContactSnapshot> contacts = InteractionSnapshotProjection.Contacts(graph);
                string hash = Hex(SnapshotHash(intents, groups, contacts));

                if (referenceHash == null)
                {
                    Assert.That(intents.Count, Is.EqualTo(4));
                    Assert.That(groups.Count, Is.EqualTo(2), "互相覆盖的一对与共享目标的一对必须分成两组");
                    Assert.That(groups[0].GroupKey, Is.EqualTo(1L));
                    Assert.That(groups[1].GroupKey, Is.EqualTo(3L));
                    Assert.That(groups[1].TargetUnitIds, Does.Contain(3L), "共享目标组必须带上目标单位 3");
                    referenceHash = hash;
                    continue;
                }

                Assert.That(hash, Is.EqualTo(referenceHash),
                    "排列 " + p + " 改变了快照哈希（组顺序/组内顺序必须只由稳定键决定）");
            }
        }

        // ================================================================= 2. 空状态合法（构造期 Tick 0）

        /// <summary>
        /// <strong>没有队列、没有图是合法状态</strong>：投影成空集合，且空集合仍然参与哈希。
        ///
        /// 会让它失败的实现缺陷：投影对 <c>null</c> 输入抛 <see cref="NullReferenceException"/>
        /// （<c>BattleSimulation</c> 构造期就会崩）；或"空集合不写哈希" ⇒
        /// 下面与"含一个 Intent"的摘要比较会相等。
        /// </summary>
        [Test]
        public void MissingQueueAndGraphProjectToEmptyCollectionsThatStillParticipateInHash()
        {
            Assert.That(InteractionSnapshotProjection.Intents((FrozenIntentQueue)null), Is.Empty,
                "构造期没有队列 ⇒ 空集合，不得抛");
            Assert.That(InteractionSnapshotProjection.Intents((IReadOnlyList<CombatIntent>)null), Is.Empty);
            Assert.That(InteractionSnapshotProjection.ConflictGroups(null), Is.Empty,
                "构造期没有图 ⇒ 空集合，不得抛");
            Assert.That(InteractionSnapshotProjection.Contacts(null), Is.Empty);

            ulong emptyHash = SnapshotHash(
                Array.Empty<IntentSnapshot>(),
                Array.Empty<ConflictGroupSnapshot>(),
                Array.Empty<ContactSnapshot>());
            ulong intentHash = SnapshotHash(
                InteractionSnapshotProjection.Intents(new[] { Attack(planId: 1, ownerId: 1, sequence: 1) }),
                Array.Empty<ConflictGroupSnapshot>(),
                Array.Empty<ContactSnapshot>());
            Assert.That(intentHash, Is.Not.EqualTo(emptyHash), "Intent 集合必须参与快照哈希");
        }

        // ================================================================= 3. Intent 载荷逐字段参与哈希

        /// <summary>
        /// <strong>Intent 快照的每一个冻结字段都必须进哈希</strong>，且投影必须真的搬运它们
        /// （不是恒零占位）。
        ///
        /// 会让它失败的实现缺陷：<c>ComputeHash</c> 少写某个字段（两条不同状态的 Intent 撞成同一摘要）；
        /// 投影漏搬运（例如把 <c>PrimaryTargetUnitId</c> 恒写 0、把 <c>RawAmount</c> 转成 Q10 整数、
        /// 把区域点集丢掉）。逐条断言都带字段名，红了能直接定位。
        /// </summary>
        [Test]
        public void IntentSnapshotCarriesEveryFrozenFieldAndEachOneParticipatesInHash()
        {
            var damage = new[]
            {
                new DamageComponentSpec(DamageChannels.PhysicalSlash, 12.5f, DamageTagMask.Blockable),
                new DamageComponentSpec(DamageChannels.PhysicalBlunt, 3.25f, DamageTagMask.Guardable)
            };

            CombatIntent rich = Attack(
                planId: 7, ownerId: 2, sequence: 5,
                facing: GridDirection.West,
                anchor: G(4, 2),
                impactTick: Tick,
                mask: TargetRelationMask.Hostile | TargetRelationMask.Neutral,
                policy: TargetPolicy.PrimaryTargetOnly,
                primary: 3L,
                window: W(9),
                priority: InteractionPriorities.AttackVsDodge,
                momentumUnits: 640,
                momentumOffsetSteps: -2,
                damageComponents: damage,
                tags: AttackTagMask.Reactable | AttackTagMask.Dodgeable);

            IntentSnapshot snapshot = InteractionSnapshotProjection.From(rich);

            // 1. 投影真的搬运了每个事实（含"无 ⇒ 0"两个哨兵）。
            Assert.That(snapshot.IntentSequence, Is.EqualTo(5L));
            Assert.That(snapshot.ActionPlanId, Is.EqualTo(7L));
            Assert.That(snapshot.OwnerUnitId, Is.EqualTo(2L));
            Assert.That(snapshot.ActionSpecId, Is.EqualTo(AttackSpecId.Value));
            Assert.That(snapshot.TargetPolicy, Is.EqualTo((int)TargetPolicy.PrimaryTargetOnly));
            Assert.That(snapshot.PrimaryTargetUnitId, Is.EqualTo(3L));
            Assert.That(snapshot.AllowedTargetRelations,
                Is.EqualTo((int)(TargetRelationMask.Hostile | TargetRelationMask.Neutral)));
            Assert.That(snapshot.Tags, Is.EqualTo((int)(AttackTagMask.Reactable | AttackTagMask.Dodgeable)));
            Assert.That(snapshot.Facing, Is.EqualTo((int)GridDirection.West));
            Assert.That(snapshot.ImpactTick, Is.EqualTo(Tick));
            Assert.That(snapshot.InteractionPriority, Is.EqualTo(InteractionPriorities.AttackVsDodge));
            Assert.That(snapshot.MomentumDirectionOffsetSteps, Is.EqualTo(-2));
            Assert.That(snapshot.SubmittedWindowId, Is.EqualTo(9L));
            Assert.That(snapshot.Momentum.Direction, Is.EqualTo((int)GridDirection.West));
            Assert.That(snapshot.Momentum.Units, Is.EqualTo(640));
            Assert.That(snapshot.Momentum.ImpactProfileId, Is.EqualTo(ImpactProfiles.Blunt.Value));
            Assert.That(snapshot.AreaPoints.Count, Is.EqualTo(rich.AreaPoints.Count));
            Assert.That(snapshot.AreaPoints.Count, Is.GreaterThan(1), "夹具前提：区域点集不止一个点");
            Assert.That(snapshot.DamageComponents.Count, Is.EqualTo(2));
            Assert.That(snapshot.DamageComponents[0].ChannelId, Is.EqualTo(DamageChannels.PhysicalSlash.Value));
            Assert.That(snapshot.DamageComponents[0].RawAmountBits,
                Is.EqualTo(BitConverter.SingleToInt32Bits(12.5f)), "float 只能按二进制位进快照");
            Assert.That(snapshot.DamageComponents[0].Tags, Is.EqualTo((int)DamageTagMask.Blockable));
            Assert.That(snapshot.DamageComponents[1].ChannelId, Is.EqualTo(DamageChannels.PhysicalBlunt.Value));
            Assert.That(snapshot.DamageComponents[1].RawAmountBits,
                Is.EqualTo(BitConverter.SingleToInt32Bits(3.25f)));

            ulong baseline = SnapshotHash(
                new[] { snapshot }, Array.Empty<ConflictGroupSnapshot>(), Array.Empty<ContactSnapshot>());

            var mutations = new List<KeyValuePair<string, IntentSnapshot>>
            {
                new KeyValuePair<string, IntentSnapshot>("IntentSequence", snapshot with { IntentSequence = 6L }),
                new KeyValuePair<string, IntentSnapshot>("ActionPlanId", snapshot with { ActionPlanId = 8L }),
                new KeyValuePair<string, IntentSnapshot>("OwnerUnitId", snapshot with { OwnerUnitId = 3L }),
                new KeyValuePair<string, IntentSnapshot>("ActionSpecId", snapshot with { ActionSpecId = "action.test.other" }),
                new KeyValuePair<string, IntentSnapshot>("TargetPolicy", snapshot with { TargetPolicy = (int)TargetPolicy.AllTargetsInArea }),
                new KeyValuePair<string, IntentSnapshot>("PrimaryTargetUnitId", snapshot with { PrimaryTargetUnitId = 0L }),
                new KeyValuePair<string, IntentSnapshot>("AllowedTargetRelations", snapshot with { AllowedTargetRelations = (int)TargetRelationMask.Hostile }),
                new KeyValuePair<string, IntentSnapshot>("Tags", snapshot with { Tags = (int)AttackTagMask.Reactable }),
                new KeyValuePair<string, IntentSnapshot>("Facing", snapshot with { Facing = (int)GridDirection.East }),
                new KeyValuePair<string, IntentSnapshot>("ImpactTick", snapshot with { ImpactTick = Tick + 1L }),
                new KeyValuePair<string, IntentSnapshot>("InteractionPriority", snapshot with { InteractionPriority = InteractionPriorities.RemainingHits }),
                new KeyValuePair<string, IntentSnapshot>("MomentumDirectionOffsetSteps", snapshot with { MomentumDirectionOffsetSteps = -1 }),
                new KeyValuePair<string, IntentSnapshot>("SubmittedWindowId", snapshot with { SubmittedWindowId = 0L }),
                new KeyValuePair<string, IntentSnapshot>("Momentum.Direction", snapshot with { Momentum = snapshot.Momentum with { Direction = (int)GridDirection.East } }),
                new KeyValuePair<string, IntentSnapshot>("Momentum.Units", snapshot with { Momentum = snapshot.Momentum with { Units = 641 } }),
                new KeyValuePair<string, IntentSnapshot>("Momentum.ImpactProfileId", snapshot with { Momentum = snapshot.Momentum with { ImpactProfileId = "impact.other" } }),
                new KeyValuePair<string, IntentSnapshot>("AreaPoints", snapshot with { AreaPoints = new[] { snapshot.AreaPoints[0] } }),
                new KeyValuePair<string, IntentSnapshot>("DamageComponents.Count", snapshot with { DamageComponents = new[] { snapshot.DamageComponents[0] } }),
                new KeyValuePair<string, IntentSnapshot>("DamageComponents.ChannelId", snapshot with
                {
                    DamageComponents = new[]
                    {
                        snapshot.DamageComponents[0] with { ChannelId = "damage.other" },
                        snapshot.DamageComponents[1]
                    }
                }),
                new KeyValuePair<string, IntentSnapshot>("DamageComponents.RawAmountBits", snapshot with
                {
                    DamageComponents = new[]
                    {
                        snapshot.DamageComponents[0] with { RawAmountBits = BitConverter.SingleToInt32Bits(12.25f) },
                        snapshot.DamageComponents[1]
                    }
                }),
                new KeyValuePair<string, IntentSnapshot>("DamageComponents.Tags", snapshot with
                {
                    DamageComponents = new[]
                    {
                        snapshot.DamageComponents[0] with { Tags = (int)DamageTagMask.Guardable },
                        snapshot.DamageComponents[1]
                    }
                })
            };

            for (int i = 0; i < mutations.Count; i++)
            {
                ulong mutated = SnapshotHash(
                    new[] { mutations[i].Value },
                    Array.Empty<ConflictGroupSnapshot>(),
                    Array.Empty<ContactSnapshot>());
                Assert.That(mutated, Is.Not.EqualTo(baseline),
                    "字段 " + mutations[i].Key + " 必须参与快照哈希（否则两条不同状态的 Intent 会同摘要）");
            }
        }

        // ================================================================= 4. 组划分与接触集合参与哈希

        /// <summary>
        /// <strong>冲突图的组划分与接触集合是冻结输入 ⇒ 必须进哈希</strong>；
        /// 而"同一批节点/接触被拆成两组"也必须改变摘要（只有真正的分区参与哈希才可能）。
        ///
        /// 会让它失败的实现缺陷：<c>ComputeHash</c> 不写 <c>ConflictGroups</c>/<c>Contacts</c>
        /// （"结构进、数值不进"里的<strong>结构</strong>没进）；只写组数量而不写组键/节点集合/
        /// 目标集合/边数；接触只写数量不写六个分量。
        /// </summary>
        [Test]
        public void ConflictGroupAndContactSnapshotsParticipateInHash()
        {
            CombatIntent a = Attack(planId: 1, ownerId: 1, sequence: 1);
            CombatIntent b = Attack(planId: 2, ownerId: 2, sequence: 2);
            CombatIntent c = Attack(planId: 3, ownerId: 3, sequence: 3);
            var plans = new[] { AttackFacts(1, 1), AttackFacts(2, 2), AttackFacts(3, 3) };
            var units = new[]
            {
                UnitAt(1, PlayerController, a, 0),
                UnitAt(2, AiController, b, 0),
                UnitAt(3, AiController2, c, 0)
            };

            ConflictGraph graph = BuildGraph(new[] { a, b, c }, plans, units);
            IReadOnlyList<IntentSnapshot> intents = InteractionSnapshotProjection.Intents(new[] { a, b, c });
            IReadOnlyList<ConflictGroupSnapshot> groups = InteractionSnapshotProjection.ConflictGroups(graph);
            IReadOnlyList<ContactSnapshot> contacts = InteractionSnapshotProjection.Contacts(graph);

            ulong baseline = SnapshotHash(intents, groups, contacts);
            Assert.That(groups.Count, Is.EqualTo(1), "夹具前提：一个冲突组");
            Assert.That(contacts.Count, Is.GreaterThan(0), "夹具前提：接触集合非空");

            // 1. 结构整块缺席 ⇒ 摘要必须不同（这两条同时证明"新集合真的进了哈希"）。
            Assert.That(SnapshotHash(intents, Array.Empty<ConflictGroupSnapshot>(), contacts),
                Is.Not.EqualTo(baseline), "组划分必须参与快照哈希");
            Assert.That(SnapshotHash(intents, groups, Array.Empty<ContactSnapshot>()),
                Is.Not.EqualTo(baseline), "接触集合必须参与快照哈希");

            // 2. 组划分的五个分量逐个改变 ⇒ 摘要都必须不同。
            ConflictGroupSnapshot group = groups[0];
            var groupMutations = new List<KeyValuePair<string, ConflictGroupSnapshot>>
            {
                new KeyValuePair<string, ConflictGroupSnapshot>("GroupKey", group with { GroupKey = group.GroupKey + 1 }),
                new KeyValuePair<string, ConflictGroupSnapshot>("EdgeCount", group with { EdgeCount = group.EdgeCount + 1 }),
                new KeyValuePair<string, ConflictGroupSnapshot>("NodeIntentSequences",
                    group with { NodeIntentSequences = new[] { group.NodeIntentSequences[0] } }),
                new KeyValuePair<string, ConflictGroupSnapshot>("TargetUnitIds",
                    group with { TargetUnitIds = Array.Empty<long>() }),
                new KeyValuePair<string, ConflictGroupSnapshot>("ContactKeys",
                    group with { ContactKeys = Array.Empty<ContactSnapshot>() })
            };
            for (int i = 0; i < groupMutations.Count; i++)
            {
                ulong mutated = SnapshotHash(intents, new[] { groupMutations[i].Value }, contacts);
                Assert.That(mutated, Is.Not.EqualTo(baseline),
                    "冲突组字段 " + groupMutations[i].Key + " 必须参与快照哈希");
            }

            // 3. 同一批节点与接触，但被拆成两个组 ⇒ 分区本身是哈希的一部分。
            var split = new[]
            {
                new ConflictGroupSnapshot(
                    group.NodeIntentSequences[0],
                    new[] { group.NodeIntentSequences[0] },
                    Array.Empty<ContactSnapshot>(),
                    group.TargetUnitIds,
                    0),
                new ConflictGroupSnapshot(
                    group.NodeIntentSequences[1],
                    new[] { group.NodeIntentSequences[1], group.NodeIntentSequences[2] },
                    group.ContactKeys,
                    group.TargetUnitIds,
                    group.EdgeCount)
            };
            Assert.That(SnapshotHash(intents, split, contacts), Is.Not.EqualTo(baseline),
                "同一批节点被拆成两组必须改变摘要（否则组划分并没有真的进哈希）");

            // 4. 接触键的六个分量逐个改变 ⇒ 摘要都必须不同。
            ContactSnapshot contact = contacts[0];
            var contactMutations = new List<KeyValuePair<string, ContactSnapshot>>
            {
                new KeyValuePair<string, ContactSnapshot>("Type", contact with { Type = contact.Type + 1 }),
                new KeyValuePair<string, ContactSnapshot>("FirstUnitId", contact with { FirstUnitId = contact.FirstUnitId + 1 }),
                new KeyValuePair<string, ContactSnapshot>("FirstPlanId", contact with { FirstPlanId = contact.FirstPlanId + 1 }),
                new KeyValuePair<string, ContactSnapshot>("SecondUnitId", contact with { SecondUnitId = contact.SecondUnitId + 1 }),
                new KeyValuePair<string, ContactSnapshot>("SecondPlanId", contact with { SecondPlanId = contact.SecondPlanId + 1 }),
                new KeyValuePair<string, ContactSnapshot>("TargetUnitId", contact with { TargetUnitId = contact.TargetUnitId + 1 })
            };
            for (int i = 0; i < contactMutations.Count; i++)
            {
                var mutatedContacts = new List<ContactSnapshot>(contacts);
                mutatedContacts[0] = contactMutations[i].Value;
                ulong mutated = SnapshotHash(intents, groups, mutatedContacts);
                Assert.That(mutated, Is.Not.EqualTo(baseline),
                    "接触键字段 " + contactMutations[i].Key + " 必须参与快照哈希");
            }

            // 5. 接触集合的顺序不得影响摘要（构造顺序与摘要无关）。
            var reversedContacts = new List<ContactSnapshot>(contacts);
            reversedContacts.Reverse();
            Assert.That(SnapshotHash(intents, groups, reversedContacts), Is.EqualTo(baseline),
                "接触集合的构造顺序不得改变规范化摘要");

            var reversedGroupContacts = new List<ContactSnapshot>(group.ContactKeys);
            reversedGroupContacts.Reverse();
            Assert.That(
                SnapshotHash(intents, new[] { group with { ContactKeys = reversedGroupContacts } }, contacts),
                Is.EqualTo(baseline),
                "组内接触键的构造顺序不得改变规范化摘要");
        }

        /// <summary>
        /// 接触快照的排序键必须与 <c>ContactKey</c> 的全序<strong>逐字段一致</strong>：
        /// 两者分叉会让"图里的接触顺序"与"快照里的接触顺序"不一致，
        /// 进而让排列不变性在两个消费者之间出现裂缝。
        /// </summary>
        [Test]
        public void ContactSnapshotOrderingMatchesContactKeyOrdering()
        {
            ContactType[] types =
            {
                ContactType.AttackDodge, ContactType.AttackBlock, ContactType.AttackAttack,
                ContactType.AttackMove, ContactType.AttackGuard, ContactType.AttackTarget,
                ContactType.SharedTarget
            };
            var keys = new List<ContactKey>();
            for (int t = 0; t < types.Length; t++)
            {
                keys.Add(ContactKey.Create(types[t], U(1), P(1), U(2), P(2), U(3)));
                keys.Add(ContactKey.Create(types[t], U(1), P(1), U(2), P(2), U(4)));
                keys.Add(ContactKey.Create(types[t], U(2), P(2), U(3), P(3), U(4)));
            }
            keys.Sort((x, y) => x.CompareTo(y));

            var snapshots = keys.Select(InteractionSnapshotProjection.From).ToList();
            var shuffled = new List<ContactSnapshot>(snapshots);
            shuffled.Reverse();
            shuffled.Sort();

            Assert.That(shuffled.Count, Is.EqualTo(snapshots.Count));
            for (int i = 0; i < snapshots.Count; i++)
            {
                Assert.That(shuffled[i].CompareTo(snapshots[i]), Is.EqualTo(0),
                    "ContactSnapshot 的规范顺序必须与 ContactKey 相同（下标 " + i + "）");
            }
        }
    }
}
