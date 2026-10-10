using System;
using System.Collections.Generic;
using ProjectHero.Logic;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Snapshots;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// 最小 New 被调用适配器（任务 03B「必须产出」5）。
    ///
    /// <list type="bullet">
    /// <item>由 Bootstrap 显式调用，<strong>自身没有 <c>Update()</c></strong>、
    /// 没有协程、没有定时器（反射断言）。</item>
    /// <item>用 Bootstrap 传入的 <c>Time.unscaledDeltaTime</c> 累积固定 Tick
    /// （<see cref="TicksPerSecond"/> = 60，与旧 <c>BattleTimeline.TicksPerSecond</c> 同量纲），
    /// 再调用任务 03 唯一的 <c>BattleSimulation.Step(long, FrozenCommandBatch)</c>。</item>
    /// <item>用任务 02B 的真实定义、Encounter 与 <c>BattleRuntimeInputs</c> 新建模拟；
    /// 本任务不绑定正式 View / UI / 完整反馈。</item>
    /// <item>时间来源与 <c>Time.timeScale</c> <strong>无关</strong>（视觉时间缩放隔离用例）。</item>
    /// </list>
    /// </summary>
    public sealed class UnityBattleDriver : IBattleFrameAdapter
    {
        /// <summary>与旧时间线同量纲的固定 Tick 率（主方案：60 Ticks/s）。</summary>
        public const int TicksPerSecond = 60;

        /// <summary>单帧最多推进的固定 Tick 数（防止一帧卡顿导致无限推进）。</summary>
        public const int MaxTicksPerFrame = 8;

        private readonly List<string> _recentEventNames = new List<string>();

        private BattleRuntimeContext _context;
        private BattleSimulationSeed _seed;
        private BattleSimulation _simulation;
        private double _accumulator;
        private bool _initialized;
        private bool _stopped;
        private int _advanceCallCount;
        private int _stopCallCount;
        private int _ticksAdvanced;
        private StepStatus? _lastStatus;
        private IBattleViewConsumer _view;
        private ReplayRecorder _recorder;
        private bool _paused;
        private BattleReplay _releasedReplay;
        private string _replayRecordingError;

        public LogicSnapshot CurrentSnapshot => _simulation?.CurrentSnapshot;
        public ReplayHeader ReplayHeader => _recorder?.Header ?? _releasedReplay?.Header;
        public string ReplayRecordingError => _replayRecordingError;
        public BattleReplay RecordedReplay => _replayRecordingError == null
            ? _recorder?.BuildReplay() ?? _releasedReplay : null;

        public void ReleaseSimulation()
        {
            (_view as IBattleViewLifetimeConsumer)?.ReleaseVisuals();
            _releasedReplay = _replayRecordingError == null ? _recorder?.BuildReplay() : null;
            _recorder = null;
            _simulation?.Dispose();
            _simulation = null;
        }

        public Input.ViewInputPorts CreatePlayerInputPorts(ProjectHero.Logic.Ids.ControllerId controller)
        {
            if (_simulation == null) throw new LogicDefinitionException("VIEW_SIMULATION_NOT_CREATED", AdapterName);
            var port = new Input.RuntimeViewLogicPort(_simulation, controller, () => _paused || _stopped);
            return new Input.ViewInputPorts(port, new Input.ViewCommandFactory(port));
        }

        // Binding is explicit and can only happen before initialization. Shadow has no such capability.
        public void BindView(IBattleViewConsumer view)
        {
            if (_simulation != null) throw new LogicDefinitionException("VIEW_BINDING_AFTER_BATTLE_START", AdapterName);
            _view = view;
        }

        public void SetPaused(bool paused) => _paused = paused;

        public string AdapterName => "UnityBattleDriver";

        public string CallbackSite => "UnityBattleDriver.AdvanceFrame";

        /// <summary>恒为 false：本适配器不拥有任何自主 Update。</summary>
        public bool OwnsAutonomousUpdate => false;

        /// <summary>恒为 null：New Driver 不门控任何场景组件。</summary>
        public MonoBehaviour GateTarget => null;

        public bool IsInitialized => _initialized;

        public bool IsStopped => _stopped;

        public int AdvanceCallCount => _advanceCallCount;

        public int StopCallCount => _stopCallCount;

        /// <summary>累积出的固定 Tick 数（= <c>Step</c> 成功调用次数）。</summary>
        public int TicksAdvanced => _ticksAdvanced;

        /// <summary>最近一次 <c>Step</c> 的状态码。</summary>
        public StepStatus? LastStatus => _lastStatus;

        /// <summary>最近一次 Tick 的事件名称（诊断用，不含表现策略）。</summary>
        public IReadOnlyList<string> RecentEventNames => _recentEventNames;

        public bool SimulationCreated => _simulation != null;

        public string InputSummary => _seed != null ? _seed.InputSummary : string.Empty;

        public string BattleDefinitionHash => _seed != null ? _seed.BattleDefinitionHash : string.Empty;

        public void Initialize(BattleRuntimeContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _initialized = true;
            _stopped = false;
            _accumulator = 0f;
        }

        /// <summary>
        /// 用纯数据来源创建新模拟。由 Bootstrap 在 New 模式启动时调用一次；
        /// 重复调用是显式错误（每场战斗只能初始化一次）。
        /// </summary>
        public void CreateSimulation(IBattleSimulationSource source)
        {
            if (_simulation != null)
                throw new LogicDefinitionException("NEW_DRIVER_ALREADY_CREATED", AdapterName);
            if (source == null)
                throw new LogicDefinitionException("NEW_DRIVER_SOURCE_MISSING", AdapterName);

            string error = source.LastConfigurationError;
            if (!string.IsNullOrEmpty(error))
                throw new LogicDefinitionException("NEW_DRIVER_SOURCE_INVALID", error);

            _seed = source.BuildSeed();
            string seedError = _seed != null ? _seed.Validate() : "NEW_DRIVER_SEED_MISSING";
            if (seedError != null)
                throw new LogicDefinitionException(seedError, source.SourceName);

            var assemblySource = source as INewBattleAssemblySource;
            _simulation = assemblySource == null
                ? (_seed.Definition.FindEncounter(_seed.EncounterId).TurnSubmission != null
                    ? ProductionBattleComposition.Create(_seed.Definition, _seed.EncounterId, _seed.RuntimeInputs)
                    : BattleSimulation.Create(_seed.Definition, _seed.EncounterId, _seed.RuntimeInputs))
                : BattleSimulation.Create(_seed.Definition, _seed.EncounterId, _seed.RuntimeInputs, assemblySource.BuildNewAssembly(_seed));
            assemblySource?.AttachNewSimulation(_simulation);
            _recorder = new ReplayRecorder(_simulation);
            try
            {
                _view?.Bind(BattleInitializer.BuildInitialState(_seed.Definition, _seed.EncounterId, _seed.RuntimeInputs),
                    _simulation.CurrentSnapshot);
            }
            catch
            {
                (_view as IBattleViewLifetimeConsumer)?.ReleaseVisuals();
                _simulation.Dispose(); _simulation = null; _recorder = null;
                throw;
            }
        }

        public void AdvanceFrame(BattleFrameDelta delta)
        {
            if (!_initialized || _stopped || _simulation == null) return;

            _advanceCallCount++;
            if (_context != null && _context.Ledger != null)
                _context.Ledger.RecordNewDriverInvocation(delta.DeltaTime);

            // New 段的时间来源固定为 Bootstrap 采样的 unscaledDeltaTime；
            // 这里再显式校验一次，防止适配器被喂入受 timeScale 缩放的时间。
            if (!delta.TimeScaleIsolated)
                throw new LogicDefinitionException("NEW_DRIVER_REQUIRES_UNSCALED_DELTA_TIME", delta.ToString());

            _paused = delta.IsPaused;
            if (_paused) return;
            if (float.IsNaN(delta.DeltaTime) || float.IsInfinity(delta.DeltaTime) || delta.DeltaTime < 0)
                throw new LogicDefinitionException("NEW_DRIVER_FRAME_TIME_INVALID", delta.ToString());

            _accumulator += delta.DeltaTime;
            const double secondsPerTick = 1d / TicksPerSecond;

            int advancedThisFrame = 0;
            while (_accumulator + 1e-9 >= secondsPerTick && advancedThisFrame < MaxTicksPerFrame)
            {
                _accumulator = Math.Max(0, _accumulator - secondsPerTick);
                advancedThisFrame++;

                long tick = _simulation.Tick + 1;
                _recorder.CaptureBeforeStep();
                var batch = _simulation.CommandIngress.FreezeTick(tick);
                if (_context.AuthorityInput != null)
                    ShadowAuthorityProtocol.RecordFrozenBatch(_context.AuthorityInput, batch);
                StepResult result = _simulation.Step(tick, batch);
                _lastStatus = result.Status;
                if (result.Status == StepStatus.AlreadyEnded)
                {
                    _stopped = true;
                    _accumulator = 0;
                    break;
                }
                _ticksAdvanced++;
                _recorder.RecordCommittedStep(result);
                if (_context.AuthorityInput != null)
                    ShadowAuthorityProtocol.RecordOutcomes(_context.AuthorityInput, _simulation, tick, result);
                CaptureEventNames(result);
                _view?.Consume(result.Events, result.Snapshot);

                // 调用计数只记录"真的推进过一次"的 Step；暂停时不进入本循环。
                if (!delta.IsPaused && _context != null && _context.Ledger != null)
                    _context.Ledger.RecordNewSimulationStep(result.Status);
                if (result.Status == StepStatus.BattleEnded)
                {
                    _stopped = true;
                    _accumulator = 0;
                    break;
                }
            }

            // Retain all accumulated time. Limiting work per frame must not discard elapsed ticks.
        }

        private void CaptureEventNames(StepResult result)
        {
            _recentEventNames.Clear();
            if (result == null || result.Events == null) return;

            var events = result.Events.EventsInSequenceOrder;
            for (int i = 0; i < events.Count; i++)
            {
                LogicEvent logicEvent = events[i];
                if (logicEvent == null) continue;
                if (_recentEventNames.Count >= 16) break;
                _recentEventNames.Add(logicEvent.GetType().Name + "#" + logicEvent.Sequence);
            }
        }

        public void StopBattle(string reason)
        {
            if (_stopCallCount > 0) return;
            _stopCallCount++;
            _stopped = true;
            if (_simulation != null && !_simulation.IsEnded)
            {
                // Teardown is an external lifecycle request, not a recorded Player command or regenerated AI fact.
                // Keep consuming the final result, but never export a file whose last Tick cannot be reproduced.
                _replayRecordingError = "REPLAY_EXTERNAL_LIFECYCLE_STOP_UNRECORDED";
                _simulation.RequestStop(reason);
                // 请求停止只登记意图；终止由下一个 Step 的阶段 2/16 原子提交。
                long tick = _simulation.Tick + 1;
                _recorder.CaptureBeforeStep();
                var batch = _simulation.CommandIngress.FreezeTick(tick);
                if (_context.AuthorityInput != null)
                    ShadowAuthorityProtocol.RecordFrozenBatch(_context.AuthorityInput, batch);
                var result = _simulation.Step(tick, batch);
                _lastStatus = result.Status;
                _recorder.RecordCommittedStep(result);
                if (_context.AuthorityInput != null)
                    ShadowAuthorityProtocol.RecordOutcomes(_context.AuthorityInput, _simulation, tick, result);
                CaptureEventNames(result);
                _view?.Consume(result.Events, result.Snapshot);
            }
        }

        /// <summary>战斗之间重置。</summary>
        public void ResetForNewBattle()
        {
            (_view as IBattleViewLifetimeConsumer)?.ReleaseVisuals();
            _advanceCallCount = 0;
            _stopCallCount = 0;
            _ticksAdvanced = 0;
            _lastStatus = null;
            _accumulator = 0f;
            _initialized = false;
            _stopped = false;
            _seed = null;
            _recorder = null;
            _releasedReplay = null;
            _replayRecordingError = null;
            _paused = false;
            if (_simulation != null)
            {
                _simulation.Dispose();
                _simulation = null;
            }
            _recentEventNames.Clear();
        }
    }
}
