using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Events;

namespace ProjectHero.UnityView
{
    public sealed class CombatFeedbackCue
    {
        public CombatFeedbackCue(string kind, long unitId, long planId, LogicEvent fact)
        { Kind = kind; UnitId = unitId; PlanId = planId; Fact = fact; }
        public string Kind { get; }
        public long UnitId { get; }
        public long PlanId { get; }
        public LogicEvent Fact { get; }
    }
    /// <summary>Maps committed facts to local policy. No simulation reference or logic write methods.</summary>
    public sealed class CombatFeedbackMapper
    {
        private long _lastSequence;
        private readonly HashSet<long> _triggered = new HashSet<long>();
        private readonly HashSet<long> _dodged = new HashSet<long>();
        public event Action<CombatFeedbackCue> FeedbackRequested;
        public int BattleEndFeedbackCount { get; private set; }
        public void Reset() { _lastSequence = 0; _triggered.Clear(); _dodged.Clear(); BattleEndFeedbackCount = 0; }
        public void Consume(LogicEvent fact)
        {
            if (fact == null || fact.Sequence <= _lastSequence) return;
            _lastSequence = fact.Sequence;
            CombatFeedbackCue cue = null;
            if (fact is ReactionTriggeredEvent trigger && _triggered.Add(trigger.ReactionActionPlanId))
                cue = new CombatFeedbackCue(((ActionType)trigger.ReactionType).ToString(), trigger.DefenderUnitId,
                    trigger.ReactionActionPlanId, fact);
            else if (fact is DodgeResolvedEvent dodge && dodge.InvalidatedAttackPlanIds.Count > 0
                && _dodged.Add(dodge.DodgePlanId.Value))
                cue = new CombatFeedbackCue("DodgeSuccess", dodge.DefenderUnitId.Value, dodge.DodgePlanId.Value, fact);
            else if (fact is DamageChannelResolvedEvent damage)
                cue = new CombatFeedbackCue("Damage", damage.TargetUnitId.Value, damage.AttackPlanId.Value, fact);
            else if (fact is BlockResolvedEvent block)
                cue = new CombatFeedbackCue("BlockResolved", block.DefenderUnitId.Value, block.BlockPlanId.Value, fact);
            else if (fact is GuardPartiallyResistedEvent guard)
                cue = new CombatFeedbackCue("Guard", guard.DefenderUnitId.Value, guard.GuardPlanId.Value, fact);
            else if (fact is ClashParticipantResolvedEvent clash)
                cue = new CombatFeedbackCue("Clash", clash.OwnerUnitId.Value, clash.ActionPlanId.Value, fact);
            else if (fact is ForcedDisplacementResolvedEvent displacement)
                cue = new CombatFeedbackCue(displacement.AppliedSteps == 0 ? "DisplacementBlocked" : "Displacement",
                    displacement.TargetUnitId.Value, 0, fact);
            else if (fact is UnitStateChangedEvent state)
                cue = new CombatFeedbackCue("StateChanged", state.UnitId.Value, 0, fact);
            else if (fact is BattleEndedEvent && BattleEndFeedbackCount++ == 0)
                cue = new CombatFeedbackCue("BattleEnded", 0, 0, fact);
            if (cue != null) FeedbackRequested?.Invoke(cue);
        }
    }
}
