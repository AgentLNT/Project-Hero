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
    /// <summary>
    /// <strong><c>ForcedDisplacementCommitter</c></strong>（任务包「必须产出」20 + 21 的前半段）。
    ///
    /// 它是"求解器已经判定完、现在提交终态"的那一层：
    /// <list type="number">
    /// <item>只消费 <see cref="ForcedDisplacementBatch"/>（求解器给出的<strong>最终</strong> footprint 与
    /// 每单位失效候选）；</item>
    /// <item>按冻结的<strong>原因优先级</strong>给候选分类：实际被换位单位的非终态 <c>Move</c> 计划 ⇒
    /// <c>MovementOriginInvalidated</c>；其余（最终 footprint 抢占了它的 Reservation）⇒
    /// <c>ReservationPreemptedByForcedDisplacement</c>。同一计划同时属于两类时前者胜出，
    /// 判据由 <see cref="ActionPlanTerminalCoordinator.ResolveForcedDisplacementReason"/> 唯一给出；</item>
    /// <item>按 <see cref="ActionPlanId"/> <strong>升序</strong>逐个经统一终态协调器
    /// <see cref="ActionPlanTerminalCoordinator.EnterTerminal"/> 请求终态；第一次原因胜出，
    /// 后续请求（含本 Tick 稍后的 Intercept 等）幂等空操作；</item>
    /// <item>返回<strong>实际</strong>进入终态的计划（升序）——候选里已经是终态的计划不计入。</item>
    /// </list>
    ///
    /// 明确<strong>不</strong>做（任务包 :210、:212、:417）：不自己删 <c>MovementSegment</c> /
    /// <c>Reservation</c> / Lane 项；不调用 ScheduleEdit；不做预算退款或窗口联动；
    /// 释放由任务 05 协调器的固定清理参与者（500 移动/预留、600 预算）完成。
    /// </summary>
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
