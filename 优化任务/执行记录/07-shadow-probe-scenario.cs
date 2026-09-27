// 任务 07 场景机制的**离线探针**（临时验证文件，不属于交付物；运行后可删除）。
//
// 目的：在不开 Unity 的前提下，用一个**手工构造的最小 BattleDefinition + Encounter**
// 驱动真实 `BattleSimulation` + 真实 `Step` 管线，验证 PlayMode 用例依赖的机制假设：
//   ① 生产装配是否把 Encounter 的 ControllerBinding 注册进 TurnWindowManager（缺口 D-A）；
//   ② 窗口打开时"先递增个人周期再清零 Available"；
//   ③ 普通 Move 在窗口内被接受 ⇒ Available -> Reserved；锁定 ⇒ Reserved -> Spent；
//   ④ 已关闭窗口的账本继续可审计（Spent 落在关闭窗口上）；
//   ⑤ 他人窗口内的并发激活、窗口关闭即撤销授权；
//   ⑥ 跨窗口切换不改动已有计划。
// 它**不能**替代 PlayMode 用例对真实 02B 定义的验证。

using System;
using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Units;

namespace ProjectHero.Probe
{
    [TestFixture]
    public sealed class Task07ScenarioMechanicsProbe
    {
        private const string MoveSpecId = "action.probe.move";
        private const string HeroSlot = "hero";
        private const string EnemySlot = "enemy";
        private const long HeroUnitId = 1L;
        private const long EnemyUnitId = 2L;

        private const int HeroWindowOpenTick = 2;
        private const int SubmitTick = 3;
        private const int AccrualTickB = 5;
        private const int HeroWindowCloseTick = 10;
        private const int EnemyWindowOpenTick = 12;
        private const int ActivationTick = 13;
        private const int RequestedStartTick = 14;
        private const int ProbeTick = 20;
        private const int TerminalTick = 60;

        private const int HeroWindowBudget = 4096;
        private const int EnemyWindowBudget = 1024;

