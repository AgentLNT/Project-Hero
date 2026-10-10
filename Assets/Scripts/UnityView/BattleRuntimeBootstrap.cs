using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ProjectHero.Logic;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Replay;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// 战斗运行所有权壳（任务 03B「必须产出」3；不变量 21）。
    ///
    /// <strong>本组件是场景中唯一通过 Unity <c>Update()</c> 选择并推进顶层战斗时钟的组件。</strong>
    /// Legacy 适配器、Shadow runner 与最小 New Driver 都只能被它调用，不得形成第二个自主时钟入口。
    ///
    /// 职责边界：
    /// <list type="bullet">
    /// <item>启动时校验"场景中恰有一个 Bootstrap"、"没有已登记的自主推进适配器仍启用"、
    /// 以及"存在未分类逻辑写入者时 New 拒绝启动"（快速失败，附对象路径）。</item>
    /// <item>按<strong>创建本场战斗之前固定的模式</strong>调用一个明确的帧适配器；
    /// 绝不按容器枚举顺序或反射发现选择权威路径。</item>
    /// <item>显式控制 Legacy 从属写入组：Legacy/Shadow 按基线启用，New 全部禁用。</item>
    /// <item>负责暂停与销毁边界；每场战斗只能初始化、停止和释放一次。</item>
    /// <item>Shadow 在 <c>Update()</c> 中推进两边，但只在只读 <c>LateUpdate()</c> 检查点采样比较；
    /// 该检查点不推进时钟、不写两边状态，并以显式脚本执行顺序晚于全部已登记 Legacy 写入者。</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(RuntimeCallbackRegistry.BootstrapExecutionOrder)]
    public sealed class BattleRuntimeBootstrap : MonoBehaviour, IShadowLogicCheckpointSink
    {
        public const string BOOTSTRAP_DUPLICATE = "BATTLE_RUNTIME_BOOTSTRAP_DUPLICATE";
        public const string BOOTSTRAP_ADAPTER_OWNS_AUTONOMOUS_UPDATE =
            "BATTLE_RUNTIME_BOOTSTRAP_ADAPTER_OWNS_AUTONOMOUS_UPDATE";
        public const string BOOTSTRAP_UNCLASSIFIED_LEGACY_LOGIC_WRITER =
            "BATTLE_RUNTIME_BOOTSTRAP_UNCLASSIFIED_LEGACY_LOGIC_WRITER";
        public const string BOOTSTRAP_MODE_CHANGE_REJECTED = "BATTLE_RUNTIME_MODE_CHANGE_REJECTED";
        public const string BOOTSTRAP_DOUBLE_START = "BATTLE_RUNTIME_BOOTSTRAP_DOUBLE_START";
        public const string BOOTSTRAP_ADAPTER_SLOT_MISSING = "BATTLE_RUNTIME_BOOTSTRAP_ADAPTER_SLOT_MISSING";
        public const string BOOTSTRAP_ADAPTER_SLOT_INVALID = "BATTLE_RUNTIME_BOOTSTRAP_ADAPTER_SLOT_INVALID";
        public const string BOOTSTRAP_CHECKPOINT_ORDER_INVALID = "BATTLE_RUNTIME_CHECKPOINT_ORDER_INVALID";
        public const string BOOTSTRAP_STOPPED = "BATTLE_RUNTIME_BOOTSTRAP_STOPPED";

        /// <summary>本场景中所有存活的 Bootstrap（重复检测的唯一依据；含被禁用的重复实例）。</summary>
        private static readonly List<BattleRuntimeBootstrap> LiveInstances = new List<BattleRuntimeBootstrap>();

        /// <summary>确定性校验点发现的非所有者实例（对象路径，诊断与验收证据）。</summary>
        private static readonly List<string> DuplicateInstanceAudit = new List<string>();

        /// <summary>确定性校验点记录的"本场景实例数"（诊断：证明 inactive 实例也被计入）。</summary>
        private static readonly List<string> SceneInstanceAudit = new List<string>();

        [Header("模式（创建本场战斗前固定，战斗中不可修改）")]
        [SerializeField] private BattleRuntimeMode _requestedMode = BattleRuntimeMode.Legacy;
        [SerializeField] private bool _productionNewOnly;

        [Header("显式适配器槽位（顺序与内容都由本字段决定，不做容器发现）")]
        [Tooltip("Legacy 帧适配器：由 Assembly-CSharp 的 CombatDemo 实现 IBattleFrameAdapter。")]
        [SerializeField] private MonoBehaviour _legacyFrameAdapterSlot;

        [Tooltip("纯数据战斗来源：由 Assembly-CSharp 的真实 02B 初始化链实现 IBattleSimulationSource。")]
        [SerializeField] private MonoBehaviour _simulationSourceSlot;

        [Tooltip("New 模式显式只读视图消费者；必须实现 IBattleViewConsumer。")]
        [SerializeField] private MonoBehaviour _viewConsumerSlot;

        [Header("自动启动")]
        [Tooltip("Start() 时立即创建战斗（主战斗场景为 true；隐藏验证场景由测试显式驱动时为 false）。")]
        [SerializeField] private bool _autoStart = true;

        [Header("Shadow 比较配置")]
        [SerializeField] private int _shadowComparisonConfigVersion = 1;
        [SerializeField] private int _shadowMaxStepsPerComparison = 64;

        /// <summary>
        /// 单个 Shadow 世界最多持有的检查点数（超出丢弃最旧，并在报告里记录丢弃数）。
        ///
        /// 必须大于 <see cref="_shadowMaxStepsPerComparison"/>：否则每次比较都会因
        /// 检查点数超出单次预算而被作废，生产比较退化为"不再比较任何字段"（第二收尾轮 R1 的实测缺陷）。
        /// </summary>
        [SerializeField] private int _shadowMaxCheckpoints = 4096;
        [SerializeField] private ShadowComparisonMode _shadowComparisonMode = ShadowComparisonMode.Strict;

        private readonly RuntimeCallLedger _ledger = new RuntimeCallLedger();
        private readonly ShadowWriteCounters _shadowWrites = new ShadowWriteCounters();

        /// <summary>
        /// 本场的回放权威输入记录器（任务 09「必须产出」11 / 16）。
        ///
        /// 它是<strong>唯一</strong>的权威事实出口：Shadow runner 在唯一模拟入口里按
        /// <see cref="ShadowAuthorityProtocol"/> 往这里收口，真实请求镜像再从这里取
        /// 只含 <c>Player</c> 的事实。每场战斗重建一次，避免上一场的事实残留。
        /// </summary>
        private ReplayAuthorityInput _shadowAuthorityInput = new ReplayAuthorityInput();
        private readonly FrameAdapterSet _adapters = new FrameAdapterSet();
        private readonly UnityBattleDriver _newDriver = new UnityBattleDriver();
        private readonly List<LegacyWriterRegistration> _legacyWriters = new List<LegacyWriterRegistration>();
        private readonly List<ShadowComparisonReport> _shadowReports = new List<ShadowComparisonReport>();
        private readonly List<Logic.Snapshots.LogicSnapshot> _legacyCheckpoints =
            new List<Logic.Snapshots.LogicSnapshot>();

        /// <summary>Legacy 侧只读观测序列（Shadow 比较的左侧；按检查点顺序累积）。</summary>
        private readonly List<LegacyLogicObservation> _legacyObservations =
            new List<LegacyLogicObservation>();

        /// <summary>定义侧槽位顺序（SlotId Ordinal 升序 ⇒ UnitId），比较槽位映射时使用。</summary>
        private readonly List<LegacySlotOrderEntry> _legacySlotOrder = new List<LegacySlotOrderEntry>();

        private BattleRuntimeContext _context;
        private IBattleSimulationSource _simulationSource;
        private ILegacyLogicObservationSource _legacyObservationSource;
        private BattleRuntimeMode? _battleMode;
        private bool _battleCreated;
        private bool _stopped;
        private bool _disposed;
        private string _startupRejection;
        private string _lastStopReason;
        private int _explicitDriverCount;
        private ShadowComparisonReport _lastShadowReport;
        private IReadOnlyList<ShadowMigrationObligation> _diagnosticMigrationEvidence;
        public void SetShadowMigrationEvidenceForDiagnostics(IReadOnlyList<ShadowMigrationObligation> evidence)
        {
            if (_productionNewOnly || _battleCreated) throw new InvalidOperationException("MIGRATION_EVIDENCE_DIAGNOSTIC_SETUP_ONLY");
            _diagnosticMigrationEvidence = evidence == null ? null : new List<ShadowMigrationObligation>(evidence).AsReadOnly();
        }
        // ---------------- 只读观测面（PlayMode 测试直接读取，不从日志推断） ----------------

        /// <summary>可观察调用计数账本。</summary>
        public RuntimeCallLedger Ledger => _ledger;

        /// <summary>
        /// 本场的<strong>回放权威输入记录器</strong>（任务 09「必须产出」11 / 16）。
        ///
        /// 只含入口绑定之后的 <c>Player</c> 事实（Issuer / ProducerOrdinal / Request / 原始提交 Tick）
        /// 与它们的接受/拒绝处置；AI/System 由新模拟按相同初始输入与 RNG 从 Tick 0 重建，
        /// 因此在这里<strong>连载荷都不保存</strong>（只累加
        /// <see cref="ReplayAuthorityInput.ExcludedNonAuthoritativeFactCount"/>）。
        /// </summary>
        public ReplayAuthorityInput ShadowAuthorityInput => _shadowAuthorityInput;

        /// <summary>Shadow 新模拟写入计数器（Shadow 模式必须全 0）。</summary>
        public ShadowWriteCounters ShadowWrites => _shadowWrites;

        /// <summary>已登记的 Legacy 从属写入者（含当前门控状态）。</summary>
        public IReadOnlyList<LegacyWriterRegistration> LegacyWriters => _legacyWriters;

        /// <summary>显式适配器集合。</summary>
        public FrameAdapterSet Adapters => _adapters;

        /// <summary>最小 New Driver。</summary>
        public UnityBattleDriver NewDriver => _newDriver;

        /// <summary>Compatibility callbacks fail closed in New, even before their UI ports are bound.</summary>
        public static bool LegacyWritesAllowed
        {
            get
            {
                var owner = FirstClockOwner();
                return owner == null || owner._battleMode != BattleRuntimeMode.New;
            }
        }

        /// <summary>Shadow runner（可能为 null；由 <see cref="EnsureShadowRunner"/> 创建）。</summary>
        public ShadowBattleRunner Shadow => _adapters.Shadow;

        /// <summary>本场战斗固定的模式；战斗创建之前为 null。</summary>
        public BattleRuntimeMode? BattleMode => _battleMode;

        /// <summary>是否已创建战斗。</summary>
        public bool BattleCreated => _battleCreated;

        /// <summary>是否已停止。</summary>
        public bool IsStopped => _stopped;

        /// <summary>启动被拒绝时的稳定拒绝码（null = 未拒绝）。</summary>
        public string StartupRejection => _startupRejection;

        /// <summary>最后一次停止原因。</summary>
        public string LastStopReason => _lastStopReason;

        /// <summary>每个被销毁的 Bootstrap 所发出的停止原因（诊断/销毁边界证据）。</summary>
        public static IReadOnlyList<string> DestroyedStopReasons
            => new List<string>(DestroyedStopReasonLog);

        private static readonly List<string> DestroyedStopReasonLog = new List<string>();

        /// <summary>是否已获取顶层时钟所有权。</summary>
        public bool OwnsTopLevelClock { get; private set; }

        /// <summary>本组件是否被允许推进时钟（暂停时为 false）。</summary>
        public bool IsPaused { get; private set; }

        /// <summary>最近一次 Shadow 比较报告（可能为 null）。</summary>
        public ShadowComparisonReport LastShadowReport => _lastShadowReport;

        /// <summary>本场战斗的全部 Shadow 比较报告（按检查点顺序）。</summary>
        public IReadOnlyList<ShadowComparisonReport> ShadowReports => _shadowReports;

        /// <summary>Legacy 侧逻辑检查点（Shadow 比较的左侧）。</summary>
        public IReadOnlyList<Logic.Snapshots.LogicSnapshot> LegacyCheckpoints => _legacyCheckpoints;

        /// <summary>
        /// Legacy 侧只读观测序列（Shadow 比较的真实左侧；按逻辑 Tick 去重后累积）。
        ///
        /// 任务 03B 第二收尾轮 R1 的观测面：它是"生产比较真的有内容"的可断言证据，
        /// 而不是靠日志文本推断。
        /// </summary>
        public IReadOnlyList<LegacyLogicObservation> LegacyObservations => _legacyObservations;

        /// <summary>
        /// 被裁剪出比较窗口的 Legacy 观测数（最旧优先）。
        ///
        /// 生产 Shadow 战斗可以跑任意长；比较窗口必须有界，否则观测列表会无限增长、
        /// 每次比较都因超出单次预算而被作废——那正是"生产比较退化为不比较"的另一种形式。
        /// 这里只裁剪**本场比较窗口**（每帧最多比较最近 <c>MaxCheckpoints</c> 个检查点），
        /// 不丢弃逻辑 Tick、不改旧时间线、不影响等价声明之外的事实。
        /// </summary>
        public int DroppedLegacyObservations { get; private set; }

        /// <summary>定义侧槽位顺序（<c>SlotId</c> Ordinal 升序 ⇒ <c>UnitId</c>）。</summary>
        public IReadOnlyList<LegacySlotOrderEntry> LegacySlotOrder => _legacySlotOrder;

        /// <summary>Shadow 检查点的对齐键序列（逻辑 Tick@检查点）。</summary>
        public IReadOnlyList<string> ShadowCheckpointKeys
        {
            get
            {
                var keys = new List<string>();
                var shadow = _adapters.Shadow;
                if (shadow == null) return keys;
                for (int i = 0; i < shadow.Count; i++) keys.Add(shadow[i].AlignmentKey);
                return keys;
            }
        }

        /// <summary>
        /// 当前存活的 Bootstrap 数量（含未启用组件、含 <b>inactive GameObject</b> 上的实例）。
        ///
        /// 实现为"已登记实例 ∪ 场景扫描结果"的并集计数：
        /// <list type="bullet">
        /// <item>已登记实例来自 <c>Awake</c>，覆盖"已激活但被禁用"的重复组件
        /// （Unity 的 <c>FindObjectsByType</c> 默认看不到 <c>enabled = false</c> 的组件）。</item>
        /// <item>场景扫描使用 <c>FindObjectsInactive.Include</c>，覆盖 <b>inactive GameObject</b>
        /// 上的实例——它们的 <c>Awake</c> 永远不会执行，只靠登记表会漏计，
        /// 从而让"恰好一个 Bootstrap"的检测被 inactive 对象绕过。</item>
        /// </list>
        /// </summary>
        public static int LiveInstanceCount => EnumerateSceneInstances().Count;

        /// <summary>
        /// 全部存活 Bootstrap 的只读副本（含 inactive 对象上的实例与 enabled = false 的重复组件）。
        /// 允许在场景中发现被禁用的组件——Unity 的 <c>FindObjectsByType</c> 默认不返回
        /// <c>enabled = false</c> 的 MonoBehaviour，因此这里是审计与测试的权威来源。
        /// </summary>
        public IReadOnlyList<BattleRuntimeBootstrap> LiveInstancesSnapshot()
            => EnumerateSceneInstances();

        /// <summary>
        /// 静态版本：读取全部存活 Bootstrap（含 enabled = false 的组件与 inactive 对象上的实例），
        /// <strong>不创建任何探测组件</strong>。
        ///
        /// 之所以需要静态入口：用一个临时 Bootstrap 去"探测"场景会真地触发
        /// 重复启动拒绝（并打错误日志），污染 PlayMode 测试的日志断言。
        /// </summary>
        public static IReadOnlyList<BattleRuntimeBootstrap> EnumerateLiveInstances()
            => EnumerateSceneInstances();

        /// <summary>
        /// 场景实例枚举：已登记实例 ∪ 场景扫描实例（<c>FindObjectsInactive.Include</c>）。
        /// 只统计位于<b>有效已加载场景</b>中的实例。
        /// </summary>
        public static IReadOnlyList<BattleRuntimeBootstrap> EnumerateSceneInstances()
        {
            var found = new List<BattleRuntimeBootstrap>();

            var scanned = UnityEngine.Object.FindObjectsByType<BattleRuntimeBootstrap>(
                FindObjectsInactive.Include);
            for (int i = 0; i < scanned.Length; i++) AddUniqueSceneInstance(found, scanned[i]);

            // 已登记实例可能刚被创建（尚未进入扫描结果）或被销毁（扫描结果仍在刷新），
            // 并集保证两者都不漏计。
            for (int i = 0; i < LiveInstances.Count; i++) AddUniqueSceneInstance(found, LiveInstances[i]);

            return found;
        }

        private static void AddUniqueSceneInstance(
            List<BattleRuntimeBootstrap> target, BattleRuntimeBootstrap candidate)
        {
            if (candidate == null) return;
            if (!candidate.gameObject.scene.IsValid()) return;
            for (int i = 0; i < target.Count; i++)
            {
                if (ReferenceEquals(target[i], candidate)) return;
            }
            target.Add(candidate);
        }

        /// <summary>当前场景中拥有顶层时钟的实例数（正常恒为 0 或 1）。</summary>
        public static int ClockOwnerCountInScene()
        {
            var instances = EnumerateSceneInstances();
            int owners = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                if (instances[i].OwnsTopLevelClock) owners++;
            }
            return owners;
        }

        /// <summary>确定性校验点记录的非所有者实例（对象路径，按发现顺序）。</summary>
        public static IReadOnlyList<string> DuplicateInstancesObserved
            => new List<string>(DuplicateInstanceAudit);

        /// <summary>
        /// 确定性校验点（<c>Start</c>）记录的场景实例计数快照（诊断：证明 inactive 实例被计入，
        /// 且本场景的时钟所有者恰好一个）。
        /// </summary>
        public static IReadOnlyList<string> SceneInstanceObservations
            => new List<string>(SceneInstanceAudit);

        /// <summary>Bootstrap 的 Unity <c>Update()</c> 脚本执行顺序。</summary>
        public static int GetCheckpointExecutionOrder()
            => RuntimeCallbackRegistry.CheckpointExecutionOrder;

        /// <summary>记录一次"由测试显式驱动"的 Update，用于计数诊断。</summary>
        public int ExplicitDriverCount => _explicitDriverCount;

        // ---------------- Unity 生命周期（唯一顶层时钟） ----------------

        private void Awake()
        {
            // 先登记再判定：任何后续判定（包括重复检测）都建立在"本实例已被计入"之上，
            // 因此计数与拒绝码都不依赖 AddComponent/Awake 的隐式时序。
            LiveInstances.Add(this);
            EvaluateTopLevelClockOwnership("Awake");
        }

        /// <summary>
        /// 顶层时钟所有权判定（确定性时点：<c>Awake</c> / <c>Start</c>）。
        ///
        /// 规则：<b>本场景中最早完成 Awake 且未被拒绝的实例</b>是唯一所有者（确定性：
        /// 场景对象的 Awake 顺序由场景序列化顺序决定，运行时新增对象的顺序就是 AddComponent 顺序）。
        /// 其余实例一律<b>快速失败</b>：禁用自身、写入含双方对象路径的稳定拒绝码，
        /// 绝不静默接管时钟。
        /// </summary>
        private void EvaluateTopLevelClockOwnership(string point)
        {
            var owner = FirstClockOwner();
            if (owner != null && !ReferenceEquals(owner, this))
            {
                OwnsTopLevelClock = false;
                _startupRejection = BOOTSTRAP_DUPLICATE + "|at=" + point + "|" + DescribeInstanceCollision();
                DuplicateInstanceAudit.Add(_startupRejection);
                Debug.LogError("[" + nameof(BattleRuntimeBootstrap) + "] " + _startupRejection);
                enabled = false;
                return;
            }

            if (OwnsTopLevelClock) return;

            OwnsTopLevelClock = true;
            LegacyWriterRegistry.SetActiveBootstrap(this);
            CollectLegacyWriters();
        }

        /// <summary>本场景最早获得所有权的实例（无则 null）。</summary>
        private static BattleRuntimeBootstrap FirstClockOwner()
        {
            for (int i = 0; i < LiveInstances.Count; i++)
            {
                var candidate = LiveInstances[i];
                if (candidate == null) continue;
                if (candidate.OwnsTopLevelClock) return candidate;
            }
            return null;
        }

        /// <summary>
        /// "本场景恰好一个 Bootstrap"的确定性审计快照（只读；不改变任何战斗状态）。
        ///
        /// 与 <c>Awake</c>/<c>Start</c> 的判定使用同一份数据，因此测试/工具可以在任意时点
        /// 复核"含 inactive 对象的实例计数"，而不必依赖帧序或 AddComponent 时序。
        /// </summary>
        public sealed class SceneInstanceAuditReport
        {
            internal SceneInstanceAuditReport(
                int sceneInstances, int clockOwners, int inactiveOrDisabled,
                IReadOnlyList<string> foreignPaths, string auditorPath)
            {
                SceneInstances = sceneInstances;
                ClockOwners = clockOwners;
                InactiveOrDisabled = inactiveOrDisabled;
                ForeignPaths = foreignPaths;
                AuditorPath = auditorPath ?? string.Empty;
            }

            public int SceneInstances { get; }

            public int ClockOwners { get; }

            /// <summary>inactive 对象上的实例 + 被禁用的实例（两者都逃过 enabled-only 计数）。</summary>
            public int InactiveOrDisabled { get; }

            /// <summary>本场景中除审计者之外的全部实例对象路径（空 = 恰好一个）。</summary>
            public IReadOnlyList<string> ForeignPaths { get; }

            public string AuditorPath { get; }

            public bool IsUnique => ForeignPaths.Count == 0 && ClockOwners <= 1;

            public string Describe()
                => "audit=" + (AuditorPath ?? "<null>")
                   + " sceneInstances=" + SceneInstances
                   + " clockOwners=" + ClockOwners
                   + " inactiveOrDisabled=" + InactiveOrDisabled
                   + " foreign=[" + string.Join(",", ForeignPaths) + "]";
        }

        /// <summary>
        /// 立即执行一次"本场景恰好一个 Bootstrap"的确定性审计（只读，不产生拒绝、不改变状态）。
        /// </summary>
        public SceneInstanceAuditReport AuditSceneInstances()
        {
            var instances = EnumerateSceneInstances();
            var foreign = ForeignInstancesInScene(instances);
            var foreignPaths = new List<string>();
            for (int i = 0; i < foreign.Count; i++) foreignPaths.Add(DescribePath(foreign[i]));

            return new SceneInstanceAuditReport(
                instances.Count, ClockOwnerCountInScene(), CountInactiveOrDisabled(instances),
                foreignPaths, DescribePath(this));
        }

        private void Start()
        {
            if (!ValidateSingleInstanceAtSceneStart()) return;
            if (_autoStart) StartBattle(_requestedMode);
        }

        /// <summary>
        /// <b>确定性统一校验点</b>：Unity 保证全部场景对象的 <c>Awake</c>/<c>OnEnable</c>
        /// 在第一个 <c>Start</c> 之前完成，因此这里是"本场景恰好一个 Bootstrap"唯一
        /// 稳定的校验时点。
        ///
        /// 与 <c>Awake</c> 的快速失败互补：<b>inactive GameObject</b> 上的实例永远不会执行
        /// <c>Awake</c>，只靠登记表会漏计；只有在本时点做一次
        /// <c>FindObjectsInactive.Include</c> 扫描，才能可靠地把它们计入并拒绝自动启动，
        /// 而不是让"场景其实有两个 Bootstrap"静默通过。
        /// </summary>
        private bool ValidateSingleInstanceAtSceneStart()
        {
            if (!OwnsTopLevelClock || !enabled) return false;

            var instances = EnumerateSceneInstances();
            SceneInstanceAudit.Add(DescribePath(this) + "|sceneInstances=" + instances.Count
                + "|clockOwners=" + ClockOwnerCountInScene()
                + "|inactiveOrDisabled=" + CountInactiveOrDisabled(instances));

            var foreign = ForeignInstancesInScene(instances);
            if (foreign.Count > 0)
            {
                _startupRejection = BOOTSTRAP_DUPLICATE + "|at=Start|" + DescribeInstanceCollision();
                DuplicateInstanceAudit.Add(_startupRejection);
                Debug.LogError("[" + nameof(BattleRuntimeBootstrap) + "] " + _startupRejection);
                return false;
            }

            return true;
        }

        private List<BattleRuntimeBootstrap> ForeignInstancesInScene(
            IReadOnlyList<BattleRuntimeBootstrap> instances)
        {
            var foreign = new List<BattleRuntimeBootstrap>();
            var scene = gameObject.scene;
            for (int i = 0; i < instances.Count; i++)
            {
                var candidate = instances[i];
                if (candidate == null || ReferenceEquals(candidate, this)) continue;
                if (candidate.gameObject.scene != scene) continue;
                foreign.Add(candidate);
            }
            return foreign;
        }

        private static int CountInactiveOrDisabled(IReadOnlyList<BattleRuntimeBootstrap> instances)
        {
            int count = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                var candidate = instances[i];
                if (candidate == null) continue;
                if (!candidate.enabled || !candidate.gameObject.activeInHierarchy) count++;
            }
            return count;
        }

        private void Update()
        {
            if (!OwnsTopLevelClock || !enabled) return;
            Ledger.RecordBootstrapUpdate();
            AdvanceViewDiagnostics(Time.unscaledDeltaTime);
            if (!_battleCreated || _stopped) return;

            float deltaTime = Time.deltaTime;
            float unscaledDeltaTime = Time.unscaledDeltaTime;
            AdvanceOneFrame(deltaTime, unscaledDeltaTime);
        }

        /// <summary>
        /// 只读 Shadow 检查点。**不推进时钟、不写两边状态**；
        /// 脚本执行顺序由 <c>[DefaultExecutionOrder]</c> 固定为晚于全部已登记 Legacy 写入者。
        /// </summary>
        private void LateUpdate()
        {
            if (!OwnsTopLevelClock || !enabled) return;
            if (!_battleCreated || _stopped) return;
            if (_battleMode != BattleRuntimeMode.Shadow) return;

            Ledger.RecordBootstrapCheckpoint();
            RunShadowCheckpoint();
        }

        private void OnDestroy()
        {
            if (OwnsTopLevelClock)
            {
                if (StopBattle("BOOTSTRAP_DESTROYED"))
                {
                    // 销毁边界的可观察证据：每个 Bootstrap 至多贡献一条记录。
                    DestroyedStopReasonLog.Add("BOOTSTRAP_DESTROYED");
                }
                ReleaseBattle();
            }

            LegacyWriterRegistry.ClearActiveBootstrap(this);
            LiveInstances.Remove(this);
        }

        // ---------------- 公开驱动面 ----------------

        /// <summary>设置创建本场战斗前要使用的模式；战斗已创建时显式拒绝。</summary>
        public bool TrySetRequestedMode(BattleRuntimeMode mode, out string rejection)
        {
#if !UNITY_EDITOR
            if (mode != BattleRuntimeMode.New) { rejection = "PRODUCTION_LEGACY_MODE_REMOVED"; return false; }
#endif
            if (_productionNewOnly && mode != BattleRuntimeMode.New)
            {
                rejection = "PRODUCTION_LEGACY_MODE_REMOVED";
                return false;
            }
            if (_battleCreated)
            {
                rejection = BOOTSTRAP_MODE_CHANGE_REJECTED
                    + "|active=" + BattleRuntimeModes.Describe(_battleMode ?? _requestedMode)
                    + "|requested=" + BattleRuntimeModes.Describe(mode);
                return false;
            }

            _requestedMode = mode;
            rejection = null;
            return true;
        }

        /// <summary>
        /// 创建本场战斗。模式在此刻固定；重复创建是显式错误（每场战斗只能初始化一次）。
        /// </summary>
        public bool StartBattle(BattleRuntimeMode mode)
        {
            if (_battleCreated) throw new LogicDefinitionException(BOOTSTRAP_DOUBLE_START, name);

            _startupRejection = null;
            if (!TrySetRequestedMode(mode, out string modeRejection))
            {
                _startupRejection = modeRejection;
                return false;
            }

            var factoryAdapters = FindAdaptersInScene();

            // 1. 本场景恰有一个**时钟所有者**。已快速失败的重复实例不参与所有权，
            //    因此它们既被可靠计数（LiveInstanceCount / 审计），又不会阻止唯一
            //    Bootstrap 正常创建战斗。
            int clockOwners = ClockOwnerCountInScene();
            if (clockOwners != 1)
            {
                _startupRejection = BOOTSTRAP_DUPLICATE + "|clockOwners=" + clockOwners
                    + "|" + DescribeInstanceCollision();
                return false;
            }

            // 2. 没有已登记的自主推进适配器仍启用。
            string autonomous = DescribeAutonomousAdapters(factoryAdapters);
            if (autonomous != null)
            {
                _startupRejection = BOOTSTRAP_ADAPTER_OWNS_AUTONOMOUS_UPDATE + "|" + autonomous;
                return false;
            }

            // 3. 未分类逻辑写入者阻止 New 启动。
            if (mode == BattleRuntimeMode.New)
            {
                string unclassified = DescribeUnclassifiedLogicWriters();
                if (unclassified != null)
                {
                    _startupRejection = BOOTSTRAP_UNCLASSIFIED_LEGACY_LOGIC_WRITER + "|" + unclassified;
                    Ledger.RecordNewStartupRejection(_startupRejection);
                    return false;
                }
            }

            _simulationSource = ResolveSimulationSource(factoryAdapters);
            _legacyObservationSource = _simulationSource as ILegacyLogicObservationSource;
            _battleMode = mode;
            _stopped = false;
            _disposed = false;
            _battleCreated = true;

            Ledger.Reset();
            ShadowWrites.Reset();
            _legacyCheckpoints.Clear();
            _legacyObservations.Clear();
            DroppedLegacyObservations = 0;
            _shadowReports.Clear();
            _lastShadowReport = null;

            // 权威输入记录器**就地**复位（不替换对象）：真实请求镜像持有的就是本对象，
            // 替换会让镜像读到上一场的事实。
            _shadowAuthorityInput.Reset();

            // 每场战斗重新装配参与者，并<b>显式复位</b>本场参与者：
            // 适配器是场景组件/长期对象，"上一场已停止"的状态会残留，不复位就会出现
            // "新战斗开局即停止"（Adapters.Legacy.IsStopped == true）。
            ResetAdaptersForNewBattle(factoryAdapters.Legacy);
            _context = new BattleRuntimeContext(
                mode, Ledger, ShadowWrites, _simulationSource, _shadowAuthorityInput);

            // 定义侧槽位顺序（SlotId Ordinal 升序 ⇒ UnitId）：Shadow 比较用它独立解析槽位映射。
            // 只使用任务 02B 的只读纯数据入口；构建失败时留空（比较器据此报告槽位不可解析）。
            if (_simulationSource != null && string.IsNullOrEmpty(_simulationSource.LastConfigurationError))
                BuildLegacySlotOrder(_simulationSource.BuildSeed());

            _newDriver.Initialize(_context);
            _newDriver.SetPaused(IsPaused);

            if (mode == BattleRuntimeMode.Legacy || mode == BattleRuntimeMode.Shadow)
            {
                if (_adapters.Legacy == null)
                {
                    _startupRejection = BOOTSTRAP_ADAPTER_SLOT_MISSING + "|legacy";
                    _battleCreated = false;
                    return false;
                }

                _adapters.Legacy.Initialize(_context);
            }

            if (mode == BattleRuntimeMode.Shadow)
            {
                var shadow = EnsureShadowRunner();
                try
                {
                    shadow.Initialize(_context);
                }
                catch (LogicDefinitionException exception)
                {
                    _startupRejection = BOOTSTRAP_ADAPTER_SLOT_INVALID + "|shadow|" + exception.Message;
                    _adapters.Legacy.StopBattle("SHADOW_INITIALIZATION_FAILED");
                    _battleCreated = false;
                    return false;
                }
            }

            if (mode == BattleRuntimeMode.New)
            {
                try
                {
                    if (_viewConsumerSlot != null && !(_viewConsumerSlot is IBattleViewConsumer))
                        throw new LogicDefinitionException("VIEW_CONSUMER_SLOT_INVALID", DescribePath(_viewConsumerSlot));
                    _newDriver.BindView(_viewConsumerSlot as IBattleViewConsumer);
                    _newDriver.CreateSimulation(_simulationSource);
                    if (_viewConsumerSlot is IBattleInputConsumer inputConsumer)
                        inputConsumer.BindInput(_newDriver.CreatePlayerInputPorts(
                            new Logic.Ids.ControllerId(inputConsumer.PlayerControllerId)));
                }
                catch (LogicDefinitionException exception)
                {
                    _startupRejection = BOOTSTRAP_ADAPTER_SLOT_INVALID + "|new|" + exception.Message;
                    _battleCreated = false;
                    return false;
                }
            }

            ApplyLegacyWriterGroupGate(mode);
            return true;
        }

        /// <summary>
        /// 为新一场战斗重新装配并复位适配器（幂等）。
        ///
        /// 规则：
        /// <list type="bullet">
        /// <item>集合先清空——上一场的 Shadow runner 不会在下一场 Legacy 战斗里被再次停止，
        /// 也不会被误算作"活动适配器"。</item>
        /// <item>本场参与者调用 <see cref="IBattleFrameAdapter.ResetForNewBattle"/>，
        /// 清除上一场残留的 initialized/stopped 状态。</item>
        /// <item>Legacy 适配器是场景组件，其复位<b>不得</b>销毁场景对象、
        /// <b>不得</b>重置旧权威（<c>BattleTimeline.TotalAdvanceTimeCalls</c>）的累计计数。</item>
        /// </list>
        /// </summary>
        private void ResetAdaptersForNewBattle(IBattleFrameAdapter legacy)
        {
            _adapters.Clear();

            if (legacy != null) legacy.ResetForNewBattle();
            _adapters.SetLegacy(legacy);

            _newDriver.ResetForNewBattle();
            _adapters.SetNewDriver(_newDriver);
        }

        /// <summary>停止当前战斗（幂等）。</summary>
        public bool StopBattle(string reason)
        {
            if (!_battleCreated || _stopped) return false;

            _lastStopReason = reason ?? string.Empty;
            var adapters = _adapters.All;
            for (int i = 0; i < adapters.Count; i++)
            {
                var adapter = adapters[i];
                if (adapter == null) continue;
                adapter.StopBattle(_lastStopReason);
            }

            _stopped = true;
            return true;
        }

        /// <summary>释放本场战斗（幂等）。销毁边界：先停止，再释放。</summary>
        public bool ReleaseBattle()
        {
            if (!_battleCreated || _disposed) return false;

            if (!_stopped) StopBattle("RELEASE_WITHOUT_STOP");
            _disposed = true;
            _battleCreated = false;
            _battleMode = null;
            _legacyCheckpoints.Clear();

            // 释放后不再持有任何适配器引用：下一场战斗（可以是另一模式）重新装配，
            // 因此"每个活动适配器恰好停止一次"在跨战斗序列上仍然成立。
            _adapters.Clear();
            _newDriver.ReleaseSimulation();
            return true;
        }

        public void SetPaused(bool paused)
        {
            IsPaused = paused;
            _newDriver.SetPaused(paused);
        }

        public void TogglePaused() => SetPaused(!IsPaused);

        /// <summary>
        /// 由测试或工具显式推进一步（不经过 Unity 帧循环）。
        /// 与 <c>Update()</c> 共用同一条路径，因此调用矩阵与真实运行一致。
        /// </summary>
        public void DriveFrameForTests(float deltaTime, float unscaledDeltaTime)
        {
            _explicitDriverCount++;
            AdvanceViewDiagnostics(unscaledDeltaTime);
            if (!_battleCreated || _stopped) return;
            AdvanceOneFrame(deltaTime, unscaledDeltaTime);
        }
        private void AdvanceViewDiagnostics(double unscaledSeconds)
        {
            if (_battleCreated && _battleMode == BattleRuntimeMode.New)
                (_viewConsumerSlot as IBattleDiagnosticFrameConsumer)?.AdvanceDiagnostics(unscaledSeconds);
        }

        /// <summary>由测试显式触发一次只读 Shadow 检查点。</summary>
        public void RunCheckpointForTests()
        {
            if (!_battleCreated || _stopped) return;
            if (_battleMode != BattleRuntimeMode.Shadow) return;
            Ledger.RecordBootstrapCheckpoint();
            RunShadowCheckpoint();
        }

        /// <summary>
        /// 负控制探针（任务 03B 第二收尾轮 R3）：故意经<strong>被守卫的写入路径</strong>
        /// 尝试一次越界写入，返回稳定拒绝码。
        ///
        /// 它存在的唯一理由是让"Shadow 写入数为 0"成为<strong>可以失败</strong>的断言：
        /// 探针必须被拒绝，且 <see cref="ShadowWriteCounters.RejectedAttempts"/> 与对应分项
        /// 必须因此递增。没有它，四项计数器为零就只是"没人调用"，不是证据。
        ///
        /// <strong>第三收尾轮 R1 的边界声明</strong>：本探针是
        /// <see cref="ShadowWriteWall.TryWrite"/> 在<strong>全仓唯一的生产调用点</strong>——
        /// 生产侧不存在写入面，也不存在写入点埋点（结构证据见
        /// <see cref="ShadowWriteWall"/> 的覆盖边界说明）。因此本探针证明的是
        /// "没有调用方持有已发放令牌 + 计数器可以失败"，
        /// <strong>不是</strong>"某个真实写入点没写"。不得据此宣称写入面已被监控。
        /// </summary>
        public string AttemptForbiddenShadowWriteForTests(ShadowWriteKind kind)
            => ShadowWriteWall.TryWrite(ShadowWrites, ShadowWriteWall.Permit, kind, "negative-control-probe");

        /// <summary>
        /// 诊断/测试用：绑定一份<b>纯数据</b>种子后把 Shadow 检查点采样到同一仿真事实上。
        ///
        /// 它不改变任何门控、不写场景、不推进旧时钟，只把 Shadow 的只读检查点
        /// 登记进同一比较配置，用于证明"对齐按逻辑 Tick + 检查点键，不按帧序号"、
        /// "预算超限即作废比较"与"暂不可比较字段有责任任务与清零门槛"。
        /// </summary>
        public ShadowComparisonReport RunShadowCheckpointOnSeed(BattleSimulationSeed seed, string caseId)
        {
            if (seed == null) throw new LogicDefinitionException("SHADOW_SEED_MISSING", "seed is null");

            var runner = EnsureShadowRunner();
            runner.Configure(seed, EmptyShadowMirroredInputSource.Instance, BuildComparisonConfig());
            if (!string.IsNullOrEmpty(caseId)) runner.CaseId = caseId;
            runner.Initialize(new BattleRuntimeContext(
                BattleRuntimeMode.Shadow, Ledger, ShadowWrites, _simulationSource));

            runner.CaptureCheckpoint(ShadowBattleRunner.DefaultCheckpointName);

            var shadowSnapshots = runner.SnapshotSequence();
            var report = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash,
                    seed.EncounterId.Value,
                    BattleRuntimeMode.Shadow,
                    seed.InputSummary,
                    seed.RulesVersion,
                    BuildComparisonConfig(),
                    shadowSnapshots,
                    shadowSnapshots,
                    runner.EventBindings),
                ShadowCasePolicy.CreateDefault(runner.CaseId, seed.RulesVersion));

            _lastShadowReport = report;
            _shadowReports.Add(report);
            return report;
        }

        /// <summary>测试用：注册一个额外的 Legacy 从属写入者，验证 New 模式的未分类门禁。</summary>
        public void RegisterLegacyWriterForTests(string callbackSite, MonoBehaviour gateTarget)
        {
            RegisterLegacyWriter(callbackSite, "<test>", "<test>", gateTarget);
        }

        /// <summary>测试用：注销一个通过 <see cref="RegisterLegacyWriterForTests"/> 注入的写入者。</summary>
        public bool UnregisterLegacyWriterForTests(MonoBehaviour gateTarget)
        {
            if (gateTarget == null) return false;
            for (int i = _legacyWriters.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_legacyWriters[i].GateTarget, gateTarget)) continue;
                _legacyWriters.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>测试用：强制应用一次门控（用于在 StartBattle 之前验证门控决策）。</summary>
        public void ApplyLegacyWriterGroupGateForTests(BattleRuntimeMode mode)
            => ApplyLegacyWriterGroupGate(mode);

        /// <summary>测试用：把已注册写入者的类型标记为"未分类逻辑写入者"。</summary>
        public static void MarkUnclassifiedLogicWriterTypeForTests(string typeName)
            => MarkUnclassifiedLogicWriterType(typeName);

        /// <summary>
        /// 把某个类型标记为"未分类逻辑写入者"。运行时写入者注册表在 New 模式下
        /// 遇到未分类类型时也会调用它（禁止事项第 2 条）。
        /// </summary>
        public static void MarkUnclassifiedLogicWriterType(string typeName)
        {
            if (!string.IsNullOrEmpty(typeName)) UnclassifiedLogicWriterTypes.Add(typeName);
        }

        /// <summary>测试/工具清零未分类集合（只影响诊断，不影响战斗状态）。</summary>
        public static void ClearUnclassifiedLogicWriterTypes()
            => UnclassifiedLogicWriterTypes.Clear();

        /// <summary>
        /// 解除某个回调点的"未分类"标记：调用方声明它已获得显式分类。
        /// 由 <c>RuntimeCallbackRegistry</c> 的分类表变化或测试的显式声明触发。
        /// </summary>
        public static void ClearUnclassifiedLogicWriterSite(string callbackSite)
        {
            if (string.IsNullOrEmpty(callbackSite)) return;
            UnclassifiedLogicWriterTypes.Remove(callbackSite);
        }

        private static readonly HashSet<string> UnclassifiedLogicWriterTypes = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>当前被判定为"未分类逻辑写入者"的类型名（诊断与门禁用）。</summary>
        public static IReadOnlyCollection<string> UnclassifiedLogicWriterTypeNames => UnclassifiedLogicWriterTypes;

        // ---------------- 帧推进 ----------------

        private void AdvanceOneFrame(float deltaTime, float unscaledDeltaTime)
        {
            switch (_battleMode)
            {
                case BattleRuntimeMode.Legacy:
                {
                    var adapter = _adapters.Legacy;
                    if (adapter == null) return;
                    int before = LegacyAdvanceTimeTotalCalls(adapter);
                    adapter.AdvanceFrame(new BattleFrameDelta(deltaTime, IsPaused, false));
                    Ledger.RecordLegacyAdapterInvocation(
                        deltaTime, adapter is ILegacyPauseAware pauseAware && pauseAware.IsPaused,
                        before, LegacyAdvanceTimeTotalCalls(adapter));
                    break;
                }

                case BattleRuntimeMode.Shadow:
                {
                    var adapter = _adapters.Legacy;
                    var shadow = _adapters.Shadow;
                    if (adapter != null)
                    {
                        int before = LegacyAdvanceTimeTotalCalls(adapter);
                        adapter.AdvanceFrame(new BattleFrameDelta(deltaTime, IsPaused, false));
                        Ledger.RecordLegacyAdapterInvocation(
                            deltaTime, adapter is ILegacyPauseAware pauseAware && pauseAware.IsPaused,
                            before, LegacyAdvanceTimeTotalCalls(adapter));
                    }

                    // 新模拟用**旧时间线的真实逻辑 Tick**做同步点（第二收尾轮 R1）：
                    // 两侧各自报告自己的 Tick 才是可比较的事实，而"新侧自己维护一个独立时钟"
                    // 会让两个 Tick 永不相等 ⇒ 生产比较恒 UNALIGNED（上一轮的真实现象）。
                    // Tick 起点归一化（旧 0 = 开局 vs 新 -1 = 未 Step）在 runner 内完成。
                    //
                    // 注意：这里只把旧 Tick 当同步点，不读旧组件字段、不写旧状态；
                    // 暂停状态仍必须**两侧一致**（暂停时新模拟同样不推进）。
                    // delta 参数保留 unscaled 语义（观测来源不可用时的退化路径仍用它累积固定 Tick），
                    // 仅作为诊断输入进入 LastNewDeltaTime。
                    shadow?.AdvanceFrame(
                        new BattleFrameDelta(unscaledDeltaTime, IsPaused, true),
                        LegacyTimelineTickForComparison());
                    break;
                }

                case BattleRuntimeMode.New:
                {
                    // 旧推进路径调用次数恒为 0：这里刻意不触碰 Legacy 适配器。
                    _newDriver.AdvanceFrame(new BattleFrameDelta(unscaledDeltaTime, IsPaused, true));
                    break;
                }
            }
        }

        private int LegacyAdvanceTimeTotalCalls(IBattleFrameAdapter adapter)
        {
            var observed = adapter as ILegacyAdvanceTimeObservable;
            return observed != null ? observed.AdvanceTimeCallCount : 0;
        }

        /// <summary>
        /// 旧时间线的真实逻辑 Tick（Shadow 同步点与比较对齐键）。
        ///
        /// 只通过只读观测接口取得；观测来源不可用时返回 <c>-1</c>（不猜测、不用 0 顶替——
        /// 0 会被当成"开局、无需推进"，是典型的 fail-open）。
        /// </summary>
        private long LegacyTimelineTickForComparison()
        {
            var source = _legacyObservationSource;
            return source != null ? source.CurrentTick : -1L;
        }

        // ---------------- Shadow 检查点 ----------------

        void IShadowLogicCheckpointSink.OnShadowLogicCheckpoint(ShadowLogicCheckpoint checkpoint)
        {
            _ = checkpoint;
        }

        private void RunShadowCheckpoint()
        {
            var shadow = _adapters.Shadow;
            if (shadow == null) return;

            // 两个世界的权威对齐键都是逻辑 Tick。
            shadow.CaptureCheckpoint(ShadowBattleRunner.DefaultCheckpointName);

            var shadowSnapshots = shadow.SnapshotSequence();
            var legacyObservations = LegacyLogicSideObservations();
            string rulesVersion = shadow.RulesVersionOrEmpty();
            var seed = shadow.Seed;

            var report = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed != null ? seed.BattleDefinitionHash : string.Empty,
                    seed != null && seed.EncounterId.Value != null ? seed.EncounterId.Value : string.Empty,
                    BattleRuntimeMode.Shadow,
                    seed != null ? seed.InputSummary : string.Empty,
                    rulesVersion,
                    BuildComparisonConfig(),
                    _legacyCheckpoints,
                    shadowSnapshots,
                    shadow.EventBindings,
                    legacyObservations,
                    _legacySlotOrder,
                    shadow.LastStepDeficit),
                _diagnosticMigrationEvidence == null ? ShadowCasePolicy.CreateDefault(shadow.CaseId, rulesVersion)
                    : ShadowCasePolicy.ResolveMigrationObligations(shadow.CaseId, rulesVersion,
                        seed.BattleDefinitionHash, _diagnosticMigrationEvidence));

            _lastShadowReport = report;
            _shadowReports.Add(report);
        }

        /// <summary>
        /// 采样 Legacy 侧只读观测（任务 03B 第二收尾轮 R1）。
        ///
        /// 与旧实现（只 <c>Clear()</c> 后返回空列表 ⇒ 每份报告恒 <c>ComparedCheckpoints=0</c>）
        /// 的关键差别：这里从<strong>已分类的 Legacy 事实</strong>采集"当前确实可比较"的字段——
        /// 旧时间线的真实逻辑 Tick、旧场景中<strong>真实活动</strong>的单位数与活动对象路径
        /// （第三收尾轮 R2 的真读），以及每个单位由显式槽位绑定核对过的
        /// 槽位/UnitId/定义/阵营（<strong>定义派生事实</strong>，鉴别力仅限绑定恒等式）；
        /// 不可比较的字段一律留在
        /// <see cref="TemporarilyUncomparableField"/> 登记表里，绝不填默认值冒充"已比较"。
        ///
        /// 只读约束：不推进任何时钟、不写旧状态、不创建对象；观测来源不可用时返回空列表
        /// （比较器据此报告 <c>NO_COMPARABLE_CHECKPOINT</c>，而不是悄悄宣称等价）。
        /// </summary>
        private IReadOnlyList<LegacyLogicObservation> LegacyLogicSideObservations()
        {
            var source = _legacyObservationSource;
            if (source == null) return _legacyObservations;

            var observation = source.Observe(ShadowBattleRunner.DefaultCheckpointName);
            if (observation == null) return _legacyObservations;

            // 同一个逻辑 Tick 只登记一次：旧时间线在某一帧没有推进时（暂停、timeScale=0、
            // 累加器未满一个 Tick）会连续采到同一个 Tick。重复键不是新事实，会让对齐
            // 出现"同 Tick 两份观测"的假未对齐，因此按 Tick 去重——**不**按帧序号伪造新检查点。
            // 只比较最后一个元素：观测按检查点顺序追加，而新世界 Tick 单调不减，重复项必然相邻。
            if (_legacyObservations.Count > 0
                && _legacyObservations[_legacyObservations.Count - 1].Tick == observation.Tick)
                return _legacyObservations;

            _legacyObservations.Add(observation);

            // 比较窗口有界：生产战斗可以跑任意长，观测列表无限增长会让每次比较都因
            // 超出单次预算而被作废（"生产比较退化为不比较"）。超出窗口时裁剪最旧的观测，
            // 并如实记录裁剪数（不丢逻辑 Tick、不改旧时间线、不伪造随后的检查点）。
            while (_legacyObservations.Count > ComparisonWindowSize)
            {
                _legacyObservations.RemoveAt(0);
                DroppedLegacyObservations++;
            }

            return _legacyObservations;
        }

        /// <summary>
        /// 本场战斗的比较窗口（= 单次比较允许覆盖的最大检查点数）。
        ///
        /// 取值上限还受 <see cref="_shadowMaxCheckpoints"/> 约束：两者中较小者决定窗口，
        /// 因此"单场最多持有的检查点"这一序列化配置在比较侧同样生效。
        /// </summary>
        private int ComparisonWindowSize
        {
            get
            {
                int steps = _shadowMaxStepsPerComparison > 0
                    ? _shadowMaxStepsPerComparison : ShadowComparisonConfig.Default().MaxStepsPerComparison;
                int checkpoints = _shadowMaxCheckpoints > 0 ? _shadowMaxCheckpoints : int.MaxValue;
                return steps < checkpoints ? steps : checkpoints;
            }
        }

        /// <summary>
        /// 定义侧槽位顺序（<c>SlotId</c> Ordinal 升序 ⇒ <c>UnitId(1..N)</c>，与
        /// <c>BattleInitializer</c> 的唯一权威分配一致）。比较器用它独立解析新侧槽位 ID，
        /// 因此旧侧把单位绑错槽位仍会被发现。
        /// </summary>
        private void BuildLegacySlotOrder(BattleSimulationSeed seed)
        {
            _legacySlotOrder.Clear();
            if (seed == null || seed.Definition == null) return;

            var encounter = seed.Definition.FindEncounter(seed.EncounterId);
            if (encounter == null || encounter.Slots == null) return;

            var ordered = Logic.Encounter.EncounterSlotOrdering.OrderBySlotIdOrdinal(encounter.Slots);
            for (int i = 0; i < ordered.Count; i++)
            {
                var slot = ordered[i];
                if (slot == null) continue;
                _legacySlotOrder.Add(new LegacySlotOrderEntry(slot.SlotId.Value, i + 1L));
            }
        }

        private ShadowComparisonConfig BuildComparisonConfig()
            => new ShadowComparisonConfig(
                _shadowComparisonConfigVersion, _shadowMaxStepsPerComparison,
                _shadowMaxCheckpoints, _shadowComparisonMode);

        // ---------------- 适配器与写入者收集 ----------------

        private FrameAdapterSet FindAdaptersInScene()
        {
            var set = new FrameAdapterSet();
            set.SetNewDriver(_newDriver);

            var legacy = _legacyFrameAdapterSlot as IBattleFrameAdapter;
            if (legacy == null && _legacyFrameAdapterSlot != null)
                throw new LogicDefinitionException(
                    BOOTSTRAP_ADAPTER_SLOT_INVALID,
                    "_legacyFrameAdapterSlot=" + _legacyFrameAdapterSlot.GetType().FullName);
            set.SetLegacy(legacy);
            return set;
        }

        private IBattleSimulationSource ResolveSimulationSource(FrameAdapterSet set)
        {
            _ = set;
            var source = _simulationSourceSlot as IBattleSimulationSource;
            if (source == null && _simulationSourceSlot != null)
                throw new LogicDefinitionException(
                    BOOTSTRAP_ADAPTER_SLOT_INVALID,
                    "_simulationSourceSlot=" + _simulationSourceSlot.GetType().FullName);
            return source;
        }

        private ShadowBattleRunner EnsureShadowRunner()
        {
            var existing = _adapters.Shadow;
            if (existing != null)
            {
                // 幂等：真实请求镜像与权威输入记录器必须在**每场**战斗上都在位
                // （重用同一 runner 重开一局时，上一场的镜像游标不能继续生效）。
                RebindShadowAuthority(existing);
                return existing;
            }

            var runner = new ShadowBattleRunner();
            runner.Sink = this;
            runner.ConfigureCheckpoints(BuildComparisonConfig());
            RebindShadowAuthority(runner);
            _adapters.SetShadow(runner);
            return runner;
        }

        /// <summary>
        /// 把「回放权威输入记录器」与「真实请求镜像」接到 Shadow runner 上（产出 16 的生产装配点）。
        ///
        /// 两者是同一份事实的产出侧与消费侧，必须指向<strong>同一批对象</strong>：
        /// 记录器（<see cref="ReplayAuthorityInput"/>）在唯一模拟入口内收口，
        /// 镜像（<see cref="ShadowPlayerRequestMirror"/>）只从它的
        /// <c>AuthorityCommands</c> 取证 —— 因此镜像面在类型上就取不到 AI/System 载荷。
        /// </summary>
        private void RebindShadowAuthority(ShadowBattleRunner runner)
        {
            runner.ConfigureAuthorityInput(_shadowAuthorityInput);
            runner.AttachMirror(new ShadowPlayerRequestMirror(_shadowAuthorityInput));
        }

        private void CollectLegacyWriters()
        {
            _legacyWriters.Clear();

            foreach (var classification in RuntimeCallbackRegistry.All)
            {
                if (classification.CallbackClass != UnityCallbackClass.LegacyDependentWriter) continue;

                // 帧相位写入者（Update/LateUpdate）参与 enabled 门控；
                // 协程站点（R4.2 登记）没有可门控的 MonoBehaviour 帧回调，
                // 因此不进入门控登记表——它们在 New 模式下的门控由任务 04/10 迁移时处理，
                // 分类表仍然完整记录它们（禁止事项第 2 条：不得当成表现回调而漏登记）。
                if (classification.Phase == UnityCallbackPhase.Coroutine) continue;
                var gateTarget = ResolveGateTarget(classification.TypeName);
                if (_productionNewOnly && gateTarget == null) continue;
                RegisterLegacyWriter(
                    classification.Site, classification.SceneObjectPath, classification.TimeSource,
                    gateTarget);
            }
        }

        /// <summary>
        /// 解析 Legacy 从属写入者的运行时实例（用于 `enabled` 门控）。
        ///
        /// 契约程序集不能引用 <c>Assembly-CSharp</c> 类型，因此这里用类型名解析；
        /// 解析失败不报错，只在 <see cref="LegacyWriterRegistration.HasGateTarget"/> 上体现，
        /// 由测试的"已启用实例计数"证据判断门控是否真的命中运行时。
        /// </summary>
        private static MonoBehaviour ResolveGateTarget(string typeName)
        {
            var type = RuntimeCallbackRegistry.ResolveType(typeName);
            if (type == null || !typeof(MonoBehaviour).IsAssignableFrom(type)) return null;

            var components = UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include);
            return components != null && components.Length > 0 ? components[0] as MonoBehaviour : null;
        }

        private void RegisterLegacyWriter(
            string callbackSite, string sceneObjectPath, string timeSource, MonoBehaviour gateTarget)
        {
            var registration = new LegacyWriterRegistration(callbackSite, sceneObjectPath, timeSource, gateTarget);
            registration.BaselineEnabled = true;
            _legacyWriters.Add(registration);
        }

        /// <summary>
        /// 应用 Legacy 从属写入组门控：Legacy/Shadow 启用基线集合，New 全部禁用。
        /// </summary>
        public void ApplyLegacyWriterGroupGate(BattleRuntimeMode mode)
        {
            SyncRuntimeLegacyWritersInternal();

            bool enable = BattleRuntimeModes.EnablesLegacyWriterGroup(mode);
            for (int i = 0; i < _legacyWriters.Count; i++)
            {
                var registration = _legacyWriters[i];
                registration.IsEnabled = enable;
                if (registration.GateTarget != null) registration.GateTarget.enabled = enable;
            }

            // 运行时创建的写入者（TimelineEditorUI）与登记表引用表使用同一门控决策。
            LegacyWriterRegistry.ApplyGate(enable);
            Ledger.RecordLegacyWriterGroupApplied(enable, _legacyWriters);
        }

        /// <summary>
        /// 把登记表中的运行时写入者实例并入 Legacy 从属写入组的门控面。
        /// 由测试或工具在运行时创建写入者之后调用；幂等。
        /// </summary>
        public int SyncRuntimeLegacyWriters()
        {
            int added = SyncRuntimeLegacyWritersInternal();
            if (added > 0 && _battleMode.HasValue)
                ApplyLegacyWriterGroupGate(_battleMode.Value);
            return added;
        }

        private int SyncRuntimeLegacyWritersInternal()
        {
            int added = 0;
            var writers = LegacyWriterRegistry.RegisteredWriters;
            for (int i = 0; i < writers.Count; i++)
            {
                var writer = writers[i];
                if (writer == null) continue;

                bool known = false;
                for (int j = 0; j < _legacyWriters.Count; j++)
                {
                    if (ReferenceEquals(_legacyWriters[j].GateTarget, writer)) { known = true; break; }
                }
                if (known) continue;

                var gateTarget = writer as MonoBehaviour;
                RegisterLegacyWriter(
                    writer.GetType().FullName + ".Update",
                    DescribePath(writer),
                    "每帧（运行时创建的 Legacy 从属写入者）",
                    gateTarget);
                added++;
            }

            return added;
        }

        /// <summary>当前注册表中是否有写入者处于启用状态。</summary>
        public bool AnyLegacyWriterEnabled()
        {
            for (int i = 0; i < _legacyWriters.Count; i++)
            {
                if (_legacyWriters[i].IsEnabled) return true;
            }
            return false;
        }

        /// <summary>已解析到运行时实例的写入者数量（门控真正命中的数量）。</summary>
        public int LegacyWritersWithGateTarget
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _legacyWriters.Count; i++)
                {
                    if (_legacyWriters[i].HasGateTarget) count++;
                }
                return count;
            }
        }

        /// <summary>未解析到运行时实例的写入者数量（诊断；不应掩盖门控缺口）。</summary>
        public int LegacyWritersWithoutGateTarget => _legacyWriters.Count - LegacyWritersWithGateTarget;

        /// <summary>当前场景中处于 <c>enabled = true</c> 状态的已解析写入者实例数。</summary>
        public int EnabledLegacyWriterInstances
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _legacyWriters.Count; i++)
                {
                    var target = _legacyWriters[i].GateTarget;
                    if (target != null && target.enabled) count++;
                }
                return count;
            }
        }

        // ---------------- 启动前校验细节 ----------------

        private static string DescribeAutonomousAdapters(FrameAdapterSet set)
        {
            var builder = new StringBuilder();
            var adapters = set.All;
            for (int i = 0; i < adapters.Count; i++)
            {
                var adapter = adapters[i];
                if (adapter == null || !adapter.OwnsAutonomousUpdate) continue;
                if (builder.Length > 0) builder.Append(';');
                builder.Append(adapter.AdapterName).Append('@').Append(adapter.CallbackSite);
            }
            return builder.Length == 0 ? null : builder.ToString();
        }

        /// <summary>
        /// 未分类逻辑写入者清单（空 = 无）。
        ///
        /// 判定口径（规格语义：<b>分类表是唯一权威</b>）：
        /// <list type="bullet">
        /// <item>已登记在 <see cref="LegacyWriterRegistration"/> 里的回调点，只要在
        /// <see cref="RuntimeCallbackRegistry"/> 中<b>有</b>分类记录，就是"已分类的 Legacy
        /// 从属写入者"——它们在 New 模式下被整体禁用，<b>不</b>阻止 New 启动。</item>
        /// <item>登记了却<b>没有</b>分类记录、且不是引擎/框架类型（
        /// <see cref="RuntimeCallbackRegistry.IsFrameworkWriter"/>）的写入者，
        /// 才是"未分类的逻辑写入者"，必须阻止 New 启动并给出<b>对象路径</b>。</item>
        /// <item>类型无法解析（已卸载程序集/编辑器专用类型）不参与门禁，避免把
        /// "解析不到"误判成"未分类"。</item>
        /// </list>
        ///
        /// 修复记录：此前版本要求 <c>UnclassifiedLogicWriterTypes</c> 非空才继续判定，
        /// 使"登记了但从未被显式标记"的未分类写入者（例如测试探针、后续任务新增却忘记
        /// 更新分类表的组件）静默通过门禁。现在改为<b>始终</b>按分类表逐条核对。
        /// </summary>
        private static string DescribeUnclassifiedLogicWriters()
        {
            var reports = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var instances = EnumerateSceneInstances();
            for (int i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                if (instance == null) continue;

                for (int j = 0; j < instance._legacyWriters.Count; j++)
                {
                    var registration = instance._legacyWriters[j];
                    string site = registration.CallbackSite;
                    int dot = site.LastIndexOf('.');
                    if (dot <= 0)
                    {
                        AddUnclassifiedReport(reports, seen, "<invalid-site>|" + site
                            + "|path=" + ObjectPathOf(registration));
                        continue;
                    }

                    string typeName = site.Substring(0, dot);
                    string member = site.Substring(dot + 1);

                    UnityCallbackClassification classification;
                    if (RuntimeCallbackRegistry.TryGet(typeName, member, out classification)) continue;

                    var writerType = RuntimeCallbackRegistry.ResolveType(typeName);
                    if (writerType == null) continue;
                    if (RuntimeCallbackRegistry.IsFrameworkWriter(writerType)) continue;

                    AddUnclassifiedReport(reports, seen,
                        site + "|path=" + ObjectPathOf(registration));
                }
            }

            // 显式标记：运行时写入者注册表在 New 模式拒绝登记的未分类类型/回调点。
            foreach (var marked in UnclassifiedLogicWriterTypes)
            {
                AddUnclassifiedReport(reports, seen, marked);
            }

            if (reports.Count == 0) return null;

            reports.Sort(StringComparer.Ordinal);
            return string.Join(";", reports);
        }

        private static void AddUnclassifiedReport(List<string> reports, HashSet<string> seen, string report)
        {
            if (string.IsNullOrEmpty(report)) return;
            if (!seen.Add(report)) return;
            reports.Add(report);
        }

        /// <summary>登记项的运行时对象路径（优先取真实门控实例，退回登记时记录的路径）。</summary>
        private static string ObjectPathOf(LegacyWriterRegistration registration)
        {
            if (registration == null) return "<null-registration>";
            if (registration.GateTarget != null) return DescribePath(registration.GateTarget);
            return string.IsNullOrEmpty(registration.SceneObjectPath)
                ? "<unresolved-instance>" : registration.SceneObjectPath;
        }

        /// <summary>
        /// 重复实例冲突的可读描述：实例总数、所有者对象路径、本实例对象路径，
        /// 以及<b>每一个</b>实例的对象路径（含 inactive 对象上的实例）。
        /// 拒绝码必须能让接手者直接定位到对象，而不是只知道"重复了"。
        /// </summary>
        private string DescribeInstanceCollision()
        {
            var instances = EnumerateSceneInstances();
            var owner = FirstClockOwner();
            var builder = new StringBuilder();
            builder.Append("sceneInstances=").Append(instances.Count);
            builder.Append("|owner=").Append(owner != null ? DescribePath(owner) : "<none>");
            builder.Append("|this=").Append(DescribePath(this));
            for (int i = 0; i < instances.Count; i++)
            {
                builder.Append("|path=").Append(DescribePath(instances[i]));
                if (!instances[i].enabled) builder.Append("(disabled)");
                if (!instances[i].gameObject.activeInHierarchy) builder.Append("(inactive)");
            }
            return builder.ToString();
        }

        /// <summary>对象路径（<c>Scene/Root/Child</c>），失败时退回家族名。</summary>
        public static string DescribePath(Component component)
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
            var scene = component.gameObject.scene;
            return scene.IsValid() ? scene.name + "/" + path : path;
        }

        // ---------------- 只读检查点执行顺序证据 ----------------

        /// <summary>
        /// 返回"脚本执行顺序不早于只读检查点"的已登记 Legacy 写入者。
        /// 空列表 = 检查点确实晚于全部已登记 Legacy 写入者。
        ///
        /// <strong>第二收尾轮 R4.1 更正</strong>：上一轮本方法只比较分类表内的
        /// <c>ScriptExecutionOrder</c> 常量与 <c>CheckpointExecutionOrder(10000)</c>，
        /// 除非有人改表否则<strong>不可能失败</strong>（分类表自证）。
        /// 现在改成两条都可以失败的判据：
        /// <list type="number">
        /// <item><b>相位违规</b>：检查点在 <c>LateUpdate</c>，因此任何处在
        /// <c>LateUpdate</c> 或协程相位的已登记写入者都是真实违规
        /// （协程由帧末/计时器驱动，可能在检查点之后写 <c>Time.timeScale</c>）。</item>
        /// <item><b>顺序位违规</b>：登记的顺序位不早于 <c>CheckpointExecutionOrder</c> 的声明位。</item>
        /// </list>
        /// 真实的"全部写入者都在检查点之前"保证来自相位边界（全部写入者都在 <c>Update</c>）+
        /// <see cref="WriterOrderMismatches"/> 的真实执行顺序读取。
        /// </summary>
        public IReadOnlyList<string> WritersExecutingAtOrAfterCheckpoint()
        {
            var violations = new List<string>();
            int checkpointOrder = RuntimeCallbackRegistry.CheckpointExecutionOrder;

            for (int i = 0; i < _legacyWriters.Count; i++)
            {
                var registration = _legacyWriters[i];
                UnityCallbackClassification classification;
                if (!TryClassifySite(registration.CallbackSite, out classification)) continue;

                if (classification.Phase != UnityCallbackPhase.UpdatePhase)
                {
                    violations.Add(registration.CallbackSite + "|phase=" + classification.Phase
                        + "|检查点在 LateUpdate 相位，该写入者不在 Update 相位");
                    continue;
                }

                if (classification.ScriptExecutionOrder >= checkpointOrder)
                    violations.Add(registration.CallbackSite + "@" + classification.ScriptExecutionOrder);
            }

            return violations;
        }

        /// <summary>
        /// 处在 <c>LateUpdate</c> 或协程相位的已登记 Legacy 写入者（空 = 检查点的相位边界成立）。
        ///
        /// 这是 R4.1 要求的"真实相位边界证据"：一旦某个 Legacy 写入者改用 <c>LateUpdate</c>
        /// 或经协程写入，本方法立刻报警——而旧的常量比较不会。
        /// </summary>
        public IReadOnlyList<string> WritersExecutingInLateUpdatePhase()
        {
            var violations = new List<string>();
            for (int i = 0; i < _legacyWriters.Count; i++)
            {
                var registration = _legacyWriters[i];
                UnityCallbackClassification classification;
                if (!TryClassifySite(registration.CallbackSite, out classification)) continue;
                if (classification.Phase == UnityCallbackPhase.UpdatePhase) continue;
                violations.Add(registration.CallbackSite + "|phase=" + classification.Phase);
            }
            return violations;
        }

        /// <summary>
        /// 分类表中<strong>全部</strong> Legacy 从属写入者的相位清单（含不参与 enabled 门控的协程站点）。
        /// 用于交接与测试逐条核对"检查点相位边界 vs 全部写入者相位"。
        /// </summary>
        public static IReadOnlyList<string> ClassifiedLegacyWriterPhases()
        {
            var phases = new List<string>();
            foreach (var classification in RuntimeCallbackRegistry.All)
            {
                if (classification.CallbackClass != UnityCallbackClass.LegacyDependentWriter) continue;
                phases.Add(classification.Site + "|" + classification.Phase);
            }
            phases.Sort(StringComparer.Ordinal);
            return phases;
        }

        /// <summary>把 <c>Type.Member</c> 回调点解析回分类记录。</summary>
        private static bool TryClassifySite(
            string callbackSite, out UnityCallbackClassification classification)
        {
            classification = null;
            if (string.IsNullOrEmpty(callbackSite)) return false;
            int dot = callbackSite.LastIndexOf('.');
            if (dot <= 0) return false;
            return RuntimeCallbackRegistry.TryGet(
                callbackSite.Substring(0, dot), callbackSite.Substring(dot + 1), out classification);
        }

        /// <summary>
        /// 返回"分类顺序宣称晚于检查点、但与该组件真实执行顺序不一致"的登记项。
        /// 这是防止脚本执行顺序被事后篡改的证据。
        /// </summary>
        public IReadOnlyList<string> WriterOrderMismatches()
        {
            var mismatches = new List<string>();
            for (int i = 0; i < _legacyWriters.Count; i++)
            {
                var registration = _legacyWriters[i];
                if (registration.GateTarget == null) continue;

                int dot = registration.CallbackSite.LastIndexOf('.');
                if (dot <= 0) continue;
                string typeName = registration.CallbackSite.Substring(0, dot);
                string member = registration.CallbackSite.Substring(dot + 1);

                UnityCallbackClassification classification;
                if (!RuntimeCallbackRegistry.TryGet(typeName, member, out classification)) continue;

                var components = registration.GateTarget.GetComponents<MonoBehaviour>();
                for (int c = 0; c < components.Length; c++)
                {
                    var component = components[c];
                    if (component == null) continue;
                    if (component.GetType().FullName != typeName) continue;

                    int actual = GetScriptExecutionOrder(component);
                    if (actual != classification.ScriptExecutionOrder)
                    {
                        mismatches.Add(registration.CallbackSite
                            + "|expected=" + classification.ScriptExecutionOrder
                            + "|actual=" + actual);
                    }
                }
            }

            return mismatches;
        }

        /// <summary>读取组件所属 MonoScript 的显式执行顺序（Editor 下有效；运行时退化为 0）。</summary>
        private static int GetScriptExecutionOrder(MonoBehaviour component)
        {
#if UNITY_EDITOR
            var monoScript = UnityEditor.MonoScript.FromMonoBehaviour(component);
            if (monoScript != null) return UnityEditor.MonoImporter.GetExecutionOrder(monoScript);
#endif
            return 0;
        }

        /// <summary>本场景/全项目的启动校验清单（诊断输出；不改变任何状态）。</summary>
        public string DescribeOwnership()
        {
            var builder = new StringBuilder();
            builder.Append("bootstrap=").Append(DescribePath(this));
            builder.Append(" instances=").Append(LiveInstances.Count);
            builder.Append(" owns=").Append(OwnsTopLevelClock);
            builder.Append(" mode=").Append(_battleMode.HasValue
                ? BattleRuntimeModes.Describe(_battleMode.Value) : "<not-created>");
            builder.Append(" stopped=").Append(_stopped);
            builder.Append(" rejection=").Append(_startupRejection ?? "<none>");
            builder.Append(" | ").Append(Ledger.Describe());
            builder.Append(" | ").Append(ShadowWrites.Describe());
            builder.Append(" | shadowAdvance target=").Append(_adapters.Shadow != null
                    ? _adapters.Shadow.LastAdvanceTargetTick.ToString(CultureInfo.InvariantCulture) : "<none>")
                .Append(" before=").Append(_adapters.Shadow != null
                    ? _adapters.Shadow.LastAdvanceTickBefore.ToString(CultureInfo.InvariantCulture) : "<none>")
                .Append(" ticks=").Append(_adapters.Shadow != null
                    ? _adapters.Shadow.LastAdvanceTickCount.ToString(CultureInfo.InvariantCulture) : "<none>")
                .Append(" deficit=").Append(_adapters.Shadow != null
                    ? _adapters.Shadow.LastStepDeficit.ToString(CultureInfo.InvariantCulture) : "<none>")
                .Append(" legacyTick=").Append(LegacyTimelineTickForComparison()
                    .ToString(CultureInfo.InvariantCulture))
                .Append(" legacyObservations=").Append(_legacyObservations.Count
                    .ToString(CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }

    /// <summary>
    /// Legacy 适配器可选的暂停观测面（用于把"暂停语义"变成可观察事实）。
    /// </summary>
    public interface ILegacyPauseAware
    {
        bool IsPaused { get; }
    }

    /// <summary>
    /// Legacy 适配器可选的旧顶层推进计数观测面：Adapter 必须回传**旧组件自己的**
    /// 累计 <c>AdvanceTime</c> 调用数，从而让"New 模式旧推进为 0"可被直接证明。
    /// </summary>
    public interface ILegacyAdvanceTimeObservable
    {
        int AdvanceTimeCallCount { get; }
    }

    /// <summary>Shadow 辅助扩展。</summary>
    internal static class ShadowRunnerExtensions
    {
        public static string RulesVersionOrEmpty(this ShadowBattleRunner runner)
            => runner != null && runner.Seed != null ? runner.Seed.RulesVersion : string.Empty;
    }
}
