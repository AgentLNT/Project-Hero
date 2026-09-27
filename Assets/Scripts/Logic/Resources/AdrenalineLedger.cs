using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Resources
{
    /// <summary>
    /// 肾上腺素账本变化类别（进入 <c>AdrenalineLedgerChangedEvent</c>；数值进入快照/事件，不得重排）。
    /// </summary>
    public enum AdrenalineChangeKind
    {
        /// <summary>按规范聚合的伤害/结果事实一次性入账（每单位每 Tick 至多一次聚合）。</summary>
        Accrued = 0,

        /// <summary>接受 Block/Dodge：Available -&gt; 按计划归属的带周期预留。</summary>
        Reserved = 1,

        /// <summary>TriggerTick：预留被消费（删除预留，不退）。</summary>
        Consumed = 2,

        /// <summary>来源威胁在 TriggerTick 前取消且预留仍属当前周期：返还 Available。</summary>
        Refunded = 3,

        /// <summary>拥有者自己的窗口打开：递增个人周期并把 Available 清零。</summary>
        CycleReset = 4,

        /// <summary>战斗结束：清空 Available 与全部预留，不产生可继续使用的退款额度。</summary>
        BattleEndCleared = 5,

        /// <summary>旧周期预留被删除（来源取消但已跨过拥有者下一窗口边界）：不注入新周期。</summary>
        StaleCycleReservationRemoved = 6,

        /// <summary>
        /// 终态作废：预留被删除但<strong>不</strong>返还（非"来源取消"的终态，攻击尚未触发）。
        ///
        /// 它与 <see cref="Refunded"/> 的区别只在"钱去哪了"：作废永远不返还 Available，
        /// 但同样**不得**让预留悬挂到战斗结束（00 号规则 19：Step 返回时资源预留不得引用终态计划）。
        /// 数值<strong>追加在末尾</strong>：它进入快照与事件，不得重排。
        /// </summary>
        ReservationDiscarded = 7
    }

    /// <summary>肾上腺素账本错误码（进入验收与诊断；不得改名）。</summary>
    public static class AdrenalineLedgerCodes
    {
        public const string INSUFFICIENT_ADRENALINE = "ADRENALINE_INSUFFICIENT";
        public const string DUPLICATE_RESERVATION = "ADRENALINE_DUPLICATE_RESERVATION";
        public const string RESERVATION_UNKNOWN = "ADRENALINE_RESERVATION_UNKNOWN";
        public const string COST_INVALID = "ADRENALINE_COST_INVALID";
        public const string LEDGER_INVARIANT = "ADRENALINE_LEDGER_INVARIANT";
        public const string NOT_A_REACTION = "ADRENALINE_NOT_A_REACTION";
    }

    /// <summary>
    /// 一次 Tick 末<strong>规范聚合</strong>的肾上腺素入账事实（任务包「必须产出」7 最后一段）。
    ///
    /// 关键语义：它<strong>已经</strong>按稳定键聚合完毕——每个单位每 Tick 至多一条，
    /// 每条最多一次 Clash 成功、每个反应计划最多一次成功事实。因此账本<strong>不会</strong>
    /// 逐接触舍入，也不会重复发放奖励。
    /// </summary>
    public sealed record AdrenalineAccrualFacts(
        UnitId UnitId,
        int TotalFinalDamageDealtQ10,
        int TotalFinalDamageReceivedQ10,
        int SuccessfulBlockCount,
        int SuccessfulDodgeCount,
        int ClashSuccessCount);

    /// <summary>
    /// 肾上腺素入账事实来源（任务 08 的 Resolution 全部提交后提供；任务 07 冻结接口与聚合规则）。
    ///
    /// 返回顺序必须是 <c>UnitId</c> 升序（规范顺序）；重复的单位键是装配矛盾，账本会显式失败。
    /// </summary>
    public interface IAdrenalineAccrualFactSource
    {
        IReadOnlyList<AdrenalineAccrualFacts> BuildAccrualFactsOrdered(long tick);
    }

    /// <summary>
    /// 反应接受/触发的肾上腺素事务端口（由 <c>ReactionOpportunitySystem</c> 调用）。
    /// 返回 null = 成功；否则为稳定拒绝码（调用方据此整批回滚并稳定拒绝命令）。
    /// </summary>
    public interface IAdrenalineLedgerPort
    {
        /// <summary>接受反应：把 <paramref name="cost"/> 从 Available 转入带个人周期的计划预留。</summary>
        string TryReserveForReaction(UnitId unitId, ActionPlanId planId, int cost);

        /// <summary>TriggerTick：消费该计划的预留（幂等：无预留时为无操作）。</summary>
        void ConsumeAtTrigger(ActionPlanId planId);

        /// <summary>来源威胁终止：按"仍属当前周期才返还"规则释放该计划的预留。</summary>
        void ReleaseForSourceThreatCancelled(ActionPlanId planId);

        /// <summary>
        /// 终态作废：删除该计划的预留但<strong>不</strong>返还 Available
        /// （防御者主动取消 / 被控制 / 死亡 / 目的格失效等"不退款"终态）。
        /// 幂等：没有预留时为无操作。它<strong>不</strong>注入任何新周期额度。
        /// </summary>
        void DiscardReservation(ActionPlanId planId);
    }

    /// <summary>
    /// <strong>单位肾上腺素账本</strong>（主方案 0.2 / 00 号规则 27）。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item><c>AvailableAdrenaline</c> 不随时间衰减，跨其他单位窗口保留；</item>
    /// <item>该单位<strong>自己</strong>的下一次窗口打开时：先递增 <c>ReservationCycleId</c>
    /// 并把 Available 清零，再授予新窗口预算；已有合法预留<strong>继续存在并可触发</strong>；</item>
    /// <item>窗口关闭<strong>不</strong>清零；</item>
    /// <item>接受反应时从 Available 原子转入带 <c>ReservationCycleId</c> 的计划预留；
    /// TriggerTick 删除预留并视为已消费；</item>
    /// <item>来源威胁在 TriggerTick 前取消时：预留仍属当前周期 ⇒ 返还 Available；
    /// 已跨过拥有者自己下一窗口打开边界 ⇒ 只删除旧周期预留，
    /// <strong>不</strong>把过期资源带入新周期；</item>
    /// <item>防御者主动取消、被控制/死亡、目的格失效或反应已经触发 ⇒ <strong>不</strong>退款；</item>
    /// <item>入账只按 <see cref="AdrenalineAccrualFacts"/> 聚合一次，并裁到
    /// <see cref="AdrenalineRules.MaxAvailablePerCycle"/> 上限；裁剪<strong>不</strong>改变任何预留。</item>
    /// </list>
    /// </summary>
    public sealed class AdrenalineLedger
    {
        private readonly AdrenalineRules _rules;
        private readonly Dictionary<long, ReactionReservation> _reservations = new Dictionary<long, ReactionReservation>();
        private readonly List<ActionPlanId> _orderedPlanIds = new List<ActionPlanId>();

        public AdrenalineLedger(UnitId unitId, AdrenalineRules rules)
        {
            if (!unitId.IsValid)
                throw new LogicDefinitionException(AdrenalineLedgerCodes.LEDGER_INVARIANT, "unitId invalid");
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            UnitId = unitId;
            CycleId = 0L;
            AvailableAdrenaline = 0;
        }

        public UnitId UnitId { get; }

        /// <summary>
        /// 写入开局状态（初始 Available 与个人周期号，来自权威定义/初始化快照）。
        /// 它只用于战斗初始化，<strong>不</strong>发射变化事件；重复调用是装配矛盾。
        /// </summary>
        public void InitializeFromBattleStart(int initialAvailable, long initialCycleId)
        {
            if (initialAvailable < 0 || initialCycleId < 0)
                throw new LogicDefinitionException(AdrenalineLedgerCodes.LEDGER_INVARIANT,
                    "initial available=" + initialAvailable.ToString(CultureInfo.InvariantCulture) +
                    " cycle=" + initialCycleId.ToString(CultureInfo.InvariantCulture));
            if (AvailableAdrenaline != 0 || CycleId != 0L || _reservations.Count != 0)
                throw new LogicDefinitionException(AdrenalineLedgerCodes.LEDGER_INVARIANT,
                    "ledger already initialized for unit " + UnitId.Value.ToString(CultureInfo.InvariantCulture));
            AvailableAdrenaline = initialAvailable;
            CycleId = initialCycleId;
        }

        /// <summary>个人周期号（该单位自己的窗口每次打开 +1；跨其他单位窗口不变）。</summary>
        public long CycleId { get; private set; }

        public int AvailableAdrenaline { get; private set; }

        public int ReservedAdrenaline
        {
            get
            {
                int total = 0;
                foreach (KeyValuePair<long, ReactionReservation> pair in _reservations) total += pair.Value.Amount;
                return total;
            }
        }

        public int ReservationCount => _reservations.Count;

        /// <summary>该计划当前持有的预留（不存在时返回 null）。</summary>
        public AdrenalineReservationEntry ReservationOf(ActionPlanId planId)
            => _reservations.TryGetValue(planId.Value, out ReactionReservation reservation)
                ? new AdrenalineReservationEntry(planId.Value, reservation.Amount, reservation.CycleId)
                : null;

        /// <summary>按 <c>ActionPlanId</c> 升序的预留明细快照。</summary>
        public IReadOnlyList<AdrenalineReservationEntry> BuildReservationSnapshots()
        {
            var ordered = new List<ActionPlanId>(_orderedPlanIds);
            ordered.Sort((a, b) => a.Value.CompareTo(b.Value));
            var result = new List<AdrenalineReservationEntry>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                ReactionReservation reservation = _reservations[ordered[i].Value];
                result.Add(new AdrenalineReservationEntry(ordered[i].Value, reservation.Amount, reservation.CycleId));
            }
            return result;
        }

        /// <summary>账本变化事件端口（由模拟方接到 Outbox）。</summary>
        public Action<AdrenalineLedgerChange> Changed { get; set; }


        /// <summary>
        /// 接受反应：Available -&gt; 带个人周期的计划预留。
        /// 费用必须为正、来自权威定义，且同一计划不得重复预留。
        /// </summary>
        public string TryReserveForReaction(ActionPlanId planId, int cost)
        {
            if (!planId.IsValid) return AdrenalineLedgerCodes.LEDGER_INVARIANT;
            if (cost <= 0) return AdrenalineLedgerCodes.COST_INVALID;
            if (_reservations.ContainsKey(planId.Value)) return AdrenalineLedgerCodes.DUPLICATE_RESERVATION;
            if (cost > AvailableAdrenaline) return AdrenalineLedgerCodes.INSUFFICIENT_ADRENALINE;

            int before = AvailableAdrenaline;
            AvailableAdrenaline -= cost;
            _reservations[planId.Value] = new ReactionReservation(cost, CycleId);
            _orderedPlanIds.Add(planId);
            Changed?.Invoke(new AdrenalineLedgerChange(
                UnitId, CycleId, AdrenalineChangeKind.Reserved, before, AvailableAdrenaline, planId, cost));
            return null;
        }

        /// <summary>TriggerTick：删除预留并视为已消费（幂等：没有预留时是无操作）。</summary>
        public void ConsumeAtTrigger(ActionPlanId planId)
        {
            if (!_reservations.TryGetValue(planId.Value, out ReactionReservation reservation)) return;
            RemoveReservation(planId);
            Changed?.Invoke(new AdrenalineLedgerChange(
                UnitId, CycleId, AdrenalineChangeKind.Consumed,
                AvailableAdrenaline, AvailableAdrenaline, planId, reservation.Amount));
        }

        /// <summary>
        /// 来源威胁在 TriggerTick 前取消：预留仍属当前个人周期 ⇒ 返还 Available；
        /// 否则只删除旧周期预留（<strong>不</strong>注入新周期）。
        /// </summary>
        public void ReleaseForSourceThreatCancelled(ActionPlanId planId)
        {
            if (!_reservations.TryGetValue(planId.Value, out ReactionReservation reservation)) return;
            RemoveReservation(planId);

            if (reservation.CycleId == CycleId)
            {
                int before = AvailableAdrenaline;
                AvailableAdrenaline += reservation.Amount;
                Changed?.Invoke(new AdrenalineLedgerChange(
                    UnitId, CycleId, AdrenalineChangeKind.Refunded, before, AvailableAdrenaline,
                    planId, reservation.Amount));
            }
            else
            {
                Changed?.Invoke(new AdrenalineLedgerChange(
                    UnitId, CycleId, AdrenalineChangeKind.StaleCycleReservationRemoved,
                    AvailableAdrenaline, AvailableAdrenaline, planId, reservation.Amount));
            }
        }

        /// <summary>
        /// 终态作废：删除该计划的预留但<strong>不</strong>返还 Available。
        ///
        /// 用于"攻击尚未触发就因非来源取消而终结"的反应计划：规则要求不退款，
        /// 但预留也**不得**悬挂（否则 Step 返回时资源预留会引用一个终态计划）。
        /// 幂等：没有预留时是无操作；永不注入新周期。
        /// </summary>
        public void DiscardReservation(ActionPlanId planId)
        {
            if (!_reservations.TryGetValue(planId.Value, out ReactionReservation reservation)) return;
            RemoveReservation(planId);
            Changed?.Invoke(new AdrenalineLedgerChange(
                UnitId, CycleId, AdrenalineChangeKind.ReservationDiscarded,
                AvailableAdrenaline, AvailableAdrenaline, planId, reservation.Amount));
        }

        /// <summary>
        /// 该单位<strong>自己</strong>的窗口打开：先递增个人周期、把 Available 清零，
        /// 再（由调用方）授予新窗口预算。既有合法反应预留继续存在并可触发。
        /// </summary>
        public void BeginOwnWindow()
        {
            int before = AvailableAdrenaline;
            CycleId = checked(CycleId + 1L);
            AvailableAdrenaline = 0;
            Changed?.Invoke(new AdrenalineLedgerChange(
                UnitId, CycleId, AdrenalineChangeKind.CycleReset, before, 0, null, 0));
        }

        /// <summary>
        /// 按规范聚合事实入账一次：各伤害类别分别量化一次、加入固定奖励，
        /// 最后把 Available 裁到周期上限（裁剪<strong>不</strong>影响预留）。
        /// </summary>
        public void ApplyAccrualFacts(AdrenalineAccrualFacts facts)
        {
            if (facts == null) return;
            if (facts.UnitId != UnitId)
                throw new LogicDefinitionException(AdrenalineLedgerCodes.LEDGER_INVARIANT,
                    "unit mismatch: " + facts.UnitId.Value.ToString(CultureInfo.InvariantCulture));

            int gain = 0;
            if (facts.TotalFinalDamageDealtQ10 > 0)
                gain += QuantizeGain(facts.TotalFinalDamageDealtQ10, _rules.DamageDealtGainQ10);
            if (facts.TotalFinalDamageReceivedQ10 > 0)
                gain += QuantizeGain(facts.TotalFinalDamageReceivedQ10, _rules.DamageReceivedGainQ10);
            if (facts.SuccessfulBlockCount > 0)
                gain += facts.SuccessfulBlockCount * _rules.SuccessfulBlockReward;
            if (facts.SuccessfulDodgeCount > 0)
                gain += facts.SuccessfulDodgeCount * _rules.SuccessfulDodgeReward;
            if (facts.ClashSuccessCount > 0)
                gain += facts.ClashSuccessCount * _rules.ClashReward;
            if (gain <= 0) return;

            int before = AvailableAdrenaline;
            int uncapped = before + gain;
            AvailableAdrenaline = uncapped > _rules.MaxAvailablePerCycle
                ? _rules.MaxAvailablePerCycle
                : uncapped;

            Changed?.Invoke(new AdrenalineLedgerChange(
                UnitId, CycleId, AdrenalineChangeKind.Accrued, before, AvailableAdrenaline, null, gain));
        }

        /// <summary>战斗结束：清空 Available 与全部预留（不产生可继续使用的退款额度）。</summary>
        public void ClearForBattleEnd()
        {
            int before = AvailableAdrenaline;
            AvailableAdrenaline = 0;
            _reservations.Clear();
            _orderedPlanIds.Clear();
            Changed?.Invoke(new AdrenalineLedgerChange(
                UnitId, CycleId, AdrenalineChangeKind.BattleEndCleared, before, 0, null, 0));
        }

        /// <summary>
        /// 逐 Tick 伤害类别的一次性量化：<c>floor(rawQ10 × gainQ10 / 10 / 10)</c>。
        /// 它<strong>不</strong>按接触舍入——入参已经是本 Tick 的规范聚合值。
        /// </summary>
        private static int QuantizeGain(int rawQ10, int gainQ10)
        {
            long scaled = (long)rawQ10 * gainQ10;
            return (int)(scaled / 100L);
        }

        private void RemoveReservation(ActionPlanId planId)
        {
            _reservations.Remove(planId.Value);
            _orderedPlanIds.Remove(planId);
        }

        /// <summary>预留的内部表示（只在本类型内使用）。</summary>
        private readonly struct ReactionReservation
        {
            public ReactionReservation(int amount, long cycleId)
            {
                Amount = amount;
                CycleId = cycleId;
            }

            public int Amount { get; }

            public long CycleId { get; }
        }
    }

    /// <summary>
    /// 一条反应肾上腺素预留的账本条目（值语义；由 <c>AdrenalineLedger.BuildReservationSnapshots</c>
    /// 投影为 <see cref="AdrenalineReservationSnapshot"/> 快照记录）。
    /// </summary>
    public sealed record AdrenalineReservationEntry(long ActionPlanId, int ReservedAmount, long ReservationCycleId);

    /// <summary>
    /// 一次肾上腺素账本变化的<strong>值语义</strong>载荷（与 <c>AdrenalineLedgerChangedEvent</c>
    /// 一一对应；账本自身不发射逻辑事件，由模拟方把本记录转换为事件并分配 Sequence）。
    /// </summary>
    public sealed record AdrenalineLedgerChange(
        UnitId UnitId,
        long CycleId,
        AdrenalineChangeKind ChangeKind,
        int AvailableBefore,
        int AvailableAfter,
        ActionPlanId? ReservationPlanId,
        int ReservationAmount);

    /// <summary>
    /// 一次 TurnBudget 账本变化的<strong>值语义</strong>载荷（与 <c>TurnBudgetChangedEvent</c> 一一对应）。
    /// </summary>
    public sealed record TurnBudgetChange(
        WindowId WindowId,
        UnitId OwnerUnitId,
        ActionPlanId? ActionPlanId,
        TurnBudgetChangeKind ChangeKind,
        ResourceChangeSource Source,
        int AvailableBefore,
        int AvailableAfter,
        int ReservedBefore,
        int ReservedAfter,
        int SpentBefore,
        int SpentAfter);

    /// <summary>TurnBudget 账本变化的稳定来源判别式（显式命令 / 系统自动延期 / 终态清理 / 启动提交 / 结束）。</summary>
    public enum ResourceChangeSource
    {
        ExplicitScheduleEdit = 0,
        SystemAutoDeferral = 1,
        TerminalCleanup = 2,
        StartCommit = 3,
        BattleEndFinalizer = 4
    }

    /// <summary>
    /// <strong>全部窗口账本的规范聚合视图</strong>（含已关闭窗口的历史账本）。
    ///
    /// 它<strong>不</strong>拥有窗口，只把 <c>TurnWindow</c> 的账本按 <c>WindowId</c> 升序投影
    /// 为资源快照；<c>Reserved + Spent + Available == Σ Total</c> 是结构事实。
    /// 窗口<strong>不</strong>持有计划对象，因此这里也只有可审计的按计划预留明细。
    /// </summary>
    public static class TurnBudgetLedger
    {
        public static BattleResourceSnapshotAccumulator Begin(int metaResource)
            => new BattleResourceSnapshotAccumulator(metaResource);

        /// <summary>把一份肾上腺素账本集合投影为不可变快照（按 <c>UnitId</c> 升序）。</summary>
        public static IReadOnlyList<AdrenalineLedgerSnapshot> BuildAdrenalineSnapshots(
            IReadOnlyList<AdrenalineLedger> ledgers)
        {
            if (ledgers == null || ledgers.Count == 0) return Array.Empty<AdrenalineLedgerSnapshot>();
            var ordered = new List<AdrenalineLedger>(ledgers);
            ordered.Sort((a, b) => a.UnitId.Value.CompareTo(b.UnitId.Value));
            var result = new List<AdrenalineLedgerSnapshot>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                AdrenalineLedger ledger = ordered[i];
                IReadOnlyList<AdrenalineReservationEntry> entries = ledger.BuildReservationSnapshots();
                var projected = new List<AdrenalineReservationSnapshot>(entries.Count);
                for (int r = 0; r < entries.Count; r++)
                {
                    projected.Add(new AdrenalineReservationSnapshot(
                        entries[r].ActionPlanId, entries[r].ReservedAmount, entries[r].ReservationCycleId));
                }
                result.Add(new AdrenalineLedgerSnapshot(
                    ledger.UnitId.Value, ledger.AvailableAdrenaline, ledger.CycleId,
                    projected, ledger.ReservedAdrenaline));
            }
            return result;
        }
    }

    /// <summary>预算聚合累加器（避免调用方手写求和顺序；顺序与数值无关，仅便于装配）。</summary>
    public sealed class BattleResourceSnapshotAccumulator
    {
        public BattleResourceSnapshotAccumulator(int metaResource)
        {
            MetaResource = metaResource;
        }

        public int MetaResource { get; }

        public long Total { get; private set; }

        public long Reserved { get; private set; }

        public long Spent { get; private set; }

        public void Add(TurnWindow window)
        {
            if (window == null) return;
            Total += window.TotalBudgetTicks;
            Reserved += window.ReservedBudgetTicks;
            Spent += window.SpentBudgetTicks;
        }

        public BattleResourceSnapshot Build(IReadOnlyList<AdrenalineLedgerSnapshot> adrenaline)
            => new BattleResourceSnapshot(MetaResource, Total - Reserved - Spent, Reserved, Spent, adrenaline);
    }
}