        [Test]
        public void ScenarioMechanicsBehaveAsThePlayModeCaseAssumes()
        {
            var checks = new List<string>();

            // —— ① 未注册控制权：新增普通动作必须因控制权被拒（缺口 D-A 的可观察后果）——
            RunResult unwired = Run(registerControllerBindings: false, wireStartCommitPort: false);
            Assert.That(unwired.ControlBindingAvailable, Is.False, "未注册时 CanControl 必须为 false");
            Assert.That(unwired.SubmitRejectionCode, Is.EqualTo(TurnWindowCodes.ISSUER_CANNOT_CONTROL_UNIT),
                "未注册控制权时的稳定拒绝码，实测=" + unwired.SubmitRejectionCode + " ; " + unwired.Describe());
            checks.Add("unwired.submit=" + unwired.SubmitRejectionCode);

            // —— ①′ 注册控制权但**不**注入启动提交端口：验证缺口 D-C（Reserved -> Spent 未接线）——
            RunResult defaultPort = Run(registerControllerBindings: true, wireStartCommitPort: false);
            Console.WriteLine("DEFAULTPORT-STATE :: " + defaultPort.Describe());
            Assert.That(defaultPort.SubmitRejectionCode, Is.Null, "默认端口下新增仍必须被接受");
            checks.Add("defaultPort.startCommit: reserved=" + defaultPort.WindowReservedAtStart
                       + " spent=" + defaultPort.WindowSpentAtStart);
            if (defaultPort.WindowSpentAtStart == 0)
            {
                // 缺口存在：NoTurnBudgetCommitPort 只把计划预留清零，窗口账本仍是 Reserved。
                Assert.That(defaultPort.WindowReservedAtStart, Is.EqualTo(defaultPort.PlanCost),
                    "缺口 D-C 的可观察后果：窗口账本必须保持 Reserved 不变（消费没有发生）");
                Console.WriteLine("GAP-D-C :: 默认装配的 StartCommitPort = NoTurnBudgetCommitPort ⇒ "
                                  + "窗口账本 Reserved=" + defaultPort.WindowReservedAtStart
                                  + " Spent=" + defaultPort.WindowSpentAtStart + "（计划已 Running）");
            }

            // —— ② 注册控制权 + 显式注入真实启动提交端口：整条场景必须成立 ——
            RunResult wired = Run(registerControllerBindings: true, wireStartCommitPort: true);
            Console.WriteLine("WIRED-STATE :: " + wired.Describe());
            Console.WriteLine("UNWIRED-STATE :: " + unwired.Describe());
            Assert.That(wired.ControlBindingAvailable, Is.True);
            Assert.That(wired.SubmitRejectionCode, Is.Null,
                "注册控制权后新增普通 Move 必须被接受，实测=" + wired.SubmitRejectionCode
                + " ; " + wired.Describe());
            Assert.That(wired.ActivationRejectionCode, Is.Null,
                "并发激活必须成功，实测=" + wired.ActivationRejectionCode + " ; " + wired.Describe());
            checks.Add("wired.submit=<accepted> activation=<accepted>");

            // 窗口打开（Tick 2）：先递增周期再清零 Available。
            LogicSnapshot atOpen = wired.Snapshots[HeroWindowOpenTick];
            Assert.That(atOpen.WindowManager.CurrentWindowId, Is.EqualTo(1L), wired.Describe());
            Assert.That(wired.HeroWindowId, Is.EqualTo(1L));
            AdrenalineLedgerSnapshot accured = wired.LedgerAt(1);
            Assert.That(accured.AvailableAdrenaline, Is.GreaterThan(0),
                "Tick 1 的入账必须真的发生：" + wired.Describe());
            Assert.That(accured.CycleId, Is.EqualTo(0L));
            AdrenalineLedgerSnapshot cleared = wired.LedgerAt(HeroWindowOpenTick);
            Assert.That(cleared.AvailableAdrenaline, Is.EqualTo(0), "窗口打开必须清零 Available");
            Assert.That(cleared.CycleId, Is.EqualTo(1L), "窗口打开必须递增个人周期");
            checks.Add("open: available " + accured.AvailableAdrenaline + "->0, cycle 0->1");

            // Tick 3：Editable + Reserved。
            LogicSnapshot atSubmit = wired.Snapshots[SubmitTick];
            Assert.That(atSubmit.Plans.Count, Is.EqualTo(1), wired.Describe());
            ActionPlanSnapshot plan = atSubmit.Plans[0];
            Assert.That(plan.State, Is.EqualTo((int)ActionPlanState.Editable));
            Assert.That(plan.SubmittedWindowId, Is.EqualTo(1L));
            Assert.That(plan.BudgetCostTicks, Is.GreaterThan(0), wired.Describe());
            TurnWindowSnapshot w1 = WindowOf(atSubmit, 1L);
            Assert.That(w1.ReservedBudgetTicks, Is.EqualTo(plan.BudgetCostTicks));
            Assert.That(w1.SpentBudgetTicks, Is.EqualTo(0));
            checks.Add("reserve: cost=" + plan.BudgetCostTicks + " reserved=" + w1.ReservedBudgetTicks);

            // Tick 5：窗口打开之后入账照常生效。
            Assert.That(wired.LedgerAt(AccrualTickB).AvailableAdrenaline, Is.GreaterThan(0),
                "窗口打开之后的入账必须照常生效");

            // Tick 10：正式关闭；账本保留 Reserved；计划与排程不动。
            LogicSnapshot atClose = wired.Snapshots[HeroWindowCloseTick];
            TurnWindowSnapshot closed = WindowOf(atClose, 1L);
            Assert.That(closed.IsOpen, Is.False);
            Assert.That(closed.IsAcceptingSubmissions, Is.False);
            Assert.That(atClose.WindowManager.CurrentWindowId, Is.EqualTo(0L));
            Assert.That(closed.ReservedBudgetTicks, Is.EqualTo(plan.BudgetCostTicks),
                "关闭窗口的账本必须保留 Editable 预留");
            Assert.That(FindPlan(atClose) != null && FindPlan(atClose).State == (int)ActionPlanState.Editable,
                "窗口关闭不得改动计划状态");
            Assert.That(wired.LedgerAt(HeroWindowCloseTick).AvailableAdrenaline,
                Is.EqualTo(wired.LedgerAt(AccrualTickB).AvailableAdrenaline), "窗口关闭不清零");
            checks.Add("close: reserved kept=" + closed.ReservedBudgetTicks + ", accepting=false");

            // Tick 12：他人窗口打开；hero 的账本与计划不变。
            LogicSnapshot atEnemy = wired.Snapshots[EnemyWindowOpenTick];
            Assert.That(atEnemy.WindowManager.CurrentWindowId, Is.EqualTo(2L), wired.Describe());
            Assert.That(WindowOf(atEnemy, 2L).OwnerUnitId, Is.EqualTo(EnemyUnitId));
            Assert.That(atEnemy.WindowManager.Windows.Count, Is.EqualTo(2));
            Assert.That(wired.LedgerAt(EnemyWindowOpenTick).AvailableAdrenaline,
                Is.EqualTo(wired.LedgerAt(AccrualTickB).AvailableAdrenaline), "跨其他单位窗口保留");
            Assert.That(wired.LedgerAt(EnemyWindowOpenTick).CycleId, Is.EqualTo(1L));
            checks.Add("crossWindow: available kept, cycle=1");

            // Tick 13：并发激活恰好消费一次局外资源。
            LogicSnapshot atActivation = wired.Snapshots[ActivationTick];
            Assert.That(atActivation.ConcurrentAction.HasActiveAuthorization, Is.True, wired.Describe());
            Assert.That(atActivation.ConcurrentAction.WindowId, Is.EqualTo(2L));
            Assert.That(atActivation.ConcurrentAction.PlayerUnitId, Is.EqualTo(HeroUnitId));
            Assert.That(atActivation.Resources.MetaResource,
                Is.EqualTo(atEnemy.Resources.MetaResource - 1));
            checks.Add("activation: meta " + atEnemy.Resources.MetaResource + "->"
                       + atActivation.Resources.MetaResource);

            // Tick 14：计划在**他人**窗口期间从已关闭的来源窗口启动 ⇒ Reserved -> Spent。
            LogicSnapshot atStart = wired.Snapshots[RequestedStartTick];
            ActionPlanSnapshot running = FindPlan(atStart);
            Assert.That(running, Is.Not.Null, wired.Describe());
            Assert.That(running.State, Is.EqualTo((int)ActionPlanState.Running));
            Assert.That(running.LockedAtTick, Is.EqualTo((long)RequestedStartTick));
            Assert.That(running.ReservedTurnBudgetTicks, Is.EqualTo(0));
            Assert.That(atStart.WindowManager.CurrentWindowId, Is.EqualTo(2L));
            TurnWindowSnapshot sourceAfterLock = WindowOf(atStart, 1L);
            Assert.That(sourceAfterLock.ReservedBudgetTicks, Is.EqualTo(0));
            Assert.That(sourceAfterLock.SpentBudgetTicks, Is.EqualTo(plan.BudgetCostTicks),
                "Spent 必须落在来源（已关闭）窗口的账本上");
            checks.Add("lock: closedWindow spent=" + sourceAfterLock.SpentBudgetTicks);

            // Tick 20（探针 Tick）：授权仍有效、计划仍活动。
            LogicSnapshot atProbe = wired.Snapshots[ProbeTick];
            Assert.That(atProbe.ConcurrentAction.HasActiveAuthorization, Is.True, wired.Describe());
            checks.Add("probeTick: authority active, plan state="
                       + (FindPlan(atProbe) != null ? FindPlan(atProbe).State.ToString() : "<none>"));

            // Tick 60：窗口关闭 ⇒ 撤销授权；Spent 不退。
            LogicSnapshot atTerminal = wired.Snapshots[TerminalTick];
            Assert.That(WindowOf(atTerminal, 2L).IsOpen, Is.False);
            Assert.That(atTerminal.ConcurrentAction.HasActiveAuthorization, Is.False);
            Assert.That(atTerminal.WindowManager.CurrentWindowId, Is.EqualTo(0L));
            Assert.That(WindowOf(atTerminal, 1L).SpentBudgetTicks, Is.EqualTo(plan.BudgetCostTicks));
            checks.Add("terminal: authority revoked, spent kept");

            Console.WriteLine("SCENARIO-OK :: " + string.Join(" ;; ", checks));
        }

