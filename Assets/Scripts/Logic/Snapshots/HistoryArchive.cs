using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Determinism;

namespace ProjectHero.Logic.Snapshots
{
    /// <summary>
    /// 历史摘要（主方案 3.11.2）：固定大小的 <c>RecordCount + Digest</c>，不嵌入历史明细，
    /// 也不持有可变归档引用。<see cref="RecordCount"/> 标识<strong>固定历史前缀</strong>：
    /// 后续追加不改变旧快照对应的前缀。
    /// </summary>
    public sealed record HistorySummary(long RecordCount, string Digest)
    {
        /// <summary>初始摘要：空历史 + 固定种子 H0。</summary>
        public static HistorySummary Empty() => new HistorySummary(0L, CanonicalHash.ToHex(ReplayFormat.HistorySeedDigest()));

        public ulong DigestValue => CanonicalHash.FromHex(Digest);
    }

    /// <summary>归档封条：记录必须证明自己"已不再变化且不再提供未来逻辑必需状态"。</summary>
    public sealed record HistorySealProof(bool IsSealed, string NotSealedReason)
    {
        public static HistorySealProof Sealed { get; } = new HistorySealProof(true, null);

        public static HistorySealProof NotSealed(string reason) => new HistorySealProof(false, reason);
    }

    /// <summary>
    /// 一条归档候选（只增不改）。载荷是调用方按 <c>CanonicalEncoder</c> 写好的规范字节；
    /// 归档器会做<strong>防御性拷贝</strong>，之后改写原数组不再影响归档。
    /// </summary>
    public sealed class HistoryArchiveCandidate
    {
        public HistoryArchiveCandidate(
            long archivedAtTick,
            HistoryRecordKind kind,
            string stableKey,
            byte[] payload,
            HistorySealProof seal)
        {
            ArchivedAtTick = archivedAtTick;
            Kind = kind;
            StableKey = stableKey ?? string.Empty;
            Payload = payload ?? Array.Empty<byte>();
            Seal = seal ?? HistorySealProof.NotSealed("MISSING_SEAL_PROOF");
        }

        public long ArchivedAtTick { get; }
        public HistoryRecordKind Kind { get; }
        public string StableKey { get; }
        public byte[] Payload { get; }
        public HistorySealProof Seal { get; }

        /// <summary>稳定排序键：<c>ArchivedAtTick -&gt; RecordKind 整数值 -&gt; StableKey(Ordinal)</c>。</summary>
        public int CompareCanonical(HistoryArchiveCandidate other)
        {
            if (other == null) return 1;
            int byTick = ArchivedAtTick.CompareTo(other.ArchivedAtTick);
            if (byTick != 0) return byTick;
            int byKind = ((int)Kind).CompareTo((int)other.Kind);
            if (byKind != 0) return byKind;
            return string.CompareOrdinal(StableKey, other.StableKey);
        }
    }

    /// <summary>
    /// 深度不可变的归档记录。载荷只有"取一份拷贝"的读取方式，
    /// 生产 API 不允许改写，也不暴露内部数组。
    /// </summary>
    public sealed class ArchivedRecord
    {
        private readonly byte[] _payload;

        /// <summary>
        /// 只用于诊断重算与篡改检测：外部<strong>可以</strong>构造记录值来验证"参照实现能发现被改写/丢失的记录"，
        /// 但归档器只接受经过 <see cref="HistoryArchive.AppendFinalizedOrdered"/> 的候选，
        /// 不存在把外来记录注入权威归档的公开路径。
        /// </summary>
        public ArchivedRecord(long archiveIndex, long archivedAtTick, HistoryRecordKind kind, string stableKey, byte[] payload, ulong digestAfterAppend)
        {
            ArchiveIndex = archiveIndex;
            ArchivedAtTick = archivedAtTick;
            Kind = kind;
            StableKey = stableKey ?? string.Empty;
            _payload = payload == null ? Array.Empty<byte>() : (byte[])payload.Clone();
            DigestAfterAppend = digestAfterAppend;
        }

        /// <summary>1 起算的追加序号（不占用玩法 ID，也不占用 EventSequence）。</summary>
        public long ArchiveIndex { get; }

        public long ArchivedAtTick { get; }
        public HistoryRecordKind Kind { get; }
        public string StableKey { get; }

        /// <summary>本次追加后的增量摘要 Hi。</summary>
        public ulong DigestAfterAppend { get; }

        public int PayloadLength => _payload.Length;

        /// <summary>载荷的防御性拷贝（读取历史会记入访问计数）。</summary>
        public byte[] PayloadCopy() => (byte[])_payload.Clone();

