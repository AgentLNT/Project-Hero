using ProjectHero.Authoring;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Core.Entities;
using ProjectHero.Logic;

namespace ProjectHero.Core.Compatibility.Authoring
{
    /// <summary>
    /// 旧运行时组件 → Authoring 数值子集的读取器（留在 Assembly-CSharp）。
    ///
    /// 这是唯一需要 <c>CombatUnit</c> 的 Authoring 接缝：自定义 asmdef 不能引用
    /// Assembly-CSharp，所以"读组件字段"这一步必须留在旧程序集里；
    /// 之后的全部映射、校验与定义装配都在 <c>ProjectHero.Authoring</c> 中完成。
    ///
    /// 稳定性契约：
    /// <list type="bullet">
    /// <item>必须恰好找到 2 个 CombatUnit，否则显式失败——不做"取第一个"式兜底；</item>
    /// <item>只有调用方未显式给出槽位对应关系时才用 <c>IsPlayerControlled</c> 作为
    /// <strong>定位旧场景对象的线索</strong>；该布尔值不进入 Logic、不决定阵营、不决定胜负。</item>
    /// </list>
    /// </summary>
    public static class LegacyCombatUnitStatsReader
    {
        public const string LEGACY_UNIT_COUNT_INVALID = "LEGACY_UNIT_COUNT_INVALID";

        /// <summary>读取单个组件的数值子集（不保留任何 Unity 对象引用）。</summary>
        public static LegacyUnitStatInputs Read(CombatUnit unit)
        {
            if (unit == null)
                throw new LogicDefinitionException(LegacyBattleUnitSource.LEGACY_UNIT_SOURCE_MISSING, "unit is null");

            return new LegacyUnitStatInputs(
                unit.Strength,
                unit.Dexterity,
                unit.Constitution,
                unit.ArmorWeight,
                unit.ArmorDefense,
                unit.MagicResistance,
                unit.IsExhausted);
        }

        /// <summary>
        /// 从当前已加载场景读取两个 CombatUnit 的数值子集。
        /// 调用方可以显式指定哪个组件对应哪个固定槽位。
        /// </summary>
        public static LegacyBattleUnitSource ReadFromActiveScene(
            CombatUnit heroUnit = null, CombatUnit enemyUnit = null)
        {
            if (heroUnit == null || enemyUnit == null)
            {
                var units = UnityEngine.Object.FindObjectsByType<CombatUnit>();
                if (units == null || units.Length != 2)
                    throw new LogicDefinitionException(LEGACY_UNIT_COUNT_INVALID,
                        "expected exactly 2 CombatUnit, found " + (units == null ? 0 : units.Length));

                if (heroUnit == null && enemyUnit == null)
                {
                    foreach (var unit in units)
                    {
                        if (unit.IsPlayerControlled) heroUnit = unit;
                        else enemyUnit = unit;
                    }
                }
            }

            if (heroUnit == null || enemyUnit == null)
                throw new LogicDefinitionException(LegacyBattleUnitSource.LEGACY_UNIT_SOURCE_MISSING,
                    "hero/enemy unit could not be resolved from the active scene");

            return new LegacyBattleUnitSource(Read(heroUnit), Read(enemyUnit), "ActiveScene");
        }
    }
}
