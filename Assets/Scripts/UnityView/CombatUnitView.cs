using System;
using System.Collections.Generic;
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
        [SerializeField] private float _heightOffset;
        [SerializeField] private Transform _bodyVisual;
        [SerializeField] private Animator _animator;
        private readonly HashSet<string> _animationTriggers = new HashSet<string>(StringComparer.Ordinal);
        private Vector3 _bodyScale;
        private Vector3 _originalPosition;
        private Quaternion _originalRotation;
        private bool _originalBodyActive;
        private float _bounceRemaining;
        private Vector3 _from, _to;
        private float _elapsed;
        private bool _interpolating;
        private long _movementPlanId, _battleTick, _segmentStart, _segmentEnd;
        private float _visualFraction;
        private bool _forced;
        public string EncounterSlotId => _encounterSlotId;
        public UnitId UnitId { get; private set; }
        public FactionId FactionId { get; private set; }
        public UnitSnapshot LatestSnapshot { get; private set; }
        public bool IsInterpolating => _interpolating;

        public void Configure(string slot, GridView grid, float heightOffset = 0)
        {
            if (UnitId.IsValid) throw new InvalidOperationException("View is already bound");
            _encounterSlotId = slot; _grid = grid; _heightOffset = heightOffset;
        }
        private Vector3 CellWorld(GridPoint point) => _grid.GridToWorld(point) + Vector3.up * _heightOffset;
        public void ConfigureBody(Transform body, Animator animator) { _bodyVisual = body; _animator = animator; }
        public void PlayFeedback(string kind)
        {
            if (kind == "Damage" || kind == "Clash") _bounceRemaining = .12f;
            if (_animator != null && _animationTriggers.Contains(kind)) _animator.SetTrigger(kind);
        }

        public void Bind(BattleInitializationResult mapping, UnitSnapshot snapshot)
        {
            if (!UnitId.IsValid)
            {
                _originalPosition = transform.position; _originalRotation = transform.rotation;
                _originalBodyActive = _bodyVisual != null && _bodyVisual.gameObject.activeSelf;
            }
            var slot = new EncounterSlotId(_encounterSlotId);
            if (_grid == null || !mapping.SlotToUnitId.TryGetValue(slot, out var unit)
                || !mapping.SlotToFaction.TryGetValue(slot, out var faction)
                || snapshot == null || snapshot.UnitId != unit.Value || snapshot.FactionId != faction.Value)
                throw new LogicDefinitionException("VIEW_SLOT_BINDING_INVALID", _encounterSlotId);
            UnitId = unit; FactionId = faction; LatestSnapshot = snapshot;
            _interpolating = false;
            _movementPlanId = 0; _forced = false;
            _bounceRemaining = 0; _animationTriggers.Clear();
            if (_bodyVisual != null) { _bodyScale = _bodyVisual.localScale; _bodyVisual.gameObject.SetActive(snapshot.IsAlive); }
            if (_animator != null && _animator.runtimeAnimatorController != null)
                foreach (var parameter in _animator.parameters)
                    if (parameter.type == AnimatorControllerParameterType.Trigger) _animationTriggers.Add(parameter.name);
            transform.position = CellWorld(new GridPoint(snapshot.X, snapshot.Y));
        }
        public void Unbind()
        {
            if (!UnitId.IsValid) return;
            transform.position = _originalPosition; transform.rotation = _originalRotation;
            if (_bodyVisual != null) { _bodyVisual.localScale = _bodyScale; _bodyVisual.gameObject.SetActive(_originalBodyActive); }
            UnitId = default; FactionId = default; LatestSnapshot = null;
            _interpolating = false; _forced = false; _movementPlanId = 0; _bounceRemaining = 0;
        }

        internal void BindCreated(UnitSnapshot snapshot)
        {
            if (_grid == null || snapshot == null || snapshot.UnitId <= 0 || string.IsNullOrEmpty(snapshot.FactionId))
                throw new LogicDefinitionException("VIEW_CREATED_BINDING_INVALID", "");
            UnitId = default; _encounterSlotId = string.Empty;
            _originalPosition = transform.position; _originalRotation = transform.rotation;
            _originalBodyActive = _bodyVisual != null && _bodyVisual.gameObject.activeSelf;
            UnitId = new UnitId(snapshot.UnitId); FactionId = new FactionId(snapshot.FactionId);
            LatestSnapshot = snapshot; _interpolating = false; _movementPlanId = 0; _forced = false;
            _bounceRemaining = 0; _animationTriggers.Clear();
            if (_bodyVisual != null) _bodyScale = _bodyVisual.localScale;
            if (_animator != null && _animator.runtimeAnimatorController != null)
                foreach (var parameter in _animator.parameters)
                    if (parameter.type == AnimatorControllerParameterType.Trigger) _animationTriggers.Add(parameter.name);
            ApplySnapshot(snapshot);
        }

        public void Consume(LogicEvent fact)
        {
            if (fact is ForcedDisplacementResolvedEvent displacement && displacement.TargetUnitId == UnitId)
            {
                _interpolating = displacement.AppliedSteps > 0;
                _forced = true;
                _movementPlanId = 0;
                _elapsed = 0;
                _from = CellWorld(displacement.From);
                _to = CellWorld(displacement.To);
                transform.position = _from;
            }
            else if (fact is ActionPlanTerminatedEvent terminated && terminated.ActionPlanId == _movementPlanId)
                _interpolating = false;
        }

        public void ApplyMovementSnapshot(LogicSnapshot snapshot)
        {
            _battleTick = snapshot.Tick; _visualFraction = 0;
            if (_forced && _interpolating) return;
            _forced = false; _movementPlanId = 0;
            foreach (var plan in snapshot.Plans)
            {
                if (plan.OwnerUnitId != UnitId.Value || plan.State != (int)ProjectHero.Logic.Actions.ActionPlanState.Running) continue;
                foreach (var segment in snapshot.MovementSegments)
                {
                    if (segment.ActionPlanId != plan.ActionPlanId) continue;
                    if (segment.EndTick > snapshot.Tick)
                    {
                        _movementPlanId = plan.ActionPlanId;
                        _segmentStart = segment.StartTick; _segmentEnd = segment.EndTick;
                        _from = CellWorld(new GridPoint(segment.FromX, segment.FromY));
                        _to = CellWorld(new GridPoint(segment.ToX, segment.ToY));
                        _interpolating = true; return;
                    }
                }
            }
            _interpolating = false;
        }

        public void ApplySnapshot(UnitSnapshot snapshot)
        {
            if (snapshot == null || snapshot.UnitId != UnitId.Value || snapshot.FactionId != FactionId.Value)
                throw new LogicDefinitionException("VIEW_SNAPSHOT_BINDING_MISMATCH", _encounterSlotId);
            LatestSnapshot = snapshot;
            transform.rotation = _grid.transform.rotation * Quaternion.Euler(0, -30 * snapshot.Facing, 0);
            if (_bodyVisual != null) _bodyVisual.gameObject.SetActive(snapshot.IsAlive);
            Vector3 committed = CellWorld(new GridPoint(snapshot.X, snapshot.Y));
            if (!_interpolating || (_forced && (_to - committed).sqrMagnitude > 0.000001f))
            { _interpolating = false; transform.position = committed; }
        }

        private void LateUpdate()
        {
            AdvanceVisual(Time.unscaledDeltaTime);
            if (_bodyVisual != null && _bounceRemaining > 0)
            {
                _bounceRemaining = Mathf.Max(0, _bounceRemaining - Time.unscaledDeltaTime);
                _bodyVisual.localScale = _bodyScale * (1 + .08f * Mathf.Sin(_bounceRemaining / .12f * Mathf.PI));
            }
        }
        public void AdvanceVisual(float seconds)
        {
            if (!_interpolating) return;
            if (_movementPlanId != 0)
            {
                _visualFraction = Mathf.Min(1, _visualFraction + Mathf.Max(0, seconds) * 60);
                double alphaTick = (_battleTick + _visualFraction - _segmentStart) / Math.Max(1d, _segmentEnd - _segmentStart);
                transform.position = Vector3.Lerp(_from, _to, Mathf.Clamp01((float)alphaTick));
                return;
            }
            _elapsed += Mathf.Max(0, seconds);
            float alpha = Mathf.Clamp01(_elapsed / _displacementSeconds);
            transform.position = Vector3.Lerp(_from, _to, alpha);
            if (alpha >= 1) _interpolating = false;
        }
    }
}
