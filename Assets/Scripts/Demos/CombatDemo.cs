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
        public float timer = 0f;

        private void Awake()
        {
        }

        void Start()
        {
            timer = 0;

            // Ensure GridManager exists (Must be before Start)
            if (GridManager.Instance == null)
            {
                var gridObj = new GameObject("GridManager");
                var gridMgr = gridObj.AddComponent<GridManager>();
                // CRITICAL: Set LayerMasks for dynamic instance (Default Layer = 0)
                // Without this, Raycasts (GetGroundPosition) will fail.
                gridMgr.groundLayer = 1 << 0;
            }

            // Ensure InputManager exists
            if (InputManager.Instance == null)
            {
                var inputObj = new GameObject("InputManager");
                var inputMgr = inputObj.AddComponent<InputManager>();
                // CRITICAL: Set LayerMasks for dynamic instance
                // Without this, Mouse Hover and Click will hit nothing.
                inputMgr.groundLayer = 1 << 0; // Default Layer
                inputMgr.unitLayer = 1 << 0;   // Default Layer
            }

            // Ensure TacticsController exists (The Brain)
            if (Object.FindAnyObjectByType<TacticsController>() == null)
            {
                var tacticsObj = new GameObject("TacticsController");
                var controller = tacticsObj.AddComponent<TacticsController>();
                // Timeline might not be assigned yet if it's on this object
                if (Timeline == null) Timeline = GetComponent<BattleTimeline>();
                if (Timeline == null) Timeline = gameObject.AddComponent<BattleTimeline>();
                controller.Timeline = Timeline;
            }

            Debug.Log("--- Starting Combat Demo ---");

            // Ensure Units have Colliders for clicking
            EnsureCollider(Player);
            EnsureCollider(Enemy);

            if (Player != null) Player.IsPlayerControlled = true;

            //// Basic enemy AI
            //if (Enemy != null)
            //{
            //    var ai = Enemy.GetComponent<EnemyAIController>();
            //    if (ai == null) ai = Enemy.gameObject.AddComponent<EnemyAIController>();
            //    ai.Timeline = Timeline;
            //    ai.ControlledUnit = Enemy;
            //    ai.TargetUnit = Player;
            //}

            // Setup Visuals if missing
            SetupVisuals();

            // Auto-set Observed unit to Enemy so the Observed lane shows their actions immediately.
            if (Enemy != null && ProjectHero.UI.UIManager.Instance != null && ProjectHero.UI.UIManager.Instance.TimelineUI != null)
            {
                ProjectHero.UI.UIManager.Instance.TimelineUI.SetObservedUnit(Enemy);
            }
        }

        void EnsureCollider(CombatUnit unit)
        {
            if (unit != null && unit.GetComponent<Collider>() == null)
            {
                var col = unit.gameObject.AddComponent<CapsuleCollider>();
                col.height = 2.0f;
                col.radius = 0.5f;
                col.center = Vector3.up * 1.0f;
            }
        }

        void SetupVisuals()
        {
            // Add GridVisuals to GridManager if missing
            if (GridManager.Instance != null)
            {
                if (GridManager.Instance.GetComponent<GridVisuals>() == null)
                    GridManager.Instance.gameObject.AddComponent<GridVisuals>();
                
                // Add UnitVolumeRenderer to GridManager if missing (Centralized Volume Rendering)
                if (GridManager.Instance.GetComponent<UnitVolumeRenderer>() == null)
                    GridManager.Instance.gameObject.AddComponent<UnitVolumeRenderer>();
            }
        }

        void Update()
        {
            // Fallback: Ensure ObservedUnit is set (in case UIManager wasn't ready during Start).
            if (Enemy != null && ProjectHero.UI.UIManager.Instance != null && ProjectHero.UI.UIManager.Instance.TimelineUI != null)
            {
                var timelineUI = ProjectHero.UI.UIManager.Instance.TimelineUI;
                if (timelineUI.ObservedUnit == null)
                {
                    timelineUI.SetObservedUnit(Enemy);
                }
            }

            if (Timeline != null && Input.GetKeyDown(KeyCode.P))
            {
                Timeline.SetPaused(!Timeline.Paused);
                Debug.Log($"[Demo] Timeline Paused = {Timeline.Paused}");
            }

            if (Timeline != null && Timeline.Paused)
            {
                // Simulation paused; UI and editing still work.
                return;
            }

            timer += Time.deltaTime;
            Timeline.AdvanceTime(timer);
        }
    }
}
