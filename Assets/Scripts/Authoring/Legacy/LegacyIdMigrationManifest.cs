using System.Collections.Generic;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Combat;
using ProjectHero.Core.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Authoring.Legacy
{
    /// <summary>
    /// 旧条目定位键：<c>资产 GUID + 局部条目 ID</c>。
    ///
    /// 任务 02B 的 ID 迁移规则明确：GUID 与局部条目 ID <strong>只</strong>用于在 Authoring/Editor 边界
    /// 定位旧条目，最终 Logic ID 必须是人工确认的稳定小写字符串。
    /// GUID 不得进入 Logic 类型、BattleDefinition、规范化快照或 Replay。
    /// </summary>
    public readonly struct LegacyEntryKey
    {
        public readonly string AssetGuid;
        public readonly string LocalId;

        public LegacyEntryKey(string assetGuid, string localId)
        {
            AssetGuid = assetGuid ?? string.Empty;
            LocalId = localId ?? string.Empty;
        }

        public override string ToString() => AssetGuid + "#" + LocalId;
    }

    /// <summary>一条已人工确认的动作 ID 迁移记录。</summary>
    public sealed class ActionIdMigration
    {
        public ActionIdMigration(
            string assetGuid, string assetName, string localId, string finalActionSpecId,
            string patternAssetGuid, string finalAttackPatternId)
        {
            Key = new LegacyEntryKey(assetGuid, localId);
            AssetName = assetName;
            FinalActionSpecId = finalActionSpecId;
            PatternAssetGuid = patternAssetGuid ?? string.Empty;
            FinalAttackPatternId = finalAttackPatternId ?? string.Empty;
        }

        public LegacyEntryKey Key { get; }
        public string AssetName { get; }

        /// <summary>人工确认的最终稳定 ActionSpecId（小写字母/数字/点/下划线）。</summary>
        public string FinalActionSpecId { get; }

        /// <summary>该动作引用的 Pattern 资产 GUID（仅用于迁移定位与一致性校验）。</summary>
        public string PatternAssetGuid { get; }

        /// <summary>人工确认的最终稳定 AttackPatternId。</summary>
        public string FinalAttackPatternId { get; }
    }

    /// <summary>一条已人工确认的 Pattern ID 记录。</summary>
    public sealed class PatternIdMigration
    {
        public PatternIdMigration(string assetGuid, string assetName, string finalAttackPatternId)
        {
            AssetGuid = assetGuid ?? string.Empty;
            AssetName = assetName;
            FinalAttackPatternId = finalAttackPatternId;
        }

        public string AssetGuid { get; }
        public string AssetName { get; }
        public string FinalAttackPatternId { get; }
    }

    /// <summary>一条已人工确认的 UnitVolume ID 记录。</summary>
    public sealed class VolumeIdMigration
    {
        public VolumeIdMigration(string assetGuid, string assetName, string finalVolumeSpecId)
        {
            AssetGuid = assetGuid ?? string.Empty;
            AssetName = assetName;
            FinalVolumeSpecId = finalVolumeSpecId;
        }

        public string AssetGuid { get; }
        public string AssetName { get; }
        public string FinalVolumeSpecId { get; }
    }

    /// <summary>
    /// 显式旧 ID 迁移清单（任务包「必须产出」3）。
    ///
    /// 设计要点：
    /// <list type="bullet">
    /// <item>不存在"把局部 ID 自动转成全局 ID"的路径。每个最终 ID 都在这里<strong>逐条人工写死</strong>，
    /// 不做 <c>ToLowerInvariant</c>、不做文件名推导、不做最后写入覆盖。</item>
    /// <item><c>QuickSlash</c> 同时存在于 <c>ForRadius1</c> 与 <c>ForRadius2</c>：两条记录映射到
    /// <strong>不同</strong>的最终 ID（<c>action.quick_slash.radius_1</c> / <c>..._2</c>），因为两者
    /// 指向的 Pattern 形状不同（8▲ vs 14▲），规范定义不同。</item>
    /// <item>AttackPattern / UnitVolume 资产没有 ID 字段（身份只靠文件名），因此它们的最终 ID
    /// 完全由本清单给出，不从资产名推导。</item>
    /// </list>
    /// </summary>
    public static class LegacyIdMigrationManifest
    {
        // —— 资产 GUID（仅迁移定位用；任务 01 记录 §1.2 的原始表）——
        public const string ForRadius1Guid = "9ea53fc4064c1594796601d06dff0ac9";
        public const string ForRadius2Guid = "d09b70a28dd2ced43bf159a1a12b29bf";
        public const string ForRadius3Guid = "6e64b254bcf0a25428f0efc237c0f7bf";

        public const string PatternCleaveR1Guid = "291cdb608889dc042b5e622d77a9f794";
        public const string PatternCleaveR2Guid = "e31792b18b25fc54584b7d1c9b87e3d8";
        public const string PatternSlashR1Guid = "476b68c23aee80645b5c81c50cd2abb2";
        public const string PatternSlashR2Guid = "e80d7761622c08d458f98c274dd7a7f4";
        public const string PatternSmashR1Guid = "872797ef83a8d13488fbb8d109ca365f";
        public const string PatternSmashR2Guid = "4aa4cdfdbdcef0348b1520ab222a6d4a";
        public const string PatternThrustR1Guid = "b3d713a61713abd439306c87da89adc0";
        public const string PatternThrustR2Guid = "cc8de6ceea7a3f242b9a0e8483d6222c";
        public const string PatternWhirlwindR1Guid = "960e6645f09f9b4428f7d0c5490492ed";
        public const string PatternWhirlwindR2Guid = "47719ed4b12fdf94990db9e92b7e40f5";

        public const string VolumeRadius1Guid = "e7b3456b17272bf4d882a50c38d433e2";
        public const string VolumeRadius2Guid = "25dd84dc5108e4d40a90528a9ecb301c";
        public const string VolumeRadius3Guid = "faaf1464668507348836395402a34bca";

        // —— 人工确认的最终稳定 ID ——
        public const string ActionQuickSlashR1 = "action.quick_slash.radius_1";
        public const string ActionQuickSlashR2 = "action.quick_slash.radius_2";
        public const string ActionHeavySmashR1 = "action.heavy_smash.radius_1";
        public const string ActionHeavySmashR2 = "action.heavy_smash.radius_2";
        public const string ActionWideCleaveR1 = "action.wide_cleave.radius_1";
        public const string ActionWideCleaveR2 = "action.wide_cleave.radius_2";
        public const string ActionSpearThrustR1 = "action.spear_thrust.radius_1";
        public const string ActionSpearThrustR2 = "action.spear_thrust.radius_2";
        public const string ActionWhirlwindR1 = "action.whirlwind.radius_1";
        public const string ActionWhirlwindR2 = "action.whirlwind.radius_2";

        // —— 非攻击动作：旧系统没有对应资产，全部为本任务新增的显式定义 ——
        public const string ActionGuard = "action.guard.default";
        public const string ActionMove = "action.move.default";
        public const string ActionBlock = "action.block.default";
        public const string ActionDodge = "action.dodge.default";

        // —— 动作集合 ——
        public const string ActionSetRadius1 = "action_set.radius_1";
        public const string ActionSetRadius2 = "action_set.radius_2";

        // —— 单位定义（场景内 CombatUnit；不是 Prefab）——
        public const string UnitHeroRadius1 = "unit.hero.radius_1";
        public const string UnitMonsterRadius2 = "unit.monster.radius_2";

        // —— Encounter ——
        public const string MainEncounterId = "encounter.combat_sample_scene";
        public const string SlotHero = "hero";
        public const string SlotEnemy = "enemy";
        public const string ControllerPlayer = "controller.player";
        public const string ControllerEnemyAi = "controller.enemy_ai";

        /// <summary>主战斗场景资产路径（用于迁移记录与 Editor 定位）。</summary>
        public const string MainScenePath = "Assets/Scenes/CombatSampleScene.unity";

        private static readonly Dictionary<string, string> PatternNamesByGuid = new Dictionary<string, string>
        {
            [PatternCleaveR1Guid] = "Pattern_Cleave_R1_0",
            [PatternCleaveR2Guid] = "Pattern_Cleave_R2_0",
            [PatternSlashR1Guid] = "Pattern_Slash_R1_0",
            [PatternSlashR2Guid] = "Pattern_Slash_R2_0",
            [PatternSmashR1Guid] = "Pattern_Smash_R1_0",
            [PatternSmashR2Guid] = "Pattern_Smash_R2_0",
            [PatternThrustR1Guid] = "Pattern_Thrust_R1_0",
            [PatternThrustR2Guid] = "Pattern_Thrust_R2_0",
            [PatternWhirlwindR1Guid] = "Pattern_Whirlwind_R1_0",
            [PatternWhirlwindR2Guid] = "Pattern_Whirlwind_R2_0"
        };

        /// <summary>
        /// 动作迁移表。键是 (资产 GUID, 局部 ID)，值是人工确认的最终稳定 ID 与其 Pattern 映射。
        /// 顺序按 (GUID, 局部 ID) 的 Ordinal 排序，因此本表本身与资产发现顺序无关。
        /// </summary>
        public static IReadOnlyList<ActionIdMigration> Actions { get; } = new List<ActionIdMigration>
        {
            new ActionIdMigration(ForRadius1Guid, "ForRadius1", "QuickSlash", ActionQuickSlashR1, PatternSlashR1Guid, "pattern.slash.radius_1"),
            new ActionIdMigration(ForRadius1Guid, "ForRadius1", "HeavySmash", ActionHeavySmashR1, PatternSmashR1Guid, "pattern.smash.radius_1"),
            new ActionIdMigration(ForRadius1Guid, "ForRadius1", "WideCleave", ActionWideCleaveR1, PatternCleaveR1Guid, "pattern.cleave.radius_1"),
            new ActionIdMigration(ForRadius1Guid, "ForRadius1", "SpearThrust", ActionSpearThrustR1, PatternThrustR1Guid, "pattern.thrust.radius_1"),
            new ActionIdMigration(ForRadius1Guid, "ForRadius1", "Whirlwind", ActionWhirlwindR1, PatternWhirlwindR1Guid, "pattern.whirlwind.radius_1"),
            new ActionIdMigration(ForRadius2Guid, "ForRadius2", "QuickSlash", ActionQuickSlashR2, PatternSlashR2Guid, "pattern.slash.radius_2"),
            new ActionIdMigration(ForRadius2Guid, "ForRadius2", "HeavySmash", ActionHeavySmashR2, PatternSmashR2Guid, "pattern.smash.radius_2"),
            new ActionIdMigration(ForRadius2Guid, "ForRadius2", "WideCleave", ActionWideCleaveR2, PatternCleaveR2Guid, "pattern.cleave.radius_2"),
            new ActionIdMigration(ForRadius2Guid, "ForRadius2", "SpearThrust", ActionSpearThrustR2, PatternThrustR2Guid, "pattern.thrust.radius_2"),
            new ActionIdMigration(ForRadius2Guid, "ForRadius2", "Whirlwind", ActionWhirlwindR2, PatternWhirlwindR2Guid, "pattern.whirlwind.radius_2")
        };

        /// <summary>Pattern 迁移表（10 个资产，无旧 ID 字段，最终 ID 由本表给出）。</summary>
        public static IReadOnlyList<PatternIdMigration> Patterns { get; } = new List<PatternIdMigration>
        {
            new PatternIdMigration(PatternSlashR1Guid, "Pattern_Slash_R1_0", "pattern.slash.radius_1"),
            new PatternIdMigration(PatternSlashR2Guid, "Pattern_Slash_R2_0", "pattern.slash.radius_2"),
            new PatternIdMigration(PatternSmashR1Guid, "Pattern_Smash_R1_0", "pattern.smash.radius_1"),
            new PatternIdMigration(PatternSmashR2Guid, "Pattern_Smash_R2_0", "pattern.smash.radius_2"),
            new PatternIdMigration(PatternCleaveR1Guid, "Pattern_Cleave_R1_0", "pattern.cleave.radius_1"),
            new PatternIdMigration(PatternCleaveR2Guid, "Pattern_Cleave_R2_0", "pattern.cleave.radius_2"),
            new PatternIdMigration(PatternThrustR1Guid, "Pattern_Thrust_R1_0", "pattern.thrust.radius_1"),
            new PatternIdMigration(PatternThrustR2Guid, "Pattern_Thrust_R2_0", "pattern.thrust.radius_2"),
            new PatternIdMigration(PatternWhirlwindR1Guid, "Pattern_Whirlwind_R1_0", "pattern.whirlwind.radius_1"),
            new PatternIdMigration(PatternWhirlwindR2Guid, "Pattern_Whirlwind_R2_0", "pattern.whirlwind.radius_2")
        };

        /// <summary>UnitVolume 迁移表（只有 R1/R2 被主战斗消费；R3 保留但未消费）。</summary>
        public static IReadOnlyList<VolumeIdMigration> Volumes { get; } = new List<VolumeIdMigration>
        {
            new VolumeIdMigration(VolumeRadius1Guid, "Radius_1", "unit_volume.hex.radius_1"),
            new VolumeIdMigration(VolumeRadius2Guid, "Radius_2", "unit_volume.hex.radius_2"),
            new VolumeIdMigration(VolumeRadius3Guid, "Radius_3", "unit_volume.hex.radius_3")
        };

        /// <summary>
        /// 迁移清单中被正式消费的资产 GUID 集合（14 个：2 库 + 10 Pattern + 2 Volume）。
        /// 未列入的资产（ForRadius3 空库、Radius_3）仍在清单中保留最终 ID，
        /// 但不被任何 Encounter 引用。
        /// </summary>
        public static IReadOnlyList<string> ConsumedAssetGuids { get; } = new List<string>
        {
            ForRadius1Guid, ForRadius2Guid,
            PatternSlashR1Guid, PatternSlashR2Guid,
            PatternSmashR1Guid, PatternSmashR2Guid,
            PatternCleaveR1Guid, PatternCleaveR2Guid,
            PatternThrustR1Guid, PatternThrustR2Guid,
            PatternWhirlwindR1Guid, PatternWhirlwindR2Guid,
            VolumeRadius1Guid, VolumeRadius2Guid
        };

        /// <summary>按 (GUID, 局部 ID) 查动作迁移记录；不存在返回 null。</summary>
        public static ActionIdMigration FindAction(string assetGuid, string localId)
        {
            foreach (var migration in Actions)
            {
                if (string.Equals(migration.Key.AssetGuid, assetGuid, System.StringComparison.Ordinal) &&
                    string.Equals(migration.Key.LocalId, localId, System.StringComparison.Ordinal))
                    return migration;
            }
            return null;
        }

        /// <summary>按资产 GUID 查 Pattern 迁移记录；不存在返回 null。</summary>
        public static PatternIdMigration FindPattern(string assetGuid)
        {
            foreach (var migration in Patterns)
            {
                if (string.Equals(migration.AssetGuid, assetGuid, System.StringComparison.Ordinal))
                    return migration;
            }
            return null;
        }

        /// <summary>按资产 GUID 查 Volume 迁移记录；不存在返回 null。</summary>
        public static VolumeIdMigration FindVolume(string assetGuid)
        {
            foreach (var migration in Volumes)
            {
                if (string.Equals(migration.AssetGuid, assetGuid, System.StringComparison.Ordinal))
                    return migration;
            }
            return null;
        }

        public static string PatternNameFor(string assetGuid)
            => PatternNamesByGuid.TryGetValue(assetGuid ?? string.Empty, out var name) ? name : assetGuid;
    }

    /// <summary>
    /// 迁移定位辅助：把"资产实例"绑定回它的 GUID。
    /// Editor 下走 <c>AssetDatabase.TryGetGUIDAndLocalFileIdentifier</c>；
    /// 运行时（Resources 加载）由 <see cref="LegacyAssetSet"/> 在发现阶段记录。
    /// </summary>
    public static class LegacyAssetIdentity
    {
        /// <summary>Resources 下的资产根目录（旧系统唯一的资产加载根）。</summary>
        public const string ActionLibraryFolder = "ActionLibrary";
        public const string GeneratedActionsFolder = "GeneratedActions";
        public const string UnitVolumeFolder = "Unit Volume";
    }

    /// <summary>
    /// 一个已解析的旧资产 + 它的迁移定位 GUID。
    /// 集合本身按 GUID 的 Ordinal 升序排列，因此"发现顺序"不可能影响下游结果。
    /// </summary>
    public sealed class LegacyAssetSet
    {
        private readonly Dictionary<string, ActionLibrarySO> _librariesByGuid = new Dictionary<string, ActionLibrarySO>();
        private readonly Dictionary<string, AttackPattern> _patternsByGuid = new Dictionary<string, AttackPattern>();
        private readonly Dictionary<string, UnitVolume> _volumesByGuid = new Dictionary<string, UnitVolume>();

        public LegacyAssetSet(
            IEnumerable<KeyValuePair<string, ActionLibrarySO>> libraries,
            IEnumerable<KeyValuePair<string, AttackPattern>> patterns,
            IEnumerable<KeyValuePair<string, UnitVolume>> volumes)
        {
            foreach (var pair in libraries) _librariesByGuid[pair.Key] = pair.Value;
            foreach (var pair in patterns) _patternsByGuid[pair.Key] = pair.Value;
            foreach (var pair in volumes) _volumesByGuid[pair.Key] = pair.Value;
        }

        public IReadOnlyDictionary<string, ActionLibrarySO> LibrariesByGuid => _librariesByGuid;
        public IReadOnlyDictionary<string, AttackPattern> PatternsByGuid => _patternsByGuid;
        public IReadOnlyDictionary<string, UnitVolume> VolumesByGuid => _volumesByGuid;

        public ActionLibrarySO Library(string guid)
            => _librariesByGuid.TryGetValue(guid, out var value) ? value : null;

        public AttackPattern Pattern(string guid)
            => _patternsByGuid.TryGetValue(guid, out var value) ? value : null;

        public UnitVolume Volume(string guid)
            => _volumesByGuid.TryGetValue(guid, out var value) ? value : null;
    }
}
