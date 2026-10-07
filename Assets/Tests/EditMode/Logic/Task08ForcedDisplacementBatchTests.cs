using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 08 ·「同时强制位移协议」的<strong>批次 / 抢占 / 原子提交用例</strong>
    /// （任务包 :349-353、:355、:356、:359、:360，以及架构审查 R9 建议的补充用例）。
    ///
    /// 观察点是<strong>生产对象</strong>：<see cref="ForcedDisplacementSolver"/> 的
    /// <c>InvalidatedPlanIds</c>、<see cref="ForcedDisplacementCommitter"/> 经
    /// <see cref="ActionPlanTerminalCoordinator"/> 实际写入的终态原因、
    /// <see cref="LogicGrid.ReservationsOfPlanOrdered"/> 的剩余预留，以及
    /// <see cref="ForcedDisplacementBatch"/> 的 <c>Resolutions</c>/<c>Relocations</c> 分工。
    ///
    /// <para>
    /// ⚠ 夹具必须绕开的一条生产接缝（与 <c>Task07ForcedDisplacementBudgetTests</c> 同一口径）：
    /// <c>ActionScheduleAuthority.RegisterPlan</c> 是 <c>internal</c>，而
    /// <c>ProjectHero.Logic.Tests</c> 没有 <c>InternalsVisibleTo</c>。因此本文件只用反射完成
    /// "把计划接入权威"这一步；被测的求解、抢占、终态、释放代码路径<strong>没有</strong>任何放宽。
    /// </para>
    /// </summary>
    public class Task08ForcedDisplacementBatchTests
    {
        private static readonly GridBoundaryDefinition Wide =
            new GridBoundaryDefinition(new GridPoint(-20, -20), new GridPoint(20, 20));

        private static UnitId U(long id) => new UnitId(id);
        private static ActionPlanId P(long id) => new ActionPlanId(id);
        private static ReactionOpportunityId O(long id) => new ReactionOpportunityId(id);

        private static ForcedDisplacementRequest Request(long unitId, GridDirection direction, int steps)
            => new ForcedDisplacementRequest(U(unitId), direction, steps, 10L, 1L);

        // ================= 反射接缝（唯一两处） =================

        private static readonly ConstructorInfo PlanConstructor = typeof(ActionPlan).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[]
            {
                typeof(ActionPlanId), typeof(ActionPlanOrigin), typeof(UnitId), typeof(ActionSpecId),
                typeof(ActionType), typeof(GridDirection), typeof(UnitId?), typeof(GridPoint?),
                typeof(WindowId?), typeof(ActionPlanTriggerBinding), typeof(long)
            },
            null);

        private static readonly MethodInfo RegisterPlanMethod = typeof(ActionScheduleAuthority).GetMethod(
            "RegisterPlan", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(ActionPlan) }, null);

        private static ActionPlan AddPlan(
            ActionScheduleAuthority schedule, long planId, long ownerUnitId, ActionType type)
        {
            Assert.That(PlanConstructor, Is.Not.Null, "ActionPlan 的非公开构造函数必须存在");
            Assert.That(RegisterPlanMethod, Is.Not.Null, "ActionScheduleAuthority.RegisterPlan 必须存在");

            var plan = (ActionPlan)PlanConstructor.Invoke(new object[]
            {
                new ActionPlanId(planId), ActionPlanOrigin.Ordinary, new UnitId(ownerUnitId),
                new ActionSpecId("action.t08.probe"), type, GridDirection.East,
                null, null, null, null, 0L
            });
            RegisterPlanMethod.Invoke(schedule, new object[] { plan });
            return plan;
        }

        // ================= 夹具 =================

        private sealed class Rig
        {
            public LogicGrid Grid;
            public ActionScheduleAuthority Schedule;
            public ActionPlanTerminalCoordinator Coordinator;
            public DodgeRelocationAuthority Dodge;
            public ForcedDisplacementSolver Solver;
            public ForcedDisplacementCommitter Committer;
            public readonly ForcedDisplacementStats Stats = new ForcedDisplacementStats();

            public ForcedDisplacementBatch Solve(params ForcedDisplacementRequest[] requests)
                => Solver.ResolveAll(requests, Snapshots(Grid), Wide, 0L);

            public IReadOnlyList<ActionPlanId> Commit(ForcedDisplacementBatch batch)
                => Committer.TerminateInvalidatedMovementPlansOrdered(batch, 0L);
        }

        private static UnitSnapshot[] Snapshots(LogicGrid grid)
        {
            IReadOnlyList<UnitId> units = grid.RegisteredUnitsOrdered();
            var result = new List<UnitSnapshot>(units.Count);
            for (int i = 0; i < units.Count; i++)
            {
                grid.TryGetAnchor(units[i], out GridPoint anchor);
                grid.TryGetFacing(units[i], out GridDirection facing);
                result.Add(new UnitSnapshot(units[i].Value, "unit.test", "faction.test",
                    anchor.X, anchor.Y, (int)facing, 1000, true, 0, 1L));
            }
            return result.ToArray();
        }

        private static void Place(LogicGrid grid, long unitId, int x, int y)
        {
            string error = grid.RegisterUnitWithPointFootprint(U(unitId), new GridPoint(x, y), GridDirection.East);
            Assert.That(error, Is.Null, "夹具前提：单位必须注册成功 unit=" + unitId + " code=" + error);
        }

        private static void Reserve(LogicGrid grid, long planId, long ownerUnitId, int x, int y,
            long startTick, long endTick, int stepIndex = 0)
        {
            string error = grid.TryReserve(new Reservation(
                new ReservationKey(P(planId), stepIndex), U(ownerUnitId), new GridPoint(x, y),
                startTick, endTick));
            Assert.That(error, Is.Null, "夹具前提：预留必须被接受 plan=" + planId + " code=" + error);
        }

        private static Rig NewRig(bool withMovementParticipant = false)
        {
            var grid = new LogicGrid(Wide);
            var schedule = new ActionScheduleAuthority();
            var movement = new LogicGridMovementAuthority(
                grid, schedule, BattleRules.FrozenV1.PathCostRules, BattleRules.FrozenV1.PathSearchRules);

            var participants = new List<IActionPlanCleanupParticipant> { StopSchedulingCleanupParticipant.Instance };
            if (withMovementParticipant) participants.Add(movement);

            var coordinator = new ActionPlanTerminalCoordinator(schedule, participants);
            var dodge = new DodgeRelocationAuthority(grid, movement, schedule, coordinator, _ => null);
            movement.DodgeDestinationReservations = dodge;

            var rig = new Rig
            {
                Grid = grid,
                Schedule = schedule,
                Coordinator = coordinator,
                Dodge = dodge
            };
            rig.Solver = new ForcedDisplacementSolver(grid, schedule, dodge, null, rig.Stats);
            rig.Committer = new ForcedDisplacementCommitter(schedule, coordinator);
            return rig;
        }

        // ================= 349 / 350 / 351：Reservation 抢占 =================

        /// <summary>
        /// 缺陷指纹：若抢占范围用"临时模拟路径穿过的格"而不是最终 footprint，中间格上的预留会被
        /// 错误终止（任务包 :216 明确禁止）。
        /// </summary>
        [Test]
        public void OnlyFinalFootprintReservationConflictsArePreempted()
        {
            Rig rig = NewRig();
            Place(rig.Grid, 1L, 0, 0);                       // 单位 1：East 2 ⇒ 中间 (2,0)，最终 (4,0)
            AddPlan(rig.Schedule, 10L, 9L, ActionType.Move);
            AddPlan(rig.Schedule, 11L, 9L, ActionType.Move);
            Reserve(rig.Grid, 10L, 9L, 2, 0, 0L, 10L);       // 只在中间格
            Reserve(rig.Grid, 11L, 9L, 4, 0, 0L, 10L);       // 在最终格

            ForcedDisplacementBatch batch = rig.Solve(Request(1L, GridDirection.East, 2));
            ForcedDisplacementResolution resolution = batch.Resolutions.Single();

            Assert.That(resolution.To, Is.EqualTo(new GridPoint(4, 0)), "夹具前提：最终格 (4,0)");
            Assert.That(resolution.InvalidatedPlanIds.Select(id => id.Value), Is.EqualTo(new[] { 11L }),
                "只有与最终 footprint 相交的预留才进入候选");

            IReadOnlyList<ActionPlanId> terminated = rig.Commit(batch);
            Assert.That(terminated.Select(id => id.Value), Is.EqualTo(new[] { 11L }));
            Assert.That(rig.Schedule.Registry.Find(P(11L)).TerminationReason,
                Is.EqualTo(ActionTerminationReason.ReservationPreemptedByForcedDisplacement));
            Assert.That(rig.Schedule.Registry.Find(P(10L)).IsTerminal, Is.False,
                "只在临时路径上被穿过的预留不得终止其计划");
        }

        /// <summary>
        /// 缺陷指纹：若提交按 Resolution 输入顺序或按预留枚举顺序终止，计划顺序会不稳定；
        /// 冻结规则是按 <see cref="ActionPlanId"/> 升序。
        /// </summary>
        [Test]
        public void ReservationPreemptionTerminatesPlansInActionPlanIdOrder()
        {
            var grid = new LogicGrid(Wide);
            // 2 格单位：最终 footprint 有两个不同的格，分别被 12 与 11 预留。
            IReadOnlyList<DirectionalTriangleSet> directions = TwoCellDirections();
            Assert.That(grid.RegisterUnit(U(1L), new GridPoint(0, 0), GridDirection.East, directions), Is.Null);

            var schedule = new ActionScheduleAuthority();
            var coordinator = new ActionPlanTerminalCoordinator(schedule);
            var rig = new Rig
            {
                Grid = grid, Schedule = schedule, Coordinator = coordinator, Dodge = null
            };
            rig.Solver = new ForcedDisplacementSolver(grid, schedule, null, null, rig.Stats);
            rig.Committer = new ForcedDisplacementCommitter(schedule, coordinator);

            AddPlan(schedule, 11L, 9L, ActionType.Move);
            AddPlan(schedule, 12L, 9L, ActionType.Move);

            // 故意让"预留枚举顺序"与 ActionPlanId 顺序相反：先建 12 的预留，再建 11 的。
            GridPoint targetAnchor = new GridPoint(0, 0).Translate(
                GridNeighborTable.OffsetX(GridDirection.East), GridNeighborTable.OffsetY(GridDirection.East));
            IReadOnlyList<GridPoint> cells = grid.ResolveDestinationCells(U(1L), targetAnchor, GridDirection.East);
            Assert.That(cells.Count, Is.GreaterThanOrEqualTo(2), "夹具前提：多格单位有至少两个不同的最终格");
            Assert.That(cells[0], Is.Not.EqualTo(cells[1]));
            Reserve(grid, 12L, 9L, cells[1].X, cells[1].Y, 0L, 10L);
            Reserve(grid, 11L, 9L, cells[0].X, cells[0].Y, 0L, 10L);

            ForcedDisplacementBatch batch = rig.Solve(Request(1L, GridDirection.East, 1));
            ForcedDisplacementResolution resolution = batch.Resolutions.Single();
            Assert.That(resolution.InvalidatedPlanIds.Count, Is.EqualTo(2), "夹具前提：两个最终格各一条预留");

            IReadOnlyList<ActionPlanId> terminated = rig.Commit(batch);
            Assert.That(terminated.Select(id => id.Value), Is.EqualTo(new[] { 11L, 12L }),
                "终态提交必须按 ActionPlanId 升序，而不是按预留/输入枚举顺序");
        }

        /// <summary>
        /// 缺陷指纹：若提交器只写终态而不让任务 06 的清理参与者释放预留，终态计划会留下悬空预留
        /// （违反不变量 19，且下一次批量换位的第 6 步预检会硬失败）。
        /// </summary>
        [Test]
        public void ReservationPreemptionReleasesAllFutureSegmentsAndReservations()
        {
            Rig rig = NewRig(withMovementParticipant: true);
            Place(rig.Grid, 1L, 0, 0);
            AddPlan(rig.Schedule, 11L, 9L, ActionType.Move);
            Reserve(rig.Grid, 11L, 9L, 4, 0, 0L, 10L);
            Reserve(rig.Grid, 11L, 9L, 6, 0, 10L, 20L, stepIndex: 1);
            Assert.That(rig.Grid.ReservationsOfPlanOrdered(P(11L)).Count, Is.EqualTo(2));

            ForcedDisplacementBatch batch = rig.Solve(Request(1L, GridDirection.East, 2));
            IReadOnlyList<ActionPlanId> terminated = rig.Commit(batch);

            Assert.That(terminated.Select(id => id.Value), Is.EqualTo(new[] { 11L }));
            Assert.That(rig.Grid.ReservationsOfPlanOrdered(P(11L)), Is.Empty,
                "终态清理参与者必须释放该计划全部未来预留");
            Assert.That(rig.Schedule.Registry.Find(P(11L)).IsTerminal, Is.True);
            Assert.That(rig.Schedule.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null,
                "终态之后不得留下引用它的活动产物");
        }

        /// <summary>
        /// 架构审查 R9 建议补充：07 之后<strong>一格可持有多条预留</strong>（同单位 + 时间窗不相交），
        /// 抢占必须遍历<strong>全部持有者</strong>；只读 <c>ReservationAt</c>（规范持有者）会漏一条。
        /// </summary>
        [Test]
        public void ForcedDisplacementPreemptsEveryHolderOnMultiHolderCell()
        {
            Rig rig = NewRig(withMovementParticipant: true);
            Place(rig.Grid, 1L, 0, 0);
            AddPlan(rig.Schedule, 11L, 9L, ActionType.Move);
            AddPlan(rig.Schedule, 12L, 9L, ActionType.Move);

            // 同一格、同一单位、时间窗互不相交 ⇒ 两条预留共存（07 裁定 B 的唯一放宽）。
            Reserve(rig.Grid, 11L, 9L, 4, 0, 0L, 10L);
            Reserve(rig.Grid, 12L, 9L, 4, 0, 10L, 20L);
            Assert.That(rig.Grid.AllReservationsOrdered().Count, Is.EqualTo(2), "夹具前提：一格两条持有者");

            ForcedDisplacementBatch batch = rig.Solve(Request(1L, GridDirection.East, 2));
            ForcedDisplacementResolution resolution = batch.Resolutions.Single();

            Assert.That(resolution.InvalidatedPlanIds.Select(id => id.Value), Is.EqualTo(new[] { 11L, 12L }),
                "多持有者格必须遍历全部持有者（用 ReservationAt 只会得到规范持有者）");

            IReadOnlyList<ActionPlanId> terminated = rig.Commit(batch);
            Assert.That(terminated.Select(id => id.Value), Is.EqualTo(new[] { 11L, 12L }));
            Assert.That(rig.Grid.AllReservationsOrdered(), Is.Empty);
        }

        // ================= 352 / 353 / 355：旧起点失效 =================

        /// <summary>
        /// 缺陷指纹：若"实际被换位单位的旧起点 Move"没被收集，被位移单位的活动/排队 Move 会保留，
        /// 下一 Tick 就会以旧起点继续推进（旧起点已被腾空 ⇒ 位置与路径分叉）。
        /// </summary>
        [Test]
        public void DisplacedUnitsOldOriginMovePlansTerminateAsMovementOriginInvalidated()
        {
            Rig rig = NewRig();
            Place(rig.Grid, 1L, 0, 0);
            AddPlan(rig.Schedule, 21L, 1L, ActionType.Move);      // 被位移单位自己的排队 Move
            AddPlan(rig.Schedule, 22L, 2L, ActionType.Move);      // 别的单位：不受影响

            ForcedDisplacementBatch batch = rig.Solve(Request(1L, GridDirection.East, 2));
            ForcedDisplacementResolution resolution = batch.Resolutions.Single();
            Assert.That(resolution.AppliedSteps, Is.EqualTo(2), "夹具前提：单位 1 必须真的被换位");
            Assert.That(resolution.InvalidatedPlanIds.Select(id => id.Value), Is.EqualTo(new[] { 21L }));

            IReadOnlyList<ActionPlanId> terminated = rig.Commit(batch);
            Assert.That(terminated.Select(id => id.Value), Is.EqualTo(new[] { 21L }));
            Assert.That(rig.Schedule.Registry.Find(P(21L)).TerminationReason,
                Is.EqualTo(ActionTerminationReason.MovementOriginInvalidated));
            Assert.That(rig.Schedule.Registry.Find(P(22L)).IsTerminal, Is.False,
                "不得用'单位被推动'作为取消其他单位计划的全局理由");
        }

        /// <summary>
        /// 缺陷指纹：若原因分类按"先看预留、后看旧起点"，同一计划会拿到
        /// <c>ReservationPreemptedByForcedDisplacement</c>；冻结优先级要求
        /// <c>MovementOriginInvalidated</c> 胜出（:209、<c>ActionTerminationReasons.PriorityOf</c>）。
        /// </summary>
        [Test]
        public void MovementOriginInvalidatedWinsWhenSamePlanAlsoOwnsConflictingReservation()
        {
            Rig rig = NewRig();
            Place(rig.Grid, 1L, 0, 0);
            AddPlan(rig.Schedule, 31L, 1L, ActionType.Move);
            Reserve(rig.Grid, 31L, 1L, 4, 0, 0L, 10L);           // 同时持有与最终格冲突的预留

            ForcedDisplacementBatch batch = rig.Solve(Request(1L, GridDirection.East, 2));
            Assert.That(batch.Resolutions.Single().InvalidatedPlanIds.Select(id => id.Value),
                Is.EqualTo(new[] { 31L }));

            rig.Commit(batch);
            Assert.That(rig.Schedule.Registry.Find(P(31L)).TerminationReason,
                Is.EqualTo(ActionTerminationReason.MovementOriginInvalidated),
                "同一计划同时属于两类时必须取优先级更高的原因");
        }

        /// <summary>
        /// 缺陷指纹：若失效判定按"单位被换位 ⇒ 终止它的全部计划"，Attack/Guard/Block 会被误杀；
        /// 冻结规则是"不持有固定空间依赖的未来计划保留"（:211）。
        /// </summary>
        [Test]
        public void NonSpatialAttackGuardAndBlockPlansSurviveOwnerDisplacement()
        {
            Rig rig = NewRig();
            Place(rig.Grid, 1L, 0, 0);
            AddPlan(rig.Schedule, 41L, 1L, ActionType.Attack);
            AddPlan(rig.Schedule, 42L, 1L, ActionType.Guard);
            AddPlan(rig.Schedule, 43L, 1L, ActionType.Block);
            AddPlan(rig.Schedule, 44L, 1L, ActionType.Move);

            ForcedDisplacementBatch batch = rig.Solve(Request(1L, GridDirection.East, 2));
            Assert.That(batch.Resolutions.Single().InvalidatedPlanIds.Select(id => id.Value),
                Is.EqualTo(new[] { 44L }), "只有依赖旧起点的 Move 进入候选");

            IReadOnlyList<ActionPlanId> terminated = rig.Commit(batch);
            Assert.That(terminated.Select(id => id.Value), Is.EqualTo(new[] { 44L }));
            Assert.That(rig.Schedule.Registry.Find(P(41L)).IsTerminal, Is.False);
            Assert.That(rig.Schedule.Registry.Find(P(42L)).IsTerminal, Is.False);
            Assert.That(rig.Schedule.Registry.Find(P(43L)).IsTerminal, Is.False);
        }

        // ================= 356：Dodge 目的格预留 =================

        /// <summary>
        /// 缺陷指纹：Dodge 目的格预留<strong>不</strong>存在 <c>LogicGrid</c> 的预留表里，
        /// 若求解器只遍历网格预留，Dodge 计划既不会被抢占、其目的格预留也不会释放；
        /// 反过来，若只按"临时路径穿过"判定，只是被穿过的目的格预留会被错误终止（:207、:216）。
        /// </summary>
        [Test]
        public void ConflictingDodgeDestinationReservationIsPreemptedButPassThroughIsNot()
        {
            var grid = new LogicGrid(Wide);
            Place(grid, 1L, 0, 0);            // 单位 1：East 3 ⇒ 穿过 (2,0)、(4,0)，最终 (6,0)
            Place(grid, 5L, 8, 0);            // D1 防御者：West 到 (6,0) —— 与最终格冲突
            Place(grid, 6L, 3, 1);            // D2 防御者：SouthWest 到 (2,0) —— 只是被穿过

            var schedule = new ActionScheduleAuthority();
            var coordinator = new ActionPlanTerminalCoordinator(schedule);
            var movement = new LogicGridMovementAuthority(
                grid, schedule, BattleRules.FrozenV1.PathCostRules, BattleRules.FrozenV1.PathSearchRules);

            IReadOnlyList<DirectionalTriangleSet> pattern = FullPattern();
            var dodge = new DodgeRelocationAuthority(
                grid, movement, schedule, coordinator,
                _ => new DodgeDestinationRules(4, pattern));
            movement.DodgeDestinationReservations = dodge;

            AddPlan(schedule, 51L, 5L, ActionType.Dodge);
            AddPlan(schedule, 52L, 6L, ActionType.Dodge);

            string r1 = dodge.ReserveDestination(O(1L), U(5L), new ActionSpecId("action.t08.dodge"),
                new GridPoint(6, 0), triggerTick: 10L, commandSequence: 1L, tick: 0L);
            string r2 = dodge.ReserveDestination(O(2L), U(6L), new ActionSpecId("action.t08.dodge"),
                new GridPoint(2, 0), triggerTick: 10L, commandSequence: 2L, tick: 0L);
            Assert.That(r1, Is.Null, "夹具前提：D1 的目的格预留必须成功 code=" + r1);
            Assert.That(r2, Is.Null, "夹具前提：D2 的目的格预留必须成功 code=" + r2);
            dodge.BindPlan(O(1L), P(51L));
            dodge.BindPlan(O(2L), P(52L));

            var solver = new ForcedDisplacementSolver(grid, schedule, dodge, null, null);
            ForcedDisplacementBatch batch = solver.ResolveAll(
                new[] { Request(1L, GridDirection.East, 3) }, Snapshots(grid), Wide, 0L);
            ForcedDisplacementResolution resolution = batch.Resolutions.Single();

            Assert.That(resolution.To, Is.EqualTo(new GridPoint(6, 0)), "夹具前提：最终格 (6,0)");
            Assert.That(resolution.InvalidatedPlanIds.Select(id => id.Value), Is.EqualTo(new[] { 51L }),
                "只有与最终 footprint 相交的 Dodge 目的格预留才被抢占");

            var committer = new ForcedDisplacementCommitter(schedule, coordinator);
            IReadOnlyList<ActionPlanId> terminated = committer.TerminateInvalidatedMovementPlansOrdered(batch, 0L);
            Assert.That(terminated.Select(id => id.Value), Is.EqualTo(new[] { 51L }));
            Assert.That(schedule.Registry.Find(P(51L)).TerminationReason,
                Is.EqualTo(ActionTerminationReason.ReservationPreemptedByForcedDisplacement));
            Assert.That(schedule.Registry.Find(P(52L)).IsTerminal, Is.False,
                "只在临时路径上被穿过的目的格预留不得终止其计划");
        }

        // ================= 359 / 360：批次形状与事件载荷边界 =================

        /// <summary>
        /// 缺陷指纹：若批次只有一个集合（现状缺陷 R7），零步结果会被当成"搬迁到原地"送进
        /// <c>ApplyBatchRelocation</c>，并且 <c>IsEmpty</c> 会让整批（含零步结果）被提前返回而
        /// <strong>不发事件</strong>。本用例钉住"零步结果存在、位移集合为空、批次非空"三点。
        /// </summary>
        [Test]
        public void ZeroAppliedStepsStillEmitsForcedDisplacementResolutionEvent()
        {
            Rig rig = NewRig();
            Place(rig.Grid, 1L, 0, 0);
            Place(rig.Grid, 2L, 2, 0);                   // 静止阻挡 ⇒ 单位 1 零步

            ForcedDisplacementBatch batch = rig.Solve(Request(1L, GridDirection.East, 1));
            ForcedDisplacementResolution resolution = batch.Resolutions.Single();

            Assert.That(resolution.AppliedSteps, Is.EqualTo(0));
            Assert.That(resolution.StopReason, Is.EqualTo(ForcedDisplacementStopReason.OccupiedUnit));
            Assert.That(resolution.From, Is.EqualTo(resolution.To));
            Assert.That(batch.IsEmpty, Is.False,
                "零步结果仍是一条结果 ⇒ 阶段 13 必须继续走事件发射（IsEmpty 不得按 Relocations 判定）");
            Assert.That(batch.Relocations, Is.Empty,
                "ApplyBatchRelocation 只接收 From != To 的条目");

            // 事件载荷的字段来源就是这条结果 + 本 Tick 请求（Direction/Momentum/ConflictGroupKey 在请求侧）。
            Assert.That(resolution.RequestedSteps, Is.EqualTo(1));
            Assert.That(resolution.StopReason, Is.EqualTo(ForcedDisplacementStopReason.OccupiedUnit));
            Assert.That(resolution.InvalidatedPlanIds, Is.Empty, "没有换位 ⇒ 不得失效任何计划");
        }

        /// <summary>
        /// 缺陷指纹：若失效计划集合不去重、或按容器枚举顺序输出，事件会带重复/不稳定 ID。
        /// 冻结规则：按 <see cref="ActionPlanId"/> 去重升序。
        /// </summary>
        [Test]
        public void ForcedDisplacementEventContainsCanonicalStopReasonAndInvalidatedPlans()
        {
            Rig rig = NewRig();
            Place(rig.Grid, 1L, 0, 0);
            AddPlan(rig.Schedule, 63L, 1L, ActionType.Move);
            AddPlan(rig.Schedule, 61L, 1L, ActionType.Move);
            AddPlan(rig.Schedule, 62L, 9L, ActionType.Move);

            // 62 同时持有冲突预留 ⇒ 它与 61/63 一起进入候选；61/63 来自"旧起点"类。
            Reserve(rig.Grid, 62L, 9L, 4, 0, 0L, 10L);

            ForcedDisplacementBatch batch = rig.Solve(Request(1L, GridDirection.East, 2));
            ForcedDisplacementResolution resolution = batch.Resolutions.Single();

            Assert.That(resolution.StopReason, Is.EqualTo(ForcedDisplacementStopReason.Completed));
            Assert.That(resolution.InvalidatedPlanIds.Select(id => id.Value),
                Is.EqualTo(new[] { 61L, 62L, 63L }), "失效计划必须去重并升序");

            IReadOnlyList<ActionPlanId> terminated = rig.Commit(batch);
            Assert.That(terminated.Select(id => id.Value), Is.EqualTo(new[] { 61L, 62L, 63L }));
            Assert.That(rig.Schedule.Registry.Find(P(61L)).TerminationReason,
                Is.EqualTo(ActionTerminationReason.MovementOriginInvalidated));
            Assert.That(rig.Schedule.Registry.Find(P(62L)).TerminationReason,
                Is.EqualTo(ActionTerminationReason.ReservationPreemptedByForcedDisplacement));
        }

        // ================= 批量换位的原子性 =================

        /// <summary>
        /// 缺陷指纹：若 <c>ApplyBatchRelocation</c> 收到含零步条目的批次，网格第 6 步预检会把
        /// "零步单位自己占着自己的目标格"当成他人预留而失败；若批次里混入重复目标格，
        /// 网格会以 <c>BATCH_TARGET_OVERLAP</c> 失败。两者都必须由<strong>构造期</strong>排除。
        /// </summary>
        [Test]
        public void BatchRelocationsAreExactlyTheNonZeroEntriesAndCommitAtomically()
        {
            Rig rig = NewRig(withMovementParticipant: true);
            Place(rig.Grid, 1L, 0, 0);
            Place(rig.Grid, 2L, 2, 0);
            AddPlan(rig.Schedule, 71L, 1L, ActionType.Move);
            Reserve(rig.Grid, 71L, 1L, 4, 0, 0L, 10L);

            ForcedDisplacementBatch batch = rig.Solve(
                Request(1L, GridDirection.East, 1),
                Request(2L, GridDirection.East, 1));
            Assert.That(batch.Resolutions.Count, Is.EqualTo(2));
            Assert.That(batch.Relocations.Count, Is.EqualTo(2));

            IReadOnlyList<ActionPlanId> terminated = rig.Commit(batch);
            Assert.That(terminated.Select(id => id.Value), Is.EqualTo(new[] { 71L }));

            // 场景 B：其中一个单位零步 ⇒ 位移集合必须只剩另一个。
            var grid2 = new LogicGrid(Wide);
            Place(grid2, 1L, 0, 0);
            Place(grid2, 2L, 2, 0);
            Place(grid2, 3L, 10, 0);
            var schedule2 = new ActionScheduleAuthority();
            var solver2 = new ForcedDisplacementSolver(grid2, schedule2, null, null, null);
            ForcedDisplacementBatch mixed = solver2.ResolveAll(
                new[]
                {
                    Request(1L, GridDirection.East, 1),      // 被 2 号挡住 ⇒ 零步
                    Request(3L, GridDirection.East, 1)       // 正常移动
                },
                Snapshots(grid2), Wide, 0L);

            Assert.That(mixed.Resolutions.Count, Is.EqualTo(2), "结果集合含零步");
            Assert.That(mixed.Relocations.Count, Is.EqualTo(1), "位移集合只含 From != To");
            Assert.That(mixed.Relocations[0].UnitId.Value, Is.EqualTo(3L));

            // 真正提交到唯一空间权威：网格必须原子接受，且零步单位位置不变。
            string error = grid2.ApplyBatchRelocation(mixed.Relocations);
            Assert.That(error, Is.Null, "网格批量换位必须接受该批次 code=" + error);
            grid2.TryGetAnchor(U(1L), out GridPoint anchorOne);
            grid2.TryGetAnchor(U(3L), out GridPoint anchorThree);
            Assert.That(anchorOne, Is.EqualTo(new GridPoint(0, 0)));
            Assert.That(anchorThree, Is.EqualTo(new GridPoint(12, 0)));
        }

        // ================= 22：哈希参与 + R4 装配口径 =================

        /// <summary>
        /// 缺陷指纹：若方向表 / footprint 规则 / 依赖图判定 / 抢占规则 / 阶段顺序 / 终态原因优先级
        /// 没有进入 <c>BattleDefinitionHash</c>，改掉其中任何一条规则都不会改变定义哈希
        /// （任务包「必须产出」22 的字面要求）。
        /// </summary>
        [Test]
        public void ForcedDisplacementRulesParticipateInBattleDefinitionHash()
        {
            BattleRules baseline = BattleRules.FrozenV1;
            string digest = BattleDefinitionHash.OfBattleRules(baseline);

            Assert.That(baseline.EffectiveForcedDisplacementRules, Is.Not.Null);
            Assert.That(BattleDefinitionHash.OfBattleRules(baseline with
            {
                ForcedDisplacementRules = ForcedDisplacementRules.FrozenV1 with { FootprintRuleVersion = 2 }
            }), Is.Not.EqualTo(digest), "footprint 规则版本必须参与哈希");
            Assert.That(BattleDefinitionHash.OfBattleRules(baseline with
            {
                ForcedDisplacementRules = ForcedDisplacementRules.FrozenV1 with { DependencyGraphRuleVersion = 2 }
            }), Is.Not.EqualTo(digest), "依赖图判定版本必须参与哈希");
            Assert.That(BattleDefinitionHash.OfBattleRules(baseline with
            {
                ForcedDisplacementRules = ForcedDisplacementRules.FrozenV1 with { PreemptionRuleVersion = 2 }
            }), Is.Not.EqualTo(digest), "Reservation 抢占规则版本必须参与哈希");
            Assert.That(BattleDefinitionHash.OfBattleRules(baseline with
            {
                ForcedDisplacementRules = ForcedDisplacementRules.FrozenV1 with { PhaseOrderVersion = 2 }
            }), Is.Not.EqualTo(digest), "阶段顺序版本必须参与哈希");

            var writer = new CanonicalHashWriter();
            baseline.WriteHashComponents(writer);
            string text = writer.ToCanonicalText();
            Assert.That(text, Does.Contain("forced_displacement.footprint_rule_version=1"));
            Assert.That(text, Does.Contain("forced_displacement.dependency_graph_rule_version=1"));
            Assert.That(text, Does.Contain("forced_displacement.preemption_rule_version=1"));
            Assert.That(text, Does.Contain("forced_displacement.phase_order_version=1"));
            Assert.That(text, Does.Contain("forced_displacement.direction_table_version=" +
                                           GridNeighborTable.OrderVersion));
            Assert.That(text, Does.Contain("forced_displacement.direction.EastNorth=1:3,1"),
                "方向表必须逐方向写入规范偏移");
            Assert.That(text, Does.Contain("forced_displacement.termination_reason_priority.0=" +
                                           (int)ActionTerminationReason.MovementOriginInvalidated + ":" +
                                           ActionTerminationReasons.MovementOriginInvalidated),
                "终态原因优先级必须按位次写入");
            Assert.That(text, Does.Contain("forced_displacement.termination_reason_priority.1=" +
                                           (int)ActionTerminationReason.ReservationPreemptedByForcedDisplacement),
                "终态原因优先级必须按位次写入");

            // 一致性：相同输入必须得到相同摘要。
            Assert.That(BattleDefinitionHash.OfBattleRules(BattleRules.FrozenV1), Is.EqualTo(digest));
        }

        /// <summary>
        /// 缺陷指纹：若装配层把 <c>null</c> 兜底成"空实现"，默认装配就会退化成"机制不工作"；
        /// 任务 08 的 R4 要求默认绑定<strong>本场真实现</strong>。
        /// </summary>
        [Test]
        public void AssemblyLeavesForcedDisplacementPortsOpenForTheRealImplementation()
        {
            var assembly = BattleSimulationAssembly.Standard();
            Assert.That(assembly.DisplacementSolver, Is.Null,
                "求解器槽位必须保持 null ⇒ BattleSimulation 才会绑定本场真实现");
            Assert.That(assembly.DisplacementCommitter, Is.Null,
                "提交器槽位必须保持 null ⇒ BattleSimulation 才会绑定本场真实现");

            // 显式注入仍然优先（负控制 / 任务 03、04 的脚本夹具）。
            var negative = new ForcedDisplacementSolver(new LogicGrid(Wide), null, null, null, null);
            Assert.That(new BattleSimulationAssembly(displacementSolver: negative).DisplacementSolver,
                Is.SameAs(negative));
        }

        // ================= 多格 / Pattern 夹具 =================

        private static IReadOnlyList<DirectionalTriangleSet> TwoCellDirections()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });

        /// <summary>覆盖全部 12 个方向的规范 Pattern（Dodge 目的格预留预检需要）。</summary>
        private static IReadOnlyList<DirectionalTriangleSet> FullPattern()
            => DirectionalGeometry.ExpandFromBases(
                new[] { new TrianglePoint(3, 0, 1) },
                new[] { new TrianglePoint(4, 1, -1) });
    }
}
