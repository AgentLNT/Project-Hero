using System;
using System.Collections.Generic;

namespace ProjectHero.Logic.Simulation
{
    /// <summary>
    /// <c>BattleSimulation.Step()</c> 的 20 个冻结阶段（任务包「必须冻结的 Step 阶段」，顺序即枚举顺序）。
    ///
    /// 该枚举是<strong>契约</strong>而不是注释：
    /// <list type="bullet">
    /// <item>成员顺序 = 阶段顺序；<see cref="StepPhaseExtensions.FrozenOrder"/> 是唯一权威列表，
    /// 任何重排都必须同时改任务包与所有 golden；</item>
    /// <item>每个阶段在 <c>Step</c> 中都是一次显式的顺序调用（不是容器遍历、不是隐式链）；
    /// 未实现的系统是明确的空实现或小接口占位，阶段本身不会被合并掉；</item>
    /// <item>阶段顺序可从<strong>代码与测试</strong>直接识别：测试断言
    /// <see cref="BattleSimulation.LastStepPhaseTrace"/> 的相对顺序即可。</item>
    /// </list>
    /// </summary>
    public enum StepPhase
    {
        /// <summary>0. 应用全局动作的命令前边界（移动位置提交、Reservation 释放、自然完成）。</summary>
        PreCommandBoundaries = 0,

        /// <summary>1. 推进状态到期与持续效果（单位顺序固定为 UnitId）。</summary>
        StateAndEffectAdvance = 1,

        /// <summary>2. 处理死亡和胜负；战斗结束则进入唯一终态清理并输出最终结果。</summary>
        DeathAndVictory = 2,

        /// <summary>3. 打开到期的提交窗口（先执行拥有者个人周期肾上腺素清零）。</summary>
        WindowOpen = 3,

        /// <summary>4. 刷新此前已公开 ReactionOpportunity 的来源失效/已过期状态。</summary>
        ReactionOpportunityRefresh = 4,

        /// <summary>5. 合并 Step 开始前已冻结的当前 Tick 请求，并冻结 BatchBaseScheduleRevision。</summary>
        FrozenBatchMergeAndBaseRevision = 5,

        /// <summary>6. 按 scope 校验命令；原子应用排程编辑；创建固定 Locked 反应计划；关闭到期机会。</summary>
        CommandValidationAndScheduling = 6,

        /// <summary>7. 到期 Editable 普通计划的启动门禁/自动延期；到期固定 Locked 反应的启动与 Trigger。</summary>
        DuePlanStartGateAndReactionTrigger = 7,

        /// <summary>8. 取出本 Tick 全部到期 Intent。</summary>
        IntentDrain = 8,

        /// <summary>9. Dodge 提交前捕获旧格接触，再按稳定键原子提交到期 Dodge 目的格。</summary>
        LegacyContactCaptureAndDodgeCommit = 9,

        /// <summary>10. 合并冻结旧接触与新接触，构建规范化冲突图并计算 Resolution。</summary>
        ConflictGraphAndResolution = 10,

        /// <summary>11. 提交伤害与合力聚合，生成全 Tick 每单位唯一的强制位移请求。</summary>
        DamageAggregationAndDisplacementRequests = 11,

        /// <summary>12. 只读临时同时求解完整强制位移批次（不修改世界）。</summary>
        ForcedDisplacementSolve = 12,

        /// <summary>13. 清理被破坏的 Move/Reservation，再一次性 ApplyBatchRelocation 并发射位移事件。</summary>
        PlanReservationCleanupAndBatchCommit = 13,

        /// <summary>14. 提交状态、控制与其余动作终态；一次性应用肾上腺素获得事实与动作后边界。</summary>
        StateControlAndAdrenalineAccrual = 14,

        /// <summary>15. 再处理死亡（同 Tick 致死单位先完成强制位移再移除占位）。</summary>
        PostDisplacementDeath = 15,

        /// <summary>16. 再评估胜负；战斗结束则进入唯一终态清理。</summary>
        VictoryReevaluation = 16,

        /// <summary>17. 正式关闭已请求关闭的窗口并排定下一窗口。</summary>
        WindowCloseAndScheduleNext = 17,

        /// <summary>18. AI 读取与玩家相同的只读决策快照，只经注册入口投递下一 Tick。</summary>
        DecisionSnapshotDelivery = 18,

