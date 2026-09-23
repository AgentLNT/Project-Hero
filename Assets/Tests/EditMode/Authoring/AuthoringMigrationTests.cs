using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Authoring;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Combat;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Physics;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using LogicGridDirection = ProjectHero.Logic.Grid.GridDirection;

namespace ProjectHero.Authoring.Tests
{
    /// <summary>
    /// 02B 必需测试（第一批）：ID 迁移、动作/Pattern/Volume 全量转换、12 向受检整数展开、
    /// 定义规范化与哈希边界。
    /// </summary>
    public class AuthoringMigrationTests
    {
        // ============================================================
        // 1. ID 迁移规则
        // ============================================================

        [Test]
        public void EveryReferencedLegacyDefinitionHasExplicitStableId()
        {
            var definition = BattleDefinitionFixture.Definition;

            // 每个动作、Pattern、Volume、单位、动作集合、Encounter、槽位、Controller、阵营的 ID
            // 都必须满足小写字母/数字/点/下划线的稳定格式。
            foreach (var action in definition.Actions)
                Assert.That(DefinitionIdValidation.IsValidFormat(action.ActionSpecId.Value), Is.True, action.ActionSpecId.Value);
            foreach (var pattern in definition.AttackPatterns)
                Assert.That(DefinitionIdValidation.IsValidFormat(pattern.AttackPatternId.Value), Is.True, pattern.AttackPatternId.Value);
            foreach (var volume in definition.Volumes)
                Assert.That(DefinitionIdValidation.IsValidFormat(volume.VolumeSpecId.Value), Is.True, volume.VolumeSpecId.Value);
            foreach (var unit in definition.Units)
            {
                Assert.That(DefinitionIdValidation.IsValidFormat(unit.UnitDefinitionId.Value), Is.True, unit.UnitDefinitionId.Value);
                Assert.That(DefinitionIdValidation.IsValidFormat(unit.ActionSetId.Value), Is.True, unit.ActionSetId.Value);
            }
            foreach (var set in definition.ActionSets)
                Assert.That(DefinitionIdValidation.IsValidFormat(set.ActionSetId.Value), Is.True, set.ActionSetId.Value);

            var encounter = BattleDefinitionFixture.MainEncounter;
            Assert.That(DefinitionIdValidation.IsValidFormat(encounter.EncounterId.Value), Is.True);
            foreach (var slot in encounter.Slots)
            {
                Assert.That(DefinitionIdValidation.IsValidFormat(slot.SlotId.Value), Is.True, slot.SlotId.Value);
                Assert.That(DefinitionIdValidation.IsValidFormat(slot.DefinitionId.Value), Is.True, slot.DefinitionId.Value);
                Assert.That(DefinitionIdValidation.IsValidFormat(slot.FactionId.Value), Is.True, slot.FactionId.Value);
            }
            foreach (var controller in encounter.Controllers)
                Assert.That(DefinitionIdValidation.IsValidFormat(controller.ControllerId.Value), Is.True);

            // 每个被消费的旧动作条目都能在显式迁移清单里定位（GUID + 局部 ID）。
            foreach (var migration in LegacyIdMigrationManifest.Actions)
            {
                Assert.That(migration.Key.AssetGuid, Has.Length.EqualTo(32));
                Assert.That(migration.Key.LocalId, Is.Not.Empty);
                Assert.That(migration.FinalActionSpecId, Is.Not.Empty);
                Assert.That(definition.Actions.Any(a => a.ActionSpecId.Value == migration.FinalActionSpecId),
                    Is.True, migration.FinalActionSpecId);
            }

            // GUID 不得进入 Logic 定义（不出现 32 位十六进制串）。
            foreach (var action in definition.Actions)
            {
                Assert.That(action.ActionSpecId.Value, Does.Not.Match("^[0-9a-f]{32}$"));
            }
        }

        [Test]
        public void LegacyLocalIdIsNotImplicitlyTreatedAsGlobalId()
        {
            var definition = BattleDefinitionFixture.Definition;

            // 旧局部 ID（QuickSlash 等）绝不能直接成为全局 ActionSpecId：
            // 它们既不在定义里，也不满足"以小写开头"的命名约定。
            string[] legacyLocalIds = { "QuickSlash", "HeavySmash", "WideCleave", "SpearThrust", "Whirlwind" };
            foreach (var localId in legacyLocalIds)
            {
                Assert.That(definition.Actions.Any(a => a.ActionSpecId.Value == localId), Is.False,
                    $"旧局部 ID {localId} 不得直接成为 ActionSpecId");
                Assert.That(DefinitionIdValidation.IsValidFormat(localId), Is.False,
                    $"旧局部 ID {localId} 含大写字母，本就不满足稳定 ID 格式");
            }

            // 人工确认的最终 ID 采用 action.<snake_case>.radius_<n> 形态。
            Assert.That(definition.Actions.Any(a => a.ActionSpecId.Value == "action.quick_slash.radius_1"), Is.True);
            Assert.That(definition.Actions.Any(a => a.ActionSpecId.Value == "action.quick_slash.radius_2"), Is.True);
        }

