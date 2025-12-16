using ProjectHero.Core.Actions;
using ProjectHero.Core.Actions.Intents;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;
using ProjectHero.Visuals;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectHero.Core.Entities
{
    public class CombatUnit : MonoBehaviour
    {
        private void Awake()
        {
            if (GetComponent<UnitMovement>() == null)
            {
                gameObject.AddComponent<UnitMovement>();
            }
        }

        [Header("Control")]
        [Tooltip("Only the player-controlled unit can be selected for issuing commands.")]
        public bool IsPlayerControlled = false;

        [Header("Grid State")]
        public Pathfinder.GridPoint InitialGridPosition;
        public Pathfinder.GridPoint GridPosition { get; private set; }

        [Header("Volume")]
        public UnitVolume UnitVolumeDefinition;
        public GridDirection FacingDirection = GridDirection.East;

        [Header("Actions")]
        public ActionLibrarySO ActionLibrary;

        public List<TrianglePoint> GetOccupiedTriangles()
        {
            if (UnitVolumeDefinition == null) return new List<TrianglePoint>();

            var relativeTriangles = UnitVolumeDefinition.GetVolumeFor(FacingDirection);
            var occupied = new List<TrianglePoint>();

            foreach (var rel in relativeTriangles)
            {
                occupied.Add(new TrianglePoint(GridPosition.X + rel.X, GridPosition.Y + rel.Y, rel.T));
            }
            return occupied;
        }

        public List<TrianglePoint> GetProjectedOccupancy(Pathfinder.GridPoint targetPos, GridDirection targetFacing)
        {
            if (UnitVolumeDefinition == null) return new List<TrianglePoint>();

            var relativeTriangles = UnitVolumeDefinition.GetVolumeFor(targetFacing);
            var occupied = new List<TrianglePoint>();

            foreach (var rel in relativeTriangles)
            {
                occupied.Add(new TrianglePoint(targetPos.X + rel.X, targetPos.Y + rel.Y, rel.T));
            }
            return occupied;
        }

        [Header("Base Attributes")]
        public float Strength = 10f;
        public float Dexterity = 10f;
        public float Constitution = 10f;
        public float Wisdom = 10f;
        public float Intelligence = 10f;

        [Header("Equipment")]
        public float ArmorWeight = 10f;
        public float ArmorDefense = 0f;
        public float MagicResistance = 0f;

        [Header("State")]
        public float CurrentHealth = 100f;
        public float CurrentStamina = 100f;
        public float CurrentFocus = 0f;
        public float CurrentAdrenaline = 0f;

        public float MaxFocus => Mathf.Max(3f, Wisdom * 0.5f);

        public float TotalMass => 50f + (Strength * 2f) + (Constitution * 2f) + ArmorWeight;

        // Swiftness (v) = DEX + STR
        // REBALANCED: Reduced multipliers to slow down game pace significantly.
        // Old: DEX*1.5 + STR*0.5
        // New: DEX*0.75 + STR*0.25 (Approx 50% slower action speed)
        public float Swiftness
        {
            get
            {
                float baseVal = (Dexterity * 0.75f) + (Strength * 0.25f);
                if (IsExhausted)
                {
                    return baseVal * 0.5f;
                }
                return baseVal;
            }
        }

        public float ReactionWindow => Wisdom * 0.1f;
        public float MaxStamina => Constitution * 10f;
        public float MaxHealth => Constitution * 20f;

        [Header("Status Flags")]
        public bool IsStaggered;
        public bool IsKnockedDown;
        public bool IsExhausted => CurrentStamina < MaxStamina * 0.2f;

        [Header("Action State")]
        public bool IsActing;
        public bool InWindup;
        public bool InRecovery;
        public bool IsMoving;

        [Header("Recovery")]
        public bool IsRecoveringAction;

        public bool CanAct => !IsActing && !IsStaggered && !IsKnockedDown;

        public void ResetActionState()
        {
            IsActing = false;
            InWindup = false;
            InRecovery = false;
            IsMoving = false;
        }

        // --- Grid Logic ---

        public void SetGridPosition(Pathfinder.GridPoint point)
        {
            if (GridManager.Instance != null)
            {
                var oldVolume = GetOccupiedTriangles();
                GridManager.Instance.UnregisterOccupancy(oldVolume);
            }
            GridPosition = point;
            if (GridManager.Instance != null)
            {
                var newVolume = GetOccupiedTriangles();
                GridManager.Instance.RegisterOccupancy(this, newVolume);
            }
        }

        public void SetGridPositionAndFacing(Pathfinder.GridPoint point, GridDirection facing)
        {
            if (GridManager.Instance != null)
            {
                var oldVolume = GetOccupiedTriangles();
                GridManager.Instance.UnregisterOccupancy(oldVolume);
            }
            GridPosition = point;
            FacingDirection = facing;
            if (GridManager.Instance != null)
            {
                var newVolume = GetOccupiedTriangles();
                GridManager.Instance.RegisterOccupancy(this, newVolume);
            }
        }

        public void SetFacingDirection(GridDirection newFacing)
        {
            if (FacingDirection == newFacing) return;

            if (GridManager.Instance != null)
            {
                var oldVolume = GetOccupiedTriangles();
                GridManager.Instance.UnregisterOccupancy(oldVolume);
            }
            FacingDirection = newFacing;
            if (GridManager.Instance != null)
            {
                var newVolume = GetOccupiedTriangles();
                GridManager.Instance.RegisterOccupancy(this, newVolume);
            }
        }

        private void Update()
        {
            if (CurrentAdrenaline > 0)
            {
                CurrentAdrenaline -= 5f * Time.deltaTime;
                if (CurrentAdrenaline < 0) CurrentAdrenaline = 0;
            }

            if (CurrentStamina < MaxStamina)
            {
                float regenRate = IsExhausted ? 2f : 5f;
                CurrentStamina += regenRate * Time.deltaTime;
                if (CurrentStamina > MaxStamina) CurrentStamina = MaxStamina;
            }
        }

        private void Start()
        {
            GridPosition = InitialGridPosition;

            if (GridManager.Instance != null)
            {
                GridManager.Instance.RegisterUnit(this);
                GridManager.Instance.RegisterOccupancy(this, GetOccupiedTriangles());

                Vector3 position = GridManager.Instance.GridToWorld(GridPosition);
                transform.position = GridManager.GetGroundPosition(position);
            }

            CurrentStamina = MaxStamina;
            CurrentHealth = MaxHealth;
        }

        private void OnDestroy()
        {
            if (GridManager.Instance != null)
            {
                GridManager.Instance.UnregisterOccupancy(GetOccupiedTriangles());
                GridManager.Instance.UnregisterUnit(this);
            }
        }

        public void OnImpact(BattleTimeline timeline, float impactVelocity, float damage, int pushDistance = 0, GridDirection pushDirection = GridDirection.East)
        {
            CurrentHealth -= damage;
            Debug.Log($"{name} took {damage:F1} damage! HP: {CurrentHealth}/{MaxHealth}");

            if (CurrentHealth <= 0)
            {
                Debug.Log($"{name} has been DEFEATED!");
                if (timeline != null) timeline.CancelEvents(this);
                if (GridManager.Instance != null)
                {
                    GridManager.Instance.UnregisterOccupancy(GetOccupiedTriangles());
                    GridManager.Instance.UnregisterUnit(this);
                }
                gameObject.SetActive(false);
                return;
            }

            if (pushDistance > 0)
            {
                ActionScheduler.ScheduleKnockback(timeline, this, pushDirection, pushDistance, impactVelocity);
            }
        }
    }
}
