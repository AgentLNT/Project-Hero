using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Authoring.Tests.Task03
{
    /// <summary>
    /// 任务 03 必需测试：显式生命周期边界（Create / Step* / Stop / Dispose）、不可变结果、
    /// 唯一 FactionRelationResolver、决策快照与"无 Unity 生命周期/帧时间/视图回写"。
    /// </summary>
    public class SimulationLifecycleTests
    {
        [Test]
        public void SimulationCreationUsesTask02BDefinitionAndRuntimeInputs()
        {
            var sim = Task03.NewSim();

            Assert.That(sim.RulesVersion, Is.EqualTo(Task03.Definition.RulesVersion));
            Assert.That(sim.BattleDefinitionHash, Is.EqualTo(Task03.Definition.BattleDefinitionHashValue));
            Assert.That(sim.EncounterId, Is.EqualTo(Task03.EncounterId));
            Assert.That(Task03.Definition.BattleDefinitionHashValue, Is.EqualTo("a10fcfb98357418c"),
                "任务 02B 的主战斗定义哈希未漂移");

            // 唯一公开创建入口只接受 (BattleDefinition, EncounterDefinitionId, BattleRuntimeInputs)。
            MethodInfo create = typeof(BattleSimulation).GetMethod(
                "Create", new[] { typeof(BattleDefinition), typeof(EncounterDefinitionId), typeof(ProjectHero.Logic.Initialization.BattleRuntimeInputs) });
            Assert.That(create, Is.Not.Null);
            Assert.That(create.IsStatic, Is.True);

            // 运行时输入进入快照（局外初始资源）与 RNG。
            Assert.That(sim.CurrentSnapshot.Resources.MetaResource, Is.EqualTo(Task03.Inputs.InitialMetaResource));
            Assert.That(sim.CurrentSnapshot.Rng.State, Is.EqualTo(Task03.Inputs.InitialRngSeed));

            // 单位来自定义中的槽位（数量、定义、阵营、位置全部一致）。
            Assert.That(sim.CurrentSnapshot.Units.Count, Is.EqualTo(Task03.Encounter.Slots.Count));
            foreach (var slot in Task03.Encounter.Slots)
            {
                UnitSnapshot unit = sim.CurrentSnapshot.Units.Single(u => u.DefinitionId == slot.DefinitionId.Value);
                Assert.That(unit.FactionId, Is.EqualTo(slot.FactionId.Value));
                Assert.That(unit.X, Is.EqualTo(slot.InitialPosition.X));
                Assert.That(unit.Y, Is.EqualTo(slot.InitialPosition.Y));
                Assert.That(unit.HealthQ10, Is.GreaterThan(0));
            }

            // 未知 Encounter 必须稳定拒绝，不得建立空世界。
            var unknown = Assert.Throws<LogicDefinitionException>(() => BattleSimulation.Create(
                Task03.Definition, new EncounterDefinitionId("encounter.does_not_exist"), Task03.Inputs));
            Assert.That(unknown.ErrorCode, Is.EqualTo(DefinitionCodes.ENCOUNTER_NOT_FOUND));
        }

        [Test]
        public void SimulationCreatesOneImmutableFactionRelationResolverFromDefinition()
        {
            var observer = new RecordingDecisionObserver();
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                decisionObservers: new IDecisionObserver[] { observer }));

            StepResult result = Task03.StepNext(sim);

            // 整场只有一个只读解析器实例：决策快照持有的就是它本身（不是副本）。
            Assert.That(observer.Captured, Is.Not.Null);
            Assert.That(ReferenceEquals(observer.Captured.FactionResolver, sim.FactionResolver), Is.True,
                "Decision API 必须复用整场唯一的只读 FactionRelationResolver");

            // 解析器只有一个只读查询面，没有任何设置矩阵/修改关系的成员。
            var members = typeof(IFactionRelationResolver)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.MemberType == MemberTypes.Method || m.MemberType == MemberTypes.Property)
                .Select(m => m.Name)
                .Where(n => !n.StartsWith("get_"))
                .ToArray();
            Assert.That(members, Is.EquivalentTo(new[] { "Classify", "Allows" }));
            Assert.That(typeof(IFactionRelationResolver).GetProperties().Any(p => p.CanWrite), Is.False);

            // 与定义矩阵一致：hero↔monster 是 Hostile，同阵营 Allied，自身 Self。
            Assert.That(sim.FactionResolver.Classify(Task03.HeroUnitId, Task03.EnemyUnitId), Is.EqualTo(UnitRelation.Hostile));
            Assert.That(sim.FactionResolver.Classify(Task03.HeroUnitId, Task03.HeroUnitId), Is.EqualTo(UnitRelation.Self));
            Assert.That(sim.FactionResolver.Allows(TargetRelationMask.Hostile, Task03.HeroUnitId, Task03.EnemyUnitId), Is.True);
            Assert.That(sim.FactionResolver.Allows(TargetRelationMask.Allied, Task03.HeroUnitId, Task03.EnemyUnitId), Is.False);
            Assert.That(result.Snapshot.Units.All(u => !string.IsNullOrEmpty(u.FactionId)), Is.True);
        }

        [Test]
        public void DecisionSnapshotFactionQueriesMatchCanonicalDefinition()
        {
            var observer = new RecordingDecisionObserver();
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                decisionObservers: new IDecisionObserver[] { observer }));
            Task03.StepNext(sim);

            DecisionSnapshot decision = observer.Captured;
            var model = Task03.Definition.FactionModel;

            // 对全部单位对比较"定义矩阵 → 期望关系"与 DecisionSnapshot 的只读查询结果。
            foreach (UnitSnapshot source in decision.VisibleUnits)
            {
                foreach (UnitSnapshot target in decision.VisibleUnits)
                {
                    UnitRelation expected;
                    if (source.UnitId == target.UnitId)
                    {
                        expected = UnitRelation.Self;
                    }
                    else if (string.Equals(source.FactionId, target.FactionId, StringComparison.Ordinal))
                    {
                        expected = UnitRelation.Allied;
                    }
                    else
                    {
                        Assert.That(model.TryGetDisposition(new FactionId(source.FactionId), new FactionId(target.FactionId),
                            out FactionDisposition disposition), Is.True, "矩阵必须完整声明每个不同阵营对");
                        expected = disposition == FactionDisposition.Hostile ? UnitRelation.Hostile
                            : disposition == FactionDisposition.Allied ? UnitRelation.Allied
                            : UnitRelation.Neutral;
                    }

                    UnitRelation actual = decision.Classify(new UnitId(source.UnitId), new UnitId(target.UnitId));
                    Assert.That(actual, Is.EqualTo(expected),
                        $"单位 {source.UnitId}->{target.UnitId} 的关系必须与定义矩阵一致");

                    foreach (TargetRelationMask mask in new[]
                    {
                        TargetRelationMask.Self, TargetRelationMask.Allied,
                        TargetRelationMask.Neutral, TargetRelationMask.Hostile,
                        TargetRelationMask.Self | TargetRelationMask.Hostile
                    })
                    {
                        bool expectedAllows = (mask & FactionRelationResolver.ToMask(expected)) != 0;
                        Assert.That(decision.Allows(mask, new UnitId(source.UnitId), new UnitId(target.UnitId)),
                            Is.EqualTo(expectedAllows), $"Allows({mask}) 必须与定义一致");
                    }
                }
            }

            // 未知单位必须显式失败，不得返回默认关系。
            Assert.Throws<LogicDefinitionException>(() => decision.Classify(new UnitId(99L), Task03.HeroUnitId));

            // Canonical 与 Decision 分型：决策快照不是规范化快照本身。
            Assert.That(decision.GetType(), Is.Not.EqualTo(typeof(LogicSnapshot)));
            Assert.That(typeof(LogicSnapshot).GetMethods().Any(m => m.Name.Contains("Decision")), Is.False);
        }

        [Test]
        public void ControllerRegistrationCannotMutateUnitFactionOrRelations()
        {
            var sim = Task03.NewSim();
            StepResult before = Task03.StepNext(sim);

            string[] factionsBefore = before.Snapshot.Units.Select(u => u.FactionId).ToArray();
            UnitRelation relationBefore = sim.FactionResolver.Classify(Task03.HeroUnitId, Task03.EnemyUnitId);

            // 注册一个新控制者（把 hero 槽位也交给另一个 AI 控制者）：控制权变化不得影响阵营/关系。
            CommandIngressEntry extra = sim.CommandIngress.RegisterExternalEntry(new ControllerBinding(
                new ControllerId("controller.extra"),
                CommandSourceKind.Ai,
                new[] { new EncounterSlotId(Task03.HeroSlot) }));

            Assert.That(extra.SourceKind, Is.EqualTo(CommandSourceKind.Ai));
            Assert.That(extra.SourcePriority, Is.EqualTo(CommandSourcePriority.Ai));

            StepResult after = Task03.StepNext(sim);
            string[] factionsAfter = after.Snapshot.Units.Select(u => u.FactionId).ToArray();

            Assert.That(factionsAfter, Is.EqualTo(factionsBefore), "控制入口注册不得覆盖或派生阵营");
            Assert.That(sim.FactionResolver.Classify(Task03.HeroUnitId, Task03.EnemyUnitId), Is.EqualTo(relationBefore));
            Assert.That(sim.FactionResolver.Classify(Task03.HeroUnitId, Task03.HeroUnitId), Is.EqualTo(UnitRelation.Self));
            Assert.That(after.Snapshot.CommandIngresses.Entries.Count, Is.EqualTo(4));

            // ControllerBinding 结构上就不携带阵营字段。
            Assert.That(typeof(ControllerBinding).GetProperties().Any(p => p.Name.Contains("Faction")), Is.False);
        }

        [Test]
        public void StepCanBeDrivenByExplicitCallerWithoutUnityLifecycle()
        {
            var sim = Task03.NewSim();

            // 纯 C# 显式驱动：没有任何 MonoBehaviour/Update/协程参与。
            for (int i = 0; i < 6; i++)
            {
                StepResult result = Task03.StepNext(sim);
                Assert.That(result.Status, Is.EqualTo(StepStatus.Advanced));
                Assert.That(result.Snapshot.Tick, Is.EqualTo(i));
            }

            Assert.That(sim.Tick, Is.EqualTo(5L));
            Assert.That(typeof(BattleSimulation).GetMethod("Update", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
            Assert.That(typeof(BattleSimulation).GetMethod("LateUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
            Assert.That(typeof(BattleSimulation).GetMethod("FixedUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
            Assert.That(typeof(BattleSimulation).GetMethod("StartCoroutine", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
            Assert.That(typeof(BattleSimulation).GetInterfaces().Contains(typeof(System.Collections.IEnumerator)), Is.False);
        }

        [Test]
        public void StepResultExposesOnlyImmutableEventsAndSnapshot()
        {
            var sim = Task03.NewSim();
            Task03.PlayerEntry(sim).Submit(Task03.ScheduleAdd(0L));
            StepResult result = Task03.StepNext(sim);

            var properties = typeof(StepResult).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).ToArray();
            Assert.That(properties, Is.EquivalentTo(new[]
            {
                "Status", "Events", "Snapshot", "IsAdvanced", "IsBattleEnded", "IsAlreadyEnded",
                "Tick", "SnapshotHash", "SnapshotHashHex"
            }), "StepResult 只暴露状态、只读事件与规范化快照");
            Assert.That(typeof(StepResult).GetProperties().Any(p => p.CanWrite), Is.False);

            // 事件批次与快照集合都是只读集合：既不能改写也不能通过别名注入。
            var events = (IList<LogicEvent>)result.Events.Events;
            Assert.That(events.IsReadOnly, Is.True);
            Assert.Throws<NotSupportedException>(() => events.Add(null));

            var units = (IList<UnitSnapshot>)result.Snapshot.Units;
            Assert.That(units.IsReadOnly, Is.True);
            Assert.Throws<NotSupportedException>(() => units.Add(null));

            var ingressEntries = (IList<CommandIngressEntrySnapshot>)result.Snapshot.CommandIngresses.Entries;
            Assert.That(ingressEntries.IsReadOnly, Is.True);

            // StepResult 不暴露逻辑内部集合或写接口。
            Assert.That(typeof(StepResult).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => m.Name.Contains("Set") || m.Name.Contains("Write") || m.Name.Contains("Add")), Is.False);
        }

        [Test]
        public void DisposeIsIdempotentAndStepAfterDisposeIsRejected()
        {
            var sim = Task03.NewSim();
            Task03.StepNext(sim);

            sim.Dispose();
            sim.Dispose();
            Assert.That(sim.IsDisposed, Is.True);

            long frozenThrough = sim.CommandIngress.FrozenThroughTick;
            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(frozenThrough + 1L);

            var disposed = Assert.Throws<LogicDefinitionException>(() => sim.Step(frozenThrough + 1L, batch));
            Assert.That(disposed.ErrorCode, Is.EqualTo(SimulationCodes.SIMULATION_DISPOSED));
            Assert.That(sim.Tick, Is.EqualTo(0L), "释放后 Step 必须明确拒绝且不推进任何状态");

            // 重复停止幂等；释放后的停止请求是无操作。
            sim.RequestStop("IGNORED_AFTER_DISPOSE");
            Assert.That(sim.IsStopRequested, Is.False);
            Assert.That(sim.IsEnded, Is.False);

            // 释放后入口不再接受新命令。
            CommandIngressRejection rejection = Task03.PlayerEntry(sim).Submit(Task03.ScheduleAdd(2L));
            Assert.That(rejection, Is.Not.Null);
            Assert.That(rejection.ReasonCode, Is.EqualTo(CommandCodes.BATTLE_ALREADY_ENDED));
        }

        [Test]
        public void AlreadyEndedStepReturnsEmptyEventsAndSameFinalSnapshot()
        {
            var sim = Task03.NewSim();
            Task03.PlayerEntry(sim).Submit(Task03.ScheduleAdd(0L));
            Task03.StepNext(sim);

            sim.RequestStop("FINAL_CODE");
            StepResult ended = Task03.StepNext(sim);
            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded));
            Assert.That(sim.IsEnded, Is.True);
            Assert.That(sim.FinalSnapshot, Is.SameAs(ended.Snapshot));
            Assert.That(sim.FinalizerReport, Is.Not.Null);
            Assert.That(sim.FinalizerReport.EndedAtTick, Is.EqualTo(1L));
            Assert.That(sim.FinalizerReport.ResultCode, Is.EqualTo("FINAL_CODE"));
            Assert.That(sim.FinalizerReport.AlreadyFinalized, Is.False);

            // 结束后误调用：返回空事件批次与同一最终快照（同一个实例）。
            StepResult again = sim.Step(99L, sim.CommandIngress.FreezeTick(99L));
            Assert.That(again.Status, Is.EqualTo(StepStatus.AlreadyEnded));
            Assert.That(again.Events.Count, Is.EqualTo(0));
            Assert.That(again.Snapshot, Is.SameAs(ended.Snapshot));
            Assert.That(again.Tick, Is.EqualTo(1L), "结束后不采纳传入 Tick");

            // 第二次结束后的 Stop 幂等（结果码以第一次为准）。
            sim.RequestStop("SECOND_CODE");
            StepResult third = sim.Step(100L, sim.CommandIngress.FreezeTick(100L));
            Assert.That(third.Snapshot.BattleEnd.ResultCode, Is.EqualTo("FINAL_CODE"));
        }

        [Test]
        public void AlreadyEndedStepDoesNotAdvanceTickRngIdsOrSequences()
        {
            var sim = Task03.NewSim();
            Task03.PlayerEntry(sim).Submit(Task03.ScheduleAdd(0L));
            Task03.StepNext(sim);
            sim.RequestStop();
            StepResult ended = Task03.StepNext(sim);

            LogicSnapshot before = ended.Snapshot;
            long tickBefore = sim.Tick;

            for (int i = 0; i < 3; i++)
            {
                FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(sim.CommandIngress.FrozenThroughTick + 1L);
                StepResult again = sim.Step(200L + i, batch);
                Assert.That(again.Status, Is.EqualTo(StepStatus.AlreadyEnded));
                Assert.That(again.Events.Count, Is.EqualTo(0));
            }

            Assert.That(sim.Tick, Is.EqualTo(tickBefore), "结束后不推进 Tick");
            Assert.That(sim.History.RecordCount, Is.EqualTo(before.History.RecordCount), "结束后不再追加归档");
            Assert.That(sim.History.Digest, Is.EqualTo(before.History.Digest));
            Assert.That(sim.FinalSnapshot.Rng.State, Is.EqualTo(before.Rng.State), "结束后不推进 RNG");
            Assert.That(sim.FinalSnapshot.NextUnitId, Is.EqualTo(before.NextUnitId), "结束后不推进 ID");
            Assert.That(sim.FinalSnapshot.NextActionPlanId, Is.EqualTo(before.NextActionPlanId));
            Assert.That(sim.FinalSnapshot.NextWindowId, Is.EqualTo(before.NextWindowId));
            Assert.That(sim.FinalSnapshot.NextCommandSequence, Is.EqualTo(before.NextCommandSequence), "结束后不推进 Sequence");
            Assert.That(sim.FinalSnapshot.NextEventSequence, Is.EqualTo(before.NextEventSequence));
            Assert.That(sim.FinalSnapshot.NextIntentSequence, Is.EqualTo(before.NextIntentSequence));
            Assert.That(sim.FinalSnapshot.NextResolutionSequence, Is.EqualTo(before.NextResolutionSequence));
            Assert.That(sim.FinalSnapshot.NextEffectSequence, Is.EqualTo(before.NextEffectSequence));

            // 结束后误调用不重复发事件。
            Assert.That(sim.FinalSnapshot.ComputeHashHex(), Is.EqualTo(before.ComputeHashHex()));
        }

        [Test]
        public void SimulationHasNoUnityTimeOrViewWritebackDependency()
        {
            var logicAssembly = typeof(BattleSimulation).Assembly;
            Assert.That(logicAssembly.GetReferencedAssemblies().Select(a => a.Name),
                Has.None.Matches("^UnityEngine(\\..*)?"));

            var simulationTypes = logicAssembly.GetTypes()
                .Where(t => (t.Namespace ?? string.Empty).StartsWith("ProjectHero.Logic.Simulation", StringComparison.Ordinal))
                .ToArray();
            Assert.That(simulationTypes.Length, Is.GreaterThan(0));

            string[] unityLifecycleNames =
            {
                "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy",
                "StartCoroutine", "StopCoroutine", "Invoke", "InvokeRepeating"
            };

            foreach (Type type in simulationTypes)
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                              BindingFlags.Instance | BindingFlags.Static |
                                                              BindingFlags.DeclaredOnly))
                {
                    Assert.That(unityLifecycleNames.Contains(method.Name), Is.False,
                        $"{type.Name}.{method.Name} 不得实现 Unity 生命周期/定时器入口");

                    Assert.That((method.ReturnType.Namespace ?? string.Empty).StartsWith("UnityEngine", StringComparison.Ordinal),
                        Is.False, $"{type.Name}.{method.Name} 不得返回 Unity 类型");
                    foreach (ParameterInfo parameter in method.GetParameters())
                    {
                        Assert.That((parameter.ParameterType.Namespace ?? string.Empty).StartsWith("UnityEngine", StringComparison.Ordinal),
                            Is.False, $"{type.Name}.{method.Name}({parameter.Name}) 不得接受 Unity 类型");
                    }
                }

                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                                           BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    Assert.That((field.FieldType.Namespace ?? string.Empty).StartsWith("UnityEngine", StringComparison.Ordinal),
                        Is.False, $"{type.Name}.{field.Name} 不得持有 Unity 对象引用");
                }

                // 不接受回调式表现依赖：Step 的公开面里没有委托类型参数。
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    foreach (ParameterInfo parameter in method.GetParameters())
                    {
                        Assert.That(typeof(Delegate).IsAssignableFrom(parameter.ParameterType), Is.False,
                            $"{type.Name}.{method.Name}({parameter.Name}) 不得接受回调式表现依赖");
                    }
                }
            }

            // 没有 Update()/协程/定时器，也没有自行读取 Unity 帧时间。
            Assert.That(typeof(System.Threading.Timer).IsAssignableFrom(typeof(BattleSimulation)), Is.False);
            Assert.That(typeof(BattleSimulation).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(m => m.Name.Contains("Timer") || m.Name.Contains("Coroutine")), Is.False);
        }

        [Test]
        public void NoSimulationStateRestoreOrTickJumpApiExists()
        {
            var logicAssembly = typeof(BattleSimulation).Assembly;

            Assert.That(logicAssembly.GetExportedTypes().Any(t => t.Name == "SimulationState"), Is.False,
                "不得新增 SimulationState");
            Assert.That(logicAssembly.GetExportedTypes().Any(t => t.Name == "BattleSimulationState"), Is.False);

            foreach (MethodInfo method in typeof(BattleSimulation).GetMethods(
                         BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                Assert.That(new[] { "Restore", "Load", "FromSnapshot", "Deserialize", "JumpTo", "Seek", "Checkpoint" }
                        .Contains(method.Name), Is.False,
                    $"BattleSimulation.{method.Name} 不得提供恢复/跳转/检查点入口");
                Assert.That(method.GetParameters().Any(p => p.ParameterType == typeof(LogicSnapshot) ||
                                                            p.ParameterType == typeof(DecisionSnapshot)), Is.False,
                    $"BattleSimulation.{method.Name} 不得接受快照作为输入");
            }

            Assert.That(typeof(LogicSnapshot).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => m.ReturnType == typeof(BattleSimulation)), Is.False,
                "LogicSnapshot 不得能被用来重建模拟");
        }

        [Test]
        public void DecisionSnapshotDeliversAiRequestsOnlyToNextTick()
        {
            var observer = new RecordingDecisionObserver
            {
                SubmitTargetTick = 1L,
                SubmitControllerId = Task03.EnemyAiController,
                SubmitOnce = true
            };
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                decisionObservers: new IDecisionObserver[] { observer }));

            StepResult tickZero = Task03.StepNext(sim);
            Assert.That(observer.ObservedCount, Is.EqualTo(1));
            Assert.That(observer.Captured.Tick, Is.EqualTo(0L));
            Assert.That(ReferenceEquals(sim.LastDecisionSnapshot, observer.Captured), Is.True,
                "AI 与玩家读取同一个只读决策快照实例");
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(0), "AI 请求不得回写当前 Tick");
            Assert.That(tickZero.Snapshot.NextCommandSequence, Is.EqualTo(1L));
            Assert.That(tickZero.Snapshot.CommandIngresses.FutureBuckets.Single().TargetTick, Is.EqualTo(1L));

            StepResult tickOne = Task03.StepNext(sim);
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(1),
                "AI 只经注册入口投递到下一 Tick");
            Assert.That(sim.LastCommandSet.Envelopes[0].ControllerId.Value, Is.EqualTo(Task03.EnemyAiController));
            Assert.That(sim.LastCommandSet.Envelopes[0].SourcePriority, Is.EqualTo(CommandSourcePriority.Ai));
            Assert.That(tickOne.Snapshot.CommandIngresses.Entries
                .Single(e => e.ControllerId == Task03.EnemyAiController).FrozenProducerOrdinal, Is.EqualTo(1L));

            // 玩家看到的是同一份决策快照数据（同一实例、同一 Tick）。
            Assert.That(observer.Captured.Tick, Is.EqualTo(1L));
        }

        [Test]
        public void StepPhaseTraceIsDiagnosticOnlyAndNotHashed()
        {
            var sim = Task03.NewSim();
            StepResult withTrace = Task03.StepNext(sim);
            var plain = Task03.NewSim();
            StepResult withoutTrace = Task03.StepNext(plain);

            Assert.That(sim.LastStepPhaseTrace.Count, Is.EqualTo(plain.LastStepPhaseTrace.Count));
            Assert.That(withTrace.SnapshotHashHex, Is.EqualTo(withoutTrace.SnapshotHashHex),
                "阶段轨迹是诊断信息，不进入规范化哈希");
            Assert.That(withTrace.Snapshot.GetType().GetProperties().Any(p => p.Name.Contains("PhaseTrace")), Is.False);
        }
    }
}
