using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;

namespace ProjectHero.Logic.Snapshots
{
    public sealed record TimelineContactPreview(long PlanId, long OwnerUnitId, long TargetUnitId, long Tick, string Outcome, long TemporaryPlanKey = 0);
    public sealed record TimelineContactsPreview(string RejectionCode, IReadOnlyList<TimelineContactPreview> Contacts);

    public sealed partial class HudPreviewService
    {
        private ScheduleEditOperation[] _timelineOperations;
        private TimelineContactsPreview _timelineContacts;
        /// <summary>Exact Step events from the existing filtered Logic preview fork. View never infers clashes from alignment.</summary>
        public TimelineContactsPreview PreviewTimelineContacts(IReadOnlyList<ScheduleEditOperation> operations)
        {
            operations ??= Array.Empty<ScheduleEditOperation>();
            if (_timelineOperations != null && _timelineOperations.SequenceEqual(operations)) return _timelineContacts;
            _timelineOperations = operations.ToArray();
            using var world = _fork();
            var displayIds = new Dictionary<long, long>();
            var temporaryKeys = new Dictionary<long, long>();
            if (operations.Count > 0)
            {
                var preview = world.ScheduleEditor.Apply(operations, Decision.Tick + world.Definition.ReactionRules.CommandIngressLeadTicks,
                    world.ScheduleRevision, world.ScheduleRevision, preview: true, expectedWindowId: world.CurrentTurnWindow?.WindowId, issuer: Decision.ControllerId);
                if (!preview.Succeeded) return _timelineContacts = new TimelineContactsPreview(preview.RejectionCode, Array.Empty<TimelineContactPreview>());
                var applied = world.ScheduleEditor.Apply(operations, Decision.Tick + world.Definition.ReactionRules.CommandIngressLeadTicks,
                    world.ScheduleRevision, world.ScheduleRevision, expectedWindowId: world.CurrentTurnWindow?.WindowId, issuer: Decision.ControllerId);
                if (!applied.Succeeded) return _timelineContacts = new TimelineContactsPreview(applied.RejectionCode, Array.Empty<TimelineContactPreview>());
                // Keep the exact IDs used by ScheduleEditor preview, including candidate Add identities.
                for (int i = 0; i < operations.Count; i++)
                    if (operations[i] is AddOrdinaryPlanOperation add)
                    {
                        displayIds[applied.AppliedEdits[i].PlanId.Value] = preview.AppliedEdits[i].PlanId.Value;
                        temporaryKeys[applied.AppliedEdits[i].PlanId.Value] = add.TemporaryPlanKey;
                    }
            }
            long Id(long value) => displayIds.TryGetValue(value, out var id) ? id : value;
            var contacts = new Dictionary<(long, long, long), TimelineContactPreview>();
            var ranks = new Dictionary<(long, long, long), int>();
            void Contact(long plan, long owner, long target, long tick, string outcome, int priority)
            {
                var key = (Id(plan), target, tick);
                if (ranks.TryGetValue(key, out int previous) && previous > priority) return;
                ranks[key] = priority; contacts[key] = new TimelineContactPreview(Id(plan), owner, target, tick, outcome,
                    temporaryKeys.TryGetValue(plan, out long temporary) ? temporary : 0);
            }
            var attacks = world.ScheduleAuthority.Registry.ActivePlans.Where(p => p.ActionType == ActionType.Attack)
                .Select(p => ActionPlanSnapshot.From(p)).ToArray();
            long last = attacks.Select(p => p.ImpactTick).DefaultIfEmpty(Decision.Tick).Max();
            long end = Math.Min(last, (KnownHorizonTick ?? (Decision.Tick + 360)) - 1);
            // The horizon bounds normal gameplay; also cap unusually long authoring horizons for interactive queries.
            end = Math.Min(end, Decision.Tick + 3600);
            while (!world.IsEnded && world.Tick < end)
            {
                var step = Step(world);
                foreach (var fact in step.Events.EventsInSequenceOrder)
                {
                    if (fact is DamageChannelResolvedEvent damage) Contact(damage.AttackPlanId.Value, damage.AttackerUnitId.Value, damage.TargetUnitId.Value, fact.Tick, "命中", 1);
                    else if (fact is BlockResolvedEvent block && block.RewardsBlock) Contact(block.AttackPlanId.Value, block.AttackerUnitId.Value, block.DefenderUnitId.Value, fact.Tick, "被格挡", 4);
                    else if (fact is DodgeResolvedEvent dodge)
                        foreach (var attack in attacks.Where(p => dodge.InvalidatedAttackPlanIds.Any(id => id.Value == p.ActionPlanId)))
                            Contact(attack.ActionPlanId, attack.OwnerUnitId, dodge.DefenderUnitId.Value, fact.Tick, "被闪避", 4);
                    else if (fact is MoveContactResolvedEvent move) Contact(move.AttackPlanId.Value, move.AttackerUnitId.Value, move.MovingUnitId.Value, fact.Tick,
                        move.Outcome == MoveContactOutcome.Escaped ? "逃脱" : "拦截", 3);
                    else if (fact is ClashParticipantResolvedEvent clash)
                    {
                        foreach (var other in clash.Losses)
                            Contact(clash.ActionPlanId.Value, clash.OwnerUnitId.Value, other.OpponentUnitId.Value, fact.Tick, "拼刀", 5);
                    }
                }
            }
            return _timelineContacts = new TimelineContactsPreview(null, contacts.Values.OrderBy(c => c.Tick).ThenBy(c => c.PlanId).ThenBy(c => c.TargetUnitId).ToArray());
        }
    }
}
