#if UNITY_EDITOR
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
    /// ��ս����ʾ��� ���� ���� 03B ֮���ǡ����������������������Ƕ���ʱ�ӡ�
    ///
    /// ����Ҫ�㣨����������������4�������� 21����
    /// 1) �������� Update()����ʱ����Ψһ�ĵ������� BattleRuntimeBootstrap �� Legacy/Shadow ��֧��
    /// 2) �ƽ�ְ����ȡΪ��ʽ AdvanceFrame(BattleFrameDelta)��֡ʱ���� Bootstrap ���룬
    ///    �������Լ����� Time.deltaTime�������γɵڶ���ʱ����Դ��
    /// 3) �淨����ԭ��������P ����ͣ�л���Timeline.AdvanceTime ����ʱ������ʱ�䡢
    ///    �¼��ص���ȫ������д�붼���䡣
    /// 4) Start() ��ĳ�ʼ��д�뱣������������Ǽ�Ϊ Legacy ����д���ߣ�
    ///    New ģʽ���� Bootstrap ������ã�enabled = false����
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

        // ---- IBattleFrameAdapter����Լ����ӵ�нӿڣ�Legacy �����������ڱ�����ʵ�� ----

        public string AdapterName => nameof(CombatDemo);

        public string CallbackSite => "ProjectHero.Demos.CombatDemo.Update";

        /// <summary>��Ϊ false�����������������ӵ���κ����������ƽ���</summary>
        public bool OwnsAutonomousUpdate => false;

        public MonoBehaviour GateTarget => this;

        public bool IsInitialized => _initialized;

        public bool IsStopped => _stopped;

        public int AdvanceCallCount => _advanceCallCount;

        public int StopCallCount => _stopCallCount;

        /// <summary>��ʱ���ߵ���ͣ���壨�û���ͣ��ϵͳ��ͣ������ Bootstrap �۲⡣</summary>
        public bool IsPaused => Timeline != null && Timeline.Paused;

        /// <summary>
        /// ������Լ��ۼƵ� AdvanceTime ��������ΨһȨ����Դ����
        /// ���á�New ģʽ���ƽ�����Ϊ 0�����Ա�ֱ��֤���������ǿ���־�ƶϡ�
        /// </summary>
        public int AdvanceTimeCallCount => Timeline != null ? Timeline.TotalAdvanceTimeCalls : 0;

        public void Initialize(BattleRuntimeContext context)
        {
            if (_initialized) return;
            _context = context;
            _initialized = true;
            _stopped = false;

            // ���������ĳ�ʼ��д�뱣��ԭ����Start() �Ѿ��������״��������ý�����
            // ����ֻ���ϡ�Timeline ������ڡ�����ʽУ�顣
            if (Timeline == null) Timeline = GetComponent<BattleTimeline>();
        }

        /// <summary>�ƽ�һ֡��ֻ���� BattleRuntimeBootstrap ���á�</summary>
        public void AdvanceFrame(BattleFrameDelta delta)
        {
            if (!_initialized || _stopped) return;

            _advanceCallCount++;

            // �ɱ�����Ϊ��TimelineUI �۲���󶵵ף�ԭ Update ��һ�Σ�ֻ�����֣���д�߼�����
            if (Enemy != null && ProjectHero.UI.UIManager.Instance != null && ProjectHero.UI.UIManager.Instance.TimelineUI != null)
            {
                var timelineUI = ProjectHero.UI.UIManager.Instance.TimelineUI;
                if (timelineUI.ObservedUnit == null) timelineUI.SetObservedUnit(Enemy);
            }

            if (Timeline == null) return;

            // ��ͣ�л����־����壺P ���� Legacy �������ڴ��������ı�ʱ�����ŵ�֡���ʣ���
            if (Input.GetKeyDown(KeyCode.P)) Timeline.SetPaused(!Timeline.Paused);

            if (delta.IsPaused) return;

            // ʱ����Դ���屣�ֲ��䣺Legacy ��ʹ���� timeScale ���ŵ�֡ʱ�䣬
            // ���������/��֡��ȻӰ����߼� Tick �ʡ������� 03B ���Ᵽ���Ŀɼ����ߡ�
            Timeline.AdvanceTime(delta.DeltaTime);
            DebugTimeDisplay = Timeline.CurrentTime;

            if (_context != null && _context.Ledger != null)
                _context.Ledger.RecordLegacyWriterAdvanceCall(CallbackSite);
        }

        /// <summary>ֹͣ����ս�����ݵȣ�����ʱ���߲���ֹͣ�����٣�ֹֻͣ���ƽ���</summary>
        public void StopBattle(string reason)
        {
            _stopCallCount++;
            if (_stopped) return;

            _stopped = true;
            if (Timeline != null) Timeline.SetSystemPaused(true);
            _ = reason;
        }

        /// <summary>
        /// Ϊ��һ��ս����λ���ɻ������壩��
        ///
        /// ͬһ�� Bootstrap ������ StopBattle + ReleaseBattle ֮������һģʽ���¿��֣�
        /// ������λ����һ�������� _stopped = true ������ս��"���ּ�ֹͣ"
        /// ��Bootstrap �� Adapters.Legacy.IsStopped �ᱣ�� true����
        /// ������ֻ��λ"ÿ��ս��"��״̬����ʱ���ߵ��ۼƼ�����ȫ���������󱣳�ԭ����
        /// </summary>
        public void ResetForNewBattle()
        {
            _initialized = false;
            _stopped = false;
            _context = null;
            _advanceCallCount = 0;
            _stopCallCount = 0;

            // �ڶ���β�� R2��ȱ�� D2����StopBattle �����ʱ�������á�ϵͳ��ͣ����
            // ������װ����ս��ʱ����������ؿ���ľ�ʱ��**�����ƽ�**��
            // �� AdvanceTime ���ü�������֡��������������ֻ���Լ���������ڸ���������
            // ���ɻ��С�����������ؿ����������ƽ����������������ս��ʩ�ӵ�ϵͳ��ͣ��
            // �ָ����壺
            //   1) ϵͳ��ͣ = ��ս����������ʩ�ӣ�StopBattle ��λ��-> �ؿ�ʱ���������
            //   2) �û���ͣ��P �� / UI��������Լ�����ͼ�����������д��
            //   3) ս��֮��Ķ�����ͣ�� BattleRuntimeBootstrap.SetPaused ����
            //      ���������ʱ���ߣ����Ҳ�����������һ����
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

            // ʱ���߶������ã�����ǰ Update ͨ�� Timeline �ֶ��ƽ����ֶ�Ϊ��ʱ��ϵͳ�ᾲĬ���ƽ���
            // Ϊ���֡�Legacy ���Ǳ����õ�Ψһ���ƽ�·���������ﲹһ��ͬ GameObject �ϵĽ�����
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

#endif
