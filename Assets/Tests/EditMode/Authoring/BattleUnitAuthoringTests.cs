using System.Linq;
using NUnit.Framework;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
using UnityEditor;
using UnityEngine;

namespace ProjectHero.Authoring.Tests
{
    public sealed class BattleUnitAuthoringTests
    {
        [Test]
        public void ProfilesCanBeReusedAndTheirTraitsDriveTargetingAndControl()
        {
            var stats = new LegacyUnitStatInputs(10, 10, 10, 10, 0, 0, false);
            var built = BattleDefinitionBuilder.BuildMainBattleDefinition(new LegacyBattleUnitSource(stats, stats));
            Assert.That(built.Succeeded, Is.True, string.Join(", ", built.ErrorKeys()));
            var source = built.Definition;
            var encounter = source.Encounters.Single();
            var inputs = new BattleRuntimeInputs(23, 0);
            var profile = ScriptableObject.CreateInstance<BattleUnitProfile>();
            try
            {
                var first = new BattleUnitScenePlacement { SlotId = "wall.a", Profile = profile, Position = new Vector2Int(0, -8) };
                var second = new BattleUnitScenePlacement { SlotId = "wall.b", Profile = profile, Position = new Vector2Int(0, 8) };
                var placements = new[] { first.Read(), second.Read() };
                var authored = BattleDefinitionBuilder.WithAuthoredUnits(source, encounter.EncounterId, placements);
                Assert.That(authored.Succeeded, Is.True, string.Join(", ", authored.ErrorKeys()));
                Assert.That(authored.Definition.Units.Count, Is.EqualTo(source.Units.Count + 1));
                Assert.That(authored.Definition.BattleDefinitionHashValue, Is.Not.EqualTo(source.BattleDefinitionHashValue));
                var reordered = BattleDefinitionBuilder.WithAuthoredUnits(source, encounter.EncounterId, placements.Reverse().ToArray());
                Assert.That(reordered.Definition?.BattleDefinitionHashValue, Is.EqualTo(authored.Definition.BattleDefinitionHashValue));

                using var simulation = ProductionBattleComposition.Create(authored.Definition, encounter.EncounterId, inputs);
                var initial = BattleInitializer.BuildInitialState(authored.Definition, encounter.EncounterId, inputs);
                var hero = initial.SlotToUnitId[new EncounterSlotId(LegacyIdMigrationManifest.SlotHero)];
                var wall = initial.SlotToUnitId[new EncounterSlotId(first.SlotId)];
                Assert.That(initial.ControllerToUnitIds.Values.SelectMany(ids => ids).Contains(wall), Is.False);
                Assert.That(simulation.FactionResolver.Allows(TargetRelationMask.Environment, hero, wall), Is.True);
                Assert.That(simulation.LogicGrid.TryGetAnchor(wall, out _), Is.True);
                Assert.That(simulation.CurrentSnapshot.Units.Single(unit => unit.UnitId == wall.Value).HealthQ10, Is.EqualTo(5000 * 1024));

                first.Controller = BattleUnitController.EnemyAi;
                var invalid = BattleDefinitionBuilder.WithAuthoredUnits(source, encounter.EncounterId, new[] { first.Read() });
                Assert.That(invalid.ErrorCodes(), Does.Contain(UnitDefinitionCodes.UNIT_CONTROLLER_NOT_ALLOWED));

                first.Controller = BattleUnitController.None;
                profile.Kind = UnitKind.Object;
                profile.HitPoints = 12;
                var box = BattleDefinitionBuilder.WithAuthoredUnits(source, encounter.EncounterId, new[] { first.Read() });
                Assert.That(box.Succeeded, Is.True, string.Join(", ", box.ErrorKeys()));
                var boxState = BattleInitializer.BuildInitialState(box.Definition, encounter.EncounterId, inputs);
                var boxId = boxState.SlotToUnitId[new EncounterSlotId(first.SlotId)];
                var attack = box.Definition.Actions.Select(action => action.Payload).OfType<AttackPayloadSpec>().First();
                var legacyAttack = source.Actions.Select(action => action.Payload).OfType<AttackPayloadSpec>().First();
                Assert.That(legacyAttack.ScalarImpactUnits, Is.Null);
                Assert.That(attack.ScalarImpactUnits, Is.GreaterThan(0));
                Assert.That(boxState.FactionResolver.Allows(attack.AllowedTargetRelations, hero, boxId), Is.True);
                Assert.That(boxState.InitialUnits[(int)boxId.Value - 1].InitialHealth, Is.EqualTo(12f));

                profile.Kind = UnitKind.Combatant;
                profile.Control = UnitControl.AiOnly;
                first.Controller = BattleUnitController.EnemyAi;
                var guard = BattleDefinitionBuilder.WithAuthoredUnits(source, encounter.EncounterId, new[] { first.Read() });
                Assert.That(guard.Succeeded, Is.True, string.Join(", ", guard.ErrorKeys()));
                var aiId = new ControllerId(LegacyIdMigrationManifest.ControllerEnemyAi);
                var guardState = BattleInitializer.BuildInitialState(guard.Definition, encounter.EncounterId, inputs);
                var guardId = guardState.SlotToUnitId[new EncounterSlotId(first.SlotId)];
                Assert.That(guardState.ControllerToUnitIds[aiId], Does.Contain(guardId));
                using var guardSimulation = ProductionBattleComposition.Create(guard.Definition, encounter.EncounterId, inputs);
                var decision = guardSimulation.DecisionSnapshotFor(aiId);
                Assert.That(decision.ActionSetOf(guardId).Any(id => decision.FindAction(id).Type == ActionType.Move), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [TestCase("StoneWall", UnitKind.Wall, 5000f, 5000f)]
        [TestCase("WoodenCrate", UnitKind.Object, 12f, 40f)]
        public void StarterProfilesImportWithExpectedValues(string assetName, UnitKind kind, float hp, float resistance)
        {
            var profile = AssetDatabase.LoadAssetAtPath<BattleUnitProfile>("Assets/Settings/BattleUnits/" + assetName + ".asset");
            Assert.That(profile, Is.Not.Null);
            var definition = profile.BuildDefinition();
            Assert.That(UnitDefinitionValidation.Validate(definition), Is.Null);
            Assert.That(definition.Traits.Kind, Is.EqualTo(kind));
            Assert.That(definition.InitialHealth, Is.EqualTo(hp));
            Assert.That(definition.ScalarControlResistanceUnits, Is.EqualTo(MomentumQuantizer.QuantizeLegacyMomentumUnits(resistance)));
        }
    }
}
