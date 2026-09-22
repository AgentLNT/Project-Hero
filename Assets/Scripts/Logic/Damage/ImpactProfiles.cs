using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Damage
{
    /// <summary>
    /// 冲击 Profile 稳定 ID。首版内置旧 Blunt/Slash/Pierce 的兼容映射
    /// （impact.blunt / impact.slash / impact.pierce）。ImpactProfileId 只决定
    /// 动量生成/传递，不再充当伤害类型。
    /// </summary>
    public static class ImpactProfiles
    {
        public static readonly ImpactProfileId Blunt = new("impact.blunt");
        public static readonly ImpactProfileId Slash = new("impact.slash");
        public static readonly ImpactProfileId Pierce = new("impact.pierce");
    }

    /// <summary>
    /// 首版冲击传递系数目录（主方案 0.4.2.1 冻结常数；任务 08 复核后写入 BattleRules）：
    /// Blunt = 100、Slash = 60、Pierce = 30，与旧 Kw = 1.0 / 0.6 / 0.3 一一对应。
    /// </summary>
    public static class ImpactProfileCatalog
    {
        public const int BluntTransferPercent = 100;
        public const int SlashTransferPercent = 60;
        public const int PierceTransferPercent = 30;

        public static int GetTransferPercent(ImpactProfileId profileId)
        {
            if (profileId == ImpactProfiles.Blunt) return BluntTransferPercent;
            if (profileId == ImpactProfiles.Slash) return SlashTransferPercent;
            if (profileId == ImpactProfiles.Pierce) return PierceTransferPercent;
            throw new ProjectHero.Logic.LogicDefinitionException(
                "IMPACT_PROFILE_UNKNOWN", profileId.Value);
        }
    }
}
