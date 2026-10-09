using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Status;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;
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

        /// <summary>同一单位被两个不同的控制者绑定 ⇒ 占用投影的审计字段会静默取决于扫描顺序。</summary>
        public const string STEP_OCCUPANCY_CONTROLLER_CONTRADICTION = "STEP_OCCUPANCY_CONTROLLER_CONTRADICTION";

        /// <summary>单位没有任何控制者绑定 ⇒ 占用投影的审计字段会是空值（"没接线"被当成有效控制者）。</summary>
        public const string STEP_OCCUPANCY_CONTROLLER_MISSING = "STEP_OCCUPANCY_CONTROLLER_MISSING";
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
    public sealed class BattleSimulation : IDisposable, IResolutionDamageApplier
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

        /// <summary>
        /// <c>ControllerId -&gt; UnitId 集合</c>的<strong>唯一事实</strong>（构造期由
        /// <c>BattleInitializer.ControllerToUnitIds</c> 填入）。
        ///
        /// 它<strong>不</strong>是只读的容器：装配方可以经
        /// <see cref="RegisterControllerBinding"/> 追加一条绑定，但那条路径
        /// <strong>只复制</strong>同一份映射（不新建第二份控制权真值）——
        /// 命令入口（<see cref="CommandAuthority"/>）、窗口提交授权（<c>TurnWindowManager</c>）
        /// 与审计视图（<see cref="ControllerOf"/>）读的都是这里。
        /// </summary>
        private readonly Dictionary<ControllerId, IReadOnlyList<UnitId>> _controllerToUnitIds;

        /// <summary>
        /// 已验证定义<strong>声明</strong>被某个 <c>ControllerBinding</c> 控制的 UnitId 集合
        /// （由 <c>ControllerBinding.ControlledSlots</c> ∩ <c>SlotToUnitId</c> 投影，构造期算一次）。
        ///
        /// 它唯一的用途是给 <see cref="ControllerOf"/> 提供"缺映射"与"定义本就没声明控制者"的
        /// <strong>判别依据</strong>：
        /// <list type="bullet">
        /// <item>本集合<b>含</b>该单位而 <c>_controllerToUnitIds</c> 查不到 ⇒ 真实的装配缺失 ⇒
        /// 稳定码 <see cref="SimulationCodes.STEP_OCCUPANCY_CONTROLLER_MISSING"/>（守卫保留原样）；</item>
        /// <item>本集合<b>不含</b>该单位 ⇒ 定义层面的合法"无控制者"形态（例如目标外阵营槽位：
        /// 校验器只拒绝悬空/歧义/重复绑定，<strong>不</strong>要求每个槽位都被绑定），
        /// 审计字段取 <c>default</c>（空字符串是合法 <c>ControllerId</c>）而不是抛异常。</item>
        /// </list>
        ///
        /// 它<strong>不是</strong>第二份控制权权威：唯一的权威仍是定义 + <c>BattleInitializer</c>
        /// 产出的 <c>ControllerToUnitIds</c>；本集合只回答"定义有没有为该单位声明控制者"。
        /// </summary>
        private readonly HashSet<long> _controllerDeclaredUnitIds = new HashSet<long>();

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

        /// <summary>
        /// 任务 07：唯一的窗口管理器与预算账本（含并发授权）。
        /// 窗口<strong>不</strong>拥有动作，也不是执行/结算边界；它只控制提交权限与整数 Tick 预算。
        /// </summary>
        private readonly TurnWindowManager _windowManager;
        private readonly ConcurrentActionSystem _concurrentAction;

        /// <summary>
        /// 任务 07：全场唯一的肾上腺素账本集合（每单位一份，按 UnitId 升序的规范枚举）。
        /// 它是肾上腺素资源的唯一所有者与唯一写入通道。
        /// </summary>
        private readonly AdrenalineLedgerRegistry _adrenaline;

        /// <summary>
        /// 任务 07：TurnBudget 窗口账本的<strong>唯一</strong>写入通道
        /// （排程事务、启动门禁原子提交与终态清理参与者共用同一份账本）。
        /// </summary>
        private readonly TurnWindowBudgetAuthority _budgetAuthority;

        /// <summary>
        /// 任务 07：统一终态协调器的"状态感知预算参与者"（固定槽位 600）。
        /// 它在协调器构造时先建、依赖在预算权威建好后回填（与机会绑定参与者同一装配模式）；
        /// 回填之前不存在任何终态请求，因此不存在"漏清理"的中间态。
        /// </summary>
        private readonly BudgetAndAdrenalineCleanupParticipant _budgetParticipant =
            new BudgetAndAdrenalineCleanupParticipant();

        /// <summary>任务 07：本场局外资源（并发能力费用从这里原子消费；初始值来自运行时输入）。</summary>
        private int _metaResource;

        /// <summary>任务 07：战斗结束 Finalizer 实际清空的窗口预算预留总额（诊断与报告）。</summary>
        private long _windowBudgetClearedAtBattleEnd;

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
        /// 任务 09（产出 6）：<strong>唯一</strong>的命令入口处理器。阶段 6 只调用它，
        /// 所有来源（玩家 / AI / 旧壳 / 系统）先经 <see cref="CommandAuthority"/> 的
        /// <c>ControllerId -&gt; UnitId</c> 控制权校验，再路由到排程 / 反应 / 窗口端口。
        /// 它<strong>不</strong>是第二套排程实现：三个端口都指向本场既有的权威对象。
        /// </summary>
        private readonly BattleCommandProcessor _commandProcessor;

        /// <summary>任务 09（产出 6/7）：控制权校验的唯一入口。</summary>
        private readonly CommandAuthority _commandAuthority;

        /// <summary>任务 09（产出 4）：UI/AI 共用的唯一目标资格查询面。</summary>
        private readonly TargetCandidateQuery _targetCandidateQuery;

        /// <summary>
        /// 任务 09（产出 6）：<c>ControllerId -&gt; UnitId</c> 集合的权威投影
        /// （唯一装配路径 = 已验证定义里的 <c>ControllerBinding</c>）。
        /// </summary>
        private readonly ControllerUnitAuthority _controllerUnitAuthority;

        /// <summary>任务 09（产出 7）：反应命令的唯一处理路径（任务 05 的 <c>ReactionPlanner</c> 命令面）。</summary>
        private readonly ReactionCommandPlanner _reactionCommandPlanner;

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

        /// <summary>
        /// 阶段 8 冻结的本 Tick 全局 Intent 队列（任务 08「必须产出」4）。
        /// 它是<strong>只读观察值</strong>：物化与冻结都在同一阶段内一次完成，
        /// 因此它不会出现"半冻结"状态，也不构成恢复输入（恢复输入是计划集与 RNG/序号快照）。
        /// </summary>
        private FrozenIntentQueue _intentQueue = GlobalIntentQueue.Freeze(0L, Array.Empty<CombatIntent>());

        /// <summary>
        /// 阶段 10 构建的冲突图（任务 08「必须产出」6/7）。合成失败（组超上限）时为 <c>null</c>，
        /// 失败原因码留在 <see cref="_conflictGraphError"/>。
        /// </summary>
        private ConflictGraph _conflictGraph;

        /// <summary>阶段 10 构图失败时的稳定原因码（成功时为 <c>null</c>）。</summary>
        private string _conflictGraphError;

        /// <summary>
        /// 阶段 10 产出的<strong>分阶段求解结果</strong>（任务 08「必须产出」8–11）。
        /// 它是纯值产物：求解器没有写接口，唯一的世界写入发生在阶段 11（伤害提交）与阶段 13/14。
        /// </summary>
        private StagedConflictResolution _stagedResolution = StagedConflictResolution.Empty(0L);

        /// <summary>
        /// 阶段 11 的 Resolution 提交系统（装配注入优先；否则本场真实现）。
        /// 真实现就是 <see cref="ConflictGroupResolutionCommitSystem"/>：只扣血 + 登记 Clash 终止请求。
        /// </summary>
        private readonly IResolutionCommitSystem _resolutionCommit;

        /// <summary>本 Tick 的伤害/合力提交报告（只读观察面；未装配提交系统时为 null）。</summary>
        private ConflictGroupResolutionCommitSystem _resolutionCommitSystem;

        /// <summary>
        /// 阶段 11 实际使用的强制位移请求构建器（装配注入优先；否则本场真实现）。
        /// </summary>
        private readonly IForcedDisplacementRequestBuilder _displacementRequestBuilder;

        /// <summary>
        /// 阶段 14 实际使用的肾上腺素入账事实来源（装配注入优先；否则本场真实现）。
        /// </summary>
        private readonly IAdrenalineAccrualFactSource _adrenalineAccrualSource;

        /// <summary>
        /// 本 Tick 的 <c>ActionPlanId → ConflictGroupKey</c> 只读投影（阶段 10 构图后一次性冻结）。
        ///
        /// 语义事件的"冲突组"字段、位移请求的组键都取自这份投影；未入图的计划按 <c>0</c> 处理。
        /// 它在阶段 10 之后<strong>不再变更</strong>：图冻结后计划终态不追溯改写本 Tick 输入。
        /// </summary>
        private IReadOnlyDictionary<long, long> _conflictGroupKeyByPlan =
            new Dictionary<long, long>();

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
        private readonly List<UnitLifecycleCleanupNotice> _pendingLifecycleCleanup = new List<UnitLifecycleCleanupNotice>();
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

        /// <summary>阶段 12 求解器（装配注入优先；否则本场真实现）。</summary>
        private readonly IForcedDisplacementSolver _displacementSolver;

        /// <summary>阶段 13 前半段提交器（装配注入优先；否则本场真实现）。</summary>
        private readonly IForcedDisplacementCommitter _displacementCommitter;

        /// <summary>强制位移性能统计（只读计数，绝不进哈希/快照）。</summary>
        private readonly ForcedDisplacementStats _forcedDisplacementStats = new ForcedDisplacementStats();
        private IReadOnlyList<ActionPlanId> _invalidatedPlanIds = Array.Empty<ActionPlanId>();

        private long _batchBaseScheduleRevision;
        private long _scheduleRevision;
        private long _tick = -1L;
        private long _eventTick = -1L;
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
            // 任务 09（产出 4）：UI/AI 共用的唯一目标资格查询面 —— 直接引用整场唯一的
            // 关系解析器实例（与决策快照上的那个是同一个），绝不复制关系矩阵。
            _targetCandidateQuery = new TargetCandidateQuery(_factionResolver);
            // 复制的唯一理由：装配方可能经 RegisterControllerBinding 追加绑定（见方法注释），
            // 而 initialization 的结果是只读集合。这不是"第二份控制权真值"——
            // 它是同一份映射的唯一可变容器，命令入口/窗口授权/审计视图都只读它。
            _controllerToUnitIds = new Dictionary<ControllerId, IReadOnlyList<UnitId>>(
                initialization.ControllerToUnitIds);
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

            // 控制权声明集（只读投影，见字段注释）：定义声明的槽位 → 初始化器分配的单位。
            // 装配期一次算完，运行期只查表，不构成第二份控制权权威。
            if (_encounter.Controllers != null)
            {
                for (int i = 0; i < _encounter.Controllers.Count; i++)
                {
                    ControllerBinding declared = _encounter.Controllers[i];
                    if (declared == null || declared.ControlledSlots == null) continue;
                    for (int s = 0; s < declared.ControlledSlots.Count; s++)
                    {
                        if (initialization.SlotToUnitId != null
                            && initialization.SlotToUnitId.TryGetValue(declared.ControlledSlots[s], out UnitId declaredUnitId))
                        {
                            _controllerDeclaredUnitIds.Add(declaredUnitId.Value);
                        }
                    }
                }
            }

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
                _movementAuthority,
                // 任务 07 的状态感知预算参与者：固定槽位 BudgetAndAdrenaline = 600，
                // 在空间清理之后按"计划当前状态"结算 Reserved/Spent，绝不重开窗口或退款。
                _budgetParticipant
            });

            // —— 任务 07：唯一窗口管理器 + 唯一预算账本 + 独立肾上腺素账本 + 并发授权 ——
            //
            // 窗口管理器只持有"提交权限与整数 Tick 预算"；它不持有任何计划、Intent 或 Reservation。
            // 窗口 ID 的唯一分配器仍是任务 03 契约的 LogicIdGenerator（不建第二份计数状态）。
            //
            // 创建顺序（无环，且不依赖"稍后回填"）：
            //   ① 肾上腺素账本集合（窗口打开时要递增个人周期并清零 Available）；
            //   ② 窗口管理器（它的关闭回调以闭包读字段，因此并发系统可以稍后赋值）；
            //   ③ 并发授权系统（需要窗口管理器做控制权与当前窗口判定）；
            //   ④ 预算权威（账本唯一写入通道 + 启动门禁的原子提交端口）与终态清理参与者。
            _metaResource = runtimeInputs.InitialMetaResource;
            _adrenaline = new AdrenalineLedgerRegistry(definition.AdrenalineRules)
            {
                ChangedSink = change =>
                {
                    SyncUnitAdrenalineMirror(change.UnitId);
                    long reservationPlanId = change.ReservationPlanId.HasValue
                        ? change.ReservationPlanId.Value.Value
                        : 0L;
                    _outbox.Emit(sequence => new AdrenalineLedgerChangedEvent(
                        _eventTick, sequence, change.UnitId, change.CycleId, change.ChangeKind,
                        change.AvailableBefore, change.AvailableAfter,
                        reservationPlanId, change.ReservationAmount));
                }
            };

            _windowManager = new TurnWindowManager(
                new SimulationTurnWindowWorld(
                    unitId => _unitsById.TryGetValue(unitId.Value, out UnitRuntimeState state) && state.IsAlive,
                    () => _battleEnd.IsEnded),
                _idGenerator.NextWindowId)
            {
                WindowOpenedSink = (window, openedTick) =>
                {
                    WindowId windowId = window.WindowId;
                    UnitId ownerId = window.OwnerUnitId;
                    int budget = window.TotalBudgetTicks;
                    _outbox.Emit(sequence => new TurnWindowOpenedEvent(openedTick, sequence, windowId, ownerId, budget));
                },
                WindowClosedSink = (window, reason, closedTick) =>
                {
                    WindowId windowId = window.WindowId;
                    UnitId ownerId = window.OwnerUnitId;
                    _outbox.Emit(sequence => new TurnWindowClosedEvent(closedTick, sequence, windowId, ownerId, reason));
                },
                OwnerCycleResetSink = (unitId, tick) => _adrenaline.BeginOwnWindow(unitId),
                WindowClosedAuthoritySink = (windowId, tick) => _concurrentAction?.RevokeForWindow(windowId)
            };

            _concurrentAction = new ConcurrentActionSystem(definition.ConcurrentAction, _windowManager)
            {
                // 只有"主角"能购买并发行动；装配未显式指定时保持 null ⇒ 激活 fail-closed
                // （稳定拒绝 WINDOW_CONCURRENT_INVALID_ACTOR，绝不按 Controller/玩家标志猜主角）。
                HeroUnitId = assembly.ConcurrentHeroUnitId,
                ActivatedSink = (windowId, playerUnitId, ownerUnitId) =>
                    _outbox.Emit(sequence => new ConcurrentActionActivatedEvent(
                        _eventTick, sequence, windowId, playerUnitId, ownerUnitId)),
                DeactivatedSink = (windowId, playerUnitId) =>
                    _outbox.Emit(sequence => new ConcurrentActionDeactivatedEvent(_eventTick, sequence, windowId, playerUnitId))
            };
            _concurrentAction.BindMetaResource(() => _metaResource, value => _metaResource = value);

            // 预算权威 = 账本的唯一写入通道 + 启动门禁的原子提交端口（Reserved -> Spent）。
            // 装配显式注入的端口优先（任务 05 的既有测试夹具），否则用本任务的真实实现。
            _budgetAuthority = new TurnWindowBudgetAuthority(_windowManager, _concurrentAction);
            _budgetAuthority.ChangedSink = change => _outbox.Emit(sequence => new TurnBudgetChangedEvent(
                _eventTick, sequence, change.WindowId, change.OwnerUnitId,
                change.ActionPlanId.HasValue ? change.ActionPlanId.Value.Value : 0L,
                change.ChangeKind, change.Source,
                change.AvailableBefore, change.AvailableAfter,
                change.ReservedBefore, change.ReservedAfter,
                change.SpentBefore, change.SpentAfter));
            _startCommitPort = assembly.StartCommitPort ?? (IActionPlanStartCommitPort)_budgetAuthority;

            // 统一终态协调器的"状态感知预算参与者"（固定槽位 600）：Editable 释放未消费预留，
            // Locked/Running 保持 Spent；它不重开窗口、不转移额度、不前移后续计划。
            _budgetParticipant.Bind(_budgetAuthority, _windowManager, _adrenaline);

            // 控制权注册：唯一来源是已验证定义里的 ControllerBinding（与阵营/胜负/目标资格正交）。
            // 没有这一步，窗口的提交授权与并发激活都会以 WINDOW_ISSUER_CANNOT_CONTROL_UNIT
            // 拒绝一切带账本效果的显式排程编辑——那是"没接线"，不是"权限模型生效"。
            //
            // 任务 09（产出 6）：同一份投影同时填入命令入口的**唯一**控制权权威
            // （ControllerId -> UnitId 集合）。它是"载荷里的单位 ID 只表达意图目标、不证明权限"
            // 的执行点：发行者身份只来自入口绑定。
            _controllerUnitAuthority = new ControllerUnitAuthority();
            foreach (KeyValuePair<ControllerId, IReadOnlyList<UnitId>> controllerBinding in _controllerToUnitIds)
            {
                IReadOnlyList<UnitId> controlled = controllerBinding.Value;
                _controllerUnitAuthority.Register(controllerBinding.Key, controlled);
                if (controlled == null) continue;
                for (int i = 0; i < controlled.Count; i++)
                    _windowManager.RegisterControllerBinding(controllerBinding.Key, controlled[i]);
            }

            // 排程事务的预算上下文：显式编辑必须验证 ExpectedWindowId 与提交授权；
            // 系统自动延期由上下文来源区分（它不要求发行者授权，也不得重开已关闭窗口）。
            _scheduleEditor.BudgetContextFactory = (source, targetTick, expectedWindowId, issuer) =>
                new ScheduleBudgetContext(
                    source,
                    _budgetAuthority,
                    expectedWindowId,
                    _windowManager.CurrentWindow != null ? _windowManager.CurrentWindow.WindowId : (WindowId?)null,
                    issuer);
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

            // 任务 08（R4，照 MovementPathCalculator 先例）：装配点未显式注入时**绑本场真实现**，
            // 因此默认装配即生产装配，不存在"默认 = 不工作"。
            //   求解器：只读本场 LogicGrid + 计划权威 + Dodge 目的格预留；静态障碍来源显式给出
            //           （基线 LogicGrid 没有地形障碍模型，默认 = 无障碍，不是"障碍规则不存在"）。
            //   提交器：经**唯一**统一终态协调器提交 MovementOriginInvalidated /
            //           ReservationPreemptedByForcedDisplacement。
            // 任务 03/04 的脚本夹具与负控制仍然显式注入优先。
            _displacementSolver = assembly.DisplacementSolver
                ?? new Interactions.ForcedDisplacementSolver(
                    _logicGrid, _scheduleAuthority, _dodgeRelocation, null, _forcedDisplacementStats);
            _displacementCommitter = assembly.DisplacementCommitter
                ?? new Interactions.ForcedDisplacementCommitter(_scheduleAuthority, _terminalCoordinator);

            // 任务 08：阶段 11 的 Resolution 提交接缝。装配注入优先；
            // 未注入时绑**本场真实现**（与 DisplacementSolver/Committer 的先例一致）：
            // 它只做两件事——按 UnitId 升序把已聚合伤害写入权威生命、把 Clash 终止登记为请求。
            // 它不持有计划注册表/网格/Lane，因此"提交阶段自己改计划终态"在类型层就不可能。
            _resolutionCommit = assembly.ResolutionCommit;
            if (_resolutionCommit == null)
            {
                _resolutionCommitSystem =
                    new ConflictGroupResolutionCommitSystem(() => _stagedResolution, this);
                _resolutionCommit = _resolutionCommitSystem;
            }

            // 任务 08（R4 第三/第四装配点，照 DisplacementSolver/Committer 与 ResolutionCommit 先例）：
            //   阶段 11 的**位移请求构建器**：按本 Tick 的 DamageCommitReport.Units[] 生成每单位
            //   唯一请求（取该组 GroupKey），不从网格或求解器重算第二次几何。
            //   阶段 14 的**肾上腺素入账事实来源**：只读分阶段求解产物与伤害提交报告，
            //   按 UnitId 升序给出每单位至多一条的规范事实（唯一入账入口仍在任务 07 的账本）。
            // 未注入 ⇒ 绑本场真实现；显式注入优先（任务 03/04/07 夹具、负控制）。
            _displacementRequestBuilder = assembly.DisplacementRequestBuilder
                ?? new DisplacementRequestBuilder(() => LastDamageCommitReport);
            _adrenalineAccrualSource = assembly.AdrenalineAccrualFactSource
                ?? new AdrenalineAccrualFactSource(() => _stagedResolution, () => LastDamageCommitReport);

            // 任务 05：唯一的反应机会系统 + 唯一的终态/生命周期事件发射路径（经 Outbox）。
            //
            // 三个任务 06/07 接缝（区域候选来源 / 资源准备 / 预留释放接收方）由装配注入；
            // 未注入时的默认分别是"没有候选"（fail-closed）、"只校验不冻结"与"请求排队保留"
            // （绝不静默丢弃）。任务 06/07 装配后按同一入口注入，不改变机会状态机。
            //
            // 任务 08 收尾裁定：**区域威胁候选来源**改走与位移求解器/提交器同一 R4 先例 ——
            // 未显式注入时，BattleSimulation 绑定基于本场 LogicGrid 的**真实现**
            // （GridAreaThreatCandidateSource），不再静默回落到"永远没有候选"。
            // 理由是 NoAreaThreatCandidateSource 会让**区域攻击永远不公开任何反应机会**
            // （CollectDefenders 在非 PrimaryTargetOnly 策略下只能向该来源取候选），
            // 使 AOE 的 Block/Dodge/Guard 覆盖在结构上不可达；任务 05 的 fail-closed 默认口径
            // 是"任务 06 接入前"的临时态，而 06-交接记录.md:808-810 已把区域威胁来源归属任务 08。
            // 显式注入仍优先（任务 03/04/05 脚本夹具、负控制 NoAreaThreatCandidateSource）。
            _reactionSystem = new ReactionOpportunitySystem(
                _scheduleAuthority, _planFactory, definition, _factionResolver,
                // 任务 05 收尾 R1（缺陷 D2）：机会 ID 的唯一分配器（任务 03 冻结契约的
                // LogicIdGenerator），必填；缺省回落会重新引入"第二份计数状态"。
                _idGenerator,
                assembly.AreaThreatCandidateSource
                    ?? new GridAreaThreatCandidateSource(definition, _logicGrid, _logicGrid.RegisteredUnitsOrdered),
                _terminalCoordinator)
            {
                EventSink = sink => _outbox.Emit(sink),
                PreparationPort = assembly.ReactionPreparationPort,
                ReservationReleaseSink = assembly.ReactionReservationReleaseSink,
                // 任务 07：反应接受把费用从 Available 原子转入带个人周期的计划预留，
                // TriggerTick 消费、来源威胁取消按周期返还。唯一的肾上腺素权威是 _adrenaline。
                AdrenalinePort = _adrenaline
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
                ResetActorPhaseAfterTerminal(plan, tick);
                if (reason == ActionTerminationReason.None) _reactionSystem.EmitPlanCompleted(plan, tick);
                else _reactionSystem.EmitPlanTerminated(plan, tick);
            };

            // 反应命令是阶段 6 的一部分：处理器只把已冻结命令转给系统，绝不重建机会或推导 Tick。
            // 任务 09（产出 7）：唯一实现改由 ReactionCommandPlanner 承担——它同时是
            // CommandAuthority 的 IReactionCommandUnitSource（用同一个权威机会解析防御者），
            // 因此"控制权校验"与"计划创建"读的是**同一个**机会对象，不存在两份机会视图。
            _reactionCommandPlanner = new ReactionCommandPlanner(_reactionSystem, definition)
            {
                PlanCreatedSink = (plan, tick) => _reactionSystem.EmitPlanCreated(plan, tick)
            };
            // 兼容面：直接调用 ActionPlanCommandProcessor 的既有装配（任务 05/06 夹具）仍能路由反应，
            // 且路由到**同一个** planner —— 不存在第二份反应载荷解析实现。
            _planCommandProcessor.ReactionCommandHandler =
                (envelope, tick) => _reactionCommandPlanner.ProcessAuthorized(envelope, tick, _batchBaseScheduleRevision);

            // 任务 09（产出 9/10）：AI 的两个**只读**端口。
            // 它们只投影公开事实与只读查询，不暴露机会系统/注册表的任何写入面；
            // 装配方把它们注入 AiControllerLogic 即可让 AI 观察已公开机会与权威计划。
            AiReactionOpportunities = new ReactionOpportunityPort(_reactionSystem, _logicGrid);
            AiActionPlanLookup = new ActionPlanLookupPort(_scheduleAuthority);

            // 窗口命令（关窗 / 并发行动激活）走同一条冻结命令批处理路径：
            // 身份来自命令网关绑定的 ControllerId，窗口来自 scope 的 ExpectedWindowId，
            // 费用只来自权威 ConcurrentActionDefinition（载荷里根本没有这些字段）。
            _planCommandProcessor.WindowCommandHandler = HandleWindowCommand;

            // 已创建事件只在成功提交的排程事务上发射（失败/预览事务不发射）。
            // 同一次成功提交还要把 **Remove 掉的计划**经统一终态协调器收口（见下）。
            _planCommandProcessor.CommittedTransactionSink = (result, tick) =>
            {
                EmitPlanCreatedEvents(result, tick);
                TerminateRemovedPlans(result, tick);
            };

            // —— 任务 09（产出 6/7）：唯一命令入口的装配 ——
            //
            // 阶段 6 从此只调用 _commandProcessor；控制权校验的唯一实现是 CommandAuthority，
            // 它读的是本场**已验证定义**里的 ControllerBinding 投影（与阵营关系正交）。
            // 三个端口都指向本场既有权威对象，因此不存在"按来源分叉的第二套处理路径"：
            //   排程 ⇒ 同一个 ActionPlanCommandProcessor（内部仍是唯一的 ScheduleEditor 事务）
            //   窗口 ⇒ 同一个 ActionPlanCommandProcessor（内部仍是任务 07 的窗口权威）
            //   反应 ⇒ 同一个 ReactionCommandPlanner（内部仍是唯一的 ReactionOpportunitySystem）
            _commandAuthority = new CommandAuthority(_controllerUnitAuthority, _reactionCommandPlanner);
            // 计划所有者解析端口：Move/Remove 的控制权判据是"计划自己的所有者"，
            // 因此"用别人的计划 ID 顶替自己单位的编辑"在控制权这一步就被拦住。
            _commandAuthority.PlanOwnerResolver = planId =>
            {
                ActionPlan plan = _scheduleAuthority.Registry.Find(planId);
                return plan == null ? (UnitId?)null : plan.OwnerUnitId;
            };
            // 并发行动的作用单位由装配方给出的权威主角唯一决定（未配置 ⇒ 保持任务 07 的
            // INVALID_CONCURRENT_ACTOR 语义，本类不替它猜一个"玩家单位"）。
            if (assembly.ConcurrentHeroUnitId.HasValue)
                _commandAuthority.ConcurrentActionUnitResolver = () => _concurrentAction.HeroUnitId;

            _commandProcessor = new BattleCommandProcessor(_commandAuthority)
            {
                // 三个端口都是按载荷判别分派的适配器：反应载荷不可能被窗口端口二次解释，
                // 窗口载荷也不可能落进排程端口。前两者绑定**同一个**
                // ActionPlanCommandProcessor 实例（方法组绑定，因此端口的 Target 就是那个实例），
                // 因此"排程与窗口只有一套实现"是可观察事实而不是注释承诺。
                ScheduleEditSink = new PayloadRoutedCommandPort(
                    CommandScopeKind.ScheduleEdit, _planCommandProcessor.ProcessAuthorized),
                WindowSink = new PayloadRoutedCommandPort(
                    CommandScopeKind.Window, _planCommandProcessor.ProcessAuthorized),
                ReactionSink = new PayloadRoutedCommandPort(
                    CommandScopeKind.Reaction, _reactionCommandPlanner.ProcessAuthorized)
            };

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
                // 任务 07：每单位一份肾上腺素账本；开局 Available 与个人周期号来自权威初始化快照。
                _adrenaline.Register(unit.UnitId, initial.AvailableAdrenaline, initial.AdrenalineCycleId);

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

        /// <summary>
        /// 任务 09（产出 6）：<strong>唯一</strong>的命令入口处理器（只读暴露；诊断与测试用）。
        /// 它在阶段 6 被调用，是所有来源的唯一处理路径。
        /// </summary>
        public BattleCommandProcessor CommandProcessor => _commandProcessor;

        /// <summary>
        /// 任务 09（产出 6）：控制权校验的唯一入口（只读暴露）。
        /// 玩家 / AI / 旧壳 / 系统走<strong>同一条</strong>判定。
        /// </summary>
        public CommandAuthority CommandAuthority => _commandAuthority;

        /// <summary>
        /// 任务 09（产出 7）：反应命令的<strong>唯一</strong>处理路径（只读暴露）。
        /// 它同时实现 <see cref="IReactionCommandUnitSource"/>，因此控制权校验与计划创建
        /// 读的是同一个权威机会对象。
        /// </summary>
        public ReactionCommandPlanner ReactionCommandPlanner => _reactionCommandPlanner;

        /// <summary>
        /// 任务 09（产出 4）：UI 与 AI 共用的**唯一**目标资格查询面。
        /// 它引用整场唯一的 <see cref="IFactionRelationResolver"/> 实例
        /// （与 <see cref="LastDecisionSnapshot"/> 上的那个是同一个），不复制任何关系矩阵。
        /// </summary>
        public TargetCandidateQuery TargetCandidates => _targetCandidateQuery;

        /// <summary>
        /// <strong>控制权绑定的追加装配点</strong>：把一条
        /// <c>ControllerId -&gt; UnitId 集合</c>绑定登记进本场<strong>同一份</strong>控制权事实。
        ///
        /// 为什么需要它（而不是"再挂一个 WindowManager 的登记"）：
        /// <list type="bullet">
        /// <item>控制权有两个消费者，且<strong>必须</strong>读同一份事实——命令入口的
        /// <see cref="CommandAuthority"/>（"发行者能否控制载荷涉及的单位"，任务 09 产出 6）
        /// 与窗口的提交授权（<c>TurnWindowManager.CanControl</c>，任务 07）。</item>
        /// <item>只登记其中之一会让两条授权面<strong>漂移</strong>：命令被入口拒绝、
        /// 或窗口放行而入口不放行，两种都是"没接线"，不是"权限模型生效"。</item>
        /// <item>已验证定义（<c>ControllerBinding</c>）是生产装配的唯一来源，但
        /// <strong>它不是唯一可能的来源</strong>：装配方可以用一个定义变体把新单位带进战场
        /// （例如任务 04 的目标外阵营槽位——"不被任何外部入口控制"是该变体的显式设计事实），
        /// 然后为它声明一个新的控制事实。定义对象本身不可变，因此需要这个显式端口。</item>
        /// </list>
        ///
        /// 它与 <c>CommandIngress.RegisterExternalEntry</c>（"为某入口注册"）配对：
        /// 本方法是"为某控制者登记它控制的单位"，二者都不允许外部自封
        /// <c>System</c> 来源、都拒绝非法 <c>ControllerId</c>。
        ///
        /// <strong>重复调用 = 并集（幂等）</strong>，与
        /// <c>TurnWindowManager.RegisterControllerBinding</c> 的既有契约一致：
        /// 已登记的控制者可以<b>追加</b>新单位（例如某入口先控制槽位 A、稍后接管槽位 B），
        /// 每个单位只登记一次、最终集合按 <c>UnitId</c> 升序，因此"重复登记"不会产生
        /// 第二份真值或顺序分歧。空集合不覆盖已登记的非空集合（防手滑清空权威）。
        ///
        /// <strong>不变量</strong>：同一个单位不得被两个不同控制者绑定
        /// （<c>STEP_OCCUPANCY_CONTROLLER_CONTRADICTION</c> 仍由 <see cref="ControllerOf"/>
        /// 在 Step 时抛出，因此调用方无法用本方法绕过该判定）。
        /// </summary>
        /// <param name="controllerId">发行者身份（必须通过定义 ID 格式校验）。</param>
        /// <param name="unitIds">该控制者新增控制的单位集合（null / 空集合 = 不新增）。</param>
        public void RegisterControllerBinding(ControllerId controllerId, IReadOnlyList<UnitId> unitIds)
        {
            // 与 CommandIngress.Register / BattleInitializer 完全相同的两道前置校验：
            // "外部不得自封系统来源"与"ControllerId 必须合法"。
            if (controllerId.Value == SystemControllerId)
            {
                throw new LogicDefinitionException(
                    CommandCodes.EXTERNAL_INGRESS_SYSTEM_SOURCE_REJECTED, controllerId.Value);
            }
            if (DefinitionIdValidation.ValidateFormat(controllerId.Value) != null)
            {
                throw new LogicDefinitionException(
                    CommandCodes.COMMAND_INGRESS_CONTROLLER_INVALID, controllerId.Value ?? "<null>");
            }

            IReadOnlyList<UnitId> existing = _controllerUnitAuthority.ControlledUnitsOf(controllerId);
            if (existing.Count == 0 && (unitIds == null || unitIds.Count == 0)) return;

            var union = new List<UnitId>(existing.Count + (unitIds?.Count ?? 0));
            for (int i = 0; i < existing.Count; i++) union.Add(existing[i]);
            if (unitIds != null)
            {
                for (int i = 0; i < unitIds.Count; i++) union.Add(unitIds[i]);
            }
            union.Sort((a, b) => a.Value.CompareTo(b.Value));

            // 三处写的是同一份事实：命令入口的控制权权威、窗口的提交授权、审计视图的映射。
            _controllerUnitAuthority.Register(controllerId, union);
            for (int i = 0; i < union.Count; i++)
            {
                _windowManager.RegisterControllerBinding(controllerId, union[i]);
                _controllerDeclaredUnitIds.Add(union[i].Value);
            }
            _controllerToUnitIds[controllerId] = union.ToArray();
        }

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

        /// <summary>UI read projection, filtered by the same control authority used for AI and commands.</summary>
        public DecisionSnapshot DecisionSnapshotFor(ControllerId controllerId)
            => BuildDecisionSnapshot(_tick).WithDefinition(_definition).ForController(controllerId,
                _controllerUnitAuthority.ControlledUnitsOf(controllerId), OwnWindowSnapshotOf(controllerId));

        /// <summary>唯一终态清理的审计报告（未结束时为 null）。</summary>
        public BattleEndFinalizerReport FinalizerReport => _finalizerReport;

        /// <summary>最近一次 Step 冻结批次时记录的 BatchBaseScheduleRevision。</summary>
        public long BatchBaseScheduleRevision => _batchBaseScheduleRevision;

        /// <summary>
        /// 本场<strong>已验证</strong>的战斗定义（只读；唯一权威动作表与规则值的来源）。
        ///
        /// 任务 09（产出 17）：决策快照通过它暴露"与命令层同一个 <c>ActionSet</c>"，
        /// 因此 AI 与玩家的理论动作可用性不可能来自两份动作表。
        /// </summary>
        public BattleDefinition Definition => _definition;

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
        /// 任务 07：本场<strong>唯一</strong>的窗口管理器（含已关闭窗口的可审计账本）。
        ///
        /// 它是只读观察入口：账本写入全部经 <c>TurnWindowBudgetAuthority</c>，
        /// 且只允许在 <c>ProjectHero.Logic</c> 内调用。命令生产者<strong>不得</strong>用它
        /// 自报窗口身份——<c>ExpectedWindowId</c> 仍由命令 scope 声明并逐条校验。
        /// </summary>
        public TurnWindowManager WindowManager => _windowManager;

        /// <summary>
        /// 任务 09（产出 9/10）：<strong>AI 的反应候选观察面</strong>——把本场唯一机会系统的
        /// <strong>已公开且仍开放</strong>的选项投影成 AI 层自己的只读值对象。
        ///
        /// 它<strong>只</strong>投影公开事实（开放状态、公开标志、逐选项截止、Dodge 的已公布逻辑
        /// 目的格），<strong>不</strong>暴露机会系统本身，因此 AI 无法用它创建机会、推导 TriggerTick
        /// 或直接调 <c>TryAcceptById</c>。装配方把它注入 <c>AiControllerLogic</c> 即可。
        /// </summary>
        public AI.IAiReactionOpportunitySource AiReactionOpportunities { get; }

        /// <summary>
        /// 任务 09（产出 9）：<strong>AI 的只读计划检索面</strong>——只暴露权威计划的只读查询
        /// （<c>FindPlan</c> / <c>ActivePlansOf</c>），<strong>不</strong>暴露注册表的写入与生命周期方法。
        /// <c>Move</c> 候选靠它取得 <c>IMovementPathCalculator.Recompute</c> 的输入。
        /// </summary>
        public AI.IAiActionPlanLookup AiActionPlanLookup { get; }

        /// <summary>
        /// 任务 07：当前窗口（未打开时为 null）；只读观察用。
        /// </summary>
        public TurnWindow CurrentTurnWindow => _windowManager.CurrentWindow;

        /// <summary>任务 07：该单位当前的肾上腺素账本（不存在时为 null）；只读观察用。</summary>
        public AdrenalineLedger AdrenalineLedgerOf(UnitId unitId) => _adrenaline.Find(unitId);

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
        /// 阶段 8 冻结的本 Tick 全局 Intent 队列（任务 08「必须产出」4）。
        ///
        /// 只读观察入口：它是"仲裁读取本 Tick 全部有效动作、<strong>不按提交窗口过滤</strong>"
        /// （不变量 5）的直接证据——<c>Freeze</c> 的签名里没有窗口参数。
        /// 队列在阶段 8 被整体替换，因此不存在跨 Tick 的残留项。
        /// </summary>
        public FrozenIntentQueue IntentQueue => _intentQueue;

        /// <summary>
        /// 阶段 10 构建的规范化冲突图（任务 08「必须产出」6/7）。
        /// 构图失败（单组超上限）时为 <c>null</c>，原因码见 <see cref="ConflictGraphBuildError"/>。
        /// </summary>
        public ConflictGraph ConflictGraph => _conflictGraph;

        /// <summary>阶段 10 构图失败时的稳定原因码（成功时为 <c>null</c>）；只读诊断，不参与哈希。</summary>
        public string ConflictGraphBuildError => _conflictGraphError;

        /// <summary>
        /// 阶段 10 的<strong>分阶段求解结果</strong>（任务 08「必须产出」8–11）：
        /// Dodge 空间复核 → Block 完全抵抗 → Clash 同时求解 → Move → Remaining Hits 聚合。
        ///
        /// 只读观察入口。它就是阶段 11 伤害提交与阶段 14 Clash 终态的<strong>唯一输入</strong>，
        /// 因此"装配点没接线"（求解结果没人消费）在这条只读面上是可见的。
        /// 构图失败时它是以失败码整体拒绝的空结果（不丢边、不拆组）。
        /// </summary>
        public StagedConflictResolution StagedResolution => _stagedResolution;

        /// <summary>
        /// 本 Tick 的伤害/合力提交结果（按 <c>UnitId</c> 升序）。
        /// 未装配本场真实现（显式注入了别的 <see cref="IResolutionCommitSystem"/>）时为
        /// <see cref="DamageCommitReport.Empty"/>，且 <see cref="ResolutionCommitSystem"/> 为 null。
        /// </summary>
        public DamageCommitReport LastDamageCommitReport
            => _resolutionCommitSystem == null
                ? DamageCommitReport.Empty(_tick)
                : _resolutionCommitSystem.LastDamageCommitReport;

        /// <summary>本场真实现的提交系统（显式注入时为 null；只读观察入口）。</summary>
        public ConflictGroupResolutionCommitSystem ResolutionCommitSystem => _resolutionCommitSystem;

        /// <summary>
        /// 阶段 11 实际使用的强制位移请求构建器（只读观察入口；未显式注入时是<strong>本场真实现</strong>）。
        /// </summary>
        public IForcedDisplacementRequestBuilder DisplacementRequestBuilder => _displacementRequestBuilder;

        /// <summary>
        /// 阶段 14 实际使用的肾上腺素入账事实来源（只读观察入口；未显式注入时是<strong>本场真实现</strong>）。
        /// </summary>
        public IAdrenalineAccrualFactSource AdrenalineAccrualFactSource => _adrenalineAccrualSource;

        /// <summary>
        /// 最近一次构建出的肾上腺素入账事实（按 <c>UnitId</c> 严格升序、每单位至多一条）。
        /// 未显式注入时是本场真实现的产物；显式注入别的来源时为<strong>空</strong>（负控制可见）。
        /// </summary>
        public IReadOnlyList<AdrenalineAccrualFacts> LastAdrenalineAccrualFacts
            => _adrenalineAccrualSource as AdrenalineAccrualFactSource != null
                ? ((AdrenalineAccrualFactSource)_adrenalineAccrualSource).LastFacts
                : Array.Empty<AdrenalineAccrualFacts>();

        /// <summary>
        /// 最近一次构建出的强制位移请求（按 <c>TargetUnitId</c> 升序、每单位至多一条）。
        /// 未显式注入时是本场真实现的产物；显式注入别的构建器时为<strong>空</strong>（负控制可见）。
        /// </summary>
        public IReadOnlyList<ForcedDisplacementRequest> LastBuiltDisplacementRequests
            => _displacementRequestBuilder as DisplacementRequestBuilder != null
                ? ((DisplacementRequestBuilder)_displacementRequestBuilder).LastRequests
                : Array.Empty<ForcedDisplacementRequest>();

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
        /// 阶段 12 实际使用的强制位移求解器（只读观察入口；未显式注入时是<strong>本场真实现</strong>，
        /// 见 <c>BattleSimulationAssembly.DisplacementSolver</c> 的 R4 口径）。
        /// </summary>
        public IForcedDisplacementSolver ForcedDisplacementSolver => _displacementSolver;

        /// <summary>阶段 13 实际使用的强制位移提交器（只读观察入口）。</summary>
        public IForcedDisplacementCommitter ForcedDisplacementCommitter => _displacementCommitter;

        /// <summary>
        /// 强制位移性能统计（任务包「必须产出」22）：请求数、最大请求步数、每轮依赖节点/边、
        /// footprint 点检查数、批量换位数量。<strong>只读诊断面，绝不参与逻辑输入或哈希。</strong>
        /// </summary>
        public ForcedDisplacementStats ForcedDisplacementStatistics => _forcedDisplacementStats;

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
            _eventTick = tick;

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
            // 状态/效果阶段的死亡先于窗口和命令；Resolution 造成的死亡仍在阶段15处理。
            // 判据来自 <see cref="BattleSimulationAssembly.PreCommandVictoryGate"/>；
            // 未装配该扩展点时回落到配置判据 <see cref="EvaluateConfiguredResultCode"/>
            // （只读 FactionId / IsCombatEffective 与 VictoryDefinition 的 Allied/Hostile 目标组）。
            //
            _trace.Enter(StepPhase.DeathAndVictory);
            ProcessDeaths(tick);
            ConsumeLifecycleCleanup(tick);
            string preCommandResult = _assembly.PreCommandVictoryGate != null
                ? _assembly.PreCommandVictoryGate.EvaluatePreCommandResult()
                : EvaluateConfiguredResultCode();
            if (preCommandResult != null)
            {
                // 先把本 Tick 的致死单位按阶段 15 的完整语义提交（终态、事件、通知、占位移除），
                // 再关闭窗口与命令路径：这样"命令阶段之前结束"不会让死亡事实丢失。

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
            ConsumeLifecycleCleanup(tick);

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
            // 任务 09 A 流（产出 5）：命令入口的"当前 Tick"只由唯一模拟入口推进，
            // 因此设备输入/AI 的默认投递目标（CurrentTick + CommandIngressLeadTicks）
            // 不可能落到一个已经被冻结过的 Tick 上。
            _ingress.AdvanceCurrentTick(tick);
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
            _lastFinalFootprintRemovals.Clear();
            _claimedThisTick.Clear();
            _terminalCoordinator.BeginTick(tick);

            // —— 死亡清理（不依赖窗口）——
            ConsumeLifecycleCleanup(tick);

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
            CompleteDuePlans(tick, beforeResolution: true);
        }

        private void ConsumeLifecycleCleanup(long tick)
        {
            foreach (UnitLifecycleCleanupNotice notice in _pendingLifecycleCleanup)
            {
                _windowManager.RequestCloseForOwnerDeath(notice.UnitId);
                _scheduleAuthority.LockLaneSubmissions(notice.UnitId, LaneLockReasonOwnerDied);
                _terminalCoordinator.TerminateAllPlansOfUnit(notice.UnitId, ActionTerminationReason.OwnerDied, tick);
            }
            _pendingLifecycleCleanup.Clear();
        }

        private void CompleteDuePlans(long tick, bool beforeResolution)
        {
            // ActivePlans is a stable copy; terminal cleanup removes entries from the live Lane.
            foreach (ActionPlan plan in _scheduleAuthority.Registry.ActivePlans)
            {
                if (!plan.IsRunning || plan.EndTick > tick) continue;
                if (beforeResolution && ((plan.ActionType == ActionType.Attack && plan.ImpactTick == tick)
                    || (plan.IsReaction && plan.TriggerTick == tick))) continue;
                _terminalCoordinator.EnterCompletion(plan, tick);
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
        /// 唯一死亡提交点。阶段2处理状态/效果致死，阶段15处理Resolution致死。
        /// 按UnitId提交Dead、死亡事件、最终footprint移除和一次性通知；调用方在同一阶段
        /// 经统一终态协调器消费通知。阶段11造成的致死仍先完成位移、事件和控制提交。
        /// 同一Tick两次调用共同累积占位移除读数，下一Step边界才清空。
        /// </summary>
        private void ProcessDeaths(long tick)
        {
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

                // —— 裁定 6.2：死亡必须移除网格 footprint（不变量 33：时机就在本方法被调用的时刻）——
                //
                // 三条必须一起成立的事实：
                //   1. **时机**：本方法在阶段 15 被调用 ⇒ 阶段 11–13 的伤害/合力提交、只读同时求解
                //      与**一次批量换位**都已提交，因此致死单位在本 Tick 的位移依赖图里仍然是
                //      "在场"的占位者/被推动者，注销发生在换位之后；阶段 2 的补跑分支则整段跳过
                //      阶段 3–17，本 Tick 根本不存在位移。两种调用点都不违反不变量 33。
                //   2. **唯一权威**：注销的是 LogicGrid（空间权威），不是运行时镜像。
                //      镜像 unit.Position 只是"网格 → 单位"的单向投影（SyncGridLogicalPositions），
                //      不注销网格就等于把死人永久留在唯一权威里 ⇒ 幽灵阻挡。
                //   3. **幂等**：单位不在网格里时跳过。真实调用点不会命中该分支（单位在构造期
                //      全部注册，且 IsKillable/_notifiedLifecycleUnitIds 保证每个死者只走到这里一次），
                //      但"注销两次"必须不是缺陷形态，否则任何一个未来的补注册路径都会炸在这里。
                //
                // 注销发生在**死亡事件之后、生命周期通知之前**：事件顺序（位移事件 → 死亡事件）
                // 不受影响，而"通知携带 FinalPosition"只读 unit.Position，与网格注销无关。
                if (_logicGrid.Contains(unitId))
                {
                    string unregisterError = _logicGrid.UnregisterUnit(unitId);
                    if (unregisterError != null)
                    {
                        throw new LogicDefinitionException(unregisterError,
                            "death footprint removal unit=" + unitId.Value.ToString(CultureInfo.InvariantCulture));
                    }
                }

                // 一次性生命周期清理通知：供任务 05 按 ActionPlanId 经统一终态协调器
                // 终止死者全部非终态计划。死亡系统自己不动任何计划对象；
                // 通知在换位提交之后创建，携带的是**最终**位置。
                if (!_notifiedLifecycleUnitIds.Add(unitId.Value)) continue;

                var notice = new UnitLifecycleCleanupNotice(
                    tick, unitId, UnitLifecycleNoticeReasons.Death, unit.Position, remaining);
                _lifecycleNotices.Add(notice);
                _pendingLifecycleCleanup.Add(notice);
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
            // 脚本/装配的窗口排程先在**本 Tick** 到期处排定，再由唯一管理器打开。
            _windowManager.ScheduleDueWindow(tick, _assembly.TurnWindowSchedule);
            _windowManager.TryOpenDueWindow(tick);
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
            // 任务 09（产出 6）：阶段 6 只有**一个**入口 —— BattleCommandProcessor。
            // 它在任何载荷处理之前先用权威 ControllerId -> UnitId 集合校验发行者能控制
            // 其涉及的单位，然后把已授权的载荷路由到排程 / 反应 / 窗口三个端口；
            // 排程仍由唯一的 ScheduleEditor 事务写入，不存在第二套排程路径。
            _planCommandProcessor.BeginTick();
            _commandProcessor.BeginTick();
            IReadOnlyList<CommandRejectionRecord> processorRejections =
                _commandProcessor.ProcessOrdered(set.Envelopes, tick, _batchBaseScheduleRevision);

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
            InterruptControlledRunningPlans(tick);
            List<ActionPlan> due = CollectDueEditablePlans(tick);

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
            AdvanceFixedReactions(tick);
            foreach (ActionPlan running in _scheduleAuthority.Registry.ActivePlans)
                if (running.IsRunning) StartActorPhase(running, tick);
        }

        private static bool IsControlState(UnitState state)
            => state == UnitState.Staggered || state == UnitState.KnockedDown || state == UnitState.Recovering;

        private void InterruptControlledRunningPlans(long tick)
        {
            foreach (ActionPlan plan in _scheduleAuthority.Registry.ActivePlans)
            {
                if (!plan.IsRunning) continue;
                if (IsControlState(FindUnitStateMachine(plan.OwnerUnitId).CurrentState))
                    _terminalCoordinator.EnterTerminal(plan, ActionTerminationReason.InterruptedByControl, tick);
            }
        }

        private void AdvanceFixedReactions(long tick)
        {
            foreach (ActionPlan plan in _scheduleAuthority.Registry.ActivePlans)
            {
                if (!plan.IsReaction || plan.StartTick > tick) continue;
                if (!IsUnitAlive(plan.OwnerUnitId))
                {
                    _terminalCoordinator.EnterTerminal(plan, ActionTerminationReason.OwnerDied, tick);
                    continue;
                }
                if (IsControlState(FindUnitStateMachine(plan.OwnerUnitId).CurrentState))
                {
                    _terminalCoordinator.EnterTerminal(plan, ActionTerminationReason.InterruptedByControl, tick);
                    continue;
                }
                ActionPlan source = _scheduleAuthority.Registry.Find(plan.SourceThreatPlanId.Value);
                if (tick <= plan.TriggerTick && (source == null || source.IsTerminal))
                {
                    _terminalCoordinator.EnterTerminal(plan, ActionTerminationReason.SourceThreatCancelled, tick);
                    continue;
                }
                if (plan.IsLocked)
                {
                    plan.State = ActionPlanState.Running;
                    StartActorPhase(plan, tick);
                }
                if (plan.ActionType == ActionType.Block && plan.TriggerTick == tick)
                {
                    string error = _reactionSystem.ConfirmTrigger(plan.ReactionOpportunityId.Value, tick);
                    if (error != null) throw new LogicDefinitionException(SimulationCodes.STEP_INVARIANT_VIOLATION, error);
                }
            }
        }

        private void StartActorPhase(ActionPlan plan, long tick)
        {
            if (tick >= plan.EndTick) return;
            UnitState state = UnitState.Recovery;
            long phaseEnd = plan.EndTick;
            switch (plan.ActionType)
            {
                case ActionType.Attack:
                    if (tick < plan.ImpactTick) { state = UnitState.Windup; phaseEnd = plan.ImpactTick; }
                    break;
                case ActionType.Guard:
                    if (tick < plan.ActiveStartTick) { state = UnitState.Windup; phaseEnd = plan.ActiveStartTick; }
                    else if (tick < plan.ActiveEndTick) { state = UnitState.Guarding; phaseEnd = plan.ActiveEndTick; }
                    break;
                case ActionType.Move:
                    long moveEnd = checked(plan.StartTick + plan.MoveDurationTicks);
                    if (tick < moveEnd) { state = UnitState.Moving; phaseEnd = moveEnd; }
                    break;
                case ActionType.Block:
                case ActionType.Dodge:
                    if (tick < plan.TriggerTick)
                    {
                        state = plan.ActionType == ActionType.Block ? UnitState.Blocking : UnitState.Dodging;
                        phaseEnd = plan.TriggerTick;
                    }
                    break;
            }
            UnitStateMachine machine = FindUnitStateMachine(plan.OwnerUnitId);
            if (machine.CurrentState == state) return;
            var outcome = machine.TryTransition(StateTransitionSpec.Timed(state,
                checked((int)(phaseEnd - tick)), UnitState.Idle), tick, UnitStateTransitionReasons.Explicit);
            if (!outcome.Applied) throw new LogicDefinitionException(SimulationCodes.STEP_INVARIANT_VIOLATION,
                "action-phase|" + plan.ActionPlanId + "|" + outcome.RejectionCode);
        }
        private void ResetActorPhaseAfterTerminal(ActionPlan plan, long tick)
        {
            if (plan.LockedAtTick < 0 || plan.StartTick > tick) return;
            UnitStateMachine machine = FindUnitStateMachine(plan.OwnerUnitId);
            if (machine == null || machine.CurrentState == UnitState.Idle || machine.IsTerminal
                || IsControlState(machine.CurrentState)) return;
            foreach (ActionPlan other in _scheduleAuthority.Registry.ActivePlans)
                if (other.OwnerUnitId == plan.OwnerUnitId && other.IsRunning) return;
            var reset = machine.TryTransition(StateTransitionSpec.Open(UnitState.Idle), tick,
                UnitStateTransitionReasons.Explicit);
            if (!reset.Applied) throw new LogicDefinitionException(SimulationCodes.STEP_INVARIANT_VIOLATION, reset.RejectionCode);
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
            StartActorPhase(plan, tick);
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
        /// <summary>
        /// 任务 07 裁定 A（排程删除的终态收口）：一次成功提交里被 Remove 的普通计划必须经
        /// <strong>唯一且幂等的终态协调器</strong>结束（00 号规则 19：排程删除是终态之一，
        /// 且 Step 返回时不得有任何活动产物引用它）。
        ///
        /// 分工（裁定：编辑器独占预算释放）：<c>ScheduleEditor</c> 在事务里负责"移出 Lane +
        /// 释放未消费预留 + 归零计划字段"；本方法只负责把计划对象**置为终态**。
        /// 600 槽位的预算参与者随后会发现账本里已无该计划的预留，因此幂等空转——
        /// 不存在第二次释放，也不存在"已删除但仍活动"的中间态。
        /// </summary>
        private void TerminateRemovedPlans(ScheduleEditTransactionResult result, long tick)
        {
            if (result == null || result.RemovedPlanIds == null || result.RemovedPlanIds.Count == 0) return;

            for (int i = 0; i < result.RemovedPlanIds.Count; i++)
            {
                ActionPlan plan = _scheduleAuthority.Registry.Find(result.RemovedPlanIds[i]);
                if (plan == null || plan.IsTerminal) continue;
                _terminalCoordinator.EnterTerminal(plan, ActionTerminationReason.CancelledByCommand, tick);
            }
        }

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
        /// <summary>
        /// 窗口命令的唯一处理入口（任务 07「必须产出」5）。
        ///
        /// 它<strong>不</strong>解析任何自报身份或费用：发行者来自 <see cref="CommandEnvelope.ControllerId"/>，
        /// 目标窗口来自 <see cref="WindowCommandScope.ExpectedWindowId"/>，
        /// 并发能力费用只来自权威 <c>ConcurrentActionDefinition</c>。
        /// 返回非 null = 稳定拒绝码（本命令零副作用，同批其他命令不受影响）。
        /// </summary>
        private string HandleWindowCommand(CommandEnvelope envelope, long tick)
        {
            if (envelope == null) return Commands.CommandCodes.COMMAND_REQUEST_NULL;
            if (!(envelope.Request.Payload is WindowCommandPayload payload))
                return Commands.CommandCodes.SCOPE_PAYLOAD_MISMATCH;
            if (!(envelope.Request.Scope is WindowCommandScope scope))
                return Commands.CommandCodes.SCOPE_PAYLOAD_MISMATCH;

            switch (payload.WindowKind)
            {
                case WindowCommandKind.CloseOwnWindow:
                    return _windowManager.TryRequestCloseForIssuer(envelope.ControllerId, scope.ExpectedWindowId);

                case WindowCommandKind.ActivateConcurrentAction:
                    if (!_concurrentAction.HeroUnitId.HasValue)
                        return TurnWindowCodes.INVALID_CONCURRENT_ACTOR;
                    return _concurrentAction.TryActivate(
                        envelope.ControllerId, _concurrentAction.HeroUnitId.Value, scope.ExpectedWindowId);

                default:
                    return Commands.CommandCodes.SCOPE_PAYLOAD_MISMATCH;
            }
        }

        /// <summary>
        /// 反应命令的处理入口已唯一化为 <see cref="ReactionCommandPlanner"/>
        /// （任务 09 产出 7）：本类<strong>不再</strong>保留第二份反应载荷解析实现。
        /// 控制权校验用的 <see cref="IReactionCommandUnitSource"/> 与计划创建读的是
        /// <strong>同一个</strong>权威机会对象。
        /// </summary>
        private string HandleReactionCommand(CommandEnvelope envelope, long tick)
            => _reactionCommandPlanner.ProcessAuthorized(envelope, tick, _batchBaseScheduleRevision);

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


        /// <summary>
        /// 阶段 8（<see cref="StepPhase.IntentDrain"/>）：<strong>物化</strong>本 Tick 到期的攻击 Intent，
        /// 再交给全局队列<strong>冻结</strong>（任务 08「必须产出」1 与 4）。
        ///
        /// 固定两步顺序：
        /// <list type="number">
        /// <item><strong>物化</strong>：对每个 <c>ActionType.Attack</c>、<strong>非终态</strong>、
        /// 且 <c>ImpactTick == tick</c> 的计划调用一次
        /// <see cref="CombatIntentFactory.CreateAtImpactTick"/>。唯一性契约由工厂内部
        /// <see cref="CombatIntentContract.ValidateMaterialization"/> 强制（非攻击 / 已终态 /
        /// 命中时刻不匹配都以稳定原因码抛出，<strong>不</strong>静默跳过）。</item>
        /// <item><see cref="GlobalIntentQueue.Freeze"/>：只按 <c>ImpactTick == tick</c> 取到期项并稳定排序
        /// <c>(Tick, InteractionPriority, IntentSequence)</c>。该 API <strong>没有</strong>窗口参数 ⇒
        /// 「不按提交窗口过滤」（不变量 5）是<strong>结构性</strong>保证，不是本方法的纪律。</item>
        /// </list>
        ///
        /// 为什么计划按 <see cref="ActionPlanRegistry.ActivePlans"/> 的规范顺序扫描、序号也在扫描中分配：
        /// <c>IntentSequence</c> 进入稳定排序键与快照哈希，因此它必须是
        /// <strong>计划集合（多重集合）的纯函数</strong>。注册表的活动视图已经按 <c>ActionPlanId</c> 升序
        /// 拷贝，扫描顺序与容器内部顺序无关；同一批计划以任何顺序入表都得到同一批序号。
        ///
        /// 刻意<strong>不</strong>读取计划的 Recovery/Active 阶段状态、窗口、单位状态或
        /// <c>CanReceiveDirectHit</c>：计划在 <c>ImpactTick</c> 上可以已经进入 Recovery，
        /// 合法 Intent 不得因此被过滤（<c>08-多方仲裁与伤害.md:59</c>）。
        /// </summary>
        private void DrainIntents(long tick)
        {
            IReadOnlyList<ActionPlan> plans = _scheduleAuthority.Registry.ActivePlans;
            var produced = new List<CombatIntent>();

            for (int i = 0; i < plans.Count; i++)
            {
                ActionPlan plan = plans[i];
                if (plan == null) continue;
                if (plan.ActionType != ActionType.Attack) continue;
                if (plan.IsTerminal) continue;
                if (plan.ImpactTick != tick) continue;

                produced.Add(MaterializeAttackIntent(plan, tick));
            }

            _intentQueue = GlobalIntentQueue.Freeze(tick, produced);
        }

        /// <summary>
        /// 把一个到期攻击计划物化成攻击 Intent（阶段 8 的<strong>唯一</strong>物化点）。
        ///
        /// 载荷只来自已验证定义（<see cref="BattleDefinition.FindAction"/> → <see cref="AttackPayloadSpec"/>）
        /// 与计划自身的固定事实，动量经<strong>唯一一次</strong>量化
        /// （<see cref="MomentumQuantizer.QuantizePacket"/>）落到整数域；这里不读任何单位运行时状态、
        /// 不推导命中时刻、不旋转 Pattern。
        ///
        /// 交互优先级固定为 <see cref="InteractionPriorities.AttackVsAttack"/>：那是
        /// <strong>攻击 Intent 节点自身</strong>的矩阵阶段值。矩阵的阶段 1/2/4/5
        /// （Attack vs Dodge/Block/Move）是同一批节点与<strong>对手计划</strong>形成的接触类别，
        /// 由候选构建器按对手计划动作族分类，<strong>不得</strong>提前写进节点值
        /// （否则同一 Intent 在不同对手下会取到不同优先级，稳定排序键与快照哈希都会漂移）。
        /// </summary>
        private CombatIntent MaterializeAttackIntent(ActionPlan plan, long tick)
        {
            InteractionPlanFacts facts = InteractionPlanFacts.From(plan);

            ActionSpec spec = _definition.FindAction(plan.ActionSpecId);
            if (spec == null)
            {
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND,
                    plan.ActionSpecId.Value ?? string.Empty);
            }

            var payload = spec.Payload as AttackPayloadSpec;
            if (payload == null)
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_NOT_ATTACK,
                    "plan=" + plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (!_unitsById.TryGetValue(plan.OwnerUnitId.Value, out UnitRuntimeState owner))
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_IDENTITY_INVALID,
                    "owner=" + plan.OwnerUnitId.Value.ToString(CultureInfo.InvariantCulture));
            }

            UnitDefinition unitDefinition = _definition.FindUnit(owner.DefinitionId);
            if (unitDefinition == null)
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_IDENTITY_INVALID,
                    "definition=" + owner.DefinitionId.Value ?? string.Empty);
            }

            // 唯一一次量化（Mass / MomentumSpeed / ForceMultiplier 在此之后不再被读取）。
            MomentumPacket momentum = MomentumQuantizer.QuantizePacket(
                plan.Facing, payload.MomentumDirectionOffsetSteps,
                unitDefinition.Mass, unitDefinition.MomentumSpeed, payload.ForceMultiplier,
                payload.ImpactProfileId);

            // 锚点/朝向的<strong>唯一</strong>来源是空间权威 LogicGrid 的只读投影。
            // 这里刻意<strong>不</strong>回落成运行时镜像（unit.Position/Facing）：那个镜像只在权威提交边界
            // 同步，把它当成"网格查不到时的第二答案"就是把第二份位置真值偷偷放回装配侧。
            // 活单位在构造期全部注册进 LogicGrid；死亡会在**死亡阶段**把死者注销（裁定 6.2），
            // 但那时该单位的全部非终态计划已在**下一 Tick 的阶段 0** 经统一终态协调器终止，
            // 因此本阶段（阶段 8）永远看不到"死者的到期攻击计划"：查不到只可能是装配缺陷 ⇒ 以稳定码抛出。
            if (!_logicGrid.TryGetAnchor(plan.OwnerUnitId, out GridPoint anchor)
                || !_logicGrid.TryGetFacing(plan.OwnerUnitId, out GridDirection facing))
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_IDENTITY_INVALID,
                    "unit not registered in LogicGrid: owner="
                    + plan.OwnerUnitId.Value.ToString(CultureInfo.InvariantCulture));
            }

            return CombatIntentFactory.CreateAtImpactTick(
                facts,
                plan.ActionSpecId,
                plan.Facing,
                payload,
                anchor,
                tick,
                InteractionPriorities.AttackVsAttack,
                _sequences.TakeIntentSequence(),
                momentum,
                payload.MomentumDirectionOffsetSteps,
                plan.SubmittedWindowId);
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

        /// <summary>
        /// 阶段 10（<see cref="StepPhase.ConflictGraphAndResolution"/>）：
        /// <strong>规范化冲突图</strong>的构建（任务 08「必须产出」5–7 的构图部分）。
        ///
        /// 输入恰好三份只读事实，构图期<strong>不再</strong>访问 LogicGrid、计划索引、
        /// 单位状态或事件队列：
        /// <list type="number">
        /// <item><see cref="_intentQueue"/>：阶段 8 冻结的本 Tick 到期 Intent（<strong>不得</strong>追溯删除）；</item>
        /// <item><see cref="_dodgeCommitReport"/>：阶段 9 的<b>提交前</b>/<b>提交后</b>统一只读空间快照
        /// （<see cref="DodgeCommitReport.Before"/>/<see cref="DodgeCommitReport.After"/>），
        /// 由它投影出"每单位已占用 <see cref="TrianglePoint"/>"的旧格/新格两份；</item>
        /// <item><see cref="InteractionPlanFacts.From"/> 投影出的活动计划事实表
        /// （节点资格与对手计划判定的唯一来源）。</item>
        /// </list>
        ///
        /// <strong>只传活动计划是不是会漏掉合法节点？</strong>不会：<c>ImpactTick == tick</c> 的计划
        /// 在终态之后<strong>立刻</strong>离开活动索引（阶段 7 的终态协调器），而
        /// <see cref="CombatIntentContract.ValidateMaterialization"/> 已经在阶段 8 把
        /// "已终态"挡在物化之前；因此冻结队列的每个 Intent 在这里必然能按
        /// <c>ActionPlanId</c> 找到自己的计划事实。反之若传更窄的子集，
        /// 候选构建器会以 <see cref="InteractionCodes.CONTACT_INPUT_INVALID"/> 显式拒绝
        /// （"planFacts missing"），绝不会把"缺计划"静默当成"不成立"。
        ///
        /// 构图失败（单组超节点/边/目标上限）<strong>不</strong>在这里抛：
        /// <see cref="ConflictGraphBuilder.TryBuild"/> 的失败结果被保留在只读字段上，
        /// 供后续 Resolution 步骤按"整组失败、不丢边不拆组"的口径处理。
        /// </summary>
        private void BuildConflictGraphAndResolve(long tick)
        {
            _conflictGraph = null;
            _conflictGraphError = null;
            _stagedResolution = StagedConflictResolution.Empty(tick);
            _conflictGroupKeyByPlan = new Dictionary<long, long>();

            IReadOnlyList<InteractionPlanFacts> planFacts = BuildInteractionPlanFacts();
            IReadOnlyList<UnitOccupancy> unitsBefore = BuildOccupancy(_dodgeCommitReport.Before);
            IReadOnlyList<UnitOccupancy> unitsAfter = BuildOccupancy(_dodgeCommitReport.After);

            var input = new ConflictGraphInput(
                tick,
                _intentQueue == null ? Array.Empty<CombatIntent>() : _intentQueue.Intents,
                planFacts,
                unitsBefore,
                unitsAfter,
                _factionResolver);

            ConflictGraphBuildResult result = ConflictGraphBuilder.TryBuild(input, out ConflictGraph graph);
            if (!result.Succeeded)
            {
                // 冻结口径：整组失败（不丢边、不拆组、不截断目标）。失败码继续可观察，
                // 同时分阶段求解以同一码整体拒绝 ⇒ 本 Tick 不产生任何 Resolution。
                _conflictGraphError = result.ErrorCode;
                _stagedResolution = StagedConflictResolver.Resolve(new StagedResolutionContext(
                    null, result.ErrorCode, result.FailingGroupKey,
                    planFacts, unitsBefore, unitsAfter, _dodgeCommitReport,
                    BuildUnitSnapshots(), _definition.Units, BuildPlanSpecEntries(),
                    BuildBlockPayloadEntries(), BuildGuardPayloadEntries(), BuildClashQuota()));
                return;
            }

            _conflictGraph = graph;

            // 分阶段求解（任务 08「必须产出」8–11）：
            //   1 Dodge 空间复核 → 2 Block 完全抵抗 → 3 Clash 同时求解 → 4 Move → 5 Remaining Hits 聚合。
            // 求解器是纯函数（没有写接口）⇒ "查询/求解阶段不得修改世界"由类型而不是纪律保证。
            // 三类 Resolution 的消费者在阶段 11（伤害提交）与阶段 14（经统一终态协调器提交 Clash 终态）。
            _stagedResolution = StagedConflictResolver.Resolve(new StagedResolutionContext(
                graph, null, 0L,
                planFacts, unitsBefore, unitsAfter, _dodgeCommitReport,
                BuildUnitSnapshots(), _definition.Units, BuildPlanSpecEntries(),
                BuildBlockPayloadEntries(), BuildGuardPayloadEntries(), BuildClashQuota()));

            // 计划 → 冲突组键的只读投影：图在构图后**不再变更**，因此这份投影在本 Tick
            // 剩余阶段（11/13/14）里恒等于构图结果；语义事件与位移请求都取它。
            _conflictGroupKeyByPlan = ConflictGroupKeys.BuildByPlan(graph);

            // 任务包 08「必须产出」15：语义事件族。发射点留在既有阶段上（**不新增 Step 阶段**），
            // 且只描述**已经求解完成**的事实——事件不改变世界，也不携带任何表现策略（不变量 10）。
            EmitInteractionResolutionEvents(tick);
        }

        // =====================================================================
        // 任务包 08「必须产出」15：语义事件族（阶段 10 发射点）
        // =====================================================================

        /// <summary>
        /// 把本 Tick <strong>已经求解完成</strong>的分阶段结果广播为语义事件。
        ///
        /// <list type="bullet">
        /// <item><strong>只描述战斗事实</strong>（不变量 10）：冲突组键、参与计划/单位 ID、
        /// Raw/被动后/动作后伤害、抵抗前后动量、请求/实际步数、From/To、停止原因、
        /// 稳定排序的失效计划 ID。没有震屏、顿帧、动画时长或插值；</item>
        /// <item><strong>不修改世界</strong>：本方法只读 <c>_stagedResolution</c> 与
        /// <c>_dodgeCommitReport</c>，因此"求解阶段零写入"仍然成立；</item>
        /// <item><strong>不新增 Step 阶段</strong>：它就在阶段 10 的现有位置里调用，
        /// 位移最终结果事件仍在阶段 13、Clash 终态请求仍在阶段 14。</item>
        /// </list>
        /// </summary>
        private void EmitInteractionResolutionEvents(long tick)
        {
            StagedConflictResolution staged = _stagedResolution;
            if (staged == null || staged.Failed) return;

            EmitDodgeResolvedEvents(tick, staged);
            foreach (DodgeCounterWindowResolution window in staged.CounterWindows)
                _outbox.Emit(sequence => new DodgeCounterWindowOpenedEvent(tick, sequence, window.ConflictGroupKey,
                    window.DefenderUnitId, window.DodgePlanId, window.CounterTargetUnitId,
                    window.CounterTargetPlanId, window.AvoidedAttackPlanIds));
            EmitBlockResolvedEvents(tick, staged);
            EmitGuardResistanceEvents(tick, staged);
            EmitClashEvents(tick, staged);
            EmitMoveContactEvents(tick, staged);
            EmitDamageAndAggregateEvents(tick, staged);
        }

        /// <summary>
        /// Dodge 旧/新位置与失效接触（任务包 08:72）。
        ///
        /// 每个<strong>实际进入 TriggerTick 提交尝试</strong>的 Dodge 计划恰好一条：
        /// 提交成功的发 <c>ReactionTriggeredEvent</c> 的语义由任务 05/06 承担，
        /// 本处只发"求解后的结算事实"。提前终止不伪造触发（没有计划就没有事件）。
        /// </summary>
        private void EmitDodgeResolvedEvents(long tick, StagedConflictResolution staged)
        {
            for (int p = 0; p < staged.DodgePlans.Count; p++)
            {
                DodgePlanResolution plan = staged.DodgePlans[p];
                if (plan == null) continue;

                var invalidated = new List<ActionPlanId>();
                var stillHit = new List<ActionPlanId>();
                var retained = new List<ActionPlanId>();
                var participating = new List<ActionPlanId>();
                long groupKey = 0L;

                IReadOnlyList<DodgeContactResolution> contacts = plan.Contacts;
                for (int c = 0; c < contacts.Count; c++)
                {
                    DodgeContactResolution contact = contacts[c];
                    if (contact == null) continue;
                    switch (contact.Outcome)
                    {
                        case DodgeContactOutcome.Dodged:
                            invalidated.Add(contact.AttackPlanId);
                            break;
                        case DodgeContactOutcome.StillHit:
                            stillHit.Add(contact.AttackPlanId);
                            break;
                        case DodgeContactOutcome.RetainedUndodgeable:
                            retained.Add(contact.AttackPlanId);
                            break;
                    }
                    participating.Add(contact.AttackPlanId);
                    if (groupKey == 0L) groupKey = ConflictGroupKeyOf(contact.AttackPlanId);
                }
                participating.Add(plan.DodgePlanId);

                bool rewarded = plan.DestinationCommitted && plan.AvoidedAnyContact;
                bool committed = plan.DestinationCommitted;
                _outbox.Emit(sequence => new DodgeResolvedEvent(
                    tick, sequence, groupKey, plan.DodgePlanId, plan.DefenderUnitId,
                    plan.From, plan.Destination, committed, plan.FailureCode, rewarded,
                    OrderedPlanIds(invalidated), OrderedPlanIds(stillHit), OrderedPlanIds(retained),
                    OrderedPlanIds(participating)));
            }
        }

        /// <summary>Block 完全/部分载荷抵抗 + 逐计划解析结果（含"无效格挡零奖励"）。</summary>
        private void EmitBlockResolvedEvents(long tick, StagedConflictResolution staged)
        {
            for (int p = 0; p < staged.BlockPlans.Count; p++)
            {
                BlockPlanResolution plan = staged.BlockPlans[p];
                if (plan == null) continue;

                var attacks = new List<ActionPlanId>();
                bool allBlocked = true;

                for (int c = 0; c < plan.Contacts.Count; c++)
                {
                    BlockContactResolution contact = plan.Contacts[c];
                    if (contact == null) continue;

                    long groupKey = ConflictGroupKeyOf(contact.AttackPlanId);
                    bool rewards = contact.RewardsBlock;
                    BlockContactResolution captured = contact;
                    _outbox.Emit(sequence => new BlockResolvedEvent(
                        tick, sequence, groupKey, captured.BlockPlanId, captured.DefenderUnitId,
                        captured.AttackPlanId, captured.AttackerUnitId, captured.Outcome, rewards,
                        captured.RawDamageQ10, captured.AfterBlockDamageQ10,
                        captured.IncomingMomentumUnits, captured.AfterBlockMomentumUnits,
                        OrderedPlanIds(new[] { contact.AttackPlanId, contact.BlockPlanId })));

                    attacks.Add(contact.AttackPlanId);
                    allBlocked &= contact.Outcome == BlockContactOutcome.Blocked;
                }

                // 逐计划一次（任务包 08:44「每反应计划最多一个成功 Block 键」的事件面）。
                // RewardsSuccess 与 Kind 的一致性：只有 Blocked/PartiallyBlocked 才可能为 true。
                bool planRewards = plan.AnyEligibleContact;
                ReactionResolutionKind kind = !planRewards ? ReactionResolutionKind.BlockIneffective
                    : allBlocked ? ReactionResolutionKind.BlockedFully : ReactionResolutionKind.BlockedPartially;
                string reason = !planRewards ? InteractionEventCodes.CONTACT_BLOCK_INELIGIBLE
                    : allBlocked ? InteractionEventCodes.CONTACT_FULLY_RESISTED : InteractionEventCodes.CONTACT_PARTIALLY_RESISTED;
                var attackIds = OrderedPlanIds(attacks);
                ActionPlanId blockPlanId = plan.BlockPlanId;
                UnitId defenderUnitId = plan.DefenderUnitId;
                long planGroupKey = GroupKeyOfAny(attacks);
                _outbox.Emit(sequence => new ReactionPlanResolvedEvent(
                    tick, sequence, planGroupKey, blockPlanId, defenderUnitId, kind, planRewards,
                    reason, attackIds, Array.Empty<ActionPlanId>()));
            }
        }

        /// <summary>Guard 部分抵抗（不终止来源攻击；只描述抵抗前后载荷与动量）。</summary>
        private void EmitGuardResistanceEvents(long tick, StagedConflictResolution staged)
        {
            for (int i = 0; i < staged.RemainingHits.Count; i++)
            {
                RemainingHitResolution hit = staged.RemainingHits[i];
                if (hit == null || hit.Aggregate == null) continue;
                if (hit.GuardPlanId.Value <= 0L) continue;

                long raw = 0L;
                long afterPassive = 0L;
                long afterGuard = 0L;
                int incomingMomentum = 0;
                int afterMomentum = 0;
                IReadOnlyList<TargetContactResolution> contacts = hit.Aggregate.Contacts;
                for (int c = 0; c < contacts.Count; c++)
                {
                    TargetContactResolution contact = contacts[c];
                    if (contact == null || contact.Key.SourceActionPlanId != hit.AttackPlanId.Value || contact.Key.SourceUnitId != hit.AttackerUnitId.Value) continue;
                    incomingMomentum += contact.IncomingMomentumUnits;
                    afterMomentum += contact.AfterMomentumResistanceUnits;
                    for (int k = 0; k < contact.Components.Count; k++)
                    {
                        AggregatedContactComponent component = contact.Components[k];
                        raw += component.RawQ10;
                        afterPassive += component.AfterPassiveResistanceQ10;
                        afterGuard += component.AfterActionResistanceQ10;
                    }
                }

                long groupKey = ConflictGroupKeyOf(hit.AttackPlanId);
                ActionPlanId guardPlanId = hit.GuardPlanId;
                string guardSpecId = hit.GuardSpecId;
                ActionPlanId attackPlanId = hit.AttackPlanId;
                UnitId attackerUnitId = hit.AttackerUnitId;
                UnitId targetUnitId = hit.Aggregate.TargetUnitId;
                GridDirection incomingDirection = hit.IncomingDirection;
                _outbox.Emit(sequence => new GuardPartiallyResistedEvent(
                    tick, sequence, groupKey, guardPlanId, targetUnitId, guardSpecId,
                    attackPlanId, attackerUnitId, incomingDirection,
                    raw, afterPassive, afterGuard, raw - afterGuard, incomingMomentum, afterMomentum,
                    OrderedPlanIds(new[] { attackPlanId, guardPlanId })));
            }
        }

        /// <summary>拼刀：逐参与者结算 + ClashResidualImpact（同一连通块共用一个组键）。</summary>
        private void EmitClashEvents(long tick, StagedConflictResolution staged)
        {
            for (int g = 0; g < staged.Clashes.Count; g++)
            {
                ClashComponentResolution clash = staged.Clashes[g];
                if (clash == null || clash.Clash == null) continue;

                long groupKey = clash.ConflictGroupKey;
                MomentumClashResolution resolution = clash.Clash;
                IReadOnlyList<ActionPlanId> participating = resolution.TerminatedActionPlanIds;

                for (int p = 0; p < resolution.Participants.Count; p++)
                {
                    ClashParticipantResolution participant = resolution.Participants[p];
                    if (participant == null) continue;
                    _outbox.Emit(sequence => new ClashParticipantResolvedEvent(
                        tick, sequence, groupKey, participant.ActionPlanId, participant.OwnerUnitId,
                        participant.Direction, participant.OriginalMomentumUnits,
                        participant.TotalOppositionLossUnits, participant.RemainingMomentumUnits,
                        participant.Losses ?? Array.Empty<ClashOppositionLoss>(), participating));
                }

                for (int r = 0; r < resolution.ResidualImpacts.Count; r++)
                {
                    ClashResidualImpact residual = resolution.ResidualImpacts[r];
                    if (residual == null) continue;
                    _outbox.Emit(sequence => new ClashResidualImpactResolvedEvent(
                        tick, sequence, groupKey,
                        residual.RecipientUnitId, residual.RecipientActionPlanId,
                        residual.SourceUnitId, residual.SourceActionPlanId, residual.SourceDirection,
                        residual.ResidualMomentumUnits,
                        resolution.TotalRemainingMomentumUnits, resolution.TotalResidualAllocatedUnits));
                }
            }
        }

        /// <summary>移动拦截/逃脱（旧格/新格覆盖标记由求解器给出，本处不复核几何）。</summary>
        private void EmitMoveContactEvents(long tick, StagedConflictResolution staged)
        {
            for (int i = 0; i < staged.Moves.Count; i++)
            {
                MoveContactResolution move = staged.Moves[i];
                if (move == null) continue;
                long groupKey = ConflictGroupKeyOf(move.AttackPlanId);
                _outbox.Emit(sequence => new MoveContactResolvedEvent(
                    tick, sequence, groupKey, move.AttackPlanId, move.AttackerUnitId,
                    move.MovePlanId, move.MovingUnitId, move.CoveredBefore, move.CoveredAfter, move.Outcome));
            }
        }

        /// <summary>
        /// 分通道伤害明细 + 目标聚合冲击（任务包 08:39/43）。
        ///
        /// 一个目标单位在本 Tick 的聚合结果<strong>只发一条</strong>冲击事件；
        /// 逐接触的分通道明细各发一条（多条接触共享同一聚合对象）。
        /// </summary>
        private void EmitDamageAndAggregateEvents(long tick, StagedConflictResolution staged)
        {
            for (int i = 0; i < staged.RemainingHits.Count; i++)
            {
                RemainingHitResolution hit = staged.RemainingHits[i];
                if (hit == null || hit.Aggregate == null) continue;
                TargetAggregateResolution aggregate = hit.Aggregate;

                var channels = new List<DamageChannelEntry>();
                int afterMomentum = 0;
                long contactDamage = 0;
                foreach (TargetContactResolution contact in aggregate.Contacts)
                {
                    if (contact.Key.SourceActionPlanId != hit.AttackPlanId.Value
                        || contact.Key.SourceUnitId != hit.AttackerUnitId.Value) continue;
                    afterMomentum = checked(afterMomentum + contact.AfterMomentumResistanceUnits);
                    foreach (AggregatedContactComponent component in contact.Components)
                    {
                        // Keep distinct component tags/resistance values; merging them would hide bypass payloads.
                        channels.Add(new DamageChannelEntry(component.ChannelId, component.RawQ10,
                            component.AfterPassiveResistanceQ10, component.AfterActionResistanceQ10,
                            component.PassiveResistanceQ10, component.ActionResistanceQ10, component.Tags));
                        contactDamage = checked(contactDamage + component.AfterActionResistanceQ10);
                    }
                }
                long groupKey = ConflictGroupKeyOf(hit.AttackPlanId);
                // 停止原因只描述**已提交事实**：状态抑制 ⇒ 抑制码；动作抵抗后载荷严格为 0
                // 且原载荷非 0 ⇒ 完全抵抗；否则按原载荷命中。
                long rawTotal = 0L;
                for (int c = 0; c < channels.Count; c++) rawTotal += channels[c].RawQ10;
                string stopReason = hit.IsDirectHitSuppressed
                    ? InteractionEventCodes.CONTACT_SUPPRESSED_BY_STATE
                    : rawTotal > 0L && contactDamage == 0L
                        ? InteractionEventCodes.CONTACT_FULLY_RESISTED
                        : InteractionEventCodes.CONTACT_UNRESISTED;

                var orderedChannels = channels.AsReadOnly();
                long afterBlockDamage = contactDamage;

                ActionPlanId attackPlanId = hit.AttackPlanId;
                UnitId attackerUnitId = hit.AttackerUnitId;
                ActionPlanId guardPlanId = hit.GuardPlanId;
                UnitId targetUnitId = aggregate.TargetUnitId;
                GridDirection incomingDirection = hit.IncomingDirection;
                int incomingMomentumUnits = hit.IncomingMomentumUnits;
                _outbox.Emit(sequence => new DamageChannelResolvedEvent(
                    tick, sequence, groupKey, targetUnitId, attackPlanId, attackerUnitId, guardPlanId,
                    incomingDirection, incomingMomentumUnits, afterMomentum, orderedChannels,
                    contactDamage, afterBlockDamage, stopReason,
                    OrderedPlanIds(new[] { attackPlanId, guardPlanId, hit.BlockPlanId, hit.RecipientPlanId })));
            }

            // 逐目标一条聚合冲击事件（按聚合结果的稳定顺序 = UnitId 升序）。
            IReadOnlyList<TargetAggregateResolution> aggregations = staged.Aggregations;
            for (int i = 0; i < aggregations.Count; i++)
            {
                TargetAggregateResolution aggregate = aggregations[i];
                if (aggregate == null) continue;
                long groupKey = GroupKeyOfTarget(staged, aggregate.TargetUnitId);
                string stopReason = aggregate.TotalDamageQ10 == 0L
                    ? InteractionEventCodes.CONTACT_FULLY_RESISTED
                    : InteractionEventCodes.CONTACT_UNRESISTED;
                AggregatedChannelDamage[] channels = new AggregatedChannelDamage[aggregate.ChannelTotals.Count];
                for (int c = 0; c < aggregate.ChannelTotals.Count; c++) channels[c] = aggregate.ChannelTotals[c];
                _outbox.Emit(sequence => new TargetAggregateResolvedEvent(
                    tick, sequence, groupKey, aggregate.TargetUnitId, channels,
                    aggregate.TotalDamageQ10, aggregate.TotalImpactUnits, aggregate.IncomingMomentumUnits,
                    aggregate.ResultantMomentumUnits, aggregate.ResultantDirection,
                    aggregate.ControlResistanceUnits, aggregate.IsStaggered, aggregate.IsKnockedDown,
                    aggregate.KnockbackSteps, stopReason));
            }
        }

        /// <summary>计划 → 本 Tick 冲突组键（未入图 ⇒ <c>0</c>，与既有事件口径一致）。</summary>
        private long ConflictGroupKeyOf(ActionPlanId planId)
            => ConflictGroupKeys.Of(_conflictGroupKeyByPlan, planId);

        /// <summary>一组计划里第一个已入图的组键（全未入图 ⇒ <c>0</c>）。</summary>
        private long GroupKeyOfAny(IReadOnlyList<ActionPlanId> planIds)
        {
            if (planIds == null) return 0L;
            for (int i = 0; i < planIds.Count; i++)
            {
                long key = ConflictGroupKeyOf(planIds[i]);
                if (key != 0L) return key;
            }
            return 0L;
        }

        /// <summary>某目标单位在本 Tick 归属的冲突组键（取该目标全部命中的攻击计划所属组）。</summary>
        private long GroupKeyOfTarget(StagedConflictResolution staged, UnitId targetUnitId)
        {
            long found = 0L;
            for (int i = 0; i < staged.RemainingHits.Count; i++)
            {
                RemainingHitResolution hit = staged.RemainingHits[i];
                if (hit == null || hit.Aggregate == null || hit.Aggregate.TargetUnitId != targetUnitId) continue;
                long key = ConflictGroupKeyOf(hit.AttackPlanId);
                if (key == 0L) continue;
                if (found != 0L && found != key)
                    throw new LogicDefinitionException(ConflictGroupKeys.CONFLICT_GRAPH_INCONSISTENT,
                        "target=" + targetUnitId.Value.ToString(CultureInfo.InvariantCulture));
                found = key;
            }
            return found;
        }

        /// <summary>稳定排序 + 去重的计划 ID 列表（事件载荷的唯一顺序契约）。</summary>
        private static IReadOnlyList<ActionPlanId> OrderedPlanIds(IReadOnlyList<ActionPlanId> planIds)
        {
            if (planIds == null || planIds.Count == 0) return Array.Empty<ActionPlanId>();
            var ordered = new List<ActionPlanId>(planIds.Count);
            for (int i = 0; i < planIds.Count; i++)
            {
                ActionPlanId planId = planIds[i];
                if (planId.Value <= 0L) continue;
                bool duplicate = false;
                for (int k = 0; k < ordered.Count; k++)
                {
                    if (ordered[k] == planId) { duplicate = true; break; }
                }
                if (!duplicate) ordered.Add(planId);
            }
            ordered.Sort((a, b) => a.Value.CompareTo(b.Value));
            return ordered.AsReadOnly();
        }

        /// <summary>
        /// 计划 → 动作定义 ID 的只读投影（Block/Guard 抵抗配置的解析入口）。
        /// 只含活动计划：求解器只会向它索取"图里出现过的计划"，而图里的计划必然非终态。
        /// </summary>
        private IReadOnlyList<PlanSpecEntry> BuildPlanSpecEntries()        {
            IReadOnlyList<ActionPlan> active = _scheduleAuthority.Registry.ActivePlans;
            var entries = new List<PlanSpecEntry>(active.Count);
            for (int i = 0; i < active.Count; i++)
            {
                ActionPlan plan = active[i];
                if (plan == null) continue;
                entries.Add(new PlanSpecEntry(plan.ActionPlanId, plan.ActionSpecId.Value ?? string.Empty));
            }
            return entries;
        }

        /// <summary>Block 载荷的只读投影（只含 <see cref="ActionType.Block"/> 的已验证动作定义）。</summary>
        private IReadOnlyList<BlockPayloadEntry> BuildBlockPayloadEntries()
        {
            var entries = new List<BlockPayloadEntry>();
            IReadOnlyList<ActionSpec> specs = _definition.Actions;
            for (int i = 0; i < specs.Count; i++)
            {
                ActionSpec spec = specs[i];
                if (spec == null || spec.Type != ActionType.Block) continue;
                var payload = spec.Payload as BlockPayloadSpec;
                if (payload == null) continue;
                entries.Add(new BlockPayloadEntry(spec.ActionSpecId.Value ?? string.Empty, payload));
            }
            return entries;
        }

        /// <summary>Guard 载荷的只读投影（只含 <see cref="ActionType.Guard"/> 的已验证动作定义）。</summary>
        private IReadOnlyList<GuardPayloadEntry> BuildGuardPayloadEntries()
        {
            var entries = new List<GuardPayloadEntry>();
            IReadOnlyList<ActionSpec> specs = _definition.Actions;
            for (int i = 0; i < specs.Count; i++)
            {
                ActionSpec spec = specs[i];
                if (spec == null || spec.Type != ActionType.Guard) continue;
                var payload = spec.Payload as GuardPayloadSpec;
                if (payload == null) continue;
                entries.Add(new GuardPayloadEntry(spec.ActionSpecId.Value ?? string.Empty, payload));
            }
            return entries;
        }

        /// <summary>
        /// Clash 配额：数值与错误码都由<strong>构图侧</strong>持有（冲突组是它的概念），
        /// 动量求解只接收（照 <see cref="MomentumClashQuota"/> 的归属裁定）。
        /// </summary>
        private static MomentumClashQuota BuildClashQuota()
            => new MomentumClashQuota(
                InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED,
                ConflictGraphLimits.MaxConflictGroupNodes,
                ConflictGraphLimits.MaxConflictGroupEdges);

        /// <summary>
        /// 阶段 11 的扣血端口（<see cref="IResolutionDamageApplier"/>）：唯一写入点是权威生命字段。
        ///
        /// 它<strong>不</strong>碰状态机、不碰网格、不碰计划：负伤害是不可能的（聚合器只产生非负值），
        /// 越界（超过 <c>int</c> 域）以稳定码拒绝而不是钳制/饱和/回绕（任务包 08:105）。
        /// </summary>
        void IResolutionDamageApplier.ApplyDamageQ10(UnitId unitId, long damageQ10)
        {
            if (damageQ10 < 0L || damageQ10 > int.MaxValue)
            {
                throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_COMMIT_RANGE,
                    "unit=" + unitId.Value.ToString(CultureInfo.InvariantCulture)
                    + " damageQ10=" + damageQ10.ToString(CultureInfo.InvariantCulture));
            }
            if (!_unitsById.TryGetValue(unitId.Value, out UnitRuntimeState unit)) return;

            unit.HealthQ10 -= (int)damageQ10;
            unit.HealthDirty = true;
        }

        /// <summary>
        /// 活动计划的只读事实表（按 <c>ActionPlanId</c> 升序，与
        /// <see cref="ActionPlanRegistry.ActivePlans"/> 的规范视图同源）。
        /// </summary>
        private IReadOnlyList<InteractionPlanFacts> BuildInteractionPlanFacts()
        {
            IReadOnlyList<ActionPlan> active = _scheduleAuthority.Registry.ActivePlans;
            var facts = new List<InteractionPlanFacts>(active.Count);
            for (int i = 0; i < active.Count; i++)
            {
                if (active[i] == null) continue;
                facts.Add(InteractionPlanFacts.From(active[i]));
            }
            return facts;
        }

        /// <summary>
        /// 把一个<strong>统一只读空间快照</strong>投影成 <see cref="ConflictGraphInput"/> 需要的
        /// "每单位已占用 <see cref="TrianglePoint"/>"（任务 08 占用投影）。
        ///
        /// <strong>为什么用快照里的点集而不是重算 footprint</strong>：快照条目
        /// （<see cref="DodgeSpaceUnitEntry.Triangles"/>）就是空间权威在那一刻的只读投影，
        /// 直接取用既不会引入第二份空间权威，也不可能与 <see cref="DodgeCommitReport.Before"/> 的
        /// 冻结语义漂移（提交后的旧快照仍然只反映提交前的位置）。
        ///
        /// <strong>为什么还要取控制者</strong>：
        /// <see cref="UnitOccupancy"/> 必须携带审计用的 <see cref="ControllerId"/>
        /// （它只标识"谁下命令"，与阵营正交，任何过滤、连边、共享目标判定与稳定键都<strong>不得</strong>读取它，
        /// 08-多方仲裁与伤害.md:62/:409；不变量 30）。控制者映射的唯一来源是已验证定义的
        /// <c>ControllerBinding</c>：定义<b>声明</b>了控制者却查不到映射 ⇒ 稳定码显式拒绝；
        /// 定义<b>没有</b>为该单位声明控制者 ⇒ 合法的"无控制者"，审计字段取 <c>default</c>
        /// （判别依据见 <see cref="ControllerOf"/>，两种形态都不静默"猜一个控制者"）。
        ///
        /// <strong>输入来自空间权威的注册集</strong>：<c>snapshot.Units</c> 由
        /// <c>LogicGrid.RegisteredUnitsOrdered()</c> 产出 ⇒ 已被注销的死者在结构上不可能出现在这里
        /// （裁定 6.2 的 footprint 移除与"死者不占格"因此与本投影自动一致，不需要第二套存活过滤）。
        ///
        /// 输出按 <c>UnitId</c> 升序（<see cref="InteractionCandidateBuilder"/> 会再按同一键规范化，
        /// 因此这里的顺序不是判定依据，只是让投影结果本身可比较）。
        /// </summary>
        private IReadOnlyList<UnitOccupancy> BuildOccupancy(DodgeSpaceSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Units == null || snapshot.Units.Count == 0)
            {
                return Array.Empty<UnitOccupancy>();
            }

            var entries = new List<DodgeSpaceUnitEntry>(snapshot.Units.Count);
            for (int i = 0; i < snapshot.Units.Count; i++)
            {
                DodgeSpaceUnitEntry entry = snapshot.Units[i];
                if (entry == null || !entry.UnitId.IsValid) continue;
                entries.Add(entry);
            }
            entries.Sort((a, b) => a.UnitId.Value.CompareTo(b.UnitId.Value));

            var occupancy = new List<UnitOccupancy>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                DodgeSpaceUnitEntry entry = entries[i];
                occupancy.Add(new UnitOccupancy(
                    entry.UnitId, entry.Triangles, ControllerOf(entry.UnitId)));
            }
            return occupancy;
        }

        /// <summary>
        /// 单位的控制者（审计字段）：唯一来源是已验证定义的 <c>ControllerBinding</c>。
        ///
        /// 三条判定（都不静默回退到"猜一个控制者"）：
        /// <list type="bullet">
        /// <item>一个单位被两个不同控制者绑定 ⇒
        /// <see cref="SimulationCodes.STEP_OCCUPANCY_CONTROLLER_CONTRADICTION"/>
        /// （"第二份控制权真值"会让审计字段静默取决于扫描顺序）；</item>
        /// <item><strong>定义声明了该单位、映射却没给出绑定</strong> ⇒
        /// <see cref="SimulationCodes.STEP_OCCUPANCY_CONTROLLER_MISSING"/>
        /// （这是真实的装配缺失：<c>BattleInitializer</c> 会把定义声明过的槽位全部映进
        /// <c>ControllerToUnitIds</c>，所以"声明了却查不到"只可能是装配链被截断）；</item>
        /// <item><strong>定义根本没为该单位声明控制者</strong> ⇒ 返回 <c>default</c>。
        /// 这是<b>合法</b>形态而不是缺陷：<c>EncounterDefinition</c> 的校验只拒绝悬空 / 歧义 /
        /// 重复绑定，<strong>不</strong>要求每个槽位都被绑定（例如任务 04 的
        /// <c>Task04OutsiderVariant</c>：目标外阵营单位"不被任何外部入口控制"是它的<b>设计事实</b>）。
        /// 空字符串是合法 <c>ControllerId</c>（裁定 6.4），而 <see cref="UnitOccupancy.ControllerId"/>
        /// 只是审计字段——任何过滤、连边、共享目标判定与稳定键都<b>不得</b>读取它
        /// （08-多方仲裁与伤害.md:62/:409；不变量 30）⇒ 取 <c>default</c> 不影响任何判定结果，
        /// 把它当异常抛出反而会让合法定义无法推进一步。</item>
        /// </list>
        /// </summary>
        private ControllerId ControllerOf(UnitId unitId)
        {
            ControllerId found = default;
            bool any = false;
            foreach (KeyValuePair<ControllerId, IReadOnlyList<UnitId>> binding in _controllerToUnitIds)
            {
                IReadOnlyList<UnitId> controlled = binding.Value;
                if (controlled == null) continue;
                for (int i = 0; i < controlled.Count; i++)
                {
                    if (controlled[i] != unitId) continue;
                    if (any && found != binding.Key)
                    {
                        throw new LogicDefinitionException(
                            SimulationCodes.STEP_OCCUPANCY_CONTROLLER_CONTRADICTION,
                            "unit=" + unitId.Value.ToString(CultureInfo.InvariantCulture)
                            + " controllers=" + (found.Value ?? string.Empty) + "," + (binding.Key.Value ?? string.Empty));
                    }
                    found = binding.Key;
                    any = true;
                }
            }

            if (any) return found;

            // 定义没有为该单位声明控制者 ⇒ 合法的"无控制者"审计值（见方法注释第 3 条）。
            if (!_controllerDeclaredUnitIds.Contains(unitId.Value)) return default;

            throw new LogicDefinitionException(
                SimulationCodes.STEP_OCCUPANCY_CONTROLLER_MISSING,
                "unit=" + unitId.Value.ToString(CultureInfo.InvariantCulture));
        }

        private void CommitDamageAndBuildForcedDisplacementRequests(long tick)
        {
            _resolutionCommit.CommitDamageAndAggregationOrdered(tick, BuildUnitSnapshots());

            IReadOnlyList<ForcedDisplacementRequest> requests =
                _displacementRequestBuilder.BuildOrdered(tick, BuildUnitSnapshots());
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
            ordered.Sort((a, b) => a.TargetUnitId.Value.CompareTo(b.TargetUnitId.Value));

            // 每 Tick 每 UnitId 至多一个请求：重复键是 InvariantViolation，
            // 绝不按 ConflictGroupKey / MomentumUnits / UnitId / 枚举顺序挑一个。
            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i - 1].TargetUnitId == ordered[i].TargetUnitId)
                    throw new LogicDefinitionException(SimulationCodes.STEP_DISPLACEMENT_REQUEST_DUPLICATE,
                        ordered[i].TargetUnitId.Value.ToString(CultureInfo.InvariantCulture));
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
            _displacementBatch = _displacementSolver.ResolveAll(
                _displacementRequests, BuildUnitSnapshots(), _encounter.GridBoundary, tick)
                ?? ForcedDisplacementBatch.Empty;
        }

        private void CleanupAndCommitRelocationBatch(long tick)
        {
            ForcedDisplacementBatch batch = _displacementBatch;
            if (batch == null || batch.IsEmpty) return;

            // 先经统一终态协调器终止被破坏的移动计划与 Reservation。
            _invalidatedPlanIds = _displacementCommitter
                .TerminateInvalidatedMovementPlansOrdered(batch, tick) ?? Array.Empty<ActionPlanId>();

            // 再一次批量验证 + 一次批量换位（只收 From != To 的条目）：任一批量验证失败都是
            // InvariantViolation，禁止退化为逐单位提交。
            ApplyBatchRelocation(batch);

            // 批量位置成功提交之后才发射规范位移事件（阶段 13），按 TargetUnitId 升序；
            // 即使 AppliedSteps == 0 也要发射以说明停止原因（任务包 :234）。
            // 这就是任务包「必须产出」15 的「强制位移最终结果」事件：From/To、请求/实际步数、
            // 方向、动量、冲突组键、停止原因与稳定排序的失效计划 ID 都在载荷里（不变量 10：无表现参数）。
            for (int i = 0; i < batch.Resolutions.Count; i++)
            {
                ForcedDisplacementResolution resolution = batch.Resolutions[i];
                ForcedDisplacementRequest request = FindDisplacementRequest(resolution.TargetUnitId);
                _outbox.Emit(sequence => new ForcedDisplacementResolvedEvent(
                    tick, sequence, resolution.TargetUnitId,
                    resolution.From, resolution.To,
                    request.Direction, resolution.RequestedSteps, resolution.AppliedSteps,
                    request.MomentumUnits, request.ConflictGroupKey, resolution.StopReason,
                    resolution.InvalidatedPlanIds ?? Array.Empty<ActionPlanId>()));
            }
        }

        /// <summary>
        /// 把结果关联回本 Tick 的请求：<c>Direction</c> / <c>MomentumUnits</c> / <c>ConflictGroupKey</c>
        /// 只存在于请求侧（任务包冻结的 Resolution 形状不含这三项），因此这里是<strong>唯一</strong>的
        /// 关联点。找不到请求说明求解器违反了"结果必须来自本 Tick 请求"的契约 ⇒ InvariantViolation。
        /// 关联按 <see cref="UnitId"/> 精确匹配，与容器枚举顺序无关。
        /// </summary>
        private ForcedDisplacementRequest FindDisplacementRequest(UnitId unitId)
        {
            for (int i = 0; i < _displacementRequests.Count; i++)
            {
                if (_displacementRequests[i].TargetUnitId == unitId) return _displacementRequests[i];
            }
            throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                "resolution without request unit=" + unitId.Value.ToString(CultureInfo.InvariantCulture));
        }

        private void ApplyBatchRelocation(ForcedDisplacementBatch batch)
        {
            // —— 全批预检（覆盖含零步在内的全部结果）——
            var destinations = new HashSet<long>();
            var moving = new HashSet<long>();
            for (int i = 0; i < batch.Resolutions.Count; i++)
            {
                ForcedDisplacementResolution resolution = batch.Resolutions[i];
                if (!_unitsById.TryGetValue(resolution.TargetUnitId.Value, out UnitRuntimeState unit))
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "unknown unit " + resolution.TargetUnitId.Value.ToString(CultureInfo.InvariantCulture));

                // 唯一空间权威是 LogicGrid：From 必须等于网格锚点（镜像陈旧不得被静默接受）。
                if (!_logicGrid.TryGetAnchor(resolution.TargetUnitId, out GridPoint anchor) ||
                    anchor != resolution.From)
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "from mismatch (grid anchor) unit=" +
                        resolution.TargetUnitId.Value.ToString(CultureInfo.InvariantCulture));

                if (unit.Position != resolution.From)
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "from mismatch (runtime mirror) unit=" +
                        resolution.TargetUnitId.Value.ToString(CultureInfo.InvariantCulture));

                if (resolution.AppliedSteps < 0 || resolution.AppliedSteps > resolution.RequestedSteps)
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "applied steps unit=" +
                        resolution.TargetUnitId.Value.ToString(CultureInfo.InvariantCulture));

                if (!resolution.IsRelocating) continue;

                if (!_encounter.GridBoundary.Contains(resolution.To))
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "out of boundary unit=" +
                        resolution.TargetUnitId.Value.ToString(CultureInfo.InvariantCulture));

                long destinationKey = ((long)resolution.To.X << 32) ^ (uint)resolution.To.Y;
                if (!destinations.Add(destinationKey))
                    throw new LogicDefinitionException(SimulationCodes.STEP_RELOCATION_BATCH_INVALID,
                        "duplicate destination " + resolution.To.X.ToString(CultureInfo.InvariantCulture) + "," +
                        resolution.To.Y.ToString(CultureInfo.InvariantCulture));

                moving.Add(resolution.TargetUnitId.Value);
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

            // 全零步批次不是"空提交"：它连网格入口都不该调用（网格对空输入返回 BATCH_INVALID）。
            if (batch.Relocations.Count == 0) return;

            // 任务 06：同一次批量换位必须**也**提交到唯一空间权威（LogicGrid）。
            // 在此之前这里只写 unit.Position，会让网格锚点与运行时位置分叉，
            // 而阶段 0 的"网格 → 单位"镜像就会在下一 Tick 把换位回退掉。
            // 网格侧做的是与上面同一套全批预检（ExpectedFrom / 目标 footprint / 静止单位 / 待抢占预留），
            // 因此这里不是"第二条验证路径"，而是把同一次提交写到唯一权威上。
            // 入参只来自 batch.Relocations（仅 From != To），符合任务包 :234。
            string gridError = _logicGrid.ApplyBatchRelocation(batch.Relocations);
            if (gridError != null)
                throw new LogicDefinitionException(gridError, "forced-displacement batch");

            // 全部验证通过后一次性写入新位置（网格已统一写入新 footprint）。
            for (int i = 0; i < batch.Resolutions.Count; i++)
            {
                ForcedDisplacementResolution resolution = batch.Resolutions[i];
                if (!resolution.IsRelocating) continue;
                _unitsById[resolution.TargetUnitId.Value].Position = resolution.To;
            }
        }

        private void CommitStateControlAndAdrenalineAccrual(long tick)
        {
            // 阶段 14 只能读取批量换位<em>之后</em>的最终位置。
            _resolutionCommit.CommitStateControlAndRemainingTerminalsOrdered(tick, BuildUnitSnapshots());

            // —— 参与 Clash 的攻击必须在**本阶段**（状态/控制与其后终态）终止 ——
            //
            // 提交顺序（任务包 08「必须产出」13）：先伤害/合力并完成强制位移，再提交状态/控制及其余终态，
            // 最后处理死亡。因此 Clash 终态在这里才提交，而**不是**在阶段 11。
            //
            // 唯一通道是**统一终态协调器**：本方法不碰 ActionPlan 对象、不删未来 Intent/Segment/Reservation
            // （那些由协调器的清理参与者完成）；违反的可观察后果是 Step 末
            // STEP_INVARIANT_VIOLATION: active-plan-missing-from-lane。
            // 聚合结果里的 TotalImpactUnits / KnockbackSteps 是"控制结果"的判定面
            // （硬直/击倒阈值的比较已在聚合器内完成）。
            IReadOnlyList<ClashTerminalRequest> clashTerminals = _resolutionCommitSystem == null
                ? Array.Empty<ClashTerminalRequest>()
                : _resolutionCommitSystem.PendingClashTerminals;
            for (int i = 0; i < clashTerminals.Count; i++)
            {
                ClashTerminalRequest request = clashTerminals[i];
                ActionPlan plan = _scheduleAuthority.Registry.Find(request.ActionPlanId);
                if (plan == null || plan.IsTerminal) continue;   // 幂等：第一次成功请求胜出
                _terminalCoordinator.EnterTerminal(plan, request.Reason, tick);
            }

            // 任务包 08「必须产出」15：动作终止（拼刀）的语义事件。
            // 载荷里只有计划/单位 ID 与稳定原因——**没有**任何计划对象、Lane、Intent、
            // MovementSegment 或 Reservation，因为真正的清理是协调器参与者的职责。
            for (int i = 0; i < clashTerminals.Count; i++)
            {
                ClashTerminalRequest request = clashTerminals[i];
                ActionPlan plan = _scheduleAuthority.Registry.Find(request.ActionPlanId);
                if (plan == null) continue;
                long groupKey = ConflictGroupKeyOf(request.ActionPlanId);
                UnitId ownerUnitId = plan.OwnerUnitId;
                ActionPlanId planId = request.ActionPlanId;
                ActionTerminationReason reason = request.Reason;
                _outbox.Emit(sequence => new ActionPlanTerminationRequestedEvent(
                    tick, sequence, groupKey, planId, ownerUnitId, reason));
            }

            CommitResolvedControlsAndIntercepts(tick);

            // 任务 07：肾上腺素 Available 的唯一入账入口。它只接受任务 08 在全部 Resolution
            // 提交后提供的规范聚合事实（每单位每 Tick 至多一条、按 UnitId 严格升序）；
            // 其他系统一律不得逐接触直接加 Available（不变量 27）。
            // 默认装配即**本场真实现**（AdrenalineAccrualFactSource，只读 _stagedResolution 与
            // LastDamageCommitReport）；显式注入优先（负控制 NoAdrenalineAccrualFacts）。
            _adrenaline.ApplyTickEndAccrual(_adrenalineAccrualSource.BuildAccrualFactsOrdered(tick));
            CompleteDuePlans(tick, beforeResolution: false);
        }

        private void CommitResolvedControlsAndIntercepts(long tick)
        {
            if (_stagedResolution == null || _stagedResolution.Failed || _stagedResolution.Tick != tick) return;
            foreach (MoveContactResolution move in _stagedResolution.Moves)
            {
                if (move.Outcome != MoveContactOutcome.Intercepted) continue;
                bool hit = false;
                foreach (RemainingHitResolution remaining in _stagedResolution.RemainingHits)
                    if (remaining.AttackPlanId == move.AttackPlanId && remaining.Key.TargetUnitId == move.MovingUnitId
                        && remaining.ContactKind == TargetContactType.DirectHit
                        && !remaining.IsDirectHitSuppressed) { hit = true; break; }
                if (!hit) continue;
                ActionPlan plan = _scheduleAuthority.Registry.Find(move.MovePlanId);
                if (plan != null && !plan.IsTerminal)
                    _terminalCoordinator.EnterTerminal(plan, ActionTerminationReason.InterruptedByIntercept, tick);
            }
            foreach (TargetAggregateResolution aggregate in _stagedResolution.Aggregations)
            {
                if (!aggregate.IsStaggered && !aggregate.IsKnockedDown) continue;
                UnitStateMachine machine = FindUnitStateMachine(aggregate.TargetUnitId);
                if (machine == null || machine.IsTerminal) continue;
                UnitState control = aggregate.IsKnockedDown ? UnitState.KnockedDown : UnitState.Staggered;
                int duration = aggregate.IsKnockedDown ? _definition.Rules.KnockdownAutoRecoveryTicks
                    : _definition.Rules.StaggerAutoRecoveryTicks;
                if (duration > 0 && machine.CurrentState != control
                    && !(machine.CurrentState == UnitState.KnockedDown && control == UnitState.Staggered))
                {
                    var outcome = machine.TryTransition(StateTransitionSpec.Timed(control, duration, UnitState.Idle),
                        tick, UnitStateTransitionReasons.Explicit);
                    if (!outcome.Applied) throw new LogicDefinitionException(SimulationCodes.STEP_INVARIANT_VIOLATION,
                        "resolved-control|" + aggregate.TargetUnitId + "|" + outcome.RejectionCode);
                }
                foreach (ActionPlan plan in _scheduleAuthority.Registry.ActivePlans)
                    if (plan.OwnerUnitId == aggregate.TargetUnitId && plan.IsRunning)
                        _terminalCoordinator.EnterTerminal(plan, ActionTerminationReason.InterruptedByControl, tick);
            }
        }

        /// <summary>
        /// 把账本事实同步到单位的只读镜像字段（<c>AvailableAdrenaline</c> / <c>AdrenalineCycleId</c>）。
        ///
        /// 唯一权威是 <see cref="AdrenalineLedgerRegistry"/>；镜像只服务单位快照与既有只读消费者。
        /// 任何系统都<strong>不得</strong>反过来通过镜像字段修改账本。
        /// </summary>
        private void SyncUnitAdrenalineMirror(UnitId unitId)
        {
            if (!_unitsById.TryGetValue(unitId.Value, out UnitRuntimeState unit)) return;
            AdrenalineLedger ledger = _adrenaline.Find(unitId);
            if (ledger == null) return;
            unit.AvailableAdrenaline = ledger.AvailableAdrenaline;
            unit.AdrenalineCycleId = ledger.CycleId;
        }

        private void CloseRequestedWindowAndScheduleNext(long tick)
        {
            // 脚本窗口可以在任意 Tick 请求关闭当前窗口；请求与正式关闭分离：
            // 请求立即停止接受提交（本 Tick 后续命令稳定拒绝），正式关闭在本阶段完成。
            if (_assembly.TurnWindowSchedule.ShouldCloseCurrentWindow(tick))
                _windowManager.CurrentWindow?.RequestClose(TurnWindowCloseReason.OwnerRequested);

            _windowManager.FinalizeRequestedClose(tick);
        }

        /// <summary>
        /// 关闭当前窗口（唯一实现）。它只撤销属于该窗口的并发授权、翻转窗口位并发射关闭事件；
        /// <strong>不</strong>查询、取消、移动或结算任何 <c>ActionPlan</c>，也不清零肾上腺素。
        /// </summary>
        private void CloseWindow(long tick, TurnWindowCloseReason reason)
        {
            TurnWindow window = _windowManager.CurrentWindow;
            if (window == null) return;
            window.RequestClose(reason);
            _windowManager.FinalizeRequestedClose(tick);
        }

        /// <summary>
        /// 阶段 18：把<strong>按 Controller 过滤</strong>的只读决策快照投递给每个观察者。
        ///
        /// 任务 09（产出 17）冻结的投递口径：
        /// <list type="number">
        /// <item>Canonical 快照只构建一次（它<strong>不</strong>是给某个 Controller 看的视图，
        /// 而是全部权威状态的投影；开发者哈希面仍由 <see cref="LogicSnapshot"/> 承担）；</item>
        /// <item><see cref="IDecisionObserver.ObserverControllerId"/> 声明本观察者代表的受信
        /// Controller；为 <c>null</c> 的诊断观察者收到 Canonical 实例（<strong>同一实例</strong>，
        /// 因此既有"观察者与模拟读数是同一个快照"的断言不变）；</item>
        /// <item>为具体 Controller 的观察者（AI 与任务 10 的玩家 UI）收到
        /// <see cref="DecisionSnapshot.ForController"/> 的过滤结果——
        /// 其他 Controller 尚未按玩法公开的 <c>Editable</c> 普通计划被整条剔除。</item>
        /// </list>
        /// </summary>
        private void DeliverDecisionSnapshot(long tick)
        {
            IReadOnlyList<IDecisionObserver> observers = _assembly.DecisionObservers;
            if (observers.Count == 0) return;

            DecisionSnapshot canonical = BuildDecisionSnapshot(tick).WithDefinition(_definition);
            long nextTick = checked(tick + 1L);
            for (int i = 0; i < observers.Count; i++)
            {
                IDecisionObserver observer = observers[i];
                if (observer == null) continue;
                DecisionSnapshot delivered = ResolveDecisionSnapshotFor(observer, canonical);
                _lastDecisionSnapshot = delivered;
                observer.ObserveOrdered(delivered, _ingress, nextTick);
            }
        }

        /// <summary>
        /// 某个观察者应当收到的决策快照：诊断观察者（未声明 Controller）拿 Canonical 实例，
        /// 受信 Controller 拿它自己的过滤视图。过滤只读，因此这里不可能写出任何权威状态。
        /// </summary>
        private DecisionSnapshot ResolveDecisionSnapshotFor(IDecisionObserver observer, DecisionSnapshot canonical)
        {
            Ids.ControllerId controllerId = observer.ObserverControllerId(canonical);
            if (controllerId.Value == null) return canonical;

            return canonical.ForController(
                controllerId,
                _controllerUnitAuthority.ControlledUnitsOf(controllerId),
                OwnWindowSnapshotOf(controllerId));
        }

        /// <summary>
        /// 该控制者当前窗口的<strong>只读快照</strong>（<c>null</c> = 它现在没有窗口）。
        ///
        /// 判据只有一条：当前窗口的拥有者单位<strong>属于该控制者</strong>。窗口何时打开、何时停止
        /// 接收提交都是任务 07 的权威事实，这里只做投影，<strong>不</strong>据此放行任何命令。
        /// </summary>
        private TurnWindowSnapshot OwnWindowSnapshotOf(Ids.ControllerId controllerId)
        {
            TurnWindow window = _windowManager.CurrentWindow;
            if (window == null) return null;
            return _controllerUnitAuthority.CanControl(controllerId, window.OwnerUnitId)
                ? ToWindowSnapshot(window)
                : null;
        }

        // —— 唯一终态清理（幂等、固定顺序，不建第二套计划清理路径）——

        private StepResult FinalizeBattleEnd(long tick, StepStatus status)
        {
            string resultCode = _resolvedResultCode ?? BattleResultCodes.Stopped;
            _resolvedResultCode = null;

            int clearedFacts = CountFutureTickBucketFacts();
            _ingress.MarkBattleEnded();

            bool closedWindow = _windowManager.CurrentWindow != null;
            _windowManager.CloseAllForBattleEnd(tick);

            // 未消费的窗口预算预留随战斗结束清空：保留已消费的 Spent（不退款），
            // 逐窗口发射 BattleEndCleared（含 WindowId 与 Available/Reserved/Spent 前后值）。
            _windowBudgetClearedAtBattleEnd = _budgetAuthority.ClearReservationsForBattleEnd(tick);

            // 并发提交授权随窗口关闭一并撤销；它不取消任何计划。
            _concurrentAction.RevokeAll();

            // 肾上腺素：清空 Available 与全部预留，不产生可继续使用的退款额度，
            // 也不排定下一窗口（最后一条 AdrenalineLedgerChangedEvent 之后才发 BattleEndedEvent）。
            _adrenaline.ClearForBattleEnd();
            for (int i = 0; i < _units.Count; i++) SyncUnitAdrenalineMirror(_units[i].UnitId);

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
            // 任务 09 A 流（产出 12）：战斗结束也是"本 Tick 已完成"，
            // 入口的当前 Tick 必须同步前进，此后新命令一律在入口以 BATTLE_ALREADY_ENDED 稳定拒绝。
            _ingress.AdvanceCurrentTick(tick);
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
        /// 任务 09（产出 15）：<strong>AI 未来决策状态</strong>的规范化投影。
        ///
        /// 它是装配端口 <see cref="BattleSimulationAssembly.AiRuntimeStates"/> 的<strong>只读</strong>转发：
        /// 未装配（本场没有 AI 控制者）时返回空集合——那是合法状态，不是"AI 没接线"。
        /// 全部字段进哈希见 <c>LogicSnapshot</c> 的 <c>AiControllers</c> 段。
        /// </summary>
        private IReadOnlyList<AiControllerSnapshot> BuildAiControllerSnapshots()
        {
            IAiRuntimeStateSource source = _assembly.AiRuntimeStates;
            if (source == null) return Array.Empty<AiControllerSnapshot>();

            IReadOnlyList<AiControllerRuntimeState> states = source.CaptureRuntimeStatesOrdered();
            if (states == null || states.Count == 0) return Array.Empty<AiControllerSnapshot>();

            var projected = new List<AiControllerSnapshot>(states.Count);
            for (int i = 0; i < states.Count; i++)
            {
                AiControllerRuntimeState state = states[i];
                if (state == null) continue;
                projected.Add(new AiControllerSnapshot(
                    state.ControllerId ?? string.Empty,
                    state.NextThinkTick,
                    state.DecisionCount,
                    state.LastDecisionTick,
                    state.Rng));
            }
            projected.Sort((a, b) => string.CompareOrdinal(a.ControllerId, b.ControllerId));
            return projected;
        }

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

        private static TurnWindowSnapshot ToWindowSnapshot(TurnWindow window)
        {
            IReadOnlyList<TurnWindowReservation> reservations = window.Reservations;
            var projected = new List<TurnWindowReservationSnapshot>(reservations.Count);
            for (int i = 0; i < reservations.Count; i++)
            {
                projected.Add(new TurnWindowReservationSnapshot(
                    reservations[i].ActionPlanId.Value, reservations[i].ReservedTicks));
            }
            return new TurnWindowSnapshot(
                window.WindowId.Value,
                window.OwnerUnitId.Value,
                window.OpenedAtTick,
                window.TotalBudgetTicks,
                window.ReservedBudgetTicks,
                window.SpentBudgetTicks,
                window.AvailableBudgetTicks,
                window.IsOpen,
                window.IsAcceptingSubmissions,
                (int)(window.CloseReason ?? TurnWindowCloseReason.OwnerRequested),
                projected);
        }

        private LogicSnapshot BuildSnapshot(long tick)
        {
            var windows = new List<TurnWindowSnapshot>();
            if (_windowManager.CurrentWindow != null)
                windows.Add(ToWindowSnapshot(_windowManager.CurrentWindow));
            for (int i = 0; i < _windowManager.ClosedWindows.Count; i++)
                windows.Add(ToWindowSnapshot(_windowManager.ClosedWindows[i]));
            windows.Sort((a, b) => a.WindowId.CompareTo(b.WindowId));

            long total = 0L;
            long reserved = 0L;
            long spent = 0L;
            for (int i = 0; i < windows.Count; i++)
            {
                total += windows[i].TotalBudgetTicks;
                reserved += windows[i].ReservedBudgetTicks;
                spent += windows[i].SpentBudgetTicks;
            }

            var windowManager = new TurnWindowManagerSnapshot(
                _windowManager.CurrentWindow != null ? _windowManager.CurrentWindow.WindowId.Value : 0L,
                _windowManager.NextWindowTick,
                _windowManager.NextWindowOrdinal,
                _windowManager.LastClosedWindowId,
                windows);

            // 任务 08 快照契约：快照取"本 Tick 冻结后的队列"（阶段 8 的 _intentQueue）与
            // "本 Tick 的构图产物"（阶段 10 的 _conflictGraph），二者都是**只读入口**的投影，
            // 不重新读取计划/单位/网格，也不重新推导任何东西。
            // 构造期（Tick 0）没有队列与图、以及"本 Tick 无到期 Intent / 构图失败"，
            // 都是**合法**的空集合状态（投影对 null 输入返回空集合，绝不抛）。
            IReadOnlyList<IntentSnapshot> intentSnapshots = InteractionSnapshotProjection.Intents(_intentQueue);
            IReadOnlyList<ConflictGroupSnapshot> conflictGroups =
                InteractionSnapshotProjection.ConflictGroups(_conflictGraph);
            IReadOnlyList<ContactSnapshot> contacts = InteractionSnapshotProjection.Contacts(_conflictGraph);

            return new LogicSnapshot(
                tick,
                _definition.RulesVersion,
                _definition.BattleDefinitionHashValue,
                _encounterId.Value,
                _battleEnd,
                BuildUnitSnapshots(),
                BuildEffectSnapshots(),
                windowManager,
                _concurrentAction.BuildSnapshot(),
                new BattleResourceSnapshot(
                    _metaResource, total - reserved - spent, reserved, spent,
                    _adrenaline.BuildSnapshots()),
                _scheduleRevision,
                BuildActionPlanSnapshots(),
                _reactionSystem.BuildSnapshots(),
                _scheduleAuthority.BuildLaneSnapshots(),
                intentSnapshots,
                BuildMovementSegmentSnapshots(),
                BuildReservationSnapshots(),
                BuildAiControllerSnapshots(),
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
                _idGenerator.NextReactionOpportunityIdValue,
                // 任务 08：同一个冻结产物的另外两个面（组划分 / 接触键集合）。
                conflictGroups,
                contacts);
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

        /// <summary>
        /// 任务 09（产出 9/10）：<strong>机会系统 → AI 只读投影</strong>的唯一适配器。
        ///
        /// 它<strong>只</strong>读机会的公开事实（开放/公开/逐选项截止），
        /// 且<strong>不</strong>把机会系统本身交给 AI——因此"AI 直接调 <c>TryAcceptById</c>"
        /// 或"AI 自己创建一个机会"在类型上不可达。
        ///
        /// Dodge 目的格的<strong>公布</strong>口径（首版，必须逐字遵守）：
        /// <list type="number">
        /// <item>候选格 = 权威 <c>LogicGrid.GetNeighborsOrdered(anchor)</c> 的规范序列
        /// （<see cref="GridDirection"/> 0 → 11，已按边界与占位过滤）。</item>
        /// <item>它是<strong>候选面</strong>而不是授权：目的格是否合法（占位、边界、目的格预留、
        /// 位移允许、肾上腺素）的<strong>最终</strong>判定仍由命令处理时任务 06 的目的格预留权威
        /// 与任务 07 的资源权威执行。</item>
        /// <item>它<strong>不</strong>做距离/速度/权重估算——候选只来自规范邻居序列；</item>
        /// <item>锚点不可得或邻居序列为空 ⇒ 公布<strong>空列表</strong>（fail-closed：
        /// AI 于是不会提 Dodge，而不是随便挑一格）。</item>
        /// </list>
        /// </summary>
        private sealed class ReactionOpportunityPort : AI.IAiReactionOpportunitySource
        {
            private readonly ReactionOpportunitySystem _opportunities;
            private readonly LogicGrid _grid;

            public ReactionOpportunityPort(ReactionOpportunitySystem opportunities, LogicGrid grid)
            {
                _opportunities = opportunities;
                _grid = grid;
            }

            public IReadOnlyList<AI.AiOpportunityView> PublishedOpportunitiesFor(UnitId defenderUnitId, long tick)
            {
                var views = new List<AI.AiOpportunityView>();
                if (_opportunities == null || !defenderUnitId.IsValid) return views;

                IReadOnlyList<ReactionOpportunityRuntime> all = _opportunities.ActiveOpportunities;
                for (int i = 0; i < all.Count; i++)
                {
                    ReactionOpportunityRuntime opportunity = all[i];
                    if (opportunity == null) continue;
                    if (opportunity.DefenderUnitId.Value != defenderUnitId.Value) continue;
                    if (!opportunity.IsOpen) continue;

                    AI.AiDodgeDestinationView dodge = null;
                    var options = new List<AI.AiReactionOptionView>(opportunity.Options.Count);
                    for (int o = 0; o < opportunity.Options.Count; o++)
                    {
                        ReactionOptionRuntime option = opportunity.Options[o];
                        if (option == null || !option.IsPublished || !option.IsOpen) continue;
                        bool isDodge = option.ReactionType == ActionType.Dodge;
                        if (isDodge && dodge == null)
                        {
                            dodge = PublishDodgeDestinations(defenderUnitId);
                        }
                        // Block 绝不带目的格；Dodge 的目的格只能来自上面的公布列表。
                        bool hasDestinations = dodge != null && dodge.Candidates.Count > 0;
                        var view = new AI.AiReactionOptionView(
                            opportunity.Id, option.ReactionActionSpecId,
                            isDodge ? ReactionCommandKind.Dodge : ReactionCommandKind.Block,
                            option.ResponseDeadlineTick, option.IsPublished, option.IsOpen,
                            dodge?.Candidates ?? (IReadOnlyList<GridPoint>)Array.Empty<GridPoint>())
                        {
                            DestinationEligibility = isDodge
                                ? (hasDestinations
                                    ? AI.AiReactionDestinationEligibility.Eligible
                                    : AI.AiReactionDestinationEligibility.Contradicted)
                                : AI.AiReactionDestinationEligibility.NotApplicable
                        };
                        options.Add(view);
                    }
                    if (options.Count == 0) continue;
                    options.Sort((a, b) => a.CompareStableKeyTo(b));
                    views.Add(new AI.AiOpportunityView(
                        opportunity.Id, opportunity.DefenderUnitId, opportunity.SourceAttackPlanId,
                        opportunity.TriggerTick, options)
                    {
                        DodgeDestinations = dodge
                    });
                }

                views.Sort((a, b) => a.ReactionOpportunityId.Value.CompareTo(b.ReactionOpportunityId.Value));
                return views;
            }

            /// <summary>
            /// 公布该防御者的 Dodge 目的格候选：权威 <c>LogicGrid.GetNeighborsOrdered(anchor)</c>
            /// 的规范 12 向序列（已按边界与占位过滤）。它只读网格，零写入，
            /// 也<strong>不</strong>做任何距离/速度/权重估算。
            /// </summary>
            private AI.AiDodgeDestinationView PublishDodgeDestinations(UnitId defenderUnitId)
            {
                if (_grid == null) return null;
                if (!_grid.TryGetAnchor(defenderUnitId, out GridPoint anchor)) return null;

                IReadOnlyList<GridPoint> neighbors = _grid.GetNeighborsOrdered(anchor);
                var candidates = new List<GridPoint>(neighbors?.Count ?? 0);
                for (int i = 0; i < (neighbors?.Count ?? 0); i++)
                {
                    GridPoint cell = neighbors[i];
                    if (!_grid.Boundary.Contains(cell)) continue;
                    candidates.Add(cell);
                }

                return new AI.AiDodgeDestinationView(anchor, candidates);
            }
        }

        /// <summary>
        /// 任务 09（产出 9）：<strong>权威计划注册表 → AI 只读检索面</strong>的唯一适配器。
        /// 它只暴露 <c>Find</c> 与 <c>ActivePlans</c> 两个只读查询，因此 AI 无法经它写终态、
        /// 删计划或推进修订号。
        /// </summary>
        private sealed class ActionPlanLookupPort : AI.IAiActionPlanLookup
        {
            private readonly ActionScheduleAuthority _authority;

            public ActionPlanLookupPort(ActionScheduleAuthority authority) => _authority = authority;

            public ActionPlan FindPlan(ActionPlanId actionPlanId)
                => _authority?.Registry.Find(actionPlanId);

            public IReadOnlyList<ActionPlan> ActivePlansOf(UnitId ownerUnitId)
            {
                var owned = new List<ActionPlan>();
                if (_authority == null || !ownerUnitId.IsValid) return owned;

                IReadOnlyList<ActionPlan> active = _authority.Registry.ActivePlans;
                for (int i = 0; i < active.Count; i++)
                {
                    ActionPlan plan = active[i];
                    if (plan == null) continue;
                    if (plan.OwnerUnitId.Value != ownerUnitId.Value) continue;
                    owned.Add(plan);
                }
                return owned;
            }
        }
    }
}
