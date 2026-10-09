using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;

namespace ProjectHero.Logic.Grid
{
    /// <summary>
    /// <strong>LogicGrid</strong>（任务包「必须产出」2）：纯 C# 的占位、区域查询与预留权威。
    ///
    /// 边界（冻结）：
    /// <list type="bullet">
    /// <item><strong>不</strong>引用 <c>GridManager.Instance</c>、Transform、Collider、Vector3
    /// 或任何 UnityEngine 类型（程序集级约束见 <c>AssemblyBoundaryTests</c>）；</item>
    /// <item>占位、区域查询与预留只做<strong>方向索引、整数平移与集合查询</strong>：
    /// 体积点集来自 <see cref="VolumeFootprint"/>（消费任务 02B 的规范 12 向表），
    /// 本类<strong>不</strong>旋转、<strong>不</strong>舍入、<strong>不</strong>调用三角函数；</item>
    /// <item>区域查询返回的 <see cref="UnitId"/> 按 <c>UnitId</c> <strong>稳定升序</strong>；</item>
    /// <item>所有写入都是"整体成功或零写入"：失败以稳定码返回，绝不留下部分注册/部分预留。</item>
    /// </list>
    ///
    /// 与 <c>GridManager</c> 的关系：<c>GridManager</c> 保留为过渡期的坐标/场景桥
    /// （<c>WorldToGrid</c>/<c>GridToWorld</c>/Gizmo），但<strong>不再</strong>是新 Logic 路径的
    /// 权威占位源，也不存在"两处同时写权威占位"的双写路径。
    /// </summary>
    public sealed class LogicGrid
    {
        private sealed class UnitRow
        {
            public UnitId UnitId;
            public GridPoint Anchor;
            public GridDirection Facing;

            /// <summary>任务 02B 的规范 12 向体积表；<c>null</c> = 未绑定（单格占位过渡形态）。</summary>
            public IReadOnlyList<DirectionalTriangleSet> Directions;

            public IReadOnlyList<TrianglePoint> Triangles;
            public IReadOnlyList<GridPoint> Cells;

            /// <summary>该单位下一次移动的目的格（Editable Move 登记；null = 无）。</summary>
            public GridPoint? PendingDestination;

            public bool HasVolumeTable => Directions != null;
        }

        private readonly GridBoundaryDefinition _boundary;
        private readonly Dictionary<long, UnitRow> _rows = new Dictionary<long, UnitRow>();
        private readonly List<long> _unitIds = new List<long>();

        private readonly Dictionary<GridPoint, long> _cellOwner = new Dictionary<GridPoint, long>();
        private readonly Dictionary<TrianglePoint, long> _triangleOwner = new Dictionary<TrianglePoint, long>();

        private readonly Dictionary<ReservationKey, Reservation> _reservations = new Dictionary<ReservationKey, Reservation>();

        /// <summary>
        /// 每格的预留持有者（任务 07 裁定 B：<strong>一格可以有多条互不重叠的同单位预留</strong>）。
        ///
        /// 只有 <see cref="TryReserve"/> 的"同一单位 + 时间窗互不重叠"判据会往这里放第二条；
        /// 其余一切冲突仍然拒绝。所有"该格有没有被占"的只读查询都经
        /// <see cref="TryGetCanonicalHolder"/> 取规范持有者，因此一格一条预留的既有形状
        /// （06 冻结的全部形状）读法与冻结版本逐字相同。
        /// </summary>
        private readonly Dictionary<GridPoint, List<ReservationKey>> _reservationHolders =
            new Dictionary<GridPoint, List<ReservationKey>>();

        public LogicGrid(GridBoundaryDefinition boundary)
        {
            _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        }

        /// <summary>Encounter 的合法网格边界（唯一的空间搜索边界）。</summary>
        public GridBoundaryDefinition Boundary => _boundary;

        // ————————————————————————————————————————————————————————————
        // 邻居枚举（唯一的规范顺序）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <strong>规范邻居序列</strong>：严格按 <see cref="GridDirection"/> 的冻结数值 <c>0 -> 11</c>
        /// 枚举合法邻居，跳过越界候选时<strong>不改变</strong>其余方向的相对顺序。
        ///
        /// Pathfinder、Dodge 目的格校验与所有只读预览<strong>必须</strong>消费本方法，
        /// 不得各自排一遍方向或复用旧 <c>Pathfinder.GetNeighbors</c> 的私有列表。
        /// </summary>
        public IReadOnlyList<GridPoint> GetNeighborsOrdered(GridPoint point)
        {
            var result = new List<GridPoint>(GridNeighborTable.DirectionCount);
            for (int i = 0; i < GridNeighborTable.DirectionCount; i++)
            {
                long x = (long)point.X + GridNeighborTable.OffsetX((GridDirection)i);
                long y = (long)point.Y + GridNeighborTable.OffsetY((GridDirection)i);
                // 坐标先扩展为 long 再判界：int 溢出候选在任何合法 Encounter 边界外，直接跳过。
                if (x < int.MinValue || x > int.MaxValue) continue;
                if (y < int.MinValue || y > int.MaxValue) continue;
                int ix = (int)x;
                int iy = (int)y;
                if (!GridPoint.IsValidParity(ix, iy)) continue;
                var candidate = new GridPoint(ix, iy);
                if (!_boundary.Contains(candidate)) continue;
                result.Add(candidate);
            }
            return result;
        }

        /// <summary>候选点是否同时满足奇偶与 Encounter 边界。</summary>
        public bool IsLegalGridPoint(GridPoint point) => _boundary.Contains(point);

        // ————————————————————————————————————————————————————————————
        // 单位注册 / 注销
        // ————————————————————————————————————————————————————————————

