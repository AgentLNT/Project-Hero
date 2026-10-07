using System;
using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Authoring;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Compatibility.Runtime.Input;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Turns;
using UnityEngine;

namespace ProjectHero.Tests.UnityView.Editor
{
    /// <summary>
    /// 任务 09 / B 流的测试夹具。
    ///
    /// 它<strong>只用真实配置链</strong>（资源 → <c>BattleDefinitionBuilder</c> →
    /// <c>BattleSimulation.Create</c>）构造权威状态，不建立第二套配置模型，
    /// 也不用测试替身冒充 <c>BattleSimulation</c>：
    /// 「Logic 零写入」必须由<strong>真实</strong>模拟的规范化快照读数证明。
    ///
    /// 视图侧唯一被注入的是 <see cref="IViewLogicPort"/> 的实现
    /// （<see cref="SimulationViewLogicPort"/>）：它把只读查询接到真实模拟，
    /// 并把唯一写入口接到真实入口 <c>CommandIngressEntry.Submit</c>。
    /// </summary>
    internal static class Task09Fixture
    {
        private static BattleDefinition _cached;

        public static BattleDefinition Definition
        {
            get
            {
                if (_cached != null) return _cached;

                LegacyAssetSet assets = LegacyAssetResolver.LoadAllFromResources();
                var units = new LegacyBattleUnitSource(
                    HeroStats, EnemyStats, "task09-b fixture");

                BattleDefinitionBuildResult result =
                    BattleDefinitionBuilder.BuildMainBattleDefinition(assets, units);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        "TASK09_FIXTURE_DEFINITION_BUILD_FAILED|" + string.Join(",", result.ErrorCodes()));
                }

