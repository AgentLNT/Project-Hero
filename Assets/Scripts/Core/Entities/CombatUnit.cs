using ProjectHero.Core.Actions;
using ProjectHero.Core.Actions.Intents;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectHero.Core.Entities
{
    public class CombatUnit : MonoBehaviour
    {
        // ... existing code ...

        [Header("Control")]
        [Tooltip("Only the player-controlled unit can be selected for issuing commands. Other units can only be Observed.")]
        public bool IsPlayerControlled = false;
        [Header("Grid State")]
        public Pathfinder.GridPoint InitialGridPosition;

        // The logical position on the grid. 
        // Updates ONLY when a move step is fully completed.
        public Pathfinder.GridPoint GridPosition { get; private set; }

        [Header("Volume")]
        public UnitVolume UnitVolumeDefinition;
        public GridDirection FacingDirection = GridDirection.East;

        [Header("Actions")]
        public ActionLibrarySO ActionLibrary; // The unit's specific set of moves

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
        public float ArmorWeight = 10f; // W_Armor
        public float ArmorDefense = 0f; // Physical Defense
        public float MagicResistance = 0f; // Magic Defense

        [Header("State")]
        public float CurrentHealth = 100f; // Added
        public float CurrentStamina = 100f;
        public float CurrentFocus = 0f; // Focus Points (Point-based resource)
        public float CurrentAdrenaline = 0f; // Adrenaline

        // --- Derived Stats (Design Section III) ---

        // Max Focus (Point-based) = WIS / 2 (Example: 10 WIS = 5 Focus Points)
        public float MaxFocus => Mathf.Max(3f, Wisdom * 0.5f);

        // Total Mass (M_total) = STR + CON + Armor
        // Formula approximation: Base(50) + STR*2 + CON*2 + Armor
        public float TotalMass => 50f + (Strength * 2f) + (Constitution * 2f) + ArmorWeight;

        // Swiftness (v) = DEX + STR
        // Formula approximation: DEX*1.5 + STR*0.5
        // Penalty: If Stamina < 20%, Swiftness drops significantly.
        public float Swiftness 
        {
            get 
            {
                float baseVal = (Dexterity * 1.5f) + (Strength * 0.5f);
                if (IsExhausted) 
                {
                    return baseVal * 0.5f; // Exhaustion penalty: 50% speed
                }
                return baseVal;
            }
        }
        // Reaction (Window Width) = WIS
        public float ReactionWindow => Wisdom * 0.1f; 
        
        // Max Stamina derived from Constitution (Design Section III)
        // Formula: CON * 10 (Example: 10 CON = 100 Stamina)
        public float MaxStamina => Constitution * 10f;

        // Increased HP scaling to allow survival of high momentum hits
        // Formula: CON * 20 (Example: 10 CON = 200 HP)
        public float MaxHealth => Constitution * 20f; 
        
        [Header("Status Flags")]
        public bool IsStaggered;    // 硬直/失衡
        public bool IsKnockedDown;  // 倒地
        public bool IsExhausted => CurrentStamina < MaxStamina * 0.2f; // Low Stamina Flag (<20%)

        [Header("Action State")]
        public bool IsActing;       // 总开关：是否正在执行任何动作（包括移动、攻击）
        public bool InWindup;       // 前摇中（易被打断）
        public bool InRecovery;     // 后摇中（可被取消）
        public bool IsMoving;       // 移动中（主动移动）

        // Helper to check if unit can accept new commands
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
            // 1. Unregister Old Volume
            if (GridManager.Instance != null)
            {
                var oldVolume = GetOccupiedTriangles();
                GridManager.Instance.UnregisterOccupancy(oldVolume);
            }

            // 2. Update State
            GridPosition = point;

            // 3. Register New Volume
            if (GridManager.Instance != null)
            {
                var newVolume = GetOccupiedTriangles();
                GridManager.Instance.RegisterOccupancy(this, newVolume);
            }
        }

        // Overload to update both Position and Facing atomically
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
            // Adrenaline Decay
            if (CurrentAdrenaline > 0)
            {
                // Decay rate: 5 points per second (example)
                CurrentAdrenaline -= 5f * Time.deltaTime;
                if (CurrentAdrenaline < 0) CurrentAdrenaline = 0;
            }

            if(CurrentStamina < MaxStamina)
            {
                // Stamina Regeneration
                float regenRate = IsExhausted ? 2f : 5f; // Slower regen when exhausted
                CurrentStamina += regenRate * Time.deltaTime;
                if (CurrentStamina > MaxStamina) CurrentStamina = MaxStamina;
            }
        }

        private void Start()
        {
            // Initialize logical position
            GridPosition = InitialGridPosition;

            // Register Unit with GridManager
            if (GridManager.Instance != null)
            {
                GridManager.Instance.RegisterUnit(this);
            }

            // Register Initial Volume
            if (GridManager.Instance != null)
            {
                var vol = GetOccupiedTriangles();
                GridManager.Instance.RegisterOccupancy(this, vol);
            }

            // Snap visual position to grid
            if (GridManager.Instance != null)
            {
                Vector3 position = GridManager.Instance.GridToWorld(GridPosition);
                transform.position = GridManager.GetGroundPosition(position);
            }

            // Initialize stamina to max on start
            CurrentStamina = MaxStamina;
            CurrentHealth = MaxHealth; // Added
            // Focus and Adrenaline usually start at 0 or specific values
        }

        private void OnDestroy()
        {
            if (GridManager.Instance != null)
            {
                GridManager.Instance.UnregisterOccupancy(GetOccupiedTriangles());
                GridManager.Instance.UnregisterUnit(this);
            }
        }


        //private void OnDrawGizmos()
        //{
        //    if (GridManager.Instance == null) return;

        //    // Draw Logical Volume
        //    Gizmos.color = new Color(0, 1, 0, 0.4f); // Semi-transparent green
        //    var occupied = GetOccupiedTriangles();
        //    foreach (var tri in occupied)
        //    {
        //        var corners = GridManager.Instance.GetTriangleCorners(tri);
        //        if (corners.Length == 3)
        //        {
        //            Gizmos.DrawLine(corners[0], corners[1]);
        //            Gizmos.DrawLine(corners[1], corners[2]);
        //            Gizmos.DrawLine(corners[2], corners[0]);
                    
        //            // Fill hint (just lines for now, or small sphere at center)
        //            var center = GridManager.Instance.GetTriangleCenter(tri);
        //            Gizmos.DrawSphere(GridManager.GetGroundPosition(center), 0.1f);
        //        }
        //    }

        //    // Draw Facing Direction
        //    Gizmos.color = Color.blue;
        //    Vector3 pos = GridManager.Instance.GridToWorld(GridPosition);
        //    pos = GridManager.GetGroundPosition(pos);
            
        //    // Simple arrow approximation based on FacingDirection
        //    // (Ideally we'd have a helper to convert GridDirection to Vector3)
        //    Gizmos.DrawRay(pos, Vector3.up * 2f); 
        //}


        public void OnImpact(BattleTimeline timeline, float impactVelocity, float damage, int pushDistance = 0, GridDirection pushDirection = GridDirection.East)
        {
            // 1. Apply Damage
            CurrentHealth -= damage;
            Debug.Log($"{name} took {damage:F1} damage! HP: {CurrentHealth}/{MaxHealth}");

            if (CurrentHealth <= 0)
            {
                Debug.Log($"{name} has been DEFEATED!");
                
                // 1. Cancel all events
                if (timeline != null) timeline.CancelEvents(this);

                // 2. Unregister from Grid Logic
                if (GridManager.Instance != null)
                {
                    GridManager.Instance.UnregisterOccupancy(GetOccupiedTriangles());
                    GridManager.Instance.UnregisterUnit(this);
                }

                // 3. Disable/Destroy Visuals
                // TODO: Play death animation before disabling
                gameObject.SetActive(false); 
                return;
            }

            // 2. Apply Knockback / Forced Movement
            if (pushDistance > 0)
            {
                ActionScheduler.ScheduleKnockback(timeline, this, pushDirection, pushDistance, impactVelocity);
            }
        }

    }
}
