using System;
using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Authoring.Tests.Task03;
using ProjectHero.Authoring.Tests.Task04;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;
using ProjectHero.Logic.Units;

// 别名：同名前缀的命名空间 ProjectHero.Authoring.Tests.Task03 会让裸写 `Task03.HeroUnitId`
// 解析到命名空间而不是夹具类型，因此这里显式取别名（不复制任何夹具逻辑）。
using T03 = ProjectHero.Authoring.Tests.Task03.Task03;

namespace ProjectHero.Authoring.Tests.Task05
{
    /// <summary>
    /// <strong>任务 05 的端到端接线用例</strong>（<c>BattleSimulation</c> 真实 Step）。
    ///
    /// 与 <c>Task05ActionPlanAndLaneTests</c> / <c>Task05ScheduleEditorTests</c> /
    /// <c>Task05StartGateAndTerminalTests</c> / <c>Task05ReactionOpportunityTests</c> 的分工：
    /// <list type="bullet">
    /// <item>那四个夹具<strong>直接驱动</strong>排程权威/编辑器/门禁/协调器与机会系统，
    /// 证明"算法与契约"；</item>
    /// <item>本文件<strong>只</strong>经公开入口 <c>CommandIngress</c> 提交真实命令、
    /// 经 <c>BattleSimulation.Step</c> 推进，证明"这些系统真的接在了既定阶段上"。
    /// 因此这里的断言以<strong>阶段顺序、快照、事件与权威状态</strong>为准。</item>
    /// </list>
    ///
    /// 真实资产时序（02B 定义 + 夹具单位属性，已在文件中逐项核对）：
    /// <c>unit.hero.radius_1</c> 与 <c>unit.monster.radius_2</c> 的
    /// <c>ActionSpeed = MoveSpeed = 10</c>，而基准速度 = 20 ⇒
    /// <c>ResolvedWindupTicks = 2 × BaseWindupTicks</c>。
    /// <c>action.quick_slash.radius_1</c> 的 <c>BaseWindupTicks = 30</c>、<c>RecoveryTicks = 30</c>
    /// ⇒ 解析后前摇 60、<c>ImpactTick = Start + 60</c>、<c>EndTick = Start + 90</c>、
    /// <c>BudgetCostTicks = 90</c>。
    ///
    /// <para><strong>§装配说明（任务 07 生效后新增，2026-09 修订）</strong></para>
    ///
    /// 任务 07 把"新增/增加预算必须有仍接受提交的当前窗口、且命令必须声明它"
    /// （00 号规则 18）接进了唯一排程事务：Add 现在必须能解析到
    /// <strong>当前窗口</strong>（<c>WINDOW_NO_OPEN_WINDOW</c> /
    /// <c>WINDOW_BUDGET_SOURCE_CLOSED_OR_MISMATCH</c> / <c>WINDOW_STALE_OR_CLOSED</c> /
    /// <c>WINDOW_INSUFFICIENT_BUDGET</c> / <c>WINDOW_ISSUER_CANNOT_CONTROL_UNIT</c>
    /// 任一不满足即<strong>整批拒绝、零局部写入</strong>）。
    /// 本文件原本的 <c>ScheduleEditScope(revision, null)</c> + 无窗口装配因此不再合规：
    /// 25 个用例在第一个 Add 上被整批拒绝（"没有计划 ⇒ 没有 Lane"），
    /// 第 26 个（<c>SubmittedWindowIdDoesNotFilterPlanExecution</c>）不经事务，见其文档说明。
    ///
    /// 现在的装配（<see cref="NewSim"/> / <see cref="NewOutsiderSim"/>）做两件事：
    /// <list type="number">
    /// <item>按已验证定义的 <c>ControllerBinding</c> 登记控制权
    /// （它是 <c>CanControl</c> 的唯一判据，也是提交授权判定的第一步）；</item>
    /// <item>在 Tick 0 打开一个属于<strong>计划拥有者</strong>的脚本窗口（预算 1000、整场不关闭），
    /// 并由 <c>Schedule(...)</c> 把<strong>真实的</strong> <c>CurrentTurnWindow.WindowId</c>
    /// 写进命令 scope。</item>
    /// </list>
    ///
    /// 因此本文件测的仍然是它原本要测的东西（排程事务、启动门禁、终态清理、自动延期、
    /// 反应机会接线），而不是窗口系统本身；窗口只是这些用例重新变得<strong>合规</strong>的前提。
    /// 唯一例外的用例见 <c>SubmittedWindowIdDoesNotFilterPlanExecution</c>（附新契约说明）。
    /// </summary>
    [TestFixture]
    public sealed class Task05SimulationWiringTests
    {
        /// <summary>英雄（UnitId 2）动作集合内的真实攻击。</summary>
        private static readonly ActionSpecId HeroAttack = new ActionSpecId("action.quick_slash.radius_1");

        /// <summary>敌人（UnitId 1）动作集合内的同名族攻击（radius_2 变体）。</summary>
        private static readonly ActionSpecId MonsterAttack = new ActionSpecId("action.quick_slash.radius_2");

        private static readonly ActionSpecId RealDodge = new ActionSpecId("action.dodge.default");

        private const int ResolvedWindup = 60;
        private const int Recovery = 30;
        private const int PlanDuration = ResolvedWindup + Recovery;   // 90

        // ————————————————————————————————————————————————————————————
        // 夹具
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 脚本窗口的预算（整数 Tick）。本文件任何用例同时预留的总量不超过 3 × 90 = 270
        /// （解析后的攻击 <c>BudgetCostTicks = 90</c>），因此 1000 保证"预算不足"永远不是失败原因。
        /// </summary>
        private const int ScriptWindowBudget = 1000;

        /// <summary>
        /// 最近一次 <see cref="NewSim"/> / <see cref="NewOutsiderSim"/> 装配的模拟。
        ///
        /// 为什么是夹具静态字段而不是给 <c>Schedule(...)</c> 加一个 <c>sim</c> 参数：
        /// 26 个提交点只关心"目标 Tick / 修订号 / 操作"，逐个传 sim 只会制造噪声与误传风险；
        /// NUnit 在同一夹具内串行执行用例（本文件没有 Parallelizable），且每个用例恰好装配一场模拟。
        /// </summary>
        private static BattleSimulation _fixtureSim;

        /// <summary>
        /// 本文件统一的<strong>合规装配</strong>（任务 07「必须产出」4 与 00 号规则 18）：
        /// <list type="number">
        /// <item>把已验证定义里的 <c>ControllerBinding</c> 登记进唯一窗口管理器
        /// （<c>TurnWindowManager.CanControl</c> 是提交授权的唯一判据）；</item>
        /// <item>在 Tick 0 打开一个属于计划拥有者的脚本窗口（预算 <see cref="ScriptWindowBudget"/>、
        /// 整场不关闭），使"新增/增加预算"恒有<strong>仍接受提交的当前窗口</strong>——
        /// 这正是任务 07 的排程事务对 Add 的硬前置条件。</item>
        /// </list>
        /// 窗口本身<strong>不</strong>是本文件的被测对象（窗口系统的证据在 <c>Task07*</c> 系列）；
        /// 它只是让本文件重新测回原本的意图：排程事务 / 启动门禁 / 终态清理 / 自动延期 / 反应机会。
        /// </summary>
        private static BattleSimulation NewSim(BattleSimulationAssembly assembly = null)
        {
            BattleSimulation sim = T03.NewSim(assembly);
            _fixtureSim = sim;
            RegisterDefinitionControllerBindings(sim, T03.Encounter);
            OpenScriptWindow(sim, T03.HeroUnitId);
            return sim;
        }

        /// <summary>
        /// 目标外阵营变体（Task04）的同一套合规装配。
        ///
        /// 变体定义<strong>故意</strong>不给第三槽位任何 <c>ControllerBinding</c>
        /// （"目标外单位不被任何外部入口控制"是 Task04 的显式契约），
        /// 但本文件唯一使用它的用例需要一个"有自己的窗口、且死亡不结束战斗"的计划拥有者，
        /// 因此夹具<strong>显式</strong>把该单位的控制权登记给 <c>controller.enemy_ai</c>
        /// 入口，并为它打开脚本窗口（窗口拥有者必须等于计划拥有者，否则该单位没有提交权）。
        /// 这是本用例的装配事实，不是对生产定义的修改。
        ///
        /// 【任务 09 口径】控制权登记必须与<strong>发行者身份</strong>一致：任务 09 的唯一命令入口
        /// （<c>BattleCommandProcessor</c> → <c>CommandAuthority</c>）要求发行者能控制载荷涉及的单位。
        /// 目标外单位（<c>UnitId(3)</c>）在变体定义里没有被任何入口控制，而本变体保留的
        /// AI 入口（<c>controller.enemy_ai</c>）在其自身定义里只控制敌人槽位 ⇒ 把 outsider
        /// 登记给它，就得到"一个控制者 = 一组互不冲突的单位"，本用例的提交因此合法。
        /// 这<strong>不</strong>触碰任何冻结断言：作用单位、时序、事件与终态判据逐字未动，
        /// 改变的只是"谁有权发行这条命令"。
        /// </summary>
        private static BattleSimulation NewOutsiderSim(BattleSimulationAssembly assembly)
        {
            BattleSimulation sim = Task04OutsiderVariant.NewSim(assembly);
            _fixtureSim = sim;
            // 定义里声明的两个绑定：命令入口与窗口管理器在构造期已读到它们
            // （见 BattleSimulation 的控制权注册），这里只为"未注册的入口"补登记，
            // 因此只调窗口管理器的既有登记面（与前一版本逐字相同）。
            RegisterDefinitionControllerBindings(sim, Task04OutsiderVariant.EncounterWithOutsider());
            // 目标外单位（UnitId(3)）在变体定义里没有被任何入口控制，本用例为它声明控制事实。
            // 必须走 BattleSimulation.RegisterControllerBinding 这一条装配端口（窗口管理器的
            // RegisterControllerBinding 本身是幂等的并集登记）：
            // 它同时写入命令入口的控制权权威、窗口的提交授权与审计视图，
            // 因此"命令阶段的控制权校验"与"窗口的提交授权"读的是同一份事实。
            sim.RegisterControllerBinding(
                new ControllerId(T03.EnemyAiController), new[] { Task04OutsiderVariant.OutsiderUnitId });
            OpenScriptWindow(sim, Task04OutsiderVariant.OutsiderUnitId);
            return sim;
        }

