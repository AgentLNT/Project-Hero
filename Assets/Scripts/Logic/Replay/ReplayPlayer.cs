using System;
using System.Collections.Generic;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Logic.Replay
{
    public sealed record ReplayDeviation(long Tick, string ReasonCode, int EventIndex = -1,
        string Expected = null, string Actual = null);

    /// <summary>Always creates a fresh world. Playback never restores a LogicSnapshot or injects AI input.</summary>
    public sealed class ReplayPlayer : IDisposable
    {
        private readonly BattleDefinition _definition;
        private readonly Func<ReplayHeader, BattleSimulation> _create;
        private BattleSimulation _simulation;
        private BattleReplay _replay;
        private int _recordIndex;
        private int _submissionIndex;
        public bool IsPaused { get; private set; } = true;
        public bool IsComplete => _replay != null && _recordIndex == _replay.Records.Count;
        public ReplayDeviation Deviation { get; private set; }
        public double Speed { get; private set; } = 1;
        public ProjectHero.Logic.Snapshots.LogicSnapshot CurrentSnapshot => _simulation?.CurrentSnapshot;

        public ReplayPlayer(BattleDefinition definition, Func<ReplayHeader, BattleSimulation> create = null)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _create = create ?? (header => definition.FindEncounter(header.EncounterId).TurnSubmission != null
                ? ProductionBattleComposition.Create(definition, header.EncounterId, header.RuntimeInputs)
                : BattleSimulation.Create(definition, header.EncounterId, header.RuntimeInputs));
        }

        public void Load(BattleReplay replay)
        {
            if (replay == null) throw new ArgumentNullException(nameof(replay));
            string error = ReplayHeaderValidation.Validate(replay.Header, _definition);
            if (error != null) throw new LogicDefinitionException(error, "");
            ValidateRecords(replay);
            _replay = replay;
            Restart();
        }

        private void ValidateRecords(BattleReplay replay)
        {
            var ordinals = new Dictionary<string, long>(StringComparer.Ordinal);
            var encounter = _definition.FindEncounter(replay.Header.EncounterId);
            long lastBoundary = -1;
            long lastEventSequence = 0;
            for (int i = 0; i < replay.Records.Count; i++)
            {
                var record = replay.Records[i];
                if (record == null || record.Tick != i || record.Events == null)
                    throw new LogicDefinitionException("REPLAY_TICK_RECORD_INVALID", "tick=" + i);
                // The new recorder preserves submission time separately; never reinterpret target time as submission time.
                if (record.ReplayInputRequests != null && record.ReplayInputRequests.Count != 0)
                    throw new LogicDefinitionException("REPLAY_SUBMISSION_BOUNDARY_MISSING", "tick=" + i);
                foreach (var fact in record.Events)
                {
                    if (fact == null || fact.Tick != record.Tick || fact.Sequence <= lastEventSequence)
                        throw new LogicDefinitionException("REPLAY_EVENT_RECORD_INVALID", "tick=" + i);
                    lastEventSequence = fact.Sequence;
                }
            }
            foreach (var submission in replay.Submissions)
            {
                var fact = submission?.Fact;
                if (fact == null || fact.Request == null || fact.SourceKind != CommandSourceKind.Player)
                    throw new LogicDefinitionException("REPLAY_NON_PLAYER_SOURCE_NOT_AUTHORITATIVE", "");
                bool registered = false;
                foreach (var binding in encounter.Controllers)
                    if (binding.ControllerId == fact.Issuer && binding.SourceKind == CommandSourceKind.Player) registered = true;
                if (!registered) throw new LogicDefinitionException("REPLAY_CONTROLLER_NOT_REGISTERED", fact.Issuer.Value);
                if (submission.SubmittedAtTick < lastBoundary || submission.SubmittedAtTick < -1
                    || submission.SubmittedAtTick >= replay.Records.Count
                    || fact.Request.TargetTick <= submission.SubmittedAtTick)
                    throw new LogicDefinitionException("REPLAY_SUBMISSION_BOUNDARY_INVALID", "");
                ordinals.TryGetValue(fact.Issuer.Value, out long previous);
                if (fact.ProducerOrdinal != previous + 1)
                    throw new LogicDefinitionException("REPLAY_PRODUCER_ORDINAL_INVALID", fact.Issuer.Value);
                ordinals[fact.Issuer.Value] = fact.ProducerOrdinal;
                lastBoundary = submission.SubmittedAtTick;
            }
        }

        public void Restart()
        {
            if (_replay == null) throw new InvalidOperationException("Replay is not loaded");
            _simulation?.Dispose();
            _simulation = _create(_replay.Header);
            string error = ReplayHeaderValidation.ValidateInitialState(_replay.Header, _simulation.InitialStateHash);
            if (error != null) { _simulation.Dispose(); _simulation = null; throw new LogicDefinitionException(error, ""); }
            _recordIndex = 0; _submissionIndex = 0; Deviation = null; IsPaused = true;
        }

        public void Play() { if (Deviation == null && !IsComplete) IsPaused = false; }
        public void Pause() => IsPaused = true;
        public void SetSpeed(double speed)
        {
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0 || speed > 64)
                throw new ArgumentOutOfRangeException(nameof(speed));
            Speed = speed;
        }

        public bool AdvanceOneTick()
        {
            if (IsPaused || IsComplete || Deviation != null) return false;
            var expected = _replay.Records[_recordIndex];
            while (_submissionIndex < _replay.Submissions.Count
                && _replay.Submissions[_submissionIndex].SubmittedAtTick == expected.Tick - 1)
            {
                var fact = _replay.Submissions[_submissionIndex++].Fact;
                var rejection = _simulation.CommandIngress.FindEntry(fact.Issuer).InjectRecordedFact(fact);
                if (rejection != null) return Fail(expected.Tick, rejection.ReasonCode);
            }
            StepResult actual = _simulation.Step(expected.Tick, _simulation.CommandIngress.FreezeTick(expected.Tick));
            if (actual.Status == StepStatus.AlreadyEnded) return Fail(expected.Tick, "REPLAY_RECORD_AFTER_END");
            if (actual.Events.Count != expected.Events.Count) return Fail(expected.Tick, "REPLAY_EVENT_COUNT_MISMATCH");
            for (int i = 0; i < expected.Events.Count; i++)
            {
                string a = ReplayEventComparison.Canonical(actual.Events.Events[i]);
                string e = ReplayEventComparison.Canonical(expected.Events[i]);
                if (!string.Equals(a, e, StringComparison.Ordinal))
                { Deviation = new ReplayDeviation(expected.Tick, "REPLAY_EVENT_MISMATCH", i, e, a); IsPaused = true; return false; }
            }
            ulong hash = actual.Snapshot.ComputeHash();
            if (hash != expected.SnapshotHash)
            {
                Deviation = new ReplayDeviation(expected.Tick, "REPLAY_SNAPSHOT_HASH_MISMATCH", -1,
                    expected.SnapshotHash.ToString("X16"), hash.ToString("X16")); IsPaused = true; return false;
            }
            _recordIndex++;
            if (IsComplete) IsPaused = true;
            return true;
        }

        private bool Fail(long tick, string code)
        { Deviation = new ReplayDeviation(tick, code); IsPaused = true; return false; }
        public void Dispose() { _simulation?.Dispose(); _simulation = null; IsPaused = true; }
    }
}
