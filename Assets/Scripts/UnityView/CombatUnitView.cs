using System;
using ProjectHero.Logic;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Snapshots;
using UnityEngine;

namespace ProjectHero.UnityView
{
    /// <summary>Only slot metadata is authored. Runtime unit and faction IDs come from initialization.</summary>
    public sealed class CombatUnitView : MonoBehaviour
    {
        [SerializeField] private string _encounterSlotId;
        [SerializeField] private GridView _grid;
        [SerializeField, Min(0.001f)] private float _displacementSeconds = 0.12f;
        private Vector3 _from, _to;
        private float _elapsed;
        private bool _interpolating;
        public string EncounterSlotId => _encounterSlotId;
        public UnitId UnitId { get; private set; }
        public FactionId FactionId { get; private set; }
        public UnitSnapshot LatestSnapshot { get; private set; }
        public bool IsInterpolating => _interpolating;

        public void Configure(string slot, GridView grid)
        {
            if (UnitId.IsValid) throw new InvalidOperationException("View is already bound");
            _encounterSlotId = slot; _grid = grid;
        }

        public void Bind(BattleInitializationResult mapping, UnitSnapshot snapshot)
        {
            var slot = new EncounterSlotId(_encounterSlotId);
            if (_grid == null || !mapping.SlotToUnitId.TryGetValue(slot, out var unit)
                || !mapping.SlotToFaction.TryGetValue(slot, out var faction)
                || snapshot == null || snapshot.UnitId != unit.Value || snapshot.FactionId != faction.Value)
                throw new LogicDefinitionException("VIEW_SLOT_BINDING_INVALID", _encounterSlotId);
            UnitId = unit; FactionId = faction; LatestSnapshot = snapshot;
            _interpolating = false;
            transform.position = _grid.GridToWorld(new GridPoint(snapshot.X, snapshot.Y));
        }

        public void Consume(LogicEvent fact)
        {
            if (fact is ForcedDisplacementResolvedEvent displacement && displacement.TargetUnitId == UnitId)
            {
                _interpolating = displacement.AppliedSteps > 0;
                _elapsed = 0;
                _from = _grid.GridToWorld(displacement.From);
                _to = _grid.GridToWorld(displacement.To);
                transform.position = _from;
            }
            else if (fact is ActionPlanTerminatedEvent terminated && terminated.OwnerUnitId == UnitId.Value)
                _interpolating = false;
        }

        public void ApplySnapshot(UnitSnapshot snapshot)
        {
            if (snapshot == null || snapshot.UnitId != UnitId.Value || snapshot.FactionId != FactionId.Value)
                throw new LogicDefinitionException("VIEW_SNAPSHOT_BINDING_MISMATCH", _encounterSlotId);
            LatestSnapshot = snapshot;
            Vector3 committed = _grid.GridToWorld(new GridPoint(snapshot.X, snapshot.Y));
            if (!_interpolating || (_to - committed).sqrMagnitude > 0.000001f)
            { _interpolating = false; transform.position = committed; }
        }

        private void LateUpdate() => AdvanceVisual(Time.unscaledDeltaTime);
        public void AdvanceVisual(float seconds)
        {
            if (!_interpolating) return;
            _elapsed += Mathf.Max(0, seconds);
            float alpha = Mathf.Clamp01(_elapsed / _displacementSeconds);
            transform.position = Vector3.Lerp(_from, _to, alpha);
            if (alpha >= 1) _interpolating = false;
        }
    }
}
