using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Core.Compatibility.Runtime.Input;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.UnityView.Replay;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace ProjectHero.UnityView
{
    /// <summary>Explicitly authored Canvas consumer and input routing. Never owns the battle clock.</summary>
    public sealed class BattlePresentationView : MonoBehaviour, IBattleViewConsumer, IBattleInputConsumer, IBattleViewLifetimeConsumer, IBattleDiagnosticFrameConsumer
    {
        [SerializeField] private MonoBehaviour _bootstrap;
        private IBattleRuntimeHost Runtime => _bootstrap as IBattleRuntimeHost;
        [SerializeField] private BattleViewRegistry _registry;
        [SerializeField] private GridView _grid;
        [SerializeField] private Camera _camera;
        [SerializeField] private string _controllerId;
        [SerializeField] private GameObject _panel;
        [SerializeField] private RectTransform _actions, _targets, _timeline, _reactions;
        [SerializeField] private TMP_Text _status, _previewText, _debug;
        [SerializeField] private TMP_FontAsset _font;
        [SerializeField] private BattleFeedbackPlayer _feedbackPlayer;
        [SerializeField] private BattleFootprintView _footprints;
        [SerializeField] private UnityEngine.UI.Button _actionToggle;
        [SerializeField] private UnityEngine.UI.Button _confirm, _cancel, _concurrent, _pause, _rotate;
        [SerializeField, Min(1)] private float _timelinePixelsPerSecond = 240f;
        public float TimelinePixelsPerSecond => TimelineView != null ? TimelineView.PixelsPerSecond : _timelinePixelsPerSecond;
        public M2TimelineView TimelineView { get; private set; }
        public M3ReactionView ReactionView { get; private set; }
        public bool ResumeChoiceVisible => _hud?.ResumeChoiceVisible == true;
        public Vector3 GridWorld(GridPoint point) => _grid.GridToWorld(point);
        public void PlaceThreatMarker(RectTransform marker, long unitId, int index)
        {
            var unit = Model.Decision.VisibleUnits.FirstOrDefault(u => u.UnitId == unitId);
            if (unit == null || _camera == null) return;
            var screen = _camera.WorldToScreenPoint(GridWorld(new GridPoint(unit.X, unit.Y)));
            var canvas = (RectTransform)_panel.transform;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, screen, null, out var local);
            marker.anchorMin = marker.anchorMax = new Vector2(.5f, .5f); marker.pivot = new Vector2(0, 0);
            marker.sizeDelta = new Vector2(380, 28);
            marker.anchoredPosition = new Vector2(Mathf.Clamp(local.x + 12, -canvas.rect.width * .2f, canvas.rect.width * .2f - 380),
                Mathf.Clamp(local.y + 24 + index * 30, 8, canvas.rect.height * .37f));
        }
        public TrackSelectionModel TrackSelection { get; } = new TrackSelectionModel();
        public bool IsAwaitingWorldSelection => ReactionView?.AwaitingDestination == true || (!string.IsNullOrEmpty(_selectedAction.Value)
            && (Model.Decision.FindAction(_selectedAction)?.Type == ActionType.Attack || Model.Decision.FindAction(_selectedAction)?.Type == ActionType.Move));
        public bool CanConfigurePrimary => Model.Decision != null && Model.Decision.ControlledUnitIds.Contains(_owner);
        // R1：窗口不能被主动结束。旧场景里序列化的"End turn"按钮只在绑定时隐藏，不再接任何命令。
        [SerializeField, UnityEngine.Serialization.FormerlySerializedAs("_close")] private UnityEngine.UI.Button _retiredCloseButton;
        private readonly Dictionary<long, TimelinePlanView> _rows = new Dictionary<long, TimelinePlanView>();
        private readonly List<GameObject> _actionButtons = new List<GameObject>(), _targetButtons = new List<GameObject>(), _reactionButtons = new List<GameObject>();
        private readonly Queue<string> _debugLines = new Queue<string>();
        private bool _bound, _hooked;
        private UnitId _owner, _target;
        private ActionSpecId _selectedAction;
        private GridDirection _facing = GridDirection.EastNorth;
        private string _reactionKey;
        private float _refreshElapsed;
        private TMP_Text _pauseLabel;
        private float _originalCameraSize;
        private Rect _originalCameraRect;
        private Camera _backplateCamera;
        private InputAction _pauseShortcut, _systemShortcut, _trackShortcut;
        private string _autoFocusSignature;
        public void SetHudBattleViewport(bool planning)
        {
            if (_camera != null && _cameraConfigured)
                _camera.rect = planning ? new Rect(.28f, .44f, .42f, .45f) : new Rect(.02f, .36f, .68f, .53f);
        }
        private bool _cameraConfigured;
        private string _lastDamageDebug = string.Empty;
        [SerializeField] private TMP_InputField _replayPath;
        [SerializeField] private TMP_Text _replayStatus;
        [SerializeField] private UnityEngine.UI.Button _replaySave, _replayLoad, _replayPlay, _replayPause, _replayRestart, _replaySpeed;
        private DeveloperReplayControls _replayControls;
        private bool _replayLoaded, _replayHooks;
        private string _replayError;
        private double _replayPlaybackSpeed = 1;
        [SerializeField] private GameObject _developerReplayPanel;
        [SerializeField] private UnityEngine.UI.Button _replayToggle;
        private GridPoint? _draftDestination;
        private ProjectHero.Logic.Movement.DodgeCancellationPreview _selectedDodgePreview;
        private M1HudView _hud;
        private AddOrdinaryPlanOperation _candidate;
        private long _editingDraft;
        private long? _requestedStart;
        public ScheduleEditTransactionResult CandidatePreview { get; private set; }
        public bool HasCandidate => _candidate != null;
        public void ResumeSystemPause() => Runtime.SetPaused(false);
        public BattleClockController Clock => Runtime.Clock;
        public BattlePresentationModel Model { get; } = new BattlePresentationModel();
        public string PlayerControllerId => _controllerId;
        public BattleViewRegistry Registry => _registry;
        public UnitId SelectedActingUnit => _owner;
        public string UnitLabel(long id)
        {
            if (Model.Decision == null) return "单位 " + id;
            if (!Model.Decision.VisibleUnits.Any(u => u.UnitId == id)) return "预计单位 " + id;
            string group = TrackSelectionModel.Group(Model.Decision, id) switch { 0 => "我方", 1 => "友方", 3 => "敌方", _ => "中立" };
            return group + " · 单位 " + id;
        }
        public void SelectTrack(long id, bool control)
        {
            if (IsAwaitingWorldSelection || Clock.State == BattleClockState.SystemPaused || Clock.State == BattleClockState.Ended) return;
            if (!Model.Decision.VisibleUnits.Any(u => u.UnitId == id && u.IsAlive)) return;
            TrackSelection.Click(id, control); SyncPrimary();
        }
        public void FocusTrack(long id)
        {
            if (IsAwaitingWorldSelection || Clock.State == BattleClockState.SystemPaused) return;
            TrackSelection.Focus(id); SyncPrimary();
        }
        public void AddContactTrack(long id)
        { if (!TrackSelection.Contains(id)) SelectTrack(id, true); else FocusTrack(id); }
        public void RemoveTrack(long id)
        { if (IsAwaitingWorldSelection || Clock.State == BattleClockState.SystemPaused) return; TrackSelection.Remove(id); SyncPrimary(); }
        public void CyclePrimary(bool reverse)
        {
            if (IsAwaitingWorldSelection || InputFieldFocused() || Clock.State == BattleClockState.SystemPaused) return;
            TrackSelection.Cycle(Model.Decision, reverse); SyncPrimary();
        }
        public void RefreshPlanningHud() { _hud?.Refresh(); RenderPreview(); }
        private void SyncPrimary()
        {
            var next = new UnitId(TrackSelection.Primary);
            if (_owner != next)
            {
                _owner = next; CancelConfiguration(); RebuildOwnerActions();
            }
            TimelineView?.Refresh(); _hud?.Refresh();
        }
        public void CancelConfiguration()
        {
            ReactionView?.CancelSelection();
            _candidate = null; CandidatePreview = null; _editingDraft = 0; _selectedAction = default; _selectedReaction = 0;
            _selectedDodgePreview = null; _target = default; _grid?.ClearAttackPreview(); RenderPreview();
        }
        public void ConfigureFeedback(BattleFeedbackPlayer player) => _feedbackPlayer = player;
        public void ConfigureFootprints(BattleFootprintView footprints) => _footprints = footprints;
        public void ConfigureActionToggle(UnityEngine.UI.Button button) => _actionToggle = button;
        public void ConfigureReplay(GameObject panel, UnityEngine.UI.Button toggle, TMP_InputField path, TMP_Text status, UnityEngine.UI.Button save, UnityEngine.UI.Button load,
            UnityEngine.UI.Button play, UnityEngine.UI.Button pause, UnityEngine.UI.Button restart, UnityEngine.UI.Button speed)
        { _developerReplayPanel = panel; _replayToggle = toggle; _replayPath = path; _replayStatus = status; _replaySave = save; _replayLoad = load; _replayPlay = play; _replayPause = pause; _replayRestart = restart; _replaySpeed = speed; }
        public void AdvanceDiagnostics(double unscaledSeconds)
        {
            if (_bound && _replayLoaded) _replayControls.AdvanceFrame(unscaledSeconds);
        }
        private void ReplayAction(Action operation)
        {
            try { operation(); _replayError = null; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ProjectHero.Logic.LogicDefinitionException
                || error is InvalidOperationException || error is ArgumentException)
            { _replayError = error.Message; }
            RenderReplayStatus();
        }
        private void BindReplay()
        {
            _replayControls?.Dispose(); _replayControls = new DeveloperReplayControls(Model.Decision.Definition);
            _replayLoaded = false; _replayError = null; _replayPlaybackSpeed = 1;
            if (_replayPath == null) return;
            _developerReplayPanel.SetActive(false);
            _replayToggle.gameObject.SetActive(Debug.isDebugBuild || Application.isEditor);
            if (string.IsNullOrEmpty(_replayPath.text)) _replayPath.text = Path.Combine(Application.persistentDataPath, "developer-battle.phr");
            if (_replayHooks) return;
            _replayToggle.onClick.AddListener(() => _developerReplayPanel.SetActive(!_developerReplayPanel.activeSelf));
            _replaySave.onClick.AddListener(() => ReplayAction(() => {
                var replay = Runtime.NewDriver.RecordedReplay;
                if (replay == null) throw new InvalidOperationException(Runtime.NewDriver.ReplayRecordingError ?? "REPLAY_RECORDING_UNAVAILABLE");
                using (var stream = File.Create(_replayPath.text)) ProjectHero.Logic.Replay.ReplayFile.Save(stream, replay);
            }));
            _replayLoad.onClick.AddListener(() => ReplayAction(() => {
                _replayLoaded = false; _replayControls.Pause();
                using (var stream = File.OpenRead(_replayPath.text)) _replayControls.Load(stream);
                _replayLoaded = true; _replayControls.SetSpeed(_replayPlaybackSpeed);
            }));
            _replayPlay.onClick.AddListener(() => ReplayAction(() => _replayControls.Play()));
            _replayPause.onClick.AddListener(() => ReplayAction(() => _replayControls.Pause()));
            _replayRestart.onClick.AddListener(() => ReplayAction(() => _replayControls.Restart()));
            _replaySpeed.onClick.AddListener(() => ReplayAction(() => { _replayPlaybackSpeed = _replayPlaybackSpeed >= 64 ? 1 : _replayPlaybackSpeed * 2; _replayControls.SetSpeed(_replayPlaybackSpeed); }));
            _replayHooks = true;
        }
        private void RenderReplayStatus()
        {
            if (_replayStatus == null || _replayControls == null) return;
            var player = _replayControls.Player;
            _replayStatus.text = "Developer replay  " + _replayPlaybackSpeed + "x  Tick " + (player.CurrentSnapshot?.Tick ?? -1)
                + (_replayLoaded ? player.IsComplete ? "  complete" : player.IsPaused ? "  paused" : "  playing" : "  no file loaded")
                + (player.Deviation == null ? "" : "\nFirst mismatch: " + player.Deviation.Tick + "  " + player.Deviation.ReasonCode
                    + "  event " + player.Deviation.EventIndex + "\nExpected: " + player.Deviation.Expected + "\nActual: " + player.Deviation.Actual)
                + (_replayError == null ? "" : "\n" + _replayError);
        }
        public void SetActionPanelVisible(bool visible)
        { Model.SetActionPanelVisible(visible); _actions.parent.gameObject.SetActive(visible); }
        public void Configure(MonoBehaviour bootstrap, BattleViewRegistry registry, GridView grid, Camera camera,
            string controller, GameObject panel, RectTransform actions, RectTransform targets, RectTransform timeline,
            RectTransform reactions, TMP_Text status, TMP_Text preview, TMP_Text debug, TMP_FontAsset font,
            UnityEngine.UI.Button confirm, UnityEngine.UI.Button cancel,
            UnityEngine.UI.Button concurrent, UnityEngine.UI.Button pause, UnityEngine.UI.Button rotate)
        {
            _bootstrap = bootstrap; _registry = registry; _grid = grid; _camera = camera; _controllerId = controller;
            _panel = panel; _actions = actions; _targets = targets; _timeline = timeline; _reactions = reactions;
            _status = status; _previewText = preview; _debug = debug; _font = font;
            _confirm = confirm; _cancel = cancel; _concurrent = concurrent; _pause = pause; _rotate = rotate;
        }
        public void Bind(BattleInitializationResult mapping, LogicSnapshot snapshot)
        {
            if (_registry == null || Runtime == null || _panel == null) throw new InvalidOperationException("VIEW_PRESENTATION_BINDING_MISSING");
            _registry.Bind(mapping, snapshot);
            TrackSelection.Layout.Bind(mapping);
            _feedbackPlayer?.Bind();
            if (_camera != null && _camera.orthographic && !_cameraConfigured)
            { _originalCameraSize = _camera.orthographicSize; _originalCameraRect = _camera.rect; _camera.orthographicSize = 12; _cameraConfigured = true; }
            if (_cameraConfigured && _backplateCamera == null)
            {
                var background = new GameObject("HudBackplateCamera", typeof(Camera)); background.transform.SetParent(transform, false);
                _backplateCamera = background.GetComponent<Camera>(); _backplateCamera.depth = _camera.depth - 1;
                _backplateCamera.cullingMask = 0; _backplateCamera.clearFlags = CameraClearFlags.SolidColor;
                _backplateCamera.backgroundColor = new Color(.015f, .025f, .04f, 1);
            }
            _panel.SetActive(true); _debugLines.Clear(); _lastDamageDebug = string.Empty; _reactionKey = null;
            _pauseLabel = _pause.GetComponentInChildren<TMP_Text>();
            if (_retiredCloseButton != null) _retiredCloseButton.gameObject.SetActive(false);
            if (!_hooked)
            {
                _registry.Feedback.FeedbackRequested += OnFeedback;
                _confirm.onClick.AddListener(() => { if (_selectedReaction != 0) ConfirmSelectedReaction(); else Model.ConfirmDraft(); RenderPreview(); });
                _cancel.onClick.AddListener(() => { Model.CancelDraft(); _candidate = null; CandidatePreview = null; _editingDraft = 0; _selectedAction = default; _selectedReaction = 0; _selectedDodgePreview = null; RenderPreview(); });
                _concurrent.onClick.AddListener(() => { Model.ActivateConcurrent(); Render(); });
                _pause.onClick.AddListener(ToggleTacticalPause);
                // Facing is an action parameter selected on the battlefield, not a unit rotation command.
                if (_actionToggle != null) _actionToggle.onClick.AddListener(() => SetActionPanelVisible(!Model.ActionPanelVisible));
                _hooked = true;
            }
            if (_rotate != null) _rotate.gameObject.SetActive(false);
        }
        public void ReleaseVisuals()
        {
            _bound = false; Model.Drafts.Reset(); Model.CancelDraft(); _candidate = null; _editingDraft = 0; _selectedReaction = 0; _selectedAction = default; _selectedDodgePreview = null;
            if (_hud != null) _hud.Release();
            ReactionView?.Release();
            Clock.Changed -= OnClockChanged;
            _pauseShortcut?.Dispose(); _systemShortcut?.Dispose(); _trackShortcut?.Dispose(); _pauseShortcut = _systemShortcut = _trackShortcut = null;
            TimelineView?.ClearSnap(); TrackSelection.Reset(); _autoFocusSignature = null;
            if (_backplateCamera != null) { Destroy(_backplateCamera.gameObject); _backplateCamera = null; }
            _replayControls?.Dispose(); _replayControls = null; _replayLoaded = false;
            if (_developerReplayPanel != null) _developerReplayPanel.SetActive(false);
            foreach (var row in _rows.Values) if (row != null) Destroy(row.gameObject);
            _rows.Clear(); Clear(_reactionButtons); Clear(_actionButtons); Clear(_targetButtons); _debugLines.Clear();
            if (_feedbackPlayer != null) _feedbackPlayer.Unbind();
            if (_footprints != null) _footprints.ReleaseVisuals();
            _grid?.ReleaseBackdrop();
            if (_registry != null) _registry.ReleaseVisuals();
            if (_panel != null) _panel.SetActive(false);
            if (_cameraConfigured && _camera != null) { _camera.orthographicSize = _originalCameraSize; _camera.rect = _originalCameraRect; }
            _cameraConfigured = false;
        }
        public void BindInput(ViewInputPorts ports)
        {
            Model.BindClock(Clock); Model.Bind(ports); _bound = true; _owner = Model.Decision.ControlledUnitIds[0];
            Runtime.NewDriver.EnableHudClock(new ControllerId(_controllerId));
            _hud = GetComponent<M1HudView>() ?? gameObject.AddComponent<M1HudView>();
            _hud.Bind(this, (RectTransform)_panel.transform, _font, _actions, _targets, _timeline, _reactions, _status, _previewText);
            _font = _hud.HudFont;
            TrackSelection.Reset(); TrackSelection.Focus(_owner.Value, false); _autoFocusSignature = null;
            TimelineView = GetComponent<M2TimelineView>() ?? gameObject.AddComponent<M2TimelineView>();
            TimelineView.Bind(this, _timeline, _font, _status);
            ReactionView = GetComponent<M3ReactionView>() ?? gameObject.AddComponent<M3ReactionView>();
            ReactionView.Bind(this, (RectTransform)_panel.transform, _font, _reactions);
            Clock.Changed -= OnClockChanged; Clock.Changed += OnClockChanged; OnClockChanged();
            _pauseShortcut = new InputAction("Tactical pause", InputActionType.Button, "<Keyboard>/space");
            _pauseShortcut.performed += _ =>
            {
                if (_bound && !InputFieldFocused() && !Keyboard.current.shiftKey.isPressed
                    && !Keyboard.current.ctrlKey.isPressed && !Keyboard.current.altKey.isPressed) ToggleTacticalPause();
            };
            _systemShortcut = new InputAction("System pause", InputActionType.Button, "<Keyboard>/escape");
            _systemShortcut.performed += _ =>
            {
                if (!_bound || InputFieldFocused()) return;
                var drag = GetComponentsInChildren<TimelinePlanView>(true).FirstOrDefault(row => row.IsDragging);
                if (drag != null) { drag.CancelDrag(); return; }
                if (ReactionView?.CancelSelection() == true) { _hud.Refresh(); return; }
                if (IsAwaitingWorldSelection) { CancelConfiguration(); _hud.Refresh(); return; }
                Runtime.SetPaused(Clock.State != BattleClockState.SystemPaused); Render();
            };
            _trackShortcut = new InputAction("Cycle primary track", InputActionType.Button, "<Keyboard>/tab");
            _trackShortcut.performed += _ => CyclePrimary(Keyboard.current?.shiftKey.isPressed == true);
            _pauseShortcut.Enable(); _systemShortcut.Enable(); _trackShortcut.Enable();
            BindReplay();
            _grid.BindBoundary(Model.Decision.Definition.FindEncounter(Runtime.NewDriver.ReplayHeader.EncounterId).GridBoundary);
            _footprints?.Bind(Model.Decision.Definition); _footprints?.Consume(Model.Decision.VisibleUnits);
            RebuildOwnerActions();
            Render();
        }
        private void OnClockChanged()
        {
            _registry.SetPresentationPaused(Clock.IsPaused); _feedbackPlayer?.SetPresentationPaused(Clock.IsPaused);
            string focus = Model.Decision?.Tick + ":" + Clock.FocusUnitId + ":" + Clock.PauseReason;
            if (Clock.FocusUnitId > 0 && Clock.State == BattleClockState.TacticalPlanning && Model.Decision != null && focus != _autoFocusSignature)
            {
                _autoFocusSignature = focus; TrackSelection.Focus(Clock.FocusUnitId, false);
                if (Clock.PauseReason == "发现可响应的公开威胁")
                    foreach (var opportunity in Model.Reactions.Where(r => r.DefenderUnitId == Clock.FocusUnitId && r.SourceImpactTick > Model.Decision.Tick))
                    {
                        var attack = Model.Timeline.FirstOrDefault(p => p.ActionPlanId == opportunity.SourceAttackPlanId);
                        if (attack != null) TrackSelection.AddTemporary(attack.OwnerUnitId);
                    }
                SyncPrimary();
            }
            if (Clock.State == BattleClockState.SystemPaused || Clock.State == BattleClockState.Ended) TimelineView?.ClearSnap();
            if (_bound) { _hud?.Refresh(); ReactionView?.Refresh(); }
        }
        private void RebuildOwnerActions()
        {
            Clear(_actionButtons); Clear(_targetButtons);
            if (!CanConfigurePrimary) return;
            foreach (var action in Model.Decision.ActionSetOf(_owner))
            {
                var spec = Model.Decision.FindAction(action);
                if (spec.Type == ActionType.Block || spec.Type == ActionType.Dodge) continue;
                var captured = action;
                var words = action.Value.Split('.');
                var label = words.Length > 1 ? words[1].Replace('_', ' ') : spec.Type.ToString();
                label = M1HudView.ActionName(action.Value);
                var button = CreateButton(_actions, label, () => SelectAction(captured));
                var size = button.GetComponent<UnityEngine.UI.LayoutElement>(); size.minHeight = size.preferredHeight = 28;
                _actionButtons.Add(button);
            }
        }
        public void Consume(EventBatch events, LogicSnapshot snapshot)
        {
            if (this == null || _registry == null) return;
            _registry.Consume(events, snapshot);
            if (_bound) _footprints?.Consume(snapshot.Units);
            if (_bound)
            {
                Model.Synchronize(events);
                if (Model.PauseForConcurrentReceipt)
                { Model.AcknowledgeConcurrentPause(); Clock.PauseTactically("介入授权已接受，可追加普通动作"); }
                TrackSelection.Layout.Consume(events);
                TrackSelection.Prune(Model.Decision);
                TrackSelection.SyncTemporary(Model.Reactions.Where(r => r.SourceImpactTick > Model.Decision.Tick)
                    .Select(r => Model.Timeline.FirstOrDefault(p => p.ActionPlanId == r.SourceAttackPlanId)?.OwnerUnitId ?? 0));
                SyncPrimary();
                if (_selectedReaction != 0)
                {
                    _selectedDodgePreview = Model.PreviewDodge(_selectedReaction, _reactionAction, _reactionDestination);
                    if (_selectedDodgePreview.RejectionCode != null) { _selectedReaction = 0; _selectedDodgePreview = null; }
                }
                RenderTimeline(); if (Clock.State != BattleClockState.Running) RenderReactions(); RenderPreview();
            }
        }
        public void SelectAction(ActionSpecId action)
        {
            if (!CanConfigurePrimary) return;
            ReactionView?.CancelSelection();
            _grid?.ClearAttackPreview();
            _candidate = null; CandidatePreview = null; _editingDraft = 0; _requestedStart = null; _draftDestination = null;
            _selectedAction = action; _selectedReaction = 0; _selectedDodgePreview = null; _target = default; Clear(_targetButtons);
            _hud?.ResetStart();
            var spec = Model.Decision.FindAction(action);
            if (spec.Type == ActionType.Guard) DraftAction(null);
            RenderPreview();
        }
        public void DraftAction(GridPoint? destination, long? startTick = null)
        {
            if (string.IsNullOrEmpty(_selectedAction.Value)) return;
            _draftDestination = destination;
            var spec = Model.Decision.FindAction(_selectedAction);
            _requestedStart = startTick;
            _candidate = new AddOrdinaryPlanOperation(1, _owner, _selectedAction,
                startTick, PrimaryTargetUnitId: spec.Type == ActionType.Attack && _target.IsValid ? _target : (UnitId?)null, Facing: _facing, Destination: destination);
            if (_editingDraft != 0) Model.EditDraft(_editingDraft, _candidate);
            CandidatePreview = _editingDraft != 0 ? Model.Preview : Model.PreviewCandidate(_candidate);
            RenderPreview();
        }
        public void AddConfiguredDraft()
        {
            if (_candidate == null || !Model.CanPlan) return;
            if (_editingDraft == 0) Model.AppendDraft(_candidate);
            _editingDraft = 0; _candidate = null; CandidatePreview = null; _selectedAction = default;
            _grid?.ClearAttackPreview();
            _hud?.Refresh(); RenderPreview();
        }
        public void EditConfiguredDraft(long id)
        {
            foreach (var entry in Model.Drafts.Entries)
                if (entry.Id == id && entry.Operation is AddOrdinaryPlanOperation add)
                {
                    SelectAction(add.ActionSpecId); _editingDraft = id; _owner = add.OwnerUnitId;
                    _target = add.PrimaryTargetUnitId ?? default; _facing = add.Facing;
                    _draftDestination = add.Destination; _requestedStart = add.RequestedStartTick;
                    DraftAction(add.Destination, add.RequestedStartTick); _hud?.SetStart(add.RequestedStartTick); return;
                }
        }
        public void SetCandidateStart(long? tick) { _requestedStart = tick; if (_candidate != null) DraftAction(_draftDestination, tick); }
        public void ToggleTacticalPause()
        {
            if (Clock.State == BattleClockState.Running) Clock.PauseTactically();
            else if (Clock.State == BattleClockState.TacticalPlanning)
            {
                if (Model.HasDraft && Model.Drafts.State != ScheduleDraftState.AwaitingResume) _hud.ShowResumeChoice();
                else ResumePlanning(false);
            }
            Render();
        }
        public void ResumePlanning(bool apply)
        {
            if (Clock.State != BattleClockState.TacticalPlanning) return;
            if (apply && Model.HasDraft && Model.ConfirmDraft() != null) return;
            Runtime.NewDriver.ResumeTactically();
            string code = Model.SubmitOnResume();
            if (code != null) Clock.PauseTactically("排程未提交，请调整");
            _hud?.HideResumeChoice(); Render();
        }
        private void LateUpdate()
        {
            if (!_bound) return;
            _refreshElapsed += Time.unscaledDeltaTime;
            if (_refreshElapsed >= 0.1f) { _refreshElapsed = 0; Render(); }
            if (ReactionView?.AwaitingDestination == true)
            {
                if (Model.CanPlan && !_hud.ResumeChoiceVisible && Mouse.current?.leftButton.wasPressedThisFrame == true && _camera != null
                    && _camera.pixelRect.Contains(Mouse.current.position.ReadValue()) && !(EventSystem.current?.IsPointerOverGameObject() ?? false)
                    && _grid.TryPick(_camera.ScreenPointToRay(Mouse.current.position.ReadValue()), out var landing)) ReactionView.SelectDestination(landing);
                return;
            }
            if (!IsAwaitingWorldSelection && Mouse.current != null && _camera != null && Clock.State != BattleClockState.SystemPaused
                && Clock.State != BattleClockState.Ended && !_hud.ResumeChoiceVisible && Mouse.current.leftButton.wasPressedThisFrame
                && _camera.pixelRect.Contains(Mouse.current.position.ReadValue()) && !(EventSystem.current?.IsPointerOverGameObject() ?? false)
                && _grid.TryPick(_camera.ScreenPointToRay(Mouse.current.position.ReadValue()), out var selectedCell))
            {
                foreach (var unit in Model.Decision.VisibleUnits.Where(u => u.IsAlive).OrderBy(u => u.UnitId))
                    if (VolumeFootprint.ResolveCells(Model.Decision.Definition.VolumeDirectionsOf(new UnitDefinitionId(unit.DefinitionId)),
                        new GridPoint(unit.X, unit.Y), (GridDirection)unit.Facing).Contains(selectedCell))
                    { SelectTrack(unit.UnitId, M2TimelineView.ControlPressed()); break; }
            }
            if (!Model.CanPlan || !Model.ActionPanelVisible || string.IsNullOrEmpty(_selectedAction.Value)
                || _hud.ResumeChoiceVisible || Mouse.current == null || _camera == null)
            { _grid?.ClearAttackPreview(); return; }
            var spec = Model.Decision.FindAction(_selectedAction);
            var pointer = Mouse.current.position.ReadValue();
            if (!_camera.pixelRect.Contains(pointer) || (EventSystem.current?.IsPointerOverGameObject() ?? false)
                || !_grid.TryPick(_camera.ScreenPointToRay(pointer), out var cell))
            { _grid.ClearAttackPreview(); return; }
            if (spec.Type == ActionType.Attack)
            {
                PreviewAttackCell(cell);
                if (Mouse.current.leftButton.wasPressedThisFrame) SelectAttackCell(cell);
            }
            else if (spec.Type == ActionType.Move && Mouse.current.leftButton.wasPressedThisFrame) DraftAction(cell, _requestedStart);
        }
        private UnitSnapshot ActingUnit()
        {
            foreach (var unit in Model.Decision.VisibleUnits) if (unit.UnitId == _owner.Value) return unit;
            return null;
        }
        private void PreviewAttackCell(GridPoint cell)
        {
            if (!(Model.Decision.FindAction(_selectedAction)?.Payload is AttackPayloadSpec attack)) return;
            var owner = ActingUnit();
            if (owner == null || (owner.X == cell.X && owner.Y == cell.Y)) { _grid.ClearAttackPreview(); return; }
            var origin = new GridPoint(owner.X, owner.Y);
            var facing = GridView.DirectionTo(origin, cell);
            _grid.ShowAttackPreview(VolumeFootprint.ResolveTriangles(attack.Pattern.Directions, origin, facing));
        }
        /// <summary>One battlefield click selects both the primary target (if present) and attack direction.
        /// Only local configuration changes here; the existing draft transaction performs authoritative validation.</summary>
        public bool SelectAttackCell(GridPoint cell)
        {
            if (!Model.CanPlan || !(Model.Decision.FindAction(_selectedAction)?.Payload is AttackPayloadSpec attack)) return false;
            var owner = ActingUnit();
            if (owner == null || (owner.X == cell.X && owner.Y == cell.Y)) return false;
            _facing = GridView.DirectionTo(new GridPoint(owner.X, owner.Y), cell);
            _target = default;
            foreach (var target in Model.Targets(_owner, _selectedAction))
            {
                var directions = Model.Decision.Definition.VolumeDirectionsOf(new UnitDefinitionId(target.DefinitionId));
                foreach (var occupied in VolumeFootprint.ResolveCells(directions, new GridPoint(target.X, target.Y), (GridDirection)target.Facing))
                    if (occupied == cell && (!_target.IsValid || target.UnitId < _target.Value)) _target = new UnitId(target.UnitId);
            }
            // A fixed-target attack keeps an invalid configuration visible when the clicked cell is empty.
            DraftAction(null, _requestedStart);
            PreviewAttackCell(cell);
            return true;
        }
        private static bool InputFieldFocused()
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.TryGetComponent<TMP_InputField>(out var input) && input != null && input.isFocused;
        }
        private void Render()
        {
            if (!_bound) return;
            var text = new StringBuilder();
            foreach (var unit in Model.Decision.VisibleUnits)
            {
                text.Append("单位 ").Append(unit.UnitId).Append("  生命 ").Append(unit.HealthQ10 / 1024f).Append("  ").Append(unit.StateValue);
                foreach (var ledger in Model.Adrenaline)
                    if (ledger.UnitId == unit.UnitId)
                        text.Append("  肾上腺素 ").Append(ledger.AvailableAdrenaline).Append("  预留 ").Append(ledger.ReservedTotal);
                text.Append('\n');
            }
            var window = Model.Window;
            if (window != null) text.Append("窗口拥有者 ").Append(window.OwnerUnitId).Append(" · 剩余 ")
                .Append((Math.Max(0L, window.EndTick - Model.Decision.Tick - 1L)
                    / (double)Model.Decision.Definition.TicksPerSecond).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" s\n");
            if (Model.Decision.BattleEnd.IsEnded) text.Append(Model.Decision.BattleEnd.ResultCode);
            _status.text = text.ToString();
            _confirm.interactable = ((Model.HasDraft && Model.Preview?.Succeeded == true && Model.Drafts.State != ScheduleDraftState.Stale)
                || (_selectedReaction != 0 && _selectedDodgePreview?.RejectionCode == null)) && Model.CanPlan;
            _cancel.interactable = Model.CanPlan && Model.Drafts.State != ScheduleDraftState.AwaitingAcceptance;
            _concurrent.interactable = Model.CanPlan && Model.Facts.Permission == HudSubmissionPermission.CanIntervene && !Model.ConcurrentPending;
            _concurrent.GetComponentInChildren<TMP_Text>().text = Model.ConcurrentPending ? Model.ConcurrentStatus : Model.Facts.Permission == HudSubmissionPermission.Intervened ? "已介入" : "介入";
            if (_pauseLabel != null) _pauseLabel.text = Clock.State == BattleClockState.TacticalPlanning ? "继续时间" : "暂停并规划";
            _debug.transform.parent.gameObject.SetActive((Debug.isDebugBuild || Application.isEditor)
                && _developerReplayPanel != null && _developerReplayPanel.activeSelf && _debugLines.Count > 0);
            _debug.overflowMode = TextOverflowModes.Truncate;
            _debug.text = string.Join("\n", _debugLines) + _lastDamageDebug;
            RenderReplayStatus();
            RenderPreview();
            _hud?.Refresh();
            RenderTimeline();
            ReactionView?.Refresh();
        }
        private void RenderPreview()
        {
            _previewText.overflowMode = TextOverflowModes.Truncate;
            if (Model.Decision.BattleEnd.IsEnded)
            { _previewText.text = "Battle finished: " + Model.Decision.BattleEnd.ResultCode; return; }
            var text = new StringBuilder();
            if (!string.IsNullOrEmpty(_selectedAction.Value) && Model.Decision.FindAction(_selectedAction)?.Type == ActionType.Attack)
                text.Append("战场指向显示红色攻击范围，点击确定方向与目标。\n");
            if (_selectedReaction != 0 && _selectedDodgePreview != null)
            {
                text.Append("If Dodge relocates: cancel ").Append(_selectedDodgePreview.AffectedPlanIds.Count).Append(" future moves.");
            }
            else if (_candidate != null)
            {
                text.Append(CandidatePreview?.Succeeded == true ? "配置就绪，可添加至草稿" : "配置不可用：" + M1HudView.Explain(CandidatePreview?.RejectionCode));
                if (CandidatePreview?.Succeeded == true)
                    foreach (var projection in CandidatePreview.PreviewPlans ?? Array.Empty<SchedulePlanPreview>())
                        text.Append($"\n{M1HudView.ActionName(projection.Plan.ActionSpecId)}  {projection.Plan.StartTick / 60d:0.000}s → {projection.Plan.EndTick / 60d:0.000}s");
            }
            else if (!Model.HasDraft) text.Append("选择动作，再选择目标或战场落点。\n添加多条草稿后应用排程。");
            else if (Model.Preview?.Succeeded != true) text.Append("无法应用：").Append(M1HudView.Explain(Model.Preview?.RejectionCode));
            else foreach (var projection in Model.Preview.PreviewPlans ?? Array.Empty<ProjectHero.Logic.Timeline.SchedulePlanPreview>())
            {
                var plan = projection.Plan;
                text.Append($"{M1HudView.ActionName(plan.ActionSpecId)}  {plan.StartTick / 60d:0.000}s → {plan.EndTick / 60d:0.000}s\n");
            }
            if (Model.LastRejection != null) text.Append("\n").Append(M1HudView.Explain(Model.LastRejection));
            foreach (var capability in Model.HudPreviews?.OpponentCapabilities ?? Array.Empty<OpponentReactionCapability>())
            {
                if (!capability.IsWithinKnownHorizon) continue;
                text.Append("\nAttack ").Append(capability.AttackPlanId).Append(" -> Unit ").Append(capability.TargetUnitId).Append(": ");
                if (!capability.CanBlock && !capability.CanDodge) text.Append("Opponent cannot react");
                else text.Append(capability.CanBlock ? "Can Block " : "").Append(capability.CanDodge ? "Can Dodge " : "");
                if (capability.IsInRecovery) text.Append(" (in recovery)");
            }
            _previewText.text = text.ToString();
        }
        private void RenderTimeline()
        {
            TimelineView?.Refresh();
        }
        private void RenderReactions()
        {
            if (ReactionView != null) { ReactionView.Refresh(); return; }
            var key = new StringBuilder();
            foreach (var opportunity in Model.Reactions)
                foreach (var option in opportunity.Options ?? Array.Empty<ReactionOptionSnapshot>())
                    if (option.IsOpen && option.IsPublished && Model.LockLine <= option.ResponseDeadlineTick)
                    {
                        key.Append(opportunity.ReactionOpportunityId).Append(':').Append(option.ActionSpecId).Append(':').Append(option.ResponseDeadlineTick).Append(';');
                        if (Model.Decision.FindAction(new ActionSpecId(option.ActionSpecId)).Type == ActionType.Dodge)
                            foreach (var cell in Model.DodgeDestinations(opportunity.ReactionOpportunityId, new ActionSpecId(option.ActionSpecId)))
                                key.Append(cell.X).Append(',').Append(cell.Y).Append(';');
                    }
            _reactions.parent.parent.gameObject.SetActive(key.Length > 0);
            if (key.ToString() == _reactionKey) return;
            _reactionKey = key.ToString(); Clear(_reactionButtons);
            foreach (var opportunity in Model.Reactions)
                foreach (var option in opportunity.Options ?? Array.Empty<ReactionOptionSnapshot>())
                {
                    if (!option.IsOpen || !option.IsPublished || Model.LockLine > option.ResponseDeadlineTick) continue;
                    long id = opportunity.ReactionOpportunityId; var action = new ActionSpecId(option.ActionSpecId);
                    var spec = Model.Decision.FindAction(action);
                    string label = spec.Type + "  cost " + spec.AdrenalineCost + "  deadline " + option.ResponseDeadlineTick + "  default start " + opportunity.SourceImpactTick;
                    if (spec.Type == ActionType.Block)
                        _reactionButtons.Add(CreateButton(_reactions, label, () => Model.ConfirmReaction(id, action, null)));
                    else foreach (var destination in Model.DodgeDestinations(id, action))
                    {
                        var captured = destination;
                        _reactionButtons.Add(CreateButton(_reactions, label + " -> " + destination, () =>
                        {
                            var preview = Model.PreviewDodge(id, action, captured);
                            // Selecting a destination is a local preview. A separate confirm click sends the reaction.
                            _selectedDodgePreview = preview;
                            _selectedReaction = id; _reactionAction = action; _reactionDestination = captured;
                            RenderPreview(); _confirm.interactable = preview.RejectionCode == null;
                        }));
                    }
                }
        }
        private long _selectedReaction;
        private ActionSpecId _reactionAction;
        private GridPoint _reactionDestination;
        private void ConfirmSelectedReaction()
        {
            Model.ConfirmReaction(_selectedReaction, _reactionAction, _reactionDestination);
            _selectedReaction = 0; _selectedDodgePreview = null;
        }
        private void OnFeedback(CombatFeedbackCue cue)
        {
            if (!Debug.isDebugBuild && !Application.isEditor) return;
            _debugLines.Enqueue("T" + cue.Fact.Tick + "  Unit " + cue.UnitId + "  " + cue.Kind);
            while (_debugLines.Count > 4) _debugLines.Dequeue();
            if (cue.Fact is DamageChannelResolvedEvent damage)
            {
                var text = new StringBuilder("\nDamage Q10: raw / after passive / after action");
                foreach (var channel in damage.Channels ?? Array.Empty<DamageChannelEntry>())
                    text.Append('\n').Append(channel.ChannelId.Value).Append(": ").Append(channel.RawQ10).Append(" / ")
                        .Append(channel.AfterPassiveResistanceQ10).Append(" / ").Append(channel.AfterActionResistanceQ10);
                text.Append("\nMomentum: ").Append(damage.IncomingMomentumUnits).Append(" -> ").Append(damage.AfterMomentumResistanceUnits)
                    .Append("; final damage: ").Append(damage.AfterBlockDamageQ10);
                _lastDamageDebug = text.ToString();
            }
        }
        private GameObject CreateButton(RectTransform parent, string label, UnityEngine.Events.UnityAction click)
        {
            var go = new GameObject("BattleButton", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button), typeof(UnityEngine.UI.LayoutElement));
            go.transform.SetParent(parent, false); var layout = go.GetComponent<UnityEngine.UI.LayoutElement>(); layout.preferredHeight = 38; layout.minHeight = 38;
            go.GetComponent<UnityEngine.UI.Image>().color = new Color(0.12f, 0.19f, 0.27f, 0.98f);
            go.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(click);
            var textGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)); textGo.transform.SetParent(go.transform, false);
            var rect = (RectTransform)textGo.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(8, 2); rect.offsetMax = new Vector2(-8, -2);
            var text = textGo.GetComponent<TextMeshProUGUI>(); text.font = _font; text.fontSize = 16; text.enableAutoSizing = false; text.raycastTarget = false; text.text = label;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            return go;
        }
        private void Clear(List<GameObject> objects) { foreach (var go in objects) if (go != null) Destroy(go); objects.Clear(); }
    }
}
