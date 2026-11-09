using System;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Combat
{
    public enum AttackGrowthAttribute { Strength = 0, Agility = 1 }
    public enum PhysicalRecipe { Pierce = 0, Slash = 1, Blunt = 2 }

    /// <summary>First calibration range. Equipment contributes only to the shared health pool.</summary>
    public sealed record CombatAttributesV05(int Strength = 1, int Agility = 1, int Constitution = 1,
        int EquipmentHealth = 0, int? ProbeStabilityQ10 = null)
    {
        public string Validate()
            => Strength < 1 || Strength > 5 || Agility < 1 || Agility > 5
                || Constitution < 1 || Constitution > 5 || EquipmentHealth < 0 || EquipmentHealth > 6
                || (ProbeStabilityQ10.HasValue && (ProbeStabilityQ10 <= 0 || ProbeStabilityQ10 > 100 * 1024))
                ? CombatValuesV05.Invalid : null;
        public int MaximumHealth => 24 + 3 * (Constitution - 1) + EquipmentHealth;
        public int StabilityQ10 => ProbeStabilityQ10 ?? (10 * 1024 + (Constitution - 1) * 512);
        public float MoveSpeed => 20f * (1f + (Agility - 1) / 32f);
        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("unit.v05.strength", Strength).Write("unit.v05.agility", Agility)
                .Write("unit.v05.constitution", Constitution).Write("unit.v05.equipment_health", EquipmentHealth)
                .Write("unit.v05.probe_stability_q10", ProbeStabilityQ10 ?? 0);
        }
    }

    /// <summary>Damage and momentum share this single strength source. No independent overrides.</summary>
    public sealed record AttackStrengthV05(PhysicalRecipe Recipe, int BaseStrengthQ10,
        AttackGrowthAttribute GrowthAttribute)
    {
        public string Validate()
            => !Enum.IsDefined(typeof(PhysicalRecipe), Recipe) || !Enum.IsDefined(typeof(AttackGrowthAttribute), GrowthAttribute)
                || BaseStrengthQ10 <= 0 || BaseStrengthQ10 > 64 * 1024 ? CombatValuesV05.Invalid : null;
        public void WriteHashComponents(CanonicalHashWriter writer)
            => writer.Write("action.v05.recipe", (int)Recipe).Write("action.v05.base_strength_q10", BaseStrengthQ10)
                .Write("action.v05.growth_attribute", (int)GrowthAttribute);
    }

    public static class CombatValuesV05
    {
        public const string Version = "combat_values.v05.candidate.1.clash-recovery.1";
        public const string Invalid = "COMBAT_VALUES_V05_INVALID";
        public const int MomentumUnitsPerPoint = 1024;
        // This scale makes both damage/12 and damage/8 exact for every HealthQ10 quantum.
        public const int ResourceUnitsPerPoint = 24 * 1024;
        public static int DamageCoefficient(PhysicalRecipe recipe) => recipe == PhysicalRecipe.Pierce ? 5
            : recipe == PhysicalRecipe.Slash ? 4 : 3;
        public static int MomentumCoefficient(PhysicalRecipe recipe) => recipe == PhysicalRecipe.Pierce ? 1
            : recipe == PhysicalRecipe.Slash ? 3 : 5;
        public static DamageChannelId Channel(PhysicalRecipe recipe) => recipe == PhysicalRecipe.Pierce
            ? DamageChannels.PhysicalPierce : recipe == PhysicalRecipe.Slash ? DamageChannels.PhysicalSlash : DamageChannels.PhysicalBlunt;
        public static int StrengthQ10(CombatAttributesV05 attributes, AttackStrengthV05 attack)
        {
            if (attributes == null || attack == null || attributes.Validate() != null || attack.Validate() != null)
                throw new LogicDefinitionException(Invalid);
            int growth = (attack.GrowthAttribute == AttackGrowthAttribute.Strength ? attributes.Strength : attributes.Agility) - 1;
            return checked((int)TickQuantization.RoundHalfUpDivide((long)attack.BaseStrengthQ10 * (8 + growth), 8));
        }
        public static int DamageQ10(CombatAttributesV05 attributes, AttackStrengthV05 attack)
            => checked(StrengthQ10(attributes, attack) * DamageCoefficient(attack.Recipe));
        public static int MomentumUnits(CombatAttributesV05 attributes, AttackStrengthV05 attack)
            => checked(StrengthQ10(attributes, attack) * MomentumCoefficient(attack.Recipe));
        public static DamageComponentSpec Damage(CombatAttributesV05 attributes, AttackStrengthV05 attack)
        {
            var channel = Channel(attack.Recipe);
            return new DamageComponentSpec(channel, DamageQ10(attributes, attack) / 1024f,
                DamageChannelCatalog.GetDefaultTags(channel));
        }
        public static UnitDefinition Unit(UnitDefinitionId id, CombatAttributesV05 attributes, ActionSetId actions, VolumeSpecId volume)
        {
            if (attributes == null || attributes.Validate() != null) throw new LogicDefinitionException(Invalid);
            return new UnitDefinition(id, 1f, 1f, 20f, attributes.MoveSpeed,
                new System.Collections.ObjectModel.ReadOnlyDictionary<DamageChannelId, int>(new System.Collections.Generic.Dictionary<DamageChannelId, int>()),
                attributes.MaximumHealth, actions, volume, attributes);
        }
        public static string ValidateDefinition(Definitions.BattleDefinition definition)
        {
            bool enabled = definition.AdrenalineRules?.UseCombatValuesV05 == true;
            if (enabled && (definition.RulesVersion != Version || definition.AdrenalineRules.Validate() != null)) return Invalid;
            foreach (var unit in definition.Units)
                if (enabled != (unit.CombatAttributes != null) || UnitDefinitionValidation.Validate(unit) != null) return Invalid;
            foreach (var action in definition.Actions)
            {
                if (action.Payload is AttackPayloadSpec attack && enabled != (attack.Strength != null)) return Invalid;
                if (enabled && ActionDefinitionValidation.ValidateActionSpec(action, definition.Rules, null, null) != null) return Invalid;
                if (enabled && (action.Type == ActionType.Block || action.Type == ActionType.Dodge)
                    && action.AdrenalineCost != ResourceUnitsPerPoint) return Invalid;
            }
            return null;
        }
        public static void WriteProtocol(CanonicalHashWriter writer)
        {
            writer.Write("combat_values.version", Version).Write("combat_values.momentum_scale", MomentumUnitsPerPoint)
                .Write("combat_values.health_scale", 1024).Write("combat_values.resource_scale", ResourceUnitsPerPoint)
                .Write("combat_values.recipes", "pierce:5/1;slash:4/3;blunt:3/5")
                .Write("combat_values.growth", "strength-q10-half-up-times-(8+p)/8")
                .Write("combat_values.control", "independent-stability-q10")
                .Write("combat_values.residual", "floor-source-damage-q10-times-allocation/original-momentum")
                .Write("combat_values.income", "actual-health-q10-dealt-times-2-received-times-3;stock-includes-reservations;overflow-discard")
                .Write("combat_values.damage_attribution", "proportional-actual-loss-floor-remainder-contact-key-v1");
        }
    }
}
