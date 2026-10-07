using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Interactions
{
    /// <summary>强制位移求解的稳定拒绝码（不改名、不复用既有码）。</summary>
    public static class ForcedDisplacementCodes
    {
        /// <summary>请求本身非法（朝向越界、步数为负等）。</summary>
        public const string DISPLACEMENT_REQUEST_INVALID = "STEP_DISPLACEMENT_REQUEST_INVALID";

        /// <summary>请求指向未注册进 <c>LogicGrid</c> 的单位。</summary>
        public const string DISPLACEMENT_UNIT_UNKNOWN = "STEP_DISPLACEMENT_UNIT_UNKNOWN";

        /// <summary>只读快照与网格权威锚点分叉（求解必须只读同一时刻的状态）。</summary>
        public const string DISPLACEMENT_SNAPSHOT_MISMATCH = "STEP_DISPLACEMENT_SNAPSHOT_MISMATCH";
    }

    /// <summary>
    /// <strong><c>ForcedDisplacementSolver</c></strong>（任务包「必须产出」19 +「同时强制位移协议 · 同时求解算法」）。
    ///
    /// 它只做四件事：
    /// <list type="number">
    /// <item>读 <strong>不可变</strong>的 <see cref="LogicGrid"/> 公开只读面一次，把占位复制进
    /// <strong>私有临时空间</strong>；此后全部中间状态只写在私有结构里；</item>
    /// <item>按"一步一轮"同时求解本 Tick 全部请求：每轮所有仍活动的单位同时提出下一 anchor
    /// 与<strong>完整</strong>目标 footprint；</item>
    /// <item>按冻结次序判停：越界 → 静态障碍 → 同锚点争抢 → 体积重叠 → 依赖图（循环 / 静止 / 失败）
    /// —— 全程<strong>没有赢家</strong>，绝不按 UnitId / MomentumUnits / ConflictGroupKey / 输入顺序选人；</item>
    /// <item>最终 footprint 确定后，只<strong>判定</strong>失效计划候选（Reservation 抢占 + 旧起点失效），
    /// 不提交任何终态、不写世界、不发事件。</item>
    /// </list>
    ///
    /// 明确<strong>不</strong>做（任务包 :412 / 不变量 33）：不调用 <c>LogicPathfinder</c>、
    /// <c>MoveTimingSpec</c>、<c>MovementSegment</c> 推进、<c>TryReserve</c> 或普通 Reservation 竞争；
    /// 不隐式连锁推人；不把临时步写进真实 <c>LogicGrid</c>；不修改单位朝向。
    ///
    /// <strong>排列不变性</strong>：内部先按 <see cref="UnitId"/> 升序规范化请求，之后每一步的
    /// 判停、依赖图、SCC、原因分类与输出排序全部只依赖 <see cref="UnitId"/> / <see cref="GridPoint"/> /
    /// 枚举数值，<strong>不</strong>读取任何容器枚举顺序（依赖图用按 <c>UnitId</c> 升序的数组 + 索引，
    /// 不用 <c>HashSet</c>/<c>Dictionary</c> 的枚举顺序做决策）。
    /// </summary>
    public sealed class ForcedDisplacementSolver : IForcedDisplacementSolver
    {
        private readonly LogicGrid _grid;
        private readonly ActionScheduleAuthority _authority;
        private readonly DodgeRelocationAuthority _dodgeReservations;
        private readonly ForcedDisplacementStats _stats;
        private readonly HashSet<GridPoint> _staticObstacles;

        /// <param name="grid">本场唯一的空间权威（只读消费）。</param>
        /// <param name="authority">
        /// 计划权威（可选）。给出时用于"实际被换位单位依赖旧起点的活动/排队 Move 计划"判定；
        /// 为 <c>null</c> 时该来源为空（但"最终 footprint 相交 Reservation"仍可判定）。
        /// </param>
        /// <param name="dodgeReservations">
        /// 未来 Dodge 目的格预留来源（可选）。任务包 :207 要求把它们也纳入最终 footprint 的抢占范围；
        /// Dodge 目的格预留<strong>不</strong>存在 <c>LogicGrid</c> 的 Reservation 表里，必须单独读。
        /// </param>
        /// <param name="staticObstacles">
        /// 静态障碍格（可选）。基线 <c>LogicGrid</c> 没有地形障碍模型，因此障碍来源必须显式给出；
        /// 未给出 = 无障碍（不是"障碍规则不存在"）。障碍只判格级 footprint 点。
        /// </param>
        /// <param name="stats">可选的只读性能计数器（绝不进哈希）。</param>
        public ForcedDisplacementSolver(
            LogicGrid grid,
            ActionScheduleAuthority authority = null,
            DodgeRelocationAuthority dodgeReservations = null,
            IReadOnlyList<GridPoint> staticObstacles = null,
            ForcedDisplacementStats stats = null)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _authority = authority;
            _dodgeReservations = dodgeReservations;
            _stats = stats;
            _staticObstacles = staticObstacles == null
                ? new HashSet<GridPoint>()
                : new HashSet<GridPoint>(staticObstacles);
        }

        /// <summary>性能统计（装配方持有同一实例；<c>null</c> = 不采样）。</summary>
        public ForcedDisplacementStats Stats => _stats;

        public ForcedDisplacementBatch ResolveAll(
            IReadOnlyList<ForcedDisplacementRequest> requests,
            IReadOnlyList<UnitSnapshot> immutableUnitSnapshot,
            GridBoundaryDefinition boundary,
            long tick)
        {
            if (_stats != null) _stats.ResetBatch();
            if (requests == null || requests.Count == 0) return ForcedDisplacementBatch.Empty;

            // ——— 0. 规范化排序：内部唯一顺序只由 UnitId 决定（与入参排列无关）———
            var ordered = new List<ForcedDisplacementRequest>(requests.Count);
            for (int i = 0; i < requests.Count; i++)
            {
                if (requests[i] != null) ordered.Add(requests[i]);
            }
            if (ordered.Count == 0) return ForcedDisplacementBatch.Empty;
            ordered.Sort((a, b) => a.TargetUnitId.Value.CompareTo(b.TargetUnitId.Value));

            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i - 1].TargetUnitId == ordered[i].TargetUnitId)
                {
                    throw new LogicDefinitionException(SimulationCodes.STEP_DISPLACEMENT_REQUEST_DUPLICATE,
                        ordered[i].TargetUnitId.Value.ToString(CultureInfo.InvariantCulture));
                }
            }

            // ——— 1. 私有临时占位空间（读只读网格一次；此后不再依赖网格的可变状态）———
            var space = new TempSpace();
            space.Load(_grid);

            var proposals = new Proposal[ordered.Count];
            int maxSteps = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                ForcedDisplacementRequest request = ordered[i];
                if (!space.TryGetUnit(request.TargetUnitId, out TempUnit unit))
                {
                    throw new LogicDefinitionException(ForcedDisplacementCodes.DISPLACEMENT_UNIT_UNKNOWN,
                        request.TargetUnitId.Value.ToString(CultureInfo.InvariantCulture));
                }
                if (!IsCanonicalDirection(request.Direction))
                {
                    throw new LogicDefinitionException(ForcedDisplacementCodes.DISPLACEMENT_REQUEST_INVALID,
                        "direction=" + ((int)request.Direction).ToString(CultureInfo.InvariantCulture));
                }
                if (request.RequestedSteps < 0)
                {
                    throw new LogicDefinitionException(ForcedDisplacementCodes.DISPLACEMENT_REQUEST_INVALID,
                        "steps=" + request.RequestedSteps.ToString(CultureInfo.InvariantCulture));
                }

                VerifySnapshotAgreement(request.TargetUnitId, unit.Anchor, immutableUnitSnapshot);

                proposals[i] = new Proposal
                {
                    RequestIndex = i,
                    UnitId = request.TargetUnitId,
                    Direction = request.Direction,
                    Facing = unit.Facing,
                    RequestedSteps = request.RequestedSteps,
                    Origin = unit.Anchor,
                    Anchor = unit.Anchor,
                    AppliedSteps = 0,
                    // RequestedSteps <= 0 不产生位移；它仍留下一条 Completed 的规范结果。
                    StopReason = ForcedDisplacementStopReason.Completed
                };
                if (request.RequestedSteps > maxSteps) maxSteps = request.RequestedSteps;
            }

            if (_stats != null)
            {
                _stats.RequestCount = ordered.Count;
                _stats.MaxRequestedSteps = maxSteps;
                _stats.TotalRequestCount += ordered.Count;
            }

            // ——— 2. 一步一轮 · 同时求解 ———
            for (int round = 1; round <= maxSteps; round++)
            {
                var active = new List<int>(proposals.Length);
                for (int i = 0; i < proposals.Length; i++)
                {
                    Proposal proposal = proposals[i];
                    if (proposal.Failed) continue;
                    if (proposal.AppliedSteps >= proposal.RequestedSteps) continue;
                    active.Add(i);
                }
                if (active.Count == 0) break;
                if (_stats != null) _stats.RoundCount = round;

                // (a) 全部活动单位同时提出下一 anchor 与完整目标 footprint。
                for (int a = 0; a < active.Count; a++) ProposeNext(proposals[active[a]], space);

                // (b) 先边界，再静态障碍（步内固定次序）。
                for (int a = 0; a < active.Count; a++)
                {
                    Proposal proposal = proposals[active[a]];
                    if (IsOutOfBoundary(proposal, boundary)) Fail(proposal, ForcedDisplacementStopReason.Boundary);
                    else if (TouchesStaticObstacle(proposal)) Fail(proposal, ForcedDisplacementStopReason.StaticObstacle);
                }

                // (c) 同锚点争抢 → 全部失败；其余提案两两 footprint 相交 → 全部 VolumeOverlap。
                ResolveContention(proposals, active);

                // (d)-(f) 依赖图：循环 → DependencyCycle；静止/走完/本步失败 → OccupiedUnit。
                ResolveDependencies(proposals, active, space);

                // (g) 本步全部成功提案**同时**写入临时空间；(h) 失败单位冻结并停止后续全部步。
                for (int a = 0; a < active.Count; a++)
                {
                    Proposal proposal = proposals[active[a]];
                    if (proposal.Failed) continue;
                    space.Move(proposal.UnitId, proposal.TargetAnchor, proposal.TargetCells, proposal.TargetTriangles);
                    proposal.Anchor = proposal.TargetAnchor;
                    proposal.AppliedSteps++;
                    if (proposal.AppliedSteps >= proposal.RequestedSteps)
                        proposal.StopReason = ForcedDisplacementStopReason.Completed;
                }
            }

            // ——— 3. 从临时空间生成结果（按 TargetUnitId 升序）———
            var resolutions = new List<ForcedDisplacementResolution>(proposals.Length);
            for (int i = 0; i < proposals.Length; i++)
            {
                Proposal proposal = proposals[i];
                bool completed = proposal.AppliedSteps >= proposal.RequestedSteps;
                ForcedDisplacementStopReason reason = completed
                    ? ForcedDisplacementStopReason.Completed
                    : proposal.StopReason;

                IReadOnlyList<ActionPlanId> invalidated =
                    proposal.AppliedSteps > 0 ? CollectInvalidatedPlans(proposal) : Array.Empty<ActionPlanId>();

                resolutions.Add(new ForcedDisplacementResolution(
                    proposal.UnitId, proposal.Origin, proposal.Anchor,
                    proposal.RequestedSteps, proposal.AppliedSteps, reason, invalidated));
            }

            ForcedDisplacementBatch batch = ForcedDisplacementBatch.FromResolutions(resolutions);
            if (_stats != null)
            {
                _stats.ResolutionCount = batch.Resolutions.Count;
                _stats.RelocationCount = batch.Relocations.Count;
                _stats.TotalRelocationCount += batch.Relocations.Count;
            }
            return batch;
        }

        // ————————————————————————————————————————————————————————————
        // 每轮：提案 / 判停 / 依赖图
        // ————————————————————————————————————————————————————————————

        private void ProposeNext(Proposal proposal, TempSpace space)
        {
            int dx = GridNeighborTable.OffsetX(proposal.Direction);
            int dy = GridNeighborTable.OffsetY(proposal.Direction);
            // 每轮只应用一次规范方向增量：偶数/奇数方向的 PathWeight 差异**不**参与位移。
            var target = new GridPoint(checked(proposal.Anchor.X + dx), checked(proposal.Anchor.Y + dy));
            proposal.TargetAnchor = target;
            // 平移**当前朝向**的 footprint：不旋转、不改 Facing。
            proposal.TargetCells = _grid.ResolveDestinationCells(proposal.UnitId, target, proposal.Facing);
            proposal.TargetTriangles = _grid.ResolveDestinationTriangles(proposal.UnitId, target, proposal.Facing);
        }

        private bool IsOutOfBoundary(Proposal proposal, GridBoundaryDefinition boundary)
        {
            if (!boundary.Contains(proposal.TargetAnchor)) return true;
            IReadOnlyList<GridPoint> cells = proposal.TargetCells;
            for (int i = 0; i < cells.Count; i++)
            {
                if (_stats != null) _stats.FootprintPointCheckCount++;
                if (!boundary.Contains(cells[i])) return true;
            }
            return false;
        }

        private bool TouchesStaticObstacle(Proposal proposal)
        {
            if (_staticObstacles.Count == 0) return false;
            IReadOnlyList<GridPoint> cells = proposal.TargetCells;
            for (int i = 0; i < cells.Count; i++)
            {
                if (_stats != null) _stats.FootprintPointCheckCount++;
                if (_staticObstacles.Contains(cells[i])) return true;
            }
            return false;
        }

        /// <summary>
        /// 同锚点争抢（<see cref="ForcedDisplacementStopReason.DestinationContention"/>）先于
        /// 不同锚点的体积重叠（<see cref="ForcedDisplacementStopReason.VolumeOverlap"/>）；
        /// 两类都<strong>没有赢家</strong>（任务包 :160-161、:413）。
        ///
        /// 分组用"按 UnitId 升序的提案数组 + 逐对比较"，不依赖任何哈希容器枚举顺序。
        /// </summary>
        private static void ResolveContention(Proposal[] proposals, List<int> active)
        {
            for (int a = 0; a < active.Count; a++)
            {
                Proposal left = proposals[active[a]];
                if (left.Failed) continue;
                for (int b = a + 1; b < active.Count; b++)
                {
                    Proposal right = proposals[active[b]];
                    if (right.Failed) continue;
                    if (left.TargetAnchor == right.TargetAnchor)
                    {
                        Fail(left, ForcedDisplacementStopReason.DestinationContention);
                        Fail(right, ForcedDisplacementStopReason.DestinationContention);
                    }
                }
            }

            for (int a = 0; a < active.Count; a++)
            {
                Proposal left = proposals[active[a]];
                if (left.Failed) continue;
                for (int b = a + 1; b < active.Count; b++)
                {
                    Proposal right = proposals[active[b]];
                    if (right.Failed) continue;
                    if (!FootprintsIntersect(left, right)) continue;
                    Fail(left, ForcedDisplacementStopReason.VolumeOverlap);
                    Fail(right, ForcedDisplacementStopReason.VolumeOverlap);
                }
            }
        }

        private static bool FootprintsIntersect(Proposal left, Proposal right)
        {
            IReadOnlyList<GridPoint> leftCells = left.TargetCells;
            IReadOnlyList<GridPoint> rightCells = right.TargetCells;
            for (int i = 0; i < leftCells.Count; i++)
            {
                for (int j = 0; j < rightCells.Count; j++)
                {
                    if (leftCells[i] == rightCells[j]) return true;
                }
            }
            IReadOnlyList<TrianglePoint> leftTriangles = left.TargetTriangles;
            IReadOnlyList<TrianglePoint> rightTriangles = right.TargetTriangles;
            for (int i = 0; i < leftTriangles.Count; i++)
            {
                for (int j = 0; j < rightTriangles.Count; j++)
                {
                    if (leftTriangles[i] == rightTriangles[j]) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 依赖图判定（任务包 :182-186）：
        /// 提案指向其<strong>目标 footprint 当前占据者</strong>（忽略自身旧 footprint）；
        /// 先标记强连通分量（交换/任意循环 ⇒ <see cref="ForcedDisplacementStopReason.DependencyCycle"/>），
        /// 再把依赖静止单位、已走完单位或本步其他失败提案的节点归为
        /// <see cref="ForcedDisplacementStopReason.OccupiedUnit"/>；
        /// 只有"所有依赖分支最终都到达成功空格提案"的无环链/DAG 才能移动。
        /// </summary>
        private void ResolveDependencies(Proposal[] proposals, List<int> active, TempSpace space)
        {
            // 节点的稳定身份 = 提案下标；owner UnitId → 提案下标 用升序线性查找，不用字典枚举顺序。
            var nodes = new List<int>(active.Count);
            for (int a = 0; a < active.Count; a++)
            {
                Proposal proposal = proposals[active[a]];
                proposal.Dependencies = Array.Empty<int>();
                proposal.State = ProposalState.Unknown;
                if (proposal.Failed) continue;
                nodes.Add(active[a]);
            }
            if (nodes.Count == 0) return;

            for (int n = 0; n < nodes.Count; n++)
            {
                Proposal proposal = proposals[nodes[n]];
                var deps = new List<int>();
                bool external = false;
                IReadOnlyList<GridPoint> cells = proposal.TargetCells;
                for (int c = 0; c < cells.Count; c++)
                {
                    if (_stats != null) _stats.FootprintPointCheckCount++;
                    if (!space.TryGetOwner(cells[c], out long owner)) continue;
                    if (owner == proposal.UnitId.Value) continue;      // 忽略自身旧 footprint
                    int index = IndexOfUnit(proposals, nodes, owner);
                    // 占据者不在本步提案集合里 ⇒ 静止单位 / 已走完单位 / 本步失败的提案。
                    // 它是<strong>硬阻挡</strong>，必须显式记录：静默忽略会让位移单位直接穿进它。
                    if (index < 0) { external = true; continue; }
                    if (!Contains(deps, index)) deps.Add(index);
                }
                IReadOnlyList<TrianglePoint> triangles = proposal.TargetTriangles;
                for (int c = 0; c < triangles.Count; c++)
                {
                    if (_stats != null) _stats.FootprintPointCheckCount++;
                    if (!space.TryGetTriangleOwner(triangles[c], out long owner)) continue;
                    if (owner == proposal.UnitId.Value) continue;
                    int index = IndexOfUnit(proposals, nodes, owner);
                    if (index < 0) { external = true; continue; }
                    if (!Contains(deps, index)) deps.Add(index);
                }
                deps.Sort();
                proposal.Dependencies = deps.ToArray();
                proposal.HasExternalDependency = external;
                if (_stats != null)
                {
                    _stats.DependencyNodeCount++;
                    _stats.DependencyEdgeCount += proposal.Dependencies.Length + (external ? 1 : 0);
                }
            }

            // 循环预标记：节点能沿依赖边回到自身即属于某个循环（单元/三元位置循环都覆盖）。
            for (int n = 0; n < nodes.Count; n++)
            {
                int node = nodes[n];
                if (Reaches(proposals, nodes, node, node)) Fail(proposals[node], ForcedDisplacementStopReason.DependencyCycle);
            }

            // 不动点：无依赖 ⇒ 成功；依赖非提案（静止/走完）或失败提案 ⇒ OccupiedUnit；全部成功 ⇒ 成功。
            // 循环预标记后剩余子图是无环图，因此该不动点必定收敛。
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int n = 0; n < nodes.Count; n++)
                {
                    Proposal proposal = proposals[nodes[n]];
                    if (proposal.State != ProposalState.Unknown) continue;

                    int[] deps = proposal.Dependencies;
                    bool blocked = proposal.HasExternalDependency;
                    bool pending = false;
                    for (int d = 0; d < deps.Length && !blocked; d++)
                    {
                        Proposal dependency = proposals[deps[d]];
                        if (dependency.State == ProposalState.Failed) { blocked = true; break; }
                        if (dependency.State == ProposalState.Unknown) { pending = true; }
                    }

                    if (blocked)
                    {
                        Fail(proposal, ForcedDisplacementStopReason.OccupiedUnit);
                        changed = true;
                    }
                    else if (!pending)
                    {
                        proposal.State = ProposalState.Succeeded;
                        changed = true;
                    }
                }
            }

            // 结构上不可达：循环预标记保证剩余子图无环，不动点必然判定全部节点。
            // 保留显式兜底而不是静默成功——未判定的提案绝不允许移动。
            for (int n = 0; n < nodes.Count; n++)
            {
                Proposal proposal = proposals[nodes[n]];
                if (proposal.State == ProposalState.Unknown)
                    Fail(proposal, ForcedDisplacementStopReason.OccupiedUnit);
            }
        }

        private static int IndexOfUnit(Proposal[] proposals, List<int> nodes, long unitId)
        {
            for (int n = 0; n < nodes.Count; n++)
            {
                if (proposals[nodes[n]].UnitId.Value == unitId) return nodes[n];
            }
            return -1;
        }

        private static bool Contains(List<int> values, int value)
        {
            for (int i = 0; i < values.Count; i++) if (values[i] == value) return true;
            return false;
        }

        /// <summary>沿依赖边从 <paramref name="from"/> 出发能否到达 <paramref name="target"/>。</summary>
        private static bool Reaches(Proposal[] proposals, List<int> nodes, int from, int target)
        {
            var visited = new bool[proposals.Length];
            var stack = new Stack<int>();
            int[] first = proposals[from].Dependencies;
            for (int i = 0; i < first.Length; i++) stack.Push(first[i]);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                if (current == target) return true;
                if (visited[current]) continue;
                visited[current] = true;
                int[] deps = proposals[current].Dependencies;
                for (int i = 0; i < deps.Length; i++) stack.Push(deps[i]);
            }
            return false;
        }

        private static void Fail(Proposal proposal, ForcedDisplacementStopReason reason)
        {
            if (proposal.Failed) return;
            proposal.Failed = true;
            proposal.StopReason = reason;
            proposal.State = ProposalState.Failed;
        }

        // ————————————————————————————————————————————————————————————
        // 最终 footprint 确定后的失效计划候选（只判定，不提交）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 判定某个<strong>实际被换位</strong>单位的结果所失效的计划候选（任务包 :205-209）。
        ///
        /// 两类来源，去重后按 <see cref="ActionPlanId"/> 升序：
        /// <list type="number">
        /// <item>与<strong>最终 footprint</strong>相交的全部 Reservation（用
        /// <c>AllReservationsOrdered()</c> 遍历<strong>全部持有者</strong>，不用只给规范持有者的
        /// <c>ReservationAt</c> —— 07 之后一格可持有多条同单位时间不相交预留）；
        /// 也包括未来 Dodge 目的格预留（它不在网格预留表里，需单独读）；</item>
        /// <item>该单位全部依赖旧起点的<strong>活动/排队 Move 计划</strong>（非终态 <c>Move</c>）。
        /// 同单位 Lane 的位置依赖闭包是本集合的子集（闭包只在种子所属 Lane 内展开，而本集合已含该
        /// Lane 内全部非终态 Move），因此无需第二次遍历；这一等价性写在交接记录里。</item>
        /// </list>
        /// 临时模拟路径穿过、但最终并不相交的预留<strong>不</strong>在此集合内（任务包 :216）。
        /// </summary>
        private IReadOnlyList<ActionPlanId> CollectInvalidatedPlans(Proposal proposal)
        {
            var ids = new List<long>();

            IReadOnlyList<GridPoint> cells = proposal.TargetCells;
            IReadOnlyList<TrianglePoint> triangles = proposal.TargetTriangles;

            IReadOnlyList<Reservation> reservations = _grid.AllReservationsOrdered();
            for (int i = 0; i < reservations.Count; i++)
            {
                Reservation reservation = reservations[i];
                if (!ContainsPoint(cells, reservation.Cell)) continue;
                if (!ids.Contains(reservation.ActionPlanId.Value)) ids.Add(reservation.ActionPlanId.Value);
            }

            if (_dodgeReservations != null)
            {
                IReadOnlyList<DodgeDestinationReservation> dodgeReservations =
                    _dodgeReservations.AllReservationsOrdered();
                for (int i = 0; i < dodgeReservations.Count; i++)
                {
                    DodgeDestinationReservation reservation = dodgeReservations[i];
                    if (reservation.UnitId == proposal.UnitId) continue;
                    IReadOnlyList<GridPoint> destinationCells = _grid.ResolveDestinationCells(
                        reservation.UnitId, reservation.Destination, reservation.Facing);
                    if (!FootprintsOverlap(cells, triangles, destinationCells)) continue;
                    if (!ids.Contains(reservation.ReactionPlanId.Value))
                        ids.Add(reservation.ReactionPlanId.Value);
                }
            }

            if (_authority != null)
            {
                List<ActionPlan> plans = _authority.CollectNonTerminalPlansOfUnit(proposal.UnitId);
                for (int i = 0; i < plans.Count; i++)
                {
                    ActionPlan plan = plans[i];
                    if (plan.ActionType != ActionType.Move) continue;
                    if (!ids.Contains(plan.ActionPlanId.Value)) ids.Add(plan.ActionPlanId.Value);
                }
            }

            ids.Sort();
            var result = new List<ActionPlanId>(ids.Count);
            for (int i = 0; i < ids.Count; i++) result.Add(new ActionPlanId(ids[i]));
            return result;
        }

        private static bool ContainsPoint(IReadOnlyList<GridPoint> cells, GridPoint point)
        {
            for (int i = 0; i < cells.Count; i++) if (cells[i] == point) return true;
            return false;
        }

        private static bool FootprintsOverlap(
            IReadOnlyList<GridPoint> cells, IReadOnlyList<TrianglePoint> triangles,
            IReadOnlyList<GridPoint> otherCells)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                for (int j = 0; j < otherCells.Count; j++)
                {
                    if (cells[i] == otherCells[j]) return true;
                }
            }
            return false;
        }

        // ————————————————————————————————————————————————————————————
        // 只读一致性
        // ————————————————————————————————————————————————————————————

        private static void VerifySnapshotAgreement(
            UnitId unitId, GridPoint anchor, IReadOnlyList<UnitSnapshot> snapshot)
        {
            if (snapshot == null) return;
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (snapshot[i].UnitId != unitId.Value) continue;
                if (snapshot[i].X == anchor.X && snapshot[i].Y == anchor.Y) return;
                throw new LogicDefinitionException(ForcedDisplacementCodes.DISPLACEMENT_SNAPSHOT_MISMATCH,
                    "unit=" + unitId.Value.ToString(CultureInfo.InvariantCulture) +
                    " grid=" + anchor + " snapshot=(" +
                    snapshot[i].X.ToString(CultureInfo.InvariantCulture) + ", " +
                    snapshot[i].Y.ToString(CultureInfo.InvariantCulture) + ")");
            }
        }

        private static bool IsCanonicalDirection(GridDirection direction)
        {
            int value = (int)direction;
            return value >= 0 && value < GridNeighborTable.DirectionCount;
        }

        // ————————————————————————————————————————————————————————————
        // 私有临时占位空间
        // ————————————————————————————————————————————————————————————

        private enum ProposalState
        {
            Unknown = 0,
            Succeeded = 1,
            Failed = 2
        }

        private sealed class Proposal
        {
            public int RequestIndex;
            public UnitId UnitId;
            public GridDirection Direction;
            public GridDirection Facing;
            public int RequestedSteps;
            public int AppliedSteps;
            public bool Failed;
            public GridPoint Origin;
            public GridPoint Anchor;
            public GridPoint TargetAnchor;
            public IReadOnlyList<GridPoint> TargetCells = Array.Empty<GridPoint>();
            public IReadOnlyList<TrianglePoint> TargetTriangles = Array.Empty<TrianglePoint>();
            public int[] Dependencies = Array.Empty<int>();
            public bool HasExternalDependency;
            public ForcedDisplacementStopReason StopReason;
            public ProposalState State;
        }

        private sealed class TempUnit
        {
            public UnitId UnitId;
            public GridDirection Facing;
            public GridPoint Anchor;
            public IReadOnlyList<GridPoint> Cells = Array.Empty<GridPoint>();
            public IReadOnlyList<TrianglePoint> Triangles = Array.Empty<TrianglePoint>();
        }

        /// <summary>
        /// 求解器的私有占位空间：从只读 <see cref="LogicGrid"/> 深拷贝一次，之后全部读写都发生在这里。
        /// 单位表按 <c>UnitId</c> 升序装载，占用索引只做"这点属于谁"的查询，
        /// <strong>不</strong>用其枚举顺序做任何决策。
        /// </summary>
        private sealed class TempSpace
        {
            private readonly Dictionary<long, TempUnit> _units = new Dictionary<long, TempUnit>();
            private readonly Dictionary<GridPoint, long> _cellOwner = new Dictionary<GridPoint, long>();
            private readonly Dictionary<TrianglePoint, long> _triangleOwner = new Dictionary<TrianglePoint, long>();

            public void Load(LogicGrid grid)
            {
                _units.Clear();
                _cellOwner.Clear();
                _triangleOwner.Clear();

                IReadOnlyList<UnitId> units = grid.RegisteredUnitsOrdered();
                for (int i = 0; i < units.Count; i++)
                {
                    UnitId unitId = units[i];
                    if (!grid.TryGetAnchor(unitId, out GridPoint anchor)) continue;
                    grid.TryGetFacing(unitId, out GridDirection facing);
                    var unit = new TempUnit
                    {
                        UnitId = unitId,
                        Anchor = anchor,
                        Facing = facing,
                        Cells = Copy(grid.CellsOf(unitId)),
                        Triangles = Copy(grid.TrianglesOf(unitId))
                    };
                    _units[unitId.Value] = unit;
                    Index(unit);
                }
            }

            public bool TryGetUnit(UnitId unitId, out TempUnit unit)
                => _units.TryGetValue(unitId.Value, out unit);

            public bool TryGetOwner(GridPoint cell, out long owner)
                => _cellOwner.TryGetValue(cell, out owner);

            public bool TryGetTriangleOwner(TrianglePoint triangle, out long owner)
                => _triangleOwner.TryGetValue(triangle, out owner);

            public void Move(
                UnitId unitId, GridPoint anchor,
                IReadOnlyList<GridPoint> cells, IReadOnlyList<TrianglePoint> triangles)
            {
                if (!_units.TryGetValue(unitId.Value, out TempUnit unit)) return;
                Deindex(unit);
                unit.Anchor = anchor;
                unit.Cells = Copy(cells);
                unit.Triangles = Copy(triangles);
                Index(unit);
            }

            private void Index(TempUnit unit)
            {
                for (int i = 0; i < unit.Cells.Count; i++) _cellOwner[unit.Cells[i]] = unit.UnitId.Value;
                for (int i = 0; i < unit.Triangles.Count; i++)
                    _triangleOwner[unit.Triangles[i]] = unit.UnitId.Value;
            }

            private void Deindex(TempUnit unit)
            {
                for (int i = 0; i < unit.Cells.Count; i++)
                {
                    if (_cellOwner.TryGetValue(unit.Cells[i], out long owner) && owner == unit.UnitId.Value)
                        _cellOwner.Remove(unit.Cells[i]);
                }
                for (int i = 0; i < unit.Triangles.Count; i++)
                {
                    if (_triangleOwner.TryGetValue(unit.Triangles[i], out long owner) && owner == unit.UnitId.Value)
                        _triangleOwner.Remove(unit.Triangles[i]);
                }
            }

            private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
            {
                if (source == null || source.Count == 0) return Array.Empty<T>();
                var result = new T[source.Count];
                for (int i = 0; i < source.Count; i++) result[i] = source[i];
                return result;
            }
        }
    }
}
