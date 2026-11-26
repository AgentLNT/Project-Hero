using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Replay;

namespace ProjectHero.Logic.Experiments
{
    public static class AiPressureEvidence
    {
        public static IEnumerable<string> Canonical(AiPolicyExperimentResult result)
        {
            yield return result.Definition.BattleDefinitionHashValue;
            foreach (var tick in result.Replay.Records) yield return "S|" + F(tick.Tick) + "|" + tick.SnapshotHash;
            foreach (var e in result.Events) yield return "E|" + ReplayEventComparison.Canonical(e);
            foreach (var d in result.Decisions)
            {
                yield return "D|" + F(d.DecisionTick) + "|" + d.Rng + "|" + d.NoSelectionReason + "|" + d.SamplingMemory;
                foreach (var e in d.Evaluations)
                {
                    yield return string.Join("|", new[] { "C", e.Key, F(e.Score), F(e.DamageDeltaQ10), F(e.IncomingDeltaQ10), F(e.Chosen),
                        e.RejectionCode, F(e.ProbabilityBeforeQ32), F(e.ProbabilityAfterQ32), F(e.Sampled),
                        F(e.DamageUtility), F(e.HealthUtility), F(e.ResourceUtility), F(e.SurvivalUtility) });
                    if(result.Input.ShortPlans!=null)yield return string.Join("|",new[]{"P",e.Key,e.PlanKind,e.FollowUpSpecId,F(e.FollowUpTargetId),
                        F(e.FirstStepEndTick),F(e.FollowUpRequestTick),F(e.FollowUpStartTick),F(e.PressureUtility),F(e.ApproachUtility),F(e.IntentEligible)});
                }
            }
        }
        static string F(object value) => Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
