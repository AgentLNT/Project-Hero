namespace ProjectHero.Logic.Grid
{
    /// <summary>
    /// <see cref="LogicGrid"/> 的稳定错误码（任务 06）。全部码只增不改；
    /// 失败即"整体拒绝、零局部写入"，绝不出现"部分注册/部分预留"。
    /// </summary>
    public static class LogicGridCodes
    {
        /// <summary>同一 UnitId 重复注册。</summary>
        public const string LOGIC_GRID_UNIT_ALREADY_REGISTERED = "LOGIC_GRID_UNIT_ALREADY_REGISTERED";

        /// <summary>注销/查询未知 UnitId。</summary>
        public const string LOGIC_GRID_UNIT_UNKNOWN = "LOGIC_GRID_UNIT_UNKNOWN";

        /// <summary>目标锚点或完整 footprint 落在 Encounter 边界之外。</summary>
        public const string LOGIC_GRID_OUT_OF_BOUNDS = "LOGIC_GRID_OUT_OF_BOUNDS";

        /// <summary>目标 footprint 与<strong>其他</strong>单位的权威占位相交。</summary>
        public const string LOGIC_GRID_OCCUPIED_BY_OTHER = "LOGIC_GRID_OCCUPIED_BY_OTHER";

        /// <summary>目标 footprint 与<strong>其他</strong>计划的 Reservation 相交。</summary>
        public const string LOGIC_GRID_RESERVED_BY_OTHER = "LOGIC_GRID_RESERVED_BY_OTHER";

        /// <summary>体积规范表不是任务 02B 的规范 12 向表（缺失/非规范/空方向）。</summary>
        public const string LOGIC_GRID_VOLUME_TABLE_NOT_CANONICAL = "LOGIC_GRID_VOLUME_TABLE_NOT_CANONICAL";

        /// <summary>预留区间非法（EndTick 不严格大于 StartTick，或 StepIndex 为负）。</summary>
        public const string LOGIC_GRID_RESERVATION_INTERVAL_INVALID = "LOGIC_GRID_RESERVATION_INTERVAL_INVALID";

        /// <summary>重复预留键 <c>(ActionPlanId, StepIndex)</c>。</summary>
        public const string LOGIC_GRID_RESERVATION_DUPLICATE_KEY = "LOGIC_GRID_RESERVATION_DUPLICATE_KEY";

        /// <summary>内部所有权矛盾（单元行与实际索引不一致）⇒ 必须令 Step 失败，不得伪造 Retry。</summary>
        public const string LOGIC_GRID_OWNERSHIP_CONTRADICTION = "LOGIC_GRID_OWNERSHIP_CONTRADICTION";

        /// <summary><c>ApplyBatchRelocation</c> 输入重复 UnitId（仅诊断用，不用于选赢家）。</summary>
        public const string LOGIC_GRID_BATCH_DUPLICATE_UNIT = "LOGIC_GRID_BATCH_DUPLICATE_UNIT";

        /// <summary><c>ApplyBatchRelocation</c> 的单位当前锚点不等于 <c>ExpectedFrom</c>。</summary>
        public const string LOGIC_GRID_BATCH_EXPECTED_FROM_MISMATCH = "LOGIC_GRID_BATCH_EXPECTED_FROM_MISMATCH";

        /// <summary><c>ApplyBatchRelocation</c> 的批次目标彼此重叠。</summary>
        public const string LOGIC_GRID_BATCH_TARGET_OVERLAP = "LOGIC_GRID_BATCH_TARGET_OVERLAP";

        /// <summary><c>ApplyBatchRelocation</c> 仍有未被清理的待抢占 Reservation。</summary>
        public const string LOGIC_GRID_BATCH_RESERVATION_NOT_CLEARED = "LOGIC_GRID_BATCH_RESERVATION_NOT_CLEARED";

        /// <summary><c>ApplyBatchRelocation</c> 输入为 null 或空批。</summary>
        public const string LOGIC_GRID_BATCH_INVALID = "LOGIC_GRID_BATCH_INVALID";
    }

    /// <summary>
    /// 启动门禁的<strong>空间</strong>三分结果（任务包「必须产出」6 第二段）。
    ///
    /// 只有<strong>来自权威占位/Reservation 区间、ReleaseTick 有限且严格晚于当前 Tick</strong>
    /// 的阻塞才是 <see cref="RetryableTimedBlock"/>。静态非法格、无结束边界的占位与内部所有权
    /// 矛盾一律是 <see cref="TerminalOrUnknownBlock"/>——<strong>绝不</strong>伪造成 <c>tick + 1</c>。
    /// </summary>
    public enum SpaceBlockKind
    {
        /// <summary>该单位的目的地当前无阻塞。</summary>
        Free = 0,

        /// <summary>有限、严格将来的权威释放边界（可延期到该 Tick）。</summary>
        RetryableTimedBlock = 1,

        /// <summary>静态非法格、无界占位或所有权矛盾：不是"下一 Tick 再试"。</summary>
        TerminalOrUnknownBlock = 2
    }

    /// <summary>一次空间门禁查询的只读结果。</summary>
    public readonly struct SpaceBlockQueryResult
    {
        private SpaceBlockQueryResult(SpaceBlockKind kind, long releaseTick, string detail)
        {
            Kind = kind;
            ReleaseTick = releaseTick;
            Detail = detail;
        }

        public SpaceBlockKind Kind { get; }

        /// <summary>仅当 <see cref="Kind"/> 为 <see cref="SpaceBlockKind.RetryableTimedBlock"/> 时有限。</summary>
        public long ReleaseTick { get; }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Detail { get; }

        public bool IsFree => Kind == SpaceBlockKind.Free;

        public bool IsRetryable => Kind == SpaceBlockKind.RetryableTimedBlock;

        public bool IsTerminalOrUnknown => Kind == SpaceBlockKind.TerminalOrUnknownBlock;

        public static readonly SpaceBlockQueryResult Free = new SpaceBlockQueryResult(SpaceBlockKind.Free, 0L, "free");

        public static SpaceBlockQueryResult Retryable(long releaseTick, string detail)
            => new SpaceBlockQueryResult(SpaceBlockKind.RetryableTimedBlock, releaseTick, detail);

        public static SpaceBlockQueryResult Terminal(string detail)
            => new SpaceBlockQueryResult(SpaceBlockKind.TerminalOrUnknownBlock, 0L, detail);
    }
}
