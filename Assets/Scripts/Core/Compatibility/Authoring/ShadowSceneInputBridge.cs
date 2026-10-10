#if UNITY_EDITOR
using System;
using System.Linq;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Ids;
using UnityEngine;
using LogicActionType = ProjectHero.Logic.Actions.ActionType;
using LogicPoint = ProjectHero.Logic.Grid.GridPoint;
using LogicDirection = ProjectHero.Logic.Grid.GridDirection;

namespace ProjectHero.Core.Compatibility.Authoring
{
    // Called only by confirmed Legacy scheduling, without a clock or any scene/feedback writes.
    public sealed class ShadowSceneInputBridge : MonoBehaviour
    {
        [SerializeField] private BattleRuntimeBootstrap _bootstrap;
        [SerializeField] private BattleSimulationSourceFactory _source;
        public int SubmittedCount { get; private set; }
        public int RejectedCount { get; private set; }
        public string LastRejection { get; private set; }
        private void OnEnable() => ActionScheduler.ActionScheduled += OnScheduled;
        private void OnDisable() => ActionScheduler.ActionScheduled -= OnScheduled;
        private void OnScheduled(LegacyScheduledAction fact)
        {
            if (_bootstrap == null || _bootstrap.BattleMode != BattleRuntimeMode.Shadow) return;
            if (_bootstrap.IsPaused || fact.Timeline.Paused) { Reject("SHADOW_INPUT_PAUSED"); return; }
            if (_source == null || !_source.TryResolvePlayerUnit(fact.Owner, out var owner, out var controller)) return;
            var runner = _bootstrap.Adapters.Shadow;
            var snapshot = runner?.CurrentSnapshot;
            if (snapshot == null || snapshot.BattleEnd.IsEnded) { Reject("SHADOW_INPUT_NOT_ACTIVE"); return; }
            long tick = checked(snapshot.Tick + 1);
            CommandRequest request;
            if (fact.Kind == LogicActionType.Block || fact.Kind == LogicActionType.Dodge)
            {
                var options = snapshot.ReactionOpportunities.Where(o => o.DefenderUnitId == owner.Value
                    && o.TelegraphTick <= snapshot.Tick && o.State == (int)ReactionOpportunityState.Open).ToArray();
                if (options.Length != 1) { Reject("SHADOW_INPUT_REACTION_OPPORTUNITY_REQUIRED"); return; }
                if (fact.Kind == LogicActionType.Dodge && !fact.Destination.HasValue)
                { Reject("SHADOW_INPUT_DODGE_DESTINATION_REQUIRED"); return; }
                var spec = runner.Seed.Definition.Actions.SingleOrDefault(a => a.Type == fact.Kind);
                if (spec == null) { Reject("SHADOW_INPUT_ACTION_NOT_BOUND"); return; }
                request = new CommandRequest(tick, new ReactionCommandScope(new ReactionOpportunityId(options[0].ReactionOpportunityId)),
                    new ReactionCommandPayload(fact.Kind == LogicActionType.Block ? ReactionCommandKind.Block : ReactionCommandKind.Dodge,
                        spec.ActionSpecId, fact.Destination.HasValue ? new LogicPoint(fact.Destination.Value.X, fact.Destination.Value.Y) : (LogicPoint?)null));
            }
            else
            {
                ActionSpecId id;
                if (fact.Kind == LogicActionType.Attack)
                {
                    if (!_source.TryResolveAttack(fact.Owner, fact.Attack, out id)) { Reject("SHADOW_INPUT_ACTION_NOT_BOUND"); return; }
                }
                else
                {
                    var action = runner.Seed.Definition.Actions.SingleOrDefault(a => a.Type == fact.Kind);
                    if (action == null) { Reject("SHADOW_INPUT_ACTION_NOT_BOUND"); return; }
                    id = action.ActionSpecId;
                }
                if (float.IsNaN(fact.StartDelay) || float.IsInfinity(fact.StartDelay) || fact.StartDelay < 0)
                { Reject("SHADOW_INPUT_TIME_INVALID"); return; }
                var start = Math.Max(tick, checked(snapshot.Tick + (long)Math.Round(
                    fact.StartDelay * runner.Seed.Definition.TicksPerSecond, MidpointRounding.AwayFromZero)));
                var window = snapshot.WindowManager.Windows.SingleOrDefault(w => w.IsOpen && w.IsAcceptingSubmissions);
                request = new CommandRequest(tick, new ScheduleEditScope(snapshot.ScheduleRevision,
                    window != null ? new WindowId(window.WindowId) : (WindowId?)null), new ScheduleEditPayload(new ScheduleEditOperation[] {
                    new AddOrdinaryPlanOperation(1, owner, id, start, default, null, (LogicDirection)(int)fact.Facing,
                        fact.Destination.HasValue ? new LogicPoint(fact.Destination.Value.X, fact.Destination.Value.Y) : (LogicPoint?)null) }));
            }
            string error = runner.SubmitLivePlayerRequest(controller, request);
            if (error != null) { Reject(error); return; }
            SubmittedCount++; LastRejection = null;
        }
        private void Reject(string code) { RejectedCount++; LastRejection = code; }
    }
}

#endif
