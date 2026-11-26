using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Logic.Simulation
{
    public sealed partial class BattleSimulation
    {
        // Stage 18 is a decision boundary, before _tick/_currentSnapshot are committed in stage 19.
        // Reuse the HUD sanitizer with this explicit boundary; never hand AI a live simulation.
        internal IAiForecast CreateAiForecast(Snapshots.DecisionSnapshot decision)
        {
            if (decision == null || string.IsNullOrEmpty(decision.ControllerId.Value) || decision.Tick != _eventTick)
                throw new InvalidOperationException("AI_FORECAST_BOUNDARY_MISMATCH");
            var visible = new HashSet<long>(decision.VisiblePlans.Concat(decision.OwnPlans).Select(p => p.ActionPlanId));
            var template = CopyHudWorld(visible, decision.Tick);
            return new Snapshots.AiForecastWorld(decision, template, () => template.CopyHudWorld(visible));
        }

        // Called only on an already sanitized forecast world. Admit the proposed reply through
        // a real next Step, including window resets, without replaying the whole attack prefix.
        internal string PreviewAiDefenseReply(ControllerId controller, CommandRequest request)
        {
            var visible = new HashSet<long>(_scheduleAuthority.Registry.ActivePlans.Select(p => p.ActionPlanId.Value));
            using var preview = CopyHudWorld(visible);
            var entry = preview.CommandIngress.FindEntry(controller);
            if (entry == null || entry.Submit(request) != null) return "AI_DEFENSE_INGRESS_REJECTED";
            long tick = preview.Tick + 1;
            var result = preview.Step(tick, preview.CommandIngress.FreezeTick(tick));
            return result.Events.Events.OfType<CommandRejectedEvent>().FirstOrDefault()?.ReasonCode;
        }
    }
}

namespace ProjectHero.Logic.Snapshots
{
    internal sealed class AiForecastWorld : IAiSequenceForecast
    {
        internal const int LookaheadTicks = 120;
        private readonly DecisionSnapshot _decision;
        private readonly BattleSimulation _template;
        private readonly Func<BattleSimulation> _fork;
        public AiForecastWorld(DecisionSnapshot decision, BattleSimulation template, Func<BattleSimulation> fork)
        { _decision = decision; _template = template; _fork = fork; }
        public void Dispose() => _template.Dispose();

        public AiActionForecast Evaluate(CommandRequest request, AiForecastSettings settings = null)
            => Run(request, settings);

        public AiActionForecast EvaluateSequence(CommandRequest first,AiFollowUpAttack followUp,AiForecastSettings settings)
            => Run(first,settings,followUp:followUp ?? throw new ArgumentNullException(nameof(followUp)));

        private sealed record Reply(string Key, ControllerId Controller, long Boundary, CommandRequest Request, string RejectionCode,
            long DefenderUnitId,long SourcePlanId,bool IsOrdinaryEvasion=false);

        public IReadOnlyList<AiDefenseForecast> EvaluateDefenses(CommandRequest attack, AiForecastSettings settings)
            => EvaluateDefenseProtocol(attack,settings,null);

        public IReadOnlyList<AiDefenseForecast> EvaluateSequenceDefenses(CommandRequest first,AiFollowUpAttack followUp,AiForecastSettings settings)
            => EvaluateDefenseProtocol(first,settings,followUp);

