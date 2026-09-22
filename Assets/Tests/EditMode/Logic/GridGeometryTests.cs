using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Tests
{
    /// <summary>三角格离散几何纯数据契约（主方案 2.3.1 / 3.8）：合法性、60° 整数展开、12 向规范表。</summary>
    public class GridGeometryTests
    {
        private static IReadOnlyList<DirectionalTriangleSet> MakeDirections()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        [Test]
        public void GridPointRejectsOddCoordinateParity()
        {
            Assert.That(GridPoint.IsValidParity(0, 0), Is.True);
            Assert.That(GridPoint.IsValidParity(1, 1), Is.True);
            Assert.That(GridPoint.IsValidParity(2, 0), Is.True);
            Assert.That(GridPoint.IsValidParity(0, -2), Is.True);
            Assert.That(GridPoint.IsValidParity(-5, -5), Is.True);

            Assert.That(GridPoint.IsValidParity(1, 0), Is.False);
            Assert.That(GridPoint.IsValidParity(0, 1), Is.False);
            Assert.That(GridPoint.IsValidParity(-1, 0), Is.False);

            var ex = Assert.Throws<LogicDefinitionException>(() => new GridPoint(1, 0));
            Assert.That(ex.ErrorCode, Is.EqualTo(GridPoint.INVALID_PARITY));

            Assert.That(GridPoint.TryCreate(1, 0, out _), Is.False);
            Assert.That(GridPoint.TryCreate(2, 0, out var point), Is.True);
            Assert.That(point.X, Is.EqualTo(2));
            Assert.That(point.Y, Is.EqualTo(0));
            // default(GridPoint) = (0,0) 合法。
            Assert.That(default(GridPoint).Equals(new GridPoint(0, 0)), Is.True);
        }

        [Test]
        public void TrianglePointRejectsInvalidOrientationOrParity()
        {
            // 冻结规则：T ∈ {-1,1} 且 X + Y + T 为偶数（保证 60° 整数展开的两个除法整除）。
            Assert.That(TrianglePoint.IsValid(1, 0, 1), Is.True);
            Assert.That(TrianglePoint.IsValid(1, 0, -1), Is.True);
            Assert.That(TrianglePoint.IsValid(3, 0, 1), Is.True);
            Assert.That(TrianglePoint.IsValid(2, 1, -1), Is.True);
            Assert.That(TrianglePoint.IsValid(1, 0, -1), Is.True);

            // T 只能为 ±1
            Assert.That(TrianglePoint.IsValid(1, 0, 0), Is.False);
            Assert.That(TrianglePoint.IsValid(1, 0, 2), Is.False);
            // X + Y + T 必须为偶数
            Assert.That(TrianglePoint.IsValid(0, 0, 1), Is.False, "X+Y+T=1 为奇数，非法");
            Assert.That(TrianglePoint.IsValid(1, 1, 1), Is.False);
            Assert.That(TrianglePoint.IsValid(2, 0, 1), Is.False);

            var exT = Assert.Throws<LogicDefinitionException>(() => new TrianglePoint(1, 0, 0));
            Assert.That(exT.ErrorCode, Is.EqualTo(TrianglePoint.INVALID));
            var exParity = Assert.Throws<LogicDefinitionException>(() => new TrianglePoint(0, 0, 1));
            Assert.That(exParity.ErrorCode, Is.EqualTo(TrianglePoint.INVALID));

            Assert.That(TrianglePoint.TryCreate(1, 0, 1, out var p), Is.True);
            Assert.That(p.T, Is.EqualTo(1));
            Assert.That(TrianglePoint.TryCreate(0, 0, 1, out _), Is.False);
        }

        [Test]
        public void IntegerSixtyDegreeExpansionPreservesTriangleLattice()
        {
            // 手算验证：X' = (X - 3Y - T) / 2, Y' = (X + Y + T) / 2, T' = -T
            var p = new TrianglePoint(3, 0, 1);
            var r1 = DirectionalGeometry.Rotate60CounterClockwise(p);
            Assert.That(r1, Is.EqualTo(new TrianglePoint(1, 2, -1)), "60° 逆时针整数展开的精确结果");

            var r2 = DirectionalGeometry.Rotate60CounterClockwise(r1);
            Assert.That(r2, Is.EqualTo(new TrianglePoint(-2, 1, 1)));
            var r3 = DirectionalGeometry.Rotate60CounterClockwise(r2);
            Assert.That(r3, Is.EqualTo(new TrianglePoint(-3, 0, -1)));

            // 任意合法输入 → 输出仍合法（T ∈ {±1}，X + Y + T 偶数）。
            var samples = new[]
            {
                new TrianglePoint(3, 0, 1), new TrianglePoint(1, 2, -1), new TrianglePoint(-1, -2, 1),
                new TrianglePoint(2, 1, -1), new TrianglePoint(1, 0, 1), new TrianglePoint(4, 1, -1)
            };
            foreach (var sample in samples)
            {
                var rotated = DirectionalGeometry.Rotate60CounterClockwise(sample);
                Assert.That(TrianglePoint.IsValid(rotated.X, rotated.Y, rotated.T), Is.True,
                    $"旋转结果必须保持三角格合法：{sample} → {rotated}");
            }

            // 非法输入拒绝。
            Assert.Throws<LogicDefinitionException>(() =>
                DirectionalGeometry.Rotate60CounterClockwise(new TrianglePoint(1, 1, 1)));
        }

        [Test]
        public void SixIntegerRotationsReturnOriginalTriangle()
        {
            var original = new TrianglePoint(3, 0, 1);
            var current = original;
            for (int i = 0; i < 6; i++)
            {
                current = DirectionalGeometry.Rotate60CounterClockwise(current);
                Assert.That(TrianglePoint.IsValid(current.X, current.Y, current.T), Is.True,
                    $"第 {i + 1} 次旋转后仍合法");
                Assert.That(current.T, Is.EqualTo(i % 2 == 0 ? -1 : 1), "T 每 60° 翻转一次");
            }
            Assert.That(current, Is.EqualTo(original), "旋转六次必须回到原点");

            var second = new TrianglePoint(1, 2, -1);
            var probe = second;
            for (int i = 0; i < 6; i++) probe = DirectionalGeometry.Rotate60CounterClockwise(probe);
            Assert.That(probe, Is.EqualTo(second));
        }

        [Test]
        public void DirectionalTriangleTableRequiresCanonicalTwelveDirections()
        {
            // 带重复与乱序的输入 → 去重并按 (X, Y, T) 升序规范化。
            var evenBase = new[]
            {
                new TrianglePoint(3, 0, 1), new TrianglePoint(3, 0, 1), new TrianglePoint(1, 2, -1)
            };
            var oddBase = new[] { new TrianglePoint(4, 1, -1) };

            var directions = DirectionalGeometry.ExpandFromBases(evenBase, oddBase);

            Assert.That(directions.Count, Is.EqualTo(12), "必须恰好 12 个方向");
            for (int i = 0; i < 12; i++)
            {
                Assert.That(directions[i].Direction, Is.EqualTo((GridDirection)i),
                    $"Directions[{i}].Direction 必须 == (GridDirection){i}");
                Assert.That(directions[i].Triangles, Is.Not.Empty);
            }

            // 方向 0（East）= 偶数基准本身，规范化排序。
            Assert.That(directions[0].Triangles.ToArray(),
                Is.EqualTo(new[] { new TrianglePoint(1, 2, -1), new TrianglePoint(3, 0, 1) }));
            // 方向 2（NorthEast）= 偶数基准旋转 1 次。
            Assert.That(directions[2].Triangles.ToArray(),
                Is.EqualTo(new[]
                {
                    DirectionalGeometry.Rotate60CounterClockwise(new TrianglePoint(1, 2, -1)),
                    DirectionalGeometry.Rotate60CounterClockwise(new TrianglePoint(3, 0, 1))
                }.OrderBy(p => p).ToArray()));
            // 方向 1（EastNorth）= 奇数基准本身。
            Assert.That(directions[1].Triangles.ToArray(), Is.EqualTo(new[] { new TrianglePoint(4, 1, -1) }));
            // 方向 3（North）= 奇数基准旋转 1 次。
            Assert.That(directions[3].Triangles.ToArray(),
                Is.EqualTo(new[] { DirectionalGeometry.Rotate60CounterClockwise(new TrianglePoint(4, 1, -1)) }));

            // 缺少任一基准 → 整体拒绝。
            var ex = Assert.Throws<LogicDefinitionException>(() =>
                DirectionalGeometry.ExpandFromBases(evenBase, new TrianglePoint[0]));
            Assert.That(ex.ErrorCode, Is.EqualTo(DirectionalCodes.DIRECTIONAL_BASE_MISSING));

            // 表不完整 / 非规范 → 校验拒绝。
            var incomplete = directions.Take(11).ToArray();
            Assert.That(DirectionalSpecValidation.ValidateDirections(incomplete),
                Is.EqualTo(DirectionalCodes.DIRECTIONAL_TABLE_INCOMPLETE));
            var shuffled = directions.ToArray();
            shuffled[0] = directions[1];
            Assert.That(DirectionalSpecValidation.ValidateDirections(shuffled),
                Is.EqualTo(DirectionalCodes.DIRECTIONAL_TABLE_NOT_CANONICAL));

            Assert.That(DirectionalSpecValidation.ValidateDirections(directions), Is.Null);
        }

        [Test]
        public void LogicDirectionalGeometryHasNoRuntimeRotationDependency()
        {
            // 运行时查询 = 方向索引 + 整数平移。
            var directions = MakeDirections();
            var triangles = DirectionalTableQuery.GetTrianglesFor(
                directions, GridDirection.NorthEast, new GridPoint(4, 4));
            Assert.That(triangles.ToArray(),
                Is.EqualTo(new[]
                {
                    DirectionalGeometry.Rotate60CounterClockwise(new TrianglePoint(3, 0, 1))
                        .Translate(4, 4)
                }));

            // 平移保持合法性与精确整数结果：(1,2,-1) + (4,4) = (5,6,-1)，5+6-1=10 偶数。
            Assert.That(triangles[0], Is.EqualTo(new TrianglePoint(5, 6, -1)));

            // 几何查询面不得暴露任何浮点/旋转 API：方法无 float/double 参数或返回，
            // 且不得存在 Rotate/Sin/Cos/Angle/Rad/Deg 命名的方法。
            // 运行时查询类型：DirectionalTriangleSet / 三种 Spec / DirectionalTableQuery。
            var geometryTypes = new[]
            {
                typeof(DirectionalTriangleSet), typeof(AttackPatternSpec), typeof(VolumeSpec),
                typeof(MovementPatternSpec), typeof(DirectionalTableQuery)
            };
            foreach (var type in geometryTypes)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                                                       BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    string name = method.Name;
                    Assert.That(name, Does.Not.Contain("Rotate").And.Not.Contain("Sin")
                        .And.Not.Contain("Cos").And.Not.Contain("Angle")
                        .And.Not.Contain("Rad").And.Not.Contain("Deg"),
                        $"{type.Name}.{name} 不得暴露旋转/三角 API");
                    Assert.That(method.ReturnType != typeof(float) && method.ReturnType != typeof(double),
                        $"{type.Name}.{name} 返回类型不得为浮点");
                    foreach (var parameter in method.GetParameters())
                    {
                        Assert.That(parameter.ParameterType != typeof(float) &&
                                    parameter.ParameterType != typeof(double),
                            $"{type.Name}.{name} 参数不得为浮点");
                    }
                }
            }

            // 定义构建专用的整数旋转是全整数签名（DirectionalGeometry 是唯一的例外承载者，
            // 仅用于 Authoring 边界展开 12 向表，不可用于运行时查询）。
            foreach (var method in typeof(DirectionalGeometry).GetMethods(
                         BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.That(method.ReturnType != typeof(float) && method.ReturnType != typeof(double),
                    $"{nameof(DirectionalGeometry)}.{method.Name} 返回类型不得为浮点");
                foreach (var parameter in method.GetParameters())
                {
                    Assert.That(parameter.ParameterType != typeof(float) &&
                                parameter.ParameterType != typeof(double),
                        $"{nameof(DirectionalGeometry)}.{method.Name} 参数不得为浮点");
                }
            }
            var rotate = typeof(DirectionalGeometry).GetMethod("Rotate60CounterClockwise");
            Assert.That(rotate, Is.Not.Null);
            Assert.That(rotate.ReturnType, Is.EqualTo(typeof(TrianglePoint)));
            Assert.That(rotate.GetParameters().Single().ParameterType, Is.EqualTo(typeof(TrianglePoint)));
        }
    }
}
