using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Core.Compatibility.Runtime.Input;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Snapshots;
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
        [SerializeField] private BattleRuntimeBootstrap _bootstrap;
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
        [SerializeField] private GameObject[] _legacyCanvases = Array.Empty<GameObject>();
        [SerializeField] private UnityEngine.UI.Button _actionToggle;
        [SerializeField] private Behaviour[] _legacyPresentation = Array.Empty<Behaviour>();
        [SerializeField] private UnityEngine.UI.Button _confirm, _cancel, _close, _concurrent, _pause, _rotate;
        private readonly Dictionary<long, TimelinePlanView> _rows = new Dictionary<long, TimelinePlanView>();
        private readonly List<GameObject> _actionButtons = new List<GameObject>(), _targetButtons = new List<GameObject>(), _reactionButtons = new List<GameObject>();
        private readonly Queue<string> _debugLines = new Queue<string>();
        private bool _bound, _hooked;
        private bool[] _legacyCanvasStates, _legacyBehaviourStates;
        private UnitId _owner, _target;
        private ActionSpecId _selectedAction;
        private GridDirection _facing = GridDirection.EastNorth;
        private string _reactionKey;
        private float _refreshElapsed;
        private TMP_Text _pauseLabel;
        private float _originalCameraSize;
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
        public BattlePresentationModel Model { get; } = new BattlePresentationModel();
        public string PlayerControllerId => _controllerId;
        public BattleViewRegistry Registry => _registry;
        public void ConfigureFeedback(BattleFeedbackPlayer player) => _feedbackPlayer = player;
        public void ConfigureFootprints(BattleFootprintView footprints) => _footprints = footprints;
        public void ConfigureLegacyCanvases(GameObject[] canvases) => _legacyCanvases = canvases;
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
                var replay = _bootstrap.NewDriver.RecordedReplay;
                if (replay == null) throw new InvalidOperationException(_bootstrap.NewDriver.ReplayRecordingError ?? "REPLAY_RECORDING_UNAVAILABLE");
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
        public void Configure(BattleRuntimeBootstrap bootstrap, BattleViewRegistry registry, GridView grid, Camera camera,
            string controller, GameObject panel, RectTransform actions, RectTransform targets, RectTransform timeline,
            RectTransform reactions, TMP_Text status, TMP_Text preview, TMP_Text debug, TMP_FontAsset font,
            Behaviour[] legacy, UnityEngine.UI.Button confirm, UnityEngine.UI.Button cancel, UnityEngine.UI.Button close,
            UnityEngine.UI.Button concurrent, UnityEngine.UI.Button pause, UnityEngine.UI.Button rotate)
        {
            _bootstrap = bootstrap; _registry = registry; _grid = grid; _camera = camera; _controllerId = controller;
            _panel = panel; _actions = actions; _targets = targets; _timeline = timeline; _reactions = reactions;
            _status = status; _previewText = preview; _debug = debug; _font = font; _legacyPresentation = legacy;
            _confirm = confirm; _cancel = cancel; _close = close; _concurrent = concurrent; _pause = pause; _rotate = rotate;
        }
        public void Bind(BattleInitializationResult mapping, LogicSnapshot snapshot)
        {
            if (_registry == null || _bootstrap == null || _panel == null) throw new InvalidOperationException("VIEW_PRESENTATION_BINDING_MISSING");
            _registry.Bind(mapping, snapshot);
            _feedbackPlayer?.Bind();
            if (_camera != null && _camera.orthographic && !_cameraConfigured)
            { _originalCameraSize = _camera.orthographicSize; _camera.orthographicSize = 12; _cameraConfigured = true; }
            if (_legacyCanvasStates == null)
            {
                _legacyCanvasStates = new bool[_legacyCanvases.Length];
                for (int i = 0; i < _legacyCanvases.Length; i++) _legacyCanvasStates[i] = _legacyCanvases[i] != null && _legacyCanvases[i].activeSelf;
                _legacyBehaviourStates = new bool[_legacyPresentation.Length];
                for (int i = 0; i < _legacyPresentation.Length; i++) _legacyBehaviourStates[i] = _legacyPresentation[i] != null && _legacyPresentation[i].enabled;
            }
            foreach (var behaviour in _legacyPresentation) if (behaviour != null) behaviour.enabled = false;
            foreach (var canvas in _legacyCanvases) if (canvas != null) canvas.SetActive(false);
            LegacyVisualRegistry.SetNewViewActive(true);
            _panel.SetActive(true); _debugLines.Clear(); _lastDamageDebug = string.Empty; _reactionKey = null;
            _pauseLabel = _pause.GetComponentInChildren<TMP_Text>();
            if (!_hooked)
            {
                _registry.Feedback.FeedbackRequested += OnFeedback;
                _confirm.onClick.AddListener(() => { if (_selectedReaction != 0) ConfirmSelectedReaction(); else Model.ConfirmDraft(); _selectedAction = default; RenderPreview(); });
                _cancel.onClick.AddListener(() => { Model.CancelDraft(); _selectedAction = default; _selectedReaction = 0; _selectedDodgePreview = null; RenderPreview(); });
                _close.onClick.AddListener(() => Model.CloseWindow());
                _concurrent.onClick.AddListener(() => Model.ActivateConcurrent());
                _pause.onClick.AddListener(() => _bootstrap.SetPaused(!_bootstrap.IsPaused));
                _rotate.onClick.AddListener(() => { _facing = (GridDirection)(((int)_facing + 1) % 12); if (Model.HasDraft && !string.IsNullOrEmpty(_selectedAction.Value)) DraftAction(_draftDestination); RenderPreview(); });
                if (_actionToggle != null) _actionToggle.onClick.AddListener(() => SetActionPanelVisible(!Model.ActionPanelVisible));
                _hooked = true;
            }
        }
        public void ReleaseVisuals()
        {
            _bound = false; Model.CancelDraft(); _selectedReaction = 0; _selectedAction = default; _selectedDodgePreview = null;
            _replayControls?.Dispose(); _replayControls = null; _replayLoaded = false;
            if (_developerReplayPanel != null) _developerReplayPanel.SetActive(false);
            foreach (var row in _rows.Values) if (row != null) Destroy(row.gameObject);
            _rows.Clear(); Clear(_reactionButtons); Clear(_actionButtons); Clear(_targetButtons); _debugLines.Clear();
            if (_feedbackPlayer != null) _feedbackPlayer.Unbind();
            if (_footprints != null) _footprints.ReleaseVisuals();
            if (_registry != null) _registry.ReleaseVisuals();
            if (_panel != null) _panel.SetActive(false);
            if (_legacyCanvasStates != null)
            {
                for (int i = 0; i < _legacyCanvases.Length; i++) if (_legacyCanvases[i] != null) _legacyCanvases[i].SetActive(_legacyCanvasStates[i]);
                for (int i = 0; i < _legacyPresentation.Length; i++) if (_legacyPresentation[i] != null) _legacyPresentation[i].enabled = _legacyBehaviourStates[i];
            }
            LegacyVisualRegistry.SetNewViewActive(false);
            if (_cameraConfigured && _camera != null) _camera.orthographicSize = _originalCameraSize;
            _cameraConfigured = false;
        }
        public void BindInput(ViewInputPorts ports)
        {
            Model.Bind(ports); _bound = true; _owner = Model.Decision.ControlledUnitIds[0];
            BindReplay();
            _footprints?.Bind(Model.Decision.Definition); _footprints?.Consume(Model.Decision.VisibleUnits);
            Clear(_actionButtons); Clear(_targetButtons);
            foreach (var action in Model.Decision.ActionSetOf(_owner))
            {
                var spec = Model.Decision.FindAction(action);
                if (spec.Type == ActionType.Block || spec.Type == ActionType.Dodge) continue;
                var captured = action;
                var words = action.Value.Split('.');
                var label = words.Length > 1 ? words[1].Replace('_', ' ') : spec.Type.ToString();
                label = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(label);
                _actionButtons.Add(CreateButton(_actions, label, () => SelectAction(captured)));
            }
            Render();
        }
        public void Consume(EventBatch events, LogicSnapshot snapshot)
        {
            if (this == null || _registry == null) return;
            _registry.Consume(events, snapshot);
            if (_bound) _footprints?.Consume(snapshot.Units);
            if (_bound)
            {
                Model.Synchronize(events);
                if (_selectedReaction != 0)
                {
                    _selectedDodgePreview = Model.PreviewDodge(_selectedReaction, _reactionAction, _reactionDestination);
                    if (_selectedDodgePreview.RejectionCode != null) { _selectedReaction = 0; _selectedDodgePreview = null; }
                }
                RenderTimeline(); RenderReactions(); RenderPreview();
            }
        }
        public void SelectAction(ActionSpecId action)
        {
            Model.CancelDraft(); _selectedAction = action; _selectedReaction = 0; _selectedDodgePreview = null; _target = default; Clear(_targetButtons);
            var spec = Model.Decision.FindAction(action);
            if (spec.Type == ActionType.Attack)
            {
                foreach (var candidate in Model.Targets(_owner, action))
                {
                    var captured = new UnitId(candidate.UnitId);
                    _targetButtons.Add(CreateButton(_targets, "Target " + candidate.UnitId, () => { _target = captured; DraftAction(null); }));
                }
            }
            else if (spec.Type == ActionType.Guard) DraftAction(null);
            RenderPreview();
        }
        public void DraftAction(GridPoint? destination, long? startTick = null)
        {
            if (string.IsNullOrEmpty(_selectedAction.Value)) return;
            _draftDestination = destination;
            var spec = Model.Decision.FindAction(_selectedAction);
            Model.SetDraft(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, _owner, _selectedAction,
                startTick, PrimaryTargetUnitId: spec.Type == ActionType.Attack && _target.IsValid ? _target : (UnitId?)null, Facing: _facing, Destination: destination) });
            RenderPreview();
        }
        private void LateUpdate()
        {
            if (!_bound) return;
            _refreshElapsed += Time.unscaledDeltaTime;
            if (_refreshElapsed >= 0.1f) { _refreshElapsed = 0; Render(); }
            if (_bootstrap.IsPaused || Model.Decision.BattleEnd.IsEnded || string.IsNullOrEmpty(_selectedAction.Value)) return;
            var spec = Model.Decision.FindAction(_selectedAction);
            if (spec.Type != ActionType.Move || Mouse.current == null || _camera == null) return;
            if (Mouse.current.leftButton.wasPressedThisFrame && !(EventSystem.current?.IsPointerOverGameObject() ?? false)
                && _grid.TryPick(_camera.ScreenPointToRay(Mouse.current.position.ReadValue()), out var cell)) DraftAction(cell);
        }
        private void Render()
        {
            if (!_bound) return;
            var text = new StringBuilder();
            text.Append("Tick ").Append(Model.Decision.Tick).Append("  |  Locked before ").Append(Model.LockLine).Append("\n");
            foreach (var unit in Model.Decision.VisibleUnits)
                text.Append("Unit ").Append(unit.UnitId).Append("  HP ").Append(unit.HealthQ10 / 1024f).Append("  ").Append(unit.StateValue).Append('\n');
            var window = Model.Window;
            if (window != null) text.Append("Turn ").Append(window.OwnerUnitId).Append("  Available ").Append(window.AvailableBudgetTicks)
                .Append("  Reserved ").Append(window.ReservedBudgetTicks).Append("  Spent ").Append(window.SpentBudgetTicks).Append('\n');
            foreach (var ledger in Model.Adrenaline)
                text.Append("Adrenaline ").Append(ledger.AvailableAdrenaline).Append("  Reserved ").Append(ledger.ReservedTotal).Append("  Cycle ").Append(ledger.CycleId).Append('\n');
            if (Model.Decision.BattleEnd.IsEnded) text.Append(Model.Decision.BattleEnd.ResultCode);
            _status.text = text.ToString();
            _confirm.interactable = ((Model.HasDraft && Model.Preview?.Succeeded == true)
                || (_selectedReaction != 0 && _selectedDodgePreview?.RejectionCode == null)) && !_bootstrap.IsPaused && !Model.Decision.BattleEnd.IsEnded;
            _close.interactable = window != null && window.OwnerUnitId == _owner.Value && !_bootstrap.IsPaused && !Model.Decision.BattleEnd.IsEnded;
            _concurrent.interactable = window != null && window.OwnerUnitId != _owner.Value && !_bootstrap.IsPaused && !Model.Decision.BattleEnd.IsEnded;
            if (_pauseLabel != null) _pauseLabel.text = _bootstrap.IsPaused ? "Resume" : "Pause";
            _debug.transform.parent.gameObject.SetActive((Debug.isDebugBuild || Application.isEditor) && _debugLines.Count > 0);
            _debug.overflowMode = TextOverflowModes.Truncate;
            _debug.text = string.Join("\n", _debugLines) + _lastDamageDebug;
            RenderReplayStatus();
            RenderPreview();
        }
        private void RenderPreview()
        {
            _previewText.overflowMode = TextOverflowModes.Truncate;
            if (Model.Decision.BattleEnd.IsEnded)
            { _previewText.text = "Battle finished: " + Model.Decision.BattleEnd.ResultCode; return; }
            var text = new StringBuilder("Facing ").Append(_facing).Append("\n");
            if (_selectedReaction != 0 && _selectedDodgePreview != null)
            {
                text.Append("If Dodge relocates: cancel ").Append(_selectedDodgePreview.AffectedPlanIds.Count).Append(" future moves.");
                foreach (var release in _selectedDodgePreview.ReleasesByWindow) text.Append("\nTurn ").Append(release.WindowId.Value).Append(": ").Append(release.BudgetTicks).Append(release.WindowIsOpen ? " ticks released" : " historical ticks released (closed turn)");
            }
            else if (!Model.HasDraft) text.Append("Select an action. Move: choose a ground cell.\nDrag a future plan to preview; confirm once.");
            else if (Model.Preview?.Succeeded != true) text.Append("Cannot confirm: ").Append(Model.Preview?.RejectionCode);
            else foreach (var projection in Model.Preview.PreviewPlans ?? Array.Empty<ProjectHero.Logic.Timeline.SchedulePlanPreview>())
            {
                var plan = projection.Plan;
                text.Append("Start ").Append(plan.StartTick).Append("  End ").Append(plan.EndTick).Append("  Budget ").Append(plan.BudgetCostTicks)
                    .Append("  Weight ").Append(plan.ResolvedPathWeightUnits).Append("\nPath: ").Append(string.Join(" -> ", projection.Path)).Append('\n');
            }
            if (Model.LastRejection != null) text.Append("\nRejected: ").Append(Model.LastRejection);
            _previewText.text = text.ToString();
        }
        private void RenderTimeline()
        {
            var live = new HashSet<long>();
            foreach (var plan in Model.Timeline)
            {
                live.Add(plan.ActionPlanId);
                if (!_rows.TryGetValue(plan.ActionPlanId, out var row))
                {
                    var go = CreateButton(_timeline, "", () => { });
                    row = go.AddComponent<TimelinePlanView>(); row.Initialize(this, plan.ActionPlanId);
                    _rows.Add(plan.ActionPlanId, row);
                }
                row.Refresh(plan, Model.CanEdit(plan.ActionPlanId), Model.IsConditionallyInvalidated(plan.ActionPlanId));
            }
            var remove = new List<long>();
            foreach (var pair in _rows) if (!live.Contains(pair.Key)) { Destroy(pair.Value.gameObject); remove.Add(pair.Key); }
            foreach (var id in remove) _rows.Remove(id);
        }
        private void RenderReactions()
        {
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
                    string label = spec.Type + "  cost " + spec.AdrenalineCost + "  impact " + opportunity.TriggerTick + "  deadline " + option.ResponseDeadlineTick;
                    if (spec.Type == ActionType.Block)
                        _reactionButtons.Add(CreateButton(_reactions, label, () => Model.ConfirmReaction(id, action, null)));
                    else foreach (var destination in Model.DodgeDestinations(id, action))
                    {
                        var captured = destination;
                        _reactionButtons.Add(CreateButton(_reactions, label + " -> " + destination, () =>
                        {
                            var preview = Model.PreviewDodge(id, action, captured);
                            // Selecting a destination is a local preview. A separate confirm click sends the reaction.
                            Model.CancelDraft(); _selectedDodgePreview = preview;
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
