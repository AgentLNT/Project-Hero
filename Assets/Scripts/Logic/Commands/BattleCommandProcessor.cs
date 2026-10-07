using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Commands
{
    /// <summary>
    /// <strong>载荷处理端口</strong>：<see cref="BattleCommandProcessor"/> 把
    /// 「控制权校验通过之后」的载荷交给装配方提供的处理器。
    ///
    /// 返回值 = null 表示已处理（成功或幂等无操作）；否则为稳定拒绝码。
    /// 处理器<strong>不</strong>接收载荷里的单位作为权限依据——它只能从
    /// <see cref="CommandEnvelope"/> 读取发行者身份（入口绑定的唯一事实）。
    /// </summary>
    public interface IAuthorityRoutedCommandProcessor
    {
        /// <summary>
        /// 处理一条已通过控制权校验的命令。
        /// </summary>
        /// <param name="envelope">已获得 <c>CommandSequence</c> 的规范命令。</param>
        /// <param name="tick">本 Step 的命令处理 Tick（= 冻结批次的目标 Tick）。</param>
        /// <param name="batchBaseScheduleRevision">本 Step 入口冻结的排程基线修订号。</param>
        string ProcessAuthorized(CommandEnvelope envelope, long tick, long batchBaseScheduleRevision);
    }

    /// <summary>
    /// <strong>按载荷判别分派的端口适配器</strong>（任务 09「必须产出」6/7）。
    ///
    /// 它把「只处理某一类载荷」的委托暴露成端口，因此三个端口<strong>互不越界</strong>：
    /// 反应载荷绝不会落到窗口/排程端口上被二次解释，反之亦然。
    /// 这是一条<strong>结构性</strong>保证（不是调用纪律）：判别不匹配即以
    /// <see cref="CommandCodes.SCOPE_PAYLOAD_MISMATCH"/> 稳定拒绝。
    /// </summary>
    public sealed class PayloadRoutedCommandPort : IAuthorityRoutedCommandProcessor
    {
        private readonly CommandScopeKind _kind;
        private readonly Func<CommandEnvelope, long, long, string> _handler;

        public PayloadRoutedCommandPort(
            CommandScopeKind kind, Func<CommandEnvelope, long, long, string> handler)
        {
            _kind = kind;
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            // 委托的目标对象：让"两个端口是否指向同一个权威实现"成为可观察事实。
            Target = handler.Target;
        }

        /// <summary>本端口唯一接受的载荷判别。</summary>
        public CommandScopeKind Kind => _kind;

        /// <summary>被委托实现所在的对象（诊断/测试用；不参与逻辑与哈希）。</summary>
        public object Target { get; }

        public string ProcessAuthorized(CommandEnvelope envelope, long tick, long batchBaseScheduleRevision)
        {
            if (envelope?.Request?.Payload == null) return CommandCodes.COMMAND_REQUEST_NULL;
            if (envelope.Request.Payload.Kind != _kind) return CommandCodes.SCOPE_PAYLOAD_MISMATCH;
            return _handler(envelope, tick, batchBaseScheduleRevision);
        }
    }

    /// <summary>
    /// <strong><c>BattleCommandProcessor</c></strong>（任务 09「必须产出」6）：
    /// 所有来源（玩家 / AI / 旧壳 / 系统）<strong>唯一</strong>的命令入口。
    ///
    /// 冻结的处理顺序（每一步都不可跳过、不可重排）：
    /// <list type="number">
    /// <item><strong>控制权</strong>：用权威 <c>ControllerId -&gt; UnitId 集合</c> 验证发行者能控制
    /// 其载荷涉及的单位（<see cref="CommandAuthority"/>）。发行者身份<strong>只</strong>来自
    /// <see cref="CommandEnvelope.ControllerId"/>（入口绑定）；载荷里的单位 ID 只表达意图目标。
    /// 失败 ⇒ <see cref="CommandCodes.COMMAND_ISSUER_CANNOT_CONTROL_UNIT"/>，零局部写入。</item>
    /// <item><strong>路由</strong>：排程编辑 ⇒ 任务 05/07 的<strong>统一</strong>
    /// <c>ScheduleEditor</c>（<see cref="ScheduleEditSink"/>，绝不重建第二套排程写入路径）；
    /// 反应 ⇒ 任务 05 的 <c>ReactionPlanner</c> 命令路径（<see cref="ReactionSink"/>）；
    /// 窗口 ⇒ 任务 07（<see cref="WindowSink"/>）。</item>
    /// <item><strong>整批语义</strong>：一条命令的拒绝<strong>不</strong>影响同批其他命令；
    /// 拒绝按 <c>CommandSequence</c> 升序返回。</item>
    /// </list>
    /// 它<strong>不</strong>：读窗口、推导反应 TriggerTick、分配 <c>CommandSequence</c>、
    /// 写计划终态，也不检查 <c>SourceKind</c>——玩家与 AI 的差异只体现在入口注册的来源类型。
    /// </summary>
    public sealed class BattleCommandProcessor : IAuthorityRoutedCommandProcessor
    {
        private readonly CommandAuthority _authority;

        public BattleCommandProcessor(CommandAuthority authority)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        }

        /// <summary>唯一的控制权校验入口（只读暴露，供装配与诊断复用同一实例）。</summary>
        public CommandAuthority Authority => _authority;

        /// <summary>
        /// 排程编辑端口（任务 05/07 的统一 <c>ScheduleEditor</c> 事务）。
        /// 未装配时排程命令以 <see cref="CommandCodes.COMMAND_PROCESSOR_NOT_IMPLEMENTED"/> 稳定拒绝。
        /// </summary>
        public IAuthorityRoutedCommandProcessor ScheduleEditSink { get; set; }

        /// <summary>反应命令端口（任务 05 的 <c>ReactionPlanner</c> 命令路径）。</summary>
        public IAuthorityRoutedCommandProcessor ReactionSink { get; set; }

        /// <summary>窗口命令端口（任务 07 的关窗 / 并发激活）。</summary>
        public IAuthorityRoutedCommandProcessor WindowSink { get; set; }

        /// <summary>本 Tick 已成功提交的排程事务数（诊断；与既有处理器同一计数语义）。</summary>
        public int CommittedTransactionCount { get; private set; }

        /// <summary>本 Tick 因控制权被拒的命令数（诊断）。</summary>
        public int AuthorityRejectionCount { get; private set; }

        /// <summary>本 Tick 因无法证明作用单位被拒的命令数（诊断）。</summary>
        public int UnresolvableUnitRejectionCount { get; private set; }

        /// <summary>每 Tick 复位诊断计数（控制权判定本身无状态）。</summary>
        public void BeginTick()
        {
            CommittedTransactionCount = 0;
            AuthorityRejectionCount = 0;
            UnresolvableUnitRejectionCount = 0;
        }

        /// <summary>
        /// 处理一条命令（<see cref="IAuthorityRoutedCommandProcessor"/>）。
        /// 它是<strong>唯一</strong>的载荷路由实现：控制权先于一切。
        /// </summary>
        public string ProcessAuthorized(CommandEnvelope envelope, long tick, long batchBaseScheduleRevision)
        {
            string authorityError = _authority.Authorize(envelope, out IReadOnlyList<UnitId> intentUnits);
            if (authorityError != null)
            {
                if (authorityError == CommandCodes.COMMAND_ISSUER_CANNOT_CONTROL_UNIT) AuthorityRejectionCount++;
                else UnresolvableUnitRejectionCount++;
                return authorityError;
            }

            switch (envelope.Request.Payload)
            {
                case ScheduleEditPayload _:
                {
                    if (ScheduleEditSink == null) return CommandCodes.COMMAND_PROCESSOR_NOT_IMPLEMENTED;
                    string error = ScheduleEditSink.ProcessAuthorized(envelope, tick, batchBaseScheduleRevision);
                    if (error == null) CommittedTransactionCount++;
                    return error;
                }

                case ReactionCommandPayload _:
                    return ReactionSink == null
                        ? CommandCodes.COMMAND_PROCESSOR_NOT_IMPLEMENTED
                        : ReactionSink.ProcessAuthorized(envelope, tick, batchBaseScheduleRevision);

                case WindowCommandPayload _:
                    return WindowSink == null
                        ? CommandCodes.COMMAND_PROCESSOR_NOT_IMPLEMENTED
                        : WindowSink.ProcessAuthorized(envelope, tick, batchBaseScheduleRevision);

                default:
                    return CommandCodes.PAYLOAD_STRUCTURALLY_INVALID;
            }
        }

        /// <summary>
        /// 按 <c>CommandSequence</c> 升序处理一个冻结批次的全部命令。
        /// 网关已经保证 <paramref name="envelopes"/> 是规范顺序；本方法只做一遍前向扫描，
        /// 因此"批内顺序 = 规范顺序"是结构事实而不是调用方约定。
        /// </summary>
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

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
            => "commits=" + CommittedTransactionCount.ToString(CultureInfo.InvariantCulture) +
               " authorityRejections=" + AuthorityRejectionCount.ToString(CultureInfo.InvariantCulture) +
               " unresolvable=" + UnresolvableUnitRejectionCount.ToString(CultureInfo.InvariantCulture);
    }
}
