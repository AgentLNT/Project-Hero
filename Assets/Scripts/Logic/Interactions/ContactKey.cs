using System;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Interactions
{
    /// <summary>
    /// 接触类型（主方案 0.4 交互判定优先级矩阵的阶段顺序）。
    ///
    /// 枚举数值同时是该类型的<strong>冻结稳定排序键第一部分</strong>：数值变化会改变
    /// 接触规范化顺序与冲突图文本，因此不得重排或复用。
    /// 阶段 1/2/4/5 的区分（Dodge → Block → Attack → Move）来自矩阵本身。
    /// </summary>
    public enum ContactType
    {
        /// <summary>阶段 1：攻击 × TriggerTick 相同的 Dodge 计划。</summary>
        AttackDodge = 0,

        /// <summary>阶段 2：攻击 × TriggerTick 相同的 Block 计划。</summary>
        AttackBlock = 1,

        /// <summary>阶段 3：两个攻击 Intent 的有效区域相交（直接交互，Clash 候选）。Intent↔Intent 边。</summary>
        AttackAttack = 2,

        /// <summary>阶段 4/5：攻击 × 有效区间内的 Move 计划（旧格/新格覆盖标记区分 逃脱/拦截）。</summary>
        AttackMove = 3,

        /// <summary>阶段 6：攻击 × Active 区间内的 Guard 计划。</summary>
        AttackGuard = 4,

        /// <summary>阶段 6：攻击 × 无对手计划的普通单位目标。</summary>
        AttackTarget = 5,

        /// <summary>阶段 3/6：两个或更多攻击同 Tick 指向同一目标单位。Intent↔Intent 边。</summary>
        SharedTarget = 6
    }

    /// <summary>
    /// 接触规范化键（主方案 0.4.5 稳定仲裁键 / 08-多方仲裁与伤害.md:65）：
    /// <c>接触类型 + 双方稳定 UnitId + 双方 ActionPlanId + 目标 UnitId</c>。
    ///
    /// <strong>不使用发现顺序</strong>：双方按 <c>(UnitId, ActionPlanId)</c> 升序规范化成 First/Second，
    /// 因此 A→B 与 B→A 得到同一个键，任何输入排列、任何遍历顺序都得到同一个键。
    ///
    /// <c>ActionPlanId(0)</c> 表示"该侧没有计划"（ID 分配器保证真实 ID 从 1 起，0 保留为无效值），
    /// 用于"攻击 → 无对手计划的普通单位目标"。
    /// </summary>
    public readonly struct ContactKey : IEquatable<ContactKey>, IComparable<ContactKey>
    {
        public readonly ContactType Type;
        public readonly UnitId FirstUnitId;
        public readonly ActionPlanId FirstPlanId;
        public readonly UnitId SecondUnitId;
        public readonly ActionPlanId SecondPlanId;
        public readonly UnitId TargetUnitId;

        private ContactKey(
            ContactType type,
            UnitId firstUnitId,
            ActionPlanId firstPlanId,
            UnitId secondUnitId,
            ActionPlanId secondPlanId,
            UnitId targetUnitId)
        {
            Type = type;
            FirstUnitId = firstUnitId;
            FirstPlanId = firstPlanId;
            SecondUnitId = secondUnitId;
            SecondPlanId = secondPlanId;
            TargetUnitId = targetUnitId;
        }

        /// <summary>
        /// 由双方身份构造规范化键：两侧按 <c>(UnitId, ActionPlanId)</c> 升序落位，
        /// 与调用方传入顺序无关。
        /// </summary>
        public static ContactKey Create(
            ContactType type,
            UnitId unitA,
            ActionPlanId planA,
            UnitId unitB,
            ActionPlanId planB,
            UnitId targetUnitId)
        {
            bool aFirst = unitA.Value < unitB.Value
                          || (unitA.Value == unitB.Value && planA.Value <= planB.Value);
            return aFirst
                ? new ContactKey(type, unitA, planA, unitB, planB, targetUnitId)
                : new ContactKey(type, unitB, planB, unitA, planA, targetUnitId);
        }

        /// <summary>规范全序：类型 → 双方单位 → 双方计划 → 目标单位。全部为整数比较。</summary>
        public int CompareTo(ContactKey other)
        {
            int byType = ((int)Type).CompareTo((int)other.Type);
            if (byType != 0) return byType;

            int byFirstUnit = FirstUnitId.Value.CompareTo(other.FirstUnitId.Value);
            if (byFirstUnit != 0) return byFirstUnit;

            int byFirstPlan = FirstPlanId.Value.CompareTo(other.FirstPlanId.Value);
            if (byFirstPlan != 0) return byFirstPlan;

            int bySecondUnit = SecondUnitId.Value.CompareTo(other.SecondUnitId.Value);
            if (bySecondUnit != 0) return bySecondUnit;

            int bySecondPlan = SecondPlanId.Value.CompareTo(other.SecondPlanId.Value);
            if (bySecondPlan != 0) return bySecondPlan;

            return TargetUnitId.Value.CompareTo(other.TargetUnitId.Value);
        }

        public bool Equals(ContactKey other) => CompareTo(other) == 0;

        public override bool Equals(object obj) => obj is ContactKey other && Equals(other);

        /// <summary>只用于容器查找；不得作为任何判定或排序依据。</summary>
        public override int GetHashCode()
            => HashCode.Combine((int)Type, FirstUnitId.Value, FirstPlanId.Value,
                SecondUnitId.Value, SecondPlanId.Value, TargetUnitId.Value);

        /// <summary>规范序列化：不含发现顺序、不含哈希值。</summary>
        public string CanonicalText
            => Type.ToString() + '|'
               + FirstUnitId.Value.ToString(CultureInfo.InvariantCulture) + ':'
               + FirstPlanId.Value.ToString(CultureInfo.InvariantCulture) + '|'
               + SecondUnitId.Value.ToString(CultureInfo.InvariantCulture) + ':'
               + SecondPlanId.Value.ToString(CultureInfo.InvariantCulture) + '|'
               + TargetUnitId.Value.ToString(CultureInfo.InvariantCulture);

        public override string ToString() => CanonicalText;

        public static bool operator ==(ContactKey left, ContactKey right) => left.Equals(right);

        public static bool operator !=(ContactKey left, ContactKey right) => !left.Equals(right);
    }

    /// <summary>
    /// 一条<strong>带类型的接触</strong>（冲突图的边 / 锚定在 Intent 上的叶子接触）。
    ///
    /// 不可变：构造后所有字段只读，且不持有任何可变集合或世界引用。
    /// <see cref="CoveredBefore"/>/<see cref="CoveredAfter"/> 是 Dodge 旧格/新格契约
    /// （主方案 0.4.1 四格表）与 Move 逃脱/拦截所需的原始空间事实，
    /// 由候选构建器在两次空间查询后一次性写入；<strong>四格表本身的分支判定属于求解器</strong>。
    /// </summary>
    public sealed class InteractionContact
    {
        internal InteractionContact(
            ContactKey key,
            int attackerNodeIndex,
            int counterpartyNodeIndex,
            UnitId attackerUnitId,
            ActionPlanId attackerPlanId,
            UnitId counterpartyUnitId,
            ActionPlanId counterpartyPlanId,
            ActionType counterpartyActionType,
            UnitId targetUnitId,
            bool coveredBefore,
            bool coveredAfter)
        {
            Key = key;
            AttackerNodeIndex = attackerNodeIndex;
            CounterpartyNodeIndex = counterpartyNodeIndex;
            AttackerUnitId = attackerUnitId;
            AttackerPlanId = attackerPlanId;
            CounterpartyUnitId = counterpartyUnitId;
            CounterpartyPlanId = counterpartyPlanId;
            CounterpartyActionType = counterpartyActionType;
            TargetUnitId = targetUnitId;
            CoveredBefore = coveredBefore;
            CoveredAfter = coveredAfter;
        }

        /// <summary>规范化接触键（去重、排序、快照与事件的唯一依据）。</summary>
        public ContactKey Key { get; }

        public ContactType Type => Key.Type;

        /// <summary>锚定节点在冲突图节点序列中的下标。</summary>
        public int AttackerNodeIndex { get; }

        /// <summary>对手也是 Intent 节点时的下标；否则 <c>-1</c>。</summary>
        public int CounterpartyNodeIndex { get; }

        /// <summary>键中 First 侧单位（Intent↔Intent 接触时 = 规范先者）。</summary>
        public UnitId AttackerUnitId { get; }

        /// <summary>键中 First 侧计划。</summary>
        public ActionPlanId AttackerPlanId { get; }

        /// <summary>对手单位（无对手计划时 = 目标单位）。</summary>
        public UnitId CounterpartyUnitId { get; }

        /// <summary>对手计划；<c>0</c> 表示没有对手计划。</summary>
        public ActionPlanId CounterpartyPlanId { get; }

        /// <summary>对手动作族；无对手计划时为 <see cref="ActionType.None"/>。</summary>
        public ActionType CounterpartyActionType { get; }

        /// <summary>目标单位（Intent↔Intent 的 Clash 接触无单一目标，为 0）。</summary>
        public UnitId TargetUnitId { get; }

        /// <summary>Dodge 提交<strong>前</strong>空间快照下该接触是否成立（仅 Attack→Unit 接触有意义）。</summary>
        public bool CoveredBefore { get; }

        /// <summary>Dodge 提交<strong>后</strong>空间快照下该接触是否成立（仅 Attack→Unit 接触有意义）。</summary>
        public bool CoveredAfter { get; }

        /// <summary>是否是 Intent↔Intent 边（决定连通分量，因而决定冲突组划分）。</summary>
        public bool IsNodeToNode => CounterpartyNodeIndex >= 0;

        public bool HasCounterpartyPlan => CounterpartyPlanId.Value > 0;

        /// <summary>规范序列化（排列不变性断言的抓手之一）。</summary>
        public string CanonicalText
            => "contact|" + Key.CanonicalText
               + "|att=" + AttackerNodeIndex.ToString(CultureInfo.InvariantCulture)
               + "|cp=" + CounterpartyNodeIndex.ToString(CultureInfo.InvariantCulture)
               + "|cpType=" + CounterpartyActionType.ToString()
               + "|before=" + (CoveredBefore ? "1" : "0")
               + "|after=" + (CoveredAfter ? "1" : "0");

        public override string ToString() => CanonicalText;
    }
}
