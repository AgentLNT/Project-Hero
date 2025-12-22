using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Timeline;

namespace ProjectHero.UI
{
    public class UnitStatusHUD : MonoBehaviour
    {
        private CombatUnit _targetUnit;
        private BattleTimeline _timeline;
        private Collider _targetCol;
        private Renderer _targetRen;
        private float _fallbackHeight = 2.0f;

        [Header("Config")]
        public float VerticalPadding = 0.5f;
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
            _timeline = FindFirstObjectByType<BattleTimeline>();
            _cam = Camera.main;
            _canvas = GetComponent<Canvas>();
            if (_canvas != null) _canvas.worldCamera = _cam;
            _targetCol = unit.GetComponent<Collider>();
            if (_targetCol == null) _targetRen = unit.GetComponentInChildren<Renderer>();
            if (_targetCol == null && _targetRen == null) _fallbackHeight = 2.0f;
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
            if (_targetUnit == null || !_targetUnit.gameObject.activeInHierarchy) { Destroy(gameObject); return; }
            float currentTopY = _targetUnit.transform.position.y + _fallbackHeight;
            if (_targetCol != null) currentTopY = _targetCol.bounds.max.y;
            else if (_targetRen != null) currentTopY = _targetRen.bounds.max.y;

            transform.position = new Vector3(_targetUnit.transform.position.x, currentTopY + VerticalPadding, _targetUnit.transform.position.z);
            if (_cam != null) transform.rotation = _cam.transform.rotation;

            UpdateBar(HealthBar, _targetUnit.CurrentHealth, _targetUnit.MaxHealth);
            UpdateBar(StaminaBar, _targetUnit.CurrentStamina, _targetUnit.MaxStamina);
            UpdateBar(AdrenalineBar, _targetUnit.CurrentAdrenaline, 100f);
            UpdateFocusPips();
            UpdateActionRing();
        }

        private void UpdateActionRing()
        {
            if (_targetUnit.IsKnockedDown) { ShowStatusIcon(KnockdownSprite); ActionRingImage.fillAmount = 0; return; }
            if (_targetUnit.IsStaggered) { ShowStatusIcon(StaggerSprite); ActionRingImage.fillAmount = 0; return; }

            StatusIconImage.enabled = false;

            // สนำร Tick 
            long currentTick = _timeline != null ? _timeline.CurrentTick : 0;
            float progress = _targetUnit.GetActionProgress(currentTick);

            if (_targetUnit.InWindup) { ActionRingImage.color = WindupColor; ActionRingImage.fillAmount = progress; }
            else if (_targetUnit.InRecovery) { ActionRingImage.color = RecoveryColor; ActionRingImage.fillAmount = 1.0f - progress; }
            else { ActionRingImage.fillAmount = 0f; }
        }

        private void UpdateBar(Image bar, float current, float max) { if (bar != null) bar.fillAmount = Mathf.Clamp01(current / Mathf.Max(1f, max)); }

        private void UpdateFocusPips()
        {
            float currentFocus = _targetUnit.CurrentFocus;
            for (int i = 0; i < _focusPips.Count; i++)
            {
                float alpha = (i < currentFocus) ? 1f : 0.2f;
                var color = _focusPips[i].color; color.a = alpha; _focusPips[i].color = color;
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