        [Test]
        public void AmbiguousLegacyLocalIdRequiresExplicitMapping()
        {
            // QuickSlash 同时存在于 ForRadius1 与 ForRadius2：迁移清单必须给出两条不同映射，
            // 不能靠"最后写入者获胜"或自动小写合并成一条。
            var quickSlashMappings = LegacyIdMigrationManifest.Actions
                .Where(m => m.Key.LocalId == "QuickSlash")
                .ToArray();

            Assert.That(quickSlashMappings.Length, Is.EqualTo(2), "同名局部 ID 必须逐库各有一条映射");
            Assert.That(quickSlashMappings.Select(m => m.Key.AssetGuid).Distinct().Count(), Is.EqualTo(2));
            Assert.That(quickSlashMappings.Select(m => m.FinalActionSpecId).Distinct().Count(), Is.EqualTo(2),
                "跨库同名条目必须映射到不同最终 ID（形状不同 → 规范定义不同）");

            // 两个半径变体都实际存在于定义中，且各自的 Pattern 不同。
            var definition = BattleDefinitionFixture.Definition;
            var r1 = definition.FindAction(new ActionSpecId("action.quick_slash.radius_1"));
            var r2 = definition.FindAction(new ActionSpecId("action.quick_slash.radius_2"));
            Assert.That(r1, Is.Not.Null);
            Assert.That(r2, Is.Not.Null);

            var payloadR1 = (AttackPayloadSpec)r1.Payload;
            var payloadR2 = (AttackPayloadSpec)r2.Payload;
            Assert.That(payloadR1.Pattern.AttackPatternId.Value, Is.EqualTo("pattern.slash.radius_1"));
            Assert.That(payloadR2.Pattern.AttackPatternId.Value, Is.EqualTo("pattern.slash.radius_2"));

            // 旧资产事实：R1 的 East 基准 8 个三角形、R2 为 14 个 → 不是同一配置。
            Assert.That(DirectionPointCount(payloadR1.Pattern, LogicGridDirection.East), Is.EqualTo(8));
            Assert.That(DirectionPointCount(payloadR2.Pattern, LogicGridDirection.East), Is.EqualTo(14));

            // 迁移清单本身必须无重复最终 ID。
            var finalIds = LegacyIdMigrationManifest.Actions.Select(m => m.FinalActionSpecId).ToArray();
            Assert.That(finalIds.Distinct().Count(), Is.EqualTo(finalIds.Length), "迁移清单最终 ID 不得重复");
        }

        [Test]
        public void SameFinalIdWithDifferentContentIsRejected()
        {
            // 冲突检测的核心：同一最终 ID 对应不同规范内容必须以 DEFINITION_ID_CONTENT_CONFLICT 拒绝。
            string id = "action.conflict_probe";
            string[] signatureA = { "action|windup=30,recovery=30,damage=15" };
            string[] signatureB = { "action|windup=90,recovery=30,damage=40" };

            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.DetectContentConflicts(
                new[] { (id, signatureA), (id, signatureB) }, errors);

            Assert.That(errors.HasErrors, Is.True, "同 ID 不同内容必须产生冲突");
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.DEFINITION_ID_CONTENT_CONFLICT));

            // 同样内容重复出现（幂等重建）不得误报。
            var okErrors = new DefinitionErrorCollector();
            BattleDefinitionValidator.DetectContentConflicts(
                new[] { (id, signatureA), (id, signatureA) }, okErrors);
            Assert.That(okErrors.HasErrors, Is.False, "逐字相同的规范内容不得被判为冲突");