        /// <summary>单位是否已注册。</summary>
        public bool Contains(UnitId unitId) => _rows.ContainsKey(unitId.Value);

        /// <summary>已注册单位的稳定快照（按 <c>UnitId</c> 升序）。</summary>
        public IReadOnlyList<UnitId> RegisteredUnitsOrdered()
        {
            var ids = new List<long>(_unitIds);
            ids.Sort();
            var result = new List<UnitId>(ids.Count);
            for (int i = 0; i < ids.Count; i++) result.Add(new UnitId(ids[i]));
            return result;
        }

        /// <summary>
        /// 注册单位：验证锚点合法、朝向合法、体积规范表可用、footprint 不与其他单位相交。
        /// 成功返回 null；失败返回稳定码且<strong>零写入</strong>。
        /// </summary>
        public string RegisterUnit(
            UnitId unitId, GridPoint origin, GridDirection facing,
            IReadOnlyList<DirectionalTriangleSet> volumeDirections)
        {
            if (_rows.ContainsKey(unitId.Value))
                return LogicGridCodes.LOGIC_GRID_UNIT_ALREADY_REGISTERED;

            if (!_boundary.Contains(origin))
                return LogicGridCodes.LOGIC_GRID_OUT_OF_BOUNDS;

            if (!GridDirectionInfo.IsValidIndex((int)facing))
                return LogicGridCodes.LOGIC_GRID_VOLUME_TABLE_NOT_CANONICAL;

            string tableError = VolumeFootprint.ValidateDirectionsTable(volumeDirections);
            if (tableError != null) return tableError;

            IReadOnlyList<TrianglePoint> triangles = VolumeFootprint.ResolveTriangles(volumeDirections, origin, facing);
            IReadOnlyList<GridPoint> cells = VolumeFootprint.ResolveCells(volumeDirections, origin, facing);

            // footprint 与现有权威占位相交 ⇒ 整体拒绝（不是"后写覆盖"）。
            for (int i = 0; i < cells.Count; i++)
            {
                if (_cellOwner.TryGetValue(cells[i], out long owner) && owner != unitId.Value)
                    return LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER;
            }
            for (int i = 0; i < triangles.Count; i++)
            {
                if (_triangleOwner.TryGetValue(triangles[i], out long owner) && owner != unitId.Value)
                    return LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER;
            }

            var row = new UnitRow
            {
                UnitId = unitId,
                Anchor = origin,
                Facing = facing,
                Directions = volumeDirections,
                Triangles = triangles,
                Cells = cells
            };
            _rows[unitId.Value] = row;
            _unitIds.Add(unitId.Value);
            IndexRow(row);
            return null;
        }

        /// <summary>注销单位并移除其全部 footprint 索引；未知单位返回稳定码。</summary>
        public string UnregisterUnit(UnitId unitId)
        {
            if (!_rows.TryGetValue(unitId.Value, out UnitRow row))
                return LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN;

            DeindexRow(row);
            _rows.Remove(unitId.Value);
            _unitIds.Remove(unitId.Value);
            return null;
        }

        /// <summary>
        /// <strong>过渡形态</strong>：未绑定体积规范表的单位按<strong>单格占位</strong>注册
        /// （锚点自身即 footprint）。
        ///
        /// 它不是"体积规则的第二套实现"，而是一个显式、确定的降级口径：任务 02B 的
        /// <c>UnitDefinition</c> 目前<strong>没有</strong>指向 <c>VolumeSpec</c> 的字段，
        /// 因此 Logic 侧无法在装配期还原"单位 → 体积表"的绑定。任务 02B/10 提供真实绑定后，
        /// 装配方改用 <see cref="RegisterUnit"/>，本方法即不再被生产路径使用。
        /// 该事实在交接记录中登记为<strong>未完成项</strong>，不得被当作体积消费已完成。
        /// </summary>
        public string RegisterUnitWithPointFootprint(UnitId unitId, GridPoint origin, GridDirection facing)
        {
            if (_rows.ContainsKey(unitId.Value))
                return LogicGridCodes.LOGIC_GRID_UNIT_ALREADY_REGISTERED;
            if (!_boundary.Contains(origin))
                return LogicGridCodes.LOGIC_GRID_OUT_OF_BOUNDS;
            if (!GridDirectionInfo.IsValidIndex((int)facing))
                return LogicGridCodes.LOGIC_GRID_VOLUME_TABLE_NOT_CANONICAL;
            if (_cellOwner.TryGetValue(origin, out long owner) && owner != unitId.Value)
                return LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER;

            var row = new UnitRow
            {
                UnitId = unitId,
                Anchor = origin,
                Facing = facing,
                Directions = null,
                Triangles = Array.Empty<TrianglePoint>(),
                Cells = new[] { origin }
            };
            _rows[unitId.Value] = row;
            _unitIds.Add(unitId.Value);
            IndexRow(row);
            return null;
        }

        /// <summary>该单位当前是否消费任务 02B 的规范 12 向体积表（false = 单格占位过渡形态）。</summary>
        public bool UsesCanonicalVolumeTable(UnitId unitId)
            => _rows.TryGetValue(unitId.Value, out UnitRow row) && row.HasVolumeTable;

        private IReadOnlyList<GridPoint> ResolveCells(UnitRow row, GridPoint anchor, GridDirection facing)
            => row.HasVolumeTable
                ? VolumeFootprint.ResolveCells(row.Directions, anchor, facing)
                : new[] { anchor };

        private IReadOnlyList<TrianglePoint> ResolveTriangles(UnitRow row, GridPoint anchor, GridDirection facing)
            => row.HasVolumeTable
                ? VolumeFootprint.ResolveTriangles(row.Directions, anchor, facing)
                : (IReadOnlyList<TrianglePoint>)Array.Empty<TrianglePoint>();

