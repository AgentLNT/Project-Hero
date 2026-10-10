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
    public sealed class BattleViewRegistry : MonoBehaviour, IBattleViewConsumer, IBattleViewLifetimeConsumer
    {
        [SerializeField] private CombatUnitView[] _views = Array.Empty<CombatUnitView>();
        private readonly Dictionary<long, CombatUnitView> _byUnit = new Dictionary<long, CombatUnitView>();
        private readonly List<CombatUnitView> _createdViews = new List<CombatUnitView>();
        public CombatFeedbackMapper Feedback { get; } = new CombatFeedbackMapper();
        public bool TryGetView(long unitId, out CombatUnitView view) => _byUnit.TryGetValue(unitId, out view);
        public void Configure(CombatUnitView[] views) => _views = views ?? Array.Empty<CombatUnitView>();
        public void ReleaseVisuals()
        {
            foreach (var view in _views) if (view != null) view.Unbind();
            foreach (var view in _createdViews) if (view != null) { view.Unbind(); Destroy(view.gameObject); }
            _createdViews.Clear();
            _byUnit.Clear(); Feedback.Reset();
        }

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
                if (fact is UnitCreatedEvent created) CreateView(created, snapshot);
                foreach (var unit in snapshot.Units)
                    if (_byUnit.TryGetValue(unit.UnitId, out var view) && view != null) view.Consume(fact);
                Feedback.Consume(fact);
            }
            foreach (var unit in snapshot.Units)
                if (_byUnit.TryGetValue(unit.UnitId, out var view) && view != null)
                { view.ApplyMovementSnapshot(snapshot); view.ApplySnapshot(unit); }
        }

        private void CreateView(UnitCreatedEvent created, LogicSnapshot snapshot)
        {
            if (_byUnit.ContainsKey(created.UnitId.Value))
                throw new LogicDefinitionException("VIEW_CREATED_UNIT_DUPLICATE", created.SpawnId);
            UnitSnapshot unit = null; CombatUnitView template = null;
            foreach (var candidate in snapshot.Units)
                if (candidate.UnitId == created.UnitId.Value) unit = candidate;
            if (unit == null || unit.DefinitionId != created.DefinitionId.Value || unit.FactionId != created.FactionId.Value)
                throw new LogicDefinitionException("VIEW_CREATED_FACT_MISMATCH", created.SpawnId);
            foreach (var candidate in _byUnit.Values)
                if (candidate != null && candidate.LatestSnapshot.DefinitionId == unit.DefinitionId
                    && (template == null || candidate.UnitId.Value < template.UnitId.Value)) template = candidate;
            if (template == null) throw new LogicDefinitionException("VIEW_CREATED_TEMPLATE_MISSING", unit.DefinitionId);
            var view = Instantiate(template.gameObject, transform).GetComponent<CombatUnitView>();
            view.gameObject.name = "UnitView-" + unit.UnitId;
            view.BindCreated(unit); _createdViews.Add(view); _byUnit.Add(unit.UnitId, view);
        }
    }
}