        /// <summary>仅诊断重算使用的只读视图（不暴露可写数组，长度前缀由协议写入）。</summary>
        internal byte[] PayloadRaw => _payload;
    }

    /// <summary>
    /// 归档访问计数（任务 01 §19 冻结口径的证据夹具）：
    /// <c>无新增归档的 Tick：读取/复制/哈希旧归档记录数 = 0</c>。
    /// 计数是累计值，测试通过前后差值断言"只处理本批"。
    /// </summary>
    public sealed class HistoryAccessCounters
    {
        internal HistoryAccessCounters() { }

        /// <summary>读取旧归档记录（<c>ReadPrefix</c> / 参照重算）。</summary>
        public long OldRecordReads { get; internal set; }

        /// <summary>复制旧归档记录载荷。</summary>
        public long OldRecordCopies { get; internal set; }

        /// <summary>对旧归档记录做哈希（参照路径重算已有前缀）。</summary>
        public long OldRecordHashes { get; internal set; }

        /// <summary>本批实际处理的<strong>新增</strong>记录数。</summary>
        public long NewRecordsProcessed { get; internal set; }

        /// <summary>累计追加记录数。</summary>
        public long TotalAppends { get; internal set; }

        /// <summary>因未封条而被跳过的候选数（可变账本不得提前归档）。</summary>
        public long IneligibleCandidatesSkipped { get; internal set; }

        public HistoryAccessCounters DeltaFrom(HistoryAccessCounters baseline) => new HistoryAccessCounters
        {
            OldRecordReads = OldRecordReads - (baseline?.OldRecordReads ?? 0L),
            OldRecordCopies = OldRecordCopies - (baseline?.OldRecordCopies ?? 0L),
            OldRecordHashes = OldRecordHashes - (baseline?.OldRecordHashes ?? 0L),
            NewRecordsProcessed = NewRecordsProcessed - (baseline?.NewRecordsProcessed ?? 0L),
            TotalAppends = TotalAppends - (baseline?.TotalAppends ?? 0L),
            IneligibleCandidatesSkipped = IneligibleCandidatesSkipped - (baseline?.IneligibleCandidatesSkipped ?? 0L)
        };

        /// <summary>值拷贝快照。它是"基线"的唯一正确用法：直接持有活动引用会让差值恒为 0。</summary>
        public HistoryAccessCounters Snapshot() => new HistoryAccessCounters
        {
            OldRecordReads = OldRecordReads,
            OldRecordCopies = OldRecordCopies,
            OldRecordHashes = OldRecordHashes,
            NewRecordsProcessed = NewRecordsProcessed,
            TotalAppends = TotalAppends,
            IneligibleCandidatesSkipped = IneligibleCandidatesSkipped
        };

        public override string ToString()
            => "reads=" + OldRecordReads.ToString(CultureInfo.InvariantCulture) +
               ",copies=" + OldRecordCopies.ToString(CultureInfo.InvariantCulture) +
               ",hashes=" + OldRecordHashes.ToString(CultureInfo.InvariantCulture) +
               ",new=" + NewRecordsProcessed.ToString(CultureInfo.InvariantCulture) +
               ",appends=" + TotalAppends.ToString(CultureInfo.InvariantCulture) +
               ",ineligible=" + IneligibleCandidatesSkipped.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 历史摘要协议（主方案 3.11.2 冻结公式）：
    /// <code>
    /// H0 = Hash(Encode("HistorySeed", ReplayFormatVersion))
    /// Hi = Hash(Encode("HistoryAppend", ReplayFormatVersion, H(i-1), i, ArchivedAtTick, RecordKind, StableKey, RecordPayload))
    /// </code>
    /// 全部字段按 <see cref="CanonicalEncoder"/> 的域分离长度前缀编码写入，
    /// 因此任何字段顺序、类型键或编码变化都会改变摘要——这正是格式版本护栏。
    /// </summary>
    public static class HistoryDigestProtocol
    {
        public static ulong Seed() => ReplayFormat.HistorySeedDigest();

        public static ulong Append(
            ulong previousDigest,
            long archiveIndex,
            long archivedAtTick,
            HistoryRecordKind kind,
            string stableKey,
            byte[] payload)
            => Append(previousDigest, archiveIndex, archivedAtTick, kind, stableKey, payload, ReplayFormat.Version);

        public static ulong Append(
            ulong previousDigest,
            long archiveIndex,
            long archivedAtTick,
            HistoryRecordKind kind,
            string stableKey,
            byte[] payload,
            int formatVersion)
        {
            var encoder = new CanonicalEncoder(64 + (payload?.Length ?? 0));
            encoder.BeginDomain(ReplayFormat.HistoryAppendDomain);
            encoder.WriteInt32(formatVersion);
            encoder.WriteUInt64(previousDigest);
            encoder.WriteInt64(archiveIndex);
            encoder.WriteInt64(archivedAtTick);
            encoder.WriteInt32((int)kind);
            encoder.WriteString(stableKey);
            encoder.WriteBytes(payload);
            return encoder.ToDigest();
        }

