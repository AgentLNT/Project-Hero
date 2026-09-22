using System;
using System.Collections.Generic;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Actions
{
    /// <summary>攻击目标策略：固定目标 或 区域内全部合格目标。</summary>
    public enum TargetPolicy
    {
        PrimaryTargetOnly = 0,
        AllTargetsInArea = 1
    }

    /// <summary>
    /// 攻击的反应可用性标签。Reactable/Blockable/Dodgeable 为正位；
    /// 缺位即 Unreactable/Unblockable/Undodgeable（互斥对由缺失语义表达）。
    /// </summary>
    [Flags]
    public enum AttackTagMask : int
    {
        None = 0,
        Reactable = 1 << 0,
        Blockable = 1 << 1,
        Dodgeable = 1 << 2
    }

    /// <summary>防御动作的合格接触标签（Guard/Block 的 EligibleTags）。</summary>
    [Flags]
    public enum DefenseTagMask : int
    {
        None = 0,
        Blockable = 1 << 0,
        Guardable = 1 << 1
    }

    /// <summary>抽象动作载荷。与 TimingSpec 一一匹配（闭合匹配矩阵）。</summary>
    public abstract record ActionPayloadSpec;

    /// <summary>
    /// 攻击载荷：DamageComponents[] + ImpactProfileId + ForceMultiplier + AllowedTargetRelations
    /// 的正交结构（00 号规则 24）。伤害与动量分开配置；AllowedTargetRelations 必须非空。
    /// </summary>
    public sealed record AttackPayloadSpec(
        IReadOnlyList<DamageComponentSpec> DamageComponents,
        ImpactProfileId ImpactProfileId,
        float ForceMultiplier,
        TargetPolicy TargetPolicy,
        TargetRelationMask AllowedTargetRelations,
        int MomentumDirectionOffsetSteps,
        AttackPatternSpec Pattern,
        AttackTagMask Tags) : ActionPayloadSpec;

    /// <summary>
    /// Guard 载荷：分通道部分伤害抵抗 + 部分动量抵抗（各严格位于 [1,1023]），
    /// 以及对合格标签（EligibleTags）生效。禁止通用 DamageReduction。
    /// </summary>
    public sealed record GuardPayloadSpec(
        IReadOnlyDictionary<DamageChannelId, int> DamageResistanceQ10,
        int MomentumResistanceQ10,
        DefenseTagMask EligibleTags) : ActionPayloadSpec;

    /// <summary>
    /// Block 载荷：只声明合格标签。合格接触的伤害/动量抵抗由规则固定为
    /// BattleRules.FullBlockResistanceQ10 = 1024（完全抵抗），Authoring 不提供可降低该值的字段。
    /// </summary>
    public sealed record BlockPayloadSpec(DefenseTagMask EligibleTags) : ActionPayloadSpec;

    /// <summary>
    /// Move 载荷：MaxPathWeightUnits 上限 + 离散移动 Pattern。
    /// MaxPathWeightUnits 必须为正且不超过 PathSearchRules.MaxPathWeightUnits。
    /// </summary>
    public sealed record MovePayloadSpec(
        int MaxPathWeightUnits,
        MovementPatternSpec Pattern) : ActionPayloadSpec;

    /// <summary>Dodge 载荷：最大距离步数 + 离散移动 Pattern。</summary>
    public sealed record DodgePayloadSpec(
        int MaxDistanceSteps,
        MovementPatternSpec Pattern) : ActionPayloadSpec;
}
