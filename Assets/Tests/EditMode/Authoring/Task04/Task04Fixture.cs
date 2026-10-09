using System.Collections.Generic;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Status;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Units;

namespace ProjectHero.Authoring.Tests.Task04
{
    /// <summary>
    /// 任务 04 测试夹具：复用任务 03 的<strong>真实</strong> 02B 定义与 Step 驱动，
    /// 并补上任务 04 需要的阶段参与者（状态转换、持续效果、死亡观察）。
    ///
    /// 不建立第二套配置模型：<see cref="ProjectHero.Authoring.Tests.Task03.Task03.Definition"/>
    /// 来自真实资产。类名带 <c>Farm</c> 后缀是为了不与测试命名空间
    /// <c>ProjectHero.Authoring.Tests.Task04</c> 同名（同名会让 <c>Task04.X</c> 解析成命名空间）。
    /// </summary>
    internal static class Task04Farm
    {
        public static BattleDefinition Definition
            => ProjectHero.Authoring.Tests.Task03.Task03.Definition;

        public static EncounterDefinition Encounter
            => ProjectHero.Authoring.Tests.Task03.Task03.Encounter;

        public static BattleRuntimeInputs Inputs
            => ProjectHero.Authoring.Tests.Task03.Task03.Inputs;

        public static EncounterDefinitionId EncounterId
            => ProjectHero.Authoring.Tests.Task03.Task03.EncounterId;

        /// <summary>主战斗场景按 SlotId 的 Ordinal 顺序：enemy=UnitId(1)、hero=UnitId(2)。</summary>
        public static UnitId EnemyUnitId
            => ProjectHero.Authoring.Tests.Task03.Task03.EnemyUnitId;

        public static UnitId HeroUnitId
            => ProjectHero.Authoring.Tests.Task03.Task03.HeroUnitId;

        public static BattleSimulation NewSim(BattleSimulationAssembly assembly = null)
            => ProjectHero.Authoring.Tests.Task03.Task03.NewSim(assembly);

        public static StepResult StepNext(BattleSimulation sim)
            => ProjectHero.Authoring.Tests.Task03.Task03.StepNext(sim);

        public static StepResult StepEmpty(BattleSimulation sim)
            => ProjectHero.Authoring.Tests.Task03.Task03.StepEmpty(sim);

        public static ProjectHero.Logic.Commands.CommandIngressEntry PlayerEntry(BattleSimulation sim)
            => ProjectHero.Authoring.Tests.Task03.Task03.PlayerEntry(sim);

        public static UnitSnapshot UnitOf(LogicSnapshot snapshot, UnitId unitId)
            => ProjectHero.Authoring.Tests.Task03.Task03.UnitOf(snapshot, unitId);

        public static List<T> EventsOfType<T>(EventBatch batch) where T : LogicEvent
            => ProjectHero.Authoring.Tests.Task03.Task03.EventsOfType<T>(batch).ConvertAll(e => (T)e);

        /// <summary>
        /// 真实定义的阵营关系解析器（只读）。用于证明"目标组划分不改写关系矩阵"。
        /// </summary>
        public static IFactionRelationResolver SimulationFactionResolver()
            => NewSim().FactionResolver;
    }

