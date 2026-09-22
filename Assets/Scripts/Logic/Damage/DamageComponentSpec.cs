using System;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Damage
{
    /// <summary>
    /// 伤害分量标签（主方案 0.4.2.1）。带绕过标签的分量分别跳过对应抵抗层；
    /// BypassActionResistance 与 Blockable/Guardable 同置是矛盾标签，必须拒绝。
    /// </summary>
    [Flags]
    public enum DamageTagMask : int
    {
        None = 0,
        Blockable = 1 << 0,
        Guardable = 1 << 1,
        BypassPassiveResistance = 1 << 2,
        BypassActionResistance = 1 << 3
    }

    /// <summary>
    /// 攻击载荷的伤害分量：通道 + 原始量 + 标签。与动量（ImpactProfileId）正交，
    /// 不重新合并为单一 ImpactType / DamageReduction（00 号规则 24）。
    /// 注：Unity 6000.6.2f1 默认 C# 9，record struct 为 C# 10 特性，故手写只读值类型。
    /// </summary>
    public readonly struct DamageComponentSpec
    {
        public readonly DamageChannelId ChannelId;
        public readonly float RawAmount;
        public readonly DamageTagMask Tags;

        public DamageComponentSpec(DamageChannelId channelId, float rawAmount, DamageTagMask tags)
        {
            ChannelId = channelId;
            RawAmount = rawAmount;
            Tags = tags;
        }

        public bool Equals(DamageComponentSpec other)
            => ChannelId.Equals(other.ChannelId) && RawAmount.Equals(other.RawAmount) && Tags == other.Tags;

        public override bool Equals(object obj) => obj is DamageComponentSpec other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(ChannelId, RawAmount, (int)Tags);

        public override string ToString() => $"{ChannelId.Value}:{RawAmount}:{Tags}";

        public static bool operator ==(DamageComponentSpec left, DamageComponentSpec right) => left.Equals(right);

        public static bool operator !=(DamageComponentSpec left, DamageComponentSpec right) => !left.Equals(right);
    }

    public static class DamageTagCodes
    {
        public const string DAMAGE_COMPONENT_INVALID = "DAMAGE_COMPONENT_INVALID";
        public const string DAMAGE_TAGS_INVALID = "DAMAGE_TAGS_INVALID";
        public const string CONTRADICTORY_DAMAGE_TAGS = "CONTRADICTORY_DAMAGE_TAGS";
    }

    public static class DamageComponentValidation
    {
        public const DamageTagMask AllKnownTags =
            DamageTagMask.Blockable | DamageTagMask.Guardable |
            DamageTagMask.BypassPassiveResistance | DamageTagMask.BypassActionResistance;

        /// <summary>
        /// 校验单个分量：通道 ID 格式合法、伤害量有限非负、标签位已知、
        /// 且不得同时声明动作绕过与 Blockable/Guardable。
        /// </summary>
        public static string Validate(DamageComponentSpec component)
        {
            if (DefinitionIdValidation.ValidateFormat(component.ChannelId.Value) != null)
                return DamageTagCodes.DAMAGE_COMPONENT_INVALID;

            float amount = component.RawAmount;
            if (amount < 0f || float.IsNaN(amount) || float.IsInfinity(amount))
                return DamageTagCodes.DAMAGE_COMPONENT_INVALID;

            if (((int)component.Tags & ~(int)AllKnownTags) != 0)
                return DamageTagCodes.DAMAGE_TAGS_INVALID;

            bool hasActionBypass =
                (component.Tags & DamageTagMask.BypassActionResistance) != 0;
            bool hasEligibility =
                (component.Tags & (DamageTagMask.Blockable | DamageTagMask.Guardable)) != 0;
            if (hasActionBypass && hasEligibility)
                return DamageTagCodes.CONTRADICTORY_DAMAGE_TAGS;

            return null;
        }

        /// <summary>true 通道默认绕过全部两层抵抗。</summary>
        public static bool TrueChannelBypassesAllResistance(DamageComponentSpec component)
            => DamageChannels.IsTrueChannel(component.ChannelId);
    }
}
