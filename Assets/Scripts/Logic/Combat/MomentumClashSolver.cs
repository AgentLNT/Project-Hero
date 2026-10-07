using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 冲突组配额（<strong>由调用方传入，Combat 侧不持有图上限的真值</strong>）。
    ///
    /// 归属裁定（父代理）：节点/边/目标三个上限与 <c>CONFLICT_GROUP_LIMIT_EXCEEDED</c> 归
    /// <c>Logic/Interactions</c>（构图流，冲突组是它的概念）。动量求解只需在"整组失败"语义上与之对齐，
    /// 因此把数值与错误码作为参数接收：集成侧传
    /// <c>new MomentumClashQuota(InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED, ConflictGraphLimits.MaxConflictGroupNodes, ConflictGraphLimits.MaxConflictGroupEdges)</c>。
    /// </summary>
    public sealed record MomentumClashQuota(string LimitExceededErrorCode, int MaxNodes, int MaxEdges)
    {
        /// <summary>错误码必须非空、上限必须为正（否则配额本身无意义）。</summary>
        public string Validate()
        {
            if (string.IsNullOrEmpty(LimitExceededErrorCode)) return MomentumCodes.CLASH_INPUT_INVALID;
            if (MaxNodes <= 0 || MaxEdges <= 0) return MomentumCodes.CLASH_INPUT_INVALID;
            return null;
        }
    }

    /// <summary>Clash 参与者：某攻击计划及其<strong>原始</strong>整数动量（不随求解变化）。</summary>
    public sealed record ClashParticipant(UnitId OwnerUnitId, ActionPlanId ActionPlanId, MomentumPacket Momentum);

    /// <summary>一条 Attack-Attack 冲突边（无序；必须已由构图侧按空间/交互规则连边）。</summary>
    public sealed record ClashPair(ActionPlanId A, ActionPlanId B);

    /// <summary>多方 Clash 求解输入：本冲突组的全部参与攻击 + 全部 Attack-Attack 边。</summary>
    public sealed record MomentumClashInput(
        IReadOnlyList<ClashParticipant> Participants,
        IReadOnlyList<ClashPair> Pairs);

    /// <summary>单个对手施加给本参与者的损耗（审计单位：动量单位）。</summary>
    public sealed record ClashOppositionLoss(
        UnitId OpponentUnitId,
        ActionPlanId OpponentActionPlanId,
        int RingDistance,
        int OppositionFactorQ10,
        int SufferedLossUnits,
        int ImposedLossUnits);

    /// <summary>
    /// 一条 <c>ClashResidualImpact</c>：正 <c>RemainingMomentum</c> 按稳定权重分配给<strong>直接 Clash 对手</strong>
    /// 的结果（任务包 08:107）。方向是<strong>来源攻击</strong>的方向（剩余动量沿来源方向推入对手）。
    /// </summary>
    public sealed record ClashResidualImpact(
        UnitId RecipientUnitId,
        ActionPlanId RecipientActionPlanId,
        UnitId SourceUnitId,
        ActionPlanId SourceActionPlanId,
        GridDirection SourceDirection,
        int ResidualMomentumUnits);

    /// <summary>单个参与攻击的 Clash 结算（全部使用原始动量同时求解）。</summary>
    public sealed record ClashParticipantResolution(
        UnitId OwnerUnitId,
        ActionPlanId ActionPlanId,
        GridDirection Direction,
        int OriginalMomentumUnits,
        long TotalOppositionLossUnits,
        int RemainingMomentumUnits,
        IReadOnlyList<ClashOppositionLoss> Losses,
        IReadOnlyList<ClashResidualImpact> ResidualImpacts);

    /// <summary>
    /// 冲突组 Clash 的不可变结果。
    ///
    /// <see cref="TerminatedActionPlanIds"/>：<strong>所有</strong>参与 Clash 的攻击都终止
    /// （任务包 08:107），调用方据此让这些攻击**尚未消解的直接 Hit 失效**
    /// （<c>ClashingAttackCannotAlsoHitUnrelatedAoeTarget</c>）。
    /// </summary>
    public sealed record MomentumClashResolution(
        IReadOnlyList<ClashParticipantResolution> Participants,
        IReadOnlyList<ClashResidualImpact> ResidualImpacts,
        IReadOnlyList<ActionPlanId> TerminatedActionPlanIds,
        long TotalRemainingMomentumUnits,
        long TotalResidualAllocatedUnits);

    /// <summary>
    /// 任务包「必须产出」9 + 10：<strong>多方 Clash 同时求解与剩余动量稳定分配</strong>。
    ///
    /// <strong>9 · 同时求解（禁止逐边修改）</strong>
    /// <code>
    /// TotalOppositionLoss(A) = Σ_edges LossFromBToA(B.OriginalMomentumUnits, d(A,B))
    ///   LossFromBToA        = RoundHalfUp(B.MomentumUnits * OppositionFactorQ10[d] / 1024)
    /// RemainingMomentum(A)   = max(0, A.OriginalMomentumUnits - TotalOppositionLoss(A))   // 一次算出
    /// </code>
    /// 每个攻击的损耗<strong>只读原始 <c>MomentumUnits</c></strong>（任务包 08:106："不能读取同组中间状态"），
    /// 因此三方及以上时不存在"先改 A 再算 B"的顺序依赖；参与者/边的任意排列给出逐位相同的结果。
    /// 只有方向损耗系数 <c>&gt; 0</c> 的 Attack-Attack 接触才是有效 Clash（<c>d ≤ 3</c> 不是）。
    /// 全部参与攻击都终止。
    ///
    /// <strong>10 · 剩余分配（整数比例 + 稳定补齐，总和严格相等）</strong>
    /// <code>
    /// Weight(A→B) = LossFromAToB(A.OriginalMomentumUnits, d(A,B))          // 该边施加给对手的损耗
    /// BaseShare(B) = floor(RemainingMomentum(A) * Weight(A→B) / Σ_B Weight(A→B))
    /// 余数 r = RemainingMomentum(A) - Σ BaseShare  ⇒ 按 (OpponentUnitId, OpponentActionPlanId) 升序给前 r 个 +1
    /// </code>
    /// 因此 <c>Σ_B 分配(B) == RemainingMomentum(A)</c> 严格成立（不丢失、不凭空增加）；
    /// 舍入方式是"基础份额向下取整 + 稳定键补齐"，**不是**最大余数法（两者在多对手时结果不同，
    /// 由 <c>ClashResidualMomentumIsDistributedWithoutLossOrDuplication</c> 钉住）。
    ///
    /// 分配只发给<strong>直接 Clash 对手</strong>；孤立参与者（无有效边）视为调用方未过滤而稳定拒绝，
    /// 因为"终止一个没有对手的攻击并把剩余动量留在手里"会凭空造出无人承接的冲击。
    /// </summary>
    public static class MomentumClashSolver
    {
        /// <summary>求解（不检查冲突组配额）。</summary>
        public static MomentumClashResolution Solve(MomentumClashInput input)
            => Solve(input, null);

        /// <summary>求解（可选冲突组配额：超限时整组以调用方给出的错误码失败）。</summary>
        public static MomentumClashResolution Solve(MomentumClashInput input, MomentumClashQuota quota)
        {
            if (input == null || input.Participants == null || input.Pairs == null)
                throw new LogicDefinitionException(MomentumCodes.CLASH_INPUT_INVALID, "input");
            if (quota != null)
            {
                string quotaError = quota.Validate();
                if (quotaError != null)
                    throw new LogicDefinitionException(quotaError, "quota");
                if (input.Participants.Count > quota.MaxNodes || input.Pairs.Count > quota.MaxEdges)
                    throw new LogicDefinitionException(quota.LimitExceededErrorCode,
                        "nodes=" + input.Participants.Count.ToString(CultureInfo.InvariantCulture) +
                        " edges=" + input.Pairs.Count.ToString(CultureInfo.InvariantCulture));
            }

            int count = input.Participants.Count;
            if (count < 2)
                throw new LogicDefinitionException(MomentumCodes.CLASH_INPUT_INVALID,
                    "participants=" + count.ToString(CultureInfo.InvariantCulture));

            // —— 参与者按 (UnitId, ActionPlanId) 规范化排序；重复计划 ID 稳定拒绝 ——
            var ordered = new List<ClashParticipant>(input.Participants);
            ordered.Sort(CompareParticipants);
            for (int i = 0; i < count; i++)
            {
                ClashParticipant participant = ordered[i];
                if (participant == null || !participant.OwnerUnitId.IsValid || !participant.ActionPlanId.IsValid)
                    throw new LogicDefinitionException(MomentumCodes.CLASH_INPUT_INVALID, "participantIdentity");
                if (participant.Momentum.MomentumUnits <= 0 ||
                    participant.Momentum.MomentumUnits > MomentumLimits.MaxMomentumUnitsPerIntent)
                    throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                        "momentumUnits=" + participant.Momentum.MomentumUnits.ToString(CultureInfo.InvariantCulture));
                if (i > 0 && ordered[i - 1].ActionPlanId == participant.ActionPlanId)
                    throw new LogicDefinitionException(MomentumCodes.CLASH_INPUT_INVALID, "duplicatePlan");
            }

            var indexByPlan = new Dictionary<long, int>(count);
            for (int i = 0; i < count; i++) indexByPlan[ordered[i].ActionPlanId.Value] = i;

            var adjacency = new List<OppositionEdge>[count];
            for (int i = 0; i < count; i++) adjacency[i] = new List<OppositionEdge>();
            var seenPairs = new HashSet<long>();

            for (int e = 0; e < input.Pairs.Count; e++)
            {
                ClashPair pair = input.Pairs[e];
                if (pair == null)
                    throw new LogicDefinitionException(MomentumCodes.CLASH_INPUT_INVALID, "pair");
                if (pair.A == pair.B)
                    throw new LogicDefinitionException(MomentumCodes.CLASH_INPUT_INVALID, "selfPair");
                if (!indexByPlan.TryGetValue(pair.A.Value, out int ia) ||
                    !indexByPlan.TryGetValue(pair.B.Value, out int ib))
                    throw new LogicDefinitionException(MomentumCodes.CLASH_INPUT_INVALID, "unknownEndpoint");

                long lo = Math.Min(pair.A.Value, pair.B.Value);
                long hi = Math.Max(pair.A.Value, pair.B.Value);
                if (!seenPairs.Add(lo * 1000000007L + hi))
                    throw new LogicDefinitionException(MomentumCodes.CLASH_INPUT_INVALID, "duplicatePair");

                int d = MomentumRuleTable.MinimalRingDistance(ordered[ia].Momentum.Direction, ordered[ib].Momentum.Direction);
                int factor = MomentumRuleTable.OppositionFactorQ10(d);
                // 只有方向损耗系数 > 0 的 Attack-Attack 接触才是有效 Clash（d ≤ 3 不是）。
                if (factor <= 0)
                    throw new LogicDefinitionException(MomentumCodes.CLASH_INPUT_INVALID,
                        "ineffectiveClashDistance=" + d.ToString(CultureInfo.InvariantCulture));

                adjacency[ia].Add(new OppositionEdge(ib, d, factor));
                adjacency[ib].Add(new OppositionEdge(ia, d, factor));
            }

            for (int i = 0; i < count; i++)
            {
                if (adjacency[i].Count == 0)
                    throw new LogicDefinitionException(MomentumCodes.CLASH_INPUT_INVALID,
                        "isolatedParticipant=" + ordered[i].ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
                // 稳定键：对手按 (OpponentUnitId, OpponentActionPlanId) 升序 —— 余数补齐顺序即此顺序。
                adjacency[i].Sort((x, y) => CompareOpponents(ordered[x.OpponentIndex], ordered[y.OpponentIndex]));
            }

            // —— 9：从原始动量同时累计全部反向损耗，再一次性得到 RemainingMomentum ——
            // 每个参与者的总损耗 = Σ_对手 LossFromBToA(对手的**原始**动量, d)。
            // 只依赖原始动量与方向差，因此没有"逐边修改后继续计算"的可能（任务包 08:106）。
            var losses = new long[count];
            var remaining = new int[count];
            for (int i = 0; i < count; i++)
            {
                long total = 0L;
                for (int k = 0; k < adjacency[i].Count; k++)
                {
                    OppositionEdge edge = adjacency[i][k];
                    long suffered = MomentumRuleTable.LossFromBToA(
                        ordered[edge.OpponentIndex].Momentum.MomentumUnits, edge.RingDistance);
                    CheckedAdd(ref total, suffered);
                }
                losses[i] = total;
                long leftover = (long)ordered[i].Momentum.MomentumUnits - total;
                remaining[i] = leftover <= 0L ? 0 : (int)leftover;
            }

            // —— 10：正剩余按稳定权重整数比例分配，余数按对手稳定键补齐 ——
            var resolutions = new List<ClashParticipantResolution>(count);
            var allResiduals = new List<ClashResidualImpact>();
            long totalRemaining = 0L;
            long totalAllocated = 0L;

            for (int i = 0; i < count; i++)
            {
                ClashParticipant self = ordered[i];
                var lossDetails = new List<ClashOppositionLoss>(adjacency[i].Count);
                long weightSum = 0L;
                for (int k = 0; k < adjacency[i].Count; k++)
                {
                    OppositionEdge edge = adjacency[i][k];
                    ClashParticipant opponent = ordered[edge.OpponentIndex];
                    // 同一个方向差与同一对原始动量决定两个方向的损耗：对手打我的、我打对手的。
                    int suffered = (int)MomentumRuleTable.LossFromBToA(
                        opponent.Momentum.MomentumUnits, edge.RingDistance);
                    int imposed = (int)MomentumRuleTable.LossFromBToA(
                        self.Momentum.MomentumUnits, edge.RingDistance);
                    weightSum = checked(weightSum + imposed);   // Weight(A→B) = LossFromAToB
                    lossDetails.Add(new ClashOppositionLoss(opponent.OwnerUnitId, opponent.ActionPlanId,
                        edge.RingDistance, edge.OppositionFactorQ10, suffered, imposed));
                }

                var awards = new List<ClashResidualImpact>();
                if (remaining[i] > 0)
                {
                    if (weightSum <= 0L)
                        throw new LogicDefinitionException(MomentumCodes.CLASH_RESIDUAL_ALLOCATION_INVALID,
                            "weightSum=" + weightSum.ToString(CultureInfo.InvariantCulture));
                    long allocated = 0L;
                    var shares = new long[adjacency[i].Count];
                    for (int k = 0; k < shares.Length; k++)
                    {
                        long share;
                        try
                        {
                            // 基础份额 = floor(RemainingMomentum × Weight / ΣWeight)
                            share = checked((long)remaining[i] * lossDetails[k].ImposedLossUnits) / weightSum;
                        }
                        catch (OverflowException ex)
                        {
                            throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE, ex.Message);
                        }
                        shares[k] = share;
                        allocated = checked(allocated + share);
                    }
                    long remainder = remaining[i] - allocated;   // 0 ≤ r < 对手数
                    if (remainder < 0L || remainder > shares.Length)
                        throw new LogicDefinitionException(MomentumCodes.CLASH_RESIDUAL_ALLOCATION_INVALID,
                            "remainder=" + remainder.ToString(CultureInfo.InvariantCulture));

                    // 余数按 (OpponentUnitId, OpponentActionPlanId) 升序补齐（lossDetails 已是该顺序）。
                    for (int k = 0; k < shares.Length; k++)
                    {
                        long units = shares[k] + (k < remainder ? 1L : 0L);
                        if (units <= 0L) continue;
                        ClashParticipant opponent = ordered[adjacency[i][k].OpponentIndex];
                        awards.Add(new ClashResidualImpact(
                            opponent.OwnerUnitId, opponent.ActionPlanId,
                            self.OwnerUnitId, self.ActionPlanId, self.Momentum.Direction, (int)units));
                        totalAllocated = checked(totalAllocated + units);
                    }
                }

                totalRemaining = checked(totalRemaining + remaining[i]);
                resolutions.Add(new ClashParticipantResolution(
                    self.OwnerUnitId, self.ActionPlanId, self.Momentum.Direction,
                    self.Momentum.MomentumUnits, losses[i], remaining[i],
                    lossDetails.AsReadOnly(), awards.AsReadOnly()));
                allResiduals.AddRange(awards);
            }

            if (totalAllocated != totalRemaining)
                throw new LogicDefinitionException(MomentumCodes.CLASH_RESIDUAL_ALLOCATION_INVALID,
                    "allocated=" + totalAllocated.ToString(CultureInfo.InvariantCulture) +
                    " remaining=" + totalRemaining.ToString(CultureInfo.InvariantCulture));

            var terminated = new List<ActionPlanId>(count);
            for (int i = 0; i < count; i++) terminated.Add(ordered[i].ActionPlanId);
            terminated.Sort((x, y) => x.Value.CompareTo(y.Value));

            allResiduals.Sort(CompareResidualImpacts);

            return new MomentumClashResolution(resolutions.AsReadOnly(), allResiduals.AsReadOnly(),
                terminated.AsReadOnly(), totalRemaining, totalAllocated);
        }

        /// <summary>稳定键：对手 (UnitId, ActionPlanId) 升序（分配余数补齐顺序）。</summary>
        public static int CompareOpponents(ClashParticipant x, ClashParticipant y)
        {
            int byUnit = x.OwnerUnitId.Value.CompareTo(y.OwnerUnitId.Value);
            return byUnit != 0 ? byUnit : x.ActionPlanId.Value.CompareTo(y.ActionPlanId.Value);
        }

        private static int CompareParticipants(ClashParticipant x, ClashParticipant y)
        {
            if (x == null) return y == null ? 0 : -1;
            if (y == null) return 1;
            return CompareOpponents(x, y);
        }

        private static int CompareResidualImpacts(ClashResidualImpact x, ClashResidualImpact y)
        {
            int byRecipient = x.RecipientUnitId.Value.CompareTo(y.RecipientUnitId.Value);
            if (byRecipient != 0) return byRecipient;
            int byRecipientPlan = x.RecipientActionPlanId.Value.CompareTo(y.RecipientActionPlanId.Value);
            if (byRecipientPlan != 0) return byRecipientPlan;
            int bySource = x.SourceUnitId.Value.CompareTo(y.SourceUnitId.Value);
            return bySource != 0 ? bySource : x.SourceActionPlanId.Value.CompareTo(y.SourceActionPlanId.Value);
        }

        private static void CheckedAdd(ref long accumulator, long value)
        {
            try
            {
                accumulator = checked(accumulator + value);
            }
            catch (OverflowException ex)
            {
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE, ex.Message);
            }
        }

        private readonly struct OppositionEdge
        {
            public readonly int OpponentIndex;
            public readonly int RingDistance;
            public readonly int OppositionFactorQ10;

            public OppositionEdge(int opponentIndex, int ringDistance, int oppositionFactorQ10)
            {
                OpponentIndex = opponentIndex;
                RingDistance = ringDistance;
                OppositionFactorQ10 = oppositionFactorQ10;
            }
        }
    }
}
