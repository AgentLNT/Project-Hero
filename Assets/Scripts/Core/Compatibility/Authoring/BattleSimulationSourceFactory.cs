using System;
using System.Collections.Generic;
using System.Text;
using ProjectHero.Authoring;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Gameplay;
using ProjectHero.Core.Timeline;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Authoring
{
    /// <summary>
    /// 真实 02B 初始化链到 Bootstrap 契约的接缝（留在 <c>Assembly-CSharp</c>）。
    ///
    /// 为什么必须留在旧程序集：构建 <see cref="BattleDefinition"/> 需要
    /// <c>LegacyAssetResolver</c> / <c>BattleDefinitionBuilder</c>，而
    /// <c>ProjectHero.Authoring</c> 自身依赖旧资产类型（<c>ActionLibrarySO</c> /
    /// <c>AttackPattern</c> / <c>UnitVolume</c>），这些类型编译在预定义
    /// <c>Assembly-CSharp</c> 中。契约程序集
    /// <c>ProjectHero.Compatibility.Runtime</c> 因此<strong>只引用
    /// ProjectHero.Logic + UnityEngine</strong>，"读旧资产/读旧组件"这一步留在旧程序集，
    /// 通过显式 <c>MonoBehaviour</c> 槽位把 <see cref="BattleSimulationSeed"/> 注入 Bootstrap
    /// （00 号规则 17 / 任务包「必须产出」3）。
    ///
    /// 契约：
    /// <list type="bullet">
    /// <item>单位来源由<strong>显式序列化引用</strong>给出（<see cref="_heroUnit"/> / <see cref="_enemyUnit"/>），
    /// 不在运行时按发现顺序解析；<c>IsPlayerControlled</c> 不参与槽位判定。</item>
    /// <item>构建整体拒绝：任一被引用配置无法转换时 <see cref="LastConfigurationError"/> 非空，
    /// 并且 <see cref="BuildSeed"/> 只返回 <c>Definition == null</c> 的种子
    /// （Bootstrap/Driver 会据此拒绝启动）。</item>
    /// <item>不补 ID、不猜默认值、不做运行时资产转换——全部走 02B 已冻结的只读 API。</item>
    /// </list>
    /// </summary>
    public sealed class BattleSimulationSourceFactory : MonoBehaviour, IBattleSimulationSource,
        ILegacyLogicObservationSource
    {
        private const string ENCOUNTER_NOT_FOUND = "SIMULATION_SOURCE_ENCOUNTER_NOT_FOUND";

        /// <summary>旧侧观测不可用时的稳定原因（诊断用，不当作 0 顶替）。</summary>
        private const string LEGACY_OBSERVATION_UNAVAILABLE = "LEGACY_OBSERVATION_UNAVAILABLE";

        [Header("显式单位槽位（不按发现顺序解析）")]
        [SerializeField] private CombatUnit _heroUnit;
        [SerializeField] private CombatUnit _enemyUnit;

        [Header("显式运行时输入（任务 02B 契约）")]
        [Tooltip("本场 RNG 初始种子；必须显式给出，0 合法。")]
        [SerializeField] private long _initialRngSeed = 20260922L;

        [Tooltip("本场局外资源开局持有量（非负）。")]
        [SerializeField] private int _initialMetaResource = BattleDefinitionBuilder.MainEncounterInitialMetaResource;

        private BattleDefinition _definition;
        private EncounterDefinitionId _encounterId;
        private BattleRuntimeInputs _runtimeInputs;
        private string _lastError;
        private bool _built;
        private string _inputSummary = string.Empty;

        /// <summary>旧时间线缓存（只用于<strong>只读</strong>观测逻辑 Tick）。</summary>
        private BattleTimeline _timeline;

        public string SourceName => nameof(BattleSimulationSourceFactory) + ":" + name;

        public string LastConfigurationError
        {
            get
            {
                EnsureBuilt();
                return _lastError;
            }
        }

        /// <summary>构建出的主战斗定义哈希（构建失败时为空）。</summary>
        public string BattleDefinitionHash
        {
            get
            {
                EnsureBuilt();
                return _definition != null ? _definition.BattleDefinitionHashValue : string.Empty;
            }
        }

        /// <summary>构建出的 RulesVersion（构建失败时为空）。</summary>
        public string RulesVersion
        {
            get
            {
                EnsureBuilt();
                return _definition != null ? _definition.RulesVersion : string.Empty;
            }
        }

        public string InputSummary
        {
            get
            {
                EnsureBuilt();
                return _inputSummary;
            }
        }

        public EncounterDefinitionId EncounterId
        {
            get
            {
                EnsureBuilt();
                return _encounterId;
            }
        }

        public BattleSimulationSeed BuildSeed()
        {
            EnsureBuilt();
            return new BattleSimulationSeed(_definition, _encounterId, _runtimeInputs, _inputSummary);
        }

        private void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            _encounterId = new EncounterDefinitionId(LegacyIdMigrationManifest.MainEncounterId);
            _runtimeInputs = new BattleRuntimeInputs((ulong)_initialRngSeed, _initialMetaResource);

            string inputError = _runtimeInputs.Validate();
            if (inputError != null)
            {
                _lastError = inputError;
                return;
            }

            if (_heroUnit == null || _enemyUnit == null)
            {
                _lastError = LegacyBattleUnitSource.LEGACY_UNIT_SOURCE_MISSING
                    + "|explicit slots required (hero/enemy)";
                return;
            }

            LegacyBattleUnitSource unitsSource;
            LegacyAssetSet assets;
            try
            {
                assets = LegacyAssetResolver.LoadAllFromResources();
                unitsSource = LegacyCombatUnitStatsReader.ReadFromActiveScene(_heroUnit, _enemyUnit);
            }
            catch (System.Exception exception)
            {
                _lastError = "SIMULATION_SOURCE_ASSET_LOAD_FAILED|" + exception.GetType().Name
                    + "|" + exception.Message;
                return;
            }

            BattleDefinitionBuildResult result = BattleDefinitionBuilder.BuildMainBattleDefinition(assets, unitsSource);
            if (!result.Succeeded)
            {
                var builder = new StringBuilder("SIMULATION_SOURCE_DEFINITION_BUILD_FAILED|");
                var codes = result.ErrorCodes();
                for (int i = 0; i < codes.Length; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append(codes[i]);
                }
                _lastError = builder.ToString();
                return;
            }

            _definition = result.Definition;
            if (_definition.FindEncounter(_encounterId) == null)
            {
                _lastError = ENCOUNTER_NOT_FOUND + "|" + _encounterId.Value;
                _definition = null;
                return;
            }

            _lastError = null;
            _inputSummary = DescribeInputSummary(unitsSource, assets);
        }

        private static string DescribeInputSummary(LegacyBattleUnitSource unitsSource, LegacyAssetSet assets)
        {
            int libraryCount = assets != null && assets.LibrariesByGuid != null ? assets.LibrariesByGuid.Count : 0;
            int patternCount = assets != null && assets.PatternsByGuid != null ? assets.PatternsByGuid.Count : 0;
            int volumeCount = assets != null && assets.VolumesByGuid != null ? assets.VolumesByGuid.Count : 0;
            return "units=" + (unitsSource != null ? unitsSource.Description : "<none>")
                + " libraries=" + libraryCount
                + " patterns=" + patternCount
                + " volumes=" + volumeCount;
        }

        // ---------------- Legacy 侧只读观测（任务 03B 第二收尾轮 R1） ----------------

        /// <summary>
        /// 旧时间线当前的真实逻辑 Tick（<c>BattleTimeline.CurrentTick</c>）。
        ///
        /// 只读：不推进时钟、不写旧状态。不可用时返回 <c>-1</c>——
        /// <strong>绝不返回 0</strong>：0 会被当成本场比较已经对齐，是典型的 fail-open。
        /// </summary>
        public long CurrentTick
        {
            get
            {
                var timeline = ResolveTimeline();
                return timeline != null ? timeline.CurrentTick : -1L;
            }
        }

        /// <summary>
        /// 采样一次 Legacy 侧只读观测。
        ///
        /// <strong>字段来源（任务 04 更新）</strong>：
        /// <list type="bullet">
        /// <item><strong>旧场景活动事实（真读）</strong>：旧时间线的真实逻辑 Tick
        /// （<c>BattleTimeline.CurrentTick</c>）、旧场景中<strong>当前活动</strong>的
        /// <c>CombatUnit</c> 实例集合与数量、每个绑定单位的活动对象路径
        /// （<c>GameObject.scene.name</c> + <c>name</c>），
        /// 以及 03B-交接记录 §23.2 登记为「任务 04 必须切换为实时读取」的单位与战斗事实：
        /// <c>CombatUnit.CurrentHealth</c> / <c>GridPosition</c> / <c>FacingDirection</c> /
        /// 旧状态 bool，<c>BattleManager.BattleEnded</c>（+ 结束文本派生结果码），
        /// <c>BattleTimeline.ScheduledEventCount</c>。
        /// <strong>非活动单位的旧状态不被读取</strong>（搜索用
        /// <c>FindObjectsInactive.Exclude</c>），因此"活动"这一限定是结构性的，不是约定。</item>
        /// <item><strong>定义派生事实（推导，不是旧世界活动状态）</strong>：槽位 / <c>UnitId</c> /
        /// 定义 / 阵营来自 <see cref="EncounterSlotOrdering.OrderBySlotIdOrdinal"/> 排出的
        /// Encounter 定义槽位表。它们对旧世界的鉴别力仅限"显式槽位绑定恒等式"：
        /// <see cref="ValidateActiveUnitSet"/> 强制"活动单位集合 == 显式绑定集合 == 定义槽位集合"，
        /// 因此绑定被改坏时观测直接失败（比较器报 <c>NO_COMPARABLE_CHECKPOINT</c>），
        /// <strong>不会</strong>拿定义常量冒充旧世界事实。</item>
        /// </list>
        ///
        /// 仍<strong>不</strong>在这里读、也不参与字段级比较的字段（逐条登记在
        /// <c>ShadowCasePolicy</c> 的暂不可比较表里，带对象路径/原因/负责任务/清零门槛）：
        /// 体力/肾上腺素（旧侧浮点逐帧变化，与新内核整数 Tick 边界不对齐，属任务 07）、
        /// 新内核的持续效果/计划/意图计数（旧排程表不是同一结构，分别属任务 04/05/08）、
        /// RNG 状态（旧运行时可无对应采样点，属任务 09）。
        ///
        /// 不补 ID、不猜默认值、不做资产转换；不可用时返回 <c>null</c>（比较器据此报告
        /// <c>NO_COMPARABLE_CHECKPOINT</c>，而不是悄悄宣称等价）。
        /// </summary>
        public LegacyLogicObservation Observe(string checkpointName)
        {
            EnsureBuilt();
            if (_lastError != null) return null;

            var timeline = ResolveTimeline();
            if (timeline == null) return null;
            if (_definition == null || _heroUnit == null || _enemyUnit == null) return null;

            var encounter = _definition.FindEncounter(_encounterId);
            if (encounter == null || encounter.Slots == null) return null;

            // 旧场景活动事实（真读）：活动 CombatUnit 必须恰好是显式绑定的那两个，
            // 且与定义槽位数一致。任一不成立 ⇒ 本检查点不可比较（fail-closed）。
            var activeUnits = ValidateActiveUnitSet();
            if (activeUnits == null) return null;

            var boundUnits = CollectBoundUnits();
            if (boundUnits == null) return null;

            // 旧胜负事实：活动 BattleManager 必须存在且可读，否则整体失败
            // （读不到就返回 null，绝不用默认 false 冒充"旧侧未结束"）。
            var battleManager = BattleManager.Instance;
            if (battleManager == null) return null;
            bool legacyBattleEnded = battleManager.BattleEnded;

            // 唯一权威顺序：SlotId 的 Ordinal 升序 ⇒ UnitId(1..N)（与 BattleInitializer 一致）。
            var ordered = EncounterSlotOrdering.OrderBySlotIdOrdinal(encounter.Slots);

            var units = new List<LegacyLogicUnitObservation>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                var slot = ordered[i];
                if (slot == null) continue;

                CombatUnit bound;
                if (!boundUnits.TryGetValue(slot.SlotId.Value, out bound) || bound == null)
                {
                    // 槽位没有显式绑定：不猜、不取第一个。观测到此为止，由比较器报"未对齐"。
                    return null;
                }

                units.Add(new LegacyLogicUnitObservation(
                    slot.SlotId.Value,
                    i + 1L,
                    slot.DefinitionId.Value,
                    slot.FactionId.Value,
                    DescribeObjectPath(bound),
                    // —— 任务 04：旧场景活动单位的实时状态（真读）——
                    bound.CurrentHealth,
                    bound.GridPosition.X,
                    bound.GridPosition.Y,
                    (int)bound.FacingDirection,
                    DeriveLegacyStateLabel(bound)));
            }

            if (units.Count == 0) return null;

            return new LegacyLogicObservation(
                timeline.CurrentTick,
                checkpointName,
                // 单位数 = 旧场景中真实活动的单位数（上面已强制其等于定义槽位数）。
                activeUnits.Length,
                units,
                SourceName,
                legacyBattleEnded,
                DeriveLegacyResultCode(legacyBattleEnded, battleManager),
                timeline.ScheduledEventCount);
        }

        /// <summary>
        /// 旧 bool → 新状态族标签的<strong>冻结推导</strong>（单向、穷尽、可复核）。
        ///
        /// 顺序即优先级：死亡 &gt; 击倒 &gt; 硬直 &gt; 前摇 &gt; 后摇 &gt; 移动 &gt; 空闲。
        /// 旧侧没有 <c>Guarding</c>/<c>Blocking</c>/<c>Dodging</c> 的等价事实
        /// （那三种是新内核的计划状态，属任务 05/08），因此本推导<strong>不可能</strong>
        /// 产出它们——这是覆盖边界，已写入
        /// <c>LegacyLogicUnitObservation.LegacyStateFlagsLive</c> 的文档。
        /// </summary>
        private static int DeriveLegacyStateLabel(CombatUnit unit)
        {
            if (unit == null) return LegacyLogicUnitObservation.StateLabels.Unknown;
            if (unit.CurrentHealth <= 0f) return LegacyLogicUnitObservation.StateLabels.Dead;
            if (unit.IsKnockedDown) return LegacyLogicUnitObservation.StateLabels.KnockedDown;
            if (unit.IsStaggered) return LegacyLogicUnitObservation.StateLabels.Staggered;
            if (unit.InWindup) return LegacyLogicUnitObservation.StateLabels.Windup;
            if (unit.InRecovery) return LegacyLogicUnitObservation.StateLabels.Recovery;
            if (unit.IsMoving) return LegacyLogicUnitObservation.StateLabels.Moving;
            return LegacyLogicUnitObservation.StateLabels.Idle;
        }

        /// <summary>
        /// 旧结束事实 → 结果码（把旧侧已有事实映射到与新内核同一比较域）。
        ///
        /// 旧侧<strong>没有</strong>结果码字段，只有 <c>_endMessage</c> 显示文本；这里按
        /// 冻结规则解析：已结束且文本含 <c>VICTORY</c> → <c>VictoryResultCode</c>；
        /// 含 <c>DEFEAT</c> → <c>DefeatResultCode</c>；已结束但两者都不含 → 旧停止码；
        /// 未结束 → 空串。两个结果码都取自<strong>权威 <see cref="VictoryDefinition"/></strong>，
        /// 不在这里写死字符串。
        /// </summary>
        private string DeriveLegacyResultCode(bool battleEnded, BattleManager battleManager)
        {
            if (!battleEnded) return string.Empty;

            var victory = _definition?.FindEncounter(_encounterId)?.Victory;
            string message = battleManager.EndMessage ?? string.Empty;
            if (message.IndexOf("VICTORY", StringComparison.Ordinal) >= 0)
                return victory != null ? victory.VictoryResultCode : string.Empty;
            if (message.IndexOf("DEFEAT", StringComparison.Ordinal) >= 0)
                return victory != null ? victory.DefeatResultCode : string.Empty;
            return ProjectHero.Logic.Simulation.BattleResultCodes.Stopped;
        }

        /// <summary>
        /// 读取旧场景中<strong>真实活动</strong>的单位集合，并核对三个集合恒等：
        /// 活动单位集合 == 显式序列化槽位绑定集合（hero/enemy）== 定义槽位表（上面调用点保证）。
        ///
        /// 这是"旧场景活动事实"进入观测的唯一门槛：任何一个不成立就返回 <c>null</c>，
        /// 让比较器报告 <c>NO_COMPARABLE_CHECKPOINT</c>，而不是用定义常量冒充旧世界事实。
        /// 只读：不写旧状态、不推进时钟、不创建对象。
        /// </summary>
        private CombatUnit[] ValidateActiveUnitSet()
        {
            // FindObjectsInactive.Exclude ⇒ 返回的<strong>只有活动对象</strong>；
            // 非活动单位的旧状态不进入任何比较。
            var active = UnityEngine.Object.FindObjectsByType<CombatUnit>(FindObjectsInactive.Exclude);
            if (active == null || active.Length == 0) return null;

            var bound = new HashSet<CombatUnit> { _heroUnit, _enemyUnit };
            if (bound.Count != 2) return null;

            // 数量恒等：活动单位数 == 显式绑定数。
            if (active.Length != bound.Count) return null;

            // 集合恒等 + 不重复：活动单位正是被绑定的那两个，且没有被重复计入。
            for (int i = 0; i < active.Length; i++)
            {
                var unit = active[i];
                if (unit == null) return null;
                if (!bound.Contains(unit)) return null;
            }

            return active;
        }

        /// <summary>显式序列化槽位绑定（hero/enemy）⇒ 槽位 ID 的只读映射；缺失时返回 null。</summary>
        private Dictionary<string, CombatUnit> CollectBoundUnits()
        {
            if (_heroUnit == null || _enemyUnit == null) return null;

            return new Dictionary<string, CombatUnit>(StringComparer.Ordinal)
            {
                [LegacyIdMigrationManifest.SlotHero] = _heroUnit,
                [LegacyIdMigrationManifest.SlotEnemy] = _enemyUnit
            };
        }

        /// <summary>旧场景对象路径（诊断与报告用；不参与比较）。</summary>
        private static string DescribeObjectPath(CombatUnit unit)
        {
            if (unit == null) return "<null>";
            string scene = unit.gameObject.scene.IsValid() ? unit.gameObject.scene.name : "<no-scene>";
            return scene + "/" + unit.gameObject.name + "#CombatUnit";
        }

        private BattleTimeline ResolveTimeline()
        {
            if (_timeline != null) return _timeline;

            // 与 CombatDemo.Start 的兜底一致：先取显式引用，再在同场景内解析唯一实例，
            // 绝不在观测路径里创建任何对象。
            var found = UnityEngine.Object.FindObjectsByType<BattleTimeline>(
                FindObjectsInactive.Exclude);
            if (found != null && found.Length == 1) _timeline = found[0];
            return _timeline;
        }
    }
}
