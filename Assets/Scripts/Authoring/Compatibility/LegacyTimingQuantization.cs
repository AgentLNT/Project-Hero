using ProjectHero.Logic.Combat;

namespace ProjectHero.Authoring.Compatibility
{
    /// <summary>
    /// 旧秒值 → 整数 Tick 的唯一量化边界。
    /// 秒只存在于 Authoring 输入；战斗开始后禁止再次从 float 秒或旧 MonoBehaviour 属性推导逻辑值
    /// （主方案 3.1.2 / 00 号规则 6）。每个秒值在转换时恰好量化一次。
    /// </summary>
    public static class LegacyTimingQuantization
    {
        /// <summary>RoundHalfUp(seconds × TicksPerSecond)，要求有限正秒且结果 ≥ 1 Tick。</summary>
        public static int SecondsToTicks(double seconds, BattleRules rules)
        {
            return TickQuantization.SecondsToTicks(seconds, rules.TicksPerSecond);
        }
    }
}
