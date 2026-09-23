namespace ProjectHero.Logic.Combat
{
    /// <summary>整数 Tick 舍入方式（冻结：RoundHalfUp）。进入 BattleDefinitionHash。</summary>
    public enum TickRoundingMode
    {
        RoundHalfUp = 0
    }

    /// <summary>
    /// 版本化战斗规则（任务 02 冻结的全部版本化定义输入）：
    /// 攻击的 ReferenceActionSpeed = 20 / DefaultAttackRecoveryTicks = 30（替代运行时硬编码 0.5f）、
    /// 移动的 ReferenceMoveSpeed = 20 / 每权重单位 Tick、RoundHalfUp 舍入、
    /// Guard 的 Windup/Active/Recovery、Block/Dodge 的 ReactionWindup/Recovery、
    /// Stagger = 30 Tick / Knockdown = 60 Tick 自动恢复（01B 拍板 B2，状态机实现留任务 04）、
    /// PathCostRules / PathSearchRules 与 ForcedDisplacementProtocolVersion。
    /// 全部进入 BattleDefinitionHash；运行时解析由任务 05/06 完成。
    /// </summary>
    public sealed record BattleRules(
        int TicksPerSecond,
        int ReferenceActionSpeed,
        int ReferenceMoveSpeed,
        int DefaultAttackRecoveryTicks,
        TickRoundingMode TickRoundingMode,
        int StaggerAutoRecoveryTicks,
        int KnockdownAutoRecoveryTicks,
        PathCostRules PathCostRules,
        PathSearchRules PathSearchRules,
        ForcedDisplacementProtocolVersion ForcedDisplacementProtocolVersion,
        int MaxAutomaticDeferralsPerPlan = BattleRules.FrozenMaxAutomaticDeferralsPerPlan)
    {
        public const string TICKS_PER_SECOND_INVALID = "TICKS_PER_SECOND_INVALID";
        public const string REFERENCE_ACTION_SPEED_INVALID = "REFERENCE_ACTION_SPEED_INVALID";
        public const string REFERENCE_MOVE_SPEED_INVALID = "REFERENCE_MOVE_SPEED_INVALID";
        public const string DEFAULT_ATTACK_RECOVERY_INVALID = "DEFAULT_ATTACK_RECOVERY_INVALID";
        public const string STAGGER_AUTO_RECOVERY_INVALID = "STAGGER_AUTO_RECOVERY_INVALID";
        public const string KNOCKDOWN_AUTO_RECOVERY_INVALID = "KNOCKDOWN_AUTO_RECOVERY_INVALID";
        public const string FORCED_DISPLACEMENT_PROTOCOL_INVALID = "FORCED_DISPLACEMENT_PROTOCOL_INVALID";
        /// <summary>任务 02B 冻结：普通计划自动延期次数上限必须非负。</summary>
        public const string MAX_AUTOMATIC_DEFERRALS_INVALID = "MAX_AUTOMATIC_DEFERRALS_INVALID";

        /// <summary>
        /// Block 对合格接触的完全抵抗固定为 1024（规则固定值），Authoring 不提供可降低该值的字段。
        /// </summary>
        public const int FullBlockResistanceQ10 = 1024;

        public const int FrozenReferenceActionSpeed = 20;
        public const int FrozenReferenceMoveSpeed = 20;
        public const int FrozenDefaultAttackRecoveryTicks = 30;
        public const int FrozenStaggerAutoRecoveryTicks = 30;
        public const int FrozenKnockdownAutoRecoveryTicks = 60;

        /// <summary>
        /// 首版冻结的"每个普通计划允许的系统自动延期次数上限"（00 号规则 29）。
        /// 任务 02B 只负责声明该版本化常量、校验其非负并让它进入 BattleDefinitionHash；
        /// 实际延期事务、RetryAtTick 与失败判定由任务 05 实现。取值必须能与
        /// <see cref="PathSearchRules.MaxPathEdges"/> 量级共同支撑一场战斗，不得为 0（0 会让任何
        /// 临时阻塞直接终止计划），也不得无上限。
        /// </summary>
        public const int FrozenMaxAutomaticDeferralsPerPlan = 8;

        /// <summary>
        /// 最大排程视野（Tick）。任务 02B 只冻结并哈希该版本化常量，
        /// 供任务 05 的排程评估与任务 07 的窗口预算共同使用；不改变任何窗口语义。
        /// </summary>
        public const int FrozenMaxScheduleHorizonTicks = 60 * 600; // 600 秒 @ 60 Tick/s

        /// <summary>最大排程视野（Tick），与 <see cref="FrozenMaxScheduleHorizonTicks"/> 同值。</summary>
        public int MaxScheduleHorizonTicks => FrozenMaxScheduleHorizonTicks;

        /// <summary>首版冻结规则实例（TicksPerSecond = 60，RoundHalfUp，1/2 路径权重，4096/256/192，SimultaneousStepV1）。</summary>
        public static readonly BattleRules FrozenV1 = new BattleRules(
            TicksPerSecond: 60,
            ReferenceActionSpeed: FrozenReferenceActionSpeed,
            ReferenceMoveSpeed: FrozenReferenceMoveSpeed,
            DefaultAttackRecoveryTicks: FrozenDefaultAttackRecoveryTicks,
            TickRoundingMode: TickRoundingMode.RoundHalfUp,
            StaggerAutoRecoveryTicks: FrozenStaggerAutoRecoveryTicks,
            KnockdownAutoRecoveryTicks: FrozenKnockdownAutoRecoveryTicks,
            PathCostRules: PathCostRules.FrozenV1,
            PathSearchRules: PathSearchRules.FrozenV1,
            ForcedDisplacementProtocolVersion: new ForcedDisplacementProtocolVersion(
                ForcedDisplacementProtocolVersion.SimultaneousStepV1));

        /// <summary>返回首个校验错误码（null = 通过）。</summary>
        public string Validate()
        {
            if (TicksPerSecond <= 0) return TICKS_PER_SECOND_INVALID;
            if (ReferenceActionSpeed <= 0) return REFERENCE_ACTION_SPEED_INVALID;
            if (ReferenceMoveSpeed <= 0) return REFERENCE_MOVE_SPEED_INVALID;
            if (DefaultAttackRecoveryTicks <= 0) return DEFAULT_ATTACK_RECOVERY_INVALID;
            if (StaggerAutoRecoveryTicks < 0) return STAGGER_AUTO_RECOVERY_INVALID;
            if (KnockdownAutoRecoveryTicks < 0) return KNOCKDOWN_AUTO_RECOVERY_INVALID;
            if (MaxAutomaticDeferralsPerPlan < 0) return MAX_AUTOMATIC_DEFERRALS_INVALID;
            if (!ForcedDisplacementProtocolVersion.IsValid) return FORCED_DISPLACEMENT_PROTOCOL_INVALID;
            string pathCostError = PathCostRules == null ? PathCostRules.PATH_COST_WEIGHT_INVALID : PathCostRules.Validate();
            if (pathCostError != null) return pathCostError;
            return PathSearchRules == null ? PathSearchCodes.PATH_SEARCH_RULE_INVALID : PathSearchRules.Validate();
        }

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("rules.ticks_per_second", TicksPerSecond);
            writer.Write("rules.reference_action_speed", ReferenceActionSpeed);
            writer.Write("rules.reference_move_speed", ReferenceMoveSpeed);
            writer.Write("rules.default_attack_recovery_ticks", DefaultAttackRecoveryTicks);
            writer.Write("rules.tick_rounding_mode", TickRoundingMode.ToString() + ":" + (int)TickRoundingMode);
            writer.Write("rules.stagger_auto_recovery_ticks", StaggerAutoRecoveryTicks);
            writer.Write("rules.knockdown_auto_recovery_ticks", KnockdownAutoRecoveryTicks);
            writer.Write("rules.full_block_resistance_q10", FullBlockResistanceQ10);
            writer.Write("rules.max_automatic_deferrals_per_plan", MaxAutomaticDeferralsPerPlan);
            writer.Write("rules.max_schedule_horizon_ticks", MaxScheduleHorizonTicks);
            PathCostRules.WriteHashComponents(writer);
            PathSearchRules.WriteHashComponents(writer);
            ForcedDisplacementProtocolVersion.WriteHashComponents(writer);
            ForcedDisplacementEncoding.WriteHashComponents(writer);
        }
    }

    /// <summary>
    /// 01B 拍板 B3 的反应费用/时序冻结值（供兼容切片与测试示例使用）。
    /// Block/Dodge 动作的 RecoveryTicks 未拍板，本任务不为兼容切片构造 Block/Dodge 动作定义。
    /// </summary>
    public static class FrozenDesignValues
    {
        public const int BlockAdrenalineCost = 2;
        public const int DodgeAdrenalineCost = 1;
        public const int BlockReactionWindupTicks = 60;   // 旧 BlockDuration = 1.0s
        public const int DodgeReactionWindupTicks = 30;   // 旧 DodgeDuration = 0.5s
    }
}
