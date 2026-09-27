using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Authoring.Tests.Task05
{
    /// <summary>
    /// 任务 05 阶段 D：<c>ReactionOpportunitySystem</c> / <c>ReactionPlanner</c> /
    /// 机会六态 / 选项截止与过期 / 命令提前量过滤 / 来源威胁取消的预留释放通知。
    ///
    /// 冻结时序（来自 <see cref="Task05Farm"/>）：Block 前摇 60、Dodge 前摇 30；
    /// 长前摇攻击 = 200 ⇒ Block 截止 = Impact − 60、Dodge 截止 = Impact − 30。
    /// </summary>
    [TestFixture]
    public sealed class Task05ReactionOpportunityTests
    {
        private static long IngressLead => Task05Farm.ReactionRules.CommandIngressLeadTicks;

        private static long MinimumLead => Task05Farm.ReactionRules.MinimumReactionLeadTicks;

        /// <summary>可观测事件（不注入 outbox 时事件留在系统内部缓冲里）。</summary>
        private static int CountOf<T>(ReactionOpportunitySystem system) where T : LogicEvent
        {
            int count = 0;
            IReadOnlyList<LogicEvent> events = system.EmittedEvents;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] is T) count++;
            }
            return count;
        }

        /// <summary>按选项 ActionSpecId 统计过期事件（每个选项只应出现一次）。</summary>
        private static int ExpiredCountOf(ReactionOpportunitySystem system, string actionSpecId)
        {
            int count = 0;
            IReadOnlyList<LogicEvent> events = system.EmittedEvents;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] is ReactionOptionExpiredEvent expired &&
                    expired.ActionSpecId == actionSpecId) count++;
            }
            return count;
        }

        private sealed class Rig
        {
            public readonly Task05Scheduler S;
            public readonly ReactionOpportunitySystem System;
            public readonly List<ReactionOpportunityRuntime> Opened = new List<ReactionOpportunityRuntime>();

            public Rig(IAreaThreatCandidateSource area = null, bool withAlly = false)
            {
                S = withAlly
                    ? new Task05Scheduler(null, null, null, Task05Farm.FactionsWithAlly(), Task05Farm.FactsWithAlly())
                    : new Task05Scheduler();
                // 任务 05 收尾 R1（缺陷 D2）：权威的 NextReactionOptionSequence 只是唯一分配器
                // （任务 03 契约的 LogicIdGenerator）的只读投影，因此夹具必须像生产
                // BattleSimulation 那样把同一个生成器交给权威，否则镜像会指向另一个生成器。
                S.Authority.IdGenerator = S.Ids;
                System = new ReactionOpportunitySystem(
                    S.Authority, S.Factory, S.Bundle.Definition, S.Bundle.Factions,
                    S.Ids, area, new ActionPlanTerminalCoordinator(S.Authority));
            }

            /// <summary>把一条攻击推进到"已锁定并启动"（首版 TelegraphTick = StartTick）。</summary>
            public ActionPlan Telegraph(string actionSpecId, long startTick, UnitId? owner = null, UnitId? target = null)
            {
                ActionPlanCreationResult result = S.Bundle.CreatePlan(actionSpecId, startTick, owner, target);
                Assert.That(result.Succeeded, Is.True, result.RejectionCode);
                ActionPlan plan = result.Plan;
                S.Authority.RegisterPlan(plan);
                Task05Scheduler.Lock(plan, startTick);
                return plan;
            }

            public string Open(ActionPlan source, long tick)
            {
                Opened.Clear();
                return System.TryOpenForTelegraph(source, tick, Opened);
            }

            public ReactionOptionRuntime Option(int opportunityIndex, string actionSpecId)
            {
                ReactionOpportunityRuntime opportunity = Opened[opportunityIndex];
                for (int i = 0; i < opportunity.Options.Count; i++)
                {
                    if (opportunity.Options[i].ReactionActionSpecId.Value == actionSpecId)
                        return opportunity.Options[i];
                }
                Assert.Fail("选项不存在：" + actionSpecId);
                return null;
            }
        }

        /// <summary>区域候选来源桩（真实几何属于任务 06）。</summary>
        private sealed class StubAreaSource : IAreaThreatCandidateSource
        {
            public readonly List<UnitId> Candidates = new List<UnitId>();

            public IReadOnlyList<UnitId> CandidatesFor(ActionPlan sourcePlan, long telegraphTick) => Candidates;
        }

        private sealed class RecordingReleaseSink : IReactionReservationReleaseSink
        {
            public readonly List<ReactionReservationReleaseRequest> Requests =
                new List<ReactionReservationReleaseRequest>();

            public void ReleaseFor(ActionPlanId reactionPlanId, ReactionOpportunityId opportunityId, long tick)
                => Requests.Add(new ReactionReservationReleaseRequest(reactionPlanId, opportunityId, tick));
        }

        // ————————————————————————————————————————————————————————————
        // 公开时机
        // ————————————————————————————————————————————————————————————

        /// <summary><c>AttackCannotOpenReactionOpportunityBeforeTelegraphTick</c>。</summary>
        [Test]
        public void AttackCannotOpenReactionOpportunityBeforeTelegraphTick()
        {
            Rig rig = new Rig();
            ActionPlan attack = rig.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);

            string error = rig.Open(attack, 99L);
            Assert.That(error, Is.EqualTo(ReactionOpportunityCodes.SOURCE_NOT_TELEGRAPHED));
            Assert.That(rig.System.ActiveOpportunities.Count, Is.EqualTo(0), "TelegraphTick 之前不得创建机会");

            Assert.That(rig.Open(attack, 100L), Is.Null, "进入 TelegraphTick 后必须公开");
            Assert.That(rig.Opened.Count, Is.EqualTo(1));
            Assert.That(rig.System.ActiveOpportunities.Count, Is.EqualTo(1));
        }

        /// <summary><c>EditableAttackCannotOpenReactionOpportunityBeforeLockAndStart</c>。</summary>
        [Test]
        public void EditableAttackCannotOpenReactionOpportunityBeforeLockAndStart()
        {
            Rig rig = new Rig();
            ActionPlanCreationResult created = rig.S.Bundle.CreatePlan(Task05Farm.LongTelegraphAttackId, 100L);
            Assert.That(created.Succeeded, Is.True, created.RejectionCode);
            ActionPlan editable = created.Plan;
            rig.S.Authority.RegisterPlan(editable);
            Assert.That(editable.IsEditable, Is.True);

            string error = rig.Open(editable, 100L);
            Assert.That(error, Is.EqualTo(ReactionOpportunityCodes.SOURCE_NOT_TELEGRAPHED));
            Assert.That(rig.System.ActiveOpportunities.Count, Is.EqualTo(0), "Editable 攻击不得公开机会");

            // 正控制：同一条计划在锁定并启动之后必须公开。
            Task05Scheduler.Lock(editable, 100L);
            Assert.That(rig.Open(editable, 100L), Is.Null);
            Assert.That(rig.Opened.Count, Is.EqualTo(1));
        }

        /// <summary><c>UnreactableAttackDoesNotOpenReactionOpportunity</c>。</summary>
        [Test]
        public void UnreactableAttackDoesNotOpenReactionOpportunity()
        {
            Rig rig = new Rig();
            ActionPlan attack = rig.Telegraph(Task05Farm.UnreactableAttackId, 100L);
            Assert.That(rig.Open(attack, 100L), Is.EqualTo(ReactionOpportunityCodes.ATTACK_NOT_REACTABLE));
            Assert.That(rig.System.ActiveOpportunities.Count, Is.EqualTo(0));
        }

        /// <summary>
        /// <c>OpportunityCandidatesDoNotDependOnPlayerOrAiControllerKind</c>：
        /// 候选生成在<strong>结构上</strong>不读玩家标志或 Controller 类型。
        /// </summary>
        [Test]
        public void OpportunityCandidatesDoNotDependOnPlayerOrAiControllerKind()
        {
            Type type = typeof(ReactionOpportunitySystem);
            var members = new List<MemberInfo>();
            members.AddRange(type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.Instance | BindingFlags.Static));
            members.AddRange(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                            BindingFlags.Instance | BindingFlags.Static));

            for (int i = 0; i < members.Count; i++)
            {
                string name = members[i].Name.ToLowerInvariant();
                Assert.That(name.Contains("player"), Is.False, "不得存在玩家标志成员：" + members[i].Name);
                Assert.That(name.Contains("controller"), Is.False, "不得存在 Controller 成员：" + members[i].Name);
            }

            // 断言级证据：同一条攻击在把"是否玩家控制"完全排除之后仍然生成候选。
            Rig rig = new Rig();
            ActionPlan attack = rig.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);
            Assert.That(rig.Open(attack, 100L), Is.Null);
            Assert.That(rig.Opened[0].DefenderUnitId, Is.EqualTo(Task05Farm.Enemy));
        }

        /// <summary>
        /// <c>AreaOpportunityCandidatesUseSameFactionRelationMaskAsAttack</c>：
        /// 区域候选与固定目标走<strong>同一</strong>关系掩码判定（真实几何属于任务 06，
        /// 本用例注入确定性候选来源）。
        /// </summary>
        [Test]
        public void AreaOpportunityCandidatesUseSameFactionRelationMaskAsAttack()
        {
            var area = new StubAreaSource();
            area.Candidates.Add(Task05Farm.Enemy);   // Hostile ⇒ 掩码放行
            area.Candidates.Add(Task05Farm.Hero);    // 来源自己 ⇒ 必须排除
            area.Candidates.Add(new UnitId(99L));    // 未知单位 ⇒ 必须排除
            Rig rig = new Rig(area);

            ActionPlan attack = rig.Telegraph(Task05Farm.AreaAttackId, 100L);
            Assert.That(rig.Open(attack, 100L), Is.Null);
            Assert.That(rig.Opened.Count, Is.EqualTo(1), "区域威胁只放行通过关系掩码的候选");
            Assert.That(rig.Opened[0].DefenderUnitId, Is.EqualTo(Task05Farm.Enemy));
            Assert.That(rig.Opened[0].SourceAttackPlanId, Is.EqualTo(attack.ActionPlanId));
            Assert.That(rig.Opened[0].TelegraphTick, Is.EqualTo(100L));

            // 同一掩码：把候选换成一个掩码不放行的单位时不得生成机会。
            var blocked = new StubAreaSource();
            blocked.Candidates.Add(Task05Farm.Hero);
            Rig onlySelf = new Rig(blocked);
            ActionPlan onlySelfAttack = onlySelf.Telegraph(Task05Farm.AreaAttackId, 100L);
            Assert.That(onlySelf.Open(onlySelfAttack, 100L),
                Is.EqualTo(ReactionOpportunityCodes.NO_REACHABLE_OPTION));
            Assert.That(onlySelf.System.ActiveOpportunities.Count, Is.EqualTo(0));
        }

        /// <summary><c>ExplicitFriendlyFireCanCreateFriendlyReactionOpportunity</c>。</summary>
        [Test]
        public void ExplicitFriendlyFireCanCreateFriendlyReactionOpportunity()
        {
            Rig rig = new Rig(withAlly: true);
            ActionPlan attack = rig.Telegraph(
                Task05Farm.FriendlyFireAttackId, 100L, Task05Farm.Hero, Task05Farm.Ally);
            Assert.That(attack.PrimaryTargetUnitId, Is.EqualTo(Task05Farm.Ally));

            Assert.That(rig.Open(attack, 100L), Is.Null, "显式友军伤害必须生成友军机会");
            Assert.That(rig.Opened.Count, Is.EqualTo(1));
            Assert.That(rig.Opened[0].DefenderUnitId, Is.EqualTo(Task05Farm.Ally),
                "友军威胁与敌对威胁走完全相同流程");
        }

        // ————————————————————————————————————————————————————————————
        // 选项截止与公开门槛
        // ————————————————————————————————————————————————————————————

        /// <summary><c>ReactionOptionDeadlineIsDerivedFromImpactAndReactionWindup</c>。</summary>
        [Test]
        public void ReactionOptionDeadlineIsDerivedFromImpactAndReactionWindup()
        {
            Rig rig = new Rig();
            ActionPlan attack = rig.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);
            long impact = attack.ImpactTick;
            Assert.That(impact - attack.StartTick, Is.GreaterThanOrEqualTo(MinimumLead));

            Assert.That(rig.Open(attack, 100L), Is.Null);
            ReactionOptionRuntime block = rig.Option(0, Task05Farm.BlockId);
            ReactionOptionRuntime dodge = rig.Option(0, Task05Farm.DodgeId);

            Assert.That(block.ResponseDeadlineTick, Is.EqualTo(impact - Task05Farm.BlockReactionWindup));
            Assert.That(dodge.ResponseDeadlineTick, Is.EqualTo(impact - Task05Farm.DodgeReactionWindup));
            Assert.That(rig.Opened[0].TriggerTick, Is.EqualTo(impact), "TriggerTick 恒等于来源 ImpactTick");

            // 选项按 ActionSpecId Ordinal 升序。
            Assert.That(rig.Opened[0].Options[0].ReactionActionSpecId.Value,
                Is.EqualTo(Task05Farm.BlockId), "block 必须排在 dodge 之前（Ordinal）");
        }

        /// <summary><c>ReactionOptionWithoutCommandIngressLeadIsNotPublished</c>。</summary>
        [Test]
        public void ReactionOptionWithoutCommandIngressLeadIsNotPublished()
        {
            Rig rig = new Rig();
            // 基础攻击前摇 30 ⇒ 即使 Dodge 截止也只有 StartTick + 0，达不到命令提前量。
            ActionPlan attack = rig.Telegraph(Task05Farm.AttackId, 100L);
            Assert.That(attack.ImpactTick - attack.StartTick, Is.EqualTo(30));
            Assert.That(IngressLead, Is.GreaterThan(0));

            string error = rig.Open(attack, 100L);
            Assert.That(error, Is.EqualTo(ReactionOpportunityCodes.NO_REACHABLE_OPTION));
            Assert.That(rig.System.ActiveOpportunities.Count, Is.EqualTo(0),
                "没有任何选项满足命令提前量时不得创建机会");
        }

        /// <summary><c>ReactionOptionsRequireCompatibleSourceTags</c>。</summary>
        [Test]
        public void ReactionOptionsRequireCompatibleSourceTags()
        {
            Rig noDodge = new Rig();
            ActionPlan onlyBlockSource = noDodge.Telegraph(Task05Farm.NoDodgeAttackId, 100L);
            Assert.That(noDodge.Open(onlyBlockSource, 100L), Is.Null);
            Assert.That(noDodge.Opened[0].Options.Count, Is.EqualTo(1));
            Assert.That(noDodge.Opened[0].Options[0].ReactionActionSpecId.Value, Is.EqualTo(Task05Farm.BlockId),
                "缺 Dodgeable 时不得提供 Dodge");

            Rig noBlock = new Rig();
            ActionPlan onlyDodgeSource = noBlock.Telegraph(Task05Farm.NoBlockAttackId, 100L);
            Assert.That(noBlock.Open(onlyDodgeSource, 100L), Is.Null);
            Assert.That(noBlock.Opened[0].Options.Count, Is.EqualTo(1));
            Assert.That(noBlock.Opened[0].Options[0].ReactionActionSpecId.Value, Is.EqualTo(Task05Farm.DodgeId),
                "没有可格挡分量且动量为零时不得提供 Block");
        }

        // ————————————————————————————————————————————————————————————
        // 过期与关闭（每项一次 / 最后一项关闭 / 关闭只一次）
        // ————————————————————————————————————————————————————————————

        /// <summary><c>ReactionOptionDeadlineIsInclusiveAndExpiresAfterCommandPhase</c>。</summary>
        [Test]
        public void ReactionOptionDeadlineIsInclusiveAndExpiresAfterCommandPhase()
        {
            Rig rig = new Rig();
            ActionPlan attack = rig.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);
            Assert.That(rig.Open(attack, 100L), Is.Null);
            ReactionOptionRuntime dodge = rig.Option(0, Task05Farm.DodgeId);
            long deadline = dodge.ResponseDeadlineTick;

            // 截止 Tick 本身仍然可接受（含端点）。
            Assert.That(rig.System.TryAccept(
                rig.Opened[0].Id, Task05Farm.Enemy, new ActionSpecId(Task05Farm.DodgeId), deadline,
                out ActionPlan accepted), Is.Null, "截止 Tick 的命令阶段内必须仍可接受");
            Assert.That(accepted, Is.Not.Null);

            // 对照：超过截止一个 Tick 即拒绝。
            Rig late = new Rig();
            ActionPlan lateAttack = late.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);
            Assert.That(late.Open(lateAttack, 100L), Is.Null);
            ReactionOptionRuntime lateDodge = late.Option(0, Task05Farm.DodgeId);
            Assert.That(late.System.TryAccept(
                late.Opened[0].Id, Task05Farm.Enemy, new ActionSpecId(Task05Farm.DodgeId),
                lateDodge.ResponseDeadlineTick + 1L, out _),
                Is.EqualTo(ReactionCodes.OPTION_DEADLINE_ELAPSED));

            // 恰好等于截止 Tick 时，<strong>该选项</strong>不产生过期事件
            // （Block 的截止更早，在同一个 Tick 上本来就应该已经过期，因此按选项分别计数）。
            late.System.ExpireDueOptions(lateDodge.ResponseDeadlineTick);
            Assert.That(ExpiredCountOf(late.System, Task05Farm.DodgeId), Is.EqualTo(0),
                "截止 Tick 的命令阶段尚未结束，不得过期");
            Assert.That(ExpiredCountOf(late.System, Task05Farm.BlockId), Is.EqualTo(1),
                "更早截止的 Block 在同一 Tick 上本来就应该过期（对照）");
        }

        /// <summary>
        /// <c>EachReactionOptionEmitsExpiryOnceAndLastOptionClosesOpportunity</c>。
        /// </summary>
        [Test]
        public void EachReactionOptionEmitsExpiryOnceAndLastOptionClosesOpportunity()
        {
            Rig rig = new Rig();
            ActionPlan attack = rig.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);
            Assert.That(rig.Open(attack, 100L), Is.Null);
            ReactionOptionRuntime block = rig.Option(0, Task05Farm.BlockId);
            ReactionOptionRuntime dodge = rig.Option(0, Task05Farm.DodgeId);
            Assert.That(block.ResponseDeadlineTick, Is.LessThan(dodge.ResponseDeadlineTick));

            rig.System.ExpireDueOptions(block.ResponseDeadlineTick + 1L);
            Assert.That(CountOf<ReactionOptionExpiredEvent>(rig.System), Is.EqualTo(1));
            Assert.That(rig.Opened[0].IsOpen, Is.True, "仍有开放选项 ⇒ 机会不得关闭");
            Assert.That(CountOf<ReactionOpportunityClosedEvent>(rig.System), Is.EqualTo(0));

            // 同一选项不得重复过期（用一个 > block 截止但 <= dodge 截止的 Tick，只考察 block）。
            rig.System.ExpireDueOptions(dodge.ResponseDeadlineTick);
            Assert.That(CountOf<ReactionOptionExpiredEvent>(rig.System), Is.EqualTo(1));

            rig.System.ExpireDueOptions(dodge.ResponseDeadlineTick + 1L);
            Assert.That(CountOf<ReactionOptionExpiredEvent>(rig.System), Is.EqualTo(2));
            Assert.That(rig.Opened[0].State, Is.EqualTo(ReactionOpportunityState.Expired));
            Assert.That(rig.Opened[0].CloseReason, Is.EqualTo(ReactionCloseReasons.AllOptionsExpired));
            Assert.That(CountOf<ReactionOpportunityClosedEvent>(rig.System), Is.EqualTo(1));
        }

        /// <summary><c>ReactionOpportunityLeavesOpenAndEmitsCloseOnlyOnce</c>。</summary>
        [Test]
        public void ReactionOpportunityLeavesOpenAndEmitsCloseOnlyOnce()
        {
            Rig rig = new Rig();
            ActionPlan attack = rig.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);
            Assert.That(rig.Open(attack, 100L), Is.Null);

            rig.System.CloseAllForBattleEnd(200L);
            Assert.That(rig.Opened[0].State, Is.EqualTo(ReactionOpportunityState.BattleEnded));
            Assert.That(rig.Opened[0].CloseReason, Is.EqualTo(ReactionCloseReasons.BattleEnded));
            Assert.That(CountOf<ReactionOpportunityClosedEvent>(rig.System), Is.EqualTo(1));

            // 已离开 Open 之后：任何后续处理都不得再发关闭事件，也不得再接收命令。
            rig.System.CloseAllForBattleEnd(201L);
            rig.System.ExpireDueOptions(9999L);
            Assert.That(CountOf<ReactionOpportunityClosedEvent>(rig.System), Is.EqualTo(1), "关闭事件只发一次");
            Assert.That(rig.System.TryAccept(
                rig.Opened[0].Id, Task05Farm.Enemy, new ActionSpecId(Task05Farm.BlockId), 200L, out _),
                Is.EqualTo(ReactionOpportunityCodes.OPPORTUNITY_NOT_OPEN));
        }

        // ————————————————————————————————————————————————————————————
        // 接受：Locked 计划、固定区间、TriggerTick 推导
        // ————————————————————————————————————————————————————————————

        /// <summary><c>ReactionCommandCannotProvideTriggerTick</c>。</summary>
        [Test]
        public void ReactionCommandCannotProvideTriggerTick()
        {
            Type payload = typeof(ProjectHero.Logic.Commands.ReactionCommandPayload);
            MemberInfo[] members = payload.GetMembers(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < members.Length; i++)
            {
                Assert.That(members[i].Name.Contains("TriggerTick"), Is.False,
                    "反应命令载荷在类型上不可能提供 TriggerTick：" + members[i].Name);
            }

            Rig rig = new Rig();
            ActionPlan attack = rig.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);
            Assert.That(rig.Open(attack, 100L), Is.Null);
            Assert.That(rig.System.TryAccept(
                rig.Opened[0].Id, Task05Farm.Enemy, new ActionSpecId(Task05Farm.DodgeId), 110L,
                out ActionPlan plan), Is.Null);

            Assert.That(plan.TriggerBinding, Is.Not.Null);
            Assert.That(plan.TriggerBinding.TriggerTick, Is.EqualTo(attack.ImpactTick),
                "TriggerTick 只能由来源攻击的 ImpactTick 推导");
            Assert.That(plan.TriggerBinding.SourceThreatPlanId, Is.EqualTo(attack.ActionPlanId));
            Assert.That(plan.TriggerBinding.ReactionOpportunityId, Is.EqualTo(rig.Opened[0].Id));
        }

        /// <summary>
        /// <c>ReactionPlanIsCreatedLockedWithZeroBudgetAndNullWindowOnAccept</c>（任务包 §11 第四段）。
        /// </summary>
        [Test]
        public void ReactionPlanIsCreatedLockedWithZeroBudgetAndNullWindowOnAccept()
        {
            Rig rig = new Rig();
            ActionPlan attack = rig.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);
            Assert.That(rig.Open(attack, 100L), Is.Null);
            long revisionBefore = rig.S.Revision;

            Assert.That(rig.System.TryAccept(
                rig.Opened[0].Id, Task05Farm.Enemy, new ActionSpecId(Task05Farm.BlockId), 110L,
                out ActionPlan plan), Is.Null);

            Assert.That(plan.State, Is.EqualTo(ActionPlanState.Locked), "反应计划接受后直接 Locked");
            Assert.That(plan.IsEditable, Is.False, "不存在可观察的 Editable 反应计划");
            Assert.That(plan.SubmittedWindowId, Is.Null, "SubmittedWindowId 恒为空");
            Assert.That(plan.BudgetCostTicks, Is.EqualTo(0), "BudgetCostTicks 恒为 0");
            Assert.That(plan.StartTick, Is.EqualTo(attack.ImpactTick - Task05Farm.BlockReactionWindup));
            Assert.That(plan.EndTick, Is.EqualTo(attack.ImpactTick + Task05Farm.BlockRecovery));
            Assert.That(rig.S.Revision, Is.EqualTo(revisionBefore), "接受反应不是排程编辑，不推进修订号");
            Assert.That(rig.Opened[0].State, Is.EqualTo(ReactionOpportunityState.Accepted));
            Assert.That(rig.Opened[0].BoundActionPlanId, Is.EqualTo(plan.ActionPlanId));
            Assert.That(rig.S.Authority.Registry.Contains(plan.ActionPlanId), Is.True,
                "必须已进入权威注册表");
            Assert.That(rig.S.Authority.LaneOfPlan(plan.ActionPlanId), Is.Not.Null, "必须已进入 Lane");
        }

        /// <summary><c>ReactionFixedIntervalCannotBeShiftedByLaneTail</c>。</summary>
        [Test]
        public void ReactionFixedIntervalCannotBeShiftedByLaneTail()
        {
            Rig rig = new Rig();
            ActionPlan attack = rig.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);
            Assert.That(rig.Open(attack, 100L), Is.Null);
            ReactionOptionRuntime block = rig.Option(0, Task05Farm.BlockId);

            // 在<strong>防御者</strong> Lane 上放一个覆盖固定区间的 Locked 障碍。
            long intervalStart = attack.ImpactTick - Task05Farm.BlockReactionWindup;
            ActionPlanCreationResult obstacleResult = rig.S.Bundle.CreatePlan(
                Task05Farm.AttackId, intervalStart + 10L, owner: Task05Farm.Enemy, target: Task05Farm.Hero);
            Assert.That(obstacleResult.Succeeded, Is.True, obstacleResult.RejectionCode);
            ActionPlan obstacle = obstacleResult.Plan;
            rig.S.Authority.RegisterPlan(obstacle);
            Task05Scheduler.Lock(obstacle, 0L);

            int activeBefore = rig.S.Authority.Registry.ActiveCount;
            string error = rig.System.TryAccept(
                rig.Opened[0].Id, Task05Farm.Enemy, new ActionSpecId(Task05Farm.BlockId), 110L, out ActionPlan plan);

            Assert.That(error, Is.EqualTo(ReactionCodes.REACTION_LANE_INTERVAL_OCCUPIED),
                "固定区间与 Lane 投影重叠必须稳定拒绝，绝不平移反应区间");
            Assert.That(plan, Is.Null);
            Assert.That(rig.S.Authority.Registry.ActiveCount, Is.EqualTo(activeBefore), "拒绝必须零局部写入");
            Assert.That(obstacle.StartTick, Is.EqualTo(intervalStart + 10L), "障碍绝不被抢占");
            Assert.That(block.IsOpen, Is.True, "被拒后选项必须仍然开放");
        }

        /// <summary><c>SourceThreatCancelledTerminatesBoundReactionAndRequestsReservationRelease</c>。</summary>
        [Test]
        public void SourceThreatCancelledTerminatesBoundReactionAndRequestsReservationRelease()
        {
            Rig rig = new Rig();
            var sink = new RecordingReleaseSink();
            rig.System.ReservationReleaseSink = sink;

            ActionPlan attack = rig.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);
            Assert.That(rig.Open(attack, 100L), Is.Null);
            Assert.That(rig.System.TryAccept(
                rig.Opened[0].Id, Task05Farm.Enemy, new ActionSpecId(Task05Farm.BlockId), 110L,
                out ActionPlan reaction), Is.Null);

            List<ActionPlanId> terminated = rig.System.CancelForSourceThreat(attack.ActionPlanId, 120L);

            Assert.That(terminated.Count, Is.EqualTo(1));
            Assert.That(terminated[0], Is.EqualTo(reaction.ActionPlanId));
            Assert.That(reaction.IsTerminal, Is.True);
            Assert.That(reaction.TerminationReason,
                Is.EqualTo(ActionTerminationReason.SourceThreatCancelled));
            Assert.That(rig.Opened[0].State, Is.EqualTo(ReactionOpportunityState.SourceCancelled));
            Assert.That(rig.Opened[0].CloseReason, Is.EqualTo(ReactionCloseReasons.SourceThreatCancelled));
            // 已被接受的选项保持"已接受"（它不是过期、也不是被取消）；
            // 其余仍然开放的选项才标记为 SourceCancelled。
            Assert.That(rig.Option(0, Task05Farm.BlockId).OutcomeCode,
                Is.EqualTo(ReactionOptionOutcomes.Accepted));
            Assert.That(rig.Option(0, Task05Farm.DodgeId).OutcomeCode,
                Is.EqualTo(ReactionOptionOutcomes.SourceCancelled));
            Assert.That(sink.Requests.Count, Is.EqualTo(1), "必须向任务 07 发出释放未消费预留的通知");
            Assert.That(sink.Requests[0].ReactionPlanId, Is.EqualTo(reaction.ActionPlanId));
            Assert.That(attack.IsTerminal, Is.False, "来源攻击不因反应取消而终止");
        }

        /// <summary><c>LockedReactionCannotAutoDefer</c>。</summary>
        [Test]
        public void LockedReactionCannotAutoDefer()
        {
            Rig rig = new Rig();
            Assert.That(rig.S.Bundle.CreateReaction(
                Task05Farm.BlockId, triggerTick: 300L, responseDeadlineTick: 200L,
                sourcePlanIdValue: 1L, opportunityIdValue: 1L).Succeeded, Is.True);
            ActionPlan reaction = rig.S.Bundle.CreateReaction(
                Task05Farm.BlockId, triggerTick: 300L, responseDeadlineTick: 200L,
                sourcePlanIdValue: 1L, opportunityIdValue: 2L).Plan;
            rig.S.Authority.RegisterPlan(reaction);

            long revisionBefore = rig.S.Revision;
            ScheduleEditTransactionResult deferred = rig.S.Editor.ApplySystemAutoDeferral(
                reaction, retryAtTick: 400L, currentTick: 100L, claimedPlans: null);

            Assert.That(deferred.Committed, Is.False);
            Assert.That(deferred.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_PLAN_NOT_EDITABLE));
            Assert.That(reaction.StartTick, Is.EqualTo(240L), "固定区间不得被平移");
            Assert.That(rig.S.Revision, Is.EqualTo(revisionBefore));
        }

        /// <summary>
        /// <c>DodgeTriggeredRequiresSwapTransactionSeam</c>：<c>Triggered</c> 只能经显式确认接缝推进，
        /// 生成 Intent 不等于实际触发，本任务不伪造触发。
        /// </summary>
        [Test]
        public void DodgeTriggeredRequiresSwapTransactionSeam()
        {
            Rig rig = new Rig();
            ActionPlan attack = rig.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);
            Assert.That(rig.Open(attack, 100L), Is.Null);

            // 仍为 Open：确认接缝必须拒绝（任务 06/08 的换位事务尚未发生）。
            Assert.That(rig.System.ConfirmTrigger(rig.Opened[0].Id, 200L),
                Is.EqualTo(ReactionOpportunityCodes.OPPORTUNITY_NOT_OPEN));
            Assert.That(rig.Opened[0].State, Is.EqualTo(ReactionOpportunityState.Open));
            Assert.That(CountOf<ReactionTriggeredEvent>(rig.System), Is.EqualTo(0));

            Assert.That(rig.System.TryAccept(
                rig.Opened[0].Id, Task05Farm.Enemy, new ActionSpecId(Task05Farm.DodgeId), 110L,
                out ActionPlan dodge), Is.Null);
            Assert.That(dodge.ActionType, Is.EqualTo(ActionType.Dodge));

            // Accepted 但尚未确认 ⇒ 依旧不是 Triggered，也不得发射触发事件。
            Assert.That(rig.Opened[0].State, Is.EqualTo(ReactionOpportunityState.Accepted));
            Assert.That(CountOf<ReactionTriggeredEvent>(rig.System), Is.EqualTo(0));

            // 换位事务成功之后的确认接缝：只有这里能推进到 Triggered。
            Assert.That(rig.System.ConfirmTrigger(rig.Opened[0].Id, dodge.StartTick), Is.Null);
            Assert.That(rig.Opened[0].State, Is.EqualTo(ReactionOpportunityState.Triggered));
            Assert.That(CountOf<ReactionTriggeredEvent>(rig.System), Is.EqualTo(1));
            Assert.That(rig.System.ConfirmTrigger(rig.Opened[0].Id, dodge.StartTick), Is.Null,
                "重复确认必须幂等且不重复发事件");
            Assert.That(CountOf<ReactionTriggeredEvent>(rig.System), Is.EqualTo(1));
        }

        /// <summary>
        /// <c>ReactionOpportunityStateAndNextIdAppearInCanonicalSnapshot</c>。
        /// </summary>
        [Test]
        public void ReactionOpportunityStateAndNextIdAppearInCanonicalSnapshot()
        {
            Rig rig = new Rig();
            ActionPlan attack = rig.Telegraph(Task05Farm.LongTelegraphAttackId, 100L);
            Assert.That(rig.Open(attack, 100L), Is.Null);

            // 任务 05 收尾 R1（缺陷 D2）：NextReactionOptionSequence 现在是唯一分配器
            // （LogicIdGenerator.NextReactionOpportunityId）的只读投影，而不是第二份计数状态。
            long sequenceAfterOpen = rig.S.Authority.NextReactionOptionSequence;
            Assert.That(sequenceAfterOpen, Is.EqualTo(2L), "机会 ID 与选项顺序号都不复用既有计数器");
            Assert.That(sequenceAfterOpen, Is.EqualTo(rig.S.Ids.NextReactionOpportunityIdValue),
                "权威只投影唯一分配器；本属性不得自行取号");
            Assert.That(sequenceAfterOpen, Is.EqualTo(rig.Opened[0].Id.Value + 1L),
                "机会 ID 来自唯一分配器，镜像恰好是它的下一个值");

            var snapshots = rig.System.BuildSnapshots();
            Assert.That(snapshots.Count, Is.EqualTo(1));
            Assert.That(snapshots[0].ReactionOpportunityId, Is.EqualTo(rig.Opened[0].Id.Value));
            Assert.That(snapshots[0].State, Is.EqualTo((int)ReactionOpportunityState.Open));
            Assert.That(snapshots[0].TelegraphTick, Is.EqualTo(attack.StartTick));
            Assert.That(snapshots[0].TriggerTick, Is.EqualTo(attack.ImpactTick));
            Assert.That(snapshots[0].SourceAttackPlanId, Is.EqualTo(attack.ActionPlanId.Value));
            Assert.That(snapshots[0].Options.Count, Is.EqualTo(rig.Opened[0].Options.Count));
            Assert.That(snapshots[0].OpenOptionCount, Is.EqualTo(rig.Opened[0].Options.Count));

            // 选项快照顺序与选项自身顺序一致（ActionSpecId Ordinal）。
            for (int i = 0; i < snapshots[0].Options.Count; i++)
            {
                Assert.That(snapshots[0].Options[i].ActionSpecId,
                    Is.EqualTo(rig.Opened[0].Options[i].ReactionActionSpecId.Value));
                Assert.That(snapshots[0].Options[i].IsPublished, Is.True);
            }

            rig.System.ExpireDueOptions(attack.ImpactTick + 10L);
            var closed = rig.System.BuildSnapshots();
            Assert.That(closed[0].State, Is.EqualTo((int)ReactionOpportunityState.Expired));
            Assert.That(closed[0].CloseReason, Is.EqualTo(ReactionCloseReasons.AllOptionsExpired));
        }

        /// <summary>
        /// <c>ActionPlanAutoDeferredEventContainsCanonicalRippleAndRevision</c>（事件族）。
        /// </summary>
        [Test]
        public void ActionPlanAutoDeferredEventContainsCanonicalRippleAndRevision()
        {
            Rig rig = new Rig();
            ActionPlan blocked = rig.Telegraph(Task05Farm.AttackId, 100L);
            Task05Scheduler.Run(blocked, 100L);

            ActionPlan follower = rig.S.Attack(180L);
            rig.S.Authority.RegisterPlan(follower);

            // 让 blocked 可延期：Editable + 普通。用一个独立的可延期计划承载事件断言。
            ActionPlan deferrable = rig.S.Attack(300L);
            rig.S.Authority.RegisterPlan(deferrable);
            long oldStart = deferrable.StartTick;
            long revisionBefore = rig.S.Revision;

            ScheduleEditTransactionResult deferred = rig.S.Editor.ApplySystemAutoDeferral(
                deferrable, retryAtTick: 400L, currentTick: 100L, claimedPlans: null);
            Assert.That(deferred.Committed, Is.True, deferred.RejectionCode);

            rig.System.EmitPlanAutoDeferred(
                deferrable, ActionStartBlockerReasons.TimedControlState, oldStart, 400L,
                ToLongs(deferred.RipplePlanIds), 100L);

            ActionPlanAutoDeferredEvent emitted = null;
            IReadOnlyList<LogicEvent> events = rig.System.EmittedEvents;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] is ActionPlanAutoDeferredEvent candidate) emitted = candidate;
            }

            Assert.That(emitted, Is.Not.Null, "必须发射 ActionPlanAutoDeferredEvent");
            Assert.That(emitted.ActionPlanId, Is.EqualTo(deferrable.ActionPlanId.Value));
            Assert.That(emitted.BlockerReasonCode, Is.EqualTo(ActionStartBlockerReasons.TimedControlState));
            Assert.That(emitted.OldStartTick, Is.EqualTo(oldStart));
            Assert.That(emitted.NewStartTick, Is.EqualTo(deferrable.StartTick));
            Assert.That(emitted.RetryAtTick, Is.EqualTo(400L));
            Assert.That(emitted.AutomaticDeferralCount, Is.EqualTo(1));
            Assert.That(emitted.ScheduleRevision, Is.EqualTo(rig.S.Revision),
                "修订号必须是提交后的值");
            Assert.That(emitted.ScheduleRevision, Is.EqualTo(revisionBefore + 1L));
            Assert.That(emitted.RipplePlanIds, Is.Not.Null, "ripple 列表必须存在（可以为空）");

            // 稳定 ripple 列表：与事务返回的顺序（ActionPlanId 升序）逐项一致。
            for (int i = 0; i < deferred.RipplePlanIds.Count; i++)
                Assert.That(emitted.RipplePlanIds[i], Is.EqualTo(deferred.RipplePlanIds[i].Value));
        }

        private static long[] ToLongs(IReadOnlyList<ActionPlanId> ids)
        {
            var result = new long[ids.Count];
            for (int i = 0; i < ids.Count; i++) result[i] = ids[i].Value;
            return result;
        }
    }
}
