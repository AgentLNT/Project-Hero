using System;
using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Combat;
using ProjectHero.Core.Grid;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProjectHero.Authoring.Tests
{
    public sealed class SpatialShapeConfigurationTests
    {
        private static List<TrianglePoint> Even => new List<TrianglePoint>
        {
            new TrianglePoint(3, 0, 1), new TrianglePoint(1, 0, -1), new TrianglePoint(1, 0, 1)
        };
        private static List<TrianglePoint> Odd => new List<TrianglePoint>
        {
            new TrianglePoint(2, 1, 1), new TrianglePoint(0, 1, -1)
        };

        [TestCase(SpatialShapeKind.UnitVolume)]
        [TestCase(SpatialShapeKind.AttackPattern)]
        public void JsonRoundTrip_PreservesAllTwelveCanonicalDirections(SpatialShapeKind kind)
        {
            var even = Even;
            even.Add(even[0]);
            var config = SpatialShapeConfiguration.Create("test.shape", kind, even, Odd);
            string json = JsonUtility.ToJson(config);
            var restored = SpatialShapeConfiguration.Parse(json);
            Assert.That(restored.Validate(), Is.EqualTo(kind));
            Assert.That(restored.directions.Length, Is.EqualTo(12));
            Assert.That(restored.directions[0].triangles.Length, Is.EqualTo(3));
            Assert.That(JsonUtility.ToJson(restored), Is.EqualTo(json));
        }

        [Test]
        public void Export_RejectsMissingBasisAndIllegalTriangle()
        {
            Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() =>
                SpatialShapeConfiguration.Create("test", SpatialShapeKind.UnitVolume, Even, new List<TrianglePoint>()));
            Assert.Throws<ProjectHero.Logic.LogicDefinitionException>(() =>
                SpatialShapeConfiguration.Create("test", SpatialShapeKind.UnitVolume,
                    new[] { new TrianglePoint(0, 0, 1) }, Odd));
        }

        [Test]
        public void Import_RejectsTamperedGeneratedDirection()
        {
            var config = SpatialShapeConfiguration.Create("test", SpatialShapeKind.UnitVolume, Even, Odd);
            config.directions[4].triangles[0] = new TrianglePoint(99, 0, 1);
            Assert.Throws<ArgumentException>(() => SpatialShapeConfiguration.Parse(JsonUtility.ToJson(config)));
        }

        [Test]
        public void Import_RejectsWrongVersionMissingDirectionAndUnknownKind()
        {
            var config = SpatialShapeConfiguration.Create("test", SpatialShapeKind.UnitVolume, Even, Odd);
            config.version = 2;
            Assert.Throws<ArgumentException>(() => config.Validate());
            config.version = 1; config.kind = "0";
            Assert.Throws<ArgumentException>(() => config.Validate());
            config.kind = "UnitVolume"; config.directions[11] = null;
            Assert.Throws<ArgumentException>(() => config.Validate());
        }

        [Test]
        public void Apply_InvalidConfigurationDoesNotModifyAsset()
        {
            var asset = ScriptableObject.CreateInstance<AttackPattern>();
            try
            {
                asset.RelativeTriangles = Even;
                var before = JsonUtility.ToJson(asset);
                var config = SpatialShapeConfiguration.Create("test", SpatialShapeKind.AttackPattern, Even, Odd);
                config.directions[9].direction = 1;
                Assert.Throws<ArgumentException>(() => config.ApplyTo(asset));
                Assert.That(JsonUtility.ToJson(asset), Is.EqualTo(before));
            }
            finally { Object.DestroyImmediate(asset); }
        }

        [Test]
        public void SavedAssets_AreConsumedByExistingConverters()
        {
            var volume = ScriptableObject.CreateInstance<UnitVolume>();
            var pattern = ScriptableObject.CreateInstance<AttackPattern>();
            try
            {
                SpatialShapeConfiguration.Create("test.volume", SpatialShapeKind.UnitVolume, Even, Odd).ApplyTo(volume);
                SpatialShapeConfiguration.Create("test.pattern", SpatialShapeKind.AttackPattern, Even, Odd).ApplyTo(pattern);
                var actualVolume = LegacyGridDefinitionConverter.ConvertVolume("test.volume", volume);
                var actualPattern = LegacyGridDefinitionConverter.ConvertPattern("test.pattern", pattern);
                var expected = SpatialShapeConfiguration.Expand(Even, Odd);
                Assert.That(volume.Volumes.Count, Is.EqualTo(12));
                Assert.That(volume.ConfigurationId, Is.EqualTo("test.volume"));
                Assert.That(pattern.ConfigurationId, Is.EqualTo("test.pattern"));
                for (int i = 0; i < 12; i++)
                {
                    CollectionAssert.AreEqual(expected[i].Triangles, actualVolume.Directions[i].Triangles);
                    CollectionAssert.AreEqual(expected[i].Triangles, actualPattern.Directions[i].Triangles);
                }
                Assert.Throws<ArgumentException>(() =>
                    SpatialShapeConfiguration.Create("test", SpatialShapeKind.UnitVolume, Even, Odd).ApplyTo(pattern));
            }
            finally { Object.DestroyImmediate(volume); Object.DestroyImmediate(pattern); }
        }

        [Test]
        public void ReferenceVolume_JsonRoundTripPreservesIdAndDoesNotAddVolumeCellsToAttack()
        {
            var config = SpatialShapeConfiguration.Create("test.attack", SpatialShapeKind.AttackPattern,
                Even, Odd, "unit_volume.test");
            var restored = SpatialShapeConfiguration.Parse(JsonUtility.ToJson(config));
            Assert.That(restored.referenceVolumeId, Is.EqualTo("unit_volume.test"));
            CollectionAssert.AreEqual(SpatialShapeConfiguration.Expand(Even, Odd)[0].Triangles,
                SpatialShapeConfiguration.ToLogic(restored.directions[0].triangles));
        }

        [Test]
        public void ReferenceVolume_AssetPersistsReferenceAndConverterUsesOnlyAttackGeometry()
        {
            string volumePath = AssetDatabase.GenerateUniqueAssetPath("Assets/Tests/EditMode/Authoring/SpatialShapeVolumeProbe.asset");
            string patternPath = AssetDatabase.GenerateUniqueAssetPath("Assets/Tests/EditMode/Authoring/SpatialShapePatternProbe.asset");
            var volume = ScriptableObject.CreateInstance<UnitVolume>();
            var pattern = ScriptableObject.CreateInstance<AttackPattern>();
            try
            {
                SpatialShapeConfiguration.Create("unit_volume.test", SpatialShapeKind.UnitVolume, Odd, Even).ApplyTo(volume);
                AssetDatabase.CreateAsset(volume, volumePath);
                var config = SpatialShapeConfiguration.Create("test.attack", SpatialShapeKind.AttackPattern, Even, Odd, "unit_volume.test");
                config.ApplyTo(pattern, volume);
                AssetDatabase.CreateAsset(pattern, patternPath);
                AssetDatabase.SaveAssetIfDirty(pattern);
                Resources.UnloadAsset(pattern);
                pattern = AssetDatabase.LoadAssetAtPath<AttackPattern>(patternPath);
                Assert.That(pattern.AuthoringVolume, Is.EqualTo(volume));
                var converted = LegacyGridDefinitionConverter.ConvertPattern("test.attack", pattern);
                CollectionAssert.AreEqual(SpatialShapeConfiguration.Expand(Even, Odd)[0].Triangles, converted.Directions[0].Triangles);
            }
            finally
            {
                AssetDatabase.DeleteAsset(patternPath);
                AssetDatabase.DeleteAsset(volumePath);
                if (pattern != null && !AssetDatabase.Contains(pattern)) Object.DestroyImmediate(pattern);
                if (volume != null && !AssetDatabase.Contains(volume)) Object.DestroyImmediate(volume);
            }
        }

        [Test]
        public void ExistingAssetCatalog_CanRoundTripWithoutChangingGeometry()
        {
            foreach (var entry in LegacyIdMigrationManifest.Patterns)
            {
                var asset = AssetDatabase.LoadAssetAtPath<AttackPattern>(AssetDatabase.GUIDToAssetPath(entry.AssetGuid));
                Assert.That(asset, Is.Not.Null, entry.AssetName);
                var expected = LegacyGridDefinitionConverter.ConvertPattern(entry.FinalAttackPatternId, asset);
                var file = SpatialShapeConfiguration.Create(entry.FinalAttackPatternId, SpatialShapeKind.AttackPattern,
                    asset.RelativeTriangles, asset.RelativeTrianglesOdd);
                AssertTable(file, expected.Directions);
            }
            foreach (var entry in LegacyIdMigrationManifest.Volumes)
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnitVolume>(AssetDatabase.GUIDToAssetPath(entry.AssetGuid));
                Assert.That(asset, Is.Not.Null, entry.AssetName);
                var expected = LegacyGridDefinitionConverter.ConvertVolume(entry.FinalVolumeSpecId, asset);
                var file = SpatialShapeConfiguration.Create(entry.FinalVolumeSpecId, SpatialShapeKind.UnitVolume,
                    SpatialShapeConfiguration.ToLegacy(expected.Directions[0].Triangles),
                    SpatialShapeConfiguration.ToLegacy(expected.Directions[1].Triangles));
                AssertTable(file, expected.Directions);
            }
        }

        private static void AssertTable(SpatialShapeConfiguration config,
            IReadOnlyList<ProjectHero.Logic.Grid.DirectionalTriangleSet> table)
        {
            var restored = SpatialShapeConfiguration.Parse(JsonUtility.ToJson(config));
            for (int i = 0; i < 12; i++)
                CollectionAssert.AreEqual(table[i].Triangles, SpatialShapeConfiguration.ToLogic(restored.directions[i].triangles));
        }
    }
}
