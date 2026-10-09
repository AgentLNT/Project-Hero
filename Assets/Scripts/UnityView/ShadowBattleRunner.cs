using System;
using System.Collections.Generic;
using ProjectHero.Logic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// 独立新模拟世界的逻辑检查点（**不是** Unity 帧检查点）。
    /// 对齐键是 <see cref="AlignmentKey"/>（逻辑 Tick + 具名检查点），
    /// 与 Unity 帧序号无关——任务包明确禁止按帧序号强行配对。
    /// </summary>
    public sealed class ShadowLogicCheckpoint
    {
        public ShadowLogicCheckpoint(long logicalTick, string checkpoint, LogicSnapshot snapshot)
        {
            LogicalTick = logicalTick;
            Checkpoint = checkpoint ?? string.Empty;
            Snapshot = snapshot;
        }

        /// <summary>逻辑 Tick（唯一权威对齐键的一部分）。</summary>
        public long LogicalTick { get; }

        /// <summary>具名检查点（例如 <c>LateUpdate</c>）。</summary>
        public string Checkpoint { get; }

        public LogicSnapshot Snapshot { get; }

        public string AlignmentKey => LogicalTick.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "@" + Checkpoint;

        public override string ToString()
            => AlignmentKey + " hash=" + (Snapshot != null ? Snapshot.ComputeHashHex() : "<null>");
    }

    /// <summary>只读检查点接收端（Bootstrap 实现；不能让 Shadow 持有任何 Unity 写接口）。</summary>
    public interface IShadowLogicCheckpointSink
    {
        void OnShadowLogicCheckpoint(ShadowLogicCheckpoint checkpoint);
    }

    /// <summary>
    /// Shadow 的最小可镜像输入（任务 03B「必须产出」6）。
    ///
    /// 任务 09 完成前只允许空 Tick 与固定的可信测试请求；本接口就是预留的
    /// **显式请求镜像接口**——任务 04–09 每完成一个子系统都扩展对应输入与检查点，
    /// 而不是让 Shadow 直接读取旧对象。
    /// </summary>
    public interface IShadowMirroredInputSource
    {
        string SourceName { get; }

        /// <summary>本 Tick 要镜像到新模拟的请求（可以为空）。</summary>
        IReadOnlyList<ShadowMirroredRequest> RequestsFor(long logicalTick);
    }

    /// <summary>
    /// 一条可镜像请求：生产者可见的三元组（目标 Tick / 作用域 / 载荷）+ 目标入口的
    /// <c>ControllerId</c>。它<strong>不含</strong>来源优先级、ProducerOrdinal、CommandSequence、
    /// 规则费用或反应 TriggerTick——那些只能由 Logic 侧的入口注册表派生
    /// （不变量 18、任务 03 §3.2）。
    ///
    /// 任务 09「必须产出」16 追加 <see cref="SubmittedAtTick"/>：镜像的是
    /// 「入口接受的 <strong>Player</strong> 可信请求<strong>及其原始提交 Tick</strong>」，
    /// 该 Tick 等于冻结批次的 Tick，是回放侧唯一的注入对齐键。
    /// </summary>
    public sealed class ShadowMirroredRequest
    {
        public ShadowMirroredRequest(
            Logic.Ids.ControllerId producerControllerId,
            Logic.Commands.CommandRequest request,
            string origin,
            long submittedAtTick = -1L)
        {
            ProducerControllerId = producerControllerId;
            Request = request;
            Origin = origin ?? string.Empty;
            SubmittedAtTick = submittedAtTick;
        }

        /// <summary>镜像自哪个已注册入口（注册表按 <c>ControllerId</c> 索引）。</summary>
        public Logic.Ids.ControllerId ProducerControllerId { get; }

        public Logic.Commands.CommandRequest Request { get; }

        public string Origin { get; }

        /// <summary>
        /// 该请求的<strong>原始提交 Tick</strong>（= 记录它的冻结批次 Tick）；
        /// <c>-1</c> 表示镜像来源没有提供这条事实（非权威镜像路径）。
        /// </summary>
        public long SubmittedAtTick { get; }
    }

    /// <summary>空输入镜像：只有空 Tick（03B 的基线等价性用例）。</summary>
    public sealed class EmptyShadowMirroredInputSource : IShadowMirroredInputSource
    {
        public static readonly EmptyShadowMirroredInputSource Instance = new EmptyShadowMirroredInputSource();

        public string SourceName => "empty-ticks";

        public IReadOnlyList<ShadowMirroredRequest> RequestsFor(long logicalTick) => Array.Empty<ShadowMirroredRequest>();
    }

    /// <summary>
    /// Shadow runner（任务 03B「必须产出」6）。
    ///
    /// 硬性约束（不变量 22；任务包「禁止事项」第 3 条）：
    /// <list type="bullet">
    /// <item>使用<strong>独立</strong> Logic 世界（自己 <c>BattleSimulation.Create</c>），
    /// 不持有任何旧可变对象或 View 引用——本类中不存在任何旧类型字段（反射断言）。</item>
    /// <item>只消费可镜像输入；<strong>不</strong>绑定 View、不播放反馈、不修改 Unity 或旧状态。</item>
    /// <item>自身没有 <c>Update()</c>/协程；只由 Bootstrap 在 <c>Update()</c> 中显式调用
    /// <see cref="AdvanceFrame"/>（推进）与 <see cref="CaptureCheckpoint"/>（**只读**采样）。</item>
    /// <item>推进永不跳过逻辑 Tick：预算超限时标记本次比较无效，而不是丢 Tick 或篡改 Legacy 时间。</item>
    /// </list>
    /// </summary>
    public sealed class ShadowBattleRunner : IBattleFrameAdapter
    {
        public const string DefaultCaseId = "03B-shadow-equivalent-empty-ticks";
        public const string DefaultCheckpointName = "LateUpdate";

        /// <summary>单帧允许的最大追赶 Tick 数（超出即记录缺口并作废本次等价声明，但不丢 Tick）。</summary>
        public const int MaxCatchUpStepsPerFrame = 4096;

        /// <summary>
        /// 同一条入口事实被镜像<strong>两次</strong>（任务 09「必须产出」16 的"恰好一次"违规：
        /// 同一玩家请求被注入两次会让新世界在同 Tick 抢跑并与重建结果分叉）。
        /// </summary>
        public const string SHADOW_MIRROR_DUPLICATE_INJECTION = "SHADOW_MIRROR_DUPLICATE_INJECTION";

        /// <summary>
        /// 固定逻辑 Tick 时长（60 Tick/秒，与任务 03 的固定步长一致）。
        ///
        /// 只用于 <see cref="AdvanceByDelta"/> 这条**退化路径**（旧侧观测来源不可用时）；
        /// 正常路径按旧时间线的真实逻辑 Tick 推进，不使用本常量。
        /// </summary>
        public const float SecondsPerTick = 1f / 60f;

        private readonly List<ShadowLogicCheckpoint> _checkpoints = new List<ShadowLogicCheckpoint>();
        private readonly ShadowWriteCounters _writes = new ShadowWriteCounters();
        private ShadowWriteCounters _sharedWrites;
        private readonly List<ShadowCheckpointEventBinding> _eventBindings =
            new List<ShadowCheckpointEventBinding>();

        private BattleRuntimeContext _context;
        private BattleSimulationSeed _seed;
        private IShadowMirroredInputSource _mirror;
        private ReplayAuthorityInput _authorityInput;
        private readonly HashSet<string> _mirroredFactKeys = new HashSet<string>(StringComparer.Ordinal);
        private ShadowComparisonConfig _configuration = ShadowComparisonConfig.Default();
        private BattleSimulation _simulation;
        private float _accumulator;
        private bool _initialized;
        private bool _stopped;
        private int _advanceCallCount;
        private int _stopCallCount;

        public string AdapterName => "ShadowBattleRunner";

        public string CallbackSite => "ShadowBattleRunner.AdvanceFrame";

        /// <summary>恒为 false：Shadow runner 不是自主时钟入口。</summary>
        public bool OwnsAutonomousUpdate => false;

        /// <summary>恒为 null：Shadow 不持有任何场景对象。</summary>
        public MonoBehaviour GateTarget => null;

        public bool IsInitialized => _initialized;

        public bool IsStopped => _stopped;

        public int AdvanceCallCount => _advanceCallCount;

        public int StopCallCount => _stopCallCount;

        /// <summary>
        /// Shadow 新模拟对 Unity/旧状态/反馈的写入计数器（必须保持全 0）。
        ///
        /// 由 Bootstrap 在 <see cref="Initialize"/> 时注入<strong>同一份</strong>共享计数器
        /// （<c>BattleRuntimeContext.ShadowWrites</c>），因此"runner 观察到的越界尝试数"与
        /// "Bootstrap 的负控制探针计数"是同一个事实，不会出现两套互不相干的计数。
        /// </summary>
        public ShadowWriteCounters Writes => _sharedWrites ?? _writes;

        /// <summary>检查点数量。</summary>
        public int Count => _checkpoints.Count;

        public ShadowLogicCheckpoint this[int index] => _checkpoints[index];

        /// <summary>被丢弃的检查点数（超出 <see cref="ShadowComparisonConfig.MaxCheckpoints"/> 时丢弃最旧）。</summary>
        public int DroppedCheckpoints { get; private set; }

        /// <summary>本次比较是否因诊断预算超限而无效。</summary>
        public bool BudgetOverrun { get; private set; }

        public string BudgetOverrunReason { get; private set; }

        /// <summary>独立新模拟的当前逻辑 Tick（-1 = 尚未创建）。</summary>
        public long SimulationTick => _simulation != null ? _simulation.Tick : -1L;
        public LogicSnapshot CurrentSnapshot => _simulation?.CurrentSnapshot;
        public int LivePlayerRequestCount { get; private set; }
        public string SubmitLivePlayerRequest(ControllerId controller, CommandRequest request)
        {
            if (_simulation == null || _stopped || _simulation.IsEnded) return "SHADOW_INPUT_NOT_ACTIVE";
            var entry = _simulation.CommandIngress.FindEntry(controller);
            if (entry == null || entry.SourceKind != CommandSourceKind.Player) return "SHADOW_INPUT_PLAYER_REQUIRED";
            var rejection = entry.Submit(request);
            if (rejection != null) return rejection.ReasonCode;
            LivePlayerRequestCount++;
            return null;
        }

        /// <summary>
        /// 上一次推进未能追上的 Tick 数（0 = 已与旧时间线对齐）。
        ///
        /// 非 0 表示本次比较**没有覆盖全部检查点**：Bootstrap 会把该事实登记进报告，
        /// 从而阻断等价声明（既不静默丢 Tick，也不假装对齐）。
        /// </summary>
        public int LastStepDeficit { get; private set; }

        /// <summary>
        /// 上一次推进请求的诊断面：目标 Tick、请求时新世界所在 Tick、以及实际执行的 Tick 数。
        /// 用于证明"每个 Update 恰好推进一次、且推进量与旧时钟前进量一致"。
        /// </summary>
        public long LastAdvanceTargetTick { get; private set; } = -1L;

        public long LastAdvanceTickBefore { get; private set; } = -1L;

        public int LastAdvanceTickCount { get; private set; }

        /// <summary>
        /// 被守卫拒绝的越界写入尝试总数（任务 03B 第二收尾轮 R3 的负控制证据）。
        ///
        /// Shadow 侧正常运行时必须恒为 0：任何非 0 值都表示真的有一次"尝试写 Unity/旧状态/
        /// 反馈/视图绑定"被拦截，或被守卫记入 <see cref="Writes"/>。
        /// </summary>
        public int RejectedWriteAttempts => Writes.RejectedAttempts;
        public bool SimulationCreated => _simulation != null;

        public BattleSimulationSeed Seed => _seed;

        public string CaseId { get; set; } = DefaultCaseId;

        /// <summary>检查点数量超过该阈值即标记预算超限（不丢 Tick，只作废本次比较）。</summary>
        public int BudgetCheckpointLimit { get; set; } = 4096;

        /// <summary>Bootstrap 注入的只读检查点接收端。</summary>
        public IShadowLogicCheckpointSink Sink { get; set; }

        /// <summary>显式构造：注入镜像输入来源与比较配置。</summary>
        public void Configure(
            BattleSimulationSeed seed, IShadowMirroredInputSource mirror, ShadowComparisonConfig configuration = null)
        {
            _seed = seed ?? throw new ArgumentNullException(nameof(seed));
            _mirror = mirror ?? EmptyShadowMirroredInputSource.Instance;
            _configuration = configuration ?? ShadowComparisonConfig.Default();
            BudgetCheckpointLimit = _configuration.MaxCheckpoints;
        }

        /// <summary>
        /// 任务 09「必须产出」16：显式注入<b>回放权威输入记录器</b>。
        ///
        /// 注入之后每一步都在<strong>唯一模拟入口</strong>内按同一口径收口：
        /// <c>Step</c> 之前冻结批次之后记录权威事实、<c>Step</c> 之后按
        /// 「登记 Envelope → 折叠事件 → 对未被拒者 <c>RecordAccepted</c>」三步收口
        /// （见 <see cref="ShadowAuthorityProtocol"/>）。这样"镜像源"与"记录器"共享同一个事实来源，
        /// 而不是由镜像源自己猜哪些请求是可信的。
        /// </summary>
        public void ConfigureAuthorityInput(ReplayAuthorityInput authorityInput)
        {
            _authorityInput = authorityInput;
        }

        /// <summary>
        /// 本 runner 的权威输入记录器（未注入时为 <c>null</c>：独立世界中没有任何事实被记录）。
        ///
        /// 它与 <see cref="AttachMirror(ShadowPlayerRequestMirror)"/> 是交付物 16 的一对：
        /// 记录器产出事实，镜像源消费事实。
        /// </summary>
        public ReplayAuthorityInput AuthorityInput => _authorityInput;

        /// <summary>
        /// 把「真实请求镜像」接到本 runner 上（交付物 16 的生产装配点）。
        ///
        /// 只接受 <see cref="ShadowPlayerRequestMirror"/>——它是唯一只含 Player 权威事实的
        /// <see cref="IShadowMirroredInputSource"/>；AI/System 没有任何可传进来的形态
        /// （它们的请求由新模拟按相同初始输入与 RNG 从 Tick 0 重建）。
        /// </summary>
        public void AttachMirror(ShadowPlayerRequestMirror mirror)
        {
            _mirror = mirror ?? throw new ArgumentNullException(nameof(mirror));
        }

        /// <summary>
        /// 当前镜像输入来源是不是「真实请求镜像」（生产路径装配证据；空镜像是 03B 基线）。
        /// </summary>
        public bool MirrorIsAuthorityMirror => _mirror is ShadowPlayerRequestMirror;

        /// <summary>被镜像注入的入口事实条数（"恰好一次"的可观察读数；重复注入会直接失败）。</summary>
        public int MirroredRequestCount => _mirroredFactKeys.Count;

        public void ConfigureCheckpoints(ShadowComparisonConfig configuration)
        {
            _configuration = configuration ?? ShadowComparisonConfig.Default();
            BudgetCheckpointLimit = _configuration.MaxCheckpoints;
        }

        public ShadowComparisonConfig ComparisonConfig => _configuration;

        public void Initialize(BattleRuntimeContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            if (_mirror == null) _mirror = EmptyShadowMirroredInputSource.Instance;

            // 权威输入记录器随上下文注入：本场每一次 Step 都由本 runner 在唯一模拟入口里收口。
            _authorityInput = context.AuthorityInput;

            // 与 Bootstrap 共享同一份写入计数器：越界写入的拦截事实只有一个来源。
            _sharedWrites = context.ShadowWrites;
            var source = _seed != null ? null : context.SimulationSource;
            if (_seed == null && source != null)
            {
                string error = source.LastConfigurationError;
                if (!string.IsNullOrEmpty(error))
                    throw new LogicDefinitionException("SHADOW_SEED_SOURCE_INVALID", error);
                _seed = source.BuildSeed();
            }

            if (_seed == null)
                throw new LogicDefinitionException("SHADOW_SEED_MISSING", "no seed and no simulation source");

            string seedError = _seed.Validate();
            if (seedError != null)
                throw new LogicDefinitionException(seedError, _seed.InputSummary);

            // 独立 Logic 世界：与 Legacy 的唯一共享物是"同一定义 + 同一初始输入"。
            var assembly = (context.SimulationSource as IShadowScenarioSource)?.BuildShadowAssembly(_seed);
            _simulation = BattleSimulation.Create(_seed.Definition, _seed.EncounterId, _seed.RuntimeInputs,
                assembly ?? BattleSimulationAssembly.Standard());
            _eventBindings.Clear();
            _initialized = true;
            _stopped = false;
        }

        public void AdvanceFrame(BattleFrameDelta delta)
            => AdvanceFrame(delta, -1L);

        /// <summary>
        /// 推进独立新世界（任务 03B 第二收尾轮 R1：以<strong>旧时间线的真实逻辑 Tick</strong>为同步点）。
        ///
        /// <paramref name="legacyTick"/> 是旧时间线在同一帧推进后的真实逻辑 Tick
        /// （由 Bootstrap 经只读观测接口传入，<c>-1</c> 表示拿不到）。
        ///
        /// <strong>Tick 起点归一化</strong>：两个世界的 Tick 语义不同——
        /// 旧 <c>BattleTimeline.CurrentTick</c> 的 0 表示"开局、尚未推进任何 Tick"，
        /// 而 <c>BattleSimulation.Tick</c> 在首次 <c>Step</c> 之前是 <c>-1</c>
        /// （"尚未执行任何 Step"）。因此同步条件为 <c>_simulation.Tick == legacyTick</c>：
        /// 此时两侧报告的 Tick 逐位相等，按逻辑 Tick 的字段级比较才有内容。
        /// （上一轮"新侧自己维护独立时钟"⇒ 两个 Tick 永不相等 ⇒ 生产比较恒 <c>UNALIGNED</c>，即 D1。）
        ///
        /// 已披露的细节：新世界首次推进会把 Tick 从 <c>-1</c> 带到 <c>0</c>，
        /// 因此整场战斗的累计 Step 数最多比"旧时钟前进量"多 1（此后逐 Tick 一一对应）。
        ///
        /// <c>legacyTick &lt; 0</c> 时按传入 delta 累积固定 Tick 推进（观测来源不可用时的退化路径），
        /// <strong>不猜测</strong>同步点。
        ///
        /// 边界：不读旧组件字段、不写旧状态、不丢 Tick。单帧追赶上限由
        /// <see cref="MaxCatchUpStepsPerFrame"/> 给出，超出时记录
        /// <see cref="LastStepDeficit"/> 并让 Bootstrap 作废本次等价声明——
        /// 既不静默丢 Tick，也不假装两侧仍然对齐。
        /// </summary>
        public void AdvanceFrame(BattleFrameDelta delta, long legacyTick)
        {
            if (!_initialized || _stopped || _simulation == null) return;

            _advanceCallCount++;

            // 暂停语义与 Legacy 侧一致：暂停时**不推进**独立世界。
            // 两侧必须同步冻结，否则逻辑 Tick 会漂移、等价性比较失去意义。
            if (delta.IsPaused) return;

            if (legacyTick >= 0)
            {
                AdvanceToTick(legacyTick);
                return;
            }

            AdvanceByDelta(delta.DeltaTime);
        }

        /// <summary>把独立世界推进到指定逻辑 Tick（每 Tick 恰好一次 <c>BattleSimulation.Step</c>）。</summary>
        private void AdvanceToTick(long targetTick)
        {
            LastStepDeficit = 0;
            LastAdvanceTargetTick = targetTick;
            LastAdvanceTickBefore = _simulation.Tick;
            LastAdvanceTickCount = 0;

            int steps = 0;
            while (_simulation.Tick < targetTick)
            {
                if (steps >= MaxCatchUpStepsPerFrame)
                {
                    long deficit = targetTick - _simulation.Tick;
                    LastStepDeficit = deficit > int.MaxValue ? int.MaxValue : (int)deficit;
                    LastAdvanceTickCount = steps;
                    return;
                }

                long nextTick = _simulation.Tick + 1;
                StepOnce(nextTick);
                steps++;
            }

            LastAdvanceTickCount = steps;
        }

        /// <summary>按帧时间累积固定 Tick（观测来源不可用时的退化路径；语义同任务 03B 首次交付）。</summary>
        private void AdvanceByDelta(float deltaTime)
        {
            LastStepDeficit = 0;
            _accumulator += deltaTime;
            int steps = 0;
            while (_accumulator >= SecondsPerTick)
            {
                if (steps >= MaxCatchUpStepsPerFrame) return;

                _accumulator -= SecondsPerTick;
                long nextTick = _simulation.Tick + 1;
                StepOnce(nextTick);
                steps++;
            }
        }

        /// <summary>
        /// 单个逻辑 Tick 的推进：镜像可信请求 → 冻结批次 → <c>Step</c> → 记录事件绑定与账本。
        /// 绝不读取旧对象、Unity 对象或表现反馈。
        ///
        /// 任务 09「必须产出」16：<strong>镜像面只含 Player</strong>（由
        /// <see cref="ShadowPlayerRequestMirror"/> 从 <c>ReplayAuthorityInput.AuthorityCommands</c>
        /// 交出）；AI/System 的请求不经过本方法——它们由新世界按相同初始输入与 RNG 从 Tick 0 重建。
        /// 每条入口事实在整场战斗里<strong>只注入一次</strong>：同一规范键第二次出现即显式失败。
        /// </summary>
        private void StepOnce(long nextTick)
        {
            var mirrored = _mirror != null ? _mirror.RequestsFor(nextTick) : null;
            if (mirrored != null)
            {
                for (int i = 0; i < mirrored.Count; i++)
                {
                    var request = mirrored[i];
                    if (request == null || request.Request == null) continue;
                    var entry = _simulation.CommandIngress.FindEntry(request.ProducerControllerId);
                    if (entry == null)
                        throw new LogicDefinitionException(
                            "SHADOW_MIRROR_ENTRY_UNKNOWN",
                            request.ProducerControllerId.Value ?? "<null>");

                    var rejection = entry.Submit(request.Request);
                    if (rejection != null) continue;

                    // "恰好一次"：入口接受 ⇒ 该事实占用一个 ProducerOrdinal。
                    // 同一个 (ControllerId|ProducerOrdinal) 再次注入必然抢跑，直接失败而不是静默跳过。
                    long producerOrdinal = entry.NextProducerOrdinal - 1L;
                    string factKey = (request.ProducerControllerId.Value ?? string.Empty) + "|"
                        + producerOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!_mirroredFactKeys.Add(factKey))
                        throw new LogicDefinitionException(
                            SHADOW_MIRROR_DUPLICATE_INJECTION,
                            factKey + "@tick=" + nextTick.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            var batch = _simulation.CommandIngress.FreezeTick(nextTick);

            // 权威输入收口（第一步，Step 之前）：记录本 Tick 冻结出的 Player 权威事实。
            if (_authorityInput != null) ShadowAuthorityProtocol.RecordFrozenBatch(_authorityInput, batch);

            StepResult result = _simulation.Step(nextTick, batch);

            // 权威输入收口（第二、三步，Step 之后）。
            if (_authorityInput != null)
                ShadowAuthorityProtocol.RecordOutcomes(_authorityInput, _simulation, nextTick, result);

            CaptureEventBinding(nextTick, result);

            if (_context != null && _context.Ledger != null)
                _context.Ledger.RecordNewSimulationStep(result.Status);
        }

        /// <summary>
        /// 记录"该逻辑 Tick 上生成的事件"（首个事件序号 + 条数）。
        ///
        /// 这是**真实事件序号差异**的接入点：差异报告据此把一条字段差异绑定到具体事件，
        /// 而不是只给一个 Tick。任务 04–09 扩展非空 Tick 时，事件序号会自动随
        /// <see cref="StepResult.Events"/> 一起进入比较输入，无需改动比较器。
        /// </summary>
        private void CaptureEventBinding(long logicalTick, StepResult result)
        {
            var events = result != null && result.Events != null
                ? result.Events.EventsInSequenceOrder
                : (IReadOnlyList<LogicEvent>)Array.Empty<LogicEvent>();

            int count = 0;
            long first = -1L;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] == null) continue;
                if (count == 0) first = events[i].Sequence;
                count++;
            }

            for (int i = 0; i < _eventBindings.Count; i++)
            {
                if (_eventBindings[i].LogicalTick != logicalTick) continue;
                _eventBindings[i] = new ShadowCheckpointEventBinding(logicalTick, first, count);
                return;
            }

            _eventBindings.Add(new ShadowCheckpointEventBinding(logicalTick, first, count));
        }

        /// <summary>检查点事件绑定（逻辑 Tick → 该 Tick 的事件序号），随比较输入一起交给比较器。</summary>
        public IReadOnlyList<ShadowCheckpointEventBinding> EventBindings => _eventBindings;

        /// <summary>
        /// 只读检查点采样：把独立世界的规范快照登记为一个逻辑检查点。
        /// 不推进时钟、不写两边状态、不绑定 View。
        /// </summary>
        public ShadowLogicCheckpoint CaptureCheckpoint(string checkpointName, long relatedEventSequence = -1)
        {
            if (!_initialized || _simulation == null) return null;

            var snapshot = _simulation.CurrentSnapshot;
            var checkpoint = new ShadowLogicCheckpoint(
                snapshot.Tick, checkpointName ?? DefaultCheckpointName, snapshot);

            _checkpoints.Add(checkpoint);
            if (_checkpoints.Count > BudgetCheckpointLimit)
            {
                _checkpoints.RemoveAt(0);
                DroppedCheckpoints++;
                BudgetOverrun = true;
                BudgetOverrunReason = ShadowComparisonCodes.ShadowBudgetOverrun
                    + "|dropped=" + DroppedCheckpoints
                    + "|limit=" + BudgetCheckpointLimit;
            }

            _ = relatedEventSequence;
            if (Sink != null) Sink.OnShadowLogicCheckpoint(checkpoint);
            return checkpoint;
        }

        /// <summary>按逻辑 Tick 找检查点（绝不按帧序号定位）。</summary>
        public ShadowLogicCheckpoint FindCheckpoint(long logicalTick, string checkpointName)
        {
            string name = checkpointName ?? DefaultCheckpointName;
            for (int i = 0; i < _checkpoints.Count; i++)
            {
                if (_checkpoints[i].LogicalTick == logicalTick
                    && string.Equals(_checkpoints[i].Checkpoint, name, StringComparison.Ordinal))
                    return _checkpoints[i];
            }
            return null;
        }

        public IReadOnlyList<ShadowLogicCheckpoint> Checkpoints => _checkpoints;

        /// <summary>
        /// 只读快照序列（供比较器使用；顺序 = 逻辑 Tick 升序，<strong>同一 Tick 只保留一份</strong>）。
        ///
        /// 去重是必要的：旧时间线在某一帧没有推进时（暂停、<c>timeScale=0</c>、累加器未满一个
        /// Tick），连续检查点会采到同一个逻辑 Tick。按 Tick 去重保证比较器看到的是
        /// "每个对齐键一份快照"，而不是靠帧序号堆出重复条目。
        ///
        /// 实现只比较最后一个元素：检查点按采集顺序追加，而新世界的 Tick 单调不减，
        /// 因此重复项必然相邻（O(1) 判定，不在检查点上限 4096 时退化为 O(n²)）。
        /// </summary>
        public IReadOnlyList<LogicSnapshot> SnapshotSequence()
        {
            var snapshots = new List<LogicSnapshot>(_checkpoints.Count);
            for (int i = 0; i < _checkpoints.Count; i++)
            {
                var snapshot = _checkpoints[i].Snapshot;
                if (snapshot == null) continue;
                if (snapshots.Count > 0 && snapshots[snapshots.Count - 1].Tick == snapshot.Tick) continue;
                snapshots.Add(snapshot);
            }
            return snapshots;
        }

        /// <summary>释放独立世界并停止（幂等）。</summary>
        public void StopBattle(string reason)
        {
            _stopCallCount++;
            if (_stopped) return;

            _stopped = true;
            if (_simulation != null)
            {
                _simulation.Dispose();
                _simulation = null;
            }

            _ = reason;
        }

        /// <summary>战斗之间重置（同一 Bootstrap 用另一模式重新开局时调用）。</summary>
        public void ResetForNewBattle()
        {
            _checkpoints.Clear();
            _eventBindings.Clear();
            DroppedCheckpoints = 0;
            BudgetOverrun = false;
            BudgetOverrunReason = null;
            _advanceCallCount = 0;
            _stopCallCount = 0;
            _initialized = false;
            _stopped = false;
            _seed = null;
            _mirror = null;
            _authorityInput = null;
            _mirroredFactKeys.Clear();
            LivePlayerRequestCount = 0;
            _simulation = null;
            _accumulator = 0f;
            LastStepDeficit = 0;
            Sink = null;
            _sharedWrites = null;
            _writes.Reset();
        }

        /// <summary>从旧世界导出的**可镜像**输入载体基类（不携带任何 Unity 引用）。</summary>
        public abstract class MirroredRequestSourceBase : IShadowMirroredInputSource
        {
            public abstract string SourceName { get; }

            public abstract IReadOnlyList<ShadowMirroredRequest> RequestsFor(long logicalTick);
        }
    }

    /// <summary>
    /// 逻辑侧检查点构建器：让 Shadow 的等价性检查可以完全在 Logic 世界内被验证
    /// （用于 <c>EquivalentShadowScenarioUsesLogicalCheckpointAlignment</c>）。
    ///
    /// 它<strong>不是</strong>第二个 Step 入口：它只是反复调用任务 03 唯一的
    /// <c>BattleSimulation.Step()</c>，并把每次结果快照登记为逻辑检查点。
    /// </summary>
    public static class ShadowLogicCheckpointBuilder
    {
        /// <summary>
        /// 从一个新建的模拟推进 <paramref name="ticks"/> 个空 Tick，返回每个已提交 Tick 的快照。
        /// </summary>
        public static IReadOnlyList<LogicSnapshot> BuildEmptyTickCheckpoints(
            BattleDefinition definition,
            EncounterDefinitionId encounterId,
            BattleRuntimeInputs runtimeInputs,
            int ticks,
            string checkpointName = ShadowBattleRunner.DefaultCheckpointName)
        {
            var snapshots = new List<LogicSnapshot>(Math.Max(0, ticks) + 1);
            if (definition == null) return snapshots;

            var simulation = BattleSimulation.Create(definition, encounterId, runtimeInputs);
            try
            {
                // 首个检查点是 Tick 0 的初始状态；随后每个已提交 Tick 各采一个。
                snapshots.Add(simulation.CurrentSnapshot);
                for (int i = 0; i < ticks; i++)
                {
                    long tick = simulation.Tick + 1;
                    var batch = simulation.CommandIngress.FreezeTick(tick);
                    simulation.Step(tick, batch);
                    snapshots.Add(simulation.CurrentSnapshot);
                }
            }
            finally
            {
                simulation.Dispose();
            }

            _ = checkpointName;
            return snapshots;
        }
    }
}
