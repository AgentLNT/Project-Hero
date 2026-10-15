using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public abstract class ProductionRuntimeTestBase
    {
        public const string MainSceneName = "CombatSampleScene";
        public const string HiddenValidationSceneName = "HiddenRuntimeValidation";
        public const string HiddenValidationScenePath = "Assets/Diagnostics/Editor/HiddenRuntimeValidation.unity";
        public const string HiddenSceneAssetPath = "Assets/Diagnostics/Editor/HiddenRuntimeValidation.unity";
        public const string MainScenePath = "Assets/Scenes/CombatSampleScene.unity";

        protected readonly List<GameObject> TemporaryObjects = new List<GameObject>();
        protected readonly List<UnityEngine.Object> TemporaryAssets = new List<UnityEngine.Object>();

        protected BattleRuntimeBootstrap Bootstrap { get; private set; }

        protected bool UsingProductionScene { get; private set; }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            LogAssert.ignoreFailingMessages = false;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = 1f;

            for (int i = 0; i < TemporaryObjects.Count; i++)
            {
                if (TemporaryObjects[i] != null) UnityEngine.Object.Destroy(TemporaryObjects[i]);
            }
            TemporaryObjects.Clear();

            for (int i = 0; i < TemporaryAssets.Count; i++)
            {
                if (TemporaryAssets[i] != null) UnityEngine.Object.Destroy(TemporaryAssets[i]);
            }
            TemporaryAssets.Clear();

            Bootstrap = null;
            UsingProductionScene = false;

            // 让 Destroy 真正落地、让 OnDestroy 把实例从静态表里摘掉，
            // 否则下一条用例的"恰有一个 Bootstrap"检查会看到上一轮的残留。
            yield return null;
            yield return null;
        }

        // ---------------- 场景装载 ----------------

        protected IEnumerator LoadHiddenValidationScene()
        {
            Assert.That(System.IO.File.Exists(HiddenValidationScenePath), Is.True,
                "隐藏验证场景资产必须存在：" + HiddenValidationScenePath);

#if UNITY_EDITOR
            var parameters = new LoadSceneParameters(LoadSceneMode.Single);
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                HiddenValidationScenePath, parameters);
            yield return null;
#else
            var request = SceneManager.LoadSceneAsync(HiddenValidationScenePath, LoadSceneMode.Single);
            yield return request;
            yield return null;
#endif

            var active = SceneManager.GetActiveScene();
            Assert.That(active.name, Is.EqualTo(HiddenValidationSceneName),
                "隐藏验证场景必须真的被加载（活动场景=" + active.name + " path=" + active.path + "）"
                + "；不得退化为在当前场景中实例化资产根对象");

            ResolveBootstrap();
            UsingProductionScene = true;
        }

        protected IEnumerator LoadMainScene()
        {
            if (UseLegacyComparisonScene)
            {
                yield return LoadLegacyComparisonScene();
                yield break;
            }
            yield return SceneManager.LoadSceneAsync(MainSceneName, LoadSceneMode.Single);
            yield return null;
            ResolveBootstrap();
            UsingProductionScene = true;
        }
        protected virtual bool UseLegacyComparisonScene => false;
        // Historical scene regressions use active time; M1 scenarios explicitly opt into auto-pause.
        protected virtual bool UseHudAutoPause => false;
        protected IEnumerator LoadLegacyComparisonScene()
        {
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                "Assets/Diagnostics/Editor/LegacyComparisonScene.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("LegacyComparisonScene"));
            UsingProductionScene = true;
#else
            throw new InvalidOperationException("LEGACY_COMPARISON_EDITOR_ONLY");
#endif
        }

#if UNITY_EDITOR
        protected static string HiddenSceneAssetText()
        {
            Assert.That(System.IO.File.Exists(HiddenSceneAssetPath), Is.True,
                "隐藏验证场景资产必须存在于磁盘：" + HiddenSceneAssetPath);
            return System.IO.File.ReadAllText(HiddenSceneAssetPath);
        }

        protected static string ScriptGuid(string scriptPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(scriptPath);
            Assert.That(guid, Is.Not.Empty, "脚本资产必须存在：" + scriptPath);
            return guid;
        }