        /// <summary>
        /// <strong>独立参照实现</strong>：从 H0 开始，顺序读取完整归档并逐条重算，
        /// <em>不</em>复用增量路径的任何缓存值（<see cref="ArchivedRecord.DigestAfterAppend"/> 被忽略）。
        /// 诊断展开不写权威状态；它只增加访问计数。
        /// </summary>
        public static ulong RecomputeFromSeed(IReadOnlyList<ArchivedRecord> records, HistoryAccessCounters counters = null)
            => RecomputeFromSeed(records, ReplayFormat.Version, counters);

        public static ulong RecomputeFromSeed(IReadOnlyList<ArchivedRecord> records, int formatVersion, HistoryAccessCounters counters = null)
        {
            ulong digest = ReplayFormat.HistorySeedDigest(formatVersion);
            if (records == null) return digest;

            for (int i = 0; i < records.Count; i++)
            {
                ArchivedRecord record = records[i];
                if (record == null) continue;
                if (counters != null)
                {
                    counters.OldRecordReads++;
                    counters.OldRecordCopies++;
                    counters.OldRecordHashes++;
                }
                digest = Append(digest, record.ArchiveIndex, record.ArchivedAtTick, record.Kind,
                    record.StableKey, record.PayloadRaw, formatVersion);
            }
            return digest;
        }
    }

    /// <summary>诊断视图：按 RecordCount 读取的不可变历史前缀（不是第二套权威状态）。</summary>
    public sealed record HistoryDiagnosticView(long RecordCount, string Digest, IReadOnlyList<ArchivedRecord> Records);

    /// <summary>
    /// 历史归档器（主方案 3.11.2）。
    ///
    /// 关键不变量：
    /// <list type="bullet">
    /// <item>只处理<strong>增量候选</strong>：不扫描历史注册表找新记录，也不读取/复制/哈希旧记录；
    /// 无新增时候选批次为空 → 旧记录访问计数恒为 0。</item>
    /// <item>同一 Tick 收集完成后才按 <c>ArchivedAtTick -&gt; RecordKind -&gt; StableKey</c> 规范排序并追加，
    /// 因此候选枚举顺序不影响记录顺序与摘要。</item>
    /// <item>记录只追加一次（重复 <c>ArchivedAtTick+Kind+StableKey</c> 被拒绝），
    /// 归档不改变计划身份、终态、ID/Sequence 或事件顺序。</item>
    /// <item>未封条（仍可能有退款/消费/预留）的候选被跳过并计数，绝不提前冻结可变账本。</item>
    /// </list>
    /// </summary>
    public sealed class HistoryArchive
    {
        public const string HISTORY_RECORD_DUPLICATE = "HISTORY_RECORD_DUPLICATE";
        public const string HISTORY_RECORD_KIND_INVALID = "HISTORY_RECORD_KIND_INVALID";
        public const string HISTORY_PREFIX_OUT_OF_RANGE = "HISTORY_PREFIX_OUT_OF_RANGE";

        private readonly List<ArchivedRecord> _records = new List<ArchivedRecord>();
        private readonly HashSet<string> _canonicalKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _lastSkippedReasons = new List<string>();
        private readonly HistoryAccessCounters _counters = new HistoryAccessCounters();

        public HistoryArchive()
        {
            Summary = HistorySummary.Empty();
        }

        public HistorySummary Summary { get; private set; }

        public long RecordCount => Summary.RecordCount;

        /// <summary>访问计数的值拷贝快照（累计值；测试用 <see cref="HistoryAccessCounters.DeltaFrom"/> 取差值）。</summary>
        public HistoryAccessCounters AccessCounters => _counters.Snapshot();

        /// <summary>最近一次追加中因未封条而被跳过的候选原因（诊断，不改变哈希）。</summary>
        public IReadOnlyList<string> LastSkippedReasons => _lastSkippedReasons;