    /// <summary>
    /// 修订轮夹具（R2 / R3）：在<strong>真实 02B 定义</strong>上派生一个
    /// <strong>三槽位 Encounter</strong>，让"目标外阵营"第一次成为<strong>真实运行时单位</strong>
    /// （而不是合成快照字面量里的字符串）。
    ///
    /// 关键事实（三条，全部来自真实配置与真实初始化器）：
    /// <list type="bullet">
    /// <item>第三槽位的阵营 <c>outsider</c> 被<strong>注册进</strong>
    /// <see cref="FactionModelDefinition"/> 并给出显式关系（与 hero、与 monster 各一条
    /// <see cref="FactionDisposition.Hostile"/>，上三角共 3 条 = 3*2/2，缺项不许补洞），
    /// 因此 <c>FactionRelationResolver.Classify</c> 在结构上<strong>可以</strong>对它分类。</item>
    /// <item><c>outsider</c> <strong>不</strong>出现在 <c>VictoryDefinition</c> 的
    /// Allied/Hostile 任一目标组里，因此它是真正的"目标外阵营"。</item>
    /// <item>目标组本身<strong>逐字未改</strong>（仍是 hero vs monster）：这样"目标外阵营存活
    /// ⇒ 消灭条件不成立 ⇒ 战斗继续"可以在一场<strong>多 Tick、多单位</strong>的真实战斗里被观察到。</item>
    /// </list>
    ///
    /// UnitId 分配仍由 <c>BattleInitializer</c> 按 SlotId 的 Ordinal 升序给出：
    /// <c>enemy</c>=1、<c>hero</c>=2、<c>outsider</c>=3（与主场景前两个单位完全一致）。
    /// </summary>
    internal static class Task04OutsiderVariant
    {
        /// <summary>目标外阵营的稳定 FactionId（只在本变体里注册）。</summary>
        public const string OutsiderFactionId = "outsider";

        /// <summary>目标外阵营单位的槽位 ID（Ordinal 排在 hero/enemy 之后 ⇒ UnitId(3)）。</summary>
        public const string OutsiderSlotId = "outsider";

        /// <summary>目标外阵营单位的运行时 UnitId（由真实初始化器的 Ordinal 顺序决定）。</summary>
        public static UnitId OutsiderUnitId => new UnitId(3);

        /// <summary>在真实定义上注册 <c>outsider</c>：3 个阵营 ⇒ 恰好 3 条上三角关系，无缺项。</summary>
        public static FactionModelDefinition FactionModelWithOutsider()
        {
            FactionModelDefinition original = Task04Farm.Definition.FactionModel;
            var factions = new List<FactionDefinition>(original.Factions)
            {
                new FactionDefinition(new FactionId(OutsiderFactionId))
            };

            var relations = new List<FactionRelationDefinition>(original.Relations)
            {
                new FactionRelationDefinition(
                    FactionIds.Hero, new FactionId(OutsiderFactionId), FactionDisposition.Hostile),
                new FactionRelationDefinition(
                    FactionIds.Monster, new FactionId(OutsiderFactionId), FactionDisposition.Hostile)
            };

            return new FactionModelDefinition(factions, relations);
        }

        /// <summary>
        /// 主 Encounter 的三槽位变体：目标外阵营的第三槽位 + 逐字未改的
        /// <c>VictoryDefinition</c>（Allied = hero、Hostile = monster）与逐字未改的
        /// <c>ControllerBinding</c>（它只控制 hero/enemy 两个槽位，目标外单位不被任何外部入口控制）。
        /// </summary>
        public static EncounterDefinition EncounterWithOutsider()
        {
            EncounterDefinition original = Task04Farm.Encounter;
            var slots = new List<EncounterUnitSlot>(original.Slots)
            {
                new EncounterUnitSlot(
                    new EncounterSlotId(OutsiderSlotId),
                    new UnitDefinitionId(LegacyIdMigrationManifest.UnitHeroRadius1),
                    new FactionId(OutsiderFactionId),
                    // GridPoint 必须满足"包围盒内 + X+Y 为偶"（与 hero(-5,-5)/enemy(5,5) 同族）。
                    new GridPoint(-5, 5),
                    GridDirection.East)
            };

            return original with { Slots = slots };
        }

        /// <summary>完整变体定义：只替换 FactionModel 与 Encounters（EncounterId 保持不变）。</summary>
        public static BattleDefinition DefinitionWithOutsider()
            => Task04Farm.Definition with
            {
                FactionModel = FactionModelWithOutsider(),
                Encounters = new List<EncounterDefinition> { EncounterWithOutsider() }
            };

