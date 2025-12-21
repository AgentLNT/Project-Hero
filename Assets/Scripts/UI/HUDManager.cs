using UnityEngine;
using ProjectHero.Core.Entities;
using System.Collections.Generic;

namespace ProjectHero.UI
{
    public class HUDManager : MonoBehaviour
    {
        public static HUDManager Instance { get; private set; }
        public GameObject UnitHUDPrefab; 

        private void Awake()
        {
            if (Instance == null) Instance = this;
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

            var hud = go.GetComponent<UnitStatusHUD>();
            if (hud != null)
            {
                hud.Initialize(unit);
            }
        }
    }
}
