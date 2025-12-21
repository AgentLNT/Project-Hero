using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.UI;

namespace ProjectHero.Visuals
{
    public class GameFeelManager : MonoBehaviour
    {
        public static GameFeelManager Instance { get; private set; }

        [Header("Settings")]
        public Font DamageFont;

        private GameObject _screenCanvasObj;
        private Canvas _screenCanvas;

        private Dictionary<int, float> _stackOffsetMap = new Dictionary<int, float>();

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);

            EnsureScreenCanvas();
        }

        private void Update()
        {
            List<int> keys = new List<int>(_stackOffsetMap.Keys);
            foreach (var k in keys)
            {
                _stackOffsetMap[k] -= Time.unscaledDeltaTime * 2.0f;
                if (_stackOffsetMap[k] < 0) _stackOffsetMap[k] = 0;
            }
        }

        private void EnsureScreenCanvas()
        {
            if (_screenCanvas != null) return;

            _screenCanvasObj = new GameObject("DamageTextCanvas");
            _screenCanvas = _screenCanvasObj.AddComponent<Canvas>();
            _screenCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _screenCanvas.sortingOrder = 100; 

            var scaler = _screenCanvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
        }

        public void ShowDamageNumber(Vector3 worldPos, float damage, bool isCritical)
        {
            Color color = isCritical ? new Color(1f, 0.2f, 0.2f) : Color.white;
            float size = isCritical ? 1.5f : 1.0f;
            size += Mathf.Clamp(damage / 50f, 0f, 1.0f);

            SpawnText(worldPos, Mathf.RoundToInt(damage).ToString(), color, size);
        }

        public void ShowStatusText(Vector3 worldPos, string content, Color color)
        {
            SpawnText(worldPos, content, color, 1.2f);
        }

        private void SpawnText(Vector3 worldPos, string content, Color color, float sizeScale)
        {
            if (_screenCanvas == null) EnsureScreenCanvas();

            GameObject go = new GameObject("FloatText", typeof(RectTransform));
            go.transform.SetParent(_screenCanvas.transform, false);

            var text = go.AddComponent<Text>();

            // --- Unity 6 Font Fix ---
            if (DamageFont != null)
            {
                text.font = DamageFont;
            }
            else
            {
                text.font = Font.CreateDynamicFontFromOSFont("Arial", 32);
            }
            // ------------------------

            text.text = content;
            text.color = color;
            text.fontSize = 32;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            var outline = go.AddComponent<Outline>();
            outline.effectColor = Color.black;
            outline.effectDistance = new Vector2(1, -1);

            int posKey = Mathf.FloorToInt(worldPos.x * 10) + Mathf.FloorToInt(worldPos.z * 10) * 1000;
            if (!_stackOffsetMap.ContainsKey(posKey)) _stackOffsetMap[posKey] = 0f;

            float currentStackHeight = _stackOffsetMap[posKey];
            _stackOffsetMap[posKey] += 0.8f; 

            StartCoroutine(AnimateTextScreenSpace(go, text, worldPos, sizeScale, currentStackHeight));
        }

        private IEnumerator AnimateTextScreenSpace(GameObject go, Text text, Vector3 worldPos, float sizeScale, float startHeightOffset)
        {
            float duration = 1.0f;
            float timer = 0f;
            Vector3 startScale = Vector3.one * sizeScale;
            RectTransform rect = go.GetComponent<RectTransform>();

            float randomX = Random.Range(-15f, 15f);

            while (timer < duration)
            {
                if (go == null) yield break;
                timer += Time.unscaledDeltaTime; 
                float t = timer / duration;

                if (Camera.main != null)
                {
                    Vector3 screenPos = Camera.main.WorldToScreenPoint(worldPos);

                    if (screenPos.z > 0)
                    {
                        text.enabled = true;
                        screenPos.y += 100f + (startHeightOffset * 40f) + (t * 80f);
                        screenPos.x += randomX;
                        rect.position = screenPos;
                    }
                    else
                    {
                        text.enabled = false;
                    }
                }

                if (t < 0.2f)
                {
                    float s = Mathf.Lerp(0.5f, 1.2f, t / 0.2f);
                    rect.localScale = startScale * s;
                }
                else
                {
                    rect.localScale = startScale;
                }

                if (t > 0.5f)
                {
                    float alpha = Mathf.Lerp(1f, 0f, (t - 0.5f) / 0.5f);
                    text.color = new Color(text.color.r, text.color.g, text.color.b, alpha);
                }

                yield return null;
            }
            Destroy(go);
        }

        // --- Juice ---
        private bool _isFrozen = false;
        public void HitStop(float durationRealtime) { if (!_isFrozen) StartCoroutine(DoHitStop(durationRealtime)); }
        private IEnumerator DoHitStop(float duration)
        {
            _isFrozen = true;
            Time.timeScale = 0.05f;
            yield return new WaitForSecondsRealtime(duration);
            Time.timeScale = 1.0f;
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
                cam.position = originalPos + (Vector3)Random.insideUnitCircle * strength;
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
            cam.position = originalPos;
        }
    }
}
