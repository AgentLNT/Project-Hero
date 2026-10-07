using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Commands
{
    /// <summary>
    /// 一次「来源单位 → 候选单位」的<strong>只读资格判定结果</strong>
    /// （分类 + 该 Attack 掩码下的可用性）。它只是查询投影，不携带任何可写状态。
    /// </summary>
    public readonly struct TargetCandidate
    {
        public TargetCandidate(UnitId unitId, UnitRelation relation, bool isEligible)
        {
            UnitId = unitId;
            Relation = relation;
            IsEligible = isEligible;
        }

        public UnitId UnitId { get; }

        /// <summary>唯一 <c>IFactionRelationResolver</c> 给出的分类。</summary>
        public UnitRelation Relation { get; }

        /// <summary>该分类的显式掩码位（<c>Self/Allied/Neutral/Hostile</c> 各占一位）。</summary>
        public TargetRelationMask MaskBit => TargetCandidateQuery.MaskBitOf(Relation);

        /// <summary>该候选是否被<strong>这一次</strong> ActionSpec 的 <c>AllowedTargetRelations</c> 放行。</summary>
        public bool IsEligible { get; }
    }

    /// <summary>
    /// <strong><c>TargetCandidateQuery</c></strong>（任务 09「必须产出」4 第三/四段、
    /// 冻结件 §1.2「AI/玩家的统一目标资格查询面」）：
    /// UI 候选筛面与 AI 候选筛面<strong>共用</strong>的<strong>唯一</strong>目标资格查询实现。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>它只持有一个 <see cref="IFactionRelationResolver"/> <strong>引用</strong>
    /// （由 <see cref="From(DecisionSnapshot)"/> 从只读决策快照取，或由装配方直接给出整场那一个实例）。
    /// 它<strong>不</strong>复制关系矩阵、<strong>不</strong>缓存分类结果，
    /// 因此"UI、AI 与命令处理读同一份关系"是结构事实而不是纪律。</item>
    /// <item>放行判据只调用 <see cref="IFactionRelationResolver.Allows"/>，
    /// 与 <c>ActionPlanFactory.ValidatePrimaryTarget</c> 用的是<strong>同一个方法</strong>；
    /// 被拒时的稳定码固定为 <see cref="ScheduleCodes.TARGET_RELATION_NOT_ALLOWED"/>
    /// （与命令层<strong>同一个常量</strong>，不存在第二个关系拒绝码）。</item>
    /// <item>它<strong>不</strong>接受 <c>ControllerId</c>、<c>CommandSourceKind</c>、玩家标志、
    /// Tag/Layer、名称或"本地敌人列表"作为输入——这些参数在类型上根本不存在，
    /// 因此"换一个 Controller 或换一种来源种类"不会改变任何单位的资格。</item>
    /// <item>它<strong>不</strong>写任何状态：候选筛面与命令处理之间没有第二条写入路径，
    /// 展示永远不是授权——确认时命令层仍然重新校验（<c>ScheduleEditor</c> →
    /// <c>ActionPlanFactory.ValidatePrimaryTarget</c>）。</item>
    /// </list>
    /// </summary>
    public sealed class TargetCandidateQuery
    {
        private readonly IFactionRelationResolver _factions;

        public TargetCandidateQuery(IFactionRelationResolver factions)
        {
            _factions = factions ?? throw new ArgumentNullException(nameof(factions));
        }

        /// <summary>
        /// 从只读决策快照构造：<strong>UI 与 AI 的唯一构造入口</strong>。
        /// 快照自己持有整场唯一的解析器实例，本查询直接引用它，绝不复制。
        /// </summary>
        public static TargetCandidateQuery From(DecisionSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return new TargetCandidateQuery(snapshot.FactionResolver);
        }

        /// <summary>整场唯一的只读阵营关系解析器（与决策快照是<strong>同一个</strong>实例）。</summary>
        public IFactionRelationResolver Factions => _factions;

        /// <summary>单位关系分类（转发唯一解析器，不做任何二次判定）。</summary>
        public UnitRelation Classify(UnitId ownerUnitId, UnitId candidateUnitId)
            => _factions.Classify(ownerUnitId, candidateUnitId);

        /// <summary><see cref="UnitRelation"/> 对应的显式掩码位（唯一映射）。</summary>
        public static TargetRelationMask MaskBitOf(UnitRelation relation)
        {
            switch (relation)
            {
                case UnitRelation.Self: return TargetRelationMask.Self;
                case UnitRelation.Allied: return TargetRelationMask.Allied;
                case UnitRelation.Neutral: return TargetRelationMask.Neutral;
                case UnitRelation.Hostile: return TargetRelationMask.Hostile;
                default:
                    throw new ProjectHero.Logic.LogicDefinitionException(
                        FactionCodes.FACTION_RELATION_INVARIANT_VIOLATION,
                        "unknown unit relation: " + relation);
            }
        }

        /// <summary>
        /// 该 ActionSpec 是否允许该候选。非攻击动作没有关系掩码 ⇒ 恒为 <c>false</c>
        /// （目标资格只对携带 <c>AttackPayloadSpec</c> 的动作有意义，绝不猜一个默认掩码）。
        /// </summary>
        public bool IsEligible(ActionSpec spec, UnitId ownerUnitId, UnitId candidateUnitId)
            => EligibilityOf(spec, ownerUnitId, candidateUnitId) == null;

        /// <summary>
        /// 与 <see cref="IsEligible"/> 同一判定，但返回<strong>命令层同款稳定码</strong>：
        /// 放行 = <c>null</c>；掩码非法 = <c>TARGET_RELATION_MASK_INVALID</c>；
        /// 分类不在掩码内 = <see cref="ScheduleCodes.TARGET_RELATION_NOT_ALLOWED"/>。
        /// 掩码<strong>非法</strong>与关系<strong>不允许</strong>是两个不同的稳定原因，
        /// 不得被折叠成同一个码。
        /// </summary>
        public string EligibilityOf(ActionSpec spec, UnitId ownerUnitId, UnitId candidateUnitId)
        {
            if (spec == null) return ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND;
            var attack = spec.Payload as AttackPayloadSpec;
            if (attack == null) return ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND;
            if (!TargetRelationMasks.IsValid(attack.AllowedTargetRelations))
                return FactionCodes.TARGET_RELATION_MASK_INVALID;

            return _factions.Allows(attack.AllowedTargetRelations, ownerUnitId, candidateUnitId)
                ? null
                : ScheduleCodes.TARGET_RELATION_NOT_ALLOWED;
        }

        /// <summary>
        /// 该 ActionSpec 在该候选集合上放行的目标（按 <c>UnitId</c> 升序、去重）。
        /// 掩码显式位是<strong>唯一</strong>开关：敌对的位不置位时，连敌对单位也不会出现。
        /// </summary>
        public IReadOnlyList<UnitId> CandidatesFor(
            ActionSpec spec, UnitId ownerUnitId, IReadOnlyList<UnitId> universe)
        {
            IReadOnlyList<TargetCandidate> all = Inspect(spec, ownerUnitId, universe);
            var eligible = new List<UnitId>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].IsEligible) eligible.Add(all[i].UnitId);
            }
            return eligible;
        }

        /// <summary>
        /// 完整的只读候选视图（分类 + 可用性），按 <c>UnitId</c> 升序、去重。
        /// UI/AI 的展示面与 <see cref="CandidatesFor"/> 读的是同一份判定。
        /// </summary>
        public IReadOnlyList<TargetCandidate> Inspect(
            ActionSpec spec, UnitId ownerUnitId, IReadOnlyList<UnitId> universe)
        {
            if (universe == null || universe.Count == 0) return Array.Empty<TargetCandidate>();

            var ordered = new List<UnitId>(universe.Count);
            for (int i = 0; i < universe.Count; i++)
            {
                if (universe[i].IsValid) ordered.Add(universe[i]);
            }
            ordered.Sort((a, b) => a.Value.CompareTo(b.Value));

            var result = new List<TargetCandidate>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i > 0 && ordered[i].Value == ordered[i - 1].Value) continue;
                UnitId candidate = ordered[i];
                UnitRelation relation = _factions.Classify(ownerUnitId, candidate);
                result.Add(new TargetCandidate(candidate, relation, IsEligible(spec, ownerUnitId, candidate)));
            }
            return result;
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe(ActionSpec spec, UnitId ownerUnitId, UnitId candidateUnitId)
        {
            UnitRelation relation = _factions.Classify(ownerUnitId, candidateUnitId);
            string eligibility = EligibilityOf(spec, ownerUnitId, candidateUnitId);
            return "relation=" + relation.ToString() +
                   " maskBit=" + ((int)MaskBitOf(relation)).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                   " eligibility=" + (eligibility ?? "ALLOWED");
        }
    }
}
