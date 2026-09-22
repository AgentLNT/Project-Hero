namespace ProjectHero.Logic.Grid
{
    /// <summary>
    /// 12 向离散方向。枚举数值为主方案冻结值，必须保持
    /// East = 0 → EastSouth = 11；偶数 = 顶点对齐（Edge）方向，奇数 = 面朝（Corner）方向。
    /// 数值与顺序属于版本化玩法规则并进入 BattleDefinitionHash。
    /// </summary>
    public enum GridDirection
    {
        East = 0,           // 0 deg
        EastNorth = 1,      // 30 deg
        NorthEast = 2,      // 60 deg
        North = 3,          // 90 deg
        NorthWest = 4,      // 120 deg
        WestNorth = 5,      // 150 deg
        West = 6,           // 180 deg
        WestSouth = 7,      // 210 deg
        SouthWest = 8,      // 240 deg
        South = 9,          // 270 deg
        SouthEast = 10,     // 300 deg
        EastSouth = 11      // 330 deg
    }

    public static class GridDirectionInfo
    {
        public const int DirectionCount = 12;

        public static bool IsEven(GridDirection direction) => (((int)direction & 1) == 0);

        public static bool IsValidIndex(int index) => index >= 0 && index < DirectionCount;
    }
}
