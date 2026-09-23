using System.Linq;
using NUnit.Framework;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Combat;
using ProjectHero.Core.Grid;
using ProjectHero.Logic;
using ProjectHero.Logic.Grid;
using UnityEngine;

namespace ProjectHero.Authoring.Tests
{
    /// <summary>
    /// 真实 Pattern/Volume 资产的 12 向整数展开垂直切片。
    /// 任务 02 期间位于 Assembly-CSharp-Editor；02B 搬迁闭包后进入 ProjectHero.Authoring.Tests，
    /// 资产改为经 <c>Resources.Load</c> 读取（与 Builder 使用同一加载入口）。
    /// </summary>
    public class LegacyGeometrySliceTests
    {
        private const string SlashPatternR1ResourcePath = "GeneratedActions/Pattern_Slash_R1_0";
        private const string Radius1VolumeResourcePath = "Unit Volume/Radius_1";

        [Test]
        public void LegacyPatternAndVolumeSliceExpandEvenAndOddBasesWithoutFloat()
        {
            var patternAsset = Resources.Load<AttackPattern>(SlashPatternR1ResourcePath);
            Assume.That(patternAsset, Is.Not.Null, $"真实 Pattern 资产缺失：{SlashPatternR1ResourcePath}");
            var volumeAsset = Resources.Load<UnitVolume>(Radius1VolumeResourcePath);
            Assume.That(volumeAsset, Is.Not.Null, $"真实 Volume 资产缺失：{Radius1VolumeResourcePath}");

            var patternSpec = LegacyGridDefinitionConverter.ConvertPattern("pattern.slash.radius_1", patternAsset);
            var volumeSpec = LegacyGridDefinitionConverter.ConvertVolume("unit_volume.hex.radius_1", volumeAsset);

            AssertCanonicalTable(patternSpec.Directions);
            AssertCanonicalTable(volumeSpec.Directions);

            var patternEvenBase = ToLogicPoints(patternAsset.RelativeTriangles);
            var patternOddBase = ToLogicPoints(patternAsset.RelativeTrianglesOdd);
            Assert.That(patternEvenBase.Count, Is.GreaterThan(0), "Pattern 必须提供 East 基准");
            Assert.That(patternOddBase.Count, Is.GreaterThan(0), "Pattern 必须提供 EastNorth 基准");
            for (int k = 0; k < 6; k++)
            {
                AssertDirectionEqualsRotatedBase(patternSpec.Directions[k * 2], patternEvenBase, k);
                AssertDirectionEqualsRotatedBase(patternSpec.Directions[k * 2 + 1], patternOddBase, k);
            }

            var volumeEast = volumeAsset.Volumes.First(v => v.Direction == ProjectHero.Core.Grid.GridDirection.East);
            var volumeEastNorth = volumeAsset.Volumes.First(v => v.Direction == ProjectHero.Core.Grid.GridDirection.EastNorth);
            var volumeEvenBase = ToLogicPoints(volumeEast.RelativeTriangles);
            var volumeOddBase = ToLogicPoints(volumeEastNorth.RelativeTriangles);
            for (int k = 0; k < 6; k++)
            {
                AssertDirectionEqualsRotatedBase(volumeSpec.Directions[k * 2], volumeEvenBase, k);
                AssertDirectionEqualsRotatedBase(volumeSpec.Directions[k * 2 + 1], volumeOddBase, k);
            }

            Assert.That(typeof(DirectionalGeometry).GetMethod("Rotate60CounterClockwise")
                .GetParameters().Single().ParameterType,
                Is.EqualTo(typeof(ProjectHero.Logic.Grid.TrianglePoint)));
        }

        [Test]
        public void LegacyMissingOddBaseIsRejected()
        {
            var asset = ScriptableObject.CreateInstance<AttackPattern>();
            asset.RelativeTriangles.Add(new ProjectHero.Core.Grid.TrianglePoint(3, 0, 1));
            // RelativeTrianglesOdd 留空：旧系统会退回偶数基准近似，新契约必须整体拒绝。
            var ex = Assert.Throws<LogicDefinitionException>(() =>
                LegacyGridDefinitionConverter.ConvertPattern("pattern.missing_odd", asset));
            Assert.That(ex.ErrorCode, Is.EqualTo(DirectionalCodes.DIRECTIONAL_BASE_MISSING),
                "缺少 EastNorth 基准不得用偶数基准猜测 30° 方向");
            Object.DestroyImmediate(asset);
        }

