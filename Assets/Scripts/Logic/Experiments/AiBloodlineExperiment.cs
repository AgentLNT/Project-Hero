using System;
using System.Collections.Generic;
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

namespace ProjectHero.Logic.Experiments
{
    public sealed record AiBloodlineInput(int Health = 18, int FundingDamage = 8, int ThreatDamage = 4,
        int ThreatStart = 361, ulong Seed = 31, AiPolicyKind Policy = AiPolicyKind.CautiousDefense);

    /// <summary>Different bloodlines with identical funding and threat. Setup damage is before the
    /// defender's first reset; the common funding hit is after it. No runtime health/resource writes.</summary>
    public static class AiBloodlineExperiment
    {
        public static AiPolicyExperimentResult Run(AiBloodlineInput input)
        {
            var d = CombatExperimentDefinition.Build(new CombatExperimentScenario("bloodline", TargetHealth: 36));
            var original = d.Encounters[0];
            var quick = d.Actions.Single(a => a.ActionSpecId == CombatExperimentDefinition.Action("quick"));
            var pre = Attack("prehit", 1, 42 - input.Health - input.FundingDamage, false);
            var fund = Attack("fund", 160, input.FundingDamage, false);
            var threat = Attack("threat", 20, input.ThreatDamage, true);
            var defenderSet = new ActionSetDefinition(new ActionSetId("action_set.bloodline.defender"),
                new[] { CombatExperimentDefinition.Action("block"), CombatExperimentDefinition.Action("dodge") });
            var attackerSet = new ActionSetDefinition(new ActionSetId("action_set.bloodline.attacker"), new[] { fund.ActionSpecId, pre.ActionSpecId, threat.ActionSpecId });
            var units = d.Units.Select(u => CombatValuesV05.Unit(u.UnitDefinitionId,
                u.CombatAttributes with { Constitution = 5, EquipmentHealth = 6, ProbeStabilityQ10 = 100 * 1024 },
                u.UnitDefinitionId == original.Slots[0].DefinitionId ? defenderSet.ActionSetId : attackerSet.ActionSetId, u.VolumeSpecId)).ToArray();
            var encounter = original with { Controllers = original.Controllers.Select(c => c.ControllerId == CombatExperimentDefinition.Controller(1)
                ? c with { SourceKind = CommandSourceKind.Ai, AiPolicy = input.Policy, AiForecast = new AiForecastSettings(360, true, false,
                    new AiSamplingSettings(SignedDamage: true, HealthDefense: true)) } : c).ToArray() };
            d = d with { Units = units, Actions = d.Actions.Concat(new[] { pre, fund, threat }).OrderBy(a => a.ActionSpecId.Value, StringComparer.Ordinal).ToArray(),
                ActionSets = d.ActionSets.Concat(new[] { defenderSet, attackerSet }).OrderBy(a => a.ActionSetId.Value, StringComparer.Ordinal).ToArray(),
                Encounters = new[] { encounter }, BattleDefinitionHashValue = "" };
            string error = CombatValuesV05.ValidateDefinition(d); if (error != null) throw new LogicDefinitionException(error);
            d = d with { BattleDefinitionHashValue = CombatExperimentDefinition.Hash(d) };
            var runtime = new BattleRuntimeInputs(input.Seed, 4); var comp = new ProductionBattleComposition(d, CombatExperimentDefinition.Encounter, runtime);
            using var sim = BattleSimulation.Create(d, CombatExperimentDefinition.Encounter, runtime, comp.Assembly); comp.Attach(sim);
            var recorder = new ReplayRecorder(sim); var events = new List<LogicEvent>();
            for (int tick = 0; tick < 640 && !sim.IsEnded; tick++)
            {
                if (tick == 1) Submit(new[] { Add(1, pre, 1), Add(2, fund, 33) });
                if (tick == input.ThreatStart) Submit(new[] { Add(3, threat, input.ThreatStart) });
                recorder.CaptureBeforeStep(); var step = sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
                recorder.RecordCommittedStep(step); events.AddRange(step.Events.Events);
            }
            var result = new AiPolicyExperimentResult(new AiPolicyExperimentInput(input.Policy, AiPolicySituation.FundedReaction, input.Seed),
                d, comp.AiControllers.Single().Decisions.ToArray(), events.AsReadOnly(), recorder.BuildReplay(), sim.CurrentSnapshot.Units);
            AiPolicyExperiment.VerifyReplay(result); return result;

            ActionSpec Attack(string id, int windup, int damage, bool reactable) => quick with {
                ActionSpecId = new ActionSpecId("action.bloodline." + id), Timing = new AttackTimingSpec(windup, 30),
                Payload = ((AttackPayloadSpec)quick.Payload) with { Tags = reactable
                    ? AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable : AttackTagMask.None,
                    Strength = new AttackStrengthV05(PhysicalRecipe.Slash, damage * 256, AttackGrowthAttribute.Agility) } };
            ScheduleEditOperation Add(long key, ActionSpec action, long start) => new AddOrdinaryPlanOperation(key, new UnitId(2), action.ActionSpecId,
                start, PrimaryTargetUnitId: new UnitId(1), Facing: GridDirection.West);
            void Submit(IReadOnlyList<ScheduleEditOperation> operations)
            {
                var rejected = sim.CommandIngress.FindEntry(CombatExperimentDefinition.Controller(2)).SubmitAtDefaultTick(
                    new ScheduleEditScope(sim.ScheduleRevision, sim.CurrentTurnWindow.WindowId), new ScheduleEditPayload(operations));
                if (rejected != null) throw new InvalidOperationException(rejected.ToString());
            }
        }
    }
}
