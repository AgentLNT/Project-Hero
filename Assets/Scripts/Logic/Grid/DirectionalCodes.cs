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
        /// <summary>
        /// 任务 02B 冻结：旧资产显式序列化的非基准方向与受检整数展开结果逐点不一致。
        /// 语义与 <see cref="DIRECTIONAL_TABLE_MISMATCH"/> 相同，是本任务包指定的正式名称；
        /// 两者都表示"整体拒绝，不得用加载顺序决定覆盖结果"。
        /// </summary>
        public const string DIRECTIONAL_EXPLICIT_DATA_MISMATCH = "DIRECTIONAL_EXPLICIT_DATA_MISMATCH";
    }
}
