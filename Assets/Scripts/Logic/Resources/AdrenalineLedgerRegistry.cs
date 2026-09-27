using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Resources
{
    /// <summary>
    /// <strong>全场肾上腺素账本集合</strong>（任务 07「必须产出」7 与 12）。
    ///
    /// 它是肾上腺素资源的<strong>唯一</strong>所有者与唯一写入通道：
    /// <list type="bullet">
    /// <item>按 <c>UnitId</c> 升序维护每单位一份 <see cref="AdrenalineLedger"/>（稳定顺序，不依赖字典枚举）；</item>
    /// <item>实现 <see cref="IAdrenalineLedgerPort"/>：反应接受 = <c>Available -&gt; Reservation</c>、
    /// TriggerTick = 消费、来源威胁取消 = 按周期规则释放；</item>
    /// <item>拥有单位自己窗口打开时的"周期 +1 且 Available 清零"入口；</item>
    /// <item>拥有 Tick 末<strong>唯一</strong>批量入账入口 <see cref="ApplyTickEndAccrual"/>：
    /// 它只接受任务 08 规范聚合后的 <see cref="AdrenalineAccrualFacts"/>，
    /// 其他系统<strong>不得</strong>逐接触直接加 <c>Available</c>；</item>
    /// <item>拥有战斗结束清理入口（清空 Available 与全部预留，不产生可继续使用的退款额度）。</item>
    /// </list>
    ///
    /// 预留到计划的归属查询按"计划 → 账本"扫描（账本数量=单位数，且按 UnitId 升序），
    /// 因此这里<strong>不</strong>维护第二份"计划属于谁"的镜像状态，避免两套账。
    /// </summary>
    public sealed class AdrenalineLedgerRegistry : IAdrenalineLedgerPort
    {
        private readonly AdrenalineRules _rules;
        private readonly List<AdrenalineLedger> _ordered = new List<AdrenalineLedger>();
        private readonly Dictionary<long, AdrenalineLedger> _byUnit = new Dictionary<long, AdrenalineLedger>();

        public AdrenalineLedgerRegistry(AdrenalineRules rules)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        }

        /// <summary>账本变化端口（由模拟方接到 Outbox；本类型不自行发射逻辑事件）。</summary>
        public Action<AdrenalineLedgerChange> ChangedSink { get; set; }

        public AdrenalineRules Rules => _rules;

        /// <summary>按 <c>UnitId</c> 升序的账本（只读）。</summary>
        public IReadOnlyList<AdrenalineLedger> Ledgers => _ordered;

        public int Count => _ordered.Count;

        /// <summary>
        /// 注册一个单位并写入其开局状态（初始 Available 与个人周期号来自权威定义/初始化快照）。
        /// 重复注册同一单位是装配矛盾，显式失败。
        /// </summary>
        public AdrenalineLedger Register(UnitId unitId, int initialAvailable, long initialCycleId)
        {
            if (!unitId.IsValid)
                throw new LogicDefinitionException(AdrenalineLedgerCodes.LEDGER_INVARIANT, "unit id invalid");
            if (_byUnit.ContainsKey(unitId.Value))
                throw new LogicDefinitionException(
                    AdrenalineLedgerCodes.LEDGER_INVARIANT,
                    "duplicate adrenaline ledger for unit " + unitId.Value.ToString(CultureInfo.InvariantCulture));
            if (initialAvailable < 0 || initialCycleId < 0)
                throw new LogicDefinitionException(
                    AdrenalineLedgerCodes.LEDGER_INVARIANT,
                    "initial adrenaline invalid for unit " + unitId.Value.ToString(CultureInfo.InvariantCulture));

            var ledger = new AdrenalineLedger(unitId, _rules);
            ledger.InitializeFromBattleStart(initialAvailable, initialCycleId);
            ledger.Changed = ChangedSink;
            _ordered.Add(ledger);
            _byUnit[unitId.Value] = ledger;
            return ledger;
        }

        public AdrenalineLedger Find(UnitId unitId)
            => unitId.IsValid && _byUnit.TryGetValue(unitId.Value, out AdrenalineLedger ledger) ? ledger : null;

        /// <summary>找到持有该计划预留的账本（按 <c>UnitId</c> 升序扫描；不存在返回 null）。</summary>
        public AdrenalineLedger FindByReservation(ActionPlanId planId)
        {
            if (!planId.IsValid) return null;
            for (int i = 0; i < _ordered.Count; i++)
            {
                if (_ordered[i].ReservationOf(planId) != null) return _ordered[i];
            }
            return null;
        }

        /// <inheritdoc />
        public string TryReserveForReaction(UnitId unitId, ActionPlanId planId, int cost)
        {
            AdrenalineLedger ledger = Find(unitId);
            if (ledger == null) return AdrenalineLedgerCodes.LEDGER_INVARIANT;
            return ledger.TryReserveForReaction(planId, cost);
        }

        /// <inheritdoc />
        public void ConsumeAtTrigger(ActionPlanId planId)
        {
            AdrenalineLedger ledger = FindByReservation(planId);
            if (ledger == null) return;
            ledger.ConsumeAtTrigger(planId);
        }

        /// <inheritdoc />
        public void ReleaseForSourceThreatCancelled(ActionPlanId planId)
        {
            AdrenalineLedger ledger = FindByReservation(planId);
            if (ledger == null) return;
            ledger.ReleaseForSourceThreatCancelled(planId);
        }

        /// <inheritdoc />
        public void DiscardReservation(ActionPlanId planId)
        {
            AdrenalineLedger ledger = FindByReservation(planId);
            if (ledger == null) return;
            ledger.DiscardReservation(planId);
        }

        /// <summary>
        /// 单位<strong>自己</strong>的窗口打开：递增个人周期并把 Available 清零。
        /// 已有合法反应预留<strong>继续存在并可触发</strong>；窗口关闭<strong>不</strong>清零。
        /// </summary>
        public void BeginOwnWindow(UnitId unitId)
        {
            AdrenalineLedger ledger = Find(unitId);
            ledger?.BeginOwnWindow();
        }

        /// <summary>
        /// Tick 末<strong>唯一</strong>肾上腺素批量入账入口（任务 08 在全部 Resolution 提交后提供规范事实）。
        ///
        /// 入参必须是按 <c>UnitId</c> <strong>严格升序</strong>、每单位<strong>至多一条</strong>的规范序列；
        /// 乱序或重复键是装配矛盾，显式失败（绝不静默修正或按接触累加）。
        /// </summary>
        public void ApplyTickEndAccrual(IReadOnlyList<AdrenalineAccrualFacts> factsOrdered)
        {
            if (factsOrdered == null || factsOrdered.Count == 0) return;

            long previousUnitId = 0L;
            for (int i = 0; i < factsOrdered.Count; i++)
            {
                AdrenalineAccrualFacts facts = factsOrdered[i];
                if (facts == null)
                    throw new LogicDefinitionException(AdrenalineLedgerCodes.LEDGER_INVARIANT, "null accrual facts");
                if (facts.UnitId.Value <= previousUnitId)
                    throw new LogicDefinitionException(
                        AdrenalineLedgerCodes.LEDGER_INVARIANT,
                        "accrual facts must be strictly ascending by unit: " +
                        facts.UnitId.Value.ToString(CultureInfo.InvariantCulture));
                previousUnitId = facts.UnitId.Value;

                AdrenalineLedger ledger = Find(facts.UnitId);
                if (ledger == null)
                    throw new LogicDefinitionException(
                        AdrenalineLedgerCodes.LEDGER_INVARIANT,
                        "no adrenaline ledger for unit " + facts.UnitId.Value.ToString(CultureInfo.InvariantCulture));
                ledger.ApplyAccrualFacts(facts);
            }
        }

        /// <summary>战斗结束：清空全部单位的 Available 与预留，不产生可继续使用的退款额度。</summary>
        public void ClearForBattleEnd()
        {
            for (int i = 0; i < _ordered.Count; i++) _ordered[i].ClearForBattleEnd();
        }

        /// <summary>按 <c>UnitId</c> 升序的规范快照（Available、周期号与逐计划预留明细）。</summary>
        public IReadOnlyList<AdrenalineLedgerSnapshot> BuildSnapshots()
            => TurnBudgetLedger.BuildAdrenalineSnapshots(_ordered);
    }
}
