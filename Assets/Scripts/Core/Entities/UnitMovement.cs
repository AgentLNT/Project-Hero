using System.Collections.Generic;
using UnityEngine;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Grid;
using System;

namespace ProjectHero.Core.Entities
{
    public class UnitMovement : MonoBehaviour
    {
        [Header("Movement Settings")]
        public float MoveSpeed = 5.0f;
        public float RotationSpeed = 10.0f;
        public LayerMask GroundLayer;

        private Queue<Pathfinder.GridPoint> _pathQueue = new Queue<Pathfinder.GridPoint>();
        private Vector3 _currentTargetWorldPos;
        private bool _isMoving = false;
        private Action _onMoveComplete;

        // Current logical position
        public Pathfinder.GridPoint CurrentGridPos { get; private set; }

        private void Start()
        {
            // Initialize grid position based on current world position
            if (GridManager.Instance != null)
            {
                CurrentGridPos = GridManager.Instance.WorldToGrid(transform.position);
                // Snap to grid immediately to ensure consistency
                Vector3 snapPos = GridManager.Instance.GridToWorld(CurrentGridPos);
                transform.position = GetGroundPosition(snapPos);
            }
        }

        public void SetPath(List<Pathfinder.GridPoint> path, Action onComplete = null)
        {
            _pathQueue.Clear();
            if (path == null || path.Count == 0) return;

            // Skip the first point if it's the current position
            int startIndex = 0;
            if (path.Count > 0 && path[0].Equals(CurrentGridPos))
            {
                startIndex = 1;
            }

            for (int i = startIndex; i < path.Count; i++)
            {
                _pathQueue.Enqueue(path[i]);
            }

            _onMoveComplete = onComplete;
            
            if (_pathQueue.Count > 0)
            {
                SetNextTarget();
                _isMoving = true;
            }
            else
            {
                _onMoveComplete?.Invoke();
            }
        }

        private void SetNextTarget()
        {
            if (_pathQueue.Count > 0)
            {
                var nextPoint = _pathQueue.Dequeue();
                CurrentGridPos = nextPoint; // Update logical position immediately or after arrival? 
                                          // Usually updating immediately prevents other units from moving into this tile.
                
                _currentTargetWorldPos = GridManager.Instance.GridToWorld(nextPoint);
                _currentTargetWorldPos = GetGroundPosition(_currentTargetWorldPos);
            }
            else
            {
                _isMoving = false;
                _onMoveComplete?.Invoke();
            }
        }

        private Vector3 GetGroundPosition(Vector3 pos)
        {
            // Raycast from high up to find the ground
            if (UnityEngine.Physics.Raycast(new Vector3(pos.x, 100f, pos.z), Vector3.down, out RaycastHit hit, 200f, GroundLayer))
            {
                pos.y = hit.point.y;
            }
            return pos;
        }

        private void Update()
        {
            if (!_isMoving) return;

            // 1. Move towards target
            float step = MoveSpeed * Time.deltaTime;
            transform.position = Vector3.MoveTowards(transform.position, _currentTargetWorldPos, step);

            // 2. Rotate towards target
            Vector3 direction = (_currentTargetWorldPos - transform.position).normalized;
            if (direction != Vector3.zero)
            {
                transform.rotation = Quaternion.LookRotation(direction);
            }

            // 3. Check arrival with error correction
            if (Vector3.Distance(transform.position, _currentTargetWorldPos) < 0.01f)
            {
                // Force snap to exact coordinate to eliminate floating point drift
                transform.position = _currentTargetWorldPos;
                
                SetNextTarget();
            }
        }
    }
}