        // ---------------- 场景执行 ----------------

        private sealed class RunResult
        {
            public LogicSnapshot[] Snapshots;
            public long HeroWindowId;
            public string SubmitRejectionCode;
            public string ActivationRejectionCode;
            public bool ControlBindingAvailable;
            public int PlanCost;
            public int WindowReservedAtStart;
            public int WindowSpentAtStart;

            public AdrenalineLedgerSnapshot LedgerAt(int tick)
            {
                LogicSnapshot snapshot = Snapshots[tick];
                for (int i = 0; i < snapshot.Resources.AdrenalineLedgers.Count; i++)
                {
                    if (snapshot.Resources.AdrenalineLedgers[i].UnitId == HeroUnitId)
                        return snapshot.Resources.AdrenalineLedgers[i];
                }
                return null;
            }

            public string Describe()
            {
                var builder = new System.Text.StringBuilder("scenario[");
                for (int i = 0; i < Snapshots.Length; i++)
                {
                    LogicSnapshot s = Snapshots[i];
                    builder.Append("{t=").Append(s.Tick)
                        .Append(" rev=").Append(s.ScheduleRevision)
                        .Append(" curW=").Append(s.WindowManager.CurrentWindowId)
                        .Append(" windows=").Append(s.WindowManager.Windows.Count)
                        .Append(" w[");
                    for (int w = 0; w < s.WindowManager.Windows.Count; w++)
                    {
                        builder.Append('(').Append(s.WindowManager.Windows[w].WindowId)
                            .Append(":res=").Append(s.WindowManager.Windows[w].ReservedBudgetTicks)
                            .Append(",spent=").Append(s.WindowManager.Windows[w].SpentBudgetTicks)
                            .Append(",avail=").Append(s.WindowManager.Windows[w].AvailableBudgetTicks)
                            .Append(",open=").Append(s.WindowManager.Windows[w].IsOpen)
                            .Append(",acc=").Append(s.WindowManager.Windows[w].IsAcceptingSubmissions)
                            .Append(')');
                    }
                    builder.Append(']')
                        .Append(" auth=").Append(s.ConcurrentAction.HasActiveAuthorization)
                        .Append(" meta=").Append(s.Resources.MetaResource)
                        .Append(" adr=");
                    for (int a = 0; a < s.Resources.AdrenalineLedgers.Count; a++)
                    {
                        builder.Append('(').Append(s.Resources.AdrenalineLedgers[a].UnitId).Append(':')
                            .Append(s.Resources.AdrenalineLedgers[a].AvailableAdrenaline).Append('/')
                            .Append(s.Resources.AdrenalineLedgers[a].CycleId).Append(')');
                    }
                    builder.Append(" plans=");
                    for (int p = 0; p < s.Plans.Count; p++)
                    {
                        builder.Append('(').Append(s.Plans[p].ActionPlanId)
                            .Append(",st=").Append(s.Plans[p].State)
                            .Append(",cost=").Append(s.Plans[p].BudgetCostTicks)
                            .Append(",res=").Append(s.Plans[p].ReservedTurnBudgetTicks)
                            .Append(",w=").Append(s.Plans[p].SubmittedWindowId).Append(')');
                    }
                    builder.Append('}');
                }
                return builder.Append(']').ToString();
            }
        }

