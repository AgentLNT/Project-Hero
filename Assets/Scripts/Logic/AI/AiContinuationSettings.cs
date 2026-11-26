using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;

namespace ProjectHero.Logic.AI
{
    /// <summary>Experimental finite response protocol, shared by valuation-only and continuation runs.
    /// This samples representative legal replies; it does not solve every opponent response.</summary>
    public sealed record AiContinuationSettings(bool EnablePlans=true,int MaxFirstAttacks=2)
    {
        public const string Version="attack-optional-move-attack-public-v1";
        public string Validate()=>MaxFirstAttacks<1 || MaxFirstAttacks>4?"AI_CONTINUATION_SETTINGS_INVALID":null;
        public void WriteHashComponents(CanonicalHashWriter w)
        {
            w.Write("ai.continuation.version",Version);w.Write("ai.continuation.enabled",EnablePlans?1:0);
            w.Write("ai.continuation.first_attacks",MaxFirstAttacks);
            w.Write("ai.continuation.replies","first-block-extreme-lexicographic-dodges-first-guard-v1");
            w.Write("ai.continuation.utility","minimum-realized-damage-over-finite-two-reply-protocol-v1");
        }
        public static IReadOnlyList<T> Replies<T>(IEnumerable<T> values,Func<T,CommandRequest> request,Func<T,string> key)
        {
            var ordered=values.OrderBy(key,StringComparer.Ordinal).ToArray();
            var result=new List<T>();
            result.AddRange(ordered.Where(x=>request(x).Payload is ReactionCommandPayload p && p.ReactionKind==ReactionCommandKind.Block).Take(1));
            var dodges=ordered.Where(x=>request(x).Payload is ReactionCommandPayload p && p.ReactionKind==ReactionCommandKind.Dodge)
                .GroupBy(x=>request(x).Payload).Select(g=>g.First()).ToArray();
            if(dodges.Length>0)result.Add(dodges[0]);
            if(dodges.Length>1)result.Add(dodges[dodges.Length-1]);
            result.AddRange(ordered.Where(x=>request(x).Payload is ScheduleEditPayload).Take(1));
            return result;
        }
    }
}
