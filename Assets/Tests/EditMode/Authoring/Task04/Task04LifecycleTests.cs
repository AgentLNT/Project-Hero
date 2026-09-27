using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Status;
using ProjectHero.Logic.Units;

namespace ProjectHero.Authoring.Tests.Task04
{
    /// <summary>
    /// 任务 04 必需测试（模拟层）：死亡系统、阶段顺序、胜负矩阵与最终结果路由。
    ///
    /// 全部走任务 03 的<strong>真实 Step 管线</strong>与任务 02B 的<strong>真实定义</strong>；
    /// 只有明确标注"纯评估器"的用例直接调用
    /// <see cref="FactionEliminationVictoryEvaluator"/>（它们验证的是规则矩阵本身）。
    /// </summary>
    public class Task04LifecycleTests
    {
        private const string VictoryCode = "RESULT_VICTORY";
        private const string DefeatCode = "RESULT_DEFEAT";
        private const string DrawCode = "RESULT_DRAW";

        // =====================================================================
        // 死亡系统
        // =====================================================================

        /// <summary>
        /// 死亡通知必须按 <c>UnitId</c> 升序，且每个单位恰好一条。
        /// 同 Tick 双杀是最强的顺序证据：如果实现按容器顺序或按发现顺序处理，
        /// 这个用例会在不同装配下抖动。
        /// </summary>
        [Test]
        public void DeathNotifiesLifecycleCleanupHooksInUnitIdOrder()
        {
            var sink = new RecordingLifecycleSink();
            var killer = new KillBatchAtTick
            {
                Tick = 0L,
                Kills = new[] { Task04Farm.HeroUnitId.Value, Task04Farm.EnemyUnitId.Value }
            };

            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: killer,
                lifecycleNoticeSink: sink));

