using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 07 裁定 B 的**权威层**环形围栏探针（TEMP 内编译，不属于仓库测试）。
    /// 覆盖"回程链可提交"与"同格重叠 / 他人占位仍然拒绝"在真实
    /// <see cref="LogicGridMovementAuthority"/> 上的端到端表现（含零局部写入）。
    /// </summary>
    public class AuthorityReservationProbeTests
    {
        private static UnitId U(long id) => new UnitId(id);
        private static ActionPlanId P(long id) => new ActionPlanId(id);

        private static readonly GridBoundaryDefinition Wide =
            new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40));

        private static LogicGrid NewGrid()
        {
            var grid = new LogicGrid(Wide);
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            Assert.That(grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(0, 4), GridDirection.East), Is.Null);
            return grid;
        }

        private static LogicGridMovementAuthority NewAuthority(LogicGrid grid)
            => new LogicGridMovementAuthority(
                grid, new ActionScheduleAuthority(), PathCostRules.FrozenV1, PathSearchRules.FrozenV1);

        /// <summary>回程链：head (0,0)->(0,4)，tail (0,4)->(0,0)（重新经过 (0,2) 与 (0,0)）。</summary>
        private static readonly List<GridPoint> HeadPath = new List<GridPoint>
        {
            new GridPoint(0, 0), new GridPoint(0, 2), new GridPoint(0, 4)
        };

        private static readonly List<GridPoint> TailPath = new List<GridPoint>
        {
            new GridPoint(0, 4), new GridPoint(0, 2), new GridPoint(0, 0)
        };

        // ① 回程链（同单位、时间窗不相交）：必须提交成功，且两条计划的段/预留都完整。
        [Test]
        public void ProbeReentrantChainCommitsBothPlans()
        {
            LogicGrid grid = NewGrid();
            LogicGridMovementAuthority authority = NewAuthority(grid);

            MovementReplacementResult head = authority.EstablishMovement(P(1L), U(1L), HeadPath, 0L, 5);
            Assert.That(head.Succeeded, Is.True, head.FailureCode);
            Assert.That(grid.ReservationsOfPlanOrdered(P(1L)).Count, Is.EqualTo(2));

            MovementReplacementResult tail = authority.EstablishMovement(
                P(2L), U(1L), TailPath, 100L, 5, new GridPoint(0, 0), new GridPoint(0, 4));
            Assert.That(tail.Succeeded, Is.True, "回程链的第二条必须与前一条共存：" + tail.FailureCode);
            Assert.That(tail.FailureCode, Is.Null);

            Assert.That(authority.SegmentsOfPlanOrdered(P(1L)).Count, Is.EqualTo(2), "前一条的段毫发无损");
            Assert.That(authority.SegmentsOfPlanOrdered(P(2L)).Count, Is.EqualTo(2));
            Assert.That(grid.ReservationsOfPlanOrdered(P(1L)).Count, Is.EqualTo(2));
            Assert.That(grid.ReservationsOfPlanOrdered(P(2L)).Count, Is.EqualTo(2));
            Assert.That(grid.AllReservationsOrdered().Count, Is.EqualTo(4), "同格两条预留共存（(0,2) 与 (0,0)）");
            Assert.That(authority.VerifyInvariants(), Is.Null);
            Assert.That(grid.VerifyConsistency(), Is.Null);
        }

        // ② 同单位、同一格、**同一时间窗**：必须稳定拒绝，且零局部写入。
        [Test]
        public void ProbeSameUnitOverlappingReuseIsRejectedWithZeroWrites()
        {
            LogicGrid grid = NewGrid();
            LogicGridMovementAuthority authority = NewAuthority(grid);
            Assert.That(authority.EstablishMovement(P(1L), U(1L), HeadPath, 0L, 5).Succeeded, Is.True);

            // 同单位的另一条计划：自称起点 (0,4)（前一条的目的格），第一步落点 (0,2)
            // （与 head 的 [0,10) 完全重叠；(0,4) -> (0,2) 是规范方向 South，权重 2）。
            var overlapping = new List<GridPoint> { new GridPoint(0, 4), new GridPoint(0, 2) };
            MovementReplacementResult reused = authority.EstablishMovement(
                P(2L), U(1L), overlapping, 0L, 5, new GridPoint(0, 2), new GridPoint(0, 4));
            Assert.That(reused.Succeeded, Is.False, "同一时间窗的同格复用不得放行");
            Assert.That(reused.FailureCode, Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER));

            Assert.That(authority.SegmentsOfPlanOrdered(P(2L)).Count, Is.EqualTo(0), "失败必须零局部写入");
            Assert.That(grid.ReservationsOfPlanOrdered(P(2L)).Count, Is.EqualTo(0));
            Assert.That(authority.SegmentsOfPlanOrdered(P(1L)).Count, Is.EqualTo(2), "旧计划毫发无损");
            Assert.That(grid.ReservationsOfPlanOrdered(P(1L)).Count, Is.EqualTo(2));
            Assert.That(authority.VerifyInvariants(), Is.Null);
            Assert.That(grid.VerifyConsistency(), Is.Null);
        }

        // ③ 其他单位、**时间窗完全不相交**：仍然必须拒绝（裁定②：他人的占位照旧阻挡）。
        [Test]
        public void ProbeCrossUnitDisjointReuseIsStillRejected()
        {
            LogicGrid grid = NewGrid();
            LogicGridMovementAuthority authority = NewAuthority(grid);
            Assert.That(authority.EstablishMovement(P(1L), U(1L), HeadPath, 0L, 5).Succeeded, Is.True);

            // 单位 2 从自己的锚点 (0,4) 走一步到 (0,2)：时间窗 [100,110) 与 head 的 [0,10)
            // 不相交，但持有者是**别的单位** ⇒ 必须照旧拒绝。
            var foreign = new List<GridPoint> { new GridPoint(0, 4), new GridPoint(0, 2) };
            MovementReplacementResult reused = authority.EstablishMovement(
                P(9L), U(2L), foreign, 100L, 5, new GridPoint(0, 2));
            Assert.That(reused.Succeeded, Is.False, "他人的占位不得因时间窗不相交而被放行");
            Assert.That(reused.FailureCode, Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER));
            Assert.That(grid.AllReservationsOrdered().Count, Is.EqualTo(2));
            Assert.That(grid.VerifyConsistency(), Is.Null);
        }

        // ④ 同单位、**不相交时间窗**：即使跨事务（前序计划已不参与本批次重建）也必须放行。
        [Test]
        public void ProbeSameUnitDisjointReuseAcrossTransactionsIsAccepted()
        {
            LogicGrid grid = NewGrid();
            LogicGridMovementAuthority authority = NewAuthority(grid);
            Assert.That(authority.EstablishMovement(P(1L), U(1L), HeadPath, 0L, 5).Succeeded, Is.True);

            // 单位 1 稍后回到 (0,2)：时间窗 [100,110) 与 head 的 [0,10) 不相交 ⇒ 可共存。
            var later = new List<GridPoint> { new GridPoint(0, 4), new GridPoint(0, 2) };
            MovementReplacementResult reused = authority.EstablishMovement(
                P(3L), U(1L), later, 100L, 5, new GridPoint(0, 2), new GridPoint(0, 4));
            Assert.That(reused.Succeeded, Is.True, "同一单位的不相交时间窗复用必须放行：" + reused.FailureCode);
            Assert.That(grid.ReservationsOfPlanOrdered(P(1L)).Count, Is.EqualTo(2), "前一条仍持有自己的两条");
            Assert.That(grid.ReservationsOfPlanOrdered(P(3L)).Count, Is.EqualTo(1));
            Assert.That(authority.VerifyInvariants(), Is.Null);
            Assert.That(grid.VerifyConsistency(), Is.Null);
        }

        // ⑤ 起点校验（裁定 B 的另一半）没有被放宽：非链内起点仍然拒绝。
        [Test]
        public void ProbeOriginMismatchIsStillRejected()
        {
            LogicGrid grid = NewGrid();
            LogicGridMovementAuthority authority = NewAuthority(grid);
            Assert.That(authority.EstablishMovement(P(1L), U(1L), HeadPath, 0L, 5).Succeeded, Is.True);

            MovementReplacementResult noOrigin = authority.EstablishMovement(
                P(2L), U(1L), TailPath, 100L, 5, new GridPoint(0, 0));
            Assert.That(noOrigin.FailureCode, Is.EqualTo(MovementCodes.MOVEMENT_SEGMENT_ORIGIN_MISMATCH));

            MovementReplacementResult wrongOrigin = authority.EstablishMovement(
                P(2L), U(1L), TailPath, 100L, 5, new GridPoint(0, 0), new GridPoint(3, 3));
            Assert.That(wrongOrigin.FailureCode, Is.EqualTo(MovementCodes.MOVEMENT_SEGMENT_ORIGIN_MISMATCH));
            Assert.That(authority.SegmentsOfPlanOrdered(P(2L)).Count, Is.EqualTo(0));
        }
    }
}
