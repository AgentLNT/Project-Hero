using System;
using System.Collections.Generic;
using ProjectHero.Logic;

namespace ProjectHero.Logic.Grid
{
    /// <summary>
    /// 定义构建期（Builder）的受检整数方向几何。
    /// 60° 逆时针旋转的唯一整数变换固定为（主方案 2.3.1）：
    /// <code>X' = (X - 3Y - T) / 2; Y' = (X + Y + T) / 2; T' = -T</code>
    /// 合法三角格点的奇偶约束保证除法整除；全部运算受检（checked），溢出以
    /// <see cref="DirectionalCodes.DIRECTIONAL_EXPANSION_OVERFLOW"/> 拒绝。
    /// 本类只用于把 East/EastNorth 基准一次性展开成 12 向规范表；
    /// 它不是 Logic 运行时 Step 的服务，运行时只按方向查表并整数平移。
    /// </summary>
    public static class DirectionalGeometry
    {
        public const int RotationStepsPerFullTurn = 6;

        /// <summary>受检 60° 逆时针旋转（定义构建专用，全整数，无浮点/三角）。</summary>
        public static TrianglePoint Rotate60CounterClockwise(TrianglePoint point)
        {
            if (!TrianglePoint.IsValid(point.X, point.Y, point.T))
                throw new LogicDefinitionException(DirectionalCodes.TRIANGLE_POINT_INVALID,
                    $"X={point.X}, Y={point.Y}, T={point.T}");

            int x, y;
            try
            {
                x = checked((point.X - checked(3 * point.Y) - point.T) / 2);
                y = checked((point.X + point.Y + point.T) / 2);
            }
            catch (OverflowException ex)
            {
                throw new LogicDefinitionException(DirectionalCodes.DIRECTIONAL_EXPANSION_OVERFLOW, ex.Message);
            }

            int t = -point.T;
            if (!TrianglePoint.IsValid(x, y, t))
                throw new LogicDefinitionException(DirectionalCodes.TRIANGLE_POINT_INVALID,
                    $"X={x}, Y={y}, T={t}");

            return new TrianglePoint(x, y, t);
        }

        /// <summary>
        /// 把 East 基准（偶数方向 0,2,4,6,8,10）与 EastNorth 基准（奇数方向 1,3,5,7,9,11）
        /// 各连续旋转 0..5 次，交错展开为严格按 <see cref="GridDirection"/> 数值 0 → 11 排列的
        /// 12 向规范表。缺少任一基准、非法点、溢出或表不完整时整体拒绝；
        /// 不得用偶数基准猜测 30° 方向，也不得退回浮点旋转。
        /// </summary>
        public static IReadOnlyList<DirectionalTriangleSet> ExpandFromBases(
            IReadOnlyList<TrianglePoint> evenBase,
            IReadOnlyList<TrianglePoint> oddBase)
        {
            if (evenBase == null || evenBase.Count == 0 || oddBase == null || oddBase.Count == 0)
                throw new LogicDefinitionException(DirectionalCodes.DIRECTIONAL_BASE_MISSING);

            var evenCanonical = Canonicalize(evenBase);
            var oddCanonical = Canonicalize(oddBase);

            var sets = new DirectionalTriangleSet[GridDirectionInfo.DirectionCount];
            for (int step = 0; step < RotationStepsPerFullTurn; step++)
            {
                int evenIndex = step * 2;
                int oddIndex = step * 2 + 1;
                sets[evenIndex] = new DirectionalTriangleSet(
                    (GridDirection)evenIndex, RotateBase(evenCanonical, step));
                sets[oddIndex] = new DirectionalTriangleSet(
                    (GridDirection)oddIndex, RotateBase(oddCanonical, step));
            }

            for (int i = 0; i < sets.Length; i++)
            {
                if (sets[i] == null || sets[i].Triangles == null || sets[i].Triangles.Count == 0)
                    throw new LogicDefinitionException(DirectionalCodes.DIRECTIONAL_TABLE_INCOMPLETE);
            }

            return sets;
        }

        /// <summary>校验合法、按 (X, Y, T) 去重并升序排序（规范形式）。</summary>
        public static List<TrianglePoint> Canonicalize(IReadOnlyList<TrianglePoint> points)
        {
            if (points == null)
                throw new LogicDefinitionException(DirectionalCodes.TRIANGLE_POINT_INVALID, "null point list");

            var seen = new HashSet<TrianglePoint>();
            var result = new List<TrianglePoint>(points.Count);
            foreach (var p in points)
            {
                if (!TrianglePoint.IsValid(p.X, p.Y, p.T))
                    throw new LogicDefinitionException(DirectionalCodes.TRIANGLE_POINT_INVALID,
                        $"X={p.X}, Y={p.Y}, T={p.T}");
                if (seen.Add(p)) result.Add(p);
            }
            result.Sort();
            return result;
        }

        private static IReadOnlyList<TrianglePoint> RotateBase(
            IReadOnlyList<TrianglePoint> canonicalBase, int steps)
        {
            var result = new List<TrianglePoint>(canonicalBase.Count);
            foreach (var p in canonicalBase)
            {
                var q = p;
                for (int i = 0; i < steps; i++)
                {
                    q = Rotate60CounterClockwise(q);
                }
                result.Add(q);
            }
            result.Sort();
            return result;
        }
    }
}
