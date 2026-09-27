using System.Collections.Generic;
using ProjectHero.Logic.Combat;

namespace ProjectHero.Logic.Units
{
    /// <summary>
    /// 单位状态（主方案 3.5.1）。首版是<strong>单一互斥状态</strong>：
    /// 一个单位在任一逻辑 Tick 上恰好处于其中一个状态。
    ///
    /// 与提交权的关系（00 号规则 4 / 任务包禁止事项第 1 条）：
    /// 本枚举<strong>不</strong>表达"能否提交新动作"。提交权属于任务 05 的
    /// <c>ActorLane</c> 与权威 ScheduleEditor，任何"从 Idle 直接推导可提交"的实现都是越权。
    /// </summary>
    public enum UnitState
    {
        /// <summary>当前没有执行动作；是否接受新计划由 ActorLane 判断，不由本状态推导。</summary>
        Idle = 0,

        /// <summary>攻击前摇（半开区间 <c>[StartTick, ImpactTick)</c>）。</summary>
        Windup = 1,

        /// <summary>攻击后摇（半开区间 <c>[ImpactTick, EndTick)</c>）。</summary>
        Recovery = 2,

        /// <summary>移动中（位置提交属于任务 06 的移动段）。</summary>
        Moving = 3,

        /// <summary>
        /// 普通防御计划中。是否 Active 必须读取计划 Tick 区间；
        /// <strong>本状态不提供任何免伤</strong>。
        /// </summary>
        Guarding = 4,

        /// <summary>
        /// 高阶格挡反应计划中。完全抵抗只在该机会的 TriggerTick 上由匹配载荷产生；
        /// <strong>本状态不提供任何免伤</strong>。
        /// </summary>
        Blocking = 5,

        /// <summary>
        /// 高阶闪避反应计划中。避伤只来自 TriggerTick 原子换格后的空间复核；
        /// <strong>本状态不提供任何免伤</strong>。
        /// </summary>
        Dodging = 6,

        /// <summary>硬直。首版：不承受尚未被特殊交互消解的直接 Hit。</summary>
        Staggered = 7,

        /// <summary>击倒。首版：不承受尚未被特殊交互消解的直接 Hit。</summary>
        KnockedDown = 8,

        /// <summary>起身恢复中。首版预留槽位；同样不承受尚未消解的直接 Hit。</summary>
        Recovering = 9,

        /// <summary>死亡。<strong>终态</strong>：不允许恢复到任何其他状态（包括 Idle）。</summary>
        Dead = 10
    }

    /// <summary>
    /// 状态转换的原因码（稳定字符串，进入事件与诊断）。显式转换与自动到期走同一入口，
    /// 但原因码不同：因此"走同一入口、发同类事件"与"可区分显式/自动"同时成立。
    /// </summary>
    public static class UnitStateTransitionReasons
    {
        /// <summary>外部显式请求（动作阶段边界、控制 Intent 等）。</summary>
        public const string Explicit = "STATE_TRANSITION_EXPLICIT";

        /// <summary>有限持续时间到期触发的自动转换（<see cref="StateTransitionSpec.NextOnExpire"/>）。</summary>
        public const string AutomaticExpiry = "STATE_TRANSITION_AUTOMATIC_EXPIRY";

        /// <summary>致死转换（死亡系统唯一入口）。</summary>
        public const string Death = "STATE_TRANSITION_DEATH";