        /// <summary>19. 只读不变量检查 → 归档本 Tick 新冻结记录 → 增量摘要 → EventBatch 与规范化 Snapshot。</summary>
        InvariantCheckArchiveAndOutput = 19
    }

    /// <summary>阶段总数与冻结顺序（唯一权威列表）。</summary>
    public static class StepPhaseExtensions
    {
        public const int PhaseCount = 20;

        /// <summary>按执行顺序排列的全部阶段。</summary>
        public static readonly IReadOnlyList<StepPhase> FrozenOrder = new[]
        {
            StepPhase.PreCommandBoundaries,
            StepPhase.StateAndEffectAdvance,
            StepPhase.DeathAndVictory,
            StepPhase.WindowOpen,
            StepPhase.ReactionOpportunityRefresh,
            StepPhase.FrozenBatchMergeAndBaseRevision,
            StepPhase.CommandValidationAndScheduling,
            StepPhase.DuePlanStartGateAndReactionTrigger,
            StepPhase.IntentDrain,
            StepPhase.LegacyContactCaptureAndDodgeCommit,
            StepPhase.ConflictGraphAndResolution,
            StepPhase.DamageAggregationAndDisplacementRequests,
            StepPhase.ForcedDisplacementSolve,
            StepPhase.PlanReservationCleanupAndBatchCommit,
            StepPhase.StateControlAndAdrenalineAccrual,
            StepPhase.PostDisplacementDeath,
            StepPhase.VictoryReevaluation,
            StepPhase.WindowCloseAndScheduleNext,
            StepPhase.DecisionSnapshotDelivery,
            StepPhase.InvariantCheckArchiveAndOutput
        };

        public static int IndexOf(StepPhase phase) => (int)phase;
    }

    /// <summary>
    /// 一次 Step 的阶段轨迹条目（诊断用，<strong>不参与</strong>规范化哈希，也不构成恢复状态）。
    /// 它让"阶段顺序"可被测试直接断言，而不是靠注释猜测。
    /// <see cref="Timestamp"/> 只在启用阶段计时采样时非 0
    /// （<see cref="BattleSimulationAssembly.PhaseTiming"/>），使用纯 BCL 的
    /// <c>Stopwatch.GetTimestamp()</c>，绝不读取 Unity 帧时间。
    /// </summary>
    public readonly struct StepPhaseTraceEntry
    {
        public StepPhaseTraceEntry(long tick, StepPhase phase, int order, long timestamp, long allocatedBytesAtEntry = 0)
        { Tick = tick; Phase = phase; Order = order; Timestamp = timestamp; AllocatedBytesAtEntry = allocatedBytesAtEntry; }
        public long Tick { get; }
        public StepPhase Phase { get; }
        public int Order { get; }
        public long Timestamp { get; }
        public long AllocatedBytesAtEntry { get; }
    }

    /// <summary>
    /// 阶段计时采样器（任务 01 §19 的"Step 内按阶段采样"要求）。
    ///
    /// Logic 程序集不引用 <c>UnityEngine.Profiling</c>，因此采样使用纯 BCL 时间戳，
    /// 由调用方显式装配（默认关闭）。<strong>计时结果绝不进入哈希或任何逻辑输入</strong>：
    /// 它只是给任务 11 长战斗验收使用的证据。
    /// </summary>
    public sealed class StepPhaseTimingRecorder
    {
        internal Func<long> AllocationReader { get; }
        public StepPhaseTimingRecorder(Func<long> allocationReader = null)
        { AllocationReader = allocationReader ?? GC.GetAllocatedBytesForCurrentThread; }
        private readonly long[] _invocations = new long[StepPhaseExtensions.PhaseCount];
        private readonly long[] _totalTicks = new long[StepPhaseExtensions.PhaseCount];
        private readonly long[] _maxTicks = new long[StepPhaseExtensions.PhaseCount];
        private readonly long[] _ticksMeasured = new long[StepPhaseExtensions.PhaseCount];
        private readonly long[] _allocatedBytes = new long[StepPhaseExtensions.PhaseCount];

        /// <summary>采样到的 Tick 数。</summary>
        public long MeasuredTicks { get; private set; }