        private static RunResult Run(bool registerControllerBindings, bool wireStartCommitPort)
        {
            BattleDefinition definition = BuildDefinition();
            var encounterId = new EncounterDefinitionId("encounter.probe");
            var inputs = new BattleRuntimeInputs(20260922UL, 10);
            var startCommitPort = new DelegatingStartCommitPort();
            var assembly = new BattleSimulationAssembly(
                turnWindowSchedule: new ScriptedSchedule(),
                concurrentHeroUnitId: new UnitId(HeroUnitId),
                adrenalineAccrualFactSource: new ScriptedAccrual(),
                startCommitPort: wireStartCommitPort ? startCommitPort : null);

            var result = new RunResult();
            var snapshots = new LogicSnapshot[TerminalTick + 1];
            BattleSimulation simulation = BattleSimulation.Create(definition, encounterId, inputs, assembly);
            try
            {
                if (wireStartCommitPort)
                {
                    // 真实生产实现：任务 07 的 TurnWindowBudgetAuthority（Reserved -> Spent）。
                    startCommitPort.Target = new TurnWindowBudgetAuthority(simulation.WindowManager);
                }
                CommandIngressEntry entry = simulation.CommandIngress.FindEntry(
                    new ControllerId("controller.player"));
                Assert.That(entry, Is.Not.Null, "必须有 controller.player 入口");
                if (registerControllerBindings)
                {
                    simulation.WindowManager.RegisterControllerBinding(
                        new ControllerId("controller.player"), new UnitId(HeroUnitId));
                }
                result.ControlBindingAvailable = simulation.WindowManager.CanControl(
                    new ControllerId("controller.player"), new UnitId(HeroUnitId));

                for (long tick = 0L; tick <= TerminalTick; tick++)
                {
                    if (tick == SubmitTick)
                    {
                        Assert.That(simulation.CurrentTurnWindow, Is.Not.Null, "提交前必须有开放窗口");
                        WindowId expected = simulation.CurrentTurnWindow.WindowId;
                        result.HeroWindowId = expected.Value;
                        entry.Submit(new CommandRequest(tick,
                            new ScheduleEditScope(simulation.ScheduleRevision, expected),
                            new ScheduleEditPayload(new ScheduleEditOperation[]
                            {
                                new AddOrdinaryPlanOperation(1L, new UnitId(HeroUnitId),
                                    new ActionSpecId(MoveSpecId), RequestedStartTick,
                                    AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                                    Facing: GridDirection.North, Destination: new GridPoint(0, 8))
                            })));
                    }

                    if (tick == ActivationTick)
                    {
                        WindowId expected = simulation.CurrentTurnWindow.WindowId;
                        entry.Submit(new CommandRequest(tick,
                            new WindowCommandScope(expected),
                            new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction)));
                    }

                    FrozenCommandBatch batch = simulation.CommandIngress.FreezeTick(tick);
                    StepResult step = simulation.Step(tick, batch);
                    snapshots[tick] = simulation.CurrentSnapshot;
                    if (tick == SubmitTick) result.SubmitRejectionCode = FirstRejection(step);
                    if (tick == ActivationTick) result.ActivationRejectionCode = FirstRejection(step);
                }
            }
            finally
            {
                simulation.Dispose();
            }