        /// <summary>稳定原因码全集（诊断与测试用；顺序即文档顺序）。</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            Explicit, AutomaticExpiry, Death
        };
    }

    /// <summary>
    /// 状态机的稳定失败码。
    /// </summary>
    public static class UnitStateCodes
    {
        public const string STATE_TRANSITION_NOT_DECLARED = "STATE_TRANSITION_NOT_DECLARED";
        public const string STATE_TRANSITION_FROM_TERMINAL = "STATE_TRANSITION_FROM_TERMINAL";
        public const string STATE_TRANSITION_SAME_STATE = "STATE_TRANSITION_SAME_STATE";
        public const string STATE_TIMED_TRANSITION_MISSING_NEXT_ON_EXPIRE = "STATE_TIMED_TRANSITION_MISSING_NEXT_ON_EXPIRE";
        public const string STATE_DURATION_MUST_BE_POSITIVE = "STATE_DURATION_MUST_BE_POSITIVE";
        public const string STATE_DURATION_MUST_BE_ZERO_WITHOUT_NEXT_ON_EXPIRE = "STATE_DURATION_MUST_BE_ZERO_WITHOUT_NEXT_ON_EXPIRE";
        public const string STATE_TIMED_EXPIRY_NOT_DECLARED = "STATE_TIMED_EXPIRY_NOT_DECLARED";
        public const string STATE_UNKNOWN = "STATE_UNKNOWN";
    }

    /// <summary>
    /// 一次状态转换的声明（主方案 3.5.1）。
    ///
    /// 持续时间语义（<strong>半开区间</strong>，任务包核心规则）：
    /// <list type="bullet">
    /// <item><see cref="DurationTicks"/> &gt; 0：状态占据
    /// <c>[StateStartTick, StateStartTick + DurationTicks)</c>；到期 Tick
    /// （= <c>StateStartTick + DurationTicks</c>）先转换、不再停留在原状态。</item>
    /// <item><see cref="DurationTicks"/> == 0：无限持续，<c>StateEndTick = long.MaxValue</c>，
    /// 只能由显式转换结束。</item>
    /// </list>
    ///
    /// <see cref="NextOnExpire"/> 只在有限持续时有意义，且<strong>必须显式给出</strong>：
    /// 状态机不做"到期默认回 Idle"的隐式补洞（见
    /// <see cref="UnitStateMachine.ExpireAfter"/> 的显式 Idle 声明用法）。
    /// </summary>
    public sealed record StateTransitionSpec(
        UnitState Target,
        int DurationTicks = 0,
        StateTransitionSpec NextOnExpire = null)
    {
        /// <summary>无限持续的状态转换（无结束 Tick）。</summary>
        public static StateTransitionSpec Open(UnitState target)
            => new StateTransitionSpec(target, 0, null);

        /// <summary>有限持续 + 显式到期目标的状态转换。</summary>
        public static StateTransitionSpec Timed(UnitState target, int durationTicks, UnitState nextOnExpire)
        {
            if (durationTicks <= 0)
                throw new LogicDefinitionException(UnitStateCodes.STATE_DURATION_MUST_BE_POSITIVE,
                    target + "|" + durationTicks);
            return new StateTransitionSpec(target, durationTicks, Open(nextOnExpire));
        }

        /// <summary>合法性的稳定判据（null = 合法）。</summary>
        public string Validate()
        {
            if (!IsKnownState(Target)) return UnitStateCodes.STATE_UNKNOWN;
            if (DurationTicks < 0) return UnitStateCodes.STATE_DURATION_MUST_BE_POSITIVE;
            if (DurationTicks > 0 && NextOnExpire == null)
            {
                // 有限持续却没有到期目标时，"到期后去哪"就没有权威答案，
                // 只能靠隐式默认值补洞——那正是任务包禁止的。
                return UnitStateCodes.STATE_TIMED_TRANSITION_MISSING_NEXT_ON_EXPIRE;
            }
            if (DurationTicks == 0 && NextOnExpire != null)
            {
                // 无限持续的状态永远不会到期，携带到期目标会给出"会到期"的假象。
                return UnitStateCodes.STATE_DURATION_MUST_BE_ZERO_WITHOUT_NEXT_ON_EXPIRE;
            }
            if (NextOnExpire != null && NextOnExpire.NextOnExpire != null)
            {
                // 到期目标自身再带到期目标会形成"隐式链"，使一次自动转换不再是单步事实。
                return UnitStateCodes.STATE_TIMED_TRANSITION_MISSING_NEXT_ON_EXPIRE;
            }
            return NextOnExpire != null ? NextOnExpire.Validate() : null;
        }

        /// <summary>是否为有限持续（有权威结束 Tick）。</summary>
        public bool IsTimed => DurationTicks > 0;

        public static bool IsKnownState(UnitState state)
            => state >= UnitState.Idle && state <= UnitState.Dead;
    }
}
