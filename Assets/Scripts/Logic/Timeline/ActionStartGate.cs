using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// 启动阻塞原因（稳定的语义分类；字符串码见 <see cref="ActionStartBlockerReasons"/>）。
    /// 它<strong>只</strong>描述"为什么现在不能启动"，不描述"怎么处理"。
    /// </summary>
    public enum ActionStartBlockerReason
    {
        None = 0,

        /// <summary>单位处于带确定结束 Tick 的控制状态（Staggered / KnockedDown / Recovering）。</summary>
        TimedControlState = 1,

        /// <summary>任务 06 的限时空间阻塞（目的格/路径被临时占据）。</summary>
        TimedSpaceBlock = 2,

        /// <summary>任务 07 的窗口预算/授权暂不可用。</summary>
        TurnBudgetUnavailable = 3
    }

    /// <summary>启动阻塞原因的稳定字符串码（进入事件与快照；不得改名或复用）。</summary>
    public static class ActionStartBlockerReasons
    {
        public const string TimedControlState = "START_BLOCKER_TIMED_CONTROL_STATE";
        public const string TimedSpaceBlock = "START_BLOCKER_TIMED_SPACE_BLOCK";
        public const string TurnBudgetUnavailable = "START_BLOCKER_TURN_BUDGET_UNAVAILABLE";

        /// <summary>原因 → 稳定字符串码（<see cref="ActionStartBlockerReason.None"/> 返回 null）。</summary>
        public static string CodeOf(ActionStartBlockerReason reason)
        {
            switch (reason)
            {
                case ActionStartBlockerReason.None: return null;
                case ActionStartBlockerReason.TimedControlState: return TimedControlState;
                case ActionStartBlockerReason.TimedSpaceBlock: return TimedSpaceBlock;
                case ActionStartBlockerReason.TurnBudgetUnavailable: return TurnBudgetUnavailable;
                default:
                    throw new LogicDefinitionException(ScheduleCodes.SCHEDULE_START_GATE_COMMIT_INCONSISTENT,
                        ((int)reason).ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// 冻结的启动门禁结果（任务包「必须产出」5 第一段）。<strong>恰好四分</strong>，
    /// 不存在第五种可能，也不存在"静默跳过"：
    ///
    /// <list type="bullet">
    /// <item><see cref="Startable"/>：原子提交（时序/路径/Reservation 冻结 +
    /// TurnBudget <c>Reserved -&gt; Spent</c> + <c>LockedAtTick</c> + <c>Running</c>）后开始执行。</item>
    /// <item><see cref="Retryable"/>：带<strong>有限且严格大于当前 Tick</strong>的
    /// <c>RetryAtTick</c>；计划保持 Editable 并经同一求值器原子向右延期。</item>
    /// <item><see cref="Terminal"/>：以明确原因经统一终态协调器终止（释放锁定前预留）。</item>
    /// <item><see cref="InvariantViolation"/>：内部矛盾（<strong>不是</strong>普通终态），
    /// 必须以稳定错误令 Step 失败，不得"终止计划后继续"。</item>
    /// </list>
    /// </summary>
    public abstract record ActionStartGateResult
    {
        public abstract bool IsStartable { get; }
        public abstract bool IsRetryable { get; }
        public abstract bool IsTerminal { get; }
        public abstract bool IsInvariantViolation { get; }

        public sealed record Startable(ActionPlan Plan) : ActionStartGateResult
        {
            public override bool IsStartable => true;
            public override bool IsRetryable => false;
            public override bool IsTerminal => false;
            public override bool IsInvariantViolation => false;
        }

        public sealed record Retryable(
            ActionPlan Plan, ActionStartBlockerReason Blocker, long RetryAtTick) : ActionStartGateResult
        {
            public override bool IsStartable => false;
            public override bool IsRetryable => true;
            public override bool IsTerminal => false;
            public override bool IsInvariantViolation => false;

            /// <summary>阻塞原因的稳定字符串码。</summary>
            public string BlockerCode => ActionStartBlockerReasons.CodeOf(Blocker);
        }

        public sealed record Terminal(
            ActionPlan Plan, ActionTerminationReason Reason) : ActionStartGateResult
        {
            public override bool IsStartable => false;
            public override bool IsRetryable => false;
            public override bool IsTerminal => true;
            public override bool IsInvariantViolation => false;

            /// <summary>终止原因的稳定字符串码。</summary>
            public string ReasonCode => ActionTerminationReasons.CodeOf(Reason);
        }

        public sealed record InvariantViolation(
            ActionPlan Plan, string ErrorCode, string Detail) : ActionStartGateResult
        {
            public override bool IsStartable => false;
            public override bool IsRetryable => false;
            public override bool IsTerminal => false;
            public override bool IsInvariantViolation => true;
        }
    }

    /// <summary>
    /// 启动门禁的<strong>只读</strong>输入事实。它只回答事实，不写任何状态、不做延期决策。
    /// 任务 06/07 通过 <see cref="SpaceBlockUntilTickOf"/> 与 <see cref="IsActorAvailable"/>
    /// 注入限时空间阻塞与授权可用性，<strong>不改变</strong>门禁的任何其它语义。
    /// </summary>
    public sealed class ActionStartGateContext
    {
        public ActionStartGateContext(
            long tick,
            Func<UnitId, long?> blockingUntilTickOf,
            Func<UnitId, bool> isAlive,
            ActionPlanFactory factory,
            int maxAutomaticDeferralsPerPlan,
            Func<UnitId, bool> isActorAvailable = null,
            Func<UnitId, long?> spaceBlockUntilTickOf = null)
        {
            Tick = tick;
            BlockingUntilTickOf = blockingUntilTickOf ?? (_ => null);
            IsAlive = isAlive ?? (_ => true);
            Factory = factory;
            MaxAutomaticDeferralsPerPlan = maxAutomaticDeferralsPerPlan;
            IsActorAvailable = isActorAvailable ?? (_ => true);
            SpaceBlockUntilTickOf = spaceBlockUntilTickOf ?? (_ => null);
        }

        /// <summary>本 Tick（命令目标 Tick）。门禁只评估 <c>StartTick == Tick</c> 的到期计划。</summary>
        public long Tick { get; }

        /// <summary>
        /// 单位当前状态的<strong>权威</strong>有限阻塞边界（<c>null</c> = 当前状态不构成有限阻塞）。
        /// 它直接来自 <c>UnitStateMachine.BlockingUntilTick</c>，不是"下一 Tick 再试"的近似。
        /// </summary>
        public Func<UnitId, long?> BlockingUntilTickOf { get; }

        public Func<UnitId, bool> IsAlive { get; }

        /// <summary>任务 06 的限时空间阻塞（返回 null = 无空间阻塞）。</summary>
        public Func<UnitId, long?> SpaceBlockUntilTickOf { get; }

        /// <summary>任务 07 的授权/预算可用性（默认恒为可用）。</summary>
        public Func<UnitId, bool> IsActorAvailable { get; }

        public ActionPlanFactory Factory { get; }

        /// <summary><c>BattleRules.MaxAutomaticDeferralsPerPlan</c>（版本化）。</summary>
        public int MaxAutomaticDeferralsPerPlan { get; }
    }

    /// <summary>
    /// <strong><c>ActionStartGate</c></strong>（任务包「必须产出」5 第一段与「核心规则」）：
    /// 普通计划<strong>原子锁定/启动的唯一入口</strong>的判定部分。
    ///
    /// 冻结判定顺序（先永久、后临时；任一永久失败都不再考虑延期）：
    /// <list type="number">
    /// <item><strong>不变量前置</strong>：只接受 <c>StartTick == 本 Tick</c> 的 Editable 普通计划；
    /// 其它输入是装配矛盾 ⇒ <c>InvariantViolation</c>（不是普通终态）。</item>
    /// <item><strong>所有者死亡</strong> ⇒ <c>Terminal(OwnerDied)</c>。</item>
    /// <item><strong>PrimaryTarget 关系/存活</strong>（与创建、排程候选<strong>共用</strong>
    /// <see cref="ActionPlanFactory.ValidatePrimaryTarget"/>）⇒ <c>Terminal(TargetInvalid)</c>；
    /// <strong>绝不</strong>自动改选目标，也<strong>绝不</strong>自动延期。</item>
    /// <item><strong>有限临时阻塞</strong>（控制状态 / 限时空间 / 授权）且仍有延期余量
    /// ⇒ <c>Retryable</c>，条件是 <c>RetryAtTick</c> 有限且<strong>严格大于</strong>当前 Tick。</item>
    /// <item><strong>无有限恢复</strong>（<c>BlockingUntilTick == null</c> 表明没有有限恢复路径）、
    /// <strong>延期越限</strong> ⇒ <c>Terminal(ActorUnavailableAtStart / AutoDeferralLimitExceeded)</c>。</item>
    /// <item>否则 <c>Startable</c>。</item>
    /// </list>
    ///
    /// 它<strong>不</strong>：写计划状态、改修订号、推进 Lane、发射事件。
    /// 原子提交由 <c>ActionStartGateCommitter</c> 完成；自动延期由
    /// <c>ScheduleEditor.ApplySystemAutoDeferral</c> 完成（两者都由 Step 阶段 7 驱动）。
    /// </summary>
    public sealed class ActionStartGate
    {
        /// <summary>
        /// 评估一个到期计划。它是<strong>纯函数</strong>：不写状态、不发射事件、不做延期。
        /// </summary>
        public ActionStartGateResult Evaluate(ActionPlan plan, ActionStartGateContext context)
        {
            if (plan == null || context == null)
                return new ActionStartGateResult.InvariantViolation(
                    plan, ScheduleCodes.SCHEDULE_OPERATION_INVALID, "null input");

            if (!plan.IsOrdinary)
                return new ActionStartGateResult.InvariantViolation(
                    plan, ScheduleCodes.SCHEDULE_REACTION_CANNOT_BE_EDITED,
                    "reaction plans are born Locked and never pass the ordinary gate");

            if (!plan.IsEditable)
                return new ActionStartGateResult.InvariantViolation(
                    plan, ScheduleCodes.SCHEDULE_PLAN_NOT_EDITABLE,
                    "gate only evaluates Editable ordinary plans; state=" + plan.State);

            if (plan.StartTick != context.Tick)
                return new ActionStartGateResult.InvariantViolation(
                    plan, ScheduleCodes.SCHEDULE_START_GATE_NOT_AT_DUE_TICK,
                    "plan.StartTick=" + plan.StartTick.ToString(CultureInfo.InvariantCulture) +
                    " tick=" + context.Tick.ToString(CultureInfo.InvariantCulture));

            // —— 永久失败：所有者死亡 ——
            if (!context.IsAlive(plan.OwnerUnitId))
                return new ActionStartGateResult.Terminal(plan, ActionTerminationReason.OwnerDied);

            // —— 永久失败：PrimaryTarget 失效（绝不改选目标、绝不自动延期）——
            if (context.Factory != null)
            {
                string targetError = context.Factory.ValidatePrimaryTargetForGate(plan);
                if (targetError != null)
                    return new ActionStartGateResult.Terminal(plan, ActionTerminationReason.TargetInvalid);
            }

            // —— 临时阻塞：只有带有限结束 Tick 的阻塞才可延期 ——
            long? controlBlock = context.BlockingUntilTickOf(plan.OwnerUnitId);
            long? spaceBlock = context.SpaceBlockUntilTickOf(plan.OwnerUnitId);
            bool actorAvailable = context.IsActorAvailable(plan.OwnerUnitId);

            ActionStartBlockerReason blocker = ActionStartBlockerReason.None;
            long retryAt = long.MinValue;

            if (controlBlock.HasValue)
            {
                blocker = ActionStartBlockerReason.TimedControlState;
                retryAt = controlBlock.Value;
            }

            if (spaceBlock.HasValue && (blocker == ActionStartBlockerReason.None || spaceBlock.Value > retryAt))
            {
                blocker = ActionStartBlockerReason.TimedSpaceBlock;
                retryAt = spaceBlock.Value;
            }

            if (!actorAvailable && blocker == ActionStartBlockerReason.None)
            {
                // 授权/预算不可用：没有权威的有限恢复边界 ⇒ 不能凭猜测延期。
                return new ActionStartGateResult.Terminal(plan, ActionTerminationReason.ActorUnavailableAtStart);
            }

            if (blocker == ActionStartBlockerReason.None)
                return new ActionStartGateResult.Startable(plan);

            // RetryAtTick 必须有限且严格大于当前 Tick。无限/不前进的阻塞是永久失败，
            // 而不是"下一 Tick 再试"（任务包「禁止事项」明确禁止无界轮询）。
            if (retryAt == long.MaxValue || retryAt <= context.Tick)
                return new ActionStartGateResult.Terminal(plan, ActionTerminationReason.ActorUnavailableAtStart);

            // 延期余量：次数上限。
            if (plan.AutomaticDeferralCount + 1 > context.MaxAutomaticDeferralsPerPlan)
                return new ActionStartGateResult.Terminal(plan, ActionTerminationReason.AutoDeferralLimitExceeded);

            return new ActionStartGateResult.Retryable(plan, blocker, retryAt);
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public static string Describe(ActionStartGateResult result)
        {
            if (result == null) return "<null>";
            switch (result)
            {
                case ActionStartGateResult.Startable s:
                    return "Startable(" + s.Plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture) + ")";
                case ActionStartGateResult.Retryable r:
                    return "Retryable(" + r.Plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture) + "," +
                           r.BlockerCode + "," + r.RetryAtTick.ToString(CultureInfo.InvariantCulture) + ")";
                case ActionStartGateResult.Terminal t:
                    return "Terminal(" + t.Plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture) + "," +
                           t.ReasonCode + ")";
                case ActionStartGateResult.InvariantViolation v:
                    return "InvariantViolation(" + v.ErrorCode + "," + v.Detail + ")";
                default:
                    return result.GetType().Name;
            }
        }
    }

    /// <summary>原子启动提交的稳定失败码。</summary>
    public static class ActionStartGateCommitCodes
    {
        public const string RESOURCE_COMMIT_ERROR = "START_GATE_RESOURCE_COMMIT_ERROR";
        public const string PLAN_NOT_EDITABLE = "START_GATE_PLAN_NOT_EDITABLE";
        public const string PLAN_NOT_DUE = "START_GATE_PLAN_NOT_DUE";
    }
}
