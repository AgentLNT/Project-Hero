using System;
using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Authoring.Tests.Task05
{
    /// <summary>
    /// <strong>Dodge 对后续移动的终态接缝</strong>（任务包「Dodge 对后续移动的终态接缝」与
    /// 五项必需测试）。
    ///
    /// 与 <c>Task05ScheduleEditorTests.PositionDependencyClosureIsTransitiveAndCrossesNonMovementPlans</c>
    /// 的分工：那一个只证明<strong>只读查询</strong>的传递性；本文件证明
    /// <strong>接缝整体</strong>——接受 Dodge 不改动任何后续移动、触发前不伪造终态、
    /// 换位事务成功后的终态经统一协调器幂等提交、且不推进 <c>ScheduleRevision</c>、
    /// 不左吸、不 ripple 非依赖计划。
    ///
    /// 冻结时序（夹具动作，见 <c>Task05Fixture</c>）：
    /// <c>action.t05.attack.long</c> 前摇 200 / 后摇 30 ⇒ 起点 100 时
    /// <c>ImpactTick = 300</c>；<c>action.t05.dodge</c> 反应前摇 30 / 后摇 10 ⇒
    /// Dodge 固定区间 <c>[270, 310)</c>（<c>TriggerTick = 300</c>）。
    /// <c>action.t05.move</c>：基准步长 5、后摇 10 ⇒ 区间长度 = <c>权重 × 5 + 10</c>。
    /// </summary>
    [TestFixture]
    public sealed class Task05DodgeSeamTests
    {
        private static readonly ActionSpecId DodgeSpec = new ActionSpecId(Task05Farm.DodgeId);

        private const long TelegraphTick = 100L;
        private const long TriggerTick = 300L;
        private const long DodgeStart = TriggerTick - Task05Farm.DodgeReactionWindup;   // 270
        private const long DodgeEnd = TriggerTick + Task05Farm.DodgeRecovery;           // 310

        // ————————————————————————————————————————————————————————————
        // 夹具
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// 接缝夹具：真实排程权威 + 真实机会系统 + 真实统一终态协调器 + 可替换的换位事务端口。
        /// </summary>
        private sealed class Rig
        {
            public readonly Task05Scheduler S = new Task05Scheduler();
            public readonly ActionPlanTerminalCoordinator Coordinator;
            public readonly ReactionOpportunitySystem System;
            public readonly DodgeMovementInvalidationSeam Seam;
            public readonly StubRelocation Transaction;
            public readonly List<ReactionOpportunityRuntime> Opened = new List<ReactionOpportunityRuntime>();

            public Rig(IDodgeRelocationTransaction transaction = null)
            {
                Coordinator = new ActionPlanTerminalCoordinator(S.Authority);
                // 任务 05 收尾 R1（缺陷 D2）：权威只投影唯一分配器（LogicIdGenerator），
                // 与生产 BattleSimulation 的装配一致。
                S.Authority.IdGenerator = S.Ids;
                System = new ReactionOpportunitySystem(
                    S.Authority, S.Factory, S.Bundle.Definition, S.Bundle.Factions, S.Ids, null, Coordinator);
                Transaction = transaction as StubRelocation;
                Seam = new DodgeMovementInvalidationSeam(
                    S.Authority, S.Evaluator, Coordinator, System, transaction);
            }

            /// <summary>把一条长前摇攻击推进到"已锁定并启动"，并在 <c>TelegraphTick</c> 公开机会。</summary>
            public ActionPlan TelegraphAttack()
            {
                ActionPlanCreationResult result =
                    S.Bundle.CreatePlan(Task05Farm.LongTelegraphAttackId, TelegraphTick);
                Assert.That(result.Succeeded, Is.True, result.RejectionCode);
                ActionPlan attack = result.Plan;
                S.Authority.RegisterPlan(attack);
                Task05Scheduler.Lock(attack, TelegraphTick);

                Opened.Clear();
                string error = System.TryOpenForTelegraph(attack, TelegraphTick, Opened);
                Assert.That(error, Is.Null, error);
                Assert.That(Opened.Count, Is.EqualTo(1));
                return attack;
            }

            /// <summary>走真实接受路径创建直接 Locked 的 Dodge 反应计划（防御者 = enemy）。</summary>
            public ActionPlan AcceptDodge(long tick, GridPoint? destination)
            {
                TelegraphAttack();
                string error = System.TryAccept(
                    Opened[0].Id, Task05Farm.Enemy, DodgeSpec, tick, destination, out ActionPlan dodge);
                Assert.That(error, Is.Null, error);
                Assert.That(dodge, Is.Not.Null);
                Assert.That(dodge.StartTick, Is.EqualTo(DodgeStart));
                Assert.That(dodge.EndTick, Is.EqualTo(DodgeEnd));
                return dodge;
            }

            /// <summary>防御者（enemy）Lane 上直接注册一个 Editable 普通计划（构造重叠闭包场景）。</summary>
            public ActionPlan Register(string actionSpecId, long startTick,
                int pathEdgeCount = 1, int pathWeightUnits = 2, UnitId? owner = null)
            {
                ActionPlanCreationResult result = S.Bundle.CreatePlan(
                    actionSpecId, startTick, owner ?? Task05Farm.Enemy,
                    Task05Farm.Hero, pathEdgeCount, pathWeightUnits);
                Assert.That(result.Succeeded, Is.True, result.RejectionCode);
                S.Authority.RegisterPlan(result.Plan);
                return result.Plan;
            }

            public long[] QueryIds(ActionPlan dodge)
            {
                IReadOnlyList<ActionPlan> invalidated = Seam.QueryInvalidatedMoves(dodge);
                var ids = new long[invalidated.Count];
                for (int i = 0; i < invalidated.Count; i++) ids[i] = invalidated[i].ActionPlanId.Value;
                return ids;
            }
        }

        /// <summary>换位事务端口桩：记录调用与候选，并按用例需要成功/失败。</summary>
        private sealed class StubRelocation : IDodgeRelocationTransaction
        {
            public int Calls;
            public string Reject;
            public ActionPlan LastDodge;
            public long LastTick = -1L;
            public readonly List<long> LastCandidates = new List<long>();

            public string TryRelocate(ActionPlan dodgePlan, IReadOnlyList<ActionPlan> invalidatedCandidates, long tick)
            {
                Calls++;
                LastDodge = dodgePlan;
                LastTick = tick;
                LastCandidates.Clear();
                if (invalidatedCandidates != null)
                {
                    for (int i = 0; i < invalidatedCandidates.Count; i++)
                        LastCandidates.Add(invalidatedCandidates[i].ActionPlanId.Value);
                }
                return Reject;
            }
        }

        private static long[] IdsOf(IReadOnlyList<ActionPlan> plans)
        {
            var ids = new long[plans.Count];
            for (int i = 0; i < plans.Count; i++) ids[i] = plans[i].ActionPlanId.Value;
            return ids;
        }

        // ————————————————————————————————————————————————————————————
        // 1. 接受 Dodge 不终止任何后续移动
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>DodgeAcceptanceDoesNotTerminateFutureMoves</c>。
        ///
        /// 接受 Dodge <strong>只</strong>创建一个直接 Locked 的反应计划：
        /// 它不提前取消、不重排、不改写任何后续移动的路径或 Tick，也不产生任何终态；
        /// 没有换位事务端口时 <c>TryCommit</c> 明确拒绝且<strong>零写入</strong>（本任务不伪造触发）。
        /// </summary>
        [Test]
        public void DodgeAcceptanceDoesNotTerminateFutureMoves()
        {
            Rig rig = new Rig();
            ActionPlan dodge = rig.AcceptDodge(110L, null);

            // 接受之后再放两条路径依赖的移动（接受期 Lane 必须干净，因此只能在其后出现）。
            ActionPlan inside = rig.Register(Task05Farm.MoveId, DockInside(), pathWeightUnits: 4);
            ActionPlan after = rig.Register(Task05Farm.MoveId, 320L, pathWeightUnits: 2);

            long revisionAfterAccept = rig.S.Revision;
            Assert.That(revisionAfterAccept, Is.EqualTo(0L), "接受反应不推进修订号");
            Assert.That(rig.Opened[0].State, Is.EqualTo(ReactionOpportunityState.Accepted));

            // 依赖关系确实存在（否则本用例证明不了"接受不改动它们"）。
            // inside 起点落在 Dodge 固定区间内 ⇒ 直接依赖；after 起点 320 不在任何受影响区间内
            // ⇒ 它不是依赖计划（Dodge 区间右端是 310）。
            Assert.That(rig.QueryIds(dodge), Is.EqualTo(new[] { inside.ActionPlanId.Value, after.ActionPlanId.Value }));
            Assert.That(after.StartTick, Is.GreaterThanOrEqualTo(DodgeEnd));

            // 接受本身没有终止任何计划，也没有把它们移出 Lane。
            Assert.That(inside.IsTerminal, Is.False);
            Assert.That(after.IsTerminal, Is.False);
            Assert.That(inside.IsEditable, Is.True);
            Assert.That(after.IsEditable, Is.True);
            Assert.That(rig.Coordinator.FrozenTickCandidates.Count, Is.EqualTo(0), "接受不得产生归档候选");
            Assert.That(rig.S.Authority.Registry.TerminalRecordCount, Is.EqualTo(0));

            long insideStart = inside.StartTick;
            long insideEnd = inside.EndTick;
            long afterStart = after.StartTick;

            // 没有换位事务 ⇒ 明确拒绝且零写入；本任务绝不伪造"已触发"。
            DodgeRelocationOutcome outcome = rig.Seam.TryCommit(dodge, rig.Opened[0].Id, TriggerTick);
            Assert.That(outcome.Committed, Is.False);
            Assert.That(outcome.RejectionCode, Is.EqualTo(DodgeSeamCodes.DODGE_SEAM_TRANSACTION_UNAVAILABLE));
            Assert.That(inside.IsTerminal, Is.False, "缺端口时不得终止任何移动");
            Assert.That(after.IsTerminal, Is.False);
            Assert.That(inside.StartTick, Is.EqualTo(insideStart));
            Assert.That(inside.EndTick, Is.EqualTo(insideEnd));
            Assert.That(after.StartTick, Is.EqualTo(afterStart));
            Assert.That(rig.Opened[0].State, Is.EqualTo(ReactionOpportunityState.Accepted),
                "没有换位事务 ⇒ 机会不得进入 Triggered");
            Assert.That(rig.S.Revision, Is.EqualTo(revisionAfterAccept));
        }

        private static long DockInside() => DodgeStart + 5L;   // 275：落在 Dodge 固定区间内

        // ————————————————————————————————————————————————————————————
        // 2. 只读闭包的传递性（跨过非移动计划）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>DodgeOriginInvalidationFindsTransitiveMovesAcrossNonMovementPlans</c>。
        ///
        /// 查询返回<strong>实际 From 变化会失效</strong>的后续 Editable 移动及其
        /// <strong>传递闭包</strong>：它会跨过 Attack 等非移动计划（本例中夹在中间的 Attack
        /// 本身不在结果里，但穿过它才可达的移动必须在结果里），
        /// <strong>不以"紧邻下一项"代替依赖关系</strong>，也不包含独立计划与来源自身。
        /// </summary>
        [Test]
        public void DodgeOriginInvalidationFindsTransitiveMovesAcrossNonMovementPlans()
        {
            Rig rig = new Rig();
            ActionPlan dodge = rig.AcceptDodge(110L, null);

            // 区间事实：
            //   dodge     [270, 310)
            //   near      [275, 305)  起点落在 dodge 区间内 ⇒ 直接依赖
            //   attack    [290, 350)  起点落在 dodge 区间内 ⇒ 依赖（非移动族，结果里应被过滤）
            //   through   [320, 340)  起点不在 dodge 区间内，但落在 attack 区间内 ⇒ 传递依赖
            //   independent [900, 920) 与任何受影响区间都不相交
            ActionPlan near = rig.Register(Task05Farm.MoveId, 275L, pathWeightUnits: 4);
            ActionPlan attack = rig.Register(Task05Farm.AttackId, 290L);
            ActionPlan through = rig.Register(Task05Farm.MoveId, 320L, pathWeightUnits: 2);
            ActionPlan independent = rig.Register(Task05Farm.MoveId, 900L, pathWeightUnits: 2);

            Assert.That(near.EndTick, Is.EqualTo(305L));
            Assert.That(attack.EndTick, Is.EqualTo(350L));
            Assert.That(attack.StartTick, Is.LessThan(DodgeEnd),
                "attack 直接依赖 Dodge（起点落在固定区间内）");
            Assert.That(through.StartTick, Is.GreaterThanOrEqualTo(DodgeEnd),
                "through 起点不在 Dodge 固定区间内 ⇒ 它只能经 attack 传递依赖");
            Assert.That(through.StartTick, Is.LessThan(attack.EndTick),
                "through 起点落在 attack 的区间内 ⇒ 传递依赖成立");

            long[] ids = rig.QueryIds(dodge);

            Assert.That(ids, Is.EqualTo(new[] { near.ActionPlanId.Value, through.ActionPlanId.Value, independent.ActionPlanId.Value }),
                "结果 = 直接依赖的移动 + 跨过 Attack 才可达的移动，且按 ActionPlanId 升序");
            Assert.That(Array.IndexOf(ids, attack.ActionPlanId.Value), Is.LessThan(0),
                "非移动计划本身不属于'会失效的移动'");
            Assert.That(Array.IndexOf(ids, independent.ActionPlanId.Value), Is.GreaterThanOrEqualTo(0),
                "隔着时间间隙的后续Move仍依赖旧起点");
            Assert.That(Array.IndexOf(ids, dodge.ActionPlanId.Value), Is.LessThan(0),
                "来源自身不属于它的依赖闭包");

            // 只读：不写任何状态、不改修订号。
            Assert.That(rig.S.Revision, Is.EqualTo(0L));
            Assert.That(near.StartTick, Is.EqualTo(275L));
            Assert.That(attack.StartTick, Is.EqualTo(290L));
            Assert.That(through.StartTick, Is.EqualTo(320L));
            Assert.That(independent.StartTick, Is.EqualTo(900L));
            Assert.That(rig.Coordinator.FrozenTickCandidates.Count, Is.EqualTo(0));
        }

        // ————————————————————————————————————————————————————————————
        // 3. 查询读取"当前"排程（含接受之后的新增与编辑）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>DodgeOriginInvalidationUsesCurrentScheduleIncludingLaterEdits</c>。
        ///
        /// 触发时读取<strong>当前</strong>排程：接受 Dodge 之后新增的移动会出现，
        /// 之后被权威排程事务改出固定区间的移动会消失，传递依赖仍然成立。
        /// 因此预检与 TriggerTick 共用同一实现，且不需要"提前快照依赖集合"。
        /// </summary>
        [Test]
        public void DodgeOriginInvalidationUsesCurrentScheduleIncludingLaterEdits()
        {
            Rig rig = new Rig();
            ActionPlan dodge = rig.AcceptDodge(110L, null);

            Assert.That(rig.QueryIds(dodge), Is.Empty, "接受时 Lane 里还没有任何后续计划");

            ActionPlan first = rig.Register(Task05Farm.MoveId, 275L, pathWeightUnits: 4);
            Assert.That(rig.QueryIds(dodge), Is.EqualTo(new[] { first.ActionPlanId.Value }),
                "接受之后新增的移动必须立即进入闭包");

            ActionPlan attack = rig.Register(Task05Farm.AttackId, 290L);
            ActionPlan through = rig.Register(Task05Farm.MoveId, 320L, pathWeightUnits: 2);
            Assert.That(rig.QueryIds(dodge),
                Is.EqualTo(new[] { first.ActionPlanId.Value, through.ActionPlanId.Value }),
                "新增的非移动计划参与传递闭包");

            // 后续编辑：把 first 移出固定区间（投影级编辑，保持区间长度不变）。
            // 说明：权威排程事务会把"与 Locked 反应区间重叠"的计划按规范向右避让，
            // 因此这里刻意在<strong>投影</strong>层面改动起点，只观察查询是否读取当前排程。
            first.RebindAbsoluteTicks(200L);
            Assert.That(first.StartTick, Is.EqualTo(200L));
            Assert.That(first.EndTick, Is.EqualTo(230L));

            Assert.That(rig.QueryIds(dodge), Is.EqualTo(new[] { through.ActionPlanId.Value }),
                "编辑之后闭包按当前排程重算：first移到Dodge之前才离开闭包，through 仍依赖 attack");
            Assert.That(attack.IsTerminal, Is.False);

            long revision = rig.S.Revision;
            Assert.That(revision, Is.EqualTo(0L), "投影级编辑与只读查询都不推进修订号");
            Assert.That(rig.QueryIds(dodge), Is.EqualTo(new[] { through.ActionPlanId.Value }));
            Assert.That(rig.S.Revision, Is.EqualTo(revision), "只读查询不得推进修订号");
        }

        // ————————————————————————————————————————————————————————————
        // 4. 触发后的终态提交：幂等、不推进修订号、不伪造触发
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>DodgeMovementTerminalIsIdempotentAndDoesNotIncrementScheduleRevision</c>。
        ///
        /// 只有换位事务<strong>成功</strong>之后才提交终态；提交经统一终态协调器完成，
        /// 幂等（第二次调用不再调用事务、不重复终止、不覆盖状态/原因/TerminalTick、不重复发事件），
        /// 且<strong>不</strong>推进 <c>ScheduleRevision</c>。
        /// </summary>
        [Test]
        public void DodgeMovementTerminalIsIdempotentAndDoesNotIncrementScheduleRevision()
        {
            var transaction = new StubRelocation();
            Rig rig = new Rig(transaction);
            var destination = new GridPoint(4, 4);
            ActionPlan dodge = rig.AcceptDodge(110L, destination);

            ActionPlan dependent = rig.Register(Task05Farm.MoveId, 275L, pathWeightUnits: 4);
            ActionPlan transitive = rig.Register(Task05Farm.AttackId, 290L);
            ActionPlan through = rig.Register(Task05Farm.MoveId, 320L, pathWeightUnits: 2);

            long revisionBefore = rig.S.Revision;
            DodgeRelocationOutcome outcome = rig.Seam.TryCommit(dodge, rig.Opened[0].Id, TriggerTick);

            Assert.That(outcome.Committed, Is.True, outcome.RejectionCode);
            Assert.That(transaction.Calls, Is.EqualTo(1));
            Assert.That(transaction.LastDodge, Is.SameAs(dodge));
            Assert.That(transaction.LastTick, Is.EqualTo(TriggerTick));
            Assert.That(transaction.LastDodge.Destination.HasValue, Is.True,
                "目的格必须由命令在接缝之前权威记录");
            Assert.That(transaction.LastDodge.Destination.Value.X, Is.EqualTo(destination.X));
            Assert.That(transaction.LastCandidates,
                Is.EqualTo(new[] { dependent.ActionPlanId.Value, through.ActionPlanId.Value }));
            Assert.That(transitive.IsTerminal, Is.False, "非移动计划不得被本接缝终止");

            Assert.That(dependent.TerminationReason,
                Is.EqualTo(ActionTerminationReason.MovementOriginInvalidatedByDodge));
            Assert.That(through.TerminationReason,
                Is.EqualTo(ActionTerminationReason.MovementOriginInvalidatedByDodge));
            Assert.That(dependent.TerminalTick, Is.EqualTo(TriggerTick));
            Assert.That(through.TerminalTick, Is.EqualTo(TriggerTick));
            Assert.That(outcome.NewlyTerminatedPlans.Count, Is.EqualTo(2));
            Assert.That(outcome.NewlyTerminatedPlans[0].Value, Is.EqualTo(dependent.ActionPlanId.Value));
            Assert.That(outcome.NewlyTerminatedPlans[1].Value, Is.EqualTo(through.ActionPlanId.Value),
                "按 ActionPlanId 升序终止");
            Assert.That(rig.Opened[0].State, Is.EqualTo(ReactionOpportunityState.Triggered),
                "换位事务成功之后才允许推进到 Triggered");
            Assert.That(rig.S.Revision, Is.EqualTo(revisionBefore),
                "终态清理不是排程事务：不得推进修订号");
            Assert.That(rig.Coordinator.FrozenTickCandidates.Count, Is.EqualTo(2));
            Assert.That(rig.S.Authority.Registry.TerminalRecordCount, Is.EqualTo(2));

            long dependentTerminalTick = dependent.TerminalTick;
            // —— 幂等：第二次调用返回同一结果，且绝不再调用换位事务 ——
            DodgeRelocationOutcome again = rig.Seam.TryCommit(dodge, rig.Opened[0].Id, TriggerTick);

            Assert.That(again.Committed, Is.True);
            Assert.That(transaction.Calls, Is.EqualTo(1), "第一次换位事务胜出：不得再次调用端口");
            Assert.That(again.NewlyTerminatedPlans, Is.EqualTo(outcome.NewlyTerminatedPlans));
            Assert.That(dependent.TerminalTick, Is.EqualTo(dependentTerminalTick));
            Assert.That(dependent.TerminationReason,
                Is.EqualTo(ActionTerminationReason.MovementOriginInvalidatedByDodge));
            Assert.That(rig.Coordinator.FrozenTickCandidates.Count, Is.EqualTo(2), "归档候选不得重复登记");
            Assert.That(rig.S.Revision, Is.EqualTo(revisionBefore));

            // —— 第一次请求胜出：后续冲突原因不得覆盖 ——
            ActionPlanTerminalOutcome conflicting = rig.Coordinator.EnterTerminal(
                dependent, ActionTerminationReason.OwnerDied, 999L);
            Assert.That(conflicting.EnteredTerminal, Is.False);
            Assert.That(dependent.TerminationReason,
                Is.EqualTo(ActionTerminationReason.MovementOriginInvalidatedByDodge));
            Assert.That(dependent.TerminalTick, Is.EqualTo(TriggerTick));
            Assert.That(rig.S.Revision, Is.EqualTo(revisionBefore));
        }

        // ————————————————————————————————————————————————————————————
        // 5. 独立计划与非依赖对象不受影响
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>DodgeOriginInvalidationPreservesIndependentPlansAndTheirTicks</c>。
        ///
        /// 只终止"实际 From 变化会失效"的 <strong>Editable 移动</strong>：
        /// 独立计划保持原 Tick（不左吸、不 ripple）、已 Locked 的移动不被回卷、
        /// 其他 Lane 完全不受影响，且不撤销任何已提交事实。
        /// </summary>
        [Test]
        public void DodgeOriginInvalidationPreservesIndependentPlansAndTheirTicks()
        {
            var transaction = new StubRelocation();
            Rig rig = new Rig(transaction);
            ActionPlan dodge = rig.AcceptDodge(110L, null);

            ActionPlan dependent = rig.Register(Task05Farm.MoveId, 275L, pathWeightUnits: 4);
            ActionPlan independent = rig.Register(Task05Farm.GuardId, 900L);
            ActionPlan lockedDependent = rig.Register(Task05Farm.MoveId, 280L, pathWeightUnits: 2);
            Task05Scheduler.Lock(lockedDependent, 110L);
            // 另一个 Lane（hero）上的计划必须完全不受影响。
            ActionPlan otherLane = rig.Register(Task05Farm.MoveId, 275L, pathWeightUnits: 4, owner: Task05Farm.Hero);

            long independentStart = independent.StartTick;
            long independentEnd = independent.EndTick;
            long lockedStart = lockedDependent.StartTick;
            long otherStart = otherLane.StartTick;

            Assert.That(rig.QueryIds(dodge), Is.EqualTo(new[] { dependent.ActionPlanId.Value }),
                "Locked 移动不属于'会被失效的 Editable 移动'，因此不在候选中");

            DodgeRelocationOutcome outcome = rig.Seam.TryCommit(dodge, rig.Opened[0].Id, TriggerTick);

            Assert.That(outcome.Committed, Is.True, outcome.RejectionCode);
            Assert.That(outcome.InvalidatedCandidates.Count, Is.EqualTo(1));
            Assert.That(outcome.InvalidatedCandidates[0].Value, Is.EqualTo(dependent.ActionPlanId.Value));
            Assert.That(dependent.IsTerminated, Is.True);
            Assert.That(dependent.TerminationReason,
                Is.EqualTo(ActionTerminationReason.MovementOriginInvalidatedByDodge));

            Assert.That(independent.IsTerminal, Is.False, "独立计划不得被终止");
            Assert.That(independent.StartTick, Is.EqualTo(independentStart), "独立计划不左吸、不 ripple");
            Assert.That(independent.EndTick, Is.EqualTo(independentEnd));
            Assert.That(lockedDependent.IsTerminal, Is.False, "Locked 移动不得被回卷（Locked 后不退款）");
            Assert.That(lockedDependent.IsLocked, Is.True);
            Assert.That(lockedDependent.StartTick, Is.EqualTo(lockedStart));
            Assert.That(otherLane.IsTerminal, Is.False, "其他 Lane 不受影响");
            Assert.That(otherLane.StartTick, Is.EqualTo(otherStart));
            // hero Lane = 来源攻击（它在 Hero Lane，见 TelegraphAttack）+ otherLane，两者都不得被改动。
            Assert.That(rig.S.Authority.FindLane(Task05Farm.Hero).Count, Is.EqualTo(2));
            Assert.That(rig.S.Authority.Registry.TerminalRecordCount, Is.EqualTo(1));
        }

        // ————————————————————————————————————————————————————————————
        // 附加：换位事务失败 ⇒ 零写入（不因本 Dodge 修改移动链）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>DodgeRelocationFailureLeavesMovementChainUntouched</c>（附加护栏）。
        ///
        /// 换位事务失败/取消时，<strong>没有</strong>任何真实计划进入终态，
        /// 机会也不得离开 Accepted：不允许"计划先终态、换位随后失败"。
        /// </summary>
        [Test]
        public void DodgeRelocationFailureLeavesMovementChainUntouched()
        {
            var transaction = new StubRelocation { Reject = "TEST_RELOCATION_REJECTED" };
            Rig rig = new Rig(transaction);
            ActionPlan dodge = rig.AcceptDodge(110L, null);
            ActionPlan dependent = rig.Register(Task05Farm.MoveId, 275L, pathWeightUnits: 4);
            long start = dependent.StartTick;

            DodgeRelocationOutcome outcome = rig.Seam.TryCommit(dodge, rig.Opened[0].Id, TriggerTick);

            Assert.That(outcome.Committed, Is.False);
            Assert.That(outcome.RejectionCode, Is.EqualTo("TEST_RELOCATION_REJECTED"));
            Assert.That(transaction.Calls, Is.EqualTo(1));
            Assert.That(dependent.IsTerminal, Is.False, "换位失败不得留下真实终态");
            Assert.That(dependent.StartTick, Is.EqualTo(start));
            Assert.That(rig.Opened[0].State, Is.EqualTo(ReactionOpportunityState.Accepted));
            Assert.That(rig.Coordinator.FrozenTickCandidates.Count, Is.EqualTo(0));
            Assert.That(rig.S.Revision, Is.EqualTo(0L));

            // 失败之后仍然可以重试（第一次"成功"的事务才算胜出）。
            transaction.Reject = null;
            DodgeRelocationOutcome retry = rig.Seam.TryCommit(dodge, rig.Opened[0].Id, TriggerTick);
            Assert.That(retry.Committed, Is.True, retry.RejectionCode);
            Assert.That(transaction.Calls, Is.EqualTo(2));
            Assert.That(dependent.TerminationReason,
                Is.EqualTo(ActionTerminationReason.MovementOriginInvalidatedByDodge));
        }
    }
}
