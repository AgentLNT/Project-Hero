using System;
using System.Collections.Generic;
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
        /// <summary>拥有者或规则显式请求关闭（<c>CloseTurnWindowCommand</c>）。</summary>
        OwnerRequested = 0,

        /// <summary>战斗进入终态：当前窗口以该原因关闭（唯一 Finalizer）。</summary>
        BattleEnded = 1,

        /// <summary>窗口拥有者在本 Tick 打开阶段之前死亡（待打开窗口被按稳定顺序跳过）。</summary>
        OwnerDied = 2,

        /// <summary>该窗口的可用预算不足以为任何普通动作提供最小预留（预算耗尽 ⇒ 停止接受新提交）。</summary>
        BudgetExhausted = 3
    }

    /// <summary>
    /// 窗口关闭原因的稳定文本码（进入事件、拒绝与诊断；不得改名）。
    /// </summary>
    public static class TurnWindowCloseReasonCodes
    {
        public const string OwnerRequested = "WINDOW_CLOSE_OWNER_REQUESTED";
        public const string BattleEnded = "WINDOW_CLOSE_BATTLE_ENDED";
        public const string OwnerDied = "WINDOW_CLOSE_OWNER_DIED";
        public const string BudgetExhausted = "WINDOW_CLOSE_BUDGET_EXHAUSTED";

        public static string CodeOf(TurnWindowCloseReason reason)
        {
            switch (reason)
            {
                case TurnWindowCloseReason.OwnerRequested: return OwnerRequested;
                case TurnWindowCloseReason.BattleEnded: return BattleEnded;
                case TurnWindowCloseReason.OwnerDied: return OwnerDied;
                case TurnWindowCloseReason.BudgetExhausted: return BudgetExhausted;
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

        /// <summary>发行者不能控制它声明的受控单位。</summary>
        public const string ISSUER_CANNOT_CONTROL_UNIT = "WINDOW_ISSUER_CANNOT_CONTROL_UNIT";

        /// <summary>发行者不是窗口拥有者且没有有效并发授权。</summary>
        public const string NO_SUBMISSION_AUTHORITY = "WINDOW_NO_SUBMISSION_AUTHORITY";

        /// <summary>窗口可用预算不足以预留本次新增/增费。</summary>
        public const string INSUFFICIENT_WINDOW_BUDGET = "WINDOW_INSUFFICIENT_BUDGET";

        /// <summary>预算增加必须来自同一个仍为当前且开放的来源窗口。</summary>
        public const string BUDGET_SOURCE_CLOSED_OR_MISMATCH = "WINDOW_BUDGET_SOURCE_CLOSED_OR_MISMATCH";

        /// <summary>预算账本不变式被破坏（Reserved + Spent + Available != Total，或出现负值）。</summary>
        public const string BUDGET_LEDGER_INVARIANT = "WINDOW_BUDGET_LEDGER_INVARIANT";

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

        /// <summary>并发授权不得用于 Block/Dodge（高阶反应走机会 + 肾上腺素，不使用窗口预算）。</summary>
        public const string CONCURRENT_CANNOT_SUBMIT_REACTION = "WINDOW_CONCURRENT_CANNOT_SUBMIT_REACTION";

        /// <summary>关闭并发授权的幂等请求携带了非当前窗口。</summary>
        public const string CONCURRENT_WINDOW_MISMATCH = "WINDOW_CONCURRENT_WINDOW_MISMATCH";
    }

    /// <summary>
    /// 一条按 <c>ActionPlanId</c> 归属的预算预留明细（窗口<strong>只</strong>保存这份可审计账本，
    /// 不保存计划、Intent 或 Reservation 集合）。
    /// </summary>
    public sealed record TurnWindowReservation(ActionPlanId ActionPlanId, int ReservedTicks);

    /// <summary>
    /// 一次预算账本变化的稳定来源（进入 <see cref="TurnBudgetChangedEvent"/>；
    /// 用于区分显式 <c>ScheduleEdit</c> 与系统自动延期）。
    /// </summary>
    public enum TurnBudgetChangeKind
    {
        /// <summary>Editable 普通计划首次创建时把整数预算从 Available 转入 Reserved。</summary>
        Reserved = 0,

        /// <summary>排程编辑（Add/Move 依赖重算）导致的预留差额调整。</summary>
        ReservationAdjusted = 1,

        /// <summary>Startable 原子启动提交：<c>Reserved -&gt; Spent</c>。</summary>
        ConsumedAtLock = 2,

        /// <summary>锁定前终态释放未消费预留（含排程删除、系统延期终止、战斗结束清理）。</summary>
        ReleasedBeforeLock = 3,

        /// <summary>战斗结束 Finalizer 清空账本（不形成可继续使用的退款额度）。</summary>
        BattleEndCleared = 4
    }

    /// <summary>
    /// <strong>一个回合窗口</strong>（主方案 3.2.1）。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>窗口<strong>只</strong>控制"谁能提交新普通动作、还能预留多少整数 Tick 预算、何时切换授权"；
    /// 它<strong>不拥有</strong>动作、Intent 或空间 Reservation，也不是动作执行或结算边界
    /// （00 号规则 2）。因此本类型里<strong>没有</strong>、也不允许出现计划集合。</item>
    /// <item>预算恒等式：<c>Available = Total - Reserved - Spent</c>，三项都<strong>只</strong>通过
    /// 本类型的事务方法变化；任何时刻 <c>Reserved</c>/<c>Spent</c> 都不得为负。</item>
    /// <item><see cref="RequestClose"/> 只把窗口设为<strong>不再接受新增/预算增加</strong>；
    /// 正式关闭（<c>IsOpen = false</c>）由管理器在 Tick 末完成。关闭<strong>不</strong>查询、移动、
    /// 锁定、取消或结算任何计划，也不清零肾上腺素。</item>
    /// <item>关闭后的账本<strong>继续保留</strong>（可审计），直到全部 Editable 预留锁定或释放；
    /// 释放额<strong>不会</strong>重新打开提交权限，也不会转移给其他窗口。</item>
    /// </list>
    /// </summary>
    public sealed class TurnWindow
    {
        private readonly Dictionary<long, int> _reservations = new Dictionary<long, int>();
        private readonly List<ActionPlanId> _orderedPlanIds = new List<ActionPlanId>();
        private TurnWindowSnapshot _cachedSnapshot;
        internal TurnWindowSnapshot BuildSnapshot()
        {
            if (_cachedSnapshot != null && _cachedSnapshot.ClosedAtTick == ClosedAtTick) return _cachedSnapshot;
            var reservations = Reservations;
            var rows = new TurnWindowReservationSnapshot[reservations.Count];
            for (int i = 0; i < rows.Length; i++) rows[i] = new TurnWindowReservationSnapshot(reservations[i].ActionPlanId.Value, reservations[i].ReservedTicks);
            return _cachedSnapshot = new TurnWindowSnapshot(WindowId.Value, OwnerUnitId.Value, OpenedAtTick,
                TotalBudgetTicks, ReservedBudgetTicks, SpentBudgetTicks, AvailableBudgetTicks, IsOpen,
                IsAcceptingSubmissions, (int)(CloseReason ?? TurnWindowCloseReason.OwnerRequested), Array.AsReadOnly(rows), ClosedAtTick);
        }
        internal Action<TurnWindow> LedgerChanged { get; set; }
        public bool IsFrozenForHistory { get; private set; }
        public long ClosedAtTick { get; internal set; } = -1;
        internal void FreezeForHistory()
        {
            if (IsOpen || ReservationCount != 0 || ReservedBudgetTicks != 0)
                throw new LogicDefinitionException(TurnWindowCodes.BUDGET_LEDGER_INVARIANT, "mutable window cannot freeze");
            IsFrozenForHistory = true;
        }
        private void RequireMutable()
        {
            if (IsFrozenForHistory) throw new LogicDefinitionException(TurnWindowCodes.BUDGET_LEDGER_INVARIANT, "frozen window cannot change");
        }

        public TurnWindow(WindowId windowId, UnitId ownerUnitId, long openedAtTick, int totalBudgetTicks)
        {
            if (totalBudgetTicks < 0)
                throw new LogicDefinitionException(TurnWindowCodes.BUDGET_LEDGER_INVARIANT,
                    "total=" + totalBudgetTicks.ToString(CultureInfo.InvariantCulture));
            WindowId = windowId;
            OwnerUnitId = ownerUnitId;
            OpenedAtTick = openedAtTick;
            TotalBudgetTicks = totalBudgetTicks;
            IsOpen = true;
            IsAcceptingSubmissions = true;
            CloseReason = null;
        }

        public WindowId WindowId { get; }

        public UnitId OwnerUnitId { get; }

        public long OpenedAtTick { get; }

        public int TotalBudgetTicks { get; }

        public int ReservedBudgetTicks { get; private set; }

        public int SpentBudgetTicks { get; private set; }

        public int AvailableBudgetTicks => TotalBudgetTicks - ReservedBudgetTicks - SpentBudgetTicks;

        /// <summary>正式关闭后为 false（Tick 末由管理器写入）。</summary>
        public bool IsOpen { get; private set; }

        /// <summary>请求关闭后立即为 false：本 Tick 后续命令稳定拒绝。</summary>
        public bool IsAcceptingSubmissions { get; private set; }

        /// <summary>关闭原因；尚未请求关闭时为 null。</summary>
        public TurnWindowCloseReason? CloseReason { get; private set; }

        /// <summary>按 <c>ActionPlanId</c> 升序的预留明细（不可变深拷贝，用于审计与快照）。</summary>
        public IReadOnlyList<TurnWindowReservation> Reservations
        {
            get
            {
                var ordered = new List<ActionPlanId>(_orderedPlanIds);
                ordered.Sort((a, b) => a.Value.CompareTo(b.Value));
                var result = new List<TurnWindowReservation>(ordered.Count);
                for (int i = 0; i < ordered.Count; i++)
                {
                    result.Add(new TurnWindowReservation(ordered[i], _reservations[ordered[i].Value]));
                }
                return result;
            }
        }

        public int ReservationCount => _reservations.Count;

        /// <summary>是否仍可为 <paramref name="cost"/> 预留（正成本 + 窗口开放 + 仍接受提交 + 可用额度足够）。</summary>
        public bool CanReserve(int cost)
            => IsOpen && IsAcceptingSubmissions && cost > 0 && cost <= AvailableBudgetTicks;

        /// <summary>该计划当前占用的预留额（0 = 没有预留）。</summary>
        public int ReservedFor(ActionPlanId planId)
            => _reservations.TryGetValue(planId.Value, out int amount) ? amount : 0;

        public bool HasReservation(ActionPlanId planId) => _reservations.ContainsKey(planId.Value);

        /// <summary>
        /// Editable 普通计划首次进入排程时把整数预算从 Available 转入本计划的 Reserved。
        /// 同一计划重复预留以稳定码失败（不静默覆盖，也不产生第二次预留）。
        /// </summary>
        internal void ReserveForEditablePlan(ActionPlanId planId, int cost)
        {
            RequireMutable();
            if (!planId.IsValid)
                throw new LogicDefinitionException(TurnWindowCodes.BUDGET_LEDGER_INVARIANT, "planId invalid");
            if (_reservations.ContainsKey(planId.Value))
                throw new LogicDefinitionException(TurnWindowCodes.BUDGET_LEDGER_INVARIANT,
                    "duplicate reservation for plan " + planId.Value.ToString(CultureInfo.InvariantCulture));
            if (!CanReserve(cost))
                throw new LogicDefinitionException(TurnWindowCodes.INSUFFICIENT_WINDOW_BUDGET,
                    "cost=" + cost.ToString(CultureInfo.InvariantCulture) +
                    " available=" + AvailableBudgetTicks.ToString(CultureInfo.InvariantCulture));
            _reservations[planId.Value] = cost;
            _orderedPlanIds.Add(planId);
            ReservedBudgetTicks += cost;
            CheckInvariant();
        }

        /// <summary>
        /// 按差额调整同一计划的预留（成本下降释放、成本上升追加）。
        /// 追加部分要求窗口仍为开放且仍接受提交（<c>delta &gt; 0</c>）；
        /// 释放部分对已关闭窗口同样合法（只更新历史账本，不重开提交权限）。
        /// </summary>
        internal void AdjustReservation(ActionPlanId planId, int delta)
        {
            if (delta == 0) return;
            RequireMutable();
            if (!_reservations.TryGetValue(planId.Value, out int current))
                throw new LogicDefinitionException(TurnWindowCodes.BUDGET_LEDGER_INVARIANT,
                    "no reservation for plan " + planId.Value.ToString(CultureInfo.InvariantCulture));

            int updated = current + delta;
            if (updated < 0)
                throw new LogicDefinitionException(TurnWindowCodes.BUDGET_LEDGER_INVARIANT,
                    "negative reservation for plan " + planId.Value.ToString(CultureInfo.InvariantCulture));
            if (delta > 0 && !CanReserve(delta))
                throw new LogicDefinitionException(TurnWindowCodes.INSUFFICIENT_WINDOW_BUDGET,
                    "delta=" + delta.ToString(CultureInfo.InvariantCulture) +
                    " available=" + AvailableBudgetTicks.ToString(CultureInfo.InvariantCulture));

            if (updated == 0)
            {
                _reservations.Remove(planId.Value);
                _orderedPlanIds.Remove(planId);
            }
            else
            {
                _reservations[planId.Value] = updated;
            }
            ReservedBudgetTicks += delta;
            CheckInvariant();
        }

        /// <summary>
        /// Startable 原子启动提交：把该计划的预留从 <c>Reserved</c> 转为 <c>Spent</c>。
        /// 它<strong>不</strong>二次收费：费用恰好来自该计划已经持有的预留。
        /// </summary>
        internal void ConsumeReservationAtLock(ActionPlanId planId, int cost)
        {
            if (cost != 0) RequireMutable();
            if (cost < 0)
                throw new LogicDefinitionException(TurnWindowCodes.BUDGET_LEDGER_INVARIANT,
                    "cost=" + cost.ToString(CultureInfo.InvariantCulture));
            if (cost == 0)
            {
                // 零成本计划（首版无此形态）不产生账本变化，但仍必须不存在悬空预留。
                if (_reservations.ContainsKey(planId.Value))
                    throw new LogicDefinitionException(TurnWindowCodes.BUDGET_LEDGER_INVARIANT,
                        "zero-cost plan holds a reservation: " + planId.Value.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (!_reservations.TryGetValue(planId.Value, out int current) || current != cost)
                throw new LogicDefinitionException(TurnWindowCodes.BUDGET_LEDGER_INVARIANT,
                    "reservation mismatch for plan " + planId.Value.ToString(CultureInfo.InvariantCulture) +
                    " held=" + current.ToString(CultureInfo.InvariantCulture) +
                    " cost=" + cost.ToString(CultureInfo.InvariantCulture));

            _reservations.Remove(planId.Value);
            _orderedPlanIds.Remove(planId);
            ReservedBudgetTicks -= cost;
            SpentBudgetTicks += cost;
            CheckInvariant();
        }

        /// <summary>
        /// 释放该计划尚未消费的预留（Editable 终态、排程删除、系统延期终止、强制位移失效）。
        /// 幂等：不存在预留时为无操作并返回 0。
        /// </summary>
        internal int ReleaseEditableReservation(ActionPlanId planId)
        {
            if (!_reservations.TryGetValue(planId.Value, out int current)) return 0;
            _reservations.Remove(planId.Value);
            _orderedPlanIds.Remove(planId);
            ReservedBudgetTicks -= current;
            CheckInvariant();
            return current;
        }

        /// <summary>
        /// 战斗结束清理：清空全部<strong>未消费</strong>预留（返回释放总额）。
        ///
        /// 它<strong>不</strong>改动 <c>Spent</c>：Locked/Running 已经消费的时间预算在战斗结束时同样
        /// <strong>不退</strong>（任务包「原子性与关闭语义」与「必须产出」9、13）。释放额也不会变成
        /// 可继续使用的额度——窗口此时已经永久关闭且提交权限已经撤销。
        /// 恒等式 <c>Reserved + Spent + Available == Total</c> 继续成立。
        /// </summary>
        internal int ClearReservationsForBattleEnd()
        {
            if (ReservedBudgetTicks == 0) return 0;
            RequireMutable();
            int released = ReservedBudgetTicks;
            _reservations.Clear();
            _orderedPlanIds.Clear();
            ReservedBudgetTicks = 0;
            CheckInvariant();
            return released;
        }

        /// <summary>
        /// 立即停止接受新增/预算增加（本 Tick 后续命令稳定拒绝），并记录关闭原因。
        /// 已请求关闭的窗口重复请求是被忽略的幂等无操作（<strong>第一次原因胜出</strong>）。
        /// </summary>
        public void RequestClose(TurnWindowCloseReason reason)
        {
            if (!IsOpen || (!IsAcceptingSubmissions && reason != TurnWindowCloseReason.BattleEnded)) return;
            IsAcceptingSubmissions = false;
            CloseReason = reason;
            _cachedSnapshot = null;
        }

        /// <summary>Tick 末正式关闭。它只翻转窗口自身的两个布尔位，不触碰任何计划或账本明细。</summary>
        internal void FinalizeClose()
        {
            if (!IsOpen) return;
            IsOpen = false;
            IsAcceptingSubmissions = false;
            _cachedSnapshot = null;
        }

        private void CheckInvariant()
        {
            _cachedSnapshot = null;
            if (ReservedBudgetTicks < 0 || SpentBudgetTicks < 0 || AvailableBudgetTicks < 0)
                throw new LogicDefinitionException(TurnWindowCodes.BUDGET_LEDGER_INVARIANT,
                    "reserved=" + ReservedBudgetTicks.ToString(CultureInfo.InvariantCulture) +
                    " spent=" + SpentBudgetTicks.ToString(CultureInfo.InvariantCulture) +
                    " available=" + AvailableBudgetTicks.ToString(CultureInfo.InvariantCulture));
            LedgerChanged?.Invoke(this);
        }
    }
}
