using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Experiments
{
    public enum AiPolicySituation { OpenAttack, VisibleThreat, Approach, GuardOnly, FundedReaction, UnfundedReaction, AlignedClash, EarlyInterrupt }
    public sealed record AiPolicyExperimentInput(AiPolicyKind Policy, AiPolicySituation Situation,
        ulong Seed = 31, bool SwapSpawns = false, int MaxTicks = 160);
    public sealed record AiPolicyExperimentResult(AiPolicyExperimentInput Input, BattleDefinition Definition,
        IReadOnlyList<AiDecision> Decisions, IReadOnlyList<LogicEvent> Events, BattleReplay Replay, IReadOnlyList<UnitSnapshot> FinalUnits);

    /// <summary>Stage 3 mechanism probes. Registered AI regenerates all its inputs during native replay.</summary>
    public static class AiPolicyExperiment
    {
        public const string Version = "ai_policy_mechanisms.1";
        public static BattleDefinition Build(AiPolicyExperimentInput input)
        {
            var d = CombatExperimentDefinition.Build(new CombatExperimentScenario("ai_policy", ShortRange: input.Situation == AiPolicySituation.Approach));
            var encounter = d.Encounters[0];
            var slots = encounter.Slots.ToArray();
            if (input.Situation == AiPolicySituation.Approach)
                slots[1] = slots[1] with { InitialPosition = new GridPoint(14, 0) };
            if (input.SwapSpawns)
            {
                var position = slots[0].InitialPosition;
                slots[0] = slots[0] with { InitialPosition = slots[1].InitialPosition };
                slots[1] = slots[1] with { InitialPosition = position };
            }
            var controllers = encounter.Controllers.Select(c => c.ControllerId == CombatExperimentDefinition.Controller(1)
                ? c with { SourceKind = CommandSourceKind.Ai, AiPolicy = input.Policy } : c).ToArray();
            var permitted = input.Situation == AiPolicySituation.GuardOnly ? new[] { "guard" }
                : input.Situation == AiPolicySituation.FundedReaction || input.Situation == AiPolicySituation.UnfundedReaction ? new[] { "block", "dodge" }
                : new[] { "quick", "probe_blunt", "heavy", "guard", "move", "block", "dodge" };
            var ownSet = d.ActionSets[0] with { ActionSetId = new ActionSetId("action_set.experiment.ai"),
                ActionSpecIds = Array.AsReadOnly(permitted.Select(CombatExperimentDefinition.Action).OrderBy(a => a.Value, StringComparer.Ordinal).ToArray()) };
            var units = d.Units.Select(u => u.UnitDefinitionId == slots[0].DefinitionId ? u with { ActionSetId = ownSet.ActionSetId }
                : input.Situation == AiPolicySituation.EarlyInterrupt ? CombatValuesV05.Unit(u.UnitDefinitionId,
                    u.CombatAttributes with { ProbeStabilityQ10 = 4 * 1024 }, u.ActionSetId, u.VolumeSpecId) : u).ToArray();
            var actions = d.Actions.Select(a => a.ActionSpecId == CombatExperimentDefinition.Action("heavy")
                ? a with { Timing = new AttackTimingSpec(input.Situation == AiPolicySituation.FundedReaction ? 67
                    : input.Situation == AiPolicySituation.AlignedClash ? 139 : 99, 30) }
                : input.Situation == AiPolicySituation.FundedReaction && a.ActionSpecId == CombatExperimentDefinition.Action("quick")
                    ? a with { Timing = new AttackTimingSpec(1, 30), Payload = ((AttackPayloadSpec)a.Payload) with {
                        Strength = new AttackStrengthV05(PhysicalRecipe.Slash, 2048, AttackGrowthAttribute.Agility) } } : a).ToArray();
            encounter = encounter with { Slots = Array.AsReadOnly(slots), Controllers = Array.AsReadOnly(controllers),
                TurnSubmission = new TurnSubmissionDefinition(60, slots[0].SlotId) };
            d = d with { Units = Array.AsReadOnly(units), Actions = Array.AsReadOnly(actions),
                ActionSets = Array.AsReadOnly(d.ActionSets.Concat(new[] { ownSet }).OrderBy(s => s.ActionSetId.Value, StringComparer.Ordinal).ToArray()),
                Encounters = Array.AsReadOnly(new[] { encounter }), BattleDefinitionHashValue = "" };
            var error = CombatValuesV05.ValidateDefinition(d);
            if (error != null) throw new LogicDefinitionException(error);
            return d with { BattleDefinitionHashValue = CombatExperimentDefinition.Hash(d) };
        }

        public static AiPolicyExperimentResult Run(AiPolicyExperimentInput input)
        {
            var d = Build(input); var runtime = new BattleRuntimeInputs(input.Seed, 4);
            var composition = new ProductionBattleComposition(d, CombatExperimentDefinition.Encounter, runtime);
            using var sim = BattleSimulation.Create(d, CombatExperimentDefinition.Encounter, runtime, composition.Assembly);
            composition.Attach(sim);
            var recorder = new ReplayRecorder(sim); var events = new List<LogicEvent>();
            for (int tick = 0; tick < input.MaxTicks && !sim.IsEnded; tick++)
            {
                if (tick == 1 && input.Situation != AiPolicySituation.OpenAttack && input.Situation != AiPolicySituation.Approach)
                {
                    var operations = new List<ScheduleEditOperation>();
                    bool funded = input.Situation == AiPolicySituation.FundedReaction;
                    if (funded) operations.Add(Attack(1, "quick", 1));
                    operations.Add(Attack(2, "heavy", funded ? 33 : 1));
                    var error = sim.CommandIngress.FindEntry(CombatExperimentDefinition.Controller(2)).SubmitAtDefaultTick(
                        new ScheduleEditScope(sim.ScheduleRevision, sim.CurrentTurnWindow.WindowId), new ScheduleEditPayload(operations.AsReadOnly()));
                    if (error != null) throw new InvalidOperationException(error.ToString());
                }
                recorder.CaptureBeforeStep();
                var result = sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                recorder.RecordCommittedStep(result); events.AddRange(result.Events.Events);
            }
            return new AiPolicyExperimentResult(input, d, composition.AiControllers.Single().Decisions.ToArray(), events.AsReadOnly(),
                recorder.BuildReplay(), sim.CurrentSnapshot.Units);

            AddOrdinaryPlanOperation Attack(long key, string spec, long start) => new AddOrdinaryPlanOperation(key, new UnitId(2),
                CombatExperimentDefinition.Action(spec), start, PrimaryTargetUnitId: new UnitId(1),
                Facing: input.SwapSpawns ? GridDirection.East : GridDirection.West);
        }

        public static void VerifyReplay(AiPolicyExperimentResult result)
        {
            using var stream = new MemoryStream(); ReplayFile.Save(stream, result.Replay); stream.Position = 0;
            using var player = new ReplayPlayer(result.Definition);
            player.Load(ReplayFile.Load(stream, result.Definition)); player.Play();
            while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
            if (player.Deviation != null || !player.IsComplete) throw new InvalidOperationException("AI_REPLAY_DEVIATION: " + player.Deviation);
        }
    }
}