            result.Snapshots = snapshots;
            if (snapshots[RequestedStartTick] != null)
            {
                ActionPlanSnapshot plan = FindPlan(snapshots[RequestedStartTick]);
                if (plan != null) result.PlanCost = plan.BudgetCostTicks;
                TurnWindowSnapshot window = WindowOf(snapshots[RequestedStartTick], 1L);
                if (window != null)
                {
                    result.WindowReservedAtStart = window.ReservedBudgetTicks;
                    result.WindowSpentAtStart = window.SpentBudgetTicks;
                }
            }
            return result;
        }

        /// <summary>把启动提交委托给"创建后才知道"的真实端口（冻结装配点的显式注入）。</summary>
        private sealed class DelegatingStartCommitPort : ProjectHero.Logic.Timeline.IActionPlanStartCommitPort
        {
            public ProjectHero.Logic.Timeline.IActionPlanStartCommitPort Target { get; set; }

            public string Commit(ProjectHero.Logic.Actions.ActionPlan plan, long tick,
                Action<ProjectHero.Logic.Actions.ActionPlan> rollback)
                => Target != null
                    ? Target.Commit(plan, tick, rollback)
                    : ProjectHero.Logic.Timeline.NoTurnBudgetCommitPort.Instance.Commit(plan, tick, rollback);
        }

