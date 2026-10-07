using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Replay;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// 一个逻辑 Tick 上被镜像进 Shadow 独立世界的<strong>权威事实读数</strong>。
    ///
    /// 它只暴露已经被入口绑定的 Player 事实（Issuer / ProducerOrdinal / Request / 原始提交 Tick），
    /// 顺序与 <see cref="ReplayAuthorityInput.AuthorityCommands"/> 的记录顺序逐位相同。
    /// </summary>
    public sealed class ShadowMirroredTickView
    {
        internal ShadowMirroredTickView(
            long logicalTick,
            IReadOnlyList<ShadowMirroredRequest> requests,
            IReadOnlyList<ReplayAuthorityCommand> commands)
        {
            LogicalTick = logicalTick;
            Requests = requests;
            Commands = commands;
        }

        public long LogicalTick { get; }

        /// <summary>本 Tick 要注入新模拟的请求（已按记录顺序排列）。</summary>
        public IReadOnlyList<ShadowMirroredRequest> Requests { get; }

        /// <summary>与 <see cref="Requests"/> 逐位对应的记录事实（诊断读数：序号来自入口分配）。</summary>
        public IReadOnlyList<ReplayAuthorityCommand> Commands { get; }

        public int Count => Requests != null ? Requests.Count : 0;
    }

    /// <summary>
    /// <strong>Shadow 真实请求镜像</strong>（任务 09「必须产出」16）。
    ///
    /// <para><strong>镜像什么</strong></para>
    /// 唯一数据来源是 <see cref="ReplayAuthorityInput.AuthorityCommands"/>：入口绑定之后、
    /// <strong>只含 Player</strong> 来源的权威事实（<c>Issuer</c> / <c>ProducerOrdinal</c> /
    /// <c>Request</c> / 原始提交 Tick）。因此：
    /// <list type="bullet">
    /// <item>本类型<strong>不读</strong> <c>CommandIngressRegistry._gatewayIssuedBySequence</c>
    /// 或任何"登记了全部来源"的解析索引——镜像面在类型上就取不到 AI/System 载荷；</item>
    /// <item>本类型<strong>不读</strong> Legacy/View/Unity 对象（不变量 22：Shadow 零写入）。</item>
    /// </list>
    ///
    /// <para><strong>AI / System 由新模拟自己重建</strong></para>
    /// 它们不在这里出现，也不存在"镜像它们的请求"的入口。新模拟与旧世界共享的只有
    /// 「同一定义 + 同一初始输入（含 RNG）」（<c>BattleSimulation.Create(Definition, EncounterId,
    /// RuntimeInputs)</c>），AI/System 的请求因此由新世界<strong>自己从 Tick 0 重建</strong>，
    /// 而不是被二次注入。这就是"绝不二次注入"在结构上的成立方式。
    ///
    /// <para><strong>恰好一次</strong></para>
    /// 每条记录事实只在其<strong>原始提交 Tick</strong>（<c>SubmittedAtTick</c>，= 冻结批次 Tick）
    /// 被交出一次：游标只前进不回退，<see cref="RequestsFor"/> 对同一个 Tick 的第二次调用返回空，
    /// 而 <see cref="ShadowBattleRunner"/> 侧另有一道"同一入口事实不得二次注入"的护栏。
    /// 回退的 Tick 请求是显式契约违规（抛 <see cref="LogicDefinitionException"/>），
    /// 不是"跳过一条"或"重放一条"。
    /// </summary>
    public sealed class ShadowPlayerRequestMirror : ShadowBattleRunner.MirroredRequestSourceBase
    {
        /// <summary>逻辑 Tick 回退（镜像只按原始提交 Tick 单调交出事实）。</summary>
        public const string SHADOW_MIRROR_TICK_REGRESSION = "SHADOW_MIRROR_TICK_REGRESSION";

        private readonly ReplayAuthorityInput _authority;
        private int _cursor;
        private long _lastRequestedTick = -1L;
        private int _requestsFetched;
        private int _totalMirroredRequests;

        public ShadowPlayerRequestMirror(ReplayAuthorityInput authorityCommands)
        {
            _authority = authorityCommands ?? throw new ArgumentNullException(nameof(authorityCommands));
        }

        public override string SourceName => "shadow-player-request-mirror";

        /// <summary>权威事实总数（= <see cref="ReplayAuthorityInput.AuthorityCommands"/> 的条数）。</summary>
        public int AuthorityCommandCount => _authority.AuthorityCommands.Count;

        /// <summary><see cref="RequestsFor"/> 被调用的次数（用于证明"推进真的在取镜像"）。</summary>
        public int RequestsFetched => _requestsFetched;

        /// <summary>已交出的镜像请求总数（每条权威事实最多一次）。</summary>
        public int TotalMirroredRequests => _totalMirroredRequests;

        /// <summary>
        /// 镜像是否已经交出<strong>全部</strong>权威事实。未交出表示镜像面本身有缺口
        /// （例如某些 Tick 从未被请求）——它必须能被断言，而不是被静默忽略。
        /// </summary>
        public bool AllAuthorityCommandsMirrored => _cursor >= _authority.AuthorityCommands.Count;

        /// <summary>被排除在权威输入之外的非 Player 入口事实数（AI/System；只计数、不保存载荷）。</summary>
        public int ExcludedNonAuthoritativeFactCount => _authority.ExcludedNonAuthoritativeFactCount;

        /// <summary>
        /// 本逻辑 Tick 要镜像给新模拟的 Player 请求。
        ///
        /// 只返回 <c>SubmittedAtTick == logicalTick</c> 的权威事实，且每条事实只返回一次。
        /// 回退请求立即失败：静默返回空会让"镜像缺失"伪装成"这一 Tick 本来没有请求"。
        /// 单调性守卫在 <see cref="RequestsForTick"/> 里（两个公开入口共用同一份实现，
        /// 因此"回退必须显式失败"对所有入口一致成立，而不是只对其中一个）。
        /// </summary>
        public override IReadOnlyList<ShadowMirroredRequest> RequestsFor(long logicalTick)
            => RequestsForTick(logicalTick).Requests;

        /// <summary>
        /// 与 <see cref="RequestsFor"/> 同一份读数，但额外给出逐位对应的记录事实
        /// （ProducerOrdinal 等诊断字段）。第二次调用同一个 Tick 仍返回同一个视图对象，
        /// 但 <see cref="ShadowMirroredTickView.Requests"/> 已经交出过，不再重复交出新请求。
        ///
        /// <para><strong>单调性</strong>：本方法是两个公开入口的<strong>共同实现</strong>，
        /// 回退 Tick 在这里立即失败（抛 <see cref="SHADOW_MIRROR_TICK_REGRESSION"/>）。
        /// 守卫放在这里而不是只放在 <see cref="RequestsFor"/> 里，是为了让"回退即契约违规"
        /// 对<strong>所有</strong>入口一致成立 —— 若只有委托方那条重载守卫，任何绕过它直接调用
        /// <see cref="RequestsForTick"/> 的调用点都能在不报错的情况下反复取数
        /// （表现为"同一事实被交出多次"或"镜像缺失被伪装成该 Tick 本来没有请求"）。</para>
        ///
        /// 空读数返回共享的 <see cref="Array.Empty{T}()"/>（而不是私有可变列表）：
        /// 调用方拿到的只读视图<strong>不</strong>是任何内部集合的别名，向下转型也不可能改到本对象的状态。
        /// </summary>
        public ShadowMirroredTickView RequestsForTick(long logicalTick)
        {
            if (_requestsFetched > 0 && logicalTick < _lastRequestedTick)
                throw new LogicDefinitionException(
                    SHADOW_MIRROR_TICK_REGRESSION,
                    "tick=" + logicalTick.ToString(CultureInfo.InvariantCulture) +
                    " last=" + _lastRequestedTick.ToString(CultureInfo.InvariantCulture));

            _requestsFetched++;
            _lastRequestedTick = logicalTick;

            IReadOnlyList<ReplayAuthorityCommand> commands = _authority.AuthorityCommands;

            int first = _cursor;
            while (_cursor < commands.Count && commands[_cursor].SubmittedAtTick == logicalTick)
                _cursor++;

            int count = _cursor - first;
            if (count <= 0)
            {
                return new ShadowMirroredTickView(
                    logicalTick,
                    Array.Empty<ShadowMirroredRequest>(),
                    Array.Empty<ReplayAuthorityCommand>());
            }

            var requests = new List<ShadowMirroredRequest>(count);
            var facts = new List<ReplayAuthorityCommand>(count);
            for (int i = first; i < _cursor; i++)
            {
                ReplayAuthorityCommand command = commands[i];
                facts.Add(command);

                // Origin 只是诊断标签：载荷与来源都取自记录事实，不在这里重新解释。
                requests.Add(new ShadowMirroredRequest(
                    command.Issuer, command.Request, DescribeOrigin(command), command.SubmittedAtTick));
            }

            _totalMirroredRequests += count;
            return new ShadowMirroredTickView(logicalTick, requests, facts);
        }

        private static string DescribeOrigin(ReplayAuthorityCommand command)
            => "authority-command|" + command.CanonicalKey
               + "|submitted=" + command.SubmittedAtTick.ToString(CultureInfo.InvariantCulture);

        /// <summary>诊断用：把权威事实按 <c>SubmittedAtTick</c> 整理成可读清单（只读，不参与注入）。</summary>
        public string Describe()
        {
            var builder = new System.Text.StringBuilder();
            builder.Append("AuthorityCommands=").Append(AuthorityCommandCount)
                .Append(" mirrored=").Append(_totalMirroredRequests)
                .Append(" excludedNonAuthoritative=").Append(ExcludedNonAuthoritativeFactCount)
                .Append(" ticks[");
            IReadOnlyList<ReplayAuthorityCommand> commands = _authority.AuthorityCommands;
            for (int i = 0; i < commands.Count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(commands[i].CanonicalKey)
                    .Append('@')
                    .Append(commands[i].SubmittedAtTick.ToString(CultureInfo.InvariantCulture));
            }
            builder.Append(']');
            return builder.ToString();
        }
    }
}