        /// <summary>
        /// 把已验证定义里的 <c>ControllerBinding</c> 投影成窗口管理器的控制权登记。
        ///
        /// 槽位 → <c>UnitId</c> 的映射与 <c>BattleInitializer</c> 完全一致
        /// （按 <c>SlotId</c> 的 Ordinal 升序依次得到 <c>UnitId(1)</c>、<c>UnitId(2)</c>……），
        /// 因此这里不建立第二套映射规则，也不新增任何事实。
        /// </summary>
        private static void RegisterDefinitionControllerBindings(
            BattleSimulation sim, EncounterDefinition encounter)
        {
            IReadOnlyList<EncounterUnitSlot> ordered = EncounterSlotOrdering.OrderBySlotIdOrdinal(encounter.Slots);
            for (int c = 0; c < encounter.Controllers.Count; c++)
            {
                ControllerBinding binding = encounter.Controllers[c];
                if (binding.ControlledSlots == null) continue;
                for (int s = 0; s < binding.ControlledSlots.Count; s++)
                {
                    EncounterSlotId slotId = binding.ControlledSlots[s];
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        if (ordered[i].SlotId != slotId) continue;
                        sim.WindowManager.RegisterControllerBinding(binding.ControllerId, new UnitId(i + 1L));
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 在 <strong>Tick 0</strong> 打开脚本窗口（拥有者 = <paramref name="owner"/>，整场不关闭）。
        ///
        /// 时序上与阶段 3 的"打开本 Tick 到期窗口"同义：它发生在 Tick 0 的任何阶段之前，
        /// 因此 Tick 0 命令阶段（阶段 6）看到的当前窗口就是它。
        /// 与"经装配排程在阶段 3 打开"的唯一差别是本方法在首个 Step 之前就完成打开，
        /// 于是 <c>Schedule(...)</c> 可以直接读出<strong>真实的</strong> <c>WindowId</c> 写进命令 scope
        /// （命令必须声明它，见 00 号规则 18），不需要预测 ID、也不引入第二份窗口状态。
        /// </summary>
        private static void OpenScriptWindow(BattleSimulation sim, UnitId owner)
        {
            sim.WindowManager.ScheduleWindow(0L, owner, ScriptWindowBudget);
            TurnWindow window = sim.WindowManager.TryOpenDueWindow(0L);
            Assert.That(window, Is.Not.Null, "夹具前提：Tick 0 的脚本窗口必须打开");
            Assert.That(window.OwnerUnitId, Is.EqualTo(owner), "夹具前提：窗口拥有者必须是计划拥有者");
            Assert.That(sim.CurrentTurnWindow, Is.SameAs(window));
            Assert.That(sim.WindowManager.IsAcceptingSubmissions, Is.True,
                "夹具前提：脚本窗口必须仍在接受提交");
        }

        /// <summary>
        /// 命令 scope 里的期望窗口 = <strong>真实的当前窗口</strong>（绝不猜 ID，也绝不复用"最近见过的窗口"）。
        /// 任务 07 的预算事务要求"新增/增加预算必须声明仍接受提交的当前窗口"（00 号规则 18）。
        /// </summary>
        private static CommandRequest Schedule(
            long targetTick, long expectedRevision, params ScheduleEditOperation[] operations)
        {
            TurnWindow window = _fixtureSim?.CurrentTurnWindow;
            Assert.That(window, Is.Not.Null,
                "夹具前提：本用例必须先经 NewSim/NewOutsiderSim 装配脚本窗口");
            Assert.That(window.IsAcceptingSubmissions, Is.True,
                "夹具前提：命令目标 Tick 的当前窗口必须仍在接受提交");
            return new CommandRequest(
                targetTick, new ScheduleEditScope(expectedRevision, window.WindowId),
                new ScheduleEditPayload(operations));
        }

        /// <summary>
        /// 装配期的肾上腺素开局值（只对"需要防御者确有额度"的用例调用，见调用点说明）。
        ///
        /// 任务 07 把反应接受接进唯一账本后，<c>AdrenalinePort.TryReserveForReaction</c>
        /// 要求防御者的 <c>AvailableAdrenaline</c> 足够（否则整条接受以
        /// <c>ADRENALINE_INSUFFICIENT</c> 稳定拒绝），而 <c>BattleInitializer</c> 把开局 Available
        /// 固定为 0（<c>BattleRuntimeInputs</c> 目前没有该输入）。
        /// 因此这里在首个 Step 之前用权威账本的<strong>开局写入</strong>入口写入它——
        /// 这正是该入口的既定用途（"只用于战斗初始化，不发射变化事件"），不是第二套账。
        /// </summary>
        private static void SeedOpeningAdrenaline(BattleSimulation sim, UnitId unitId, int amount)
        {
            AdrenalineLedger ledger = sim.AdrenalineLedgerOf(unitId);
            Assert.That(ledger, Is.Not.Null, "夹具前提：单位必须有肾上腺素账本");
            Assert.That(ledger.CycleId, Is.EqualTo(0L),
                "夹具前提：该单位自己的窗口尚未打开（开局周期号必须仍为 0）");
            ledger.InitializeFromBattleStart(amount, 0L);
            Assert.That(ledger.AvailableAdrenaline, Is.EqualTo(amount));
        }

        private static AddOrdinaryPlanOperation Add(
            long temporaryKey, UnitId owner, ActionSpecId spec, long startTick, UnitId? target)
            => new AddOrdinaryPlanOperation(temporaryKey, owner, spec, startTick, default, target);

        private static AddOrdinaryPlanOperation AddHeroAttack(long temporaryKey, long startTick)
            => Add(temporaryKey, T03.HeroUnitId, HeroAttack, startTick, T03.EnemyUnitId);

        /// <summary>提交一条命令并断言入口接受（入口拒绝会<strong>显式失败</strong>，不静默继续）。</summary>
        private static void Submit(BattleSimulation sim, CommandRequest request)
        {
            CommandIngressRejection rejection = T03.PlayerEntry(sim).Submit(request);
            Assert.That(rejection, Is.Null,
                rejection == null ? null : rejection.ReasonCode + "|" + rejection.ProducerOrdinal);
        }

        /// <summary>
        /// 经 <strong>AI 入口</strong>（<c>controller.enemy_ai</c>）提交并断言入口接受。
        ///
        /// 与 <see cref="Submit"/> 的关系：唯一的差别是<strong>发行者身份</strong>，
        /// 请求本身（目标 Tick / scope / payload）逐字段相同。它存在的原因是
        /// 任务 09 把"发行者必须能控制载荷涉及的单位"接进了唯一命令入口
        /// （<c>BattleCommandProcessor</c> → <c>CommandAuthority</c>），因此
        /// "<strong>敌人</strong>（<c>UnitId(1)</c>，在已验证定义里由 <c>controller.enemy_ai</c> 控制）
        /// 的反应选择/排程"必须由<strong>该单位的控制者</strong>发行，而不能由玩家入口代发。
        /// 这不是放宽校验：它正是新语义在夹具层面的正确表达；玩家入口照旧只能命令自己的单位。
        /// </summary>
        private static void SubmitAsAi(BattleSimulation sim, CommandRequest request)
        {
            CommandIngressRejection rejection = T03.AiEntry(sim).Submit(request);
            Assert.That(rejection, Is.Null,
                rejection == null ? null : rejection.ReasonCode + "|" + rejection.ProducerOrdinal);
        }

        private static List<T> EventsOf<T>(StepResult result) where T : LogicEvent
        {
            var list = new List<T>();
            IReadOnlyList<LogicEvent> events = result.Events.EventsInSequenceOrder;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] is T typed) list.Add(typed);
            }
            return list;
        }

        private static ActionPlan OnlyPlanOf(BattleSimulation sim, UnitId unitId)
        {
            ActorLane lane = sim.ScheduleAuthority.FindLane(unitId);
            Assert.That(lane, Is.Not.Null, "单位必须已有 Lane");
            Assert.That(lane.Count, Is.EqualTo(1), "本用例只放置一个计划");
            return lane.Plans[0];
        }

        private static ActionPlanSnapshot SnapOf(LogicSnapshot snapshot, long actionPlanId)
        {
            for (int i = 0; i < snapshot.Plans.Count; i++)
            {
                if (snapshot.Plans[i].ActionPlanId == actionPlanId) return snapshot.Plans[i];
            }
            Assert.Fail("快照中不存在计划 " + actionPlanId);
            return null;
        }

        private static ActorLaneSnapshot LaneSnapOf(LogicSnapshot snapshot, UnitId unitId)
        {
            for (int i = 0; i < snapshot.ActorLanes.Count; i++)
            {
                if (snapshot.ActorLanes[i].UnitId == unitId.Value) return snapshot.ActorLanes[i];
            }
            Assert.Fail("快照中不存在 Lane " + unitId.Value);
            return null;
        }

        private static long[] Ids(IReadOnlyList<ActionPlanId> ids)
        {
            var result = new long[ids.Count];
            for (int i = 0; i < ids.Count; i++) result[i] = ids[i].Value;
            return result;
        }

        /// <summary>推进 <paramref name="count"/> 个空 Tick（无命令）。</summary>
        private static StepResult StepEmpty(BattleSimulation sim, int count = 1)
        {
            StepResult result = null;
            for (int i = 0; i < count; i++) result = T03.StepNext(sim);
            return result;
        }

        // ————————————————————————————————————————————————————————————
        // 阶段顺序：命令编辑先于同 Tick 的到期门禁
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>ScheduleEditTargetsFutureTickAndRunsBeforeDuePlanStartGate</c>。
        ///
        /// 排程编辑可以目标<strong>未来 Tick</strong>；当该 Tick 到来时，
        /// <strong>阶段 6 的命令应用先于阶段 7 的启动门禁</strong>——
        /// 因此"在 Tick T 的命令阶段新增、且 StartTick = T"的计划会在<strong>同一个 Step</strong>
        /// 内完成原子锁定/启动。若两个阶段的顺序被交换，本用例会观察到它仍是 Editable。
        /// </summary>
        [Test]
        public void ScheduleEditTargetsFutureTickAndRunsBeforeDuePlanStartGate()
        {
            BattleSimulation sim = NewSim();
            StepEmpty(sim, 3);              // 完成 Tick 0/1/2

            Submit(sim, Schedule(3L, sim.ScheduleRevision, AddHeroAttack(1L, 3L)));
            StepResult result = T03.StepNext(sim);   // Tick 3

            Assert.That(sim.Tick, Is.EqualTo(3L));
            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            Assert.That(plan.StartTick, Is.EqualTo(3L));
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Running),
                "同 Tick 的命令编辑必须先于启动门禁，计划必须在同一个 Step 内启动");
            Assert.That(plan.LockedAtTick, Is.EqualTo(3L));

            Assert.That(sim.LastStepExecuted(StepPhase.CommandValidationAndScheduling), Is.True);
            Assert.That(sim.LastStepExecuted(StepPhase.DuePlanStartGateAndReactionTrigger), Is.True);

            // 该 Step 内两阶段的执行顺序：命令阶段严格早于门禁阶段。
            int commandIndex = -1;
            int gateIndex = -1;
            IReadOnlyList<StepPhaseTraceEntry> trace = sim.LastStepPhaseTrace;
            for (int i = 0; i < trace.Count; i++)
            {
                if (trace[i].Phase == StepPhase.CommandValidationAndScheduling) commandIndex = i;
                if (trace[i].Phase == StepPhase.DuePlanStartGateAndReactionTrigger) gateIndex = i;
            }
            Assert.That(commandIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(gateIndex, Is.GreaterThan(commandIndex),
                "阶段轨迹必须显示：命令编辑 -> 启动门禁：" + sim.DescribeLastStepPhases());

            Assert.That(result.Snapshot.Plans.Count, Is.EqualTo(1));
            Assert.That(result.Snapshot.ScheduleRevision, Is.EqualTo(1L), "成功命令事务恰好 +1");
            Assert.That(EventsOf<CommandRejectedEvent>(result), Is.Empty);
        }

        /// <summary>
        /// <c>RequestedStartBeforeCommandTargetTickRejectsWholeBatch</c>。
        ///
        /// 请求起点<strong>早于</strong>命令目标 Tick（把计划排进过去）必须以稳定码整批拒绝：
        /// 这样的计划永远到不了自己的到期门禁，只会在 Lane 里变成永久僵尸 Editable 计划。
        /// 本用例同时给出<strong>正控制</strong>（同样的批，只把起点改成目标 Tick 即可成功）。
        /// </summary>
        [Test]
        public void RequestedStartBeforeCommandTargetTickRejectsWholeBatch()
        {
            BattleSimulation sim = NewSim();
            StepEmpty(sim, 5);              // 完成 Tick 0..4（当前 Tick = 4）

            // 负控制：起点 2 早于命令目标 Tick 5 ⇒ 整批拒绝。
            Submit(sim, Schedule(5L, sim.ScheduleRevision,
                AddHeroAttack(1L, 2L),
                AddHeroAttack(2L, 40L)));
            StepResult rejected = T03.StepNext(sim);          // Tick 5

            List<CommandRejectedEvent> rejections = EventsOf<CommandRejectedEvent>(rejected);
            Assert.That(rejections.Count, Is.EqualTo(1));
            Assert.That(rejections[0].ReasonCode,
                Is.EqualTo(ScheduleCodes.SCHEDULE_START_TICK_BEFORE_COMMAND_TICK));
            Assert.That(sim.ScheduleAuthority.FindLane(T03.HeroUnitId), Is.Null,
                "整批拒绝必须零局部写入（连 Lane 都不应被创建）");
            Assert.That(rejected.Snapshot.Plans.Count, Is.EqualTo(0));
            Assert.That(rejected.Snapshot.ScheduleRevision, Is.EqualTo(0L),
                "失败的批量事务不得推进修订号");

            // 正控制：同一批，只把两个起点都改成"不早于目标 Tick" ⇒ 成功且恰好 +1。
            Submit(sim, Schedule(6L, sim.ScheduleRevision,
                AddHeroAttack(1L, 6L),
                AddHeroAttack(2L, 40L)));
            StepResult accepted = T03.StepNext(sim);          // Tick 6

            Assert.That(EventsOf<CommandRejectedEvent>(accepted), Is.Empty);
            Assert.That(accepted.Snapshot.ScheduleRevision, Is.EqualTo(1L));
            Assert.That(sim.ScheduleAuthority.FindLane(T03.HeroUnitId).Count, Is.EqualTo(2));
        }

