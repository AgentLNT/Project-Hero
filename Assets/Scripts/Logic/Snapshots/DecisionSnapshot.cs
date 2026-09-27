using System;
using System.Collections.Generic;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Snapshots
{
    /// <summary>
    /// 面向 Controller（玩家 UI 与 AI）的<strong>只读决策快照</strong>。
    ///
    /// 与 <see cref="LogicSnapshot"/> <strong>分型</strong>：
    /// <list type="bullet">
    /// <item>决策快照只包含"该控制者有权看到"的信息，任务 05/09 接入后其他 Controller 的
    /// Editable 普通计划<strong>不得</strong>出现在这里；</item>
    /// <item>它<strong>不</strong>是 Canonical Snapshot 的别名，也不得为了 UI 方便直接暴露
    /// Canonical Snapshot；</item>
    /// <item>它持有整场唯一的 <see cref="IFactionRelationResolver"/> 实例本身，
    /// <strong>不复制</strong>任何可被 UI/AI 修改的关系矩阵；查询结果由该只读解析器给出。</item>
    /// </list>
    /// AI 与玩家读取的是<strong>同一个</strong>只读决策快照实例（主方案 3.4.3 阶段 18）。
    /// </summary>
    public sealed class DecisionSnapshot
    {
        private readonly IFactionRelationResolver _factionResolver;

        public DecisionSnapshot(
            long tick,
            string rulesVersion,
            string battleDefinitionHash,
            IReadOnlyList<UnitSnapshot> visibleUnits,
            IReadOnlyList<ActionPlanSnapshot> visiblePlans,
            long scheduleRevision,
            BattleEndSnapshot battleEnd,
            IFactionRelationResolver factionResolver)
        {
            Tick = tick;
            RulesVersion = rulesVersion ?? string.Empty;
            BattleDefinitionHash = battleDefinitionHash ?? string.Empty;
            VisibleUnits = Freeze(visibleUnits);
            VisiblePlans = Freeze(visiblePlans);
            ScheduleRevision = scheduleRevision;
            BattleEnd = battleEnd ?? BattleEndSnapshot.Active();
            _factionResolver = factionResolver
                ?? throw new ProjectHero.Logic.LogicDefinitionException(
                    FactionCodes.FACTION_RELATION_INVARIANT_VIOLATION, "decision snapshot requires the single resolver");
        }

        private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<T>();
            var copy = new T[source.Count];
            for (int i = 0; i < source.Count; i++) copy[i] = source[i];
            return Array.AsReadOnly(copy);
        }

        public long Tick { get; }
        public string RulesVersion { get; }
        public string BattleDefinitionHash { get; }
        public IReadOnlyList<UnitSnapshot> VisibleUnits { get; }
        public IReadOnlyList<ActionPlanSnapshot> VisiblePlans { get; }
        public long ScheduleRevision { get; }
        public BattleEndSnapshot BattleEnd { get; }

        /// <summary>整场唯一的只读阵营关系解析器（引用同一条实例，不是副本）。</summary>
        public IFactionRelationResolver FactionResolver => _factionResolver;

        public UnitRelation Classify(UnitId sourceUnitId, UnitId targetUnitId)
            => _factionResolver.Classify(sourceUnitId, targetUnitId);

        public bool Allows(TargetRelationMask allowedRelations, UnitId sourceUnitId, UnitId targetUnitId)
            => _factionResolver.Allows(allowedRelations, sourceUnitId, targetUnitId);
    }
}
