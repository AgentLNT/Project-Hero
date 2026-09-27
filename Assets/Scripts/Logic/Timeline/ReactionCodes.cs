namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// <c>ReactionOpportunitySystem</c> / <c>ReactionPlanner</c> 的稳定失败码与拒绝码。
    /// 它们进入命令拒绝事件、机会关闭原因与诊断；<strong>不得改名或复用</strong>。
    /// </summary>
    public static class ReactionCodes
    {
        public const string OPPORTUNITY_ID_INVALID = "REACTION_OPPORTUNITY_ID_INVALID";
        public const string OPPORTUNITY_NOT_OPEN = "REACTION_OPPORTUNITY_NOT_OPEN";
        public const string OPPORTUNITY_DEFENDER_MISMATCH = "REACTION_OPPORTUNITY_DEFENDER_MISMATCH";
        public const string OPPORTUNITY_OPTION_NOT_OFFERED = "REACTION_OPPORTUNITY_OPTION_NOT_OFFERED";
        public const string OPPORTUNITY_SOURCE_THREAT_UNKNOWN = "REACTION_OPPORTUNITY_SOURCE_THREAT_UNKNOWN";
        public const string OPPORTUNITY_ALREADY_ACCEPTED = "REACTION_OPPORTUNITY_ALREADY_ACCEPTED";
        public const string OPTION_DEADLINE_ELAPSED = "REACTION_OPTION_DEADLINE_ELAPSED";
        public const string OPTION_NOT_PUBLISHED = "REACTION_OPTION_NOT_PUBLISHED";
        public const string REACTION_ACTION_NOT_BLOCK_OR_DODGE = "REACTION_ACTION_NOT_BLOCK_OR_DODGE";
        public const string REACTION_LANE_INTERVAL_OCCUPIED = "REACTION_LANE_INTERVAL_OCCUPIED";
        public const string REACTION_LANE_SUBMISSION_LOCKED = "REACTION_LANE_SUBMISSION_LOCKED";
        public const string REACTION_SOURCE_THREAT_ALREADY_TERMINAL = "REACTION_SOURCE_THREAT_ALREADY_TERMINAL";
        public const string REACTION_TRIGGER_REQUIRES_SWAP_TRANSACTION = "REACTION_TRIGGER_REQUIRES_SWAP_TRANSACTION";
        public const string REACTION_RESERVATION_RELEASE_SINK_MISSING = "REACTION_RESERVATION_RELEASE_SINK_MISSING";
        public const string REACTION_TELEGRAPH_TICK_MISMATCH = "REACTION_TELEGRAPH_TICK_MISMATCH";
    }

    /// <summary>机会关闭原因的稳定字符串码（进入 <c>ReactionOpportunityClosedEvent</c> 与快照）。</summary>
    public static class ReactionCloseReasons
    {
        /// <summary>最后一个选项过期后自然关闭。</summary>
        public const string AllOptionsExpired = "REACTION_CLOSE_ALL_OPTIONS_EXPIRED";

        /// <summary>来源攻击在 TriggerTick 前终止。</summary>
        public const string SourceThreatCancelled = "REACTION_CLOSE_SOURCE_THREAT_CANCELLED";

        /// <summary>战斗结束（唯一 Finalizer 关闭全部机会）。</summary>
        public const string BattleEnded = "REACTION_CLOSE_BATTLE_ENDED";
    }

    /// <summary>逐选项过期/关闭的稳定原因码。</summary>
    public static class ReactionOptionOutcomes
    {
        /// <summary>选项在其截止 Tick 的命令阶段结束后过期。</summary>
        public const string Expired = "REACTION_OPTION_EXPIRED";

        /// <summary>选项被接受（成为绑定反应计划）。</summary>
        public const string Accepted = "REACTION_OPTION_ACCEPTED";

        /// <summary>来源威胁取消导致绑定反应被终止。</summary>
        public const string SourceCancelled = "REACTION_OPTION_SOURCE_CANCELLED";
    }
}
