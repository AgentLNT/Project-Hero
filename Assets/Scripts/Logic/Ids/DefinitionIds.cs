namespace ProjectHero.Logic.Ids
{
    /// <summary>
    /// 配置定义 ID（12 种）。底层为字符串；格式统一为小写英文字母、数字、点和下划线；
    /// 不能为空且在各自命名空间内不得重复。格式/重复由 <see cref="DefinitionIdValidation"/>
    /// 集中校验（与主方案 2.2.1 一致，ID 值类型本身不做构造期校验）。
    /// 不同 ID 类型之间没有隐式转换，防止 <c>UnitDefinitionId</c> 与 <c>FactionId</c> 互相误传。
    /// 注：Unity 6000.6.2f1 默认 C# 9，record struct 为 C# 10 特性，故手写只读值类型。
    /// </summary>
    public readonly struct UnitDefinitionId
    {
        public readonly string Value;
        public UnitDefinitionId(string value) { Value = value; }
        public bool Equals(UnitDefinitionId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is UnitDefinitionId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(UnitDefinitionId left, UnitDefinitionId right) => left.Equals(right);
        public static bool operator !=(UnitDefinitionId left, UnitDefinitionId right) => !left.Equals(right);
    }

    public readonly struct ActionSpecId
    {
        public readonly string Value;
        public ActionSpecId(string value) { Value = value; }
        public bool Equals(ActionSpecId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is ActionSpecId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(ActionSpecId left, ActionSpecId right) => left.Equals(right);
        public static bool operator !=(ActionSpecId left, ActionSpecId right) => !left.Equals(right);
    }

    public readonly struct VolumeSpecId
    {
        public readonly string Value;
        public VolumeSpecId(string value) { Value = value; }
        public bool Equals(VolumeSpecId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is VolumeSpecId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(VolumeSpecId left, VolumeSpecId right) => left.Equals(right);
        public static bool operator !=(VolumeSpecId left, VolumeSpecId right) => !left.Equals(right);
    }

    public readonly struct AttackPatternId
    {
        public readonly string Value;
        public AttackPatternId(string value) { Value = value; }
        public bool Equals(AttackPatternId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is AttackPatternId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(AttackPatternId left, AttackPatternId right) => left.Equals(right);
        public static bool operator !=(AttackPatternId left, AttackPatternId right) => !left.Equals(right);
    }

    public readonly struct MovementPatternId
    {
        public readonly string Value;
        public MovementPatternId(string value) { Value = value; }
        public bool Equals(MovementPatternId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is MovementPatternId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(MovementPatternId left, MovementPatternId right) => left.Equals(right);
        public static bool operator !=(MovementPatternId left, MovementPatternId right) => !left.Equals(right);
    }

    public readonly struct ActionSetId
    {
        public readonly string Value;
        public ActionSetId(string value) { Value = value; }
        public bool Equals(ActionSetId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is ActionSetId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(ActionSetId left, ActionSetId right) => left.Equals(right);
        public static bool operator !=(ActionSetId left, ActionSetId right) => !left.Equals(right);
    }

    public readonly struct DamageChannelId
    {
        public readonly string Value;
        public DamageChannelId(string value) { Value = value; }
        public bool Equals(DamageChannelId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is DamageChannelId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(DamageChannelId left, DamageChannelId right) => left.Equals(right);
        public static bool operator !=(DamageChannelId left, DamageChannelId right) => !left.Equals(right);
    }

    public readonly struct ImpactProfileId
    {
        public readonly string Value;
        public ImpactProfileId(string value) { Value = value; }
        public bool Equals(ImpactProfileId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is ImpactProfileId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(ImpactProfileId left, ImpactProfileId right) => left.Equals(right);
        public static bool operator !=(ImpactProfileId left, ImpactProfileId right) => !left.Equals(right);
    }

    public readonly struct StatusEffectSpecId
    {
        public readonly string Value;
        public StatusEffectSpecId(string value) { Value = value; }
        public bool Equals(StatusEffectSpecId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is StatusEffectSpecId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(StatusEffectSpecId left, StatusEffectSpecId right) => left.Equals(right);
        public static bool operator !=(StatusEffectSpecId left, StatusEffectSpecId right) => !left.Equals(right);
    }

    public readonly struct EncounterDefinitionId
    {
        public readonly string Value;
        public EncounterDefinitionId(string value) { Value = value; }
        public bool Equals(EncounterDefinitionId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is EncounterDefinitionId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(EncounterDefinitionId left, EncounterDefinitionId right) => left.Equals(right);
        public static bool operator !=(EncounterDefinitionId left, EncounterDefinitionId right) => !left.Equals(right);
    }

    public readonly struct EncounterSlotId
    {
        public readonly string Value;
        public EncounterSlotId(string value) { Value = value; }
        public bool Equals(EncounterSlotId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is EncounterSlotId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(EncounterSlotId left, EncounterSlotId right) => left.Equals(right);
        public static bool operator !=(EncounterSlotId left, EncounterSlotId right) => !left.Equals(right);
    }

    public readonly struct FactionId
    {
        public readonly string Value;
        public FactionId(string value) { Value = value; }
        public bool Equals(FactionId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is FactionId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? string.Empty;
        public static bool operator ==(FactionId left, FactionId right) => left.Equals(right);
        public static bool operator !=(FactionId left, FactionId right) => !left.Equals(right);
    }
}
