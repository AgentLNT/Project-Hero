using System;
using System.Collections.Generic;

namespace ProjectHero.Logic.Grid
{
    /// <summary>
    /// <strong>体积 footprint 的唯一运行时消费方式</strong>（任务包「必须产出」1 与「逻辑与表现边界」）。
    ///
    /// 单位体积来自任务 02B 已预展开的 <c>VolumeSpec.Directions[0..11]</c> 规范整数表。
    /// 本类只做三件事，并且<strong>只能</strong>做这三件事：
    /// <list type="number">
    /// <item>按 <see cref="GridDirection"/> 的冻结数值<strong>索引</strong>规范表；</item>
    /// <item>对单位逻辑位置（<see cref="GridPoint"/> 锚点）做<strong>整数平移</strong>；</item>
    /// <item>把结果整理成规范（去重 + 升序）的集合供集合查询使用。</item>
    /// </list>
    ///
    /// 它<strong>不</strong>旋转点集、<strong>不</strong>调用 <c>GridMath.Rotate</c>、<strong>不</strong>使用
    /// <c>Mathf</c>/三角函数/浮点舍入：任何角度的朝向都只是"换一个已预展开的方向下标"。
    ///
    /// 单位体积在 doubled coordinates 下有两种等价投影，本类同时提供：
    /// <list type="bullet">
    /// <item><strong>三角 footprint</strong>（<see cref="ResolveTriangles"/>）：规范表给出的三角形，
    /// 平移后按 <c>(X, Y, T)</c> 去重升序——用于体积级合法性与相交判断；</item>
    /// <item><strong>格 footprint</strong>（<see cref="ResolveCells"/>）：上述三角形的三个角顶点
    /// （<c>(X-1,Y)</c>、<c>(X+1,Y)</c>、<c>(X,Y+T)</c>），平移后按 <c>(X, Y)</c> 去重升序——
    /// 用于占位索引、区域查询、Reservation 与 BatchRelocation 的锚点级判定。</item>
    /// </list>
    /// 两种投影都只依赖"方向查表 + 整数平移"，因此同一 <c>VolumeSpec</c>、同一逻辑位置与同一方向
    /// 永远得到同一结果，与资产枚举顺序、运行平台或视觉插值无关。
    /// </summary>
    public static class VolumeFootprint
    {
        /// <summary>规范 12 向表校验（复用任务 02 的定义校验，错误码统一为 LogicGrid 口径）。</summary>
        public static string ValidateDirectionsTable(IReadOnlyList<DirectionalTriangleSet> directions)
            => DirectionalSpecValidation.ValidateDirections(directions) == null
                ? null
                : LogicGridCodes.LOGIC_GRID_VOLUME_TABLE_NOT_CANONICAL;

        /// <summary>
        /// 朝向 <paramref name="facing"/>、锚点 <paramref name="origin"/> 的体积三角 footprint。
        /// 只做方向索引与整数平移，输出按 <c>(X, Y, T)</c> 升序且已去重。
        /// </summary>
        public static IReadOnlyList<TrianglePoint> ResolveTriangles(
            IReadOnlyList<DirectionalTriangleSet> directions, GridPoint origin, GridDirection facing)
        {
            string error = ValidateDirectionsTable(directions);
            if (error != null) throw new LogicDefinitionException(error);

            DirectionalTriangleSet set = directions[(int)facing];
            if (set == null || set.Direction != facing)
                throw new LogicDefinitionException(LogicGridCodes.LOGIC_GRID_VOLUME_TABLE_NOT_CANONICAL);

            IReadOnlyList<TrianglePoint> source = set.Triangles;
            if (source == null || source.Count == 0)
                throw new LogicDefinitionException(LogicGridCodes.LOGIC_GRID_VOLUME_TABLE_NOT_CANONICAL);

            var translated = new List<TrianglePoint>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                TrianglePoint p = source[i];
                int x = checked(p.X + origin.X);
                int y = checked(p.Y + origin.Y);
                if (!TrianglePoint.IsValid(x, y, p.T))
                    throw new LogicDefinitionException(TrianglePoint.INVALID, $"X={x}, Y={y}, T={p.T}");
                translated.Add(new TrianglePoint(x, y, p.T));
            }
            translated.Sort();
            return DeduplicateSorted(translated);
        }

        /// <summary>
        /// 朝向 <paramref name="facing"/>、锚点 <paramref name="origin"/> 的体积<strong>格</strong>footprint
        /// （三角三个角顶点）。只做方向索引与整数平移，输出按 <c>(X, Y)</c> 升序且已去重。
        /// </summary>
        public static IReadOnlyList<GridPoint> ResolveCells(
            IReadOnlyList<DirectionalTriangleSet> directions, GridPoint origin, GridDirection facing)
        {
            IReadOnlyList<TrianglePoint> triangles = ResolveTriangles(directions, origin, facing);
            var cells = new List<GridPoint>(triangles.Count * 3);
            for (int i = 0; i < triangles.Count; i++)
            {
                TrianglePoint tri = triangles[i];
                AddCell(cells, tri.X - 1, tri.Y);
                AddCell(cells, tri.X + 1, tri.Y);
                AddCell(cells, tri.X, tri.Y + tri.T);
            }
            cells.Sort();
            return DeduplicateSorted(cells);
        }

        private static void AddCell(List<GridPoint> cells, int x, int y)
        {
            if (!GridPoint.IsValidParity(x, y))
                throw new LogicDefinitionException(GridPoint.INVALID_PARITY, $"X={x}, Y={y}");
            cells.Add(new GridPoint(x, y));
        }

        private static List<T> DeduplicateSorted<T>(List<T> sorted) where T : IEquatable<T>
        {
            if (sorted.Count <= 1) return sorted;
            var result = new List<T>(sorted.Count) { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (!sorted[i].Equals(result[result.Count - 1])) result.Add(sorted[i]);
            }
            return result;
        }
    }
}
