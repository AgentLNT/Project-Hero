using System;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Status
{
    /// <summary>
    /// 持续效果的<strong>运行时</strong>规格（任务 04「必须产出」5 的最小内核）。
    ///
    /// 与配置侧的分工（避免两套权威）：
    /// <list type="bullet">
    /// <item><see cref="ProjectHero.Logic.Definitions.StatusEffectSpec"/>（任务 02B，进入
    /// <c>BattleDefinition</c> 与定义哈希）是<strong>效果目录</strong>：稳定 ID、
    /// 显示键、最大叠层、是否减益。它是配置侧的唯一权威。</item>
    /// <item>本类型是<strong>运行时</strong>规格：把目录 ID 与"本场如何推进"绑在一起
    /// （持续 Tick 数 + 载荷）。它<strong>不</strong>重新定义 ID 命名空间，
    /// 也不复制目录字段——两套东西靠 <see cref="StatusEffectSpecId"/> 关联。</item>
    /// </list>
    ///
    /// <strong>配置类型 ≠ 实例身份</strong>：
    /// <list type="bullet">
    /// <item><see cref="StatusEffectSpecId"/> 是配置身份，可被同时施加到多个单位、多次施加。</item>
    /// <item><c>EffectId</c> 是实例身份，由 <c>LogicIdGenerator.NextEffectId()</c> 分配，
    /// 从 1 起、单场单调递增、不复用；每个可独立移除/到期的实例恰好拥有一个，
    /// 并进入规范化快照。</item>
    /// </list>
    ///
    /// 完整配置扩展（资产定义、来源动作绑定、表现映射）留到第六阶段；
    /// 但<strong>后续任务必须复用本内核</strong>，不得再建立第二套效果推进器。
    /// </summary>
    public sealed record StatusEffectRuntimeSpec(
        StatusEffectSpecId SpecId,
        int DurationTicks,
        StatusEffectPayload Payload)
    {
        public const string EFFECT_SPEC_ID_INVALID = "EFFECT_SPEC_ID_INVALID";
        public const string EFFECT_SPEC_DURATION_INVALID = "EFFECT_SPEC_DURATION_INVALID";
        public const string EFFECT_SPEC_PAYLOAD_MISSING = "EFFECT_SPEC_PAYLOAD_MISSING";

        /// <summary>合法性的稳定判据（null = 合法）。持续时长必须严格为正。</summary>
        public string Validate()
        {
            if (string.IsNullOrEmpty(SpecId.Value)) return EFFECT_SPEC_ID_INVALID;
            if (DurationTicks <= 0) return EFFECT_SPEC_DURATION_INVALID;
            if (Payload == null) return EFFECT_SPEC_PAYLOAD_MISSING;
            return null;
        }

        /// <summary>
        /// 有限持续（无载荷）的常用构造：只占用实例身份与生命周期，不改变任何单位事实。
        /// 它让"效果推进器本身"可以被独立测试（到期、排序、延迟队列），
        /// 而不需要伪造伤害或状态改动。
        /// </summary>
        public static StatusEffectRuntimeSpec Timed(StatusEffectSpecId specId, int durationTicks)
            => new StatusEffectRuntimeSpec(specId, durationTicks, NoOpEffectPayload.Instance);
    }

    /// <summary>
    /// 持续效果的载荷（最小内核）。载荷<strong>只声明"改什么"</strong>，
    /// 不直接触碰单位对象：所有写入都由 <see cref="BuffSystem"/> 在阶段 1 内经
    /// 统一入口提交，因此不存在"效果回调直接改活动集合"的路径。
    /// </summary>
    public abstract record StatusEffectPayload
    {
        /// <summary>载荷类型名（稳定、非本地化；进入诊断文本）。</summary>
        public abstract string Kind { get; }
    }

    /// <summary>无操作载荷：只占用实例身份与生命周期。</summary>
    public sealed record NoOpEffectPayload : StatusEffectPayload
    {
        public static readonly NoOpEffectPayload Instance = new NoOpEffectPayload();

        public override string Kind => "noop";
    }

    /// <summary>
    /// 计时伤害载荷（阶段 1 提交）：效果 Tick 到达时对宿主单位写一次确定伤害。
    ///
    /// 它是"持续效果可以在新命令处理之前致死"这条阶段顺序的必要载体
    /// （必需测试 <c>DamageOverTimeDeathOccursBeforeNewCommands</c>）。
    /// 伤害数值直接使用 Q10 生命整数（与 <c>UnitSnapshot.HealthQ10</c> 同域），
    /// 因此浮点不进入逻辑世界。
    /// </summary>
    public sealed record DamageOverTimeEffectPayload(int DamagePerTickQ10) : StatusEffectPayload
    {
        public const string EFFECT_DOT_DAMAGE_INVALID = "EFFECT_DOT_DAMAGE_INVALID";

        public override string Kind => "damage_over_time";

        /// <summary>合法性的稳定判据（null = 合法）。伤害必须严格为正。</summary>
        public string Validate()
            => DamagePerTickQ10 > 0 ? null : EFFECT_DOT_DAMAGE_INVALID;
    }
}
