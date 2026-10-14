using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.AI
{
    /// <summary>
    /// <strong>候选 → <see cref="CommandRequest"/> 的唯一出口</strong>（任务 09 产出 9 第三段）。
    ///
    /// 它只填 <c>TargetTick</c>、scope 判别与载荷：<c>ControllerId</c>、来源种类、优先级、
    /// <c>ProducerOrdinal</c>、<c>CommandSequence</c>、规则费用与反应 SourceImpactTick 在类型上不存在。
    /// 因此"生产者自报身份/顺序/费用"在这条路径上不可能，也不需要额外的运行时校验。
    /// </summary>
    public static class AiCommandRequestBuilder
    {
        /// <summary>
        /// 把一条候选编译成标准 <c>ScheduleEditCommand</c>：
        /// <c>Add</c> 的起点用 <c>null</c>（= 本次事务该 Lane 的尾部，由排程事务解析，
        /// 生产者不声明绝对排程）；<c>Move</c>/<c>Remove</c> 只携带计划 ID 与目标 Tick。
        /// </summary>
        internal static CommandRequest ToRequest(AiDecisionContext context, AiCandidate candidate)
        {
            switch (candidate.Kind)
            {
                case AiCandidateKind.Add:
                    return new CommandRequest(context.NextTick,
                        new ScheduleEditScope(context.Snapshot.ScheduleRevision, context.ExpectedWindowId),
                        new ScheduleEditPayload(new ScheduleEditOperation[]
                        {
                            new AddOrdinaryPlanOperation(
                                candidate.TemporaryPlanKey, candidate.OwnerUnitId, candidate.ActionSpecId,
                                RequestedStartTick: null, AnchorAfterPlanId: default,
                                PrimaryTargetUnitId: candidate.TargetUnitId,
                                Facing: GridDirection.East,
                                Destination: candidate.Destination)
                        }));

                case AiCandidateKind.Move:
                    return new CommandRequest(context.NextTick,
                        new ScheduleEditScope(context.Snapshot.ScheduleRevision, context.ExpectedWindowId),
                        new ScheduleEditPayload(new ScheduleEditOperation[]
                        {
                            new MoveEditablePlanOperation(candidate.PlanId, context.NextTick, default)
                        }));

                case AiCandidateKind.Remove:
                    return new CommandRequest(context.NextTick,
                        new ScheduleEditScope(context.Snapshot.ScheduleRevision, context.ExpectedWindowId),
                        new ScheduleEditPayload(new ScheduleEditOperation[]
                        {
                            new RemoveEditablePlanOperation(
                                candidate.PlanId, ActionTerminationReason.CancelledByCommand)
                        }));

                default:
                    return null;
            }
        }

    }
}
