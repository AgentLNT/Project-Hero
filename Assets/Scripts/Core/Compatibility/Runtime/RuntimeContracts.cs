using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Runtime
{    /// <summary>
    /// 帧适配器契约（任务 03B「必须产出」3/4/5）。
    ///
    /// <strong>本接口不得依赖任何 Legacy 具体类型</strong>：它只认识纯数据、计数账本与
    /// 自身的生命周期。Legacy 具体适配器（<c>CombatDemo</c>）留在预定义
    /// <c>Assembly-CSharp</c> 中实现本接口，并由 Bootstrap 通过显式 <c>MonoBehaviour</c>
    /// 槽位校验后注入；任务 10 创建 UnityView 后，本文件连同 Bootstrap 一起保留 <c>.meta</c>
    /// 搬入 <c>Assets/Scripts/UnityView/</c>，Legacy 适配器继续留在旧程序集。
    /// </summary>
    public interface IBattleFrameAdapter
    {
        /// <summary>适配器名称，用于诊断输出（稳定、非本地化）。</summary>
        string AdapterName { get; }

        /// <summary>
        /// 该适配器所属的 Unity 回调点（<c>Type.Member</c>）。
        /// Bootstrap 用它验证"没有适配器仍启用自主推进"，并与分类表交叉核对。
        /// </summary>
        string CallbackSite { get; }

        /// <summary>适配器是否仍在自主推进顶层时钟（不得为 true；启动时校验）。</summary>
        bool OwnsAutonomousUpdate { get; }

        /// <summary>适配器的门控对象（Legacy 从属写入组门控用）。</summary>
        MonoBehaviour GateTarget { get; }

        /// <summary>该适配器在本次战斗中是否已初始化。</summary>
        bool IsInitialized { get; }

        /// <summary>该适配器在本次战斗中是否已停止（停止幂等）。</summary>
        bool IsStopped { get; }

        /// <summary>累计被 Bootstrap 调用的推进次数。</summary>
        int AdvanceCallCount { get; }

        /// <summary>累计停止次数（必须恰为 1）。</summary>
        int StopCallCount { get; }

        /// <summary>创建本场战斗之前固定的一次性初始化。</summary>
        void Initialize(BattleRuntimeContext context);

        /// <summary>
        /// 推进一帧。只允许 Bootstrap 调用；Delta 必须由 Bootstrap 显式传入，
        /// 适配器自身不得读取 Unity 帧时间或形成第二个时钟入口。
        /// </summary>
        void AdvanceFrame(BattleFrameDelta delta);

        /// <summary>停止本场战斗（幂等）；Bootstrap 负责暂停与销毁边界。</summary>
        void StopBattle(string reason);

        /// <summary>
        /// 为新一场战斗复位适配器（幂等）。
        ///
        /// 为什么必须显式复位：同一 Bootstrap 可以在 <c>StopBattle</c> + <c>ReleaseBattle</c>
        /// 之后以<b>另一模式</b>重新开局（可回切语义）。适配器是场景组件或长期持有的对象，
        /// "上一场已停止"的状态会残留；不复位就会出现"新战斗开局即停止"的假死
        /// （<c>Adapters.Legacy.IsStopped == true</c>）。
        /// 本方法只清理由本接口约定的每次战斗状态，不得销毁场景对象、不得重置旧权威
        /// （<c>BattleTimeline</c>）的累计计数。
        /// </summary>
        void ResetForNewBattle();
    }

    /// <summary>
    /// Bootstrap 交给适配器的只读运行上下文。
    /// 只含模式、调用账本与纯数据来源，<strong>不含 Legacy 类型</strong>。
    /// </summary>
    public sealed class BattleRuntimeContext
    {
        public BattleRuntimeContext(
            BattleRuntimeMode mode,
            RuntimeCallLedger ledger,
            ShadowWriteCounters shadowWrites,
            IBattleSimulationSource simulationSource)
        {
            Mode = mode;
            Ledger = ledger;
            ShadowWrites = shadowWrites;
            SimulationSource = simulationSource;
        }

        /// <summary>本场战斗创建之前已固定的模式。</summary>
        public BattleRuntimeMode Mode { get; }

        /// <summary>可观察调用计数账本（只记录事实，不参与调度）。</summary>
        public RuntimeCallLedger Ledger { get; }

        /// <summary>Shadow 新模拟写入计数器（只有 Shadow runner 会写入，且必须保持全 0）。</summary>
        public ShadowWriteCounters ShadowWrites { get; }

        /// <summary>纯数据战斗来源；Legacy 模式可以为 null。</summary>
        public IBattleSimulationSource SimulationSource { get; }
    }

    /// <summary>
    /// 一帧的显式时间输入。
    ///
    /// <see cref="DeltaTime"/> 是 Bootstrap 从 Unity 帧时间采样的结果；
    /// <see cref="IsPaused"/> 是当前顶层暂停状态。适配器<strong>不得</strong>自行读取
    /// <c>Time.deltaTime</c>——否则会产生第二个时间来源。
    /// </summary>
    public readonly struct BattleFrameDelta
    {
        public BattleFrameDelta(float deltaTime, bool isPaused, bool timeScaleIsolated)
        {
            DeltaTime = deltaTime;
            IsPaused = isPaused;
            TimeScaleIsolated = timeScaleIsolated;
        }

        /// <summary>Bootstrap 采样的帧时间。</summary>
        public float DeltaTime { get; }

        /// <summary>顶层暂停状态（Legacy 语义：暂停时不推进）。</summary>
        public bool IsPaused { get; }

        /// <summary>帧时间是否来自 <c>Time.unscaledDeltaTime</c>（New 段为 true）。</summary>
        public bool TimeScaleIsolated { get; }

        public override string ToString()
            => "dt=" + DeltaTime.ToString("F6") + " paused=" + IsPaused + " unscaled=" + TimeScaleIsolated;
    }

    /// <summary>
    /// 纯数据战斗来源：由 Bootstrap 的显式序列化槽位提供。
    ///
    /// 定义与初始化每次调用都重新构建（<strong>构造函数只被调用一次</strong>，
    /// 结果由 Bootstrap 缓存），因此"新模拟覆盖全部规则常量"而不需要 Bootstrap 依赖
    /// <c>ProjectHero.Authoring</c> 或任何 Legacy 具体类型。
    /// </summary>
    public interface IBattleSimulationSource
    {
        /// <summary>来源名称（诊断用）。</summary>
        string SourceName { get; }

        /// <summary>最近一次配置构建的稳定错误（非空表示来源不可用）。</summary>
        string LastConfigurationError { get; }

        /// <summary>创建本场战斗的纯数据定义与初始化结果。</summary>
        BattleSimulationSeed BuildSeed();
    }

    /// <summary>
    /// <see cref="IBattleSimulationSource"/> 的纯数据产物。全部成员都是任务 02B/03 的公开只读类型。
    /// </summary>
    public sealed class BattleSimulationSeed
    {
        public BattleSimulationSeed(
            Logic.Definitions.BattleDefinition definition,
            Logic.Ids.EncounterDefinitionId encounterId,
            Logic.Initialization.BattleRuntimeInputs runtimeInputs,
            string inputSummary)
        {
            Definition = definition;
            EncounterId = encounterId;
            RuntimeInputs = runtimeInputs;
            InputSummary = inputSummary ?? string.Empty;
        }

        public Logic.Definitions.BattleDefinition Definition { get; }

        public Logic.Ids.EncounterDefinitionId EncounterId { get; }

        public Logic.Initialization.BattleRuntimeInputs RuntimeInputs { get; }

        /// <summary>输入摘要（进入 Shadow 报告的"输入摘要"字段，稳定可复现）。</summary>
        public string InputSummary { get; }

        public string BattleDefinitionHash => Definition != null ? Definition.BattleDefinitionHashValue : string.Empty;

        public string RulesVersion => Definition != null ? Definition.RulesVersion : string.Empty;

        /// <summary>构建失败原因；null 表示可用。</summary>
        public string Validate()
        {
            if (Definition == null) return "SIMULATION_SOURCE_MISSING_DEFINITION";
            if (RuntimeInputs == null) return "SIMULATION_SOURCE_MISSING_RUNTIME_INPUTS";
            return null;
        }
    }

    /// <summary>
    /// Legacy 侧<strong>单个单位</strong>的只读观测事实（任务 03B 第二收尾轮 D1/R1；
    /// 任务 04 起扩展了实时活动状态）。
    ///
    /// 每一条都满足同一个门槛：<strong>旧侧与新侧都能从各自世界的真实事实独立推出</strong>，
    /// 且不依赖任何只在其中一侧存在的系统。因此它可以被字段级比较，而不是被声明为
    /// "暂不可比较"或被默认值填充成"已比较"。
    ///
    /// <strong>字段来源（任务 04 更新后的逐字段口径）</strong>：
    /// <list type="bullet">
    /// <item><strong>定义派生事实</strong>：<see cref="SlotId"/> / <see cref="UnitId"/> /
    /// <see cref="DefinitionId"/> / <see cref="FactionId"/> 由 Encounter 定义槽位表
    /// （<c>slot.SlotId</c> / <c>slot.DefinitionId</c> / <c>slot.FactionId</c>）按唯一权威顺序推出，
    /// 不是旧场景活动单位的状态。鉴别力<strong>仅限"显式槽位绑定恒等式"</strong>：
    /// 生产观测实现强制"活动单位集合 == 显式绑定集合 == 定义槽位集合"，
    /// 绑定被改坏时<strong>整个观测失败</strong>（比较器报 <c>NO_COMPARABLE_CHECKPOINT</c>）。</item>
    /// <item><strong>旧场景活动单位的实时状态（任务 04 新增，真读）</strong>：
    /// <see cref="CurrentHealthLive"/>（<c>CombatUnit.CurrentHealth</c>，浮点）、
    /// <see cref="GridPositionLive"/> / <see cref="GridPositionLiveY"/>（<c>CombatUnit.GridPosition</c>）、
    /// <see cref="FacingLive"/>（<c>CombatUnit.FacingDirection</c>）、
    /// <see cref="LegacyStateFlagsLive"/>（由 <c>CombatUnit</c> 的旧控制/状态 bool 推导的
    /// <strong>状态族标签</strong>）。它们由 <c>Assembly-CSharp</c> 的生产观测组件从
    /// <strong>当前活动</strong>的 <c>CombatUnit</c> 实例读取（<c>FindObjectsInactive.Exclude</c>），
    /// 非活动实例的旧状态不进入任何比较。</item>
    /// <item><see cref="LegacyObjectPath"/> 是诊断字段，不参与比较。</item>
    /// </list>
    ///
    /// 反例（因此<strong>不</strong>在这里出现）：持续效果计数、旧排程条目计数、RNG 状态——
    /// 旧侧这些结构与新内核不是同一结构，计数相等是巧合而不是事实，
    /// 必须留在 <see cref="TemporarilyUncomparableField"/> 登记表里，
    /// 带对象路径/原因/负责迁移任务/清零门槛。
    /// </summary>
    public sealed class LegacyLogicUnitObservation
    {
        /// <summary>旧侧状态族标签的稳定取值（与 <c>UnitState</c> 的数值一致）。</summary>
        public static class StateLabels
        {
            public const int Unknown = -1;
            public const int Dead = 10;
            public const int Staggered = 7;
            public const int KnockedDown = 8;
            public const int Windup = 1;
            public const int Recovery = 2;
            public const int Moving = 3;
            public const int Guarding = 4;
            public const int Blocking = 5;
            public const int Dodging = 6;
            public const int Idle = 0;
        }

        public LegacyLogicUnitObservation(
            string slotId, long unitId, string definitionId, string factionId, string legacyObjectPath,
            float currentHealthLive = float.NaN,
            int gridPositionLiveX = 0,
            int gridPositionLiveY = 0,
            int facingLive = 0,
            int legacyStateFlagsLive = StateLabels.Unknown)
        {
            SlotId = slotId ?? string.Empty;
            UnitId = unitId;
            DefinitionId = definitionId ?? string.Empty;
            FactionId = factionId ?? string.Empty;
            LegacyObjectPath = legacyObjectPath ?? string.Empty;
            CurrentHealthLive = currentHealthLive;
            GridPositionLiveX = gridPositionLiveX;
            GridPositionLiveY = gridPositionLiveY;
            FacingLive = facingLive;
            LegacyStateFlagsLive = legacyStateFlagsLive;
        }

        /// <summary>
        /// Encounter 槽位 ID（旧侧稳定键；<strong>定义派生</strong>，见类型注释的字段来源说明）。
        /// </summary>
        public string SlotId { get; }

        /// <summary>该槽位在唯一权威顺序（<c>SlotId</c> Ordinal 升序）下的 <c>UnitId</c>（定义派生）。</summary>
        public long UnitId { get; }

        /// <summary>槽位定义的单位定义 ID（定义派生）。</summary>
        public string DefinitionId { get; }

        /// <summary>槽位携带的阵营 ID（定义派生；创建后不可变）。</summary>
        public string FactionId { get; }

        /// <summary>对应的旧场景对象路径（诊断与报告用，不参与比较）。</summary>
        public string LegacyObjectPath { get; }

        /// <summary>
        /// 旧场景活动单位的当前生命（<c>CombatUnit.CurrentHealth</c>，<strong>浮点原值</strong>）。
        ///
        /// 比较时按 <c>BattleSimulation.QuantizeHealth</c> 的<strong>同一约定</strong>量化成
        /// Q10 整数再与 <c>UnitSnapshot.HealthQ10</c> 比较——量化公式是唯一权威，
        /// 不在这里另写一份（否则"两侧一致"会退化成实现副本之间的自证）。
        /// 不可采样时为 <c>float.NaN</c>（比较器据此报"不可采样"，不当成 0）。
        /// </summary>
        public float CurrentHealthLive { get; }

        /// <summary>旧场景活动单位的格坐标 X（<c>CombatUnit.GridPosition.X</c>，真读）。</summary>
        public int GridPositionLiveX { get; }

        /// <summary>旧场景活动单位的格坐标 Y（<c>CombatUnit.GridPosition.Y</c>，真读）。</summary>
        public int GridPositionLiveY { get; }

        /// <summary>旧场景活动单位的朝向（<c>CombatUnit.FacingDirection</c>，真读；值域与 <c>GridDirection</c> 一致）。</summary>
        public int FacingLive { get; }

        /// <summary>
        /// 旧侧<strong>状态族标签</strong>（任务 04 新增，真读旧 bool 后按冻结规则推导）。
        ///
        /// 推导是<strong>单向且穷尽</strong>的（旧侧没有等价的状态机）：
        /// 生命 &lt;= 0 → <c>Dead</c>；<c>IsKnockedDown</c> → <c>KnockedDown</c>；
        /// <c>IsStaggered</c> → <c>Staggered</c>；<c>InWindup</c> → <c>Windup</c>；
        /// <c>InRecovery</c> → <c>Recovery</c>；<c>IsMoving</c> → <c>Moving</c>；否则 <c>Idle</c>。
        ///
        /// <strong>覆盖边界（如实披露）</strong>：旧侧没有 <c>Guarding</c>/<c>Blocking</c>/<c>Dodging</c>
        /// 的等价事实（这三个状态在新内核里是任务 05/08 的计划状态），因此本标签
        /// 只在这三种状态出现时会与 <c>UnitSnapshot.State</c> 不一致——而当前
        /// Shadow 场景不产生任何计划，所以两者在**当前用例集合内**可比较。
        /// 任务 05/08 落地计划后，必须把这三态纳入比较并同步更新策略。
        /// 不可采样时为 <see cref="StateLabels.Unknown"/>。
        /// </summary>
        public int LegacyStateFlagsLive { get; }

        /// <summary>旧侧生命是否可采样（<c>false</c> = 显式不可采样，不是 0）。</summary>
        public bool HasCurrentHealthLive => !float.IsNaN(CurrentHealthLive);

        public override string ToString()
            => SlotId + " unit=" + UnitId + " def=" + DefinitionId + " faction=" + FactionId;
    }

    /// <summary>
    /// Legacy 侧一次只读检查点的观测事实（任务 03B 第二收尾轮 D1/R1；任务 04 扩展实时读取）。
    ///
    /// <see cref="Tick"/> 是旧时间线的真实逻辑 Tick（<c>BattleTimeline.CurrentTick</c>），
    /// 也就是比较的唯一对齐键——新侧同样按 <c>LogicSnapshot.Tick</c> 对齐，
    /// 因此两侧<strong>各自报告自己的 Tick</strong>，不一致就计入未对齐，绝不按帧序号强行配对。
    /// </summary>
    public sealed class LegacyLogicObservation
    {
        /// <summary>
        /// 当前确实可字段级比较的字段路径集合（报告用它统计真实比较量）。
        ///
        /// 任务 04 的扩展依据是 03B-交接记录 §23.2 的硬约束：登记为
        /// 「任务 04 必须切换为实时读取」的字段，其旧侧取值<strong>必须</strong>来自
        /// 旧场景/旧运行时的真实活动状态。切换后必须<strong>同时</strong>移入本集合，
        /// 否则"读过却不比较"等于没切换。
        ///
        /// 逐条口径：
        /// <list type="bullet">
        /// <item><c>units[i].state</c> ← 旧活动 <c>CombatUnit</c> 的旧 bool 按冻结规则推导的
        /// 状态族标签 vs 新状态机（<c>UnitSnapshot.State</c>）。</item>
        /// <item><c>units[i].healthQ10</c> ← 旧活动 <c>CombatUnit.CurrentHealth</c> 经
        /// <c>BattleSimulation.QuantizeHealth</c> 量化 vs <c>UnitSnapshot.HealthQ10</c>。</item>
        /// <item><c>units[i].position</c> / <c>units[i].facing</c> ← 旧活动
        /// <c>CombatUnit.GridPosition</c> / <c>FacingDirection</c> vs 单位快照。</item>
        /// <item><c>battleEnd.isEnded</c> / <c>battleEnd.resultCode</c> ← 旧活动
        /// <c>BattleManager</c> 的结束标志及其派生的结果码 vs <c>BattleEndSnapshot</c>。
        /// 其中"尚无结果码"的两种表示（旧侧空串、新侧 <c>null</c>）在比较器里
        /// 归一为同一缺席事实，只有任一侧真的给出结果码时才逐字判定。</item>
        /// <item><c>scheduledEventCount</c> ← 旧活动 <c>BattleTimeline</c> 的未执行排程条目数
        /// （<strong>不是</strong>新内核 <c>StatusEffect</c>/<c>ActionPlan</c> 的计数；
        /// 它用于证明"旧侧排程事实确实被读过"，见 03B-交接记录 §23.2 对
        /// <c>effects.count</c>/<c>plans.count</c>/<c>intents.count</c> 的语义边界说明）。</item>
        /// </list>
        /// </summary>
        public static readonly IReadOnlyList<string> ComparableFieldPaths =
            new List<string>
            {
                "tick",
                "units.count",
                "battleEnd.isEnded",
                "battleEnd.resultCode",
                "scheduledEventCount",
                "units[i].slotId",
                "units[i].unitId",
                "units[i].definitionId",
                "units[i].factionId",
                "units[i].state",
                "units[i].healthQ10",
                "units[i].position",
                "units[i].facing"
            }.AsReadOnly();

        /// <summary>每个检查点上<strong>与单位数无关</strong>的可比较字段数。</summary>
        public const int ComparableFieldCountPerCheckpoint = 5;

        /// <summary>每个检查点上<strong>逐单位</strong>的可比较字段数。</summary>
        public const int ComparableFieldCountPerUnit = 8;

        public LegacyLogicObservation(
            long tick, string checkpoint, int observedUnitCount,
            IReadOnlyList<LegacyLogicUnitObservation> units, string sourceName,
            bool battleEndedLive = false,
            string battleResultCodeLive = "",
            int scheduledEventCountLive = 0)
        {
            Tick = tick;
            Checkpoint = checkpoint ?? string.Empty;
            ObservedUnitCount = observedUnitCount;
            Units = units ?? (IReadOnlyList<LegacyLogicUnitObservation>)Array.Empty<LegacyLogicUnitObservation>();
            SourceName = sourceName ?? string.Empty;
            BattleEndedLive = battleEndedLive;
            BattleResultCodeLive = battleResultCodeLive ?? string.Empty;
            ScheduledEventCountLive = scheduledEventCountLive;
        }

        public long Tick { get; }

        /// <summary>具名检查点（与 Shadow 侧一致，例如 <c>LateUpdate</c>）。</summary>
        public string Checkpoint { get; }

        /// <summary>
        /// 旧场景中<strong>真实活动</strong>的单位数（第三收尾轮 R2 更正口径）。
        ///
        /// 来源：<c>FindObjectsByType&lt;CombatUnit&gt;(FindObjectsInactive.Exclude)</c> 的长度——
        /// 即旧场景里当前被激活的 <c>CombatUnit</c> 实例数（非活动实例不计入）。
        /// 生产观测实现在采样时强制它与显式绑定数、定义槽位数三者相等；
        /// 因此它<strong>不是</strong>定义槽位数，也<strong>不是</strong>场景资产里的对象总数。
        /// </summary>
        public int ObservedUnitCount { get; }

        /// <summary>按 <c>UnitId</c> 升序（= 唯一权威槽位顺序）的单位身份事实。</summary>
        public IReadOnlyList<LegacyLogicUnitObservation> Units { get; }

        /// <summary>观测来源名称（诊断用；例如实现该观测的生产组件名）。</summary>
        public string SourceName { get; }

        /// <summary>
        /// 旧活动 <c>BattleManager</c> 的结束标志（<c>BattleManager.BattleEnded</c>，真读）。
        /// 无该组件可读时为 <c>false</c>——但那会让"未结束"变成两侧常量相等，
        /// 因此生产观测实现<strong>读不到就整体失败</strong>（返回 <c>null</c> 观测），
        /// 不靠默认值冒充。
        /// </summary>
        public bool BattleEndedLive { get; }

        /// <summary>
        /// 旧活动 <c>BattleManager</c> 结束信息的派生结果码（真读后按冻结规则解析）：
        /// 旧侧没有结果码字段，只有显示文本；解析规则为"文本含 <c>VICTORY</c> → Victory 码、
        /// 含 <c>DEFEAT</c> → Defeat 码、已结束但两者都不含 → 旧停止码、未结束 → 空串"。
        /// 它**不是**在旧侧新增语义，而是把旧侧已有事实映射到同一比较域。
        /// </summary>
        public string BattleResultCodeLive { get; }

        /// <summary>
        /// 旧活动 <c>BattleTimeline</c> 尚未执行的排程条目数（真读）。
        ///
        /// <strong>语义边界</strong>：它是旧排程表（动作 Intent 与状态改变 Intent 混合）
        /// 的计数，与新内核 <c>StatusEffect</c>/<c>ActionPlan</c>/<c>Intent</c> 不是同一结构。
        /// 新侧对应事实恒为 0（新内核当前不产生排程条目），因此它与旧侧 0 相等；
        /// 这个等式在当前用例集合内成立，但**不**推广为"结构等价"。
        /// </summary>
        public int ScheduledEventCountLive { get; }

        /// <summary>本检查点实际参与字段级比较的字段数（= 比较内容量的可证伪指标）。</summary>
        public int ComparableFieldCount
            => ComparableFieldCountPerCheckpoint
               + (Units != null ? Units.Count * ComparableFieldCountPerUnit : 0);

        public string AlignmentKey => Tick.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "@" + Checkpoint;

        public override string ToString()
            => AlignmentKey + " units=" + ObservedUnitCount
               + " fields=" + ComparableFieldCount + " source=" + SourceName;
    }

    /// <summary>
    /// Legacy 侧只读观测来源（任务 03B 第二收尾轮 R1）。
    ///
    /// 为什么由 <c>Assembly-CSharp</c> 的生产组件实现：读取"旧场景里真实存在哪些单位、
    /// 它们各自绑定哪个 Encounter 槽位、旧时间线推进到哪个 Tick"必须由持有旧类型的
    /// 程序集完成；契约程序集只接收<strong>纯数据</strong>观测（不含任何 Unity 或 Legacy 成员），
    /// 因此比较器可以在不认识 Legacy 类型的前提下做字段级比较。
    ///
    /// 实现方<strong>只读</strong>：不得写旧状态、不得推进时钟、不得创建对象。
    /// </summary>
    public interface ILegacyLogicObservationSource
    {
        /// <summary>观测来源名称（诊断用）。</summary>
        string SourceName { get; }

        /// <summary>
        /// 旧时间线当前的真实逻辑 Tick（比较对齐键与 Shadow 推进目标）。
        /// 不可用（未接线/未解析）时返回 <c>-1</c>，<strong>不得</strong>返回 0——
        /// 0 会让"新侧目标 Tick 已达成"从而停止推进，是典型的 fail-open。
        /// </summary>
        long CurrentTick { get; }

        /// <summary>
        /// 采样一次 Legacy 侧只读观测（含单位身份事实）。无法采样（未接线/构建失败）时返回 <c>null</c>，
        /// <strong>不得</strong>返回"看起来像空世界"的半成品观测——那会让比较空转而不报警。
        /// </summary>
        LegacyLogicObservation Observe(string checkpointName);
    }

    /// <summary>
    /// 显式槽位注入的适配器集合。Bootstrap 只从本集合取得权威路径，
    /// <strong>绝不按容器枚举顺序或反射发现选择权威路径</strong>（任务包「必须产出」3）。
    /// </summary>
    public sealed class FrameAdapterSet
    {
        private readonly List<IBattleFrameAdapter> _adapters = new List<IBattleFrameAdapter>();

        /// <summary>显式序列化槽位：Legacy 帧适配器（Assembly-CSharp 实现）。</summary>
        public IBattleFrameAdapter Legacy { get; private set; }

        /// <summary>显式序列化槽位：最小 New Driver（契约程序集实现）。</summary>
        public IBattleFrameAdapter NewDriver { get; private set; }

        /// <summary>显式序列化槽位：Shadow runner（契约程序集实现）。</summary>
        public ShadowBattleRunner Shadow { get; private set; }

        public IReadOnlyList<IBattleFrameAdapter> All => _adapters;

        public void SetLegacy(IBattleFrameAdapter adapter)
        {
            Legacy = adapter;
            Rebuild();
        }

        public void SetNewDriver(IBattleFrameAdapter adapter)
        {
            NewDriver = adapter;
            Rebuild();
        }

        public void SetShadow(ShadowBattleRunner runner)
        {
            Shadow = runner;
            Rebuild();
        }

        /// <summary>
        /// 清空集合：每场战斗开始时重新装配"参与本场战斗的适配器"，
        /// 释放时不再持有任何适配器引用（可回切语义）。
        ///
        /// 这条不变量让 <c>StopBattle</c> 的"每个活动适配器恰好停止一次"成立：
        /// 上一场战斗的 Shadow runner 不会在下一场 Legacy 战斗里被再次停止。
        /// </summary>
        public void Clear()
        {
            Legacy = null;
            NewDriver = null;
            Shadow = null;
            _adapters.Clear();
        }

        private void Rebuild()
        {
            _adapters.Clear();
            if (Legacy != null) _adapters.Add(Legacy);
            if (NewDriver != null) _adapters.Add(NewDriver);
            if (Shadow != null) _adapters.Add(Shadow);
        }
    }
}
