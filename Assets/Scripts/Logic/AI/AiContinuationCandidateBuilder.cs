using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;

namespace ProjectHero.Logic.AI
{
    public static class AiContinuationCandidateBuilder
    {
        public static IReadOnlyList<AiTacticalCandidate> Build(AiDecisionContext context,IReadOnlyList<AiTacticalCandidate> ordinary,
            IReadOnlyDictionary<string,AiActionForecast> previews,AiActionForecast baseline)
        {
            if(context.ForecastSettings.Continuations?.EnablePlans!=true)return Array.Empty<AiTacticalCandidate>();
            var attacks=ordinary.Where(c=>c.Type==ActionType.Attack && previews.TryGetValue(c.Key,out var f)
                && f.RejectionCode==null && f.Coverage==AiForecastCoverage.Complete
                && AiTacticalPolicy.Evaluate(context,AiPolicyKind.CautiousDefense,c,baseline,f).DamageDeltaQ10>0)
                .OrderBy(c=>previews[c.Key].PlanEndTick).ThenBy(c=>c.Key,StringComparer.Ordinal).ToArray();
            var result=new List<AiTacticalCandidate>();
            foreach(var first in attacks.Take(context.ForecastSettings.Continuations.MaxFirstAttacks))
            {
                var a=Add(first);
                // One fastest usable B per actor/target. A and B may use the same skill.
                var next=attacks.FirstOrDefault(c=>Add(c).OwnerUnitId==a.OwnerUnitId && Add(c).PrimaryTargetUnitId==a.PrimaryTargetUnitId);
                if(next==null)continue;
                var b=Add(next);
                result.Add(first with {Key=first.Key+"/then/"+b.ActionSpecId.Value,
                    FollowUp=new AiFollowUpAttack(b.ActionSpecId,b.PrimaryTargetUnitId.Value,true)});
            }
            return result;
        }
        static AddOrdinaryPlanOperation Add(AiTacticalCandidate c)=>((ScheduleEditPayload)c.Request.Payload).Operations.OfType<AddOrdinaryPlanOperation>().Single();
    }
}
