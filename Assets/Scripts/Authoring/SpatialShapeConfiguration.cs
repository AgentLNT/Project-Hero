using System;
using System.Collections.Generic;
using ProjectHero.Core.Combat;
using ProjectHero.Core.Grid;
using ProjectHero.Logic.Grid;
using UnityEngine;
using Point = ProjectHero.Logic.Grid.TrianglePoint;
using LegacyPoint = ProjectHero.Core.Grid.TrianglePoint;

namespace ProjectHero.Authoring
{
    public enum SpatialShapeKind { UnitVolume, AttackPattern }

    /// <summary>Versioned exchange file. Directions contain the same canonical integer table as Logic.</summary>
    [Serializable]
    public sealed class SpatialShapeConfiguration
    {
        public int version;
        public string id;
        public string kind;
        public string referenceVolumeId;
        public Direction[] directions;

        [Serializable]
        public sealed class Direction
        {
            public int direction;
            public LegacyPoint[] triangles;
        }

        public static SpatialShapeConfiguration Create(string id, SpatialShapeKind kind,
            IReadOnlyList<LegacyPoint> even, IReadOnlyList<LegacyPoint> odd, string referenceVolumeId = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("配置 ID 不能为空。");
            if (!Enum.IsDefined(typeof(SpatialShapeKind), kind)) throw new ArgumentException("配置类型无效。");
            var table = Expand(even, odd);
            var result = new SpatialShapeConfiguration
            {
                version = 1, id = id.Trim(), kind = kind.ToString(), directions = new Direction[12],
                referenceVolumeId = kind == SpatialShapeKind.AttackPattern ? referenceVolumeId : null
            };
            for (int i = 0; i < 12; i++)
                result.directions[i] = new Direction { direction = i, triangles = ToLegacy(table[i].Triangles).ToArray() };
            return result;
        }

        public static IReadOnlyList<DirectionalTriangleSet> Expand(
            IReadOnlyList<LegacyPoint> even, IReadOnlyList<LegacyPoint> odd)
            => DirectionalGeometry.ExpandFromBases(ToLogic(even), ToLogic(odd));

        public static List<Point> ToLogic(IReadOnlyList<LegacyPoint> points)
        {
            if (points == null) throw new ArgumentException("缺少三角格点列表。");
            var result = new List<Point>(points.Count);
            foreach (var p in points) result.Add(new Point(p.X, p.Y, p.T));
            return result;
        }

        public static List<LegacyPoint> ToLegacy(IReadOnlyList<Point> points)
        {
            var result = new List<LegacyPoint>(points.Count);
            foreach (var p in points) result.Add(new LegacyPoint(p.X, p.Y, p.T));
            return result;
        }

        public static SpatialShapeConfiguration Parse(string json)
        {
            var result = JsonUtility.FromJson<SpatialShapeConfiguration>(json);
            if (result == null) throw new ArgumentException("JSON 配置为空。");
            result.Validate();
            return result;
        }

        public SpatialShapeKind Validate()
        {
            if (version != 1) throw new ArgumentException("不支持的配置版本，当前仅支持 version=1。");
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("配置 ID 不能为空。");
            if (!Enum.TryParse(kind, out SpatialShapeKind parsed) || !Enum.IsDefined(typeof(SpatialShapeKind), parsed)
                || kind != parsed.ToString()) throw new ArgumentException("kind 必须是 UnitVolume 或 AttackPattern。");
            if (directions == null || directions.Length != 12) throw new ArgumentException("必须包含 12 个朝向。");
            for (int i = 0; i < 12; i++)
                if (directions[i] == null || directions[i].direction != i || directions[i].triangles == null)
                    throw new ArgumentException("朝向必须按 0～11 排列，且包含三角格点列表。");
            var expected = Expand(directions[0].triangles, directions[1].triangles);
            for (int i = 0; i < 12; i++)
            {
                var actual = ToLogic(directions[i].triangles);
                if (actual.Count != expected[i].Triangles.Count) throw new ArgumentException($"朝向 {i} 与基准展开不一致。");
                for (int j = 0; j < actual.Count; j++)
                    if (actual[j] != expected[i].Triangles[j])
                        throw new ArgumentException($"朝向 {i} 必须与基准展开一致，按 (X,Y,T) 排序且不重复。");
            }
            return parsed;
        }

        /// <summary>Write only after validation, so malformed imports never partially modify an asset.</summary>
        public void ApplyTo(ScriptableObject asset, UnitVolume referenceVolume = null)
        {
            var type = Validate();
            if (type == SpatialShapeKind.AttackPattern && asset is AttackPattern pattern)
            {
                pattern.RelativeTriangles = new List<LegacyPoint>(directions[0].triangles);
                pattern.RelativeTrianglesOdd = new List<LegacyPoint>(directions[1].triangles);
                pattern.ConfigurationId = id;
                pattern.AuthoringVolume = referenceVolume;
            }
            else if (type == SpatialShapeKind.UnitVolume && asset is UnitVolume volume)
            {
                var volumes = new List<UnitVolume.DirectionalVolume>();
                foreach (var dir in directions)
                    volumes.Add(new UnitVolume.DirectionalVolume
                    {
                        Direction = (ProjectHero.Core.Grid.GridDirection)dir.direction,
                        RelativeTriangles = new List<LegacyPoint>(dir.triangles)
                    });
                volume.Volumes = volumes;
                volume.ConfigurationId = id;
            }
            else throw new ArgumentException("配置类型与目标资产类型不一致。");
        }
    }
}
