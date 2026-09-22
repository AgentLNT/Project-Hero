namespace ProjectHero.Core.Compatibility.Authoring
{
    /// <summary>
    /// 旧程序集兼容适配器的稳定错误码（读取旧资产/旧字段失败时的拒绝码）。
    /// 这些适配器仍编译在预定义 Assembly-CSharp 中（asmdef 不能引用 Assembly-CSharp，
    /// 主方案 2.1）；任务 02B 搬迁完整 Authoring 依赖闭包后由正式 Builder 接管。
    /// </summary>
    public static class LegacyAuthoringCodes
    {
        public const string LEGACY_MOMENTUM_INPUT_INVALID = "LEGACY_MOMENTUM_INPUT_INVALID";
        public const string LEGACY_RESISTANCE_INPUT_INVALID = "LEGACY_RESISTANCE_INPUT_INVALID";
        public const string LEGACY_ACTION_NOT_ATTACK = "LEGACY_ACTION_NOT_ATTACK";
        public const string LEGACY_PATTERN_MISSING = "LEGACY_PATTERN_MISSING";
    }
}
