using System;
using System.Collections.Generic;
using ProjectHero.Core.Combat;
using ProjectHero.Core.Grid;
using ProjectHero.Logic;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Core.Compatibility.Authoring
{
    /// <summary>
    /// 旧 AttackPattern / UnitVolume（ScriptableObject）→ 规范 12 向整数表（AttackPatternSpec / VolumeSpec）的
    /// 兼容转换接缝。使用主方案 2.3.1 冻结的受检整数 60° 展开（East 基准 → 偶数方向、EastNorth 基准 → 奇数方向），
    /// 全程无浮点/三角函数；缺少任一基准、非法三角格点、溢出或显式序列化方向与生成结果不一致时整体拒绝。
    /// 本适配器仍编译在 Assembly-CSharp（读取旧类型的适配器暂留旧程序集，主方案 2.1）。
    /// </summary>
    public static class LegacyGridDefinitionConverter
    {
        public const string LEGACY_GRID_DIRECTION_INVALID = "LEGACY_GRID_DIRECTION_INVALID";

        /// <summary>
        /// 旧攻击 Pattern → AttackPatternSpec。旧资产提供 East（偶数基准）与 EastNorth（奇数基准）两套三角形。
        /// </summary>
        public static AttackPatternSpec ConvertPattern(string patternId, AttackPattern asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            var evenBase = ToLogicPoints(asset.RelativeTriangles);
            var oddBase = ToLogicPoints(asset.RelativeTrianglesOdd);
            IReadOnlyList<DirectionalTriangleSet> directions = DirectionalGeometry.ExpandFromBases(evenBase, oddBase);
            return new AttackPatternSpec(new AttackPatternId(patternId), directions);
        }

        /// <summary>
        /// 旧单位体积 → VolumeSpec。基准必须显式序列化 East 与 EastNorth；
        /// 旧资产显式序列化的其他方向只作为校验输入，与展开后的规范结果逐点比较。
        /// </summary>
        public static VolumeSpec ConvertVolume(string volumeId, UnitVolume asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));

            List<ProjectHero.Core.Grid.TrianglePoint> evenBase = FindBase(asset, ProjectHero.Core.Grid.GridDirection.East);
            List<ProjectHero.Core.Grid.TrianglePoint> oddBase = FindBase(asset, ProjectHero.Core.Grid.GridDirection.EastNorth);

            IReadOnlyList<DirectionalTriangleSet> directions =
                DirectionalGeometry.ExpandFromBases(ToLogicPoints(evenBase), ToLogicPoints(oddBase));

            ValidateExplicitDirections(asset, directions);

            return new VolumeSpec(new VolumeSpecId(volumeId), directions);
        }

        private static List<ProjectHero.Core.Grid.TrianglePoint> FindBase(UnitVolume asset, ProjectHero.Core.Grid.GridDirection direction)
        {
            foreach (var vol in asset.Volumes)
            {
                if (vol.Direction == direction && vol.RelativeTriangles != null && vol.RelativeTriangles.Count > 0)
                    return vol.RelativeTriangles;
            }
            return new List<ProjectHero.Core.Grid.TrianglePoint>();
        }

        /// <summary>
        /// 旧资产显式序列化的非基准方向只能作为校验输入：与展开生成的规范结果逐点比较，
        /// 不一致以 DIRECTIONAL_TABLE_MISMATCH 拒绝；不得用加载顺序决定覆盖结果（主方案 2.3.1）。
        /// </summary>
        private static void ValidateExplicitDirections(
            UnitVolume asset, IReadOnlyList<DirectionalTriangleSet> generated)
        {
            foreach (var vol in asset.Volumes)
            {
                if (vol.Direction == ProjectHero.Core.Grid.GridDirection.East ||
                    vol.Direction == ProjectHero.Core.Grid.GridDirection.EastNorth)
                    continue;

                int index = (int)vol.Direction;
                if (!GridDirectionInfo.IsValidIndex(index))
                    throw new LogicDefinitionException(LEGACY_GRID_DIRECTION_INVALID, index.ToString());

                var expected = ToLogicPoints(vol.RelativeTriangles);
                var canonicalExpected = DirectionalGeometry.Canonicalize(expected);
                var generatedSet = generated[index];
                if (generatedSet.Triangles.Count != canonicalExpected.Count)
                    throw new LogicDefinitionException(DirectionalCodes.DIRECTIONAL_TABLE_MISMATCH,
                        $"direction={index}");
                for (int i = 0; i < canonicalExpected.Count; i++)
                {
                    if (generatedSet.Triangles[i] != canonicalExpected[i])
                        throw new LogicDefinitionException(DirectionalCodes.DIRECTIONAL_TABLE_MISMATCH,
                            $"direction={index}");
                }
            }
        }

        private static List<ProjectHero.Logic.Grid.TrianglePoint> ToLogicPoints(
            IReadOnlyList<ProjectHero.Core.Grid.TrianglePoint> legacyPoints)
        {
            if (legacyPoints == null) return new List<ProjectHero.Logic.Grid.TrianglePoint>();
            var result = new List<ProjectHero.Logic.Grid.TrianglePoint>(legacyPoints.Count);
            foreach (var p in legacyPoints)
            {
                // 构造期受检：T ∈ {-1,1} 且 X + Y + T 为偶数，非法点以 TRIANGLE_POINT_INVALID 拒绝
                result.Add(new ProjectHero.Logic.Grid.TrianglePoint(p.X, p.Y, p.T));
            }
            return result;
        }
    }
}
