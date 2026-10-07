using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Replay;

namespace ProjectHero.Logic.Commands
{
    /// <summary>一条入口级拒绝记录（可能对应一个碰撞组）。</summary>
    public sealed class CommandIngressRejection
    {
        internal CommandIngressRejection(
            ControllerId controllerId,
            CommandSourceKind sourceKind,
            int sourcePriority,
            long producerOrdinal,
            long targetTick,
            string reasonCode,
            int collisionCount)
        {
            ControllerId = controllerId;
            SourceKind = sourceKind;
            SourcePriority = sourcePriority;
            ProducerOrdinal = producerOrdinal;
            TargetTick = targetTick;
            ReasonCode = reasonCode;
            CollisionCount = collisionCount;
        }

        public ControllerId ControllerId { get; }
        public CommandSourceKind SourceKind { get; }
        public int SourcePriority { get; }

        /// <summary>0 表示"从未获得本批序号"（例如批次冻结后才到达的迟到请求）。</summary>
        public long ProducerOrdinal { get; }

        public long TargetTick { get; }

        public string ReasonCode { get; }

        /// <summary>碰撞组成员数量；单键非法时为 1。</summary>
        public int CollisionCount { get; }

        /// <summary>组键：<c>SourcePriority|ControllerId(Ordinal)|ProducerOrdinal</c>。</summary>
        public string GroupKey =>
            SourcePriority.ToString(CultureInfo.InvariantCulture) + "|" +
            (ControllerId.Value ?? string.Empty) + "|" +
            ProducerOrdinal.ToString(CultureInfo.InvariantCulture);

        public int CompareGroupKeyTo(CommandIngressRejection other)
        {
            if (other == null) return 1;
            int byPriority = SourcePriority.CompareTo(other.SourcePriority);
            if (byPriority != 0) return byPriority;
            int byController = StringComparer.Ordinal.Compare(ControllerId.Value, other.ControllerId.Value);
            return byController != 0 ? byController : ProducerOrdinal.CompareTo(other.ProducerOrdinal);
        }
    }

    /// <summary>规范化快照里的单个入口（只读）。</summary>
    public sealed record CommandIngressEntrySnapshot(
        string ControllerId,
        int SourceKind,
        int SourcePriority,
        string RegistrationKind,
        long NextProducerOrdinal,
        long FrozenProducerOrdinal);

    /// <summary>规范化快照里的未来 Tick 桶（只读；只暴露目标 Tick 与待处理数量）。</summary>
    public sealed record CommandIngressTickBucketSnapshot(long TargetTick, int PendingCount);

    /// <summary>
    /// 命令入口注册表的规范化观察快照（只读）。
    /// 入口按 <c>SourcePriority -&gt; ControllerId(Ordinal)</c> 排序，未来桶按 TargetTick 升序。
    /// 它用于尽早发现未来行为偏差，不构成恢复契约。
    /// </summary>
    public sealed record CommandIngressRegistrySnapshot(
        IReadOnlyList<CommandIngressEntrySnapshot> Entries,
        IReadOnlyList<CommandIngressTickBucketSnapshot> FutureBuckets,
        long FrozenThroughTick,
        int PendingRejectionCount);

    /// <summary>
    /// 命令入口（任务 03「必须产出」2）。
    ///
    /// 它是来源身份的<strong>唯一信任边界</strong>：
    /// <list type="bullet">
    /// <item>入口由已验证的 <see cref="ControllerBinding"/> 创建，固定绑定
    /// <c>ControllerId</c> 与 <c>CommandSourceKind</c>；</item>
    /// <item>每条被接受的请求获得该入口<strong>严格递增</strong>的 <c>ProducerOrdinal</c>（从 1 开始）；</item>
    /// <item>外部入口<strong>不得</strong>注册为 <c>System</c>（只有 <c>internal</c> 路径能创建系统来源）；</item>
    /// <item>请求按目标 Tick 分桶；冻结后到达或目标 Tick 被跳过的请求被<strong>稳定拒绝</strong>
    /// 而不是静默丢弃。</item>
    /// </list>
    /// </summary>
    public sealed class CommandIngressRegistry
    {
        private sealed class PendingFact
        {
            public ControllerId ControllerId;
            public CommandSourceKind SourceKind;
            public long ProducerOrdinal;
            public long FrozenWatermarkBefore;
            public bool IsRecordedFactReplay;
            public CommandRequest Request;
        }

