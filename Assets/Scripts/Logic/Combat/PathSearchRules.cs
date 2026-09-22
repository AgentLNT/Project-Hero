namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 路径搜索规则（主方案 3.8 / 00 号规则 32）。首版冻结上限：
    /// MaxExpandedNodes = 4096、MaxPathWeightUnits = 256、MaxPathEdges = 192。
    /// 三个上限均为包含式边界（恰好等于上限合法，严格大于才失败）；
    /// 三项必须为正。搜索空间由 Encounter GridBoundary 唯一限定，无独立坐标距离上限。
    /// Pathfinder 本身由任务 06 实现；本任务只冻结数据契约、稳定失败码与哈希字段。
    /// </summary>
    public sealed record PathSearchRules(
        int MaxExpandedNodes,
        int MaxPathWeightUnits,
        int MaxPathEdges)
    {
        public const int FrozenMaxExpandedNodes = 4096;
        public const int FrozenMaxPathWeightUnits = 256;
        public const int FrozenMaxPathEdges = 192;

        public static readonly PathSearchRules FrozenV1 = new(
            FrozenMaxExpandedNodes, FrozenMaxPathWeightUnits, FrozenMaxPathEdges);

        public string Validate()
            => MaxExpandedNodes <= 0 || MaxPathWeightUnits <= 0 || MaxPathEdges <= 0
                ? PathSearchCodes.PATH_SEARCH_LIMIT_INVALID : null;

        /// <summary>包含式边界：严格大于上限才失败。</summary>
        public bool ExceedsNodeLimit(long candidate) => candidate > MaxExpandedNodes;

        public bool ExceedsWeightLimit(long candidate) => candidate > MaxPathWeightUnits;

        public bool ExceedsEdgeLimit(long candidate) => candidate > MaxPathEdges;

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("path_search.max_expanded_nodes", MaxExpandedNodes);
            writer.Write("path_search.max_path_weight_units", MaxPathWeightUnits);
            writer.Write("path_search.max_path_edges", MaxPathEdges);
        }
    }

    /// <summary>
    /// 路径搜索稳定失败码（主方案 3.8 冻结优先级：
    /// PATH_COST_OVERFLOW > PATH_SEARCH_NODE_LIMIT_EXCEEDED > PATH_EDGE_LIMIT_EXCEEDED
    /// > PATH_WEIGHT_LIMIT_EXCEEDED > PATH_NOT_FOUND；
    /// 预检 PATH_INVALID_START > PATH_INVALID_DESTINATION）。任务 06 实现。
    /// </summary>
    public static class PathSearchCodes
    {
        public const string PATH_SEARCH_LIMIT_INVALID = "PATH_SEARCH_LIMIT_INVALID";
        public const string PATH_INVALID_START = "PATH_INVALID_START";
        public const string PATH_INVALID_DESTINATION = "PATH_INVALID_DESTINATION";
        public const string PATH_COST_OVERFLOW = "PATH_COST_OVERFLOW";
        public const string PATH_SEARCH_NODE_LIMIT_EXCEEDED = "PATH_SEARCH_NODE_LIMIT_EXCEEDED";
        public const string PATH_EDGE_LIMIT_EXCEEDED = "PATH_EDGE_LIMIT_EXCEEDED";
        public const string PATH_WEIGHT_LIMIT_EXCEEDED = "PATH_WEIGHT_LIMIT_EXCEEDED";
        public const string PATH_NOT_FOUND = "PATH_NOT_FOUND";
    }
}
