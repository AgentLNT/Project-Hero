using ProjectHero.Logic.Combat;

namespace ProjectHero.Logic.Actions
{
    /// <summary>抽象动作时序。每种动作族必须有自己的权威 TimingSpec，不得互相套用。</summary>
    public abstract record ActionTimingSpec;

    /// <summary>
    /// 攻击时序（主方案 3.1.2）：「速度修正前摇 → 命中 → 固定后摇」。
    /// BaseWindupTicks 由 Authoring 边界的 BaseWindupSeconds × TicksPerSecond 只量化一次得到；
    /// 运行时解析 ResolvedWindupTicks = max(1, RoundHalfUp(BaseWindupTicks × ReferenceActionSpeed / ActionSpeed))。
    /// RecoveryTicks 不受 ActionSpeed 缩短；旧资产缺后摇时使用 DefaultAttackRecoveryTicks = 30。
    /// </summary>
    public sealed record AttackTimingSpec(int BaseWindupTicks, int RecoveryTicks) : ActionTimingSpec
    {
        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("attack_timing.base_windup_ticks", BaseWindupTicks);
            writer.Write("attack_timing.recovery_ticks", RecoveryTicks);
        }
    }

    public sealed record GuardTimingSpec(int DurationTicks) : ActionTimingSpec
    {
        public void WriteHashComponents(CanonicalHashWriter writer)
            => writer.Write("guard_timing.duration_ticks", DurationTicks);
    }

    /// <summary>
    /// Move 时序：BaseStepTicks = 每路径权重单位的 Tick 基准。
    /// 运行时解析 ResolvedBaseStepTicks = max(1, RoundHalfUp(BaseStepTicks × ReferenceMoveSpeed / MoveSpeed))；
    /// 每条边时长 = StepWeightUnits(edge.Direction) × ResolvedBaseStepTicks（受检整数）。
    /// MoveSpeed 不参与选路。旧逐边 0.2-4.0s 浮点 Clamp 不属于新规则。
    /// </summary>
    public sealed record MoveTimingSpec(int BaseStepTicks, int RecoveryTicks) : ActionTimingSpec
    {
        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("move_timing.base_step_ticks", BaseStepTicks);
            writer.Write("move_timing.recovery_ticks", RecoveryTicks);
        }
    }

    public sealed record BlockReactionTimingSpec(int DurationTicks) : ActionTimingSpec
    {
        public void WriteHashComponents(CanonicalHashWriter writer)
            => writer.Write("block_reaction.duration_ticks", DurationTicks);
    }

    public sealed record DodgeReactionTimingSpec(int DurationTicks) : ActionTimingSpec
    {
        public void WriteHashComponents(CanonicalHashWriter writer)
            => writer.Write("dodge_reaction.duration_ticks", DurationTicks);
    }
}
