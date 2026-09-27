// 任务 07 Shadow 检查点的**离线探针**（临时验证文件，不属于交付物；运行后可删除）。
//
// 目的：在不开 Unity 的前提下，用手工构造的规范化快照验证
//   `ShadowDifferenceDetector.Task07TurnWindowFacts` 真的按声明的字段路径逐条比较，
//   以及 `ShadowCasePolicy` 的任务 07 登记项/计数口径与 PlayMode 用例的断言一致。
//
// 它**不**验证 PlayMode 场景本身（真实 02B 定义 + 真实 Step 管线），只验证比较器与策略。

using System;
using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Probe
{
    [TestFixture]
    public sealed class ShadowTask07DetectorProbe
    {
        private const string CaseId = "task07-turn-window-budget-authority-profile";
        private const string RulesVersion = "battle-def-v1";
        private const string Hash = "probe-hash";
        private const string Encounter = "encounter.probe";

        private const long HeroUnitId = 1L;
        private const long EnemyUnitId = 2L;
        private const long HeroWindowId = 1L;
        private const long EnemyWindowId = 2L;
        private const long PlanId = 1L;
        private const int PlanCost = 256;
        private const int HeroBudget = 4096;
        private const int EnemyBudget = 1024;
        private const int ProbeTick = 20;

        [Test]
        public void Task07FactsCompareEachDeclaredFieldPath()
        {
            var checks = new List<string>();
            LogicSnapshot legacy = ScenarioSnapshot(ProbeTick, ScenarioShape.ProbeTick);
            LogicSnapshot shadow = ScenarioSnapshot(ProbeTick, ScenarioShape.ProbeTick);
            var policy = Policy();

            ShadowComparisonReport clean = ShadowDifferenceDetector.Compare(Input(legacy, shadow), policy);
            Assert.That(clean.UnalignedCheckpoints, Is.EqualTo(0), clean.Describe());
            Assert.That(clean.ComparedCheckpoints, Is.EqualTo(1), clean.Describe());
            Assert.That(clean.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(0), clean.Describe());
            Assert.That(clean.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.EqualTo(0), clean.Describe());
            Assert.That(clean.HasUnexpectedDifference, Is.False, clean.Describe());
            Assert.That(clean.CanClaimEquivalence, Is.True, clean.Describe());
            Assert.That(clean.TemporarilyUncomparable.Count, Is.EqualTo(59),
                "登记项 = 既有 10 + 任务 05 的 12 + 任务 06 的 8 + 任务 07 的 29："
                + string.Join(" | ", clean.TemporarilyUncomparable));
            Assert.That(clean.CountOf(ShadowDifferenceKind.TemporarilyUncomparable),
                Is.EqualTo(clean.TemporarilyUncomparable.Count), clean.Describe());
            checks.Add("clean=" + clean.Describe());

            // —— 逐通道探针：篡改新侧 ⇒ 必须出现精确字段路径 ——
            AssertProbe(checks, legacy, policy, "windows[1].openedAtTick",
                s => WithWindowManager(s, TamperWindow(s.WindowManager, HeroWindowId,
                    w => w with { OpenedAtTick = w.OpenedAtTick + 5L })), expected: 1);
            AssertProbe(checks, legacy, policy, "windows[1].isAcceptingSubmissions",
                s => WithWindowManager(s, TamperWindow(s.WindowManager, HeroWindowId,
                    w => w with { IsAcceptingSubmissions = !w.IsAcceptingSubmissions })));
            AssertProbe(checks, legacy, policy, "windows.count",
                s => WithWindowManager(s, RemoveWindow(s.WindowManager, HeroWindowId)));
            AssertProbe(checks, legacy, policy, "windows[1].spentBudgetTicks",
                s => WithWindowManager(s, TamperWindow(s.WindowManager, HeroWindowId,
                    w => w with { SpentBudgetTicks = w.SpentBudgetTicks + 1 })), minimum: 2);
            AssertProbe(checks, legacy, policy, "windows[1].budgetIdentity",
                s => WithWindowManager(s, TamperWindow(s.WindowManager, HeroWindowId,
                    w => w with { AvailableBudgetTicks = w.AvailableBudgetTicks + 1 })));
            AssertProbe(checks, legacy, policy, "resources.turnBudgetSpent",
                s => WithResources(s, s.Resources with { TurnBudgetSpent = s.Resources.TurnBudgetSpent + 1L }));
            AssertProbe(checks, legacy, policy, "concurrentAction.playerUnitId",
                s => WithConcurrentAction(s, s.ConcurrentAction with
                {
                    PlayerUnitId = s.ConcurrentAction.PlayerUnitId + 1L
                }));
            AssertProbe(checks, legacy, policy, "concurrentAction.authorizationTargetsOpenWindow",
                s => WithConcurrentAction(s, s.ConcurrentAction with
                {
                    WindowId = s.ConcurrentAction.WindowId + 1L
                }));
            AssertProbe(checks, legacy, policy, "adrenaline[1].cycleId",
                s => WithResources(s, TamperLedger(s.Resources, HeroUnitId,
                    l => l with { CycleId = l.CycleId + 1L })));
            AssertProbe(checks, legacy, policy, "adrenaline[1].available",
                s => WithResources(s, TamperLedger(s.Resources, HeroUnitId,
                    l => l with { AvailableAdrenaline = l.AvailableAdrenaline + 3 })));
            AssertProbe(checks, legacy, policy, "adrenaline[1].mirrorMatchesLedger",
                s => WithResources(s, TamperLedger(s.Resources, HeroUnitId,
                    l => l with { AvailableAdrenaline = l.AvailableAdrenaline + 3 })));
            AssertProbe(checks, legacy, policy, "plans[1].submittedWindowLedger",
                s => WithPlans(s, new[]
                {
                    FindPlan(s, PlanId) with { SubmittedWindowId = EnemyWindowId }
                }));
            AssertProbe(checks, legacy, policy, "plans[1].budgetLedgerLinked",
                s => WithPlans(s, new[]
                {
                    FindPlan(s, PlanId) with { ReservedTurnBudgetTicks = 1 }
                }));

            // —— Editable 形态（Tick 9：窗口仍开放接受提交、预留未被消费、无授权）的不变量 ——
            LogicSnapshot editable = ScenarioSnapshot(9, ScenarioShape.Editable);
            ShadowComparisonReport editableReport = ShadowDifferenceDetector.Compare(
                Input(editable, ScenarioSnapshot(9, ScenarioShape.Editable)), policy);
            Assert.That(editableReport.HasUnexpectedDifference, Is.False, editableReport.Describe());
            Assert.That(editableReport.CanClaimEquivalence, Is.True, editableReport.Describe());
            checks.Add("editable=" + editableReport.Describe());

            // —— 默认策略（不开任何任务检查点）对同一条篡改流必须零差异 ——
            LogicSnapshot tampered = ScenarioSnapshot(ProbeTick, ScenarioShape.ProbeTick);
            tampered = WithWindowManager(tampered, TamperWindow(tampered.WindowManager, HeroWindowId,
                w => w with { OpenedAtTick = w.OpenedAtTick + 7L }));
            ShadowComparisonReport defaultOff = ShadowDifferenceDetector.Compare(
                Input(legacy, tampered),
                ShadowCasePolicy.CreateDefault(CaseId + "-default-off", RulesVersion));
            Assert.That(defaultOff.TemporarilyUncomparable.Count, Is.EqualTo(10),
                "默认策略只登记既有 10 条：" + defaultOff.Describe());
            Assert.That(defaultOff.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(0),
                defaultOff.Describe());
            Assert.That(ShadowCasePolicy.CreateDefault(CaseId, RulesVersion).CompareTurnWindowFacts, Is.False);
            checks.Add("defaultOff=" + defaultOff.Describe());

            Console.WriteLine("PROBE-OK :: " + string.Join(" ;; ", checks));
        }

        /// <summary>对单条通道做"篡改 ⇒ 必须命中该字段路径"的断言。</summary>
        private static void AssertProbe(
            List<string> checks, LogicSnapshot legacy, ShadowCasePolicy policy, string expectedPath,
            Func<LogicSnapshot, LogicSnapshot> tamper, int expected = -1, int minimum = -1)
        {
            LogicSnapshot tampered = tamper(ScenarioSnapshot(ProbeTick, ScenarioShape.ProbeTick));
            ShadowComparisonReport report = ShadowDifferenceDetector.Compare(Input(legacy, tampered), policy);
            string list = Describe(report.Differences);

            ShadowFieldDifference hit = null;
            for (int i = 0; i < report.Differences.Count; i++)
            {
                ShadowFieldDifference difference = report.Differences[i];
                if (difference.Kind != ShadowDifferenceKind.NewRuleVerifiedFact) continue;
                if (!string.Equals(difference.FieldPath, expectedPath, StringComparison.Ordinal)) continue;
                hit = difference;
                break;
            }

            Assert.That(hit, Is.Not.Null, expectedPath + " 未出现在差异里： " + list);
            Assert.That(hit.IsUnexpected, Is.True);
            int count = report.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact);
            if (expected >= 0)
            {
                Assert.That(count, Is.EqualTo(expected),
                    expectedPath + " 必须恰好产生 " + expected + " 条差异： " + list);
                Assert.That(report.FirstUnexpectedDifference.FieldPath, Is.EqualTo(expectedPath), list);
            }
            if (minimum >= 0)
            {
                Assert.That(count, Is.GreaterThanOrEqualTo(minimum),
                    expectedPath + " 必须至少产生 " + minimum + " 条差异： " + list);
            }
            checks.Add(expectedPath + "=" + count);
        }

        private static ShadowCasePolicy Policy()
            => ShadowCasePolicy.CreateDefault(CaseId, RulesVersion,
                compareScheduleFacts: true, compareMovementFacts: true, compareTurnWindowFacts: true);

        private static ShadowComparisonInput Input(LogicSnapshot legacy, LogicSnapshot shadow)
            => new ShadowComparisonInput(Hash, Encounter, BattleRuntimeMode.Shadow, "probe-input", RulesVersion,
                ShadowComparisonConfig.Strict(64), new[] { legacy }, new[] { shadow });

        private enum ScenarioShape
        {
            /// <summary>Tick 9：窗口仍开放接受提交、计划 Editable 且持预留、无并发授权。</summary>
            Editable = 0,

            /// <summary>Tick 20：W1 已关闭（Spent 已就位）、W2（他人）开放且授权有效、计划 Running。</summary>
            ProbeTick = 1
        }

        private static LogicSnapshot ScenarioSnapshot(long tick, ScenarioShape shape)
        {
            bool probe = shape == ScenarioShape.ProbeTick;

            var heroWindow = new TurnWindowSnapshot(HeroWindowId, HeroUnitId, 2L, HeroBudget,
                probe ? 0 : PlanCost, probe ? PlanCost : 0, probe ? HeroBudget - PlanCost : HeroBudget - PlanCost,
                !probe, !probe, probe ? 0 : 0,
                probe
                    ? Array.Empty<TurnWindowReservationSnapshot>()
                    : new[] { new TurnWindowReservationSnapshot(PlanId, PlanCost) });

            var windows = new List<TurnWindowSnapshot> { heroWindow };
            if (probe)
            {
                windows.Add(new TurnWindowSnapshot(EnemyWindowId, EnemyUnitId, 12L, EnemyBudget,
                    0, 0, EnemyBudget, true, true, 0, Array.Empty<TurnWindowReservationSnapshot>()));
            }

            var manager = new TurnWindowManagerSnapshot(
                probe ? EnemyWindowId : HeroWindowId, -1L, probe ? 2L : 1L, probe ? HeroWindowId : 0L, windows);

            var concurrent = probe
                ? new ConcurrentActionSnapshot(true, EnemyWindowId, HeroUnitId)
                : ConcurrentActionSnapshot.None();

            var plan = new ActionPlanSnapshot(PlanId, HeroUnitId, "action.move.default",
                State: probe ? (int)ActionPlanState.Running : (int)ActionPlanState.Editable,
                SubmittedWindowId: HeroWindowId,
                StartTick: 14L, EndTick: 270L, LockedAtTick: probe ? 14L : -1L,
                BudgetCostTicks: PlanCost,
                ReservedTurnBudgetTicks: probe ? 0 : PlanCost);

            long reserved = probe ? PlanCost : 0L;
            long spent = probe ? PlanCost : 0L;
            long available = BudgetTotal() - reserved - spent;
            var resources = new BattleResourceSnapshot(
                probe ? 9 : 10, available, reserved, spent,
                new[]
                {
                    new AdrenalineLedgerSnapshot(HeroUnitId, 61, 1L,
                        Array.Empty<AdrenalineReservationSnapshot>(), 0),
                    new AdrenalineLedgerSnapshot(EnemyUnitId, 0, 0L,
                        Array.Empty<AdrenalineReservationSnapshot>(), 0)
                });

            var units = new[]
            {
                new UnitSnapshot(HeroUnitId, "unit.hero", "faction.hero", 0, 0, 0, 10240, true, 61, 1L),
                new UnitSnapshot(EnemyUnitId, "unit.enemy", "faction.monster", 0, 8, 0, 10240, true, 0, 0L)
            };

            return new LogicSnapshot(
                tick, RulesVersion, Hash, Encounter,
                BattleEndSnapshot.Active(),
                units, Array.Empty<StatusEffectSnapshot>(), manager, concurrent, resources,
                1L, new[] { plan }, Array.Empty<ReactionOpportunitySnapshot>(),
                new[] { new ActorLaneSnapshot(HeroUnitId, 1, false) },
                Array.Empty<IntentSnapshot>(), Array.Empty<MovementSegmentSnapshot>(),
                Array.Empty<ReservationSnapshot>(), Array.Empty<AiControllerSnapshot>(),
                null, new RngSnapshot(1, 12345UL),
                2L, 2L, 1L, probe ? 3L : 2L, 1L, 1L, 1L, 1L, 1L, 1L,
                HistorySummary.Empty(), "command-source-priority-v1",
                0L, null, 1L);
        }

        // 预算恒等式必须成立：Available + Reserved + Spent == Σ Total。
        private static long BudgetTotal() => HeroBudget + EnemyBudget;

        private static LogicSnapshot WithWindowManager(LogicSnapshot s, TurnWindowManagerSnapshot manager)
            => Copy(s, windowManager: manager);

        private static LogicSnapshot WithConcurrentAction(LogicSnapshot s, ConcurrentActionSnapshot authority)
            => Copy(s, concurrentAction: authority);

        private static LogicSnapshot WithResources(LogicSnapshot s, BattleResourceSnapshot resources)
            => Copy(s, resources: resources);

        private static LogicSnapshot WithPlans(LogicSnapshot s, IReadOnlyList<ActionPlanSnapshot> plans)
            => Copy(s, plans: plans);

        private static LogicSnapshot Copy(
            LogicSnapshot source,
            IReadOnlyList<ActionPlanSnapshot> plans = null,
            TurnWindowManagerSnapshot windowManager = null,
            ConcurrentActionSnapshot concurrentAction = null,
            BattleResourceSnapshot resources = null)
            => new LogicSnapshot(
                source.Tick, source.RulesVersion, source.BattleDefinitionHash, source.EncounterId,
                source.BattleEnd, source.Units, source.Effects,
                windowManager ?? source.WindowManager,
                concurrentAction ?? source.ConcurrentAction,
                resources ?? source.Resources,
                source.ScheduleRevision, plans ?? source.Plans, source.ReactionOpportunities,
                source.ActorLanes, source.Intents, source.MovementSegments, source.Reservations,
                source.AiControllers, source.CommandIngresses, source.Rng,
                source.NextUnitId, source.NextActionPlanId, source.NextReactionOpportunityId,
                source.NextWindowId, source.NextEffectId, source.NextCommandSequence,
                source.NextIntentSequence, source.NextResolutionSequence, source.NextEventSequence,
                source.NextEffectSequence, source.History, source.CommandSourcePriorityMappingVersion,
                source.TerminalPlanRecordCount, source.TerminalPlanDigest,
                source.NextReactionOptionSequence);

        private static ActionPlanSnapshot FindPlan(LogicSnapshot snapshot, long planId)
        {
            for (int i = 0; i < snapshot.Plans.Count; i++)
            {
                if (snapshot.Plans[i].ActionPlanId == planId) return snapshot.Plans[i];
            }
            return null;
        }

        private static TurnWindowManagerSnapshot TamperWindow(
            TurnWindowManagerSnapshot source, long windowId,
            Func<TurnWindowSnapshot, TurnWindowSnapshot> change)
        {
            var windows = new List<TurnWindowSnapshot>(source.Windows.Count);
            for (int i = 0; i < source.Windows.Count; i++)
            {
                TurnWindowSnapshot window = source.Windows[i];
                windows.Add(window.WindowId == windowId ? change(window) : window);
            }
            return new TurnWindowManagerSnapshot(source.CurrentWindowId, source.NextWindowTick,
                source.NextWindowOrdinal, source.LastClosedWindowId, windows);
        }

        private static TurnWindowManagerSnapshot RemoveWindow(
            TurnWindowManagerSnapshot source, long windowId)
        {
            var windows = new List<TurnWindowSnapshot>(source.Windows.Count);
            for (int i = 0; i < source.Windows.Count; i++)
            {
                if (source.Windows[i].WindowId == windowId) continue;
                windows.Add(source.Windows[i]);
            }
            return new TurnWindowManagerSnapshot(source.CurrentWindowId, source.NextWindowTick,
                source.NextWindowOrdinal, source.LastClosedWindowId, windows);
        }

        private static BattleResourceSnapshot TamperLedger(
            BattleResourceSnapshot source, long unitId,
            Func<AdrenalineLedgerSnapshot, AdrenalineLedgerSnapshot> change)
        {
            var ledgers = new List<AdrenalineLedgerSnapshot>(source.AdrenalineLedgers);
            for (int i = 0; i < ledgers.Count; i++)
            {
                if (ledgers[i].UnitId == unitId) ledgers[i] = change(ledgers[i]);
            }
            return source with { AdrenalineLedgers = ledgers };
        }

        private static string Describe(IReadOnlyList<ShadowFieldDifference> differences)
        {
            var parts = new List<string>();
            for (int i = 0; i < differences.Count; i++)
            {
                if (differences[i].Kind != ShadowDifferenceKind.NewRuleVerifiedFact) continue;
                parts.Add(differences[i].FieldPath + ":" + differences[i].LegacyValue
                          + "->" + differences[i].ShadowValue);
            }
            return string.Join(" | ", parts);
        }
    }
}
