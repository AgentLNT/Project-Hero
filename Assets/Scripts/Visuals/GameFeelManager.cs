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
        // 关键：在 Inspector 里拖入一个字体文件！
        public Font DamageFont;

        private GameObject _screenCanvasObj;
        private Canvas _screenCanvas;

        // 简单的堆叠管理：记录每个单位头顶最近一次飘字的高度偏移
        private Dictionary<int, float> _stackOffsetMap = new Dictionary<int, float>();

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);

            EnsureScreenCanvas();
        }

        private void Update()
        {
            // 慢慢衰减堆叠高度
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
            // FIX 3: 使用 ScreenSpaceOverlay 解决模型遮挡问题
            _screenCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _screenCanvas.sortingOrder = 100; // 确保在最上层

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
                // Fallback: 尝试使用系统字体，避免 crash
                text.font = Font.CreateDynamicFontFromOSFont("Arial", 32);
            }
            // ------------------------

            text.text = content;
            text.color = color;
            text.fontSize = 32;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            // 加个描边更清晰
            var outline = go.AddComponent<Outline>();
            outline.effectColor = Color.black;
            outline.effectDistance = new Vector2(1, -1);

            // FIX 4: 简单的哈希映射来计算堆叠
            int posKey = Mathf.FloorToInt(worldPos.x * 10) + Mathf.FloorToInt(worldPos.z * 10) * 1000;
            if (!_stackOffsetMap.ContainsKey(posKey)) _stackOffsetMap[posKey] = 0f;

            float currentStackHeight = _stackOffsetMap[posKey];
            _stackOffsetMap[posKey] += 0.8f; // 每个新字往上顶一点

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
                timer += Time.unscaledDeltaTime; // 使用 unscaledDeltaTime 保证顿帧时飘字依然流畅
                float t = timer / duration;

                if (Camera.main != null)
                {
                    // 核心：实时转换坐标
                    Vector3 screenPos = Camera.main.WorldToScreenPoint(worldPos);

                    // 只有当物体在相机前方时才显示
                    if (screenPos.z > 0)
                    {
                        text.enabled = true;
                        // Y轴偏移 = 基础高度(防遮挡) + 堆叠高度 + 动画上升
                        screenPos.y += 100f + (startHeightOffset * 40f) + (t * 80f);
                        screenPos.x += randomX;
                        rect.position = screenPos;
                    }
                    else
                    {
                        text.enabled = false;
                    }
                }

                // 弹跳动画
                if (t < 0.2f)
                {
                    float s = Mathf.Lerp(0.5f, 1.2f, t / 0.2f);
                    rect.localScale = startScale * s;
                }
                else
                {
                    rect.localScale = startScale;
                }

                // 淡出
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
