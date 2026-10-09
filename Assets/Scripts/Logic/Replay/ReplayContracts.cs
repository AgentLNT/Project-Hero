using System.Collections.Generic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;

namespace ProjectHero.Logic.Replay
{
    /// <summary>回放数据契约的稳定失败码（任务 03 冻结；播放器由任务 11 实现）。</summary>
    public static class ReplayCodes
    {
        public const string REPLAY_FORMAT_VERSION_MISMATCH = "REPLAY_FORMAT_VERSION_MISMATCH";
        public const string REPLAY_RULES_VERSION_MISMATCH = "REPLAY_RULES_VERSION_MISMATCH";
        public const string REPLAY_DEFINITION_HASH_MISMATCH = "REPLAY_DEFINITION_HASH_MISMATCH";
        public const string REPLAY_TICKS_PER_SECOND_MISMATCH = "REPLAY_TICKS_PER_SECOND_MISMATCH";
        public const string REPLAY_ENCOUNTER_NOT_FOUND = "REPLAY_ENCOUNTER_NOT_FOUND";
        public const string REPLAY_INITIAL_STATE_HASH_MISMATCH = "REPLAY_INITIAL_STATE_HASH_MISMATCH";
        public const string REPLAY_HEADER_NULL = "REPLAY_HEADER_NULL";
    }

    /// <summary>
    /// 回放头部（主方案 3.11 冻结）：只承载"重建这一场模拟所需的全部输入"。
    ///
    /// <see cref="ReplayFormatVersion"/> 与 <see cref="RulesVersion"/> 是两件不同的事：
    /// 前者版本化文件结构/字段语义/哈希编码（<see cref="ReplayFormat"/>），
    /// 后者版本化玩法规则（<c>BattleDefinition.RulesVersion</c>）。改变其中一个
    /// <strong>不得</strong>被当成改变另一个，也<strong>不得</strong>宣称跨规则版本得到相同回放结果。
    ///
    /// <see cref="InitialStateHash"/> 是<strong>首个 Step 之前</strong>的初始规范化哈希：
    /// 回放必须先重建模拟、验证它与头部一致，再从 Tick 0 开始注入记录输入。
    /// </summary>
    public sealed record ReplayHeader(
        int ReplayFormatVersion,
        string RulesVersion,
        string BattleDefinitionHash,
        int TicksPerSecond,
        EncounterDefinitionId EncounterId,
        BattleRuntimeInputs RuntimeInputs,
        ulong InitialStateHash);

    /// <summary>
    /// 已由可信入口绑定、<strong>尚未</strong>由网关分配全局序号的命令事实。
    ///
    /// 首版权威回放输入只接受 <c>Player</c> 来源：AI/System 请求必须由逻辑重建
    /// （通过快照哈希与事件序列校验），若被记录也只能作为诊断轨迹，
    /// 不得再次注入而造成双重执行。序列化 DTO 不是运行时授权。
    /// </summary>
    public sealed record RecordedCommandRequest(
        ControllerId Issuer,
        CommandSourceKind SourceKind,
        long ProducerOrdinal,
        CommandRequest Request)
    {
        public bool IsAuthoritativeReplayInput => SourceKind == CommandSourceKind.Player;
    }

    /// <summary>逐 Tick 回放记录：原始提交 Tick、被记录的可信玩家请求、该 Tick 事件与快照哈希。</summary>
    public sealed record ReplayTickRecord(
        long Tick,
        IReadOnlyList<RecordedCommandRequest> ReplayInputRequests,
        IReadOnlyList<LogicEvent> Events,
        ulong SnapshotHash);

    /// <summary>Accepted Player fact at the completed-Tick boundary; -1 is before Step 0.</summary>
    public sealed record ReplaySubmission(long SubmittedAtTick, RecordedCommandRequest Fact);

    /// <summary>
    /// 一份从 Tick 0 重演的开发者回放数据（主方案 3.11）。任务 03 只冻结数据边界；
    /// 录制管理、播放控制与产品 UI 属于任务 11。
    /// </summary>
    public sealed class BattleReplay
    {
        public BattleReplay(ReplayHeader header, IReadOnlyList<ReplayTickRecord> records,
            IReadOnlyList<ReplaySubmission> submissions = null)
        {
            Header = header;
            var copied = new List<ReplayTickRecord>();
            if (records != null)
                foreach (var record in records)
                    copied.Add(record == null ? null : record with {
                        Events = Freeze(record.Events), ReplayInputRequests = Freeze(record.ReplayInputRequests) });
            Records = copied.AsReadOnly();
            Submissions = Freeze(submissions);
        }

        private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values)
        {
            if (values == null) return System.Array.Empty<T>();
            var copy = new T[values.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = values[i];
            return System.Array.AsReadOnly(copy);
        }

        public ReplayHeader Header { get; }

        /// <summary>按 Tick 升序（包含空 Tick）。Tick 0 表示首个 Step 之前的初始状态。</summary>
        public IReadOnlyList<ReplayTickRecord> Records { get; }
        public IReadOnlyList<ReplaySubmission> Submissions { get; }
    }

    /// <summary>
    /// 回放头部校验（不是播放器）：在重建模拟<strong>之前</strong>把版本/规则/定义/Encounter
    /// 的不匹配稳定拒绝，绝不"尽力兼容"。
    /// </summary>
    public static class ReplayHeaderValidation
    {
        /// <summary>返回首个失败码（null = 通过）。</summary>
        public static string Validate(ReplayHeader header, BattleDefinition definition)
        {
            if (header == null) return ReplayCodes.REPLAY_HEADER_NULL;
            if (header.ReplayFormatVersion != ReplayFormat.Version)
                return ReplayCodes.REPLAY_FORMAT_VERSION_MISMATCH;
            if (definition == null) return ReplayCodes.REPLAY_HEADER_NULL;

            if (!string.Equals(header.RulesVersion, definition.RulesVersion, System.StringComparison.Ordinal))
                return ReplayCodes.REPLAY_RULES_VERSION_MISMATCH;

            if (!string.Equals(header.BattleDefinitionHash, definition.BattleDefinitionHashValue, System.StringComparison.Ordinal))
                return ReplayCodes.REPLAY_DEFINITION_HASH_MISMATCH;

            if (header.TicksPerSecond != definition.TicksPerSecond)
                return ReplayCodes.REPLAY_TICKS_PER_SECOND_MISMATCH;

            if (definition.FindEncounter(header.EncounterId) == null)
                return ReplayCodes.REPLAY_ENCOUNTER_NOT_FOUND;

            return null;
        }

        /// <summary>重建模拟后验证"首个 Step 之前"的初始哈希（不一致即拒绝重演）。</summary>
        public static string ValidateInitialState(ReplayHeader header, ulong rebuiltInitialStateHash)
            => header != null && header.InitialStateHash == rebuiltInitialStateHash
                ? null
                : ReplayCodes.REPLAY_INITIAL_STATE_HASH_MISMATCH;
    }
}