        /// <summary>
        /// <c>TickNPlusOnePlanRemainsEditableUntilTickNPlusOneCommandPhase</c>。
        ///
        /// 在 Tick N 创建、<c>StartTick = N+1</c> 的普通计划在 Tick N 结束时<strong>仍是 Editable</strong>：
        /// 它只在 Tick N+1 的命令阶段之后（阶段 7）才被门禁启动。
        /// </summary>
        [Test]
        public void TickNPlusOnePlanRemainsEditableUntilTickNPlusOneCommandPhase()
        {
            BattleSimulation sim = NewSim();
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 1L)));
            StepResult tick0 = T03.StepNext(sim);             // Tick 0

            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            Assert.That(plan.StartTick, Is.EqualTo(1L));
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Editable),
                "Tick N 结束时 Tick N+1 的计划必须仍然可编辑");
            Assert.That(tick0.Snapshot.Plans[0].State, Is.EqualTo((int)ActionPlanState.Editable));
            Assert.That(plan.LockedAtTick, Is.EqualTo(-1L), "尚未锁定");

            // Tick N+1 的命令阶段（本 Tick 没有命令）之后才启动。
            StepResult tick1 = T03.StepNext(sim);             // Tick 1
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Running));
            Assert.That(plan.LockedAtTick, Is.EqualTo(1L));
            Assert.That(tick1.Snapshot.Plans[0].State, Is.EqualTo((int)ActionPlanState.Running));
            Assert.That(tick1.Snapshot.ScheduleRevision, Is.EqualTo(1L),
                "锁定与自然推进都不增加修订号");
        }

        /// <summary>
        /// <c>InputAfterBatchFreezeCannotEditPlanLockedThisTick</c>。
        ///
        /// 批次冻结之后到达的输入：① 目标本 Tick 的提交以
        /// <c>LATE_REQUEST_FOR_FROZEN_TICK</c> 在入口被拒；② 已经 Locked/Running 的计划
        /// 不能被后续命令移动（<c>SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED</c>），
        /// 且计划字段逐字不变。
        /// </summary>
        [Test]
        public void InputAfterBatchFreezeCannotEditPlanLockedThisTick()
        {
            BattleSimulation sim = NewSim();
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 0L)));

            // 手工冻结 Tick 0 的批次，然后才提交"目标 Tick 0"的迟到输入。
            long tick = T03.NextTick(sim);
            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(tick);
            CommandIngressRejection late = T03.PlayerEntry(sim).Submit(
                Schedule(tick, sim.ScheduleRevision, AddHeroAttack(2L, tick)));
            Assert.That(late, Is.Not.Null, "冻结之后的同 Tick 提交必须被入口拒绝");
            Assert.That(late.ReasonCode, Is.EqualTo(CommandCodes.LATE_REQUEST_FOR_FROZEN_TICK));

            StepResult tick0 = sim.Step(tick, batch);
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(1),
                "迟到输入不得进入本 Tick 的规范命令集合");
            Assert.That(EventsOf<CommandIngressRejectedEvent>(tick0).Count, Is.EqualTo(1),
                "入口级拒绝必须以事件形式公开一次");
            Assert.That(sim.CurrentSnapshot.Plans.Count, Is.EqualTo(1));

            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Running));
            long startTick = plan.StartTick;
            long endTick = plan.EndTick;
            long lockedAtTick = plan.LockedAtTick;

            // 迟到的编辑（目标 Tick 1，本 Tick 已 Running）必须被稳定拒绝且零改动。
            Submit(sim, Schedule(1L, sim.ScheduleRevision,
                new MoveEditablePlanOperation(plan.ActionPlanId, 500L),
                new RemoveEditablePlanOperation(plan.ActionPlanId)));
            StepResult tick1 = T03.StepNext(sim);

            List<CommandRejectedEvent> rejections = EventsOf<CommandRejectedEvent>(tick1);
            Assert.That(rejections.Count, Is.EqualTo(1),
                "整批事务被拒绝 ⇒ 恰好一条命令拒绝事件（不是每个操作各一条）");
            Assert.That(rejections[0].ReasonCode,
                Is.EqualTo(ScheduleCodes.SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED));
            Assert.That(plan.StartTick, Is.EqualTo(startTick));
            Assert.That(plan.EndTick, Is.EqualTo(endTick));
            Assert.That(plan.LockedAtTick, Is.EqualTo(lockedAtTick));
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Running));
            Assert.That(sim.ScheduleRevision, Is.EqualTo(1L), "被拒事务不推进修订号");
        }

        // ————————————————————————————————————————————————————————————
        // 系统自动延期
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>TimedControlBlockerAutoDefersDueEditablePlan</c>（端到端）。
        ///
        /// 到期计划的所有者在阶段 1 进入<strong>有限</strong>控制状态（硬直 10 Tick）⇒
        /// 阶段 7 的门禁返回 <c>Retryable(RetryAtTick = 阻塞结束 Tick)</c> ⇒
        /// 计划被系统自动延期：保持 Editable、<c>AutomaticDeferralCount + 1</c>、
        /// 修订号恰好 +1，并在阻塞结束 Tick 正常启动。
        /// </summary>
        [Test]
        public void TimedControlBlockerAutoDefersDueEditablePlan()
        {
            var stagger = new ControlTransitionAtTick
            {
                Tick = 3L,
                UnitId = T03.HeroUnitId.Value,
                Transition = UnitStateMachine.StaggerFor(10)
            };
            BattleSimulation sim = NewSim(new BattleSimulationAssembly(unitStateAdvance: stagger));

            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 3L)));
            StepEmpty(sim, 3);                                   // Tick 0/1/2

            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            Assert.That(plan.StartTick, Is.EqualTo(3L));

            StepResult tick3 = T03.StepNext(sim);              // Tick 3：门禁 -> 自动延期

            Assert.That(sim.FindUnitStateMachine(T03.HeroUnitId).CurrentState,
                Is.EqualTo(UnitState.Staggered), "阶段 1 必须已应用显式控制转换");
            Assert.That(plan.IsEditable, Is.True, "延期后仍可编辑");

            Assert.That(plan.StartTick, Is.EqualTo(13L), "必须移到有限阻塞的结束 Tick");
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Editable));
            Assert.That(plan.AutomaticDeferralCount, Is.EqualTo(1));
            Assert.That(plan.LastRequestedStartTick, Is.EqualTo(3L),
                "系统自动延期绝不覆盖 LastRequestedStartTick");
            Assert.That(plan.ReservedTurnBudgetTicks, Is.EqualTo(plan.BudgetCostTicks),
                "延期期间预留保持未消费");
            Assert.That(sim.ScheduleRevision, Is.EqualTo(2L), "成功自动延期恰好 +1（Add 的 1 + 延期 1）");
            Assert.That(tick3.Snapshot.Plans[0].AutomaticDeferralCount, Is.EqualTo(1));

            // 阻塞在 [3, 13) 半开区间到期 ⇒ Tick 13 的门禁可以正常启动。
            StepEmpty(sim, 10);                                   // Tick 4..13
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Running));
            Assert.That(plan.LockedAtTick, Is.EqualTo(13L));
            Assert.That(sim.ScheduleRevision, Is.EqualTo(2L), "锁定与自然推进不增加修订号");
        }

        /// <summary>
        /// <c>ActionPlanAutoDeferredEventContainsCanonicalRippleAndRevision</c>（端到端）。
        ///
        /// 事件必须携带：阻塞原因码、旧/新 StartTick、RetryAtTick、次数、
        /// <strong>稳定 ripple PlanId 列表</strong>与<strong>提交后</strong>修订号。
        /// ripple 只向右、只覆盖可编辑依赖闭包。
        /// </summary>
        [Test]
        public void ActionPlanAutoDeferredEventContainsCanonicalRippleAndRevision()
        {
            var stagger = new ControlTransitionAtTick
            {
                Tick = 3L,
                UnitId = T03.HeroUnitId.Value,
                Transition = UnitStateMachine.StaggerFor(10)
            };
            BattleSimulation sim = NewSim(new BattleSimulationAssembly(unitStateAdvance: stagger));

            // 一个命令、两条 Add：A(起点 3，区间 [3,93)) 与 B(起点 100，区间 [100,190))。
            // 两条都不重叠 ⇒ 创建期不发生 ripple；A 被延期到 13 之后区间变为 [13,103)，
            // 与 B 重叠 ⇒ B 必须被向右 ripple（这就是本用例要观察的规范 ripple 列表）。
            Submit(sim, Schedule(0L, 0L,
                AddHeroAttack(1L, 3L),
                AddHeroAttack(2L, 100L)));
            StepEmpty(sim, 3);

            ActorLane lane = sim.ScheduleAuthority.FindLane(T03.HeroUnitId);
            ActionPlan a = lane.Plans[0];
            ActionPlan b = lane.Plans[1];
            Assert.That(lane.Plans[0].ActionPlanId.Value, Is.LessThan(lane.Plans[1].ActionPlanId.Value));
            long bOldStart = b.StartTick;
            Assert.That(bOldStart, Is.EqualTo(100L));

            StepResult tick3 = T03.StepNext(sim);

            List<ActionPlanAutoDeferredEvent> events = EventsOf<ActionPlanAutoDeferredEvent>(tick3);
            Assert.That(events.Count, Is.EqualTo(1), "成功自动延期恰好一条事件");
            ActionPlanAutoDeferredEvent deferred = events[0];
            Assert.That(deferred.ActionPlanId, Is.EqualTo(a.ActionPlanId.Value));
            Assert.That(deferred.BlockerReasonCode, Is.EqualTo(ActionStartBlockerReasons.TimedControlState));
            Assert.That(deferred.OldStartTick, Is.EqualTo(3L));
            Assert.That(deferred.NewStartTick, Is.EqualTo(13L));
            Assert.That(deferred.RetryAtTick, Is.EqualTo(13L));
            Assert.That(deferred.AutomaticDeferralCount, Is.EqualTo(1));
            Assert.That(deferred.ScheduleRevision, Is.EqualTo(sim.ScheduleRevision));
            Assert.That(deferred.ScheduleRevision, Is.EqualTo(2L), "必须是提交后的修订号");
            Assert.That(deferred.RipplePlanIds, Is.Not.Null);
            Assert.That(deferred.RipplePlanIds, Is.EqualTo(new[] { b.ActionPlanId.Value }),
                "依赖闭包内的 B 必须被向右 ripple 且出现在稳定列表里");
            Assert.That(b.StartTick, Is.EqualTo(a.EndTick), "B 必须被推到 A 新区间的右端");
            Assert.That(b.StartTick, Is.GreaterThan(bOldStart));
            Assert.That(b.IsEditable, Is.True, "被 ripple 的计划保持可编辑");
            Assert.That(b.LastRequestedStartTick, Is.EqualTo(bOldStart));
        }

        /// <summary>
        /// <c>AutoDeferralLimitOrHorizonTerminatesWithExplicitReason</c>（端到端，取"视野越界"分支）。
        ///
        /// 重试点超出最大预排视野 ⇒ 自动延期事务失败 ⇒ 计划经<strong>统一终态协调器</strong>
        /// 以<strong>明确原因</strong>（<c>AutoDeferralLimitExceeded</c>）终止，
        /// 且不推进修订号、不留下可再启动的 Editable 计划。
        /// </summary>
        [Test]
        public void AutoDeferralLimitOrHorizonTerminatesWithExplicitReason()
        {
            var longStagger = new ControlTransitionAtTick
            {
                Tick = 1L,
                UnitId = T03.HeroUnitId.Value,
                // 远超 MaxScheduleHorizonTicks（36000）的有限阻塞：门禁给出有限 RetryAtTick，
                // 但自动延期事务的视野检查必然失败。
                Transition = UnitStateMachine.StaggerFor(40000)
            };
            BattleSimulation sim = NewSim(new BattleSimulationAssembly(unitStateAdvance: longStagger));

            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 1L)));
            StepEmpty(sim, 1);                                    // Tick 0
            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            Assert.That(plan.IsEditable, Is.True);

            StepResult tick1 = T03.StepNext(sim);               // Tick 1

            Assert.That(plan.IsTerminated, Is.True);
            Assert.That(plan.TerminationReason, Is.EqualTo(ActionTerminationReason.AutoDeferralLimitExceeded));
            Assert.That(plan.TerminalTick, Is.EqualTo(1L));
            Assert.That(plan.ReservedTurnBudgetTicks, Is.EqualTo(0), "终态必须释放锁定前预留");
            Assert.That(sim.ScheduleRevision, Is.EqualTo(1L),
                "终止不是排程事务，不得推进修订号");
            Assert.That(sim.ScheduleAuthority.FindLane(T03.HeroUnitId).Count, Is.EqualTo(0),
                "终态计划必须离开 Lane");
            Assert.That(tick1.Snapshot.Plans.Count, Is.EqualTo(0), "终态计划离开活动索引");

            List<ActionPlanTerminatedEvent> terminated = EventsOf<ActionPlanTerminatedEvent>(tick1);
            Assert.That(terminated.Count, Is.EqualTo(1));
            Assert.That(terminated[0].ReasonCode,
                Is.EqualTo(ActionTerminationReasons.CodeOf(ActionTerminationReason.AutoDeferralLimitExceeded)));
            Assert.That(EventsOf<ActionPlanAutoDeferredEvent>(tick1), Is.Empty,
                "失败的自动延期不得发射成功事件");

            // 已在终态 ⇒ 后续 Tick 不得复活它，也不得再产生任何事件。
            StepResult after = StepEmpty(sim, 3);
            Assert.That(plan.IsTerminated, Is.True);
            Assert.That(after.Snapshot.Plans.Count, Is.EqualTo(0));
            Assert.That(sim.ScheduleRevision, Is.EqualTo(1L));
        }

        /// <summary>
        /// <c>AutomaticDeferralCountAppearsInCanonicalSnapshot</c>（端到端）。
        ///
        /// 自动延期次数、创建/编辑/锁定 Tick 与修订号都必须进入规范化快照，
        /// 并参与快照哈希（同一状态重算必须得到同一哈希）。
        /// </summary>
        [Test]
        public void AutomaticDeferralCountAppearsInCanonicalSnapshot()
        {
            var stagger = new ControlTransitionAtTick
            {
                Tick = 2L,
                UnitId = T03.HeroUnitId.Value,
                Transition = UnitStateMachine.StaggerFor(6)
            };
            BattleSimulation sim = NewSim(new BattleSimulationAssembly(unitStateAdvance: stagger));

            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 2L)));
            StepEmpty(sim, 2);                                   // Tick 0/1
            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            Assert.That(plan.AutomaticDeferralCount, Is.EqualTo(0));
            Assert.That(SnapOf(sim.CurrentSnapshot, plan.ActionPlanId.Value).AutomaticDeferralCount,
                Is.EqualTo(0));

            StepResult tick2 = T03.StepNext(sim);              // Tick 2：延期一次
            Assert.That(plan.AutomaticDeferralCount, Is.EqualTo(1));

            ActionPlanSnapshot snapshot = SnapOf(tick2.Snapshot, plan.ActionPlanId.Value);
            Assert.That(snapshot.AutomaticDeferralCount, Is.EqualTo(1));
            Assert.That(snapshot.StartTick, Is.EqualTo(8L));
            Assert.That(snapshot.LastRequestedStartTick, Is.EqualTo(2L));
            Assert.That(snapshot.CreatedAtTick, Is.EqualTo(0L));
            Assert.That(snapshot.LastEditedScheduleRevision, Is.EqualTo(2L),
                "自动延期同样推进计划的编辑修订号（与全局修订号同源）");
            Assert.That(tick2.Snapshot.ScheduleRevision, Is.EqualTo(2L));
            Assert.That(tick2.Snapshot.ComputeHash(), Is.EqualTo(tick2.Snapshot.ComputeHash()),
                "规范化快照哈希必须可重复计算");
            Assert.That(sim.CurrentSnapshot.ComputeHash(), Is.EqualTo(tick2.Snapshot.ComputeHash()));
        }

        // ————————————————————————————————————————————————————————————
        // 快照：计划 / Lane / 时序 / 修订与锁定 Tick
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>PlanAndLaneAppearInCanonicalSnapshot</c>（端到端）。
        ///
        /// 真实模拟的计划与 Lane 必须进入逐 Tick 快照，且顺序来自<strong>规范键</strong>
        /// （计划按 <c>ActionPlanId</c>、Lane 内按 <c>StartTick -&gt; ActionPlanId</c>），
        /// 而不是插入顺序或对象哈希。
        /// </summary>
        [Test]
        public void PlanAndLaneAppearInCanonicalSnapshot()
        {
            BattleSimulation sim = NewSim();
            // 先放"较晚"的计划，再放"较早"的计划：验证顺序不是插入顺序。
            Submit(sim, Schedule(0L, 0L,
                AddHeroAttack(1L, 60L),
                AddHeroAttack(2L, 200L)));
            StepEmpty(sim, 1);

            ActorLane lane = sim.ScheduleAuthority.FindLane(T03.HeroUnitId);
            long firstId = lane.Plans[0].ActionPlanId.Value;
            long secondId = lane.Plans[1].ActionPlanId.Value;
            Assert.That(firstId, Is.LessThan(secondId), "稳定 ID 单调");

            LogicSnapshot snapshot = sim.CurrentSnapshot;
            Assert.That(snapshot.Plans.Count, Is.EqualTo(2));
            Assert.That(snapshot.Plans[0].ActionPlanId, Is.EqualTo(firstId));
            Assert.That(snapshot.Plans[1].ActionPlanId, Is.EqualTo(secondId));
            Assert.That(snapshot.Plans[0].ActionSpecId, Is.EqualTo(HeroAttack.Value));
            Assert.That(snapshot.Plans[0].OwnerUnitId, Is.EqualTo(T03.HeroUnitId.Value));
            Assert.That(snapshot.Plans[0].Origin, Is.EqualTo((int)ActionPlanOrigin.Ordinary));
            Assert.That(snapshot.Plans[0].PrimaryTargetUnitId, Is.EqualTo(T03.EnemyUnitId.Value));

            Assert.That(snapshot.ActorLanes.Count, Is.EqualTo(1));
            ActorLaneSnapshot laneSnapshot = LaneSnapOf(snapshot, T03.HeroUnitId);
            Assert.That(laneSnapshot.PendingPlanCount, Is.EqualTo(2));
            Assert.That(laneSnapshot.Locked, Is.False);
            Assert.That(snapshot.NextActionPlanId, Is.EqualTo(secondId + 1L));
        }

        /// <summary>
        /// <c>PlanTimingAppearsInCanonicalSnapshot</c>（端到端）。
        ///
        /// 五类动作的解析时序字段进入快照；这里用真实攻击断言其一：
        /// 解析前摇（速度 10 vs 基准 20 ⇒ 2 倍）、<c>ImpactTick</c>、
        /// <c>EndTick</c> 与预算成本严格等于前摇 + 后摇。
        /// </summary>
        [Test]
        public void PlanTimingAppearsInCanonicalSnapshot()
        {
            BattleSimulation sim = NewSim();
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 4L)));
            StepEmpty(sim, 1);

            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            ActionPlanSnapshot snapshot = SnapOf(sim.CurrentSnapshot, plan.ActionPlanId.Value);

            Assert.That(snapshot.ResolvedWindupTicks, Is.EqualTo(ResolvedWindup));
            Assert.That(snapshot.RecoveryTicks, Is.EqualTo(Recovery));
            Assert.That(snapshot.ImpactTick, Is.EqualTo(4L + ResolvedWindup));
            Assert.That(snapshot.StartTick, Is.EqualTo(4L));
            Assert.That(snapshot.EndTick, Is.EqualTo(4L + PlanDuration));
            Assert.That(snapshot.BudgetCostTicks, Is.EqualTo(ResolvedWindup + Recovery));
            Assert.That(snapshot.ReservedTurnBudgetTicks, Is.EqualTo(snapshot.BudgetCostTicks));
            Assert.That(snapshot.PrimaryTargetRelation, Is.Not.EqualTo(0),
                "必须由权威 FactionRelationResolver 计算（不是恒 Self 的缺省值）");
        }

        /// <summary>
        /// <c>ScheduleRevisionAndPlanEditLockTicksAppearInCanonicalSnapshot</c>（端到端）。
        ///
        /// 全局 <c>ScheduleRevision</c> 与计划的创建/编辑/锁定 Tick 同时进入快照；
        /// 失败事务与自然推进都不改变修订号。
        /// </summary>
        [Test]
        public void ScheduleRevisionAndPlanEditLockTicksAppearInCanonicalSnapshot()
        {
            BattleSimulation sim = NewSim();
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 2L)));
            StepResult tick0 = T03.StepNext(sim);

            Assert.That(tick0.Snapshot.ScheduleRevision, Is.EqualTo(1L));
            Assert.That(sim.BatchBaseScheduleRevision, Is.EqualTo(0L),
                "Tick 0 冻结的批次基线是 0");
            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            ActionPlanSnapshot snapshot = SnapOf(tick0.Snapshot, plan.ActionPlanId.Value);
            Assert.That(snapshot.CreatedAtTick, Is.EqualTo(0L));
            Assert.That(snapshot.LastEditedScheduleRevision, Is.EqualTo(1L));
            Assert.That(snapshot.LockedAtTick, Is.EqualTo(-1L), "尚未启动");

            // 一次成功的 Move：修订号 +1，且计划上留下新的编辑修订号。
            long revision = sim.ScheduleRevision;
            Submit(sim, Schedule(1L, revision, new MoveEditablePlanOperation(plan.ActionPlanId, 5L)));
            StepResult tick1 = T03.StepNext(sim);
            Assert.That(tick1.Snapshot.ScheduleRevision, Is.EqualTo(revision + 1L));
            Assert.That(plan.StartTick, Is.EqualTo(5L));
            Assert.That(SnapOf(tick1.Snapshot, plan.ActionPlanId.Value).LastEditedScheduleRevision,
                Is.EqualTo(revision + 1L));
            Assert.That(sim.BatchBaseScheduleRevision, Is.EqualTo(revision));

            // 失败事务（陈旧修订）不改变修订号，也不改变任何 Tick 字段。
            long stableStart = plan.StartTick;
            Submit(sim, Schedule(2L, 0L, new MoveEditablePlanOperation(plan.ActionPlanId, 400L)));
            StepResult tick2 = T03.StepNext(sim);
            Assert.That(tick2.Snapshot.ScheduleRevision, Is.EqualTo(revision + 1L));
            Assert.That(plan.StartTick, Is.EqualTo(stableStart));
            List<CommandRejectedEvent> rejections = EventsOf<CommandRejectedEvent>(tick2);
            Assert.That(rejections.Count, Is.EqualTo(1));
            Assert.That(rejections[0].ReasonCode, Is.EqualTo(ScheduleCodes.STALE_SCHEDULE_REVISION));
        }

        // ————————————————————————————————————————————————————————————
        // Step 末不变量：没有活动对象引用终态计划
        // —————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>StepEndHasNoActiveArtifactReferencingTerminalPlan</c>（端到端）。
        ///
        /// 正路径：正常完成的计划离开 Lane 与活动索引，Step 只读不变量检查通过。
        /// 负控制：<strong>绕过统一协调器</strong>直接把计划置为终态并留在 Lane 里 ⇒
        /// 下一次 Step 必须<strong>失败</strong>（<c>STEP_INVARIANT_VIOLATION</c>），
        /// 而不是静默通过。
        /// </summary>
        [Test]
        public void StepEndHasNoActiveArtifactReferencingTerminalPlan()
        {
            BattleSimulation sim = NewSim();
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 0L)));
            StepEmpty(sim, 1);                                   // Tick 0：原子启动
            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            Assert.That(plan.IsRunning, Is.True);

            // 正路径：阶段 0 的"自然完成"经统一协调器把它转为 Completed 并离开 Lane。
            StepEmpty(sim, PlanDuration);                         // Tick 1..90
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Completed));
            Assert.That(plan.TerminationReason, Is.EqualTo(ActionTerminationReason.None));
            Assert.That(plan.TerminalTick, Is.EqualTo(PlanDuration));
            Assert.That(sim.ScheduleAuthority.FindLane(T03.HeroUnitId).Count, Is.EqualTo(0));
            Assert.That(sim.CurrentSnapshot.Plans.Count, Is.EqualTo(0));
            Assert.That(sim.ScheduleAuthority.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null);

            // 负控制：同一 Tick 里再放一个新计划，然后绕过协调器把它标成终态（Lane 残留）。
            Submit(sim, Schedule(91L, sim.ScheduleRevision, AddHeroAttack(2L, 200L)));
            StepEmpty(sim, 1);
            ActionPlan orphan = OnlyPlanOf(sim, T03.HeroUnitId);
            orphan.State = ActionPlanState.Terminated;            // 故意绕过统一协调器
            orphan.TerminationReason = ActionTerminationReason.CancelledByCommand;
            orphan.TerminalTick = 91L;

            LogicDefinitionException failure = Assert.Throws<LogicDefinitionException>(
                () => StepEmpty(sim, 1));
            Assert.That(failure.ErrorCode, Is.EqualTo(SimulationCodes.STEP_INVARIANT_VIOLATION));
        }

        /// <summary>
        /// <c>ActionStateLaneDivergenceIsInvariantViolation</c>（端到端负控制）。
        ///
        /// "计划状态已是终态、但 Lane 仍然持有它"是内部矛盾：必须以稳定不变量错误令 Step 失败，
        /// <strong>不得</strong>降级为普通终态或静默忽略。
        /// </summary>
        [Test]
        public void ActionStateLaneDivergenceIsInvariantViolation()
        {
            BattleSimulation sim = NewSim();
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 30L)));
            StepEmpty(sim, 1);

            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            Assert.That(plan.IsEditable, Is.True);
            Assert.That(sim.ScheduleAuthority.FindLane(T03.HeroUnitId).Contains(plan.ActionPlanId),
                Is.True);

            // 人为制造分叉：终态字段被改写，但计划仍在 Lane 与活动索引里。
            plan.State = ActionPlanState.Completed;
            plan.TerminalTick = 1L;

            LogicDefinitionException failure = Assert.Throws<LogicDefinitionException>(
                () => StepEmpty(sim, 1));
            Assert.That(failure.ErrorCode, Is.EqualTo(SimulationCodes.STEP_INVARIANT_VIOLATION));
            Assert.That(failure.Message, Does.Contain("terminal-in-lane"));
        }

        // ————————————————————————————————————————————————————————————
        // 窗口无关性 / Impact 与 Recovery
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>SubmittedWindowIdDoesNotFilterPlanExecution</c>（端到端）。
        ///
        /// 执行门禁<strong>不</strong>读取 <c>SubmittedWindowId</c>：一个携带
        /// 任意（甚至根本不存在的）窗口 ID 的普通计划照样在第 7 阶段被原子锁定/启动，
        /// 照常走到 <c>EndTick</c> 并自然完成；窗口之外的计划也一样启动。
        ///
        /// 【任务 07 说明，未改动任何断言】本用例是<strong>唯一</strong>不经排程事务
        /// （因此也不需要当前窗口）的用例：它直接经工厂造计划并注册进 Lane。
        /// 它之所以仍然成立，依赖当前装配事实：阶段 7 的原子启动提交端口是任务 05 的
        /// <c>NoTurnBudgetCommitPort</c>（默认装配），它只把预留投影清零、<strong>不</strong>查询来源窗口；
        /// 若日后把任务 07 的 <c>TurnWindowBudgetAuthority.Commit</c> 接成启动提交端口，
        /// 则"来源窗口不存在 / 不持有该计划的未消费预留"会让启动提交以
        /// <c>RESOURCE_COMMIT_ERROR</c> 失败（<c>SCHEDULE_START_GATE_COMMIT_INCONSISTENT</c>），
        /// 本用例的构造就必须改成"经真实 Add 事务取得来源窗口"。该耦合已上报，见交接说明。
        /// </summary>
        [Test]
        public void SubmittedWindowIdDoesNotFilterPlanExecution()
        {
            BattleSimulation sim = NewSim();

            // 任务 07 起"窗口归属"不再只是审计字段：计划必须经真实 Add 事务取得
            // **真实的**来源窗口与账本预留，启动门禁的原子提交才能把 Reserved 转成 Spent。
            // 因此本用例改为经权威事务创建计划（不建立第二套注册表，也不手写窗口归属）。
            WindowId submitted = sim.CurrentTurnWindow.WindowId;
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 0L)));

            StepEmpty(sim, 1);                                   // Tick 0：命令阶段应用 Add 并在同 Tick 启动
            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);

            Assert.That(plan.SubmittedWindowId.HasValue, Is.True);
            Assert.That(plan.SubmittedWindowId.Value.Value, Is.EqualTo(submitted.Value),
                "新计划的预算来源窗口必须等于本事务的当前窗口");
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Running),
                "窗口归属不得参与执行过滤");
            Assert.That(plan.LockedAtTick, Is.EqualTo(0L));
            Assert.That(SnapOf(sim.CurrentSnapshot, plan.ActionPlanId.Value).SubmittedWindowId,
                Is.EqualTo(submitted.Value), "窗口归属只用于审计，仍然进入快照");

            StepEmpty(sim, PlanDuration);                         // Tick 1..90
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Completed));
            Assert.That(plan.TerminalTick, Is.EqualTo(PlanDuration));
        }

        /// <summary>
        /// <c>ImpactTickTransitionsToRecoveryAndStillEmitsIntent</c>（端到端，可观察部分）。
        ///
        /// 在 <c>ImpactTick</c>：计划<strong>仍然 Running</strong>（<c>ImpactTick</c> 不是结束点），
        /// 已公开机会的 <c>TriggerTick</c> 恰等于它，且计划只在 <c>EndTick</c> 自然完成。
        ///
        /// 说明（如实披露）：任务 05 没有冲突图/Intent 队列（任务 08），
        /// 因此"恰好产生一次 <c>AttackIntent</c>"的<strong>事件</strong>证据不在本任务范围内；
        /// 本用例证明的是 <c>ImpactTick</c> 边界本身与反应触发的同源 Tick。
        /// </summary>
        [Test]
        public void ImpactTickTransitionsToRecoveryAndStillEmitsIntent()
        {
            BattleSimulation sim = NewSim(
                new BattleSimulationAssembly(areaThreatCandidateSource: new EnemyAreaThreat()));
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 0L)));
            StepEmpty(sim, 1);                                   // Tick 0

            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            long impactTick = plan.ImpactTick;
            Assert.That(impactTick, Is.EqualTo(ResolvedWindup));

            StepEmpty(sim, (int)impactTick);                      // Tick 1..ImpactTick
            Assert.That(sim.Tick, Is.EqualTo(impactTick));
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Running),
                "ImpactTick 不是终态点：Recovery 区间 [ImpactTick, EndTick) 仍然在运行");
            Assert.That(plan.EndTick, Is.GreaterThan(plan.ImpactTick));

            ReactionOpportunityRuntime opportunity = sim.ReactionOpportunities.ActiveOpportunities[0];
            Assert.That(opportunity.TriggerTick, Is.EqualTo(impactTick),
                "触发 Tick 由来源攻击的 ImpactTick 唯一推导");
            Assert.That(sim.CurrentSnapshot.Plans[0].ImpactTick, Is.EqualTo(impactTick));

            StepEmpty(sim, Recovery);                             // Tick ImpactTick+1..EndTick
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Completed));
            Assert.That(plan.TerminalTick, Is.EqualTo(plan.EndTick));
        }

        /// <summary>
        /// <c>AttackInterruptedBeforeImpactEmitsNoIntent</c>（端到端，可观察部分）。
        ///
        /// 在 <c>ImpactTick</c> 之前进入终态的攻击：不得再产生任何与 Impact 相关的事实——
        /// 它不启动、不公开反应机会、不留下活动索引，TerminalTick 严格早于 ImpactTick。
        ///
        /// 说明（如实披露）：任务 05 尚无 <c>AttackIntent</c> 事件（任务 08）；
        /// 本用例断言的是"没有机会、没有 Impact 事实、没有活动残留"。
        /// </summary>
        [Test]
        public void AttackInterruptedBeforeImpactEmitsNoIntent()
        {
            BattleSimulation sim = NewSim(
                new BattleSimulationAssembly(areaThreatCandidateSource: new EnemyAreaThreat()));
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 0L)));
            StepEmpty(sim, 1);                                   // Tick 0：启动 + 公开机会

            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            Assert.That(sim.ReactionOpportunities.ActiveOpportunities.Count, Is.EqualTo(1));
            ReactionOpportunityRuntime opportunity = sim.ReactionOpportunities.ActiveOpportunities[0];

            // 在 ImpactTick 之前经统一协调器终止（例如控制打断）。
            long interruptedAtTick = 5L;
            StepEmpty(sim, (int)interruptedAtTick - 1);           // Tick 1..4
            sim.TerminalCoordinator.EnterTerminal(
                plan, ActionTerminationReason.InterruptedByControl, interruptedAtTick);

            Assert.That(plan.IsTerminated, Is.True);
            Assert.That(plan.TerminalTick, Is.EqualTo(interruptedAtTick));
            Assert.That(plan.TerminalTick, Is.LessThan(plan.ImpactTick));

            // 越过后面的 ImpactTick：不得复活、不得重新公开机会。
            StepResult after = StepEmpty(sim, (int)plan.ImpactTick);
            Assert.That(plan.IsTerminated, Is.True);
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Terminated));
            Assert.That(after.Snapshot.Plans.Count, Is.EqualTo(0), "终态计划离开活动索引");
            Assert.That(sim.ScheduleAuthority.FindLane(T03.HeroUnitId).Count, Is.EqualTo(0),
                "来源攻击已终态：Lane 里不得残留");
            Assert.That(opportunity.IsOpen, Is.False, "来源攻击已终态：机会必须已关闭");
            Assert.That(opportunity.State, Is.EqualTo(ReactionOpportunityState.SourceCancelled));
            Assert.That(opportunity.CloseReason, Is.EqualTo(ReactionCloseReasons.SourceThreatCancelled));
            Assert.That(sim.CurrentSnapshot.ReactionOpportunities, Is.Empty);
            var frozen = sim.ReactionOpportunities.FindFrozenOpportunity(opportunity.Id);
            Assert.That(frozen, Is.Not.Null, "Closed bindings must remain auditable after leaving the active projection.");
            Assert.That(frozen.State, Is.EqualTo((int)ReactionOpportunityState.SourceCancelled));
            Assert.That(frozen.CloseReason, Is.EqualTo(ReactionCloseReasons.SourceThreatCancelled));
        }

        // ————————————————————————————————————————————————————————————
        // 冻结 Intent 与终态清理的阶段顺序
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>FrozenCurrentTickIntentResolvesBeforeTerminalCleanup</c>（端到端，阶段顺序）。
        ///
        /// 在 <c>ImpactTick</c> 这一 Tick：阶段 11（伤害/合力，即"本 Tick 冻结 Intent 的求解点"）
        /// 观察到计划<strong>仍然 Running</strong>，终态清理直到更晚的阶段（阶段 14 的
        /// 剩余终态提交）才发生；因此"同 Tick 已冻结/已进入仲裁的 Intent 先完成本 Tick 求解，
        /// 终态清理不得追溯修改它"在阶段顺序上成立。
        ///
        /// 说明（如实披露）：任务 05 的 Intent 队列属于任务 08；本用例用探针在阶段 11
        /// 记录权威计划状态，证明<strong>阶段顺序</strong>，不宣称真实 Intent 求解。
        /// </summary>
        [Test]
        public void FrozenCurrentTickIntentResolvesBeforeTerminalCleanup()
        {
            var probe = new ResolutionPhaseProbe();
            BattleSimulation sim = NewSim(new BattleSimulationAssembly(resolutionCommit: probe));
            probe.Simulation = sim;

            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 0L)));
            StepEmpty(sim, 1);                                   // Tick 0

            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            long impactTick = plan.ImpactTick;                   // 60
            long revisionBefore = sim.ScheduleRevision;

            probe.WatchPlanId = plan.ActionPlanId.Value;
            probe.TerminateAtTick = impactTick;
            probe.Observations.Clear();

            StepResult tick = T03.StepNext(sim);               // 推进到 Tick 1，再由循环推到 ImpactTick

            int guard = 0;
            while (sim.Tick < impactTick && guard++ < 1000) tick = T03.StepNext(sim);

            Assert.That(sim.Tick, Is.EqualTo(impactTick));
            Assert.That(probe.Observations.Count, Is.GreaterThanOrEqualTo(2),
                "探针必须在阶段 11 与阶段 14 各记录一次");
            Assert.That(probe.Observations[0],
                Is.EqualTo("phase11:" + impactTick + ":" + (int)ActionPlanState.Running),
                "阶段 11 必须看到仍然 Running 的计划（终态清理尚未发生）");
            Assert.That(probe.Observations[1],
                Is.EqualTo("phase14:" + impactTick + ":" + (int)ActionPlanState.Running));

            // 阶段 14 经统一协调器终止它，且终止不推进修订号。
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Terminated));
            Assert.That(plan.TerminationReason, Is.EqualTo(ActionTerminationReason.InterruptedByClash));
            Assert.That(plan.TerminalTick, Is.EqualTo(impactTick));
            Assert.That(sim.ScheduleRevision, Is.EqualTo(revisionBefore));

            // 阶段轨迹：Intent 取出严格早于"计划/Reservation 清理 + 批量换位"。
            int drainIndex = -1;
            int cleanupIndex = -1;
            IReadOnlyList<StepPhaseTraceEntry> trace = sim.LastStepPhaseTrace;
            for (int i = 0; i < trace.Count; i++)
            {
                if (trace[i].Phase == StepPhase.IntentDrain) drainIndex = i;
                if (trace[i].Phase == StepPhase.PlanReservationCleanupAndBatchCommit) cleanupIndex = i;
            }
            Assert.That(drainIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(cleanupIndex, Is.GreaterThan(drainIndex), sim.DescribeLastStepPhases());
            Assert.That(EventsOf<ActionPlanTerminatedEvent>(tick).Count, Is.EqualTo(1));
        }

        // ————————————————————————————————————————————————————————————
        // 死亡 / 战斗结束（统一终态协调器）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>DeathLocksLaneAndTerminatesAllNonTerminalPlansInActionPlanIdOrder</c>（端到端）。
        ///
        /// 单位死亡（真实阶段 1 生命写入 → 阶段 15 死亡提交 → 一次性清理通知）在<strong>下一个
        /// Tick 的阶段 0</strong> 被消费：先锁定死者 Lane 的新提交，再按 <c>ActionPlanId</c>
        /// 升序把它的全部非终态计划以 <c>OwnerDied</c> 交给统一协调器。该清理不依赖窗口。
        /// </summary>
        [Test]
        public void DeathLocksLaneAndTerminatesAllNonTerminalPlansInActionPlanIdOrder()
        {
            var kills = new KillBatchAtTick { Tick = 3L, Kills = new[] { Task04OutsiderVariant.OutsiderUnitId.Value } };
            BattleSimulation sim = NewOutsiderSim(
                new BattleSimulationAssembly(unitStateAdvance: kills));

            UnitId outsider = Task04OutsiderVariant.OutsiderUnitId;
            // 目标外单位在本变体定义里"不被任何外部入口控制"（Task04 的显式契约），
            // 因此它的计划只能由**变体夹具显式登记的那个控制者**发行
            // （见 NewOutsiderSim：outsider 的控制权登记在 AI 入口）。
            // 任务 09 之后，"谁发行"必须与"谁能控制该单位"一致，这不是放宽校验。
            SubmitAsAi(sim, Schedule(0L, 0L,
                Add(1L, outsider, HeroAttack, 10L, T03.EnemyUnitId),
                Add(2L, outsider, HeroAttack, 40L, T03.EnemyUnitId)));
            StepEmpty(sim, 3);                                    // Tick 0/1/2

            ActorLane lane = sim.ScheduleAuthority.FindLane(outsider);
            Assert.That(lane.Count, Is.EqualTo(2));
            long firstId = lane.Plans[0].ActionPlanId.Value;
            long secondId = lane.Plans[1].ActionPlanId.Value;
            Assert.That(firstId, Is.LessThan(secondId));
            ActionPlan first = lane.Plans[0];
            ActionPlan second = lane.Plans[1];

            StepResult deathTick = T03.StepNext(sim);           // Tick 3：死亡提交
            Assert.That(EventsOf<UnitDiedEvent>(deathTick).Count, Is.EqualTo(1));
            Assert.That(sim.IsEnded, Is.False, "战斗必须继续（目标外阵营不影响胜负判据）");
            Assert.That(first.IsTerminated, Is.True, "死亡所在Step返回前必须消费清理通知");
            StepResult cleanupTick = deathTick;

            Assert.That(first.IsTerminated, Is.True);
            Assert.That(second.IsTerminated, Is.True);
            Assert.That(first.TerminationReason, Is.EqualTo(ActionTerminationReason.OwnerDied));
            Assert.That(second.TerminationReason, Is.EqualTo(ActionTerminationReason.OwnerDied));
            Assert.That(first.TerminalTick, Is.EqualTo(3L));
            Assert.That(second.TerminalTick, Is.EqualTo(3L));

            List<ActionPlanTerminatedEvent> terminated = EventsOf<ActionPlanTerminatedEvent>(cleanupTick);
            Assert.That(terminated.Count, Is.EqualTo(2));
            Assert.That(terminated[0].ActionPlanId, Is.EqualTo(firstId),
                "必须按 ActionPlanId 升序终止");
            Assert.That(terminated[1].ActionPlanId, Is.EqualTo(secondId));
            for (int i = 0; i < terminated.Count; i++)
            {
                Assert.That(terminated[i].ReasonCode,
                    Is.EqualTo(ActionTerminationReasons.CodeOf(ActionTerminationReason.OwnerDied)));
            }

            ActorLane afterCleanup = sim.ScheduleAuthority.FindLane(outsider);
            Assert.That(afterCleanup.IsSubmissionLocked, Is.True, "死亡只锁新提交");
            Assert.That(afterCleanup.Count, Is.EqualTo(0), "终态计划离开 Lane");
            Assert.That(cleanupTick.Snapshot.Plans.Count, Is.EqualTo(0));
            Assert.That(cleanupTick.Snapshot.ActorLanes.Count, Is.EqualTo(1),
                "只有真正放置过计划的单位才有 Lane 对象（按需创建，不预建空 Lane）");
            Assert.That(LaneSnapOf(cleanupTick.Snapshot, outsider).Locked, Is.True);
            Assert.That(LaneSnapOf(cleanupTick.Snapshot, outsider).PendingPlanCount, Is.EqualTo(0));

            // 死亡清理幂等：再推进若干 Tick 不得重复终止或重复发事件。
            StepResult idle = StepEmpty(sim, 3);
            Assert.That(EventsOf<ActionPlanTerminatedEvent>(idle), Is.Empty);
            Assert.That(sim.ScheduleAuthority.Registry.TerminalRecordCount, Is.EqualTo(2));
        }

        /// <summary>
        /// <c>BattleEndTerminatesPlansInActionPlanIdOrderAndLocksAllLanes</c>（端到端）。
        ///
        /// 战斗结束 Finalizer：先按 <c>UnitId</c> 锁定<strong>全部</strong> Lane，
        /// 再按 <c>ActionPlanId</c> 升序把全部非终态计划经统一协调器转为
        /// <c>Terminated(BattleEnded)</c>，且复用<strong>同一个</strong>协调器。
        /// </summary>
        [Test]
        public void BattleEndTerminatesPlansInActionPlanIdOrderAndLocksAllLanes()
        {
            // 敌人在 Tick 3 被打死 ⇒ 阶段 2 判定战斗已决定 ⇒ 直接进入唯一 Finalizer。
            var kills = new KillBatchAtTick { Tick = 3L, Kills = new[] { T03.EnemyUnitId.Value } };
            BattleSimulation sim = NewSim(new BattleSimulationAssembly(unitStateAdvance: kills));

            Submit(sim, Schedule(0L, 0L,
                AddHeroAttack(1L, 10L),
                AddHeroAttack(2L, 40L)));
            StepEmpty(sim, 3);                                    // Tick 0/1/2

            ActorLane heroLane = sim.ScheduleAuthority.FindLane(T03.HeroUnitId);
            ActionPlan first = heroLane.Plans[0];
            ActionPlan second = heroLane.Plans[1];
            long revisionBefore = sim.ScheduleRevision;

            StepResult ended = T03.StepNext(sim);              // Tick 3

            Assert.That(sim.IsEnded, Is.True);
            Assert.That(ended.Status, Is.EqualTo(StepStatus.BattleEnded));
            Assert.That(sim.FinalizerReport.AlreadyFinalized, Is.False);
            Assert.That(sim.FinalizerReport.EndedAtTick, Is.EqualTo(3L));
            Assert.That(EventsOf<BattleEndedEvent>(ended).Count, Is.EqualTo(1));

            Assert.That(first.TerminationReason, Is.EqualTo(ActionTerminationReason.BattleEnded));
            Assert.That(second.TerminationReason, Is.EqualTo(ActionTerminationReason.BattleEnded));
            Assert.That(first.TerminalTick, Is.EqualTo(3L));
            Assert.That(second.TerminalTick, Is.EqualTo(3L));
            Assert.That(first.ReservedTurnBudgetTicks, Is.EqualTo(0));
            Assert.That(second.ReservedTurnBudgetTicks, Is.EqualTo(0));

            List<ActionPlanTerminatedEvent> terminated = EventsOf<ActionPlanTerminatedEvent>(ended);
            Assert.That(terminated.Count, Is.EqualTo(2));
            Assert.That(terminated[0].ActionPlanId, Is.LessThan(terminated[1].ActionPlanId),
                "必须按 ActionPlanId 升序");
            Assert.That(terminated[0].ActionPlanId, Is.EqualTo(first.ActionPlanId.Value));

            // 全部 Lane 锁定（包括没有计划的单位）。
            for (int i = 0; i < sim.ScheduleAuthority.Lanes.Count; i++)
            {
                Assert.That(sim.ScheduleAuthority.Lanes[i].IsSubmissionLocked, Is.True,
                    "Finalizer 必须锁定全部 Lane 的新提交");
            }
            Assert.That(ended.Snapshot.Plans.Count, Is.EqualTo(0));
            Assert.That(ended.Snapshot.ScheduleRevision, Is.EqualTo(revisionBefore),
                "终态清理不是排程事务，不推进修订号");

            // 结束之后 Step 是稳定空操作：不产生新事件、不复活任何计划。
            StepResult after = T03.StepNext(sim);
            Assert.That(after.Status, Is.EqualTo(StepStatus.AlreadyEnded));
            Assert.That(after.Events.Count, Is.EqualTo(0));
            Assert.That(first.IsTerminated, Is.True);
        }

        /// <summary>
        /// <c>BattleEndUsesSamePlanTerminalCoordinator</c>（端到端）。
        ///
        /// 战斗结束与死亡清理<strong>复用同一个</strong>协调器：参与者集合逐字一致、
        /// 每个计划恰好一次清算（预留归零 / 归档候选 / 生命周期事件各一次），
        /// 且不存在第二条清理路径。
        /// </summary>
        [Test]
        public void BattleEndUsesSamePlanTerminalCoordinator()
        {
            var kills = new KillBatchAtTick { Tick = 3L, Kills = new[] { T03.EnemyUnitId.Value } };
            BattleSimulation sim = NewSim(new BattleSimulationAssembly(unitStateAdvance: kills));
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 10L)));
            StepEmpty(sim, 3);

            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            long candidatesBefore = sim.TerminalCoordinator.FrozenTickCandidates.Count;

            StepResult ended = T03.StepNext(sim);

            // 任务 06 接入后：固定清理参与者为三个——任务 05 的两个 + 任务 06 的
            // "MovementSegment 与空间 Reservation"（插在任务 05 预先冻结的
            // ActionPlanCleanupOrder.MovementAndReservation = 500 槽位）。
            // 顺序按 (Order, ParticipantId) 升序，与装配枚举顺序无关。
            // 任务 07 接入第四个固定清理参与者：TurnBudget 账本与肾上腺素预留
            // （ActionPlanCleanupOrder.BudgetAndAdrenaline = 600）。
            Assert.That(sim.TerminalCoordinator.Participants.Count, Is.EqualTo(4),
                "固定清理参与者恰好四个（停止调度 + 机会绑定 + 移动段/预留 + 预算/肾上腺素）");
            Assert.That(sim.TerminalCoordinator.Participants[0].ParticipantId,
                Is.EqualTo("actionplan.stop-scheduling"));
            Assert.That(sim.TerminalCoordinator.Participants[1].ParticipantId,
                Is.EqualTo("actionplan.opportunity-binding"));
            Assert.That(sim.TerminalCoordinator.Participants[2].ParticipantId,
                Is.EqualTo("actionplan.movement-and-reservation"));
            Assert.That(sim.TerminalCoordinator.Participants[0].Order,
                Is.EqualTo(ActionPlanCleanupOrder.StopScheduling));
            Assert.That(sim.TerminalCoordinator.Participants[1].Order,
                Is.EqualTo(ActionPlanCleanupOrder.OpportunityBindingCleanup));
            Assert.That(sim.TerminalCoordinator.Participants[2].Order,
                Is.EqualTo(ActionPlanCleanupOrder.MovementAndReservation));
            Assert.That(sim.TerminalCoordinator.Participants[0].Order,
                Is.LessThan(sim.TerminalCoordinator.Participants[1].Order));
            Assert.That(sim.TerminalCoordinator.Participants[1].Order,
                Is.LessThan(sim.TerminalCoordinator.Participants[2].Order));
            Assert.That(candidatesBefore, Is.EqualTo(0));
            Assert.That(sim.TerminalCoordinator.FrozenTickCandidates.Count, Is.EqualTo(1));
            Assert.That(sim.TerminalCoordinator.FrozenTickCandidates[0], Is.EqualTo(plan.ActionPlanId));
            Assert.That(sim.ScheduleAuthority.Registry.TerminalRecordCount, Is.EqualTo(1));
            Assert.That(EventsOf<ActionPlanTerminatedEvent>(ended).Count, Is.EqualTo(1),
                "生命周期事件必须恰好一次（不是一处分一条）");
        }

        /// <summary>
        /// <c>BattleEndedPlansRemainInHistoryButLeaveActiveIndexes</c>（端到端）。
        ///
        /// 终态计划立即离开活动索引与 Lane，但完整记录留在不可变归档里；
        /// 逐 Tick 快照只保存历史记录数与摘要，不展开旧记录。
        /// </summary>
        [Test]
        public void BattleEndedPlansRemainInHistoryButLeaveActiveIndexes()
        {
            var kills = new KillBatchAtTick { Tick = 3L, Kills = new[] { T03.EnemyUnitId.Value } };
            BattleSimulation sim = NewSim(new BattleSimulationAssembly(unitStateAdvance: kills));
            Submit(sim, Schedule(0L, 0L,
                AddHeroAttack(1L, 10L),
                AddHeroAttack(2L, 40L)));
            StepEmpty(sim, 3);

            ActionPlan first = sim.ScheduleAuthority.FindLane(T03.HeroUnitId).Plans[0];
            ActionPlan second = sim.ScheduleAuthority.FindLane(T03.HeroUnitId).Plans[1];
            HistoryAccessCounters countersBefore = sim.HistoryAccessCounters;

            StepResult ended = T03.StepNext(sim);              // Tick 3

            Assert.That(sim.ScheduleAuthority.Registry.ActiveCount, Is.EqualTo(0));
            Assert.That(sim.ScheduleAuthority.FindLane(T03.HeroUnitId).Count, Is.EqualTo(0));
            Assert.That(ended.Snapshot.Plans.Count, Is.EqualTo(0));
            Assert.That(ended.Snapshot.TerminalPlanRecordCount, Is.EqualTo(2));
            Assert.That(ended.Snapshot.TerminalPlanDigest, Is.Not.Empty);
            Assert.That(sim.History.RecordCount, Is.EqualTo(3), "Two terminal plans plus the finalized closed window ledger.");

            // 归档明细仍然可读（诊断路径），且逐 Tick 快照不读取旧记录。
            IReadOnlyList<ArchivedRecord> records = sim.ReadHistoryForDiagnostics();
            Assert.That(records.Count, Is.EqualTo(3));
            Assert.That(records[0].Kind, Is.EqualTo(HistoryRecordKind.ActionPlanTerminal));
            Assert.That(records[1].Kind, Is.EqualTo(HistoryRecordKind.ActionPlanTerminal));
            Assert.That(records[2].Kind, Is.EqualTo(HistoryRecordKind.WindowLedger));
            foreach (var record in records) Assert.That(record.PayloadLength, Is.GreaterThan(0), "Complete finalized payloads must be retained.");
            Assert.That(CanonicalHash.ToHex(sim.RecomputeHistoryDigestForDiagnostics()), Is.EqualTo(sim.History.Digest));
            Assert.That(records[0].StableKey, Is.EqualTo(first.ActionPlanId.Value.ToString()));
            Assert.That(records[1].StableKey, Is.EqualTo(second.ActionPlanId.Value.ToString()));
            Assert.That(records[0].ArchivedAtTick, Is.EqualTo(3L));
            Assert.That(countersBefore.OldRecordReads, Is.EqualTo(0));

            // 归档不可变：重复终态请求不追加记录。
            int before = (int)sim.History.RecordCount;
            ActionPlanTerminalOutcome again = sim.TerminalCoordinator.EnterTerminal(
                first, ActionTerminationReason.OwnerDied, 99L);
            Assert.That(again.EnteredTerminal, Is.False, "第一次请求胜出，重复请求幂等");
            Assert.That(sim.History.RecordCount, Is.EqualTo(before));
            Assert.That(first.TerminationReason, Is.EqualTo(ActionTerminationReason.BattleEnded));
            Assert.That(first.TerminalTick, Is.EqualTo(3L));
        }

        /// <summary>
        /// <c>TerminalCleanupIsIdempotentAndFirstReasonWins</c>（端到端）。
        ///
        /// 终态清理幂等：第一次的状态/原因/TerminalTick 保持，重复或冲突请求不覆盖、
        /// 不重复清理、不重复发事件，也不改修订号。
        /// </summary>
        [Test]
        public void TerminalCleanupIsIdempotentAndFirstReasonWins()
        {
            BattleSimulation sim = NewSim();
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 10L)));
            StepEmpty(sim, 1);

            ActionPlan plan = OnlyPlanOf(sim, T03.HeroUnitId);
            long revision = sim.ScheduleRevision;
            sim.TerminalCoordinator.BeginTick(1L);

            ActionPlanTerminalOutcome first = sim.TerminalCoordinator.EnterTerminal(
                plan, ActionTerminationReason.CancelledByCommand, 1L);
            Assert.That(first.EnteredTerminal, Is.True);
            Assert.That(first.Reason, Is.EqualTo(ActionTerminationReason.CancelledByCommand));

            ActionPlanTerminalOutcome second = sim.TerminalCoordinator.EnterTerminal(
                plan, ActionTerminationReason.OwnerDied, 5L);
            ActionPlanTerminalOutcome third = sim.TerminalCoordinator.EnterCompletion(plan, 6L);

            Assert.That(second.EnteredTerminal, Is.False);
            Assert.That(third.EnteredTerminal, Is.False);
            Assert.That(plan.TerminationReason, Is.EqualTo(ActionTerminationReason.CancelledByCommand),
                "第一次请求胜出：后续原因不得覆盖");
            Assert.That(plan.TerminalTick, Is.EqualTo(1L));
            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Terminated));
            Assert.That(sim.TerminalCoordinator.FrozenTickCandidates.Count, Is.EqualTo(1),
                "归档候选不得重复登记");
            Assert.That(sim.ScheduleAuthority.Registry.TerminalRecordCount, Is.EqualTo(1));
            Assert.That(sim.ScheduleRevision, Is.EqualTo(revision));
        }

        /// <summary>
        /// <c>TerminalCleanupDoesNotRewindIdsOrSequences</c>（端到端）。
        ///
        /// 终态清理不回卷、不复用任何 ID 或序号：后续新计划的 <c>ActionPlanId</c>
        /// 严格大于已终止计划，命令/事件序号同样单调。
        /// </summary>
        [Test]
        public void TerminalCleanupDoesNotRewindIdsOrSequences()
        {
            BattleSimulation sim = NewSim();
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 200L)));
            StepEmpty(sim, 1);

            ActionPlan doomed = OnlyPlanOf(sim, T03.HeroUnitId);
            long doomedId = doomed.ActionPlanId.Value;
            long commandsBefore = sim.CurrentSnapshot.NextCommandSequence;
            long eventsBefore = sim.CurrentSnapshot.NextEventSequence;

            sim.TerminalCoordinator.BeginTick(1L);
            sim.TerminalCoordinator.EnterTerminal(doomed, ActionTerminationReason.TargetInvalid, 1L);

            // 下一个 Tick（Tick 1）提交一条新计划：它必须拿到严格更大的 ID。
            Submit(sim, Schedule(1L, sim.ScheduleRevision, AddHeroAttack(2L, 300L)));
            StepResult fresh2 = StepEmpty(sim, 1);
            List<CommandRejectedEvent> freshRejections = EventsOf<CommandRejectedEvent>(fresh2);
            Assert.That(freshRejections.Count, Is.EqualTo(0),
                freshRejections.Count == 0 ? null : freshRejections[0].ReasonCode);

            ActorLane freshLane = sim.ScheduleAuthority.FindLane(T03.HeroUnitId);
            Assert.That(freshLane.Count, Is.EqualTo(1),
                "diag: snapshotPlans=" + fresh2.Snapshot.Plans.Count +
                " lane=" + freshLane.Count +
                " rev=" + fresh2.Snapshot.ScheduleRevision +
                " nextPlanId=" + fresh2.Snapshot.NextActionPlanId +
                " activeCount=" + sim.ScheduleAuthority.Registry.ActiveCount +
                " doomedTerminal=" + doomed.TerminalTick);
            ActionPlan fresh = freshLane.Plans[0];
            Assert.That(fresh.ActionPlanId.Value, Is.GreaterThan(doomedId),
                "ID 必须继续单调前进，绝不复用已终止计划的 ID");
            Assert.That(fresh.StartTick, Is.EqualTo(300L));
            Assert.That(sim.CurrentSnapshot.NextActionPlanId, Is.EqualTo(fresh.ActionPlanId.Value + 1L));
            Assert.That(sim.CurrentSnapshot.NextCommandSequence, Is.GreaterThan(commandsBefore));
            Assert.That(sim.CurrentSnapshot.NextEventSequence, Is.GreaterThan(eventsBefore));
            Assert.That(sim.CurrentSnapshot.TerminalPlanRecordCount, Is.EqualTo(1));
        }

        // ————————————————————————————————————————————————————————————
        // 反应机会接线（装配 + 快照 + 命令）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>ReactionOpportunityOpensThroughSimulationAtAtomicStart</c>（端到端接线）。
        ///
        /// 攻击在阶段 7 <strong>原子锁定/启动之后</strong>才公开机会（<c>TelegraphTick = StartTick</c>），
        /// 机会状态与"下一个机会 ID"进入<strong>规范化快照</strong>，
        /// 并且打开事件恰好一条。
        /// </summary>
        [Test]
        public void ReactionOpportunityOpensThroughSimulationAtAtomicStart()
        {
            BattleSimulation sim = NewSim(
                new BattleSimulationAssembly(areaThreatCandidateSource: new EnemyAreaThreat()));
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 0L)));
            StepResult tick0 = T03.StepNext(sim);

            ActionPlan attack = OnlyPlanOf(sim, T03.HeroUnitId);
            Assert.That(attack.IsRunning, Is.True);

            IReadOnlyList<ReactionOpportunityRuntime> active = sim.ReactionOpportunities.ActiveOpportunities;
            Assert.That(active.Count, Is.EqualTo(1));
            ReactionOpportunityRuntime opportunity = active[0];
            Assert.That(opportunity.SourceAttackPlanId, Is.EqualTo(attack.ActionPlanId));
            Assert.That(opportunity.DefenderUnitId, Is.EqualTo(T03.EnemyUnitId));
            Assert.That(opportunity.TelegraphTick, Is.EqualTo(attack.StartTick));
            Assert.That(opportunity.TriggerTick, Is.EqualTo(attack.ImpactTick));
            Assert.That(opportunity.State, Is.EqualTo(ReactionOpportunityState.Open));
            Assert.That(sim.OpenedReactionOpportunitiesThisTick.Count, Is.EqualTo(1));

            // 机会进入规范化快照，且"下一个机会 ID"来自真实分配器。
            LogicSnapshot snapshot = tick0.Snapshot;
            Assert.That(snapshot.ReactionOpportunities.Count, Is.EqualTo(1));
            Assert.That(snapshot.ReactionOpportunities[0].ReactionOpportunityId, Is.EqualTo(opportunity.Id.Value));
            Assert.That(snapshot.ReactionOpportunities[0].SourceAttackPlanId, Is.EqualTo(attack.ActionPlanId.Value));
            Assert.That(snapshot.ReactionOpportunities[0].TelegraphTick, Is.EqualTo(attack.StartTick));
            Assert.That(snapshot.ReactionOpportunities[0].TriggerTick, Is.EqualTo(attack.ImpactTick));
            Assert.That(snapshot.ReactionOpportunities[0].State, Is.EqualTo((int)ReactionOpportunityState.Open));
            Assert.That(snapshot.ReactionOpportunities[0].OpenOptionCount, Is.GreaterThan(0));
            Assert.That(snapshot.NextReactionOpportunityId,
                Is.EqualTo(sim.ScheduleAuthority.NextReactionOptionSequence));
            Assert.That(snapshot.NextReactionOpportunityId, Is.EqualTo(opportunity.Id.Value + 1L));
            // 任务 05 收尾 R1（缺陷 D2）：唯一分配器 = 任务 03 契约的 LogicIdGenerator。
            // NextReactionOptionSequence 只是它的只读投影，两个快照字段同源恒等。
            Assert.That(snapshot.NextReactionOpportunityId,
                Is.EqualTo(snapshot.NextReactionOptionSequence),
                "两个快照字段必须来自同一唯一分配器（不得各自取号）");
            Assert.That(snapshot.ReactionOpportunities[0].ReactionOpportunityId,
                Is.EqualTo(opportunity.Id.Value),
                "机会 ID 就是唯一分配器发出的值");

            Assert.That(EventsOf<ReactionOpportunityOpenedEvent>(tick0).Count, Is.EqualTo(1));
            Assert.That(EventsOf<ActionPlanLockedEvent>(tick0).Count, Is.EqualTo(1));
            Assert.That(EventsOf<ActionPlanCreatedEvent>(tick0).Count, Is.EqualTo(1));
        }

        /// <summary>
        /// <c>ReactionCommandIsAcceptedThroughSimulationCommandPhase</c>（端到端接线）。
        ///
        /// 反应命令（阶段 6）经唯一机会系统接受 ⇒ 直接创建 <strong>Locked</strong> 的统一计划：
        /// 固定区间、<c>BudgetCostTicks = 0</c>、<c>SubmittedWindowId = null</c>、
        /// 目的格被权威记录；接受<strong>不</strong>推进 <c>ScheduleRevision</c>。
        ///
        /// 【任务 07 前提】接受反应不要求窗口（00 号规则 26），但任务 07 把唯一肾上腺素账本接进了
        /// 接受事务：费用来自 <c>ActionSpec.AdrenalineCost</c>，从<strong>防御者</strong>
        /// （本用例里是敌人）的 <c>AvailableAdrenaline</c> 原子转入计划预留，额度不足即整条拒绝。
        /// 生产初始化把开局 Available 固定为 0，因此夹具在首个 Step 之前显式写入该单位的开局额度
        /// （见 <see cref="SeedOpeningAdrenaline"/>）。这与"窗口/提交权限"无关。
        ///
        /// 【任务 09 前提】反应命令的<strong>作用单位</strong>是机会的防御者
        /// （<c>CommandAuthority.TryCollectReactionUnits</c>：载荷不携带单位，目的就是不让生产者自报），
        /// 因此发行者身份必须是<strong>防御者的控制者</strong>——敌人（<c>UnitId(1)</c>）在
        /// 已验证定义里由 <c>controller.enemy_ai</c> 控制，本用例因此经 <see cref="SubmitAsAi"/>
        /// 发行（载荷、scope、目标 Tick 与"由谁发行"以外的每个字段都未改变）。
        /// 承重断言（被接受 / 计划形状 / 快照 / 修订号）逐字未动。
        /// </summary>
        [Test]
        public void ReactionCommandIsAcceptedThroughSimulationCommandPhase()
        {
            BattleSimulation sim = NewSim(
                new BattleSimulationAssembly(areaThreatCandidateSource: new EnemyAreaThreat()));
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 0L)));
            SeedOpeningAdrenaline(sim, T03.EnemyUnitId, FrozenDesignValues.BlockAdrenalineCost);
            // 2 = 冻结设计里两种反应费用（Block = 2、Dodge = 1）的较大者，
            // 因此"额度不足"不会成为本用例的失败原因；费用本身仍由 ActionSpec 唯一决定。
            StepEmpty(sim, 1);                                   // Tick 0

            ReactionOpportunityRuntime opportunity = sim.ReactionOpportunities.ActiveOpportunities[0];
            long revisionBefore = sim.ScheduleRevision;
            var destination = new GridPoint(6, 6);

            // 反应的作用单位 = 机会的防御者（这里是敌人）。任务 09 的唯一命令入口要求
            // 发行者能控制其涉及单位，而敌人（UnitId(1)）在已验证定义里由 AI 入口控制，
            // 因此本命令必须由该控制者发行。载荷/scope/目标 Tick 与原先逐字段相同。
            SubmitAsAi(sim, new CommandRequest(
                1L,
                new ReactionCommandScope(opportunity.Id),
                new ReactionCommandPayload(ReactionCommandKind.Dodge, RealDodge, destination)));
            StepResult tick1 = T03.StepNext(sim);              // Tick 1

            Assert.That(EventsOf<CommandRejectedEvent>(tick1), Is.Empty);
            Assert.That(opportunity.State, Is.EqualTo(ReactionOpportunityState.Accepted));
            Assert.That(opportunity.AcceptedActionSpecId, Is.EqualTo(RealDodge.Value));
            Assert.That(opportunity.BoundActionPlanId.IsValid, Is.True);

            ActionPlan dodge = sim.ScheduleAuthority.Registry.Find(opportunity.BoundActionPlanId);
            Assert.That(dodge, Is.Not.Null);
            Assert.That(dodge.ActionType, Is.EqualTo(ActionType.Dodge));
            Assert.That(dodge.Origin, Is.EqualTo(ActionPlanOrigin.Reaction));
            Assert.That(dodge.State, Is.EqualTo(ActionPlanState.Locked), "反应计划直接 Locked");
            Assert.That(dodge.BudgetCostTicks, Is.EqualTo(0));
            Assert.That(dodge.SubmittedWindowId, Is.Null);
            Assert.That(dodge.TriggerTick, Is.EqualTo(opportunity.TriggerTick));
            Assert.That(dodge.ReactionOpportunityId.Value, Is.EqualTo(opportunity.Id));
            Assert.That(dodge.SourceThreatPlanId.Value,
                Is.EqualTo(opportunity.SourceAttackPlanId));
            Assert.That(dodge.StartTick, Is.EqualTo(opportunity.TriggerTick - dodge.ReactionWindupTicks));
            Assert.That(dodge.EndTick, Is.EqualTo(opportunity.TriggerTick + dodge.RecoveryTicks));
            Assert.That(dodge.Destination.HasValue, Is.True);
            Assert.That(dodge.Destination.Value.X, Is.EqualTo(destination.X));
            Assert.That(dodge.Destination.Value.Y, Is.EqualTo(destination.Y));
            Assert.That(sim.ScheduleRevision, Is.EqualTo(revisionBefore),
                "接受反应不是排程编辑事务，不推进修订号");

            // 快照：绑定计划与目的格可见。
            ActionPlanSnapshot snapshot = SnapOf(tick1.Snapshot, dodge.ActionPlanId.Value);
            Assert.That(snapshot.ReactionOpportunityId, Is.EqualTo(opportunity.Id.Value));
            Assert.That(snapshot.SourceThreatPlanId, Is.EqualTo(opportunity.SourceAttackPlanId.Value));
            Assert.That(snapshot.TriggerTick, Is.EqualTo(opportunity.TriggerTick));
            Assert.That(snapshot.HasDestination, Is.True);
            Assert.That(snapshot.DestinationX, Is.EqualTo(destination.X));
            Assert.That(snapshot.DestinationY, Is.EqualTo(destination.Y));
            Assert.That(snapshot.State, Is.EqualTo((int)ActionPlanState.Locked));
        }

        /// <summary>
        /// <c>ReactionCommandCannotProvideTriggerTick</c>（端到端）。
        ///
        /// 载荷在<strong>类型上</strong>就没有 TriggerTick：命令只能指名机会与动作，
        /// 触发 Tick 恒等于来源攻击的 ImpactTick。本用例用"动作定义与载荷种类不一致"
        /// 这一可表达的错误证明命令路径不会接受生产者指定的时刻/动作。
        ///
        /// 【任务 09 前提】与 <see cref="ReactionCommandIsAcceptedThroughSimulationCommandPhase"/>
        /// 同一发行者口径：反应的作用单位是机会的防御者（敌人，由 AI 入口控制），
        /// 因此经 <see cref="SubmitAsAi"/> 发行；否则命令会先被控制权拒绝
        /// （<c>COMMAND_ISSUER_CANNOT_CONTROL_UNIT</c>），测不到本用例要测的码。
        /// </summary>
        [Test]
        public void ReactionCommandCannotProvideTriggerTick()
        {
            BattleSimulation sim = NewSim(
                new BattleSimulationAssembly(areaThreatCandidateSource: new EnemyAreaThreat()));
            Submit(sim, Schedule(0L, 0L, AddHeroAttack(1L, 0L)));
            StepEmpty(sim, 1);

            ReactionOpportunityRuntime opportunity = sim.ReactionOpportunities.ActiveOpportunities[0];

            // 载荷声称 Block，动作定义却是 Dodge ⇒ 稳定拒绝，且不创建任何计划。
            // 发行者同前：反应的作用单位（机会的防御者 = 敌人）由 AI 入口控制，
            // 因此由该控制者发行（否则先撞上控制权拒绝，测不到本用例要测的码）。
            SubmitAsAi(sim, new CommandRequest(
                1L,
                new ReactionCommandScope(opportunity.Id),
                new ReactionCommandPayload(ReactionCommandKind.Block, RealDodge)));
            StepResult tick1 = T03.StepNext(sim);

            List<CommandRejectedEvent> rejections = EventsOf<CommandRejectedEvent>(tick1);
            Assert.That(rejections.Count, Is.EqualTo(1));
            Assert.That(rejections[0].ReasonCode, Is.EqualTo(ReactionCodes.REACTION_ACTION_NOT_BLOCK_OR_DODGE));
            Assert.That(opportunity.State, Is.EqualTo(ReactionOpportunityState.Open));
            Assert.That(opportunity.BoundActionPlanId.IsValid, Is.False);
            Assert.That(tick1.Snapshot.Plans.Count, Is.EqualTo(1), "只应有来源攻击那一条计划");
        }

        // ————————————————————————————————————————————————————————————
        // 夹具
        // ————————————————————————————————————————————————————————————

        /// <summary>区域攻击的候选来源桩（真实几何属于任务 06）：把敌人作为唯一候选。</summary>
        private sealed class EnemyAreaThreat : IAreaThreatCandidateSource
        {
            public IReadOnlyList<UnitId> CandidatesFor(ActionPlan sourcePlan, long telegraphTick)
                => new[] { T03.EnemyUnitId };
        }

        /// <summary>
        /// 阶段探针：在阶段 11（伤害/合力聚合）与阶段 14（状态/控制/剩余终态）各记录一次
        /// 被观察计划的权威状态；并在指定 Tick 的<strong>阶段 14</strong>
        /// 经统一终态协调器终止它。
        /// </summary>
        private sealed class ResolutionPhaseProbe : IResolutionCommitSystem
        {
            public BattleSimulation Simulation;
            public long WatchPlanId = -1L;
            public long TerminateAtTick = -1L;
            public readonly List<string> Observations = new List<string>();

            public void CommitDamageAndAggregationOrdered(long tick, IReadOnlyList<UnitSnapshot> units)
                => Observe("phase11", tick);

            public void CommitStateControlAndRemainingTerminalsOrdered(
                long tick, IReadOnlyList<UnitSnapshot> unitsAfterRelocation)
            {
                Observe("phase14", tick);
                if (TerminateAtTick != tick || WatchPlanId < 0L) return;

                ActionPlan plan = Simulation?.ScheduleAuthority.Registry.Find(new ActionPlanId(WatchPlanId));
                if (plan == null || plan.IsTerminal) return;
                Simulation.TerminalCoordinator.EnterTerminal(
                    plan, ActionTerminationReason.InterruptedByClash, tick);
            }

            private void Observe(string phase, long tick)
            {
                if (WatchPlanId < 0L) return;
                if (TerminateAtTick >= 0L && tick != TerminateAtTick) return;
                ActionPlan plan = Simulation?.ScheduleAuthority.Registry.Find(new ActionPlanId(WatchPlanId));
                Observations.Add(plan == null
                    ? phase + ":" + tick + ":<missing>"
                    : phase + ":" + tick + ":" + (int)plan.State);
            }
        }
    }
}