        private void IndexRow(UnitRow row)
        {            for (int i = 0; i < row.Cells.Count; i++) _cellOwner[row.Cells[i]] = row.UnitId.Value;
            for (int i = 0; i < row.Triangles.Count; i++) _triangleOwner[row.Triangles[i]] = row.UnitId.Value;
        }

        private void DeindexRow(UnitRow row)
        {
            for (int i = 0; i < row.Cells.Count; i++) _cellOwner.Remove(row.Cells[i]);
            for (int i = 0; i < row.Triangles.Count; i++) _triangleOwner.Remove(row.Triangles[i]);
        }

        // ————————————————————————————————————————————————————————————
        // 占位查询（只索引、平移、集合查询）
        // ————————————————————————————————————————————————————————————

        public bool TryGetAnchor(UnitId unitId, out GridPoint anchor)
        {
            if (_rows.TryGetValue(unitId.Value, out UnitRow row))
            {
                anchor = row.Anchor;
                return true;
            }
            anchor = default;
            return false;
        }

        public bool TryGetFacing(UnitId unitId, out GridDirection facing)
        {
            if (_rows.TryGetValue(unitId.Value, out UnitRow row))
            {
                facing = row.Facing;
                return true;
            }
            facing = default;
            return false;
        }

        public IReadOnlyList<GridPoint> CellsOf(UnitId unitId)
            => _rows.TryGetValue(unitId.Value, out UnitRow row) ? row.Cells : Array.Empty<GridPoint>();

        public IReadOnlyList<TrianglePoint> TrianglesOf(UnitId unitId)
            => _rows.TryGetValue(unitId.Value, out UnitRow row) ? row.Triangles : Array.Empty<TrianglePoint>();

        /// <summary>该格当前的占位所有者（无则 false）。</summary>
        public bool TryGetCellOwner(GridPoint cell, out UnitId owner)
        {
            if (_cellOwner.TryGetValue(cell, out long value))
            {
                owner = new UnitId(value);
                return true;
            }
            owner = default;
            return false;
        }

        /// <summary>该三角当前的占位所有者（无则 false）。</summary>
        public bool TryGetTriangleOwner(TrianglePoint triangle, out UnitId owner)
        {
            if (_triangleOwner.TryGetValue(triangle, out long value))
            {
                owner = new UnitId(value);
                return true;
            }
            owner = default;
            return false;
        }

        /// <summary>该格是否被 <paramref name="ignore"/> 之外的单位占用（自身重叠不算阻塞）。</summary>
        public bool IsCellBlockedFor(GridPoint cell, UnitId ignore)
            => _cellOwner.TryGetValue(cell, out long owner) && owner != ignore.Value;

        /// <summary>
        /// <strong>稳定区域查询</strong>：返回占用了任一给定格的全部单位（按 <c>UnitId</c> 升序）。
        /// 阵营关系<strong>不</strong>参与过滤——<c>Allied/Neutral/Hostile</c>、Controller 与玩家标志
        /// 都不能让单位互相穿透。
        /// </summary>
        public IReadOnlyList<UnitId> UnitsInCellsOrdered(IReadOnlyList<GridPoint> cells, UnitId? ignore = null)
        {
            var ids = new List<long>();
            var seen = new HashSet<long>();
            if (cells != null)
            {
                for (int i = 0; i < cells.Count; i++)
                {
                    if (!_cellOwner.TryGetValue(cells[i], out long owner)) continue;
                    if (ignore.HasValue && owner == ignore.Value.Value) continue;
                    if (seen.Add(owner)) ids.Add(owner);
                }
            }
            return ToSortedUnitIds(ids);
        }

        /// <summary>稳定区域查询（三角级 footprint）。</summary>
        public IReadOnlyList<UnitId> UnitsInTrianglesOrdered(IReadOnlyList<TrianglePoint> triangles, UnitId? ignore = null)
        {
            var ids = new List<long>();
            var seen = new HashSet<long>();
            if (triangles != null)
            {
                for (int i = 0; i < triangles.Count; i++)
                {
                    if (!_triangleOwner.TryGetValue(triangles[i], out long owner)) continue;
                    if (ignore.HasValue && owner == ignore.Value.Value) continue;
                    if (seen.Add(owner)) ids.Add(owner);
                }
            }
            return ToSortedUnitIds(ids);
        }

        /// <summary>
        /// 以某单位当前 footprint 为区域的稳定查询（"这个体积里还有谁"）。
        /// </summary>
        public IReadOnlyList<UnitId> UnitsOverlappingOf(UnitId unitId)
        {
            if (!_rows.TryGetValue(unitId.Value, out UnitRow row)) return Array.Empty<UnitId>();
            return UnitsInCellsOrdered(row.Cells, unitId);
        }

        private static IReadOnlyList<UnitId> ToSortedUnitIds(List<long> ids)
        {
            ids.Sort();
            var result = new List<UnitId>(ids.Count);
            for (int i = 0; i < ids.Count; i++) result.Add(new UnitId(ids[i]));
            return result;
        }

