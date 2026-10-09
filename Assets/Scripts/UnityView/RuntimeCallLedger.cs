using System.Collections.Generic;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// 顶层推进入口标识（任务 03B「必须产出」9「可观察调用计数」）。
    /// 每一种入口都有独立的计数器；测试直接读取计数，<strong>不从日志文本推断</strong>。
    /// </summary>
    public enum RuntimeAdvancePath
    {
        /// <summary>旧顶层时钟：<c>BattleTimeline.AdvanceTime(float)</c>（经 Legacy 适配器）。</summary>
        LegacyAdvanceTime = 0,

        /// <summary>新模拟：任务 03 唯一的 <c>BattleSimulation.Step(long, FrozenCommandBatch)</c>。</summary>
        NewSimulationStep = 1
    }

    /// <summary>
    /// 可观察调用账本（任务 03B「必须产出」9）。
    ///
    /// 唯一目的：让 PlayMode 测试<strong>直接证明</strong>每种模式调用了哪条顶层推进路径、
    /// 哪条路径为 0、Legacy 从属写入组是否正确启停，以及 Shadow 新模拟的
    /// Unity/旧状态/反馈写入数为 0。
    ///
    /// 契约：
    /// <list type="bullet">
    /// <item>本账本只<strong>记录事实</strong>，不参与任何调度或门控决策——
    /// 读取它不会改变战斗演化（<c>RuntimeCallLedgerDoesNotAffectBattleEvolution</c>）。</item>
    /// <item>每场战斗一个实例，由 <see cref="BattleRuntimeBootstrap"/> 拥有并跨战斗重置。</item>
    /// <item><c>LegacyAdvanceTimeObservedDelta</c> 是"紧邻上一次 Legacy 适配器推进所观察到的
    /// 权威计数增量"；<see cref="LegacyAdvanceTimeTotalCalls"/> 是旧组件自己累计的总调用数
    /// （唯一权威来源，见 <c>BattleTimeline.TotalAdvanceTimeCalls</c>）。两者独立记录，
    /// 因此"New 模式旧推进为 0"可以同时从两侧证明。</item>
    /// </list>
    /// </summary>
    public sealed class RuntimeCallLedger
    {
        private readonly Dictionary<RuntimeAdvancePath, int> _advanceCalls =
            new Dictionary<RuntimeAdvancePath, int>();

        private readonly Dictionary<string, int> _legacyWriterAdvanceCalls =
            new Dictionary<string, int>();

        private readonly Dictionary<string, bool> _legacyWriterStates =
            new Dictionary<string, bool>();

        /// <summary>Bootstrap 的 Unity <c>Update()</c> 被调用的次数。</summary>
        public int BootstrapUpdates { get; private set; }

        /// <summary>Bootstrap 的只读 <c>LateUpdate()</c> 检查点被调用的次数。</summary>
        public int BootstrapCheckpoints { get; private set; }

        /// <summary>Legacy 适配器的 <c>AdvanceFrame</c> 被调用的次数。</summary>
        public int LegacyAdapterInvocations { get; private set; }

        /// <summary>New 最小 Driver 的 <c>AdvanceFrame</c> 被调用的次数。</summary>
        public int NewDriverInvocations { get; private set; }

        /// <summary>Legacy 适配器最后一次收到的帧时间（null = 从未被调用）。</summary>
        public float? LastLegacyDeltaTime { get; private set; }

        /// <summary>New 最小 Driver 最后一次收到的帧时间（null = 从未被调用）。</summary>
        public float? LastNewDeltaTime { get; private set; }

        /// <summary>Legacy 适配器推进时旧时间线是否处于暂停（用户或系统暂停）。</summary>
        public bool LastLegacyWasPaused { get; private set; }

        /// <summary>New Driver 累积出的固定 Tick 总数。</summary>
        public int NewDriverTicks { get; private set; }

        /// <summary>新模拟 Step 返回过的状态码流水（诊断用；末尾为最近一次）。</summary>
        public StepStatus? LastNewStepStatus { get; private set; }

        /// <summary>New 模式是否拒绝了启动（拒绝原因非空）。</summary>
        public string NewStartupRejectionReason { get; private set; }

        /// <summary>旧 <c>BattleTimeline</c> 自报的累计 <c>AdvanceTime</c> 调用数。</summary>
        public int LegacyAdvanceTimeTotalCalls { get; private set; }

        /// <summary>紧邻上一次 Legacy 适配器推进观察到的权威计数增量。</summary>
        public int LegacyAdvanceTimeObservedDelta { get; private set; }

        /// <summary>Legacy 从属写入组是否按模式启用（最近一次应用后的状态）。</summary>
        public bool LegacyWriterGroupEnabled { get; private set; }

        public void Reset()
        {
            _advanceCalls.Clear();
            _legacyWriterAdvanceCalls.Clear();
            _legacyWriterStates.Clear();
            BootstrapUpdates = 0;
            BootstrapCheckpoints = 0;
            LegacyAdapterInvocations = 0;
            NewDriverInvocations = 0;
            LastLegacyDeltaTime = null;
            LastNewDeltaTime = null;
            LastLegacyWasPaused = false;
            NewDriverTicks = 0;
            LastNewStepStatus = null;
            NewStartupRejectionReason = null;
            LegacyAdvanceTimeTotalCalls = 0;
            LegacyAdvanceTimeObservedDelta = 0;
            LegacyWriterGroupEnabled = false;
        }

        public void RecordBootstrapUpdate() => BootstrapUpdates++;

        public void RecordBootstrapCheckpoint() => BootstrapCheckpoints++;

        /// <summary>记录一次顶层推进调用（模式无关；按模式断言由测试完成）。</summary>
        public void RecordAdvance(RuntimeAdvancePath path)
        {
            int current;
            _advanceCalls.TryGetValue(path, out current);
            _advanceCalls[path] = current + 1;
        }

        public int AdvanceCalls(RuntimeAdvancePath path)
        {
            int current;
            return _advanceCalls.TryGetValue(path, out current) ? current : 0;
        }

        /// <param name="previousAdvanceTimeCalls">推进<strong>之前</strong>读到的旧权威计数。</param>
        /// <param name="advanceTimeCallsObserved">推进<strong>之后</strong>读到的旧权威计数。</param>
        public void RecordLegacyAdapterInvocation(
            float deltaTime, bool wasPaused, int previousAdvanceTimeCalls, int advanceTimeCallsObserved)
        {
            LegacyAdapterInvocations++;
            RecordAdvance(RuntimeAdvancePath.LegacyAdvanceTime);
            LastLegacyDeltaTime = deltaTime;
            LastLegacyWasPaused = wasPaused;
            LegacyAdvanceTimeTotalCalls = advanceTimeCallsObserved;
            LegacyAdvanceTimeObservedDelta = advanceTimeCallsObserved - previousAdvanceTimeCalls;
        }

        public void RecordNewDriverInvocation(float deltaTime)
        {
            NewDriverInvocations++;
            LastNewDeltaTime = deltaTime;
        }

        /// <summary>记录一次固定 Tick 的新模拟推进（<c>BattleSimulation.Step</c>）。</summary>
        public void RecordNewSimulationStep(StepStatus status)
        {
            NewDriverTicks++;
            RecordAdvance(RuntimeAdvancePath.NewSimulationStep);
            LastNewStepStatus = status;
        }

        public void RecordNewStartupRejection(string reason)
        {
            NewStartupRejectionReason = reason;
        }

        public void RecordLegacyWriterGroupApplied(bool enabled, IReadOnlyList<LegacyWriterRegistration> registrations)
        {
            LegacyWriterGroupEnabled = enabled;
            if (registrations == null) return;
            for (int i = 0; i < registrations.Count; i++)
            {
                var registration = registrations[i];
                if (registration == null) continue;
                _legacyWriterStates[registration.CallbackSite] = registration.IsEnabled;
            }
        }

        public bool? LegacyWriterEnabled(string callbackSite)
        {
            bool value;
            return _legacyWriterStates.TryGetValue(callbackSite, out value) ? value : (bool?)null;
        }

        public IReadOnlyDictionary<string, bool> LegacyWriterStates => _legacyWriterStates;

        /// <summary>由 Legacy 适配器转发的旧 <c>AdvanceTime</c> 实际调用次数（按回调点）。</summary>
        public void RecordLegacyWriterAdvanceCall(string callbackSite)
        {
            int current;
            _legacyWriterAdvanceCalls.TryGetValue(callbackSite, out current);
            _legacyWriterAdvanceCalls[callbackSite] = current + 1;
        }

        public int LegacyWriterAdvanceCalls(string callbackSite)
        {
            int current;
            return _legacyWriterAdvanceCalls.TryGetValue(callbackSite, out current) ? current : 0;
        }

        /// <summary>三种模式的调用矩阵（诊断输出；顺序 = <see cref="BattleRuntimeModes.All"/>）。</summary>
        public string Describe()
        {
            return "mode-matrix legacyAdvance=" + AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime)
                + " newStep=" + AdvanceCalls(RuntimeAdvancePath.NewSimulationStep)
                + " legacyAdapter=" + LegacyAdapterInvocations
                + " newDriver=" + NewDriverInvocations
                + " legacyWriterGroup=" + (LegacyWriterGroupEnabled ? "on" : "off")
                + " bootstrapUpdate=" + BootstrapUpdates
                + " checkpoint=" + BootstrapCheckpoints
                + " timelineAdvanceTotal=" + LegacyAdvanceTimeTotalCalls;
        }
    }

    /// <summary>
    /// Shadow 越界写入的四类（任务 03B「必须产出」9 后半句；第二收尾轮 R3）。
    ///
    /// 它们与 <see cref="ShadowWriteCounters"/> 的四个分项一一对应；
    /// 分类的用途是让"哪一类写入被尝试过"成为可断言的事实，而不是一个恒真的总数。
    /// </summary>
    public enum ShadowWriteKind
    {
        /// <summary>对 Unity 场景对象（GameObject/Component/Transform/资源）的写入。</summary>
        UnityObject = 0,

        /// <summary>对旧运行组可变状态（CombatUnit 字段、BattleTimeline 计划表等）的写入。</summary>
        LegacyState = 1,

        /// <summary>表现反馈写入（伤害数字、震屏、顿帧、动画触发……）。</summary>
        Feedback = 2,

        /// <summary>绑定正式 View。</summary>
        ViewBinding = 3
    }

    /// <summary>
    /// 写入许可令牌（能力令牌模式）。
    ///
    /// 只有持有<strong>已发放</strong>令牌的调用方才被允许写入。Shadow 侧的令牌
    /// <strong>从不发放</strong>（<see cref="ShadowWriteWall.Permit"/> 的发放标志恒为 false），
    /// 因此"Shadow 写 Unity/旧状态/反馈/View"在结构上不可完成，而不是靠约定。
    /// 令牌是 readonly struct 且只有本文件的墙能构造，外部无法伪造。
    /// </summary>
    public readonly struct ShadowWritePermit
    {
        internal ShadowWritePermit(bool issued)
        {
            IsIssued = issued;
        }

        /// <summary>令牌是否由守卫发放（Shadow 侧恒为 false）。</summary>
        public bool IsIssued { get; }
    }

    /// <summary>
    /// Shadow 写入守卫（任务 03B 第二收尾轮 R3；第三收尾轮 R1 更正表述）。
    ///
    /// 为什么需要它：上一轮四项计数器<strong>零调用点</strong>，于是
    /// <c>ShadowWrites.Total == 0</c> 与分项 <c>== 0</c> 是结构性恒真断言——
    /// 即使 Shadow 真的写了 Unity 对象也不会被捕获。本守卫把"写入"收敛到唯一入口：
    /// <list type="bullet">
    /// <item>没有令牌的写入尝试**必须**被拒绝：记入
    /// <see cref="ShadowWriteCounters.RejectedAttempts"/> 与对应分项计数，
    /// 并返回稳定拒绝码 <see cref="SHADOW_WRITE_REJECTED"/>（调用方据此抛错）。</item>
    /// <item>因此"计数器为 0"变成了**可以失败**的断言：负控制探针故意经被守卫的路径
    /// 尝试一次写入 ⇒ 必须被拒绝且计数递增；正常运行时计数保持 0。</item>
    /// </list>
    ///
    /// <strong>覆盖边界（第三收尾轮 R1 如实更正，不得再写成"真实触发路径"）</strong>：
    /// 本守卫**没有**接到任何生产写入点。全仓 <see cref="TryWrite"/> 只有一个生产调用点，
    /// 就是负控制探针本身（<c>BattleRuntimeBootstrap.AttemptForbiddenShadowWriteForTests</c>）。
    /// 也就是说 <c>ShadowWrites.* == 0</c> 证明的是
    /// "<strong>没有调用方持有已发放令牌</strong>"，<strong>不是</strong>"某个真实写入点没写"。
    /// 生产侧不存在写入面这件事由**结构证据**承担（第三收尾轮 R1 选择方案 b）：
    /// ① 本程序集 <c>ProjectHero.Compatibility.Runtime</c> 的 <c>.asmdef</c> 只引用
    /// <c>ProjectHero.Logic</c>，而 <c>ProjectHero.Logic</c> 是 <c>noEngineReferences: true</c>
    /// ⇒ 契约程序集内**类型层面**无法命名旧运行组的 <c>CombatUnit</c>/<c>BattleTimeline</c>，
    /// 也无法经由 Logic 拿到 Unity 对象，因此"往旧状态/Unity 对象写"的代码在这里根本写不出来；
    /// ② <c>AssertNoForbiddenUnitySurface(ShadowBattleRunner|UnityBattleDriver)</c> 按**声明成员**
    /// 核对公开与非公开面均不持有 Unity 视图/反馈类型，且不声明 <c>Update/LateUpdate/FixedUpdate</c>；
    /// ③ 旧侧写入者由 <c>LegacyWriterRegistry</c> 的 <c>enabled</c> 门控整体禁用，
    /// 不经本守卫（它们不是 Shadow 的写入面）。
    /// 若后续任务要宣称"写入点级别已受监控"，必须先把守卫接到真实边界访问器上——
    /// 那需要契约程序集能命名被写对象，属任务 04+ 的范围。
    /// </summary>
    public static class ShadowWriteWall
    {
        /// <summary>越界写入被拒绝时的稳定拒绝码。</summary>
        public const string SHADOW_WRITE_REJECTED = "SHADOW_WRITE_REJECTED";

        /// <summary>Shadow 侧永不发放的令牌（发放标志恒为 false）。</summary>
        public static readonly ShadowWritePermit Permit = new ShadowWritePermit(false);

        /// <summary>
        /// 唯一写入入口。没有已发放令牌时**必然失败**并把事实记入计数器。
        /// </summary>
        /// <returns>被拒绝时返回 <see cref="SHADOW_WRITE_REJECTED"/>；否则返回 null。</returns>
        public static string TryWrite(
            ShadowWriteCounters counters, ShadowWritePermit permit, ShadowWriteKind kind, string target)
        {
            if (permit.IsIssued) return null;

            counters?.RecordRejectedAttempt(kind);
            return SHADOW_WRITE_REJECTED + "|" + kind + "|" + (target ?? "<unknown>");
        }
    }

    /// <summary>
    /// Shadow 新模拟写入计数器（任务 03B「必须产出」9 后半句）。
    /// Shadow 模式下四项必须全部为 0。
    ///
    /// 第二收尾轮 R3 更正：本计数器<strong>曾经零调用点</strong>，因此
    /// <c>Total == 0</c> 与分项 <c>== 0</c> 是恒真断言（不能失败，因而不是证据）。
    ///
    /// <strong>第三收尾轮 R1 的准确表述（替换此前"接到真实触发路径"的过度说法）</strong>：
    /// 四项分项由 <see cref="ShadowWriteWall.TryWrite"/> 的拒绝路径递增，
    /// 而 <c>TryWrite</c> 在生产中的<strong>唯一调用点就是负控制探针本身</strong>——
    /// 生产侧<strong>没有</strong>写入点埋点，也不存在写入面（结构证据见
    /// <see cref="ShadowWriteWall"/> 的覆盖边界说明）。因此：
    /// <list type="bullet">
    /// <item>能证明的：没有调用方持有已发放令牌（fail-closed 结构）+ 计数器可以失败（探针可证伪）。</item>
    /// <item><strong>不能</strong>证明的：某个真实写入点"没有写"。</item>
    /// </list>
    /// 仍保留的恒定断言由结构证据承担：<c>AssertNoForbiddenUnitySurface</c>
    /// （引用闭包不含 Unity/旧运行时类型）与反向断言 <c>Shadow.SimulationTick &gt; 0</c>。
    /// </summary>
    public sealed class ShadowWriteCounters
    {
        /// <summary>对 Unity 场景对象（GameObject/Component/Transform/资源）的写入次数。</summary>
        public int UnityObjectWrites { get; private set; }

        /// <summary>对旧运行组可变状态（CombatUnit 字段、BattleTimeline 计划表等）的写入次数。</summary>
        public int LegacyStateWrites { get; private set; }

        /// <summary>表现反馈写入次数（伤害数字、震屏、顿帧、动画触发……）。</summary>
        public int FeedbackWrites { get; private set; }

        /// <summary>绑定正式 View 的次数。</summary>
        public int ViewBindings { get; private set; }

        /// <summary>
        /// 被 <see cref="ShadowWriteWall"/> 拒绝的越界写入尝试总数（含被记入分项的那次）。
        ///
        /// 正常运行时必须为 0；负控制探针会把它推到 1，从而证明"计数为 0"不是恒真。
        /// </summary>
        public int RejectedAttempts { get; private set; }

        public int Total => UnityObjectWrites + LegacyStateWrites + FeedbackWrites + ViewBindings;

        // 四个分项只允许 ShadowWriteWall（唯一经令牌校验的写入入口）递增：
        // 若把它们保持 public，任何代码都能绕过守卫直接推高计数，
        // "计数器只能由守卫写入"就不成立（第二收尾轮 R3 的独立审查结论）。
        internal void RecordUnityObjectWrite() => UnityObjectWrites++;

        internal void RecordLegacyStateWrite() => LegacyStateWrites++;

        internal void RecordFeedbackWrite() => FeedbackWrites++;

        internal void RecordViewBinding() => ViewBindings++;

        /// <summary>
        /// 记录一次被拒绝的越界写入尝试：既计入 <see cref="RejectedAttempts"/>，
        /// 也计入对应分项（这样"分项为 0"与"总数为 0"具有同样的可失败性）。
        /// </summary>
        internal void RecordRejectedAttempt(ShadowWriteKind kind)
        {
            RejectedAttempts++;
            switch (kind)
            {
                case ShadowWriteKind.UnityObject:
                    UnityObjectWrites++;
                    break;
                case ShadowWriteKind.LegacyState:
                    LegacyStateWrites++;
                    break;
                case ShadowWriteKind.Feedback:
                    FeedbackWrites++;
                    break;
                case ShadowWriteKind.ViewBinding:
                    ViewBindings++;
                    break;
            }
        }

        /// <summary>战斗之间重置（记录的是事实，重置只影响计数起点）。</summary>
        public void Reset()
        {
            UnityObjectWrites = 0;
            LegacyStateWrites = 0;
            FeedbackWrites = 0;
            ViewBindings = 0;
            RejectedAttempts = 0;
        }

        public string Describe()
        {
            return "shadow-writes unity=" + UnityObjectWrites
                + " legacy=" + LegacyStateWrites
                + " feedback=" + FeedbackWrites
                + " viewBindings=" + ViewBindings
                + " rejectedAttempts=" + RejectedAttempts;
        }
    }
}
