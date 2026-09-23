using System;
using System.Collections.Generic;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using LogicGridDirection = ProjectHero.Logic.Grid.GridDirection;

namespace ProjectHero.Authoring
{
    /// <summary>
    /// 定义装配：把已校验的中间结果组装成不可变的 Logic 定义。
    /// 所有集合在返回前按稳定键（ID 的 Ordinal）排序并复制成只读列表，
    /// 因此下游拿到的顺序与输入枚举顺序无关。
    /// </summary>
    public static class BattleDefinitionAssembler
    {
        /// <summary>
        /// 伤害通道目录：首版内置全集（physical / elemental / arcane / true）。
        /// 通道本身不携带任何数值，只有默认标签目录；数值全部落在单位被动抵抗与动作载荷上。
        /// </summary>
        public static IReadOnlyList<DamageChannelDefinition> BuildDamageChannelCatalog()
        {
            var channels = new List<DamageChannelDefinition>
            {
                new DamageChannelDefinition(DamageChannels.PhysicalBlunt, DamageChannelCatalog.GetDefaultTags(DamageChannels.PhysicalBlunt)),
                new DamageChannelDefinition(DamageChannels.PhysicalSlash, DamageChannelCatalog.GetDefaultTags(DamageChannels.PhysicalSlash)),
                new DamageChannelDefinition(DamageChannels.PhysicalPierce, DamageChannelCatalog.GetDefaultTags(DamageChannels.PhysicalPierce)),
                new DamageChannelDefinition(DamageChannels.ElementalFire, DamageChannelCatalog.GetDefaultTags(DamageChannels.ElementalFire)),
                new DamageChannelDefinition(DamageChannels.ElementalFrost, DamageChannelCatalog.GetDefaultTags(DamageChannels.ElementalFrost)),
                new DamageChannelDefinition(DamageChannels.ElementalLightning, DamageChannelCatalog.GetDefaultTags(DamageChannels.ElementalLightning)),
                new DamageChannelDefinition(DamageChannels.Arcane, DamageChannelCatalog.GetDefaultTags(DamageChannels.Arcane)),
                new DamageChannelDefinition(DamageChannels.True, DamageChannelCatalog.GetDefaultTags(DamageChannels.True))
            };
            channels.Sort((a, b) => StringComparer.Ordinal.Compare(a.DamageChannelId.Value, b.DamageChannelId.Value));
            return channels;
        }

        /// <summary>
        /// 冲击 Profile 目录：旧 Kw 的整数百分点等价物（Blunt 100 / Slash 60 / Pierce 30）。
        /// 这些数值来自任务 02 的 <c>ImpactProfileCatalog</c>，本任务只把它们提升为
        /// 参与哈希的定义条目，不改动数值。
        /// </summary>
        public static IReadOnlyList<ImpactProfileDefinition> BuildImpactProfileCatalog()
        {
            var profiles = new List<ImpactProfileDefinition>
            {
                new ImpactProfileDefinition(ImpactProfiles.Blunt, ImpactProfileCatalog.BluntTransferPercent),
                new ImpactProfileDefinition(ImpactProfiles.Slash, ImpactProfileCatalog.SlashTransferPercent),
                new ImpactProfileDefinition(ImpactProfiles.Pierce, ImpactProfileCatalog.PierceTransferPercent)
            };
            profiles.Sort((a, b) => StringComparer.Ordinal.Compare(a.ImpactProfileId.Value, b.ImpactProfileId.Value));
            return profiles;
        }

