using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Interactions
{
    // Pure detection. Both callers supply the current tick's frozen intents and spatial graph.
    public static class DefenseContactDetector
    {
        public static IReadOnlyList<ActionPlanId> Detect(ConflictGraph graph, ActionType type,
            IReadOnlyList<PlanSpecEntry> plans, BattleDefinition definition)
        {
            var intents = new Dictionary<long, CombatIntent>();
            foreach (var node in graph.Nodes) intents[node.Intent.ActionPlanId.Value] = node.Intent;
            var ids = new SortedSet<long>();
            foreach (var contact in graph.Contacts)
            {
                if (contact.Key.Type != (type == ActionType.Dodge ? ContactType.AttackDodge : ContactType.AttackBlock)) continue;
                if (!(type == ActionType.Dodge ? contact.CoveredBefore : contact.CoveredAfter)) continue;
                bool firstAttack = intents.TryGetValue(contact.Key.FirstPlanId.Value, out CombatIntent intent);
                if (!firstAttack && !intents.TryGetValue(contact.Key.SecondPlanId.Value, out intent)) continue;
                var reactionId = firstAttack ? contact.Key.SecondPlanId : contact.Key.FirstPlanId;
                bool eligible = type == ActionType.Dodge ? (intent.Tags & AttackTagMask.Dodgeable) != 0 : HasBlockablePayload(intent);
                if (eligible && type == ActionType.Block)
                {
                    eligible = false;
                    foreach (var plan in plans)
                        if (plan.ActionPlanId == reactionId && definition.FindAction(new ActionSpecId(plan.ActionSpecId))?.Payload is BlockPayloadSpec payload)
                            eligible = (payload.EligibleTags & DefenseTagMask.Blockable) != 0;
                }
                if (eligible) ids.Add(reactionId.Value);
            }
            var result = new List<ActionPlanId>();
            foreach (long id in ids) result.Add(new ActionPlanId(id));
            return result.AsReadOnly();
        }

        public static bool HasBlockablePayload(CombatIntent intent)
        {
            if (intent.Momentum.MomentumUnits > 0 && (intent.Tags & AttackTagMask.Blockable) != 0) return true;
            foreach (var component in intent.DamageComponents)
                if ((component.Tags & DamageTagMask.Blockable) != 0) return true;
            return false;
        }
    }
}
