using System;
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
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 09 · A2 轮：<strong>反应命令的 scope 语义、机会校验与控制权</strong>
    /// （任务包「必须产出」6/7 第二段与「核心命令语义」第二段）。
    ///
    /// 逐条对应必需测试：<c>ReactionCommandUsesOpportunityScopeWithoutWindowScope</c>、
    /// <c>ReactionCommandCannotCarryBlockTickEvadeTickOrCost</c>、
    /// <c>ReactionBeforeOpportunityExistsIsRejected</c>、
    /// <c>ReactionAtOptionDeadlineIsAcceptedAndAfterDeadlineIsRejected</c>、
    /// <c>ReactionDoesNotRequireOwnWindowOrConcurrentAuthority</c>、
    /// <c>PlayerAndAiReactionsUseSameProcessorAndPlanner</c>、
    /// <c>ConcurrentAuthorityDoesNotPermitReactionWithoutOpportunity</c>。
    ///
    /// <para>
    /// 全部用例都走<strong>真实命令入口 → 真实 CommandGateway → 真实 BattleCommandProcessor
    /// → 真实 ReactionCommandPlanner → 真实 ReactionOpportunitySystem</strong>，
    /// 不使用 <c>TryAcceptById</c> 的旁路调用，因此"反应命令只有一条路径"是被管线观察到的。
    /// </para>
    /// </summary>
    public class Task09ReactionCommandTests
    {
        private static ActionSpecId BlockSpec() => Task09A2Fixture.Spec(Task09A2Fixture.BlockSpecId);
        private static ActionSpecId DodgeSpec() => Task09A2Fixture.Spec(Task09A2Fixture.DodgeSpecId);

        /// <summary>把一场"攻击已启动、机会已公开"的世界推进到 <paramref name="tick"/>。</summary>
        private static void AdvanceTo(BattleSimulation sim, long tick)
        {
            for (long t = sim.Tick + 1L; t <= tick; t++) Task09A2Fixture.Step(sim, t);
        }

        // =====================================================================
        // 必需测试 14：ReactionCommandUsesOpportunityScopeWithoutWindowScope
        // =====================================================================

        /// <summary>
        /// 反应命令的 scope 只有 <c>ReactionOpportunityId</c>：<strong>没有</strong>窗口字段，
        /// 处理器也<strong>不</strong>查询当前窗口。命令被接受时：
        /// 反应计划直接 Locked、<c>SubmittedWindowId</c> 为空、<c>BudgetCostTicks</c> 与
        /// <c>ReservedTurnBudgetTicks</c> 恒为 0（08 交接 §4.5 冻结的
        /// <c>ReservedTurnBudgetTicks == 0</c> 口径），窗口账本一位不变。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>把窗口校验加进反应路径（要求发行者拥有当前窗口 / 要求 <c>ExpectedWindowId</c>）
        /// ⇒ 接受失败或反应计划带上了窗口预留；</item>
        /// <item>反应计划参与窗口预算（<c>BudgetCostTicks != 0</c>）⇒ 账本断言红；</item>
        /// <item>把反应 scope 当成 <c>WindowCommandScope</c> 处理（"按当前窗口猜"）⇒
        /// 窗口 scope 错配用例的拒绝码断言红。</item>
        /// </list>
        /// </para>
        /// </summary>
        [Test]
        public void ReactionCommandUsesOpportunityScopeWithoutWindowScope()
        {
            var sim = Task09A2Fixture.NewSim();
            Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            ReactionOpportunityRuntime opportunity = Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);

            WindowId windowId = sim.CurrentTurnWindow.WindowId;
            string windowBefore = Task09A2Fixture.WindowFingerprint(sim, windowId);

            // scope 类型上不存在窗口字段（反射是结构性证据，不是纪律）。
            string[] scopeProperties = typeof(ReactionCommandScope)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.That(scopeProperties, Is.EqualTo(new[] { "ReactionOpportunityId" }),
                "ReactionCommandScope 只有 ReactionOpportunityId：不存在任何窗口字段");
            Assert.That(typeof(ReactionCommandScope).GetProperties()
                    .Any(p => p.Name.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0), Is.False);

            // 反应由 AI 入口发行（防御者是 AI 控制的 mon），当前窗口却属于 hero。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                Task09A2Fixture.Reaction(2L, opportunity.Id, ReactionCommandKind.Block, BlockSpec()));
            StepResult result = Task09A2Fixture.Step(sim, 2L);

            Assert.That(Task09A2Fixture.RejectionCodes(result), Is.Empty,
                "不带窗口 scope 的反应命令必须被接受（拒绝 = 反应路径错误地要求了窗口）");

            IReadOnlyList<ActionPlan> plans = sim.ScheduleAuthority.Registry.ActivePlans;
            Assert.That(plans.Count, Is.EqualTo(2), "攻击 + 反应 = 两条活动计划");
            ActionPlan reaction = plans.Single(p => p.IsReaction);
            Assert.That(reaction.ActionType, Is.EqualTo(ActionType.Block));
            Assert.That(reaction.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Monster));
            Assert.That(reaction.IsLocked, Is.True, "反应计划接受后直接 Locked");
            Assert.That(reaction.SubmittedWindowId.HasValue, Is.False,
                "反应计划恒无 SubmittedWindowId（高阶反应不参与窗口预算）");
            Assert.That(reaction.BudgetCostTicks, Is.Zero);
            Assert.That(reaction.ReservedTurnBudgetTicks, Is.Zero,
                "08 交接 §4.5：反应计划的窗口预留恒为 0，不得放宽");
            Assert.That(reaction.StartTick,
                Is.EqualTo(Task09A2Fixture.AttackImpactTick - Task09A2Fixture.BlockReactionWindupTicks),
                "反应区间由来源攻击的 ImpactTick 推导，命令无法声明");

            Assert.That(Task09A2Fixture.WindowFingerprint(sim, windowId), Is.EqualTo(windowBefore),
                "反应命令不得改动窗口账本任何一位");
        }

        /// <summary>
        /// 反面对照（同一用例内的判别力）：把 <c>ReactionCommandScope</c> 换成
        /// <c>WindowCommandScope</c> 后，同一条载荷以 <c>SCOPE_PAYLOAD_MISMATCH</c> 稳定拒绝，
        /// 且不发生任何局部写入。⇒ 拒绝码不匹配就说明 scope 判别没有生效。
        /// </summary>
        [Test]
        public void ReactionPayloadWithWindowScopeIsRejectedAsMismatch()
        {
            var sim = Task09A2Fixture.NewSim();
            Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            ReactionOpportunityRuntime opportunity = Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);
            WindowId windowId = sim.CurrentTurnWindow.WindowId;

            string scheduleBefore = Task09A2Fixture.ScheduleFingerprint(sim);
            string windowBefore = Task09A2Fixture.WindowFingerprint(sim, windowId);

            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId, new CommandRequest(
                2L, new WindowCommandScope(windowId),
                new ReactionCommandPayload(ReactionCommandKind.Block, BlockSpec())));
            StepResult result = Task09A2Fixture.Step(sim, 2L);

            Assert.That(Task09A2Fixture.IngressRejectionCodes(result).ToArray(),
                Is.EqualTo(new[] { CommandCodes.SCOPE_PAYLOAD_MISMATCH }),
                "反应载荷配窗口 scope 必须精确拒绝，绝不回退到当前窗口"
                + "（判别不匹配是入口级拒绝：不分配 CommandSequence）");
            Assert.That(Task09A2Fixture.RejectionCodes(result), Is.Empty,
                "判别不匹配不得进入处理器（它连序号都不该拿到）");
            Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(scheduleBefore));
            Assert.That(Task09A2Fixture.WindowFingerprint(sim, windowId), Is.EqualTo(windowBefore));
            Assert.That(opportunity.IsOpen, Is.True, "被拒绝的命令不得消耗机会");
        }

        // =====================================================================
        // 必需测试 15：ReactionCommandCannotCarryBlockTickEvadeTickOrCost
        // =====================================================================

        /// <summary>
        /// 反应载荷/scope 上<strong>不存在</strong> TriggerTick（BlockTick / EvadeTick）、窗口、
        /// 费用字段；Block 不得携带目的格；实际消费只来自权威
        /// <c>ActionSpec.AdrenalineCost</c>。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：给 <c>ReactionCommandPayload</c> 加回
        /// <c>TriggerTick</c>/<c>WindowId</c>/<c>Cost</c> 之类字段（生产者即可声明时机或费用）
        /// ⇒ 字段扫描红；让 Block 携带目的格被静默接受 ⇒ 结构校验断言红；
        /// 让费用来自载荷而不是定义 ⇒ 账本断言红。
        /// </para>
        /// </summary>
        [Test]
        public void ReactionCommandCannotCarryBlockTickEvadeTickOrCost()
        {
            string[] forbidden =
            {
                "TriggerTick", "BlockTick", "EvadeTick", "WindowId", "Cost", "Price", "Fee",
                "Adrenaline", "Budget", "Sequence", "Ordinal", "Priority", "Controller", "Issuer", "Source"
            };

            var offenders = new List<string>();
            foreach (Type type in new[]
                     {
                         typeof(ReactionCommandPayload), typeof(ReactionCommandScope), typeof(CommandRequest)
                     })
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (forbidden.Any(f => property.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                        offenders.Add(type.Name + "." + property.Name);
                }
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (forbidden.Any(f => field.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                        offenders.Add(type.Name + "." + field.Name);
                }
            }
            Assert.That(offenders, Is.Empty,
                "反应命令面上不得存在 TriggerTick/窗口/费用/身份/顺序字段：" + string.Join(",", offenders));

            string[] payloadProperties = typeof(ReactionCommandPayload)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.That(payloadProperties,
                Is.EqualTo(new[] { "DodgeDestination", "Kind", "ReactionActionSpecId", "ReactionKind" }),
                "反应载荷恰好四样：判别标签 Kind、反应种类、动作定义 ID、可选的 Dodge 目的格");

            var sim = Task09A2Fixture.NewSim();
            Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            ReactionOpportunityRuntime opportunity = Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);

            string scheduleBefore = Task09A2Fixture.ScheduleFingerprint(sim);

            // 1. Block 携带目的格 ⇒ 结构层稳定拒绝，零局部写入。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                Task09A2Fixture.Reaction(2L, opportunity.Id, ReactionCommandKind.Block, BlockSpec(),
                    dodgeDestination: new GridPoint(8, 0)));
            StepResult withDestination = Task09A2Fixture.Step(sim, 2L);

            Assert.That(Task09A2Fixture.IngressRejectionCodes(withDestination).ToArray(),
                Is.EqualTo(new[] { CommandCodes.PAYLOAD_STRUCTURALLY_INVALID }),
                "Block 不得携带目的格（结构层拒绝：不分配 CommandSequence）");
            Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(scheduleBefore));
            Assert.That(opportunity.IsOpen, Is.True);

            // 1b. Dodge 缺少目的格 ⇒ 同样在结构层稳定拒绝（绝不凭空换位）。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                Task09A2Fixture.Reaction(3L, opportunity.Id, ReactionCommandKind.Dodge, DodgeSpec()));
            StepResult dodgeWithoutDestination = Task09A2Fixture.Step(sim, 3L);
            Assert.That(Task09A2Fixture.IngressRejectionCodes(dodgeWithoutDestination).ToArray(),
                Is.EqualTo(new[] { CommandCodes.PAYLOAD_STRUCTURALLY_INVALID }),
                "Dodge 必须带合法目的格");
            Assert.That(opportunity.IsOpen, Is.True);

            // 2. 费用只来自权威定义：接受一条真实 Block 后的账本差额恰好等于 ActionSpec.AdrenalineCost。
            AdrenalineLedger ledger = sim.AdrenalineLedgerOf(Task09A2Fixture.Monster);
            int availableBefore = ledger.AvailableAdrenaline;
            int authoritativeCost = FrozenDesignValues.BlockAdrenalineCost;
            Assert.That(availableBefore, Is.GreaterThanOrEqualTo(FrozenDesignValues.BlockAdrenalineCost),
                "夹具前提：防御者的可用额度够付一次 Block");

            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                Task09A2Fixture.Reaction(4L, opportunity.Id, ReactionCommandKind.Block, BlockSpec()));
            StepResult accepted = Task09A2Fixture.Step(sim, 4L);
            Assert.That(Task09A2Fixture.RejectionCodes(accepted), Is.Empty);

            ActionPlan reaction = sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.IsReaction);
            AdrenalineReservationEntry reservation = ledger.ReservationOf(reaction.ActionPlanId);
            Assert.That(reservation, Is.Not.Null, "真实预留必须存在（费用只来自定义）");
            Assert.That(reservation.ReservedAmount, Is.EqualTo(authoritativeCost),
                "预留额度必须等于权威 ActionSpec.AdrenalineCost，命令无法提高或降低它");
            Assert.That(ledger.AvailableAdrenaline, Is.EqualTo(availableBefore - authoritativeCost),
                "Available 的减少量恰好等于权威费用（载荷里没有任何费用字段可被利用）");
        }

        // =====================================================================
        // 必需测试 17：ReactionBeforeOpportunityExistsIsRejected
        // =====================================================================

        /// <summary>
        /// 在任何机会存在之前（Tick 0，攻击尚未提交）就提交反应命令 ⇒ 稳定拒绝，
        /// <strong>绝不</strong>猜一个"当前最近的攻击"。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：控制权解析"查不到机会就跳过校验"（那样命令会一路走到
        /// 机会系统并以别的码失败，或者更糟——被接受）；或者把未知
        /// <c>ReactionOpportunityId</c> 静默顺延到当前 Tick 的某个机会。
        /// 拒绝必须是<strong>两个稳定码之一</strong>且零局部写入：
        /// 机会解析不出防御者 ⇒ <c>COMMAND_ISSUER_UNIT_UNRESOLVABLE</c>（无法证明控制权，先于一切）；
        /// 若实现走到机会系统 ⇒ <c>REACTION_OPPORTUNITY_NOT_OPEN</c>。
        /// </para>
        /// </summary>
        [Test]
        public void ReactionBeforeOpportunityExistsIsRejected()
        {
            var sim = Task09A2Fixture.NewSim();
            Task09A2Fixture.Step(sim, 0L);
            Assert.That(sim.Tick, Is.EqualTo(0L));
            Assert.That(sim.ReactionOpportunities.ActiveOpportunities.Count, Is.Zero,
                "夹具前提：Tick 0 不存在任何机会");

            string scheduleBefore = Task09A2Fixture.ScheduleFingerprint(sim);

            // 一个格式合法但从未公开过的机会 ID。
            var phantom = new ReactionOpportunityId(9999L);
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                Task09A2Fixture.Reaction(1L, phantom, ReactionCommandKind.Block, BlockSpec()));
            StepResult result = Task09A2Fixture.Step(sim, 1L);

            string[] codes = Task09A2Fixture.RejectionCodes(result).ToArray();
            Assert.That(codes.Length, Is.EqualTo(1), "恰好一条稳定拒绝");
            Assert.That(
                    codes[0] == CommandCodes.COMMAND_ISSUER_UNIT_UNRESOLVABLE
                    || codes[0] == ReactionOpportunityCodes.OPPORTUNITY_NOT_OPEN,
                Is.True,
                "机会不存在必须是稳定拒绝码，绝不猜机会；实测=" + codes[0]);
            Assert.That(reactionPlans(sim), Is.Zero, "不得创建任何反应计划");
            Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(scheduleBefore));
            Assert.That(sim.ReactionOpportunities.ActiveOpportunities.Count, Is.Zero,
                "拒绝不得凭空创建机会");

            // 对照：同一入口、同一 Tick 的攻击命令**不**受影响（拒绝不污染同批其他命令）。
            int reactionPlans(BattleSimulation s)
                => s.ScheduleAuthority.Registry.ActivePlans.Count(p => p.IsReaction);
        }

        // =====================================================================
        // 必需测试 18：ReactionAtOptionDeadlineIsAcceptedAndAfterDeadlineIsRejected
        // =====================================================================

        /// <summary>
        /// 选项截止是<strong>半开区间</strong>：<c>tick == ResponseDeadlineTick</c> 的命令阶段仍然接受；
        /// <c>tick == deadline + 1</c> 以 <c>REACTION_OPTION_DEADLINE_ELAPSED</c> 稳定拒绝。
        /// 两个世界都是"攻击已启动、机会已公开"，唯一变量是提交 Tick。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把截止判定写成 <c>tick &gt;= deadline</c>（截止 Tick 被误拒）
        /// 或 <c>tick &gt; deadline + 1</c>（过期命令被放行）；把过期判定放在命令阶段之后
        /// （命令能抢先接受）或之前（本 Tick 的命令被提前作废）。
        /// </para>
        /// </summary>
        [Test]
        public void ReactionAtOptionDeadlineIsAcceptedAndAfterDeadlineIsRejected()
        {
            long deadline = Task09A2Fixture.BlockOptionDeadlineTick;
            Assert.That(deadline, Is.EqualTo(10L), "夹具前提：Block 选项截止 = ImpactTick − ReactionWindup");

            // —— 世界 A：恰好截止 Tick ——
            var atDeadline = Task09A2Fixture.NewSim();
            Task09A2Fixture.ArrangeStartedAttack(atDeadline, Task09A2Fixture.Monster);
            ReactionOpportunityRuntime opportunityA =
                Task09A2Fixture.OpenOpportunityFor(atDeadline, Task09A2Fixture.Monster);
            Assert.That(Task09A2Fixture.PublishedOption(opportunityA, Task09A2Fixture.BlockSpecId)
                .ResponseDeadlineTick, Is.EqualTo(deadline));

            AdvanceTo(atDeadline, deadline - 1L);
            Task09A2Fixture.Submit(atDeadline, Task09A2Fixture.AiId,
                Task09A2Fixture.Reaction(deadline, opportunityA.Id, ReactionCommandKind.Block, BlockSpec()));
            StepResult atDeadlineResult = Task09A2Fixture.Step(atDeadline, deadline);

            Assert.That(Task09A2Fixture.RejectionCodes(atDeadlineResult), Is.Empty,
                "截止 Tick 本身仍可接受（tick > deadline 才过期）");
            Assert.That(atDeadline.ScheduleAuthority.Registry.ActivePlans.Count(p => p.IsReaction),
                Is.EqualTo(1));

            // —— 世界 B：截止 Tick + 1 ——
            var afterDeadline = Task09A2Fixture.NewSim();
            Task09A2Fixture.ArrangeStartedAttack(afterDeadline, Task09A2Fixture.Monster);
            ReactionOpportunityRuntime opportunityB =
                Task09A2Fixture.OpenOpportunityFor(afterDeadline, Task09A2Fixture.Monster);

            AdvanceTo(afterDeadline, deadline);
            string scheduleBefore = Task09A2Fixture.ScheduleFingerprint(afterDeadline);
            Task09A2Fixture.Submit(afterDeadline, Task09A2Fixture.AiId,
                Task09A2Fixture.Reaction(deadline + 1L, opportunityB.Id, ReactionCommandKind.Block, BlockSpec()));
            StepResult afterDeadlineResult = Task09A2Fixture.Step(afterDeadline, deadline + 1L);

            Assert.That(Task09A2Fixture.RejectionCodes(afterDeadlineResult).ToArray(),
                Is.EqualTo(new[] { ReactionCodes.OPTION_DEADLINE_ELAPSED }),
                "过期的选项必须稳定拒绝，绝不顺延到当前 Tick");
            Assert.That(afterDeadline.ScheduleAuthority.Registry.ActivePlans.Count(p => p.IsReaction),
                Is.Zero, "过期命令不得创建反应计划");
            Assert.That(Task09A2Fixture.ScheduleFingerprint(afterDeadline), Is.EqualTo(scheduleBefore));
        }

        // =====================================================================
        // 必需测试 19：ReactionDoesNotRequireOwnWindowOrConcurrentAuthority
        // =====================================================================

        /// <summary>
        /// 反应权与窗口<strong>正交</strong>：把当前窗口彻底关掉（并确认没有任何并发授权）之后，
        /// 仍然可以接受一条机会合法的反应命令。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：在反应路径里加"必须存在当前窗口"或
        /// "必须持有并发授权"的前置（接受失败）；把窗口关闭当成机会失效（机会被提前关闭）。
        /// </para>
        /// </summary>
        [Test]
        public void ReactionDoesNotRequireOwnWindowOrConcurrentAuthority()
        {
            var sim = Task09A2Fixture.NewSim();
            Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            ReactionOpportunityRuntime opportunity = Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);
            WindowId windowId = sim.CurrentTurnWindow.WindowId;

            // 攻击所有者（hero 的控制者）在 Tick 2 关掉自己的窗口；Tick 2 结束时窗口正式关闭。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId, Task09A2Fixture.CloseWindow(2L, windowId));
            StepResult closed = Task09A2Fixture.Step(sim, 2L);
            Assert.That(Task09A2Fixture.RejectionCodes(closed), Is.Empty, "关窗命令本身必须被接受");
            Assert.That(sim.CurrentTurnWindow, Is.Null, "夹具前提：Tick 2 结束时窗口已关闭");
            Assert.That(sim.CurrentSnapshot.ConcurrentAction.HasActiveAuthorization, Is.False,
                "夹具前提：不存在任何并发授权");
            Assert.That(opportunity.IsOpen, Is.True,
                "关闭窗口不得取消计划，也不得关闭机会（WindowId 不是动作生命周期边界）");

            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                Task09A2Fixture.Reaction(3L, opportunity.Id, ReactionCommandKind.Block, BlockSpec()));
            StepResult accepted = Task09A2Fixture.Step(sim, 3L);

            Assert.That(Task09A2Fixture.RejectionCodes(accepted), Is.Empty,
                "没有当前窗口、也没有并发授权时，机会合法的反应命令仍必须被接受");
            ActionPlan reaction = sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.IsReaction);
            Assert.That(reaction.ReservedTurnBudgetTicks, Is.Zero);
            Assert.That(reaction.SubmittedWindowId.HasValue, Is.False);
        }

        // =====================================================================
        // A2.1 补充必需测试：反应命令的控制权判据是「机会的防御者」
        // =====================================================================

        /// <summary>
        /// 发行者必须能控制<strong>机会的防御者</strong>：反应命令的 scope 里没有单位 ID
        /// （目的就是不让生产者自报作用单位），因此 <c>CommandAuthority</c> 从权威机会系统解析出
        /// 防御者，再用与排程/窗口<strong>完全相同</strong>的控制权判定求证发行者身份。
        ///
        /// <para>
        /// 本用例补的是一个真实存在的<strong>覆盖缺口</strong>：A2 的其余反应用例都让
        /// "攻击者"与"防御者的控制者"恰好是同一个主体（AI 打、AI 防），因此
        /// 把 <c>TryCollectReactionUnits</c> 改成恒返回空（= 完全不校验反应的作用单位）
        /// 时它们<strong>仍然全绿</strong>。只有"防御者不由发行者控制"这一形态能钉住该判据。
        /// </para>
        ///
        /// <para>
        /// 会让它失败的实现缺陷：反应载荷被允许自报作用单位（或干脆跳过控制权校验）⇒
        /// 玩家可以替敌人做防御决策；解析不到机会时跳过校验继续处理；拒绝时留下半提交
        /// （计划 / 机会状态 / 排程指纹任一变化）。
        /// </para>
        /// </summary>
        [Test]
        public void PlayerCannotIssueReactionForUnitItDoesNotControl()
        {
            var sim = Task09A2Fixture.NewSim();
            // hero（玩家）攻击 mon（AI）⇒ 机会的防御者是 mon。
            Task09A2Fixture.ArrangeStartedAttack(sim, Task09A2Fixture.Monster);
            ReactionOpportunityRuntime opportunity =
                Task09A2Fixture.OpenOpportunityFor(sim, Task09A2Fixture.Monster);

            string scheduleBefore = Task09A2Fixture.ScheduleFingerprint(sim);
            ReactionOpportunityState stateBefore = opportunity.State;

            // 玩家入口（它控制 hero，不控制 mon）替 mon 提交 Block。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.PlayerId,
                Task09A2Fixture.Reaction(2L, opportunity.Id, ReactionCommandKind.Block, BlockSpec()));
            StepResult rejected = Task09A2Fixture.Step(sim, 2L);

            Assert.That(Task09A2Fixture.RejectionCodes(rejected).ToArray(),
                Is.EqualTo(new[] { CommandCodes.COMMAND_ISSUER_CANNOT_CONTROL_UNIT }),
                "反应的作用单位是机会的防御者，玩家不得替别人控制的单位做防御决策");
            Assert.That(Task09A2Fixture.ScheduleFingerprint(sim), Is.EqualTo(scheduleBefore),
                "越权反应必须零局部写入");
            Assert.That(opportunity.State, Is.EqualTo(stateBefore),
                "被拒绝的反应不得消费机会");
            Assert.That(opportunity.BoundActionPlanId.IsValid, Is.False,
                "被拒绝的反应不得绑定任何计划");
            Assert.That(sim.ScheduleAuthority.Registry.ActivePlans.Count, Is.EqualTo(1),
                "仍然只有来源攻击那一条计划");
        }

        // =====================================================================
        // 必需测试 22：PlayerAndAiReactionsUseSameProcessorAndPlanner
        // =====================================================================

        /// <summary>
        /// 同一场里玩家与 AI 各发行一条反应命令，两条都经<strong>同一个</strong>
        /// <c>BattleCommandProcessor.ReactionSink</c>（同一个 <c>ReactionCommandPlanner</c> 实例）
        /// 与同一个机会系统处理；产出的反应计划形状<strong>逐字段相同</strong>。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：按 <c>CommandSourceKind</c> 分叉出第二条反应处理路径
        /// （形状不匹配、或某一侧被拒）；反应计划创建绕过机会系统。
        /// </para>
        /// </summary>
        [Test]
        public void PlayerAndAiReactionsUseSameProcessorAndPlanner()
        {
            // —— 世界 1：AI 发行 Block（hero 攻击 mon）——
            var aiWorld = Task09A2Fixture.NewSim();
            Task09A2Fixture.ArrangeStartedAttack(aiWorld, Task09A2Fixture.Monster);
            ReactionOpportunityRuntime aiOpportunity =
                Task09A2Fixture.OpenOpportunityFor(aiWorld, Task09A2Fixture.Monster);
            Task09A2Fixture.Submit(aiWorld, Task09A2Fixture.AiId,
                Task09A2Fixture.Reaction(2L, aiOpportunity.Id, ReactionCommandKind.Block, BlockSpec()));
            StepResult aiResult = Task09A2Fixture.Step(aiWorld, 2L);

            // —— 世界 2：玩家发行 Block（mon 攻击 hero）——
            var playerWorld = Task09A2Fixture.NewSim();
            Task09A2Fixture.ArrangeStartedAttack(
                playerWorld, Task09A2Fixture.Hero, Task09A2Fixture.Spec(Task09A2Fixture.AttackSpecId),
                Task09A2Fixture.Monster, seedDefenderAdrenaline: true);
            ReactionOpportunityRuntime playerOpportunity =
                Task09A2Fixture.OpenOpportunityFor(playerWorld, Task09A2Fixture.Hero);
            Task09A2Fixture.Submit(playerWorld, Task09A2Fixture.PlayerId,
                Task09A2Fixture.Reaction(2L, playerOpportunity.Id, ReactionCommandKind.Block, BlockSpec()));
            StepResult playerResult = Task09A2Fixture.Step(playerWorld, 2L);

            Assert.That(Task09A2Fixture.RejectionCodes(aiResult), Is.Empty, "AI 的反应命令必须被接受");
            Assert.That(Task09A2Fixture.RejectionCodes(playerResult), Is.Empty, "玩家的反应命令必须被接受");

            // 结构事实：两侧的反应端口是同一个类型，且它就是 sim 自己的唯一 planner。
            Assert.That(aiWorld.CommandProcessor.ReactionSink, Is.Not.Null);
            Assert.That(((PayloadRoutedCommandPort)aiWorld.CommandProcessor.ReactionSink).Kind,
                Is.EqualTo(CommandScopeKind.Reaction),
                "反应端口的载荷判别必须是 Reaction（窗口/排程载荷不可能落到它上面）");
            Assert.That(((PayloadRoutedCommandPort)aiWorld.CommandProcessor.ReactionSink).Target,
                Is.SameAs(aiWorld.ReactionCommandPlanner),
                "统一入口的反应端口必须指向本场的唯一 ReactionCommandPlanner");
            Assert.That(((PayloadRoutedCommandPort)aiWorld.CommandProcessor.ScheduleEditSink).Target,
                Is.Not.SameAs(((PayloadRoutedCommandPort)aiWorld.CommandProcessor.ReactionSink).Target),
                "反应与排程是两个不同的权威实现（不是同一条被复用的路径）");
            Assert.That(aiWorld.ReactionCommandPlanner.GetType(), Is.EqualTo(playerWorld.ReactionCommandPlanner.GetType()));
            Assert.That(aiWorld.CommandProcessor.GetType(), Is.EqualTo(playerWorld.CommandProcessor.GetType()));
            Assert.That(aiWorld.CommandProcessor, Is.Not.SameAs(playerWorld.CommandProcessor),
                "两者是同一类型的两场实例（同一实现，不是同一世界）");

            // 两侧反应计划形状逐字段相同（动作/时长/预算/窗口来源）。
            ActionPlan aiReaction = aiWorld.ScheduleAuthority.Registry.ActivePlans.Single(p => p.IsReaction);
            ActionPlan playerReaction = playerWorld.ScheduleAuthority.Registry.ActivePlans.Single(p => p.IsReaction);
            Assert.That(aiReaction.ActionType, Is.EqualTo(playerReaction.ActionType));
            Assert.That(aiReaction.ActionSpecId, Is.EqualTo(playerReaction.ActionSpecId));
            Assert.That(aiReaction.BudgetCostTicks, Is.EqualTo(playerReaction.BudgetCostTicks));
            Assert.That(aiReaction.ReservedTurnBudgetTicks, Is.EqualTo(playerReaction.ReservedTurnBudgetTicks));
            Assert.That(aiReaction.StartTick - aiReaction.TriggerTick,
                Is.EqualTo(playerReaction.StartTick - playerReaction.TriggerTick),
                "两侧的反应前摇（TriggerTick − StartTick）必须相同：差异只体现在来源与生产策略");
            Assert.That(aiReaction.IsLocked, Is.True);
            Assert.That(playerReaction.IsLocked, Is.True);
        }

        // =====================================================================
        // 必需测试 52：ConcurrentAuthorityDoesNotPermitReactionWithoutOpportunity
        // =====================================================================

        /// <summary>
        /// 并发授权<strong>不能</strong>替代机会：在"玩家已凭并发授权在他人窗口内行动"的世界里，
        /// 一条不带合法机会的反应命令仍然稳定拒绝，且不创建任何反应计划。
        ///
        /// <para>
        /// 会让它失败的实现缺陷：把"已有并发授权"当作反应的前置放行条件
        /// （于是缺机会的反应被接受）；或者反过来——把并发授权当成反应的必要条件
        /// （那样在他人窗口里本可成立的反应被误拒，由
        /// <c>ReactionDoesNotRequireOwnWindowOrConcurrentAuthority</c> 捕获）。
        /// </para>
        /// </summary>
        [Test]
        public void ConcurrentAuthorityDoesNotPermitReactionWithoutOpportunity()
        {
            // 他人（mon）的窗口：hero 不能天然提交，必须购买并发授权。
            var sim = Task09A2Fixture.NewSimWithMeta(ConcurrentActionDefinition.FrozenV1.MetaResourceCost);
            sim.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Monster, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(sim, 0L);

            TurnWindow window = sim.CurrentTurnWindow;
            Assert.That(window, Is.Not.Null, "夹具前提：必须打开一个窗口");
            Assert.That(window.OwnerUnitId, Is.EqualTo(Task09A2Fixture.Monster),
                "夹具前提：窗口属于 mon（他人窗口）");

            // 玩家经命令入口激活并发行动（主角由装配方给出的权威单位唯一决定）。
            var assembly = new BattleSimulationAssembly(
                concurrentHeroUnitId: Task09A2Fixture.Hero,
                turnWindowSchedule: null);
            var concurrentSim = Task09A2Fixture.NewSimWithMeta(
                ConcurrentActionDefinition.FrozenV1.MetaResourceCost, assembly: assembly);
            concurrentSim.WindowManager.ScheduleWindow(0L, Task09A2Fixture.Monster, Task09A2Fixture.WindowBudget);
            Task09A2Fixture.Step(concurrentSim, 0L);
            WindowId concurrentWindowId = concurrentSim.CurrentTurnWindow.WindowId;

            Task09A2Fixture.Submit(concurrentSim, Task09A2Fixture.PlayerId, new CommandRequest(
                1L, new WindowCommandScope(concurrentWindowId),
                new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction)));
            StepResult activated = Task09A2Fixture.Step(concurrentSim, 1L);

            Assert.That(Task09A2Fixture.RejectionCodes(activated), Is.Empty,
                "夹具前提：并发激活本身必须成功（费用来自权威定义）");
            Assert.That(concurrentSim.CurrentSnapshot.ConcurrentAction.HasActiveAuthorization, Is.True,
                "夹具前提：并发授权确实已生效");

            // 在授权生效的同一 Tick 上，一条缺机会的反应命令仍然稳定拒绝。
            string scheduleBefore = Task09A2Fixture.ScheduleFingerprint(concurrentSim);
            Task09A2Fixture.Submit(concurrentSim, Task09A2Fixture.PlayerId,
                Task09A2Fixture.Reaction(2L, new ReactionOpportunityId(4242L),
                    ReactionCommandKind.Block, BlockSpec()));
            StepResult rejected = Task09A2Fixture.Step(concurrentSim, 2L);

            string[] codes = Task09A2Fixture.RejectionCodes(rejected).ToArray();
            Assert.That(codes.Length, Is.EqualTo(1), "恰好一条稳定拒绝");
            Assert.That(
                    codes[0] == CommandCodes.COMMAND_ISSUER_UNIT_UNRESOLVABLE
                    || codes[0] == ReactionOpportunityCodes.OPPORTUNITY_NOT_OPEN,
                Is.True,
                "并发授权不得替代机会；实测=" + codes[0]);
            Assert.That(concurrentSim.ScheduleAuthority.Registry.ActivePlans.Count(p => p.IsReaction),
                Is.Zero, "缺机会的反应不得创建计划");
            Assert.That(Task09A2Fixture.ScheduleFingerprint(concurrentSim), Is.EqualTo(scheduleBefore));
            Assert.That(concurrentSim.ReactionOpportunities.ActiveOpportunities.Count, Is.Zero);

            // 夹具自检：普通场景（无并发授权）同样拒绝，因此上面的结论不依赖授权状态。
            Task09A2Fixture.Submit(sim, Task09A2Fixture.AiId,
                Task09A2Fixture.Reaction(1L, new ReactionOpportunityId(4242L),
                    ReactionCommandKind.Block, BlockSpec()));
            StepResult plainRejected = Task09A2Fixture.Step(sim, 1L);
            Assert.That(Task09A2Fixture.RejectionCodes(plainRejected).Count, Is.EqualTo(1),
                "无并发授权的普通场景也必须拒绝同一条缺机会的反应命令");
        }
    }
}
