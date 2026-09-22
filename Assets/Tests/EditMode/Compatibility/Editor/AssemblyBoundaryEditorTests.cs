using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Core.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ProjectHero.Tests.Compatibility.Editor
{
    /// <summary>程序集边界审计与主战斗场景冒烟（任务 02「必需测试」最后两项）。</summary>
    public class AssemblyBoundaryEditorTests
    {
        [Serializable]
        private class AsmdefInfo
        {
            public string name;
            public string[] references;
            public bool noEngineReferences;
        }

        [Test]
        public void CustomAssemblyDoesNotReferenceAssemblyCSharp()
        {
            var asmdefFiles = Directory.GetFiles(Application.dataPath, "*.asmdef", SearchOption.AllDirectories);
            Assert.That(asmdefFiles, Is.Not.Empty, "必须存在自定义 asmdef");

            var infos = asmdefFiles
                .Select(path => JsonUtility.FromJson<AsmdefInfo>(File.ReadAllText(path)))
                .ToArray();

            // 本任务恰好创建两个 asmdef：ProjectHero.Logic 与 ProjectHero.Logic.Tests。
            var names = infos.Select(i => i.name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.That(names, Is.EqualTo(new[] { "ProjectHero.Logic", "ProjectHero.Logic.Tests" }),
                "任务 02 只允许 Logic 与 Logic.Tests 两个 asmdef（不得提前创建 Authoring/UnityView）");

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
            Assert.That(logic.references ?? new string[0], Is.Empty, "Logic 不得引用任何程序集");

            var tests = infos.Single(i => i.name == "ProjectHero.Logic.Tests");
            var testReferences = tests.references ?? new string[0];
            Assert.That(testReferences, Does.Contain("ProjectHero.Logic"),
                "Logic.Tests 必须引用 ProjectHero.Logic");
            // 除测试框架程序集外，项目 asmdef 引用只有 ProjectHero.Logic。
            var projectReferences = testReferences
                .Where(r => !r.StartsWith("UnityEngine.") && !r.StartsWith("UnityEditor."))
                .ToArray();
            Assert.That(projectReferences, Is.EqualTo(new[] { "ProjectHero.Logic" }));
        }

        [Test]
        public void CurrentMainBattleSceneLoadsWithNoNewCompileErrors()
        {
            // 编译错误会阻止 EditMode 测试本身运行；此测试补足场景级冒烟证据。
            const string scenePath = "Assets/Scenes/CombatSampleScene.unity";
            Assert.That(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), scenePath)), Is.True,
                "主战斗场景文件必须存在");

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            Assert.That(scene.IsValid(), Is.True, "主战斗场景必须可加载");

            // 无缺失脚本（MISSING SCRIPT = 0）。
            foreach (var root in scene.GetRootGameObjects())
            {
                AssertNoMissingScripts(root);
            }

            // 两个 CombatUnit（Player/Enemy），且关键配置引用仍在。
            var units = UnityEngine.Object.FindObjectsByType<CombatUnit>(FindObjectsSortMode.None);
            Assert.That(units.Length, Is.EqualTo(2), "主场景必须恰好有两个 CombatUnit");
            Assert.That(units.Any(u => u.IsPlayerControlled), Is.True);
            Assert.That(units.All(u => u.ActionLibrary != null), Is.True,
                "动作库引用必须完好（兼容入口保留）");
            Assert.That(units.All(u => u.UnitVolumeDefinition != null), Is.True,
                "体积引用必须完好（兼容入口保留）");
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