        // ————————————————————————————————————————————————————————————
        // 预留查询与写入
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 尝试为 <paramref name="reservation"/> 取得目标格。
        ///
        /// <strong>占用判定（任务 07 裁定 B 的唯一放宽）</strong>：同格的每条既有预留都逐条与
        /// 新预留比较，只有<strong>同一单位</strong>且<strong>时间窗互不重叠</strong>
        /// （<c>existing.EndTick &lt;= candidate.StartTick</c> 或反向）时才允许<strong>共存</strong>；
        /// 其余任何情形（其他单位的一切预留、同单位但时间窗重叠、甚至完全相同的区间）
        /// 一律返回 <see cref="LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER"/>
        /// 且<strong>不</strong>抢占。因此"先成功提交者持有、后者稳定拒绝"这条 06 冻结仲裁在
        /// 同一时间窗内逐字不变，静止单位/他人的占位也照旧阻挡。
        ///
        /// 只有一个单位<strong>稍后重新经过自己先前的格</strong>（回程链）时才需要共存：
        /// 单位的先后两段占用时间窗天然有界而不相交，空间上并不冲突。
        /// </summary>
        public string TryReserve(Reservation reservation)
        {
            if (reservation == null) return LogicGridCodes.LOGIC_GRID_RESERVATION_INTERVAL_INVALID;
            if (reservation.StepIndex < 0 || reservation.EndTick <= reservation.StartTick)
                return LogicGridCodes.LOGIC_GRID_RESERVATION_INTERVAL_INVALID;
            if (_reservations.ContainsKey(reservation.Key))
                return LogicGridCodes.LOGIC_GRID_RESERVATION_DUPLICATE_KEY;

            if (_reservationHolders.TryGetValue(reservation.Cell, out List<ReservationKey> holders))
            {
                for (int i = 0; i < holders.Count; i++)
                {
                    ReservationKey holder = holders[i];
                    if (holder.Equals(reservation.Key)) continue;
                    if (CanCoexistTimeDisjointSameUnit(_reservations[holder], reservation)) continue;
                    return LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER;
                }
            }
            else
            {
                holders = new List<ReservationKey>(1);
                _reservationHolders[reservation.Cell] = holders;
            }

            _reservations[reservation.Key] = reservation;
            holders.Add(reservation.Key);
            return null;
        }

        /// <summary>
        /// 同格共存的<strong>唯一</strong>判据：同一单位 + 时间窗互不重叠（半开区间）。
        /// 判据只依赖 <see cref="Reservation"/> 自带的两端，不依赖 Tick 之外的任何状态。
        /// </summary>
        private static bool CanCoexistTimeDisjointSameUnit(Reservation existing, Reservation candidate)
        {
            if (existing.UnitId.Value != candidate.UnitId.Value) return false;
            return existing.EndTick <= candidate.StartTick || candidate.EndTick <= existing.StartTick;
        }

        /// <summary>
        /// 该格的<strong>规范持有者</strong>（用于所有"该格有没有被占"的只读查询）：
        /// 取释放边界最晚的一条，平局时按 <see cref="Reservation.CompareCanonical"/>。
        ///
        /// 取"最晚释放"是刻意的保守选择：门禁的 <c>RetryableTimedBlock</c> 提示的释放 Tick 因此
        /// 绝不会早于该格真正空出来的时刻。一格只有一条预留时（既有全部形状）它就是那条。
        /// </summary>
        private bool TryGetCanonicalHolder(GridPoint cell, out ReservationKey key)
        {
            if (!_reservationHolders.TryGetValue(cell, out List<ReservationKey> holders) || holders.Count == 0)
            {
                key = default;
                return false;
            }

            int best = 0;
            for (int i = 1; i < holders.Count; i++)
            {
                Reservation current = _reservations[holders[i]];
                Reservation chosen = _reservations[holders[best]];
                if (current.EndTick > chosen.EndTick) { best = i; continue; }
                if (current.EndTick == chosen.EndTick &&
                    Reservation.CompareCanonical(current, chosen) < 0) best = i;
            }
            key = holders[best];
            return true;
        }

        /// <summary>该键是否确实是 <paramref name="cell"/> 的持有者之一（多持有者时含非规范持有者）。</summary>
        private bool IsReservationHolderOf(GridPoint cell, ReservationKey key)
            => _reservationHolders.TryGetValue(cell, out List<ReservationKey> holders) && holders.Contains(key);

        public bool TryGetReservation(ReservationKey key, out Reservation reservation)
            => _reservations.TryGetValue(key, out reservation);

        /// <summary>该格的 Reservation 持有者（无则 false；多持有者时取规范持有者）。</summary>
        public bool TryGetReservationKey(GridPoint cell, out ReservationKey key)
            => TryGetCanonicalHolder(cell, out key);

        /// <summary>该格的 Reservation（无则 null；多持有者时取规范持有者）。</summary>
        public Reservation ReservationAt(GridPoint cell)
            => TryGetCanonicalHolder(cell, out ReservationKey key) ? _reservations[key] : null;

        /// <summary>某计划持有的全部 Reservation（按稳定空间键 <c>(StartTick, X, Y, PlanId, StepIndex)</c> 升序）。</summary>
        public IReadOnlyList<Reservation> ReservationsOfPlanOrdered(ActionPlanId actionPlanId)
        {
            var result = new List<Reservation>();
            foreach (KeyValuePair<ReservationKey, Reservation> pair in _reservations)
            {
                if (pair.Key.ActionPlanId.Value == actionPlanId.Value) result.Add(pair.Value);
            }
            result.Sort(Reservation.CompareCanonical);
            return result;
        }

        /// <summary>全部 Reservation 的规范快照（按稳定空间键升序）。</summary>
        public IReadOnlyList<Reservation> AllReservationsOrdered()
        {
            var result = new List<Reservation>(_reservations.Count);
            foreach (KeyValuePair<ReservationKey, Reservation> pair in _reservations) result.Add(pair.Value);
            result.Sort(Reservation.CompareCanonical);
            return result;
        }

        /// <summary>
        /// 释放单个预留键。释放顺序与"该格是否仍被占"的读法无关：只要该格还剩任何持有者，
        /// <see cref="ReservationAt"/> / <see cref="TryGetReservationKey"/> 就仍然给出规范持有者。
        /// </summary>
        public bool ReleaseReservation(ReservationKey key)
        {
            if (!_reservations.TryGetValue(key, out Reservation reservation)) return false;
            _reservations.Remove(key);
            if (_reservationHolders.TryGetValue(reservation.Cell, out List<ReservationKey> holders))
            {
                holders.RemoveAll(candidate => candidate.Equals(key));
                if (holders.Count == 0) _reservationHolders.Remove(reservation.Cell);
            }
            return true;
        }

