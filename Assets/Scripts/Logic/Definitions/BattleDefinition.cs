using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;

namespace ProjectHero.Logic.Definitions
{
    /// <summary>
    /// 并发行动系统的权威定义（主方案 0.5.3 / 任务包「必须产出」2）。
    /// 首版只有一项：<see cref="MetaResourceCost"/>——一次并发行动提交所消耗的局外资源量。
    /// 它是<strong>定义</strong>而不是命令载荷：调用方不得通过命令、参数或外部 Singleton 覆盖；
    /// 本场初始局外资源数值由 <c>BattleRuntimeInputs.InitialMetaResource</c> 显式注入。
    /// 该数值参与 BattleDefinitionHash。
    /// </summary>
    public sealed record ConcurrentActionDefinition(int MetaResourceCost)
    {
        public const string CONCURRENT_ACTION_COST_INVALID = "CONCURRENT_ACTION_COST_INVALID";

        /// <summary>首版冻结：一次并发行动消耗 1 点局外资源。</summary>
        public static readonly ConcurrentActionDefinition FrozenV1 = new(1);

        public string Validate() => MetaResourceCost < 0 ? CONCURRENT_ACTION_COST_INVALID : null;

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("concurrent_action.meta_resource_cost", MetaResourceCost);
        }
    }

    /// <summary>
    /// 完整、纯 C#、不可变的战斗定义（主方案 2.3）。这是 Logic 内核唯一的配置真相输入：
    /// 由 Authoring 边界的 <c>BattleDefinitionBuilder</c> 从旧资产一次性转换产出，
    /// 运行时不得再读取 ScriptableObject、Resources、场景或字符串名称。
    ///
    /// 不可变性约定：所有集合在构建时即被复制成只读列表并按稳定键（ID 的 Ordinal）排序；
    /// 所有 12 向表都是最终的规范整数表（不含旋转配方、不含浮点）。
    /// 哈希由 <see cref="BattleDefinitionHash"/> 在 <c>Builder</c> 内一次性计算并写入
    /// <see cref="BattleDefinitionHashValue"/>；<c>Builder</c> 以外的代码不得重算或改写它。
    /// </summary>
    public sealed record BattleDefinition(
        string RulesVersion,
        int TicksPerSecond,
        BattleRules Rules,
        ConcurrentActionDefinition ConcurrentAction,
        ReactionRules ReactionRules,
        AdrenalineRules AdrenalineRules,
        FactionModelDefinition FactionModel,
        IReadOnlyList<DamageChannelDefinition> DamageChannels,
        IReadOnlyList<ImpactProfileDefinition> ImpactProfiles,
        IReadOnlyList<UnitDefinition> Units,
        IReadOnlyList<ActionSpec> Actions,
        IReadOnlyList<AttackPatternSpec> AttackPatterns,
        IReadOnlyList<VolumeSpec> Volumes,
        IReadOnlyList<MovementPatternSpec> MovementPatterns,
        IReadOnlyList<ActionSetDefinition> ActionSets,
        IReadOnlyList<StatusEffectSpec> StatusEffects,
        IReadOnlyList<EncounterDefinition> Encounters,
        DynamicSpawnFactionPolicy DefaultDynamicSpawnPolicy,
        string BattleDefinitionHashValue)
    {
        /// <summary>按 ID 查找单位定义；不存在返回 null（调用方决定错误码）。</summary>
        public UnitDefinition FindUnit(Ids.UnitDefinitionId id)
        {
            if (Units == null) return null;
            foreach (var unit in Units)
            {
                if (unit != null && unit.UnitDefinitionId == id) return unit;
            }
            return null;
        }

        /// <summary>按 ID 查找动作定义。</summary>
        public ActionSpec FindAction(Ids.ActionSpecId id)
        {
            if (Actions == null) return null;
            foreach (var action in Actions)
            {
                if (action != null && action.ActionSpecId == id) return action;
            }
            return null;
        }

        /// <summary>按 ID 查找 Encounter 定义。</summary>
        public EncounterDefinition FindEncounter(Ids.EncounterDefinitionId id)
        {
            if (Encounters == null) return null;
            foreach (var encounter in Encounters)
            {
                if (encounter != null && encounter.EncounterId == id) return encounter;
            }
            return null;
        }

        /// <summary>按 ID 查找动作集合定义。</summary>
        public ActionSetDefinition FindActionSet(Ids.ActionSetId id)
        {
            if (ActionSets == null) return null;
            foreach (var set in ActionSets)
            {
                if (set != null && set.ActionSetId == id) return set;
            }
            return null;
        }

        /// <summary>按 ID 查找体积规范表（任务 06 的单位→体积绑定）。不存在返回 null。</summary>
        public VolumeSpec FindVolume(Ids.VolumeSpecId id)
        {
            if (Volumes == null || string.IsNullOrEmpty(id.Value)) return null;
            foreach (var volume in Volumes)
            {
                if (volume != null && string.Equals(volume.VolumeSpecId.Value, id.Value, System.StringComparison.Ordinal))
                    return volume;
            }
            return null;
        }

        /// <summary>
        /// 单位定义绑定的<strong>规范 12 向体积表</strong>（任务 06 生产路径的唯一入口）。
        ///
        /// 返回 null = 该单位未绑定体积规范表，或绑定指向不存在的表（悬空引用在 Builder 边界
        /// 已被拒绝；运行时再次返回 null 只是 fail-closed，不伪造表）。返回的表已由
        /// 任务 02B 预展开为规范整数表，消费端只允许按方向索引 + 整数平移。
        /// </summary>
        public IReadOnlyList<DirectionalTriangleSet> VolumeDirectionsOf(Ids.UnitDefinitionId unitDefinitionId)
        {
            UnitDefinition unit = FindUnit(unitDefinitionId);
            if (unit == null) return null;
            VolumeSpec volume = FindVolume(unit.VolumeSpecId);
            return volume?.Directions;
        }
    }
}
