using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ProjectHero.Authoring.Tests;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Authoring.Tests.Task03
{
    /// <summary>
    /// 任务 03 测试夹具。全部使用任务 02B 的<strong>真实</strong>定义
    /// （<see cref="BattleDefinitionFixture"/>，来自真实资产），不建立第二套配置模型。
    /// </summary>
    internal static class Task03
    {
        public const string HeroSlot = "hero";
        public const string EnemySlot = "enemy";
        public const string PlayerController = "controller.player";
        public const string EnemyAiController = "controller.enemy_ai";

        public static BattleDefinition Definition => BattleDefinitionFixture.Definition;

        public static EncounterDefinition Encounter => BattleDefinitionFixture.MainEncounter;

        public static BattleRuntimeInputs Inputs => BattleDefinitionFixture.RuntimeInputs;

        public static EncounterDefinitionId EncounterId => BattleDefinitionFixture.MainEncounter.EncounterId;

        /// <summary>主战斗场景按 SlotId 的 Ordinal 顺序：enemy=UnitId(1)、hero=UnitId(2)。</summary>
        public static UnitId EnemyUnitId => new UnitId(1);

        public static UnitId HeroUnitId => new UnitId(2);

        public static BattleSimulation NewSim(BattleSimulationAssembly assembly = null)
            => assembly == null
                ? BattleSimulation.Create(Definition, EncounterId, Inputs)
                : BattleSimulation.Create(Definition, EncounterId, Inputs, assembly);

        public static CommandIngressEntry PlayerEntry(BattleSimulation sim)
            => sim.CommandIngress.FindEntry(new ControllerId(PlayerController));

        public static CommandIngressEntry AiEntry(BattleSimulation sim)
            => sim.CommandIngress.FindEntry(new ControllerId(EnemyAiController));

        public static CommandIngressEntry SystemEntry(BattleSimulation sim)
            => sim.CommandIngress.FindEntry(new ControllerId(BattleSimulation.SystemControllerId));

        public static long NextTick(BattleSimulation sim) => sim.Tick + 1L;

        /// <summary>冻结当前应推进的 Tick 并发起 Step（驱动器的唯一调用序列）。</summary>
        public static StepResult StepNext(BattleSimulation sim)
        {
            long tick = NextTick(sim);
            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(tick);
            return sim.Step(tick, batch);
        }

        public static StepResult StepEmpty(BattleSimulation sim) => StepNext(sim);

        // —— 生产者可构造的请求（只有三样东西：目标 Tick / scope / payload）——

        public static CommandRequest ScheduleAdd(long targetTick, long expectedScheduleRevision = 0L, WindowId? window = null)
            => new CommandRequest(
                targetTick,
                new ScheduleEditScope(expectedScheduleRevision, window),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    new ScheduleAddOperation(new ActionPlanId(1L), targetTick)
                }));

        public static CommandRequest CloseWindow(long targetTick, WindowId window)
            => new CommandRequest(
                targetTick,
                new WindowCommandScope(window),
                new WindowCommandPayload(WindowCommandKind.CloseOwnWindow));

        public static CommandRequest ActivateConcurrent(long targetTick, WindowId window)
            => new CommandRequest(
                targetTick,
                new WindowCommandScope(window),
                new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction));

        public static CommandRequest Block(long targetTick, long opportunityId)
            => new CommandRequest(
                targetTick,
                new ReactionCommandScope(new ReactionOpportunityId(opportunityId)),
                new ReactionCommandPayload(ReactionCommandKind.Block, new ActionSpecId("action.block")));

        public static CommandRequest Dodge(long targetTick, long opportunityId, GridPoint destination)
            => new CommandRequest(
                targetTick,
                new ReactionCommandScope(new ReactionOpportunityId(opportunityId)),
                new ReactionCommandPayload(ReactionCommandKind.Dodge, new ActionSpecId("action.dodge"), destination));

        /// <summary>事件签名：类型 + Tick + Sequence + 关键字段的稳定文本（用于逐 Tick 比较）。</summary>
        public static string EventSignature(EventBatch batch)
        {
            var builder = new StringBuilder();
            builder.Append("tick=").Append(batch.Tick.ToString(CultureInfo.InvariantCulture)).Append('[');
            for (int i = 0; i < batch.Count; i++)
            {
                LogicEvent logicEvent = batch.Events[i];
                if (i > 0) builder.Append(',');
                builder.Append(logicEvent.GetType().Name).Append('#')
                       .Append(logicEvent.Sequence.ToString(CultureInfo.InvariantCulture));
                switch (logicEvent)
                {
                    case CommandIngressRejectedEvent rejected:
                        builder.Append('|').Append(rejected.ControllerId.Value).Append('|')
                               .Append(rejected.ProducerOrdinal.ToString(CultureInfo.InvariantCulture)).Append('|')
                               .Append(rejected.ReasonCode).Append('|')
                               .Append(rejected.CollisionCount.ToString(CultureInfo.InvariantCulture));
                        break;
                    case CommandRejectedEvent commandRejected:
                        builder.Append('|').Append(commandRejected.CommandSequence.ToString(CultureInfo.InvariantCulture))
                               .Append('|').Append(commandRejected.ReasonCode);
                        break;
                    case BattleEndedEvent ended:
                        builder.Append('|').Append(ended.ResultCode);
                        break;
                    case TurnWindowOpenedEvent opened:
                        builder.Append('|').Append(opened.WindowId.Value.ToString(CultureInfo.InvariantCulture))
                               .Append('|').Append(opened.OwnerUnitId.Value.ToString(CultureInfo.InvariantCulture));
                        break;
                    case UnitDiedEvent died:
                        builder.Append('|').Append(died.UnitId.Value.ToString(CultureInfo.InvariantCulture));
                        break;
                    case ForcedDisplacementResolvedEvent displaced:
                        builder.Append('|').Append(displaced.TargetUnitId.Value.ToString(CultureInfo.InvariantCulture))
                               .Append('|').Append(displaced.From.ToString()).Append("->").Append(displaced.To.ToString())
                               .Append('|').Append(displaced.StopReason.ToString());
                        break;
                }
            }
            builder.Append(']');
            return builder.ToString();
        }

        public static string SnapshotSignature(LogicSnapshot snapshot)
            => snapshot.ComputeHashHex() + "|units=" + UnitSignature(snapshot);

        public static string UnitSignature(LogicSnapshot snapshot)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < snapshot.Units.Count; i++)
            {
                UnitSnapshot unit = snapshot.Units[i];
                if (i > 0) builder.Append(';');
                builder.Append(unit.UnitId.ToString(CultureInfo.InvariantCulture)).Append(':')
                       .Append(unit.FactionId).Append(':')
                       .Append(unit.X.ToString(CultureInfo.InvariantCulture)).Append(',')
                       .Append(unit.Y.ToString(CultureInfo.InvariantCulture)).Append(':')
                       .Append(unit.HealthQ10.ToString(CultureInfo.InvariantCulture)).Append(':')
                       .Append(unit.IsAlive ? "alive" : "dead");
            }
            return builder.ToString();
        }

        public static UnitSnapshot UnitOf(LogicSnapshot snapshot, UnitId unitId)
        {
            for (int i = 0; i < snapshot.Units.Count; i++)
            {
                if (snapshot.Units[i].UnitId == unitId.Value) return snapshot.Units[i];
            }
            throw new InvalidOperationException("unit not found: " + unitId.Value.ToString(CultureInfo.InvariantCulture));
        }

        public static List<LogicEvent> EventsOfType<T>(EventBatch batch) where T : LogicEvent
        {
            var result = new List<LogicEvent>();
            for (int i = 0; i < batch.Count; i++)
            {
                if (batch.Events[i] is T) result.Add(batch.Events[i]);
            }
            return result;
        }

        public static byte[] Payload(params long[] values)
        {
            var encoder = new CanonicalEncoder(16);
            encoder.BeginDomain("test-payload");
            for (int i = 0; i < values.Length; i++) encoder.WriteInt64(values[i]);
            return encoder.ToArray();
        }

        public static HistoryArchiveCandidate Candidate(
            long tick, HistoryRecordKind kind, string stableKey, bool sealedRecord = true, params long[] payload)
            => new HistoryArchiveCandidate(
                tick, kind, stableKey, Payload(payload),
                sealedRecord ? HistorySealProof.Sealed : HistorySealProof.NotSealed("RESERVATIONS_REMAIN"));
    }

    // —— 阶段夹具（只通过公开扩展点接入既定阶段）——

    /// <summary>阶段 1 夹具：在指定 Tick 把指定单位的生命清零，并记录观察到的位置。</summary>
    internal sealed class KillOnTickAdvanceSystem : IUnitStateAdvanceSystem
    {
        public long KillTick = -1L;
        public long KillUnitId = -1L;
        public int KillHealthQ10;
        public readonly List<string> Observations = new List<string>();

        public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(IReadOnlyList<UnitSnapshot> units, long tick)
        {
            for (int i = 0; i < units.Count; i++)
            {
                UnitSnapshot unit = units[i];
                Observations.Add(tick + ":" + unit.UnitId + "@" + unit.X + "," + unit.Y + ":" + unit.HealthQ10);
            }

            if (tick != KillTick) return Array.Empty<UnitStateAdvanceRequest>();
            var requests = new List<UnitStateAdvanceRequest>();
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].UnitId == KillUnitId)
                    requests.Add(new UnitStateAdvanceRequest(KillUnitId, KillHealthQ10));
            }
            return requests;
        }
    }

    /// <summary>阶段 3/17 夹具：在指定 Tick 打开窗口，并在关闭请求时返回 true。</summary>
    internal sealed class ScriptedWindowSchedule : ITurnWindowSchedule
    {
        public long OpenTick = -1L;
        public long OwnerUnitId = -1L;
        public int BudgetTicks = 60;
        public long CloseTick = -1L;

        public WindowOpenRequest TryOpenDue(long tick)
            => tick == OpenTick ? new WindowOpenRequest(new UnitId(OwnerUnitId), BudgetTicks) : null;

        public bool ShouldCloseCurrentWindow(long tick) => tick == CloseTick;
    }

    /// <summary>阶段 11 夹具：返回固定的强制位移请求。</summary>
    internal sealed class ScriptedDisplacementRequests : IForcedDisplacementRequestBuilder
    {
        public long TargetUnitId = -1L;
        public int Steps = 2;
        public GridDirection Direction = GridDirection.East;
        public long MomentumUnits = 4L;
        public long ConflictGroupKey = 77L;

        public IReadOnlyList<ForcedDisplacementRequest> BuildOrdered(long tick, IReadOnlyList<UnitSnapshot> units)
            => TargetUnitId < 0
                ? Array.Empty<ForcedDisplacementRequest>()
                : new[] { new ForcedDisplacementRequest(TargetUnitId, Steps, Direction, MomentumUnits, ConflictGroupKey) };
    }

    /// <summary>
    /// 阶段 12 夹具：只读求解（记录入参位置，证明求解阶段不修改世界），返回确定性换位。
    /// </summary>
    internal sealed class ObservingDisplacementSolver : IForcedDisplacementSolver
    {
        public int AppliedSteps = 2;
        public int Dx = 2;
        public int Dy = 0;
        public readonly List<string> ObservedInputs = new List<string>();

        public ForcedDisplacementBatch ResolveAll(
            IReadOnlyList<ForcedDisplacementRequest> requests,
            IReadOnlyList<UnitSnapshot> immutableUnitSnapshot,
            GridBoundaryDefinition boundary,
            long tick)
        {
            for (int i = 0; i < immutableUnitSnapshot.Count; i++)
            {
                UnitSnapshot unit = immutableUnitSnapshot[i];
                ObservedInputs.Add(tick + ":" + unit.UnitId + "@" + unit.X + "," + unit.Y);
            }

            if (requests == null || requests.Count == 0) return ForcedDisplacementBatch.Empty;

            var relocations = new List<ForcedDisplacementRelocation>(requests.Count);
            for (int i = 0; i < requests.Count; i++)
            {
                ForcedDisplacementRequest request = requests[i];
                UnitSnapshot source = null;
                for (int u = 0; u < immutableUnitSnapshot.Count; u++)
                {
                    if (immutableUnitSnapshot[u].UnitId == request.TargetUnitId) source = immutableUnitSnapshot[u];
                }
                if (source == null) continue;

                int toX = source.X + Dx;
                int toY = source.Y + Dy;
                int applied = Math.Min(AppliedSteps, request.RequestedSteps);
                relocations.Add(new ForcedDisplacementRelocation(
                    request.TargetUnitId, source.X, source.Y, toX, toY, request.Direction,
                    request.RequestedSteps, applied, request.MomentumUnits, request.ConflictGroupKey,
                    applied == request.RequestedSteps
                        ? ForcedDisplacementStopReason.Completed
                        : ForcedDisplacementStopReason.Boundary,
                    Array.Empty<ActionPlanId>()));
            }

            return new ForcedDisplacementBatch(relocations, requests);
        }
    }

    /// <summary>阶段 14 夹具：记录状态/控制提交时看到的最终位置（证明批量换位已经发生）。</summary>
    internal sealed class ObservingResolutionCommit : IResolutionCommitSystem
    {
        public readonly List<string> DamageObservations = new List<string>();
        public readonly List<string> StateControlObservations = new List<string>();

        public void CommitDamageAndAggregationOrdered(long tick, IReadOnlyList<UnitSnapshot> units)
        {
            for (int i = 0; i < units.Count; i++)
                DamageObservations.Add(tick + ":" + units[i].UnitId + "@" + units[i].X + "," + units[i].Y);
        }

        public void CommitStateControlAndRemainingTerminalsOrdered(long tick, IReadOnlyList<UnitSnapshot> unitsAfterRelocation)
        {
            for (int i = 0; i < unitsAfterRelocation.Count; i++)
                StateControlObservations.Add(tick + ":" + unitsAfterRelocation[i].UnitId + "@" +
                                             unitsAfterRelocation[i].X + "," + unitsAfterRelocation[i].Y);
        }
    }

    /// <summary>阶段 19 夹具：记录只读检查时刻的快照 Tick（证明检查先于本 Tick 快照构建）。</summary>
    internal sealed class RecordingInvariantCheck : IStepInvariantCheck
    {
        public BattleSimulation Simulation;
        public readonly List<long> CheckedTicks = new List<long>();
        public readonly List<long> SnapshotTicksAtCheck = new List<long>();

        public string CheckOrdered(long tick, IReadOnlyList<UnitSnapshot> units)
        {
            CheckedTicks.Add(tick);
            SnapshotTicksAtCheck.Add(Simulation?.CurrentSnapshot?.Tick ?? -1L);
            return null;
        }
    }

    /// <summary>阶段 19 夹具：始终失败（用于证明失败时不归档）。</summary>
    internal sealed class FailingInvariantCheck : IStepInvariantCheck
    {
        public string CheckOrdered(long tick, IReadOnlyList<UnitSnapshot> units) => "fixture-violation";
    }

    /// <summary>阶段 19 夹具：每 Tick 产出固定数量的封条/未封条候选。</summary>
    internal sealed class FixtureArchiveSource : IHistoryArchiveCandidateSource
    {
        public int SealedPerTick = 1;
        public int UnsealedPerTick;
        public string KeyPrefix = "fixture.record";
        public HistoryRecordKind Kind = HistoryRecordKind.WindowLedger;

        public IReadOnlyList<HistoryArchiveCandidate> CollectOrdered(long tick)
        {
            var candidates = new List<HistoryArchiveCandidate>();
            for (int i = 0; i < SealedPerTick; i++)
            {
                candidates.Add(Task03.Candidate(tick, Kind, KeyPrefix + "." + tick + "." + i, true, tick, i));
            }
            for (int i = 0; i < UnsealedPerTick; i++)
            {
                candidates.Add(Task03.Candidate(tick, Kind, "unsealed." + tick + "." + i, false, tick, i));
            }
            return candidates;
        }
    }

    /// <summary>阶段 18 夹具：捕获 AI 与玩家共同读取的只读决策快照，并按契约投递下一 Tick 请求。</summary>
    internal sealed class RecordingDecisionObserver : IDecisionObserver
    {
        public DecisionSnapshot Captured;
        public int ObservedCount;
        public long SubmitTargetTick = -1L;
        public string SubmitControllerId;
        public bool SubmitOnce;

        public void ObserveOrdered(DecisionSnapshot snapshot, CommandIngressRegistry ingress, long nextTick)
        {
            Captured = snapshot;
            ObservedCount++;

            if (SubmitTargetTick != nextTick || string.IsNullOrEmpty(SubmitControllerId)) return;
            if (SubmitOnce && ObservedCount > 1) return;

            CommandIngressEntry entry = ingress.FindEntry(new ControllerId(SubmitControllerId));
            entry?.Submit(Task03.ScheduleAdd(nextTick));
        }
    }
}
