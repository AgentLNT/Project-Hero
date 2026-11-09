using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Combat;
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
    public enum Stage4Layout { ProbeOpen, ProbeWall, HeavyQuickOpen, HeavyQuickWall, TeamSweepOpen, TeamSweepWall, HoldoutDiagonal, HoldoutStaggered }
    public sealed record Stage4Input(Stage4Layout Layout, int RecoveryTicks, AiPolicyKind BluntPolicy, AiPolicyKind OpponentPolicy,
        bool OpponentFirst = false, bool SwapSpawns = false, ulong Seed = 31, int WindowTicks = 180)
    {
        public const string Version = "recovery_policy_long_matches.1";
        public bool Team => Layout == Stage4Layout.TeamSweepOpen || Layout == Stage4Layout.TeamSweepWall || Layout == Stage4Layout.HoldoutStaggered;
        public bool Holdout => Layout >= Stage4Layout.HoldoutDiagonal;
        public bool Wall => Layout == Stage4Layout.ProbeWall || Layout == Stage4Layout.HeavyQuickWall || Layout == Stage4Layout.TeamSweepWall;
        public int MaxTicks => WindowTicks * (Team ? 8 : 6);
        public string Block => string.Format(CultureInfo.InvariantCulture, "{0}-{1}-{2}-first{3}-swap{4}-seed{5}-w{6}",
            Layout, BluntPolicy, OpponentPolicy, OpponentFirst ? "B" : "A", SwapSpawns ? 1 : 0, Seed, WindowTicks);
        public string Id => Block + "-r" + RecoveryTicks.ToString(CultureInfo.InvariantCulture);
    }

    public static class Stage4Matrix
    {
        public static readonly int[] CoreTicks = { 30, 31, 32, 39, 40, 41 };
        public static readonly int[] BoundaryTicks = { 31, 40, 42, 43, 44, 58, 59, 60 };
        public static IReadOnlyList<Stage4Input> All()
        {
            var result = new List<Stage4Input>();
            Add(Stage4Layout.ProbeOpen, AiPolicyKind.SustainedOffense, AiPolicyKind.SustainedOffense, CoreTicks);
            Add(Stage4Layout.ProbeWall, AiPolicyKind.SeekInterrupt, AiPolicyKind.SeekInterrupt, CoreTicks);
            Add(Stage4Layout.HeavyQuickOpen, AiPolicyKind.SustainedOffense, AiPolicyKind.CautiousDefense, CoreTicks);
            Add(Stage4Layout.HeavyQuickWall, AiPolicyKind.CautiousDefense, AiPolicyKind.SustainedOffense, CoreTicks);
            Add(Stage4Layout.TeamSweepOpen, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense, CoreTicks);
            Add(Stage4Layout.TeamSweepWall, AiPolicyKind.CautiousDefense, AiPolicyKind.SeekInterrupt, CoreTicks);
            Add(Stage4Layout.ProbeOpen, AiPolicyKind.CautiousDefense, AiPolicyKind.CautiousDefense, CoreTicks);
            Add(Stage4Layout.HeavyQuickOpen, AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, CoreTicks);
            foreach (var policy in new[] { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense })
                Add(Stage4Layout.ProbeOpen, policy, AiPolicyKind.Legacy, new[] { 31, 40 });
            Add(Stage4Layout.HoldoutDiagonal, AiPolicyKind.SustainedOffense, AiPolicyKind.CautiousDefense, BoundaryTicks);
            Add(Stage4Layout.HoldoutStaggered, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense, BoundaryTicks);
            Add(Stage4Layout.ProbeOpen, AiPolicyKind.SustainedOffense, AiPolicyKind.Legacy, new[] { 31, 40 }, 97);
            foreach (int window in new[] { 179, 181 })
                Add(Stage4Layout.ProbeOpen, AiPolicyKind.SustainedOffense, AiPolicyKind.SustainedOffense, new[] { 31, 40 }, 31, window);
            return result.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();

            void Add(Stage4Layout layout, AiPolicyKind a, AiPolicyKind b, int[] ticks, ulong seed = 31, int window = 180)
            {
                foreach (bool first in new[] { false, true }) foreach (bool swap in new[] { false, true }) foreach (int recovery in ticks)
                    result.Add(new Stage4Input(layout, recovery, a, b, first, swap, seed, window));
            }
        }
    }

    public sealed class Stage4SideMetrics
    {
        public long HealthQ10, DamageReceivedQ10, ReceivedDamageUnitTicks, NominalDirectQ10, DamagingDirectContacts, AttackCreated, AttackStarts, AttackImpacts, CancelledWindups;
        public long GuardStarts, MoveStarts, BlockTriggers, DodgeTriggers, BlockSuccesses, DodgeSuccesses, Clashes, MaxClashStreak;
        public long PostClashRecoveryHits, PostClashRecoveryLossQ10, RecoveryHits, RecoveryLossQ10, ControlUnitTicks, DisplacementSteps;
        public long IncomeRequested, IncomeCredited, IncomeOverflow, Reserved, Consumed, Refunded, CycleExpired, BattleEndAvailableCleared, FinalAvailable, FinalReserved;
        public long Windows, WindowTicks, WindowBusyTicks, WindowNoPositiveTicks, WaitCommittedTicks, WaitCadenceTicks, ReactionNoBenefitTicks;
        public long Decisions, Selected, EligibleCandidates, ResourceBlockedDecisionTicks, ReactionCandidateTicks, NoDodgeDestinationTicks, IngressRejections;
    }
    public sealed class Stage4PlanTrace
    {
        public long Id, Owner, Window, Created, Start, Impact, End, Locked = -1, ExecutedImpact = -1, Terminated = -1;
        public int Type; public string Spec, TerminalReason;
    }
    public sealed record Stage4DecisionFrame(long Tick, string Controller, bool BluntSide, long WindowId, long WindowOwner,
        bool OwnWindow, int PublicThreats, int Available, int Reserved, int NoDodgeDestinations, AiDecision Decision);
    public sealed record Stage4Result(Stage4Input Input, BattleDefinition Definition, BattleReplay Replay, LogicSnapshot FinalSnapshot,
        string Outcome, Stage4SideMetrics Blunt, Stage4SideMetrics Opponent, IReadOnlyList<Stage4PlanTrace> Plans,
        IReadOnlyDictionary<string, int> Rejections, long FirstClashTick, long LastClashTick);

    /// <summary>All combat and all AI choices remain production code. This fixture only supplies immutable definitions and reads facts.</summary>
    public static class Stage4RecoveryExperiment
    {
        public static readonly ControllerId BluntController = new ControllerId("controller.stage4.a");
        public static readonly ControllerId OpponentController = new ControllerId("controller.stage4.b");
        public static BattleDefinition Build(Stage4Input s)
        {
            if (s == null || !Enum.IsDefined(typeof(Stage4Layout), s.Layout) || !Enum.IsDefined(typeof(AiPolicyKind), s.BluntPolicy)
                || !Enum.IsDefined(typeof(AiPolicyKind), s.OpponentPolicy) || s.RecoveryTicks < 30 || s.RecoveryTicks > 60
                || s.WindowTicks < 179 || s.WindowTicks > 181) throw new LogicDefinitionException("STAGE4_INPUT_INVALID");
            var d = CombatExperimentDefinition.Build(new CombatExperimentScenario("stage4", ShortRange: true, BluntRecoveryDelta: s.RecoveryTicks - 30));
            var encounter = d.Encounters[0];
            bool heavy = s.Layout == Stage4Layout.HeavyQuickOpen || s.Layout == Stage4Layout.HeavyQuickWall
                || s.Layout == Stage4Layout.TeamSweepOpen || s.Layout == Stage4Layout.TeamSweepWall;
            string aAttack = heavy ? "heavy" : "probe_blunt";
            string bAttack = s.Team && heavy ? "sweep" : heavy ? "quick" : "probe_slash";
            var aSet = new ActionSetDefinition(new ActionSetId("action_set.stage4.a"), Actions(aAttack));
            var bSet = new ActionSetDefinition(new ActionSetId("action_set.stage4.b"), Actions(bAttack));
            var units = new List<UnitDefinition>(); var slots = new List<Encounter.EncounterUnitSlot>();
            var aSlots = new List<EncounterSlotId>(); var bSlots = new List<EncounterSlotId>();
            for (int i = 1; i <= (s.Team ? 4 : 2); i++)
            {
                bool a = (i % 2 == 1) != s.OpponentFirst; int row = (i - 1) / 2;
                var unitId = new UnitDefinitionId("unit.stage4." + i); var slotId = new EncounterSlotId("slot.stage4." + i);
                var position = new GridPoint(a ? 0 : 6, row * 6);
                if (s.Layout == Stage4Layout.HoldoutDiagonal) position = a ? new GridPoint(0, 0) : new GridPoint(10, 4);
                if (s.Layout == Stage4Layout.HoldoutStaggered) position = new GridPoint(a ? 0 : 10, row * 8 + (a ? 0 : 4));
                if (s.SwapSpawns) position = new GridPoint((s.Holdout ? 10 : 6) - position.X, position.Y);
                var set = a ? aSet : bSet;
                units.Add(CombatValuesV05.Unit(unitId, new CombatAttributesV05(1, 1, 1), set.ActionSetId, d.Volumes[0].VolumeSpecId));
                slots.Add(new Encounter.EncounterUnitSlot(slotId, unitId, encounter.Slots[a ? 0 : 1].FactionId, position,
                    (a != s.SwapSpawns) ? GridDirection.East : GridDirection.West));
                (a ? aSlots : bSlots).Add(slotId);
            }
            encounter = encounter with { GridBoundary = new GridBoundaryDefinition(new GridPoint(-16, -12), new GridPoint(s.Wall ? 8 : 24, 24)),
                Slots = slots.AsReadOnly(), Controllers = Array.AsReadOnly(new[] {
                    new ControllerBinding(BluntController, CommandSourceKind.Ai, aSlots.AsReadOnly(), s.BluntPolicy),
                    new ControllerBinding(OpponentController, CommandSourceKind.Ai, bSlots.AsReadOnly(), s.OpponentPolicy) }),
                TurnSubmission = new TurnSubmissionDefinition(s.WindowTicks, aSlots[0]) };
            d = d with { Units = units.AsReadOnly(), ActionSets = Array.AsReadOnly(new[] { aSet, bSet }),
                Encounters = Array.AsReadOnly(new[] { encounter }), BattleDefinitionHashValue = "" };
            string invalid = CombatValuesV05.ValidateDefinition(d); if (invalid != null) throw new LogicDefinitionException(invalid);
            return d with { BattleDefinitionHashValue = CombatExperimentDefinition.Hash(d) };

            IReadOnlyList<ActionSpecId> Actions(string attack) => Array.AsReadOnly(new[] { attack, "block", "dodge", "guard", "move" }
                .Select(CombatExperimentDefinition.Action).OrderBy(a => a.Value, StringComparer.Ordinal).ToArray());
        }

        public static Stage4Result Run(Stage4Input s, Action<Stage4DecisionFrame> decisions = null, Action<StepResult> committed = null)
        {
            var d = Build(s); var runtime = new BattleRuntimeInputs(s.Seed, 4);
            var composition = new ProductionBattleComposition(d, CombatExperimentDefinition.Encounter, runtime);
            using var sim = BattleSimulation.Create(d, CombatExperimentDefinition.Encounter, runtime, composition.Assembly);
            composition.Attach(sim); var recorder = new ReplayRecorder(sim);
            var a = new Stage4SideMetrics(); var b = new Stage4SideMetrics();
            var isA = d.Encounters[0].Slots.Select((slot, index) => new { id = index + 1L, a = slot.FactionId == d.Encounters[0].Slots.First(x =>
                d.Encounters[0].Controllers[0].ControlledSlots.Contains(x.SlotId)).FactionId }).ToDictionary(x => x.id, x => x.a);
            Stage4SideMetrics Side(long id) => isA[id] ? a : b;
            var traces = new Dictionary<long, Stage4PlanTrace>(); var streaks = new Dictionary<long, int>();
            var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
            long firstClash = -1, lastClash = -1;
            for (int tick = 0; tick < s.MaxTicks && !sim.IsEnded; tick++)
            {
                var before = sim.CurrentSnapshot; var processingWindow = sim.CurrentTurnWindow;
                recorder.CaptureBeforeStep(); var step = sim.Step(tick, sim.CommandIngress.FreezeTick(tick)); recorder.RecordCommittedStep(step);
                foreach (var unit in step.Snapshot.Units)
                {
                    var stats = Side(unit.UnitId); var old = before.Units.First(u => u.UnitId == unit.UnitId);
                    long loss = Math.Max(0, old.HealthQ10 - unit.HealthQ10); stats.DamageReceivedQ10 += loss;
                    if (loss > 0) stats.ReceivedDamageUnitTicks++;
                    if (unit.State == (int)UnitState.Staggered || unit.State == (int)UnitState.KnockedDown || unit.State == (int)UnitState.Recovering) stats.ControlUnitTicks++;
                    var recovery = before.Plans.Where(p => p.OwnerUnitId == unit.UnitId && p.ImpactTick > 0 && p.ImpactTick < tick && tick < p.EndTick
                        && d.FindAction(new ActionSpecId(p.ActionSpecId))?.Type == ActionType.Attack).ToArray();
                    var contacts = step.Events.Events.OfType<DamageChannelResolvedEvent>().Where(e => e.TargetUnitId.Value == unit.UnitId && e.AfterBlockDamageQ10 > 0).ToArray();
                    if (loss > 0 && contacts.Length > 0 && recovery.Length > 0)
                    {
                        stats.RecoveryHits += contacts.Length; stats.RecoveryLossQ10 += loss;
                        if (recovery.Any(p => p.ClashResolvedAtTick >= 0 && p.ClashResolvedAtTick < tick))
                        { stats.PostClashRecoveryHits += contacts.Length; stats.PostClashRecoveryLossQ10 += loss; }
                    }
                }
                foreach (var fact in step.Events.Events)
                {
                    if (fact is ActionPlanCreatedEvent created)
                    {
                        var plan = step.Snapshot.Plans.FirstOrDefault(p => p.ActionPlanId == created.ActionPlanId);
                        var timing = d.FindAction(new ActionSpecId(created.ActionSpecId)).Timing as AttackTimingSpec;
                        traces.Add(created.ActionPlanId, new Stage4PlanTrace { Id = created.ActionPlanId, Owner = created.OwnerUnitId,
                            Type = created.ActionType, Spec = created.ActionSpecId, Created = tick, Start = created.StartTick, End = created.EndTick,
                            Impact = plan?.ImpactTick ?? (timing == null ? 0 : created.EndTick - timing.RecoveryTicks),
                            Window = plan?.SubmittedWindowId ?? (created.Origin == (int)ActionPlanOrigin.Ordinary ? processingWindow?.WindowId.Value ?? 0 : 0) });
                        if (created.ActionType == (int)ActionType.Attack) Side(created.OwnerUnitId).AttackCreated++;
                    }
                    if (fact is ActionPlanLockedEvent locked && traces.TryGetValue(locked.ActionPlanId, out var started))
                    {
                        started.Locked = tick; started.Start = locked.StartTick; var stats = Side(started.Owner);
                        if (started.Type == (int)ActionType.Attack) stats.AttackStarts++;
                        else if (started.Type == (int)ActionType.Guard) stats.GuardStarts++;
                        else if (started.Type == (int)ActionType.Move) stats.MoveStarts++;
                    }
                    if (fact is ActionPlanTerminatedEvent terminal && traces.TryGetValue(terminal.ActionPlanId, out var ended))
                    {
                        ended.Terminated = tick; ended.TerminalReason = terminal.ReasonCode;
                        if (ended.Type == (int)ActionType.Attack && tick < ended.Impact) Side(ended.Owner).CancelledWindups++;
                    }
                    if (fact is TurnWindowOpenedEvent window) Side(window.OwnerUnitId.Value).Windows++;
                    if (fact is DamageChannelResolvedEvent damage)
                    {
                        Side(damage.AttackerUnitId.Value).NominalDirectQ10 += damage.AfterBlockDamageQ10;
                        if (damage.AfterBlockDamageQ10 > 0) Side(damage.AttackerUnitId.Value).DamagingDirectContacts++;
                    }
                    if (fact is ReactionTriggeredEvent reaction)
                    { if (reaction.ReactionType == (int)ActionType.Block) Side(reaction.DefenderUnitId).BlockTriggers++; else Side(reaction.DefenderUnitId).DodgeTriggers++; }
                    if (fact is BlockResolvedEvent block && block.RewardsBlock) Side(block.DefenderUnitId.Value).BlockSuccesses++;
                    if (fact is DodgeResolvedEvent dodge && dodge.RewardedSuccess) Side(dodge.DefenderUnitId.Value).DodgeSuccesses++;
                    if (fact is ForcedDisplacementResolvedEvent displacement) Side(displacement.TargetUnitId.Value).DisplacementSteps += displacement.AppliedSteps;
                    if (fact is AdrenalineLedgerChangedEvent money)
                    {
                        var stats = Side(money.UnitId.Value);
                        if (money.ChangeKind == AdrenalineChangeKind.Accrued) stats.IncomeCredited += money.AvailableAfter - money.AvailableBefore;
                        if (money.ChangeKind == AdrenalineChangeKind.Reserved) stats.Reserved += money.ReservationAmount;
                        if (money.ChangeKind == AdrenalineChangeKind.Consumed) stats.Consumed += money.ReservationAmount;
                        if (money.ChangeKind == AdrenalineChangeKind.Refunded) stats.Refunded += money.AvailableAfter - money.AvailableBefore;
                        if (money.ChangeKind == AdrenalineChangeKind.CycleReset) stats.CycleExpired += money.AvailableBefore;
                        if (money.ChangeKind == AdrenalineChangeKind.BattleEndCleared) stats.BattleEndAvailableCleared += money.AvailableBefore;
                    }
                    if (fact is CommandRejectedEvent rejected)
                    { string key = rejected.ReasonCode; rejections.TryGetValue(key, out int count); rejections[key] = count + 1; }
                }
                foreach (var income in sim.LastAdrenalineAccrualFacts)
                {
                    var stats = Side(income.UnitId.Value);
                    long requested = 2L * income.TotalFinalDamageDealtQ10 + 3L * income.TotalFinalDamageReceivedQ10;
                    stats.IncomeRequested += requested;
                    long credited = step.Events.Events.OfType<AdrenalineLedgerChangedEvent>().Where(e => e.UnitId == income.UnitId
                        && e.ChangeKind == AdrenalineChangeKind.Accrued).Sum(e => (long)e.AvailableAfter - e.AvailableBefore);
                    stats.IncomeOverflow += requested - credited;
                }
                foreach (var plan in step.Snapshot.Plans) if (traces.TryGetValue(plan.ActionPlanId, out var trace))
                { trace.Start = plan.StartTick; trace.Impact = plan.ImpactTick; trace.End = plan.EndTick; }
                var clashOwners = new HashSet<long>(step.Events.Events.OfType<ClashParticipantResolvedEvent>().Select(e => e.OwnerUnitId.Value));
                foreach (var id in clashOwners)
                {
                    if (firstClash < 0) firstClash = tick; lastClash = tick; Side(id).Clashes++;
                    streaks.TryGetValue(id, out int previous); streaks[id] = previous + 1;
                    Side(id).MaxClashStreak = Math.Max(Side(id).MaxClashStreak, previous + 1);
                }
                foreach (var intent in step.Snapshot.Intents) if (traces.TryGetValue(intent.ActionPlanId, out var attack))
                {
                    attack.ExecutedImpact = tick; Side(attack.Owner).AttackImpacts++;
                    if (!clashOwners.Contains(attack.Owner)) streaks[attack.Owner] = 0;
                }
                var currentWindow = sim.CurrentTurnWindow;
                if (currentWindow != null)
                {
                    var stats = Side(currentWindow.OwnerUnitId.Value); stats.WindowTicks++;
                    if (step.Snapshot.Plans.Any(p => p.OwnerUnitId == currentWindow.OwnerUnitId.Value && p.StartTick <= tick && tick < p.EndTick)) stats.WindowBusyTicks++;
                }
                foreach (var ai in composition.AiControllers)
                {
                    var decision = ai.DecisionAt(tick); if (decision == null) continue;
                    var controller = new ControllerId(ai.RegisteredControllerIds[0]); bool blunt = controller == BluntController;
                    var stats = blunt ? a : b; stats.Decisions++; if (decision.HasSelection) stats.Selected++;
                    stats.EligibleCandidates += decision.Evaluations.Count(e => e.RejectionCode == null);
                    if (decision.Evaluations.Any(e => e.IsReaction)) stats.ReactionCandidateTicks++;
                    if (decision.Evaluations.Any(e => e.RejectionCode == "ADRENALINE_INSUFFICIENT")) stats.ResourceBlockedDecisionTicks++;
                    if (decision.NoSelectionReason == "WAIT_LANE_COMMITTED") stats.WaitCommittedTicks++;
                    if (decision.NoSelectionReason == "WAIT_ORDINARY_CADENCE") stats.WaitCadenceTicks++;
                    if (decision.NoSelectionReason == "WAIT_NO_POSITIVE_CANDIDATE") stats.WindowNoPositiveTicks++;
                    if (decision.NoSelectionReason == "WAIT_REACTION_NOT_BENEFICIAL") stats.ReactionNoBenefitTicks++;
                    var snapshot = sim.DecisionSnapshotFor(controller); var context = ai.BuildContext(snapshot, controller);
                    int noLanding = context.Opportunities.Count(o => o.Options.Any(p => p.ReactionKind == Commands.ReactionCommandKind.Dodge)
                        && (o.DodgeDestinations?.Candidates?.Count ?? 0) == 0);
                    stats.NoDodgeDestinationTicks += noLanding;
                    var owned = snapshot.VisibleUnits.Where(u => snapshot.ControlledUnitIds.Any(id => id.Value == u.UnitId)).ToArray();
                    decisions?.Invoke(new Stage4DecisionFrame(tick, controller.Value, blunt, currentWindow?.WindowId.Value ?? 0,
                        currentWindow?.OwnerUnitId.Value ?? 0, context.HasSubmissionWindow, snapshot.VisiblePlans.Count(p => !snapshot.ControlledUnitIds.Any(id => id.Value == p.OwnerUnitId)
                            && snapshot.FindAction(new ActionSpecId(p.ActionSpecId))?.Type == ActionType.Attack),
                        owned.Sum(u => u.AvailableAdrenaline), owned.Sum(u => sim.AdrenalineLedgerOf(new UnitId(u.UnitId)).ReservedAdrenaline), noLanding, decision));
                    stats.IngressRejections = ai.IngressRejections.Count;
                }
                committed?.Invoke(step);
            }
            foreach (var unit in sim.CurrentSnapshot.Units)
            {
                var stats = Side(unit.UnitId); stats.HealthQ10 += unit.HealthQ10; stats.FinalAvailable += unit.AvailableAdrenaline;
                stats.FinalReserved += sim.AdrenalineLedgerOf(new UnitId(unit.UnitId)).ReservedAdrenaline;
            }
            var resultCode = sim.CurrentSnapshot.BattleEnd.ResultCode;
            return new Stage4Result(s, d, recorder.BuildReplay(), sim.CurrentSnapshot, sim.IsEnded ? resultCode : "timeout", a, b,
                traces.Values.OrderBy(p => p.Id).ToArray(), rejections, firstClash, lastClash);
        }
    }
}
