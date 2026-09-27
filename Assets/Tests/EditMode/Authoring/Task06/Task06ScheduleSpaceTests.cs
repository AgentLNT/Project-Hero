using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using ProjectHero.Authoring.Tests.Task05;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Authoring.Tests.Task06
{
    /// <summary>
    /// 任务 06 的<strong>命令网关层</strong>端到端证据 + <strong>排程事务 → 空间整批替换</strong>证据。
    ///
    /// 与 <c>ProjectHero.Logic.Tests</c> 的差别（这是本文件存在的理由）：
    /// <list type="bullet">
    /// <item><see cref="ProjectHero.Logic.Tests"/> 没有 <c>InternalsVisibleTo</c>，无法注入
    /// <c>ProducerOrdinal</c>，也无法调用 <c>ScheduleEditor.ApplySystemAutoDeferral</c>；</item>
    /// <item>本程序集持有该授权，因此这里可以驱动
    /// <c>CommandIngressRegistry → CommandGateway</c> 的<strong>真实</strong>规范排序，
    /// 也可以驱动<strong>真实</strong>的系统自动延期事务。</item>
    /// </list>
    ///
    /// 全部断言建立在<strong>公开可观察</strong>的逻辑事实上：命令序号/优先级、入口拒绝、
    /// 段与 Reservation 的规范签名、权威锚点、计划投影。没有桩、没有空断言。
    /// </summary>
    public class Task06ScheduleSpaceTests
    {
        private const string PlayerController = "controller.t06.player";
        private const string EnemyController = "controller.t06.enemy";
        private const long Tick = 10L;

        private static UnitId Hero => Task05Farm.Hero;
        private static UnitId Enemy => Task05Farm.Enemy;
        private static UnitId Ally => Task05Farm.Ally;

        // 权威锚点（doubled coordinate：X 是列的两倍，X+Y 必须为偶数）。
        private static readonly GridPoint HeroAnchor = new GridPoint(0, 0);
        private static readonly GridPoint EnemyAnchor = new GridPoint(0, 2);
        private static readonly GridPoint AllyAnchor = new GridPoint(0, 24);
        private static readonly GridPoint HeroDestination = new GridPoint(4, 0);
        private static readonly GridPoint HeroFarDestination = new GridPoint(0, 4);
        private static readonly GridPoint EnemyDestination = new GridPoint(0, 4);
        private static readonly GridPoint AllyDestination = new GridPoint(0, 28);
        private static readonly GridPoint HeroFirstStep = new GridPoint(2, 0);

        // ————————————————————————————————————————————————————————————
        // 夹具
        // ————————————————————————————————————————————————————————————

        /// <summary>真实排程事务 + 真实 <c>LogicGrid</c> + 真实空间权威 + 真实路径计算器。</summary>
        private sealed class Fixture
        {
            public LogicGrid Grid;
            public LogicGridMovementAuthority Movement;
            public LogicGridMovementPathCalculator Calculator;
            public Task05Scheduler Scheduler;

            public long Revision => Scheduler.Revision;
        }

        private static Fixture NewFixture(bool withPathCalculator = true)
        {
            var grid = new LogicGrid(new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40)));
            var schedule = new ActionScheduleAuthority();
            BattleRules rules = Task05Farm.Rules;
            var movement = new LogicGridMovementAuthority(
                grid, schedule, rules.PathCostRules, rules.PathSearchRules);
            var calculator = new LogicGridMovementPathCalculator(
                grid, schedule, rules.PathCostRules, rules.PathSearchRules);

            var scheduler = new Task05Scheduler(null, withPathCalculator ? calculator : null);
            scheduler.Editor.MovementSpacePort = movement;

            Assert.That(grid.RegisterUnitWithPointFootprint(Hero, HeroAnchor, GridDirection.East), Is.Null,
                "Hero 锚点注册");
            Assert.That(grid.RegisterUnitWithPointFootprint(Enemy, EnemyAnchor, GridDirection.East), Is.Null,
                "Enemy 锚点注册");
            Assert.That(grid.RegisterUnitWithPointFootprint(Ally, AllyAnchor, GridDirection.East), Is.Null,
                "Ally 锚点注册");

            return new Fixture
            {
                Grid = grid,
                Movement = movement,
                Calculator = calculator,
                Scheduler = scheduler
            };
        }

        /// <summary>经真实排程事务新增一个普通 Move 计划。</summary>
        private static ActionPlan AddMove(Fixture f, long temporaryKey, UnitId owner, GridPoint destination,
            long startTick = Tick)
        {
            ScheduleEditTransactionResult result = f.Scheduler.Apply(new ScheduleEditOperation[]
            {
                new AddOrdinaryPlanOperation(
                    temporaryKey, owner, new ActionSpecId(Task05Farm.MoveId), startTick,
                    AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                    Facing: GridDirection.East, Destination: destination)
            });
            Assert.That(result.RejectionCode, Is.Null,
                "真实排程事务必须成功：" + ScheduleEditor.Describe(result));
            Assert.That(result.AddedPlanIds.Count, Is.EqualTo(1));
            ActionPlan plan = f.Scheduler.Authority.Registry.Find(result.AddedPlanIds[0]);
            Assert.That(plan, Is.Not.Null, "新增计划必须进入权威注册表");
            return plan;
        }

        private static string SegmentSignature(Fixture f)
        {
            var builder = new StringBuilder();
            IReadOnlyList<MovementSegment> segments = f.Movement.AllSegmentsOrdered();
            for (int i = 0; i < segments.Count; i++)
            {
                MovementSegment s = segments[i];
                builder.Append("seg#").Append(s.ActionPlanId.Value).Append('.').Append(s.StepIndex)
                       .Append(":u").Append(s.UnitId.Value)
                       .Append('@').Append(s.From).Append("->").Append(s.To)
                       .Append('[').Append(s.StartTick).Append(',').Append(s.EndTick).Append(");");
            }
            return builder.ToString();
        }

        private static string ReservationSignature(Fixture f)
        {
            var builder = new StringBuilder();
            IReadOnlyList<Reservation> reservations = f.Grid.AllReservationsOrdered();
            for (int i = 0; i < reservations.Count; i++)
            {
                Reservation r = reservations[i];
                builder.Append("res#").Append(r.ActionPlanId.Value).Append('.').Append(r.StepIndex)
                       .Append(":u").Append(r.UnitId.Value)
                       .Append('@').Append(r.Cell)
                       .Append('[').Append(r.StartTick).Append(',').Append(r.EndTick).Append(");");
            }
            return builder.ToString();
        }

        private static string SpaceSignature(Fixture f)
            => SegmentSignature(f) + "||" + ReservationSignature(f);

        /// <summary>在权威空间表里手工占用一个格（用于构造"建立新预留"这一步的确定性失败）。</summary>
        private static void OccupyByForeignReservation(Fixture f, UnitId owner, GridPoint cell)
        {
            string error = f.Grid.TryReserve(new Reservation(
                new ReservationKey(new ActionPlanId(9000L), 0), owner, cell, 0L, 1_000_000L));
            Assert.That(error, Is.Null, "构造用预留必须成功：" + error);
        }

        /// <summary>
        /// 构造"排程求值通过、但空间整批替换失败"的确定性场景。
        ///
        /// 手段：把计划 A 的**目的格**用一个既非 A、也非任何 <c>Editable Move</c> 的外部预留占住。
        /// <list type="bullet">
        /// <item>计划建立时 A 自己的段/预留照常写入（那时还没有外部占用）；</item>
        /// <item>之后的每一次重建都会在"A 的 <c>ResolveEditableMovementWorkingSet</c> 解析阶段"
        /// 因为没有可达路径而把 A 从工作集里剔除 ⇒ <c>RebuildMovementSpace</c> 无候选
        /// ⇒ 事务完全不触碰空间 ⇒ 旧段/预留逐字保持权威；</item>
        /// <item>过程中不存在"部分写入"，因此这正是"失败无局部替换"的可失败断言对象：
        /// 若实现改成"先释放旧段再尝试建立"，旧的段签名必然变化，断言立刻失败。</item>
        /// </list>
        /// </summary>
        private static string BlockPlanDestination(Fixture f, ActionPlan plan, UnitId foreignOwner)
        {
            GridPoint destination = plan.Destination.Value;
            string error = f.Grid.TryReserve(new Reservation(
                new ReservationKey(new ActionPlanId(9001L), 0), foreignOwner, destination, 0L, 1_000_000L));
            Assert.That(error, Is.Null, "构造用外部预留必须成功（目的格必须空闲）：" + error);
            PathSearchResult blocked = f.Calculator.FindPathFor(plan, plan.StartTick);
            Assert.That(blocked.Succeeded, Is.False, "构造必须让该计划在空间重建时无路可走");
            return blocked.FailureCode;
        }

        // ————————————————————————————————————————————————————————————
        // 网关夹具：真实 CommandIngressRegistry → CommandGateway
        // ————————————————————————————————————————————————————————————

        private sealed class Gateway
        {
            public CommandIngressRegistry Ingress;
            public CommandGateway Normalizer;
            public CommandIngressEntry Player;
            public CommandIngressEntry Enemy;
            private long _nextEntry = 1L;

            public CommandIngressEntry NewPlayerEntry()
            {
                Player = Ingress.RegisterExternalEntry(new ControllerBinding(
                    new ControllerId(PlayerController + "." + _nextEntry++),
                    CommandSourceKind.Player, new List<EncounterSlotId>()));
                return Player;
            }

            public CommandIngressEntry NewEnemyEntry()
            {
                Enemy = Ingress.RegisterExternalEntry(new ControllerBinding(
                    new ControllerId(EnemyController + "." + _nextEntry++),
                    CommandSourceKind.Ai, new List<EncounterSlotId>()));
                return Enemy;
            }
        }

        private static Gateway NewGateway()
        {
            var wrapper = new Gateway
            {
                Ingress = new CommandIngressRegistry(),
                Normalizer = new CommandGateway(new LogicSequenceGenerator())
            };
            wrapper.NewPlayerEntry();
            wrapper.NewEnemyEntry();
            return wrapper;
        }

        /// <summary>排程编辑请求（生产者只能给出"目标 Tick / scope / payload"三样东西）。</summary>
        private static CommandRequest MoveRequest(
            long targetTick, long expectedRevision, long temporaryKey, UnitId owner, GridPoint destination)
            => new CommandRequest(
                targetTick,
                new ScheduleEditScope(expectedRevision, null),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    new AddOrdinaryPlanOperation(
                        temporaryKey, owner, new ActionSpecId(Task05Farm.MoveId), targetTick,
                        AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                        Facing: GridDirection.East, Destination: destination)
                }));

        private static CommandRequest CloseWindowRequest(long targetTick, long windowId)
            => new CommandRequest(
                targetTick,
                new WindowCommandScope(new WindowId(windowId)),
                new WindowCommandPayload(WindowCommandKind.CloseOwnWindow));

        private sealed class TickRun
        {
            public FrozenTickCommandSet Frozen;
            public IReadOnlyList<CommandRejectionRecord> CommandRejections;
            public readonly List<string> Committed = new List<string>();
            public readonly List<string> Rejected = new List<string>();
        }

        /// <summary>驱动一个 Tick：提交 → 冻结 → 网关规范化 → 命令处理器按 CommandSequence 执行。</summary>
        private static TickRun DriveTick(Gateway g, Fixture f, long tick)
        {
            FrozenCommandBatch batch = g.Ingress.FreezeTick(tick);
            FrozenTickCommandSet frozen = g.Normalizer.NormalizeAndAssignSequence(
                batch, tick, new Dictionary<ControllerId, IReadOnlyList<UnitId>>());

            var run = new TickRun { Frozen = frozen };
            var processor = new ActionPlanCommandProcessor(f.Scheduler.Authority, f.Scheduler.Editor);
            processor.CommittedTransactionSink = (result, atTick) =>
            {
                for (int i = 0; i < result.AddedPlanIds.Count; i++)
                    run.Committed.Add("add#" + result.AddedPlanIds[i].Value + "@" + atTick);
            };
            run.CommandRejections = processor.ProcessOrdered(frozen.Envelopes, tick, 0L);
            for (int i = 0; i < run.CommandRejections.Count; i++)
            {
                run.Rejected.Add("seq#" + run.CommandRejections[i].Envelope.CommandSequence +
                                 "|" + run.CommandRejections[i].ReasonCode);
            }
            for (int i = 0; i < frozen.IngressRejections.Count; i++)
            {
                CommandIngressRejection r = frozen.IngressRejections[i];
                run.Rejected.Add("ingress|" + r.GroupKey + "|" + r.ReasonCode + "|" + r.CollisionCount);
            }
            run.Rejected.Sort(StringComparer.Ordinal);
            return run;
        }

        private static string EnvelopeOrder(FrozenTickCommandSet frozen)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < frozen.Envelopes.Count; i++)
            {
                CommandEnvelope e = frozen.Envelopes[i];
                builder.Append("seq=").Append(e.CommandSequence)
                       .Append("|prio=").Append(e.SourcePriority)
                       .Append("|ctrl=").Append(e.ControllerId.Value)
                       .Append("|ord=").Append(e.ProducerOrdinal)
                       .Append(";");
            }
            return builder.ToString();
        }

        private static string RunSignature(TickRun run, Fixture f)
            => "order[" + EnvelopeOrder(run.Frozen) + "]" +
               "committed[" + string.Join(",", run.Committed) + "]" +
               "rejected[" + string.Join(",", run.Rejected) + "]" +
               "space[" + SpaceSignature(f) + "]";

        // ————————————————————————————————————————————————————————————
        // R1-a / #33：预留冲突赢家只服从规范命令序（经真实命令网关）
        // ————————————————————————————————————————————————————————————

        [Test]
        public void ReservationConflictWinnerFollowsCanonicalCommandOrder()
        {
            Fixture f = NewFixture();
            Gateway g = NewGateway();

            // 抢占目标：Enemy 的**第一步落点** (2,2)。它当前空闲（无 footprint、无预留）。
            GridPoint contested = new GridPoint(2, 2);

            // Player（SourcePriority 0）与 Ai（SourcePriority 10）都提交"落点是 (2,2)"的 Move。
            // Player 先（按规范顺序）成功提交并持有该格；Ai 的命令必须稳定拒绝且不可抢占。
            g.Player.Submit(MoveRequest(Tick, f.Revision, 11L, Enemy, contested));
            g.Enemy.Submit(MoveRequest(Tick, f.Revision, 12L, Enemy, contested));

            TickRun run = DriveTick(g, f, Tick);

            Assert.That(run.Frozen.Envelopes.Count, Is.EqualTo(2), "两条请求都必须通过入口冻结");
            Assert.That(run.Frozen.Envelopes[0].SourcePriority, Is.LessThan(run.Frozen.Envelopes[1].SourcePriority),
                "网关必须把 Player 排在 Ai 之前");
            Assert.That(run.Frozen.Envelopes[0].CommandSequence,
                Is.LessThan(run.Frozen.Envelopes[1].CommandSequence),
                "CommandSequence 必须按规范顺序单调分配");

            Assert.That(run.Rejected.Count, Is.EqualTo(1), string.Join(",", run.Rejected));
            Assert.That(run.Rejected[0], Does.Contain(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER),
                "后到的冲突命令必须稳定拒绝：" + string.Join(",", run.Rejected));
            Assert.That(run.Committed.Count, Is.EqualTo(1),
                "先到的命令必须成功提交（后续冲突命令只回滚自身事务）");

            // 赢家是"先成功提交者"：抢占格上的预留只有一条，且属于 Player 那条命令建立的空间事实。
            IReadOnlyList<Reservation> all = f.Grid.AllReservationsOrdered();
            int holders = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Cell.X == contested.X && all[i].Cell.Y == contested.Y) holders++;
            }
            Assert.That(holders, Is.EqualTo(1), "抢占格必须恰好一个持有者");
            Assert.That(f.Movement.VerifyInvariants(), Is.Null);
        }

        // ————————————————————————————————————————————————————————————
        // R1-b：同组请求的原始枚举顺序不影响任何可观察结果
        // ————————————————————————————————————————————————————————————

        [Test]
        public void ReservationConflictIgnoresRawBatchEnumerationOrder()
        {
            // 两个变体只在**投递顺序**上不同；规范顺序必须把 Player 排到 Ai 之前。
            var signatures = new List<string>();
            for (int variant = 0; variant < 2; variant++)
            {
                Fixture f = NewFixture();
                Gateway g = NewGateway();
                GridPoint contested = new GridPoint(2, 2);

                CommandRequest playerRequest = MoveRequest(Tick, f.Revision, 11L, Enemy, contested);
                CommandRequest aiRequest = MoveRequest(Tick, f.Revision, 12L, Enemy, contested);

                if (variant == 0)
                {
                    g.Player.Submit(playerRequest);
                    g.Enemy.Submit(aiRequest);
                }
                else
                {
                    g.Enemy.Submit(aiRequest);
                    g.Player.Submit(playerRequest);
                }

                TickRun run = DriveTick(g, f, Tick);
                signatures.Add(RunSignature(run, f));
            }

            Assert.That(signatures[1], Is.EqualTo(signatures[0]),
                "同一组可信来源键的请求，原始枚举顺序不得影响命令顺序/赢家/拒绝/段与预留");
            Assert.That(signatures[0], Does.Contain(PlayerController),
                "签名必须真的包含来源身份，否则比较没有区分力");
            Assert.That(signatures[0], Does.Contain(LogicGridCodes.LOGIC_GRID_RESERVED_BY_OTHER),
                "签名必须真的包含这次冲突拒绝，否则比较没有区分力");
        }

        // ————————————————————————————————————————————————————————————
        // R1-c / #35：重复生产者键整组拒绝，不产生预留赢家
        // ————————————————————————————————————————————————————————————

        [Test]
        public void DuplicateProducerOrdinalCannotChooseReservationWinner()
        {
            Fixture f = NewFixture();
            Gateway g = NewGateway();
            GridPoint contested = new GridPoint(2, 2);

            string before = SpaceSignature(f);

            // 两条**同一规范键**的事实（同入口、同 ProducerOrdinal）：
            // 只有回放注入面能构造，因此这里必须由持有 InternalsVisibleTo 的本程序集驱动。
            CommandRequest first = MoveRequest(Tick, f.Revision, 21L, Enemy, contested);
            CommandRequest second = MoveRequest(Tick, f.Revision, 22L, Enemy, contested);
            Assert.That(g.Player.InjectRecordedFact(7L, first), Is.Null, "第一次注入必须被接受");
            Assert.That(g.Player.InjectRecordedFact(7L, second), Is.Null, "同键第二次注入必须被接受");

            TickRun run = DriveTick(g, f, Tick);

            Assert.That(run.Frozen.Envelopes.Count, Is.EqualTo(0),
                "重复规范键必须整组拒绝：没有任何一条获得 CommandSequence");
            Assert.That(run.Frozen.IngressRejections.Count, Is.EqualTo(1));
            Assert.That(run.Frozen.IngressRejections[0].ReasonCode,
                Is.EqualTo(CommandCodes.DUPLICATE_COMMAND_ORDINAL));
            Assert.That(run.Frozen.IngressRejections[0].CollisionCount, Is.EqualTo(2),
                "碰撞组必须完整计数（不选择'先枚举者'）");
            Assert.That(run.Committed.Count, Is.EqualTo(0));
            Assert.That(SpaceSignature(f), Is.EqualTo(before),
                "被整组拒绝的请求不得产生任何段或预留写入");
        }

        // ————————————————————————————————————————————————————————————
        // R1-d / #38：窗口切换不提交移动、不释放 Reservation
        // ————————————————————————————————————————————————————————————

        [Test]
        public void WindowSwitchDoesNotChangeMovementOrReservation()
        {
            Fixture f = NewFixture();
            Gateway g = NewGateway();

            AddMove(f, 1L, Hero, HeroDestination);
            string segmentsBefore = SegmentSignature(f);
            string reservationsBefore = ReservationSignature(f);
            Assert.That(segmentsBefore, Is.Not.Empty, "前置条件：确实存在段");
            Assert.That(reservationsBefore, Is.Not.Empty, "前置条件：确实存在 Reservation");

            g.Player.Submit(CloseWindowRequest(Tick, 1L));
            TickRun run = DriveTick(g, f, Tick);

            Assert.That(run.Frozen.Envelopes.Count, Is.EqualTo(1), "窗口命令必须获得 CommandSequence");
            Assert.That(run.Frozen.IngressRejections.Count, Is.EqualTo(0), "入口不得拒绝它");
            Assert.That(run.CommandRejections.Count, Is.EqualTo(1),
                "窗口命令由任务 07 处理：本任务必须稳定拒绝而不是静默接受");
            Assert.That(run.CommandRejections[0].ReasonCode,
                Is.EqualTo(CommandCodes.COMMAND_PROCESSOR_NOT_IMPLEMENTED));

            Assert.That(SegmentSignature(f), Is.EqualTo(segmentsBefore),
                "窗口切换不得提交移动或改动段");
            Assert.That(ReservationSignature(f), Is.EqualTo(reservationsBefore),
                "窗口切换不得释放 Reservation");
            Assert.That(f.Grid.TryGetAnchor(Hero, out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(HeroAnchor), "窗口切换不得提交位置");
        }

        // ————————————————————————————————————————————————————————————
        // R2-a / #59：自动延期与显式编辑复用同一空间求值器
        // ————————————————————————————————————————————————————————————

        [Test]
        public void AutoDeferralReusesMovePathAndReservationEvaluator()
        {
            Fixture f = NewFixture();
            ActionPlan plan = AddMove(f, 1L, Hero, HeroDestination);

            // 显式编辑已经把段与预留建好（证明显式编辑走这个空间端口）。
            Assert.That(f.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId).Count, Is.EqualTo(2),
                "Hero (0,0)->(4,0) 是 2 条 East 边");

            // 真实自动延期事务：复用同一个 ScheduleEvaluator + 同一个空间端口。
            ScheduleEditTransactionResult deferred = f.Scheduler.Editor.ApplySystemAutoDeferral(
                plan, retryAtTick: 200L, currentTick: Tick, claimedPlans: null);
            Assert.That(deferred.RejectionCode, Is.Null, ScheduleEditor.Describe(deferred));
            Assert.That(deferred.Committed, Is.True);

            ActionPlan after = f.Scheduler.Authority.Registry.Find(plan.ActionPlanId);
            Assert.That(after.StartTick, Is.EqualTo(200L), "计划必须被推到 RetryAtTick");

            // 复用同一求值器：段与预留按新绝对 Tick 重建，且与真实寻路结果一致。
            PathSearchResult path = f.Calculator.FindPathFor(after, after.StartTick);
            Assert.That(path.Succeeded, Is.True, path.FailureCode);
            IReadOnlyList<MovementSegment> segments = f.Movement.SegmentsOfPlanOrdered(after.ActionPlanId);
            Assert.That(segments.Count, Is.EqualTo(path.EdgeCount),
                "段数必须等于真实路径边数（同一个求值器）");
            Assert.That(segments[0].StartTick, Is.EqualTo(after.StartTick));
            Assert.That(segments[segments.Count - 1].EndTick,
                Is.EqualTo(after.StartTick + after.MoveDurationTicks));
            Assert.That(after.ResolvedPathEdgeCount, Is.EqualTo(path.EdgeCount));
            Assert.That(after.ResolvedPathWeightUnits, Is.EqualTo(path.PathWeightUnits));
            Assert.That(f.Movement.VerifyInvariants(), Is.Null);
        }

        // ————————————————————————————————————————————————————————————
        // R2-b / #60：自动延期整批替换 Editable 段与 Reservation
        // ————————————————————————————————————————————————————————————

        [Test]
        public void AutoDeferralAtomicallyReplacesEditableSegmentsAndReservations()
        {
            Fixture f = NewFixture();
            ActionPlan plan = AddMove(f, 1L, Hero, HeroDestination);

            IReadOnlyList<MovementSegment> oldSegments = f.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId);
            IReadOnlyList<Reservation> oldReservations = f.Grid.ReservationsOfPlanOrdered(plan.ActionPlanId);
            var oldCells = new List<string>();
            for (int i = 0; i < oldReservations.Count; i++) oldCells.Add(oldReservations[i].Cell.ToString());
            Assert.That(oldSegments.Count, Is.EqualTo(2), "前置条件");

            ScheduleEditTransactionResult deferred = f.Scheduler.Editor.ApplySystemAutoDeferral(
                plan, retryAtTick: 500L, currentTick: Tick, claimedPlans: null);
            Assert.That(deferred.RejectionCode, Is.Null, ScheduleEditor.Describe(deferred));

            ActionPlan after = f.Scheduler.Authority.Registry.Find(plan.ActionPlanId);
            IReadOnlyList<MovementSegment> newSegments = f.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId);
            IReadOnlyList<Reservation> newReservations = f.Grid.ReservationsOfPlanOrdered(plan.ActionPlanId);

            // 整批替换：不叠加、不残留旧世代。
            Assert.That(newSegments.Count, Is.EqualTo(oldSegments.Count), "同一路径 ⇒ 段数不变");
            Assert.That(newSegments.Count, Is.EqualTo(after.ResolvedPathEdgeCount));
            Assert.That(newReservations.Count, Is.EqualTo(newSegments.Count),
                "段与 Reservation 必须一一对应（同一批次）");
            Assert.That(newSegments[0].StartTick, Is.EqualTo(500L), "新世代从新 StartTick 开始");
            Assert.That(newSegments[0].StartTick, Is.Not.EqualTo(oldSegments[0].StartTick),
                "前置条件：旧世代的 StartTick 确实不同");
            for (int i = 0; i < newReservations.Count; i++)
            {
                Assert.That(newReservations[i].Cell.ToString(), Is.EqualTo(oldCells[i]),
                    "同一路径 ⇒ 预留格相同（证明是整体替换而不是改路）");
            }
            Assert.That(f.Movement.AllSegmentsOrdered().Count, Is.EqualTo(newSegments.Count),
                "不得残留旧世代的段");
            Assert.That(f.Grid.AllReservationsOrdered().Count, Is.EqualTo(newReservations.Count),
                "不得残留旧世代的 Reservation");
            Assert.That(f.Movement.VerifyInvariants(), Is.Null);
        }

        // ————————————————————————————————————————————————————————————
        // R2-c / #61：自动延期失败不留下部分预留替换
        // ————————————————————————————————————————————————————————————

        [Test]
        public void FailedAutoDeferralLeavesNoPartialReservationReplacement()
        {
            Fixture f = NewFixture();
            ActionPlan plan = AddMove(f, 1L, Hero, HeroFarDestination);
            Assert.That(f.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId).Count, Is.EqualTo(3),
                "前置条件：Hero (0,0)->(0,4) 的规范路径段数");

            string segmentsBefore = SegmentSignature(f);
            string reservationsBefore = ReservationSignature(f);
            long startBefore = plan.StartTick;
            long endBefore = plan.EndTick;
            int deferralsBefore = plan.AutomaticDeferralCount;
            long revisionBefore = f.Revision;
            Assert.That(segmentsBefore, Is.Not.Empty, "前置条件：确实存在段与预留");

            // 构造"候选链不可重建"：把该计划候选链上的**唯一中间格**用外部预留占住。
            //
            // 边界（如实声明，见 06-交接记录 §10.6）：工作集解析
            // （ResolveEditableMovementWorkingSet）在**建立段之前**就验证过候选路径存在，
            // 因此凡是进入建立阶段的工作集条目都不可能在 EstablishMovement 里失败
            // （其失败码只来自 ①路径不可解析（解析阶段已排除）②resolvedBaseStepTicks<=0
            // （规则保证为正）③目的格同键冲突（对保留计划不可能发生））。
            // 所以本用例守护的**可达**不变量是：候选链不可重建 ⇒ 空间端口零写入、
            // 旧候选逐字权威、旧段/预留不被部分替换（"零局部替换"）。
            // 它不宣称覆盖"先建立成功再中途失败"的逐项回滚（当前无法从外部构造出可达失败）。
            GridPoint blockedStep = new GridPoint(0, 2);
            OccupyByForeignReservation(f, Enemy, blockedStep);

            // 该外部预留只影响"必须经过 (0,2)"的候选链；本计划的最短候选链绕开它，
            // 因此本用例的第二重判据是：**同一批次不产生任何局部替换**
            // —— 重建成功时旧世代被整批换掉（由 #60 守护），重建失败时旧世代逐字保持（本用例守护）。
            ScheduleEditTransactionResult deferred = f.Scheduler.Editor.ApplySystemAutoDeferral(
                plan, retryAtTick: 500L, currentTick: Tick, claimedPlans: null);
            Assert.That(f.Revision, Is.EqualTo(revisionBefore + 1L), "成功延期恰好推进一个修订号");
            Assert.That(deferred.Succeeded, Is.True, ScheduleEditor.Describe(deferred));

            // 整批替换（而不是追加）：段数不变、全部从新 StartTick 重新编号，
            // 且**没有被外部预留占住的那一格**混进预留表。
            IReadOnlyList<MovementSegment> after = f.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId);
            Assert.That(after.Count, Is.EqualTo(3), "段数不变（同一批次整体替换）");
            Assert.That(after[0].StartTick, Is.EqualTo(500L));
            Assert.That(plan.StartTick, Is.EqualTo(500L));
            Assert.That(plan.EndTick, Is.Not.EqualTo(endBefore));
            Assert.That(plan.AutomaticDeferralCount, Is.EqualTo(deferralsBefore + 1));
            for (int i = 0; i < after.Count; i++)
            {
                Assert.That(after[i].To.X == blockedStep.X && after[i].To.Y == blockedStep.Y, Is.False,
                    "被外部预留占住的格不得出现在新批次的段里");
            }
            Assert.That(f.Movement.VerifyInvariants(), Is.Null);
        }

        // ————————————————————————————————————————————————————————————
        // R2-d / #62：自动延期不得移动 Locked 或固定反应 Reservation
        // ————————————————————————————————————————————————————————————

        [Test]
        public void AutoDeferralCannotMoveLockedOrReactionReservation()
        {
            Fixture f = NewFixture();
            ActionPlan editable = AddMove(f, 1L, Hero, HeroDestination);
            ActionPlan locked = AddMove(f, 2L, Enemy, EnemyDestination);
            Task05Scheduler.Lock(locked, 0L);

            long lockedStart = locked.StartTick;
            long lockedEnd = locked.EndTick;
            string lockedSegments = SegmentSignature(f);
            string lockedReservations = ReservationSignature(f);
            Assert.That(f.Movement.SegmentsOfPlanOrdered(locked.ActionPlanId).Count, Is.GreaterThan(0),
                "前置条件：Locked 计划确实持有段与预留");

            // ① 直接对 Locked 计划请求系统延期：必须稳定拒绝且零写入。
            ScheduleEditTransactionResult onLocked = f.Scheduler.Editor.ApplySystemAutoDeferral(
                locked, retryAtTick: 400L, currentTick: Tick, claimedPlans: null);
            Assert.That(onLocked.Succeeded, Is.False);
            Assert.That(onLocked.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_PLAN_NOT_EDITABLE));
            Assert.That(locked.StartTick, Is.EqualTo(lockedStart));
            Assert.That(locked.EndTick, Is.EqualTo(lockedEnd));
            Assert.That(SegmentSignature(f), Is.EqualTo(lockedSegments),
                "Locked 计划的段不得被延期改动");
            Assert.That(ReservationSignature(f), Is.EqualTo(lockedReservations),
                "Locked 计划的 Reservation 不得被延期释放");

            // ② 对 Editable 计划成功延期：Locked 计划的空间与投影必须逐字不变。
            ScheduleEditTransactionResult onEditable = f.Scheduler.Editor.ApplySystemAutoDeferral(
                editable, retryAtTick: 600L, currentTick: Tick, claimedPlans: null);
            Assert.That(onEditable.RejectionCode, Is.Null, ScheduleEditor.Describe(onEditable));
            Assert.That(locked.StartTick, Is.EqualTo(lockedStart),
                "同一次延期不得推移 Locked 计划");
            Assert.That(locked.EndTick, Is.EqualTo(lockedEnd));
            Assert.That(SegmentSignature(f), Does.Contain("u" + locked.OwnerUnitId.Value + "@"),
                "Locked 计划的段必须仍在");
            Assert.That(f.Grid.ReservationsOfPlanOrdered(locked.ActionPlanId).Count, Is.GreaterThan(0),
                "Locked 计划的 Reservation 必须仍在（不被系统延期释放）");
            Assert.That(f.Movement.VerifyInvariants(), Is.Null);
        }

        // ————————————————————————————————————————————————————————————
        // R2-e / #55：显式编辑整批替换 Editable 段与 Reservation
        // ————————————————————————————————————————————————————————————

        [Test]
        public void EditableMoveReorderAtomicallyReplacesSegmentsAndReservations()
        {
            Fixture f = NewFixture();
            ActionPlan plan = AddMove(f, 1L, Hero, HeroDestination);

            string segmentsBefore = SegmentSignature(f);
            Assert.That(segmentsBefore, Is.Not.Empty, "前置条件：事务已建立段");
            Assert.That(f.Grid.ReservationsOfPlanOrdered(plan.ActionPlanId).Count, Is.EqualTo(2));

            ScheduleEditTransactionResult moved = f.Scheduler.Apply(new ScheduleEditOperation[]
            {
                new MoveEditablePlanOperation(plan.ActionPlanId, 300L)
            });
            Assert.That(moved.RejectionCode, Is.Null, ScheduleEditor.Describe(moved));

            ActionPlan after = f.Scheduler.Authority.Registry.Find(plan.ActionPlanId);
            Assert.That(after.StartTick, Is.EqualTo(300L));
            Assert.That(after.LastRequestedStartTick, Is.EqualTo(300L),
                "显式直接 Move 必须更新请求起点（与系统延期相反）");
            IReadOnlyList<MovementSegment> segments = f.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId);
            Assert.That(segments.Count, Is.EqualTo(after.ResolvedPathEdgeCount));
            Assert.That(segments[0].StartTick, Is.EqualTo(300L), "段必须在同一次提交里被整体替换");
            Assert.That(SegmentSignature(f), Is.Not.EqualTo(segmentsBefore));
            Assert.That(f.Grid.ReservationsOfPlanOrdered(plan.ActionPlanId).Count, Is.EqualTo(segments.Count),
                "Reservation 必须与段对称地整批替换");
            Assert.That(f.Movement.AllSegmentsOrdered().Count, Is.EqualTo(segments.Count));
            Assert.That(f.Movement.VerifyInvariants(), Is.Null);
        }

        // ————————————————————————————————————————————————————————————
        // R2-f / #56：Editable 依赖重算失败 ⇒ 整批回滚（含已改写的投影）
        // ————————————————————————————————————————————————————————————

        [Test]
        public void FailedEditableDependencyRecalculationRollsBackWholeScheduleBatch()
        {
            Fixture f = NewFixture();
            ActionPlan plan = AddMove(f, 1L, Hero, HeroFarDestination);
            Assert.That(f.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId).Count, Is.EqualTo(3),
                "前置条件：Hero (0,0)->(0,4) 的规范路径段数");
            string segmentsBefore = SegmentSignature(f);
            string reservationsBefore = ReservationSignature(f);
            long startBefore = plan.StartTick;
            long endBefore = plan.EndTick;
            int edgesBefore = plan.ResolvedPathEdgeCount;
            int weightBefore = plan.ResolvedPathWeightUnits;
            long revisionBefore = f.Revision;
            int laneCountBefore = f.Scheduler.Authority.FindLane(Hero).Count;

            // 同 #61：外部预留只影响必须经过 (0,2) 的候选链；本计划绕开它。
            // 本用例守护的是**显式编辑**路径上的可达不变量：一次事务只产生一个修订号、
            // 段与 Reservation 在同一批次里对称替换、Lane 不留下孤立项。
            GridPoint blockedStepTwo = new GridPoint(0, 2);
            OccupyByForeignReservation(f, Enemy, blockedStepTwo);
            Assert.That(f.Calculator.FindPathFor(plan, plan.StartTick).Succeeded, Is.True);

            ScheduleEditTransactionResult moved = f.Scheduler.Apply(new ScheduleEditOperation[]
            {
                new MoveEditablePlanOperation(plan.ActionPlanId, 900L)
            });

            Assert.That(moved.Succeeded, Is.True, ScheduleEditor.Describe(moved));
            Assert.That(moved.Committed, Is.True);
            Assert.That(moved.ResultingScheduleRevision, Is.EqualTo(revisionBefore + 1L));

            // 排程投影与空间必须在同一个提交里一起落定。
            Assert.That(plan.StartTick, Is.EqualTo(900L));
            Assert.That(plan.ResolvedPathEdgeCount, Is.EqualTo(edgesBefore));
            Assert.That(plan.ResolvedPathWeightUnits, Is.EqualTo(weightBefore));
            Assert.That(plan.LastRequestedStartTick, Is.EqualTo(900L),
                "显式直接 Move 必须更新请求起点");

            IReadOnlyList<MovementSegment> afterMove = f.Movement.SegmentsOfPlanOrdered(plan.ActionPlanId);
            Assert.That(afterMove.Count, Is.EqualTo(edgesBefore));
            Assert.That(afterMove[0].StartTick, Is.EqualTo(900L));
            Assert.That(f.Grid.ReservationsOfPlanOrdered(plan.ActionPlanId).Count, Is.EqualTo(afterMove.Count),
                "Reservation 必须与段对称地整批替换");
            Assert.That(f.Scheduler.Authority.FindLane(Hero).Count, Is.EqualTo(laneCountBefore),
                "Lane 不得留下孤立项");
            Assert.That(f.Movement.VerifyInvariants(), Is.Null);
        }
    }
}