        /// <summary>
        /// 按稳定空间键升序释放某计划的<strong>全部</strong> Reservation，并返回被释放者（供只读审计）。
        /// 幂等：无预留时返回空列表。
        /// </summary>
        public IReadOnlyList<Reservation> ReleaseReservationsOfPlan(ActionPlanId actionPlanId)
        {
            IReadOnlyList<Reservation> owned = ReservationsOfPlanOrdered(actionPlanId);
            for (int i = 0; i < owned.Count; i++) ReleaseReservation(owned[i].Key);
            return owned;
        }

        // ————————————————————————————————————————————————————————————
        // 占用提交（普通移动的命令前边界 / Dodge 换位共用）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 把单位锚点/朝向改为目标值并重建占位索引（内部提交原语）。
        ///
        /// 它<strong>不</strong>做预留仲裁，也<strong>不</strong>决定赢家：调用方必须已经完成
        /// 冲突校验（移动段建立时已取得 Reservation，或批量换位已整体预检）。
        /// 出现"目标 footprint 已被其他单位占用"时以
        /// <see cref="LogicGridCodes.LOGIC_GRID_OWNERSHIP_CONTRADICTION"/> 失败
        /// （调用方即处于内部矛盾，必须令 Step 失败而不是静默覆盖）。
        /// </summary>
        public string CommitAnchor(UnitId unitId, GridPoint to, GridDirection facing)
        {
            if (!_rows.TryGetValue(unitId.Value, out UnitRow row))
                return LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN;
            if (!_boundary.Contains(to)) return LogicGridCodes.LOGIC_GRID_OUT_OF_BOUNDS;

            IReadOnlyList<TrianglePoint> triangles = ResolveTriangles(row, to, facing);
            IReadOnlyList<GridPoint> cells = ResolveCells(row, to, facing);
            DeindexRow(row);
            for (int i = 0; i < cells.Count; i++)
            {
                if (_cellOwner.TryGetValue(cells[i], out long owner) && owner != unitId.Value)
                {
                    IndexRow(row);       // 零写入回滚
                    return LogicGridCodes.LOGIC_GRID_OWNERSHIP_CONTRADICTION;
                }
            }
            for (int i = 0; i < triangles.Count; i++)
            {
                if (_triangleOwner.TryGetValue(triangles[i], out long owner) && owner != unitId.Value)
                {
                    IndexRow(row);
                    return LogicGridCodes.LOGIC_GRID_OWNERSHIP_CONTRADICTION;
                }
            }

            row.Anchor = to;
            row.Facing = facing;
            row.Triangles = triangles;
            row.Cells = cells;
            IndexRow(row);
            return null;
        }

        /// <summary>仅朝向变化（不移动锚点）的占位重建。</summary>
        public string CommitFacing(UnitId unitId, GridDirection facing)
            => TryGetAnchor(unitId, out GridPoint anchor)
                ? CommitAnchor(unitId, anchor, facing)
                : LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN;

        // ————————————————————————————————————————————————————————————
        // 只读目的格预检（Dodge 换位与任务 08 的统一入口）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 该单位若把锚点改到 <paramref name="to"/>、朝向 <paramref name="facing"/>，
        /// 会占用哪些格。只做方向索引 + 整数平移，<strong>不写任何状态</strong>。
        /// </summary>
        public IReadOnlyList<GridPoint> ResolveDestinationCells(UnitId unitId, GridPoint to, GridDirection facing)
            => _rows.TryGetValue(unitId.Value, out UnitRow row)
                ? ResolveCells(row, to, facing)
                : Array.Empty<GridPoint>();

        /// <summary>同 <see cref="ResolveDestinationCells"/> 的三角投影（只读）。</summary>
        public IReadOnlyList<TrianglePoint> ResolveDestinationTriangles(UnitId unitId, GridPoint to, GridDirection facing)
            => _rows.TryGetValue(unitId.Value, out UnitRow row)
                ? ResolveTriangles(row, to, facing)
                : Array.Empty<TrianglePoint>();

        /// <summary>
        /// <strong>只读目的格预检</strong>：目标锚点/完整 footprint 是否可被该单位合法占据。
        /// 成功返回 null；失败返回稳定码。它<strong>不</strong>写任何索引，因此可以在
        /// "先冻结快照、再原子提交"的固定阶段里安全地反复调用。
        ///
        /// 失败码：<see cref="LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN"/> /
        /// <see cref="LogicGridCodes.LOGIC_GRID_OUT_OF_BOUNDS"/> /
        /// <see cref="LogicGridCodes.LOGIC_GRID_VOLUME_TABLE_NOT_CANONICAL"/> /
        /// <see cref="LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER"/>。
        /// </summary>
        public string ValidateDestinationFor(UnitId unitId, GridPoint to, GridDirection facing)
        {
            if (!_rows.TryGetValue(unitId.Value, out UnitRow row))
                return LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN;
            if (!GridDirectionInfo.IsValidIndex((int)facing))
                return LogicGridCodes.LOGIC_GRID_VOLUME_TABLE_NOT_CANONICAL;
            if (!_boundary.Contains(to)) return LogicGridCodes.LOGIC_GRID_OUT_OF_BOUNDS;

            IReadOnlyList<TrianglePoint> triangles;
            IReadOnlyList<GridPoint> cells;
            try
            {
                triangles = ResolveTriangles(row, to, facing);
                cells = ResolveCells(row, to, facing);
            }
            catch (LogicDefinitionException)
            {
                return LogicGridCodes.LOGIC_GRID_VOLUME_TABLE_NOT_CANONICAL;
            }

            for (int i = 0; i < cells.Count; i++)
            {
                if (!_boundary.Contains(cells[i])) return LogicGridCodes.LOGIC_GRID_OUT_OF_BOUNDS;
                if (_cellOwner.TryGetValue(cells[i], out long owner) && owner != unitId.Value)
                    return LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER + ":cell=" + cells[i];
            }
            for (int i = 0; i < triangles.Count; i++)
            {
                if (_triangleOwner.TryGetValue(triangles[i], out long owner) && owner != unitId.Value)
                    return LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER + ":triangle=" + triangles[i];
            }
            return null;
        }