        /// <summary>真实初始化器的结果（用于断言 UnitId 分配与阵营映射）。</summary>
        public static BattleInitializationResult Initialize()
            => BattleInitializer.BuildInitialState(
                DefinitionWithOutsider(), Task04Farm.EncounterId, Task04Farm.Inputs);

        /// <summary>
        /// 用变体定义创建一场真实模拟。走任务 03 的同一条公开创建入口
        /// （<c>BattleSimulation.Create</c>），不建立第二套模拟入口。
        /// </summary>
        public static BattleSimulation NewSim(BattleSimulationAssembly assembly = null)
            => BattleSimulation.Create(
                DefinitionWithOutsider(), Task04Farm.EncounterId, Task04Farm.Inputs,
                assembly ?? BattleSimulationAssembly.Standard());
    }

    /// <summary>
    /// 阶段 1 夹具：在指定 Tick 对指定单位声明一次<strong>显式状态转换</strong>
    /// （走状态机统一入口，与自动到期同一路径）。
    /// </summary>
    internal sealed class ControlTransitionAtTick : IUnitStateAdvanceSystem
    {
        public long Tick = -1L;
        public long UnitId = -1L;
        public StateTransitionSpec Transition;

        public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(
            IReadOnlyList<UnitSnapshot> units, long tick)
            => tick == Tick && UnitId >= 0 && Transition != null
                ? new[] { new UnitStateAdvanceRequest(UnitId, null, Transition, null) }
                : System.Array.Empty<UnitStateAdvanceRequest>();
    }

    /// <summary>
    /// 阶段 1 夹具：在指定 Tick 对指定单位施加一个持续效果（只声明，不直接改集合）。
    ///
    /// 持续时间是<strong>半开区间</strong> <c>[Applied, Applied + Duration)</c>：
    /// 因此 <c>DurationTicks = 1</c> 的效果<strong>一次都不会触发</strong>（在该 Tick 立即到期移除）。
    /// </summary>
    internal sealed class ApplyEffectAtTick : IUnitStateAdvanceSystem
    {
        public long Tick = -1L;
        public long UnitId = -1L;
        public StatusEffectRuntimeSpec Spec;

        public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(
            IReadOnlyList<UnitSnapshot> units, long tick)
            => tick == Tick && UnitId >= 0 && Spec != null
                ? new[] { new UnitStateAdvanceRequest(UnitId, null, null, Spec) }
                : System.Array.Empty<UnitStateAdvanceRequest>();
    }

    /// <summary>
    /// 阶段 1 夹具：在指定 Tick 施加一个<strong>计时伤害</strong>效果。
    /// 效果从 <c>ApplyTick + 1</c> 起按半开区间逐 Tick 触发。
    /// </summary>
    internal sealed class DamageOverTimeApplyAtTick : IUnitStateAdvanceSystem
    {
        public long ApplyTick = -1L;
        public long UnitId = -1L;
        public int DurationTicks = 2;
        public int DamagePerTickQ10 = 1;
        public string SpecId = "status.test_dot";

        public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(
            IReadOnlyList<UnitSnapshot> units, long tick)
        {
            if (tick != ApplyTick || UnitId < 0) return System.Array.Empty<UnitStateAdvanceRequest>();

            var spec = new StatusEffectRuntimeSpec(
                new StatusEffectSpecId(SpecId),
                DurationTicks,
                new DamageOverTimeEffectPayload(DamagePerTickQ10));
            return new[] { new UnitStateAdvanceRequest(UnitId, null, null, spec) };
        }
    }

    /// <summary>阶段 1 夹具：在指定 Tick 把指定单位的生命清零（批量、顺序由夹具显式给出）。</summary>
    internal sealed class KillBatchAtTick : IUnitStateAdvanceSystem
    {
        public long Tick = -1L;
        public long[] Kills = System.Array.Empty<long>();

