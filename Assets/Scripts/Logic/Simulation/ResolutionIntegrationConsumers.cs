using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Simulation
{
    /// <summary>
    /// 任务 08 集成段第三步的<strong>两个装配点真实现</strong>（照任务 06 小修轮 R4 的
    /// <c>MovementPathCalculator</c> / <c>DisplacementSolver</c> 先例：装配层未显式注入时由
    /// <c>BattleSimulation</c> 绑定"本场真实现"，因此"未注入"在装配层不是"默认不工作"）。
    ///
    /// <list type="number">
    /// <item><see cref="AdrenalineAccrualFactSource"/>：Tick 末肾上腺素入账事实
    /// （任务包「必须产出」16）。它<strong>只读</strong>本 Tick 的分阶段求解产物与伤害提交报告，
    /// 不持有任何账本，因此"逐接触直接加 <c>Available</c>"在类型层就不可能；</item>
    /// <item><see cref="DisplacementRequestBuilder"/>：按 <c>DamageCommitReport.Units[]</c> 生成
    /// 每单位唯一的 <see cref="ForcedDisplacementRequest"/>（任务包「必须产出」18）。</item>
    /// </list>
    /// </summary>
    internal static class ResolutionIntegrationCodes
    {
        /// <summary>同一单位在同一 Tick 被判给两个不同冲突组（聚合面与图面矛盾）⇒ 稳定拒绝。</summary>
        public const string STEP_ACCRUAL_GROUP_AMBIGUOUS = "STEP_ACCRUAL_GROUP_AMBIGUOUS";
    }

    /// <summary>
    /// 任务包「必须产出」16 的<strong>规范聚合入账事实来源</strong>（Tick 末、全部 Resolution 提交之后）。
    ///
    /// <para><strong>三项聚合规则（逐条可失败）</strong></para>
    /// <list type="number">
    /// <item><strong>每单位每 Tick 至多一条</strong>：实际造成/承受的<strong>最终</strong>伤害各一项。
    /// "最终"= 已经过被动抵抗与 Guard/Block 动作抵抗、并已由阶段 11 提交到权威生命的那个数
    /// （直接取自 <see cref="DamageCommitReport.Units"/>，<strong>不</strong>在这里重算）。</item>
    /// <item><strong>每单位每 <c>ConflictGroup</c> 至多一个 Clash 成功键</strong>：
    /// 同一单位在同一冲突组里参与多个连通块只算一次；不同冲突组各算一次。</item>
    /// <item><strong>每反应计划至多一次成功 Block/Dodge 键</strong>：
    /// 一个 Block 计划面对多条合格入射接触只加一次；Dodge 只在
    /// "位置已提交 <strong>且</strong> 至少一条旧有可动接触因换位失效"时成功。</item>
    /// </list>
    ///
    /// <para><strong>失败反应不得获得成功奖励</strong>：<c>BlockIneffective</c>、
    /// <c>DodgeNotCommitted</c>、<c>DodgeCommittedWithoutInvalidation</c> 三类形态
    /// 在本实现里走的是"不加任何计数"的分支，而不是"加了再减"。</para>
    ///
    /// <para><strong>输出顺序</strong>：按 <c>UnitId</c> 严格升序、每单位至多一条——
    /// 这正是任务 07 的 <c>AdrenalineLedgerRegistry.ApplyTickEndAccrual</c> 唯一入口的入参契约
    /// （乱序或重复键会被账本显式拒绝，因此本类型没有"静默修正"的余地）。</para>
    ///
    /// <para><strong>为什么它是只读消费者</strong>：两个来源都是不可变值产物，
    /// 因此重复调用 <see cref="BuildAccrualFactsOrdered"/> 得到逐位相同的结果，
    /// 也不会让 <c>Available</c> 增长两次（真正的写入只发生在账本里一次）。</para>
    /// </summary>
    public sealed class AdrenalineAccrualFactSource : IAdrenalineAccrualFactSource
    {
        private readonly Func<StagedConflictResolution> _resolutionSource;
        private readonly Func<DamageCommitReport> _commitReportSource;

        public AdrenalineAccrualFactSource(
            Func<StagedConflictResolution> resolutionSource,
            Func<DamageCommitReport> commitReportSource)
        {
            _resolutionSource = resolutionSource ?? throw new ArgumentNullException(nameof(resolutionSource));
            _commitReportSource = commitReportSource ?? throw new ArgumentNullException(nameof(commitReportSource));
        }

        /// <summary>最近一次构建出的规范事实（只读观察面；未构建时为空）。</summary>
        public IReadOnlyList<AdrenalineAccrualFacts> LastFacts { get; private set; }
            = Array.Empty<AdrenalineAccrualFacts>();

        public IReadOnlyList<AdrenalineAccrualFacts> BuildAccrualFactsOrdered(long tick)
        {
            var accumulators = new List<Accumulator>();
            StagedConflictResolution resolution = _resolutionSource();
            DamageCommitReport report = _commitReportSource();

            // —— 1. 实际造成/承受的**最终**伤害：唯一来源是阶段 11 的提交结果 ——
            // 它不是"接触明细之和"：同一目标的多条接触已在阶段 5 聚合过一次，
            // 因此这里每单位只出现一次，不存在逐接触舍入。
            // 累加用 long，只在构造冻结形状 AdrenalineAccrualFacts 时收窄一次（越界稳定拒绝，
            // 绝不打回或回绕）。
            if (report != null && report.Units != null)
            {
                for (int i = 0; i < report.Units.Count; i++)
                {
                    UnitDamageCommit commit = report.Units[i];
                    if (commit == null) continue;
                    Require(accumulators, commit.UnitId).ReceivedDamageQ10 += commit.DamageQ10;
                }
            }

            // —— 2. 实际**造成**的最终伤害：按攻击者的合法 Intent 归属 ——
            // 与承受方**同源**：都取自该目标在本 Tick 的**唯一一次**聚合结果
            // （因此 AOE 攻击对 N 个目标造成伤害时，攻击者一项就是 N 个目标最终伤害之和，
            // 不存在"逐接触给攻击者各记一次"的路径）。
            if (resolution != null && !resolution.Failed)
            {
                AccumulateDealtDamage(accumulators, resolution);
                AccumulateClashSuccessKeys(accumulators, resolution);
                AccumulateReactionSuccessKeys(accumulators, resolution);
            }

            var facts = new List<AdrenalineAccrualFacts>(accumulators.Count);
            for (int i = 0; i < accumulators.Count; i++)
            {
                Accumulator entry = accumulators[i];
                facts.Add(new AdrenalineAccrualFacts(
                    entry.UnitId,
                    Narrow(entry.DealtDamageQ10, entry.UnitId),
                    Narrow(entry.ReceivedDamageQ10, entry.UnitId),
                    entry.SuccessfulBlockCount,
                    entry.SuccessfulDodgeCount,
                    entry.ClashSuccessCount));
            }

            // 规范化顺序：UnitId **严格升序**。累加器本身按首次出现顺序插入
            // （提交报告已按 UnitId 升序，因此实际上已经是升序），但排序是**契约**
            // 而不是"上游恰好有序"的推论。
            facts.Sort((a, b) => a.UnitId.Value.CompareTo(b.UnitId.Value));
            for (int i = 1; i < facts.Count; i++)
            {
                if (facts[i - 1].UnitId == facts[i].UnitId)
                    throw new LogicDefinitionException(ResolutionIntegrationCodes.STEP_ACCRUAL_GROUP_AMBIGUOUS,
                        "duplicate accrual unit=" + facts[i].UnitId.Value.ToString(CultureInfo.InvariantCulture));
            }

            LastFacts = facts.AsReadOnly();
            return LastFacts;
        }

        /// <summary>
        /// 「实际造成的最终伤害」：逐未消解命中的<strong>合法攻击 Intent</strong>把该目标的
        /// 最终聚合伤害记给攻击者一次。
        ///
        /// 只在 <c>RemainingHits</c> 上遍历（已完成求解的接触事实），因此
        /// "被 Dodge 回避 / 被 Block 完全抵抗 / 因参战 Clash 而失效"的攻击<strong>不会</strong>
        /// 出现在这里——它们的最终伤害本来就是 0，而不是"先加再减"。
        /// </summary>
        private static void AccumulateDealtDamage(List<Accumulator> accumulators,
            StagedConflictResolution resolution)
        {
            for (int i = 0; i < resolution.RemainingHits.Count; i++)
            {
                RemainingHitResolution hit = resolution.RemainingHits[i];
                if (hit == null || hit.Aggregate == null) continue;
                Require(accumulators, hit.AttackerUnitId).DealtDamageQ10 += hit.Aggregate.TotalDamageQ10;
            }
        }

        /// <summary>「每单位每 ConflictGroup 至多一个 Clash 成功键」。</summary>
        private static void AccumulateClashSuccessKeys(List<Accumulator> accumulators,
            StagedConflictResolution resolution)
        {
            for (int c = 0; c < resolution.Clashes.Count; c++)
            {
                ClashComponentResolution clash = resolution.Clashes[c];
                if (clash == null || clash.Clash == null) continue;
                IReadOnlyList<ClashParticipantResolution> participants = clash.Clash.Participants;
                if (participants == null) continue;

                for (int p = 0; p < participants.Count; p++)
                {
                    ClashParticipantResolution participant = participants[p];
                    if (participant == null) continue;
                    Accumulator entry = Require(accumulators, participant.OwnerUnitId);
                    if (entry.ClashGroupKeys.Add(clash.ConflictGroupKey)) entry.ClashSuccessCount++;
                }
            }
        }

        /// <summary>「每反应计划至多一次成功 Block/Dodge 键」+ 失败反应零奖励。</summary>
        private static void AccumulateReactionSuccessKeys(List<Accumulator> accumulators,
            StagedConflictResolution resolution)
        {
            for (int b = 0; b < resolution.BlockPlans.Count; b++)
            {
                BlockPlanResolution plan = resolution.BlockPlans[b];
                if (plan == null || !plan.AnyEligibleContact) continue;   // 无效格挡 ⇒ 零奖励
                Require(accumulators, plan.DefenderUnitId).SuccessfulBlockCount++;
            }

            for (int d = 0; d < resolution.DodgePlans.Count; d++)
            {
                DodgePlanResolution plan = resolution.DodgePlans[d];
                if (plan == null || !plan.DestinationCommitted || !plan.AvoidedAnyContact) continue;
                Require(accumulators, plan.DefenderUnitId).SuccessfulDodgeCount++;
            }
        }

        /// <summary>按需创建累加器（<paramref name="unitId"/> 必须有效）。</summary>
        private static Accumulator Require(List<Accumulator> accumulators, UnitId unitId)
        {
            for (int i = 0; i < accumulators.Count; i++)
            {
                if (accumulators[i].UnitId == unitId) return accumulators[i];
            }
            var created = new Accumulator(unitId);
            accumulators.Add(created);
            return created;
        }

        /// <summary>
        /// 已提交伤害总量收窄到任务 07 冻结的 <c>int</c> 形状。<strong>绝不</strong>钳制或回绕：
        /// 越界是装配/规则矛盾（单个单位的单 Tick 伤害不该超过 <c>int</c> 域），以稳定码拒绝。
        /// </summary>
        private static int Narrow(long value, UnitId unitId)
        {
            if (value < 0L || value > int.MaxValue)
                throw new LogicDefinitionException(ResolutionIntegrationCodes.STEP_ACCRUAL_GROUP_AMBIGUOUS,
                    "accrual damage out of range unit=" + unitId.Value.ToString(CultureInfo.InvariantCulture) +
                    " value=" + value.ToString(CultureInfo.InvariantCulture));
            return (int)value;
        }

        private sealed class Accumulator
        {
            public Accumulator(UnitId unitId) { UnitId = unitId; }

            public UnitId UnitId { get; }
            public long DealtDamageQ10 { get; set; }
            public long ReceivedDamageQ10 { get; set; }
            public int SuccessfulBlockCount { get; set; }
            public int SuccessfulDodgeCount { get; set; }
            public int ClashSuccessCount { get; set; }

            /// <summary>已计入 Clash 成功键的冲突组集合（"每单位每 ConflictGroup 至多一个"）。</summary>
            public HashSet<long> ClashGroupKeys { get; } = new HashSet<long>();
        }
    }

    /// <summary>
    /// 任务包「必须产出」18 的<strong>阶段 11 请求构建真实现</strong>：把本 Tick 的聚合结果
    /// （<see cref="DamageCommitReport.Units"/>）投影为<strong>每单位唯一</strong>的
    /// <see cref="ForcedDisplacementRequest"/>。
    ///
    /// <para><strong>为什么只读提交报告</strong>：阶段 11 的报告已经是"每单位一条、按
    /// <c>UnitId</c> 升序、含最终 <c>KnockbackSteps</c>/<c>ResultantDirection</c>/<c>TotalImpactUnits</c>
    /// 与 <c>ConflictGroupKey</c>"的规范面。因此本类型不做第二次量化、不读单位状态机、
    /// 不重算合力，也就没有"两条几何真值"的可能。</para>
    ///
    /// <para><strong>唯一性</strong>：<c>DamageCommitReport.Units</c> 已按 <c>UnitId</c> 唯一，
    /// 再加一道显式判重 ⇒ 重复单位键以 <c>STEP_DISPLACEMENT_REQUEST_DUPLICATE</c> 稳定拒绝
    /// （绝不按 <c>ConflictGroupKey</c>/<c>MomentumUnits</c>/枚举顺序挑一个）。</para>
    ///
    /// <para><strong>零步请求</strong>：<c>KnockbackSteps == 0</c>（含反向合力互相抵消）或
    /// 无合力方向时<strong>不产生请求</strong>——"零步结果事件"由阶段 12 的求解器按请求产生，
    /// 不在这里伪造。</para>
    /// </summary>
    public sealed class DisplacementRequestBuilder : IForcedDisplacementRequestBuilder
    {
        private readonly Func<DamageCommitReport> _commitReportSource;

        public DisplacementRequestBuilder(Func<DamageCommitReport> commitReportSource)
        {
            _commitReportSource = commitReportSource ?? throw new ArgumentNullException(nameof(commitReportSource));
        }

        /// <summary>最近一次构建出的请求（只读观察面；未构建时为空）。</summary>
        public IReadOnlyList<ForcedDisplacementRequest> LastRequests { get; private set; }
            = Array.Empty<ForcedDisplacementRequest>();

        public IReadOnlyList<ForcedDisplacementRequest> BuildOrdered(long tick, IReadOnlyList<UnitSnapshot> units)
        {
            DamageCommitReport report = _commitReportSource();
            if (report == null || report.Units == null || report.Units.Count == 0)
            {
                LastRequests = Array.Empty<ForcedDisplacementRequest>();
                return LastRequests;
            }

            var requests = new List<ForcedDisplacementRequest>(report.Units.Count);
            var seen = new List<long>(report.Units.Count);
            for (int i = 0; i < report.Units.Count; i++)
            {
                UnitDamageCommit commit = report.Units[i];
                if (commit == null || !commit.UnitId.IsValid) continue;
                if (commit.KnockbackSteps <= 0 || commit.ResultantDirection == null) continue;

                if (Contains(seen, commit.UnitId.Value))
                    throw new LogicDefinitionException(SimulationCodes.STEP_DISPLACEMENT_REQUEST_DUPLICATE,
                        commit.UnitId.Value.ToString(CultureInfo.InvariantCulture));
                seen.Add(commit.UnitId.Value);

                requests.Add(new ForcedDisplacementRequest(
                    commit.UnitId,
                    commit.ResultantDirection.Value,
                    commit.KnockbackSteps,
                    commit.TotalImpactUnits,
                    commit.ConflictGroupKey));
            }

            requests.Sort((a, b) => a.TargetUnitId.Value.CompareTo(b.TargetUnitId.Value));
            LastRequests = requests.AsReadOnly();
            return LastRequests;
        }

        private static bool Contains(List<long> values, long value)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == value) return true;
            }
            return false;
        }
    }
}