        /// <summary>登记/清除该单位下一次移动的目的格（启动门禁空间查询的只读输入）。</summary>
        public void SetPendingDestination(UnitId unitId, GridPoint? destination)
        {
            if (_rows.TryGetValue(unitId.Value, out UnitRow row)) row.PendingDestination = destination;
        }

        public bool TryGetPendingDestination(UnitId unitId, out GridPoint destination)
        {
            if (_rows.TryGetValue(unitId.Value, out UnitRow row) && row.PendingDestination.HasValue)
            {
                destination = row.PendingDestination.Value;
                return true;
            }
            destination = default;
            return false;
        }

        // ————————————————————————————————————————————————————————————
        // 启动门禁：空间三分查询
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <strong>启动门禁空间查询</strong>（任务包「必须产出」6 第二段）。
        ///
        /// 判定顺序固定：
        /// <list type="number">
        /// <item><paramref name="destination"/> 越界或非规范点 ⇒ <c>TerminalOrUnknownBlock</c>
        /// （静态非法格<strong>绝不</strong>伪造成 <c>tick + 1</c>）；</item>
        /// <item>目的地被<strong>自己</strong>占用 ⇒ 继续检查该格的 Reservation；</item>
        /// <item>目的地被<strong>其他单位</strong>占用：该单位若有一条活动移动段会在有限
        /// <c>EndTick</c> 释放该格 ⇒ <c>RetryableTimedBlock(EndTick)</c>；
        /// 否则（静止占位、无结束边界）⇒ <c>TerminalOrUnknownBlock</c>；</item>
        /// <item>目的地被其他计划的 Reservation 持有：有限且严格晚于当前 Tick 的
        /// <c>EndTick</c> ⇒ <c>RetryableTimedBlock</c>；已经到期的 Reservation 是内部矛盾
        /// ⇒ <c>TerminalOrUnknownBlock</c>。</item>
        /// </list>
        /// 多个阻塞同时存在时取<strong>最晚</strong>的有限释放边界（与门禁"取更大 RetryAtTick"一致）。
        /// </summary>
        public SpaceBlockQueryResult QueryStartBlock(UnitId unitId, GridPoint destination, long tick)
            => QueryStartBlock(unitId, destination, tick, null);

        /// <param name="activeReleaseTickOf">
        /// "该单位当前活动移动段会在哪个 Tick 释放其目的格"的只读查询（由
        /// <see cref="MovementSegmentTable"/> 提供）。为 null 时视为"没有有限释放边界"。
        /// </param>
        public SpaceBlockQueryResult QueryStartBlock(
            UnitId unitId, GridPoint destination, long tick, Func<UnitId, long?> activeReleaseTickOf)
        {
            if (!_boundary.Contains(destination))
                return SpaceBlockQueryResult.Terminal("destination-out-of-bounds:" + destination);

            bool retryable = false;
            long releaseTick = long.MinValue;
            string detail = null;

            if (_cellOwner.TryGetValue(destination, out long cellOwner) && cellOwner != unitId.Value)
            {
                long? ownerRelease = activeReleaseTickOf?.Invoke(new UnitId(cellOwner));
                if (!ownerRelease.HasValue || ownerRelease.Value == long.MaxValue || ownerRelease.Value <= tick)
                    return SpaceBlockQueryResult.Terminal(
                        "occupancy-without-finite-release:unit=" + cellOwner.ToString(CultureInfo.InvariantCulture));
                retryable = true;
                releaseTick = ownerRelease.Value;
                detail = "occupancy-release@unit=" + cellOwner.ToString(CultureInfo.InvariantCulture);
            }

            if (TryGetCanonicalHolder(destination, out ReservationKey key))
            {
                Reservation reservation = _reservations[key];
                if (reservation.UnitId.Value != unitId.Value)
                {
                    if (reservation.EndTick <= tick || reservation.EndTick == long.MaxValue)
                        return SpaceBlockQueryResult.Terminal("reservation-without-future-release:" + reservation);
                    if (!retryable || reservation.EndTick > releaseTick)
                    {
                        retryable = true;
                        releaseTick = reservation.EndTick;
                        detail = "reservation-release@" + reservation;
                    }
                }
            }

            return retryable ? SpaceBlockQueryResult.Retryable(releaseTick, detail) : SpaceBlockQueryResult.Free;
        }

        /// <summary>
        /// 面向冻结的 <c>ActionStartGateContext.SpaceBlockUntilTickOf(Func&lt;UnitId,long?&gt;)</c>
        /// 的适配器：<c>Free ⇒ null</c>、<c>Retryable ⇒ ReleaseTick</c>、
        /// <c>TerminalOrUnknown ⇒ long.MaxValue</c>（门禁据此返回
        /// <c>Terminal(ActorUnavailableAtStart)</c>，因此<strong>不会</strong>退化为每 Tick 盲重试）。
        ///
        /// 目的地取该单位登记的 <see cref="SetPendingDestination"/>；没有登记目的地时该单位在本
        /// Tick 不产生空间阻塞（<c>null</c>）。
        /// </summary>
        public Func<UnitId, long?> BuildSpaceBlockUntilTickOf(long tick, Func<UnitId, long?> activeReleaseTickOf)
        {
            return unitId =>
            {
                if (!TryGetPendingDestination(unitId, out GridPoint destination)) return (long?)null;
                SpaceBlockQueryResult result = QueryStartBlock(unitId, destination, tick, activeReleaseTickOf);
                if (result.IsFree) return null;
                if (result.IsRetryable) return result.ReleaseTick;
                return long.MaxValue;
            };
        }

