using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.AI
{
    /// <summary>
    /// <strong>决策上下文的唯一构建点</strong>（任务 09 产出 9/10）。
    ///
    /// 它把"与玩家同时公开"的只读面拼成 <see cref="AiDecisionContext"/>：
    /// 受控单位、自己仍为 <c>Editable</c> 的计划、自己的窗口快照、已公开机会与两个只读端口。
    /// 它<strong>不</strong>写任何权威状态、<strong>不</strong>建立机会、<strong>不</strong>算路径。
    ///
    /// <para>
    /// 机会收集按 <c>ReactionOpportunityId</c> 升序规范化，因此"受控单位顺序"或
    /// "机会来源返回顺序"都不会渗进候选枚举序。
    /// </para>
    /// </summary>
    internal static class AiDecisionContextFactory
    {
        public static AiDecisionContext Build(
            DecisionSnapshot snapshot,
            ControllerId controllerId,
            IAiReactionOpportunitySource opportunities,
            IAiActionPlanLookup planLookup,
            IMovementPathCalculator pathCalculator)
        {
            IReadOnlyList<UnitId> units = snapshot.ControlledUnitIds;
            IReadOnlyList<ActionPlanSnapshot> editable = snapshot.EditablePlansOf(controllerId);
            IReadOnlyList<AiOpportunityView> published = CollectOpportunities(opportunities, units, snapshot.Tick);

            return new AiDecisionContext(
                snapshot, controllerId, units, editable, snapshot.OwnWindow,
                published, pathCalculator, planLookup);
        }

        /// <summary>受控单位当前<strong>已公开</strong>的机会（按 <c>ReactionOpportunityId</c> 升序）。</summary>
        private static IReadOnlyList<AiOpportunityView> CollectOpportunities(
            IAiReactionOpportunitySource source, IReadOnlyList<UnitId> units, long tick)
        {
            if (source == null) return Array.Empty<AiOpportunityView>();

            var collected = new List<AiOpportunityView>();
            for (int i = 0; i < units.Count; i++)
            {
                IReadOnlyList<AiOpportunityView> found = source.PublishedOpportunitiesFor(units[i], tick);
                for (int j = 0; j < (found?.Count ?? 0); j++)
                {
                    if (found[j] != null) collected.Add(found[j]);
                }
            }
            collected.Sort((a, b) => a.ReactionOpportunityId.Value.CompareTo(b.ReactionOpportunityId.Value));
            return collected;
        }
    }
}
