using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.AI
{
    /// <summary>Three approach headings and three nearest target triangles define a bounded set of
    /// exact contact origins. Production A* decides reachability. No runtime rotations or fake plans.</summary>
    public static class AiShortPlanCandidateBuilder
    {
        public static IReadOnlyList<AiTacticalCandidate> Build(AiDecisionContext context,
            IReadOnlyList<AiTacticalCandidate> ordinary,IReadOnlyDictionary<string,AiActionForecast> previews,AiActionForecast baseline)
            =>BuildCore(context,ordinary,previews,baseline,false);

        /// <summary>Geometry/path proposals for a fresh public boundary inside a continuation branch.</summary>
        public static IReadOnlyList<AiTacticalCandidate> ApproachesFor(AiDecisionContext context,AiTacticalCandidate attack)
            =>BuildCore(context,new[]{attack},null,null,true);

        static IReadOnlyList<AiTacticalCandidate> BuildCore(AiDecisionContext context,
            IReadOnlyList<AiTacticalCandidate> ordinary,IReadOnlyDictionary<string,AiActionForecast> previews,AiActionForecast baseline,bool forceApproach)
        {
            var settings=context.ForecastSettings.ShortPlans;
            if(settings==null || !(context.PathCalculator is IAiMovementPaths paths))return Array.Empty<AiTacticalCandidate>();
            var result=new List<AiTacticalCandidate>();var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var attack in ordinary.Where(c=>c.Type==ActionType.Attack).OrderBy(c=>c.Key,StringComparer.Ordinal))
            {
                var add=((ScheduleEditPayload)attack.Request.Payload).Operations.OfType<AddOrdinaryPlanOperation>().Single();
                if(!add.PrimaryTargetUnitId.HasValue)continue;
                if(!forceApproach)
                {
                    if(!previews.TryGetValue(attack.Key,out var direct) || direct.RejectionCode!=null)continue;
                    var directValue=AiTacticalPolicy.Evaluate(context,AiPolicyKind.CautiousDefense,attack,baseline,direct);
                    if(directValue.DamageDeltaQ10>0 || directValue.InterruptedThreats>0 || directValue.Clashes>0)continue;
                }
                var actor=context.Snapshot.VisibleUnits.First(u=>u.UnitId==add.OwnerUnitId.Value);
                var target=context.Snapshot.VisibleUnits.FirstOrDefault(u=>u.UnitId==add.PrimaryTargetUnitId.Value.Value && u.IsAlive);
                var spec=context.Snapshot.FindAction(add.ActionSpecId);
                if(target==null || !(spec.Payload is AttackPayloadSpec payload))continue;
                var footprints=context.Snapshot.Definition.VolumeDirectionsOf(new UnitDefinitionId(target.DefinitionId));
                if(footprints==null)continue;
                var targetTriangles=VolumeFootprint.ResolveTriangles(footprints,new GridPoint(target.X,target.Y),(GridDirection)target.Facing)
                    .OrderBy(t=>Math.Abs((long)t.X-actor.X)+Math.Abs((long)t.Y-actor.Y)).ThenBy(t=>t.X).ThenBy(t=>t.Y).ThenBy(t=>t.T).Take(3).ToArray();
                int aim=(int)AiTacticalCandidateBuilder.Aim(target.X-actor.X,target.Y-actor.Y);
                var origins=new HashSet<GridPoint>();
                foreach(int direction in new[]{aim,(aim+2)%12,(aim+10)%12}.Distinct())
                    foreach(var tile in payload.Pattern.Directions[direction].Triangles)
                        foreach(var targetTile in targetTriangles)
                        {
                            if(tile.T!=targetTile.T || !GridPoint.TryCreate(targetTile.X-tile.X,targetTile.Y-tile.Y,out var origin))continue;
                            if(origin.X==actor.X && origin.Y==actor.Y)continue;
                            if(AiTacticalCandidateBuilder.Aim(target.X-origin.X,target.Y-origin.Y)!=(GridDirection)direction)continue;
                            if(!context.Snapshot.Definition.Encounters.Any(e=>e.GridBoundary.Contains(origin)))continue;
                            origins.Add(origin);
                        }
                var from=new GridPoint(actor.X,actor.Y);var costs=context.Snapshot.Definition.Rules.PathCostRules;
                var goalOrigins=origins.OrderBy(p=>costs.HeuristicWeightUnits(from,p)).ThenBy(p=>p.X).ThenBy(p=>p.Y)
                    .Take(settings.MaxGoalPathProbes).ToArray();
                foreach(var moveId in context.Snapshot.ActionSetOf(add.OwnerUnitId).OrderBy(x=>x.Value,StringComparer.Ordinal))
                {
                    var move=context.Snapshot.FindAction(moveId);if(move?.Type!=ActionType.Move)continue;
                    int accepted=0;
                    foreach(var origin in goalOrigins)
                    {
                        var path=paths.FindAiPath(add.OwnerUnitId,origin);
                        if(!path.Succeeded || path.EdgeCount==0)continue;
                        int edges=Math.Min(settings.MaxMoveEdges,path.EdgeCount),prefixWeight=0;
                        for(int i=0;i<edges;i++)
                        {
                            var a=path.Path[i];var b=path.Path[i+1];
                            int d=Enumerable.Range(0,12).First(n=>GridNeighborTable.OffsetX((GridDirection)n)==b.X-a.X
                                && GridNeighborTable.OffsetY((GridDirection)n)==b.Y-a.Y);
                            prefixWeight+=costs.StepWeightUnits((GridDirection)d);
                        }
                        var destination=path.Path[edges];
                        string key=FormattableString.Invariant($"P/{add.OwnerUnitId.Value}/{moveId.Value}/{add.ActionSpecId.Value}/{target.UnitId}/{destination.X}/{destination.Y}/{origin.X}/{origin.Y}");
                        if(!seen.Add(key))continue;
                        var request=new CommandRequest(context.NextTick,new ScheduleEditScope(context.Snapshot.ScheduleRevision,context.ExpectedWindowId),
                            new ScheduleEditPayload(new ScheduleEditOperation[]{new AddOrdinaryPlanOperation(1,add.OwnerUnitId,moveId,
                                Facing:(GridDirection)actor.Facing,Destination:destination)}));
                        if(context.NormalPreview!=null && context.NormalPreview(context.ControllerId,request)!=null)continue;
                        result.Add(new AiTacticalCandidate(key,ActionType.Move,request,false,0) {
                            FollowUp=new AiFollowUpAttack(add.ActionSpecId,new UnitId(target.UnitId)),
                            Approach=new AiApproachGoal(new UnitId(target.UnitId),add.ActionSpecId,origin,path.PathWeightUnits,path.PathWeightUnits-prefixWeight) });
                        if(++accepted>=settings.GoalPositionsPerAttack)break;
                    }
                }
            }
            return result.OrderBy(c=>c.Key,StringComparer.Ordinal).ToArray();
        }

        public static bool HasTacticalEffect(AiDecisionContext context,AiCandidateEvaluation value,AiActionForecast baseline,AiActionForecast outcome)
        {
            if(value.DamageDeltaQ10>0 || value.IncomingDeltaQ10<0 || value.InterruptedThreats>0 || value.Clashes>0)return true;
            // A zero-health-damage attack may still relocate a hostile unit. It remains eligible;
            // its signed positional value, rather than the predicate, determines preference.
            return baseline.Units.Any(u=>context.ControlledUnits.Any(id=>context.Snapshot.Classify(id,new UnitId(u.UnitId))==Factions.UnitRelation.Hostile)
                && outcome.Units.Any(a=>a.UnitId==u.UnitId && (a.X!=u.X || a.Y!=u.Y)));
        }
    }
}
