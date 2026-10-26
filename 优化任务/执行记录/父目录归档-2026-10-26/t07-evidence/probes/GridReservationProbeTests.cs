using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 07 裁定 B 的**环形围栏**探针（不属于仓库测试；编译在 TEMP，用于验证
    /// "同一单位同格不相交时间窗可共存"这一唯一放宽**没有**动到任何既有语义）。
    /// </summary>
    public class GridReservationProbeTests
    {
        private static UnitId U(long id) => new UnitId(id);
        private static ActionPlanId P(long id) => new ActionPlanId(id);

        private static LogicGrid NewGrid()
        {
            var grid = new LogicGrid(new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40)));
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            Assert.That(grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(0, 4), GridDirection.East), Is.Null);
            return grid;
        }

        private static readonly GridPoint Cell = new GridPoint(2, 0);

        // ① 同一时间窗 + 同格（同单位）：仍必须稳定拒绝。
        [Test]
        public void ProbeIdenticalWindowSameUnitIsStillRejected()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(11L), 0), U(1L), Cell, 0L, 10L)),
                Is.Null);
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(12L), 0), U(1L), Cell, 0L, 10L)),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER));
            Assert.That(grid.AllReservationsOrdered().Count, Is.EqualTo(1), "失败的预留绝不落地");
            Assert.That(grid.ReservationAt(Cell).ActionPlanId.Value, Is.EqualTo(11L), "持有者不变");
            Assert.That(grid.VerifyConsistency(), Is.Null);
        }

        // ② 时间窗重叠（同单位）：仍必须稳定拒绝。
        [Test]
        public void ProbeOverlappingWindowSameUnitIsStillRejected()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(11L), 0), U(1L), Cell, 0L, 10L)),
                Is.Null);
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(12L), 0), U(1L), Cell, 5L, 15L)),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER), "重叠（5 < 10）不得放行");
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(12L), 0), U(1L), Cell, 9L, 10L)),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER), "半开区间 [9,10) 仍与 [0,10) 重叠");
            Assert.That(grid.AllReservationsOrdered().Count, Is.EqualTo(1));
        }

        // ③ 其他单位的占位：即使时间窗完全不相交也必须照旧阻挡（裁定②）。
        [Test]
        public void ProbeOtherUnitDisjointWindowIsStillRejected()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(11L), 0), U(1L), Cell, 0L, 10L)),
                Is.Null);
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(21L), 0), U(2L), Cell, 100L, 110L)),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER),
                "他人的占位仍然阻挡——不含时间维的放宽");
            Assert.That(grid.AllReservationsOrdered().Count, Is.EqualTo(1));
        }

        // ④ 同一单位的相邻（不相交）时间窗：允许共存，且读法仍保守（最晚释放边界）。
        [Test]
        public void ProbeSameUnitDisjointWindowCoexists()
        {
            LogicGrid grid = NewGrid();
            var earlier = new Reservation(new ReservationKey(P(11L), 0), U(1L), Cell, 0L, 10L);
            var later = new Reservation(new ReservationKey(P(12L), 0), U(1L), Cell, 10L, 20L);
            Assert.That(grid.TryReserve(earlier), Is.Null);
            Assert.That(grid.TryReserve(later), Is.Null, "同一单位、半开区间 [0,10) 与 [10,20) 不相交 ⇒ 可共存");
            Assert.That(grid.AllReservationsOrdered().Count, Is.EqualTo(2));
            Assert.That(grid.ReservationAt(Cell).EndTick, Is.EqualTo(20L), "规范持有者取最晚释放边界");
            Assert.That(grid.VerifyConsistency(), Is.Null, "共存也必须自洽");

            // 释放顺序不得改变读数：先释放晚的，早的仍在。
            Assert.That(grid.ReleaseReservation(later.Key), Is.True);
            Assert.That(grid.ReservationAt(Cell).EndTick, Is.EqualTo(10L));
            Assert.That(grid.ReleaseReservation(earlier.Key), Is.True);
            Assert.That(grid.ReservationAt(Cell), Is.Null);

            // 再按相反顺序释放，读数同样正确。
            Assert.That(grid.TryReserve(earlier), Is.Null);
            Assert.That(grid.TryReserve(later), Is.Null);
            Assert.That(grid.ReleaseReservation(earlier.Key), Is.True);
            Assert.That(grid.ReservationAt(Cell).EndTick, Is.EqualTo(20L), "早的释放后晚的仍是持有者");
            Assert.That(grid.VerifyConsistency(), Is.Null);
        }

        // ⑤ 倒序建立（回滚恢复的形状）：晚的先在、早的后到，同样允许共存。
        [Test]
        public void ProbeReverseOrderCoexistenceIsAlsoAllowed()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(12L), 0), U(1L), Cell, 10L, 20L)),
                Is.Null);
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(11L), 0), U(1L), Cell, 0L, 10L)),
                Is.Null, "回滚恢复可能以相反顺序重放同一格的两条不相交预留");
            Assert.That(grid.AllReservationsOrdered().Count, Is.EqualTo(2));
            Assert.That(grid.ReservationAt(Cell).EndTick, Is.EqualTo(20L));
            Assert.That(grid.VerifyConsistency(), Is.Null);
        }

        // ⑥ 启动门禁：多持有者时提示的释放边界必须保守（不早于该格真正空出的时刻）。
        [Test]
        public void ProbeStartGateReportsLatestReleaseBoundary()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(11L), 0), U(1L), Cell, 0L, 10L)),
                Is.Null);
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(12L), 0), U(1L), Cell, 100L, 110L)),
                Is.Null);

            // 单位 2 想在 tick 5 进入该格：它必须被告知等到最晚的释放边界。
            SpaceBlockQueryResult result = grid.QueryStartBlock(U(2L), Cell, 5L, null);
            Assert.That(result.Kind, Is.EqualTo(SpaceBlockKind.RetryableTimedBlock));
            Assert.That(result.ReleaseTick, Is.EqualTo(110L), "不得提示 10（那会让门禁过早重试）");
        }

        // ⑦ 强制位移前置条件：同格存在任何他人计划预留时预检仍然失败（冻结语义）。
        [Test]
        public void ProbeBatchRelocationStillRequiresReservationsCleared()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(11L), 0), U(1L), Cell, 0L, 10L)),
                Is.Null);
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(12L), 0), U(1L), Cell, 10L, 20L)),
                Is.Null);

            Assert.That(grid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(U(2L), new GridPoint(0, 4), Cell)
            }), Does.StartWith(LogicGridCodes.LOGIC_GRID_BATCH_RESERVATION_NOT_CLEARED));

            Assert.That(grid.ReleaseReservationsOfPlan(P(11L)).Count, Is.EqualTo(1));
            Assert.That(grid.ReleaseReservationsOfPlan(P(12L)).Count, Is.EqualTo(1));
            Assert.That(grid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(U(2L), new GridPoint(0, 4), Cell)
            }), Is.Null);
        }

        // ⑧ 预留键唯一性/区间合法性的既有错误码不变。
        [Test]
        public void ProbeExistingReservationErrorCodesUnchanged()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.TryReserve(null), Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVATION_INTERVAL_INVALID));
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(11L), 0), U(1L), Cell, 5L, 5L)),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVATION_INTERVAL_INVALID));
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(11L), 0), U(1L), Cell, 20L, 10L)),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVATION_INTERVAL_INVALID));
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(11L), -1), U(1L), Cell, 0L, 10L)),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVATION_INTERVAL_INVALID));
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(11L), 0), U(1L), Cell, 0L, 10L)), Is.Null);
            Assert.That(grid.TryReserve(new Reservation(new ReservationKey(P(11L), 0), U(1L), Cell, 100L, 110L)),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVATION_DUPLICATE_KEY));
        }
    }
}
