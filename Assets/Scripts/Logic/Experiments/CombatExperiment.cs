using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Experiments
{
    public enum ExperimentScript { Unopposed, BlockWhenFunded, DodgeWhenFunded, Clash, EarlyInterrupt, LateInterrupt, SweepTwo, FocusTwo, Guard }

    public sealed record CombatExperimentScenario(string Id, string Attack = "quick", int Growth = 0,
        int TargetHealth = 24, int StabilityQ10 = 10 * 1024, ExperimentScript Script = ExperimentScript.Unopposed,
        bool ShortRange = false, bool Wall = false, int MaxTicks = 1800, ulong Seed = 31,
        int BluntRecoveryDelta = 0)
    {
        public const string ScenarioVersion = "combat_experiment.scenes.1";
        public const string ScriptVersion = "combat_experiment.fixed_scripts.1";
    }

    /// <summary>Independent, immutable inputs; shares production definitions, validation, hashing and simulation.</summary>
    public static class CombatExperimentDefinition
    {
        public static readonly EncounterDefinitionId Encounter = new EncounterDefinitionId("encounter.combat_experiment");
        public static ActionSpecId Action(string name) => new ActionSpecId("action.experiment." + name);
        public static ControllerId Controller(long unit) => new ControllerId("controller.experiment." + unit);
        public static BattleDefinition Build(CombatExperimentScenario scenario)
        {
            if (scenario == null || scenario.Growth < 0 || scenario.Growth > 4 || scenario.MaxTicks < 1
                || scenario.BluntRecoveryDelta < 0 || scenario.BluntRecoveryDelta > 300
                || !(new[] {24, 27, 30, 36}).Contains(scenario.TargetHealth)) throw new LogicDefinitionException(CombatValuesV05.Invalid);
            var volume = new VolumeSpec(new VolumeSpecId("volume.experiment.normal"), Directions(1, false));
            var pattern = new AttackPatternSpec(new AttackPatternId("pattern.experiment." + (scenario.ShortRange ? "short" : "probe")),
                Directions(scenario.ShortRange ? 7 : 17, true));
            var movement = new MovementPatternSpec(new MovementPatternId("movement.experiment"), volume.Directions);
            var actions = new List<ActionSpec>();
            AddAttack(actions, "quick", PhysicalRecipe.Slash, 1024, AttackGrowthAttribute.Agility, 30, pattern);
            AddAttack(actions, "heavy", PhysicalRecipe.Blunt, 2048, AttackGrowthAttribute.Strength, 90, pattern,
                recoveryDelta: scenario.BluntRecoveryDelta);
            AddAttack(actions, "sweep", PhysicalRecipe.Slash, 768, AttackGrowthAttribute.Agility, 60, pattern, true);
            // These three are equal-condition probes, including pierce. No formal thrust timing is implied.
            AddAttack(actions, "probe_pierce", PhysicalRecipe.Pierce, 1024, AttackGrowthAttribute.Agility, 30, pattern);
            AddAttack(actions, "probe_slash", PhysicalRecipe.Slash, 1024, AttackGrowthAttribute.Agility, 30, pattern);
            AddAttack(actions, "probe_blunt", PhysicalRecipe.Blunt, 1024, AttackGrowthAttribute.Agility, 30, pattern,
                recoveryDelta: scenario.BluntRecoveryDelta);
            actions.Add(new ActionSpec(Action("block"), ActionType.Block, new BlockReactionTimingSpec(60),
                new BlockPayloadSpec(DefenseTagMask.Blockable), CombatValuesV05.ResourceUnitsPerPoint));
            actions.Add(new ActionSpec(Action("dodge"), ActionType.Dodge, new DodgeReactionTimingSpec(30),
                new DodgePayloadSpec(4, movement), CombatValuesV05.ResourceUnitsPerPoint));
            actions.Add(new ActionSpec(Action("move"), ActionType.Move, new MoveTimingSpec(12, 1), new MovePayloadSpec(32, movement), 0));
            actions.Add(new ActionSpec(Action("guard"), ActionType.Guard, new GuardTimingSpec(60),
                new GuardPayloadSpec(new System.Collections.ObjectModel.ReadOnlyDictionary<DamageChannelId, int>(new Dictionary<DamageChannelId, int> {
                    [DamageChannels.PhysicalBlunt] = 512, [DamageChannels.PhysicalSlash] = 512, [DamageChannels.PhysicalPierce] = 512 }),
                    512, DefenseTagMask.Guardable | DefenseTagMask.Blockable), 0));
            actions.Sort((a,b) => StringComparer.Ordinal.Compare(a.ActionSpecId.Value,b.ActionSpecId.Value));
            foreach (var action in actions)
            {
                string error = ActionDefinitionValidation.ValidateTypePairing(action) ?? ActionDefinitionValidation.ValidateAdrenalineCost(action);
                if (action.Type == ActionType.Attack) error = error ?? ActionDefinitionValidation.ValidateAttack(action);
                if (error != null) throw new LogicDefinitionException(error);
            }
            if (!actions.Any(a => a.ActionSpecId == Action(scenario.Attack))) throw new LogicDefinitionException(CombatValuesV05.Invalid, "unknown sample");
            var set = new ActionSetDefinition(new ActionSetId("action_set.experiment"), Array.AsReadOnly(actions.Select(a=>a.ActionSpecId).ToArray()));
            var allies = new FactionId("faction.experiment.a"); var enemies = new FactionId("faction.experiment.b");
            var factions = new FactionModelDefinition(Array.AsReadOnly(new[] { new FactionDefinition(allies), new FactionDefinition(enemies) }),
                Array.AsReadOnly(new[] { new FactionRelationDefinition(allies, enemies, FactionDisposition.Hostile) }));
            int count = scenario.Script == ExperimentScript.FocusTwo ? 4 : scenario.Script == ExperimentScript.SweepTwo ? 3 : 2;
            var units = new List<UnitDefinition>(); var slots = new List<EncounterUnitSlot>(); var controllers = new List<ControllerBinding>();
            for (int i = 1; i <= count; i++)
            {
                bool attacker = i == 1 || (scenario.Script == ExperimentScript.FocusTwo && i == 2);
                int constitution = !attacker && scenario.TargetHealth == 36 ? 5 : 1;
                int equipment = attacker || scenario.TargetHealth == 36 ? 0 : scenario.TargetHealth - 24;
                var attributes = new CombatAttributesV05(attacker ? 1 + scenario.Growth : 1, attacker ? 1 + scenario.Growth : 1,
                    constitution, equipment, attacker ? (int?)null : scenario.StabilityQ10);
                var id = new UnitDefinitionId("unit.experiment." + i);
                units.Add(CombatValuesV05.Unit(id, attributes, set.ActionSetId, volume.VolumeSpecId));
                var slot = new EncounterSlotId("slot.experiment." + i);
                var position = i == 1 ? new GridPoint(0, 0) : i == 2 ? new GridPoint(6, 0)
                    : i == 3 ? new GridPoint(0, 6) : new GridPoint(6, 6);
                slots.Add(new EncounterUnitSlot(slot, id, attacker ? allies : enemies, position, GridDirection.East));
                controllers.Add(new ControllerBinding(Controller(i), CommandSourceKind.Player, Array.AsReadOnly(new[] { slot })));
            }
            var encounter = new EncounterDefinition(Encounter,
                new GridBoundaryDefinition(new GridPoint(-20,-20), new GridPoint(scenario.Wall ? 8 : 20,20)),
                slots.AsReadOnly(), controllers.AsReadOnly(),
                new VictoryDefinition(Array.AsReadOnly(new[]{allies}), Array.AsReadOnly(new[]{enemies}), "victory", "defeat", "draw"),
                new TurnSubmissionDefinition(180, slots[0].SlotId));
            var channels = new[] { DamageChannels.PhysicalBlunt, DamageChannels.PhysicalPierce, DamageChannels.PhysicalSlash }
                .OrderBy(c=>c.Value, StringComparer.Ordinal).Select(c=>new DamageChannelDefinition(c, DamageChannelCatalog.GetDefaultTags(c))).ToArray();
            var definition = new BattleDefinition(CombatValuesV05.Version, 60, BattleRules.FrozenV1,
                ConcurrentActionDefinition.FrozenV1, new ReactionRules(1,1), AdrenalineRules.CombatValuesV05Candidate, factions,
                Array.AsReadOnly(channels), Array.AsReadOnly(new[] { new ImpactProfileDefinition(ImpactProfiles.Blunt,100) }),
                units.AsReadOnly(), actions.AsReadOnly(), Array.AsReadOnly(new[]{pattern}), Array.AsReadOnly(new[]{volume}),
                Array.AsReadOnly(new[]{movement}), Array.AsReadOnly(new[]{set}), Array.Empty<StatusEffectSpec>(),
                Array.AsReadOnly(new[]{encounter}), null, "");
            string validation = CombatValuesV05.ValidateDefinition(definition);
            if (validation != null) throw new LogicDefinitionException(validation);
            return definition with { BattleDefinitionHashValue = Hash(definition) };
        }
        public static string Hash(BattleDefinition d) => BattleDefinitionHash.Compute(d.RulesVersion, d.TicksPerSecond, 0,
            d.Rules, d.ConcurrentAction, d.ReactionRules, d.AdrenalineRules, d.FactionModel, d.DamageChannels, d.ImpactProfiles,
            d.Units, d.Actions, d.AttackPatterns, d.Volumes, d.MovementPatterns, d.ActionSets, d.StatusEffects, d.Encounters, d.DefaultDynamicSpawnPolicy);
        private static void AddAttack(List<ActionSpec> actions, string id, PhysicalRecipe recipe, int strength,
            AttackGrowthAttribute growth, int windup, AttackPatternSpec pattern, bool area = false, int recoveryDelta = 0)
            => actions.Add(new ActionSpec(Action(id), ActionType.Attack,
                new AttackTimingSpec(windup, checked(30 + (recipe == PhysicalRecipe.Blunt ? recoveryDelta : 0))),
                new AttackPayloadSpec(Array.Empty<DamageComponentSpec>(), ImpactProfiles.Blunt, 1f,
                    area ? TargetPolicy.AllTargetsInArea : TargetPolicy.PrimaryTargetOnly, TargetRelationMask.Hostile,
                    0, pattern, AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable,
                    new AttackStrengthV05(recipe,strength,growth)),0));
        // Explicit synthetic geometry: symmetric triangle coverage isolates recipe/timing effects.
        // Short-range and wall variants measure loss of follow-up range separately.
        private static IReadOnlyList<DirectionalTriangleSet> Directions(int reach, bool area)
        {
            var triangles = new List<TrianglePoint>();
            for (int x = area ? -reach : 1; x <= reach; x++)
                for (int y = area ? -reach : 0; y <= (area ? reach : 0); y++)
                    foreach (int t in new[]{-1,1}) if (TrianglePoint.IsValid(x,y,t)) triangles.Add(new TrianglePoint(x,y,t));
            var points = Array.AsReadOnly(triangles.OrderBy(p=>p.X).ThenBy(p=>p.Y).ThenBy(p=>p.T).ToArray());
            return Array.AsReadOnly(Enumerable.Range(0,12).Select(d=>new DirectionalTriangleSet((GridDirection)d,points)).ToArray());
        }
    }

    public sealed record ExperimentDecision(long Tick, long UnitId, string Reason, string Action, string IngressRejection);
    public sealed record ExperimentTick(long Tick, IReadOnlyList<AdrenalineAccrualFacts> Income, DamageCommitReport Damage);
    public sealed record CombatExperimentResult(CombatExperimentScenario Scenario, BattleDefinition Definition, BattleReplay Replay,
        LogicSnapshot FinalSnapshot, string Outcome, IReadOnlyList<ExperimentDecision> Decisions, IReadOnlyList<ExperimentTick> Diagnostics,
        IReadOnlyList<LogicEvent> Events);

    public static class CombatExperimentRunner
    {
        public static CombatExperimentResult Run(CombatExperimentScenario scenario)
        {
            var definition = CombatExperimentDefinition.Build(scenario);
            var decisions = new List<ExperimentDecision>(); var diagnostics = new List<ExperimentTick>();
            var events = new List<LogicEvent>();
            using (var simulation = ProductionBattleComposition.Create(definition, CombatExperimentDefinition.Encounter, new BattleRuntimeInputs(scenario.Seed, 4)))
            {
                var recorder = new ReplayRecorder(simulation);
                var committed = new HashSet<string>(StringComparer.Ordinal);
                for (int tick = 0; tick < scenario.MaxTicks && !simulation.BattleEnd.IsEnded; tick++)
                {
                    if (tick > 0) SubmitScript(simulation, scenario, decisions, committed);
                    recorder.CaptureBeforeStep();
                    var result = simulation.Step(tick, simulation.CommandIngress.FreezeTick(tick));
                    recorder.RecordCommittedStep(result);
                    events.AddRange(result.Events.Events);
                    if (simulation.LastDamageCommitReport.Units.Count > 0 || simulation.LastAdrenalineAccrualFacts.Count > 0)
                        diagnostics.Add(new ExperimentTick(tick, simulation.LastAdrenalineAccrualFacts, simulation.LastDamageCommitReport));
                }
                string outcome = simulation.BattleEnd.IsEnded ? simulation.BattleEnd.ResultCode : "timeout";
                return new CombatExperimentResult(scenario, definition, recorder.BuildReplay(), simulation.CurrentSnapshot, outcome,
                    decisions.AsReadOnly(), diagnostics.AsReadOnly(), events.AsReadOnly());
            }
        }

        private static void SubmitScript(BattleSimulation sim, CombatExperimentScenario scenario,
            List<ExperimentDecision> decisions, HashSet<string> committed)
        {
            foreach (var binding in sim.Definition.FindEncounter(sim.EncounterId).Controllers)
            {
                var view = sim.DecisionSnapshotFor(binding.ControllerId);
                foreach (var owner in view.ControlledUnitIds)
                {
                    var unit = view.VisibleUnits.First(u=>u.UnitId == owner.Value);
                    if (!unit.IsAlive) continue;
                    var context = AiDecisionContextFactory.Build(view, binding.ControllerId, sim.AiReactionOpportunities,
                        sim.AiActionPlanLookup, sim.MovementPathCalculator);
                    bool focused = scenario.Script == ExperimentScript.FocusTwo;
                    bool attacking = owner.Value == 1 || (focused && owner.Value == 2);
                    if (!attacking && (scenario.Script == ExperimentScript.BlockWhenFunded || scenario.Script == ExperimentScript.DodgeWhenFunded))
                    {
                        var options = AiReactionCandidateBuilder.BuildReactionCandidatesOrdered(context, view.Tick);
                        var option = options.FirstOrDefault(o => o.ReactionKind == (scenario.Script == ExperimentScript.BlockWhenFunded
                            ? ReactionCommandKind.Block : ReactionCommandKind.Dodge)
                            && view.FindAction(o.ActionSpecId).AdrenalineCost <= unit.AvailableAdrenaline);
                        if (option != null)
                        {
                            var request = new CommandRequest(context.NextTick, new ReactionCommandScope(option.ReactionOpportunityId),
                                new ReactionCommandPayload(option.ReactionKind, option.ActionSpecId, option.SelectedDestination, option.StartTick));
                            Submit(sim, binding.ControllerId, owner, request, "fixed-response-to-published-opportunity", option.ReactionKind.ToString(), decisions);
                            continue;
                        }
                        foreach (var opportunity in context.Opportunities)
                        {
                            string reason = unit.AvailableAdrenaline < CombatValuesV05.ResourceUnitsPerPoint ? "no-resource"
                                : scenario.Script == ExperimentScript.DodgeWhenFunded
                                    && (opportunity.DodgeDestinations?.Candidates == null || opportunity.DodgeDestinations.Candidates.Count == 0)
                                    ? "no-safe-destination" : "no-legal-lane-or-timing";
                            string key="wait."+owner.Value+"."+opportunity.ReactionOpportunityId.Value+"."+reason;
                            if(committed.Add(key))decisions.Add(new ExperimentDecision(view.Tick,owner.Value,reason,"wait","not-submitted"));
                        }
                    }
                    if (!context.HasSubmissionWindow || context.OwnWindow.OwnerUnitId != owner.Value) continue;
                    if (scenario.Script == ExperimentScript.Clash || scenario.Script == ExperimentScript.EarlyInterrupt || scenario.Script == ExperimentScript.LateInterrupt
                        || scenario.Script == ExperimentScript.Guard || scenario.Script == ExperimentScript.FocusTwo)
                    {
                        string key = "commit." + owner.Value;
                        if (owner.Value > 2 || committed.Contains(key)) continue;
                        long start = owner.Value == 1 ? (scenario.Script == ExperimentScript.EarlyInterrupt ? 121 : scenario.Script == ExperimentScript.LateInterrupt ? 123 : 122) : 182;
                        if (context.NextTick > start) continue;
                        if (owner.Value == 2 && context.NextTick != 182) continue;
                        string action = owner.Value == 1 ? "heavy" : scenario.Script == ExperimentScript.Guard ? "guard" : "quick";
                        var request = AttackRequest(context, owner, action, start, focused ? 3 : owner.Value == 1 ? 2 : 1,
                            owner.Value == 1 || focused ? GridDirection.East : GridDirection.West);
                        Submit(sim,binding.ControllerId,owner,request,"fixed-cross-window-commitment",action,decisions);
                        committed.Add(key); continue;
                    }
                    if (!attacking)
                    {
                        if (scenario.Script == ExperimentScript.Guard && context.LaneTailStartTickOf(owner) <= context.NextTick)
                            Submit(sim,binding.ControllerId,owner,AttackRequest(context,owner,"guard",context.NextTick,1,GridDirection.East),"fixed-guard", "guard",decisions);
                        continue;
                    }
                    if (context.LaneTailStartTickOf(owner) > context.NextTick || context.NextTick >= context.OwnWindow.EndTick) continue;
                    long target = focused ? 3 : 2;
                    if (!view.VisibleUnits.Any(u=>u.UnitId == target && u.IsAlive)) continue;
                    Submit(sim, binding.ControllerId, owner, AttackRequest(context,owner,scenario.Attack,context.NextTick,target,GridDirection.East),
                        "fixed-repeat-when-lane-is-free",scenario.Attack,decisions);
                }
            }
        }
        private static CommandRequest AttackRequest(AiDecisionContext context, UnitId owner, string action, long start, long target, GridDirection facing)
            => new CommandRequest(context.NextTick, new ScheduleEditScope(context.Snapshot.ScheduleRevision,context.ExpectedWindowId),
                new ScheduleEditPayload(new ScheduleEditOperation[]{new AddOrdinaryPlanOperation(1,owner,CombatExperimentDefinition.Action(action),start,
                    PrimaryTargetUnitId:new UnitId(target),Facing:facing)}));
        private static void Submit(BattleSimulation sim, ControllerId controller, UnitId owner, CommandRequest request,
            string reason, string action, List<ExperimentDecision> decisions)
        {
            var rejected = sim.CommandIngress.FindEntry(controller).Submit(request);
            decisions.Add(new ExperimentDecision(sim.Tick,owner.Value,reason,action,rejected?.ReasonCode));
        }

        public static void VerifyReplay(CombatExperimentResult result)
        {
            using (var stream = new System.IO.MemoryStream())
            {
                ReplayFile.Save(stream,result.Replay); stream.Position = 0;
                var loaded = ReplayFile.Load(stream,result.Definition);
                using (var player = new ReplayPlayer(result.Definition))
                {
                    player.Load(loaded); player.Play();
                    while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                    if (player.Deviation != null || !player.IsComplete) throw new LogicDefinitionException("EXPERIMENT_REPLAY_DEVIATION",player.Deviation?.ToString());
                }
            }
        }
        public static IReadOnlyList<CombatExperimentScenario> FirstRound()
        {
            var cases = new List<CombatExperimentScenario>();
            foreach (string action in new[]{"quick","heavy","sweep"}) cases.Add(new CombatExperimentScenario("htk_"+action,action));
            foreach (string action in new[]{"probe_pierce","probe_slash","probe_blunt"})
                foreach (int growth in Enumerable.Range(0,5)) foreach (int health in new[]{24,27,30,36})
                    cases.Add(new CombatExperimentScenario(action+"_p"+growth+"_h"+health,action,growth,health));
            foreach (int stability in new[]{8192,10239,10240,10241,10752,15360})
                cases.Add(new CombatExperimentScenario("stability_"+stability,"heavy",StabilityQ10:stability,MaxTicks:300));
            foreach (var script in new[]{ExperimentScript.BlockWhenFunded,ExperimentScript.DodgeWhenFunded,ExperimentScript.Clash,
                ExperimentScript.EarlyInterrupt,ExperimentScript.LateInterrupt,ExperimentScript.SweepTwo,ExperimentScript.FocusTwo,ExperimentScript.Guard})
                cases.Add(new CombatExperimentScenario("script_"+script, script == ExperimentScript.SweepTwo ? "sweep" : "quick",Script:script,
                    ShortRange:script == ExperimentScript.DodgeWhenFunded,MaxTicks:800));
            cases.Add(new CombatExperimentScenario("knockback_open_short","heavy",ShortRange:true,MaxTicks:800));
            cases.Add(new CombatExperimentScenario("knockback_wall_short","heavy",ShortRange:true,Wall:true,MaxTicks:800));
            return cases.AsReadOnly();
        }
    }
}
