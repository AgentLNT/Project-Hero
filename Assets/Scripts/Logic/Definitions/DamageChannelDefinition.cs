using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Definitions
{
    /// <summary>
    /// 伤害通道定义（主方案 0.4.2.1 / 任务包「必须产出」2）。
    /// 通道本身只声明稳定 ID 与"默认伤害标签目录"；它不携带任何单位的抵抗数值
    /// （抵抗属于 <c>UnitDefinition.BaseDamageResistanceQ10</c> 与具体动作载荷）。
    ///
    /// <see cref="DefaultTags"/> 是 Authoring 在资产没有显式标签时的声明值本身，
    /// 不是 Logic 运行时的隐式默认：Logic 运行时只读取已经写在
    /// <c>DamageComponentSpec.Tags</c> 上的最终值，不会因为通道 ID 再补标签。
    /// </summary>
    public sealed record DamageChannelDefinition(
        DamageChannelId DamageChannelId,
        Damage.DamageTagMask DefaultTags)
    {
        public void WriteHashComponents(Combat.CanonicalHashWriter writer)
        {
            writer.Write("damage_channel.id", DamageChannelId.Value ?? string.Empty);
            writer.Write("damage_channel.default_tags", (int)DefaultTags);
        }
    }

    /// <summary>
    /// 冲击（动量）Profile 定义。Profile 只决定动量生成/传递，不充当伤害类型。
    /// <see cref="TransferPercent"/> 是旧 Kw 的整数百分点等价物：
    /// Blunt 100 / Slash 60 / Pierce 30（与旧 Kw = 1.0 / 0.6 / 0.3 一一对应）。
    /// </summary>
    public sealed record ImpactProfileDefinition(
        ImpactProfileId ImpactProfileId,
        int TransferPercent)
    {
        public const string IMPACT_PROFILE_TRANSFER_INVALID = "IMPACT_PROFILE_TRANSFER_INVALID";

        public string Validate() => TransferPercent < 0 ? IMPACT_PROFILE_TRANSFER_INVALID : null;

        public void WriteHashComponents(Combat.CanonicalHashWriter writer)
        {
            writer.Write("impact_profile.id", ImpactProfileId.Value ?? string.Empty);
            writer.Write("impact_profile.transfer_percent", TransferPercent);
        }
    }
}