        public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(
            IReadOnlyList<UnitSnapshot> units, long tick)
        {
            if (tick != Tick || Kills.Length == 0) return System.Array.Empty<UnitStateAdvanceRequest>();
            var requests = new List<UnitStateAdvanceRequest>(Kills.Length);
            for (int i = 0; i < Kills.Length; i++)
            {
                // 显式给出全部四个实参：声明一次"只写生命、不改状态、不加效果"的请求。
                requests.Add(new UnitStateAdvanceRequest(Kills[i], (int?)0, null, null));
            }
            return requests;
        }
    }

    /// <summary>阶段 1 夹具：把多个子系统的声明合并（顺序固定为装配顺序）。</summary>
    internal sealed class CompositeAdvanceSystem : IUnitStateAdvanceSystem
    {
        private readonly IUnitStateAdvanceSystem[] _parts;

        public CompositeAdvanceSystem(params IUnitStateAdvanceSystem[] parts)
        {
            _parts = parts ?? System.Array.Empty<IUnitStateAdvanceSystem>();
        }

        public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(
            IReadOnlyList<UnitSnapshot> units, long tick)
        {
            var all = new List<UnitStateAdvanceRequest>();
            for (int i = 0; i < _parts.Length; i++)
            {
                IReadOnlyList<UnitStateAdvanceRequest> part = _parts[i]?.AdvanceOrdered(units, tick);
                if (part == null) continue;
                for (int r = 0; r < part.Count; r++) all.Add(part[r]);
            }
            return all;
        }
    }

    /// <summary>
    /// 生命周期通知接收点夹具：记录死亡系统发出的<strong>一次性</strong>清理通知顺序。
    /// 它只读取事实，不写任何逻辑状态（阶段 2/15 内被调用）。
    /// </summary>
    internal sealed class RecordingLifecycleSink : IUnitLifecycleNoticeSink
    {
        public readonly List<UnitLifecycleCleanupNotice> Notices =
            new List<UnitLifecycleCleanupNotice>();

        public void OnUnitLifecycleNoticeOrdered(UnitLifecycleCleanupNotice notice)
        {
            if (notice != null) Notices.Add(notice);
        }
    }

    /// <summary>在真实伤害提交阶段安排致死，区别于命令前状态/持续效果致死。</summary>
    internal sealed class KillDuringResolution : IResolutionCommitSystem
    {
        public long Tick;
        public long[] Kills;
        public IResolutionDamageApplier DamageApplier;

        public void CommitDamageAndAggregationOrdered(long tick, IReadOnlyList<UnitSnapshot> units)
        {
            if (tick != Tick) return;
            foreach (long id in Kills)
                foreach (UnitSnapshot unit in units)
                    if (unit.UnitId == id) DamageApplier.ApplyDamageQ10(new UnitId(id), unit.HealthQ10);
        }

        public void CommitStateControlAndRemainingTerminalsOrdered(long tick, IReadOnlyList<UnitSnapshot> units) { }
    }

    /// <summary>
    /// 阶段 11 夹具：为单个单位产生一次强制位移请求（带只读诊断计数）。
    /// </summary>
    internal sealed class DisplacementRequestsForTask04 : IForcedDisplacementRequestBuilder
    {
        public long TargetUnitId = -1L;
        public int Steps = 1;
        public GridDirection Direction = GridDirection.East;
        public long MomentumUnits = 5L;
        public long ConflictGroupKey = 1L;

        /// <summary>诊断计数（只读观察，不影响逻辑）。</summary>
        public int BuildCalls { get; private set; }

        public IReadOnlyList<ForcedDisplacementRequest> BuildOrdered(
            long tick, IReadOnlyList<UnitSnapshot> units)
        {
            BuildCalls++;
            if (TargetUnitId < 0) return System.Array.Empty<ForcedDisplacementRequest>();
            return new[]
            {
                new ForcedDisplacementRequest(
                    new UnitId(TargetUnitId), Direction, Steps, MomentumUnits, ConflictGroupKey)
            };
        }
    }

