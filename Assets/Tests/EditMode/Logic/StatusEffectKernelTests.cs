using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Status;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 04 必需测试：持续效果最小内核（实例身份、稳定排序键、半开区间到期、
    /// 遍历中增删的延迟队列、EffectId 唯一性与规范化快照）。
    /// </summary>
    public class StatusEffectKernelTests
    {
        private sealed class Host : IStatusEffectHost
        {
            public Host(long unitId, LogicEventOutbox outbox, int healthQ10 = 102400)
            {
                UnitId = new UnitId(unitId);
                StateMachine = new UnitStateMachine(UnitId, outbox);
                HealthQ10 = healthQ10;
            }

            public UnitId UnitId { get; }

            public UnitStateMachine StateMachine { get; }

            public int HealthQ10 { get; private set; }

            public int DamageApplications { get; private set; }

            public List<int> DamageHistory { get; } = new List<int>();

            public void ApplyDamageQ10(int damageQ10)
            {
                HealthQ10 -= damageQ10;
                DamageApplications++;
                DamageHistory.Add(damageQ10);
            }
        }

        private static BuffSystem NewSystem(out LogicEventOutbox outbox, LogicIdGenerator ids = null)
        {
            outbox = new LogicEventOutbox(new LogicSequenceGenerator());
            return new BuffSystem(ids ?? new LogicIdGenerator(), new LogicSequenceGenerator(), outbox);
        }

        private static StatusEffectSpecId Spec(string id) => new StatusEffectSpecId(id);

        // ——— 必需测试：到期 Tick 不额外触发 ———

        [Test]
        public void ExpiredEffectDoesNotTickAtEndTick()
        {
            BuffSystem buffs = NewSystem(out LogicEventOutbox outbox);
            var host = new Host(1L, outbox);
            var spec = new StatusEffectRuntimeSpec(
                Spec("status.burn"), 5, new DamageOverTimeEffectPayload(100));

            buffs.Apply(host, spec, 10L);
            Assert.That(buffs.ActiveCount, Is.EqualTo(1));
            Assert.That(buffs.ActiveEffectsOf(host.UnitId)[0].EndTick, Is.EqualTo(15L));

            // [10, 15)：Tick 11..14 各触发一次。
            for (long tick = 11L; tick <= 14L; tick++)
            {
                buffs.AdvanceOrdered(new IStatusEffectHost[] { host }, tick);
            }
            Assert.That(host.DamageApplications, Is.EqualTo(4), "半开区间内 11..14 共 4 次");
            Assert.That(host.HealthQ10, Is.EqualTo(102400 - 4 * 100));

            // 到期 Tick 15：先移除，**不**额外触发一次。
            var report = buffs.AdvanceOrdered(new IStatusEffectHost[] { host }, 15L);
            Assert.That(host.DamageApplications, Is.EqualTo(4), "到期 Tick 不得额外触发一次效果");
            Assert.That(report.ExpiredEffectIds.Count, Is.EqualTo(1));
            Assert.That(buffs.ActiveCount, Is.EqualTo(0));
            Assert.That(buffs.ActiveEffectsOf(host.UnitId), Is.Empty);

            // 事件：一次 Applied + 一次 Expired Removed。
            EventBatch batch = outbox.Flush(15L);
            var applied = Task04State.EventsOfType<StatusEffectAppliedEvent>(batch);
            var removed = Task04State.EventsOfType<StatusEffectRemovedEvent>(batch);
            Assert.That(applied.Count, Is.EqualTo(1), "施加事件在本批次内可见（施加发生在 Tick 10）");
            Assert.That(applied[0].AppliedAtTick, Is.EqualTo(10L));
            Assert.That(applied[0].DurationTicks, Is.EqualTo(5));
            Assert.That(applied[0].EndTick, Is.EqualTo(15L));
            Assert.That(removed.Count, Is.EqualTo(1));
            Assert.That(removed[0].Expired, Is.True, "到期移除必须显式标记为 Expired");
            Assert.That(removed[0].SpecId, Is.EqualTo(Spec("status.burn")));
            Assert.That(removed[0].EffectId, Is.EqualTo(applied[0].EffectId),
                "移除事件必须引用同一实例身份");
        }

        [Test]
        public void ApplyEmitsAppliedEventWithEffectIdentityAndInterval()
        {
            BuffSystem buffs = NewSystem(out LogicEventOutbox outbox);
            var host = new Host(2L, outbox);

            StatusEffectInstance instance = buffs.Apply(
                host, StatusEffectRuntimeSpec.Timed(Spec("status.guard"), 20), 7L);

            EventBatch batch = outbox.Flush(7L);
            var applied = Task04State.EventsOfType<StatusEffectAppliedEvent>(batch);
            Assert.That(applied.Count, Is.EqualTo(1));
            Assert.That(applied[0].EffectId, Is.EqualTo(instance.EffectId));
            Assert.That(applied[0].UnitId.Value, Is.EqualTo(2L));
            Assert.That(applied[0].AppliedAtTick, Is.EqualTo(7L));
            Assert.That(applied[0].DurationTicks, Is.EqualTo(20));
            Assert.That(applied[0].EndTick, Is.EqualTo(27L));
            Assert.That(applied[0].EffectSequence, Is.GreaterThanOrEqualTo(0L));
        }

        // ——— 必需测试：遍历中的增删被延迟且有序 ———

        [Test]
        public void EffectMutationDuringIterationIsDeferredAndOrdered()
        {
            LogicEventOutbox outbox;
            BuffSystem buffs = NewSystem(out outbox);
            var host = new MutatingHost(1L, outbox) { Buffs = buffs };

            // burn = 计时伤害（在 Tick 1 的载荷回调里对内核做增删）；guard = 长效果（遍历中被移除的目标）。
            StatusEffectInstance burn = buffs.Apply(host, new StatusEffectRuntimeSpec(
                Spec("status.burn"), 5, new DamageOverTimeEffectPayload(100)), 0L);
            StatusEffectInstance guard = buffs.Apply(host,
                StatusEffectRuntimeSpec.Timed(Spec("status.guard"), 50), 0L);
            Assert.That(buffs.ActiveCount, Is.EqualTo(2));
            Assert.That(buffs.HasPendingOperations, Is.False, "非遍历期的施加立即提交");

            host.RemoveTwiceEffectId = guard.EffectId;
            host.ApplyDuringIteration = StatusEffectRuntimeSpec.Timed(Spec("status.regen"), 50);
            outbox.Flush(0L);   // 丢弃 Tick 0 的施加事件，只观察本 Tick 的增删事实

            var report = buffs.AdvanceOrdered(new IStatusEffectHost[] { host }, 1L);

            // ① 回调期间：活动集合未被修改，增删只进延迟队列。
            Assert.That(host.DamageCallbacks, Is.EqualTo(1), "计时伤害必须在 [0,5) 内的 Tick 1 触发一次");
            Assert.That(host.HealthQ10, Is.EqualTo(102400 - 100));
            Assert.That(host.ActiveCountSeenDuringCallback, Is.EqualTo(2),
                "遍历期间活动集合不得被回调直接修改（增删只能进延迟队列）");
            Assert.That(host.PendingAfterFirstMutation, Is.True,
                "回调发起的移除必须进入延迟队列，而不是当场提交");
            Assert.That(host.ActiveCountAfterMutations, Is.EqualTo(2),
                "重复移除与新增都不得在遍历期间改变活动集合");
            Assert.That(host.DoubleRemoveAccepted, Is.True,
                "延迟期间对同一实例的重复移除请求仍被接受（提交时只生效一次）");
            Assert.That(host.AppliedDuringIteration, Is.Not.Null);
            Assert.That(host.AppliedDuringIteration.EffectId.IsValid, Is.True,
                "遍历中施加也必须立即给出确定的实例身份");

            // ② 遍历后：延迟队列原子排空，结果唯一。
            Assert.That(buffs.HasPendingOperations, Is.False, "遍历结束后延迟队列必须排空");
            IReadOnlyList<StatusEffectInstance> active = buffs.ActiveEffectsOf(host.UnitId);
            Assert.That(active.Count, Is.EqualTo(2), "burn 保留、guard 被移除、regen 被加入");
            var ids = new List<long>();
            for (int i = 0; i < active.Count; i++) ids.Add(active[i].EffectId.Value);
            Assert.That(ids, Does.Contain(burn.EffectId.Value));
            Assert.That(ids, Has.No.Member(guard.EffectId.Value), "移除必须真的生效");
            Assert.That(ids, Does.Contain(host.AppliedDuringIteration.EffectId.Value));

            Assert.That(report.ExplicitlyRemovedEffectIds.Count, Is.EqualTo(1),
                "重复移除请求只产生一条移除事实");
            Assert.That(report.AppliedEffectIds.Count, Is.EqualTo(1));

            EventBatch batch = outbox.Flush(1L);
            var removed = Task04State.EventsOfType<StatusEffectRemovedEvent>(batch);
            Assert.That(removed.Count, Is.EqualTo(1), "重复移除请求不得产生两条移除事件");
            Assert.That(removed[0].EffectId, Is.EqualTo(guard.EffectId));
            Assert.That(removed[0].Expired, Is.False, "显式移除不得被标成到期");

            var applied = Task04State.EventsOfType<StatusEffectAppliedEvent>(batch);
            Assert.That(applied.Count, Is.EqualTo(1), "遍历中施加也必须留下施加事件");
            Assert.That(applied[0].EffectId.Value, Is.EqualTo(host.AppliedDuringIteration.EffectId.Value));
            Assert.That(applied[0].Tick, Is.EqualTo(1L), "施加事件必须用本 Tick 发（不得静默丢失）");
        }

        /// <summary>
        /// 可在<strong>遍历中被回调</strong>的宿主：载荷触发时经宿主回调对效果内核发起增删，
        /// 用来验证"遍历中的增删进入延迟队列、遍历结束后原子应用"。
        /// </summary>
        private sealed class MutatingHost : IStatusEffectHost
        {
            public MutatingHost(long unitId, LogicEventOutbox outbox)
            {
                UnitId = new UnitId(unitId);
                StateMachine = new UnitStateMachine(UnitId, outbox);
            }

            public UnitId UnitId { get; }

            public UnitStateMachine StateMachine { get; }

            public int HealthQ10 { get; private set; } = 102400;

            public int DamageCallbacks { get; private set; }

            /// <summary>由用例回填：回调里要操作的内核。</summary>
            public BuffSystem Buffs;

            /// <summary>遍历中要重复移除的实例。</summary>
            public EffectId RemoveTwiceEffectId;

            /// <summary>遍历中要施加的新效果配置。</summary>
            public StatusEffectRuntimeSpec ApplyDuringIteration;

            public int ActiveCountSeenDuringCallback { get; private set; } = -1;

            public int ActiveCountAfterMutations { get; private set; } = -1;

            public bool PendingAfterFirstMutation { get; private set; }

            public bool DoubleRemoveAccepted { get; private set; }

            public StatusEffectInstance AppliedDuringIteration { get; private set; }

            public void ApplyDamageQ10(int damageQ10)
            {
                DamageCallbacks++;
                HealthQ10 -= damageQ10;
                if (Buffs == null) return;

                // 遍历中：活动集合不得被回调直接修改；增删一律进延迟队列。
                ActiveCountSeenDuringCallback = Buffs.ActiveEffectsOf(UnitId).Count;
                bool firstRemove = Buffs.Remove(UnitId, RemoveTwiceEffectId);
                PendingAfterFirstMutation = Buffs.HasPendingOperations;
                bool secondRemove = Buffs.Remove(UnitId, RemoveTwiceEffectId);
                AppliedDuringIteration = Buffs.Apply(this, ApplyDuringIteration, 1L);
                ActiveCountAfterMutations = Buffs.ActiveEffectsOf(UnitId).Count;
                DoubleRemoveAccepted = firstRemove && secondRemove;
            }
        }

        [Test]
        public void InsertionOrderDoesNotChangeOutcomeOrEventSequence()
        {
            // 同一组效果、两种**全局插入交错顺序** ⇒ 每个单位内部的推进顺序与事件序列必须一致。
            string contiguous = RunInterleaving(interleaved: false);
            string interleaved = RunInterleaving(interleaved: true);
            Assert.That(interleaved, Is.EqualTo(contiguous),
                "推进顺序由 (AppliedAtTick, EffectSequence) 决定，全局插入交错不得改变结果与事件序列");
        }

        /// <summary>
        /// 两个单位各施加三个同 Tick 效果。<c>interleaved = false</c> 时逐单位连续施加，
        /// <c>interleaved = true</c> 时两个单位的施加互相交错（全局 EffectSequence 因此不同）。
        ///
        /// 观察量只记录<strong>每个单位内部的配置顺序</strong>与按 <c>UnitId</c> 的事件顺序：
        /// 绝对序号必然随全局交错变化，所以"序号逐一相同"是错误的不变量；
        /// 正确的是"顺序由冻结排序键决定，与插入顺序、宿主传入顺序都无关"。
        /// </summary>
        private static string RunInterleaving(bool interleaved)
        {
            LogicEventOutbox outbox;
            BuffSystem buffs = NewSystem(out outbox);
            var unit1 = new Host(1L, outbox);
            var unit2 = new Host(2L, outbox);

            string[] first = { "status.a", "status.b", "status.c" };
            string[] second = { "status.x", "status.y", "status.z" };

            if (interleaved)
            {
                for (int i = 0; i < first.Length; i++)
                {
                    buffs.Apply(unit1, StatusEffectRuntimeSpec.Timed(Spec(first[i]), 10), 3L);
                    buffs.Apply(unit2, StatusEffectRuntimeSpec.Timed(Spec(second[i]), 10), 3L);
                }
            }
            else
            {
                for (int i = 0; i < first.Length; i++)
                    buffs.Apply(unit1, StatusEffectRuntimeSpec.Timed(Spec(first[i]), 10), 3L);
                for (int i = 0; i < second.Length; i++)
                    buffs.Apply(unit2, StatusEffectRuntimeSpec.Timed(Spec(second[i]), 10), 3L);
            }

            var builder = new System.Text.StringBuilder();
            AppendActiveSpecOrder(builder, buffs, unit1);
            AppendActiveSpecOrder(builder, buffs, unit2);

            // 故意以与 UnitId 相反的顺序传入宿主：推进与报告顺序仍必须按 UnitId 升序。
            buffs.AdvanceOrdered(new IStatusEffectHost[] { unit2, unit1 }, 20L);

            EventBatch batch = outbox.Flush(20L);
            var removed = Task04State.EventsOfType<StatusEffectRemovedEvent>(batch);
            builder.Append("events:");
            for (int i = 0; i < removed.Count; i++)
            {
                builder.Append(removed[i].UnitId.Value).Append(':')
                       .Append(removed[i].SpecId.Value).Append(';');
            }
            return builder.ToString();
        }

        /// <summary>
        /// 追加某单位活动集合的配置顺序，并顺带断言它严格按冻结排序键升序
        /// ——顺序来自键，而不是容器插入顺序。
        /// </summary>
        private static void AppendActiveSpecOrder(System.Text.StringBuilder builder, BuffSystem buffs, Host host)
        {
            IReadOnlyList<StatusEffectInstance> active = buffs.ActiveEffectsOf(host.UnitId);
            builder.Append("unit").Append(host.UnitId.Value).Append(':');
            for (int i = 0; i < active.Count; i++)
            {
                if (i > 0)
                {
                    bool ascending = active[i - 1].AppliedAtTick < active[i].AppliedAtTick
                        || (active[i - 1].AppliedAtTick == active[i].AppliedAtTick
                            && active[i - 1].EffectSequence < active[i].EffectSequence);
                    Assert.That(ascending, Is.True,
                        "活动集合必须按 (AppliedAtTick, EffectSequence) 升序，而不是插入顺序："
                        + active[i - 1] + " -> " + active[i]);
                }

                builder.Append(active[i].SpecId.Value).Append(';');
            }
        }

        [Test]
        public void EffectUnitsAreAdvancedInUnitIdOrder()
        {
            LogicEventOutbox outbox;
            BuffSystem buffs = NewSystem(out outbox);
            var host3 = new Host(3L, outbox);
            var host1 = new Host(1L, outbox);
            var host2 = new Host(2L, outbox);

            // 故意按非 UnitId 顺序施加。
            StatusEffectInstance onHost3 = buffs.Apply(host3, StatusEffectRuntimeSpec.Timed(Spec("status.x"), 5), 0L);
            StatusEffectInstance onHost1 = buffs.Apply(host1, StatusEffectRuntimeSpec.Timed(Spec("status.x"), 5), 0L);
            StatusEffectInstance onHost2 = buffs.Apply(host2, StatusEffectRuntimeSpec.Timed(Spec("status.x"), 5), 0L);

            Assert.That(buffs.UnitIdsWithEffects, Is.EqualTo(new[] { 1L, 2L, 3L }),
                "持有效果的单位必须按 UnitId 升序报告");
            Assert.That(onHost3.EffectId.Value, Is.LessThan(onHost1.EffectId.Value),
                "对照证据：EffectId 按**施加顺序**分配，因此它与 UnitId 顺序不同——"
                + "这正是下面断言具备鉴别力的前提");

            // 到期：报告中的到期 ID 顺序必须按 (UnitId, AppliedAtTick, EffectSequence)，
            // 而不是按施加顺序，也不是按传入的宿主数组顺序。
            var report = buffs.AdvanceOrdered(new IStatusEffectHost[] { host2, host3, host1 }, 5L);
            Assert.That(report.ExpiredEffectIds.Count, Is.EqualTo(3));
            long[] expired = new long[report.ExpiredEffectIds.Count];
            for (int i = 0; i < expired.Length; i++) expired[i] = report.ExpiredEffectIds[i].Value;
            Assert.That(expired, Is.EqualTo(new[]
                {
                    onHost1.EffectId.Value, onHost2.EffectId.Value, onHost3.EffectId.Value
                }),
                "跨单位推进与到期报告必须按 UnitId 升序");
        }

        // ——— 必需测试：EffectId 唯一 ———

        [Test]
        public void EffectIdsAreUniqueAndMonotonicAcrossUnitsAndSpecs()
        {
            LogicEventOutbox outbox;
            var ids = new LogicIdGenerator();
            BuffSystem buffs = NewSystem(out outbox, ids);
            var host1 = new Host(1L, outbox);
            var host2 = new Host(2L, outbox);

            var seen = new HashSet<long>();
            for (int i = 0; i < 5; i++)
            {
                StatusEffectInstance a = buffs.Apply(host1, StatusEffectRuntimeSpec.Timed(Spec("status.a"), 3), 0L);
                StatusEffectInstance b = buffs.Apply(host2, StatusEffectRuntimeSpec.Timed(Spec("status.b"), 3), 0L);
                Assert.That(seen.Add(a.EffectId.Value), Is.True, "EffectId 必须唯一：" + a.EffectId.Value);
                Assert.That(seen.Add(b.EffectId.Value), Is.True, "EffectId 必须唯一：" + b.EffectId.Value);
                Assert.That(b.EffectId.Value, Is.EqualTo(a.EffectId.Value + 1L), "EffectId 必须单调递增");
            }

            Assert.That(buffs.ActiveCount, Is.EqualTo(10));
            Assert.That(ids.NextEffectIdValue, Is.EqualTo(11L),
                "下一个 EffectId 必须进入规范化快照（快照侧断言见 "
                + "Task04LifecycleTests.EffectIdIsUniqueAndAppearsInCanonicalSnapshot）");
        }

        [Test]
        public void SeparateInstancesOfSameSpecAreIndependentlyRemovable()
        {
            LogicEventOutbox outbox;
            BuffSystem buffs = NewSystem(out outbox);
            var host = new Host(1L, outbox);

            StatusEffectInstance first = buffs.Apply(host, StatusEffectRuntimeSpec.Timed(Spec("status.regen"), 10), 0L);
            StatusEffectInstance second = buffs.Apply(host, StatusEffectRuntimeSpec.Timed(Spec("status.regen"), 10), 0L);

            Assert.That(first.SpecId, Is.EqualTo(second.SpecId), "同一配置可被多次施加");
            Assert.That(first.EffectId, Is.Not.EqualTo(second.EffectId), "但实例身份不同");

            buffs.Remove(host.UnitId, first.EffectId);
            buffs.AdvanceOrdered(new IStatusEffectHost[] { host }, 1L);

            IReadOnlyList<StatusEffectInstance> remaining = buffs.ActiveEffectsOf(host.UnitId);
            Assert.That(remaining.Count, Is.EqualTo(1));
            Assert.That(remaining[0].EffectId, Is.EqualTo(second.EffectId),
                "移除一个实例不得影响同配置的另一个实例");
        }

        // ——— 规格校验 ———

        [Test]
        public void EffectSpecRejectsInvalidDurationPayloadOrSpecId()
        {
            Assert.That(StatusEffectRuntimeSpec.Timed(Spec("status.a"), 0).Validate(),
                Is.EqualTo(StatusEffectRuntimeSpec.EFFECT_SPEC_DURATION_INVALID));
            Assert.That(StatusEffectRuntimeSpec.Timed(Spec("status.a"), -1).Validate(),
                Is.EqualTo(StatusEffectRuntimeSpec.EFFECT_SPEC_DURATION_INVALID));
            Assert.That(new StatusEffectRuntimeSpec(Spec(""), 5, NoOpEffectPayload.Instance).Validate(),
                Is.EqualTo(StatusEffectRuntimeSpec.EFFECT_SPEC_ID_INVALID));
            Assert.That(new StatusEffectRuntimeSpec(Spec("status.a"), 5, null).Validate(),
                Is.EqualTo(StatusEffectRuntimeSpec.EFFECT_SPEC_PAYLOAD_MISSING));
            Assert.That(new DamageOverTimeEffectPayload(0).Validate(),
                Is.EqualTo(DamageOverTimeEffectPayload.EFFECT_DOT_DAMAGE_INVALID));

            LogicEventOutbox outbox;
            BuffSystem buffs = NewSystem(out outbox);
            var host = new Host(1L, outbox);
            Assert.Throws<LogicDefinitionException>(() =>
                buffs.Apply(host, new StatusEffectRuntimeSpec(Spec("status.a"), 0, NoOpEffectPayload.Instance), 0L));
        }
    }
}
