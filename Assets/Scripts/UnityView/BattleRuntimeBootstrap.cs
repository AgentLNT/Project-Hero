using System;
using System.Collections.Generic;
using ProjectHero.Logic;
using ProjectHero.UnityView;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>Owns the single production clock and the lifetime of one New battle.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(-1000)]
    public sealed class BattleRuntimeBootstrap : MonoBehaviour, IBattleRuntimeHost
    {
        public const string BOOTSTRAP_DUPLICATE = "BATTLE_RUNTIME_BOOTSTRAP_DUPLICATE";
        public const string BOOTSTRAP_MODE_CHANGE_REJECTED = "BATTLE_RUNTIME_MODE_CHANGE_REJECTED";
        public const string BOOTSTRAP_DOUBLE_START = "BATTLE_RUNTIME_BOOTSTRAP_DOUBLE_START";
        public const string BOOTSTRAP_ADAPTER_SLOT_INVALID = "BATTLE_RUNTIME_BOOTSTRAP_ADAPTER_SLOT_INVALID";
        [SerializeField] private MonoBehaviour _simulationSourceSlot;
        [SerializeField] private MonoBehaviour _viewConsumerSlot;
        [SerializeField] private bool _autoStart = true;
        // Retained solely to reject stale serialized configuration explicitly.
        [SerializeField, HideInInspector] private BattleRuntimeMode _requestedMode = BattleRuntimeMode.New;
        private readonly UnityBattleDriver _newDriver = new UnityBattleDriver();
        private bool _systemPaused;
        public UnityBattleDriver NewDriver => _newDriver;
        public BattleClockController Clock => _newDriver.Clock;
        public bool BattleCreated { get; private set; }
        public BattleRuntimeMode? BattleMode => BattleCreated ? BattleRuntimeMode.New : (BattleRuntimeMode?)null;
        public bool IsStopped { get; private set; }
        public bool IsPaused => _systemPaused || Clock.IsPaused;
        public bool OwnsTopLevelClock { get; private set; }
        public string StartupRejection { get; private set; }
        public string LastStopReason { get; private set; }
        public static int LiveInstanceCount => EnumerateLiveInstances().Count;
        public static IReadOnlyList<BattleRuntimeBootstrap> EnumerateLiveInstances()
        {
            var result = new List<BattleRuntimeBootstrap>();
            foreach (var host in FindObjectsByType<BattleRuntimeBootstrap>(FindObjectsInactive.Include))
                if (host.gameObject.scene.IsValid() && host.gameObject.scene.isLoaded) result.Add(host);
            return result;
        }
        private void Awake()
        {
            foreach (var host in EnumerateLiveInstances())
                if (host != this && host.OwnsTopLevelClock)
                {
                    StartupRejection = BOOTSTRAP_DUPLICATE + "|" + DescribePath(host);
                    enabled = false;
                    Debug.LogError(StartupRejection);
                    return;
                }
            OwnsTopLevelClock = true;
        }
        private void Start() { if (_autoStart && enabled && OwnsTopLevelClock) StartBattle(_requestedMode); }
        private void Update() => AdvanceFrame(Time.unscaledDeltaTime);
        private void AdvanceFrame(float unscaledSeconds)
        {
            if (!OwnsTopLevelClock || !BattleCreated) return;
            (_viewConsumerSlot as IBattleDiagnosticFrameConsumer)?.AdvanceDiagnostics(unscaledSeconds);
            if (IsStopped) return;
            _newDriver.AdvanceFrame(new BattleFrameDelta(unscaledSeconds, IsPaused, true));
        }
        public bool TrySetRequestedMode(BattleRuntimeMode mode, out string rejection)
        {
            rejection = BattleCreated ? BOOTSTRAP_MODE_CHANGE_REJECTED
                : mode != BattleRuntimeMode.New ? "PRODUCTION_LEGACY_MODE_REMOVED" : null;
            if (rejection != null) return false;
            _requestedMode = mode;
            return true;
        }
        public bool StartBattle(BattleRuntimeMode mode)
        {
            if (BattleCreated) throw new LogicDefinitionException(BOOTSTRAP_DOUBLE_START, name);
            if (!TrySetRequestedMode(mode, out string rejection)) { StartupRejection = rejection; return false; }
            if (!OwnsTopLevelClock || !enabled) { StartupRejection = BOOTSTRAP_DUPLICATE; return false; }
            // Includes inactive hosts so a dormant second clock cannot bypass scene validation.
            foreach (var component in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                if (component != this && component is IBattleRuntimeHost && component.gameObject.scene.IsValid())
                { StartupRejection = BOOTSTRAP_DUPLICATE + "|" + DescribePath(component); return false; }
            try
            {
                if (!(_simulationSourceSlot is IBattleSimulationSource source))
                    throw new LogicDefinitionException("NEW_DRIVER_SOURCE_MISSING", DescribePath(_simulationSourceSlot));
                if (_viewConsumerSlot != null && !(_viewConsumerSlot is IBattleViewConsumer))
                    throw new LogicDefinitionException("VIEW_CONSUMER_SLOT_INVALID", DescribePath(_viewConsumerSlot));
                _systemPaused = false;
                _newDriver.ResetForNewBattle();
                _newDriver.Initialize(new BattleRuntimeContext(source));
                _newDriver.BindView(_viewConsumerSlot as IBattleViewConsumer);
                _newDriver.CreateSimulation(source);
                if (_viewConsumerSlot is IBattleInputConsumer consumer)
                    consumer.BindInput(_newDriver.CreatePlayerInputPorts(new Logic.Ids.ControllerId(consumer.PlayerControllerId)));
                BattleCreated = true;
                IsStopped = false;
                StartupRejection = null;
                LastStopReason = null;
                return true;
            }
            catch (Exception exception)
            {
                _newDriver.ReleaseSimulation();
                BattleCreated = false;
                StartupRejection = BOOTSTRAP_ADAPTER_SLOT_INVALID + "|new|" + exception.Message;
                return false;
            }
        }
        public bool StopBattle(string reason)
        {
            if (!BattleCreated || IsStopped) return false;
            LastStopReason = reason ?? string.Empty;
            _newDriver.StopBattle(LastStopReason);
            IsStopped = true;
            return true;
        }
        public bool ReleaseBattle()
        {
            if (!BattleCreated) return false;
            if (!IsStopped) StopBattle("RELEASE_WITHOUT_STOP");
            _newDriver.ReleaseSimulation();
            BattleCreated = false;
            return true;
        }
        public void SetPaused(bool paused) { _systemPaused = paused; _newDriver.SetPaused(paused); }
        public void TogglePaused() => SetPaused(!IsPaused);
        public void DriveFrameForTests(float deltaTime, float unscaledDeltaTime) => AdvanceFrame(unscaledDeltaTime);
        private void OnDestroy() { StopBattle("BOOTSTRAP_DESTROYED"); ReleaseBattle(); OwnsTopLevelClock = false; }
        public static string DescribePath(Component component)
        {
            if (component == null) return "<null>";
            string path = component.name;
            for (var parent = component.transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return component.gameObject.scene.name + "/" + path;
        }
    }
}
