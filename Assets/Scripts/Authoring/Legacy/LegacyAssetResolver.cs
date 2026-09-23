using System.Collections.Generic;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Combat;
using ProjectHero.Core.Grid;
using UnityEngine;

namespace ProjectHero.Authoring.Legacy
{
    /// <summary>
    /// 旧资产加载结果。
    ///
    /// 发现顺序无关性由这里保证：<c>Resources.LoadAll</c> 的返回顺序不做任何承诺，
    /// 因此加载后立刻按 <strong>资产 GUID 的 <see cref="System.StringComparer.Ordinal"/> 升序</strong>
    /// 重排，再交给 Builder。任何调用方传入的自定义顺序也在这里被归一化。
    /// 找不到 GUID 的资产以资产名（Ordinal）排序并追加在末尾，绝不影响已识别资产的相对顺序。
    /// </summary>
    public static class LegacyAssetResolver
    {
        public const string ASSET_IDENTITY_UNRESOLVED = "ASSET_IDENTITY_UNRESOLVED";

        /// <summary>从 Resources 读取全部旧配置资产（唯一运行时加载入口，不依赖 AssetDatabase）。</summary>
        public static LegacyAssetSet LoadAllFromResources()
        {
            return new LegacyAssetSet(
                OrderByGuid(Resources.LoadAll<ActionLibrarySO>(LegacyAssetIdentity.ActionLibraryFolder)),
                OrderByGuid(Resources.LoadAll<AttackPattern>(LegacyAssetIdentity.GeneratedActionsFolder)),
                OrderByGuid(Resources.LoadAll<UnitVolume>(LegacyAssetIdentity.UnitVolumeFolder)));
        }

        /// <summary>用显式给出的资产构造集合（测试与 Editor 工具用；顺序同样被归一化）。</summary>
        public static LegacyAssetSet Create(
            IEnumerable<ActionLibrarySO> libraries,
            IEnumerable<AttackPattern> patterns,
            IEnumerable<UnitVolume> volumes)
        {
            return new LegacyAssetSet(
                OrderByGuid(libraries),
                OrderByGuid(patterns),
                OrderByGuid(volumes));
        }

        private static List<KeyValuePair<string, T>> OrderByGuid<T>(IEnumerable<T> assets) where T : Object
        {
            var pairs = new List<KeyValuePair<string, T>>();
            if (assets == null) return pairs;

            foreach (var asset in assets)
            {
                if (asset == null) continue;
                pairs.Add(new KeyValuePair<string, T>(ResolveGuid(asset), asset));
            }

            pairs.Sort((a, b) => System.StringComparer.Ordinal.Compare(a.Key, b.Key));
            return pairs;
        }

        /// <summary>
        /// 解析资产 GUID：Editor 下取真实 GUID；构建产物（没有 AssetDatabase）退回
        /// "由迁移清单声明的资产名 → GUID"映射。两条路径必须得到同一组 GUID，
        /// 否则以 <see cref="ASSET_IDENTITY_UNRESOLVED"/> 标记，Builder 会据此显式拒绝。
        /// </summary>
        public static string ResolveGuid(Object asset)
        {
            string name = asset != null ? asset.name : null;
#if UNITY_EDITOR
            if (asset != null &&
                UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long _) &&
                !string.IsNullOrEmpty(guid))
            {
                return guid;
            }
#endif
            return ManifestGuidForName(name) ?? ASSET_IDENTITY_UNRESOLVED + ":" + (name ?? "<null>");
        }

        /// <summary>按资产名在迁移清单中查 GUID（清单是唯一权威名称→GUID 来源）。</summary>
        public static string ManifestGuidForName(string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return null;

            switch (assetName)
            {
                case "ForRadius1": return LegacyIdMigrationManifest.ForRadius1Guid;
                case "ForRadius2": return LegacyIdMigrationManifest.ForRadius2Guid;
                case "ForRadius3": return LegacyIdMigrationManifest.ForRadius3Guid;
                case "Pattern_Cleave_R1_0": return LegacyIdMigrationManifest.PatternCleaveR1Guid;
                case "Pattern_Cleave_R2_0": return LegacyIdMigrationManifest.PatternCleaveR2Guid;
                case "Pattern_Slash_R1_0": return LegacyIdMigrationManifest.PatternSlashR1Guid;
                case "Pattern_Slash_R2_0": return LegacyIdMigrationManifest.PatternSlashR2Guid;
                case "Pattern_Smash_R1_0": return LegacyIdMigrationManifest.PatternSmashR1Guid;
                case "Pattern_Smash_R2_0": return LegacyIdMigrationManifest.PatternSmashR2Guid;
                case "Pattern_Thrust_R1_0": return LegacyIdMigrationManifest.PatternThrustR1Guid;
                case "Pattern_Thrust_R2_0": return LegacyIdMigrationManifest.PatternThrustR2Guid;
                case "Pattern_Whirlwind_R1_0": return LegacyIdMigrationManifest.PatternWhirlwindR1Guid;
                case "Pattern_Whirlwind_R2_0": return LegacyIdMigrationManifest.PatternWhirlwindR2Guid;
                case "Radius_1": return LegacyIdMigrationManifest.VolumeRadius1Guid;
                case "Radius_2": return LegacyIdMigrationManifest.VolumeRadius2Guid;
                case "Radius_3": return LegacyIdMigrationManifest.VolumeRadius3Guid;
                default: return null;
            }
        }
    }
}
