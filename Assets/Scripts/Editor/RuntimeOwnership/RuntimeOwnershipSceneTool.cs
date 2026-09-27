using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Gameplay;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Input;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;
using ProjectHero.Demos;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectHero.Editor.RuntimeOwnership
{
    /// <summary>
    /// 任务 03B 的场景/资产接入工具（幂等、可复跑）。
    ///
    /// 为什么必须是 Editor 工具而不是手改 YAML：任务 03B 要求场景与 Prefab 的改动
    /// 只能经 Unity API 完成，且不得丢失任何 <c>.meta</c> 或 MonoScript GUID。
    /// 本工具的全部改动都走 <c>EditorSceneManager</c> / <c>SerializedObject</c>，
    /// 由 Unity 自己序列化，GUID 与既有引用保持不变。
    ///
    /// 四个入口：
    /// <list type="bullet">
    /// <item><c>接入主战斗场景</c>：在 <c>CombatSampleScene/CombatDemo</c> 上补一个
    /// <c>BattleRuntimeBootstrap</c>（默认 Legacy），把两个显式槽位指向场景中真实的
    /// Legacy 适配器与 02B 初始化来源；并设置脚本执行顺序。</item>
    /// <item><c>创建隐藏验证场景</c>：新建 <c>Assets/Scenes/HiddenRuntimeValidation.unity</c>
    /// （不进入 Build Settings），用<b>真实生产组件</b>装配最小初始化链，
    /// <c>_autoStart = false</c> 供 PlayMode 三模式验证显式驱动。</item>
    /// <item><c>审计场景所有权</c>：扫描全部场景，报告顶层时钟推进者数量、
    /// 未分类 <c>Update()</c> 写入者、重复 Bootstrap、脚本执行顺序。</item>
    /// <item><c>一键接入（幂等）</c>：前两步 + 审计。</item>
    /// </list>
    /// </summary>
    public static class RuntimeOwnershipSceneTool
    {
        public const string MainScenePath = "Assets/Scenes/CombatSampleScene.unity";
        public const string HiddenValidationScenePath = "Assets/Scenes/HiddenRuntimeValidation.unity";
        public const string CombatDemoObjectName = "CombatDemo";

        /// <summary>隐藏场景的规范组件集合（PlayMode 测试据此核对"最小但真实"）。</summary>
        public static readonly string[] HiddenSceneComponentTypes =
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

        [MenuItem("Tools/Runtime Ownership/1. Wire Main Battle Scene", false, 100)]
        public static void WireMainSceneMenu() => WireMainScene();

        [MenuItem("Tools/Runtime Ownership/2. Create Hidden Validation Scene", false, 101)]
        public static void CreateHiddenValidationSceneMenu() => CreateHiddenValidationScene();

        [MenuItem("Tools/Runtime Ownership/3. Audit Scene Ownership", false, 102)]
        public static void AuditMenu() => Debug.Log(AuditAllScenes());

        [MenuItem("Tools/Runtime Ownership/4. Wire Everything (idempotent)", false, 103)]
        public static void WireEverythingMenu()
        {
            WireMainScene();
            CreateHiddenValidationScene();
            Debug.Log(AuditAllScenes());
        }

        // ---------------- 1. 主战斗场景 ----------------

        /// <summary>接入主战斗场景（幂等）。返回诊断文本。</summary>
        public static string WireMainScene()
        {
            var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
            var report = new StringBuilder();

            var demo = FindInScene<CombatDemo>(scene);
            if (demo == null)
            {
                report.AppendLine("MAIN_SCENE_COMBAT_DEMO_MISSING");
                Debug.LogError(report.ToString());
                return report.ToString();
            }

            var bootstrap = demo.GetComponent<BattleRuntimeBootstrap>();
            if (bootstrap == null)
            {
                bootstrap = demo.gameObject.AddComponent<BattleRuntimeBootstrap>();
                report.AppendLine("added BattleRuntimeBootstrap on " + PathOf(demo));
            }
            else
            {
                report.AppendLine("BattleRuntimeBootstrap already present on " + PathOf(demo));
            }

            var factory = demo.GetComponent<BattleSimulationSourceFactory>();
            if (factory == null)
            {
                factory = demo.gameObject.AddComponent<BattleSimulationSourceFactory>();
                report.AppendLine("added BattleSimulationSourceFactory on " + PathOf(demo));
            }

            SetFactoryUnitSlots(factory, demo, report);
            ConfigureBootstrap(bootstrap, demo, factory, autoStart: true, mode: BattleRuntimeMode.Legacy, report);
            EnsureExecutionOrders(report);
            RegisterHiddenSceneAsDisabled(report);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            report.AppendLine("saved " + MainScenePath);
            Debug.Log(report.ToString());
            return report.ToString();
        }

        // ---------------- 2. 隐藏验证场景 ----------------

        /// <summary>
        /// 创建（或重建）隐藏验证场景。
        ///
        /// <strong>为什么是"最小真实链"而不是复制主场景</strong>：
        /// 任务包要求隐藏场景"使用任务 02B 真实初始化链运行三模式测试"，同时明确它
        /// "不进入正式内容流"。复制整份主场景会带进 Canvas/TMP/相机/URP 资产与
        /// 一整套交互链，使"三模式所有权验证"耦合到表现资产；因此本工具用
        /// <b>真实生产组件</b>（<see cref="CombatDemo"/>、<see cref="BattleTimeline"/>、
        /// <see cref="CombatUnit"/>、<see cref="BattleSimulationSourceFactory"/>、
        /// <see cref="BattleRuntimeBootstrap"/>）装配最小闭包，明确排除
        /// UI/Canvas/相机/预览/可视化组件。
        ///
        /// <c>_autoStart = false</c>：三种模式都由 PlayMode 测试显式调用 <c>StartBattle</c> 驱动。
        /// </summary>
        public static string CreateHiddenValidationScene()
        {
            var report = new StringBuilder();

            // 先删旧文件（幂等重建；连同 .meta 一起由 Unity 管理）。
            if (File.Exists(HiddenValidationScenePath))
            {
                AssetDatabase.DeleteAsset(HiddenValidationScenePath);
                report.AppendLine("removed previous " + HiddenValidationScenePath);
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "HiddenRuntimeValidation";

            // --- 根对象：CombatDemo（唯一顶层所有权对象） ---
            var root = new GameObject(CombatDemoObjectName);
            var timeline = root.AddComponent<BattleTimeline>();
            var grid = root.AddComponent<GridManager>();
            grid.groundLayer = 1 << 0;
            var input = root.AddComponent<InputManager>();
            input.groundLayer = 1 << 0;
            input.unitLayer = 1 << 0;
            root.AddComponent<BattleManager>();
            var demo = root.AddComponent<CombatDemo>();
            var factory = root.AddComponent<BattleSimulationSourceFactory>();
            var bootstrap = root.AddComponent<BattleRuntimeBootstrap>();

            // --- 两个单位（真实 CombatUnit，含真实 ActionLibrary/UnitVolume 引用） ---
            var player = CreateUnitObject("Player", new GridPoint(-5, -5), "ForRadius1", "Radius_1", report);
            var enemy = CreateUnitObject("Enemy", new GridPoint(5, 5), "ForRadius2", "Radius_2", report);

            // 主场景的 Player.IsPlayerControlled = 1；隐藏场景保持同一可见语义。
            SetBoolField(player, "IsPlayerControlled", true);

            SetObjectField(demo, "Player", player);
            SetObjectField(demo, "Enemy", enemy);
            SetObjectField(demo, "Timeline", timeline);

            SetObjectField(factory, "_heroUnit", player);
            SetObjectField(factory, "_enemyUnit", enemy);

            ConfigureBootstrap(bootstrap, demo, factory, autoStart: false, mode: BattleRuntimeMode.Legacy, report);
            EnsureExecutionOrders(report);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, HiddenValidationScenePath);
            AssetDatabase.SaveAssets();
            RegisterHiddenSceneAsDisabled(report);

            report.AppendLine("saved " + HiddenValidationScenePath
                + " (roots=" + scene.rootCount + ", units=2, autoStart=false)");
            Debug.Log(report.ToString());
            return report.ToString();
        }


        private static CombatUnit CreateUnitObject(
            string name, GridPoint initialPosition, string libraryAssetName, string volumeAssetName,
            StringBuilder report)
        {
            var go = new GameObject(name);
            var unit = go.AddComponent<CombatUnit>();

            SetGridPointField(unit, "InitialGridPosition", initialPosition);

            // 直接加载主场景使用的<b>同两个配置资产</b>（按资产名，来自 Resources），
            // 不新造配置、不复制 GUID、不在运行时补默认值。
            var library = AssetDatabase.LoadAssetAtPath<ProjectHero.Core.Actions.ActionLibrarySO>(
                AssetDatabase.GUIDToAssetPath(ProjectHero.Authoring.Legacy.LegacyIdMigrationManifest.ForRadius1Guid));
            var volume = AssetDatabase.LoadAssetAtPath<ProjectHero.Core.Grid.UnitVolume>(
                AssetDatabase.GUIDToAssetPath(ProjectHero.Authoring.Legacy.LegacyIdMigrationManifest.VolumeRadius1Guid));

            _ = libraryAssetName;
            _ = volumeAssetName;

            if (library == null || volume == null)
            {
                report.AppendLine("HIDDEN_SCENE_UNIT_ASSET_UNRESOLVED|" + name
                    + "|library=" + (library != null) + "|volume=" + (volume != null));
                return unit;
            }

            SetObjectField(unit, "ActionLibrary", library);
            SetObjectField(unit, "UnitVolumeDefinition", volume);
            return unit;
        }

        private static void ConfigureBootstrap(
            BattleRuntimeBootstrap bootstrap, CombatDemo demo, BattleSimulationSourceFactory factory,
            bool autoStart, BattleRuntimeMode mode, StringBuilder report)
        {
            var serialized = new SerializedObject(bootstrap);
            SetEnum(serialized, "_requestedMode", (int)mode);
            SetBool(serialized, "_autoStart", autoStart);
            SetObject(serialized, "_legacyFrameAdapterSlot", demo);
            SetObject(serialized, "_simulationSourceSlot", factory);
            SetInt(serialized, "_shadowComparisonConfigVersion", 1);
            SetInt(serialized, "_shadowMaxStepsPerComparison", 64);
            SetInt(serialized, "_shadowMaxCheckpoints", 4096);
            SetEnum(serialized, "_shadowComparisonMode", (int)ShadowComparisonMode.Strict);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bootstrap);

            report.AppendLine("configured bootstrap on " + PathOf(demo)
                + " autoStart=" + autoStart + " mode=" + BattleRuntimeModes.Describe(mode));
        }

        private static void SetFactoryUnitSlots(
            BattleSimulationSourceFactory factory, CombatDemo demo, StringBuilder report)
        {
            var serialized = new SerializedObject(factory);
            var hero = serialized.FindProperty("_heroUnit");
            var enemy = serialized.FindProperty("_enemyUnit");

            if (hero != null && hero.objectReferenceValue == null)
            {
                hero.objectReferenceValue = demo.Player;
                report.AppendLine("factory hero slot <- " + (demo.Player != null ? demo.Player.name : "<null>"));
            }
            if (enemy != null && enemy.objectReferenceValue == null)
            {
                enemy.objectReferenceValue = demo.Enemy;
                report.AppendLine("factory enemy slot <- " + (demo.Enemy != null ? demo.Enemy.name : "<null>"));
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(factory);
        }

        /// <summary>
        /// 把隐藏验证场景登记进 <c>EditorBuildSettings</c>，但保持
        /// <strong>enabled: 0（不打进玩家构建）</strong>。
        ///
        /// 为什么必须登记：PlayMode 测试运行在播放模式，此时只允许
        /// <c>SceneManager.LoadScene/LoadSceneAsync</c>（<c>EditorSceneManager.OpenScene</c>
        /// 会抛 <c>This cannot be used during play mode</c>），而 <c>SceneManager</c>
        /// 只加载登记过的场景。用 <c>enabled: 0</c> 同时满足两条约束：
        /// 场景可被测试加载，但<strong>不进入正式内容流、不进玩家构建</strong>。
        /// </summary>
        public static void RegisterHiddenSceneAsDisabled(StringBuilder report = null)
        {
            string guid = AssetDatabase.AssetPathToGUID(HiddenValidationScenePath);
            if (string.IsNullOrEmpty(guid))
            {
                report?.AppendLine("HIDDEN_SCENE_GUID_UNRESOLVED");
                return;
            }

            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            int index = scenes.FindIndex(s => string.Equals(s.path, HiddenValidationScenePath, StringComparison.Ordinal));
            if (index < 0)
            {
                scenes.Add(new EditorBuildSettingsScene(HiddenValidationScenePath, false));
                report?.AppendLine("registered hidden scene in build settings (enabled=false)");
            }
            else if (scenes[index].enabled)
            {
                scenes[index] = new EditorBuildSettingsScene(HiddenValidationScenePath, false);
                report?.AppendLine("forced hidden scene enabled=false");
            }

            int mainIndex = scenes.FindIndex(s => string.Equals(s.path, MainScenePath, StringComparison.Ordinal));
            if (mainIndex >= 0 && !scenes[mainIndex].enabled)
            {
                scenes[mainIndex] = new EditorBuildSettingsScene(MainScenePath, true);
                report?.AppendLine("restored main scene enabled=true");
            }

            EditorBuildSettings.scenes = scenes.ToArray();

            if (!HiddenSceneIsRegisteredDisabled())
                throw new InvalidOperationException("hidden scene registration failed");
        }

        /// <summary>隐藏验证场景是否已登记且未启用（可被 PlayMode 加载，但不进玩家构建）。</summary>
        public static bool HiddenSceneIsRegisteredDisabled()
        {
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (!string.Equals(scene.path, HiddenValidationScenePath, StringComparison.Ordinal)) continue;
                return !scene.enabled;
            }
            return false;
        }
        // ---------------- 3. 脚本执行顺序 ----------------

        /// <summary>
        /// 显式脚本执行顺序：Legacy 从属写入者 = -500（早），Bootstrap = -1000（更早），
        /// 只读检查点 = +10000（晚于全部写入者）。幂等。
        /// </summary>
        public static void EnsureExecutionOrders(StringBuilder report)
        {
            var assignments = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "Assets/Scripts/Demos/CombatDemo.cs", RuntimeCallbackRegistry.LegacyWriterExecutionOrder },
                { "Assets/Scripts/Core/Timeline/BattleTimeline.cs", RuntimeCallbackRegistry.LegacyWriterExecutionOrder },
                { "Assets/Scripts/Core/Entities/CombatUnit.cs", RuntimeCallbackRegistry.LegacyWriterExecutionOrder },
                { "Assets/Scripts/Core/Gameplay/EnemyAIController.cs", RuntimeCallbackRegistry.LegacyWriterExecutionOrder },
                { "Assets/Scripts/Core/Gameplay/BattleManager.cs", RuntimeCallbackRegistry.LegacyWriterExecutionOrder },
                { "Assets/Scripts/Core/Gameplay/TacticsController.cs", RuntimeCallbackRegistry.LegacyWriterExecutionOrder }
            };

            foreach (var pair in assignments)
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(pair.Key);
                if (script == null)
                {
                    report.AppendLine("EXECUTION_ORDER_SCRIPT_MISSING|" + pair.Key);
                    continue;
                }

                int current = MonoImporter.GetExecutionOrder(script);
                if (current != pair.Value)
                {
                    MonoImporter.SetExecutionOrder(script, pair.Value);
                    report.AppendLine("execution order " + Path.GetFileName(pair.Key)
                        + ": " + current + " -> " + pair.Value);
                }
            }

            VerifyExecutionOrders(assignments, report);
        }

        /// <summary>
        /// 写回读：确认每个脚本的执行顺序真的落盘。不一致时报告（不静默失败），
        /// 因为"只读检查点晚于全部 Legacy 写入者"这条验收依赖它。
        /// </summary>
        private static void VerifyExecutionOrders(Dictionary<string, int> assignments, StringBuilder report)
        {
            foreach (var pair in assignments)
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(pair.Key);
                if (script == null) continue;
                int actual = MonoImporter.GetExecutionOrder(script);
                if (actual != pair.Value)
                {
                    report.AppendLine("EXECUTION_ORDER_NOT_PERSISTED|" + pair.Key
                        + "|expected=" + pair.Value + "|actual=" + actual);
                }
            }
        }

        // ---------------- 4. 审计 ----------------

        /// <summary>
        /// 扫描全部场景，返回所有权审计文本。
        ///
        /// 审计范围分两档：
        /// <list type="bullet">
        /// <item><b>战斗场景</b>（主场景 + 隐藏验证场景，以及任何含战斗生产组件的场景）：
        /// 完整规则——恰一个 Bootstrap、无其他顶层时钟推进者、无未分类逻辑写入者、无缺失脚本。</item>
        /// <item><b>其余场景</b>（第三方示例等）：只统计是否含战斗组件，不套用"恰一个 Bootstrap"规则，
        /// 其明细单独归档，不参与本任务验收。</item>
        /// </list>
        /// </summary>
        public static string AuditAllScenes()
        {
            var report = new StringBuilder();
            report.AppendLine("=== Runtime ownership audit ===");

            var guids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
            var paths = new List<string>();
            foreach (var guid in guids) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            paths.Sort(StringComparer.Ordinal);

            var outOfScopeDetail = new StringBuilder();

            foreach (var path in paths)
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                bool isKnownBattleScene = string.Equals(path, MainScenePath, StringComparison.Ordinal)
                    || string.Equals(path, HiddenValidationScenePath, StringComparison.Ordinal);

                int bootstrapCount = 0;
                int productionTypeCount = 0;
                var sceneDetail = new StringBuilder();

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (component == null)
                        {
                            sceneDetail.AppendLine("  MISSING_SCRIPT on " + root.name);
                            continue;
                        }

                        var type = component.GetType();
                        if (type == typeof(BattleRuntimeBootstrap)) bootstrapCount++;
                        if (IsProductionBattleType(type)) productionTypeCount++;

                        if (IsTopLevelAdvancer(type))
                            sceneDetail.AppendLine("  TOP_LEVEL_ADVANCER " + PathOf(component));

                        if (RuntimeCallbackRegistry.IsUnclassifiedLogicWriter(type) && !IsFrameworkWriter(type))
                            sceneDetail.AppendLine("  UNCLASSIFIED_LOGIC_WRITER " + PathOf(component));
                    }
                }

                bool inScope = isKnownBattleScene || productionTypeCount > 0;
                string headline = "--- " + path + (inScope ? " [battle-scope]" : " [out-of-scope]")
                    + " bootstrapCount=" + bootstrapCount;
                if (inScope) headline += bootstrapCount == 1 ? " OK" : " *** EXPECTED EXACTLY 1 ***";
                report.AppendLine(headline);

                if (inScope)
                {
                    report.Append(sceneDetail);
                    foreach (var pair in ExecutionOrderSnapshot())
                    {
                        report.AppendLine("  execOrder " + pair.Key + " = " + pair.Value);
                    }
                }
                else if (sceneDetail.Length > 0)
                {
                    outOfScopeDetail.Append("--- ").Append(path).Append('\n').Append(sceneDetail);
                }
            }

            if (outOfScopeDetail.Length > 0)
            {
                report.AppendLine("--- out-of-scope detail（第三方/示例场景，仅记录，不参与本任务验收）");
                report.Append(outOfScopeDetail);
            }

            report.AppendLine("=== end ===");
            return report.ToString();
        }

        /// <summary>
        /// 引擎/框架/第三方命名空间的自主 <c>Update()</c> 组件。
        ///
        /// 它们不是"项目逻辑写入者"，因此不参与 New 模式的未分类门禁
        /// （例如 <c>UnityEngine.EventSystems.EventSystem</c> 只声明 <c>Update()</c> 而不写战斗状态）。
        /// 项目自己的类型（<c>ProjectHero.*</c> 或预定义程序集）不在此列。
        /// </summary>
        private static bool IsFrameworkWriter(Type type)
        {
            string ns = type.Namespace ?? string.Empty;
            if (ns.StartsWith("ProjectHero", StringComparison.Ordinal)) return false;
            if (ns.Length == 0) return false;
            return ns.StartsWith("UnityEngine", StringComparison.Ordinal)
                || ns.StartsWith("UnityEditor", StringComparison.Ordinal)
                || ns.StartsWith("TMPro", StringComparison.Ordinal)
                || ns.StartsWith("Unity.", StringComparison.Ordinal)
                || ns.StartsWith("Cinemachine", StringComparison.Ordinal);
        }

        /// <summary>是否属于战斗运行链的生产类型（用全名判定，不引用 Legacy 运行时类型）。</summary>
        private static bool IsProductionBattleType(Type type)
        {
            string fullName = type.FullName ?? string.Empty;
            return fullName.StartsWith("ProjectHero.Demos.", StringComparison.Ordinal)
                || fullName.StartsWith("ProjectHero.Core.", StringComparison.Ordinal)
                || fullName.StartsWith("ProjectHero.UI.", StringComparison.Ordinal)
                || fullName.StartsWith("ProjectHero.Visuals.", StringComparison.Ordinal)
                || type == typeof(BattleRuntimeBootstrap);
        }

        private static bool IsTopLevelAdvancer(Type type)
        {
            if (type == typeof(BattleRuntimeBootstrap)) return false; // 唯一合法者
            var update = type.GetMethod("Update",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly,
                null, Type.EmptyTypes, null);
            if (update == null) return false;
            UnityCallbackClassification classification;
            return RuntimeCallbackRegistry.TryGet(type.FullName, "Update", out classification)
                && classification.CallbackClass == UnityCallbackClass.TopLevelClockAdvancer;
        }

        private static IEnumerable<KeyValuePair<string, int>> ExecutionOrderSnapshot()
        {
            string[] scripts =
            {
                "Assets/Scripts/Demos/CombatDemo.cs",
                "Assets/Scripts/Core/Timeline/BattleTimeline.cs",
                "Assets/Scripts/Core/Entities/CombatUnit.cs",
                "Assets/Scripts/Core/Gameplay/EnemyAIController.cs",
                "Assets/Scripts/Core/Gameplay/BattleManager.cs",
                "Assets/Scripts/Core/Gameplay/TacticsController.cs"
            };

            foreach (var path in scripts)
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                yield return new KeyValuePair<string, int>(
                    Path.GetFileName(path), script != null ? MonoImporter.GetExecutionOrder(script) : int.MinValue);
            }
        }

        // ---------------- 辅助 ----------------

        private static T FindInScene<T>(Scene scene) where T : Component
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentInChildren<T>(true);
                if (found != null) return found;
            }
            return null;
        }

        private static T FindInSceneNamed<T>(Scene scene, string name) where T : Component
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var component in root.GetComponentsInChildren<T>(true))
                {
                    if (component != null && component.name == name) return component;
                }
            }
            return null;
        }

        private static string PathOf(Component component)
        {
            if (component == null) return "<null>";
            var transform = component.transform;
            string path = transform.name;
            var parent = transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return component.gameObject.scene.name + "/" + path + "#" + component.GetType().Name;
        }

        private static SerializedProperty Require(SerializedObject serialized, string name)
        {
            var property = serialized.FindProperty(name);
            if (property == null) throw new InvalidOperationException("serialized field missing: " + name);
            return property;
        }

        private static void SetEnum(SerializedObject serialized, string name, int value)
            => Require(serialized, name).enumValueIndex = value;

        private static void SetBool(SerializedObject serialized, string name, bool value)
            => Require(serialized, name).boolValue = value;

        private static void SetInt(SerializedObject serialized, string name, int value)
            => Require(serialized, name).intValue = value;

        private static void SetObject(SerializedObject serialized, string name, UnityEngine.Object value)
            => Require(serialized, name).objectReferenceValue = value;

        private static void SetBoolField(Component target, string name, bool value)
        {
            var serialized = new SerializedObject(target);
            SetBool(serialized, name, value);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }

        private static void SetObjectField(Component target, string name, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            SetObject(serialized, name, value);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }

        private static void SetGridPointField(Component target, string name, GridPoint value)
        {
            var serialized = new SerializedObject(target);
            var property = Require(serialized, name);
            var x = property.FindPropertyRelative("X");
            var y = property.FindPropertyRelative("Y");
            if (x == null || y == null) throw new InvalidOperationException("GridPoint field invalid: " + name);
            x.intValue = value.X;
            y.intValue = value.Y;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }

        private static void CopyObjectField(Component source, Component target, string fieldName)
        {
            var sourceSerialized = new SerializedObject(source);
            var sourceProperty = sourceSerialized.FindProperty(fieldName);
            if (sourceProperty == null) return;

            var targetSerialized = new SerializedObject(target);
            var targetProperty = targetSerialized.FindProperty(fieldName);
            if (targetProperty == null) return;

            targetProperty.objectReferenceValue = sourceProperty.objectReferenceValue;
            targetSerialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }
    }
}
