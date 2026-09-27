namespace ProjectHero.Logic.Ids
{
    using ProjectHero.Logic.Actions;

    /// <summary>
    /// 战斗实例 ID（5 种）。底层统一为 <c>long</c>；0 保留为无效值（default 即无效），
    /// 首个有效值为 1，单场战斗内单调递增且永不复用。不同 ID 类型之间不得隐式转换。
    /// 运行时正数约束由逻辑构造入口（<see cref="LogicIdGenerator"/>）验证。
    /// 注：Unity 6000.6.2f1 默认 C# 9，record struct 为 C# 10 特性，故手写只读值类型。
    /// </summary>
    public readonly struct UnitId
    {
        public readonly long Value;
        public UnitId(long value) { Value = value; }
        public bool IsValid => Value > 0;
        public bool Equals(UnitId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is UnitId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(UnitId left, UnitId right) => left.Equals(right);
        public static bool operator !=(UnitId left, UnitId right) => !left.Equals(right);
    }

    public readonly struct ActionPlanId
    {
        public readonly long Value;
        public ActionPlanId(long value) { Value = value; }
        public bool IsValid => Value > 0;
        public bool Equals(ActionPlanId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is ActionPlanId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(ActionPlanId left, ActionPlanId right) => left.Equals(right);
        public static bool operator !=(ActionPlanId left, ActionPlanId right) => !left.Equals(right);
    }

    public readonly struct ReactionOpportunityId
    {
        public readonly long Value;
        public ReactionOpportunityId(long value) { Value = value; }
        public bool IsValid => Value > 0;
        public bool Equals(ReactionOpportunityId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is ReactionOpportunityId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(ReactionOpportunityId left, ReactionOpportunityId right) => left.Equals(right);
        public static bool operator !=(ReactionOpportunityId left, ReactionOpportunityId right) => !left.Equals(right);
    }

    public readonly struct WindowId
    {
        public readonly long Value;
        public WindowId(long value) { Value = value; }
        public bool IsValid => Value > 0;
        public bool Equals(WindowId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is WindowId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(WindowId left, WindowId right) => left.Equals(right);
        public static bool operator !=(WindowId left, WindowId right) => !left.Equals(right);
    }

    public readonly struct EffectId
    {
        public readonly long Value;
        public EffectId(long value) { Value = value; }
        public bool IsValid => Value > 0;
        public bool Equals(EffectId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is EffectId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(EffectId left, EffectId right) => left.Equals(right);
        public static bool operator !=(EffectId left, EffectId right) => !left.Equals(right);
    }

    /// <summary>
    /// 战斗实例 ID 生成器（主方案 2.2.3）。各计数器从 1 起、单调递增、不复用；
    /// 所有下一个值都必须进入规范化快照与哈希（任务 03 落实）。
    ///
    /// <strong>唯一分配器契约（任务 05 收尾 R1 / 缺陷 D2）</strong>：本类中每种 ID 类型
    /// <strong>恰好一个</strong>分配方法（<see cref="NextUnitId"/> / <see cref="NextActionPlanId"/> /
    /// <see cref="NextReactionOpportunityId"/> / <see cref="NextWindowId"/> / <see cref="NextEffectId"/>），
    /// 且每个计数器<strong>只在该方法内</strong>递增。任何需要"下一个值"的地方
    /// （包括规范化快照）都只能读 <c>Next…Value</c> 只读属性，
    /// <strong>不得</strong>另建第二份计数状态——否则两条路径会各自取号而产生 ID 冲突。
    /// 该不变量由 <c>Task05ReactionOpportunityIdAllocatorTests</c> 逐条守护。
    /// </summary>
    public sealed class LogicIdGenerator
    {
        private long _nextUnitId = 1;
        private long _nextActionPlanId = 1;
        private long _nextReactionOpportunityId = 1;
        private long _nextWindowId = 1;
        private long _nextEffectId = 1;

        /// <summary>下一个将被分配的 <c>UnitId</c>（只读观察值；进入规范化快照与哈希）。</summary>
        public long NextUnitIdValue => _nextUnitId;

        /// <summary>下一个将被分配的 <c>ActionPlanId</c>（只读观察值）。</summary>
        public long NextActionPlanIdValue => _nextActionPlanId;

        /// <summary>下一个将被分配的 <c>ReactionOpportunityId</c>（只读观察值）。</summary>
        public long NextReactionOpportunityIdValue => _nextReactionOpportunityId;

        /// <summary>下一个将被分配的 <c>WindowId</c>（只读观察值）。</summary>
        public long NextWindowIdValue => _nextWindowId;

        /// <summary>下一个将被分配的 <c>EffectId</c>（只读观察值）。</summary>
        public long NextEffectIdValue => _nextEffectId;

        public UnitId NextUnitId() => new UnitId(_nextUnitId++);

        public ActionPlanId NextActionPlanId() => new ActionPlanId(_nextActionPlanId++);

        /// <summary>
        /// 分配指定的 <c>ActionPlanId</c>，当且仅当它<strong>恰好</strong>等于下一个待分配值时前进计数器。
        /// 它让"排程事务在求值与全部校验通过后、提交之前才真正占用 ID"成为可能：
        /// 事务先观察候选值建计划，失败则计数器不动，成功则在提交点原子占用。
        /// 任何跳号、回退或与已分配值冲突的请求一律以稳定码拒绝（绝不静默改写计数器）。
        /// </summary>
        internal void ReserveActionPlanId(ActionPlanId actionPlanId)
        {
            if (!actionPlanId.IsValid)
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_ID_INVALID, "0");
            if (actionPlanId.Value != _nextActionPlanId)
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_ID_RESERVATION_MISMATCH,
                    "requested=" + actionPlanId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " next=" + _nextActionPlanId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _nextActionPlanId++;
        }

        /// <summary>
        /// <c>ReactionOpportunityId</c> 的<strong>唯一</strong>分配器（任务 03 冻结契约字段
        /// <c>NextReactionOpportunityId</c> 的取值来源）。它不会被任何调用方旁路：
        /// 运行时机会的 ID 与快照里的"下一个机会 ID"都由它派生。
        /// </summary>
        public ReactionOpportunityId NextReactionOpportunityId() => new ReactionOpportunityId(_nextReactionOpportunityId++);

        public WindowId NextWindowId() => new WindowId(_nextWindowId++);

        public EffectId NextEffectId() => new EffectId(_nextEffectId++);
    }
}
