using System;
using System.Collections.Generic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Snapshots
{
    /// <summary>战斗结束状态（参与规范化哈希）。</summary>
    public sealed record BattleEndSnapshot(bool IsEnded, long EndedAtTick, string ResultCode)
    {
        /// <summary>活动战斗：无结束 Tick、无结果码。</summary>
        public static BattleEndSnapshot Active() => new BattleEndSnapshot(false, -1L, null);
    }

    /// <summary>
    /// 单位快照（主方案 3.11.1）。从首版起就包含 <see cref="FactionId"/>——
    /// 阵营是创建事实，控制入口注册不得覆盖或派生它。
    /// <see cref="HealthQ10"/> 是单位生命进入逻辑世界时<strong>一次性</strong>量化得到的
    /// Q10 整数（RoundHalfUp），因此浮点不会进入哈希。
    ///
    /// 任务 04 扩展的状态族（<see cref="State"/> / <see cref="StateStartTick"/> /
    /// <see cref="StateEndTick"/> / <see cref="CanReceiveDirectHit"/>）：
    /// <list type="bullet">
    /// <item><see cref="StateStartTick"/> / <see cref="StateEndTick"/> 是<strong>半开区间</strong>
    /// <c>[Start, End)</c>；<c>StateEndTick == long.MaxValue</c> 表示无限持续。</item>
    /// <item><see cref="CanReceiveDirectHit"/> 只是<strong>派生只读事实</strong>
    /// （由状态唯一决定，见 <c>UnitStateMachine.CanReceiveDirectHitIn</c>）：
    /// 它进入快照是为了让任务 08 的 Remaining Hit 阶段与 Shadow 比较能读到同一事实，
    /// <strong>不</strong>构成第二个权威状态，也<strong>不</strong>表示"可否被任何交互选中"。</item>
    /// <item>它<strong>不</strong>参与规范化哈希：状态本身已完整决定它，重复哈希只会制造
    /// "两份必须同步的真值"。哈希成员仍是 <see cref="State"/> 与两个区间端点。</item>
    /// </list>
    /// </summary>
    public sealed record UnitSnapshot(
        long UnitId,
        string DefinitionId,
        string FactionId,
        int X,
        int Y,
        int Facing,
        int HealthQ10,
        bool IsAlive,
        int AvailableAdrenaline,
        long AdrenalineCycleId,
        int State = 0,
        long StateStartTick = 0L,
        long StateEndTick = long.MaxValue,
        bool CanReceiveDirectHit = true)
    {
        /// <summary>强类型状态视图（与 <see cref="State"/> 同值）。</summary>
        public UnitState StateValue => (UnitState)State;
    }

    /// <summary>
    /// 持续效果快照（任务 04 扩展字段）。
    ///
    /// <see cref="EffectId"/> 是<strong>实例</strong>身份（唯一、进入哈希）；
    /// <see cref="SpecId"/> 是<strong>配置</strong>身份；<see cref="EndTick"/> 是权威结束边界
    /// （半开区间 <c>[AppliedAtTick, EndTick)</c>）。同 Tick 同序号不可能出现
    /// （二者同源分配），<see cref="EffectId"/> 只作为最终兜底排序键。
    /// </summary>
    public sealed record StatusEffectSnapshot(
        long EffectId,
        long UnitId,
        long AppliedAtTick,
        long EffectSequence,
        string SpecId = "",
        int DurationTicks = 0,
        long EndTick = 0L);

    /// <summary>窗口快照（任务 07 扩展预算/并发授权字段）。窗口不拥有动作，也不是结算边界。</summary>
    public sealed record TurnWindowSnapshot(
        long WindowId,
        long OwnerUnitId,
        long OpenedAtTick,
        int TotalBudgetTicks,
        int ReservedBudgetTicks,
        int SpentBudgetTicks,
        int AvailableBudgetTicks,
        bool IsOpen,
        bool IsAcceptingSubmissions,
        int CloseReason,
        IReadOnlyList<TurnWindowReservationSnapshot> Reservations);

    /// <summary>窗口账本里的一条按计划归属的预留明细（审计用；窗口不保存计划对象）。</summary>
    public sealed record TurnWindowReservationSnapshot(long ActionPlanId, int ReservedTicks);

    /// <summary>窗口管理器快照。<see cref="CurrentWindowId"/> = 0 表示当前没有打开窗口。</summary>
    public sealed record TurnWindowManagerSnapshot(
        long CurrentWindowId,
        long NextWindowTick,
        long NextWindowOrdinal,
        long LastClosedWindowId,
        IReadOnlyList<TurnWindowSnapshot> Windows)
    {
        public static TurnWindowManagerSnapshot None()
            => new TurnWindowManagerSnapshot(0L, -1L, 0L, 0L, Array.Empty<TurnWindowSnapshot>());
    }

    /// <summary>并发行动授权快照（任务 07 扩展版本化内部状态）。</summary>
    public sealed record ConcurrentActionSnapshot(bool HasActiveAuthorization, long WindowId, long PlayerUnitId)
    {
        public static ConcurrentActionSnapshot None() => new ConcurrentActionSnapshot(false, 0L, 0L);
    }

    /// <summary>单位肾上腺素账本快照（任务 07：Available + 个人周期 + 按计划归属的预留明细）。</summary>
    public sealed record AdrenalineLedgerSnapshot(
        long UnitId,
        int AvailableAdrenaline,
        long CycleId,
        IReadOnlyList<AdrenalineReservationSnapshot> Reservations,
        int ReservedTotal);

    /// <summary>一条反应肾上腺素预留快照（按 <see cref="ActionPlanId"/> 与个人周期归属）。</summary>
    public sealed record AdrenalineReservationSnapshot(long ActionPlanId, int ReservedAmount, long ReservationCycleId);

    /// <summary>
    /// 资源账本快照。<see cref="TurnBudgetAvailable"/>/<see cref="TurnBudgetReserved"/>/
    /// <see cref="TurnBudgetSpent"/> 是<strong>全部窗口</strong>的规范聚合（含已关闭窗口的审计账本），
    /// 因此恒有 <c>Reserved + Spent + Available == Σ Total</c>。
    /// </summary>
    public sealed record BattleResourceSnapshot(
        int MetaResource,
        long TurnBudgetAvailable,
        long TurnBudgetReserved,
        long TurnBudgetSpent,
        IReadOnlyList<AdrenalineLedgerSnapshot> AdrenalineLedgers = null)
    {
        public static BattleResourceSnapshot None(int metaResource)
            => new BattleResourceSnapshot(metaResource, 0L, 0L, 0L, Array.Empty<AdrenalineLedgerSnapshot>());
    }

    /// <summary>
    /// 未归档（活动）计划快照（任务 05 扩展 State/编辑/锁定 Tick/AutomaticDeferralCount/Timing/预算/TriggerBinding）。
    ///
    /// 边界（任务包「必须产出」4 的最后一段）：
    /// <list type="bullet">
    /// <item>它<strong>只</strong>包含非终态计划；进入终态的计划立即离开活动索引，
    /// 只留在<strong>历史记录数与摘要</strong>（<see cref="LogicSnapshot.TerminalPlanRecordCount"/> /
    /// <see cref="LogicSnapshot.TerminalPlanDigest"/>）与不可变归档里。</item>
    /// <item>逐 Tick 快照<strong>不</strong>遍历旧终态记录，因此它不会随战斗长度线性增长。</item>
    /// <item>全部字段参与规范化哈希：任一投影、修订或时序字段变化都改变摘要。</item>
    /// </list>
    /// </summary>
    public sealed record ActionPlanSnapshot(
        long ActionPlanId,
        long OwnerUnitId,
        string ActionSpecId = "",
        int Origin = 0,
        int State = 0,
        long SubmittedWindowId = 0L,
        long ReactionOpportunityId = 0L,
        long SourceThreatPlanId = 0L,
        long TriggerTick = 0L,
        long ResponseDeadlineTick = 0L,
        long StartTick = 0L,
        long EndTick = 0L,
        long ActiveStartTick = 0L,
        long ActiveEndTick = 0L,
        long LastRequestedStartTick = 0L,
        long CreatedAtTick = 0L,
        long LastEditedScheduleRevision = 0L,
        int AutomaticDeferralCount = 0,
        long LockedAtTick = -1L,
        long ImpactTick = 0L,
        int ResolvedWindupTicks = 0,
        int RecoveryTicks = 0,
        int ActiveTicks = 0,
        int ResolvedPathEdgeCount = 0,
        int ResolvedPathWeightUnits = 0,
        int ResolvedBaseStepTicks = 0,
        int MoveDurationTicks = 0,
        int ReactionWindupTicks = 0,
        int BudgetCostTicks = 0,
        int ReservedTurnBudgetTicks = 0,
        long PrimaryTargetUnitId = 0L,
        int PrimaryTargetRelation = 0,
        int Facing = 0,
        int DestinationX = 0,
        int DestinationY = 0,
        bool HasDestination = false,
        long TerminalTick = -1L,
        int TerminationReason = 0)
    {
        /// <summary>
        /// 从权威计划投影快照。它是<strong>唯一</strong>的字段映射处：
        /// 新增字段只在这里补，避免"两处各写一半"。
        /// </summary>
        public static ActionPlanSnapshot From(
            ProjectHero.Logic.Actions.ActionPlan plan,
            ProjectHero.Logic.Definitions.IFactionRelationResolver factions = null)
        {
            if (plan == null) return null;

            int relation = 0;
            if (factions != null && plan.PrimaryTargetUnitId.HasValue)
                relation = (int)factions.Classify(plan.OwnerUnitId, plan.PrimaryTargetUnitId.Value);

            return new ActionPlanSnapshot(
                plan.ActionPlanId.Value,
                plan.OwnerUnitId.Value,
                plan.ActionSpecId.Value ?? string.Empty,
                (int)plan.Origin,
                (int)plan.State,
                plan.SubmittedWindowId.HasValue ? plan.SubmittedWindowId.Value.Value : 0L,
                plan.ReactionOpportunityId.HasValue ? plan.ReactionOpportunityId.Value.Value : 0L,
                plan.SourceThreatPlanId.HasValue ? plan.SourceThreatPlanId.Value.Value : 0L,
                plan.TriggerTick,
                plan.TriggerBinding != null ? plan.TriggerBinding.ResponseDeadlineTick : 0L,
                plan.StartTick,
                plan.EndTick,
                plan.ActiveStartTick,
                plan.ActiveEndTick,
                plan.LastRequestedStartTick,
                plan.CreatedAtTick,
                plan.LastEditedScheduleRevision,
                plan.AutomaticDeferralCount,
                plan.LockedAtTick,
                plan.ImpactTick,
                plan.ResolvedWindupTicks,
                plan.RecoveryTicks,
                plan.ActiveTicks,
                plan.ResolvedPathEdgeCount,
                plan.ResolvedPathWeightUnits,
                plan.ResolvedBaseStepTicks,
                plan.MoveDurationTicks,
                plan.ReactionWindupTicks,
                plan.BudgetCostTicks,
                plan.ReservedTurnBudgetTicks,
                plan.PrimaryTargetUnitId.HasValue ? plan.PrimaryTargetUnitId.Value.Value : 0L,
                relation,
                (int)plan.Facing,
                plan.Destination.HasValue ? plan.Destination.Value.X : 0,
                plan.Destination.HasValue ? plan.Destination.Value.Y : 0,
                plan.Destination.HasValue,
                plan.TerminalTick,
                (int)plan.TerminationReason);
        }
    }

    /// <summary>
    /// 反应机会快照（任务 05/08 扩展逐选项截止 Tick、状态与 TriggerBinding）。
    ///
    /// <strong>已实现语义（以代码为唯一权威，2026-09-24 P2 更正）</strong>：
    /// <see cref="ReactionOpportunitySystem"/> 的<strong>全部</strong>机会（含已关闭者）都留在活动列表里，
    /// 因此快照会同时包含 <c>Open/Accepted</c> 与已关闭（<c>Expired/SourceCancelled/BattleEnded</c>）的记录，
    /// 每条都带状态、关闭原因、关闭 Tick 与绑定计划——这是刻意的审计面。
    /// 旧注释曾声称"已关闭的机会立即离开活动集合"，与实现不符，已删除。
    /// </summary>
    public sealed record ReactionOpportunitySnapshot(
        long ReactionOpportunityId,
        long DefenderUnitId,
        long SourceAttackPlanId,
        long TriggerTick,
        long TelegraphTick = 0L,
        int State = 0,
        string CloseReason = null,
        long ClosedAtTick = -1L,
        long BoundActionPlanId = 0L,
        string AcceptedActionSpecId = null,
        IReadOnlyList<ReactionOptionSnapshot> Options = null)
    {
        public int OpenOptionCount
        {
            get
            {
                if (Options == null) return 0;
                int count = 0;
                for (int i = 0; i < Options.Count; i++)
                {
                    if (Options[i] != null && Options[i].IsOpen) count++;
                }
                return count;
            }
        }
    }

    /// <summary>单个反应选项的快照（按 <c>ActionSpecId</c> Ordinal 升序）。</summary>
    public sealed record ReactionOptionSnapshot(
        string ActionSpecId,
        int ReactionType,
        long ResponseDeadlineTick,
        bool IsOpen,
        bool IsPublished,
        string OutcomeCode = null);


    /// <summary>ActorLane 快照（任务 05 扩展队列内容）。</summary>
    public sealed record ActorLaneSnapshot(long UnitId, int PendingPlanCount, bool Locked);

    /// <summary>
    /// 分通道伤害分量的快照（任务 08 快照契约；与 <c>DamageComponentSpec</c> 一一对应）。
    ///
    /// <see cref="RawAmountBits"/> 是 <c>DamageComponentSpec.RawAmount</c> 的
    /// <strong>二进制位</strong>（<c>BitConverter.SingleToInt32Bits</c>）：
    /// 归一化快照与哈希<strong>只搬运</strong>它，从不做算术或浮点比较，
    /// 因此浮点既不参与任何判定，也不可能因区域设置或舍入改变摘要。
    /// </summary>
    public sealed record DamageComponentSnapshot(string ChannelId, int RawAmountBits, int Tags);

    /// <summary>
    /// Intent 动量载荷的快照：<strong>方向</strong> + <strong>已量化的整数动量</strong>
    /// + 冲击 Profile 的审计引用（Profile 在量化完成后不再参与求解，但它是 Intent 的冻结输入事实，
    /// 因此一并进快照；缺失时为空串）。
    /// </summary>
    public sealed record IntentMomentumSnapshot(int Direction, int Units, string ImpactProfileId);

    /// <summary>
    /// Intent 快照（任务 08 扩展为完整载荷，替换首版的
    /// <c>(IntentSequence, SourceUnitId, TargetUnitId)</c> 三字段形态）。
    ///
    /// <strong>它是"本 Tick 冻结后的队列"的规范化投影</strong>：全部字段都是 Intent 自身携带的
    /// 不可变事实，不含任何"从当前状态重新推导"的量（不读动作速度、不推导命中时刻、不旋转 Pattern）。
    ///
    /// <list type="bullet">
    /// <item>排序键 = <see cref="IntentSequence"/>（全局唯一 ⇒ 全序，与插入顺序无关）；</item>
    /// <item><see cref="PrimaryTargetUnitId"/> 与 <see cref="SubmittedWindowId"/> 的"无"
    /// 写成 <c>0</c>（两者的 ID 分配器都保留 <c>0</c> 为无效值）；</item>
    /// <item><see cref="AreaPoints"/> 是已规范排序去重的三角点集，按 <c>(X, Y, T)</c> 升序进哈希；</item>
    /// <item><see cref="DamageComponents"/> 按 Intent 自身的规范存储顺序进哈希
    /// （与 <c>CombatIntent.CanonicalText</c> 同序）：<strong>不</strong>排序是因为
    /// 唯一可用的额外键是 <c>RawAmount</c> 的浮点值，而浮点比较被冻结规则禁止；
    /// 该顺序由定义（Authoring）唯一决定，不随任何运行时插入顺序变化；</item>
    /// <item>全部整数/字符串，<strong>无浮点</strong>（见 <see cref="DamageComponentSnapshot"/>）。</item>
    /// </list>
    /// </summary>
    public sealed record IntentSnapshot(
        long IntentSequence,
        long ActionPlanId,
        long OwnerUnitId,
        string ActionSpecId,
        int TargetPolicy,
        long PrimaryTargetUnitId,
        int AllowedTargetRelations,
        int Tags,
        int Facing,
        long ImpactTick,
        int InteractionPriority,
        int MomentumDirectionOffsetSteps,
        long SubmittedWindowId,
        IntentMomentumSnapshot Momentum,
        IReadOnlyList<TrianglePoint> AreaPoints,
        IReadOnlyList<DamageComponentSnapshot> DamageComponents);

    /// <summary>
    /// 规范化接触键的快照（<c>ContactKey</c> 的六个分量；
    /// 类型数值即 <c>ContactType</c> 的冻结整数值）。
    ///
    /// <strong>不含</strong>发现顺序、节点下标与 <c>CoveredBefore/After</c> 空间标记：
    /// 前者是遍历产物（排列相关，进哈希会把顺序带进摘要），
    /// 后者是求解器输入的空间事实，不属于"接触键集合"这一冻结结构面。
    ///
    /// <see cref="CompareTo"/> <strong>逐字段复刻</strong> <c>ContactKey.CompareTo</c> 的全序，
    /// 两者必须同时改；它是快照侧接触集合唯一允许的排序键。
    /// </summary>
    public sealed record ContactSnapshot(
        int Type,
        long FirstUnitId,
        long FirstPlanId,
        long SecondUnitId,
        long SecondPlanId,
        long TargetUnitId) : IComparable<ContactSnapshot>
    {
        /// <summary>规范全序：类型 → 双方单位 → 双方计划 → 目标单位（全部整数比较）。</summary>
        public int CompareTo(ContactSnapshot other)
        {
            if (other == null) return 1;

            int byType = Type.CompareTo(other.Type);
            if (byType != 0) return byType;

            int byFirstUnit = FirstUnitId.CompareTo(other.FirstUnitId);
            if (byFirstUnit != 0) return byFirstUnit;

            int byFirstPlan = FirstPlanId.CompareTo(other.FirstPlanId);
            if (byFirstPlan != 0) return byFirstPlan;

            int bySecondUnit = SecondUnitId.CompareTo(other.SecondUnitId);
            if (bySecondUnit != 0) return bySecondUnit;

            int bySecondPlan = SecondPlanId.CompareTo(other.SecondPlanId);
            if (bySecondPlan != 0) return bySecondPlan;

            return TargetUnitId.CompareTo(other.TargetUnitId);
        }
    }

    /// <summary>
    /// 冲突组快照（任务 08 快照契约）：冲突图的<strong>组划分</strong>。
    ///
    /// 这是"冻结输入"而不是"求解结果摘要"——同一冲突组的 Intent、接触边与目标集合
    /// 任意排列都必须得到逐字节相同的快照哈希，因此组划分必须进哈希；
    /// 而分阶段求解的聚合数值（RemainingHits 等）是已入哈希状态的纯函数，
    /// 只留在只读诊断面，重复哈希会制造"两份必须同步的真值"。
    ///
    /// 排序键：<see cref="GroupKey"/>（= 组内最小 <c>IntentSequence</c>）；
    /// <see cref="NodeIntentSequences"/>/<see cref="TargetUnitIds"/> 升序、
    /// <see cref="ContactKeys"/> 按 <see cref="ContactSnapshot.CompareTo"/> 升序。
    /// </summary>
    public sealed record ConflictGroupSnapshot(
        long GroupKey,
        IReadOnlyList<long> NodeIntentSequences,
        IReadOnlyList<ContactSnapshot> ContactKeys,
        IReadOnlyList<long> TargetUnitIds,
        int EdgeCount);

    /// <summary>MovementSegment 快照；以 (ActionPlanId, StepIndex) 定位，没有独立 SegmentId。</summary>
    public sealed record MovementSegmentSnapshot(long ActionPlanId, int StepIndex, int FromX, int FromY, int ToX, int ToY, long EndTick);

    /// <summary>Reservation 快照；归属于 ActionPlanId，没有独立 ReservationId。</summary>
    public sealed record ReservationSnapshot(long ActionPlanId, int X, int Y);

    /// <summary>AI 决策时钟快照（任务 09 扩展 nextThinkTick 等未来决策状态）。</summary>
    public sealed record AiControllerSnapshot(string ControllerId, long NextThinkTick);

    /// <summary>
    /// 规范化 <see cref="LogicSnapshot"/>（主方案 3.11.1 + 3.11.2）：覆盖<strong>全部会影响未来结果</strong>
    /// 的权威活动状态，加上固定大小的 <see cref="HistorySummary"/>。
    ///
    /// 重要边界：
    /// <list type="bullet">
    /// <item>它是<strong>观察/校验/哈希模型</strong>，<strong>不是</strong> <c>SimulationState</c>，
    /// <strong>不能</strong>作为 <c>BattleSimulation</c> 的构造或恢复输入：没有
    /// <c>Restore(...)</c>、没有中途存档/载入、没有任意 Tick 跳转、没有周期性检查点。</item>
    /// <item>哈希前每类集合都会按各自冻结的键<strong>重新规范排序</strong>，
    /// 因此构造时的插入顺序绝不影响摘要。</item>
    /// <item>不得为了 UI 方便直接把它交给 Controller：面向控制者的过滤视图是
    /// <see cref="DecisionSnapshot"/>（两者分型）。</item>
    /// </list>
    /// </summary>
    public sealed class LogicSnapshot
    {
        public LogicSnapshot(
            long tick,
            string rulesVersion,
            string battleDefinitionHash,
            string encounterId,
            BattleEndSnapshot battleEnd,
            IReadOnlyList<UnitSnapshot> units,
            IReadOnlyList<StatusEffectSnapshot> effects,
            TurnWindowManagerSnapshot windowManager,
            ConcurrentActionSnapshot concurrentAction,
            BattleResourceSnapshot resources,
            long scheduleRevision,
            IReadOnlyList<ActionPlanSnapshot> plans,
            IReadOnlyList<ReactionOpportunitySnapshot> reactionOpportunities,
            IReadOnlyList<ActorLaneSnapshot> actorLanes,
            IReadOnlyList<IntentSnapshot> intents,
            IReadOnlyList<MovementSegmentSnapshot> movementSegments,
            IReadOnlyList<ReservationSnapshot> reservations,
            IReadOnlyList<AiControllerSnapshot> aiControllers,
            CommandIngressRegistrySnapshot commandIngresses,
            RngSnapshot rng,
            long nextUnitId,
            long nextActionPlanId,
            long nextReactionOpportunityId,
            long nextWindowId,
            long nextEffectId,
            long nextCommandSequence,
            long nextIntentSequence,
            long nextResolutionSequence,
            long nextEventSequence,
            long nextEffectSequence,
            HistorySummary history,
            string commandSourcePriorityMappingVersion,
            long terminalPlanRecordCount = 0L,
            string terminalPlanDigest = null,
            long nextReactionOptionSequence = 0L,
            // 任务 08：冲突图的组划分与接触键集合。刻意放在签名末尾并带默认值——
            // 它们与 intents 同属"本 Tick 冻结输入"的快照面，但末尾追加可以让
            // 既有位置参数调用点（含 Unity 侧测试夹具）零改动继续编译。
            IReadOnlyList<ConflictGroupSnapshot> conflictGroups = null,
            IReadOnlyList<ContactSnapshot> contacts = null)
        {
            Tick = tick;
            RulesVersion = rulesVersion ?? string.Empty;
            BattleDefinitionHash = battleDefinitionHash ?? string.Empty;
            EncounterId = encounterId ?? string.Empty;
            BattleEnd = battleEnd ?? BattleEndSnapshot.Active();
            Units = Freeze(units);
            Effects = Freeze(effects);
            WindowManager = windowManager ?? TurnWindowManagerSnapshot.None();
            ConcurrentAction = concurrentAction ?? ConcurrentActionSnapshot.None();
            Resources = resources ?? BattleResourceSnapshot.None(0);
            ScheduleRevision = scheduleRevision;
            Plans = Freeze(plans);
            ReactionOpportunities = Freeze(reactionOpportunities);
            ActorLanes = Freeze(actorLanes);
            Intents = Freeze(intents);
            MovementSegments = Freeze(movementSegments);
            Reservations = Freeze(reservations);
            AiControllers = Freeze(aiControllers);
            CommandIngresses = commandIngresses;
            Rng = rng;
            NextUnitId = nextUnitId;
            NextActionPlanId = nextActionPlanId;
            NextReactionOpportunityId = nextReactionOpportunityId;
            NextWindowId = nextWindowId;
            NextEffectId = nextEffectId;
            NextCommandSequence = nextCommandSequence;
            NextIntentSequence = nextIntentSequence;
            NextResolutionSequence = nextResolutionSequence;
            NextEventSequence = nextEventSequence;
            NextEffectSequence = nextEffectSequence;
            History = history ?? HistorySummary.Empty();
            CommandSourcePriorityMappingVersion = commandSourcePriorityMappingVersion ?? string.Empty;
            TerminalPlanRecordCount = terminalPlanRecordCount;
            TerminalPlanDigest = terminalPlanDigest ?? CanonicalHash.ToHex(ReplayFormat.HistorySeedDigest());
            NextReactionOptionSequence = nextReactionOptionSequence;
            ConflictGroups = Freeze(conflictGroups);
            Contacts = Freeze(contacts);
        }

        /// <summary>只读冻结：拷贝成独立数组并包装为不可变集合，杜绝别名改写。</summary>
        private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<T>();
            var copy = new T[source.Count];
            for (int i = 0; i < source.Count; i++) copy[i] = source[i];
            return Array.AsReadOnly(copy);
        }

        /// <summary>
        /// 接触集合的<strong>唯一</strong>排序键：<see cref="ContactSnapshot.CompareTo"/>
        /// （逐字段复刻 <c>ContactKey.CompareTo</c>）。空值安全，便于对调用方直接构造的集合做规范排序。
        /// </summary>
        private static int CompareContacts(ContactSnapshot left, ContactSnapshot right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return -1;
            if (right == null) return 1;
            return left.CompareTo(right);
        }

        /// <summary>写一条接触键（六个整数分量，无字符串、无浮点、无发现顺序）。</summary>
        private static void WriteContact(CanonicalEncoder encoder, ContactSnapshot contact)
        {
            encoder.WriteInt32(contact == null ? 0 : contact.Type);
            encoder.WriteInt64(contact == null ? 0L : contact.FirstUnitId);
            encoder.WriteInt64(contact == null ? 0L : contact.FirstPlanId);
            encoder.WriteInt64(contact == null ? 0L : contact.SecondUnitId);
            encoder.WriteInt64(contact == null ? 0L : contact.SecondPlanId);
            encoder.WriteInt64(contact == null ? 0L : contact.TargetUnitId);
        }

        public long Tick { get; }
        public string RulesVersion { get; }
        public string BattleDefinitionHash { get; }
        public string EncounterId { get; }
        public BattleEndSnapshot BattleEnd { get; }
        public IReadOnlyList<UnitSnapshot> Units { get; }
        public IReadOnlyList<StatusEffectSnapshot> Effects { get; }
        public TurnWindowManagerSnapshot WindowManager { get; }
        public ConcurrentActionSnapshot ConcurrentAction { get; }
        public BattleResourceSnapshot Resources { get; }

        /// <summary>每次成功的命令排程事务或系统自动延期事务恰好 +1（含两者，不只是"玩家编辑"）。</summary>
        public long ScheduleRevision { get; }

        public IReadOnlyList<ActionPlanSnapshot> Plans { get; }
        public IReadOnlyList<ReactionOpportunitySnapshot> ReactionOpportunities { get; }
        public IReadOnlyList<ActorLaneSnapshot> ActorLanes { get; }
        public IReadOnlyList<IntentSnapshot> Intents { get; }
        public IReadOnlyList<MovementSegmentSnapshot> MovementSegments { get; }
        public IReadOnlyList<ReservationSnapshot> Reservations { get; }
        public IReadOnlyList<AiControllerSnapshot> AiControllers { get; }
        public CommandIngressRegistrySnapshot CommandIngresses { get; }
        public RngSnapshot Rng { get; }

        public long NextUnitId { get; }
        public long NextActionPlanId { get; }
        public long NextReactionOpportunityId { get; }

        /// <summary>
        /// 下一个将被分配的 <c>WindowId</c>（任务 03 冻结契约：所有"下一个 ID/Sequence"值
        /// 进入规范化快照与哈希）。窗口 ID 与其它实例 ID 共用同一个 <c>LogicIdGenerator</c>，
        /// 这里只是它的只读投影，<strong>不是</strong>第二份计数状态。
        /// </summary>
        public long NextWindowId { get; }
        public long NextEffectId { get; }
        public long NextCommandSequence { get; }
        public long NextIntentSequence { get; }
        public long NextResolutionSequence { get; }
        public long NextEventSequence { get; }
        public long NextEffectSequence { get; }
        public HistorySummary History { get; }
        public string CommandSourcePriorityMappingVersion { get; }

        /// <summary>
        /// 已进入终态并离开活动索引的计划累计数（任务包「必须产出」4：逐 Tick 快照使用
        /// <strong>历史记录数与摘要</strong>，不遍历旧终态记录）。
        /// </summary>
        public long TerminalPlanRecordCount { get; }

        /// <summary>终态计划历史的增量摘要（与 <see cref="TerminalPlanRecordCount"/> 成对使用）。</summary>
        public string TerminalPlanDigest { get; }

        /// <summary>下一个将被分配的 <c>ReactionOptionSequence</c>（反应选项内部排序键）。</summary>
        public long NextReactionOptionSequence { get; }

        /// <summary>
        /// 本 Tick 冻结队列所构成的冲突图<strong>组划分</strong>（任务 08；按 <see cref="ConflictGroupSnapshot.GroupKey"/> 升序）。
        ///
        /// 来源是阶段 10 构图产物 <c>BattleSimulation.ConflictGraph</c>（只读入口）：
        /// 构造期（Tick 0）、本 Tick 无到期 Intent、或本 Tick 构图失败时是<strong>空集合</strong>——
        /// "没有图"是合法状态，不是错误。
        /// </summary>
        public IReadOnlyList<ConflictGroupSnapshot> ConflictGroups { get; }

        /// <summary>
        /// 本 Tick 冲突图的<strong>全部接触键</strong>（任务 08；按 <see cref="ContactSnapshot.CompareTo"/> 升序、按键去重）。
        ///
        /// 与 <see cref="ConflictGroups"/> 同源同生命周期：没有图时为空集合。
        /// </summary>
        public IReadOnlyList<ContactSnapshot> Contacts { get; }

        public ulong ComputeHash() => ComputeHash(ReplayFormat.Version);

        /// <summary>
        /// 指定 <c>ReplayFormatVersion</c> 的哈希，只用于格式版本护栏测试：
        /// 同一份活动状态在两个格式版本下必须得到不同摘要。
        /// </summary>
        public ulong ComputeHash(int replayFormatVersion)
        {
            var encoder = new CanonicalEncoder(2048);
            encoder.BeginDomain(ReplayFormat.LogicSnapshotDomain);
            encoder.WriteInt32(replayFormatVersion);
            encoder.WriteInt64(Tick);
            encoder.WriteString(RulesVersion);
            encoder.WriteString(BattleDefinitionHash);
            encoder.WriteString(EncounterId);
            encoder.WriteString(CommandSourcePriorityMappingVersion);

            // —— 活动状态（每类按冻结键规范排序，插入顺序不影响摘要）——
            encoder.WriteBool(BattleEnd.IsEnded);
            encoder.WriteInt64(BattleEnd.EndedAtTick);
            encoder.WriteString(BattleEnd.ResultCode);

            var units = new List<UnitSnapshot>(Units);
            units.Sort((a, b) => a.UnitId.CompareTo(b.UnitId));
            encoder.WriteCount(units.Count);
            for (int i = 0; i < units.Count; i++)
            {
                UnitSnapshot unit = units[i];
                encoder.WriteInt64(unit.UnitId);
                encoder.WriteString(unit.DefinitionId);
                encoder.WriteString(unit.FactionId);
                encoder.WriteInt32(unit.X);
                encoder.WriteInt32(unit.Y);
                encoder.WriteInt32(unit.Facing);
                encoder.WriteInt32(unit.HealthQ10);
                encoder.WriteBool(unit.IsAlive);
                encoder.WriteInt32(unit.AvailableAdrenaline);
                encoder.WriteInt64(unit.AdrenalineCycleId);
                // 任务 04：状态是权威事实（半开区间端点必须一起哈希，
                // 否则"同一状态但不同剩余时长"会撞成同一摘要）。
                encoder.WriteInt32(unit.State);
                encoder.WriteInt64(unit.StateStartTick);
                encoder.WriteInt64(unit.StateEndTick);
            }

            var effects = new List<StatusEffectSnapshot>(Effects);
            effects.Sort((a, b) =>
            {
                int byUnit = a.UnitId.CompareTo(b.UnitId);
                if (byUnit != 0) return byUnit;
                int byApplied = a.AppliedAtTick.CompareTo(b.AppliedAtTick);
                if (byApplied != 0) return byApplied;
                int bySequence = a.EffectSequence.CompareTo(b.EffectSequence);
                return bySequence != 0 ? bySequence : a.EffectId.CompareTo(b.EffectId);
            });
            encoder.WriteCount(effects.Count);
            for (int i = 0; i < effects.Count; i++)
            {
                encoder.WriteInt64(effects[i].EffectId);
                encoder.WriteInt64(effects[i].UnitId);
                encoder.WriteInt64(effects[i].AppliedAtTick);
                encoder.WriteInt64(effects[i].EffectSequence);
                encoder.WriteString(effects[i].SpecId);
                encoder.WriteInt32(effects[i].DurationTicks);
                encoder.WriteInt64(effects[i].EndTick);
            }

            encoder.WriteInt64(WindowManager.CurrentWindowId);
            encoder.WriteInt64(WindowManager.NextWindowTick);
            encoder.WriteInt64(WindowManager.NextWindowOrdinal);
            encoder.WriteInt64(WindowManager.LastClosedWindowId);
            var windows = new List<TurnWindowSnapshot>(WindowManager.Windows);
            windows.Sort((a, b) => a.WindowId.CompareTo(b.WindowId));
            encoder.WriteCount(windows.Count);
            for (int i = 0; i < windows.Count; i++)
            {
                TurnWindowSnapshot window = windows[i];
                encoder.WriteInt64(window.WindowId);
                encoder.WriteInt64(window.OwnerUnitId);
                encoder.WriteInt64(window.OpenedAtTick);
                encoder.WriteInt32(window.TotalBudgetTicks);
                encoder.WriteInt32(window.ReservedBudgetTicks);
                encoder.WriteInt32(window.SpentBudgetTicks);
                encoder.WriteInt32(window.AvailableBudgetTicks);
                encoder.WriteBool(window.IsOpen);
                encoder.WriteBool(window.IsAcceptingSubmissions);
                encoder.WriteInt32(window.CloseReason);
                // 账本明细按 ActionPlanId 升序（窗口不持有计划对象，只持有这份可审计预留明细）。
                var windowReservations = new List<TurnWindowReservationSnapshot>(
                    window.Reservations ?? Array.Empty<TurnWindowReservationSnapshot>());
                windowReservations.Sort((a, b) => a.ActionPlanId.CompareTo(b.ActionPlanId));
                encoder.WriteCount(windowReservations.Count);
                for (int r = 0; r < windowReservations.Count; r++)
                {
                    encoder.WriteInt64(windowReservations[r].ActionPlanId);
                    encoder.WriteInt32(windowReservations[r].ReservedTicks);
                }
            }

            encoder.WriteBool(ConcurrentAction.HasActiveAuthorization);
            encoder.WriteInt64(ConcurrentAction.WindowId);
            encoder.WriteInt64(ConcurrentAction.PlayerUnitId);

            encoder.WriteInt32(Resources.MetaResource);
            encoder.WriteInt64(Resources.TurnBudgetAvailable);
            encoder.WriteInt64(Resources.TurnBudgetReserved);
            encoder.WriteInt64(Resources.TurnBudgetSpent);
            // 肾上腺素账本：Available、每条反应预留与 ReservationCycleId 全部进入规范快照。
            var ledgers = new List<AdrenalineLedgerSnapshot>(
                Resources.AdrenalineLedgers ?? Array.Empty<AdrenalineLedgerSnapshot>());
            ledgers.Sort((a, b) => a.UnitId.CompareTo(b.UnitId));
            encoder.WriteCount(ledgers.Count);
            for (int i = 0; i < ledgers.Count; i++)
            {
                AdrenalineLedgerSnapshot ledger = ledgers[i];
                encoder.WriteInt64(ledger.UnitId);
                encoder.WriteInt32(ledger.AvailableAdrenaline);
                encoder.WriteInt64(ledger.CycleId);
                encoder.WriteInt32(ledger.ReservedTotal);
                var adrenalineReservations = new List<AdrenalineReservationSnapshot>(
                    ledger.Reservations ?? Array.Empty<AdrenalineReservationSnapshot>());
                adrenalineReservations.Sort((a, b) => a.ActionPlanId.CompareTo(b.ActionPlanId));
                encoder.WriteCount(adrenalineReservations.Count);
                for (int r = 0; r < adrenalineReservations.Count; r++)
                {
                    encoder.WriteInt64(adrenalineReservations[r].ActionPlanId);
                    encoder.WriteInt32(adrenalineReservations[r].ReservedAmount);
                    encoder.WriteInt64(adrenalineReservations[r].ReservationCycleId);
                }
            }

            encoder.WriteInt64(ScheduleRevision);

            var plans = new List<ActionPlanSnapshot>(Plans);
            plans.Sort((a, b) => a.ActionPlanId.CompareTo(b.ActionPlanId));
            encoder.WriteCount(plans.Count);
            for (int i = 0; i < plans.Count; i++)
            {
                ActionPlanSnapshot plan = plans[i];
                encoder.WriteInt64(plan.ActionPlanId);
                encoder.WriteInt64(plan.OwnerUnitId);
                encoder.WriteString(plan.ActionSpecId);
                encoder.WriteInt32(plan.Origin);
                encoder.WriteInt32(plan.State);
                encoder.WriteInt64(plan.SubmittedWindowId);
                encoder.WriteInt64(plan.ReactionOpportunityId);
                encoder.WriteInt64(plan.SourceThreatPlanId);
                encoder.WriteInt64(plan.TriggerTick);
                encoder.WriteInt64(plan.ResponseDeadlineTick);
                // 权威投影（半开区间 [StartTick, EndTick)）与 Guard Active 区间
                encoder.WriteInt64(plan.StartTick);
                encoder.WriteInt64(plan.EndTick);
                encoder.WriteInt64(plan.ActiveStartTick);
                encoder.WriteInt64(plan.ActiveEndTick);
                encoder.WriteInt64(plan.LastRequestedStartTick);
                // 创建/编辑/锁定/延期记账
                encoder.WriteInt64(plan.CreatedAtTick);
                encoder.WriteInt64(plan.LastEditedScheduleRevision);
                encoder.WriteInt32(plan.AutomaticDeferralCount);
                encoder.WriteInt64(plan.LockedAtTick);
                // 已解析的相对时序（一次性采样结果）
                encoder.WriteInt64(plan.ImpactTick);
                encoder.WriteInt32(plan.ResolvedWindupTicks);
                encoder.WriteInt32(plan.RecoveryTicks);
                encoder.WriteInt32(plan.ActiveTicks);
                encoder.WriteInt32(plan.ResolvedPathEdgeCount);
                encoder.WriteInt32(plan.ResolvedPathWeightUnits);
                encoder.WriteInt32(plan.ResolvedBaseStepTicks);
                encoder.WriteInt32(plan.MoveDurationTicks);
                encoder.WriteInt32(plan.ReactionWindupTicks);
                // 预算与预留
                encoder.WriteInt32(plan.BudgetCostTicks);
                encoder.WriteInt32(plan.ReservedTurnBudgetTicks);
                // 目标、朝向与目的格
                encoder.WriteInt64(plan.PrimaryTargetUnitId);
                encoder.WriteInt32(plan.PrimaryTargetRelation);
                encoder.WriteInt32(plan.Facing);
                encoder.WriteInt32(plan.DestinationX);
                encoder.WriteInt32(plan.DestinationY);
                encoder.WriteBool(plan.HasDestination);
                // 终态字段（活动索引里应当恒为初值；写进去是为了让"不该出现却出现"可失败）
                encoder.WriteInt64(plan.TerminalTick);
                encoder.WriteInt32(plan.TerminationReason);
            }

            // 终态计划历史：只写记录数与摘要，绝不展开旧终态记录。
            encoder.WriteInt64(TerminalPlanRecordCount);
            encoder.WriteString(TerminalPlanDigest);

            var opportunities = new List<ReactionOpportunitySnapshot>(ReactionOpportunities);
            opportunities.Sort((a, b) => a.ReactionOpportunityId.CompareTo(b.ReactionOpportunityId));
            encoder.WriteCount(opportunities.Count);
            for (int i = 0; i < opportunities.Count; i++)
            {
                ReactionOpportunitySnapshot opportunity = opportunities[i];
                encoder.WriteInt64(opportunity.ReactionOpportunityId);
                encoder.WriteInt64(opportunity.DefenderUnitId);
                encoder.WriteInt64(opportunity.SourceAttackPlanId);
                encoder.WriteInt64(opportunity.TriggerTick);
                encoder.WriteInt64(opportunity.TelegraphTick);
                encoder.WriteInt32(opportunity.State);
                encoder.WriteString(opportunity.CloseReason);
                encoder.WriteInt64(opportunity.ClosedAtTick);
                encoder.WriteInt64(opportunity.BoundActionPlanId);
                encoder.WriteString(opportunity.AcceptedActionSpecId);

                var options = new List<ReactionOptionSnapshot>(
                    opportunity.Options ?? (IReadOnlyList<ReactionOptionSnapshot>)Array.Empty<ReactionOptionSnapshot>());
                options.Sort((a, b) => string.CompareOrdinal(a.ActionSpecId, b.ActionSpecId));
                encoder.WriteCount(options.Count);
                for (int o = 0; o < options.Count; o++)
                {
                    encoder.WriteString(options[o].ActionSpecId);
                    encoder.WriteInt32(options[o].ReactionType);
                    encoder.WriteInt64(options[o].ResponseDeadlineTick);
                    encoder.WriteBool(options[o].IsOpen);
                    encoder.WriteBool(options[o].IsPublished);
                    encoder.WriteString(options[o].OutcomeCode);
                }
            }

            encoder.WriteInt64(NextReactionOptionSequence);

            var lanes = new List<ActorLaneSnapshot>(ActorLanes);
            lanes.Sort((a, b) => a.UnitId.CompareTo(b.UnitId));
            encoder.WriteCount(lanes.Count);
            for (int i = 0; i < lanes.Count; i++)
            {
                encoder.WriteInt64(lanes[i].UnitId);
                encoder.WriteInt32(lanes[i].PendingPlanCount);
                encoder.WriteBool(lanes[i].Locked);
            }

            // —— 本 Tick 冻结队列的完整载荷（任务 08）：排序键 = IntentSequence（全局唯一 ⇒ 全序）——
            var intents = new List<IntentSnapshot>(Intents);
            intents.Sort((a, b) => a.IntentSequence.CompareTo(b.IntentSequence));
            encoder.WriteCount(intents.Count);
            for (int i = 0; i < intents.Count; i++)
            {
                IntentSnapshot intent = intents[i];
                encoder.WriteInt64(intent.IntentSequence);
                encoder.WriteInt64(intent.ActionPlanId);
                encoder.WriteInt64(intent.OwnerUnitId);
                encoder.WriteString(intent.ActionSpecId);
                encoder.WriteInt32(intent.TargetPolicy);
                encoder.WriteInt64(intent.PrimaryTargetUnitId);
                encoder.WriteInt32(intent.AllowedTargetRelations);
                encoder.WriteInt32(intent.Tags);
                encoder.WriteInt32(intent.Facing);
                encoder.WriteInt64(intent.ImpactTick);
                encoder.WriteInt32(intent.InteractionPriority);
                encoder.WriteInt32(intent.MomentumDirectionOffsetSteps);
                encoder.WriteInt64(intent.SubmittedWindowId);

                // 动量载荷：方向 + 已量化整数动量 + 冲击 Profile 审计引用（无浮点）。
                IntentMomentumSnapshot momentum = intent.Momentum;
                encoder.WriteInt32(momentum == null ? 0 : momentum.Direction);
                encoder.WriteInt32(momentum == null ? 0 : momentum.Units);
                encoder.WriteString(momentum == null ? string.Empty : momentum.ImpactProfileId);

                // 区域点集：按 (X, Y, T) 升序（唯一稳定键），写前重新规范排序。
                var areaPoints = new List<TrianglePoint>(
                    intent.AreaPoints ?? (IReadOnlyList<TrianglePoint>)Array.Empty<TrianglePoint>());
                areaPoints.Sort((a, b) => a.CompareTo(b));
                encoder.WriteCount(areaPoints.Count);
                for (int p = 0; p < areaPoints.Count; p++)
                {
                    encoder.WriteInt32(areaPoints[p].X);
                    encoder.WriteInt32(areaPoints[p].Y);
                    encoder.WriteInt32(areaPoints[p].T);
                }

                // 分通道伤害：浮点只按二进制位写入，绝不参与算术或比较。
                // 顺序 = Intent 自身的规范存储顺序（不排序，理由见 IntentSnapshot 文档：
                // 唯一可用的额外键是 RawAmount 的浮点值，而浮点比较被冻结规则禁止）。
                var damageComponents = new List<DamageComponentSnapshot>(
                    intent.DamageComponents ?? (IReadOnlyList<DamageComponentSnapshot>)Array.Empty<DamageComponentSnapshot>());
                encoder.WriteCount(damageComponents.Count);
                for (int c = 0; c < damageComponents.Count; c++)
                {
                    DamageComponentSnapshot component = damageComponents[c];
                    encoder.WriteString(component == null ? string.Empty : component.ChannelId);
                    encoder.WriteInt32(component == null ? 0 : component.RawAmountBits);
                    encoder.WriteInt32(component == null ? 0 : component.Tags);
                }
            }

            // —— 冲突图的组划分（任务 08）：组键 → 节点集合 → 接触键 → 目标集合 → 边数 ——
            // 这是"同组任意排列 ⇒ 快照哈希一致"所依赖的冻结结构面；求解聚合数值不进这里。
            var conflictGroups = new List<ConflictGroupSnapshot>(ConflictGroups);
            conflictGroups.Sort((a, b) => a.GroupKey.CompareTo(b.GroupKey));
            encoder.WriteCount(conflictGroups.Count);
            for (int g = 0; g < conflictGroups.Count; g++)
            {
                ConflictGroupSnapshot group = conflictGroups[g];
                encoder.WriteInt64(group.GroupKey);

                var groupSequences = new List<long>(
                    group.NodeIntentSequences ?? (IReadOnlyList<long>)Array.Empty<long>());
                groupSequences.Sort();
                encoder.WriteCount(groupSequences.Count);
                for (int n = 0; n < groupSequences.Count; n++) encoder.WriteInt64(groupSequences[n]);

                var groupContacts = new List<ContactSnapshot>(
                    group.ContactKeys ?? (IReadOnlyList<ContactSnapshot>)Array.Empty<ContactSnapshot>());
                groupContacts.Sort((a, b) => CompareContacts(a, b));
                encoder.WriteCount(groupContacts.Count);
                for (int c = 0; c < groupContacts.Count; c++) WriteContact(encoder, groupContacts[c]);

                var groupTargets = new List<long>(
                    group.TargetUnitIds ?? (IReadOnlyList<long>)Array.Empty<long>());
                groupTargets.Sort();
                encoder.WriteCount(groupTargets.Count);
                for (int t = 0; t < groupTargets.Count; t++) encoder.WriteInt64(groupTargets[t]);

                encoder.WriteInt32(group.EdgeCount);
            }

            // —— 冲突图的全部接触键（同一冻结产物的另一投影面）——
            var contacts = new List<ContactSnapshot>(Contacts);
            contacts.Sort((a, b) => CompareContacts(a, b));
            encoder.WriteCount(contacts.Count);
            for (int i = 0; i < contacts.Count; i++) WriteContact(encoder, contacts[i]);

            var segments = new List<MovementSegmentSnapshot>(MovementSegments);
            segments.Sort((a, b) =>
            {
                int byPlan = a.ActionPlanId.CompareTo(b.ActionPlanId);
                return byPlan != 0 ? byPlan : a.StepIndex.CompareTo(b.StepIndex);
            });
            encoder.WriteCount(segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                encoder.WriteInt64(segments[i].ActionPlanId);
                encoder.WriteInt32(segments[i].StepIndex);
                encoder.WriteInt32(segments[i].FromX);
                encoder.WriteInt32(segments[i].FromY);
                encoder.WriteInt32(segments[i].ToX);
                encoder.WriteInt32(segments[i].ToY);
                encoder.WriteInt64(segments[i].EndTick);
            }

            var reservations = new List<ReservationSnapshot>(Reservations);
            reservations.Sort((a, b) =>
            {
                int byPlan = a.ActionPlanId.CompareTo(b.ActionPlanId);
                if (byPlan != 0) return byPlan;
                int byX = a.X.CompareTo(b.X);
                return byX != 0 ? byX : a.Y.CompareTo(b.Y);
            });
            encoder.WriteCount(reservations.Count);
            for (int i = 0; i < reservations.Count; i++)
            {
                encoder.WriteInt64(reservations[i].ActionPlanId);
                encoder.WriteInt32(reservations[i].X);
                encoder.WriteInt32(reservations[i].Y);
            }

            var aiControllers = new List<AiControllerSnapshot>(AiControllers);
            aiControllers.Sort((a, b) => string.CompareOrdinal(a.ControllerId, b.ControllerId));
            encoder.WriteCount(aiControllers.Count);
            for (int i = 0; i < aiControllers.Count; i++)
            {
                encoder.WriteString(aiControllers[i].ControllerId);
                encoder.WriteInt64(aiControllers[i].NextThinkTick);
            }

            // —— 命令入口（注册、每入口下一个/已冻结 ProducerOrdinal、未来 Tick 桶）——
            // 入口按 SourcePriority -> ControllerId(Ordinal) -> RegistrationKind 规范排序，
            // 桶按 TargetTick 升序；插入顺序不影响摘要。
            CommandIngressRegistrySnapshot ingresses = CommandIngresses;
            var ingressEntries = new List<CommandIngressEntrySnapshot>(
                ingresses?.Entries ?? (IReadOnlyList<CommandIngressEntrySnapshot>)Array.Empty<CommandIngressEntrySnapshot>());
            ingressEntries.Sort((a, b) =>
            {
                int byPriority = a.SourcePriority.CompareTo(b.SourcePriority);
                if (byPriority != 0) return byPriority;
                int byController = string.CompareOrdinal(a.ControllerId, b.ControllerId);
                return byController != 0 ? byController : string.CompareOrdinal(a.RegistrationKind, b.RegistrationKind);
            });
            encoder.WriteCount(ingressEntries.Count);
            for (int i = 0; i < ingressEntries.Count; i++)
            {
                CommandIngressEntrySnapshot entry = ingressEntries[i];
                encoder.WriteString(entry.ControllerId);
                encoder.WriteInt32(entry.SourceKind);
                encoder.WriteInt32(entry.SourcePriority);
                encoder.WriteString(entry.RegistrationKind);
                encoder.WriteInt64(entry.NextProducerOrdinal);
                encoder.WriteInt64(entry.FrozenProducerOrdinal);
            }

            var ingressBuckets = new List<CommandIngressTickBucketSnapshot>(
                ingresses?.FutureBuckets ?? (IReadOnlyList<CommandIngressTickBucketSnapshot>)Array.Empty<CommandIngressTickBucketSnapshot>());
            ingressBuckets.Sort((a, b) => a.TargetTick.CompareTo(b.TargetTick));
            encoder.WriteCount(ingressBuckets.Count);
            for (int i = 0; i < ingressBuckets.Count; i++)
            {
                encoder.WriteInt64(ingressBuckets[i].TargetTick);
                encoder.WriteInt32(ingressBuckets[i].PendingCount);
            }
            encoder.WriteInt64(ingresses?.FrozenThroughTick ?? -1L);
            encoder.WriteInt32(ingresses?.PendingRejectionCount ?? 0);

            // —— RNG ——
            encoder.WriteInt32(Rng?.AlgorithmVersion ?? 0);
            encoder.WriteUInt64(Rng?.State ?? 0UL);

            // —— 全部 ID/Sequence 的下一个值 ——
            encoder.WriteInt64(NextUnitId);
            encoder.WriteInt64(NextActionPlanId);
            encoder.WriteInt64(NextReactionOpportunityId);
            encoder.WriteInt64(NextWindowId);
            encoder.WriteInt64(NextEffectId);
            encoder.WriteInt64(NextCommandSequence);
            encoder.WriteInt64(NextIntentSequence);
            encoder.WriteInt64(NextResolutionSequence);
            encoder.WriteInt64(NextEventSequence);
            encoder.WriteInt64(NextEffectSequence);

            // —— 历史摘要（只哈希 RecordCount 与摘要值，不展开明细）——
            encoder.WriteInt64(History.RecordCount);
            encoder.WriteUInt64(History.DigestValue);

            return encoder.ToDigest();
        }

        /// <summary>规范摘要（16 位小写十六进制）。</summary>
        public string ComputeHashHex() => CanonicalHash.ToHex(ComputeHash());

        public string ComputeHashHex(int replayFormatVersion) => CanonicalHash.ToHex(ComputeHash(replayFormatVersion));
    }
}
