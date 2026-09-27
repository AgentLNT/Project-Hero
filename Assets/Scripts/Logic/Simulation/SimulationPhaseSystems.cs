using System;
using System.Collections.Generic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Status;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Simulation
{
    /// <summary>
    /// 阶段 1 的单位状态推进请求（纯数据出参；系统<strong>不</strong>直接改逻辑对象）。
    ///
    /// 任务 04 扩展：除"直接设置生命"外，还能声明一次<strong>显式状态转换</strong>与
    /// 一次<strong>效果施加</strong>。三者都经统一入口提交：
    /// <list type="bullet">
    /// <item><see cref="Transition"/> 走 <c>UnitStateMachine.TryTransition</c>，
    /// 因此与自动到期共用合法性判定、区间计算与事件形状；</item>
    /// <item><see cref="ApplyEffect"/> 走 <c>BuffSystem.Apply</c>（延迟队列），
    /// 因此永远不在遍历中改活动集合。</item>
    /// </list>
    /// 任务 05+ 的动作阶段边界（Windup/Recovery/Guard Active/Block/Dodge Trigger）
    /// 通过 <see cref="Transition"/> 声明，而不是各自直接写状态。
    /// </summary>
    public sealed record UnitStateAdvanceRequest(
        long UnitId,
        int? SetHealthQ10,
        StateTransitionSpec Transition = null,
        StatusEffectRuntimeSpec ApplyEffect = null)
    {
        /// <summary>把生命设为给定 Q10 值（任务 03 既有形态，保持兼容）。</summary>
        public UnitStateAdvanceRequest(long unitId, int setHealthQ10)
            : this(unitId, (int?)setHealthQ10, null, null)
        {
        }
    }

    /// <summary>
    /// 阶段 1 扩展点：推进状态到期与持续效果。
    /// 单位顺序固定按 UnitId；返回值按 UnitId 升序原子应用。
    ///
    /// 本扩展点<strong>只产出声明</strong>：状态机的到期推进、持续效果的到期移除与载荷 Tick
    /// 由 <see cref="BattleSimulation"/> 在阶段 1 内按冻结顺序执行，
    /// 系统本身不持有状态机、不写单位事实。
    /// </summary>
    public interface IUnitStateAdvanceSystem
    {
        IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(IReadOnlyList<UnitSnapshot> units, long tick);
    }

    /// <summary>任务 03 默认：没有状态推进内容。</summary>
    public sealed class NoUnitStateAdvanceSystem : IUnitStateAdvanceSystem
    {
        public static readonly NoUnitStateAdvanceSystem Instance = new NoUnitStateAdvanceSystem();

        public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(IReadOnlyList<UnitSnapshot> units, long tick)
            => Array.Empty<UnitStateAdvanceRequest>();
    }

    /// <summary>
    /// 单位生命周期清理通知的稳定失败码。
    /// </summary>
    public static class UnitLifecycleCodes
    {
        public const string LIFECYCLE_NOTICE_SINK_MISSING = "LIFECYCLE_NOTICE_SINK_MISSING";
        public const string LIFECYCLE_NOTICE_UNIT_UNKNOWN = "LIFECYCLE_NOTICE_UNIT_UNKNOWN";
    }

    /// <summary>
    /// 命令阶段之前（阶段 2）的胜负判据扩展点（可选）。
    ///
    /// <strong>默认实现</strong>由 <c>BattleSimulation</c> 提供（读 <c>VictoryDefinition</c> 的
    /// Allied/Hostile 目标组与单位的战斗有效性，返回配置的 Victory/Defeat/Draw 码或 null）。
    /// 本接口只用于<strong>测试与诊断</strong>：让用例能把"命令前判定"整体延后一格，
    /// 从而观察到阶段 11–15（强制位移 → 批量换位 → 位移事件 → 死亡）在同一 Tick 内的完整顺序。
    ///
    /// 约束：实现<strong>不得</strong>写任何逻辑状态、不得发射事件、不得改变死亡时机；
    /// 它只回答"现在是否已经决定、按配置是什么结果码"。
    /// </summary>
    public interface IPreCommandVictoryGate
    {
        /// <summary>返回配置的结果码；返回 null 表示"尚未决定，继续本 Tick 的窗口与命令阶段"。</summary>
        string EvaluatePreCommandResult();
    }

    /// <summary>
    /// 死亡系统产生的<strong>一次性</strong>生命周期清理通知的只读接收点
    /// （任务 04「必须产出」4）。
    ///
    /// 死亡系统只发通知：它<strong>不</strong>直接修改 ActionPlan、Intent、
    /// MovementSegment、Reservation 或 ActorLane。任务 05 实现本接口（或读
    /// <c>BattleSimulation.PendingLifecycleNotices</c>），按 <c>ActionPlanId</c>
    /// 经统一终态协调器终止死者的全部非终态计划。
    ///
    /// 顺序契约：一次死亡处理内的通知按 <c>UnitId</c> 升序；每个单位整场恰好一条。
    /// 实现方<strong>不得</strong>在这里写逻辑状态（它在 Step 的阶段 2/15 内被调用）。
    /// </summary>
    public interface IUnitLifecycleNoticeSink
    {
        void OnUnitLifecycleNoticeOrdered(UnitLifecycleCleanupNotice notice);
    }

    /// <summary>
    /// 阶段 2 / 16 的胜负评估扩展点。返回 null 表示战斗继续；否则返回结果码。
    /// 结果码必须来自权威 <see cref="VictoryDefinition"/>，不得读取控制权、来源或窗口归属。
    /// </summary>
    public interface IVictoryEvaluator
    {
        string Evaluate(IReadOnlyList<UnitSnapshot> units, VictoryDefinition victory, long tick);
    }

    /// <summary>
    /// 消灭型胜负（00 号规则 31）：只读取 <see cref="VictoryDefinition"/> 的
    /// Allied/Hostile 目标阵营集合与单位存活状态。
    /// 目标外阵营不阻止消灭条件；双方同 Tick 全灭使用显式 DrawResultCode。
    /// </summary>
    public sealed class FactionEliminationVictoryEvaluator : IVictoryEvaluator
    {
        public static readonly FactionEliminationVictoryEvaluator Instance = new FactionEliminationVictoryEvaluator();

        public string Evaluate(IReadOnlyList<UnitSnapshot> units, VictoryDefinition victory, long tick)
        {
            if (victory == null || units == null || units.Count == 0) return null;
            if (victory.AlliedFactionIds == null || victory.AlliedFactionIds.Count == 0) return null;
            if (victory.HostileFactionIds == null || victory.HostileFactionIds.Count == 0) return null;

            bool alliedEliminated = true;
            bool hostileEliminated = true;
            bool sawAllied = false;
            bool sawHostile = false;

            for (int i = 0; i < units.Count; i++)
            {
                UnitSnapshot unit = units[i];
                if (Contains(victory.AlliedFactionIds, unit.FactionId))
                {
                    sawAllied = true;
                    if (unit.IsAlive) alliedEliminated = false;
                }
                else if (Contains(victory.HostileFactionIds, unit.FactionId))
                {
                    sawHostile = true;
                    if (unit.IsAlive) hostileEliminated = false;
                }
            }

            if (!sawAllied || !sawHostile) return null;
            if (alliedEliminated && hostileEliminated) return victory.DrawResultCode;
            if (hostileEliminated) return victory.VictoryResultCode;
            if (alliedEliminated) return victory.DefeatResultCode;
            return null;
        }

        private static bool Contains(IReadOnlyList<FactionId> factions, string factionId)
        {
            for (int i = 0; i < factions.Count; i++)
            {
                if (StringComparer.Ordinal.Equals(factions[i].Value, factionId)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 阶段 6 扩展点：按 <c>CommandSequence</c> 处理已获得序号的命令。
    /// 任务 03 的默认实现把每条命令稳定拒绝为
    /// <see cref="CommandCodes.COMMAND_PROCESSOR_NOT_IMPLEMENTED"/>（不修改任何权威状态）。
    /// 任务 05–09 用真实排程/窗口/反应处理器替换它。
    /// </summary>
    public interface IFrozenCommandProcessor
    {
        /// <summary>返回本次处理的处理器级拒绝（已分配 CommandSequence）。</summary>
        IReadOnlyList<CommandRejectionRecord> ProcessOrdered(
            IReadOnlyList<CommandEnvelope> envelopes, long tick, long batchBaseScheduleRevision);
    }

    /// <summary>任务 03 占位处理器：不写状态，只产生稳定拒绝。</summary>
    public sealed class PendingImplementationCommandProcessor : IFrozenCommandProcessor
    {
        public static readonly PendingImplementationCommandProcessor Instance = new PendingImplementationCommandProcessor();

        public IReadOnlyList<CommandRejectionRecord> ProcessOrdered(
            IReadOnlyList<CommandEnvelope> envelopes, long tick, long batchBaseScheduleRevision)
        {
            if (envelopes == null || envelopes.Count == 0) return Array.Empty<CommandRejectionRecord>();
            var rejections = new List<CommandRejectionRecord>(envelopes.Count);
            for (int i = 0; i < envelopes.Count; i++)
            {
                rejections.Add(new CommandRejectionRecord(
                    envelopes[i], CommandCodes.COMMAND_PROCESSOR_NOT_IMPLEMENTED));
            }
            return rejections;
        }
    }

    /// <summary>每 Tick 每单位唯一的强制位移请求（阶段 11 产出）。</summary>
    public sealed record ForcedDisplacementRequest(
        long TargetUnitId,
        int RequestedSteps,
        GridDirection Direction,
        long MomentumUnits,
        long ConflictGroupKey);

    /// <summary>阶段 12 求解出的单个换位（只读值）。</summary>
    public sealed record ForcedDisplacementRelocation(
        long TargetUnitId,
        int FromX,
        int FromY,
        int ToX,
        int ToY,
        GridDirection Direction,
        int RequestedSteps,
        int AppliedSteps,
        long MomentumUnits,
        long ConflictGroupKey,
        ForcedDisplacementStopReason StopReason,
        IReadOnlyList<ActionPlanId> InvalidatedPlanIds);

    /// <summary>一次同时求解得到的完整强制位移批次。</summary>
    public sealed class ForcedDisplacementBatch
    {
        public static readonly ForcedDisplacementBatch Empty = new ForcedDisplacementBatch(
            Array.Empty<ForcedDisplacementRelocation>(), Array.Empty<ForcedDisplacementRequest>());

        public ForcedDisplacementBatch(
            IReadOnlyList<ForcedDisplacementRelocation> relocations,
            IReadOnlyList<ForcedDisplacementRequest> originalRequests)
        {
            Relocations = relocations ?? Array.Empty<ForcedDisplacementRelocation>();
            OriginalRequests = originalRequests ?? Array.Empty<ForcedDisplacementRequest>();
        }

        public IReadOnlyList<ForcedDisplacementRelocation> Relocations { get; }

        /// <summary>本批对应的原始请求（按 TargetUnitId 升序），用于审计。</summary>
        public IReadOnlyList<ForcedDisplacementRequest> OriginalRequests { get; }

        public bool IsEmpty => Relocations.Count == 0;
    }

    /// <summary>
    /// 阶段 11 扩展点：由本 Tick 全部冲突组生成<strong>每单位唯一</strong>的强制位移请求。
    /// 任务 03 默认不产生任何请求；任务 08 接入真实仲裁结果。
    /// </summary>
    public interface IForcedDisplacementRequestBuilder
    {
        IReadOnlyList<ForcedDisplacementRequest> BuildOrdered(long tick, IReadOnlyList<UnitSnapshot> units);
    }

    public sealed class NoForcedDisplacementRequests : IForcedDisplacementRequestBuilder
    {
        public static readonly NoForcedDisplacementRequests Instance = new NoForcedDisplacementRequests();

        public IReadOnlyList<ForcedDisplacementRequest> BuildOrdered(long tick, IReadOnlyList<UnitSnapshot> units)
            => Array.Empty<ForcedDisplacementRequest>();
    }

    /// <summary>
    /// 阶段 12 扩展点：在<strong>只读</strong>网格快照与临时占位空间中同时求解完整位移批次。
    /// 实现不得调用 Pathfinder、普通 Move、MovementSegment 或普通 Reservation 仲裁，
    /// 也不得修改任何逻辑状态。任务 08 接入真实求解器。
    /// </summary>
    public interface IForcedDisplacementSolver
    {
        ForcedDisplacementBatch ResolveAll(
            IReadOnlyList<ForcedDisplacementRequest> requests,
            IReadOnlyList<UnitSnapshot> immutableUnitSnapshot,
            GridBoundaryDefinition boundary,
            long tick);
    }

    public sealed class NoForcedDisplacementSolver : IForcedDisplacementSolver
    {
        public static readonly NoForcedDisplacementSolver Instance = new NoForcedDisplacementSolver();

        public ForcedDisplacementBatch ResolveAll(
            IReadOnlyList<ForcedDisplacementRequest> requests,
            IReadOnlyList<UnitSnapshot> immutableUnitSnapshot,
            GridBoundaryDefinition boundary,
            long tick)
            => ForcedDisplacementBatch.Empty;
    }

    /// <summary>
    /// 阶段 13 前半段扩展点：按 ActionPlanId 终止被位移破坏的 Move/Reservation
    /// （统一终态协调器入口）。任务 05 接入真实清理。
    /// </summary>
    public interface IForcedDisplacementCommitter
    {
        IReadOnlyList<ActionPlanId> TerminateInvalidatedMovementPlansOrdered(ForcedDisplacementBatch batch, long tick);
    }

    public sealed class NoForcedDisplacementCommitter : IForcedDisplacementCommitter
    {
        public static readonly NoForcedDisplacementCommitter Instance = new NoForcedDisplacementCommitter();

        public IReadOnlyList<ActionPlanId> TerminateInvalidatedMovementPlansOrdered(ForcedDisplacementBatch batch, long tick)
            => Array.Empty<ActionPlanId>();
    }

    /// <summary>
    /// 阶段 11（伤害/合力提交）与阶段 14（状态、控制与其余终态提交）扩展点。
    /// 任务 03 默认空实现；任务 08 接入真实求解结果提交。
    ///
    /// 阶段 14 的入参是<strong>批量换位之后</strong>的单位快照，因此实现只能读取最终位置：
    /// 状态、控制与死亡不能提前读取或改写中间位置。
    /// </summary>
    public interface IResolutionCommitSystem
    {
        void CommitDamageAndAggregationOrdered(long tick, IReadOnlyList<UnitSnapshot> units);

        void CommitStateControlAndRemainingTerminalsOrdered(long tick, IReadOnlyList<UnitSnapshot> unitsAfterRelocation);
    }

    public sealed class NoResolutionCommitSystem : IResolutionCommitSystem
    {
        public static readonly NoResolutionCommitSystem Instance = new NoResolutionCommitSystem();

        public void CommitDamageAndAggregationOrdered(long tick, IReadOnlyList<UnitSnapshot> units) { }

        public void CommitStateControlAndRemainingTerminalsOrdered(long tick, IReadOnlyList<UnitSnapshot> unitsAfterRelocation) { }
    }

    /// <summary>
    /// 阶段 19 的只读 Step 末不变量检查参与者。
    /// 参与者<strong>显式装配且顺序固定</strong>，检查期间不得修改逻辑状态、不得发射玩法事件。
    /// 任务 05 起在这里检查活动索引、Lane、Intent、MovementSegment 与 Reservation
    /// 不引用终止 ActionPlan。
    /// </summary>
    public interface IStepInvariantCheck
    {
        /// <summary>返回失败描述（null = 通过）。</summary>
        string CheckOrdered(long tick, IReadOnlyList<UnitSnapshot> units);
    }

    /// <summary>
    /// 阶段 19 的归档候选来源（只增不改；增量候选，不扫描历史注册表）。
    /// 关闭但仍可能退款/消费的账本必须返回"未封条"的候选，由归档器跳过并计数。
    /// </summary>
    public interface IHistoryArchiveCandidateSource
    {
        IReadOnlyList<HistoryArchiveCandidate> CollectOrdered(long tick);
    }

    /// <summary>
    /// 阶段 18 扩展点：AI/系统决策观察者。它读取与玩家<strong>同一份</strong>只读
    /// <see cref="DecisionSnapshot"/>，且只能经已注册入口把请求投递到<strong>下一 Tick</strong>。
    /// 不得在当前 Tick 回写或抢跑。
    /// </summary>
    public interface IDecisionObserver
    {
        void ObserveOrdered(DecisionSnapshot snapshot, CommandIngressRegistry ingress, long nextTick);
    }

    /// <summary>
    /// 装配期的<strong>单位体积规范表来源</strong>（任务 06 的空间权威消费面）。
    ///
    /// 返回任务 02B 预展开的 12 向整数表；返回 <c>null</c> 表示该单位未绑定体积规范表，
    /// 装配方将按显式的<strong>单格占位过渡形态</strong>注册（见
    /// <c>LogicGrid.RegisterUnitWithPointFootprint</c>）——那不是"体积规则的第二套实现"，
    /// 而是一个被登记为未完成项的降级口径。
    /// </summary>
    public interface IUnitVolumeTableSource
    {
        System.Collections.Generic.IReadOnlyList<Grid.DirectionalTriangleSet> VolumeDirectionsOf(
            Ids.UnitDefinitionId unitDefinitionId);
    }

    /// <summary>
    /// <strong>定义级单位体积表来源</strong>（任务 06 的生产默认）：直接把已解析的
    /// <see cref="Definitions.BattleDefinition"/> 上"单位 → <c>VolumeSpec</c>"的绑定投影成
    /// 装配期查询。
    ///
    /// 它<strong>不</strong>读资产、<strong>不</strong>按名字/半径猜测体积、<strong>不</strong>旋转点集：
    /// 绑定本身来自 <c>UnitDefinition.VolumeSpecId</c>（Authoring 边界的迁移清单一次性写入），
    /// 这里只做 ID → 规范 12 向表的只读解析。未绑定或悬空的单位返回 <c>null</c>，
    /// 装配方据此走显式的单格占位降级口径（不是"体积规则的第二套实现"）。
    /// </summary>
    public sealed class DefinitionVolumeTableSource : IUnitVolumeTableSource
    {
        private readonly Definitions.BattleDefinition _definition;

        public DefinitionVolumeTableSource(Definitions.BattleDefinition definition)
        {
            _definition = definition ?? throw new System.ArgumentNullException(nameof(definition));
        }

        public System.Collections.Generic.IReadOnlyList<Grid.DirectionalTriangleSet> VolumeDirectionsOf(
            Ids.UnitDefinitionId unitDefinitionId)
            => _definition.VolumeDirectionsOf(unitDefinitionId);
    }

    /// <summary>
    /// 显式装配的阶段参与者集合（顺序固定、构建后不可变）。
    /// 它<strong>不是</strong>容器扫描或反射注册：装配顺序就是检查/提交顺序。
    /// </summary>
    public sealed class BattleSimulationAssembly
    {
        public BattleSimulationAssembly(
            IUnitStateAdvanceSystem unitStateAdvance = null,
            IVictoryEvaluator victoryEvaluator = null,
            Turns.ITurnWindowSchedule turnWindowSchedule = null,
            IFrozenCommandProcessor commandProcessor = null,
            IForcedDisplacementRequestBuilder displacementRequestBuilder = null,
            IForcedDisplacementSolver displacementSolver = null,
            IForcedDisplacementCommitter displacementCommitter = null,
            IResolutionCommitSystem resolutionCommit = null,
            ICommandPayloadAuthorizer payloadAuthorizer = null,
            IReadOnlyList<IStepInvariantCheck> invariantChecks = null,
            IReadOnlyList<IHistoryArchiveCandidateSource> archiveCandidateSources = null,
            IReadOnlyList<IDecisionObserver> decisionObservers = null,
            StepPhaseTimingRecorder phaseTiming = null,
            IUnitLifecycleNoticeSink lifecycleNoticeSink = null,
            IPreCommandVictoryGate preCommandVictoryGate = null,
            IActionPlanStartCommitPort startCommitPort = null,
            IDodgeRelocationTransaction dodgeRelocationTransaction = null,
            IAreaThreatCandidateSource areaThreatCandidateSource = null,
            IReactionPreparationPort reactionPreparationPort = null,
            IReactionReservationReleaseSink reactionReservationReleaseSink = null,
            IMovementPathCalculator movementPathCalculator = null,
            IUnitVolumeTableSource unitVolumeTables = null,
            Action<IReadOnlyList<ActionPlanId>> budgetReleaseSink = null,
            UnitId? concurrentHeroUnitId = null,
            Resources.IAdrenalineAccrualFactSource adrenalineAccrualFactSource = null)
        {
            UnitStateAdvance = unitStateAdvance ?? NoUnitStateAdvanceSystem.Instance;
            VictoryEvaluator = victoryEvaluator ?? FactionEliminationVictoryEvaluator.Instance;
            TurnWindowSchedule = turnWindowSchedule ?? Turns.NoTurnWindowSchedule.Instance;
            CommandProcessor = commandProcessor ?? PendingImplementationCommandProcessor.Instance;
            DisplacementRequestBuilder = displacementRequestBuilder ?? NoForcedDisplacementRequests.Instance;
            DisplacementSolver = displacementSolver ?? NoForcedDisplacementSolver.Instance;
            DisplacementCommitter = displacementCommitter ?? NoForcedDisplacementCommitter.Instance;
            ResolutionCommit = resolutionCommit ?? NoResolutionCommitSystem.Instance;
            PayloadAuthorizer = payloadAuthorizer ?? PermissivePayloadAuthorizer.Instance;
            InvariantChecks = invariantChecks ?? Array.Empty<IStepInvariantCheck>();
            ArchiveCandidateSources = archiveCandidateSources ?? Array.Empty<IHistoryArchiveCandidateSource>();
            DecisionObservers = decisionObservers ?? Array.Empty<IDecisionObserver>();
            PhaseTiming = phaseTiming;
            LifecycleNoticeSink = lifecycleNoticeSink;
            // 未显式注入时保持 null：BattleSimulation 会把本场自己的预算权威接到该接缝上
            // （任务 07 的真实 Reserved -> Spent 提交端口）。
            // 注意：这里**不得**兜底成 NoTurnBudgetCommitPort——那会让
            // "assembly.StartCommitPort ?? 预算权威" 变成死代码，任务 07 的启动提交永不生效。
            StartCommitPort = startCommitPort;
            DodgeRelocationTransaction = dodgeRelocationTransaction;
            AreaThreatCandidateSource = areaThreatCandidateSource;
            ReactionPreparationPort = reactionPreparationPort;
            ReactionReservationReleaseSink = reactionReservationReleaseSink;
            MovementPathCalculator = movementPathCalculator;
            UnitVolumeTables = unitVolumeTables;
            BudgetReleaseSink = budgetReleaseSink;
            ConcurrentHeroUnitId = concurrentHeroUnitId;
            AdrenalineAccrualFactSource = adrenalineAccrualFactSource;
            // 阶段 2 的判据扩展点：显式给出的优先；否则若评估器同时实现了它
            // （测试用的同类夹具常常同时实现两处钩子），自动采用同一实例，
            // 避免"评估器被推迟但命令前判据没被推迟"这类装配歧义。
            PreCommandVictoryGate = preCommandVictoryGate ?? (VictoryEvaluator as IPreCommandVictoryGate);
        }

        public IUnitStateAdvanceSystem UnitStateAdvance { get; }
        public IVictoryEvaluator VictoryEvaluator { get; }
        public Turns.ITurnWindowSchedule TurnWindowSchedule { get; }
        public IFrozenCommandProcessor CommandProcessor { get; }
        public IForcedDisplacementRequestBuilder DisplacementRequestBuilder { get; }
        public IForcedDisplacementSolver DisplacementSolver { get; }
        public IForcedDisplacementCommitter DisplacementCommitter { get; }
        public IResolutionCommitSystem ResolutionCommit { get; }
        public ICommandPayloadAuthorizer PayloadAuthorizer { get; }
        public IReadOnlyList<IStepInvariantCheck> InvariantChecks { get; }
        public IReadOnlyList<IHistoryArchiveCandidateSource> ArchiveCandidateSources { get; }
        public IReadOnlyList<IDecisionObserver> DecisionObservers { get; }

        /// <summary>可选的阶段计时采样器（默认 null = 不采样；计时结果绝不进入逻辑输入或哈希）。</summary>
        public StepPhaseTimingRecorder PhaseTiming { get; }

        /// <summary>
        /// 可选的死亡生命周期清理通知接收点（任务 05 接入统一终态协调器）。
        /// 默认 null = 通知只写入 <c>BattleSimulation.PendingLifecycleNotices</c>，
        /// 由任务 05 在阶段 13 读取——两种消费方式共享<strong>同一份</strong>一次性事实。
        /// </summary>
        public IUnitLifecycleNoticeSink LifecycleNoticeSink { get; }

        /// <summary>
        /// 可选的阶段 2 胜负判据扩展点（默认 null = 用内置的配置判据）。
        /// 只服务测试与诊断，见 <see cref="IPreCommandVictoryGate"/>。
        /// </summary>
        public IPreCommandVictoryGate PreCommandVictoryGate { get; }

        /// <summary>
        /// 任务 05 的启动门禁原子提交端口。<strong>默认 null</strong> ⇒ 由 <c>BattleSimulation</c>
        /// 接上本场自己的预算权威（任务 07 的真实实现，把 <c>Reserved</c> 原子转为 <c>Spent</c>）；
        /// 显式注入的端口优先（任务 05 的失败端口、诊断装配等）。它<strong>不改变</strong>
        /// 门禁的四分结果、原子性要求或计划生命周期。
        /// </summary>
        public IActionPlanStartCommitPort StartCommitPort { get; }

        /// <summary>
        /// 任务 07：可激活并发行动的<strong>主角</strong>单位（默认 null ⇒ 激活 fail-closed）。
        ///
        /// 它<strong>必须</strong>由装配方显式给出：Logic 绝不从 Controller、玩家标志、阵营或名称
        /// 猜"谁是主角"，否则并发提交授权就退化为"按调用方自报身份放行"。
        /// </summary>
        public UnitId? ConcurrentHeroUnitId { get; }

        /// <summary>
        /// 任务 07 冻结的 Tick 末肾上腺素入账事实来源（任务 08 在全部 Resolution 提交后提供；
        /// 默认 null = 本 Tick 不入账）。它是 Available 增长的<strong>唯一</strong>入口，
        /// 其他系统不得逐接触直接加 Available。
        /// </summary>
        public Resources.IAdrenalineAccrualFactSource AdrenalineAccrualFactSource { get; }

        /// <summary>
        /// 任务 05 的 <strong>Dodge TriggerTick 换位事务端口</strong>（默认 null = 明确拒绝且零写入）。
        ///
        /// 任务 06/08 提供真实实现：它必须在<strong>一个</strong>事务内完成换位与空间 Reservation 调整，
        /// 失败时零副作用；被失效的后续移动仍然<strong>只能</strong>由统一终态协调器以
        /// <c>MovementOriginInvalidatedByDodge</c> 终止。任务 05 只冻结接缝，绝不伪造触发。
        /// </summary>
        public IDodgeRelocationTransaction DodgeRelocationTransaction { get; }

        /// <summary>
        /// 任务 05 的反应机会装配接缝：区域攻击的逻辑威胁候选来源（任务 06 提供真实几何）。
        /// 默认 null ⇒ 机会系统使用 <c>NoAreaThreatCandidateSource</c>（fail-closed，不猜区域几何）。
        /// </summary>
        public IAreaThreatCandidateSource AreaThreatCandidateSource { get; }

        /// <summary>
        /// 任务 07 的资源/目的格冻结接缝（接受反应之前调用）。默认 null ⇒ 只校验、不冻结。
        /// </summary>
        public IReactionPreparationPort ReactionPreparationPort { get; }

        /// <summary>
        /// 任务 07 的"释放未消费预留"接收方（<c>SourceThreatCancelled</c> 例外）。
        /// 默认 null ⇒ 请求在机会系统内<strong>排队保留</strong>（绝不静默丢弃），任务 07 装配后取走。
        /// </summary>
        public IReactionReservationReleaseSink ReactionReservationReleaseSink { get; }

        /// <summary>任务 03 标准装配（全部为明确的空实现/最小规则实现）。</summary>
        public static BattleSimulationAssembly Standard() => new BattleSimulationAssembly();

        /// <summary>
        /// 任务 06 的<strong>移动链路径重算端口</strong>（任务 05 交接记录 §27.2 硬前置条件 1）。
        ///
        /// 显式给出时：它被透传给 <c>ScheduleEditor</c>，并且<strong>同一个实例</strong>也交给
        /// Dodge 终态接缝的求值器——两处必须共用同一计算器，否则路径投影会分叉（缺陷 D6）。
        ///
        /// 为 <c>null</c> 时由 <c>BattleSimulation</c> 绑定<strong>它自己</strong>基于本次
        /// <c>LogicGrid</c> 构造的真实现（消费唯一的纯逻辑 <c>LogicPathfinder</c>）。
        /// 因此"未注入"在装配层不是静默恒等：真实现始终存在；
        /// 只有显式注入 <c>NotImplementedMovementPathCalculator.Instance</c>（负控制）
        /// 才会让求值器以 <c>SCHEDULE_PATH_CALCULATOR_NOT_IMPLEMENTED</c> 失败。
        /// </summary>
        public IMovementPathCalculator MovementPathCalculator { get; }

        /// <summary>
        /// 任务 06 的<strong>单位体积规范表来源</strong>（默认 null ⇒ 单格占位过渡形态）。
        /// 任务 02B/10 补齐"单位 → <c>VolumeSpec</c>"绑定后在此注入。
        /// </summary>
        public IUnitVolumeTableSource UnitVolumeTables { get; }

        /// <summary>
        /// 任务 07 的<strong>预算释放消费者</strong>（任务 06 小修轮 R4/B-1 新增的显式注册点）。
        ///
        /// 语义：Dodge 换位<strong>实际发生</strong>时被调用一次，参数为按 <c>ActionPlanId</c> 升序的
        /// 失效计划；它表示"这些计划的 Editable 预算预留已被释放"。
        /// 装配路径：<c>BattleSimulation</c> 把它接到
        /// <c>DodgeRelocationAuthority.BudgetReleaseSink</c>。
        ///
        /// 默认 <c>null</c> 是<strong>有意</strong>的：任务 06 不实现 TurnBudget 账本，
        /// 因此默认装配下没有"预算"这个对象可以被释放——该调用是<strong>语义上的 no-op，
        /// 而不是被漏掉的接线</strong>；接缝本身已在生产装配里接通（见上），
        /// 任务 07 只需注入真实消费者，<strong>不需要</strong>再改装配代码。
        ///
        /// <strong>顺序契约</strong>：回调整体在换位的全部变异<strong>之前</strong>被调用
        /// （见 <c>DodgeRelocationAuthority.CommitReservation</c> 阶段 B′），
        /// 因此抛异常的消费者不会留下半提交状态。
        /// </summary>
        public Action<IReadOnlyList<ActionPlanId>> BudgetReleaseSink { get; }
    }
}
