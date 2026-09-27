using System;
using System.Collections.Generic;
using System.Reflection;
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
    /// 任务 06「必须产出」1/2/3/4/5/6/13：
    /// LogicGrid 占位与稳定区域查询、体积规范表消费、规范邻居序列、
    /// MovementSegment 离散提交、Reservation 冲突与回滚、空间门禁三分查询、
    /// <c>ApplyBatchRelocation</c> 全批预检与统一提交。
    /// </summary>
    public class Task06LogicGridTests
    {
        private static readonly GridBoundaryDefinition Wide =
            new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40));

        private static LogicGrid NewGrid() => new LogicGrid(Wide);

        /// <summary>任务 02B 形状的规范 12 向体积表（偶数基准 (3,0,↑)、奇数基准 (4,1,↓)）。</summary>
        private static IReadOnlyList<DirectionalTriangleSet> VolumeTable()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        private static readonly IReadOnlyList<DirectionalTriangleSet> Volume = VolumeTable();

        private static UnitId U(long id) => new UnitId(id);

        private static ActionPlanId P(long id) => new ActionPlanId(id);

        private static List<GridPoint> StraightEast(GridPoint from, int steps)
        {
            var path = new List<GridPoint> { from };
            GridPoint cursor = from;
            for (int i = 0; i < steps; i++)
            {
                cursor = cursor.Translate(2, 0);
                path.Add(cursor);
            }
            return path;
        }

        // ————————————————————————————————————————————————————————————
        // 占位与稳定区域查询
        // ————————————————————————————————————————————————————————————

        [Test]
        public void AreaQueryReturnsUnitsOrderedByUnitId()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnit(U(30L), new GridPoint(0, 0), GridDirection.East, Volume), Is.Null);
            Assert.That(grid.RegisterUnit(U(10L), new GridPoint(10, 10), GridDirection.East, Volume), Is.Null);
            Assert.That(grid.RegisterUnit(U(20L), new GridPoint(-10, -10), GridDirection.East, Volume), Is.Null);

            var area = new List<GridPoint>();
            area.AddRange(grid.CellsOf(U(30L)));
            area.AddRange(grid.CellsOf(U(10L)));
            area.AddRange(grid.CellsOf(U(20L)));

            IReadOnlyList<UnitId> units = grid.UnitsInCellsOrdered(area);
            Assert.That(units.Count, Is.EqualTo(3));
            Assert.That(units[0].Value, Is.EqualTo(10L), "区域查询必须按 UnitId 稳定升序");
            Assert.That(units[1].Value, Is.EqualTo(20L));
            Assert.That(units[2].Value, Is.EqualTo(30L));

            // 传入顺序被打乱也必须得到同一结果。
            area.Reverse();
            IReadOnlyList<UnitId> again = grid.UnitsInCellsOrdered(area);
            Assert.That(again[0].Value, Is.EqualTo(10L));
            Assert.That(again[2].Value, Is.EqualTo(30L));

            // ignore 只排除指定单位。
            IReadOnlyList<UnitId> without = grid.UnitsInCellsOrdered(area, U(20L));
            Assert.That(without.Count, Is.EqualTo(2));
            Assert.That(without[0].Value, Is.EqualTo(10L));
        }

        [Test]
        public void LogicGridUsesCanonicalVolumeTableForEveryDirection()
        {
            LogicGrid grid = NewGrid();
            for (int i = 0; i < GridDirectionInfo.DirectionCount; i++)
            {
                var facing = (GridDirection)i;
                var id = U(100L + i);
                var origin = new GridPoint(((i % 4) * 16) - 24, ((i / 4) * 16) - 16);
                Assert.That(grid.RegisterUnit(id, origin, facing, Volume), Is.Null, facing.ToString());

                IReadOnlyList<TrianglePoint> expected = Volume[i].GetTranslated(origin);
                Assert.That(grid.TrianglesOf(id), Is.EqualTo(expected),
                    facing + " 必须消费规范表第 " + i + " 项");

                // 朝向不同 ⇒ footprint 不同（不是"忽略朝向"）。
                if (i > 0)
                {
                    Assert.That(grid.CellsOf(id), Is.Not.EqualTo(grid.CellsOf(U(100L))),
                        facing + " 的 footprint 不得与 East 相同");
                }
            }
        }

        [Test]
        public void VolumeOccupancyOnlyIndexesAndTranslatesIntegerPoints()
        {
            LogicGrid grid = NewGrid();
            var origin = new GridPoint(4, 8);
            Assert.That(grid.RegisterUnit(U(1L), origin, GridDirection.NorthWest, Volume), Is.Null);

            IReadOnlyList<GridPoint> cells = grid.CellsOf(U(1L));

            // 与"零锚点 + 整数平移"完全一致：不存在旋转或舍入步骤。
            var expected = new List<GridPoint>();
            foreach (TrianglePoint p in Volume[(int)GridDirection.NorthWest].Triangles)
            {
                expected.Add(new GridPoint(p.X - 1 + origin.X, p.Y + origin.Y));
                expected.Add(new GridPoint(p.X + 1 + origin.X, p.Y + origin.Y));
                expected.Add(new GridPoint(p.X + origin.X, p.Y + p.T + origin.Y));
            }
            expected.Sort();
            var dedup = new List<GridPoint>();
            foreach (GridPoint p in expected)
            {
                if (dedup.Count == 0 || !dedup[dedup.Count - 1].Equals(p)) dedup.Add(p);
            }

            Assert.That(cells, Is.EqualTo(dedup));

            // 平移不变性：同一朝向、不同锚点的相对点集必须逐点相同。
            LogicGrid other = NewGrid();
            var otherOrigin = new GridPoint(-20, 6);
            Assert.That(other.RegisterUnit(U(1L), otherOrigin, GridDirection.NorthWest, Volume), Is.Null);
            IReadOnlyList<GridPoint> otherCells = other.CellsOf(U(1L));
            Assert.That(otherCells.Count, Is.EqualTo(cells.Count));
            for (int i = 0; i < cells.Count; i++)
            {
                Assert.That(otherCells[i].X - otherOrigin.X, Is.EqualTo(cells[i].X - origin.X));
                Assert.That(otherCells[i].Y - otherOrigin.Y, Is.EqualTo(cells[i].Y - origin.Y));
            }

            // 占位索引确实建立在同一集合上。
            for (int i = 0; i < cells.Count; i++)
            {
                Assert.That(grid.TryGetCellOwner(cells[i], out UnitId owner), Is.True);
                Assert.That(owner.Value, Is.EqualTo(1L));
            }
            Assert.That(grid.TryGetCellOwner(new GridPoint(38, 38), out _), Is.False);
        }

        [Test]
        public void RuntimeGridHasNoLegacyFloatRotationDependency()
        {
            // 1. 程序集级：Logic 不引用任何 UnityEngine 模块。
            foreach (AssemblyName reference in typeof(LogicGrid).Assembly.GetReferencedAssemblies())
            {
                Assert.That(reference.Name, Does.Not.StartWith("UnityEngine"),
                    "ProjectHero.Logic 不得引用 UnityEngine：" + reference.Name);
            }

            // 2. 类型级：LogicGrid / VolumeFootprint / LogicPathfinder / LogicGridMovementAuthority
            //    的成员签名与字段里不得出现 UnityEngine 命名空间（含 Mathf/Vector3/Transform）。
            AssertNoUnityTypes(typeof(LogicGrid));
            AssertNoUnityTypes(typeof(VolumeFootprint));
            AssertNoUnityTypes(typeof(LogicPathfinder));
            AssertNoUnityTypes(typeof(LogicGridMovementAuthority));
            AssertNoUnityTypes(typeof(MovementSegment));
            AssertNoUnityTypes(typeof(Reservation));

            // 3. 运行时空间消费面里没有任何以"旋转"命名的入口。
            foreach (Type type in new[] { typeof(LogicGrid), typeof(VolumeFootprint) })
            {
                foreach (MethodInfo method in type.GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                             BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    Assert.That(method.Name.IndexOf("Rotate", StringComparison.Ordinal), Is.LessThan(0),
                        type.Name + " 不得提供旋转入口：" + method.Name);
                }
            }

            // 4. 唯一的角度旋转原语只存在于定义构建边界（DirectionalGeometry），
            //    且它不是 LogicGrid 的依赖：LogicGrid 的构造函数只接受边界定义。
            Assert.That(typeof(DirectionalGeometry).GetMethod("Rotate60CounterClockwise"), Is.Not.Null);
            foreach (ConstructorInfo ctor in typeof(LogicGrid).GetConstructors())
            {
                foreach (ParameterInfo parameter in ctor.GetParameters())
                {
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(DirectionalGeometry)));
                }
            }
        }

        private static void AssertNoUnityTypes(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (FieldInfo field in type.GetFields(flags)) AssertNotUnity(field.FieldType, type.Name + "." + field.Name);
            foreach (PropertyInfo property in type.GetProperties(flags)) AssertNotUnity(property.PropertyType, type.Name + "." + property.Name);
            foreach (MethodInfo method in type.GetMethods(flags))
            {
                AssertNotUnity(method.ReturnType, type.Name + "." + method.Name);
                foreach (ParameterInfo parameter in method.GetParameters())
                    AssertNotUnity(parameter.ParameterType, type.Name + "." + method.Name + "(" + parameter.Name + ")");
            }
        }

        private static void AssertNotUnity(Type type, string context)
        {
            if (type == null) return;
            if (type.IsGenericType)
            {
                foreach (Type argument in type.GetGenericArguments()) AssertNotUnity(argument, context);
                return;
            }
            if (type.IsArray)
            {
                AssertNotUnity(type.GetElementType(), context);
                return;
            }
            string ns = type.Namespace ?? string.Empty;
            Assert.That(ns, Does.Not.StartWith("UnityEngine"), context + " 使用了 " + type.FullName);
            Assert.That(type.Name, Is.Not.EqualTo("Mathf"), context);
            Assert.That(type.Name, Is.Not.EqualTo("Vector3"), context);
        }

        [Test]
        public void FactionRelationDoesNotImplicitlyDisableOccupancyOrReservation()
        {
            // LogicGrid 的空间 API 完全不接收阵营/控制者/玩家标志：占位与 Reservation
            // 不是"关系矩阵开关"。这是结构事实，不是约定。
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.DeclaredOnly;
            foreach (MethodInfo method in typeof(LogicGrid).GetMethods(flags))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    string name = parameter.Name ?? string.Empty;
                    Assert.That(name.IndexOf("faction", StringComparison.OrdinalIgnoreCase), Is.LessThan(0),
                        "占位/预留查询不得以阵营为开关：" + method.Name);
                    Assert.That(name.IndexOf("controller", StringComparison.OrdinalIgnoreCase), Is.LessThan(0),
                        method.Name);
                    Assert.That(name.IndexOf("relation", StringComparison.OrdinalIgnoreCase), Is.LessThan(0),
                        method.Name);
                }
            }

            // 两个单位无论"关系"如何都互相阻挡。
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East, Volume), Is.Null);
            IReadOnlyList<GridPoint> occupied = grid.CellsOf(U(1L));
            for (int i = 0; i < occupied.Count; i++)
                Assert.That(grid.IsCellBlockedFor(occupied[i], U(2L)), Is.True,
                    "其他单位在任何关系下都必须被占位阻挡");

            // 注册在相同 footprint 上必须整体拒绝。
            Assert.That(grid.RegisterUnit(U(2L), new GridPoint(0, 0), GridDirection.East, Volume),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER));
        }

        // ————————————————————————————————————————————————————————————
        // MovementSegment 离散提交
        // ————————————————————————————————————————————————————————————

        private static LogicGridMovementAuthority NewAuthority(LogicGrid grid)
            => new LogicGridMovementAuthority(
                grid, new ActionScheduleAuthority(), PathCostRules.FrozenV1, PathSearchRules.FrozenV1);

        [Test]
        public void MovingUnitOccupiesFromUntilEndTick()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            List<GridPoint> path = StraightEast(new GridPoint(0, 0), 1);
            MovementReplacementResult result = authority.EstablishMovement(P(1L), U(1L), path, 100L, 5);
            Assert.That(result.Succeeded, Is.True, result.FailureCode);

            MovementSegment segment = result.Segments[0];
            Assert.That(segment.StepWeightUnits, Is.EqualTo(1), "East 是偶数方向 ⇒ 权重 1");
            Assert.That(segment.StartTick, Is.EqualTo(100L));
            Assert.That(segment.EndTick, Is.EqualTo(105L), "1 × 基准 5 = 5");

            // 在飞行中：单位仍占 From，To 尚未提交。
            authority.ApplyPreCommandBoundary(100L);
            authority.ApplyPreCommandBoundary(104L);
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(0, 0)), "StartTick <= tick < EndTick 期间仍占 From");
            Assert.That(grid.TryGetCellOwner(new GridPoint(2, 0), out _), Is.False, "To 尚未被占位提交");
        }

        [Test]
        public void MovementCommitsToDestinationAtEndTickBeforeCommands()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            MovementReplacementResult result = authority.EstablishMovement(
                P(1L), U(1L), StraightEast(new GridPoint(0, 0), 1), 100L, 5);
            Assert.That(result.Succeeded, Is.True, result.FailureCode);
            Assert.That(grid.ReservationAt(new GridPoint(2, 0)), Is.Not.Null, "To 必须保持 Reservation");

            IReadOnlyList<MovementSegment> committed = authority.ApplyPreCommandBoundary(105L);
            Assert.That(committed.Count, Is.EqualTo(1));
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(2, 0)), "EndTick 的命令前边界原子提交到 To");
            Assert.That(grid.ReservationAt(new GridPoint(2, 0)), Is.Null, "同一提交释放该 Reservation");
            Assert.That(authority.AllSegmentsOrdered().Count, Is.EqualTo(0));
            Assert.That(authority.AuditOfPlan(P(1L)).Count, Is.EqualTo(1), "已完成段进入只读审计");
            Assert.That(authority.AuditOfPlan(P(1L))[0].CommittedAtTick, Is.EqualTo(105L));
            Assert.That(authority.TryGetFinalLogicalPosition(U(1L), out GridPoint final), Is.True);
            Assert.That(final, Is.EqualTo(new GridPoint(2, 0)));
        }

        [Test]
        public void DestinationRemainsReservedDuringMovementSegment()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);
            Assert.That(authority.EstablishMovement(P(1L), U(1L), StraightEast(new GridPoint(0, 0), 2), 10L, 5).Succeeded,
                Is.True);

            Reservation first = grid.ReservationAt(new GridPoint(2, 0));
            Reservation second = grid.ReservationAt(new GridPoint(4, 0));
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(first.StartTick, Is.EqualTo(10L));
            Assert.That(first.EndTick, Is.EqualTo(15L));
            Assert.That(second.StartTick, Is.EqualTo(15L));
            Assert.That(second.EndTick, Is.EqualTo(20L));

            // 另一个计划不能在这段时间里抢占 To。
            Assert.That(grid.TryReserve(new Reservation(
                    new ReservationKey(P(2L), 0), U(2L), new GridPoint(2, 0), 10L, 15L)),
                Does.StartWith(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER));
        }

        [Test]
        public void MultiStepPathUsesIndependentSegments()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            // 两步：East（权重 1）+ North（权重 2）⇒ 时长 5 与 10。
            var path = new List<GridPoint> { new GridPoint(0, 0), new GridPoint(2, 0), new GridPoint(2, 2) };
            MovementReplacementResult result = authority.EstablishMovement(P(7L), U(1L), path, 0L, 5);
            Assert.That(result.Succeeded, Is.True, result.FailureCode);
            Assert.That(result.Segments.Count, Is.EqualTo(2), "每步一个独立段");

            Assert.That(result.Segments[0].StepIndex, Is.EqualTo(0));
            Assert.That(result.Segments[0].Direction, Is.EqualTo(GridDirection.East));
            Assert.That(result.Segments[0].StepWeightUnits, Is.EqualTo(1));
            Assert.That(result.Segments[0].StartTick, Is.EqualTo(0L));
            Assert.That(result.Segments[0].EndTick, Is.EqualTo(5L));

            Assert.That(result.Segments[1].StepIndex, Is.EqualTo(1));
            Assert.That(result.Segments[1].Direction, Is.EqualTo(GridDirection.North));
            Assert.That(result.Segments[1].StepWeightUnits, Is.EqualTo(2));
            Assert.That(result.Segments[1].StartTick, Is.EqualTo(5L));
            Assert.That(result.Segments[1].EndTick, Is.EqualTo(15L));

            Assert.That(authority.VerifyInvariants(), Is.Null);
        }

        [Test]
        public void WeightTwoSegmentTakesTwiceWeightOneSegmentTicks()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            MovementReplacementResult east = authority.EstablishMovement(
                P(1L), U(1L), StraightEast(new GridPoint(0, 0), 1), 0L, 7);
            Assert.That(east.Succeeded, Is.True, east.FailureCode);

            grid.UnregisterUnit(U(1L));
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            MovementReplacementResult north = authority.EstablishMovement(
                P(2L), U(1L), new List<GridPoint> { new GridPoint(0, 0), new GridPoint(0, 2) }, 0L, 7);
            Assert.That(north.Succeeded, Is.True, north.FailureCode);

            Assert.That(north.Segments[0].DurationTicks, Is.EqualTo(2 * east.Segments[0].DurationTicks),
                "权重 2 的边必须恰好是权重 1 边时长的两倍");
        }

        [Test]
        public void MoveSegmentsUsePlanResolvedBaseStepTicksWithoutRecalculation()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            // 同一路径、不同"已量化基准"⇒ 只有时长变化，权重与边数不变。
            MovementReplacementResult slow = authority.EstablishMovement(
                P(1L), U(1L), StraightEast(new GridPoint(0, 0), 2), 0L, 30);
            Assert.That(slow.Succeeded, Is.True, slow.FailureCode);

            MovementReplacementResult fast = authority.EstablishMovement(
                P(1L), U(1L), StraightEast(new GridPoint(0, 0), 2), 0L, 15);
            Assert.That(fast.Succeeded, Is.True, fast.FailureCode);

            Assert.That(fast.Segments.Count, Is.EqualTo(slow.Segments.Count));
            Assert.That(fast.Segments[0].StepWeightUnits, Is.EqualTo(slow.Segments[0].StepWeightUnits));
            Assert.That(fast.Segments[1].StepWeightUnits, Is.EqualTo(slow.Segments[1].StepWeightUnits));
            Assert.That(fast.Segments[1].EndTick, Is.EqualTo(slow.Segments[1].EndTick / 2),
                "MoveSpeed 只改变每权重单位的 Tick 数");
        }

        [Test]
        public void MoveEndTickAndBudgetUsePathWeightNotEdgeCount()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            // 路径：East(1) + North(2) = 3 权重、2 条边。总时长必须是 3 × 基准，不是 2 × 基准。
            var path = new List<GridPoint> { new GridPoint(0, 0), new GridPoint(2, 0), new GridPoint(2, 2) };
            MovementReplacementResult result = authority.EstablishMovement(P(1L), U(1L), path, 0L, 10);
            Assert.That(result.Succeeded, Is.True, result.FailureCode);

            int weightSum = 0;
            for (int i = 0; i < result.Segments.Count; i++) weightSum += result.Segments[i].StepWeightUnits;

            Assert.That(result.Segments.Count, Is.EqualTo(2), "段数 = 边数");
            Assert.That(weightSum, Is.EqualTo(3), "权重和 = 路径权重");
            Assert.That(result.Segments[result.Segments.Count - 1].EndTick, Is.EqualTo(30L),
                "总时长 = 路径权重 × 基准");
            Assert.That(result.Segments[result.Segments.Count - 1].EndTick,
                Is.Not.EqualTo(2 * 10L), "不得用边数计费");
        }

        [Test]
        public void FailedPathReservationRollsBackAllSegments()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            Assert.That(grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(10, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            // 先建立一个 2 段路径：(0,0)→(2,0)→(4,0)。
            var initial = new List<GridPoint> { new GridPoint(0, 0), new GridPoint(2, 0), new GridPoint(4, 0) };
            MovementReplacementResult ok = authority.EstablishMovement(P(1L), U(1L), initial, 0L, 5);
            Assert.That(ok.Succeeded, Is.True, ok.FailureCode);
            Assert.That(authority.SegmentsOfPlanOrdered(P(1L)).Count, Is.EqualTo(2));

            // 另一个计划抢占**本次新路径需要**的格 (6,0)（旧路径不含该格）。
            Assert.That(grid.TryReserve(new Reservation(
                new ReservationKey(P(9L), 0), U(2L), new GridPoint(6, 0), 0L, 100L)), Is.Null);

            var path = new List<GridPoint>
            {
                new GridPoint(0, 0), new GridPoint(2, 0), new GridPoint(4, 0), new GridPoint(6, 0)
            };

            // 整批替换失败 ⇒ 旧段与旧预留必须原样保留（零局部写入）。
            MovementReplacementResult failed = authority.EstablishMovement(P(1L), U(1L), path, 0L, 5);
            Assert.That(failed.Succeeded, Is.False);
            Assert.That(failed.FailureCode, Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER));
            Assert.That(authority.SegmentsOfPlanOrdered(P(1L)).Count, Is.EqualTo(2), "旧段必须完整保留");
            Assert.That(grid.ReservationsOfPlanOrdered(P(1L)).Count, Is.EqualTo(2), "旧预留必须完整保留");
            Assert.That(grid.ReservationAt(new GridPoint(2, 0)), Is.Not.Null);
            Assert.That(grid.ReservationAt(new GridPoint(4, 0)), Is.Not.Null);
            Assert.That(grid.ReservationAt(new GridPoint(6, 0)).ActionPlanId.Value, Is.EqualTo(9L),
                "外来预留的持有者不得被改写");
            Assert.That(authority.VerifyInvariants(), Is.Null);
        }

        [Test]
        public void LaterCommandCannotPreemptCommittedReservation()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            Assert.That(grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(0, 4), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            Assert.That(authority.EstablishMovement(
                P(1L), U(1L), new List<GridPoint> { new GridPoint(0, 0), new GridPoint(2, 0) }, 0L, 5).Succeeded,
                Is.True);

            // 后到的计划想占同一格 ⇒ 稳定拒绝，且已有预留毫发无损。
            MovementReplacementResult later = authority.EstablishMovement(
                P(2L), U(2L), new List<GridPoint> { new GridPoint(0, 4), new GridPoint(2, 4) }, 0L, 5);
            Assert.That(later.Succeeded, Is.True, "不同格的计划不受影响");

            Assert.That(grid.TryReserve(new Reservation(
                    new ReservationKey(P(3L), 0), U(2L), new GridPoint(2, 0), 0L, 5L)),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER), "后到者不能抢占已提交的 Reservation");
            Assert.That(grid.ReservationAt(new GridPoint(2, 0)).ActionPlanId.Value, Is.EqualTo(1L),
                "持有者不变");
        }

        [Test]
        public void ReservationConflictIgnoresRawBatchEnumerationOrder()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            Assert.That(grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(0, 4), GridDirection.East), Is.Null);

            // 两条请求以同一目标格竞争：无论哪个先被枚举，胜者都是同一个（先提交者）。
            var a = new Reservation(new ReservationKey(P(1L), 0), U(1L), new GridPoint(2, 0), 0L, 5L);
            var b = new Reservation(new ReservationKey(P(2L), 0), U(2L), new GridPoint(2, 0), 0L, 5L);

            LogicGrid first = NewGrid();
            first.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East);
            first.RegisterUnitWithPointFootprint(U(2L), new GridPoint(0, 4), GridDirection.East);
            Assert.That(first.TryReserve(a), Is.Null);
            string loserA = first.TryReserve(b);

            LogicGrid second = NewGrid();
            second.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East);
            second.RegisterUnitWithPointFootprint(U(2L), new GridPoint(0, 4), GridDirection.East);
            Assert.That(second.TryReserve(b), Is.Null);
            string loserB = second.TryReserve(a);

            Assert.That(loserA, Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER));
            Assert.That(loserB, Is.EqualTo(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER));
            Assert.That(first.ReservationAt(new GridPoint(2, 0)).ActionPlanId.Value, Is.EqualTo(1L));
            Assert.That(second.ReservationAt(new GridPoint(2, 0)).ActionPlanId.Value, Is.EqualTo(2L));
        }

        [Test]
        public void PlanTerminalReleasesAllOwnedReservations()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            var path = new List<GridPoint>
            {
                new GridPoint(0, 0), new GridPoint(2, 0), new GridPoint(4, 0), new GridPoint(6, 0)
            };
            Assert.That(authority.EstablishMovement(P(1L), U(1L), path, 0L, 5).Succeeded, Is.True);
            authority.ApplyPreCommandBoundary(5L);   // 第一段已提交

            Assert.That(authority.SegmentsOfPlanOrdered(P(1L)).Count, Is.EqualTo(2), "活动/未来段");
            Assert.That(grid.ReservationsOfPlanOrdered(P(1L)).Count, Is.EqualTo(2));

            IReadOnlyList<Reservation> released = authority.ReleasePlanMovement(P(1L));
            Assert.That(released.Count, Is.EqualTo(2), "按稳定空间键释放全部自有 Reservation");
            Assert.That(grid.ReservationsOfPlanOrdered(P(1L)).Count, Is.EqualTo(0));
            Assert.That(authority.SegmentsOfPlanOrdered(P(1L)).Count, Is.EqualTo(0));
            Assert.That(authority.AuditOfPlan(P(1L)).Count, Is.EqualTo(1), "已完成段保留在只读审计里");
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(2, 0)), "保留最后一次已提交的逻辑格");
        }

        [Test]
        public void TerminalPlanNeverCommitsDestinationAtFormerEndTick()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            var path = new List<GridPoint> { new GridPoint(0, 0), new GridPoint(2, 0), new GridPoint(4, 0) };
            Assert.That(authority.EstablishMovement(P(1L), U(1L), path, 0L, 10).Succeeded, Is.True);
            authority.ApplyPreCommandBoundary(0L);

            // 计划在当前段 EndTick 之前进入终态。
            authority.ReleasePlanMovement(P(1L));
            grid.SetPendingDestination(U(1L), null);

            // 原 EndTick 到点后再走边界：不得再提交到 To。
            authority.ApplyPreCommandBoundary(10L);
            authority.ApplyPreCommandBoundary(20L);
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(0, 0)), "终态后停在最后已提交的逻辑格");
            Assert.That(grid.TryGetCellOwner(new GridPoint(2, 0), out _), Is.False, "原 EndTick 不得再提交");
            Assert.That(authority.VerifyInvariants(), Is.Null);
        }

        [Test]
        public void VisualInterpolationDoesNotAffectInterruptedMovementResult()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            var path = new List<GridPoint> { new GridPoint(0, 0), new GridPoint(2, 0), new GridPoint(4, 0) };
            Assert.That(authority.EstablishMovement(P(1L), U(1L), path, 0L, 10).Succeeded, Is.True);

            // 边界只在 Stage 0 按 Tick 推进；"视觉插值进度"根本不是逻辑输入。
            // 这里用同一 Tick 的两次边界调用证明结果只由 Tick 决定（幂等）。
            IReadOnlyList<MovementSegment> first = authority.ApplyPreCommandBoundary(9L);
            IReadOnlyList<MovementSegment> second = authority.ApplyPreCommandBoundary(9L);
            Assert.That(first.Count, Is.EqualTo(0));
            Assert.That(second.Count, Is.EqualTo(0));
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(0, 0)), "未到 EndTick 时位置不得提前推进");

            authority.ApplyPreCommandBoundary(10L);
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint after), Is.True);
            Assert.That(after, Is.EqualTo(new GridPoint(2, 0)), "只有 Tick 到达 EndTick 才提交");
        }

        [Test]
        public void RepeatedPlanTerminalCleanupIsIdempotent()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);
            Assert.That(authority.EstablishMovement(
                P(1L), U(1L), new List<GridPoint> { new GridPoint(0, 0), new GridPoint(2, 0) }, 0L, 5).Succeeded,
                Is.True);

            IReadOnlyList<Reservation> first = authority.ReleasePlanMovement(P(1L));
            IReadOnlyList<Reservation> second = authority.ReleasePlanMovement(P(1L));
            Assert.That(first.Count, Is.EqualTo(1));
            Assert.That(second.Count, Is.EqualTo(0), "重复清理必须是安全无操作");
            Assert.That(authority.VerifyInvariants(), Is.Null);
        }

        // ————————————————————————————————————————————————————————————
        // 空间门禁三分查询
        // ————————————————————————————————————————————————————————————

        [Test]
        public void TimedReservationBlockerExposesFiniteReleaseTick()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            Assert.That(grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(0, 4), GridDirection.East), Is.Null);

            // (2,0) 上有一条到 Tick 50 结束的他人 Reservation。
            Assert.That(grid.TryReserve(new Reservation(
                new ReservationKey(P(9L), 0), U(2L), new GridPoint(2, 0), 10L, 50L)), Is.Null);

            SpaceBlockQueryResult result = grid.QueryStartBlock(U(1L), new GridPoint(2, 0), 20L);
            Assert.That(result.Kind, Is.EqualTo(SpaceBlockKind.RetryableTimedBlock));
            Assert.That(result.ReleaseTick, Is.EqualTo(50L), "权威 Reservation 区间的有限释放边界");
            Assert.That(result.ReleaseTick, Is.GreaterThan(20L), "必须严格晚于当前 Tick");

            // 到点之后不再构成**可重试**阻塞：EndTick 本身不是"严格晚于当前 Tick"，
            // 因此绝不返回 Retryable（门禁不得据此排一个等于自己的 RetryAtTick）。
            Assert.That(grid.QueryStartBlock(U(1L), new GridPoint(2, 0), 50L).IsRetryable, Is.False);
            Assert.That(grid.QueryStartBlock(U(1L), new GridPoint(2, 0), 49L).IsRetryable, Is.True);
            Assert.That(grid.QueryStartBlock(U(1L), new GridPoint(2, 0), 49L).ReleaseTick, Is.EqualTo(50L));

            // 已经越过结束边界的 Reservation 记录是内部矛盾，不得伪造成"下一 Tick 再试"。
            Assert.That(grid.QueryStartBlock(U(1L), new GridPoint(2, 0), 60L).Kind,
                Is.EqualTo(SpaceBlockKind.TerminalOrUnknownBlock));
        }

        [Test]
        public void StaticOrUnknownSpatialBlockerIsNotRetryable()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            // 静止占位（没有活动移动段 ⇒ 无有限释放边界）。
            Assert.That(grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(2, 0), GridDirection.East), Is.Null);

            SpaceBlockQueryResult stationary = grid.QueryStartBlock(U(1L), new GridPoint(2, 0), 10L);
            Assert.That(stationary.Kind, Is.EqualTo(SpaceBlockKind.TerminalOrUnknownBlock),
                "无结束边界的占位不得被伪造成可重试");

            // 静态非法格（越界）同样不是可重试阻塞。
            SpaceBlockQueryResult outside = grid.QueryStartBlock(U(1L), new GridPoint(0, 200), 10L);
            Assert.That(outside.Kind, Is.EqualTo(SpaceBlockKind.TerminalOrUnknownBlock));

            // 冻结的 bool? 适配器：Free ⇒ null，TerminalOrUnknown ⇒ long.MaxValue
            // （门禁据此返回 Terminal 而不是 tick + 1）。
            Func<UnitId, long?> adapter = grid.BuildSpaceBlockUntilTickOf(10L, _ => null);
            grid.SetPendingDestination(U(1L), new GridPoint(2, 0));
            Assert.That(adapter(U(1L)), Is.EqualTo(long.MaxValue));
            grid.SetPendingDestination(U(1L), new GridPoint(-30, -30));
            Assert.That(adapter(U(1L)), Is.Null, "无阻塞时必须返回 null（不是 0 或 tick+1）");
            Assert.That(adapter(U(99L)), Is.Null, "未登记目的地的单位不产生空间阻塞");
        }

        [Test]
        public void MovingOccupancyReleasesCellAtFiniteEndTick()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            Assert.That(grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(2, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            // U2 正在离开 (2,0)：给出有限释放边界。
            Assert.That(authority.EstablishMovement(
                P(2L), U(2L), new List<GridPoint> { new GridPoint(2, 0), new GridPoint(4, 0) }, 5L, 10).Succeeded,
                Is.True);

            SpaceBlockQueryResult result = grid.QueryStartBlock(
                U(1L), new GridPoint(2, 0), 6L, unit => authority.ActiveReleaseTickOf(unit, 6L));
            Assert.That(result.Kind, Is.EqualTo(SpaceBlockKind.RetryableTimedBlock));
            Assert.That(result.ReleaseTick, Is.EqualTo(15L), "活动移动段在 EndTick 释放它当前占据的格");
        }

        // ————————————————————————————————————————————————————————————
        // BatchRelocation
        // ————————————————————————————————————————————————————————————

        [Test]
        public void BatchRelocationValidatesAllSourcesBeforeMutation()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            Assert.That(grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(10, 0), GridDirection.East), Is.Null);

            // 第二个单位的 ExpectedFrom 与真实锚点不符 ⇒ 整体拒绝、零写入。
            string error = grid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(U(1L), new GridPoint(0, 0), new GridPoint(4, 0)),
                new BatchRelocation(U(2L), new GridPoint(99, 99), new GridPoint(14, 0))
            });

            Assert.That(error, Is.EqualTo(LogicGridCodes.LOGIC_GRID_BATCH_EXPECTED_FROM_MISMATCH));
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint a), Is.True);
            Assert.That(a, Is.EqualTo(new GridPoint(0, 0)), "第一个单位不得被部分提交");

            // 重复 UnitId 只用于诊断，不用于选赢家。
            Assert.That(grid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(U(1L), new GridPoint(0, 0), new GridPoint(4, 0)),
                new BatchRelocation(U(1L), new GridPoint(0, 0), new GridPoint(6, 0))
            }), Is.EqualTo(LogicGridCodes.LOGIC_GRID_BATCH_DUPLICATE_UNIT));
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint b), Is.True);
            Assert.That(b, Is.EqualTo(new GridPoint(0, 0)));
        }

        [Test]
        public void BatchRelocationValidatesWholeMultiCellFootprint()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East, Volume), Is.Null);
            IReadOnlyList<GridPoint> source = grid.CellsOf(U(1L));
            Assert.That(source.Count, Is.GreaterThan(1), "体积表必须给出多格 footprint");

            // 锚点在界内、但完整 footprint 越界 ⇒ 拒绝。
            string error = grid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(U(1L), new GridPoint(0, 0), new GridPoint(40, 40))
            });
            Assert.That(error, Is.EqualTo(LogicGridCodes.LOGIC_GRID_OUT_OF_BOUNDS));
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(0, 0)), "零写入");

            // 锚点与完整 footprint 都在界内 ⇒ 成功，且索引严格跟随新 footprint。
            string ok = grid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(U(1L), new GridPoint(0, 0), new GridPoint(6, 6))
            });
            Assert.That(ok, Is.Null, ok);
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint moved), Is.True);
            Assert.That(moved, Is.EqualTo(new GridPoint(6, 6)));
            Assert.That(grid.VerifyConsistency(), Is.Null);
            IReadOnlyList<GridPoint> after = grid.CellsOf(U(1L));
            for (int i = 0; i < after.Count; i++)
            {
                Assert.That(grid.TryGetCellOwner(after[i], out UnitId owner), Is.True);
                Assert.That(owner.Value, Is.EqualTo(1L));
            }
            for (int i = 0; i < source.Count; i++)
            {
                Assert.That(grid.TryGetCellOwner(source[i], out _), Is.False, "旧 footprint 必须已统一移除");
            }
        }

        [Test]
        public void BatchRelocationRemovesAllOldFootprintsBeforeAddingNewFootprints()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East, Volume), Is.Null);
            Assert.That(grid.RegisterUnit(U(2L), new GridPoint(20, 0), GridDirection.East, Volume), Is.Null);

            // U1 的目标锚点落在 U2 当前的旧 footprint 内（(22,0) 等都是 U2 现在的占位格），
            // 同时 U2 自己也移走。若按"逐单位先移除再加"就会与静止单位相交而失败；
            // 只有"统一移除全部旧 footprint 再统一写入"才可能成功。
            IReadOnlyList<GridPoint> cells2 = grid.CellsOf(U(2L));
            var target1 = new GridPoint(cells2[0].X, cells2[0].Y);
            Assert.That(GridPoint.IsValidParity(target1.X, target1.Y), Is.True);
            Assert.That(grid.IsCellBlockedFor(target1, U(1L)), Is.True, "该格当前确实被 U2 占用");

            string error = grid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(U(1L), new GridPoint(0, 0), target1),
                new BatchRelocation(U(2L), new GridPoint(20, 0), new GridPoint(30, 0))
            });
            Assert.That(error, Is.Null, error);
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint moved1), Is.True);
            Assert.That(grid.TryGetAnchor(U(2L), out GridPoint moved2), Is.True);
            Assert.That(moved1, Is.EqualTo(target1));
            Assert.That(moved2, Is.EqualTo(new GridPoint(30, 0)));
            Assert.That(grid.VerifyConsistency(), Is.Null);

            // 旧 footprint 必须已全部移除：U2 原来独占的格变成空闲，
            // 被 U1 新 footprint 覆盖的格则已改由 U1 持有。
            IReadOnlyList<GridPoint> moved1Cells = grid.CellsOf(U(1L));
            for (int i = 0; i < cells2.Count; i++)
            {
                bool coveredByNew = false;
                for (int c = 0; c < moved1Cells.Count; c++)
                {
                    if (moved1Cells[c].Equals(cells2[i])) coveredByNew = true;
                }
                if (coveredByNew)
                {
                    Assert.That(grid.TryGetCellOwner(cells2[i], out UnitId o), Is.True);
                    Assert.That(o.Value, Is.EqualTo(1L));
                }
                else
                {
                    Assert.That(grid.TryGetCellOwner(cells2[i], out _), Is.False,
                        "未被新 footprint 覆盖的旧格必须变成空闲");
                }
            }
        }

        [Test]
        public void BatchRelocationRejectsOverlapWithStationaryOccupancy()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            Assert.That(grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(4, 0), GridDirection.East), Is.Null);

            // 批次外仍有静止单位占着 (4,0) ⇒ 拒绝。
            string error = grid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(U(1L), new GridPoint(0, 0), new GridPoint(4, 0))
            });
            Assert.That(error, Does.StartWith(LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER));
            Assert.That(grid.TryGetAnchor(U(1L), out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new GridPoint(0, 0)));

            // 批次目标彼此重叠 ⇒ 拒绝。
            LogicGrid grid2 = NewGrid();
            Assert.That(grid2.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East, Volume), Is.Null);
            Assert.That(grid2.RegisterUnit(U(2L), new GridPoint(20, 20), GridDirection.East, Volume), Is.Null);
            Assert.That(grid2.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(U(1L), new GridPoint(0, 0), new GridPoint(0, 0)),
                new BatchRelocation(U(2L), new GridPoint(20, 20), new GridPoint(0, 0))
            }), Is.EqualTo(LogicGridCodes.LOGIC_GRID_BATCH_TARGET_OVERLAP));
        }

        [Test]
        public void BatchRelocationFailureLeavesGridUnchanged()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East, Volume), Is.Null);
            Assert.That(grid.RegisterUnit(U(2L), new GridPoint(30, 30), GridDirection.East, Volume), Is.Null);

            string before = grid.Describe();
            string a = grid.VerifyConsistency();

            Assert.That(grid.ApplyBatchRelocation(new BatchRelocation[]
            {
                new BatchRelocation(U(1L), new GridPoint(0, 0), new GridPoint(0, 0)),
                new BatchRelocation(U(2L), new GridPoint(31, 31), new GridPoint(0, 0))
            }), Is.Not.Null);

            Assert.That(grid.Describe(), Is.EqualTo(before), "失败必须零写入");
            Assert.That(grid.VerifyConsistency(), Is.EqualTo(a));
            Assert.That(grid.TryGetAnchor(U(2L), out GridPoint second), Is.True);
            Assert.That(second, Is.EqualTo(new GridPoint(30, 30)));
        }

        [Test]
        public void BatchRelocationRequiresPreemptedReservationsToBeCleared()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            Assert.That(grid.RegisterUnitWithPointFootprint(U(2L), new GridPoint(30, 0), GridDirection.East), Is.Null);

            Assert.That(grid.TryReserve(new Reservation(
                new ReservationKey(P(9L), 0), U(2L), new GridPoint(6, 0), 0L, 500L)), Is.Null);

            Assert.That(grid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(U(1L), new GridPoint(0, 0), new GridPoint(6, 0))
            }), Does.StartWith(LogicGridCodes.LOGIC_GRID_BATCH_RESERVATION_NOT_CLEARED));

            // 清理之后即可提交。
            Assert.That(grid.ReleaseReservationsOfPlan(P(9L)).Count, Is.EqualTo(1));
            Assert.That(grid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(U(1L), new GridPoint(0, 0), new GridPoint(6, 0))
            }), Is.Null);
        }

        [Test]
        public void BatchRelocationDoesNotUsePathfinderSegmentsOrReservationArbitration()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            LogicGridMovementAuthority authority = NewAuthority(grid);

            // 目标远离来源、中间隔着大量格：若走寻路/段推进，必然产生段与 Reservation。
            string error = grid.ApplyBatchRelocation(new[]
            {
                new BatchRelocation(U(1L), new GridPoint(0, 0), new GridPoint(30, 30))
            });
            Assert.That(error, Is.Null, error);
            Assert.That(authority.AllSegmentsOrdered().Count, Is.EqualTo(0),
                "批量换位不得产生 MovementSegment");
            Assert.That(grid.AllReservationsOrdered().Count, Is.EqualTo(0),
                "批量换位不得产生普通 Reservation");
            Assert.That(authority.AuditOfPlan(P(1L)).Count, Is.EqualTo(0), "批量换位不产生移动审计步");
        }

        [Test]
        public void LogicGridRegistrationRejectsUnknownUnitsAndDuplicates()
        {
            LogicGrid grid = NewGrid();
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(0, 0), GridDirection.East), Is.Null);
            Assert.That(grid.RegisterUnitWithPointFootprint(U(1L), new GridPoint(4, 0), GridDirection.East),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_UNIT_ALREADY_REGISTERED));
            Assert.That(grid.UnregisterUnit(U(2L)), Is.EqualTo(LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN));
            Assert.That(grid.UnregisterUnit(U(1L)), Is.Null);
            Assert.That(grid.Contains(U(1L)), Is.False);
            Assert.That(grid.TryGetCellOwner(new GridPoint(0, 0), out _), Is.False, "注销必须移除占位索引");
            Assert.That(grid.VerifyConsistency(), Is.Null);
        }

        [Test]
        public void NonCanonicalVolumeTableIsRejected()
        {
            LogicGrid grid = NewGrid();
            var broken = new List<DirectionalTriangleSet>();
            for (int i = 0; i < 12; i++)
            {
                broken.Add(new DirectionalTriangleSet(
                    (GridDirection)i, new[] { new TrianglePoint(3, 0, 1) }));
            }
            // 第 3 项的方向字段与索引不符 ⇒ 非规范表整体拒绝。
            broken[3] = new DirectionalTriangleSet(GridDirection.NorthWest, new[] { new TrianglePoint(3, 0, 1) });

            Assert.That(grid.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East, broken),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_VOLUME_TABLE_NOT_CANONICAL));
            Assert.That(grid.Contains(U(1L)), Is.False, "整体拒绝必须零写入");

            Assert.That(grid.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East,
                    new List<DirectionalTriangleSet>()),
                Is.EqualTo(LogicGridCodes.LOGIC_GRID_VOLUME_TABLE_NOT_CANONICAL));
        }
    }
}
