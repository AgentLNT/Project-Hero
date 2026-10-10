#if UNITY_EDITOR
using UnityEngine;
using ProjectHero.Core.Entities;
using System.Collections.Generic;

namespace ProjectHero.UI
{
    public class HUDManager : MonoBehaviour, ProjectHero.Core.Compatibility.Runtime.ILegacyVisualGate
    {
        public static HUDManager Instance { get; private set; }
        public GameObject UnitHUDPrefab; 
        private readonly List<GameObject> _generated = new List<GameObject>();
        private readonly Dictionary<GameObject, bool> _savedVisibility = new Dictionary<GameObject, bool>();
        private bool _newViewActive;
        public void SetNewViewActive(bool active)
        {
            if (_newViewActive == active) return;
            _newViewActive = active;
            foreach (var root in _generated)
            {
                if (root == null) continue;
                if (active) { _savedVisibility[root] = root.activeSelf; root.SetActive(false); }
                else if (_savedVisibility.TryGetValue(root, out bool visible)) root.SetActive(visible);
            }
            if (!active) _savedVisibility.Clear();
        }

        private void Awake()
        {
            if (Instance == null) { Instance = this; ProjectHero.Core.Compatibility.Runtime.LegacyVisualRegistry.Register(this); }
            else Destroy(gameObject);
        }

        public void RegisterUnit(CombatUnit unit)
        {
            if (UnitHUDPrefab == null)
            {
                Debug.LogWarning("[HUDManager] UnitHUDPrefab is missing!");
                return;
            }

            GameObject go = Instantiate(UnitHUDPrefab, transform);
            go.name = $"HUD_{unit.name}";
            _generated.Add(go);

            var hud = go.GetComponent<UnitStatusHUD>();
            if (hud != null)
            {
                hud.Initialize(unit);
            }
            if (_newViewActive) { _savedVisibility[go] = go.activeSelf; go.SetActive(false); }
        }
        private void OnDestroy()
        {
            ProjectHero.Core.Compatibility.Runtime.LegacyVisualRegistry.Unregister(this);
            if (Instance == this) Instance = null;
        }
    }
}

#endif