        private IReadOnlyList<AiDefenseForecast> EvaluateDefenseProtocol(CommandRequest attack,AiForecastSettings settings,AiFollowUpAttack followUp)
        {
            if (settings == null || settings.Validate() != null) throw new LogicDefinitionException("AI_FORECAST_SETTINGS_INVALID");
            var replies = new List<Reply>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var contexts = new HashSet<string>(StringComparer.Ordinal);
            var evasionContexts = new HashSet<string>(StringComparer.Ordinal);
            var evasionCounts = new Dictionary<long,int>();
            var control = Run(attack, settings, observe: Collect,followUp:followUp);
            if (control.RejectionCode != null || control.Coverage != AiForecastCoverage.Complete) return Array.Empty<AiDefenseForecast>();
            if(followUp?.AttackPrefix==true)
            {
                var leaves=new List<AiDefenseForecast>();
                var firstReplies=AiContinuationSettings.Replies(replies.Where(r=>r.SourcePlanId==control.FirstPlanId && r.RejectionCode==null),r=>r.Request,r=>r.Key);
                foreach(var first in new Reply[]{null}.Concat(firstReplies))
                {
                    AiActionForecast branch;
                    if(first==null)branch=control;
                    else
                    {
                        replies.Clear();seen.Clear();contexts.Clear();
                        branch=Run(attack,settings,first,Collect,followUp);
                        leaves.Add(new AiDefenseForecast(first.Key,first.Controller,first.Boundary,first.Request,branch){DefenderUnitId=first.DefenderUnitId});
                    }
                    if(branch.RejectionCode!=null || branch.Coverage!=AiForecastCoverage.Complete)continue;
                    var seconds=AiContinuationSettings.Replies(replies.Where(r=>r.SourcePlanId==branch.FollowUpPlanId && r.RejectionCode==null),r=>r.Request,r=>r.Key);
                    foreach(var second in seconds)
                    {
                        var outcome=Run(attack,settings,first,followUp:followUp,secondReply:second);
                        leaves.Add(new AiDefenseForecast((first?.Key??"NO_FIRST_REPLY")+"/THEN/"+second.Key,second.Controller,second.Boundary,second.Request,outcome)
                            {DefenderUnitId=second.DefenderUnitId});
                    }
                }
                return leaves;
            }
            var selectedReplies=settings.Continuations==null?replies.OrderBy(r=>r.Key,StringComparer.Ordinal).ToArray()
                :AiContinuationSettings.Replies(replies.Where(r=>r.RejectionCode==null),r=>r.Request,r=>r.Key);
            return selectedReplies.Select(r =>
            {
                var outcome = r.RejectionCode != null ? new AiActionForecast(r.RejectionCode, control.HorizonTick,
                    Array.Empty<UnitSnapshot>(), -1, 0, 0, Coverage: AiForecastCoverage.Rejected, ObservedThroughTick: r.Boundary + 1)
                    : Run(attack, settings, r,followUp:followUp);
                if (r.RejectionCode == null && outcome.RejectionCode != null && !r.IsOrdinaryEvasion)
                    throw new LogicDefinitionException("AI_DEFENSE_ADMISSION_MISMATCH", $"reply={r.Key}; after={r.Boundary}; rejected={outcome.RejectionCode}; step={outcome.ObservedThroughTick}; decision={_decision.Tick}; playback={r.Request.Payload}");
                return new AiDefenseForecast(r.Key,r.Controller,r.Boundary,r.Request,outcome)
                    {DefenderUnitId=r.DefenderUnitId,IsOrdinaryEvasion=r.IsOrdinaryEvasion};
            }).ToArray();

            void Collect(BattleSimulation world, long planId)
            {
                var plan = world.AiActionPlanLookup.FindPlan(new ActionPlanId(planId));
                if (plan == null || plan.ActionType != ActionType.Attack || plan.IsTerminal || world.Tick >= plan.ImpactTick) return;
                foreach (var binding in world.Definition.FindEncounter(world.EncounterId).Controllers.OrderBy(c => c.ControllerId.Value, StringComparer.Ordinal))
                {
                    var snapshot = world.DecisionSnapshotFor(binding.ControllerId);
                    var hostile = snapshot.ControlledUnitIds.Where(id => _decision.ControlledUnitIds.Any(own =>
                        _decision.Classify(own, id) == Factions.UnitRelation.Hostile)).ToArray();
                    // A reply can be prompted only by the candidate's genuinely published attack.
                    if (hostile.Length == 0 || !snapshot.VisiblePlans.Any(p => p.ActionPlanId == planId && DecisionSnapshot.IsPubliclyRevealed(p))) continue;
                    // Building public Dodge destinations is expensive. Reuse identical admission
                    // contexts before calling that port, while retaining next-Tick expiry boundaries.
                    string units = string.Join(";", snapshot.VisibleUnits.OrderBy(u => u.UnitId).Select(u =>
                        FormattableString.Invariant($"{u.UnitId}:{u.IsAlive}:{u.HealthQ10}:{u.X}:{u.Y}:{u.Facing}:{u.State}:{u.StateEndTick}:{u.StateEndTick <= snapshot.Tick + 1}")));
                    string resources = string.Join(";", snapshot.AdrenalineLedgers.OrderBy(l => l.UnitId).Select(l =>
                        FormattableString.Invariant($"{l.UnitId}:{l.CycleId}:{l.AvailableAdrenaline}:{l.ReservedTotal}")));
                    string effects = string.Join(";", world.CurrentSnapshot.Effects.OrderBy(e => e.EffectId).Select(e =>
                        FormattableString.Invariant($"{e.EffectId}:{e.UnitId}:{e.SpecId}:{e.EndTick}:{e.EndTick <= snapshot.Tick + 1}")));
                    string plans = string.Join(";", snapshot.VisiblePlans.Concat(snapshot.OwnPlans).GroupBy(p => p.ActionPlanId)
                        .Select(g => g.First()).OrderBy(p => p.ActionPlanId).Select(p =>
                            FormattableString.Invariant($"{p.ActionPlanId}:{p.State}:{p.StartTick}:{p.ImpactTick}:{p.EndTick}:{p.EndTick <= snapshot.Tick + 1}:{p.SourceImpactTick < snapshot.Tick + 1}")));
                    string opportunities = string.Join(";", world.ReactionOpportunities.ActiveOpportunities
                        .Where(o => o.SourceAttackPlanId.Value == planId && hostile.Contains(o.DefenderUnitId) && o.Options.Any(p => p.IsPublished))
                        .OrderBy(o => o.Id.Value).Select(o => o.Id.Value.ToString(CultureInfo.InvariantCulture) + ":" + o.State + ":"
                            + string.Join(",", o.Options.Where(p => p.IsPublished).OrderBy(p => p.ReactionActionSpecId.Value, StringComparer.Ordinal)
                                .Select(p => FormattableString.Invariant($"{p.ReactionActionSpecId.Value}:{p.IsOpen}:{p.ResponseDeadlineTick >= snapshot.Tick + 1}")))));
                    string admission = FormattableString.Invariant($"{binding.ControllerId.Value}/{snapshot.ScheduleRevision}/{snapshot.OwnWindow?.WindowId}/{snapshot.OwnWindow?.IsAcceptingSubmissions}/{world.Tick == plan.ImpactTick - 1}/{units}/{resources}/{effects}/{plans}/{opportunities}");
                    bool collectDefenses=contexts.Add(admission);
                    bool collectEvasion=settings.ShortPlans?.ConsiderOrdinaryEvasion==true
                        && snapshot.OwnWindow is TurnWindowSnapshot window && window.IsOpen && window.IsAcceptingSubmissions
                        && world.Tick%AiTacticalPolicy.OrdinaryThinkInterval==0 && world.Tick+1<window.EndTick
                        && hostile.Any(id=>id.Value==window.OwnerUnitId) && evasionCounts.GetValueOrDefault(planId)<2
                        && evasionContexts.Add(admission+"/"+planId.ToString(CultureInfo.InvariantCulture));
                    if (!collectDefenses && !collectEvasion) continue;
                    var context = AiDecisionContextFactory.Build(snapshot, binding.ControllerId, world.AiReactionOpportunities,
                        world.AiActionPlanLookup, world.MovementPathCalculator);
                    if(collectEvasion)
                    {
                        int used=evasionCounts.GetValueOrDefault(planId);
                        foreach(var escape in FindOrdinaryEvasions(world,context,plan,2-used))
                        {
                            var add=(AddOrdinaryPlanOperation)((ScheduleEditPayload)escape.Request.Payload).Operations[0];
                            string key=FormattableString.Invariant($"MOVE/{binding.ControllerId.Value}/{planId}/{world.Tick}/{escape.Key}");
                            replies.Add(new Reply(key,binding.ControllerId,world.Tick,escape.Request,null,add.OwnerUnitId.Value,planId,true));
                            used++;
                        }
                        evasionCounts[planId]=used;
                    }
                    if(!collectDefenses)continue;
                    context = context with { Opportunities = context.Opportunities.Where(o => o.SourceAttackPlanId.Value == planId
                        && hostile.Contains(o.DefenderUnitId)).ToArray() };
                    var candidates = new List<AiTacticalCandidate>(AiTacticalCandidateBuilder.Reactions(context));
                    foreach (var guard in AiTacticalCandidateBuilder.Ordinary(context).Where(c => c.Type == ActionType.Guard))
                    {
                        var add = ((ScheduleEditPayload)guard.Request.Payload).Operations.OfType<AddOrdinaryPlanOperation>().Single();
                        if (!hostile.Contains(add.OwnerUnitId)) continue;
                        // Latest start permitted by this real window. The kernel resolves duration, lane and coverage.
                        long start = Math.Min(plan.ImpactTick, context.OwnWindow.EndTick - 1);
                        if (start < context.NextTick) continue;
                        candidates.Add(guard with { Key = guard.Key + "/at/" + start.ToString(CultureInfo.InvariantCulture),
                            Request = guard.Request with { Payload = new ScheduleEditPayload(new ScheduleEditOperation[] { add with { RequestedStartTick = start } }) } });
                    }
                    foreach (var candidate in candidates.OrderBy(c => c.Key, StringComparer.Ordinal))
                    {
                        // Retry only when public admission facts change, not at every identical boundary.
                        // This bounds a one-reply protocol; it is not a search over every legal submission time.
                        string facts = string.Join(";", snapshot.VisibleUnits.Where(u => hostile.Any(id => id.Value == u.UnitId))
                            .OrderBy(u => u.UnitId).Select(u => FormattableString.Invariant($"{u.UnitId}:{u.State}:{u.StateEndTick}:{u.X}:{u.Y}")));
                        string key = FormattableString.Invariant($"{binding.ControllerId.Value}/{candidate.Key}/w{context.OwnWindow?.WindowId}/r{snapshot.ScheduleRevision}/{facts}/resources/{resources}");
                        long defender = candidate.Request.Scope is ReactionCommandScope scope
                            ? context.Opportunities.First(o => o.ReactionOpportunityId == scope.ReactionOpportunityId).DefenderUnitId.Value
                            : ((ScheduleEditPayload)candidate.Request.Payload).Operations.OfType<AddOrdinaryPlanOperation>().Single().OwnerUnitId.Value;
                        if (seen.Add(key)) replies.Add(new Reply(key, binding.ControllerId, world.Tick, candidate.Request,
                            world.PreviewAiDefenseReply(binding.ControllerId, candidate.Request), defender,planId));
                    }
                }
            }
        }