        /// <summary>
        /// 设备输入与 AI 的<strong>默认投递提前量</strong>（整数 Tick，首版冻结为 1）。
        ///
        /// 它与来源类型无关：玩家设备输入与 AI 决策使用<strong>同一</strong>提前量，
        /// 因此不存在"AI 比玩家多看一眼"的抢跑空间（任务 09「核心命令语义」）。
        /// </summary>
        public const int CommandIngressLeadTicks = 1;

        private readonly List<CommandIngressEntry> _entries = new List<CommandIngressEntry>();
        private readonly Dictionary<string, CommandIngressEntry> _entriesByController =
            new Dictionary<string, CommandIngressEntry>(StringComparer.Ordinal);
        private readonly Dictionary<long, List<PendingFact>> _buckets = new Dictionary<long, List<PendingFact>>();
        private readonly List<CommandIngressRejection> _pendingRejections = new List<CommandIngressRejection>();
        private readonly List<RecordedCommandRequest> _lastFrozenPlayerFacts = new List<RecordedCommandRequest>();

        private readonly object _registryStamp = new object();

        private long _frozenThroughTick = -1L;
        private long _currentTick = -1L;
        private bool _battleEnded;

        /// <summary>
        /// 本注册表的唯一印记：冻结批次携带它，<c>BattleSimulation.Step</c> 校验它，
        /// 因此只有<strong>本注册表</strong>冻结出的批次能进入模拟。
        /// </summary>
        internal object RegistryStamp => _registryStamp;

        /// <summary>已冻结的最高 Tick；-1 表示尚未冻结任何批次。</summary>
        public long FrozenThroughTick => _frozenThroughTick;

        /// <summary>
        /// 最近一次<strong>已完成</strong> <c>Step</c> 的 Tick（-1 = 尚未推进任何 Tick）的只读镜像。
        /// 由 <see cref="AdvanceCurrentTick"/> 唯一推进；它不参与任何授权判定，
        /// 只是"当前 Tick"这一事实的可观察读数。
        /// </summary>
        public long CurrentTick => _currentTick;

        /// <summary>
        /// <strong>设备输入与 AI 的默认投递目标 Tick</strong>（任务 09「必须产出」5）。
        ///
        /// 唯一判据是"已经冻结过输入的最高 Tick"（它已经在规范化快照与哈希里）：
        /// 目标 = <see cref="FrozenThroughTick"/> + <see cref="CommandIngressLeadTicks"/>。
        /// 在正常推进下 <c>FrozenThroughTick == CurrentTick</c>（每个 Step 都恰好冻结它自己的 Tick），
        /// 因此它就是"<c>CurrentTick + 1</c>"；批次冻结之后它自动变成 <c>N + 2</c>，
        /// 从而"冻结后的输入只能目标下一 Tick"是结构事实而不是调用方约定。
        ///
        /// 它<strong>永不</strong>返回一个已经被冻结过的 Tick：默认投递不可能被静默修正回本 Tick。
        /// </summary>
        public long NextDefaultTargetTick => checked(_frozenThroughTick + CommandIngressLeadTicks);

        /// <summary>
        /// 由唯一模拟入口在<strong>提交</strong>一个 Tick 之后调用（<c>BattleSimulation.Step</c> 的两条收尾路径）。
        /// 它只前进、不回退，且不改变任何授权判定。
        /// </summary>
        internal void AdvanceCurrentTick(long tick)
        {
            if (tick > _currentTick) _currentTick = tick;
        }

