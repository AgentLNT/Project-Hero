using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 07「必须产出」1/2/3/4/5/6/7/8 的纯逻辑用例：窗口只控制提交权限与整数 Tick 预算、
    /// 预算账本的三段式事务、并发授权、肾上腺素周期账本。
    ///
    /// 这些用例只使用公共契约（TurnWindowManager / TurnWindowBudgetAuthority /
    /// ConcurrentActionSystem / AdrenalineLedgerRegistry），因此它们验证的是对外语义；
    /// Startable 原子提交与 Spent 保持等跨子系统行为由模拟级用例覆盖。
    /// </summary>
    public class Task07TurnWindowAndBudgetTests
    {
        // ================= 夹具 =================

        private sealed class WorldView : ITurnWindowWorldView
        {
            public readonly HashSet<long> DeadUnits = new HashSet<long>();
            public bool Ended;

            public bool IsUnitAliveForWindow(UnitId unitId) => !DeadUnits.Contains(unitId.Value);

            public bool IsBattleEnded => Ended;
        }

        private static UnitId U(long id) => new UnitId(id);

        private static ActionPlanId P(long id) => new ActionPlanId(id);

        private static WindowId W(long id) => new WindowId(id);

        private static ControllerId C(string id) => new ControllerId(id);

        private static readonly ControllerId Player = C("controller.player");

        /// <summary>打开一个拥有者 = ownerId 的窗口并返回它。</summary>
        private static TurnWindow OpenWindow(
            TurnWindowManager manager, WorldView world, long tick, long ownerId, int budget = 10)
        {
            manager.ScheduleWindow(tick, U(ownerId), budget);
            TurnWindow window = manager.TryOpenDueWindow(tick);
            Assert.That(window, Is.Not.Null, "夹具前提：窗口应当在 tick " + tick + " 打开");
            return window;
        }

        private static ScheduleBudgetContext ExplicitContext(
            ITurnBudgetAuthority authority, WindowId? expected, WindowId? current)
            => new ScheduleBudgetContext(
                ResourceChangeSource.ExplicitScheduleEdit, authority, expected, current, Player);

        /// <summary>把一个计划的整数预算从 Available 转入该计划的 Reserved（新计划路径）。</summary>
        private static void Reserve(
            TurnWindowBudgetAuthority authority, TurnWindow window, ActionPlanId planId, int cost, long tick = 0L)
        {
            var changes = new[]
            {
                new TurnBudgetChangeRequest(
                    planId, window.WindowId, cost, cost, 0, isNewReservation: true,
                    TurnBudgetChangeKind.Reserved)
            };
            var context = ExplicitContext(authority, window.WindowId, window.WindowId);
            Assert.That(authority.ValidateBudget(changes, context), Is.Null, "夹具前提：预留应当可满足");
            authority.ApplyBudget(changes, context, tick);
        }

        // ================= 窗口结构与打开/关闭语义 =================

        [Test]
        public void TurnWindowHoldsNoActionPlanIntentOrReservationCollections()
        {
            // 验收标准：TurnWindow 内不存在 ActionPlan、Intent 或 Reservation 集合。
            Type windowType = typeof(TurnWindow);
            string[] forbidden = { "ActionPlan", "Intent", "Reservation" };
            var offenders = new List<string>();

            foreach (PropertyInfo property in windowType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.Name == nameof(TurnWindow.Reservations)) continue;
                string typeName = property.PropertyType.Name;
                foreach (string word in forbidden)
                {
                    if (typeName.Contains(word)) offenders.Add(property.Name + ":" + typeName);
                }
            }

            Assert.That(offenders, Is.Empty,
                "窗口只保存可审计的整数预算账本，不得成为动作/Intent/Reservation 容器：" + string.Join(",", offenders));
            Assert.That(windowType.GetProperties().Any(p => p.Name == "Reservations"), Is.True,
                "窗口必须暴露按计划归属的预留明细（可审计），但它不是计划容器");
        }

        [Test]
        public void NextWindowOpensNoEarlierThanNextTick()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);

            manager.ScheduleWindow(5L, U(1L), 10);

            Assert.That(manager.TryOpenDueWindow(4L), Is.Null, "到期 Tick 之前不得打开");
            TurnWindow first = manager.TryOpenDueWindow(5L);
            Assert.That(first, Is.Not.Null);

            // 当前窗口未关闭时，同一 Tick 不得打开第二个窗口。
            manager.ScheduleWindow(5L, U(2L), 10);
            Assert.That(manager.TryOpenDueWindow(5L), Is.Null);

            // 正式关闭发生在 Tick 末：同一 Tick 内仍然不得再打开新窗口。
            first.RequestClose(TurnWindowCloseReason.OwnerRequested);
            Assert.That(manager.FinalizeRequestedClose(5L), Is.Not.Null);
            manager.ScheduleWindow(5L, U(2L), 10);
            Assert.That(manager.TryOpenDueWindow(5L), Is.Null, "同一 Tick 内已关闭过窗口，新窗口最早下一 Tick");
            manager.ScheduleWindow(6L, U(2L), 10);
            Assert.That(manager.TryOpenDueWindow(6L), Is.Not.Null, "下一 Tick 可以打开");
        }

        [Test]
        public void ScheduledWindowOwnerDiesBeforeOpenDoesNotOpenWindow()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            int opened = 0;
            int cycleResets = 0;
            manager.WindowOpenedSink = (w, t) => opened++;
            manager.OwnerCycleResetSink = (u, t) => cycleResets++;

            // 稳定顺序 = UnitId 升序：拥有者 1 已死亡则跳过，拥有者 2 正常打开。
            world.DeadUnits.Add(1L);
            manager.ScheduleWindow(3L, U(1L), 10);
            manager.ScheduleWindow(3L, U(2L), 10);

            TurnWindow window = manager.TryOpenDueWindow(3L);
            Assert.That(window, Is.Not.Null);
            Assert.That(window.OwnerUnitId, Is.EqualTo(U(2L)), "死亡拥有者必须被跳过，打开下一个合法窗口");
            Assert.That(window.TotalBudgetTicks, Is.EqualTo(10));
            Assert.That(opened, Is.EqualTo(1), "死亡拥有者不得产生窗口打开事件");
            Assert.That(cycleResets, Is.EqualTo(1), "只有真正打开的窗口才递增个人周期");

            // 全部拥有者都死亡时：不打开、不发事件、不产生预算。
            var world2 = new WorldView();
            var manager2 = new TurnWindowManager(world2);
            int opened2 = 0;
            manager2.WindowOpenedSink = (w, t) => opened2++;
            world2.DeadUnits.Add(7L);
            manager2.ScheduleWindow(1L, U(7L), 10);
            Assert.That(manager2.TryOpenDueWindow(1L), Is.Null);
            Assert.That(opened2, Is.Zero);
            Assert.That(manager2.CurrentWindow, Is.Null);
        }

        [Test]
        public void BattleEndClosesWindowWithoutOpeningNewOnes()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            TurnWindow window = OpenWindow(manager, world, 2L, 1L);

            world.Ended = true;
            manager.CloseAllForBattleEnd(2L);

            Assert.That(window.IsOpen, Is.False);
            Assert.That(window.IsAcceptingSubmissions, Is.False);
            Assert.That(window.CloseReason, Is.EqualTo(TurnWindowCloseReason.BattleEnded));
            Assert.That(manager.CurrentWindow, Is.Null);
            Assert.That(manager.ScheduledWindowCount, Is.Zero, "战斗结束必须清空未来窗口排程");

            manager.ScheduleWindow(9L, U(1L), 10);
            Assert.That(manager.TryOpenDueWindow(9L), Is.Null, "战斗结束后不再打开新窗口");
        }

        [Test]
        public void CloseRequestRejectsLaterCommandsInSameTick()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            manager.RegisterControllerBinding(Player, U(1L));
            var authority = new TurnWindowBudgetAuthority(manager, null);
            TurnWindow window = OpenWindow(manager, world, 0L, 1L);

            window.RequestClose(TurnWindowCloseReason.OwnerRequested);

            Assert.That(window.IsAcceptingSubmissions, Is.False, "请求关闭立即停止接受提交");
            Assert.That(window.IsOpen, Is.True, "正式关闭必须等到 Tick 末");
            Assert.That(manager.IsAcceptingSubmissions, Is.False);
            Assert.That(manager.OpenWindow, Is.Null);
            Assert.That(window.CanReserve(1), Is.False);

            Assert.That(manager.TryRequestCloseForIssuer(Player, window.WindowId),
                Is.EqualTo(TurnWindowCodes.NO_OPEN_WINDOW));

            var changes = new[]
            {
                new TurnBudgetChangeRequest(
                    P(1L), window.WindowId, 3, 3, 0, isNewReservation: true, TurnBudgetChangeKind.Reserved)
            };
            var context = ExplicitContext(authority, window.WindowId, window.WindowId);
            Assert.That(authority.ValidateBudget(changes, context), Is.Not.Null);
            Assert.That(window.ReservedBudgetTicks, Is.Zero, "被拒绝的命令不得留下预留");
        }

        // ================= TurnBudget: Available -> Reserved -> Spent =================

        [Test]
        public void EditablePlanReservesBudgetWithoutSpendingIt()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            var authority = new TurnWindowBudgetAuthority(manager, null);
            TurnWindow window = OpenWindow(manager, world, 0L, 1L, budget: 12);

            Reserve(authority, window, P(1L), 5);

            Assert.That(window.ReservedBudgetTicks, Is.EqualTo(5), "Editable 阶段严格使用 Reserved");
            Assert.That(window.SpentBudgetTicks, Is.Zero, "Editable 阶段不得消费");
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(7));
            Assert.That(window.ReservedFor(P(1L)), Is.EqualTo(5), "预留必须按 ActionPlanId 归属");
            Assert.That(window.Reservations.Count, Is.EqualTo(1));
            Assert.That(window.Reservations[0].ActionPlanId, Is.EqualTo(P(1L)));
        }

        [Test]
        public void BudgetLedgerInvariantHoldsAcrossReserveAdjustAndRelease()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            var authority = new TurnWindowBudgetAuthority(manager, null);
            TurnWindow window = OpenWindow(manager, world, 0L, 1L, budget: 20);
            var context = ExplicitContext(authority, window.WindowId, window.WindowId);

            Reserve(authority, window, P(1L), 6);
            Assert.That(window.ReservedBudgetTicks + window.SpentBudgetTicks + window.AvailableBudgetTicks,
                Is.EqualTo(window.TotalBudgetTicks));

            var increase = new[]
            {
                new TurnBudgetChangeRequest(
                    P(1L), window.WindowId, 9, 3, 6, isNewReservation: false,
                    TurnBudgetChangeKind.ReservationAdjusted)
            };
            Assert.That(authority.ValidateBudget(increase, context), Is.Null);
            authority.ApplyBudget(increase, context, 0L);
            Assert.That(window.ReservedFor(P(1L)), Is.EqualTo(9));

            var decrease = new[]
            {
                new TurnBudgetChangeRequest(
                    P(1L), window.WindowId, 4, -5, 9, isNewReservation: false,
                    TurnBudgetChangeKind.ReservationAdjusted)
            };
            Assert.That(authority.ValidateBudget(decrease, context), Is.Null);
            authority.ApplyBudget(decrease, context, 0L);
            Assert.That(window.ReservedFor(P(1L)), Is.EqualTo(4));
            Assert.That(window.ReservedBudgetTicks + window.SpentBudgetTicks + window.AvailableBudgetTicks,
                Is.EqualTo(window.TotalBudgetTicks));

            var release = new[]
            {
                new TurnBudgetChangeRequest(
                    P(1L), window.WindowId, 0, -4, 4, isNewReservation: false,
                    TurnBudgetChangeKind.ReleasedBeforeLock)
            };
            Assert.That(authority.ValidateBudget(release, context), Is.Null);
            authority.ApplyBudget(release, context, 1L);
            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(20));
            Assert.That(window.HasReservation(P(1L)), Is.False);
        }

        [Test]
        public void BudgetNeverBecomesNegative()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            var authority = new TurnWindowBudgetAuthority(manager, null);
            TurnWindow window = OpenWindow(manager, world, 0L, 1L, budget: 5);
            var context = ExplicitContext(authority, window.WindowId, window.WindowId);

            var tooMuch = new[]
            {
                new TurnBudgetChangeRequest(
                    P(1L), window.WindowId, 6, 6, 0, isNewReservation: true, TurnBudgetChangeKind.Reserved)
            };
            Assert.That(authority.ValidateBudget(tooMuch, context),
                Is.EqualTo(TurnWindowCodes.INSUFFICIENT_WINDOW_BUDGET));
            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(5));

            Reserve(authority, window, P(1L), 5);
            Assert.That(window.AvailableBudgetTicks, Is.Zero);

            // 账本漂移（期望预留与实际不符）必须被识别为不变量错误，而不是静默补差。
            var drifted = new[]
            {
                new TurnBudgetChangeRequest(
                    P(1L), window.WindowId, 8, 3, 2, isNewReservation: false,
                    TurnBudgetChangeKind.ReservationAdjusted)
            };
            Assert.That(authority.ValidateBudget(drifted, context),
                Is.EqualTo(TurnWindowCodes.BUDGET_LEDGER_INVARIANT));

            Assert.That(window.ReservedBudgetTicks, Is.GreaterThanOrEqualTo(0));
            Assert.That(window.SpentBudgetTicks, Is.GreaterThanOrEqualTo(0));
            Assert.That(window.AvailableBudgetTicks, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void CostIncreaseRequiresValidOpenWindowAndAuthority()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            var authority = new TurnWindowBudgetAuthority(manager, null);
            TurnWindow window = OpenWindow(manager, world, 0L, 1L, budget: 10);
            Reserve(authority, window, P(1L), 4);

            var changes = new[]
            {
                new TurnBudgetChangeRequest(
                    P(1L), window.WindowId, 6, 2, 4, isNewReservation: false,
                    TurnBudgetChangeKind.ReservationAdjusted)
            };

            // 命令声明的 ExpectedWindowId 不是当前窗口：稳定拒绝，不得追加。
            var stale = ExplicitContext(authority, W(999L), window.WindowId);
            Assert.That(authority.ValidateBudget(changes, stale), Is.EqualTo(TurnWindowCodes.STALE_OR_CLOSED_WINDOW));
            Assert.That(window.ReservedFor(P(1L)), Is.EqualTo(4), "被拒绝的增费不得改变账本");

            // 窗口已请求关闭（不再接受提交）：增费同样稳定拒绝。
            window.RequestClose(TurnWindowCloseReason.OwnerRequested);
            var sameWindow = ExplicitContext(authority, window.WindowId, window.WindowId);
            Assert.That(authority.ValidateBudget(changes, sameWindow),
                Is.EqualTo(TurnWindowCodes.BUDGET_SOURCE_CLOSED_OR_MISMATCH));
            Assert.That(window.ReservedFor(P(1L)), Is.EqualTo(4));
        }

        [Test]
        public void CostIncreaseCannotTransferPlanBudgetToAnotherWindow()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            var authority = new TurnWindowBudgetAuthority(manager, null);

            TurnWindow first = OpenWindow(manager, world, 0L, 1L, budget: 10);
            Reserve(authority, first, P(1L), 4, 0L);

            first.RequestClose(TurnWindowCloseReason.OwnerRequested);
            manager.FinalizeRequestedClose(0L);
            TurnWindow second = OpenWindow(manager, world, 1L, 2L, budget: 10);

            // 计划 1 的来源窗口是 first：在新窗口里为它追加预留必须被拒绝
            // （首版不跨窗口转移或拆分计划预算来源）。
            var changes = new[]
            {
                new TurnBudgetChangeRequest(
                    P(1L), second.WindowId, 6, 2, 4, isNewReservation: false,
                    TurnBudgetChangeKind.ReservationAdjusted)
            };
            var context = ExplicitContext(authority, second.WindowId, second.WindowId);
            Assert.That(authority.ValidateBudget(changes, context), Is.Not.Null);
            Assert.That(second.ReservedFor(P(1L)), Is.Zero, "新窗口不得承载别的窗口的计划预算");
            Assert.That(first.ReservedFor(P(1L)), Is.EqualTo(4), "原窗口账本保持不变");
        }

        [Test]
        public void ClosedWindowLedgerPersistsUntilEditableReservationsSettle()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            var authority = new TurnWindowBudgetAuthority(manager, null);
            TurnWindow window = OpenWindow(manager, world, 0L, 1L, budget: 10);
            Reserve(authority, window, P(1L), 4, 0L);

            window.RequestClose(TurnWindowCloseReason.OwnerRequested);
            manager.FinalizeRequestedClose(0L);

            Assert.That(manager.FindWindow(window.WindowId), Is.SameAs(window), "关闭后的账本必须仍可审计");
            Assert.That(window.ReservedFor(P(1L)), Is.EqualTo(4), "关闭不得结算或丢弃未消费预留");
            Assert.That(window.SpentBudgetTicks, Is.Zero);

            var release = new[]
            {
                new TurnBudgetChangeRequest(
                    P(1L), window.WindowId, 0, -4, 4, isNewReservation: false,
                    TurnBudgetChangeKind.ReleasedBeforeLock)
            };
            var context = ExplicitContext(authority, null, null);
            Assert.That(authority.ValidateBudget(release, context), Is.Null, "已关闭窗口必须允许释放");
            authority.ApplyBudget(release, context, 1L);

            Assert.That(window.ReservedBudgetTicks, Is.Zero);
            Assert.That(window.AvailableBudgetTicks, Is.EqualTo(10), "释放额回到原窗口的历史账本");
        }

        [Test]
        public void ReleasingClosedWindowReservationDoesNotReopenSubmissions()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            var authority = new TurnWindowBudgetAuthority(manager, null);
            TurnWindow window = OpenWindow(manager, world, 0L, 1L, budget: 10);
            Reserve(authority, window, P(1L), 4, 0L);

            window.RequestClose(TurnWindowCloseReason.BudgetExhausted);
            manager.FinalizeRequestedClose(0L);

            var release = new[]
            {
                new TurnBudgetChangeRequest(
                    P(1L), window.WindowId, 0, -4, 4, isNewReservation: false,
                    TurnBudgetChangeKind.ReleasedBeforeLock)
            };
            var context = ExplicitContext(authority, null, null);
            authority.ApplyBudget(release, context, 1L);

            Assert.That(window.IsAcceptingSubmissions, Is.False, "释放不得重开提交权限");
            Assert.That(window.IsOpen, Is.False);
            Assert.That(manager.IsAcceptingSubmissions, Is.False);
            Assert.That(manager.OpenWindow, Is.Null);
            Assert.That(window.CloseReason, Is.EqualTo(TurnWindowCloseReason.BudgetExhausted));
        }

        [Test]
        public void BudgetExhaustionAndOwnerDeathMapToExplicitCloseReasons()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);

            TurnWindow first = OpenWindow(manager, world, 0L, 1L);
            manager.RequestCloseForOwnerDeath(U(2L));
            Assert.That(first.CloseReason, Is.Null, "非拥有者死亡不得关闭该窗口");
            manager.RequestCloseForOwnerDeath(U(1L));
            Assert.That(first.CloseReason, Is.EqualTo(TurnWindowCloseReason.OwnerDied));
            Assert.That(first.IsAcceptingSubmissions, Is.False);

            manager.FinalizeRequestedClose(0L);
            TurnWindow second = OpenWindow(manager, world, 1L, 3L);
            manager.RequestCloseForBudgetExhaustion();
            Assert.That(second.CloseReason, Is.EqualTo(TurnWindowCloseReason.BudgetExhausted));
        }

        [Test]
        public void BattleEndClearsReservationsAcrossOpenAndClosedWindows()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            var authority = new TurnWindowBudgetAuthority(manager, null);

            // 已关闭窗口上的历史账本 + 当前窗口上的未消费预留都要被清空。
            TurnWindow closed = OpenWindow(manager, world, 0L, 1L, budget: 10);
            Reserve(authority, closed, P(1L), 4, 0L);
            closed.RequestClose(TurnWindowCloseReason.OwnerRequested);
            manager.FinalizeRequestedClose(0L);

            TurnWindow current = OpenWindow(manager, world, 1L, 2L, budget: 10);
            Reserve(authority, current, P(2L), 3, 1L);

            world.Ended = true;
            manager.CloseAllForBattleEnd(1L);
            int released = authority.ClearReservationsForBattleEnd(1L);

            Assert.That(released, Is.EqualTo(7), "战斗结束清空的是全部未消费预留（4 + 3）");
            Assert.That(closed.ReservedBudgetTicks, Is.Zero);
            Assert.That(current.ReservedBudgetTicks, Is.Zero);
            Assert.That(current.IsOpen, Is.False);
            Assert.That(current.IsAcceptingSubmissions, Is.False);
            // 释放不形成可继续使用的授权：战斗结束后窗口永久关闭，且没有任何并发授权存在。
            Assert.That(manager.IsAcceptingSubmissions, Is.False);
        }

        // ================= 并发行动授权 =================

        private static ConcurrentActionSystem NewConcurrentSystem(
            TurnWindowManager manager, out int[] meta, int heroUnitId = 2, int metaResource = 5)
        {
            var store = new int[1];
            store[0] = metaResource;
            meta = store;
            var system = new ConcurrentActionSystem(
                new ConcurrentActionDefinition(MetaResourceCost: 3), manager)
            {
                HeroUnitId = U(heroUnitId)
            };
            system.BindMetaResource(() => store[0], value => store[0] = value);
            return system;
        }

        [Test]
        public void ConcurrentActivationRejectsStaleWindow()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            manager.RegisterControllerBinding(Player, U(2L));
            TurnWindow window = OpenWindow(manager, world, 0L, 1L);
            ConcurrentActionSystem concurrent = NewConcurrentSystem(manager, out int[] meta);

            Assert.That(concurrent.TryActivate(Player, U(2L), W(1234L)),
                Is.EqualTo(TurnWindowCodes.STALE_OR_CLOSED_WINDOW));
            Assert.That(concurrent.IsActive, Is.False);
            Assert.That(meta[0], Is.EqualTo(5), "被拒绝的激活不得消费局外资源");

            Assert.That(concurrent.TryActivate(Player, U(2L), window.WindowId), Is.Null);
            Assert.That(concurrent.IsActive, Is.True);
            Assert.That(meta[0], Is.EqualTo(2));
        }

        [Test]
        public void IssuerCannotActivateForUncontrolledUnit()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            TurnWindow window = OpenWindow(manager, world, 0L, 1L);
            ConcurrentActionSystem concurrent = NewConcurrentSystem(manager, out int[] meta);

            Assert.That(concurrent.TryActivate(Player, U(2L), window.WindowId),
                Is.EqualTo(TurnWindowCodes.ISSUER_CANNOT_CONTROL_UNIT));
            Assert.That(concurrent.IsActive, Is.False);
            Assert.That(meta[0], Is.EqualTo(5));

            // 主角由另一个控制者控制时，本发行者依然无权激活。
            manager.RegisterControllerBinding(C("controller.ai"), U(2L));
            Assert.That(concurrent.TryActivate(Player, U(2L), window.WindowId),
                Is.EqualTo(TurnWindowCodes.ISSUER_CANNOT_CONTROL_UNIT));
            Assert.That(meta[0], Is.EqualTo(5));
        }

        [Test]
        public void ConcurrentActivationRejectsControlledNonHeroUnit()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            TurnWindow window = OpenWindow(manager, world, 0L, 1L);
            manager.RegisterControllerBinding(Player, U(4L));
            ConcurrentActionSystem concurrent = NewConcurrentSystem(manager, out int[] meta, heroUnitId: 2);

            Assert.That(manager.CanControl(Player, U(4L)), Is.True);
            Assert.That(concurrent.TryActivate(Player, U(4L), window.WindowId),
                Is.EqualTo(TurnWindowCodes.INVALID_CONCURRENT_ACTOR));
            Assert.That(concurrent.IsActive, Is.False);
            Assert.That(meta[0], Is.EqualTo(5), "非主角激活不得消费局外资源");
        }

        [Test]
        public void ConcurrentCostComesFromAbilityDefinition()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            manager.RegisterControllerBinding(Player, U(2L));
            TurnWindow window = OpenWindow(manager, world, 0L, 1L);
            ConcurrentActionSystem concurrent = NewConcurrentSystem(manager, out int[] meta);

            Assert.That(concurrent.AuthorityCost, Is.EqualTo(3),
                "费用只能来自权威 ConcurrentActionDefinition");
            Assert.That(concurrent.TryActivate(Player, U(2L), window.WindowId), Is.Null);
            Assert.That(meta[0], Is.EqualTo(5 - concurrent.AuthorityCost),
                "激活恰好消费一次权威费用（调用方无法覆盖金额）");
        }

        [Test]
        public void RejectedConcurrentActivationDoesNotSpendMetaResource()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            manager.RegisterControllerBinding(Player, U(2L));
            TurnWindow window = OpenWindow(manager, world, 0L, 1L);
            ConcurrentActionSystem concurrent = NewConcurrentSystem(manager, out int[] meta, metaResource: 1);

            Assert.That(concurrent.TryActivate(Player, U(2L), window.WindowId),
                Is.EqualTo(TurnWindowCodes.INSUFFICIENT_META_RESOURCE));
            Assert.That(meta[0], Is.EqualTo(1));
            Assert.That(concurrent.IsActive, Is.False);
        }

        [Test]
        public void ConcurrentActivationRejectsOwnWindow()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            manager.RegisterControllerBinding(Player, U(1L));
            TurnWindow window = OpenWindow(manager, world, 0L, 1L);
            ConcurrentActionSystem concurrent = NewConcurrentSystem(manager, out int[] meta, heroUnitId: 1);

            Assert.That(concurrent.TryActivate(Player, U(1L), window.WindowId),
                Is.EqualTo(TurnWindowCodes.PLAYER_OWNS_WINDOW));
            Assert.That(meta[0], Is.EqualTo(5), "自己的窗口天然持有提交权，不需要也不允许购买并发授权");
        }

        [Test]
        public void ConcurrentAuthorityCannotSubmitBlockOrDodge()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            manager.RegisterControllerBinding(Player, U(2L));
            TurnWindow window = OpenWindow(manager, world, 0L, 1L);
            ConcurrentActionSystem concurrent = NewConcurrentSystem(manager, out int[] meta);

            Assert.That(concurrent.TryActivate(Player, U(2L), window.WindowId), Is.Null);

            Assert.That(concurrent.CanSubmitOrdinaryAction(Player, U(2L), window.WindowId, ActionType.Block),
                Is.False, "并发授权绝不授权 Block");
            Assert.That(concurrent.CanSubmitOrdinaryAction(Player, U(2L), window.WindowId, ActionType.Dodge),
                Is.False, "并发授权绝不授权 Dodge");
            Assert.That(concurrent.CanSubmitOrdinaryAction(Player, U(2L), window.WindowId, ActionType.Attack),
                Is.True);
            Assert.That(concurrent.CanSubmitOrdinaryAction(Player, U(2L), window.WindowId, ActionType.Move),
                Is.True);
        }

        [Test]
        public void ConcurrentAuthorityEndsWithWindowButScheduledPlanSurvives()
        {
            var world = new WorldView();
            var manager = new TurnWindowManager(world);
            var authority = new TurnWindowBudgetAuthority(manager, null);
            manager.RegisterControllerBinding(Player, U(2L));
            TurnWindow window = OpenWindow(manager, world, 0L, 1L);
            ConcurrentActionSystem concurrent = NewConcurrentSystem(manager, out int[] meta);

            Assert.That(concurrent.TryActivate(Player, U(2L), window.WindowId), Is.Null);
            Reserve(authority, window, P(11L), 4, 0L);
            manager.WindowClosedAuthoritySink = (windowId, tick) => concurrent.RevokeForWindow(windowId);

            window.RequestClose(TurnWindowCloseReason.OwnerRequested);
            manager.FinalizeRequestedClose(0L);

            Assert.That(concurrent.IsActive, Is.False, "窗口关闭必须撤销授权");
            Assert.That(concurrent.CanSubmitOrdinaryAction(Player, U(2L), window.WindowId, ActionType.Attack),
                Is.False, "关闭后不得再授权该窗口的提交");
            Assert.That(window.ReservedFor(P(11L)), Is.EqualTo(4),
                "已接受计划继续存在：窗口切换不触碰已有预留");
            Assert.That(window.SpentBudgetTicks, Is.Zero);
        }

        // ================= 肾上腺素账本 =================

        private static AdrenalineLedgerRegistry NewRegistry(out List<AdrenalineLedgerChange> events)
        {
            var sink = new List<AdrenalineLedgerChange>();
            events = sink;
            return new AdrenalineLedgerRegistry(AdrenalineRules.FrozenV1) { ChangedSink = sink.Add };
        }

        [Test]
        public void AdrenalinePersistsAcrossOtherUnitsWindowsAndOwnWindowClose()
        {
            AdrenalineLedgerRegistry registry = NewRegistry(out _);
            AdrenalineLedger hero = registry.Register(U(2L), initialAvailable: 7, initialCycleId: 0L);
            AdrenalineLedger other = registry.Register(U(1L), initialAvailable: 0, initialCycleId: 0L);

            registry.BeginOwnWindow(U(1L));
            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(7), "Available 不随其他单位窗口变化");
            Assert.That(hero.CycleId, Is.Zero);
            Assert.That(other.CycleId, Is.EqualTo(1L));

            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(7), "窗口关闭不清零肾上腺素");
        }

        [Test]
        public void OwnersNextWindowOpenClearsAvailableAdrenalineBeforeCommands()
        {
            AdrenalineLedgerRegistry registry = NewRegistry(out _);
            AdrenalineLedger hero = registry.Register(U(2L), initialAvailable: 7, initialCycleId: 0L);

            registry.BeginOwnWindow(U(2L));

            Assert.That(hero.CycleId, Is.EqualTo(1L), "自己的窗口打开先递增个人周期");
            Assert.That(hero.AvailableAdrenaline, Is.Zero, "再清零 Available");
        }

        [Test]
        public void ReactionAcceptanceReservesAdrenalineExactlyOnce()
        {
            AdrenalineLedgerRegistry registry = NewRegistry(out _);
            AdrenalineLedger hero = registry.Register(U(2L), initialAvailable: 5, initialCycleId: 0L);

            Assert.That(registry.TryReserveForReaction(U(2L), P(7L), 2), Is.Null);
            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(3));
            Assert.That(hero.ReservedAdrenaline, Is.EqualTo(2));
            Assert.That(hero.ReservationOf(P(7L)).ReservationCycleId, Is.Zero);

            Assert.That(registry.TryReserveForReaction(U(2L), P(7L), 2),
                Is.EqualTo(AdrenalineLedgerCodes.DUPLICATE_RESERVATION));
            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(3));

            Assert.That(registry.TryReserveForReaction(U(2L), P(8L), 9),
                Is.EqualTo(AdrenalineLedgerCodes.INSUFFICIENT_ADRENALINE));
            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(3));
            Assert.That(hero.ReservationCount, Is.EqualTo(1));
        }

        [Test]
        public void ReactionTriggerConsumesReservationExactlyOnce()
        {
            AdrenalineLedgerRegistry registry = NewRegistry(out _);
            AdrenalineLedger hero = registry.Register(U(2L), initialAvailable: 5, initialCycleId: 0L);
            Assert.That(registry.TryReserveForReaction(U(2L), P(7L), 2), Is.Null);

            registry.ConsumeAtTrigger(P(7L));
            Assert.That(hero.ReservationOf(P(7L)), Is.Null);
            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(3), "消费不退费");
            Assert.That(hero.ReservedAdrenaline, Is.Zero);

            registry.ConsumeAtTrigger(P(7L));
            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(3), "重复消费是幂等无操作");
            Assert.That(hero.ReservedAdrenaline, Is.Zero);
        }

        [Test]
        public void SourceThreatCancellationRefundsOnlyCurrentCycleReservation()
        {
            AdrenalineLedgerRegistry registry = NewRegistry(out List<AdrenalineLedgerChange> events);
            AdrenalineLedger hero = registry.Register(U(2L), initialAvailable: 5, initialCycleId: 0L);
            Assert.That(registry.TryReserveForReaction(U(2L), P(7L), 2), Is.Null);
            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(3));

            registry.ReleaseForSourceThreatCancelled(P(7L));

            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(5), "当前周期的预留必须返还 Available");
            Assert.That(hero.ReservationOf(P(7L)), Is.Null);
            Assert.That(events[events.Count - 1].ChangeKind, Is.EqualTo(AdrenalineChangeKind.Refunded));
            Assert.That(events[events.Count - 1].AvailableBefore, Is.EqualTo(3));
            Assert.That(events[events.Count - 1].AvailableAfter, Is.EqualTo(5));

            registry.ReleaseForSourceThreatCancelled(P(7L));
            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(5), "重复释放是幂等无操作");
        }

        [Test]
        public void OldCycleReservationSurvivesToTriggerButCannotRefundIntoNewCycle()
        {
            AdrenalineLedgerRegistry registry = NewRegistry(out List<AdrenalineLedgerChange> events);
            AdrenalineLedger hero = registry.Register(U(2L), initialAvailable: 6, initialCycleId: 0L);

            Assert.That(registry.TryReserveForReaction(U(2L), P(7L), 2), Is.Null);
            registry.BeginOwnWindow(U(2L));
            Assert.That(hero.CycleId, Is.EqualTo(1L));
            Assert.That(hero.ReservationOf(P(7L)).ReservationCycleId, Is.Zero, "已有合法预留继续存在");
            Assert.That(hero.ReservedAdrenaline, Is.EqualTo(2), "窗口打开不清零预留");

            registry.ReleaseForSourceThreatCancelled(P(7L));
            Assert.That(hero.AvailableAdrenaline, Is.Zero, "旧周期预留不得返还到新周期");
            Assert.That(hero.ReservationOf(P(7L)), Is.Null);
            Assert.That(events[events.Count - 1].ChangeKind,
                Is.EqualTo(AdrenalineChangeKind.StaleCycleReservationRemoved));

            // 跨过拥有者自己的窗口边界之后，预留依然可以正常触发消费（只是不能退款）。
            // 新周期的资源只能来自规范入账事实（唯一增长入口），不得靠旧周期返还。
            registry.ApplyTickEndAccrual(new[] { new AdrenalineAccrualFacts(U(2L), 0, 0, 0, 0, 0) });
            Assert.That(hero.AvailableAdrenaline, Is.Zero, "跨周期之后 Available 仍然为零");
            Assert.That(registry.TryReserveForReaction(U(2L), P(8L), 2),
                Is.EqualTo(AdrenalineLedgerCodes.INSUFFICIENT_ADRENALINE),
                "新周期不得继承旧周期的资源");
            registry.BeginOwnWindow(U(2L));
            Assert.That(hero.CycleId, Is.EqualTo(2L));
            Assert.That(hero.AvailableAdrenaline, Is.Zero);

            // 在旧周期预留存活期间跨过第二个窗口边界：它仍然可以被触发消费（不退费）。
            registry.ApplyTickEndAccrual(new[] { new AdrenalineAccrualFacts(U(2L), 10, 0, 0, 0, 0) });
            Assert.That(registry.TryReserveForReaction(U(2L), P(9L), 2), Is.Null);
            registry.BeginOwnWindow(U(2L));
            Assert.That(hero.CycleId, Is.EqualTo(3L));
            Assert.That(hero.ReservationOf(P(9L)).ReservationCycleId, Is.EqualTo(2L));
            registry.ConsumeAtTrigger(P(9L));
            Assert.That(hero.ReservationOf(P(9L)), Is.Null);
            Assert.That(hero.AvailableAdrenaline, Is.Zero, "跨周期消费同样不退费");
        }

        [Test]
        public void InterruptedOrInvalidReactionDoesNotRefundAdrenaline()
        {
            AdrenalineLedgerRegistry registry = NewRegistry(out List<AdrenalineLedgerChange> events);
            AdrenalineLedger hero = registry.Register(U(2L), initialAvailable: 5, initialCycleId: 0L);
            Assert.That(registry.TryReserveForReaction(U(2L), P(7L), 2), Is.Null);

            // 账本上只有「来源威胁取消」这一条返还入口：主动取消/被控制/死亡/目的格失效/已触发
            // 都没有对应入口，因此预留只能被消费掉。
            int refundEvents = events.Count(e => e.ChangeKind == AdrenalineChangeKind.Refunded);
            registry.ConsumeAtTrigger(P(7L));

            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(3), "被中断的反应不退款");
            Assert.That(hero.ReservedAdrenaline, Is.Zero);
            Assert.That(events.Count(e => e.ChangeKind == AdrenalineChangeKind.Refunded), Is.EqualTo(refundEvents));
        }

        [Test]
        public void AdrenalineDamageGainAggregatesOncePerUnitPerTick()
        {
            AdrenalineLedgerRegistry registry = NewRegistry(out List<AdrenalineLedgerChange> events);
            registry.Register(U(1L), 0, 0L);
            registry.Register(U(2L), 0, 0L);

            int before = events.Count;
            registry.ApplyTickEndAccrual(new[]
            {
                new AdrenalineAccrualFacts(U(1L), 10, 10, 0, 0, 0),
                new AdrenalineAccrualFacts(U(2L), 0, 0, 0, 0, 0)
            });

            AdrenalineLedger first = registry.Find(U(1L));
            Assert.That(first.AvailableAdrenaline, Is.EqualTo(30),
                "每个伤害类别只量化一次：10*102/100 + 10*205/100 = 30");
            Assert.That(events.Count - before, Is.EqualTo(1), "零增益单位不产生入账事件");

            // 重复单位键是装配矛盾：显式失败，绝不静默累加（否则就是逐接触重复发放）。
            Assert.Throws<LogicDefinitionException>(() => registry.ApplyTickEndAccrual(new[]
            {
                new AdrenalineAccrualFacts(U(1L), 10, 0, 0, 0, 0),
                new AdrenalineAccrualFacts(U(1L), 10, 0, 0, 0, 0)
            }));
        }

        [Test]
        public void AdrenalineOutcomeRewardOccursOncePerPlanOrConflictGroup()
        {
            AdrenalineLedgerRegistry registry = NewRegistry(out _);
            registry.Register(U(2L), 0, 0L);

            registry.ApplyTickEndAccrual(new[]
            {
                new AdrenalineAccrualFacts(U(2L), 0, 0, 1, 0, 1)
            });

            AdrenalineLedger hero = registry.Find(U(2L));
            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(51),
                "每个反应计划至多一次 Block 成功奖励（1）+ 每个 ConflictGroup 至多一次 Clash 奖励（50）");
        }

        [Test]
        public void AvailableAdrenalineIsCappedWithoutChangingReservations()
        {
            AdrenalineLedgerRegistry registry = NewRegistry(out _);
            AdrenalineLedger hero = registry.Register(U(2L), initialAvailable: 5, initialCycleId: 0L);
            Assert.That(registry.TryReserveForReaction(U(2L), P(7L), 2), Is.Null);

            registry.ApplyTickEndAccrual(new[]
            {
                new AdrenalineAccrualFacts(U(2L), 100000, 100000, 0, 0, 0)
            });

            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(AdrenalineRules.FrozenV1.MaxAvailablePerCycle),
                "Available 必须被裁到周期上限");
            Assert.That(hero.ReservedAdrenaline, Is.EqualTo(2), "裁剪不得改变任何预留");
            Assert.That(hero.ReservationOf(P(7L)).ReservedAmount, Is.EqualTo(2));
        }

        [Test]
        public void SuccessfulBlockRewardIsLowerThanCostAndCannotSelfLoop()
        {
            AdrenalineRules rules = AdrenalineRules.FrozenV1;
            Assert.That(rules.SuccessfulBlockReward, Is.LessThan(FrozenDesignValues.BlockAdrenalineCost));

            AdrenalineLedgerRegistry registry = NewRegistry(out _);
            AdrenalineLedger hero = registry.Register(
                U(2L), initialAvailable: FrozenDesignValues.BlockAdrenalineCost, initialCycleId: 0L);

            Assert.That(registry.TryReserveForReaction(U(2L), P(7L), FrozenDesignValues.BlockAdrenalineCost), Is.Null);
            registry.ConsumeAtTrigger(P(7L));
            registry.ApplyTickEndAccrual(new[] { new AdrenalineAccrualFacts(U(2L), 0, 0, 1, 0, 0) });

            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(rules.SuccessfulBlockReward));
            Assert.That(hero.AvailableAdrenaline, Is.LessThan(FrozenDesignValues.BlockAdrenalineCost),
                "成功奖励低于费用，不能自我循环");
            Assert.That(registry.TryReserveForReaction(U(2L), P(8L), FrozenDesignValues.BlockAdrenalineCost),
                Is.EqualTo(AdrenalineLedgerCodes.INSUFFICIENT_ADRENALINE));
        }

        [Test]
        public void SuccessfulDodgeRewardIsLowerThanCostAndCannotSelfLoop()
        {
            AdrenalineRules rules = AdrenalineRules.FrozenV1;
            Assert.That(rules.SuccessfulDodgeReward, Is.LessThan(FrozenDesignValues.DodgeAdrenalineCost));

            AdrenalineLedgerRegistry registry = NewRegistry(out _);
            AdrenalineLedger hero = registry.Register(
                U(2L), initialAvailable: FrozenDesignValues.DodgeAdrenalineCost, initialCycleId: 0L);

            Assert.That(registry.TryReserveForReaction(U(2L), P(7L), FrozenDesignValues.DodgeAdrenalineCost), Is.Null);
            registry.ConsumeAtTrigger(P(7L));
            registry.ApplyTickEndAccrual(new[] { new AdrenalineAccrualFacts(U(2L), 0, 0, 0, 1, 0) });

            Assert.That(hero.AvailableAdrenaline, Is.EqualTo(rules.SuccessfulDodgeReward));
            Assert.That(hero.AvailableAdrenaline, Is.LessThan(FrozenDesignValues.DodgeAdrenalineCost),
                "成功奖励低于费用，不能自我循环");
        }

        [Test]
        public void BattleEndClearsAdrenalineWithoutRefund()
        {
            AdrenalineLedgerRegistry registry = NewRegistry(out List<AdrenalineLedgerChange> events);
            AdrenalineLedger hero = registry.Register(U(2L), initialAvailable: 5, initialCycleId: 0L);
            Assert.That(registry.TryReserveForReaction(U(2L), P(7L), 2), Is.Null);

            registry.ClearForBattleEnd();

            Assert.That(hero.AvailableAdrenaline, Is.Zero);
            Assert.That(hero.ReservedAdrenaline, Is.Zero);
            Assert.That(events[events.Count - 1].ChangeKind, Is.EqualTo(AdrenalineChangeKind.BattleEndCleared));
            Assert.That(hero.AvailableAdrenaline, Is.Zero, "清空后不得留下可继续使用的额度");
        }

        [Test]
        public void AdrenalineSnapshotsAreOrderedAndCarryCycleAndReservations()
        {
            AdrenalineLedgerRegistry registry = NewRegistry(out _);
            registry.Register(U(20L), 1, 0L);
            registry.Register(U(10L), 5, 2L);
            Assert.That(registry.TryReserveForReaction(U(10L), P(3L), 2), Is.Null);

            var snapshots = registry.BuildSnapshots();

            Assert.That(snapshots.Count, Is.EqualTo(2));
            Assert.That(snapshots[0].UnitId, Is.EqualTo(10L), "快照必须按 UnitId 升序");
            Assert.That(snapshots[1].UnitId, Is.EqualTo(20L));
            Assert.That(snapshots[0].CycleId, Is.EqualTo(2L));
            Assert.That(snapshots[0].AvailableAdrenaline, Is.EqualTo(3));
            Assert.That(snapshots[0].Reservations.Count, Is.EqualTo(1));
            Assert.That(snapshots[0].Reservations[0].ActionPlanId, Is.EqualTo(3L));
            Assert.That(snapshots[0].Reservations[0].ReservedAmount, Is.EqualTo(2));
            Assert.That(snapshots[0].Reservations[0].ReservationCycleId, Is.EqualTo(2L));
        }
    }
}
