using ProjectHero.Core.Compatibility.Runtime;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using UnityEngine;

namespace ProjectHero.Tests.PlayMode
{
    // Explicit test authoring input, used only in loaded test scenes. No runtime
    // state, health, resource, plan, or event is injected into an existing battle.
    public sealed class Task11SceneScenarioSource : MonoBehaviour, IBattleSimulationSource
    {
        public BattleSimulationSeed Seed;
        public string SourceName => "task11-scene-combination";
        public string LastConfigurationError => null;
        public BattleSimulationSeed BuildSeed() => Seed;

        public static BattleSimulationSeed Configure(BattleSimulationSeed original, bool aiReactionOnly)
        {
            var d = original.Definition;
            var attackPatterns = d.Actions.Where(a => a.Type == ActionType.Attack).Select(a => ((AttackPayloadSpec)a.Payload).Pattern).Where(p => p != null).ToArray();
            var uniform = new AttackPatternSpec(new AttackPatternId("attack.pattern.task11.scene.union"),
                Enumerable.Range(0, 12).Select(f => new DirectionalTriangleSet((GridDirection)f,
                    attackPatterns.SelectMany(p => p.Directions.Single(direction => (int)direction.Direction == f).Triangles)
                        .Distinct().OrderBy(t => t.X).ThenBy(t => t.Y).ThenBy(t => t.T).ToArray())).ToArray());
            var actions = d.Actions.Select(a => a.Type == ActionType.Attack
                ? a with { Timing = new AttackTimingSpec(60, aiReactionOnly ? 10 : 30),
                    Payload = ((AttackPayloadSpec)a.Payload) with { ForceMultiplier = 0.000001f, Pattern = uniform,
                        Tags = AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable } }
                : a.Type == ActionType.Dodge ? a with { Timing = new DodgeReactionTimingSpec(1, 30) }
                : a.Type == ActionType.Block ? a with { Timing = new BlockReactionTimingSpec(1, 30) } : a).ToArray();
            var sets = d.ActionSets;
            var units = d.Units;
            if (aiReactionOnly)
            {
                var e = d.FindEncounter(original.EncounterId);
                var slots = e.Controllers.Where(c => c.SourceKind == CommandSourceKind.Ai).SelectMany(c => c.ControlledSlots).ToArray();
                var ids = e.Slots.Where(s => slots.Contains(s.SlotId)).Select(s => s.DefinitionId).ToArray();
                var reactionSet = new ActionSetId("action_set.task11.ai_reactions_only");
                sets = sets.Concat(new[] { new ActionSetDefinition(reactionSet,
                    actions.Where(a => a.Type == ActionType.Block || a.Type == ActionType.Dodge).Select(a => a.ActionSpecId).ToArray()) })
                    .OrderBy(s => s.ActionSetId.Value, System.StringComparer.Ordinal).ToArray();
                units = units.Select(u => u with { ActionSpeed = d.Rules.ReferenceActionSpeed,
                    ActionSetId = ids.Contains(u.UnitDefinitionId) ? reactionSet : u.ActionSetId }).ToArray();
            }
            var patterns = d.AttackPatterns.Concat(new[] { uniform }).OrderBy(p => p.AttackPatternId.Value, System.StringComparer.Ordinal).ToArray();
            var hash = BattleDefinitionHash.Compute(d.RulesVersion, d.TicksPerSecond, 0, d.Rules, d.ConcurrentAction,
                d.ReactionRules, d.AdrenalineRules, d.FactionModel, d.DamageChannels, d.ImpactProfiles, units, actions,
                patterns, d.Volumes, d.MovementPatterns, sets, d.StatusEffects, d.Encounters, d.DefaultDynamicSpawnPolicy);
            d = new BattleDefinition(d.RulesVersion, d.TicksPerSecond, d.Rules, d.ConcurrentAction, d.ReactionRules,
                d.AdrenalineRules, d.FactionModel, d.DamageChannels, d.ImpactProfiles, units, actions, patterns,
                d.Volumes, d.MovementPatterns, sets, d.StatusEffects, d.Encounters, d.DefaultDynamicSpawnPolicy, hash);
            return new BattleSimulationSeed(d, original.EncounterId, original.RuntimeInputs,
                "Explicit scene fixture: minimum momentum, configured timing, real damage accrual, 180 Tick / hero; aiReactionOnly=" + aiReactionOnly);
        }
    }
}
