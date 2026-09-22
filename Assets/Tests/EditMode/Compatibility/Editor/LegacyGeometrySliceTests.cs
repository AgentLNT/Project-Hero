using System.Linq;
using NUnit.Framework;
using ProjectHero.Core.Combat;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Core.Grid;
using ProjectHero.Logic;
using ProjectHero.Logic.Grid;
using UnityEditor;
using UnityEngine;

namespace ProjectHero.Tests.Compatibility.Editor
{
    /// <summary>真实 Pattern/Volume 资产的 12 向整数展开垂直切片（任务 02「必须产出」13）。</summary>
    public class LegacyGeometrySliceTests
    {
        private const string SlashPatternR1Path = "Assets/Resources/GeneratedActions/Pattern_Slash_R1_0.asset";
        private const string Radius1VolumePath = "Assets/Resources/Unit Volume/Radius_1.asset";

        [Test]
        public void LegacyPatternAndVolumeSliceExpandEvenAndOddBasesWithoutFloat()
        {
            var patternAsset = AssetDatabase.LoadAssetAtPath<AttackPattern>(SlashPatternR1Path);
            Assume.That(patternAsset, Is.Not.Null, $"真实 Pattern 资产缺失：{SlashPatternR1Path}");
            var volumeAsset = AssetDatabase.LoadAssetAtPath<UnitVolume>(Radius1VolumePath);
            Assume.That(volumeAsset, Is.Not.Null, $"真实 Volume 资产缺失：{Radius1VolumePath}");

            var patternSpec = LegacyGridDefinitionConverter.ConvertPattern("pattern.slash.r1", patternAsset);
            var volumeSpec = LegacyGridDefinitionConverter.ConvertVolume("volume.radius_1", volumeAsset);

            // 规范 12 向表：Directions[i].Direction == (GridDirection)i。
            AssertCanonicalTable(patternSpec.Directions);
            AssertCanonicalTable(volumeSpec.Directions);

            // 偶数方向 = East 基准旋转 k 次；奇数方向 = EastNorth 基准旋转 k 次（无浮点展开）。
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

            // 无浮点依赖：展开只经 DirectionalGeometry 的受检整数公式（签名已在 Logic 侧测试锁定）。
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
                LegacyGridDefinitionConverter.ConvertVolume("volume.mismatch", asset));
            Assert.That(ex.ErrorCode, Is.EqualTo(DirectionalCodes.DIRECTIONAL_TABLE_MISMATCH),
                "显式序列化方向只能作为校验输入，与生成结果逐点比较");

            // 一致时通过。
            asset.Volumes[2].RelativeTriangles =
                new System.Collections.Generic.List<ProjectHero.Core.Grid.TrianglePoint>
                {
                    new ProjectHero.Core.Grid.TrianglePoint(1, 2, -1) // (3,0,1) 旋转 60° 的结果
                };
            var ok = LegacyGridDefinitionConverter.ConvertVolume("volume.match", asset);
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
