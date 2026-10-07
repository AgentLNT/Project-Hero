using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Replay
{
    /// <summary>
    /// 任务 09「必须产出」11 的稳定失败码（回放权威输入记录）。
    ///
    /// 与任务 03 冻结的 <see cref="ReplayCodes"/> 分开：那一族是<strong>回放头部/重演</strong>的
    /// 失败码，本族只描述"记录器把什么样的输入当成权威回放输入"。
    /// </summary>
    public static class ReplayAuthorityCodes
    {
        /// <summary>
        /// 同一个冻结批次对象被记录两次。
        ///
        /// 后果是灾难性的：同一批玩家事实会在回放输入里出现两次，重演时会被注入两次
        /// （第二个 <c>ProducerOrdinal</c> 相撞 ⇒ 整组 <c>DUPLICATE_COMMAND_ORDINAL</c>），
        /// 两次运行不可能得到相同哈希。因此它必须立即失败，不得静默去重。
        /// </summary>
        public const string REPLAY_AUTHORITY_FACT_RECORDED_TWICE = "REPLAY_AUTHORITY_FACT_RECORDED_TWICE";

        /// <summary>命令事件到达的 Tick 出现回退（记录器只接受同一 Tick 或更晚的 Tick）。</summary>
        public const string REPLAY_AUTHORITY_TICK_REGRESSION = "REPLAY_AUTHORITY_TICK_REGRESSION";

        /// <summary>
        /// 处理器级事实（<c>CommandRejectedEvent</c>）引用了本记录器<strong>解析不出</strong>的
        /// <c>CommandSequence</c>：该序号从未经 <see cref="ReplayAuthorityInput.RecordEnvelope"/>
        /// 登记。
        ///
        /// 注意它<strong>不</strong>代表"解析出来源是 AI/System"——那种情况是正常重建事实，直接跳过。
        /// 本码只覆盖真正的驱动契约违规：序号既不在冻结批次的事实里，也不在网关输出里。
        /// </summary>
        public const string REPLAY_AUTHORITY_UNKNOWN_COMMAND_SEQUENCE = "REPLAY_AUTHORITY_UNKNOWN_COMMAND_SEQUENCE";

        /// <summary>记录到的权威输入事实不是 <c>Player</c> 来源（不可达分支的显式护栏）。</summary>
        public const string REPLAY_AUTHORITY_NON_PLAYER_SOURCE = "REPLAY_AUTHORITY_NON_PLAYER_SOURCE";

        /// <summary>参数为 null / 记录序列非法的编程错误。</summary>
        public const string REPLAY_AUTHORITY_ARGUMENT_INVALID = "REPLAY_AUTHORITY_ARGUMENT_INVALID";
    }

    /// <summary>
    /// 一条<strong>权威回放输入</strong>：入口绑定之后的 Player 来源事实。
    ///
    /// 它<strong>只</strong>承载四样东西（任务 09「必须产出」11 与裁定 R-4）：
    /// <list type="number">
    /// <item><see cref="Issuer"/>：入口绑定的 <c>ControllerId</c>（生产者<strong>不能</strong>填写，它由
    /// <see cref="CommandIngressRegistry"/> 在冻结批次里给出）；</item>
    /// <item><see cref="ProducerOrdinal"/>：该入口分配的生产者序号；</item>
    /// <item><see cref="Request"/>：不可变请求（目标 Tick + scope + 载荷）。对 ScheduleEdit 而言，
    /// 载荷就是<strong>确认后的最终 Operations</strong>，scope 里的
    /// <c>ExpectedScheduleRevision</c> 也原样在案；</item>
    /// <item><see cref="SubmittedAtTick"/>：原始提交 Tick（冻结批次的目标 Tick）。</item>
    /// </list>
    ///
    /// <see cref="SourceKind"/> 恒为 <see cref="CommandSourceKind.Player"/>，且它在构造时被强制：
    /// 不存在"AI/System 权威输入"这一形态。这一条同时被
    /// <see cref="ReplayAuthorityInput.RecordFrozenBatch"/> 与
    /// <see cref="ReplayAuthorityInput.RecordTickEvents"/> 在运行时门控。
    /// </summary>
    public sealed record ReplayAuthorityCommand(
        ControllerId Issuer,
        long ProducerOrdinal,
        CommandRequest Request,
        long SubmittedAtTick)
    {
        /// <summary>权威输入只可能是 Player 来源；它不是可填写字段。</summary>
        public CommandSourceKind SourceKind => CommandSourceKind.Player;

        /// <summary>
        /// 把一条<strong>已记录</strong>的事实原样转成回放注入用的 DTO
        /// （Tick 0 重演时经 <c>CommandIngressEntry.InjectRecordedFact</c> 重新校验后注入）。
        ///
        /// 与"读取任意 DTO"的区别：这里的事实只能来自本记录器
        /// （<see cref="ReplayAuthorityInput.RecordFrozenBatch"/> 从冻结批次取），
        /// 因此 <c>SourceKind</c> 与 <c>ProducerOrdinal</c> 都不是生产者自报的。
        /// </summary>
        public RecordedCommandRequest ToRecordedCommandRequest()
            => new RecordedCommandRequest(Issuer, SourceKind, ProducerOrdinal, Request);

        /// <summary>
        /// 权威输入的<strong>唯一</strong>转换点：只接受按冻结批次记录出来的事实。
        ///
        /// 它在结构上再做一次"AI/System 不入权威输入"的检查，因此
        /// "把一条 <c>SourceKind = Ai/System</c> 的回放记录事实当成权威输入"在类型与运行时都不成立，
        /// 但它<strong>不</strong>构成运行时授权：重演注入仍由入口逐条重新校验。
        /// </summary>
        public static bool TryCreateAuthoritative(
            ControllerId issuer, CommandSourceKind sourceKind, long producerOrdinal,
            CommandRequest request, long submittedAtTick, out ReplayAuthorityCommand command)
        {
            command = null;
            if (request == null) return false;
            if (sourceKind != CommandSourceKind.Player) return false;
            if (producerOrdinal < 1L) return false;

            command = new ReplayAuthorityCommand(issuer, producerOrdinal, request, submittedAtTick);
            return true;
        }

        /// <summary>权威输入的规范键：<c>ControllerId(Ordinal)|ProducerOrdinal</c>（Player 优先级恒为 0）。</summary>
        public string CanonicalKey =>
            (Issuer.Value ?? string.Empty) + "|" +
            ProducerOrdinal.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 命令事件的<strong>记录种类</strong>。首版<strong>没有</strong>"未确认手势/取消手势"这一类：
    /// 它们根本不是命令事件（它们是视图层输入模式状态），因此在这里连枚举值都不存在。
    /// </summary>
    public enum ReplayCommandOutcomeKind
    {
        /// <summary>0 保留为无效值：没有种类的记录不得进入回放事件流。</summary>
        Invalid = 0,

        /// <summary>入口接受：命令获得 <c>CommandSequence</c> 并进入本 Tick 的规范命令集合。</summary>
        Accepted = 1,

        /// <summary>入口级拒绝：<strong>从未</strong>获得 <c>CommandSequence</c>（重复规范键/序号回退/结构非法）。</summary>
        IngressRejected = 2,

        /// <summary>处理器级拒绝：已获得 <c>CommandSequence</c> 之后由权威状态判定。</summary>
        ProcessorRejected = 3
    }

    /// <summary>
    /// 一条命令接受/拒绝事实的<strong>记录视图</strong>。
    ///
    /// 它<strong>不</strong>新建第二套事件体系：它逐字段镜像既有的
    /// <see cref="CommandIngressRejectedEvent"/>（<see cref="OutcomeKind"/> =
    /// <see cref="ReplayCommandOutcomeKind.IngressRejected"/>，区别就是"从未获得
    /// <c>CommandSequence</c>"，<see cref="CommandSequence"/> 为 0）与
    /// <see cref="CommandRejectedEvent"/>（<see cref="ReplayCommandOutcomeKind.ProcessorRejected"/>），
    /// 接受侧则对应"命令经网关分配序号后进入 <c>FrozenTickCommandSet.Envelopes</c>"。
    /// <see cref="ReasonCode"/> 复用既有稳定码（<c>CommandCodes.*</c>），不新造码。
    /// </summary>
    public sealed record ReplayCommandOutcome(
        long SubmittedAtTick,
        long EventTick,
        ControllerId Issuer,
        CommandSourceKind SourceKind,
        long ProducerOrdinal,
        long CommandSequence,
        ReplayCommandOutcomeKind OutcomeKind,
        string ReasonCode)
    {
        /// <summary>0 表示"从未获得 <c>CommandSequence</c>"（入口级拒绝与接受前的入口事实）。</summary>
        public bool HasCommandSequence => CommandSequence > 0L;

        /// <summary>该命令是否被接受（获得序号并进入命令处理器）。</summary>
        public bool IsAccepted => OutcomeKind == ReplayCommandOutcomeKind.Accepted;

        /// <summary>规范键：<c>Issuer(Ordinal)|ProducerOrdinal</c>；同一入口内唯一。</summary>
        public string CanonicalKey =>
            (Issuer.Value ?? string.Empty) + "|" +
            ProducerOrdinal.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <strong>回放权威输入记录器</strong>（任务 09「必须产出」11；裁定 R-4）。
    ///
    /// <para><strong>记录什么</strong></para>
    /// <list type="bullet">
    /// <item>入口绑定之后的 <c>Player</c> 来源事实：ControllerId + ProducerOrdinal + Request +
    /// 原始提交 Tick（<see cref="AuthorityCommands"/>）；</item>
    /// <item>这些命令的接受/拒绝事实与其稳定码（<see cref="CommandOutcomes"/>）。</item>
    /// </list>
    ///
    /// <para><strong>为什么不记录 AI/System</strong></para>
    /// AI/System 请求由逻辑在 Tick 0 重演时<strong>重建</strong>（相同初始输入 + 相同确定性 RNG，
    /// 见任务 09「必须产出」9/15）。若把它们的请求或 Envelope 也记成权威输入，重演时会与
    /// 重建结果<strong>双重注入</strong>，同 Tick 抢跑与哈希分叉不可避免。
    /// 因此本记录器<strong>在结构上</strong>让这不可能发生，而不是靠调用方自觉：
    /// <list type="number">
    /// <item><see cref="AuthorityCommands"/> 的元素类型 <see cref="ReplayAuthorityCommand"/>
    /// <strong>没有</strong> <c>SourceKind</c> 字段可填——它的 <c>SourceKind</c> 是只读派生属性且恒为
    /// <see cref="CommandSourceKind.Player"/>；记录"AI 权威输入"在类型上无法表达；</item>
    /// <item><see cref="RecordFrozenBatch"/> 只从冻结批次里取 <c>Player</c> 事实，
    /// 并把被排除的非玩家事实计数到 <see cref="ExcludedNonAuthoritativeFactCount"/>
    /// （可观察、可断言，但不是"静默丢弃"）；</item>
    /// <item><see cref="RecordTickEvents"/> 只折叠 <c>Player</c> 来源的命令事件
    /// （判定依据在两条既有事件族上都能拿到：入口级拒绝自带 <c>SourceKind</c>，
    /// 处理器级拒绝经 <c>CommandSequence → 网关事实</c> 索引解析出来源后再判一次）。
    /// 这个索引<strong>必须</strong>登记全部来源：同一 Tick 里 AI/System 的处理器级拒绝事件
    /// 同样携带序号，若索引只登记 Player，"来源不是 Player"（正常）就会被误判成
    /// "序号未知"（契约违规）而立即失败。登记索引不等于记录事实——
    /// 非 Player 的接受/拒绝事实一条都不进 <see cref="CommandOutcomes"/>；</item>
    /// <item>本类<strong>不提供</strong>任何"记录 AI/System 诊断轨迹"的入口。诊断轨迹不是回放输入，
    /// 首版宁可不记，也不提供一个日后被误当成重演输入的旁路。</item>
    /// </list>
    ///
    /// <para><strong>不记录什么</strong></para>
    /// 时间线本地草稿、鼠标轨迹与吸附预览是 UnityView 输入模式的中间态（任务 09「必须产出」8），
    /// 它们<strong>不</strong>是 <c>CommandRequest</c>，因此没有任何路径能进入本记录器：
    /// 记录的<strong>唯一</strong>来源是 <see cref="CommandIngressRegistry"/> 冻结出的
    /// <see cref="FrozenCommandBatch"/> 与 Step 输出的逻辑事件。只有确认后的原子 Operations
    /// 会出现在 <see cref="AuthorityCommands"/> 的载荷里。
    ///
    /// <para><strong>启动门禁的系统自动延期</strong></para>
    /// 它是逻辑<strong>输出</strong>事件（阶段 7 的 <c>Retryable</c> 路径，经 <c>ScheduleEditor</c>
    /// 与账本发射），既不是 <c>CommandRequest</c> 也不是 System 来源命令，
    /// 因此不出现在 <see cref="AuthorityCommands"/>，也不出现在 <see cref="CommandOutcomes"/>
    /// （后者只折叠 <see cref="CommandIngressRejectedEvent"/> / <see cref="CommandRejectedEvent"/>）。
    ///
    /// <para><strong>确定性</strong></para>
    /// 全部集合只是按记录顺序追加的只读副本；相同冻结批次序列 + 相同事件序列 ⇒ 相同记录。
    /// 本类不进任何哈希，也不改变任何授权判定（它<strong>只读</strong>记录，零逻辑写入）。
    /// </summary>
    public sealed class ReplayAuthorityInput
    {
        private readonly List<ReplayAuthorityCommand> _commands = new List<ReplayAuthorityCommand>();
        private readonly List<ReplayCommandOutcome> _outcomes = new List<ReplayCommandOutcome>();

        /// <summary>
        /// <c>CommandSequence → 该序号的入口绑定事实</c>的<strong>解析索引</strong>
        /// （处理器级拒绝事件只有序号，必须靠它还原来源）。
        ///
        /// <para>
        /// 它登记<strong>全部</strong>来源（含 AI/System），因为"解析不出序号"必须与
        /// "解析出来源不是 Player"区分开：前者是驱动契约违规，后者是正常的重建事实。
        /// 元素类型刻意<strong>不是</strong> <see cref="ReplayAuthorityCommand"/> ——
        /// 后者的 <c>SourceKind</c> 是恒为 <c>Player</c> 的派生属性，
        /// 用它当解析结果会让"来源不是 Player"这条判定永远失效，AI/System 的拒绝事实
        /// 就会从后门进入回放输入。索引保存的是入口绑定事实的<strong>真实</strong>来源种类。
        /// </para>
        /// <para>
        /// 但登记本身不产出任何权威输入：<see cref="AuthorityCommands"/> 的唯一来源仍是
        /// <see cref="RecordFrozenBatch"/>，非 Player 事实也永远不会被折叠进
        /// <see cref="CommandOutcomes"/>。
        /// </para>
        /// </summary>
        private readonly Dictionary<long, GatewayIssuedFact> _gatewayIssuedBySequence =
            new Dictionary<long, GatewayIssuedFact>();

        /// <summary>
        /// 一条<strong>入口绑定之后</strong>的网关事实（可能是 Player，也可能是 AI/System）。
        ///
        /// 它只服务"按序号解析来源"，<strong>不存在</strong>于
        /// <see cref="AuthorityCommands"/>，也没有把来源种类改写成 <c>Player</c> 的派生属性。
        /// </summary>
        private sealed class GatewayIssuedFact
        {
            public GatewayIssuedFact(ControllerId issuer, CommandSourceKind sourceKind,
                long producerOrdinal, long commandSequence, CommandRequest request, long submittedAtTick)
            {
                Issuer = issuer;
                SourceKind = sourceKind;
                ProducerOrdinal = producerOrdinal;
                CommandSequence = commandSequence;
                Request = request;
                SubmittedAtTick = submittedAtTick;
            }

            public ControllerId Issuer { get; }
            public CommandSourceKind SourceKind { get; }
            public long ProducerOrdinal { get; }
            public long CommandSequence { get; }
            public CommandRequest Request { get; }
            public long SubmittedAtTick { get; }

            public string CanonicalKey =>
                (Issuer.Value ?? string.Empty) + "|" + ProducerOrdinal.ToString(CultureInfo.InvariantCulture);
        }

        private readonly HashSet<string> _recordedCommands = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 本场<strong>全部</strong>已记录入口事实的规范键（含 AI/System）。
        ///
        /// 它与 <see cref="_recordedCommands"/>（只含权威 Player 事实）刻意分开：
        /// 前者回答"这条 Envelope 的序号能不能解析出来源"（入口绑定之后的事实，
        /// 与来源种类无关），后者回答"哪些事实进了权威回放输入"。
        /// 合并两者会让同一 Tick 里 AI/System 的 Envelope 无法登记，
        /// 从而让它们的拒绝事件在折叠时被误判成"序号未知"。
        /// </summary>
        private readonly HashSet<string> _allFrozenFactKeys = new HashSet<string>(StringComparer.Ordinal);

        private FrozenCommandBatch _lastRecordedBatch;
        private long _lastEventTick = -1L;
        private int _excludedNonAuthoritativeFacts;

        /// <summary>按记录顺序排列的权威回放输入（只含 Player 来源）。</summary>
        public IReadOnlyList<ReplayAuthorityCommand> AuthorityCommands => _commands;

        /// <summary>按记录顺序排列的命令接受/拒绝事实（只含 Player 来源）。</summary>
        public IReadOnlyList<ReplayCommandOutcome> CommandOutcomes => _outcomes;

        /// <summary>
        /// 被排除在权威输入之外的<strong>非 Player</strong>入口事实数量（AI/System）。
        ///
        /// 它只计数、不保存载荷：保存载荷就等于把"可被误注入的 AI 请求"重新搬回了回放数据旁边。
        /// </summary>
        public int ExcludedNonAuthoritativeFactCount => _excludedNonAuthoritativeFacts;

        /// <summary>最近一次被记录的冻结批次 Tick（-1 = 尚未记录任何批次）。</summary>
        public long LastRecordedBatchTick { get; private set; } = -1L;

        /// <summary>
        /// 清空全部记录，回到"本场尚未记录任何事实"的状态。
        ///
        /// 用途：长期存活的持有者（<c>BattleRuntimeBootstrap</c>）在<strong>新一场战斗</strong>开局时
        /// 复位同一份记录器——镜像面持有的是本对象本身，替换对象会让镜像读到上一场的事实。
        /// 它<strong>不</strong>改变任何授权判定（本类只读记录、零逻辑写入）。
        /// </summary>
        public void Reset()
        {
            _commands.Clear();
            _outcomes.Clear();
            _gatewayIssuedBySequence.Clear();
            _recordedCommands.Clear();
            _allFrozenFactKeys.Clear();
            _lastRecordedBatch = null;
            _lastEventTick = -1L;
            _excludedNonAuthoritativeFacts = 0;
            LastRecordedBatchTick = -1L;
        }

        /// <summary>
        /// 记录一个已冻结批次中的<strong>权威（Player）</strong>输入事实。
        ///
        /// 调用点：唯一模拟入口在 <c>BattleSimulation.Step</c> 开始前冻结批次之后、执行 Step 之前。
        /// 参数就是注册表冻结出的那个对象本身（不是它的副本）——事实来源必须是
        /// <see cref="CommandIngressRegistry"/>"入口绑定之后"的读数，而不是生产者自报。
        /// </summary>
        /// <returns>本次记录的权威事实条数（&gt;= 0）。</returns>
        public int RecordFrozenBatch(FrozenCommandBatch batch)
        {
            if (batch == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    ReplayAuthorityCodes.REPLAY_AUTHORITY_ARGUMENT_INVALID, "batch is null");

            if (ReferenceEquals(batch, _lastRecordedBatch))
                throw new ProjectHero.Logic.LogicDefinitionException(
                    ReplayAuthorityCodes.REPLAY_AUTHORITY_FACT_RECORDED_TWICE,
                    "batch.TargetTick=" + batch.TargetTick.ToString(CultureInfo.InvariantCulture));

            _lastRecordedBatch = batch;
            LastRecordedBatchTick = batch.TargetTick;

            int recorded = 0;
            IReadOnlyList<SourcedCommandRequest> facts = batch.Requests;
            for (int i = 0; i < facts.Count; i++)
            {
                SourcedCommandRequest fact = facts[i];
                if (fact == null || fact.Request == null)
                    throw new ProjectHero.Logic.LogicDefinitionException(
                        ReplayAuthorityCodes.REPLAY_AUTHORITY_ARGUMENT_INVALID, "frozen batch member is null");

                // 权威回放输入 = 入口绑定之后的 Player 来源。AI/System 由逻辑重建，不记录。
                // 但它们**仍然登记**在"入口绑定之后的全部事实"里：同一 Tick 的网关会给它们
                // 分配 CommandSequence，而它们的接受/拒绝事件只有序号 —— 登记是"能判出来源"
                // 的前提，判出来源之后才谈得上把它们排除在权威输入之外。
                _allFrozenFactKeys.Add(CanonicalKeyOf(fact.ControllerId, fact.ProducerOrdinal));
                if (fact.SourceKind != CommandSourceKind.Player)
                {
                    _excludedNonAuthoritativeFacts++;
                    continue;
                }

                AppendCommand(fact.ControllerId, fact.ProducerOrdinal, fact.Request, batch.TargetTick);
                recorded++;
            }

            return recorded;
        }

        private void AppendCommand(ControllerId issuer, long producerOrdinal, CommandRequest request, long submittedAtTick)
        {
            var command = new ReplayAuthorityCommand(issuer, producerOrdinal, request, submittedAtTick);

            // 不可达分支的显式护栏：类型上 SourceKind 恒为 Player，这里再钉一次。
            if (command.SourceKind != CommandSourceKind.Player)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    ReplayAuthorityCodes.REPLAY_AUTHORITY_NON_PLAYER_SOURCE,
                    command.Issuer.Value ?? "<null>");

            if (!_recordedCommands.Add(command.CanonicalKey))
                throw new ProjectHero.Logic.LogicDefinitionException(
                    ReplayAuthorityCodes.REPLAY_AUTHORITY_FACT_RECORDED_TWICE, command.CanonicalKey);

            _commands.Add(command);
        }

        /// <summary>
        /// 在一条命令获得网关 <c>CommandSequence</c> 时登记该序号（<strong>只登记解析索引</strong>）。
        ///
        /// 调用点：<c>FrozenTickCommandSet</c> 生成之后（<c>Envelopes</c> ∪ <c>RejectedCommands</c>）。
        ///
        /// <para>
        /// 它与 <see cref="AuthorityCommands"/> <strong>不是</strong>同一条记录：登记进本索引
        /// <strong>不</strong>产出任何权威输入事实，因此 AI/System 的序号也在这里登记 ——
        /// 否则同一 Tick 里"AI/System 的处理器级拒绝事件"（它只携带 <c>CommandSequence</c>）
        /// 就会因为解析不出来源而无法与 Player 事实区分开。
        /// </para>
        /// <para>
        /// 唯一能产出 <see cref="ReplayCommandOutcome"/> 的地方是
        /// <see cref="RecordTickEvents"/>，而它在折叠每一条 <c>CommandRejectedEvent</c> 时
        /// 都按本索引解析出的 <see cref="CommandSourceKind"/> 再判一次：
        /// <strong>非 Player 的拒绝事实一条都不进回放输入</strong>。
        /// </para>
        /// <para>
        /// 入口绑定之后的 Player 事实仍然只能由 <see cref="RecordFrozenBatch"/> 产生
        /// （本方法的 <c>ControllerId|ProducerOrdinal</c> 必须已经在案），
        /// 因此"事实来源是唯一模拟入口冻结出的批次"这条不变量不因登记索引而放宽。
        /// </para>
        /// </summary>
        public void RecordEnvelope(CommandEnvelope envelope)
        {
            if (envelope == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    ReplayAuthorityCodes.REPLAY_AUTHORITY_ARGUMENT_INVALID, "envelope is null");

            if (!TryRegisterGatewayIssued(envelope))
                throw new ProjectHero.Logic.LogicDefinitionException(
                    ReplayAuthorityCodes.REPLAY_AUTHORITY_UNKNOWN_COMMAND_SEQUENCE,
                    "未在案的入口事实：" + CanonicalKeyOf(envelope.ControllerId, envelope.ProducerOrdinal));
        }

        /// <summary>
        /// 把一条网关已分配序号的命令登记进"按序号解析来源"的索引。
        /// </summary>
        /// <returns>
        /// <c>true</c> = 该命令的 <c>ControllerId|ProducerOrdinal</c> 确实来自
        /// <see cref="RecordFrozenBatch"/> 在案的入口事实（含 AI/System）；<c>false</c> = 不在案。
        /// </returns>
        private bool TryRegisterGatewayIssued(CommandEnvelope envelope)
        {
            string key = CanonicalKeyOf(envelope.ControllerId, envelope.ProducerOrdinal);
            if (!_allFrozenFactKeys.Contains(key)) return false;

            _gatewayIssuedBySequence[envelope.CommandSequence] = new GatewayIssuedFact(
                envelope.ControllerId, envelope.SourceKind, envelope.ProducerOrdinal,
                envelope.CommandSequence, envelope.Request, envelope.TargetTick);
            return true;
        }

        private static string CanonicalKeyOf(ControllerId issuer, long producerOrdinal)
            => (issuer.Value ?? string.Empty) + "|" + producerOrdinal.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// 折叠一个已完成 Tick 的逻辑事件，记录命令接受/拒绝事实。
        ///
        /// 只消费既有事件族（<see cref="CommandIngressRejectedEvent"/> /
        /// <see cref="CommandRejectedEvent"/>），且只消费 <c>Player</c> 来源的那部分：
        /// 入口级拒绝自带 <c>SourceKind</c>，处理器级拒绝先经
        /// <see cref="RecordEnvelope"/> 登记好的序号索引解析出来源 ——
        /// <strong>AI/System 的接受与拒绝事实一条都不进回放输入</strong>
        /// （它们由逻辑在 Tick 0 重演时重建）。
        /// 其余事件（包含启动门禁的系统自动延期输出）一概不进入权威输入记录。
        /// </summary>
        public int RecordTickEvents(long tick, IReadOnlyList<LogicEvent> events)
        {
            if (tick < _lastEventTick)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    ReplayAuthorityCodes.REPLAY_AUTHORITY_TICK_REGRESSION,
                    "tick=" + tick.ToString(CultureInfo.InvariantCulture) +
                    " last=" + _lastEventTick.ToString(CultureInfo.InvariantCulture));

            _lastEventTick = tick;
            if (events == null) return 0;

            int recorded = 0;
            for (int i = 0; i < events.Count; i++)
            {
                switch (events[i])
                {
                    case CommandIngressRejectedEvent rejection:
                        if (rejection.SourceKind != CommandSourceKind.Player) continue;
                        _outcomes.Add(new ReplayCommandOutcome(
                            rejection.Tick,
                            rejection.Tick,
                            rejection.ControllerId,
                            rejection.SourceKind,
                            rejection.ProducerOrdinal,
                            0L,
                            ReplayCommandOutcomeKind.IngressRejected,
                            rejection.ReasonCode));
                        recorded++;
                        break;

                    case CommandRejectedEvent rejected:
                        if (!_gatewayIssuedBySequence.TryGetValue(rejected.CommandSequence, out GatewayIssuedFact command))
                            throw new ProjectHero.Logic.LogicDefinitionException(
                                ReplayAuthorityCodes.REPLAY_AUTHORITY_UNKNOWN_COMMAND_SEQUENCE,
                                rejected.CommandSequence.ToString(CultureInfo.InvariantCulture));

                        // AI/System 的处理器级拒绝不是权威输入：它们的命令由逻辑重建。
                        // 注意这里筛的是"解析出来的来源"，而不是"序号在不在索引里"。
                        if (command.SourceKind != CommandSourceKind.Player) continue;

                        _outcomes.Add(new ReplayCommandOutcome(
                            command.SubmittedAtTick,
                            rejected.Tick,
                            command.Issuer,
                            command.SourceKind,
                            command.ProducerOrdinal,
                            rejected.CommandSequence,
                            ReplayCommandOutcomeKind.ProcessorRejected,
                            rejected.ReasonCode));
                        recorded++;
                        break;
                }
            }

            return recorded;
        }

        /// <summary>
        /// 记录一条被接受的命令（网关分配序号后进入 <c>FrozenTickCommandSet.Envelopes</c>）。
        ///
        /// 非 Player 来源<strong>一条都不记</strong>：AI/System 的接受事实与它由逻辑重建这一
        /// 事实完全一致，记下来只会让回放输入多出一个"可被误注入"的形态。
        /// 拒绝事实由 <see cref="RecordTickEvents"/> 从既有拒绝事件折叠，不在这里重复造码。
        /// </summary>
        public void RecordAccepted(CommandEnvelope envelope)
        {
            if (envelope == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    ReplayAuthorityCodes.REPLAY_AUTHORITY_ARGUMENT_INVALID, "envelope is null");

            if (envelope.SourceKind != CommandSourceKind.Player) return;

            if (!TryRegisterGatewayIssued(envelope))
                throw new ProjectHero.Logic.LogicDefinitionException(
                    ReplayAuthorityCodes.REPLAY_AUTHORITY_UNKNOWN_COMMAND_SEQUENCE,
                    "未在案的入口事实：" + CanonicalKeyOf(envelope.ControllerId, envelope.ProducerOrdinal));

            _outcomes.Add(new ReplayCommandOutcome(
                envelope.TargetTick,
                envelope.TargetTick,
                envelope.ControllerId,
                envelope.SourceKind,
                envelope.ProducerOrdinal,
                envelope.CommandSequence,
                ReplayCommandOutcomeKind.Accepted,
                null));
        }

        /// <summary>
        /// 记录一条<strong>已获得序号</strong>但被授权层拒绝的命令（复用网关既有的
        /// <see cref="CommandRejectionRecord"/> 与稳定码，不新造第二套）。
        /// </summary>
        public void RecordAuthorizerRejection(CommandRejectionRecord rejection)
        {
            if (rejection == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    ReplayAuthorityCodes.REPLAY_AUTHORITY_ARGUMENT_INVALID, "rejection is null");

            CommandEnvelope envelope = rejection.Envelope;
            if (envelope == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    ReplayAuthorityCodes.REPLAY_AUTHORITY_ARGUMENT_INVALID, "rejection.Envelope is null");

            if (envelope.SourceKind != CommandSourceKind.Player) return;

            _outcomes.Add(new ReplayCommandOutcome(
                envelope.TargetTick,
                envelope.TargetTick,
                envelope.ControllerId,
                envelope.SourceKind,
                envelope.ProducerOrdinal,
                envelope.CommandSequence,
                ReplayCommandOutcomeKind.ProcessorRejected,
                rejection.ReasonCode));
        }
    }
}
