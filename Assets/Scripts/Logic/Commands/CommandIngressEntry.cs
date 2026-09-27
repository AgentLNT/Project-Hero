using System;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Replay;

namespace ProjectHero.Logic.Commands
{
    /// <summary>
    /// 一个已注册的命令入口。它固定绑定 <see cref="ControllerId"/>、<see cref="SourceKind"/>
    /// 与由来源种类派生的 <see cref="SourcePriority"/>，并为每条被接受的请求分配
    /// 每入口<strong>严格递增</strong>的 <see cref="ProducerOrdinal"/>。
    ///
    /// 只有入口能决定"谁提交了什么"；<see cref="Submit"/> 的返回值只用于诊断/UI，
    /// 权威拒绝事实由注册表在冻结时汇总成入口拒绝事件。
    /// </summary>
    public sealed class CommandIngressEntry
    {
        private readonly CommandIngressRegistry _registry;
        private long _nextProducerOrdinal = 1L;
        private long _frozenProducerOrdinal;

        internal CommandIngressEntry(
            CommandIngressRegistry registry,
            ControllerId controllerId,
            CommandSourceKind sourceKind,
            string registrationKind,
            int registrationOrdinal)
        {
            _registry = registry;
            ControllerId = controllerId;
            SourceKind = sourceKind;
            RegistrationKind = registrationKind;
            RegistrationOrdinal = registrationOrdinal;
            SourcePriority = CommandSourcePriority.Of(sourceKind);
        }

        public ControllerId ControllerId { get; }

        public CommandSourceKind SourceKind { get; }

        /// <summary>由 <see cref="SourceKind"/> 派生（Player=0 / AI=10 / System=20）。</summary>
        public int SourcePriority { get; }

        /// <summary><c>External</c> 或 <c>System</c>；系统来源只能由 Logic 内部创建。</summary>
        public string RegistrationKind { get; }

        /// <summary>系统来源入口：公开 <see cref="Submit"/> 对其稳定拒绝，只有 Logic 内部系统可提交。</summary>
        public bool IsSystemInternal => SourceKind == CommandSourceKind.System;

        public int RegistrationOrdinal { get; }

        /// <summary>下一个将被分配的 ProducerOrdinal（从 1 开始；未被接受的请求不消耗序号）。</summary>
        public long NextProducerOrdinal => _nextProducerOrdinal;

        /// <summary>已冻结水位：已进入冻结批次的最高序号（0 = 尚未冻结）。</summary>
        public long FrozenProducerOrdinal => _frozenProducerOrdinal;

        /// <summary>
        /// 提交一条请求。返回 null 表示已被接受并占用一个 ProducerOrdinal；
        /// 否则返回稳定拒绝记录（迟到请求不消耗序号）。
        ///
        /// 系统来源入口不接受该公开路径：外部调用者不能把自己伪装成 <c>System</c> 优先级。
        /// </summary>
        public CommandIngressRejection Submit(CommandRequest request)
        {
            if (IsSystemInternal)
                return new CommandIngressRejection(ControllerId, SourceKind, SourcePriority,
                    0L, request?.TargetTick ?? 0L, CommandCodes.SYSTEM_SOURCE_INTERNAL_ONLY, 1);

            return SubmitAuthorized(request);
        }

        /// <summary>
        /// 仅限 Logic 内部稳定系统的提交路径（任务 05+ 的系统事务使用）。
        /// 它不是 public，因此外部程序集无法获得系统来源的命令身份。
        /// </summary>
        internal CommandIngressRejection SubmitSystemInternal(CommandRequest request) => SubmitAuthorized(request);

        private CommandIngressRejection SubmitAuthorized(CommandRequest request)
        {
            long ordinal = _nextProducerOrdinal;
            CommandIngressRejection rejection = _registry.OnSubmit(this, ordinal, request, out bool accepted);
            if (accepted) _nextProducerOrdinal = ordinal + 1L;
            return rejection;
        }

        /// <summary>
        /// 只供 Logic 内部的回放驱动使用：把<strong>已记录</strong>的可信请求事实
        /// 按原始 <c>ProducerOrdinal</c> 重新注入，由网关重新验证重复/回退并重新分配
        /// <c>CommandSequence</c>。
        ///
        /// 生产者本身不得使用该路径声明自己的序号：它只接受
        /// <see cref="RecordedCommandRequest"/>（回放记录 DTO），且只对
        /// <c>Player</c> 来源生效；非 Player 记录事实与非法序号被稳定拒绝。
        ///
        /// 该能力<strong>不是</strong> public：外部程序集（UI/AI/UnityView）无法获得"可填写
        /// 生产者序号"的入口，因此"UI/AI 可见 API 中不存在可填写生产者序号的入口"
        /// 这一验收标准在编译期即成立，而不是只靠语义注释约束。
        /// </summary>
        internal CommandIngressRejection InjectRecordedFact(RecordedCommandRequest fact)
        {
            if (fact == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_REQUEST_NULL, ControllerId.Value);

            if (!fact.Issuer.Equals(ControllerId))
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_INGRESS_CONTROLLER_INVALID,
                    "recorded fact issuer " + (fact.Issuer.Value ?? "<null>") + " does not match ingress " + ControllerId.Value);

            if (fact.SourceKind != SourceKind)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_INGRESS_CONTROLLER_INVALID,
                    "recorded fact source " + fact.SourceKind + " does not match ingress " + SourceKind);

            return InjectRecordedFact(fact.ProducerOrdinal, fact.Request);
        }

        /// <summary>
        /// 记录事实注入的底层重载（序号必须来自回放记录，不得来自生产者）。
        /// 与 DTO 重载一样是 <c>internal</c>：只有 Logic 内部的回放驱动可调用。
        /// </summary>
        internal CommandIngressRejection InjectRecordedFact(long producerOrdinal, CommandRequest request)
        {
            CommandIngressRejection rejection = _registry.OnInjectRecordedFact(
                this, producerOrdinal, ordinalProvided: true, request, out bool accepted);
            if (accepted && producerOrdinal >= _nextProducerOrdinal) _nextProducerOrdinal = producerOrdinal + 1L;
            return rejection;
        }

        /// <summary>入口在本批冻结前的水位（供网关判定回退；不改变任何状态）。</summary>
        internal long WatermarkBeforeFreeze => _frozenProducerOrdinal;

        internal void MarkFrozenThrough(long highestOrdinal)
        {
            if (highestOrdinal > _frozenProducerOrdinal) _frozenProducerOrdinal = highestOrdinal;
        }

        /// <summary>规范入口顺序：<c>SourcePriority -&gt; ControllerId(Ordinal)</c>。</summary>
        public static int CompareCanonical(CommandIngressEntry a, CommandIngressEntry b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int byPriority = a.SourcePriority.CompareTo(b.SourcePriority);
            if (byPriority != 0) return byPriority;
            int byController = StringComparer.Ordinal.Compare(a.ControllerId.Value, b.ControllerId.Value);
            return byController != 0 ? byController : a.RegistrationOrdinal.CompareTo(b.RegistrationOrdinal);
        }
    }
}
