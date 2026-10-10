using System;
using System.Collections.Generic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Units
{
    /// <summary>
    /// 单位状态机（主方案 3.5 / 任务 04「必须产出」1–3）。
    ///
    /// 职责边界（任务包「核心规则」与「禁止事项」）：
    /// <list type="bullet">
    /// <item><strong>只描述事实</strong>：当前状态、是否有有限结束 Tick、权威阻塞边界
    /// <see cref="BlockingUntilTick"/>。它为任务 05 的启动门禁提供只读事实。</item>
    /// <item><strong>不提供提交/延期权威</strong>：没有 <c>CanExecuteImmediately</c>、
    /// 没有"能否接受新计划"的判断、不修改 ActorLane、不移动/终止/锁定任何 ActionPlan。
    /// 延期由任务 05 的 <c>ScheduleEvaluator</c> 原子提交，不在这里。</item>
    /// <item><strong>不提供免伤</strong>：<see cref="CanReceiveDirectHit"/> 只回答
    /// "尚未被特殊交互消解的直接 Hit 能否进入 Remaining Hit 结算"；
    /// Guard 的部分抵抗来自计划 Active 区间、Block 的完全抵抗来自匹配 TriggerTick 的反应 Intent、
    /// Dodge 的避伤来自换格后的空间复核，三者都<strong>不</strong>由状态实现。</item>
    /// <item><strong>不提供通用的交互参与资格</strong>：没有 <c>IsTargetableByInteraction</c>。
    /// 特殊交互（Block/Dodge 反应、AOE 目标聚合等）的参与资格各自读取自己的权威来源。</item>
    /// <item><strong>单一互斥状态</strong>：首版不做正交状态机；"边移动边攻击"若成为需求，
    /// 必须另行设计并显式修改本契约。</item>
    /// </list>
    ///
    /// 不变的转换入口：显式转换与自动到期都经 <see cref="TryTransition"/>，
    /// 因此合法性判定、区间计算与事件形状完全一致，只有原因码不同。
    /// </summary>
    public sealed class UnitStateMachine
    {
        /// <summary>转换被拒绝时的稳定诊断（拒绝<strong>不</strong>抛出，见 <see cref="TryTransition"/>）。</summary>
        public readonly struct TransitionOutcome
        {
            public TransitionOutcome(bool applied, string rejectionCode, UnitState from, UnitState to)
            {
                Applied = applied;
                RejectionCode = rejectionCode;
                From = from;
                To = to;
            }

            /// <summary>是否真的发生了转换。</summary>
            public bool Applied { get; }

            /// <summary>被拒绝时的稳定原因码（成功时为 null）。</summary>
            public string RejectionCode { get; }

            public UnitState From { get; }

            public UnitState To { get; }

            public override string ToString()
                => (Applied ? "applied " : "rejected(" + RejectionCode + ") ") + From + "->" + To;
        }

        private readonly UnitId _unitId;
        private readonly LogicEventOutbox _outbox;

        /// <summary>
        /// 构造状态机。<paramref name="outbox"/> 是本场战斗唯一的事件 Outbox：
        /// 状态机的每一次转换都在同一个 Outbox 上分配事件序号，
        /// 因此不存在"状态机自己的一套事件顺序"。
        /// </summary>
        public UnitStateMachine(UnitId unitId, LogicEventOutbox outbox, long initialTick = 0)
        {
            if (!unitId.IsValid)
                throw new LogicDefinitionException(UnitStateCodes.STATE_UNKNOWN, "unitId=" + unitId.Value);
            _unitId = unitId;
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            if (initialTick < 0) throw new LogicDefinitionException(UnitStateCodes.STATE_UNKNOWN, "initialTick");
            StateStartTick = initialTick;
        }

        public UnitId UnitId => _unitId;

        public UnitState CurrentState { get; private set; } = UnitState.Idle;

        public UnitState PreviousState { get; private set; } = UnitState.Idle;

        /// <summary>当前状态开始 Tick（进入该状态的 Tick）。</summary>
        public long StateStartTick { get; private set; }

        /// <summary>
        /// 当前状态的权威结束边界（<strong>半开区间右端</strong>）；
        /// <c>long.MaxValue</c> 表示无限持续。
        /// </summary>
        public long StateEndTick { get; private set; } = long.MaxValue;

        /// <summary>有限持续状态到期后的显式目标（无限持续时为 null）。</summary>
        public StateTransitionSpec NextOnExpire { get; private set; }

        /// <summary>当前状态是否有权威的有限结束 Tick。</summary>
        public bool HasFiniteEndTick => StateEndTick != long.MaxValue;

        /// <summary>是否处于终态（<see cref="UnitState.Dead"/>）。</summary>
        public bool IsTerminal => CurrentState == UnitState.Dead;

        /// <summary>被拒绝的转换累计数（只读诊断；不参与哈希）。</summary>
        public int RejectedTransitionCount { get; private set; }

        /// <summary>自动到期转换累计数（只读诊断；显式转换不计入）。</summary>
        public int AutomaticExpiryCount { get; private set; }

        /// <summary>
        /// 供任务 05 启动门禁消费的<strong>只读</strong>有限阻塞事实
        /// （任务包「必须产出」2 的第二段）。
        ///
        /// 语义：当前状态有一个<strong>权威</strong>结束 Tick 时返回它；否则返回 null。
        /// 它<strong>不是</strong>"下一 Tick 再试"的替代品——返回值直接来自状态实例的
        /// <see cref="StateEndTick"/>，因此门禁可以在
        /// <c>RetryAtTick = BlockingUntilTick</c> 上做一次确定性的自动延期，
        /// 而不是每 Tick 轮询重试。
        ///
        /// 它<strong>不</strong>回答"能否提交新动作"：那只属于 ActorLane/ScheduleEditor。
        /// </summary>
        public long? BlockingUntilTick => HasFiniteEndTick ? StateEndTick : (long?)null;

        /// <summary>
        /// 单位当前状态是否能承受<strong>尚未被特殊交互消解</strong>的直接 Hit。
        ///
        /// 该属性<strong>只</strong>回答 Remaining Hit 阶段（处理阶段名称，不是攻击类型）的
        /// 直接命中结算资格，并<strong>不</strong>控制 Intent 收集、冲突图连边、
        /// Attack–Block／Attack–Dodge 的配对，也不表示"可否被任何交互选中"。
        ///
        /// <list type="bullet">
        /// <item><see cref="UnitState.Guarding"/> / <see cref="UnitState.Blocking"/> /
        /// <see cref="UnitState.Dodging"/> 一律返回 <c>true</c>——状态本身零免伤。</item>
        /// <item><see cref="UnitState.Staggered"/> / <see cref="UnitState.KnockedDown"/> /
        /// <see cref="UnitState.Recovering"/> / <see cref="UnitState.Dead"/> 返回 <c>false</c>
        /// （首版规则；倒地追击若成为需求，必须作为直接命中规则变更单独修改并更新 golden）。</item>
        /// </list>
        /// </summary>
        public bool CanReceiveDirectHit => CanReceiveDirectHitIn(CurrentState);

        /// <summary>直接命中结算资格的<strong>唯一</strong>状态映射（矩阵与查询共用同一处真值）。</summary>
        public static bool CanReceiveDirectHitIn(UnitState state)
        {
            switch (state)
            {
                case UnitState.Idle:
                case UnitState.Windup:
                case UnitState.Recovery:
                case UnitState.Moving:
                case UnitState.Guarding:
                case UnitState.Blocking:
                case UnitState.Dodging:
                    return true;
                case UnitState.Staggered:
                case UnitState.KnockedDown:
                case UnitState.Recovering:
                case UnitState.Dead:
                    return false;
                default:
                    throw new LogicDefinitionException(UnitStateCodes.STATE_UNKNOWN, state.ToString());
            }
        }

        /// <summary>
        /// 唯一的转换入口。显式转换与自动到期都调用本方法：合法性判定、区间计算与
        /// 事件发射完全一致。
        ///
        /// 被拒绝的转换<strong>返回</strong>失败结果而<strong>不</strong>抛异常，也不修改任何状态；
        /// 调用方（动作阶段边界/控制 Intent）负责按自己的规则处理拒绝。
        /// 自动到期路径（<see cref="Advance"/>）在遇到"未被声明的到期转换"时才抛出
        /// <see cref="LogicDefinitionException"/>——那是装配错误，不是运行时拒绝。
        /// </summary>
        public TransitionOutcome TryTransition(
            StateTransitionSpec transition, long currentTick, string reasonCode)
        {
            UnitState from = CurrentState;
            if (transition == null)
                return Reject(UnitStateCodes.STATE_TRANSITION_NOT_DECLARED, from, from);

            string validation = transition.Validate();
            if (validation != null)
                return Reject(validation, from, transition.Target);

            UnitState to = transition.Target;
            bool changed = IsValidTransition(from, to, out string rejection);
            if (!changed) return Reject(rejection, from, to);

            PreviousState = from;
            CurrentState = to;
            StateStartTick = currentTick;
            StateEndTick = transition.IsTimed ? checked(currentTick + transition.DurationTicks) : long.MaxValue;
            NextOnExpire = transition.IsTimed ? transition.NextOnExpire : null;

            string reason = string.IsNullOrEmpty(reasonCode) ? UnitStateTransitionReasons.Explicit : reasonCode;
            long sequenceTick = currentTick;
            UnitId unitId = _unitId;
            UnitState capturedFrom = from;
            UnitState capturedTo = to;
            int duration = transition.DurationTicks;
            long endTick = StateEndTick;
            _outbox.Emit(sequence => new UnitStateChangedEvent(
                sequenceTick, sequence, unitId, capturedFrom, capturedTo,
                duration, endTick, reason));

            return new TransitionOutcome(true, null, from, to);
        }

        /// <summary>
        /// 自动到期推进（阶段 1 的唯一状态入口）。
        ///
        /// 到期条件使用<strong>半开区间</strong>：<c>currentTick &gt;= StateEndTick</c>
        /// 即视为已到期，因此状态在 <c>[Start, End)</c> 内有效，到期 Tick 上先转换、
        /// 不再额外停留一 Tick。无限持续状态不在此推进。
        ///
        /// 返回是否发生了自动转换。到期目标未被转换矩阵声明时抛出
        /// <see cref="LogicDefinitionException"/>（装配错误，显式失败）。
        /// </summary>
        public bool Advance(long currentTick)
        {
            if (!HasFiniteEndTick) return false;
            if (currentTick < StateEndTick) return false;

            StateTransitionSpec next = NextOnExpire;
            if (next == null)
                throw new LogicDefinitionException(
                    UnitStateCodes.STATE_TIMED_TRANSITION_MISSING_NEXT_ON_EXPIRE,
                    _unitId.Value + "|" + CurrentState);

            TransitionOutcome outcome = TryTransition(
                next, currentTick, UnitStateTransitionReasons.AutomaticExpiry);
            if (!outcome.Applied)
                throw new LogicDefinitionException(UnitStateCodes.STATE_TIMED_EXPIRY_NOT_DECLARED,
                    _unitId.Value + "|" + outcome.From + "->" + outcome.To + "|" + outcome.RejectionCode);

            AutomaticExpiryCount++;
            return true;
        }

        /// <summary>
        /// 声明的转换矩阵（唯一真值）。<c>to == Dead</c> 从任何非终态都合法；
        /// <c>from == Dead</c> 一律非法（Dead 是终态）；自反转换非法。
        /// </summary>
        public static bool IsValidTransition(UnitState from, UnitState to, out string rejectionCode)        {
            if (!StateTransitionSpec.IsKnownState(from) || !StateTransitionSpec.IsKnownState(to))
            {
                rejectionCode = UnitStateCodes.STATE_UNKNOWN;
                return false;
            }

            if (from == UnitState.Dead)
            {
                // Dead 是终态：不允许自动回 Idle，也不允许任何其他恢复路径。
                rejectionCode = UnitStateCodes.STATE_TRANSITION_FROM_TERMINAL;
                return false;
            }

            if (from == to)
            {
                rejectionCode = UnitStateCodes.STATE_TRANSITION_SAME_STATE;
                return false;
            }

            if (to == UnitState.Dead)
            {
                rejectionCode = null;
                return true;
            }

            bool declared;
            switch (from)
            {
                case UnitState.Idle:
                    declared = to == UnitState.Windup || to == UnitState.Recovery || to == UnitState.Moving
                        || to == UnitState.Guarding || to == UnitState.Blocking
                        || to == UnitState.Dodging || to == UnitState.Staggered
                        || to == UnitState.KnockedDown;
                    break;
                case UnitState.Windup:
                    declared = to == UnitState.Idle || to == UnitState.Recovery || to == UnitState.Staggered
                        || to == UnitState.KnockedDown;
                    break;
                case UnitState.Recovery:
                    declared = to == UnitState.Idle || to == UnitState.Staggered
                        || to == UnitState.KnockedDown;
                    break;
                case UnitState.Moving:
                    declared = to == UnitState.Idle || to == UnitState.Staggered
                        || to == UnitState.KnockedDown;
                    break;
                case UnitState.Guarding:
                    declared = to == UnitState.Idle || to == UnitState.Staggered
                        || to == UnitState.KnockedDown;
                    break;
                case UnitState.Blocking:
                    declared = to == UnitState.Idle || to == UnitState.Staggered
                        || to == UnitState.KnockedDown;
                    break;
                case UnitState.Dodging:
                    declared = to == UnitState.Idle || to == UnitState.Staggered
                        || to == UnitState.KnockedDown;
                    break;
                case UnitState.Staggered:
                    // 首版：硬直到期直接回 Idle（30 Tick）；Recovering 槽位保留、
                    // 默认不启用（01B 决策 B2）。转换矩阵里保留 Recovering 的合法声明，
                    // 使任务 05+ 可以显式启用它而不必改矩阵。
                    declared = to == UnitState.Recovering || to == UnitState.Idle || to == UnitState.KnockedDown;
                    break;
                case UnitState.KnockedDown:
                    declared = to == UnitState.Recovering || to == UnitState.Idle;
                    break;
                case UnitState.Recovering:
                    declared = to == UnitState.Idle || to == UnitState.Staggered
                        || to == UnitState.KnockedDown;
                    break;
                default:
                    declared = false;
                    break;
            }

            rejectionCode = declared ? null : UnitStateCodes.STATE_TRANSITION_NOT_DECLARED;
            return declared;
        }

        /// <summary>矩阵条目（<c>From -&gt; To</c>）。</summary>
        public readonly struct TransitionPair
        {
            public TransitionPair(UnitState from, UnitState to)
            {
                From = from;
                To = to;
            }

            public UnitState From { get; }

            public UnitState To { get; }

            public override string ToString() => From + "->" + To;
        }

        /// <summary>
        /// 冻结的转换矩阵（<strong>不含</strong> <c>* -&gt; Dead</c> 这一族：它由
        /// "任何非终态都可以致死"规则统一给出）。矩阵是<strong>枚举出来的真值</strong>：
        /// 测试可以对它逐项断言"恰好这些转换被接受、其余一律拒绝"，
        /// 而不是靠读 <see cref="IsValidTransition"/> 的实现来猜。
        /// </summary>
        public static readonly IReadOnlyList<TransitionPair> DeclaredTransitions = new[]
        {
            new TransitionPair(UnitState.Idle, UnitState.Windup),
            new TransitionPair(UnitState.Idle, UnitState.Recovery),
            new TransitionPair(UnitState.Idle, UnitState.Moving),
            new TransitionPair(UnitState.Idle, UnitState.Guarding),
            new TransitionPair(UnitState.Idle, UnitState.Blocking),
            new TransitionPair(UnitState.Idle, UnitState.Dodging),
            new TransitionPair(UnitState.Idle, UnitState.Staggered),
            new TransitionPair(UnitState.Idle, UnitState.KnockedDown),
            new TransitionPair(UnitState.Windup, UnitState.Recovery),
            new TransitionPair(UnitState.Windup, UnitState.Idle),
            new TransitionPair(UnitState.Windup, UnitState.Staggered),
            new TransitionPair(UnitState.Windup, UnitState.KnockedDown),
            new TransitionPair(UnitState.Recovery, UnitState.Idle),
            new TransitionPair(UnitState.Recovery, UnitState.Staggered),
            new TransitionPair(UnitState.Recovery, UnitState.KnockedDown),
            new TransitionPair(UnitState.Moving, UnitState.Idle),
            new TransitionPair(UnitState.Moving, UnitState.Staggered),
            new TransitionPair(UnitState.Moving, UnitState.KnockedDown),
            new TransitionPair(UnitState.Guarding, UnitState.Idle),
            new TransitionPair(UnitState.Guarding, UnitState.Staggered),
            new TransitionPair(UnitState.Guarding, UnitState.KnockedDown),
            new TransitionPair(UnitState.Blocking, UnitState.Idle),
            new TransitionPair(UnitState.Blocking, UnitState.Staggered),
            new TransitionPair(UnitState.Blocking, UnitState.KnockedDown),
            new TransitionPair(UnitState.Dodging, UnitState.Idle),
            new TransitionPair(UnitState.Dodging, UnitState.Staggered),
            new TransitionPair(UnitState.Dodging, UnitState.KnockedDown),
            // 首版：硬直/击倒到期<strong>直接回 Idle</strong>（01B 决策 B2：Stagger=30、Knockdown=60）。
            // Recovering 槽位保留：它的转换仍被声明，但首版默认不使用（时长未指定）。
            new TransitionPair(UnitState.Staggered, UnitState.Idle),
            new TransitionPair(UnitState.Staggered, UnitState.Recovering),
            new TransitionPair(UnitState.Staggered, UnitState.KnockedDown),
            new TransitionPair(UnitState.KnockedDown, UnitState.Idle),
            new TransitionPair(UnitState.KnockedDown, UnitState.Recovering),
            new TransitionPair(UnitState.Recovering, UnitState.Idle),
            new TransitionPair(UnitState.Recovering, UnitState.Staggered),
            new TransitionPair(UnitState.Recovering, UnitState.KnockedDown)
        };

        /// <summary>
        /// 硬直的标准声明：持续 <paramref name="durationTicks"/> Tick 后<strong>显式</strong>回到 Idle。
        /// 显式给出到期目标，状态机内部不存在隐式默认值。
        /// </summary>
        public static StateTransitionSpec StaggerFor(int durationTicks)
            => StateTransitionSpec.Timed(UnitState.Staggered, durationTicks, UnitState.Idle);

        /// <summary>击倒的标准声明：持续 <paramref name="durationTicks"/> Tick 后显式回到 Idle。</summary>
        public static StateTransitionSpec KnockDownFor(int durationTicks)
            => StateTransitionSpec.Timed(UnitState.KnockedDown, durationTicks, UnitState.Idle);

        private TransitionOutcome Reject(string code, UnitState from, UnitState to)
        {
            RejectedTransitionCount++;
            return new TransitionOutcome(false, code, from, to);
        }
    }
}