    /// <summary>阶段 12 夹具：按固定位移量返回换位，并记录只读求解入参。</summary>
    internal sealed class DisplacementSolverForTask04 : IForcedDisplacementSolver
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

            var resolutions = new List<ForcedDisplacementResolution>(requests.Count);
            for (int i = 0; i < requests.Count; i++)
            {
                ForcedDisplacementRequest request = requests[i];
                UnitSnapshot source = null;
                for (int u = 0; u < immutableUnitSnapshot.Count; u++)
                {
                    if (immutableUnitSnapshot[u].UnitId == request.TargetUnitId.Value)
                        source = immutableUnitSnapshot[u];
                }
                if (source == null) continue;

                int applied = System.Math.Min(AppliedSteps, request.RequestedSteps);
                resolutions.Add(new ForcedDisplacementResolution(
                    request.TargetUnitId,
                    new GridPoint(source.X, source.Y), new GridPoint(source.X + Dx, source.Y + Dy),
                    request.RequestedSteps, applied,
                    applied == request.RequestedSteps
                        ? ForcedDisplacementStopReason.Completed
                        : ForcedDisplacementStopReason.Boundary,
                    System.Array.Empty<ActionPlanId>()));
            }

            return ForcedDisplacementBatch.FromResolutions(resolutions);
        }
    }

    /// <summary>
    /// 阶段 12 夹具：观察求解输入是否仍然包含"本 Tick 致死的单位"，
    /// 并在它仍存活时把它当作静止阻挡，返回 AppliedSteps = 0 的换位。
    /// </summary>
    internal sealed class BlockingDependencyProbeSolver : IForcedDisplacementSolver
    {
        public long BlockerUnitId = -1L;
        public long FollowerUnitId = -1L;
        public int Steps = 1;
        public bool SawLethalUnitAlive;
        public bool SawLethalUnitAsObstacle;

        public ForcedDisplacementBatch ResolveAll(
            IReadOnlyList<ForcedDisplacementRequest> requests,
            IReadOnlyList<UnitSnapshot> immutableUnitSnapshot,
            GridBoundaryDefinition boundary,
            long tick)
        {
            UnitSnapshot blocker = null;
            UnitSnapshot follower = null;
            for (int i = 0; i < immutableUnitSnapshot.Count; i++)
            {
                UnitSnapshot unit = immutableUnitSnapshot[i];
                if (unit.UnitId == BlockerUnitId) blocker = unit;
                if (unit.UnitId == FollowerUnitId) follower = unit;
            }

            // 关键观察：求解阶段看到的致死单位仍然是 IsAlive（尚未进入 Dead）。
            if (blocker != null && blocker.IsAlive) SawLethalUnitAlive = true;


            if (requests == null || requests.Count == 0 || follower == null)
                return ForcedDisplacementBatch.Empty;

            var resolutions = new List<ForcedDisplacementResolution>(requests.Count);
            for (int i = 0; i < requests.Count; i++)
            {
                ForcedDisplacementRequest request = requests[i];
                UnitSnapshot source = request.TargetUnitId.Value == FollowerUnitId ? follower : null;
                if (source == null) continue;

                bool blocked = blocker != null && blocker.IsAlive;
                if (blocked) SawLethalUnitAsObstacle = true;

                // 被阻挡时应用 0 步：目的地等于起点，批次验证因此可以通过。
                int applied = blocked ? 0 : System.Math.Min(Steps, request.RequestedSteps);

                resolutions.Add(new ForcedDisplacementResolution(
                    request.TargetUnitId,
                    new GridPoint(source.X, source.Y), new GridPoint(source.X, source.Y),
                    request.RequestedSteps, applied,
                    blocked ? ForcedDisplacementStopReason.StaticObstacle
                            : ForcedDisplacementStopReason.Completed,
                    System.Array.Empty<ActionPlanId>()));
            }

            return ForcedDisplacementBatch.FromResolutions(resolutions);
        }
    }

    /// <summary>阶段 3/17 夹具：在指定 Tick 打开窗口、在指定 Tick 请求关闭。</summary>
    internal sealed class WindowScheduleForTask04 : ITurnWindowSchedule
    {
        public long OpenTick = -1L;
        public long OwnerUnitId = -1L;
        public int BudgetTicks = 60;
        public long CloseTick = -1L;

        public WindowOpenRequest TryOpenDue(long tick)
            => tick == OpenTick ? new WindowOpenRequest(new UnitId(OwnerUnitId), BudgetTicks) : null;

        public bool ShouldCloseCurrentWindow(long tick) => tick == CloseTick;
    }

    /// <summary>胜负评估夹具：统计调用与返回结果码，但导出到生产评估器（不复制规则）。</summary>
    internal sealed class CountingVictoryEvaluator : IVictoryEvaluator
    {
        private readonly FactionEliminationVictoryEvaluator _inner = FactionEliminationVictoryEvaluator.Instance;

        public int Calls { get; private set; }

        public string LastResultCode { get; private set; }

        public readonly List<string> DistinctResultCodes = new List<string>();

        public string Evaluate(IReadOnlyList<UnitSnapshot> units, VictoryDefinition victory, long tick)
        {
            Calls++;
            string result = _inner.Evaluate(units, victory, tick);
            LastResultCode = result;
            if (result != null && !DistinctResultCodes.Contains(result)) DistinctResultCodes.Add(result);
            return result;
        }
    }

    /// <summary>
    /// <strong>[04-FROZEN-STAGE2-COMMIT]</strong>（任务 04 修订轮 R1 冻结结论；以代码真实行为为唯一权威；
    /// 任务 05 的阶段 2 完成语义必须依赖这四条）：
    /// <list type="number">
    /// <item>死亡提交只有一个实现（<c>BattleSimulation.ProcessDeaths</c>）：<c>IsAlive = false</c> 与
    /// <c>DeathProcessed = true</c> 在同一处成对写入（恒有 <c>DeathProcessed == !IsAlive</c>），
    /// 不存在 "<c>DeathProcessed == true</c> 且 <c>IsAlive == true</c>" 的中间状态。</item>
    /// <item>阶段 2（<c>DeathAndVictory</c>）在命令前判定为假（战斗继续）时<strong>完全不提交</strong>
    /// 任何死亡事实。</item>
    /// <item>阶段 2 判定为真（战斗已决定）时先按与阶段 15 完全相同的语义<strong>完整提交</strong>死亡，
    /// 再设结果码、拒绝已冻结批次并直接进入唯一 Finalizer：<strong>阶段 3–17 全部不执行</strong>。</item>
    /// <item>提交顺序固定为：死亡事实 → 结果码 → 冻结批次拒绝 → Finalizer 的
    /// <c>BattleEndedEvent</c>；因此<strong>死亡事件先于结束事件</strong>，"致死单位仍参与同 Tick
    /// 阶段 11–13 强制位移"仅在命令前判定为假时成立。</item>
    /// </list>
    /// <para>
    /// 三处逐字一致：本注释、<c>BattleSimulation</c> 类文档注释、
    /// <c>优化任务/执行记录/04-交接记录.md</c> §3.3（修订轮 R1 已消除旧文档中
    /// "阶段 2 置 <c>DeathProcessed</c> 但 <c>IsAlive</c> 仍为 <c>true</c>"的错误表述）。
    /// </para>
    /// </summary>
    /// <summary>
    /// 胜负评估夹具：在指定 Tick <strong>暂缓</strong>胜负判定（返回 null = 战斗继续），
    /// 其余 Tick 仍导出到生产评估器。
    ///
    /// 用途：验证"阶段 1 伤害 → 阶段 11 强制位移 → 阶段 13 批量换位 → 阶段 15 死亡"这条
    /// 同 Tick 顺序。<strong>如果阶段 2 的直接判据就把战斗结束掉，后面的位移阶段永远不执行</strong>
    /// （阶段 3–17 都不跑），那么"致死单位仍参与同时位移求解"这条断言就无法被真正检验。
    /// 本夹具同时实现 <see cref="IPreCommandVictoryGate"/>，把<strong>命令前判定</strong>
    /// 也一并推迟，因此只影响"何时宣布结果"，不改变死亡时机与位移阶段。
    /// </summary>
    internal sealed class DeferredVictoryEvaluator : IVictoryEvaluator, IPreCommandVictoryGate
    {
        private readonly FactionEliminationVictoryEvaluator _inner = FactionEliminationVictoryEvaluator.Instance;

        /// <summary>在这些 Tick 上暂缓判定（战斗继续）。</summary>
        public readonly List<long> DeferredTicks = new List<long>();

        public int Calls { get; private set; }

        public int DeferredCalls { get; private set; }

        /// <summary>命令前判定扩展点被调用的次数（阶段 2）。</summary>
        public int PreCommandGateCalls { get; private set; }

        public string Evaluate(IReadOnlyList<UnitSnapshot> units, VictoryDefinition victory, long tick)
        {
            Calls++;
            if (DeferredTicks.Contains(tick))
            {
                DeferredCalls++;
                return null;
            }
            return _inner.Evaluate(units, victory, tick);
        }

        /// <summary>
        /// 阶段 2 的扩展点：一律返回 null（本夹具不复制配置判据，交由用例用 DeferredTicks
        /// 控制何时进入阶段 16 的真实判定）。它的存在让用例可以把命令前判定整体推迟一格，
        /// 从而观察到同一 Tick 内阶段 11–15 的完整顺序。
        /// </summary>
        public string EvaluatePreCommandResult()
        {
            PreCommandGateCalls++;
            return null;
        }
    }

    /// <summary>
    /// 阶段 2 探针：在"命令前门禁"这一<strong>最早的门禁点</strong>上记录指定单位的状态事实
    /// （当前状态 + 权威阻塞边界），并一律返回 null（不干预胜负）。
    ///
    /// 用途：证明 Tick T 的状态自动到期<strong>已经</strong>发生（阶段 1 在阶段 2 之前），
    /// 因此门禁看到的是到期后的状态与已消失的阻塞边界，而不是"仍在阻塞中 / 下一 Tick 再试"。
    ///
    /// 记录的是<strong>状态事实</strong>而非 Tick：<c>BattleSimulation.Tick</c> 是"最近一个
    /// <strong>已完成</strong>的 Tick"，Step 执行期间尚未推进到本 Tick，因此门禁内部不能拿它当
    /// 当前 Tick 用（用例按调用次序断言，第 n 次调用即第 n 个 Step）。
    /// </summary>
    internal sealed class StartGateStateProbe : IPreCommandVictoryGate
    {
        public BattleSimulation Simulation;
        public long UnitId = -1L;

        /// <summary>每次门禁调用一条 <c>State/BlockingUntilTick</c> 事实（按调用顺序）。</summary>
        public readonly List<string> Observations = new List<string>();

        public string EvaluatePreCommandResult()
        {
            if (Simulation == null || UnitId < 0L) return null;

            UnitStateMachine machine = Simulation.FindUnitStateMachine(new UnitId(UnitId));
            Observations.Add(machine == null
                ? "<no-machine>"
                : machine.CurrentState + "/" + (machine.BlockingUntilTick.HasValue
                    ? machine.BlockingUntilTick.Value.ToString()
                    : "null"));
            return null;
        }
    }
}