        /// <summary>
        /// 移动 Pattern 目录。当前项目<strong>没有</strong>任何 MovementPattern 资产：
        /// 旧系统的 Move 只用逐边时长，没有三角格偏移表，因此不存在"可迁移的旧数据"。
        /// 这里给出显式新增的最小 identity 偏移表：每个方向只含单位自身占据的三角格
        /// （偶数方向 T=1、奇数方向 T=-1，均为合法点且去重后有序）。
        /// 它是本任务新增的内容配置（见 02B 配置迁移记录 §8），不是从资产猜出来的值；
        /// 它的唯一作用是让 Move/Dodge 载荷有一个闭合、可校验、可哈希的 Pattern 引用。
        /// </summary>
        public static IReadOnlyList<MovementPatternSpec> BuildMovementPatternCatalog()
        {
            var directions = new List<DirectionalTriangleSet>();
            for (int directionIndex = 0; directionIndex < GridDirectionInfo.DirectionCount; directionIndex++)
            {
                // 合法三角格点要求 T ∈ {-1,1} 且 X + Y + T 为偶数。
                // 取 (1, 0, 1)：1 + 0 + 1 = 2 为偶数 → 合法；且与方向无关，
                // 因此 identity 偏移表在 12 个方向上完全一致、可去重、可哈希。
                var selfCell = new ProjectHero.Logic.Grid.TrianglePoint(1, 0, 1);
                directions.Add(new DirectionalTriangleSet((LogicGridDirection)directionIndex,
                    new List<ProjectHero.Logic.Grid.TrianglePoint> { selfCell }));
            }

            return new List<MovementPatternSpec>
            {
                new MovementPatternSpec(new MovementPatternId(MovementPatternIds.Identity), directions)
            };
        }

        /// <summary>
        /// 动作集合装配：半径变体攻击动作 + 四个与半径无关的窗口/移动/高阶级动作
        /// （Guard / Move / Block / Dodge）。后者是首版每个单位都必须可用的最小集合，
        /// 且<strong>不因单位由玩家还是 AI 控制而不同</strong>。
        /// 结果按 ActionSpecId 的 Ordinal 排序。
        /// </summary>
        public static ActionSetDefinition BuildActionSet(string actionSetId, IReadOnlyList<ActionSpecId> attackActionIds)
        {
            var ids = new List<ActionSpecId>(attackActionIds ?? new List<ActionSpecId>())
            {
                new ActionSpecId(Legacy.LegacyIdMigrationManifest.ActionGuard),
                new ActionSpecId(Legacy.LegacyIdMigrationManifest.ActionMove),
                new ActionSpecId(Legacy.LegacyIdMigrationManifest.ActionBlock),
                new ActionSpecId(Legacy.LegacyIdMigrationManifest.ActionDodge)
            };
            ids.Sort((a, b) => StringComparer.Ordinal.Compare(a.Value, b.Value));
            return new ActionSetDefinition(new ActionSetId(actionSetId), ids);
        }

        /// <summary>按 (FactionAId, FactionBId) 规范顺序排序关系对。</summary>
        public static List<FactionRelationDefinition> CanonicalizeRelations(
            IEnumerable<FactionRelationDefinition> relations)
        {
            var list = new List<FactionRelationDefinition>(relations);
            list.Sort((a, b) =>
            {
                int byFirst = StringComparer.Ordinal.Compare(a.FactionAId.Value, b.FactionAId.Value);
                return byFirst != 0 ? byFirst : StringComparer.Ordinal.Compare(a.FactionBId.Value, b.FactionBId.Value);
            });
            return list;
        }

        /// <summary>按 FactionId 的 Ordinal 排序阵营清单。</summary>
        public static List<FactionDefinition> CanonicalizeFactions(IEnumerable<FactionDefinition> factions)
        {
            var list = new List<FactionDefinition>(factions);
            list.Sort((a, b) => StringComparer.Ordinal.Compare(a.FactionId.Value, b.FactionId.Value));
            return list;
        }

        public static List<FactionId> CanonicalizeFactionIds(IEnumerable<FactionId> factionIds)
        {
            var list = new List<FactionId>(factionIds);
            list.Sort((a, b) => StringComparer.Ordinal.Compare(a.Value, b.Value));
            return list;
        }

        public static List<EncounterUnitSlot> CanonicalizeSlots(IEnumerable<EncounterUnitSlot> slots)
            => EncounterSlotOrdering.OrderBySlotIdOrdinal(slots);

        public static List<ControllerBinding> CanonicalizeControllers(IEnumerable<ControllerBinding> controllers)
        {
            var list = new List<ControllerBinding>(controllers);
            list.Sort((a, b) => StringComparer.Ordinal.Compare(a.ControllerId.Value, b.ControllerId.Value));
            return list;
        }
    }

    /// <summary>首版内置的稳定 MovementPatternId（内容配置，非从资产推导）。</summary>
    public static class MovementPatternIds
    {
        /// <summary>单位自身占据格的最小偏移表（所有方向只含自身三角格）。</summary>
        public const string Identity = "movement.identity";
    }
}
