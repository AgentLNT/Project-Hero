using ProjectHero.Core.Actions;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Grid;
using ProjectHero.Core.Pathfinding;
using ProjectHero.Core.Timeline;
using ProjectHero.Core.Compatibility.Runtime.Input;
using ProjectHero.Logic.Ids;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectHero.UI.Timeline
{
    public class TimelineEditorUI : MonoBehaviour
    {
        public event System.Action PlacementCommitted;
        public event System.Action PlacementCancelled;

        [Header("Refs")]
        public BattleTimeline Timeline;
        public Canvas Canvas;
        public Font UiFont; // Optional: Drag a .ttf here if you want custom font

        [Header("Lanes")]
        public RectTransform PlayerLane;
        public RectTransform ObservedLane;

        [Header("Time Ruler")]
        public RectTransform RulerArea;

        [Header("Units")]
        public CombatUnit PlayerUnit;
        public CombatUnit ObservedUnit;

        [Header("Mapping")]
        public float PixelsPerSecond = 240f;
        public float MinBlockWidthPx = 24f;

        [Header("Colors")]
        public Color MoveColor = new Color(0.25f, 0.55f, 1.00f, 1f);
        public Color AttackColor = new Color(1.00f, 0.30f, 0.30f, 1f);
        public Color BlockColor = new Color(1.00f, 0.75f, 0.15f, 1f);
        public Color DodgeColor = new Color(0.25f, 0.95f, 0.65f, 1f);
        public Color RecoverColor = new Color(0.75f, 0.65f, 1.00f, 1f);

        [Header("Depth By Length")]
        public float DeepenAtSeconds = 3.0f;
        public float MaxDarkenFactor = 0.45f;

        [Header("Snapping")]
        public float SnapThresholdSeconds = 0.15f;

        // ─────────────────────────────────────────────────────────────────────
        // 任务 09 接入点（B 流）：输入模式 + 原子命令提交
        //
        // 这里**不**保存任何逻辑权威状态：端口只提供"当前 Tick / 预期修订号 /
        // 可编辑计划 / 反应机会 / Dodge 目的格合法性"这些只读投影，
        // 以及唯一写入口 SubmitCommand(CommandRequest)。
        // 没有端口时，删除动作**拒绝执行**（绝不回退到直接删计划/意图/移动段/预留）。
        //
        // B2 补三条结构性约束（见各成员注释）：
        // ① `LegacyTimelineWritesEnabled`：端口接线后旧时间线写入（CancelGroup /
        //    ReserveGroupId / placement.Schedule）在**代码上**不可达，不再依赖
        //    "_playerBlocks 恰好为空"这一数据巧合；
        // ② 块标识 = Logic `ActionPlanSnapshot.ActionPlanId`（唯一来源
        //    `IViewLogicPort.EditablePlansOf`），不是 `ReserveGroupId()`；
        // ③ 秒 / Tick 只有 `ViewTickConverter` 一个换算点（tick 率取自
        //    旧时间线的唯一常量 `BattleTimeline.TicksPerSecond`）。
        // ─────────────────────────────────────────────────────────────────────
        [Header("Task 09 命令接入（可空）")]
        [Tooltip("注入后：删除 Editable 普通计划走 RemoveEditablePlanOperation 经同一命令入口提交；锁定线显示 CurrentTick+1。")]
        public ViewInputController InputController;
        public ViewInputPorts InputPorts;

        /// <summary>本视图的输入模式状态机（未注入时为 null）。</summary>
        public ViewInputController Controller => InputController;

        /// <summary>把端口注入本视图与内部输入控制器（宿主在 New 模式启动时调用一次）。</summary>
        public void BindInputPorts(ViewInputPorts ports)
        {
            InputPorts = ports;
            if (InputController == null) InputController = new ViewInputController();
            InputController.BindPorts(ports);
            _layoutDirty = true;

            if (IsInputPortsBound) return;

            // 回到旧路径：丢弃上一轮由 Logic 投影产生的块与占位 placement，
            // 让旧 ReserveGroupId 键空间保持干净（否则旧路径会撞上 Schedule == null 的占位条目）。
            foreach (long planId in _projectedActionPlanIds)
            {
                _playerBlocks.Remove(planId);
                _placementsByGroupId.Remove(planId);
            }
            _projectedActionPlanIds.Clear();
        }

        /// <summary>
        /// 锁定线显示 Tick：快照 <c>CurrentTick + 1</c>。
        /// 这只是<strong>提前反馈</strong>——越线编辑的最终拒绝来自 Logic
        /// （命令目标 Tick + 计划 State + 启动门禁）。
        /// </summary>
        public long LockedLineTick => InputController != null ? InputController.LockedLineTick : 0L;

        /// <summary>该计划在 UI 侧是否<strong>看起来</strong>可删除（存在 + 属主受控 + 普通计划 + Editable）。</summary>
        public bool CanRequestDelete(long actionPlanId)
        {
            if (InputController == null || actionPlanId <= 0L) return false;
            IViewLogicPort logic = InputController.Logic;
            if (logic == null) return false;
            ProjectHero.Logic.Snapshots.ActionPlanSnapshot plan;
            return logic.TryFindEditablePlan(new ActionPlanId(actionPlanId), out plan);
        }

        /// <summary>
        /// 时间线块在 UI 侧"此刻可删 / 可拖"的<strong>唯一</strong>判定
        /// （任务 09 / B2 范围 §3.5 的 UI 侧口径）。
        ///
        /// 它<strong>只</strong>是提前反馈，不是授权：
        /// <list type="bullet">
        /// <item>真值来源是 <see cref="IViewLogicPort.TryFindEditablePlan"/>，
        /// 即"存在 + 属主受控 + <strong>普通</strong>计划 + <c>Editable</c>"。
        /// 因此 Locked / Running / 反应计划在 UI 上<strong>就地不可删、不可拖</strong>
        /// （按钮/拖拽入口直接不响应，连命令都不构造）；</item>
        /// <item>UI 的判定<strong>不是</strong>权威：所有删除/重排仍然编码为
        /// <c>RemoveEditablePlanOperation</c> / <c>MoveEditablePlanOperation</c> 经唯一入口提交，
        /// Logic 会用修订号、计划 State 与控制权重新校验；
        /// 即使 UI 判断被绕过，Logic 的稳定拒绝仍是最终答案。</item>
        /// </list>
        /// </summary>
        public bool CanEditBlockInUi(long actionPlanId) => actionPlanId > 0L && CanRequestDelete(actionPlanId);

        /// <summary>
        /// 本地放置草稿的确认：把当前草稿编译为原子 Operations 并经唯一入口提交。
        /// 逐帧预览/吸附<strong>不</strong>走这里。
        /// </summary>
        public ViewSubmissionOutcome ConfirmLocalDraft()
            => InputController != null
                ? InputController.ConfirmDraft()
                : ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_LOGIC_PORT);

        // ————————————————————————————————————————————————————————————
        // 唯一换算点 / 旧写入守卫 / Logic 投影（任务 09 / B2）
        // ————————————————————————————————————————————————————————————

        /// <summary>视图时间轴绝对秒 → 绝对 Tick（唯一换算点的公开出口）。</summary>
        public long ViewSecondsToTick(double secondsAbsolute)
            => TickConverter.TickAtAbsoluteSeconds(secondsAbsolute);

        /// <summary>绝对 Tick → 视图时间轴绝对秒（同一换算点的反向出口）。</summary>
        public double ViewSecondsOfTick(long tick) => TickConverter.SecondsAtTick(tick);

        /// <summary>
        /// 旧时间线排程写入的<strong>唯一出口</strong>（防御纵深）：
        /// 调用点已经显式判断过 <see cref="LegacyTimelineWritesEnabled"/>，这里再判一次——
        /// 任何新增的旧写入路径只要走它就不可能越过守卫。
        /// </summary>
        private bool TryLegacySchedule(TimelineActionPlacement placement, float delaySeconds, long blockId)
        {
            if (!LegacyTimelineWritesEnabled || placement == null)
            {
                LegacyTimelineWritesBlockedCount++;
                return false;
            }
            placement.Schedule?.Invoke(delaySeconds, blockId);
            return true;
        }

        /// <summary>旧时间线 <c>BattleTimeline.CancelGroup</c> 的唯一出口（同上）。</summary>
        private bool TryLegacyCancelGroup(long blockId)
        {
            if (!LegacyTimelineWritesEnabled || Timeline == null)
            {
                LegacyTimelineWritesBlockedCount++;
                return false;
            }
            Timeline.CancelGroup(blockId);
            return true;
        }

        /// <summary>旧时间线 <c>BattleTimeline.ReserveGroupId</c> 的唯一出口（同上）。</summary>
        private long ReserveLegacyGroupId()
        {
            if (!LegacyTimelineWritesEnabled || Timeline == null)
            {
                LegacyTimelineWritesBlockedCount++;
                return 0L;
            }
            return Timeline.ReserveGroupId();
        }

        /// <summary>
        /// 把 Logic 只读投影同步进玩家 Lane 的块表：<strong>块的唯一身份来源</strong>。
        ///
        /// <list type="bullet">
        /// <item>键<strong>就是</strong> <c>ActionPlanSnapshot.ActionPlanId</c>
        /// （来自 <see cref="IViewLogicPort.EditablePlansOf"/>），
        /// <strong>不是</strong> <c>BattleTimeline.ReserveGroupId()</c>；
        /// 因此删除/重排拿到的 plan id 一定能在 <c>IViewLogicPort</c> 上查得到，
        /// "接线后 <c>CanRequestDelete</c> 恒 false"这个缺陷被从根上消除。</item>
        /// <item>集合本身已经只含"存在 + 属主受控 + 普通计划 + <c>Editable</c>"，
        /// 因此 Locked / Running / 反应计划天然不在其中（UI 侧无需再判一次状态，
        /// 也不会出现"UI 与 Logic 两套口径"）。</item>
        /// <item><strong>未接线时是纯 no-op</strong>：不伪造块、不回退到旧 <c>ReserveGroupId</c>。</item>
        /// <item>离开可编辑集合的块只清<strong>视图</strong>模型——
        /// 权威删除由 Logic 的统一终态协调器完成，本方法绝不写逻辑。</item>
        /// </list>
        /// </summary>
        /// <returns>本次投影出的块数量。</returns>
        public int SyncPlayerBlocksFromLogic()
        {
            if (!IsInputPortsBound) return 0;

            IViewLogicPort logic = InputController.Logic;
            IReadOnlyList<ProjectHero.Logic.Snapshots.ActionPlanSnapshot> editable =
                logic.EditablePlansOf(logic.ControllerId);
            if (editable == null) return 0;

            var seen = new HashSet<long>();
            for (int i = 0; i < editable.Count; i++)
            {
                ProjectHero.Logic.Snapshots.ActionPlanSnapshot plan = editable[i];
                if (plan == null) continue;

                long planId = plan.ActionPlanId;
                if (planId <= 0L) continue;

                CombatUnit owner = ResolveViewUnit(plan.OwnerUnitId);
                var model = new BlockRenderModel
                {
                    ActionPlanId = planId,
                    Owner = owner,
                    Lane = TimelineLane.Player,
                    Kind = ViewKindOf(plan),
                    StartTimeAbs = (float)ViewSecondsOfTick(plan.StartTick),
                    Duration = (float)ViewSecondsOfTick(plan.EndTick - plan.StartTick),
                    IsInteractable = true,
                    MoveDestination = plan.HasDestination
                        ? new GridPoint?(new GridPoint(plan.DestinationX, plan.DestinationY))
                        : null
                };
                _playerBlocks[planId] = model;

                // 旧 placement 表只保留"标签/时长"这些纯视图字段；
                // 接线后它的 Schedule 恒为 null —— 旧时间线调度委托在 New 模式下不存在。
                if (!_placementsByGroupId.TryGetValue(planId, out TimelineActionPlacement placement)
                    || placement == null)
                {
                    _placementsByGroupId[planId] = new TimelineActionPlacement
                    {
                        Owner = owner,
                        Kind = model.Kind,
                        Label = plan.ActionSpecId,
                        DurationSeconds = model.Duration,
                        Lane = model.Lane,
                        MoveDestination = model.MoveDestination,
                        Schedule = null
                    };
                }
                else
                {
                    placement.Owner = owner;
                    placement.Kind = model.Kind;
                    placement.Label = plan.ActionSpecId;
                    placement.DurationSeconds = model.Duration;
                    placement.Lane = model.Lane;
                    placement.MoveDestination = model.MoveDestination;
                    placement.Schedule = null;
                }

                seen.Add(planId);
            }

            var stale = new List<long>();
            foreach (long projected in _projectedActionPlanIds)
            {
                if (!seen.Contains(projected)) stale.Add(projected);
            }
            for (int i = 0; i < stale.Count; i++)
            {
                _playerBlocks.Remove(stale[i]);
                _placementsByGroupId.Remove(stale[i]);
            }

            _projectedActionPlanIds.Clear();
            foreach (long planId in seen) _projectedActionPlanIds.Add(planId);

            if (seen.Count > 0) _layoutDirty = true;
            return seen.Count;
        }

        private CombatUnit ResolveViewUnit(long unitId)
        {
            Func<long, CombatUnit> resolver = ViewUnitResolver;
            return resolver != null ? resolver(unitId) : null;
        }

        /// <summary>
        /// 权威 <c>ActionType</c> → 旧渲染用的 <see cref="TimelineActionKind"/>。
        /// 只影响颜色/关键帧表现，不参与任何判定；未知规格返回 <c>None</c>（不猜）。
        /// </summary>
        private TimelineActionKind ViewKindOf(ProjectHero.Logic.Snapshots.ActionPlanSnapshot plan)
        {
            IViewLogicPort logic = InputController != null ? InputController.Logic : null;
            ProjectHero.Logic.Actions.ActionType actionType;
            if (logic == null || plan == null
                || !logic.TryGetActionType(plan.ActionSpecId, out actionType))
                return TimelineActionKind.None;

            switch (actionType)
            {
                case ProjectHero.Logic.Actions.ActionType.Attack:
                case ProjectHero.Logic.Actions.ActionType.Cast:
                    return TimelineActionKind.Attack;
                case ProjectHero.Logic.Actions.ActionType.Block:
                    return TimelineActionKind.Block;
                case ProjectHero.Logic.Actions.ActionType.Dodge:
                    return TimelineActionKind.Dodge;
                case ProjectHero.Logic.Actions.ActionType.Move:
                    return TimelineActionKind.Move;
                case ProjectHero.Logic.Actions.ActionType.Guard:
                    return TimelineActionKind.Recover;
                default:
                    return TimelineActionKind.None;
            }
        }

        /// <summary>
        /// 视图对象的销毁：运行时用 <c>Destroy</c>；编辑期（Editor 工具 / EditMode 测试）用
        /// <c>DestroyImmediate</c>——编辑期调用 <c>Destroy</c> 会打印错误日志。
        /// <strong>只</strong>用于接线后守卫分支里的本地 ghost 清理；旧路径的销毁点逐字未改。
        /// </summary>
        private static void DestroyViewObject(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        private readonly Dictionary<long, TimelineBlockView> _activeViews = new();
        /// <summary>
        /// 旧 <c>TimelineActionPlacement</c> 表。键是<strong>块标识</strong>：
        /// 未接线时是旧 <c>ReserveGroupId</c> 组号；接线后是 Logic <c>ActionPlanId</c>
        /// （此时条目的 <c>Schedule</c> 恒为 <c>null</c>——接线后不存在任何旧时间线调度委托）。
        /// </summary>
        private readonly Dictionary<long, TimelineActionPlacement> _placementsByGroupId = new();
        /// <summary>玩家 Lane 块表。键 = 块标识（见 <see cref="BlockRenderModel.ActionPlanId"/>）。</summary>
        private readonly Dictionary<long, BlockRenderModel> _playerBlocks = new();
        private readonly Dictionary<long, BlockRenderModel> _observedBlocks = new();

        private class BlockRenderModel
        {
            /// <summary>
            /// 块标识（任务 09 / B2「时间线块标识语义」）。
            ///
            /// <list type="bullet">
            /// <item>对 <see cref="_playerBlocks"/>（玩家 Lane、可交互）它<strong>就是</strong>
            /// Logic 只读投影 <c>ActionPlanSnapshot.ActionPlanId</c>
            /// （唯一来源 <c>IViewLogicPort.EditablePlansOf</c>），删除/重排直接拿它构造
            /// <c>RemoveEditablePlanOperation</c> / <c>MoveEditablePlanOperation</c>。
            /// <strong>不是</strong>旧 <c>BattleTimeline.ReserveGroupId()</c>、
            /// 更不是 <c>GetInstanceID()</c>/<c>GetEntityId()</c>/注册顺序/名称（00 号规则 16）。</item>
            /// <item>对 <see cref="_observedBlocks"/>（观察 Lane、<c>IsInteractable == false</c>）
            /// 它仍是旧时间线快照的组号，<strong>只</strong>作为渲染键使用：
            /// 它永远走不到删除/重排决策（那两条路径都要求
            /// <see cref="IsBlockInteractable"/> 且以 <c>IViewLogicPort</c> 的只读判定为准）。</item>
            /// </list>
            /// </summary>
            public long ActionPlanId;

            public long OriginalGroupId;
            public CombatUnit Owner;
            public TimelineLane Lane;
            public TimelineActionKind Kind;
            public float StartTimeAbs;
            public float Duration;
            public bool IsInteractable;
            public GridPoint? MoveDestination;
        }

        private TimelineActionPlacement _pendingPlacement;
        private TimelineBlockView _pendingGhost;
        private RectTransform _pendingLane;
        private float _pendingLastResolvedCenterX;
        private float _pendingLastMouseXFromLeft;

        private bool _isRepositioning;
        /// <summary>重排手势的块标识（键语义同 <see cref="BlockRenderModel.ActionPlanId"/>）。</summary>
        private long _repositionGroupId;
        private BlockRenderModel _repositionOriginalModel;

        private bool _isDraggingExisting;
        /// <summary>拖动中的块标识（键语义同 <see cref="BlockRenderModel.ActionPlanId"/>）。</summary>
        private long _draggingGroupId;
        private TimelineBlockView _draggingView;
        private float _dragOriginalX;

        private Image _currentTimeLinePlayer;
        private Image _currentTimeLineObserved;
        private Image _mouseLinePlayer;
        private Image _mouseLineObserved;
        private Image _snapIndicatorLine;

        private Text _mouseTimeText;
        private Text _nowTimeText;

        private Image _placementShield;
        private Image _lockedLinePlayer;
        private Image _lockedLineObserved;
        private bool _layoutDirty;
        private float _suppressBlockClicksUntilUnscaled;

        /// <summary>
        /// 秒 / Tick 的<strong>唯一</strong>换算点（任务 09 / B2 范围 §3.3）。
        ///
        /// tick 率来自旧时间线的唯一常量 <c>BattleTimeline.TicksPerSecond</c>，
        /// 本视图<strong>不再</strong>自建第二个 60。任何"秒 → 整数 Tick"的换算
        /// （放置/重排请求、Logic 计划位点的渲染投影、锁定线像素偏移）都必须经它，
        /// 禁止在 UI 各处散落乘/除 60。
        /// </summary>
        private ViewTickConverter TickConverter
            => new ViewTickConverter(InputController != null ? InputController.Logic : null,
                BattleTimeline.TicksPerSecond);

        /// <summary>
        /// <c>UnitId</c>(Logic) → <c>CombatUnit</c>(视图) 的映射，由宿主（任务 10 适配器）注入。
        ///
        /// 未注入时返回 <c>null</c>：投影出的块<strong>只携带权威身份</strong>
        /// （<c>ActionPlanId</c>），不参与渲染——绝不按名字/顺序/槽位猜单位。
        /// </summary>
        public Func<long, CombatUnit> ViewUnitResolver;

        /// <summary>当前由 Logic 投影产生的块标识（离开可编辑集合时要回收，见 <see cref="BindInputPorts"/>）。</summary>
        private readonly HashSet<long> _projectedActionPlanIds = new();

        /// <summary>
        /// 被 <see cref="LegacyTimelineWritesEnabled"/> 守卫拦下的旧时间线写入尝试次数
        /// （观测面；正常路径恒为 0 增长）。
        ///
        /// 它只用于让"接线后旧写入不可达"这条结构性约束可被复核；
        /// 它<strong>不是</strong>授权、不参与任何决策。
        /// </summary>
        public int LegacyTimelineWritesBlockedCount { get; private set; }

        /// <summary>
        /// 旧时间线写入是否仍然启用（任务 09 / B2 的<strong>结构性</strong>守卫）。
        ///
        /// <list type="bullet">
        /// <item><strong>端口接线后</strong>（<see cref="IsInputPortsBound"/>）恒为 <c>false</c>：
        /// 全部旧入口（<see cref="FinalizePlacement"/>、<see cref="RequestReposition"/>、
        /// <see cref="RequestDelete"/>、<c>EndDragExisting</c>、<c>RecomputePlayerBlocksForLane</c>、
        /// <see cref="CancelPlacement"/> 的旧回滚分支）都<strong>只能</strong>改本地视图状态，
        /// 一个都不能触达 <c>BattleTimeline.CancelGroup</c> /
        /// <c>BattleTimeline.ReserveGroupId</c> / <c>TimelineActionPlacement.Schedule</c>。</item>
        /// <item><strong>为什么必须是显式守卫</strong>：接线后 <c>_playerBlocks</c> 恰好为空
        /// 只是<strong>当时</strong>的数据巧合——任务 10 的适配器一旦把 Logic 计划投影进
        /// <c>_playerBlocks</c>（本类已提供 <see cref="SyncPlayerBlocksFromLogic"/>），
        /// 这个隐式前提立刻消失，旧写入就会重新变得可达。守卫把"不可达"从数据巧合
        /// 升级为代码事实，并由 <c>SelectionAndDragModesDoNotMutateLogic</c> 的扩展段钉住。</item>
        /// <item><strong>未接线</strong>（旧 Legacy 场景 / PlayMode 回归）恒为 <c>true</c>：
        /// 旧行为逐字保留（PlayMode 38 条与旧场景零回归）。</item>
        /// </list>
        /// </summary>
        public bool LegacyTimelineWritesEnabled => ProjectHero.Core.Compatibility.Runtime.BattleRuntimeBootstrap.LegacyWritesAllowed && !IsInputPortsBound;

        public bool SuppressBlockClicks => Time.unscaledTime < _suppressBlockClicksUntilUnscaled;

        private const float PredictionTimeQuantumSeconds = 0.05f;
        private const float DurationQuantumSeconds = 0.05f;
        private const float PredictionEpsilonSeconds = 0.0005f;

        private void Awake()
        {
            if (Timeline == null) Timeline = FindFirstObjectByType<BattleTimeline>();
            if (Canvas == null) Canvas = GetComponentInParent<Canvas>();

            // Ensure Ruler Area
            if (RulerArea == null)
            {
                var go = new GameObject("RulerArea", typeof(RectTransform));
                go.transform.SetParent(transform, false);
                RulerArea = go.GetComponent<RectTransform>();
                
                RulerArea.anchorMin = new Vector2(0, 1);
                RulerArea.anchorMax = new Vector2(1, 1);
                
                RulerArea.pivot = new Vector2(0f, 1f);
                RulerArea.sizeDelta = new Vector2(0, 30);
                RulerArea.anchoredPosition = Vector2.zero;
            }

            EnsureTimeLines();
            EnsureLaneMasks();
        }

        private void Start()
        {
            if (RulerArea != null)
            {
                RulerArea.SetAsLastSibling();
            }
        }

        // --- Core Methods ---

        public Color GetBaseColor(TimelineActionKind kind)
        {
            return kind switch
            {
                TimelineActionKind.Move => MoveColor,
                TimelineActionKind.Block => BlockColor,
                TimelineActionKind.Dodge => DodgeColor,
                TimelineActionKind.Attack => AttackColor,
                TimelineActionKind.Recover => RecoverColor,
                _ => new Color(1f, 1f, 1f, 1f)
            };
        }

        public void SetPlayerUnit(CombatUnit unit)
        {
            if (PlayerUnit == unit) return;
            PlayerUnit = unit;
            ClearAllBlocks();
            _layoutDirty = true;
        }

        public void SetObservedUnit(CombatUnit unit)
        {
            if (ObservedUnit == unit) return;
            ObservedUnit = unit;
            _observedBlocks.Clear();
            _layoutDirty = true;
        }

        private void ClearAllBlocks()
        {
            foreach (var view in _activeViews.Values) if (view != null) Destroy(view.gameObject);
            _activeViews.Clear();
            _playerBlocks.Clear();
            _observedBlocks.Clear();
            _placementsByGroupId.Clear();
        }

        // --- Interaction Logic ---

        public void BeginPlacement(TimelineActionPlacement placement)
        {
            if (placement == null || placement.Owner == null) return;
            if (PlayerLane == null || ObservedLane == null) return;

            _pendingPlacement = placement;
            _pendingLastResolvedCenterX = 0f;
            _pendingLastMouseXFromLeft = 0f;
            _isRepositioning = false;
            _repositionGroupId = 0;

            var lane = placement.Lane == TimelineLane.Player ? PlayerLane : ObservedLane;
            _pendingLane = lane;
            EnsurePlacementShield(lane);

            var ghost = CreateBlockGO(lane);
            ghost.SetModel(actionPlanId: 0, eventId: 0, startTime: 0f, duration: placement.DurationSeconds, label: placement.Label, isGhost: true);
            ghost.SetWidth(Mathf.Max(MinBlockWidthPx, placement.DurationSeconds * PixelsPerSecond));
            ghost.SetColor(ApplyDepth(GetBaseColor(placement.Kind), placement.DurationSeconds, isGhost: true));

            float halfWidth = ghost.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
            ghost.SetX(halfWidth);
            _pendingLastResolvedCenterX = halfWidth;
            _pendingLastMouseXFromLeft = halfWidth;
            _pendingGhost = ghost;
        }

        public bool HasPendingPlacement => _pendingGhost != null;

        /// <summary>
        /// 取消当前放置/重排手势（右键 / 返回 / 关闭面板）。
        ///
        /// 任务 09：接线后它<strong>只改变输入模式与本地草稿</strong>——
        /// 不写逻辑、不暂停执行、不产生命令、不影响回放输入。
        /// 旧的 <c>placement.Schedule</c> 回滚只在<strong>未接线</strong>的 Legacy 放置路径上执行
        /// （那条路径本身不面向新逻辑路径）。
        /// </summary>
        public void CancelPlacement()
        {
            _suppressBlockClicksUntilUnscaled = Time.unscaledTime + 0.05f;

            if (InputController != null)
            {
                // 纯视图回退：丢弃草稿、退出输入模式。
                InputController.CancelGesture();
                _isRepositioning = false;
                _repositionGroupId = 0;
            }
            else if (LegacyTimelineWritesEnabled && _isRepositioning && Timeline != null && _repositionGroupId != 0)
            {
                // 旧回滚分支：只有"未接线"的 Legacy 场景会走到这里（显式守卫，见
                // LegacyTimelineWritesEnabled）。接线后该分支整体不可达。
                if (_placementsByGroupId.TryGetValue(_repositionGroupId, out var placement) && placement != null)
                {
                    float delay = Mathf.Max(0f, _repositionOriginalModel.StartTimeAbs - Timeline.CurrentTime);
                    TryLegacySchedule(placement, delay, _repositionGroupId);
                    _playerBlocks[_repositionGroupId] = _repositionOriginalModel;
                    _layoutDirty = true;
                }
                _isRepositioning = false;
                _repositionGroupId = 0;
            }

            if (_pendingGhost != null) Destroy(_pendingGhost.gameObject);
            DestroyPlacementShield();
            _pendingGhost = null;
            _pendingPlacement = null;
            _pendingLane = null;
            if (_snapIndicatorLine != null) _snapIndicatorLine.enabled = false;
            PlacementCancelled?.Invoke();
        }

        /// <summary>
        /// 确认一次放置（旧放置路径）。
        ///
        /// <strong>任务 09 / B2 边界（结构性不可达）</strong>：端口接线后本方法
        /// <strong>整体不可达</strong>——顶部的 <see cref="LegacyTimelineWritesEnabled"/> 守卫
        /// 会让它只清理本地 ghost，绝不触达 <c>Timeline.ReserveGroupId()</c> /
        /// <c>placement.Schedule</c> / <c>BattleTimeline.CancelGroup</c>，也不建立
        /// <c>_playerBlocks</c>/<c>_placementsByGroupId</c> 条目。
        /// 新逻辑路径的"确认"走 <see cref="ConfirmLocalDraft"/>（本地草稿 → 原子 Operations
        /// → 同一命令入口）。
        ///
        /// <strong>未接线</strong>（旧 Legacy 场景）：行为逐字不变。
        /// </summary>
        public void FinalizePlacement(TimelineBlockView ghost)
        {
            if (_pendingPlacement == null || ghost == null || Timeline == null) return;
            _suppressBlockClicksUntilUnscaled = Time.unscaledTime + 0.05f;

            if (!LegacyTimelineWritesEnabled)
            {
                // 接线后：只回收本地 ghost 与手势状态，一个旧时间线写入都不发生。
                LegacyTimelineWritesBlockedCount++;
                DestroyViewObject(ghost.gameObject);
                if (_placementShield != null) { DestroyViewObject(_placementShield.gameObject); _placementShield = null; }
                _pendingGhost = null;
                _pendingPlacement = null;
                _pendingLane = null;
                _isRepositioning = false;
                _repositionGroupId = 0;
                if (_snapIndicatorLine != null) _snapIndicatorLine.enabled = false;
                return;
            }

            float halfWidth = ghost.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
            float leftEdgeX = ghost.GetX() - halfWidth;
            float startDelay = Mathf.Max(0f, leftEdgeX / Mathf.Max(1f, PixelsPerSecond));
            float startAbs = Timeline.CurrentTime + startDelay;

            UpdatePendingPlacementDynamics(startAbs);

            long groupId = _isRepositioning ? _repositionGroupId : ReserveLegacyGroupId();
            if (groupId == 0L) return;

            TryLegacySchedule(_pendingPlacement, startDelay, groupId);
            _placementsByGroupId[groupId] = _pendingPlacement;

            _playerBlocks[groupId] = new BlockRenderModel
            {
                ActionPlanId = groupId,
                Owner = _pendingPlacement.Owner,
                Lane = _pendingPlacement.Lane,
                Kind = _pendingPlacement.Kind,
                StartTimeAbs = startAbs,
                Duration = _pendingPlacement.DurationSeconds,
                IsInteractable = true,
                MoveDestination = _pendingPlacement.MoveDestination
            };

            var placedOwner = _pendingPlacement.Owner;
            var placedLane = _pendingPlacement.Lane;

            _isRepositioning = false;
            _repositionGroupId = 0;

            Destroy(ghost.gameObject);
            DestroyPlacementShield();
            _pendingGhost = null;
            _pendingPlacement = null;
            _pendingLane = null;
            if (_snapIndicatorLine != null) _snapIndicatorLine.enabled = false;

            RecomputePlayerBlocksForLane(placedOwner, placedLane);
            PlacementCommitted?.Invoke();
        }

        /// <summary>
        /// 删除请求（任务 09「必须产出」14）。
        ///
        /// <strong>接线后</strong>（<see cref="InputController"/> 已绑定端口）：
        /// <list type="bullet">
        /// <item>只有"仍为 Editable 的<strong>普通</strong>计划"才进入手势；Locked/Running/反应计划
        /// 在 UI 侧即<strong>不可删</strong>（连命令都不构造）。</item>
        /// <item>真正删除编码为 <c>RemoveEditablePlanOperation</c>，经
        /// <c>CommandIngressEntry.Submit(CommandRequest)</c> 走同一入口、同一修订号与原子事务，
        /// 再由 Logic 的统一终态协调器收口。</item>
        /// <item>本方法<strong>不</strong>直接删 Plan / Intent / MovementSegment / Reservation，
        /// 也<strong>不</strong>调用旧 <c>BattleTimeline.CancelGroup</c>。</item>
        /// </list>
        ///
        /// <strong>未接线</strong>（旧 Legacy 场景）：完全没有可编辑计划时，删除请求被<strong>拒绝</strong>
        /// 并记录原因——绝不静默回退到"直接删计划"的旧路径。
        /// </summary>
        public void RequestDelete(TimelineBlockView block)
        {
            if (block == null) return;
            // 接线后的删除路径**不依赖**旧 BattleTimeline（New 模式可以没有它）；
            // 未接线时旧引用仍是硬前提。
            if (Timeline == null && !IsInputPortsBound) return;
            if (block.ActionPlanId == 0) return;

            if (IsInputPortsBound)
            {
                // 块标识就是 Logic 的 ActionPlanId（唯一来源 IViewLogicPort.EditablePlansOf）。
                var planId = new ActionPlanId(block.ActionPlanId);
                if (!CanEditBlockInUi(block.ActionPlanId))
                {
                    Debug.LogWarning(
                        "[TimelineEditorUI] 拒绝删除：目标不是仍为 Editable 的普通计划（Locked/Running/反应计划不可删）。planId=" +
                        block.ActionPlanId);
                    return;
                }

                if (!InputController.BeginRemoval(planId))
                {
                    Debug.LogWarning("[TimelineEditorUI] 拒绝删除：未能进入删除手势。planId=" + block.ActionPlanId);
                    return;
                }

                ViewSubmissionOutcome outcome = InputController.ConfirmDraft();
                if (!outcome.Submitted)
                {
                    Debug.LogWarning(
                        "[TimelineEditorUI] RemoveEditablePlanOperation 未提交：" + outcome.ReasonCode +
                        " / " + outcome.RejectionReasonCode);
                    return;
                }

                // 视图侧只清掉本地渲染模型；权威删除由 Logic 的统一终态协调器完成，
                // 下一次快照同步会把该块移出可编辑集合。
                _placementsByGroupId.Remove(block.ActionPlanId);
                _playerBlocks.Remove(block.ActionPlanId);
                _layoutDirty = true;
                return;
            }

            Debug.LogWarning(
                "[TimelineEditorUI] 未注入命令端口：拒绝删除。删除 Editable 计划必须经 RemoveEditablePlanOperation 提交。" +
                "planId=" + block.ActionPlanId);
        }

        /// <summary>该计划在 UI 侧是否<strong>看起来</strong>可编辑（未接线时恒为 false）。</summary>
        public bool IsPlanEditableInUi(long planId) => CanEditBlockInUi(planId);

        /// <summary>命令端口是否已注入（旧块据此决定是否保持只改本地渲染的旧拖动行为）。</summary>
        public bool IsInputPortsBound => InputController != null && InputController.Logic != null;

        /// <summary>
        /// 该块是否可交互（左键重排 / 右键删除的 UI 门槛）。
        /// 接线后叠加"仍为 Editable 普通计划"的只读判定：Locked/Running/反应计划在 UI 侧即只读。
        /// </summary>
        private bool IsBlockInteractable(BlockRenderModel model)
            => model != null && model.IsInteractable && IsPlanEditableInUi(model.ActionPlanId);

        /// <summary>
        /// 重排请求（任务 09「必须产出」14）：拖动<strong>仍为 Editable 的普通计划</strong>。
        ///
        /// <strong>接线后</strong>：进入重排手势（<see cref="ViewInputController.BeginReorder"/>），
        /// 手势期间的每次鼠标移动只更新本地预览；确认时由
        /// <see cref="ViewInputController.ConfirmDraft"/> 编译为 <c>MoveEditablePlanOperation</c>
        /// 并经同一命令入口提交。放下时的请求 Tick 由<strong>唯一换算点</strong>
        /// <see cref="ViewSecondsToTick"/> 从视图轴秒推出，本方法<strong>不</strong>调用
        /// <c>BattleTimeline.CancelGroup</c>/<c>placement.Schedule</c> 写旧时间线。
        ///
        /// <strong>未接线</strong>：拒绝重排并记录原因——绝不静默回退到旧时间线的直接写入。
        /// </summary>
        public void RequestReposition(TimelineBlockView block)
        {
            if (block == null) return;
            // 接线后的重排路径**不依赖**旧 BattleTimeline（New 模式可以没有它）；
            // 未接线时旧引用仍是硬前提。
            if (Timeline == null && !IsInputPortsBound) return;
            if (block.ActionPlanId == 0) return;
            if (!_playerBlocks.TryGetValue(block.ActionPlanId, out var model)) return;

            if (IsInputPortsBound)
            {
                // ——— 新路径：块标识 = Logic ActionPlanId；legacy placement 完全不参与 ———
                if (!IsBlockInteractable(model))
                {
                    Debug.LogWarning(
                        "[TimelineEditorUI] 拒绝重排：目标不是仍为 Editable 的普通计划。planId=" + block.ActionPlanId);
                    return;
                }

                long previewTick = ViewSecondsToTick(model.StartTimeAbs);
                if (!InputController.BeginReorder(new ActionPlanId(block.ActionPlanId), previewTick))
                {
                    Debug.LogWarning("[TimelineEditorUI] 拒绝重排：未能进入重排手势。planId=" + block.ActionPlanId);
                    return;
                }

                // 手势本体：只建立视图草稿与 ghost，不写逻辑、不提交命令。
                if (_pendingGhost != null) CancelPlacement();

                _pendingPlacement = new TimelineActionPlacement
                {
                    Owner = model.Owner,
                    Kind = model.Kind,
                    Label = PlanIdLabel(block.ActionPlanId),
                    DurationSeconds = model.Duration,
                    Lane = model.Lane,
                    MoveDestination = model.MoveDestination,
                    Schedule = null
                };
                _pendingLane = model.Lane == TimelineLane.Player ? PlayerLane : ObservedLane;
                EnsurePlacementShield(_pendingLane);

                _isRepositioning = true;
                _repositionGroupId = block.ActionPlanId;
                _repositionOriginalModel = model;
                _pendingLastResolvedCenterX = 0f;
                _pendingLastMouseXFromLeft = 0f;

                var ghost = CreateBlockGO(_pendingLane);
                ghost.SetModel(actionPlanId: 0, eventId: 0, startTime: model.StartTimeAbs, duration: model.Duration, label: _pendingPlacement.Label, isGhost: true);
                float width = Mathf.Max(MinBlockWidthPx, model.Duration * PixelsPerSecond);
                ghost.SetWidth(width);
                ghost.SetColor(ApplyDepth(GetBaseColor(model.Kind), model.Duration, isGhost: true));

                // 视图轴原点：旧时间线存在时用它，不存在（New 模式无 Legacy 时间线）时
                // 用 0——位点本身来自 Logic Tick 投影（ViewSecondsOfTick），此处只算像素。
                float viewNow = Timeline != null ? Timeline.CurrentTime : 0f;
                float xLeftEdge = (model.StartTimeAbs - viewNow) * PixelsPerSecond;
                float xCenter = xLeftEdge + width * 0.5f;
                ghost.SetX(xCenter);
                _pendingLastResolvedCenterX = xCenter;
                _pendingLastMouseXFromLeft = xCenter;
                _pendingGhost = ghost;
                return;
            }

            // ——— 未接线（旧 Legacy 场景）：拒绝重排并记录原因 ———
            // 旧实现的"未接线"分支在手势起点就直接 Timeline.CancelGroup + 删本地模型
            // （HEAD:301-308），那是一条绕过命令入口的写入；任务 09 的验收标准要求
            // 不存在这样的写入路径，因此该预写自 B1 起已移除，本处保持拒绝语义。
            Debug.LogWarning(
                "[TimelineEditorUI] 未注入命令端口：拒绝重排。重排 Editable 计划必须经 MoveEditablePlanOperation 提交。" +
                "planId=" + block.ActionPlanId);
        }

        private string PlanIdLabel(long actionPlanId)
        {
            if (_placementsByGroupId.TryGetValue(actionPlanId, out var placement) && placement != null
                && !string.IsNullOrEmpty(placement.Label))
                return placement.Label;
            return "plan=" + actionPlanId;
        }

        internal void BeginDragExisting(TimelineBlockView block)
        {
            if (block == null || block.ActionPlanId == 0) return;
            if (!_playerBlocks.ContainsKey(block.ActionPlanId)) return;

            _isDraggingExisting = true;
            _draggingGroupId = block.ActionPlanId;
            _draggingView = block;
            _dragOriginalX = block.GetX();
        }

        internal void EndDragExisting(TimelineBlockView block)
        {
            if (!_isDraggingExisting) return;
            if (block == null || block.ActionPlanId != _draggingGroupId) { ResetDrag(); return; }

            // 任务 09 / B2：接线后拖动**只是本地手势**。
            // 每一次鼠标移动/吸附只更新视图预览（草稿），确认时才由
            // ViewInputController.ConfirmDraft 编译为 MoveEditablePlanOperation 并经入口提交。
            // 这里绝不调用 BattleTimeline.CancelGroup / placement.Schedule 写旧时间线。
            if (!LegacyTimelineWritesEnabled)
            {
                LegacyTimelineWritesBlockedCount++;
                if (block.GetX() != _dragOriginalX) block.SetX(_dragOriginalX);
                ResetDrag();
                return;
            }

            if (!_placementsByGroupId.TryGetValue(_draggingGroupId, out var placement)) { ResetDrag(); return; }

            float halfWidth = block.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
            float leftEdgeX = block.GetX() - halfWidth;
            float startDelay = leftEdgeX / Mathf.Max(1f, PixelsPerSecond);
            if (startDelay < 0f) startDelay = 0f;

            float startAbs = Timeline.CurrentTime + startDelay;
            float endAbs = startAbs + Mathf.Max(0f, placement.DurationSeconds);

            if (WouldOverlap(owner: placement.Owner, lane: placement.Lane, startAbs: startAbs, endAbs: endAbs, ignoreGroupId: _draggingGroupId))
            {
                block.SetX(_dragOriginalX);
                ResetDrag();
                return;
            }

            TryLegacyCancelGroup(_draggingGroupId);
            TryLegacySchedule(placement, startDelay, _draggingGroupId);

            if (_playerBlocks.TryGetValue(_draggingGroupId, out var model))
            {
                model.StartTimeAbs = startAbs;
                _playerBlocks[_draggingGroupId] = model;
            }

            ResetDrag();
        }

        private void ResetDrag()
        {
            _isDraggingExisting = false;
            _draggingGroupId = 0;
            _draggingView = null;
        }

        private void Update()
        {
            // 任务 09 / B2：块的权威身份同步（ActionPlanId 投影）必须在旧时间线引用检查
            // **之前**——接线后的 New 模式可以没有旧 BattleTimeline，身份仍然必须正确。
            SyncPlayerBlocksFromLogic();

            if (Timeline == null || PlayerLane == null || ObservedLane == null) return;

            EnsureTimeLines();
            UpdateCurrentTimeLines();
            UpdateLockedLine();
            UpdateMouseLineAndTime();
            EnsureLaneMasks();
            UpdatePlacementGhost();

            if (_layoutDirty)
            {
                // 旧 Lane 几何重算：接线后由入口守卫直接返回（见 RecomputePlayerBlocksForLane）。
                RecomputePlayerBlocksForLane(PlayerUnit, TimelineLane.Player);
                _layoutDirty = false;
            }

            SyncObservedBlocks();
            RenderAllBlocks();
        }

        // --- Helper Methods (All Included) ---

        private void UpdatePlacementGhost()
        {
            if (_pendingGhost == null || _pendingLane == null) return;

            var mousePos = UnityEngine.Input.mousePosition;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_pendingLane, mousePos, null, out var localPoint))
            {
                float xFromLeft = LocalXToXFromLeft(_pendingLane, localPoint.x);
                float mouseClamped = Mathf.Clamp(xFromLeft, 0f, _pendingLane.rect.width);
                float resolvedCenterFinal = _pendingLastResolvedCenterX;

                for (int iter = 0; iter < 2; iter++)
                {
                    float halfWidth = _pendingGhost.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
                    float desiredCenter = Mathf.Max(0f + halfWidth, mouseClamped);

                    desiredCenter = ApplyKeyframeSnap(desiredCenter, halfWidth, out bool snapped, out float snapX);

                    if (_snapIndicatorLine != null)
                    {
                        if (snapped)
                        {
                            _snapIndicatorLine.enabled = true;
                            SetLineX(_snapIndicatorLine.rectTransform, snapX);
                        }
                        else
                        {
                            _snapIndicatorLine.enabled = false;
                        }
                    }

                    bool movingRight = mouseClamped > _pendingLastMouseXFromLeft + 0.001f;
                    bool movingLeft = mouseClamped < _pendingLastMouseXFromLeft - 0.001f;

                    float resolvedCenter = ResolveGhostCenterInsert(
                        desiredCenter, _pendingPlacement, halfWidth, _pendingLane.rect.width,
                        movingRight, movingLeft, _pendingLastResolvedCenterX);

                    resolvedCenterFinal = resolvedCenter;
                    _pendingGhost.SetX(resolvedCenterFinal);

                    float leftEdgeX = resolvedCenterFinal - halfWidth;
                    float startDelaySeconds = Mathf.Max(0f, leftEdgeX / Mathf.Max(1f, PixelsPerSecond));
                    float startAbs = Timeline.CurrentTime + startDelaySeconds;
                    UpdatePendingPlacementDynamics(startAbs);
                }

                _pendingGhost.SetKeyframeOffsetsSeconds(GetKeyframeOffsetsSeconds(_pendingPlacement.Kind, _pendingPlacement.DurationSeconds), PixelsPerSecond);
                _pendingLastResolvedCenterX = resolvedCenterFinal;
                _pendingLastMouseXFromLeft = mouseClamped;
            }

            bool overLane = RectTransformUtility.RectangleContainsScreenPoint(_pendingLane, mousePos, null);
            if (overLane)
            {
                if (UnityEngine.Input.GetMouseButtonDown(1)) { CancelPlacement(); return; }
                if (UnityEngine.Input.GetMouseButtonDown(0)) { FinalizePlacement(_pendingGhost); return; }
            }
        }

        private void SyncObservedBlocks()
        {
            var snapshot = Timeline.GetScheduledIntentsSnapshot();
            var grouped = snapshot.Where(e => e.GroupId != 0 && e.Owner != null).GroupBy(e => e.GroupId);
            var seenGroups = new HashSet<long>();

            foreach (var g in grouped)
            {
                long groupId = g.Key;
                if (_playerBlocks.ContainsKey(groupId)) continue;

                var owners = g.Select(e => e.Owner).Distinct().ToList();
                if (owners.Count != 1) continue;
                var owner = owners[0];

                TimelineLane lane;
                if (owner == PlayerUnit) lane = TimelineLane.Player;
                else if (owner == ObservedUnit) lane = TimelineLane.Observed;
                else continue;

                seenGroups.Add(groupId);

                float minTime = g.Min(e => e.Time);
                float maxTime = g.Max(e => e.Time);

                var types = g.Select(e => e.Type).ToList();
                TimelineActionKind kind = TimelineActionKind.None;
                if (types.Contains(ActionType.Attack)) kind = TimelineActionKind.Attack;
                else if (types.Contains(ActionType.Block)) kind = TimelineActionKind.Block;
                else if (types.Contains(ActionType.Dodge)) kind = TimelineActionKind.Dodge;
                else if (types.Contains(ActionType.Move)) kind = TimelineActionKind.Move;
                else if (types.Contains(ActionType.Cast)) kind = TimelineActionKind.Attack;

                if (kind == TimelineActionKind.None) continue;

                if (!_observedBlocks.TryGetValue(groupId, out var model))
                {
                    model = new BlockRenderModel
                    {
                        // 观察 Lane 的块来自旧时间线快照的组号，只是渲染键：
                        // IsInteractable == false 意味着永不进入删除/重排决策路径。
                        ActionPlanId = groupId,
                        Owner = owner,
                        Lane = lane,
                        IsInteractable = false
                    };
                    model.StartTimeAbs = minTime;
                }

                float currentEnd = model.StartTimeAbs + model.Duration;
                float newEnd = maxTime;

                if (Mathf.Abs(newEnd - currentEnd) > 0.01f)
                {
                    model.Duration = Mathf.Max(0.05f, newEnd - model.StartTimeAbs);
                }

                model.Kind = kind;
                _observedBlocks[groupId] = model;
            }

            var keys = _observedBlocks.Keys.ToList();
            foreach (var key in keys)
            {
                bool isFinished = !seenGroups.Contains(key);
                if (isFinished)
                {
                    var model = _observedBlocks[key];
                    float xLeft = (model.StartTimeAbs - Timeline.CurrentTime) * PixelsPerSecond;
                    float width = model.Duration * PixelsPerSecond;
                    if (xLeft + width < -50f)
                    {
                        _observedBlocks.Remove(key);
                    }
                }
            }
        }

        private void RenderAllBlocks()
        {
            var validViewIds = new HashSet<long>();

            var playerModels = _playerBlocks.Values.ToList();
            foreach (var model in playerModels)
            {
                RenderBlockModel(model, validViewIds);
            }

            var observedModels = _observedBlocks.Values.ToList();
            foreach (var model in observedModels)
            {
                RenderBlockModel(model, validViewIds);
            }

            var snapshot = Timeline.GetScheduledIntentsSnapshot();
            var singles = snapshot.Where(e => e.GroupId == 0 && (e.Owner == PlayerUnit || e.Owner == ObservedUnit));
            foreach (var e in singles)
            {
                long viewId = -e.Id;
                validViewIds.Add(viewId);
                RectTransform lane = (e.Owner == PlayerUnit) ? PlayerLane : ObservedLane;
                float startX = (e.Time - Timeline.CurrentTime) * PixelsPerSecond;
                float width = MinBlockWidthPx;

                if (startX + width < -50f) continue;

                if (!_activeViews.TryGetValue(viewId, out var view))
                {
                    view = CreateBlockGO(lane);
                    _activeViews[viewId] = view;
                }
                view.SetModel(0, e.Id, e.Time, 0.05f, "", false);
                view.SetWidth(width);
                view.SetX(startX + width * 0.5f);
                view.SetColor(new Color(1f, 1f, 1f, 0.35f));
                view.Background.raycastTarget = false;
            }

            var allKeys = _activeViews.Keys.ToList();
            foreach (var key in allKeys)
            {
                if (!validViewIds.Contains(key))
                {
                    if (_activeViews[key] != null) Destroy(_activeViews[key].gameObject);
                    _activeViews.Remove(key);
                }
            }
        }

        private void RenderBlockModel(BlockRenderModel model, HashSet<long> validViewIds)
        {
            if (model.Owner == null) return;
            RectTransform lane = (model.Lane == TimelineLane.Player) ? PlayerLane : ObservedLane;
            if (lane == null) return;

            float width = Mathf.Max(MinBlockWidthPx, model.Duration * PixelsPerSecond);
            if (width > 5000f) width = 5000f;

            // 使用 VisualTime
            float startX = (model.StartTimeAbs - Timeline.VisualTime) * PixelsPerSecond;

            if (startX + width < -50f)
            {
                if (model.IsInteractable)
                {
                    _playerBlocks.Remove(model.ActionPlanId);
                    _placementsByGroupId.Remove(model.ActionPlanId);
                }
                return;
            }

            validViewIds.Add(model.ActionPlanId);
            // ... (Create view logic unchanged)

            if (!_activeViews.TryGetValue(model.ActionPlanId, out var view))
            {
                view = CreateBlockGO(lane);
                _activeViews[model.ActionPlanId] = view;
            }
            if (view.transform.parent != lane) view.transform.SetParent(lane, false);
            var rt = view.GetComponent<RectTransform>();
            var pos = rt.anchoredPosition; pos.y = 0; rt.anchoredPosition = pos;

            string label = "";
            if (model.IsInteractable && _placementsByGroupId.TryGetValue(model.ActionPlanId, out var placement)) label = placement.Label;

            view.SetModel(model.ActionPlanId, 0, model.StartTimeAbs, model.Duration, label, false);
            view.SetWidth(width);
            view.SetX(startX + width * 0.5f);

            // ... (Color logic unchanged)
            var baseColor = GetBaseColor(model.Kind);
            var finalColor = ApplyDepth(baseColor, model.Duration, isGhost: false);
            if (!model.IsInteractable) finalColor.a = 0.85f;
            view.SetColor(finalColor);

            view.SetKeyframeOffsetsSeconds(GetKeyframeOffsetsSeconds(model.Kind, model.Duration), PixelsPerSecond);
            // 任务 09 / B2（§3.5 UI 侧口径）：不可删/不可拖的块连点击都不接收——
            // Locked/Running/反应计划在 UI 上就是"按钮不可用"，而不是"点了以后被拒绝"。
            if (view.Background != null) view.Background.raycastTarget = IsBlockInteractable(model);
        }
        private void EnsureTimeLines()
        {
            if (PlayerLane == null || ObservedLane == null) return;

            if (_currentTimeLinePlayer == null) _currentTimeLinePlayer = CreateLine(PlayerLane, "CurrentTimeLine");
            if (_currentTimeLineObserved == null) _currentTimeLineObserved = CreateLine(ObservedLane, "CurrentTimeLine");
            if (_mouseLinePlayer == null) _mouseLinePlayer = CreateLine(PlayerLane, "MouseLine");
            if (_mouseLineObserved == null) _mouseLineObserved = CreateLine(ObservedLane, "MouseLine");

            // 任务 09：锁定线（快照 CurrentTick + 1）。它只做提前反馈，不是权限判定。
            if (_lockedLinePlayer == null) _lockedLinePlayer = CreateLockedLine(PlayerLane, "LockedLine");
            if (_lockedLineObserved == null) _lockedLineObserved = CreateLockedLine(ObservedLane, "LockedLine");

            if (_snapIndicatorLine == null)
            {
                var go = new GameObject("SnapLine", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(transform, false);
                go.transform.SetAsLastSibling();
                var img = go.GetComponent<Image>();
                img.color = new Color(0f, 1f, 1f, 0.8f);
                img.enabled = false;
                var r = go.GetComponent<RectTransform>();
                r.anchorMin = new Vector2(0, 0);
                r.anchorMax = new Vector2(0, 1);
                r.sizeDelta = new Vector2(3, 0);
                r.localScale = Vector3.one;
                r.anchoredPosition3D = Vector3.zero;
                _snapIndicatorLine = img;
            }

            if (_mouseTimeText == null && RulerArea != null)
            {
                _mouseTimeText = CreateTimeText("MouseTimeText", RulerArea, Color.yellow);
            }
            if (_nowTimeText == null && RulerArea != null)
            {
                _nowTimeText = CreateTimeText("NowTimeText", RulerArea, Color.white);
            }

            if (_mouseLinePlayer != null) _mouseLinePlayer.enabled = false;
            if (_mouseLineObserved != null) _mouseLineObserved.enabled = false;
        }

        private Text CreateTimeText(string name, RectTransform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);

            var t = go.GetComponent<Text>();

            if (UiFont != null)
            {
                t.font = UiFont;
            }
            else
            {
                t.font = Font.CreateDynamicFontFromOSFont("Bangers", 14);
            }
            // --- FIX END ---

            t.fontSize = 14;
            t.color = color;
            t.alignment = TextAnchor.LowerLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Truncate; 

            var r = go.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 0);
            r.anchorMax = new Vector2(0, 0);
            r.pivot = new Vector2(0f, 0f);

            r.sizeDelta = new Vector2(100, 20);

            r.localScale = Vector3.one;
            r.anchoredPosition3D = Vector3.zero;

            return t;
        }

        private void UpdateCurrentTimeLines()
        {
            if (_currentTimeLinePlayer == null) return;
            SetLineX(_currentTimeLinePlayer.rectTransform, 0f);
            SetLineX(_currentTimeLineObserved.rectTransform, 0f);
            if (_nowTimeText != null && Timeline != null)
            {
                // 使用 VisualTime
                _nowTimeText.text = $"now {Timeline.VisualTime:F2}s";
                float rulerX = ConvertLaneXToRulerX(0f);
                SetTextX(_nowTimeText.rectTransform, rulerX);
                _nowTimeText.enabled = true;
            }
        }

        private void UpdateMouseLineAndTime()
        {
            // ... (Logic unchanged)
            if (_mouseLinePlayer == null) return;
            var mousePos = UnityEngine.Input.mousePosition;
            bool overP = RectTransformUtility.RectangleContainsScreenPoint(PlayerLane, mousePos, null);
            bool overO = RectTransformUtility.RectangleContainsScreenPoint(ObservedLane, mousePos, null);

            if (!overP && !overO)
            {
                _mouseLinePlayer.enabled = false;
                _mouseLineObserved.enabled = false;
                if (_mouseTimeText != null) _mouseTimeText.enabled = false;
                if (_nowTimeText != null) _nowTimeText.enabled = true;
                return;
            }

            RectTransform lane = overP ? PlayerLane : ObservedLane;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(lane, mousePos, null, out var lp)) return;
            float x = Mathf.Clamp(LocalXToXFromLeft(lane, lp.x), 0f, lane.rect.width);
            _mouseLinePlayer.enabled = true; _mouseLineObserved.enabled = true;
            SetLineX(_mouseLinePlayer.rectTransform, x); SetLineX(_mouseLineObserved.rectTransform, x);

            float offsetTime = x / PixelsPerSecond;
            // 使用 VisualTime
            float absTime = Timeline != null ? Timeline.VisualTime + offsetTime : 0f;

            if (_mouseTimeText != null)
            {
                _mouseTimeText.enabled = true;
                _mouseTimeText.text = $"{absTime:F2}s (+{offsetTime:F2})";
                float rulerX = ConvertLaneXToRulerX(x);
                SetTextX(_mouseTimeText.rectTransform, rulerX);
                if (_nowTimeText != null)
                {
                    float dist = Mathf.Abs(_nowTimeText.rectTransform.anchoredPosition.x - rulerX);
                    _nowTimeText.enabled = dist > 80f;
                }
            }
        }

        private float ConvertLaneXToRulerX(float laneX)
        {
            if (PlayerLane == null || RulerArea == null) return laneX;
            float localXInLane = laneX - PlayerLane.rect.width * PlayerLane.pivot.x;
            Vector3 worldPos = PlayerLane.TransformPoint(new Vector3(localXInLane, 0, 0));
            Vector2 localPoint;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(RulerArea, RectTransformUtility.WorldToScreenPoint(null, worldPos), null, out localPoint);
            return localPoint.x;
        }

        private void SetLineX(RectTransform r, float x) { var p = r.anchoredPosition; p.x = x; r.anchoredPosition = p; }
        private void SetTextX(RectTransform r, float x) { var p = r.anchoredPosition; p.x = x; r.anchoredPosition = p; }

        private void UpdatePendingPlacementDynamics(float startAbs)
        {
            if (_pendingPlacement == null || _pendingGhost == null) return;
            if (_pendingPlacement.Kind != TimelineActionKind.Move || !_pendingPlacement.MoveDestination.HasValue) return;

            float t = QuantizeSeconds(startAbs, PredictionTimeQuantumSeconds);
            var predictedStart = PredictUnitGridPositionAt(_pendingPlacement.Owner, t, _isRepositioning ? _repositionGroupId : 0);

            var obstacles = GridManager.Instance != null ? GridManager.Instance.GetGlobalObstacles(_pendingPlacement.Owner) : null;
            var pathfinder = new Pathfinder();
            var path = pathfinder.FindPath(predictedStart, _pendingPlacement.MoveDestination.Value, _pendingPlacement.Owner.UnitVolumeDefinition, obstacles);

            float newDuration = ActionScheduler.EstimateMoveDuration(_pendingPlacement.Owner, path);
            if (newDuration <= 0f) newDuration = _pendingPlacement.DurationSeconds;
            newDuration = QuantizeSeconds(newDuration, DurationQuantumSeconds);

            if (Mathf.Abs(newDuration - _pendingPlacement.DurationSeconds) > 0.001f)
            {
                _pendingPlacement.DurationSeconds = newDuration;
                _pendingGhost.SetModel(0, 0, startAbs, newDuration, _pendingPlacement.Label, true);
                float width = Mathf.Max(MinBlockWidthPx, newDuration * PixelsPerSecond);
                _pendingGhost.SetWidth(width);
                _pendingGhost.SetColor(ApplyDepth(GetBaseColor(_pendingPlacement.Kind), newDuration, true));
            }
        }

        private GridPoint PredictUnitGridPositionAt(CombatUnit unit, float timeAbs, long ignoreGroupId)
        {
            var pos = unit.GridPosition;
            foreach (var kvp in _playerBlocks.OrderBy(k => k.Value.StartTimeAbs))
            {
                if (kvp.Key == ignoreGroupId) continue;
                var m = kvp.Value;
                if (m.Owner != unit) continue;
                if (m.Kind != TimelineActionKind.Move) continue;
                if (!m.MoveDestination.HasValue) continue;

                float end = m.StartTimeAbs + Mathf.Max(0f, m.Duration);
                if (end <= timeAbs - PredictionEpsilonSeconds)
                {
                    pos = m.MoveDestination.Value;
                }
            }
            return pos;
        }

        private bool WouldOverlap(CombatUnit owner, TimelineLane lane, float startAbs, float endAbs, long ignoreGroupId)
        {
            foreach (var model in _playerBlocks.Values)
            {
                if (model.ActionPlanId == ignoreGroupId) continue;
                if (model.Owner != owner || model.Lane != lane) continue;

                float otherStart = model.StartTimeAbs;
                float otherEnd = model.StartTimeAbs + Mathf.Max(0f, model.Duration);
                if (startAbs < otherEnd && endAbs > otherStart) return true;
            }
            return false;
        }

        private float ResolveGhostCenterInsert(float desiredCenterX, TimelineActionPlacement placement, float halfWidth, float laneWidth, bool movingRight, bool movingLeft, float lastResolvedCenterX)
        {
            float width = halfWidth * 2f;
            float left = Mathf.Max(0f, desiredCenterX - halfWidth);
            var intervals = new List<(float left, float right)>();

            foreach (var b in _playerBlocks.Values)
            {
                if (b.Owner != placement.Owner || b.Lane != placement.Lane) continue;
                float w = Mathf.Max(MinBlockWidthPx, b.Duration * PixelsPerSecond);
                float l = (b.StartTimeAbs - Timeline.CurrentTime) * PixelsPerSecond;
                intervals.Add((l, l + w));
            }
            intervals.Sort((a, b) => a.left.CompareTo(b.left));

            for (int i = 0; i < intervals.Count + 2; i++)
            {
                float right = left + width;
                (float oLeft, float oRight)? overlapLeftNeighbor = null;
                foreach (var iv in intervals)
                {
                    if (iv.left < left - 0.001f && left < iv.right && right > iv.left)
                    {
                        overlapLeftNeighbor = (iv.left, iv.right);
                        break;
                    }
                }
                if (overlapLeftNeighbor == null) break;
                left = overlapLeftNeighbor.Value.oRight;
            }
            return left + halfWidth;
        }

        private void RecomputePlayerBlocksForLane(CombatUnit owner, TimelineLane lane)
        {
            if (Timeline == null || owner == null) return;

            // 任务 09 / B2 的结构性守卫（对应冻结件 §8.3 第一条）：
            // 旧实现无条件走到底部的 Timeline.CancelGroup(id) + placement.Schedule，
            // 今天"没出事"只是因为接线后 _playerBlocks 恰好为空——那是数据巧合，不是保证。
            // 接线后整段几何重算与旧时间线回写都失去意义（位点来自 Logic 投影），
            // 因此在入口显式拒绝，并由 TryLegacyCancelGroup/TryLegacySchedule 在出口再判一次。
            if (!LegacyTimelineWritesEnabled)
            {
                LegacyTimelineWritesBlockedCount++;
                return;
            }

            var ids = _playerBlocks.Values
                .Where(b => b.Owner == owner && b.Lane == lane)
                .OrderBy(b => b.StartTimeAbs)
                .Select(b => b.ActionPlanId)
                .ToList();

            if (ids.Count == 0) return;

            var predictedPos = owner.GridPosition;

            for (int i = 0; i < ids.Count; i++)
            {
                long id = ids[i];
                var model = _playerBlocks[id];

                float duration = Mathf.Max(0f, model.Duration);
                if (model.Kind == TimelineActionKind.Move && model.MoveDestination.HasValue)
                {
                    var obstacles = GridManager.Instance != null ? GridManager.Instance.GetGlobalObstacles(owner) : null;
                    var pathfinder = new Pathfinder();
                    var path = pathfinder.FindPath(predictedPos, model.MoveDestination.Value, owner.UnitVolumeDefinition, obstacles);
                    float d = ActionScheduler.EstimateMoveDuration(owner, path);
                    if (d > 0f) duration = QuantizeSeconds(d, DurationQuantumSeconds);
                    predictedPos = model.MoveDestination.Value;
                }

                if (Mathf.Abs(model.Duration - duration) > 0.001f)
                {
                    model.Duration = duration;
                    _playerBlocks[id] = model;
                }
            }

            for (int i = 0; i < ids.Count - 1; i++)
            {
                long leftId = ids[i];
                long rightId = ids[i + 1];
                var left = _playerBlocks[leftId];
                var right = _playerBlocks[rightId];
                float leftEnd = left.StartTimeAbs + Mathf.Max(0f, left.Duration);
                if (right.StartTimeAbs < leftEnd)
                {
                    float delta = leftEnd - right.StartTimeAbs;
                    for (int j = i + 1; j < ids.Count; j++)
                    {
                        long id = ids[j];
                        var m = _playerBlocks[id];
                        m.StartTimeAbs += delta;
                        _playerBlocks[id] = m;
                    }
                }
            }

            for (int i = 0; i < ids.Count; i++)
            {
                long id = ids[i];
                var model = _playerBlocks[id];
                if (model.StartTimeAbs < Timeline.CurrentTime + 0.0001f) continue;
                if (_placementsByGroupId.TryGetValue(id, out var placement) && placement != null)
                {
                    placement.DurationSeconds = model.Duration;
                    TryLegacyCancelGroup(id);
                    float delay = Mathf.Max(0f, model.StartTimeAbs - Timeline.CurrentTime);
                    TryLegacySchedule(placement, delay, id);
                }
            }
        }

        private float ApplyKeyframeSnap(float desiredCenterX, float halfWidth, out bool snapped, out float snapX)
        {
            snapped = false;
            snapX = 0f;

            float snapPx = SnapThresholdSeconds * Mathf.Max(1f, PixelsPerSecond);
            float leftEdgeX = desiredCenterX - halfWidth;
            float ghostStartAbs = Timeline.CurrentTime + Mathf.Max(0f, leftEdgeX / Mathf.Max(1f, PixelsPerSecond));

            var ghostOffsets = GetKeyframeOffsetsSeconds(_pendingPlacement.Kind, _pendingPlacement.DurationSeconds);
            var candidateTimes = new List<float>();

            foreach (var m in _playerBlocks.Values)
            {
                if (_isRepositioning && m.ActionPlanId == _repositionGroupId) continue;
                var offsets = GetKeyframeOffsetsSeconds(m.Kind, m.Duration);
                foreach (var o in offsets) candidateTimes.Add(m.StartTimeAbs + o);
            }
            foreach (var m in _observedBlocks.Values)
            {
                var offsets = GetKeyframeOffsetsSeconds(m.Kind, m.Duration);
                foreach (var o in offsets) candidateTimes.Add(m.StartTimeAbs + o);
            }

            float bestDeltaPx = float.MaxValue;
            float bestSnapShiftSeconds = 0f;
            float bestTargetTime = 0f;

            foreach (var gk in ghostOffsets)
            {
                float ghostKeyAbs = ghostStartAbs + gk;
                foreach (var ct in candidateTimes)
                {
                    float d = ct - ghostKeyAbs;
                    float dPx = Mathf.Abs(d * PixelsPerSecond);

                    if (dPx <= snapPx && dPx < bestDeltaPx)
                    {
                        bestDeltaPx = dPx;
                        bestSnapShiftSeconds = d;
                        bestTargetTime = ct;
                    }
                }
            }

            if (bestDeltaPx != float.MaxValue)
            {
                snapped = true;
                float shiftPx = bestSnapShiftSeconds * PixelsPerSecond;
                float finalCenterX = desiredCenterX + shiftPx;
                snapX = (bestTargetTime - Timeline.CurrentTime) * PixelsPerSecond;
                return finalCenterX;
            }

            float quantizedStart = QuantizeSeconds(ghostStartAbs, PredictionTimeQuantumSeconds);
            float qDelay = quantizedStart - Timeline.CurrentTime;
            float qCenterX = (qDelay * PixelsPerSecond) + halfWidth;

            return qCenterX;
        }

        private float QuantizeSeconds(float value, float quantum) => quantum <= 0f ? value : Mathf.Round(value / quantum) * quantum;

        private List<float> GetKeyframeOffsetsSeconds(TimelineActionKind kind, float durationSeconds)
        {
            var offsets = new List<float>();
            float d = Mathf.Max(0f, durationSeconds);
            if (kind == TimelineActionKind.Move) offsets.Add(d);
            else if (kind == TimelineActionKind.Attack) offsets.Add(Mathf.Clamp(d - 0.5f, 0f, d));
            return offsets;
        }

        private Color ApplyDepth(Color baseColor, float durationSeconds, bool isGhost)
        {
            float t = DeepenAtSeconds <= 0f ? 1f : Mathf.Clamp01(durationSeconds / DeepenAtSeconds);
            float darken = Mathf.Lerp(0f, MaxDarkenFactor, t);
            var c = new Color(baseColor.r * (1f - darken), baseColor.g * (1f - darken), baseColor.b * (1f - darken), 1f);
            c.a = isGhost ? 0.45f : 0.90f;
            return c;
        }

        private float LocalXToXFromLeft(RectTransform lane, float localX) => localX + lane.rect.width * lane.pivot.x;

        private TimelineBlockView CreateBlockGO(RectTransform lane)
        {
            var go = new GameObject("TimelineBlock", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(TimelineBlockView));
            go.transform.SetParent(lane, false);
            var rect = go.GetComponent<RectTransform>();

            // FIX: Ensure Anchors are set for Left-Middle alignment to support width scaling correctly
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            // FIX: Reset sizeDelta to a sane default, height relative to lane
            // Do NOT use PixelsPerSecond here for initialization, width will be set by SetWidth later.
            rect.sizeDelta = new Vector2(MinBlockWidthPx, lane.rect.height * 0.8f);

            var img = go.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.9f);

            var view = go.GetComponent<TimelineBlockView>();
            view.Background = img;
            view.Init(this, lane, Canvas);
            return view;
        }

        private void EnsurePlacementShield(RectTransform lane)
        {
            if (lane == null) return;
            if (_placementShield != null)
            {
                if (_placementShield.transform.parent != lane) _placementShield.transform.SetParent(lane, false);
                _placementShield.transform.SetAsLastSibling();
                return;
            }
            var go = new GameObject("PlacementShield", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(lane, false);
            go.transform.SetAsLastSibling();
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            var img = go.GetComponent<Image>();
            img.color = Color.clear;
            img.raycastTarget = true;
            _placementShield = img;
        }
        private void DestroyPlacementShield() { if (_placementShield != null) { Destroy(_placementShield.gameObject); _placementShield = null; } }
        private void EnsureLaneMasks() { if (PlayerLane != null && PlayerLane.GetComponent<RectMask2D>() == null) PlayerLane.gameObject.AddComponent<RectMask2D>(); if (ObservedLane != null && ObservedLane.GetComponent<RectMask2D>() == null) ObservedLane.gameObject.AddComponent<RectMask2D>(); }
        private Image CreateLine(RectTransform lane, string name) { var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)); go.transform.SetParent(lane, false); var img = go.GetComponent<Image>(); img.color = new Color(1f, 1f, 1f, 0.8f); var r = go.GetComponent<RectTransform>(); r.anchorMin = new Vector2(0, 0); r.anchorMax = new Vector2(0, 1); r.sizeDelta = new Vector2(2, 0); return img; }

        /// <summary>
        /// 锁定线（任务 09「必须产出」14）：位置取自快照 <c>CurrentTick + 1</c>。
        ///
        /// 它<strong>只显示</strong>——UI 不据此自判权限，也不把线右侧当作"可编辑"的证明：
        /// 越线编辑的最终拒绝来自 Logic（命令目标 Tick、计划 State、Step 启动门禁）。
        /// 未注入端口时整条线隐藏，而不是回退到"用旧时间线的当前时刻当锁定线"。
        /// </summary>
        private Image CreateLockedLine(RectTransform lane, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(lane, false);
            var img = go.GetComponent<Image>();
            img.color = new Color(1f, 0.35f, 0.35f, 0.55f);
            img.raycastTarget = false;
            img.enabled = false;
            var r = go.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 0);
            r.anchorMax = new Vector2(0, 1);
            r.sizeDelta = new Vector2(2, 0);
            return img;
        }

        private void UpdateLockedLine()
        {
            if (_lockedLinePlayer == null || _lockedLineObserved == null) return;

            if (InputController == null || InputController.Logic == null || PlayerLane == null)
            {
                _lockedLinePlayer.enabled = false;
                _lockedLineObserved.enabled = false;
                return;
            }

            long currentTick = InputController.Logic.CurrentTick;
            long lockedTick = currentTick + 1L;
            // 唯一换算点：Tick 差 → 视图秒（不再在这里散落写 / TicksPerSecond）。
            float offsetSeconds = (float)ViewSecondsOfTick(lockedTick - currentTick);
            float x = offsetSeconds * Mathf.Max(1f, PixelsPerSecond);

            _lockedLinePlayer.enabled = true;
            _lockedLineObserved.enabled = true;
            SetLineX(_lockedLinePlayer.rectTransform, x);
            SetLineX(_lockedLineObserved.rectTransform, x);
            _lockedLinePlayer.transform.SetAsLastSibling();
            _lockedLineObserved.transform.SetAsLastSibling();
        }
    }
}