        public IReadOnlyList<CommandIngressEntry> Entries => _entries;

        /// <summary>本场最近一次冻结批次中的 Player 可信事实（Tick 0 重演契约的输入记录源）。</summary>
        public IReadOnlyList<RecordedCommandRequest> LastFrozenPlayerFacts => _lastFrozenPlayerFacts;

        /// <summary>
        /// 外部入口注册。拒绝 <c>CommandSourceKind.System</c>
        /// （<see cref="CommandCodes.EXTERNAL_INGRESS_SYSTEM_SOURCE_REJECTED"/>）与重复 ControllerId。
        /// </summary>
        public CommandIngressEntry RegisterExternalEntry(ControllerBinding binding)
        {
            if (binding == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_INGRESS_CONTROLLER_INVALID, "binding is null");

            if (binding.SourceKind == CommandSourceKind.System)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.EXTERNAL_INGRESS_SYSTEM_SOURCE_REJECTED, binding.ControllerId.Value);

            return Register(binding.ControllerId, binding.SourceKind, "External");
        }

        /// <summary>
        /// 系统来源入口：<strong>只有 Logic 内部</strong>可以创建（<c>System</c> 优先级 20）。
        /// 该路径不是 public，因此外部程序集无法把自己注册成系统来源。
        /// </summary>
        internal CommandIngressEntry RegisterSystemEntry(ControllerId controllerId)
            => Register(controllerId, CommandSourceKind.System, "System");

        private CommandIngressEntry Register(ControllerId controllerId, CommandSourceKind sourceKind, string registrationKind)
        {
            if (DefinitionIdValidation.ValidateFormat(controllerId.Value) != null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_INGRESS_CONTROLLER_INVALID, controllerId.Value ?? "<null>");

            if (_entriesByController.ContainsKey(controllerId.Value))
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_INGRESS_DUPLICATE_CONTROLLER, controllerId.Value);

