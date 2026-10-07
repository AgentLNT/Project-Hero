using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 09 A 流：<strong>生产者可见面与规范顺序不变量</strong>
    /// （任务包「必须产出」1/3 与「核心命令语义」）。
    ///
    /// 逐条对应必需测试：<c>ProducerCannotChooseSourcePriorityOrCommandSequence</c>、
    /// <c>RawRequestPermutationDoesNotChangeCommandOrder</c>。
    /// </summary>
    public class Task09CommandSurfaceTests
    {
        /// <summary>生产者可见类型上<strong>不得出现</strong>的身份/顺序/费用字段片段。</summary>
        private static readonly string[] ForbiddenNameFragments =
        {
            "SourcePriority", "CommandSequence", "ProducerOrdinal", "FrozenProducerOrdinal",
            "ControllerId", "SourceKind", "Issuer",
            "TriggerTick", "Cost", "Price", "Fee", "Adrenaline", "Budget", "MetaResource"
        };

        /// <summary>生产者可以构造的全部类型（UI/AI 唯一能拿到的命令面）。</summary>
        private static Type[] ProducerVisibleTypes() => new[]
        {
            typeof(CommandRequest), typeof(CommandScope),
            typeof(ScheduleEditScope), typeof(WindowCommandScope), typeof(ReactionCommandScope),
            typeof(ScheduleEditPayload), typeof(WindowCommandPayload), typeof(ReactionCommandPayload),
            typeof(ScheduleEditOperation), typeof(ScheduleAddOperation),
            typeof(ScheduleMoveOperation), typeof(ScheduleRemoveOperation),
            typeof(AddOrdinaryPlanOperation), typeof(MoveEditablePlanOperation),
            typeof(RemoveEditablePlanOperation)
        };

        private static string Describe(CommandEnvelope envelope)
            => envelope.SourcePriority + ":" + envelope.ControllerId.Value + ":" + envelope.ProducerOrdinal +
               ":" + envelope.CommandSequence + ":" + CommandScopes.KindOf(envelope.Request.Scope) +
               ":" + envelope.Request.Payload.Kind;

        private static string EventSignature(StepResult result)
            => string.Join(",", result.Events.Events.Select(e => e.GetType().Name + ":" + e.Tick + ":" + e.Sequence));

        // =====================================================================
        // 必需测试 10：生产者不能选择来源优先级或 CommandSequence
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>把 <c>ControllerId</c>/<c>SourceKind</c>/<c>SourcePriority</c>/<c>ProducerOrdinal</c>/
        /// <c>CommandSequence</c>/费用字段加回 <c>CommandRequest</c> 或任一载荷 ⇒ 字段扫描红；</item>
        /// <item>让 <c>SourcedCommandRequest</c>/<c>FrozenCommandBatch</c>/<c>CommandEnvelope</c>
        /// 拥有 public 构造器（表现层可伪造可信来源）⇒ 构造器断言红；</item>
        /// <item>优先级不再是来源类型<strong>唯一</strong>的函数（可按载荷/调用方改变）⇒ 派生唯一性断言红；</item>
        /// <item>允许生产者跳号或指定序号 ⇒ 1..N 递增断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void ProducerCannotChooseSourcePriorityOrCommandSequence()
        {
            // 1. CommandRequest 恰好三样：目标 Tick、scope 判别、载荷判别。
            string[] requestProperties = typeof(CommandRequest)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.That(requestProperties, Is.EqualTo(new[] { "Payload", "Scope", "TargetTick" }),
                "CommandRequest 只有三样东西：TargetTick / Scope / Payload");

            // 2. 生产者可见面上不存在身份/顺序/费用字段（属性与字段都扫）。
            var offenders = new List<string>();
            foreach (Type type in ProducerVisibleTypes())
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (ForbiddenNameFragments.Any(f => property.Name.Contains(f)))
                        offenders.Add(type.Name + "." + property.Name);
                }
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (ForbiddenNameFragments.Any(f => field.Name.Contains(f)))
                        offenders.Add(type.Name + "." + field.Name);
                }
            }
            Assert.That(offenders, Is.Empty,
                "生产者可见类型不得携带来源身份/顺序/规则费用：" + string.Join(",", offenders));

            // 3. 可信类型不可被生产者构造。
            foreach (Type trusted in new[]
                     {
                         typeof(SourcedCommandRequest), typeof(FrozenCommandBatch), typeof(CommandEnvelope)
                     })
            {
                Assert.That(trusted.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty,
                    trusted.Name + " 不得有 public 构造器");
            }

            // 4. CommandIngressEntry 的全部 public 成员都不得接受生产者可填写的身份/序号/费用参数。
            string[] forbiddenParameters =
            {
                "producerordinal", "sourcepriority", "commandsequence", "triggertick", "issuer",
                "controllerid", "sourcekind", "frozenproducerordinal", "cost", "fee", "price",
                "adrenaline", "budget", "metaresource"
            };
            Type entryType = typeof(CommandIngressEntry);
            var publicEntryMembers = new List<MethodBase>();
            publicEntryMembers.AddRange(entryType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
            publicEntryMembers.AddRange(entryType.GetConstructors(
                BindingFlags.Public | BindingFlags.Instance));
            foreach (MethodBase member in publicEntryMembers)
            {
                foreach (ParameterInfo parameter in member.GetParameters())
                {
                    string parameterName = (parameter.Name ?? string.Empty).ToLowerInvariant();
                    string parameterType = (parameter.ParameterType.Name ?? string.Empty).ToLowerInvariant();
                    Assert.That(forbiddenParameters.Any(f => parameterName.Contains(f)), Is.False,
                        $"CommandIngressEntry.{member.Name} 的参数 '{parameter.Name}' 不得让生产者填写身份/序号/费用");
                    Assert.That(forbiddenParameters.Any(f => parameterType.Contains(f)), Is.False,
                        $"CommandIngressEntry.{member.Name} 的参数类型 '{parameter.ParameterType.Name}' 不得承载身份/序号/费用");
                }
            }

            // 5. 优先级是来源类型的唯一函数（版本化映射，生产者不可参与）。
            Assert.That(CommandSourcePriority.MappingVersion, Is.EqualTo("source-priority-v1"));
            Assert.That(CommandSourcePriority.Of(CommandSourceKind.Player), Is.EqualTo(CommandSourcePriority.Player));
            Assert.That(CommandSourcePriority.Of(CommandSourceKind.Ai), Is.EqualTo(CommandSourcePriority.Ai));
            Assert.That(CommandSourcePriority.Of(CommandSourceKind.System), Is.EqualTo(CommandSourcePriority.System));
            Assert.That(CommandSourcePriority.Player, Is.LessThan(CommandSourcePriority.Ai));
            Assert.That(CommandSourcePriority.Ai, Is.LessThan(CommandSourcePriority.System));

            var schedule = new Task09Fixture.ScriptedWindowSchedule()
                .Open(Task09Fixture.WindowOpenTick, Task09Fixture.Hero, Task09Fixture.WindowBudget);
            var sim = Task09Fixture.NewSim(schedule);
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);
            CommandIngressEntry ai = Task09Fixture.AiEntry(sim);

            Assert.That(player.SourcePriority, Is.EqualTo(CommandSourcePriority.Of(player.SourceKind)));
            Assert.That(ai.SourcePriority, Is.EqualTo(CommandSourcePriority.Of(ai.SourceKind)));

            Task09Fixture.StepNext(sim);   // Tick 0：打开窗口

            // 6. 同一入口提交不同载荷，优先级完全不变（载荷不能影响派生值）。
            Assert.That(player.Submit(Task09Fixture.ScheduleAdd(1L, 5L, 0L, new WindowId(1L), 1L)), Is.Null);
            Assert.That(player.Submit(Task09Fixture.CloseWindow(1L, new WindowId(1L))), Is.Null);
            Assert.That(ai.Submit(Task09Fixture.CloseWindow(1L, new WindowId(1L))), Is.Null);

            StepResult result = Task09Fixture.StepNext(sim);

            IReadOnlyList<CommandEnvelope> envelopes = sim.LastCommandSet.Envelopes;
            Assert.That(envelopes.Select(e => e.SourcePriority).Distinct().ToArray(),
                Is.EqualTo(new[] { CommandSourcePriority.Player, CommandSourcePriority.Ai }));
            Assert.That(envelopes.Select(e => e.CommandSequence).ToArray(), Is.EqualTo(new[] { 1L, 2L, 3L }),
                "CommandSequence 由网关按规范顺序分配：生产者不能跳号、不能指定、不能复用");
            Assert.That(envelopes[0].SourcePriority,
                Is.EqualTo(CommandSourcePriority.Of(envelopes[0].SourceKind)));
            Assert.That(envelopes[2].SourcePriority,
                Is.EqualTo(CommandSourcePriority.Of(envelopes[2].SourceKind)));
            Assert.That(result.Snapshot.NextCommandSequence, Is.EqualTo(4L));
            Assert.That(result.Snapshot.CommandSourcePriorityMappingVersion,
                Is.EqualTo(CommandSourcePriority.MappingVersion),
                "映射版本进入规范化快照与哈希");
        }

        // =====================================================================
        // 必需测试 11：原始请求排列不改变命令顺序
        // =====================================================================

        /// <summary>
        /// <strong>真的构造三种输入排列</strong>（同一组请求、不同投递交错），断言：
        /// 冻结批次里的原始顺序确实不同，而下游的规范顺序、事件与规范化快照哈希完全相同。
        ///
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>规范排序把原始枚举序号当末级键（或直接按投递顺序处理）⇒ 三种排列的
        /// Envelope 顺序/CommandSequence 分配/快照哈希至少一处不同；</item>
        /// <item>入口级拒绝事件不按组键排序 ⇒ 事件签名不同；</item>
        /// <item>排程事务按投递顺序而非 CommandSequence 提交 ⇒ 计划投影不同 ⇒ 哈希不同。</item>
        /// </list>
        /// </summary>
        [Test]
        public void RawRequestPermutationDoesNotChangeCommandOrder()
        {
            string playerFirst = RunPermutation(RawOrder.PlayerFirst,
                out string firstBatchOrder, out long[] firstSequences);
            string aiFirst = RunPermutation(RawOrder.AiFirst,
                out string secondBatchOrder, out long[] secondSequences);
            string interleaved = RunPermutation(RawOrder.Interleaved,
                out string thirdBatchOrder, out long[] thirdSequences);

            // 前提：三种排列的**原始冻结顺序确实不同**（否则本用例什么都没测到）。
            Assert.That(firstBatchOrder, Is.Not.EqualTo(secondBatchOrder),
                "夹具前提：两种排列的原始投递顺序必须真的不同");
            Assert.That(thirdBatchOrder, Is.Not.EqualTo(firstBatchOrder));
            Assert.That(thirdBatchOrder, Is.Not.EqualTo(secondBatchOrder));

            // 结论：下游完全一致。
            Assert.That(aiFirst, Is.EqualTo(playerFirst),
                "原始集合顺序不得成为隐式末级键，也不得选择任何赢家");
            Assert.That(interleaved, Is.EqualTo(playerFirst));
            Assert.That(secondSequences, Is.EqualTo(firstSequences));
            Assert.That(thirdSequences, Is.EqualTo(firstSequences));
            Assert.That(firstSequences, Is.EqualTo(new[] { 1L, 2L, 3L }),
                "CommandSequence 分配只由规范顺序决定");
        }

        private enum RawOrder
        {
            PlayerFirst = 0,
            AiFirst = 1,
            Interleaved = 2
        }

        private static string RunPermutation(
            RawOrder order, out string rawBatchOrder, out long[] commandSequences)
        {
            var schedule = new Task09Fixture.ScriptedWindowSchedule()
                .Open(Task09Fixture.WindowOpenTick, Task09Fixture.Hero, Task09Fixture.WindowBudget);
            var sim = Task09Fixture.NewSim(schedule);
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);
            CommandIngressEntry ai = Task09Fixture.AiEntry(sim);

            Task09Fixture.StepNext(sim);   // Tick 0：打开窗口

            // 同一组请求：Player 两条排程 Add（同一 Lane、互不重叠），AI 一条窗口命令。
            // Tick 0 没有任何命令事务 ⇒ Tick 1 冻结时的 BatchBase 修订号仍是 0。
            CommandRequest addFirst = Task09Fixture.ScheduleAdd(1L, 20L, 0L, new WindowId(1L), 1L);
            CommandRequest addSecond = Task09Fixture.ScheduleAdd(1L, 40L, 0L, new WindowId(1L), 2L);
            CommandRequest aiClose = Task09Fixture.CloseWindow(1L, new WindowId(1L));

            switch (order)
            {
                case RawOrder.PlayerFirst:
                    Assert.That(player.Submit(addFirst), Is.Null);
                    Assert.That(player.Submit(addSecond), Is.Null);
                    Assert.That(ai.Submit(aiClose), Is.Null);
                    break;
                case RawOrder.AiFirst:
                    Assert.That(ai.Submit(aiClose), Is.Null);
                    Assert.That(player.Submit(addFirst), Is.Null);
                    Assert.That(player.Submit(addSecond), Is.Null);
                    break;
                default:
                    Assert.That(player.Submit(addFirst), Is.Null);
                    Assert.That(ai.Submit(aiClose), Is.Null);
                    Assert.That(player.Submit(addSecond), Is.Null);
                    break;
            }

            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(1L);
            rawBatchOrder = string.Join(",", batch.Requests.Select(r => r.ControllerId.Value));

            StepResult result = sim.Step(1L, batch);

            IReadOnlyList<CommandEnvelope> envelopes = sim.LastCommandSet.Envelopes;
            commandSequences = envelopes.Select(e => e.CommandSequence).ToArray();

            // 规范顺序：Player（优先级 0）的两条 Add 在 AI（优先级 10）之前，
            // 与原始投递顺序无关。
            Assert.That(envelopes.Select(e => e.ControllerId.Value).ToArray(), Is.EqualTo(new[]
            {
                Task09Fixture.PlayerId.Value, Task09Fixture.PlayerId.Value, Task09Fixture.AiId.Value
            }));
            Assert.That(envelopes.Select(e => e.ProducerOrdinal).ToArray(), Is.EqualTo(new[] { 1L, 2L, 1L }));
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Select(p => p.StartTick).ToArray(),
                Is.EqualTo(new[] { 20L, 40L }), "两条 Add 的最终投影与投递顺序无关");

            return string.Join(",", envelopes.Select(Describe)) + "|" +
                   EventSignature(result) + "|" + result.SnapshotHashHex;
        }
    }
}
