using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.AI
{
    public sealed record AiApproachChoice(AiTacticalCandidate Candidate, string Reason);

    /// <summary>Stateless, opt-in engagement goal after ordinary utility chooses Wait.</summary>
    public static class AiApproachPolicy
    {
        public const string Version = "public-unthreatened-approach-v1";
        public const string SelectedReason = "SELECTED_UNTHREATENED_APPROACH";

        public static AiApproachChoice Choose(AiDecisionContext context, IReadOnlyList<AiTacticalCandidate> candidates,
            IReadOnlyList<AiCandidateEvaluation> evaluations, IReadOnlyDictionary<string, AiActionForecast> previews)
        {
            if (context.ForecastSettings?.ApproachWhenUnthreatened != true) return new AiApproachChoice(null, null);
            if (!context.HasSubmissionWindow) return Wait("WAIT_APPROACH_NO_OWN_WINDOW");
            if (context.Snapshot.Tick % AiTacticalPolicy.OrdinaryThinkInterval != 0) return Wait("WAIT_ORDINARY_CADENCE");
            if (context.Snapshot.VisiblePlans.Any(p => DecisionSnapshot.IsPubliclyRevealed(p)
                && p.ImpactTick >= context.NextTick
                && context.Snapshot.FindAction(new ActionSpecId(p.ActionSpecId))?.Type == ActionType.Attack
                && context.ControlledUnits.Any(id => context.Snapshot.Classify(id, new UnitId(p.OwnerUnitId)) == UnitRelation.Hostile))
                || context.Opportunities.Any(o => o.SourceImpactTick >= context.NextTick && o.IsOfferedTo(context.ControlledUnits, context.Snapshot.Tick)))
                return Wait("WAIT_APPROACH_PUBLIC_THREAT");

            var attacks = evaluations.Where(e => !e.IsReaction && e.Type == ActionType.Attack && e.RejectionCode == null).ToArray();
            if (attacks.Any(e => e.Coverage != AiForecastCoverage.Complete)) return Wait("WAIT_APPROACH_ATTACK_OUTCOME_UNCOVERED");
            if (attacks.Length == 0) return Wait("WAIT_APPROACH_NO_ATTACK_OPTION");
            // A blocked attack can still establish that the actor is already in useful range.
            // Pressure against a funded defender is a separate goal, not a reason to keep closing.
            if (attacks.Any(e => (e.NoDefenseDamageDeltaQ10 ?? e.DamageDeltaQ10) > 0))
                return Wait("WAIT_APPROACH_ATTACK_AVAILABLE");

            AiTacticalCandidate best = null; AiCandidateEvaluation bestValue = null;
            foreach (var candidate in candidates.Where(c => c.Type == ActionType.Move && !c.IsReaction)
                .OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                var value = evaluations.FirstOrDefault(e => e.Key == candidate.Key);
                if (value == null || value.RejectionCode != null || value.Coverage != AiForecastCoverage.Complete
                    || !value.Score.HasValue || !(value.PositionProgress > 0) || !(value.IncomingDeltaQ10 <= 0)
                    || !(value.DamageDeltaQ10 >= 0) || !previews.TryGetValue(candidate.Key, out var outcome)
                    || outcome.CandidateTerminalReason != 0) continue;
                if (best == null || Better(value, bestValue)) { best = candidate; bestValue = value; }
            }
            return best == null ? Wait("WAIT_APPROACH_NO_SAFE_PROGRESS") : new AiApproachChoice(best, SelectedReason);

            AiApproachChoice Wait(string reason) => new AiApproachChoice(null, reason);
            long Duration(AiCandidateEvaluation value) => Math.Max(1, value.EndTick - context.NextTick);
            bool Better(AiCandidateEvaluation a, AiCandidateEvaluation b)
            {
                // Compare progress/Tick without floating point. Equal rates prefer shorter commitments.
                long aTime = Duration(a), bTime = Duration(b);
                long left = checked(a.PositionProgress.Value * bTime), right = checked(b.PositionProgress.Value * aTime);
                if (left != right) return left > right;
                if (aTime != bTime) return aTime < bTime;
                return a.Score > b.Score; // Exact ties retain the first ordinal key.
            }
        }
    }
}
