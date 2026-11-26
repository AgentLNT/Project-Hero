using System;
using System.IO;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.UnityView.Replay
{
    /// <summary>Called developer tool; no Update, coroutine, scene writes, or arbitrary seek.</summary>
    public sealed class DeveloperReplayControls : IDisposable
    {
        private readonly BattleDefinition _definition;
        private readonly Func<ReplayHeader, BattleSimulation> _create;
        private double _accumulator;
        public ReplayPlayer Player { get; private set; }
        public DeveloperReplayControls(BattleDefinition definition, Func<ReplayHeader, BattleSimulation> create = null)
        { _definition = definition; _create = create; Player = new ReplayPlayer(definition, create); }
        public void Load(Stream stream)
        {
            var replay = ReplayFile.Load(stream, _definition, out var matchedDefinition);
            var next = new ReplayPlayer(matchedDefinition, ReferenceEquals(matchedDefinition, _definition) ? _create : null);
            try { next.Load(replay); }
            catch { next.Dispose(); throw; }
            Player.Dispose();
            Player = next;
            _accumulator = 0;
        }
        public void Play() => Player.Play();
        public void Pause() => Player.Pause();
        public void SetSpeed(double speed) => Player.SetSpeed(speed);
        public void Restart() { Player.Restart(); _accumulator = 0; }
        public void AdvanceFrame(double unscaledSeconds)
        {
            if (Player.IsPaused) return;
            if (double.IsNaN(unscaledSeconds) || double.IsInfinity(unscaledSeconds) || unscaledSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(unscaledSeconds));
            _accumulator += unscaledSeconds * Player.Speed * _definition.TicksPerSecond;
            int processed = 0;
            while (_accumulator >= 1 && processed++ < 64 && !Player.IsPaused)
            { _accumulator -= 1; if (!Player.AdvanceOneTick()) break; }
        }
        public void Dispose() => Player.Dispose();
    }
}
