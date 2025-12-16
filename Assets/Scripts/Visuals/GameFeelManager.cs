using UnityEngine;
using System.Collections;
using TMPro;

namespace ProjectHero.Visuals
{
    public class GameFeelManager : MonoBehaviour
    {
        public static GameFeelManager Instance { get; private set; }

        [Header("Floating Text Settings")]
        public GameObject FloatingTextPrefab; // ASSIGN A PREFAB WITH TMP HERE
        public float TextFloatSpeed = 2.0f;
        public float TextFadeDuration = 1.0f;
        public float TextScaleMultiplier = 0.05f;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        public void ShowDamageNumber(Vector3 worldPos, float damage, bool isCritical)
        {
            Color color = isCritical ? new Color(1f, 0.2f, 0.2f) : Color.white;
            float size = isCritical ? 1.5f : 1.0f;
            size += Mathf.Clamp(damage / 50f, 0f, 1.0f);
            SpawnFloatingText(worldPos + Vector3.up * 2f, Mathf.RoundToInt(damage).ToString(), color, size);
        }

        public void ShowStatusText(Vector3 worldPos, string text, Color color)
        {
            SpawnFloatingText(worldPos + Vector3.up * 2.5f, text, color, 1.2f);
        }

        private void SpawnFloatingText(Vector3 pos, string content, Color color, float sizeScale)
        {
            if (FloatingTextPrefab == null)
            {
                Debug.LogWarning("FloatingTextPrefab not assigned in GameFeelManager!");
                return;
            }

            // Move text towards camera to prevent z-fighting
            Vector3 camFwd = Camera.main.transform.forward;
            Vector3 renderPos = pos - camFwd * 2.0f;
            renderPos += Random.insideUnitSphere * 0.3f;

            GameObject go = Instantiate(FloatingTextPrefab, renderPos, Camera.main.transform.rotation);

            var tmp = go.GetComponent<TextMeshPro>();
            if (tmp == null) tmp = go.AddComponent<TextMeshPro>(); // Fallback

            tmp.text = content;
            tmp.color = color;
            tmp.fontSize = 6;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontStyle = FontStyles.Bold;

            go.transform.localScale = Vector3.one * sizeScale;

            StartCoroutine(AnimateFloatingText(go, tmp));
        }

        private IEnumerator AnimateFloatingText(GameObject go, TextMeshPro tmp)
        {
            float timer = 0f;
            Vector3 startPos = go.transform.position;
            Color startColor = tmp.color;

            while (timer < TextFadeDuration)
            {
                if (go == null) yield break;
                timer += Time.unscaledDeltaTime;
                float progress = timer / TextFadeDuration;
                go.transform.position = startPos + Vector3.up * (progress * TextFloatSpeed);

                if (progress < 0.2f)
                {
                    float s = Mathf.Lerp(0.5f, 1.2f, progress / 0.2f);
                    float pScale = (s == 0 ? 1 : s);
                    go.transform.localScale = Vector3.one * (s * (go.transform.localScale.x / pScale));
                }

                if (progress > 0.5f)
                {
                    float alpha = Mathf.Lerp(1f, 0f, (progress - 0.5f) / 0.5f);
                    tmp.color = new Color(startColor.r, startColor.g, startColor.b, alpha);
                }
                yield return null;
            }
            Destroy(go);
        }

        // ... Juice ...
        private bool _isFrozen = false;
        public void HitStop(float durationRealtime)
        {
            if (_isFrozen) return;
            StartCoroutine(DoHitStop(durationRealtime));
        }
        private IEnumerator DoHitStop(float duration)
        {
            _isFrozen = true;
            float originalScale = Time.timeScale;
            Time.timeScale = 0.05f;
            yield return new WaitForSecondsRealtime(duration);
            Time.timeScale = originalScale;
            _isFrozen = false;
        }
        public void ScreenShake(float intensity, float duration) { StartCoroutine(DoScreenShake(intensity, duration)); }
        private IEnumerator DoScreenShake(float intensity, float duration)
        {
            Transform cam = Camera.main.transform;
            Vector3 originalPos = cam.position;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float strength = Mathf.Lerp(intensity, 0f, elapsed / duration);
                float x = Random.Range(-1f, 1f) * strength;
                float y = Random.Range(-1f, 1f) * strength;
                cam.position = originalPos + cam.right * x + cam.up * y;
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
            cam.position = originalPos;
        }
    }
}
