using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Interactions
{
    /// <summary>
    /// 分阶段求解的<strong>冻结阶段序</strong>（任务包 08「必须产出」8 / 验收 :386）。
    ///
    /// 数值同时是执行顺序：求解器<strong>只</strong>按这个升序推进，任何新阶段只能追加在末尾。
    /// Dodge 的**原子位置提交**不在这里——它在阶段 9（构图之前）就已经发生，
    /// 本枚举的第一个阶段做的是"用提交前/后两份空间快照复核四格表"，
    /// 因此"除主方案明确规定的 Dodge 前置空间提交外，查询/求解阶段不得修改世界"
    /// 是<strong>结构事实</strong>，而不是各实现点的纪律。
    /// </summary>
    public enum ConflictResolutionPhase
    {
        /// <summary>阶段 1：Dodge 原子位置提交与空间复核（只读复核，位置提交在阶段 9）。</summary>
        DodgeSpatialRecheck = 0,

        /// <summary>阶段 2：Block 合格载荷完全抵抗。</summary>
        BlockFullResistance = 1,

        /// <summary>阶段 3：全部 Clash <strong>同时</strong>求解（禁止逐边修改后继续计算）。</summary>
        ClashSimultaneousSolve = 2,

        /// <summary>阶段 4：Move 拦截/逃脱判定。</summary>
        MoveIntercept = 3,

        /// <summary>阶段 5：Remaining Hits 中应用 Guard/被动抵抗并聚合。</summary>
        RemainingHitsGuardAndAggregate = 4
    }

    /// <summary>Clash 求解失败时的稳定错误码（不得与构图/聚合码复用）。</summary>
    public static class StagedResolutionCodes
    {
        /// <summary>构图失败（整组失败）时，求解阶段以本码整体拒绝。</summary>
        public const string STAGED_RESOLUTION_GRAPH_INVALID = "STAGED_RESOLUTION_GRAPH_INVALID";

        /// <summary>输入自相矛盾（缺计划事实、缺单位快照、单位集不一致等）。</summary>
        public const string STAGED_RESOLUTION_INPUT_INVALID = "STAGED_RESOLUTION_INPUT_INVALID";

        /// <summary>同一攻击计划对同一目标单位产生了多条未消解接触（会重复结算）。</summary>
        public const string STAGED_RESOLUTION_DUPLICATE_DIRECT_HIT = "STAGED_RESOLUTION_DUPLICATE_DIRECT_HIT";

        /// <summary>聚合结果超出逻辑血量使用的范围（不得静默截断）。</summary>
        public const string STAGED_RESOLUTION_COMMIT_RANGE = "STAGED_RESOLUTION_COMMIT_RANGE";
    }

    /// <summary>Dodge 复核的三种判定（主方案 0.4.1 旧/新接触四格表 + 标签）。</summary>
    public enum DodgeContactOutcome
    {
        /// <summary>旧有 <c>Dodgeable</c> 接触在新格失效 ⇒ 该攻击被回避（唯一奖励成功的形态）。</summary>
        Dodged = 0,

        /// <summary>接触在新格仍成立 ⇒ 照常命中（换位没有救下它）。</summary>
        StillHit = 1,

        /// <summary><c>Undodgeable</c>：保留旧接触（不因换位而失效，也不追踪两格都不命中的攻击）。</summary>
        RetainedUndodgeable = 2,

        /// <summary>两格都不命中，或该攻击没有可动的 Dodge 计划 ⇒ 普通命中接触，四格表不参与。</summary>
        NotApplicable = 3
    }

    /// <summary>一条攻击对防守者的 Dodge 复核结果（按 <see cref="ContactKey"/> 规范排序）。</summary>
    public sealed record DodgeContactResolution(
        ContactKey Key,
        ActionPlanId AttackPlanId,
        UnitId AttackerUnitId,
        ActionPlanId DodgePlanId,
        UnitId DefenderUnitId,
        bool CoveredBefore,
        bool CoveredAfter,
        bool DestinationCommitted,
        string FailureCode,
        DodgeContactOutcome Outcome)
    {
        /// <summary>换位是否让这条接触失效（旧的 <c>Dodgeable</c> 接触在新格不再成立）。</summary>
        public bool Invalidated => Outcome == DodgeContactOutcome.Dodged;
    }

    /// <summary>一个 Dodge 计划的复核结果（每个冻结的 <see cref="DodgeCommitResult"/> 恰好一条）。</summary>
    public sealed record DodgePlanResolution(
        ActionPlanId DodgePlanId,
        UnitId DefenderUnitId,
        GridPoint From,
        GridPoint Destination,
        bool DestinationCommitted,
        string FailureCode,
        IReadOnlyList<DodgeContactResolution> Contacts)
    {
        /// <summary>至少一条旧有 <c>Dodgeable</c> 接触因成功换位失效 ⇒ 唯一允许发成功奖励的形态。</summary>
        public bool AvoidedAnyContact
        {
            get
            {
                for (int i = 0; i < Contacts.Count; i++)
                {
                    if (Contacts[i].Invalidated) return true;
                }
                return false;
            }
        }
    }

    /// <summary>Block 的合格性与结果（任务包 08:73）。</summary>
    public enum BlockContactOutcome
    {
        /// <summary>实际降低过合格载荷且最终载荷全为 0。</summary>
        Blocked = 0,

        /// <summary>实际降低过合格载荷但仍有绕过载荷。</summary>
        PartiallyBlocked = 1,

        /// <summary>完全没有可降低载荷 ⇒ 无效格挡（不产生成功 Block 奖励）。</summary>
        BlockIneffective = 2
    }

    /// <summary>一条接触的 Block 结果。</summary>
    public sealed record BlockContactResolution(
        ContactKey Key,
        ActionPlanId AttackPlanId,
        UnitId AttackerUnitId,
        ActionPlanId BlockPlanId,
        UnitId DefenderUnitId,
        BlockContactOutcome Outcome,
        long RawDamageQ10,
        long AfterBlockDamageQ10,
        int IncomingMomentumUnits,
        int AfterBlockMomentumUnits)
    {
        /// <summary>该接触是否让 Block 计划获得"成功格挡"资格。</summary>
        public bool RewardsBlock => Outcome == BlockContactOutcome.Blocked || Outcome == BlockContactOutcome.PartiallyBlocked;
    }

    /// <summary>一个 Block 计划的结果：TriggerTick 内全部合格入射接触同时处理。</summary>
    public sealed record BlockPlanResolution(
        ActionPlanId BlockPlanId,
        UnitId DefenderUnitId,
        IReadOnlyList<BlockContactResolution> Contacts)
    {
        public bool AnyEligibleContact
        {
            get
            {
                for (int i = 0; i < Contacts.Count; i++)
                {
                    if (Contacts[i].RewardsBlock) return true;
                }
                return false;
            }
        }
    }

    /// <summary>一个 Clash 连通块（同一冲突组内、彼此由<strong>有效</strong> Clash 边连通的攻击集合）。</summary>
    public sealed record ClashComponentResolution(
        long ConflictGroupKey,
        MomentumClashResolution Clash)
    {
        /// <summary>本连通块的全部参与计划（= <c>TerminatedActionPlanIds</c>，已按 <c>ActionPlanId</c> 升序）。</summary>
        public IReadOnlyList<ActionPlanId> TerminatedActionPlanIds => Clash.TerminatedActionPlanIds;
    }

    /// <summary>Move 接触的判定（旧格/新格覆盖标记区分逃脱/拦截）。</summary>
    public enum MoveContactOutcome
    {
        /// <summary>换位/移动后攻击区域仍覆盖该单位 ⇒ 拦截。</summary>
        Intercepted = 0,

        /// <summary>旧格覆盖、新格不覆盖，且没有 Dodge 复核把它判为"被回避" ⇒ 移动逃脱。</summary>
        Escaped = 1,

        /// <summary>两格都不覆盖（本不该出现在候选里）⇒ 求解阶段稳定拒绝。</summary>
        NoCoverage = 2
    }

    /// <summary>一条 Move 接触的结果。</summary>
    public sealed record MoveContactResolution(
        ContactKey Key,
        ActionPlanId AttackPlanId,
        UnitId AttackerUnitId,
        ActionPlanId MovePlanId,
        UnitId MovingUnitId,
        bool CoveredBefore,
        bool CoveredAfter,
        MoveContactOutcome Outcome);

    /// <summary>
    /// 一个目标单位的未消解直接命中（Remaining Hit）：<strong>只</strong>在这一阶段读取
    /// <c>CanReceiveDirectHit</c>，并在此处解析 Guard/被动抵抗。
    /// </summary>
    public sealed record RemainingHitResolution(
        ContactKey Key,
        ActionPlanId AttackPlanId,
        UnitId AttackerUnitId,
        string AttackSpecId,
        GridDirection IncomingDirection,
        int IncomingMomentumUnits,
        IReadOnlyList<DamageComponentSpec> DamageComponents,
        AttackTagMask AttackTags,
        ActionPlanId GuardPlanId,
        string GuardSpecId,
        bool CanReceiveDirectHit,
        bool IsDirectHitSuppressed,
        TargetAggregateResolution Aggregate);

    /// <summary>
    /// 分阶段求解的不可变结果（任务包 08「必须产出」8–11）。
    ///
    /// 五个阶段列表<strong>严格按 <see cref="ConflictResolutionPhase"/> 的顺序</strong>各自规范排序；
    /// 求解器只读输入、<strong>不</strong>修改任何世界状态（不扣血、不切状态、不移动、不终止计划、不发事件）。
    /// </summary>
    public sealed record StagedConflictResolution(
        long Tick,
        string GraphErrorCode,
        long GraphFailingGroupKey,
        IReadOnlyList<DodgeContactResolution> DodgeContacts,
        IReadOnlyList<DodgePlanResolution> DodgePlans,
        IReadOnlyList<BlockPlanResolution> BlockPlans,
        IReadOnlyList<ClashComponentResolution> Clashes,
        IReadOnlyList<MoveContactResolution> Moves,
        IReadOnlyList<RemainingHitResolution> RemainingHits,
        ConflictGraph Graph = null)
    {
        public static StagedConflictResolution Empty(long tick)
            => new StagedConflictResolution(tick, null, 0L,
                Array.Empty<DodgeContactResolution>(), Array.Empty<DodgePlanResolution>(),
                Array.Empty<BlockPlanResolution>(), Array.Empty<ClashComponentResolution>(),
                Array.Empty<MoveContactResolution>(), Array.Empty<RemainingHitResolution>());

        /// <summary>构图失败 ⇒ 整组失败（不丢边、不拆组），本 Tick 不产生任何 Resolution。</summary>
        public bool Failed => GraphErrorCode != null;

        /// <summary>参与 Clash 并因此终止的全部攻击计划（按 <c>ActionPlanId</c> 升序、已去重）。</summary>
        public IReadOnlyList<ActionPlanId> ClashTerminatedPlanIds
        {
            get
            {
                var ids = new List<ActionPlanId>();
                for (int i = 0; i < Clashes.Count; i++)
                {
                    IReadOnlyList<ActionPlanId> terminated = Clashes[i].TerminatedActionPlanIds;
                    for (int t = 0; t < terminated.Count; t++) ids.Add(terminated[t]);
                }
                ids.Sort((a, b) => a.Value.CompareTo(b.Value));
                return ids.AsReadOnly();
            }
        }

        /// <summary>本 Tick 需要提交的伤害（按 <c>UnitId</c> 升序，<strong>已经</strong>按通道聚合）。</summary>
        public IReadOnlyList<TargetAggregateResolution> Aggregations
        {
            get
            {
                var result = new List<TargetAggregateResolution>(RemainingHits.Count);
                var seen = new List<long>();
                for (int i = 0; i < RemainingHits.Count; i++)
                {
                    TargetAggregateResolution aggregate = RemainingHits[i].Aggregate;
                    if (aggregate == null) continue;
                    if (Contains(seen, aggregate.TargetUnitId.Value)) continue;
                    seen.Add(aggregate.TargetUnitId.Value);
                    result.Add(aggregate);
                }
                return result.AsReadOnly();
            }
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

    /// <summary>
    /// 分阶段求解的<strong>只读输入</strong>（冻结清单 §3.2 的求解侧扩展）。
    ///
    /// 全部字段都是本 Tick 一次性冻结的事实：构图产物、计划生命周期事实、Dodge 提交报告
    /// （含提交前/后两份空间快照）、只读单位快照与定义表、以及 Clash 配额。
    /// 求解器<strong>不再</strong>访问 <c>BattleSimulation</c>、<c>LogicGrid</c>、事件队列或计划对象。
    /// </summary>
    public sealed class StagedResolutionContext
    {
        public StagedResolutionContext(
            ConflictGraph graph,
            string graphErrorCode,
            long graphFailingGroupKey,
            IReadOnlyList<InteractionPlanFacts> plans,
            IReadOnlyList<UnitOccupancy> unitsBefore,
            IReadOnlyList<UnitOccupancy> unitsAfter,
            DodgeCommitReport dodgeReport,
            IReadOnlyList<UnitSnapshot> units,
            IReadOnlyList<UnitDefinition> unitDefinitions,
            IReadOnlyList<PlanSpecEntry> planSpecs,
            IReadOnlyList<BlockPayloadEntry> blockPayloads,
            IReadOnlyList<GuardPayloadEntry> guardPayloads,
            MomentumClashQuota clashQuota)
        {
            Graph = graph;
            GraphErrorCode = graphErrorCode;
            GraphFailingGroupKey = graphFailingGroupKey;
            Plans = plans ?? Array.Empty<InteractionPlanFacts>();
            UnitsBefore = unitsBefore ?? Array.Empty<UnitOccupancy>();
            UnitsAfter = unitsAfter ?? Array.Empty<UnitOccupancy>();
            DodgeReport = dodgeReport;
            Units = units ?? Array.Empty<UnitSnapshot>();
            UnitDefinitions = unitDefinitions ?? Array.Empty<UnitDefinition>();
            PlanSpecs = planSpecs ?? Array.Empty<PlanSpecEntry>();
            BlockPayloads = blockPayloads ?? Array.Empty<BlockPayloadEntry>();
            GuardPayloads = guardPayloads ?? Array.Empty<GuardPayloadEntry>();
            ClashQuota = clashQuota;
        }

        /// <summary>阶段 10 的构图产物（失败时为 null）。</summary>
        public ConflictGraph Graph { get; }

        /// <summary>构图失败码（非 null ⇒ 整组失败，本 Tick 无任何 Resolution）。</summary>
        public string GraphErrorCode { get; }

        /// <summary>失败组键（<see cref="GraphErrorCode"/> 非 null 时有效）。</summary>
        public long GraphFailingGroupKey { get; }

        /// <summary>计划生命周期事实（节点资格与对手计划判定的唯一来源）。</summary>
        public IReadOnlyList<InteractionPlanFacts> Plans { get; }

        /// <summary>Dodge 提交前的统一只读空间快照投影（旧格）。</summary>
        public IReadOnlyList<UnitOccupancy> UnitsBefore { get; }

        /// <summary>Dodge 提交后的统一只读空间快照投影（新格）。</summary>
        public IReadOnlyList<UnitOccupancy> UnitsAfter { get; }

        /// <summary>阶段 9 的 Dodge 提交报告（含每计划的 From/Destination/失败码）。</summary>
        public DodgeCommitReport DodgeReport { get; }

        /// <summary>只读单位快照（控制阻力与 <c>CanReceiveDirectHit</c> 的唯一来源）。</summary>
        public IReadOnlyList<UnitSnapshot> Units { get; }

        /// <summary>单位定义表（被动分通道抵抗的唯一来源）。</summary>
        public IReadOnlyList<UnitDefinition> UnitDefinitions { get; }

        /// <summary>
        /// 计划 → 动作定义 ID 的只读投影。<see cref="InteractionPlanFacts"/> 刻意<strong>不</strong>携带
        /// <c>ActionSpecId</c>（它是"生命周期事实"视图的一部分，候选收集不需要它），
        /// 而 Block/Guard 的抵抗配置必须按动作定义解析 ⇒ 装配侧显式提供本表，求解器不解析 <c>ActionSpec</c>。
        /// </summary>
        public IReadOnlyList<PlanSpecEntry> PlanSpecs { get; }

        /// <summary>Block 载荷表（装配侧从已验证定义投影；求解器不解析 <c>ActionSpec</c>）。</summary>
        public IReadOnlyList<BlockPayloadEntry> BlockPayloads { get; }

        /// <summary>Guard 载荷表（同上）。</summary>
        public IReadOnlyList<GuardPayloadEntry> GuardPayloads { get; }

        /// <summary>Clash 配额（数值与错误码由构图侧持有）。</summary>
        public MomentumClashQuota ClashQuota { get; }
    }
}
