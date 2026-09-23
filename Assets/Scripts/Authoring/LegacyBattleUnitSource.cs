using ProjectHero.Authoring.Compatibility;

namespace ProjectHero.Authoring
{
    /// <summary>
    /// 单位定义加载来源：主战斗场景里两个旧单位的<strong>纯数值子集</strong>。
    ///
    /// 为什么不是 MonoBehaviour：<c>CombatUnit</c> 编译在 Assembly-CSharp 中，
    /// 自定义 asmdef 不允许引用 Assembly-CSharp（00 号规则 17）。因此这里只承载
    /// <see cref="LegacyUnitStatInputs"/>，由旧程序集中的读取器
    /// （<c>LegacyCombatUnitStatsReader</c>）在加载边界一次性从组件字段解析。
    ///
    /// 阵营完全不由这里决定：阵营写死在 Encounter 槽位上，
    /// 本对象只提供属性数值（Mass / 速度 / 防御 / MaxHealth）。
    /// </summary>
    public sealed class LegacyBattleUnitSource
    {
        public const string LEGACY_UNIT_SOURCE_MISSING = "LEGACY_UNIT_SOURCE_MISSING";

        public LegacyBattleUnitSource(
            LegacyUnitStatInputs heroStats,
            LegacyUnitStatInputs enemyStats,
            string description = null)
        {
            HeroStats = heroStats;
            EnemyStats = enemyStats;
            Description = description ?? string.Empty;
        }

        /// <summary>对应 Encounter 槽位 <c>hero</c> 的旧单位数值。</summary>
        public LegacyUnitStatInputs HeroStats { get; }

        /// <summary>对应 Encounter 槽位 <c>enemy</c> 的旧单位数值。</summary>
        public LegacyUnitStatInputs EnemyStats { get; }

        public string Description { get; }
    }
}
