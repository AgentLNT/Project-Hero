using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Authoring.Tests.Task05
{
    /// <summary>
    /// 任务 05 阶段 B：<c>ScheduleEvaluator</c>（纯求值）+ <c>ScheduleEditor</c>（权威事务）
    /// + Add/Move/Remove + 全局 <c>ScheduleRevision</c> + 只向右 ripple / 删除不左吸
    /// + 预览与提交同源 + 稳定拒绝码 + <c>ScheduleLimits</c> 三个上限与最大排程视野的真实消费。
    ///
    /// 时序事实（来自 <see cref="Task05Farm"/>）：Attack 前摇 30 + 后摇 30 ⇒ 区间长度 60；
    /// Guard 10+20+30 ⇒ 60；Move 权重 w × 基准 5 + 后摇 10。
    /// </summary>
    [TestFixture]
    public sealed class Task05ScheduleEditorTests
    {
        private const long T = 0L;   // 默认命令目标 Tick

        /// <summary>
        /// 下一个 Tick。冻结语义里"陈旧修订"是<strong>跨 Tick</strong> 条件
        /// （规格原文：「跨 Tick 旧修订以 <c>STALE_SCHEDULE_REVISION</c> 拒绝」）：
        /// 同一 Tick、同一冻结基线的第二条事务是<strong>合法续作</strong>
        /// （见 <c>DisjointScheduleEditsFromSameFrozenRevisionMayBothSucceed</c>），
        /// 因此陈旧场景必须在另一个 Tick 上提交。
        /// </summary>
        private const long NextTick = T + 1L;

        private static readonly Func<ActionPlan, long> IdOf = p => p.ActionPlanId.Value;

        /// <summary>把 <c>IReadOnlyList&lt;ActionPlanId&gt;</c> 投影为 <c>long[]</c>（不依赖 LINQ，便于离线运行）。</summary>
        private static long[] LongIdsOf(IReadOnlyList<ActionPlanId> ids)
        {
            var result = new long[ids.Count];
            for (int i = 0; i < ids.Count; i++) result[i] = ids[i].Value;
            return result;
        }

        /// <summary>把求值结果列表投影为 <c>long[]</c>（同上）。</summary>
        private static long[] LongIdsOf(IReadOnlyList<ScheduleOperationEvaluation> rows)
        {
            var result = new long[rows.Count];
            for (int i = 0; i < rows.Count; i++) result[i] = rows[i].PlanId.Value;
            return result;
        }

        /// <summary>
        /// 把计划 ID 列表投影为<strong>十进制文本</strong>数组。
        /// 用文本而不是数值：NUnit 的 <c>Does.Contain</c>/<c>Does.Not.Contain</c> 在
        /// <c>unity-custom</c> 构建里对"数值集合 + 数值期望"的组合会走字符串比较路径，
        /// 直接给文本可让"包含/不包含"的语义无歧义。
        /// </summary>
        private static string[] TextIdsOf(IReadOnlyList<ActionPlanId> ids)
        {
            var result = new string[ids.Count];
            for (int i = 0; i < ids.Count; i++) result[i] = ids[i].Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            return result;
        }

        private static string[] TextIdsOf(IReadOnlyList<ScheduleOperationEvaluation> rows)
        {
            var result = new string[rows.Count];
            for (int i = 0; i < rows.Count; i++) result[i] = rows[i].PlanId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            return result;
        }

        // ————————————————————————————————————————————————————————————
        // 预览与提交同源
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>SchedulePreviewAndCommitUseSameCanonicalEvaluator</c>：
        /// 预览与提交返回<strong>逐字段相同</strong>的求值结果（同一算法），
        /// 且预览不分配正式 ID、不改修订号、不动任何既有计划。
        /// </summary>
        [Test]
        public void SchedulePreviewAndCommitUseSameCanonicalEvaluator()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            ActionPlan b = s.Attack(140L);
            s.Authority.RegisterPlan(a);
            s.Authority.RegisterPlan(b);

            long revisionBefore = s.Revision;
            long nextIdBefore = s.Ids.NextActionPlanIdValue;

            ScheduleEditTransactionResult preview = s.Preview(new ScheduleEditOperation[]
            {
                s.AddOp(1L, 200L), Task05Scheduler.MoveOp(b.ActionPlanId, 100L)
            });

            Assert.That(preview.Succeeded, Is.True, "预览必须成功：" + preview.RejectionCode);
            Assert.That(preview.IsPreview, Is.True);
            Assert.That(preview.Committed, Is.False, "预览绝不是提交");
            Assert.That(preview.AddedPlanIds, Is.Empty, "预览不得分配正式 ActionPlanId");
            Assert.That(s.Revision, Is.EqualTo(revisionBefore), "预览不得修改 ScheduleRevision");
            Assert.That(s.Ids.NextActionPlanIdValue, Is.EqualTo(nextIdBefore),
                "预览不得消耗 ActionPlanId 计数器");
            Assert.That(a.StartTick, Is.EqualTo(100L), "预览不得改动任何既有计划");
            Assert.That(b.StartTick, Is.EqualTo(140L), "预览不得改动任何既有计划");

            ScheduleEditTransactionResult commit = s.Apply(new ScheduleEditOperation[]
            {
                s.AddOp(1L, 200L), Task05Scheduler.MoveOp(b.ActionPlanId, 100L)
            });

            Assert.That(commit.Succeeded, Is.True, "提交必须成功：" + commit.RejectionCode);
            Assert.That(commit.Committed, Is.True);
            Assert.That(commit.IsPreview, Is.False);
            Assert.That(s.Revision, Is.EqualTo(revisionBefore + 1L), "成功事务恰好 +1");

            Assert.That(commit.Evaluations.Count, Is.EqualTo(preview.Evaluations.Count));
            for (int i = 0; i < commit.Evaluations.Count; i++)
            {
                Assert.That(commit.Evaluations[i].PlanId, Is.EqualTo(preview.Evaluations[i].PlanId));
                Assert.That(commit.Evaluations[i].NewStartTick, Is.EqualTo(preview.Evaluations[i].NewStartTick),
                    "预览与提交的规范 Tick 必须一致（同源求值器）");
                Assert.That(commit.Evaluations[i].IsDirectMove, Is.EqualTo(preview.Evaluations[i].IsDirectMove));
            }

            Assert.That(commit.AddedPlanIds.Count, Is.EqualTo(1));
            ActionPlan added = s.Authority.Registry.Find(commit.AddedPlanIds[0]);
            Assert.That(added.StartTick, Is.EqualTo(200L), "Add 的最终位点必须与预览一致");
        }

        // ————————————————————————————————————————————————————————————
        // 修订号语义
        // ————————————————————————————————————————————————————————————

        /// <summary><c>SuccessfulScheduleEditIncrementsRevisionExactlyOnce</c>。</summary>
        [Test]
        public void SuccessfulScheduleEditIncrementsRevisionExactlyOnce()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            s.Authority.RegisterPlan(a);

            for (int i = 0; i < 3; i++)
            {
                long before = s.Revision;
                ScheduleEditTransactionResult result = s.Apply(
                    new ScheduleEditOperation[] { Task05Scheduler.MoveOp(a.ActionPlanId, 100L + (i * 10L)) });

                Assert.That(result.Succeeded, Is.True, result.RejectionCode);
                Assert.That(s.Revision, Is.EqualTo(before + 1L), "第 " + i + " 次成功事务必须恰好 +1");
            }
            Assert.That(s.Revision, Is.EqualTo(3L));
        }

        /// <summary>
        /// <c>FailedOrPreviewedScheduleEditDoesNotIncrementRevision</c>：
        /// 失败与预览都不增加修订号（且失败是零局部写入）。
        /// </summary>
        [Test]
        public void FailedOrPreviewedScheduleEditDoesNotIncrementRevision()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            s.Authority.RegisterPlan(a);
            long baseline = s.Revision;

            s.Preview(new ScheduleEditOperation[] { Task05Scheduler.MoveOp(a.ActionPlanId, 300L) });
            Assert.That(s.Revision, Is.EqualTo(baseline), "预览不增加修订号");
            Assert.That(a.StartTick, Is.EqualTo(100L));

            ScheduleEditTransactionResult failed = s.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(new ActionPlanId(9999L), 300L) });
            Assert.That(failed.Succeeded, Is.False);
            Assert.That(failed.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_PLAN_NOT_IN_LANE));
            Assert.That(s.Revision, Is.EqualTo(baseline), "失败不增加修订号");
            Assert.That(a.StartTick, Is.EqualTo(100L), "失败事务是零局部写入");

            // 单批操作上限。
            Task05Scheduler tiny = new Task05Scheduler(new ScheduleLimits(
                MaxQueuedPlansPerLane: 16, MaxBatchOperations: 2, MaxDependencyClosurePlans: 256,
                MaxPreScheduleHorizonTicks: 36000L));
            ActionPlan t = tiny.Attack(100L);
            tiny.Authority.RegisterPlan(t);
            ScheduleEditTransactionResult overLimit = tiny.Apply(new ScheduleEditOperation[]
            {
                Task05Scheduler.MoveOp(t.ActionPlanId, 100L),
                Task05Scheduler.MoveOp(t.ActionPlanId, 130L),
                Task05Scheduler.MoveOp(t.ActionPlanId, 160L)
            });
            Assert.That(overLimit.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_BATCH_TOO_LARGE));
            Assert.That(tiny.Revision, Is.EqualTo(0L));
        }

        /// <summary>
        /// <c>StaleScheduleRevisionRejectsWholeBatch</c>：
        /// Expected 值与 Step 冻结的 <c>BatchBaseScheduleRevision</c> 比较；
        /// 跨 Tick 的旧修订以 <c>STALE_SCHEDULE_REVISION</c> 拒绝整批，且零局部写入。
        /// </summary>
        [Test]
        public void StaleScheduleRevisionRejectsWholeBatch()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            ActionPlan b = s.Attack(300L);
            s.Authority.RegisterPlan(a);
            s.Authority.RegisterPlan(b);

            Assert.That(s.Apply(new ScheduleEditOperation[] { Task05Scheduler.MoveOp(a.ActionPlanId, 110L) }).Succeeded,
                Is.True);
            Assert.That(s.Revision, Is.EqualTo(1L));

            long[] startsBefore = s.StartTicksOf(a, b);
            long[] laneBefore = s.LanePlanIds(Task05Farm.Hero);
            long idBefore = s.Ids.NextActionPlanIdValue;

            ScheduleEditTransactionResult stale = s.Editor.Apply(
                new ScheduleEditOperation[] { s.AddOp(7L, 500L), Task05Scheduler.MoveOp(b.ActionPlanId, 320L) },
                NextTick, batchBaseScheduleRevision: 0L, expectedScheduleRevision: 0L);

            Assert.That(stale.Succeeded, Is.False);
            Assert.That(stale.RejectionCode, Is.EqualTo(ScheduleCodes.STALE_SCHEDULE_REVISION));
            Assert.That(s.Revision, Is.EqualTo(1L), "陈旧批次不得推进修订号");
            Assert.That(s.StartTicksOf(a, b), Is.EqualTo(startsBefore), "陈旧批次必须零局部写入");
            Assert.That(s.LanePlanIds(Task05Farm.Hero), Is.EqualTo(laneBefore), "Lane 不得被改动");
            Assert.That(s.Ids.NextActionPlanIdValue, Is.EqualTo(idBefore), "陈旧批次不得消耗 ID");
            Assert.That(s.Authority.Registry.ActiveCount, Is.EqualTo(2), "不得留下孤立计划");
        }

        /// <summary>
        /// <c>DisjointScheduleEditsFromSameFrozenRevisionMayBothSucceed</c>：
        /// 互不相交 Lane 的事务可以依序各成功并各 +1。
        /// </summary>
        [Test]
        public void DisjointScheduleEditsFromSameFrozenRevisionMayBothSucceed()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan hero = s.Attack(100L);

            ActionPlanCreationResult enemyResult = s.Factory.TryCreateOrdinary(
                new OrdinaryPlanRequest(
                    Task05Farm.Enemy, new ActionSpecId(Task05Farm.AttackId), GridDirection.East,
                    Task05Farm.Hero, null, null, 100L),
                0L);
            Assert.That(enemyResult.Succeeded, Is.True, enemyResult.RejectionCode);
            ActionPlan enemy = enemyResult.Plan;

            s.Authority.RegisterPlan(hero);
            s.Authority.RegisterPlan(enemy);

            long frozenBase = s.Revision;   // 本批冻结时的基线（0）

            ScheduleEditTransactionResult first = s.Editor.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(hero.ActionPlanId, 200L) },
                T, batchBaseScheduleRevision: frozenBase, expectedScheduleRevision: frozenBase);
            Assert.That(first.Succeeded, Is.True, first.RejectionCode);
            Assert.That(s.Revision, Is.EqualTo(1L));

            ScheduleEditTransactionResult second = s.Editor.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(enemy.ActionPlanId, 200L) },
                T, batchBaseScheduleRevision: frozenBase, expectedScheduleRevision: frozenBase);
            Assert.That(second.Succeeded, Is.True,
                "互不相交 Lane 的第二条事务必须成功（" + second.RejectionCode + "）");
            Assert.That(s.Revision, Is.EqualTo(2L), "两条成功事务各 +1");
            Assert.That(hero.StartTick, Is.EqualTo(200L));
            Assert.That(enemy.StartTick, Is.EqualTo(200L));
        }

        /// <summary>
        /// <c>OverlappingScheduleEditLaterInBatchIsRejectedByCommandOrder</c>：
        /// 触及本批已改写依赖闭包的计划时，后处理事务以
        /// <c>SCHEDULE_EDIT_CONFLICT_IN_BATCH</c> 拒绝，先处理者保持已提交结果。
        /// </summary>
        [Test]
        public void OverlappingScheduleEditLaterInBatchIsRejectedByCommandOrder()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            ActionPlan b = s.Attack(140L);
            s.Authority.RegisterPlan(a);
            s.Authority.RegisterPlan(b);

            long frozenBase = s.Revision;

            ScheduleEditTransactionResult first = s.Editor.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(a.ActionPlanId, 300L) },
                T, batchBaseScheduleRevision: frozenBase, expectedScheduleRevision: frozenBase);
            Assert.That(first.Succeeded, Is.True, first.RejectionCode);
            Assert.That(b.StartTick, Is.EqualTo(360L),
                "b 必须被 ripple 到 a 的新区间 [300,360) 之后");

            long[] startsBefore = s.StartTicksOf(a, b);
            long revisionBefore = s.Revision;
            ScheduleEditTransactionResult later = s.Editor.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(b.ActionPlanId, 500L) },
                T, batchBaseScheduleRevision: frozenBase, expectedScheduleRevision: frozenBase);

            Assert.That(later.Succeeded, Is.False);
            Assert.That(later.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_EDIT_CONFLICT_IN_BATCH));
            Assert.That(s.StartTicksOf(a, b), Is.EqualTo(startsBefore), "后处理者被拒后零局部写入");
            Assert.That(s.Revision, Is.EqualTo(revisionBefore), "被拒事务不推进修订号");
        }

        /// <summary>
        /// <c>ScheduleEditFailureLeavesNoPartialPlanOrLaneMutation</c>：
        /// 一批中后一个操作失败 ⇒ 整批回滚（无孤立计划、无 Lane 改动、无 ID 消耗、无修订推进）。
        /// </summary>
        [Test]
        public void ScheduleEditFailureLeavesNoPartialPlanOrLaneMutation()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            s.Authority.RegisterPlan(a);

            long[] startsBefore = s.StartTicksOf(a);
            long[] laneBefore = s.LanePlanIds(Task05Farm.Hero);
            long revisionBefore = s.Revision;
            long idBefore = s.Ids.NextActionPlanIdValue;
            int activeBefore = s.Authority.Registry.ActiveCount;

            ScheduleEditTransactionResult result = s.Apply(new ScheduleEditOperation[]
            {
                s.AddOp(1L, 400L),
                Task05Scheduler.MoveOp(new ActionPlanId(4242L), 500L)
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_PLAN_NOT_IN_LANE));
            Assert.That(s.Authority.Registry.ActiveCount, Is.EqualTo(activeBefore), "不得留下孤立计划");
            Assert.That(s.Ids.NextActionPlanIdValue, Is.EqualTo(idBefore), "失败事务不得消耗 ActionPlanId");
            Assert.That(s.Revision, Is.EqualTo(revisionBefore), "失败不推进修订号");
            Assert.That(s.StartTicksOf(a), Is.EqualTo(startsBefore), "既有计划不得被改动");
            Assert.That(s.LanePlanIds(Task05Farm.Hero), Is.EqualTo(laneBefore), "Lane 不得被改动");
            Assert.That(s.Authority.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null,
                "Step 末不变量仍然成立");
        }

        /// <summary>
        /// <c>AddedPlanReceivesIdOnlyAfterTransactionValidation</c>：
        /// 正式 <c>ActionPlanId</c> 只在事务校验通过后的提交点占用并注册；
        /// 失败与预览都<strong>不</strong>消耗 ID。
        /// </summary>
        [Test]
        public void AddedPlanReceivesIdOnlyAfterTransactionValidation()
        {
            Task05Scheduler s = new Task05Scheduler();
            long idBefore = s.Ids.NextActionPlanIdValue;

            ScheduleEditTransactionResult preview = s.Preview(new ScheduleEditOperation[] { s.AddOp(1L, 100L) });
            Assert.That(preview.Succeeded, Is.True, preview.RejectionCode);
            Assert.That(preview.AddedPlanIds, Is.Empty);
            Assert.That(s.Ids.NextActionPlanIdValue, Is.EqualTo(idBefore));
            Assert.That(s.Authority.Registry.ActiveCount, Is.EqualTo(0));

            ScheduleEditTransactionResult failed = s.Apply(new ScheduleEditOperation[]
            {
                s.AddOp(1L, 100L), Task05Scheduler.MoveOp(new ActionPlanId(777L), 100L)
            });
            Assert.That(failed.Succeeded, Is.False);
            Assert.That(s.Ids.NextActionPlanIdValue, Is.EqualTo(idBefore), "失败事务不得消耗 ID");
            Assert.That(s.Authority.Registry.ActiveCount, Is.EqualTo(0), "不得注册候选计划");

            ScheduleEditTransactionResult ok = s.Apply(new ScheduleEditOperation[] { s.AddOp(1L, 100L) });
            Assert.That(ok.Succeeded, Is.True, ok.RejectionCode);
            Assert.That(ok.AddedPlanIds.Count, Is.EqualTo(1));
            Assert.That(ok.AddedPlanIds[0].Value, Is.EqualTo(idBefore), "正式 ID 必须是提交点分配的下一个值");
            Assert.That(s.Ids.NextActionPlanIdValue, Is.EqualTo(idBefore + 1L));
            Assert.That(s.Authority.Registry.Find(ok.AddedPlanIds[0]), Is.Not.Null, "提交后必须已在注册表里");
        }

        // ————————————————————————————————————————————————————————————
        // 规范化规则：只向右 ripple / 不左吸 / 障碍不可变
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>ScheduleRippleMovesOnlyEditablePlansRight</c>：
        /// 重叠只向右 ripple，且只移动 <strong>Editable 普通计划</strong>；
        /// Locked/Running 计划与固定反应区间是障碍，永不被推移。
        /// </summary>
        [Test]
        public void ScheduleRippleMovesOnlyEditablePlansRight()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            ActionPlan editable = s.Attack(400L);
            ActionPlan locked = Task05Scheduler.Lock(s.Attack(700L), lockedAtTick: 5L);
            ActionPlanCreationResult reaction = s.Bundle.CreateReaction(
                Task05Farm.BlockId, triggerTick: 1000L, responseDeadlineTick: 900L);
            Assert.That(reaction.Succeeded, Is.True, reaction.RejectionCode);

            s.Authority.RegisterPlan(a);
            s.Authority.RegisterPlan(editable);
            s.Authority.RegisterPlan(locked);
            s.Authority.RegisterPlan(reaction.Plan);

            ScheduleEditTransactionResult result = s.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(a.ActionPlanId, 380L) });
            Assert.That(result.Succeeded, Is.True, result.RejectionCode);

            Assert.That(a.StartTick, Is.EqualTo(380L), "直接移动的计划落在请求位点");
            Assert.That(editable.StartTick, Is.EqualTo(440L), "重叠只向右 ripple：editable 从 400 推到 440");
            Assert.That(locked.StartTick, Is.EqualTo(700L), "Locked 障碍不得被移动");
            Assert.That(reaction.Plan.StartTick, Is.EqualTo(940L),
                "固定反应区间不得被移动（TriggerTick 1000 - 前摇 60 = 940）");

            string[] rippled = TextIdsOf(result.RipplePlanIds);
            Assert.That(rippled, Does.Contain(editable.ActionPlanId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That(rippled, Does.Not.Contain(locked.ActionPlanId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That(rippled, Does.Not.Contain(reaction.Plan.ActionPlanId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That(s.Authority.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null);
        }

        /// <summary>
        /// <c>OnlyDirectlyMovedPlanMayStartEarlierThanItsOldProjection</c>：
        /// 只有被直接移动的计划可以早于旧投影；被 ripple 的计划<strong>只</strong>向右。
        /// </summary>
        [Test]
        public void OnlyDirectlyMovedPlanMayStartEarlierThanItsOldProjection()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            ActionPlan b = s.Attack(200L);
            ActionPlan c = s.Attack(300L);
            s.Authority.RegisterPlan(a);
            s.Authority.RegisterPlan(b);
            s.Authority.RegisterPlan(c);

            ScheduleEditTransactionResult result = s.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(c.ActionPlanId, 120L) });

            Assert.That(result.Succeeded, Is.True, result.RejectionCode);
            Assert.That(c.StartTick, Is.EqualTo(120L), "直接移动的计划可以早于旧投影");
            Assert.That(a.StartTick, Is.EqualTo(180L), "a 只被向右 ripple（100 → 180）");
            Assert.That(b.StartTick, Is.EqualTo(240L), "b 只被向右 ripple（200 → 240）");

            for (int i = 0; i < result.Evaluations.Count; i++)
            {
                ScheduleOperationEvaluation item = result.Evaluations[i];
                if (item.PlanId == c.ActionPlanId) continue;
                Assert.That(item.IsDirectMove, Is.False, "除直接移动的计划外不得出现 IsDirectMove 项");
            }
            Assert.That(s.Authority.CheckNoActiveArtifactReferencesTerminalPlan(), Is.Null);
        }

        /// <summary>
        /// <c>RemovingEditablePlanDoesNotPullLaterPlansForward</c>：
        /// 删除只移除目标，<strong>不</strong>自动左吸后续计划（不压缩绝对时间空隙）。
        /// </summary>
        [Test]
        public void RemovingEditablePlanDoesNotPullLaterPlansForward()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            ActionPlan b = s.Attack(200L);
            ActionPlan c = s.Attack(300L);
            s.Authority.RegisterPlan(a);
            s.Authority.RegisterPlan(b);
            s.Authority.RegisterPlan(c);

            ScheduleEditTransactionResult result = s.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.RemoveOp(b.ActionPlanId) });

            Assert.That(result.Succeeded, Is.True, result.RejectionCode);
            Assert.That(a.StartTick, Is.EqualTo(100L), "删除不得改动前面的计划");
            Assert.That(c.StartTick, Is.EqualTo(300L), "删除绝不左吸：c 必须留在 300");
            Assert.That(s.LanePlanIds(Task05Farm.Hero),
                Is.EqualTo(new[] { a.ActionPlanId.Value, c.ActionPlanId.Value }),
                "被删计划必须离开 Lane，其余保持规范顺序");
            Assert.That(s.Authority.Registry.Find(b.ActionPlanId), Is.Not.Null,
                "计划对象仍在注册表里（终态由统一协调器处理，排程事务不写终态）");
            Assert.That(result.RemovedPlanIds, Is.EqualTo(new[] { b.ActionPlanId }));
        }

        /// <summary>
        /// <c>MovingEditableAttackRebindsAbsoluteTicksWithoutResamplingSpeed</c>（阶段 B 补全）：
        /// 排程移动只重绑绝对 Tick，绝不重新采样相对时长。
        /// </summary>
        [Test]
        public void MovingEditableAttackRebindsAbsoluteTicksWithoutResamplingSpeed()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            s.Authority.RegisterPlan(a);

            int windup = a.ResolvedWindupTicks;
            int recovery = a.RecoveryTicks;
            Assert.That(windup, Is.EqualTo(30));
            Assert.That(a.ImpactTick, Is.EqualTo(130L));
            Assert.That(a.EndTick, Is.EqualTo(160L));
            Assert.That(a.BudgetCostTicks, Is.EqualTo(60));

            s.Apply(new ScheduleEditOperation[] { Task05Scheduler.MoveOp(a.ActionPlanId, 500L) });
            s.Bundle.Facts.SetActionSpeed(Task05Farm.Hero, 10f);
            s.Apply(new ScheduleEditOperation[] { Task05Scheduler.MoveOp(a.ActionPlanId, 600L) });

            Assert.That(a.StartTick, Is.EqualTo(600L));
            Assert.That(a.ImpactTick, Is.EqualTo(630L), "ImpactTick = StartTick + 已解析前摇");
            Assert.That(a.EndTick, Is.EqualTo(660L), "EndTick = StartTick + 前摇 + 后摇");
            Assert.That(a.ResolvedWindupTicks, Is.EqualTo(windup), "速度变化不得重新采样前摇");
            Assert.That(a.RecoveryTicks, Is.EqualTo(recovery), "后摇不随速度缩短");
            Assert.That(a.BudgetCostTicks, Is.EqualTo(60), "预算仍等于解析前摇 + 后摇");
        }

        // ————————————————————————————————————————————————————————————
        // 可编辑性：Locked / 反应不可移动或删除
        // ————————————————————————————————————————————————————————————

        /// <summary><c>LockedPlanCannotBeMovedOrRemoved</c>。</summary>
        [Test]
        public void LockedPlanCannotBeMovedOrRemoved()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan locked = Task05Scheduler.Lock(s.Attack(100L), lockedAtTick: 3L);
            ActionPlan running = Task05Scheduler.Run(s.Attack(300L), lockedAtTick: 3L);
            s.Authority.RegisterPlan(locked);
            s.Authority.RegisterPlan(running);

            ScheduleEditTransactionResult moveLocked = s.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(locked.ActionPlanId, 200L) });
            Assert.That(moveLocked.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED));

            ScheduleEditTransactionResult removeRunning = s.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.RemoveOp(running.ActionPlanId) });
            Assert.That(removeRunning.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED));

            Assert.That(locked.StartTick, Is.EqualTo(100L));
            Assert.That(running.StartTick, Is.EqualTo(300L));
            Assert.That(s.Revision, Is.EqualTo(0L), "被拒事务不推进修订号");
            Assert.That(s.LanePlanIds(Task05Farm.Hero).Length, Is.EqualTo(2), "Lane 不得被改动");
        }

        /// <summary><c>ReactionPlanCannotBeMovedOrRemoved</c>。</summary>
        [Test]
        public void ReactionPlanCannotBeMovedOrRemoved()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlanCreationResult reaction = s.Bundle.CreateReaction(
                Task05Farm.BlockId, triggerTick: 500L, responseDeadlineTick: 400L);
            Assert.That(reaction.Succeeded, Is.True, reaction.RejectionCode);
            s.Authority.RegisterPlan(reaction.Plan);

            ScheduleEditTransactionResult move = s.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(reaction.Plan.ActionPlanId, 100L) });
            Assert.That(move.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_REACTION_CANNOT_BE_EDITED));

            ScheduleEditTransactionResult remove = s.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.RemoveOp(reaction.Plan.ActionPlanId) });
            Assert.That(remove.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_REACTION_CANNOT_BE_EDITED));

            Assert.That(reaction.Plan.StartTick, Is.EqualTo(440L), "固定反应区间不得被平移");
            Assert.That(s.Revision, Is.EqualTo(0L));
        }

        /// <summary>
        /// 添加锁定 Lane 的新计划以 <c>SCHEDULE_LANE_SUBMISSION_LOCKED</c> 拒绝，
        /// 而修改已有计划不受"提交锁定"影响（它只阻止新提交）。
        /// </summary>
        [Test]
        public void LaneSubmissionLockBlocksAddButNotExistingEdits()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            s.Authority.RegisterPlan(a);
            s.Authority.LockLaneSubmissions(Task05Farm.Hero, "TEST_LOCK");

            ScheduleEditTransactionResult add = s.Apply(new ScheduleEditOperation[] { s.AddOp(1L, 400L) });
            Assert.That(add.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_LANE_SUBMISSION_LOCKED));

            ScheduleEditTransactionResult move = s.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(a.ActionPlanId, 200L) });
            Assert.That(move.Succeeded, Is.True, "提交锁定只阻止新提交：" + move.RejectionCode);
            Assert.That(a.StartTick, Is.EqualTo(200L));
        }

        // ————————————————————————————————————————————————————————————
        // 锚点
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>DuplicateOperationOrCyclicInsertionAnchorRejectsWholeBatch</c>：
        /// 重复操作与成环插入锚点都拒绝整批。
        /// </summary>
        [Test]
        public void DuplicateOperationOrCyclicInsertionAnchorRejectsWholeBatch()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            ActionPlan b = s.Attack(400L);
            s.Authority.RegisterPlan(a);
            s.Authority.RegisterPlan(b);

            ScheduleEditTransactionResult duplicate = s.Apply(new ScheduleEditOperation[]
            {
                s.AddOp(1L, 500L), s.AddOp(1L, 600L)
            });
            Assert.That(duplicate.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_OPERATION_DUPLICATE));
            Assert.That(s.Revision, Is.EqualTo(0L));
            Assert.That(s.Authority.Registry.ActiveCount, Is.EqualTo(2));

            ScheduleEditTransactionResult afterSelf = s.Apply(new ScheduleEditOperation[]
            {
                Task05Scheduler.MoveOp(a.ActionPlanId, 100L, anchor: a.ActionPlanId)
            });
            Assert.That(afterSelf.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_ANCHOR_AFTER_SELF));

            ScheduleEditTransactionResult unknown = s.Apply(new ScheduleEditOperation[]
            {
                Task05Scheduler.MoveOp(a.ActionPlanId, 100L, anchor: new ActionPlanId(98765L))
            });
            Assert.That(unknown.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_ANCHOR_UNKNOWN));

            ScheduleEditTransactionResult cyclic = s.Apply(new ScheduleEditOperation[]
            {
                Task05Scheduler.MoveOp(a.ActionPlanId, 100L, anchor: b.ActionPlanId),
                Task05Scheduler.MoveOp(b.ActionPlanId, 400L, anchor: a.ActionPlanId)
            });
            Assert.That(cyclic.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_ANCHOR_CYCLIC));

            Assert.That(s.Revision, Is.EqualTo(0L));
            Assert.That(a.StartTick, Is.EqualTo(100L));
            Assert.That(b.StartTick, Is.EqualTo(400L));
            Assert.That(s.Authority.Registry.ActiveCount, Is.EqualTo(2));
            Assert.That(s.Ids.NextActionPlanIdValue, Is.EqualTo(3L), "被拒事务不得消耗 ID");
        }

        /// <summary>
        /// 显式插入锚点接受"锚点存在且不成环"的批次（正控制，证明上一条不是恒真拒绝）。
        /// </summary>
        [Test]
        public void ExplicitInsertionAnchorAcceptsAcyclicBatch()
        {
            Task05Scheduler s = new Task05Scheduler();
            ActionPlan a = s.Attack(100L);
            s.Authority.RegisterPlan(a);

            ScheduleEditTransactionResult result = s.Apply(new ScheduleEditOperation[]
            {
                s.AddOp(1L, 400L, anchor: a.ActionPlanId)
            });

            Assert.That(result.Succeeded, Is.True, result.RejectionCode);
            Assert.That(s.Authority.Registry.ActiveCount, Is.EqualTo(2));
            Assert.That(s.Revision, Is.EqualTo(1L));
        }

        // ————————————————————————————————————————————————————————————
        // ScheduleLimits 的真实消费（阶段 A 自曝缺口 ①）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>ScheduleLimits</c> 三个上限与最大排程视野必须真正决定"接受/编辑"：
        /// 单 Lane 队列上限、单批操作上限、依赖闭包上限、最大预排视野。
        /// </summary>
        [Test]
        public void ScheduleLimitsAreEnforcedOnAcceptAndEditDecisions()
        {
            // —— 单 Lane 队列上限 ——
            Task05Scheduler queueLimited = new Task05Scheduler(new ScheduleLimits(
                MaxQueuedPlansPerLane: 2, MaxBatchOperations: 64, MaxDependencyClosurePlans: 256,
                MaxPreScheduleHorizonTicks: 36000L));
            Assert.That(queueLimited.Apply(new ScheduleEditOperation[] { queueLimited.AddOp(1L, 100L) }).Succeeded,
                Is.True);
            Assert.That(queueLimited.Apply(new ScheduleEditOperation[] { queueLimited.AddOp(2L, 200L) }).Succeeded,
                Is.True);

            long revisionAtLimit = queueLimited.Revision;
            ScheduleEditTransactionResult overQueue = queueLimited.Apply(
                new ScheduleEditOperation[] { queueLimited.AddOp(3L, 300L) });
            Assert.That(overQueue.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_LANE_QUEUE_LIMIT_EXCEEDED),
                "第 3 个计划超过单 Lane 上限 2 必须被拒绝");
            Assert.That(queueLimited.Revision, Is.EqualTo(revisionAtLimit), "拒绝不得推进修订号");
            Assert.That(queueLimited.Authority.Registry.ActiveCount, Is.EqualTo(2), "不得留下孤立计划");

            // —— 删除之后可以再次提交（上限是"当前排队数"而不是单调计数）——
            ActionPlanId firstId = queueLimited.Authority.FindLane(Task05Farm.Hero).Plans[0].ActionPlanId;
            Assert.That(queueLimited.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.RemoveOp(firstId) }).Succeeded, Is.True);
            Assert.That(queueLimited.Apply(new ScheduleEditOperation[] { queueLimited.AddOp(4L, 300L) }).Succeeded,
                Is.True, "腾出名额后必须可以再次接受新计划");

            // —— 最大预排视野 ——
            Task05Scheduler horizonLimited = new Task05Scheduler(new ScheduleLimits(
                MaxQueuedPlansPerLane: 16, MaxBatchOperations: 64, MaxDependencyClosurePlans: 256,
                MaxPreScheduleHorizonTicks: 100L));
            const long currentTick = 1000L;
            ScheduleEditTransactionResult atHorizon = horizonLimited.Editor.Apply(
                new ScheduleEditOperation[] { horizonLimited.AddOp(1L, currentTick + 100L) },
                currentTick, 0L, 0L);
            Assert.That(atHorizon.Succeeded, Is.True, "恰好等于视野上限必须通过（<= 为合法边界）：" +
                atHorizon.RejectionCode);

            ScheduleEditTransactionResult beyondHorizon = horizonLimited.Editor.Apply(
                new ScheduleEditOperation[] { horizonLimited.AddOp(2L, currentTick + 101L) },
                currentTick, horizonLimited.Revision, horizonLimited.Revision);
            Assert.That(beyondHorizon.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_HORIZON_EXCEEDED),
                "严格超过视野上限必须被拒绝");

            // —— 依赖闭包上限 ——
            Task05Scheduler closureLimited = new Task05Scheduler(new ScheduleLimits(
                MaxQueuedPlansPerLane: 16, MaxBatchOperations: 64, MaxDependencyClosurePlans: 2,
                MaxPreScheduleHorizonTicks: 36000L));
            ActionPlan c1 = closureLimited.Attack(100L);
            ActionPlan c3 = closureLimited.Attack(140L);
            ActionPlan c2 = closureLimited.Attack(180L);
            closureLimited.Authority.RegisterPlan(c1);
            closureLimited.Authority.RegisterPlan(c2);
            closureLimited.Authority.RegisterPlan(c3);

            ScheduleEditTransactionResult closureOver = closureLimited.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(c1.ActionPlanId, 120L) });
            Assert.That(closureOver.RejectionCode, Is.EqualTo(ScheduleCodes.SCHEDULE_DEPENDENCY_CLOSURE_TOO_LARGE),
                "闭包 {c1,c3} 加 ripple 引入的 c2 = 3 > 上限 2 ⇒ 必须拒绝整批");
            Assert.That(closureLimited.Revision, Is.EqualTo(0L));

            Task05Scheduler closureOk = new Task05Scheduler(new ScheduleLimits(
                MaxQueuedPlansPerLane: 16, MaxBatchOperations: 64, MaxDependencyClosurePlans: 256,
                MaxPreScheduleHorizonTicks: 36000L));
            ActionPlan d1 = closureOk.Attack(100L);
            ActionPlan d3 = closureOk.Attack(140L);
            closureOk.Authority.RegisterPlan(d1);
            closureOk.Authority.RegisterPlan(d3);
            Assert.That(closureOk.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(d1.ActionPlanId, 120L) }).Succeeded,
                Is.True, "同样形状在上限充裕时必须成功（正控制）");
        }

        // ————————————————————————————————————————————————————————————
        // 移动链预测重算（任务 06 的接入端口）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>MoveChainRecomputesProjectedStartPathDurationAndBudget</c>：
        /// 受影响 Move 链的路径、边数、路径权重、绝对 Tick 与预算一起重算，
        /// 且<strong>预算与 EndTick 使用路径权重而不是边数</strong>。
        /// 任务 05 只提供 <see cref="IMovementPathCalculator"/> 端口；本用例注入确定性假实现验证接缝。
        /// </summary>
        [Test]
        public void MoveChainRecomputesProjectedStartPathDurationAndBudget()
        {
            var calculator = new StubPathCalculator();
            Task05Scheduler s = new Task05Scheduler(null, calculator);

            ActionPlan move = s.Create(Task05Farm.MoveId, 100L, pathEdgeCount: 2, pathWeightUnits: 4);
            s.Authority.RegisterPlan(move);
            Assert.That(move.EndTick, Is.EqualTo(130L), "权重 4 × 基准 5 = 20，+ 后摇 10 ⇒ [100,130)");
            Assert.That(move.BudgetCostTicks, Is.EqualTo(30));

            calculator.Next = new MovementPathProjection(EdgeCount: 3, WeightUnits: 7);
            ScheduleEditTransactionResult result = s.Apply(
                new ScheduleEditOperation[] { Task05Scheduler.MoveOp(move.ActionPlanId, 200L) });

            Assert.That(result.Succeeded, Is.True, result.RejectionCode);
            Assert.That(calculator.CallCount, Is.EqualTo(1), "受影响 Move 必须恰好触发一次路径重算");
            Assert.That(move.StartTick, Is.EqualTo(200L));
            Assert.That(move.ResolvedPathEdgeCount, Is.EqualTo(3));
            Assert.That(move.ResolvedPathWeightUnits, Is.EqualTo(7));
            Assert.That(move.MoveDurationTicks, Is.EqualTo(35), "7 权重 × 基准 5 = 35");
            Assert.That(move.EndTick, Is.EqualTo(245L), "200 + 35 + 后摇 10");
            Assert.That(move.BudgetCostTicks, Is.EqualTo(45), "预算 = 路径权重时长 + 后摇（不是 3×5）");
            Assert.That(move.BudgetCostTicks,
                Is.Not.EqualTo((3 * move.ResolvedBaseStepTicks) + move.RecoveryTicks),
                "预算必须用路径权重而不是边数");
            Assert.That(move.ReservedTurnBudgetTicks, Is.EqualTo(45), "Editable 期预留跟随新预算");
        }

        /// <summary>
        /// 任务 06 硬前置条件 2/3（任务 05 交接记录 §27.2 / 缺陷 D1）：
        /// 默认路径计算器必须<strong>真 fail-closed</strong>。
        ///
        /// 原行为：<c>Recompute =&gt; null</c>，而求值器把 <c>null</c> 解释为"该计划无需重算"
        /// ⇒ 实际是<strong>静默恒等</strong>，与本类型自身文档相反，且没有任何测试会失败。
        /// 现行为：被真正要求重算时以
        /// <see cref="ScheduleCodes.SCHEDULE_PATH_CALCULATOR_NOT_IMPLEMENTED"/> 失败。
        /// 本用例是<strong>可失败的负控制</strong>：未注入真实计算器时事务必须抛错并零写入。
        /// </summary>
        [Test]
        public void DefaultPathCalculatorDoesNotResamplePath()
        {
            Task05Scheduler s = new Task05Scheduler();
            Assert.That(s.Evaluator.PathCalculator, Is.SameAs(NotImplementedMovementPathCalculator.Instance));

            ActionPlan move = s.Create(Task05Farm.MoveId, 100L, pathEdgeCount: 2, pathWeightUnits: 4);
            s.Authority.RegisterPlan(move);

            // 负控制：未注入真实计算器 ⇒ 受影响 Move 的 ripple 必须显式失败。
            LogicDefinitionException error = Assert.Throws<LogicDefinitionException>(() =>
                s.Apply(new ScheduleEditOperation[] { Task05Scheduler.MoveOp(move.ActionPlanId, 300L) }));
            Assert.That(error.ErrorCode,
                Is.EqualTo(ScheduleCodes.SCHEDULE_PATH_CALCULATOR_NOT_IMPLEMENTED));

            // 失败必须零局部写入：起点与旧投影都不变，修订号也不推进。
            Assert.That(move.StartTick, Is.EqualTo(100L), "失败事务不得移动计划");
            Assert.That(move.ResolvedPathWeightUnits, Is.EqualTo(4));
            Assert.That(move.EndTick, Is.EqualTo(130L));
            Assert.That(s.Revision, Is.EqualTo(0L), "失败事务不得推进修订号");

            // 端口自身也不得以 null 表示"没实现"（这正是被修复的语义混淆）。
            IMovementPathCalculator calculator = NotImplementedMovementPathCalculator.Instance;
            Assert.Throws<LogicDefinitionException>(() => calculator.Recompute(move, 300L, 200L));
        }

        // ————————————————————————————————————————————————————————————
        // 只读位置依赖闭包查询（阶段 E 的 Dodge 接缝共用实现）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>DodgeOriginInvalidationFindsTransitiveMovesAcrossNonMovementPlans</c>（只读查询部分）：
        /// 闭包按<strong>位置依赖的传递关系</strong>计算，跨过 Attack/Guard 等非移动计划，
        /// 不以"紧邻下一项"代替依赖关系。
        /// </summary>
        [Test]
        public void PositionDependencyClosureIsTransitiveAndCrossesNonMovementPlans()
        {
            Task05Scheduler s = new Task05Scheduler();

            // 位置事实（全部来自冻结夹具时序）：
            //   source（Move 权重 7 × 基准 5 = 35 + 后摇 10）= [100,145)
            //   attackInBetween（Attack 30+30）              = [140,200)  ← 起点落在 source 区间内
            //   dependent（Move 权重 3 × 5 = 15 + 后摇 10）  = [190,215)  ← 起点落在 attack 区间内
            //   independent（Move 权重 2 × 5 = 10 + 后摇 10）= [900,920)  ← 完全独立
            ActionPlan source = s.Create(Task05Farm.MoveId, 100L, pathEdgeCount: 3, pathWeightUnits: 7);
            ActionPlan attackInBetween = s.Attack(140L);
            ActionPlan dependent = s.Create(Task05Farm.MoveId, 190L, pathEdgeCount: 2, pathWeightUnits: 3);
            ActionPlan independent = s.Create(Task05Farm.MoveId, 900L, pathEdgeCount: 1, pathWeightUnits: 2);

            Assert.That(source.EndTick, Is.EqualTo(145L));
            Assert.That(attackInBetween.StartTick, Is.LessThan(source.EndTick),
                "attack 必须落在 source 的旧投影区间内");
            Assert.That(dependent.StartTick, Is.LessThan(attackInBetween.EndTick),
                "dependent 必须落在 attack 区间内");

            s.Authority.RegisterPlan(source);
            s.Authority.RegisterPlan(attackInBetween);
            s.Authority.RegisterPlan(dependent);
            s.Authority.RegisterPlan(independent);

            IReadOnlyList<ScheduleOperationEvaluation> closure =
                s.Evaluator.QueryPositionDependencyClosure(new[] { source }, movementOnly: false);

            string[] ids = TextIdsOf(closure);

            Assert.That(ids, Does.Contain(attackInBetween.ActionPlanId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
                "起点落在来源区间内的 Attack 也在闭包内（跨过非移动计划）");
            Assert.That(ids, Does.Contain(dependent.ActionPlanId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
                "传递：dependent 依赖 attackInBetween 的区间，因此也依赖 source");
            Assert.That(ids, Does.Not.Contain(independent.ActionPlanId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)), "独立计划不得进入闭包");
            Assert.That(ids, Does.Not.Contain(source.ActionPlanId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)), "来源自身不属于它的依赖闭包");

            Assert.That(source.StartTick, Is.EqualTo(100L), "只读查询不得改动任何计划");
            Assert.That(attackInBetween.StartTick, Is.EqualTo(140L));
            Assert.That(dependent.StartTick, Is.EqualTo(190L));
            Assert.That(s.Revision, Is.EqualTo(0L), "只读查询不得推进修订号");
        }

        private sealed class StubPathCalculator : IMovementPathCalculator
        {
            public MovementPathProjection Next;
            public int CallCount;

            public MovementPathProjection Recompute(ActionPlan plan, long newStartTick, long deltaTicks)
            {
                CallCount++;
                return Next;
            }
        }
    }
}
