using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Damage
{
    /// <summary>
    /// 伤害通道稳定 ID 清单（主方案 0.4.2.1 冻结）。
    /// 旧 ImpactType 同时充当伤害语义与动量系数，新模型拆为 DamageChannelId × ImpactProfileId 正交。
    /// </summary>
    public static class DamageChannels
    {
        public static readonly DamageChannelId PhysicalBlunt = new("physical.blunt");
        public static readonly DamageChannelId PhysicalSlash = new("physical.slash");
        public static readonly DamageChannelId PhysicalPierce = new("physical.pierce");
        public static readonly DamageChannelId ElementalFire = new("elemental.fire");
        public static readonly DamageChannelId ElementalFrost = new("elemental.frost");
        public static readonly DamageChannelId ElementalLightning = new("elemental.lightning");
        public static readonly DamageChannelId Arcane = new("arcane");
        public static readonly DamageChannelId True = new("true");

        /// <summary>true 通道严格绕过被动/动作抵抗且不可 Guard/Block。</summary>
        public static bool IsTrueChannel(DamageChannelId channel) => channel == True;
    }

    /// <summary>
    /// 通道默认标签目录。首版固定：
    /// true 通道默认同时带 BypassPassiveResistance | BypassActionResistance，
    /// 且不带 Blockable/Guardable（不可格挡/防御）；其余通道默认 Blockable | Guardable。
    /// Authoring 可在定义边界显式覆盖（02B）。
    /// </summary>
    public static class DamageChannelCatalog
    {
        public static DamageTagMask GetDefaultTags(DamageChannelId channel)
        {
            return DamageChannels.IsTrueChannel(channel)
                ? DamageTagMask.BypassPassiveResistance | DamageTagMask.BypassActionResistance
                : DamageTagMask.Blockable | DamageTagMask.Guardable;
        }
    }
}