        // Geometry only proposes destinations. The schedule editor bounds the number of actual
        // branches; each reply goes through ingress and is scored only from a replayed outcome.
        static IEnumerable<AiTacticalCandidate> FindOrdinaryEvasions(BattleSimulation world,AiDecisionContext context,ActionPlan threatenedBy,int limit)
        {
            var defender=context.Snapshot.VisibleUnits.FirstOrDefault(u=>u.IsAlive && u.UnitId==context.OwnWindow.OwnerUnitId);
            if(defender==null || context.Snapshot.OwnPlans.Any(p=>p.OwnerUnitId==defender.UnitId && p.Origin==(int)ActionPlanOrigin.Ordinary)
                || !world.LogicGrid.TryGetAnchor(threatenedBy.OwnerUnitId,out var origin)
                || !(context.Snapshot.FindAction(threatenedBy.ActionSpecId)?.Payload is AttackPayloadSpec attack)
                || (attack.TargetPolicy==TargetPolicy.PrimaryTargetOnly && threatenedBy.PrimaryTargetUnitId?.Value!=defender.UnitId))yield break;
            var volumes=world.Definition.VolumeDirectionsOf(new UnitDefinitionId(defender.DefinitionId));
            if(volumes==null)yield break;
            var from=new GridPoint(defender.X,defender.Y);
            var area=attack.Pattern.Directions[(int)threatenedBy.Facing].GetTranslated(origin).ToHashSet();
            if(!VolumeFootprint.ResolveTriangles(volumes,from,(GridDirection)defender.Facing).Any(area.Contains))yield break;
            var exits=AiTacticalCandidateBuilder.Ordinary(context).Where(c=>c.Type==ActionType.Move)
                .Select(c=>(Candidate:c,Add:(AddOrdinaryPlanOperation)((ScheduleEditPayload)c.Request.Payload).Operations[0]))
                .Where(x=>x.Add.Destination.HasValue && world.LogicGrid.Boundary.Contains(x.Add.Destination.Value)
                    && !VolumeFootprint.ResolveTriangles(volumes,x.Add.Destination.Value,x.Add.Facing).Any(area.Contains))
                .OrderBy(x=>world.Definition.Rules.PathCostRules.HeuristicWeightUnits(from,x.Add.Destination.Value))
                .ThenBy(x=>x.Candidate.Key,StringComparer.Ordinal).Take(8);
            var used=new HashSet<GridPoint>();
            foreach(var exit in exits)
            {
                if(used.Contains(exit.Add.Destination.Value))continue;
                var cmd=exit.Candidate.Request;
                var scope=(ScheduleEditScope)cmd.Scope;
                var trial=world.ScheduleEditor.Apply(((ScheduleEditPayload)cmd.Payload).Operations,cmd.TargetTick,
                    scope.ExpectedScheduleRevision,world.ScheduleRevision,preview:true,expectedWindowId:scope.ExpectedWindowId,issuer:context.ControllerId);
                if(!trial.Succeeded || trial.AppliedEdits.Count!=1)continue;
                long moveId=trial.AppliedEdits[0].PlanId.Value;
                var move=trial.PreviewPlans?.SingleOrDefault(p=>p.Plan.ActionPlanId==moveId)?.Plan;
                if(move==null || move.EndTick>threatenedBy.ImpactTick
                    || world.PreviewAiDefenseReply(context.ControllerId,cmd)!=null)continue;
                used.Add(exit.Add.Destination.Value);
                yield return exit.Candidate;
                if(used.Count>=limit)yield break;
            }
        }

