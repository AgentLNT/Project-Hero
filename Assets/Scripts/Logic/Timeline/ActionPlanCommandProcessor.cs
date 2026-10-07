using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// 任务 05 的<strong>阶段 6 命令处理器</strong>：把已冻结、已获得
    /// <c>CommandSequence</c> 的命令路由到权威排程事务。
    ///
    /// 处理规则（严格按 <c>CommandSequence</c> 顺序，网关已保证规范顺序）：
    /// <list type="bullet">
    /// <item><see cref="ScheduleEditPayload"/> ⇒ <see cref="ScheduleEditor.Apply"/>，
    /// <c>ExpectedScheduleRevision</c> 与 Step 冻结的 <c>BatchBaseScheduleRevision</c> 比较。
    /// 失败时整批零局部写入并以稳定码拒绝该命令。</item>
    /// <item>反应命令 ⇒ 阶段 D 的 <c>ReactionOpportunitySystem</c>（本阶段稳定拒绝）。</item>
    /// <item>窗口命令 ⇒ 任务 07（本阶段稳定拒绝，不写任何状态）。</item>
    /// </list>
    ///
    /// 它<strong>不</strong>启动计划：启动门禁是阶段 7，相位顺序不可合并。
    ///
    /// <para>
    /// <strong>任务 09 A 流（产出 6）</strong>：本类现在是统一入口
    /// <see cref="BattleCommandProcessor"/> 的<strong>排程端口</strong>——一个薄适配器，
    /// 只保留任务 05/06/07 冻结的公开面（构造签名、<c>ProcessOrdered</c>、三个端口属性、
    /// <c>ClaimedPlanIds</c>、<c>BeginTick</c>）。载荷路由与<strong>控制权校验</strong>的唯一实现
    /// 在 <see cref="ProcessAuthorized"/> 里，因此不存在第二套分派逻辑：
    /// 所有来源（玩家 / AI / 旧壳 / 系统）经同一入口、同一 `ControllerId -&gt; UnitId` 校验，
    /// 排程写入仍然只经同一个 <c>ScheduleEditor</c>。
    /// </para>
    /// </summary>
    public sealed class ActionPlanCommandProcessor : IAuthorityRoutedCommandProcessor
    {
        private readonly ActionScheduleAuthority _authority;
        private readonly ScheduleEditor _editor;
        private readonly IFrozenCommandProcessor _fallback;
        private Func<CommandEnvelope, long, string> _reactionCommandHandler;

        /// <summary>本 Tick 已被排程事务改写的计划（系统自动延期的批内冲突判据的唯一来源）。</summary>
        private readonly HashSet<long> _claimedPlans = new HashSet<long>();

        /// <param name="fallback">
        /// 不归本任务管的载荷（窗口命令等）的委托处理器（默认任务 03 的拒绝占位）。
        /// 它<strong>不改变</strong>本处理器对排程与反应命令的权威。
        /// </param>
        public ActionPlanCommandProcessor(
            ActionScheduleAuthority authority, ScheduleEditor editor, IFrozenCommandProcessor fallback = null)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _fallback = fallback;
        }

        /// <summary>本 Tick 已被排程事务改写的计划 ID（只读）。</summary>
        public IReadOnlyCollection<long> ClaimedPlanIds => _claimedPlans;

        /// <summary>本 Tick 已成功提交的排程事务数（诊断）。</summary>
        public int CommittedTransactionCount { get; private set; }

        /// <summary>
        /// 反应命令处理端口（阶段 D 的 <c>ReactionOpportunitySystem</c> 注入）。
        /// 返回 null = 接受；否则返回稳定拒绝码。为 null 时反应命令以稳定码拒绝。
        /// </summary>
        public Func<CommandEnvelope, long, string> ReactionCommandHandler
        {
            get => _reactionCommandHandler;
            set => _reactionCommandHandler = value;
        }

        /// <summary>
        /// 成功提交的排程事务端口（装配方接到事件族；每提交一次调用一次）。
        /// 它只被用于<strong>审计与语义事件</strong>：失败/预览事务不会触发它，
        /// 因此"每个成功事务恰好一条已创建事件"是结构事实。
        /// </summary>
        public Action<ScheduleEditTransactionResult, long> CommittedTransactionSink { get; set; }

        /// <summary>
        /// 窗口命令处理端口（任务 07 的 <c>TurnWindowManager</c> + <c>ConcurrentActionSystem</c> 注入）。
        ///
        /// 返回 null = 已处理（成功或幂等无操作）；否则为稳定拒绝码。
        /// 未注入时窗口命令<strong>不</strong>静默接受：它们走既有的 fallback 路径并以稳定码拒绝。
        /// </summary>
        public Func<CommandEnvelope, long, string> WindowCommandHandler { get; set; }

        /// <summary>每个 Step 开始时复位本 Tick 的批内冲突集合。</summary>
        public void BeginTick()
        {
            _claimedPlans.Clear();
            CommittedTransactionCount = 0;
        }

        public IReadOnlyList<CommandRejectionRecord> ProcessOrdered(
            IReadOnlyList<CommandEnvelope> envelopes, long tick, long batchBaseScheduleRevision)
        {
            if (envelopes == null || envelopes.Count == 0) return Array.Empty<CommandRejectionRecord>();

            var rejections = new List<CommandRejectionRecord>();
            for (int i = 0; i < envelopes.Count; i++)
            {
                CommandEnvelope envelope = envelopes[i];
                if (envelope == null) continue;
                string error = ProcessAuthorized(envelope, tick, batchBaseScheduleRevision);
                if (error != null) rejections.Add(new CommandRejectionRecord(envelope, error));
            }
            return rejections;
        }

        /// <summary>
        /// <strong>载荷路由的唯一实现</strong>（任务 09「必须产出」6/7）。控制权校验发生在
        /// <see cref="BattleCommandProcessor"/>（统一入口）里，本方法只处理"已授权的载荷"：
        /// 因此直接调用本方法的调用方（任务 06 的既有夹具、任务 10 的装配）与生产路径
        /// 共享同一份载荷语义，而生产路径额外多一道控制权门。
        /// </summary>
        public string ProcessAuthorized(CommandEnvelope envelope, long tick, long batchBaseScheduleRevision)
        {
            if (envelope?.Request == null) return CommandCodes.COMMAND_REQUEST_NULL;

            switch (envelope.Request.Payload)
            {
                case ScheduleEditPayload schedule:
                {
                    long expectedRevision = ExpectedRevisionOf(envelope.Request.Scope);
                    // 任务 07「必须产出」4 / 00 号规则 18：新增或增加预算时窗口必填，
                    // 且发行者身份只能来自命令网关绑定的 CommandEnvelope.ControllerId
                    // （绝不来自命令载荷、scope 或调用参数）。
                    ScheduleEditTransactionResult result = _editor.Apply(
                        schedule.Operations, tick, batchBaseScheduleRevision, expectedRevision,
                        preview: false,
                        expectedWindowId: ExpectedWindowIdOf(envelope.Request.Scope),
                        issuer: envelope.ControllerId);
                    if (!result.Succeeded) return result.RejectionCode;

                    CommittedTransactionCount++;
                    for (int e = 0; e < result.Evaluations.Count; e++)
                    {
                        _claimedPlans.Add(result.Evaluations[e].PlanId.Value);
                    }
                    CommittedTransactionSink?.Invoke(result, tick);
                    return null;
                }

                case ReactionCommandPayload _:
                {
                    // 反应 scope 必须精确匹配：反应载荷配窗口/排程 scope 一律稳定拒绝，
                    // 绝不回退到"当前窗口"或临时猜一个机会。
                    if (!(envelope.Request.Scope is ReactionCommandScope))
                        return CommandCodes.SCOPE_PAYLOAD_MISMATCH;
                    string reactionError = _reactionCommandHandler == null
                        ? ReactionCodes.OPPORTUNITY_NOT_OPEN
                        : _reactionCommandHandler(envelope, tick);
                    return reactionError;
                }

                case WindowCommandPayload _:
                {
                    // 任务 07：关窗与并发行动激活的权威入口。
                    // 身份（ControllerId）、目标窗口（ExpectedWindowId）与权威费用都由
                    // 注入的处理器自行从 envelope/scope/定义读取；载荷里根本没有这些字段。
                    // 未注入处理器时保持既有语义：走 fallback（没有 fallback 即以稳定码拒绝），
                    // 绝不静默接受。
                    if (WindowCommandHandler == null)
                        return FallbackReasonFor(envelope, tick, batchBaseScheduleRevision);

                    return WindowCommandHandler(envelope, tick);
                }

                default:
                    return FallbackReasonFor(envelope, tick, batchBaseScheduleRevision);
            }
        }

        /// <summary>
        /// 把不归本任务管的载荷交给装配里的委托处理器；没有委托时以
        /// <c>COMMAND_PROCESSOR_NOT_IMPLEMENTED</c> 稳定拒绝（绝不静默接受或忽略）。
        /// </summary>
        public string FallbackReasonFor(CommandEnvelope envelope, long tick, long batchBaseScheduleRevision)
        {
            if (_fallback == null) return CommandCodes.COMMAND_PROCESSOR_NOT_IMPLEMENTED;
            IReadOnlyList<CommandRejectionRecord> records =
                _fallback.ProcessOrdered(new[] { envelope }, tick, batchBaseScheduleRevision);
            if (records == null || records.Count == 0) return null;
            return records[0]?.ReasonCode ?? CommandCodes.COMMAND_PROCESSOR_NOT_IMPLEMENTED;
        }

        /// <summary>
        /// 从排程 scope 读取 <c>ExpectedScheduleRevision</c>。
        /// scope 判别不匹配时返回一个不可能相等的哨兵值，从而以
        /// <c>STALE_SCHEDULE_REVISION</c> 稳定拒绝（绝不回退到"当前修订号"）。
        /// </summary>
        private static long ExpectedRevisionOf(CommandScope scope)
            => scope is ScheduleEditScope schedule ? schedule.ExpectedScheduleRevision : long.MinValue;

        /// <summary>
        /// 从排程 scope 读取 <c>ExpectedWindowId</c>（任务 07「必须产出」4）。
        ///
        /// scope 判别不匹配时返回 <c>null</c>（"没有声明窗口"），
        /// <strong>绝不</strong>回退成"当前窗口"：
        /// 此时同一条命令的 <c>ExpectedScheduleRevision</c> 已经是哨兵值，
        /// 一定先以 <c>STALE_SCHEDULE_REVISION</c> 稳定拒绝，因此窗口参数不会被用于任何授权。
        /// </summary>
        private static WindowId? ExpectedWindowIdOf(CommandScope scope)
            => scope is ScheduleEditScope schedule ? schedule.ExpectedWindowId : (WindowId?)null;

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
            => "schedule-commits=" + CommittedTransactionCount.ToString(CultureInfo.InvariantCulture) +
               " claimed=" + _claimedPlans.Count.ToString(CultureInfo.InvariantCulture) +
               " rev=" + _authority.ScheduleRevision.ToString(CultureInfo.InvariantCulture);
    }
}
