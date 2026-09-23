using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Authoring;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Grid;
using ProjectHero.Logic.Combat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ProjectHero.Authoring.Tests
{
    /// <summary>
    /// 程序集边界审计与主战斗场景冒烟。
    ///
    /// 任务 02 期间本测试位于 Assembly-CSharp-Editor（当时项目中还不存在 Authoring 程序集）。
    /// 任务 02B 搬迁完整 Authoring 闭包后，它随边界所有者一并进入
    /// <c>ProjectHero.Authoring.Tests</c>，断言列表随之扩展为 02B 的真实拓扑，
    /// 但"任何自定义 asmdef 都不得引用 Assembly-CSharp / Assembly-CSharp-Editor"这一
    /// 核心不变量保持不变（00 号规则 17）。
    ///
    /// 场景冒烟只做"加载级只读检查"：场景可加载、0 缺失脚本、两个 CombatUnit 组件
    /// （按 MonoScript GUID 识别，不引用 Assembly-CSharp 中的运行时类型）以及它们的
    /// ActionLibrary / UnitVolumeDefinition 引用完好（这正是 .meta GUID 未变、搬迁未破坏
    /// 资产引用的直接证据）。它不启动战斗逻辑。
    /// </summary>
    public class AssemblyBoundaryAuthoringTests
    {
        private const string MainScenePath = "Assets/Scenes/CombatSampleScene.unity";
        private const string CombatUnitScriptPath = "Assets/Scripts/Core/Entities/CombatUnit.cs";

        /// <summary>Legacy ActionLibrarySO 的 MonoScript GUID（搬迁前后不变）。</summary>
        private const string ActionLibrarySoScriptGuid = "01511c6326086974ab1e98830528a667";
        /// <summary>Legacy UnitVolume 的 MonoScript GUID（搬迁前后不变）。</summary>
        private const string UnitVolumeScriptGuid = "72c1b722215097e42819e4e1e5ed94a5";

        /// <summary>02B 结束时项目中必须存在的自定义 asmdef 全量集合。</summary>
        private static readonly string[] ExpectedAssemblyNames =
        {
            "ProjectHero.Authoring",
            "ProjectHero.Authoring.Tests",
            "ProjectHero.Grid",
            "ProjectHero.Logic",
            "ProjectHero.Logic.Tests"
        };

        [Serializable]
        private class AsmdefInfo
        {
            public string name;
            public string[] references;
            public bool noEngineReferences;
            public bool autoReferenced;
        }

        [Test]
        public void AuthoringAssemblyDoesNotReferenceAssemblyCSharp()
        {
            var asmdefFiles = Directory.GetFiles(Application.dataPath, "*.asmdef", SearchOption.AllDirectories);
            Assert.That(asmdefFiles, Is.Not.Empty, "必须存在自定义 asmdef");

            var infos = asmdefFiles
                .Select(path => JsonUtility.FromJson<AsmdefInfo>(File.ReadAllText(path)))
                .ToArray();

            var names = infos.Select(i => i.name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.That(names, Is.EqualTo(ExpectedAssemblyNames),
                "任务 02B 只允许 Grid / Logic / Authoring 三个生产 asmdef 与两个测试 asmdef（不得提前创建 UnityView）");

            foreach (var info in infos)
            {
                var references = info.references ?? new string[0];
                Assert.That(references, Does.Not.Contain("Assembly-CSharp"),
                    $"{info.name} 不得引用预定义 Assembly-CSharp");
                Assert.That(references, Does.Not.Contain("Assembly-CSharp-Editor"),
                    $"{info.name} 不得引用预定义 Assembly-CSharp-Editor");
            }

            var logic = infos.Single(i => i.name == "ProjectHero.Logic");
            Assert.That(logic.noEngineReferences, Is.True, "ProjectHero.Logic 必须启用 noEngineReferences");
            Assert.That(logic.references ?? new string[0], Is.Empty, "Logic 不得引用任何项目程序集");

            var grid = infos.Single(i => i.name == "ProjectHero.Grid");
            Assert.That(grid.references ?? new string[0], Is.Empty,
                "共享网格程序集不得引用任何项目程序集（它是依赖图的最底层）");

            var authoring = infos.Single(i => i.name == "ProjectHero.Authoring");
            Assert.That(authoring.references ?? new string[0],
                Is.EquivalentTo(new[] { "ProjectHero.Grid", "ProjectHero.Logic" }),
                "Authoring 只允许引用共享网格程序集与 Logic");
            Assert.That(authoring.autoReferenced, Is.True,
                "Authoring 必须保持 autoReferenced（Assembly-CSharp 中的 CombatUnit/ActionScheduler 仍读取这些 SO 类型）");

            var logicTests = infos.Single(i => i.name == "ProjectHero.Logic.Tests");
            var logicTestReferences = (logicTests.references ?? new string[0])
                .Where(r => !r.StartsWith("UnityEngine.") && !r.StartsWith("UnityEditor."))
                .ToArray();
            Assert.That(logicTestReferences, Is.EqualTo(new[] { "ProjectHero.Logic" }),
                "Logic.Tests 只允许引用 Logic（不得通过 Authoring 访问旧资产）");

            var authoringTests = infos.Single(i => i.name == "ProjectHero.Authoring.Tests");
            var authoringTestReferences = (authoringTests.references ?? new string[0])
                .Where(r => !r.StartsWith("UnityEngine.") && !r.StartsWith("UnityEditor."))
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToArray();
            Assert.That(authoringTestReferences,
                Is.EqualTo(new[] { "ProjectHero.Authoring", "ProjectHero.Grid", "ProjectHero.Logic" }),
                "Authoring.Tests 只能引用 Authoring / Grid / Logic");
        }

        [Test]
        public void CurrentMainBattleSceneLoadsWithNoNewCompileErrors()
        {
            // 编译错误会阻止 EditMode 测试本身运行；此测试补足场景级冒烟证据。
            Assert.That(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), MainScenePath)), Is.True,
                "主战斗场景文件必须存在");

            var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            Assert.That(scene.IsValid(), Is.True, "主战斗场景必须可加载");

            // 无缺失脚本（MISSING SCRIPT = 0）。
            foreach (var root in scene.GetRootGameObjects())
            {
                AssertNoMissingScripts(root);
            }

            // 两个 CombatUnit 组件：按 MonoScript GUID 识别，不引用 Assembly-CSharp 的运行时类型。
            string unitScriptGuid = AssetDatabase.GUIDFromAssetPath(CombatUnitScriptPath).ToString();
            Assert.That(unitScriptGuid, Is.Not.Empty.And.Not.EqualTo("00000000000000000000000000000000"),
                "必须能把 CombatUnit.cs 解析成 MonoScript GUID");

            var unitComponents = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                .Where(component => component != null && ScriptGuidOf(component) == unitScriptGuid)
                .ToArray();

            Assert.That(unitComponents.Length, Is.EqualTo(2), "主场景必须恰好有两个 CombatUnit");

            // 关键配置引用必须完好：这正是 ".cs 与 .meta 一起搬迁、MonoScript GUID 未变" 的直接证据。
            int actionLibraryRefs = 0;
            int volumeRefs = 0;
            foreach (var component in unitComponents)
            {
                var serialized = new SerializedObject(component);
                var actionLibrary = serialized.FindProperty("ActionLibrary");
                var unitVolume = serialized.FindProperty("UnitVolumeDefinition");

                Assert.That(actionLibrary, Is.Not.Null, "CombatUnit 必须仍有 ActionLibrary 字段");
                Assert.That(unitVolume, Is.Not.Null, "CombatUnit 必须仍有 UnitVolumeDefinition 字段");

                if (actionLibrary.objectReferenceValue != null) actionLibraryRefs++;
                if (unitVolume.objectReferenceValue != null) volumeRefs++;
            }

            Assert.That(actionLibraryRefs, Is.EqualTo(2), "两个单位的动作库引用必须完好（未因搬迁丢失）");
            Assert.That(volumeRefs, Is.EqualTo(2), "两个单位的体积引用必须完好（未因搬迁丢失）");

            // 这两个旧配置类型现在由 ProjectHero.Authoring 提供（本测试程序集能直接看到它们）。
            Assert.That(typeof(ActionLibrarySO).Assembly.GetName().Name, Is.EqualTo("ProjectHero.Authoring"));
            Assert.That(typeof(UnitVolume).Assembly.GetName().Name, Is.EqualTo("ProjectHero.Authoring"));
            Assert.That(typeof(ProjectHero.Core.Pathfinding.GridPoint).Assembly.GetName().Name,
                Is.EqualTo("ProjectHero.Grid"));

            // 场景引用的动作库/体积资产必须能被 Authoring 的加载入口解析到（GUID 未变）。
            var assets = BattleDefinitionFixture.Assets;
            Assert.That(assets.Library(LegacyIdMigrationManifest.ForRadius1Guid), Is.Not.Null,
                "ActionLibrarySO GUID 01511c63… 必须仍解析到 ForRadius1");
            Assert.That(assets.Library(LegacyIdMigrationManifest.ForRadius2Guid), Is.Not.Null);
            Assert.That(assets.Volume(LegacyIdMigrationManifest.VolumeRadius1Guid), Is.Not.Null,
                "UnitVolume GUID 72c1b722… 必须仍解析到 Radius_1");
            Assert.That(assets.Volume(LegacyIdMigrationManifest.VolumeRadius2Guid), Is.Not.Null);
        }

        [Test]
        public void MainEncounterReactionActionsAreReachableFromActionSets()
        {
            // 反应闭合的最小独立复核：Block/Dodge 必须真的落在至少一个 ActionSet 内，
            // 且两个集合的可用性结构一致（不因控制者类型不同）。
            var definition = BattleDefinitionFixture.Definition;

            var available = new HashSet<string>(StringComparer.Ordinal);
            foreach (var set in definition.ActionSets)
            {
                Assert.That(set.ActionSpecIds, Is.Not.Null, set.ActionSetId.Value);
                Assert.That(set.ActionSpecIds.Count, Is.GreaterThan(0), set.ActionSetId.Value);
                foreach (var id in set.ActionSpecIds) available.Add(id.Value);
            }

            foreach (var action in definition.Actions)
            {
                if (action.Type != ProjectHero.Logic.Actions.ActionType.Block &&
                    action.Type != ProjectHero.Logic.Actions.ActionType.Dodge) continue;
                Assert.That(available.Contains(action.ActionSpecId.Value), Is.True,
                    action.ActionSpecId.Value + " 必须至少出现在一个动作集合中");
            }

            Assert.That(available.Contains(LegacyIdMigrationManifest.ActionBlock), Is.True);
            Assert.That(available.Contains(LegacyIdMigrationManifest.ActionDodge), Is.True);
            Assert.That(available.Contains(LegacyIdMigrationManifest.ActionGuard), Is.True);
            Assert.That(available.Contains(LegacyIdMigrationManifest.ActionMove), Is.True);

            // Guard/Move/Block/Dodge 在两个集合中的可用性完全一致。
            var sets = definition.ActionSets.OrderBy(s => s.ActionSetId.Value, StringComparer.Ordinal).ToArray();
            Assert.That(sets.Length, Is.EqualTo(2));
            foreach (var id in new[]
                     {
                         LegacyIdMigrationManifest.ActionGuard, LegacyIdMigrationManifest.ActionMove,
                         LegacyIdMigrationManifest.ActionBlock, LegacyIdMigrationManifest.ActionDodge
                     })
            {
                bool first = sets[0].Contains(new ProjectHero.Logic.Ids.ActionSpecId(id));
                bool second = sets[1].Contains(new ProjectHero.Logic.Ids.ActionSpecId(id));
                Assert.That(second, Is.EqualTo(first), id);
            }
        }

        private static string ScriptGuidOf(MonoBehaviour component)
        {
            var script = MonoScript.FromMonoBehaviour(component);
            return ScriptGuidOf(script);
        }

        private static string ScriptGuidOf(UnityEngine.Object asset)
        {
            if (asset == null) return string.Empty;
            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long _)
                ? guid
                : string.Empty;
        }

        private static void AssertNoMissingScripts(GameObject go)
        {
            int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
            Assert.That(missing, Is.Zero, $"GameObject '{go.name}' 存在 {missing} 个缺失脚本");
            foreach (Transform child in go.transform)
                AssertNoMissingScripts(child.gameObject);
        }
    }
}
