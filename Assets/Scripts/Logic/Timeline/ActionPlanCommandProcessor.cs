using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
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
    /// </summary>
    public sealed class ActionPlanCommandProcessor : IFrozenCommandProcessor
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

                switch (envelope.Request.Payload)
                {
                    case ScheduleEditPayload schedule:
                    {
                        long expectedRevision = ExpectedRevisionOf(envelope.Request.Scope);
                        ScheduleEditTransactionResult result = _editor.Apply(
                            schedule.Operations, tick, batchBaseScheduleRevision, expectedRevision);
                        if (!result.Succeeded)
                        {
                            rejections.Add(new CommandRejectionRecord(envelope, result.RejectionCode));
                            break;
                        }

                        CommittedTransactionCount++;
                        for (int e = 0; e < result.Evaluations.Count; e++)
                        {
                            _claimedPlans.Add(result.Evaluations[e].PlanId.Value);
                        }
                        CommittedTransactionSink?.Invoke(result, tick);
                        break;
                    }

                    case ReactionCommandPayload _:
                    {
                        string reactionError = _reactionCommandHandler == null
                            ? ReactionCodes.OPPORTUNITY_NOT_OPEN
                            : _reactionCommandHandler(envelope, tick);
                        if (reactionError != null)
                            rejections.Add(new CommandRejectionRecord(envelope, reactionError));
                        break;
                    }

                    case WindowCommandPayload _:
                        // 任务 07：窗口预算与提交授权。本任务不写任何状态。
                        rejections.Add(new CommandRejectionRecord(
                            envelope, FallbackReasonFor(envelope, tick, batchBaseScheduleRevision)));
                        break;

                    default:
                        rejections.Add(new CommandRejectionRecord(
                            envelope, FallbackReasonFor(envelope, tick, batchBaseScheduleRevision)));
                        break;
                }
            }

            return rejections;
        }

        /// <summary>
        /// 把不归本任务管的载荷交给装配里的委托处理器；没有委托时以
        /// <c>COMMAND_PROCESSOR_NOT_IMPLEMENTED</c> 稳定拒绝（绝不静默接受或忽略）。
        /// </summary>
        private string FallbackReasonFor(CommandEnvelope envelope, long tick, long batchBaseScheduleRevision)
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

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
            => "schedule-commits=" + CommittedTransactionCount.ToString(CultureInfo.InvariantCulture) +
               " claimed=" + _claimedPlans.Count.ToString(CultureInfo.InvariantCulture) +
               " rev=" + _authority.ScheduleRevision.ToString(CultureInfo.InvariantCulture);
    }
}
