using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.AI
{
    public enum AiExecutionKind { SingleCommand = 0, UtilityBehaviorTree = 1 }
    public enum AiBehaviorStatus { Failure = 0, Running = 1, Success = 2 }

    // Stable node cursors: 0 = select/submit, 1 = acknowledge ingress, 2 = await terminal.
    // Each channel owns its cursor; responding never cancels an ordinary combat plan.
    public sealed record AiBehaviorTask(int Cursor = 0, long RequestTick = -1, long ProducerOrdinal = 0,
        long PlanId = 0, AiBehaviorStatus Status = AiBehaviorStatus.Failure, string Reason = "IDLE", long PredecessorPlanId = 0);
    public sealed record AiBehaviorMemory(AiBehaviorTask Ordinary, AiBehaviorTask Reaction)
    {
        public static AiBehaviorMemory Empty => new AiBehaviorMemory(new AiBehaviorTask(), new AiBehaviorTask());
        public void Encode(CanonicalEncoder encoder)
        {
            encoder.WriteString(AiBehaviorTree.Version);
            Write(Ordinary); Write(Reaction);
            void Write(AiBehaviorTask task)
            {
                encoder.WriteInt32(task.Cursor); encoder.WriteInt64(task.RequestTick);
                encoder.WriteInt64(task.ProducerOrdinal); encoder.WriteInt64(task.PlanId);
                encoder.WriteInt32((int)task.Status); encoder.WriteString(task.Reason);
                encoder.WriteInt64(task.PredecessorPlanId);
            }
        }
    }

    /// <summary>Memoryful Sequence and reactive Selector. All persistent memory belongs to the canonical task record.</summary>
    public static class AiBehaviorNodes
    {
        public delegate AiBehaviorStatus Leaf(ref AiBehaviorTask memory);
        public static AiBehaviorStatus Sequence(ref AiBehaviorTask memory, params Leaf[] children)
        {
            while (memory.Cursor < children.Length)
            {
                var status = children[memory.Cursor](ref memory);
                memory = memory with { Status = status };
                if (status == AiBehaviorStatus.Running) return status;
                if (status == AiBehaviorStatus.Failure) { memory = memory with { Cursor = 0 }; return status; }
                memory = memory with { Cursor = memory.Cursor + 1 };
            }
            memory = memory with { Cursor = 0, Status = AiBehaviorStatus.Success };
            return AiBehaviorStatus.Success;
        }
        public static AiBehaviorStatus Selector(params Func<AiBehaviorStatus>[] children)
        {
            foreach (var child in children)
            {
                var status = child();
                if (status != AiBehaviorStatus.Failure) return status;
            }
            return AiBehaviorStatus.Failure;
        }
    }

    /// <summary>First hybrid: utility selects data-driven action tasks; the tree owns their asynchronous lifecycle.</summary>
    public static class AiBehaviorTree
    {
        public const string Version = "utility-behavior-tree-v1";
        public static AiDecision Tick(AiDecisionContext context, ulong seed, AiPolicyKind policy,
            AiBehaviorMemory memory, IAiExecutionFeedback feedback, long nextProducerOrdinal)
        {
            if (feedback == null) throw new LogicDefinitionException("AI_EXECUTION_FEEDBACK_MISSING");
            // Utility and opt-in engagement goals propose commands; the tree owns their real lifecycle.
            var proposed = AiTacticalPolicy.Decide(context, seed, policy);
            var ordinary = memory.Ordinary; var reaction = memory.Reaction;
            var selectedEvaluation = proposed.Evaluations.FirstOrDefault(e => e.Chosen);
            CommandRequest selected = null;
            string reason = proposed.NoSelectionReason;
            bool reactionChosen = selectedEvaluation?.IsReaction == true;
            bool reactionWait = proposed.Evaluations.Any(e => e.IsReaction && e.RejectionCode == null) && !reactionChosen;
            // Consume both channels' receipts before priority arbitration: an urgent response must
            // not leave an ordinary acknowledgement unread until its per-Tick receipt has expired.
            BindReceipt(ref ordinary); BindReceipt(ref reaction);
            CompleteReadyTask(ref ordinary); CompleteReadyTask(ref reaction);
            AiBehaviorNodes.Selector(Response, Ordinary, Wait);
            var evaluations = proposed.Evaluations.Select(e => e.Chosen && selected == null
                ? e with { Chosen = false, Reason = "TREE_WAITING_FOR_ACTUAL_RESULT" } : e).ToArray();
            return proposed with { Selected = selected, Evaluations = evaluations,
                NoSelectionReason = selected == null ? reason : null,
                Behavior = new AiBehaviorMemory(ordinary, reaction), Execution = AiExecutionKind.UtilityBehaviorTree,
                UnsupportedActionIds = Unsupported(context) };

            AiBehaviorStatus Response()
            {
                // A legal ordinary continuation may start at the reaction's soft/end boundary.
                // Its existing reaction task stays tracked; the shared scheduler still arbitrates.
                if (reaction.Cursor > 0 && !reactionChosen && proposed.Selected != null)
                    return AiBehaviorStatus.Failure;
                if (reaction.Cursor > 0 || reactionChosen)
                    return Run(ref reaction);
                if (!reactionWait) return AiBehaviorStatus.Failure;
                reason = proposed.NoSelectionReason ?? "WAIT_REACTION_NOT_BENEFICIAL";
                return AiBehaviorStatus.Running;
            }
            AiBehaviorStatus Ordinary()
            {
                if (ordinary.Cursor == 0 && (proposed.Selected == null || reactionChosen)) return AiBehaviorStatus.Failure;
                if (ordinary.Cursor == 2 && proposed.Selected != null && !reactionChosen)
                {
                    // V2 can queue the next-Tick continuation at an existing plan's half-open
                    // end boundary. Keep the predecessor association until its real terminal.
                    var current = feedback.ReadPlan(context.ControllerId, ordinary.PlanId);
                    if (current != null && current.EndTick <= context.NextTick)
                        ordinary = new AiBehaviorTask(PredecessorPlanId: ordinary.PlanId);
                }
                return Run(ref ordinary);
            }
            AiBehaviorStatus Wait() => AiBehaviorStatus.Running;
            AiBehaviorStatus Run(ref AiBehaviorTask task)
                => AiBehaviorNodes.Sequence(ref task, Submit, Acknowledge, AwaitTerminal);
            void CompleteReadyTask(ref AiBehaviorTask task)
            {
                if (task.Cursor != 2) return;
                var status = AwaitTerminal(ref task);
                task = task with { Cursor = status == AiBehaviorStatus.Running ? 2 : 0, Status = status };
            }
            void BindReceipt(ref AiBehaviorTask task)
            {
                if (task.Cursor != 1 || task.RequestTick > context.Snapshot.Tick) return;
                var status = Acknowledge(ref task);
                task = task with { Cursor = status == AiBehaviorStatus.Success ? 2 : 0,
                    Status = status == AiBehaviorStatus.Success ? AiBehaviorStatus.Running : status };
            }
            AiBehaviorStatus Submit(ref AiBehaviorTask task)
            {
                if (proposed.Selected == null) return AiBehaviorStatus.Failure;
                selected = proposed.Selected;
                task = new AiBehaviorTask(RequestTick: context.NextTick, ProducerOrdinal: nextProducerOrdinal,
                    Status: AiBehaviorStatus.Running, Reason: "REQUEST_QUEUED", PredecessorPlanId: task.PredecessorPlanId);
                return AiBehaviorStatus.Success;
            }
            AiBehaviorStatus Acknowledge(ref AiBehaviorTask task)
            {
                reason = "WAIT_COMMAND_RESULT";
                if (context.Snapshot.Tick < task.RequestTick) return AiBehaviorStatus.Running;
                var result = feedback.ReadSubmission(context.ControllerId, task.RequestTick, task.ProducerOrdinal);
                if (result == null || result.RejectionCode != null || result.PlanIds.Count != 1)
                {
                    reason = result?.RejectionCode ?? "AI_REQUEST_RESULT_MISSING";
                    task = task with { Reason = reason };
                    return AiBehaviorStatus.Failure;
                }
                task = task with { PlanId = result.PlanIds[0], Reason = "COMMAND_ACCEPTED" };
                return AiBehaviorStatus.Success;
            }
            AiBehaviorStatus AwaitTerminal(ref AiBehaviorTask task)
            {
                if (task.PredecessorPlanId != 0)
                {
                    var previous = feedback.ReadPlan(context.ControllerId, task.PredecessorPlanId);
                    if (previous == null)
                    { task = task with { Reason = reason = "AI_PREDECESSOR_RESULT_MISSING" }; return AiBehaviorStatus.Failure; }
                    if (previous.State < (int)ActionPlanState.Completed)
                    { task = task with { Reason = reason = "WAIT_PREDECESSOR_TERMINAL" }; return AiBehaviorStatus.Running; }
                    task = task with { PredecessorPlanId = 0 };
                }
                var plan = feedback.ReadPlan(context.ControllerId, task.PlanId);
                bool fulfilled = plan != null && (plan.State == (int)ActionPlanState.Completed
                    || plan.State == (int)ActionPlanState.Terminated &&
                        (plan.TerminationReason == (int)ActionTerminationReason.ReactionTriggered
                            || plan.TerminationReason == (int)ActionTerminationReason.GuardQuotaExhausted));
                reason = plan == null ? "AI_PLAN_RESULT_MISSING" :
                    plan.State == (int)ActionPlanState.Completed ? "PLAN_COMPLETED" :
                    fulfilled ? "PLAN_FULFILLED_" + plan.TerminationReason :
                    plan.State == (int)ActionPlanState.Terminated ? "PLAN_TERMINATED_" + plan.TerminationReason : "WAIT_PLAN_TERMINAL";
                task = task with { Reason = reason };
                return fulfilled ? AiBehaviorStatus.Success
                    : plan == null || plan.State == (int)ActionPlanState.Terminated ? AiBehaviorStatus.Failure : AiBehaviorStatus.Running;
            }
        }

        public static IReadOnlyList<string> Unsupported(AiDecisionContext context)
            => context.ControlledUnits.SelectMany(context.Snapshot.ActionSetOf).Distinct()
                .Where(id => context.Snapshot.FindAction(id)?.Type is not
                    (ActionType.Attack or ActionType.Move or ActionType.Guard or ActionType.Block or ActionType.Dodge))
                .Select(id => id.Value).OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }
}
