using UnityEngine;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Demos;

namespace ProjectHero.Visuals
{
    /// <summary>
    /// Timeline-driven visual movement.
    /// 
    /// FINAL FIX: Uses World Position comparison to robustly detect expected logic updates.
    /// This ensures smooth animation regardless of ActionScheduler execution order or missing parameters.
    /// </summary>
    public class UnitMovement : MonoBehaviour
    {
        private CombatUnit _unit;
        private BattleTimeline _timeline;
        private CombatDemo _combatDemo;

        // Active visual move state
        private bool _isMoving;
        private Vector3 _moveStartPos;
        private Vector3 _moveTargetPos;
        private float _moveStartTime;   // Timeline time when the move started
        private float _moveDuration;
        private bool _rotateOnMove;

        // Track the last known logical position to detect changes
        private Pathfinder.GridPoint _lastKnownLogicPos;
        private bool _hasLastKnownLogicPos;

        private void Awake()
        {
            _unit = GetComponent<CombatUnit>();
            _timeline = FindFirstObjectByType<BattleTimeline>();
            _combatDemo = FindFirstObjectByType<CombatDemo>();
        }

        private float GetVisualTimeSeconds()
        {
            // Prefer CombatDemo.timer because it is the authoritative pause-controlled time source.
            if (_combatDemo != null) return _combatDemo.timer;
            if (_timeline != null) return _timeline.CurrentTime;
            return Time.time;
        }

        private void Start()
        {
            // Initialize to current logical position
            SyncToLogicPosition();
        }

        private void LateUpdate()
        {
            if (_unit == null || GridManager.Instance == null) return;

            var currentLogic = _unit.GridPosition;

            // Check if logical position has changed since last frame
            if (_hasLastKnownLogicPos && !currentLogic.Equals(_lastKnownLogicPos))
            {
                // --- ROBUST FIX ---
                // Instead of relying on optional parameters or exact GridPoint matching (which can be fragile),
                // we compare the World Position of the new logical coordinate against our active visual path.

                bool isExpected = false;

                if (_isMoving)
                {
                    // Calculate where this new logical position is in the world
                    Vector3 newLogicWorld = GridManager.GetGroundPosition(GridManager.Instance.GridToWorld(currentLogic));

                    // Check 1: Is the new logic position the DESTINATION of our current visual move?
                    // (Matches cases where logic commits instantly or at end of step)
                    if (Vector3.SqrMagnitude(newLogicWorld - _moveTargetPos) < 0.05f)
                    {
                        isExpected = true;
                    }
                    // Check 2: Is the new logic position the START of our current visual move?
                    // (Matches cases where logic update happens right before MoveVisuals, or redundant updates)
                    else if (Vector3.SqrMagnitude(newLogicWorld - _moveStartPos) < 0.05f)
                    {
                        isExpected = true;
                    }
                }

                if (isExpected)
                {
                    // The logic layer updated to a position we are already handling visually.
                    // IGNORE the snap. Let the Lerp finish naturally.
                    _lastKnownLogicPos = currentLogic;
                }
                else
                {
                    // The logic layer moved the unit to somewhere completely different (Teleport, Knockback, etc).
                    // We must SNAP immediately to honor the game state.
                    Vector3 logicWorld = GridManager.GetGroundPosition(GridManager.Instance.GridToWorld(currentLogic));
                    transform.position = logicWorld;

                    _lastKnownLogicPos = currentLogic;
                    _hasLastKnownLogicPos = true;

                    // Stop any current visual move since logic diverted.
                    _isMoving = false;
                }
                // --- FIX END ---
            }
            else
            {
                // No change, just update tracker
                _lastKnownLogicPos = currentLogic;
                _hasLastKnownLogicPos = true;
            }

            // If not moving, nothing else to do
            if (!_isMoving) return;

            float elapsed = GetVisualTimeSeconds() - _moveStartTime;
            float t = Mathf.Clamp01(elapsed / Mathf.Max(0.001f, _moveDuration));

            transform.position = Vector3.Lerp(_moveStartPos, _moveTargetPos, t);

            // Check if move completed
            if (t >= 1f)
            {
                transform.position = _moveTargetPos;
                _isMoving = false;

                if (_rotateOnMove)
                {
                    Vector3 direction = (_moveTargetPos - _moveStartPos).normalized;
                    if (direction != Vector3.zero)
                    {
                        direction.y = 0;
                        if (direction.sqrMagnitude > 0.001f)
                            transform.rotation = Quaternion.LookRotation(direction);
                    }
                }
            }
        }

        public void MoveVisuals(Vector3 targetPos, float duration, System.Action onComplete = null, bool rotate = true, Pathfinder.GridPoint? expectedLogicEnd = null)
        {
            // Ensure target is grounded
            targetPos = GridManager.GetGroundPosition(targetPos);

            // Chain from current visual position (not logical position)
            _moveStartPos = transform.position;
            _moveTargetPos = targetPos;
            _moveDuration = Mathf.Max(0.001f, duration);
            _moveStartTime = GetVisualTimeSeconds();
            _rotateOnMove = rotate;
            _isMoving = true;

            // Update logical tracker immediately to avoid self-triggering logic change detection
            // if logic was updated just before this call in the same frame.
            if (_unit != null)
            {
                _lastKnownLogicPos = _unit.GridPosition;
                _hasLastKnownLogicPos = true;
            }
        }

        public void SyncToLogicPosition()
        {
            if (_unit == null || GridManager.Instance == null) return;

            Vector3 logicWorld = GridManager.GetGroundPosition(GridManager.Instance.GridToWorld(_unit.GridPosition));
            transform.position = logicWorld;

            _lastKnownLogicPos = _unit.GridPosition;
            _hasLastKnownLogicPos = true;
            _isMoving = false;
        }

        public void CancelVisualMoveAndSnapToLogic()
        {
            _isMoving = false;
            SyncToLogicPosition();
        }

        public bool IsMoving => _isMoving;
    }
}
