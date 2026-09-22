namespace ProjectHero.Logic.Grid
{
    /// <summary>
    /// 方向几何定义的稳定错误码（主方案 2.3.1 冻结部分）。
    /// </summary>
    public static class DirectionalCodes
    {
        public const string DIRECTIONAL_BASE_MISSING = "DIRECTIONAL_BASE_MISSING";
        public const string TRIANGLE_POINT_INVALID = "TRIANGLE_POINT_INVALID";
        public const string DIRECTIONAL_EXPANSION_OVERFLOW = "DIRECTIONAL_EXPANSION_OVERFLOW";
        public const string DIRECTIONAL_TABLE_INCOMPLETE = "DIRECTIONAL_TABLE_INCOMPLETE";
        public const string DIRECTIONAL_TABLE_NOT_CANONICAL = "DIRECTIONAL_TABLE_NOT_CANONICAL";
        public const string DIRECTIONAL_TABLE_MISMATCH = "DIRECTIONAL_TABLE_MISMATCH";
    }
}
