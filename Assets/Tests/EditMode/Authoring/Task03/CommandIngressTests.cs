using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Authoring.Tests.Task03
{
    /// <summary>
    /// 任务 03 必需测试：命令确定性骨架（入口信任边界、规范排序、重复/回退拒绝、
    /// scope 判别、CommandSequence 归属）。
    /// </summary>
    public class CommandIngressTests
    {
        [Test]
        public void StepRejectsNonCurrentTickRequest()
        {
            // 1. 活动战斗要求严格递增的当前 Tick。
            var skippedSim = Task03.NewSim();
            FrozenCommandBatch batch0 = skippedSim.CommandIngress.FreezeTick(0L);
            var skipped = Assert.Throws<LogicDefinitionException>(() => skippedSim.Step(5L, batch0));
            Assert.That(skipped.ErrorCode, Is.EqualTo(SimulationCodes.STEP_TICK_NOT_CURRENT));
            Assert.That(skippedSim.Tick, Is.EqualTo(-1L), "被拒绝的调用不得推进 Tick");

            // 2. 批次来源必须是本场入口注册表（驱动器无法伪造来源事实）。
            FrozenCommandBatch foreignBatch = new CommandIngressRegistry().FreezeTick(0L);
            var notFromRegistry = Assert.Throws<LogicDefinitionException>(() => skippedSim.Step(0L, foreignBatch));
            Assert.That(notFromRegistry.ErrorCode, Is.EqualTo(SimulationCodes.STEP_BATCH_NOT_FROM_REGISTRY));
            Assert.That(skippedSim.Tick, Is.EqualTo(-1L));

            // 3. batch 为 null。
            var nullBatch = Assert.Throws<LogicDefinitionException>(() => skippedSim.Step(0L, null));
            Assert.That(nullBatch.ErrorCode, Is.EqualTo(CommandCodes.COMMAND_BATCH_TICK_MISMATCH));

            // 4. 批次 Tick 与 Step Tick 不一致（同一注册表、目标 Tick 不同）。
            var mismatchSim = Task03.NewSim();
            FrozenCommandBatch batchThree = mismatchSim.CommandIngress.FreezeTick(3L);
            var mismatchedBatch = Assert.Throws<LogicDefinitionException>(() => mismatchSim.Step(0L, batchThree));
            Assert.That(mismatchedBatch.ErrorCode, Is.EqualTo(CommandCodes.COMMAND_BATCH_TICK_MISMATCH));
            Assert.That(mismatchSim.Tick, Is.EqualTo(-1L));

            // 5. 非法/重复冻结必须稳定拒绝（不得静默复用旧批次）。
            var repeatedFreeze = Assert.Throws<LogicDefinitionException>(() => skippedSim.CommandIngress.FreezeTick(0L));
            Assert.That(repeatedFreeze.ErrorCode, Is.EqualTo(CommandCodes.COMMAND_INGRESS_TICK_NOT_ADVANCING));

            // 6. 正常推进后，已用批次不能用于下一个 Tick。
            var normalSim = Task03.NewSim();
            FrozenCommandBatch usedBatch = normalSim.CommandIngress.FreezeTick(0L);
            normalSim.Step(0L, usedBatch);
            var reused = Assert.Throws<LogicDefinitionException>(() => normalSim.Step(1L, usedBatch));
            Assert.That(reused.ErrorCode, Is.EqualTo(CommandCodes.COMMAND_BATCH_TICK_MISMATCH));
            Assert.That(normalSim.Tick, Is.EqualTo(0L));
        }

        [Test]
        public void EmptyTickReturnsEmptyEventBatchAndSnapshot()
        {
            var sim = Task03.NewSim();
            StepResult result = Task03.StepEmpty(sim);

            Assert.That(result.Status, Is.EqualTo(StepStatus.Advanced));
            Assert.That(result.Events, Is.Not.Null);
            Assert.That(result.Events.Count, Is.EqualTo(0), "空 Tick 必须返回空批次而不是 null");
            Assert.That(result.Events.Tick, Is.EqualTo(0L));
            Assert.That(result.Snapshot, Is.Not.Null);
            Assert.That(result.Snapshot.Tick, Is.EqualTo(0L));
            Assert.That(sim.Tick, Is.EqualTo(0L));
            Assert.That(sim.CurrentSnapshot.Tick, Is.EqualTo(0L));

            for (int i = 0; i < 3; i++)
            {
                StepResult next = Task03.StepEmpty(sim);
                Assert.That(next.Events.Count, Is.EqualTo(0));
                Assert.That(next.Snapshot.Units.Count, Is.EqualTo(2));
            }
            Assert.That(sim.Tick, Is.EqualTo(3L));
        }

        [Test]
        public void EventSequenceDoesNotResetBetweenTicks()
        {
            var sim = Task03.NewSim();
            var sequences = new List<long>();

            for (long tick = 0; tick < 4; tick++)
            {
                // 每条结构合法的命令都会在处理器占位槽得到一条稳定拒绝事件（已分配 CommandSequence）。
                Task03.PlayerEntry(sim).Submit(Task03.ScheduleAdd(tick));
                StepResult result = Task03.StepNext(sim);
                foreach (LogicEvent logicEvent in result.Events.Events)
                {
                    sequences.Add(logicEvent.Sequence);
                    Assert.That(logicEvent.Tick, Is.EqualTo(tick));
                }
            }

            Assert.That(sequences.Count, Is.EqualTo(4), "每个 Tick 恰好一条拒绝事件");
            Assert.That(sequences, Is.EqualTo(new[] { 1L, 2L, 3L, 4L }), "事件序号整场单调递增，不按 Tick 清零");
            Assert.That(sim.CurrentSnapshot.NextEventSequence, Is.EqualTo(5L));
        }

        [Test]
        public void CommandOrderUsesStableKeys()
        {
            var sim = Task03.NewSim();
            CommandIngressEntry player = Task03.PlayerEntry(sim);
            CommandIngressEntry ai = Task03.AiEntry(sim);

            // 故意乱序提交：AI 先提交多条，再提交 Player。
            ai.Submit(Task03.ScheduleAdd(0L));
            ai.Submit(Task03.ScheduleAdd(0L));
            player.Submit(Task03.ScheduleAdd(0L));

            StepResult result = Task03.StepNext(sim);
            IReadOnlyList<CommandEnvelope> envelopes = sim.LastCommandSet.Envelopes;

            Assert.That(envelopes.Count, Is.EqualTo(3));
            Assert.That(envelopes.Select(e => e.ControllerId.Value).ToArray(),
                Is.EqualTo(new[] { Task03.PlayerController, Task03.EnemyAiController, Task03.EnemyAiController }),
                "规范顺序 = SourcePriority -> ControllerId(Ordinal) -> ProducerOrdinal");
            Assert.That(envelopes.Select(e => e.SourcePriority).ToArray(),
                Is.EqualTo(new[] { CommandSourcePriority.Player, CommandSourcePriority.Ai, CommandSourcePriority.Ai }));
            Assert.That(envelopes.Select(e => e.ProducerOrdinal).ToArray(), Is.EqualTo(new[] { 1L, 1L, 2L }));
            Assert.That(envelopes.Select(e => e.CommandSequence).ToArray(), Is.EqualTo(new[] { 1L, 2L, 3L }));

            // 固定映射与版本化映射标识。
            Assert.That(CommandSourcePriority.Player, Is.EqualTo(0));
            Assert.That(CommandSourcePriority.Ai, Is.EqualTo(10));
            Assert.That(CommandSourcePriority.System, Is.EqualTo(20));
            Assert.That(CommandSourcePriority.MappingVersion, Is.EqualTo("source-priority-v1"));
            Assert.That(result.Snapshot.CommandSourcePriorityMappingVersion,
                Is.EqualTo(CommandSourcePriority.MappingVersion));

            // 入口列表按 (SourcePriority, ControllerId) 规范排序，System 入口排在最后。
            var entries = result.Snapshot.CommandIngresses.Entries;
            Assert.That(entries.Select(e => e.ControllerId).ToArray(), Is.EqualTo(new[]
            {
                Task03.PlayerController, Task03.EnemyAiController, BattleSimulation.SystemControllerId
            }));
            Assert.That(entries[2].RegistrationKind, Is.EqualTo("System"));
            Assert.That(entries[2].SourcePriority, Is.EqualTo(CommandSourcePriority.System));
        }

        [Test]
        public void RawRequestPermutationProducesSameCanonicalCommandOrder()
        {
            string firstOrder = RunAndDescribe(submitAiFirst: true);
            string secondOrder = RunAndDescribe(submitAiFirst: false);
            Assert.That(secondOrder, Is.EqualTo(firstOrder),
                "原始集合枚举顺序不得成为隐式末级键");
        }

        private static string RunAndDescribe(bool submitAiFirst)
        {
            var sim = Task03.NewSim();
            CommandIngressEntry player = Task03.PlayerEntry(sim);
            CommandIngressEntry ai = Task03.AiEntry(sim);

            if (submitAiFirst)
            {
                ai.Submit(Task03.ScheduleAdd(0L));
                ai.Submit(Task03.CloseWindow(0L, new WindowId(1L)));
                player.Submit(Task03.Block(0L, 1L));
                player.Submit(Task03.Dodge(0L, 2L, new GridPoint(0, 0)));
            }
            else
            {
                // 只交换两个入口之间的投递交错：每个入口自身的提交顺序保持不变
                // （入口序号是提交顺序的函数，交换同一入口内部顺序会改变序号本身）。
                player.Submit(Task03.Block(0L, 1L));
                ai.Submit(Task03.ScheduleAdd(0L));
                player.Submit(Task03.Dodge(0L, 2L, new GridPoint(0, 0)));
                ai.Submit(Task03.CloseWindow(0L, new WindowId(1L)));
            }

            StepResult result = Task03.StepNext(sim);
            string envelopes = string.Join(",", sim.LastCommandSet.Envelopes.Select(Describe));
            return envelopes + "|" + Task03.EventSignature(result.Events) + "|" + result.SnapshotHashHex;
        }

        private static string Describe(CommandEnvelope envelope)
            => envelope.SourcePriority + ":" + envelope.ControllerId.Value + ":" +
               envelope.ProducerOrdinal + ":" + envelope.CommandSequence + ":" +
               CommandScopes.KindOf(envelope.Request.Scope) + ":" + envelope.Request.Payload.Kind;

        [Test]
        public void DuplicateProducerOrdinalRejectsEntireCollisionGroup()
        {
            var sim = Task03.NewSim();
            CommandIngressEntry player = Task03.PlayerEntry(sim);

            // 两条已记录事实携带同一 ProducerOrdinal → 同一规范键碰撞。
            player.InjectRecordedFact(3L, Task03.ScheduleAdd(0L));
            player.InjectRecordedFact(3L, Task03.Block(0L, 1L));

            StepResult result = Task03.StepNext(sim);

            var rejections = Task03.EventsOfType<CommandIngressRejectedEvent>(result.Events)
                .Cast<CommandIngressRejectedEvent>().ToList();
            Assert.That(rejections.Count, Is.EqualTo(1), "每个非法规范键只产生一个拒绝事件");
            Assert.That(rejections[0].ReasonCode, Is.EqualTo(CommandCodes.DUPLICATE_COMMAND_ORDINAL));
            Assert.That(rejections[0].CollisionCount, Is.EqualTo(2), "整个碰撞组被拒绝");
            Assert.That(rejections[0].ProducerOrdinal, Is.EqualTo(3L));
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(0), "碰撞组没有赢家");
            Assert.That(result.Snapshot.NextCommandSequence, Is.EqualTo(1L),
                "未通过入口校验的键不得伪造 CommandSequence");

            // 碰撞成员的原始排列不影响拒绝事件顺序与快照。
            var reversed = Task03.NewSim();
            CommandIngressEntry reversedPlayer = Task03.PlayerEntry(reversed);
            reversedPlayer.InjectRecordedFact(3L, Task03.Block(0L, 1L));
            reversedPlayer.InjectRecordedFact(3L, Task03.ScheduleAdd(0L));
            StepResult reversedResult = Task03.StepNext(reversed);

            Assert.That(Task03.EventSignature(reversedResult.Events),
                Is.EqualTo(Task03.EventSignature(result.Events)),
                "入口级组拒绝的事件顺序必须独立于碰撞成员的原始排列");
            Assert.That(reversedResult.SnapshotHashHex, Is.EqualTo(result.SnapshotHashHex));
        }

        [Test]
        public void ProducerOrdinalRegressionIsRejected()
        {
            var sim = Task03.NewSim();
            CommandIngressEntry player = Task03.PlayerEntry(sim);

            player.Submit(Task03.ScheduleAdd(0L));
            Task03.StepNext(sim);
            Assert.That(player.FrozenProducerOrdinal, Is.EqualTo(1L), "本批冻结后水位 = 1");

            // 回放注入一个低于已冻结水位的序号。
            player.InjectRecordedFact(1L, Task03.ScheduleAdd(1L));
            StepResult result = Task03.StepNext(sim);

            var rejections = Task03.EventsOfType<CommandIngressRejectedEvent>(result.Events)
                .Cast<CommandIngressRejectedEvent>().ToList();
            Assert.That(rejections.Count, Is.EqualTo(1));
            Assert.That(rejections[0].ReasonCode, Is.EqualTo(CommandCodes.COMMAND_ORDINAL_REGRESSION));
            Assert.That(rejections[0].ProducerOrdinal, Is.EqualTo(1L));
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(0));
            Assert.That(result.Snapshot.NextCommandSequence, Is.EqualTo(2L),
                "只有 Tick 0 的合法命令消耗了序号");
            Assert.That(player.FrozenProducerOrdinal, Is.EqualTo(1L), "被拒绝的回退序号不得推进水位");
        }

        [Test]
        public void CommandSequenceIsUniqueAndMonotonic()
        {
            var sim = Task03.NewSim();
            var observed = new List<long>();

            for (long tick = 0; tick < 5; tick++)
            {
                Task03.PlayerEntry(sim).Submit(Task03.ScheduleAdd(tick));
                Task03.AiEntry(sim).Submit(Task03.ScheduleAdd(tick));
                StepResult result = Task03.StepNext(sim);

                // 每条已编号命令都在处理器占位槽产生一条拒绝事件，事件顺序即 CommandSequence 顺序。
                foreach (CommandRejectedEvent rejection in
                         Task03.EventsOfType<CommandRejectedEvent>(result.Events).Cast<CommandRejectedEvent>())
                {
                    observed.Add(rejection.CommandSequence);
                }

                Assert.That(result.Snapshot.NextCommandSequence, Is.EqualTo(tick * 2L + 3L));
            }

            Assert.That(observed, Is.EqualTo(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }),
                "CommandSequence 整场唯一、单调、不复用");
            Assert.That(observed.Distinct().Count(), Is.EqualTo(observed.Count));
        }

        [Test]
        public void ProducerCannotProvideSourcePriorityOrCommandSequence()
        {
            string[] forbidden =
            {
                "SourcePriority", "CommandSequence", "ProducerOrdinal", "ControllerId", "SourceKind",
                "FrozenProducerOrdinal", "Issuer"
            };

            var producerVisible = new[]
            {
                typeof(CommandRequest), typeof(CommandScope), typeof(ScheduleEditScope),
                typeof(WindowCommandScope), typeof(ReactionCommandScope),
                typeof(ScheduleEditPayload), typeof(WindowCommandPayload), typeof(ReactionCommandPayload),
                typeof(ScheduleEditOperation), typeof(ScheduleAddOperation),
                typeof(ScheduleMoveOperation), typeof(ScheduleRemoveOperation)
            };

            foreach (Type type in producerVisible)
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    Assert.That(forbidden.Any(f => property.Name.Contains(f)), Is.False,
                        $"{type.Name}.{property.Name} 不得让生产者提供来源身份/序号");
                }
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    Assert.That(forbidden.Any(f => field.Name.Contains(f)), Is.False,
                        $"{type.Name}.{field.Name} 不得让生产者提供来源身份/序号");
                }
            }
            // 可信事实与内部 Envelope 都不可被生产者构造。
            foreach (Type trustedType in new[] { typeof(SourcedCommandRequest), typeof(FrozenCommandBatch), typeof(CommandEnvelope) })
            {
                Assert.That(trustedType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty,
                    $"{trustedType.Name} 不得有 public 构造器");
            }

            // 入口注册是唯一信任边界：注册表只能给出绑定后的入口。
            var registry = new CommandIngressRegistry();
            var entry = registry.RegisterExternalEntry(CommandBindingFixture("controller.test", CommandSourceKind.Player));
            Assert.That(entry.SourcePriority, Is.EqualTo(CommandSourcePriority.Player));
            Assert.That(entry.Submit(Task03.ScheduleAdd(0L)), Is.Null);
            Assert.That(entry.NextProducerOrdinal, Is.EqualTo(2L));
            Assert.That(entry.Submit(Task03.ScheduleAdd(0L)), Is.Null);
            Assert.That(entry.NextProducerOrdinal, Is.EqualTo(3L));

            // 验收标准补齐（R3）：CommandIngressEntry 是生产者唯一持有的可信对象，
            // 因此它的**全部 public 成员**都不得接受生产者可填写的身份/序号/费用参数。
            // 这里检查的是**参数**而不是成员名：入口上 public 的 SourcePriority /
            // NextProducerOrdinal / FrozenProducerOrdinal 只是诊断读数（只读属性），
            // 不构成"可填写的入口"——把它们一起禁掉是过度收紧，会误伤诊断快照 API。
            string[] forbiddenParameters =
            {
                "producerordinal", "sourcepriority", "commandsequence", "triggertick",
                "issuer", "controllerid", "sourcekind", "frozenproducerordinal",
                "cost", "fee", "price", "adrenaline", "budget", "metaresource"
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
                    Assert.That(forbiddenParameters.Any(f => parameterName.Contains(f)), Is.False,
                        $"CommandIngressEntry.{member.Name} 的参数 '{parameter.Name}' 不得让生产者填写来源身份/序号/费用");
                    string parameterType = (parameter.ParameterType.Name ?? string.Empty).ToLowerInvariant();
                    Assert.That(forbiddenParameters.Any(f => parameterType.Contains(f)), Is.False,
                        $"CommandIngressEntry.{member.Name} 的参数类型 '{parameter.ParameterType.Name}' 不得承载来源身份/序号/费用");
                }
            }

            // 记录事实注入能力必须**存在**（回放需要它），但在 public 面上必须**不可达**：
            // 收窄为 internal 后，"UI/AI 可见 API 中不存在可填写生产者序号的入口"成为编译期事实。
            MethodInfo[] injections = entryType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == "InjectRecordedFact")
                .ToArray();
            Assert.That(injections.Length, Is.EqualTo(2), "记录事实注入的两个重载都必须仍然存在");
            foreach (MethodInfo injection in injections)
            {
                Assert.That(injection.IsPublic, Is.False,
                    "InjectRecordedFact 不得是 public：生产者可见 API 中不存在可填写 ProducerOrdinal 的入口");
                Assert.That(injection.IsAssembly, Is.True,
                    "InjectRecordedFact 必须是 internal（程序集可见），不得放宽为 protected / protected internal");
            }
            Assert.That(entryType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Any(m => m.Name == "InjectRecordedFact"), Is.False,
                "public 面上不得出现任何名为 InjectRecordedFact 的成员");
            // 非 public 的其它注入面同样不得留在 public 面（系统来源走 internal SubmitSystemInternal）。
            Assert.That(entryType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Any(m => m.Name == "SubmitSystemInternal"), Is.False,
                "public 面上不得出现 SubmitSystemInternal");
        }

        private static ControllerBinding CommandBindingFixture(string controllerId, CommandSourceKind kind)
            => new ControllerBinding(new ControllerId(controllerId), kind, Array.Empty<EncounterSlotId>());

        [Test]
        public void CommandRequestUsesDiscriminatedScheduleWindowOrReactionScope()
        {
            var scheduleScope = new ScheduleEditScope(4L, new WindowId(2L));
            var windowScope = new WindowCommandScope(new WindowId(2L));
            var reactionScope = new ReactionCommandScope(new ReactionOpportunityId(9L));

            Assert.That(CommandScopes.KindOf(scheduleScope), Is.EqualTo(CommandScopeKind.ScheduleEdit));
            Assert.That(CommandScopes.KindOf(windowScope), Is.EqualTo(CommandScopeKind.Window));
            Assert.That(CommandScopes.KindOf(reactionScope), Is.EqualTo(CommandScopeKind.Reaction));
            Assert.That(scheduleScope.ExpectedScheduleRevision, Is.EqualTo(4L));
            Assert.That(scheduleScope.ExpectedWindowId, Is.EqualTo(new WindowId(2L)));
            Assert.That(windowScope.ExpectedWindowId, Is.EqualTo(new WindowId(2L)));
            Assert.That(reactionScope.ReactionOpportunityId, Is.EqualTo(new ReactionOpportunityId(9L)));

            // 三类载荷判别与 scope 一一对应。
            Assert.That(Task03.ScheduleAdd(0L).Payload.Kind, Is.EqualTo(CommandScopeKind.ScheduleEdit));
            Assert.That(Task03.CloseWindow(0L, new WindowId(1L)).Payload.Kind, Is.EqualTo(CommandScopeKind.Window));
            Assert.That(Task03.Block(0L, 1L).Payload.Kind, Is.EqualTo(CommandScopeKind.Reaction));

            Assert.That(Task03.ScheduleAdd(0L).ValidateStaticStructure(), Is.Null);
            Assert.That(Task03.CloseWindow(0L, new WindowId(1L)).ValidateStaticStructure(), Is.Null);
            Assert.That(Task03.Dodge(0L, 1L, new GridPoint(0, 0)).ValidateStaticStructure(), Is.Null);

            var mismatched = new CommandRequest(0L, windowScope, new ScheduleEditPayload(
                new ScheduleEditOperation[] { new ScheduleAddOperation(new ActionPlanId(1L), 0L) }));
            Assert.That(mismatched.ValidateStaticStructure(), Is.EqualTo(CommandCodes.SCOPE_PAYLOAD_MISMATCH));

            var emptyOperations = new CommandRequest(0L, scheduleScope,
                new ScheduleEditPayload(Array.Empty<ScheduleEditOperation>()));
            Assert.That(emptyOperations.ValidateStaticStructure(), Is.EqualTo(CommandCodes.PAYLOAD_STRUCTURALLY_INVALID));

            var dodgeWithoutCell = new CommandRequest(0L, reactionScope,
                new ReactionCommandPayload(ReactionCommandKind.Dodge, new ActionSpecId("action.dodge")));
            Assert.That(dodgeWithoutCell.ValidateStaticStructure(), Is.EqualTo(CommandCodes.PAYLOAD_STRUCTURALLY_INVALID));

            var blockWithCell = new CommandRequest(0L, reactionScope,
                new ReactionCommandPayload(ReactionCommandKind.Block, new ActionSpecId("action.block"), new GridPoint(0, 0)));
            Assert.That(blockWithCell.ValidateStaticStructure(), Is.EqualTo(CommandCodes.PAYLOAD_STRUCTURALLY_INVALID));
        }

        [Test]
        public void ProducerCannotProvideReactionTriggerTickOrRuleCost()
        {
            string[] forbidden = { "TriggerTick", "Cost", "Adrenaline", "Budget", "MetaResource", "Price", "Fee" };

            var producerVisible = new[]
            {
                typeof(CommandRequest), typeof(CommandScope), typeof(ScheduleEditScope),
                typeof(WindowCommandScope), typeof(ReactionCommandScope),
                typeof(ScheduleEditPayload), typeof(WindowCommandPayload), typeof(ReactionCommandPayload),
                typeof(ScheduleEditOperation), typeof(ScheduleAddOperation),
                typeof(ScheduleMoveOperation), typeof(ScheduleRemoveOperation)
            };

            foreach (Type type in producerVisible)
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    Assert.That(forbidden.Any(f => property.Name.Contains(f)), Is.False,
                        $"{type.Name}.{property.Name} 不得让生产者提供反应 TriggerTick 或规则费用");
                }
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    Assert.That(forbidden.Any(f => field.Name.Contains(f)), Is.False,
                        $"{type.Name}.{field.Name} 不得让生产者提供反应 TriggerTick 或规则费用");
                }
            }

            var properties = typeof(ReactionCommandPayload)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).ToArray();
            Assert.That(properties, Is.EquivalentTo(new[]
                { "ReactionKind", "ReactionActionSpecId", "DodgeDestination", "Kind" }));
            Assert.That(typeof(ReactionCommandPayload).GetProperty("TriggerTick"), Is.Null);
        }

        [Test]
        public void ScopePayloadMismatchIsRejectedDeterministically()
        {
            var sim = Task03.NewSim();
            CommandIngressEntry player = Task03.PlayerEntry(sim);

            // 三种判别不匹配，提交顺序与组键顺序刻意相反。
            player.Submit(new CommandRequest(0L, new WindowCommandScope(new WindowId(1L)),
                new ScheduleEditPayload(new ScheduleEditOperation[] { new ScheduleAddOperation(new ActionPlanId(1L), 0L) })));
            player.Submit(new CommandRequest(0L, new ReactionCommandScope(new ReactionOpportunityId(1L)),
                new WindowCommandPayload(WindowCommandKind.CloseOwnWindow)));
            player.Submit(new CommandRequest(0L, new ScheduleEditScope(0L, null),
                new ReactionCommandPayload(ReactionCommandKind.Block, new ActionSpecId("action.block"))));

            StepResult result = Task03.StepNext(sim);

            var rejections = Task03.EventsOfType<CommandIngressRejectedEvent>(result.Events)
                .Cast<CommandIngressRejectedEvent>().ToList();
            Assert.That(rejections.Count, Is.EqualTo(3));
            Assert.That(rejections.All(r => r.ReasonCode == CommandCodes.SCOPE_PAYLOAD_MISMATCH), Is.True);
            Assert.That(rejections.Select(r => r.ProducerOrdinal).ToArray(), Is.EqualTo(new[] { 1L, 2L, 3L }),
                "拒绝事件按组键（含 ProducerOrdinal）排序");
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(0));
            Assert.That(result.Snapshot.NextCommandSequence, Is.EqualTo(1L));

            var repeated = Task03.NewSim();
            CommandIngressEntry repeatedPlayer = Task03.PlayerEntry(repeated);
            repeatedPlayer.Submit(new CommandRequest(0L, new ScheduleEditScope(0L, null),
                new ReactionCommandPayload(ReactionCommandKind.Block, new ActionSpecId("action.block"))));
            repeatedPlayer.Submit(new CommandRequest(0L, new WindowCommandScope(new WindowId(1L)),
                new ScheduleEditPayload(new ScheduleEditOperation[] { new ScheduleAddOperation(new ActionPlanId(1L), 0L) })));
            repeatedPlayer.Submit(new CommandRequest(0L, new ReactionCommandScope(new ReactionOpportunityId(1L)),
                new WindowCommandPayload(WindowCommandKind.CloseOwnWindow)));

            StepResult repeatedResult = Task03.StepNext(repeated);
            Assert.That(Task03.EventSignature(repeatedResult.Events),
                Is.EqualTo(Task03.EventSignature(result.Events)));
        }

        [Test]
        public void ExternalIngressCannotRegisterSystemSource()
        {
            var registry = new CommandIngressRegistry();
            var binding = new ControllerBinding(
                new ControllerId("controller.forged_system"), CommandSourceKind.System, Array.Empty<EncounterSlotId>());

            var rejected = Assert.Throws<LogicDefinitionException>(() => registry.RegisterExternalEntry(binding));
            Assert.That(rejected.ErrorCode, Is.EqualTo(CommandCodes.EXTERNAL_INGRESS_SYSTEM_SOURCE_REJECTED));

            registry.RegisterExternalEntry(CommandBindingFixture("controller.test", CommandSourceKind.Player));
            var duplicate = Assert.Throws<LogicDefinitionException>(() => registry.RegisterExternalEntry(
                CommandBindingFixture("controller.test", CommandSourceKind.Ai)));
            Assert.That(duplicate.ErrorCode, Is.EqualTo(CommandCodes.COMMAND_INGRESS_DUPLICATE_CONTROLLER));

            // 系统注册路径不是 public：外部程序集无法把自己注册成 System 来源。
            MethodInfo systemRegistration = typeof(CommandIngressRegistry)
                .GetMethod("RegisterSystemEntry", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(systemRegistration, Is.Not.Null, "Logic 内部仍需要系统来源路径");
            Assert.That(systemRegistration.IsPublic, Is.False);
            Assert.That(typeof(CommandIngressRegistry).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => m.Name.Contains("System")), Is.False, "公开面上不存在系统来源注册入口");

            // 模拟内部注册的系统入口存在且可观测，但公开提交路径被稳定拒绝。
            var sim = Task03.NewSim();
            CommandIngressEntry systemEntry = Task03.SystemEntry(sim);
            Assert.That(systemEntry, Is.Not.Null);
            Assert.That(systemEntry.IsSystemInternal, Is.True);
            Assert.That(systemEntry.RegistrationKind, Is.EqualTo("System"));

            CommandIngressRejection rejection = systemEntry.Submit(Task03.ScheduleAdd(0L));
            Assert.That(rejection, Is.Not.Null);
            Assert.That(rejection.ReasonCode, Is.EqualTo(CommandCodes.SYSTEM_SOURCE_INTERNAL_ONLY));
            Assert.That(systemEntry.NextProducerOrdinal, Is.EqualTo(1L), "被拒绝的系统提交不得消耗序号");

            StepResult result = Task03.StepNext(sim);
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(0));
            Assert.That(result.Snapshot.CommandIngresses.Entries
                .Single(e => e.ControllerId == BattleSimulation.SystemControllerId).NextProducerOrdinal, Is.EqualTo(1L));
        }

        [Test]
        public void CommandIngressStateAppearsInCanonicalSnapshot()
        {
            var sim = Task03.NewSim();
            CommandIngressEntry player = Task03.PlayerEntry(sim);
            CommandIngressEntry ai = Task03.AiEntry(sim);

            player.Submit(Task03.ScheduleAdd(1L));
            player.Submit(Task03.ScheduleAdd(2L));
            ai.Submit(Task03.ScheduleAdd(2L));

            StepResult result = Task03.StepEmpty(sim);
            CommandIngressRegistrySnapshot snapshot = result.Snapshot.CommandIngresses;

            CommandIngressEntrySnapshot playerEntry = snapshot.Entries.Single(e => e.ControllerId == Task03.PlayerController);
            Assert.That(playerEntry.NextProducerOrdinal, Is.EqualTo(3L), "每入口下一个 ProducerOrdinal");
            Assert.That(playerEntry.FrozenProducerOrdinal, Is.EqualTo(0L), "尚未冻结任何本入口事实");
            Assert.That(snapshot.FrozenThroughTick, Is.EqualTo(0L));

            Assert.That(snapshot.FutureBuckets.Select(b => b.TargetTick).ToArray(), Is.EqualTo(new[] { 1L, 2L }));
            Assert.That(snapshot.FutureBuckets.Select(b => b.PendingCount).ToArray(), Is.EqualTo(new[] { 1, 2 }));

            StepResult second = Task03.StepEmpty(sim);
            Assert.That(second.Snapshot.CommandIngresses.FrozenThroughTick, Is.EqualTo(1L));
            Assert.That(second.Snapshot.CommandIngresses.Entries
                .Single(e => e.ControllerId == Task03.PlayerController).FrozenProducerOrdinal, Is.EqualTo(1L));
            Assert.That(second.Snapshot.CommandIngresses.FutureBuckets.Select(b => b.TargetTick).ToArray(),
                Is.EqualTo(new[] { 2L }));
            Assert.That(second.Snapshot.NextCommandSequence, Is.EqualTo(2L));
        }

        [Test]
        public void LateInputAfterBatchFreezeCannotMutateCurrentTickSchedule()
        {
            var sim = Task03.NewSim();
            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(0L);

            // 批次冻结后到达、目标 Tick 已被冻结 → 稳定拒绝，不静默丢弃。
            CommandIngressRejection late = Task03.PlayerEntry(sim).Submit(Task03.ScheduleAdd(0L));
            Assert.That(late, Is.Not.Null);
            Assert.That(late.ReasonCode, Is.EqualTo(CommandCodes.LATE_REQUEST_FOR_FROZEN_TICK));
            Assert.That(late.ProducerOrdinal, Is.EqualTo(0L), "迟到请求不得获得本批序号");

            StepResult result = sim.Step(0L, batch);

            Assert.That(result.Snapshot.ScheduleRevision, Is.EqualTo(0L), "本 Tick 排程修订不得被迟到输入改写");
            Assert.That(result.Snapshot.NextCommandSequence, Is.EqualTo(1L),
                "迟到输入不得消耗 CommandSequence（本 Tick 没有合法命令）");
            Assert.That(result.Snapshot.Plans.Count, Is.EqualTo(0));
            Assert.That(result.Snapshot.Reservations.Count, Is.EqualTo(0));
            Assert.That(result.Snapshot.Intents.Count, Is.EqualTo(0));
            Assert.That(result.Snapshot.MovementSegments.Count, Is.EqualTo(0));

            var rejectionEvents = Task03.EventsOfType<CommandIngressRejectedEvent>(result.Events)
                .Cast<CommandIngressRejectedEvent>().ToList();
            Assert.That(rejectionEvents.Count, Is.EqualTo(1));
            Assert.That(rejectionEvents[0].ReasonCode, Is.EqualTo(CommandCodes.LATE_REQUEST_FOR_FROZEN_TICK));

            // 与"完全没有迟到输入"的运行比较：除该拒绝事件外活动状态完全一致。
            var clean = Task03.NewSim();
            StepResult cleanResult = clean.Step(0L, clean.CommandIngress.FreezeTick(0L));

            Assert.That(Task03.UnitSignature(result.Snapshot), Is.EqualTo(Task03.UnitSignature(cleanResult.Snapshot)));
            Assert.That(result.Snapshot.ScheduleRevision, Is.EqualTo(cleanResult.Snapshot.ScheduleRevision));
            Assert.That(result.Snapshot.NextCommandSequence, Is.EqualTo(cleanResult.Snapshot.NextCommandSequence));
            Assert.That(result.Snapshot.NextEventSequence, Is.EqualTo(cleanResult.Snapshot.NextEventSequence + 1L),
                "唯一差异是那条入口拒绝事件");

            // 未来 Tick 仍然可以提交（"此后新请求只能进入未来 Tick"）。
            Assert.That(Task03.PlayerEntry(sim).Submit(Task03.ScheduleAdd(1L)), Is.Null);
        }

        [Test]
        public void ScheduleEditsCompareExpectedRevisionToFrozenBatchBase()
        {
            var sim = Task03.NewSim();
            CommandIngressEntry player = Task03.PlayerEntry(sim);

            player.Submit(Task03.ScheduleAdd(0L, expectedScheduleRevision: 0L, window: new WindowId(7L)));
            player.Submit(Task03.ScheduleAdd(0L, expectedScheduleRevision: 5L));

            StepResult result = Task03.StepNext(sim);

            Assert.That(sim.BatchBaseScheduleRevision, Is.EqualTo(0L));
            Assert.That(result.Snapshot.ScheduleRevision, Is.EqualTo(0L));

            IReadOnlyList<CommandEnvelope> envelopes = sim.LastCommandSet.Envelopes;
            Assert.That(envelopes.Count, Is.EqualTo(2));
            var scopes = envelopes.Select(e => (ScheduleEditScope)e.Request.Scope).ToArray();
            Assert.That(scopes[0].ExpectedScheduleRevision, Is.EqualTo(0L));
            Assert.That(scopes[0].ExpectedWindowId, Is.EqualTo(new WindowId(7L)));
            Assert.That(scopes[1].ExpectedScheduleRevision, Is.EqualTo(5L),
                "期望修订号必须原样送达处理器，不得被钳制或静默改写");

            // BatchBase 的冻结点（阶段 5）严格早于排程编辑的应用点（阶段 6）。
            var phases = sim.LastStepPhaseTrace.Select(e => e.Phase).ToList();
            Assert.That(phases.IndexOf(StepPhase.FrozenBatchMergeAndBaseRevision),
                Is.LessThan(phases.IndexOf(StepPhase.CommandValidationAndScheduling)));
            Assert.That(phases.IndexOf(StepPhase.CommandValidationAndScheduling),
                Is.LessThan(phases.IndexOf(StepPhase.DuePlanStartGateAndReactionTrigger)));
        }
    }
}
