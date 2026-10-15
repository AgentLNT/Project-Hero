using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Runtime
{
    public interface IShadowScenarioSource
    {
        Logic.Simulation.BattleSimulationAssembly BuildShadowAssembly(BattleSimulationSeed seed);
    }
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
    /// <summary>Migration-only observations and write walls, owned by the Editor host.</summary>
    public sealed class MigrationRuntimeContext : BattleRuntimeContext, IBattleRuntimeObserver
    {
        public MigrationRuntimeContext(BattleRuntimeMode mode, RuntimeCallLedger ledger,
            ShadowWriteCounters shadowWrites, IBattleSimulationSource source,
            Logic.Replay.ReplayAuthorityInput authorityInput = null) : base(source)
        { Mode = mode; Ledger = ledger; ShadowWrites = shadowWrites; AuthorityInput = authorityInput; }
        public BattleRuntimeMode Mode { get; }
        public RuntimeCallLedger Ledger { get; }
        public ShadowWriteCounters ShadowWrites { get; }
        public Logic.Replay.ReplayAuthorityInput AuthorityInput { get; }
        public void OnDriverFrame(float deltaTime) => Ledger?.RecordNewDriverInvocation(deltaTime);
        public void OnFrozenBatch(Logic.Commands.FrozenCommandBatch batch)
        { if (AuthorityInput != null) ShadowAuthorityProtocol.RecordFrozenBatch(AuthorityInput, batch); }
        public void OnCommittedStep(Logic.Simulation.BattleSimulation simulation, long tick, Logic.Simulation.StepResult result)
        { if (AuthorityInput != null) ShadowAuthorityProtocol.RecordOutcomes(AuthorityInput, simulation, tick, result); }
        public void OnSimulationStep(Logic.Simulation.StepStatus status) => Ledger?.RecordNewSimulationStep(status);
        public BattleRuntimeContext DriverContext => new BattleRuntimeContext(SimulationSource, this);
    }
}
