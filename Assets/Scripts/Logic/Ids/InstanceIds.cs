namespace ProjectHero.Logic.Ids
{
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
    /// </summary>
    public sealed class LogicIdGenerator
    {
        private long _nextUnitId = 1;
        private long _nextActionPlanId = 1;
        private long _nextReactionOpportunityId = 1;
        private long _nextWindowId = 1;
        private long _nextEffectId = 1;

        public UnitId NextUnitId() => new UnitId(_nextUnitId++);

        public ActionPlanId NextActionPlanId() => new ActionPlanId(_nextActionPlanId++);

        public ReactionOpportunityId NextReactionOpportunityId() => new ReactionOpportunityId(_nextReactionOpportunityId++);

        public WindowId NextWindowId() => new WindowId(_nextWindowId++);

        public EffectId NextEffectId() => new EffectId(_nextEffectId++);
    }
}