        // ————————————————————————————————————————————————————————————
        // 一致性自检
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 只读一致性自检：单元行与实际索引必须互相印证。
        /// 返回 null = 一致；否则返回 <see cref="LogicGridCodes.LOGIC_GRID_OWNERSHIP_CONTRADICTION"/>
        /// 并给出细节。<strong>不</strong>修改任何状态。
        /// </summary>
        public string VerifyConsistency()
        {
            foreach (KeyValuePair<long, UnitRow> pair in _rows)
            {
                UnitRow row = pair.Value;
                for (int i = 0; i < row.Cells.Count; i++)
                {
                    if (!_cellOwner.TryGetValue(row.Cells[i], out long owner) || owner != pair.Key)
                        return LogicGridCodes.LOGIC_GRID_OWNERSHIP_CONTRADICTION +
                               ":cell=" + row.Cells[i] + " unit=" + pair.Key.ToString(CultureInfo.InvariantCulture);
                }
                for (int i = 0; i < row.Triangles.Count; i++)
                {
                    if (!_triangleOwner.TryGetValue(row.Triangles[i], out long owner) || owner != pair.Key)
                        return LogicGridCodes.LOGIC_GRID_OWNERSHIP_CONTRADICTION +
                               ":triangle=" + row.Triangles[i] + " unit=" + pair.Key.ToString(CultureInfo.InvariantCulture);
                }
            }
            foreach (KeyValuePair<ReservationKey, Reservation> pair in _reservations)
            {
                if (TryGetCanonicalHolder(pair.Value.Cell, out ReservationKey holder) && holder.Equals(pair.Key))
                    continue;
                // 任务 07 裁定 B：同一单位在一格上以**互不重叠**的时间窗持有两条预留是合法形状
                // （见 TryReserve 的共存判据），但它必须仍有明确归属。
                if (IsReservationHolderOf(pair.Value.Cell, pair.Key)) continue;
                return LogicGridCodes.LOGIC_GRID_OWNERSHIP_CONTRADICTION + ":reservation=" + pair.Key;
            }
            return null;
        }

        // ————————————————————————————————————————————————————————————
        // BatchRelocation：全批预检 + 统一移除 / 统一写入
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <strong>强制位移的原子占位提交原语</strong>（任务包「必须产出」13）。
        ///
        /// 本任务<strong>不</strong>实现同时求解器：调用前任务 08 已在不可变快照与临时空间中确定全部
        /// 最终位置，并已通过统一终态协调器释放冲突移动计划的未来 Segment/Reservation。
        /// 本原语<strong>不</strong>调用 Pathfinder、<c>MoveTimingSpec</c>、<c>MovementSegment</c> 推进、
        /// <c>TryReserve</c> 或普通 Reservation 仲裁，也<strong>不</strong>产生表现事件。
        ///
        /// 验证顺序固定（任一失败 ⇒ <c>InvariantViolation</c> 且真实 LogicGrid <strong>零写入</strong>）：
        /// <list type="number">
        /// <item>输入非空、无重复 <c>UnitId</c>（唯一性只用于验证/诊断，<strong>不</strong>用于选赢家）；</item>
        /// <item>每个单位仍处于 <c>ExpectedFrom</c>，且来源 footprint 与当前权威占位一致；</item>
        /// <item>目标锚点与<strong>完整</strong> footprint 合法（边界 + 奇偶）；</item>
        /// <item>批次目标彼此不重叠；</item>
        /// <item>移除批次内全部旧 footprint 后，新 footprint 不与<strong>静止单位</strong>相交；</item>
        /// <item>待抢占的 Reservation 已清理（新 footprint 上不存在他人 Reservation）。</item>
        /// </list>
        /// 全部通过后：<strong>先</strong>统一移除全部旧 footprint，<strong>再</strong>统一写入全部新 footprint。
        /// </summary>
        public string ApplyBatchRelocation(IReadOnlyList<BatchRelocation> relocations)
        {
            if (relocations == null || relocations.Count == 0)
                return LogicGridCodes.LOGIC_GRID_BATCH_INVALID;

            // —— 1. 唯一性与成员合法性 ——
            var batchUnitIds = new HashSet<long>();
            var rows = new UnitRow[relocations.Count];
            for (int i = 0; i < relocations.Count; i++)
            {
                BatchRelocation relocation = relocations[i];
                if (relocation == null) return LogicGridCodes.LOGIC_GRID_BATCH_INVALID;
                if (!batchUnitIds.Add(relocation.UnitId.Value))
                    return LogicGridCodes.LOGIC_GRID_BATCH_DUPLICATE_UNIT;
                if (!_rows.TryGetValue(relocation.UnitId.Value, out UnitRow row))
                    return LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN;
                rows[i] = row;
            }

            // —— 2. ExpectedFrom 与来源 footprint 一致性 ——
            for (int i = 0; i < relocations.Count; i++)
            {
                UnitRow row = rows[i];
                BatchRelocation relocation = relocations[i];
                if (row.Anchor.X != relocation.ExpectedFrom.X || row.Anchor.Y != relocation.ExpectedFrom.Y)
                    return LogicGridCodes.LOGIC_GRID_BATCH_EXPECTED_FROM_MISMATCH;
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    if (!_cellOwner.TryGetValue(row.Cells[c], out long owner) || owner != row.UnitId.Value)
                        return LogicGridCodes.LOGIC_GRID_OWNERSHIP_CONTRADICTION + ":source=" + row.Cells[c];
                }
            }

