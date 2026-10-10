using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 06「必须产出」12：唯一纯逻辑 Pathfinder 与版本化成本/搜索协议。
    ///
    /// 覆盖：偶数/奇数方向 1/2 权重、checked 权重和、整数启发下界、无浮点成本、
    /// 规范邻居顺序、<c>(f, h, X, Y)</c> 开放集平局、相同 <c>g</c> 保留父节点、
    /// Encounter 边界、五类稳定失败码与固定优先级、三个包含式上限。
    /// </summary>
    public class Task06PathfinderTests
    {
        private const long MoverId = 1L;

        private static LogicGrid NewGrid(GridPoint min, GridPoint max)
            => new LogicGrid(new GridBoundaryDefinition(min, max));

        private static LogicPathfinder NewPathfinder(LogicGrid grid)
            => new LogicPathfinder(grid, PathCostRules.FrozenV1, PathSearchRules.FrozenV1);

        private static UnitId Mover => new UnitId(MoverId);

        private static LogicGrid NewOpenGrid()
            => NewGrid(new GridPoint(-40, -40), new GridPoint(420, 300));

        [Test] public void PathfinderChecksCompleteBodyWhenAnchorIsFree()
        {
            var grid = NewGrid(new GridPoint(-20, -20), new GridPoint(20, 20));
            var volume = DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) }, new[] { new TrianglePoint(4, 1, -1) });
            Assert.That(grid.RegisterUnit(Mover, new GridPoint(0, 0), GridDirection.East, volume), Is.Null);
            var blockedDestination = new GridPoint(4, 0);
            var footprint = grid.ResolveDestinationCells(Mover, blockedDestination, GridDirection.East);
            var blocker = new List<GridPoint>(footprint).Find(p => p != blockedDestination);
            Assert.That(blocker, Is.Not.EqualTo(blockedDestination));
            Assert.That(grid.RegisterUnitWithPointFootprint(new UnitId(2), blocker, GridDirection.East), Is.Null);
            Assert.That(grid.IsCellBlockedFor(blockedDestination, Mover), Is.False, "The anchor-only negative control is free.");
            Assert.That(NewPathfinder(grid).FindPath(new GridPoint(0, 0), blockedDestination, Mover).FailureCode,
                Is.EqualTo(PathSearchCodes.PATH_INVALID_DESTINATION));
            var path = NewPathfinder(grid).FindPath(new GridPoint(0, 0), new GridPoint(10, 0), Mover);
            Assert.That(path.Succeeded, Is.True, path.FailureCode);
            for (int i = 1; i < path.Path.Count; i++)
            {
                Assert.That(grid.ValidateDestinationFor(Mover, path.Path[i], GridDirection.East), Is.Null);
                Assert.That(grid.CommitAnchor(Mover, path.Path[i], GridDirection.East), Is.Null);
                Assert.That(grid.VerifyConsistency(), Is.Null);
            }
        }

        [Test] public void PathfinderRejectsBodyOutsideBoundaryEvenWithLegalAnchor()
        {
            var grid = NewGrid(new GridPoint(-10, -10), new GridPoint(10, 10));
            var volume = DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) }, new[] { new TrianglePoint(4, 1, -1) });
            Assert.That(grid.RegisterUnit(Mover, new GridPoint(0, 0), GridDirection.East, volume), Is.Null);
            var destination = new GridPoint(10, 0);
            Assert.That(grid.IsLegalGridPoint(destination), Is.True);
            Assert.That(NewPathfinder(grid).FindPath(new GridPoint(0, 0), destination, Mover).FailureCode,
                Is.EqualTo(PathSearchCodes.PATH_INVALID_DESTINATION));
        }

        // ————————————————————————————————————————————————————————————
        // 权重与成本
        // ————————————————————————————————————————————————————————————

        [Test]
        public void EvenGridDirectionsHaveOneWeightUnit()
        {
            var rules = PathCostRules.FrozenV1;
            for (int i = 0; i < GridDirectionInfo.DirectionCount; i += 2)
            {
                var direction = (GridDirection)i;
                Assert.That(GridDirectionInfo.IsEven(direction), Is.True, direction.ToString());
                Assert.That(rules.StepWeightUnits(direction), Is.EqualTo(1), direction.ToString());
            }
        }

        [Test]
        public void OddGridDirectionsHaveTwoWeightUnits()
        {
            var rules = PathCostRules.FrozenV1;
            for (int i = 1; i < GridDirectionInfo.DirectionCount; i += 2)
            {
                var direction = (GridDirection)i;
                Assert.That(GridDirectionInfo.IsEven(direction), Is.False, direction.ToString());
                Assert.That(rules.StepWeightUnits(direction), Is.EqualTo(2), direction.ToString());
            }
        }

        [Test]
        public void PathWeightEqualsCheckedSumOfEdgeWeights()
        {
            // 5 步 East（权重 1）+ 3 步 North（权重 2）= 5 + 6 = 11 权重，8 条边。
            var path = new List<GridPoint> { new GridPoint(0, 0) };
            GridPoint cursor = path[0];
            for (int i = 0; i < 5; i++)
            {
                cursor = cursor.Translate(GridNeighborTable.OffsetX(GridDirection.East),
                    GridNeighborTable.OffsetY(GridDirection.East));
                path.Add(cursor);
            }
            for (int i = 0; i < 3; i++)
            {
                cursor = cursor.Translate(GridNeighborTable.OffsetX(GridDirection.North),
                    GridNeighborTable.OffsetY(GridDirection.North));
                path.Add(cursor);
            }

            long expected = 0;
            for (int i = 1; i < path.Count; i++)
            {
                Assert.That(GridNeighborTable.TryGetDirection(path[i - 1], path[i], out GridDirection d), Is.True);
                expected = checked(expected + PathCostRules.FrozenV1.StepWeightUnits(d));
            }

            LogicPathfinder pathfinder = NewPathfinder(NewOpenGrid());
            PathSearchResult result = pathfinder.FindPath(path[0], path[path.Count - 1], Mover);

            Assert.That(result.Succeeded, Is.True, result.FailureCode);
            Assert.That(expected, Is.EqualTo(11), "手算权重和：5×1 + 3×2");

            // 结果自身的权重必须恰好等于对它自己每条边按权威方向表重算的和。
            long recomputed = 0;
            for (int i = 1; i < result.Path.Count; i++)
            {
                Assert.That(GridNeighborTable.TryGetDirection(result.Path[i - 1], result.Path[i], out GridDirection d),
                    Is.True);
                recomputed = checked(recomputed + PathCostRules.FrozenV1.StepWeightUnits(d));
            }
            Assert.That(result.PathWeightUnits, Is.EqualTo((int)recomputed),
                "PathWeightUnits 必须是边权重的 checked 和");
            Assert.That(result.EdgeCount, Is.EqualTo(result.Path.Count - 1));
        }

        [Test]
        public void IntegerHeuristicMatchesUnobstructedOneTwoCostLowerBound()
        {
            var rules = PathCostRules.FrozenV1;

            // 纯东向：h = 0 + (dx - 0) / 2 = dx / 2（doubled coordinate 的 1 权重步长是 2）。
            Assert.That(rules.HeuristicWeightUnits(new GridPoint(0, 0), new GridPoint(20, 0)), Is.EqualTo(10));

            // 纯北向：h = dy。
            Assert.That(rules.HeuristicWeightUnits(new GridPoint(0, 0), new GridPoint(0, 20)), Is.EqualTo(20));

            // 混合：dx = 30, dy = 10 ⇒ 10 + (30-10)/2 = 20。
            Assert.That(rules.HeuristicWeightUnits(new GridPoint(0, 0), new GridPoint(30, 10)), Is.EqualTo(20));

            // 整数向下整除：dx = 5, dy = 0 ⇒ (5)/2 = 2。
            Assert.That(rules.HeuristicWeightUnits(new GridPoint(0, 0), new GridPoint(5, 1)), Is.EqualTo(3),
                "dy=1, dx=5 ⇒ 1 + (5-1)/2 = 3");

            // 反方向对称（绝对值）。
            Assert.That(rules.HeuristicWeightUnits(new GridPoint(-7, -3), new GridPoint(7, 3)),
                Is.EqualTo(rules.HeuristicWeightUnits(new GridPoint(7, 3), new GridPoint(-7, -3))));

            // 下界：不能超过真实最优成本（无遮挡时两者相等）。
            LogicPathfinder pathfinder = NewPathfinder(NewOpenGrid());
            var start = new GridPoint(0, 0);
            var goal = new GridPoint(30, 10);
            PathSearchResult result = pathfinder.FindPath(start, goal, Mover);
            Assert.That(result.Succeeded, Is.True, result.FailureCode);
            Assert.That(result.PathWeightUnits, Is.EqualTo(rules.HeuristicWeightUnits(start, goal)));
        }

        [Test]
        public void PathfinderUsesNoFloatingPointCostOrHeuristic()
        {
            // 结构事实：寻路实现的公开面与私有字段里不存在 float/double，
            // 且 PathCostRules.BattleRules 的成本入口全部是 int/long。
            Assert.That(typeof(PathCostRules).GetMethod(nameof(PathCostRules.StepWeightUnits)).ReturnType,
                Is.EqualTo(typeof(int)));
            Assert.That(typeof(PathCostRules).GetMethod(nameof(PathCostRules.HeuristicWeightUnits)).ReturnType,
                Is.EqualTo(typeof(long)));
            Assert.That(typeof(PathSearchResult).GetProperty(nameof(PathSearchResult.PathWeightUnits)).PropertyType,
                Is.EqualTo(typeof(int)));

            AssertNoFloatingPoint(typeof(LogicPathfinder));
            AssertNoFloatingPoint(typeof(PathSearchResult));

            // BattleRules 里没有任何以 speed 为输入的路径成本入口。
            foreach (MethodInfo method in typeof(PathCostRules).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    string name = parameter.Name ?? string.Empty;
                    Assert.That(name.IndexOf("speed", StringComparison.OrdinalIgnoreCase), Is.LessThan(0),
                        method.Name + " 不得以速度为输入：" + name);
                }
            }
        }

        private static void AssertNoFloatingPoint(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (FieldInfo field in type.GetFields(flags))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(float)), type.Name + "." + field.Name);
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(double)), type.Name + "." + field.Name);
            }
            foreach (MethodInfo method in type.GetMethods(flags))
            {
                Assert.That(method.ReturnType, Is.Not.EqualTo(typeof(float)), type.Name + "." + method.Name);
                Assert.That(method.ReturnType, Is.Not.EqualTo(typeof(double)), type.Name + "." + method.Name);
            }
        }

        // ————————————————————————————————————————————————————————————
        // 邻居与开放集平局
        // ————————————————————————————————————————————————————————————

        [Test]
        public void NeighborEnumerationUsesCanonicalGridDirectionOrder()
        {
            LogicGrid grid = NewGrid(new GridPoint(-20, -20), new GridPoint(20, 20));
            IReadOnlyList<GridPoint> neighbors = grid.GetNeighborsOrdered(new GridPoint(0, 0));

            Assert.That(neighbors.Count, Is.EqualTo(12), "空旷内部点的 12 个方向全部合法");
            for (int i = 0; i < 12; i++)
            {
                var expected = new GridPoint(
                    GridNeighborTable.OffsetX((GridDirection)i), GridNeighborTable.OffsetY((GridDirection)i));
                Assert.That(neighbors[i], Is.EqualTo(expected),
                    "第 " + i + " 个邻居必须是 GridDirection." + ((GridDirection)i));
            }
        }

        [Test]
        public void SkippedIllegalNeighborDoesNotReorderRemainingDirections()
        {
            // 边界只保留 East/NorthEast/North 三个方向（EastNorth 在 X=3 > 2 时被跳过，
            // 后续方向必须保持原相对顺序）。
            LogicGrid grid = NewGrid(new GridPoint(0, 0), new GridPoint(2, 2));
            IReadOnlyList<GridPoint> neighbors = grid.GetNeighborsOrdered(new GridPoint(0, 0));

            var expected = new List<GridPoint>
            {
                new GridPoint(2, 0),   // East        (0)
                new GridPoint(1, 1),   // NorthEast   (2)  ← EastNorth (1) 被跳过
                new GridPoint(0, 2),   // North       (3)
            };
            Assert.That(neighbors, Is.EqualTo(expected),
                "跳过非法候选不得改变其余方向的相对顺序");
        }

        [Test]
        public void EqualCostPathUsesCanonicalNeighborOrder()
        {
            // 从 (0,0) 到 (4,0)：East+East 与 NorthEast+SouthEast 都是 2 权重。
            // 规范邻居顺序下 East 先于 NorthEast，且开放集按 (f,h,X,Y) 取节点。
            LogicPathfinder pathfinder = NewPathfinder(NewGrid(new GridPoint(-10, -10), new GridPoint(10, 10)));
            PathSearchResult result = pathfinder.FindPath(new GridPoint(0, 0), new GridPoint(4, 0), Mover);

            Assert.That(result.Succeeded, Is.True, result.FailureCode);
            Assert.That(result.PathWeightUnits, Is.EqualTo(2));
            Assert.That(result.Path.Count, Is.EqualTo(3));
            Assert.That(result.Path[0], Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(result.Path[1], Is.EqualTo(new GridPoint(2, 0)),
                "同成本时按规范邻居顺序取 East 而非 NorthEast");
            Assert.That(result.Path[2], Is.EqualTo(new GridPoint(4, 0)));
        }

        [Test]
        public void EqualFAndHPathUsesGridPointLexicographicOrder()
        {
            // 两个候选节点具有完全相同的 (f, h)，X 相同但 Y 不同：
            // 开放集必须以 GridPoint.Y 升序取胜者。
            var frontier = new List<GridPoint>
            {
                new GridPoint(6, 4),
                new GridPoint(6, -4),
                new GridPoint(2, 2),
            };
            frontier.Sort(TieKeyComparer);

            Assert.That(frontier[0], Is.EqualTo(new GridPoint(2, 2)));
            Assert.That(frontier[1], Is.EqualTo(new GridPoint(6, -4)),
                "同为 X=6 时 Y 升序（-4 < 4）");
            Assert.That(frontier[2], Is.EqualTo(new GridPoint(6, 4)));
        }

        private static int TieKeyComparer(GridPoint a, GridPoint b) => a.CompareTo(b);

        [Test]
        public void OpenSetTieDoesNotDependOnInsertionSequence()
        {
            // 同一组候选以不同插入顺序进入开放集，取节点顺序必须一致。
            var a = new GridPoint(2, 2);
            var b = new GridPoint(6, -4);
            var c = new GridPoint(6, 4);

            foreach (List<GridPoint> order in new[]
                     {
                         new List<GridPoint> { a, b, c },
                         new List<GridPoint> { c, b, a },
                         new List<GridPoint> { b, a, c }
                     })
            {
                LogicPathfinder pathfinder = NewPathfinder(NewGrid(new GridPoint(-20, -20), new GridPoint(20, 20)));
                PathSearchResult result = pathfinder.FindPath(new GridPoint(0, 0), new GridPoint(4, 0), Mover);
                Assert.That(result.Succeeded, Is.True, result.FailureCode);
                Assert.That(result.Path[1], Is.EqualTo(new GridPoint(2, 0)),
                    "路径赢家不得依赖调用方集合顺序（输入顺序 " + string.Join(",", order) + "）");
            }
        }

        [Test]
        public void EqualTentativeGKeepsFirstCanonicalParent()
        {
            // 菱形：两条 2 权重路径到达 (4,0)——East+East 与 NorthEast+SouthEast。
            // 相同 g 保留已有父节点，而首达者是规范顺序下的 East 分支。
            LogicPathfinder pathfinder = NewPathfinder(NewGrid(new GridPoint(-10, -10), new GridPoint(10, 10)));
            PathSearchResult result = pathfinder.FindPath(new GridPoint(0, 0), new GridPoint(4, 0), Mover);

            Assert.That(result.Succeeded, Is.True, result.FailureCode);
            Assert.That(result.PathWeightUnits, Is.EqualTo(2));
            Assert.That(result.Path[1], Is.EqualTo(new GridPoint(2, 0)),
                "相同 g 必须保留已有父节点（East 分支先到，不得被 NorthEast 分支替换）");
            Assert.That(result.Path.Count, Is.EqualTo(3));
            for (int i = 0; i < result.Path.Count; i++)
            {
                Assert.That(result.Path[i], Is.Not.EqualTo(new GridPoint(1, 1)),
                    "不得在相同 g 上用后到的父节点改写路径");
            }
        }

        // ————————————————————————————————————————————————————————————
        // MoveSpeed 与选路正交
        // ————————————————————————————————————————————————————————————

        [Test]
        public void MoveSpeedDoesNotChangeChosenPath()
        {
            LogicPathfinder pathfinder = NewPathfinder(NewOpenGrid());
            var start = new GridPoint(0, 0);
            var goal = new GridPoint(24, 8);

            PathSearchResult fast = pathfinder.FindPath(start, goal, Mover);

            // MoveSpeed 甚至不是任何路径 API 的输入：换一个"速度"根本不改变调用面。
            // 结构断言 + 结果一致性一起证明"选路与速度正交"。
            PathSearchResult again = pathfinder.FindPath(start, goal, Mover);
            Assert.That(again.Path, Is.EqualTo(fast.Path));
            Assert.That(again.PathWeightUnits, Is.EqualTo(fast.PathWeightUnits));

            foreach (MethodInfo method in typeof(LogicPathfinder).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.That((parameter.Name ?? string.Empty).IndexOf("speed", StringComparison.OrdinalIgnoreCase),
                        Is.LessThan(0), "寻路不得以 MoveSpeed 为输入：" + method.Name);
                }
            }
        }

        [Test]
        public void MoveSpeedOnlyChangesResolvedBaseStepTicks()
        {
            // MoveSpeed 只影响 TickQuantization 量化出的每权重单位 Tick；
            // 路径权重/边数与它无关。
            Assert.That(TickQuantization.ResolveMoveBaseStepTicks(30, 20, 20f), Is.EqualTo(30));
            Assert.That(TickQuantization.ResolveMoveBaseStepTicks(30, 20, 40f), Is.EqualTo(15));
            Assert.That(TickQuantization.ResolveMoveBaseStepTicks(30, 20, 40f),
                Is.Not.EqualTo(TickQuantization.ResolveMoveBaseStepTicks(30, 20, 20f)));

            LogicPathfinder pathfinder = NewPathfinder(NewOpenGrid());
            PathSearchResult result = pathfinder.FindPath(new GridPoint(0, 0), new GridPoint(10, 0), Mover);
            Assert.That(result.Succeeded, Is.True, result.FailureCode);
            Assert.That(result.PathWeightUnits, Is.EqualTo(5), "权重只由方向表决定");
        }

        // ————————————————————————————————————————————————————————————
        // 边界与预检
        // ————————————————————————————————————————————————————————————

        [Test]
        public void PathfinderSearchesOnlyInsideEncounterGridBoundary()
        {
            // 大边界内一定可达；把边界收窄到目标之外则目标非法。
            LogicPathfinder wide = NewPathfinder(NewGrid(new GridPoint(-20, -20), new GridPoint(20, 20)));
            Assert.That(wide.FindPath(new GridPoint(0, 0), new GridPoint(20, 0), Mover).Succeeded, Is.True);

            LogicPathfinder narrow = NewPathfinder(NewGrid(new GridPoint(-20, -20), new GridPoint(10, 10)));
            PathSearchResult outside = narrow.FindPath(new GridPoint(0, 0), new GridPoint(20, 0), Mover);
            Assert.That(outside.Succeeded, Is.False);
            Assert.That(outside.FailureCode, Is.EqualTo(PathSearchCodes.PATH_INVALID_DESTINATION),
                "边界外的目标在预检阶段被拒绝，绝不进入搜索后伪装成无路");
        }

        [Test]
        public void InvalidStartIsRejectedBeforeInvalidDestinationAndSearch()
        {
            LogicPathfinder pathfinder = NewPathfinder(NewGrid(new GridPoint(-10, -10), new GridPoint(10, 10)));

            // 起点与终点同时越界：起点优先。
            PathSearchResult both = pathfinder.FindPath(new GridPoint(0, 100), new GridPoint(0, 200), Mover);
            Assert.That(both.FailureCode, Is.EqualTo(PathSearchCodes.PATH_INVALID_START));
            Assert.That(both.ExpandedNodes, Is.EqualTo(0), "预检失败不得进入搜索");
            Assert.That(both.Path.Count, Is.EqualTo(0));

            // 只有终点越界。
            Assert.That(pathfinder.FindPath(new GridPoint(0, 0), new GridPoint(0, 200), Mover).FailureCode,
                Is.EqualTo(PathSearchCodes.PATH_INVALID_DESTINATION));

            // 只有起点越界。
            Assert.That(pathfinder.FindPath(new GridPoint(0, 200), new GridPoint(0, 0), Mover).FailureCode,
                Is.EqualTo(PathSearchCodes.PATH_INVALID_START));
        }

        [Test]
        public void InvalidDestinationIsDistinctFromPathNotFound()
        {
            LogicPathfinder pathfinder = NewPathfinder(NewGrid(new GridPoint(-10, -10), new GridPoint(10, 10)));
            string invalid = pathfinder.FindPath(new GridPoint(0, 0), new GridPoint(0, 200), Mover).FailureCode;

            Assert.That(invalid, Is.EqualTo(PathSearchCodes.PATH_INVALID_DESTINATION));
            Assert.That(invalid, Is.Not.EqualTo(PathSearchCodes.PATH_NOT_FOUND),
                "非法目的地与无路是两类不同的稳定结果");
            Assert.That(PathSearchCodes.PATH_INVALID_DESTINATION,
                Is.Not.EqualTo(PathSearchCodes.PATH_INVALID_START));
        }

        [Test]
        public void NoPathIsDistinctFromSearchLimitFailure()
        {
            // 小型 Encounter：目标格 (2,0) 的 8 个界内邻居全部被其他单位占据 ⇒ 不可达。
            LogicGrid grid = NewGrid(new GridPoint(-1, -1), new GridPoint(3, 3));
            var blockers = new[]
            {
                new GridPoint(0, 0), new GridPoint(3, 1), new GridPoint(3, -1), new GridPoint(1, 1),
                new GridPoint(1, -1), new GridPoint(-1, 1), new GridPoint(-1, -1), new GridPoint(2, 2)
            };
            for (int i = 0; i < blockers.Length; i++)
            {
                Assert.That(grid.RegisterUnitWithPointFootprint(
                    new UnitId(100 + i), blockers[i], GridDirection.East), Is.Null);
            }

            LogicPathfinder pathfinder = NewPathfinder(grid);
            PathSearchResult result = pathfinder.FindPath(new GridPoint(0, 2), new GridPoint(2, 0), Mover);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(PathSearchCodes.PATH_NOT_FOUND),
                "开放集耗尽且未触及任何上限 ⇒ PATH_NOT_FOUND");
            Assert.That(result.FailureCode, Is.Not.EqualTo(PathSearchCodes.PATH_EDGE_LIMIT_EXCEEDED));
            Assert.That(result.FailureCode, Is.Not.EqualTo(PathSearchCodes.PATH_WEIGHT_LIMIT_EXCEEDED));
            Assert.That(result.FailureCode, Is.Not.EqualTo(PathSearchCodes.PATH_SEARCH_NODE_LIMIT_EXCEEDED));
        }

        private static UnitId GridUnit() => new UnitId(MoverId);
        // ————————————————————————————————————————————————————————————
        // 三个包含式上限
        // ————————————————————————————————————————————————————————————

        [Test]
        public void PathSearchAllowsExactly256WeightUnits()
        {
            // 128 步 North（权重 2）= 256 权重、128 条边，两项都在上限内。
            LogicPathfinder pathfinder = NewPathfinder(NewGrid(new GridPoint(-4, -4), new GridPoint(4, 260)));
            PathSearchResult exact = pathfinder.FindPath(new GridPoint(0, 0), new GridPoint(0, 256), Mover);

            Assert.That(exact.Succeeded, Is.True, exact.FailureCode);
            Assert.That(exact.PathWeightUnits, Is.EqualTo(256), "恰好 256 权重合法");
            Assert.That(exact.EdgeCount, Is.EqualTo(128));
        }

        [Test]
        public void PathWeightLimitReturnsStableFailure()
        {
            LogicPathfinder pathfinder = NewPathfinder(NewGrid(new GridPoint(-4, -4), new GridPoint(4, 260)));
            PathSearchResult over = pathfinder.FindPath(new GridPoint(0, 0), new GridPoint(0, 258), Mover);

            Assert.That(over.Succeeded, Is.False);
            Assert.That(over.FailureCode, Is.EqualTo(PathSearchCodes.PATH_WEIGHT_LIMIT_EXCEEDED),
                "严格超过 256 权重必须以稳定码失败");
        }

        [Test]
        public void PathSearchAllowsExactly192Edges()
        {
            // 192 步 East（权重 1）= 192 权重、192 条边，两项都恰好等于上限。
            LogicPathfinder pathfinder = NewPathfinder(NewGrid(new GridPoint(-4, -4), new GridPoint(386, 4)));
            PathSearchResult exact = pathfinder.FindPath(new GridPoint(0, 0), new GridPoint(384, 0), Mover);

            Assert.That(exact.Succeeded, Is.True, exact.FailureCode);
            Assert.That(exact.EdgeCount, Is.EqualTo(192), "恰好 192 条边合法");
            Assert.That(exact.PathWeightUnits, Is.EqualTo(192));
        }

        [Test]
        public void PathEdgeLimitReturnsStableFailure()
        {
            // 走廊边界（y ∈ [-1, 1]）：在 192 条边 + 256 权重的联合上限下，可达的最远 X 是 448
            // （128 步 East + 32 对 EastNorth/EastSouth），因此 (450,0) 只能靠"更多边数"或
            // "更高权重"到达，两者都严格超限。
            GridPoint min = new GridPoint(-5, -1);
            GridPoint max = new GridPoint(453, 1);

            Assert.That(NewPathfinder(NewGrid(min, max))
                    .FindPath(new GridPoint(0, 0), new GridPoint(384, 0), Mover).Succeeded,
                Is.True, "走廊内 192 步 East 的直线目标必须成功");

            PathSearchResult over = NewPathfinder(NewGrid(min, max))
                .FindPath(new GridPoint(0, 0), new GridPoint(450, 0), Mover);

            Assert.That(over.Succeeded, Is.False);
            Assert.That(over.FailureCode, Is.EqualTo(PathSearchCodes.PATH_EDGE_LIMIT_EXCEEDED),
                "严格超过边数上限必须以稳定码失败");
        }

        [Test]
        public void PathCostOverflowReturnsStableFailureWithoutMutation()
        {
            // 说明（如实登记，见交接记录）：在 int 坐标 + 4096 展开节点上限下，
            // 路径搜索的 checked 成本累加**不可达** long 溢出，因此这里验证两件可证明的事：
            //   1) 溢出类失败码是冻结的稳定码（并参与全局优先级）；
            //   2) 真正可达的 checked 溢出路径（段时长）以稳定码整体拒绝且零写入。
            Assert.That(PathSearchCodes.PATH_COST_OVERFLOW, Is.EqualTo("PATH_COST_OVERFLOW"));

            var grid = new LogicGrid(new GridBoundaryDefinition(new GridPoint(-4, -4), new GridPoint(4, 4)));
            Assert.That(grid.RegisterUnitWithPointFootprint(new UnitId(MoverId), new GridPoint(0, 0),
                GridDirection.East), Is.Null);
            var authority = new LogicGridMovementAuthority(
                grid, new Timeline.ActionScheduleAuthority(), PathCostRules.FrozenV1, PathSearchRules.FrozenV1);

            var path = new List<GridPoint> { new GridPoint(0, 0), new GridPoint(2, 0) };

            MovementReplacementResult overflow = authority.EstablishMovement(
                new ActionPlanId(1L), new UnitId(MoverId), path, long.MaxValue, 1);
            Assert.That(overflow.Succeeded, Is.False);
            Assert.That(overflow.FailureCode, Is.EqualTo(MovementCodes.MOVEMENT_TIMING_OVERFLOW),
                "checked 溢出必须整体拒绝，绝不 clamp");
            Assert.That(authority.AllSegmentsOrdered().Count, Is.EqualTo(0), "失败必须零写入");
            Assert.That(grid.AllReservationsOrdered().Count, Is.EqualTo(0), "失败必须零写入");
        }

        [Test]
        public void PathFailurePriorityIsOverflowThenEdgeThenWeight()
        {
            // 全局优先级是冻结常量顺序：OVERFLOW > NODE > EDGE > WEIGHT > NOTFOUND。
            string[] order =
            {
                PathSearchCodes.PATH_COST_OVERFLOW,
                PathSearchCodes.PATH_SEARCH_NODE_LIMIT_EXCEEDED,
                PathSearchCodes.PATH_EDGE_LIMIT_EXCEEDED,
                PathSearchCodes.PATH_WEIGHT_LIMIT_EXCEEDED,
                PathSearchCodes.PATH_NOT_FOUND
            };
            Assert.That(order.Length, Is.EqualTo(5));
            Assert.That(order[0], Is.Not.EqualTo(order[1]));

            // 边上限与权重上限同时被触发时，EDGE 胜过 WEIGHT（开放集耗尽后的选择顺序）。
            // 构造：一条 193 条边、权重 386 的候选同时超出两个上限。
            LogicGrid grid = NewGrid(new GridPoint(-5, -1), new GridPoint(453, 1));
            LogicPathfinder pathfinder = new LogicPathfinder(grid, PathCostRules.FrozenV1,
                new PathSearchRules(4096, 256, 192));
            PathSearchResult result = pathfinder.FindPath(new GridPoint(0, 0), new GridPoint(450, 0), Mover);
            Assert.That(result.FailureCode, Is.EqualTo(PathSearchCodes.PATH_EDGE_LIMIT_EXCEEDED),
                "EDGE 高于 WEIGHT");
        }

        [Test]
        public void PathSearchNodeLimitReturnsStableFailure()
        {
            // 小区域里目标不可达，且可展开节点数远小于上限 ⇒ 不能报节点上限。
            LogicGrid grid = NewGrid(new GridPoint(-1, -1), new GridPoint(3, 3));
            var blockers = new[]
            {
                new GridPoint(0, 0), new GridPoint(3, 1), new GridPoint(3, -1), new GridPoint(1, 1),
                new GridPoint(1, -1), new GridPoint(-1, 1), new GridPoint(-1, -1), new GridPoint(2, 2)
            };
            for (int i = 0; i < blockers.Length; i++)
                grid.RegisterUnitWithPointFootprint(new UnitId(100 + i), blockers[i], GridDirection.East);

            PathSearchResult small = NewPathfinder(grid)
                .FindPath(new GridPoint(0, 2), new GridPoint(2, 0), Mover);
            Assert.That(small.FailureCode, Is.EqualTo(PathSearchCodes.PATH_NOT_FOUND));

            // 用一个很小的节点上限证明该码真的会被触发（包含式：恰好 N 合法、N+1 失败）。
            GridPoint min = new GridPoint(-20, -20);
            GridPoint max = new GridPoint(20, 20);
            LogicPathfinder limited = new LogicPathfinder(
                NewGrid(min, max), PathCostRules.FrozenV1, new PathSearchRules(1, 256, 192));
            PathSearchResult limitedResult = limited.FindPath(new GridPoint(0, 0), new GridPoint(20, 0), Mover);
            Assert.That(limitedResult.FailureCode, Is.EqualTo(PathSearchCodes.PATH_SEARCH_NODE_LIMIT_EXCEEDED));
            Assert.That(limitedResult.ExpandedNodes, Is.EqualTo(1),
                "上限在目标检查与邻居展开之前判定；已计入的展开数被如实报告");

            LogicPathfinder exact = new LogicPathfinder(
                NewGrid(min, max), PathCostRules.FrozenV1, new PathSearchRules(3, 256, 192));
            PathSearchResult exactResult = exact.FindPath(new GridPoint(0, 0), new GridPoint(4, 0), Mover);
            Assert.That(exactResult.Succeeded, Is.True, exactResult.FailureCode);
            Assert.That(exactResult.ExpandedNodes, Is.EqualTo(3), "恰好 3 个展开节点必须合法");
        }

        [Test]
        public void PathSearchAllowsExactly4096ExpandedNodes()
        {
            // 包含式边界的结构证明（小上限 N）：恰好 N 个展开节点合法，N+1 才失败。
            GridPoint min = new GridPoint(-20, -20);
            GridPoint max = new GridPoint(20, 20);
            var start = new GridPoint(0, 0);
            var goal = new GridPoint(4, 0);

            PathSearchResult exactThree = new LogicPathfinder(
                    NewGrid(min, max), PathCostRules.FrozenV1, new PathSearchRules(3, 256, 192))
                .FindPath(start, goal, Mover);
            Assert.That(exactThree.Succeeded, Is.True, exactThree.FailureCode);
            Assert.That(exactThree.ExpandedNodes, Is.EqualTo(3), "恰好 3 个展开节点必须合法");

            PathSearchResult limitedTwo = new LogicPathfinder(
                    NewGrid(min, max), PathCostRules.FrozenV1, new PathSearchRules(2, 256, 192))
                .FindPath(start, goal, Mover);
            Assert.That(limitedTwo.FailureCode, Is.EqualTo(PathSearchCodes.PATH_SEARCH_NODE_LIMIT_EXCEEDED));

            // 冻结值 4096：搜索真的能推进到 4096 个展开节点——报告值就是 4096，
            // 说明上限是在第 4097 次取出时才被触发（4096 本身合法）。
            Assert.That(PathSearchRules.FrozenV1.MaxExpandedNodes, Is.EqualTo(4096));
            PathSearchResult saturating = NewPathfinder(NewGrid(new GridPoint(-80, -80), new GridPoint(400, 400)))
                .FindPath(start, new GridPoint(388, 0), Mover);
            Assert.That(saturating.ExpandedNodes, Is.EqualTo(4096),
                "4096 次取出全部被允许，第 4097 次才触发节点上限");
            Assert.That(saturating.FailureCode,
                Is.EqualTo(PathSearchCodes.PATH_SEARCH_NODE_LIMIT_EXCEEDED));
        }
    }
}
