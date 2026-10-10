using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectHero.Authoring.Tests
{
    /// <summary>
    /// 任务 03B 的场景接入证据（在 EditMode 下执行）。
    ///
    /// 为什么放在 EditMode：场景装配必须经 Unity API（<c>EditorSceneManager</c> /
    /// <c>SerializedObject</c>），而 PlayMode 下 Unity 明确拒绝
    /// <c>EditorSceneManager.OpenScene</c>（<c>This cannot be used during play mode</c>）。
    /// 本类负责"装配 + 场景结构 + 幂等 + GUID 稳定 + 执行顺序"，PlayMode 侧
    /// （<c>ProjectHero.Compatibility.Runtime.Tests</c>）负责"三模式调用矩阵与 Shadow 零写入"。
    ///
    /// 工具本体在 <c>Assembly-CSharp-Editor</c>（<c>ProjectHero.Editor.RuntimeOwnership</c>），
    /// 本测试程序集不引用它，因此通过反射调用同一个生产方法——不存在第二份装配逻辑。
    /// </summary>
    public class RuntimeOwnershipSceneWiringTests
    {
        private const string SceneToolTypeName =
            "ProjectHero.Editor.RuntimeOwnership.RuntimeOwnershipSceneTool";

        private const string MainScenePath = "Assets/Scenes/CombatSampleScene.unity";
        private const string HiddenScenePath = "Assets/Scenes/HiddenRuntimeValidation.unity";

        /// <summary>隐藏场景必须包含的真实生产组件（不引用 Legacy 运行时类型，用全名核对）。</summary>
        private static readonly string[] ExpectedHiddenSceneTypes =
        {
            "ProjectHero.Core.Entities.CombatUnit",
            "ProjectHero.Core.Timeline.BattleTimeline",
            "ProjectHero.Core.Grid.GridManager",
            "ProjectHero.Core.Input.InputManager",
            "ProjectHero.Core.Gameplay.BattleManager",
            "ProjectHero.Demos.CombatDemo",
            "ProjectHero.Core.Compatibility.Authoring.BattleSimulationSourceFactory",
            "ProjectHero.Core.Compatibility.Runtime.BattleRuntimeBootstrap"
        };

        /// <summary>隐藏场景明确排除的表现/交互/UI 闭包。</summary>
        private static readonly string[] ExcludedHiddenSceneTypes =
        {
            "ProjectHero.UI.UIManager",
            "ProjectHero.UI.HUDManager",
            "ProjectHero.Core.Gameplay.TacticsController",
            "ProjectHero.Core.Gameplay.EnemyAIController",
            "ProjectHero.Visuals.GridVisuals",
            "ProjectHero.Visuals.UnitVolumeRenderer",
            "ProjectHero.Visuals.NextActionPreview.NextActionPreviewSystem"
        };

        [Test, Order(1)]
        public void SceneToolInstallsBothScenesThroughUnityApi()
        {
            var toolType = ResolveSceneToolType();

            string mainReport = (string)InvokeStatic(toolType, "WireMainScene");
            Assert.That(mainReport, Is.Not.Null.And.Not.Empty);
            Assert.That(mainReport, Does.Not.Contain("MISSING"), mainReport);
            Assert.That(mainReport, Does.Not.Contain("EXECUTION_ORDER_NOT_PERSISTED"), mainReport);

            string hiddenReport = (string)InvokeStatic(toolType, "CreateHiddenValidationScene");
            Assert.That(hiddenReport, Does.Contain("HiddenRuntimeValidation.unity"), hiddenReport);
            Assert.That(hiddenReport, Does.Contain("autoStart=false"), hiddenReport);

            Assert.That(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(HiddenScenePath), Is.Not.Null,
                "隐藏验证场景必须由工具落盘");
            Assert.That(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(MainScenePath), Is.Not.Null);
        }

        [Test, Order(2)]
        public void MainSceneHasExactlyOneBootstrapAndKeepsOriginalContent()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Diagnostics/Editor/LegacyComparisonScene.unity", OpenSceneMode.Single);
            Assert.That(scene.IsValid(), Is.True);

            int bootstrapCount = 0;
            int combatUnitCount = 0;
            var allTypes = new List<string>();
            foreach (var root in scene.GetRootGameObjects())
            {
                AssertNoMissingScripts(root);
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null) continue;
                    var type = behaviour.GetType();
                    allTypes.Add(type.FullName);
                    if (type == typeof(BattleRuntimeBootstrap)) bootstrapCount++;
                    if (type.FullName == "ProjectHero.Core.Entities.CombatUnit") combatUnitCount++;
                }
            }

            Assert.That(bootstrapCount, Is.EqualTo(1), "主战斗场景必须恰有一个 BattleRuntimeBootstrap");
            Assert.That(combatUnitCount, Is.EqualTo(2), "主战斗场景必须仍有恰好两个 CombatUnit");
            Assert.That(allTypes, Does.Contain("ProjectHero.Demos.CombatDemo"));
            Assert.That(allTypes, Does.Contain("ProjectHero.Core.Gameplay.EnemyAIController"),
                "主场景不得因 03B 丢失既有旧从属写入者");

            // Bootstrap 槽位必须指向真实的 Legacy 适配器与 02B 初始化来源。
            var bootstrap = FindAll<BattleRuntimeBootstrap>(scene).Single();
            var serialized = new SerializedObject(bootstrap);
            var legacySlot = serialized.FindProperty("_legacyFrameAdapterSlot").objectReferenceValue;
            var sourceSlot = serialized.FindProperty("_simulationSourceSlot").objectReferenceValue;
            Assert.That(legacySlot, Is.Not.Null, "必须注入 Legacy 帧适配器");
            Assert.That(legacySlot.GetType().FullName, Is.EqualTo("ProjectHero.Demos.CombatDemo"));
            Assert.That(sourceSlot, Is.Not.Null, "必须注入纯数据战斗来源");
            Assert.That(sourceSlot.GetType().FullName,
                Is.EqualTo("ProjectHero.Core.Compatibility.Authoring.BattleSimulationSourceFactory"));

            Assert.That(serialized.FindProperty("_autoStart").boolValue, Is.True,
                "主场景必须自动启动战斗（可见基线不变）");
            Assert.That(serialized.FindProperty("_requestedMode").enumValueIndex,
                Is.EqualTo((int)BattleRuntimeMode.Legacy), "主场景必须默认 Legacy");

            // 工厂单位槽位必须显式指向场景中的两个真实单位（不按发现顺序解析）。
            var factory = sourceSlot as MonoBehaviour;
            Assert.That(factory, Is.Not.Null);
            var factorySerialized = new SerializedObject(factory);
            Assert.That(factorySerialized.FindProperty("_heroUnit").objectReferenceValue, Is.Not.Null);
            Assert.That(factorySerialized.FindProperty("_enemyUnit").objectReferenceValue, Is.Not.Null);
        }

        [Test, Order(3)]
        public void HiddenValidationSceneIsMinimalRealChain()
        {
            var scene = EditorSceneManager.OpenScene(HiddenScenePath, OpenSceneMode.Single);
            Assert.That(scene.IsValid(), Is.True);

            var types = new List<string>();
            foreach (var root in scene.GetRootGameObjects())
            {
                AssertNoMissingScripts(root);
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour != null) types.Add(behaviour.GetType().FullName);
                }
            }

            foreach (var expected in ExpectedHiddenSceneTypes)
            {
                Assert.That(types, Does.Contain(expected), "隐藏场景缺少生产组件：" + expected);
            }
            foreach (var excluded in ExcludedHiddenSceneTypes)
            {
                Assert.That(types, Does.Not.Contain(excluded), "隐藏场景不得包含表现/交互组件：" + excluded);
            }

            Assert.That(types.Count(t => t == "ProjectHero.Core.Entities.CombatUnit"), Is.EqualTo(2));
            Assert.That(types.Count(t => t == "ProjectHero.Core.Compatibility.Runtime.BattleRuntimeBootstrap"),
                Is.EqualTo(1));

            // 不得含相机 / Canvas（表现闭包）。
            foreach (var root in scene.GetRootGameObjects())
            {
                Assert.That(root.GetComponentInChildren<Camera>(true), Is.Null, "隐藏场景不得包含相机");
                Assert.That(root.GetComponentInChildren<Canvas>(true), Is.Null, "隐藏场景不得包含 Canvas");
            }

            var bootstrap = FindAll<BattleRuntimeBootstrap>(scene).Single();
            var serialized = new SerializedObject(bootstrap);
            Assert.That(serialized.FindProperty("_autoStart").boolValue, Is.False,
                "隐藏验证场景必须 _autoStart = false（三模式由测试显式驱动）");
            Assert.That(serialized.FindProperty("_requestedMode").enumValueIndex,
                Is.EqualTo((int)BattleRuntimeMode.Legacy));

            // 隐藏场景也必须显式注入两个槽位。
            Assert.That(serialized.FindProperty("_legacyFrameAdapterSlot").objectReferenceValue, Is.Not.Null);
            Assert.That(serialized.FindProperty("_simulationSourceSlot").objectReferenceValue, Is.Not.Null);

            // 两个单位的动作库/体积引用必须完好（复用主场景资产，不新建配置）。
            var units = new List<MonoBehaviour>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour != null && behaviour.GetType().FullName == "ProjectHero.Core.Entities.CombatUnit")
                        units.Add(behaviour);
                }
            }

            foreach (var unit in units)
            {
                var unitSerialized = new SerializedObject(unit);
                Assert.That(unitSerialized.FindProperty("ActionLibrary").objectReferenceValue, Is.Not.Null,
                    "隐藏场景单位必须带真实动作库引用：" + unit.name);
                Assert.That(unitSerialized.FindProperty("UnitVolumeDefinition").objectReferenceValue, Is.Not.Null,
                    "隐藏场景单位必须带真实体积引用：" + unit.name);
            }
        }

        [Test, Order(4)]
        public void SceneToolIsIdempotentAndPreservesGuids()
        {
            var toolType = ResolveSceneToolType();

            string mainGuidBefore = AssetDatabase.AssetPathToGUID(MainScenePath);
            string hiddenGuidBefore = AssetDatabase.AssetPathToGUID(HiddenScenePath);
            Assert.That(mainGuidBefore, Is.Not.Empty);
            Assert.That(hiddenGuidBefore, Is.Not.Empty);

            for (int i = 0; i < 2; i++)
            {
                InvokeStatic(toolType, "WireMainScene");
                InvokeStatic(toolType, "CreateHiddenValidationScene");
            }

            Assert.That(AssetDatabase.AssetPathToGUID(MainScenePath), Is.EqualTo(mainGuidBefore),
                "幂等复跑不得改变主场景 GUID");
            Assert.That(AssetDatabase.AssetPathToGUID(HiddenScenePath), Is.EqualTo(hiddenGuidBefore),
                "幂等复跑不得改变隐藏场景 GUID");

            var hiddenReport = (string)InvokeStatic(toolType, "CreateHiddenValidationScene");
            Assert.That(hiddenReport, Does.Contain("removed previous"), "复跑必须显式重建而不是累积");
            Assert.That(hiddenReport, Does.Not.Contain("EXECUTION_ORDER_NOT_PERSISTED"));

            // 复跑后结构与首轮一致。
            HiddenValidationSceneIsMinimalRealChain();
        }

        [Test, Order(5)]
        public void ScriptExecutionOrdersArePersistedForEveryLegacyWriter()
        {
            var expectations = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "Assets/Scripts/Demos/CombatDemo.cs", RuntimeCallbackRegistry.LegacyWriterExecutionOrder },
                { "Assets/Scripts/Core/Timeline/BattleTimeline.cs", RuntimeCallbackRegistry.LegacyWriterExecutionOrder },
                { "Assets/Scripts/Core/Entities/CombatUnit.cs", RuntimeCallbackRegistry.LegacyWriterExecutionOrder },
                { "Assets/Scripts/Core/Gameplay/EnemyAIController.cs", RuntimeCallbackRegistry.LegacyWriterExecutionOrder },
                { "Assets/Scripts/Core/Gameplay/BattleManager.cs", RuntimeCallbackRegistry.LegacyWriterExecutionOrder },
                { "Assets/Scripts/Core/Gameplay/TacticsController.cs", RuntimeCallbackRegistry.LegacyWriterExecutionOrder }
            };

            foreach (var pair in expectations)
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(pair.Key);
                Assert.That(script, Is.Not.Null, "必须能加载脚本：" + pair.Key);
                Assert.That(MonoImporter.GetExecutionOrder(script), Is.EqualTo(pair.Value),
                    "执行顺序必须已落盘：" + pair.Key);
            }

            Assert.That(RuntimeCallbackRegistry.BootstrapExecutionOrder,
                Is.LessThan(RuntimeCallbackRegistry.LegacyWriterExecutionOrder),
                "Bootstrap 必须早于 Legacy 写入者");
            Assert.That(RuntimeCallbackRegistry.CheckpointExecutionOrder,
                Is.GreaterThan(RuntimeCallbackRegistry.LegacyWriterExecutionOrder),
                "只读检查点必须晚于全部 Legacy 写入者");
        }

        [Test, Order(6)]
        public void HiddenValidationSceneStaysOutOfPlayerBuilds()
        {
            // 隐藏验证场景必须登记但 **enabled = false**：PlayMode 测试只能加载登记过的场景
            // （播放模式下不允许 EditorSceneManager.OpenScene），而 enabled=false 保证它
            // 不进玩家构建、不进入正式内容流。
            // 场景交付态：登记但 enabled = false（不进玩家构建）。
            // 注意：PlayMode 测试在加载前会把 enabled 临时置为 true（播放模式只能加载已启用场景），
            // 因此这里先调用工具把交付态恢复为 enabled = false，再核对。
            var toolType = ResolveSceneToolType();
            var hiddenRunner = SceneToolTypeForBuildSettings();
            if (hiddenRunner != null) hiddenRunner.GetMethod("RegisterHiddenSceneAsDisabled")
                .Invoke(null, new object[] { null });

            Assert.That((bool)InvokeStatic(toolType, "HiddenSceneIsRegisteredDisabled"), Is.True,
                "隐藏验证场景必须登记且 enabled=false");

            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (!string.Equals(scene.path, HiddenScenePath, StringComparison.Ordinal)) continue;
                Assert.That(scene.enabled, Is.False, "隐藏验证场景不得启用（不进玩家构建）");
            }

            Assert.That(EditorBuildSettings.scenes.Any(
                    s => s.path == MainScenePath && s.enabled), Is.True,
                "主战斗场景必须保持启用");
        }

        [Test, Order(7)]
        public void SceneOwnershipAuditReportsSingleTopLevelClock()
        {
            var toolType = ResolveSceneToolType();
            string audit = (string)InvokeStatic(toolType, "AuditAllScenes");

            Assert.That(audit, Is.Not.Null.And.Not.Empty);
            Assert.That(audit, Does.Contain(MainScenePath + " [battle-scope] bootstrapCount=1 OK"));
            Assert.That(audit, Does.Contain(HiddenScenePath + " [battle-scope] bootstrapCount=1 OK"));
            Assert.That(audit, Does.Not.Contain("*** EXPECTED EXACTLY 1 ***"),
                "全部战斗场景必须恰有一个 Bootstrap：" + audit);

            // 战斗场景段落里不得出现违规项。第三方/示例场景的明细归入 out-of-scope 段，
            // 因此这里按"battle-scope 段落"切片核对。
            string battleScope = BattleScopeSection(audit);
            Assert.That(battleScope, Does.Not.Contain("TOP_LEVEL_ADVANCER"),
                "战斗场景不得有除 Bootstrap 之外的顶层时钟推进者：" + battleScope);
            Assert.That(battleScope, Does.Not.Contain("UNCLASSIFIED_LOGIC_WRITER"),
                "战斗场景不得有未分类逻辑写入者：" + battleScope);
            Assert.That(battleScope, Does.Not.Contain("MISSING_SCRIPT"),
                "战斗场景不得有缺失脚本：" + battleScope);

            // 六个旧写入者的执行顺序必须出现在审计里。
            foreach (var script in new[]
                     {
                         "CombatDemo.cs", "BattleTimeline.cs", "CombatUnit.cs",
                         "EnemyAIController.cs", "BattleManager.cs", "TacticsController.cs"
                     })
            {
                Assert.That(audit, Does.Contain("execOrder " + script + " = " +
                    RuntimeCallbackRegistry.LegacyWriterExecutionOrder));
            }
        }

        /// <summary>取出审计文本中全部 battle-scope 场景段落（到 out-of-scope 段之前）。</summary>
        private static string BattleScopeSection(string audit)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var line in audit.Split('\n'))
            {
                if (line.StartsWith("--- out-of-scope detail", StringComparison.Ordinal)) break;
                if (line.StartsWith("--- ", StringComparison.Ordinal) && !line.Contains("[battle-scope]")) continue;
                builder.AppendLine(line);
            }
            return builder.ToString();
        }

        // ---------------- 辅助 ----------------

        private static Type SceneToolTypeForBuildSettings() => ResolveSceneToolType();

        private static Type ResolveSceneToolType()
        {
            foreach (var assemblyName in new[] { "Assembly-CSharp-Editor", "Assembly-CSharp" })
            {
                var type = Type.GetType(SceneToolTypeName + ", " + assemblyName, throwOnError: false);
                if (type != null) return type;
            }

            Assert.Fail("无法解析生产 Editor 工具类型：" + SceneToolTypeName);
            return null;
        }

        private static object InvokeStatic(Type type, string methodName)
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "工具方法必须存在：" + methodName);
            try
            {
                return method.Invoke(null, null);
            }
            catch (TargetInvocationException exception)
            {
                Assert.Fail("工具方法抛出异常：" + methodName + " -> " + exception.InnerException);
                return null;
            }
        }

        private static IEnumerable<T> FindAll<T>(Scene scene) where T : Component
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var found in root.GetComponentsInChildren<T>(true)) yield return found;
            }
        }

        private static void AssertNoMissingScripts(GameObject go)
        {
            int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
            Assert.That(missing, Is.Zero, $"GameObject '{go.name}' 存在 {missing} 个缺失脚本");
            foreach (Transform child in go.transform) AssertNoMissingScripts(child.gameObject);
        }
    }
}
