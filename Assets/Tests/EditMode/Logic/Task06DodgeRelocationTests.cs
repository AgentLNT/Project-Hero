using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 06「必须产出」11 与「Dodge 换位与移动依赖的原子提交」：
    /// Dodge 目的格预留（接受事务）、TriggerTick 原子换位、统一只读空间快照、
    /// 失败零写入、来源威胁取消释放预留、防御者自身原因的 <c>TargetInvalid</c>。
    ///
    /// 全部用例只消费纯逻辑 API：<see cref="DodgeRelocationAuthority"/> 的空间事务不需要
    /// <c>ActionPlan</c> 实例即可验证（生产路径经同一个内部实现，
    /// 适配层只做 <c>ActionPlan -&gt; 纯数据</c> 的提取）。
    /// </summary>
    public class Task06DodgeRelocationTests
    {
        private static readonly GridBoundaryDefinition Wide =
            new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40));

        private static UnitId U(long id) => new UnitId(id);

        private static ActionPlanId P(long id) => new ActionPlanId(id);

        private static ReactionOpportunityId Opp(long id) => new ReactionOpportunityId(id);

        /// <summary>Dodge Pattern：默认 12 个方向全部覆盖。</summary>
        private static IReadOnlyList<DirectionalTriangleSet> Pattern(params int[] blocked)
        {
            var blockedSet = new HashSet<int>(blocked ?? Array.Empty<int>());
            var list = new List<DirectionalTriangleSet>(GridDirectionInfo.DirectionCount);
            for (int i = 0; i < GridDirectionInfo.DirectionCount; i++)
            {
                var direction = (GridDirection)i;
                IReadOnlyList<TrianglePoint> triangles = blockedSet.Contains(i)
                    ? Array.Empty<TrianglePoint>()
                    : new[] { new TrianglePoint(3, 0, 1) };
                list.Add(new DirectionalTriangleSet(direction, triangles));
            }
            return list;
        }

        private static DodgeDestinationRules Rules(int maxSteps = 2, params int[] blocked)
            => new DodgeDestinationRules(maxSteps, Pattern(blocked));

        private sealed class Harness
        {
            public LogicGrid Grid;
            public LogicGridMovementAuthority Movement;
            public ActionScheduleAuthority Authority;
            public DodgeRelocationAuthority Dodge;
            public readonly List<IReadOnlyList<ActionPlanId>> BudgetReleases =
                new List<IReadOnlyList<ActionPlanId>>();
        }

        private static Harness NewHarness()
        {
            var grid = new LogicGrid(Wide);
            var authority = new ActionScheduleAuthority();
            var movement = new LogicGridMovementAuthority(
                grid, authority, PathCostRules.FrozenV1, PathSearchRules.FrozenV1);
            var dodge = new DodgeRelocationAuthority(grid, movement, authority);
            movement.DodgeDestinationReservations = dodge;
            var harness = new Harness
            {
                Grid = grid,
                Movement = movement,
                Authority = authority,
                Dodge = dodge
            };
            dodge.BudgetReleaseSink = ids => harness.BudgetReleases.Add(ids);
            return harness;
        }

        private static DodgeDestinationRequest Request(
            long opportunityId, long planId, long unitId,
            int fromX, int fromY, int toX, int toY, long triggerTick, long commandSequence = 1L)
            => new DodgeDestinationRequest(
                Opp(opportunityId), P(planId), U(unitId), GridDirection.East,
                new GridPoint(fromX, fromY), new GridPoint(toX, toY), triggerTick, commandSequence);

        /// <summary>为一个单位建立一条 Editable 移动链（North twice）：两段 + 两条 Reservation。</summary>
        private static void EstablishNorthChain(Harness harness, long unitId, long planId, long startTick)
        {
            var path = new List<GridPoint>
            {
                new GridPoint(0, 0), new GridPoint(0, 2), new GridPoint(0, 4)
            };
            MovementReplacementResult result =
                harness.Movement.EstablishMovement(P(planId), U(unitId), path, startTick, 5);
            Assert.That(result.Succeeded, Is.True, result.FailureCode);
        }

        private static void AssertGridUntouched(Harness harness, UnitId unitId, GridPoint expected)
        {
            Assert.That(harness.Grid.TryGetAnchor(unitId, out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(expected), "失败的 Dodge 绝不可以移动单位");
            Assert.That(harness.Grid.VerifyConsistency(), Is.Null, "占位索引必须一致");
        }

        // ————————————————————————————————————————————————————————————
        // 1. 目的格按推导出的 TriggerTick 预留
        // ————————————————————————————————————————————————————————————

        [Test]
        public void DodgeDestinationIsReservedForDerivedTriggerTick()
        {
            Harness h = NewHarness();
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East),
                Is.Null);

            // TriggerTick 由来源攻击的 ImpactTick 推导（这里是 120），命令无法声明它。
            DodgeDestinationRequest request = Request(7L, 10L, 1L, 0, 0, 4, 0, 120L);
            Assert.That(h.Dodge.ReserveDestination(request, Rules(), 100L), Is.Null);

            Assert.That(h.Dodge.TryGetReservation(Opp(7L), out DodgeDestinationReservation reservation), Is.True);
            Assert.That(reservation.TriggerTick, Is.EqualTo(120L), "预留必须绑定推导出的 TriggerTick");
            Assert.That(reservation.From, Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(reservation.Destination, Is.EqualTo(new GridPoint(4, 0)));
            Assert.That(reservation.ReservedAtTick, Is.EqualTo(100L));
            Assert.That(reservation.UnitId.Value, Is.EqualTo(1L));

            // 未到 TriggerTick：不提交；到点：成为到期预留。
            Assert.That(h.Dodge.DueReservationsOrdered(119L).Count, Is.EqualTo(0));
            Assert.That(h.Dodge.DueReservationsOrdered(120L).Count, Is.EqualTo(1));

            // 预留窗口内的该格被标记为已预留；窗口之后不再标记。
            Assert.That(h.Dodge.IsCellReservedByDodge(new GridPoint(4, 0), 100L), Is.True);
            Assert.That(h.Dodge.IsCellReservedByDodge(new GridPoint(4, 0), 119L), Is.True);
            Assert.That(h.Dodge.IsCellReservedByDodge(new GridPoint(4, 0), 121L), Is.False);

            // 接受并等待 Dodge **不**改变位置预测：锚点与占位完全不变。
            AssertGridUntouched(h, U(1L), new GridPoint(0, 0));
            Assert.That(h.Grid.TryGetCellOwner(new GridPoint(4, 0), out _), Is.False,
                "等待期间目的格不得被占位提交");
        }

        // ————————————————————————————————————————————————————————————
        // 2. 提交前的统一只读快照 + 原子提交早于接触复核
        // ————————————————————————————————————————————————————————————

        [Test]
        public void DodgeCommitsDestinationAtomicallyBeforeContactRecheck()
        {
            Harness h = NewHarness();
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East),
                Is.Null);
            Assert.That(h.Dodge.ReserveDestination(Request(7L, 10L, 1L, 0, 0, 4, 0, 120L), Rules(), 100L),
                Is.Null);

            // 任务 08 在提交前冻结统一只读快照（旧接触证据必须留在快照里）。
            DodgeSpaceSnapshot before = h.Dodge.CaptureSpaceSnapshot(120L);
            Assert.That(before.TryGetAnchor(U(1L), out GridPoint oldAnchor), Is.True);
            Assert.That(oldAnchor, Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(before.DodgeReservations.Count, Is.EqualTo(1));
            Assert.That(before.Units.Count, Is.EqualTo(1));

            DodgeCommitResult result = h.Dodge.CommitRelocation(Opp(7L), Array.Empty<ActionPlanId>(), 120L);
            Assert.That(result.Committed, Is.True, result.Code);
            Assert.That(result.Moved, Is.True);
            Assert.That(result.From, Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(result.Destination, Is.EqualTo(new GridPoint(4, 0)));
            Assert.That(result.Tick, Is.EqualTo(120L));

            // 原子提交：占位立刻切到 Destination，且该目的格预留同批释放。
            Assert.That(h.Grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(4, 0)));
            Assert.That(h.Grid.TryGetCellOwner(new GridPoint(0, 0), out _), Is.False, "旧格必须被释放");
            Assert.That(h.Grid.TryGetCellOwner(new GridPoint(4, 0), out UnitId owner), Is.True);
            Assert.That(owner.Value, Is.EqualTo(1L));
            Assert.That(h.Dodge.AllReservationsOrdered().Count, Is.EqualTo(0), "目的格预留必须被释放");
            Assert.That(h.Grid.VerifyConsistency(), Is.Null);

            // 提交前的快照是冻结副本：提交之后仍只反映旧位置（旧接触不会丢失）。
            Assert.That(before.TryGetAnchor(U(1L), out GridPoint stillOld), Is.True);
            Assert.That(stillOld, Is.EqualTo(new GridPoint(0, 0)));

            // 同一份报告同时给出"提交前 + 全部提交后的新位置 + 每个提交的结果"。
            DodgeCommitReport report = h.Dodge.BuildReport(120L, before, h.Dodge.CommitLog);
            Assert.That(report.Before.TryGetAnchor(U(1L), out GridPoint reportOld), Is.True);
            Assert.That(reportOld, Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(report.After.TryGetAnchor(U(1L), out GridPoint reportNew), Is.True);
            Assert.That(reportNew, Is.EqualTo(new GridPoint(4, 0)));
            Assert.That(report.Results.Count, Is.EqualTo(1));
            Assert.That(report.Results[0].Destination, Is.EqualTo(new GridPoint(4, 0)));
            Assert.That(report.CommittedPositions().Count, Is.EqualTo(1));
        }

        // ————————————————————————————————————————————————————————————
        // 3/4. 换位与依赖移动清理的原子性
        // ————————————————————————————————————————————————————————————

        [Test]
        public void DodgeRelocationAndDependentMoveCleanupCommitAtomically()
        {
            Harness h = NewHarness();
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East),
                Is.Null);
            EstablishNorthChain(h, 1L, 20L, 130L);
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.EqualTo(2));
            Assert.That(h.Grid.ReservationsOfPlanOrdered(P(20L)).Count, Is.EqualTo(2));

            Assert.That(h.Dodge.ReserveDestination(Request(7L, 10L, 1L, 0, 0, 4, 0, 120L), Rules(), 100L),
                Is.Null);

            DodgeCommitResult result = h.Dodge.CommitRelocation(Opp(7L), new[] { P(20L) }, 120L);
            Assert.That(result.Committed, Is.True, result.Code);

            // 换位成功 与 依赖移动的整体清理在**同一次提交**内生效。
            Assert.That(h.Grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(4, 0)));
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.EqualTo(0), "失效移动的全部未来段必须同批清理");
            Assert.That(h.Grid.ReservationsOfPlanOrdered(P(20L)).Count, Is.EqualTo(0),
                "失效移动的全部 Reserve 必须同批清理");
            Assert.That(h.Movement.VerifyInvariants(), Is.Null);
            Assert.That(h.Grid.VerifyConsistency(), Is.Null);

            // 任务 07 的预算释放接缝：只在真正换位时被调用一次，内容为按 id 升序的失效计划。
            Assert.That(h.BudgetReleases.Count, Is.EqualTo(1));
            Assert.That(h.BudgetReleases[0].Count, Is.EqualTo(1));
            Assert.That(h.BudgetReleases[0][0].Value, Is.EqualTo(20L));
        }

        [Test]
        public void DodgeCommitFailureLeavesDependentMovesReservationsAndBudgetsUnchanged()
        {
            Harness h = NewHarness();
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East),
                Is.Null);
            EstablishNorthChain(h, 1L, 20L, 130L);
            Assert.That(h.Dodge.ReserveDestination(Request(7L, 10L, 1L, 0, 0, 4, 0, 120L), Rules(), 100L),
                Is.Null);

            // 预留之后目的格被另一个单位占用 ⇒ 换位必须整体失败。
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(4, 0), GridDirection.East),
                Is.Null);

            IReadOnlyList<MovementSegment> segmentsBefore = h.Movement.AllSegmentsOrdered();
            IReadOnlyList<Reservation> reservationsBefore = h.Grid.ReservationsOfPlanOrdered(P(20L));
            int allReservationsBefore = h.Grid.AllReservationsOrdered().Count;

            DodgeCommitResult result = h.Dodge.CommitRelocation(Opp(7L), new[] { P(20L) }, 120L);
            Assert.That(result.Committed, Is.False);
            Assert.That(result.Code, Does.StartWith(LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER));
            Assert.That(result.DefenseFailureReason, Is.EqualTo(ActionTerminationReason.TargetInvalid));

            AssertGridUntouched(h, U(1L), new GridPoint(0, 0));
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.EqualTo(segmentsBefore.Count),
                "失败不得清理任何段");
            Assert.That(h.Grid.ReservationsOfPlanOrdered(P(20L)).Count, Is.EqualTo(reservationsBefore.Count),
                "失败不得释放任何 Reservation");
            Assert.That(h.Grid.AllReservationsOrdered().Count, Is.EqualTo(allReservationsBefore));
            Assert.That(h.BudgetReleases.Count, Is.EqualTo(0), "失败绝不触发预算释放接缝");
            Assert.That(h.Grid.TryGetAnchor(U(2L), out GridPoint other), Is.True);
            Assert.That(other, Is.EqualTo(new GridPoint(4, 0)), "其他单位的占位不得被改写");
        }

        // ————————————————————————————————————————————————————————————
        // 5. 来源威胁取消
        // ————————————————————————————————————————————————————————————

        [Test]
        public void CancelledDodgePreservesFutureMovementChain()
        {
            Harness h = NewHarness();
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East),
                Is.Null);
            EstablishNorthChain(h, 1L, 20L, 130L);
            Assert.That(h.Dodge.ReserveDestination(Request(7L, 10L, 1L, 0, 0, 4, 0, 120L), Rules(), 100L),
                Is.Null);

            IReadOnlyList<MovementSegment> segmentsBefore = h.Movement.AllSegmentsOrdered();
            IReadOnlyList<Reservation> reservationsBefore = h.Grid.ReservationsOfPlanOrdered(P(20L));

            // 来源威胁在 TriggerTick 前取消：任务 05 发来释放通知（同一个接收方）。
            var sink = (IReactionReservationReleaseSink)h.Dodge;
            sink.ReleaseFor(P(10L), Opp(7L), 110L);

            Assert.That(h.Dodge.TryGetReservation(Opp(7L), out _), Is.False, "取消必须释放目的格预留");
            Assert.That(h.Dodge.AllReservationsOrdered().Count, Is.EqualTo(0));

            // 未来移动链完全不受影响（未换位 ⇒ 不清理任何移动）。
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.EqualTo(segmentsBefore.Count));
            Assert.That(h.Grid.ReservationsOfPlanOrdered(P(20L)).Count, Is.EqualTo(reservationsBefore.Count));
            for (int i = 0; i < segmentsBefore.Count; i++)
            {
                MovementSegment expected = segmentsBefore[i];
                MovementSegment actual = h.Movement.AllSegmentsOrdered()[i];
                Assert.That(actual.From, Is.EqualTo(expected.From));
                Assert.That(actual.To, Is.EqualTo(expected.To));
                Assert.That(actual.StartTick, Is.EqualTo(expected.StartTick));
                Assert.That(actual.EndTick, Is.EqualTo(expected.EndTick));
            }
            Assert.That(h.BudgetReleases.Count, Is.EqualTo(0), "取消绝不触发预算释放");
            AssertGridUntouched(h, U(1L), new GridPoint(0, 0));

            // 释放之后再次换位 ⇒ 明确拒绝（内部矛盾口径），且仍然零写入。
            DodgeCommitResult after = h.Dodge.CommitRelocation(Opp(7L), new[] { P(20L) }, 120L);
            Assert.That(after.Committed, Is.False);
            Assert.That(after.Code, Is.EqualTo(DodgeRelocationCodes.DODGE_RELOCATION_NOT_RESERVED));
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.EqualTo(segmentsBefore.Count));
            AssertGridUntouched(h, U(1L), new GridPoint(0, 0));
        }

        // ————————————————————————————————————————————————————————————
        // 6. 未换位不清理任何移动
        // ————————————————————————————————————————————————————————————

        [Test]
        public void DodgeWithoutPositionChangeDoesNotInvalidateMoves()
        {
            Harness h = NewHarness();
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East),
                Is.Null);
            EstablishNorthChain(h, 1L, 20L, 130L);

            // 目的地就是当前格 ⇒ 合法但零位移。
            Assert.That(h.Dodge.ReserveDestination(Request(7L, 10L, 1L, 0, 0, 0, 0, 120L), Rules(), 100L),
                Is.Null);

            DodgeCommitResult result = h.Dodge.CommitRelocation(Opp(7L), new[] { P(20L) }, 120L);
            Assert.That(result.Committed, Is.True, result.Code);
            Assert.That(result.Moved, Is.False, "零位移提交不得报告为换位");

            Assert.That(h.Grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.EqualTo(2), "零位移不得失效任何移动段");
            Assert.That(h.Grid.ReservationsOfPlanOrdered(P(20L)).Count, Is.EqualTo(2),
                "零位移不得释放任何 Reservation");
            Assert.That(h.BudgetReleases.Count, Is.EqualTo(0), "零位移不得触发预算释放");
            Assert.That(h.Dodge.TryGetReservation(Opp(7L), out _), Is.False);
        }

        // ————————————————————————————————————————————————————————————
        // 7. 只读条件预检与提交共用同一个求值函数
        // ————————————————————————————————————————————————————————————

        [Test]
        public void DodgeDependencyPreviewIsReadOnlyAndUsesCommitDependencyQuery()
        {
            Harness h = NewHarness();
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East),
                Is.Null);
            EstablishNorthChain(h, 1L, 20L, 130L);

            DodgeDestinationRequest request = Request(7L, 10L, 1L, 0, 0, 4, 0, 120L);
            var moves = new List<ActionPlanId> { P(22L), P(20L), P(21L) };

            long revisionBefore = h.Authority.ScheduleRevision;
            int reservationsBefore = h.Grid.AllReservationsOrdered().Count;
            int segmentsBefore = h.Movement.AllSegmentsOrdered().Count;

            DodgeDependencyPreview first = h.Dodge.PreviewDependencies(request, Rules(), moves, 100L);
            DodgeDependencyPreview second = h.Dodge.PreviewDependencies(request, Rules(), moves, 100L);

            Assert.That(first.WouldCommit, Is.True, first.Code);
            Assert.That(first.AffectedPlanIds.Count, Is.EqualTo(3));
            Assert.That(first.AffectedPlanIds[0].Value, Is.EqualTo(20L), "受影响列表必须按 ActionPlanId 升序规范");
            Assert.That(first.AffectedPlanIds[2].Value, Is.EqualTo(22L));
            Assert.That(first.BudgetReleasePlanIds.Count, Is.EqualTo(3));
            Assert.That(second.WouldCommit, Is.EqualTo(first.WouldCommit));
            Assert.That(second.Code, Is.EqualTo(first.Code));
            Assert.That(second.BudgetReleasePlanIds.Count, Is.EqualTo(first.BudgetReleasePlanIds.Count));

            // 预检绝不可写状态、绝不可分配 ID、绝不可推进修订号。
            Assert.That(h.Authority.ScheduleRevision, Is.EqualTo(revisionBefore));
            Assert.That(h.Grid.AllReservationsOrdered().Count, Is.EqualTo(reservationsBefore));
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.EqualTo(segmentsBefore));
            Assert.That(h.Dodge.AllReservationsOrdered().Count, Is.EqualTo(0), "预检不得留下预留");
            Assert.That(h.Grid.VerifyConsistency(), Is.Null);

            // 预检与提交的结果一致（同一个求值函数）：
            // 1) 失败场景：目的格被占 ⇒ 预检 code 与实际提交 code 相同。
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(4, 0), GridDirection.East),
                Is.Null);
            DodgeDependencyPreview blocked = h.Dodge.PreviewDependencies(request, Rules(), moves, 100L);
            Assert.That(blocked.WouldCommit, Is.False);
            Assert.That(blocked.BudgetReleasePlanIds.Count, Is.EqualTo(0), "预检失败时不得列出拟释放预算");
            Assert.That(h.Dodge.ReserveDestination(request, Rules(), 100L), Is.EqualTo(blocked.Code));
            Assert.That(h.Dodge.AllReservationsOrdered().Count, Is.EqualTo(0));

            // 2) 成功场景：释放障碍后预检说可以，实际提交也成功。
            Assert.That(h.Grid.UnregisterUnit(U(2L)), Is.Null);
            DodgeDependencyPreview free = h.Dodge.PreviewDependencies(request, Rules(), moves, 100L);
            Assert.That(free.WouldCommit, Is.True, free.Code);
            Assert.That(h.Dodge.ReserveDestination(request, Rules(), 100L), Is.Null);
            Assert.That(h.Dodge.CommitRelocation(Opp(7L), free.BudgetReleasePlanIds, 120L).Committed, Is.True);
        }

        // ————————————————————————————————————————————————————————————
        // 8. 不生成持续移动 / 不产生无敌状态
        // ————————————————————————————————————————————————————————————

        [Test]
        public void DodgeDoesNotCreateContinuousMovementOrInvulnerability()
        {
            Harness h = NewHarness();
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East),
                Is.Null);
            Assert.That(h.Dodge.ReserveDestination(Request(7L, 10L, 1L, 0, 0, 6, 0, 120L), Rules(3), 100L),
                Is.Null);

            DodgeCommitResult result = h.Dodge.CommitRelocation(Opp(7L), Array.Empty<ActionPlanId>(), 120L);
            Assert.That(result.Committed, Is.True, result.Code);

            // 1. 结构：Dodge 不产生任何 MovementSegment，也不写段表。
            Assert.That(h.Movement.AllSegmentsOrdered().Count, Is.EqualTo(0));
            Assert.That(h.Movement.SegmentsOfPlanOrdered(P(10L)).Count, Is.EqualTo(0));
            Assert.That(h.Movement.InFlightSegmentOf(U(1L), 121L), Is.Null,
                "Dodge 之后不存在任何'仍在飞行'的段");
            Assert.That(h.Movement.ActiveReleaseTickOf(U(1L), 121L).HasValue, Is.False,
                "位移不是移动段 ⇒ 没有有限释放边界，也不会伪造 RetryAtTick");

            // 2. 行为：位置是离散跳变，中间格从未被占用。
            Assert.That(h.Grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(6, 0)));
            Assert.That(h.Grid.TryGetCellOwner(new GridPoint(2, 0), out _), Is.False);
            Assert.That(h.Grid.TryGetCellOwner(new GridPoint(4, 0), out _), Is.False);

            // 3. 结构：整个 API 面上不存在"无敌/闪避状态"入口（只有 Dodge 这个名字本身）。
            Assert.That(h.Grid.UnitsInCellsOrdered(new[] { new GridPoint(6, 0) })[0].Value, Is.EqualTo(1L),
                "换位之后单位必须仍然按占位参与查询（不存在免占位旁路）");
            foreach (MethodInfo method in typeof(DodgeRelocationAuthority).GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                         BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (string forbidden in new[] { "Invulner", "Immune", "Dodging", "IgnoreDamage", "NoHit" })
                {
                    Assert.That(method.Name.IndexOf(forbidden, StringComparison.Ordinal), Is.LessThan(0),
                        "Dodge 换位事务不得提供免伤/无敌入口：" + method.Name);
                }
            }
        }

        // ————————————————————————————————————————————————————————————
        // 9. 来源威胁取消释放预留（只读一致性）
        // ————————————————————————————————————————————————————————————

        [Test]
        public void SourceThreatCancellationReleasesDodgeDestinationReservation()
        {
            Harness h = NewHarness();
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East),
                Is.Null);

            Assert.That(h.Dodge.ReserveDestination(Request(7L, 10L, 1L, 0, 0, 4, 0, 120L), Rules(), 100L),
                Is.Null);
            Assert.That(h.Dodge.ReserveDestination(Request(8L, 11L, 1L, 0, 0, 0, 4, 120L), Rules(), 100L),
                Is.Null);
            Assert.That(h.Dodge.AllReservationsOrdered().Count, Is.EqualTo(2));

            var sink = (IReactionReservationReleaseSink)h.Dodge;
            sink.ReleaseFor(P(10L), Opp(7L), 110L);

            Assert.That(h.Dodge.TryGetReservation(Opp(7L), out _), Is.False);
            Assert.That(h.Dodge.TryGetReservation(Opp(8L), out DodgeDestinationReservation kept), Is.True,
                "只释放被取消的那一个机会");
            Assert.That(kept.Destination, Is.EqualTo(new GridPoint(0, 4)));

            // 幂等：重复释放是安全无操作。
            sink.ReleaseFor(P(10L), Opp(7L), 111L);
            Assert.That(h.Dodge.AllReservationsOrdered().Count, Is.EqualTo(1));

            // 被取消的机会无法再换位（零写入）。
            Assert.That(h.Dodge.CommitRelocation(Opp(7L), Array.Empty<ActionPlanId>(), 120L).Code,
                Is.EqualTo(DodgeRelocationCodes.DODGE_RELOCATION_NOT_RESERVED));
            AssertGridUntouched(h, U(1L), new GridPoint(0, 0));
        }

        // ————————————————————————————————————————————————————————————
        // 10. 冲突胜者只服从规范命令序
        // ————————————————————————————————————————————————————————————

        [Test]
        public void DodgeDestinationConflictFollowsCanonicalCommandOrder()
        {
            // 两个机会抢同一个目的格：规范命令序小的赢，且与原始枚举顺序无关。
            DodgeDestinationRequest low = Request(20L, 30L, 1L, 0, 0, 4, 0, 120L, commandSequence: 10L);
            DodgeDestinationRequest high = Request(21L, 31L, 2L, 4, -2, 4, 0, 120L, commandSequence: 50L);

            foreach (bool reversed in new[] { false, true })
            {
                Harness h = NewHarness();
                Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East),
                    Is.Null);
                Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(4, -2), GridDirection.East),
                    Is.Null);

                var requests = new List<DodgeDestinationRequest>();
                var rules = new List<DodgeDestinationRules>();
                if (reversed)
                {
                    requests.Add(high);
                    requests.Add(low);
                    rules.Add(Rules());
                    rules.Add(Rules());
                }
                else
                {
                    requests.Add(low);
                    requests.Add(high);
                    rules.Add(Rules());
                    rules.Add(Rules());
                }

                IReadOnlyList<DodgeReservationOutcome> outcomes = h.Dodge.ReserveDestinations(requests, rules, 100L);

                DodgeReservationOutcome winner = reversed ? outcomes[1] : outcomes[0];
                DodgeReservationOutcome loser = reversed ? outcomes[0] : outcomes[1];
                Assert.That(winner.OpportunityId.Value, Is.EqualTo(20L),
                    "赢家必须由规范命令序决定，与原始枚举顺序无关");
                Assert.That(winner.Reserved, Is.True);
                Assert.That(loser.Reserved, Is.False);
                Assert.That(loser.Code, Does.StartWith(DodgeRelocationCodes.DODGE_DESTINATION_RESERVED_BY_OTHER));

                // 后到者不可抢占：即便赢家被释放之前再试一次也仍然失败。
                Assert.That(h.Dodge.ReserveDestination(high, Rules(), 101L),
                    Does.StartWith(DodgeRelocationCodes.DODGE_DESTINATION_RESERVED_BY_OTHER));

                Assert.That(h.Dodge.TryGetReservation(Opp(20L), out DodgeDestinationReservation reservation),
                    Is.True);
                Assert.That(reservation.CommandSequence, Is.EqualTo(10L));
            }
        }

        // ————————————————————————————————————————————————————————————
        // 11. 非法目的格：以 TargetInvalid 终止且不移动单位
        // ————————————————————————————————————————————————————————————

        [Test]
        public void InvalidDodgeDestinationTerminatesReactionWithoutMovingUnit()
        {
            Harness h = NewHarness();
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East),
                Is.Null);

            // (a) 越界
            Assert.That(h.Dodge.ReserveDestination(Request(1L, 10L, 1L, 0, 0, 200, 0, 120L), Rules(), 100L),
                Is.EqualTo(DodgeRelocationCodes.DODGE_DESTINATION_OUT_OF_BOUNDS));
            // (b) 距离超过 MaxDistanceSteps（3 步 East > 2）
            Assert.That(h.Dodge.ReserveDestination(Request(2L, 11L, 1L, 0, 0, 6, 0, 120L), Rules(2), 100L),
                Is.EqualTo(DodgeRelocationCodes.DODGE_DESTINATION_TOO_FAR));
            // (c) 位移不是任何规范方向（(4,2) 不是 12 向表中任何方向的整数倍）
            Assert.That(h.Dodge.ReserveDestination(Request(3L, 12L, 1L, 0, 0, 4, 2, 120L), Rules(3), 100L),
                Is.EqualTo(DodgeRelocationCodes.DODGE_DESTINATION_TOO_FAR));
            // (d) 方向不被 Pattern 覆盖
            Assert.That(h.Dodge.ReserveDestination(Request(4L, 13L, 1L, 0, 0, 4, 0, 120L), Rules(2, (int)GridDirection.East), 100L),
                Is.EqualTo(DodgeRelocationCodes.DODGE_DESTINATION_DIRECTION_NOT_IN_PATTERN));

            Assert.That(h.Dodge.AllReservationsOrdered().Count, Is.EqualTo(0), "全部拒绝都必须零写入");
            AssertGridUntouched(h, U(1L), new GridPoint(0, 0));

            // (e) 触发时刻目的格已经不可用（防御者自身原因）⇒ TargetInvalid，单位不移动。
            Assert.That(h.Dodge.ReserveDestination(Request(5L, 14L, 1L, 0, 0, 4, 0, 120L), Rules(), 100L),
                Is.Null);
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(4, 0), GridDirection.East),
                Is.Null);

            DodgeCommitResult result = h.Dodge.CommitRelocation(Opp(5L), Array.Empty<ActionPlanId>(), 120L);
            Assert.That(result.Committed, Is.False);
            Assert.That(result.DefenseFailureReason, Is.EqualTo(ActionTerminationReason.TargetInvalid),
                "防御者自身原因导致的触发失败必须以 TargetInvalid 终止");
            AssertGridUntouched(h, U(1L), new GridPoint(0, 0));
            Assert.That(h.Dodge.TryGetReservation(Opp(5L), out _), Is.False);
            Assert.That(h.BudgetReleases.Count, Is.EqualTo(0));
        }

        // ————————————————————————————————————————————————————————————
        // 12. 换位后的新格仍然能被区域攻击命中
        // ————————————————————————————————————————————————————————————

        [Test]
        public void AreaAttackCanStillHitCommittedDodgeDestination()
        {
            Harness h = NewHarness();
            Assert.That(h.Grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East),
                Is.Null);
            Assert.That(h.Dodge.ReserveDestination(Request(7L, 10L, 1L, 0, 0, 4, 0, 120L), Rules(), 100L),
                Is.Null);

            DodgeSpaceSnapshot before = h.Dodge.CaptureSpaceSnapshot(120L);
            int logStart = h.Dodge.CommitLog.Count;
            DodgeCommitResult result = h.Dodge.CommitRelocation(Opp(7L), Array.Empty<ActionPlanId>(), 120L);
            Assert.That(result.Committed, Is.True, result.Code);
            DodgeCommitReport report = h.Dodge.BuildReport(120L, before, h.Dodge.CommitLogSince(logStart));

            // 任务 08 用"提交前快照的旧格 + 提交后快照的新格"建立并集：新格必须能被查询命中。
            Assert.That(h.Grid.UnitsInCellsOrdered(new[] { new GridPoint(4, 0) })[0].Value, Is.EqualTo(1L),
                "新格上的单位必须仍然可被区域查询命中（不存在基于 Dodging 状态的免伤旁路）");
            Assert.That(h.Grid.UnitsInCellsOrdered(new[] { new GridPoint(0, 0) }).Count, Is.EqualTo(0),
                "旧格已经不再占用");

            Assert.That(report.After.TryGetAnchor(U(1L), out GridPoint after), Is.True);
            Assert.That(after, Is.EqualTo(new GridPoint(4, 0)));
            Assert.That(report.Before.TryGetAnchor(U(1L), out GridPoint old), Is.True);
            Assert.That(old, Is.EqualTo(new GridPoint(0, 0)), "旧接触证据必须保留（不得只给新格）");

            // 单位朝向不因换位改变，体积/占位查询口径保持一致。
            Assert.That(h.Grid.TryGetFacing(U(1L), out GridDirection facing), Is.True);
            Assert.That(facing, Is.EqualTo(GridDirection.East));
            Assert.That(h.Grid.CellsOf(U(1L)).Count, Is.EqualTo(1));
        }
    }
}
