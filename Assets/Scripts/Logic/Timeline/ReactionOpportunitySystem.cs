using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>反应机会系统新增的稳定码（不改名、不复用既有码）。</summary>
    public static class ReactionOpportunityCodes
    {
        public const string ATTACK_NOT_REACTABLE = "REACTION_ATTACK_NOT_REACTABLE";
        public const string LEAD_TOO_SHORT = "REACTION_LEAD_TOO_SHORT";
        public const string NO_REACHABLE_OPTION = "REACTION_NO_REACHABLE_OPTION";
        public const string SOURCE_NOT_ATTACK = "REACTION_SOURCE_NOT_ATTACK";
        public const string SOURCE_NOT_TELEGRAPHED = "REACTION_SOURCE_NOT_TELEGRAPHED";
        public const string SOURCE_ALREADY_OPEN = "REACTION_SOURCE_ALREADY_OPEN";
        public const string OPTION_ALREADY_RESOLVED = "REACTION_OPTION_ALREADY_RESOLVED";
        public const string OPPORTUNITY_NOT_OPEN = ReactionCodes.OPPORTUNITY_NOT_OPEN;
    }

    /// <summary>
    /// 区域攻击在 <c>TelegraphTick</c> 的<strong>逻辑威胁候选</strong>来源。
    /// 真实几何属于任务 06；任务 05 只定义接入面，默认实现明确"没有候选"（fail-closed），
    /// 绝不猜测区域、也绝不用"敌方单位"之类的表现标志代替几何。
    /// </summary>
    public interface IAreaThreatCandidateSource
    {
        /// <summary>返回候选单位（顺序无关；系统会按 <c>UnitId</c> 升序规范化）。</summary>
        IReadOnlyList<UnitId> CandidatesFor(ActionPlan sourcePlan, long telegraphTick);
    }

    /// <summary>任务 06 接入前的默认候选来源：<strong>不猜测</strong>，返回空。</summary>
    public sealed class NoAreaThreatCandidateSource : IAreaThreatCandidateSource
    {
        public static readonly NoAreaThreatCandidateSource Instance = new NoAreaThreatCandidateSource();

        public IReadOnlyList<UnitId> CandidatesFor(ActionPlan sourcePlan, long telegraphTick)
            => Array.Empty<UnitId>();
    }

    /// <summary>
    /// 接受反应前的<strong>资源/目的格事务接缝</strong>（任务 07/06 接入）。
    /// 默认实现只校验、不冻结；任务 07 在此处把肾上腺素/Cycle 预留从 Available 转入 Reserved。
    /// </summary>
    public interface IReactionPreparationPort
    {
        /// <summary>返回 null = 允许；否则返回稳定拒绝码，且不得留下任何已冻结副作用。</summary>
        string Prepare(ActionPlan sourcePlan, ReactionOption option, UnitId defenderUnitId, long tick);

        /// <summary>回滚 <see cref="Prepare"/> 可能产生的副作用（仅在后续步骤失败时调用）。</summary>
        void Rollback(ActionPlan sourcePlan, ReactionOption option, UnitId defenderUnitId, long tick);
    }

    /// <summary>
    /// <c>SourceThreatCancelled</c> 的"释放未消费预留"通知接收方（任务 07）。
    /// 未注入接收方时请求会<strong>排队保留</strong>（而不是静默丢弃），任务 07 可在装配后取走。
    /// </summary>
    public interface IReactionReservationReleaseSink
    {
        void ReleaseFor(ActionPlanId reactionPlanId, ReactionOpportunityId opportunityId, long tick);
    }

    /// <summary>
    /// 反应<strong>接受事务</strong>内的 <strong>Dodge 目的格预留</strong>接缝（任务 06 接入；
    /// 实现见 <c>ProjectHero.Logic.Movement.DodgeRelocationAuthority</c>）。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>它在反应计划被创建<strong>之前</strong>被调用，因此返回非 null 时整个接受事务必须
    /// 整条回滚（<see cref="IReactionPreparationPort.Rollback"/> + <see cref="ReleaseDestination"/>），
    /// 且<strong>不</strong>留下任何已冻结副作用；</item>
    /// <item>成功时按 <c>ReactionOpportunityId</c> 为 <c>TriggerTick</c> 冻结合法目的格；
    /// 计划创建成功后由 <see cref="BindPlan"/> 绑定 <c>ActionPlanId</c>；</item>
    /// <item>冲突胜者<strong>只</strong>由 <paramref name="commandSequence"/>（任务 03 网关的规范命令序）
    /// 决定，后到者稳定拒绝、不可抢占。</item>
    /// </list>
    /// </summary>
    public interface IDodgeDestinationReservationPort
    {
        /// <summary>返回 null = 目的格已为该机会合法冻结；否则稳定拒绝码且零写入。</summary>
        string ReserveDestination(
            ReactionOpportunityId opportunityId,
            UnitId defenderUnitId,
            ActionSpecId dodgeSpecId,
            GridPoint destination,
            long triggerTick,
            long commandSequence,
            long tick);

        /// <summary>计划创建成功后绑定 <c>ActionPlanId</c>（不改变预留的合法性）。</summary>
        void BindPlan(ReactionOpportunityId opportunityId, ActionPlanId reactionPlanId);

        /// <summary>释放该机会的预留（幂等；用于接受事务回滚与来源威胁取消）。</summary>
        void ReleaseDestination(ReactionOpportunityId opportunityId);
    }

    /// <summary>
    /// 一条待发送的预留释放通知。
    /// </summary>
    public sealed record ReactionReservationReleaseRequest(
        ActionPlanId ReactionPlanId, ReactionOpportunityId OpportunityId, long Tick);

    /// <summary>运行时反应选项：在冻结的 <see cref="ReactionOption"/> 之上叠加发布/过期状态。</summary>
    public sealed class ReactionOptionRuntime
    {
        internal ReactionOptionRuntime(ReactionOption option, bool isPublished)
        {
            Option = option;
            IsPublished = isPublished;
        }

        public ReactionOption Option { get; }

        public ActionSpecId ReactionActionSpecId => Option.ReactionActionSpecId;

        public ActionType ReactionType => Option.ReactionType;

        public long ResponseDeadlineTick => Option.ResponseDeadlineTick;

        /// <summary>是否满足 <c>ResponseDeadlineTick &gt;= TelegraphTick + CommandIngressLeadTicks</c>。</summary>
        public bool IsPublished { get; }

        /// <summary>仍然开放（可被接受、也仍会产生过期事件）。</summary>
        public bool IsOpen { get; internal set; } = true;

        /// <summary>结果码（Accepted / Expired / SourceCancelled）；null = 尚未有结果。</summary>
        public string OutcomeCode { get; internal set; }
    }

    /// <summary>
    /// 运行时反应机会。它是<strong>权威活动对象</strong>：状态机固定为
    /// <c>Open/Accepted/Triggered/Expired/SourceCancelled/BattleEnded</c>，
    /// 只有 <c>Open</c> 接收命令。
    /// </summary>
    public sealed class ReactionOpportunityRuntime
    {
        internal ReactionOpportunityRuntime(
            ReactionOpportunityId id, ActionPlanId sourceAttackPlanId, UnitId defenderUnitId,
            long telegraphTick, long triggerTick, List<ReactionOptionRuntime> options)
        {
            Id = id;
            SourceAttackPlanId = sourceAttackPlanId;
            DefenderUnitId = defenderUnitId;
            TelegraphTick = telegraphTick;
            TriggerTick = triggerTick;
            Options = options;
        }

        public ReactionOpportunityId Id { get; }

        public ActionPlanId SourceAttackPlanId { get; }

        public UnitId DefenderUnitId { get; }

        /// <summary>首版 = 来源攻击的 <c>StartTick</c>。</summary>
        public long TelegraphTick { get; }

        /// <summary>= 来源攻击的 <c>ImpactTick</c>（由 Logic 固定推导，命令无法声明）。</summary>
        public long TriggerTick { get; }

        public IReadOnlyList<ReactionOptionRuntime> Options { get; }

        public ReactionOpportunityState State { get; internal set; } = ReactionOpportunityState.Open;

        public string CloseReason { get; internal set; }

        public long ClosedAtTick { get; internal set; } = -1L;

        public ActionPlanId BoundActionPlanId { get; internal set; }

        public string AcceptedActionSpecId { get; internal set; }

        public bool IsOpen => State == ReactionOpportunityState.Open;

        /// <summary>仍然开放的选项数。</summary>
        public int OpenOptionCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < Options.Count; i++)
                {
                    if (Options[i].IsOpen) count++;
                }
                return count;
            }
        }
    }

    /// <summary>
    /// <strong><c>ReactionOpportunitySystem</c></strong>（任务包「必须产出」11）。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>来源攻击在执行 Tick <strong>锁定并启动</strong>后才公开机会；仍为 Editable 的攻击不公开、不能响应。</item>
    /// <item>只为带 <c>Reactable</c> 且 <c>ImpactTick - TelegraphTick &gt;= MinimumReactionLeadTicks</c>
    /// 的攻击创建机会。</item>
    /// <item>候选<strong>只</strong>经 <see cref="IFactionRelationResolver"/> + 攻击载荷的
    /// <c>AllowedTargetRelations</c> 判定：本系统在结构上不读 <c>IsPlayerControlled</c>、
    /// Controller 类型或任何"默认敌方"标志。显式允许友军伤害时，友军威胁走完全相同流程。</item>
    /// <item>选项按 <c>ActionSpecId</c> Ordinal 排序，各自
    /// <c>ResponseDeadlineTick = ImpactTick - ReactionWindupTicks</c>；只公开
    /// <c>ResponseDeadlineTick &gt;= TelegraphTick + CommandIngressLeadTicks</c> 的选项；
    /// 没有相容且可达选项时不创建机会。</item>
    /// <item>每个选项在其截止 Tick 的命令阶段结束后<strong>只发一次</strong>过期事件；
    /// 最后一项过期才关闭机会；第一次离开 <c>Open</c> 只发一次关闭事件。</item>
    /// </list>
    ///
    /// 它<strong>不</strong>伪造触发：Dodge 的 <c>Triggered</c> 转换必须等任务 06/08 的
    /// TriggerTick 位置事务成功（见 <see cref="ConfirmTrigger"/>），本任务只留接缝。
    /// </summary>
    public sealed class ReactionOpportunitySystem
    {
        private readonly ActionScheduleAuthority _authority;
        private readonly LogicIdGenerator _idGenerator;
        private readonly ActionPlanFactory _factory;
        private readonly BattleDefinition _definition;
        private readonly IFactionRelationResolver _factions;
        private readonly IAreaThreatCandidateSource _areaCandidates;
        private readonly ActionPlanTerminalCoordinator _coordinator;
        private readonly ReactionPlanner _planner;

        private readonly List<ReactionOpportunityRuntime> _active = new List<ReactionOpportunityRuntime>();
        private readonly List<LogicEvent> _emitted = new List<LogicEvent>();
        private readonly List<ReactionReservationReleaseRequest> _pendingReleases =
            new List<ReactionReservationReleaseRequest>();

        public ReactionOpportunitySystem(
            ActionScheduleAuthority authority,
            ActionPlanFactory factory,
            BattleDefinition definition,
            IFactionRelationResolver factions,
            LogicIdGenerator idGenerator,
            IAreaThreatCandidateSource areaCandidates = null,
            ActionPlanTerminalCoordinator coordinator = null)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _factions = factions ?? throw new ArgumentNullException(nameof(factions));
            // 任务 05 收尾 R1（缺陷 D2）：机会 ID 的唯一分配器是任务 03 冻结契约的
            // LogicIdGenerator.NextReactionOpportunityId()。它<strong>必填</strong>——
            // 缺省回落会重新引入"第二份计数状态"这一缺陷形态。
            _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
            _areaCandidates = areaCandidates ?? NoAreaThreatCandidateSource.Instance;
            _coordinator = coordinator;
            _planner = new ReactionPlanner(_authority, _factory, _definition);
        }

        /// <summary>把选项变成真实 Locked 计划的唯一入口。</summary>
        public ReactionPlanner Planner => _planner;

        /// <summary>事件发射端口（装配方接到 <c>LogicEventOutbox.Emit</c>）。未注入时事件只进内部缓冲。</summary>
        public Action<Func<long, LogicEvent>> EventSink { get; set; }

        /// <summary>预留释放通知接收方（任务 07）。</summary>
        public IReactionReservationReleaseSink ReservationReleaseSink { get; set; }

        /// <summary>资源/目的格事务接缝（任务 07/06）。默认只校验。</summary>
        public IReactionPreparationPort PreparationPort { get; set; }

        /// <summary>
        /// 任务 06 的 <strong>Dodge 目的格预留端口</strong>（未装配时为 null ⇒ 只校验不冻结，
        /// 与任务 05 的原有语义一致）。装配后，Dodge 的接受事务会在创建计划之前先冻结合法目的格；
        /// 预留失败即整条接受事务回滚。
        /// </summary>
        public IDodgeDestinationReservationPort DestinationReservationPort { get; set; }

        /// <summary>活动机会（按 <c>ReactionOpportunityId</c> 升序的稳定枚举）。</summary>
        public IReadOnlyList<ReactionOpportunityRuntime> ActiveOpportunities => _active;

        /// <summary>
        /// 按 <c>ReactionOpportunityId</c> 查找机会（<strong>只读</strong>；未找到返回 <c>null</c>）。
        /// 它只暴露权威活动对象的只读视图；命令路径仍然是 <see cref="TryAccept"/>。
        /// </summary>
        public ReactionOpportunityRuntime FindOpportunity(ReactionOpportunityId opportunityId)
            => FindById(opportunityId);

        /// <summary>尚未被接收方取走的预留释放请求（顺序 = 产生顺序）。</summary>
        public IReadOnlyList<ReactionReservationReleaseRequest> PendingReservationReleases => _pendingReleases;

        /// <summary>本 Tick 已发射的事件（按产生顺序；<c>Sequence</c> 为 0 占位）。</summary>
        public IReadOnlyList<LogicEvent> EmittedEvents => _emitted;

        /// <summary>取走并清空事件缓冲（注入 <see cref="EventSink"/> 后仍可用作审计）。</summary>
        public List<LogicEvent> DrainEmittedEvents()
        {
            var copy = new List<LogicEvent>(_emitted);
            _emitted.Clear();
            return copy;
        }

        // ————————————————————————————————————————————————————————————
        // 公开机会
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 为一条已锁定/启动的攻击公开机会。
        /// 返回 null = 已处理（<paramref name="opened"/> 里追加了新建的机会）；
        /// 否则返回稳定拒绝码，且<strong>不写任何状态</strong>。
        /// </summary>
        public string TryOpenForTelegraph(ActionPlan sourcePlan, long tick, List<ReactionOpportunityRuntime> opened)
        {
            if (sourcePlan == null || !sourcePlan.IsOrdinary) return ReactionOpportunityCodes.SOURCE_NOT_ATTACK;
            if (sourcePlan.ActionType != ActionType.Attack) return ReactionOpportunityCodes.SOURCE_NOT_ATTACK;
            if (sourcePlan.IsEditable) return ReactionOpportunityCodes.SOURCE_NOT_TELEGRAPHED;

            ActionSpec spec = _definition.FindAction(sourcePlan.ActionSpecId);
            if (spec == null) return ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND;
            var attack = spec.Payload as AttackPayloadSpec;
            if (attack == null) return ReactionOpportunityCodes.SOURCE_NOT_ATTACK;

            // 首版 TelegraphTick = StartTick。
            long telegraphTick = sourcePlan.StartTick;
            if (tick < telegraphTick) return ReactionOpportunityCodes.SOURCE_NOT_TELEGRAPHED;

            if ((attack.Tags & AttackTagMask.Reactable) == 0)
                return ReactionOpportunityCodes.ATTACK_NOT_REACTABLE;

            long lead = sourcePlan.ImpactTick - telegraphTick;
            if (lead < _definition.ReactionRules.MinimumReactionLeadTicks)
                return ReactionOpportunityCodes.LEAD_TOO_SHORT;

            if (FindBySource(sourcePlan.ActionPlanId) != null)
                return ReactionOpportunityCodes.SOURCE_ALREADY_OPEN;

            List<UnitId> candidates = CollectDefenders(sourcePlan, attack, telegraphTick);
            if (candidates.Count == 0) return ReactionOpportunityCodes.NO_REACHABLE_OPTION;

            long ingressLead = _definition.ReactionRules.CommandIngressLeadTicks;
            bool anyCreated = false;

            for (int c = 0; c < candidates.Count; c++)
            {
                UnitId defender = candidates[c];
                List<ReactionOptionRuntime> options = BuildOptions(sourcePlan, attack, defender, telegraphTick, ingressLead);
                if (options.Count == 0) continue;

                var runtime = new ReactionOpportunityRuntime(
                    // 唯一分配器：任务 03 契约的 LogicIdGenerator（缺陷 D2 修复后的单一来源）。
                    _idGenerator.NextReactionOpportunityId(),
                    sourcePlan.ActionPlanId, defender, telegraphTick, sourcePlan.ImpactTick, options);

                _active.Add(runtime);
                SortActive();
                anyCreated = true;
                if (opened != null) opened.Add(runtime);

                var published = new List<string>();
                for (int o = 0; o < options.Count; o++)
                {
                    if (options[o].IsPublished) published.Add(options[o].ReactionActionSpecId.Value);
                }

                Record(new ReactionOpportunityOpenedEvent(
                    tick, 0L, runtime.Id.Value, runtime.SourceAttackPlanId.Value, defender.Value,
                    telegraphTick, runtime.TriggerTick, published));
            }

            return anyCreated ? null : ReactionOpportunityCodes.NO_REACHABLE_OPTION;
        }

        // ————————————————————————————————————————————————————————————
        // 接受命令
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 处理一条反应命令（Block/Dodge）。<strong>载荷不提供 Tick</strong>：
        /// <c>TriggerTick</c> 恒等于来源攻击的 <c>ImpactTick</c>。
        /// 返回 null = 已接受（<paramref name="plan"/> 是新建的、直接 Locked 的统一计划）。
        /// </summary>
        public string TryAccept(
            ReactionOpportunityId opportunityId,
            UnitId defenderUnitId,
            ActionSpecId optionSpecId,
            long tick,
            out ActionPlan plan)
            => TryAccept(opportunityId, defenderUnitId, optionSpecId, tick, null, out plan);

        /// <summary>
        /// 命令路径的便捷入口：<strong>防御者由机会自身决定</strong>，命令不携带单位
        /// （"谁能替该单位提交"属于任务 07 的提交权限，不属于本系统）。
        /// 未找到机会 ⇒ <c>OPPORTUNITY_NOT_OPEN</c>，零写入。
        /// </summary>
        public string TryAcceptById(
            ReactionOpportunityId opportunityId, ActionSpecId optionSpecId, long tick,
            GridPoint? dodgeDestination, out ActionPlan plan)
            => TryAcceptByIdWithCommandSequence(opportunityId, optionSpecId, 0L, tick, dodgeDestination, out plan);

        /// <summary>
        /// 命令路径的便捷入口（携带任务 03 网关的规范命令序）。
        /// 该序号是 Dodge 目的格预留冲突的<strong>唯一</strong>胜者依据。
        /// </summary>
        public string TryAcceptByIdWithCommandSequence(
            ReactionOpportunityId opportunityId, ActionSpecId optionSpecId, long commandSequence, long tick,
            GridPoint? dodgeDestination, out ActionPlan plan)
        {
            ReactionOpportunityRuntime opportunity = FindById(opportunityId);
            if (opportunity == null)
            {
                plan = null;
                return ReactionOpportunityCodes.OPPORTUNITY_NOT_OPEN;
            }
            return TryAcceptWithCommandSequence(
                opportunityId, opportunity.DefenderUnitId, optionSpecId, commandSequence, tick,
                dodgeDestination, out plan);
        }

        /// <summary>
        /// 与 <see cref="TryAccept(ReactionOpportunityId, UnitId, ActionSpecId, long, out ActionPlan)"/> 同语义，
        /// 但把命令携带的 <paramref name="dodgeDestination"/>（仅 Dodge 允许）落到计划的
        /// <c>Destination</c> 上：换位事务属于任务 06/08，目的格必须先被权威记录下来，
        /// 绝不能在命令 Tick 结束后丢失。
        /// </summary>
        public string TryAccept(
            ReactionOpportunityId opportunityId,
            UnitId defenderUnitId,
            ActionSpecId optionSpecId,
            long tick,
            GridPoint? dodgeDestination,
            out ActionPlan plan)
            => TryAcceptWithCommandSequence(
                opportunityId, defenderUnitId, optionSpecId, 0L, tick, dodgeDestination, out plan);

        /// <summary>
        /// 与 <see cref="TryAccept(ReactionOpportunityId, UnitId, ActionSpecId, long, GridPoint?, out ActionPlan)"/>
        /// 同语义，但显式携带任务 03 网关的规范命令序 <paramref name="commandSequence"/>：
        /// 它是 Dodge 目的格预留冲突的<strong>唯一</strong>胜者依据（后到者稳定拒绝、不可抢占）。
        /// </summary>
        public string TryAcceptWithCommandSequence(
            ReactionOpportunityId opportunityId,
            UnitId defenderUnitId,
            ActionSpecId optionSpecId,
            long commandSequence,
            long tick,
            GridPoint? dodgeDestination,
            out ActionPlan plan)
        {
            plan = null;

            ReactionOpportunityRuntime opportunity = FindById(opportunityId);
            if (opportunity == null) return ReactionOpportunityCodes.OPPORTUNITY_NOT_OPEN;
            if (opportunity.DefenderUnitId != defenderUnitId)
                return ReactionCodes.OPPORTUNITY_DEFENDER_MISMATCH;
            if (!opportunity.IsOpen) return ReactionOpportunityCodes.OPPORTUNITY_NOT_OPEN;

            ReactionOptionRuntime option = null;
            for (int i = 0; i < opportunity.Options.Count; i++)
            {
                if (opportunity.Options[i].ReactionActionSpecId != optionSpecId) continue;
                option = opportunity.Options[i];
                break;
            }
            if (option == null) return ReactionCodes.OPPORTUNITY_OPTION_NOT_OFFERED;
            if (!option.IsPublished) return ReactionCodes.OPTION_NOT_PUBLISHED;
            if (!option.IsOpen) return ReactionOpportunityCodes.OPTION_ALREADY_RESOLVED;
            // 到期判定在<strong>截止 Tick 的命令阶段之后</strong>：本 Tick 恰好等于截止 Tick 仍然可接受。
            if (tick > option.ResponseDeadlineTick) return ReactionCodes.OPTION_DEADLINE_ELAPSED;

            ActionPlan sourcePlan = _authority.Registry.Find(opportunity.SourceAttackPlanId);
            if (sourcePlan == null) return ReactionCodes.REACTION_SOURCE_THREAT_ALREADY_TERMINAL;
            if (sourcePlan.IsTerminal) return ReactionCodes.REACTION_SOURCE_THREAT_ALREADY_TERMINAL;

            ActionSpec optionSpec = _definition.FindAction(optionSpecId);
            if (optionSpec == null) return ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND;
            if (optionSpec.Type != ActionType.Block && optionSpec.Type != ActionType.Dodge)
                return ReactionCodes.REACTION_ACTION_NOT_BLOCK_OR_DODGE;

            // 目的格只属于 Dodge：Block 携带目的格必须在命令结构校验处就被拒绝，
            // 这里做第二道防线（绝不让 Block 悄悄换位）。
            if (optionSpec.Type == ActionType.Block && dodgeDestination.HasValue)
                return ReactionCodes.REACTION_ACTION_NOT_BLOCK_OR_DODGE;

            ActorLane lane = _authority.FindLane(defenderUnitId);
            if (lane != null && lane.IsSubmissionLocked)
                return ReactionCodes.REACTION_LANE_SUBMISSION_LOCKED;

            // 先冻结资源/目的格（任务 07/06 接缝），再创建计划：失败必须整条回滚。
            string prepared = PreparationPort?.Prepare(sourcePlan, option.Option, defenderUnitId, tick);
            if (prepared != null) return prepared;

            // 任务 06：Dodge 目的格在**计划创建之前**冻结（按 ReactionOpportunityId 键）。
            // 目的格无效 ⇒ 接受事务整条回滚：命令被拒绝、机会保持 Open、单位不移动、零写入。
            if (dodgeDestination.HasValue && DestinationReservationPort != null)
            {
                string reserved = DestinationReservationPort.ReserveDestination(
                    opportunity.Id, defenderUnitId, optionSpecId, dodgeDestination.Value,
                    opportunity.TriggerTick, commandSequence, tick);
                if (reserved != null)
                {
                    PreparationPort?.Rollback(sourcePlan, option.Option, defenderUnitId, tick);
                    return reserved;
                }
            }

            string planError = _planner.TryPlan(
                opportunity, option, sourcePlan, tick, dodgeDestination, out ActionPlan reaction);
            if (planError != null)
            {
                DestinationReservationPort?.ReleaseDestination(opportunity.Id);
                PreparationPort?.Rollback(sourcePlan, option.Option, defenderUnitId, tick);
                return planError;
            }

            // 计划已存在 ⇒ 把 ActionPlanId 绑定到已冻结的目的格预留上（不改预留的合法性）。
            if (dodgeDestination.HasValue && DestinationReservationPort != null)
                DestinationReservationPort.BindPlan(opportunity.Id, reaction.ActionPlanId);

            opportunity.State = ReactionOpportunityState.Accepted;
            opportunity.BoundActionPlanId = reaction.ActionPlanId;
            opportunity.AcceptedActionSpecId = optionSpecId.Value;
            option.IsOpen = false;
            option.OutcomeCode = ReactionOptionOutcomes.Accepted;
            CloseRemainingOptions(opportunity, option);

            plan = reaction;
            return null;
        }

        // ————————————————————————————————————————————————————————————
        // 过期与关闭
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 处理截止：每个选项在其 <c>ResponseDeadlineTick</c> 的命令阶段<strong>结束之后</strong>
        /// （即 <c>tick &gt; deadline</c>）恰好过期一次；最后一个开放选项过期时关闭机会。
        /// </summary>
        public void ExpireDueOptions(long tick)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                ReactionOpportunityRuntime opportunity = _active[i];
                if (!opportunity.IsOpen) continue;

                for (int o = 0; o < opportunity.Options.Count; o++)
                {
                    ReactionOptionRuntime option = opportunity.Options[o];
                    if (!option.IsOpen) continue;
                    if (tick <= option.ResponseDeadlineTick) continue;

                    option.IsOpen = false;
                    option.OutcomeCode = ReactionOptionOutcomes.Expired;

                    bool closes = opportunity.OpenOptionCount == 0;
                    Record(new ReactionOptionExpiredEvent(
                        tick, 0L, opportunity.Id.Value, opportunity.DefenderUnitId.Value,
                        option.ReactionActionSpecId.Value, option.ResponseDeadlineTick, closes));

                    if (closes) CloseOpportunity(opportunity, ReactionOpportunityState.Expired,
                        ReactionCloseReasons.AllOptionsExpired, tick, -1L);
                }
            }
        }

        /// <summary>
        /// 来源攻击在 <c>TriggerTick</c> 前终止：关闭机会、以
        /// <c>SourceThreatCancelled</c> 终止绑定反应，并<strong>发出释放未消费预留的通知</strong>。
        /// 返回被终止的反应计划 id（按稳定序）。
        /// </summary>
        public List<ActionPlanId> CancelForSourceThreat(ActionPlanId sourceAttackPlanId, long tick)
        {
            var terminated = new List<ActionPlanId>();
            for (int i = 0; i < _active.Count; i++)
            {
                ReactionOpportunityRuntime opportunity = _active[i];
                if (opportunity.SourceAttackPlanId != sourceAttackPlanId) continue;
                if (!opportunity.IsOpen && opportunity.State != ReactionOpportunityState.Accepted) continue;

                for (int o = 0; o < opportunity.Options.Count; o++)
                {
                    ReactionOptionRuntime option = opportunity.Options[o];
                    if (option.OutcomeCode == ReactionOptionOutcomes.Accepted) continue;
                    option.IsOpen = false;
                    option.OutcomeCode = ReactionOptionOutcomes.SourceCancelled;
                }

                if (opportunity.BoundActionPlanId.IsValid)
                {
                    ActionPlan bound = _authority.Registry.Find(opportunity.BoundActionPlanId);
                    if (bound != null && !bound.IsTerminal)
                    {
                        if (_coordinator != null)
                        {
                            _coordinator.EnterTerminal(bound, ActionTerminationReason.SourceThreatCancelled, tick);
                        }
                        terminated.Add(bound.ActionPlanId);
                    }
                    RequestReservationRelease(bound == null ? opportunity.BoundActionPlanId : bound.ActionPlanId,
                        opportunity.Id, tick);
                }

                CloseOpportunity(opportunity, ReactionOpportunityState.SourceCancelled,
                    ReactionCloseReasons.SourceThreatCancelled, tick,
                    opportunity.BoundActionPlanId.IsValid ? opportunity.BoundActionPlanId.Value : -1L);
            }
            return terminated;
        }

        /// <summary>战斗结束 Finalizer：关闭全部仍未关闭的机会（同一个 Finalizer，不另写一套清理）。</summary>
        public void CloseAllForBattleEnd(long tick)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                ReactionOpportunityRuntime opportunity = _active[i];
                if (!opportunity.IsOpen && opportunity.State != ReactionOpportunityState.Accepted) continue;
                for (int o = 0; o < opportunity.Options.Count; o++)
                {
                    ReactionOptionRuntime option = opportunity.Options[o];
                    if (option.IsOpen) { option.IsOpen = false; option.OutcomeCode = ReactionOptionOutcomes.Expired; }
                }
                CloseOpportunity(opportunity, ReactionOpportunityState.BattleEnded,
                    ReactionCloseReasons.BattleEnded, tick,
                    opportunity.BoundActionPlanId.IsValid ? opportunity.BoundActionPlanId.Value : -1L);
            }
        }

        /// <summary>
        /// 任务 06/08 的 TriggerTick 位置事务<strong>成功</strong>之后的确认接缝。
        /// 只有它能把 <c>Accepted</c> 推进到 <c>Triggered</c> 并发射
        /// <see cref="ReactionTriggeredEvent"/>；本任务绝不伪造触发。
        /// </summary>
        public string ConfirmTrigger(ReactionOpportunityId opportunityId, long tick)
        {
            ReactionOpportunityRuntime opportunity = FindById(opportunityId);
            if (opportunity == null) return ReactionOpportunityCodes.OPPORTUNITY_NOT_OPEN;
            if (opportunity.State == ReactionOpportunityState.Triggered) return null;
            if (opportunity.State != ReactionOpportunityState.Accepted)
                return ReactionOpportunityCodes.OPPORTUNITY_NOT_OPEN;

            ActionPlan bound = _authority.Registry.Find(opportunity.BoundActionPlanId);
            if (bound == null) return ReactionCodes.REACTION_SOURCE_THREAT_ALREADY_TERMINAL;

            opportunity.State = ReactionOpportunityState.Triggered;
            Record(new ReactionTriggeredEvent(
                tick, 0L, opportunity.Id.Value, bound.ActionPlanId.Value,
                opportunity.SourceAttackPlanId.Value, opportunity.DefenderUnitId.Value,
                opportunity.TriggerTick, (int)bound.ActionType));
            return null;
        }

        // ————————————————————————————————————————————————————————————
        // 生命周期事件（由阶段 7/19 调用；同一发射路径）
        // ————————————————————————————————————————————————————————————

        public void EmitPlanCreated(ActionPlan plan, long tick)
        {
            if (plan == null) return;
            Record(new ActionPlanCreatedEvent(
                tick, 0L, plan.ActionPlanId.Value, (int)plan.Origin, plan.OwnerUnitId.Value,
                plan.ActionSpecId.Value, (int)plan.ActionType, plan.StartTick, plan.EndTick,
                plan.LastRequestedStartTick, plan.CreatedAtTick, _authority.ScheduleRevision));
        }

        public void EmitPlanLocked(ActionPlan plan, long tick)
        {
            if (plan == null) return;
            Record(new ActionPlanLockedEvent(
                tick, 0L, plan.ActionPlanId.Value, plan.OwnerUnitId.Value,
                plan.StartTick, plan.LockedAtTick, _authority.ScheduleRevision));
        }

        public void EmitPlanAutoDeferred(
            ActionPlan plan, string blockerReasonCode, long oldStartTick, long retryAtTick,
            IReadOnlyList<long> ripplePlanIds, long tick)
        {
            if (plan == null) return;
            Record(new ActionPlanAutoDeferredEvent(
                tick, 0L, plan.ActionPlanId.Value, plan.OwnerUnitId.Value, blockerReasonCode,
                oldStartTick, plan.StartTick, retryAtTick, plan.AutomaticDeferralCount,
                ripplePlanIds ?? Array.Empty<long>(), _authority.ScheduleRevision));
        }

        public void EmitPlanTerminated(ActionPlan plan, long tick)
        {
            if (plan == null) return;
            Record(new ActionPlanTerminatedEvent(
                tick, 0L, plan.ActionPlanId.Value, plan.OwnerUnitId.Value,
                (int)plan.TerminationReason,
                ActionTerminationReasons.CodeOf(plan.TerminationReason),
                plan.TerminalTick, _authority.ScheduleRevision));
        }

        public void EmitPlanCompleted(ActionPlan plan, long tick)
        {
            if (plan == null) return;
            Record(new ActionPlanCompletedEvent(
                tick, 0L, plan.ActionPlanId.Value, plan.OwnerUnitId.Value,
                plan.TerminalTick, _authority.ScheduleRevision));
        }

        // ————————————————————————————————————————————————————————————
        // 快照
        // ————————————————————————————————————————————————————————————

        /// <summary>活动机会的规范化快照（按 <c>ReactionOpportunityId</c> 升序）。</summary>
        public IReadOnlyList<ReactionOpportunitySnapshot> BuildSnapshots()
        {
            var snapshots = new List<ReactionOpportunitySnapshot>(_active.Count);
            for (int i = 0; i < _active.Count; i++)
            {
                ReactionOpportunityRuntime opportunity = _active[i];
                var options = new List<ReactionOptionSnapshot>(opportunity.Options.Count);
                for (int o = 0; o < opportunity.Options.Count; o++)
                {
                    ReactionOptionRuntime option = opportunity.Options[o];
                    options.Add(new ReactionOptionSnapshot(
                        option.ReactionActionSpecId.Value, (int)option.ReactionType,
                        option.ResponseDeadlineTick, option.IsOpen, option.IsPublished, option.OutcomeCode));
                }

                snapshots.Add(new ReactionOpportunitySnapshot(
                    opportunity.Id.Value, opportunity.DefenderUnitId.Value,
                    opportunity.SourceAttackPlanId.Value, opportunity.TriggerTick,
                    opportunity.TelegraphTick, (int)opportunity.State, opportunity.CloseReason,
                    opportunity.ClosedAtTick, opportunity.BoundActionPlanId.Value,
                    opportunity.AcceptedActionSpecId, options));
            }
            return snapshots;
        }

        // ————————————————————————————————————————————————————————————
        // 内部
        // ————————————————————————————————————————————————————————————

        private List<UnitId> CollectDefenders(ActionPlan sourcePlan, AttackPayloadSpec attack, long telegraphTick)
        {
            var candidates = new List<UnitId>();
            var seen = new HashSet<long>();

            if (attack.TargetPolicy == TargetPolicy.PrimaryTargetOnly)
            {
                if (sourcePlan.PrimaryTargetUnitId.HasValue && sourcePlan.PrimaryTargetUnitId.Value.IsValid)
                    candidates.Add(sourcePlan.PrimaryTargetUnitId.Value);
            }
            else
            {
                IReadOnlyList<UnitId> area = _areaCandidates.CandidatesFor(sourcePlan, telegraphTick);
                if (area != null)
                {
                    for (int i = 0; i < area.Count; i++)
                    {
                        if (!area[i].IsValid) continue;
                        candidates.Add(area[i]);
                    }
                }
            }

            // 候选只经<strong>权威关系解析器 + Attack 的 AllowedTargetRelations</strong> 过滤：
            // 不读 IsPlayerControlled、Controller 类型或任何"默认敌方"标志。
            var accepted = new List<UnitId>();
            for (int i = 0; i < candidates.Count; i++)
            {
                UnitId candidate = candidates[i];
                if (!candidate.IsValid) continue;
                if (!seen.Add(candidate.Value)) continue;
                if (!_factory.TryGetOwnerFacts(candidate, out ActionPlanOwnerFacts facts)) continue;
                if (!facts.IsAlive) continue;
                if (candidate == sourcePlan.OwnerUnitId) continue;
                if (!_factions.Allows(attack.AllowedTargetRelations, sourcePlan.OwnerUnitId, candidate)) continue;
                accepted.Add(candidate);
            }

            accepted.Sort((a, b) => a.Value.CompareTo(b.Value));
            return accepted;
        }

        private List<ReactionOptionRuntime> BuildOptions(
            ActionPlan sourcePlan, AttackPayloadSpec attack, UnitId defender,
            long telegraphTick, long ingressLead)
        {
            var options = new List<ReactionOptionRuntime>();
            bool hasBlockable = HasBlockableContact(attack);
            bool hasDodgeable = (attack.Tags & AttackTagMask.Dodgeable) != 0;
            if (!hasBlockable && !hasDodgeable) return options;

            _factory.TryGetOwnerFacts(defender, out ActionPlanOwnerFacts defenderFacts);
            ActionSetDefinition actionSet = string.IsNullOrEmpty(defenderFacts.ActionSetId.Value)
                ? null
                : _definition.FindActionSet(defenderFacts.ActionSetId);

            // 按 ActionSpecId Ordinal 升序枚举全部候选反应动作。
            var reactions = new List<ActionSpec>();
            IReadOnlyList<ActionSpec> all = _definition.Actions;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    ActionSpec spec = all[i];
                    if (spec == null) continue;
                    if (spec.Type != ActionType.Block && spec.Type != ActionType.Dodge) continue;
                    reactions.Add(spec);
                }
            }
            reactions.Sort((a, b) => string.CompareOrdinal(a.ActionSpecId.Value, b.ActionSpecId.Value));

            for (int i = 0; i < reactions.Count; i++)
            {
                ActionSpec spec = reactions[i];
                bool isBlock = spec.Type == ActionType.Block;
                if (isBlock && !hasBlockable) continue;
                if (!isBlock && !hasDodgeable) continue;

                // 动作集合归属：只在单位声明了 ActionSet 且该集合可解析时生效（与创建期同口径）。
                if (actionSet != null && !actionSet.Contains(spec.ActionSpecId)) continue;

                int windup = ReactionWindupOf(spec);
                if (windup <= 0) continue;

                long deadline = sourcePlan.ImpactTick - windup;
                if (deadline <= 0L) continue;

                bool published = deadline >= telegraphTick + ingressLead;
                options.Add(new ReactionOptionRuntime(
                    new ReactionOption(spec.ActionSpecId, spec.Type, deadline), published));
            }

            // 只保留真正公开的选项；没有公开选项的候选不产生机会。
            var publishedOnly = new List<ReactionOptionRuntime>();
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i].IsPublished) publishedOnly.Add(options[i]);
            }
            return publishedOnly;
        }

        private static int ReactionWindupOf(ActionSpec spec)
        {
            switch (spec.Timing)
            {
                case BlockReactionTimingSpec block: return block.ReactionWindupTicks;
                case DodgeReactionTimingSpec dodge: return dodge.ReactionWindupTicks;
                default: return 0;
            }
        }

        /// <summary>来源至少有一个可格挡伤害分量<strong>或</strong>动量时才提供 Block。</summary>
        private static bool HasBlockableContact(AttackPayloadSpec attack)
        {
            if (attack.ForceMultiplier > 0f) return true;
            IReadOnlyList<DamageComponentSpec> components = attack.DamageComponents;
            if (components == null) return false;
            for (int i = 0; i < components.Count; i++)
            {
                if ((components[i].Tags & DamageTagMask.Blockable) != 0) return true;
            }
            return false;
        }

        private void CloseRemainingOptions(ReactionOpportunityRuntime opportunity, ReactionOptionRuntime accepted)
        {
            for (int i = 0; i < opportunity.Options.Count; i++)
            {
                ReactionOptionRuntime option = opportunity.Options[i];
                if (option == accepted) continue;
                if (!option.IsOpen) continue;
                // 已接受 ⇒ 机会不再接收命令；其余选项既不是"过期"也不是"被接受"，
                // 因此只关闭、不写结果码（绝不伪造过期事件）。
                option.IsOpen = false;
            }
        }

        private void CloseOpportunity(
            ReactionOpportunityRuntime opportunity, ReactionOpportunityState state,
            string reason, long tick, long boundPlanId)
        {
            if (opportunity.State != ReactionOpportunityState.Open &&
                opportunity.State != ReactionOpportunityState.Accepted) return;

            opportunity.State = state;
            opportunity.CloseReason = reason;
            opportunity.ClosedAtTick = tick;
            Record(new ReactionOpportunityClosedEvent(
                tick, 0L, opportunity.Id.Value, opportunity.SourceAttackPlanId.Value,
                opportunity.DefenderUnitId.Value, reason, tick, boundPlanId));
        }

        private void RequestReservationRelease(ActionPlanId reactionPlanId, ReactionOpportunityId opportunityId, long tick)
        {
            if (ReservationReleaseSink != null)
            {
                ReservationReleaseSink.ReleaseFor(reactionPlanId, opportunityId, tick);
                return;
            }

            // 没有接收方时排队保留（绝不静默丢弃）：任务 07 装配后取走。
            //
            // 任务 06 小修轮 R4/B-2（独立验证 L-4）：调用点（CancelForSourceThreat）只在
            // "绑定计划仍非终态"时经统一终态协调器进入终态，而终态清理是 Dodge 目的格预留的
            // 常规释放路径。绑定计划**已经是终态**时协调器幂等跳过 ⇒ 若这里只排队，
            // 预留就只能等任务 07 消费队列（或永远不被释放）。
            //
            // 本方法改为**确定性释放**：只要"绑定计划不存在或已终态"（即本 Tick 不可能再由
            // 终态清理路径负责），就把释放就地下发给目的格预留端口（DodgeRelocationAuthority
            // 实现了 IReactionReservationReleaseSink ⇒ 幂等）。生产装配下该预留在计划进入终态时
            // 已被 MovementAndReservation = 500 参与者释放过，因此这句是**幂等的重复释放**；
            // 它的价值在于自定义装配（未把 Dodge 预留挂到终态参与者上）也**不可能**泄漏，
            // 而不是修补一条当前可达的生产缺陷。
            //
            // 仍会进入队列的只剩一种情形：绑定计划**仍非终态**且没有接收方。
            // 那是任务 05 冻结的语义（请求排队保留给任务 07），不是遗漏。
            if (reactionPlanId.IsValid &&
                !(_authority.Registry.Find(reactionPlanId) is ActionPlan bound && !bound.IsTerminal))
            {
                if (DestinationReservationPort is IReactionReservationReleaseSink portSink)
                {
                    portSink.ReleaseFor(reactionPlanId, opportunityId, tick);
                    return;
                }
            }

            _pendingReleases.Add(new ReactionReservationReleaseRequest(reactionPlanId, opportunityId, tick));
        }

        private ReactionOpportunityRuntime FindById(ReactionOpportunityId id)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i].Id == id) return _active[i];
            }
            return null;
        }

        private ReactionOpportunityRuntime FindBySource(ActionPlanId sourcePlanId)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i].SourceAttackPlanId == sourcePlanId) return _active[i];
            }
            return null;
        }

        private void SortActive()
            => _active.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));

        private void Record(LogicEvent logicEvent)
        {
            if (logicEvent == null) return;
            _emitted.Add(logicEvent);
            if (EventSink == null) return;
            LogicEvent captured = logicEvent;
            EventSink(sequence => captured with { Sequence = sequence });
        }
    }
}
