using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ProjectHero.Logic.Determinism
{
    /// <summary>
    /// 历史归档记录类型（主方案 3.11.2「归档总顺序」的 RecordKind 分量）。
    ///
    /// 数值是<strong>版本化整数键</strong>，参与编码与摘要，因此不得重排、复用或改值；
    /// 新增类型只能追加新的整数值，并同时提升 <see cref="ReplayFormat.Version"/>。
    /// 归档顺序为 <c>ArchivedAtTick -&gt; RecordKind 整数值 -&gt; StableKey</c>。
    /// </summary>
    public enum HistoryRecordKind
    {
        /// <summary>0 保留为无效值：没有类型的记录不得进入归档。</summary>
        Invalid = 0,

        /// <summary>普通/反应计划进入终态并经统一协调器清理完成后的审计记录（任务 05 接入真实候选）。</summary>
        ActionPlanTerminal = 1,

        /// <summary>关闭且不再退款/消费的窗口账本（任务 07 接入真实候选）。</summary>
        WindowLedger = 2,

        /// <summary>已关闭且不再触发/退款的反应机会绑定（任务 05/08 接入真实候选）。</summary>
        ReactionOpportunityBinding = 3,

        /// <summary>已清空的入口 Tick 桶审计记录（任务 03 基础设施；任务 09 接入真实入口）。</summary>
        CommandIngressBucket = 4
    }

    /// <summary>
    /// 回放/归档格式版本（主方案 3.11：<c>ReplayFormatVersion</c> 只版本化文件结构、字段语义
    /// 与哈希编码；<c>RulesVersion</c> 版本化玩法规则，两者不得混用）。
    ///
    /// 本类型是纯 C# 常量与协议描述的唯一来源：
    /// <list type="bullet">
    /// <item>算法 ID、摘要宽度与整数字节序；</item>
    /// <item>域标记（HistorySeed / HistoryAppend / LogicSnapshot）与长度前缀规则；</item>
    /// <item>字符串/布尔/集合编码；</item>
    /// <item><see cref="HistoryRecordKind"/> 的整数值。</item>
    /// </list>
    /// 改变其中任何一项都必须提升 <see cref="Version"/>（并更新相应 golden），
    /// 因为 <see cref="HistorySeedDigest"/>、历史增量摘要与快照哈希都把它写进域标记。
    /// 该协议变化不改变玩法规则，因此不得借此改写 <c>RulesVersion</c> 或
    /// <c>BattleDefinitionHash</c>。
    /// </summary>
    public static class ReplayFormat
    {
        /// <summary>
        /// 当前回放/归档数据与哈希编码版本。
        ///
        /// <list type="bullet">
        /// <item><strong>1</strong>：首版（任务 03 冻结）。</item>
        /// <item><strong>2</strong>（任务 08 快照契约）：<c>LogicSnapshot</c> 的<strong>快照载荷字段集</strong>扩展——
        /// <c>IntentSnapshot</c> 由三字段扩为完整 Intent 载荷，并新增冲突图的
        /// <c>ConflictGroupSnapshot</c> / <c>ContactSnapshot</c> 两个集合。
        /// 快照域里的字段集与字段顺序变了，旧回放资产若按同一版本号读取会被<strong>静默错读</strong>，
        /// 因此必须提升格式版本；这不涉及玩法规则，<c>RulesVersion</c> 与
        /// <c>BattleDefinitionHash</c> 都不因此改变。</item>
        /// </list>
        /// </summary>
        // v3: future ingress buckets include full canonical requests; recordings preserve
        // original submission boundaries independently of target Tick. Rules remain unchanged.
        public const int Version = 3;

        public const string HashAlgorithmId = "fnv1a64";
        public const int DigestHexWidth = 16;
        public const string IntegerEncoding = "int64-little-endian";
        public const string StringEncoding = "utf8-with-int64-byte-length-prefix";
        public const string BoolEncoding = "uint8-0-or-1";
        public const string BytesEncoding = "raw-with-int64-length-prefix";
        public const string CollectionEncoding = "int64-count-then-stable-ordered-items";
        public const string DomainEncoding = "length-prefixed-domain-tag";
        public const string NullReferenceEncoding = "null-and-empty-encode-identically";

        public const string HistorySeedDomain = "HistorySeed";
        public const string HistoryAppendDomain = "HistoryAppend";
        public const string LogicSnapshotDomain = "LogicSnapshot";
        public const string InitialStateDomain = "InitialStateHash";

        /// <summary>历史归档的固定初始种子 H0 = Hash(Encode("HistorySeed", ReplayFormatVersion))。</summary>
        public static ulong HistorySeedDigest() => HistorySeedDigest(Version);

        /// <summary>指定格式版本的初始种子（只用于版本护栏测试与诊断重算）。</summary>
        public static ulong HistorySeedDigest(int formatVersion)
        {
            var encoder = new CanonicalEncoder(32);
            encoder.BeginDomain(HistorySeedDomain);
            encoder.WriteInt32(formatVersion);
            return encoder.ToDigest();
        }

        /// <summary>
        /// 冻结协议描述：把算法、宽度、编码、域标记与类型键拼成稳定文本，
        /// 供格式版本护栏测试逐项断言。文本本身不参与哈希（哈希只写
        /// <see cref="Version"/> 与真实字段），因此它的改动不改变任何摘要。
        /// </summary>
        public static string FrozenProtocolDescription()
        {
            var builder = new StringBuilder();
            builder.Append("version=").Append(Version.ToString(CultureInfo.InvariantCulture)).Append(';');
            builder.Append("hash=").Append(HashAlgorithmId).Append(';');
            builder.Append("digest_hex_width=").Append(DigestHexWidth.ToString(CultureInfo.InvariantCulture)).Append(';');
            builder.Append("integer=").Append(IntegerEncoding).Append(';');
            builder.Append("string=").Append(StringEncoding).Append(';');
            builder.Append("bool=").Append(BoolEncoding).Append(';');
            builder.Append("bytes=").Append(BytesEncoding).Append(';');
            builder.Append("collection=").Append(CollectionEncoding).Append(';');
            builder.Append("domain=").Append(DomainEncoding).Append(';');
            builder.Append("null=").Append(NullReferenceEncoding).Append(';');
            builder.Append("domain_tag=").Append(HistorySeedDomain).Append(',')
                   .Append(HistoryAppendDomain).Append(',')
                   .Append(LogicSnapshotDomain).Append(',')
                   .Append(InitialStateDomain).Append(';');
            foreach (HistoryRecordKind kind in HistoryRecordKinds.All)
            {
                builder.Append("record_kind=").Append(kind.ToString()).Append(':')
                       .Append(((int)kind).ToString(CultureInfo.InvariantCulture)).Append(';');
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// <see cref="HistoryRecordKind"/> 的规范枚举顺序（按整数值升序，只读、固定）。
    /// 归档排序与哈希写入都使用该顺序，绝不依赖反射枚举顺序或容器遍历顺序。
    /// </summary>
    public static class HistoryRecordKinds
    {
        public static readonly IReadOnlyList<HistoryRecordKind> All = new[]
        {
            HistoryRecordKind.ActionPlanTerminal,
            HistoryRecordKind.WindowLedger,
            HistoryRecordKind.ReactionOpportunityBinding,
            HistoryRecordKind.CommandIngressBucket
        };

        public static bool IsValid(HistoryRecordKind kind)
        {
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i] == kind) return true;
            }
            return false;
        }
    }
}
