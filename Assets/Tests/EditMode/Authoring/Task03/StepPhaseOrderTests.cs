using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Authoring.Tests.Task03
{
    /// <summary>
    /// 任务 03 必需测试：20 个冻结阶段的顺序、阶段槽位互不合并，以及关键顺序不变量
    /// （状态到期/排程编辑先于启动门禁、启动门禁先于 Intent、批量换位先于状态控制与死亡）。
    /// </summary>
    public class StepPhaseOrderTests
    {
        [Test]
        public void FrozenStepPhaseOrderIsExplicitAndMatchesExecution()
        {
            Assert.That(StepPhaseExtensions.FrozenOrder.Count, Is.EqualTo(StepPhaseExtensions.PhaseCount));
            Assert.That(StepPhaseExtensions.PhaseCount, Is.EqualTo(20));

            // 枚举顺序 = 冻结顺序（契约可从代码直接识别）。
            for (int i = 0; i < StepPhaseExtensions.FrozenOrder.Count; i++)
            {
                Assert.That((int)StepPhaseExtensions.FrozenOrder[i], Is.EqualTo(i),
                    $"阶段 {i} 必须是 {StepPhaseExtensions.FrozenOrder[i]}");
            }

            var sim = Task03.NewSim();
            Task03.StepEmpty(sim);

            var executed = sim.LastStepPhaseTrace.Select(e => e.Phase).ToArray();
            Assert.That(executed, Is.EqualTo(StepPhaseExtensions.FrozenOrder.ToArray()),
                "正常 Tick 必须按冻结顺序执行全部 20 个阶段，每个阶段恰好一次");
            Assert.That(sim.LastStepPhaseTrace.Select(e => e.Order).ToArray(),
                Is.EqualTo(Enumerable.Range(0, 20).ToArray()));
            Assert.That(sim.DescribeLastStepPhases(), Does.StartWith(nameof(StepPhase.PreCommandBoundaries)));
        }

        [Test]
        public void ScheduledWindowOpenRunsAfterPreCommandDeathAndVictory()
        {
            // 1. 控制用例：无死亡时窗口在阶段 3 打开，且状态推进/死亡胜负阶段先于它。
            var schedule = new ScriptedWindowSchedule { OpenTick = 1L, OwnerUnitId = Task03.HeroUnitId.Value };
            var control = Task03.NewSim(new BattleSimulationAssembly(turnWindowSchedule: schedule));
            Task03.StepEmpty(control);
            StepResult opened = Task03.StepEmpty(control);

            Assert.That(control.LastStepPhaseTrace.Select(e => e.Phase).ToList().IndexOf(StepPhase.StateAndEffectAdvance),
                Is.LessThan(control.LastStepPhaseTrace.Select(e => e.Phase).ToList().IndexOf(StepPhase.DeathAndVictory)));
            Assert.That(control.LastStepPhaseTrace.Select(e => e.Phase).ToList().IndexOf(StepPhase.DeathAndVictory),
                Is.LessThan(control.LastStepPhaseTrace.Select(e => e.Phase).ToList().IndexOf(StepPhase.WindowOpen)),
                "窗口打开严格晚于命令前的死亡与胜负阶段");

            var windowEvents = Task03.EventsOfType<TurnWindowOpenedEvent>(opened.Events)
                .Cast<TurnWindowOpenedEvent>().ToList();
            Assert.That(windowEvents.Count, Is.EqualTo(1));
            Assert.That(windowEvents[0].OwnerUnitId.Value, Is.EqualTo(Task03.HeroUnitId.Value));
            Assert.That(opened.Snapshot.WindowManager.CurrentWindowId, Is.EqualTo(1L));
            Assert.That(Task03.UnitOf(opened.Snapshot, Task03.HeroUnitId).AdrenalineCycleId, Is.EqualTo(1L),
                "自身窗口打开时递增个人周期并清零 Available");

            // 2. 死亡先发生：拥有者所属敌对阵营全灭 → 战斗在阶段 2 结束，本 Tick 不得打开窗口。
            var killing = new KillOnTickAdvanceSystem
            {
                KillTick = 1L,
                KillUnitId = Task03.EnemyUnitId.Value,
                KillHealthQ10 = 0
            };
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: killing,
                turnWindowSchedule: new ScriptedWindowSchedule { OpenTick = 1L, OwnerUnitId = Task03.HeroUnitId.Value }));

            Task03.StepEmpty(sim);
            StepResult ended = Task03.StepEmpty(sim);

            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded));
            Assert.That(sim.LastStepExecuted(StepPhase.DeathAndVictory), Is.True);
            Assert.That(sim.LastStepExecuted(StepPhase.WindowOpen), Is.False,
                "战斗已在阶段 2 结束：阶段 3 的窗口打开不得执行");
            Assert.That(Task03.EventsOfType<TurnWindowOpenedEvent>(ended.Events).Count, Is.EqualTo(0),
                "待打开窗口的拥有者若在前述阶段死亡/战斗已结束，不得产生打开事件");
            Assert.That(ended.Snapshot.NextWindowId, Is.EqualTo(1L), "不得消耗 WindowId");
            Assert.That(Task03.EventsOfType<UnitDiedEvent>(ended.Events).Count, Is.EqualTo(1));
            Assert.That(ended.Snapshot.BattleEnd.ResultCode, Is.EqualTo(Task03.Encounter.Victory.VictoryResultCode));
        }

        [Test]
        public void StateExpiryPhasePrecedesDuePlanStartGatePhase()
        {
            // 1. 阶段 1 写入的生命清零在同 Tick 的阶段 2 就被死亡系统读取（行为证据）。
            var killing = new KillOnTickAdvanceSystem
            {
                KillTick = 0L,
                KillUnitId = Task03.EnemyUnitId.Value,
                KillHealthQ10 = 0
            };
            var endingSim = Task03.NewSim(new BattleSimulationAssembly(unitStateAdvance: killing));
            StepResult ended = Task03.StepNext(endingSim);

            var endingPhases = endingSim.LastStepPhaseTrace.Select(e => e.Phase).ToList();
            Assert.That(endingPhases.IndexOf(StepPhase.StateAndEffectAdvance),
                Is.LessThan(endingPhases.IndexOf(StepPhase.DeathAndVictory)),
                "状态到期必须先于死亡/胜负");
            Assert.That(Task03.EventsOfType<UnitDiedEvent>(ended.Events).Count, Is.EqualTo(1),
                "阶段 1 清零的生命必须被同 Tick 的阶段 2 读取");
            Assert.That(Task03.UnitOf(ended.Snapshot, Task03.EnemyUnitId).IsAlive, Is.False);

            // 2. 正常 Tick：死亡/胜负阶段严格早于启动门禁（阶段 2 < 阶段 7），二者合起来给出
            //    "状态到期 → 死亡/胜负 → 启动门禁"的完整顺序。
            var normalSim = Task03.NewSim();
            StepResult normal = Task03.StepNext(normalSim);
            var phases = normalSim.LastStepPhaseTrace.Select(e => e.Phase).ToList();
            Assert.That(phases.IndexOf(StepPhase.StateAndEffectAdvance),
                Is.LessThan(phases.IndexOf(StepPhase.DeathAndVictory)));
            Assert.That(phases.IndexOf(StepPhase.DeathAndVictory),
                Is.LessThan(phases.IndexOf(StepPhase.DuePlanStartGateAndReactionTrigger)),
                "状态到期 → 死亡/胜负 → 启动门禁的顺序必须固定");
            Assert.That(normal.Snapshot.Units.All(u => u.IsAlive), Is.True);
        }

        [Test]
        public void TickTargetedScheduleEditPhasePrecedesDuePlanStartGatePhase()
        {
            var sim = Task03.NewSim();
            Task03.PlayerEntry(sim).Submit(Task03.ScheduleAdd(1L, expectedScheduleRevision: 0L));

            Task03.StepEmpty(sim);
            StepResult result = Task03.StepNext(sim);

            Assert.That(result.Snapshot.Tick, Is.EqualTo(1L));
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(1), "目标 Tick=1 的编辑在 Tick 1 的冻结批次中被处理");

            var phases = sim.LastStepPhaseTrace.Select(e => e.Phase).ToList();
            Assert.That(phases.IndexOf(StepPhase.FrozenBatchMergeAndBaseRevision),
                Is.LessThan(phases.IndexOf(StepPhase.CommandValidationAndScheduling)));
            Assert.That(phases.IndexOf(StepPhase.CommandValidationAndScheduling),
                Is.LessThan(phases.IndexOf(StepPhase.DuePlanStartGateAndReactionTrigger)),
                "目标 Tick 的排程编辑必须早于同 Tick 的启动门禁");
            Assert.That(phases.IndexOf(StepPhase.ReactionOpportunityRefresh),
                Is.LessThan(phases.IndexOf(StepPhase.FrozenBatchMergeAndBaseRevision)),
                "命令前只刷新既有机会，随后才合并冻结批次");
            Assert.That(phases.IndexOf(StepPhase.WindowOpen),
                Is.LessThan(phases.IndexOf(StepPhase.CommandValidationAndScheduling)));
        }

        [Test]
        public void DuePlanStartGatePhasePrecedesIntentDrain()
        {
            var sim = Task03.NewSim();
            StepResult result = Task03.StepEmpty(sim);

            var phases = sim.LastStepPhaseTrace.Select(e => e.Phase).ToList();
            Assert.That(phases.IndexOf(StepPhase.DuePlanStartGateAndReactionTrigger),
                Is.LessThan(phases.IndexOf(StepPhase.IntentDrain)),
                "启动门禁/反应 Trigger 必须早于 Intent 取出");
            Assert.That(phases.IndexOf(StepPhase.IntentDrain),
                Is.LessThan(phases.IndexOf(StepPhase.LegacyContactCaptureAndDodgeCommit)),
                "旧格接触捕获发生在 Dodge 提交之前");
            Assert.That(result.Snapshot.Intents.Count, Is.EqualTo(0));
        }

        [Test]
        public void StepProvidesDistinctGlobalForcedDisplacementSolveAndBatchCommitPhases()
        {
            var requests = new ScriptedDisplacementRequests
            {
                TargetUnitId = Task03.HeroUnitId.Value,
                Steps = 1,
                Direction = GridDirection.East,
                MomentumUnits = 5L,
                ConflictGroupKey = 42L
            };
            var solver = new ObservingDisplacementSolver { AppliedSteps = 1, Dx = 2, Dy = 0 };
            var commit = new ObservingResolutionCommit();

            var sim = Task03.NewSim(new BattleSimulationAssembly(
                displacementRequestBuilder: requests,
                displacementSolver: solver,
                resolutionCommit: commit));

            UnitSnapshot before = Task03.UnitOf(sim.CurrentSnapshot, Task03.HeroUnitId);
            StepResult result = Task03.StepNext(sim);
            UnitSnapshot after = Task03.UnitOf(result.Snapshot, Task03.HeroUnitId);

            // 三个槽位是彼此独立的阶段，且求解早于批量提交。
            Assert.That(sim.LastStepExecuted(StepPhase.DamageAggregationAndDisplacementRequests), Is.True);
            Assert.That(sim.LastStepExecuted(StepPhase.ForcedDisplacementSolve), Is.True);
            Assert.That(sim.LastStepExecuted(StepPhase.PlanReservationCleanupAndBatchCommit), Is.True);
            var phases = sim.LastStepPhaseTrace.Select(e => e.Phase).ToList();
            Assert.That(phases.IndexOf(StepPhase.DamageAggregationAndDisplacementRequests),
                Is.LessThan(phases.IndexOf(StepPhase.ForcedDisplacementSolve)));
            Assert.That(phases.IndexOf(StepPhase.ForcedDisplacementSolve),
                Is.LessThan(phases.IndexOf(StepPhase.PlanReservationCleanupAndBatchCommit)));

            // 求解阶段只读：它看到的是未修改的世界。
            Assert.That(solver.ObservedInputs, Does.Contain("0:" + Task03.HeroUnitId.Value + "@" + before.X + "," + before.Y));
            Assert.That(solver.ObservedInputs, Does.Not.Contain("0:" + Task03.HeroUnitId.Value + "@" + (before.X + 2) + "," + before.Y));

            // 提交后位置与事件都是从同一批次一次性写入的。
            Assert.That(after.X, Is.EqualTo(before.X + 2));
            Assert.That(after.Y, Is.EqualTo(before.Y));
            var displacementEvents = Task03.EventsOfType<ForcedDisplacementResolvedEvent>(result.Events)
                .Cast<ForcedDisplacementResolvedEvent>().ToList();
            Assert.That(displacementEvents.Count, Is.EqualTo(1));
            Assert.That(displacementEvents[0].TargetUnitId.Value, Is.EqualTo(Task03.HeroUnitId.Value));
            Assert.That(displacementEvents[0].From, Is.EqualTo(new GridPoint(before.X, before.Y)));
            Assert.That(displacementEvents[0].To, Is.EqualTo(new GridPoint(before.X + 2, before.Y)));
            Assert.That(displacementEvents[0].StopReason, Is.EqualTo(ProjectHero.Logic.Combat.ForcedDisplacementStopReason.Completed));
            Assert.That(displacementEvents[0].ConflictGroupKey, Is.EqualTo(42L));
        }

        [Test]
        public void ForcedDisplacementBatchCommitPrecedesStateControlAndDeathPhases()
        {
            var solver = new ObservingDisplacementSolver { AppliedSteps = 1, Dx = 2, Dy = 0 };
            var commit = new ObservingResolutionCommit();

            var sim = Task03.NewSim(new BattleSimulationAssembly(
                displacementRequestBuilder: new ScriptedDisplacementRequests { TargetUnitId = Task03.HeroUnitId.Value, Steps = 1 },
                displacementSolver: solver,
                resolutionCommit: commit));

            UnitSnapshot before = Task03.UnitOf(sim.CurrentSnapshot, Task03.HeroUnitId);
            StepResult result = Task03.StepNext(sim);
            UnitSnapshot after = Task03.UnitOf(result.Snapshot, Task03.HeroUnitId);

            var phases = sim.LastStepPhaseTrace.Select(e => e.Phase).ToList();
            Assert.That(phases.IndexOf(StepPhase.PlanReservationCleanupAndBatchCommit),
                Is.LessThan(phases.IndexOf(StepPhase.StateControlAndAdrenalineAccrual)));
            Assert.That(phases.IndexOf(StepPhase.StateControlAndAdrenalineAccrual),
                Is.LessThan(phases.IndexOf(StepPhase.PostDisplacementDeath)));
            Assert.That(phases.IndexOf(StepPhase.PostDisplacementDeath),
                Is.LessThan(phases.IndexOf(StepPhase.VictoryReevaluation)));

            // 状态/控制提交（阶段 14）只能看到批量换位<em>之后</em>的最终位置。
            Assert.That(commit.StateControlObservations,
                Does.Contain("0:" + Task03.HeroUnitId.Value + "@" + after.X + "," + after.Y));
            Assert.That(commit.StateControlObservations,
                Does.Not.Contain("0:" + Task03.HeroUnitId.Value + "@" + before.X + "," + before.Y),
                "状态/控制不得提前读取中间位置");

            // 伤害/合力聚合阶段（阶段 11）仍在换位之前。
            Assert.That(commit.DamageObservations,
                Does.Contain("0:" + Task03.HeroUnitId.Value + "@" + before.X + "," + before.Y));
        }

        [Test]
        public void StepInvariantChecksRunBeforeSnapshotAndDoNotMutateState()
        {
            var recording = new RecordingInvariantCheck();
            var sim = Task03.NewSim(new BattleSimulationAssembly(
                invariantChecks: new IStepInvariantCheck[] { recording }));
            recording.Simulation = sim;

            var plain = Task03.NewSim();

            for (int i = 0; i < 3; i++)
            {
                StepResult withCheck = Task03.StepNext(sim);
                StepResult withoutCheck = Task03.StepNext(plain);

                Assert.That(withCheck.SnapshotHashHex, Is.EqualTo(withoutCheck.SnapshotHashHex),
                    "只读不变量检查不得修改逻辑状态");
                Assert.That(Task03.EventSignature(withCheck.Events), Is.EqualTo(Task03.EventSignature(withoutCheck.Events)));
            }

            Assert.That(recording.CheckedTicks, Is.EqualTo(new[] { 0L, 1L, 2L }));
            // 检查发生在快照构建之前：检查时看到的是上一 Tick 的快照（Tick 0 之前是初始快照）。
            Assert.That(recording.SnapshotTicksAtCheck, Is.EqualTo(new[] { 0L, 0L, 1L }),
                "不变量检查必须早于本 Tick 快照构建");
            Assert.That(sim.CurrentSnapshot.Tick, Is.EqualTo(2L));

            // 参与者顺序固定且显式装配（不依赖容器/反射注册顺序）。
            var order = new List<string>();
            var first = new OrderRecordingCheck(order, "first");
            var second = new OrderRecordingCheck(order, "second");
            var ordered = Task03.NewSim(new BattleSimulationAssembly(
                invariantChecks: new IStepInvariantCheck[] { first, second }));
            Task03.StepNext(ordered);
            Assert.That(order, Is.EqualTo(new[] { "first", "second" }));
        }

        private sealed class OrderRecordingCheck : IStepInvariantCheck
        {
            private readonly List<string> _order;
            private readonly string _name;

            public OrderRecordingCheck(List<string> order, string name)
            {
                _order = order;
                _name = name;
            }

            public string CheckOrdered(long tick, IReadOnlyList<UnitSnapshot> units)
            {
                _order.Add(_name);
                return null;
            }
        }

        [Test]
        public void WindowCloseAndBattleEndKeepPhasesAndEventsOrdered()
        {
            var schedule = new ScriptedWindowSchedule
            {
                OpenTick = 0L,
                OwnerUnitId = Task03.HeroUnitId.Value,
                CloseTick = 2L
            };
            var sim = Task03.NewSim(new BattleSimulationAssembly(turnWindowSchedule: schedule));

            StepResult opened = Task03.StepNext(sim);
            Assert.That(opened.Snapshot.WindowManager.CurrentWindowId, Is.EqualTo(1L));

            Task03.StepNext(sim);
            StepResult closed = Task03.StepNext(sim);

            var closedEvents = Task03.EventsOfType<TurnWindowClosedEvent>(closed.Events)
                .Cast<TurnWindowClosedEvent>().ToList();
            Assert.That(closedEvents.Count, Is.EqualTo(1));
            Assert.That(closedEvents[0].Reason, Is.EqualTo(TurnWindowCloseReason.OwnerRequested));
            Assert.That(closed.Snapshot.WindowManager.CurrentWindowId, Is.EqualTo(0L));
            Assert.That(closed.Snapshot.NextWindowId, Is.EqualTo(2L), "关闭不回收 WindowId");
            Assert.That(sim.LastStepPhaseTrace.Select(e => e.Phase).ToList().IndexOf(StepPhase.WindowCloseAndScheduleNext),
                Is.LessThan(sim.LastStepPhaseTrace.Select(e => e.Phase).ToList().IndexOf(StepPhase.DecisionSnapshotDelivery)));

            // 结束 Tick 也必须执行归档/输出阶段，并把 BattleEndedEvent 放在最后。
            sim.RequestStop();
            StepResult ended = Task03.StepNext(sim);
            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded));
            Assert.That(sim.LastStepPhaseTrace.Select(e => e.Phase).Last(),
                Is.EqualTo(StepPhase.InvariantCheckArchiveAndOutput));
            Assert.That(ended.Events.Events.Last(), Is.InstanceOf<BattleEndedEvent>());
        }
    }
}
