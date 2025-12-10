using System.Collections;
using UnityEngine;
using System;
using ProjectHero.Core.Grid;

namespace ProjectHero.Core.Visuals
{
    // Refactored: Now a pure visual executor. Logic is handled by Timeline/CombatUnit.
    public class UnitMovement : MonoBehaviour
    {
        // Move the visual representation to a target world position over 'duration' seconds.
        public void MoveVisuals(Vector3 targetPos, float duration, Action onComplete = null, bool rotate = true)
        {
            // Ensure target is grounded
            targetPos = GridManager.GetGroundPosition(targetPos);
            
            StopAllCoroutines();
            StartCoroutine(MoveRoutine(targetPos, duration, onComplete, rotate));
        }

        private IEnumerator MoveRoutine(Vector3 targetPos, float duration, Action onComplete, bool rotate)
        {
            Vector3 startPos = transform.position;
            
            if (rotate)
            {
                // Instant rotation to face target (Design Choice: Crisp movement)
                Vector3 direction = (targetPos - startPos).normalized;
                if (direction != Vector3.zero)
                {
                    // Flatten direction to ignore Y difference for rotation
                    direction.y = 0; 
                    if (direction != Vector3.zero) 
                        transform.rotation = Quaternion.LookRotation(direction);
                }
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
    }
}