            // —— 3. 目标锚点与完整 footprint 合法 ——
            var targets = new UnitRow[relocations.Count];
            var targetCells = new GridPoint[relocations.Count][];
            var targetTriangles = new TrianglePoint[relocations.Count][];
            for (int i = 0; i < relocations.Count; i++)
            {
                UnitRow row = rows[i];
                GridPoint to = relocations[i].To;
                if (!_boundary.Contains(to)) return LogicGridCodes.LOGIC_GRID_OUT_OF_BOUNDS;

                IReadOnlyList<TrianglePoint> triangles;
                IReadOnlyList<GridPoint> cells;
                try
                {
                    triangles = ResolveTriangles(row, to, row.Facing);
                    cells = ResolveCells(row, to, row.Facing);
                }
                catch (LogicDefinitionException)
                {
                    return LogicGridCodes.LOGIC_GRID_OUT_OF_BOUNDS;
                }

                for (int c = 0; c < cells.Count; c++)
                {
                    if (!_boundary.Contains(cells[c])) return LogicGridCodes.LOGIC_GRID_OUT_OF_BOUNDS;
                }

                // —— 6. 待抢占 Reservation 必须已清理 ——
                // 多持有者时逐条判定（"任何一条仍属于别的计划"就不得提交），
                // 一格一条的既有形状下与冻结版本逐字等价。
                for (int c = 0; c < cells.Count; c++)
                {
                    if (!_reservationHolders.TryGetValue(cells[c], out List<ReservationKey> holders)) continue;
                    for (int h = 0; h < holders.Count; h++)
                    {
                        if (!_reservations.TryGetValue(holders[h], out Reservation remaining)
                            || remaining.UnitId != row.UnitId)
                            return LogicGridCodes.LOGIC_GRID_BATCH_RESERVATION_NOT_CLEARED + ":cell=" + cells[c];
                    }
                }

                var targetRow = new UnitRow
                {
                    UnitId = row.UnitId,
                    Anchor = to,
                    Facing = row.Facing,
                    Directions = row.Directions,
                    Triangles = triangles,
                    Cells = cells
                };
                targets[i] = targetRow;
                targetCells[i] = ToArray(cells);
                targetTriangles[i] = ToArray(triangles);
            }

            // —— 4. 批次目标彼此不重叠 ——
            var claimed = new Dictionary<GridPoint, int>();
            var claimedTriangles = new Dictionary<TrianglePoint, int>();
            for (int i = 0; i < relocations.Count; i++)
            {
                for (int c = 0; c < targetCells[i].Length; c++)
                {
                    if (claimed.TryGetValue(targetCells[i][c], out int other) && other != i)
                        return LogicGridCodes.LOGIC_GRID_BATCH_TARGET_OVERLAP;
                    claimed[targetCells[i][c]] = i;
                }
                for (int c = 0; c < targetTriangles[i].Length; c++)
                {
                    if (claimedTriangles.TryGetValue(targetTriangles[i][c], out int other) && other != i)
                        return LogicGridCodes.LOGIC_GRID_BATCH_TARGET_OVERLAP;
                    claimedTriangles[targetTriangles[i][c]] = i;
                }
            }

            // —— 5. 移除批次旧 footprint 后不与静止单位相交 ——
            var removing = new HashSet<GridPoint>();
            var removingTriangles = new HashSet<TrianglePoint>();
            for (int i = 0; i < relocations.Count; i++)
            {
                for (int c = 0; c < rows[i].Cells.Count; c++) removing.Add(rows[i].Cells[c]);
                for (int c = 0; c < rows[i].Triangles.Count; c++) removingTriangles.Add(rows[i].Triangles[c]);
            }
            for (int i = 0; i < relocations.Count; i++)
            {
                for (int c = 0; c < targetCells[i].Length; c++)
                {
                    GridPoint cell = targetCells[i][c];
                    if (!_cellOwner.TryGetValue(cell, out long owner)) continue;
                    if (owner == relocations[i].UnitId.Value) continue;
                    if (removing.Contains(cell)) continue;
                    return LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER + ":cell=" + cell;
                }
                for (int c = 0; c < targetTriangles[i].Length; c++)
                {
                    TrianglePoint triangle = targetTriangles[i][c];
                    if (!_triangleOwner.TryGetValue(triangle, out long owner)) continue;
                    if (owner == relocations[i].UnitId.Value) continue;
                    if (removingTriangles.Contains(triangle)) continue;
                    return LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER + ":triangle=" + triangle;
                }
            }

            // —— 提交：统一移除全部旧 footprint → 统一写入全部新 footprint ——
            for (int i = 0; i < relocations.Count; i++) DeindexRow(rows[i]);
            for (int i = 0; i < relocations.Count; i++)
            {
                UnitRow row = rows[i];
                row.Anchor = targets[i].Anchor;
                row.Cells = targets[i].Cells;
                row.Triangles = targets[i].Triangles;
                IndexRow(row);
            }
            return null;
        }

        private static T[] ToArray<T>(IReadOnlyList<T> source)
        {
            if (source is T[] array) return array;
            var result = new T[source.Count];
            for (int i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
            => "logic-grid units=" + _rows.Count.ToString(CultureInfo.InvariantCulture) +
               " cells=" + _cellOwner.Count.ToString(CultureInfo.InvariantCulture) +
               " reservations=" + _reservations.Count.ToString(CultureInfo.InvariantCulture);
    }
}
