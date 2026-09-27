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
    /// <summary>
    /// 任务 03B PlayMode 夹具。
    ///
    /// 关键设计（见交接记录「asmdef 边界」）：
    /// <list type="bullet">
    /// <item>测试程序集<strong>只引用</strong> <c>ProjectHero.Compatibility.Runtime</c> 与
    /// <c>ProjectHero.Logic</c>。它<strong>不</strong>引用 <c>Assembly-CSharp</c>，
    /// 也不引用 <c>ProjectHero.Authoring</c>——因此不能直接用 Legacy 具体类型。</item>
    /// <item>真实初始化链通过"加载真实场景"（隐藏验证场景与主战斗场景）来参与测试；
    /// 具体组件类型的解析走反射（<see cref="ResolveProductionType"/>），
    /// 不使用任何测试夹具冒充正式初始化链。</item>
    /// <item>全部通过/失败判据都来自<b>可观察调用计数</b>（<see cref="RuntimeCallLedger"/>、
    /// <c>ShadowWriteCounters</c>、旧 <c>BattleTimeline.TotalAdvanceTimeCalls</c>），
    /// 不从日志文本推断。</item>
    /// </list>
    /// </summary>
    public abstract class RuntimeOwnershipTestBase
    {
        public const string MainSceneName = "CombatSampleScene";
        public const string HiddenValidationSceneName = "HiddenRuntimeValidation";
        public const string HiddenValidationScenePath = "Assets/Scenes/HiddenRuntimeValidation.unity";
        public const string HiddenSceneAssetPath = "Assets/Scenes/HiddenRuntimeValidation.unity";
        public const string MainScenePath = "Assets/Scenes/CombatSampleScene.unity";

        protected readonly List<GameObject> TemporaryObjects = new List<GameObject>();
        protected readonly List<UnityEngine.Object> TemporaryAssets = new List<UnityEngine.Object>();

        protected BattleRuntimeBootstrap Bootstrap { get; private set; }

        /// <summary>当前测试使用的是真实场景（而不是自建夹具场景）。</summary>
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
            BattleRuntimeBootstrap.ClearUnclassifiedLogicWriterTypes();
            LegacyWriterRegistry.ResetForTests();

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

        /// <summary>
        /// 加载隐藏验证场景（真实初始化链，三模式都由测试显式驱动）。
        ///
        /// <strong>确定性要求</strong>：加载完成后必须核对"活动场景就是隐藏验证场景"，
        /// 否则立刻失败——旧实现会在加载失败时静默退化为"在当前场景里实例化资产根对象"，
        /// 使断言看到的是混合场景（例如把主场景的表现闭包、或运行时兜底创建的对象
        /// 误报成隐藏场景内容）。
        ///
        /// 加载方式：<c>EditorSceneManager.LoadSceneInPlayMode(path)</c> —— 它是 Editor
        /// <b>专为播放模式按资源路径加载场景</b>提供的入口，因此隐藏验证场景
        /// <b>完全不需要</b>进入 Build Settings / Build Profile：
        /// <list type="bullet">
        /// <item><c>SceneManager.LoadSceneAsync(name)</c> 只加载"已登记进活动 Build Profile
        /// 或共享场景列表"的场景（Unity 6.6 的 Build Profiles 语义），本场景刻意不在其中；</item>
        /// <item><c>EditorSceneManager.OpenScene</c> 在播放模式被 Unity 拒绝。</item>
        /// </list>
        /// </summary>
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

        /// <summary>加载主战斗场景（默认 Legacy，自动启动）。主场景在 Build Settings 中且启用。</summary>
        protected IEnumerator LoadMainScene()
        {
            yield return SceneManager.LoadSceneAsync(MainSceneName, LoadSceneMode.Single);
            yield return null;
            ResolveBootstrap();
            UsingProductionScene = true;
        }

#if UNITY_EDITOR
        /// <summary>
        /// 隐藏验证场景<b>资产文本</b>里出现的全部脚本 GUID（交付态的结构事实）。
        ///
        /// 为什么读资产文本而不是枚举运行时对象：隐藏验证场景在运行时会由旧兜底链
        /// （<c>CombatDemo.Start</c>）创建 <c>TacticsController</c> / <c>GridVisuals</c> /
        /// <c>UnitVolumeRenderer</c> 等组件——那是**运行时旧行为**，不是场景装配内容。
        /// "隐藏场景是最小真实链、不带 UI/交互闭包"这条规格针对的是<b>序列化内容</b>。
        /// </summary>
        protected static string HiddenSceneAssetText()
        {
            Assert.That(System.IO.File.Exists(HiddenSceneAssetPath), Is.True,
                "隐藏验证场景资产必须存在于磁盘：" + HiddenSceneAssetPath);
            return System.IO.File.ReadAllText(HiddenSceneAssetPath);
        }

        /// <summary>某个生产脚本的 GUID（用于在场景资产文本里做结构核对）。</summary>
        protected static string ScriptGuid(string scriptPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(scriptPath);
            Assert.That(guid, Is.Not.Empty, "脚本资产必须存在：" + scriptPath);
            return guid;
        }
#endif
        /// <summary>
        /// 从当前已加载场景解析唯一 Bootstrap（含 enabled = false 的组件）。
        /// 使用静态枚举入口，<strong>不创建探测组件</strong>——探测组件本身会触发
        /// 重复 Bootstrap 拒绝并打错误日志。
        /// </summary>
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

            Assert.That(count, requireExactlyOne ? Is.EqualTo(1) : Is.GreaterThan(0),
                "场景中必须存在" + (requireExactlyOne ? "恰好一个" : "至少一个") + " BattleRuntimeBootstrap，实际 " + count
                + " [" + string.Join(",", paths) + "]");
        }

        /// <summary>
        /// 推进路径诊断：列出账本记录的每条顶层推进路径计数、旧时间线的权威计数与
        /// 每个已登记写入者自己的推进计数。
        ///
        /// 用途：当"没有任何显式驱动时旧时间线不得推进"失败时，
        /// 失败信息必须能指出**是哪条非目标推进路径被调用**（而不是只说"期望 0 实际 N"）。
        /// </summary>
        protected string DescribeAdvancePaths()
        {
            var builder = new System.Text.StringBuilder();
            builder.Append("ledger[")
                .Append(Bootstrap != null ? Bootstrap.Ledger.Describe() : "<no bootstrap>")
                .Append("] timelineTotal=").Append(LegacyTimelineAdvanceTimeCalls())
                .Append(" timelineTick=").Append(LegacyTimelineCurrentTick());

            if (Bootstrap == null) return builder.ToString();

            builder.Append(" adapters[legacy=")
                .Append(Bootstrap.Adapters.Legacy != null
                    ? Bootstrap.Adapters.Legacy.AdvanceCallCount.ToString()
                    : "<null>")
                .Append(" shadow=")
                .Append(Bootstrap.Shadow != null ? Bootstrap.Shadow.AdvanceCallCount.ToString() : "<null>")
                .Append(" newDriver=").Append(Bootstrap.NewDriver.AdvanceCallCount)
                .Append(']');

            builder.Append(" writerAdvance[");
            for (int i = 0; i < Bootstrap.LegacyWriters.Count; i++)
            {
                string site = Bootstrap.LegacyWriters[i].CallbackSite;
                builder.Append(site).Append('=')
                    .Append(Bootstrap.Ledger.LegacyWriterAdvanceCalls(site)).Append(';');
            }
            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>旧顶层推进路径（<c>LegacyAdvanceTime</c>）被调用的次数（账本口径）。</summary>
        protected int LegacyAdvancePathCalls()
            => Bootstrap != null
                ? Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime)
                : 0;

        /// <summary>新模拟 Step 路径被调用的次数（账本口径）。</summary>
        protected int NewStepPathCalls()
            => Bootstrap != null
                ? Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep)
                : 0;

        /// <summary>
        /// 场景内的 Bootstrap 数量（含 enabled = false 的重复组件、含 inactive 对象上的实例）。
        /// 直接读静态计数：Unity 的 FindObjectsByType 默认看不到被禁用的组件，而重复
        /// Bootstrap 会被 Awake 立刻禁用，正是最需要被数到的情形。
        /// </summary>
        protected static int BootstrapCountInScene() => BattleRuntimeBootstrap.LiveInstanceCount;

        /// <summary>诊断输出（只在断言失败时需要，正常路径不改变任何状态）。</summary>
        protected void LogLedger(string context)
        {
            UnityEngine.Debug.Log("[03B-diag] " + context + " :: "
                + (Bootstrap != null ? Bootstrap.DescribeOwnership() : "<no bootstrap>"));
        }

        /// <summary>取本次比较报告的可读摘要（诊断用）。</summary>
        protected static string DescribeDifferenceList(System.Collections.Generic.IReadOnlyList<ShadowFieldDifference> differences)
        {
            if (differences == null) return "<null>";
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < differences.Count && i < 12; i++)
            {
                builder.Append(differences[i].Kind).Append('|')
                    .Append(differences[i].FieldPath).Append('|')
                    .Append(differences[i].LegacyValue).Append("->")
                    .Append(differences[i].ShadowValue).Append(" ; ");
            }
            return builder.ToString();
        }
        // ---------------- 反射桥（不引用 Assembly-CSharp） ----------------

        /// <summary>
        /// 按完整类型名解析<b>正式</b>组件类型（来自 <c>Assembly-CSharp</c>）。
        /// 这是"使用真实初始化链"的手段，不是夹具：类型与实例都来自项目代码。
        /// </summary>
        protected static Type ResolveProductionType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            // 先按程序集限定名解析（Assembly-CSharp / Assembly-CSharp-Editor 是 Legacy 具体类型的
            // 唯一宿主），再退回候选程序集清单——不枚举 AppDomain（Unity 分析器 UAC0005）。
            var resolved = Type.GetType(fullName + ", Assembly-CSharp", throwOnError: false)
                ?? Type.GetType(fullName + ", Assembly-CSharp-Editor", throwOnError: false);
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
            yield return typeof(RuntimeOwnershipTestBase).Assembly;
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

        /// <summary>
        /// 按 <c>IsPlayerControlled</c> 分辨隐藏场景里的两个 <c>CombatUnit</c>
        /// （任务 04 的 Shadow 实时读取用例需要分别操作"英雄"与"敌人"的活动字段）。
        ///
        /// 只用于<strong>测试侧定位对象</strong>：逻辑层从不读这个布尔值决定阵营或胜负
        /// （见 00 号规则 30）。返回 (hero, enemy)，任一缺失即断言失败。
        /// </summary>
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

        /// <summary>读取旧单位上一个公开浮点字段（用于任务 04 的负控制）。</summary>
        protected static float ReadLegacyFloat(MonoBehaviour unit, string fieldName)
        {
            Assert.That(unit, Is.Not.Null);
            var field = unit.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, "CombatUnit 必须仍有公开字段 " + fieldName);
            Assert.That(field.FieldType, Is.EqualTo(typeof(float)),
                fieldName + " 必须是 float（任务 04 的实时读取契约按浮点原值 + 统一量化比较）");
            return (float)field.GetValue(unit);
        }

        /// <summary>按名字读取旧单位一个公开整数字段（用于任务 04 的负控制）。</summary>
        protected static int ReadLegacyInt(MonoBehaviour unit, string fieldName)
        {
            Assert.That(unit, Is.Not.Null);
            var field = unit.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, "CombatUnit 必须仍有公开字段 " + fieldName);
            return (int)field.GetValue(unit);
        }

        /// <summary>旧 <c>BattleTimeline.TotalAdvanceTimeCalls</c>（权威旧推进计数）。</summary>
        protected static int LegacyTimelineAdvanceTimeCalls()
        {
            var timeline = FindProductionComponent("ProjectHero.Core.Timeline.BattleTimeline");
            Assert.That(timeline, Is.Not.Null,
                "对照证据：场景中必须存在旧 BattleTimeline（解析不到时不得静默返回 0——"
                + "返回 0 会让『New 模式旧推进为 0』变成 0 == 0 的恒真断言）");
            var value = ReadTimelineProperty(timeline, "TotalAdvanceTimeCalls");
            Assert.That(value, Is.Not.Null,
                "旧 BattleTimeline 必须具备 TotalAdvanceTimeCalls 属性（改名必须让本断言失败，而不是静默变 0）");
            return (int)value;
        }

        /// <summary>旧 <c>BattleTimeline.CurrentTick</c>。</summary>
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

        /// <summary>观测到的 Legacy 侧只读检查点的逻辑 Tick 列表（诊断用）。</summary>
        protected string DescribeObservationTicks()
        {
            if (Bootstrap == null) return "<no bootstrap>";
            var builder = new System.Text.StringBuilder();
            var observations = Bootstrap.LegacyObservations;
            for (int i = 0; i < observations.Count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(observations[i].Tick);
            }
            return builder.ToString();
        }

        /// <summary>旧时间线的系统暂停状态（第二收尾轮 R2 的重开局断言用）。</summary>
        protected static bool LegacyTimelineSystemPaused()
        {
            var timeline = FindProductionComponent("ProjectHero.Core.Timeline.BattleTimeline");
            Assert.That(timeline, Is.Not.Null, "对照证据：场景中必须存在旧 BattleTimeline");
            var value = ReadTimelineProperty(timeline, "SystemPaused");
            Assert.That(value, Is.Not.Null,
                "旧 BattleTimeline 必须具备 SystemPaused 属性（第二收尾轮 R2 的回切语义证据）");
            return (bool)value;
        }

        /// <summary>
        /// 旧时间线的<strong>用户暂停</strong>状态（第三收尾轮 R4）。只读可观察入口
        /// <c>BattleTimeline.UserPaused</c>。
        ///
        /// 与 <see cref="LegacyTimelineSystemPaused"/> 分开是刻意的：恢复语义要求
        /// 系统暂停被重开清除、用户暂停不被清除；只有一个合并的 <c>Paused</c>
        /// 无法区分这两条，也就无法断言它们各自成立。
        /// </summary>
        protected static bool LegacyTimelineUserPaused()
            => LegacyTimelineUserPaused(out _);

        /// <summary>同时给出旧时间线的 <c>Paused</c>（合并语义），供"顶层/合并暂停语义"断言使用。</summary>
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

        /// <summary>
        /// 施加<strong>用户暂停</strong>（镜像生产入口 <c>UIManager.TogglePause</c> 对
        /// <c>BattleTimeline.SetPaused</c> 的调用；第三收尾轮 R4 需要"先施加用户暂停再重开"）。
        ///
        /// 只经公开生产的 <c>SetPaused</c> 写入，测试不新增任何生产写入面。
        /// </summary>
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

        /// <summary>
        /// 反射读取旧时间线属性。第二收尾轮 R4.4：**解析失败不再 fail-open 返回 0**，
        /// 而是返回 null 让调用点显式断言失败——"解析不到就当作 0"会掩盖真实缺陷。
        /// </summary>
        private static object ReadTimelineProperty(MonoBehaviour timeline, string propertyName)
        {
            var property = timeline.GetType().GetProperty(
                propertyName, BindingFlags.Instance | BindingFlags.Public);
            return property != null ? property.GetValue(timeline) : null;
        }

        /// <summary>真实初始化来源组件（<c>BattleSimulationSourceFactory</c>）。</summary>
        protected static MonoBehaviour FindSimulationSourceFactory()
            => FindProductionComponent("ProjectHero.Core.Compatibility.Authoring.BattleSimulationSourceFactory");

        // ---------------- 调用矩阵断言 ----------------

        protected void AssertAdvanceMatrix(
            int expectedLegacyAdvanceTime, int expectedNewStep, string context)
        {
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime),
                Is.EqualTo(expectedLegacyAdvanceTime),
                context + "：Ledger 记录的 Legacy 顶层推进次数不符");
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep),
                Is.EqualTo(expectedNewStep),
                context + "：Ledger 记录的新模拟 Step 次数不符");
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