            StepResult ended = Task04Farm.StepNext(sim);

            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded));
            Assert.That(sim.LifecycleNotices.Count, Is.EqualTo(2), "两个单位各一条一次性通知");
            Assert.That(sim.LifecycleNotices[0].UnitId.Value, Is.EqualTo(Task04Farm.EnemyUnitId.Value),
                "通知必须按 UnitId 升序（enemy=1 先于 hero=2）");
            Assert.That(sim.LifecycleNotices[1].UnitId.Value, Is.EqualTo(Task04Farm.HeroUnitId.Value));
            Assert.That(sink.Notices.Count, Is.EqualTo(2), "接收点必须收到同一份一次性事实");
            Assert.That(sink.Notices[0].UnitId.Value, Is.EqualTo(Task04Farm.EnemyUnitId.Value));
            Assert.That(sink.Notices[1].UnitId.Value, Is.EqualTo(Task04Farm.HeroUnitId.Value));

            for (int i = 0; i < sim.LifecycleNotices.Count; i++)
            {
                Assert.That(sim.LifecycleNotices[i].ReasonCode,
                    Is.EqualTo(UnitLifecycleNoticeReasons.Death));
                Assert.That(sim.LifecycleNotices[i].Tick, Is.EqualTo(0L));
            }

            // 死亡事件的顺序必须与通知一致。
            var died = Task04Farm.EventsOfType<UnitDiedEvent>(ended.Events);
            Assert.That(died.Count, Is.EqualTo(2));
            Assert.That(died[0].UnitId.Value, Is.EqualTo(Task04Farm.EnemyUnitId.Value));
            Assert.That(died[1].UnitId.Value, Is.EqualTo(Task04Farm.HeroUnitId.Value));

            // 最终 footprint 移除顺序同样是 UnitId 升序。
            Assert.That(sim.LastDeathFootprintRemovals.Select(u => u.Value).ToArray(),
                Is.EqualTo(new[] { Task04Farm.EnemyUnitId.Value, Task04Farm.HeroUnitId.Value }));
        }

        /// <summary>
        /// 重复死亡处理不得重复发通知、重复发死亡事件或重复消耗状态转换。
        ///
        /// <strong>修订轮（R2，逐条证据见 04-交接记录 §13）：本用例过去根本没有重入
        /// 死亡阶段</strong>——它在 Tick 1 消灭 hostile 侧后战斗立即结束，
        /// Tick 2 的 <c>Step</c> 在入口 <c>AlreadyEnded</c> 短路处返回，
        /// <c>ProcessDeaths</c> 从未被第二次调用；它实际验证的只是"结束后的 Step 是稳定空操作"。
        ///
        /// 现在用两个各自必要的手段把它变成<strong>真正的重入</strong>：
        /// <list type="number">
        /// <item><strong>三单位场景</strong>（<see cref="Task04OutsiderVariant"/>）：真实 02B 定义 +
        /// 第三槽位 <c>outsider</c> 注册进关系矩阵但不列入胜负目标组，
        /// 让"单位死亡而战斗尚未结束"成为可能，并让 Tick 2 是一次<strong>三单位</strong>的完整 Step。</item>
        /// <item><strong>阶段 2 判定推迟</strong>（<see cref="DeferredVictoryEvaluator"/>，
        /// 任务 04 既有的阶段扩展点夹具）：<strong>实测发现</strong>仅靠目标外阵营不足以让战斗继续——
        /// 阶段 2 的门禁判据是"某一侧已无<strong>战斗效力</strong>单位"，消灭 hostile 目标组后
        /// 该判据仍然成立并直接结束战斗（首次运行本用例即因此得到 <c>BattleEnded</c>）。
        /// 因此本用例显式推迟 Tick 1/2 的胜负宣布（只影响"何时宣布结果"，
        /// 不改变死亡时机、不改变位移阶段），这才使阶段 15 与阶段 16 在 Tick 2 真实执行。</item>
        /// </list>
        ///
        /// 断言的可证伪性：Tick 2 的 <c>ProcessDeaths</c> 至少被调用一次（阶段 15 已执行），
        /// 此时已提交死亡的单位必须完全无副作用——只要有人在重入时重复移除 footprint、
        /// 重复发通知/事件或重复分配身份，本用例立刻失败。
        /// </summary>
        [Test]
        public void RepeatedDeathProcessingDoesNotDuplicateLifecycleNotice()
        {
            var sink = new RecordingLifecycleSink();
            var killer = new KillBatchAtTick
            {
                Tick = 1L,
                Kills = new[] { Task04Farm.EnemyUnitId.Value }
            };
            // 只推迟 Tick 1/2 的胜负宣布；阶段 2 与阶段 16 共用该夹具。
            var gate = new DeferredVictoryEvaluator { DeferredTicks = { 1L, 2L } };
            var sim = Task04OutsiderVariant.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: killer,
                victoryEvaluator: gate,
                lifecycleNoticeSink: sink));

            // 场景自证：三个真实运行时单位，第三个属于目标外阵营。
            Assert.That(sim.CurrentSnapshot.Units.Count, Is.EqualTo(3),
                "变体场景必须有三个真实单位（enemy=1 / hero=2 / outsider=3）：" + DumpUnits(sim));
            Assert.That(Task04Farm.UnitOf(sim.CurrentSnapshot, Task04OutsiderVariant.OutsiderUnitId).FactionId,
                Is.EqualTo(Task04OutsiderVariant.OutsiderFactionId), DumpUnits(sim));

            StepResult zeroth = Task04Farm.StepNext(sim);
            Assert.That(zeroth.Status, Is.EqualTo(StepStatus.Advanced), DumpUnits(sim));
            Assert.That(sim.LifecycleNotices, Is.Empty, "Tick 0 未致死：不得有通知");
            Assert.That(sim.LastDeathFootprintRemovals, Is.Empty);

            StepResult first = Task04Farm.StepNext(sim);
            Assert.That(first.Status, Is.EqualTo(StepStatus.Advanced),
                "Tick 1 的胜负宣布被显式推迟 ⇒ 战斗继续（这正是本用例能真正重入的前提）；"
                + DumpUnits(sim));
            Assert.That(gate.DeferredCalls, Is.GreaterThan(0), "推迟确实发生过（避免本用例落在假路径上）");
            Assert.That(sim.IsEnded, Is.False);
            Assert.That(sim.LifecycleNotices.Count, Is.EqualTo(1), "Tick 1 致死：恰好一条通知");
            Assert.That(sink.Notices.Count, Is.EqualTo(1));
            Assert.That(sim.LastDeathFootprintRemovals.Count, Is.EqualTo(1),
                "本 Tick 恰好移除一个最终 footprint");
            Assert.That(Task04Farm.EventsOfType<UnitDiedEvent>(first.Events).Count, Is.EqualTo(1));
            Assert.That(Task04Farm.EventsOfType<UnitStateChangedEvent>(first.Events)
                .Count(e => e.ToState == UnitState.Dead), Is.EqualTo(1),
                "阶段 2 与阶段 15 不得各发一次 Dead 状态转换事件");
            long nextEffectIdAfterFirst = first.Snapshot.NextEffectId;
            UnitLifecycleCleanupNotice onlyNotice = sim.LifecycleNotices[0];
            Assert.That(onlyNotice.UnitId.Value, Is.EqualTo(Task04Farm.EnemyUnitId.Value));
            Assert.That(onlyNotice.Tick, Is.EqualTo(1L));

            // —— 真正的重入：战斗继续，Tick 2 必须再跑一次完整死亡阶段 ——
            StepResult second = Task04Farm.StepNext(sim);
            Assert.That(second.Status, Is.EqualTo(StepStatus.Advanced),
                "没有单位在本 Tick 致死 ⇒ 战斗仍未结束（重入确实发生了）；" + DumpUnits(sim));
            Assert.That(second.Snapshot.Tick, Is.EqualTo(2L));
            Assert.That(sim.LastStepExecuted(StepPhase.PostDisplacementDeath), Is.True,
                "阶段 15 在本 Tick 真实执行（不是入口短路后的空结果）");
            Assert.That(gate.PreCommandGateCalls, Is.GreaterThan(0),
                "阶段 2 的门禁在本 Tick 被真实调用过（死亡提交前的判据点）");

            Assert.That(sim.LifecycleNotices.Count, Is.EqualTo(1), "每个单位整场恰好一条通知");
            Assert.That(sink.Notices.Count, Is.EqualTo(1), "一次性通知不因重复处理而重发");
            Assert.That(sim.LifecycleNotices[0], Is.EqualTo(onlyNotice),
                "重入不得改写既有通知（Tick/位置/原因码全部不变）");
            Assert.That(sim.LastDeathFootprintRemovals, Is.Empty,
                "本 Tick 没有可致死的单位 ⇒ 不得重复移除上一 Tick 的 footprint");
            Assert.That(Task04Farm.EventsOfType<UnitDiedEvent>(second.Events), Is.Empty,
                "已提交死亡的单位不得再发一次 UnitDiedEvent");
            Assert.That(Task04Farm.EventsOfType<UnitStateChangedEvent>(second.Events), Is.Empty,
                "已处于终态的单位不得再消耗一次状态转换");
            Assert.That(second.Snapshot.NextEffectId, Is.EqualTo(nextEffectIdAfterFirst),
                "死亡处理不得分配新的 EffectId");
            Assert.That(second.Snapshot.NextEventSequence,
                Is.GreaterThanOrEqualTo(first.Snapshot.NextEventSequence),
                "事件序号只允许单调前进，重入不得回卷或复用");
            Assert.That(Task04Farm.UnitOf(second.Snapshot, Task04Farm.EnemyUnitId).IsAlive, Is.False);
            Assert.That(Task04Farm.UnitOf(second.Snapshot, Task04Farm.EnemyUnitId).State,
                Is.EqualTo((int)UnitState.Dead));
        }

        /// <summary>
        /// 同一 Tick 内对<strong>同一已提交死亡的单位</strong>再跑一遍完整死亡阶段：必须是无操作。
        ///
        /// 这是对 <see cref="RepeatedDeathProcessingDoesNotDuplicateLifecycleNotice"/> 的
        /// 直接强化：上一条用例证明"跨 Tick 重入不重复"，本用例证明"同一 Tick 的第二次
        /// 完整 <c>ProcessDeaths</c> 也不重复"。做法是让单位在<strong>阶段 1</strong> 致死：
        /// 阶段 2 的 <c>DeathAndVictory</c> 已知晓"某侧失去战斗效力"（<c>IsCombatEffective</c>）
        /// 并<strong>完整提交</strong>死亡事实，因此阶段 15 的 <c>PostDisplacementDeath</c>
        /// 必然对同一单位再跑一次——它必须被 <c>IsKillable</c> 守卫跳过后直接返回。
        /// 全 Tick 只允许存在<strong>一条</strong>通知、<strong>一个</strong>死亡事件、
        /// <strong>一次</strong> Dead 状态转换，且死亡阶段不分配任何新身份。
        /// </summary>
        [Test]
        public void CommittedDeathIsNotReprocessedWithinTheSameTick()
        {
            var sink = new RecordingLifecycleSink();
            var killer = new KillBatchAtTick
            {
                Tick = 0L,
                Kills = new[] { Task04Farm.EnemyUnitId.Value }
            };
            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: killer,
                lifecycleNoticeSink: sink));

            StepResult ended = Task04Farm.StepNext(sim);

            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded));
            // 同一 Tick 内死亡阶段被调用了两次（阶段 2 提交 + 阶段 15 补跑），两次都必须幂等。
            Assert.That(sim.LastStepExecuted(StepPhase.DeathAndVictory), Is.True);
            Assert.That(sim.LastStepExecuted(StepPhase.PostDisplacementDeath), Is.True,
                "阶段 2 提交死亡后仍进入阶段 15：第二次处理必须是纯无操作");

            Assert.That(sim.LifecycleNotices.Count, Is.EqualTo(1), "同一 Tick 二次处理不得追加通知");
            Assert.That(sink.Notices.Count, Is.EqualTo(1));
            Assert.That(sim.LastDeathFootprintRemovals.Count, Is.EqualTo(1),
                "同一 Tick 二次处理不得重复移除 footprint");
            Assert.That(Task04Farm.EventsOfType<UnitDiedEvent>(ended.Events).Count, Is.EqualTo(1),
                "同一 Tick 二次处理不得重复发死亡事件");
            Assert.That(Task04Farm.EventsOfType<UnitStateChangedEvent>(ended.Events)
                .Count(e => e.ToState == UnitState.Dead), Is.EqualTo(1),
                "同一 Tick 二次处理不得重复消耗 Dead 转换");
            Assert.That(ended.Snapshot.NextEffectId, Is.EqualTo(1L),
                "死亡阶段不得分配任何 EffectId（初始 NextEffectId 为 1）");

            // 结束后的幂等 Step：稳定空操作，不推进 Tick、不发射任何事件。
            StepResult already = sim.Step(1L, sim.CommandIngress.FreezeTick(1L));
            Assert.That(already.Status, Is.EqualTo(StepStatus.AlreadyEnded));
            Assert.That(already.Events.Count, Is.EqualTo(0));
            Assert.That(sim.LifecycleNotices.Count, Is.EqualTo(1), "每个单位整场恰好一条通知");
            Assert.That(sim.LastDeathFootprintRemovals.Count, Is.EqualTo(1),
                "结束后的幂等 Step 不得再次移除 footprint");
        }

        /// <summary>
        /// 持续效果造成的死亡必须发生在<strong>新命令处理之前</strong>（阶段 1 → 阶段 2），
        /// 因此：① 窗口不打开；② 本 Tick 已冻结的批次获得稳定拒绝码而不是被静默丢弃。
        /// </summary>
        [Test]
        public void DamageOverTimeDeathOccursBeforeNewCommands()
        {
            // Tick 0 施加，持续 2 Tick：半开区间 [0, 2) ⇒ 只在 Tick 1 触发一次致死伤害。
            var dot = new DamageOverTimeApplyAtTick
            {
                ApplyTick = 0L,
                UnitId = Task04Farm.EnemyUnitId.Value,
                DurationTicks = 2,
                DamagePerTickQ10 = 204800
            };
            var schedule = new WindowScheduleForTask04
            {
                OpenTick = 1L,
                OwnerUnitId = Task04Farm.HeroUnitId.Value
            };

            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: dot,
                turnWindowSchedule: schedule));

            Task04Farm.StepEmpty(sim); // Tick 0：施加效果，窗口未到期
            Assert.That(sim.CurrentSnapshot.Effects.Count, Is.EqualTo(1), "效果必须在 Tick 0 进入快照");

            // Tick 1：本 Tick 冻结一条命令，同时 DOT 触发致死。
            Task04Farm.PlayerEntry(sim)?.Submit(ScheduleAdd(1L, 0L));
            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(1L);
            StepResult ended = sim.Step(1L, batch);

            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded), "DOT 致死必须在命令阶段之前结束战斗");

            Assert.That(sim.LastStepExecuted(StepPhase.DeathAndVictory), Is.True);
            Assert.That(sim.LastStepExecuted(StepPhase.WindowOpen), Is.False,
                "阶段 2 已判定战斗结束：窗口打开阶段不得执行");
            Assert.That(Task04Farm.EventsOfType<TurnWindowOpenedEvent>(ended.Events).Count, Is.EqualTo(0));

            var deaths = Task04Farm.EventsOfType<UnitDiedEvent>(ended.Events);
            Assert.That(deaths.Count, Is.EqualTo(1), "DOT 必须在阶段 1 触发、阶段 2 的死亡路径提交");
            Assert.That(Task04Farm.UnitOf(ended.Snapshot, Task04Farm.EnemyUnitId).IsAlive, Is.False);

            // 本 Tick 冻结的批次必须获得稳定拒绝码，而不是被静默丢弃。
            Assert.That(sim.LastCommandSet, Is.Not.Null, "冻结批次仍然可读（审计）");
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(1));
            var rejections = Task04Farm.EventsOfType<CommandRejectedEvent>(ended.Events);
            Assert.That(rejections.Count, Is.EqualTo(1),
                "命令阶段之前结束战斗时，已冻结命令必须得到稳定拒绝事件");
            Assert.That(rejections[0].ReasonCode,
                Is.EqualTo(CommandCodes.COMMAND_BATTLE_ENDED_BEFORE_COMMAND_PHASE));
            Assert.That(rejections[0].CommandSequence,
                Is.EqualTo(sim.LastCommandSet.Envelopes[0].CommandSequence));

            // 事件顺序：死亡先于该拒绝。
            Assert.That(IndexOfFirst<UnitDiedEvent>(ended.Events),
                Is.LessThan(IndexOfFirst<CommandRejectedEvent>(ended.Events)));
            Assert.That(ended.Events.Events.Last(), Is.InstanceOf<BattleEndedEvent>());
        }

        // =====================================================================
        // 同 Tick 致死与强制位移
        // =====================================================================

        /// <summary>
        /// 同 Tick 致死单位必须先参与同时位移求解、批量换位之后才进入 Dead，
        /// 并从<strong>最终位置</strong>移除 footprint；位移事件先于死亡事件。
        ///
        /// <strong>Tick 设计</strong>：致死与位移都在 Tick 1 发生——若在 Tick 0，阶段 2 的
        /// 死亡/胜负会先结束战斗（消灭 hostile 侧即胜负已定），后面的位移阶段根本不会执行。
        /// 胜负判定在 Tick 1 同样被显式推迟一格（见 <see cref="DeferredVictoryEvaluator"/>），
        /// 否则阶段 16 的结束仍会抢在报告之前；<strong>推迟的只是"何时宣布结果"</strong>，
        /// 死亡时机与位移阶段完全不变。
        /// </summary>
        [Test]
        public void LethalForcedDisplacementCommitsBeforeDeathRemovesFinalOccupancy()
        {
            var displacement = new DisplacementRequestsForTask04
            {
                TargetUnitId = Task04Farm.EnemyUnitId.Value,
                Steps = 1,
                Direction = GridDirection.East,
                MomentumUnits = 5L,
                ConflictGroupKey = 11L
            };
            // 一次位移 1 步 = 沿 East 前进 2 个 x 单位（doubled 坐标：合法格保持 x+y 偶数）。
            var solver = new DisplacementSolverForTask04 { AppliedSteps = 1, Dx = 2, Dy = 0 };
            var kill = new KillBatchAtTick
            {
                Tick = 1L,
                Kills = new[] { Task04Farm.EnemyUnitId.Value }
            };
            var deferred = new DeferredVictoryEvaluator();
            deferred.DeferredTicks.Add(1L);

            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: kill,
                displacementRequestBuilder: displacement,
                displacementSolver: solver,
                victoryEvaluator: deferred));

            Task04Farm.StepEmpty(sim); // Tick 0：正常推进
            var initial = Task04Farm.UnitOf(sim.CurrentSnapshot, Task04Farm.EnemyUnitId);
            StepResult first = Task04Farm.StepNext(sim);

            // 阶段 12 的只读求解必须看到"致死但仍在场"的单位（未提前退出同时求解）。
            Assert.That(solver.ObservedInputs,
                Does.Contain("1:" + Task04Farm.EnemyUnitId.Value + "@" + initial.X + "," + initial.Y),
                "同 Tick 致死单位仍必须参与强制位移的只读求解（求解看到的是换位前的位置）："
                + string.Join(" | ", solver.ObservedInputs));
            Assert.That(displacement.BuildCalls, Is.GreaterThan(0), "位移请求构建器必须被调用");
            Assert.That(deferred.DeferredCalls, Is.GreaterThan(0), "对照证据：Tick 1 的判定确实被推迟");

            Assert.That(first.Status, Is.EqualTo(StepStatus.Advanced));
            var after = Task04Farm.UnitOf(first.Snapshot, Task04Farm.EnemyUnitId);
            Assert.That(after.X, Is.EqualTo(initial.X + 2), "换位必须已提交（1 步 = +2 x，doubled 坐标）");
            Assert.That(after.IsAlive, Is.False, "换位提交之后才进入 Dead");
            Assert.That(after.State, Is.EqualTo((int)UnitState.Dead));

            // 死亡通知携带的是**换位后**的最终位置（而不是换位前的旧位置）。
            Assert.That(sim.LifecycleNotices.Count, Is.EqualTo(1));
            Assert.That(sim.LifecycleNotices[0].FinalPosition.X, Is.EqualTo(initial.X + 2),
                "死亡通知必须报告换位后的最终位置（换位前为 " + initial.X + "）");
            Assert.That(sim.LifecycleNotices[0].FinalPosition.Y, Is.EqualTo(after.Y));

            // 事件顺序：位移事件先于死亡事件（阶段 13 的位移事件早于阶段 15 的死亡提交）。
            int displacementIndex = IndexOfFirst<ForcedDisplacementResolvedEvent>(first.Events);
            int deathIndex = IndexOfFirst<UnitDiedEvent>(first.Events);
            Assert.That(displacementIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(deathIndex, Is.GreaterThan(displacementIndex),
                "位移事件必须严格早于死亡事件；displacement=" + displacementIndex
                + " death=" + deathIndex + " events="
                + string.Join(",", first.Events.Events.Select(e => e.GetType().Name)));

            // 阶段顺序：批量换位 → 状态/控制 → 死亡。
            var phases = sim.LastStepPhaseTrace.Select(e => e.Phase).ToList();
            Assert.That(phases.IndexOf(StepPhase.PlanReservationCleanupAndBatchCommit),
                Is.LessThan(phases.IndexOf(StepPhase.PostDisplacementDeath)));
        }

        /// <summary>
        /// 同批中"致死单位"与"存活单位"必须同时求解：致死单位参与依赖图
        /// （它的占位仍然阻挡后车，因此后车停住），而不是被提前摘除后让后车通行。
        /// </summary>
        [Test]
        public void LethalUnitStillParticipatesInSameTickDisplacementDependencyGraph()
        {
            var solver = new BlockingDependencyProbeSolver
            {
                BlockerUnitId = Task04Farm.EnemyUnitId.Value,
                FollowerUnitId = Task04Farm.HeroUnitId.Value,
                Steps = 1
            };
            var displacement = new DisplacementRequestsForTask04
            {
                TargetUnitId = Task04Farm.HeroUnitId.Value,
                Steps = 1,
                Direction = GridDirection.East,
                MomentumUnits = 5L,
                ConflictGroupKey = 21L
            };
            var kill = new KillBatchAtTick
            {
                Tick = 1L,
                Kills = new[] { Task04Farm.EnemyUnitId.Value }
            };
            // 被位移的是 hero，致死的是 enemy；Tick 1 的胜负判定被显式推迟一格，
            // 否则阶段 16 会在结束战斗前把位移事件丢在未执行的阶段 11–13 里。
            var deferred = new DeferredVictoryEvaluator();
            deferred.DeferredTicks.Add(1L);

            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: kill,
                displacementRequestBuilder: displacement,
                displacementSolver: solver,
                victoryEvaluator: deferred));

            Task04Farm.StepEmpty(sim); // Tick 0：正常推进
            StepResult result = Task04Farm.StepNext(sim);

            Assert.That(solver.SawLethalUnitAlive, Is.True,
                "求解阶段必须看到致死单位仍然 IsAlive（否则它在同一 Tick 提前退出了同时求解）");
            Assert.That(solver.SawLethalUnitAsObstacle, Is.True,
                "致死单位的占位必须在同一 Tick 仍然阻挡静止单位");

            // 求解结论：后车停住（AppliedSteps = 0），因为它前方被本 Tick 尚未死亡的敌人占住。
            var relocations = Task04Farm.EventsOfType<ForcedDisplacementResolvedEvent>(result.Events);
            Assert.That(relocations.Count, Is.EqualTo(1),
                "必须恰好一条位移事件；实测=" + relocations.Count
                + " events=" + string.Join(",", result.Events.Events.Select(e => e.GetType().Name)));
            Assert.That(relocations[0].TargetUnitId.Value, Is.EqualTo(Task04Farm.HeroUnitId.Value));
            Assert.That(relocations[0].AppliedSteps, Is.EqualTo(0),
                "被致死单位阻挡时后车必须停住：换位结果不得因该单位本 Tick 会死而改变；实测="
                + relocations[0].AppliedSteps + " stop=" + relocations[0].StopReason
                + " alive=" + solver.SawLethalUnitAlive + " obstacle=" + solver.SawLethalUnitAsObstacle);
            Assert.That(relocations[0].StopReason, Is.EqualTo(ForcedDisplacementStopReason.StaticObstacle));

            // 死亡仍发生在位移事件之后（阶段 15 在阶段 13 之后）。
            Assert.That(IndexOfFirst<UnitDiedEvent>(result.Events),
                Is.GreaterThan(IndexOfFirst<ForcedDisplacementResolvedEvent>(result.Events)));

            // 对照证据：致死单位在换位阶段之后才进入 Dead，且它自己的位置没有被改动。
            UnitSnapshot enemy = Task04Farm.UnitOf(result.Snapshot, Task04Farm.EnemyUnitId);
            Assert.That(enemy.IsAlive, Is.False);
            Assert.That(enemy.State, Is.EqualTo((int)UnitState.Dead));
            Assert.That(enemy.X, Is.EqualTo(5), "被阻挡的是 hero，enemy 自己的位置不变");
        }

        // =====================================================================
        // 胜负阶段顺序与结果路由
        // =====================================================================

        [Test]
        public void VictoryIsEvaluatedBeforeWindowAdvance()
        {
            var schedule = new WindowScheduleForTask04
            {
                OpenTick = 1L,
                OwnerUnitId = Task04Farm.HeroUnitId.Value
            };
            var kill = new KillBatchAtTick
            {
                Tick = 1L,
                Kills = new[] { Task04Farm.EnemyUnitId.Value }
            };

            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: kill,
                turnWindowSchedule: schedule));

            Task04Farm.StepEmpty(sim);
            StepResult ended = Task04Farm.StepNext(sim);

            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded));
            // 阶段 1 的击杀在阶段 2 就被判定为"战斗已决定"，因此本 Tick 的窗口打开
            // （阶段 3）与窗口推进（阶段 17）都不执行——这正是"胜负先于窗口推进"。
            Assert.That(sim.LastStepExecuted(StepPhase.DeathAndVictory), Is.True);
            Assert.That(sim.LastStepExecuted(StepPhase.WindowOpen), Is.False,
                "命令前的死亡/胜负判定先于窗口打开");
            Assert.That(sim.LastStepExecuted(StepPhase.WindowCloseAndScheduleNext), Is.False);
            Assert.That(ended.Snapshot.NextWindowId, Is.EqualTo(1L), "不得消耗 WindowId");
            Assert.That(ended.Snapshot.WindowManager.CurrentWindowId, Is.EqualTo(0L));
            Assert.That(Task04Farm.EventsOfType<TurnWindowOpenedEvent>(ended.Events).Count, Is.EqualTo(0));
        }

        [Test]
        public void PreCommandDeathAndVictoryRunBeforeScheduledWindowOpen()
        {
            var schedule = new WindowScheduleForTask04
            {
                OpenTick = 0L,
                OwnerUnitId = Task04Farm.HeroUnitId.Value
            };
            var kill = new KillBatchAtTick
            {
                Tick = 0L,
                Kills = new[] { Task04Farm.EnemyUnitId.Value, Task04Farm.HeroUnitId.Value }
            };

            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: kill,
                turnWindowSchedule: schedule));

            StepResult ended = Task04Farm.StepNext(sim);

            var phases = sim.LastStepPhaseTrace.Select(e => e.Phase).ToList();
            Assert.That(phases.IndexOf(StepPhase.StateAndEffectAdvance),
                Is.LessThan(phases.IndexOf(StepPhase.DeathAndVictory)),
                "状态/持续效果推进必须先于死亡与胜负");
            // 战斗在阶段 2 结束 ⇒ 阶段 3（窗口打开）**根本不执行**，
            // 因此"胜负先于窗口推进"的证据是：死亡阶段已执行且窗口阶段未执行。
            // （若窗口阶段也执行了，它的下标会存在，而"窗口未打开"就只是巧合。）
            Assert.That(sim.LastStepExecuted(StepPhase.DeathAndVictory), Is.True);
            // 阶段 2 的死亡与胜负判定都在窗口打开之前：两个单位同 Tick 全灭 ⇒
            // 阶段 3 的窗口打开不执行，因此窗口拥有者死亡时不会产生 TurnWindowOpenedEvent。
            Assert.That(phases.IndexOf(StepPhase.WindowOpen), Is.EqualTo(-1),
                "命令前的死亡与胜负必须先于窗口打开：窗口阶段不得执行");
            Assert.That(sim.LastStepExecuted(StepPhase.WindowCloseAndScheduleNext), Is.False);
            Assert.That(Task04Farm.EventsOfType<TurnWindowOpenedEvent>(ended.Events).Count, Is.EqualTo(0),
                "同 Tick 全灭时窗口拥有者也已死亡：不得产生打开事件");
            Assert.That(ended.Snapshot.WindowManager.CurrentWindowId, Is.EqualTo(0L));
        }

        /// <summary>
        /// 胜负系统只确定结果码并触发唯一 Finalizer：
        /// 事件批次里恰好一个 <see cref="BattleEndedEvent"/>、且它是最后一个事件。
        /// </summary>
        [Test]
        public void VictoryDeclaresResultWithoutEmittingBattleEndedBeforeFinalization()
        {
            var evaluator = new CountingVictoryEvaluator();
            var kill = new KillBatchAtTick
            {
                Tick = 0L,
                Kills = new[] { Task04Farm.EnemyUnitId.Value }
            };

            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: kill,
                victoryEvaluator: evaluator));

            StepResult ended = Task04Farm.StepNext(sim);

            // 本 Tick 的击杀在阶段 1 生效、阶段 2 就判出"战斗已决定"，因此不会走到
            // 阶段 16；结果码由配置直接推出（见 EvaluateConfiguredResultCode），
            // 而**不是** BATTLE_STOPPED 之类的默认码。
            Assert.That(ended.Snapshot.BattleEnd.ResultCode,
                Is.EqualTo(Task04Farm.Encounter.Victory.VictoryResultCode));
            Assert.That(ended.Snapshot.BattleEnd.ResultCode,
                Is.Not.EqualTo(ProjectHero.Logic.Simulation.BattleResultCodes.Stopped),
                "命令前结束的结果码必须来自 VictoryDefinition，不是默认停止码");
            Assert.That(evaluator.DistinctResultCodes, Does.Not.Contain("BATTLE_ENDED"),
                "评估器只能返回结果码，不得自行发射结束事件");

            var battleEnded = Task04Farm.EventsOfType<BattleEndedEvent>(ended.Events);
            Assert.That(battleEnded.Count, Is.EqualTo(1), "整场只能有一个 BattleEndedEvent");
            Assert.That(battleEnded[0].ResultCode,
                Is.EqualTo(Task04Farm.Encounter.Victory.VictoryResultCode),
                "结果码必须来自权威 VictoryDefinition");
            Assert.That(battleEnded[0].ResultCode, Is.EqualTo(evaluator.Evaluate(
                    ended.Snapshot.Units, Task04Farm.Encounter.Victory, sim.Tick)),
                "结束事件的 ResultCode 必须与按当前存活集合复算的评估结果一致");
            Assert.That(ended.Events.Events.Last(), Is.InstanceOf<BattleEndedEvent>(),
                "BattleEndedEvent 必须是结束 Tick 的最后一个逻辑事件");
            Assert.That(sim.FinalizerReport, Is.Not.Null);
            Assert.That(sim.FinalizerReport.AlreadyFinalized, Is.False);

            // 结束后的 Step 是幂等 AlreadyEnded：不推进 Tick、不处理命令、不发射事件。
            StepResult after = sim.Step(sim.Tick + 1L, sim.CommandIngress.FreezeTick(sim.Tick + 1L));
            Assert.That(after.Status, Is.EqualTo(StepStatus.AlreadyEnded));
            Assert.That(after.Events.Count, Is.EqualTo(0));
        }

        [Test]
        public void VictoryUsesFactionIdNotControllerOrPlayerFlag()
        {
            // 真实定义：Allied = {hero}、Hostile = {monster}；hero 槽位由 controller.player 控制、
            // enemy 槽位由 controller.enemy_ai 控制。消灭**被 AI 控制**的 enemy 必须产生胜利——
            // 如果实现按"玩家标志/控制者类型"判胜负，这条会得到相反结果。
            var killEnemy = new KillBatchAtTick
            {
                Tick = 0L,
                Kills = new[] { Task04Farm.EnemyUnitId.Value }
            };
            var winning = Task04Farm.NewSim(new BattleSimulationAssembly(unitStateAdvance: killEnemy));
            StepResult victory = Task04Farm.StepNext(winning);
            Assert.That(victory.Status, Is.EqualTo(StepStatus.BattleEnded));
            Assert.That(victory.Snapshot.BattleEnd.ResultCode,
                Is.EqualTo(Task04Farm.Encounter.Victory.VictoryResultCode),
                "消灭 hostile 侧（AI 控制）必须产生 Victory：胜负只读 FactionId");

            // 对称对照：消灭被玩家控制的 hero 必须产生 Defeat。
            var killHero = new KillBatchAtTick
            {
                Tick = 0L,
                Kills = new[] { Task04Farm.HeroUnitId.Value }
            };
            var losing = Task04Farm.NewSim(new BattleSimulationAssembly(unitStateAdvance: killHero));
            StepResult defeat = Task04Farm.StepNext(losing);
            Assert.That(defeat.Status, Is.EqualTo(StepStatus.BattleEnded));
            Assert.That(defeat.Snapshot.BattleEnd.ResultCode,
                Is.EqualTo(Task04Farm.Encounter.Victory.DefeatResultCode));

            // 对照证据：两个单位确实分属两个目标组，且控制者不同。
            UnitSnapshot enemy = Task04Farm.UnitOf(victory.Snapshot, Task04Farm.EnemyUnitId);
            UnitSnapshot hero = Task04Farm.UnitOf(defeat.Snapshot, Task04Farm.HeroUnitId);
            Assert.That(enemy.FactionId, Is.Not.EqualTo(hero.FactionId));
            Assert.That(Task04Farm.Encounter.Victory.HostileFactionIds
                .Any(f => f.Value == enemy.FactionId), Is.True);
            Assert.That(Task04Farm.Encounter.Victory.AlliedFactionIds
                .Any(f => f.Value == hero.FactionId), Is.True);
        }

        [Test]
        public void VictoryUsesConfiguredObjectiveGroupsNotRuntimeDispositionScan()
        {
            // 同一份单位集合、同一份关系矩阵，只换 VictoryDefinition 的目标组：
            // 结果必须随**配置**变化，而不是随关系矩阵推导出来。
            var victory = VictoryOf(new[] { "hero" }, new[] { "monster" });
            var evaluator = new FactionEliminationVictoryEvaluator();
            IReadOnlyList<UnitSnapshot> units = new[]
            {
                Snapshot(1L, "monster", alive: false),
                Snapshot(2L, "hero", alive: true)
            };

            Assert.That(evaluator.Evaluate(units, victory, 0L), Is.EqualTo(VictoryCode));

            // 把目标组对调：同一份单位集合必须得到**相反**结果 —— 证明目标组来自配置，
            // 不是"看到 monster 死了就当赢"。对调后 Allied={monster}（已全灭）、
            // Hostile={hero}（存活）⇒ 配置的 Defeat 码。
            var swapped = VictoryOf(new[] { "monster" }, new[] { "hero" });
            Assert.That(evaluator.Evaluate(units, swapped, 0L), Is.EqualTo(DefeatCode),
                "对调后 Allied 组（monster）全灭 ⇒ 配置的 Defeat，而不是沿用旧目标组的 Victory");
            Assert.That(evaluator.Evaluate(units, victory, 0L),
                Is.Not.EqualTo(evaluator.Evaluate(units, swapped, 0L)),
                "同一单位存活形态在两个目标组配置下必须得到不同结果");

            // 精确对照：让对调后的 Hostile 组（hero）也被消灭。
            IReadOnlyList<UnitSnapshot> bothDead = new[]
            {
                Snapshot(1L, "monster", alive: false),
                Snapshot(2L, "hero", alive: false)
            };
            Assert.That(evaluator.Evaluate(bothDead, swapped, 0L), Is.EqualTo(DrawCode));

            // 目标组两侧都必须非空，否则不产生结果（不做"缺省即胜利"补洞）。
            Assert.That(evaluator.Evaluate(units, VictoryOf(new[] { "hero" }, new string[0]), 0L), Is.Null);
            Assert.That(evaluator.Evaluate(units, null, 0L), Is.Null);

            // 目标外阵营的存在不改变结论（胜负系统不扫描关系矩阵扩张目标组）。
            IReadOnlyList<UnitSnapshot> withOutsider = new[]
            {
                Snapshot(1L, "monster", alive: false),
                Snapshot(2L, "hero", alive: true),
                Snapshot(3L, "outsider", alive: true)
            };
            Assert.That(evaluator.Evaluate(withOutsider, victory, 0L), Is.EqualTo(VictoryCode));
        }

        [Test]
        public void AlliedEliminatedProducesConfiguredDefeat()
        {
            var evaluator = new FactionEliminationVictoryEvaluator();
            var victory = VictoryOf(new[] { "hero" }, new[] { "monster" });

            IReadOnlyList<UnitSnapshot> units = new[]
            {
                Snapshot(1L, "monster", alive: true),
                Snapshot(2L, "hero", alive: false)
            };
            Assert.That(evaluator.Evaluate(units, victory, 0L), Is.EqualTo(DefeatCode));

            // 真实管线对照：结果码必须来自配置而不是硬编码字符串。
            var killHero = new KillBatchAtTick
            {
                Tick = 0L,
                Kills = new[] { Task04Farm.HeroUnitId.Value }
            };
            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(unitStateAdvance: killHero));
            StepResult ended = Task04Farm.StepNext(sim);
            Assert.That(ended.Snapshot.BattleEnd.ResultCode,
                Is.EqualTo(Task04Farm.Encounter.Victory.DefeatResultCode));
            Assert.That(ended.Snapshot.BattleEnd.ResultCode, Is.Not.EqualTo(VictoryCode));
        }

        [Test]
        public void HostileEliminatedProducesConfiguredVictory()
        {
            var evaluator = new FactionEliminationVictoryEvaluator();
            var victory = VictoryOf(new[] { "hero" }, new[] { "monster" });

            IReadOnlyList<UnitSnapshot> units = new[]
            {
                Snapshot(1L, "monster", alive: false),
                Snapshot(2L, "hero", alive: true)
            };
            Assert.That(evaluator.Evaluate(units, victory, 0L), Is.EqualTo(VictoryCode));
        }

        [Test]
        public void SimultaneousFactionEliminationProducesConfiguredDraw()
        {
            var evaluator = new FactionEliminationVictoryEvaluator();
            var victory = VictoryOf(new[] { "hero" }, new[] { "monster" });

            IReadOnlyList<UnitSnapshot> units = new[]
            {
                Snapshot(1L, "monster", alive: false),
                Snapshot(2L, "hero", alive: false)
            };
            Assert.That(evaluator.Evaluate(units, victory, 0L), Is.EqualTo(DrawCode));

            // 结果不得依赖单位顺序（不能用 if 顺序偶然判成胜利或失败）。
            IReadOnlyList<UnitSnapshot> reversed = new[]
            {
                Snapshot(2L, "hero", alive: false),
                Snapshot(1L, "monster", alive: false)
            };
            Assert.That(evaluator.Evaluate(reversed, victory, 0L), Is.EqualTo(DrawCode));

            // 真实管线对照：同 Tick 双杀必须得到真实定义的 Draw 码，且死亡顺序按 UnitId。
            var killAll = new KillBatchAtTick
            {
                Tick = 0L,
                Kills = new[] { Task04Farm.EnemyUnitId.Value, Task04Farm.HeroUnitId.Value }
            };
            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(unitStateAdvance: killAll));
            StepResult ended = Task04Farm.StepNext(sim);
            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded));
            Assert.That(ended.Snapshot.BattleEnd.ResultCode,
                Is.EqualTo(Task04Farm.Encounter.Victory.DrawResultCode));
            Assert.That(sim.LifecycleNotices.Count, Is.EqualTo(2));
            Assert.That(sim.LifecycleNotices[0].UnitId.Value, Is.EqualTo(Task04Farm.EnemyUnitId.Value));
            Assert.That(sim.LifecycleNotices[1].UnitId.Value, Is.EqualTo(Task04Farm.HeroUnitId.Value));
        }

        [Test]
        public void OutOfObjectiveFactionDoesNotBlockEliminationVictoryOrLoseItsDisposition()
        {
            var evaluator = new FactionEliminationVictoryEvaluator();
            var victory = VictoryOf(new[] { "hero" }, new[] { "monster" });

            // 目标外阵营存活：不阻止消灭条件完成。
            IReadOnlyList<UnitSnapshot> withOutsider = new[]
            {
                Snapshot(1L, "monster", alive: false),
                Snapshot(2L, "hero", alive: true),
                Snapshot(3L, "outsider", alive: true)
            };
            Assert.That(evaluator.Evaluate(withOutsider, victory, 0L), Is.EqualTo(VictoryCode),
                "目标外阵营的存活不得阻止消灭条件完成");

            // 目标外阵营全灭 + 目标组未全灭：不是"胜利"，也不被改写成 Neutral 后消失。
            IReadOnlyList<UnitSnapshot> outsiderDead = new[]
            {
                Snapshot(1L, "monster", alive: true),
                Snapshot(2L, "hero", alive: true),
                Snapshot(3L, "outsider", alive: false)
            };
            Assert.That(evaluator.Evaluate(outsiderDead, victory, 0L), Is.Null);

            // 关系矩阵仍保留目标外阵营的真实关系：它既没有被重写，也没有被移除。
            var model = Task04Farm.Definition.FactionModel;
            Assert.That(model.ContainsFaction(new FactionId("hero")), Is.True);
            Assert.That(model.ContainsFaction(new FactionId("monster")), Is.True);
            Assert.That(model.TryGetDisposition(new FactionId("hero"), new FactionId("monster"),
                out FactionDisposition disposition), Is.True);
            Assert.That(disposition, Is.EqualTo(FactionDisposition.Hostile),
                "目标组划分不得改写关系矩阵");

            // 目标外阵营在关系解析器里仍按矩阵解析（不被当作 Neutral）。
            var resolver = Task04Farm.SimulationFactionResolver();
            UnitId hero = Task04Farm.HeroUnitId;
            UnitId enemy = Task04Farm.EnemyUnitId;
            Assert.That(resolver.Classify(hero, enemy), Is.EqualTo(UnitRelation.Hostile),
                "关系解析器必须仍按矩阵返回 Hostile");

            // —— 修订轮（R3，见 04-交接记录 §13）：目标外阵营必须有「经真实关系解析器」产出的断言实体。
            //
            //    此前本用例的 "outsider" 只出现在上面合成的 UnitSnapshot 字面量里，
            //    真实 FactionModel 中也未注册该阵营，因此"不被改写为 Neutral"实际由结构性事实
            //    （胜负系统零写入面）承担，而不是由本用例承担。
            //    现在改为：把 outsider 注册进真实关系矩阵，并作为第三个真实出场单位跑完整管线，
            //    逐条断言"矩阵原值 → 运行时解析 → 胜负判定 → 管线跑完"四个环节都没有把它
            //    改写成 Neutral。
            FactionModelDefinition modelWithOutsider = Task04OutsiderVariant.FactionModelWithOutsider();
            var outsiderFaction = new FactionId(Task04OutsiderVariant.OutsiderFactionId);

            // ① 注册与矩阵原值：outsider 必须真实存在于关系矩阵，且与两个目标阵营各有一条关系。
            Assert.That(modelWithOutsider.ContainsFaction(outsiderFaction), Is.True,
                "目标外阵营必须真实注册在关系矩阵里（否则解析器在结构上不可能对它分类）");
            Assert.That(modelWithOutsider.ExpectedRelationCount, Is.EqualTo(3),
                "3 个阵营 = 恰好 3 条上三角关系（缺项不得补洞）");
            Assert.That(modelWithOutsider.Relations.Count, Is.EqualTo(3));
            Assert.That(modelWithOutsider.TryGetDisposition(
                    new FactionId("hero"), outsiderFaction, out FactionDisposition heroOutsider), Is.True);
            Assert.That(heroOutsider, Is.EqualTo(FactionDisposition.Hostile));
            Assert.That(modelWithOutsider.TryGetDisposition(
                    new FactionId("monster"), outsiderFaction, out FactionDisposition monsterOutsider), Is.True);
            Assert.That(monsterOutsider, Is.EqualTo(FactionDisposition.Hostile));

            // ② 真实初始化器分配：第三个槽位必须拿到 UnitId(3) 且阵营原样复制。
            BattleInitializationResult initialization = Task04OutsiderVariant.Initialize();
            UnitId outsider = initialization.SlotToUnitId[
                new EncounterSlotId(Task04OutsiderVariant.OutsiderSlotId)];
            Assert.That(outsider.Value, Is.EqualTo(Task04OutsiderVariant.OutsiderUnitId.Value),
                "SlotId 的 Ordinal 顺序决定 UnitId：enemy=1 / hero=2 / outsider=3");
            Assert.That(initialization.SlotToFaction[
                    new EncounterSlotId(Task04OutsiderVariant.OutsiderSlotId)].Value,
                Is.EqualTo(Task04OutsiderVariant.OutsiderFactionId), "槽位阵营原样复制，不得被派生或改写");

            // ③ 真实关系解析器：三个方向全部来自配置矩阵，最近的一步就是"矩阵原值"。
            IFactionRelationResolver lookup = initialization.FactionResolver;
            Assert.That(lookup.Classify(hero, outsider), Is.EqualTo(UnitRelation.Hostile),
                "目标外阵营的关系必须来自配置矩阵，不得被强制改写为 Neutral");
            Assert.That(lookup.Classify(enemy, outsider), Is.EqualTo(UnitRelation.Hostile));
            Assert.That(lookup.Classify(outsider, hero), Is.EqualTo(UnitRelation.Hostile));
            Assert.That(lookup.Classify(outsider, enemy), Is.EqualTo(UnitRelation.Hostile));
            Assert.That(lookup.Classify(outsider, outsider), Is.EqualTo(UnitRelation.Self));
            Assert.That(lookup.Classify(hero, enemy), Is.EqualTo(UnitRelation.Hostile),
                "加入目标外阵营不得改变两个目标阵营之间的关系");

            // ④ 让这场三单位战斗真的跑到终局：outsider 全程存活，但两个目标阵营同 Tick 全灭
            //    ⇒ 配置 Draw。即"目标外阵营既不阻止消灭条件成立，也没有因为'不在目标组里'
            //    而被降级成 Neutral 或从世界里消失"。
            var killBothObjectives = new KillBatchAtTick
            {
                Tick = 0L,
                Kills = new[] { Task04Farm.EnemyUnitId.Value, Task04Farm.HeroUnitId.Value }
            };
            BattleSimulation outsiderBattle = Task04OutsiderVariant.NewSim(
                new BattleSimulationAssembly(unitStateAdvance: killBothObjectives));
            StepResult outsiderEnded = Task04Farm.StepNext(outsiderBattle);

            Assert.That(outsiderEnded.Status, Is.EqualTo(StepStatus.BattleEnded));
            Assert.That(outsiderEnded.Snapshot.BattleEnd.ResultCode,
                Is.EqualTo(Task04Farm.Encounter.Victory.DrawResultCode),
                "两个目标组同 Tick 全灭 ⇒ 配置 Draw；目标外阵营的存活不参与消灭条件");
            Assert.That(Task04Farm.UnitOf(outsiderEnded.Snapshot, outsider).IsAlive, Is.True,
                "目标外阵营单位全程未被死亡系统或胜负系统改写");
            Assert.That(Task04Farm.UnitOf(outsiderEnded.Snapshot, outsider).FactionId,
                Is.EqualTo(Task04OutsiderVariant.OutsiderFactionId),
                "运行时单位的 FactionId 不得被改写成 Neutral 或任何其他值");

            // ⑤ 管线跑完之后，运行时解析器给出的关系仍与配置矩阵逐条一致（未被胜负路径改写）。
            IFactionRelationResolver afterBattle = outsiderBattle.FactionResolver;
            Assert.That(afterBattle.Classify(hero, outsider), Is.EqualTo(UnitRelation.Hostile),
                "胜负提交之后目标外阵营的关系必须仍等于矩阵原值");
            Assert.That(afterBattle.Classify(outsider, enemy), Is.EqualTo(UnitRelation.Hostile));
            Assert.That(afterBattle.Classify(hero, enemy), Is.EqualTo(UnitRelation.Hostile));
            Assert.That(afterBattle.Classify(outsider, outsider), Is.EqualTo(UnitRelation.Self));
        }

        // =====================================================================
        // 状态、效果进入事件与快照
        // =====================================================================

        [Test]
        public void StateAndEffectsAppearInEventsAndCanonicalSnapshot()
        {
            var control = new ControlTransitionAtTick
            {
                Tick = 0L,
                UnitId = Task04Farm.EnemyUnitId.Value,
                Transition = StateTransitionSpec.Timed(UnitState.Guarding, 4, UnitState.Idle)
            };
            var dot = new ApplyEffectAtTick
            {
                Tick = 0L,
                UnitId = Task04Farm.EnemyUnitId.Value,
                Spec = new StatusEffectRuntimeSpec(
                    new StatusEffectSpecId("status.test_dot"), 6,
                    new DamageOverTimeEffectPayload(1024))
            };

            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: new CompositeAdvanceSystem(control, dot)));

            StepResult first = Task04Farm.StepNext(sim);

            // 1) 状态进入事件。
            var changed = Task04Farm.EventsOfType<UnitStateChangedEvent>(first.Events);
            Assert.That(changed.Count, Is.EqualTo(1),
                "本 Tick 只应有一个状态转换事件；实测 changed=" + changed.Count
                + " events=" + first.Events.Count
                + " states=" + string.Join(",", first.Snapshot.Units
                    .Select(u => u.UnitId + ":" + u.State + "@" + u.StateStartTick + "-" + u.StateEndTick))
                + " names=" + string.Join(",", first.Events.Events.Select(e => e.GetType().Name)));
            Assert.That(changed[0].FromState, Is.EqualTo(UnitState.Idle));
            Assert.That(changed[0].ToState, Is.EqualTo(UnitState.Guarding));
            Assert.That(changed[0].EndTick, Is.EqualTo(4L));

            // 2) 状态进入快照（含半开区间端点与派生资格）。
            UnitSnapshot unit = Task04Farm.UnitOf(first.Snapshot, Task04Farm.EnemyUnitId);
            Assert.That(unit.State, Is.EqualTo((int)UnitState.Guarding));
            Assert.That(unit.StateValue, Is.EqualTo(UnitState.Guarding));
            Assert.That(unit.StateStartTick, Is.EqualTo(0L));
            Assert.That(unit.StateEndTick, Is.EqualTo(4L));
            Assert.That(unit.CanReceiveDirectHit, Is.True, "Guarding 仍是直接命中合格状态");

            // 3) 效果进入快照与事件（EffectId 唯一且出现在规范化快照里）。
            Assert.That(first.Snapshot.Effects.Count, Is.EqualTo(1),
                "Tick 0 施加的效果必须进入快照：effects=" + first.Snapshot.Effects.Count
                + " active=" + sim.ActiveEffectCount
                + " report=" + sim.LastEffectAdvanceReport
                + " events=" + string.Join(",", first.Events.Events.Select(e => e.GetType().Name)));
            StatusEffectSnapshot effect = first.Snapshot.Effects[0];
            Assert.That(effect.EffectId, Is.GreaterThan(0L));
            Assert.That(effect.UnitId, Is.EqualTo(Task04Farm.EnemyUnitId.Value));
            Assert.That(effect.SpecId, Is.EqualTo("status.test_dot"));
            Assert.That(effect.AppliedAtTick, Is.EqualTo(0L));
            Assert.That(effect.DurationTicks, Is.EqualTo(6));
            Assert.That(effect.EndTick, Is.EqualTo(6L));
            Assert.That(first.Snapshot.NextEffectId, Is.EqualTo(effect.EffectId + 1L),
                "下一个 EffectId 必须进入规范化快照");

            var applied = Task04Farm.EventsOfType<StatusEffectAppliedEvent>(first.Events);
            Assert.That(applied.Count, Is.EqualTo(1), "施加事件必须与实例同时可见：applied=" + applied.Count + " effects=" + first.Snapshot.Effects.Count);
            Assert.That(applied[0].EffectId.Value, Is.EqualTo(effect.EffectId),
                "施加事件必须引用快照中的同一实例：applied=" + applied[0].EffectId.Value
                + " snapshot=" + effect.EffectId);
            Assert.That(applied[0].EndTick, Is.EqualTo(effect.EndTick));

            // 4) 效果到期：半开区间 [0, 6) ⇒ Tick 6 到期，**先移除、不额外触发**。
            StepResult second = Task04Farm.StepNext(sim); // Tick 1
            Assert.That(second.Snapshot.NextEffectId, Is.EqualTo(effect.EffectId + 1L), "ID 不回卷");
            Assert.That(second.Snapshot.Effects.Count, Is.EqualTo(1), "Tick 1 效果仍在 [0,6) 内");

            Task04Farm.StepNext(sim); // Tick 2
            Task04Farm.StepNext(sim); // Tick 3
            Task04Farm.StepNext(sim); // Tick 4
            Task04Farm.StepNext(sim); // Tick 5
            StepResult atExpiry = Task04Farm.StepNext(sim); // Tick 6：到期 Tick
            Assert.That(atExpiry.Snapshot.Effects.Count, Is.EqualTo(0),
                "到期后效果必须从快照消失：effects=" + atExpiry.Snapshot.Effects.Count
                + " report=" + sim.LastEffectAdvanceReport);
            Assert.That(atExpiry.Snapshot.NextEffectId, Is.EqualTo(effect.EffectId + 1L));

            // 5) 状态到期：Guarding 在 [0,4) 内有效，Tick 4 回到 Idle。
            UnitSnapshot expiryUnit = Task04Farm.UnitOf(atExpiry.Snapshot, Task04Farm.EnemyUnitId);
            Assert.That(expiryUnit.State, Is.EqualTo((int)UnitState.Idle),
                "状态在 [Start, End) 内有效，到期 Tick 先转换");
            Assert.That(expiryUnit.StateStartTick, Is.EqualTo(4L));
        }

        [Test]
        public void CanonicalSnapshotHashDistinguishesStateAndRemainingDuration()
        {
            // 同一状态、不同剩余时长必须哈希不同（否则"状态进入快照"只是装饰）。
            // 用两个**真实模拟**分别推进到同一 Tick，只有有限状态的结束边界不同。
            LogicSnapshot shorter = GuardingSnapshotAt(4L);
            LogicSnapshot longer = GuardingSnapshotAt(9L);
            LogicSnapshot idle = IdleUntimedSnapshot();

            Assert.That(shorter.ComputeHash(), Is.Not.EqualTo(longer.ComputeHash()),
                "同一状态但不同结束边界必须产生不同摘要（半开区间端点必须参与哈希）");
            Assert.That(idle.ComputeHash(), Is.Not.EqualTo(longer.ComputeHash()),
                "不同状态必须产生不同摘要");

            // 对照证据：三条快照的单位集合完全相同（否则上面的差异可能来自别的字段）。
            Assert.That(shorter.Units.Count, Is.EqualTo(2), "本场战斗固定 2 个单位");
            Assert.That(longer.Units.Count, Is.EqualTo(2));
            Assert.That(idle.Units.Count, Is.EqualTo(2));
            Assert.That(longer.Units.Select(u => u.UnitId).ToArray(),
                Is.EqualTo(shorter.Units.Select(u => u.UnitId).ToArray()),
                "三条快照的单位身份集合必须相同");
            Assert.That(shorter.Effects.Count, Is.EqualTo(longer.Effects.Count),
                "三条快照的效果集合计数必须相同：shorter=" + shorter.Effects.Count
                + " longer=" + longer.Effects.Count + " idle=" + idle.Effects.Count);
            Assert.That(idle.Units[0].UnitId, Is.EqualTo(shorter.Units[0].UnitId));

            // 只有**被脚本转换的那一个单位**的状态族不同：这是差异来源的唯一变量。
            UnitSnapshot shorterEnemy = Task04Farm.UnitOf(shorter, Task04Farm.EnemyUnitId);
            UnitSnapshot longerEnemy = Task04Farm.UnitOf(longer, Task04Farm.EnemyUnitId);
            UnitSnapshot idleEnemy = Task04Farm.UnitOf(idle, Task04Farm.EnemyUnitId);
            Assert.That(shorterEnemy.StateEndTick, Is.EqualTo(4L));
            Assert.That(longerEnemy.StateEndTick, Is.EqualTo(9L));
            Assert.That(idleEnemy.State, Is.EqualTo((int)UnitState.Idle));
            Assert.That(shorterEnemy.State, Is.EqualTo((int)UnitState.Guarding));
            Assert.That(longerEnemy.State, Is.EqualTo((int)UnitState.Guarding));

            // 另一个单位（hero）在三条快照里必须完全一致：差异不是来自它。
            UnitSnapshot shorterHero = Task04Farm.UnitOf(shorter, Task04Farm.HeroUnitId);
            UnitSnapshot longerHero = Task04Farm.UnitOf(longer, Task04Farm.HeroUnitId);
            Assert.That(longerHero.State, Is.EqualTo(shorterHero.State));
            Assert.That(longerHero.StateEndTick, Is.EqualTo(shorterHero.StateEndTick));
        }

        [Test]
        public void EffectSnapshotOrderIsStableAndEffectIdIsHashed()
        {
            // 一次 Step 内施加多个效果：快照集合必须按
            // (UnitId, AppliedAtTick, EffectSequence) 规范排序，且 EffectSequence 单调递增。
            string canonical = EffectsSnapshotCanonicalOrder(new[] { "status.a", "status.b", "status.c" });
            Assert.That(canonical, Is.EqualTo(
                    "status.a@1#0|status.b@1#0|status.c@1#0"),
                "效果快照必须按稳定键升序（AppliedAtTick -> EffectSequence）：" + canonical);

            // 不同效果数量 ⇒ 摘要不同（集合内容真的进入哈希，而不是被忽略）。
            ulong two = EffectsSnapshotHash(new[] { "status.a", "status.b" });
            ulong three = EffectsSnapshotHash(new[] { "status.a", "status.b", "status.c" });
            Assert.That(three, Is.Not.EqualTo(two), "效果数量变化必须改变规范化摘要");

            // 同一装配的两次独立运行必须完全一致（确定性）。
            Assert.That(EffectsSnapshotHash(new[] { "status.a", "status.b" }), Is.EqualTo(two));
        }

        // =====================================================================
        // 状态到期与门禁顺序 / EffectId 与规范化快照
        // =====================================================================

        /// <summary>
        /// 必需测试：<strong>Tick T 的状态自动到期发生在启动门禁之前</strong>。
        ///
        /// 证据形式：把探针挂在阶段 2（"命令前门禁"，当前 Step 里最早的、与门禁同构的判定点），
        /// 记录每次调用时该单位的<strong>权威事实</strong>（当前状态 + <c>BlockingUntilTick</c>）。
        /// 冻结规则要求 Tick T 的到期先发生，因此门禁在 T 看到的必须是"已到期"，
        /// 而 <c>BlockingUntilTick</c> 必须直接来自状态实例的结束边界、不能是"下一 Tick 再试"。
        ///
        /// 这个用例可失败：若到期被推迟到门禁之后（或实现用 <c>StartTick + 1</c> 近似阻塞边界），
        /// Tick 3 的观察会是 <c>Staggered/3</c> 而不是 <c>Idle/null</c>。
        /// </summary>
        [Test]
        public void ControlStateExpiringAtTickIsAdvancedBeforeStartGate()
        {
            // Tick 0 进入 Staggered，有限持续 3 Tick ⇒ 权威阻塞边界 = 0 + 3 = 3（半开区间右端）。
            var stagger = new ControlTransitionAtTick
            {
                Tick = 0L,
                UnitId = Task04Farm.EnemyUnitId.Value,
                Transition = StateTransitionSpec.Timed(UnitState.Staggered, 3, UnitState.Idle)
            };
            var probe = new StartGateStateProbe { UnitId = Task04Farm.EnemyUnitId.Value };
            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: stagger,
                preCommandVictoryGate: probe));
            probe.Simulation = sim;

            StepResult atEntry = Task04Farm.StepNext(sim);   // Tick 0
            Assert.That(probe.Observations.Count, Is.EqualTo(1),
                "门禁每个 Step 必须恰好被调用一次；实测=" + probe.Observations.Count);
            Assert.That(probe.Observations[0], Is.EqualTo("Staggered/3"),
                "门禁看到的阻塞边界必须来自权威结束边界（不是「下一 Tick」）：" + probe.Observations[0]);

            Task04Farm.StepNext(sim);                        // Tick 1
            Task04Farm.StepNext(sim);                        // Tick 2
            Assert.That(probe.Observations[1], Is.EqualTo("Staggered/3"));
            Assert.That(probe.Observations[2], Is.EqualTo("Staggered/3"),
                "半开区间内不得提前放行：" + probe.Observations[2]);

            StepResult atExpiry = Task04Farm.StepNext(sim);  // Tick 3：到期 Tick
            Assert.That(probe.Observations.Count, Is.EqualTo(4));
            Assert.That(probe.Observations[3], Is.EqualTo("Idle/null"),
                "Tick T 的状态到期必须发生在门禁之前（门禁看到的是到期后的事实）："
                + probe.Observations[3]);
            Assert.That(probe.Observations[3], Is.Not.EqualTo("Staggered/3"),
                "对照：到期若排在门禁之后，门禁仍会看到阻塞中的 Staggered");

            // 同 Tick 的到期事件走统一入口、原因码为自动到期。
            var changed = Task04Farm.EventsOfType<UnitStateChangedEvent>(atExpiry.Events);
            Assert.That(changed.Count, Is.EqualTo(1));
            Assert.That(changed[0].Tick, Is.EqualTo(3L));
            Assert.That(changed[0].FromState, Is.EqualTo(UnitState.Staggered));
            Assert.That(changed[0].ToState, Is.EqualTo(UnitState.Idle));
            Assert.That(changed[0].ReasonCode, Is.EqualTo(UnitStateTransitionReasons.AutomaticExpiry),
                "自动到期必须与显式转换走同一入口并发射同类事件");

            // 快照与门禁事实一致：同一 Tick 的快照里阻塞边界已经消失。
            UnitSnapshot unit = Task04Farm.UnitOf(atExpiry.Snapshot, Task04Farm.EnemyUnitId);
            Assert.That(unit.StateValue, Is.EqualTo(UnitState.Idle));
            Assert.That(unit.StateStartTick, Is.EqualTo(3L), "新状态从到期 Tick 开始");
            Assert.That(unit.StateEndTick, Is.EqualTo(long.MaxValue), "Idle 无限持续");
            Assert.That(unit.CanReceiveDirectHit, Is.True);

            // 对照证据：进入 Tick 的快照里阻塞边界是权威值（不是恒 null）。
            UnitSnapshot entryUnit = Task04Farm.UnitOf(atEntry.Snapshot, Task04Farm.EnemyUnitId);
            Assert.That(entryUnit.StateValue, Is.EqualTo(UnitState.Staggered));
            Assert.That(entryUnit.StateEndTick, Is.EqualTo(3L));

            // 阶段顺序对照：推进状态到期的阶段必须严格早于启动门禁阶段。
            // 任务 05 的 StartTick=T 门禁实现于阶段 7；本探针挂在阶段 2，
            // 因此"门禁看到已到期事实"对阶段 7 只会更强（2 早于 7）。
            int advanceIndex = StepPhaseExtensions.IndexOf(StepPhase.StateAndEffectAdvance);
            int probeIndex = StepPhaseExtensions.IndexOf(StepPhase.DeathAndVictory);
            int gateIndex = StepPhaseExtensions.IndexOf(StepPhase.DuePlanStartGateAndReactionTrigger);
            Assert.That(advanceIndex, Is.LessThan(probeIndex),
                "状态到期阶段必须早于命令前门禁阶段");
            Assert.That(probeIndex, Is.LessThan(gateIndex),
                "命令前门禁阶段必须早于启动门禁阶段（探针结论因此覆盖启动门禁）");
            Assert.That(sim.LastStepExecuted(StepPhase.StateAndEffectAdvance), Is.True,
                "对照证据：本 Step 确实执行过状态到期阶段");
        }

        /// <summary>
        /// 必需测试：<strong>EffectId 唯一且出现在规范化快照中</strong>。
        ///
        /// 覆盖三点：① 跨单位、跨配置、跨 Tick 分配的实例身份互不相同且单调递增；
        /// ② 每个身份都出现在该 Tick 的规范化快照里（并且 <c>NextEffectId</c> 一并进入快照）；
        /// ③ 到期移除后身份<strong>不回卷、不复用</strong>，且效果集合的变化确实改变规范化摘要。
        /// </summary>
        [Test]
        public void EffectIdIsUniqueAndAppearsInCanonicalSnapshot()
        {
            var enemyFirst = new ApplyEffectAtTick
            {
                Tick = 0L,
                UnitId = Task04Farm.EnemyUnitId.Value,
                Spec = StatusEffectRuntimeSpec.Timed(new StatusEffectSpecId("status.a"), 4)
            };
            var heroFirst = new ApplyEffectAtTick
            {
                Tick = 0L,
                UnitId = Task04Farm.HeroUnitId.Value,
                Spec = StatusEffectRuntimeSpec.Timed(new StatusEffectSpecId("status.b"), 10)
            };
            var heroSecond = new ApplyEffectAtTick
            {
                Tick = 1L,
                UnitId = Task04Farm.HeroUnitId.Value,
                Spec = StatusEffectRuntimeSpec.Timed(new StatusEffectSpecId("status.c"), 10)
            };

            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: new CompositeAdvanceSystem(enemyFirst, heroFirst, heroSecond)));

            StepResult tick0 = Task04Farm.StepNext(sim);
            Assert.That(tick0.Snapshot.Effects.Count, Is.EqualTo(2),
                "同 Tick 两个单位的两个效果都必须进入快照");
            long firstId = tick0.Snapshot.Effects[0].EffectId;
            long secondId = tick0.Snapshot.Effects[1].EffectId;
            Assert.That(firstId, Is.GreaterThan(0L));
            Assert.That(secondId, Is.Not.EqualTo(firstId), "跨单位分配的 EffectId 必须唯一");
            Assert.That(tick0.Snapshot.NextEffectId, Is.EqualTo(System.Math.Max(firstId, secondId) + 1L),
                "下一个 EffectId 必须进入规范化快照");

            StepResult tick1 = Task04Farm.StepNext(sim);
            Assert.That(tick1.Snapshot.Effects.Count, Is.EqualTo(3));
            var ids = new List<long>();
            for (int i = 0; i < tick1.Snapshot.Effects.Count; i++) ids.Add(tick1.Snapshot.Effects[i].EffectId);
            Assert.That(ids, Is.Unique, "同一快照内的 EffectId 必须互不相同");
            Assert.That(ids, Does.Contain(firstId));
            Assert.That(ids, Does.Contain(secondId), "既有实例身份不得因新增实例而改变");
            Assert.That(tick1.Snapshot.NextEffectId, Is.EqualTo(ids.Max() + 1L));
            Assert.That(tick1.Snapshot.NextEffectId, Is.GreaterThan(tick0.Snapshot.NextEffectId),
                "EffectId 必须单调递增（不回卷）");

            // 同 Tick 对照：只有"是否施加第三个效果"不同 ⇒ 摘要必须不同。
            // 这证明 EffectId 与效果集合真的进入规范化哈希，而不是只存在于内存里。
            var withoutThird = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: new CompositeAdvanceSystem(enemyFirst, heroFirst)));
            Task04Farm.StepNext(withoutThird);                            // Tick 0
            StepResult baselineTick1 = Task04Farm.StepNext(withoutThird); // Tick 1
            Assert.That(baselineTick1.Snapshot.Effects.Count, Is.EqualTo(2));
            Assert.That(baselineTick1.Snapshot.ComputeHash(), Is.Not.EqualTo(tick1.Snapshot.ComputeHash()),
                "同一 Tick、同一单位集合，仅效果集合不同 ⇒ 规范化摘要必须不同");

            Task04Farm.StepNext(sim);   // Tick 2
            Task04Farm.StepNext(sim);   // Tick 3
            StepResult atExpiry = Task04Farm.StepNext(sim);   // Tick 4：status.a 的 [0,4) 到期

            Assert.That(atExpiry.Snapshot.Effects.Count, Is.EqualTo(2),
                "到期 Tick 先移除、不额外保留（半开区间）");
            Assert.That(atExpiry.Snapshot.NextEffectId, Is.EqualTo(tick1.Snapshot.NextEffectId),
                "到期不得回卷或复用 EffectId");
            for (int i = 0; i < atExpiry.Snapshot.Effects.Count; i++)
            {
                Assert.That(atExpiry.Snapshot.Effects[i].EffectId, Is.Not.EqualTo(firstId),
                    "到期实例的身份不得被复用给其他实例");
            }
        }

        // =====================================================================
        // 工具
        // =====================================================================

        private static CommandRequest ScheduleAdd(long targetTick, long expectedScheduleRevision)
            => new CommandRequest(
                targetTick,
                new ScheduleEditScope(expectedScheduleRevision, null),
                new ScheduleEditPayload(new ScheduleEditOperation[]
                {
                    new ScheduleAddOperation(new ActionPlanId(1L), targetTick)
                }));

        private static VictoryDefinition VictoryOf(
            string[] allied, string[] hostile,
            string victory = VictoryCode, string defeat = DefeatCode, string draw = DrawCode)
        {
            var alliedIds = new List<FactionId>();
            for (int i = 0; i < allied.Length; i++) alliedIds.Add(new FactionId(allied[i]));
            var hostileIds = new List<FactionId>();
            for (int i = 0; i < hostile.Length; i++) hostileIds.Add(new FactionId(hostile[i]));
            return new VictoryDefinition(alliedIds, hostileIds, victory, defeat, draw);
        }

        private static int IndexOfFirst<T>(EventBatch batch) where T : LogicEvent
        {
            for (int i = 0; i < batch.Count; i++)
            {
                if (batch.Events[i] is T) return i;
            }
            return -1;
        }

        /// <summary>失败信息用的单位事实转储（只读观察，不参与断言逻辑）。</summary>
        private static string DumpUnits(BattleSimulation sim)
        {
            var builder = new System.Text.StringBuilder("units=");
            for (int i = 0; i < sim.CurrentSnapshot.Units.Count; i++)
            {
                UnitSnapshot unit = sim.CurrentSnapshot.Units[i];
                if (i > 0) builder.Append(',');
                builder.Append(unit.UnitId).Append(':').Append(unit.FactionId)
                       .Append(":alive=").Append(unit.IsAlive)
                       .Append(":hp=").Append(unit.HealthQ10)
                       .Append(":state=").Append(unit.State);
            }
            builder.Append(" ended=").Append(sim.IsEnded)
                   .Append(" notices=").Append(sim.LifecycleNotices.Count)
                   .Append(" effects=").Append(sim.ActiveEffectCount);
            return builder.ToString();
        }

        private static UnitSnapshot Snapshot(long unitId, string factionId, bool alive)
            => new UnitSnapshot(
                unitId, "unit.test", factionId, 0, 0, 0, alive ? 102400 : 0, alive, 0, 0L,
                (int)(alive ? UnitState.Idle : UnitState.Dead),
                0L,
                alive ? long.MaxValue : 0L,
                alive);

        /// <summary>
        /// 用一次 <strong>真实</strong> Step 产生"Guarding，结束边界 = endTick"的快照
        /// （除状态族之外没有任何伪造字段）。
        /// </summary>
        private static LogicSnapshot GuardingSnapshotAt(long endTick)
        {
            var script = new ControlTransitionAtTick
            {
                Tick = 0L,
                UnitId = Task04Farm.EnemyUnitId.Value,
                Transition = StateTransitionSpec.Timed(UnitState.Guarding, (int)endTick, UnitState.Idle)
            };
            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(unitStateAdvance: script));
            return Task04Farm.StepNext(sim).Snapshot;
        }

        /// <summary>用一次真实 Step 产生"无限持续 Idle"的快照（状态族对照）。</summary>
        private static LogicSnapshot IdleUntimedSnapshot()
        {
            var sim = Task04Farm.NewSim();
            return Task04Farm.StepNext(sim).Snapshot;
        }

        /// <summary>
        /// 用一次真实 Step 施加一组效果，返回<strong>规范排序后</strong>的集合内容
        /// （<c>SpecId@UnitId#AppliedAtTick</c> 序列）。
        /// </summary>
        private static string EffectsSnapshotCanonicalOrder(string[] specIds)
        {
            StepResult result = EffectsStep(specIds);
            Assert.That(result.Snapshot.Effects.Count, Is.EqualTo(specIds.Length));

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < result.Snapshot.Effects.Count; i++)
            {
                StatusEffectSnapshot effect = result.Snapshot.Effects[i];
                if (i > 0) builder.Append('|');
                builder.Append(effect.SpecId).Append('@').Append(effect.UnitId)
                       .Append('#').Append(effect.AppliedAtTick);
            }
            return builder.ToString();
        }

        private static ulong EffectsSnapshotHash(string[] specIds)
            => EffectsStep(specIds).Snapshot.ComputeHash();

        private static StepResult EffectsStep(string[] specIds)
        {
            var systems = new IUnitStateAdvanceSystem[specIds.Length];
            for (int i = 0; i < specIds.Length; i++)
            {
                systems[i] = new ApplyEffectAtTick
                {
                    Tick = 0L,
                    UnitId = Task04Farm.EnemyUnitId.Value,
                    Spec = StatusEffectRuntimeSpec.Timed(new StatusEffectSpecId(specIds[i]), 10)
                };
            }

            var sim = Task04Farm.NewSim(new BattleSimulationAssembly(
                unitStateAdvance: new CompositeAdvanceSystem(systems)));
            return Task04Farm.StepNext(sim);
        }
    }
}
