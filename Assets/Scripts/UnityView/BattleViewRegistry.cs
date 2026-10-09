using System;
using System.Collections.Generic;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Snapshots;
using UnityEngine;

namespace ProjectHero.UnityView
{
    public sealed class BattleViewRegistry : MonoBehaviour, IBattleViewConsumer
    {
        [SerializeField] private CombatUnitView[] _views = Array.Empty<CombatUnitView>();
        private readonly Dictionary<long, CombatUnitView> _byUnit = new Dictionary<long, CombatUnitView>();
        public CombatFeedbackMapper Feedback { get; } = new CombatFeedbackMapper();
        public void Configure(CombatUnitView[] views) => _views = views ?? Array.Empty<CombatUnitView>();

        public void Bind(BattleInitializationResult mapping, LogicSnapshot snapshot)
        {
            var pending = new Dictionary<long, CombatUnitView>();
            foreach (var view in _views)
            {
                if (view == null || string.IsNullOrEmpty(view.EncounterSlotId)
                    || !mapping.SlotToUnitId.TryGetValue(new EncounterSlotId(view.EncounterSlotId), out var unit))
                    throw new LogicDefinitionException("VIEW_SLOT_MISSING", view?.EncounterSlotId ?? "<null>");
                if (pending.ContainsKey(unit.Value)) throw new LogicDefinitionException("VIEW_SLOT_DUPLICATE", view.EncounterSlotId);
                pending.Add(unit.Value, view);
            }
            if (pending.Count != mapping.SlotToUnitId.Count) throw new LogicDefinitionException("VIEW_SLOT_SET_INCOMPLETE", "");
            // All identity checks pass before any view is bound.
            foreach (var unit in snapshot.Units) pending[unit.UnitId].Bind(mapping, unit);
            _byUnit.Clear();
            foreach (var item in pending) _byUnit.Add(item.Key, item.Value);
            Feedback.Reset();
        }

        public void Consume(EventBatch events, LogicSnapshot snapshot)
        {
            foreach (var fact in events.EventsInSequenceOrder)
            {
                foreach (var unit in snapshot.Units)
                    if (_byUnit.TryGetValue(unit.UnitId, out var view)) view.Consume(fact);
                Feedback.Consume(fact);
            }
            foreach (var unit in snapshot.Units)
                if (_byUnit.TryGetValue(unit.UnitId, out var view)) view.ApplySnapshot(unit);
        }
    }
}
