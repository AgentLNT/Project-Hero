namespace ProjectHero.Logic.Ids
{
    /// <summary>
    /// 控制者稳定配置 ID（主方案 2.2.1 的配置定义 ID 分类之一）。
    /// 与 <see cref="FactionId"/> 正交：它只标识"谁来下命令"，
    /// 不隐含阵营、不隐含胜负、不隐含目标资格，也绝不能被用来推断敌我。
    /// </summary>
    public readonly struct ControllerId
    {
        public readonly string Value;

        public ControllerId(string value) { Value = value; }

        public bool Equals(ControllerId other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is ControllerId other && Equals(other);

        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();

        public override string ToString() => Value ?? string.Empty;

        public static bool operator ==(ControllerId left, ControllerId right) => left.Equals(right);

        public static bool operator !=(ControllerId left, ControllerId right) => !left.Equals(right);
    }
}
