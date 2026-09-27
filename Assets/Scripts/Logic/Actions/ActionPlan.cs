using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Actions
{
    /// <summary>
    /// 计划生命周期状态（任务包「必须产出」1）。<strong>普通计划只有一个对象</strong>：
    /// 创建即 <see cref="Editable"/>，启动门禁通过时在<strong>同一个原子提交</strong>中建立
    /// <see cref="Locked"/> 边界并立即进入 <see cref="Running"/>。
    ///
    /// <list type="bullet">
    /// <item><see cref="Editable"/>：普通计划的唯一可编辑状态；可由权威 ScheduleEditor
    /// 放置、移动、删除、排序并只向右避让。</item>
    /// <item><see cref="Locked"/>：<strong>固定反应区间</strong>的静止状态（反应计划接受后直接
    /// Locked 并一直保持到固定 <c>StartTick</c>）。普通计划在同一提交内经过它，
    /// 因此<strong>阶段边界上永远观察不到「普通计划 Locked 但未 Running」</strong>。</item>
    /// <item><see cref="Running"/>：已启动；产出 Intent、占据 Lane 区间。</item>
    /// <item><see cref="Completed"/>：自然终态（到达 <c>EndTick</c>）。</item>
    /// <item><see cref="Terminated"/>：带原因的非自然终态，只能经统一终态协调器进入。</item>
    /// </list>
    /// </summary>
    public enum ActionPlanState
    {
        Editable = 0,
        Locked = 1,
        Running = 2,
        Completed = 3,
        Terminated = 4
    }

    /// <summary>计划来源：普通滚动排程 或 已公开 ReactionOpportunity 绑定反应。</summary>
    public enum ActionPlanOrigin
    {
        Ordinary = 0,
        Reaction = 1
    }

    /// <summary>
    /// 独立的动作终止原因（任务包「必须产出」1）。
    /// <see cref="Completed"/> 可以没有终止原因；<see cref="ActionPlanState.Terminated"/>
    /// 必须把明确原因写入事件与快照。
    /// </summary>
    public enum ActionTerminationReason
    {
        None = 0,
        CancelledByCommand = 1,
        TargetInvalid = 2,
        ActorUnavailableAtStart = 3,
        AutoDeferralLimitExceeded = 4,
        SourceThreatCancelled = 5,
        InterruptedByClash = 6,
        InterruptedByIntercept = 7,
        InterruptedByControl = 8,
        ReservationPreemptedByForcedDisplacement = 9,
        MovementOriginInvalidated = 10,
        MovementOriginInvalidatedByDodge = 11,
        OwnerDied = 12,
        BattleEnded = 13
    }

    /// <summary>
    /// 终止原因的稳定字符串码（进入事件与快照；不得改名或复用）。
    /// </summary>
    public static class ActionTerminationReasons
    {
        public const string CancelledByCommand = "ACTION_TERMINATION_CANCELLED_BY_COMMAND";
        public const string TargetInvalid = "ACTION_TERMINATION_TARGET_INVALID";
        public const string ActorUnavailableAtStart = "ACTION_TERMINATION_ACTOR_UNAVAILABLE_AT_START";
        public const string AutoDeferralLimitExceeded = "ACTION_TERMINATION_AUTO_DEFERRAL_LIMIT_EXCEEDED";
        public const string SourceThreatCancelled = "ACTION_TERMINATION_SOURCE_THREAT_CANCELLED";
        public const string InterruptedByClash = "ACTION_TERMINATION_INTERRUPTED_BY_CLASH";
        public const string InterruptedByIntercept = "ACTION_TERMINATION_INTERRUPTED_BY_INTERCEPT";
        public const string InterruptedByControl = "ACTION_TERMINATION_INTERRUPTED_BY_CONTROL";
        public const string ReservationPreemptedByForcedDisplacement =
            "ACTION_TERMINATION_RESERVATION_PREEMPTED_BY_FORCED_DISPLACEMENT";
        public const string MovementOriginInvalidated = "ACTION_TERMINATION_MOVEMENT_ORIGIN_INVALIDATED";
        public const string MovementOriginInvalidatedByDodge =
            "ACTION_TERMINATION_MOVEMENT_ORIGIN_INVALIDATED_BY_DODGE";
        public const string OwnerDied = "ACTION_TERMINATION_OWNER_DIED";
        public const string BattleEnded = "ACTION_TERMINATION_BATTLE_ENDED";

        /// <summary>原因 → 稳定字符串码（<see cref="ActionTerminationReason.None"/> 返回 null）。</summary>
        public static string CodeOf(ActionTerminationReason reason)
        {
            switch (reason)
            {
                case ActionTerminationReason.None: return null;
                case ActionTerminationReason.CancelledByCommand: return CancelledByCommand;
                case ActionTerminationReason.TargetInvalid: return TargetInvalid;
                case ActionTerminationReason.ActorUnavailableAtStart: return ActorUnavailableAtStart;
                case ActionTerminationReason.AutoDeferralLimitExceeded: return AutoDeferralLimitExceeded;
                case ActionTerminationReason.SourceThreatCancelled: return SourceThreatCancelled;
                case ActionTerminationReason.InterruptedByClash: return InterruptedByClash;
                case ActionTerminationReason.InterruptedByIntercept: return InterruptedByIntercept;
                case ActionTerminationReason.InterruptedByControl: return InterruptedByControl;
                case ActionTerminationReason.ReservationPreemptedByForcedDisplacement:
                    return ReservationPreemptedByForcedDisplacement;
                case ActionTerminationReason.MovementOriginInvalidated: return MovementOriginInvalidated;
                case ActionTerminationReason.MovementOriginInvalidatedByDodge: return MovementOriginInvalidatedByDodge;
                case ActionTerminationReason.OwnerDied: return OwnerDied;
                case ActionTerminationReason.BattleEnded: return BattleEnded;
                default:
                    throw new LogicDefinitionException(ActionPlanCodes.ACTION_TERMINATION_REASON_UNKNOWN,
                        ((int)reason).ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>原因全集（诊断与测试用；顺序即文档顺序）。</summary>
        public static readonly IReadOnlyList<ActionTerminationReason> All = new[]
        {
            ActionTerminationReason.CancelledByCommand,
            ActionTerminationReason.TargetInvalid,
            ActionTerminationReason.ActorUnavailableAtStart,
            ActionTerminationReason.AutoDeferralLimitExceeded,
            ActionTerminationReason.SourceThreatCancelled,
            ActionTerminationReason.InterruptedByClash,
            ActionTerminationReason.InterruptedByIntercept,
            ActionTerminationReason.InterruptedByControl,
            ActionTerminationReason.ReservationPreemptedByForcedDisplacement,
            ActionTerminationReason.MovementOriginInvalidated,
            ActionTerminationReason.MovementOriginInvalidatedByDodge,
            ActionTerminationReason.OwnerDied,
            ActionTerminationReason.BattleEnded
        };

        /// <summary>
        /// 冻结优先级（数字越小越优先）。同一计划同时满足多个强制位移原因时取优先级最高者：
        /// <c>MovementOriginInvalidated</c> 优先于
        /// <c>ReservationPreemptedByForcedDisplacement</c>。
        /// </summary>
        public static int PriorityOf(ActionTerminationReason reason)
        {
            switch (reason)
            {
                case ActionTerminationReason.MovementOriginInvalidated: return 0;
                case ActionTerminationReason.ReservationPreemptedByForcedDisplacement: return 1;
                default: return 100;
            }
        }
    }

    /// <summary>
    /// 固定反应触发绑定（Block/Dodge 必填）。<see cref="TriggerTick"/> 只能由来源攻击的
    /// <c>ImpactTick</c> 推导；命令生产者无法声明它。
    /// </summary>
    public sealed record ActionPlanTriggerBinding(
        ReactionOpportunityId ReactionOpportunityId,
        ActionPlanId SourceThreatPlanId,
        long TriggerTick,
        long ResponseDeadlineTick);

    /// <summary>
    /// 计划动作时序的稳定失败码。
    /// </summary>
    public static class ActionPlanCodes
    {
        public const string ACTION_TERMINATION_REASON_UNKNOWN = "ACTION_TERMINATION_REASON_UNKNOWN";
        public const string ACTION_PLAN_SPEC_NOT_FOUND = "ACTION_PLAN_SPEC_NOT_FOUND";
        public const string ACTION_PLAN_TIMING_NOT_RESOLVED = "ACTION_PLAN_TIMING_NOT_RESOLVED";
        public const string ACTION_PLAN_TIMING_ALREADY_RESOLVED = "ACTION_PLAN_TIMING_ALREADY_RESOLVED";
        public const string ACTION_PLAN_REACTION_INTERVAL_MISMATCH = "ACTION_PLAN_REACTION_INTERVAL_MISMATCH";
        public const string ACTION_PLAN_REACTION_CANNOT_REBIND = "ACTION_PLAN_REACTION_CANNOT_REBIND";
        public const string ACTION_PLAN_ID_INVALID = "ACTION_PLAN_ID_INVALID";
        public const string ACTION_PLAN_OWNER_INVALID = "ACTION_PLAN_OWNER_INVALID";
        public const string ACTION_PLAN_DUPLICATE_ID = "ACTION_PLAN_DUPLICATE_ID";
        public const string ACTION_PLAN_NOT_FOUND = "ACTION_PLAN_NOT_FOUND";
        public const string ACTION_PLAN_REACTION_REQUIRES_BINDING = "ACTION_PLAN_REACTION_REQUIRES_BINDING";
        public const string ACTION_PLAN_ORDINARY_FORBIDS_BINDING = "ACTION_PLAN_ORDINARY_FORBIDS_BINDING";
        public const string ACTION_PLAN_REACTION_REQUIRES_ZERO_BUDGET = "ACTION_PLAN_REACTION_REQUIRES_ZERO_BUDGET";
        public const string ACTION_PLAN_ID_RESERVATION_MISMATCH = "ACTION_PLAN_ID_RESERVATION_MISMATCH";
    }

    /// <summary>
    /// 全局 <c>ActionPlan</c>（任务包「必须产出」1）。<strong>单一身份</strong>：
    /// 创建即获得稳定 <see cref="ActionPlanId"/> 并进入 <see cref="ActionPlanState.Editable"/>，
    /// 执行门禁通过时<strong>在同一原子提交</strong>中建立 Locked 边界并立即 Running。
    ///
    /// 禁止事项（结构上不可能发生）：
    /// <list type="bullet">
    /// <item>没有 <c>DraftPlan</c>/<c>ScheduledAction</c> 之类的第二次复制：本类型是唯一形态；</item>
    /// <item><see cref="RebindAbsoluteTicks"/> <strong>只</strong>重绑绝对 Tick，
    /// <strong>不</strong>重采样任何相对时长（速度、窗口切换、视觉时间缩放都不得影响它）；</item>
    /// <item>终态字段（<see cref="State"/>/<see cref="TerminationReason"/>/<see cref="TerminalTick"/>）
    /// 的写入面是 <c>internal</c>，只有统一终态协调器（同一程序集内）能改；</item>
    /// <item>计划<strong>不</strong>持有任何场景/MonoBehaviour/视图引用，也不引用 <c>SubmittedWindowId</c>
    /// 做执行过滤（它只用于预算来源与审计）。</item>
    /// </list>
    /// </summary>
    public sealed class ActionPlan
    {
        internal ActionPlan(
            ActionPlanId actionPlanId,
            ActionPlanOrigin origin,
            UnitId ownerUnitId,
            ActionSpecId actionSpecId,
            ActionType actionType,
            GridDirection facing,
            UnitId? primaryTargetUnitId,
            GridPoint? destination,
            WindowId? submittedWindowId,
            ActionPlanTriggerBinding triggerBinding,
            long createdAtTick)
        {
            if (!actionPlanId.IsValid)
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_ID_INVALID,
                    actionPlanId.Value.ToString(CultureInfo.InvariantCulture));
            if (!ownerUnitId.IsValid)
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_OWNER_INVALID,
                    ownerUnitId.Value.ToString(CultureInfo.InvariantCulture));
            if (origin == ActionPlanOrigin.Reaction && triggerBinding == null)
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_REACTION_REQUIRES_BINDING,
                    actionPlanId.Value.ToString(CultureInfo.InvariantCulture));
            if (origin == ActionPlanOrigin.Ordinary && triggerBinding != null)
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_ORDINARY_FORBIDS_BINDING,
                    actionPlanId.Value.ToString(CultureInfo.InvariantCulture));

            ActionPlanId = actionPlanId;
            Origin = origin;
            OwnerUnitId = ownerUnitId;
            ActionSpecId = actionSpecId;
            ActionType = actionType;
            Facing = facing;
            PrimaryTargetUnitId = primaryTargetUnitId;
            Destination = destination;
            SubmittedWindowId = submittedWindowId;
            TriggerBinding = triggerBinding;
            CreatedAtTick = createdAtTick;
            LastRequestedStartTick = createdAtTick;
            State = ActionPlanState.Editable;
            TerminationReason = ActionTerminationReason.None;
            TerminalTick = -1L;
            LockedAtTick = -1L;
        }

        // —— 身份 ——

        public ActionPlanId ActionPlanId { get; }

        public ActionPlanOrigin Origin { get; }

        /// <summary>普通计划用于预算来源与审计；高阶反应恒为 <c>null</c>。</summary>
        public WindowId? SubmittedWindowId { get; }

        /// <summary>固定反应触发绑定（普通计划为 <c>null</c>）。</summary>
        public ActionPlanTriggerBinding TriggerBinding { get; }

        public ReactionOpportunityId? ReactionOpportunityId
            => TriggerBinding == null ? (ReactionOpportunityId?)null : TriggerBinding.ReactionOpportunityId;

        public ActionPlanId? SourceThreatPlanId
            => TriggerBinding == null ? (ActionPlanId?)null : TriggerBinding.SourceThreatPlanId;

        /// <summary>是否为带固定 <c>TriggerTick</c> 的反应计划（Block/Dodge）。</summary>
        public bool HasFixedTriggerTick => TriggerBinding != null;

        public long TriggerTick => TriggerBinding == null ? 0L : TriggerBinding.TriggerTick;

        public UnitId OwnerUnitId { get; }

        public ActionSpecId ActionSpecId { get; }

        public ActionType ActionType { get; }

        /// <summary>首次 Add 时固定的朝向；排程编辑不得改写它。</summary>
        public GridDirection Facing { get; }

        /// <summary>可选固定目标（PrimaryTargetOnly 动作必填）。排程编辑<strong>不得</strong>改选目标。</summary>
        public UnitId? PrimaryTargetUnitId { get; }

        /// <summary>Move/Dodge 的目的格（任务 06 的空间事务接缝）。</summary>
        public GridPoint? Destination { get; internal set; }

        // —— 权威排程投影 ——

        /// <summary>只可由<strong>直接 Move 操作</strong>更新的请求起点；系统自动延期不得覆盖它。</summary>
        public long LastRequestedStartTick { get; internal set; }

        public long StartTick { get; internal set; }

        public long EndTick { get; internal set; }

        /// <summary>Guard Active 半开区间起点；非 Guard 恒为 0。</summary>
        public long ActiveStartTick { get; internal set; }

        /// <summary>Guard Active 半开区间右端；非 Guard 恒为 0。</summary>
        public long ActiveEndTick { get; internal set; }

        // —— 已解析的相对时序（首次进入 Editable 时一次性采样）——

        /// <summary>Attack 的解析前摇；Guard 的 Windup；Move 恒为 0。</summary>
        public int ResolvedWindupTicks { get; internal set; }

        /// <summary>Attack/Move 的后摇；Guard 的 Recovery；Block/Dodge 的 Recovery。</summary>
        public int RecoveryTicks { get; internal set; }

        /// <summary>Attack 命中 Tick（恰好产生一次 AttackIntent）；非 Attack 恒为 0。</summary>
        public long ImpactTick { get; internal set; }

        /// <summary>Guard 的 Active 时长；非 Guard 恒为 0。</summary>
        public int ActiveTicks { get; internal set; }

        /// <summary>Move 每路径权重单位的 Tick 基准（<c>ReferenceMoveSpeed = 20</c> 一次性解析）。</summary>
        public int ResolvedBaseStepTicks { get; internal set; }

        /// <summary>已解析路径边数（Editable 期可随依赖重算；Locked 后冻结）。</summary>
        public int ResolvedPathEdgeCount { get; internal set; }

        /// <summary>已解析路径权重单位（预算与 EndTick 使用它，而不是边数）。</summary>
        public int ResolvedPathWeightUnits { get; internal set; }

        /// <summary>Move 的移动段总时长 = <c>ResolvedPathWeightUnits × ResolvedBaseStepTicks</c>。</summary>
        public int MoveDurationTicks { get; internal set; }

        /// <summary>Block/Dodge 的反应前摇。</summary>
        public int ReactionWindupTicks { get; internal set; }

        // —— 预算 ——

        /// <summary>本计划的窗口预算成本（Attack = 前摇+后摇；Guard 三段；Move = 路径权重×基准+后摇；反应 = 0）。</summary>
        public int BudgetCostTicks { get; internal set; }

        /// <summary>尚未消费的 TurnBudget 预留（Editable 期为 <see cref="BudgetCostTicks"/>，启动提交后归 0）。</summary>
        public int ReservedTurnBudgetTicks { get; internal set; }

        // —— 生命周期记账 ——

        public long CreatedAtTick { get; }

        public long LastEditedScheduleRevision { get; internal set; }

        public int AutomaticDeferralCount { get; internal set; }

        /// <summary>启动门禁原子提交的 Tick；-1 表示尚未锁定。</summary>
        public long LockedAtTick { get; internal set; }

        public ActionPlanState State { get; internal set; }

        public ActionTerminationReason TerminationReason { get; internal set; }

        /// <summary>终态 Tick；-1 表示尚未进入终态。</summary>
        public long TerminalTick { get; internal set; }

        /// <summary>时序是否已一次性解析。</summary>
        public bool IsTimingResolved { get; internal set; }

        // —— 派生只读事实 ——

        public bool IsEditable => State == ActionPlanState.Editable;

        public bool IsLocked => State == ActionPlanState.Locked;

        public bool IsRunning => State == ActionPlanState.Running;

        public bool IsCompleted => State == ActionPlanState.Completed;

        public bool IsTerminated => State == ActionPlanState.Terminated;

        public bool IsTerminal => State == ActionPlanState.Completed || State == ActionPlanState.Terminated;

        public bool IsNonTerminal => !IsTerminal;

        public bool IsReaction => Origin == ActionPlanOrigin.Reaction;

        public bool IsOrdinary => Origin == ActionPlanOrigin.Ordinary;

        /// <summary>是否是移动族计划（Move/Dodge）：它们拥有起点依赖与路径投影。</summary>
        public bool IsMovementFamily => ActionType == ActionType.Move || ActionType == ActionType.Dodge;

        /// <summary>是否是普通 Move（会占用路径与空间 Reservation 的族）。</summary>
        public bool IsOrdinaryMove => ActionType == ActionType.Move && Origin == ActionPlanOrigin.Ordinary;

        public bool HasFrozenProjection =>
            State == ActionPlanState.Locked || State == ActionPlanState.Running || IsTerminal;

        /// <summary>是否为 Lane 上的不可变障碍：Locked/Running/终态，或任何固定反应区间。</summary>
        public bool IsLaneObstacle => IsReaction || State != ActionPlanState.Editable;

        /// <summary>计划区间长度（半开区间 <c>[StartTick, EndTick)</c>）。</summary>
        public long IntervalLength => EndTick - StartTick;

        public bool OverlapsInterval(long startTick, long endTick)
            => StartTick < endTick && startTick < EndTick;

        /// <summary>该计划在给定 Tick 是否处于 Guard 的 Active 半开区间。</summary>
        public bool IsActiveAt(long tick)
            => ActionType == ActionType.Guard && tick >= ActiveStartTick && tick < ActiveEndTick;

        // —— 相对时序解析与绝对重绑（唯一的写入口；只有工厂/编辑器/协调器调用）——

        /// <summary>
        /// 首次进入 Editable 时<strong>一次性</strong>解析与属性相关的相对时长。
        /// 二次调用以稳定码拒绝（"只解析一次"是契约而不是约定）。
        /// </summary>
        internal void ResolveTimingOnce(BattleRules rules, double actionSpeed, double moveSpeed,
            ActionSpec spec)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (spec == null)
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND,
                    ActionSpecId.Value ?? string.Empty);
            if (IsTimingResolved)
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_TIMING_ALREADY_RESOLVED,
                    ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));

            switch (ActionType)
            {
                case ActionType.Attack:
                {
                    var timing = spec.Timing as AttackTimingSpec;
                    if (timing == null)
                        throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND,
                            ActionSpecId.Value ?? string.Empty);
                    ResolvedWindupTicks = TickQuantization.ResolveWindupTicks(
                        timing.BaseWindupTicks, rules.ReferenceActionSpeed, actionSpeed);
                    // RecoveryTicks 来自 AttackTimingSpec，不受 ActionSpeed 缩短。
                    RecoveryTicks = timing.RecoveryTicks;
                    break;
                }
                case ActionType.Guard:
                {
                    var timing = spec.Timing as GuardTimingSpec;
                    if (timing == null)
                        throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND,
                            ActionSpecId.Value ?? string.Empty);
                    // Guard 的相对时序固定：不读 ActionSpeed，也不读 MoveSpeed。
                    ResolvedWindupTicks = timing.WindupTicks;
                    ActiveTicks = timing.ActiveTicks;
                    RecoveryTicks = timing.RecoveryTicks;
                    break;
                }
                case ActionType.Move:
                {
                    var timing = spec.Timing as MoveTimingSpec;
                    if (timing == null)
                        throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND,
                            ActionSpecId.Value ?? string.Empty);
                    ResolvedBaseStepTicks = TickQuantization.ResolveMoveBaseStepTicks(
                        timing.BaseStepTicks, rules.ReferenceMoveSpeed, moveSpeed);
                    RecoveryTicks = timing.RecoveryTicks;
                    // 路径本身在创建时给出一次投影；Editable 期可按依赖重算。
                    if (ResolvedPathWeightUnits <= 0) SetPathProjection(0, 0);
                    break;
                }
                case ActionType.Block:
                {
                    var timing = spec.Timing as BlockReactionTimingSpec;
                    if (timing == null)
                        throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND,
                            ActionSpecId.Value ?? string.Empty);
                    ReactionWindupTicks = timing.ReactionWindupTicks;
                    RecoveryTicks = timing.RecoveryTicks;
                    BudgetCostTicks = 0;
                    break;
                }
                case ActionType.Dodge:
                {
                    var timing = spec.Timing as DodgeReactionTimingSpec;
                    if (timing == null)
                        throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND,
                            ActionSpecId.Value ?? string.Empty);
                    ReactionWindupTicks = timing.ReactionWindupTicks;
                    RecoveryTicks = timing.RecoveryTicks;
                    ResolvedBaseStepTicks = rules.ReferenceMoveSpeed;
                    BudgetCostTicks = 0;
                    break;
                }
                default:
                    throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND,
                        ActionSpecId.Value ?? string.Empty);
            }

            IsTimingResolved = true;

            // 普通计划：按当前投影 StartTick 重绑绝对 Tick（此处 StartTick 仍为创建时的值，
            // 真正的起点由排程事务/工厂随后重绑）。
            //
            // 反应计划：<strong>不</strong>在这里重绑——它的绝对区间只能由
            // <see cref="ApplyReactionInterval"/> 从固定 TriggerTick 推导
            // （<c>StartTick = TriggerTick - ReactionWindupTicks</c>），
            // 而 <see cref="RebindAbsoluteTicks"/> 对反应计划是显式禁止的
            // （<see cref="ActionPlanCodes.ACTION_PLAN_REACTION_CANNOT_REBIND"/>）。
            // 若这里无条件调用，反应计划将永远无法创建。
            if (IsOrdinary) RebindAbsoluteTicks(StartTick);
        }

        /// <summary>
        /// 按当前 <see cref="StartTick"/> 重绑<strong>绝对</strong> Tick。
        /// 它<strong>不</strong>重采样任何相对时长：速度 Buff/Debuff、窗口切换与视觉时间缩放
        /// 都不得改变已解析的 <see cref="ResolvedWindupTicks"/>/<see cref="ResolvedBaseStepTicks"/>。
        /// </summary>
        internal void RebindAbsoluteTicks(long startTick)
        {
            if (IsReaction)
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_REACTION_CANNOT_REBIND,
                    ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
            if (startTick < 0L) startTick = 0L;

            StartTick = startTick;
            switch (ActionType)
            {
                case ActionType.Attack:
                    ImpactTick = checked(startTick + ResolvedWindupTicks);
                    EndTick = checked(ImpactTick + RecoveryTicks);
                    BudgetCostTicks = ResolvedWindupTicks + RecoveryTicks;
                    break;
                case ActionType.Guard:
                    ActiveStartTick = checked(startTick + ResolvedWindupTicks);
                    ActiveEndTick = checked(ActiveStartTick + ActiveTicks);
                    EndTick = checked(ActiveEndTick + RecoveryTicks);
                    BudgetCostTicks = ResolvedWindupTicks + ActiveTicks + RecoveryTicks;
                    break;
                case ActionType.Move:
                    MoveDurationTicks = checked(ResolvedPathWeightUnits * ResolvedBaseStepTicks);
                    EndTick = checked(startTick + MoveDurationTicks + RecoveryTicks);
                    BudgetCostTicks = MoveDurationTicks + RecoveryTicks;
                    break;
                default:
                    EndTick = checked(startTick + RecoveryTicks);
                    BudgetCostTicks = RecoveryTicks;
                    break;
            }
        }

        /// <summary>
        /// 重算 Move 的路径投影（边数与<strong>路径权重单位</strong>）。
        /// 预算与 <c>EndTick</c> 使用路径权重而不是边数（00 号规则 32）。
        /// 冻结后调用以稳定码拒绝。
        /// </summary>
        internal void SetPathProjection(int edgeCount, int pathWeightUnits)
        {
            if (edgeCount < 0) edgeCount = 0;
            if (pathWeightUnits < 0) pathWeightUnits = 0;
            ResolvedPathEdgeCount = edgeCount;
            ResolvedPathWeightUnits = pathWeightUnits;
            MoveDurationTicks = checked(ResolvedPathWeightUnits * ResolvedBaseStepTicks);
            EndTick = checked(StartTick + MoveDurationTicks + RecoveryTicks);
            BudgetCostTicks = MoveDurationTicks + RecoveryTicks;
        }

        /// <summary>
        /// 按固定触发绑定建立反应区间：<c>StartTick = TriggerTick - ReactionWindupTicks</c>、
        /// <c>EndTick = TriggerTick + RecoveryTicks</c>、<c>BudgetCostTicks = 0</c>。
        /// 反应区间<strong>不能</strong>被 Lane 或时间线编辑器平移。
        /// </summary>
        internal void ApplyReactionInterval()
        {
            if (!IsReaction)
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_REACTION_REQUIRES_BINDING,
                    ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));

            StartTick = TriggerTick - ReactionWindupTicks;
            EndTick = TriggerTick + RecoveryTicks;
            BudgetCostTicks = 0;
            ReservedTurnBudgetTicks = 0;
            if (StartTick < 0L)
                throw new LogicDefinitionException(ActionPlanCodes.ACTION_PLAN_REACTION_INTERVAL_MISMATCH,
                    StartTick.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 由 <see cref="ActionLifecycleSystem"/> 在 Editable 期同步预留额（未消费）。
        /// 反应计划不参与窗口预算（恒为 0）。
        /// </summary>
        internal void SyncEditableReservation()
        {
            ReservedTurnBudgetTicks = IsEditable && IsOrdinary ? BudgetCostTicks : 0;
        }

        /// <summary>
        /// 一次性投影/空间重建接缝（任务 06）：<see cref="ProjectionCommittedSink"/> 由装配方
        /// 接到"排程事务 → 空间整批替换"的提交点上，因此<strong>不新增</strong>任何特殊命令通道，
        /// 也不改变 <see cref="ActionPlan"/> 的状态机。未接缝时是安全无操作。
        /// </summary>
        internal Action<ActionPlan, long, long, long> ProjectionCommittedSink { get; set; }

        /// <summary>
        /// 通知"本计划的排程投影与空间段/预留<strong>已经</strong>在同一个提交里落定"。
        /// 只在提交成功后调用；失败路径（含空间整批替换失败）<strong>不</strong>调用它。
        /// </summary>
        internal void OnSpaceBatchCommitted(long startTick, long scheduleRevision, long tick)
            => ProjectionCommittedSink?.Invoke(this, startTick, scheduleRevision, tick);

        public override string ToString()
            => "plan#" + ActionPlanId.Value.ToString(CultureInfo.InvariantCulture) +
               "(" + ActionType + "," + State + ")[" + StartTick.ToString(CultureInfo.InvariantCulture) +
               "," + EndTick.ToString(CultureInfo.InvariantCulture) + ")";
    }
}
