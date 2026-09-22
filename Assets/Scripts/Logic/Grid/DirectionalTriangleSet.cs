using System.Collections.Generic;

namespace ProjectHero.Logic.Grid
{
    /// <summary>
    /// 单个方向的规范三角形集合（主方案 2.3.1）。点已按 (X, Y, T) 去重并升序排序。
    /// 运行时消费端只允许按方向索引本表并对单位逻辑位置做整数平移，
    /// 不得调用三角函数、浮点舍入或任意角旋转。
    /// </summary>
    public sealed record DirectionalTriangleSet(
        GridDirection Direction,
        IReadOnlyList<TrianglePoint> Triangles)
    {
        /// <summary>按原点做整数平移的运行时查询（唯一允许的空间消费方式）。</summary>
        public IReadOnlyList<TrianglePoint> GetTranslated(GridPoint origin)
        {
            if (Triangles == null) return System.Array.Empty<TrianglePoint>();
            var result = new TrianglePoint[Triangles.Count];
            for (int i = 0; i < Triangles.Count; i++)
            {
                result[i] = Triangles[i].Translate(origin.X, origin.Y);
            }
            return result;
        }
    }
}
