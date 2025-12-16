using UnityEngine;
using ProjectHero.Visuals;

namespace ProjectHero.Visuals
{
    [RequireComponent(typeof(UnitMovement))]
    public class UnitBounce : MonoBehaviour
    {
        [Header("Movement Stretch")]
        // Increased from 0.15 to 0.35 to be visible at slower speeds
        public float StretchAmount = 0.35f;
        // Increased speed for snappier reaction
        public float StretchSpeed = 15f;

        [Header("Impact Squash")]
        // Increased from 0.3 to 0.5 for cartoonish impact
        public float SquashAmount = 0.5f;
        // Increased from 0.2 to 0.35 so the squash lingers longer (matching slower pace)
        public float SquashDuration = 0.35f;

        private UnitMovement _movement;
        private Vector3 _originalScale;
        private Vector3 _targetScale;
        private float _squashTimer = 0f;

        private void Awake()
        {
            _movement = GetComponent<UnitMovement>();
            _originalScale = transform.localScale;
            _targetScale = _originalScale;
        }

        private void Update()
        {
            // Squash Recovery (Impact)
            if (_squashTimer > 0)
            {
                _squashTimer -= Time.deltaTime;
                float t = 1f - (_squashTimer / SquashDuration);
                // Overshoot curve
                float curve = Mathf.Sin(t * Mathf.PI);

                // Y Scale reduces (Squash)
                float squashY = _originalScale.y * (1f - SquashAmount * (1f - t));

                // XZ Scale expands significantly to conserve volume feel
                // Tweaked logic: (1 + Amount * 1.0) instead of 0.5 to make it "fatter" when squashed
                float squashXZ = _originalScale.x * (1f + SquashAmount * 1.0f * (1f - t));

                _targetScale = new Vector3(squashXZ, squashY, squashXZ);
            }
            // Movement Stretch (Moving)
            else if (_movement != null && _movement.IsMoving)
            {
                // Y Scale increases (Stretch)
                float stretchY = _originalScale.y * (1f + StretchAmount);
                // XZ Scale reduces (Thin)
                float stretchXZ = _originalScale.x * (1f - StretchAmount * 0.4f);

                _targetScale = new Vector3(stretchXZ, stretchY, stretchXZ);
            }
            // Return to Normal
            else
            {
                _targetScale = _originalScale;
            }

            // Smoothly interpolate to the target scale
            transform.localScale = Vector3.Lerp(transform.localScale, _targetScale, Time.deltaTime * StretchSpeed);
        }

        public void OnImpact(float force)
        {
            // Reset timer to full duration on impact
            _squashTimer = SquashDuration;
        }
    }
}
