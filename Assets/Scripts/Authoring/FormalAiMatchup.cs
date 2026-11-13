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
    public enum FormalAiLayout { Near = 0, Main = 1, Far = 2 }

    /// <summary>Formal-content matchup inputs. Changes controller preferences and spawn geometry only.</summary>
    public static class FormalAiMatchup
    {
        public static BattleDefinition Build(BattleDefinition source, AiPolicyKind hero, AiPolicyKind enemy,
            FormalAiLayout layout, bool mirror)
        {
            if (!Valid(hero) || !Valid(enemy) || !Enum.IsDefined(typeof(FormalAiLayout), layout))
                throw new LogicDefinitionException("FORMAL_MATCHUP_INPUT_INVALID");
            var d = FormalAiBaseline.Build(source, hero, AiExecutionKind.UtilityBehaviorTree, layout == FormalAiLayout.Near,
                false, new AiForecastSettings(360, true, true));
            var original = d.Encounters[0];
            if (original.Slots.Count != 2 || original.Controllers.Count != 2
                || !original.Slots.Any(s => s.SlotId.Value == "hero") || !original.Slots.Any(s => s.SlotId.Value == "enemy")
                || original.Controllers.Any(c => c.ControlledSlots.Count != 1))
                throw new LogicDefinitionException("FORMAL_MATCHUP_ROLES_INVALID");
            var slots = original.Slots.Select(s =>
            {
                bool isHero = s.SlotId.Value == "hero";
                var position = layout == FormalAiLayout.Far ? new GridPoint(isHero ? -12 : 12, isHero ? -8 : 8) : s.InitialPosition;
                var facing = layout == FormalAiLayout.Far ? (isHero ? GridDirection.East : GridDirection.West) : s.InitialFacing;
                return s with { InitialPosition = mirror ? new GridPoint(-position.X, -position.Y) : position,
                    InitialFacing = mirror ? (GridDirection)(((int)facing + 6) % 12) : facing };
            }).ToArray();
            var encounter = original with { Slots = Array.AsReadOnly(slots),
                Controllers = Array.AsReadOnly(original.Controllers.Select(c => c with {
                    AiPolicy = c.ControlledSlots[0].Value == "hero" ? hero : enemy }).ToArray()) };
            var errors = new DefinitionErrorCollector(); BattleDefinitionValidator.ValidateEncounter(encounter, d.FactionModel, errors);
            if (errors.HasErrors) throw new LogicDefinitionException("FORMAL_MATCHUP_ENCOUNTER_INVALID");
            d = d with { Encounters = Array.AsReadOnly(new[] { encounter }), BattleDefinitionHashValue = "" };
            return d with { BattleDefinitionHashValue = BattleDefinitionHash.Compute(d.RulesVersion, d.TicksPerSecond,
                LegacyIdMigrationManifest.LibraryVolumes.Count, d.Rules, d.ConcurrentAction, d.ReactionRules,
                d.AdrenalineRules, d.FactionModel, d.DamageChannels, d.ImpactProfiles, d.Units, d.Actions,
                d.AttackPatterns, d.Volumes, d.MovementPatterns, d.ActionSets, d.StatusEffects, d.Encounters, d.DefaultDynamicSpawnPolicy) };
        }
        static bool Valid(AiPolicyKind policy) => policy == AiPolicyKind.SustainedOffense
            || policy == AiPolicyKind.SeekInterrupt || policy == AiPolicyKind.CautiousDefense;
    }
}
