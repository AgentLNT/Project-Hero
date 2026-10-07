using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Grid;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 强制位移协议版本（主方案 0.4.6 版本标识）。
    /// SimultaneousStepV1 = 1：全 Tick 单批、逐格临时同时求解、每步一次规范方向增量且
    /// 不读取 PathWeight、部分距离保留、同落点全败、依赖链只通向空格、不连锁推人、
    /// 强制位移优先 Reservation、批量换位先于死亡。协议版本进入 BattleDefinitionHash；
    /// 求解器与运行时请求/结果由任务 08 实现。
    /// 注：Unity 6000.6.2f1 默认 C# 9，record struct 为 C# 10 特性，故手写只读值类型。
    /// </summary>
    public readonly struct ForcedDisplacementProtocolVersion
    {
        public const int SimultaneousStepV1 = 1;

        public readonly int Value;

        public ForcedDisplacementProtocolVersion(int value) { Value = value; }

        public bool IsValid => Value > 0;

        public bool Equals(ForcedDisplacementProtocolVersion other) => Value == other.Value;

        public override bool Equals(object obj) => obj is ForcedDisplacementProtocolVersion other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static bool operator ==(ForcedDisplacementProtocolVersion left, ForcedDisplacementProtocolVersion right) => left.Equals(right);

        public static bool operator !=(ForcedDisplacementProtocolVersion left, ForcedDisplacementProtocolVersion right) => !left.Equals(right);

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("forced_displacement.protocol_version", Value);
        }
    }

    /// <summary>
    /// 强制位移停止原因稳定数值编码（主方案 0.4.6 冻结；数值不得更改）。
    /// </summary>
    public enum ForcedDisplacementStopReason
    {
        Completed = 0,
        Boundary = 1,
        StaticObstacle = 2,
        OccupiedUnit = 3,
        DestinationContention = 4,
        DependencyCycle = 5,
        VolumeOverlap = 6
    }

    /// <summary>
    /// 停止原因编码的规范哈希分量（名称 + 数值，声明顺序固定）。
    /// 与协议版本一起参与 BattleDefinitionHash。
    /// </summary>
    public static class ForcedDisplacementEncoding
    {
        public static void WriteHashComponents(CanonicalHashWriter writer)
        {
            var values = (ForcedDisplacementStopReason[])Enum.GetValues(typeof(ForcedDisplacementStopReason));
            foreach (var reason in values)
            {
                writer.Write("forced_displacement.stop_reason." + reason, (int)reason);
            }
        }
    }

    /// <summary>
    /// 强制位移的<strong>版本化规则面</strong>（任务包「必须产出」22）。
    ///
    /// 任务包要求"方向表、footprint、停止原因、依赖图判定、Reservation 抢占、终态原因优先级和
    /// 阶段顺序全部进入 <c>RulesVersion</c> / <c>BattleDefinitionHash</c>"。
    /// 停止原因已由 <see cref="ForcedDisplacementEncoding"/> 承担；本类型承担其余六项，
    /// 由 <see cref="BattleRules.WriteHashComponents"/> 唯一写入
    /// （<c>BattleDefinitionHash.Compute</c> 的签名<strong>不</strong>改，避免破 02B 冻结面）。
    ///
    /// 这些字段是"规则形状"的版本标记：任何一条冻结规则的语义变化都必须改版本值，
    /// 否则 golden 会在规则已变的前提下继续通过。
    /// </summary>
    public sealed record ForcedDisplacementRules(
        int FootprintRuleVersion,
        int DependencyGraphRuleVersion,
        int PreemptionRuleVersion,
        int PhaseOrderVersion,
        IReadOnlyList<int> TerminationReasonPriority)
    {
        /// <summary>每轮只平移<strong>当前朝向</strong>的 footprint，不旋转、不改 Facing（:157）。</summary>
        public const int TranslateCurrentFootprintV1 = 1;

        /// <summary>一步一轮的依赖图判定：无赢家 + 只有通向空格的 DAG 成功（:172-194）。</summary>
        public const int StepDependencyGraphV1 = 1;

        /// <summary>求解忽略 Reservation；只在最终 footprint 确定后按 :205-209 抢占。</summary>
        public const int FinalFootprintPreemptionV1 = 1;

        /// <summary>伤害 → 请求 → 求解 → 终态/释放 → 批量换位 → 事件 → 状态/控制 → 死亡（:220-230）。</summary>
        public const int CommitPhaseOrderV1 = 1;

        public const string FORCED_DISPLACEMENT_RULES_INVALID = "FORCED_DISPLACEMENT_RULES_INVALID";

        /// <summary>
        /// 首版实例：四个规则版本均为 V1；终态原因优先级按
        /// <see cref="ActionTerminationReasons.PriorityOf"/> 冻结为
        /// <c>MovementOriginInvalidated</c> → <c>ReservationPreemptedByForcedDisplacement</c>。
        /// </summary>
        public static readonly ForcedDisplacementRules FrozenV1 = new ForcedDisplacementRules(
            TranslateCurrentFootprintV1,
            StepDependencyGraphV1,
            FinalFootprintPreemptionV1,
            CommitPhaseOrderV1,
            new[]
            {
                (int)ActionTerminationReason.MovementOriginInvalidated,
                (int)ActionTerminationReason.ReservationPreemptedByForcedDisplacement
            });

        /// <summary>返回首个校验错误码（null = 通过）。</summary>
        public string Validate()
        {
            if (FootprintRuleVersion <= 0) return FORCED_DISPLACEMENT_RULES_INVALID;
            if (DependencyGraphRuleVersion <= 0) return FORCED_DISPLACEMENT_RULES_INVALID;
            if (PreemptionRuleVersion <= 0) return FORCED_DISPLACEMENT_RULES_INVALID;
            if (PhaseOrderVersion <= 0) return FORCED_DISPLACEMENT_RULES_INVALID;
            if (TerminationReasonPriority == null || TerminationReasonPriority.Count != 2)
                return FORCED_DISPLACEMENT_RULES_INVALID;
            // 冻结次序：序号 0 的优先级必须严格高于序号 1（数字越小越优先）。
            if (ActionTerminationReasons.PriorityOf(
                    (ActionTerminationReason)TerminationReasonPriority[0]) >=
                ActionTerminationReasons.PriorityOf(
                    (ActionTerminationReason)TerminationReasonPriority[1]))
                return FORCED_DISPLACEMENT_RULES_INVALID;
            return null;
        }

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("forced_displacement.footprint_rule_version", FootprintRuleVersion);
            writer.Write("forced_displacement.dependency_graph_rule_version", DependencyGraphRuleVersion);
            writer.Write("forced_displacement.preemption_rule_version", PreemptionRuleVersion);
            writer.Write("forced_displacement.phase_order_version", PhaseOrderVersion);

            // 方向表：12 个方向的冻结数值 + 规范偏移（方向表是版本化玩法规则，必须进哈希）。
            writer.Write("forced_displacement.direction_table_version", GridNeighborTable.OrderVersion);
            for (int i = 0; i < GridNeighborTable.DirectionCount; i++)
            {
                var direction = (GridDirection)i;
                writer.Write(
                    "forced_displacement.direction." + direction,
                    i + ":" + GridNeighborTable.OffsetX(direction) + "," + GridNeighborTable.OffsetY(direction));
            }

            // 终态原因优先级：位次 + 枚举值 + 稳定字符串码。
            for (int i = 0; i < TerminationReasonPriority.Count; i++)
            {
                var reason = (ActionTerminationReason)TerminationReasonPriority[i];
                writer.Write(
                    "forced_displacement.termination_reason_priority." + i,
                    (int)reason + ":" + ActionTerminationReasons.CodeOf(reason));
            }
        }
    }

    /// <summary>
    /// 强制位移求解的<strong>性能统计</strong>（任务包「必须产出」22 末句）。
    ///
    /// 它<strong>绝不</strong>进入哈希、快照或任何逻辑输入（与 <c>StepPhaseTimingRecorder</c> 同理）：
    /// 性能采样一旦进哈希，采样开关本身就会改变 golden。
    /// 计数器只增不改语义，因此重复求解同一输入得到同一计数。
    /// </summary>
    public sealed class ForcedDisplacementStats
    {
        /// <summary>本批次请求数（去重后）。</summary>
        public int RequestCount { get; internal set; }

        /// <summary>本批次最大请求步数（<c>max(RequestedSteps)</c>）。</summary>
        public int MaxRequestedSteps { get; internal set; }

        /// <summary>实际执行的"一步一轮"轮数。</summary>
        public int RoundCount { get; internal set; }

        /// <summary>本批次累计依赖图节点数（每轮每提案一个节点）。</summary>
        public long DependencyNodeCount { get; internal set; }

        /// <summary>本批次累计依赖图边数。</summary>
        public long DependencyEdgeCount { get; internal set; }

        /// <summary>本批次累计 footprint 点检查次数（边界 / 障碍 / 占位判定各计一次）。</summary>
        public long FootprintPointCheckCount { get; internal set; }

        /// <summary>本批次结果条数（含零步）。</summary>
        public int ResolutionCount { get; internal set; }

        /// <summary>本批次真实换位条数（<c>From != To</c>）。</summary>
        public int RelocationCount { get; internal set; }

        /// <summary>累计统计（跨批次），用于长局诊断。</summary>
        public long TotalRequestCount { get; internal set; }

        /// <summary>累计真实换位数。</summary>
        public long TotalRelocationCount { get; internal set; }

        internal void ResetBatch()
        {
            RequestCount = 0;
            MaxRequestedSteps = 0;
            RoundCount = 0;
            DependencyNodeCount = 0;
            DependencyEdgeCount = 0;
            FootprintPointCheckCount = 0;
            ResolutionCount = 0;
            RelocationCount = 0;
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public override string ToString()
            => "forced-displacement requests=" + RequestCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " max-steps=" + MaxRequestedSteps.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " rounds=" + RoundCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " dep-nodes=" + DependencyNodeCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " dep-edges=" + DependencyEdgeCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " footprint-checks=" + FootprintPointCheckCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " relocations=" + RelocationCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
