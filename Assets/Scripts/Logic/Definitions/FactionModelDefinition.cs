using System;
using System.Collections.Generic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Definitions
{
    /// <summary>
    /// 阵营定义。首版只需要稳定 ID：显示名、本地化键、图标和颜色属于 Authoring/UnityView
    /// 的展示映射，不参与敌我推导，也不能回写 Logic（主方案 2.3.2）。
    /// </summary>
    public sealed record FactionDefinition(FactionId FactionId);

    /// <summary>
    /// 一条静态、对称的阵营关系。<see cref="FactionAId"/> 必须按 <see cref="StringComparer.Ordinal"/>
    /// 严格小于 <see cref="FactionBId"/>：矩阵只保留规范化上三角，A→B 与 B→A 不允许同时出现。
    /// </summary>
    public sealed record FactionRelationDefinition(
        FactionId FactionAId,
        FactionId FactionBId,
        FactionDisposition Disposition);

    /// <summary>
    /// 阵营关系模型：N 个阵营必须恰好有 N*(N-1)/2 条无向关系，缺项不得用 Hostile/Neutral 补洞。
    /// 同 Faction 的不同单位为 Allied、同一 Unit 为 Self 由规则自动成立，不占矩阵条目。
    /// </summary>
    public sealed record FactionModelDefinition(
        IReadOnlyList<FactionDefinition> Factions,
        IReadOnlyList<FactionRelationDefinition> Relations)
    {
        /// <summary>按规范化对键查询关系；未配置返回 false。</summary>
        public bool TryGetDisposition(FactionId a, FactionId b, out FactionDisposition disposition)
        {
            FactionId first = FactionPairs.CanonicalFirst(a, b);
            FactionId second = FactionPairs.CanonicalSecond(a, b);
            if (Relations != null)
            {
                foreach (var relation in Relations)
                {
                    if (relation.FactionAId == first && relation.FactionBId == second)
                    {
                        disposition = relation.Disposition;
                        return true;
                    }
                }
            }
            disposition = default;
            return false;
        }

        public bool ContainsFaction(FactionId factionId)
        {
            if (Factions == null) return false;
            foreach (var faction in Factions)
            {
                if (faction.FactionId == factionId) return true;
            }
            return false;
        }

        public int ExpectedRelationCount => Factions == null ? 0 : Factions.Count * (Factions.Count - 1) / 2;

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            if (Factions != null)
            {
                foreach (var faction in Factions)
                {
                    writer.Write("faction.id", faction.FactionId.Value ?? string.Empty);
                }
            }
            if (Relations != null)
            {
                foreach (var relation in Relations)
                {
                    writer.Write("faction.relation",
                        (relation.FactionAId.Value ?? string.Empty) + "|" +
                        (relation.FactionBId.Value ?? string.Empty) + "|" +
                        relation.Disposition.ToString() + ":" + (int)relation.Disposition);
                }
            }
        }
    }

    /// <summary>
    /// 无序对规范化助手：固定 (min, max) 顺序，全部使用 <see cref="StringComparer.Ordinal"/>。
    /// 它保证"输入枚举顺序不影响规范结果"。
    /// </summary>
    public static class FactionPairs
    {
        public static FactionId CanonicalFirst(FactionId a, FactionId b)
            => StringComparer.Ordinal.Compare(a.Value, b.Value) <= 0 ? a : b;

        public static FactionId CanonicalSecond(FactionId a, FactionId b)
            => StringComparer.Ordinal.Compare(a.Value, b.Value) <= 0 ? b : a;
    }

    /// <summary>
    /// 不可变阵营关系解析器：Logic 中<strong>唯一</strong>的单位关系查询入口。
    /// 语义冻结为：
    /// <list type="bullet">
    /// <item>来源与候选是同一个 <see cref="UnitId"/> → <see cref="UnitRelation.Self"/>。</item>
    /// <item>两个不同 UnitId 的 FactionId 相同 → <see cref="UnitRelation.Allied"/>。</item>
    /// <item>不同阵营 → 读取已验证矩阵；缺失关系是运行时不变量错误
    /// （<see cref="FactionCodes.FACTION_RELATION_INVARIANT_VIOLATION"/>），
    /// 不得退回 Neutral/Hostile 后继续。</item>
    /// </list>
    /// 它只读取运行时单位的 FactionId 与不可变矩阵，绝不读取 ControllerBinding、窗口、
    /// AI/Player 来源、Tag/Layer 或 <c>IsPlayerControlled</c>。
    /// </summary>
    public interface IFactionRelationResolver
    {
        UnitRelation Classify(UnitId sourceUnitId, UnitId targetUnitId);

        bool Allows(TargetRelationMask allowedRelations, UnitId sourceUnitId, UnitId targetUnitId);
    }

    public sealed class FactionRelationResolver : IFactionRelationResolver
    {
        private readonly FactionModelDefinition _model;
        private readonly Dictionary<UnitId, FactionId> _unitFactions;

        public FactionRelationResolver(FactionModelDefinition model, IReadOnlyDictionary<UnitId, FactionId> unitFactions)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _unitFactions = new Dictionary<UnitId, FactionId>();
            if (unitFactions != null)
            {
                foreach (var pair in unitFactions)
                {
                    _unitFactions[pair.Key] = pair.Value;
                }
            }
        }

        public FactionModelDefinition Model => _model;

        public FactionId FactionOf(UnitId unitId)
        {
            if (!_unitFactions.TryGetValue(unitId, out var faction))
                throw new LogicDefinitionException(FactionCodes.FACTION_RELATION_UNKNOWN_ID, unitId.ToString());
            return faction;
        }

        public bool HasUnit(UnitId unitId) => _unitFactions.ContainsKey(unitId);

        public UnitRelation Classify(UnitId sourceUnitId, UnitId targetUnitId)
        {
            if (sourceUnitId == targetUnitId) return UnitRelation.Self;

            FactionId sourceFaction = FactionOf(sourceUnitId);
            FactionId targetFaction = FactionOf(targetUnitId);
            if (sourceFaction == targetFaction) return UnitRelation.Allied;

            if (!_model.TryGetDisposition(sourceFaction, targetFaction, out var disposition))
                throw new LogicDefinitionException(FactionCodes.FACTION_RELATION_INVARIANT_VIOLATION,
                    $"{sourceFaction.Value}|{targetFaction.Value}");

            switch (disposition)
            {
                case FactionDisposition.Allied: return UnitRelation.Allied;
                case FactionDisposition.Neutral: return UnitRelation.Neutral;
                case FactionDisposition.Hostile: return UnitRelation.Hostile;
                default:
                    throw new LogicDefinitionException(FactionCodes.FACTION_RELATION_INVARIANT_VIOLATION,
                        disposition.ToString());
            }
        }

        public bool Allows(TargetRelationMask allowedRelations, UnitId sourceUnitId, UnitId targetUnitId)
        {
            UnitRelation relation = Classify(sourceUnitId, targetUnitId);
            return (allowedRelations & ToMask(relation)) != 0;
        }

        /// <summary>UnitRelation → TargetRelationMask 位的唯一映射。</summary>
        public static TargetRelationMask ToMask(UnitRelation relation)
        {
            switch (relation)
            {
                case UnitRelation.Self: return TargetRelationMask.Self;
                case UnitRelation.Allied: return TargetRelationMask.Allied;
                case UnitRelation.Neutral: return TargetRelationMask.Neutral;
                case UnitRelation.Hostile: return TargetRelationMask.Hostile;
                default:
                    throw new LogicDefinitionException(FactionCodes.FACTION_RELATION_INVARIANT_VIOLATION,
                        relation.ToString());
            }
        }
    }
}