                _cached = result.Definition;
                return _cached;
            }
        }

        /// <summary>主战斗场景 Player 的旧字段（与任务 02B 夹具一致）。</summary>
        public static LegacyUnitStatInputs HeroStats => new LegacyUnitStatInputs(
            strength: 10f, dexterity: 10f, constitution: 10f,
            armorWeight: 10f, armorDefense: 0f, magicResistance: 0f, isExhausted: false);

        public static LegacyUnitStatInputs EnemyStats => HeroStats;

        public static EncounterDefinitionId EncounterId
            => new EncounterDefinitionId(LegacyIdMigrationManifest.MainEncounterId);

        public static BattleRuntimeInputs RuntimeInputs => new BattleRuntimeInputs(
            InitialRngSeed: 20260922UL,
            InitialMetaResource: BattleDefinitionBuilder.MainEncounterInitialMetaResource);

        /// <summary>与 <c>BattleInitializer</c> 一致的槽位顺序：enemy = UnitId(1)、hero = UnitId(2)。</summary>
        public static UnitId EnemyUnitId => new UnitId(1);

        public static UnitId HeroUnitId => new UnitId(2);

        public static ControllerId PlayerControllerId
            => new ControllerId(LegacyIdMigrationManifest.ControllerPlayer);

        public static ControllerId AiControllerId
            => new ControllerId(LegacyIdMigrationManifest.ControllerEnemyAi);

        public static BattleSimulation NewSimulation()
            => BattleSimulation.Create(Definition, EncounterId, RuntimeInputs);

        /// <summary>
        /// 带<strong>脚本窗口</strong>的模拟（B2）：真实配置链不提供窗口排程
        /// （<c>noTurnWindowSchedule</c>），而"新增普通计划"必须携带当前窗口 scope，
        /// 因此"真实可编辑计划"的夹具前提必须显式给窗口。
        ///
        /// 窗口排程的唯一入口就是 <see cref="ITurnWindowSchedule"/> 端口——
        /// 这里不绕过 <c>TurnWindowManager</c>、不直接造 <c>TurnWindow</c>。
        /// </summary>
        public static BattleSimulation NewSimulation(ITurnWindowSchedule schedule)
            => schedule == null
                ? NewSimulation()
                : BattleSimulation.Create(Definition, EncounterId, RuntimeInputs,
                    new BattleSimulationAssembly(turnWindowSchedule: schedule));

        /// <summary>真实窗口预算（与任务 09 A 流夹具同值；窗口只在脚本 Tick 打开）。</summary>
        public const int WindowBudget = 400;

        public static ScriptedWindowSchedule WindowAtZero()
            => new ScriptedWindowSchedule().Open(0L, HeroUnitId, WindowBudget);

        /// <summary>在指定 Tick 提交"给英雄新增一个普通攻击计划"（真实命令载荷）。</summary>
        public static CommandRequest AddHeroAttackCommand(
            long targetTick, long? requestedStartTick, long expectedRevision, WindowId? window,
            long temporaryKey = 1L)
            => new CommandRequest(
                targetTick,
                new ScheduleEditScope(expectedRevision, window),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    new AddOrdinaryPlanOperation(
                        temporaryKey,
                        HeroUnitId,
                        new ActionSpecId(LegacyIdMigrationManifest.ActionQuickSlashR1),
                        requestedStartTick,
                        default,
                        null,
                        GridDirection.East,
                        null)
                }));

        /// <summary>删除仍为 Editable 的普通计划（真实命令载荷；scope 由调用方给）。</summary>
        public static CommandRequest RemovePlanCommand(
            long targetTick, long expectedRevision, WindowId? window, long actionPlanId)
            => new CommandRequest(
                targetTick,
                new ScheduleEditScope(expectedRevision, window),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    new RemoveEditablePlanOperation(
                        new ActionPlanId(actionPlanId), ActionTerminationReason.CancelledByCommand)
                }));

        /// <summary>
        /// 脚本窗口排程（唯一入口 <see cref="ITurnWindowSchedule"/>）。
        /// 与任务 09 A 流夹具同形，刻意复用同一形态而不是各写一套。
        /// </summary>
        public sealed class ScriptedWindowSchedule : ITurnWindowSchedule
        {
            private readonly Dictionary<long, WindowOpenRequest> _opens =
                new Dictionary<long, WindowOpenRequest>();
            private readonly HashSet<long> _closes = new HashSet<long>();

            public ScriptedWindowSchedule Open(long tick, UnitId owner, int budget)
            {
                _opens[tick] = new WindowOpenRequest(owner, budget);
                return this;
            }

            public ScriptedWindowSchedule Close(long tick)
            {
                _closes.Add(tick);
                return this;
            }

            public WindowOpenRequest TryOpenDue(long tick)
                => _opens.TryGetValue(tick, out WindowOpenRequest request) ? request : null;

            public bool ShouldCloseCurrentWindow(long tick) => _closes.Contains(tick);
        }

        public static CommandIngressEntry PlayerEntry(BattleSimulation simulation)
            => simulation.CommandIngress.FindEntry(PlayerControllerId);

        /// <summary>推进一个 Tick（唯一合法驱动序列：先冻结、再 Step）。</summary>
        public static StepResult StepNext(BattleSimulation simulation)
        {
            long tick = simulation.Tick + 1L;
            FrozenCommandBatch batch = simulation.CommandIngress.FreezeTick(tick);
            return simulation.Step(tick, batch);
        }

        /// <summary>推进一个 Tick，并在冻结前经 Player 入口提交一批请求。</summary>
        public static StepResult StepNext(BattleSimulation simulation, params CommandRequest[] requests)
        {
            CommandIngressEntry player = PlayerEntry(simulation);
            for (int i = 0; i < requests.Length; i++)
            {
                Assert.That(player.Submit(requests[i]), Is.Null, "夹具前提：命令入口必须接受请求");
            }
            return StepNext(simulation);
        }

        /// <summary>推进到指定 Tick（含），用于构造"战斗中进行到某一 Tick"的场景。</summary>
        public static void AdvanceTo(BattleSimulation simulation, long targetTick)
        {
            while (simulation.Tick < targetTick)
            {
                StepResult result = StepNext(simulation);
                if (result.Status == StepStatus.BattleEnded) return;
            }
        }

        /// <summary>
        /// 真实权威状态的逐字段读数：调用前后比较它，就能证明"视图层没有写逻辑"。
        ///
        /// 覆盖的字段（全部来自 <see cref="LogicSnapshot"/> 的规范化投影）：
        /// 规范化哈希、Tick、ScheduleRevision、全部计划（逐字段）、全部单位（位置/生命/朝向）、
        /// 全部 Intent / MovementSegment / Reservation / ActorLane、窗口账本、资源账本、
        /// 命令入口水位与未来桶、终态归档计数与摘要。
        /// </summary>
        public static string AuthorityFingerprint(BattleSimulation simulation)
        {
            LogicSnapshot snapshot = simulation.CurrentSnapshot;
            var builder = new System.Text.StringBuilder(4096);

            builder.Append("hash=").Append(snapshot.ComputeHashHex())
                   .Append(";tick=").Append(snapshot.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture))
                   .Append(";rev=").Append(snapshot.ScheduleRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))
                   .Append(";ended=").Append(snapshot.BattleEnd.IsEnded ? "1" : "0")
                   .Append(";terminalRecords=").Append(snapshot.TerminalPlanRecordCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                   .Append(";terminalDigest=").Append(snapshot.TerminalPlanDigest ?? string.Empty);

            builder.Append("|ingress=").Append(snapshot.CommandIngresses.FrozenThroughTick.ToString(System.Globalization.CultureInfo.InvariantCulture))
                   .Append('/').Append(snapshot.CommandIngresses.PendingRejectionCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < snapshot.CommandIngresses.Entries.Count; i++)
            {
                CommandIngressEntrySnapshot entry = snapshot.CommandIngresses.Entries[i];
                builder.Append('/').Append(entry.ControllerId).Append(':')
                       .Append(entry.SourceKind.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(entry.NextProducerOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(entry.FrozenProducerOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            for (int i = 0; i < snapshot.CommandIngresses.FutureBuckets.Count; i++)
            {
                CommandIngressTickBucketSnapshot bucket = snapshot.CommandIngresses.FutureBuckets[i];
                builder.Append("/b").Append(bucket.TargetTick.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Append(':').Append(bucket.PendingCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            builder.Append("|window=").Append(snapshot.WindowManager.CurrentWindowId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                   .Append('/').Append(snapshot.WindowManager.NextWindowTick.ToString(System.Globalization.CultureInfo.InvariantCulture))
                   .Append('/').Append(snapshot.WindowManager.LastClosedWindowId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < snapshot.WindowManager.Windows.Count; i++)
            {
                TurnWindowSnapshot window = snapshot.WindowManager.Windows[i];
                builder.Append("/w").Append(window.WindowId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Append(':').Append(window.TotalBudgetTicks.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Append(':').Append(window.ReservedBudgetTicks.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Append(':').Append(window.SpentBudgetTicks.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Append(':').Append(window.IsOpen ? "1" : "0")
                       .Append(':').Append(window.IsAcceptingSubmissions ? "1" : "0");
                if (window.Reservations == null) continue;
                for (int r = 0; r < window.Reservations.Count; r++)
                {
                    builder.Append("/wr").Append(window.Reservations[r].ActionPlanId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                           .Append(':').Append(window.Reservations[r].ReservedTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            builder.Append("|res=").Append(snapshot.Resources.MetaResource.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (snapshot.Resources.AdrenalineLedgers != null)
            for (int i = 0; i < snapshot.Resources.AdrenalineLedgers.Count; i++)
            {
                AdrenalineLedgerSnapshot ledger = snapshot.Resources.AdrenalineLedgers[i];
                builder.Append("/a").Append(ledger.UnitId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Append(':').Append(ledger.AvailableAdrenaline.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Append(':').Append(ledger.CycleId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Append(':').Append(ledger.ReservedTotal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            builder.Append("|plans=");
            for (int i = 0; i < snapshot.Plans.Count; i++)
            {
                ActionPlanSnapshot plan = snapshot.Plans[i];
                if (i > 0) builder.Append('/');
                builder.Append(plan.ActionPlanId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.OwnerUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.ActionSpecId ?? string.Empty).Append(':')
                       .Append(plan.Origin.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.State.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.StartTick.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.EndTick.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.LastRequestedStartTick.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.LastEditedScheduleRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.BudgetCostTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.ReservedTurnBudgetTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.SubmittedWindowId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.TriggerTick.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.PrimaryTargetUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.DestinationX.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                       .Append(plan.DestinationY.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.HasDestination ? "1" : "0").Append(':')
                       .Append(plan.TerminalTick.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(plan.TerminationReason.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            builder.Append("|units=");
            for (int i = 0; i < snapshot.Units.Count; i++)
            {
                UnitSnapshot unit = snapshot.Units[i];
                if (i > 0) builder.Append('/');
                builder.Append(unit.UnitId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(unit.X.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                       .Append(unit.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(unit.Facing.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(unit.HealthQ10.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(unit.IsAlive ? "1" : "0");
            }

            builder.Append("|intents=").Append(snapshot.Intents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < snapshot.Intents.Count; i++)
            {
                IntentSnapshot intent = snapshot.Intents[i];
                builder.Append('/').Append(intent.IntentSequence.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Append(':').Append(intent.ActionPlanId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Append(':').Append(intent.ImpactTick.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            builder.Append("|segments=");
            for (int i = 0; i < snapshot.MovementSegments.Count; i++)
            {
                MovementSegmentSnapshot segment = snapshot.MovementSegments[i];
                if (i > 0) builder.Append('/');
                builder.Append(segment.ActionPlanId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(segment.StepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(segment.FromX.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                       .Append(segment.FromY.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("->")
                       .Append(segment.ToX.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                       .Append(segment.ToY.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(segment.EndTick.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            builder.Append("|reservations=");
            for (int i = 0; i < snapshot.Reservations.Count; i++)
            {
                ReservationSnapshot reservation = snapshot.Reservations[i];
                if (i > 0) builder.Append('/');
                builder.Append(reservation.ActionPlanId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(reservation.X.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                       .Append(reservation.Y.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            builder.Append("|lanes=");
            for (int i = 0; i < snapshot.ActorLanes.Count; i++)
            {
                ActorLaneSnapshot lane = snapshot.ActorLanes[i];
                if (i > 0) builder.Append('/');
                builder.Append(lane.UnitId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(lane.PendingPlanCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(lane.Locked ? "1" : "0");
            }

            builder.Append("|opps=");
            for (int i = 0; i < snapshot.ReactionOpportunities.Count; i++)
            {
                ReactionOpportunitySnapshot opportunity = snapshot.ReactionOpportunities[i];
                if (i > 0) builder.Append('/');
                builder.Append(opportunity.ReactionOpportunityId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(opportunity.DefenderUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(opportunity.State.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(opportunity.TriggerTick.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(opportunity.BoundActionPlanId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                       .Append(opportunity.OpenOptionCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// <see cref="IViewLogicPort"/> 的真实实现：全部只读查询接到真实
    /// <c>BattleSimulation</c>，唯一写入口接到真实 <c>CommandIngressEntry.Submit</c>。
    ///
    /// 它<strong>不</strong>在视图侧重建任何规则：关系分类用
    /// <c>DecisionSnapshot.FactionResolver</c> 的同一个只读实例；Dodge 目的格用
    /// <c>DodgeRelocationAuthority.EvaluateDestination</c>（与提交共用的同一个求值函数）；
    /// 控制权过滤用权威 Encounter 槽位绑定。
    /// </summary>
    internal sealed class SimulationViewLogicPort : IViewLogicPort
    {
        private readonly BattleSimulation _simulation;
        private readonly CommandIngressEntry _entry;
        private readonly BattleDefinition _definition;
        private readonly List<long> _controllableUnits = new List<long>();

        public SimulationViewLogicPort(BattleSimulation simulation, ControllerId controllerId)
            : this(simulation, controllerId, Task09Fixture.Definition)
        {
        }

        public SimulationViewLogicPort(
            BattleSimulation simulation, ControllerId controllerId, BattleDefinition definition)
        {
            _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            ControllerId = controllerId;
            _entry = simulation.CommandIngress.FindEntry(controllerId);
            if (_entry == null)
            {
                throw new InvalidOperationException(
                    "TASK09_FIXTURE_INGRESS_MISSING|" + (controllerId.Value ?? "<null>"));
            }

            // 控制权过滤用权威 ControllerBinding.ControlledSlots + EncounterSlotOrdering，
            // 与 BattleInitializer 的 UnitId 分配规则完全一致（不按发现顺序猜）。
            EncounterDefinition encounter = _definition.FindEncounter(simulation.EncounterId);
            if (encounter != null && encounter.Controllers != null && encounter.Slots != null)
            {
                IReadOnlyList<EncounterUnitSlot> ordered =
                    EncounterSlotOrdering.OrderBySlotIdOrdinal(encounter.Slots);
                for (int c = 0; c < encounter.Controllers.Count; c++)
                {
                    ControllerBinding binding = encounter.Controllers[c];
                    if (binding == null || binding.ControlledSlots == null) continue;
                    if (!string.Equals(binding.ControllerId.Value, controllerId.Value, StringComparison.Ordinal)) continue;

                    for (int s = 0; s < ordered.Count; s++)
                    {
                        EncounterUnitSlot slot = ordered[s];
                        if (slot == null) continue;
                        for (int k = 0; k < binding.ControlledSlots.Count; k++)
                        {
                            if (!string.Equals(binding.ControlledSlots[k].Value, slot.SlotId.Value, StringComparison.Ordinal))
                                continue;
                            _controllableUnits.Add(s + 1L);
                        }
                    }
                }
            }
            _controllableUnits.Sort();
        }

        public ControllerId ControllerId { get; }

        /// <summary>本端口发出的请求条目数（用于证明"逐帧预览不提交"）。</summary>
        public int SubmissionCount { get; private set; }

        /// <summary>最近一次被提交的请求（诊断用）。</summary>
        public CommandRequest LastSubmitted { get; private set; }

        public long CurrentTick => _simulation.Tick;

        public long ScheduleRevision => _simulation.CurrentSnapshot.ScheduleRevision;

        public bool IsBattleEnded => _simulation.IsEnded;

        public CommandIngressRejection SubmitCommand(CommandRequest request)
        {
            if (request == null) return null;
            SubmissionCount++;
            LastSubmitted = request;
            return _entry.Submit(request);
        }

        public bool TryGetPlanSnapshot(ActionPlanId planId, out ActionPlanSnapshot plan)
        {
            plan = FindPlan(planId);
            return plan != null;
        }

        public bool TryFindEditablePlan(ActionPlanId planId, out ActionPlanSnapshot plan)
        {
            plan = FindPlan(planId);
            return IsEditableOrdinaryForThisController(plan);
        }

        /// <summary>
        /// 该 Controller 当前可编辑的普通计划投影（B2 时间线块身份来源）。
        /// 过滤谓词与 <see cref="TryFindEditablePlan"/> <strong>共用同一个</strong>
        /// <see cref="IsEditableOrdinaryForThisController"/>，因此两条只读面不可能给出不同口径。
        /// 顺序沿用 <c>LogicSnapshot.Plans</c> 的规范顺序（不重排）。
        /// </summary>
        public IReadOnlyList<ActionPlanSnapshot> EditablePlansOf(ControllerId controllerId)
        {
            LogicSnapshot snapshot = _simulation.CurrentSnapshot;
            var result = new List<ActionPlanSnapshot>();
            if (snapshot.Plans == null) return result;

            for (int i = 0; i < snapshot.Plans.Count; i++)
            {
                ActionPlanSnapshot plan = snapshot.Plans[i];
                if (!IsEditableOrdinaryForThisController(plan)) continue;
                if (!string.Equals(ControllerId.Value, controllerId.Value, StringComparison.Ordinal)) continue;
                result.Add(plan);
            }
            return result;
        }

        private bool IsEditableOrdinaryForThisController(ActionPlanSnapshot plan)
        {
            if (plan == null) return false;
            if (plan.State != (int)ActionPlanState.Editable) return false;
            if (plan.Origin != (int)ActionPlanOrigin.Ordinary) return false;
            return IsControllable(plan.OwnerUnitId);
        }

        public bool TryGetActionType(string actionSpecId, out ActionType actionType)
        {
            actionType = default;
            if (string.IsNullOrEmpty(actionSpecId)) return false;
            ActionSpec spec = _definition.FindAction(new ActionSpecId(actionSpecId));
            if (spec == null) return false;
            actionType = spec.Type;
            return true;
        }

        public IReadOnlyList<ReactionOpportunitySnapshot> ReactionOpportunitiesOf(ControllerId controllerId)
        {
            LogicSnapshot snapshot = _simulation.CurrentSnapshot;
            var result = new List<ReactionOpportunitySnapshot>();
            for (int i = 0; i < snapshot.ReactionOpportunities.Count; i++)
            {
                ReactionOpportunitySnapshot opportunity = snapshot.ReactionOpportunities[i];
                if (opportunity == null) continue;
                if (!IsControllable(opportunity.DefenderUnitId)) continue;
                result.Add(opportunity);
            }
            return result;
        }

        public WindowId? OpenWindowId
        {
            get
            {
                long windowId = _simulation.CurrentSnapshot.WindowManager.CurrentWindowId;
                return windowId > 0L ? new WindowId?(new WindowId(windowId)) : null;
            }
        }

        public string DescribeDodgeDestinationRejection(
            UnitId defenderUnitId, ActionSpecId dodgeSpecId, GridPoint destination)
        {
            ActionSpec spec = _definition.FindAction(dodgeSpecId);
            var payload = spec != null ? spec.Payload as DodgePayloadSpec : null;
            DodgeDestinationRules rules = DodgeDestinationRules.FromPayload(payload);
            if (rules == null) return DodgeRelocationCodes.DODGE_RELOCATION_PLAN_NOT_CANONICAL;

            LogicGrid grid = _simulation.LogicGrid;
            GridPoint from;
            GridDirection facing;
            if (!grid.TryGetAnchor(defenderUnitId, out from)) return LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN;
            if (!grid.TryGetFacing(defenderUnitId, out facing)) return LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN;

            long triggerTick = _simulation.Tick + 1L;
            var provisional = new DodgeDestinationReservation(
                default, default, defenderUnitId, facing, from, destination,
                rules.MaxDistanceSteps, rules.PatternDirections, triggerTick, _simulation.Tick, 0L);
            return _simulation.DodgeRelocation.EvaluateDestination(provisional);
        }

        public DecisionSnapshot DecisionSnapshot => _simulation.LastDecisionSnapshot;

        /// <summary>Faction 分类一律走整场唯一的只读解析器实例（不复制矩阵）。</summary>
        public ProjectHero.Logic.Factions.UnitRelation Classify(UnitId source, UnitId target)
            => _simulation.FactionResolver.Classify(source, target);

        private ActionPlanSnapshot FindPlan(ActionPlanId planId)
        {
            if (!planId.IsValid) return null;
            LogicSnapshot snapshot = _simulation.CurrentSnapshot;
            for (int i = 0; i < snapshot.Plans.Count; i++)
            {
                ActionPlanSnapshot plan = snapshot.Plans[i];
                if (plan != null && plan.ActionPlanId == planId.Value) return plan;
            }
            return null;
        }

        private bool IsControllable(long unitId)
        {
            for (int i = 0; i < _controllableUnits.Count; i++)
            {
                if (_controllableUnits[i] == unitId) return true;
            }
            return false;
        }
    }
}
