namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// 全局排程（<c>ActorLane</c> / <c>ScheduleEditor</c> / <c>ScheduleEvaluator</c> /
    /// <c>ActionStartGate</c> / 统一终态协调器）的<strong>稳定失败码与拒绝码</strong>。
    ///
    /// 这些字符串进入命令拒绝事件、自动延期事件与诊断；<strong>不得改名或复用</strong>。
    /// 其中 <see cref="STALE_SCHEDULE_REVISION"/> 与
    /// <see cref="SCHEDULE_EDIT_CONFLICT_IN_BATCH"/> 是任务包明确要求稳定化的两个拒绝码。
    /// </summary>
    public static class ScheduleCodes
    {
        // —— Lane 结构 ——

        public const string SCHEDULE_LANE_UNKNOWN_UNIT = "SCHEDULE_LANE_UNKNOWN_UNIT";
        public const string SCHEDULE_LANE_QUEUE_LIMIT_EXCEEDED = "SCHEDULE_LANE_QUEUE_LIMIT_EXCEEDED";
        public const string SCHEDULE_LANE_SUBMISSION_LOCKED = "SCHEDULE_LANE_SUBMISSION_LOCKED";
        public const string SCHEDULE_LANE_OWNER_MISMATCH = "SCHEDULE_LANE_OWNER_MISMATCH";
        public const string SCHEDULE_PLAN_ALREADY_IN_LANE = "SCHEDULE_PLAN_ALREADY_IN_LANE";
        public const string SCHEDULE_PLAN_NOT_IN_LANE = "SCHEDULE_PLAN_NOT_IN_LANE";
        public const string SCHEDULE_LANE_OVERLAP = "SCHEDULE_LANE_OVERLAP";
        public const string SCHEDULE_PLAN_TERMINAL_IN_LANE = "SCHEDULE_PLAN_TERMINAL_IN_LANE";

        // —— 计划可编辑性 ——

        public const string SCHEDULE_PLAN_NOT_EDITABLE = "SCHEDULE_PLAN_NOT_EDITABLE";
        public const string SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED = "SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED";
        public const string SCHEDULE_REACTION_CANNOT_BE_EDITED = "SCHEDULE_REACTION_CANNOT_BE_EDITED";
        public const string SCHEDULE_REACTION_INTERVAL_IS_FIXED = "SCHEDULE_REACTION_INTERVAL_IS_FIXED";

        // —— 事务与修订 ——

        public const string STALE_SCHEDULE_REVISION = "STALE_SCHEDULE_REVISION";
        public const string SCHEDULE_EDIT_CONFLICT_IN_BATCH = "SCHEDULE_EDIT_CONFLICT_IN_BATCH";
        public const string SCHEDULE_BATCH_TOO_LARGE = "SCHEDULE_BATCH_TOO_LARGE";
        public const string SCHEDULE_DEPENDENCY_CLOSURE_TOO_LARGE = "SCHEDULE_DEPENDENCY_CLOSURE_TOO_LARGE";
        public const string SCHEDULE_HORIZON_EXCEEDED = "SCHEDULE_HORIZON_EXCEEDED";
        public const string SCHEDULE_OPERATION_DUPLICATE = "SCHEDULE_OPERATION_DUPLICATE";
        public const string SCHEDULE_OPERATION_INVALID = "SCHEDULE_OPERATION_INVALID";

        /// <summary>
        /// 受影响 Move 链需要重算路径，但当前求值器绑定的是
        /// <c>NotImplementedMovementPathCalculator</c>（任务 06 之前的占位端口）。
        ///
        /// 冻结语义（任务 05 交接记录 §27.2 硬前置条件 2）：该默认实现<strong>真 fail-closed</strong>——
        /// 它在<strong>被真正要求重算</strong>时以本码失败，因此"忘记注入真实路径计算器"
        /// 会在第一次 ripple 上<strong>显式失败</strong>，而不是静默保持旧路径的恒等实现。
        /// 它<strong>不</strong>返回 <c>null</c>（<c>null</c> 在 <c>ScheduleEvaluator</c> 里表示
        /// "该计划无需重算"，把两者混同正是被修复的缺陷 D1）。
        /// </summary>
        public const string SCHEDULE_PATH_CALCULATOR_NOT_IMPLEMENTED = "SCHEDULE_PATH_CALCULATOR_NOT_IMPLEMENTED";

        /// <summary>
        /// 请求起点<strong>早于</strong>命令的目标 Tick（即"把计划排进过去"）。
        ///
        /// 冻结语义（任务包「必需测试」<c>RequestedStartBeforeCommandTargetTickRejectsWholeBatch</c>）：
        /// 它必须以稳定码<strong>整批</strong>拒绝，而不是"放在过去等它自然启动"——
        /// 排程阶段只处理"目标 Tick=T 的冻结排程命令"，一个起点早于 T 的普通计划
        /// 永远不可能到达它自己的到期门禁，会退化成永久的僵尸 Editable 计划。
        /// 只允许"直接移动的计划早于其<em>旧投影</em>"，不允许早于当前 Tick。
        /// </summary>
        public const string SCHEDULE_START_TICK_BEFORE_COMMAND_TICK = "SCHEDULE_START_TICK_BEFORE_COMMAND_TICK";

        // —— 目标关系（创建 / 排程候选 / 启动门禁三处共用同一实现）——

        public const string SCHEDULE_PRIMARY_TARGET_REQUIRED = "SCHEDULE_PRIMARY_TARGET_REQUIRED";
        public const string SCHEDULE_PRIMARY_TARGET_DEAD = "SCHEDULE_PRIMARY_TARGET_DEAD";
        public const string SCHEDULE_PRIMARY_TARGET_RELATION_REJECTED = "SCHEDULE_PRIMARY_TARGET_RELATION_REJECTED";
        public const string SCHEDULE_ACTION_NOT_IN_ACTION_SET = "SCHEDULE_ACTION_NOT_IN_ACTION_SET";

        // —— 锚点 ——

        public const string SCHEDULE_ANCHOR_UNKNOWN = "SCHEDULE_ANCHOR_UNKNOWN";
        public const string SCHEDULE_ANCHOR_CYCLIC = "SCHEDULE_ANCHOR_CYCLIC";
        public const string SCHEDULE_ANCHOR_AFTER_SELF = "SCHEDULE_ANCHOR_AFTER_SELF";

        // —— 启动门禁 ——

        public const string SCHEDULE_START_GATE_NOT_AT_DUE_TICK = "SCHEDULE_START_GATE_NOT_AT_DUE_TICK";
        public const string SCHEDULE_RETRY_TICK_NOT_FINITE = "SCHEDULE_RETRY_TICK_NOT_FINITE";
        public const string SCHEDULE_RETRY_TICK_NOT_IN_FUTURE = "SCHEDULE_RETRY_TICK_NOT_IN_FUTURE";
        public const string SCHEDULE_START_GATE_COMMIT_INCONSISTENT = "SCHEDULE_START_GATE_COMMIT_INCONSISTENT";
        public const string SCHEDULE_START_GATE_RESOURCE_COMMIT_MISSING = "SCHEDULE_START_GATE_RESOURCE_COMMIT_MISSING";

        // —— 终态协调器 ——

        public const string SCHEDULE_TERMINAL_REASON_REQUIRED = "SCHEDULE_TERMINAL_REASON_REQUIRED";
        public const string SCHEDULE_TERMINAL_PLAN_UNKNOWN = "SCHEDULE_TERMINAL_PLAN_UNKNOWN";
        public const string SCHEDULE_TERMINAL_PLAN_NOT_IN_LANE = "SCHEDULE_TERMINAL_PLAN_NOT_IN_LANE";
        public const string SCHEDULE_TERMINAL_CLEANUP_PARTICIPANT_INVALID = "SCHEDULE_TERMINAL_CLEANUP_PARTICIPANT_INVALID";
        public const string SCHEDULE_TERMINAL_ORPHAN_ACTIVE_REFERENCE = "SCHEDULE_TERMINAL_ORPHAN_ACTIVE_REFERENCE";

        // —— 反应机会 ——

        public const string REACTION_OPPORTUNITY_SOURCE_NOT_RUNNING = "REACTION_OPPORTUNITY_SOURCE_NOT_RUNNING";
        public const string REACTION_OPPORTUNITY_SOURCE_NOT_TELEGRAPHED = "REACTION_OPPORTUNITY_SOURCE_NOT_TELEGRAPHED";
        public const string REACTION_OPPORTUNITY_SOURCE_NOT_REACTABLE = "REACTION_OPPORTUNITY_SOURCE_NOT_REACTABLE";
        public const string REACTION_OPPORTUNITY_LEAD_BELOW_MINIMUM = "REACTION_OPPORTUNITY_LEAD_BELOW_MINIMUM";
        public const string REACTION_OPPORTUNITY_NO_COMPATIBLE_OPTION = "REACTION_OPPORTUNITY_NO_COMPATIBLE_OPTION";
        public const string REACTION_OPPORTUNITY_ALREADY_CLOSED = "REACTION_OPPORTUNITY_ALREADY_CLOSED";
        public const string REACTION_OPPORTUNITY_UNKNOWN = "REACTION_OPPORTUNITY_UNKNOWN";
        public const string REACTION_OPTION_UNKNOWN = "REACTION_OPTION_UNKNOWN";
        public const string REACTION_OPTION_DEADLINE_ELAPSED = "REACTION_OPTION_DEADLINE_ELAPSED";
        public const string REACTION_COMMAND_MUST_NOT_PROVIDE_TRIGGER_TICK = "REACTION_COMMAND_MUST_NOT_PROVIDE_TRIGGER_TICK";
        public const string REACTION_PLAN_IS_BORN_LOCKED = "REACTION_PLAN_IS_BORN_LOCKED";
        public const string REACTION_PLAN_REQUIRES_SOURCE_THREAT = "REACTION_PLAN_REQUIRES_SOURCE_THREAT";
        public const string REACTION_TRIGGER_TICK_MISMATCH = "REACTION_TRIGGER_TICK_MISMATCH";
        public const string REACTION_SOURCE_THREAT_NOT_TERMINATED = "REACTION_SOURCE_THREAT_NOT_TERMINATED";
        public const string REACTION_RESERVATION_RELEASE_MISSING_SINK = "REACTION_RESERVATION_RELEASE_MISSING_SINK";
    }
}
