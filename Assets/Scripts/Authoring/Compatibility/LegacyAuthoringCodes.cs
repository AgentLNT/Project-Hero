namespace ProjectHero.Authoring.Compatibility
{
    /// <summary>
    /// 旧资产兼容转换的稳定错误码（读取旧资产/旧字段失败时的拒绝码）。
    /// 任务 02B 搬迁完整 Authoring 依赖闭包后，这些适配器已进入 <c>ProjectHero.Authoring</c>；
    /// 只有需要 MonoBehaviour 类型（CombatUnit）的读取器仍留在 Assembly-CSharp。
    /// </summary>
    public static class LegacyAuthoringCodes
    {
        public const string LEGACY_MOMENTUM_INPUT_INVALID = "LEGACY_MOMENTUM_INPUT_INVALID";
        public const string LEGACY_RESISTANCE_INPUT_INVALID = "LEGACY_RESISTANCE_INPUT_INVALID";
        public const string LEGACY_HEALTH_INPUT_INVALID = "LEGACY_HEALTH_INPUT_INVALID";
        public const string LEGACY_ACTION_NOT_ATTACK = "LEGACY_ACTION_NOT_ATTACK";
        public const string LEGACY_PATTERN_MISSING = "LEGACY_PATTERN_MISSING";
    }
}
