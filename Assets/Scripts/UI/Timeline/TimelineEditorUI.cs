#if UNITY_EDITOR
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

        // ������������������������������������������������������������������������������������������������������������������������������������������
        // ���� 09 ����㣨B ����������ģʽ + ԭ�������ύ
        //
        // ����**��**�����κ��߼�Ȩ��״̬���˿�ֻ�ṩ"��ǰ Tick / Ԥ���޶��� /
        // �ɱ༭�ƻ� / ��Ӧ���� / Dodge Ŀ�ĸ�Ϸ���"��Щֻ��ͶӰ��
        // �Լ�Ψһд��� SubmitCommand(CommandRequest)��
        // û�ж˿�ʱ��ɾ������**�ܾ�ִ��**���������˵�ֱ��ɾ�ƻ�/��ͼ/�ƶ���/Ԥ������
        //
        // B2 �������ṹ��Լ����������Աע�ͣ���
        // �� `LegacyTimelineWritesEnabled`���˿ڽ��ߺ��ʱ����д�루CancelGroup /
        //    ReserveGroupId / placement.Schedule����**������**���ɴ��������
        //    "_playerBlocks ǡ��Ϊ��"��һ�����ɺϣ�
        // �� ���ʶ = Logic `ActionPlanSnapshot.ActionPlanId`��Ψһ��Դ
        //    `IViewLogicPort.EditablePlansOf`�������� `ReserveGroupId()`��
        // �� �� / Tick ֻ�� `ViewTickConverter` һ������㣨tick ��ȡ��
        //    ��ʱ���ߵ�Ψһ���� `BattleTimeline.TicksPerSecond`����
        // ������������������������������������������������������������������������������������������������������������������������������������������
        [Header("Task 09 ������루�ɿգ�")]
        [Tooltip("ע���ɾ�� Editable ��ͨ�ƻ��� RemoveEditablePlanOperation ��ͬһ��������ύ����������ʾ CurrentTick+1��")]
        public ViewInputController InputController;
        public ViewInputPorts InputPorts;

        /// <summary>����ͼ������ģʽ״̬����δע��ʱΪ null����</summary>
        public ViewInputController Controller => InputController;

        /// <summary>�Ѷ˿�ע�뱾��ͼ���ڲ������������������ New ģʽ����ʱ����һ�Σ���</summary>
        public void BindInputPorts(ViewInputPorts ports)
        {
            InputPorts = ports;
            if (InputController == null) InputController = new ViewInputController();
            InputController.BindPorts(ports);
            _layoutDirty = true;

            if (IsInputPortsBound) return;

            // �ص���·����������һ���� Logic ͶӰ�����Ŀ���ռλ placement��
            // �þ� ReserveGroupId ���ռ䱣�ָɾ��������·����ײ�� Schedule == null ��ռλ��Ŀ����
            foreach (long planId in _projectedActionPlanIds)
            {
                _playerBlocks.Remove(planId);
                _placementsByGroupId.Remove(planId);
            }
            _projectedActionPlanIds.Clear();
        }

        /// <summary>
        /// ��������ʾ Tick������ <c>CurrentTick + 1</c>��
        /// ��ֻ��<strong>��ǰ����</strong>����Խ�߱༭�����վܾ����� Logic
        /// ������Ŀ�� Tick + �ƻ� State + �����Ž�����
        /// </summary>
        public long LockedLineTick => InputController != null ? InputController.LockedLineTick : 0L;

        /// <summary>�üƻ��� UI ���Ƿ�<strong>������</strong>��ɾ�������� + �����ܿ� + ��ͨ�ƻ� + Editable����</summary>
        public bool CanRequestDelete(long actionPlanId)
        {
            if (InputController == null || actionPlanId <= 0L) return false;
            IViewLogicPort logic = InputController.Logic;
            if (logic == null) return false;
            ProjectHero.Logic.Snapshots.ActionPlanSnapshot plan;
            return logic.TryFindEditablePlan(new ActionPlanId(actionPlanId), out plan);
        }

        /// <summary>
        /// ʱ���߿��� UI ��"�˿̿�ɾ / ����"��<strong>Ψһ</strong>�ж�
        /// ������ 09 / B2 ��Χ ��3.5 �� UI ��ھ�����
        ///
        /// ��<strong>ֻ</strong>����ǰ������������Ȩ��
        /// <list type="bullet">
        /// <item>��ֵ��Դ�� <see cref="IViewLogicPort.TryFindEditablePlan"/>��
        /// ��"���� + �����ܿ� + <strong>��ͨ</strong>�ƻ� + <c>Editable</c>"��
        /// ��� Locked / Running / ��Ӧ�ƻ��� UI ��<strong>�͵ز���ɾ��������</strong>
        /// ����ť/��ק���ֱ�Ӳ���Ӧ������������죩��</item>
        /// <item>UI ���ж�<strong>����</strong>Ȩ��������ɾ��/������Ȼ����Ϊ
        /// <c>RemoveEditablePlanOperation</c> / <c>MoveEditablePlanOperation</c> ��Ψһ����ύ��
        /// Logic �����޶��š��ƻ� State �����Ȩ����У�飻
        /// ��ʹ UI �жϱ��ƹ���Logic ���ȶ��ܾ��������մ𰸡�</item>
        /// </list>
        /// </summary>
        public bool CanEditBlockInUi(long actionPlanId) => actionPlanId > 0L && CanRequestDelete(actionPlanId);

        /// <summary>
        /// ���ط��òݸ��ȷ�ϣ��ѵ�ǰ�ݸ����Ϊԭ�� Operations ����Ψһ����ύ��
        /// ��֡Ԥ��/����<strong>��</strong>�����
        /// </summary>
        public ViewSubmissionOutcome ConfirmLocalDraft()
            => InputController != null
                ? InputController.ConfirmDraft()
                : ViewSubmissionOutcome.Blocked(ViewInputCodes.NO_LOGIC_PORT);

        // ������������������������������������������������������������������������������������������������������������������������
        // Ψһ����� / ��д������ / Logic ͶӰ������ 09 / B2��
        // ������������������������������������������������������������������������������������������������������������������������

        /// <summary>��ͼʱ��������� �� ���� Tick��Ψһ�����Ĺ������ڣ���</summary>
        public long ViewSecondsToTick(double secondsAbsolute)
            => TickConverter.TickAtAbsoluteSeconds(secondsAbsolute);

        /// <summary>���� Tick �� ��ͼʱ��������루ͬһ�����ķ�����ڣ���</summary>
        public double ViewSecondsOfTick(long tick) => TickConverter.SecondsAtTick(tick);

        /// <summary>
        /// ��ʱ�����ų�д���<strong>Ψһ����</strong>�����������
        /// ���õ��Ѿ���ʽ�жϹ� <see cref="LegacyTimelineWritesEnabled"/>����������һ�Ρ���
        /// �κ������ľ�д��·��ֻҪ�����Ͳ�����Խ��������
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

        /// <summary>��ʱ���� <c>BattleTimeline.CancelGroup</c> ��Ψһ���ڣ�ͬ�ϣ���</summary>
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

        /// <summary>��ʱ���� <c>BattleTimeline.ReserveGroupId</c> ��Ψһ���ڣ�ͬ�ϣ���</summary>
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
        /// �� Logic ֻ��ͶӰͬ������� Lane �Ŀ����<strong>���Ψһ������Դ</strong>��
        ///
        /// <list type="bullet">
        /// <item>��<strong>����</strong> <c>ActionPlanSnapshot.ActionPlanId</c>
        /// ������ <see cref="IViewLogicPort.EditablePlansOf"/>����
        /// <strong>����</strong> <c>BattleTimeline.ReserveGroupId()</c>��
        /// ���ɾ��/�����õ��� plan id һ������ <c>IViewLogicPort</c> �ϲ�õ���
        /// "���ߺ� <c>CanRequestDelete</c> �� false"���ȱ�ݱ��Ӹ���������</item>
        /// <item>���ϱ����Ѿ�ֻ��"���� + �����ܿ� + ��ͨ�ƻ� + <c>Editable</c>"��
        /// ��� Locked / Running / ��Ӧ�ƻ���Ȼ�������У�UI ����������һ��״̬��
        /// Ҳ�������"UI �� Logic ���׿ھ�"����</item>
        /// <item><strong>δ����ʱ�Ǵ� no-op</strong>����α��顢�����˵��� <c>ReserveGroupId</c>��</item>
        /// <item>�뿪�ɱ༭���ϵĿ�ֻ��<strong>��ͼ</strong>ģ�͡���
        /// Ȩ��ɾ���� Logic ��ͳһ��̬Э������ɣ�����������д�߼���</item>
        /// </list>
        /// </summary>
        /// <returns>����ͶӰ���Ŀ�������</returns>
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

                // �� placement ��ֻ����"��ǩ/ʱ��"��Щ����ͼ�ֶΣ�
                // ���ߺ����� Schedule ��Ϊ null ���� ��ʱ���ߵ���ί���� New ģʽ�²����ڡ�
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
        /// Ȩ�� <c>ActionType</c> �� ����Ⱦ�õ� <see cref="TimelineActionKind"/>��
        /// ֻӰ����ɫ/�ؼ�֡���֣��������κ��ж���δ֪��񷵻� <c>None</c>�����£���
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
        /// ��ͼ��������٣�����ʱ�� <c>Destroy</c>���༭�ڣ�Editor ���� / EditMode ���ԣ���
        /// <c>DestroyImmediate</c>�����༭�ڵ��� <c>Destroy</c> ���ӡ������־��
        /// <strong>ֻ</strong>���ڽ��ߺ�������֧��ı��� ghost ��������·�������ٵ�����δ�ġ�
        /// </summary>
        private static void DestroyViewObject(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        private readonly Dictionary<long, TimelineBlockView> _activeViews = new();
        /// <summary>
        /// �� <c>TimelineActionPlacement</c> ��������<strong>���ʶ</strong>��
        /// δ����ʱ�Ǿ� <c>ReserveGroupId</c> ��ţ����ߺ��� Logic <c>ActionPlanId</c>
        /// ����ʱ��Ŀ�� <c>Schedule</c> ��Ϊ <c>null</c>�������ߺ󲻴����κξ�ʱ���ߵ���ί�У���
        /// </summary>
        private readonly Dictionary<long, TimelineActionPlacement> _placementsByGroupId = new();
        /// <summary>��� Lane ������� = ���ʶ���� <see cref="BlockRenderModel.ActionPlanId"/>����</summary>
        private readonly Dictionary<long, BlockRenderModel> _playerBlocks = new();
        private readonly Dictionary<long, BlockRenderModel> _observedBlocks = new();

        private class BlockRenderModel
        {
            /// <summary>
            /// ���ʶ������ 09 / B2��ʱ���߿��ʶ���塹����
            ///
            /// <list type="bullet">
            /// <item>�� <see cref="_playerBlocks"/>����� Lane���ɽ�������<strong>����</strong>
            /// Logic ֻ��ͶӰ <c>ActionPlanSnapshot.ActionPlanId</c>
            /// ��Ψһ��Դ <c>IViewLogicPort.EditablePlansOf</c>����ɾ��/����ֱ����������
            /// <c>RemoveEditablePlanOperation</c> / <c>MoveEditablePlanOperation</c>��
            /// <strong>����</strong>�� <c>BattleTimeline.ReserveGroupId()</c>��
            /// ������ <c>GetInstanceID()</c>/<c>GetEntityId()</c>/ע��˳��/���ƣ�00 �Ź��� 16����</item>
            /// <item>�� <see cref="_observedBlocks"/>���۲� Lane��<c>IsInteractable == false</c>��
            /// �����Ǿ�ʱ���߿��յ���ţ�<strong>ֻ</strong>��Ϊ��Ⱦ��ʹ�ã�
            /// ����Զ�߲���ɾ��/���ž��ߣ�������·����Ҫ��
            /// <see cref="IsBlockInteractable"/> ���� <c>IViewLogicPort</c> ��ֻ���ж�Ϊ׼����</item>
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
        /// <summary>�������ƵĿ��ʶ��������ͬ <see cref="BlockRenderModel.ActionPlanId"/>����</summary>
        private long _repositionGroupId;
        private BlockRenderModel _repositionOriginalModel;

        private bool _isDraggingExisting;
        /// <summary>�϶��еĿ��ʶ��������ͬ <see cref="BlockRenderModel.ActionPlanId"/>����</summary>
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
        /// �� / Tick ��<strong>Ψһ</strong>����㣨���� 09 / B2 ��Χ ��3.3����
        ///
        /// tick �����Ծ�ʱ���ߵ�Ψһ���� <c>BattleTimeline.TicksPerSecond</c>��
        /// ����ͼ<strong>����</strong>�Խ��ڶ��� 60���κ�"�� �� ���� Tick"�Ļ���
        /// ������/��������Logic �ƻ�λ�����ȾͶӰ������������ƫ�ƣ������뾭����
        /// ��ֹ�� UI ����ɢ���/�� 60��
        /// </summary>
        private ViewTickConverter TickConverter
            => new ViewTickConverter(InputController != null ? InputController.Logic : null,
                BattleTimeline.TicksPerSecond);

        /// <summary>
        /// <c>UnitId</c>(Logic) �� <c>CombatUnit</c>(��ͼ) ��ӳ�䣬������������ 10 ��������ע�롣
        ///
        /// δע��ʱ���� <c>null</c>��ͶӰ���Ŀ�<strong>ֻЯ��Ȩ������</strong>
        /// ��<c>ActionPlanId</c>������������Ⱦ��������������/˳��/��λ�µ�λ��
        /// </summary>
        public Func<long, CombatUnit> ViewUnitResolver;

        /// <summary>��ǰ�� Logic ͶӰ�����Ŀ��ʶ���뿪�ɱ༭����ʱҪ���գ��� <see cref="BindInputPorts"/>����</summary>
        private readonly HashSet<long> _projectedActionPlanIds = new();

        /// <summary>
        /// �� <see cref="LegacyTimelineWritesEnabled"/> �������µľ�ʱ����д�볢�Դ���
        /// ���۲��棻����·����Ϊ 0 ��������
        ///
        /// ��ֻ������"���ߺ��д�벻�ɴ�"�����ṹ��Լ���ɱ����ˣ�
        /// ��<strong>����</strong>��Ȩ���������κξ��ߡ�
        /// </summary>
        public int LegacyTimelineWritesBlockedCount { get; private set; }

        /// <summary>
        /// ��ʱ����д���Ƿ���Ȼ���ã����� 09 / B2 ��<strong>�ṹ��</strong>��������
        ///
        /// <list type="bullet">
        /// <item><strong>�˿ڽ��ߺ�</strong>��<see cref="IsInputPortsBound"/>����Ϊ <c>false</c>��
        /// ȫ������ڣ�<see cref="FinalizePlacement"/>��<see cref="RequestReposition"/>��
        /// <see cref="RequestDelete"/>��<c>EndDragExisting</c>��<c>RecomputePlayerBlocksForLane</c>��
        /// <see cref="CancelPlacement"/> �ľɻع���֧����<strong>ֻ��</strong>�ı�����ͼ״̬��
        /// һ�������ܴ��� <c>BattleTimeline.CancelGroup</c> /
        /// <c>BattleTimeline.ReserveGroupId</c> / <c>TimelineActionPlacement.Schedule</c>��</item>
        /// <item><strong>Ϊʲô��������ʽ����</strong>�����ߺ� <c>_playerBlocks</c> ǡ��Ϊ��
        /// ֻ��<strong>��ʱ</strong>�������ɺϡ������� 10 ��������һ���� Logic �ƻ�ͶӰ��
        /// <c>_playerBlocks</c>���������ṩ <see cref="SyncPlayerBlocksFromLogic"/>����
        /// �����ʽǰ��������ʧ����д��ͻ����±�ÿɴ������"���ɴ�"�������ɺ�
        /// ����Ϊ������ʵ������ <c>SelectionAndDragModesDoNotMutateLogic</c> ����չ�ζ�ס��</item>
        /// <item><strong>δ����</strong>���� Legacy ���� / PlayMode �ع飩��Ϊ <c>true</c>��
        /// ����Ϊ���ֱ�����PlayMode 38 ����ɳ�����ع飩��</item>
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
        /// ȡ����ǰ����/�������ƣ��Ҽ� / ���� / �ر���壩��
        ///
        /// ���� 09�����ߺ���<strong>ֻ�ı�����ģʽ�뱾�زݸ�</strong>����
        /// ��д�߼�������ִͣ�С������������Ӱ��ط����롣
        /// �ɵ� <c>placement.Schedule</c> �ع�ֻ��<strong>δ����</strong>�� Legacy ����·����ִ��
        /// ������·���������������߼�·������
        /// </summary>
        public void CancelPlacement()
        {
            _suppressBlockClicksUntilUnscaled = Time.unscaledTime + 0.05f;

            if (InputController != null)
            {
                // ����ͼ���ˣ������ݸ塢�˳�����ģʽ��
                InputController.CancelGesture();
                _isRepositioning = false;
                _repositionGroupId = 0;
            }
            else if (LegacyTimelineWritesEnabled && _isRepositioning && Timeline != null && _repositionGroupId != 0)
            {
                // �ɻع���֧��ֻ��"δ����"�� Legacy �������ߵ������ʽ��������
                // LegacyTimelineWritesEnabled�������ߺ�÷�֧���岻�ɴ
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
        /// ȷ��һ�η��ã��ɷ���·������
        ///
        /// <strong>���� 09 / B2 �߽磨�ṹ�Բ��ɴ</strong>���˿ڽ��ߺ󱾷���
        /// <strong>���岻�ɴ�</strong>���������� <see cref="LegacyTimelineWritesEnabled"/> ����
        /// ������ֻ�������� ghost���������� <c>Timeline.ReserveGroupId()</c> /
        /// <c>placement.Schedule</c> / <c>BattleTimeline.CancelGroup</c>��Ҳ������
        /// <c>_playerBlocks</c>/<c>_placementsByGroupId</c> ��Ŀ��
        /// ���߼�·����"ȷ��"�� <see cref="ConfirmLocalDraft"/>�����زݸ� �� ԭ�� Operations
        /// �� ͬһ������ڣ���
        ///
        /// <strong>δ����</strong>���� Legacy ����������Ϊ���ֲ��䡣
        /// </summary>
        public void FinalizePlacement(TimelineBlockView ghost)
        {
            if (_pendingPlacement == null || ghost == null || Timeline == null) return;
            _suppressBlockClicksUntilUnscaled = Time.unscaledTime + 0.05f;

            if (!LegacyTimelineWritesEnabled)
            {
                // ���ߺ�ֻ���ձ��� ghost ������״̬��һ����ʱ����д�붼��������
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
        /// ɾ���������� 09�����������14����
        ///
        /// <strong>���ߺ�</strong>��<see cref="InputController"/> �Ѱ󶨶˿ڣ���
        /// <list type="bullet">
        /// <item>ֻ��"��Ϊ Editable ��<strong>��ͨ</strong>�ƻ�"�Ž������ƣ�Locked/Running/��Ӧ�ƻ�
        /// �� UI �༴<strong>����ɾ</strong>������������죩��</item>
        /// <item>����ɾ������Ϊ <c>RemoveEditablePlanOperation</c>����
        /// <c>CommandIngressEntry.Submit(CommandRequest)</c> ��ͬһ��ڡ�ͬһ�޶�����ԭ������
        /// ���� Logic ��ͳһ��̬Э�����տڡ�</item>
        /// <item>������<strong>��</strong>ֱ��ɾ Plan / Intent / MovementSegment / Reservation��
        /// Ҳ<strong>��</strong>���þ� <c>BattleTimeline.CancelGroup</c>��</item>
        /// </list>
        ///
        /// <strong>δ����</strong>���� Legacy ����������ȫû�пɱ༭�ƻ�ʱ��ɾ������<strong>�ܾ�</strong>
        /// ����¼ԭ�򡪡�������Ĭ���˵�"ֱ��ɾ�ƻ�"�ľ�·����
        /// </summary>
        public void RequestDelete(TimelineBlockView block)
        {
            if (block == null) return;
            // ���ߺ��ɾ��·��**������**�� BattleTimeline��New ģʽ����û��������
            // δ����ʱ����������Ӳǰ�ᡣ
            if (Timeline == null && !IsInputPortsBound) return;
            if (block.ActionPlanId == 0) return;

            if (IsInputPortsBound)
            {
                // ���ʶ���� Logic �� ActionPlanId��Ψһ��Դ IViewLogicPort.EditablePlansOf����
                var planId = new ActionPlanId(block.ActionPlanId);
                if (!CanEditBlockInUi(block.ActionPlanId))
                {
                    Debug.LogWarning(
                        "[TimelineEditorUI] �ܾ�ɾ����Ŀ�겻����Ϊ Editable ����ͨ�ƻ���Locked/Running/��Ӧ�ƻ�����ɾ����planId=" +
                        block.ActionPlanId);
                    return;
                }

                if (!InputController.BeginRemoval(planId))
                {
                    Debug.LogWarning("[TimelineEditorUI] �ܾ�ɾ����δ�ܽ���ɾ�����ơ�planId=" + block.ActionPlanId);
                    return;
                }

                ViewSubmissionOutcome outcome = InputController.ConfirmDraft();
                if (!outcome.Submitted)
                {
                    Debug.LogWarning(
                        "[TimelineEditorUI] RemoveEditablePlanOperation δ�ύ��" + outcome.ReasonCode +
                        " / " + outcome.RejectionReasonCode);
                    return;
                }

                // ��ͼ��ֻ���������Ⱦģ�ͣ�Ȩ��ɾ���� Logic ��ͳһ��̬Э������ɣ�
                // ��һ�ο���ͬ����Ѹÿ��Ƴ��ɱ༭���ϡ�
                _placementsByGroupId.Remove(block.ActionPlanId);
                _playerBlocks.Remove(block.ActionPlanId);
                _layoutDirty = true;
                return;
            }

            Debug.LogWarning(
                "[TimelineEditorUI] δע������˿ڣ��ܾ�ɾ����ɾ�� Editable �ƻ����뾭 RemoveEditablePlanOperation �ύ��" +
                "planId=" + block.ActionPlanId);
        }

        /// <summary>�üƻ��� UI ���Ƿ�<strong>������</strong>�ɱ༭��δ����ʱ��Ϊ false����</summary>
        public bool IsPlanEditableInUi(long planId) => CanEditBlockInUi(planId);

        /// <summary>����˿��Ƿ���ע�루�ɿ�ݴ˾����Ƿ񱣳�ֻ�ı�����Ⱦ�ľ��϶���Ϊ����</summary>
        public bool IsInputPortsBound => InputController != null && InputController.Logic != null;

        /// <summary>
        /// �ÿ��Ƿ�ɽ������������ / �Ҽ�ɾ���� UI �ż�����
        /// ���ߺ����"��Ϊ Editable ��ͨ�ƻ�"��ֻ���ж���Locked/Running/��Ӧ�ƻ��� UI �༴ֻ����
        /// </summary>
        private bool IsBlockInteractable(BlockRenderModel model)
            => model != null && model.IsInteractable && IsPlanEditableInUi(model.ActionPlanId);

        /// <summary>
        /// ������������ 09�����������14�����϶�<strong>��Ϊ Editable ����ͨ�ƻ�</strong>��
        ///
        /// <strong>���ߺ�</strong>�������������ƣ�<see cref="ViewInputController.BeginReorder"/>����
        /// �����ڼ��ÿ������ƶ�ֻ���±���Ԥ����ȷ��ʱ��
        /// <see cref="ViewInputController.ConfirmDraft"/> ����Ϊ <c>MoveEditablePlanOperation</c>
        /// ����ͬһ��������ύ������ʱ������ Tick ��<strong>Ψһ�����</strong>
        /// <see cref="ViewSecondsToTick"/> ����ͼ�����Ƴ���������<strong>��</strong>����
        /// <c>BattleTimeline.CancelGroup</c>/<c>placement.Schedule</c> д��ʱ���ߡ�
        ///
        /// <strong>δ����</strong>���ܾ����Ų���¼ԭ�򡪡�������Ĭ���˵���ʱ���ߵ�ֱ��д�롣
        /// </summary>
        public void RequestReposition(TimelineBlockView block)
        {
            if (block == null) return;
            // ���ߺ������·��**������**�� BattleTimeline��New ģʽ����û��������
            // δ����ʱ����������Ӳǰ�ᡣ
            if (Timeline == null && !IsInputPortsBound) return;
            if (block.ActionPlanId == 0) return;
            if (!_playerBlocks.TryGetValue(block.ActionPlanId, out var model)) return;

            if (IsInputPortsBound)
            {
                // ������ ��·�������ʶ = Logic ActionPlanId��legacy placement ��ȫ������ ������
                if (!IsBlockInteractable(model))
                {
                    Debug.LogWarning(
                        "[TimelineEditorUI] �ܾ����ţ�Ŀ�겻����Ϊ Editable ����ͨ�ƻ���planId=" + block.ActionPlanId);
                    return;
                }

                long previewTick = ViewSecondsToTick(model.StartTimeAbs);
                if (!InputController.BeginReorder(new ActionPlanId(block.ActionPlanId), previewTick))
                {
                    Debug.LogWarning("[TimelineEditorUI] �ܾ����ţ�δ�ܽ����������ơ�planId=" + block.ActionPlanId);
                    return;
                }

                // ���Ʊ��壺ֻ������ͼ�ݸ��� ghost����д�߼������ύ���
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

                // ��ͼ��ԭ�㣺��ʱ���ߴ���ʱ�����������ڣ�New ģʽ�� Legacy ʱ���ߣ�ʱ
                // �� 0����λ�㱾������ Logic Tick ͶӰ��ViewSecondsOfTick�����˴�ֻ�����ء�
                float viewNow = Timeline != null ? Timeline.CurrentTime : 0f;
                float xLeftEdge = (model.StartTimeAbs - viewNow) * PixelsPerSecond;
                float xCenter = xLeftEdge + width * 0.5f;
                ghost.SetX(xCenter);
                _pendingLastResolvedCenterX = xCenter;
                _pendingLastMouseXFromLeft = xCenter;
                _pendingGhost = ghost;
                return;
            }

            // ������ δ���ߣ��� Legacy ���������ܾ����Ų���¼ԭ�� ������
            // ��ʵ�ֵ�"δ����"��֧����������ֱ�� Timeline.CancelGroup + ɾ����ģ��
            // ��HEAD:301-308��������һ���ƹ�������ڵ�д�룻���� 09 �����ձ�׼Ҫ��
            // ������������д��·������˸�Ԥд�� B1 �����Ƴ����������־ܾ����塣
            Debug.LogWarning(
                "[TimelineEditorUI] δע������˿ڣ��ܾ����š����� Editable �ƻ����뾭 MoveEditablePlanOperation �ύ��" +
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

            // ���� 09 / B2�����ߺ��϶�**ֻ�Ǳ�������**��
            // ÿһ������ƶ�/����ֻ������ͼԤ�����ݸ壩��ȷ��ʱ����
            // ViewInputController.ConfirmDraft ����Ϊ MoveEditablePlanOperation ��������ύ��
            // ����������� BattleTimeline.CancelGroup / placement.Schedule д��ʱ���ߡ�
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
            // ���� 09 / B2�����Ȩ������ͬ����ActionPlanId ͶӰ�������ھ�ʱ�������ü��
            // **֮ǰ**�������ߺ�� New ģʽ����û�о� BattleTimeline��������Ȼ������ȷ��
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
                // �� Lane �������㣺���ߺ����������ֱ�ӷ��أ��� RecomputePlayerBlocksForLane����
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
                        // �۲� Lane �Ŀ����Ծ�ʱ���߿��յ���ţ�ֻ����Ⱦ����
                        // IsInteractable == false ��ζ����������ɾ��/���ž���·����
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

            // ʹ�� VisualTime
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
            // ���� 09 / B2����3.5 UI ��ھ���������ɾ/�����ϵĿ�������������ա���
            // Locked/Running/��Ӧ�ƻ��� UI �Ͼ���"��ť������"��������"�����Ժ󱻾ܾ�"��
            if (view.Background != null) view.Background.raycastTarget = IsBlockInteractable(model);
        }
        private void EnsureTimeLines()
        {
            if (PlayerLane == null || ObservedLane == null) return;

            if (_currentTimeLinePlayer == null) _currentTimeLinePlayer = CreateLine(PlayerLane, "CurrentTimeLine");
            if (_currentTimeLineObserved == null) _currentTimeLineObserved = CreateLine(ObservedLane, "CurrentTimeLine");
            if (_mouseLinePlayer == null) _mouseLinePlayer = CreateLine(PlayerLane, "MouseLine");
            if (_mouseLineObserved == null) _mouseLineObserved = CreateLine(ObservedLane, "MouseLine");

            // ���� 09�������ߣ����� CurrentTick + 1������ֻ����ǰ����������Ȩ���ж���
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
                // ʹ�� VisualTime
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
            // ʹ�� VisualTime
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

            // ���� 09 / B2 �Ľṹ����������Ӧ����� ��8.3 ��һ������
            // ��ʵ���������ߵ��ײ��� Timeline.CancelGroup(id) + placement.Schedule��
            // ����"û����"ֻ����Ϊ���ߺ� _playerBlocks ǡ��Ϊ�ա������������ɺϣ����Ǳ�֤��
            // ���ߺ����μ����������ʱ���߻�д��ʧȥ���壨λ������ Logic ͶӰ����
            // ����������ʽ�ܾ������� TryLegacyCancelGroup/TryLegacySchedule �ڳ�������һ�Ρ�
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
        /// �����ߣ����� 09�����������14����λ��ȡ�Կ��� <c>CurrentTick + 1</c>��
        ///
        /// ��<strong>ֻ��ʾ</strong>����UI ���ݴ�����Ȩ�ޣ�Ҳ�������Ҳ൱��"�ɱ༭"��֤����
        /// Խ�߱༭�����վܾ����� Logic������Ŀ�� Tick���ƻ� State��Step �����Ž�����
        /// δע��˿�ʱ���������أ������ǻ��˵�"�þ�ʱ���ߵĵ�ǰʱ�̵�������"��
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
            // Ψһ����㣺Tick �� �� ��ͼ�루����������ɢ��д / TicksPerSecond����
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

#endif
