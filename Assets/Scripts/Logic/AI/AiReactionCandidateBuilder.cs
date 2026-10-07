using System;
using System.Collections.Generic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.AI
{
    /// <summary>
    /// <strong>反应候选的构建与稳定排序</strong>（任务 09 产出 9 第二段）。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>候选项只来自<strong>已公开且仍开放</strong>的机会，且该机会的防御者必须是本控制者
    /// 当前可控制的单位；AI <strong>不</strong>能新增机会、选项，也不能推导 TriggerTick；</item>
    /// <item>Block <strong>绝不</strong>带目的格；Dodge 的目的格只能取自 Logic
    /// <strong>公布列表</strong>（<see cref="AiOpportunityView.DodgeDestinations"/>）——
    /// 这里是"从公布面里挑一个"，<strong>不是</strong>"算一个"：不读网格、不算距离、不建权重；</item>
    /// <item>本类<strong>不</strong>使用 RNG、不提交命令、不写任何权威状态。</item>
    /// </list>
    /// </summary>
    public static class AiReactionCandidateBuilder
    {
        /// <summary>
        /// 全部反应候选（<strong>已按稳定键排序</strong>）。
        /// </summary>
        public static IReadOnlyList<AiReactionOptionView> BuildReactionCandidatesOrdered(
            AiDecisionContext context, long tick)
        {
            var container = new List<AiReactionOptionView>();
            IReadOnlyList<UnitId> controlled = context.ControlledUnits;
            for (int o = 0; o < context.Opportunities.Count; o++)
            {
                AiOpportunityView opportunity = context.Opportunities[o];
                if (opportunity == null || !opportunity.IsOfferedTo(controlled, tick)) continue;

                IReadOnlyList<AiReactionOptionView> options = opportunity.Options;
                for (int i = 0; i < (options?.Count ?? 0); i++)
                {
                    AiReactionOptionView option = options[i];
                    if (option == null || !option.IsAvailableAt(tick)) continue;

                    if (option.ReactionKind == ReactionCommandKind.Dodge)
                    {
                        GridPoint? destination = FirstPublishedDestination(opportunity);
                        if (!destination.HasValue ||
                            option.DestinationEligibility != AiReactionDestinationEligibility.Eligible)
                            continue;
                        container.Add(option with { SelectedDestination = destination });
                        continue;
                    }

                    if (option.SelectedDestination.HasValue) continue;
                    container.Add(option);
                }
            }

            container.Sort((a, b) => a.CompareStableKeyTo(b));
            return container;
        }

        /// <summary>
        /// <strong>稳定排序本身</strong>（唯一比较实现是
        /// <see cref="AiReactionOptionView.CompareStableKeyTo"/>）：它接受任意输入排列并返回规范顺序，
        /// 因此"原始枚举序不是末级键"可以脱离决策主入口被单独观察与断言。
        /// </summary>
        public static IReadOnlyList<AiReactionOptionView> OrderReactions(
            IReadOnlyList<AiReactionOptionView> candidates)
        {
            var ordered = new List<AiReactionOptionView>(
                candidates ?? (IReadOnlyList<AiReactionOptionView>)Array.Empty<AiReactionOptionView>());
            ordered.Sort((a, b) => a.CompareStableKeyTo(b));
            return ordered;
        }

        /// <summary>Dodge 目的格：取 Logic 公布列表里的第一个候选（列表本身已按规范方向顺序给出）。</summary>
        private static GridPoint? FirstPublishedDestination(AiOpportunityView opportunity)
        {
            IReadOnlyList<GridPoint> published = opportunity?.DodgeDestinations?.Candidates;
            if (published == null || published.Count == 0) return null;
            return published[0];
        }
    }
}
