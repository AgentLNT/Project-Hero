#if UNITY_EDITOR
using ProjectHero.Core.Actions;
using ProjectHero.Core.Actions.Intents;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;
using ProjectHero.Logic.Units;
using ProjectHero.Visuals;
using ProjectHero.UI;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectHero.Core.Entities
{
    /// <summary>
    /// 旧单位组件。
    ///
    /// 任务 03B：本组件的 <c>Update()</c> 保留旧资源衰减/恢复语义，但被登记为
    /// <strong>Legacy 从属写入者</strong>——它依赖顶层时钟推进，由
    /// <c>BattleRuntimeBootstrap</c> 按模式整体门控（Legacy/Shadow 启用、New 全部禁用）。
    /// 职责迁移（移除旧资源模型）属于任务 04–10。
    /// </summary>
    [DefaultExecutionOrder(ProjectHero.Core.Compatibility.Runtime.RuntimeCallbackRegistry.LegacyWriterExecutionOrder)]
    public class CombatUnit : MonoBehaviour
    {
        private bool _hasRegisteredUI = false;
        private bool _hasRegisteredGrid = false; 
        private BattleTimeline _timeline;

        private void Awake()
        {
            if (GetComponent<UnitMovement>() == null)
                gameObject.AddComponent<UnitMovement>();
        }

        [Header("Control")]
        public bool IsPlayerControlled = false;

        [Header("Grid State")]
        public GridPoint InitialGridPosition;
        public GridPoint GridPosition { get; private set; }

        [Header("Volume")]
        public UnitVolume UnitVolumeDefinition;
        public GridDirection FacingDirection = GridDirection.East;

        [Header("Actions")]
        public ActionLibrarySO ActionLibrary;

        [Header("Action Timing (Logic Ticks)")]
        public long CurrentStateStartTick;
        public int CurrentStateDurationTicks;

        public float GetActionProgress(long currentTimelineTick)
        {
            if (CurrentStateDurationTicks <= 0) return 0f;
            long elapsedTicks = currentTimelineTick - CurrentStateStartTick;
            return Mathf.Clamp01((float)elapsedTicks / CurrentStateDurationTicks);
        }

        public List<TrianglePoint> GetOccupiedTriangles()
        {
            if (UnitVolumeDefinition == null) return new List<TrianglePoint>();
            var relativeTriangles = UnitVolumeDefinition.GetVolumeFor(FacingDirection);
            var occupied = new List<TrianglePoint>();
            foreach (var rel in relativeTriangles)
                occupied.Add(new TrianglePoint(GridPosition.X + rel.X, GridPosition.Y + rel.Y, rel.T));
            return occupied;
        }

        public List<TrianglePoint> GetProjectedOccupancy(GridPoint targetPos, GridDirection targetFacing)
        {
            if (UnitVolumeDefinition == null) return new List<TrianglePoint>();
            var relativeTriangles = UnitVolumeDefinition.GetVolumeFor(targetFacing);
            var occupied = new List<TrianglePoint>();
            foreach (var rel in relativeTriangles)
                occupied.Add(new TrianglePoint(targetPos.X + rel.X, targetPos.Y + rel.Y, rel.T));
            return occupied;
        }

        // Stats
        public float Strength = 10f;
        public float Dexterity = 10f;
        public float Constitution = 10f;
        public float Wisdom = 10f;
        public float Intelligence = 10f;
        public float ArmorWeight = 10f;
        public float ArmorDefense = 0f;
        public float MagicResistance = 0f;
        public float CurrentHealth = 100f;
        public float CurrentStamina = 100f;
        public float CurrentFocus = 0f;
        public float CurrentAdrenaline = 0f;

        public float MaxFocus => Mathf.Max(3f, Wisdom * 0.5f);
        public float TotalMass => 50f + (Strength * 2f) + (Constitution * 2f) + ArmorWeight;
        public float Swiftness
        {
            get
            {
                float baseVal = (Dexterity * 0.75f) + (Strength * 0.25f);
                if (IsExhausted) return baseVal * 0.5f;
                return baseVal;
            }
        }
        public float ReactionWindow => Wisdom * 0.1f;
        public float MaxStamina => Constitution * 10f;
        public float MaxHealth => Constitution * 20f;

        public bool IsStaggered;
        public bool IsKnockedDown;
        public bool IsExhausted => CurrentStamina < MaxStamina * 0.2f;

        public bool IsActing;
        public bool InWindup;
        public bool InRecovery;
        public bool IsMoving;
        public bool IsRecoveringAction;

        public bool CanAct => !IsActing && !IsStaggered && !IsKnockedDown;

        // ——— 任务 04：新 Logic 状态机的兼容接缝（只转发，不建立第二套权威） ———

        /// <summary>
        /// 本组件对应的新 Logic 状态机（由任务 10 的 Bootstrap/UnityView 适配器注入）。
        ///
        /// <strong>可空</strong>：Legacy/Shadow 模式下新 Logic 不写权威状态，因此可能没有适配器；
        /// 未注入时 <see cref="LogicUnitStateValue"/> 与三个派生 bool 保持旧字段自己的值
        /// （不猜、不默认成 Idle）。
        /// </summary>
        public UnitStateMachine LogicStateMachine { get; private set; }

        /// <summary>
        /// 任务 10 的适配器注入点。它只保存引用，<strong>不</strong>写任何状态：
        /// 新 Logic 路径的权威状态只能由 <c>BattleSimulation.Step</c> 的阶段推进改写。
        /// </summary>
        public void BindLogicStateMachine(UnitStateMachine stateMachine)
        {
            LogicStateMachine = stateMachine;
        }

        /// <summary>是否已接入新 Logic 状态机（Legacy 模式下为 false）。</summary>
        public bool HasLogicStateMachine => LogicStateMachine != null;

        /// <summary>
        /// 新 Logic 路径的权威状态；未接入状态机时返回 <c>null</c>。
        /// </summary>
        public UnitState? LogicUnitStateOrNull => LogicStateMachine?.CurrentState;

        /// <summary>
        /// 当前控制状态（枚举值；未接入时返回 -1）。兼容只读视图，供 UI/HUD 与旧代码过渡使用。
        /// </summary>
        public int LogicUnitStateValue => LogicStateMachine != null ? (int)LogicStateMachine.CurrentState : -1;

        /// <summary>
        /// 新 Logic 路径的<strong>有限阻塞结束 Tick</strong>（无有限结束边界时为 null）。
        /// 只读转发，不参与任何提交/延期判断。
        /// </summary>
        public long? LogicBlockingUntilTick => LogicStateMachine?.BlockingUntilTick;

        /// <summary>
        /// 兼容属性：派生自状态机的"正在前摇"。
        ///
        /// <strong>不是</strong>第二套权威：它 <c>get</c> 只读状态机；<c>set</c> 仅为旧代码
        /// （<c>StateChangeIntent</c> 等迁移期写入者）保留写入旧字段的通道。
        /// 新 Logic 路径<strong>读取</strong>时应使用 <see cref="LogicUnitStateOrNull"/>。
        /// </summary>
        public bool WindupActive
            => LogicStateMachine != null ? LogicStateMachine.CurrentState == UnitState.Windup : InWindup;

        /// <summary>兼容属性：派生自状态机的"正在后摇"（语义见 <see cref="WindupActive"/>）。</summary>
        public bool RecoveryActive
            => LogicStateMachine != null ? LogicStateMachine.CurrentState == UnitState.Recovery : InRecovery;

        /// <summary>兼容属性：派生自状态机的"正在移动"（语义见 <see cref="WindupActive"/>）。</summary>
        public bool MovingActive
            => LogicStateMachine != null ? LogicStateMachine.CurrentState == UnitState.Moving : IsMoving;

        /// <summary>兼容属性：派生自状态机的"已硬直"（语义见 <see cref="WindupActive"/>）。</summary>
        public bool StaggeredActive
            => LogicStateMachine != null
                ? LogicStateMachine.CurrentState == UnitState.Staggered
                : IsStaggered;

        /// <summary>兼容属性：派生自状态机的"已击倒"（语义见 <see cref="WindupActive"/>）。</summary>
        public bool KnockedDownActive
            => LogicStateMachine != null
                ? LogicStateMachine.CurrentState == UnitState.KnockedDown
                : IsKnockedDown;

        /// <summary>兼容属性：派生自状态机的"已死亡"（终态；未接入时读旧生命值）。</summary>
        public bool DeadActive
            => LogicStateMachine != null
                ? LogicStateMachine.IsTerminal
                : CurrentHealth <= 0f;

        /// <summary>
        /// 旧状态名 → 新 <see cref="UnitState"/> 的<strong>唯一</strong>映射。
        /// 它只服务迁移期：让 <c>StateChangeIntent</c> 把同一事实同时写进状态机与旧字段，
        /// 而不是让两条路径各自演化出不同状态。未知名字返回 <c>null</c>（不猜默认值）。
        /// </summary>
        public static UnitState? MapLegacyStateName(string stateName)
        {
            switch (stateName)
            {
                case "Idle": return UnitState.Idle;
                case "Windup": return UnitState.Windup;
                case "Recovery": return UnitState.Recovery;
                case "Moving": return UnitState.Moving;
                case "Guarding": return UnitState.Guarding;
                case "Blocking": return UnitState.Blocking;
                case "Dodging": return UnitState.Dodging;
                case "Staggered": return UnitState.Staggered;
                case "KnockedDown": return UnitState.KnockedDown;
                case "Recovering": return UnitState.Recovering;
                case "Dead": return UnitState.Dead;
                default: return null;
            }
        }

        public void ResetActionState()
        {
            IsActing = false;
            InWindup = false;
            InRecovery = false;
            IsMoving = false;
            CurrentStateDurationTicks = 0;
        }

        public void SetGridPosition(GridPoint point)
        {
            if (GridManager.Instance != null && _hasRegisteredGrid)
            {
                GridManager.Instance.UnregisterOccupancy(GetOccupiedTriangles());
            }
            GridPosition = point;
            if (GridManager.Instance != null && _hasRegisteredGrid)
            {
                GridManager.Instance.RegisterOccupancy(this, GetOccupiedTriangles());
            }
        }

        public void SetFacingDirection(GridDirection newFacing)
        {
            if (FacingDirection == newFacing) return;
            if (GridManager.Instance != null && _hasRegisteredGrid)
            {
                GridManager.Instance.UnregisterOccupancy(GetOccupiedTriangles());
            }
            FacingDirection = newFacing;
            if (GridManager.Instance != null && _hasRegisteredGrid)
            {
                GridManager.Instance.RegisterOccupancy(this, GetOccupiedTriangles());
            }
        }

        private void Start()
        {
            _timeline = FindFirstObjectByType<BattleTimeline>();
            GridPosition = InitialGridPosition;

            TryRegisterToGrid();

            CurrentStamina = MaxStamina;
            CurrentHealth = MaxHealth;
        }

        private void TryRegisterToGrid()
        {
            if (_hasRegisteredGrid) return;
            if (GridManager.Instance != null)
            {
                GridManager.Instance.RegisterUnit(this);
                GridManager.Instance.RegisterOccupancy(this, GetOccupiedTriangles());

                Vector3 position = GridManager.Instance.GridToWorld(GridPosition);
                transform.position = GridManager.GetGroundPosition(position);

                _hasRegisteredGrid = true;
            }
        }

        private void Update()
        {
            if (!_hasRegisteredGrid) TryRegisterToGrid();

            if (!_hasRegisteredUI && HUDManager.Instance != null)
            {
                HUDManager.Instance.RegisterUnit(this);
                _hasRegisteredUI = true;
            }

            float dt = Time.deltaTime;
            if (_timeline != null && _timeline.Paused) dt = 0f;

            if (dt > 0f)
            {
                if (CurrentAdrenaline > 0)
                {
                    CurrentAdrenaline -= 5f * dt;
                    if (CurrentAdrenaline < 0) CurrentAdrenaline = 0;
                }
                if (CurrentStamina < MaxStamina)
                {
                    float regenRate = IsExhausted ? 2f : 5f;
                    CurrentStamina += regenRate * dt;
                    if (CurrentStamina > MaxStamina) CurrentStamina = MaxStamina;
                }
            }
        }

        private void OnDestroy()
        {
            if (GridManager.Instance != null)
            {
                GridManager.Instance.UnregisterOccupancy(GetOccupiedTriangles());
                GridManager.Instance.UnregisterUnit(this);
            }
        }

        public void OnImpact(BattleTimeline timeline, float impactVelocity, float damage, int pushDistance = 0, GridDirection pushDirection = GridDirection.East)
        {
            CurrentHealth -= damage;
            Debug.Log($"{name} took {damage:F1} damage! HP: {CurrentHealth}/{MaxHealth}");

            if (CurrentHealth <= 0)
            {
                Debug.Log($"{name} has been DEFEATED!");
                if (timeline != null) timeline.CancelEvents(this);
                if (GridManager.Instance != null)
                {
                    GridManager.Instance.UnregisterOccupancy(GetOccupiedTriangles());
                    GridManager.Instance.UnregisterUnit(this);
                }
                gameObject.SetActive(false);
                return;
            }

            if (pushDistance > 0)
            {
                ActionScheduler.ScheduleKnockback(timeline, this, pushDirection, pushDistance, impactVelocity);
            }
        }
    }
}

#endif
