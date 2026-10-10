using System;
using System.Collections.Generic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Definitions
{
    /// <summary>Encounter-owned creation facts. No command or View can supply a faction.</summary>
    public sealed record DynamicUnitSpawnDefinition(
        string SpawnId, long Tick, EncounterSlotId SourceSlotId,
        UnitDefinitionId DefinitionId, GridPoint Position, GridDirection Facing,
        DynamicSpawnFactionPolicy FactionPolicy)
    {
        public static string ValidateAll(BattleDefinition definition, EncounterDefinition encounter)
        {
            if (encounter.DynamicSpawns == null) return null;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spawn in encounter.DynamicSpawns)
            {
                if (spawn == null || DefinitionIdValidation.ValidateFormat(spawn.SpawnId) != null
                    || !ids.Add(spawn.SpawnId) || spawn.Tick < 0)
                    return "DYNAMIC_SPAWN_DEFINITION_INVALID";
                bool sourceExists = false;
                foreach (var slot in encounter.Slots)
                    if (slot.SlotId == spawn.SourceSlotId) sourceExists = true;
                if (!sourceExists || definition.FindUnit(spawn.DefinitionId) == null)
                    return "DYNAMIC_SPAWN_REFERENCE_UNKNOWN";
                if (encounter.GridBoundary == null || !encounter.GridBoundary.Contains(spawn.Position)
                    || !GridDirectionInfo.IsValidIndex((int)spawn.Facing))
                    return "DYNAMIC_SPAWN_GEOMETRY_INVALID";
                var policy = spawn.FactionPolicy;
                if (policy == null || policy.Validate() != null
                    || (policy.InheritSourceFaction && !string.IsNullOrEmpty(policy.FixedFactionId.Value))
                    || (!policy.InheritSourceFaction && !definition.FactionModel.ContainsFaction(policy.FixedFactionId)))
                    return DynamicSpawnFactionPolicy.DYNAMIC_SPAWN_POLICY_INVALID;
                float health = definition.FindUnit(spawn.DefinitionId).InitialHealth;
                double healthQ10 = Math.Round(health * 1024d, MidpointRounding.AwayFromZero);
                if (float.IsNaN(health) || float.IsInfinity(health) || healthQ10 < 1 || healthQ10 > int.MaxValue)
                    return "DYNAMIC_SPAWN_HEALTH_INVALID";
            }
            return null;
        }

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("spawn.id", SpawnId);
            writer.Write("spawn.tick", Tick);
            writer.Write("spawn.source_slot", SourceSlotId.Value);
            writer.Write("spawn.definition", DefinitionId.Value);
            writer.Write("spawn.x", Position.X); writer.Write("spawn.y", Position.Y);
            writer.Write("spawn.facing", (int)Facing);
            FactionPolicy.WriteHashComponents(writer);
        }
    }
}
