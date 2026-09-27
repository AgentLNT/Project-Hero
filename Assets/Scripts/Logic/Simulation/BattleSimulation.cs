using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Status;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Simulation
{
    /// <summary>模拟生命周期与 Step 契约的稳定失败码（任务 03 冻结）。</summary>
    public static class SimulationCodes
    {
        public const string SIMULATION_DEFINITION_NULL = "SIMULATION_DEFINITION_NULL";
        public const string SIMULATION_ASSEMBLY_NULL = "SIMULATION_ASSEMBLY_NULL";
        public const string SIMULATION_DISPOSED = "SIMULATION_DISPOSED";
        public const string STEP_TICK_NOT_CURRENT = "STEP_TICK_NOT_CURRENT";
        public const string STEP_NON_CURRENT_TICK_REQUEST = "STEP_NON_CURRENT_TICK_REQUEST";
        public const string STEP_BATCH_NOT_FROM_REGISTRY = "STEP_BATCH_NOT_FROM_REGISTRY";
        public const string STEP_INVARIANT_VIOLATION = "STEP_INVARIANT_VIOLATION";
        public const string STEP_RELOCATION_BATCH_INVALID = "STEP_RELOCATION_BATCH_INVALID";
        public const string STEP_DISPLACEMENT_REQUEST_DUPLICATE = "STEP_DISPLACEMENT_REQUEST_DUPLICATE";
        public const string STEP_PHASE_BASE_REVISION_CHANGED = "STEP_PHASE_BASE_REVISION_CHANGED";
        public const string STEP_ADVANCE_REQUEST_INVALID = "STEP_ADVANCE_REQUEST_INVALID";
    }

    /// <summary>默认战斗结果码（<see cref="VictoryDefinition"/> 的显式结果码优先）。</summary>
    public static class BattleResultCodes
    {
        public const string Stopped = "BATTLE_STOPPED";
    }

    /// <summary>唯一终态清理的审计报告（幂等；不做第二套计划清理路径）。</summary>
    public sealed record BattleEndFinalizerReport(
        long EndedAtTick,
        string ResultCode,
        int ClearedFutureTickBucketFacts,
        bool ClosedWindow,
        bool AlreadyFinalized);

    /// <summary>
    /// 纯 C# 战斗模拟内核（主方案 3.4.3 的 <c>BattleSimulation</c>）。
    ///
    /// 生命周期：<c>Create -&gt; Step* -&gt; RequestStop -&gt; Dispose</c>。
    /// <list type="bullet">
    /// <item><strong>唯一模拟入口</strong>是 <see cref="Step"/>；它只接受本场
    /// <see cref="CommandIngressRegistry"/> 冻结出的 <see cref="FrozenCommandBatch"/>。</item>
    /// <item>不实现 <c>Update()</c>、协程或定时器，不读取 Unity 帧时间/缩放；每次推进都必须由
    /// 外部显式调用 <see cref="Step"/>。</item>
    /// <item>接口不接受 <c>deltaTime</c>、MonoBehaviour、GameObject、View 或回调式表现依赖
    /// （帧时间到 Tick 的累积属于任务 03B/10 的外部适配器）。</item>
    /// <item>不提供 <c>Restore(...)</c>、中途存档/载入或任意 Tick 跳转；确定性由"两次新建
    /// 模拟并从 Tick 0 运行"证明。</item>
    /// </list>
    ///
    /// <para>
    /// <strong>[04-FROZEN-STAGE2-COMMIT]</strong>（任务 04 修订轮 R1 冻结结论；以代码真实行为为唯一权威；
    /// 任务 05 的阶段 2 完成语义必须依赖这四条）：
    /// </para>
    /// <list type="number">
    /// <item>死亡提交只有一个实现（<see cref="ProcessDeaths"/>）：<c>IsAlive = false</c> 与
    /// <c>DeathProcessed = true</c> 在同一处成对写入（恒有 <c>DeathProcessed == !IsAlive</c>），
    /// 不存在 "<c>DeathProcessed == true</c> 且 <c>IsAlive == true</c>" 的中间状态。</item>
    /// <item>阶段 2（<c>DeathAndVictory</c>）在命令前判定为假（战斗继续）时<strong>完全不提交</strong>
    /// 任何死亡事实。</item>
    /// <item>阶段 2 判定为真（战斗已决定）时先按与阶段 15 完全相同的语义<strong>完整提交</strong>死亡，
    /// 再设结果码、拒绝已冻结批次并直接进入唯一 Finalizer：<strong>阶段 3–17 全部不执行</strong>。</item>
    /// <item>提交顺序固定为：死亡事实 → 结果码 → 冻结批次拒绝 → Finalizer 的
    /// <c>BattleEndedEvent</c>；因此<strong>死亡事件先于结束事件</strong>，"致死单位仍参与同 Tick
    /// 阶段 11–13 强制位移"仅在命令前判定为假时成立。</item>
    /// </list>
    /// </summary>
    public sealed class BattleSimulation : IDisposable
    {
        private sealed class UnitRuntimeState : IStatusEffectHost
        {
            public UnitId UnitId;
            public UnitDefinitionId DefinitionId;
            public FactionId FactionId;
            public GridPoint Position;
            public GridDirection Facing;
            public int HealthQ10;
            /// <summary>
            /// 该单位是否仍在场（= 仍占有格、仍参与同时空间求解）。
            ///
            /// 冻结语义（任务 04 死亡提交；<strong>修订轮 R1 以代码真实行为为唯一权威统一表述</strong>）：
            /// <list type="bullet">
            /// <item>死亡提交的<strong>唯一实现</strong>是 <see cref="ProcessDeaths"/>：它把
            /// <c>IsAlive = false</c>、<see cref="DeathProcessed"/> = <c>true</c>、终态
            /// <c>Dead</c> 转换、<c>UnitDiedEvent</c>、一次性清理通知与最终 footprint 移除
            /// <strong>在同一次调用里原子完成</strong>。因此<strong>不存在</strong>
            /// "<c>DeathProcessed == true</c> 且 <c>IsAlive == true</c>" 的中间状态，
            /// 也不存在"死亡已提交但仍占有格"的窗口。</item>
            /// <item>致死单位仍参与同 Tick 阶段 11–13 强制位移这件事是<strong>条件性</strong>的，
            /// 而不是结构性保证：<c>IsAlive</c> 直到死亡提交前都保持 <c>true</c>，但
            /// <strong>是否还有机会走到阶段 11</strong>取决于阶段 2 的门禁结果——
            /// 门禁返回 <c>null</c>（战斗继续）时才继续执行阶段 3–17；
            /// 门禁命中时阶段 2 <strong>先</strong>用 <see cref="ProcessDeaths"/> 完整提交死亡，
            /// <strong>再</strong>直接进入唯一 Finalizer，阶段 3–17 全部不执行。</item>
            /// <item>"本 Tick 已失去战斗效力但死亡尚未提交"这一事实的载体是
            /// <see cref="IsCombatEffective"/>（生命 ≤ 0 或已提交死亡），不是
            /// <see cref="DeathProcessed"/> 与 <c>IsAlive</c> 的组合：胜负判定必须用它，
            /// 才能在命令阶段之前就看到本 Tick 在阶段 1 刚被打死的单位。</item>
            /// </list>
            /// </summary>
            public bool IsAlive = true;

            /// <summary>
            /// 该单位是否已提交死亡终态。它由 <see cref="ProcessDeaths"/> 与
            /// <see cref="IsAlive"/> <strong>同一次写入成对置位</strong>（同一个原子提交），
            /// 因此恒有 <c>DeathProcessed == !IsAlive</c>；对存活单位而言它只是
            /// <see cref="IsKillable"/> / <see cref="IsCombatEffective"/> 里的<strong>冗余防御位</strong>
            /// （保留以防未来出现"只置一个"的误改，行为上不可达）。
            /// 它<strong>不是</strong>某个独立阶段的产物，也不表示"终态已置但仍在场"。
            /// </summary>
            public bool DeathProcessed;
            public int AvailableAdrenaline;
            public long AdrenalineCycleId;

            /// <summary>
            /// 该单位的唯一状态机（任务 04 起，新 Logic 路径的<strong>唯一</strong>权威状态）。
            /// 旧 <c>CombatUnit</c> 的兼容 bool 只能从这里派生读取。
            /// </summary>
            public UnitStateMachine StateMachine;

            /// <summary>本 Tick 内被持续效果写入的生命是否需要重新判定存活（阶段 2/15 统一处理）。</summary>
            public bool HealthDirty;

            UnitId IStatusEffectHost.UnitId => UnitId;

            UnitStateMachine IStatusEffectHost.StateMachine => StateMachine;

            int IStatusEffectHost.HealthQ10 => HealthQ10;

            void IStatusEffectHost.ApplyDamageQ10(int damageQ10)
            {
                HealthQ10 -= damageQ10;
                HealthDirty = true;

            }

            /// <summary>存活视图：<c>IsAlive</c> 由死亡系统唯一改写；生命 &lt;= 0 只是"待处理致死"。</summary>
            public bool IsKillable => IsAlive && !DeathProcessed && HealthQ10 <= 0;

            /// <summary>
            /// <strong>战斗有效</strong>：本 Tick 是否还能被算作存活参与胜负判定。
            ///
            /// 与 <see cref="IsAlive"/> 的差别是<strong>同一 Tick 内的阶段先后</strong>，
            /// 不是某个持久的中间状态：阶段 1 的伤害可以把生命降到 0，而死亡提交发生在
            /// 该 Tick 更晚的阶段（门禁未命中时是阶段 15，命中时是阶段 2 自己）。
            /// 胜负判定必须在<strong>命令阶段之前</strong>就得到正确结论，因此它用
            /// 本属性而不是 <see cref="IsAlive"/>：生命 &lt;= 0 或已提交死亡的单位
            /// 都算"已失去战斗有效"。
            /// </summary>
            public bool IsCombatEffective => IsAlive && !DeathProcessed && HealthQ10 > 0;
        }

        private readonly BattleDefinition _definition;
        private readonly EncounterDefinitionId _encounterId;
        private readonly EncounterDefinition _encounter;
        private readonly BattleRuntimeInputs _runtimeInputs;
        private readonly BattleSimulationAssembly _assembly;
        private readonly IFactionRelationResolver _factionResolver;
        private readonly IReadOnlyDictionary<ControllerId, IReadOnlyList<UnitId>> _controllerToUnitIds;

        private readonly List<UnitRuntimeState> _units = new List<UnitRuntimeState>();
        private readonly Dictionary<long, UnitRuntimeState> _unitsById = new Dictionary<long, UnitRuntimeState>();

        private readonly LogicIdGenerator _idGenerator;
        private readonly LogicSequenceGenerator _sequences = new LogicSequenceGenerator();
        private readonly DeterministicRng _rng;
        private readonly CommandIngressRegistry _ingress = new CommandIngressRegistry();
        private readonly CommandGateway _gateway;
        private readonly LogicEventOutbox _outbox;
        private readonly HistoryArchive _history = new HistoryArchive();
        private readonly StepPhaseTrace _trace = new StepPhaseTrace();
        private readonly TurnWindowState _window = new TurnWindowState();

        /// <summary>整场唯一的效果推进器（任务 04；后续任务必须复用，不得另建第二套）。</summary>
        private readonly BuffSystem _buffSystem;

        /// <summary>
        /// 任务 05：全局动作排程权威、唯一计划工厂、权威排程编辑器、启动门禁与
        /// 统一终态协调器。<strong>它们是本场战斗唯一的一套</strong>——不存在第二套
        /// 计划注册表、Lane 或终态清理入口。
        /// </summary>
        private readonly ActionScheduleAuthority _scheduleAuthority;

        private readonly ActionPlanFactory _planFactory;
        private readonly ScheduleEditor _scheduleEditor;
        private readonly ActionStartGate _startGate = new ActionStartGate();
        private readonly ActionPlanTerminalCoordinator _terminalCoordinator;
        private readonly IActionPlanStartCommitPort _startCommitPort;
        private readonly ActionPlanTerminalArchiveSource _terminalArchiveSource;
        private readonly ActionPlanCommandProcessor _planCommandProcessor;

        /// <summary>
        /// 任务 05：机会绑定的终态清理参与者。它在统一协调器里<strong>唯一</strong>负责
        /// "来源攻击终止 ⇒ 关闭机会 + 终止绑定反应 + 请求释放未消费预留"。
        /// </summary>
        private readonly OpportunityBindingCleanupParticipant _opportunityBindingParticipant;

        /// <summary>
        /// 任务 05：唯一的反应机会系统（与 <see cref="ReactionPlanner"/> 同属一套）。
        /// 它<strong>不</strong>保留第二份计划注册表或 Lane。
        /// </summary>
        private readonly ReactionOpportunitySystem _reactionSystem;

        /// <summary>任务 05：Dodge 终态接缝（只读闭包查询 + 统一终态提交；换位事务由任务 06/08 注入）。</summary>
        private readonly DodgeMovementInvalidationSeam _dodgeSeam;

        /// <summary>
        /// 任务 06：<strong>空间权威</strong>（占位、区域查询、邻居序列与 Reservation）。
        /// 它是新 Logic 路径唯一的占位来源；<c>GridManager</c> 只保留坐标/场景桥，不再双写。
        /// </summary>
        private readonly LogicGrid _logicGrid;

        /// <summary>任务 06：移动段表 + Reservation 事务 + 终态清理参与者（固定顺序槽位 500）。</summary>
        private readonly LogicGridMovementAuthority _movementAuthority;

        /// <summary>
        /// 任务 06：本次模拟绑定的移动链路径计算器（唯一实例，同时喂给排程事务与 Dodge 接缝的求值器）。
        /// </summary>
        private readonly IMovementPathCalculator _movementPathCalculator;

        /// <summary>
        /// 任务 06：<strong>Dodge 目的格预留与换位事务的真实实现</strong>。
        /// 它同时挂在反应接受事务、来源威胁取消通知与移动终态清理槽位上（同一个对象）。
        /// </summary>
        private readonly DodgeRelocationAuthority _dodgeRelocation;

        /// <summary>
        /// 任务 06：启动时使用的单位体积规范表来源（装配方注入优先，否则为
        /// <see cref="DefinitionVolumeTableSource"/> 的定义级绑定投影）。
        /// </summary>
        private readonly IUnitVolumeTableSource _unitVolumeTables;

        /// <summary>阶段 9 产出的 Dodge 提交报告（提交前快照 + 结果 + 提交后快照；供任务 08 消费）。</summary>
        private DodgeCommitReport _dodgeCommitReport = DodgeCommitReport.Empty(0L);

        /// <summary>本 Tick 新公开的机会（阶段 7；只用于诊断与测试观察，不参与哈希）。</summary>
        private readonly List<ReactionOpportunityRuntime> _openedOpportunitiesThisTick =
            new List<ReactionOpportunityRuntime>();

        /// <summary>本 Tick 已被排程事务改写的依赖闭包（系统自动延期的批内冲突判据）。</summary>
        private readonly HashSet<long> _claimedThisTick = new HashSet<long>();

        /// <summary>
        /// 每个单位一次性生命周期清理通知（按 <c>UnitId</c> 升序追加、整场不重复）。
        /// 死亡系统唯一的对外产物；任务 05 消费它锁定 Lane 并终止死者全部非终态计划。
        /// </summary>
        private readonly List<UnitLifecycleCleanupNotice> _lifecycleNotices = new List<UnitLifecycleCleanupNotice>();
        private readonly HashSet<long> _notifiedLifecycleUnitIds = new HashSet<long>();

        /// <summary>最近一次死亡处理中实际移除的最终 footprint（与通知一一对应，按 UnitId 升序）。</summary>
        private readonly List<UnitId> _lastFinalFootprintRemovals = new List<UnitId>();

        /// <summary>阶段的复用缓冲区（不参与哈希、不跨 Step 保留语义）。</summary>
        private readonly List<IStatusEffectHost> _effectHosts = new List<IStatusEffectHost>();
        private readonly List<UnitStateAdvanceRequest> _stateAdvanceRequests = new List<UnitStateAdvanceRequest>();

        private FrozenCommandBatch _frozenBatch;
        private FrozenTickCommandSet _tickCommandSet;
        private IReadOnlyList<CommandIngressRejection> _pendingIngressRejections = Array.Empty<CommandIngressRejection>();
        private IReadOnlyList<ForcedDisplacementRequest> _displacementRequests = Array.Empty<ForcedDisplacementRequest>();
        private ForcedDisplacementBatch _displacementBatch = ForcedDisplacementBatch.Empty;
        private IReadOnlyList<ActionPlanId> _invalidatedPlanIds = Array.Empty<ActionPlanId>();

        private long _batchBaseScheduleRevision;
        private long _scheduleRevision;
        private long _tick = -1L;
        private string _resolvedResultCode;
        private BattleEndSnapshot _battleEnd = BattleEndSnapshot.Active();
        private string _pendingStopResultCode;
        private bool _disposed;

        private LogicSnapshot _currentSnapshot;
        private LogicSnapshot _finalSnapshot;
        private DecisionSnapshot _lastDecisionSnapshot;
        private BattleEndFinalizerReport _finalizerReport;

        private BattleSimulation(
            BattleDefinition definition,
            EncounterDefinitionId encounterId,
            BattleRuntimeInputs runtimeInputs,
            BattleSimulationAssembly assembly)
        {
            if (definition == null)
                throw new LogicDefinitionException(SimulationCodes.SIMULATION_DEFINITION_NULL, "definition is null");
            if (assembly == null)
                throw new LogicDefinitionException(SimulationCodes.SIMULATION_ASSEMBLY_NULL, "assembly is null");

            // 唯一的配置/初始化真相：任务 02B 的 BattleInitializer。
            BattleInitializationResult initialization =
                BattleInitializer.BuildInitialState(definition, encounterId, runtimeInputs);

            _definition = definition;
            _encounterId = initialization.EncounterId;
            _runtimeInputs = initialization.RuntimeInputs;
            _assembly = assembly;
            _factionResolver = initialization.FactionResolver;
            _controllerToUnitIds = initialization.ControllerToUnitIds;
            _idGenerator = initialization.IdGenerator;
            _rng = new DeterministicRng(runtimeInputs.InitialRngSeed);
            _gateway = new CommandGateway(_sequences, assembly.PayloadAuthorizer);
            _outbox = new LogicEventOutbox(_sequences);
            _buffSystem = new BuffSystem(_idGenerator, _sequences, _outbox);

            // 任务 05：唯一一套全局排程权威 + 计划工厂 + 排程编辑器 + 统一终态协调器。
            // 计划事实来源就是本模拟的单位运行时状态（IActionPlanFactsSource 的只读投影）。
            _scheduleAuthority = new ActionScheduleAuthority();
            _scheduleAuthority.Factions = _factionResolver;
            // 任务 05 收尾 R1（缺陷 D2）：反应机会 ID 的唯一分配器是任务 03 契约的
            // LogicIdGenerator；权威只把它投影成只读的 NextReactionOptionSequence，
            // 不保留第二份计数状态（因此两条路径不可能各自取号）。
            _scheduleAuthority.IdGenerator = _idGenerator;

            // 任务 06 硬前置条件（任务 05 交接记录 §27.2 条款 1/2 与缺陷 D6）：
            // 唯一一套 <c>LogicGrid</c>（权威占位 + Reservation）与唯一一个路径计算器实例。
            // 两处求值器（排程编辑器内部与 Dodge 接缝）必须喂**同一个**计算器，否则路径投影会分叉。
            _encounter = definition.FindEncounter(_encounterId);
            if (_encounter == null)
                throw new LogicDefinitionException(DefinitionCodes.ENCOUNTER_NOT_FOUND, _encounterId.Value);

            _logicGrid = new LogicGrid(_encounter.GridBoundary);
            _movementAuthority = new LogicGridMovementAuthority(
                _logicGrid, _scheduleAuthority, definition.Rules.PathCostRules, definition.Rules.PathSearchRules);
            _movementPathCalculator = assembly.MovementPathCalculator
                ?? new LogicGridMovementPathCalculator(
                    _logicGrid, _scheduleAuthority, definition.Rules.PathCostRules, definition.Rules.PathSearchRules);
            // 任务 06：体积规范表的默认来源就是该定义的"单位 → VolumeSpec"绑定投影。
            _unitVolumeTables = assembly.UnitVolumeTables ?? new DefinitionVolumeTableSource(definition);

            _planFactory = new ActionPlanFactory(definition, _factionResolver, new UnitPlanFactsSource(this), _idGenerator);
            _scheduleEditor = new ScheduleEditor(_scheduleAuthority, _planFactory, _idGenerator, _movementPathCalculator);
            // 任务 06：排程事务 → 空间整批替换的接缝。显式编辑与系统自动延期因此**共用**
            // 同一个空间求值器与同一次整批提交；失败时两侧都零局部写入。
            _scheduleEditor.MovementSpacePort = _movementAuthority;
            // 机会绑定的清理参与者与机会系统互相引用：先建参与者 → 建协调器 → 建系统后回填。
            // 未回填的窗口内不存在任何终态请求，因此不存在"漏清理"的中间态。
            _opportunityBindingParticipant = new OpportunityBindingCleanupParticipant();
            _terminalCoordinator = new ActionPlanTerminalCoordinator(_scheduleAuthority, new IActionPlanCleanupParticipant[]
            {
                StopSchedulingCleanupParticipant.Instance,
                _opportunityBindingParticipant,
                // 任务 06 的移动段/Reservation 清理参与者：插在任务 05 冻结的固定位置
                // （MovementAndReservation = 500），不新建第二条清理路径。
                _movementAuthority
            });
            _startCommitPort = assembly.StartCommitPort ?? NoTurnBudgetCommitPort.Instance;
            _terminalArchiveSource = new ActionPlanTerminalArchiveSource(_scheduleAuthority, _terminalCoordinator);
            _planCommandProcessor = new ActionPlanCommandProcessor(_scheduleAuthority, _scheduleEditor, assembly.CommandProcessor);

            // 任务 06：Dodge 目的格预留与换位事务的**真实实现**（默认装配；任务 08 可整体替换）。
            // 它同时是：
            //   1. 反应接受事务的目的格预留端口（按 ReactionOpportunityId/TriggerTick 冻结）；
            //   2. 来源威胁取消的预留释放接收方（SourceThreatCancelled）；
            //   3. 移动终态清理槽位（MovementAndReservation = 500）上的 Dodge 预留释放端口；
            //   4. 任务 05 冻结的 IDodgeRelocationTransaction（TriggerTick 原子换位）。
            _dodgeRelocation = new DodgeRelocationAuthority(
                _logicGrid, _movementAuthority, _scheduleAuthority, _terminalCoordinator,
                specId => DodgeDestinationRules.FromPayload(
                    _definition.FindAction(specId)?.Payload as DodgePayloadSpec));
            // 任务 06 小修轮 R4/B-1：预算释放接缝在**装配点**接通（而不是留给下游记得赋值）。
            // 默认 assembly.BudgetReleaseSink 为 null ⇒ `?.Invoke` 不产生任何调用：
            // 这是"任务 06 不实现 TurnBudget 账本"的显式口径，不是漏接线
            // （接缝由 BattleSimulationAssembly.BudgetReleaseSink 显式声明，任务 07 只注入消费者）。
            _dodgeRelocation.BudgetReleaseSink = assembly.BudgetReleaseSink;
            _movementAuthority.DodgeDestinationReservations = _dodgeRelocation;

            // 任务 05：唯一的反应机会系统 + 唯一的终态/生命周期事件发射路径（经 Outbox）。
            //
            // 三个任务 06/07 接缝（区域候选来源 / 资源准备 / 预留释放接收方）由装配注入；
            // 未注入时的默认分别是"没有候选"（fail-closed）、"只校验不冻结"与"请求排队保留"
            // （绝不静默丢弃）。任务 06/07 装配后按同一入口注入，不改变机会状态机。
            _reactionSystem = new ReactionOpportunitySystem(
                _scheduleAuthority, _planFactory, definition, _factionResolver,
                // 任务 05 收尾 R1（缺陷 D2）：机会 ID 的唯一分配器（任务 03 冻结契约的
                // LogicIdGenerator），必填；缺省回落会重新引入"第二份计数状态"。
                _idGenerator,
                assembly.AreaThreatCandidateSource, _terminalCoordinator)
            {
                EventSink = sink => _outbox.Emit(sink),
                PreparationPort = assembly.ReactionPreparationPort,
                ReservationReleaseSink = assembly.ReactionReservationReleaseSink
            };
            // 任务 06：Dodge 目的格预留端口。装配方注入的实现若同时提供该端口则优先使用，
            // 否则用本任务的真实现（默认装配，绝不静默缺省）。
            //
            // 注意：**不**接管 ReservationReleaseSink。任务 05 冻结的语义是"没有接收方时排队保留
            // 给任务 07"，Dodge 预留的释放走统一终态清理槽位
            // （SourceThreatCancelled → 协调器 → MovementAndReservation 参与者 → DodgeDestinationReservations），
            // 因此两条路径互不抢占，也不会有预留被漏放。
            _reactionSystem.DestinationReservationPort =
                assembly.DodgeRelocationTransaction as IDodgeDestinationReservationPort ?? _dodgeRelocation;
            _opportunityBindingParticipant.Opportunities = _reactionSystem;

            // 终态事件只在"第一次请求胜出"时发射一次（协调器是唯一判定点）。
            _terminalCoordinator.TerminalLifecycleSink = (plan, reason, tick) =>
            {
                if (reason == ActionTerminationReason.None) _reactionSystem.EmitPlanCompleted(plan, tick);
                else _reactionSystem.EmitPlanTerminated(plan, tick);
            };

            // 反应命令是阶段 6 的一部分：处理器只把已冻结命令转给系统，绝不重建机会或推导 Tick。
            _planCommandProcessor.ReactionCommandHandler = HandleReactionCommand;

            // 已创建事件只在成功提交的排程事务上发射（失败/预览事务不发射）。
            _planCommandProcessor.CommittedTransactionSink = EmitPlanCreatedEvents;

            // Dodge 终态接缝：只读闭包查询 + 统一终态提交；换位事务端口由任务 06/08 注入。
            // 任务 06 硬前置条件（缺陷 D6）：这里必须复用**排程编辑器持有的同一个求值器实例**，
            // 而不是 `new ScheduleEvaluator(...)` 自建第二个——两者共享同一个路径计算器，
            // 因此路径投影不可能分叉。
            // 任务 06：未注入换位事务端口时绑定的是**真实现**（默认装配），
            // 与 `NotImplementedMovementPathCalculator` 不同，它不会静默恒等。
            _dodgeSeam = new DodgeMovementInvalidationSeam(
                _scheduleAuthority, _scheduleEditor.Evaluator, _terminalCoordinator,
                _reactionSystem, assembly.DodgeRelocationTransaction ?? _dodgeRelocation);

            // UnitId 由初始化器按 EncounterSlotId 的 Ordinal 顺序分配；这里只做运行时复制。
            for (int i = 0; i < initialization.InitialUnits.Count; i++)
            {
                UnitInitialSnapshot initial = initialization.InitialUnits[i];
                var unit = new UnitRuntimeState
                {
                    UnitId = new UnitId(i + 1L),
                    DefinitionId = initial.DefinitionId,
                    FactionId = initial.FactionId,
                    Position = initial.InitialPosition,
                    Facing = initial.InitialFacing,
                    HealthQ10 = QuantizeHealth(initial.InitialHealth),
                    IsAlive = initial.InitialHealth > 0f,
                    AvailableAdrenaline = initial.AvailableAdrenaline,
                    AdrenalineCycleId = initial.AdrenalineCycleId
                };
                // 每单位一个状态机，全部挂在同一个 Outbox 上：不存在"状态机自己的一套事件序号"。
                unit.StateMachine = new UnitStateMachine(unit.UnitId, _outbox);
                _units.Add(unit);
                _unitsById[unit.UnitId.Value] = unit;

                // 任务 06：把活单位注册进唯一的 <c>LogicGrid</c>（所有活单位默认按体积参与
                // Occupancy/Reservation，不因 Allied/Neutral/Hostile、Controller 或玩家标志穿透）。
                // 体积规范表来自**定义级绑定**（UnitDefinition.VolumeSpecId → VolumeSpec.Directions）；
                // 装配方注入的来源优先，否则使用定义投影出的默认来源。
                // 未绑定体积的单位走显式的单格占位降级口径（见 RegisterUnitWithPointFootprint），
                // 该降级不再依赖"生产定义没有绑定"这一事实——它只是可选的兜底。
                IReadOnlyList<DirectionalTriangleSet> volumeDirections =
                    _unitVolumeTables.VolumeDirectionsOf(unit.DefinitionId);
                string registration = volumeDirections == null
                    ? _logicGrid.RegisterUnitWithPointFootprint(unit.UnitId, unit.Position, unit.Facing)
                    : _logicGrid.RegisterUnit(unit.UnitId, unit.Position, unit.Facing, volumeDirections);
                if (registration != null)
                    throw new LogicDefinitionException(registration,
                        "unit=" + unit.UnitId.Value.ToString(CultureInfo.InvariantCulture));
            }

            _window.Clear();

            // 入口注册：外部入口来自已验证定义中的 ControllerBinding；
            // System 入口只有 Logic 内部稳定来源才能创建（优先级 20）。
            if (_encounter.Controllers != null)
            {
                for (int i = 0; i < _encounter.Controllers.Count; i++)
                {
                    _ingress.RegisterExternalEntry(_encounter.Controllers[i]);
                }
            }
            _ingress.RegisterSystemEntry(new ControllerId(SystemControllerId));

            _currentSnapshot = BuildSnapshot(0L);
            InitialStateHash = _currentSnapshot.ComputeHash();
            InitialStateHashHex = CanonicalHash.ToHex(InitialStateHash);
        }

        /// <summary>Logic 内部系统来源的稳定 ControllerId（外部无法注册为 System）。</summary>
        public const string SystemControllerId = "controller.system";

        /// <summary>
        /// 由完整 <see cref="BattleDefinition"/>、Encounter 与 <see cref="BattleRuntimeInputs"/> 创建
        /// 一场新模拟（任务 03B/10 唯一允许的创建路径）。标准装配全部是明确的空实现或最小规则实现。
        /// </summary>
        public static BattleSimulation Create(
            BattleDefinition definition,
            EncounterDefinitionId encounterId,
            BattleRuntimeInputs runtimeInputs)
            => new BattleSimulation(definition, encounterId, runtimeInputs, BattleSimulationAssembly.Standard());

        /// <summary>
        /// 扩展装配重载：供任务 04–11 的内部系统与测试夹具接入既定阶段。
        /// 它<strong>不</strong>改变生命周期、Tick 契约或信任边界，也不构成第二个模拟入口。
        /// </summary>
        public static BattleSimulation Create(
            BattleDefinition definition,
            EncounterDefinitionId encounterId,
            BattleRuntimeInputs runtimeInputs,
            BattleSimulationAssembly assembly)
            => new BattleSimulation(definition, encounterId, runtimeInputs, assembly);

        // —— 只读观察面（不含任何写接口）——

        /// <summary>首个 Step 之前的初始规范化哈希（在创建时冻结，之后不再变化）。</summary>
        public ulong InitialStateHash { get; }

        public string InitialStateHashHex { get; }

        public string RulesVersion => _definition.RulesVersion;

        public string BattleDefinitionHash => _definition.BattleDefinitionHashValue;

        public EncounterDefinitionId EncounterId => _encounterId;

        /// <summary>已完成的最后一个 Tick；-1 表示尚未执行任何 Step。</summary>
        public long Tick => _tick;

        public bool IsEnded => _battleEnd.IsEnded;

        public bool IsDisposed => _disposed;

        public BattleEndSnapshot BattleEnd => _battleEnd;

        /// <summary>整场唯一的只读阵营关系解析器（引用同一条实例，不复制矩阵）。</summary>
        public IFactionRelationResolver FactionResolver => _factionResolver;

        /// <summary>命令入口注册表（生产者提交命令的唯一信任边界）。</summary>
        public CommandIngressRegistry CommandIngress => _ingress;

        /// <summary>历史摘要（RecordCount + Digest；不展开明细）。</summary>
        public HistorySummary History => _history.Summary;

        /// <summary>历史访问计数（无新增归档的 Tick 必须为 0 的证据夹具）。</summary>
        public HistoryAccessCounters HistoryAccessCounters => _history.AccessCounters;

        /// <summary>最近一次 Step 的阶段轨迹（诊断；不参与哈希、不构成恢复状态）。</summary>
        public IReadOnlyList<StepPhaseTraceEntry> LastStepPhaseTrace => _trace.Entries;

        /// <summary>最近一次 Step 的执行阶段（便捷查询）。</summary>
        public bool LastStepExecuted(StepPhase phase) => _trace.Executed(phase);

        /// <summary>阶段轨迹的可读描述（诊断/失败信息）。</summary>
        public string DescribeLastStepPhases() => _trace.Describe();

        /// <summary>
        /// 最近一次构建的规范化快照；首个 Step 之前是"初始状态"快照。
        /// 它是只读观察模型，<strong>不能</strong>作为构造或恢复输入。
        /// </summary>
        public LogicSnapshot CurrentSnapshot => _currentSnapshot;

        /// <summary>缓存的最终快照（战斗结束后由 <c>AlreadyEnded</c> 返回）。</summary>
        public LogicSnapshot FinalSnapshot => _finalSnapshot;

        /// <summary>最近一次投递给决策观察者的只读决策快照（AI 与玩家共用同一实例）。</summary>
        public DecisionSnapshot LastDecisionSnapshot => _lastDecisionSnapshot;

        /// <summary>唯一终态清理的审计报告（未结束时为 null）。</summary>
        public BattleEndFinalizerReport FinalizerReport => _finalizerReport;

        /// <summary>最近一次 Step 冻结批次时记录的 BatchBaseScheduleRevision。</summary>
        public long BatchBaseScheduleRevision => _batchBaseScheduleRevision;

        /// <summary>
        /// 本场战斗<strong>唯一</strong>的全局动作排程权威（注册表 + 全部 Lane + <c>ScheduleRevision</c>）。
        /// 它是只读观察入口：写入口全部是 <c>internal</c>，只能在 <c>ProjectHero.Logic</c> 内调用。
        /// </summary>
        public ActionScheduleAuthority ScheduleAuthority => _scheduleAuthority;

        /// <summary>唯一计划工厂（普通计划创建即 Editable；反应计划创建即 Locked）。</summary>
        public ActionPlanFactory PlanFactory => _planFactory;

        /// <summary>唯一权威排程编辑器（Add/Move/Remove 与系统自动延期的同源求值事务）。</summary>
        public ScheduleEditor ScheduleEditor => _scheduleEditor;

        /// <summary>唯一统一终态协调器（第一次请求胜出、幂等、固定清理顺序）。</summary>
        public ActionPlanTerminalCoordinator TerminalCoordinator => _terminalCoordinator;

        /// <summary>
        /// 任务 06：本场战斗<strong>唯一</strong>的空间权威（占位、稳定区域查询、规范邻居序列与
        /// Reservation）。它只暴露只读查询；写入口全部是 <c>internal</c>/受限方法。
        ///
        /// 它<strong>取代</strong> <c>GridManager</c> 成为新 Logic 路径的占位来源：
        /// <c>GridManager</c> 只保留为过渡期的坐标/场景桥，不再双写权威占位。
        /// </summary>
        public LogicGrid LogicGrid => _logicGrid;

        /// <summary>任务 06：移动段表 + Reservation 事务 + 终态清理参与者（只读观察面）。</summary>
        public LogicGridMovementAuthority MovementAuthority => _movementAuthority;

        /// <summary>
        /// 任务 06：本场战斗绑定的<strong>唯一</strong>移动链路径计算器。
        /// 同一个实例同时喂给排程编辑器内部的求值器与 Dodge 接缝的求值器（同一个求值器实例）。
        /// </summary>
        public IMovementPathCalculator MovementPathCalculator => _movementPathCalculator;

        /// <summary>
        /// 任务 06：<strong>Dodge 目的格预留与换位事务</strong>（反应接受事务的目的格冻结、
        /// TriggerTick 原子换位、来源威胁取消释放、统一只读空间快照）。
        /// </summary>
        public DodgeRelocationAuthority DodgeRelocation => _dodgeRelocation;

        /// <summary>
        /// 阶段 9 的 Dodge 提交报告：<strong>提交前</strong>统一只读空间快照 +
        /// 本阶段全部提交结果（<c>From</c>/<c>Destination</c>/结果）+ <strong>提交后</strong>新位置快照。
        /// 任务 08 用它与同一批冻结 Intent 建立旧/新接触并集。
        /// </summary>
        public DodgeCommitReport LastDodgeCommitReport => _dodgeCommitReport;

        /// <summary>
        /// 本场战斗<strong>唯一</strong>的反应机会系统（机会状态机 + <c>ReactionPlanner</c>）。
        /// 只读观察入口：写路径是"阶段 6 的命令处理器 → <c>TryAcceptById</c>"与
        /// "阶段 7 的原子启动 → <c>TryOpenForTelegraph</c>"。
        /// </summary>
        public ReactionOpportunitySystem ReactionOpportunities => _reactionSystem;

        /// <summary>
        /// 本场战斗<strong>唯一</strong>的 Dodge 终态接缝（只读位置依赖闭包查询 + 统一终态提交）。
        /// 它的换位事务端口由任务 06/08 注入；未注入时提交以稳定码拒绝且零写入。
        /// </summary>
        public DodgeMovementInvalidationSeam DodgeMovementInvalidation => _dodgeSeam;

        /// <summary>本 Tick 新公开的机会（阶段 7；只读诊断，不参与哈希）。</summary>
        public IReadOnlyList<ReactionOpportunityRuntime> OpenedReactionOpportunitiesThisTick
            => _openedOpportunitiesThisTick;

        /// <summary>最近一次强制位移清理中被终止的移动计划（审计；任务 05 接入真实计划后才有内容）。</summary>
        public IReadOnlyList<ActionPlanId> LastInvalidatedPlanIds => _invalidatedPlanIds;

        /// <summary>
        /// 按 <c>UnitId</c> 升序的全部单位状态机（<strong>只读设计视图</strong>）。
        ///
        /// 暴露原因：任务 05 的启动门禁需要读取"当前控制状态、是否有有限结束 Tick、
        /// <c>BlockingUntilTick</c>"这三项<strong>只读事实</strong>。它是状态机的读取入口，
        /// <strong>不是</strong>提交/延期权威：门禁不得据此接受或拒绝计划，
        /// 更不得调用 <c>TryTransition</c> 去"顺手改状态"（那属于动作阶段边界）。
        /// </summary>
        public IReadOnlyList<UnitStateMachine> UnitStateMachines
        {
            get
            {
                var machines = new UnitStateMachine[_units.Count];
                for (int i = 0; i < _units.Count; i++) machines[i] = _units[i].StateMachine;
                return Array.AsReadOnly(machines);
            }
        }

        /// <summary>
        /// 按 <c>UnitId</c> 查询只读状态机视图；未知单位返回 null。
        /// </summary>
        public UnitStateMachine FindUnitStateMachine(UnitId unitId)
            => _unitsById.TryGetValue(unitId.Value, out UnitRuntimeState unit) ? unit.StateMachine : null;

        /// <summary>
        /// 整场战斗中死亡系统发出的<strong>一次性</strong>生命周期清理通知
        /// （按 <c>UnitId</c> 升序、每个单位恰好一条）。
        ///
        /// 任务 05 必须消费它：按 <c>ActionPlanId</c> 经统一终态协调器终止死者全部
        /// <c>Editable</c>/<c>Locked</c>/<c>Running</c> 计划。本属性只读；
        /// 死亡系统自己<strong>不</strong>改任何计划、Intent、MovementSegment、
        /// Reservation 或 ActorLane。
        /// </summary>
        public IReadOnlyList<UnitLifecycleCleanupNotice> LifecycleNotices => _lifecycleNotices;

        /// <summary>最近一次死亡处理中实际从最终 footprint 移除的单位（按 <c>UnitId</c> 升序）。</summary>
        public IReadOnlyList<UnitId> LastDeathFootprintRemovals => _lastFinalFootprintRemovals;

        /// <summary>当前活动持续效果总数（只读诊断；权威集合在 <see cref="CurrentSnapshot"/>.Effects）。</summary>
        public int ActiveEffectCount => _buffSystem.ActiveCount;

        /// <summary>最近一次阶段 1 的效果推进报告（到期/移除/触发/施加；只读诊断）。</summary>
        public EffectAdvanceReport LastEffectAdvanceReport => _buffSystem.LastAdvanceReport;

        /// <summary>
        /// 最近一次 Step 的规范命令集合（只读诊断）：已分配序号的 Envelope、授权拒绝与入口级拒绝。
        /// 它不含任何写接口，也不允许调用者把命令重新塞回模拟。
        /// </summary>
        public FrozenTickCommandSet LastCommandSet => _tickCommandSet;

        /// <summary>
        /// 诊断查询：按记录数读取不可变历史前缀（计数读取/复制，不写权威状态、不影响摘要）。
        /// </summary>
        public HistoryDiagnosticView ReadHistoryPrefix(int recordCount) => _history.BuildDiagnosticView(recordCount);

        /// <summary>诊断用：整段历史的只读拷贝（读取会记入访问计数）。</summary>
        public IReadOnlyList<ArchivedRecord> ReadHistoryForDiagnostics() => _history.CopyAllForDiagnostics();

        /// <summary>诊断重算：从 H0 独立重算完整历史摘要（与增量结果比较的是同一协议）。</summary>
        public ulong RecomputeHistoryDigestForDiagnostics() => _history.RecomputeFullDigestForDiagnostics();

        /// <summary>阶段计时采样器（未装配时为 null；采样结果不参与逻辑与哈希）。</summary>
        public StepPhaseTimingRecorder PhaseTiming => _assembly.PhaseTiming;

        /// <summary>全局排程修订号（成功命令事务与系统自动延期各 +1）。</summary>
        public long ScheduleRevision => _scheduleRevision;

        /// <summary>Tick 0 重演头部（任务 11 消费；任务 03 只冻结契约）。</summary>
        public ReplayHeader BuildReplayHeader()
            => new ReplayHeader(
                ReplayFormat.Version,
                _definition.RulesVersion,
                _definition.BattleDefinitionHashValue,
                _definition.TicksPerSecond,
                _encounterId,
                _runtimeInputs,
                InitialStateHash);

        /// <summary>
        /// 请求结束战斗（<c>End/Stop</c>）。它是<strong>请求</strong>：终止由下一个
        /// <see cref="Step"/> 的阶段 2/16 原子提交，并输出唯一一次 <see cref="BattleEndedEvent"/>。
        /// 重复调用幂等（结果码以第一次为准），释放后调用是无操作。
        /// </summary>
        public void RequestStop(string resultCode = null)
        {
            if (_disposed) return;
            if (_battleEnd.IsEnded) return;
            if (_pendingStopResultCode != null) return;
            _pendingStopResultCode = string.IsNullOrEmpty(resultCode) ? BattleResultCodes.Stopped : resultCode;
        }

        /// <summary>是否已请求结束但尚未在某个 Step 中提交。</summary>
        public bool IsStopRequested => _pendingStopResultCode != null;

        /// <summary>
        /// 释放模拟。重复释放幂等；释放后 <see cref="Step"/> 明确拒绝
        /// （<see cref="SimulationCodes.SIMULATION_DISPOSED"/>），且命令入口不再接受新命令。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ingress.MarkBattleEnded();
        }

        // —— 唯一模拟入口 ——

        /// <summary>
        /// 推进一个 Tick。<paramref name="externalBatch"/> 必须是本场
        /// <see cref="CommandIngressRegistry"/> 在 Step 开始前冻结出的批次。
        /// </summary>
        public StepResult Step(long tick, FrozenCommandBatch externalBatch)
        {
            if (_disposed)
                throw new LogicDefinitionException(SimulationCodes.SIMULATION_DISPOSED,
                    "tick=" + tick.ToString(CultureInfo.InvariantCulture));

            // 战斗结束后的误调用是稳定空操作：不验证新 Tick、不处理命令、不推进任何计数器。
            if (_battleEnd.IsEnded) return BuildAlreadyEndedResult();

            if (tick != _tick + 1L)
                throw new LogicDefinitionException(SimulationCodes.STEP_TICK_NOT_CURRENT,
                    "step.Tick=" + tick.ToString(CultureInfo.InvariantCulture) +
                    " lastCompletedTick=" + _tick.ToString(CultureInfo.InvariantCulture));

            ValidateBatch(tick, externalBatch);

            _trace.Begin(tick, _assembly.PhaseTiming != null);
            _frozenBatch = externalBatch;
            // 主方案 3.4.3：批次冻结与 BatchBaseScheduleRevision 在 Step 入口一起冻结，
            // 阶段 5 只是把它提交为本 Tick 的命令基线（并断言阶段 0–4 未改动排程修订）。
            _batchBaseScheduleRevision = _scheduleRevision;
            // 同时开启排程事务的批次边界（任务 05 的乐观并发）：本 Tick 的全部命令事务共享
            // 这个冻结基线，批内续作不再与本批已递增的实时修订号比较。
            _scheduleEditor.BeginBatch(tick, _batchBaseScheduleRevision);

            // 0. 命令前边界（任务 05/06 接入：移动位置提交、Reservation 释放、自然完成）
            _trace.Enter(StepPhase.PreCommandBoundaries);
            ApplyPreCommandBoundaries(tick);

            // 1. 状态到期与持续效果（任务 04 接入）
            _trace.Enter(StepPhase.StateAndEffectAdvance);
            AdvanceStateAndEffects(tick);

            // 2. 命令前的死亡与胜负检查（任务包「必须产出」6 的第一段）。
            //
            // 本阶段只回答一个问题：**在本 Tick 打开窗口之前战斗是否已经结束？**
            // 判据来自 <see cref="BattleSimulationAssembly.PreCommandVictoryGate"/>；
            // 未装配该扩展点时回落到配置判据 <see cref="EvaluateConfiguredResultCode"/>
            // （只读 FactionId / IsCombatEffective 与 VictoryDefinition 的 Allied/Hostile 目标组）。
            //
            // 冻结结论（修订轮 R1，以代码真实行为为唯一权威）——阶段 2 是**二选一**，
            // 不存在"部分提交"的中间态：
            //   · 判据返回 null（战斗继续）⇒ 阶段 2 **完全不提交**任何死亡事实，
            //     死亡提交留给阶段 15（批量换位与位移事件之后），
            //     因此"位移事件先于死亡事件"在这条路径上成立，
            //     致死单位在该 Tick 的阶段 11–13 强制位移中仍以"在场"身份参与同时求解。
            //   · 判据非 null（战斗已决定）⇒ 阶段 2 立刻用 <see cref="ProcessDeaths"/>
            //     按与阶段 15 **完全相同**的语义**完整提交**死亡（终态 Dead、UnitDiedEvent、
            //     一次性通知、最终 footprint 移除，IsAlive 与 DeathProcessed 在同一处成对写入），
            //     再设 _resolvedResultCode、拒绝已冻结批次并直接进入唯一 Finalizer：
            //     **阶段 3–17 全部不执行**（位移阶段也不会执行）。
            _trace.Enter(StepPhase.DeathAndVictory);
            string preCommandResult = _assembly.PreCommandVictoryGate != null
                ? _assembly.PreCommandVictoryGate.EvaluatePreCommandResult()
                : EvaluateConfiguredResultCode();
            if (preCommandResult != null)
            {
                // 先把本 Tick 的致死单位按阶段 15 的完整语义提交（终态、事件、通知、占位移除），
                // 再关闭窗口与命令路径：这样"命令阶段之前结束"不会让死亡事实丢失。
                _trace.Enter(StepPhase.PostDisplacementDeath);
                ProcessDeaths(tick);

                // 结果码必须来自权威 <see cref="VictoryDefinition"/> 的配置项
                // （胜/败/平三选一），而不是默认的"停止"码——否则"胜负只读 FactionId"
                // 这条契约在"命令前结束"分支上就不成立。
                _resolvedResultCode = preCommandResult;
                _pendingStopResultCode = null;

                // 战斗在命令处理前结束：已冻结的本 Tick 批次必须获得**稳定拒绝码**，
                // 不得静默丢弃（主方案 3.4.3 的 BATTLE_ENDED_BEFORE_COMMAND_PHASE 分支）。
                RejectFrozenBatchBeforeCommandPhase(tick);
                return FinalizeBattleEnd(tick, StepStatus.BattleEnded);
            }

            // 3. 战斗未结束时打开到期窗口（先递增拥有者周期并清零 Available）
            _trace.Enter(StepPhase.WindowOpen);
            OpenDueWindows(tick);

            // 4. 只刷新此前已公开的机会（新 Attack 仍可能被本 Tick 排程编辑移动或删除）
            _trace.Enter(StepPhase.ReactionOpportunityRefresh);
            RefreshExistingReactionOpportunities(tick);

            // 5. 合并已冻结批次并冻结 BatchBaseScheduleRevision
            _trace.Enter(StepPhase.FrozenBatchMergeAndBaseRevision);
            MergeFrozenBatchAndFreezeBaseRevision(tick);

            // 6. scope 校验、原子排程应用、固定 Locked 反应创建、机会截止关闭
            _trace.Enter(StepPhase.CommandValidationAndScheduling);
            ValidateAndApplyCommands(tick);

            // 7. 启动门禁 / 系统自动延期 / 到期固定反应启动与 Trigger
            _trace.Enter(StepPhase.DuePlanStartGateAndReactionTrigger);
            EvaluateStartGatesAndReactionTriggers(tick);

            // 8. 取出本 Tick 到期 Intent
            _trace.Enter(StepPhase.IntentDrain);
            DrainIntents(tick);

            // 9. Dodge 提交前捕获旧格接触，再原子提交到期 Dodge 目的格
            _trace.Enter(StepPhase.LegacyContactCaptureAndDodgeCommit);
            CaptureLegacyContactsAndCommitDodges(tick);

            // 10. 规范化冲突图与 Resolution
            _trace.Enter(StepPhase.ConflictGraphAndResolution);
            BuildConflictGraphAndResolve(tick);

            // 11. 伤害/合力聚合 + 全 Tick 每单位唯一强制位移请求
            _trace.Enter(StepPhase.DamageAggregationAndDisplacementRequests);
            CommitDamageAndBuildForcedDisplacementRequests(tick);

            // 12. 只读临时同时求解（不修改世界）
            _trace.Enter(StepPhase.ForcedDisplacementSolve);
            SolveForcedDisplacementBatch(tick);

            // 13. 计划/Reservation 清理 + 单次批量换位 + 位移事件
            _trace.Enter(StepPhase.PlanReservationCleanupAndBatchCommit);
            CleanupAndCommitRelocationBatch(tick);

            // 14. 状态、控制与其余终态；肾上腺素获得事实与动作后边界
            _trace.Enter(StepPhase.StateControlAndAdrenalineAccrual);
            CommitStateControlAndAdrenalineAccrual(tick);

            // 15. 死亡提交：同 Tick 致死单位已先完成强制位移与批量换位，
            //     因此这里提交终态 Dead、死亡事件、一次性清理通知，并从
            //     **换位后的最终位置**移除 footprint（不改写已提交的换位结果）。
            _trace.Enter(StepPhase.PostDisplacementDeath);
            ProcessDeaths(tick);

            // 16. 再评估胜负
            _trace.Enter(StepPhase.VictoryReevaluation);
            if (ReevaluateVictory(tick)) return FinalizeBattleEnd(tick, StepStatus.BattleEnded);

            // 17. 关闭已请求关闭的窗口并排定下一窗口
            _trace.Enter(StepPhase.WindowCloseAndScheduleNext);
            CloseRequestedWindowAndScheduleNext(tick);

            // 18. 决策快照投递（AI 与玩家看到同一只读快照，只经注册入口投递下一 Tick）
            _trace.Enter(StepPhase.DecisionSnapshotDelivery);
            DeliverDecisionSnapshot(tick);

            // 19. 只读不变量检查 → 归档 → 增量摘要 → EventBatch 与规范化 Snapshot
            _trace.Enter(StepPhase.InvariantCheckArchiveAndOutput);
            _tick = tick;
            // 排程修订号的镜像只在 Step 收尾同步：阶段 5 的基线断言要求阶段 0–4
            // 观察到的是本 Tick 冻结时的值，而阶段 6/7 的提交必须对快照可见。
            SyncScheduleRevision();
            return BuildStepResult(tick, StepStatus.Advanced);
        }

        // —— 各阶段实现（未实现者保持显式空实现/小接口占位，阶段本身不被合并）——

        /// <summary>
        /// 阶段 0（任务 05 真实接入）：命令前边界。
        ///
        /// 固定顺序：
        /// <list type="number">
        /// <item>开始本 Tick 的终态归档候选批次（候选只含本 Tick 的新终态）与批内冲突集合；</item>
        /// <item><strong>消费任务 04 的死亡通知</strong>：锁定死者 Lane 的新提交，并按
        /// <c>ActionPlanId</c> 把死者全部非终态计划（Editable/Locked/Running）经<strong>统一终态协调器</strong>
        /// 以 <c>OwnerDied</c> 终止。该清理<strong>不依赖窗口</strong>。</item>
        /// <item><strong>自然完成</strong>：<c>Running</c> 且已到达 <c>EndTick</c> 的计划经同一协调器
        /// 进入 <c>Completed</c>（没有终止原因）。</item>
        /// </list>
        /// 每单位恰好一条死亡通知 ⇒ 清理天然幂等。
        /// </summary>
        private void ApplyPreCommandBoundaries(long tick)
        {
            _claimedThisTick.Clear();
            _terminalCoordinator.BeginTick(tick);

            // —— 死亡清理（不依赖窗口）——
            for (int i = 0; i < _lifecycleNotices.Count; i++)
            {
                UnitLifecycleCleanupNotice notice = _lifecycleNotices[i];
                if (notice.ReasonCode != UnitLifecycleNoticeReasons.Death) continue;

                _scheduleAuthority.LockLaneSubmissions(notice.UnitId, LaneLockReasonOwnerDied);
                _terminalCoordinator.TerminateAllPlansOfUnit(
                    notice.UnitId, ActionTerminationReason.OwnerDied, tick);
            }

            // —— 命令前边界：移动位置提交（任务 05/06 接入）——
            //
            // 把所有 EndTick <= tick 的 MovementSegment 原子提交到 To 并释放对应 Reservation。
            // 它必须早于本 Tick 的新命令校验，也必须早于"自然完成"（否则同 Tick 结束的计划
            // 会在目的格提交之前就离开活动索引）。提交顺序只由段区间与 (ActionPlanId, StepIndex)
            // 决定，与视觉插值进度完全无关。
            _movementAuthority.ApplyPreCommandBoundary(tick);
            // 提交之后立刻把网格的权威锚点镜像到运行时单位状态：快照、死亡 footprint 移除与
            // 强制位移请求都读同一份位置，因此不存在"网格已提交、快照还停在旧格"的窗口。
            SyncGridLogicalPositions();

            // —— 自然完成：Running 且到达 EndTick（半开区间右端）——
            for (int laneIndex = 0; laneIndex < _scheduleAuthority.Lanes.Count; laneIndex++)
            {
                ActorLane lane = _scheduleAuthority.Lanes[laneIndex];
                IReadOnlyList<ActionPlan> plans = lane.Plans;
                for (int p = 0; p < plans.Count; p++)
                {
                    ActionPlan plan = plans[p];
                    if (!plan.IsRunning) continue;
                    if (plan.EndTick > tick) continue;
                    _terminalCoordinator.EnterCompletion(plan, tick);
                }
            }
        }

        /// <summary>Lane 提交锁定的稳定原因码：所有者死亡（只锁新提交，不清除已有计划）。</summary>
        internal const string LaneLockReasonOwnerDied = "SCHEDULE_LANE_LOCKED_OWNER_DIED";

        /// <summary>Lane 提交锁定的稳定原因码：战斗结束（Finalizer 锁定全部 Lane）。</summary>
        internal const string LaneLockReasonBattleEnded = "SCHEDULE_LANE_LOCKED_BATTLE_ENDED";

        /// <summary>
        /// 阶段 1（任务 04 真实接入）：
        /// <list type="number">
        /// <item>按 <c>UnitId</c> 升序推进每个单位的状态机到期（半开区间
        /// <c>[StartTick, EndTick)</c>：到期 Tick 上先转换、不再停留）；</item>
        /// <item>按 <c>AppliedAtTick -&gt; EffectSequence</c> 推进持续效果
        /// （到期 Tick 先移除、不额外触发）；</item>
        /// <item>最后原子应用扩展点声明的显式转换、生命写入与新效果施加。</item>
        /// </list>
        /// 三段的顺序固定：<strong>自动到期 → 效果推进 → 显式声明</strong>，
        /// 因此"显式转换"在同一个 Tick 内总是最后发生，读到的结束边界是权威值。
        /// 自动状态转换只使用当前逻辑 Tick，不读动画事件或 <c>Update</c> 计时。
        /// </summary>
        private void AdvanceStateAndEffects(long tick)
        {
            IReadOnlyList<UnitSnapshot> view = BuildUnitSnapshots();

            // —— 1. 状态机自动到期（单位顺序固定 UnitId 升序）——
            for (int i = 0; i < _units.Count; i++)
            {
                UnitRuntimeState unit = _units[i];
                if (!unit.IsAlive) continue;
                unit.StateMachine.Advance(tick);
            }

            // —— 2. 持续效果推进（到期先移除，不额外触发；遍历中的增删进延迟队列）——
            _effectHosts.Clear();
            for (int i = 0; i < _units.Count; i++)
            {
                if (_units[i].IsAlive) _effectHosts.Add(_units[i]);
            }
            _buffSystem.AdvanceOrdered(_effectHosts, tick);

            // —— 3. 扩展点声明的显式转换 / 生命写入 / 新效果 ——
            IReadOnlyList<UnitStateAdvanceRequest> requests = _assembly.UnitStateAdvance.AdvanceOrdered(view, tick);
            if (requests == null || requests.Count == 0) return;

            _stateAdvanceRequests.Clear();
            for (int i = 0; i < requests.Count; i++)
            {
                if (requests[i] != null) _stateAdvanceRequests.Add(requests[i]);
            }
            _stateAdvanceRequests.Sort((a, b) => a.UnitId.CompareTo(b.UnitId));

            for (int i = 0; i < _stateAdvanceRequests.Count; i++)
            {
                UnitStateAdvanceRequest request = _stateAdvanceRequests[i];
                if (!_unitsById.TryGetValue(request.UnitId, out UnitRuntimeState unit))
                    throw new LogicDefinitionException(SimulationCodes.STEP_ADVANCE_REQUEST_INVALID,
                        "unknown unit " + request.UnitId.ToString(CultureInfo.InvariantCulture));

                if (request.SetHealthQ10.HasValue)
                {
                    unit.HealthQ10 = request.SetHealthQ10.Value;
                    unit.HealthDirty = true;
                }

                if (request.Transition != null)
                {
                    // 显式转换与自动到期走同一入口：合法性判定、区间计算与事件形状一致。
                    UnitStateMachine.TransitionOutcome outcome = unit.StateMachine.TryTransition(
                        request.Transition, tick, UnitStateTransitionReasons.Explicit);
                    if (!outcome.Applied)
                        throw new LogicDefinitionException(
                            SimulationCodes.STEP_ADVANCE_REQUEST_INVALID,
                            "state transition rejected unit=" + request.UnitId.ToString(CultureInfo.InvariantCulture)
                            + "|" + outcome.From + "->" + outcome.To + "|" + outcome.RejectionCode
                            + "|spec=" + request.Transition);
                }

                if (request.ApplyEffect != null)
                {
                    _buffSystem.Apply(unit, request.ApplyEffect, tick);
                }
            }
        }

        /// <summary>
        /// 命令阶段之前的"战斗是否已经决定"检查（阶段 2），返回<strong>配置的结果码</strong>
        /// （未决定时返回 <c>null</c>）。
        ///
        /// 判据与阶段 16 的最终胜负<strong>同源</strong>：只读取单位的
        /// <c>FactionId</c>、<c>IsAlive</c>、<c>HealthQ10</c> 与 Encounter 的
        /// <see cref="VictoryDefinition"/> Allied/Hostile 目标组；
        /// <strong>不</strong>扫描 <c>FactionDisposition</c>、<strong>不</strong>读取
        /// <c>IsPlayerControlled</c>/Controller 类型/窗口归属。
        ///
        /// 与阶段 16 的差别有两处，都是必要的：
        /// <list type="number">
        /// <item>用 <c>IsCombatEffective</c>（生命 &gt; 0 且未提交死亡）而不是裸 <c>IsAlive</c>：
        /// 本 Tick 的致死伤害发生在阶段 1，而死亡事实要到阶段 15 才提交
        /// （为了让致死单位仍参与同 Tick 的强制位移同时求解）。若这里用 <c>IsAlive</c>，
        /// 命令前判定就会漏掉"本 Tick 刚被打死"的单位。</item>
        /// <item>结果码直接由<strong>配置</strong>推出（Victory/Defeat/Draw 三选一），
        /// 而不经过 <see cref="BattleSimulationAssembly.VictoryEvaluator"/>：
        /// 评估器是阶段 16 的扩展点（可能带策略），本方法只回答"是否已决定、按配置是什么结果"。
        /// 两条路径读取的目标组与存活事实完全一致，因此不会产生分歧。</item>
        /// </list>
        ///
        /// 本方法<strong>不提交</strong>任何死亡事实、不发 <c>BattleEndedEvent</c>；
        /// 死亡提交由调用方紧接着用 <see cref="ProcessDeaths"/> 完成。
        /// </summary>
        private string EvaluateConfiguredResultCode()
        {
            VictoryDefinition rule = _encounter.Victory;
            if (rule == null) return null;
            if (rule.AlliedFactionIds == null || rule.AlliedFactionIds.Count == 0) return null;
            if (rule.HostileFactionIds == null || rule.HostileFactionIds.Count == 0) return null;

            bool alliedEffective = false;
            bool hostileEffective = false;
            bool sawAllied = false;
            bool sawHostile = false;

            for (int i = 0; i < _units.Count; i++)
            {
                UnitRuntimeState unit = _units[i];
                string factionId = unit.FactionId.Value ?? string.Empty;
                if (ContainsFaction(rule.AlliedFactionIds, factionId))
                {
                    sawAllied = true;
                    if (unit.IsCombatEffective) alliedEffective = true;
                }
                else if (ContainsFaction(rule.HostileFactionIds, factionId))
                {
                    sawHostile = true;
                    if (unit.IsCombatEffective) hostileEffective = true;
                }
            }

            if (!sawAllied || !sawHostile) return null;
            if (alliedEffective && hostileEffective) return null;

            // 双方同 Tick 全部失去战斗有效 ⇒ 配置的 Draw（不得用 if 顺序偶然判成胜/败）。
            if (!alliedEffective && !hostileEffective) return rule.DrawResultCode;
            if (!hostileEffective) return rule.VictoryResultCode;
            return rule.DefeatResultCode;
        }

        private static bool ContainsFaction(IReadOnlyList<FactionId> factions, string factionId)
        {
            for (int i = 0; i < factions.Count; i++)
            {
                if (StringComparer.Ordinal.Equals(factions[i].Value, factionId)) return true;
            }
            return false;
        }

        /// <summary>
        /// 死亡系统（<strong>唯一</strong>死亡提交点；终态、事件、一次性通知与 footprint 移除的
        /// 唯一实现，见修订轮 R1：不存在"部分提交"的第二个写点）。
        ///
        /// 冻结契约：
        /// <list type="bullet">
        /// <item><strong>调用点恰好两个</strong>：阶段 15（<c>PostDisplacementDeath</c>，
        /// 即"伤害 → 合力聚合 → 强制位移批量换位 → 位移事件 → 状态/控制"全部提交之后）；
        /// 以及阶段 2 判定"命令前已经决定"时按<strong>完全相同语义</strong>的补跑
        /// （该分支随后直接进入唯一 Finalizer，阶段 3–17 不执行）。两个调用点共享同一段代码，
        /// 因此"每单位恰好一次"由本方法内部保证，不依赖调用次数。</item>
        /// <item><strong>原子提交</strong>：<c>IsAlive = false</c>、<c>DeathProcessed = true</c>、
        /// 终态 <c>Dead</c> 转换、<c>UnitDiedEvent</c>、一次性通知与最终 footprint 移除
        /// 在同一次单位迭代里成对/连续完成 ⇒ 恒有 <c>DeathProcessed == !IsAlive</c>，
        /// 绝不出现"已提交死亡却仍在场"的状态。</item>
        /// <item>死亡事件与同 Tick 位移事件的先后：本方法在阶段 15 被调用时，
        /// 阶段 11–13 的强制位移与位移事件<strong>已经</strong>提交，因此死亡事件严格晚于位移事件；
        /// 若本方法是在阶段 2 被判据命中后被调用，则阶段 3–17（含位移阶段）全部不执行，
        /// 该 Tick 根本不存在位移事件（"致死单位参与同 Tick 位移"仅在命令前判定为假时成立）。</item>
        /// <item>按 <c>UnitId</c> <strong>升序</strong>处理：注册表顺序即 UnitId 升序，
        /// 这里再显式排序一次，使顺序来自契约而不是容器实现。</item>
        /// <item>每个单位整场<strong>只发一次</strong>死亡事件与生命周期清理通知
        /// （<see cref="UnitLifecycleCleanupNotice"/>，供任务 05 消费）：重复调用由
        /// <c>IsKillable</c>（<c>IsAlive &amp;&amp; !DeathProcessed &amp;&amp; HealthQ10 &lt;= 0</c>）
        /// 与整场不清空的 <c>_notifiedLifecycleUnitIds</c> 双重保护，且
        /// <c>_lastFinalFootprintRemovals</c> 每次调用先清空 ⇒ 重入时它是空列表，
        /// 不会重复报告上一 Tick 的移除。</item>
        /// <item>终态 <c>Dead</c> 经统一转换入口（与显式/自动转换同形状）；
        /// <c>IsAlive</c> 在本方法内置假并从最终 footprint 移除，
        /// 通知携带的 <c>FinalPosition</c> 因此是换位后的最终位置。</item>
        /// <item>本方法<strong>不</strong>修改 ActionPlan、Intent、MovementSegment、
        /// Reservation 或 ActorLane；计划从属对象由任务 05 的统一终态协调器按通知清理。</item>
        /// </list>
        /// </summary>
        private void ProcessDeaths(long tick)
        {
            _lastFinalFootprintRemovals.Clear();
            if (_units.Count == 0) return;

            var ordered = new List<UnitRuntimeState>(_units);
            ordered.Sort((a, b) => a.UnitId.Value.CompareTo(b.UnitId.Value));

            for (int i = 0; i < ordered.Count; i++)
            {
                UnitRuntimeState unit = ordered[i];
                if (!unit.IsKillable) continue;

                UnitId unitId = unit.UnitId;
                int remaining = unit.HealthQ10;

                // 存活视图：死亡系统是唯一改写点。
                unit.IsAlive = false;
                unit.DeathProcessed = true;
                unit.HealthDirty = false;
                _lastFinalFootprintRemovals.Add(unitId);

                // 终态 Dead：经统一转换入口，因此与显式/自动转换共享同一套判定与事件形状。
                UnitStateMachine.TransitionOutcome outcome = unit.StateMachine.TryTransition(
                    StateTransitionSpec.Open(UnitState.Dead), tick, UnitStateTransitionReasons.Death);
                if (!outcome.Applied)
                    throw new LogicDefinitionException(SimulationCodes.STEP_INVARIANT_VIOLATION,
                        "death transition rejected unit=" + unitId.Value.ToString(CultureInfo.InvariantCulture)
                        + "|" + outcome.From + "|" + outcome.RejectionCode);

                _outbox.Emit(sequence => new UnitDiedEvent(tick, sequence, unitId, remaining));

                // 一次性生命周期清理通知：供任务 05 按 ActionPlanId 经统一终态协调器
                // 终止死者全部非终态计划。死亡系统自己不动任何计划对象；
                // 通知在换位提交之后创建，携带的是**最终**位置。
                if (!_notifiedLifecycleUnitIds.Add(unitId.Value)) continue;

                var notice = new UnitLifecycleCleanupNotice(
                    tick, unitId, UnitLifecycleNoticeReasons.Death, unit.Position, remaining);
                _lifecycleNotices.Add(notice);
                _assembly.LifecycleNoticeSink?.OnUnitLifecycleNoticeOrdered(notice);
            }
        }

        /// <summary>
        /// 胜负评估（阶段 2 / 阶段 16 共用，且<strong>严格早于</strong>窗口推进）。
        ///
        /// 职责边界（00 号规则 31 / 任务包「必须产出」8 与禁止事项）：
        /// <list type="bullet">
        /// <item>只读取运行时单位的 <c>FactionId</c> 与 Encounter 的 <c>VictoryDefinition</c>
        /// Allied/Hostile 目标组；<strong>不</strong>扫描 <c>FactionDisposition</c> 重建或扩张目标组。</item>
        /// <item><strong>不</strong>读取 <c>IsPlayerControlled</c>、Controller 类型、ControllerBinding 或窗口归属。</item>
        /// <item>只确定 <c>ResultCode</c> 并让本类唯一的 Finalizer 提交；
        /// <strong>不</strong>自行发射 <c>BattleEndedEvent</c>（该事件由 Finalizer 在全部终态清理完成后
        /// 最后发射，且整场只出现一次）。</item>
        /// </list>
        /// </summary>
        private bool ReevaluateVictory(long tick)
        {
            if (_pendingStopResultCode != null)
            {
                _resolvedResultCode = _pendingStopResultCode;
                return true;
            }

            string result = _assembly.VictoryEvaluator.Evaluate(BuildUnitSnapshots(), _encounter.Victory, tick);
            if (result == null) return false;
            _resolvedResultCode = result;
            return true;
        }

        /// <summary>
        /// 命令阶段之前战斗已结束（阶段 2 的死亡/胜负）时的稳定拒绝路径。
        ///
        /// 已冻结的本 Tick 请求<strong>必须</strong>获得可见的拒绝结果，不得静默丢弃：
        /// 每条命令按 <c>CommandSequence</c> 顺序恰好一条
        /// <see cref="CommandRejectedEvent"/>，原因码为
        /// <see cref="CommandCodes.COMMAND_BATTLE_ENDED_BEFORE_COMMAND_PHASE"/>。
        ///
        /// 它<strong>不</strong>运行命令处理器、不写任何权威状态、不分配新序号以外的副作用；
        /// 冻结批次本身（<see cref="LastCommandSet"/>）仍然可读，便于审计。
        /// </summary>
        private void RejectFrozenBatchBeforeCommandPhase(long tick)
        {
            _pendingIngressRejections = _ingress.DrainPendingRejections();
            _tickCommandSet = _gateway.NormalizeAndAssignSequence(
                _frozenBatch, tick, _controllerToUnitIds, _pendingIngressRejections);

            // 入口级拒绝照常公开（它们是入口事实，与战斗是否结束无关）。
            IReadOnlyList<CommandIngressRejection> ingressRejections = _tickCommandSet.IngressRejections;
            for (int i = 0; i < ingressRejections.Count; i++)
            {
                CommandIngressRejection rejection = ingressRejections[i];
                _outbox.Emit(sequence => new CommandIngressRejectedEvent(
                    tick, sequence, rejection.ControllerId, rejection.SourceKind,
                    rejection.SourcePriority, rejection.ProducerOrdinal,
                    rejection.ReasonCode, rejection.CollisionCount));
            }

            IReadOnlyList<CommandEnvelope> envelopes = _tickCommandSet.Envelopes;
            for (int i = 0; i < envelopes.Count; i++)
            {
                long commandSequence = envelopes[i].CommandSequence;
                _outbox.Emit(sequence => new CommandRejectedEvent(
                    tick, sequence, commandSequence, CommandCodes.COMMAND_BATTLE_ENDED_BEFORE_COMMAND_PHASE));
            }
        }

        private void OpenDueWindows(long tick)
        {
            WindowOpenRequest request = _assembly.TurnWindowSchedule.TryOpenDue(tick);
            if (request == null || _window.IsOpen) return;

            // 拥有者已在前述阶段死亡时按稳定顺序跳过，且不得产生该单位的打开事件。
            if (!_unitsById.TryGetValue(request.OwnerUnitId.Value, out UnitRuntimeState owner) || !owner.IsAlive)
                return;

            // 自身窗口打开：先递增个人周期并清零 AvailableAdrenaline（既有预留不取消）。
            owner.AdrenalineCycleId = owner.AdrenalineCycleId + 1L;
            owner.AvailableAdrenaline = 0;

            _window.WindowId = _idGenerator.NextWindowId();
            _window.OwnerUnitId = request.OwnerUnitId;
            _window.OpenedAtTick = tick;
            _window.BudgetTicks = request.BudgetTicks;
            _window.CloseRequested = false;
            _window.IsOpen = true;

            WindowId windowId = _window.WindowId;
            int budget = _window.BudgetTicks;
            UnitId ownerId = _window.OwnerUnitId;
            _outbox.Emit(sequence => new TurnWindowOpenedEvent(tick, sequence, windowId, ownerId, budget));
        }

        private void RefreshExistingReactionOpportunities(long tick)
        {
            // 只刷新<strong>此前已公开</strong>的机会：新 Attack 仍可能被本 Tick 的排程编辑移动或删除，
            // 因此它只能在阶段 7 原子锁定/启动之后才 Telegraph（见 EvaluateStartGatesAndReactionTriggers）。
            //
            // 刷新内容 = "来源失效"：来源攻击已在 TriggerTick 之前进入终态（死亡清理、强制位移
            // 失效、战斗结束前的终态等）⇒ 关闭机会、经统一终态协调器以 SourceThreatCancelled
            // 终止绑定反应，并向任务 07 请求释放未消费预留。
            //
            // 这不是第二套取消逻辑：它与终态协调器的"机会绑定清理参与者"调用的是
            // <strong>同一个</strong> CancelForSourceThreat，并且对已关闭的机会是幂等无操作
            // （参与者负责同 Tick 的即时清理，本处负责跨 Tick 的兜底再校验）。
            IReadOnlyList<ReactionOpportunityRuntime> active = _reactionSystem.ActiveOpportunities;
            if (active.Count == 0) return;

            var invalidSources = new List<ActionPlanId>();
            for (int i = 0; i < active.Count; i++)
            {
                ReactionOpportunityRuntime opportunity = active[i];
                if (!opportunity.IsOpen && opportunity.State != ReactionOpportunityState.Accepted) continue;
                if (tick >= opportunity.TriggerTick) continue;   // TriggerTick 之后由触发/终态路径决定
                ActionPlan source = _scheduleAuthority.Registry.Find(opportunity.SourceAttackPlanId);
                if (source == null || !source.IsTerminal) continue;
                if (!ContainsPlanId(invalidSources, opportunity.SourceAttackPlanId))
                    invalidSources.Add(opportunity.SourceAttackPlanId);
            }

            invalidSources.Sort((a, b) => a.Value.CompareTo(b.Value));
            for (int i = 0; i < invalidSources.Count; i++)
                _reactionSystem.CancelForSourceThreat(invalidSources[i], tick);
        }

        private static bool ContainsPlanId(List<ActionPlanId> ids, ActionPlanId id)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] == id) return true;
            }
            return false;
        }

        private void MergeFrozenBatchAndFreezeBaseRevision(long tick)
        {
            // 阶段 0–4 不允许改动排程修订：批次冻结时记录的命令基线必须仍然有效。
            if (_scheduleRevision != _batchBaseScheduleRevision)
                throw new LogicDefinitionException(SimulationCodes.STEP_PHASE_BASE_REVISION_CHANGED,
                    "base=" + _batchBaseScheduleRevision.ToString(CultureInfo.InvariantCulture) +
                    " current=" + _scheduleRevision.ToString(CultureInfo.InvariantCulture));

            _pendingIngressRejections = _ingress.DrainPendingRejections();
            _tickCommandSet = _gateway.NormalizeAndAssignSequence(
                _frozenBatch, tick, _controllerToUnitIds, _pendingIngressRejections);

            // 入口级拒绝：每个非法规范键恰好一个事件，按组键排序，不回显任何成员载荷。
            IReadOnlyList<CommandIngressRejection> rejections = _tickCommandSet.IngressRejections;
            for (int i = 0; i < rejections.Count; i++)
            {
                CommandIngressRejection rejection = rejections[i];
                _outbox.Emit(sequence => new CommandIngressRejectedEvent(
                    tick, sequence, rejection.ControllerId, rejection.SourceKind,
                    rejection.SourcePriority, rejection.ProducerOrdinal,
                    rejection.ReasonCode, rejection.CollisionCount));
            }
        }

        private void ValidateAndApplyCommands(long tick)
        {
            FrozenTickCommandSet set = _tickCommandSet;
            if (set == null) return;

            for (int i = 0; i < set.RejectedCommands.Count; i++)
            {
                CommandRejectionRecord rejection = set.RejectedCommands[i];
                _outbox.Emit(sequence => new CommandRejectedEvent(
                    tick, sequence, rejection.Envelope.CommandSequence, rejection.ReasonCode));
            }

            // 处理器严格按 CommandSequence 执行（Envelopes 已是规范顺序）。
            // 任务 05 起：排程编辑由权威 ScheduleEditor 事务处理，其余载荷委托给装配处理器。
            _planCommandProcessor.BeginTick();
            IReadOnlyList<CommandRejectionRecord> processorRejections =
                _planCommandProcessor.ProcessOrdered(set.Envelopes, tick, _batchBaseScheduleRevision);

            // 阶段 6 末：反应选项截止。判定条件是 tick > ResponseDeadlineTick，因此
            // "截止 Tick 的命令阶段仍然可以接受"（含端点），过期事件恰好在命令阶段之后、
            // 每个选项只发一次、最后一个开放选项过期才关闭机会。
            _reactionSystem.ExpireDueOptions(tick);

            if (processorRejections != null && processorRejections.Count > 0)
            {
                var ordered = new List<CommandRejectionRecord>(processorRejections.Count);
                for (int i = 0; i < processorRejections.Count; i++)
                {
                    if (processorRejections[i] != null) ordered.Add(processorRejections[i]);
                }
                ordered.Sort((a, b) => a.Envelope.CommandSequence.CompareTo(b.Envelope.CommandSequence));
                for (int i = 0; i < ordered.Count; i++)
                {
                    CommandRejectionRecord rejection = ordered[i];
                    _outbox.Emit(sequence => new CommandRejectedEvent(
                        tick, sequence, rejection.Envelope.CommandSequence, rejection.ReasonCode));
                }
            }
        }

        /// <summary>
        /// 阶段 7（任务 05 真实接入）：到期计划的启动门禁 / 系统自动延期 / 到期固定反应启动。
        ///
        /// 固定顺序与语义：
        /// <list type="number">
        /// <item>按 <c>UnitId -&gt; StartTick -&gt; ActionPlanId</c> 稳定枚举
        /// <c>StartTick == tick</c> 的 <strong>Editable 普通计划</strong>；</item>
        /// <item>逐个经闭合四分门禁求值（<c>Startable</c>/<c>Retryable</c>/<c>Terminal</c>/<c>InvariantViolation</c>）；</item>
        /// <item><c>Startable</c> ⇒ <strong>原子提交</strong>：冻结时序 / <c>Reserved -&gt; Spent</c>（任务 07 端口）
        /// / <c>LockedAtTick</c> / <c>Running</c> 一次完成，失败则整体失败；</item>
        /// <item><c>Retryable</c> ⇒ 用<strong>同一 <c>ScheduleEvaluator</c></strong> 把计划移到
        /// <c>RetryAtTick</c>（保持 Editable 与未消费预留，只向右 ripple，修订号恰好 +1）；</item>
        /// <item><c>Terminal</c> ⇒ 经统一终态协调器终止（释放锁定前预留，<strong>不</strong>增加修订号）；</item>
        /// <item><c>InvariantViolation</c> ⇒ 抛稳定不变量错误令 Step 失败（<strong>不</strong>降级为普通终态）。</item>
        /// </list>
        /// 因此阶段边界上永远观察不到"Locked 但未 Running"的普通计划。
        /// </summary>
        private void EvaluateStartGatesAndReactionTriggers(long tick)
        {
            _openedOpportunitiesThisTick.Clear();
            List<ActionPlan> due = CollectDueEditablePlans(tick);
            if (due.Count == 0) return;

            // 任务 06：把本 Tick 到期 Move 的声明的目的格登记为"待进入格"，
            // 供空间门禁查询区分 Free / RetryableTimedBlock / TerminalOrUnknownBlock。
            for (int i = 0; i < due.Count; i++)
            {
                ActionPlan candidate = due[i];
                if (candidate.Destination.HasValue)
                    _logicGrid.SetPendingDestination(candidate.OwnerUnitId, candidate.Destination);
            }

            var context = new ActionStartGateContext(
                tick,
                BlockingUntilTickOf,
                IsUnitAlive,
                _planFactory,
                _definition.Rules.MaxAutomaticDeferralsPerPlan,
                isActorAvailable: null,
                // 任务 06 的限时空间阻塞：只有来自权威占位/Reservation 区间且 ReleaseTick 有限、
                // 严格晚于当前 Tick 的阻塞才返回有限值；无界占位/非法格返回 long.MaxValue，
                // 门禁据此返回 Terminal(ActorUnavailableAtStart) 而不是每 Tick 盲重试。
                spaceBlockUntilTickOf: _movementAuthority.SpaceBlockUntilTickOf(tick));

            for (int i = 0; i < due.Count; i++)
            {
                ActionPlan plan = due[i];
                if (!plan.IsEditable || plan.StartTick != tick) continue;   // 本 Tick 内已被前一项改写

                ActionStartGateResult result = _startGate.Evaluate(plan, context);
                switch (result)
                {
                    case ActionStartGateResult.Startable startable:
                        CommitStart(startable.Plan, tick);
                        // 原子锁定/启动成功之后才 Telegraph：TelegraphTick 首版 = StartTick = 本 Tick。
                        // 仍为 Editable 的攻击在结构上无法公开机会（系统自身再校验一次）。
                        _reactionSystem.TryOpenForTelegraph(startable.Plan, tick, _openedOpportunitiesThisTick);
                        break;

                    case ActionStartGateResult.Retryable retryable:
                        ApplyAutoDeferral(retryable, tick);
                        break;

                    case ActionStartGateResult.Terminal terminal:
                        _terminalCoordinator.EnterTerminal(terminal.Plan, terminal.Reason, tick);
                        break;

                    case ActionStartGateResult.InvariantViolation violation:
                        throw new LogicDefinitionException(violation.ErrorCode, violation.Detail);

                    default:
                        throw new LogicDefinitionException(
                            ScheduleCodes.SCHEDULE_START_GATE_COMMIT_INCONSISTENT, "unknown gate result");
                }
            }
        }

        /// <summary>
        /// 原子启动提交：门禁通过后<strong>一次</strong>完成资源消费与
        /// <c>Locked -&gt; Running</c>。任一步失败即整体失败并回滚资源，绝不留下
        /// Locked-but-not-Running。
        /// </summary>
        private void CommitStart(ActionPlan plan, long tick)
        {            if (!plan.IsEditable || plan.StartTick != tick)
            {
                throw new LogicDefinitionException(
                    ScheduleCodes.SCHEDULE_START_GATE_COMMIT_INCONSISTENT,
                    "plan=" + plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
            }

            long previousLockedAtTick = plan.LockedAtTick;
            int previousReserved = plan.ReservedTurnBudgetTicks;
            ActionPlanState previousState = plan.State;

            string error = _startCommitPort.Commit(plan, tick, rollback =>
            {
                rollback.ReservedTurnBudgetTicks = previousReserved;
            });

            if (error != null)
            {
                // 端口负责回滚它自己的副作用；这里恢复计划字段并令 Step 失败。
                plan.ReservedTurnBudgetTicks = previousReserved;
                plan.State = previousState;
                plan.LockedAtTick = previousLockedAtTick;
                throw new LogicDefinitionException(
                    ScheduleCodes.SCHEDULE_START_GATE_COMMIT_INCONSISTENT, error);
            }

            // 同一原子提交内建立 Locked 边界并立即 Running（不存在可观察的中间态）。
            plan.State = ActionPlanState.Running;
            plan.LockedAtTick = tick;
            plan.ReservedTurnBudgetTicks = 0;
            _claimedThisTick.Add(plan.ActionPlanId.Value);

            // 语义事件：锁定/启动事件与原子提交同一次发生，且只在此处发射。
            _reactionSystem.EmitPlanLocked(plan, tick);
        }

        /// <summary>
        /// 系统自动延期：复用<strong>同一 <c>ScheduleEvaluator</c></strong> 把到期计划整体推到
        /// <c>RetryAtTick</c> 并只向右 ripple 后续 Editable 依赖闭包。
        ///
        /// 成功：保持 Editable/未消费预留，<c>AutomaticDeferralCount</c> 与 <c>ScheduleRevision</c>
        /// 各 +1，<c>LastRequestedStartTick</c> <strong>不变</strong>。
        /// 失败：经统一终态协调器以冻结原因终止（释放锁定前预留，<strong>不</strong>增加修订号）。
        /// </summary>
        private void ApplyAutoDeferral(ActionStartGateResult.Retryable retryable, long tick)
        {
            ActionPlan plan = retryable.Plan;
            var claimed = new List<ActionPlanId>(_planCommandProcessor.ClaimedPlanIds.Count);
            foreach (long id in _planCommandProcessor.ClaimedPlanIds) claimed.Add(new ActionPlanId(id));

            long oldStartTick = plan.StartTick;
            ScheduleEditTransactionResult deferred = _scheduleEditor.ApplySystemAutoDeferral(
                plan, retryable.RetryAtTick, tick, claimed);

            if (deferred.Committed)
            {
                // 事件载荷 = 阻塞原因码 / 旧新 StartTick / RetryAtTick / 次数 / 稳定 ripple 列表 / 提交后修订号。
                _reactionSystem.EmitPlanAutoDeferred(
                    plan, retryable.BlockerCode, oldStartTick, retryable.RetryAtTick,
                    ToLongIds(deferred.RipplePlanIds), tick);
                return;
            }

            ActionTerminationReason reason = deferred.RejectionCode == ScheduleCodes.SCHEDULE_HORIZON_EXCEEDED
                ? ActionTerminationReason.AutoDeferralLimitExceeded
                : ActionTerminationReason.ActorUnavailableAtStart;
            _terminalCoordinator.EnterTerminal(plan, reason, tick);
        }

        private static IReadOnlyList<long> ToLongIds(IReadOnlyList<ActionPlanId> ids)
        {
            if (ids == null || ids.Count == 0) return Array.Empty<long>();
            var result = new long[ids.Count];
            for (int i = 0; i < ids.Count; i++) result[i] = ids[i].Value;
            return result;
        }

        /// <summary>
        /// 已创建事件：只对<strong>成功提交</strong>的排程事务新增的计划发射（一次一个）。
        /// 计划对象从权威注册表读取，事件只携带不可变值与稳定 ID。
        /// </summary>
        private void EmitPlanCreatedEvents(ScheduleEditTransactionResult result, long tick)
        {
            if (result == null || result.AddedPlanIds == null) return;
            for (int i = 0; i < result.AddedPlanIds.Count; i++)
            {
                ActionPlan plan = _scheduleAuthority.Registry.Find(result.AddedPlanIds[i]);
                if (plan == null) continue;
                _reactionSystem.EmitPlanCreated(plan, tick);
            }
        }

        /// <summary>
        /// 阶段 6 的反应命令路径：把已冻结的 <c>ReactionCommandPayload</c> 交给唯一机会系统。
        ///
        /// 冻结语义：
        /// <list type="bullet">
        /// <item>命令<strong>不提供</strong> <c>TriggerTick</c>（它等于来源攻击的 <c>ImpactTick</c>），
        /// 也不提供单位：防御者由机会自身决定（"谁能替该单位提交"是任务 07 的提交权限）；</item>
        /// <item>命令的 <c>DodgeDestination</c> 落到计划的目的格字段，供任务 06/08 的 TriggerTick
        /// 换位事务使用；本任务<strong>不</strong>换位、<strong>不</strong>伪造触发；</item>
        /// <item>返回 null = 已接受；否则返回稳定拒绝码（处理器据此发 <c>CommandRejectedEvent</c>）。</item>
        /// </list>
        /// </summary>
        private string HandleReactionCommand(CommandEnvelope envelope, long tick)
        {
            if (envelope?.Request == null) return CommandCodes.COMMAND_REQUEST_NULL;
            if (!(envelope.Request.Scope is ReactionCommandScope scope)) return CommandCodes.SCOPE_PAYLOAD_MISMATCH;
            if (!(envelope.Request.Payload is ReactionCommandPayload payload)) return CommandCodes.SCOPE_PAYLOAD_MISMATCH;

            // 载荷种类必须与动作定义一致（Dodge 只能提交 Dodge，Block 只能提交 Block）。
            ActionSpec spec = _definition.FindAction(payload.ReactionActionSpecId);
            if (spec == null) return ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND;
            bool isDodge = spec.Type == ActionType.Dodge;
            if (spec.Type != ActionType.Block && !isDodge) return ReactionCodes.REACTION_ACTION_NOT_BLOCK_OR_DODGE;
            if (isDodge != (payload.ReactionKind == ReactionCommandKind.Dodge))
                return ReactionCodes.REACTION_ACTION_NOT_BLOCK_OR_DODGE;

            string error = _reactionSystem.TryAcceptByIdWithCommandSequence(
                scope.ReactionOpportunityId, payload.ReactionActionSpecId, envelope.CommandSequence, tick,
                isDodge ? payload.DodgeDestination : null, out ActionPlan plan);
            if (error != null) return error;

            _reactionSystem.EmitPlanCreated(plan, tick);
            return null;
        }

        /// <summary>按 <c>UnitId -&gt; StartTick -&gt; ActionPlanId</c> 稳定枚举本 Tick 到期、仍为 Editable 的普通计划。</summary>
        private List<ActionPlan> CollectDueEditablePlans(long tick)
        {
            var due = new List<ActionPlan>();
            var lanes = new List<ActorLane>(_scheduleAuthority.Lanes);
            lanes.Sort((a, b) => a.UnitId.Value.CompareTo(b.UnitId.Value));
            for (int i = 0; i < lanes.Count; i++)
            {
                IReadOnlyList<ActionPlan> plans = lanes[i].Plans;
                for (int p = 0; p < plans.Count; p++)
                {
                    ActionPlan plan = plans[p];
                    if (!plan.IsEditable || !plan.IsOrdinary) continue;
                    if (plan.StartTick != tick) continue;
                    due.Add(plan);
                }
            }
            due.Sort((a, b) =>
            {
                int byUnit = a.OwnerUnitId.Value.CompareTo(b.OwnerUnitId.Value);
                if (byUnit != 0) return byUnit;
                int byStart = a.StartTick.CompareTo(b.StartTick);
                return byStart != 0 ? byStart : a.ActionPlanId.Value.CompareTo(b.ActionPlanId.Value);
            });
            return due;
        }

        /// <summary>单位当前状态的权威有限阻塞边界（<c>null</c> = 当前状态不构成有限阻塞）。</summary>
        private long? BlockingUntilTickOf(UnitId unitId)
        {
            UnitStateMachine machine = FindUnitStateMachine(unitId);
            return machine == null ? (long?)null : machine.BlockingUntilTick;
        }

        private bool IsUnitAlive(UnitId unitId)
            => _unitsById.TryGetValue(unitId.Value, out UnitRuntimeState unit) && unit.IsAlive;


        private void DrainIntents(long tick)
        {
            // 任务 08：取出本 Tick 全部到期 Intent（排序键 Tick -&gt; InteractionPriority -&gt; IntentSequence）。
        }

        /// <summary>
        /// 阶段 9（<see cref="StepPhase.LegacyContactCaptureAndDodgeCommit"/>）：
        /// <strong>提交前先冻结统一只读空间快照</strong>，再按规范顺序原子提交全部到期的 Dodge 目的格。
        ///
        /// 这个阶段是任务 08 构图（阶段 10）<strong>之前</strong>的固定阶段，因此
        /// "Dodge 换位早于同 Tick 接触复核"是阶段顺序本身保证的，不是调用点各自记得先调。
        /// 本阶段<strong>不</strong>把攻击标为 Dodged：接触复核由任务 08 用同一批冻结 Intent
        /// 建立旧/新接触并集完成。
        /// </summary>
        private void CaptureLegacyContactsAndCommitDodges(long tick)
        {
            // 1. 统一只读空间快照（任何 Dodge 提交之前）。任务 08 稍后用它与冻结 Intent 建立旧接触。
            DodgeSpaceSnapshot before = _dodgeRelocation.CaptureSpaceSnapshot(tick);

            // 2. 全部到期预留，按 (TriggerTick, Destination.X, Destination.Y, CommandSequence, OpportunityId) 升序。
            IReadOnlyList<DodgeDestinationReservation> due = _dodgeRelocation.DueReservationsOrdered(tick);
            int logStart = _dodgeRelocation.CommitLog.Count;

            for (int i = 0; i < due.Count; i++)
            {
                DodgeDestinationReservation reservation = due[i];
                ActionPlan plan = _scheduleAuthority.Registry.Find(reservation.ReactionPlanId);
                if (plan == null || plan.IsTerminal)
                {
                    // 计划已不在（终态/回滚）：目的格预留没有消费者，一次性释放（幂等）。
                    _dodgeRelocation.ReleaseDestination(reservation.OpportunityId);
                    continue;
                }

                // 唯一的换位入口：试换位 → （成功才）统一终态失效依赖移动 → 触发确认。
                _dodgeSeam.TryCommit(plan, reservation.OpportunityId, tick);
            }

            // 3. 提交后快照 + 本阶段全部提交结果（From/Destination/结果/新位置）。
            _dodgeCommitReport = _dodgeRelocation.BuildReport(
                tick, before, _dodgeRelocation.CommitLogSince(logStart));
            // Dodge 换位也是权威位置提交：同一 Tick 的后续阶段必须看到换位后的逻辑格。
            SyncGridLogicalPositions();
        }

        /// <summary>
        /// 把 <see cref="LogicGrid"/> 的权威锚点/朝向<strong>单向镜像</strong>到运行时单位状态。
        ///
        /// 只在权威提交边界之后调用（阶段 0 的移动提交、阶段 9 的 Dodge 换位、阶段 13 的批量换位），
        /// 因此<strong>不存在双写</strong>：<c>LogicGrid</c> 永远是唯一的位置权威，
        /// 这里的复制只是让"读 <c>unit.Position</c> 的既有消费者"（快照、死亡 footprint 移除、
        /// 强制位移请求、胜利评估）看到同一个事实。
        /// </summary>
        private void SyncGridLogicalPositions()
        {
            for (int i = 0; i < _units.Count; i++)
            {
                UnitRuntimeState unit = _units[i];
                if (_logicGrid.TryGetAnchor(unit.UnitId, out GridPoint anchor)) unit.Position = anchor;
                if (_logicGrid.TryGetFacing(unit.UnitId, out GridDirection facing)) unit.Facing = facing;
            }
        }

        private void BuildConflictGraphAndResolve(long tick)
        {
            // 任务 08：构建规范化冲突图并按连通分量计算 Resolution。
        }

        private void CommitDamageAndBuildForcedDisplacementRequests(long tick)
        {
            _assembly.ResolutionCommit.CommitDamageAndAggregationOrdered(tick, BuildUnitSnapshots());

            IReadOnlyList<ForcedDisplacementRequest> requests =
                _assembly.DisplacementRequestBuilder.BuildOrdered(tick, BuildUnitSnapshots());
            if (requests == null || requests.Count == 0)
            {
                _displacementRequests = Array.Empty<ForcedDisplacementRequest>();
                return;
            }

            var ordered = new List<ForcedDisplacementRequest>(requests.Count);
            for (int i = 0; i < requests.Count; i++)
            {
                if (requests[i] != null) ordered.Add(requests[i]);
            }
            ordered.Sort((a, b) => a.TargetUnitId.CompareTo(b.TargetUnitId));

            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i - 1].TargetUnitId == ordered[i].TargetUnitId)
                    throw new LogicDefinitionException(SimulationCodes.STEP_DISPLACEMENT_REQUEST_DUPLICATE,
                        ordered[i].TargetUnitId.ToString(CultureInfo.InvariantCulture));
            }

            _displacementRequests = ordered;
        }

        private void SolveForcedDisplacementBatch(long tick)
        {
            if (_displacementRequests.Count == 0)
            {
                _displacementBatch = ForcedDisplacementBatch.Empty;
                return;
            }

            // 只读快照 + 临时占位空间：本阶段不修改世界，也不调用普通移动/寻路/Reservation 仲裁。
            _displacementBatch = _assembly.DisplacementSolver.ResolveAll(
                _displacementRequests, BuildUnitSnapshots(), _encounter.GridBoundary, tick)
                ?? ForcedDisplacementBatch.Empty;
        }

        private void CleanupAndCommitRelocationBatch(long tick)
        {
            ForcedDisplacementBatch batch = _displacementBatch;
            if (batch == null || batch.IsEmpty) return;

            // 先经统一终态协调器终止被破坏的移动计划与 Reservation。
            _invalidatedPlanIds = _assembly.DisplacementCommitter
                .TerminateInvalidatedMovementPlansOrdered(batch, tick) ?? Array.Empty<ActionPlanId>();

            // 再一次批量验证 + 一次批量换位：任一批量验证失败都是 InvariantViolation，
            // 禁止退化为逐单位提交。
            ApplyBatchRelocation(batch);

            var ordered = new List<ForcedDisplacementRelocation>(batch.Relocations);
            ordered.Sort((a, b) => a.TargetUnitId.CompareTo(b.TargetUnitId));
            for (int i = 0; i < ordered.Count; i++)
            {
                ForcedDisplacementRelocation relocation = ordered[i];
                _outbox.Emit(sequence => new ForcedDisplacementResolvedEvent(
                    tick, sequence, new UnitId(relocation.TargetUnitId),
                    new GridPoint(relocation.FromX, relocation.FromY),
                    new GridPoint(relocation.ToX, relocation.ToY),
                    relocation.Direction, relocation.RequestedSteps, relocation.AppliedSteps,
                    relocation.MomentumUnits, relocation.ConflictGroupKey, relocation.StopReason,
                    relocation.InvalidatedPlanIds ?? Array.Empty<ActionPlanId>()));
            }
        }

        private void ApplyBatchRelocation(ForcedDisplacementBatch batch)
        {
            var destinations = new HashSet<long>();
            var moving = new HashSet<long>();

            for (int i = 0; i < batch.Relocations.Count; i++)
            {
                ForcedDisplacementRelocation relocation = batch.Relocations[i];
                if (!_unitsById.TryGetValue(relocation.TargetUnitId, out UnitRuntimeState unit))
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "unknown unit " + relocation.TargetUnitId.ToString(CultureInfo.InvariantCulture));

                if (unit.Position.X != relocation.FromX || unit.Position.Y != relocation.FromY)
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "from mismatch unit=" + relocation.TargetUnitId.ToString(CultureInfo.InvariantCulture));

                if (!GridPoint.IsValidParity(relocation.ToX, relocation.ToY))
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "invalid parity unit=" + relocation.TargetUnitId.ToString(CultureInfo.InvariantCulture));

                var destination = new GridPoint(relocation.ToX, relocation.ToY);
                if (!_encounter.GridBoundary.Contains(destination))
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "out of boundary unit=" + relocation.TargetUnitId.ToString(CultureInfo.InvariantCulture));

                if (relocation.AppliedSteps < 0 || relocation.AppliedSteps > relocation.RequestedSteps)
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "applied steps unit=" + relocation.TargetUnitId.ToString(CultureInfo.InvariantCulture));

                long destinationKey = ((long)relocation.ToX << 32) ^ (uint)relocation.ToY;
                if (!destinations.Add(destinationKey))
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "duplicate destination " + relocation.ToX.ToString(CultureInfo.InvariantCulture) + "," +
                        relocation.ToY.ToString(CultureInfo.InvariantCulture));

                moving.Add(relocation.TargetUnitId);
            }

            // 静止单位阻挡：目的地不得被不在本批中的存活单位占据。
            for (int i = 0; i < _units.Count; i++)
            {
                UnitRuntimeState unit = _units[i];
                if (!unit.IsAlive || moving.Contains(unit.UnitId.Value)) continue;
                long key = ((long)unit.Position.X << 32) ^ (uint)unit.Position.Y;
                if (destinations.Contains(key))
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "destination occupied by stationary unit " + unit.UnitId.Value.ToString(CultureInfo.InvariantCulture));
            }

            // 任务 06：同一次批量换位必须**也**提交到唯一空间权威（LogicGrid）。
            // 在此之前这里只写 unit.Position，会让网格锚点与运行时位置分叉，
            // 而阶段 0 的"网格 → 单位"镜像就会在下一 Tick 把换位回退掉。
            // 网格侧做的是与上面同一套全批预检（ExpectedFrom / 目标 footprint / 静止单位 / 待抢占预留），
            // 因此这里不是"第二条验证路径"，而是把同一次提交写到唯一权威上。
            var gridRelocations = new List<BatchRelocation>(batch.Relocations.Count);
            for (int i = 0; i < batch.Relocations.Count; i++)
            {
                ForcedDisplacementRelocation relocation = batch.Relocations[i];
                gridRelocations.Add(new BatchRelocation(
                    new UnitId(relocation.TargetUnitId),
                    new GridPoint(relocation.FromX, relocation.FromY),
                    new GridPoint(relocation.ToX, relocation.ToY)));
            }
            string gridError = _logicGrid.ApplyBatchRelocation(gridRelocations);
            if (gridError != null)
                throw new LogicDefinitionException(gridError, "forced-displacement batch");

            // 全部验证通过后一次性写入新 footprint，并在写入前统一移除旧 footprint。
            for (int i = 0; i < batch.Relocations.Count; i++)
            {
                ForcedDisplacementRelocation relocation = batch.Relocations[i];
                UnitRuntimeState unit = _unitsById[relocation.TargetUnitId];
                unit.Position = new GridPoint(relocation.ToX, relocation.ToY);
            }
        }

        private void CommitStateControlAndAdrenalineAccrual(long tick)
        {
            // 阶段 14 只能读取批量换位<em>之后</em>的最终位置。
            _assembly.ResolutionCommit.CommitStateControlAndRemainingTerminalsOrdered(tick, BuildUnitSnapshots());
        }

        private void CloseRequestedWindowAndScheduleNext(long tick)
        {
            if (!_window.IsOpen) return;
            if (!_assembly.TurnWindowSchedule.ShouldCloseCurrentWindow(tick)) return;
            CloseWindow(tick, TurnWindowCloseReason.OwnerRequested);
        }

        private void CloseWindow(long tick, TurnWindowCloseReason reason)
        {
            if (!_window.IsOpen) return;
            WindowId windowId = _window.WindowId;
            UnitId ownerId = _window.OwnerUnitId;
            _window.Clear();
            _outbox.Emit(sequence => new TurnWindowClosedEvent(tick, sequence, windowId, ownerId, reason));
        }

        private void DeliverDecisionSnapshot(long tick)
        {
            IReadOnlyList<IDecisionObserver> observers = _assembly.DecisionObservers;
            if (observers.Count == 0) return;

            DecisionSnapshot snapshot = BuildDecisionSnapshot(tick);
            _lastDecisionSnapshot = snapshot;
            long nextTick = checked(tick + 1L);
            for (int i = 0; i < observers.Count; i++)
            {
                observers[i]?.ObserveOrdered(snapshot, _ingress, nextTick);
            }
        }

        // —— 唯一终态清理（幂等、固定顺序，不建第二套计划清理路径）——

        private StepResult FinalizeBattleEnd(long tick, StepStatus status)
        {
            string resultCode = _resolvedResultCode ?? BattleResultCodes.Stopped;
            _resolvedResultCode = null;

            int clearedFacts = CountFutureTickBucketFacts();
            _ingress.MarkBattleEnded();

            bool closedWindow = false;
            if (_window.IsOpen)
            {
                CloseWindow(tick, TurnWindowCloseReason.BattleEnded);
                closedWindow = true;
            }

            // 任务 05：战斗结束复用<strong>同一个</strong>统一终态协调器，
            // 按 UnitId -> ActionPlanId 锁定全部 Lane 并终止全部非终态计划。
            // 这里<strong>不</strong>复制一套计划清理逻辑。
            _terminalCoordinator.BeginTick(tick);
            _scheduleAuthority.LockAllLaneSubmissions(LaneLockReasonBattleEnded);
            _terminalCoordinator.TerminateAllPlans(ActionTerminationReason.BattleEnded, tick);

            // 战斗结束也关闭全部仍未关闭的机会（<strong>同一个</strong> Finalizer，
            // 不另写一套机会清理）；已接受但未触发的绑定反应已随上面统一终止。
            _reactionSystem.CloseAllForBattleEnd(tick);
            SyncScheduleRevision();

            _pendingStopResultCode = null;
            _battleEnd = new BattleEndSnapshot(true, tick, resultCode);
            _finalizerReport = new BattleEndFinalizerReport(tick, resultCode, clearedFacts, closedWindow, AlreadyFinalized: false);

            // BattleEndedEvent 必须是该 Tick 最后一个逻辑事件，且整场只出现一次。
            string code = resultCode;
            _outbox.Emit(sequence => new BattleEndedEvent(tick, sequence, code));

            _trace.Enter(StepPhase.InvariantCheckArchiveAndOutput);
            _tick = tick;
            SyncScheduleRevision();
            StepResult result = BuildStepResult(tick, status);
            _finalSnapshot = result.Snapshot;
            return result;
        }

        private int CountFutureTickBucketFacts()
        {
            CommandIngressRegistrySnapshot snapshot = _ingress.CaptureSnapshot();
            int total = 0;
            for (int i = 0; i < snapshot.FutureBuckets.Count; i++) total += snapshot.FutureBuckets[i].PendingCount;
            return total;
        }

        private StepResult BuildAlreadyEndedResult()
            => new StepResult(StepStatus.AlreadyEnded, EventBatch.Empty(_battleEnd.EndedAtTick), _finalSnapshot);

        private StepResult BuildStepResult(long tick, StepStatus status)
        {
            // 1. 只读不变量检查（显式装配、顺序固定、不得修改状态、不得发射玩法事件）。
            IReadOnlyList<UnitSnapshot> units = BuildUnitSnapshots();
            IReadOnlyList<IStepInvariantCheck> checks = _assembly.InvariantChecks;
            for (int i = 0; i < checks.Count; i++)
            {
                IStepInvariantCheck check = checks[i];
                if (check == null) continue;
                string failure = check.CheckOrdered(tick, units);
                if (failure != null)
                    throw new LogicDefinitionException(SimulationCodes.STEP_INVARIANT_VIOLATION,
                        "check#" + i.ToString(CultureInfo.InvariantCulture) + ":" + failure);
            }

            // 1b. 任务 05 的 Step 末不变量：没有任何活动对象引用终态计划。
            //     它同时检出 Lane 内重叠与"活动索引持有终态计划"，失败即令 Step 失败（不只是断言）。
            string timelineFailure = _scheduleAuthority.CheckNoActiveArtifactReferencesTerminalPlan();
            if (timelineFailure != null)
                throw new LogicDefinitionException(SimulationCodes.STEP_INVARIANT_VIOLATION, timelineFailure);

            // 1c. 本 Tick 的终态归档候选在此刻定序（清理与只读检查都已完成）。
            _terminalCoordinator.EndTick(tick);

            // 2. 清理与只读检查完成之后，仅归档本 Tick 新冻结的增量候选。
            ArchiveFinalizedCandidates(tick);

            // 3. 事件在 Tick 全部提交后一次性可见。
            EventBatch eventBatch = _outbox.Flush(tick);

            // 4. 完整活动状态 + HistorySummary；不复制已归档明细。
            LogicSnapshot snapshot = BuildSnapshot(tick);
            _currentSnapshot = snapshot;

            // 5. 可选的阶段计时采样（纯诊断，不参与逻辑与哈希）。
            _assembly.PhaseTiming?.RecordTick(_trace, _trace.EndTimestamp());
            return new StepResult(status, eventBatch, snapshot);
        }

        private void ArchiveFinalizedCandidates(long tick)
        {
            // 阶段 19 的归档候选来源 = 装配里显式给出的来源 + 任务 05 的终态计划来源。
            // 两者共享<strong>同一个</strong>HistoryArchive 与同一份封条契约；
            // 无新增终态时本来源返回空列表 ⇒ 归档器不读取、不复制、不哈希任何旧记录。
            IReadOnlyList<HistoryArchiveCandidate> fromTimeline = _terminalArchiveSource.CollectOrdered(tick);

            IReadOnlyList<IHistoryArchiveCandidateSource> sources = _assembly.ArchiveCandidateSources;
            if (sources.Count == 0 && fromTimeline.Count == 0)
            {
                // 空批次：不读取、不复制、不哈希任何旧记录。
                _history.AppendFinalizedOrdered(tick, null);
                return;
            }

            var candidates = new List<HistoryArchiveCandidate>();
            for (int i = 0; i < sources.Count; i++)
            {
                IReadOnlyList<HistoryArchiveCandidate> fromSource = sources[i]?.CollectOrdered(tick);
                if (fromSource == null) continue;
                for (int c = 0; c < fromSource.Count; c++)
                {
                    if (fromSource[c] != null) candidates.Add(fromSource[c]);
                }
            }
            for (int i = 0; i < fromTimeline.Count; i++)
            {
                if (fromTimeline[i] != null) candidates.Add(fromTimeline[i]);
            }

            _history.AppendFinalizedOrdered(tick, candidates);
        }

        /// <summary>
        /// 任务 05：把权威排程修订号同步到本模拟的镜像字段。
        /// 阶段 5 的硬断言（<c>STEP_PHASE_BASE_REVISION_CHANGED</c>）依赖
        /// "阶段 0–4 不得改动修订号"，因此本方法只在事务提交后调用。
        /// </summary>
        private void SyncScheduleRevision()
        {
            if (_scheduleRevision == _scheduleAuthority.ScheduleRevision) return;
            _scheduleRevision = _scheduleAuthority.ScheduleRevision;
        }

        // —— 快照构建 ——

        private IReadOnlyList<UnitSnapshot> BuildUnitSnapshots()
        {
            var snapshots = new UnitSnapshot[_units.Count];
            for (int i = 0; i < _units.Count; i++)
            {
                UnitRuntimeState unit = _units[i];
                UnitState state = unit.StateMachine != null ? unit.StateMachine.CurrentState : UnitState.Idle;
                snapshots[i] = new UnitSnapshot(
                    unit.UnitId.Value,
                    unit.DefinitionId.Value ?? string.Empty,
                    unit.FactionId.Value ?? string.Empty,
                    unit.Position.X,
                    unit.Position.Y,
                    (int)unit.Facing,
                    unit.HealthQ10,
                    unit.IsAlive,
                    unit.AvailableAdrenaline,
                    unit.AdrenalineCycleId,
                    (int)state,
                    unit.StateMachine != null ? unit.StateMachine.StateStartTick : 0L,
                    unit.StateMachine != null ? unit.StateMachine.StateEndTick : long.MaxValue,
                    UnitStateMachine.CanReceiveDirectHitIn(state));
            }
            return snapshots;
        }

        /// <summary>
        /// 任务 06「必须产出」7：<strong>MovementSegment 的规范化快照</strong>。
        ///
        /// 来源是 <see cref="LogicGridMovementAuthority.AllSegmentsOrdered"/>（按
        /// <c>(ActionPlanId, StepIndex)</c> 升序），与 <see cref="LogicSnapshot"/> 内部
        /// 重新规范排序所用的键完全一致。它<strong>不</strong>包含已提交的只读审计段：
        /// 审计记录不进哈希（它们只描述过去，不影响未来结果）。
        /// </summary>
        private IReadOnlyList<MovementSegmentSnapshot> BuildMovementSegmentSnapshots()
            => MovementSnapshotProjection.Segments(_movementAuthority.AllSegmentsOrdered());

        /// <summary>
        /// 任务 06「必须产出」7：<strong>空间 Reservation 的规范化快照</strong>。
        ///
        /// 来源是 <see cref="LogicGrid.AllReservationsOrdered"/>（稳定空间键
        /// <c>(StartTick, X, Y, ActionPlanId, StepIndex)</c>）；<see cref="LogicSnapshot"/>
        /// 会按 <c>(ActionPlanId, X, Y)</c> 重新规范排序，因此构造顺序不影响摘要。
        ///
        /// 边界：Dodge 目的格预留<strong>不</strong>在此列（它属于
        /// <c>DodgeRelocationAuthority</c> 的独立表，且其生命周期只到 TriggerTick）；
        /// 它进入 <c>DodgeSpaceSnapshot</c> 与任务 08 的接触复核，不进入本快照的 Reservation 列表。
        /// </summary>
        private IReadOnlyList<ReservationSnapshot> BuildReservationSnapshots()
            => MovementSnapshotProjection.Reservations(_logicGrid.AllReservationsOrdered());

        /// <summary>
        /// 当前活动持续效果的规范化快照（按 <c>UnitId -&gt; AppliedAtTick -&gt; EffectSequence -&gt; EffectId</c>）。
        /// 它是"效果进入事件与快照"（任务包「必须产出」7）的唯一来源。
        /// </summary>
        private IReadOnlyList<StatusEffectSnapshot> BuildEffectSnapshots()
        {
            if (_buffSystem.ActiveCount == 0) return Array.Empty<StatusEffectSnapshot>();

            var effects = new List<StatusEffectSnapshot>(_buffSystem.ActiveCount);
            for (int i = 0; i < _units.Count; i++)
            {
                UnitRuntimeState unit = _units[i];
                IReadOnlyList<StatusEffectInstance> active = _buffSystem.ActiveEffectsOf(unit.UnitId);
                for (int e = 0; e < active.Count; e++)
                {
                    StatusEffectInstance instance = active[e];
                    effects.Add(new StatusEffectSnapshot(
                        instance.EffectId.Value,
                        instance.UnitId.Value,
                        instance.AppliedAtTick,
                        instance.EffectSequence,
                        instance.SpecId.Value ?? string.Empty,
                        instance.DurationTicks,
                        instance.EndTick));
                }
            }
            return effects;
        }

        private DecisionSnapshot BuildDecisionSnapshot(long tick)
            => new DecisionSnapshot(
                tick,
                _definition.RulesVersion,
                _definition.BattleDefinitionHashValue,
                BuildUnitSnapshots(),
                BuildActionPlanSnapshots(),
                _scheduleRevision,
                _battleEnd,
                _factionResolver);

        /// <summary>
        /// 任务 05：活动计划的规范化快照。<strong>唯一</strong>映射点在
        /// <c>ActionPlanSnapshot.From</c>；这里必须传入权威
        /// <see cref="IFactionRelationResolver"/>，否则 <c>PrimaryTargetRelation</c> 会退化为
        /// 恒 <c>Self</c>（阶段 A 自曝缺口 ② 的修复点）。
        /// </summary>
        private IReadOnlyList<ActionPlanSnapshot> BuildActionPlanSnapshots()
        {
            IReadOnlyList<ActionPlan> active = _scheduleAuthority.Registry.ActivePlans;
            var snapshots = new List<ActionPlanSnapshot>(active.Count);
            for (int i = 0; i < active.Count; i++)
            {
                snapshots.Add(ActionPlanSnapshot.From(active[i], _factionResolver));
            }
            return snapshots;
        }

        private LogicSnapshot BuildSnapshot(long tick)
        {
            var windows = new List<TurnWindowSnapshot>(1);
            if (_window.IsOpen)
            {
                windows.Add(new TurnWindowSnapshot(
                    _window.WindowId.Value, _window.OwnerUnitId.Value, _window.OpenedAtTick,
                    _window.BudgetTicks, _window.CloseRequested));
            }

            return new LogicSnapshot(
                tick,
                _definition.RulesVersion,
                _definition.BattleDefinitionHashValue,
                _encounterId.Value,
                _battleEnd,
                BuildUnitSnapshots(),
                BuildEffectSnapshots(),
                new TurnWindowManagerSnapshot(_window.IsOpen ? _window.WindowId.Value : 0L, -1L, windows),
                ConcurrentActionSnapshot.None(),
                BattleResourceSnapshot.None(_runtimeInputs.InitialMetaResource),
                _scheduleRevision,
                BuildActionPlanSnapshots(),
                _reactionSystem.BuildSnapshots(),
                _scheduleAuthority.BuildLaneSnapshots(),
                Array.Empty<IntentSnapshot>(),
                BuildMovementSegmentSnapshots(),
                BuildReservationSnapshots(),
                Array.Empty<AiControllerSnapshot>(),
                _ingress.CaptureSnapshot(),
                _rng.CaptureSnapshot(),
                _idGenerator.NextUnitIdValue,
                _idGenerator.NextActionPlanIdValue,
                // 任务 05：机会 ID 的唯一分配器是任务 03 契约的
                // LogicIdGenerator.NextReactionOpportunityId()（ReactionOpportunitySystem 经它取号），
                // 因此快照里的"下一个机会 ID"必须来自同一个计数器。
                // 此处读只读属性，不另建第二份计数状态（缺陷 D2 修复后的唯一来源口径）。
                _idGenerator.NextReactionOpportunityIdValue,
                _idGenerator.NextWindowIdValue,
                _idGenerator.NextEffectIdValue,
                _sequences.NextCommandSequence,
                _sequences.NextIntentSequence,
                _sequences.NextResolutionSequence,
                _sequences.NextEventSequence,
                _sequences.NextEffectSequence,
                _history.Summary,
                CommandSourcePriority.MappingVersion,
                _scheduleAuthority.Registry.TerminalRecordCount,
                _scheduleAuthority.BuildTerminalSummary().Digest,
                // 兼容镜像：与上面的 NextReactionOpportunityId 同源（同一唯一分配器），
                // 不存在第二个可自行取号的计数器（任务 05 收尾 R1 / 缺陷 D2）。
                _idGenerator.NextReactionOpportunityIdValue);
        }

        // —— 校验与量化 ——

        private void ValidateBatch(long tick, FrozenCommandBatch externalBatch)
        {
            if (externalBatch == null)
                throw new LogicDefinitionException(CommandCodes.COMMAND_BATCH_TICK_MISMATCH, "batch is null");

            if (!ReferenceEquals(externalBatch.RegistryStamp, _ingress.RegistryStamp))
                throw new LogicDefinitionException(SimulationCodes.STEP_BATCH_NOT_FROM_REGISTRY,
                    "batch was not frozen by this simulation's command ingress registry");

            if (externalBatch.TargetTick != tick)
                throw new LogicDefinitionException(CommandCodes.COMMAND_BATCH_TICK_MISMATCH,
                    "batch.TargetTick=" + externalBatch.TargetTick.ToString(CultureInfo.InvariantCulture) +
                    " step.Tick=" + tick.ToString(CultureInfo.InvariantCulture));

            for (int i = 0; i < externalBatch.Requests.Count; i++)
            {
                SourcedCommandRequest fact = externalBatch.Requests[i];
                if (fact?.Request == null)
                    throw new LogicDefinitionException(CommandCodes.COMMAND_REQUEST_NULL, "frozen batch member");

                if (fact.Request.TargetTick != tick)
                    throw new LogicDefinitionException(SimulationCodes.STEP_NON_CURRENT_TICK_REQUEST,
                        "request.TargetTick=" + fact.Request.TargetTick.ToString(CultureInfo.InvariantCulture) +
                        " step.Tick=" + tick.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// 生命进入逻辑世界时<strong>一次性</strong>量化为 Q10 整数（RoundHalfUp），
        /// 使浮点不进入任何哈希；此后所有生命运算都在整数域进行。
        ///
        /// <strong>它是全项目唯一的量化约定</strong>：需要把旧侧浮点生命与
        /// <c>UnitSnapshot.HealthQ10</c> 比较的地方（例如 Shadow 比较器）必须调用本方法，
        /// 不得另写一份公式——否则"两侧一致"会退化成实现副本之间的自证。
        /// </summary>
        public static int QuantizeHealth(float health)
            => (int)Math.Round(health * 1024d, MidpointRounding.AwayFromZero);

        /// <summary>
        /// 任务 05：<c>ActionPlan</c> 工厂的<strong>只读事实来源</strong>，直接投影本模拟的单位运行时状态。
        ///
        /// 它只回答只读事实（阵营 / 速度 / 动作集合 / 存活），
        /// <strong>不</strong>写任何状态、<strong>不</strong>读 Controller 或玩家标志——
        /// 因此"敌我判断来自 <c>FactionRelationResolver</c> 而不是控制权"在结构上成立。
        /// 速度是<strong>当前</strong>值：它只在计划创建时被采样一次，
        /// 之后的属性变化不会重新采样既有计划。
        /// </summary>
        private sealed class UnitPlanFactsSource : IActionPlanFactsSource
        {
            private readonly BattleSimulation _simulation;

            public UnitPlanFactsSource(BattleSimulation simulation) => _simulation = simulation;

            public bool TryGetOwnerFacts(UnitId unitId, out ActionPlanOwnerFacts facts)
            {
                facts = null;
                if (!_simulation._unitsById.TryGetValue(unitId.Value, out UnitRuntimeState state)) return false;

                UnitDefinition unitDefinition = _simulation._definition.FindUnit(state.DefinitionId);
                if (unitDefinition == null) return false;

                facts = new ActionPlanOwnerFacts(
                    state.UnitId,
                    state.FactionId,
                    unitDefinition.ActionSpeed,
                    unitDefinition.MoveSpeed,
                    unitDefinition.ActionSetId,
                    state.IsAlive);
                return true;
            }
        }
    }
}
