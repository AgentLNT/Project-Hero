using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using ProjectHero.Core.Entities;

namespace ProjectHero.UI
{
    public class UnitStatusHUD : MonoBehaviour
    {
        private CombatUnit _targetUnit;

        // [新增] 缓存碰撞体或渲染器，用于每一帧计算高度
        private Collider _targetCol;
        private Renderer _targetRen;
        private float _fallbackHeight = 2.0f;

        [Header("Config")]
        public float VerticalPadding = 0.5f; // 头顶留白的距离

        [Header("Center")]
        public Image ActionRingImage;
        public Image StatusIconImage;
        public Sprite StaggerSprite;
        public Sprite KnockdownSprite;
        public Color WindupColor = new Color(1f, 0.5f, 0f);
        public Color RecoveryColor = new Color(0.8f, 0.8f, 0.8f);

        [Header("Bars")]
        public Image HealthBar;
        public Image StaminaBar;
        public Image AdrenalineBar;
        public Transform FocusPipContainer;
        public GameObject FocusPipPrefab;

        private List<Image> _focusPips = new List<Image>();
        private Camera _cam;
        private Canvas _canvas;

        public void Initialize(CombatUnit unit)
        {
            _targetUnit = unit;
            _cam = Camera.main;
            _canvas = GetComponent<Canvas>();
            if (_canvas != null) _canvas.worldCamera = _cam;

            // [新增] 缓存引用，不要只算一次，而是存下来每一帧算
            _targetCol = unit.GetComponent<Collider>();
            if (_targetCol == null)
            {
                _targetRen = unit.GetComponentInChildren<Renderer>();
            }

            // 如果实在没有任何体积信息，算一个保底高度
            if (_targetCol == null && _targetRen == null)
            {
                _fallbackHeight = 2.0f;
            }

            InitializeFocusPips();
        }

        private void InitializeFocusPips()
        {
            if (_targetUnit == null || FocusPipPrefab == null) return;
            foreach (Transform child in FocusPipContainer) Destroy(child.gameObject);
            _focusPips.Clear();
            int maxFocus = Mathf.FloorToInt(_targetUnit.MaxFocus);
            for (int i = 0; i < maxFocus; i++)
            {
                GameObject pip = Instantiate(FocusPipPrefab, FocusPipContainer);
                _focusPips.Add(pip.GetComponent<Image>());
            }
        }

        private void LateUpdate()
        {
            // [修复] 死亡检查：如果单位为空，或者单位被隐藏(SetActive false)，则销毁 UI
            if (_targetUnit == null || !_targetUnit.gameObject.activeInHierarchy)
            {
                Destroy(gameObject);
                return;
            }

            // [修复] 实时高度计算：每一帧都重新获取头顶位置
            float currentTopY = _targetUnit.transform.position.y + _fallbackHeight;

            if (_targetCol != null)
            {
                // bounds.max.y 会随模型缩放自动变化
                currentTopY = _targetCol.bounds.max.y;
            }
            else if (_targetRen != null)
            {
                currentTopY = _targetRen.bounds.max.y;
            }

            // 设置位置：水平跟随单位，垂直使用实时计算的最高点
            transform.position = new Vector3(
                _targetUnit.transform.position.x,
                currentTopY + VerticalPadding,
                _targetUnit.transform.position.z
            );

            // 始终朝向摄像机
            if (_cam != null) transform.rotation = _cam.transform.rotation;

            // 更新数值显示
            UpdateBar(HealthBar, _targetUnit.CurrentHealth, _targetUnit.MaxHealth);
            UpdateBar(StaminaBar, _targetUnit.CurrentStamina, _targetUnit.MaxStamina);
            UpdateBar(AdrenalineBar, _targetUnit.CurrentAdrenaline, 100f);
            UpdateFocusPips();
            UpdateActionRing();
        }

        private void UpdateActionRing()
        {
            if (_targetUnit.IsKnockedDown)
            {
                ShowStatusIcon(KnockdownSprite);
                ActionRingImage.fillAmount = 0;
                return;
            }
            if (_targetUnit.IsStaggered)
            {
                ShowStatusIcon(StaggerSprite);
                ActionRingImage.fillAmount = 0;
                return;
            }

            StatusIconImage.enabled = false;
            float progress = _targetUnit.GetActionProgress();

            if (_targetUnit.InWindup)
            {
                ActionRingImage.color = WindupColor;
                ActionRingImage.fillAmount = progress;
            }
            else if (_targetUnit.InRecovery)
            {
                ActionRingImage.color = RecoveryColor;
                ActionRingImage.fillAmount = 1.0f - progress;
            }
            else
            {
                ActionRingImage.fillAmount = 0f;
            }
        }

        private void UpdateBar(Image bar, float current, float max)
        {
            if (bar != null) bar.fillAmount = Mathf.Clamp01(current / Mathf.Max(1f, max));
        }

        private void UpdateFocusPips()
        {
            float currentFocus = _targetUnit.CurrentFocus;
            for (int i = 0; i < _focusPips.Count; i++)
            {
                float alpha = (i < currentFocus) ? 1f : 0.2f;
                var color = _focusPips[i].color;
                color.a = alpha;
                _focusPips[i].color = color;
            }
        }

        private void ShowStatusIcon(Sprite icon)
        {
            if (StatusIconImage != null && icon != null)
            {
                StatusIconImage.enabled = true;
                StatusIconImage.sprite = icon;
                StatusIconImage.transform.localScale = Vector3.one * (1f + Mathf.Sin(Time.time * 10f) * 0.2f);
            }
        }
    }
}
