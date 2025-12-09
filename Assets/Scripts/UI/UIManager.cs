using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Gameplay;
using ProjectHero.Core.Actions;

namespace ProjectHero.UI
{
    public class UIManager : MonoBehaviour
    {
        public static UIManager Instance { get; private set; }

        [Header("References")]
        public GameObject ActionPanel; // The parent container (Horizontal Layout Group)
        public ActionButton ButtonPrefab;
        public TacticsController Controller;

        private List<ActionButton> _spawnedButtons = new List<ActionButton>();

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        private void Start()
        {
            if (Controller == null) Controller = FindFirstObjectByType<TacticsController>();
            
            // Hide panel initially
            if (ActionPanel != null) ActionPanel.SetActive(false);
        }

        public void OnUnitSelected(CombatUnit unit)
        {
            if (ActionPanel == null || ButtonPrefab == null) return;

            ActionPanel.SetActive(true);
            ClearButtons();

            // 1. Add "Move" Button
            CreateButton("Move", null, (a) => Controller.SelectMove());

            // 2. Add Action Buttons
            if (unit.ActionLibrary != null)
            {
                foreach (var entry in unit.ActionLibrary.Actions)
                {
                    CreateButton(entry.Data.Name, entry.Data, (a) => Controller.SelectAction(a));
                }
            }
            
            // 3. Add "Wait" Button (End Turn)
            // CreateButton("Wait", null, (a) => Debug.Log("Wait clicked"));
        }

        public void OnUnitDeselected()
        {
            if (ActionPanel != null) ActionPanel.SetActive(false);
            ClearButtons();
        }

        private void CreateButton(string name, Action action, System.Action<Action> callback)
        {
            var btnObj = Instantiate(ButtonPrefab, ActionPanel.transform);
            var btnScript = btnObj.GetComponent<ActionButton>();
            if (btnScript != null)
            {
                btnScript.Setup(name, action, callback);
                _spawnedButtons.Add(btnScript);
            }
        }

        private void ClearButtons()
        {
            foreach (var btn in _spawnedButtons)
            {
                if (btn != null) Destroy(btn.gameObject);
            }
            _spawnedButtons.Clear();
        }
    }
}