using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
namespace ProjectHero.Logic.Simulation
{
    /// <summary>
    /// 提交阶段唯一允许的<strong>扣血写入端口</strong>（任务包 08「必须产出」13/14）。
    ///
    /// 它与 <c>UnitSnapshot</c> 一样是<strong>最小值面</strong>：提交系统只看得到
    /// "给某单位扣多少 Q10 生命"，看不到单位对象、状态机或网格，
    /// 因此"提交阶段自己改状态/位置/计划"在类型层就不可能。
    /// </summary>
    public interface IResolutionDamageApplier
    {
        /// <summary>把已聚合的 Q10 伤害写入该单位的权威生命值（越界以稳定码抛出，不钳制）。</summary>
        void ApplyDamageQ10(UnitId unitId, long damageQ10);
    }

    /// <summary>一个单位在本 Tick 的伤害提交结果（审计、事件载荷与位移请求共用）。</summary>
    public sealed record UnitDamageCommit(
        UnitId UnitId,
        long DamageQ10,
        long TotalImpactUnits,
        int KnockbackSteps,
        GridDirection? ResultantDirection,
        ActionPlanId RequestedDisplacementPlanId,
        long ConflictGroupKey = 0L);

    /// <summary>
    /// 一个攻击计划因参与 Clash 而必须终止的请求（<strong>只</strong>是请求）。
    ///
    /// 提交期<strong>不</strong>改写 <c>ActionPlan</c>/Lane/Intent/MovementSegment/Reservation：
    /// 它只把原因交给统一终态协调器（阶段 14），由协调器决定"第一次成功请求胜出"。
    /// </summary>
    public sealed record ClashTerminalRequest(ActionPlanId ActionPlanId, ActionTerminationReason Reason);

    /// <summary>本 Tick 的伤害/合力提交结果（不可变、只读观察面）。</summary>
    public sealed record DamageCommitReport(
        long Tick,
        string Status,
        IReadOnlyList<UnitDamageCommit> Units,
        IReadOnlyList<ClashTerminalRequest> Terminals)
    {
        public static DamageCommitReport Empty(long tick)
            => new DamageCommitReport(tick, "EMPTY", Array.Empty<UnitDamageCommit>(),
                Array.Empty<ClashTerminalRequest>());
    }

    /// <summary>
    /// 任务 08 的<strong>分阶段 Resolution 提交系统</strong>（阶段 11 的
    /// <c>IResolutionCommitSystem.CommitDamageAndAggregationOrdered</c> 真实现）。
    ///
    /// <list type="bullet">
    /// <item><strong>只做两件事</strong>：把已聚合的伤害经 <see cref="IResolutionDamageApplier"/> 写入单位生命；
    /// 把"哪些攻击因参与 Clash 必须终止"登记为<strong>请求</strong>（阶段 14 才经统一终态协调器提交）。</item>
    /// <item><strong>不</strong>改 <c>ActionPlan</c>/Lane/Intent/MovementSegment/Reservation
    /// （任务包 08 禁止事项 <c>:417</c>）：违反的可观察后果是 Step 末
    /// <c>STEP_INVARIANT_VIOLATION: active-plan-missing-from-lane</c>。</item>
    /// <item>聚合结果本身来自<strong>已经求解完成</strong>的阶段 10（<see cref="StagedConflictResolution"/>）：
    /// 本类不做任何解析、不做第二份量化、不读取单位状态机或 <c>CanReceiveDirectHit</c>。</item>
    /// </list>
    /// </summary>
    public sealed class ConflictGroupResolutionCommitSystem : IResolutionCommitSystem
    {
        private readonly Func<StagedConflictResolution> _resolutionSource;
        private readonly IResolutionDamageApplier _damageApplier;

        public ConflictGroupResolutionCommitSystem(
            Func<StagedConflictResolution> resolutionSource,
            IResolutionDamageApplier damageApplier)
        {
            _resolutionSource = resolutionSource ?? throw new ArgumentNullException(nameof(resolutionSource));
            _damageApplier = damageApplier ?? throw new ArgumentNullException(nameof(damageApplier));
        }

        /// <summary>最近一次阶段 11 提交的只读结果。</summary>
        public DamageCommitReport LastDamageCommitReport { get; private set; } = DamageCommitReport.Empty(0L);

        /// <summary>阶段 14 需要交给统一终态协调器的 Clash 终止请求（按 <c>ActionPlanId</c> 升序）。</summary>
        public IReadOnlyList<ClashTerminalRequest> PendingClashTerminals
            => LastDamageCommitReport.Terminals;

