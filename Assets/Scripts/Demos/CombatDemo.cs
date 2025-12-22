using UnityEngine;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Physics;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Input;
using ProjectHero.Core.Gameplay;
using ProjectHero.Visuals;
using ProjectHero.Core.Grid;
using System.Threading;

namespace ProjectHero.Demos
{
    public class CombatDemo : MonoBehaviour
    {
        public CombatUnit Player;
        public CombatUnit Enemy;
        public BattleTimeline Timeline;

        // Debug Only
        public float DebugTimeDisplay = 0f;

        private void Awake() { }

        void Start()
        {
            if (GridManager.Instance == null)
            {
                var gridObj = new GameObject("GridManager");
                var gridMgr = gridObj.AddComponent<GridManager>();
                gridMgr.groundLayer = 1 << 0;
            }
            if (InputManager.Instance == null)
            {
                var inputObj = new GameObject("InputManager");
                var inputMgr = inputObj.AddComponent<InputManager>();
                inputMgr.groundLayer = 1 << 0; inputMgr.unitLayer = 1 << 0;
            }
            if (Object.FindAnyObjectByType<TacticsController>() == null)
            {
                var tacticsObj = new GameObject("TacticsController");
                var controller = tacticsObj.AddComponent<TacticsController>();
                if (Timeline == null) Timeline = GetComponent<BattleTimeline>();
                if (Timeline == null) Timeline = gameObject.AddComponent<BattleTimeline>();
                controller.Timeline = Timeline;
            }

            Debug.Log("--- Starting Combat Demo (Ticks) ---");

            EnsureCollider(Player); EnsureCollider(Enemy);
            if (Player != null) Player.IsPlayerControlled = true;
            SetupVisuals();

            if (Enemy != null && ProjectHero.UI.UIManager.Instance != null && ProjectHero.UI.UIManager.Instance.TimelineUI != null)
                ProjectHero.UI.UIManager.Instance.TimelineUI.SetObservedUnit(Enemy);
        }

        void EnsureCollider(CombatUnit unit)
        {
            if (unit != null && unit.GetComponent<Collider>() == null)
            {
                var col = unit.gameObject.AddComponent<CapsuleCollider>();
                col.height = 2.0f; col.radius = 0.5f; col.center = Vector3.up * 1.0f;
            }
        }

        void SetupVisuals()
        {
            if (GridManager.Instance != null)
            {
                if (GridManager.Instance.GetComponent<GridVisuals>() == null) GridManager.Instance.gameObject.AddComponent<GridVisuals>();
                if (GridManager.Instance.GetComponent<UnitVolumeRenderer>() == null) GridManager.Instance.gameObject.AddComponent<UnitVolumeRenderer>();
            }
        }

        void Update()
        {
            if (Enemy != null && ProjectHero.UI.UIManager.Instance != null && ProjectHero.UI.UIManager.Instance.TimelineUI != null)
            {
                var timelineUI = ProjectHero.UI.UIManager.Instance.TimelineUI;
                if (timelineUI.ObservedUnit == null) timelineUI.SetObservedUnit(Enemy);
            }

            if (Timeline != null)
            {
                if (Input.GetKeyDown(KeyCode.P)) Timeline.SetPaused(!Timeline.Paused);
                // Çý¶¯Ê±¼äÖá
                Timeline.AdvanceTime(Time.deltaTime);
                DebugTimeDisplay = Timeline.CurrentTime;
            }
        }
    }
}
