using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Turns
{
    /// <summary>一份只读的窗口排程请求（待打开窗口；拥有者死亡时按稳定顺序跳过）。</summary>
    public sealed record ScheduledTurnWindowRequest(UnitId OwnerUnitId, int BudgetTicks);

    /// <summary>
    /// 打开窗口所需的只读世界事实（由模拟装配方提供，管理器自身不持有单位状态）。
    /// </summary>
    public interface ITurnWindowWorldView
    {
        /// <summary>窗口拥有者是否仍然存活（待打开窗口在打开阶段前死亡 ⇒ 跳过，不发事件、不产生预算）。</summary>
        bool IsUnitAliveForWindow(UnitId unitId);

        /// <summary>战斗是否已经结束（结束后不得再打开任何窗口，也不得再排定下一窗口）。</summary>
        bool IsBattleEnded { get; }
    }

    /// <summary>
    /// 窗口排程的只读来源（任务 03 的 <c>ITurnWindowSchedule</c> 端口）。
    ///
    /// 它只回答"本 Tick 是否到期打开一个窗口""是否请求关闭当前窗口"；
    /// 窗口<strong>不</strong>因此拥有任何计划。
    /// </summary>
    public interface ITurnWindowSchedule
    {
        /// <summary>本 Tick 应打开的窗口；null 表示没有。</summary>
        WindowOpenRequest TryOpenDue(long tick);

        /// <summary>本 Tick 是否正式请求关闭当前窗口。</summary>
        bool ShouldCloseCurrentWindow(long tick);
    }

    /// <summary>默认窗口排程：永不打开、永不关闭（测试与显式脚本窗口装配沿用）。</summary>
    public sealed class NoTurnWindowSchedule : ITurnWindowSchedule
    {
        public static readonly NoTurnWindowSchedule Instance = new NoTurnWindowSchedule();

        public WindowOpenRequest TryOpenDue(long tick) => null;

        public bool ShouldCloseCurrentWindow(long tick) => false;
    }

    /// <summary>
    /// <strong>唯一 <c>TurnWindowManager</c></strong>（主方案 3.2.1；任务包「必须产出」1–2）。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>窗口顺序键 = <c>PlayerFirst -&gt; ActionSpeed 降序 -&gt; UnitId</c>（稳定；不得依赖字典枚举）。
    /// 首版每 Tick 至多打开一个到期窗口，下一个窗口最早在<strong>下一 Tick</strong> 打开。</item>
    /// <item>打开窗口时<strong>先</strong>递增拥有者个人周期并清零上个周期遗留的 Available 肾上腺素，
    /// 然后才创建 <see cref="WindowId"/> 与预算；顺序先于本 Tick 新机会与命令处理。</item>
    /// <item>窗口切换<strong>不</strong>查询、取消、截断、加速、锁定、结算或重排任何 <c>ActionPlan</c>，
    /// 也不增加 <c>ScheduleRevision</c>；它只翻转提交权限与账本状态。</item>
    /// <item>关闭只撤销属于该窗口的并发提交授权并发射一个 <c>TurnWindowClosedEvent</c>；
    /// 已关闭窗口的账本继续保留到其 Editable 预留全部锁定或释放。</item>
    /// <item>战斗结束后不再打开新窗口；Finalizer 关闭当前窗口、清空未来排程、把全部活动窗口账本
    /// 归零（不产生可继续使用的退款额度）。</item>
    /// </list>
    /// </summary>
    public sealed class TurnWindowManager
    {
        private readonly ITurnWindowWorldView _world;
        private readonly Func<WindowId> _allocateWindowId;

        /// <summary>已关闭窗口的账本历史（按 <c>WindowId</c> 升序；关闭后仍然可审计）。</summary>
        private readonly List<TurnWindow> _closedWindows = new List<TurnWindow>();

        /// <summary>未来 Tick 的窗口排程桶（按 Tick 升序；同 Tick 内按稳定窗口键）。</summary>
        private readonly SortedDictionary<long, List<ScheduledTurnWindowRequest>> _futureSchedule =
            new SortedDictionary<long, List<ScheduledTurnWindowRequest>>();

        private long _nextWindowOrdinal;

        /// <param name="allocateWindowId">
        /// 窗口 ID 分配器（由模拟方接到它自己的 <c>LogicIdGenerator</c>，使"下一个窗口 ID"
        /// 与全局实例 ID 计数保持<strong>单一事实来源</strong>）。为 null 时使用本管理器内部的
        /// 单调序号（从 1 开始，同样进入快照与哈希）。
        /// </param>
        public TurnWindowManager(ITurnWindowWorldView world, Func<WindowId> allocateWindowId = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _allocateWindowId = allocateWindowId;
        }

        /// <summary>当前打开或刚请求关闭的窗口；<c>null</c> = 当前没有窗口。</summary>
        public TurnWindow CurrentWindow { get; private set; }

        /// <summary>最近一次正式关闭的窗口 ID（0 = 尚无）。</summary>
        public long LastClosedWindowId { get; private set; }

        /// <summary>
        /// 最近一次正式关闭发生的 Tick（-1 = 尚无）。
        ///
        /// 它是"新窗口最早在<strong>下一 Tick</strong> 打开"这条规则的执行点：
        /// 窗口在 Tick T 末正式关闭后，Tick T 内不可能再打开任何窗口
        /// （哪怕排程把它们排在了同一个 Tick）。
        /// </summary>
        public long LastClosedAtTick { get; private set; } = -1L;

        /// <summary>本场战斗已经创建过的窗口数（= "下一个窗口 ID" 的配套审计值，进入快照）。</summary>
        public long NextWindowOrdinal => _nextWindowOrdinal;

        /// <summary>下一个窗口的最早打开 Tick（-1 = 未排定）。</summary>
        public long NextWindowTick { get; private set; } = -1L;

        /// <summary>已关闭窗口账本（只读；按提交关闭顺序，即 Tick 升序）。</summary>
        public IReadOnlyList<TurnWindow> ClosedWindows => _closedWindows;

        /// <summary>当前是否有一个仍在接受新增提交的窗口。</summary>
        public bool IsAcceptingSubmissions
            => CurrentWindow != null && CurrentWindow.IsOpen && CurrentWindow.IsAcceptingSubmissions;

        /// <summary>当前开放窗口（不接受提交时返回 null；用于"必须有开放窗口"的校验）。</summary>
        public TurnWindow OpenWindow => IsAcceptingSubmissions ? CurrentWindow : null;

        /// <summary>
        /// 按 <c>WindowId</c> 查找窗口（含已关闭窗口的历史账本）。
        ///
        /// 关闭后的窗口<strong>必须</strong>仍可被找到：Editable 预留的释放要回到它原来的来源账本，
        /// 而不是"最近的窗口"。找不到返回 null（调用方以稳定码拒绝，绝不回退到当前窗口）。
        /// </summary>
        public TurnWindow FindWindow(WindowId windowId)
        {
            if (CurrentWindow != null && CurrentWindow.WindowId == windowId) return CurrentWindow;
            for (int i = _closedWindows.Count - 1; i >= 0; i--)
            {
                if (_closedWindows[i].WindowId == windowId) return _closedWindows[i];
            }
            return null;
        }

        /// <summary>已经排定但尚未打开的窗口请求总数（诊断与快照）。</summary>
        public int ScheduledWindowCount
        {
            get
            {
                int total = 0;
                foreach (KeyValuePair<long, List<ScheduledTurnWindowRequest>> bucket in _futureSchedule)
                    total += bucket.Value.Count;
                return total;
            }
        }

        /// <summary>请求打开事件（由模拟方接到 Outbox；参数为窗口、拥有者、预算与打开 Tick）。</summary>
        public Action<TurnWindow, long> WindowOpenedSink { get; set; }

        /// <summary>关闭事件（由模拟方接到 Outbox；参数为窗口、关闭原因与关闭 Tick）。</summary>
        public Action<TurnWindow, TurnWindowCloseReason, long> WindowClosedSink { get; set; }

        /// <summary>个人周期递增与 Available 清零端口（由模拟方接到 <c>AdrenalineLedger</c>）。</summary>
        public Action<UnitId, long> OwnerCycleResetSink { get; set; }

        /// <summary>关闭窗口时撤销并发授权的端口（由模拟方接到 <c>ConcurrentActionSystem</c>）。</summary>
        public Action<WindowId, long> WindowClosedAuthoritySink { get; set; }

        /// <summary>只读观察面：<c>Controller</c> → 受控单位（由模拟方在创建时按定义注册一次）。</summary>
        private readonly Dictionary<string, List<UnitId>> _controlledUnitsByController =
            new Dictionary<string, List<UnitId>>(StringComparer.Ordinal);

        /// <summary>
        /// 注册一个控制者绑定（只定义<strong>控制权</strong>；与阵营、胜负、目标资格正交）。
        /// 同一个控制者可以被注册多次（多次调用的并集），每个受控单位只登记一次。
        /// </summary>
        public void RegisterControllerBinding(ControllerId controllerId, UnitId controlledUnitId)
        {
            if (string.IsNullOrEmpty(controllerId.Value) || !controlledUnitId.IsValid) return;
            if (!_controlledUnitsByController.TryGetValue(controllerId.Value, out List<UnitId> units))
            {
                units = new List<UnitId>();
                _controlledUnitsByController[controllerId.Value] = units;
            }
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == controlledUnitId) return;
            }
            units.Add(controlledUnitId);
        }

        /// <summary>
        /// 该发行者是否<strong>控制</strong>该单位（唯一判据 = 定义级 <c>ControllerBinding</c>）。
        /// 它<strong>不</strong>读取 <c>IsPlayerControlled</c>、阵营、名称或窗口归属。
        /// </summary>
        public bool CanControl(ControllerId issuer, UnitId unitId)
            => !string.IsNullOrEmpty(issuer.Value)
               && unitId.IsValid
               && _controlledUnitsByController.TryGetValue(issuer.Value, out List<UnitId> units)
               && ContainsUnit(units, unitId);

        private static bool ContainsUnit(List<UnitId> units, UnitId unitId)
        {
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == unitId) return true;
            }
            return false;
        }

        /// <summary>排定一个未来 Tick 的窗口（同一个 Tick 可以被多次排定；打开时按稳定键取第一个可打开者）。</summary>
        public void ScheduleWindow(long tick, UnitId ownerUnitId, int budgetTicks)
        {
            if (tick < 0L)
                throw new LogicDefinitionException(TurnWindowCodes.BUDGET_LEDGER_INVARIANT,
                    "tick=" + tick.ToString(CultureInfo.InvariantCulture));
            if (!_futureSchedule.TryGetValue(tick, out List<ScheduledTurnWindowRequest> bucket))
            {
                bucket = new List<ScheduledTurnWindowRequest>();
                _futureSchedule[tick] = bucket;
            }
            bucket.Add(new ScheduledTurnWindowRequest(ownerUnitId, budgetTicks));
            RefreshNextWindowTick();
        }

        /// <summary>
        /// 由 <see cref="ITurnWindowSchedule"/> 声明本 Tick 到期窗口（脚本窗口与测试装配的唯一入口）。
        /// 返回值表示是否真的排定了一个待打开窗口。
        /// </summary>
        public bool ScheduleDueWindow(long tick, ITurnWindowSchedule schedule)
        {
            WindowOpenRequest request = schedule?.TryOpenDue(tick);
            if (request == null) return false;
            ScheduleWindow(tick, request.OwnerUnitId, request.BudgetTicks);
            return true;
        }

        /// <summary>
        /// 阶段 3（唯一 Finalizer 判定战斗继续进行之后）：打开本 Tick 到期的窗口。
        ///
        /// 顺序语义（不变量，见任务包「原子性与关闭语义」）：
        /// <list type="number">
        /// <item>战斗已结束 ⇒ 不打开、不排定任何窗口；</item>
        /// <item>拥有者已死亡 ⇒ 按稳定窗口顺序<strong>跳过</strong>，
        /// 不产生该单位的窗口打开事件、不产生预算，也不递增其个人周期；</item>
        /// <item>当前仍有未正式关闭的窗口 ⇒ 本 Tick 不打开新窗口（下一个窗口最早下一 Tick）；</item>
        /// <item>打开顺序 = 先递增拥有者个人周期并清零 Available，再创建窗口与预算。</item>
        /// </list>
        /// </summary>
        public TurnWindow TryOpenDueWindow(long tick)
        {
            if (_world.IsBattleEnded) return null;
            if (CurrentWindow != null && CurrentWindow.IsOpen) return null;
            // 新窗口最早在下一 Tick 打开：同一 Tick 内已关闭过窗口时不得再打开。
            if (tick <= LastClosedAtTick) return null;
            if (!_futureSchedule.TryGetValue(tick, out List<ScheduledTurnWindowRequest> bucket) || bucket.Count == 0)
                return null;

            SortRequests(bucket);
            for (int i = 0; i < bucket.Count; i++)
            {
                ScheduledTurnWindowRequest request = bucket[i];
                if (!_world.IsUnitAliveForWindow(request.OwnerUnitId)) continue;

                bucket.RemoveAt(i);
                if (bucket.Count == 0) _futureSchedule.Remove(tick);
                RefreshNextWindowTick();

                // 顺序契约：先递增个人周期并清零 Available，再创建窗口与预算。
                OwnerCycleResetSink?.Invoke(request.OwnerUnitId, tick);

                _nextWindowOrdinal++;
                var window = new TurnWindow(
                    _allocateWindowId != null ? _allocateWindowId() : new WindowId(_nextWindowOrdinal),
                    request.OwnerUnitId, tick, request.BudgetTicks);
                CurrentWindow = window;
                WindowOpenedSink?.Invoke(window, tick);
                return window;
            }

            // 桶内全部拥有者都已死亡：整个桶被丢弃（不产生任何打开事件或预算）。
            _futureSchedule.Remove(tick);
            RefreshNextWindowTick();
            return null;
        }

        /// <summary>
        /// 请求关闭当前窗口（命令路径）。返回稳定错误码；null = 已请求关闭
        /// （<c>IsAcceptingSubmissions</c> 立即为 false，因此本 Tick 后续命令稳定拒绝）。
        /// </summary>
        public string TryRequestClose(UnitId issuerUnitId, WindowId expectedWindowId, TurnWindowCloseReason reason)
        {
            TurnWindow window = CurrentWindow;
            if (window == null || !window.IsOpen || !window.IsAcceptingSubmissions)
                return TurnWindowCodes.NO_OPEN_WINDOW;
            if (window.WindowId != expectedWindowId)
                return TurnWindowCodes.STALE_OR_CLOSED_WINDOW;
            if (window.OwnerUnitId != issuerUnitId)
                return TurnWindowCodes.NO_SUBMISSION_AUTHORITY;

            window.RequestClose(reason);
            return null;
        }

        /// <summary>
        /// 命令路径的关窗入口：发行者（由命令网关绑定）可以关闭<strong>它自己控制</strong>的单位的窗口。
        ///
        /// 顺序契约：先确认存在仍在接受提交的当前窗口，再比对 <c>ExpectedWindowId</c>
        /// （绝不允许"按当前窗口猜"），最后验证控制权。任一步失败都<strong>零副作用</strong>。
        /// </summary>
        public string TryRequestCloseForIssuer(ControllerId issuer, WindowId expectedWindowId)
        {
            TurnWindow window = CurrentWindow;
            if (window == null || !window.IsOpen || !window.IsAcceptingSubmissions)
                return TurnWindowCodes.NO_OPEN_WINDOW;
            if (window.WindowId != expectedWindowId)
                return TurnWindowCodes.STALE_OR_CLOSED_WINDOW;
            if (!CanControl(issuer, window.OwnerUnitId))
                return TurnWindowCodes.NO_SUBMISSION_AUTHORITY;

            window.RequestClose(TurnWindowCloseReason.OwnerRequested);
            return null;
        }

        /// <summary>
        /// 预算耗尽：把当前窗口（若仍接受提交）停权，并记录 <see cref="TurnWindowCloseReason.BudgetExhausted"/>。
        /// 它<strong>不</strong>触碰任何计划、Lane、Intent 或 Reservation。
        /// </summary>
        public void RequestCloseForBudgetExhaustion()
        {
            TurnWindow window = CurrentWindow;
            if (window == null || !window.IsOpen || !window.IsAcceptingSubmissions) return;
            window.RequestClose(TurnWindowCloseReason.BudgetExhausted);
        }

        /// <summary>拥有者死亡：该窗口立即停止接受提交（Tick 末正式关闭）。</summary>
        public void RequestCloseForOwnerDeath(UnitId unitId)
        {
            TurnWindow window = CurrentWindow;
            if (window == null || !window.IsOpen) return;
            if (window.OwnerUnitId != unitId) return;
            window.RequestClose(TurnWindowCloseReason.OwnerDied);
        }

        /// <summary>
        /// 阶段 17：正式关闭已请求关闭的当前窗口。
        ///
        /// 冻结语义：正式关闭<strong>只</strong>做三件事——撤销属于该窗口的并发授权、翻转窗口位、
        /// 发射关闭事件。它<strong>不</strong>在关闭时结算任何计划或预留，也不"顺手"排定下一窗口：
        /// 下一窗口由排程来源在<strong>下一 Tick</strong> 的打开阶段到期决定
        /// （本方法记录 <see cref="LastClosedAtTick"/>，使同一 Tick 内不可能再打开新窗口）。
        /// </summary>
        public TurnWindow FinalizeRequestedClose(long tick)
        {
            TurnWindow window = CurrentWindow;
            if (window == null || !window.IsOpen) return null;
            if (window.IsAcceptingSubmissions) return null;   // 尚未请求关闭

            TurnWindowCloseReason reason = window.CloseReason ?? TurnWindowCloseReason.OwnerRequested;
            window.FinalizeClose();
            CurrentWindow = null;
            LastClosedWindowId = window.WindowId.Value;
            LastClosedAtTick = tick;
            _closedWindows.Add(window);

            WindowClosedAuthoritySink?.Invoke(window.WindowId, tick);
            WindowClosedSink?.Invoke(window, reason, tick);
            return window;
        }

        /// <summary>
        /// 战斗结束 Finalizer：以 <see cref="TurnWindowCloseReason.BattleEnded"/> 关闭当前窗口、
        /// 撤销属于它的并发授权、清空全部未来窗口排程。
        ///
        /// 它<strong>不</strong>遍历或终止任何 <c>ActionPlan</c>（那是统一终态协调器的职责），
        /// 也<strong>不</strong>在这里结算账本：未消费预留由
        /// <see cref="TurnWindowBudgetAuthority.ClearReservationsForBattleEnd"/> 逐窗口清空
        /// （保留 <c>Spent</c>，且不产生可继续使用的额度），这样事件里才有完整的前后值。
        /// </summary>
        public void CloseAllForBattleEnd(long tick)
        {
            TurnWindow window = CurrentWindow;
            if (window != null)
            {
                if (window.IsOpen)
                {
                    window.RequestClose(TurnWindowCloseReason.BattleEnded);
                    window.FinalizeClose();
                    _closedWindows.Add(window);
                    LastClosedWindowId = window.WindowId.Value;
                    LastClosedAtTick = tick;
                    WindowClosedAuthoritySink?.Invoke(window.WindowId, tick);
                    WindowClosedSink?.Invoke(window, TurnWindowCloseReason.BattleEnded, tick);
                }
                CurrentWindow = null;
            }

            _futureSchedule.Clear();
            NextWindowTick = -1L;
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
            => "current=" + (CurrentWindow == null
                    ? "none"
                    : CurrentWindow.WindowId.Value.ToString(CultureInfo.InvariantCulture))
               + " closed=" + _closedWindows.Count.ToString(CultureInfo.InvariantCulture)
               + " scheduled=" + ScheduledWindowCount.ToString(CultureInfo.InvariantCulture);

        private void RefreshNextWindowTick()
        {
            NextWindowTick = -1L;
            foreach (KeyValuePair<long, List<ScheduledTurnWindowRequest>> bucket in _futureSchedule)
            {
                if (bucket.Value.Count == 0) continue;
                NextWindowTick = bucket.Key;
                return;
            }
        }

        /// <summary>
        /// 稳定窗口顺序：<c>PlayerFirst -&gt; ActionSpeed 降序 -&gt; UnitId</c>。
        /// 首版没有"玩家优先"的阵营信息，因此稳定键退化为 <c>ActionSpeed 降序 -&gt; UnitId</c>，
        /// 并在<strong>显式</strong>给出 PlayerFirst 标记时把它排在最前（不得依赖注册/枚举顺序）。
        /// </summary>
        private static void SortRequests(List<ScheduledTurnWindowRequest> bucket)
        {
            bucket.Sort((a, b) =>
            {
                int byPlayerFirst = PlayerFirstRank(a).CompareTo(PlayerFirstRank(b));
                if (byPlayerFirst != 0) return byPlayerFirst;
                return a.OwnerUnitId.Value.CompareTo(b.OwnerUnitId.Value);
            });
        }

        /// <summary>玩家优先档位：<c>PlayerFirst = 0</c> 之外一律 1（首版全部为 1）。</summary>
        private static int PlayerFirstRank(ScheduledTurnWindowRequest request) => 1;
    }

    /// <summary>
    /// 一个待打开的提交窗口请求（阶段 3 消费）。
    ///
    /// 它<strong>只</strong>描述"谁、多少预算"，不携带任何计划、Intent 或 Reservation。
    /// </summary>
    public sealed record WindowOpenRequest(UnitId OwnerUnitId, int BudgetTicks);

    /// <summary>
    /// 窗口命令处理的只读上下文（由模拟方在阶段 6 装配，禁用"生产者自报身份"）。
    /// </summary>
    public sealed class TurnWindowCommandContext
    {
        public TurnWindowCommandContext(
            ControllerId issuer,
            IReadOnlyList<UnitId> controlledUnitIds,
            UnitId? concurrentHeroUnitId,
            bool concurrentAbilityConfigured,
            long scheduleRevision)
        {
            Issuer = issuer;
            ControlledUnitIds = controlledUnitIds ?? Array.Empty<UnitId>();
            ConcurrentHeroUnitId = concurrentHeroUnitId;
            ConcurrentAbilityConfigured = concurrentAbilityConfigured;
            ScheduleRevision = scheduleRevision;
        }

        /// <summary>命令网关绑定的发行者身份（<strong>不</strong>来自命令载荷）。</summary>
        public ControllerId Issuer { get; }

        /// <summary>该发行者在本场战斗中受控的单位（稳定顺序由装配方保证）。</summary>
        public IReadOnlyList<UnitId> ControlledUnitIds { get; }

        /// <summary>可激活并发行动的主角单位（未配置时为 null）。</summary>
        public UnitId? ConcurrentHeroUnitId { get; }

        /// <summary>权威 <c>ConcurrentActionDefinition</c> 是否存在。</summary>
        public bool ConcurrentAbilityConfigured { get; }

        /// <summary>命令目标 Tick 的排程修订（并发激活不写排程，仅用于诊断）。</summary>
        public long ScheduleRevision { get; }

        public bool Controls(UnitId unitId)
        {
            for (int i = 0; i < ControlledUnitIds.Count; i++)
            {
                if (ControlledUnitIds[i] == unitId) return true;
            }
            return false;
        }
    }
}