        /// <summary>
        /// 追加本 Tick 已冻结的候选。空批次<strong>不读取、不复制、不哈希</strong>任何旧记录。
        /// </summary>
        public void AppendFinalizedOrdered(long tick, IReadOnlyList<HistoryArchiveCandidate> candidates)
        {
            _lastSkippedReasons.Clear();
            if (candidates == null || candidates.Count == 0) return;

            var accepted = new List<HistoryArchiveCandidate>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                HistoryArchiveCandidate candidate = candidates[i];
                if (candidate == null) continue;

                if (!HistoryRecordKinds.IsValid(candidate.Kind))
                    throw new ProjectHero.Logic.LogicDefinitionException(
                        HISTORY_RECORD_KIND_INVALID,
                        "kind=" + (int)candidate.Kind + " tick=" + candidate.ArchivedAtTick.ToString(CultureInfo.InvariantCulture));

                if (!candidate.Seal.IsSealed)
                {
                    _counters.IneligibleCandidatesSkipped++;
                    _lastSkippedReasons.Add(candidate.StableKey + ":" + (candidate.Seal.NotSealedReason ?? "UNSEALED"));
                    continue;
                }

                accepted.Add(candidate);
            }

            if (accepted.Count == 0) return;

            accepted.Sort((a, b) => a.CompareCanonical(b));

            // 先做整体冲突预检：任何重复键都在写入任何记录之前失败，绝不留部分写入。
            var batchKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < accepted.Count; i++)
            {
                string canonicalKey = CanonicalKeyOf(accepted[i]);
                if (_canonicalKeys.Contains(canonicalKey) || !batchKeys.Add(canonicalKey))
                    throw new ProjectHero.Logic.LogicDefinitionException(HISTORY_RECORD_DUPLICATE, canonicalKey);
            }

            ulong digest = Summary.DigestValue;
            long index = Summary.RecordCount;
            for (int i = 0; i < accepted.Count; i++)
            {
                HistoryArchiveCandidate candidate = accepted[i];
                index++;
                string canonicalKey = CanonicalKeyOf(candidate);

                _canonicalKeys.Add(canonicalKey);

                // 归档总是做防御性拷贝：之后改写原数组不影响已冻结记录。
                byte[] frozenPayload = candidate.Payload; // ArchivedRecord owns the single defensive copy.
                digest = HistoryDigestProtocol.Append(
                    digest, index, candidate.ArchivedAtTick, candidate.Kind, candidate.StableKey, frozenPayload);

                _records.Add(new ArchivedRecord(index, candidate.ArchivedAtTick, candidate.Kind,
                    candidate.StableKey, frozenPayload, digest));

                _counters.NewRecordsProcessed++;
                _counters.TotalAppends++;
            }

            Summary = new HistorySummary(Summary.RecordCount + accepted.Count, CanonicalHash.ToHex(digest));
        }

        private static string CanonicalKeyOf(HistoryArchiveCandidate candidate)
            => candidate.ArchivedAtTick.ToString(CultureInfo.InvariantCulture) + "|" +
               ((int)candidate.Kind).ToString(CultureInfo.InvariantCulture) + "|" +
               candidate.StableKey;

        /// <summary>
        /// 诊断查询：按记录数读取不可变历史前缀（计数读取/复制，不写权威状态）。
        /// </summary>
        public HistoryDiagnosticView BuildDiagnosticView(int recordCount)
        {
            if (recordCount < 0 || recordCount > _records.Count)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    HISTORY_PREFIX_OUT_OF_RANGE, recordCount.ToString(CultureInfo.InvariantCulture));

            var prefix = new List<ArchivedRecord>(recordCount);
            for (int i = 0; i < recordCount; i++)
            {
                prefix.Add(_records[i]);
                _counters.OldRecordReads++;
                _counters.OldRecordCopies++;
            }

            ulong digest = HistoryDigestProtocol.RecomputeFromSeed(prefix, ReplayFormat.Version, _counters);
            return new HistoryDiagnosticView(recordCount, CanonicalHash.ToHex(digest), prefix);
        }

        /// <summary>
        /// 诊断/测试用：整段归档的只读快照（读取会记入访问计数）。
        /// 篡改测试在此拷贝上执行，绝不影响权威归档。
        /// </summary>
        public IReadOnlyList<ArchivedRecord> CopyAllForDiagnostics()
        {
            var all = new List<ArchivedRecord>(_records.Count);
            for (int i = 0; i < _records.Count; i++)
            {
                ArchivedRecord record = _records[i];
                _counters.OldRecordReads++;
                _counters.OldRecordCopies++;
                all.Add(new ArchivedRecord(record.ArchiveIndex, record.ArchivedAtTick, record.Kind,
                    record.StableKey, record.PayloadCopy(), record.DigestAfterAppend));
            }
            return all;
        }

        /// <summary>诊断重算：与增量结果比较的是<strong>同一摘要协议</strong>。</summary>
        public ulong RecomputeFullDigestForDiagnostics()
            => HistoryDigestProtocol.RecomputeFromSeed(CopyAllForDiagnostics(), ReplayFormat.Version, _counters);
    }
}
