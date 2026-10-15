using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.UnityView
{
    /// <summary>Stable presentation order: faction group, authored slot order, then ordinal SpawnId. Never window order.</summary>
    public sealed class TrackLayoutModel
    {
        private readonly Dictionary<long, int> _slots = new Dictionary<long, int>();
        private readonly Dictionary<long, string> _spawns = new Dictionary<long, string>();
        public void Bind(BattleInitializationResult mapping)
        {
            _slots.Clear(); _spawns.Clear(); int ordinal = 0;
            foreach (var slot in mapping.SlotToUnitId.OrderBy(p => p.Key.Value, StringComparer.Ordinal)) _slots[slot.Value.Value] = ordinal++;
        }
        public void ObserveSpawn(long unit, string spawnId) => _spawns[unit] = spawnId;
        public void Consume(EventBatch events)
        { foreach (var created in events.Events.OfType<UnitCreatedEvent>()) ObserveSpawn(created.UnitId.Value, created.SpawnId); }
        public IReadOnlyList<UnitSnapshot> Order(DecisionSnapshot decision, Func<long, bool> include)
            => decision.VisibleUnits.Where(u => u.IsAlive && include(u.UnitId)).OrderBy(u => TrackSelectionModel.Group(decision, u.UnitId))
                .ThenBy(u => _spawns.ContainsKey(u.UnitId) ? 1 : 0)
                .ThenBy(u => _slots.TryGetValue(u.UnitId, out int slot) ? slot : int.MaxValue)
                .ThenBy(u => _spawns.TryGetValue(u.UnitId, out string spawn) ? spawn : "", StringComparer.Ordinal)
                .ThenBy(u => u.UnitId).ToArray();
    }
}
