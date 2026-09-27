using System;
using System.Collections.Generic;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Status
{
    /// <summary>
    /// 效果内核的稳定失败码与稳定排序键说明。
    /// </summary>
    public static class BuffSystemCodes
    {
        public const string EFFECT_TARGET_UNKNOWN = "EFFECT_TARGET_UNKNOWN";
        public const string EFFECT_SPEC_INVALID = "EFFECT_SPEC_INVALID";
        public const string EFFECT_ID_NOT_UNIQUE = "EFFECT_ID_NOT_UNIQUE";
        public const string EFFECT_ID_INVALID = "EFFECT_ID_INVALID";
        public const string EFFECT_DEFERRED_OPERATION_UNKNOWN = "EFFECT_DEFERRED_OPERATION_UNKNOWN";
    }

    /// <summary>
    /// 效果内核访问宿主单位的<strong>唯一</strong>受控通道（任务 04「必须产出」5）。
    ///
    /// 为什么需要它：效果载荷不得持有可变单位引用直接写入（那会让"遍历中修改活动集合"
    /// 与"绕过统一入口写状态"同时成为可能）。载荷描述的改动经本接口转交，
    /// 由系统在阶段 1 内按稳定顺序调用。
    /// </summary>
    public interface IStatusEffectHost
    {
        UnitId UnitId { get; }

        /// <summary>宿主单位当前状态机（主动效果要改状态时经它走统一转换入口）。</summary>
        UnitStateMachine StateMachine { get; }

        /// <summary>当前生命（Q10 整数域，与 <c>UnitSnapshot.HealthQ10</c> 同域）。</summary>
        int HealthQ10 { get; }

        /// <summary>施加一次 Q10 伤害（由系统在阶段 1 内调用；不做任何表现副作用）。</summary>
        void ApplyDamageQ10(int damageQ10);
    }

    /// <summary>
    /// 一个可独立移除/到期的持续效果<strong>实例</strong>（任务 04）。
    ///
    /// 身份与生命周期的边界：
    /// <list type="bullet">
    /// <item><see cref="EffectId"/> 唯一（由 <c>LogicIdGenerator</c> 分配），进入规范化快照。</item>
    /// <item><see cref="SpecId"/> 是配置类型；同一配置可以被多次施加。</item>
    /// <item>持续时间使用半开区间 <c>[AppliedAtTick, AppliedAtTick + DurationTicks)</c>：
    /// <see cref="IsExpiredAt"/> 在到期 Tick 即为 true，因此到期 Tick <strong>先移除</strong>，
    /// 不再额外触发一次效果 Tick。</item>
    /// </list>
    ///
    /// <see cref="EndTick"/> 是权威结束边界：任务 05 的启动门禁/排程只读它。
    /// </summary>
    public sealed class StatusEffectInstance
    {
        internal StatusEffectInstance(
            EffectId effectId,
            StatusEffectRuntimeSpec spec,
            UnitId unitId,
            long appliedAtTick,
            long effectSequence)
        {
            if (!effectId.IsValid)
                throw new LogicDefinitionException(BuffSystemCodes.EFFECT_ID_INVALID, effectId.Value.ToString());
            if (spec == null)
                throw new LogicDefinitionException(BuffSystemCodes.EFFECT_SPEC_INVALID, "spec is null");
            string specError = spec.Validate();
            if (specError != null)
                throw new LogicDefinitionException(BuffSystemCodes.EFFECT_SPEC_INVALID, specError + "|" + spec.SpecId);

            EffectId = effectId;
            Spec = spec;
            UnitId = unitId;
            AppliedAtTick = appliedAtTick;
            EffectSequence = effectSequence;
            EndTick = checked(appliedAtTick + spec.DurationTicks);
        }

        /// <summary>实例身份（唯一，进入规范化快照）。</summary>
        public EffectId EffectId { get; }

        /// <summary>配置身份（同一配置可多次施加）。</summary>
        public StatusEffectSpecId SpecId => Spec.SpecId;

        /// <summary>配置类型本体（最小内核；完整配置扩展留到第六阶段）。</summary>
        public StatusEffectRuntimeSpec Spec { get; }

        public UnitId UnitId { get; }

        /// <summary>施加 Tick（排序键第一段）。</summary>
        public long AppliedAtTick { get; }

        /// <summary>整场唯一的效果序号（排序键第二段；与 EffectId 同源分配顺序）。</summary>
        public long EffectSequence { get; }

        public int DurationTicks => Spec.DurationTicks;

        /// <summary>权威结束边界（半开区间右端）。</summary>
        public long EndTick { get; }

        /// <summary>是否已在 <paramref name="tick"/> 到期（半开区间：到期 Tick 即为 true）。</summary>
        public bool IsExpiredAt(long tick) => tick >= EndTick;

        public override string ToString()
            => SpecId + "#" + EffectId.Value + "@" + UnitId.Value
               + " applied=" + AppliedAtTick + " end=" + EndTick;
    }

    /// <summary>
    /// 一次效果推进（阶段 1）的只读报告：到期移除、显式移除、实际触发 Tick 的实例。
    /// 它让"到期 Tick 不额外触发"与"遍历中的增删被延迟"都可被直接断言。
    /// </summary>
    public sealed class EffectAdvanceReport
    {
        internal readonly List<EffectId> ExpiredIds = new List<EffectId>();
        internal readonly List<EffectId> RemovedIds = new List<EffectId>();
        internal readonly List<EffectId> TickedIds = new List<EffectId>();
        internal readonly List<EffectId> AppliedIds = new List<EffectId>();

        /// <summary>本 Tick 因到期被移除的效果（按稳定键升序）。</summary>
        public IReadOnlyList<EffectId> ExpiredEffectIds => ExpiredIds;

        /// <summary>本 Tick 被显式移除的效果（按稳定键升序）。</summary>
        public IReadOnlyList<EffectId> ExplicitlyRemovedEffectIds => RemovedIds;

        /// <summary>本 Tick 真正触发了效果 Tick 的实例（不含到期者）。</summary>
        public IReadOnlyList<EffectId> TickedEffectIds => TickedIds;

        /// <summary>本 Tick 新施加的效果（延迟队列中已提交的部分）。</summary>
        public IReadOnlyList<EffectId> AppliedEffectIds => AppliedIds;

        public override string ToString()
            => "expired=" + ExpiredIds.Count + " removed=" + RemovedIds.Count
               + " ticked=" + TickedIds.Count + " applied=" + AppliedIds.Count;
    }

    /// <summary>
    /// 持续效果最小内核（任务 04「必须产出」5；主方案 3.10）。
    ///
    /// 冻结契约：
    /// <list type="number">
    /// <item><strong>实例身份</strong>：每个可独立移除/到期的实例拥有唯一 <c>EffectId</c>；
    /// 配置类型使用 <c>StatusEffectSpecId</c>。</item>
    /// <item><strong>稳定推进顺序</strong>：按 <c>AppliedAtTick -&gt; EffectSequence</c>；同 Tick 同序号
    /// （实际不可能，二者同源分配）再按 <c>EffectId</c> 兜底。插入顺序不改变结果与事件序列。</item>
    /// <item><strong>半开区间</strong>：<c>[AppliedAtTick, AppliedAtTick + DurationTicks)</c>；
    /// 到期 Tick <strong>先移除</strong>，不额外触发一次效果。</item>
    /// <item><strong>延迟队列</strong>：遍历中的新增/删除一律进入延迟队列，遍历完成后按
    /// 稳定键原子应用；效果回调<strong>从不</strong>直接修改活动集合。</item>
    /// <item><strong>单位顺序</strong>：跨单位遍历按 <c>UnitId</c> 升序。</item>
    /// </list>
    ///
    /// 本内核是后续任务<strong>唯一</strong>的效果推进器：任务 05–11 只能扩展
    /// <see cref="StatusEffectPayload"/> 与配置侧，不得建立第二套推进器。
    /// </summary>
    public sealed class BuffSystem
    {
        private enum OperationKind
        {
            Apply = 0,
            Remove = 1
        }

        private readonly struct PendingOperation
        {
            public PendingOperation(OperationKind kind, UnitId unitId, StatusEffectInstance effect, string reasonCode)
            {
                Kind = kind;
                UnitId = unitId;
                Effect = effect;
                ReasonCode = reasonCode;
            }

            public OperationKind Kind { get; }

            public UnitId UnitId { get; }

            public StatusEffectInstance Effect { get; }

            /// <summary>显式移除的稳定原因码（到期移除由系统自己填）。</summary>
            public string ReasonCode { get; }
        }

        private readonly LogicIdGenerator _ids;
        private readonly LogicSequenceGenerator _sequences;
        private readonly LogicEventOutbox _outbox;
        private readonly Dictionary<long, List<StatusEffectInstance>> _effectsByUnit =
            new Dictionary<long, List<StatusEffectInstance>>();
        private readonly Dictionary<long, StatusEffectInstance> _effectsById =
            new Dictionary<long, StatusEffectInstance>();
        private readonly List<PendingOperation> _pending = new List<PendingOperation>();
        private readonly List<long> _orderedUnitIds = new List<long>();
        private readonly EffectAdvanceReport _lastAdvanceReport = new EffectAdvanceReport();

        private bool _iterating;

        /// <summary>
        /// 最近一次推进的 Tick。延迟队列在<strong>非遍历期</strong>被排空时
        /// （例如阶段 1 的扩展点施加效果）必须用当前 Tick 发事件，
        /// 否则"施加"这个事实会<strong>没有任何事件</strong>——那是静默丢失，不是延迟。
        /// </summary>
        private long _currentTick = -1L;

        public BuffSystem(LogicIdGenerator ids, LogicSequenceGenerator sequences, LogicEventOutbox outbox)
        {
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
            _sequences = sequences ?? throw new ArgumentNullException(nameof(sequences));
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        }

        /// <summary>当前活动效果实例总数（只读诊断；权威活动集合就是各单位的列表）。</summary>
        public int ActiveCount => _effectsById.Count;

        /// <summary>是否存在尚未应用的延迟操作（遍历中产生的增删）。</summary>
        public bool HasPendingOperations => _pending.Count > 0;

        /// <summary>最近一次 <see cref="AdvanceOrdered"/> 的只读报告。</summary>
        public EffectAdvanceReport LastAdvanceReport => _lastAdvanceReport;

        /// <summary>
        /// 施加一个效果实例。
        ///
        /// <strong>遍历中调用</strong>（即由某个效果 Tick 回调触发）时进入延迟队列，
        /// 在本次遍历结束后按稳定键原子应用；返回值仍然立即给出最终
        /// <see cref="EffectId"/>，因此调用方（与事件）看到的身份是确定的。
        ///
        /// <strong>非遍历期</strong>（阶段 1 的扩展点、显式施加）立即提交，并且
        /// <strong>以本方法收到的 <paramref name="currentTick"/> 发事件</strong>——
        /// 首次推进之前就施加的效果同样必须留下"施加"事件，不能静默丢失。
        /// </summary>
        public StatusEffectInstance Apply(IStatusEffectHost host, StatusEffectRuntimeSpec spec, long currentTick)
        {
            if (host == null)
                throw new LogicDefinitionException(BuffSystemCodes.EFFECT_TARGET_UNKNOWN, "host is null");
            string specError = spec?.Validate();
            if (specError != null)
                throw new LogicDefinitionException(BuffSystemCodes.EFFECT_SPEC_INVALID, specError);

            // 施加事实自带 Tick：非遍历期的立即提交必须用它，而不是"上一次推进的 Tick"
            // （上一次推进可能不存在，那会让事件被静默丢弃）。
            _currentTick = currentTick;

            EffectId effectId = _ids.NextEffectId();
            long effectSequence = _sequences.TakeEffectSequence();
            var instance = new StatusEffectInstance(effectId, spec, host.UnitId, currentTick, effectSequence);

            Stage(new PendingOperation(OperationKind.Apply, host.UnitId, instance, null));
            return instance;
        }

        /// <summary>
        /// 显式移除一个效果实例。返回是否真的排入了移除操作
        /// （未登记 / 已移除的实例返回 false，不抛异常）。
        ///
        /// <strong>遍历中调用</strong>时进入延迟队列，在遍历结束后原子生效：
        /// 延迟期间对同一实例重复请求移除仍然返回 <c>true</c>（后置条件"该实例将被移除"成立），
        /// 但提交时只生效一次——只发一条移除事件、活动集合只被改动一次。
        /// </summary>
        public bool Remove(UnitId unitId, EffectId effectId)
        {
            if (!_effectsById.TryGetValue(effectId.Value, out StatusEffectInstance instance)) return false;
            Stage(new PendingOperation(OperationKind.Remove, unitId, instance, "EFFECT_REMOVED_EXPLICIT"));
            return true;
        }

        /// <summary>按稳定键读取某个单位当前活动的效果快照（只读；不修改任何状态）。</summary>
        public IReadOnlyList<StatusEffectInstance> ActiveEffectsOf(UnitId unitId)
        {
            if (!_effectsByUnit.TryGetValue(unitId.Value, out List<StatusEffectInstance> list))
                return Array.Empty<StatusEffectInstance>();

            var ordered = new List<StatusEffectInstance>(list);
            ordered.Sort(CompareStable);
            return ordered;
        }

        /// <summary>
        /// 阶段 1 的效果推进：<strong>严格按 <c>UnitId</c> 升序</strong>遍历宿主，
        /// 每个宿主内的活动实例按 <c>AppliedAtTick -&gt; EffectSequence</c> 推进。
        ///
        /// 到期实例<strong>先移除</strong>且不触发 <c>OnTick</c>；其余实例触发一次载荷 Tick。
        /// 遍历期间产生的任何增删进入延迟队列，遍历结束后原子应用——
        /// 因此在遍历过程中<strong>不可能</strong>修改正在被遍历的集合。
        /// </summary>
        public EffectAdvanceReport AdvanceOrdered(IReadOnlyList<IStatusEffectHost> hosts, long tick)
        {
            _currentTick = tick;
            var report = new EffectAdvanceReport();
            if (hosts == null || hosts.Count == 0)
            {
                DrainPending(report, tick);
                Publish(report);
                return report;
            }

            var ordered = new List<IStatusEffectHost>(hosts.Count);
            for (int i = 0; i < hosts.Count; i++)
            {
                if (hosts[i] != null) ordered.Add(hosts[i]);
            }
            ordered.Sort((a, b) => a.UnitId.Value.CompareTo(b.UnitId.Value));

            _iterating = true;
            try
            {
                for (int i = 0; i < ordered.Count; i++)
                {
                    AdvanceHostOrdered(ordered[i], tick, report);
                }
            }
            finally
            {
                _iterating = false;
            }

            DrainPending(report, tick);
            Publish(report);
            return report;
        }

        private void AdvanceHostOrdered(IStatusEffectHost host, long tick, EffectAdvanceReport report)
        {
            List<StatusEffectInstance> active = ActiveListOf(host.UnitId, create: false);
            if (active == null || active.Count == 0) return;

            // 快照遍历：遍历期间活动集合本身不被修改（增删只进延迟队列）。
            var ordered = new List<StatusEffectInstance>(active);
            ordered.Sort(CompareStable);

            for (int i = 0; i < ordered.Count; i++)
            {
                StatusEffectInstance instance = ordered[i];
                if (!_effectsById.ContainsKey(instance.EffectId.Value)) continue;

                if (instance.IsExpiredAt(tick))
                {
                    // 半开区间：到期 Tick 先移除，不额外触发一次效果。
                    report.ExpiredIds.Add(instance.EffectId);
                    Stage(new PendingOperation(OperationKind.Remove, host.UnitId, instance, "EFFECT_EXPIRED"));
                    continue;
                }

                ApplyPayloadOnTick(host, instance, tick);
                report.TickedIds.Add(instance.EffectId);
            }
        }

        /// <summary>载荷 Tick 的唯一分派点（新载荷类型必须在此显式加入，不得隐式跳过）。</summary>
        private static void ApplyPayloadOnTick(IStatusEffectHost host, StatusEffectInstance instance, long tick)
        {
            switch (instance.Spec.Payload)
            {
                case NoOpEffectPayload:
                    return;
                case DamageOverTimeEffectPayload damage:
                {
                    string error = damage.Validate();
                    if (error != null)
                        throw new LogicDefinitionException(BuffSystemCodes.EFFECT_SPEC_INVALID,
                            error + "|" + instance.SpecId);
                    host.ApplyDamageQ10(damage.DamagePerTickQ10);
                    return;
                }
                default:
                    throw new LogicDefinitionException(BuffSystemCodes.EFFECT_DEFERRED_OPERATION_UNKNOWN,
                        instance.Spec.Payload != null ? instance.Spec.Payload.Kind : "<null-payload>");
            }
        }

        private void Stage(PendingOperation operation)
        {
            // 无论是否处于遍历中，写入活动集合的动作都只发生在 DrainPending 里；
            // 这让"回调直接改集合"在结构上不可能，而不是靠调用方自觉。
            _pending.Add(operation);
            // 非遍历期排空：用当前 Tick 发事件（施加/移除都是本 Tick 已提交的事实）。
            if (!_iterating) DrainPending(_lastAdvanceReport, _currentTick);
        }

        private void DrainPending(EffectAdvanceReport report, long tick)
        {
            if (_pending.Count == 0) return;

            var batch = new List<PendingOperation>(_pending);
            _pending.Clear();
            batch.Sort(ComparePendingStable);

            for (int i = 0; i < batch.Count; i++)
            {
                PendingOperation operation = batch[i];
                switch (operation.Kind)
                {
                    case OperationKind.Apply:
                        CommitApply(operation, report, tick);
                        break;
                    case OperationKind.Remove:
                        CommitRemove(operation, report, tick);
                        break;
                    default:
                        throw new LogicDefinitionException(
                            BuffSystemCodes.EFFECT_DEFERRED_OPERATION_UNKNOWN, operation.Kind.ToString());
                }
            }
        }

        private void CommitApply(PendingOperation operation, EffectAdvanceReport report, long tick)
        {
            StatusEffectInstance instance = operation.Effect;
            if (_effectsById.ContainsKey(instance.EffectId.Value))
                throw new LogicDefinitionException(BuffSystemCodes.EFFECT_ID_NOT_UNIQUE, instance.EffectId.Value.ToString());

            ActiveListOf(instance.UnitId, create: true).Add(instance);
            _effectsById[instance.EffectId.Value] = instance;
            TrackUnitOrder(instance.UnitId);
            report.AppliedIds.Add(instance.EffectId);

            if (tick < 0L) return;
            EffectId effectId = instance.EffectId;
            UnitId unitId = instance.UnitId;
            StatusEffectSpecId specId = instance.SpecId;
            long appliedAt = instance.AppliedAtTick;
            long sequence = instance.EffectSequence;
            int duration = instance.DurationTicks;
            long endTick = instance.EndTick;
            _outbox.Emit(seq => new StatusEffectAppliedEvent(
                tick, seq, effectId, unitId, specId, appliedAt, sequence, duration, endTick));
        }

        private void CommitRemove(PendingOperation operation, EffectAdvanceReport report, long tick)
        {
            StatusEffectInstance instance = operation.Effect;
            if (!_effectsById.TryGetValue(instance.EffectId.Value, out StatusEffectInstance registered)) return;

            List<StatusEffectInstance> list = ActiveListOf(registered.UnitId, create: false);
            list?.Remove(registered);
            _effectsById.Remove(registered.EffectId.Value);

            bool expired = string.Equals(operation.ReasonCode, "EFFECT_EXPIRED", StringComparison.Ordinal);
            if (!expired) report.RemovedIds.Add(registered.EffectId);

            if (tick < 0L) return;
            EffectId effectId = registered.EffectId;
            UnitId unitId = registered.UnitId;
            StatusEffectSpecId specId = registered.SpecId;
            _outbox.Emit(seq => new StatusEffectRemovedEvent(tick, seq, effectId, unitId, specId, expired));
        }

        private void Publish(EffectAdvanceReport report)
        {
            _lastAdvanceReport.ExpiredIds.Clear();
            _lastAdvanceReport.ExpiredIds.AddRange(report.ExpiredIds);
            _lastAdvanceReport.RemovedIds.Clear();
            _lastAdvanceReport.RemovedIds.AddRange(report.RemovedIds);
            _lastAdvanceReport.TickedIds.Clear();
            _lastAdvanceReport.TickedIds.AddRange(report.TickedIds);
            _lastAdvanceReport.AppliedIds.Clear();
            _lastAdvanceReport.AppliedIds.AddRange(report.AppliedIds);
        }

        private List<StatusEffectInstance> ActiveListOf(UnitId unitId, bool create)
        {
            if (_effectsByUnit.TryGetValue(unitId.Value, out List<StatusEffectInstance> list)) return list;
            if (!create) return null;
            list = new List<StatusEffectInstance>();
            _effectsByUnit[unitId.Value] = list;
            return list;
        }

        private void TrackUnitOrder(UnitId unitId)
        {
            for (int i = 0; i < _orderedUnitIds.Count; i++)
            {
                if (_orderedUnitIds[i] == unitId.Value) return;
            }
            _orderedUnitIds.Add(unitId.Value);
        }

        /// <summary>当前持有活动效果的单位（<c>UnitId</c> 升序，只读诊断）。</summary>
        public IReadOnlyList<long> UnitIdsWithEffects
        {
            get
            {
                var ordered = new List<long>(_orderedUnitIds);
                ordered.Sort();
                return ordered;
            }
        }

        /// <summary>冻结的稳定排序键：<c>AppliedAtTick -&gt; EffectSequence -&gt; EffectId</c>。</summary>
        private static int CompareStable(StatusEffectInstance a, StatusEffectInstance b)
        {
            int byApplied = a.AppliedAtTick.CompareTo(b.AppliedAtTick);
            if (byApplied != 0) return byApplied;
            int bySequence = a.EffectSequence.CompareTo(b.EffectSequence);
            if (bySequence != 0) return bySequence;
            return a.EffectId.Value.CompareTo(b.EffectId.Value);
        }

        /// <summary>
        /// 延迟队列的原子应用顺序：先按单位，再按冻结排序键，最后把移除排在施加之后
        /// （同 Tick 同单位内"先施加后移除"与"先移除后施加"必须产生同一最终集合与同一事件序列，
        /// 因此这里给出唯一规范顺序）。
        /// </summary>
        private static int ComparePendingStable(PendingOperation a, PendingOperation b)
        {
            int byUnit = a.UnitId.Value.CompareTo(b.UnitId.Value);
            if (byUnit != 0) return byUnit;
            int byKind = ((int)a.Kind).CompareTo((int)b.Kind);
            if (byKind != 0) return byKind;
            return CompareStable(a.Effect, b.Effect);
        }
    }
}
