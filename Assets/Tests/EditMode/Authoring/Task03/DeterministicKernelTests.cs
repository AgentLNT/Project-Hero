using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Authoring.Tests.Task03
{
    /// <summary>
    /// 任务 03 必需测试：确定性内核（版本化 RNG、规范编码/哈希、单调 ID/Sequence、
    /// 规范化快照的哈希参与）。
    /// </summary>
    public class DeterministicKernelTests
    {
        [Test]
        public void RngStateAppearsInCanonicalSnapshot()
        {
            var sim = Task03.NewSim();
            LogicSnapshot before = sim.CurrentSnapshot;

            Assert.That(before.Rng, Is.Not.Null);
            Assert.That(before.Rng.AlgorithmVersion, Is.EqualTo(DeterministicRng.AlgorithmVersion));
            Assert.That(before.Rng.State, Is.EqualTo(Task03.Inputs.InitialRngSeed),
                "首个 Step 之前 RNG 仍处于初始种子");

            StepResult result = Task03.StepEmpty(sim);
            Assert.That(result.Snapshot.Rng.AlgorithmVersion, Is.EqualTo(DeterministicRng.AlgorithmVersion));
            Assert.That(result.Snapshot.Rng.State, Is.EqualTo(Task03.Inputs.InitialRngSeed),
                "空 Tick 不得消耗 RNG");

            // RNG 状态参与哈希：只改 RNG 状态的两份快照必须得到不同摘要。
            ulong a = MakeSnapshot(rng: new RngSnapshot(DeterministicRng.AlgorithmVersion, 1UL)).ComputeHash();
            ulong b = MakeSnapshot(rng: new RngSnapshot(DeterministicRng.AlgorithmVersion, 2UL)).ComputeHash();
            Assert.That(a, Is.Not.EqualTo(b));

            // 同一种子必得同一序列；不同种子必得不同序列。
            var rngA = new DeterministicRng(20260922UL);
            var rngB = new DeterministicRng(20260922UL);
            var rngC = new DeterministicRng(20260923UL);
            var sequenceA = new List<ulong>();
            var sequenceB = new List<ulong>();
            var sequenceC = new List<ulong>();
            for (int i = 0; i < 16; i++)
            {
                sequenceA.Add(rngA.NextUInt64());
                sequenceB.Add(rngB.NextUInt64());
                sequenceC.Add(rngC.NextUInt64());
            }
            Assert.That(sequenceA, Is.EqualTo(sequenceB));
            Assert.That(sequenceA, Is.Not.EqualTo(sequenceC));

            // 随机选择只接受"候选数量"：空候选集必须由调用方过滤，不得随机挑一个容器元素。
            Assert.Throws<ArgumentOutOfRangeException>(() => new DeterministicRng(1UL).PickIndex(0));
            Assert.That(new DeterministicRng(7UL).PickIndex(3), Is.InRange(0, 2));

            // 首版不提供中途恢复 API：没有 FromState / State setter。
            Assert.That(typeof(DeterministicRng).GetProperty("State").CanWrite, Is.False);
            Assert.That(typeof(DeterministicRng).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(m => m.Name.Contains("FromState")), Is.False);
        }

        [Test]
        public void CanonicalEncodingIsDomainSeparatedLengthPrefixedAndFrozen()
        {
            // 手工构造期望字节（独立于实现），验证编码契约。
            var encoder = new CanonicalEncoder();
            encoder.BeginDomain("HistorySeed");
            encoder.WriteInt32(1);

            var expected = new List<byte>();
            byte[] domain = System.Text.Encoding.UTF8.GetBytes("HistorySeed");
            expected.AddRange(LittleEndian(domain.Length));
            expected.AddRange(domain);
            expected.AddRange(LittleEndian(1));

            Assert.That(encoder.ToArray(), Is.EqualTo(expected.ToArray()),
                "域标记必须长度前缀、整数必须 8 字节小端");
            Assert.That(encoder.ToDigest(), Is.EqualTo(ReplayFormat.HistorySeedDigest(1)),
                "H0 必须等于 Hash(Encode(\"HistorySeed\", ReplayFormatVersion))");

            // 域分离：("AB","C") 与 ("A","BC") 的编码必须不同。
            var first = new CanonicalEncoder().BeginDomain("AB").WriteString("C").ToDigest();
            var second = new CanonicalEncoder().BeginDomain("A").WriteString("BC").ToDigest();
            Assert.That(first, Is.Not.EqualTo(second));

            // 集合顺序必须影响摘要（编码器不排序、不去重）。
            var orderA = new CanonicalEncoder().WriteInt64(1).WriteInt64(2).ToDigest();
            var orderB = new CanonicalEncoder().WriteInt64(2).WriteInt64(1).ToDigest();
            Assert.That(orderA, Is.Not.EqualTo(orderB));

            Assert.That(CanonicalHash.ToHex(CanonicalHash.OfBytes(Array.Empty<byte>())),
                Is.EqualTo("cbf29ce484222325"), "空输入的 FNV-1a 64 偏移基值");
            Assert.That(CanonicalHash.FromHex("cbf29ce484222325"), Is.EqualTo(CanonicalHash.FnvOffsetBasis));
            Assert.That(CanonicalHash.TryFromHex("CBF29CE484222325", out _), Is.False, "摘要必须是小写十六进制");
            Assert.That(CanonicalHash.ToHex(0UL).Length, Is.EqualTo(ReplayFormat.DigestHexWidth));
        }

        [Test]
        public void IdGeneratorNextValuesAppearInCanonicalSnapshot()
        {
            var sim = Task03.NewSim();
            LogicSnapshot snapshot = sim.CurrentSnapshot;

            // 主战斗场景两个单位 → 下一个 UnitId 是 3；其余计数器仍为 1。
            Assert.That(snapshot.NextUnitId, Is.EqualTo(3L));
            Assert.That(snapshot.NextActionPlanId, Is.EqualTo(1L));
            Assert.That(snapshot.NextReactionOpportunityId, Is.EqualTo(1L));
            Assert.That(snapshot.NextWindowId, Is.EqualTo(1L));
            Assert.That(snapshot.NextEffectId, Is.EqualTo(1L));

            // 每个下一个值都参与哈希。
            ulong baseline = MakeSnapshot().ComputeHash();
            Assert.That(MakeSnapshot(nextUnitId: 4L).ComputeHash(), Is.Not.EqualTo(baseline));
            Assert.That(MakeSnapshot(nextActionPlanId: 2L).ComputeHash(), Is.Not.EqualTo(baseline));
            Assert.That(MakeSnapshot(nextReactionOpportunityId: 2L).ComputeHash(), Is.Not.EqualTo(baseline));
            Assert.That(MakeSnapshot(nextWindowId: 2L).ComputeHash(), Is.Not.EqualTo(baseline));
            Assert.That(MakeSnapshot(nextEffectId: 2L).ComputeHash(), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void SequenceCountersAreIndependentFromObjectIds()
        {
            var sequences = new LogicSequenceGenerator();
            var ids = new LogicIdGenerator();

            Assert.That(sequences.NextCommandSequence, Is.EqualTo(1L));
            Assert.That(sequences.TakeCommandSequence(), Is.EqualTo(1L));
            Assert.That(sequences.TakeIntentSequence(), Is.EqualTo(1L));
            Assert.That(sequences.TakeResolutionSequence(), Is.EqualTo(1L));
            Assert.That(sequences.TakeEventSequence(), Is.EqualTo(1L));
            Assert.That(sequences.TakeEffectSequence(), Is.EqualTo(1L));

            // 取用 Sequence 不推进任何对象 ID。
            Assert.That(ids.NextUnitIdValue, Is.EqualTo(1L));
            Assert.That(ids.NextActionPlanIdValue, Is.EqualTo(1L));
            Assert.That(ids.NextReactionOpportunityIdValue, Is.EqualTo(1L));
            Assert.That(ids.NextWindowIdValue, Is.EqualTo(1L));
            Assert.That(ids.NextEffectIdValue, Is.EqualTo(1L));
            Assert.That(ids.NextUnitId().Value, Is.EqualTo(1L));

            // 0 保留为无效值；每类计数独立且从 1 起。
            Assert.That(new UnitId(0L).IsValid, Is.False);
            Assert.That(new ActionPlanId(0L).IsValid, Is.False);
            Assert.That(new ReactionOpportunityId(0L).IsValid, Is.False);
            Assert.That(new WindowId(0L).IsValid, Is.False);
            Assert.That(new EffectId(0L).IsValid, Is.False);

            var sim = Task03.NewSim();
            Assert.That(sim.CurrentSnapshot.NextCommandSequence, Is.EqualTo(1L));
            Assert.That(sim.CurrentSnapshot.NextEventSequence, Is.EqualTo(1L));
        }

        [Test]
        public void FixedUnitsReceiveIdsInOrdinalEncounterSlotOrder()
        {
            var sim = Task03.NewSim();
            var encounter = Task03.Encounter;
            var initialization = ProjectHero.Logic.Initialization.BattleInitializer.BuildInitialState(
                Task03.Definition, Task03.EncounterId, Task03.Inputs);

            // 权威顺序 = SlotId 的 StringComparer.Ordinal 升序：enemy(1) < hero(2)。
            var orderedSlots = encounter.Slots
                .OrderBy(s => s.SlotId.Value, StringComparer.Ordinal)
                .ToArray();

            Assert.That(orderedSlots[0].SlotId.Value, Is.EqualTo(Task03.EnemySlot));
            Assert.That(orderedSlots[1].SlotId.Value, Is.EqualTo(Task03.HeroSlot));

            for (int i = 0; i < orderedSlots.Length; i++)
            {
                UnitId expectedUnitId = initialization.SlotToUnitId[orderedSlots[i].SlotId];
                Assert.That(expectedUnitId.Value, Is.EqualTo(i + 1L));

                UnitSnapshot unit = Task03.UnitOf(sim.CurrentSnapshot, expectedUnitId);
                Assert.That(unit.DefinitionId, Is.EqualTo(orderedSlots[i].DefinitionId.Value));
                Assert.That(unit.FactionId, Is.EqualTo(orderedSlots[i].FactionId.Value));
                Assert.That(unit.X, Is.EqualTo(orderedSlots[i].InitialPosition.X));
                Assert.That(unit.Y, Is.EqualTo(orderedSlots[i].InitialPosition.Y));
            }

            Assert.That(sim.CurrentSnapshot.NextUnitId, Is.EqualTo(orderedSlots.Length + 1L));

            // 分配不依赖槽位输入枚举顺序：倒序槽位必须得到同样的 UnitId。
            var reversed = encounter with
            {
                Slots = encounter.Slots.Reverse().ToList(),
                Controllers = encounter.Controllers.Reverse().ToList()
            };
            var reversedDefinition = Task03.Definition with
            {
                Encounters = new List<ProjectHero.Logic.Definitions.EncounterDefinition> { reversed }
            };
            var reversedSim = BattleSimulation.Create(reversedDefinition, Task03.EncounterId, Task03.Inputs);
            Assert.That(Task03.UnitSignature(reversedSim.CurrentSnapshot),
                Is.EqualTo(Task03.UnitSignature(sim.CurrentSnapshot)));
            Assert.That(reversedSim.InitialStateHash, Is.EqualTo(sim.InitialStateHash),
                "槽位/控制者枚举顺序不得改变初始状态哈希");
        }

        [Test]
        public void RuntimeIdsStartAtOneAndAreNeverReused()
        {
            var generator = new LogicIdGenerator();
            Assert.That(generator.NextUnitId().Value, Is.EqualTo(1L));
            Assert.That(generator.NextUnitId().Value, Is.EqualTo(2L));
            Assert.That(generator.NextUnitId().Value, Is.EqualTo(3L));
            Assert.That(generator.NextUnitIdValue, Is.EqualTo(4L));

            Assert.That(generator.NextActionPlanId().Value, Is.EqualTo(1L));
            Assert.That(generator.NextActionPlanId().Value, Is.EqualTo(2L));
            Assert.That(generator.NextWindowId().Value, Is.EqualTo(1L));
            Assert.That(generator.NextEffectId().Value, Is.EqualTo(1L));
            Assert.That(generator.NextReactionOpportunityId().Value, Is.EqualTo(1L));

            // 运行时：初始单位用掉 1、2；窗口打开只推进 WindowId，不回收也不复用 UnitId。
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                turnWindowSchedule: new ScriptedWindowSchedule { OpenTick = 1L, OwnerUnitId = 2L }));
            Task03.StepEmpty(sim);
            StepResult opened = Task03.StepEmpty(sim);

            Assert.That(opened.Snapshot.NextUnitId, Is.EqualTo(3L), "UnitId 计数器不得因窗口打开而回退或复用");
            Assert.That(opened.Snapshot.NextWindowId, Is.EqualTo(2L));
            Assert.That(opened.Snapshot.WindowManager.CurrentWindowId, Is.EqualTo(1L));

            // 单位 ID 集合始终是从 1 起的连续前缀（不复用、无空洞）。
            Assert.That(Task03.UnitOf(opened.Snapshot, new UnitId(1L)).UnitId, Is.EqualTo(1L));
            Assert.That(Task03.UnitOf(opened.Snapshot, new UnitId(2L)).UnitId, Is.EqualTo(2L));
        }

        [Test]
        public void ReactionOpportunityIdsHaveIndependentMonotonicGenerator()
        {
            var generator = new LogicIdGenerator();
            Assert.That(generator.NextReactionOpportunityId().Value, Is.EqualTo(1L));
            Assert.That(generator.NextWindowId().Value, Is.EqualTo(1L));
            Assert.That(generator.NextReactionOpportunityId().Value, Is.EqualTo(2L));
            Assert.That(generator.NextReactionOpportunityId().Value, Is.EqualTo(3L));
            Assert.That(generator.NextWindowIdValue, Is.EqualTo(2L), "两类计数器互不影响");

            // 规范化快照把 ReactionOpportunity 计数独立暴露。
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                turnWindowSchedule: new ScriptedWindowSchedule { OpenTick = 0L, OwnerUnitId = 2L }));
            StepResult result = Task03.StepEmpty(sim);
            Assert.That(result.Snapshot.NextReactionOpportunityId, Is.EqualTo(1L));
            Assert.That(result.Snapshot.NextWindowId, Is.EqualTo(2L));
        }

        [Test]
        public void SameSeedAndCommandsProduceSameHashOneHundredTimes()
        {
            string baselineHash = null;
            string baselineEvents = null;

            for (int run = 1; run <= 100; run++)
            {
                var sim = Task03.NewSim();
                var events = new System.Text.StringBuilder();

                for (long tick = 0; tick < 6; tick++)
                {
                    CommandIngressEntry player = Task03.PlayerEntry(sim);
                    CommandIngressEntry ai = Task03.AiEntry(sim);

                    if (tick == 0)
                    {
                        player.Submit(Task03.ScheduleAdd(tick, expectedScheduleRevision: 0L));
                        ai.Submit(Task03.CloseWindow(tick, new WindowId(1L)));
                    }
                    else if (tick == 2)
                    {
                        player.Submit(Task03.Block(tick, 1L));
                        player.Submit(Task03.Dodge(tick, 2L, new GridPoint(0, 0)));
                    }
                    else if (tick == 4)
                    {
                        // 故意注入一个非法键（scope/payload 不匹配），验证拒绝路径也完全可复现。
                        player.Submit(new CommandRequest(
                            tick, new WindowCommandScope(new WindowId(1L)),
                            new ScheduleEditPayload(new ScheduleEditOperation[]
                            {
                                new ScheduleAddOperation(new ActionPlanId(1L), tick)
                            })));
                    }

                    StepResult result = Task03.StepNext(sim);
                    events.Append(Task03.EventSignature(result.Events)).Append(';');
                }

                string hash = sim.CurrentSnapshot.ComputeHashHex();
                if (run == 1)
                {
                    baselineHash = hash;
                    baselineEvents = events.ToString();
                }
                else
                {
                    Assert.That(hash, Is.EqualTo(baselineHash), $"第 {run} 次运行的快照哈希必须完全一致");
                    Assert.That(events.ToString(), Is.EqualTo(baselineEvents), $"第 {run} 次运行的事件流必须完全一致");
                }
            }

            Assert.That(baselineHash, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void FreshSimulationsFromSameInitialInputsProduceSameCanonicalHashes()
        {
            var first = Task03.NewSim();
            var second = Task03.NewSim();

            Assert.That(second.InitialStateHash, Is.EqualTo(first.InitialStateHash));
            Assert.That(second.CurrentSnapshot.ComputeHashHex(), Is.EqualTo(first.CurrentSnapshot.ComputeHashHex()));

            for (int i = 0; i < 3; i++)
            {
                StepResult a = Task03.StepEmpty(first);
                StepResult b = Task03.StepEmpty(second);
                Assert.That(b.SnapshotHashHex, Is.EqualTo(a.SnapshotHashHex));
                Assert.That(Task03.EventSignature(b.Events), Is.EqualTo(Task03.EventSignature(a.Events)));
            }
        }

        [Test]
        public void InitialStateHashMatchesFreshSimulationBeforeFirstStep()
        {
            var sim = Task03.NewSim();

            Assert.That(sim.Tick, Is.EqualTo(-1L), "首个 Step 之前没有已完成的 Tick");
            Assert.That(sim.CurrentSnapshot.ComputeHash(), Is.EqualTo(sim.InitialStateHash),
                "InitialStateHash 必须等于首个 Step 之前的初始规范化哈希");
            Assert.That(sim.InitialStateHashHex, Is.EqualTo(CanonicalHash.ToHex(sim.InitialStateHash)));

            // 另一个新建模拟得到相同初始哈希（Tick 0 重演的前置校验）。
            var fresh = Task03.NewSim();
            Assert.That(fresh.InitialStateHash, Is.EqualTo(sim.InitialStateHash));

            // 推进后当前快照哈希必须与初始哈希不同（历史/入口水位已变化）。
            StepResult result = Task03.StepEmpty(sim);
            Assert.That(result.SnapshotHash, Is.Not.EqualTo(sim.InitialStateHash));
            Assert.That(sim.InitialStateHash, Is.EqualTo(fresh.InitialStateHash), "InitialStateHash 在创建时冻结后不再变化");
        }

        [Test]
        public void CanonicalSnapshotIgnoresInsertionOrder()
        {
            var unitA = new UnitSnapshot(1L, "unit.a", "monster", 0, 0, 9, 100, true, 0, 0L);
            var unitB = new UnitSnapshot(2L, "unit.b", "hero", 2, 0, 3, 200, true, 0, 0L);

            ulong forward = MakeSnapshot(units: new[] { unitA, unitB }).ComputeHash();
            ulong reversed = MakeSnapshot(units: new[] { unitB, unitA }).ComputeHash();
            Assert.That(reversed, Is.EqualTo(forward), "单位集合必须按 UnitId 规范排序");

            // 入口顺序同样不影响摘要（不同注册顺序 → 同一份规范化入口列表）。
            var ingressForward = new CommandIngressRegistrySnapshot(
                new[]
                {
                    new CommandIngressEntrySnapshot("controller.player", 0, 0, "External", 3L, 1L),
                    new CommandIngressEntrySnapshot("controller.enemy_ai", 1, 10, "External", 2L, 1L),
                    new CommandIngressEntrySnapshot("controller.system", 2, 20, "System", 1L, 0L)
                },
                new[] { new CommandIngressTickBucketSnapshot(3L, 1), new CommandIngressTickBucketSnapshot(2L, 0) },
                1L, 0);

            var ingressReordered = new CommandIngressRegistrySnapshot(
                new[]
                {
                    new CommandIngressEntrySnapshot("controller.system", 2, 20, "System", 1L, 0L),
                    new CommandIngressEntrySnapshot("controller.enemy_ai", 1, 10, "External", 2L, 1L),
                    new CommandIngressEntrySnapshot("controller.player", 0, 0, "External", 3L, 1L)
                },
                new[] { new CommandIngressTickBucketSnapshot(2L, 0), new CommandIngressTickBucketSnapshot(3L, 1) },
                1L, 0);

            Assert.That(MakeSnapshot(ingresses: ingressForward).ComputeHash(),
                Is.EqualTo(MakeSnapshot(ingresses: ingressReordered).ComputeHash()));

            // 真实模拟里注册顺序不同的两个入口集合也必须得到相同摘要。
            var simA = Task03.NewSim();
            var simB = Task03.NewSim();
            Assert.That(simB.CurrentSnapshot.ComputeHash(), Is.EqualTo(simA.CurrentSnapshot.ComputeHash()));

            // 桶的待处理数量必须参与哈希（否则未来请求不可观测）。
            var ingressWithBucket = new CommandIngressRegistrySnapshot(
                Array.Empty<CommandIngressEntrySnapshot>(),
                new[] { new CommandIngressTickBucketSnapshot(3L, 2) }, 1L, 0);
            var ingressWithoutBucket = new CommandIngressRegistrySnapshot(
                Array.Empty<CommandIngressEntrySnapshot>(),
                new[] { new CommandIngressTickBucketSnapshot(3L, 1) }, 1L, 0);
            Assert.That(MakeSnapshot(ingresses: ingressWithBucket).ComputeHash(),
                Is.Not.EqualTo(MakeSnapshot(ingresses: ingressWithoutBucket).ComputeHash()));
        }

        [Test]
        public void TimeScaleOrFrameTimingIsNotPartOfLogicInput()
        {
            float originalTimeScale = UnityEngine.Time.timeScale;
            try
            {
                var baseline = Task03.NewSim();
                for (int i = 0; i < 3; i++) Task03.StepEmpty(baseline);
                string baselineHash = baseline.CurrentSnapshot.ComputeHashHex();

                UnityEngine.Time.timeScale = 7.5f;
                System.Threading.Thread.Sleep(20);
                var shifted = Task03.NewSim();
                for (int i = 0; i < 3; i++) Task03.StepEmpty(shifted);
                Assert.That(shifted.CurrentSnapshot.ComputeHashHex(), Is.EqualTo(baselineHash),
                    "帧时间/时间缩放/墙上时钟不得成为逻辑输入");

                // 公开接口不接受 deltaTime/time/frame 参数。
                foreach (MethodInfo method in typeof(BattleSimulation).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (method.IsSpecialName) continue;
                    foreach (ParameterInfo parameter in method.GetParameters())
                    {
                        Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(float)),
                            $"{method.Name}({parameter.Name}) 不得接受浮点帧时间");
                        Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(double)),
                            $"{method.Name}({parameter.Name}) 不得接受浮点帧时间");
                        string name = (parameter.Name ?? string.Empty).ToLowerInvariant();
                        Assert.That(name.Contains("delta") || name.Contains("timescale") || name.Contains("framecount"),
                            Is.False, $"{method.Name}({parameter.Name}) 不得接受帧时间参数");
                    }
                }
            }
            finally
            {
                UnityEngine.Time.timeScale = originalTimeScale;
            }
        }

        private static LogicSnapshot MakeSnapshot(
            long tick = 0L,
            IReadOnlyList<UnitSnapshot> units = null,
            long scheduleRevision = 0L,
            RngSnapshot rng = null,
            long nextUnitId = 3L,
            long nextActionPlanId = 1L,
            long nextReactionOpportunityId = 1L,
            long nextWindowId = 1L,
            long nextEffectId = 1L,
            CommandIngressRegistrySnapshot ingresses = null,
            HistorySummary history = null,
            BattleEndSnapshot battleEnd = null)
            => new LogicSnapshot(
                tick,
                "battle-def-v1",
                "a10fcfb98357418c",
                Task03.EncounterId.Value,
                battleEnd ?? BattleEndSnapshot.Active(),
                units ?? Array.Empty<UnitSnapshot>(),
                Array.Empty<StatusEffectSnapshot>(),
                TurnWindowManagerSnapshot.None(),
                ConcurrentActionSnapshot.None(),
                BattleResourceSnapshot.None(0),
                scheduleRevision,
                Array.Empty<ActionPlanSnapshot>(),
                Array.Empty<ReactionOpportunitySnapshot>(),
                Array.Empty<ActorLaneSnapshot>(),
                Array.Empty<IntentSnapshot>(),
                Array.Empty<MovementSegmentSnapshot>(),
                Array.Empty<ReservationSnapshot>(),
                Array.Empty<AiControllerSnapshot>(),
                ingresses,
                rng ?? new RngSnapshot(DeterministicRng.AlgorithmVersion, Task03.Inputs.InitialRngSeed),
                nextUnitId,
                nextActionPlanId,
                nextReactionOpportunityId,
                nextWindowId,
                nextEffectId,
                1L,
                1L,
                1L,
                1L,
                1L,
                history ?? HistorySummary.Empty(),
                CommandSourcePriority.MappingVersion);

        private static IEnumerable<byte> LittleEndian(long value)
        {
            ulong raw = unchecked((ulong)value);
            for (int i = 0; i < 8; i++) yield return (byte)(raw >> (8 * i));
        }

        [Test]
        public void CanonicalSnapshotFieldSemanticsAreFrozen()
        {
            // RulesVersion / BattleDefinitionHash / EncounterId / 来源优先级映射版本都参与哈希。
            ulong baseline = MakeSnapshot().ComputeHash();
            Assert.That(baseline.ToString("x16", CultureInfo.InvariantCulture).Length, Is.EqualTo(16));

            var withDifferentEncounter = new LogicSnapshot(
                0L, "battle-def-v1", "a10fcfb98357418c", "encounter.other",
                BattleEndSnapshot.Active(), Array.Empty<UnitSnapshot>(), Array.Empty<StatusEffectSnapshot>(),
                TurnWindowManagerSnapshot.None(), ConcurrentActionSnapshot.None(), BattleResourceSnapshot.None(0),
                0L, Array.Empty<ActionPlanSnapshot>(), Array.Empty<ReactionOpportunitySnapshot>(),
                Array.Empty<ActorLaneSnapshot>(), Array.Empty<IntentSnapshot>(), Array.Empty<MovementSegmentSnapshot>(),
                Array.Empty<ReservationSnapshot>(), Array.Empty<AiControllerSnapshot>(), null,
                new RngSnapshot(DeterministicRng.AlgorithmVersion, 1UL),
                3L, 1L, 1L, 1L, 1L, 1L, 1L, 1L, 1L, 1L, HistorySummary.Empty(),
                CommandSourcePriority.MappingVersion);
            Assert.That(withDifferentEncounter.ComputeHash(), Is.Not.EqualTo(baseline));
        }
    }
}
