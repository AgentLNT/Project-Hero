using UnityEngine;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Pathfinding;

namespace ProjectHero.Visuals
{
    public class UnitMovement : MonoBehaviour
    {
        private CombatUnit _unit;
        private BattleTimeline _timeline;
        private bool _isMoving;
        private Vector3 _moveStartPos;
        private Vector3 _moveTargetPos;
        private float _moveStartTime;
        private float _moveDuration;
        private bool _rotateOnMove;
        private Pathfinder.GridPoint _lastKnownLogicPos;
        private bool _hasLastKnownLogicPos;

        private void Awake()
        {
            _unit = GetComponent<CombatUnit>();
            _timeline = FindFirstObjectByType<BattleTimeline>();
        }

        private float GetVisualTimeSeconds()
        {
            if (_timeline != null) return _timeline.VisualTime;
            return Time.time;
        }

        private void Start()
        {
            SyncToLogicPosition();
        }

        private void LateUpdate()
        {
            if (_unit == null || GridManager.Instance == null) return;
            var currentLogic = _unit.GridPosition;

            if (_hasLastKnownLogicPos && !currentLogic.Equals(_lastKnownLogicPos))
            {
                bool isExpected = false;
                if (_isMoving)
                {
                    Vector3 newLogicWorld = GridManager.GetGroundPosition(GridManager.Instance.GridToWorld(currentLogic));
                    if (Vector3.SqrMagnitude(newLogicWorld - _moveTargetPos) < 0.05f) isExpected = true;
                    else if (Vector3.SqrMagnitude(newLogicWorld - _moveStartPos) < 0.05f) isExpected = true;
                }

                if (isExpected) _lastKnownLogicPos = currentLogic;
                else
                {
                    Vector3 logicWorld = GridManager.GetGroundPosition(GridManager.Instance.GridToWorld(currentLogic));
                    transform.position = logicWorld;
                    _lastKnownLogicPos = currentLogic;
                    _hasLastKnownLogicPos = true;
                    _isMoving = false;
                }
            }
            else
            {
                _lastKnownLogicPos = currentLogic;
                _hasLastKnownLogicPos = true;
            }

            if (!_isMoving) return;

            float elapsed = GetVisualTimeSeconds() - _moveStartTime;
            float t = Mathf.Clamp01(elapsed / Mathf.Max(0.001f, _moveDuration));
            transform.position = Vector3.Lerp(_moveStartPos, _moveTargetPos, t);

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
                        if (direction.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(direction);
                    }
                }
            }
        }

        public void MoveVisuals(Vector3 targetPos, float duration, System.Action onComplete = null, bool rotate = true, Pathfinder.GridPoint? expectedLogicEnd = null)
        {
            targetPos = GridManager.GetGroundPosition(targetPos);
            _moveStartPos = transform.position;
            _moveTargetPos = targetPos;
            _moveDuration = Mathf.Max(0.001f, duration);
            _moveStartTime = GetVisualTimeSeconds();
            _rotateOnMove = rotate;
            _isMoving = true;

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
