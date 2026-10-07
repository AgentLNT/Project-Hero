using System;
using System.Globalization;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.AI
{
    /// <summary>
    /// <strong>一条普通动作候选</strong>：种类、来源单位、ActionSpec、可空目标、可空目的格与可空计划 ID。
    ///
    /// 它是<strong>纯只读值对象</strong>：不携带 ControllerId、来源种类、优先级、
    /// <c>ProducerOrdinal</c>、<c>CommandSequence</c>、规则费用或反应 TriggerTick——
    /// 这些字段在类型上不存在，因此 AI 无法自报身份或顺序。
    ///
    /// <para>
    /// <see cref="SortActionSpecId"/>/<see cref="SortTargetUnitId"/>/<see cref="SortPlanId"/> 是
    /// <strong>稳定排序键</strong>；<see cref="CompareStableKeyTo"/> 是唯一比较实现，
    /// 它<strong>不含</strong>候选集合的枚举序号——原始顺序绝不成为末级键。
    /// </para>
    /// </summary>
    public sealed record AiCandidate(
        AiCandidateKind Kind,
        UnitId OwnerUnitId,
        ActionSpecId ActionSpecId,
        UnitId? TargetUnitId,
        GridPoint? Destination,
        ActionPlanId PlanId,
        AiCandidateEligibility Eligibility)
    {
        /// <summary>
        /// 新增（<see cref="AiCandidateKind.Add"/>）操作在<strong>本次事务内</strong>的临时键。
        ///
        /// 它在稳定排序<strong>之后</strong>按名次派生，因此与候选集合的原始枚举顺序无关；
        /// 它<strong>不是</strong> <c>ActionPlanId</c>：正式 ID 只能由 Logic 在事务校验通过后分配。
        /// </summary>
        public long TemporaryPlanKey { get; init; }

        /// <summary>排序用 <c>ActionSpecId</c>（Remove/Move 的 spec 缺失时退化为空串）。</summary>
        public string SortActionSpecId => ActionSpecId.Value ?? string.Empty;

        /// <summary>排序用 <c>TargetUnitId</c>（0 = 无目标，恒排在任何有效 ID 之前）。</summary>
        public long SortTargetUnitId => TargetUnitId.HasValue ? TargetUnitId.Value.Value : 0L;

        /// <summary>排序用 <c>ActionPlanId</c>（0 = 本次事务新分配，恒排在最前）。</summary>
        public long SortPlanId => PlanId.Value;

        public bool IsEligible => Eligibility == AiCandidateEligibility.Eligible;

        /// <summary>
        /// 稳定全序：<c>Kind → OwnerUnitId → ActionSpecId(Ordinal) → TargetUnitId → PlanId</c>。
        /// 逐级都是整数或 Ordinal 字符串比较，<strong>没有</strong>哈希码、文化敏感比较或插入序号兜底。
        ///
        /// <para>
        /// <c>OwnerUnitId</c> 必须是键的一部分：一个控制者可以同时控制多个单位，
        /// 缺了它，两个单位的同 <c>ActionSpec</c> 候选就会依赖容器枚举序排前后，
        /// 于是"候选排列不改变决策"就不成立。
        /// </para>
        /// </summary>
        public int CompareStableKeyTo(AiCandidate other)
        {
            if (other == null) return 1;
            int byKind = ((int)Kind).CompareTo((int)other.Kind);
            if (byKind != 0) return byKind;
            int byOwner = OwnerUnitId.Value.CompareTo(other.OwnerUnitId.Value);
            if (byOwner != 0) return byOwner;
            int bySpec = string.CompareOrdinal(SortActionSpecId, other.SortActionSpecId);
            if (bySpec != 0) return bySpec;
            int byTarget = SortTargetUnitId.CompareTo(other.SortTargetUnitId);
            if (byTarget != 0) return byTarget;
            return SortPlanId.CompareTo(other.SortPlanId);
        }

        public override string ToString()
            => Kind + ":" + SortActionSpecId + ":unit" +
               OwnerUnitId.Value.ToString(CultureInfo.InvariantCulture) + ":target" +
               SortTargetUnitId.ToString(CultureInfo.InvariantCulture) + ":plan" +
               SortPlanId.ToString(CultureInfo.InvariantCulture) + ":" + Eligibility;
    }
}