        private AiActionForecast Run(CommandRequest request, AiForecastSettings settings, Reply reply = null,
            Action<BattleSimulation, long> observe = null,AiFollowUpAttack followUp=null,Reply secondReply=null)
        {
            if (settings?.Validate() is string invalid) throw new LogicDefinitionException(invalid);
            using var world = _fork();
            long horizon = checked(_decision.Tick + (settings?.LookaheadTicks ?? LookaheadTicks));
            string error = null;
            long start = 0, impact = 0, end = 0;
            long candidatePlanId = 0;
            long coveragePlanId = 0;
            long replyPlanId = 0;
            long firstPlanId=0,firstEnd=0,followPlanId=0,followRequestTick=-1,followStart=-1;
            bool pressure=followUp?.AttackPrefix==true,stopped=false;
            long bridgePlanId=0,bridgeRequestTick=-1;
            var responsePlans=new List<long>();
            var responses=new[]{reply,secondReply}.Where(r=>r!=null).ToArray();
            var exchanges=new List<AiResourceExchange>();
            var own = new HashSet<long>(_decision.ControlledUnitIds.Select(u => u.Value));
            var hostile = new HashSet<long>(_decision.VisibleUnits.Where(u => own.Any(id =>
                _decision.Classify(new UnitId(id), new UnitId(u.UnitId)) == Factions.UnitRelation.Hostile)).Select(u => u.UnitId));
            if (request != null)
            {
                error = request.ValidateStaticStructure();
                if(error==null && followUp!=null && (!(request.Payload is ScheduleEditPayload sequenceEdit)
                    || sequenceEdit.Operations.Count!=1 || !(sequenceEdit.Operations[0] is AddOrdinaryPlanOperation firstAdd)
                    || _decision.FindAction(firstAdd.ActionSpecId)?.Type!=(pressure?ActionType.Attack:ActionType.Move)))error="AI_SEQUENCE_FIRST_STEP_NOT_MOVE";
                if(error==null && pressure && settings?.Continuations?.EnablePlans!=true)error="AI_CONTINUATION_DISABLED";
                if (error == null && request.TargetTick != _decision.Tick + 1) error = "AI_FORECAST_TARGET_TICK";
                if (error == null && !(request.Payload is ScheduleEditPayload) && !(request.Payload is ReactionCommandPayload))
                    error = CommandCodes.SCOPE_PAYLOAD_MISMATCH;
                if (error == null)
                {
                    // A candidate is a real next-tick submission in this isolated world. Directly
                    // applying it before Step misses window/resource resets and command-batch claims.
                    var entry = world.CommandIngress.FindEntry(_decision.ControllerId);
                    if (entry == null) error = "AI_FORECAST_CONTROLLER_MISSING";
                    else if (entry.Submit(request) != null) error = "AI_FORECAST_INGRESS_REJECTED";
                }
            }
            if (error != null) return new AiActionForecast(error, horizon, Array.Empty<UnitSnapshot>(), -1, 0, 0,
                Coverage: AiForecastCoverage.Rejected, ObservedThroughTick: world.Tick);
            var threats = _decision.VisiblePlans.Where(p => hostile.Contains(p.OwnerUnitId)
                && _decision.FindAction(new ActionSpecId(p.ActionSpecId))?.Type == ActionType.Attack)
                .ToDictionary(p => p.ActionPlanId, p => p.ImpactTick);
            var interrupted = new HashSet<long>(); var clashes = new HashSet<long>();
            var trace = new Queue<string>();
            long firstHit = -1;
            var expiries = new Dictionary<long, long>();
            while (!world.IsEnded && world.Tick < horizon)
            {
                var before = world.CurrentSnapshot.Units.Where(u => hostile.Contains(u.UnitId)).Sum(u => (long)u.HealthQ10);
                long tick = world.Tick + 1;
                ProjectHero.Logic.Simulation.StepResult result;
                try { result = world.Step(tick, world.CommandIngress.FreezeTick(tick)); }
                catch (LogicDefinitionException exception)
                {
                    throw new LogicDefinitionException(exception.ErrorCode, $"{exception.Message}; decision={_decision.Tick}; branch={reply?.Key}; simulated={tick}" + "\n" + string.Join("\n", trace));
                }
                foreach (var item in result.Events.Events)
                    if (item is DodgeResolvedEvent || item is ForcedDisplacementResolvedEvent || item is ActionPlanTerminatedEvent || item is ActionPlanCreatedEvent)
                    {
                        trace.Enqueue(ProjectHero.Logic.Replay.ReplayEventComparison.Canonical(item));
                        if (trace.Count > 17) trace.Dequeue();
                    }
                if(pressure)foreach(var e in result.Events.Events.OfType<AdrenalineLedgerChangedEvent>())
                    exchanges.Add(new AiResourceExchange(tick,e.UnitId.Value,(int)e.ChangeKind,e.AvailableBefore,e.AvailableAfter,e.ReservationAmount));
                if (settings?.Sampling != null)
                    foreach (var ledger in result.Snapshot.Resources.AdrenalineLedgers ?? Array.Empty<AdrenalineLedgerSnapshot>())
                        if (!expiries.ContainsKey(ledger.UnitId) && _decision.AdrenalineLedgers.Any(l => l.UnitId == ledger.UnitId && l.CycleId != ledger.CycleId))
                            expiries.Add(ledger.UnitId, tick);
                if (request != null && tick == request.TargetTick)
                {
                    var rejected = result.Events.Events.OfType<CommandRejectedEvent>().FirstOrDefault();
                    if (rejected != null)
                        return new AiActionForecast(rejected.ReasonCode, horizon, Array.Empty<UnitSnapshot>(), -1, 0, 0,
                            Coverage: AiForecastCoverage.Rejected, ObservedThroughTick: world.Tick);
                    var accepted = result.Events.Events.OfType<ActionPlanCreatedEvent>().LastOrDefault();
                    if (accepted != null) coveragePlanId = accepted.ActionPlanId;
                    if (accepted != null && settings?.Sampling != null && request.Payload is ReactionCommandPayload)
                    { candidatePlanId = accepted.ActionPlanId; start = accepted.StartTick; end = accepted.EndTick; }
                    if (request.Payload is ScheduleEditPayload)
                    {
                        var created = result.Events.Events.OfType<ActionPlanCreatedEvent>().LastOrDefault(e => e.Origin == (int)ActionPlanOrigin.Ordinary);
                        if (created != null)
                        {
                            candidatePlanId = created.ActionPlanId; start = created.StartTick; end = created.EndTick;
                            if(followUp!=null){firstPlanId=created.ActionPlanId;firstEnd=created.EndTick;}
                            var timing = world.Definition.FindAction(new ActionSpecId(created.ActionSpecId)).Timing as AttackTimingSpec;
                            impact = timing == null ? 0 : end - timing.RecoveryTicks;
                        }
                    }
                }
                if(followUp!=null && tick==followRequestTick)
                {
                    var rejected=result.Events.Events.OfType<CommandRejectedEvent>().FirstOrDefault();
                    if(rejected!=null)return new AiActionForecast(rejected.ReasonCode,horizon,Array.Empty<UnitSnapshot>(),-1,0,0,
                        Coverage:AiForecastCoverage.Rejected,ObservedThroughTick:world.Tick);
                    var created=result.Events.Events.OfType<ActionPlanCreatedEvent>().LastOrDefault(e=>e.Origin==(int)ActionPlanOrigin.Ordinary);
                    if(created==null)throw new LogicDefinitionException("AI_SEQUENCE_FOLLOW_UP_NOT_CREATED");
                    followPlanId=coveragePlanId=candidatePlanId=created.ActionPlanId;followStart=created.StartTick;
                }
                if(pressure && tick==bridgeRequestTick)
                {
                    var rejected=result.Events.Events.OfType<CommandRejectedEvent>().FirstOrDefault();
                    if(rejected!=null)return new AiActionForecast(rejected.ReasonCode,horizon,Array.Empty<UnitSnapshot>(),-1,0,0,
                        Coverage:AiForecastCoverage.Rejected,ObservedThroughTick:world.Tick);
                    var created=result.Events.Events.OfType<ActionPlanCreatedEvent>().LastOrDefault(e=>e.Origin==(int)ActionPlanOrigin.Ordinary);
                    if(created==null)throw new LogicDefinitionException("AI_CONTINUATION_MOVE_NOT_CREATED");
                    bridgePlanId=coveragePlanId=candidatePlanId=created.ActionPlanId;
                }
                if (candidatePlanId != 0)
                {
                    var plan = result.Snapshot.Plans.FirstOrDefault(p => p.ActionPlanId == candidatePlanId);
                    if (plan != null) { if(followPlanId==0 && (!pressure || candidatePlanId==firstPlanId))start = plan.StartTick;else if(followPlanId!=0)followStart=plan.StartTick; impact = plan.ImpactTick; end = plan.EndTick; }
                }
                long after = result.Snapshot.Units.Where(u => hostile.Contains(u.UnitId)).Sum(u => (long)u.HealthQ10);
                if (firstHit < 0 && after < before) firstHit = tick;
                foreach (var fact in result.Events.Events)
                {
                    if (fact is ActionPlanTerminatedEvent terminated && threats.TryGetValue(terminated.ActionPlanId, out long due)
                        && terminated.Tick < due) interrupted.Add(terminated.ActionPlanId);
                    if (fact is ClashParticipantResolvedEvent clash && hostile.Contains(clash.OwnerUnitId.Value))
                        clashes.Add(clash.ActionPlanId.Value);
                }
                foreach(var response in responses.Where(r=>tick==r.Request.TargetTick))
                {
                    var rejected = result.Events.Events.OfType<CommandRejectedEvent>().FirstOrDefault();
                    if (rejected != null) return new AiActionForecast(rejected.ReasonCode, horizon, Array.Empty<UnitSnapshot>(), -1, 0, 0,
                        Coverage: AiForecastCoverage.Rejected, ObservedThroughTick: world.Tick);
                    replyPlanId = result.Events.Events.OfType<ActionPlanCreatedEvent>().LastOrDefault()?.ActionPlanId ?? 0;
                    if (replyPlanId == 0) throw new LogicDefinitionException("AI_DEFENSE_REPLY_NOT_CREATED");
                    responsePlans.Add(replyPlanId);
                }
                foreach(var response in responses.Where(r=>tick==r.Boundary))
                {
                    var entry = world.CommandIngress.FindEntry(response.Controller);
                    if (entry == null || entry.Submit(response.Request) != null)
                        return new AiActionForecast("AI_DEFENSE_INGRESS_REJECTED", horizon, Array.Empty<UnitSnapshot>(), -1, 0, 0,
                            Coverage: AiForecastCoverage.Rejected, ObservedThroughTick: world.Tick);
                }
                if(followUp!=null && firstPlanId!=0 && followRequestTick<0 && !world.IsEnded && !stopped)
                {
                    var first=world.AiActionPlanLookup.FindPlan(new ActionPlanId(firstPlanId));
                    firstEnd=first.EndTick;
                    var prerequisite=bridgePlanId==0?first:world.AiActionPlanLookup.FindPlan(new ActionPlanId(bridgePlanId));
                    if(prerequisite.IsTerminal && !(bridgeRequestTick>=0 && bridgePlanId==0))
                    {
                        if(prerequisite.State!=ActionPlanState.Completed)
                        {
                            if(pressure){stopped=true;continue;}
                            return new AiActionForecast("AI_SEQUENCE_FIRST_STEP_TERMINATED",horizon,
                                Array.Empty<UnitSnapshot>(),-1,0,0,Coverage:AiForecastCoverage.Rejected,ObservedThroughTick:world.Tick);
                        }
                        var snapshot=world.DecisionSnapshotFor(_decision.ControllerId);
                        var window=snapshot.OwnWindow;
                        if(tick%AiTacticalPolicy.OrdinaryThinkInterval==0 && window!=null && window.IsOpen && window.IsAcceptingSubmissions && tick+1<window.EndTick)
                        {
                            var edit=((ScheduleEditPayload)request.Payload).Operations.OfType<AddOrdinaryPlanOperation>().Single();
                            var actor=snapshot.VisibleUnits.FirstOrDefault(u=>u.UnitId==edit.OwnerUnitId.Value && u.IsAlive);
                            var target=snapshot.VisibleUnits.FirstOrDefault(u=>u.UnitId==followUp.TargetUnitId.Value && u.IsAlive);
                            if(actor==null || target==null)
                            {
                                if(pressure){stopped=true;continue;}
                                return new AiActionForecast("AI_SEQUENCE_TARGET_UNAVAILABLE",horizon,
                                    Array.Empty<UnitSnapshot>(),-1,0,0,Coverage:AiForecastCoverage.Rejected,ObservedThroughTick:world.Tick);
                            }
                            var next=new CommandRequest(tick+1,new ScheduleEditScope(snapshot.ScheduleRevision,new WindowId(window.WindowId)),
                                new ScheduleEditPayload(new ScheduleEditOperation[]{new AddOrdinaryPlanOperation(1,edit.OwnerUnitId,followUp.ActionSpecId,
                                    PrimaryTargetUnitId:followUp.TargetUnitId,Facing:AiTacticalCandidateBuilder.Aim(target.X-actor.X,target.Y-actor.Y))}));
                            if(pressure)
                            {
                                var payload=world.Definition.FindAction(followUp.ActionSpecId)?.Payload as AttackPayloadSpec;
                                if(payload==null){stopped=true;continue;}
                                var area=payload.Pattern.Directions[(int)AiTacticalCandidateBuilder.Aim(target.X-actor.X,target.Y-actor.Y)]
                                    .GetTranslated(new Grid.GridPoint(actor.X,actor.Y));
                                var footprint=VolumeFootprint.ResolveTriangles(world.Definition.VolumeDirectionsOf(new UnitDefinitionId(target.DefinitionId)),
                                    new Grid.GridPoint(target.X,target.Y),(Grid.GridDirection)target.Facing);
                                if(!area.Intersect(footprint).Any())
                                {
                                    if(bridgeRequestTick>=0){stopped=true;continue;}
                                    var context=AiDecisionContextFactory.Build(snapshot,_decision.ControllerId,world.AiReactionOpportunities,
                                        world.AiActionPlanLookup,world.MovementPathCalculator) with {ForecastSettings=settings,NormalPreview=(issuer,command)=>
                                        {
                                            var scope=(ScheduleEditScope)command.Scope;
                                            return world.ScheduleEditor.Apply(((ScheduleEditPayload)command.Payload).Operations,command.TargetTick,
                                                scope.ExpectedScheduleRevision,world.ScheduleRevision,preview:true,expectedWindowId:scope.ExpectedWindowId,issuer:issuer).RejectionCode;
                                        }};
                                    var attackCandidate=new AiTacticalCandidate("CONTINUATION",ActionType.Attack,next,false,0);
                                    var bridge=AiShortPlanCandidateBuilder.ApproachesFor(context,attackCandidate).FirstOrDefault();
                                    if(bridge==null){stopped=true;continue;}
                                    next=bridge.Request;bridgeRequestTick=tick+1;
                                }
                            }
                            var entry=world.CommandIngress.FindEntry(_decision.ControllerId);
                            if(entry==null || entry.Submit(next)!=null)return new AiActionForecast("AI_SEQUENCE_INGRESS_REJECTED",horizon,
                                Array.Empty<UnitSnapshot>(),-1,0,0,Coverage:AiForecastCoverage.Rejected,ObservedThroughTick:world.Tick);
                            if(bridgeRequestTick!=tick+1)followRequestTick=tick+1;
                        }
                    }
                }
                if (!world.IsEnded && candidatePlanId != 0) observe?.Invoke(world, candidatePlanId);
            }
            var candidatePlan = coveragePlanId == 0 ? null : world.AiActionPlanLookup.FindPlan(new ActionPlanId(coveragePlanId));
            bool complete = request == null || world.IsEnded || candidatePlan?.IsTerminal == true;
            if(followUp!=null)complete &= followPlanId!=0 && candidatePlan?.IsTerminal==true;
            if(pressure && (stopped || world.IsEnded))complete=true;
            var replyPlan = replyPlanId == 0 ? null : world.AiActionPlanLookup.FindPlan(new ActionPlanId(replyPlanId));
            if (reply != null && !world.IsEnded) complete &= replyPlan?.IsTerminal == true;
            if(pressure && !world.IsEnded)complete &= responsePlans.Count==responses.Length && responsePlans.All(id=>world.AiActionPlanLookup.FindPlan(new ActionPlanId(id))?.IsTerminal==true);
            return new AiActionForecast(null, horizon, world.CurrentSnapshot.Units, firstHit, interrupted.Count, clashes.Count, start, impact, end,
                complete ? AiForecastCoverage.Complete : AiForecastCoverage.Uncovered, world.Tick,
                Math.Max(candidatePlan?.EndTick ?? 0, replyPlan?.EndTick ?? 0), candidatePlan == null ? 0 : (int)candidatePlan.TerminationReason)
                { ResourceExpiries = expiries.OrderBy(p => p.Key).Select(p => new AiResourceExpiry(p.Key, p.Value)).ToArray(),
                    HasAdmittedPlan = coveragePlanId != 0,
                    FirstStepEndTick=firstEnd,FollowUpRequestTick=followRequestTick,FollowUpStartTick=followStart,
                    SequenceCompleted=followUp!=null && complete && followPlanId!=0,
                    FirstPlanId=firstPlanId,FollowUpPlanId=followPlanId,BridgePlanId=bridgePlanId,ResourceExchanges=exchanges.ToArray() };
        }
    }
}
