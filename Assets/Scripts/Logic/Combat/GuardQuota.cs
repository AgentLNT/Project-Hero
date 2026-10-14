using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Combat
{
    public static class GuardQuota
    {
        public static GuardPayloadSpec EffectivePayload(GuardPayloadSpec payload, int remaining)
        {
            var channels = new Dictionary<DamageChannelId, int>();
            if (payload.DamageResistanceQ10 != null)
                foreach (var entry in payload.DamageResistanceQ10)
                    channels.Add(entry.Key, Math.Min(entry.Value, remaining));
            return payload with { DamageResistanceQ10 = channels,
                MomentumResistanceQ10 = Math.Min(payload.MomentumResistanceQ10, remaining) };
        }

        // A tick debits the largest ratio actually used by any eligible payload, once.
        public static int ContactDebit(GuardPayloadSpec effective, IReadOnlyList<DamageComponentSpec> components,
            AttackTagMask tags, int momentum)
        {
            if ((effective.EligibleTags & DefenseTagMask.Guardable) == 0) return 0;
            int debit = momentum > 0 && (tags & AttackTagMask.Blockable) != 0 ? effective.MomentumResistanceQ10 : 0;
            foreach (var component in components)
                if (component.RawAmount > 0 && (component.Tags & DamageTagMask.Guardable) != 0
                    && effective.DamageResistanceQ10.TryGetValue(component.ChannelId, out int ratio))
                    debit = Math.Max(debit, ratio);
            return debit;
        }
    }
}
