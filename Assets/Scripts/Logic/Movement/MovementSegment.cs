using System;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Movement
{
    /// <summary>移动段/预留的稳定失败码（任务 06；只增不改）。</summary>
    public static class MovementCodes
    {
        /// <summary>起止格相同或方向不是规范单步（<c>GridNeighborTable</c> 中不存在该偏移）。</summary>
        public const string MOVEMENT_SEGMENT_DIRECTION_NOT_CANONICAL = "MOVEMENT_SEGMENT_DIRECTION_NOT_CANONICAL";

        /// <summary><c>StepIndex</c> 为负。</summary>
        public const string MOVEMENT_SEGMENT_STEP_INDEX_INVALID = "MOVEMENT_SEGMENT_STEP_INDEX_INVALID";

        /// <summary>区间不是非空半开区间（<c>EndTick</c> 必须严格大于 <c>StartTick</c>）。</summary>
        public const string MOVEMENT_SEGMENT_INTERVAL_INVALID = "MOVEMENT_SEGMENT_INTERVAL_INVALID";

        /// <summary>时长算术溢出 ⇒ 整候选拒绝，<strong>绝不</strong> clamp。</summary>
        public const string MOVEMENT_TIMING_OVERFLOW = "MOVEMENT_TIMING_OVERFLOW";

        /// <summary>同一个 <c>(ActionPlanId, StepIndex)</c> 出现两次。</summary>
        public const string MOVEMENT_SEGMENT_DUPLICATE_KEY = "MOVEMENT_SEGMENT_DUPLICATE_KEY";

        /// <summary>段链不连续：前一段的 <c>To</c> 不等于后一段的 <c>From</c>。</summary>
        public const string MOVEMENT_SEGMENT_CHAIN_DISCONTINUOUS = "MOVEMENT_SEGMENT_CHAIN_DISCONTINUOUS";

        /// <summary>段链的起始 <c>From</c> 不等于该单位的权威逻辑格。</summary>
        public const string MOVEMENT_SEGMENT_ORIGIN_MISMATCH = "MOVEMENT_SEGMENT_ORIGIN_MISMATCH";

        /// <summary>批次替换的段与 Reservation 数量/键不一致。</summary>
        public const string MOVEMENT_RESERVATION_BATCH_MISMATCH = "MOVEMENT_RESERVATION_BATCH_MISMATCH";
    }

    /// <summary>
    /// <strong>MovementSegment</strong>（任务包「必须产出」3）：
    /// 多步路径的每一步一个段，用 <c>(ActionPlanId, StepIndex)</c> 定位，
    /// <strong>不</strong>创建独立 <c>SegmentId</c>。
    ///
    /// 首版离散提交语义（任务包「必须产出」4）：
    /// <list type="bullet">
    /// <item><c>StartTick &lt;= tick &lt; EndTick</c>：单位仍占用 <see cref="From"/>，
    /// <see cref="To"/> 保持一条区间为 <c>[StartTick, EndTick)</c> 的 Reservation；</item>
    /// <item>到 <c>EndTick</c> 的<strong>命令前边界</strong>原子提交到 <see cref="To"/>
    /// 并释放该 Reservation。</item>
    /// </list>
    ///
    /// <see cref="StepWeightUnits"/> <strong>只能</strong>由权威方向表导出（偶 1 / 奇 2），
    /// 调用方无法自填：唯一的构造入口 <see cref="Create"/> 从
    /// <see cref="GridNeighborTable"/> 反查方向、再从 <see cref="PathCostRules"/> 取权重。
    /// </summary>
    public sealed record MovementSegment(
        ActionPlanId ActionPlanId,
        int StepIndex,
        UnitId UnitId,
        GridPoint From,
        GridPoint To,
        GridDirection Direction,
        int StepWeightUnits,
        long StartTick,
        long EndTick)
    {
        /// <summary>该段在 <paramref name="tick"/> 上是否"单位仍占 From"（半开区间）。</summary>
        public bool IsInFlightAt(long tick) => tick >= StartTick && tick < EndTick;

        /// <summary>命令前边界是否应在本 Tick 提交该段。</summary>
        public bool CommitsAt(long tick) => tick >= EndTick;

        /// <summary>段时长（Tick）。首版严格等于 <c>StepWeightUnits * ResolvedBaseStepTicks</c>。</summary>
        public long DurationTicks => EndTick - StartTick;

        /// <summary>
        /// 规范构造：方向与权重都来自权威表，时长使用 <strong>checked</strong> 乘法。
        /// 任一失败以稳定码抛 <see cref="LogicDefinitionException"/>
        /// （溢出<strong>不</strong> clamp，整候选拒绝由调用方负责）。
        /// </summary>
        public static MovementSegment Create(
            ActionPlanId actionPlanId,
            int stepIndex,
            UnitId unitId,
            GridPoint from,
            GridPoint to,
            long startTick,
            PathCostRules costRules,
            int resolvedBaseStepTicks)
        {
            if (stepIndex < 0)
                throw new LogicDefinitionException(MovementCodes.MOVEMENT_SEGMENT_STEP_INDEX_INVALID,
                    stepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (costRules == null)
                throw new LogicDefinitionException(PathCostRules.PATH_COST_WEIGHT_INVALID);
            if (resolvedBaseStepTicks <= 0)
                throw new LogicDefinitionException(MovementCodes.MOVEMENT_SEGMENT_INTERVAL_INVALID,
                    "baseStepTicks=" + resolvedBaseStepTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (!GridNeighborTable.TryGetDirection(from, to, out GridDirection direction))
                throw new LogicDefinitionException(MovementCodes.MOVEMENT_SEGMENT_DIRECTION_NOT_CANONICAL,
                    from + " -> " + to);

            int weight = costRules.StepWeightUnits(direction);
            if (weight <= 0)
                throw new LogicDefinitionException(PathCostRules.PATH_COST_WEIGHT_INVALID);

            long duration;
            long endTick;
            try
            {
                duration = checked((long)weight * resolvedBaseStepTicks);
                endTick = checked(startTick + duration);
            }
            catch (OverflowException)
            {
                throw new LogicDefinitionException(MovementCodes.MOVEMENT_TIMING_OVERFLOW,
                    "weight=" + weight.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " base=" + resolvedBaseStepTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " start=" + startTick.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (endTick <= startTick)
                throw new LogicDefinitionException(MovementCodes.MOVEMENT_SEGMENT_INTERVAL_INVALID,
                    "start=" + startTick.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " end=" + endTick.ToString(System.Globalization.CultureInfo.InvariantCulture));

            return new MovementSegment(actionPlanId, stepIndex, unitId, from, to, direction, weight, startTick, endTick);
        }

        /// <summary>规范化排序键：<c>(ActionPlanId, StepIndex)</c> 升序（定位键）。</summary>
        public static int Compare(MovementSegment a, MovementSegment b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int byPlan = a.ActionPlanId.Value.CompareTo(b.ActionPlanId.Value);
            return byPlan != 0 ? byPlan : a.StepIndex.CompareTo(b.StepIndex);
        }

        public override string ToString()
            => "seg#" + ActionPlanId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               "/" + StepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " " + From + "->" + To + "[" + StartTick.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               "," + EndTick.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")w" + StepWeightUnits;
    }
}
