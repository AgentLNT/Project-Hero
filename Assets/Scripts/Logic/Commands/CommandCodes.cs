namespace ProjectHero.Logic.Commands
{
    /// <summary>
    /// 命令入口/网关的稳定失败与拒绝码（任务 03 冻结）。
    ///
    /// 约定：
    /// <list type="bullet">
    /// <item><c>*_REJECTED</c> 与 <c>DUPLICATE_*</c>/<c>*_REGRESSION</c> 是
    /// <strong>入口级</strong>拒绝：这些请求<strong>不会</strong>获得 <c>CommandSequence</c>，
    /// 每个非法规范键只产生一个按组键排序的拒绝事件，不选择也不回显某个成员载荷。</item>
    /// <item><c>COMMAND_*</c> 是入口的编程错误（调用方式非法），以
    /// <see cref="ProjectHero.Logic.LogicDefinitionException"/> 立即失败，不静默忽略。</item>
    /// </list>
    /// 全部码为稳定字符串，不得改名或复用。
    /// </summary>
    public static class CommandCodes
    {
        // —— 入口注册 ——
        /// <summary>外部入口试图注册为 <c>CommandSourceKind.System</c>。</summary>
        public const string EXTERNAL_INGRESS_SYSTEM_SOURCE_REJECTED = "EXTERNAL_INGRESS_SYSTEM_SOURCE_REJECTED";
        /// <summary>同一个 <c>ControllerId</c> 重复注册入口。</summary>
        public const string COMMAND_INGRESS_DUPLICATE_CONTROLLER = "COMMAND_INGRESS_DUPLICATE_CONTROLLER";
        /// <summary>入口的 ControllerId 非法（空或格式不合法）。</summary>
        public const string COMMAND_INGRESS_CONTROLLER_INVALID = "COMMAND_INGRESS_CONTROLLER_INVALID";
        /// <summary>
        /// 外部调用者试图经公开路径使用 <c>System</c> 来源入口：系统来源只有 Logic 内部
        /// 稳定系统可以提交（<c>SubmitSystemInternal</c>，非 public）。
        /// </summary>
        public const string SYSTEM_SOURCE_INTERNAL_ONLY = "SYSTEM_SOURCE_INTERNAL_ONLY";

        // —— 编程错误（抛异常）——
        public const string COMMAND_REQUEST_NULL = "COMMAND_REQUEST_NULL";
        public const string COMMAND_SCOPE_NULL = "COMMAND_SCOPE_NULL";
        public const string COMMAND_PAYLOAD_NULL = "COMMAND_PAYLOAD_NULL";
        public const string COMMAND_TARGET_TICK_NEGATIVE = "COMMAND_TARGET_TICK_NEGATIVE";
        public const string COMMAND_INGRESS_TICK_NOT_ADVANCING = "COMMAND_INGRESS_TICK_NOT_ADVANCING";
        public const string COMMAND_BATCH_TICK_MISMATCH = "COMMAND_BATCH_TICK_MISMATCH";

        // —— 入口级拒绝（获得拒绝事件，不获得 CommandSequence）——
        /// <summary>同一规范键在冻结批次里出现多次 → 整个碰撞组拒绝，没有赢家。</summary>
        public const string DUPLICATE_COMMAND_ORDINAL = "DUPLICATE_COMMAND_ORDINAL";
        /// <summary>序号低于入口已冻结水位（回退）。</summary>
        public const string COMMAND_ORDINAL_REGRESSION = "COMMAND_ORDINAL_REGRESSION";
        /// <summary>记录事实注入使用了非 Player 来源（首版权威回放只接受 Player）。</summary>
        public const string REPLAY_NON_PLAYER_SOURCE_NOT_AUTHORITATIVE = "REPLAY_NON_PLAYER_SOURCE_NOT_AUTHORITATIVE";
        /// <summary>记录事实的 ProducerOrdinal 非法（必须 &gt;= 1）。</summary>
        public const string COMMAND_ORDINAL_INVALID = "COMMAND_ORDINAL_INVALID";
        /// <summary>批次冻结后到达、目标 Tick 已经被冻结（不得回写本 Tick，也不得静默丢弃）。</summary>
        public const string LATE_REQUEST_FOR_FROZEN_TICK = "LATE_REQUEST_FOR_FROZEN_TICK";
        /// <summary>请求目标 Tick 在推进过程中被跳过，永远等不到对应 Step。</summary>
        public const string STALE_REQUEST_TARGET_TICK_SKIPPED = "STALE_REQUEST_TARGET_TICK_SKIPPED";
        /// <summary>scope 判别与 payload 判别不一致（普通编辑/窗口/反应三类必须精确匹配）。</summary>
        public const string SCOPE_PAYLOAD_MISMATCH = "SCOPE_PAYLOAD_MISMATCH";
        /// <summary>载荷内容结构非法（空操作列表、Dodge 缺目的格、Block 携带目的格等）。</summary>
        public const string PAYLOAD_STRUCTURALLY_INVALID = "PAYLOAD_STRUCTURALLY_INVALID";

        // —— 处理器级拒绝（已获得 CommandSequence 后由权威状态判定）——
        /// <summary>任务 03 占位：排程/窗口/反应处理器由任务 05–09 接入。</summary>
        public const string COMMAND_PROCESSOR_NOT_IMPLEMENTED = "COMMAND_PROCESSOR_NOT_IMPLEMENTED";
        /// <summary>战斗已结束：新命令在入口被稳定拒绝（不进入 Tick 桶）。</summary>
        public const string BATTLE_ALREADY_ENDED = "BATTLE_ALREADY_ENDED";
        /// <summary>
        /// 战斗在本 Tick 的<strong>命令阶段之前</strong>结束（阶段 2 的死亡/胜负）。
        /// 已冻结的本 Tick 请求仍获得 <c>CommandSequence</c>，但按本码稳定拒绝——
        /// 既不静默丢弃，也不进入命令处理器（主方案 3.4.3 的对应分支）。
        /// </summary>
        public const string COMMAND_BATTLE_ENDED_BEFORE_COMMAND_PHASE = "BATTLE_ENDED_BEFORE_COMMAND_PHASE";
    }

    /// <summary>
    /// 来源优先级固定映射（任务 03「核心契约」冻结）：<c>Player = 0</c>、<c>AI = 10</c>、
    /// <c>System = 20</c>。它<strong>由入口绑定的 <c>CommandSourceKind</c> 派生</strong>，
    /// 生产者不能填写；<c>System</c> 只允许 Logic 内部稳定来源。
    ///
    /// <see cref="MappingVersion"/> 参与规范化快照哈希：改变映射就必须同时提升
    /// <c>RulesVersion</c>、重算定义哈希并更新 golden snapshot。
    /// </summary>
    public static class CommandSourcePriority
    {
        public const string MappingVersion = "source-priority-v1";

        public const int Player = 0;
        public const int Ai = 10;
        public const int System = 20;

        public static int Of(Definitions.CommandSourceKind sourceKind)
        {
            switch (sourceKind)
            {
                case Definitions.CommandSourceKind.Player: return Player;
                case Definitions.CommandSourceKind.Ai: return Ai;
                case Definitions.CommandSourceKind.System: return System;
                default:
                    throw new ProjectHero.Logic.LogicDefinitionException(
                        CommandCodes.COMMAND_INGRESS_CONTROLLER_INVALID,
                        "unknown source kind: " + sourceKind);
            }
        }
    }
}
