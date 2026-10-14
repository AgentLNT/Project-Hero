using System;
using System.Globalization;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Turns
{
    /// <summary>
    /// 窗口关闭原因（任务 07 冻结；数值参与快照哈希，<strong>不得重排或复用</strong>）。
    /// </summary>
    public enum TurnWindowCloseReason
    {
        /// <summary>
        /// <strong>已废止（R1）</strong>：主动结束窗口的命令已删除，运行时不再产生该原因。
        /// 数值保留、不得复用。
        /// </summary>
        OwnerRequested = 0,

        /// <summary>战斗进入终态：当前窗口以该原因关闭（唯一 Finalizer）。</summary>
        BattleEnded = 1,

        /// <summary>窗口拥有者死亡：本 Tick 停止接受提交，Tick 末正式关闭。</summary>
        OwnerDied = 2,

        /// <summary>Retired R1 reason. Numeric value 3 is reserved and never produced.</summary>
        BudgetExhausted = 3,

        /// <summary>窗口打开时长到期：在 <c>EndTick - 1</c> 的 Tick 末关闭（R1）。</summary>
        Expired = 4
    }

    /// <summary>
    /// 窗口关闭原因的稳定文本码（进入事件、拒绝与诊断；不得改名）。
    /// </summary>
    public static class TurnWindowCloseReasonCodes
    {
        public const string BattleEnded = "WINDOW_CLOSE_BATTLE_ENDED";
        public const string OwnerDied = "WINDOW_CLOSE_OWNER_DIED";
        public const string Expired = "WINDOW_CLOSE_EXPIRED";

        public static string CodeOf(TurnWindowCloseReason reason)
        {
            switch (reason)
            {
                case TurnWindowCloseReason.BattleEnded: return BattleEnded;
                case TurnWindowCloseReason.OwnerDied: return OwnerDied;
                case TurnWindowCloseReason.Expired: return Expired;
                default:
                    throw new LogicDefinitionException(
                        TurnWindowCodes.WINDOW_CLOSE_REASON_UNKNOWN,
                        ((int)reason).ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// 稳定拒绝/错误码（进入命令拒绝事件、诊断与验收；<strong>不得改名</strong>）。
    /// </summary>
    public static class TurnWindowCodes
    {
        /// <summary>当前没有打开且仍在接受提交的窗口。</summary>
        public const string NO_OPEN_WINDOW = "WINDOW_NO_OPEN_WINDOW";

        /// <summary>命令携带的 <c>ExpectedWindowId</c> 不是当前开放窗口（过期或已关闭）。</summary>
        public const string STALE_OR_CLOSED_WINDOW = "WINDOW_STALE_OR_CLOSED";

        /// <summary>新增普通计划的命令没有声明 <c>ExpectedWindowId</c>（00 号规则 18：窗口必填）。</summary>
        public const string EXPECTED_WINDOW_REQUIRED = "WINDOW_EXPECTED_WINDOW_REQUIRED";

        /// <summary>发行者不能控制它声明的受控单位。</summary>
        public const string ISSUER_CANNOT_CONTROL_UNIT = "WINDOW_ISSUER_CANNOT_CONTROL_UNIT";

        /// <summary>发行者不是窗口拥有者且没有有效并发授权。</summary>
        public const string NO_SUBMISSION_AUTHORITY = "WINDOW_NO_SUBMISSION_AUTHORITY";

        /// <summary>窗口结构不变式被破坏（时长非正、关闭时仍有来源于它的 Editable 普通计划等）。</summary>
        public const string WINDOW_INVARIANT = "WINDOW_INVARIANT";

        /// <summary>窗口不能被主动结束（R1）：只因到期、拥有者死亡或战斗结束而关闭。</summary>
        public const string WINDOW_CLOSE_NOT_PERMITTED = "WINDOW_CLOSE_NOT_PERMITTED";

        /// <summary>关闭原因未知。</summary>
        public const string WINDOW_CLOSE_REASON_UNKNOWN = "WINDOW_CLOSE_REASON_UNKNOWN";

        /// <summary>并发动作能力未在权威定义中配置（fail-closed，不猜费用）。</summary>
        public const string CONCURRENT_ABILITY_NOT_CONFIGURED = "WINDOW_CONCURRENT_ABILITY_NOT_CONFIGURED";

        /// <summary>同一个窗口已经激活过并发行动。</summary>
        public const string ALREADY_ACTIVE = "WINDOW_CONCURRENT_ALREADY_ACTIVE";

        /// <summary>并发行动的受控单位不是可激活的主角单位。</summary>
        public const string INVALID_CONCURRENT_ACTOR = "WINDOW_CONCURRENT_INVALID_ACTOR";

        /// <summary>窗口拥有者就是激活者本人（自身窗口天然持有提交权，不需要并发授权）。</summary>
        public const string PLAYER_OWNS_WINDOW = "WINDOW_CONCURRENT_PLAYER_OWNS_WINDOW";

        /// <summary>局外资源不足。</summary>
        public const string INSUFFICIENT_META_RESOURCE = "WINDOW_INSUFFICIENT_META_RESOURCE";

        /// <summary>并发授权不得用于 Block/Dodge（高阶反应走机会 + 肾上腺素，不受窗口区间约束）。</summary>
        public const string CONCURRENT_CANNOT_SUBMIT_REACTION = "WINDOW_CONCURRENT_CANNOT_SUBMIT_REACTION";

        /// <summary>关闭并发授权的幂等请求携带了非当前窗口。</summary>
        public const string CONCURRENT_WINDOW_MISMATCH = "WINDOW_CONCURRENT_WINDOW_MISMATCH";
    }

    public sealed class TurnWindow
    {
        private TurnWindowSnapshot _cachedSnapshot;

        internal TurnWindowSnapshot BuildSnapshot()
        {
            if (_cachedSnapshot != null && _cachedSnapshot.ClosedAtTick == ClosedAtTick) return _cachedSnapshot;
            return _cachedSnapshot = new TurnWindowSnapshot(WindowId.Value, OwnerUnitId.Value, OpenedAtTick,
                DurationTicks, EndTick, IsOpen, IsAcceptingSubmissions,
                (int)(CloseReason ?? TurnWindowCloseReason.Expired), ClosedAtTick);
        }

        public bool IsFrozenForHistory { get; private set; }

        public long ClosedAtTick { get; internal set; } = -1;

        internal void FreezeForHistory()
        {
            if (IsOpen)
                throw new LogicDefinitionException(TurnWindowCodes.WINDOW_INVARIANT, "open window cannot freeze");
            IsFrozenForHistory = true;
        }

        public TurnWindow(WindowId windowId, UnitId ownerUnitId, long openedAtTick, int durationTicks)
        {
            if (durationTicks <= 0)
                throw new LogicDefinitionException(TurnWindowCodes.WINDOW_INVARIANT,
                    "duration=" + durationTicks.ToString(CultureInfo.InvariantCulture));
            WindowId = windowId;
            OwnerUnitId = ownerUnitId;
            OpenedAtTick = openedAtTick;
            DurationTicks = durationTicks;
            EndTick = checked(openedAtTick + durationTicks);
            IsOpen = true;
            IsAcceptingSubmissions = true;
            CloseReason = null;
        }

        public WindowId WindowId { get; }

        public UnitId OwnerUnitId { get; }

        public long OpenedAtTick { get; }

        /// <summary>打开时长（Tick，&gt; 0）。</summary>
        public int DurationTicks { get; }

        /// <summary>区间终点（不含）：普通计划的 <c>StartTick</c> 必须严格小于它。</summary>
        public long EndTick { get; }

        /// <summary>该 Tick 是否落在窗口区间 <c>[OpenedAtTick, EndTick)</c> 内。</summary>
        public bool Contains(long tick) => tick >= OpenedAtTick && tick < EndTick;

        /// <summary>正式关闭后为 false（Tick 末由管理器写入）。</summary>
        public bool IsOpen { get; private set; }

        /// <summary>请求关闭后立即为 false：本 Tick 后续命令稳定拒绝。</summary>
        public bool IsAcceptingSubmissions { get; private set; }

        /// <summary>关闭原因；尚未请求关闭时为 null。</summary>
        public TurnWindowCloseReason? CloseReason { get; private set; }

        /// <summary>
        /// 立即停止接受新增（本 Tick 后续命令稳定拒绝），并记录关闭原因。
        /// 已请求关闭的窗口重复请求是被忽略的幂等无操作（<strong>第一次原因胜出</strong>）。
        /// </summary>
        public void RequestClose(TurnWindowCloseReason reason)
        {
            if (reason != TurnWindowCloseReason.Expired && reason != TurnWindowCloseReason.OwnerDied
                && reason != TurnWindowCloseReason.BattleEnded)
                throw new LogicDefinitionException(TurnWindowCodes.WINDOW_CLOSE_NOT_PERMITTED);
            if (!IsOpen || (!IsAcceptingSubmissions && reason != TurnWindowCloseReason.BattleEnded)) return;
            IsAcceptingSubmissions = false;
            CloseReason = reason;
            _cachedSnapshot = null;
        }

        /// <summary>Tick 末正式关闭。它只翻转窗口自身的两个布尔位，不触碰任何计划。</summary>
        internal void FinalizeClose()
        {
            if (!IsOpen) return;
            IsOpen = false;
            IsAcceptingSubmissions = false;
            _cachedSnapshot = null;
        }
    }
}