        private static string FirstRejection(StepResult step)
        {
            var events = step.Events.EventsInSequenceOrder;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] is ProjectHero.Logic.Events.CommandRejectedEvent rejected)
                    return rejected.ReasonCode;
            }
            return null;
        }

        private sealed class ScriptedSchedule : ITurnWindowSchedule
        {
            public WindowOpenRequest TryOpenDue(long tick)
            {
                if (tick == HeroWindowOpenTick)
                    return new WindowOpenRequest(new UnitId(HeroUnitId), HeroWindowBudget);
                if (tick == EnemyWindowOpenTick)
                    return new WindowOpenRequest(new UnitId(EnemyUnitId), EnemyWindowBudget);
                return null;
            }

            public bool ShouldCloseCurrentWindow(long tick)
                => tick == HeroWindowCloseTick || tick == TerminalTick;
        }

        private sealed class ScriptedAccrual : IAdrenalineAccrualFactSource
        {
            public IReadOnlyList<AdrenalineAccrualFacts> BuildAccrualFactsOrdered(long tick)
            {
                if (tick == 1)
                    return new[] { new AdrenalineAccrualFacts(new UnitId(HeroUnitId), 50, 0, 0, 0, 0) };
                if (tick == AccrualTickB)
                    return new[] { new AdrenalineAccrualFacts(new UnitId(HeroUnitId), 10, 0, 0, 0, 0) };
                return Array.Empty<AdrenalineAccrualFacts>();
            }
        }

        // ---------------- 最小定义 ----------------

        private static BattleDefinition BuildDefinition()
        {
            var hero = new FactionId("faction.hero");
            var monster = new FactionId("faction.monster");
            var factionModel = new FactionModelDefinition(
                new List<FactionDefinition> { new FactionDefinition(hero), new FactionDefinition(monster) },
                new List<FactionRelationDefinition>
                {
                    new FactionRelationDefinition(hero, monster, FactionDisposition.Hostile)
                });

            var moveSpec = new ActionSpec(
                new ActionSpecId(MoveSpecId), ActionType.Move,
                new MoveTimingSpec(BaseStepTicks: 5, RecoveryTicks: 5),
                new MovePayloadSpec(200, MovementPattern()),
                AdrenalineCost: 0);

            var actionSet = new ActionSetDefinition(
                new ActionSetId("action_set.probe"), new List<ActionSpecId> { moveSpec.ActionSpecId });
            var volume = new VolumeSpec(new VolumeSpecId("unit_volume.probe"), Directions());

            var units = new List<UnitDefinition>
            {
                new UnitDefinition(new UnitDefinitionId("unit.probe.hero"), 10f, 10f, 10f, 10f,
                    new Dictionary<DamageChannelId, int>(), 200f, actionSet.ActionSetId, volume.VolumeSpecId),
                new UnitDefinition(new UnitDefinitionId("unit.probe.enemy"), 10f, 10f, 10f, 10f,
                    new Dictionary<DamageChannelId, int>(), 200f, actionSet.ActionSetId, volume.VolumeSpecId)
            };

            var encounter = new EncounterDefinition(
                new EncounterDefinitionId("encounter.probe"),
                new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40)),
                new List<EncounterUnitSlot>
                {
                    new EncounterUnitSlot(new EncounterSlotId(EnemySlot),
                        new UnitDefinitionId("unit.probe.enemy"), monster, new GridPoint(0, 20),
                        GridDirection.South),
                    new EncounterUnitSlot(new EncounterSlotId(HeroSlot),
                        new UnitDefinitionId("unit.probe.hero"), hero, new GridPoint(0, 0),
                        GridDirection.North)
                },
                new List<ControllerBinding>
                {
                    new ControllerBinding(new ControllerId("controller.player"), CommandSourceKind.Player,
                        new List<EncounterSlotId> { new EncounterSlotId(HeroSlot) }),
                    new ControllerBinding(new ControllerId("controller.enemy_ai"), CommandSourceKind.Ai,
                        new List<EncounterSlotId> { new EncounterSlotId(EnemySlot) })
                },
                new VictoryDefinition(new List<FactionId> { hero }, new List<FactionId> { monster },
                    "RESULT_VICTORY", "RESULT_DEFEAT", "RESULT_DRAW"));

            return new BattleDefinition(
                "battle-def-v1", 60, BattleRules.FrozenV1, ConcurrentActionDefinition.FrozenV1,
                new ReactionRules(2, 1), AdrenalineRules.FrozenV1, factionModel,
                new List<DamageChannelDefinition>(), new List<ImpactProfileDefinition>(),
                units, new List<ActionSpec> { moveSpec }, new List<AttackPatternSpec>(),
                new List<VolumeSpec> { volume }, new List<MovementPatternSpec> { MovementPattern() },
                new List<ActionSetDefinition> { actionSet }, new List<StatusEffectSpec>(),
                new List<EncounterDefinition> { encounter }, null, "test-definition-hash.probe");
        }

        private static IReadOnlyList<DirectionalTriangleSet> Directions()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        private static MovementPatternSpec MovementPattern()
            => new MovementPatternSpec(new MovementPatternId("movement_pattern.probe"), Directions());

        private static ActionPlanSnapshot FindPlan(LogicSnapshot snapshot)
            => snapshot.Plans.Count > 0 ? snapshot.Plans[0] : null;

        private static TurnWindowSnapshot WindowOf(LogicSnapshot snapshot, long windowId)
        {
            for (int i = 0; i < snapshot.WindowManager.Windows.Count; i++)
            {
                if (snapshot.WindowManager.Windows[i].WindowId == windowId)
                    return snapshot.WindowManager.Windows[i];
            }
            return null;
        }
    }
}