            var entry = new CommandIngressEntry(this, controllerId, sourceKind, registrationKind, _entries.Count);
            _entries.Add(entry);
            _entriesByController[controllerId.Value] = entry;
            return entry;
        }

        /// <summary>按 ControllerId 查找入口；不存在返回 null。</summary>
        public CommandIngressEntry FindEntry(ControllerId controllerId)
        {
            if (controllerId.Value == null) return null;
            return _entriesByController.TryGetValue(controllerId.Value, out var entry) ? entry : null;
        }

        /// <summary>战斗进入终态：此后新命令在入口被稳定拒绝，未来 Tick 桶清空。</summary>
        internal void MarkBattleEnded()
        {
            if (_battleEnded) return;
            _battleEnded = true;
            _buckets.Clear();
        }

        internal bool IsBattleEnded => _battleEnded;

        /// <summary>
        /// 冻结目标 Tick 的批次并返回<strong>唯一</strong>可交给
        /// <c>BattleSimulation.Step</c> 的 <see cref="FrozenCommandBatch"/>。
        /// </summary>
        public FrozenCommandBatch FreezeTick(long tick)
        {
            if (tick <= _frozenThroughTick)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_INGRESS_TICK_NOT_ADVANCING,
                    "tick=" + tick.ToString(CultureInfo.InvariantCulture) +
                    " frozenThrough=" + _frozenThroughTick.ToString(CultureInfo.InvariantCulture));

            // 目标 Tick 在推进中被跳过：这些请求永远等不到 Step，必须显式拒绝而不是静默丢弃。
            var skippedTicks = new List<long>();
            foreach (var pair in _buckets)
            {
                if (pair.Key < tick) skippedTicks.Add(pair.Key);
            }
            skippedTicks.Sort();
            for (int i = 0; i < skippedTicks.Count; i++)
            {
                long skippedTick = skippedTicks[i];
                var stale = _buckets[skippedTick];
                for (int f = 0; f < stale.Count; f++)
                {
                    AddRejection(stale[f], skippedTick, CommandCodes.STALE_REQUEST_TARGET_TICK_SKIPPED);
                }
                _buckets.Remove(skippedTick);
            }

            var facts = new List<SourcedCommandRequest>();
            if (_buckets.TryGetValue(tick, out var bucket) && bucket.Count > 0)
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    PendingFact fact = bucket[i];
                    facts.Add(new SourcedCommandRequest(
                        fact.ControllerId,
                        fact.SourceKind,
                        CommandSourcePriority.Of(fact.SourceKind),
                        fact.ProducerOrdinal,
                        fact.FrozenWatermarkBefore,
                        fact.IsRecordedFactReplay,
                        fact.Request));
                }
            }

            _frozenThroughTick = tick;
            _buckets.Remove(tick);

            // 冻结水位：只把<em>已进入本批</em>的最高序号记为已冻结，
            // 供网关判定"低于已冻结水位的回退序号"。
            for (int i = 0; i < facts.Count; i++)
            {
                SourcedCommandRequest fact = facts[i];
                if (_entriesByController.TryGetValue(fact.ControllerId.Value ?? string.Empty, out var entry))
                    entry.MarkFrozenThrough(fact.ProducerOrdinal);
            }

            _lastFrozenPlayerFacts.Clear();
            for (int i = 0; i < facts.Count; i++)
            {
                SourcedCommandRequest fact = facts[i];
                if (fact.SourceKind != CommandSourceKind.Player) continue;
                _lastFrozenPlayerFacts.Add(new RecordedCommandRequest(
                    fact.ControllerId, fact.SourceKind, fact.ProducerOrdinal, fact.Request));
            }

            return new FrozenCommandBatch(tick, facts.ToArray(), _registryStamp);
        }

        /// <summary>等待被 Step 第 5 阶段排空并转成入口拒绝事件的记录（按组键排序）。</summary>
        internal List<CommandIngressRejection> DrainPendingRejections()
        {
            var drained = new List<CommandIngressRejection>(_pendingRejections);
            _pendingRejections.Clear();
            drained.Sort((a, b) => a.CompareGroupKeyTo(b));
            return drained;
        }

        private void AddRejection(PendingFact fact, long targetTick, string reason)
        {
            _pendingRejections.Add(new CommandIngressRejection(
                fact.ControllerId,
                fact.SourceKind,
                CommandSourcePriority.Of(fact.SourceKind),
                fact.ProducerOrdinal,
                targetTick,
                reason,
                1));
        }

        /// <summary>规范化入口/桶快照（只读，顺序固定）。</summary>
        public CommandIngressRegistrySnapshot CaptureSnapshot()
        {
            var ordered = new List<CommandIngressEntry>(_entries);
            ordered.Sort(CommandIngressEntry.CompareCanonical);
            var entries = new List<CommandIngressEntrySnapshot>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                CommandIngressEntry entry = ordered[i];
                entries.Add(new CommandIngressEntrySnapshot(
                    entry.ControllerId.Value,
                    (int)entry.SourceKind,
                    entry.SourcePriority,
                    entry.RegistrationKind,
                    entry.NextProducerOrdinal,
                    entry.FrozenProducerOrdinal));
            }

            var ticks = new List<long>(_buckets.Keys);
            ticks.Sort();
            var buckets = new List<CommandIngressTickBucketSnapshot>(ticks.Count);
            for (int i = 0; i < ticks.Count; i++)
            {
                buckets.Add(new CommandIngressTickBucketSnapshot(ticks[i], _buckets[ticks[i]].Count));
            }

            // 只读冻结：规范化快照不得暴露可写集合别名。
            return new CommandIngressRegistrySnapshot(
                Array.AsReadOnly(entries.ToArray()),
                Array.AsReadOnly(buckets.ToArray()),
                _frozenThroughTick,
                _pendingRejections.Count);
        }

        // —— 由 CommandIngressEntry 调用的内部入口 ——

        internal CommandIngressRejection OnSubmit(CommandIngressEntry entry, long ordinal, CommandRequest request, out bool accepted)
        {
            if (request == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_REQUEST_NULL, entry.ControllerId.Value);

            if (_battleEnded)
            {
                accepted = false;
                return RecordPending(Reject(entry, 0L, request.TargetTick, CommandCodes.BATTLE_ALREADY_ENDED));
            }

            if (request.TargetTick <= _frozenThroughTick)
            {
                // 批次已冻结：迟到请求不得回写本 Tick，也不得静默丢弃。
                accepted = false;
                return RecordPending(Reject(entry, 0L, request.TargetTick, CommandCodes.LATE_REQUEST_FOR_FROZEN_TICK));
            }

            Enqueue(entry, request.TargetTick, ordinal, entry.FrozenProducerOrdinal, request, isRecordedFactReplay: false);
            accepted = true;
            return null;
        }

        internal CommandIngressRejection OnInjectRecordedFact(
            CommandIngressEntry entry, long producerOrdinal, bool ordinalProvided, CommandRequest request, out bool accepted)
        {
            accepted = false;

            if (entry.SourceKind != CommandSourceKind.Player)
                return RecordPending(Reject(entry, producerOrdinal, request?.TargetTick ?? 0L,
                    CommandCodes.REPLAY_NON_PLAYER_SOURCE_NOT_AUTHORITATIVE));

            if (!ordinalProvided || producerOrdinal < 1L)
                return RecordPending(Reject(entry, producerOrdinal, request?.TargetTick ?? 0L,
                    CommandCodes.COMMAND_ORDINAL_INVALID));

            if (request == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_REQUEST_NULL, entry.ControllerId.Value);

            if (_battleEnded)
                return RecordPending(Reject(entry, 0L, request.TargetTick, CommandCodes.BATTLE_ALREADY_ENDED));

            if (request.TargetTick <= _frozenThroughTick)
                return RecordPending(Reject(entry, 0L, request.TargetTick, CommandCodes.LATE_REQUEST_FOR_FROZEN_TICK));

            Enqueue(entry, request.TargetTick, producerOrdinal, entry.FrozenProducerOrdinal, request, isRecordedFactReplay: true);
            accepted = true;
            return null;
        }

        /// <summary>
        /// 入口级拒绝不仅返回给调用者，还进入待排空队列：拒绝事实必须在某个 Step 的
        /// 第 5 阶段以 <c>CommandIngressRejectedEvent</c> 对外可见，绝不静默消失。
        /// </summary>
        private CommandIngressRejection RecordPending(CommandIngressRejection rejection)
        {
            _pendingRejections.Add(rejection);
            return rejection;
        }

        private static CommandIngressRejection Reject(CommandIngressEntry entry, long ordinal, long targetTick, string reason)
            => new CommandIngressRejection(entry.ControllerId, entry.SourceKind, entry.SourcePriority,
                ordinal, targetTick, reason, 1);

        private void Enqueue(
            CommandIngressEntry entry, long targetTick, long producerOrdinal, long frozenWatermarkBefore,
            CommandRequest request, bool isRecordedFactReplay)
        {
            if (!_buckets.TryGetValue(targetTick, out var bucket))
            {
                bucket = new List<PendingFact>();
                _buckets[targetTick] = bucket;
            }
            bucket.Add(new PendingFact
            {
                ControllerId = entry.ControllerId,
                SourceKind = entry.SourceKind,
                ProducerOrdinal = producerOrdinal,
                FrozenWatermarkBefore = frozenWatermarkBefore,
                IsRecordedFactReplay = isRecordedFactReplay,
                Request = request
            });
        }
    }
}
