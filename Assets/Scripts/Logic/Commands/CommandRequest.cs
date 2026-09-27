using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Commands
{
    /// <summary>
    /// 命令生产者唯一可以构造的请求（主方案 3.6 / 任务 03「必须产出」2）。
    ///
    /// 它<strong>只有三样东西</strong>：目标 Tick、作用域判别、载荷判别。
    /// 生产者<strong>不能</strong>填写 <c>ControllerId</c>、<c>CommandSourceKind</c>、
    /// <c>SourcePriority</c>、<c>ProducerOrdinal</c>、<c>CommandSequence</c>、
    /// 规则费用或反应 <c>TriggerTick</c>——这些字段在类型上不存在。
    /// 身份与顺序由 <see cref="CommandIngressRegistry"/>（唯一信任边界）与
    /// <see cref="CommandGateway"/> 派生。
    /// </summary>
    public sealed record CommandRequest(long TargetTick, CommandScope Scope, ICommandPayload Payload)
    {
        /// <summary>便捷工厂（与构造器完全等价，只提升可读性）。</summary>
        public static CommandRequest To(long targetTick, CommandScope scope, ICommandPayload payload)
            => new CommandRequest(targetTick, scope, payload);

        /// <summary>
        /// 静态结构校验（与权威状态无关，因此可在入口冻结阶段完成）：
        /// 目标 Tick 非负、scope/payload 判别一致、载荷内容结构合法。
        /// 返回首个拒绝码（null = 通过）。权威状态相关的判定属于命令处理器。
        /// </summary>
        public string ValidateStaticStructure()
        {
            if (TargetTick < 0) return CommandCodes.COMMAND_TARGET_TICK_NEGATIVE;
            if (Scope == null) return CommandCodes.COMMAND_SCOPE_NULL;
            if (Payload == null) return CommandCodes.COMMAND_PAYLOAD_NULL;

            if ((int)CommandScopes.KindOf(Scope) != (int)Payload.Kind) return CommandCodes.SCOPE_PAYLOAD_MISMATCH;

            switch (Payload)
            {
                case ScheduleEditPayload schedule:
                    if (schedule.Operations == null || schedule.Operations.Count == 0)
                        return CommandCodes.PAYLOAD_STRUCTURALLY_INVALID;
                    for (int i = 0; i < schedule.Operations.Count; i++)
                    {
                        if (schedule.Operations[i] == null) return CommandCodes.PAYLOAD_STRUCTURALLY_INVALID;
                    }
                    return null;

                case WindowCommandPayload _:
                    return null;

                case ReactionCommandPayload reaction:
                    if (DefinitionIdValidation.ValidateFormat(reaction.ReactionActionSpecId.Value) != null)
                        return CommandCodes.PAYLOAD_STRUCTURALLY_INVALID;
                    // Block 不换位、Dodge 必须给出目的格：与 ReactionCommandKind 精确对应。
                    if (reaction.ReactionKind == ReactionCommandKind.Dodge)
                        return reaction.DodgeDestination.HasValue ? null : CommandCodes.PAYLOAD_STRUCTURALLY_INVALID;
                    return reaction.DodgeDestination.HasValue ? CommandCodes.PAYLOAD_STRUCTURALLY_INVALID : null;

                default:
                    return CommandCodes.PAYLOAD_STRUCTURALLY_INVALID;
            }
        }
    }

    /// <summary>
    /// 已由入口绑定的可信来源事实（Controller + 来源种类 + 派生优先级 + 生产者序号）。
    ///
    /// 构造器<strong>不是 public</strong>：驱动器、表现层与测试都无法伪造来源身份；
    /// 唯一创建路径是 <see cref="CommandIngressRegistry"/> 的提交/冻结流程。
    /// </summary>
    public sealed class SourcedCommandRequest
    {
        internal SourcedCommandRequest(
            ControllerId controllerId,
            Definitions.CommandSourceKind sourceKind,
            int sourcePriority,
            long producerOrdinal,
            long frozenProducerOrdinalWatermarkBefore,
            bool isRecordedFactReplay,
            CommandRequest request)
        {
            ControllerId = controllerId;
            SourceKind = sourceKind;
            SourcePriority = sourcePriority;
            ProducerOrdinal = producerOrdinal;
            FrozenProducerOrdinalWatermarkBefore = frozenProducerOrdinalWatermarkBefore;
            IsRecordedFactReplay = isRecordedFactReplay;
            Request = request;
        }

        public ControllerId ControllerId { get; }

        public Definitions.CommandSourceKind SourceKind { get; }

        /// <summary>由 <c>CommandSourceKind</c> 派生（Player=0 / AI=10 / System=20），生产者不可填写。</summary>
        public int SourcePriority { get; }

        /// <summary>入口分配（或记录事实原样重演）的每入口严格递增序号，从 1 开始。</summary>
        public long ProducerOrdinal { get; }

        /// <summary>本批冻结前该入口的水位；序号低于它即为回退。</summary>
        public long FrozenProducerOrdinalWatermarkBefore { get; }

        /// <summary>是否为 Tick 0 重演注入的已记录事实（诊断与审计用，不改变校验强度）。</summary>
        public bool IsRecordedFactReplay { get; }

        public CommandRequest Request { get; }

        /// <summary>规范键：<c>SourcePriority|ControllerId(Ordinal)|ProducerOrdinal</c>。</summary>
        public string CanonicalKeyString =>
            SourcePriority.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
            (ControllerId.Value ?? string.Empty) + "|" +
            ProducerOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 已冻结的当前 Tick 命令批次。只由 <see cref="CommandIngressRegistry"/> 创建
    /// （构造器不是 public），因此驱动器只能<em>传递</em>它，不能<em>伪造</em>其中的来源事实。
    /// 批次内容按创建时的投递顺序保存；规范化排序由
    /// <see cref="CommandGateway"/> 负责，原始枚举顺序不得成为隐式末级键。
    /// </summary>
    public sealed class FrozenCommandBatch
    {
        internal FrozenCommandBatch(
            long targetTick,
            System.Collections.Generic.IReadOnlyList<SourcedCommandRequest> requests,
            object registryStamp)
        {
            TargetTick = targetTick;
            Requests = requests ?? System.Array.Empty<SourcedCommandRequest>();
            RegistryStamp = registryStamp;
        }

        public long TargetTick { get; }

        public System.Collections.Generic.IReadOnlyList<SourcedCommandRequest> Requests { get; }

        public int Count => Requests.Count;

        /// <summary>
        /// 创建该批次的注册表印记。只有同一注册表冻结出的批次才能被
        /// <c>BattleSimulation.Step</c> 接受，因此"驱动器伪造来源事实"在类型与运行时都不成立。
        /// </summary>
        internal object RegistryStamp { get; }

        /// <summary>空批次（仅用于诊断路径；Step 只接受注册表冻结出的批次）。</summary>
        internal static FrozenCommandBatch Empty(long tick) => new FrozenCommandBatch(tick, System.Array.Empty<SourcedCommandRequest>(), null);
    }

    /// <summary>
    /// 通过入口校验、已获得整场唯一 <see cref="CommandSequence"/> 的可处理命令。
    /// 构造器不是 public：只有 <see cref="CommandGateway"/> 能生成。
    /// </summary>
    public sealed class CommandEnvelope
    {
        internal CommandEnvelope(
            long commandSequence,
            long targetTick,
            ControllerId controllerId,
            Definitions.CommandSourceKind sourceKind,
            int sourcePriority,
            long producerOrdinal,
            CommandRequest request)
        {
            CommandSequence = commandSequence;
            TargetTick = targetTick;
            ControllerId = controllerId;
            SourceKind = sourceKind;
            SourcePriority = sourcePriority;
            ProducerOrdinal = producerOrdinal;
            Request = request;
        }

        /// <summary>整场唯一、单调、不复用；处理器严格按它执行。</summary>
        public long CommandSequence { get; }

        public long TargetTick { get; }

        public ControllerId ControllerId { get; }

        public Definitions.CommandSourceKind SourceKind { get; }

        public int SourcePriority { get; }

        public long ProducerOrdinal { get; }

        public CommandRequest Request { get; }

        /// <summary>规范顺序键：<c>SourcePriority -&gt; ControllerId(Ordinal) -&gt; ProducerOrdinal</c>。</summary>
        public int CompareTo(CommandEnvelope other)
        {
            if (other == null) return 1;
            int byPriority = SourcePriority.CompareTo(other.SourcePriority);
            if (byPriority != 0) return byPriority;
            int byController = System.StringComparer.Ordinal.Compare(ControllerId.Value, other.ControllerId.Value);
            if (byController != 0) return byController;
            return ProducerOrdinal.CompareTo(other.ProducerOrdinal);
        }
    }
}
