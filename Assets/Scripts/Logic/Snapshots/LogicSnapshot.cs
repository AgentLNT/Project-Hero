using System;
using System.Collections.Generic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
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
        int BudgetTicks,
        bool CloseRequested);

    /// <summary>窗口管理器快照。<see cref="CurrentWindowId"/> = 0 表示当前没有打开窗口。</summary>
    public sealed record TurnWindowManagerSnapshot(
        long CurrentWindowId,
        long NextWindowTick,
        IReadOnlyList<TurnWindowSnapshot> Windows)
    {
        public static TurnWindowManagerSnapshot None() => new TurnWindowManagerSnapshot(0L, -1L, Array.Empty<TurnWindowSnapshot>());
    }

    /// <summary>并发行动授权快照（任务 07 扩展版本化内部状态）。</summary>
    public sealed record ConcurrentActionSnapshot(bool HasActiveAuthorization, long WindowId, long PlayerUnitId)
    {
        public static ConcurrentActionSnapshot None() => new ConcurrentActionSnapshot(false, 0L, 0L);
    }

    /// <summary>资源账本快照（任务 07 接入 TurnBudget / 肾上腺素账本明细）。</summary>
    public sealed record BattleResourceSnapshot(int MetaResource, long TurnBudgetAvailable, long TurnBudgetReserved, long TurnBudgetSpent)
    {
        public static BattleResourceSnapshot None(int metaResource)
            => new BattleResourceSnapshot(metaResource, 0L, 0L, 0L);
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

    /// <summary>Intent 快照（任务 08 扩展载荷；排序键 Tick -&gt; Priority -&gt; IntentSequence）。</summary>
    public sealed record IntentSnapshot(long IntentSequence, long SourceUnitId, long TargetUnitId);

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
            long nextReactionOptionSequence = 0L)
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
        }

        /// <summary>只读冻结：拷贝成独立数组并包装为不可变集合，杜绝别名改写。</summary>
        private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<T>();
            var copy = new T[source.Count];
            for (int i = 0; i < source.Count; i++) copy[i] = source[i];
            return Array.AsReadOnly(copy);
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
            var windows = new List<TurnWindowSnapshot>(WindowManager.Windows);
            windows.Sort((a, b) => a.WindowId.CompareTo(b.WindowId));
            encoder.WriteCount(windows.Count);
            for (int i = 0; i < windows.Count; i++)
            {
                encoder.WriteInt64(windows[i].WindowId);
                encoder.WriteInt64(windows[i].OwnerUnitId);
                encoder.WriteInt64(windows[i].OpenedAtTick);
                encoder.WriteInt32(windows[i].BudgetTicks);
                encoder.WriteBool(windows[i].CloseRequested);
            }

            encoder.WriteBool(ConcurrentAction.HasActiveAuthorization);
            encoder.WriteInt64(ConcurrentAction.WindowId);
            encoder.WriteInt64(ConcurrentAction.PlayerUnitId);

            encoder.WriteInt32(Resources.MetaResource);
            encoder.WriteInt64(Resources.TurnBudgetAvailable);
            encoder.WriteInt64(Resources.TurnBudgetReserved);
            encoder.WriteInt64(Resources.TurnBudgetSpent);

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

            var intents = new List<IntentSnapshot>(Intents);
            intents.Sort((a, b) => a.IntentSequence.CompareTo(b.IntentSequence));
            encoder.WriteCount(intents.Count);
            for (int i = 0; i < intents.Count; i++)
            {
                encoder.WriteInt64(intents[i].IntentSequence);
                encoder.WriteInt64(intents[i].SourceUnitId);
                encoder.WriteInt64(intents[i].TargetUnitId);
            }

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
