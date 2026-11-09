using System;
using System.Collections.Generic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.AI
{
    public sealed record AiSubmissionResult(string RejectionCode, IReadOnlyList<long> PlanIds);
    public interface IAiExecutionFeedback
    {
        AiSubmissionResult ReadSubmission(ControllerId controller, long tick, long producerOrdinal);
        ActionPlanSnapshot ReadPlan(ControllerId controller, long planId);
    }

    /// <summary>Read-only per-command receipts. Written by the real command processor, consumed in the same Tick's AI phase.</summary>
    internal sealed class AiExecutionFeedback : IAiExecutionFeedback
    {
        private long _tick = -1;
        private readonly Dictionary<(string, long), AiSubmissionResult> _results = new();
        private readonly List<long> _created = new();
        private readonly Func<ControllerId, long, ActionPlanSnapshot> _readPlan;
        public AiExecutionFeedback(Func<ControllerId, long, ActionPlanSnapshot> readPlan) => _readPlan = readPlan;
        public void Begin(CommandEnvelope command)
        {
            if (_tick != command.TargetTick) { _results.Clear(); _tick = command.TargetTick; }
            _created.Clear();
        }
        public void Created(long planId) => _created.Add(planId);
        public void End(CommandEnvelope command, string error)
            => _results[(command.ControllerId.Value, command.ProducerOrdinal)] = new AiSubmissionResult(error, _created.ToArray());
        public AiSubmissionResult ReadSubmission(ControllerId controller, long tick, long producerOrdinal)
            => tick == _tick && _results.TryGetValue((controller.Value, producerOrdinal), out var result) ? result : null;
        public ActionPlanSnapshot ReadPlan(ControllerId controller, long planId) => _readPlan(controller, planId);
    }
}
