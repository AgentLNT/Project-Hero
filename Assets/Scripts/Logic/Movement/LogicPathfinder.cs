using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Movement
{
    /// <summary>
    /// 一次路径搜索的<strong>规范结果</strong>（任务包「必须产出」12）。
    /// 成功时携带完整路径（含起点与终点）、边数与<strong>路径权重单位</strong>；
    /// 失败时携带冻结的五类稳定失败码之一。
    ///
    /// <see cref="EdgeCount"/> 只用于 <c>MovementSegment</c> 的数量/索引；
    /// 时长与预算成本<strong>只</strong>用 <see cref="PathWeightUnits"/>。
    /// </summary>
    public sealed record PathSearchResult(
        bool Succeeded,
        string FailureCode,
        IReadOnlyList<GridPoint> Path,
        int EdgeCount,
        int PathWeightUnits,
        int ExpandedNodes,
        bool FailureRecordedDuringSearch)
    {
        public static PathSearchResult Failed(string code, int expandedNodes = 0)
            => new PathSearchResult(false, code, Array.Empty<GridPoint>(), 0, 0, expandedNodes, false);

        public static PathSearchResult FailedAtPreflight(string code)
            => new PathSearchResult(false, code, Array.Empty<GridPoint>(), 0, 0, 0, false);

        public GridPoint Start => Path.Count > 0 ? Path[0] : default;

        public GridPoint Destination => Path.Count > 0 ? Path[Path.Count - 1] : default;

        public override string ToString()
            => Succeeded
                ? "path(edges=" + EdgeCount.ToString(CultureInfo.InvariantCulture) +
                  ", weight=" + PathWeightUnits.ToString(CultureInfo.InvariantCulture) +
                  ", expanded=" + ExpandedNodes.ToString(CultureInfo.InvariantCulture) + ")"
                : "path(" + FailureCode + ", expanded=" + ExpandedNodes.ToString(CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>
    /// <strong>唯一纯逻辑 Pathfinder</strong>（任务包「必须产出」12）。
    ///
    /// 冻结协议：
    /// <list type="bullet">
    /// <item>邻居<strong>只</strong>来自 <see cref="LogicGrid.GetNeighborsOrdered"/> 的规范序列
    /// （<see cref="GridDirection"/> <c>0 -> 11</c>）；</item>
    /// <item>边权来自 <see cref="PathCostRules.StepWeightUnits"/>（偶 1 / 奇 2），
    /// <strong>不</strong>读单位速度、动画距离或世界坐标；</item>
    /// <item>启发 <c>h = dy + max(0, (dx - dy) / 2)</c>，坐标差先扩展为 checked <c>long</c>；</item>
    /// <item><c>g/h/f</c> 全部 checked <c>long</c>；开放集按
    /// <c>(f, h, GridPoint.X, GridPoint.Y)</c> 升序取节点，<strong>没有</strong> <c>g</c> 或
    /// 发现序号末级键；</item>
    /// <item>相同坐标只有<strong>更小</strong>的 <c>g</c> 才更新；相同 <c>g</c> 保留已有父节点；</item>
    /// <item>只在 Encounter 边界内搜索；三个上限（4096/256/192）都是包含式边界；
    /// 节点上限在目标检查与邻居展开<strong>之前</strong>判定。</item>
    /// </list>
    /// </summary>
    public sealed class LogicPathfinder
    {
        private readonly LogicGrid _grid;
        private readonly PathCostRules _costRules;
        private readonly PathSearchRules _searchRules;

        public LogicPathfinder(LogicGrid grid, PathCostRules costRules, PathSearchRules searchRules)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _costRules = costRules ?? throw new ArgumentNullException(nameof(costRules));
            _searchRules = searchRules ?? throw new ArgumentNullException(nameof(searchRules));
        }

        public LogicGrid Grid => _grid;

        public PathCostRules CostRules => _costRules;

        public PathSearchRules SearchRules => _searchRules;

        /// <summary>
        /// 为一个移动计划搜索路径。<paramref name="actionPlanId"/> 用于忽略该计划自己的
        /// Reservation（自己的未来预留不阻塞自己）；传 <c>default</c> 表示不忽略任何预留。
        /// </summary>
        public PathSearchResult FindPath(GridPoint start, GridPoint destination, UnitId mover, ActionPlanId actionPlanId = default)
        {
            // —— 预检（起点优先；两者同时非法时起点胜出，绝不进入搜索后伪装无路）——
            if (!IsTraversable(start, mover, actionPlanId))
                return PathSearchResult.FailedAtPreflight(PathSearchCodes.PATH_INVALID_START);
            if (!IsTraversable(destination, mover, actionPlanId))
                return PathSearchResult.FailedAtPreflight(PathSearchCodes.PATH_INVALID_DESTINATION);

            var open = new SortedSet<OpenNode>(OpenNodeComparer.Instance);
            var bestG = new Dictionary<GridPoint, long>();
            var edgeCount = new Dictionary<GridPoint, int>();
            var weight = new Dictionary<GridPoint, long>();
            var parent = new Dictionary<GridPoint, GridPoint>();

            long startH = _costRules.HeuristicWeightUnits(start, destination);
            bestG[start] = 0L;
            edgeCount[start] = 0;
            weight[start] = 0L;
            open.Add(new OpenNode(startH, startH, start.X, start.Y));

            int expandedNodes = 0;
            bool sawEdgeLimit = false;
            bool sawWeightLimit = false;

            while (open.Count > 0)
            {
                OpenNode current = open.Min;
                open.Remove(current);
                var point = new GridPoint(current.X, current.Y);

                bestG.TryGetValue(point, out long knownG);
                long nodeG = current.F - current.H;
                if (nodeG > knownG) continue;   // 过期队列项：不计数、不展开

                long expanded;
                try
                {
                    expanded = checked((long)expandedNodes + 1L);
                }
                catch (OverflowException)
                {
                    return PathSearchResult.Failed(PathSearchCodes.PATH_COST_OVERFLOW, expandedNodes);
                }
                if (_searchRules.ExceedsNodeLimit(expanded))
                    return PathSearchResult.Failed(PathSearchCodes.PATH_SEARCH_NODE_LIMIT_EXCEEDED, expandedNodes);
                expandedNodes = (int)expanded;

                if (point.Equals(destination))
                    return BuildSuccess(point, parent, edgeCount, weight, expandedNodes);

                IReadOnlyList<GridPoint> neighbors = _grid.GetNeighborsOrdered(point);
                for (int i = 0; i < neighbors.Count; i++)
                {
                    GridPoint neighbor = neighbors[i];
                    // 边界已由 GetNeighborsOrdered 保证；这里只判占位/Reservation 阻塞。
                    if (IsBlocked(neighbor, mover, actionPlanId)) continue;

                    GridDirection direction;
                    if (!GridNeighborTable.TryGetDirection(point, neighbor, out direction))
                    {
                        // 规范邻居必然对应某个方向；出现反例说明邻居表被破坏 ⇒ 整体失败。
                        return PathSearchResult.Failed(PathSearchCodes.PATH_COST_OVERFLOW, expandedNodes);
                    }

                    long stepWeight = _costRules.StepWeightUnits(direction);
                    long candidateG;
                    long candidateWeight;
                    long candidateF;
                    int candidateEdges;
                    try
                    {
                        candidateG = checked(nodeG + stepWeight);
                        candidateWeight = checked(weight[point] + stepWeight);
                        candidateEdges = checked(edgeCount[point] + 1);
                        candidateF = checked(candidateG + _costRules.HeuristicWeightUnits(neighbor, destination));
                    }
                    catch (OverflowException)
                    {
                        // checked 溢出立即终止整个搜索（全局优先级最高）。
                        return PathSearchResult.Failed(PathSearchCodes.PATH_COST_OVERFLOW, expandedNodes);
                    }

                    // 先算 EdgeCount / PathWeight，再按包含式边界丢弃该候选（记录原因，其他分支继续）。
                    if (_searchRules.ExceedsEdgeLimit(candidateEdges))
                    {
                        sawEdgeLimit = true;
                        continue;
                    }
                    if (_searchRules.ExceedsWeightLimit(candidateWeight))
                    {
                        sawWeightLimit = true;
                        continue;
                    }

                    long neighborH = candidateF - candidateG;
                    if (bestG.TryGetValue(neighbor, out long existing))
                    {
                        if (candidateG >= existing) continue;   // 相同 g 保留已有父节点
                    }

                    bestG[neighbor] = candidateG;
                    edgeCount[neighbor] = candidateEdges;
                    weight[neighbor] = candidateWeight;
                    parent[neighbor] = point;
                    open.Add(new OpenNode(candidateF, neighborH, neighbor.X, neighbor.Y));
                }
            }

            // 开放集耗尽：按冻结优先级 EDGE -> WEIGHT -> NOT_FOUND 选择已记录的最高原因。
            if (sawEdgeLimit) return PathSearchResult.Failed(PathSearchCodes.PATH_EDGE_LIMIT_EXCEEDED, expandedNodes);
            if (sawWeightLimit) return PathSearchResult.Failed(PathSearchCodes.PATH_WEIGHT_LIMIT_EXCEEDED, expandedNodes);
            return PathSearchResult.Failed(PathSearchCodes.PATH_NOT_FOUND, expandedNodes);
        }

        /// <summary>起点/终点是否可通行：必须在 Encounter 边界内、且不被其他单位/其他计划占用。</summary>
        private bool IsTraversable(GridPoint point, UnitId mover, ActionPlanId actionPlanId)
            => _grid.IsLegalGridPoint(point) && !IsBlocked(point, mover, actionPlanId);

        private bool IsBlocked(GridPoint cell, UnitId mover, ActionPlanId actionPlanId)
        {
            if (_grid.IsCellBlockedFor(cell, mover)) return true;
            Reservation reservation = _grid.ReservationAt(cell);
            if (reservation == null) return false;
            if (reservation.UnitId.Value == mover.Value) return false;
            if (actionPlanId.IsValid && reservation.ActionPlanId.Value == actionPlanId.Value) return false;
            return true;
        }

        private static PathSearchResult BuildSuccess(
            GridPoint destination,
            Dictionary<GridPoint, GridPoint> parent,
            Dictionary<GridPoint, int> edgeCount,
            Dictionary<GridPoint, long> weight,
            int expandedNodes)
        {
            var path = new List<GridPoint> { destination };
            GridPoint cursor = destination;
            while (parent.TryGetValue(cursor, out GridPoint previous))
            {
                path.Add(previous);
                cursor = previous;
            }
            path.Reverse();

            long totalWeight = weight[destination];
            if (totalWeight > int.MaxValue)
                return PathSearchResult.Failed(PathSearchCodes.PATH_COST_OVERFLOW, expandedNodes);

            return new PathSearchResult(
                true, null, path, edgeCount[destination], (int)totalWeight, expandedNodes, false);
        }

        /// <summary>开放集节点：<c>(f, h, X, Y)</c>。没有 <c>g</c> 或发现序号末级键。</summary>
        private readonly struct OpenNode
        {
            public OpenNode(long f, long h, int x, int y)
            {
                F = f;
                H = h;
                X = x;
                Y = y;
            }

            public readonly long F;
            public readonly long H;
            public readonly int X;
            public readonly int Y;
        }

        /// <summary>
        /// 开放集排序：<c>f</c> → <c>h</c> → <c>GridPoint.X</c> → <c>GridPoint.Y</c> 严格升序。
        /// 相同坐标的两个条目必然具有相同的 <c>g</c>（因为 <c>g = f - h</c>），因此"相等即同一状态"，
        /// 去重不引入任何未声明的平局规则。
        /// </summary>
        private sealed class OpenNodeComparer : IComparer<OpenNode>
        {
            public static readonly OpenNodeComparer Instance = new OpenNodeComparer();

            public int Compare(OpenNode a, OpenNode b)
            {
                int byF = a.F.CompareTo(b.F);
                if (byF != 0) return byF;
                int byH = a.H.CompareTo(b.H);
                if (byH != 0) return byH;
                int byX = a.X.CompareTo(b.X);
                if (byX != 0) return byX;
                return a.Y.CompareTo(b.Y);
            }
        }
    }
}
