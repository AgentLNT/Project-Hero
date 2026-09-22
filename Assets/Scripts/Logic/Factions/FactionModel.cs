using System;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Factions
{
    /// <summary>
    /// 两个阵营之间的关系（主方案 2.3 冻结数值编码）。
    /// </summary>
    public enum FactionDisposition
    {
        Allied = 0,
        Neutral = 1,
        Hostile = 2
    }

    /// <summary>
    /// 一次具体的「来源单位 → 候选单位」查询结果（主方案 2.3 冻结数值编码）。
    /// </summary>
    public enum UnitRelation
    {
        Self = 0,
        Allied = 1,
        Neutral = 2,
        Hostile = 3
    }

    /// <summary>
    /// 攻击可影响的目标关系掩码（主方案 2.3 冻结数值编码）。
    /// Attack Payload 必须显式携带非空掩码；Self/友军/中立目标只能通过显式位开启。
    /// </summary>
    [Flags]
    public enum TargetRelationMask : int
    {
        None = 0,
        Self = 1 << 0,
        Allied = 1 << 1,
        Neutral = 1 << 2,
        Hostile = 1 << 3
    }

    public static class TargetRelationMasks
    {
        public const TargetRelationMask All = TargetRelationMask.Self | TargetRelationMask.Allied |
                                              TargetRelationMask.Neutral | TargetRelationMask.Hostile;

        /// <summary>非空且不含未定义位。</summary>
        public static bool IsValid(TargetRelationMask mask)
            => mask != TargetRelationMask.None && (((int)mask & ~(int)All) == 0);
    }

    /// <summary>
    /// 阵营关系校验稳定错误码（主方案 2.3.2 冻结部分；完整矩阵 Builder 由任务 02B 实现）。
    /// </summary>
    public static class FactionCodes
    {
        public const string TARGET_RELATION_MASK_EMPTY = "TARGET_RELATION_MASK_EMPTY";
        public const string TARGET_RELATION_MASK_INVALID = "TARGET_RELATION_MASK_INVALID";
        public const string FACTION_RELATION_MISSING = "FACTION_RELATION_MISSING";
        public const string FACTION_RELATION_DUPLICATE = "FACTION_RELATION_DUPLICATE";
        public const string FACTION_RELATION_NOT_CANONICAL = "FACTION_RELATION_NOT_CANONICAL";
        public const string FACTION_RELATION_UNKNOWN_ID = "FACTION_RELATION_UNKNOWN_ID";
        public const string VICTORY_FACTION_RELATION_CONFLICT = "VICTORY_FACTION_RELATION_CONFLICT";
        public const string FACTION_RELATION_INVARIANT_VIOLATION = "FACTION_RELATION_INVARIANT_VIOLATION";
    }

    /// <summary>
    /// 冻结阵营示例值（01B 拍板 A1：demo 只有 hero / monster 两个 FactionId）。
    /// 02B 的 FactionModelDefinition 使用这些值构建关系矩阵（hero↔monster = Hostile）。
    /// </summary>
    public static class FactionIds
    {
        public static readonly FactionId Hero = new("hero");
        public static readonly FactionId Monster = new("monster");
    }
}