            // 真实定义里不存在任何同 ID 冲突（构建成功即为证据）。
            Assert.That(BattleDefinitionFixture.Build().Succeeded, Is.True);
        }

        // ============================================================
        // 2. 全量资产转换
        // ============================================================

        [Test]
        public void AllReferencedActionLibrariesConvert()
        {
            var assets = BattleDefinitionFixture.Assets;
            Assert.That(assets.LibrariesByGuid.Count, Is.EqualTo(3), "三个 ActionLibrarySO 都必须被发现");

            var definition = BattleDefinitionFixture.Definition;

            // 两个被 Encounter 引用的库：各 5 条攻击动作全部转换，无跳过、无回退。
            var attackActions = definition.Actions
                .Where(a => a.Type == ProjectHero.Logic.Actions.ActionType.Attack)
                .OrderBy(a => a.ActionSpecId.Value, StringComparer.Ordinal)
                .ToArray();

            Assert.That(attackActions.Length, Is.EqualTo(10), "R1/R2 两库共 10 条攻击动作全部转换");
            Assert.That(attackActions.Select(a => a.ActionSpecId.Value).Distinct().Count(), Is.EqualTo(10));
            Assert.That(attackActions.Count(a => a.ActionSpecId.Value.EndsWith(".radius_1", StringComparison.Ordinal)),
                Is.EqualTo(5));
            Assert.That(attackActions.Count(a => a.ActionSpecId.Value.EndsWith(".radius_2", StringComparison.Ordinal)),
                Is.EqualTo(5));
        }

        [Test]
        public void AllReferencedPatternsAndVolumesConvert()
        {
            var assets = BattleDefinitionFixture.Assets;
            Assert.That(assets.PatternsByGuid.Count, Is.EqualTo(10), "10 个 AttackPattern 资产");
            Assert.That(assets.VolumesByGuid.Count, Is.EqualTo(3), "3 个 UnitVolume 资产（含未被消费的 Radius_3）");

            var definition = BattleDefinitionFixture.Definition;
            Assert.That(definition.AttackPatterns.Count, Is.EqualTo(10));
            Assert.That(definition.Volumes.Count, Is.EqualTo(3));

            // 每个规范表都必须通过 12 向完整性校验。
            foreach (var pattern in definition.AttackPatterns)
                Assert.That(DirectionalSpecValidation.ValidateDirections(pattern.Directions), Is.Null,
                    pattern.AttackPatternId.Value);
            foreach (var volume in definition.Volumes)
                Assert.That(DirectionalSpecValidation.ValidateDirections(volume.Directions), Is.Null,
                    volume.VolumeSpecId.Value);
        }

        // ============================================================
        // 3. 12 向受检整数展开
        // ============================================================

        [Test]
        public void LegacyEvenAndOddBasesExpandToCanonicalTwelveDirections()
        {
            var assets = BattleDefinitionFixture.Assets;

            foreach (var pair in assets.PatternsByGuid)
            {
                var migration = LegacyIdMigrationManifest.FindPattern(pair.Key);
                Assert.That(migration, Is.Not.Null, pair.Key);

                var spec = LegacyGridDefinitionConverter.ConvertPattern(
                    migration.FinalAttackPatternId, pair.Value);

                Assert.That(spec.Directions.Count, Is.EqualTo(GridDirectionInfo.DirectionCount));
                for (int i = 0; i < spec.Directions.Count; i++)
                {
                    Assert.That(spec.Directions[i].Direction, Is.EqualTo((LogicGridDirection)i));
                    Assert.That(spec.Directions[i].Triangles, Is.Not.Empty);
                }
                Assert.That(DirectionalSpecValidation.ValidateDirections(spec.Directions), Is.Null);

                // 偶数方向来自 East 基准、奇数方向来自 EastNorth 基准（各连续旋转 0..5 次）。
                var evenBase = ToLogicPoints(pair.Value.RelativeTriangles);
                var oddBase = ToLogicPoints(pair.Value.RelativeTrianglesOdd);
                for (int step = 0; step < 6; step++)
                {
                    Assert.That(spec.Directions[step * 2].Triangles.ToArray(),
                        Is.EqualTo(RotateBase(evenBase, step)), $"{migration.FinalAttackPatternId} even step {step}");
                    Assert.That(spec.Directions[step * 2 + 1].Triangles.ToArray(),
                        Is.EqualTo(RotateBase(oddBase, step)), $"{migration.FinalAttackPatternId} odd step {step}");
                }
            }
        }

        [Test]
        public void MissingEvenOrOddDirectionalBaseStopsDefinitionBuild()
        {
            // 只给偶数基准（East），EastNorth 留空 → DIRECTIONAL_BASE_MISSING。
            var asset = UnityEngine.ScriptableObject.CreateInstance<AttackPattern>();
            try
            {
                asset.RelativeTriangles.Add(new ProjectHero.Core.Grid.TrianglePoint(3, 0, 1));

                var ex = Assert.Throws<LogicDefinitionException>(() =>
                    LegacyGridDefinitionConverter.ConvertPattern("pattern.missing_odd_probe", asset));
                Assert.That(ex.ErrorCode, Is.EqualTo(DirectionalCodes.DIRECTIONAL_BASE_MISSING));

                // 反向：只给奇数基准也必须拒绝。
                var asset2 = UnityEngine.ScriptableObject.CreateInstance<AttackPattern>();
                try
                {
                    asset2.RelativeTrianglesOdd.Add(new ProjectHero.Core.Grid.TrianglePoint(4, 1, -1));
                    var ex2 = Assert.Throws<LogicDefinitionException>(() =>
                        LegacyGridDefinitionConverter.ConvertPattern("pattern.missing_even_probe", asset2));
                    Assert.That(ex2.ErrorCode, Is.EqualTo(DirectionalCodes.DIRECTIONAL_BASE_MISSING));
                }
                finally { UnityEngine.Object.DestroyImmediate(asset2); }
            }
            finally { UnityEngine.Object.DestroyImmediate(asset); }

            // Volume 侧同理：缺少基准方向同样拒绝。
            var volume = UnityEngine.ScriptableObject.CreateInstance<UnitVolume>();
            try
            {
                volume.Volumes.Add(new UnitVolume.DirectionalVolume
                {
                    Direction = ProjectHero.Core.Grid.GridDirection.East,
                    RelativeTriangles = new List<ProjectHero.Core.Grid.TrianglePoint>
                    {
                        new ProjectHero.Core.Grid.TrianglePoint(1, 0, 1)
                    }
                });
                var ex = Assert.Throws<LogicDefinitionException>(() =>
                    LegacyGridDefinitionConverter.ConvertVolume("unit_volume.missing_odd_probe", volume));
                Assert.That(ex.ErrorCode, Is.EqualTo(DirectionalCodes.DIRECTIONAL_BASE_MISSING));
            }
            finally { UnityEngine.Object.DestroyImmediate(volume); }
        }

        [Test]
        public void InvalidTriangleOrientationOrParityStopsDefinitionBuild()
        {
            // T = 0：不是合法朝向。
            var badT = UnityEngine.ScriptableObject.CreateInstance<AttackPattern>();
            try
            {
                badT.RelativeTriangles.Add(new ProjectHero.Core.Grid.TrianglePoint(3, 0, 0));
                badT.RelativeTrianglesOdd.Add(new ProjectHero.Core.Grid.TrianglePoint(4, 1, -1));
                var ex = Assert.Throws<LogicDefinitionException>(() =>
                    LegacyGridDefinitionConverter.ConvertPattern("pattern.bad_t", badT));
                Assert.That(ex.ErrorCode, Is.EqualTo(DirectionalCodes.TRIANGLE_POINT_INVALID));
            }
            finally { UnityEngine.Object.DestroyImmediate(badT); }

            // X + Y + T 为奇数：奇偶约束失败。
            var badParity = UnityEngine.ScriptableObject.CreateInstance<AttackPattern>();
            try
            {
                badParity.RelativeTriangles.Add(new ProjectHero.Core.Grid.TrianglePoint(2, 0, 1));
                badParity.RelativeTrianglesOdd.Add(new ProjectHero.Core.Grid.TrianglePoint(4, 1, -1));
                var ex = Assert.Throws<LogicDefinitionException>(() =>
                    LegacyGridDefinitionConverter.ConvertPattern("pattern.bad_parity", badParity));
                Assert.That(ex.ErrorCode, Is.EqualTo(DirectionalCodes.TRIANGLE_POINT_INVALID));
            }
            finally { UnityEngine.Object.DestroyImmediate(badParity); }

            // Logic 侧同样拒绝：构造期校验即抛 TRIANGLE_POINT_INVALID。
            Assert.That(ProjectHero.Logic.Grid.TrianglePoint.IsValid(3, 0, 0), Is.False);
            Assert.That(ProjectHero.Logic.Grid.TrianglePoint.IsValid(2, 0, 1), Is.False);
            Assert.That(ProjectHero.Logic.Grid.TrianglePoint.IsValid(3, 0, 1), Is.True);
        }

        [Test]
        public void DirectionalExpansionOverflowStopsDefinitionBuild()
        {
            // 受检整数运算：接近 int 边界的合法三角格点参与 60° 旋转必定溢出。
            // 合法点要求 T ∈ {-1,1} 且 X + Y + T 为偶数。
            int x = int.MaxValue - 1;   // 偶数
            int y = int.MaxValue - 2;   // 奇数
            int t = 1;                  // 偶 + 奇 + 1 = 偶 → 合法
            Assert.That(ProjectHero.Logic.Grid.TrianglePoint.IsValid(x, y, t), Is.True,
                "溢出探针必须先是合法三角格点");

            var point = new ProjectHero.Logic.Grid.TrianglePoint(x, y, t);
            var ex = Assert.Throws<LogicDefinitionException>(() =>
                DirectionalGeometry.Rotate60CounterClockwise(point));
            Assert.That(ex.ErrorCode, Is.EqualTo(DirectionalCodes.DIRECTIONAL_EXPANSION_OVERFLOW));

            // 对照：起点奇偶非法的探针必须在此之前就以 TRIANGLE_POINT_INVALID 拒绝。
            Assert.That(ProjectHero.Logic.Grid.TrianglePoint.IsValid(0, 0, 1), Is.False,
                "X + Y + T = 1 为奇数，不是合法三角格点");
            Assert.That(ProjectHero.Logic.Grid.TrianglePoint.IsValid(0, 0, -1), Is.False,
                "X + Y + T = -1 为奇数，不是合法三角格点");
            Assert.That(ProjectHero.Logic.Grid.TrianglePoint.IsValid(1, 0, 1), Is.True);
        }

        [Test]
        public void DirectionalExpansionDeduplicatesAndSortsCanonicalPoints()
        {
            // 故意给乱序 + 重复的基准。
            var evenBase = new List<ProjectHero.Core.Grid.TrianglePoint>
            {
                new ProjectHero.Core.Grid.TrianglePoint(4, 1, -1),
                new ProjectHero.Core.Grid.TrianglePoint(3, 0, 1),
                new ProjectHero.Core.Grid.TrianglePoint(3, 0, 1),   // 重复
                new ProjectHero.Core.Grid.TrianglePoint(2, 1, -1)
            };
            var oddBase = new List<ProjectHero.Core.Grid.TrianglePoint>
            {
                new ProjectHero.Core.Grid.TrianglePoint(2, 1, 1),
                new ProjectHero.Core.Grid.TrianglePoint(3, 0, 1)
            };

            var expanded = DirectionalGeometry.ExpandFromBases(
                LegacyTypeConversion.ToLogicPoints(evenBase), LegacyTypeConversion.ToLogicPoints(oddBase));

            foreach (var set in expanded)
            {
                var points = set.Triangles.ToArray();
                Assert.That(points, Is.Unique, "每个方向内必须按 (X,Y,T) 去重");
                var sorted = points.OrderBy(p => p).ToArray();
                Assert.That(points, Is.EqualTo(sorted), "每个方向内必须按 (X,Y,T) 升序");
            }

            // 偶数基准去重后应为 3 个点。
            Assert.That(expanded[0].Triangles.Count, Is.EqualTo(3));
            Assert.That(expanded[2].Triangles.Count, Is.EqualTo(3));
        }

        /// <summary>
        /// 12 向逐向比对（13 资产 × 12 方向 = 156 个方向）：新规范的每一向必须逐点等于
        /// <b>旧生产代码</b>的逐向输出。旧侧直接调用仍可调用的旧类型
        /// <see cref="AttackPattern.GetAffectedTriangles"/> / <see cref="UnitVolume.GetVolumeFor"/>，
        /// 它们内部走真实 <see cref="GridMath.Rotate"/>（浮点旋转），因此与生产侧
        /// <c>DirectionalGeometry</c> 的受检整数公式构成两条<b>独立</b>实现路径的交叉验证，
        /// 而非同公式自证。
        /// </summary>
        [Test]
        public void CurrentPatternAndVolumeAssetsMatchLegacyOutputInAllDirections()
        {
            var assets = BattleDefinitionFixture.Assets;
            var definition = BattleDefinitionFixture.Definition;
            int comparedDirections = 0;

            // Pattern：新规范的每一向都必须等于旧实现的逐向输出。
            foreach (var pair in assets.PatternsByGuid.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var migration = LegacyIdMigrationManifest.FindPattern(pair.Key);
                var spec = definition.AttackPatterns.Single(p => p.AttackPatternId.Value == migration.FinalAttackPatternId);
                var asset = pair.Value;

                for (int dir = 0; dir < GridDirectionInfo.DirectionCount; dir++)
                {
                    var legacy = LegacyPatternDirection(asset, (ProjectHero.Core.Grid.GridDirection)dir);
                    var canonical = spec.Directions[dir].Triangles
                        .Select(p => (p.X, p.Y, p.T))
                        .OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.T)
                        .ToArray();
                    var expected = legacy
                        .Select(p => (p.X, p.Y, p.T))
                        .OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.T)
                        .ToArray();
                    Assert.That(canonical, Is.EqualTo(expected),
                        $"{migration.FinalAttackPatternId} direction {dir} 必须与旧输出逐点一致");
                    comparedDirections++;
                }
            }

            // Volume：旧资产显式序列化的方向同样逐向比对（East/EastNorth 之外的显式数据也在此校验）。
            foreach (var pair in assets.VolumesByGuid.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var migration = LegacyIdMigrationManifest.FindVolume(pair.Key);
                var spec = definition.Volumes.Single(v => v.VolumeSpecId.Value == migration.FinalVolumeSpecId);
                var asset = pair.Value;

                for (int dir = 0; dir < GridDirectionInfo.DirectionCount; dir++)
                {
                    var legacy = LegacyVolumeDirection(asset, (ProjectHero.Core.Grid.GridDirection)dir);
                    var canonical = spec.Directions[dir].Triangles
                        .Select(p => (p.X, p.Y, p.T))
                        .OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.T)
                        .ToArray();
                    var expected = legacy
                        .Select(p => (p.X, p.Y, p.T))
                        .OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.T)
                        .ToArray();
                    Assert.That(canonical, Is.EqualTo(expected),
                        $"{migration.FinalVolumeSpecId} direction {dir} 必须与旧输出逐点一致");
                    comparedDirections++;
                }
            }

            Assert.That(comparedDirections, Is.EqualTo((10 + 3) * 12), "必须覆盖全部资产的 12 个方向");
        }

        [Test]
        public void ExplicitLegacyDirectionMustMatchCanonicalExpansion()
        {
            var asset = UnityEngine.ScriptableObject.CreateInstance<UnitVolume>();
            try
            {
                asset.Volumes.Add(new UnitVolume.DirectionalVolume
                {
                    Direction = ProjectHero.Core.Grid.GridDirection.East,
                    RelativeTriangles = new List<ProjectHero.Core.Grid.TrianglePoint>
                    {
                        new ProjectHero.Core.Grid.TrianglePoint(3, 0, 1)
                    }
                });
                asset.Volumes.Add(new UnitVolume.DirectionalVolume
                {
                    Direction = ProjectHero.Core.Grid.GridDirection.EastNorth,
                    RelativeTriangles = new List<ProjectHero.Core.Grid.TrianglePoint>
                    {
                        new ProjectHero.Core.Grid.TrianglePoint(4, 1, -1)
                    }
                });
                // 显式序列化的 NorthEast 合法但与展开结果不符 → 整体拒绝。
                asset.Volumes.Add(new UnitVolume.DirectionalVolume
                {
                    Direction = ProjectHero.Core.Grid.GridDirection.NorthEast,
                    RelativeTriangles = new List<ProjectHero.Core.Grid.TrianglePoint>
                    {
                        new ProjectHero.Core.Grid.TrianglePoint(5, 6, -1)
                    }
                });

                var ex = Assert.Throws<LogicDefinitionException>(() =>
                    LegacyGridDefinitionConverter.ConvertVolume("unit_volume.explicit_mismatch", asset));
                Assert.That(ex.ErrorCode, Is.EqualTo(DirectionalCodes.DIRECTIONAL_TABLE_MISMATCH));

                // 改成与展开结果一致时通过。
                asset.Volumes[2].RelativeTriangles = new List<ProjectHero.Core.Grid.TrianglePoint>
                {
                    new ProjectHero.Core.Grid.TrianglePoint(1, 2, -1)
                };
                var ok = LegacyGridDefinitionConverter.ConvertVolume("unit_volume.explicit_match", asset);
                Assert.That(ok.Directions.Count, Is.EqualTo(12));
            }
            finally { UnityEngine.Object.DestroyImmediate(asset); }
        }

        [Test]
        public void DirectionalExpansionIgnoresAssetAndPointEnumerationOrder()
        {
            // 同一资产内容以不同"发现顺序"与不同"点排列"输入，规范表必须完全相同。
            var assets = BattleDefinitionFixture.Assets;

            var reversedLibraries = assets.LibrariesByGuid
                .OrderByDescending(p => p.Key, StringComparer.Ordinal).ToList();
            var reversedPatterns = assets.PatternsByGuid
                .OrderByDescending(p => p.Key, StringComparer.Ordinal)
                .Select(p => new KeyValuePair<string, AttackPattern>(p.Key, ShufflePoints(p.Value)))
                .ToList();
            var reversedVolumes = assets.VolumesByGuid
                .OrderByDescending(p => p.Key, StringComparer.Ordinal).ToList();

            var shuffledSet = new LegacyAssetSet(reversedLibraries, reversedPatterns, reversedVolumes);
            var shuffled = BattleDefinitionBuilder.BuildMainBattleDefinition(
                shuffledSet, BattleDefinitionFixture.UnitSource);

            Assert.That(shuffled.Succeeded, Is.True,
                "顺序打乱后仍必须成功：" + string.Join(",", shuffled.ErrorCodes()));

            var baseline = BattleDefinitionFixture.Definition;
            Assert.That(shuffled.Definition.BattleDefinitionHashValue,
                Is.EqualTo(baseline.BattleDefinitionHashValue),
                "发现顺序与资产内点排列不得影响哈希");

            for (int i = 0; i < baseline.AttackPatterns.Count; i++)
            {
                Assert.That(shuffled.Definition.AttackPatterns[i].AttackPatternId,
                    Is.EqualTo(baseline.AttackPatterns[i].AttackPatternId));
                for (int d = 0; d < GridDirectionInfo.DirectionCount; d++)
                {
                    Assert.That(shuffled.Definition.AttackPatterns[i].Directions[d].Triangles.ToArray(),
                        Is.EqualTo(baseline.AttackPatterns[i].Directions[d].Triangles.ToArray()));
                }
            }
        }

        // ============================================================
        // 4. 定义构建的拒绝路径与哈希边界
        // ============================================================

        [Test]
        public void BuilderOutputContainsNoRuntimeRotationRecipe()
        {
            var definition = BattleDefinitionFixture.Definition;

            // 每个带朝向的表都是"完整的 12 向最终整数表"，没有半成品、没有空表。
            foreach (var pattern in definition.AttackPatterns) AssertCompleteTable(pattern.Directions);
            foreach (var volume in definition.Volumes) AssertCompleteTable(volume.Directions);
            foreach (var pattern in definition.MovementPatterns) AssertCompleteTable(pattern.Directions);

            // Logic 程序集里不得存在"按角度旋转"的运行时配方：
            // DirectionalGeometry 只暴露整数 60° 单步（参数为 TrianglePoint），
            // 且公开 API 中不存在 float/double 参数或返回值。
            var geometryType = typeof(DirectionalGeometry);
            foreach (var method in geometryType.GetMethods(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static |
                         System.Reflection.BindingFlags.DeclaredOnly))
            {
                Assert.That(method.ReturnType, Is.Not.EqualTo(typeof(float)), method.Name);
                Assert.That(method.ReturnType, Is.Not.EqualTo(typeof(double)), method.Name);
                foreach (var parameter in method.GetParameters())
                {
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(float)), method.Name);
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(double)), method.Name);
                }
            }

            // Logic 定义里不得出现任何"角度/弧度"字段名。
            foreach (var member in typeof(AttackPayloadSpec).GetMembers(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                Assert.That(member.Name, Does.Not.Contain("Angle").And.Not.Contain("Radian").And.Not.Contain("Float"));
            }
        }

        [Test]
        public void BuilderRejectsDanglingDefinitionReference()
        {
            var assets = BattleDefinitionFixture.Assets;

            // ① 迁移清单里声明了最终 ID、但资产集合里没有这个 Pattern：
            //    被动作引用的 Pattern 缺位必须给出 DEFINITION_REFERENCE_DANGLING。
            var withoutSlashR1 = assets.PatternsByGuid
                .Where(p => p.Key != LegacyIdMigrationManifest.PatternSlashR1Guid)
                .ToList();
            var mutated = new LegacyAssetSet(
                assets.LibrariesByGuid, withoutSlashR1, assets.VolumesByGuid);

            var result = BattleDefinitionBuilder.BuildMainBattleDefinition(
                mutated, BattleDefinitionFixture.UnitSource);

            Assert.That(result.Succeeded, Is.False, "被引用的 Pattern 缺失时必须整体失败");
            Assert.That(result.Definition, Is.Null, "失败时不得产出可运行定义");
            Assert.That(result.ErrorCodes(), Does.Contain(DefinitionCodes.DEFINITION_REFERENCE_DANGLING));
            Assert.That(result.ErrorCodes(), Does.Not.Contain(DefinitionCodes.DEFINITION_SILENTLY_SKIPPED));

            // ② 迁移清单里根本没有这个资产 GUID：必须给出 LEGACY_ID_MIGRATION_MISSING
            //    （同样整体失败，不得跳过）。
            var unmappedAssets = assets.PatternsByGuid
                .Select(p => p.Key == LegacyIdMigrationManifest.PatternSlashR1Guid
                    ? new KeyValuePair<string, AttackPattern>("ffffffffffffffffffffffffffffffff", p.Value)
                    : p)
                .ToList();
            var unmappedSet = new LegacyAssetSet(
                assets.LibrariesByGuid, unmappedAssets, assets.VolumesByGuid);

            var unmappedResult = BattleDefinitionBuilder.BuildMainBattleDefinition(
                unmappedSet, BattleDefinitionFixture.UnitSource);

            Assert.That(unmappedResult.Succeeded, Is.False, "无迁移映射的资产必须整体失败");
            Assert.That(unmappedResult.ErrorCodes(), Does.Contain(DefinitionCodes.LEGACY_ID_MIGRATION_MISSING));

            // ③ 错误列表按稳定键排序：同一输入重复构建得到逐项相同的错误序列。
            var again = BattleDefinitionBuilder.BuildMainBattleDefinition(
                mutated, BattleDefinitionFixture.UnitSource);
            Assert.That(again.ErrorKeys(), Is.EqualTo(result.ErrorKeys()),
                "错误列表必须按稳定键排序且可复现");
        }

        [Test]
        public void BuilderRejectsInvalidOrDuplicateDefinitionId()
        {
            var elements = new List<(string Id, string[] Signature)>
            {
                ("action.dup_probe", new[] { "sig-a" }),
                ("action.dup_probe", new[] { "sig-b" })
            };

            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.DetectContentConflicts(elements, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.DEFINITION_ID_CONTENT_CONFLICT));

            // ID 格式校验：空、含大写、含非法字符都被拒绝。
            Assert.That(DefinitionIdValidation.ValidateFormat(""), Is.EqualTo(DefinitionIdValidation.ID_EMPTY));
            Assert.That(DefinitionIdValidation.ValidateFormat("Action.Bad"), Is.EqualTo(DefinitionIdValidation.ID_INVALID_CHARACTERS));
            Assert.That(DefinitionIdValidation.ValidateFormat("action-bad"), Is.EqualTo(DefinitionIdValidation.ID_INVALID_CHARACTERS));
            Assert.That(DefinitionIdValidation.ValidateFormat("action.good_1"), Is.Null);

            // 重复检测：同命名空间内同 ID 两次必须报 ID_DUPLICATE。
            Assert.That(DefinitionIdValidation.ValidateUnique(new[] { "a.b", "a.b" }),
                Is.EqualTo(DefinitionIdValidation.ID_DUPLICATE));
            Assert.That(DefinitionIdValidation.ValidateUnique(new[] { "a.b", "a.c" }), Is.Null);
        }

        [Test]
        public void DefinitionAndHashIgnoreAssetDiscoveryOrder()
        {
            var baseline = BattleDefinitionFixture.Definition;

            // 三种"发现顺序"：正序、逆序、以及按资产名排序后再逆序。
            var forward = BattleDefinitionBuilder.BuildMainBattleDefinition(
                OrderedAssets(ascending: true), BattleDefinitionFixture.UnitSource);
            var backward = BattleDefinitionBuilder.BuildMainBattleDefinition(
                OrderedAssets(ascending: false), BattleDefinitionFixture.UnitSource);

            Assert.That(forward.Succeeded, Is.True);
            Assert.That(backward.Succeeded, Is.True);

            Assert.That(forward.Definition.BattleDefinitionHashValue,
                Is.EqualTo(baseline.BattleDefinitionHashValue));
            Assert.That(backward.Definition.BattleDefinitionHashValue,
                Is.EqualTo(baseline.BattleDefinitionHashValue));

            // 定义内的集合顺序也必须一致。
            Assert.That(backward.Definition.Actions.Select(a => a.ActionSpecId.Value).ToArray(),
                Is.EqualTo(baseline.Actions.Select(a => a.ActionSpecId.Value).ToArray()));
            Assert.That(backward.Definition.Units.Select(u => u.UnitDefinitionId.Value).ToArray(),
                Is.EqualTo(baseline.Units.Select(u => u.UnitDefinitionId.Value).ToArray()));
        }

        [Test]
        public void DefinitionHashChangesWhenGameplayDefinitionChanges()
        {
            var definition = BattleDefinitionFixture.Definition;

            // 只改一个纯玩法数值（Guard 的 ActiveTicks），哈希必须变化。
            var mutated = MutateAction(definition, LegacyIdMigrationManifest.ActionGuard,
                mutate: action => new ActionSpec(
                    action.ActionSpecId,
                    action.Type,
                    new GuardTimingSpec(15, 61, 15),     // 60 → 61
                    action.Payload,
                    action.AdrenalineCost));

            Assert.That(mutated.BattleDefinitionHashValue,
                Is.Not.EqualTo(definition.BattleDefinitionHashValue),
                "改变玩法定义必须改变哈希");
        }

        [Test]
        public void ExpandedPatternAndVolumeTablesAffectDefinitionHash()
        {
            var definition = BattleDefinitionFixture.Definition;

            // 只改 Pattern 的一个方向上的一个点。
            var mutated = MutateFirstPatternPoint(definition);

            Assert.That(mutated.BattleDefinitionHashValue,
                Is.Not.EqualTo(definition.BattleDefinitionHashValue),
                "12 向最终表的任何点变化都必须改变哈希");
        }

        [Test]
        public void EquivalentCanonicalTablesKeepHashAcrossBuilderImplementationChanges()
        {
            var definition = BattleDefinitionFixture.Definition;

            // "改写 Builder 但产生相同规范表"：把每个方向内的点按不同顺序、并插入重复项重建，
            // 再交给同一个哈希入口 —— 规范表逐点相同时哈希必须保持不变。
            var rebuilt = CanonicalizeAllTables(definition);

            string originalHash = BattleDefinitionHash.Compute(
                definition.RulesVersion, definition.TicksPerSecond, definition.Rules,
                definition.ConcurrentAction, definition.ReactionRules, definition.AdrenalineRules,
                definition.FactionModel, definition.DamageChannels, definition.ImpactProfiles,
                definition.Units, definition.Actions, definition.AttackPatterns, definition.Volumes,
                definition.MovementPatterns, definition.ActionSets, definition.StatusEffects,
                definition.Encounters, definition.DefaultDynamicSpawnPolicy);

            string rebuiltHash = BattleDefinitionHash.Compute(
                rebuilt.RulesVersion, rebuilt.TicksPerSecond, rebuilt.Rules,
                rebuilt.ConcurrentAction, rebuilt.ReactionRules, rebuilt.AdrenalineRules,
                rebuilt.FactionModel, rebuilt.DamageChannels, rebuilt.ImpactProfiles,
                rebuilt.Units, rebuilt.Actions, rebuilt.AttackPatterns, rebuilt.Volumes,
                rebuilt.MovementPatterns, rebuilt.ActionSets, rebuilt.StatusEffects,
                rebuilt.Encounters, rebuilt.DefaultDynamicSpawnPolicy);

            Assert.That(rebuiltHash, Is.EqualTo(originalHash));
            Assert.That(originalHash, Is.EqualTo(definition.BattleDefinitionHashValue));

            // 而"点集合真的变了"必须改变哈希（防止上面的规范化把差异抹平）。
            var changed = MutateFirstPatternPoint(definition);
            Assert.That(changed.BattleDefinitionHashValue, Is.Not.EqualTo(originalHash));
        }

        // ============================================================
        // 私有助手
        // ============================================================

        private static LegacyAssetSet OrderedAssets(bool ascending)
        {
            var assets = BattleDefinitionFixture.Assets;
            var comparer = ascending
                ? (IComparer<string>)StringComparer.Ordinal
                : StringComparer.Ordinal;

            var libraries = assets.LibrariesByGuid.ToList();
            var patterns = assets.PatternsByGuid.ToList();
            var volumes = assets.VolumesByGuid.ToList();

            libraries.Sort((a, b) => comparer.Compare(a.Key, b.Key));
            patterns.Sort((a, b) => comparer.Compare(a.Key, b.Key));
            volumes.Sort((a, b) => comparer.Compare(a.Key, b.Key));

            if (!ascending)
            {
                libraries.Reverse();
                patterns.Reverse();
                volumes.Reverse();
            }
            return new LegacyAssetSet(libraries, patterns, volumes);
        }

        private static AttackPattern ShufflePoints(AttackPattern source)
        {
            // 复制一份资产（不修改原资产），把点列表倒序并插入重复项：
            // 规范化的去重 + 排序必须抹平这些差异。
            var copy = UnityEngine.ScriptableObject.CreateInstance<AttackPattern>();
            copy.name = source.name + "_shuffled";
            var even = new List<ProjectHero.Core.Grid.TrianglePoint>(source.RelativeTriangles);
            var odd = new List<ProjectHero.Core.Grid.TrianglePoint>(source.RelativeTrianglesOdd);
            even.Reverse();
            odd.Reverse();
            if (even.Count > 0) even.Add(even[0]);
            if (odd.Count > 0) odd.Add(odd[0]);
            copy.RelativeTriangles = even;
            copy.RelativeTrianglesOdd = odd;
            return copy;
        }

        private static List<ProjectHero.Logic.Grid.TrianglePoint> ToLogicPoints(
            IReadOnlyList<ProjectHero.Core.Grid.TrianglePoint> points)
            => points.Select(p => new ProjectHero.Logic.Grid.TrianglePoint(p.X, p.Y, p.T)).ToList();

        private static ProjectHero.Logic.Grid.TrianglePoint[] RotateBase(
            IReadOnlyList<ProjectHero.Logic.Grid.TrianglePoint> basePoints, int steps)
        {
            return basePoints
                .Select(p =>
                {
                    var q = p;
                    for (int i = 0; i < steps; i++) q = DirectionalGeometry.Rotate60CounterClockwise(q);
                    return q;
                })
                .OrderBy(p => p)
                .ToArray();
        }

        /// <summary>
        /// 旧实现的逐方向输出：<b>直接调用旧生产类型</b> <see cref="AttackPattern.GetAffectedTriangles"/>，
        /// 其内部走真实 <see cref="GridMath.Rotate"/>（浮点三角函数 + RoundToInt）。
        /// <c>attackerPos</c> 传 (0,0)，返回值即旧实现的相对方向输出。
        /// 真实资产全部同时具备 East/EastNorth 基准（见迁移记录 §6），故旧实现的
        /// "缺失奇数基准则回退偶数基准" 分支不会在语料上触发；缺基准的拒绝行为由
        /// <c>LegacyMissingOddBaseIsRejected</c> 单独锁定。
        /// </summary>
        private static List<ProjectHero.Core.Grid.TrianglePoint> LegacyPatternDirection(
            AttackPattern asset, ProjectHero.Core.Grid.GridDirection direction)
            => asset.GetAffectedTriangles(new ProjectHero.Core.Pathfinding.GridPoint(0, 0), direction);

        /// <summary>
        /// 旧实现的逐方向输出（Volume 侧）：<b>直接调用旧生产类型</b> <see cref="UnitVolume.GetVolumeFor"/>
        /// （显式序列化方向优先，缺失时按基准方向 + 真实 <see cref="GridMath.Rotate"/> 旋转）。
        /// </summary>
        private static List<ProjectHero.Core.Grid.TrianglePoint> LegacyVolumeDirection(
            UnitVolume asset, ProjectHero.Core.Grid.GridDirection direction)
            => asset.GetVolumeFor(direction);

        private static int DirectionPointCount(
            ProjectHero.Logic.Grid.AttackPatternSpec pattern, LogicGridDirection direction)
            => pattern.Directions[(int)direction].Triangles.Count;

        private static void AssertCompleteTable(
            IReadOnlyList<DirectionalTriangleSet> directions)
        {
            Assert.That(directions.Count, Is.EqualTo(GridDirectionInfo.DirectionCount));
            for (int i = 0; i < directions.Count; i++)
            {
                Assert.That(directions[i].Direction, Is.EqualTo((LogicGridDirection)i));
                Assert.That(directions[i].Triangles, Is.Not.Empty);
                foreach (var point in directions[i].Triangles)
                {
                    Assert.That(ProjectHero.Logic.Grid.TrianglePoint.IsValid(point.X, point.Y, point.T),
                        Is.True, $"({point.X},{point.Y},{point.T})");
                }
            }
            Assert.That(DirectionalSpecValidation.ValidateDirections(directions), Is.Null);
        }

        /// <summary>用反射式重建验证哈希只依赖规范内容（等价规范表 → 相同哈希）。</summary>
        private static BattleDefinition CanonicalizeAllTables(BattleDefinition source)
        {
            var rebuiltPatterns = source.AttackPatterns
                .Select(p => new ProjectHero.Logic.Grid.AttackPatternSpec(
                    p.AttackPatternId, ShuffleAndCanonicalize(p.Directions)))
                .ToList();
            var rebuiltVolumes = source.Volumes
                .Select(v => new ProjectHero.Logic.Grid.VolumeSpec(
                    v.VolumeSpecId, ShuffleAndCanonicalize(v.Directions)))
                .ToList();

            var rebuiltActions = source.Actions.Select(a =>
            {
                if (!(a.Payload is AttackPayloadSpec attack)) return a;
                var pattern = rebuiltPatterns.Single(p =>
                    p.AttackPatternId.Value == attack.Pattern.AttackPatternId.Value);
                return new ActionSpec(a.ActionSpecId, a.Type, a.Timing,
                    new AttackPayloadSpec(attack.DamageComponents, attack.ImpactProfileId,
                        attack.ForceMultiplier, attack.TargetPolicy, attack.AllowedTargetRelations,
                        attack.MomentumDirectionOffsetSteps, pattern, attack.Tags),
                    a.AdrenalineCost);
            }).ToList();

            return new BattleDefinition(
                source.RulesVersion, source.TicksPerSecond, source.Rules, source.ConcurrentAction,
                source.ReactionRules, source.AdrenalineRules, source.FactionModel, source.DamageChannels,
                source.ImpactProfiles, source.Units, rebuiltActions, rebuiltPatterns, rebuiltVolumes,
                source.MovementPatterns, source.ActionSets, source.StatusEffects, source.Encounters,
                source.DefaultDynamicSpawnPolicy, source.BattleDefinitionHashValue);
        }

        private static IReadOnlyList<DirectionalTriangleSet> ShuffleAndCanonicalize(
            IReadOnlyList<DirectionalTriangleSet> directions)
        {
            var result = new List<DirectionalTriangleSet>(directions.Count);
            foreach (var set in directions)
            {
                // 先倒序、再插入重复项，最后规范化（去重 + 升序）。
                var points = new List<ProjectHero.Logic.Grid.TrianglePoint>(set.Triangles);
                points.Reverse();
                if (points.Count > 0) points.Add(points[0]);
                var canonical = DirectionalGeometry.Canonicalize(points);
                result.Add(new DirectionalTriangleSet(set.Direction, canonical));
            }
            return result;
        }

        private static BattleDefinition MutateAction(
            BattleDefinition source, string actionSpecId, Func<ActionSpec, ActionSpec> mutate)
        {
            var actions = source.Actions
                .Select(a => a.ActionSpecId.Value == actionSpecId ? mutate(a) : a)
                .ToList();
            return Rehash(source, actions, source.AttackPatterns, source.Volumes);
        }

        private static BattleDefinition MutateFirstPatternPoint(BattleDefinition source)
        {
            var first = source.AttackPatterns[0];
            var directions = new List<DirectionalTriangleSet>(first.Directions);
            var original = directions[0];
            var points = new List<ProjectHero.Logic.Grid.TrianglePoint>(original.Triangles);

            // 在 East 上追加一个合法但原来没有的点（X=8, Y=0, T=1 → 8+0+1 为奇数？用 (9,0,1) 保证合法）。
            points.Add(new ProjectHero.Logic.Grid.TrianglePoint(9, 0, 1));
            points.Sort();
            directions[0] = new DirectionalTriangleSet(original.Direction, points);

            var patterns = source.AttackPatterns
                .Select(p => p.AttackPatternId.Value == first.AttackPatternId.Value
                    ? new ProjectHero.Logic.Grid.AttackPatternSpec(p.AttackPatternId, directions)
                    : p)
                .ToList();

            return Rehash(source, source.Actions, patterns, source.Volumes);
        }

        private static BattleDefinition Rehash(
            BattleDefinition source,
            IReadOnlyList<ActionSpec> actions,
            IReadOnlyList<ProjectHero.Logic.Grid.AttackPatternSpec> patterns,
            IReadOnlyList<ProjectHero.Logic.Grid.VolumeSpec> volumes)
        {
            string hash = BattleDefinitionHash.Compute(
                source.RulesVersion, source.TicksPerSecond, source.Rules, source.ConcurrentAction,
                source.ReactionRules, source.AdrenalineRules, source.FactionModel, source.DamageChannels,
                source.ImpactProfiles, source.Units, actions, patterns, volumes,
                source.MovementPatterns, source.ActionSets, source.StatusEffects, source.Encounters,
                source.DefaultDynamicSpawnPolicy);

            return new BattleDefinition(
                source.RulesVersion, source.TicksPerSecond, source.Rules, source.ConcurrentAction,
                source.ReactionRules, source.AdrenalineRules, source.FactionModel, source.DamageChannels,
                source.ImpactProfiles, source.Units, actions, patterns, volumes,
                source.MovementPatterns, source.ActionSets, source.StatusEffects, source.Encounters,
                source.DefaultDynamicSpawnPolicy, hash);
        }
    }
}
