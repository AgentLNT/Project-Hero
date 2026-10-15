using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.UnityView
{
    public enum BattleClockState { Running, TacticalPlanning, SystemPaused, Ended }
    public enum HudSubmissionPermission { None, OwnWindow, CanIntervene, Intervened }
    public readonly struct HudFacts
    {
        public readonly BattleClockState Clock; public readonly HudSubmissionPermission Permission; public readonly bool HasThreat;
        public HudFacts(BattleClockState clock, HudSubmissionPermission permission, bool hasThreat) { Clock = clock; Permission = permission; HasThreat = hasThreat; }
    }
    public readonly struct HudLayout
    {
        public readonly bool ExpandedTimeline, Configurator, Reactions, Overlay;
        public HudLayout(bool expandedTimeline, bool configurator, bool reactions, bool overlay)
        { ExpandedTimeline = expandedTimeline; Configurator = configurator; Reactions = reactions; Overlay = overlay; }
    }

    public static class HudLayoutResolver
    {
        public static HudLayout Resolve(HudFacts facts)
        {
            if (facts.Clock == BattleClockState.SystemPaused || facts.Clock == BattleClockState.Ended)
                return new HudLayout(false, false, false, true);
            bool paused = facts.Clock == BattleClockState.TacticalPlanning;
            return new HudLayout(paused, paused && (facts.Permission == HudSubmissionPermission.OwnWindow
                || facts.Permission == HudSubmissionPermission.Intervened), facts.HasThreat, false);
        }
    }

    /// <summary>Driver-side gate; observes committed projections only and never calls Step.</summary>
    public sealed class BattleClockController
    {
        public event System.Action Changed;
        private BattleClockState _beforeSystemPause;
        private long _observedWindow;
        private readonly HashSet<long> _observedThreats = new HashSet<long>();
        public BattleClockState State { get; private set; }
        public bool IsPaused => State != BattleClockState.Running;
        public bool AutoPauseOwnWindow { get; set; } = true;
        public bool AutoPauseThreat { get; set; } = true;
        public bool AutoPauseTermination { get; set; }
        public long FocusUnitId { get; private set; }
        public string PauseReason { get; private set; }
        public void PauseTactically(string reason = "手动暂停", long unit = 0)
        {
            if (State == BattleClockState.Ended) return;
            if (State == BattleClockState.SystemPaused) _beforeSystemPause = BattleClockState.TacticalPlanning;
            else State = BattleClockState.TacticalPlanning;
            PauseReason = reason; FocusUnitId = unit;
            Changed?.Invoke();
        }
        public void ResumeTactically()
        {
            if (State == BattleClockState.TacticalPlanning) { State = BattleClockState.Running; PauseReason = null; Changed?.Invoke(); }
        }
        public void End() { State = BattleClockState.Ended; Changed?.Invoke(); }
        public void SetSystemPaused(bool paused)
        {
            if (State == BattleClockState.Ended) return;
            if (paused && State != BattleClockState.SystemPaused) { _beforeSystemPause = State; State = BattleClockState.SystemPaused; }
            else if (!paused && State == BattleClockState.SystemPaused) State = _beforeSystemPause;
            Changed?.Invoke();
        }
        public void Reset()
        {
            State = BattleClockState.Running; _beforeSystemPause = State; _observedWindow = 0;
            _observedThreats.Clear(); FocusUnitId = 0; PauseReason = null;
        }
        public void Observe(DecisionSnapshot decision, TurnWindowSnapshot window, HudPreviewService preview, EventBatch events)
        {
            if (decision.BattleEnd.IsEnded) { End(); return; }
            if (window != null && window.WindowId != _observedWindow)
            {
                _observedWindow = window.WindowId;
                if (AutoPauseOwnWindow && decision.ControlledUnitIds.Any(u => u.Value == window.OwnerUnitId))
                    PauseTactically("我方窗口打开", window.OwnerUnitId);
            }
            // Remember new opportunities even when impossible: do not repeatedly interrupt on refresh.
            bool selectedThreat = false;
            foreach (var threat in (events?.Events ?? System.Array.Empty<LogicEvent>()).OfType<ReactionOpportunityOpenedEvent>()
                .OrderBy(t => t.SourceImpactTick).ThenBy(t => t.ReactionOpportunityId))
            {
                if (!_observedThreats.Add(threat.ReactionOpportunityId) || !AutoPauseThreat
                    || !decision.ControlledUnitIds.Any(u => u.Value == threat.DefenderUnitId)) continue;
                bool feasible = (preview?.ReactionOptions ?? System.Array.Empty<ReactionFeasibility>())
                    .Where(o => o.OpportunityId == threat.ReactionOpportunityId)
                    .Any(o => o.IsFeasible || (o.RejectionCode == "REACTION_LANE_INTERVAL_OCCUPIED"
                        && o.ConflictingPlans.Count > 0 && o.AvailableAdrenaline >= o.RequiredAdrenaline
                        && o.ConflictingPlans.All(p => p.State == (int)ActionPlanState.Editable && p.Origin == (int)ActionPlanOrigin.Ordinary)));
                if (feasible && !selectedThreat) { PauseTactically("发现可响应的公开威胁", threat.DefenderUnitId); selectedThreat = true; }
            }
            if (AutoPauseTermination && events != null)
                foreach (var ended in events.Events.OfType<ActionPlanTerminatedEvent>())
                    if (decision.ControlledUnitIds.Any(u => u.Value == ended.OwnerUnitId))
                        PauseTactically("计划已中止：" + ended.ReasonCode, ended.OwnerUnitId);
        }
    }
}
