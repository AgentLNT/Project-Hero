using System;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Movement
{
    /// <summary>
    /// 预留的稳定定位键：<c>(ActionPlanId, StepIndex)</c>。
    /// 与 <see cref="MovementSegment"/> 共用同一把键，因此"段的预留"永远可被该段重算/清理精确定位，
    /// 不存在独立的 <c>ReservationId</c> 计数状态。
    /// </summary>
    public readonly struct ReservationKey : IEquatable<ReservationKey>
    {
        public ReservationKey(ActionPlanId actionPlanId, int stepIndex)
        {
            ActionPlanId = actionPlanId;
            StepIndex = stepIndex;
        }

        public ActionPlanId ActionPlanId { get; }

        public int StepIndex { get; }

        public bool Equals(ReservationKey other)
            => ActionPlanId.Value == other.ActionPlanId.Value && StepIndex == other.StepIndex;

        public override bool Equals(object obj) => obj is ReservationKey other && Equals(other);

        public override int GetHashCode()
            => (ActionPlanId.Value * 397L ^ StepIndex).GetHashCode();

        public override string ToString()
            => "res#" + ActionPlanId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               "/" + StepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <strong>空间 Reservation</strong>（任务包「必须产出」2/4/5）：
    /// 一个移动段在 <c>[StartTick, EndTick)</c> 期间为目标格 <see cref="Cell"/> 持有的排他占位意图。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>预留冲突<strong>只</strong>服从任务 03 网关的 <c>CommandSequence</c>：
    /// 先成功提交的有效命令取得该格，后续冲突命令稳定拒绝，
    /// <strong>不能</strong>抢占此前已提交的计划；</item>
    /// <item>Editable 计划持有的未来 Reservation 属于该计划，可被<strong>同一原子批次</strong>
    /// 整体替换，但仍会阻挡其他计划；</item>
    /// <item>释放按稳定空间键 <c>(StartTick, Cell.X, Cell.Y, ActionPlanId, StepIndex)</c> 升序进行，
    /// 因此释放顺序与容器枚举顺序无关。</item>
    /// </list>
    /// </summary>
    public sealed record Reservation(
        ReservationKey Key,
        UnitId UnitId,
        GridPoint Cell,
        long StartTick,
        long EndTick)
    {
        public ActionPlanId ActionPlanId => Key.ActionPlanId;

        public int StepIndex => Key.StepIndex;

        /// <summary>该预留是否仍会在 <paramref name="tick"/> 及之后生效（半开区间）。</summary>
        public bool IsLiveAt(long tick) => tick < EndTick;

        /// <summary>规范释放/枚举顺序：<c>(StartTick, X, Y, ActionPlanId, StepIndex)</c> 升序。</summary>
        public static int CompareCanonical(Reservation a, Reservation b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int byStart = a.StartTick.CompareTo(b.StartTick);
            if (byStart != 0) return byStart;
            int byX = a.Cell.X.CompareTo(b.Cell.X);
            if (byX != 0) return byX;
            int byY = a.Cell.Y.CompareTo(b.Cell.Y);
            if (byY != 0) return byY;
            int byPlan = a.ActionPlanId.Value.CompareTo(b.ActionPlanId.Value);
            return byPlan != 0 ? byPlan : a.StepIndex.CompareTo(b.StepIndex);
        }

        public override string ToString()
            => Key + "@" + Cell + "[" + StartTick.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               "," + EndTick.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>
    /// <c>LogicGrid.ApplyBatchRelocation</c> 的单个输入（任务包「必须产出」13）。
    /// 它只声明"哪个单位应从哪个锚点移到哪个锚点"，朝向保持不变；
    /// 本原语<strong>不</strong>求解强制位移（那是任务 08），只做全批验证 + 统一提交。
    /// </summary>
    public sealed record BatchRelocation(UnitId UnitId, GridPoint ExpectedFrom, GridPoint To);
}
