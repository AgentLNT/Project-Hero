using System.Collections.Generic;
using ProjectHero.Authoring;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Authoring.Tests
{
    /// <summary>
    /// 02B 测试夹具：全部来自<strong>真实资产</strong>（Resources 下的旧 ActionLibrary /
    /// AttackPattern / UnitVolume）与主战斗场景两个单位的真实旧字段值。
    /// 任务 03 可直接复用 <see cref="Definition"/> 作为内核测试的真实定义。
    /// </summary>
    public static class BattleDefinitionFixture
    {
        private static BattleDefinitionBuildResult _cached;

        /// <summary>主战斗场景 Player 的旧字段（任务 01 §1.3 记录值）。</summary>
        public static LegacyUnitStatInputs HeroStats => new LegacyUnitStatInputs(
            strength: 10f, dexterity: 10f, constitution: 10f,
            armorWeight: 10f, armorDefense: 0f, magicResistance: 0f, isExhausted: false);

        /// <summary>主战斗场景 Enemy 的旧字段（与 Player 相同，只是半径变体不同）。</summary>
        public static LegacyUnitStatInputs EnemyStats => HeroStats;

        public static LegacyBattleUnitSource UnitSource
            => new LegacyBattleUnitSource(HeroStats, EnemyStats, "02B fixture");

        /// <summary>真实资产集合（全部 16 个资产；顺序会被归一化）。</summary>
        public static LegacyAssetSet Assets => LegacyAssetResolver.LoadAllFromResources();

        /// <summary>构建主战斗定义（结果按资产内容缓存）。</summary>
        public static BattleDefinitionBuildResult Build()
            => BattleDefinitionBuilder.BuildMainBattleDefinition(Assets, UnitSource);

        /// <summary>已构建的主战斗定义（构建失败时断言失败并给出稳定错误码）。</summary>
        public static BattleDefinition Definition
        {
            get
            {
                if (_cached == null) _cached = Build();
                NUnit.Framework.Assert.That(_cached.Succeeded, NUnit.Framework.Is.True,
                    "主战斗定义必须构建成功，错误码：" + string.Join(",", _cached.ErrorCodes()));
                return _cached.Definition;
            }
        }

        public static EncounterDefinition MainEncounter
            => Definition.FindEncounter(new EncounterDefinitionId(LegacyIdMigrationManifest.MainEncounterId));

        public static BattleRuntimeInputs RuntimeInputs
            => new BattleRuntimeInputs(InitialRngSeed: 20260922UL,
                InitialMetaResource: BattleDefinitionBuilder.MainEncounterInitialMetaResource);
    }
}
