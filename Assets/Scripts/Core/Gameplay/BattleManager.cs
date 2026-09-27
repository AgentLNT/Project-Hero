using UnityEngine;
using UnityEngine.SceneManagement;
using ProjectHero.Core.Entities;
using System.Collections.Generic;
using ProjectHero.Core.Grid;

namespace ProjectHero.Core.Gameplay
{
    /// <summary>
    /// 旧胜负轮询器。任务 03B：<c>Update()</c> 保留旧行为（每帧 FindObjectsByType 轮询 +
    /// <c>Time.timeScale = 0.2</c> 慢放），但被登记为 <strong>Legacy 从属写入者</strong>，
    /// 由 <c>BattleRuntimeBootstrap</c> 按模式整体门控（New 全部禁用）。
    /// 胜负迁移到唯一 Finalizer 属任务 04/10。
    /// </summary>
    [DefaultExecutionOrder(ProjectHero.Core.Compatibility.Runtime.RuntimeCallbackRegistry.LegacyWriterExecutionOrder)]
    public class BattleManager : MonoBehaviour
    {
        public static BattleManager Instance { get; private set; }

        private bool _battleEnded = false;

        /// <summary>
        /// 旧胜负标志的<strong>只读可观察入口</strong>（任务 04 新增）。
        ///
        /// 用途：让 Shadow 比较能把 <c>battleEnd.isEnded</c> 与 <c>battleEnd.resultCode</c>
        /// 从旧运行的<strong>真实活动状态</strong>读出，而不是继续登记为"不可采样"
        /// （03B-交接记录 §23.2 的任务 04 前置条件）。
        ///
        /// 它只暴露事实，<strong>不新增写入面</strong>：<c>EndBattle</c> 仍是唯一写入点。
        /// 旧胜负语义（每帧轮询 + timeScale 慢放）本身不变，属任务 10 的迁移范围。
        /// </summary>
        public bool BattleEnded => _battleEnded;

        /// <summary>
        /// 旧结束显示文本的<strong>只读可观察入口</strong>（任务 04 新增）。
        ///
        /// 与 <see cref="BattleEnded"/> 同属"暴露既有事实、不新增写入面"：
        /// <c>EndBattle</c> 仍是唯一写入点。Shadow 观测用它把旧结束事实映射成
        /// 与新内核同域的结果码（旧侧没有结果码字段）。
        /// </summary>
        public string EndMessage => _endMessage;

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
