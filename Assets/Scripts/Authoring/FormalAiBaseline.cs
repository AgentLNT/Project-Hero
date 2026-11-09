using System;
using System.Linq;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Logic;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;

namespace ProjectHero.Authoring
{
    /// <summary>Explicit test variation of a formal definition. Never constructs replacement actions or unit stats.</summary>
    public static class FormalAiBaseline
    {
        public const string Version = "formal-content-ai-baseline-v1";
        public static BattleDefinition Build(BattleDefinition source, AiPolicyKind policy, AiExecutionKind execution,
            bool near, bool mirror, AiForecastSettings forecast = null)
        {
            if (source == null || source.AdrenalineRules.UseCombatValuesV05 || source.Encounters.Count != 1)
                throw new LogicDefinitionException("FORMAL_BASELINE_SOURCE_INVALID");
            var encounter = source.Encounters[0];
            var slots = encounter.Slots.Select(s =>
            {
                bool hero = s.SlotId.Value == "hero";
                var point = near ? new GridPoint(hero ? -4 : 4, 0) : s.InitialPosition;
                return s with { InitialPosition = mirror ? new GridPoint(-point.X, -point.Y) : point,
                    InitialFacing = near ? (hero != mirror ? GridDirection.East : GridDirection.West)
                        : mirror ? (GridDirection)(((int)s.InitialFacing + 6) % 12) : s.InitialFacing };
            }).ToArray();
            encounter = encounter with { Slots = Array.AsReadOnly(slots),
                Controllers = Array.AsReadOnly(encounter.Controllers.Select(c => c with {
                    SourceKind = CommandSourceKind.Ai, AiPolicy = policy, AiExecution = execution, AiForecast = forecast }).ToArray()) };
            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateEncounter(encounter, source.FactionModel, errors);
            if (errors.HasErrors) throw new LogicDefinitionException("FORMAL_BASELINE_ENCOUNTER_INVALID");
            var d = source with { Encounters = Array.AsReadOnly(new[] { encounter }), BattleDefinitionHashValue = "" };
            return d with { BattleDefinitionHashValue = BattleDefinitionHash.Compute(d.RulesVersion, d.TicksPerSecond,
                LegacyIdMigrationManifest.LibraryVolumes.Count, d.Rules, d.ConcurrentAction, d.ReactionRules,
                d.AdrenalineRules, d.FactionModel, d.DamageChannels, d.ImpactProfiles, d.Units, d.Actions,
                d.AttackPatterns, d.Volumes, d.MovementPatterns, d.ActionSets, d.StatusEffects, d.Encounters, d.DefaultDynamicSpawnPolicy) };
        }
    }
}
