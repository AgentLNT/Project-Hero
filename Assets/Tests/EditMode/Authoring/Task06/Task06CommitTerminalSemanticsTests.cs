using System;
using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Authoring.Tests.Task05;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Authoring.Tests.Task06
{
    /// <summary>
    /// 任务 06 <strong>小修轮</strong>的处置证据：Dodge 提交期"计划终态 vs 计划存活"的预留语义（R1）
    /// 与预算释放接缝的顺序契约（B-1）、终态清理不可跳过（B-2）。
    ///
    /// 本文件存在的原因（为什么不能并进 <c>ProjectHero.Logic.Tests</c>）：
    /// 这三条都必须<strong>经真实 <c>ActionPlan</c></strong>（<c>IsReaction</c>/<c>IsTerminal</c>/
    /// <c>ReactionOpportunityId</c>）与<strong>真实统一终态协调器</strong>驱动，而
    /// <c>ProjectHero.Logic.Tests</c> 没有 <c>InternalsVisibleTo</c> 也没有反应机会夹具。
    ///
    /// 全部断言建立在公开可观察的逻辑事实（预留集合、提交日志、占位索引、计划终态、
    /// 机会系统队列）之上；没有桩冒充被测行为，没有空断言。
    /// </summary>
    [TestFixture]
    public sealed class Task06CommitTerminalSemanticsTests
    {
        private const long TelegraphTick = 100L;
        private const long TriggerTick = 300L;
        private const long AcceptTick = 110L;
        private const long PlayerSequence = 1L;

        private static readonly ActionSpecId DodgeSpec = new ActionSpecId(Task05Farm.DodgeId);

        /// <summary>敌人（Dodge 的防御者）在夹具里的权威锚点。</summary>
        private static readonly GridPoint DefenderAnchor = new GridPoint(0, 2);

        /// <summary>合法目的格：与锚点同一个规范方向的 1 步。</summary>
        private static readonly GridPoint DodgeDestination = new GridPoint(0, 0);

        // ————————————————————————————————————————————————————————————
        // 夹具：真实排程权威 + 真实网格 + 真实移动权威 + 真实机会系统 +
        //       真实统一终态协调器 + 真实 Dodge 换位事务 + 真实换位接缝
        // ————————————————————————————————————————————————————————————

        private sealed class Rig
        {
            public readonly Task05Scheduler S = new Task05Scheduler();
            public readonly LogicGrid Grid;
            public readonly LogicGridMovementAuthority Movement;
            public readonly ActionPlanTerminalCoordinator Coordinator;
            public readonly ReactionOpportunitySystem System;
            public readonly DodgeRelocationAuthority Dodge;
            public readonly DodgeMovementInvalidationSeam Seam;
            public readonly List<ReactionOpportunityRuntime> Opened = new List<ReactionOpportunityRuntime>();

            public Rig(bool wireDodgeReleaseParticipant = true)
            {
                Grid = new LogicGrid(new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40)));
                ActionScheduleAuthority authority = S.Authority;
                // 缺陷 D2 的同一口径：权威只投影唯一分配器。
                authority.IdGenerator = S.Ids;
                Movement = new LogicGridMovementAuthority(
                    Grid, authority, S.Bundle.Definition.Rules.PathCostRules,
                    S.Bundle.Definition.Rules.PathSearchRules);

                // 与生产 BattleSimulation 同构：Dodge 预留释放挂在既有的
                // MovementAndReservation = 500 槽位，不新增第二个清理参与者。
                Coordinator = new ActionPlanTerminalCoordinator(
                    authority, new IActionPlanCleanupParticipant[] { Movement });
                System = new ReactionOpportunitySystem(
                    authority, S.Factory, S.Bundle.Definition, S.Bundle.Factions, S.Ids, null, Coordinator);
                Dodge = new DodgeRelocationAuthority(Grid, Movement, authority, Coordinator,
                    specId => DodgeDestinationRules.FromPayload(
                        S.Bundle.Definition.FindAction(specId)?.Payload as DodgePayloadSpec));
                // 生产装配含"终态清理 ⇒ Dodge 预留释放"这条路径；
                // 只做"来源威胁取消"的隔离用例可以关掉它（R4/B-2 的窗口复现）。
                if (wireDodgeReleaseParticipant) Movement.DodgeDestinationReservations = Dodge;
                System.DestinationReservationPort = Dodge;
                Seam = new DodgeMovementInvalidationSeam(
                    authority, S.Evaluator, Coordinator, System, Dodge);

                Assert.That(Grid.RegisterUnitWithPointFootprint(
                        Task05Farm.Hero, new GridPoint(0, 20), GridDirection.East), Is.Null, "Hero 注册");
                Assert.That(Grid.RegisterUnitWithPointFootprint(
                        Task05Farm.Enemy, DefenderAnchor, GridDirection.East), Is.Null, "Enemy 注册");
                Assert.That(Grid.RegisterUnitWithPointFootprint(
                        Task05Farm.Ally, new GridPoint(0, 30), GridDirection.East), Is.Null, "Ally 注册");
            }

            /// <summary>
            /// 注册一条<strong>真实</strong> Editable 移动计划并建立真实段链 + Reservation。
            /// 返回值同时既进权威注册表、又持有可被换位失效的空间事实；
            /// <c>TryRelocate</c> 的 <c>invalidatedCandidates</c> 就是它的对象（不是桩）。
            /// </summary>
            public ActionPlan EstablishHeroMovement(long planId, long startTick)
            {
                ActionPlanCreationResult created = S.Bundle.CreatePlan(
                    Task05Farm.MoveId, startTick, Task05Farm.Hero);
                Assert.That(created.Succeeded, Is.True, created.RejectionCode);
                ActionPlan plan = created.Plan;
                S.Authority.RegisterPlan(plan);

                var path = new List<GridPoint>
                {
                    new GridPoint(0, 20), new GridPoint(0, 22), new GridPoint(0, 24)
                };
                MovementReplacementResult established = Movement.EstablishMovement(plan, path, startTick);
                Assert.That(established.Succeeded, Is.True, established.FailureCode);
                Assert.That(Grid.ReservationsOfPlanOrdered(plan.ActionPlanId).Count, Is.GreaterThan(0),
                    "对照证据：真实移动计划必须持有 Reservation（否则“释放”断言是空的）");
                return plan;
            }

            /// <summary>真实机会公开：锁定攻击 → 公开机会（阶段 7 的真实入口）。</summary>
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

            /// <summary>走真实接受事务接受 Dodge：目的格预留与 <c>BindPlan</c> 都在事务内完成。</summary>
            public ActionPlan AcceptDodge()
            {
                TelegraphAttack();
                string error = System.TryAcceptWithCommandSequence(
                    Opened[0].Id, Task05Farm.Enemy, DodgeSpec, PlayerSequence, AcceptTick,
                    DodgeDestination, out ActionPlan dodge);
                Assert.That(error, Is.Null, error);
                Assert.That(dodge, Is.Not.Null);
                Assert.That(dodge.IsReaction, Is.True);
                Assert.That(dodge.ReactionOpportunityId.HasValue, Is.True);
                Assert.That(dodge.ReactionOpportunityId.Value, Is.EqualTo(Opened[0].Id));
                Assert.That(Dodge.TryGetReservation(Opened[0].Id, out _), Is.True,
                    "接受事务必须为目标格冻结预留");
                return dodge;
            }

            /// <summary>占位某一格：用一个点占位单位（<c>U(99)</c>）挡住目的格。</summary>
            public void OccupyDestinationCell()
            {
                Assert.That(Grid.RegisterUnitWithPointFootprint(
                        new UnitId(99L), DodgeDestination, GridDirection.East), Is.Null,
                    "目的格必须可被另一个单位占用（夹具前提）");
                Assert.That(Grid.TryGetCellOwner(DodgeDestination, out UnitId owner), Is.True);
                Assert.That(owner.Value, Is.EqualTo(99L));
            }

            public long Revision => S.Revision;

            public int Reservations => Dodge.AllReservationsOrdered().Count;

            public int CommitLogCount => Dodge.CommitLog.Count;

            public int ReleasedReservationCodes()
            {
                int count = 0;
                IReadOnlyList<DodgeCommitResult> log = Dodge.CommitLog;
                for (int i = 0; i < log.Count; i++)
                {
                    if (log[i].DefenseFailureReason == ActionTerminationReason.TargetInvalid) count++;
                }
                return count;
            }
        }

        // ————————————————————————————————————————————————————————————
        // R1：计划终态 ⇒ 同批释放自有预留；计划仍存活 ⇒ 保留预留
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <strong>R1（情形一）：防御者自身原因导致触发失败 ⇒ 计划进入终态
        /// ⇒ 该计划的目的格预留同批释放。</strong>
        ///
        /// 这是独立验证 M-1 裁定的结论面：契约原文"<c>Committed = false</c> ⇒ Reservation 保持原样"
        /// 与代码不符，而规格要求"计划一旦终态就不得保留该计划拥有的预留"——
        /// 因此<strong>代码是对的、契约文本是错的</strong>。
        /// 本用例同时钉死三件事：①预留确实消失（不是遗漏）；②它消失的原因被记录为
        /// <c>TargetInvalid</c>（<see cref="DodgeCommitResult.TerminatesPlan"/> 为真）；
        /// ③单位没有移动、移动段/预算/他人占位零写入。
        /// </summary>
        [Test]
        public void DodgeCommitTerminalFailureReleasesItsOwnReservationAndNeverMovesUnit()
        {
            var rig = new Rig();
            ActionPlan dodge = rig.AcceptDodge();

            // 触发时刻目的格已不可用（占用者是无关单位）⇒ 防御者自身原因失败。
            rig.OccupyDestinationCell();
            long revisionBefore = rig.Revision;
            int commitmentsBefore = rig.CommitLogCount;

            IReadOnlyList<DodgeCommitResult> log =
                rig.Dodge.CommitLogSince(commitmentsBefore);
            Assert.That(log.Count, Is.EqualTo(0), "对照证据：提交日志必须真的从空开始");

            string code = rig.Dodge.TryRelocate(dodge, Array.Empty<ActionPlan>(), TriggerTick);

            Assert.That(code, Is.Not.Null, "目的格被占 ⇒ 换位必须稳定拒绝");
            Assert.That(code, Does.StartWith(LogicGridCodes.LOGIC_GRID_OCCUPIED_BY_OTHER));

            IReadOnlyList<DodgeCommitResult> after = rig.Dodge.CommitLogSince(commitmentsBefore);
            Assert.That(after.Count, Is.EqualTo(1), "恰好记录一次提交尝试");
            DodgeCommitResult result = after[0];
            Assert.That(result.Committed, Is.False);
            Assert.That(result.Moved, Is.False);
            Assert.That(result.DefenseFailureReason, Is.EqualTo(ActionTerminationReason.TargetInvalid));
            Assert.That(result.TerminatesPlan, Is.True,
                "该结果必须被判定为『计划随之进入终态』——这正是预留被释放的理由");

            // ① 终态 ⇒ 自有预留不得存活（规格不变量）。
            Assert.That(rig.Dodge.TryGetReservation(dodge.ReactionOpportunityId.Value, out _), Is.False,
                "计划进入终态后，该计划的目的格预留必须已被释放（不变量：终态不留预留）");
            Assert.That(rig.Reservations, Is.EqualTo(0));

            // ② 生产接缝必须真的把计划写入终态（而不是只释放预留）。
            Assert.That(dodge.IsTerminal, Is.True,
                "防御者自身原因的失败必须经统一终态协调器终止该反应计划");
            Assert.That(dodge.TerminationReason, Is.EqualTo(ActionTerminationReason.TargetInvalid));

            // ③ 零位移 + 移动链/预算/他人占位零写入。
            Assert.That(rig.Grid.TryGetAnchor(Task05Farm.Enemy, out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(DefenderAnchor), "失败的 Dodge 绝不移动单位");
            Assert.That(rig.Grid.VerifyConsistency(), Is.Null, "占位索引必须一致");
            Assert.That(rig.Movement.AllSegmentsOrdered().Count, Is.EqualTo(0),
                "未换位 ⇒ 不因本 Dodge 清理任何移动段");
            Assert.That(rig.Revision, Is.EqualTo(revisionBefore), "换位不是排程编辑，不推进修订号");

            // ④ 释放是幂等的：再次释放同一机会是安全无操作。
            Assert.That(rig.Dodge.ReleaseDestination(dodge.ReactionOpportunityId.Value), Is.False,
                "预留已被释放，重复释放必须返回『没有释放任何东西』");
            Assert.That(rig.Reservations, Is.EqualTo(0));
        }

        /// <summary>
        /// <strong>R1（情形二）：纯数据入口不写终态 ⇒ 预留按"计划仍存活"保留。</strong>
        ///
        /// 与情形一的对照：同一条失败路径（目的格在触发时刻不可用），
        /// 但<see cref="DodgeRelocationAuthority.CommitRelocation"/> 拿不到 <c>ActionPlan</c>，
        /// 因此呼叫方没有得到"计划已终态"的证据。为了不制造"没有消费者的泄漏预留"，
        /// 该入口仍然释放自有预留（<c>TerminatesPlan</c> 为真 ⇒ 调用方必须自行终止计划），
        /// 但<strong>不</strong>触碰计划终态、<strong>不</strong>清理任何移动——由调用方决定。
        ///
        /// 本用例把"两种情形"的差别钉在<strong>可观察量</strong>上：情形一有终态写入 + 预留释放，
        /// 情形二只有预留释放（终态由调用方负责）。
        /// </summary>
        [Test]
        public void DodgeCommitTerminalFailureWithoutPlanContextReleasesReservationOnlyAndRetryIsRejected()
        {
            var rig = new Rig();
            ActionPlan dodge = rig.AcceptDodge();
            rig.OccupyDestinationCell();

            DodgeCommitResult result = rig.Dodge.CommitRelocation(
                dodge.ReactionOpportunityId.Value, Array.Empty<ActionPlanId>(), TriggerTick);

            Assert.That(result.Committed, Is.False);
            Assert.That(result.DefenseFailureReason, Is.EqualTo(ActionTerminationReason.TargetInvalid));
            Assert.That(result.TerminatesPlan, Is.True, "纯数据入口同样给出终态判定");
            Assert.That(rig.Dodge.TryGetReservation(dodge.ReactionOpportunityId.Value, out _), Is.False,
                "该计划的预留必须已释放（否则它没有消费者）");
            Assert.That(dodge.IsTerminal, Is.False,
                "纯数据入口不写计划终态：终态由调用方经统一协调器写入（这里刻意不写，作为对照）");
            Assert.That(rig.Grid.TryGetAnchor(Task05Farm.Enemy, out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(DefenderAnchor));

            // 释放之后重试 ⇒ 内部矛盾口径的稳定拒绝，且仍然零写入。
            DodgeCommitResult retry = rig.Dodge.CommitRelocation(
                dodge.ReactionOpportunityId.Value, Array.Empty<ActionPlanId>(), TriggerTick);
            Assert.That(retry.Committed, Is.False);
            Assert.That(retry.Code, Is.EqualTo(DodgeRelocationCodes.DODGE_RELOCATION_NOT_RESERVED));
            Assert.That(retry.TerminatesPlan, Is.False,
                "『预留不存在』既不是终态证据、也不保留预留——它是第三种情形，判定必须为假");
            Assert.That(rig.Grid.TryGetAnchor(Task05Farm.Enemy, out GridPoint still), Is.True);
            Assert.That(still, Is.EqualTo(DefenderAnchor));
            Assert.That(rig.ReleasedReservationCodes(), Is.EqualTo(1),
                "只有第一条（目的格被占）的失败带 TargetInvalid；第二条是『预留不存在』的内部矛盾口径，"
                + "其 DefenseFailureReason 必须为 None（因此不进入该计数）");
        }

        /// <summary>
        /// <strong>R1（情形三，结构证明）："失败但计划仍存活 ⇒ 保留预留"这条分支在正常路径上
        /// 不可达，因此它的保留语义必须由<strong>结构不变量</strong>证明，而不是由测试触发。</strong>
        ///
        /// 论证（逐条对应生产代码）：
        /// <list type="number">
        /// <item><c>CommitReservation</c> 的提交期<strong>唯一</strong>可失败调用是
        /// <c>LogicGrid.CommitAnchor</c>（其余是只读查询与内存操作）；</item>
        /// <item><c>CommitAnchor</c> 失败的唯一原因是
        /// <c>LOGIC_GRID_OWNERSHIP_CONTRADICTION</c>（目标 footprint 的某个格/三角已被
        /// <strong>其他单位</strong>占用）——见 <c>LogicGrid.cs:460-472</c>；</item>
        /// <item>而它在索引之前调用了<strong>同一个</strong>
        /// <c>ValidateDestinationFor</c> 已经做过的检查（同一 <c>ResolveDestinationCells</c>、
        /// 同一所有权谓词、同一批格），且 <c>ValidateDestinationFor</c> 在前一步刚刚返回 <c>null</c>；</item>
        /// <item><c>CommitAnchor</c> 还会先 <c>DeindexRow</c>（移除本单位自己的旧 footprint），
        /// 因此本单位旧格不可能与本单位的"他人占用"判定冲突；</item>
        /// <item>⇒ 两次判定之间没有任何写入发生（同一线程、无回调），
        /// 该分支的触发要求"占位索引在不变量上自相矛盾"，属缺陷而非可达状态。</item>
        /// </list>
        /// 本用例把这个结论钉成<strong>可失败的结构断言</strong>：同一次提交里
        /// 预检成功 ⇒ 提交成功；并断言失败分支的判定谓词
        /// （<see cref="DodgeCommitResult.TerminatesPlan"/> 为假）与"预留保留"是同一件事。
        /// </summary>
        [Test]
        public void DodgeCommitInvariantViolationBranchKeepsReservationAndIsStructurallyUnreachable()
        {
            var rig = new Rig();
            ActionPlan dodge = rig.AcceptDodge();

            // 成功路径：预检通过 ⇒ 提交期不可能失败（结构不变量）。
            string code = rig.Dodge.TryRelocate(dodge, Array.Empty<ActionPlan>(), TriggerTick);
            Assert.That(code, Is.Null, "预检通过 ⇒ CommitAnchor 必须成功（无中间写入窗口）");
            Assert.That(dodge.IsTerminal, Is.False, "成功换位不写终态");
            Assert.That(rig.Dodge.TryGetReservation(dodge.ReactionOpportunityId.Value, out _), Is.False,
                "成功提交同批消费该预留");
            Assert.That(rig.Grid.TryGetAnchor(Task05Farm.Enemy, out GridPoint anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(DodgeDestination), "成功换位必须真的移动单位");

            // 判定谓词的三分：成功 / 终态失败 / 无终态证据——只有第三类保留预留。
            IReadOnlyList<DodgeCommitResult> log = rig.Dodge.CommitLog;
            Assert.That(log.Count, Is.EqualTo(1));
            Assert.That(log[0].TerminatesPlan, Is.False);
            Assert.That(log[0].Committed, Is.True);

            // 覆盖矩阵：每一种结果形态的 TerminatesPlan 取值必须与"预留是否被释放"一一对应。
            foreach (ActionTerminationReason reason in new[]
                     {
                         ActionTerminationReason.None,
                         ActionTerminationReason.TargetInvalid,
                         ActionTerminationReason.MovementOriginInvalidatedByDodge
                     })
            {
                var probe = new DodgeCommitResult(
                    dodge.ActionPlanId, dodge.ReactionOpportunityId.Value, Task05Farm.Enemy,
                    DefenderAnchor, DodgeDestination, false, "PROBE", reason, TriggerTick);
                bool expectTerminal = reason == ActionTerminationReason.TargetInvalid;
                Assert.That(probe.TerminatesPlan, Is.EqualTo(expectTerminal),
                    "TerminatesPlan 必须等价于『未提交且原因为 TargetInvalid』：reason=" + reason);
            }
        }

        // ————————————————————————————————————————————————————————————
        // R4 / B-1：预算释放回调的顺序契约（外部回调不得留下半提交状态）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <strong>R4 / B-1：<c>BudgetReleaseSink</c> 必须在换位的全部变异之前被调用一次，
        /// 且抛异常的消费者不得留下半提交状态。</strong>
        ///
        /// 独立验证 M-3 指出：旧代码把 <c>?.Invoke</c> 放在锚点提交与依赖移动清理<strong>之后</strong>，
        /// 一旦任务 07 接通消费者，抛异常的消费者会留下"锚点已换、段/预留未清理"的中间状态。
        /// 本用例用两个可失败断言把新顺序钉死：
        /// <list type="number">
        /// <item><strong>回调时刻的世界状态</strong>：在回调内部读取权威锚点、移动段数、
        /// 失效计划的 Reservation 与提交日志长度——全部必须还是"提交前"的值
        /// （旧顺序下锚点已是目的格 ⇒ 立即失败）；</item>
        /// <item><strong>异常安全</strong>：让消费者抛异常 ⇒ 世界必须保持完全未提交
        /// （锚点、段、预留、提交日志、修订号逐项不变），调用方可安全重试。</item>
        /// </list>
        /// </summary>
        [Test]
        public void BudgetReleaseSinkRunsBeforeAnyRelocationMutationAndThrowingSinkLeavesNoPartialCommit()
        {
            var rig = new Rig();
            ActionPlan dodge = rig.AcceptDodge();

            // 建立一条失效移动链（真实计划对象 + 真实段 + 真实 Reservation）。
            ActionPlan movePlan = rig.EstablishHeroMovement(77L, 130L);
            int segmentsBefore = rig.Movement.AllSegmentsOrdered().Count;
            int heroReservationsBefore = rig.Grid.ReservationsOfPlanOrdered(movePlan.ActionPlanId).Count;
            Assert.That(segmentsBefore, Is.GreaterThan(0), "对照证据：必须先有真实段可被清理");
            Assert.That(heroReservationsBefore, Is.GreaterThan(0), "对照证据：必须先有真实 Reservation");

            var invalidated = new List<ActionPlan> { movePlan };

            // —— ① 回调时刻的世界状态必须是"提交前" ——
            GridPoint anchorAtCallback = default;
            int segmentsAtCallback = -1;
            int heroReservationsAtCallback = -1;
            int commitLogAtCallback = -1;
            int callbackCount = 0;
            rig.Dodge.BudgetReleaseSink = ids =>
            {
                callbackCount++;
                rig.Grid.TryGetAnchor(Task05Farm.Enemy, out anchorAtCallback);
                segmentsAtCallback = rig.Movement.AllSegmentsOrdered().Count;
                heroReservationsAtCallback = rig.Grid.ReservationsOfPlanOrdered(movePlan.ActionPlanId).Count;
                commitLogAtCallback = rig.Dodge.CommitLog.Count;
                Assert.That(ids, Is.Not.Null);
                Assert.That(ids.Count, Is.EqualTo(1), "失效集合必须按 ActionPlanId 升序给出 1 条");
                Assert.That(ids[0].Value, Is.EqualTo(movePlan.ActionPlanId.Value));
            };

            long revisionBefore = rig.Revision;
            string code = rig.Dodge.TryRelocate(dodge, invalidated, TriggerTick);

            Assert.That(code, Is.Null, "无占用冲突 ⇒ 换位必须成功");
            Assert.That(callbackCount, Is.EqualTo(1), "预算释放接缝只在真正换位时被调用一次");
            Assert.That(anchorAtCallback, Is.EqualTo(DefenderAnchor),
                "回调必须发生在锚点提交之前（旧顺序下此处已是目的格）");
            Assert.That(segmentsAtCallback, Is.EqualTo(segmentsBefore),
                "回调必须发生在依赖移动清理之前");
            Assert.That(heroReservationsAtCallback, Is.EqualTo(heroReservationsBefore),
                "回调必须发生在依赖 Reservation 释放之前");
            Assert.That(commitLogAtCallback, Is.EqualTo(0),
                "回调必须发生在本次提交被记录之前");

            // 提交确实完成了全部变异。
            Assert.That(rig.Grid.TryGetAnchor(Task05Farm.Enemy, out GridPoint committed), Is.True);
            Assert.That(committed, Is.EqualTo(DodgeDestination));
            Assert.That(rig.Movement.AllSegmentsOrdered().Count, Is.EqualTo(0), "失效移动的段必须被清理");
            Assert.That(rig.Grid.ReservationsOfPlanOrdered(movePlan.ActionPlanId).Count, Is.EqualTo(0));
            Assert.That(rig.Dodge.CommitLog.Count, Is.EqualTo(1));
            Assert.That(rig.Revision, Is.EqualTo(revisionBefore), "换位不推进排程修订号");

            // —— ② 抛异常的消费者：世界必须完全未提交 ——
            var rig2 = new Rig();
            ActionPlan dodge2 = rig2.AcceptDodge();
            ActionPlan movePlan2 = rig2.EstablishHeroMovement(78L, 130L);
            int segmentsBefore2 = rig2.Movement.AllSegmentsOrdered().Count;
            int heroReservationsBefore2 = rig2.Grid.ReservationsOfPlanOrdered(movePlan2.ActionPlanId).Count;
            long revisionBefore2 = rig2.Revision;

            rig2.Dodge.BudgetReleaseSink = ids => throw new InvalidOperationException("消费者故障（负控制）");

            Assert.That(() => rig2.Dodge.TryRelocate(
                    dodge2, new List<ActionPlan> { movePlan2 }, TriggerTick),
                Throws.TypeOf<InvalidOperationException>(),
                "消费者的异常必须原样上抛（不得被静默吞掉，否则任务 07 的故障不可见）");

            Assert.That(rig2.Grid.TryGetAnchor(Task05Farm.Enemy, out GridPoint still), Is.True);
            Assert.That(still, Is.EqualTo(DefenderAnchor),
                "抛异常 ⇒ 不得留下半提交状态：锚点必须还在原位");
            Assert.That(rig2.Dodge.TryGetReservation(dodge2.ReactionOpportunityId.Value, out _), Is.True,
                "抛异常 ⇒ 目的格预留必须仍在（提交没有开始）");
            Assert.That(rig2.Movement.AllSegmentsOrdered().Count, Is.EqualTo(segmentsBefore2),
                "抛异常 ⇒ 依赖移动的段必须原样");
            Assert.That(rig2.Grid.ReservationsOfPlanOrdered(movePlan2.ActionPlanId).Count,
                Is.EqualTo(heroReservationsBefore2), "抛异常 ⇒ 依赖 Reservation 必须原样");
            Assert.That(rig2.Dodge.CommitLog.Count, Is.EqualTo(0), "抛异常 ⇒ 不得记录一次提交");
            Assert.That(rig2.Revision, Is.EqualTo(revisionBefore2));
            Assert.That(dodge2.IsTerminal, Is.False, "抛异常 ⇒ 不得写计划终态");
            Assert.That(rig2.Grid.VerifyConsistency(), Is.Null);
        }

        // ————————————————————————————————————————————————————————————
        // R4 / B-2：终态清理不可跳过
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <strong>R4 / B-2：来源威胁取消时的"释放未消费预留"通知不得因绑定计划已终态而被跳过。</strong>
        ///
        /// 独立验证 L-4 登记的子情形：<c>CancelForSourceThreat</c> 只在绑定计划<strong>仍非终态</strong>时
        /// 经统一终态协调器进入终态，而终态清理是 Dodge 目的格预留的常规释放路径；
        /// 绑定计划<strong>已经是终态</strong>时协调器幂等跳过清理。
        ///
        /// 本用例把"绑定计划已经终态"这一子情形**真实构造出来**（先经另一个原因终止绑定计划，
        /// 再让来源威胁取消），并在<strong>未接任何接收方</strong>的前提下断言预留<strong>必须</strong>消失：
        /// 这正是 R4 要求的"不可跳过"。对照证据同时给出：该预留的消失<strong>不是</strong>终态清理的功劳
        /// （第一步的终态原因本身不释放 Dodge 预留——见下面的中间断言），
        /// 因此它只能来自取消路径上的确定性就地释放。
        /// </summary>
        [Test]
        public void SourceThreatCancellationReleasesReservationEvenWhenBoundPlanIsAlreadyTerminal()
        {
            var rig = new Rig(wireDodgeReleaseParticipant: false);
            ActionPlan dodge = rig.AcceptDodge();
            ActionPlan source = rig.S.Authority.Registry.Find(rig.Opened[0].SourceAttackPlanId);
            Assert.That(source, Is.Not.Null, "Dodge 的来源攻击必须在权威注册表里");

            Assert.That(rig.System.ReservationReleaseSink, Is.Null,
                "对照前提：本用例刻意不接任何接收方（复现生产默认装配的口径）");
            Assert.That(rig.System.PendingReservationReleases.Count, Is.EqualTo(0));

            // 绑定计划先因**另一个**原因进入终态（第一胜出者不是 SourceThreatCancelled）。
            ActionPlanTerminalOutcome outcome = rig.Coordinator.EnterTerminal(
                dodge, ActionTerminationReason.TargetInvalid, AcceptTick);
            Assert.That(outcome.EnteredTerminal, Is.True);
            Assert.That(dodge.IsTerminal, Is.True);
            Assert.That(dodge.TerminationReason, Is.EqualTo(ActionTerminationReason.TargetInvalid));
            // 本夹具刻意把 Dodge 预留端口**不**挂到终态协调器的参与者上，用来隔离出
            // "终态清理被跳过"这一条路径：因此此刻预留仍然存在。
            Assert.That(rig.Dodge.TryGetReservation(dodge.ReactionOpportunityId.Value, out _), Is.True,
                "对照证据：本用例的终态原因不释放 Dodge 目的格预留（窗口存在，等待取消路径处理）");

            List<ActionPlanId> terminated = rig.System.CancelForSourceThreat(source.ActionPlanId, AcceptTick + 1L);

            Assert.That(terminated, Is.Empty,
                "绑定计划已终态 ⇒ 协调器幂等跳过（不重复发终态）；这正是不变量 6 要求的形态");
            Assert.That(rig.Dodge.TryGetReservation(dodge.ReactionOpportunityId.Value, out _), Is.False,
                "终态清理被跳过时，释放请求仍必须真的生效（不得只排队等待任务 07）");
            Assert.That(rig.Reservations, Is.EqualTo(0));
            Assert.That(rig.System.PendingReservationReleases.Count, Is.EqualTo(0),
                "就地释放成功 ⇒ 不得再留下排队项（避免任务 07 二次释放同一预留）");
        }

        /// <summary>
        /// <strong>R4 / B-2 的生产口径与剩余归属（两条并列断言）</strong>：
        /// <list type="number">
        /// <item><strong>生产装配（含终态清理参与者）</strong>：绑定计划仍非终态时，来源威胁取消
        /// 经统一协调器进入终态，Dodge 目的格预留<strong>由终态清理参与者释放</strong>
        /// （<c>LogicGridMovementAuthority.Cleanup</c> → <c>DodgeDestinationReservations.ReleaseForPlan</c>）；
        /// 此时释放通知因没有接收方而进入 <c>_pendingReleases</c> 队列——
        /// 那正是任务 07 要消费的对象，不是泄漏。</item>
        /// <item><strong>无参与者装配</strong>：同一场景下预留不会被终态清理释放；
        /// 取消路径的就地释放（R4/B-2 的修正）保证它<strong>仍然</strong>消失。
        /// 两条断言合起来说明："预留一定被释放"在两种装配下都成立。</item>
        /// </list>
        /// 这两条都必须显式断言，否则"B-2 已关闭"会被误读成"所有未接接收方的情形都已就地释放"。
        /// </summary>
        [Test]
        public void SourceThreatCancellationPutsReleaseOnQueueInProductionWiringAndStillReleasesWithoutParticipant()
        {
            // —— ① 生产装配：终态清理参与者负责释放；通知排队给任务 07 ——
            var production = new Rig();
            ActionPlan dodge = production.AcceptDodge();
            ActionPlan source = production.S.Authority.Registry.Find(production.Opened[0].SourceAttackPlanId);
            Assert.That(source, Is.Not.Null);
            Assert.That(production.System.ReservationReleaseSink, Is.Null);

            List<ActionPlanId> terminated =
                production.System.CancelForSourceThreat(source.ActionPlanId, AcceptTick + 1L);

            Assert.That(terminated.Count, Is.EqualTo(1), "绑定计划仍非终态 ⇒ 由统一协调器终止");
            Assert.That(dodge.IsTerminal, Is.True);
            Assert.That(dodge.TerminationReason, Is.EqualTo(ActionTerminationReason.SourceThreatCancelled));
            Assert.That(production.Dodge.TryGetReservation(dodge.ReactionOpportunityId.Value, out _), Is.False,
                "生产装配下该预留在计划进入终态时已被 MovementAndReservation = 500 参与者释放，"
                + "取消路径上的就地释放再幂等确认一次");

            // 取消通知的场景是"计划已终态"（协调器刚把它推进去）⇒ 就地释放已处理，
            // 因此队列里**只有**该机会的取消通知？不：请求携带的释放被就地消化后不再入队，
            // 这里断言队列为空正是"不留重复释放项"的证据。
            Assert.That(production.System.PendingReservationReleases.Count, Is.EqualTo(0),
                "计划已终态 ⇒ 释放已就地完成，不得再留下队列项（任务 07 不会收到重复释放请求）");

            // 若接收方已就位，则同一场景下请求走接收方（而不是队列）。
            var sink = new RecordingReleaseSink();
            production.System.ReservationReleaseSink = sink;
            Assert.That(sink.Requests.Count, Is.EqualTo(0), "对照证据：接收方是本步才接上的");
            Assert.That(production.Dodge.ReleaseDestination(dodge.ReactionOpportunityId.Value), Is.False,
                "幂等：重复释放必须返回『没有释放任何东西』");
            Assert.That(production.Dodge.TryGetReservation(dodge.ReactionOpportunityId.Value, out _), Is.False);

            // —— ② 无参与者装配：就地释放保证不泄漏 ——
            var isolated = new Rig(wireDodgeReleaseParticipant: false);
            ActionPlan dodge2 = isolated.AcceptDodge();
            ActionPlan source2 = isolated.S.Authority.Registry.Find(isolated.Opened[0].SourceAttackPlanId);
            Assert.That(source2, Is.Not.Null);
            Assert.That(isolated.Dodge.TryGetReservation(dodge2.ReactionOpportunityId.Value, out _), Is.True);

            List<ActionPlanId> terminated2 =
                isolated.System.CancelForSourceThreat(source2.ActionPlanId, AcceptTick + 1L);

            Assert.That(terminated2.Count, Is.EqualTo(1));
            Assert.That(isolated.Dodge.TryGetReservation(dodge2.ReactionOpportunityId.Value, out _), Is.False,
                "无参与者装配下也必须释放（R4/B-2 的就地释放）");
            Assert.That(isolated.System.PendingReservationReleases.Count, Is.EqualTo(0),
                "就地释放成功 ⇒ 不留排队项");

            // —— ③ "仍非终态 + 无接收方" 的队列语义（任务 05 冻结口径）——
            // 该情形只有在没有终态协调器时才能被构造出来（有协调器则一定把计划推进终态，
            // 从而落入"已终态 ⇒ 就地释放"）。这里显式断言它**仍然**排队保留，绝不静默丢弃。
            var queued = new RigWithoutCoordinator();
            ActionPlan dodge3 = queued.AcceptDodge();
            Assert.That(queued.Dodge.TryGetReservation(dodge3.ReactionOpportunityId.Value, out _), Is.True);
            Assert.That(queued.System.ReservationReleaseSink, Is.Null);

            List<ActionPlanId> terminated3 = queued.System.CancelForSourceThreat(
                queued.SourceAttackPlan, AcceptTick + 1L);

            Assert.That(terminated3.Count, Is.EqualTo(1),
                "计划仍非终态 ⇒ 进入 terminated 列表；本夹具没有协调器，因此终态字段并未被写入");
            Assert.That(dodge3.IsTerminal, Is.False,
                "没有协调器 ⇒ 不写终态（该夹具的显式构造，用于隔离出『仍非终态 + 无接收方』这一情形）");
            Assert.That(queued.Dodge.TryGetReservation(dodge3.ReactionOpportunityId.Value, out _), Is.True,
                "『仍非终态』⇒ 释放请求归任务 07，本任务不改预留");
            Assert.That(queued.System.PendingReservationReleases.Count, Is.EqualTo(1),
                "没有接收方时必须排队保留（任务 05 冻结语义），而不是静默丢弃");
            ReactionReservationReleaseRequest request = queued.System.PendingReservationReleases[0];
            Assert.That(request.ReactionPlanId, Is.EqualTo(dodge3.ActionPlanId));
            Assert.That(request.OpportunityId, Is.EqualTo(dodge3.ReactionOpportunityId.Value));
        }

        /// <summary>
        /// 只用于构造"仍非终态 + 无接收方"这一情形的夹具：没有终态协调器，
        /// 因此来源威胁取消不会推进计划终态，释放请求按任务 05 冻结语义入队。
        /// </summary>
        private sealed class RigWithoutCoordinator
        {
            public readonly Task05Scheduler S = new Task05Scheduler();
            public readonly LogicGrid Grid;
            public readonly LogicGridMovementAuthority Movement;
            public readonly ReactionOpportunitySystem System;
            public readonly DodgeRelocationAuthority Dodge;
            public readonly List<ReactionOpportunityRuntime> Opened = new List<ReactionOpportunityRuntime>();

            public ActionPlanId SourceAttackPlan { get; private set; }

            public RigWithoutCoordinator()
            {
                Grid = new LogicGrid(new GridBoundaryDefinition(new GridPoint(-40, -40), new GridPoint(40, 40)));
                ActionScheduleAuthority authority = S.Authority;
                authority.IdGenerator = S.Ids;
                Movement = new LogicGridMovementAuthority(
                    Grid, authority, S.Bundle.Definition.Rules.PathCostRules,
                    S.Bundle.Definition.Rules.PathSearchRules);
                System = new ReactionOpportunitySystem(
                    authority, S.Factory, S.Bundle.Definition, S.Bundle.Factions, S.Ids, null, null);
                Dodge = new DodgeRelocationAuthority(Grid, Movement, authority, null,
                    specId => DodgeDestinationRules.FromPayload(
                        S.Bundle.Definition.FindAction(specId)?.Payload as DodgePayloadSpec));
                Movement.DodgeDestinationReservations = Dodge;
                System.DestinationReservationPort = Dodge;

                Assert.That(Grid.RegisterUnitWithPointFootprint(
                        Task05Farm.Hero, new GridPoint(0, 20), GridDirection.East), Is.Null);
                Assert.That(Grid.RegisterUnitWithPointFootprint(
                        Task05Farm.Enemy, DefenderAnchor, GridDirection.East), Is.Null);
            }

            public ActionPlan AcceptDodge()
            {
                ActionPlanCreationResult result =
                    S.Bundle.CreatePlan(Task05Farm.LongTelegraphAttackId, TelegraphTick);
                Assert.That(result.Succeeded, Is.True, result.RejectionCode);
                ActionPlan attack = result.Plan;
                S.Authority.RegisterPlan(attack);
                Task05Scheduler.Lock(attack, TelegraphTick);
                SourceAttackPlan = attack.ActionPlanId;

                Opened.Clear();
                Assert.That(System.TryOpenForTelegraph(attack, TelegraphTick, Opened), Is.Null);
                Assert.That(Opened.Count, Is.EqualTo(1));

                string error = System.TryAcceptWithCommandSequence(
                    Opened[0].Id, Task05Farm.Enemy, DodgeSpec, PlayerSequence, AcceptTick,
                    DodgeDestination, out ActionPlan dodge);
                Assert.That(error, Is.Null, error);
                return dodge;
            }
        }

        /// <summary>释放请求记录桩（只记录，不改状态）。</summary>
        private sealed class RecordingReleaseSink : IReactionReservationReleaseSink
        {
            public readonly List<ReactionReservationReleaseRequest> Requests =
                new List<ReactionReservationReleaseRequest>();

            public void ReleaseFor(ActionPlanId reactionPlanId, ReactionOpportunityId opportunityId, long tick)
                => Requests.Add(new ReactionReservationReleaseRequest(reactionPlanId, opportunityId, tick));
        }
    }
}
