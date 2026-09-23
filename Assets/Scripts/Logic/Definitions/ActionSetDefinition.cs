using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Definitions
{
    /// <summary>
    /// 动作集合定义：一个单位理论可用的全部动作（主方案 2.3 / 00 号规则 26）。
    /// 关键约束：ActionSet 的<strong>规则结构</strong>只取决于内容配置，
    /// 不因单位由玩家还是 AI 控制而不同——玩家与 AI 必须提交同一种命令，
    /// 且高阶反应权限不得检查 <c>IsPlayerControlled</c>。
    ///
    /// 这里只冻结"可用性"与"是否高阶反应"两个事实；Lane/状态/空间/肾上腺素等
    /// 运行时合法性由任务 05-09 判定。
    /// </summary>
    public sealed record ActionSetDefinition(
        ActionSetId ActionSetId,
        IReadOnlyList<ActionSpecId> ActionSpecIds)
    {
        public const string ACTION_SET_ID_INVALID = "ACTION_SET_ID_INVALID";
        public const string ACTION_SET_EMPTY = "ACTION_SET_EMPTY";
        public const string ACTION_SET_DUPLICATE_ACTION = "ACTION_SET_DUPLICATE_ACTION";

        public bool Contains(ActionSpecId actionSpecId)
        {
            if (ActionSpecIds == null) return false;
            foreach (var id in ActionSpecIds)
            {
                if (id == actionSpecId) return true;
            }
            return false;
        }

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("action_set.id", ActionSetId.Value ?? string.Empty);
            if (ActionSpecIds == null) return;
            foreach (var id in ActionSpecIds)
            {
                writer.Write("action_set.action", id.Value ?? string.Empty);
            }
        }
    }

    /// <summary>
    /// 状态效果定义。当前项目没有任何状态效果资产，因此
    /// <c>BattleDefinition.StatusEffects</c> 允许为空集合（任务包「必须产出」2 明确允许）。
    /// 该类型存在的目的是让 EffectId 的归属、ID 命名空间与哈希边界现在就闭合，
    /// 而不是让任务 04/10 现场补一套。
    /// </summary>
    public sealed record StatusEffectSpec(
        StatusEffectSpecId StatusEffectSpecId,
        string DisplayNameKey,
        int MaxStackCount,
        bool IsDebuff)
    {
        public const string STATUS_EFFECT_STACK_INVALID = "STATUS_EFFECT_STACK_INVALID";

        public string Validate() => MaxStackCount <= 0 ? STATUS_EFFECT_STACK_INVALID : null;

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("status_effect.id", StatusEffectSpecId.Value ?? string.Empty);
            writer.Write("status_effect.max_stack_count", MaxStackCount);
            writer.Write("status_effect.is_debuff", IsDebuff ? "1" : "0");
        }
    }

    /// <summary>
    /// 动态生成单位的阵营解析策略（主方案 2.3.2 第 8 条 / 00 号规则 31）。
    /// 只允许两种来源：
    /// <list type="bullet">
    /// <item><see cref="FixedFactionId"/>：权威生成定义里写死的稳定阵营。</item>
    /// <item><see cref="InheritSourceFaction"/>：显式声明"继承召唤者阵营"。</item>
    /// </list>
    /// 命令、Controller、View 和 <c>IsPlayerControlled</c> 都不得提供或改写阵营；
    /// 也没有"缺省 = Hostile/Neutral"的兜底。
    /// </summary>
    public sealed record DynamicSpawnFactionPolicy(
        bool InheritSourceFaction,
        FactionId FixedFactionId)
    {
        public const string DYNAMIC_SPAWN_POLICY_INVALID = "DYNAMIC_SPAWN_POLICY_INVALID";

        /// <summary>显式继承召唤者阵营（最常见的召唤语义）。</summary>
        public static DynamicSpawnFactionPolicy InheritSource()
            => new(true, default);

        /// <summary>固定阵营策略；未知/空 FactionId 在定义校验阶段被拒绝。</summary>
        public static DynamicSpawnFactionPolicy Fixed(FactionId factionId)
            => new(false, factionId);

        public string Validate()
        {
            if (InheritSourceFaction) return null;
            return string.IsNullOrEmpty(FixedFactionId.Value) ? DYNAMIC_SPAWN_POLICY_INVALID : null;
        }

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("dynamic_spawn.inherit_source_faction", InheritSourceFaction ? "1" : "0");
            writer.Write("dynamic_spawn.fixed_faction_id",
                InheritSourceFaction ? string.Empty : (FixedFactionId.Value ?? string.Empty));
        }
    }
}
