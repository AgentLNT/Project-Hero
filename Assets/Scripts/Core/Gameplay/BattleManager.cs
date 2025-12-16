using UnityEngine;
using UnityEngine.SceneManagement;
using ProjectHero.Core.Entities;
using System.Collections.Generic;
using ProjectHero.Core.Grid;

namespace ProjectHero.Core.Gameplay
{
    public class BattleManager : MonoBehaviour
    {
        public static BattleManager Instance { get; private set; }

        private bool _battleEnded = false;
        private string _endMessage = "";

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        private void Update()
        {
            if (_battleEnded)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.R))
                {
                    RestartGame();
                }
                return;
            }

            CheckWinCondition();
        }

        private void CheckWinCondition()
        {
            if (GridManager.Instance == null) return;

            var units = FindObjectsByType<CombatUnit>(FindObjectsSortMode.None);

            bool playerAlive = false;
            bool enemyAlive = false;

            foreach (var u in units)
            {
                if (!u.gameObject.activeInHierarchy || u.CurrentHealth <= 0) continue;

                if (u.IsPlayerControlled) playerAlive = true;
                else enemyAlive = true;
            }

            if (!playerAlive)
            {
                EndBattle("DEFEAT\nPress 'R' to Restart");
            }
            else if (!enemyAlive)
            {
                EndBattle("VICTORY\nPress 'R' to Restart");
            }
        }

        private void EndBattle(string msg)
        {
            _battleEnded = true;
            _endMessage = msg;
            Debug.Log($"Battle Ended: {msg}");

            Time.timeScale = 0.2f;
        }

        public void RestartGame()
        {
            Time.timeScale = 1.0f;
            string currentSceneName = SceneManager.GetActiveScene().name;
            SceneManager.LoadScene(currentSceneName);
        }

        private void OnGUI()
        {
            if (_battleEnded)
            {
                GUIStyle style = new GUIStyle();
                style.fontSize = 60;
                style.fontStyle = FontStyle.Bold;
                style.normal.textColor = _endMessage.Contains("VICTORY") ? Color.yellow : Color.red;
                style.alignment = TextAnchor.MiddleCenter;

                float w = Screen.width;
                float h = Screen.height;
                GUI.Label(new Rect(0, 0, w, h), _endMessage, style);
            }
        }
    }
}
