using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Authoring.Tests.Task03
{
    /// <summary>
    /// 任务 01 §19 的"新内核测量夹具"：Step 阶段采样、固定容量直方图 P50/P95/P99、
    /// 每 Tick 分配与历史访问计数证据。
    ///
    /// 本夹具<strong>只做证据化声明</strong>：它把实测值写入测试结果 XML 的 output 节点，
    /// 并只断言不依赖机器速度的不变量（阶段采样覆盖 20 个阶段、阶段采样与外部测量同量级、
    /// 无新增归档时旧历史访问为 0）。§19 的硬阈值（P50 ≤ 0.5ms / P95 ≤ 1.0ms / P99 ≤ 2.0ms、
    /// 每 Tick ≤ 8KB、30 分钟长战斗）属于任务 11 在 Development Build + Profiler 下的验收，
    /// 本轮不伪造通过结论：分配计数不可靠时明确报告"未取得可用于阈值判定的有效测量"。
    /// </summary>
    public class StepEvidenceTests
    {
        private const int SampleTicks = 900;

        [Test]
        public void StepPhaseSamplingAndArchiveAccessCountersProvideEvidence()
        {
            var timing = new StepPhaseTimingRecorder();
            var archiveSource = new FixtureArchiveSource { SealedPerTick = 1 };
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                turnWindowSchedule: new ScriptedWindowSchedule { OpenTick = 0L, OwnerUnitId = Task03.HeroUnitId.Value },
                archiveCandidateSources: new IHistoryArchiveCandidateSource[] { archiveSource },
                phaseTiming: timing));

            var totalTicks = new FixedCapacityHistogram(2048);
            var allocationsPerTick = new List<long>(SampleTicks);

            // 预热（JIT、字典扩容等），不计入样本。
            for (int i = 0; i < 50; i++) Task03.StepNext(sim);

            for (int i = 0; i < SampleTicks; i++)
            {
                long allocationBefore = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
                long start = Stopwatch.GetTimestamp();
                Task03.StepNext(sim);
                long end = Stopwatch.GetTimestamp();

                // §19 口径：每 Tick 分配用 Profiler.GetTotalAllocatedMemoryLong 差值法。
                allocationsPerTick.Add(
                    UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() - allocationBefore);
                totalTicks.Add((end - start) * 1000d / Stopwatch.Frequency); // ms
            }

            // 1. 阶段采样必须覆盖全部 20 个阶段（每个阶段的调用次数与 Tick 数一致）。
            IReadOnlyList<PhaseTimingEntry> phases = timing.Snapshot();
            Assert.That(phases.Count, Is.EqualTo(StepPhaseExtensions.PhaseCount));
            Assert.That(timing.MeasuredTicks, Is.GreaterThanOrEqualTo(SampleTicks + 50L));
            foreach (PhaseTimingEntry phase in phases)
            {
                Assert.That(phase.Invocations, Is.GreaterThan(0L), $"{phase.Phase} 必须被采样到");
                Assert.That(phase.TotalTicks, Is.GreaterThanOrEqualTo(0L));
            }

            // 2. 无新增归档的 Tick 不得读取/复制/哈希旧历史（硬不变量，见 §19 历史访问口径）。
            HistoryAccessCounters counters = sim.HistoryAccessCounters;
            var plainSim = Task03.NewSim();
            HistoryAccessCounters baseline = plainSim.HistoryAccessCounters;
            for (int i = 0; i < 200; i++) Task03.StepNext(plainSim);
            HistoryAccessCounters delta = plainSim.HistoryAccessCounters.DeltaFrom(baseline);
            Assert.That(delta.OldRecordReads + delta.OldRecordCopies + delta.OldRecordHashes, Is.EqualTo(0L),
                "没有归档来源时，每 Tick 旧历史访问次数必须为 0");

            // 3. 报告实测值（证据化声明，不做硬阈值判断）。
            double meanAllocation = allocationsPerTick.Average();
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[03 证据] Step 样本={0} 归档记录={1} 旧历史访问(读/复制/哈希)={2}/{3}/{4}",
                SampleTicks, sim.History.RecordCount,
                counters.OldRecordReads, counters.OldRecordCopies, counters.OldRecordHashes));
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[03 证据] Step 总耗时 ms: P50={0:F4} P95={1:F4} P99={2:F4} max={3:F4}",
                totalTicks.Percentile(50), totalTicks.Percentile(95), totalTicks.Percentile(99), totalTicks.Max));
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[03 证据] 每 Tick 分配 bytes: 平均={0:F0} 最大={1}",
                meanAllocation, allocationsPerTick.Max()));

            // 阶段采样总和与外部测量必须同量级（同一 Tick 的两种口径互证）。
            double innerTotalMs = phases.Sum(p => p.MeanTicks) * 1000d / Stopwatch.Frequency;
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[03 证据] 阶段采样总和(每 Tick 平均)={0:F4}ms; 外部 Step P50={1:F4}ms",
                innerTotalMs, totalTicks.Percentile(50)));
            Assert.That(innerTotalMs, Is.LessThan(totalTicks.Percentile(50) * 5d + 1d),
                "阶段采样总和不得与外部 Step 测量严重背离");

            foreach (PhaseTimingEntry phase in phases.OrderByDescending(p => p.TotalTicks).Take(6))
            {
                TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[03 证据] 阶段 {0}: 调用={1} 平均={2:F4}ms 最大={3:F4}ms",
                    phase.Phase, phase.Invocations,
                    phase.MeanTicks * 1000d / Stopwatch.Frequency, phase.MaxTicks * 1000d / Stopwatch.Frequency));
            }

            // 4. 每 Tick 分配：本环境批处理 EditMode 下 Profiler.GetTotalAllocatedMemoryLong 的
            //    读数不可靠（同一夹具上 0 与 65536 均出现过），因此它**不构成**可用于 §19
            //    阈值判定的有效测量。这里只如实报告观测值并明确标注"未取得有效测量"，
            //    绝不用全 0（或偶发 64KB 跳变）样本伪造通过。
            long maxAllocationBytes = allocationsPerTick.Max();
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[03 证据] 每 Tick 分配：本环境批次运行下 Profiler.GetTotalAllocatedMemoryLong 读数不可靠" +
                "（0 与 65536 均出现），未取得可用于阈值判定的有效测量；" +
                "不以全 0 样本伪造通过，§19 的 ≤8KB 每 Tick 分配验收必须在任务 11 的 Development Build + Profiler 中执行。"));

            if (maxAllocationBytes > 0L)
            {
                // 读数非 0 时仍做一次宽松自查（这不是 §19 阈值判定）：分配不得随历史增长而显著膨胀。
                Assert.That(allocationsPerTick.Skip(SampleTicks / 2).Average(),
                    Is.LessThan(allocationsPerTick.Take(SampleTicks / 2).Average() * 3d + 4096d),
                    "每 Tick 分配不得随历史增长而显著膨胀");
            }
        }

        /// <summary>固定容量直方图（§19 要求：P50/P95/P99 用直方图，不保留全量样本）。</summary>
        private sealed class FixedCapacityHistogram
        {
            private const double BucketWidthMs = 0.01d;
            private readonly long[] _buckets;
            private long _count;
            private double _max;

            public FixedCapacityHistogram(int capacity)
            {
                _buckets = new long[capacity];
            }

            public long Count => _count;

            public void Add(double milliseconds)
            {
                int index = (int)Math.Round(milliseconds / BucketWidthMs, MidpointRounding.AwayFromZero);
                if (index < 0) index = 0;
                if (index >= _buckets.Length) index = _buckets.Length - 1;
                _buckets[index]++;
                _count++;
                if (milliseconds > _max) _max = milliseconds;
            }

            public double Max => _max;

            public double Percentile(double percentile)
            {
                if (_count == 0) return 0d;
                long target = (long)Math.Ceiling(_count * percentile / 100d);
                long running = 0;
                for (int i = 0; i < _buckets.Length; i++)
                {
                    running += _buckets[i];
                    if (running >= target) return i * BucketWidthMs;
                }
                return _max;
            }
        }
    }
}
