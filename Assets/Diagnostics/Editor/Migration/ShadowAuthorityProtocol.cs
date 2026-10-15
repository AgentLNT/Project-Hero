using System.Collections.Generic;
using ProjectHero.Logic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// <strong>回放权威输入的收口协议</strong>（任务 09「必须产出」11 的驱动器契约，
    /// 「必须产出」16 的在案调用点）。
    ///
    /// <para><strong>为什么需要它</strong></para>
    /// <see cref="ReplayAuthorityInput"/> 本身<strong>不是</strong>一个 Step 入口：它只提供
    /// "记录"的原语。若没有唯一的收口点，每个调用方都会各写一套顺序，于是同一场战斗里
    /// 「权威事实由谁产出」就有多份互相矛盾的读数（例如：只记录不折叠 ⇒
    /// <see cref="ReplayAuthorityInput.CommandOutcomes"/> 恒空；先记接受再折叠拒绝 ⇒
    /// 同一条命令同时出现 <c>Accepted</c> 与 <c>ProcessorRejected</c>，即<b>双记</b>）。
    ///
    /// <para><strong>协议（严格按此顺序）</strong></para>
    /// <list type="number">
    /// <item>Step <strong>之前</strong>、冻结批次<strong>之后</strong>：对批次对象本身调一次
    /// <see cref="RecordFrozenBatch"/> —— 权威事实的唯一来源是"入口绑定之后"的读数，
    /// 不是生产者自报；</item>
    /// <item>Step <strong>之后</strong>：按 <c>Envelopes</c> ∪ <c>RejectedCommands</c> 登记
    /// Envelope（只登记解析索引，含 AI/System —— 否则它们的处理器级拒绝事件解析不出来源）；</item>
    /// <item>随后折叠本 Tick 的事件（<see cref="RecordTickEvents"/> 只取 Player 来源）；</item>
    /// <item>最后<strong>只</strong>对"获得序号且本 Tick 未被拒"的命令调
    /// <see cref="RecordAccepted"/>：记录的是<b>最终处置</b>，绝不两种处置各记一次。</item>
    /// </list>
    ///
    /// 非 Player 来源在这四步里全部被排除：接受侧与折叠侧都由 <see cref="ReplayAuthorityInput"/>
    /// 内部按 <c>SourceKind</c> 门控，本协议不替它过滤、也不额外放宽。
    /// </summary>
    public static class ShadowAuthorityProtocol
    {
        /// <summary>第一步：记录一个已冻结批次中的权威（Player）输入事实。</summary>
        /// <returns>本次记录的权威事实条数（&gt;= 0）。</returns>
        public static int RecordFrozenBatch(ReplayAuthorityInput authorityInput, FrozenCommandBatch batch)
        {
            if (authorityInput == null) throw new LogicDefinitionException(
                ReplayAuthorityCodes.REPLAY_AUTHORITY_ARGUMENT_INVALID, "authorityInput is null");
            return authorityInput.RecordFrozenBatch(batch);
        }

        /// <summary>
        /// 第二、三步（Step 之后一次收口）：登记 Envelope → 折叠事件 → 对未被拒者记录接受。
        /// </summary>
        /// <returns>本次记录的命令处置事实条数（接受 + 拒绝，只含 Player）。</returns>
        public static ShadowAuthorityStepFacts RecordOutcomes(
            ReplayAuthorityInput authorityInput, BattleSimulation simulation, long tick, StepResult result)
        {
            if (authorityInput == null) throw new LogicDefinitionException(
                ReplayAuthorityCodes.REPLAY_AUTHORITY_ARGUMENT_INVALID, "authorityInput is null");
            if (simulation == null) throw new LogicDefinitionException(
                ReplayAuthorityCodes.REPLAY_AUTHORITY_ARGUMENT_INVALID, "simulation is null");

            IReadOnlyList<LogicEvent> events = result != null && result.Events != null
                ? result.Events.EventsInSequenceOrder
                : (IReadOnlyList<LogicEvent>)System.Array.Empty<LogicEvent>();

            FrozenTickCommandSet set = simulation.LastCommandSet;

            // 第二步：登记"网关分配了序号"的命令（含 AI/System，登记 ≠ 记录事实）。
            int registered = 0;
            if (set != null)
            {
                for (int i = 0; i < set.Envelopes.Count; i++)
                {
                    authorityInput.RecordEnvelope(set.Envelopes[i]);
                    registered++;
                }
            }

            // 第三步：折叠本 Tick 的事件，得到每条命令在**本 Tick 的最终**处置
            // （处理器级拒绝只有在这里才可见）。
            int rejectedFolded = authorityInput.RecordTickEvents(tick, events);

            // 第四步：只对"未被拒"的命令记录接受事实，避免同一命令出现两种处置（双记）。
            var rejectedSequences = new HashSet<long>();
            if (set != null)
            {
                for (int i = 0; i < set.RejectedCommands.Count; i++)
                {
                    CommandRejectionRecord rejection = set.RejectedCommands[i];
                    if (rejection != null && rejection.Envelope != null)
                        rejectedSequences.Add(rejection.Envelope.CommandSequence);
                }
            }

            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] is CommandRejectedEvent rejected)
                    rejectedSequences.Add(rejected.CommandSequence);
            }

            int acceptedRecorded = 0;
            if (set != null)
            {
                for (int i = 0; i < set.Envelopes.Count; i++)
                {
                    CommandEnvelope envelope = set.Envelopes[i];
                    if (rejectedSequences.Contains(envelope.CommandSequence)) continue;
                    authorityInput.RecordAccepted(envelope);
                    acceptedRecorded++;
                }
            }

            return new ShadowAuthorityStepFacts(tick, registered, rejectedFolded, acceptedRecorded);
        }
    }

    /// <summary>一次收口的事实计数（只读诊断读数，不参与任何判定）。</summary>
    public readonly struct ShadowAuthorityStepFacts
    {
        public ShadowAuthorityStepFacts(long tick, int registeredEnvelopes, int rejectedFolded, int acceptedRecorded)
        {
            Tick = tick;
            RegisteredEnvelopes = registeredEnvelopes;
            RejectedFolded = rejectedFolded;
            AcceptedRecorded = acceptedRecorded;
        }

        public long Tick { get; }

        /// <summary>登记进"按序号解析来源"索引的 Envelope 条数（含 AI/System）。</summary>
        public int RegisteredEnvelopes { get; }

        /// <summary>被折叠进权威输入的命令拒绝事实条数（只含 Player）。</summary>
        public int RejectedFolded { get; }

        /// <summary>被记录为接受事实的命令条数（只含 Player，且本 Tick 未被拒）。</summary>
        public int AcceptedRecorded { get; }
    }
}
