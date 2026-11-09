using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Experiments
{
    public enum RecoveryPair { ProbeSlash, ProbePierce, ProbeBlunt, HeavyQuick, HeavySweep,
        UnopposedQuick, UnopposedHeavy, UnopposedSweep }
    public enum RecoveryResponse { Repeat, WaitRealign, Guard, Block, Dodge, Move, Switch }

    public sealed record RecoveryExperimentScenario(string Id, int RecoveryTicks = 30,
        RecoveryPair Pair = RecoveryPair.ProbeSlash, RecoveryResponse Response = RecoveryResponse.Repeat,
        int BluntOwner = 1, bool SwapSpawns = false, int FirstImpact = 0, int Growth = 0,
        int Health = 24, int StabilityQ10 = 10240, bool ShortRange = false, bool Wall = false,
        int MaxTicks = 720, ulong Seed = 31)
    {
        public const string Version = "combat_recovery.scenes.1";
        public const string ScriptVersion = "combat_recovery.fixed_scripts.1";
        public bool Unopposed => Pair >= RecoveryPair.UnopposedQuick;
        public string BluntAction => Pair == RecoveryPair.HeavyQuick || Pair == RecoveryPair.HeavySweep
            || Pair == RecoveryPair.UnopposedHeavy ? "heavy"
            : Pair == RecoveryPair.UnopposedQuick ? "quick" : Pair == RecoveryPair.UnopposedSweep ? "sweep" : "probe_blunt";
        public string OpponentAction => Pair == RecoveryPair.ProbePierce ? "probe_pierce"
            : Pair == RecoveryPair.ProbeBlunt ? "probe_blunt" : Pair == RecoveryPair.HeavyQuick ? "quick"
            : Pair == RecoveryPair.HeavySweep ? "sweep" : "probe_slash";
        public int Windup(string action) => action == "heavy" ? 90 : action == "sweep" ? 60 : 30;
        public long OpeningImpact => FirstImpact > 0 ? FirstImpact : Unopposed ? 1 + Windup(BluntAction)
            : 183 + Math.Max(Windup(BluntAction), Windup(OpponentAction));
        public string ActionOf(long owner) => owner == BluntOwner || Unopposed ? BluntAction : OpponentAction;
    }

    public sealed class RecoveryActionTrace
    {
        public long PlanId, Owner, Start, Impact, End, Window, Locked = -1, ExecutedImpact = -1, Terminal = -1;
        public string Action;
        public int Type, Reason;
    }
    public sealed record RecoveryFrame(long Tick, long Unit, int HealthQ10, int State, long StateEnd,
        int X, int Y, int Available, int Reserved, long Window, long WindowOwner, bool Concurrent);
    public sealed record RecoveryExperimentResult(RecoveryExperimentScenario Scenario, BattleDefinition Definition,
        BattleReplay Replay, LogicSnapshot FinalSnapshot, string Outcome,
        IReadOnlyList<ExperimentDecision> Decisions, IReadOnlyList<ExperimentTick> Diagnostics,
        IReadOnlyList<LogicEvent> Events, IReadOnlyList<RecoveryActionTrace> Actions, IReadOnlyList<RecoveryFrame> Frames);

    public static class RecoveryExperimentRunner
    {
        public static readonly int[] CoarseTicks = { 30, 33, 36, 39, 42, 48, 60 };

        public static BattleDefinition BuildDefinition(RecoveryExperimentScenario s)
        {
            if (s == null || s.RecoveryTicks < 30 || s.RecoveryTicks > 330 || s.BluntOwner < 1 || s.BluntOwner > 2
                || s.Growth < 0 || s.Growth > 4 || !new[] { 24, 27, 30, 36 }.Contains(s.Health)
                || s.StabilityQ10 <= 0 || s.MaxTicks <= s.OpeningImpact
                || !Enum.IsDefined(typeof(RecoveryPair), s.Pair) || !Enum.IsDefined(typeof(RecoveryResponse), s.Response)
                || (!s.Unopposed && s.OpeningImpact - Math.Max(s.Windup(s.BluntAction), s.Windup(s.OpponentAction)) < 183))
                throw new LogicDefinitionException("RECOVERY_EXPERIMENT_INVALID");
            var d = CombatExperimentDefinition.Build(new CombatExperimentScenario(s.Id, ShortRange: s.ShortRange,
                Wall: s.Wall, BluntRecoveryDelta: s.RecoveryTicks - 30));
            var attributes = new CombatAttributesV05(1 + s.Growth, 1 + s.Growth, s.Health == 36 ? 5 : 1,
                s.Health == 36 ? 0 : s.Health - 24, s.StabilityQ10);
            // These are explicitly symmetric growth/health probes, not same-budget character builds.
            var units = d.Units.Select(u => CombatValuesV05.Unit(u.UnitDefinitionId, attributes, u.ActionSetId, u.VolumeSpecId)).ToArray();
            var encounter = d.Encounters.Single();
            if (s.SwapSpawns) encounter = encounter with { Slots = encounter.Slots.Select(slot => slot with {
                InitialPosition = new GridPoint(6 - slot.InitialPosition.X, slot.InitialPosition.Y) }).ToArray() };
            d = d with { Units = units, Encounters = new[] { encounter } };
            d = d with { BattleDefinitionHashValue = CombatExperimentDefinition.Hash(d) };
            string invalid = CombatValuesV05.ValidateDefinition(d);
            if (invalid != null) throw new LogicDefinitionException(invalid);
            return d;
        }

        sealed class ScriptState
        {
            public readonly HashSet<long> Initialized = new HashSet<long>();
            public readonly HashSet<long> ResponseUsed = new HashSet<long>();
            public readonly HashSet<long> ActivatedWindows = new HashSet<long>();
            public readonly HashSet<string> Noted = new HashSet<string>(StringComparer.Ordinal);
            public long FirstClash = -1, LastClash = -1;
        }

        public static RecoveryExperimentResult Run(RecoveryExperimentScenario s)
        {
            var d = BuildDefinition(s);
            var decisions = new List<ExperimentDecision>(); var diagnostics = new List<ExperimentTick>();
            var events = new List<LogicEvent>(); var actions = new Dictionary<long, RecoveryActionTrace>();
            var frames = new List<RecoveryFrame>(); var state = new ScriptState();
            using var sim = ProductionBattleComposition.Create(d, CombatExperimentDefinition.Encounter, new BattleRuntimeInputs(s.Seed, 4));
            var recorder = new ReplayRecorder(sim);
            for (int tick = 0; tick < s.MaxTicks && !sim.IsEnded; tick++)
            {
                if (tick > 0) SubmitScript(sim, s, state, decisions);
                long processingWindow = sim.CurrentTurnWindow?.WindowId.Value ?? 0;
                recorder.CaptureBeforeStep();
                var step = sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                recorder.RecordCommittedStep(step); events.AddRange(step.Events.Events);
                if (sim.LastDamageCommitReport.Units.Count > 0 || sim.LastAdrenalineAccrualFacts.Count > 0)
                    diagnostics.Add(new ExperimentTick(tick, sim.LastAdrenalineAccrualFacts, sim.LastDamageCommitReport));
                foreach (var fact in step.Events.Events)
                {
                    if (fact is ClashParticipantResolvedEvent)
                    { if (state.FirstClash < 0) state.FirstClash = tick; state.LastClash = tick; }
                    if (fact is ActionPlanCreatedEvent created)
                    {
                        var plan = step.Snapshot.Plans.FirstOrDefault(p => p.ActionPlanId == created.ActionPlanId);
                        var timing = d.FindAction(new ActionSpecId(created.ActionSpecId)).Timing as AttackTimingSpec;
                        actions.Add(created.ActionPlanId, new RecoveryActionTrace { PlanId = created.ActionPlanId,
                            Owner = created.OwnerUnitId, Action = created.ActionSpecId, Type = created.ActionType,
                            // A same-Tick control/death may already have archived this newly created plan.
                            // Its published EndTick minus the immutable recovery preserves the accepted impact.
                            Start = created.StartTick, Impact = plan?.ImpactTick ?? (timing == null ? 0 : created.EndTick - timing.RecoveryTicks),
                            End = created.EndTick, Window = plan?.SubmittedWindowId
                                ?? (created.Origin == (int)ActionPlanOrigin.Ordinary ? processingWindow : 0) });
                    }
                    if (fact is ActionPlanLockedEvent locked && actions.TryGetValue(locked.ActionPlanId, out var started))
                    { started.Locked = tick; started.Start = locked.StartTick; }
                    if (fact is ActionPlanTerminatedEvent terminal && actions.TryGetValue(terminal.ActionPlanId, out var ended))
                    { ended.Terminal = tick; ended.Reason = terminal.Reason; }
                    if (fact is ActionPlanCompletedEvent completed && actions.TryGetValue(completed.ActionPlanId, out var complete))
                        complete.Terminal = tick;
                }
                foreach (var plan in step.Snapshot.Plans)
                    if (actions.TryGetValue(plan.ActionPlanId, out var trace))
                    { trace.Start = plan.StartTick; trace.Impact = plan.ImpactTick; trace.End = plan.EndTick; }
                foreach (var intent in step.Snapshot.Intents)
                    if (actions.TryGetValue(intent.ActionPlanId, out var trace)) trace.ExecutedImpact = tick;
                if (tick >= s.OpeningImpact - 1)
                    foreach (var unit in step.Snapshot.Units)
                    {
                        var ledger = step.Snapshot.Resources.AdrenalineLedgers?.FirstOrDefault(l => l.UnitId == unit.UnitId);
                        var window = sim.CurrentTurnWindow;
                        frames.Add(new RecoveryFrame(tick, unit.UnitId, unit.HealthQ10, unit.State, unit.StateEndTick,
                            unit.X, unit.Y, unit.AvailableAdrenaline, ledger?.ReservedTotal ?? 0,
                            window?.WindowId.Value ?? 0, window?.OwnerUnitId.Value ?? 0,
                            step.Snapshot.ConcurrentAction.HasActiveAuthorization));
                    }
            }
            if (events.OfType<CommandRejectedEvent>().Any() || decisions.Any(c => c.IngressRejection != null && c.IngressRejection != "not-submitted"))
                throw new LogicDefinitionException("RECOVERY_EXPERIMENT_COMMAND_REJECTED", s.Id);
            if (!s.Unopposed && state.FirstClash != s.OpeningImpact)
                throw new LogicDefinitionException("RECOVERY_EXPERIMENT_FIRST_CLASH_MISSING", s.Id);
            foreach (var unit in sim.CurrentSnapshot.Units)
                if (diagnostics.Sum(t => t.Damage.Units.Where(c => c.UnitId.Value == unit.UnitId).Sum(c => c.DamageQ10)) != s.Health * 1024L - unit.HealthQ10)
                    throw new LogicDefinitionException("RECOVERY_EXPERIMENT_DAMAGE_NOT_CONSERVED", s.Id);
            return new RecoveryExperimentResult(s, d, recorder.BuildReplay(), sim.CurrentSnapshot,
                sim.IsEnded ? sim.BattleEnd.ResultCode : "observation-limit", decisions.AsReadOnly(), diagnostics.AsReadOnly(),
                events.AsReadOnly(), actions.Values.OrderBy(a => a.PlanId).ToArray(), frames.AsReadOnly());
        }

        static void SubmitScript(BattleSimulation sim, RecoveryExperimentScenario s, ScriptState state, List<ExperimentDecision> decisions)
        {
            var window = sim.CurrentTurnWindow;
            if (!s.Unopposed && window != null && window.OwnerUnitId.Value != 1
                && !state.ActivatedWindows.Contains(window.WindowId.Value))
            {
                if (sim.CurrentSnapshot.Resources.MetaResource >= sim.Definition.ConcurrentAction.MetaResourceCost)
                    Submit(sim, 1, new CommandRequest(sim.Tick + 1, new WindowCommandScope(window.WindowId),
                        new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction)), "normal-concurrent-activation", "activate", decisions);
                state.ActivatedWindows.Add(window.WindowId.Value);
                return; // authorization must be committed before either initial attack is submitted
            }
            foreach (long owner in new long[] { 1, 2 })
            {
                if (s.Unopposed && owner != 1) continue;
                var controller = CombatExperimentDefinition.Controller(owner); var view = sim.DecisionSnapshotFor(controller);
                var unit = view.VisibleUnits.Single(u => u.UnitId == owner);
                if (!unit.IsAlive) continue;
                var next = sim.Tick + 1;
                bool initial = !state.Initialized.Contains(owner);
                var own = view.OwnPlans.Where(p => p.OwnerUnitId == owner).ToArray();
                long tail = own.Select(p => p.Origin == (int)ActionPlanOrigin.Reaction ? p.SourceImpactTick + 1 : p.EndTick).DefaultIfEmpty(next).Max();
                if (!initial && !s.Unopposed && state.FirstClash < 0) continue;
                if (!initial && tail > next) continue;
                if (unit.StateValue == UnitState.Staggered || unit.StateValue == UnitState.KnockedDown || unit.StateValue == UnitState.Recovering)
                { Note(sim, state, owner, "control-blocks-start", decisions); continue; }
                string action = s.ActionOf(owner);
                long start = initial ? s.OpeningImpact - s.Windup(action) : next;
                if (initial && start < next) throw new LogicDefinitionException("RECOVERY_EXPERIMENT_MISSED_OPENING", s.Id);
                bool responding = !initial && owner == (s.Response == RecoveryResponse.WaitRealign ? 3 - s.BluntOwner : s.BluntOwner);
                if (responding && s.Response == RecoveryResponse.WaitRealign && state.LastClash >= 0)
                {
                    // This is a declared fixed commitment, not a read of an opponent's hidden draft.
                    long align = state.LastClash + s.RecoveryTicks + s.Windup(s.BluntAction) - s.Windup(action);
                    start = Math.Max(start, align);
                }
                if (responding && s.Response == RecoveryResponse.Switch) action = "quick";
                if (responding && !state.ResponseUsed.Contains(owner) && (s.Response == RecoveryResponse.Block || s.Response == RecoveryResponse.Dodge))
                {
                    var context = AiDecisionContextFactory.Build(view, controller, sim.AiReactionOpportunities,
                        sim.AiActionPlanLookup, sim.MovementPathCalculator);
                    var kind = s.Response == RecoveryResponse.Block ? ReactionCommandKind.Block : ReactionCommandKind.Dodge;
                    var available = AiReactionCandidateBuilder.BuildReactionCandidatesOrdered(context, view.Tick);
                    var selected = available.FirstOrDefault(o => o.ReactionKind == kind
                        && view.FindAction(o.ActionSpecId).AdrenalineCost <= unit.AvailableAdrenaline);
                    if (selected != null)
                    {
                        Submit(sim, owner, new CommandRequest(next, new ReactionCommandScope(selected.ReactionOpportunityId),
                            new ReactionCommandPayload(kind, selected.ActionSpecId, selected.SelectedDestination, selected.StartTick)),
                            "fixed-funded-response", kind.ToString(), decisions);
                        state.ResponseUsed.Add(owner); continue;
                    }
                    Note(sim, state, owner, unit.AvailableAdrenaline < CombatValuesV05.ResourceUnitsPerPoint ? "no-resource"
                        : context.Opportunities.Count == 0 ? "no-published-opportunity"
                        : kind == ReactionCommandKind.Dodge && !available.Any(o => o.ReactionKind == kind) ? "no-safe-or-legal-dodge"
                        : "no-legal-reaction-timing", decisions);
                }
                if (window == null || start >= window.EndTick)
                { Note(sim, state, owner, "start-outside-current-window", decisions); continue; }
                bool concurrent = sim.CurrentSnapshot.ConcurrentAction.HasActiveAuthorization
                    && sim.CurrentSnapshot.ConcurrentAction.WindowId == window.WindowId.Value
                    && sim.CurrentSnapshot.ConcurrentAction.PlayerUnitId == owner;
                if (window.OwnerUnitId.Value != owner && !concurrent)
                { Note(sim, state, owner, "no-submission-permission", decisions); continue; }
                if (responding && !state.ResponseUsed.Contains(owner) && s.Response == RecoveryResponse.Guard) action = "guard";
                GridPoint? destination = null;
                if (responding && !state.ResponseUsed.Contains(owner) && s.Response == RecoveryResponse.Move)
                {
                    action = "move";
                    var other = view.VisibleUnits.Single(u => u.UnitId == 3 - owner);
                    destination = new GridPoint(unit.X < other.X ? -12 : 18, unit.Y);
                }
                var operation = new AddOrdinaryPlanOperation(1, new UnitId(owner), CombatExperimentDefinition.Action(action), start,
                    PrimaryTargetUnitId: action == "guard" || action == "move" ? (UnitId?)null : new UnitId(3 - owner),
                    Facing: unit.X <= view.VisibleUnits.Single(u => u.UnitId == 3 - owner).X ? GridDirection.East : GridDirection.West,
                    Destination: destination);
                var preview = sim.ScheduleEditor.Apply(new[] { operation }, next, view.ScheduleRevision, view.ScheduleRevision,
                    preview: true, expectedWindowId: window.WindowId, issuer: controller);
                if (!preview.Succeeded)
                { Note(sim, state, owner, "preview:" + preview.RejectionCode, decisions); continue; }
                Submit(sim, owner, new CommandRequest(next, new ScheduleEditScope(view.ScheduleRevision, window.WindowId),
                    new ScheduleEditPayload(new[] { operation })), initial ? "fixed-opening" : "fixed-" + s.Response, action, decisions);
                state.Initialized.Add(owner);
                if (responding && (action == "guard" || action == "move")) state.ResponseUsed.Add(owner);
            }
        }

        static void Submit(BattleSimulation sim, long owner, CommandRequest request, string reason, string action, List<ExperimentDecision> decisions)
        {
            var error = sim.CommandIngress.FindEntry(CombatExperimentDefinition.Controller(owner)).Submit(request);
            decisions.Add(new ExperimentDecision(sim.Tick, owner, reason, action, error?.ReasonCode));
        }
        static void Note(BattleSimulation sim, ScriptState state, long owner, string reason, List<ExperimentDecision> decisions)
        {
            string key = owner + ":" + (sim.CurrentTurnWindow?.WindowId.Value ?? 0) + ":" + state.LastClash + ":" + reason;
            if (state.Noted.Add(key)) decisions.Add(new ExperimentDecision(sim.Tick, owner, reason, "wait", "not-submitted"));
        }

        public static void VerifyReplay(RecoveryExperimentResult result)
        {
            using var stream = new MemoryStream(); ReplayFile.Save(stream, result.Replay); stream.Position = 0;
            using var player = new ReplayPlayer(result.Definition); player.Load(ReplayFile.Load(stream, result.Definition)); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            if (player.Deviation != null || !player.IsComplete)
                throw new LogicDefinitionException("RECOVERY_EXPERIMENT_REPLAY_DEVIATION", result.Scenario.Id + "|" + player.Deviation);
        }

        public static IReadOnlyList<RecoveryExperimentScenario> Matrix(IEnumerable<int> ticks)
        {
            var cases = new List<RecoveryExperimentScenario>();
            foreach (int recovery in ticks.Distinct().OrderBy(v => v))
            {
                void Add(RecoveryPair pair = RecoveryPair.ProbeSlash, RecoveryResponse response = RecoveryResponse.Repeat,
                    int owner = 1, bool swap = false, int impact = 0, int growth = 0, int health = 24,
                    int stability = 10240, bool shortRange = false, bool wall = false)
                {
                    var scenario = new RecoveryExperimentScenario("", recovery, pair, response, owner, swap, impact, growth,
                        health, stability, shortRange, wall, pair >= RecoveryPair.UnopposedQuick ? 1800 : 720);
                    string id = "rec" + recovery + "_" + pair + "_" + response + "_b" + owner + "_s" + (swap ? 1 : 0)
                        + "_t" + scenario.OpeningImpact + "_p" + growth + "_h" + health + "_r" + stability + "_short" + (shortRange ? 1 : 0) + "_wall" + (wall ? 1 : 0);
                    cases.Add(scenario with { Id = id });
                }
                foreach (int owner in new[] { 1, 2 })
                {
                    foreach (bool swap in new[] { false, true }) foreach (int impact in new[] { 213, 273, 329, 330, 360 })
                        Add(owner: owner, swap: swap, impact: impact);
                    foreach (var response in new[] { RecoveryResponse.WaitRealign, RecoveryResponse.Guard, RecoveryResponse.Block,
                        RecoveryResponse.Dodge, RecoveryResponse.Move, RecoveryResponse.Switch })
                    {
                        foreach (int impact in new[] { 213, 273 }) Add(response: response, owner: owner, impact: impact);
                        Add(RecoveryPair.HeavyQuick, response, owner);
                    }
                    Add(RecoveryPair.ProbeBlunt, owner: owner); Add(RecoveryPair.ProbePierce, owner: owner);
                    foreach (var pair in new[] { RecoveryPair.HeavyQuick, RecoveryPair.HeavySweep })
                        foreach (int impact in new[] { 273, 303, 330 })
                            foreach (var setting in new[] { (0,24),(1,27),(2,30),(3,36),(4,24),(0,27),(0,30),(0,36) })
                                Add(pair, owner: owner, impact: impact, growth: setting.Item1, health: setting.Item2);
                    foreach (int growth in new[] { 0, 4 }) foreach (int stability in new[] { 4096,6144,10240,10241,15360 })
                        Add(RecoveryPair.HeavyQuick, owner: owner, growth: growth, stability: stability);
                    foreach (int stability in new[] { 4096,10240 }) foreach (bool wall in new[] { false, true })
                        Add(RecoveryPair.HeavyQuick, owner: owner, stability: stability, shortRange: true, wall: wall);
                    foreach (var response in new[] { RecoveryResponse.Dodge, RecoveryResponse.Move })
                        foreach (int growth in new[] { 0,4 })
                            Add(response: response, owner: owner, growth: growth, health: 36, shortRange: true);
                }
                foreach (var pair in new[] { RecoveryPair.UnopposedQuick, RecoveryPair.UnopposedHeavy, RecoveryPair.UnopposedSweep })
                    foreach (var setting in new[] { (0,24),(1,27),(2,30),(3,36),(4,24),(0,27),(0,30),(0,36) })
                        Add(pair, growth: setting.Item1, health: setting.Item2);
            }
            return cases.GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.First()).ToArray();
        }
    }
}
