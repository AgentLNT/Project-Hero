using System;
using System.Collections.Generic;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Logic.Replay
{
    /// <summary>Called at the sole driver boundary, before freezing inputs and after committing Step.</summary>
    public sealed class ReplayRecorder
    {
        private readonly BattleSimulation _simulation;
        private readonly List<ReplayTickRecord> _records = new List<ReplayTickRecord>();
        private readonly List<ReplaySubmission> _submissions = new List<ReplaySubmission>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private bool _awaitingResult;
        public ReplayHeader Header { get; }

        public ReplayRecorder(BattleSimulation simulation)
        {
            _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
            if (simulation.Tick != -1) throw new LogicDefinitionException("REPLAY_RECORDING_MUST_START_BEFORE_STEP_ZERO", "");
            Header = simulation.BuildReplayHeader();
        }

        public void CaptureBeforeStep()
        {
            if (_awaitingResult) throw new LogicDefinitionException("REPLAY_STEP_RESULT_MISSING", "");
            foreach (var submission in _simulation.CommandIngress.CapturePendingPlayerSubmissions())
            {
                string key = submission.Fact.Issuer.Value + ":" + submission.Fact.ProducerOrdinal;
                if (_seen.Add(key)) _submissions.Add(new ReplaySubmission(submission.SubmittedAtTick,
                    submission.Fact with { Request = ReplayFile.CopyRequest(submission.Fact.Request) }));
            }
            _awaitingResult = true;
        }

        public void RecordCommittedStep(StepResult result)
        {
            if (!_awaitingResult || result == null || result.Status == StepStatus.AlreadyEnded)
                throw new LogicDefinitionException("REPLAY_RECORDING_STEP_INVALID", "");
            if (result.Snapshot.Tick != _records.Count)
                throw new LogicDefinitionException("REPLAY_RECORDING_TICK_GAP", "");
            var events = new ProjectHero.Logic.Events.LogicEvent[result.Events.Count];
            for (int i = 0; i < events.Length; i++)
            {
                var fact = result.Events.Events[i];
                events[i] = new StoredReplayEvent(fact.Tick, fact.Sequence, ReplayEventComparison.Canonical(fact));
            }
            _records.Add(new ReplayTickRecord(result.Snapshot.Tick,
                Array.Empty<RecordedCommandRequest>(), Array.AsReadOnly(events),
                result.Snapshot.ComputeHash()));
            _awaitingResult = false;
        }

        public BattleReplay BuildReplay() => new BattleReplay(Header, _records, _submissions);
    }
}
