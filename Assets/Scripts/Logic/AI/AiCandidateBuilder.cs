using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.AI
{
    /// <summary>
    /// <strong>候选构建与稳定排序</strong>（任务 09 产出 9 的第一/二段）。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>普通候选的<strong>目标资格</strong>逐 <c>ActionSpec</c> 走与命令处理器
    /// <strong>同一个</strong> <see cref="TargetCandidateQuery"/>（唯一构造入口是
    /// <see cref="TargetCandidateQuery.From(DecisionSnapshot)"/>），因此掩码语义不可能分叉；</item>
    /// <item><c>Move</c> 候选<strong>必须</strong>经任务 06 的同一
    /// <see cref="IMovementPathCalculator.Recompute"/> 求路径投影；端口缺失 ⇒ fail-closed，
    /// <strong>绝不用距离、速度或自建权重估算路线</strong>；</item>
    /// <item>反应候选只来自<strong>已公开且仍开放</strong>的机会；AI 无法新增机会、推导 TriggerTick
    /// 或自造 Dodge 目的格（目的格只能取自 Logic 公布列表）。</item>
    /// <item>本类<strong>不</strong>使用 RNG、不提交命令、不写任何权威状态。</item>
    /// </list>
    /// </summary>
    public static class AiCandidateBuilder
    {
        /// <summary>
        /// 全部普通动作候选的<strong>原始枚举序</strong>（未排序）。
        ///
        /// 它<strong>只</strong>用于"排列确实不同 / 排序确实发生"这类断言：
        /// 决策路径永远只消费 <see cref="BuildNormalCandidatesOrdered"/> 的结果，
        /// 因此原始枚举序在实现上不可能是末级键。
        /// </summary>
        public static IReadOnlyList<AiCandidate> BuildNormalCandidates(AiDecisionContext context)
        {
            var container = new List<AiCandidate>();
            IReadOnlyList<UnitId> units = context.ControlledUnits;
            for (int u = 0; u < units.Count; u++)
            {
                UnitId owner = units[u];
                AppendAddCandidates(context, owner, container);
                AppendRemoveCandidates(context, owner, container);
                AppendMoveCandidates(context, owner, container);
            }
            return container;
        }

        /// <summary>
        /// 全部普通动作候选（<strong>已按稳定键排序</strong>，含不合格者，供诊断与排列不变性断言）。
        /// </summary>
        public static IReadOnlyList<AiCandidate> BuildNormalCandidatesOrdered(AiDecisionContext context)
        {
            var container = new List<AiCandidate>(BuildNormalCandidates(context));
            container.Sort((a, b) => a.CompareStableKeyTo(b));

            // 新增候选的事务内临时键在**排序之后**按名次派生：
            // 因此它与候选集合的原始枚举顺序无关（同 seed 的两次排列得到同一组键）。
            for (int i = 0; i < container.Count; i++)
            {
                if (container[i].Kind == AiCandidateKind.Add)
                    container[i] = container[i] with { TemporaryPlanKey = i + 1L };
            }
            return container;
        }

        private static void AppendAddCandidates(
            AiDecisionContext context, UnitId owner, List<AiCandidate> container)
        {
            IReadOnlyList<ActionSpecId> specIds = context.Snapshot.ActionSetOf(owner);
            IReadOnlyList<UnitId> universe = context.Snapshot.VisibleUnitIds;

            for (int s = 0; s < specIds.Count; s++)
            {
                ActionSpecId specId = specIds[s];
                ActionSpec spec = context.Snapshot.FindAction(specId);
                if (spec == null || !(spec.Payload is AttackPayloadSpec))
                    continue;   // 非攻击载荷没有关系掩码 ⇒ 绝不猜一个默认目标集合

                IReadOnlyList<TargetCandidate> inspected = context.Candidates.Inspect(spec, owner, universe);
                for (int c = 0; c < inspected.Count; c++)
                {
                    TargetCandidate candidate = inspected[c];
                    container.Add(new AiCandidate(
                        AiCandidateKind.Add, owner, specId, candidate.UnitId, null, default,
                        EligibilityOf(context, spec, owner, candidate)));
                }
            }
        }

        private static AiCandidateEligibility EligibilityOf(
            AiDecisionContext context, ActionSpec spec, UnitId owner, TargetCandidate candidate)
        {
            string code = context.Candidates.EligibilityOf(spec, owner, candidate.UnitId);
            if (code == ScheduleCodes.TARGET_RELATION_NOT_ALLOWED)
                return AiCandidateEligibility.TargetRelationNotAllowed;
            if (code != null) return AiCandidateEligibility.SpecHasNoTargetRelationMask;
            return context.HasSubmissionWindow
                ? AiCandidateEligibility.Eligible
                : AiCandidateEligibility.NoSubmissionWindow;
        }

        private static void AppendRemoveCandidates(
            AiDecisionContext context, UnitId owner, List<AiCandidate> container)
        {
            IReadOnlyList<ActionPlanSnapshot> plans = context.EditablePlans;
            for (int i = 0; i < plans.Count; i++)
            {
                ActionPlanSnapshot plan = plans[i];
                if (plan == null || plan.OwnerUnitId != owner.Value) continue;
                container.Add(new AiCandidate(
                    AiCandidateKind.Remove, owner,
                    new ActionSpecId(plan.ActionSpecId ?? string.Empty),
                    null, null, new ActionPlanId(plan.ActionPlanId),
                    AiCandidateEligibility.Eligible));
            }
        }

        /// <summary>
        /// <c>Move</c> 候选：只针对该控制者<strong>自己仍为 Editable 的普通 Move 计划</strong>，
        /// 且<strong>必须</strong>经任务 06 的同一 <see cref="IMovementPathCalculator"/> 求路径投影
        /// （与玩家预览、<c>ScheduleEditor</c> 求值器共用同一实例）。
        ///
        /// <para>
        /// 端口（<see cref="AiDecisionContext.PathCalculator"/> 或
        /// <see cref="AiDecisionContext.PlanLookup"/>）缺失时<strong>不</strong>降级为距离或速度估算，
        /// 而是整类候选以稳定原因排除——"AI 用自建权重猜路线"在实现上不存在。
        /// </para>
        /// </summary>
        private static void AppendMoveCandidates(
            AiDecisionContext context, UnitId owner, List<AiCandidate> container)
        {
            if (context.PlanLookup == null) return;
            IReadOnlyList<ActionPlan> plans = context.PlanLookup.ActivePlansOf(owner);
            for (int i = 0; i < plans.Count; i++)
            {
                ActionPlan plan = plans[i];
                if (plan == null || !plan.IsEditable || !plan.IsOrdinaryMove) continue;

                if (context.PathCalculator == null)
                {
                    container.Add(ToMoveCandidate(plan, AiCandidateEligibility.PathCalculatorUnavailable));
                    continue;
                }

                long requestedStartTick = context.NextTick;
                MovementPathProjection projection;
                try
                {
                    projection = context.PathCalculator.Recompute(
                        plan, requestedStartTick, requestedStartTick - plan.StartTick);
                }
                catch (ProjectHero.Logic.LogicDefinitionException)
                {
                    container.Add(ToMoveCandidate(plan, AiCandidateEligibility.PathRejected));
                    continue;
                }

                container.Add(projection == null
                    ? ToMoveCandidate(plan, AiCandidateEligibility.PathRejected)
                    : ToMoveCandidate(plan, AiCandidateEligibility.Eligible));
            }
        }

        private static AiCandidate ToMoveCandidate(ActionPlan plan, AiCandidateEligibility eligibility)
            => new AiCandidate(
                AiCandidateKind.Move, plan.OwnerUnitId, plan.ActionSpecId, null,
                plan.Destination, plan.ActionPlanId, eligibility);
    }
}
