using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Interactions
{
    public sealed class ForcedDisplacementCommitter : IForcedDisplacementCommitter
    {
        private readonly ActionScheduleAuthority _authority;
        private readonly ActionPlanTerminalCoordinator _coordinator;

        public ForcedDisplacementCommitter(
            ActionScheduleAuthority authority, ActionPlanTerminalCoordinator coordinator)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        public IReadOnlyList<ActionPlanId> TerminateInvalidatedMovementPlansOrdered(
            ForcedDisplacementBatch batch, long tick)
        {
            if (batch == null || batch.Resolutions.Count == 0) return Array.Empty<ActionPlanId>();

            // 实际被换位单位（AppliedSteps > 0 ⇒ From != To），按 UnitId 升序（源自 batch 的规范序）。
            var relocated = new List<long>();
            for (int i = 0; i < batch.Resolutions.Count; i++)
            {
                ForcedDisplacementResolution resolution = batch.Resolutions[i];
                if (!resolution.IsRelocating) continue;
                if (!relocated.Contains(resolution.TargetUnitId.Value))
                    relocated.Add(resolution.TargetUnitId.Value);
            }

            // 候选合并：同一计划出现在多条结果里时取优先级更高（PriorityOf 更小）的原因。
            var candidateIds = new List<long>();
            var candidateReasons = new List<ActionTerminationReason>();
            for (int i = 0; i < batch.Resolutions.Count; i++)
            {
                ForcedDisplacementResolution resolution = batch.Resolutions[i];
                IReadOnlyList<ActionPlanId> candidates = resolution.InvalidatedPlanIds;
                if (candidates == null) continue;

                for (int c = 0; c < candidates.Count; c++)
                {
                    ActionPlan plan = _authority.Registry.Find(candidates[c]);
                    // 已终态 / 已不在注册表的候选是幂等空操作，绝不复活。
                    if (plan == null || plan.IsTerminal) continue;

                    bool movementOriginInvalidated =
                        plan.ActionType == ActionType.Move && relocated.Contains(plan.OwnerUnitId.Value);
                    ActionTerminationReason reason = ActionPlanTerminalCoordinator.ResolveForcedDisplacementReason(
                        movementOriginInvalidated, !movementOriginInvalidated);

                    int slot = candidateIds.IndexOf(candidates[c].Value);
                    if (slot < 0)
                    {
                        candidateIds.Add(candidates[c].Value);
                        candidateReasons.Add(reason);
                    }
                    else if (ActionTerminationReasons.PriorityOf(reason) <
                             ActionTerminationReasons.PriorityOf(candidateReasons[slot]))
                    {
                        candidateReasons[slot] = reason;
                    }
                }
            }

            // 稳定提交序：只按 ActionPlanId 升序（不读 Resolution 输入顺序）。
            var ordered = new List<int>(candidateIds.Count);
            for (int i = 0; i < candidateIds.Count; i++) ordered.Add(i);
            ordered.Sort((a, b) => candidateIds[a].CompareTo(candidateIds[b]));

            var terminated = new List<ActionPlanId>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                int slot = ordered[i];
                var planId = new ActionPlanId(candidateIds[slot]);
                ActionPlan plan = _authority.Registry.Find(planId);
                if (plan == null || plan.IsTerminal) continue;

                ActionPlanTerminalOutcome outcome = _coordinator.EnterTerminal(plan, candidateReasons[slot], tick);
                if (outcome.EnteredTerminal) terminated.Add(planId);
            }
            return terminated;
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
            => "forced-displacement-committer plans=" +
               _authority.Registry.ActiveCount.ToString(CultureInfo.InvariantCulture);
    }
}
