using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 08 ·「同时强制位移协议」的<strong>求解器用例</strong>（任务包 :333-348、:358）。
    ///
    /// 全部用例只依赖 <see cref="LogicGrid"/> 的公开只读面 + <see cref="ForcedDisplacementSolver"/>，
    /// 不装配 <c>BattleSimulation</c>：这样"一步一轮、无赢家、不连锁推人"这些规则被
    /// <strong>单独</strong>钉住，而不是混在整步流程里。
    ///
    /// 每个用例都写明"哪种实现缺陷会让它失败"。
    /// </summary>
    public class Task08ForcedDisplacementSolverTests
    {
        private static readonly GridBoundaryDefinition Wide =
            new GridBoundaryDefinition(new GridPoint(-20, -20), new GridPoint(20, 20));

        /// <summary>窄边界（East 增量 = (+2, 0)）：用于边界停止与"边界先于障碍"用例。</summary>
        private static readonly GridBoundaryDefinition Narrow =
            new GridBoundaryDefinition(new GridPoint(0, 0), new GridPoint(4, 6));

        private static UnitId U(long id) => new UnitId(id);
        private static ActionPlanId P(long id) => new ActionPlanId(id);

        private static ForcedDisplacementRequest Request(
            long unitId, GridDirection direction, int steps,
            long momentum = 10L, long conflictGroupKey = 1L)
            => new ForcedDisplacementRequest(U(unitId), direction, steps, momentum, conflictGroupKey);

        private static ForcedDisplacementSolver Solver(
            LogicGrid grid, ActionScheduleAuthority authority = null,
            IReadOnlyList<GridPoint> obstacles = null, ForcedDisplacementStats stats = null)
            => new ForcedDisplacementSolver(grid, authority, null, obstacles, stats);

        private static void Place(LogicGrid grid, long unitId, int x, int y,
            GridDirection facing = GridDirection.East)
        {
            string error = grid.RegisterUnitWithPointFootprint(U(unitId), new GridPoint(x, y), facing);
            Assert.That(error, Is.Null, "夹具前提：单位必须注册成功 unit=" + unitId + " code=" + error);
        }

        private static UnitSnapshot[] Snapshot(LogicGrid grid)
        {
            IReadOnlyList<UnitId> units = grid.RegisteredUnitsOrdered();
            var result = new List<UnitSnapshot>(units.Count);
            for (int i = 0; i < units.Count; i++)
            {
                grid.TryGetAnchor(units[i], out GridPoint anchor);
                grid.TryGetFacing(units[i], out GridDirection facing);
                result.Add(new UnitSnapshot(
                    units[i].Value, "unit.test", "faction.test", anchor.X, anchor.Y, (int)facing,
                    1000, true, 0, 1L));
            }
            return result.ToArray();
        }

        private static ForcedDisplacementBatch Solve(
            ForcedDisplacementSolver solver, LogicGrid grid, GridBoundaryDefinition boundary,
            params ForcedDisplacementRequest[] requests)
            => solver.ResolveAll(requests, Snapshot(grid), boundary, 0L);

        private static ForcedDisplacementResolution Of(ForcedDisplacementBatch batch, long unitId)
            => batch.Resolutions.Single(r => r.TargetUnitId.Value == unitId);

        private static GridPoint AnchorOf(LogicGrid grid, long unitId)
        {
            Assert.That(grid.TryGetAnchor(U(unitId), out GridPoint anchor), Is.True);
            return anchor;
        }

        // ================= 基本步进 =================

        /// <summary>
        /// 缺陷指纹：若每轮距离由 <c>PathCostRules.StepWeightUnits</c>（偶数方向 1 / 奇数方向 2）决定，
        /// 奇数方向（EastNorth = (+3,+1)，权重 2）会走错格或走不完。
        /// </summary>
        [Test]
        public void ForcedDisplacementStepUsesCanonicalDirectionDeltaNotPathWeight()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            ForcedDisplacementBatch batch = Solve(Solver(grid), grid, Wide,
                Request(1L, GridDirection.EastNorth, 1));

            ForcedDisplacementResolution resolution = Of(batch, 1L);
            Assert.That(resolution.From, Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(resolution.To, Is.EqualTo(new GridPoint(3, 1)), "EastNorth 必须是 (+3,+1)");
            Assert.That(resolution.AppliedSteps, Is.EqualTo(1));
            Assert.That(resolution.StopReason, Is.EqualTo(ForcedDisplacementStopReason.Completed));
            Assert.That(AnchorOf(grid, 1L), Is.EqualTo(new GridPoint(0, 0)), "求解不得修改真实网格");
        }

        /// <summary>
        /// 缺陷指纹：若位移按"绕锚点旋转 footprint"实现，朝向会被改写成位移方向；
        /// 若求解期提交朝向，网格朝向也会变。
        /// </summary>
        [Test]
        public void ForcedDisplacementTranslatesCurrentFootprintWithoutChangingFacing()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0, GridDirection.SouthWest);
            ForcedDisplacementBatch batch = Solve(Solver(grid), grid, Wide,
                Request(1L, GridDirection.North, 2));

            Assert.That(Of(batch, 1L).To, Is.EqualTo(new GridPoint(0, 4)));
            Assert.That(grid.TryGetFacing(U(1L), out GridDirection facing), Is.True);
            Assert.That(facing, Is.EqualTo(GridDirection.SouthWest), "位移不得改变朝向");
        }

        /// <summary>
        /// 缺陷指纹：若边界只判锚点不判完整 footprint，多格单位会有半截身体越界；
        /// 若越界时把步数清零，"保留成功前缀"会被破坏。
        /// </summary>
        [Test]
        public void ForcedDisplacementStopsAtBoundaryOnFirstOrLaterStep()
        {
            var grid = new LogicGrid(Narrow);
            Place(grid, 1L, 0, 0);
            ForcedDisplacementBatch first = Solve(Solver(grid), grid, Narrow, Request(1L, GridDirection.West, 1));
            Assert.That(Of(first, 1L).AppliedSteps, Is.EqualTo(0));
            Assert.That(Of(first, 1L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.Boundary));
            Assert.That(Of(first, 1L).To, Is.EqualTo(Of(first, 1L).From), "第一步就停下时 To 必须等于 From");

            var later = new LogicGrid(Narrow);
            Place(later, 2L, 0, 0);
            ForcedDisplacementBatch batch = Solve(Solver(later), later, Narrow, Request(2L, GridDirection.East, 4));
            // 每轮 +2：0 -> 2 -> 4，第三轮越界；成功前缀 = 2 步。
            Assert.That(Of(batch, 2L).AppliedSteps, Is.EqualTo(2));
            Assert.That(Of(batch, 2L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.Boundary));
            Assert.That(Of(batch, 2L).To, Is.EqualTo(new GridPoint(4, 0)));
        }

        /// <summary>
        /// 缺陷指纹：若步内次序写成"先障碍后边界"，同时越界且是障碍的格会报
        /// <c>StaticObstacle</c> 而不是 <c>Boundary</c>（任务包 :192 的固定次序被写反）。
        /// </summary>
        [Test]
        public void ForcedDisplacementStopsAtStaticObstacleOnFirstOrLaterStep()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            ForcedDisplacementBatch batch = Solve(
                Solver(grid, null, new[] { new GridPoint(4, 0) }), grid, Wide,
                Request(1L, GridDirection.East, 3));
            Assert.That(Of(batch, 1L).AppliedSteps, Is.EqualTo(1), "保留障碍之前的成功步数");
            Assert.That(Of(batch, 1L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.StaticObstacle));

            // 边界优先：同一个点同时越界且是障碍时必须是 Boundary。
            var narrow = new LogicGrid(Narrow);
            Place(narrow, 2L, 0, 0);
            ForcedDisplacementBatch priority = Solve(
                Solver(narrow, null, new[] { new GridPoint(-2, 0) }), narrow, Narrow,
                Request(2L, GridDirection.West, 1));
            Assert.That(Of(priority, 2L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.Boundary),
                "越界提案不得再用其外部点查询障碍");
        }

        // ================= 无赢家 =================

        /// <summary>
        /// 缺陷指纹：若"没有请求的单位"被当作可推对象自动加入批次（隐式连锁推人），
        /// 静止单位会跟着走，并出现它自己的 Resolution。
        /// </summary>
        [Test]
        public void StationaryUnitHardBlocksForcedDisplacement()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            Place(grid, 2L, 2, 0);
            ForcedDisplacementBatch batch = Solve(Solver(grid), grid, Wide, Request(1L, GridDirection.East, 1));

            Assert.That(Of(batch, 1L).AppliedSteps, Is.EqualTo(0));
            Assert.That(Of(batch, 1L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.OccupiedUnit));
            Assert.That(batch.Resolutions.Count, Is.EqualTo(1), "没有请求的单位不得被隐式加入批次");
            Assert.That(batch.Relocations, Is.Empty);
            Assert.That(AnchorOf(grid, 2L), Is.EqualTo(new GridPoint(2, 0)));
        }

        /// <summary>
        /// 缺陷指纹：若同锚点争抢按 UnitId / MomentumUnits / ConflictGroupKey / 输入顺序选赢家，
        /// 会有单位移动；冻结规则是全部 <c>DestinationContention</c>。
        /// </summary>
        [Test]
        public void SameAnchorContentionFailsAllProposalsWithoutWinner()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            Place(grid, 2L, 6, 2);
            var target = new GridPoint(3, 1);
            Assert.That(new GridPoint(0, 0).Translate(GridNeighborTable.OffsetX(GridDirection.EastNorth),
                GridNeighborTable.OffsetY(GridDirection.EastNorth)), Is.EqualTo(target),
                "夹具前提：1 号的目标锚点");
            Assert.That(new GridPoint(6, 2).Translate(GridNeighborTable.OffsetX(GridDirection.WestSouth),
                GridNeighborTable.OffsetY(GridDirection.WestSouth)), Is.EqualTo(target),
                "夹具前提：2 号的目标锚点");

            ForcedDisplacementBatch batch = Solve(
                Solver(grid), grid, Wide,
                Request(1L, GridDirection.EastNorth, 1, momentum: 999L, conflictGroupKey: 1L),
                Request(2L, GridDirection.WestSouth, 1, momentum: 1L, conflictGroupKey: 2L));

            Assert.That(Of(batch, 1L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.DestinationContention));
            Assert.That(Of(batch, 2L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.DestinationContention));
            Assert.That(Of(batch, 1L).AppliedSteps, Is.EqualTo(0));
            Assert.That(Of(batch, 2L).AppliedSteps, Is.EqualTo(0));
            Assert.That(batch.Relocations, Is.Empty, "争抢者全部失败 ⇒ 不得有任何换位");
            Assert.That(AnchorOf(grid, 1L), Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(AnchorOf(grid, 2L), Is.EqualTo(new GridPoint(6, 2)));
        }

        /// <summary>
        /// 缺陷指纹：若"体积重叠"按锚点判而不是按完整 footprint 判，不同锚点但身体相交的提案会被放行。
        /// </summary>
        [Test]
        public void OverlappingMultiCellDestinationFootprintsFailAllProposals()
        {
            GridPoint originA = new GridPoint(0, 0);
            GridPoint targetA = originA.Translate(GridNeighborTable.OffsetX(GridDirection.East),
                GridNeighborTable.OffsetY(GridDirection.East));

            // 穷举搜索一个"能注册、锚点不同、目标 footprint 与 A 相交"的 B 起点，
            // 而不是对体积几何做猜测（体积表是任务 02B 的规范 12 向表，格数不保证是 2）。
            GridPoint originB = default;
            bool found = false;
            for (int x = -6; x <= 14 && !found; x++)
            {
                for (int y = -6; y <= 14 && !found; y++)
                {
                    if (!GridPoint.TryCreate(x, y, out GridPoint candidate)) continue;
                    if (candidate == originA) continue;

                    var probe = new LogicGrid(Wide);
                    Assert.That(probe.RegisterUnit(U(1L), originA, GridDirection.East, MultiCellDirections()),
                        Is.Null, "夹具前提：1 号必须可注册");
                    if (probe.RegisterUnit(U(2L), candidate, GridDirection.East, MultiCellDirections()) != null)
                        continue;

                    GridPoint candidateTarget = candidate.Translate(
                        GridNeighborTable.OffsetX(GridDirection.West),
                        GridNeighborTable.OffsetY(GridDirection.West));
                    if (candidateTarget == targetA) continue;
                    if (!FootprintsIntersect(probe, U(1L), targetA, GridDirection.East,
                            U(2L), candidateTarget, GridDirection.East)) continue;
                    originB = candidate;
                    found = true;
                }
            }
            Assert.That(found, Is.True, "夹具前提：必须存在一对待重叠的多格目标 footprint");

            GridPoint targetB = originB.Translate(GridNeighborTable.OffsetX(GridDirection.West),
                GridNeighborTable.OffsetY(GridDirection.West));

            var grid = new LogicGrid(Wide);
            Assert.That(grid.RegisterUnit(U(1L), originA, GridDirection.East, MultiCellDirections()), Is.Null);
            Assert.That(grid.RegisterUnit(U(2L), originB, GridDirection.East, MultiCellDirections()), Is.Null);

            ForcedDisplacementBatch batch = Solve(
                Solver(grid), grid, Wide,
                Request(1L, GridDirection.East, 1),
                Request(2L, GridDirection.West, 1));

            ForcedDisplacementResolution a = Of(batch, 1L);
            ForcedDisplacementResolution b = Of(batch, 2L);
            Assert.That(FootprintsIntersect(grid, U(1L), targetA, GridDirection.East, U(2L), targetB, GridDirection.East),
                Is.True, "夹具前提：目标 footprint 必须相交");
            Assert.That(targetA, Is.Not.EqualTo(targetB), "夹具前提：锚点不同");

            Assert.That(a.StopReason, Is.EqualTo(ForcedDisplacementStopReason.VolumeOverlap));
            Assert.That(b.StopReason, Is.EqualTo(ForcedDisplacementStopReason.VolumeOverlap));
            Assert.That(batch.Relocations, Is.Empty);
        }

        // ================= 依赖图 =================

        /// <summary>
        /// 缺陷指纹：若交换被当作"双方都无依赖"或"先到先得"，会有人移动；
        /// 冻结规则是双方 <c>DependencyCycle</c>。
        /// </summary>
        [Test]
        public void TwoUnitSwapStopsBothAsDependencyCycle()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            Place(grid, 2L, 2, 0);
            ForcedDisplacementBatch batch = Solve(
                Solver(grid), grid, Wide,
                Request(1L, GridDirection.East, 1),
                Request(2L, GridDirection.West, 1));

            Assert.That(Of(batch, 1L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.DependencyCycle));
            Assert.That(Of(batch, 2L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.DependencyCycle));
            Assert.That(batch.Relocations, Is.Empty);
        }

        /// <summary>
        /// 缺陷指纹：若求解器不建依赖图（"目标格空才移动"），A→B 会被错误拒绝；
        /// 冻结规则允许依赖链最终通向空格时<strong>同时</strong>腾挪。
        /// </summary>
        [Test]
        public void DependencyChainEndingInEmptyCellMovesSimultaneously()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            Place(grid, 2L, 2, 0);
            ForcedDisplacementBatch batch = Solve(
                Solver(grid), grid, Wide,
                Request(1L, GridDirection.East, 1),
                Request(2L, GridDirection.East, 1));

            Assert.That(Of(batch, 1L).AppliedSteps, Is.EqualTo(1));
            Assert.That(Of(batch, 2L).AppliedSteps, Is.EqualTo(1));
            Assert.That(Of(batch, 1L).To, Is.EqualTo(new GridPoint(2, 0)));
            Assert.That(Of(batch, 2L).To, Is.EqualTo(new GridPoint(4, 0)));
            Assert.That(batch.Relocations.Count, Is.EqualTo(2), "依赖链必须在同一轮同时提交");
        }

        /// <summary>
        /// 缺陷指纹：若多格单位"一条分支成功就移动"，它会把身体压进失败分支的格。
        /// 冻结规则：全部分支都成功才移动（:194、:342）。
        /// </summary>
        [Test]
        public void BranchingDependencyMovesOnlyWhenEveryBranchSucceeds()
        {
            // 1 号是多格单位；East 目标 footprint 里挑两格：一格给会走的 2 号，一格给不动的 3 号。
            var probe = new LogicGrid(Wide);
            Assert.That(probe.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East,
                MultiCellDirections()), Is.Null);
            var pusheeOrigin = new GridPoint(0, 0);
            GridPoint targetAnchor = pusheeOrigin.Translate(
                GridNeighborTable.OffsetX(GridDirection.East), GridNeighborTable.OffsetY(GridDirection.East));
            IReadOnlyList<GridPoint> currentCells = probe.CellsOf(U(1L));
            IReadOnlyList<GridPoint> targetCells =
                probe.ResolveDestinationCells(U(1L), targetAnchor, GridDirection.East);

            // 只挑"不在 1 号自己旧 footprint 里"的目标格：否则占据者是 1 号自己（被忽略），构不成依赖。
            var candidates = new List<GridPoint>();
            for (int i = 0; i < targetCells.Count; i++)
            {
                if (Contains(currentCells, targetCells[i])) continue;
                if (!candidates.Contains(targetCells[i])) candidates.Add(targetCells[i]);
            }
            Assert.That(candidates.Count, Is.GreaterThanOrEqualTo(2),
                "夹具前提：至少两个目标格不属于 1 号自己");

            // 2 号必须能自己走掉：它的目标格既不能是 1 号的目标格，也不能在 1 号的旧 footprint 里。
            GridPoint moverCell = default;
            GridPoint moverTarget = default;
            bool moverFound = false;
            for (int i = 0; i < candidates.Count && !moverFound; i++)
            {
                GridPoint candidateTarget = candidates[i].Translate(
                    GridNeighborTable.OffsetX(GridDirection.East), GridNeighborTable.OffsetY(GridDirection.East));
                if (Contains(targetCells, candidateTarget)) continue;
                if (Contains(currentCells, candidateTarget)) continue;
                moverCell = candidates[i];
                moverTarget = candidateTarget;
                moverFound = true;
            }
            Assert.That(moverFound, Is.True, "夹具前提：必须存在一个能走掉的依赖分支");
            GridPoint blockerCell = candidates.First(c => c != moverCell);

            // 变体 A：阻挡分支静止 ⇒ 1 号整步不得移动。
            var blocked = BuildBranchGrid(moverCell, blockerCell, placeBlocker: true);
            ForcedDisplacementBatch blockedBatch = Solve(
                Solver(blocked), blocked, Wide,
                Request(1L, GridDirection.East, 1),
                Request(2L, GridDirection.East, 1));

            ForcedDisplacementResolution pushee = blockedBatch.Resolutions.Single(r => r.TargetUnitId.Value == 1L);
            Assert.That(blockedBatch.Resolutions.Single(r => r.TargetUnitId.Value == 2L).AppliedSteps,
                Is.EqualTo(1), "夹具前提：会走的分支必须成功（它的目标格是 " + moverTarget + "）");
            Assert.That(pushee.AppliedSteps, Is.EqualTo(0), "一条分支被静止单位挡住 ⇒ 多格单位整步不得移动");
            Assert.That(pushee.StopReason, Is.EqualTo(ForcedDisplacementStopReason.OccupiedUnit));

            // 变体 B（负控制）：阻挡分支移除 ⇒ 同一条依赖链必须成功。
            var free = BuildBranchGrid(moverCell, blockerCell, placeBlocker: false);
            ForcedDisplacementBatch freeBatch = Solve(
                Solver(free), free, Wide,
                Request(1L, GridDirection.East, 1),
                Request(2L, GridDirection.East, 1));
            Assert.That(freeBatch.Resolutions.Single(r => r.TargetUnitId.Value == 1L).AppliedSteps,
                Is.EqualTo(1), "全部分支成功时多格单位必须移动");
        }

        /// <summary>
        /// 缺陷指纹：三元位置循环若只做两两比较或按 UnitId 打破平局，会有人移动。
        /// </summary>
        [Test]
        public void ThreeUnitPositionCycleStopsAllParticipants()
        {
            var grid = new LogicGrid(Wide);
            // 1:(0,0) -East-> 2:(2,0)；2:(2,0) -SouthWest-> 3:(1,-1)；3:(1,-1) -NorthWest-> 1:(0,0)。
            Place(grid, 1L, 0, 0);
            Place(grid, 2L, 2, 0);
            Place(grid, 3L, 1, -1);
            ForcedDisplacementBatch batch = Solve(
                Solver(grid), grid, Wide,
                Request(1L, GridDirection.East, 1),
                Request(2L, GridDirection.SouthWest, 1),
                Request(3L, GridDirection.NorthWest, 1));

            Assert.That(new GridPoint(0, 0).Translate(GridNeighborTable.OffsetX(GridDirection.East),
                GridNeighborTable.OffsetY(GridDirection.East)), Is.EqualTo(new GridPoint(2, 0)),
                "夹具前提：1 → 2 的格");
            Assert.That(new GridPoint(2, 0).Translate(GridNeighborTable.OffsetX(GridDirection.SouthWest),
                GridNeighborTable.OffsetY(GridDirection.SouthWest)), Is.EqualTo(new GridPoint(1, -1)),
                "夹具前提：2 → 3 的格");
            Assert.That(new GridPoint(1, -1).Translate(GridNeighborTable.OffsetX(GridDirection.NorthWest),
                GridNeighborTable.OffsetY(GridDirection.NorthWest)), Is.EqualTo(new GridPoint(0, 0)),
                "夹具前提：3 → 1 的格");

            Assert.That(Of(batch, 1L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.DependencyCycle));
            Assert.That(Of(batch, 2L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.DependencyCycle));
            Assert.That(Of(batch, 3L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.DependencyCycle));
            Assert.That(batch.Relocations, Is.Empty);
        }

        // ================= 部分移动与后续阻挡 =================

        /// <summary>
        /// 缺陷指纹：若多格单位只判目标锚点合法性，身体的一角可以压进越界格。
        /// </summary>
        [Test]
        public void MultiCellUnitCannotMoveWhenAnyFootprintPointIsIllegal()
        {
            IReadOnlyList<GridPoint> bases = MultiCellBases();
            GridPoint second = bases.First(b => b != new GridPoint(0, 0));
            // 让 East 位移后的第二格越界：锚点 (4,4) 合法，第二格 (4+dx,4+dy) 越界。
            var origin = new GridPoint(4 - GridNeighborTable.OffsetX(GridDirection.East) - second.X,
                4 - GridNeighborTable.OffsetY(GridDirection.East) - second.Y);
            var boundary = new GridBoundaryDefinition(new GridPoint(0, 0), new GridPoint(4, 6));

            var grid = new LogicGrid(boundary);
            string error = grid.RegisterUnit(U(1L), origin, GridDirection.East, MultiCellDirections());
            Assert.That(error, Is.Null, "夹具前提：初始 footprint 必须合法 code=" + error);

            ForcedDisplacementBatch batch = Solve(
                Solver(grid), grid, boundary, Request(1L, GridDirection.East, 1));
            ForcedDisplacementResolution resolution = Of(batch, 1L);
            GridPoint targetAnchor = origin.Translate(GridNeighborTable.OffsetX(GridDirection.East),
                GridNeighborTable.OffsetY(GridDirection.East));
            Assert.That(boundary.Contains(targetAnchor), Is.True, "夹具前提：目标锚点本身合法");

            Assert.That(resolution.AppliedSteps, Is.EqualTo(0));
            Assert.That(resolution.StopReason, Is.EqualTo(ForcedDisplacementStopReason.Boundary));
        }

        /// <summary>
        /// 缺陷指纹：若求解器给阻挡者自动生成请求（隐式连锁推人），被撞单位会移动。
        /// </summary>
        [Test]
        public void ForcedDisplacementDoesNotImplicitlyPushUnitWithoutOwnRequest()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            Place(grid, 2L, 2, 0);
            ForcedDisplacementBatch batch = Solve(Solver(grid), grid, Wide, Request(1L, GridDirection.East, 3));

            Assert.That(Of(batch, 1L).AppliedSteps, Is.EqualTo(0));
            Assert.That(batch.Resolutions.Any(r => r.TargetUnitId.Value == 2L), Is.False,
                "没有请求的单位不得产生 Resolution");
            Assert.That(batch.Relocations.Any(r => r.UnitId.Value == 2L), Is.False);
        }

        /// <summary>
        /// 缺陷指纹：若失败时把步数清零（"全或无"），成功前缀会丢。
        /// </summary>
        [Test]
        public void ForcedDisplacementKeepsSuccessfulPrefixWhenLaterStepFails()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            Place(grid, 2L, 6, 0);
            ForcedDisplacementBatch batch = Solve(Solver(grid), grid, Wide, Request(1L, GridDirection.East, 3));

            Assert.That(Of(batch, 1L).AppliedSteps, Is.EqualTo(2));
            Assert.That(Of(batch, 1L).To, Is.EqualTo(new GridPoint(4, 0)));
            Assert.That(Of(batch, 1L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.OccupiedUnit));
        }

        /// <summary>
        /// 缺陷指纹：若"已走完"的单位在后续轮次继续参与提案，它会给后面让路而不是阻挡；
        /// 冻结规则是它成为后续轮次的静止占位（:166）。
        /// </summary>
        [Test]
        public void CompletedShorterDisplacementBecomesStationaryBlockerForLaterRounds()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 3L, 0, 0);
            Place(grid, 4L, 2, 0);
            ForcedDisplacementBatch batch = Solve(
                Solver(grid), grid, Wide,
                Request(3L, GridDirection.East, 3),
                Request(4L, GridDirection.East, 1));

            Assert.That(Of(batch, 4L).AppliedSteps, Is.EqualTo(1));
            Assert.That(Of(batch, 3L).AppliedSteps, Is.EqualTo(1), "第二轮起 4 号已是静止占位，挡住 3 号");
            Assert.That(Of(batch, 3L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.OccupiedUnit));
            Assert.That(Of(batch, 4L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.Completed));
        }

        // ================= Reservation、统计、唯一性 =================

        /// <summary>
        /// 缺陷指纹：若求解把 Reservation 当作物理墙，被预留的空格会被判 <c>OccupiedUnit</c>。
        /// </summary>
        [Test]
        public void ForcedDisplacementIgnoresReservationDuringSpatialSolve()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            Assert.That(grid.TryReserve(new Reservation(
                new ReservationKey(P(7L), 0), U(9L), new GridPoint(2, 0), 0L, 10L)), Is.Null);

            ForcedDisplacementBatch batch = Solve(Solver(grid), grid, Wide, Request(1L, GridDirection.East, 1));
            Assert.That(Of(batch, 1L).AppliedSteps, Is.EqualTo(1), "预留不是位移的物理墙");
            Assert.That(Of(batch, 1L).StopReason, Is.EqualTo(ForcedDisplacementStopReason.Completed));
        }

        /// <summary>
        /// 缺陷指纹：若求解阶段就清理预留或移动单位（中间格写世界），求解前后世界会不同。
        /// </summary>
        [Test]
        public void ResolveAllDoesNotMutateWorldBeforeCommit()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            Assert.That(grid.TryReserve(new Reservation(
                new ReservationKey(P(7L), 0), U(9L), new GridPoint(2, 0), 0L, 10L)), Is.Null);
            int reservationsBefore = grid.AllReservationsOrdered().Count;

            Solve(Solver(grid, null, null, new ForcedDisplacementStats()), grid, Wide,
                Request(1L, GridDirection.East, 2));

            Assert.That(grid.AllReservationsOrdered().Count, Is.EqualTo(reservationsBefore),
                "求解阶段不得清理任何 Reservation");
            Assert.That(AnchorOf(grid, 1L), Is.EqualTo(new GridPoint(0, 0)), "求解阶段不得移动单位");
        }

        /// <summary>
        /// 缺陷指纹：若统计不记录，"必须产出 22" 的性能面没有证据；若统计影响结果，
        /// 采样开关就会改变 golden。
        /// </summary>
        [Test]
        public void SolverRecordsPerformanceStatisticsWithoutAffectingResults()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            Place(grid, 2L, 2, 0);
            var stats = new ForcedDisplacementStats();
            ForcedDisplacementBatch batch = Solve(Solver(grid, null, null, stats), grid, Wide,
                Request(1L, GridDirection.East, 2), Request(2L, GridDirection.East, 2));

            Assert.That(stats.RequestCount, Is.EqualTo(2));
            Assert.That(stats.MaxRequestedSteps, Is.EqualTo(2));
            Assert.That(stats.RoundCount, Is.EqualTo(2));
            Assert.That(stats.DependencyNodeCount, Is.GreaterThan(0L));
            Assert.That(stats.DependencyEdgeCount, Is.GreaterThan(0L));
            Assert.That(stats.FootprintPointCheckCount, Is.GreaterThan(0L));
            Assert.That(stats.ResolutionCount, Is.EqualTo(2));
            Assert.That(stats.RelocationCount, Is.EqualTo(2));

            // 负控制：同样的输入，结果与统计无关（统计只读）。
            var plain = new LogicGrid(Wide);
            Place(plain, 1L, 0, 0);
            Place(plain, 2L, 2, 0);
            ForcedDisplacementBatch reference = Solve(Solver(plain), plain, Wide,
                Request(1L, GridDirection.East, 2), Request(2L, GridDirection.East, 2));
            Assert.That(Describe(batch), Is.EqualTo(Describe(reference)));
        }

        /// <summary>
        /// 缺陷指纹：若"每 Tick 每单位至多一个请求"只写在文档里，重复键会被静默按第一条处理。
        /// </summary>
        [Test]
        public void DuplicateForcedDisplacementRequestForUnitIsInvariantViolation()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            var solver = Solver(grid);

            var duplicate = new[]
            {
                Request(1L, GridDirection.East, 1, momentum: 10L, conflictGroupKey: 1L),
                Request(1L, GridDirection.West, 1, momentum: 999L, conflictGroupKey: 2L)
            };
            Assert.Throws<LogicDefinitionException>(() => Solve(solver, grid, Wide, duplicate));
            Assert.DoesNotThrow(() => Solve(solver, grid, Wide, Request(1L, GridDirection.East, 1)));
        }

        // ================= 排列不变性 =================

        /// <summary>
        /// 任务包 :358。构造<strong>两种真实不同的输入排列</strong>（正序 / 完全倒序），
        /// 逐项比较位置（From/To）、步数、停止原因与失效计划集合。
        ///
        /// 缺陷指纹：任何按输入顺序做决策的实现（先到先得、按输入序选赢家、依赖图用容器枚举序）
        /// 都会在两种排列下给出不同结果。
        /// </summary>
        [Test]
        public void ForcedDisplacementInputAndResolutionPermutationsProduceIdenticalResults()
        {
            ForcedDisplacementRequest[] canonical =
            {
                Request(1L, GridDirection.East, 2, momentum: 30L, conflictGroupKey: 5L),
                Request(2L, GridDirection.East, 2, momentum: 20L, conflictGroupKey: 5L),
                Request(3L, GridDirection.West, 1, momentum: 10L, conflictGroupKey: 4L)
            };

            string forward = Describe(RunPermutation(canonical));
            var reversed = new ForcedDisplacementRequest[canonical.Length];
            for (int i = 0; i < canonical.Length; i++) reversed[i] = canonical[canonical.Length - 1 - i];
            string backward = Describe(RunPermutation(reversed));

            // 夹具前提：结果必须非平凡（有换位、有失效计划），否则"排列不变"是空断言。
            Assert.That(forward, Does.Contain("->"), "夹具前提：必须有真实换位");
            Assert.That(forward, Does.Contain("[11]"), "夹具前提：必须有失效计划候选");
            Assert.That(backward, Is.EqualTo(forward),
                "输入排列改变了位置 / 停止原因 / 失效计划 / 批量换位");
        }

        private static ForcedDisplacementBatch RunPermutation(IReadOnlyList<ForcedDisplacementRequest> requests)
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);
            Place(grid, 2L, 2, 0);
            Place(grid, 3L, 10, 0);
            // 3 号向西一步落到 (8,0)：让"最终 footprint 相交 Reservation"也参与排列比较。
            Assert.That(grid.TryReserve(new Reservation(
                new ReservationKey(P(11L), 0), U(11L), new GridPoint(8, 0), 0L, 10L)), Is.Null);
            return Solve(Solver(grid), grid, Wide, requests.ToArray());
        }

        private static string Describe(ForcedDisplacementBatch batch)
        {
            var parts = new List<string>();
            for (int i = 0; i < batch.Resolutions.Count; i++)
            {
                ForcedDisplacementResolution resolution = batch.Resolutions[i];
                var ids = new List<string>();
                for (int p = 0; p < resolution.InvalidatedPlanIds.Count; p++)
                    ids.Add(resolution.InvalidatedPlanIds[p].Value.ToString());
                parts.Add(
                    resolution.TargetUnitId.Value + ":" + resolution.From + "->" + resolution.To +
                    "/applied=" + resolution.AppliedSteps + "/" + resolution.StopReason +
                    "/[" + string.Join(",", ids) + "]");
            }
            return string.Join("|", parts);
        }

        // ================= 多格夹具 =================

        private static IReadOnlyList<DirectionalTriangleSet> MultiCellDirections()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        /// <summary>用探针网格读出"2 格单位相对锚点的格偏移"，避免对几何做任何猜测。</summary>
        private static IReadOnlyList<GridPoint> MultiCellBases()
        {
            var probe = new LogicGrid(Wide);
            var origin = new GridPoint(0, 0);
            string error = probe.RegisterUnit(U(77L), origin, GridDirection.East, MultiCellDirections());
            Assert.That(error, Is.Null, "夹具前提：2 格单位必须可注册 code=" + error);
            IReadOnlyList<GridPoint> cells = probe.CellsOf(U(77L));
            var bases = new List<GridPoint>(cells.Count);
            for (int i = 0; i < cells.Count; i++)
            {
                bases.Add(new GridPoint(cells[i].X - origin.X, cells[i].Y - origin.Y));
            }
            return bases;
        }

        /// <summary>
        /// 构造"多格单位 1 号的 East 目标 footprint 一格给 2 号（会走）、一格给 3 号（静止）"。
        /// 1 号与 2 号都发请求；3 号按 <paramref name="placeBlocker"/> 决定是否放置。
        /// 2/3 号是<strong>单格</strong>单位，因此它们的注册不会与多格体积互相挤占。
        /// </summary>
        private static LogicGrid BuildBranchGrid(GridPoint moverCell, GridPoint blockerCell, bool placeBlocker)
        {
            var grid = new LogicGrid(Wide);
            Assert.That(grid.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East,
                MultiCellDirections()), Is.Null, "夹具前提：1 号必须可注册");
            Assert.That(grid.RegisterUnitWithPointFootprint(U(2L), moverCell, GridDirection.East), Is.Null,
                "夹具前提：2 号必须可注册于 " + moverCell);
            if (placeBlocker)
            {
                Assert.That(grid.RegisterUnitWithPointFootprint(U(3L), blockerCell, GridDirection.East), Is.Null,
                    "夹具前提：3 号必须可注册于 " + blockerCell);
            }
            return grid;
        }

        private static bool Contains(IReadOnlyList<GridPoint> cells, GridPoint point)
        {
            for (int i = 0; i < cells.Count; i++) if (cells[i] == point) return true;
            return false;
        }

        private static bool FootprintsIntersect(
            LogicGrid grid, UnitId a, GridPoint anchorA, GridDirection facingA,
            UnitId b, GridPoint anchorB, GridDirection facingB)
        {
            IReadOnlyList<GridPoint> cellsA = grid.ResolveDestinationCells(a, anchorA, facingA);
            IReadOnlyList<GridPoint> cellsB = grid.ResolveDestinationCells(b, anchorB, facingB);
            for (int i = 0; i < cellsA.Count; i++)
            {
                for (int j = 0; j < cellsB.Count; j++)
                {
                    if (cellsA[i] == cellsB[j]) return true;
                }
            }
            return false;
        }
    }
}
