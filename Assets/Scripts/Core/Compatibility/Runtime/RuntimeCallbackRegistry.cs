using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// Unity 回调三类分类（任务 03B「必须产出」1）。
    ///
    /// <list type="bullet">
    /// <item><see cref="TopLevelClockAdvancer"/>：推进顶层战斗时钟/Timeline 的入口。
    /// 每个场景<strong>只能有一个</strong>，且必须是 <c>BattleRuntimeBootstrap.Update()</c>。</item>
    /// <item><see cref="LegacyDependentWriter"/>：写旧战斗状态、但依赖顶层时钟推进的从属写入者。
    /// 由 Bootstrap 按模式整体门控：Legacy/Shadow 启用，New 全部禁用（不变量 21）。</item>
    /// <item><see cref="ReadOnlyInputOrPresentation"/>：只读输入或表现回调。可以跨模式保留，
    /// 但<strong>不得写逻辑</strong>。</item>
    /// </list>
    /// </summary>
    public enum UnityCallbackClass
    {
        TopLevelClockAdvancer = 0,
        LegacyDependentWriter = 1,
        ReadOnlyInputOrPresentation = 2
    }

    /// <summary>
    /// Unity 回调的<strong>相位</strong>（任务 03B 第二收尾轮 R4.1：顺序保证的真实来源）。
    ///
    /// 只读检查点位于 <see cref="LateUpdatePhase"/>：Unity 保证同一帧内全部
    /// <c>Update()</c> 先于全部 <c>LateUpdate()</c> 执行，因此"检查点晚于全部 Update 相位的
    /// Legacy 写入者"由**相位边界**保证，而不是由某个执行顺序常量保证。
    /// 协程回调（<c>StartCoroutine</c> / <c>Invoke</c>）不绑定帧相位：它们由帧末或计时器驱动。
    /// </summary>
    public enum UnityCallbackPhase
    {
        /// <summary>帧内 <c>Update()</c> 相位。</summary>
        UpdatePhase = 0,

        /// <summary>帧内 <c>LateUpdate()</c> 相位（只读检查点所在相位）。</summary>
        LateUpdatePhase = 1,

        /// <summary>不绑定帧相位的协程 / 延迟调用（帧末或计时器驱动）。</summary>
        Coroutine = 2
    }

    /// <summary>
    /// 一条回调分类记录。完整类型名用字符串保存：分类表由契约程序集拥有，而
    /// Legacy 具体类型编译在预定义 <c>Assembly-CSharp</c> 中，自定义 asmdef 不允许
    /// 引用它（00 号规则 17）。
    /// </summary>
    public sealed class UnityCallbackClassification
    {
        public UnityCallbackClassification(
            string typeName,
            string memberName,
            UnityCallbackClass callbackClass,
            string sceneObjectPath,
            string timeSource,
            string callTarget,
            string writtenObjects,
            int scriptExecutionOrder,
            UnityCallbackPhase phase = UnityCallbackPhase.UpdatePhase,
            string modeGate = null)
        {
            TypeName = typeName ?? string.Empty;
            MemberName = memberName ?? string.Empty;
            CallbackClass = callbackClass;
            SceneObjectPath = sceneObjectPath ?? string.Empty;
            TimeSource = timeSource ?? string.Empty;
            CallTarget = callTarget ?? string.Empty;
            WrittenObjects = writtenObjects ?? string.Empty;
            ScriptExecutionOrder = scriptExecutionOrder;
            Phase = phase;
            ModeGate = modeGate ?? DefaultModeGate(callbackClass);
        }

        /// <summary>完整类型名（含命名空间）。</summary>
        public string TypeName { get; }

        /// <summary>成员名（<c>Update</c> / <c>LateUpdate</c> / <c>AdvanceTime</c> …）。</summary>
        public string MemberName { get; }

        public UnityCallbackClass CallbackClass { get; }

        /// <summary>主场景中的对象路径（例如 <c>CombatSampleScene/CombatDemo</c>）。</summary>
        public string SceneObjectPath { get; }

        public string TimeSource { get; }

        public string CallTarget { get; }

        public string WrittenObjects { get; }

        /// <summary>该脚本当前的显式执行顺序（未配置时为 0）。</summary>
        public int ScriptExecutionOrder { get; }

        /// <summary>
        /// 该回调所在帧相位（<see cref="UnityCallbackPhase"/>）。
        /// 只读检查点只保证晚于 <see cref="UnityCallbackPhase.UpdatePhase"/> 的写入者。
        /// </summary>
        public UnityCallbackPhase Phase { get; }

        /// <summary>
        /// 模式门控状态（该回调在各模式下是否启用/被调用）。
        /// 三类各自给出确定文本，便于交接与测试逐条核对。
        /// </summary>
        public string ModeGate { get; }

        public string Site => TypeName + "." + MemberName;

        private static string DefaultModeGate(UnityCallbackClass callbackClass)
        {
            switch (callbackClass)
            {
                case UnityCallbackClass.TopLevelClockAdvancer:
                    return "三模式都由唯一 Bootstrap 选择并调用；无模式差异（Legacy/Shadow/New 各自一支）";
                case UnityCallbackClass.LegacyDependentWriter:
                    return "Legacy/Shadow 启用；New 由 Bootstrap 整体禁用（enabled = false）";
                default:
                    return "三模式都保留（只读或只写表现），不得写逻辑";
            }
        }

        public override string ToString()
            => Site + " [" + CallbackClass + "/" + Phase + "] @ " + SceneObjectPath;
    }

    /// <summary>
    /// 已登记的 Legacy 从属写入者（跨 Bootstrap / 适配器 / 测试的共享模型）。
    ///
    /// <see cref="IsEnabled"/> 既是门控状态也是观测点：Bootstrap 在
    /// <c>ApplyLegacyWriterGroupGate</c> 中把模式决策写进这里，测试直接读它，
    /// 从而证明"New 全部禁用、Legacy/Shadow 启用基线集合"。
    /// </summary>
    public sealed class LegacyWriterRegistration
    {
        public LegacyWriterRegistration(string callbackSite, string sceneObjectPath, string timeSource, MonoBehaviour gateTarget)
        {
            CallbackSite = callbackSite ?? string.Empty;
            SceneObjectPath = sceneObjectPath ?? string.Empty;
            TimeSource = timeSource ?? string.Empty;
            GateTarget = gateTarget;
        }

        /// <summary>回调点（<c>Type.Member</c>）。</summary>
        public string CallbackSite { get; }

        public string SceneObjectPath { get; }

        public string TimeSource { get; }

        /// <summary>模式门控对象；为 null 表示"注册时未能解析到运行时实例"（诊断可见）。</summary>
        public MonoBehaviour GateTarget { get; }

        /// <summary>门控是否命中运行时实例。</summary>
        public bool HasGateTarget => GateTarget != null;

        /// <summary>当前是否启用（由 Bootstrap 按模式写入）。</summary>
        public bool IsEnabled { get; internal set; }

        /// <summary>基线上是否为"启用"状态（用于 New 模式恢复判断与诊断）。</summary>
        public bool BaselineEnabled { get; internal set; } = true;

        public override string ToString()
            => CallbackSite + " enabled=" + IsEnabled + " target=" + (GateTarget != null ? GateTarget.name : "<none>");
    }

    /// <summary>
    /// 回调分类注册表（静态事实表 + 运行时门控清单）。
    ///
    /// <strong>分类表是唯一权威</strong>：任务 01 §1.0–1.3 的三类分类在这里逐条落成数据。
    /// 未出现在本表中的 <c>Update()</c>/<c>LateUpdate()</c> 一律被视为
    /// <em>未分类逻辑写入者</em>，在 New 模式启动时<strong>阻止启动</strong>
    /// （任务包「必须产出」3；禁止事项第 2 条）。
    /// </summary>
    public static class RuntimeCallbackRegistry
    {
        public const string AssemblyCSharp = "Assembly-CSharp";
        public const string MainScenePrefix = "CombatSampleScene/";

        /// <summary>Legacy 从属写入者需要早于只读检查点执行的显式顺序。</summary>
        public const int LegacyWriterExecutionOrder = -500;

        /// <summary>Bootstrap（唯一顶层时钟）的显式执行顺序。</summary>
        public const int BootstrapExecutionOrder = -1000;

        /// <summary>
        /// 只读 Shadow 检查点的<strong>声明</strong>执行顺序位（晚于全部 Legacy 写入者的 -500）。
        ///
        /// 第二收尾轮 R4.1 的更正：本常量<strong>没有</strong>施加到任何组件上
        /// （<c>BattleRuntimeBootstrap</c> 整体是 <c>[DefaultExecutionOrder(-1000)]</c>，
        /// 其 <c>.meta</c> 也没有独立的 <c>MonoImporter.executionOrder</c>）。
        /// 因此它<strong>不是</strong>顺序保证，而只是"检查点被声明在哪个顺序位"的元数据；
        /// 真实的顺序保证有两条，都可被证伪：
        /// <list type="number">
        /// <item><b>相位边界</b>：检查点在 <c>LateUpdate</c>，全部 Legacy 写入者在 <c>Update</c>；
        /// Unity 保证同一帧内全部 <c>Update</c> 先于全部 <c>LateUpdate</c>。
        /// 违反时 <see cref="UnityCallbackPhase"/> 会把它标成 <c>LateUpdate</c>/<c>Coroutine</c> 相位，
        /// 于是 <c>WritersExecutingInLateUpdatePhase()</c> 报警（旧实现不可能报警）。</item>
        /// <item><b>真实执行顺序读取</b>：<c>BattleRuntimeBootstrap.WriterOrderMismatches()</c>
        /// 读 <c>MonoImporter.GetExecutionOrder</c> 并与分类表逐条比对。</item>
        /// </list>
        /// </summary>
        public const int CheckpointExecutionOrder = 10000;

        private static readonly Dictionary<string, UnityCallbackClassification> Classifications =
            new Dictionary<string, UnityCallbackClassification>(StringComparer.Ordinal);

        private static readonly Dictionary<string, Type> ResolvedTypes = new Dictionary<string, Type>(StringComparer.Ordinal);

        static RuntimeCallbackRegistry()
        {
            // ---- 顶层时钟推进者（唯一） ----
            Add("ProjectHero.Demos.CombatDemo", "Update", UnityCallbackClass.TopLevelClockAdvancer,
                MainScenePrefix + "CombatDemo", "Time.deltaTime",
                "改造前：BattleTimeline.AdvanceTime；改造后：无自主推进，只由 Bootstrap 调用 AdvanceFrame",
                "旧 BattleTimeline 计划表与全部旧运行组", BootstrapExecutionOrder);

            Add("ProjectHero.Core.Compatibility.Runtime.BattleRuntimeBootstrap", "Update",
                UnityCallbackClass.TopLevelClockAdvancer, MainScenePrefix + "CombatDemo",
                "Time.deltaTime（Legacy 段）/ Time.unscaledDeltaTime（New 段）",
                "按固定模式调用唯一帧适配器",
                "无（Bootstrap 自身不直接写场景）", BootstrapExecutionOrder);

            Add("ProjectHero.Core.Compatibility.Runtime.BattleRuntimeBootstrap", "LateUpdate",
                UnityCallbackClass.ReadOnlyInputOrPresentation, MainScenePrefix + "CombatDemo",
                "无（只读检查点，不采样帧时间）",
                "Shadow 比较（只读快照）",
                "无", CheckpointExecutionOrder);

            // ---- Legacy 从属写入者（New 模式必须全部禁用） ----
            Add("ProjectHero.Core.Entities.CombatUnit", "Update", UnityCallbackClass.LegacyDependentWriter,
                MainScenePrefix + "Player|" + MainScenePrefix + "Enemy", "Time.deltaTime",
                "网格/HUD 注册重试；肾上腺素衰减；体力恢复",
                "CombatUnit.CurrentAdrenaline / CurrentStamina", LegacyWriterExecutionOrder);

            Add("ProjectHero.Core.Gameplay.BattleManager", "Update", UnityCallbackClass.LegacyDependentWriter,
                MainScenePrefix + "Manager", "每帧轮询",
                "CheckWinCondition（FindObjectsByType<CombatUnit>）",
                "BattleManager._battleEnded / Time.timeScale", LegacyWriterExecutionOrder);

            Add("ProjectHero.Core.Gameplay.EnemyAIController", "Update", UnityCallbackClass.LegacyDependentWriter,
                MainScenePrefix + "Manager", "Time.unscaledTime（10Hz 节流）",
                "MakeDecision → ActionScheduler.ScheduleAttack/ScheduleMoveTo/ScheduleRecover",
                "BattleTimeline._events / AI 自己的节流时刻", LegacyWriterExecutionOrder);

            Add("ProjectHero.UI.Timeline.TimelineEditorUI", "Update", UnityCallbackClass.LegacyDependentWriter,
                MainScenePrefix + "Canvas/TimelineUI（运行时由 UIManager.EnsureTimelineUI 创建）",
                "每帧（含鼠标输入轮询）",
                "UpdatePlacementGhost → FinalizePlacement / CancelPlacement；RequestDelete/RequestReposition",
                "BattleTimeline.CancelGroup / placement.Schedule", LegacyWriterExecutionOrder);

            Add("ProjectHero.Core.Gameplay.TacticsController", "OnDestroy", UnityCallbackClass.LegacyDependentWriter,
                MainScenePrefix + "Manager", "输入事件回调（无自主 Update）",
                "HandleUnitClick / GroundClick / Cancel → ActionScheduler.Schedule*；IssueMoveCommand",
                "BattleTimeline 计划表 / CombatUnit.SetGridPosition / Time.timeScale", LegacyWriterExecutionOrder);

            Add("ProjectHero.Demos.CombatDemo", "Start", UnityCallbackClass.LegacyDependentWriter,
                MainScenePrefix + "CombatDemo", "场景启动（初始化写入，非时钟推进）",
                "兜底创建 GridManager/InputManager/TacticsController；IsPlayerControlled=true",
                "场景对象 / CombatUnit.IsPlayerControlled", LegacyWriterExecutionOrder);

            // ---- 只读输入/表现回调（跨模式保留，不得写逻辑） ----
            AddReadOnly("ProjectHero.Core.Input.InputManager", "Update", MainScenePrefix + "Manager",
                "射线 hover，仅发事件");
            AddReadOnly("ProjectHero.UI.UIManager", "Update", MainScenePrefix + "Manager",
                "暂停态轮询（只读）");
            AddReadOnly("ProjectHero.Visuals.UnitMovement", "LateUpdate",
                MainScenePrefix + "Player|" + MainScenePrefix + "Enemy", "视觉插值（VisualTime），只写 Transform");
            AddReadOnly("ProjectHero.Visuals.UnitBounce", "Update",
                MainScenePrefix + "Player|" + MainScenePrefix + "Enemy", "缩放表现，只写 localScale");
            AddReadOnly("ProjectHero.Visuals.GameFeelManager", "Update", MainScenePrefix + "Manager",
                "文字堆叠偏移衰减");
            AddReadOnly("ProjectHero.UI.UnitStatusHUD", "LateUpdate", "HUD 实例（HUDManager 运行时实例化）",
                "血条/体力/肾上腺素条/动作环（只读 CombatUnit + Timeline.CurrentTick）");
            AddReadOnly("ProjectHero.Visuals.UnitVolumeRenderer", "LateUpdate", MainScenePrefix + "Manager",
                "占位 mesh 重建（只读）");
            AddReadOnly("ProjectHero.Visuals.UnitVolumeVisuals", "Update", MainScenePrefix + "Manager",
                "占位可视化同步（只读）");
            AddReadOnly("ProjectHero.Visuals.GridCursor", "Update", MainScenePrefix + "Cursor",
                "材质色同步");
            AddReadOnly("ProjectHero.Visuals.NextActionPreview.NextActionPreviewSystem", "Update",
                MainScenePrefix + "GridManager/NextActionPreview（运行时创建）",
                "预览重建（只读 Timeline 快照，不写逻辑）");
            AddReadOnly("ProjectHero.Visuals.NextActionPreview.NextActionPreviewRenderer", "Update",
                MainScenePrefix + "GridManager/NextActionPreview（运行时创建）", "材质同步");

            // ---- 协程 / 延迟回调站点（第二收尾轮 R4.2：D5 的漏登记项） ----
            // 这些站点不声明 Update/LateUpdate，因此既不会被 IsUnclassifiedLogicWriter 扫到，
            // 也不会出现在"回调清单"里；但它们会改**全局** Time.timeScale（进而改变旧逻辑
            // Tick 率），所以必须显式分类并写清模式门控。

            Add("ProjectHero.Core.Timeline.BattleTimeline", "TriggerSlowMotion→DoSlowMotion",
                UnityCallbackClass.LegacyDependentWriter, MainScenePrefix + "CombatDemo",
                "WaitForSecondsRealtime（真实时间，不受 timeScale 影响）",
                "StartCoroutine(DoSlowMotion)：Start 写 Time.timeScale = scale，结束后写回 1.0",
                "Time.timeScale（全局视觉时间缩放）", LegacyWriterExecutionOrder,
                UnityCallbackPhase.Coroutine);

            Add("ProjectHero.Visuals.GameFeelManager", "HitStop",
                UnityCallbackClass.LegacyDependentWriter, MainScenePrefix + "Manager",
                "WaitForSecondsRealtime（真实时间）",
                "StartCoroutine(DoHitStop)：写 Time.timeScale = 0.05，等待后写回 1.0",
                "Time.timeScale（全局顿帧，改变旧逻辑 Tick 率）", LegacyWriterExecutionOrder,
                UnityCallbackPhase.Coroutine);

            Add("ProjectHero.Visuals.GameFeelManager", "ScreenShake",
                UnityCallbackClass.LegacyDependentWriter, MainScenePrefix + "Manager",
                "协程逐帧（Time.unscaledDeltaTime）",
                "StartCoroutine(DoScreenShake)：只写 Camera.main 的 Transform 位置",
                "相机 Transform（表现，不写逻辑）", LegacyWriterExecutionOrder,
                UnityCallbackPhase.Coroutine);
        }

        private static void Add(
            string typeName, string memberName, UnityCallbackClass callbackClass, string sceneObjectPath,
            string timeSource, string callTarget, string writtenObjects, int scriptExecutionOrder,
            UnityCallbackPhase phase = UnityCallbackPhase.UpdatePhase, string modeGate = null)
        {
            var classification = new UnityCallbackClassification(
                typeName, memberName, callbackClass, sceneObjectPath, timeSource, callTarget, writtenObjects,
                scriptExecutionOrder, phase, modeGate);
            Classifications[classification.Site] = classification;
        }

        private static void AddReadOnly(string typeName, string memberName, string sceneObjectPath, string callTarget)
        {
            Add(typeName, memberName, UnityCallbackClass.ReadOnlyInputOrPresentation, sceneObjectPath,
                "无逻辑时间语义", callTarget, "无（只读或只写表现）", 0, ResolvePhase(memberName));
        }

        /// <summary>按成员名推断帧相位（只用于只读表现回调；显式分类一律显式传相位）。</summary>
        private static UnityCallbackPhase ResolvePhase(string memberName)
            => string.Equals(memberName, "LateUpdate", StringComparison.Ordinal)
                ? UnityCallbackPhase.LateUpdatePhase
                : UnityCallbackPhase.UpdatePhase;

        /// <summary>整张分类表（稳定顺序：按 Site 的 Ordinal 排序为输出顺序，存储顺序不承诺）。</summary>
        public static IReadOnlyCollection<UnityCallbackClassification> All
        {
            get
            {
                var list = new List<UnityCallbackClassification>(Classifications.Values);
                list.Sort((a, b) => string.CompareOrdinal(a.Site, b.Site));
                return list;
            }
        }

        public static bool TryGet(string typeName, string memberName, out UnityCallbackClassification classification)
            => Classifications.TryGetValue((typeName ?? string.Empty) + "." + (memberName ?? string.Empty), out classification);

        public static UnityCallbackClass? ClassOf(string typeName, string memberName)
        {
            UnityCallbackClassification classification;
            return TryGet(typeName, memberName, out classification)
                ? classification.CallbackClass
                : (UnityCallbackClass?)null;
        }

        /// <summary>解析 Legacy 具体类型（编译在 <c>Assembly-CSharp</c> 中）。</summary>
        public static Type ResolveType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            Type cached;
            if (ResolvedTypes.TryGetValue(fullName, out cached)) return cached;

            Type resolved = Type.GetType(fullName + ", " + AssemblyCSharp, throwOnError: false);
            if (resolved == null)
            {
                // 只在显式列出的候选程序集中查找（不枚举 AppDomain：Unity 分析器 UAC0005
                // 指出 AppDomain.GetAssemblies() 可能返回已卸载程序集）。
                resolved = FindInCandidateAssemblies(fullName);
            }

            if (resolved != null) ResolvedTypes[fullName] = resolved;
            return resolved;
        }

        private static Type FindInCandidateAssemblies(string fullName)
        {
            var self = typeof(RuntimeCallbackRegistry).Assembly;
            var candidates = new[]
            {
                self,
                typeof(Logic.Simulation.BattleSimulation).Assembly,
                typeof(MonoBehaviour).Assembly,
                typeof(GameObject).Assembly,
                typeof(System.Collections.Generic.List<int>).Assembly
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                var assembly = candidates[i];
                if (assembly == null) continue;
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }

            // 最后再枚举一次已加载程序集（编辑器/播放器都覆盖；AppDomain.GetAssemblies 是
            // 唯一不依赖 Unity 内部 API 的通用枚举入口，Unity 分析器 UAC0005 只是提示风险）。
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
            {
                var type = loaded[i].GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }

            return null;
        }

        /// <summary>
        /// 该 MonoBehaviour 类型是否是"未分类的自主 Update/LateUpdate 写入者"。
        /// 只读表现类型与被显式分类的类型都不算。
        /// </summary>
        public static bool IsUnclassifiedLogicWriter(Type type)
        {
            if (type == null) return false;
            if (!typeof(MonoBehaviour).IsAssignableFrom(type)) return false;
            if (typeof(BattleRuntimeBootstrap).IsAssignableFrom(type)) return false;
            if (IsFrameworkWriter(type)) return false;

            string fullName = type.FullName ?? type.Name;
            foreach (var member in new[] { "Update", "LateUpdate" })
            {
                bool declares = type.GetMethod(
                    member,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null) != null;
                if (!declares) continue;

                // 刻意不用 ClassOf(...)?.HasValue：Nullable<T> 在装箱路径上会把
                // "有值" 误判成 "无值"，导致已分类的回调被当成未分类写入者。
                UnityCallbackClassification classification;
                if (TryGet(fullName, member, out classification)) continue;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 引擎 / 框架 / 第三方命名空间的自主 <c>Update()</c> 组件
        /// （例如 <c>UnityEngine.EventSystems.EventSystem</c>、<c>UnityEngine.Rendering.Volume</c>）。
        ///
        /// 它们不是"项目逻辑写入者"，因此不参与 New 模式的未分类门禁。
        /// 项目自己的类型（<c>ProjectHero.*</c>）不在此列。
        /// </summary>
        public static bool IsFrameworkWriter(Type type)
        {
            if (type == null) return false;
            string ns = type.Namespace ?? string.Empty;
            if (ns.Length == 0) return false;
            if (ns.StartsWith("ProjectHero", StringComparison.Ordinal)) return false;
            return ns.StartsWith("UnityEngine", StringComparison.Ordinal)
                || ns.StartsWith("UnityEditor", StringComparison.Ordinal)
                || ns.StartsWith("Unity.", StringComparison.Ordinal)
                || ns.StartsWith("TMPro", StringComparison.Ordinal)
                || ns.StartsWith("Cinemachine", StringComparison.Ordinal);
        }

        /// <summary>场景审计用的简明分类文本。</summary>
        public static string DescribeClasses()
        {
            var builder = new StringBuilder();
            foreach (var classification in All)
            {
                builder.AppendLine(classification.ToString());
            }
            return builder.ToString();
        }
    }
}