        internal void RecordTick(StepPhaseTrace trace, long endTimestamp, long endAllocatedBytes)
        {
            IReadOnlyList<StepPhaseTraceEntry> entries = trace.Entries;
            if (entries.Count == 0) return;

            for (int i = 0; i < entries.Count; i++)
            {
                long start = entries[i].Timestamp;
                long end = i + 1 < entries.Count ? entries[i + 1].Timestamp : endTimestamp;
                if (start == 0L || end < start) continue;

                long elapsed = end - start;
                int index = (int)entries[i].Phase;
                _invocations[index]++;
                _totalTicks[index] += elapsed;
                if (elapsed > _maxTicks[index]) _maxTicks[index] = elapsed;
                _ticksMeasured[index]++;
                long allocationEnd = i + 1 < entries.Count ? entries[i + 1].AllocatedBytesAtEntry : endAllocatedBytes;
                if (allocationEnd >= entries[i].AllocatedBytesAtEntry) _allocatedBytes[index] += allocationEnd - entries[i].AllocatedBytesAtEntry;
            }
            MeasuredTicks++;
        }

        public IReadOnlyList<PhaseTimingEntry> Snapshot()
        {
            var entries = new List<PhaseTimingEntry>(StepPhaseExtensions.PhaseCount);
            for (int i = 0; i < StepPhaseExtensions.PhaseCount; i++)
            {
                entries.Add(new PhaseTimingEntry(
                    StepPhaseExtensions.FrozenOrder[i], _invocations[i], _totalTicks[i], _maxTicks[i], _allocatedBytes[i]));
            }
            return entries;
        }

        public void Reset()
        {
            Array.Clear(_invocations, 0, _invocations.Length);
            Array.Clear(_totalTicks, 0, _totalTicks.Length);
            Array.Clear(_maxTicks, 0, _maxTicks.Length);
            Array.Clear(_ticksMeasured, 0, _ticksMeasured.Length);
            Array.Clear(_allocatedBytes, 0, _allocatedBytes.Length);
            MeasuredTicks = 0L;
        }
    }

    /// <summary>单个阶段的累计采样结果（计数、总耗时、最大耗时；耗时单位为 Stopwatch 刻度）。</summary>
    public sealed record PhaseTimingEntry(StepPhase Phase, long Invocations, long TotalTicks, long MaxTicks, long TotalAllocatedBytes = 0)
    {
        public double MeanTicks => Invocations == 0 ? 0d : (double)TotalTicks / Invocations;
    }

    /// <summary>阶段轨迹记录器（每次 Step 开始时清空）。</summary>
    public sealed class StepPhaseTrace
    {
        private Func<long> _allocationReader;
        private readonly List<StepPhaseTraceEntry> _entries = new List<StepPhaseTraceEntry>(StepPhaseExtensions.PhaseCount);

        public long Tick { get; private set; } = -1L;

        /// <summary>本次 Step 是否采集阶段时间戳。</summary>
        public bool TimestampsEnabled { get; private set; }

        public IReadOnlyList<StepPhaseTraceEntry> Entries => _entries;

        internal void Begin(long tick, bool recordTimestamps, Func<long> allocationReader = null)
        {
            Tick = tick;
            TimestampsEnabled = recordTimestamps;
            _allocationReader = allocationReader;
            _entries.Clear();
        }

        internal void Enter(StepPhase phase)
        {
            _entries.Add(new StepPhaseTraceEntry(
                Tick, phase, _entries.Count,
                TimestampsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L,
                TimestampsEnabled ? _allocationReader?.Invoke() ?? GC.GetAllocatedBytesForCurrentThread() : 0L));
        }

        internal long EndTimestamp() => TimestampsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

        /// <summary>1 起算的执行序位；未执行返回 0。</summary>
        public int OrderOf(StepPhase phase)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Phase == phase) return i + 1;
            }
            return 0;
        }

        public bool Executed(StepPhase phase) => OrderOf(phase) > 0;

        public bool IsBefore(StepPhase earlier, StepPhase later)
        {
            int a = OrderOf(earlier);
            int b = OrderOf(later);
            return a > 0 && b > 0 && a < b;
        }

        public string Describe()
        {
            var parts = new List<string>(_entries.Count);
            for (int i = 0; i < _entries.Count; i++) parts.Add(_entries[i].Phase.ToString());
            return string.Join(" -> ", parts);
        }
    }
}
