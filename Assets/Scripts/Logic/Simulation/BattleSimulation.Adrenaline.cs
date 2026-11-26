using System.Collections.Generic;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Resources;

namespace ProjectHero.Logic.Simulation
{
    public sealed partial class BattleSimulation
    {
        private readonly List<int> _healthBeforeEffects = new List<int>();

        private void ClaimDirectDamageThresholds(long tick)
        {
            var report = LastDamageCommitReport;
            if (_stagedResolution == null || _stagedResolution.Failed || report.Tick != tick) return;

            foreach (var commit in report.Units)
            {
                if (commit.DamageQ10 <= 0 || !_unitsById.TryGetValue(commit.UnitId.Value, out var unit)
                    || !unit.Traits.IsCombatant) continue;
                int before = checked(unit.HealthQ10 + (int)commit.DamageQ10);
                int maximum = QuantizeHealth(_definition.FindUnit(unit.DefinitionId).InitialHealth);
                UnitId attacker = default;
                if (AdrenalineThresholds.Crossed(before, unit.HealthQ10, maximum, AdrenalineThresholds.DealtPercentages) != 0)
                {
                    foreach (var aggregate in _stagedResolution.Aggregations)
                    {
                        if (aggregate.TargetUnitId != commit.UnitId) continue;
                        var totals = new SortedDictionary<long, long>();
                        foreach (var share in Simulation.AdrenalineAccrualFactSource.AllocateActualContactDamage(aggregate, commit.DamageQ10))
                        {
                            if (share.DamageQ10 <= 0 || _factionResolver.Classify(share.Source, unit.UnitId) != UnitRelation.Hostile)
                                continue;
                            totals.TryGetValue(share.Source.Value, out long previous);
                            totals[share.Source.Value] = previous + share.DamageQ10;
                        }
                        long largest = 0;
                        foreach (var share in totals)
                        {
                            if (share.Value <= largest) continue;
                            largest = share.Value;
                            attacker = new UnitId(share.Key);
                        }
                        break;
                    }
                }
                ClaimAdrenalineThresholds(unit, before, attacker);
            }
        }

        private void ClaimAdrenalineThresholds(UnitRuntimeState unit, int before, UnitId attacker = default)
        {
            if (!unit.Traits.IsCombatant || unit.HealthQ10 >= before) return;
            int maximum = QuantizeHealth(_definition.FindUnit(unit.DefinitionId).InitialHealth);
            int received = _adrenaline.Find(unit.UnitId)?.ClaimDamageReceived(before, unit.HealthQ10, maximum) ?? 0;
            if (received > 0)
                _outbox.Emit(sequence => new AdrenalineThresholdRewardEvent(_eventTick, sequence, unit.UnitId, unit.UnitId, received));
            if (attacker.IsValid && _factionResolver.Classify(attacker, unit.UnitId) == UnitRelation.Hostile)
            {
                int dealt = _adrenaline.Find(attacker)?.ClaimDamageDealt(unit.UnitId, before, unit.HealthQ10, maximum) ?? 0;
                if (dealt > 0)
                    _outbox.Emit(sequence => new AdrenalineThresholdRewardEvent(_eventTick, sequence, attacker, unit.UnitId, dealt));
            }
        }
    }
}