        [Test]
        public void LegacyExplicitVolumeDirectionsAreValidatedAgainstGeneratedTable()
        {
            var asset = ScriptableObject.CreateInstance<UnitVolume>();
            asset.Volumes.Add(new UnitVolume.DirectionalVolume
            {
                Direction = ProjectHero.Core.Grid.GridDirection.East,
                RelativeTriangles = new System.Collections.Generic.List<ProjectHero.Core.Grid.TrianglePoint>
                {
                    new ProjectHero.Core.Grid.TrianglePoint(3, 0, 1)
                }
            });
            asset.Volumes.Add(new UnitVolume.DirectionalVolume
            {
                Direction = ProjectHero.Core.Grid.GridDirection.EastNorth,
                RelativeTriangles = new System.Collections.Generic.List<ProjectHero.Core.Grid.TrianglePoint>
                {
                    new ProjectHero.Core.Grid.TrianglePoint(4, 1, -1)
                }
            });
            // 显式序列化 NorthEast 方向，但与展开结果不一致 → 逐点比较失败，整体拒绝。
            asset.Volumes.Add(new UnitVolume.DirectionalVolume
            {
                Direction = ProjectHero.Core.Grid.GridDirection.NorthEast,
                RelativeTriangles = new System.Collections.Generic.List<ProjectHero.Core.Grid.TrianglePoint>
                {
                    new ProjectHero.Core.Grid.TrianglePoint(5, 6, -1) // 合法点但内容与展开结果不符
                }
            });
            var ex = Assert.Throws<LogicDefinitionException>(() =>
                LegacyGridDefinitionConverter.ConvertVolume("unit_volume.mismatch", asset));
            Assert.That(ex.ErrorCode, Is.EqualTo(DirectionalCodes.DIRECTIONAL_TABLE_MISMATCH),
                "显式序列化方向只能作为校验输入，与生成结果逐点比较");

            // 一致时通过。
            asset.Volumes[2].RelativeTriangles =
                new System.Collections.Generic.List<ProjectHero.Core.Grid.TrianglePoint>
                {
                    new ProjectHero.Core.Grid.TrianglePoint(1, 2, -1) // (3,0,1) 旋转 60° 的结果
                };
            var ok = LegacyGridDefinitionConverter.ConvertVolume("unit_volume.match", asset);
            Assert.That(ok.Directions.Count, Is.EqualTo(12));
            Object.DestroyImmediate(asset);
        }

        private static void AssertCanonicalTable(
            System.Collections.Generic.IReadOnlyList<DirectionalTriangleSet> directions)
        {
            Assert.That(directions.Count, Is.EqualTo(12));
            for (int i = 0; i < 12; i++)
            {
                Assert.That(directions[i].Direction,
                    Is.EqualTo((ProjectHero.Logic.Grid.GridDirection)i));
                Assert.That(directions[i].Triangles, Is.Not.Empty);
            }
            Assert.That(DirectionalSpecValidation.ValidateDirections(directions), Is.Null,
                "真实资产展开结果必须通过规范校验");
        }

        private static void AssertDirectionEqualsRotatedBase(
            DirectionalTriangleSet set,
            System.Collections.Generic.List<ProjectHero.Logic.Grid.TrianglePoint> basePoints,
            int rotationSteps)
        {
            var expected = basePoints
                .Select(p =>
                {
                    var q = p;
                    for (int i = 0; i < rotationSteps; i++)
                        q = DirectionalGeometry.Rotate60CounterClockwise(q);
                    return q;
                })
                .OrderBy(p => p)
                .ToArray();
            Assert.That(set.Triangles.ToArray(), Is.EqualTo(expected),
                $"{set.Direction} 必须等于基准旋转 {rotationSteps} 次的规范表");
        }

        private static System.Collections.Generic.List<ProjectHero.Logic.Grid.TrianglePoint> ToLogicPoints(
            System.Collections.Generic.List<ProjectHero.Core.Grid.TrianglePoint> legacyPoints)
            => legacyPoints
                .Select(p => new ProjectHero.Logic.Grid.TrianglePoint(p.X, p.Y, p.T))
                .ToList();
    }
}
