using UnityEngine;
using ProjectHero.Core.Compatibility.Runtime;
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
    /// <summary>
    /// 旧战斗演示入口 —— 任务 03B 之后是「被调用适配器」，不再是顶层时钟。
    ///
    /// 改造要点（任务包「必须产出」4；不变量 21）：
    /// 1) 不再声明 Update()：旧时间线唯一的调用者是 BattleRuntimeBootstrap 的 Legacy/Shadow 分支。
    /// 2) 推进职责提取为显式 AdvanceFrame(BattleFrameDelta)；帧时间由 Bootstrap 传入，
    ///    适配器自己不读 Time.deltaTime，避免形成第二个时间来源。
    /// 3) 玩法语义原样保留：P 键暂停切换、Timeline.AdvanceTime 的受时标缩放时间、
    ///    事件回调与全部场景写入都不变。
    /// 4) Start() 里的初始化写入保留，但本组件登记为 Legacy 从属写入者，
    ///    New 模式下由 Bootstrap 整体禁用（enabled = false）。
    /// </summary>
    [DefaultExecutionOrder(RuntimeCallbackRegistry.LegacyWriterExecutionOrder)]
    public class CombatDemo : MonoBehaviour, IBattleFrameAdapter, ILegacyPauseAware, ILegacyAdvanceTimeObservable
    {
        public CombatUnit Player;
        public CombatUnit Enemy;
        public BattleTimeline Timeline;

        // Debug Only
        public float DebugTimeDisplay = 0f;

        private BattleRuntimeContext _context;
        private bool _initialized;
        private bool _stopped;
        private int _advanceCallCount;
        private int _stopCallCount;

        // ---- IBattleFrameAdapter：契约程序集拥有接口，Legacy 具体类型留在本程序集实现 ----

        public string AdapterName => nameof(CombatDemo);

        public string CallbackSite => "ProjectHero.Demos.CombatDemo.Update";

        /// <summary>恒为 false：改造后本适配器不再拥有任何自主顶层推进。</summary>
        public bool OwnsAutonomousUpdate => false;

        public MonoBehaviour GateTarget => this;

        public bool IsInitialized => _initialized;

        public bool IsStopped => _stopped;

        public int AdvanceCallCount => _advanceCallCount;

        public int StopCallCount => _stopCallCount;

        /// <summary>旧时间线的暂停语义（用户暂停或系统暂停），供 Bootstrap 观测。</summary>
        public bool IsPaused => Timeline != null && Timeline.Paused;

        /// <summary>
        /// 旧组件自己累计的 AdvanceTime 调用数（唯一权威来源）。
        /// 它让「New 模式旧推进调用为 0」可以被直接证明，而不是靠日志推断。
        /// </summary>
        public int AdvanceTimeCallCount => Timeline != null ? Timeline.TotalAdvanceTimeCalls : 0;

        public void Initialize(BattleRuntimeContext context)
        {
            if (_initialized) return;
            _context = context;
            _initialized = true;
            _stopped = false;

            // 旧启动链的初始化写入保持原样：Start() 已经做过兜底创建与引用解析，
            // 这里只补上「Timeline 必须存在」的显式校验。
            if (Timeline == null) Timeline = GetComponent<BattleTimeline>();
        }

        /// <summary>推进一帧。只允许 BattleRuntimeBootstrap 调用。</summary>
        public void AdvanceFrame(BattleFrameDelta delta)
        {
            if (!_initialized || _stopped) return;

            _advanceCallCount++;

            // 旧表现行为：TimelineUI 观察对象兜底（原 Update 第一段，只读表现，不写逻辑）。
            if (Enemy != null && ProjectHero.UI.UIManager.Instance != null && ProjectHero.UI.UIManager.Instance.TimelineUI != null)
            {
                var timelineUI = ProjectHero.UI.UIManager.Instance.TimelineUI;
                if (timelineUI.ObservedUnit == null) timelineUI.SetObservedUnit(Enemy);
            }

            if (Timeline == null) return;

            // 暂停切换保持旧语义：P 键在 Legacy 适配器内处理（不改变时间缩放的帧速率）。
            if (Input.GetKeyDown(KeyCode.P)) Timeline.SetPaused(!Timeline.Paused);

            if (delta.IsPaused) return;

            // 时间来源语义保持不变：Legacy 段使用受 timeScale 缩放的帧时间，
            // 因此慢动作/顿帧仍然影响旧逻辑 Tick 率——这是 03B 刻意保留的可见基线。
            Timeline.AdvanceTime(delta.DeltaTime);
            DebugTimeDisplay = Timeline.CurrentTime;

            if (_context != null && _context.Ledger != null)
                _context.Ledger.RecordLegacyWriterAdvanceCall(CallbackSite);
        }

        /// <summary>停止本场战斗（幂等）。旧时间线不因停止而销毁，只停止被推进。</summary>
        public void StopBattle(string reason)
        {
            _stopCallCount++;
            if (_stopped) return;

            _stopped = true;
            if (Timeline != null) Timeline.SetSystemPaused(true);
            _ = reason;
        }

        /// <summary>
        /// 为新一场战斗复位（可回切语义）。
        ///
        /// 同一个 Bootstrap 可以在 StopBattle + ReleaseBattle 之后以另一模式重新开局；
        /// 若不复位，上一场遗留的 _stopped = true 会让新战斗"开局即停止"
        /// （Bootstrap 的 Adapters.Legacy.IsStopped 会保持 true）。
        /// 本方法只复位"每场战斗"的状态：旧时间线的累计计数与全部场景对象保持原样。
        /// </summary>
        public void ResetForNewBattle()
        {
            _initialized = false;
            _stopped = false;
            _context = null;
            _advanceCallCount = 0;
            _stopCallCount = 0;

            // 第二收尾轮 R2（缺陷 D2）：StopBattle 会给旧时间线设置「系统暂停」，
            // 而重新装配新战斗时若不清除，重开后的旧时钟**永不推进**，
            // 但 AdvanceTime 调用计数仍逐帧增长（既有用例只断言计数，因此掩盖了它）。
            // 「可回切」必须包含「重开后能真正推进」：这里清除本场战斗施加的系统暂停。
            // 恢复语义：
            //   1) 系统暂停 = 由战斗生命周期施加（StopBattle 置位）-> 重开时必须清除；
            //   2) 用户暂停（P 键 / UI）是玩家自己的意图，不在这里改写；
            //   3) 战斗之间的顶层暂停由 BattleRuntimeBootstrap.SetPaused 负责，
            //      它不进入旧时间线，因此也不会残留到下一场。
            if (Timeline == null) Timeline = GetComponent<BattleTimeline>();
            if (Timeline != null && Timeline.SystemPaused) Timeline.SetSystemPaused(false);
        }

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

            // 时间线兜底引用：改造前 Update 通过 Timeline 字段推进，字段为空时旧系统会静默不推进；
            // 为保持「Legacy 仍是被调用的唯一旧推进路径」，这里补一次同 GameObject 上的解析。
            if (Timeline == null) Timeline = GetComponent<BattleTimeline>();

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
    }
}
