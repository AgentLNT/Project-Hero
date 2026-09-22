using System.Collections.Generic;

namespace ProjectHero.Logic.Grid
{
    /// <summary>
    /// 12 向规范表的定义校验。要求：恰好 12 个方向、
    /// <c>Directions[i].Direction == (GridDirection)i</c>、每个方向非空、
    /// 点全部合法且按 (X, Y, T) 严格升序（去重 + 稳定排序的规范形式）。
    /// </summary>
    public static class DirectionalSpecValidation
    {
        public static string ValidateDirections(IReadOnlyList<DirectionalTriangleSet> directions)
        {
            if (directions == null || directions.Count != GridDirectionInfo.DirectionCount)
                return DirectionalCodes.DIRECTIONAL_TABLE_INCOMPLETE;

            for (int i = 0; i < GridDirectionInfo.DirectionCount; i++)
            {
                var set = directions[i];
                if (set == null || set.Direction != (GridDirection)i)
                    return DirectionalCodes.DIRECTIONAL_TABLE_NOT_CANONICAL;
                if (set.Triangles == null || set.Triangles.Count == 0)
                    return DirectionalCodes.DIRECTIONAL_TABLE_INCOMPLETE;

                TrianglePoint? previous = null;
                foreach (var p in set.Triangles)
                {
                    if (!TrianglePoint.IsValid(p.X, p.Y, p.T))
                        return DirectionalCodes.TRIANGLE_POINT_INVALID;
                    if (previous.HasValue && previous.Value.CompareTo(p) >= 0)
                        return DirectionalCodes.DIRECTIONAL_TABLE_NOT_CANONICAL;
                    previous = p;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Logic 运行时唯一的空间查询方式：按方向索引规范表，再对单位逻辑位置做整数平移。
    /// 不暴露、不调用任意角旋转、三角函数或浮点舍入（主方案 2.3.1 / 3.8）。
    /// </summary>
    public static class DirectionalTableQuery
    {
        public static IReadOnlyList<TrianglePoint> GetTrianglesFor(
            IReadOnlyList<DirectionalTriangleSet> directions,
            GridDirection direction,
            GridPoint origin)
        {
            int index = (int)direction;
            var set = directions[index];
            if (set == null || set.Direction != direction)
                throw new ProjectHero.Logic.LogicDefinitionException(DirectionalCodes.DIRECTIONAL_TABLE_NOT_CANONICAL);
            return set.GetTranslated(origin);
        }
    }
}
