using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 09 C 流（回放权威输入面）的专有夹具。
    ///
    /// 它只复用 <see cref="Task09Fixture"/> 的<strong>真实</strong>定义/模拟/入口（不另造一套几何），
    /// 并额外提供三样回放侧专有的东西：
    /// <list type="number">
    /// <item>把"一次冻结批次的完整流水"折叠进 <see cref="ReplayAuthorityInput"/> 的驱动器
    /// （记录批次 → 按 <c>CommandSequence</c> 登记 Player Envelope → 折叠本 Tick 事件）；</item>
    /// <item>确认后的排程编辑事务构造器（最终 Operations + <c>ExpectedScheduleRevision</c>）；</item>
    /// <item>System 来源入口的反射接缝（<c>RegisterSystemEntry</c> / <c>SubmitSystemInternal</c> 是
    /// <c>internal</c>，测试程序集没有 <c>InternalsVisibleTo</c>；用法与
    /// <c>Task03/CommandIngressTests.cs:519-536</c> 完全一致）。</item>
    /// </list>
    /// </summary>
    internal static class Task09ReplayFixture
    {
        private static readonly MethodInfo RegisterSystemEntryMethod = typeof(CommandIngressRegistry)
            .GetMethod("RegisterSystemEntry", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo SubmitSystemInternalMethod = typeof(CommandIngressEntry)
            .GetMethod("SubmitSystemInternal", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>Player / AI 各自的 <see cref="CommandIngressEntry.SourcePriority"/>（由来源种类派生）。</summary>
        public static int PlayerPriority => CommandSourcePriority.Player;
        public static int AiPriority => CommandSourcePriority.Ai;

        public static ControllerId SystemControllerId => new ControllerId(BattleSimulation.SystemControllerId);


        /// <summary>
        /// 记录一个<strong>已冻结</strong>批次并推进一个 Tick，然后按冻结批次的 Tick 折叠该 Tick 的事件。
        ///
        /// 批次的 Tick 与 <c>Step</c> 的 Tick 恒等（<c>Step</c> 只接受本 Tick 的冻结批次），
        /// 因此这里返回的记录器与 <c>StepResult</c> 一一对应。
        ///
        /// 网关输出的两条读数各自对应一种事实：
        /// <list type="bullet">
        /// <item><c>Envelopes</c> = "分配了序号并进入本 Tick 规范命令集合"（<c>RecordAccepted</c>
        /// 记录接受事实；AI/System 由被测实现自行跳过，测试不替它过滤）；</item>
        /// <item>被处理器拒绝的命令在 <c>Step</c> 的事件流里以 <c>CommandRejectedEvent</c>
        /// 出现，由 <c>RecordTickEvents</c> 折叠。</item>
        /// </list>
        /// </summary>
        public static StepResult RecordAndStep(BattleSimulation sim, ReplayAuthorityInput log)
        {
            Assert.That(log, Is.Not.Null, "夹具前提：必须先建立回放权威输入记录器");
            long tick = sim.Tick + 1L;
            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(tick);
            log.RecordFrozenBatch(batch);

            StepResult result = sim.Step(tick, batch);

            FrozenTickCommandSet set = sim.LastCommandSet;

            // 第一步：把网关分配了序号的命令登记进"按序号解析来源"的索引
            // （与来源种类无关，否则 AI/System 的拒绝事件解析不出来源）。
            if (set != null)
            {
                foreach (CommandEnvelope envelope in set.Envelopes) log.RecordEnvelope(envelope);
            }

            // 第二步：折叠本 Tick 的事件，得到每条命令在本 Tick 的**最终**处置
            // （处理器级拒绝只有在这里才可见）。
            log.RecordTickEvents(tick, result.Events.EventsInSequenceOrder);

            // 第三步：只有"获得序号且本 Tick 没有被处理器拒绝"的命令才是被接受的。
            // 记录**最终**处置，绝不重复记两种处置（否则同一条命令会有两个结果事实）。
            if (set != null)
            {
                var rejectedSequences = new HashSet<long>();
                foreach (CommandRejectionRecord rejection in set.RejectedCommands)
                    if (rejection?.Envelope != null) rejectedSequences.Add(rejection.Envelope.CommandSequence);
                foreach (CommandRejectedEvent rejected in
                         result.Events.EventsInSequenceOrder.OfType<CommandRejectedEvent>())
                    rejectedSequences.Add(rejected.CommandSequence);

                foreach (CommandEnvelope envelope in set.Envelopes)
                {
                    if (rejectedSequences.Contains(envelope.CommandSequence)) continue;
                    log.RecordAccepted(envelope);
                }
            }

            return result;
        }

        /// <summary>记录一个已冻结批次但不推进（用于构造"冻结后迟到"的入口拒绝）。</summary>
        public static FrozenCommandBatch RecordFreeze(BattleSimulation sim, ReplayAuthorityInput log)
        {
            long tick = sim.Tick + 1L;
            FrozenCommandBatch batch = sim.CommandIngress.FreezeTick(tick);
            log.RecordFrozenBatch(batch);
            return batch;
        }

        /// <summary>
        /// 一次<strong>确认后</strong>的排程编辑事务：最终 Operations（Move + Remove）+ 目标 Tick +
        /// <c>ExpectedScheduleRevision</c>。本地草稿/吸附预览根本没有参数可传——它们不是命令。
        /// </summary>
        public static CommandRequest ConfirmedScheduleEdit(
            long targetTick, long expectedRevision, WindowId? window, params ScheduleEditOperation[] finalOperations)
            => new CommandRequest(
                targetTick, new ScheduleEditScope(expectedRevision, window),
                new ScheduleEditPayload(finalOperations));

        /// <summary>
        /// 一条普通攻击的 Add 操作。<paramref name="target"/> <strong>不得</strong>省略：
        /// <c>PrimaryTargetOnly</c> 的攻击在创建时就必须带目标，
        /// 否则事务以 <c>SCHEDULE_PRIMARY_TARGET_REQUIRED</c> 拒绝，
        /// 于是"时间线上存在一个可编辑计划"这个前提根本建不出来。
        /// </summary>
        public static AddOrdinaryPlanOperation ConfirmedAddHeroAttack(
            long temporaryKey, long? requestedStartTick, UnitId target)
            => new AddOrdinaryPlanOperation(
                temporaryKey, Task09Fixture.Hero, new ActionSpecId(Task09Fixture.AttackSpecId),
                requestedStartTick, default, target, GridDirection.East, null);

        public static ActionPlanId PlanId(long value) => new ActionPlanId(value);

        /// <summary>
        /// 本场<strong>已经存在</strong>的 System 来源入口。
        ///
        /// System 来源的<strong>权威唯一</strong>注册点就是模拟的装配路径
        /// （<c>BattleSimulation</c> 在构造时经 <c>RegisterSystemEntry</c> 注册 <c>controller.system</c>），
        /// 因此这里只<strong>取用</strong>那个既有入口，不新建第二个同 <c>ControllerId</c> 的入口
        /// ——重复注册会被唯一注册守卫稳定拒绝（<c>COMMAND_INGRESS_DUPLICATE_CONTROLLER</c>），
        /// 而"守住唯一 System 身份"这条生产不变量不因测试需要而被绕过。
        /// </summary>
        public static CommandIngressEntry SystemEntry(CommandIngressRegistry registry)
        {
            Assert.That(RegisterSystemEntryMethod, Is.Not.Null,
                "CommandIngressRegistry.RegisterSystemEntry 必须存在（Logic 内部系统来源路径）");
            Assert.That(RegisterSystemEntryMethod.IsPublic, Is.False,
                "系统来源注册路径不得是 public：外部程序集无法把自己注册成 System");
            Assert.That(registry, Is.Not.Null, "夹具前提：必须有一个命令入口注册表");

            CommandIngressEntry entry = registry.FindEntry(SystemControllerId);
            Assert.That(entry, Is.Not.Null,
                "夹具前提：模拟装配路径必须已经注册 System 入口（唯一 System 身份由生产代码建立，不由测试建立）");
            Assert.That(entry.SourceKind, Is.EqualTo(CommandSourceKind.System));
            return entry;
        }

        /// <summary>
        /// <c>internal</c> 的系统内部提交路径（模拟"Logic 内部稳定系统提交了一条请求"）。
        /// </summary>
        public static CommandIngressRejection SubmitSystemInternal(CommandIngressEntry entry, CommandRequest request)
        {
            Assert.That(SubmitSystemInternalMethod, Is.Not.Null, "CommandIngressEntry.SubmitSystemInternal 必须存在");
            Assert.That(SubmitSystemInternalMethod.IsPublic, Is.False,
                "系统内部提交路径不得是 public（公开的 Submit 对系统入口稳定拒绝）");
            return (CommandIngressRejection)SubmitSystemInternalMethod.Invoke(entry, new object[] { request });
        }
    }

    /// <summary>
    /// 任务 09 必需测试 #41/#44/#45（C 流：回放权威输入面）。
    ///
    /// 三条用例都<strong>真的构造</strong>输入序列（同一个冻结批次里的 Player + AI + System 请求；
    /// 多次未确认手势 + 一次确认事务），而不是只断言"没有别的东西"。
    ///
    /// 被测面 = <see cref="ReplayAuthorityInput"/>（任务 09「必须产出」11）：
    /// 权威回放输入<strong>只</strong>记录入口绑定之后的 Player 来源
    /// （ControllerId / ProducerOrdinal / Request / 原始提交 Tick），
    /// AI/System 由逻辑在 Tick 0 重演时重建，绝不进入权威输入。
    /// </summary>
    public class Task09ReplayInputTests
    {
        // =====================================================================
        // 必需测试 #41：回放权威输入排除 AI 与 System 请求
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>记录器从"提交入口"而不是"冻结批次"取事实 —— 它就没有
        /// <c>CommandSourceKind</c> 可筛，AI/System 请求会一起进权威输入 ⇒ 断言红；</item>
        /// <item>筛掉了 <c>System</c> 却漏了 <c>Ai</c>（或反之，例如按 <c>SourcePriority == 0</c>
        /// 之外的任何写法漏判）⇒ 被漏的那条出现在 <c>AuthorityCommands</c>/<c>CommandOutcomes</c> ⇒ 断言红；</item>
        /// <item>把 <c>ExcludedNonAuthoritativeFactCount</c> 实现成"总是 0"（即根本没看见 AI/System 事实）
        /// ⇒ 排除计数断言红（它会退化成静默丢弃）；</item>
        /// <item>权威输入元素类型允许 <c>SourceKind = Ai/System</c>（例如直接复用可变的
        /// <c>RecordedCommandRequest</c> 并保留其可填来源）⇒ 全元素来源断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void ReplayAuthorityInputExcludesAiAndSystemRequests()
        {
            // 前提：Tick 0 真的打开一个窗口 —— 三条 CloseOwnWindow 请求才有真实目标，
            // "Player 的关窗被接受、AI/System 的同类请求在处理器层被拒绝"才是可观察事实。
            var schedule = new Task09Fixture.ScriptedWindowSchedule()
                .Open(Task09Fixture.WindowOpenTick, Task09Fixture.Hero, Task09Fixture.WindowBudget);
            var sim = Task09Fixture.NewSim(schedule);
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);
            CommandIngressEntry ai = Task09Fixture.AiEntry(sim);

            // 前提：System 来源入口真的存在（唯一注册点是 Logic 内部的模拟装配路径，不是测试），
            // 且它的公开提交路径被稳定拒绝。
            CommandIngressEntry system = Task09ReplayFixture.SystemEntry(sim.CommandIngress);
            Assert.That(system.SourceKind, Is.EqualTo(CommandSourceKind.System));
            Assert.That(system.SourcePriority, Is.EqualTo(CommandSourcePriority.System));
            Assert.That(sim.CommandIngress.FindEntry(Task09ReplayFixture.SystemControllerId), Is.SameAs(system),
                "System 入口在本场唯一：注册表里不存在第二个同 ControllerId 的入口");
            Assert.That(system.Submit(Task09Fixture.CloseWindow(1L, new WindowId(1L))).ReasonCode,
                Is.EqualTo(CommandCodes.SYSTEM_SOURCE_INTERNAL_ONLY));

            Task09Fixture.StepNext(sim);   // 完成 Tick 0（无命令）

            var log = new ReplayAuthorityInput();

            // —— 同一个冻结批次（Tick 1）里放三条真实请求：Player / AI / System ——
            CommandRequest playerRequest = Task09Fixture.CloseWindow(1L, new WindowId(1L));
            CommandRequest aiRequest = Task09Fixture.CloseWindow(1L, new WindowId(1L));
            CommandRequest systemRequest = Task09Fixture.CloseWindow(1L, new WindowId(1L));

            Assert.That(player.Submit(playerRequest), Is.Null, "前提：Player 请求被入口接受");
            Assert.That(ai.Submit(aiRequest), Is.Null, "前提：AI 请求被入口接受");
            Assert.That(Task09ReplayFixture.SubmitSystemInternal(system, systemRequest), Is.Null,
                "前提：Logic 内部系统请求被入口接受");

            // 记录器若把 AI/System 当成权威输入，这三条会被一起记下 —— 那正是本用例要排除的缺陷。
            StepResult result = Task09ReplayFixture.RecordAndStep(sim, log);

            // 前提：三条请求确实都进入了同一个冻结批次；只有 Player 在权威输入侧。
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(3));
            Assert.That(sim.LastCommandSet.Envelopes.Select(e => e.ControllerId.Value).ToArray(),
                Is.EqualTo(new[]
                {
                    Task09Fixture.PlayerId.Value,
                    Task09Fixture.AiId.Value,
                    BattleSimulation.SystemControllerId
                }),
                "规范顺序 = SourcePriority(0/10/20) -> ControllerId(Ordinal) -> ProducerOrdinal");
            Assert.That(sim.LastCommandSet.Envelopes.Select(e => (int)e.SourceKind).ToArray(),
                Is.EqualTo(new[]
                {
                    (int)CommandSourceKind.Player,
                    (int)CommandSourceKind.Ai,
                    (int)CommandSourceKind.System
                }));

            // 入口侧既有读数也只暴露 Player 事实。
            Assert.That(sim.CommandIngress.LastFrozenPlayerFacts.Count, Is.EqualTo(1));
            Assert.That(sim.CommandIngress.LastFrozenPlayerFacts[0].SourceKind,
                Is.EqualTo(CommandSourceKind.Player));

            // —— 权威输入：恰好一条，且是 Player ——
            Assert.That(log.AuthorityCommands.Count, Is.EqualTo(1),
                "权威回放输入只记录入口绑定之后的 Player 来源；AI/System 由逻辑重建，不得记录");
            ReplayAuthorityCommand recorded = log.AuthorityCommands[0];
            Assert.That(recorded.Issuer.Value, Is.EqualTo(Task09Fixture.PlayerId.Value));
            Assert.That(recorded.SourceKind, Is.EqualTo(CommandSourceKind.Player));
            Assert.That((int)recorded.SourceKind, Is.EqualTo((int)CommandSourceKind.Player));
            Assert.That(recorded.ProducerOrdinal, Is.EqualTo(1L), "ProducerOrdinal 来自入口分配，不是生产者自报");
            Assert.That(recorded.SubmittedAtTick, Is.EqualTo(1L), "原始提交 Tick = 冻结批次 Tick");
            Assert.That(recorded.Request.TargetTick, Is.EqualTo(1L));
            Assert.That(recorded.Request, Is.SameAs(playerRequest), "记录的是请求对象本身，不做任何改写");

            // AI 与 System 都没被记进权威输入（载荷身份级断言，不只是计数）。
            Assert.That(log.AuthorityCommands.Any(c => ReferenceEquals(c.Request, aiRequest)), Is.False,
                "AI 请求不得出现在权威回放输入");
            Assert.That(log.AuthorityCommands.Any(c => ReferenceEquals(c.Request, systemRequest)), Is.False,
                "System 请求不得出现在权威回放输入");

            // 命令事件流：只折叠 Player 的一条接受事实（AI/System 的命令事件不进入回放输入）。
            Assert.That(log.CommandOutcomes.Count, Is.EqualTo(1));
            ReplayCommandOutcome outcome = log.CommandOutcomes[0];
            Assert.That(outcome.OutcomeKind, Is.EqualTo(ReplayCommandOutcomeKind.Accepted));
            Assert.That(outcome.SourceKind, Is.EqualTo(CommandSourceKind.Player));
            Assert.That(outcome.Issuer.Value, Is.EqualTo(Task09Fixture.PlayerId.Value));
            Assert.That(outcome.ProducerOrdinal, Is.EqualTo(1L));
            Assert.That(outcome.SubmittedAtTick, Is.EqualTo(1L));
            Assert.That(outcome.HasCommandSequence, Is.True, "被接受的命令在网关获得整场唯一序号");
            Assert.That(outcome.CommandSequence, Is.EqualTo(sim.LastCommandSet.Envelopes[0].CommandSequence),
                "接受事实复用的是网关既有的 CommandSequence，不是第二套序号");
            Assert.That(outcome.ReasonCode, Is.Null, "接受没有拒绝码");

            Assert.That(log.AuthorityCommands.All(c => c.SourceKind == CommandSourceKind.Player), Is.True);
            Assert.That(log.CommandOutcomes.All(o => o.SourceKind == CommandSourceKind.Player), Is.True);

            // 非 Player 事实被显式计数（可观察的排除，而不是静默丢弃），且不保存任何载荷。
            Assert.That(log.ExcludedNonAuthoritativeFactCount, Is.EqualTo(2),
                "同一冻结批次里的 AI 与 System 各计一次；被排除意味着它们连请求载荷都不进回放数据");

            // 类型层：权威输入元素不存在"可填来源"，也不存在 System/AI 的记录形态。
            Assert.That(typeof(ReplayAuthorityCommand).GetProperty("SourceKind").CanWrite, Is.False,
                "权威输入元素的 SourceKind 只读派生 —— 不存在\u201cAI/System 权威输入\u201d这一形态");
            Assert.That(ReplayCommandOutcomeKind.IngressRejected, Is.Not.EqualTo(ReplayCommandOutcomeKind.Accepted));

            // 无关的既有事件（本 Tick 没有）不影响折叠结果；事件批次本身仍然看得到全部来源的事实。
            Assert.That(EventsOf<CommandIngressRejectedEvent>(result).Count, Is.EqualTo(0));

            // 唯一的权威输入转换点再做一次门控：AI/System 记录事实不能成为权威输入。
            Assert.That(ReplayAuthorityCommand.TryCreateAuthoritative(
                Task09Fixture.AiId, CommandSourceKind.Ai, 1L, aiRequest, 1L, out _), Is.False);
            Assert.That(ReplayAuthorityCommand.TryCreateAuthoritative(
                Task09ReplayFixture.SystemControllerId, CommandSourceKind.System, 1L, systemRequest, 1L, out _), Is.False);
            Assert.That(ReplayAuthorityCommand.TryCreateAuthoritative(
                Task09Fixture.PlayerId, CommandSourceKind.Player, 1L, playerRequest, 1L, out ReplayAuthorityCommand ok),
                Is.True);
            Assert.That(ok.SourceKind, Is.EqualTo(CommandSourceKind.Player));
            Assert.That(ok.ToRecordedCommandRequest().IsAuthoritativeReplayInput, Is.True,
                "回放注入 DTO 的 IsAuthoritativeReplayInput 与记录器口径一致");

            // 记录器只读：它不改变任何授权判定与逻辑状态。
            Assert.That(result.Snapshot.ScheduleRevision, Is.EqualTo(0L));
            Assert.That(result.Snapshot.NextCommandSequence, Is.EqualTo(4L),
                "三条请求各消耗一个整场序号：记录行为本身不分配也不复用序号");
        }

        // =====================================================================
        // 必需测试 #44：未确认的拖拽轨迹不出现在回放输入里
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>把"未确认手势/取消手势"当成命令记进回放输入（例如给记录器加一条
        /// <c>RecordGesture</c> 旁路，或让 UI 草稿直接构造 <c>CommandRequest</c> 并提交）
        /// ⇒ 权威输入里出现该 gesture 请求 ⇒ 身份级断言红；</item>
        /// <item>草稿层把每次鼠标移动/吸附预览都包成一个 ScheduleEdit 提交 ⇒ 同样红
        /// （本用例刻意让三条手势都带着可识别的计划 ID，任何泄漏都能被指名）；</item>
        /// <item>记录器把入口级拒绝（迟到请求，<c>ProducerOrdinal == 0</c>）当成权威输入
        /// ⇒ 拒绝事实断言红，且回放会把从未生效的手势重新注入。</item>
        /// </list>
        ///
        /// <para>
        /// 夹具口径：三条手势<strong>一条都没有被确认</strong>，因此
        /// <see cref="ReplayAuthorityInput.AuthorityCommands"/> 必须恒为空 ——
        /// "确认"的唯一形态是一次<strong>确认事务</strong>（原子 Operations 的
        /// <c>CommandRequest</c>）进入冻结批次，而不是手势本身。
        /// </para>
        /// </summary>
        [Test]
        public void UncommittedDragTrajectoryIsAbsentFromReplayInput()
        {
            var schedule = new Task09Fixture.ScriptedWindowSchedule()
                .Open(Task09Fixture.WindowOpenTick, Task09Fixture.Hero, Task09Fixture.WindowBudget);
            var sim = Task09Fixture.NewSim(schedule);
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);
            var log = new ReplayAuthorityInput();

            Task09Fixture.StepNext(sim);   // 完成 Tick 0

            // —— 未确认手势 #1：冻结 Tick 1 之前拖出来的"临时计划"（尚未确认，从未提交）——
            const long gestureTemporaryKey = 9001L;
            CommandRequest cancelledGesture = Task09Fixture.ScheduleAdd(
                1L, 1L, 1L, new WindowId(1L), gestureTemporaryKey);

            FrozenCommandBatch batch = Task09ReplayFixture.RecordFreeze(sim, log);

            // —— 冻结之后继续"拖动"：这两次手势仍不是命令 ——
            CommandRequest dragAfterFreeze = Task09Fixture.ScheduleAdd(
                1L, 1L, 1L, new WindowId(1L), gestureTemporaryKey);
            CommandIngressRejection late = player.Submit(dragAfterFreeze);
            Assert.That(late, Is.Not.Null, "冻结后的提交必须在入口稳定拒绝");
            Assert.That(late.ReasonCode, Is.EqualTo(CommandCodes.LATE_REQUEST_FOR_FROZEN_TICK));
            Assert.That(late.ProducerOrdinal, Is.EqualTo(0L), "迟到请求不消耗也不获得 ProducerOrdinal");

            CommandRequest snappedPreview = Task09Fixture.ScheduleEdit(
                1L, 1L, new WindowId(1L),
                Task09Fixture.AddHeroAttack(gestureTemporaryKey, 1L, target: Task09Fixture.Monster));
            CommandIngressRejection previewLate = player.Submit(snappedPreview);
            Assert.That(previewLate.ReasonCode, Is.EqualTo(CommandCodes.LATE_REQUEST_FOR_FROZEN_TICK));

            // 记录器不得把这些手势/预览当成命令，也不得把入口拒绝回写成权威输入。
            // 冻结批次本身是空的：未确认手势从未进入任何逻辑输入。
            Assert.That(batch.Count, Is.EqualTo(0),
                "冻结前的那次拖动仍是未确认手势（不是确认事务）：它不进入冻结批次");
            Assert.That(log.CommandOutcomes.Count, Is.EqualTo(0),
                "手势与预览不是命令，不产生任何接受/拒绝记录");
            Assert.That(log.AuthorityCommands.Count, Is.EqualTo(0),
                "未确认手势不进权威回放输入：权威输入的唯一来源是冻结批次里的 Player 事实");

            StepResult result = sim.Step(1L, batch);
            foreach (CommandEnvelope envelope in sim.LastCommandSet.Envelopes) log.RecordEnvelope(envelope);
            log.RecordTickEvents(1L, result.Events.EventsInSequenceOrder);

            // —— 手势在逻辑侧的痕迹只能是入口拒绝事件：那是"从未生效的事实" ——
            List<CommandIngressRejectedEvent> rejections = EventsOf<CommandIngressRejectedEvent>(result);
            Assert.That(rejections.Count, Is.EqualTo(2), "两次冻结后的手势各得到恰一个可见入口拒绝");
            Assert.That(rejections.All(r => r.ReasonCode == CommandCodes.LATE_REQUEST_FOR_FROZEN_TICK), Is.True);
            Assert.That(rejections.All(r => r.ProducerOrdinal == 0L), Is.True);
            Assert.That(rejections.All(r => r.SourceKind == CommandSourceKind.Player), Is.True);

            // 手势永远不能成为命令：冻结批次空 ⇒ 权威输入的 Player 事实也空。
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(0),
                "未确认手势没有变成任何命令");
            Assert.That(log.AuthorityCommands.Count, Is.EqualTo(0),
                "回放输入里不存在\u201c未确认手势\u201d这一类 —— 它们从未被记录");
            Assert.That(sim.CommandIngress.LastFrozenPlayerFacts.Count, Is.EqualTo(0),
                "入口侧读数同样为空：手势没有成为\u201c入口绑定之后的 Player 事实\u201d");

            // 手势请求对象一个都不在回放输入里（载荷身份级断言）。
            Assert.That(log.AuthorityCommands.Any(c => ReferenceEquals(c.Request, cancelledGesture)), Is.False,
                "取消的拖动请求不得进入回放输入");
            Assert.That(log.AuthorityCommands.Any(c => ReferenceEquals(c.Request, dragAfterFreeze)), Is.False,
                "冻结后继续拖动的请求不得进入回放输入");
            Assert.That(log.AuthorityCommands.Any(c => ReferenceEquals(c.Request, snappedPreview)), Is.False,
                "吸附预览不得进入回放输入");

            // 那些手势也绝不可能"变成"被接受的命令。
            Assert.That(log.CommandOutcomes.Count, Is.EqualTo(2),
                "入口级拒绝事实按既有事件族折叠：每条迟到手势恰一条");
            Assert.That(log.CommandOutcomes.All(o => o.OutcomeKind == ReplayCommandOutcomeKind.IngressRejected),
                Is.True,
                "回放输入里不存在\u201c未确认手势\u201d这一类 —— 它们只是入口拒绝，不是命令");
            Assert.That(log.CommandOutcomes.All(o => o.IsAccepted), Is.False);
            Assert.That(log.CommandOutcomes.All(o => o.HasCommandSequence), Is.False,
                "入口级拒绝从未获得 CommandSequence");
            Assert.That(log.CommandOutcomes.All(o => o.ReasonCode == CommandCodes.LATE_REQUEST_FOR_FROZEN_TICK), Is.True,
                "拒绝码复用既有稳定码，不新造第二套");
            Assert.That(log.CommandOutcomes.All(o => o.ProducerOrdinal == 0L), Is.True);

            // 未确认手势不消耗任何序号的更强读数：整场序号与入口序号都被冻结不动。
            Assert.That(result.Snapshot.NextCommandSequence, Is.EqualTo(1L),
                "手势连整场序号都不消耗");
            Assert.That(player.NextProducerOrdinal, Is.EqualTo(1L),
                "手势连入口的 ProducerOrdinal 都不消耗");
            Assert.That(log.AuthorityCommands.Count, Is.EqualTo(sim.CommandIngress.LastFrozenPlayerFacts.Count),
                "可重放性：回放输入 == 注册表给出的\u201c入口绑定之后\u201d事实（两者都为空）");
        }

        // =====================================================================
        // 必需测试 #45：确认后的 ScheduleEdit 只记录最终 Operations
        // =====================================================================

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>记录"每一次预览/中间态"而不是确认后的最终 Operations（例如把 Move 与 Remove
        /// 拆成两条记录，或把拖动过程中的中间 Tick 也记进去）⇒ Operations 数量/顺序断言红；</item>
        /// <item>记录时刻改写请求（把 <c>TargetTick</c> 归一成"当前 Tick"、"尽量合并"到最新修订号）
        /// ⇒ 目标 Tick 与 <c>ExpectedScheduleRevision</c> 断言红（回放会得到不同的采纳结果）；</item>
        /// <item>原子事务未保序（把 Remove 提前到 Move 之前）⇒ 最终 Operations 顺序断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void ConfirmedScheduleEditRecordsOnlyFinalOperations()
        {
            var schedule = new Task09Fixture.ScriptedWindowSchedule()
                .Open(Task09Fixture.WindowOpenTick, Task09Fixture.Hero, Task09Fixture.WindowBudget);
            var sim = Task09Fixture.NewSim(schedule);
            CommandIngressEntry player = Task09Fixture.PlayerEntry(sim);
            var log = new ReplayAuthorityInput();

            // Tick 0：窗口按脚本打开。必须先完成它，Tick 1 的确认事务才有真实目标 Tick
            // （在 Tick -1 就把目标 Tick = 1 的请求投出去，只会落进一个**不同** Tick 的桶里，
            //  而 FreezeTick(0) 冻结的批次里根本没有它 —— 那条命令永远不会被处理）。
            Task09Fixture.StepNext(sim);
            Assert.That(sim.CurrentTurnWindow, Is.Not.Null, "前提：Tick 0 打开窗口");
            WindowId windowId = sim.CurrentTurnWindow.WindowId;
            Assert.That(windowId.Value, Is.EqualTo(1L), "前提：本场第一个窗口的 WindowId 为 1");

            // Tick 1：先确认一条 Add，让时间线上真的存在一个可编辑计划（后续 Move/Remove 的目标）。
            long revisionAtConfirmation = sim.ScheduleAuthority.ScheduleRevision;
            Assert.That(revisionAtConfirmation, Is.EqualTo(0L));

            var firstOperations = new ScheduleEditOperation[]
            {
                // PrimaryTargetOnly 的攻击在创建时必须有目标（ScheduleCodes.SCHEDULE_PRIMARY_TARGET_REQUIRED）：
                // 确认事务携带的是**已经定好的**目标，而不是"稍后再吸附"的空引用。
                //
                // 起点刻意排在 Tick 3 / 4（都晚于下一次确认事务所在的 Tick 2）：
                // 只有**还没被启动门禁锁定**的 Editable 计划才能被 Move/Remove
                // （Tick 1 就到期启动的计划在 Tick 2 已是 Running，任何编辑都会被
                //  SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED 整批拒绝）。
                //
                // 一次事务里建**两个**计划是必要的：批内每个计划只能被声明一次
                // （ScheduleEditor 的 claimedPlans 守卫，重复触及 = SCHEDULE_EDIT_CONFLICT_IN_BATCH），
                // 所以"Move A + Remove B"必须落在两个不同的计划上，
                // 而"对同一个计划 Move 再 Remove"在权威事务语义下根本不可表达。
                Task09ReplayFixture.ConfirmedAddHeroAttack(1L, 3L, Task09Fixture.Monster),
                Task09ReplayFixture.ConfirmedAddHeroAttack(2L, 4L, Task09Fixture.Monster)
            };
            CommandRequest firstConfirmed = Task09ReplayFixture.ConfirmedScheduleEdit(
                1L, revisionAtConfirmation, windowId, firstOperations);
            Assert.That(player.Submit(firstConfirmed), Is.Null);
            Task09ReplayFixture.RecordAndStep(sim, log);

            Assert.That(sim.LastCommandSet.RejectedCommands.Count, Is.EqualTo(0),
                "前提：确认事务必须被处理器接受");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.EqualTo(2),
                "前提：时间线上存在两个可编辑普通计划（Move 的目标 + Remove 的目标）");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.All(p => p.IsEditable), Is.True,
                "前提：两个计划都还没到期启动，因此仍可编辑");

            // —— 多次未确认手势（拖动/吸附/取消），全部不产生命令 ——
            Task09Fixture.ScheduleEdit(2L, 1L, windowId,
                Task09Fixture.AddHeroAttack(9101L, 2L, target: Task09Fixture.Monster));
            Task09Fixture.ScheduleEdit(2L, 1L, windowId,
                Task09Fixture.AddHeroAttack(9102L, 3L, target: Task09Fixture.Monster));
            Task09Fixture.ScheduleEdit(2L, 1L, windowId,
                Task09Fixture.AddHeroAttack(9103L, 4L, target: Task09Fixture.Monster));

            Assert.That(log.AuthorityCommands.Count, Is.EqualTo(1),
                "手势构造的请求没有被提交：权威输入里只有确认后的那一条");

            // —— 一次确认事务：最终 Operations = Move（重排 A）+ Remove（删除 B），顺序即采纳顺序 ——
            long firstPlanId = sim.ScheduleAuthority.Registry.ActivePlans[0].ActionPlanId.Value;
            long secondPlanId = sim.ScheduleAuthority.Registry.ActivePlans[1].ActionPlanId.Value;
            ActionPlanId movePlanId = new ActionPlanId(firstPlanId);
            ActionPlanId removePlanId = new ActionPlanId(secondPlanId);
            long revisionAtSecondConfirmation = sim.ScheduleAuthority.ScheduleRevision;
            Assert.That(revisionAtSecondConfirmation, Is.EqualTo(1L), "Tick 1 的确认事务令修订号恰好 +1");

            var finalOperations = new ScheduleEditOperation[]
            {
                new MoveEditablePlanOperation(movePlanId, 6L),
                new RemoveEditablePlanOperation(removePlanId, ActionTerminationReason.CancelledByCommand)
            };
            CommandRequest confirmed = Task09ReplayFixture.ConfirmedScheduleEdit(
                2L, revisionAtSecondConfirmation, windowId, finalOperations);

            Assert.That(player.Submit(confirmed), Is.Null);
            Task09ReplayFixture.RecordAndStep(sim, log);

            // —— 权威输入：只多出这一条，且只含最终 Operations ——
            Assert.That(log.AuthorityCommands.Count, Is.EqualTo(2),
                "确认事务只记录一条权威输入：不存在\u201c每次手势各一条\u201d的形态");
            ReplayAuthorityCommand recorded = log.AuthorityCommands[1];
            Assert.That(recorded.Issuer.Value, Is.EqualTo(Task09Fixture.PlayerId.Value));
            Assert.That(recorded.SourceKind, Is.EqualTo(CommandSourceKind.Player));
            Assert.That(recorded.ProducerOrdinal, Is.EqualTo(2L));
            Assert.That(recorded.SubmittedAtTick, Is.EqualTo(2L), "原始提交 Tick 原样在案");

            Assert.That(recorded.Request, Is.SameAs(confirmed), "记录原请求，不复制也不改写");
            Assert.That(recorded.Request.TargetTick, Is.EqualTo(2L), "记录目标 Tick");

            var recordedScope = recorded.Request.Scope as ScheduleEditScope;
            Assert.That(recordedScope, Is.Not.Null, "确认后的排程编辑必须携带 ScheduleEditScope");
            Assert.That(recordedScope.ExpectedScheduleRevision, Is.EqualTo(revisionAtSecondConfirmation),
                "记录 ExpectedScheduleRevision（采样时看到的修订号）");
            Assert.That(recordedScope.ExpectedWindowId.HasValue, Is.True);
            Assert.That(recordedScope.ExpectedWindowId.Value.Value, Is.EqualTo(1L));

            var recordedPayload = recorded.Request.Payload as ScheduleEditPayload;
            Assert.That(recordedPayload, Is.Not.Null);
            Assert.That(recordedPayload.Operations.Count, Is.EqualTo(2),
                "只记录确认后的最终 Operations —— 不是手势序列，也不是中间态");
            Assert.That(recordedPayload.Operations.Select(o => o.Kind).ToArray(),
                Is.EqualTo(new[] { ScheduleEditOperationKind.Move, ScheduleEditOperationKind.Remove }),
                "最终 Operations 的顺序就是事务采纳顺序");

            var recordedMove = recordedPayload.Operations[0] as MoveEditablePlanOperation;
            Assert.That(recordedMove, Is.Not.Null);
            Assert.That(recordedMove.PlanId.Value, Is.EqualTo(movePlanId.Value));
            Assert.That(recordedMove.RequestedStartTick, Is.EqualTo(6L));

            var recordedRemove = recordedPayload.Operations[1] as RemoveEditablePlanOperation;
            Assert.That(recordedRemove, Is.Not.Null);
            Assert.That(recordedRemove.PlanId.Value, Is.EqualTo(removePlanId.Value));
            Assert.That(recordedRemove.TerminationReason,
                Is.EqualTo(ActionTerminationReason.CancelledByCommand));

            // 手势的临时计划键绝不出现（9101/9102/9103 从未被提交，更没有进入回放输入）。
            Assert.That(recordedPayload.Operations.Any(
                    o => o is AddOrdinaryPlanOperation add && add.TemporaryPlanKey >= 9101L), Is.False,
                "未确认手势的临时计划键不得出现在权威回放输入");

            // —— 接受/拒绝事件：两条都是"被接受"，序号来自网关、稳定码复用既有族 ——
            Assert.That(log.CommandOutcomes.Count, Is.EqualTo(2));
            Assert.That(log.CommandOutcomes.All(o => o.OutcomeKind == ReplayCommandOutcomeKind.Accepted), Is.True);
            Assert.That(log.CommandOutcomes.Select(o => o.CommandSequence).ToArray(),
                Is.EqualTo(new[] { 1L, 2L }),
                "接受事实复用网关既有的整场唯一 CommandSequence");
            Assert.That(log.CommandOutcomes.Select(o => o.ProducerOrdinal).ToArray(), Is.EqualTo(new[] { 1L, 2L }));
            Assert.That(log.CommandOutcomes.Select(o => o.SubmittedAtTick).ToArray(), Is.EqualTo(new[] { 1L, 2L }));
            Assert.That(log.CommandOutcomes.All(o => o.ReasonCode == null), Is.True);

            // 记录器一致复述网关的接受结论（记录不是第二套判定）。
            Assert.That(sim.LastCommandSet.RejectedCommands.Count, Is.EqualTo(0));
            Assert.That(sim.LastCommandSet.Envelopes.Count, Is.EqualTo(1));
            Assert.That(log.CommandOutcomes[1].CommandSequence,
                Is.EqualTo(sim.LastCommandSet.Envelopes[0].CommandSequence));
            Assert.That(log.CommandOutcomes[1].Issuer.Value,
                Is.EqualTo(sim.LastCommandSet.Envelopes[0].ControllerId.Value));

            // 拒绝事实同样只复用既有稳定码：把确认后的请求重投到已冻结 Tick ⇒ 迟到拒绝（不污染权威输入）。
            CommandRequest replayedAfterFreeze = Task09ReplayFixture.ConfirmedScheduleEdit(
                2L, revisionAtSecondConfirmation, new WindowId(1L), finalOperations);
            CommandIngressRejection late = player.Submit(replayedAfterFreeze);
            Assert.That(late.ReasonCode, Is.EqualTo(CommandCodes.LATE_REQUEST_FOR_FROZEN_TICK));
            Assert.That(log.AuthorityCommands.Any(c => ReferenceEquals(c.Request, replayedAfterFreeze)), Is.False);

            // 同一冻结批次对象不得被记录两次（否则回放输入会重复注入同一批事实）。
            Assert.That(log.LastRecordedBatchTick, Is.EqualTo(2L));
            Assert.That(log.ExcludedNonAuthoritativeFactCount, Is.EqualTo(0),
                "本用例的批次里没有 AI/System 事实");
        }

        private static List<TEvent> EventsOf<TEvent>(StepResult result)
            where TEvent : LogicEvent
            => result.Events.EventsInSequenceOrder.OfType<TEvent>().ToList();
    }
}
