using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.AI
{
    public enum AiPolicyKind { Legacy = 0, SustainedOffense = 1, SeekInterrupt = 2, CautiousDefense = 3 }
    public sealed record AiCandidateEvaluation(string Key, ActionType Type, bool IsReaction, long? Score,
        string RejectionCode, string Reason, long? DamageDeltaQ10, long? IncomingDeltaQ10, long? PositionProgress,
        int? InterruptedThreats, int? Clashes, int Cost, long StartTick, long ImpactTick, long EndTick, long HorizonTick, bool Chosen = false,
        AiForecastCoverage Coverage = AiForecastCoverage.Unspecified, long ObservedThroughTick = -1, long RequiredThroughTick = 0)
    {
        public long? NoDefenseScore { get; init; }
        public long? NoDefenseDamageDeltaQ10 { get; init; }
        public string WorstDefenseKey { get; init; }
        public IReadOnlyList<AiDefenseEvaluation> DefenseResponses { get; init; } = Array.Empty<AiDefenseEvaluation>();
    }

    public sealed record AiDefenseEvaluation(string Key, string Defender, long SubmittedAfterTick, ActionType Type,
        long? Score, long? DamageDeltaQ10, string RejectionCode, AiForecastCoverage Coverage, long RequiredThroughTick);

    /// <summary>Versioned public-world utility; opt-in attacks also compare bounded single-defense replies.</summary>
    public static class AiTacticalPolicy
    {
        public const string Version = "tactical-public-kernel-v2";
        public const int OrdinaryThinkInterval = 6;

        public static AiDecision Decide(AiDecisionContext context, ulong seed, AiPolicyKind policy)
        {
            if (policy == AiPolicyKind.Legacy) return AiDecisionFunction.Decide(context, seed);
            if (!Enum.IsDefined(typeof(AiPolicyKind), policy)) throw new ArgumentOutOfRangeException(nameof(policy));
            var rng = new DeterministicRng(AiDecisionFunction.DeriveSeed(seed, context.ControllerId.Value, context.Snapshot.Tick));
            var evaluations = new List<AiCandidateEvaluation>();
            CommandRequest selected = null;
            var reactions = AiTacticalCandidateBuilder.Reactions(context);
            bool ordinaryDue = context.HasSubmissionWindow && context.Snapshot.Tick % OrdinaryThinkInterval == 0;
            var ordinary = ordinaryDue ? AiTacticalCandidateBuilder.Ordinary(context) : Array.Empty<AiTacticalCandidate>();
            if (reactions.Count == 0 && ordinary.Count == 0)
                return Finish(!context.HasSubmissionWindow ? "WAIT_NO_OWN_WINDOW" : !ordinaryDue ? "WAIT_ORDINARY_CADENCE" : "WAIT_LANE_COMMITTED");
            if (context.ForecastFactory == null) return Finish("FORECAST_UNAVAILABLE");
            using var forecast = context.ForecastFactory(context.Snapshot);
            AiActionForecast baseline = null;
            // Reactions and ordinary actions never compete. A legal but unhelpful reaction can choose Wait.
            var choice = Choose(reactions);
            if (choice != null) { selected = choice.Request; return Finish(null); }
            if (evaluations.Any(e => e.IsReaction && e.RejectionCode == null))
                return Finish(context.ForecastSettings != null && evaluations.Any(e => e.IsReaction && e.Coverage == AiForecastCoverage.Uncovered)
                    ? "WAIT_REACTION_OUTCOME_UNCOVERED" : "WAIT_REACTION_NOT_BENEFICIAL");
            if (!context.HasSubmissionWindow) return Finish("WAIT_NO_OWN_WINDOW");
            if (context.Snapshot.Tick % OrdinaryThinkInterval != 0) return Finish("WAIT_ORDINARY_CADENCE");
            var candidates = new List<AiTacticalCandidate>(ordinary);
            var previews = new Dictionary<string, AiActionForecast>(StringComparer.Ordinal);
            foreach (var candidate in ordinary)
            {
                var preview = forecast.Evaluate(candidate.Request, context.ForecastSettings); previews.Add(candidate.Key, preview);
                candidates.AddRange(AiTacticalCandidateBuilder.Aligned(context, candidate, preview));
            }
            choice = Choose(candidates, previews);
            string approachWait = null;
            if (choice == null && context.ForecastSettings?.ApproachWhenUnthreatened == true)
            {
                var approach = AiApproachPolicy.Choose(context, candidates, evaluations, previews);
                choice = approach.Candidate; approachWait = approach.Reason;
                if (choice != null)
                    for (int i = 0; i < evaluations.Count; i++)
                        if (evaluations[i].Key == choice.Key)
                            evaluations[i] = evaluations[i] with { Chosen = true, Reason = AiApproachPolicy.SelectedReason };
            }
            if (choice != null) selected = choice.Request;
            return Finish(selected == null ? approachWait ?? (ordinary.Count == 0 ? "WAIT_LANE_COMMITTED"
                : context.ForecastSettings != null && evaluations.Any(e => e.Coverage == AiForecastCoverage.Uncovered)
                    ? "WAIT_NO_POSITIVE_COVERED_CANDIDATE" : "WAIT_NO_POSITIVE_CANDIDATE") : null);

            AiTacticalCandidate Choose(IReadOnlyList<AiTacticalCandidate> candidates, Dictionary<string, AiActionForecast> cached = null)
            {
                AiTacticalCandidate best = null; long bestScore = 0;
                foreach (var candidate in candidates.OrderBy(c => c.Key, StringComparer.Ordinal))
                {
                    var preview = cached != null && cached.TryGetValue(candidate.Key, out var value) ? value : forecast.Evaluate(candidate.Request, context.ForecastSettings);
                    if (preview.RejectionCode == null && baseline == null) baseline = forecast.Evaluate(null, context.ForecastSettings);
                    var evaluation = Evaluate(context, policy, candidate, baseline ?? preview, preview);
                    if (context.ForecastSettings?.ConsiderDefenses == true && candidate.Type == ActionType.Attack && evaluation.Score.HasValue)
                    {
                        evaluation = evaluation with { NoDefenseScore = evaluation.Score, NoDefenseDamageDeltaQ10 = evaluation.DamageDeltaQ10 };
                        // The conservative minimum cannot exceed its no-reply upper bound or beat Wait at <= 0.
                        if (evaluation.Score > 0) evaluation = WithDefenses(context, policy, candidate, baseline, evaluation, forecast);
                    }
                    evaluations.Add(evaluation);
                    if (evaluation.RejectionCode == null && evaluation.Score is long score && score > bestScore)
                    { best = candidate; bestScore = score; }
                }
                if (best != null)
                    for (int i = 0; i < evaluations.Count; i++)
                        if (evaluations[i].Key == best.Key) evaluations[i] = evaluations[i] with { Chosen = true, Reason = "SELECTED_MAX_SCORE_STABLE_KEY" };
                return best;
            }

            AiDecision Finish(string waitReason) => new AiDecision(selected, Array.Empty<AiCandidate>(), Array.Empty<AiReactionOptionView>(),
                rng.CaptureSnapshot(), evaluations.Count(e => !e.IsReaction), evaluations.Count(e => e.IsReaction), waitReason, context.Snapshot.Tick)
                { Policy = policy, Evaluations = evaluations.AsReadOnly() };
        }

        private static AiCandidateEvaluation WithDefenses(AiDecisionContext context, AiPolicyKind policy,
            AiTacticalCandidate candidate, AiActionForecast baseline, AiCandidateEvaluation original, IAiForecast forecast)
        {
            if (!(forecast is IAiDefenseForecast defenses))
                return original with { Score = null, Reason = "DEFENSE_FORECAST_UNAVAILABLE" };
            var diagnostics = new List<AiDefenseEvaluation>();
            var worst = original; string worstKey = null;
            bool uncovered = false; long required = original.RequiredThroughTick;
            foreach (var reply in defenses.EvaluateDefenses(candidate.Request, context.ForecastSettings))
            {
                var evaluation = Evaluate(context, policy, candidate, baseline, reply.Outcome);
                var type = reply.Request.Payload is ReactionCommandPayload reaction
                    ? (reaction.ReactionKind == ReactionCommandKind.Block ? ActionType.Block : ActionType.Dodge) : ActionType.Guard;
                diagnostics.Add(new AiDefenseEvaluation(reply.Key, reply.Defender.Value, reply.SubmittedAfterTick, type,
                    evaluation.Score, evaluation.DamageDeltaQ10, evaluation.RejectionCode, evaluation.Coverage, evaluation.RequiredThroughTick));
                if (evaluation.RejectionCode != null) continue;
                required = Math.Max(required, evaluation.RequiredThroughTick);
                if (evaluation.Coverage != AiForecastCoverage.Complete)
                { uncovered = true; continue; }
                if (evaluation.Score < worst.Score) { worst = evaluation; worstKey = reply.Key; }
            }
            return worst with { NoDefenseScore = original.Score, NoDefenseDamageDeltaQ10 = original.DamageDeltaQ10,
                WorstDefenseKey = worstKey, DefenseResponses = diagnostics.AsReadOnly(),
                Score = uncovered ? null : worst.Score, Coverage = uncovered ? AiForecastCoverage.Uncovered : worst.Coverage,
                RequiredThroughTick = required, Reason = uncovered ? "DEFENSE_OUTCOME_NOT_COVERED" : worst.Reason };
        }

        public static AiCandidateEvaluation Evaluate(AiDecisionContext context, AiPolicyKind policy, AiTacticalCandidate candidate,
            AiActionForecast baseline, AiActionForecast outcome)
        {
            if (context.ForecastSettings != null)
            {
                if (outcome.RejectionCode == null && baseline.HorizonTick != outcome.HorizonTick)
                    throw new LogicDefinitionException("AI_FORECAST_BASELINE_HORIZON_MISMATCH");
                if (outcome.RejectionCode != null || outcome.Coverage != AiForecastCoverage.Complete)
                    return new AiCandidateEvaluation(candidate.Key, candidate.Type, candidate.IsReaction, null,
                        outcome.RejectionCode, outcome.RejectionCode == null ? "OUTCOME_NOT_COVERED" : "REJECTED_BY_LOGIC_PREVIEW",
                        null, null, null, null, null, candidate.Cost, outcome.PlanStartTick, outcome.PlanImpactTick, outcome.PlanEndTick,
                        outcome.HorizonTick, Coverage: outcome.Coverage, ObservedThroughTick: outcome.ObservedThroughTick,
                        RequiredThroughTick: outcome.RequiredThroughTick);
            }
            long damage = 0, incoming = 0, progress = 0;
            int interrupts = outcome.InterruptedThreats - baseline.InterruptedThreats, clashes = outcome.Clashes - baseline.Clashes;
            if (outcome.RejectionCode == null)
            {
                foreach (var unit in baseline.Units)
                {
                    var after = outcome.Units.FirstOrDefault(u => u.UnitId == unit.UnitId);
                    long difference = unit.HealthQ10 - (after?.HealthQ10 ?? 0);
                    if (context.ControlledUnits.Any(id => context.Snapshot.Classify(id, new UnitId(unit.UnitId)) == UnitRelation.Hostile)) damage += difference;
                    else if (context.ControlledUnits.Any(id => context.Snapshot.Classify(id, new UnitId(unit.UnitId)) == UnitRelation.Self
                        || context.Snapshot.Classify(id, new UnitId(unit.UnitId)) == UnitRelation.Allied)) incoming += difference;
                }
                progress = Distance(baseline.Units) - Distance(outcome.Units);
            }
            long duration = Math.Max(0, outcome.PlanEndTick - context.NextTick);
            long latency = outcome.FirstHostileDamageTick < 0 ? 0 : outcome.FirstHostileDamageTick - context.Snapshot.Tick;
            var weights = AiUtilityProfile.For(policy);
            long score = damage * weights.Damage - incoming * weights.Incoming + progress * weights.Position
                + interrupts * weights.Interrupt + clashes * weights.Clash;
            int resourceScale = context.Snapshot.Definition.AdrenalineRules.UseCombatValuesV05 ? CombatValuesV05.ResourceUnitsPerPoint : 1;
            score -= duration * weights.Duration + (long)candidate.Cost * weights.Resource / resourceScale + (damage > 0 ? latency : 0);
            return new AiCandidateEvaluation(candidate.Key, candidate.Type, candidate.IsReaction, score, outcome.RejectionCode,
                outcome.RejectionCode != null ? "REJECTED_BY_LOGIC_PREVIEW" : score <= 0 ? "WAIT_HAS_HIGHER_OR_EQUAL_SCORE" : "LOWER_SCORE_OR_STABLE_TIE",
                damage, incoming, progress, interrupts, clashes, candidate.Cost, outcome.PlanStartTick, outcome.PlanImpactTick, outcome.PlanEndTick, outcome.HorizonTick,
                Coverage: outcome.Coverage, ObservedThroughTick: outcome.ObservedThroughTick, RequiredThroughTick: outcome.RequiredThroughTick);

            long Distance(IReadOnlyList<UnitSnapshot> units)
            {
                long sum = 0;
                foreach (var id in context.ControlledUnits)
                {
                    var own = units.FirstOrDefault(u => u.UnitId == id.Value && u.IsAlive);
                    if (own == null) continue;
                    sum += units.Where(u => u.IsAlive && context.Snapshot.Classify(id, new UnitId(u.UnitId)) == UnitRelation.Hostile)
                        .Select(u => Math.Abs((long)u.X - own.X) + Math.Abs((long)u.Y - own.Y)).DefaultIfEmpty(0).Min();
                }
                return sum;
            }
        }
    }
}