        public void CommitDamageAndAggregationOrdered(long tick, IReadOnlyList<UnitSnapshot> units)
        {
            StagedConflictResolution resolution = _resolutionSource();
            if (resolution == null || resolution.Failed || resolution.Tick != tick)
            {
                LastDamageCommitReport = new DamageCommitReport(tick,
                    resolution == null ? "NO_RESOLUTION"
                        : resolution.Failed ? resolution.GraphErrorCode
                        : "TICK_MISMATCH",
                    Array.Empty<UnitDamageCommit>(), Array.Empty<ClashTerminalRequest>());
                return;
            }

            IReadOnlyList<TargetAggregateResolution> aggregations = resolution.Aggregations;
            var commits = new List<UnitDamageCommit>(aggregations.Count);
            for (int i = 0; i < aggregations.Count; i++)
            {
                TargetAggregateResolution aggregate = aggregations[i];
                if (aggregate == null) continue;

                // 稳定顺序：按 UnitId 升序逐单位一次写入（不存在"逐接触扣血"的路径）。
                _damageApplier.ApplyDamageQ10(aggregate.TargetUnitId, aggregate.TotalDamageQ10);

                commits.Add(new UnitDamageCommit(
                    aggregate.TargetUnitId,
                    aggregate.TotalDamageQ10,
                    aggregate.TotalImpactUnits,
                    aggregate.KnockbackSteps,
                    aggregate.ResultantDirection,
                    FindDisplacementPlanId(resolution, aggregate),
                    ResolveConflictGroupKey(resolution, aggregate)));
            }

            var terminals = new List<ClashTerminalRequest>();
            IReadOnlyList<ActionPlanId> clashTerminated = resolution.ClashTerminatedPlanIds;
            for (int i = 0; i < clashTerminated.Count; i++)
            {
                terminals.Add(new ClashTerminalRequest(clashTerminated[i], ActionTerminationReason.InterruptedByClash));
            }

            LastDamageCommitReport = new DamageCommitReport(tick, "COMMITTED", commits.AsReadOnly(),
                terminals.AsReadOnly());
        }

        public void CommitStateControlAndRemainingTerminalsOrdered(
            long tick, IReadOnlyList<UnitSnapshot> unitsAfterRelocation)
        {
            // 状态/控制与其余终态在阶段 14 由 <c>BattleSimulation</c> 用**统一终态协调器**提交
            // （它持有 Lane/Reservation/Segment 的唯一清理参与者）。本类不再重复实现一条清理路径：
            // 那只会在两处产生"谁的清理先跑"的顺序歧义。
        }

        /// <summary>某单位本 Tick 的强制位移请求来源计划（供审计；无对应 Resolutions 时返回 0）。</summary>
        private static ActionPlanId FindDisplacementPlanId(
            StagedConflictResolution resolution, TargetAggregateResolution aggregate)
        {
            IReadOnlyList<RemainingHitResolution> hits = resolution.RemainingHits;
            ActionPlanId found = default(ActionPlanId);
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i].Aggregate == null || hits[i].Aggregate.TargetUnitId != aggregate.TargetUnitId) continue;
                // 接触键的 First/Second 已按 (UnitId, ActionPlanId) 规范化，因此攻击侧计划
                // 可能是任意一侧；这里取两侧里较大的那个（= 更晚创建的攻击计划，稳定且与遍历顺序无关）。
                long candidate = hits[i].Key.FirstPlanId.Value > hits[i].Key.SecondPlanId.Value
                    ? hits[i].Key.FirstPlanId.Value
                    : hits[i].Key.SecondPlanId.Value;
                if (candidate > found.Value) found = new ActionPlanId(candidate);
            }
            return found;
        }

        /// <summary>
        /// 该目标单位本 Tick 归属的冲突组键（<c>0</c> = 没有对应冲突组）。
        ///
        /// 判据来自<strong>求解产物自己</strong>（该目标全部未消解命中的攻击侧计划），
        /// 因此不需要图与求解结果之间的第二份关联规则。同一目标的两条命中若来自不同组，
        /// 说明二者在图里本应被"共享目标"连边（冲突图契约），此时<strong>不</strong>静默挑一个。
        /// </summary>
        private static long ResolveConflictGroupKey(
            StagedConflictResolution resolution, TargetAggregateResolution aggregate)
        {
            IReadOnlyDictionary<long, long> byPlan = ConflictGroupKeys.BuildByPlan(resolution.Graph);
            IReadOnlyList<RemainingHitResolution> hits = resolution.RemainingHits;
            long found = 0L;
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i].Aggregate == null || hits[i].Aggregate.TargetUnitId != aggregate.TargetUnitId) continue;
                long key = ConflictGroupKeys.Of(byPlan, hits[i].AttackPlanId);
                if (key == 0L) continue;
                if (found != 0L && found != key)
                    throw new LogicDefinitionException(ConflictGroupKeys.CONFLICT_GRAPH_INCONSISTENT,
                        "target=" + aggregate.TargetUnitId.Value.ToString(CultureInfo.InvariantCulture) +
                        " groups=" + found.ToString(CultureInfo.InvariantCulture) + "," +
                        key.ToString(CultureInfo.InvariantCulture));
                found = key;
            }
            return found;
        }
    }
}
