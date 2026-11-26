using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Authoring
{
    public static partial class BattleDefinitionBuilder
    {
        private static readonly FactionId EnvironmentFaction = new("environment");

        public static BattleDefinitionBuildResult WithAuthoredUnits(BattleDefinition source,
            EncounterDefinitionId encounterId, IReadOnlyList<BattleUnitPlacement> placements)
        {
            if (placements == null || placements.Count == 0)
                return BattleDefinitionBuildResult.Success(source, null);

            var errors = new DefinitionErrorCollector();
            var encounter = source.FindEncounter(encounterId);
            if (encounter == null)
            {
                errors.Add(DefinitionCodes.ENCOUNTER_NOT_FOUND, encounterId.Value);
                return BattleDefinitionBuildResult.Failed(errors, null);
            }

            var units = new SortedDictionary<string, UnitDefinition>(StringComparer.Ordinal);
            foreach (var unit in source.Units)
                units.Add(unit.UnitDefinitionId.Value, unit.ScalarControlResistanceUnits.HasValue || unit.CombatAttributes != null
                    ? unit : unit with { ScalarControlResistanceUnits = MomentumQuantizer.QuantizeControlResistanceUnits(unit.Mass, unit.MomentumSpeed) });
            var slots = new List<EncounterUnitSlot>(encounter.Slots);
            var controllers = (encounter.Controllers ?? Array.Empty<ControllerBinding>()).ToDictionary(binding => binding.ControllerId);
            var addedSlots = new HashSet<EncounterSlotId>();
            bool hasObjects = false;

            foreach (var placement in placements)
            {
                var unit = placement?.Unit;
                if (unit == null)
                {
                    errors.Add(DefinitionCodes.DEFINITION_REFERENCE_DANGLING, "unit profile", placement?.SlotId.Value);
                    continue;
                }
                string unitError = UnitDefinitionValidation.Validate(unit);
                if (!unit.ScalarControlResistanceUnits.HasValue && unit.CombatAttributes == null)
                    unitError ??= "UNIT_IMPACT_RESISTANCE_REQUIRED";
                if (unitError != null)
                {
                    errors.Add(unitError, unit.UnitDefinitionId.Value, placement.SlotId.Value);
                    continue;
                }
                if (units.TryGetValue(unit.UnitDefinitionId.Value, out var prior) && prior != unit)
                    errors.Add("UNIT_PROFILE_ID_DUPLICATE", unit.UnitDefinitionId.Value, placement.SlotId.Value);
                else
                    units[unit.UnitDefinitionId.Value] = unit;

                hasObjects |= !unit.Traits.IsCombatant;
                var faction = unit.Traits.IsCombatant ? placement.FactionId : EnvironmentFaction;
                slots.Add(new EncounterUnitSlot(placement.SlotId, unit.UnitDefinitionId, faction, placement.Position, placement.Facing));
                addedSlots.Add(placement.SlotId);

                if (string.IsNullOrEmpty(placement.ControllerId.Value))
                {
                    if (unit.Traits.Control != UnitControl.Passive)
                        errors.Add(UnitDefinitionCodes.UNIT_CONTROLLER_NOT_ALLOWED, "controller required", placement.SlotId.Value);
                    continue;
                }
                if (!controllers.TryGetValue(placement.ControllerId, out var binding)
                    || !unit.Traits.AllowsController(binding.SourceKind))
                {
                    errors.Add(UnitDefinitionCodes.UNIT_CONTROLLER_NOT_ALLOWED, placement.ControllerId.Value, placement.SlotId.Value);
                    continue;
                }
                controllers[binding.ControllerId] = binding with
                {
                    ControlledSlots = Array.AsReadOnly((binding.ControlledSlots ?? Array.Empty<EncounterSlotId>()).Append(placement.SlotId)
                        .OrderBy(id => id.Value, StringComparer.Ordinal).ToArray())
                };
            }

            var factions = source.FactionModel;
            if (hasObjects && !factions.ContainsFaction(EnvironmentFaction))
            {
                factions = new FactionModelDefinition(
                    BattleDefinitionAssembler.CanonicalizeFactions(factions.Factions.Append(new FactionDefinition(EnvironmentFaction))).AsReadOnly(),
                    BattleDefinitionAssembler.CanonicalizeRelations(factions.Relations.Concat(factions.Factions.Select(faction =>
                        new FactionRelationDefinition(FactionPairs.CanonicalFirst(faction.FactionId, EnvironmentFaction),
                            FactionPairs.CanonicalSecond(faction.FactionId, EnvironmentFaction), FactionDisposition.Neutral)))).AsReadOnly());
            }
            encounter = encounter with
            {
                Slots = BattleDefinitionAssembler.CanonicalizeSlots(slots).AsReadOnly(),
                Controllers = BattleDefinitionAssembler.CanonicalizeControllers(controllers.Values).AsReadOnly()
            };
            var volumes = source.Volumes.ToDictionary(volume => volume.VolumeSpecId.Value, StringComparer.Ordinal);
            BattleDefinitionValidator.ValidateFactionModel(factions.Factions, factions.Relations, errors);
            BattleDefinitionValidator.ValidateUnits(units,
                source.ActionSets.ToDictionary(set => set.ActionSetId.Value, StringComparer.Ordinal), errors, volumes);
            BattleDefinitionValidator.ValidateEncounter(encounter, factions, errors);
            if (errors.HasErrors) return BattleDefinitionBuildResult.Failed(errors, null);

            var grid = new LogicGrid(encounter.GridBoundary, encounter.StaticWalls);
            long unitId = 0;
            foreach (var slot in encounter.Slots)
            {
                var directions = volumes[units[slot.DefinitionId.Value].VolumeSpecId.Value].Directions;
                var id = new UnitId(++unitId);
                string registration = grid.RegisterUnit(id, slot.InitialPosition, slot.InitialFacing, directions);
                if (registration == null && addedSlots.Contains(slot.SlotId)
                    && grid.CellsOf(id).Any(cell => !encounter.GridBoundary.Contains(cell)))
                    registration = LogicGridCodes.LOGIC_GRID_OUT_OF_BOUNDS;
                AddIfError(errors, registration, slot.SlotId.Value);
            }
            if (errors.HasErrors) return BattleDefinitionBuildResult.Failed(errors, null);

            var actions = BuildAuthoredUnitActions(source, hasObjects, errors);
            if (errors.HasErrors) return BattleDefinitionBuildResult.Failed(errors, null);
            var orderedUnits = ToUnitList(units).AsReadOnly();
            var encounters = Array.AsReadOnly(source.Encounters.Select(value => value.EncounterId == encounterId ? encounter : value).ToArray());
            var definition = source with
            {
                Units = orderedUnits, Actions = actions, Encounters = encounters, FactionModel = factions,
                PriorReplayDefinition = source,
                BattleDefinitionHashValue = BattleDefinitionHash.Compute(source.RulesVersion, source.TicksPerSecond,
                    LegacyIdMigrationManifest.LibraryVolumes.Count, source.Rules, source.ConcurrentAction, source.ReactionRules,
                    source.AdrenalineRules, factions, source.DamageChannels, source.ImpactProfiles, orderedUnits,
                    actions, source.AttackPatterns, source.Volumes, source.MovementPatterns, source.ActionSets,
                    source.StatusEffects, encounters, source.DefaultDynamicSpawnPolicy)
            };
            return BattleDefinitionBuildResult.Success(definition, null);
        }

        private static IReadOnlyList<ActionSpec> BuildAuthoredUnitActions(BattleDefinition source,
            bool hasObjects, DefinitionErrorCollector errors)
        {
            // Import the old per-loadout impact once. Actions shared by newly authored units use this explicit value.
            var imported = new Dictionary<ActionSpecId, int>();
            foreach (var unit in source.Units)
            {
                var set = source.FindActionSet(unit.ActionSetId);
                if (set == null) continue;
                foreach (var id in set.ActionSpecIds)
                {
                    if (source.FindAction(id)?.Payload is not AttackPayloadSpec attack
                        || attack.Strength != null || attack.ScalarImpactUnits.HasValue) continue;
                    int value = MomentumQuantizer.QuantizeMomentumUnits(unit.Mass, unit.MomentumSpeed,
                        attack.ForceMultiplier, attack.ImpactProfileId);
                    if (imported.TryGetValue(id, out int existing) && existing != value)
                        errors.Add("UNIT_ATTACK_IMPACT_AMBIGUOUS", id.Value, unit.UnitDefinitionId.Value);
                    else imported[id] = value;
                }
            }

            return Array.AsReadOnly(source.Actions.Select(action =>
            {
                if (action.Payload is not AttackPayloadSpec attack) return action;
                return action with
                {
                    Payload = attack with
                    {
                        ScalarImpactUnits = imported.TryGetValue(action.ActionSpecId, out int value) ? value : attack.ScalarImpactUnits,
                        AllowedTargetRelations = hasObjects ? attack.AllowedTargetRelations | TargetRelationMask.Environment : attack.AllowedTargetRelations
                    }
                };
            }).ToArray());
        }
    }
}
