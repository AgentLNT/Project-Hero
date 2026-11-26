using System;
using System.Collections.Generic;
using System.Globalization;
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
    public sealed record AiTargetDefense(long UnitId, long DamageQ10, long OverkillQ10, bool LegalReaction, long GuardDamageQ10);

    /// <summary>Samples actions by their utility relative to the same public forecast without a new command.</summary>
    public static class AiPressurePolicy
    {
        public static AiDecision Decide(AiDecisionContext context, ulong seed, AiPolicyKind policy)
        {
            var settings = context.ForecastSettings.Sampling;
            var rng = new DeterministicRng(AiDecisionFunction.DeriveSeed(seed, context.ControllerId.Value, context.Snapshot.Tick));
            var evaluations = new List<AiCandidateEvaluation>();
            var memory = context.SamplingMemory;
            CommandRequest selected = null;
            if (context.ForecastFactory == null) return Finish("FORECAST_UNAVAILABLE");
            using var forecast = context.ForecastFactory(context.Snapshot);
            AiActionForecast baseline = null;
            var reactions = AiTacticalCandidateBuilder.Reactions(context);
            var choice = Choose(reactions, true);
            if (choice != null) { selected = choice.Request; return Finish(null); }
            if (!context.HasSubmissionWindow) return Finish("WAIT_NO_OWN_WINDOW");
            if (context.Snapshot.Tick % AiTacticalPolicy.OrdinaryThinkInterval != 0) return Finish("WAIT_ORDINARY_CADENCE");
            if(context.ForecastSettings.ShortPlans!=null && context.Snapshot.OwnPlans.Any(p=>p.Origin==(int)ActionPlanOrigin.Ordinary))
                return Finish("WAIT_SHORT_PLAN_STEP_TERMINAL");
            var ordinary = AiTacticalCandidateBuilder.Ordinary(context);
            if (ordinary.Count == 0) return Finish("WAIT_LANE_COMMITTED");
            IReadOnlyList<AiTacticalCandidate> deferredMoves = Array.Empty<AiTacticalCandidate>();
            if(context.ForecastSettings.ShortPlans!=null && !context.Snapshot.VisiblePlans.Any(p=>p.ImpactTick>=context.NextTick
                && context.Snapshot.FindAction(new ActionSpecId(p.ActionSpecId))?.Type==ActionType.Attack
                && context.ControlledUnits.Any(id=>context.Snapshot.Classify(id,new UnitId(p.OwnerUnitId))==UnitRelation.Hostile)))
            {
                deferredMoves = ordinary.Where(c => c.Type == ActionType.Move).ToArray();
                ordinary=ordinary.Where(c=>c.Type!=ActionType.Move).ToArray();
            }
            var candidates = new List<AiTacticalCandidate>(ordinary);
            var previews = new Dictionary<string, AiActionForecast>(StringComparer.Ordinal);
            foreach (var c in ordinary)
            {
                var value = forecast.Evaluate(c.Request, context.ForecastSettings); previews[c.Key] = value;
                candidates.AddRange(AiTacticalCandidateBuilder.Aligned(context, c, value));
            }
            if(context.ForecastSettings.ShortPlans!=null)
            {
                baseline ??= forecast.Evaluate(null,context.ForecastSettings);
                foreach(var plan in AiShortPlanCandidateBuilder.Build(context,ordinary,previews,baseline))
                {
                    var outcome=EvaluateCandidate(plan);previews[plan.Key]=outcome;candidates.Add(plan);
                    var value=AiTacticalPolicy.Evaluate(context,policy,plan,baseline,outcome);
                    if(value.Score.HasValue && AiShortPlanCandidateBuilder.HasTacticalEffect(context,value,baseline,outcome))continue;
                    // The complete two-step proposal remains logged, including uncovered outcomes.
                    // A completed, useful first stage is a separately labelled approach proposal.
                    var approach=plan with {Key=plan.Key+"/approach",FollowUp=null};
                    candidates.Add(approach);previews[approach.Key]=forecast.Evaluate(approach.Request,context.ForecastSettings);
                }
                candidates.AddRange(AiContinuationCandidateBuilder.Build(context,ordinary,previews,baseline));
            }
            choice = Choose(candidates, false, previews, deferredMoves);
            selected = choice?.Request;
            return Finish(selected == null ? "WAIT_NO_ADMITTED_ACTION" : null);

            AiTacticalCandidate Choose(IReadOnlyList<AiTacticalCandidate> candidates, bool reaction,
                Dictionary<string, AiActionForecast> cached = null, IReadOnlyList<AiTacticalCandidate> deferred = null)
            {
                var pool = new List<AiSampleItem>();
                var fallbackPool = new List<AiSampleItem>();
                AiTacticalCandidate admitted = null;
                Collect(candidates);
                if (pool.Count == 0 && deferred != null) Collect(deferred);
                bool usingFallback = pool.Count == 0;
                if (usingFallback) pool = fallbackPool;
                if (pool.Count == 0)
                {
                    if (admitted != null)
                    {
                        int i = evaluations.FindIndex(e => e.Key == admitted.Key);
                        evaluations[i] = evaluations[i] with { Chosen = true, Reason = "SELECTED_ADMITTED_WITHOUT_SCORE" };
                    }
                    return admitted;
                }

                if (reaction)
                {
                    string opportunities = string.Join(";", context.Opportunities.OrderBy(o => o.ReactionOpportunityId.Value)
                        .Select(o => o.ReactionOpportunityId.Value.ToString(CultureInfo.InvariantCulture)));
                    string admission = string.Join(";", pool.Select(p => p.Key)) + "/" + string.Join(";", context.Snapshot.VisibleUnits
                        .OrderBy(u => u.UnitId).Select(u => FormattableString.Invariant($"{u.UnitId}:{u.HealthQ10}:{u.AvailableAdrenaline}:{u.AdrenalineCycleId}:{u.X}:{u.Y}:{u.State}")));
                    memory = new AiSamplingMemory(opportunities, admission, memory?.OpportunityKey == opportunities ? memory.DrawCount + 1 : 1);
                }
                var sample = AiSoftmax.Sample(pool, settings, rng);
                for (int i = 0; i < evaluations.Count; i++)
                {
                    var p = sample.Probabilities.FirstOrDefault(x => x.Key == evaluations[i].Key);
                    if (p != null) evaluations[i] = evaluations[i] with { ProbabilityBeforeQ32 = p.BeforeQ32, ProbabilityAfterQ32 = p.AfterQ32,
                        Sampled = p.Key == sample.Key, Chosen = p.Key == sample.Key,
                        Reason = p.Key == sample.Key
                            ? (usingFallback ? "SELECTED_LEGAL_FALLBACK" : "SELECTED_TEMPERATURE_SAMPLE")
                            : "TEMPERATURE_POOL" };
                }
                return candidates.FirstOrDefault(c => c.Key == sample.Key) ?? deferred?.FirstOrDefault(c => c.Key == sample.Key);

                void Collect(IReadOnlyList<AiTacticalCandidate> source)
                {
                    foreach (var candidate in source.OrderBy(c => c.Key, StringComparer.Ordinal))
                    {
                        var outcome = cached != null && cached.TryGetValue(candidate.Key, out var v) ? v : EvaluateCandidate(candidate);
                        if (outcome.RejectionCode == null && baseline == null) baseline = forecast.Evaluate(null, context.ForecastSettings);
                        var e = AiTacticalPolicy.Evaluate(context, policy, candidate, baseline ?? outcome, outcome);
                        if (e.Score.HasValue && e.RejectionCode == null)
                        {
                            e = Score(context, policy, candidate, baseline, outcome, e, forecast);
                            if (context.ForecastSettings.ShortPlans is AiShortPlanSettings plans && e.Score.HasValue && !candidate.IsReaction)
                            {
                                bool approach = candidate.FollowUp == null && candidate.Approach != null;
                                bool intent = approach
                                    ? candidate.Approach.InitialPathWeight > candidate.Approach.RemainingPathWeight
                                        && outcome.CandidateTerminalReason == 0 && e.PositionProgress > 0
                                    : candidate.Type == ActionType.Move && candidate.FollowUp == null
                                        ? e.IncomingDeltaQ10 < 0
                                        : AiShortPlanCandidateBuilder.HasTacticalEffect(context, e, baseline, outcome);
                                long pressure = 0, progress = 0;
                                if (intent && (candidate.Type == ActionType.Attack || candidate.FollowUp != null))
                                    pressure = PressureBonus(context, policy, baseline, outcome);
                                if (intent && approach)
                                    progress = (long)plans.ApproachValue * (candidate.Approach.InitialPathWeight - candidate.Approach.RemainingPathWeight)
                                        / Math.Max(1, candidate.Approach.InitialPathWeight);
                                long total = e.Score.Value + pressure + progress;
                                e = e with { Score = Math.Min(total, e.OrdinaryEvasionScoreCap ?? total), PressureUtility = pressure,
                                    ApproachUtility = progress, IntentEligible = intent, Reason = intent ? e.Reason : "NO_TACTICAL_EFFECT" };
                            }
                            if (e.Score.HasValue)
                            {
                                string group = Group(candidate);
                                e = e with { SampleGroup = group };
                                var item = new AiSampleItem(e.Key, group, e.Score.Value);
                                if (e.IntentEligible) pool.Add(item);
                                else if (candidate.FollowUp == null) fallbackPool.Add(item);
                            }
                        }
                        if (candidate.FollowUp == null && outcome.HasAdmittedPlan) admitted ??= candidate;
                        if(context.ForecastSettings.ShortPlans!=null)e=e with {PlanKind=candidate.IsReaction?"Reaction":candidate.FollowUp?.AttackPrefix==true?"AttackAttack":candidate.FollowUp!=null?"MoveAttack"
                            :candidate.Approach!=null?"Approach":candidate.Type==ActionType.Attack?"DirectAttack":candidate.Type.ToString(),
                            FollowUpSpecId=(candidate.FollowUp?.ActionSpecId ?? candidate.Approach?.AttackSpecId)?.Value,
                            FollowUpTargetId=candidate.FollowUp?.TargetUnitId.Value ?? candidate.Approach?.TargetUnitId.Value ?? 0,
                            FirstStepEndTick=candidate.FollowUp==null?outcome.PlanEndTick:outcome.FirstStepEndTick,
                            FollowUpRequestTick=outcome.FollowUpRequestTick,FollowUpStartTick=outcome.FollowUpStartTick,BridgePlanId=outcome.BridgePlanId};
                        evaluations.Add(e);
                    }
                }
            }
            AiDecision Finish(string reason) => new AiDecision(selected, Array.Empty<AiCandidate>(), Array.Empty<AiReactionOptionView>(),
                rng.CaptureSnapshot(), evaluations.Count(e => !e.IsReaction), evaluations.Count(e => e.IsReaction), reason, context.Snapshot.Tick)
                { Policy = policy, Evaluations = evaluations.AsReadOnly(), SamplingMemory = memory };

            AiActionForecast EvaluateCandidate(AiTacticalCandidate c)=>c.FollowUp==null?forecast.Evaluate(c.Request,context.ForecastSettings)
                :forecast is IAiSequenceForecast sequences?sequences.EvaluateSequence(c.Request,c.FollowUp,context.ForecastSettings)
                :new AiActionForecast("AI_SEQUENCE_FORECAST_UNAVAILABLE",context.Snapshot.Tick+context.ForecastSettings.LookaheadTicks,
                    Array.Empty<UnitSnapshot>(),-1,0,0,Coverage:AiForecastCoverage.Rejected);
        }

        static string Group(AiTacticalCandidate c)
        {
            if (c.Request.Payload is ScheduleEditPayload edit)
            {
                var add = edit.Operations.OfType<AddOrdinaryPlanOperation>().Single();
                if(c.FollowUp!=null)return FormattableString.Invariant($"PLAN/{add.OwnerUnitId.Value}/{(c.FollowUp.AttackPrefix?"AttackAttack":"MoveAttack")}/{c.FollowUp.ActionSpecId.Value}");
                if(c.Approach!=null)return FormattableString.Invariant($"PLAN/{add.OwnerUnitId.Value}/Approach/{add.ActionSpecId.Value}");
                return FormattableString.Invariant($"N/{add.OwnerUnitId.Value}/{add.ActionSpecId.Value}");
            }
            var reaction = (ReactionCommandPayload)c.Request.Payload;
            return FormattableString.Invariant($"R/{((ReactionCommandScope)c.Request.Scope).ReactionOpportunityId.Value}/{reaction.ReactionActionSpecId.Value}");
        }

        public static AiCandidateEvaluation Score(AiDecisionContext context, AiPolicyKind policy, AiTacticalCandidate candidate,
            AiActionForecast baseline, AiActionForecast outcome, AiCandidateEvaluation original, IAiForecast forecast,bool evaluateReplies=true)
        {
            var settings = context.ForecastSettings.Sampling; var weights = AiUtilityProfile.For(policy);
            long score = original.Score.Value;
            long actorId = candidate.Request.Scope is ReactionCommandScope actorScope
                ? context.Opportunities.First(o => o.ReactionOpportunityId == actorScope.ReactionOpportunityId).DefenderUnitId.Value
                : ((ScheduleEditPayload)candidate.Request.Payload).Operations.OfType<AddOrdinaryPlanOperation>().Single().OwnerUnitId.Value;
            var actor = context.Snapshot.VisibleUnits.First(u => u.UnitId == actorId);
            long resetTick = baseline.ResourceExpiries.FirstOrDefault(x => x.UnitId == actorId)?.ResetTick ?? baseline.HorizonTick;
            original = original with { ActorHealthQ10 = actor.HealthQ10, ActorMaximumHealthQ10 = MaximumHealth(context, actor),
                AvailableResource = context.AdrenalineLedgers.First(l => l.UnitId == actorId).AvailableAdrenaline, ResourceResetTick = resetTick };
            long health = -(original.IncomingDeltaQ10 ?? 0) * weights.Incoming, resource = 0, survival = 0;
            var current = context.Snapshot.VisibleUnits;
            if (settings.HealthDefense)
            {
                health = 0;
                foreach (var own in current.Where(u => context.ControlledUnits.Any(id => context.Snapshot.Classify(id, new UnitId(u.UnitId)) is UnitRelation.Self or UnitRelation.Allied)))
                {
                    var before = baseline.Units.FirstOrDefault(u => u.UnitId == own.UnitId);
                    var after = outcome.Units.FirstOrDefault(u => u.UnitId == own.UnitId);
                    long maximum = MaximumHealth(context, own);
                    health += (long)((HealthValue(after?.HealthQ10 ?? 0, maximum, settings.LowHealthBoostMilli)
                        - HealthValue(before?.HealthQ10 ?? 0, maximum, settings.LowHealthBoostMilli)) * weights.Incoming);
                    if ((before?.HealthQ10 ?? 0) <= 0 && (after?.HealthQ10 ?? 0) > 0) survival += settings.SurvivalValue;
                    if ((before?.HealthQ10 ?? 0) > 0 && (after?.HealthQ10 ?? 0) <= 0) survival -= settings.SurvivalValue;
                }
                score += health + (original.IncomingDeltaQ10 ?? 0) * weights.Incoming + survival;
                if (candidate.Cost > 0)
                {
                    long owner = candidate.Request.Scope is ReactionCommandScope scope
                        ? context.Opportunities.First(o => o.ReactionOpportunityId == scope.ReactionOpportunityId).DefenderUnitId.Value : context.ControlledUnits[0].Value;
                    var ledger = context.AdrenalineLedgers.First(l => l.UnitId == owner);
                    long reset = baseline.ResourceExpiries.FirstOrDefault(x => x.UnitId == owner)?.ResetTick ?? baseline.HorizonTick;
                    int scale = context.Snapshot.Definition.AdrenalineRules.UseCombatValuesV05 ? CombatValuesV05.ResourceUnitsPerPoint : 1;
                    resource = ResourceCost(candidate.Cost, ledger.AvailableAdrenaline, reset - context.Snapshot.Tick, scale, settings.ResourceValue);
                    score += (long)candidate.Cost * weights.Resource / scale - resource;
                }
            }
            long damageUtility = (original.DamageDeltaQ10 ?? 0) * weights.Damage, overkill = 0;
            var targets = new List<AiTargetDefense>();
            if (evaluateReplies && (candidate.Type == ActionType.Attack || candidate.FollowUp!=null) && context.ForecastSettings.ConsiderDefenses)
            {
                if (!(forecast is IAiDefenseForecast defenses)) return original with { Score = null, Reason = "DEFENSE_FORECAST_UNAVAILABLE" };
                var replies = candidate.FollowUp!=null && forecast is IAiSequenceForecast sequences
                    ?sequences.EvaluateSequenceDefenses(candidate.Request,candidate.FollowUp,context.ForecastSettings)
                    :defenses.EvaluateDefenses(candidate.Request, context.ForecastSettings);
                long? evasionCap=null;
                var diagnostics = replies.Select(r =>
                {
                    if(!r.IsOrdinaryEvasion)return new AiDefenseEvaluation(r.Key,r.Defender.Value,r.SubmittedAfterTick,
                        r.Request.Payload is ReactionCommandPayload p?p.ReactionKind==ReactionCommandKind.Block?ActionType.Block:ActionType.Dodge:ActionType.Guard,
                        null,null,r.Outcome.RejectionCode,r.Outcome.Coverage,r.Outcome.RequiredThroughTick);
                    var value=AiTacticalPolicy.Evaluate(context,policy,candidate,baseline,r.Outcome);
                    long? cap=null;
                    if(value.Score.HasValue && value.DamageDeltaQ10<original.DamageDeltaQ10)
                    {
                        var realized=Score(context,policy,candidate,baseline,r.Outcome,value,forecast,false);
                        cap=realized.Score+PressureBonus(context,policy,baseline,r.Outcome);
                        evasionCap=Math.Min(evasionCap??cap.Value,cap.Value);
                    }
                    return new AiDefenseEvaluation(r.Key,r.Defender.Value,r.SubmittedAfterTick,ActionType.Move,cap,value.DamageDeltaQ10,
                        r.Outcome.RejectionCode,r.Outcome.Coverage,r.Outcome.RequiredThroughTick);
                }).ToArray();
                if (replies.Any(r => !r.IsOrdinaryEvasion && r.Outcome.RejectionCode == null && r.Outcome.Coverage != AiForecastCoverage.Complete))
                    return original with { Score = null, Coverage = AiForecastCoverage.Uncovered, DefenseResponses = diagnostics, Reason = "DEFENSE_OUTCOME_NOT_COVERED" };
                if(context.ForecastSettings.Continuations!=null)
                {
                    // Compare entire realized branches. A response can spend resources, earn rewards,
                    // move or be reset before B; the kernel decides B's available replies in that state.
                    var control=Score(context,policy,candidate,baseline,outcome,original,forecast,false);
                    var worst=control;
                    var evaluated=new List<AiDefenseEvaluation>();
                    foreach(var reply in replies)
                    {
                        var value=AiTacticalPolicy.Evaluate(context,policy,candidate,baseline,reply.Outcome);
                        if(value.Score.HasValue)value=Score(context,policy,candidate,baseline,reply.Outcome,value,forecast,false);
                        evaluated.Add(new AiDefenseEvaluation(reply.Key,reply.Defender.Value,reply.SubmittedAfterTick,
                            reply.Request.Payload is ReactionCommandPayload p?p.ReactionKind==ReactionCommandKind.Block?ActionType.Block:ActionType.Dodge:ActionType.Guard,
                            value.Score,value.DamageDeltaQ10,reply.Outcome.RejectionCode,reply.Outcome.Coverage,reply.Outcome.RequiredThroughTick));
                        if(reply.Outcome.RejectionCode!=null)return original with {Score=null,Reason="CONTINUATION_REPLY_REJECTED",DefenseResponses=evaluated};
                        if(value.Score.HasValue && value.Score.Value<worst.Score.Value)worst=value;
                    }
                    return original with {Score=worst.Score,DamageUtility=worst.DamageUtility,HealthUtility=worst.HealthUtility,
                        ResourceUtility=worst.ResourceUtility,SurvivalUtility=worst.SurvivalUtility,NoDefenseScore=control.Score,
                        NoDefenseDamageDeltaQ10=original.DamageDeltaQ10,DefenseResponses=evaluated,WorstDefenseKey=worst==control?null:"FINITE_RESPONSE_BRANCH"};
                }
                if (!settings.SignedDamage)
                {
                    // B keeps the previous worst-reply utility, while permitting negative samples.
                    long worst = score;
                    foreach (var reply in replies.Where(r => r.Outcome.RejectionCode == null))
                        worst = Math.Min(worst, AiTacticalPolicy.Evaluate(context, policy, candidate, baseline, reply.Outcome).Score ?? worst);
                    return original with { Score = worst, NoDefenseScore = original.Score, NoDefenseDamageDeltaQ10 = original.DamageDeltaQ10, DefenseResponses = diagnostics };
                }
                long signed = 0;
                foreach (var target in baseline.Units.Where(u => context.ControlledUnits.Any(id => context.Snapshot.Classify(id, new UnitId(u.UnitId)) == UnitRelation.Hostile)))
                {
                    var after = outcome.Units.FirstOrDefault(u => u.UnitId == target.UnitId);
                    long d0 = Math.Max(0, target.HealthQ10) - Math.Max(0, after?.HealthQ10 ?? 0);
                    long excess = Math.Max(0, -(long)(after?.HealthQ10 ?? 0)) - Math.Max(0, -(long)target.HealthQ10);
                    // Reply keys identify the actual opportunity/owner via forecast metadata, including multi-unit controllers.
                    var relevant = replies.Where(r => r.Outcome.RejectionCode == null && r.DefenderUnitId == target.UnitId).ToArray();
                    bool legal = relevant.Any(r => r.Request.Payload is ReactionCommandPayload);
                    long guard = relevant.Where(r => !r.IsOrdinaryEvasion && r.Request.Payload is ScheduleEditPayload)
                        .Select(r => (long)Math.Max(0, target.HealthQ10) - Math.Max(0, r.Outcome.Units.FirstOrDefault(u => u.UnitId == target.UnitId)?.HealthQ10 ?? 0)).DefaultIfEmpty(d0).Min();
                    signed += legal ? -d0 : guard;
                    overkill += excess; targets.Add(new AiTargetDefense(target.UnitId, d0, excess, legal, guard));
                }
                score += (signed - (original.DamageDeltaQ10 ?? 0)) * weights.Damage;
                damageUtility = signed * weights.Damage;
                original = original with { NoDefenseScore = original.Score, NoDefenseDamageDeltaQ10 = original.DamageDeltaQ10,
                    DefenseResponses = diagnostics,OrdinaryEvasionScoreCap=evasionCap };
            }
            return original with { Score = score, DamageUtility = damageUtility, HealthUtility = health, ResourceUtility = -resource,
                SurvivalUtility = survival, OverkillQ10 = overkill, TargetDefenses = targets.AsReadOnly() };
        }

        static long PressureBonus(AiDecisionContext context,AiPolicyKind policy,AiActionForecast baseline,AiActionForecast outcome)
        {
            int value=context.ForecastSettings.ShortPlans?.PressureValueQ10??0;
            if(value==0)return 0;
            int targets=baseline.Units.Count(u=>context.ControlledUnits.Any(id=>context.Snapshot.Classify(id,new UnitId(u.UnitId))==UnitRelation.Hostile)
                && Math.Max(0,u.HealthQ10)>Math.Max(0,outcome.Units.FirstOrDefault(a=>a.UnitId==u.UnitId)?.HealthQ10??0));
            return (long)value*AiUtilityProfile.For(policy).Damage*targets;
        }

        public static decimal HealthValue(long health, long maximum, int boostMilli)
        {
            decimal h = Math.Max(0, Math.Min(health, maximum)), m = Math.Max(1, maximum);
            return h + boostMilli / 1000m * (h - h * h / m + h * h * h / (3 * m * m));
        }
        public static long ResourceCost(int cost, int available, long ticksToReset, int resourceScale, int value)
            => (long)((decimal)cost * value / resourceScale * (1 + (decimal)cost / Math.Max(cost, available))
                * Math.Max(0, Math.Min(120, ticksToReset)) / 120);
        static long MaximumHealth(AiDecisionContext c, UnitSnapshot u)
            => (long)((decimal)c.Snapshot.Definition.FindUnit(new UnitDefinitionId(u.DefinitionId)).InitialHealth * 1024);
    }
}
