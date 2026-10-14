using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Commands
{
    /// <summary>已获得 <c>CommandSequence</c> 但被授权层拒绝的命令。</summary>
    public sealed record CommandRejectionRecord(CommandEnvelope Envelope, string ReasonCode);

    /// <summary>
    /// 一个 Tick 的规范命令集合（只读）：已分配序号且可处理的 Envelope、
    /// 被授权层拒绝的已编号命令、以及从未获得序号的入口级拒绝。
    /// </summary>
    public sealed class FrozenTickCommandSet
    {
        internal FrozenTickCommandSet(
            long tick,
            IReadOnlyList<CommandEnvelope> envelopes,
            IReadOnlyList<CommandRejectionRecord> rejectedCommands,
            IReadOnlyList<CommandIngressRejection> ingressRejections)
        {
            Tick = tick;
            Envelopes = envelopes;
            RejectedCommands = rejectedCommands;
            IngressRejections = ingressRejections;
        }

        public long Tick { get; }

        /// <summary>严格按 <c>SourcePriority -&gt; ControllerId(Ordinal) -&gt; ProducerOrdinal</c> 排序。</summary>
        public IReadOnlyList<CommandEnvelope> Envelopes { get; }

        public IReadOnlyList<CommandRejectionRecord> RejectedCommands { get; }

        /// <summary>按组键排序；每个非法规范键恰好一条。</summary>
        public IReadOnlyList<CommandIngressRejection> IngressRejections { get; }
    }

    /// <summary>
    /// 任务 09 接入点：基于<strong>权威控制映射</strong>的载荷授权
    /// （例如"该 Controller 是否真的控制这条命令声称的单位/窗口"）。
    ///
    /// 它发生在入口身份校验<strong>之后</strong>、命令处理器<strong>之前</strong>：
    /// 命令此时已经拥有唯一的 <c>CommandSequence</c>，因此拒绝是
    /// <see cref="CommandRejectionRecord"/>（处理器级），而不是入口级拒绝。
    /// </summary>
    public interface ICommandPayloadAuthorizer
    {
        /// <summary>返回 null 表示通过；否则返回稳定拒绝码。</summary>
        string Authorize(
            CommandEnvelope envelope,
            IReadOnlyDictionary<ControllerId, IReadOnlyList<UnitId>> controllerToUnitIds);
    }

    /// <summary>
    /// 任务 03 默认授权器：不做权威控制映射检查（任务 09 用真实实现替换）。
    /// 它<strong>不</strong>放宽任何入口身份规则，也不允许生产者自报身份。
    /// </summary>
    public sealed class PermissivePayloadAuthorizer : ICommandPayloadAuthorizer
    {
        public static readonly PermissivePayloadAuthorizer Instance = new PermissivePayloadAuthorizer();

        public string Authorize(CommandEnvelope envelope, IReadOnlyDictionary<ControllerId, IReadOnlyList<UnitId>> controllerToUnitIds)
            => null;
    }

    public sealed class CommandGateway
    {
        private struct KeyedFact
        {
            public int Priority;
            public string Controller;
            public long Ordinal;
            public int OriginalIndex;
            public SourcedCommandRequest Fact;
        }

        private sealed class KeyedFactComparer : IComparer<KeyedFact>
        {
            public static readonly KeyedFactComparer Instance = new KeyedFactComparer();

            public int Compare(KeyedFact a, KeyedFact b)
            {
                int byPriority = a.Priority.CompareTo(b.Priority);
                if (byPriority != 0) return byPriority;
                int byController = string.CompareOrdinal(a.Controller, b.Controller);
                if (byController != 0) return byController;
                int byOrdinal = a.Ordinal.CompareTo(b.Ordinal);
                return byOrdinal != 0 ? byOrdinal : a.OriginalIndex.CompareTo(b.OriginalIndex);
            }
        }

        /// <summary>
        /// 规范键相等性：<c>SourcePriority -&gt; ControllerId(Ordinal) -&gt; ProducerOrdinal</c>。
        /// 原始枚举序号<strong>不</strong>参与相等性，因此同一键的两条事实必然碰撞
        /// （排序时的 OriginalIndex 只是稳定排序的末级平局键，不是身份的一部分）。
        /// </summary>
        private static bool SameCanonicalKey(KeyedFact a, KeyedFact b)
            => a.Priority == b.Priority &&
               string.CompareOrdinal(a.Controller, b.Controller) == 0 &&
               a.Ordinal == b.Ordinal;

        private readonly LogicSequenceGenerator _sequences;
        private readonly ICommandPayloadAuthorizer _authorizer;

        public CommandGateway(LogicSequenceGenerator sequences, ICommandPayloadAuthorizer authorizer = null)
        {
            _sequences = sequences ?? throw new ArgumentNullException(nameof(sequences));
            _authorizer = authorizer ?? PermissivePayloadAuthorizer.Instance;
        }

        public long NextCommandSequence => _sequences.NextCommandSequence;

        /// <summary>
        /// 冻结批次 → 规范命令集合。<paramref name="pendingIngressRejections"/>
        /// 是入口在冻结/投递阶段已经记下的拒绝（例如迟到请求），会被并入并按组键排序。
        /// </summary>
        public FrozenTickCommandSet NormalizeAndAssignSequence(
            FrozenCommandBatch batch,
            long tick,
            IReadOnlyDictionary<ControllerId, IReadOnlyList<UnitId>> controllerToUnitIds,
            IReadOnlyList<CommandIngressRejection> pendingIngressRejections = null)
        {
            if (batch == null)
                throw new ProjectHero.Logic.LogicDefinitionException(CommandCodes.COMMAND_BATCH_TICK_MISMATCH, "batch is null");

            if (batch.TargetTick != tick)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_BATCH_TICK_MISMATCH,
                    "batch.TargetTick=" + batch.TargetTick.ToString(CultureInfo.InvariantCulture) +
                    " step.Tick=" + tick.ToString(CultureInfo.InvariantCulture));

            var keyed = new List<KeyedFact>(batch.Requests.Count);
            for (int i = 0; i < batch.Requests.Count; i++)
            {
                SourcedCommandRequest fact = batch.Requests[i];
                if (fact == null || fact.Request == null)
                    throw new ProjectHero.Logic.LogicDefinitionException(CommandCodes.COMMAND_REQUEST_NULL, "frozen batch member");

                if (fact.Request.TargetTick != tick)
                    throw new ProjectHero.Logic.LogicDefinitionException(
                        CommandCodes.COMMAND_BATCH_TICK_MISMATCH,
                        "request.TargetTick=" + fact.Request.TargetTick.ToString(CultureInfo.InvariantCulture) +
                        " step.Tick=" + tick.ToString(CultureInfo.InvariantCulture));

                keyed.Add(new KeyedFact
                {
                    Priority = fact.SourcePriority,
                    Controller = fact.ControllerId.Value ?? string.Empty,
                    Ordinal = fact.ProducerOrdinal,
                    OriginalIndex = i,
                    Fact = fact
                });
            }

            keyed.Sort(KeyedFactComparer.Instance);

            var rejections = new List<CommandIngressRejection>();
            if (pendingIngressRejections != null)
            {
                for (int i = 0; i < pendingIngressRejections.Count; i++)
                {
                    if (pendingIngressRejections[i] != null) rejections.Add(pendingIngressRejections[i]);
                }
            }

            var accepted = new List<SourcedCommandRequest>(keyed.Count);
            int index = 0;
            while (index < keyed.Count)
            {
                int groupEnd = index + 1;
                while (groupEnd < keyed.Count && SameCanonicalKey(keyed[index], keyed[groupEnd]))
                {
                    groupEnd++;
                }

                int collisionCount = groupEnd - index;
                KeyedFact first = keyed[index];

                if (collisionCount > 1)
                {
                    // 重复规范键：整个碰撞组没有赢家。
                    rejections.Add(new CommandIngressRejection(
                        first.Fact.ControllerId,
                        first.Fact.SourceKind,
                        first.Priority,
                        first.Ordinal,
                        tick,
                        CommandCodes.DUPLICATE_COMMAND_ORDINAL,
                        collisionCount));
                }
                else if (first.Ordinal <= first.Fact.FrozenProducerOrdinalWatermarkBefore)
                {
                    rejections.Add(new CommandIngressRejection(
                        first.Fact.ControllerId,
                        first.Fact.SourceKind,
                        first.Priority,
                        first.Ordinal,
                        tick,
                        CommandCodes.COMMAND_ORDINAL_REGRESSION,
                        1));
                }
                else
                {
                    string structuralError = first.Fact.Request.ValidateStaticStructure();
                    if (structuralError != null)
                    {
                        rejections.Add(new CommandIngressRejection(
                            first.Fact.ControllerId,
                            first.Fact.SourceKind,
                            first.Priority,
                            first.Ordinal,
                            tick,
                            structuralError,
                            1));
                    }
                    else
                    {
                        accepted.Add(first.Fact);
                    }
                }

                index = groupEnd;
            }

            rejections.Sort((a, b) => a.CompareGroupKeyTo(b));

            // 只有通过入口校验的请求按规范顺序获得整场唯一序号。
            var envelopes = new List<CommandEnvelope>(accepted.Count);
            var rejectedCommands = new List<CommandRejectionRecord>();
            for (int i = 0; i < accepted.Count; i++)
            {
                SourcedCommandRequest fact = accepted[i];
                var envelope = new CommandEnvelope(
                    _sequences.TakeCommandSequence(),
                    tick,
                    fact.ControllerId,
                    fact.SourceKind,
                    fact.SourcePriority,
                    fact.ProducerOrdinal,
                    fact.Request);

                string authorization = _authorizer.Authorize(envelope, controllerToUnitIds);
                if (authorization != null) rejectedCommands.Add(new CommandRejectionRecord(envelope, authorization));
                else envelopes.Add(envelope);
            }

            return new FrozenTickCommandSet(tick, envelopes, rejectedCommands, rejections);
        }
    }
}
