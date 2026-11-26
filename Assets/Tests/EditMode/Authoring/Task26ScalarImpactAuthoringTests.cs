using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Units;
using UnityEngine;
using Tempo = ProjectHero.Authoring.BattleDefinitionBuilder.MainBattleTempo;

namespace ProjectHero.Authoring.Tests
{
    public sealed class Task26ScalarImpactAuthoringTests
    {
        [TestCase(19, 2, 0)]
        [TestCase(20, 2, 30)]
        [TestCase(39, 2, 30)]
        [TestCase(40, 2, 60)]
        [TestCase(60, 2, 60)]
        [TestCase(60, 0, 90)]
        public void ScalarKnockdownCountsCompleteSegmentsAndPreservesTheRiseBoundary(int impact, int limit, int ticks)
        {
            var target = new UnitId(2);
            var key = new TargetContactKey(TargetContactType.DirectHit, new UnitId(1), new ActionPlanId(1), target, default);
            var contact = new TargetContact(key, GridDirection.East, impact, 0, null, null)
            { ControlEffects = AttackControlEffects.Knockdown };
            var aggregate = TargetAggregator.Aggregate(new TargetAggregationInput(target, null, 20, new[] { contact })
            { UseScalarImpact = true });
            Assert.That(aggregate.IsKnockedDown, Is.EqualTo(ticks > 0));
            if (!aggregate.IsKnockedDown) return;
            var state = new UnitStateMachine(target, new LogicEventOutbox(new LogicSequenceGenerator()));
            Assert.That(state.StartScalarKnockdown(impact, 20, new ScalarImpactRules(MaxKnockdownSegments: limit), 10).Applied, Is.True);
            Assert.That(state.StateEndTick, Is.EqualTo(10 + ticks));
            for (int elapsed = 1; elapsed < ticks; elapsed++)
            {
                Assert.That(state.Advance(10 + elapsed), Is.False);
                Assert.That(state.Knockdown.RemainingImpactUnits, Is.EqualTo(impact - elapsed / 30 * 20));
            }
            Assert.That(state.Advance(10 + ticks), Is.True);
            Assert.That(state.Knockdown, Is.Null);
            Assert.That(state.CurrentState, Is.EqualTo(UnitState.Idle));
        }

        [Test]
        public void WeaponLoadoutPreservesHistoricalInputsAndInheritsOneGrowthSource()
        {
            var stats = new LegacyUnitStatInputs(10, 10, 10, 10, 0, 0, false);
            var source = new LegacyBattleUnitSource(stats, stats);
            var rules = new ScalarImpactRules();
            var walls = Array.Empty<GridPoint>();
            var baseline = BattleDefinitionBuilder.BuildScalarImpactBattleDefinition(source, walls, Tempo.Action24Move20, rules);
            Assert.That(baseline.Succeeded, Is.True, string.Join(", ", baseline.ErrorCodes()));
            var definition = baseline.Definition;
            Assert.That(definition.Units.All(unit => unit.InitialHealth == 50 && unit.ActionSpeed == 24 && unit.MoveSpeed == 20), Is.True);
            Assert.That(definition.PriorReplayDefinition.PriorReplayDefinition.Units.All(unit => unit.InitialHealth == 200), Is.True);
            var heroSetId = new ActionSetId(LegacyIdMigrationManifest.ActionSetRadius1);
            var weapon = ScriptableObject.CreateInstance<WeaponBattleProfile>();
            try
            {
                weapon.GrowthAttribute = AttackGrowthAttribute.Strength;
                weapon.Techniques = definition.FindActionSet(heroSetId).ActionSpecIds
                    .Where(id => definition.FindAction(id).Type == ActionType.Attack)
                    .Select(id => new WeaponBattleProfile.Technique { ActionId = id.Value }).ToArray();
                var equivalent = BattleDefinitionBuilder.BuildScalarImpactBattleDefinition(source, walls, Tempo.Action24Move20,
                    rules, heroWeapon: weapon);
                Assert.That(equivalent.Definition?.BattleDefinitionHashValue, Is.EqualTo(definition.BattleDefinitionHashValue));

                weapon.Techniques = new[]
                {
                    new WeaponBattleProfile.Technique { ActionId = LegacyIdMigrationManifest.ActionQuickSlashR1,
                        Recipe = PhysicalRecipe.Slash, BaseStrengthQ10 = 1024 },
                    new WeaponBattleProfile.Technique { ActionId = LegacyIdMigrationManifest.ActionHeavySmashR1,
                        Recipe = PhysicalRecipe.Blunt, BaseStrengthQ10 = 1024,
                        Controls = AttackControlEffects.Displacement, DisplacementDirectionOffset = 2 }
                };
                var grown = BattleDefinitionBuilder.BuildScalarImpactBattleDefinition(source, walls, Tempo.Action24Move20,
                    rules, heroWeapon: weapon, growth: new CombatAttributesV05(Strength: 5, Agility: 1));
                Assert.That(grown.Succeeded, Is.True, string.Join(", ", grown.ErrorCodes()));
                var loadout = grown.Definition.FindActionSet(heroSetId);
                Assert.That(loadout.ActionSpecIds.Count(id => grown.Definition.FindAction(id).Type == ActionType.Attack), Is.EqualTo(2));
                var slash = (AttackPayloadSpec)grown.Definition.FindAction(new ActionSpecId(weapon.Techniques[0].ActionId)).Payload;
                var smash = (AttackPayloadSpec)grown.Definition.FindAction(new ActionSpecId(weapon.Techniques[1].ActionId)).Payload;
                Assert.That(slash.DamageComponents.Single().RawAmount, Is.EqualTo(6));
                Assert.That(slash.ScalarImpactUnits, Is.EqualTo(450));
                Assert.That(smash.DamageComponents.Single().RawAmount, Is.EqualTo(4.5));
                Assert.That(smash.ScalarImpactUnits, Is.EqualTo(750));
                Assert.That(smash.MomentumDirectionOffsetSteps, Is.EqualTo(2));
                Assert.That(grown.Definition.BattleDefinitionHashValue, Is.Not.EqualTo(definition.BattleDefinitionHashValue));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(weapon);
            }
        }
    }
}
