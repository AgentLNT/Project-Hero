using System;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Physics;
using ProjectHero.Logic;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Authoring.Compatibility
{
    /// <summary>
    /// 旧伤害/冲击类型 → 新正交模型的显式映射（任务 01 记录 §6.3 候选映射，02B 按拍板落实）：
    /// 旧 ImpactType 同时充当伤害语义与动量系数，新模型拆为 DamageChannelId（伤害通道）
    /// 与 ImpactProfileId（冲击 Profile）两个独立字段，从此分开配置。
    /// </summary>
    public static class LegacyDamageMapping
    {
        /// <summary>旧 ImpactType → 伤害通道：Blunt/Slash/Pierce → physical.blunt/slash/pierce。</summary>
        public static DamageChannelId ChannelForImpactType(ImpactType type)
        {
            switch (type)
            {
                case ImpactType.Blunt: return DamageChannels.PhysicalBlunt;
                case ImpactType.Slash: return DamageChannels.PhysicalSlash;
                case ImpactType.Pierce: return DamageChannels.PhysicalPierce;
                default:
                    throw new LogicDefinitionException(LegacyAuthoringCodes.LEGACY_MOMENTUM_INPUT_INVALID,
                        $"未知旧 ImpactType 数值 {(int)type}");
            }
        }

        /// <summary>旧 ImpactType → 冲击 Profile：Blunt/Slash/Pierce → impact.blunt/slash/pierce。</summary>
        public static ImpactProfileId ProfileForImpactType(ImpactType type)
        {
            switch (type)
            {
                case ImpactType.Blunt: return ImpactProfiles.Blunt;
                case ImpactType.Slash: return ImpactProfiles.Slash;
                case ImpactType.Pierce: return ImpactProfiles.Pierce;
                default:
                    throw new LogicDefinitionException(LegacyAuthoringCodes.LEGACY_MOMENTUM_INPUT_INVALID,
                        $"未知旧 ImpactType 数值 {(int)type}");
            }
        }

        /// <summary>
        /// 旧非线性护甲/魔抗公式只在 Authoring 边界一次性折算为 Q10 被动抵抗：
        /// R = RoundHalfUp(1024 × d / (d + 100))，d 为有限非负浮点，结果限定 [0, 1024]。
        /// 只写入明确物理/元素通道，不再生成通用 DamageReduction（00 号规则 24 / 01B 拍板 B1）。
        /// </summary>
        public static int ResistanceQ10FromLegacyDefense(float defense)
        {
            if (float.IsNaN(defense) || float.IsInfinity(defense) || defense < 0f)
                throw new LogicDefinitionException(LegacyAuthoringCodes.LEGACY_RESISTANCE_INPUT_INVALID,
                    $"defense={defense}");

            double q10 = 1024.0 * defense / (defense + 100.0);
            long rounded = (long)Math.Round(q10, MidpointRounding.AwayFromZero);
            if (rounded < 0L || rounded > 1024L)
                throw new LogicDefinitionException(LegacyAuthoringCodes.LEGACY_RESISTANCE_INPUT_INVALID,
                    $"defense={defense}, q10={rounded}");
            return (int)rounded;
        }
    }
}
