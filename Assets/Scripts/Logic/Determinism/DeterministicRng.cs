using System;
using System.Globalization;

namespace ProjectHero.Logic.Determinism
{
    /// <summary>
    /// 版本化确定性随机数发生器（主方案 2.2 / 3.11.1 <c>RngSnapshot</c>）。
    ///
    /// 冻结算法：SplitMix64（<see cref="AlgorithmId"/> = <c>splitmix64-v1</c>）。
    /// 全部运算都是无符号 64 位整数运算，与平台、文化、浮点无关；
    /// 不使用 <c>System.Random</c>（其序列不承诺跨运行时稳定），也不读取时间或帧计数。
    ///
    /// 契约：
    /// <list type="bullet">
    /// <item>状态必须进入规范化快照与哈希（<see cref="State"/>）。</item>
    /// <item>随机选择前候选集<strong>必须</strong>由调用方按稳定键排序；
    /// 本类型只接受"候选数量"，不接受无序集合，因此不存在"随机挑一个容器元素"的入口。</item>
    /// <item>首版<strong>不提供</strong>从中途状态恢复模拟的公开 API：构造只接受种子，
    /// <see cref="State"/> 是只读观察值，没有 setter、没有 <c>FromState</c> 工厂。
    /// 未来的中途恢复必须另行设计版本化完整状态，不允许把观察值当成存档格式。</item>
    /// </list>
    /// </summary>
    public sealed class DeterministicRng
    {
        public const int AlgorithmVersion = 1;
        public const string AlgorithmId = "splitmix64-v1";

        private const ulong Gamma = 0x9E3779B97F4A7C15UL;

        private ulong _state;

        public DeterministicRng(ulong seed)
        {
            _state = seed;
        }

        /// <summary>当前 RNG 状态（只读；进入规范化快照与哈希）。</summary>
        public ulong State => _state;

        /// <summary>下一个 64 位无符号随机数（推进状态一次）。</summary>
        public ulong NextUInt64()
        {
            unchecked
            {
                _state += Gamma;
                ulong z = _state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>取高 32 位后取模：跨平台稳定，且与 <see cref="NextUInt64"/> 的推进语义一致。</summary>
        public uint NextUInt32() => (uint)(NextUInt64() >> 32);

        public int PickIndex(int candidateCount)
        {
            if (candidateCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(candidateCount),
                    "candidate count must be positive; empty candidate sets must be filtered before RNG use");
            return (int)(NextUInt32() % (uint)candidateCount);
        }

        public RngSnapshot CaptureSnapshot() => new RngSnapshot(AlgorithmVersion, _state);
    }

    /// <summary>
    /// RNG 观察快照（只读、值语义）。它不是恢复输入：没有把它交回
    /// <see cref="DeterministicRng"/> 的公开构造路径。
    /// </summary>
    public sealed record RngSnapshot(int AlgorithmVersion, ulong State)
    {
        public override string ToString()
            => AlgorithmVersion.ToString(CultureInfo.InvariantCulture) + ":" +
               State.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 确定性序列计数器（主方案 3.11.1：Command / Intent / Resolution / Event / Effect 顺序）。
    ///
    /// 与实例 ID 生成器<strong>完全独立</strong>：Sequence 不占用玩法 ID，ID 也不占用 Sequence；
    /// 每类计数独立、从 1 开始、0 保留为无效值、整场战斗单调递增且不复用。
    /// 所有"下一个值"都进入规范化快照与哈希。
    /// </summary>
    public sealed class LogicSequenceGenerator
    {
        private long _nextCommandSequence = 1;
        private long _nextIntentSequence = 1;
        private long _nextResolutionSequence = 1;
        private long _nextEventSequence = 1;
        private long _nextEffectSequence = 1;

        public long NextCommandSequence => _nextCommandSequence;
        public long NextIntentSequence => _nextIntentSequence;
        public long NextResolutionSequence => _nextResolutionSequence;
        public long NextEventSequence => _nextEventSequence;
        public long NextEffectSequence => _nextEffectSequence;

        public long TakeCommandSequence() => _nextCommandSequence++;
        public long TakeIntentSequence() => _nextIntentSequence++;
        public long TakeResolutionSequence() => _nextResolutionSequence++;
        public long TakeEventSequence() => _nextEventSequence++;
        public long TakeEffectSequence() => _nextEffectSequence++;
    }
}
