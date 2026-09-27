namespace ProjectHero.Logic.Grid
{
    /// <summary>
    /// <strong>12 向邻居的规范偏移表</strong>（主方案 3.8 / 00 号规则 32）。
    ///
    /// 这是 Logic 内<strong>唯一</strong>的邻居几何来源：占位、区域查询、寻路与 Dodge 校验
    /// 都只能按 <see cref="GridDirection"/> 的冻结数值 <c>0 -> 11</c> 索引本表，再对逻辑位置做
    /// <strong>整数平移</strong>。不存在第二套方向排列，也不存在三角函数、浮点舍入或任意角旋转。
    ///
    /// 偏移量（doubled coordinates；全部满足 <c>dx + dy</c> 为偶数，因此平移保持顶点奇偶）：
    /// <code>
    ///  0 East       ( +2,  0)   1 EastNorth  ( +3, +1)
    ///  2 NorthEast  ( +1, +1)   3 North      (  0, +2)
    ///  4 NorthWest  ( -1, +1)   5 WestNorth  ( -3, +1)
    ///  6 West       ( -2,  0)   7 WestSouth  ( -3, -1)
    ///  8 SouthWest  ( -1, -1)   9 South      (  0, -2)
    /// 10 SouthEast  ( +1, -1)  11 EastSouth  ( +3, -1)
    /// </code>
    /// 偶数方向（Edge 对齐）的平移量最大分量为 2、<strong>权重 1</strong>；
    /// 奇数方向（Corner/面朝）的平移量含 <c>|dx| = 3</c> 或 <c>|dy| = 2</c>、<strong>权重 2</strong>。
    /// 权重本身由 <see cref="Combat.PathCostRules.StepWeightUnits"/> 给出，本表不重复定义权重。
    ///
    /// 数值/顺序与偏移表一起属于版本化玩法规则：改变任一方向数值、顺序或偏移都会改变
    /// <c>BattleDefinitionHash</c> 并要求更新 <see cref="OrderVersion"/>。
    /// </summary>
    public static class GridNeighborTable
    {
        public const int DirectionCount = GridDirectionInfo.DirectionCount;

        /// <summary>规范邻居顺序/偏移表版本（进入 <c>BattleDefinitionHash</c>）。</summary>
        public const string OrderVersion = "grid-direction-0-to-11-v1";

        private static readonly int[] OffsetsX = { 2, 3, 1, 0, -1, -3, -2, -3, -1, 0, 1, 3 };
        private static readonly int[] OffsetsY = { 0, 1, 1, 2, 1, 1, 0, -1, -1, -2, -1, -1 };

        /// <summary>方向的 X 平移量（受检索引；越界以稳定参数异常拒绝，不静默回落）。</summary>
        public static int OffsetX(GridDirection direction)
        {
            int index = (int)direction;
            if (!GridDirectionInfo.IsValidIndex(index)) throw new System.ArgumentOutOfRangeException(nameof(direction));
            return OffsetsX[index];
        }

        /// <summary>方向的 Y 平移量。</summary>
        public static int OffsetY(GridDirection direction)
        {
            int index = (int)direction;
            if (!GridDirectionInfo.IsValidIndex(index)) throw new System.ArgumentOutOfRangeException(nameof(direction));
            return OffsetsY[index];
        }

        /// <summary>方向对应的单位偏移点（相对原点的平移量）。</summary>
        public static GridPoint OffsetPoint(GridDirection direction)
            => new GridPoint(OffsetX(direction), OffsetY(direction));

        /// <summary>按 <c>0 -> 11</c> 升序把 12 个方向与其偏移配对（规范枚举顺序）。</summary>
        public static GridDirection DirectionAt(int index)
        {
            if (!GridDirectionInfo.IsValidIndex(index)) throw new System.ArgumentOutOfRangeException(nameof(index));
            return (GridDirection)index;
        }

        /// <summary>
        /// 求 <paramref name="from"/> 到 <paramref name="to"/> 的规范单步方向。
        /// 只有<strong>恰好</strong>等于表中某个偏移才成立；否则返回 false（不是"最近方向"近似）。
        /// </summary>
        public static bool TryGetDirection(GridPoint from, GridPoint to, out GridDirection direction)
        {
            int dx = to.X - from.X;
            int dy = to.Y - from.Y;
            for (int i = 0; i < DirectionCount; i++)
            {
                if (OffsetsX[i] == dx && OffsetsY[i] == dy)
                {
                    direction = (GridDirection)i;
                    return true;
                }
            }
            direction = default;
            return false;
        }

        /// <summary>规范邻居的稳定枚举（严格按 <see cref="GridDirection"/> 数值升序）。</summary>
        public static void ForEachCanonicalDirection(System.Action<GridDirection> visit)
        {
            if (visit == null) return;
            for (int i = 0; i < DirectionCount; i++) visit((GridDirection)i);
        }
    }
}
