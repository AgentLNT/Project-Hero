using System;
using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 06 的<strong>装配级</strong>证据（全部在纯逻辑程序集内可执行）：
    ///
    /// <list type="bullet">
    /// <item>「必须产出」7：GridState / MovementSegment / Reservation 进入规范化快照与哈希，且规范排序与构造顺序无关；</item>
    /// <item>「必须产出」8/9：任意终态原因后不存在该计划的活动/未来段与 Reservation，战斗结束走同一个入口；</item>
    /// <item>任务 06 P4：<strong>单位 → 体积规范表</strong>的定义级绑定在生产装配路径上被真正消费（多格 footprint + 区域查询）；</item>
    /// <item>任务 06 P5：真实 <see cref="LogicGridMovementPathCalculator"/> 经<strong>真实排程事务</strong>被触发，
    /// 预测起点变化时路径权重/边数/绝对 Tick 与段一起重算。</item>
    /// </list>
    ///
    /// 边界（如实声明）：这些用例<strong>不</strong>构造 <c>BattleSimulation</c> 本体（那需要完整的
    /// Encounter/Controller/资产夹具）；它们消费的是与生产装配<strong>同一个</strong>类与同一份
    /// 投影实现，因此证明的是"这些类在真实驱动下行为正确"，而不是"某个场景里跑过"。
    /// </summary>
    public class Task06AssemblyIntegrationTests
    {
        [Test] public void ReorderingMoveChainRecomputesEveryCandidatePathFromTheNewPredecessor()
        {
            var h = NewHarness();
            var first = AddMoveOrFail(h, 1, 1, 4, 0, 100);
            var second = AddMoveOrFail(h, 2, 1, 8, 0, 300);
            var preview = h.Editor.Apply(new ScheduleEditOperation[] { new MoveEditablePlanOperation(second.ActionPlanId, 10) },
                1, h.Schedule.ScheduleRevision, h.Schedule.ScheduleRevision, preview: true);
            Assert.That(preview.Succeeded, Is.True, preview.RejectionCode);
            var firstPreview = new List<SchedulePlanPreview>(preview.PreviewPlans).Find(p => p.Plan.ActionPlanId == first.ActionPlanId.Value);
            Assert.That(firstPreview, Is.Not.Null, "The reordered successor must be part of the position dependency projection.");
            Assert.That(firstPreview.Path[0], Is.EqualTo(new GridPoint(8, 0)));
            Assert.That(firstPreview.Path[firstPreview.Path.Count - 1], Is.EqualTo(new GridPoint(4, 0)));
        }

        [Test] public void RemovingMovePredecessorRecomputesPathCostWithoutLeftCompactingItsSuccessor()
        {
            var h = NewHarness();
            var first = AddMoveOrFail(h, 1, 1, 4, 0, 100);
            var second = AddMoveOrFail(h, 2, 1, 8, 0, 300);
            long previousStart = second.StartTick;
            var preview = h.Editor.Apply(new ScheduleEditOperation[] { new RemoveEditablePlanOperation(first.ActionPlanId) },
                1, h.Schedule.ScheduleRevision, h.Schedule.ScheduleRevision, preview: true);
            Assert.That(preview.Succeeded, Is.True, preview.RejectionCode);
            var next = new List<SchedulePlanPreview>(preview.PreviewPlans).Find(p => p.Plan.ActionPlanId == second.ActionPlanId.Value);
            Assert.That(next, Is.Not.Null);
            Assert.That(next.Path[0], Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(next.Plan.ResolvedPathWeightUnits, Is.EqualTo(4));
            Assert.That(next.Plan.StartTick, Is.EqualTo(previousStart));
        }

        [Test] public void DetachedSchedulePreviewDoesNotChangePlansSpaceRevisionOrInvokeCommitHooks()
        {
            var h = NewHarness();
            h.Editor.MovementSpacePort = h.Movement;
            var plan = AddMoveOrFail(h, 1, 1, 4, 0, 10);
            var before = ActionPlanSnapshot.From(plan);
            var segments = MovementSnapshotProjection.Segments(h.Movement.AllSegmentsOrdered());
            var reservations = MovementSnapshotProjection.Reservations(h.Grid.AllReservationsOrdered());
            long revision = h.Schedule.ScheduleRevision;
            int committedHooks = 0;
            var hook = typeof(ActionPlan).GetProperty("ProjectionCommittedSink", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(hook, Is.Not.Null);
            hook.SetValue(plan, new Action<ActionPlan, long, long, long>((p, s, r, t) => committedHooks++));
            for (int i = 0; i < 5; i++)
            {
                var preview = h.Editor.Apply(new ScheduleEditOperation[] { new MoveEditablePlanOperation(plan.ActionPlanId, 100) },
                    1, revision, revision, preview: true);
                Assert.That(preview.Succeeded, Is.True, preview.RejectionCode);
                Assert.That(preview.PreviewPlans.Count, Is.EqualTo(1));
                Assert.That(preview.PreviewPlans[0].Plan.StartTick, Is.EqualTo(100));
            }
            Assert.That(ActionPlanSnapshot.From(plan), Is.EqualTo(before));
            Assert.That(h.Schedule.ScheduleRevision, Is.EqualTo(revision));
            Assert.That(MovementSnapshotProjection.Segments(h.Movement.AllSegmentsOrdered()), Is.EqualTo(segments));
            Assert.That(MovementSnapshotProjection.Reservations(h.Grid.AllReservationsOrdered()), Is.EqualTo(reservations));
            Assert.That(committedHooks, Is.Zero);
        }

        [Test] public void MovementSpaceBatchUsesEachUnitsOwnOriginWhenChainsAreInterleaved()
        {
            var h = NewHarness();
            var a = AddMoveOrFail(h, 1, 1, 2, 0, 10);
            var b = AddMoveOrFail(h, 2, 3, 2, -30, 10);
            var error = h.Movement.RebuildMovementSpace(new[] { b, a }, h.Schedule.ScheduleRevision, 0);
            Assert.That(error, Is.Null);
            Assert.That(h.Movement.SegmentsOfPlanOrdered(a.ActionPlanId)[0].From, Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(h.Movement.SegmentsOfPlanOrdered(b.ActionPlanId)[0].From, Is.EqualTo(new GridPoint(0, -30)));
            Assert.That(h.Movement.VerifyInvariants(), Is.Null);
        }

        private static readonly GridBoundaryDefinition Wide =
            new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40));

        private const string MoveSpecId = "action.move.test";
        private const string ActionSetId = "action_set.test";
        private const string VolumeSpecId = "unit_volume.test.radius_pair";
        private const string UnitA = "unit.test.a";
        private const string UnitB = "unit.test.b";

        private static UnitId U(long id) => new UnitId(id);

        private static ActionPlanId P(long id) => new ActionPlanId(id);

        private static IReadOnlyList<DirectionalTriangleSet> Directions()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        private static MovementPatternSpec MovementPattern()
            => new MovementPatternSpec(new MovementPatternId("movement_pattern.test"), Directions());

        private static BattleDefinition BuildDefinition()
        {
            var hero = new FactionId("faction.hero");
            var monster = new FactionId("faction.monster");
            var factionModel = new FactionModelDefinition(
                new List<FactionDefinition> { new FactionDefinition(hero), new FactionDefinition(monster) },
                new List<FactionRelationDefinition>
                {
                    new FactionRelationDefinition(hero, monster, FactionDisposition.Hostile)
                });

            var moveSpec = new ActionSpec(
                new ActionSpecId(MoveSpecId), ActionType.Move,
                new MoveTimingSpec(BaseStepTicks: 5, RecoveryTicks: 5),
                new MovePayloadSpec(200, MovementPattern()),
                AdrenalineCost: 0);

            var actionSet = new ActionSetDefinition(
                new ActionSetId(ActionSetId), new List<ActionSpecId> { moveSpec.ActionSpecId });

            var volume = new VolumeSpec(new VolumeSpecId(VolumeSpecId), Directions());

            var units = new List<UnitDefinition>
            {
                new UnitDefinition(new UnitDefinitionId(UnitA), 10f, 10f, 10f, 10f,
                    new Dictionary<DamageChannelId, int>(), 200f,
                    actionSet.ActionSetId, volume.VolumeSpecId),
                new UnitDefinition(new UnitDefinitionId(UnitB), 10f, 10f, 10f, 10f,
                    new Dictionary<DamageChannelId, int>(), 200f,
                    actionSet.ActionSetId, volume.VolumeSpecId)
            };

            return new BattleDefinition(
                "battle-def-v1",
                60,
                BattleRules.FrozenV1,
                ConcurrentActionDefinition.FrozenV1,
                new ReactionRules(2, 1),
                AdrenalineRules.FrozenV1,
                factionModel,
                new List<DamageChannelDefinition>(),
                new List<ImpactProfileDefinition>(),
                units,
                new List<ActionSpec> { moveSpec },
                new List<AttackPatternSpec>(),
                new List<VolumeSpec> { volume },
                new List<MovementPatternSpec> { MovementPattern() },
                new List<ActionSetDefinition> { actionSet },
                new List<StatusEffectSpec>(),
                new List<EncounterDefinition>(),
                null,
                "test-definition-hash");
        }

        private sealed class Facts : IActionPlanFactsSource
        {
            private readonly Dictionary<long, ActionPlanOwnerFacts> _map =
                new Dictionary<long, ActionPlanOwnerFacts>();

            public void Add(UnitId unitId, FactionId factionId, bool isAlive = true)
                => _map[unitId.Value] = new ActionPlanOwnerFacts(
                    unitId, factionId, 10f, 10f, new ActionSetId(ActionSetId), isAlive);

            public bool TryGetOwnerFacts(UnitId unitId, out ActionPlanOwnerFacts facts)
                => _map.TryGetValue(unitId.Value, out facts);
        }

        private sealed class Harness
        {
            public BattleDefinition Definition;
            public LogicGrid Grid;
            public ActionScheduleAuthority Schedule;
            public LogicGridMovementAuthority Movement;
            public LogicGridMovementPathCalculator Calculator;
            public ScheduleEditor Editor;
            public ActionPlanFactory Factory;
            public Facts Facts;
            public ActionPlanTerminalCoordinator Coordinator;
            public readonly Dictionary<long, long> TerminatedAt = new Dictionary<long, long>();
        }

        private static Harness NewHarness()
        {
            BattleDefinition definition = BuildDefinition();
            var facts = new Facts();
            facts.Add(U(1L), new FactionId("faction.hero"));
            facts.Add(U(2L), new FactionId("faction.monster"));
            facts.Add(U(3L), new FactionId("faction.hero"));

            var grid = new LogicGrid(Wide);
            var schedule = new ActionScheduleAuthority();
            var movement = new LogicGridMovementAuthority(
                grid, schedule, definition.Rules.PathCostRules, definition.Rules.PathSearchRules);
            var calculator = new LogicGridMovementPathCalculator(
                grid, schedule, definition.Rules.PathCostRules, definition.Rules.PathSearchRules);
            var ids = new LogicIdGenerator();
            var factory = new ActionPlanFactory(definition, BuildResolver(definition), facts, ids);
            var editor = new ScheduleEditor(schedule, factory, ids, calculator);
            var harness = new Harness
            {
                Definition = definition,
                Grid = grid,
                Schedule = schedule,
                Movement = movement,
                Calculator = calculator,
                Editor = editor,
                Factory = factory,
                Facts = facts
            };
            var participants = new List<IActionPlanCleanupParticipant> { movement };
            harness.Coordinator = new ActionPlanTerminalCoordinator(schedule, participants);
            Assert.That(grid.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East, Directions()), Is.Null);
            Assert.That(grid.RegisterUnit(U(2L), new GridPoint(0, 30), GridDirection.East, Directions()), Is.Null);
            Assert.That(grid.RegisterUnit(U(3L), new GridPoint(0, -30), GridDirection.East, Directions()), Is.Null);
            return harness;
        }

        private static IFactionRelationResolver BuildResolver(BattleDefinition definition)
        {
            var unitFactions = new Dictionary<UnitId, FactionId>
            {
                [U(1L)] = new FactionId("faction.hero"),
                [U(2L)] = new FactionId("faction.monster")
            };
            return new FactionRelationResolver(definition.FactionModel, unitFactions);
        }

        /// <summary>经真实排程事务新增一个普通 Move 计划（返回事务结果）。</summary>
        private static ScheduleEditTransactionResult AddMove(
            Harness h, long temporaryKey, long unitId, int toX, int toY, long startTick)
            => h.Editor.Apply(
                new ScheduleEditOperation[]
                {
                    new AddOrdinaryPlanOperation(
                        temporaryKey, U(unitId), new ActionSpecId(MoveSpecId), startTick,
                        AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                        Facing: GridDirection.East, Destination: new GridPoint(toX, toY))
                },
                currentTick: 0L,
                batchBaseScheduleRevision: h.Schedule.ScheduleRevision,
                expectedScheduleRevision: h.Schedule.ScheduleRevision);

        /// <summary>新增一个 Move 计划并断言事务成功；返回权威注册表里的计划。</summary>
        private static ActionPlan AddMoveOrFail(
            Harness h, long temporaryKey, long unitId, int toX, int toY, long startTick)
        {
            ScheduleEditTransactionResult result = AddMove(h, temporaryKey, unitId, toX, toY, startTick);
            Assert.That(result.RejectionCode, Is.Null,
                "排程事务必须成功（startTick=" + startTick + " unit=" + unitId + " -> (" + toX + "," + toY + ")）");
            Assert.That(result.Committed, Is.True, result.RejectionCode);
            Assert.That(result.AddedPlanIds.Count, Is.EqualTo(1));
            ActionPlan plan = h.Schedule.Registry.Find(result.AddedPlanIds[0]);
            Assert.That(plan, Is.Not.Null, "新增计划必须进入权威注册表");
            return plan;
        }

        /// <summary>按该计划当前的权威时序为它建立真实段/预留（消费真实 Pathfinder 的结果）。</summary>
        private static void EstablishRealMovement(Harness h, ActionPlan plan)
        {
            PathSearchResult path = h.Calculator.FindPathFor(plan, plan.StartTick);
            Assert.That(path.Succeeded, Is.True, path.FailureCode);
            MovementReplacementResult result = h.Movement.EstablishMovement(
                plan, path.Path, plan.StartTick);
            Assert.That(result.Succeeded, Is.True, result.FailureCode);
        }

        private static LogicSnapshot BuildSnapshot(Harness h, long tick,
            IReadOnlyList<MovementSegmentSnapshot> segments, IReadOnlyList<ReservationSnapshot> reservations)
            => new LogicSnapshot(
                tick, h.Definition.RulesVersion, h.Definition.BattleDefinitionHashValue, "encounter.test",
                BattleEndSnapshot.Active(),
                Array.Empty<UnitSnapshot>(), Array.Empty<StatusEffectSnapshot>(),
                TurnWindowManagerSnapshot.None(), ConcurrentActionSnapshot.None(), BattleResourceSnapshot.None(0),
                h.Schedule.ScheduleRevision,
                Array.Empty<ActionPlanSnapshot>(), Array.Empty<ReactionOpportunitySnapshot>(),
                Array.Empty<ActorLaneSnapshot>(), Array.Empty<IntentSnapshot>(),
                segments, reservations, Array.Empty<AiControllerSnapshot>(),
                null, null,
                3L, 10L, 1L, 1L, 1L, 1L, 1L, 1L, 1L, 1L,
                HistorySummary.Empty(), "command-source-priority-v1");

        /// <summary>
        /// 真实主战斗定义的库↔体积显式绑定条数（任务 02B 定义哈希修订的
        /// <c>definition.library_volume_binding_count</c> 分量）。
        ///
        /// 该常量是**生产事实**：<c>LegacyIdMigrationManifest.LibraryVolumes</c> 当前恰好有
        /// 2 条（<c>ForRadius1 → Radius_1</c>、<c>ForRadius2 → Radius_2</c>）。
        /// <see cref="ProjectHero.Logic.Tests"/> <strong>没有</strong>对
        /// <c>ProjectHero.Authoring</c> 的引用，因此这里不可能读取那个清单；本任务只在
        /// 这里使用它构造快照，绝对摘要锚点由 Authoring.Tests 的
        /// <c>MainEncounterDefinitionHashMatchesFrozenAnchor</c> 负责，两边对同一事实的一致性
        /// 由"两份程序集各自复算 == 同一个冻结摘要"共同保证。
        /// </summary>
        private const int ProductionLibraryVolumeBindingCount = 2;

        // ————————————————————————————————————————————————————————————
        // P2：GridState / Segment / Reservation 进入规范化快照与哈希
        // ————————————————————————————————————————————————————————————

        [Test]
        public void GridStateAppearsCanonicallyInSnapshot()
        {
            Harness h = NewHarness();

            ActionPlan first = AddMoveOrFail(h, 1L, 1L, 4, 0, 100L);
            ActionPlan second = AddMoveOrFail(h, 2L, 2L, 4, 30, 100L);
            EstablishRealMovement(h, first);
            EstablishRealMovement(h, second);

            // 1. 权威表里确实有段与预留（否则"快照为空"会是空洞的通过）。
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.GreaterThan(0));
            Assert.That(h.Grid.AllReservationsOrdered().Count, Is.EqualTo(h.Movement.AllSegmentsOrdered().Count),
                "每个活动段必须恰好持有一条目标格 Reservation");

            IReadOnlyList<MovementSegmentSnapshot> segments =
                MovementSnapshotProjection.Segments(h.Movement.AllSegmentsOrdered());
            IReadOnlyList<ReservationSnapshot> reservations =
                MovementSnapshotProjection.Reservations(h.Grid.AllReservationsOrdered());
            Assert.That(segments.Count, Is.EqualTo(h.Movement.AllSegmentsOrdered().Count));
            Assert.That(reservations.Count, Is.EqualTo(h.Grid.AllReservationsOrdered().Count));

            // 2. 投影逐项等于权威数据（不是"数量碰巧相同"）。
            IReadOnlyList<MovementSegment> ordered = h.Movement.AllSegmentsOrdered();
            for (int i = 0; i < ordered.Count; i++)
            {
                Assert.That(segments[i].ActionPlanId, Is.EqualTo(ordered[i].ActionPlanId.Value));
                Assert.That(segments[i].StepIndex, Is.EqualTo(ordered[i].StepIndex));
                Assert.That(segments[i].FromX, Is.EqualTo(ordered[i].From.X));
                Assert.That(segments[i].FromY, Is.EqualTo(ordered[i].From.Y));
                Assert.That(segments[i].ToX, Is.EqualTo(ordered[i].To.X));
                Assert.That(segments[i].ToY, Is.EqualTo(ordered[i].To.Y));
                Assert.That(segments[i].EndTick, Is.EqualTo(ordered[i].EndTick));
            }

            // 3. 快照哈希对"构造顺序"不敏感：打乱输入后必须得到同一摘要。
            ulong fromCanonical = BuildSnapshot(h, 100L, segments, reservations).ComputeHash();
            var shuffledSegments = new List<MovementSegmentSnapshot>(segments);
            shuffledSegments.Reverse();
            var shuffledReservations = new List<ReservationSnapshot>(reservations);
            shuffledReservations.Reverse();
            ulong fromShuffled = BuildSnapshot(h, 100L, shuffledSegments, shuffledReservations).ComputeHash();
            Assert.That(fromShuffled, Is.EqualTo(fromCanonical), "段/预留的构造顺序不得改变规范化摘要");

            // 4. 空 vs 非空必须可区分：这套状态真的进入了哈希。
            ulong empty = BuildSnapshot(h, 100L,
                Array.Empty<MovementSegmentSnapshot>(), Array.Empty<ReservationSnapshot>()).ComputeHash();
            Assert.That(empty, Is.Not.EqualTo(fromCanonical), "段/预留必须参与快照哈希");

            // 5. 已提交段的只读审计不进快照（它在提交后从活动表移除）。
            IReadOnlyList<MovementSegment> committed = h.Movement.ApplyPreCommandBoundary(1000L);
            Assert.That(committed.Count, Is.GreaterThan(0));
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.EqualTo(ordered.Count - committed.Count));
            Assert.That(h.Movement.AuditOfPlan(P(1L)).Count, Is.GreaterThan(0), "审计记录保留");
            Assert.That(MovementSnapshotProjection.Segments(h.Movement.AllSegmentsOrdered()).Count,
                Is.EqualTo(h.Movement.AllSegmentsOrdered().Count));
        }

        [Test]
        public void NoActiveMovementArtifactReferencesTerminalPlanAtStepEnd()
        {
            Harness h = NewHarness();
            ActionPlan first = AddMoveOrFail(h, 1L, 1L, 4, 0, 100L);
            ActionPlan second = AddMoveOrFail(h, 2L, 2L, 4, 30, 100L);
            ActionPlanId firstId = first.ActionPlanId;
            ActionPlanId secondId = second.ActionPlanId;
            EstablishRealMovement(h, first);
            EstablishRealMovement(h, second);
            Assert.That(h.Movement.VerifyInvariants(), Is.Null);

            // 只终止**其中一个**：另一个必须原封不动。
            int before = h.Movement.AllSegmentsOrdered().Count;
            ActionPlanTerminalOutcome outcome = h.Coordinator.EnterTerminal(
                first, ActionTerminationReason.InterruptedByControl, 150L);
            Assert.That(outcome.EnteredTerminal, Is.True);

            // 1. 终态计划不再有任何活动/未来段或 Reservation。
            Assert.That(h.Movement.SegmentsOfPlanOrdered(firstId).Count, Is.EqualTo(0));
            Assert.That(h.Grid.ReservationsOfPlanOrdered(firstId).Count, Is.EqualTo(0));
            Assert.That(h.Movement.VerifyInvariants(), Is.Null,
                "VerifyInvariants 会拒绝任何终态计划残留的活动段");
            Assert.That(h.Grid.VerifyConsistency(), Is.Null);

            // 2. Step 末的只读不变量检查通过。
            Assert.That(h.Schedule.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null);

            // 3. 未终态的另一个计划完全不受影响（不是"顺带清空整张表"）。
            Assert.That(h.Movement.SegmentsOfPlanOrdered(secondId).Count, Is.GreaterThan(0));
            Assert.That(h.Grid.ReservationsOfPlanOrdered(secondId).Count,
                Is.EqualTo(h.Movement.SegmentsOfPlanOrdered(secondId).Count));
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.LessThan(before));
            Assert.That(h.Movement.AllSegmentsOrdered().Count,
                Is.EqualTo(h.Movement.SegmentsOfPlanOrdered(secondId).Count));

            // 4. 幂等：重复终态请求不改变任何东西。
            h.Coordinator.EnterTerminal(first, ActionTerminationReason.BattleEnded, 151L);
            Assert.That(h.Movement.AllSegmentsOrdered().Count,
                Is.EqualTo(h.Movement.SegmentsOfPlanOrdered(secondId).Count));

            // 5. 快照里不再出现该计划的段/预留。
            IReadOnlyList<MovementSegmentSnapshot> segments =
                MovementSnapshotProjection.Segments(h.Movement.AllSegmentsOrdered());
            for (int i = 0; i < segments.Count; i++)
                Assert.That(segments[i].ActionPlanId, Is.Not.EqualTo(firstId.Value));
        }

        [Test]
        public void BattleEndReleasesReservationsAndRemovesActiveMovementSegmentsInStableOrder()
        {
            Harness h = NewHarness();
            var plans = new List<ActionPlan>();
            for (int i = 0; i < 3; i++)
            {
                ActionPlan plan = AddMoveOrFail(h, 10L + i, i + 1L, 4, i * 8, 100L + (i * 100L));
                EstablishRealMovement(h, plan);
                plans.Add(plan);
            }
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.GreaterThan(2));

            // 战斗结束经**同一个**终态协调器逐计划终止（不另建移动清理路径）。
            var planIds = new List<long>();
            foreach (ActionPlan plan in h.Schedule.Registry.ActivePlans) planIds.Add(plan.ActionPlanId.Value);
            planIds.Sort();
            Assert.That(planIds.Count, Is.EqualTo(3));

            for (int i = 0; i < planIds.Count; i++)
            {
                ActionPlan plan = h.Schedule.Registry.Find(new ActionPlanId(planIds[i]));
                Assert.That(plan, Is.Not.Null);
                ActionPlanTerminalOutcome outcome = h.Coordinator.EnterTerminal(
                    plan, ActionTerminationReason.BattleEnded, 1000L);
                Assert.That(outcome.EnteredTerminal, Is.True);
            }

            // 终态：不存在活动段、不存在 Reservation、不变量与 Step 末检查都通过。
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.EqualTo(0));
            Assert.That(h.Grid.AllReservationsOrdered().Count, Is.EqualTo(0));
            Assert.That(h.Movement.VerifyInvariants(), Is.Null);
            Assert.That(h.Grid.VerifyConsistency(), Is.Null);
            Assert.That(h.Schedule.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null,
                "战斗结束只验证'不存在活动段或 Reservation'，不另建清理路径");

            // 重复的战斗结束清理是安全无操作（幂等）。
            for (int i = 0; i < planIds.Count; i++)
            {
                h.Coordinator.EnterTerminal(
                    h.Schedule.Registry.Find(new ActionPlanId(planIds[i])),
                    ActionTerminationReason.BattleEnded, 1001L);
            }
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.EqualTo(0));
            Assert.That(h.Grid.AllReservationsOrdered().Count, Is.EqualTo(0));
        }

        // ————————————————————————————————————————————————————————————
        // P4：单位 → 体积规范表绑定的生产路径
        // ————————————————————————————————————————————————————————————

        // ————————————————————————————————————————————————————————————
        // 段数量 / 权重 / 时长 / 终态中断（任务包必需测试名的逐名补全）
        // ————————————————————————————————————————————————————————————

        [Test]
        public void MoveSegmentCountMatchesLockedPlanResolvedPathEdgeCount()
        {
            Harness h = NewHarness();
            ActionPlan plan = AddMoveOrFail(h, 1L, 1L, 4, 0, 100L);
            Assert.That(plan.ResolvedPathEdgeCount, Is.EqualTo(2), "East 两步的真实边数");
            EstablishRealMovement(h, plan);

            IReadOnlyList<MovementSegment> segments = h.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId);
            Assert.That(segments.Count, Is.EqualTo(plan.ResolvedPathEdgeCount),
                "段数量只由 ResolvedPathEdgeCount 决定（不用它计费）");
            for (int i = 0; i < segments.Count; i++)
            {
                Assert.That(segments[i].StepIndex, Is.EqualTo(i), "步索引必须是 0..n-1 连续");
            }
            Assert.That(h.Movement.VerifyInvariants(), Is.Null);
        }

        [Test]
        public void MoveSegmentWeightSumMatchesLockedPlanResolvedPathWeightUnits()
        {
            Harness h = NewHarness();
            ActionPlan plan = AddMoveOrFail(h, 1L, 1L, 4, 0, 100L);
            EstablishRealMovement(h, plan);

            IReadOnlyList<MovementSegment> segments = h.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId);
            int sum = 0;
            for (int i = 0; i < segments.Count; i++) sum += segments[i].StepWeightUnits;
            Assert.That(sum, Is.EqualTo(plan.ResolvedPathWeightUnits),
                "段权重和必须等于 ResolvedPathWeightUnits（唯一计费尺度）");

            // 每一步的时长严格等于 StepWeightUnits × ResolvedBaseStepTicks。
            for (int i = 0; i < segments.Count; i++)
            {
                Assert.That(segments[i].DurationTicks,
                    Is.EqualTo((long)segments[i].StepWeightUnits * plan.ResolvedBaseStepTicks));
            }
            Assert.That(plan.EndTick,
                Is.GreaterThan(segments[segments.Count - 1].EndTick - 1L),
                "计划结束 Tick 不得早于最后一段的结束边界");
        }

        [Test]
        public void NewMoveTimingDoesNotApplyLegacyPointTwoToFourSecondEdgeClamp()
        {
            // 60 Tick/秒下旧的 0.2–0.4 秒 clamp 等价于 [12, 24] Tick。
            // 新时序不得存在任何这种夹取：极短与极长都必须原样接受。
            var grid = new LogicGrid(Wide);
            Assert.That(grid.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East, Directions()), Is.Null);
            var schedule = new ActionScheduleAuthority();
            var movement = new LogicGridMovementAuthority(
                grid, schedule, PathCostRules.FrozenV1, PathSearchRules.FrozenV1);
            var path = new List<GridPoint> { new GridPoint(0, 0), new GridPoint(2, 0) };

            MovementReplacementResult shortest = movement.EstablishMovement(P(1L), U(1L), path, 0L, 1);
            Assert.That(shortest.Succeeded, Is.True, shortest.FailureCode);
            Assert.That(shortest.Segments[0].DurationTicks, Is.EqualTo(1L),
                "1 Tick 的段必须被原样接受，不得被夹到 12 Tick");

            MovementReplacementResult longest = movement.EstablishMovement(P(2L), U(2L), path, 0L, 1000);
            Assert.That(longest.Succeeded, Is.False, "该单位尚未注册，必须稳定拒绝");

            Assert.That(grid.RegisterUnit(U(2L), new GridPoint(0, 20), GridDirection.East, Directions()), Is.Null);
            MovementReplacementResult longOk = movement.EstablishMovement(
                P(3L), U(2L), new List<GridPoint> { new GridPoint(0, 20), new GridPoint(2, 20) }, 0L, 1000);
            Assert.That(longOk.Succeeded, Is.True, longOk.FailureCode);
            Assert.That(longOk.Segments[0].DurationTicks, Is.EqualTo(1000L),
                "1000 Tick 的段必须被原样接受，不得被夹到 24 Tick");

            // 结构事实：移动时序路径上不存在任何以 Clamp 命名的实现。
            Assert.That(typeof(MovementSegment).GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.DeclaredOnly).Length, Is.GreaterThan(0));
            foreach (System.Reflection.MethodInfo method in typeof(MovementSegment).GetMethods(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                         System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance |
                         System.Reflection.BindingFlags.DeclaredOnly))
            {
                Assert.That(method.Name.IndexOf("Clamp", StringComparison.Ordinal), Is.LessThan(0),
                    "移动时序不得存在夹取：" + method.Name);
            }
        }

        [Test]
        public void MoveTimingOverflowRejectsWholeCandidateWithoutClamp()
        {
            var grid = new LogicGrid(Wide);
            Assert.That(grid.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East, Directions()), Is.Null);
            var movement = new LogicGridMovementAuthority(
                grid, new ActionScheduleAuthority(), PathCostRules.FrozenV1, PathSearchRules.FrozenV1);
            var path = new List<GridPoint> { new GridPoint(0, 0), new GridPoint(2, 0) };

            MovementReplacementResult overflow = movement.EstablishMovement(
                P(1L), U(1L), path, long.MaxValue, 1);
            Assert.That(overflow.Succeeded, Is.False);
            Assert.That(overflow.FailureCode, Is.EqualTo(MovementCodes.MOVEMENT_TIMING_OVERFLOW),
                "checked 溢出必须整体拒绝，绝不 clamp");
            Assert.That(movement.AllSegmentsOrdered().Count, Is.EqualTo(0), "失败必须零写入");
            Assert.That(grid.AllReservationsOrdered().Count, Is.EqualTo(0), "失败必须零写入");
            Assert.That(grid.VerifyConsistency(), Is.Null);
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(0, 0)));
        }

        [Test]
        public void PlanTerminalRemovesActiveAndFutureMovementSegments()
        {
            Harness h = NewHarness();
            ActionPlan plan = AddMoveOrFail(h, 1L, 1L, 4, 0, 100L);
            EstablishRealMovement(h, plan);
            IReadOnlyList<MovementSegment> segments = h.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId);
            Assert.That(segments.Count, Is.EqualTo(2));

            // 先让第 0 段在命令前边界提交（它的足迹进入只读审计）。
            h.Movement.ApplyPreCommandBoundary(segments[0].EndTick);
            Assert.That(h.Movement.AuditOfPlan(plan.ActionPlanId).Count, Is.EqualTo(1));
            Assert.That(h.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId).Count, Is.EqualTo(1),
                "已提交段离开活动表");

            // 任意终态原因：活动与未来段全部移除，审计保留。
            h.Coordinator.EnterTerminal(plan, ActionTerminationReason.InterruptedByIntercept, 200L);
            Assert.That(h.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId).Count, Is.EqualTo(0));
            Assert.That(h.Grid.ReservationsOfPlanOrdered(plan.ActionPlanId).Count, Is.EqualTo(0));
            Assert.That(h.Movement.AuditOfPlan(plan.ActionPlanId).Count, Is.EqualTo(1),
                "已完成段的只读审计必须保留");
            Assert.That(h.Movement.VerifyInvariants(), Is.Null);
        }

        [Test]
        public void PlanTerminalBeforeMovementEndKeepsLastCommittedCell()
        {
            Harness h = NewHarness();
            ActionPlan plan = AddMoveOrFail(h, 1L, 1L, 4, 0, 100L);
            EstablishRealMovement(h, plan);
            IReadOnlyList<MovementSegment> segments = h.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId);
            Assert.That(segments.Count, Is.EqualTo(2));
            Assert.That(segments[0].From, Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(segments[0].To, Is.EqualTo(new GridPoint(2, 0)));

            // 在最后一段 EndTick 之前进入终态：单位停在最后一次已提交的逻辑格（这里还没提交过任何段 ⇒ From）。
            long terminateAt = segments[0].StartTick + 1L;
            Assert.That(terminateAt, Is.LessThan(segments[segments.Count - 1].EndTick));
            h.Coordinator.EnterTerminal(plan, ActionTerminationReason.CancelledByCommand, terminateAt);

            Assert.That(h.Grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(0, 0)),
                "中断在段结束前 ⇒ 保留最后一次已提交逻辑格，绝不预支目的格");
            Assert.That(h.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId).Count, Is.EqualTo(0));
            Assert.That(h.Grid.ReservationsOfPlanOrdered(plan.ActionPlanId).Count, Is.EqualTo(0),
                "目的格与未来路径 Reservation 必须立即释放");
            // (2,0) 是该单位自身体积 footprint 的一部分（East 朝向），因此它仍被 **自己** 占用——
            // 关键事实是"没有被另一个单位的提交写入"，而不是"该格为空"。
            Assert.That(h.Grid.TryGetCellOwner(new GridPoint(2, 0), out UnitId ownerOfFirstStep), Is.True);
            Assert.That(ownerOfFirstStep.Value, Is.EqualTo(1L), "该格只属于本单位自身的体积 footprint");

            // 原 EndTick 之后也不得再发生迟到位置提交（段与预留都已被清理）。
            Assert.That(h.Movement.ApplyPreCommandBoundary(10_000L).Count, Is.EqualTo(0));
            Assert.That(h.Grid.TryGetAnchor(U(1L), out GridPoint stillHere), Is.True);
            Assert.That(stillHere, Is.EqualTo(new GridPoint(0, 0)), "旧 EndTick 不得再提交到 To");
        }

        [Test]
        public void LockedMovePathAndSegmentsCannotBeRecomputed()
        {
            Harness h = NewHarness();
            ActionPlan plan = AddMoveOrFail(h, 1L, 1L, 4, 0, 100L);
            EstablishRealMovement(h, plan);
            IReadOnlyList<MovementSegment> before = h.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId);
            int edgeCount = plan.ResolvedPathEdgeCount;
            int weightUnits = plan.ResolvedPathWeightUnits;
            long startTick = plan.StartTick;
            long endTick = plan.EndTick;

            // 进入非 Editable（Locked/Running/终态共用同一守卫）：路径与段都必须冻结。
            h.Coordinator.EnterTerminal(plan, ActionTerminationReason.InterruptedByClash, 150L);
            Assert.That(plan.IsEditable, Is.False);
            Assert.That(h.Calculator.Recompute(plan, 900L, 0L), Is.Null);
            Assert.That(h.Calculator.ProjectedOriginOf(plan, 900L), Is.EqualTo(new GridPoint(0, 0)),
                "冻结后只读投影仍按权威锚点，不得按新输入漂移");

            Assert.That(plan.ResolvedPathEdgeCount, Is.EqualTo(edgeCount));
            Assert.That(plan.ResolvedPathWeightUnits, Is.EqualTo(weightUnits));
            Assert.That(plan.StartTick, Is.EqualTo(startTick));
            Assert.That(plan.EndTick, Is.EqualTo(endTick));

            // 段被终态清理移除（不是"留着旧段"），且不会因重算被替换成新路径。
            Assert.That(h.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId).Count, Is.EqualTo(0));
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.EqualTo(0));
            Assert.That(before.Count, Is.EqualTo(2), "终态前确实有 2 段（前置条件）");
            Assert.That(h.Movement.VerifyInvariants(), Is.Null);
        }

        [Test]
        public void ProductionVolumeBindingConsumesCanonicalTableForMultiCellOccupancy()
        {
            BattleDefinition definition = BuildDefinition();
            UnitDefinition unitDefinition = definition.FindUnit(new UnitDefinitionId(UnitA));
            Assert.That(unitDefinition, Is.Not.Null);

            // 1. 定义级绑定存在且指向真实规范表（不是"未绑定 → 单格降级"）。
            Assert.That(unitDefinition.VolumeSpecId.Value, Is.EqualTo(VolumeSpecId));
            Assert.That(definition.FindVolume(unitDefinition.VolumeSpecId), Is.Not.Null);

            var source = new DefinitionVolumeTableSource(definition);
            IReadOnlyList<DirectionalTriangleSet> directions =
                source.VolumeDirectionsOf(unitDefinition.UnitDefinitionId);
            Assert.That(directions, Is.Not.Null, "生产装配必须能解析出体积规范表");
            Assert.That(directions.Count, Is.EqualTo(GridDirectionInfo.DirectionCount));
            Assert.That(DirectionalSpecValidation.ValidateDirections(directions), Is.Null);

            // 2. 生产装配路径：用同一来源注册 → 消费规范表（多格 footprint），不再走单格占位。
            var grid = new LogicGrid(Wide);
            Assert.That(grid.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East, directions), Is.Null);
            Assert.That(grid.UsesCanonicalVolumeTable(U(1L)), Is.True,
                "生产装配不得退回 RegisterUnitWithPointFootprint");

            IReadOnlyList<GridPoint> cells = grid.CellsOf(U(1L));
            Assert.That(cells.Count, Is.GreaterThan(1), "该体积表必须产生多格 footprint");
            Assert.That(cells, Is.EqualTo(
                VolumeFootprint.ResolveCells(directions, new GridPoint(0, 0), GridDirection.East)));

            // 3. 区域查询：以 footprint 中任意一格查询都能命中，且按 UnitId 稳定升序。
            var second = new LogicGrid(Wide);
            Assert.That(second.RegisterUnit(U(7L), new GridPoint(0, 0), GridDirection.East, directions), Is.Null);
            Assert.That(second.RegisterUnit(U(3L), new GridPoint(20, 20), GridDirection.East, directions), Is.Null);

            var area = new List<GridPoint>();
            area.AddRange(second.CellsOf(U(7L)));
            area.AddRange(second.CellsOf(U(3L)));
            IReadOnlyList<UnitId> hits = second.UnitsInCellsOrdered(area);
            Assert.That(hits.Count, Is.EqualTo(2));
            Assert.That(hits[0].Value, Is.EqualTo(3L), "区域查询必须按 UnitId 升序");
            Assert.That(hits[1].Value, Is.EqualTo(7L));

            // 4. 只查一格也能命中（体积 footprint 真的进了占位索引）。
            Assert.That(second.UnitsInCellsOrdered(new[] { cells[0] })[0].Value, Is.EqualTo(7L));

            // 5. 未绑定体积的单位仍然走显式降级口径，且可被观察区分（不是静默成功）。
            var degraded = new LogicGrid(Wide);
            Assert.That(degraded.RegisterUnitWithPointFootprint(U(9L), new GridPoint(0, 0), GridDirection.East),
                Is.Null);
            Assert.That(degraded.UsesCanonicalVolumeTable(U(9L)), Is.False);
            Assert.That(degraded.CellsOf(U(9L)).Count, Is.EqualTo(1));
            Assert.That(degraded.CellsOf(U(9L))[0], Is.EqualTo(new GridPoint(0, 0)));

            // 6. 悬空绑定 fail-closed：解析结果是 null，绝不伪造表。
            BattleDefinition dangling = definition with
            {
                Units = new List<UnitDefinition>
                {
                    unitDefinition with { VolumeSpecId = new VolumeSpecId("unit_volume.does_not_exist") }
                }
            };
            Assert.That(new DefinitionVolumeTableSource(dangling)
                .VolumeDirectionsOf(unitDefinition.UnitDefinitionId), Is.Null);
        }

        // ————————————————————————————————————————————————————————————
        // P5：真实路径计算器经真实排程事务被触发
        // ————————————————————————————————————————————————————————————

        [Test]
        public void EditableMoveChainRecomputesPathCountFromProjectedPredecessorPosition()
        {
            Harness h = NewHarness();

            // 第一个 Move：从权威锚点 (0,0) 走到 (4,4)。
            ActionPlan direct = AddMoveOrFail(h, 1L, 1L, 4, 4, 100L);
            Assert.That(direct.IsEditable, Is.True);

            // 真实计算器：预测起点 = 权威锚点 (0,0) ⇒ 4 步 NorthEast（权重 4）。
            Assert.That(h.Calculator.ProjectedOriginOf(direct, 100L), Is.EqualTo(new GridPoint(0, 0)));
            PathSearchResult fromAnchor = h.Calculator.FindPathFor(direct, 100L);
            Assert.That(fromAnchor.Succeeded, Is.True, fromAnchor.FailureCode);
            Assert.That(fromAnchor.EdgeCount, Is.EqualTo(4));
            Assert.That(fromAnchor.PathWeightUnits, Is.EqualTo(4));

            // 插入一个**前置** Move：它在 (0,0) 结束、目的格 (4,0)；
            // 于是后者的预测起点变成 (4,0) ⇒ 到 (4,4) 只需 2 步 North（权重 4）。
            ActionPlan predecessor = AddMoveOrFail(h, 2L, 1L, 4, 0, 0L);
            Assert.That(predecessor.IsEditable, Is.True);

            // 真实 ripple：排程事务必须让后者的路径计数随投影起点一起重算。
            ScheduleEditTransactionResult moved = h.Editor.Apply(
                new ScheduleEditOperation[]
                {
                    new MoveEditablePlanOperation(direct.ActionPlanId, 300L)
                },
                currentTick: 0L,
                batchBaseScheduleRevision: h.Schedule.ScheduleRevision,
                expectedScheduleRevision: h.Schedule.ScheduleRevision);
            Assert.That(moved.Committed, Is.True, moved.RejectionCode);

            ActionPlan recomputed = h.Schedule.Registry.Find(direct.ActionPlanId);
            Assert.That(recomputed, Is.Not.Null);
            Assert.That(recomputed.IsEditable, Is.True, "重算只发生在 Editable 计划上");

            // 预测起点随前序计划变化（这是"重算真的发生了"的判据）。
            GridPoint projected = h.Calculator.ProjectedOriginOf(recomputed, recomputed.StartTick);
            Assert.That(projected, Is.EqualTo(new GridPoint(4, 0)),
                "预测起点必须取时间线上最后一个在 StartTick 前结束的移动族计划的目的格");

            // 路径与预算随投影起点一起重算：边数/权重/结束 Tick 都变了。
            PathSearchResult afterRipple = h.Calculator.FindPathFor(recomputed, recomputed.StartTick);
            Assert.That(afterRipple.Succeeded, Is.True, afterRipple.FailureCode);
            Assert.That(afterRipple.EdgeCount, Is.EqualTo(2));
            Assert.That(afterRipple.PathWeightUnits, Is.EqualTo(4));
            Assert.That(recomputed.ResolvedPathEdgeCount, Is.EqualTo(afterRipple.EdgeCount));
            Assert.That(recomputed.ResolvedPathWeightUnits, Is.EqualTo(afterRipple.PathWeightUnits));

            // 段与结束 Tick 由真实事务重建：每一步严格等于 StepWeightUnits × ResolvedBaseStepTicks。
            // 先让前序 Move 真的走完（命令前边界提交），于是权威锚点变成预测起点 (4,0)，
            // 后者的段链可以按真实路径建立。
            PathSearchResult predecessorPath = h.Calculator.FindPathFor(predecessor, predecessor.StartTick);
            Assert.That(predecessorPath.Succeeded, Is.True, predecessorPath.FailureCode);
            Assert.That(h.Movement.EstablishMovement(predecessor, predecessorPath.Path, predecessor.StartTick).Succeeded,
                Is.True);
            h.Movement.ApplyPreCommandBoundary(1000L);
            Assert.That(h.Grid.TryGetAnchor(U(1L), out GridPoint anchorAfterWalk), Is.True);
            Assert.That(anchorAfterWalk, Is.EqualTo(new GridPoint(4, 0)));
            Assert.That(h.Calculator.ProjectedOriginOf(recomputed, recomputed.StartTick),
                Is.EqualTo(anchorAfterWalk), "前序 Move 提交后，预测起点与权威锚点必须一致");

            MovementReplacementResult established =
                h.Movement.EstablishMovement(recomputed, afterRipple.Path, recomputed.StartTick);
            Assert.That(established.Succeeded, Is.True, established.FailureCode);
            IReadOnlyList<MovementSegment> segments = h.Movement.SegmentsOfPlanOrdered(recomputed.ActionPlanId);
            Assert.That(segments.Count, Is.EqualTo(afterRipple.EdgeCount),
                "段数必须等于真实路径的边数");
            Assert.That(segments[0].From, Is.EqualTo(new GridPoint(4, 0)));
            int weightSum = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                weightSum += segments[i].StepWeightUnits;
                Assert.That(segments[i].DurationTicks,
                    Is.EqualTo((long)segments[i].StepWeightUnits * recomputed.ResolvedBaseStepTicks));
            }
            Assert.That(weightSum, Is.EqualTo(afterRipple.PathWeightUnits),
                "段权重和必须等于真实路径权重（ResolvedPathWeightUnits）");
            Assert.That(h.Movement.VerifyInvariants(), Is.Null);

            // 终态（不可再编辑）之后：真实计算器必须冻结，返回 null 而不是按新输入重算。
            h.Coordinator.EnterTerminal(recomputed, ActionTerminationReason.CancelledByCommand, 301L);
            Assert.That(recomputed.IsEditable, Is.False);
            Assert.That(h.Calculator.Recompute(recomputed, 500L, 0L), Is.Null,
                "非 Editable（Locked/Running/终态共用同一守卫）的计划不得重算路径");
            Assert.That(h.Calculator.FindPathFor(recomputed, 500L).EdgeCount,
                Is.EqualTo(afterRipple.EdgeCount), "冻结后只读查询仍返回冻结结果");
        }
    }
}
