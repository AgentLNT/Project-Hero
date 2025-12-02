using System.Collections;
using UnityEngine;
using System;

namespace ProjectHero.Core.Visuals
{
    // Refactored: Now a pure visual executor. Logic is handled by Timeline/CombatUnit.
    public class UnitMovement : MonoBehaviour
    {
        [Header("Visual Settings")]
        public LayerMask GroundLayer;

        // Move the visual representation to a target world position over 'duration' seconds.
        public void MoveVisuals(Vector3 targetPos, float duration, Action onComplete = null)
        {
            // Ensure target is grounded
            targetPos = GetGroundPosition(targetPos);
            
            StopAllCoroutines();
            StartCoroutine(MoveRoutine(targetPos, duration, onComplete));
        }

        private IEnumerator MoveRoutine(Vector3 targetPos, float duration, Action onComplete)
        {
            Vector3 startPos = transform.position;
            
            // Instant rotation to face target (Design Choice: Crisp movement)
            Vector3 direction = (targetPos - startPos).normalized;
            if (direction != Vector3.zero)
            {
                // Flatten direction to ignore Y difference for rotation
                direction.y = 0; 
                if (direction != Vector3.zero) 
                    transform.rotation = Quaternion.LookRotation(direction);
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;
                
                // Linear interpolation for position
                transform.position = Vector3.Lerp(startPos, targetPos, t);
                
                yield return null;
            }

            // Ensure exact arrival
            transform.position = targetPos;
            onComplete?.Invoke();
        }

        private Vector3 GetGroundPosition(Vector3 pos)
        {
            if (UnityEngine.Physics.Raycast(new Vector3(pos.x, 100f, pos.z), Vector3.down, out RaycastHit hit, 200f, GroundLayer))
            {
                pos.y = hit.point.y;
            }
            return pos;
        }
    }
}