#endif
        protected void ResolveBootstrap(bool requireExactlyOne = true)
        {
            var snapshot = BattleRuntimeBootstrap.EnumerateLiveInstances();
            Bootstrap = null;
            int count = 0;
            var paths = new List<string>();

            for (int i = 0; i < snapshot.Count; i++)
            {
                var candidate = snapshot[i];
                if (candidate == null || !candidate.gameObject.scene.IsValid()) continue;
                count++;
                paths.Add(BattleRuntimeBootstrap.DescribePath(candidate));
                if (Bootstrap == null) Bootstrap = candidate;
            }
            if (Bootstrap != null && !UseHudAutoPause)
            {
                Bootstrap.Clock.AutoPauseOwnWindow = false; Bootstrap.Clock.AutoPauseThreat = false;
                Bootstrap.NewDriver.ResumeTactically();
            }

            Assert.That(count, requireExactlyOne ? Is.EqualTo(1) : Is.GreaterThan(0),
                "场景中必须存在" + (requireExactlyOne ? "恰好一个" : "至少一个") + " BattleRuntimeBootstrap，实际 " + count
                + " [" + string.Join(",", paths) + "]");
        }

        protected string DescribeAdvancePaths() => "New ticks=" + Bootstrap.NewDriver.TicksAdvanced;

        protected int LegacyAdvancePathCalls() => LegacyTimelineAdvanceTimeCalls();

        protected int NewStepPathCalls() => Bootstrap.NewDriver.TicksAdvanced;

        protected static int BootstrapCountInScene() => BattleRuntimeBootstrap.LiveInstanceCount;

        protected void LogLedger(string context) => Debug.Log(context + "|" + DescribeAdvancePaths());


        // ---------------- 反射桥（不引用 Assembly-CSharp） ----------------

        protected static Type ResolveProductionType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            // 先按程序集限定名解析（Assembly-CSharp / Assembly-CSharp-Editor 是 Legacy 具体类型的
            // 唯一宿主），再退回候选程序集清单——不枚举 AppDomain（Unity 分析器 UAC0005）。
            var resolved = Type.GetType(fullName + ", Assembly-CSharp", throwOnError: false)
                ?? Type.GetType(fullName + ", Assembly-CSharp-Editor", throwOnError: false)
                ?? Type.GetType(fullName + ", ProjectHero.Legacy.Diagnostics", throwOnError: false)
                ?? Type.GetType(fullName + ", ProjectHero.UnityAuthoring", throwOnError: false);
            if (resolved != null) return resolved;

            foreach (var assembly in CandidateAssemblies())
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }
            return null;
        }

        private static IEnumerable<System.Reflection.Assembly> CandidateAssemblies()
        {
            yield return typeof(ProductionRuntimeTestBase).Assembly;
            yield return typeof(BattleRuntimeBootstrap).Assembly;
            yield return typeof(ProjectHero.Logic.Simulation.BattleSimulation).Assembly;
            yield return typeof(GameObject).Assembly;
        }

        protected static MonoBehaviour FindProductionComponent(string fullName)
        {
            var type = ResolveProductionType(fullName);
            if (type == null) return null;
            var found = UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include);
            return found != null && found.Length > 0 ? found[0] as MonoBehaviour : null;
        }

        protected (MonoBehaviour Hero, MonoBehaviour Enemy) FindLegacyUnits()
        {
            var type = ResolveProductionType("ProjectHero.Core.Entities.CombatUnit");
            Assert.That(type, Is.Not.Null,
                "对照证据：旧场景单位类型必须可解析（否则本用例退化为恒真）");

            var flag = type.GetField("IsPlayerControlled",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(flag, Is.Not.Null,
                "对照证据：CombatUnit 必须仍有 IsPlayerControlled 字段（仅用于测试侧定位对象）");

            var all = UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Exclude);
            Assert.That(all.Length, Is.EqualTo(2),
                "隐藏验证场景必须恰好两个活动 CombatUnit，实测=" + all.Length);

            MonoBehaviour hero = null;
            MonoBehaviour enemy = null;
            for (int i = 0; i < all.Length; i++)
            {
                var unit = all[i] as MonoBehaviour;
                if (unit == null) continue;
                if ((bool)flag.GetValue(unit)) hero = unit;
                else enemy = unit;
            }

            Assert.That(hero, Is.Not.Null, "必须能定位被玩家控制的旧单位（对照证据）");
            Assert.That(enemy, Is.Not.Null, "必须能定位另一个旧单位（对照证据）");
            return (hero, enemy);
        }

        protected static float ReadLegacyFloat(MonoBehaviour unit, string fieldName)
        {
            Assert.That(unit, Is.Not.Null);
            var field = unit.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, "CombatUnit 必须仍有公开字段 " + fieldName);
            Assert.That(field.FieldType, Is.EqualTo(typeof(float)),
                fieldName + " 必须是 float（任务 04 的实时读取契约按浮点原值 + 统一量化比较）");
            return (float)field.GetValue(unit);
        }

        protected static int ReadLegacyInt(MonoBehaviour unit, string fieldName)
        {
            Assert.That(unit, Is.Not.Null);
            var field = unit.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, "CombatUnit 必须仍有公开字段 " + fieldName);
            return (int)field.GetValue(unit);
        }

        protected static int LegacyTimelineAdvanceTimeCalls()
        {
            var timeline = FindProductionComponent("ProjectHero.Core.Timeline.BattleTimeline");
            if (timeline == null && UnityEngine.SceneManagement.SceneManager.GetActiveScene().path == MainScenePath)
            {
                var owners = UnityEngine.Object.FindObjectsByType<BattleRuntimeBootstrap>(FindObjectsInactive.Include);
                Assert.That(owners.Length, Is.EqualTo(1));
                Assert.That(!owners[0].BattleCreated || owners[0].NewDriver.SimulationCreated, Is.True,
                    "An active production battle must own its New simulation; a released battle has none.");
                Assert.That(FindProductionComponent("ProjectHero.Core.Entities.CombatUnit"), Is.Null,
                    "A missing counter is valid only after the production Legacy components are actually removed.");
                return 0;
            }
            Assert.That(timeline, Is.Not.Null,
                "对照证据：场景中必须存在旧 BattleTimeline（解析不到时不得静默返回 0——"
                + "返回 0 会让『New 模式旧推进为 0』变成 0 == 0 的恒真断言）");
            var value = ReadTimelineProperty(timeline, "TotalAdvanceTimeCalls");
            Assert.That(value, Is.Not.Null,
                "旧 BattleTimeline 必须具备 TotalAdvanceTimeCalls 属性（改名必须让本断言失败，而不是静默变 0）");
            return (int)value;
        }

        protected static long LegacyTimelineCurrentTick()
        {
            var timeline = FindProductionComponent("ProjectHero.Core.Timeline.BattleTimeline");
            Assert.That(timeline, Is.Not.Null,
                "对照证据：场景中必须存在旧 BattleTimeline（第二收尾轮 R4.4：解析失败必须显式失败）");
            var value = ReadTimelineProperty(timeline, "CurrentTick");
            Assert.That(value, Is.Not.Null,
                "旧 BattleTimeline 必须具备 CurrentTick 属性（改名必须让本断言失败，而不是静默返回 0）");
            return (long)value;
        }



        protected static bool LegacyTimelineSystemPaused()
        {
            var timeline = FindProductionComponent("ProjectHero.Core.Timeline.BattleTimeline");
            Assert.That(timeline, Is.Not.Null, "对照证据：场景中必须存在旧 BattleTimeline");
            var value = ReadTimelineProperty(timeline, "SystemPaused");
            Assert.That(value, Is.Not.Null,
                "旧 BattleTimeline 必须具备 SystemPaused 属性（第二收尾轮 R2 的回切语义证据）");
            return (bool)value;
        }

        protected static bool LegacyTimelineUserPaused()
            => LegacyTimelineUserPaused(out _);

        protected static bool LegacyTimelineUserPaused(out bool combinedPaused)
        {
            var timeline = FindProductionComponent("ProjectHero.Core.Timeline.BattleTimeline");
            Assert.That(timeline, Is.Not.Null, "对照证据：场景中必须存在旧 BattleTimeline");
            var userPaused = ReadTimelineProperty(timeline, "UserPaused");
            Assert.That(userPaused, Is.Not.Null,
                "旧 BattleTimeline 必须具备 UserPaused 属性（第三收尾轮 R4 的用户暂停语义证据；"
                + "改名必须让本断言失败，而不是静默返回 false）");
            var paused = ReadTimelineProperty(timeline, "Paused");
            Assert.That(paused, Is.Not.Null, "旧 BattleTimeline 必须具备 Paused 属性");
            combinedPaused = (bool)paused;
            return (bool)userPaused;
        }

        protected static void SetLegacyTimelineUserPaused(bool paused)
        {
            var timeline = FindProductionComponent("ProjectHero.Core.Timeline.BattleTimeline");
            Assert.That(timeline, Is.Not.Null, "对照证据：场景中必须存在旧 BattleTimeline");
            var method = timeline.GetType().GetMethod(
                "SetPaused", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null,
                "旧 BattleTimeline 必须具备公开 SetPaused(bool)（生产用户暂停入口；"
                + "改名必须让本断言失败，而不是静默什么都不做）");
            method.Invoke(timeline, new object[] { paused });
        }

        private static object ReadTimelineProperty(MonoBehaviour timeline, string propertyName)
        {
            var property = timeline.GetType().GetProperty(
                propertyName, BindingFlags.Instance | BindingFlags.Public);
            return property != null ? property.GetValue(timeline) : null;
        }

        protected static MonoBehaviour FindSimulationSourceFactory()
            => FindProductionComponent("ProjectHero.Core.Compatibility.Authoring.BattleSimulationSourceFactory");

        // ---------------- 调用矩阵断言 ----------------

        protected void AssertAdvanceMatrix(int expectedLegacyAdvanceTime, int expectedNewStep, string context)
        {
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(expectedLegacyAdvanceTime), context);
            Assert.That(Bootstrap.NewDriver.TicksAdvanced, Is.EqualTo(expectedNewStep), context);
        }

        protected static IEnumerator WaitFrames(int frames)
        {
            for (int i = 0; i < frames; i++) yield return null;
        }

        protected GameObject NewGameObject(string name, params Type[] components)
        {
            var go = new GameObject(name, components);
            TemporaryObjects.Add(go);
            return go;
        }

        protected T Track<T>(T component) where T : Component
        {
            if (component != null && !TemporaryObjects.Contains(component.gameObject))
                TemporaryObjects.Add(component.gameObject);
            return component;
        }
    }
}
