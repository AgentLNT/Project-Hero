using UnityEngine;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Demos;

namespace ProjectHero.Core.Visuals
{
    /// <summary>
    /// Timeline-driven visual movement.
    /// 
    /// Design principles:
    /// 1. No coroutines - all movement is driven by the timeline's timer.
    /// 2. When MoveVisuals() is called, we record start time, start pos, target pos, and duration.
    /// 3. Each frame, we interpolate based on (timeline.CurrentTime - startTime) / duration.
    /// 4. When the logical GridPosition changes (via SetGridPosition), we immediately snap
    ///    the visual position to match the new logical position. This ensures visuals
    ///    always reflect the authoritative logical state.
    /// 5. If a new MoveVisuals() call comes in while one is active, we seamlessly chain
    ///    from the current visual position.
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

        // Logical anchors for the currently active move step.
        // These let us distinguish "expected" logic updates (commit boundaries) from interruptions.
        private Pathfinder.GridPoint _moveStartLogicPos;
        private bool _hasMoveStartLogicPos;
        private Pathfinder.GridPoint _moveExpectedEndLogicPos;
        private bool _hasMoveExpectedEndLogicPos;

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
                // Authoritative rule: whenever logic updates, snap visuals immediately.
                Vector3 logicWorld = GridManager.GetGroundPosition(GridManager.Instance.GridToWorld(currentLogic));
                transform.position = logicWorld;

                // Update tracking first so we don't repeatedly process the same change.
                _lastKnownLogicPos = currentLogic;
                _hasLastKnownLogicPos = true;

                // If we are mid-move, decide whether this logic change is expected.
                // Expected cases:
                // - Commit from previous step updates logic to our current step's START.
                // - Commit from this step updates logic to our current step's END.
                if (_isMoving)
                {
                    bool expected = false;
                    if (_hasMoveStartLogicPos && currentLogic.Equals(_moveStartLogicPos)) expected = true;
                    if (!expected && _hasMoveExpectedEndLogicPos && currentLogic.Equals(_moveExpectedEndLogicPos)) expected = true;

                    if (expected)
                    {
                        // Rebase interpolation from the snapped logic position so the step continues smoothly.
                        _moveStartPos = logicWorld;
                        _moveStartTime = GetVisualTimeSeconds();

                        // If we just snapped to the expected end of this step, stop the tween.
                        if (_hasMoveExpectedEndLogicPos && currentLogic.Equals(_moveExpectedEndLogicPos))
                        {
                            _isMoving = false;
                        }
                    }
                    else
                    {
                        // Unexpected logic change (e.g., knockback/teleport/cancel): stop current tween.
                        _isMoving = false;
                        _hasMoveStartLogicPos = false;
                        _hasMoveExpectedEndLogicPos = false;
                    }
                }
                return;
            }

            // Update tracking
            _lastKnownLogicPos = currentLogic;
            _hasLastKnownLogicPos = true;

            // If not moving, nothing else to do (we're already synced)
            if (!_isMoving) return;

            float elapsed = GetVisualTimeSeconds() - _moveStartTime;
            float t = Mathf.Clamp01(elapsed / Mathf.Max(0.001f, _moveDuration));

            transform.position = Vector3.Lerp(_moveStartPos, _moveTargetPos, t);

            // Check if move completed
            if (t >= 1f)
            {
                transform.position = _moveTargetPos;
                _isMoving = false;
            }
        }

        /// <summary>
        /// Start a visual move from the current visual position to the target world position.
        /// Uses timeline time for interpolation (no coroutines).
        /// </summary>
        /// <param name="targetPos">World position to move to.</param>
        /// <param name="duration">Duration in timeline seconds.</param>
        /// <param name="onComplete">Ignored (kept for API compatibility).</param>
        /// <param name="rotate">Whether to rotate to face movement direction.</param>
        /// <param name="expectedLogicEnd">Ignored (kept for API compatibility).</param>
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

            // Capture logical anchors for this step.
            if (_unit != null)
            {
                _moveStartLogicPos = _unit.GridPosition;
                _hasMoveStartLogicPos = true;

                // Critical: refresh last-known logic so we don't treat the step-start commit as an "unexpected" change.
                _lastKnownLogicPos = _unit.GridPosition;
                _hasLastKnownLogicPos = true;
            }

            _hasMoveExpectedEndLogicPos = expectedLogicEnd.HasValue;
            if (_hasMoveExpectedEndLogicPos)
            {
                _moveExpectedEndLogicPos = expectedLogicEnd.Value;
            }

            // Instant rotation to face target
            if (rotate)
            {
                Vector3 direction = (targetPos - _moveStartPos).normalized;
                if (direction != Vector3.zero)
                {
                    direction.y = 0;
                    if (direction.sqrMagnitude > 0.001f)
                        transform.rotation = Quaternion.LookRotation(direction);
                }
            }

            // No coroutine: motion is applied in LateUpdate using the demo/timeline timer.
        }

        /// <summary>
        /// Immediately sync the visual position to the current logical grid position.
        /// </summary>
        public void SyncToLogicPosition()
        {
            if (_unit == null || GridManager.Instance == null) return;

            Vector3 logicWorld = GridManager.GetGroundPosition(GridManager.Instance.GridToWorld(_unit.GridPosition));
            transform.position = logicWorld;

            _lastKnownLogicPos = _unit.GridPosition;
            _hasLastKnownLogicPos = true;
        }

        /// <summary>
        /// Cancel any in-progress visual move and snap to the current logical position.
        /// </summary>
        public void CancelVisualMoveAndSnapToLogic()
        {
            _isMoving = false;
            SyncToLogicPosition();
        }

        /// <summary>
        /// Returns true if a visual move is currently in progress.
        /// </summary>
        public bool IsMoving => _isMoving;
    }
}
